using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Content;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Draws fog of war as a terrain-conforming overlay textured by the team's
/// visibility mask.
/// <para>
/// The earlier implementation placed one dark box on every unseen navigation
/// cell. That is cheap but it looks exactly like what it is: a grid of boxes.
/// The boxes also stood on cell centres, so on slopes neighbouring boxes
/// interpenetrated and the map read as stairs. This renderer instead duplicates
/// the terrain's own vertices — so the fog hugs every ridge — and lets bilinear
/// filtering and a small CPU blur turn the boolean cell grid into a soft edge.
/// </para>
/// </summary>
public sealed class FogOverlayRenderer : IDisposable
{
    /// <summary>
    /// Height above the terrain the overlay floats, in metres. The overlay shares
    /// the terrain's vertices, so this only has to beat depth precision, not
    /// clear the ground.
    /// </summary>
    private const float HeightOffsetMetres = 0.12f;

    private readonly GraphicsDevice _device;
    private readonly Effect _effect;
    private readonly EffectParameter _viewProjectionParameter;
    private readonly EffectParameter _maskParameter;
    private readonly EffectParameter? _rememberedTintParameter;
    private readonly EffectParameter? _unknownTintParameter;

    private readonly VertexBuffer _vertexBuffer;
    private readonly IndexBuffer _indexBuffer;
    private readonly int _primitiveCount;
    private readonly Texture2D _maskTexture;
    private readonly int _size;
    private readonly byte[] _maskBytes;
    private readonly byte[] _fogChannel;
    private readonly byte[] _memoryChannel;
    private readonly byte[] _blurScratch;
    private readonly byte[] _textureBytes;

    private long _builtTick = -1;

    public FogOverlayRenderer(GraphicsDevice device, ContentManager content, HeightMap map, NavGrid navigation)
    {
        ArgumentNullException.ThrowIfNull(device);
        ArgumentNullException.ThrowIfNull(content);
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(navigation);

        _device = device;
        _effect = content.Load<Effect>("Shaders/FogOverlay");

        _viewProjectionParameter = _effect.Parameters["ViewProjection"]
            ?? throw new InvalidOperationException("Fog shader is missing the ViewProjection parameter.");
        _maskParameter = _effect.Parameters["VisibilityMask"]
            ?? throw new InvalidOperationException("Fog shader is missing the VisibilityMask parameter.");
        _rememberedTintParameter = _effect.Parameters["RememberedTint"];
        _unknownTintParameter = _effect.Parameters["UnknownTint"];

        if (_rememberedTintParameter is null || _unknownTintParameter is null)
        {
            throw new InvalidOperationException("Fog shader is missing the tint parameters.");
        }

        (VertexPositionTexture[] vertices, ushort[] indices) = BuildMesh(map, navigation);

        _vertexBuffer = new VertexBuffer(device, VertexPositionTexture.Declaration, vertices.Length, BufferUsage.WriteOnly);
        _vertexBuffer.SetData(vertices);

        _indexBuffer = new IndexBuffer(device, IndexElementSize.SixteenBits, indices.Length, BufferUsage.WriteOnly);
        _indexBuffer.SetData(indices);
        _primitiveCount = indices.Length / 3;

        _size = navigation.Size;
        int cells = _size * _size;

        _maskTexture = new Texture2D(device, _size, _size, mipmap: false, SurfaceFormat.Color);
        _maskBytes = new byte[cells * 2];
        _fogChannel = new byte[cells];
        _memoryChannel = new byte[cells];
        _blurScratch = new byte[cells];
        _textureBytes = new byte[cells * 4];

        // A remembered cell keeps a faint blue cast; unknown ground is black, so
        // the fog edge reads as nightfall rather than as a coloured rectangle.
        _rememberedTintParameter?.SetValue(new Vector4(0.05f, 0.07f, 0.12f, 1f));
        _unknownTintParameter?.SetValue(new Vector4(0.0f, 0.0f, 0.0f, 1f));

        // Upload an all-fog mask so the first frame cannot show the map unfogged
        // if the overlay is drawn before the simulation's first vision update.
        for (int i = 0; i < cells; i++)
        {
            _textureBytes[(i * 4) + 0] = VisibilityGrid.UnknownFogLevel;
            _textureBytes[(i * 4) + 3] = 255;
        }

        _maskTexture.SetData(_textureBytes);
    }

    /// <summary>
    /// Rebuilds the mask from a team's knowledge. The simulation only refreshes
    /// visibility on its own interval, so callers may call this every frame.
    /// </summary>
    public void Update(SimWorld world, int team, long tick)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (tick == _builtTick)
        {
            return;
        }

        _builtTick = tick;

        world.Visibility.BuildFogMask(team, _maskBytes);

        int cells = _size * _size;

        for (int cell = 0; cell < cells; cell++)
        {
            _fogChannel[cell] = _maskBytes[(cell * 2) + 0];
            _memoryChannel[cell] = _maskBytes[(cell * 2) + 1];
        }

