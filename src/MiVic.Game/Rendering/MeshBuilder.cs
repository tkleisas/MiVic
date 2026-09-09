using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering;

/// <summary>
/// Builds the placeholder geometry used until real art exists.
/// <para>
/// Every primitive is generated in code, so the project has no art dependency
/// and the meshes are identical on every machine. Units are built around their
/// origin with +Y up and the model facing +X, matching the simulation's heading
/// convention (0 brads faces +X).
/// </para>
/// </summary>
public static class MeshBuilder
{
    /// <summary>
    /// A camera-facing quad in the XY plane, centred on the origin. Particles use
    /// it as a billboard: the CPU builds a per-particle matrix from the camera's
    /// own right and up vectors, so the quad always faces the viewer.
    /// </summary>
    public static MeshData Quad(float width, float height)
    {
        float x = width * 0.5f;
        float y = height * 0.5f;

        VertexPositionNormal[] vertices =
        [
            new(new Vector3(-x, -y, 0f), Vector3.UnitZ),
            new(new Vector3(x, -y, 0f), Vector3.UnitZ),
            new(new Vector3(x, y, 0f), Vector3.UnitZ),
            new(new Vector3(-x, y, 0f), Vector3.UnitZ),
        ];

        // Counter-clockwise seen from +Z, matching every other mesh here.
        ushort[] indices = [0, 1, 2, 0, 2, 3];

        return new MeshData(vertices, indices);
    }

    /// <summary>Axis-aligned box centred on the origin, footprint centred, base at y = 0.</summary>
    public static MeshData Box(float width, float height, float depth)
    {
        float x = width * 0.5f;
        float z = depth * 0.5f;

        VertexPositionNormal[] vertices =
        [
            // +Y (top)
            new(new Vector3(-x, height, -z), Vector3.Up),
            new(new Vector3(x, height, -z), Vector3.Up),
            new(new Vector3(x, height, z), Vector3.Up),
            new(new Vector3(-x, height, z), Vector3.Up),

            // -Y (bottom)
            new(new Vector3(-x, 0, z), -Vector3.Up),
            new(new Vector3(x, 0, z), -Vector3.Up),
            new(new Vector3(x, 0, -z), -Vector3.Up),
            new(new Vector3(-x, 0, -z), -Vector3.Up),

            // +X
            new(new Vector3(x, 0, -z), Vector3.UnitX),
            new(new Vector3(x, height, -z), Vector3.UnitX),
            new(new Vector3(x, height, z), Vector3.UnitX),
            new(new Vector3(x, 0, z), Vector3.UnitX),

            // -X
            new(new Vector3(-x, 0, z), -Vector3.UnitX),
            new(new Vector3(-x, height, z), -Vector3.UnitX),
            new(new Vector3(-x, height, -z), -Vector3.UnitX),
            new(new Vector3(-x, 0, -z), -Vector3.UnitX),

            // +Z
            new(new Vector3(-x, 0, z), Vector3.UnitZ),
            new(new Vector3(-x, height, z), Vector3.UnitZ),
            new(new Vector3(x, height, z), Vector3.UnitZ),
            new(new Vector3(x, 0, z), Vector3.UnitZ),

            // -Z
            new(new Vector3(x, 0, -z), -Vector3.UnitZ),
            new(new Vector3(x, height, -z), -Vector3.UnitZ),
            new(new Vector3(-x, height, -z), -Vector3.UnitZ),
            new(new Vector3(-x, 0, -z), -Vector3.UnitZ),
        ];

        ushort[] indices = new ushort[36];
        for (int face = 0; face < 6; face++)
        {
            int v = face * 4;
            int i = face * 6;
            indices[i + 0] = (ushort)(v + 0);
            indices[i + 1] = (ushort)(v + 1);
            indices[i + 2] = (ushort)(v + 2);
            indices[i + 3] = (ushort)(v + 0);
            indices[i + 4] = (ushort)(v + 2);
            indices[i + 5] = (ushort)(v + 3);
        }

        return new MeshData(vertices, indices);
    }

    /// <summary>Wedge shape, useful for aircraft and sloped armour. Faces +X, base at y = 0.</summary>
    public static MeshData Wedge(float width, float height, float depth)
    {
        float x = width * 0.5f;
        float z = depth * 0.5f;

        VertexPositionNormal[] vertices =
        [
            new(new Vector3(x, 0, -z), -Vector3.UnitX),          // 0 nose bottom
            new(new Vector3(x, 0, z), -Vector3.UnitX),           // 1 nose bottom
            new(new Vector3(x, height * 0.5f, 0), -Vector3.UnitX), // 2 nose tip
            new(new Vector3(-x, 0, -z), Vector3.UnitX),          // 3 tail bottom left
            new(new Vector3(-x, 0, z), Vector3.UnitX),           // 4 tail bottom right
            new(new Vector3(-x, height, 0), Vector3.UnitX),      // 5 tail top
        ];

        ushort[] indices =
        [
            0, 1, 2,             // nose cap
            3, 5, 4,             // tail cap
            0, 2, 5, 0, 5, 3,    // -Z side
            1, 4, 5, 1, 5, 2,    // +Z side
            0, 3, 4, 0, 4, 1,    // underside
        ];

        return new MeshData(vertices, indices);
    }

