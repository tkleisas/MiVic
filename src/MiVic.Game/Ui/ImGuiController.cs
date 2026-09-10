using System.Runtime.InteropServices;
using System.Text;
using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace MiVic.Game.Ui;

/// <summary>
/// Draws ImGui using MonoGame's <see cref="BasicEffect"/>.
/// <para>
/// Written in-house rather than pulled from a package because the font setup is
/// the whole point: ImGui's default atlas covers only Basic Latin, so a Greek
/// game renders as a wall of tofu boxes unless the Greek ranges are requested
/// explicitly. Everything here is standard immediate-mode plumbing around that
/// one critical detail.
/// </para>
/// </summary>
public sealed unsafe class ImGuiController : IDisposable
{
    /// <summary>
    /// Glyph ranges loaded into the atlas. Latin is included alongside Greek
    /// because numbers, units and debug text still use it.
    /// </summary>
    private static readonly ushort[] GlyphRanges =
    [
        0x0020, 0x00FF, // Basic Latin + Latin-1 Supplement
        0x0100, 0x017F, // Latin Extended-A
        0x0370, 0x03FF, // Greek and Coptic
        0x2000, 0x206F, // General Punctuation
        0x2190, 0x21FF, // Arrows
        0x2200, 0x22FF, // Mathematical Operators
        0x2500, 0x257F, // Box Drawing
        0x0000,         // terminator
    ];

    private readonly GraphicsDevice _device;
    private readonly GameWindow _window;
    private readonly BasicEffect _effect;
    private readonly RasterizerState _rasterizerState;
    private readonly Dictionary<Keys, ImGuiKey> _keyMap;

    private VertexBuffer? _vertexBuffer;
    private IndexBuffer? _indexBuffer;
    private VertexPositionColorTexture[] _vertices = [];
    private short[] _indices = [];
    private Texture2D? _fontTexture;
    private KeyboardState _previousKeyboard;
    private bool _frameBegun;
    private bool _disposed;
    private int _scrollWheelValue;

    /// <summary>Creates the context and loads a Greek-capable font.</summary>
    /// <param name="device">Graphics device the UI renders with.</param>
    /// <param name="window">Window used for text input events.</param>
    /// <param name="fontPath">Path to a TTF that contains Greek glyphs.</param>
    /// <param name="fontSizePixels">Font size in pixels.</param>
    public ImGuiController(GraphicsDevice device, GameWindow window, string fontPath, float fontSizePixels)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(window);

        _device = device;
        _window = window;

        ImGui.CreateContext();
        ImGuiIOPtr io = ImGui.GetIO();
        io.ConfigFlags |= ImGuiConfigFlags.NavEnableKeyboard;

        // The effect must exist before the font is loaded, because loading the
        // atlas binds the resulting texture to it.
        _effect = new BasicEffect(device)
        {
            TextureEnabled = true,
            VertexColorEnabled = true,
            LightingEnabled = false,
            World = Matrix.Identity,
            View = Matrix.Identity,
        };

        LoadFont(fontPath, fontSizePixels);

        _rasterizerState = new RasterizerState
        {
            CullMode = CullMode.None,
            ScissorTestEnable = true,
        };

