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
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct VertexPositionNormal : IVertexType
{
    /// <summary>Byte stride of one vertex.</summary>
    public const int Stride = 28;

    public Vector3 Position;
    public Vector3 Normal;
    public Color Color;

    public VertexPositionNormal(Vector3 position, Vector3 normal)
        : this(position, normal, Color.White)
    {
    }

    /// <summary>
    /// A vertex in a material colour. Pass <paramref name="color"/> with alpha 0
    /// to opt out of the faction tint entirely.
    /// </summary>
    public VertexPositionNormal(Vector3 position, Vector3 normal, Color color)
    {
        Position = position;
        Normal = normal;
        Color = color;
    }

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector3, VertexElementUsage.Position, 0),
        new VertexElement(12, VertexElementFormat.Vector3, VertexElementUsage.Normal, 0),
        new VertexElement(24, VertexElementFormat.Color, VertexElementUsage.Color, 0));

    public readonly VertexDeclaration VertexDeclaration => Declaration;
}
