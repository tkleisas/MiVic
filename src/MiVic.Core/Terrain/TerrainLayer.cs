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

    private int _weatherCells;

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
    }

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
    /// Generates the layer for a height field, aligned to a navigation grid so the
    /// two share one lattice.
    /// </summary>
    /// <param name="map">Source height field.</param>
    /// <param name="grid">Navigation grid built from the same map.</param>
    public static TerrainLayer Build(HeightMap map, NavGrid grid)
    {
        ArgumentNullException.ThrowIfNull(map);
        ArgumentNullException.ThrowIfNull(grid);

        int size = grid.Size;
        int stride = Math.Max(1, grid.CellSizeMm / map.CellSizeMm);

        // The water line is deliberately shallow relative to the map's relief: a
        // high line floods a quarter of the battlefield into lakes, and since deep
        // water is impassable that fragments the map into pockets no ground unit
        // can leave. Fords (shallow water) connect the rest.
        int waterLevel = map.MaxHeightMm / 12;
        int snowLine = (map.MaxHeightMm * 3) / 4;
        int sandLine = map.MaxHeightMm / 8;
        int mudLine = waterLevel + 2_000;

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

        EnsureGroundConnectivity(types, size, grid);

        return new TerrainLayer(size, grid.CellSizeMm, grid.OriginMm, waterLevel, map.MaxHeightMm, types);
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

    /// <summary>True while any cell is under a temporary weather effect.</summary>
    public bool HasWeather => _weatherCells > 0;

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

        return covered;
    }

    /// <summary>Reverts every weather effect that has run out. Returns cells restored.</summary>
    public int ExpireWeather(long tick)
    {
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
    public static int BaseCostPermille(MovementClass movement, TerrainType type) => type switch
    {
        TerrainType.Grass => BasePermille,
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
