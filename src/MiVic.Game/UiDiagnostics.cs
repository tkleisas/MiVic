using ImGuiNET;
using NVec2 = System.Numerics.Vector2;

namespace MiVic.Game;

/// <summary>
/// Headless ImGui diagnostics. Builds a frame without any graphics device and
/// dumps the draw data, which is how the renderer's index conventions were
/// pinned down.
/// <para>
/// The question this answers: ImGui stores vertices per draw list but issues
/// several commands per list, each with its own <c>VtxOffset</c>. Whether the
/// indices in <c>IdxBuffer</c> are relative to the list or to the command
/// decides whether a renderer must add <c>VtxOffset</c> or not, and getting it
/// wrong produces garbled or box-shaped glyphs on screen.
/// </para>
/// </summary>
public static class UiDiagnostics
{
    /// <summary>Runs the diagnostic and prints the draw data layout.</summary>
    public static unsafe int Run(string fontPath, float fontSize)
    {
        ImGui.CreateContext();
        ImGuiIOPtr io = ImGui.GetIO();
        io.DisplaySize = new NVec2(1280f, 720f);
        io.DeltaTime = 1f / 60f;

        if (File.Exists(fontPath))
        {
            ushort[] ranges = [0x0020, 0x00FF, 0x0370, 0x03FF, 0x0000];
            fixed (ushort* rangePointer = ranges)
            {
                io.Fonts.AddFontFromFileTTF(fontPath, fontSize, default, (nint)rangePointer);
            }
        }
        else
        {
            Console.Error.WriteLine($"Font not found at '{fontPath}'; using the built-in font.");
            io.Fonts.AddFontDefault();
        }

        io.Fonts.Build();
        io.Fonts.TexID = 1;
        Console.WriteLine($"atlas {io.Fonts.TexWidth}x{io.Fonts.TexHeight}, fonts={io.Fonts.Fonts.Size}, texReady={io.Fonts.TexReady}");

        // Two windows and a separator guarantee more than one draw command.
        // Several frames are rendered because ImGui needs a frame to size windows
        // before it emits geometry.
        for (int frame = 0; frame < 3; frame++)
        {
            ImGui.NewFrame();

            ImGui.SetNextWindowPos(new NVec2(10f, 10f), ImGuiCond.Always);
            ImGui.Begin("Πρώτο");
            ImGui.TextUnformatted("Σοβιετικοί Κινέζοι Δυτικοί");
            ImGui.TextUnformatted("Τικ: 1234  FPS: 60");
            ImGui.End();

            ImGui.SetNextWindowPos(new NVec2(10f, 200f), ImGuiCond.Always);
            ImGui.Begin("Δεύτερο");
            ImGui.TextUnformatted("Δεύτερο παράθυρο");
            ImGui.Separator();
            ImGui.TextUnformatted("Γραμμή μετά το διαχωριστικό");
            ImGui.End();

            ImGui.Render();
        }

        ImDrawDataPtr drawData = ImGui.GetDrawData();

        Console.WriteLine($"valid={drawData.Valid} displaySize={io.DisplaySize}");

        Console.WriteLine($"CmdListsCount={drawData.CmdListsCount} TotalVtxCount={drawData.TotalVtxCount} TotalIdxCount={drawData.TotalIdxCount}");

        int accumulatedVertices = 0;

        for (int listIndex = 0; listIndex < drawData.CmdListsCount; listIndex++)
        {
            ImDrawList* list = drawData.CmdLists[listIndex].NativePtr;
            int vertexCount = list->VtxBuffer.Size;
            int indexCount = list->IdxBuffer.Size;

            int minIndex = int.MaxValue;
            int maxIndex = int.MinValue;
            ushort* indices = (ushort*)list->IdxBuffer.Data;

            for (int i = 0; i < indexCount; i++)
            {
                int value = indices[i];
                minIndex = Math.Min(minIndex, value);
                maxIndex = Math.Max(maxIndex, value);
            }

            Console.WriteLine($"list[{listIndex}] vtx={vertexCount} idx={indexCount} idxRange=[{minIndex}..{maxIndex}] listVtxBase={accumulatedVertices}");

            int commandCount = list->CmdBuffer.Size;

            for (int c = 0; c < commandCount; c++)
            {
                ImDrawCmd* command = (ImDrawCmd*)list->CmdBuffer.Data + c;

                Console.WriteLine(
                    $"   cmd[{c}] VtxOffset={command->VtxOffset} IdxOffset={command->IdxOffset} ElemCount={command->ElemCount} " +
                    $"clip=({command->ClipRect.X:0},{command->ClipRect.Y:0})-({command->ClipRect.Z:0},{command->ClipRect.W:0})");
            }

            accumulatedVertices += vertexCount;
        }

        Console.WriteLine();
        Console.WriteLine("Interpretation: if a command's VtxOffset is 0 and its IdxOffset equals the running index total,");
        Console.WriteLine("indices are list-relative and the renderer must add VtxOffset only when it is non-zero.");

        DumpGlyphs(io, "Σοβιετικοί Κινέζοι Δυτικοί 1234");
        DumpAtlas(io);

        ImGui.DestroyContext();
        return 0;
    }

