using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>How far along one crossing is.</summary>
/// <param name="Total">Cells in the span.</param>
/// <param name="Built">Cells whose deck is up — and whose water is ford.</param>
/// <param name="StartTick">Tick the order was carried out on.</param>
/// <param name="ReadyTick">Tick the last cell of the span is placed on.</param>
public readonly record struct BridgeState(int Total, int Built, long StartTick, long ReadyTick)
{
    /// <summary>True once the last cell is up, which is when the crossing is whole.</summary>
    public bool Complete => Built >= Total;

    /// <summary>How much of the work is done, in permille.</summary>
    public int ProgressPermille => Total <= 0 ? 1_000 : (Built * 1_000) / Total;

    /// <summary>Ticks of work left, or zero once the crossing is whole.</summary>
    public long RemainingTicks(long tick) => Math.Max(0, ReadyTick - tick);
}

/// <summary>What one crossing costs, in each resource.</summary>
/// <param name="Materials">Steel, timber and concrete.</param>
/// <param name="Energy">Plant, welding and pumping.</param>
/// <param name="Water">Concrete needs a great deal of it.</param>
public readonly record struct BridgeCost(int Materials, int Energy, int Water);

/// <summary>
/// The crossings on the map: where each one runs, how much of it is up, and what one costs.
/// <para>
/// A bridge used to be a surface change and nothing else — one tick, one command, water
/// turned into ford with no trace that anything had been built and nothing on screen to
/// show for it. It is engineering work now, which is what the rest of the game already
/// says it is: the resources are spent when the order is given, the deck goes up cell by
/// cell from one bank, and the water it has reached becomes ford as it arrives. The
/// player watches the crossing happen instead of being told it has.
/// </para>
/// <para>
/// What a crossing costs is the same shape as what it takes: a fixed preparation — survey,
/// plant, materials on site — and then so much for every cell of the span. A ford a hundred
/// metres wide is not the same undertaking as a ditch, and a price that did not know the
/// difference made the long crossing the bargain and the short one the rip-off.
/// </para>
/// <para>
/// Kept as state rather than inferred from the terrain, because the terrain cannot tell a
/// ford a player built from a ford the generator carved to keep islands reachable. The
/// client draws the deck from these spans, so a bridge that exists only as a surface type
/// would be a bridge nobody could see.
/// </para>
/// <para>
/// Deterministic: the work done is a function of the tick and the span, the cells are
/// converted in span order, and nothing here reads the clock or the RNG.
/// </para>
/// </summary>
public sealed class Bridgeworks
{
    /// <summary>Crossings one map may carry. Engineering is finite, and the client draws every one.</summary>
    public const int MaxBridges = 64;

    /// <summary>
    /// Ticks of work before the first cell is placed: survey, piling and materials on site.
    /// Without it a one-cell crossing would be instant and a long one would look like it
    /// started before the order was given.
    /// </summary>
    public const int SetupTicks = 30;

    /// <summary>Ticks of work each cell of the span costs. The deck grows one cell every 1.5 s.</summary>
    public const int TicksPerCell = 30;

    /// <summary>Materials the site itself costs, before any span is laid across it.</summary>
    public const int SetupMaterials = 30;

    /// <summary>Materials per cell of span: the deck, the rails and the piling under them.</summary>
    public const int MaterialsPerCell = 20;

    /// <summary>Energy the site itself costs.</summary>
    public const int SetupEnergy = 10;

    /// <summary>Energy per cell: plant running, and welding that does not stop for twelve seconds.</summary>
    public const int EnergyPerCell = 4;

    /// <summary>Water the site itself costs. Concrete is poured from the first cell onward.</summary>
    public const int SetupWater = 8;

    /// <summary>Water per cell.</summary>
    public const int WaterPerCell = 3;

    private readonly int[] _cells = new int[MaxBridges * SimWorld.MaxBridgeCells];
    private readonly int[] _length = new int[MaxBridges];
    private readonly int[] _built = new int[MaxBridges];
    private readonly long[] _startTick = new long[MaxBridges];

    private int _count;

    /// <summary>How many crossings are on the map, finished or not.</summary>
    public int Count => _count;

    /// <summary>True while there is room for another crossing.</summary>
    public bool HasRoom => _count < MaxBridges;

    /// <summary>Ticks of work a span of this many cells takes, from the order to the last cell.</summary>
    public static int TicksFor(int cells) => SetupTicks + (Math.Max(0, cells) * TicksPerCell);

    /// <summary>
    /// What a crossing of this many cells costs. The cheapest crossing a player can order is a
    /// single cell — <see cref="SetupMaterials"/> and the two other setup costs, plus one cell of
    /// span — and that is what a team has to be able to afford before the button is worth
    /// pressing, since a button cannot know the site.
    /// </summary>
    public static BridgeCost Cost(int cells)
    {
        int span = Math.Max(0, cells);

        return new BridgeCost(
            SetupMaterials + (span * MaterialsPerCell),
            SetupEnergy + (span * EnergyPerCell),
            SetupWater + (span * WaterPerCell));
    }

    /// <summary>The cells of one crossing, in the order the work reaches them: bank to bank.</summary>
    public ReadOnlySpan<int> Cells(int bridge) => _cells.AsSpan(bridge * SimWorld.MaxBridgeCells, _length[bridge]);

    /// <summary>How far along one crossing is.</summary>
    public BridgeState State(int bridge) => new(
        _length[bridge],
        _built[bridge],
        _startTick[bridge],
        _startTick[bridge] + TicksFor(_length[bridge]));

    /// <summary>
    /// Starts a crossing. The span is already validated and paid for; this only records it,
    /// with no cell placed yet — the work begins on the next <see cref="Tick"/>.
    /// </summary>
    internal bool Begin(ReadOnlySpan<int> cells, long tick)
    {
        if (!HasRoom || cells.IsEmpty || cells.Length > SimWorld.MaxBridgeCells)
        {
            return false;
        }

        Span<int> span = _cells.AsSpan(_count * SimWorld.MaxBridgeCells, cells.Length);
        cells.CopyTo(span);

        _length[_count] = cells.Length;
        _built[_count] = 0;
        _startTick[_count] = tick;
        _count++;
        return true;
    }

    /// <summary>
    /// Advances every crossing's work and lays the deck on whatever ground it has reached.
    /// <para>
    /// The completion of a cell is a function of the tick it started on, so a replay rebuilds
    /// the same bridge on the same tick without any of it being replayed command by command.
    /// </para>
    /// </summary>
    public void Tick(TerrainLayer terrain, long tick)
    {
        ArgumentNullException.ThrowIfNull(terrain);

        for (int bridge = 0; bridge < _count; bridge++)
        {
            int total = _length[bridge];
            int built = _built[bridge];

            if (built >= total)
            {
                continue;
            }

            long elapsed = tick - _startTick[bridge];
            int reached = elapsed <= 0 ? 0 : (int)Math.Min((elapsed - SetupTicks) / TicksPerCell, total);
            int offset = bridge * SimWorld.MaxBridgeCells;

            // One cell at a time, so the ford and the deck appear together: the tick a cell
            // becomes crossable is the tick the work reached it.
            while (built < reached)
            {
                terrain.SetType(_cells[offset + built], TerrainType.ShallowWater);
                built++;
            }

            _built[bridge] = built;
        }
    }
}
