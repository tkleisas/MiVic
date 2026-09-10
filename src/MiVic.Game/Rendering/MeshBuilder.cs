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

    /// <summary>
    /// A cube centred on the origin.
    /// <para>
    /// Distinct from <see cref="Box"/>, which sits on its base because buildings and
    /// unit bodies stand on the ground. Anything that flies is placed by its centre,
    /// and a box placed by its base needs half its height subtracted at every call
    /// site — which is the sort of correction that gets forgotten once and leaves
    /// every shell flying half a metre low.
    /// </para>
    /// </summary>
    public static MeshData Cube(float size)
    {
        MeshData box = Box(size, size, size);
        float half = size * 0.5f;

        for (int i = 0; i < box.Vertices.Length; i++)
        {
            VertexPositionNormal vertex = box.Vertices[i];
            vertex.Position.Y -= half;
            box.Vertices[i] = vertex;
        }

        return box;
    }

    /// <summary>
    /// A low-poly sphere centred on the origin, radius 1.
    /// <para>
    /// Faceted on purpose, and coarse: it is the body of an explosion, seen for a
    /// fifth of a second while it expands, and a smooth sphere would cost vertices to
    /// look like a bad ball where a faceted one looks like a deliberate one. Twelve
    /// segments and eight rings is 96 triangles, which is nothing to draw a hundred of.
    /// </para>
    /// <para>
    /// Unit radius rather than a radius in metres, so the caller scales it: a blast
    /// grows, and its mesh should not have to.
    /// </para>
    /// </summary>
    public static MeshData Sphere(int segments = 12, int rings = 8)
    {
        var vertices = new List<VertexPositionNormal>((segments + 1) * (rings + 1));
        var indices = new List<ushort>(segments * rings * 6);

        for (int ring = 0; ring <= rings; ring++)
        {
            // Top to bottom, so the poles are single vertices rather than a fan of
            // coincident ones.
            float phi = MathF.PI * ring / rings;
            float y = MathF.Cos(phi);
            float r = MathF.Sin(phi);

            for (int segment = 0; segment <= segments; segment++)
            {
                float theta = MathF.Tau * segment / segments;
                var normal = new Vector3(r * MathF.Cos(theta), y, r * MathF.Sin(theta));

                vertices.Add(new VertexPositionNormal(normal, normal));
            }
        }

        int stride = segments + 1;

        for (int ring = 0; ring < rings; ring++)
        {
            for (int segment = 0; segment < segments; segment++)
            {
                ushort a = (ushort)((ring * stride) + segment);
                ushort b = (ushort)(a + stride);

                indices.Add(a);
                indices.Add(b);
                indices.Add((ushort)(a + 1));

                indices.Add((ushort)(a + 1));
                indices.Add(b);
                indices.Add((ushort)(b + 1));
            }
        }

        return new MeshData([.. vertices], [.. indices]);
    }

    /// <summary>
    /// A flat annulus in the XY plane, centred on the origin.
    /// <para>
    /// A shockwave drawn with <see cref="Quad"/> is a filled square that grows, which
    /// reads as a sheet of paper being pulled across the ground rather than as a blast
    /// going outwards. The shape is the entire effect.
    /// </para>
    /// </summary>
    public static MeshData Ring(float innerRadius, float outerRadius, int segments = 24)
    {
        var vertices = new VertexPositionNormal[(segments + 1) * 2];
        var indices = new ushort[segments * 6];

        for (int i = 0; i <= segments; i++)
        {
            float angle = (MathF.Tau * i) / segments;
            float cos = MathF.Cos(angle);
            float sin = MathF.Sin(angle);

            vertices[i * 2] = new VertexPositionNormal(
                new Vector3(cos * innerRadius, sin * innerRadius, 0f), Vector3.UnitZ);

            vertices[(i * 2) + 1] = new VertexPositionNormal(
                new Vector3(cos * outerRadius, sin * outerRadius, 0f), Vector3.UnitZ);
        }

        for (int i = 0; i < segments; i++)
        {
            ushort inner = (ushort)(i * 2);
            ushort outer = (ushort)((i * 2) + 1);
            ushort nextInner = (ushort)((i + 1) * 2);
            ushort nextOuter = (ushort)(((i + 1) * 2) + 1);

            indices[i * 6] = inner;
            indices[(i * 6) + 1] = outer;
            indices[(i * 6) + 2] = nextOuter;

            indices[(i * 6) + 3] = inner;
            indices[(i * 6) + 4] = nextOuter;
            indices[(i * 6) + 5] = nextInner;
        }

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