    /// <summary>
    /// Reports the actual pixel content of the font atlas. A blank atlas is what
    /// makes every glyph render as a solid white rectangle — the quads are placed
    /// correctly, but they sample an empty texture.
    /// </summary>
    private static unsafe void DumpAtlas(ImGuiIOPtr io)
    {
        int width = io.Fonts.TexWidth;
        int height = io.Fonts.TexHeight;

        Console.WriteLine();
        Console.WriteLine($"atlas {width}x{height}  alpha8=0x{(long)io.Fonts.TexPixelsAlpha8:X}  rgba32=0x{(long)io.Fonts.TexPixelsRGBA32:X}  useColors={io.Fonts.TexPixelsUseColors}");

        nint alphaPointer = io.Fonts.TexPixelsAlpha8;

        if (alphaPointer == 0)
        {
            Console.WriteLine("  no alpha8 data");
            return;
        }

        byte* alpha = (byte*)alphaPointer;
        int count = width * height;
        int[] histogram = new int[16];
        int zero = 0;
        int full = 0;

        for (int i = 0; i < count; i++)
        {
            byte value = alpha[i];

            if (value == 0)
            {
                zero++;
            }
            else if (value == 255)
            {
                full++;
            }

            histogram[value >> 4]++;
        }

        Console.WriteLine($"  pixels={count} zero={zero} full={full} other={count - zero - full}");
        Console.Write("  histogram (16 buckets of 16 levels): ");

        for (int i = 0; i < histogram.Length; i++)
        {
            Console.Write($"{histogram[i]} ");
        }

        Console.WriteLine();
    }

    /// <summary>Prints the atlas entry for every character of a sample string.</summary>
    private static unsafe void DumpGlyphs(ImGuiIOPtr io, string sample)
    {
        if (io.Fonts.Fonts.Size == 0)
        {
            Console.WriteLine("no font loaded");
            return;
        }

        ImFontPtr font = io.Fonts.Fonts[0];
        Console.WriteLine();
        Console.WriteLine($"glyphs of \"{sample}\" (fontSize={font.FontSize:0.0}):");
        Console.WriteLine("  char  code   adv     x0     x1     u0     u1  visible");

        foreach (char character in sample)
        {
            ImFontGlyphPtr glyph = font.FindGlyphNoFallback(character);

            if (glyph.NativePtr is null)
            {
                Console.WriteLine($"  '{character}' {character,5}   <no glyph entry>");
                continue;
            }

            Console.WriteLine(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"  '{character}' {character,5} {glyph.AdvanceX,6:0.0} {glyph.X0,6:0.0} {glyph.X1,6:0.0} {glyph.U0,6:0.000} {glyph.U1,6:0.000}   {glyph.Visible}"));
        }
    }
}
