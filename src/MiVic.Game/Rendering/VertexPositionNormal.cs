using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Vertex format for both procedural meshes and imported glTF models.
/// <para>
/// The colour is the model's own material shade, not a faction colour: the
/// shader multiplies it by the per-instance tint, so a single red faction tint
/// still shows the dark tracks, light panels and gun metal of an imported tank.
/// Procedural meshes simply use white.
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
