using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// A uniform grid over the battlefield, rebuilt once per tick.
/// <para>
/// Morale needs to know how many friends and enemies stand near every unit. Doing
/// that by scanning the whole entity array for each unit is O(n²) — with 500
/// units that is 250,000 distance checks per tick, which alone pushed the frame
/// past the 60 fps budget. Counting into cells once is O(n), and a radius query
/// then touches a handful of cells instead of every entity.
/// </para>
/// <para>
/// Cells are filled in ascending slot order, so a query always visits neighbours
/// in the same order on every machine.
/// </para>
/// </summary>
public sealed class SpatialIndex
{
    private readonly int _cellsPerSide;
    private readonly int _cellSizeMm;
    private readonly int _originMm;
    private readonly int[] _counts;
    private readonly int[] _starts;
    private readonly int[] _slots;

    /// <summary>Creates an index covering a square of <paramref name="extentMm"/>.</summary>
    public SpatialIndex(int capacity, int extentMm, int cellSizeMm = 60_000)
    {
        if (capacity <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Must be positive.");
        }

        if (cellSizeMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(cellSizeMm), cellSizeMm, "Must be positive.");
        }

        _cellSizeMm = cellSizeMm;
        _originMm = -(extentMm / 2);
        _cellsPerSide = (extentMm / cellSizeMm) + 1;

        int cellCount = _cellsPerSide * _cellsPerSide;
        _counts = new int[cellCount + 1];
        _starts = new int[cellCount + 1];
        _slots = new int[capacity];
    }

    /// <summary>Cells along each axis.</summary>
    public int CellsPerSide => _cellsPerSide;

    /// <summary>Rebuilds the index from the world's live entities.</summary>
    public void Rebuild(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        int cellCount = _cellsPerSide * _cellsPerSide;
        Array.Clear(_counts, 0, cellCount);

        int capacity = Math.Min(world.Capacity, _slots.Length);

        for (int slot = 0; slot < capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                _counts[CellOf(world.GetRefBySlot(slot).Position)]++;
            }
        }

        int running = 0;

        for (int cell = 0; cell < cellCount; cell++)
        {
            _starts[cell] = running;
            running += _counts[cell];
            _counts[cell] = _starts[cell];
        }

        _starts[cellCount] = running;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                _slots[_counts[CellOf(world.GetRefBySlot(slot).Position)]++] = slot;
            }
        }
    }

    /// <summary>Slot indices in one cell, in ascending slot order.</summary>
    public ReadOnlySpan<int> Cell(int cellIndex) => _slots.AsSpan(_starts[cellIndex], _starts[cellIndex + 1] - _starts[cellIndex]);

    /// <summary>Cell index containing a position, clamped to the grid.</summary>
    public int CellOf(WorldPos position)
    {
        int x = IntMath.Clamp((position.X - _originMm) / _cellSizeMm, 0, _cellsPerSide - 1);
        int z = IntMath.Clamp((position.Z - _originMm) / _cellSizeMm, 0, _cellsPerSide - 1);
        return (z * _cellsPerSide) + x;
    }

    /// <summary>Cell coordinates of an index.</summary>
    public (int X, int Z) CellCoordinates(int cellIndex) => (cellIndex % _cellsPerSide, cellIndex / _cellsPerSide);

    /// <summary>Cell coordinate along an axis for a world coordinate, clamped to the grid.</summary>
    public int CoordinateOf(int worldMm) => IntMath.Clamp((worldMm - _originMm) / _cellSizeMm, 0, _cellsPerSide - 1);

    /// <summary>Index of a cell, which the caller must have clamped.</summary>
    public int IndexOf(int cellX, int cellZ) => (cellZ * _cellsPerSide) + cellX;
}