    /// <summary>Vertical cylinder with caps. Base at y = 0.</summary>
    public static MeshData Cylinder(float radius, float height, int segments = 12)
    {
        if (segments < 3)
        {
            throw new ArgumentOutOfRangeException(nameof(segments), segments, "At least three segments are required.");
        }

        int vertexCount = (segments * 2) + (segments * 2) + 2;
        var vertices = new List<VertexPositionNormal>(vertexCount);
        var indices = new List<ushort>(segments * 12);

        // Side quads.
        for (int i = 0; i < segments; i++)
        {
            float a0 = MathHelper.TwoPi * i / segments;
            float a1 = MathHelper.TwoPi * (i + 1) / segments;

            Vector3 n0 = new(MathF.Cos(a0), 0f, MathF.Sin(a0));
            Vector3 n1 = new(MathF.Cos(a1), 0f, MathF.Sin(a1));

            ushort baseIndex = (ushort)vertices.Count;
            vertices.Add(new VertexPositionNormal(n0 * radius, n0));
            vertices.Add(new VertexPositionNormal((n0 * radius) + new Vector3(0, height, 0), n0));
            vertices.Add(new VertexPositionNormal((n1 * radius) + new Vector3(0, height, 0), n1));
            vertices.Add(new VertexPositionNormal(n1 * radius, n1));

            indices.Add(baseIndex);
            indices.Add((ushort)(baseIndex + 1));
            indices.Add((ushort)(baseIndex + 2));
            indices.Add(baseIndex);
            indices.Add((ushort)(baseIndex + 2));
            indices.Add((ushort)(baseIndex + 3));
        }

        // Caps, as fans around a centre vertex each.
        ushort topCentre = (ushort)vertices.Count;
        vertices.Add(new VertexPositionNormal(new Vector3(0, height, 0), Vector3.Up));
        ushort topRing = (ushort)vertices.Count;
        for (int i = 0; i < segments; i++)
        {
            float a = MathHelper.TwoPi * i / segments;
            vertices.Add(new VertexPositionNormal(new Vector3(MathF.Cos(a) * radius, height, MathF.Sin(a) * radius), Vector3.Up));
        }

        ushort bottomCentre = (ushort)vertices.Count;
        vertices.Add(new VertexPositionNormal(Vector3.Zero, -Vector3.Up));
        ushort bottomRing = (ushort)vertices.Count;
        for (int i = 0; i < segments; i++)
        {
            float a = MathHelper.TwoPi * i / segments;
            vertices.Add(new VertexPositionNormal(new Vector3(MathF.Cos(a) * radius, 0f, MathF.Sin(a) * radius), -Vector3.Up));
        }

        for (int i = 0; i < segments; i++)
        {
            ushort next = (ushort)((i + 1) % segments);

            indices.Add(topCentre);
            indices.Add((ushort)(topRing + i));
            indices.Add((ushort)(topRing + next));

            indices.Add(bottomCentre);
            indices.Add((ushort)(bottomRing + next));
            indices.Add((ushort)(bottomRing + i));
        }

        return new MeshData([.. vertices], [.. indices]);
    }

    /// <summary>Ground plane centred on the origin, facing up, split into tiles.</summary>
    public static MeshData Plane(float width, float depth, int tiles = 1)
    {
        if (tiles < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(tiles), tiles, "At least one tile is required.");
        }

        int steps = tiles + 1;
        var vertices = new VertexPositionNormal[steps * steps];

        for (int z = 0; z < steps; z++)
        {
            for (int x = 0; x < steps; x++)
            {
                float px = ((float)x / tiles - 0.5f) * width;
                float pz = ((float)z / tiles - 0.5f) * depth;
                vertices[(z * steps) + x] = new VertexPositionNormal(new Vector3(px, 0f, pz), Vector3.Up);
            }
        }

        var indices = new ushort[tiles * tiles * 6];
        int i = 0;
        for (int z = 0; z < tiles; z++)
        {
            for (int x = 0; x < tiles; x++)
            {
                ushort v = (ushort)((z * steps) + x);
                indices[i++] = v;
                indices[i++] = (ushort)(v + (ushort)steps + 1);
                indices[i++] = (ushort)(v + (ushort)steps);
                indices[i++] = v;
                indices[i++] = (ushort)(v + 1);
                indices[i++] = (ushort)(v + (ushort)steps + 1);
            }
        }

        return new MeshData(vertices, indices);
    }
}
