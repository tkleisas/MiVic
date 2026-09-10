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
    /// <summary>
    /// Builds the two animated liquid surfaces: water and lava.
    /// <para>
    /// Separate meshes rather than a flag on the terrain vertices, because the terrain
    /// vertex alpha is already the faction paint mask and borrowing it would let a
    /// team's colour bleed into the sea. Each is a flat quad over every cell of that
    /// surface, a few centimetres above the ground so it does not fight the terrain
    /// for the depth buffer — a distance nobody can see at the scale the game is
    /// played at, and the alternative is a stipple of dropped pixels.
    /// </para>
    /// <para>
    /// Returns empty meshes when there is no liquid at all, which is most maps.
    /// </para>
    /// </summary>
    public static (MeshData Water, MeshData Lava) BuildLiquids(HeightMap map, TerrainLayer terrain)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(terrain);

        var waterVertices = new List<VertexPositionNormal>();
        var waterIndices = new List<ushort>();
        var lavaVertices = new List<VertexPositionNormal>();
        var lavaIndices = new List<ushort>();

        int size = terrain.Size;
        float cell = terrain.CellSizeMm / (float)WorldPos.MmPerMetre;
        int stride = Math.Max(1, terrain.CellSizeMm / map.CellSizeMm);

        // Enough to clear the terrain's own water triangles without being visible.
        const float Lift = 0.05f;

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                TerrainType surface = terrain.TypeAtCell(x, z);

                bool isWater = surface is TerrainType.ShallowWater or TerrainType.DeepWater;
                bool isLava = surface is TerrainType.Lava;

                if (!isWater && !isLava)
                {
                    continue;
                }

                // Water sits on the water line rather than on the bed it fills; lava
                // sits on the ground it flowed over, which is a height the surface
                // layer does not carry.
                int heightMm;

                if (isWater)
                {
                    heightMm = terrain.WaterLevelMm;
                }
                else
                {
                    int mapX = Math.Min((x * stride) + (stride / 2), map.Size - 1);
                    int mapZ = Math.Min((z * stride) + (stride / 2), map.Size - 1);

                    heightMm = map.HeightAt(mapX, mapZ);
                }

                float y = (heightMm / (float)WorldPos.MmPerMetre) + Lift;
                float x0 = (terrain.OriginMm + (x * terrain.CellSizeMm)) / (float)WorldPos.MmPerMetre;
                float z0 = (terrain.OriginMm + (z * terrain.CellSizeMm)) / (float)WorldPos.MmPerMetre;

                Color color = SurfaceColor(surface);

                List<VertexPositionNormal> vertices = isWater ? waterVertices : lavaVertices;
                List<ushort> indices = isWater ? waterIndices : lavaIndices;

                AppendLiquidQuad(vertices, indices, x0, z0, cell, y, color);
            }
        }

        return (
            new MeshData([.. waterVertices], [.. waterIndices]),
            new MeshData([.. lavaVertices], [.. lavaIndices]));
    }

    /// <summary>
    /// One liquid cell: a flat quad wound to face upwards, matching the terrain's own
    /// winding — get this backwards and the whole surface is culled and invisible.
    /// </summary>
    private static void AppendLiquidQuad(
        List<VertexPositionNormal> vertices,
        List<ushort> indices,
        float x0,
        float z0,
        float cell,
        float y,
        Color color)
    {
        var index = (ushort)vertices.Count;

        vertices.Add(new VertexPositionNormal(new Vector3(x0, y, z0), Vector3.Up, color));
        vertices.Add(new VertexPositionNormal(new Vector3(x0 + cell, y, z0), Vector3.Up, color));
        vertices.Add(new VertexPositionNormal(new Vector3(x0 + cell, y, z0 + cell), Vector3.Up, color));
        vertices.Add(new VertexPositionNormal(new Vector3(x0, y, z0 + cell), Vector3.Up, color));

        indices.Add(index);
        indices.Add((ushort)(index + 3));
        indices.Add((ushort)(index + 2));

        indices.Add(index);
        indices.Add((ushort)(index + 2));
        indices.Add((ushort)(index + 1));
    }

    /// <summary>
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
                int terrainCell = -1;

                if (terrain is not null)
                {
                    int cellX = Math.Min(x / stride, terrain.Size - 1);
                    int cellZ = Math.Min(z / stride, terrain.Size - 1);
                    surface = terrain.TypeAtCell(cellX, cellZ);
                    terrainCell = (cellZ * terrain.Size) + cellX;

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

                // Worn ground reads as worn ground: the route an army has driven
                // over darkens towards mud, so a player can see why their column
                // slowed down instead of guessing.
                if (terrain is not null && terrainCell >= 0)
                {
                    int churn = terrain.ChurnAt(terrainCell);

                    if (churn > 0)
                    {
                        color = Color.Lerp(color, Mud, Math.Clamp(churn / 255f, 0f, 0.85f));
                    }
                }

                // Alpha 0 is the faction paint mask saying "no faction tint": the
                // ground carries its own colours and must not be repainted by
                // whichever team happens to be drawing it.
                vertices[(z * size) + x] = new VertexPositionNormal(
                    position,
                    normal,
                    new Color(color.R, color.G, color.B, (byte)0));
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
