using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Rendering;

/// <summary>
/// A CPU-side mesh ready to be uploaded to the GPU. Uses 16-bit indices, which
/// caps a single mesh at 65,535 vertices — ample for the placeholder primitives
/// and cheap on bandwidth.
/// </summary>
public sealed class MeshData
{
    /// <summary>Empty mesh, useful as a null object.</summary>
    public static readonly MeshData Empty = new([], []);

    public MeshData(VertexPositionNormal[] vertices, ushort[] indices)
    {
        ArgumentNullException.ThrowIfNull(vertices);
        ArgumentNullException.ThrowIfNull(indices);

        if (indices.Length % 3 != 0)
        {
            throw new ArgumentException("Index count must be a multiple of three.", nameof(indices));
        }

        Vertices = vertices;
        Indices = indices;
    }

    /// <summary>Unique vertices.</summary>
    public VertexPositionNormal[] Vertices { get; }

    /// <summary>Triangle indices, three per primitive.</summary>
    public ushort[] Indices { get; }

    /// <summary>Number of triangles.</summary>
    public int PrimitiveCount => Indices.Length / 3;

    /// <summary>Uploads this mesh to a vertex buffer and an index buffer.</summary>
    public (VertexBuffer Vertices, IndexBuffer Indices) Upload(GraphicsDevice device)
    {
        VertexBuffer vertexBuffer = new(device, VertexPositionNormal.Declaration, Vertices.Length, BufferUsage.WriteOnly);
        vertexBuffer.SetData(Vertices);

        IndexBuffer indexBuffer = new(device, IndexElementSize.SixteenBits, Indices.Length, BufferUsage.WriteOnly);
        indexBuffer.SetData(Indices);

        return (vertexBuffer, indexBuffer);
    }
}
