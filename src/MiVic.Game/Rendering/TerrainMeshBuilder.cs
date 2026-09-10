using MiVic.Core.Numerics;
using MiVic.Core.Terrain;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering;

/// <summary>
/// Turns the simulation's height field into a renderable mesh.
/// <para>
/// The client never generates terrain of its own: it meshes exactly the
/// <see cref="HeightMap"/> the simulation uses, so what the player sees is what
/// pathfinding reasons about. Colour is baked per vertex from the simulation's own
/// <see cref="TerrainLayer"/>, so mud the player can see is mud the pathfinder
/// charges for — including mud that weather control has just created.
/// </para>
/// </summary>
public static class TerrainMeshBuilder
{
    private static readonly Color Grass = new(74, 98, 56);
    private static readonly Color Mud = new(84, 62, 42);
    private static readonly Color Sand = new(198, 180, 126);
    private static readonly Color Snow = new(228, 232, 238);
    private static readonly Color Rock = new(112, 106, 94);
    private static readonly Color ShallowWater = new(76, 122, 146);
    private static readonly Color DeepWater = new(38, 68, 108);
    private static readonly Color Lava = new(196, 74, 28);
    private static readonly Color Mine = new(92, 88, 82);

    /// <summary>Colour of a surface as it appears on the ground.</summary>
    public static Color SurfaceColor(TerrainType type) => type switch
    {
        TerrainType.Mud => Mud,
        TerrainType.Sand => Sand,
        TerrainType.Snow => Snow,
        TerrainType.Rock => Rock,
        TerrainType.ShallowWater => ShallowWater,
        TerrainType.DeepWater => DeepWater,
        TerrainType.Lava => Lava,
        TerrainType.Mine => Mine,
        _ => Grass,
    };

    /// <summary>
    /// Builds a mesh from a height map, in metres.
    /// <para>
    /// A surface layer colours the ground and flattens water to the water line, so
    /// lakes read as lakes instead of as dark pits. Without one the mesh falls back
    /// to relief shading alone, which is what the terrain looked like before
    /// surfaces existed.
    /// </para>
    /// </summary>
    public static MeshData FromHeightMap(HeightMap map, TerrainLayer? terrain = null)
    {
        ArgumentNullException.ThrowIfNull(map);

        int size = map.Size;
        int stride = terrain is null ? 1 : Math.Max(1, terrain.CellSizeMm / map.CellSizeMm);

        VertexPositionNormal[] vertices = new VertexPositionNormal[size * size];

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int height = map.HeightAt(x, z);
                TerrainType surface = TerrainType.Grass;

                if (terrain is not null)
                {
                    int cellX = Math.Min(x / stride, terrain.Size - 1);
                    int cellZ = Math.Min(z / stride, terrain.Size - 1);
                    surface = terrain.TypeAtCell(cellX, cellZ);

                    // Water is drawn at the water line, not at the bottom of the
                    // basin it fills.
                    if (surface is TerrainType.ShallowWater or TerrainType.DeepWater)
                    {
                        height = terrain.WaterLevelMm;
                    }
                }

                Vector3 position = new(
                    (map.OriginMm + (x * map.CellSizeMm)) / (float)WorldPos.MmPerMetre,
                    height / (float)WorldPos.MmPerMetre,
                    (map.OriginMm + (z * map.CellSizeMm)) / (float)WorldPos.MmPerMetre);

                // Central differences give a smooth normal without a second pass.
                float dx = (map.HeightAt(x - 1, z) - map.HeightAt(x + 1, z)) / (2f * map.CellSizeMm);
                float dz = (map.HeightAt(x, z - 1) - map.HeightAt(x, z + 1)) / (2f * map.CellSizeMm);

                Vector3 normal = Vector3.Normalize(new Vector3(dx, 1f, dz));

                float slopeFraction = Math.Clamp(map.SlopePermille(x, z) / 900f, 0f, 1f);
                float heightFraction = height / (float)Math.Max(1, map.MaxHeightMm);

                // The surface decides the hue; slope and altitude decide the shade,
                // so the ground still reads as terrain rather than as flat colour
                // swatches.
                Color color = SurfaceColor(surface);

                if (surface is TerrainType.Grass or TerrainType.Rock)
                {
                    color = Color.Lerp(color, Rock, Math.Clamp(slopeFraction * 1.15f, 0f, 1f));
                    color = Color.Lerp(color, Snow, Math.Clamp((heightFraction - 0.70f) / 0.30f, 0f, 1f));
                }

                float shade = 1f - (slopeFraction * 0.22f);

                color = new Color(
                    (int)(color.R * shade),
                    (int)(color.G * shade),
                    (int)(color.B * shade));

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
