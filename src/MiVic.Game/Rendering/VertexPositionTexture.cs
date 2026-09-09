using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Vertex format for meshes that sample a texture, currently only the fog of war
/// overlay. Position is in metres and the texture coordinate addresses the
/// visibility mask, so one vertex carries both the terrain height and the
/// player's knowledge of that spot.
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexPositionTexture : IVertexType
{
    /// <summary>Byte stride of one vertex.</summary>
    public const int Stride = 20;

    public Vector3 Position;
    public Vector2 TexCoord;

    public VertexPositionTexture(Vector3 position, Vector2 texCoord)
    {
        Position = position;
        TexCoord = texCoord;
    }

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

    public readonly VertexDeclaration VertexDeclaration => Declaration;
}
