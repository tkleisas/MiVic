using MiVic.Core.Numerics;
using MiVic.Core.Terrain;

namespace MiVic.Core.Pathfinding;

/// <summary>
/// Walkability and movement cost, derived from a <see cref="HeightMap"/>.
/// <para>
/// Costs are integers and terrain steepness is the only input, so the grid is a
/// pure function of the height map and therefore of the world seed. Cells that
/// are too steep to cross are marked impassable rather than merely expensive, so
/// that cliffs read as real obstacles instead of slow ground.
/// </para>
/// </summary>
public sealed class NavGrid
{
    /// <summary>Cost of crossing one flat cell.</summary>
    public const int BaseCost = 100;

    private readonly int[] _cost;
    private readonly int[] _height;

    private NavGrid(int size, int cellSizeMm, int originMm, int[] cost, int[] height, int blockedCells)
    {
        Size = size;
        CellSizeMm = cellSizeMm;
        OriginMm = originMm;
        _cost = cost;
        _height = height;
        BlockedCellCount = blockedCells;
    }

    /// <summary>Samples per side.</summary>
    public int Size { get; }

    /// <summary>Cell size in millimetres.</summary>
    public int CellSizeMm { get; }

    /// <summary>World coordinate of cell zero, in millimetres.</summary>
    public int OriginMm { get; }

    /// <summary>Number of impassable cells.</summary>
    public int BlockedCellCount { get; }

    /// <summary>Total cell count.</summary>
    public int CellCount => Size * Size;

    /// <summary>Builds the grid from a height map.</summary>
    /// <param name="map">Source height field.</param>
    /// <param name="maxSlopePermille">Slope above which a cell becomes impassable, in mm per metre.</param>
    /// <param name="stride">
    /// Height-map samples per navigation cell. Pathfinding does not need the
    /// terrain's full resolution: halving it quarters the search space, and A\*
    /// cost is dominated by the number of nodes it may expand.
    /// </param>
    public static NavGrid Build(HeightMap map, int maxSlopePermille = 900, int stride = 2)
    {
        ArgumentNullException.ThrowIfNull(map);

        if (stride < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(stride), stride, "Must be at least one.");
        }

        int size = ((map.Size - 1) / stride) + 1;
        int count = size * size;
        int[] cost = new int[count];
        int[] height = new int[count];
        int blocked = 0;

        for (int z = 0; z < size; z++)
        {
            for (int x = 0; x < size; x++)
            {
                int sampleX = Math.Min(x * stride, map.Size - 1);
                int sampleZ = Math.Min(z * stride, map.Size - 1);
                int index = (z * size) + x;

                height[index] = map.HeightAt(sampleX, sampleZ);

                int slope = map.SlopePermille(sampleX, sampleZ);

                if (slope > maxSlopePermille)
                {
                    cost[index] = 0;
                    blocked++;
                }
                else
                {
                    // Steeper ground is more expensive but still passable.
                    cost[index] = BaseCost + (slope / 8);
                }
            }
        }

        return new NavGrid(size, map.CellSizeMm * stride, map.OriginMm, cost, height, blocked);
    }

    /// <summary>True when a cell can be entered.</summary>
    public bool IsWalkable(int index) => _cost[index] != 0;

    /// <summary>Cost of entering a cell; zero when blocked.</summary>
    public int CostAt(int index) => _cost[index];

    /// <summary>Terrain height of a cell, in millimetres.</summary>
    public int HeightAt(int index) => _height[index];

    /// <summary>Lattice coordinates of an index.</summary>
    public int CellX(int index) => index % Size;

    /// <summary>Lattice coordinates of an index.</summary>
    public int CellZ(int index) => index / Size;

    /// <summary>Index of a lattice coordinate, or -1 when outside the grid.</summary>
    public int IndexOf(int cellX, int cellZ)
        => (uint)cellX >= (uint)Size || (uint)cellZ >= (uint)Size ? -1 : (cellZ * Size) + cellX;

    /// <summary>
    /// True when a straight line between two cells stays on walkable ground.
    /// <para>
    /// Used to smooth routes: a raw A* path hugs cell centres, so a unit walking
    /// diagonally visibly zigzags. Collapsing the route to its turning points
    /// makes movement read as a straight line.
    /// </para>
    /// </summary>
    public bool HasLineOfSight(int fromIndex, int toIndex)
    {
        int x0 = CellX(fromIndex);
        int z0 = CellZ(fromIndex);
        int x1 = CellX(toIndex);
        int z1 = CellZ(toIndex);

        int dx = x1 - x0;
        int dz = z1 - z0;
        int steps = Math.Max(Math.Abs(dx), Math.Abs(dz));

        if (steps == 0)
        {
            return true;
        }

        for (int step = 1; step <= steps; step++)
        {
            int x = x0 + ((dx * step) / steps);
            int z = z0 + ((dz * step) / steps);

            if (!IsWalkable(IndexOf(x, z)))
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Collapses a cell path to its turning points in place, returning the new
    /// length. <paramref name="start"/> is the cell the unit currently occupies.
    /// </summary>
    public int Smooth(ReadOnlySpan<int> path, int length, int start, Span<int> destination)
    {
        if (length <= 1)
        {
            path[..length].CopyTo(destination);
            return length;
        }

        int kept = 0;
        int anchor = start;

        for (int i = 0; i < length - 1; i++)
        {
            if (!HasLineOfSight(anchor, path[i + 1]))
            {
                destination[kept++] = path[i];
                anchor = path[i];
            }
        }

        destination[kept++] = path[length - 1];
        return kept;
    }

    /// <summary>Index of the cell containing a world position, clamped to the grid.</summary>
    public int IndexOfWorld(WorldPos position)
        => IndexOf(
            IntMath.Clamp((position.X - OriginMm) / CellSizeMm, 0, Size - 1),
            IntMath.Clamp((position.Z - OriginMm) / CellSizeMm, 0, Size - 1));

    /// <summary>Centre of a cell in world millimetres, at the cell's terrain height.</summary>
    public WorldPos CentreOf(int index)
    {
        int x = OriginMm + (CellX(index) * CellSizeMm) + (CellSizeMm / 2);
        int z = OriginMm + (CellZ(index) * CellSizeMm) + (CellSizeMm / 2);
        return new WorldPos(x, _height[index], z);
    }

    /// <summary>
    /// Finds the closest walkable cell to <paramref name="index"/> by searching
    /// outward in rings. Returns -1 when the grid has no walkable cell at all.
    /// </summary>
    public int NearestWalkable(int index)
    {
        if (IsWalkable(index))
        {
            return index;
        }

        int cx = CellX(index);
        int cz = CellZ(index);
        int maxRadius = Size;

        for (int radius = 1; radius <= maxRadius; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // Only the ring itself, not the filled square.
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = IndexOf(cx + dx, cz + dz);

                    if (candidate >= 0 && IsWalkable(candidate))
                    {
                        return candidate;
                    }
                }
            }
        }

        return -1;
    }
}
