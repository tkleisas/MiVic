using System.Runtime.InteropServices;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// Per-instance data: a world matrix and a tint.
/// <para>
/// The layout is the contract with <c>InstancedMesh.fx</c>. The four matrix rows
/// occupy TEXCOORD1..4 and the colour TEXCOORD5, which is how MonoGame maps an
/// instance vertex stream onto shader inputs.
/// </para>
/// </summary>
[StructLayout(LayoutKind.Sequential, Pack = 1)]
public struct InstanceData
{
    /// <summary>Byte stride of one instance record.</summary>
    public const int Stride = 80;

    public Matrix Transform;
    public Vector4 Color;

    public InstanceData(Matrix transform, Vector4 color)
    {
        Transform = transform;
        Color = color;
    }

    public static readonly VertexDeclaration Declaration = new(
        new VertexElement(0, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 1),
        new VertexElement(16, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 2),
        new VertexElement(32, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 3),
        new VertexElement(48, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 4),
        new VertexElement(64, VertexElementFormat.Vector4, VertexElementUsage.TextureCoordinate, 5));
}
