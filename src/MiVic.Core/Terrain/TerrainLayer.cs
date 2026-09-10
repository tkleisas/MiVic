using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;

namespace MiVic.Core.Terrain;

/// <summary>
/// Surface types and their movement costs, one byte per navigation cell.
/// <para>
/// The layer is a pure function of the height field and the world seed, so it is
/// reproducible without being hashed: two machines with the same seed generate the
/// same mud in the same place. Costs are integer permille of the flat-ground cost,
/// with zero meaning impassable, which is what lets the existing cost-driven A*
/// pick terrain up without a single change to its algorithm.
/// </para>
/// <para>
/// Mud and snow are further scaled by the mover's <em>ground pressure</em>, which
/// is where the faction asymmetry becomes physical: a light hull on wide tracks
/// barely notices the *rasputitsa*, and a heavy one bogs down in it.
/// </para>
/// </summary>
public sealed class TerrainLayer
{
    /// <summary>Cost of flat, open ground, in permille. Everything else is relative to this.</summary>
    public const int BasePermille = 100;

    /// <summary>Slope, in millimetres per metre, above which ground becomes bare rock.</summary>
    public const int RockSlopePermille = 700;

    private readonly byte[] _types;
    private readonly byte[] _original;
    private readonly int[] _weatherExpiry;
    private readonly byte[] _churn;

    /// <summary>
    /// The attribute word per cell — canopy density, moisture, aspect, landform, fuel
    /// and the damage flags.
    /// <para>
    /// A second word rather than a second layer, and the arithmetic is the reason: 65×65
    /// words is about 17 KB, which is nothing next to the entity array, and it buys
    /// attributes that cannot get out of step with the cell they describe.
    /// </para>
    /// </summary>
    private readonly uint[] _attributes;

    private int _weatherCells;
    private int _churnedCells;

    private TerrainLayer(
        int size,
        int cellSizeMm,
        int originMm,
        int waterLevelMm,
        int maxHeightMm,
        byte[] types,
        uint[] attributes)
    {
        Size = size;
        CellSizeMm = cellSizeMm;
        OriginMm = originMm;
        WaterLevelMm = waterLevelMm;
        MaxHeightMm = maxHeightMm;
        _types = types;
        _attributes = attributes;
        _original = new byte[types.Length];
        _weatherExpiry = new int[types.Length];
        _churn = new byte[types.Length];
    }

    /// <summary>Churn above which ground counts as deep mud.</summary>
    public const int DeepChurn = 160;

    /// <summary>Most a fully churned cell can add to its own cost, in permille.</summary>
    public const int MaxChurnSurcharge = 600;

    /// <summary>Cells per side of the lattice used to cluster mineral deposits.</summary>
    private const int DepositCluster = 3;

    /// <summary>One in this many deposit clusters carries ore, in permille.</summary>
    private const int DepositFrequencyPermille = 70;

    /// <summary>Radius of a volcano's lava pool, in cells.</summary>
    private const int VolcanoRadius = 2;

    /// <summary>Cells per side; matches the navigation grid.</summary>
    public int Size { get; }

    /// <summary>Cell size in millimetres.</summary>
    public int CellSizeMm { get; }

    /// <summary>World coordinate of cell zero, in millimetres.</summary>
    public int OriginMm { get; }

    /// <summary>Height below which a cell holds water, in millimetres.</summary>
    public int WaterLevelMm { get; }

    /// <summary>Highest terrain height, in millimetres.</summary>
    public int MaxHeightMm { get; }

    /// <summary>Total cell count.</summary>
    public int CellCount => Size * Size;

    /// <summary>
    /// Bumped on every surface change. The client rebuilds its terrain mesh when
    /// this moves, so weather control is visible without meshing the ground every
    /// frame.
    /// </summary>
    public int Revision { get; private set; }

    /// <summary>
    /// Bumped on every change to ground wear. Separate from <see cref="Revision"/>
    /// because churn changes every tick an army moves, and re-meshing the ground at
    /// that rate would cost more than it shows: the client throttles this one.
    /// </summary>
    public int ChurnRevision { get; private set; }

    /// <summary>
    /// Bumped on every change to the attributes. Separate from <see cref="Revision"/>
    /// and <see cref="ChurnRevision"/> because it moves for its own reasons: a wood
    /// thinning from a fire, tracks crushing a clearing, or a field regrowing.
    /// </summary>
    public int AttributeRevision { get; private set; }

    /// <summary>
    /// Generates the layer for a height field, aligned to a navigation grid so the
    /// two share one lattice.
    /// </summary>
    /// <param name="map">Source height field.</param>
    /// <param name="grid">Navigation grid built from the same map.</param>
    public static TerrainLayer Build(HeightMap map, NavGrid grid, ulong seed = 0)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(grid);

        int size = grid.Size;
        int stride = Math.Max(1, grid.CellSizeMm / map.CellSizeMm);

        // The bands are cut from the map's *relief* — its highest point above its
        // lowest — rather than from its highest value, and they are cut in order, each
        // one against the one below it.
        //
        // Both of those matter, and both were wrong. Tied to the peak's absolute value,
        // a band is a fraction of a range the terrain may only occupy the bottom of,
        // and it can come out empty: the volcano line once sat above every square of
        // ground, and sand was worse. Its band was cut between the mud line and a sand
        // line that was *lower* than the mud line, and since the mud test is taken
        // first, no cell could ever be sand — on any seed, on any map, with a movement
        // cost, a cover value, a colour and a shader treatment all built for it. Twenty
        // four seeds were checked: zero sand cells, every one.
        int lowest = int.MaxValue;
        int highest = 0;

        for (int index = 0; index < size * size; index++)
        {
            int sample = grid.HeightAt(index);

            lowest = Math.Min(lowest, sample);
            highest = Math.Max(highest, sample);
        }