        // A 1-2-1 blur on both channels widens the transition to roughly a cell
        // and a half, which hides the lattice without smearing fog far enough to
        // reveal units the team cannot actually see.
        Blur(_fogChannel);
        Blur(_memoryChannel);

        for (int cell = 0; cell < cells; cell++)
        {
            int target = cell * 4;
            _textureBytes[target + 0] = _fogChannel[cell];
            _textureBytes[target + 1] = _memoryChannel[cell];
            _textureBytes[target + 2] = 0;
            _textureBytes[target + 3] = 255;
        }

        _maskTexture.SetData(_textureBytes);
    }

    /// <summary>Draws the overlay. Expects the caller to have set up the camera.</summary>
    public void Draw(in Matrix view, in Matrix projection)
    {
        if (_primitiveCount == 0)
        {
            return;
        }

        BlendState blend = _device.BlendState;
        DepthStencilState depth = _device.DepthStencilState;
        RasterizerState rasterizer = _device.RasterizerState;

        // Straight alpha, like the ImGui pass: the shader returns an unmultiplied
        // tint, and MonoGame's AlphaBlend would darken it by the alpha twice.
        _device.BlendState = BlendState.NonPremultiplied;
        _device.DepthStencilState = DepthStencilState.DepthRead;
        _device.RasterizerState = RasterizerState.CullCounterClockwise;
        _device.SetVertexBuffer(_vertexBuffer);
        _device.Indices = _indexBuffer;

        _viewProjectionParameter.SetValue(view * projection);
        _maskParameter?.SetValue(_maskTexture);
        _effect.CurrentTechnique.Passes[0].Apply();

        _device.DrawIndexedPrimitives(PrimitiveType.TriangleList, 0, 0, _primitiveCount);

        _device.BlendState = blend;
        _device.DepthStencilState = depth;
        _device.RasterizerState = rasterizer;
        _device.Indices = null;
    }

    /// <summary>
    /// Builds a copy of the terrain surface whose texture coordinates address the
    /// navigation grid. Cell centres land exactly on texel centres, so a mask
    /// texel covers precisely the ground the simulation evaluated.
    /// </summary>
    private static (VertexPositionTexture[] Vertices, ushort[] Indices) BuildMesh(HeightMap map, NavGrid navigation)
    {
        int size = map.Size;
        var vertices = new VertexPositionTexture[size * size];

        float extentMm = navigation.Size * (float)navigation.CellSizeMm;

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int worldX = map.OriginMm + (x * map.CellSizeMm);
                int worldZ = map.OriginMm + (z * map.CellSizeMm);

                Vector3 position = new(
                    worldX / (float)WorldPos.MmPerMetre,
                    (map.HeightAt(x, z) / (float)WorldPos.MmPerMetre) + HeightOffsetMetres,
                    worldZ / (float)WorldPos.MmPerMetre);

                Vector2 texCoord = new(
                    (worldX - navigation.OriginMm) / extentMm,
                    (worldZ - navigation.OriginMm) / extentMm);

                vertices[(z * size) + x] = new VertexPositionTexture(position, texCoord);
            }
        }

        ushort[] indices = new ushort[(size - 1) * (size - 1) * 6];
        int index = 0;

        for (int z = 0; z < size - 1; z++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                ushort v = (ushort)((z * size) + x);

                // Same winding as the terrain: counter-clockwise from above, or
                // the overlay would be culled while the ground it covers is not.
                indices[index++] = v;
                indices[index++] = (ushort)(v + size + 1);
                indices[index++] = (ushort)(v + size);

                indices[index++] = v;
                indices[index++] = (ushort)(v + 1);
                indices[index++] = (ushort)(v + size + 1);
            }
        }

        return (vertices, indices);
    }

    /// <summary>Separable 1-2-1 blur, in place, wrapping the scratch buffer.</summary>
    private void Blur(byte[] channel)
    {
        int size = _size;

        for (int z = 0; z < size; z++)
        {
            int row = z * size;

            for (int x = 0; x < size; x++)
            {
                int left = channel[row + Math.Max(x - 1, 0)];
                int right = channel[row + Math.Min(x + 1, size - 1)];
                _blurScratch[row + x] = (byte)((left + (channel[row + x] * 2) + right + 2) >> 2);
            }
        }

        for (int z = 0; z < size; z++)
        {
            int above = Math.Max(z - 1, 0) * size;
            int below = Math.Min(z + 1, size - 1) * size;
            int row = z * size;

            for (int x = 0; x < size; x++)
            {
                channel[row + x] = (byte)((_blurScratch[above + x] + (_blurScratch[row + x] * 2) + _blurScratch[below + x] + 2) >> 2);
            }
        }
    }

    public void Dispose()
    {
        _vertexBuffer.Dispose();
        _indexBuffer.Dispose();
        _maskTexture.Dispose();
        _effect.Dispose();
    }
}