        _keyMap = BuildKeyMap();
        _window.TextInput += OnTextInput;
    }

    /// <summary>Number of glyphs in the loaded font atlas.</summary>
    public int GlyphCount { get; private set; }

    /// <summary>Number of fonts in the atlas; two means the headline font loaded.</summary>
    public int FontCount => ImGui.GetIO().Fonts.Fonts.Size;

    /// <summary>True when ImGui wants the mouse, so the game camera must ignore it.</summary>
    public bool WantsMouse => ImGui.GetIO().WantCaptureMouse;

    /// <summary>True when ImGui wants the keyboard.</summary>
    public bool WantsKeyboard => ImGui.GetIO().WantCaptureKeyboard;

    /// <summary>Current mouse position in screen pixels, as ImGui sees it.</summary>
    public System.Numerics.Vector2 MousePosition => ImGui.GetIO().MousePos;

    /// <summary>
    /// A second, much larger copy of the same font, used for headline text such
    /// as the victory banner. ImGui's per-window font scale is deprecated and had
    /// no effect, so the size is baked into a real atlas entry instead.
    /// </summary>
    public ImFontPtr LargeFont
    {
        get
        {
            ImVector<ImFontPtr> fonts = ImGui.GetIO().Fonts.Fonts;
            return fonts.Size > 1 ? fonts[1] : fonts[0];
        }
    }

    /// <summary>
    /// Coverage of <paramref name="sample"/> in the loaded font.
    /// <para>
    /// <see cref="ImFontPtr.FindGlyph"/> falls back to a substitute glyph and
    /// returns non-null for every codepoint in the font's index range, so it
    /// cannot be used to prove a character is actually rendered. A rasterised
    /// glyph always has a non-zero advance, which is the check used here.
    /// </para>
    /// </summary>
    /// <param name="sample">Text to measure, normally Greek.</param>
    public FontCoverage MeasureCoverage(string sample)
    {
        ArgumentNullException.ThrowIfNull(sample);

        ImGuiIOPtr io = ImGui.GetIO();

        if (io.Fonts.Fonts.Size == 0)
        {
            return new FontCoverage(0, sample.Length, sample, io.Fonts.TexWidth, io.Fonts.TexHeight, 0f, 0f);
        }

        ImFontPtr font = io.Fonts.Fonts[0];
        int covered = 0;
        StringBuilder missing = new();

        foreach (char character in sample)
        {
            ImFontGlyphPtr glyph = font.FindGlyphNoFallback(character);

            if (glyph.NativePtr is not null && glyph.AdvanceX > 0f)
            {
                covered++;
            }
            else
            {
                missing.Append(character);
            }
        }

        return new FontCoverage(
            covered,
            sample.Length,
            missing.ToString(),
            io.Fonts.TexWidth,
            io.Fonts.TexHeight,
            font.FontSize,
            font.Ascent);
    }

    /// <summary>True when every character of <paramref name="text"/> has a rasterised glyph.</summary>
    public bool HasGlyphs(string text)
    {
        FontCoverage coverage = MeasureCoverage(text);
        return coverage.Covered == coverage.Total && coverage.Total > 0;
    }

    /// <summary>Ends the previous frame and starts a new one.</summary>
    public void Update(GameTime gameTime)
    {
        if (_frameBegun)
        {
            // A frame was started but never rendered; close it so ImGui stays in sync.
            ImGui.Render();
        }

        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new NVec2(_device.Viewport.Width, _device.Viewport.Height);
        io.DisplayFramebufferScale = NVec2.One;

        double seconds = gameTime.ElapsedGameTime.TotalSeconds;
        io.DeltaTime = seconds > 0d ? (float)seconds : 1f / 60f;

        UpdateInput(io);

        ImGui.NewFrame();
        _frameBegun = true;
    }

    /// <summary>Renders the frame that was built since <see cref="Update"/>.</summary>
    public void Render()
    {
        if (!_frameBegun)
        {
            return;
        }

        _frameBegun = false;
        ImGui.Render();
        RenderDrawData(ImGui.GetDrawData());
    }

    /// <summary>
    /// Starts another frame in the same update, without re-reading the input devices.
    /// <para>
    /// ImGui gives a window it has never seen before no geometry on its first frame: it does
    /// not know how big the window is until its contents have been laid out once, so the first
    /// frame of a panel is a frame in which the panel draws nothing at all. Played, that is one
    /// frame of sixteen milliseconds and invisible. A probe, though, draws the HUD only when a
    /// script asks for it — so the first photographed frame with the HUD in it was a
    /// photograph of an empty one, and the panels turned up in the next shot as if they had
    /// needed warming up.
    /// </para>
    /// </summary>
    public void BeginAnotherFrame()
    {
        if (_frameBegun)
        {
            ImGui.Render();
        }

        ImGui.NewFrame();
        _frameBegun = true;
    }

    private void LoadFont(string fontPath, float fontSizePixels)
    {
        if (!File.Exists(fontPath))
        {
            throw new FileNotFoundException(
                $"UI font not found at '{fontPath}'. A Greek-capable TTF must sit next to the executable.",
                fontPath);
        }

        ImGuiIOPtr io = ImGui.GetIO();

        fixed (ushort* ranges = GlyphRanges)
        {
            io.Fonts.AddFontFromFileTTF(fontPath, fontSizePixels, default, (nint)ranges);
            io.Fonts.AddFontFromFileTTF(fontPath, fontSizePixels * 2.6f, default, (nint)ranges);
        }

        if (!io.Fonts.Build())
        {
            throw new InvalidOperationException("Failed to build the ImGui font atlas.");
        }

        int width = io.Fonts.TexWidth;
        int height = io.Fonts.TexHeight;

        if (width <= 0 || height <= 0)
        {
            throw new InvalidOperationException(
                $"The ImGui font atlas is empty ({width}x{height}). Check that '{fontPath}' is a valid TrueType font.");
        }

        byte[] textureData = ReadAtlasPixels(io, width, height);

        _fontTexture = new Texture2D(_device, width, height, mipmap: false, SurfaceFormat.Color);
        _fontTexture.SetData(textureData);
        _effect.Texture = _fontTexture;

        // The handle is opaque to ImGui; the renderer binds the texture directly.
        io.Fonts.TexID = 1;

        // Count the glyphs we actually care about, which doubles as a coverage
        // check: a missing Greek range shows up here as a suspiciously low count.
        ImFontPtr font = io.Fonts.Fonts[0];
        int glyphs = 0;
        for (int codepoint = 0x20; codepoint <= 0x03FF; codepoint++)
        {
            if (font.FindGlyph((ushort)codepoint).NativePtr is not null)
            {
                glyphs++;
            }
        }

        GlyphCount = glyphs;
    }

    /// <summary>
    /// Reads the built atlas as 32-bit RGBA.
    /// <para>
    /// ImGui builds the atlas as 8-bit alpha unless the font carries colour
    /// glyphs, and this binding exposes no <c>GetTexDataAsRGBA32</c> helper, so
    /// the expansion to RGBA is done here: white pixels carrying the glyph
    /// coverage in the alpha channel.
    /// </para>
    /// </summary>
    private static byte[] ReadAtlasPixels(ImGuiIOPtr io, int width, int height)
    {
        int pixelCount = width * height;
        byte[] textureData = new byte[pixelCount * 4];

        nint rgba = io.Fonts.TexPixelsRGBA32;
        if (rgba != 0)
        {
            Marshal.Copy(rgba, textureData, 0, textureData.Length);
            return textureData;
        }

        nint alpha8 = io.Fonts.TexPixelsAlpha8;
        if (alpha8 == 0)
        {
            throw new InvalidOperationException("The ImGui font atlas has neither RGBA nor alpha pixel data.");
        }

        byte[] alpha = new byte[pixelCount];
        Marshal.Copy(alpha8, alpha, 0, pixelCount);

        for (int i = 0; i < pixelCount; i++)
        {
            int destination = i * 4;
            textureData[destination] = 255;
            textureData[destination + 1] = 255;
            textureData[destination + 2] = 255;
            textureData[destination + 3] = alpha[i];
        }

        return textureData;
    }

    private void UpdateInput(ImGuiIOPtr io)    {
        MouseState mouse = Mouse.GetState();
        io.AddMousePosEvent(mouse.X, mouse.Y);
        io.AddMouseButtonEvent(0, mouse.LeftButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(1, mouse.RightButton == ButtonState.Pressed);
        io.AddMouseButtonEvent(2, mouse.MiddleButton == ButtonState.Pressed);

        int scroll = mouse.ScrollWheelValue - _scrollWheelValue;
        if (scroll != 0)
        {
            io.AddMouseWheelEvent(0f, scroll / 120f);
            _scrollWheelValue = mouse.ScrollWheelValue;
        }

        KeyboardState keyboard = Keyboard.GetState();

        // Only report transitions; ImGui derives held state from them.
        foreach ((Keys key, ImGuiKey imGuiKey) in _keyMap)
        {
            bool down = keyboard.IsKeyDown(key);
            if (down != _previousKeyboard.IsKeyDown(key))
            {
                io.AddKeyEvent(imGuiKey, down);
            }
        }

        _previousKeyboard = keyboard;
    }

    private void OnTextInput(object? sender, TextInputEventArgs e) => ImGui.GetIO().AddInputCharacter(e.Character);

    private void RenderDrawData(ImDrawDataPtr drawData)
    {
        int commandListCount = drawData.CmdListsCount;
        if (commandListCount == 0)
        {
            return;
        }

        int totalVertices = 0;
        int totalIndices = 0;

        for (int i = 0; i < commandListCount; i++)
        {
            totalVertices += drawData.CmdLists[i].VtxBuffer.Size;
            totalIndices += drawData.CmdLists[i].IdxBuffer.Size;
        }

        EnsureBuffers(totalVertices, totalIndices);

        int vertexOffset = 0;
        int indexOffset = 0;

        int[] listVertexBase = new int[commandListCount];
        int[] listIndexBase = new int[commandListCount];

        for (int i = 0; i < commandListCount; i++)
        {
            ImDrawList* list = drawData.CmdLists[i].NativePtr;
            int vertexCount = list->VtxBuffer.Size;
            int indexCount = list->IdxBuffer.Size;

            listVertexBase[i] = vertexOffset;
            listIndexBase[i] = indexOffset;

            ImDrawVert* sourceVertices = (ImDrawVert*)list->VtxBuffer.Data;
            for (int v = 0; v < vertexCount; v++)
            {
                ImDrawVert source = sourceVertices[v];
                _vertices[vertexOffset + v] = new VertexPositionColorTexture(
                    new Vector3(source.pos.X, source.pos.Y, 0f),
                    ConvertColor(source.col),
                    new Vector2(source.uv.X, source.uv.Y));
            }

            // ImGui indices are relative to their own draw list, so they are kept
            // as-is and offset later via baseVertex.
            ushort* sourceIndices = (ushort*)list->IdxBuffer.Data;
            for (int n = 0; n < indexCount; n++)
            {
                _indices[indexOffset + n] = (short)sourceIndices[n];
            }

            vertexOffset += vertexCount;
            indexOffset += indexCount;
        }

        _vertexBuffer!.SetData(_vertices, 0, totalVertices);
        _indexBuffer!.SetData(_indices, 0, totalIndices);

        GraphicsDevice device = _device;
        BlendState previousBlend = device.BlendState;
        DepthStencilState previousDepth = device.DepthStencilState;
        RasterizerState previousRasterizer = device.RasterizerState;
        SamplerState previousSampler = device.SamplerStates[0];

        // ImGui's font atlas stores straight alpha (RGB = white, A = coverage),
        // so the blend must be (SrcAlpha, InvSrcAlpha). MonoGame's AlphaBlend is
        // premultiplied (One, InvSrcAlpha) and would draw every glyph as a solid
        // white rectangle — the classic "tofu" failure.
        device.BlendState = BlendState.NonPremultiplied;
        device.DepthStencilState = DepthStencilState.None;
        device.RasterizerState = _rasterizerState;
        device.SamplerStates[0] = SamplerState.LinearClamp;

        _effect.Projection = Matrix.CreateOrthographicOffCenter(
            0f,
            device.Viewport.Width,
            device.Viewport.Height,
            0f,
            -1f,
            1f);

        device.Textures[0] = _fontTexture;
        device.SetVertexBuffer(_vertexBuffer);
        device.Indices = _indexBuffer;

        for (int i = 0; i < commandListCount; i++)
        {
            ImDrawList* list = drawData.CmdLists[i].NativePtr;
            int commandCount = list->CmdBuffer.Size;

            for (int c = 0; c < commandCount; c++)
            {
                ImDrawCmd* command = (ImDrawCmd*)list->CmdBuffer.Data + c;

                NVec4 clip = command->ClipRect;
                Rectangle scissor = Rectangle.Intersect(
                    new Rectangle(
                        (int)clip.X,
                        (int)clip.Y,
                        (int)MathF.Max(clip.Z - clip.X, 0f),
                        (int)MathF.Max(clip.W - clip.Y, 0f)),
                    device.Viewport.Bounds);

                if (scissor.Width <= 0 || scissor.Height <= 0)
                {
                    continue;
                }

                device.ScissorRectangle = scissor;

                foreach (EffectPass pass in _effect.CurrentTechnique.Passes)
                {
                    pass.Apply();
                    device.DrawIndexedPrimitives(
                        PrimitiveType.TriangleList,
                        baseVertex: listVertexBase[i],
                        startIndex: listIndexBase[i] + (int)command->IdxOffset,
                        primitiveCount: (int)(command->ElemCount / 3));
                }
            }
        }

        device.SetVertexBuffer(null);
        device.Indices = null;
        device.Textures[0] = null;

        device.BlendState = previousBlend;
        device.DepthStencilState = previousDepth;
        device.RasterizerState = previousRasterizer;
        device.SamplerStates[0] = previousSampler;
    }

    private void EnsureBuffers(int vertexCount, int indexCount)
    {
        if (_vertexBuffer is not null && _vertices.Length >= vertexCount && _indices.Length >= indexCount)
        {
            return;
        }

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();

        int vertices = Math.Max(vertexCount, 4096);
        int indices = Math.Max(indexCount, 8192);

        _vertices = new VertexPositionColorTexture[vertices];
        _indices = new short[indices];

        _vertexBuffer = new DynamicVertexBuffer(
            _device,
            VertexPositionColorTexture.VertexDeclaration,
            vertices,
            BufferUsage.WriteOnly);
        _indexBuffer = new DynamicIndexBuffer(
            _device,
            IndexElementSize.SixteenBits,
            indices,
            BufferUsage.WriteOnly);
    }

    /// <summary>Converts ImGui's packed ABGR into a MonoGame colour.</summary>
    private static Color ConvertColor(uint packed)
        => new(
            (byte)(packed & 0xFF),
            (byte)((packed >> 8) & 0xFF),
            (byte)((packed >> 16) & 0xFF),
            (byte)((packed >> 24) & 0xFF));

    private static Dictionary<Keys, ImGuiKey> BuildKeyMap()
    {
        var map = new Dictionary<Keys, ImGuiKey>
        {
            [Keys.Tab] = ImGuiKey.Tab,
            [Keys.Left] = ImGuiKey.LeftArrow,
            [Keys.Right] = ImGuiKey.RightArrow,
            [Keys.Up] = ImGuiKey.UpArrow,
            [Keys.Down] = ImGuiKey.DownArrow,
            [Keys.PageUp] = ImGuiKey.PageUp,
            [Keys.PageDown] = ImGuiKey.PageDown,
            [Keys.Home] = ImGuiKey.Home,
            [Keys.End] = ImGuiKey.End,
            [Keys.Insert] = ImGuiKey.Insert,
            [Keys.Delete] = ImGuiKey.Delete,
            [Keys.Back] = ImGuiKey.Backspace,
            [Keys.Space] = ImGuiKey.Space,
            [Keys.Enter] = ImGuiKey.Enter,
            [Keys.Escape] = ImGuiKey.Escape,
            [Keys.OemComma] = ImGuiKey.Comma,
            [Keys.OemPeriod] = ImGuiKey.Period,
            [Keys.OemMinus] = ImGuiKey.Minus,
            [Keys.OemPlus] = ImGuiKey.Equal,
            [Keys.OemQuestion] = ImGuiKey.Slash,
            [Keys.OemSemicolon] = ImGuiKey.Semicolon,
            [Keys.OemQuotes] = ImGuiKey.Apostrophe,
            [Keys.OemOpenBrackets] = ImGuiKey.LeftBracket,
            [Keys.OemCloseBrackets] = ImGuiKey.RightBracket,
            [Keys.OemPipe] = ImGuiKey.Backslash,
            [Keys.OemTilde] = ImGuiKey.GraveAccent,
            [Keys.LeftShift] = ImGuiKey.ModShift,
            [Keys.RightShift] = ImGuiKey.ModShift,
            [Keys.LeftControl] = ImGuiKey.ModCtrl,
            [Keys.RightControl] = ImGuiKey.ModCtrl,
            [Keys.LeftAlt] = ImGuiKey.ModAlt,
            [Keys.RightAlt] = ImGuiKey.ModAlt,
        };

        for (Keys key = Keys.A; key <= Keys.Z; key++)
        {
            map[key] = (ImGuiKey)((int)ImGuiKey.A + (key - Keys.A));
        }

        for (Keys key = Keys.D0; key <= Keys.D9; key++)
        {
            map[key] = (ImGuiKey)((int)ImGuiKey._0 + (key - Keys.D0));
        }

        for (Keys key = Keys.F1; key <= Keys.F12; key++)
        {
            map[key] = (ImGuiKey)((int)ImGuiKey.F1 + (key - Keys.F1));
        }

        return map;
    }

    public void Dispose()
    {
        if (_disposed)
        {
            return;
        }

        _disposed = true;
        _window.TextInput -= OnTextInput;

        _vertexBuffer?.Dispose();
        _indexBuffer?.Dispose();
        _fontTexture?.Dispose();
        _effect.Dispose();
        _rasterizerState.Dispose();

        ImGui.DestroyContext();
    }
}