        int relief = Math.Max(1, highest - lowest);
        int waterLevel = map.MaxHeightMm / 12;

        // Each band is a meaningful slice of the relief, and each is cut above the last,
        // so every one of these surfaces can actually appear.
        int mudLine = waterLevel + Math.Max(500, relief / 20);
        int sandLine = mudLine + Math.Max(500, relief / 8);
        int snowLine = highest - Math.Max(1_000, relief / 5);

        byte[] types = new byte[size * size];

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (z * size) + x;
                int height = grid.HeightAt(index);
                int sampleX = Math.Min(x * stride, map.Size - 1);
                int sampleZ = Math.Min(z * stride, map.Size - 1);
                int slope = map.SlopePermille(sampleX, sampleZ);

                types[index] = (byte)Classify(height, slope, waterLevel, mudLine, sandLine, snowLine);
            }
        }

        // Volcanoes go in before connectivity, not after: they place impassable
        // lava, and anything impassable added after the ford pass silently breaks
        // the guarantee that every patch of ground can be reached. A* then spends
        // its whole expansion budget on goals that cannot be reached.
        RaiseVolcanoes(types, size, grid, seed);
        ScatterForests(types, size, grid, seed);
        EnsureGroundConnectivity(types, size, grid);
        ScatterDeposits(types, size, grid, seed);

        // Attributes come last, once every pass that can rewrite a surface has run:
        // vegetation is read off the surface, so a grove planted afterwards would be a
        // wood with a bare floor and a ford carved afterwards would be a lake with
        // grass in it.
        uint[] attributes = GenerateAttributes(types, size, grid, map, stride, waterLevel, relief, seed);

        // Aspect and landform are the two fields that are not facts about the cell: they
        // are facts about the cell *and its neighbours*, and they are read off the height
        // field rather than off the surface, so they are a pass of their own.
        GenerateShape(attributes, size, map, stride);

        return new TerrainLayer(
            size,
            grid.CellSizeMm,
            grid.OriginMm,
            waterLevel,
            map.MaxHeightMm,
            types,
            attributes);
    }

    /// <summary>
    /// Plants woodland in clusters on open ground.
    /// <para>
    /// Clustered rather than sprinkled, and that is the whole point: woods are places,
    /// not noise. A forest that is a scatter of single cells gives no cover anywhere
    /// worth standing and no obstacle anywhere worth avoiding — it is a texture. A wood
    /// big enough to hide a squad in is a decision on the map.
    /// </para>
    /// <para>
    /// Only on grass, and only below the snow line, so forests appear on the ground a
    /// player would expect trees on rather than on sand dunes and mountainsides.
    /// </para>
    /// </summary>
    private static void ScatterForests(byte[] types, int size, NavGrid grid, ulong seed)
    {
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (z * size) + x;

                if (types[index] != (byte)TerrainType.Grass || !grid.IsWalkable(index))
                {
                    continue;
                }

                // The cluster test decides where a grove is seeded; the radius below
                // decides how big it grows.
                int cluster = Hash(x / ForestCluster, z / ForestCluster, seed ^ 0xF0_4E_57);

                if ((int)(cluster % 1_000) >= ForestFrequencyPermille)
                {
                    continue;
                }

                for (int dz = -ForestRadius; dz <= ForestRadius; dz++)
                {
                    for (int dx = -ForestRadius; dx <= ForestRadius; dx++)
                    {
                        int nx = x + dx;
                        int nz = z + dz;

                        if ((uint)nx >= (uint)size || (uint)nz >= (uint)size)
                        {
                            continue;
                        }

                        // A ragged edge rather than a circle: trees thin out at the
                        // treeline, and a wood with a squared-off border looks drawn.
                        int distance = (dx * dx) + (dz * dz);

                        if (distance > (ForestRadius * ForestRadius) ||
                            (distance > 1 && Hash(nx, nz, seed ^ 0x7E_EE) % 3 == 0))
                        {
                            continue;
                        }

                        int cell = (nz * size) + nx;

                        if (types[cell] == (byte)TerrainType.Grass && grid.IsWalkable(cell))
                        {
                            types[cell] = (byte)TerrainType.Forest;
                        }
                    }
                }
            }
        }
    }

    /// <summary>How many cells across a forest cluster is judged, and how big it grows.</summary>
    private const int ForestCluster = 3;

    private const int ForestRadius = 2;

    /// <summary>How often a cluster becomes a wood, in permille of clusters.</summary>
    private const int ForestFrequencyPermille = 260;

    /// <summary>
    /// Scatters mineral deposits over dry, passable ground.
    /// <para>
    /// Clustered rather than uniform: a deposit is a place worth fighting over, so
    /// it has to be big enough to find and defend. Deterministic from the seed, and
    /// placed last so a deposit can never land in a lake or on a cliff.
    /// </para>
    /// </summary>
    private static void ScatterDeposits(byte[] types, int size, NavGrid grid, ulong seed)
    {
        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (z * size) + x;
                TerrainType type = (TerrainType)types[index];

                if (type is not (TerrainType.Grass or TerrainType.Sand or TerrainType.Snow) ||
                    !grid.IsWalkable(index))
                {
                    continue;
                }

                int cluster = Hash(x / DepositCluster, z / DepositCluster, seed);

                if ((int)(cluster % 1_000) < DepositFrequencyPermille)
                {
                    types[index] = (byte)TerrainType.Mine;
                }
            }
        }
    }

    /// <summary>
    /// Raises volcanic cones on the highest ground: rock on the slopes, lava in the
    /// crater. The lava is impassable and burns whatever is standing on it, which is
    /// what makes a volcano terrain rather than scenery.
    /// </summary>
    private static void RaiseVolcanoes(byte[] types, int size, NavGrid grid, ulong seed)
    {
        // The volcano line is a fraction of the map's *relief* — its highest point
        // above its lowest — rather than of its highest value.
        //
        // Those are the same number only when the terrain starts at zero. Measured
        // against the maximum instead, the band is the top twelfth of a range the
        // terrain may only occupy the bottom of, and on a map with gentle relief that
        // band can contain no cells at all. It did: a map with a volcano feature, lava
        // hazard damage and a lava surface shader had zero lava cells, because the
        // line the volcanoes were asked to clear sat above every square of ground.
        int lowest = int.MaxValue;
        int highest = 0;

        for (int index = 0; index < size * size; index++)
        {
            int height = grid.HeightAt(index);

            lowest = Math.Min(lowest, height);
            highest = Math.Max(highest, height);
        }

        if (highest <= lowest)
        {
            return;
        }

        int snowLine = highest - ((highest - lowest) / 5);

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (z * size) + x;

                // Rare enough that a map has a few volcanoes rather than a lava
                // field, and only on the high ground. One in thirteen of the cells
                // above the line: at one in twenty-three the roll came up empty often
                // enough that a map could have a single volcano or none, and a hazard
                // that appears once on one map in three is not a feature.
                if (grid.HeightAt(index) < snowLine || Hash(x, z, seed ^ 0x5EED) % 13 != 0)
                {
                    continue;
                }

                for (int dz = -VolcanoRadius; dz <= VolcanoRadius; dz++)
                {
                    for (int dx = -VolcanoRadius; dx <= VolcanoRadius; dx++)
                    {
                        int nx = x + dx;
                        int nz = z + dz;

                        if ((uint)nx >= (uint)size || (uint)nz >= (uint)size)
                        {
                            continue;
                        }

                        int distance = (dx * dx) + (dz * dz);
                        int cell = (nz * size) + nx;

                        if (distance <= 1)
                        {
                            types[cell] = (byte)TerrainType.Lava;
                        }
                        else if (distance <= VolcanoRadius * VolcanoRadius)
                        {
                            types[cell] = (byte)TerrainType.Rock;
                        }
                    }
                }
            }
        }
    }

    /// <summary>A stable integer hash of a lattice coordinate and the world seed.</summary>
    private static int Hash(int x, int z, ulong seed)
    {
        ulong value = seed + ((ulong)(uint)x * 0x9E3779B97F4A7C15UL) + ((ulong)(uint)z * 0xC2B2AE3D27D4EB4FUL);
        value ^= value >> 29;
        value *= 0xBF58476D1CE4E5B9UL;
        value ^= value >> 32;

        return (int)(value & 0x7FFFFFFF);
    }

    /// <summary>
    /// Carves fords so that every patch of ground a tracked vehicle can stand on is
    /// reachable from the largest one.
    /// <para>
    /// Deep water is impassable, which is the point — but a generator that floods
    /// 20 % of the map leaves units stranded on islands, and a unit that cannot path
    /// anywhere reads as a bug rather than as terrain. Bridges do not exist yet, so
    /// the generator guarantees a crossing instead: the narrowest water gap between
    /// two components is turned into shallow water, which is a ford.
    /// </para>
    /// <para>
    /// Deterministic: components are labelled in ascending cell order and ties are
    /// broken by index, so the same seed carves the same fords.
    /// </para>
    /// </summary>
    private static void EnsureGroundConnectivity(byte[] types, int size, NavGrid grid)
    {
        int count = size * size;
        int[] label = new int[count];
        int[] queue = new int[count];
        int[] componentSize = new int[count + 1];
        int[] nearestMain = new int[count];
        int[] distance = new int[count];

        // Each pass merges at least one component into the main one, so the loop is
        // bounded by the number of components.
        for (int pass = 0; pass < count; pass++)
        {
            Array.Clear(label, 0, count);
            Array.Clear(componentSize, 0, componentSize.Length);

            int components = Label(types, size, grid, label, queue, componentSize);

            if (components <= 1)
            {
                return;
            }

            int main = 1;

            for (int c = 2; c <= components; c++)
            {
                if (componentSize[c] > componentSize[main])
                {
                    main = c;
                }
            }

            // Multi-source BFS out of the main component, over every cell including
            // water, so each stranded cell knows its closest cell on the main body.
            Array.Fill(distance, -1);

            int head = 0;
            int tail = 0;

            for (int index = 0; index < count; index++)
            {
                if (label[index] == main)
                {
                    nearestMain[index] = index;
                    distance[index] = 0;
                    queue[tail++] = index;
                }
            }

            while (head < tail)
            {
                int cell = queue[head++];
                int cx = cell % size;
                int cz = cell / size;

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int nz = cz + (d == 2 ? 1 : d == 3 ? -1 : 0);

                    if ((uint)nx >= (uint)size || (uint)nz >= (uint)size)
                    {
                        continue;
                    }

                    int next = (nz * size) + nx;

                    if (distance[next] >= 0)
                    {
                        continue;
                    }

                    distance[next] = distance[cell] + 1;
                    nearestMain[next] = nearestMain[cell];
                    queue[tail++] = next;
                }
            }

            // Carve every stranded component's narrowest crossing in one go, not one
            // per pass: the flood fill is the expensive part, and a per-component
            // loop over components would make world construction quadratic.
            int[] bestCell = new int[components + 1];

            for (int c = 1; c <= components; c++)
            {
                bestCell[c] = -1;
            }

            for (int index = 0; index < count; index++)
            {
                int component = label[index];

                if (component == 0 || component == main)
                {
                    continue;
                }

                if (bestCell[component] < 0 || distance[index] < distance[bestCell[component]])
                {
                    bestCell[component] = index;
                }
            }

            bool carved = false;

            for (int c = 1; c <= components; c++)
            {
                if (c == main || bestCell[c] < 0)
                {
                    continue;
                }

                // A component walled off by cliffs rather than water cannot be
                // reached by carving a ford, and retrying it would spin until the
                // pass cap. Only a carve that actually changed something counts.
                carved |= CarveFord(types, size, bestCell[c], nearestMain[bestCell[c]]);
            }

            if (!carved)
            {
                return;
            }
        }
    }

    /// <summary>Labels connected ground components, four-connected.</summary>
    private static int Label(
        byte[] types,
        int size,
        NavGrid grid,
        int[] label,
        int[] queue,
        int[] componentSize)
    {
        int count = size * size;
        int components = 0;

        for (int seed = 0; seed < count; seed++)
        {
            if (label[seed] != 0 || !Ground(types, grid, seed))
            {
                continue;
            }

            components++;
            int head = 0;
            int tail = 0;
            queue[tail++] = seed;
            label[seed] = components;

            while (head < tail)
            {
                int cell = queue[head++];
                componentSize[components]++;

                int cx = cell % size;
                int cz = cell / size;

                for (int d = 0; d < 4; d++)
                {
                    int nx = cx + (d == 0 ? 1 : d == 1 ? -1 : 0);
                    int nz = cz + (d == 2 ? 1 : d == 3 ? -1 : 0);

                    if ((uint)nx >= (uint)size || (uint)nz >= (uint)size)
                    {
                        continue;
                    }

                    int next = (nz * size) + nx;

                    if (label[next] != 0 || !Ground(types, grid, next))
                    {
                        continue;
                    }

                    label[next] = components;
                    queue[tail++] = next;
                }
            }
        }

        return components;
    }

    /// <summary>True when a cell is ground a tracked vehicle can occupy.</summary>
    private static bool Ground(byte[] types, NavGrid grid, int index)
        => grid.IsWalkable(index) && BaseCostPermille(MovementClass.Tracked, (TerrainType)types[index]) != 0;

    /// <summary>
    /// Turns the water along a straight line between two cells into a ford.
    /// Returns true when at least one cell actually changed.
    /// </summary>
    private static bool CarveFord(byte[] types, int size, int from, int to)
    {
        int x0 = from % size;
        int z0 = from / size;
        int x1 = to % size;
        int z1 = to / size;
        int steps = Math.Max(Math.Abs(x1 - x0), Math.Abs(z1 - z0));

        if (steps == 0)
        {
            return false;
        }

        bool changed = false;

        for (int step = 0; step <= steps; step++)
        {
            int x = x0 + (((x1 - x0) * step) / steps);
            int z = z0 + (((z1 - z0) * step) / steps);
            int index = (z * size) + x;

            if (types[index] == (byte)TerrainType.DeepWater)
            {
                types[index] = (byte)TerrainType.ShallowWater;
                changed = true;
            }
            else if (types[index] == (byte)TerrainType.Lava)
            {
                // A crossing over a crater rim is rock, not a ford: it is passable
                // and nobody wants to linger on it.
                types[index] = (byte)TerrainType.Rock;
                changed = true;
            }
        }

        return changed;
    }

    /// <summary>
    /// Decides a cell's surface. Ordered from the most restrictive rule down:
    /// water first, then rock, then altitude bands, then the wet lowlands where
    /// mud forms.
    /// </summary>
    private static TerrainType Classify(
        int height,
        int slope,
        int waterLevel,
        int mudLine,
        int sandLine,
        int snowLine)
    {
        if (height <= waterLevel - 1_200)
        {
            return TerrainType.DeepWater;
        }

        if (height <= waterLevel)
        {
            return TerrainType.ShallowWater;
        }

        if (slope > RockSlopePermille)
        {
            return TerrainType.Rock;
        }

        if (height >= snowLine)
        {
            return TerrainType.Snow;
        }

        if (height <= mudLine)
        {
            return TerrainType.Mud;
        }

        if (height <= sandLine)
        {
            return TerrainType.Sand;
        }

        return TerrainType.Grass;
    }

    /// <summary>Feature size of the broad regional swing in vegetation and moisture, in cells.</summary>
    private const int TextureRegionCells = 11;

    /// <summary>Feature size of the grove-sized ripple beneath it, in cells.</summary>
    private const int TextureGroveCells = 4;

    /// <summary>Vegetation lost per 1000 mm/m of slope: soil does not sit on a hillside.</summary>
    private const int VegetationSlopePenalty = 60;

    /// <summary>
    /// How far the canopy noise is stretched around its mean, in permille: 1000 would use
    /// the noise as it comes, and 3000 reaches the ends of each surface's band.
    /// </summary>
    private const int TextureContrastPermille = 3_000;

    /// <summary>Wetness permille a cell loses per 1000 mm/m of slope, as a hillside drains.</summary>
    private const int MoistureDrainPermille = 500;

    /// <summary>
    /// How many navigation cells out aspect and landform sample the ground the cell's shape
    /// is decided from. One, so the eight ring samples are the anchors of the eight
    /// neighbouring navigation cells: the shape is then read over the same ground the
    /// lattice itself calls one cell across, and the two height samples inside the cell
    /// cannot outvote it.
    /// </summary>
    public const int ShapeRadiusCells = 1;

    /// <summary>
    /// How many navigation cells out the shape pass looks for the ground a level cell sits
    /// among — the fetch that decides whether flat ground is a plateau, a basin or a shelf.
    /// Further out than the ring, because "high relative to its surroundings" is not a claim
    /// a cell can make about ground at its own edge, and far enough that the fetch sees the
    /// next landform rather than the same bump twice.
    /// </summary>
    public const int ShapeFetchCells = 3;

    /// <summary>
    /// Generates the aspect and the landform of every cell.
    /// <para>
    /// Sampled on the <em>height map</em> rather than on the navigation heights. Aspect is a
    /// property of the ground, the finer field carries more of it, and the surface's own
    /// slope already comes from the height map at this very sample — reading the coarse
    /// lattice here instead would leave one layer holding two terrains to disagree about.
    /// The cell still gets one direction, because the ring is drawn at the stride: its eight
    /// samples are the anchors of the eight neighbouring navigation cells, so the answer is
    /// a direction over the cell's own footprint rather than over half of it.
    /// </para>
    /// <para>
    /// A pure function of the height field and the lattice: no seed, no floating point, and
    /// no dependence on the order cells are visited.
    /// </para>
    /// </summary>
    private static void GenerateShape(uint[] attributes, int size, HeightMap map, int stride)
    {
        Span<int> ring = stackalloc int[TerrainShape.RingSamples];
        Span<int> far = stackalloc int[TerrainShape.RingSamples];
        ReadOnlySpan<int> heights = map.RawHeights;
        int radius = ShapeRadiusCells * stride;
        int fetch = ShapeFetchCells * stride;
        int ringMm = radius * map.CellSizeMm;
        int farMm = fetch * map.CellSizeMm;

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (z * size) + x;
                int sampleX = Math.Min(x * stride, map.Size - 1);
                int sampleZ = Math.Min(z * stride, map.Size - 1);
                int height = map.HeightAt(sampleX, sampleZ);

                TerrainShape.SampleRing(heights, map.Size, sampleX, sampleZ, radius, ring);
                TerrainShape.SampleRing(heights, map.Size, sampleX, sampleZ, fetch, far);

                attributes[index] = new TerrainAttributes(attributes[index])
                    .WithAspect(TerrainShape.AspectOf(ring, ringMm))
                    .WithLandform(TerrainShape.LandformOf(ring, far, height, ringMm, farMm))
                    .Raw;
            }
        }
    }

    /// <summary>
    /// Generates the attribute word of every cell.
    /// <para>
    /// A pure function of the height field, the surface, the lattice and the seed: no
    /// floating point, no RNG stream, no dependence on the order cells are visited, so
    /// two machines grow the same ground from the same seed without either of them
    /// having to be told.
    /// </para>
    /// </summary>
    private static uint[] GenerateAttributes(
        byte[] types,
        int size,
        NavGrid grid,
        HeightMap map,
        int stride,
        int waterLevel,
        int relief,
        ulong seed)
    {
        uint[] attributes = new uint[types.Length];

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int index = (z * size) + x;
                int sampleX = Math.Min(x * stride, map.Size - 1);
                int sampleZ = Math.Min(z * stride, map.Size - 1);
                int slope = map.SlopePermille(sampleX, sampleZ);
                TerrainType type = (TerrainType)types[index];
                int height = grid.HeightAt(index);

                attributes[index] = new TerrainAttributes()
                    .WithVegetation(VegetationFor(type, slope, x, z, seed))
                    .WithMoisture(MoistureFor(type, height, slope, waterLevel, relief, x, z, seed))
                    .Raw;
            }
        }

        return attributes;
    }

    /// <summary>
    /// Canopy density of a cell, 0..255.
    /// <para>
    /// The surface decides which band the cell lives in — a wood carries a canopy, open
    /// ground carries scrub, sand and scree carry next to nothing — and the noise decides
    /// where in that band it sits. That is what makes woodland a *density* rather than a
    /// type: a thin wood and a closed one are both woods, and a player can tell them
    /// apart without the movement table changing underneath them.
    /// </para>
    /// </summary>
    private static int VegetationFor(TerrainType type, int slope, int x, int z, ulong seed)
    {
        // Bare at the bottom, closed canopy at the top. A wood is never quite bare, and
        // dunes and scree are bare far more often than not, which is what keeps each
        // surface's mean where a player would expect to find it.
        (int floor, int ceiling) = type switch
        {
            TerrainType.Forest => (140, 255),
            TerrainType.Grass => (10, 180),
            TerrainType.Mud => (10, 150),
            TerrainType.Sand => (0, 70),
            TerrainType.Mine => (0, 60),
            TerrainType.Rock => (0, 45),
            TerrainType.Snow => (0, 35),
            _ => (0, 0),
        };

        // Water and lava grow nothing, and saying so once here is cheaper than a rule
        // about it everywhere downstream.
        if (ceiling == 0)
        {
            return 0;
        }

        int texture = Texture(x, z, seed ^ 0x7E6E_7A71);

        // Spread the noise across the band rather than letting it cluster in the middle of
        // it. The weighted average of three scales is roughly a bell, and a bell uses half
        // the range it is given — which would leave the closed-canopy end of the scale
        // somewhere no cell ever reaches, with bare ground at the other. That is the shape
        // of the last three bugs this project had, and it costs one multiply to not have a
        // fourth: a wood can now be closed, and open ground can be bare.
        int spread = IntMath.Clamp(
            128 + (((texture - 128) * TextureContrastPermille) / 1_000),
            0,
            255);

        // Divided by 255 rather than shifted: both ends of the band are then positions a
        // cell can actually hold.
        int vegetation = floor + (((ceiling - floor) * spread) / 255);

        return IntMath.Clamp(
            vegetation - ((slope * VegetationSlopePenalty) / 1_000),
            0,
            TerrainAttributes.MaxVegetation);
    }

    /// <summary>
    /// How wet a cell's ground is, 0..15.
    /// <para>
    /// Moisture follows the water: the low ground by the shore is wet, the tops are dry,
    /// and a slope sheds what falls on it. Which is what makes wet ground the ground a
    /// player already reads as marsh and dry ground the ridge line — and it is the input
    /// the fire step will need to decide what will not burn.
    /// </para>
    /// </summary>
    private static int MoistureFor(
        TerrainType type,
        int height,
        int slope,
        int waterLevel,
        int relief,
        int x,
        int z,
        ulong seed)
    {
        // Standing water is wet by definition; there is nothing for four bits to decide.
        if (type is TerrainType.DeepWater or TerrainType.ShallowWater)
        {
            return TerrainAttributes.MaxMoisture;
        }

        int above = IntMath.Clamp(height - waterLevel, 0, relief);

        // 1000 at the waterline, 0 at the highest ground on the map. Measured against the
        // relief rather than against the map's ceiling, for the reason the surface bands
        // are: a map that only occupies the bottom of its height range still has to have
        // both wet ground and dry ground on it.
        int wetness = 1_000 - ((above * 1_000) / relief);

        wetness -= (slope * MoistureDrainPermille) / 1_000;

        // Half a level of rounding, so wetness does not sit a whole step low because the
        // division truncated towards zero.
        int moisture = ((wetness * TerrainAttributes.MaxMoisture) + 500) / 1_000;

        return IntMath.Clamp(
            moisture + Variation(x, z, seed ^ 0x3501_57A7),
            0,
            TerrainAttributes.MaxMoisture);
    }

    /// <summary>
    /// Multi-scale noise over the cell lattice, 0..255.
    /// <para>
    /// Three feature sizes weighted coarse to fine, because one scale is either a flat
    /// wash or single-cell speckle and ground is neither: a field has regions, groves and
    /// grain, and a canopy that does not have all three reads as a texture rather than as
    /// somewhere.
    /// </para>
    /// </summary>
    private static int Texture(int x, int z, ulong seed)
        => ((CellNoise(x, z, TextureRegionCells, seed) * 5) +
            // Each scale gets its own salt: sharing one leaves the scales correlated
            // wherever their lattices happen to line up, which shows up as a visible grain.
            (CellNoise(x, z, TextureGroveCells, seed ^ 0x1F1F_1F1F) * 3) +
            (CellNoise(x, z, 1, seed ^ 0x2F2F_2F2F) * 2)) / 10;

    /// <summary>A small signed swing off the same noise, -2..+2.</summary>
    private static int Variation(int x, int z, ulong seed) => ((Texture(x, z, seed) * 5) >> 8) - 2;

    /// <summary>
    /// Smooth value noise over the cell lattice, 0..255, at a given feature size.
    /// <para>
    /// Interpolated rather than blocky, through the same Q16 smoothstep the height field
    /// uses. A per-block roll would put a straight edge through the middle of a wood, and
    /// the edge would be the thing a player noticed.
    /// </para>
    /// </summary>
    private static int CellNoise(int x, int z, int spacing, ulong seed)
    {
        // Lattice coordinates are cell coordinates and therefore never negative, so the
        // divisions below are the floor of a non-negative value.
        int gridX = x / spacing;
        int gridZ = z / spacing;
        int fractionX = (int)(((long)(x - (gridX * spacing)) << 16) / spacing);
        int fractionZ = (int)(((long)(z - (gridZ * spacing)) << 16) / spacing);

        int smoothX = Smoothstep(fractionX);
        int smoothZ = Smoothstep(fractionZ);

        int top = Lerp(Corner(gridX, gridZ, seed), Corner(gridX + 1, gridZ, seed), smoothX);
        int bottom = Lerp(Corner(gridX, gridZ + 1, seed), Corner(gridX + 1, gridZ + 1, seed), smoothX);

        return Lerp(top, bottom, smoothZ);
    }

    /// <summary>A lattice corner's value, 0..255.</summary>
    private static int Corner(int gridX, int gridZ, ulong seed) => (Hash(gridX, gridZ, seed) >> 8) & 0xFF;

    /// <summary>Smoothstep on a Q16 fraction: f*f*(3-2f).</summary>
    private static int Smoothstep(int fraction)
    {
        long f = fraction;
        long squared = (f * f) >> 16;

        return (int)((squared * ((3L * 65_536) - (2 * f))) >> 16);
    }

    /// <summary>Interpolates two lattice values by a Q16 fraction.</summary>
    private static int Lerp(int from, int to, int fraction)
        => from + (int)(((long)(to - from) * fraction) >> 16);

    /// <summary>Surface at a cell index.</summary>
    public TerrainType TypeAt(int index) => (TerrainType)_types[index];

    /// <summary>Raw surface bytes, for hashing. Terrain is no longer seed-only once it can be changed.</summary>
    public ReadOnlySpan<byte> RawTypes => _types;

    /// <summary>Raw churn bytes, for hashing. Ground wear is state like any other.</summary>
    public ReadOnlySpan<byte> RawChurn => _churn;

    /// <summary>
    /// Raw attribute words, for hashing. Canopy density and moisture are state in every
    /// sense that matters: fire, tracks and regrowth write them, so two machines that
    /// disagreed about them would disagree about the battle.
    /// </summary>
    public ReadOnlySpan<uint> RawAttributes => _attributes;

    /// <summary>
    /// Attribute word of a cell; a bare default word outside the lattice, as for churn.
    /// </summary>
    public TerrainAttributes AttributesAt(int index)
        => (uint)index < (uint)_attributes.Length ? new TerrainAttributes(_attributes[index]) : default;

    /// <summary>True while any cell is under a temporary weather effect.</summary>
    public bool HasWeather => _weatherCells > 0;

    /// <summary>True while any cell carries churn, so the decay pass can be skipped.</summary>
    public bool HasChurn => _churnedCells > 0;

    /// <summary>
    /// How worn a cell is, 0..255. Ground driven over becomes mud, and mud driven
    /// over becomes a bog — which is how a heavy push bogs itself down.
    /// </summary>
    public int ChurnAt(int index) => (uint)index < (uint)_churn.Length ? _churn[index] : 0;

    /// <summary>
    /// Adds wear to a cell, saturating. <paramref name="amount"/> is the ground
    /// pressure that caused it, so a heavy hull churns more than a light one.
    /// </summary>
    public void AddChurn(int index, int amount)
    {
        if ((uint)index >= (uint)_churn.Length || amount <= 0)
        {
            return;
        }

        int worn = _churn[index];
        int total = Math.Min(255, worn + amount);

        if (worn == 0 && total > 0)
        {
            _churnedCells++;
        }

        if (total != worn)
        {
            _churn[index] = (byte)total;
            ChurnRevision++;
        }
    }

    /// <summary>Lets every worn cell settle a little. Returns cells still worn.</summary>
    public int DecayChurn()
    {
        if (_churnedCells == 0)
        {
            return 0;
        }

        int remaining = 0;

        for (int index = 0; index < _churn.Length; index++)
        {
            if (_churn[index] == 0)
            {
                continue;
            }

            _churn[index]--;
            remaining++;
        }

        _churnedCells = remaining;
        ChurnRevision++;
        return remaining;
    }

    /// <summary>Cell index containing a world coordinate, or -1 when outside.</summary>
    public int IndexOfWorld(int worldX, int worldZ)
    {
        int x = (worldX - OriginMm) / CellSizeMm;
        int z = (worldZ - OriginMm) / CellSizeMm;

        return (uint)x >= (uint)Size || (uint)z >= (uint)Size ? -1 : (z * Size) + x;
    }

    /// <summary>Replaces a cell's surface immediately.</summary>
    public bool SetType(int index, TerrainType type)
    {
        if ((uint)index >= (uint)_types.Length)
        {
            return false;
        }

        _types[index] = (byte)type;
        Revision++;
        return true;
    }

    /// <summary>
    /// Replaces a cell's attributes immediately.
    /// <para>
    /// Nothing in this step calls it: generation is the only writer for now. It exists
    /// because the fields it writes are the ones fire, crushing and regrowth will write,
    /// and a mutator added after the fact is a mutator that forgets to move the revision.
    /// </para>
    /// </summary>
    public bool SetAttributes(int index, TerrainAttributes attributes)
    {
        if ((uint)index >= (uint)_attributes.Length)
        {
            return false;
        }

        _attributes[index] = attributes.Raw;
        AttributeRevision++;
        return true;
    }

    /// <summary>
    /// Lays a surface over a circular area until <paramref name="expiresTick"/>.
    /// <para>
    /// The original surface of each affected cell is remembered once, so overlapping
    /// effects extend rather than corrupt one another and the ground reverts to
    /// exactly what it was. That is what makes weather control a timed weapon
    /// rather than a permanent edit to the map.
    /// </para>
    /// </summary>
    /// <returns>Number of cells covered.</returns>
    public int ApplyWeather(int centreX, int centreZ, int radiusMm, TerrainType type, long expiresTick)
    {
        int minX = (centreX - radiusMm - OriginMm) / CellSizeMm;
        int maxX = (centreX + radiusMm - OriginMm) / CellSizeMm;
        int minZ = (centreZ - radiusMm - OriginMm) / CellSizeMm;
        int maxZ = (centreZ + radiusMm - OriginMm) / CellSizeMm;

        minX = IntMath.Clamp(minX, 0, Size - 1);
        maxX = IntMath.Clamp(maxX, 0, Size - 1);
        minZ = IntMath.Clamp(minZ, 0, Size - 1);
        maxZ = IntMath.Clamp(maxZ, 0, Size - 1);

        long radiusSquared = (long)radiusMm * radiusMm;
        int tick = (int)Math.Min(expiresTick, int.MaxValue);
        int covered = 0;

        for (int z = minZ; z <= maxZ; z++)
        {
            for (int x = minX; x <= maxX; x++)
            {
                int centreXmm = OriginMm + (x * CellSizeMm) + (CellSizeMm / 2);
                int centreZmm = OriginMm + (z * CellSizeMm) + (CellSizeMm / 2);
                int dx = centreXmm - centreX;
                int dz = centreZmm - centreZ;

                if (((long)dx * dx) + ((long)dz * dz) > radiusSquared)
                {
                    continue;
                }

                int index = (z * Size) + x;

                if (_weatherExpiry[index] == 0)
                {
                    _original[index] = _types[index];
                    _weatherCells++;
                }

                _types[index] = (byte)type;
                _weatherExpiry[index] = tick;
                covered++;
            }
        }

        if (covered > 0)
        {
            Revision++;
        }

        return covered;
    }

    /// <summary>Reverts every weather effect that has run out. Returns cells restored.</summary>
    public int ExpireWeather(long tick)    {
        if (_weatherCells == 0)
        {
            return 0;
        }

        int restored = 0;

        for (int index = 0; index < _types.Length; index++)
        {
            if (_weatherExpiry[index] == 0 || _weatherExpiry[index] > tick)
            {
                continue;
            }

            _types[index] = _original[index];
            _weatherExpiry[index] = 0;
            _weatherCells--;
            restored++;
        }

        if (restored > 0)
        {
            Revision++;
        }

        return restored;
    }

    /// <summary>Surface at a lattice coordinate; grass outside the grid.</summary>
    public TerrainType TypeAtCell(int cellX, int cellZ)
        => (uint)cellX >= (uint)Size || (uint)cellZ >= (uint)Size
            ? TerrainType.Grass
            : (TerrainType)_types[(cellZ * Size) + cellX];

    /// <summary>Number of cells of a given surface, for diagnostics.</summary>
    public int CountOf(TerrainType type)
    {
        int count = 0;

        for (int i = 0; i < _types.Length; i++)
        {
            if (_types[i] == (byte)type)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// Movement cost of a surface in permille of flat ground; zero means the
    /// surface cannot be entered at all.
    /// </summary>
    /// <summary>
    /// What cover a surface gives, as the permille of incoming damage that still
    /// lands: 1000 is no cover, 600 is a third off.
    /// <para>
    /// Deliberately keyed on the movement class as well as the surface, because cover
    /// is not a property of the ground on its own. A wood is cover to a man who can lie
    /// in it and an obstruction to a tank that can only sit on top of it — which is the
    /// same reason the cost table inverts there.
    /// </para>
    /// </summary>
    public static int CoverPermille(MovementClass movement, TerrainType type) => type switch
    {
        TerrainType.Forest => movement switch
        {
            MovementClass.Foot => 600,
            MovementClass.Tracked => 880,
            MovementClass.Wheeled => 880,
            _ => 1_000,
        },
        TerrainType.Rock => 850,
        TerrainType.Mine => 900,
        _ => 1_000,
    };

    /// <summary>Cover where a unit is standing, by its movement class.</summary>
    public int CoverAt(int index, MovementClass movement)
        => index >= 0 && index < _types.Length
            ? CoverPermille(movement, TypeAt(index))
            : 1_000;

    /// <summary>
    /// Movement cost of a surface, in permille of the baseline: 1000 is normal going,
    /// higher is slower, and zero means the surface cannot be entered at all.
    /// </summary>
    public static int BaseCostPermille(MovementClass movement, TerrainType type) => type switch
    {
        TerrainType.Grass => BasePermille,
        TerrainType.Forest => movement switch
        {
            // The only surface where the classes invert: trees are an obstacle to a
            // vehicle and an advantage to a man. Infantry pick their way through at
            // some cost; armour has to go round or push through slowly.
            MovementClass.Air => BasePermille,
            MovementClass.Foot => 140,
            MovementClass.Tracked => 280,
            MovementClass.Wheeled => 320,
            _ => 0,
        },
        TerrainType.Rock => movement == MovementClass.Air ? BasePermille : 180,
        TerrainType.Sand => movement switch
        {
            MovementClass.Air => BasePermille,
            MovementClass.Foot => 120,
            MovementClass.Tracked => 150,
            MovementClass.Wheeled => 160,
            _ => 0,
        },
        TerrainType.Snow => movement switch
        {
            MovementClass.Air => BasePermille,
            MovementClass.Foot => 150,
            MovementClass.Tracked => 180,
            MovementClass.Wheeled => 200,
            _ => 0,
        },
        TerrainType.Mud => movement switch
        {
            MovementClass.Air => BasePermille,
            MovementClass.Foot => 200,
            MovementClass.Tracked => 250,
            MovementClass.Wheeled => 300,
            _ => 0,
        },
        // Shallow water is a ford: slow and risky, but crossable. Deep water is the
        // real barrier. Making shallow water impassable too fragments the map into
        // pockets, and bridges do not exist yet.
        TerrainType.ShallowWater => movement switch
        {
            MovementClass.Air => BasePermille,
            MovementClass.Foot => 250,
            MovementClass.Tracked => 300,
            MovementClass.Wheeled => 450,
            _ => 0,
        },

        // Ore ground is rough going for everyone, and it is a place to stop rather
        // than a place to cross.
        TerrainType.Mine => movement == MovementClass.Air ? BasePermille : 130,
        TerrainType.DeepWater or TerrainType.Lava =>
            movement == MovementClass.Air ? BasePermille : 0,
        _ => BasePermille,
    };

    /// <summary>
    /// Cost of entering a cell for a mover, applying ground pressure to the soft
    /// surfaces. Returns zero for an impassable surface.
    /// </summary>
    public int CostPermille(int index, MovementClass movement, int groundPressurePermille)
    {
        TerrainType type = TypeAt(index);
        int cost = BaseCostPermille(movement, type);

        if (cost == 0 || movement == MovementClass.Air)
        {
            return cost;
        }

        // Only the soft surfaces care how heavily the mover presses on them.
        if (type is TerrainType.Mud or TerrainType.Snow)
        {
            int pressure = groundPressurePermille > 0 ? groundPressurePermille : 1_000;
            cost = IntMath.Clamp((cost * pressure) / 1_000, 25, 2_000);
        }

        // Worn ground is worse for everyone, but it is still worse for a heavy
        // mover: the surcharge multiplies the cost the unit already pays, so the
        // same churned field is a nuisance to infantry and a bog to a tank.
        int churn = ChurnAt(index);

        if (churn > 0 && type is TerrainType.Grass or TerrainType.Sand or TerrainType.Mud)
        {
            cost += (cost * ((churn * MaxChurnSurcharge) / 255)) / 1_000;
        }

        return cost;
    }

    /// <summary>True when a mover may enter the cell at all.</summary>
    public bool IsPassable(int index, MovementClass movement)
        => BaseCostPermille(movement, TypeAt(index)) != 0;

    /// <summary>Greek name of a surface, for the interface.</summary>
    public static string GreekName(TerrainType type) => type switch
    {
        TerrainType.Grass => "Γρασίδι",
        TerrainType.Mud => "Λάσπη",
        TerrainType.Sand => "Άμμος",
        TerrainType.Snow => "Χιόνι",
        TerrainType.Rock => "Βράχος",
        TerrainType.ShallowWater => "Νερό",
        TerrainType.DeepWater => "Βαθύ νερό",
        TerrainType.Lava => "Λάβα",
        TerrainType.Mine => "Κοίτασμα",
        _ => "Άγνωστο",
    };
}
