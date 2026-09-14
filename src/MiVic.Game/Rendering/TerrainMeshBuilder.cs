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

    /// <summary>
    /// Darker and greener than the grass it grows on: a wood has to read as a place on
    /// the map, and a shade that only just differs from open ground reads as a smudge.
    /// </summary>
    private static readonly Color Forest = new(40, 66, 38);

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
        TerrainType.Forest => Forest,
        _ => Grass,
    };

    /// <summary>
    /// One cell's hue with the vertex's own slope and altitude treatment applied: the
    /// treatment is asked of the cell, the blend is asked of the picture.
    /// </summary>
    private static Color TreatedSurfaceColor(TerrainType type, float slopeFraction, float heightFraction)
    {
        Color color = SurfaceColor(type);

        if (type is TerrainType.Grass or TerrainType.Rock)
        {
            color = Color.Lerp(color, Rock, Math.Clamp(slopeFraction * 1.15f, 0f, 1f));
            color = Color.Lerp(color, Snow, Math.Clamp((heightFraction - 0.70f) / 0.30f, 0f, 1f));
        }

        return color;
    }

    /// <summary>
    /// The hue a vertex takes: its four surrounding surface cells' own treated colours,
    /// blended with smoothstep bilinear weights.
    /// <para>
    /// The weights come from the vertex's fractional position inside the cell it stands in,
    /// run through smoothstep so the blend is flat at a cell's centre and steep across its
    /// border — a ramp that keeps each cell reading as itself in the middle and hands the
    /// transition over across the third of the cell nearest the edge. Without the curve the
    /// blend is a linear smear that reaches every cell's centre and the surfaces stop
    /// reading as fields at all.
    /// </para>
    /// <para>
    /// Water is not blended away: a vertex that stands in a water cell flattens to the water
    /// line as before, and its hue blends into the shore beside it — which is what a beach
    /// is. A null layer (the pre-surface fallback) answers the plain grass.
    /// </para>
    /// </summary>
    private static Color BlendedSurfaceColor(
        TerrainLayer? terrain,
        float fractionX,
        float fractionZ,
        int cellX,
        int cellZ,
        float slopeFraction,
        float heightFraction)
    {
        if (terrain is null)
        {
            return TreatedSurfaceColor(TerrainType.Grass, slopeFraction, heightFraction);
        }

        int nextX = Math.Min(cellX + 1, terrain.Size - 1);
        int nextZ = Math.Min(cellZ + 1, terrain.Size - 1);

        // The ramp is centred on the border between the cells, and the smoothstep curve
        // gives it flat shoulders: a vertex in the leading half of its cell takes its own
        // cell's colour pure, the transition happens across the quarter either side of the
        // border, and the corner between four cells rounds diagonally instead of stepping
        // square. Run through plain and the blend is a linear smear that reaches every
        // cell's centre, and the surfaces stop reading as fields.
        const float RampHalfWidth = 0.25f;
        float weightX = Smoothstep((fractionX - (0.5f - RampHalfWidth)) / (2f * RampHalfWidth));
        float weightZ = Smoothstep((fractionZ - (0.5f - RampHalfWidth)) / (2f * RampHalfWidth));

        Color xz = TreatedSurfaceColor(terrain.TypeAtCell(cellX, cellZ), slopeFraction, heightFraction);
        Color x1z = TreatedSurfaceColor(terrain.TypeAtCell(nextX, cellZ), slopeFraction, heightFraction);
        Color xz1 = TreatedSurfaceColor(terrain.TypeAtCell(cellX, nextZ), slopeFraction, heightFraction);
        Color x1z1 = TreatedSurfaceColor(terrain.TypeAtCell(nextX, nextZ), slopeFraction, heightFraction);

        Color xRow = Color.Lerp(xz, x1z, weightX);
        Color nextRow = Color.Lerp(xz1, x1z1, weightX);

        return Color.Lerp(xRow, nextRow, weightZ);
    }

    /// <summary>
    /// The water's hue at one corner, read off the bed the corner stands over: shallows at
    /// the water line, the deep blue at the classifier's own depth threshold, and a linear
    /// hand-over between the two. The height field is smooth, so the contour the gradient
    /// follows is the bed's — the same reason the lake's edge against the land is organic
    /// while the band's inner edge was a lattice line.
    /// </summary>
    private static Color WaterColorByDepth(HeightMap map, TerrainLayer terrain, int worldX, int worldZ)
    {
        int depth = Math.Max(0, terrain.WaterLevelMm - map.SampleHeightMm(worldX, worldZ));
        float weight = Math.Clamp(depth / (float)TerrainLayer.DeepWaterDepthMm, 0f, 1f);

        return Color.Lerp(SurfaceColor(TerrainType.ShallowWater), SurfaceColor(TerrainType.DeepWater), weight);
    }

    /// <summary>Hermite's smoothstep, clamped: the S-curve the ramp is drawn with.</summary>
    private static float Smoothstep(float t)
    {
        float clamped = Math.Clamp(t, 0f, 1f);

        return (clamped * clamped) * (3f - (2f * clamped));
    }

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

                // The quad's corners take the blended hue at their own lattice corner, so
                // two quads that share a corner agree about the colour there. Water takes
                // its hue from the bed's own depth at the corner — the classifier's
                // threshold, applied continuously — so the pale shallows follow the
                // terrain's contours instead of the band's edges, and the deck a span lays
                // over deep water stops reading as a pale stripe under it. Lava has no
                // depth to read, and takes the same-liquid average.
                Color c00, c10, c11, c01;

                int x0Mm = terrain.OriginMm + (x * terrain.CellSizeMm);
                int z0Mm = terrain.OriginMm + (z * terrain.CellSizeMm);
                int x1Mm = x0Mm + terrain.CellSizeMm;
                int z1Mm = z0Mm + terrain.CellSizeMm;

                if (isWater)
                {
                    c00 = WaterColorByDepth(map, terrain, x0Mm, z0Mm);
                    c10 = WaterColorByDepth(map, terrain, x1Mm, z0Mm);
                    c11 = WaterColorByDepth(map, terrain, x1Mm, z1Mm);
                    c01 = WaterColorByDepth(map, terrain, x0Mm, z1Mm);
                }
                else
                {
                    c00 = LiquidCornerColor(terrain, x, z, surface);
                    c10 = LiquidCornerColor(terrain, x + 1, z, surface);
                    c11 = LiquidCornerColor(terrain, x + 1, z + 1, surface);
                    c01 = LiquidCornerColor(terrain, x, z + 1, surface);
                }

                List<VertexPositionNormal> vertices = isWater ? waterVertices : lavaVertices;
                List<ushort> indices = isWater ? waterIndices : lavaIndices;

                AppendLiquidQuad(vertices, indices, x0, z0, cell, y, c00, c10, c11, c01);
            }
        }

        return (
            new MeshData([.. waterVertices], [.. waterIndices]),
            new MeshData([.. lavaVertices], [.. lavaIndices]));
    }

    /// <summary>
    /// One liquid cell: a flat quad wound to face upwards, matching the terrain's own
    /// winding — get this backwards and the whole surface is culled and invisible.
    /// <para>
    /// It was backwards, and the whole surface was invisible. The quad's four corners are
    /// added in grid order — the same order <see cref="FromHeightMap"/> adds them — so it has
    /// to carry the same two triangles the terrain does, <c>(0, 2, 3)</c> and <c>(0, 1, 2)</c>.
    /// It carried <c>(0, 3, 2)</c> and <c>(0, 2, 1)</c> instead, which is the same quad wound
    /// the other way, and every water cell in the game was discarded by the cull stage before
    /// it reached the shader. The lake still looked like a lake, because the terrain mesh
    /// paints water cells in the water palette and draws them at the water line — which is
    /// exactly why a culled surface went unnoticed: what was missing was the movement, the
    /// grazing reflection and the sun glint, and a flat blue lake is not obviously wrong.
    /// </para>
    /// <para>
    /// The four colours are the blend at each corner, not one flat hue: one colour per quad
    /// is what made every shallow patch a pale square with a hard edge, once the bed under
    /// it began to blend.
    /// </para>
    /// </summary>
    private static void AppendLiquidQuad(
        List<VertexPositionNormal> vertices,
        List<ushort> indices,
        float x0,
        float z0,
        float cell,
        float y,
        Color c00,
        Color c10,
        Color c11,
        Color c01)
    {
        var index = (ushort)vertices.Count;

        vertices.Add(new VertexPositionNormal(new Vector3(x0, y, z0), Vector3.Up, c00));
        vertices.Add(new VertexPositionNormal(new Vector3(x0 + cell, y, z0), Vector3.Up, c10));
        vertices.Add(new VertexPositionNormal(new Vector3(x0 + cell, y, z0 + cell), Vector3.Up, c11));
        vertices.Add(new VertexPositionNormal(new Vector3(x0, y, z0 + cell), Vector3.Up, c01));

        indices.Add(index);
        indices.Add((ushort)(index + 2));
        indices.Add((ushort)(index + 3));

        indices.Add(index);
        indices.Add((ushort)(index + 1));
        indices.Add((ushort)(index + 2));
    }

    /// <summary>
    /// The blended hue at one lattice corner of the liquid mesh: the four cells that touch
    /// the corner, averaged. A corner is the mid-point between four cell centres in the
    /// blend's own parameterisation, so the weights are quarters — and because two quads
    /// that share a corner compute the same four cells there, the colour is continuous
    /// across the mesh and no seam can show.
    /// </summary>
    /// <summary>
    /// The blended hue at one lattice corner of the liquid mesh: the liquid cells that
    /// touch the corner, averaged. A corner is the mid-point between four cell centres in
    /// the blend's own parameterisation, so the touching cells weigh equally — and because
    /// two quads that share a corner compute the same set there, the colour is continuous
    /// across the mesh and no seam can show.
    /// <para>
    /// Only cells of the quad's own liquid count: land hues averaged into a water corner
    /// turned every shoreline milky, a pale wash that reads as fog rather than as shallows.
    /// The beach the eye expects at a water's edge is the bed's own blend showing at the
    /// places the liquid does not cover, not the water painted sand.
    /// </para>
    /// </summary>
    private static Color LiquidCornerColor(TerrainLayer terrain, int cornerX, int cornerZ, TerrainType liquid)
    {
        int fromX = Math.Max(0, cornerX - 1);
        int fromZ = Math.Max(0, cornerZ - 1);
        int toX = Math.Min(cornerX, terrain.Size - 1);
        int toZ = Math.Min(cornerZ, terrain.Size - 1);

        int r = 0;
        int g = 0;
        int b = 0;
        int count = 0;

        for (int cz = fromZ; cz <= toZ; cz++)
        {
            for (int cx = fromX; cx <= toX; cx++)
            {
                TerrainType type = terrain.TypeAtCell(cx, cz);

                bool sameLiquid = liquid == TerrainType.Lava
                    ? type == TerrainType.Lava
                    : type is TerrainType.ShallowWater or TerrainType.DeepWater;

                if (!sameLiquid)
                {
                    continue;
                }

                Color color = SurfaceColor(type);
                r += color.R;
                g += color.G;
                b += color.B;
                count++;
            }
        }

        if (count == 0)
        {
            return SurfaceColor(liquid);
        }

        return new Color(r / count, g / count, b / count);
    }

    /// <summary>
    /// A surface layer colours the ground and flattens water to the water line, so
    /// lakes read as lakes instead of as dark pits. Without one the mesh falls back
    /// to relief shading alone, which is what the terrain looked like before
    /// surfaces existed.
    /// <para>
    /// The surface hue is <b>blended, not taken</b>. Each vertex sits at a fractional
    /// position inside the surface lattice — the layer runs at navigation pitch, twice
    /// the mesh's vertex spacing — and the exact-cell read gave every transition an
    /// axis-aligned staircase: mud ending in a square cliff against grass, a shore
    /// that was a checker's edge. The four cells around a vertex are blended with
    /// smoothstep weights instead, which turns each border into an S-curved ramp about
    /// half a cell wide and lets the line between two surfaces bend diagonally
    /// wherever the lattice allows it. The simulation still reads the exact cell — this
    /// is a colour, and the ground a unit walks is what the layer says — but the picture
    /// of it is no longer a picture of the lattice.
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
                int cellX = 0;
                int cellZ = 0;

                if (terrain is not null)
                {
                    cellX = Math.Min(x / stride, terrain.Size - 1);
                    cellZ = Math.Min(z / stride, terrain.Size - 1);
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

                // The blended hue of the four cells around the vertex, weighted by the
                // vertex's own fractional position inside the lattice.
                Color color = BlendedSurfaceColor(
                    terrain,
                    stride <= 1 ? 0f : (x % stride) / (float)stride,
                    stride <= 1 ? 0f : (z % stride) / (float)stride,
                    cellX,
                    cellZ,
                    slopeFraction,
                    heightFraction);

                // The surface decides the hue; slope and altitude decide the shade,
                // so the ground still reads as terrain rather than as flat colour
                // swatches.
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
