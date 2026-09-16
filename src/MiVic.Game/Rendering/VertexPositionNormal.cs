using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Vertex format for both procedural meshes and imported glTF models.
/// <para>
/// The colour is a material colour, and its <b>alpha is the faction paint mask</b>:
/// alpha 255 means "this surface is the faction's colour", alpha 0 means "leave
/// this material exactly as authored". The shader blends between the two, so an
/// imported tank keeps black rubber tracks, a gunmetal barrel and a light grey
/// radar dish while its armour still reads as red, amber or blue.
/// </para>
/// <para>
/// Procedural meshes therefore default to alpha 255 — the faction tint alone
/// decides what they look like — and terrain, which paints itself and is never
/// tinted, passes alpha 0.
/// </para>
/// <para>
/// The texture coordinate is the second colour channel, and it is unused by
/// almost everything: a texture on a tank would be a decal nobody has authored,
/// and on terrain it would be a pattern fighting the shader's own treatments. It
/// exists because a <b>face</b> cannot be modelled out of primitives at the
/// detail a face is read at — an iris, a lid, a lip, a fold are paint, not
/// geometry — and the head is the one thing in the game that is looked at from
/// three metres. Meshes that carry no texture leave it at zero and the lit
/// technique never samples it.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexPositionNormal : IVertexType
{
    /// <summary>Byte stride of one vertex.</summary>
    public const int Stride = 36;

    public Vector3 Position;
    public Vector3 Normal;
    public Color Color;
    public Vector2 TextureCoordinate;

    public VertexPositionNormal(Vector3 position, Vector3 normal)
        : this(position, normal, Color.White)
    {
    }

    /// <summary>
    /// A vertex in a material colour. Pass <paramref name="color"/> with alpha 0
    /// to opt out of the faction tint entirely.
    /// </summary>
    public VertexPositionNormal(Vector3 position, Vector3 normal, Color color)
        : this(position, normal, color, Vector2.Zero)
    {
    }

    /// <summary>A vertex that samples a texture as well as carrying a material colour.</summary>
    public VertexPositionNormal(Vector3 position, Vector3 normal, Color color, Vector2 textureCoordinate)
    {
        Position = position;
        Normal = normal;
        Color = color;
        TextureCoordinate = textureCoordinate;
    }

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0),
        new VertexElement(28, VertexElementFormat.Vector2, VertexElementUsage.TextureCoordinate, 0));

    public readonly VertexDeclaration VertexDeclaration => Declaration;
}
