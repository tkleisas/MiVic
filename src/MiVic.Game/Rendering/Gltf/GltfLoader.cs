using System.Text;
using System.Text.Json;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering.Gltf;

/// <summary>How a model is fitted into the game world on import.</summary>
/// <param name="TargetSizeMetres">
/// Length of the model's longest axis after import. A tank becomes its length, a
/// soldier its height, a building its widest side — one rule that reads
/// correctly for every asset.
/// </param>
/// <param name="YawOffsetDegrees">
/// Rotation applied so the model faces +X, matching the simulation's heading
/// convention of 0 brads.
/// </param>
/// <param name="FlipNormals">Set when a model's winding order makes it light inside-out.</param>
/// <param name="MeshNameContains">
/// When set, only mesh nodes whose name contains this text are imported. Used to
/// strip modular extras — for example the weapon meshes that ship inside a
/// character model and otherwise inflate its bounds.
/// </param>
/// <param name="AlignLongestHorizontalAxis">
/// Rotates the model so its longest horizontal axis points along +X. Most glTF
/// models are authored facing -Z, so this is what puts them nose-first without
/// hand-tuning every asset.
/// </param>
public readonly record struct ModelImportOptions(
    float TargetSizeMetres,
    float YawOffsetDegrees = 0f,
    bool FlipNormals = false,
    string? MeshNameContains = null,
    bool AlignLongestHorizontalAxis = true);

/// <summary>
/// One named part of a model, with the transform that places it.
/// </summary>
/// <param name="Name">The glTF node name, which is the part contract: <c>turret</c>, <c>wheel_l01</c>.</param>
/// <param name="Mesh">Geometry in the space the part's transform expects.</param>
/// <param name="LocalTransform">
/// Where the part sits relative to <em>its parent</em>, not to the model. Keeping
/// it parent-relative is what lets a turret turn and carry its barrel with it: an
/// accumulated transform would freeze the barrel in place the moment the turret
/// moved.
/// </param>
/// <param name="ParentIndex">Index of the enclosing part, or -1 for a root part.</param>
public readonly record struct ModelPart(string Name, MeshData Mesh, Matrix LocalTransform, int ParentIndex);

/// <summary>
/// A model as its parts, plus the single transform that normalises the whole thing
/// for the world: alignment, scale, centring and grounding.
/// </summary>
/// <param name="Parts">The named parts, in scene order.</param>
/// <param name="ModelTransform">Applied after each part's own transform.</param>
/// <param name="SizeMetres">Extent of the whole model after scaling, in metres.</param>
public readonly record struct ModelData(
    IReadOnlyList<ModelPart> Parts,
    Matrix ModelTransform,
    Vector3 SizeMetres);

