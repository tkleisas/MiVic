using MiVic.Game.Rendering.Gltf;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering;

/// <summary>
/// Flattens an imported model's node hierarchy into one mesh in the model's own space.
/// <para>
/// For anything the renderer draws as a single instanced mesh rather than as an animated
/// hierarchy: a tree sways as a whole and has no turret to traverse, a bridge block has nothing
/// that moves at all, and keeping a node list for either would buy a matrix multiply per instance
/// per frame and nothing else. What the flatten also buys is a mesh whose origin is its base, once
/// the model transform is in — which is what the foliage pass weights its sway against, and what
/// lets the bridge be placed by the water line it stands on.
/// </para>
/// </summary>
public static class ModelBake
{
    /// <summary>
    /// Bakes a model into one mesh, and reports how tall the result stands in metres.
    /// </summary>
    /// <param name="model">The imported model, its parts already in parent order.</param>
    /// <param name="height">Top of the baked mesh, in metres, measured off the vertices.</param>
    public static MeshData Flatten(ModelData model, out float height)
    {
        ArgumentNullException.ThrowIfNull(model);

        List<VertexPositionNormal> vertices = [];
        List<ushort> indices = [];

        // Parts arrive parents-first, so a parent's transform is always known by the
        // time its children are walked.
        Matrix[] placed = new Matrix[model.Parts.Count];

        for (int i = 0; i < model.Parts.Count; i++)
        {
            ModelPart part = model.Parts[i];

            placed[i] = part.ParentIndex >= 0 && part.ParentIndex < i
                ? part.LocalTransform * placed[part.ParentIndex]
                : part.LocalTransform;

            Matrix transform = placed[i] * model.ModelTransform;
            var index = (ushort)vertices.Count;

            foreach (VertexPositionNormal vertex in part.Mesh.Vertices)
            {
                vertices.Add(new VertexPositionNormal(
                    Vector3.Transform(vertex.Position, transform),
                    Vector3.Normalize(Vector3.TransformNormal(vertex.Normal, transform)),
                    vertex.Color));
            }

            foreach (ushort indexInPart in part.Mesh.Indices)
            {
                indices.Add((ushort)(index + indexInPart));
            }
        }

        height = 0f;

        foreach (VertexPositionNormal vertex in vertices)
        {
            height = MathF.Max(height, vertex.Position.Y);
        }

        return new MeshData([.. vertices], [.. indices]);
    }
}
