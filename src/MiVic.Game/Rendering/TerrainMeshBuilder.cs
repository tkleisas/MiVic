using MiVic.Core.Numerics;
using MiVic.Core.Terrain;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering;

/// <summary>
/// Turns the simulation's height field into a renderable mesh.
/// <para>
/// The client never generates terrain of its own: it meshes exactly the
/// <see cref="HeightMap"/> the simulation uses, so what the player sees is what
/// pathfinding reasons about. Colour is baked per vertex from height and slope,
/// which keeps the terrain on the same single-shader path as everything else.
/// </para>
/// </summary>
public static class TerrainMeshBuilder
{
    private static readonly Color Grass = new(74, 98, 56);
    private static readonly Color Rock = new(112, 106, 94);
    private static readonly Color Snow = new(228, 232, 238);

    /// <summary>Builds a mesh from a height map, in metres.</summary>
    public static MeshData FromHeightMap(HeightMap map)
    {
        ArgumentNullException.ThrowIfNull(map);

        int size = map.Size;

        VertexPositionNormal[] vertices = new VertexPositionNormal[size * size];

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int height = map.HeightAt(x, z);

                Vector3 position = new(
                    (map.OriginMm + (x * map.CellSizeMm)) / (float)WorldPos.MmPerMetre,
                    height / (float)WorldPos.MmPerMetre,
                    (map.OriginMm + (z * map.CellSizeMm)) / (float)WorldPos.MmPerMetre);

                // Central differences give a smooth normal without a second pass.
                float dx = (map.HeightAt(x - 1, z) - map.HeightAt(x + 1, z)) / (2f * map.CellSizeMm);
                float dz = (map.HeightAt(x, z - 1) - map.HeightAt(x, z + 1)) / (2f * map.CellSizeMm);

                Vector3 normal = Vector3.Normalize(new Vector3(dx, 1f, dz));

                float heightFraction = height / (float)map.MaxHeightMm;
                float slopeFraction = Math.Clamp(map.SlopePermille(x, z) / 900f, 0f, 1f);

                Color color = Color.Lerp(Grass, Rock, Math.Clamp(slopeFraction * 1.15f, 0f, 1f));
                color = Color.Lerp(color, Snow, Math.Clamp((heightFraction - 0.70f) / 0.30f, 0f, 1f));

                vertices[(z * size) + x] = new VertexPositionNormal(position, normal, color);
            }
        }

        ushort[] indices = new ushort[(size - 1) * (size - 1) * 6];
        int index = 0;

        for (int z = 0; z < size - 1; z++)
        {
            for (int x = 0; x < size - 1; x++)
            {
                ushort v = (ushort)((z * size) + x);

                // Counter-clockwise when viewed from above, matching every other
                // mesh: the reverse winding is back-facing and gets culled, which
                // makes the entire terrain silently disappear.
                indices[index++] = v;
                indices[index++] = (ushort)(v + size + 1);
                indices[index++] = (ushort)(v + size);

                indices[index++] = v;
                indices[index++] = (ushort)(v + 1);
                indices[index++] = (ushort)(v + size + 1);
            }
        }

        return new MeshData(vertices, indices);
    }
}