/// <summary>
/// Loads static geometry out of a glTF 2.0 binary (<c>.glb</c>) or JSON
/// (<c>.gltf</c> + <c>.bin</c>) file into the engine's own mesh format.
/// <para>
/// Written in-house rather than routed through the content pipeline because the
/// content pipeline's model processor bakes its own effects, which cannot be
/// used with the instanced shader that makes hundreds of units affordable. This
/// loader produces plain vertex and index arrays that feed
/// <see cref="InstancedRenderer"/> directly.
/// </para>
/// <para>
/// Supported: node hierarchy with TRS or matrix transforms, TRIANGLES
/// primitives, POSITION / NORMAL / COLOR_0, 8/16/32-bit indices and material
/// base colours. Unsupported: textures, skins, morph targets, sparse accessors
/// and non-triangle modes — all of which are rejected loudly rather than
/// silently rendering garbage.
/// </para>
/// </summary>
public static class GltfLoader
{
    private const uint GlbMagic = 0x46546C67; // 'glTF'
    private const uint ChunkJson = 0x4E4F534A; // 'JSON'
    private const uint ChunkBinary = 0x004E4942; // 'BIN\0'

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNameCaseInsensitive = true,
    };

    /// <summary>Loads a model and returns a mesh in game units (metres), centred and grounded.</summary>
    public static MeshData Load(string path, ModelImportOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        (GltfRoot root, byte[]? binaryChunk) = Parse(path);
        byte[][] buffers = ResolveBuffers(path, root, binaryChunk);

        var positions = new List<Vector3>(4096);
        var normals = new List<Vector3>(4096);
        var colors = new List<Vector3>(4096);
        var masks = new List<float>(4096);
        var indices = new List<ushort>(8192);

        int[]? sceneNodes = root.Scenes is { Length: > 0 }
            ? root.Scenes[Math.Clamp(root.Scene, 0, root.Scenes.Length - 1)].Nodes
            : null;

        // The per-model yaw is applied inside BuildMesh, after the automatic
        // forward alignment. Doing it here would let the aligner rotate the model
        // again and silently cancel the correction.
        Matrix rootTransform = Matrix.Identity;

        if (sceneNodes is null)
        {
            // No scene graph: treat every node as a root.
            for (int i = 0; i < (root.Nodes?.Length ?? 0); i++)
            {
                AppendNode(root, buffers, i, rootTransform, options, positions, normals, colors, masks, indices);
            }
        }
        else
        {
            foreach (int node in sceneNodes)
            {
                AppendNode(root, buffers, node, rootTransform, options, positions, normals, colors, masks, indices);
            }
        }

        if (positions.Count == 0)
        {
            throw new InvalidDataException($"'{path}' contains no renderable triangle geometry.");
        }

        return BuildMesh(positions, normals, colors, masks, indices, options);
    }

    /// <summary>
    /// Loads a model keeping its parts separate.
    /// <para>
    /// The parts are what make animation possible: a model exported with a named
    /// <c>turret</c> or <c>wheel_l01</c> node keeps that node's own mesh and
    /// transform, so the renderer can move one part without rebuilding the model.
    /// <see cref="Load"/> merges them instead, which is all a static mesh needs.
    /// </para>
    /// <para>
    /// Part geometry stays in model space and the normalisation — alignment, scale,
    /// centring, grounding — is returned as a single <see cref="ModelData.ModelTransform"/>
    /// to be applied outside the parts. That is deliberate: baking it into the
    /// vertices would move a turret's pivot away from the turret, and a part cannot
    /// rotate about a pivot it no longer has.
    /// </para>
    /// </summary>
    public static ModelData LoadModel(string path, ModelImportOptions options)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(path);

        (GltfRoot root, byte[]? binaryChunk) = Parse(path);
        byte[][] buffers = ResolveBuffers(path, root, binaryChunk);

        var parts = new List<PartBuilder>();

        int[]? sceneNodes = root.Scenes is { Length: > 0 }
            ? root.Scenes[Math.Clamp(root.Scene, 0, root.Scenes.Length - 1)].Nodes
            : null;

        if (sceneNodes is null)
        {
            for (int i = 0; i < (root.Nodes?.Length ?? 0); i++)
            {
                AppendPart(root, buffers, i, Matrix.Identity, -1, options, parts);
            }
        }
        else
        {
            foreach (int node in sceneNodes)
            {
                AppendPart(root, buffers, node, Matrix.Identity, -1, options, parts);
            }
        }

        if (parts.Count == 0)
        {
            throw new InvalidDataException($"'{path}' contains no renderable triangle geometry.");
        }

        // Orientation first, then the explicit correction, exactly as the merged
        // path does it — an explicit yaw must always win.
        Matrix orientation = Matrix.Identity;

        if (options.AlignLongestHorizontalAxis)
        {
            Vector3 raw = Bounds(parts, out _, out _);

            if (raw.Z > raw.X)
            {
                orientation = Matrix.CreateRotationY(-MathHelper.PiOver2);
            }
        }

        if (options.YawOffsetDegrees != 0f)
        {
            orientation *= Matrix.CreateRotationY(MathHelper.ToRadians(options.YawOffsetDegrees));
        }

        Vector3 size = Bounds(parts, out Vector3 min, out Vector3 max, orientation);
        float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        float scale = longest > 1e-6f && options.TargetSizeMetres > 0f ? options.TargetSizeMetres / longest : 1f;

        Vector3 centre = new((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);

        // Row-vector convention: a vertex is travelled through orientation, then
        // scale, then the centring translation.
        Matrix modelTransform = orientation
            * Matrix.CreateScale(scale)
            * Matrix.CreateTranslation(-centre * scale);

        ModelPart[] built = new ModelPart[parts.Count];

        for (int i = 0; i < parts.Count; i++)
        {
            PartBuilder builder = parts[i];
            VertexPositionNormal[] vertices = new VertexPositionNormal[builder.Positions.Count];

            // Per part, not per model: normalising across the whole model would
            // let one bright plate drag every other part's palette with it, which
            // is exactly how a tank ends up as a single flat colour.
            float brightest = 0f;

            foreach (Vector3 color in builder.Colors)
            {
                brightest = MathF.Max(brightest, MathF.Max(color.X, MathF.Max(color.Y, color.Z)));
            }

            float colorScale = brightest < 0.02f ? 0.5f / MathF.Max(brightest, 0.001f) : 1f;

            for (int v = 0; v < vertices.Length; v++)
            {
                Vector3 color = builder.Colors[v] * colorScale;

                vertices[v] = new VertexPositionNormal(
                    builder.Positions[v],
                    builder.Normals[v],
                    new Color(
                        Channel(color.X),
                        Channel(color.Y),
                        Channel(color.Z),
                        PaintMask(builder.Masks[v])));
            }

            built[i] = new ModelPart(
                builder.Name,
                new MeshData(vertices, [.. builder.Indices]),
                builder.LocalTransform,
                builder.ParentIndex);
        }

        return new ModelData(built, modelTransform, size * scale);
    }

    /// <summary>Accumulates one named part's geometry while the scene graph is walked.</summary>
    private sealed class PartBuilder(string name, Matrix localTransform, int parentIndex)
    {
        public string Name { get; } = name;

        /// <summary>Sits inside its parent, not inside the model.</summary>
        public Matrix LocalTransform { get; } = localTransform;

        public int ParentIndex { get; } = parentIndex;

        public List<Vector3> Positions { get; } = new(512);

        public List<Vector3> Normals { get; } = new(512);

        public List<Vector3> Colors { get; } = new(512);

        /// <summary>Faction paint mask per vertex: 1 team colour, 0 bare material.</summary>
        public List<float> Masks { get; } = new(512);

        public List<ushort> Indices { get; } = new(1024);
    }

    private static void AppendPart(
        GltfRoot root,
        byte[][] buffers,
        int nodeIndex,
        Matrix parentTransform,
        int parentPartIndex,
        ModelImportOptions options,
        List<PartBuilder> parts)
    {
        GltfNode[] nodes = root.Nodes ?? [];

        if ((uint)nodeIndex >= (uint)nodes.Length)
        {
            return;
        }

        GltfNode node = nodes[nodeIndex];

        // The node's own transform, not the accumulated one. Children are walked
        // with the accumulated transform so the bounding box stays right, but each
        // part keeps only its own step of the chain.
        Matrix local = NodeTransform(node);
        Matrix world = local * parentTransform;
        int partIndex = parentPartIndex;

        if (node.Mesh is int meshIndex && root.Meshes is not null && (uint)meshIndex < (uint)root.Meshes.Length)
        {
            string name = node.Name
                ?? root.Meshes[meshIndex].Name
                ?? $"part{parts.Count}";

            var builder = new PartBuilder(name, local, parentPartIndex);

            // Identity here: the node's own transform becomes the part's local
            // transform, so the geometry stays in the space the pivot lives in.
            AppendMesh(
                root,
                buffers,
                root.Meshes[meshIndex],
                Matrix.Identity,
                options,
                builder.Positions,
                builder.Normals,
                builder.Colors,
                builder.Masks,
                builder.Indices);

            if (builder.Positions.Count > 0)
            {
                parts.Add(builder);
                partIndex = parts.Count - 1;
            }
        }

        if (node.Children is null)
        {
            return;
        }

        foreach (int child in node.Children)
        {
            AppendPart(root, buffers, child, world, partIndex, options, parts);
        }
    }

    /// <summary>
    /// Combined extent of every part, optionally after the model's orientation is
    /// applied. The size is returned so the caller can decide the alignment from it.
    /// </summary>
    private static Vector3 Bounds(List<PartBuilder> parts, out Vector3 min, out Vector3 max)
        => Bounds(parts, out min, out max, Matrix.Identity);

    private static Vector3 Bounds(List<PartBuilder> parts, out Vector3 min, out Vector3 max, Matrix orientation)
    {
        min = new Vector3(float.MaxValue);
        max = new Vector3(float.MinValue);

        foreach (PartBuilder builder in parts)
        {
            foreach (Vector3 local in builder.Positions)
            {
                Vector3 position = Vector3.Transform(local, Accumulated(parts, builder) * orientation);
                min = Vector3.Min(min, position);
                max = Vector3.Max(max, position);
            }
        }

        return max - min;
    }

    /// <summary>
    /// Walks a part's chain of parents to get the transform that places it in the
    /// model. Parts are stored parents-first, so this only ever looks backwards.
    /// </summary>
    private static Matrix Accumulated(List<PartBuilder> parts, PartBuilder part)
    {
        Matrix result = part.LocalTransform;
        int parent = part.ParentIndex;

        while (parent >= 0 && parent < parts.Count)
        {
            result *= parts[parent].LocalTransform;
            parent = parts[parent].ParentIndex;
        }

        return result;
    }

    private static (GltfRoot Root, byte[]? Binary) Parse(string path)
    {
        byte[] file = File.ReadAllBytes(path);

        if (file.Length >= 12 && BitConverter.ToUInt32(file, 0) == GlbMagic)
        {
            return ParseGlb(path, file);
        }

        string json = Encoding.UTF8.GetString(file);
        GltfRoot root = JsonSerializer.Deserialize<GltfRoot>(json, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' is not a valid glTF document.");

        return (root, null);
    }

    private static (GltfRoot Root, byte[]? Binary) ParseGlb(string path, byte[] file)
    {
        int offset = 12;
        string? json = null;
        byte[]? binary = null;

        while (offset + 8 <= file.Length)
        {
            int chunkLength = (int)BitConverter.ToUInt32(file, offset);
            uint chunkType = BitConverter.ToUInt32(file, offset + 4);
            int dataStart = offset + 8;

            if (chunkLength < 0 || dataStart + chunkLength > file.Length)
            {
                throw new InvalidDataException($"'{path}' has a corrupt GLB chunk header.");
            }

            if (chunkType == ChunkJson)
            {
                json = Encoding.UTF8.GetString(file, dataStart, chunkLength);
            }
            else if (chunkType == ChunkBinary)
            {
                binary = new byte[chunkLength];
                Array.Copy(file, dataStart, binary, 0, chunkLength);
            }

            offset = dataStart + chunkLength;
        }

        if (json is null)
        {
            throw new InvalidDataException($"'{path}' contains no JSON chunk.");
        }

        GltfRoot root = JsonSerializer.Deserialize<GltfRoot>(json, JsonOptions)
            ?? throw new InvalidDataException($"'{path}' is not a valid glTF document.");

        return (root, binary);
    }

    private static byte[][] ResolveBuffers(string path, GltfRoot root, byte[]? binaryChunk)
    {
        GltfBuffer[] declared = root.Buffers ?? [];
        byte[][] buffers = new byte[declared.Length][];
        string? directory = Path.GetDirectoryName(Path.GetFullPath(path));

        for (int i = 0; i < declared.Length; i++)
        {
            string? uri = declared[i].Uri;

            if (string.IsNullOrEmpty(uri))
            {
                buffers[i] = binaryChunk
                    ?? throw new InvalidDataException($"'{path}' declares an embedded buffer but has no binary chunk.");
            }
            else if (uri.StartsWith("data:", StringComparison.OrdinalIgnoreCase))
            {
                int comma = uri.IndexOf(',', StringComparison.Ordinal);
                buffers[i] = Convert.FromBase64String(uri[(comma + 1)..]);
            }
            else
            {
                string bufferPath = Path.Combine(directory ?? ".", Uri.UnescapeDataString(uri));
                buffers[i] = File.ReadAllBytes(bufferPath);
            }
        }

        return buffers;
    }

    private static void AppendNode(
        GltfRoot root,
        byte[][] buffers,
        int nodeIndex,
        Matrix parentTransform,
        ModelImportOptions options,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector3> colors,
        List<float> masks,
        List<ushort> indices)
    {
        GltfNode[] nodes = root.Nodes ?? [];
        if ((uint)nodeIndex >= (uint)nodes.Length)
        {
            return;
        }

        GltfNode node = nodes[nodeIndex];
        Matrix transform = NodeTransform(node) * parentTransform;

        if (node.Mesh is int meshIndex && root.Meshes is not null && (uint)meshIndex < (uint)root.Meshes.Length)
        {
            AppendMesh(root, buffers, root.Meshes[meshIndex], transform, options, positions, normals, colors, masks, indices);
        }

        if (node.Children is null)
        {
            return;
        }

        foreach (int child in node.Children)
        {
            AppendNode(root, buffers, child, transform, options, positions, normals, colors, masks, indices);
        }
    }

    private static Matrix NodeTransform(GltfNode node)
    {
        if (node.Matrix is { Length: 16 } m)
        {
            // glTF matrices are column-major; XNA's are row-major.
            return new Matrix(
                m[0], m[4], m[8], m[12],
                m[1], m[5], m[9], m[13],
                m[2], m[6], m[10], m[14],
                m[3], m[7], m[11], m[15]);
        }

        Vector3 translation = node.Translation is { Length: 3 } t ? new Vector3(t[0], t[1], t[2]) : Vector3.Zero;
        Vector3 scale = node.Scale is { Length: 3 } s ? new Vector3(s[0], s[1], s[2]) : Vector3.One;
        Quaternion rotation = node.Rotation is { Length: 4 } r ? new Quaternion(r[0], r[1], r[2], r[3]) : Quaternion.Identity;

        return Matrix.CreateScale(scale) * Matrix.CreateFromQuaternion(rotation) * Matrix.CreateTranslation(translation);
    }

    private static void AppendMesh(
        GltfRoot root,
        byte[][] buffers,
        GltfMesh mesh,
        Matrix transform,
        ModelImportOptions options,
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector3> colors,
        List<float> masks,
        List<ushort> indices)
    {
        if (mesh.Primitives is null)
        {
            return;
        }

        if (options.MeshNameContains is { Length: > 0 } filter
            && (mesh.Name is null || !mesh.Name.Contains(filter, StringComparison.OrdinalIgnoreCase)))
        {
            return;
        }

        Matrix normalTransform = Matrix.Transpose(Matrix.Invert(transform));

        foreach (GltfPrimitive primitive in mesh.Primitives)
        {
            if (primitive.Mode != 4)
            {
                throw new NotSupportedException($"Mesh '{mesh.Name}' uses primitive mode {primitive.Mode}; only TRIANGLES is supported.");
            }

            if (primitive.Attributes is null)
            {
                continue;
            }

            int positionAccessor = FindAttribute(primitive, "POSITION");
            if (positionAccessor < 0)
            {
                continue;
            }

            int vertexCount = root.Accessors![positionAccessor].Count;
            if (positions.Count + vertexCount > ushort.MaxValue)
            {
                throw new NotSupportedException("Model exceeds 65,535 vertices; split it into parts.");
            }

            float[] rawPositions = ReadFloats(root, buffers, positionAccessor);
            int normalAccessor = FindAttribute(primitive, "NORMAL");
            float[]? rawNormals = normalAccessor >= 0 ? ReadFloats(root, buffers, normalAccessor) : null;
            int colorAccessor = FindAttribute(primitive, "COLOR_0");
            float[]? rawColors = colorAccessor >= 0 ? ReadFloats(root, buffers, colorAccessor) : null;
            int colorComponents = colorAccessor >= 0 ? ComponentCount(root.Accessors![colorAccessor].Type) : 0;

            float materialShade = MaterialShade(root, primitive.Material);
            ushort baseIndex = (ushort)positions.Count;

            for (int v = 0; v < vertexCount; v++)
            {
                Vector3 position = new(rawPositions[v * 3], rawPositions[(v * 3) + 1], rawPositions[(v * 3) + 2]);
                positions.Add(Vector3.Transform(position, transform));

                if (rawNormals is not null)
                {
                    Vector3 normal = new(rawNormals[v * 3], rawNormals[(v * 3) + 1], rawNormals[(v * 3) + 2]);
                    Vector3 transformed = Vector3.Normalize(Vector3.TransformNormal(normal, normalTransform));
                    normals.Add(options.FlipNormals ? -transformed : transformed);
                }
                else
                {
                    normals.Add(Vector3.Up);
                }

                // Colour is kept as RGB plus the paint mask in alpha rather than
                // collapsed to one shade. The mask is what lets the faction tint
                // cover the armour plates while leaving the tracks black and the
                // gun barrel gunmetal; throwing it away would flatten the model
                // into a single hue.
                Vector3 vertexColor = Vector3.One;
                float paintMask = 1f;

                if (rawColors is not null)
                {
                    vertexColor = colorComponents >= 3
                        ? new Vector3(
                            rawColors[v * colorComponents],
                            rawColors[(v * colorComponents) + 1],
                            rawColors[(v * colorComponents) + 2])
                        : new Vector3(Luminance(rawColors, v, 1));

                    if (vertexColor.X <= 0.001f && vertexColor.Y <= 0.001f && vertexColor.Z <= 0.001f)
                    {
                        vertexColor = Vector3.One;
                    }

                    if (colorComponents >= 4)
                    {
                        paintMask = Math.Clamp(rawColors[(v * colorComponents) + 3], 0f, 1f);
                    }
                }

                colors.Add(vertexColor * materialShade);
                masks.Add(paintMask);
            }

            if (primitive.Indices is int indexAccessor)
            {
                int[] rawIndices = ReadIndices(root, buffers, indexAccessor);
                foreach (int index in rawIndices)
                {
                    indices.Add((ushort)(baseIndex + index));
                }
            }
            else
            {
                for (int v = 0; v < vertexCount; v++)
                {
                    indices.Add((ushort)(baseIndex + v));
                }
            }
        }
    }

    private static MeshData BuildMesh(
        List<Vector3> positions,
        List<Vector3> normals,
        List<Vector3> colors,
        List<float> masks,
        List<ushort> indices,
        ModelImportOptions options)
    {
        // Most glTF exports face -Z. Turning the longest horizontal axis to +X
        // puts vehicles and aircraft nose-first without per-asset tuning.
        if (options.AlignLongestHorizontalAxis)
        {
            Vector3 initialSize = Size(positions);

            if (initialSize.Z > initialSize.X)
            {
                Matrix yaw = Matrix.CreateRotationY(-MathHelper.PiOver2);

                for (int i = 0; i < positions.Count; i++)
                {
                    positions[i] = Vector3.Transform(positions[i], yaw);
                    normals[i] = Vector3.Normalize(Vector3.TransformNormal(normals[i], yaw));
                }
            }
        }

        // Then the per-model correction, so an explicit yaw always wins.
        if (options.YawOffsetDegrees != 0f)
        {
            Matrix yaw = Matrix.CreateRotationY(MathHelper.ToRadians(options.YawOffsetDegrees));

            for (int i = 0; i < positions.Count; i++)
            {
                positions[i] = Vector3.Transform(positions[i], yaw);
                normals[i] = Vector3.Normalize(Vector3.TransformNormal(normals[i], yaw));
            }
        }

        // Fit to the requested size, then centre horizontally and sit on the ground.
        Vector3 min = positions[0];
        Vector3 max = positions[0];

        foreach (Vector3 position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        Vector3 size = max - min;
        float longest = MathF.Max(size.X, MathF.Max(size.Y, size.Z));
        float scale = longest > 1e-6f && options.TargetSizeMetres > 0f ? options.TargetSizeMetres / longest : 1f;

        Vector3 centre = new((min.X + max.X) * 0.5f, min.Y, (min.Z + max.Z) * 0.5f);

        // A mesh that is entirely black is a broken export rather than a
        // deliberate choice, so it is lifted; anything else is left exactly as
        // the modeller authored it. Normalising every mesh to its brightest
        // channel — which this used to do — silently erased the difference
        // between dark tracks and light armour and made every vehicle one flat
        // colour.
        float brightest = 0f;

        foreach (Vector3 color in colors)
        {
            brightest = MathF.Max(brightest, MathF.Max(color.X, MathF.Max(color.Y, color.Z)));
        }

        float colorScale = brightest < 0.02f ? 0.5f / MathF.Max(brightest, 0.001f) : 1f;

        VertexPositionNormal[] vertices = new VertexPositionNormal[positions.Count];

        for (int i = 0; i < positions.Count; i++)
        {
            Vector3 position = (positions[i] - centre) * scale;
            Vector3 color = colors[i] * colorScale;

            vertices[i] = new VertexPositionNormal(
                position,
                normals[i],
                new Color(Channel(color.X), Channel(color.Y), Channel(color.Z), PaintMask(masks[i])));
        }

        return new MeshData(vertices, [.. indices]);
    }

    /// <summary>Converts a 0..1 channel into a vertex colour byte.</summary>
    private static byte Channel(float value)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    /// <summary>The faction paint mask byte, as the mesh shader reads it.</summary>
    private static byte PaintMask(float value)
        => (byte)Math.Clamp((int)MathF.Round(Math.Clamp(value, 0f, 1f) * 255f), 0, 255);

    private static Vector3 Size(List<Vector3> positions)
    {
        Vector3 min = positions[0];
        Vector3 max = positions[0];

        foreach (Vector3 position in positions)
        {
            min = Vector3.Min(min, position);
            max = Vector3.Max(max, position);
        }

        return max - min;
    }

    private static int FindAttribute(GltfPrimitive primitive, string name)
    {
        if (primitive.Attributes is null)
        {
            return -1;
        }

        if (primitive.Attributes.TryGetValue(name, out int index))
        {
            return index;
        }

        foreach ((string key, int value) in primitive.Attributes)
        {
            if (string.Equals(key, name, StringComparison.OrdinalIgnoreCase))
            {
                return value;
            }
        }

        return -1;
    }

    private static float MaterialShade(GltfRoot root, int? materialIndex)
    {
        if (materialIndex is not int index || root.Materials is null || (uint)index >= (uint)root.Materials.Length)
        {
            return 1f;
        }

        float[]? factor = root.Materials[index].PbrMetallicRoughness?.BaseColorFactor;
        if (factor is not { Length: >= 3 })
        {
            return 1f;
        }

        float luminance = (factor[0] * 0.299f) + (factor[1] * 0.587f) + (factor[2] * 0.114f);
        return luminance <= 0.001f ? 1f : luminance;
    }

    private static float Luminance(float[] values, int offset, int components)
        => components >= 3
            ? (values[offset] * 0.299f) + (values[offset + 1] * 0.587f) + (values[offset + 2] * 0.114f)
            : 1f;

    private static int ComponentCount(string? type) => type switch
    {
        "SCALAR" => 1,
        "VEC2" => 2,
        "VEC3" => 3,
        "VEC4" => 4,
        _ => 0,
    };

    private static float[] ReadFloats(GltfRoot root, byte[][] buffers, int accessorIndex)
    {
        GltfAccessor accessor = GetAccessor(root, accessorIndex);
        int components = ComponentCount(accessor.Type);
        if (components == 0)
        {
            throw new NotSupportedException($"Accessor type '{accessor.Type}' is not supported.");
        }

        (byte[] buffer, int start, int stride) = Locate(root, buffers, accessor, components);
        float[] result = new float[accessor.Count * components];
        int elementSize = ComponentSize(accessor.ComponentType) * components;

        for (int i = 0; i < accessor.Count; i++)
        {
            int elementOffset = start + (i * (stride > 0 ? stride : elementSize));

            for (int c = 0; c < components; c++)
            {
                result[(i * components) + c] = ReadComponent(buffer, elementOffset + (c * ComponentSize(accessor.ComponentType)), accessor);
            }
        }

        return result;
    }

    private static int[] ReadIndices(GltfRoot root, byte[][] buffers, int accessorIndex)
    {
        GltfAccessor accessor = GetAccessor(root, accessorIndex);
        (byte[] buffer, int start, int stride) = Locate(root, buffers, accessor, 1);
        int[] result = new int[accessor.Count];
        int componentSize = ComponentSize(accessor.ComponentType);

        for (int i = 0; i < accessor.Count; i++)
        {
            int elementOffset = start + (i * (stride > 0 ? stride : componentSize));

            result[i] = accessor.ComponentType switch
            {
                5121 => buffer[elementOffset],
                5123 => BitConverter.ToUInt16(buffer, elementOffset),
                5125 => (int)BitConverter.ToUInt32(buffer, elementOffset),
                _ => throw new NotSupportedException($"Index component type {accessor.ComponentType} is not supported."),
            };
        }

        return result;
    }

    private static GltfAccessor GetAccessor(GltfRoot root, int index)
    {
        GltfAccessor[] accessors = root.Accessors ?? [];
        if ((uint)index >= (uint)accessors.Length)
        {
            throw new InvalidDataException($"Accessor {index} is out of range.");
        }

        GltfAccessor accessor = accessors[index];
        if (accessor.Sparse is { Count: > 0 })
        {
            throw new NotSupportedException("Sparse accessors are not supported.");
        }

        return accessor;
    }

    private static (byte[] Buffer, int Start, int Stride) Locate(GltfRoot root, byte[][] buffers, GltfAccessor accessor, int components)
    {
        if (accessor.BufferView is not int viewIndex)
        {
            return (new byte[0], 0, 0);
        }

        GltfBufferView[] views = root.BufferViews ?? [];
        if ((uint)viewIndex >= (uint)views.Length)
        {
            throw new InvalidDataException($"Buffer view {viewIndex} is out of range.");
        }

        GltfBufferView view = views[viewIndex];
        if ((uint)view.Buffer >= (uint)buffers.Length)
        {
            throw new InvalidDataException($"Buffer {view.Buffer} is out of range.");
        }

        int stride = view.ByteStride ?? 0;
        int minimum = ComponentSize(accessor.ComponentType) * components;

        if (stride != 0 && stride < minimum)
        {
            throw new InvalidDataException($"Buffer view stride {stride} is smaller than the element size {minimum}.");
        }

        return (buffers[view.Buffer], view.ByteOffset + accessor.ByteOffset, stride);
    }

    private static float ReadComponent(byte[] buffer, int offset, GltfAccessor accessor)
    {
        int componentType = accessor.ComponentType;

        return componentType switch
        {
            5126 => BitConverter.ToSingle(buffer, offset),
            5123 => accessor.Normalized ? BitConverter.ToUInt16(buffer, offset) / 65535f : BitConverter.ToUInt16(buffer, offset),
            5121 => accessor.Normalized ? buffer[offset] / 255f : buffer[offset],
            5122 => accessor.Normalized
                ? MathF.Max(BitConverter.ToInt16(buffer, offset) / 32767f, -1f)
                : BitConverter.ToInt16(buffer, offset),
            5120 => accessor.Normalized
                ? MathF.Max((sbyte)buffer[offset] / 127f, -1f)
                : (sbyte)buffer[offset],
            5125 => BitConverter.ToUInt32(buffer, offset),
            _ => throw new NotSupportedException($"Component type {componentType} is not supported."),
        };
    }

    private static int ComponentSize(int componentType) => componentType switch
    {
        5120 or 5121 => 1,
        5122 or 5123 => 2,
        5125 or 5126 => 4,
        _ => throw new NotSupportedException($"Component type {componentType} is not supported."),
    };
}
