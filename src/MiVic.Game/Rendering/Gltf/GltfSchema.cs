using System.Text.Json.Serialization;

namespace MiVic.Game.Rendering.Gltf;

/// <summary>
/// Minimal glTF 2.0 document model: only the parts MiVic needs to turn a static
/// mesh into a <see cref="MeshData"/>. Skins, animations and textures are
/// ignored — units render in their bind pose, tinted per faction.
/// </summary>
internal sealed class GltfRoot
{
    public GltfAsset? Asset { get; set; }

    public int Scene { get; set; }

    public GltfScene[]? Scenes { get; set; }

    public GltfNode[]? Nodes { get; set; }

    public GltfMesh[]? Meshes { get; set; }

    public GltfMaterial[]? Materials { get; set; }

    public GltfAccessor[]? Accessors { get; set; }

    public GltfBufferView[]? BufferViews { get; set; }

    public GltfBuffer[]? Buffers { get; set; }
}

internal sealed class GltfAsset
{
    public string? Version { get; set; }

    public string? Generator { get; set; }
}

internal sealed class GltfScene
{
    public string? Name { get; set; }

    public int[]? Nodes { get; set; }
}

internal sealed class GltfNode
{
    public string? Name { get; set; }

    public int[]? Children { get; set; }

    public int? Mesh { get; set; }

    /// <summary>Column-major 4x4 matrix, when present it overrides TRS.</summary>
    public float[]? Matrix { get; set; }

    public float[]? Translation { get; set; }

    public float[]? Rotation { get; set; }

    public float[]? Scale { get; set; }
}

internal sealed class GltfMesh
{
    public string? Name { get; set; }

    public GltfPrimitive[]? Primitives { get; set; }
}

internal sealed class GltfPrimitive
{
    public Dictionary<string, int>? Attributes { get; set; }

    public int? Indices { get; set; }

    public int? Material { get; set; }

    /// <summary>4 means TRIANGLES, which is all MiVic renders.</summary>
    public int Mode { get; set; } = 4;
}

internal sealed class GltfMaterial
{
    public string? Name { get; set; }

    public GltfPbrMetallicRoughness? PbrMetallicRoughness { get; set; }
}

internal sealed class GltfPbrMetallicRoughness
{
    public float[]? BaseColorFactor { get; set; }
}

internal sealed class GltfAccessor
{
    public int? BufferView { get; set; }

    public int ByteOffset { get; set; }

    /// <summary>5120 byte, 5121 ubyte, 5122 short, 5123 ushort, 5125 uint, 5126 float.</summary>
    public int ComponentType { get; set; }

    public bool Normalized { get; set; }

    public int Count { get; set; }

    /// <summary>SCALAR, VEC2, VEC3, VEC4, MAT2, MAT3, MAT4.</summary>
    public string? Type { get; set; }

    public GltfSparse? Sparse { get; set; }
}

internal sealed class GltfSparse
{
    public int Count { get; set; }
}

internal sealed class GltfBufferView
{
    public int Buffer { get; set; }

    public int ByteOffset { get; set; }

    public int ByteLength { get; set; }

    public int? ByteStride { get; set; }
}

internal sealed class GltfBuffer
{
    public int ByteLength { get; set; }

    public string? Uri { get; set; }
}
