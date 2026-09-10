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

    private int _weatherCells;
    private int _churnedCells;

    private TerrainLayer(int size, int cellSizeMm, int originMm, int waterLevelMm, int maxHeightMm, byte[] types)
    {
        Size = size;
        CellSizeMm = cellSizeMm;
        OriginMm = originMm;
        WaterLevelMm = waterLevelMm;
        MaxHeightMm = maxHeightMm;
        _types = types;
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

        return new TerrainLayer(size, grid.CellSizeMm, grid.OriginMm, waterLevel, map.MaxHeightMm, types);
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

    /// <summary>Surface at a cell index.</summary>
    public TerrainType TypeAt(int index) => (TerrainType)_types[index];

    /// <summary>Raw surface bytes, for hashing. Terrain is no longer seed-only once it can be changed.</summary>
    public ReadOnlySpan<byte> RawTypes => _types;

    /// <summary>Raw churn bytes, for hashing. Ground wear is state like any other.</summary>
    public ReadOnlySpan<byte> RawChurn => _churn;

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
