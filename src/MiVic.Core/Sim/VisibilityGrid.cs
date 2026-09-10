namespace MiVic.Core.Sim;

/// <summary>
/// What each team can currently see, and what it has seen before.
/// <para>
/// Two separate facts matter in an RTS: <em>visible</em> cells show live enemy
/// units, while <em>explored</em> cells show remembered terrain. Keeping them
/// apart is what makes scouting meaningful — you keep the map you have uncovered
/// but lose the ability to watch it.
/// </para>
/// <para>
/// <b>There is a third channel, and it is not a second vision system.</b> A cell
/// is <em>detected</em> when a sensor looked at it closely enough to find something
/// that is hiding: the same disc <see cref="VisionSystem"/> stamps for fog, scaled by
/// <see cref="VisionSystem.StealthDetectionPermille"/>. It lives here, in the same
/// class, written by the same loop on the same tick, for one reason — "can team 0 see
/// this cell" and "can team 0 see the stalker standing on it" have to be answered by
/// the same pass over the same numbers, or a radar will light one and not the other
/// and the fog will show a man the guns cannot shoot.
/// </para>
/// </summary>
public sealed class VisibilityGrid
{
    /// <summary>
    /// Fog level for ground a team remembers but cannot currently see. Not one,
    /// because remembered ground should still read as terrain rather than as a
    /// hole in the map.
    /// </summary>
    public const byte RememberedFogLevel = 150;

    /// <summary>Fog level for ground a team has never seen.</summary>
    public const byte UnknownFogLevel = 232;

    private readonly int[] _visibleTick;
    private readonly int[] _detectedTick;
    private readonly bool[] _explored;
    private long _tick;

    /// <summary>Creates a grid matching a navigation grid's resolution.</summary>
    public VisibilityGrid(int size)
    {
        if (size <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "Must be positive.");
        }

        Size = size;
        _visibleTick = new int[size * size * SimConstants.TeamCount];
        _detectedTick = new int[size * size * SimConstants.TeamCount];
        _explored = new bool[size * size * SimConstants.TeamCount];

        // Sentinel: no cell has ever been seen, so nothing is visible at tick 0.
        Array.Fill(_visibleTick, -1_000_000);
        Array.Fill(_detectedTick, -1_000_000);
    }

    /// <summary>Cells along each axis.</summary>
    public int Size { get; }

    /// <summary>Number of cells.</summary>
    public int CellCount => Size * Size;

    /// <summary>
    /// Ticks a cell stays visible after it was last stamped. Vision is staggered
    /// across ticks, so a cell marked on one tick must stay visible until its
    /// stamping unit comes round again.
    /// </summary>
    public const int VisibleWindowTicks = VisionSystem.UpdateInterval + 1;

    /// <summary>
    /// Advances the grid's clock. Called once per simulation tick before any
    /// system reads visibility.
    /// </summary>
    public void BeginTick(long tick) => _tick = tick;

    /// <summary>Marks a cell visible for a team and remembers it as explored.</summary>
    public void MarkVisible(int team, int cell)
    {
        int index = (team * CellCount) + cell;
        _visibleTick[index] = (int)_tick;
        _explored[index] = true;
    }

    /// <summary>
    /// Marks a cell as closely detected for a team. Every detected cell is also a
    /// visible one, because the disc stamped here is a fraction of the disc stamped
    /// for fog — but the two are written separately, and the fog is not derived from
    /// this, so that a future sensor that detects without looking (a seismic line, a
    /// listening post) does not have to fake having eyes.
    /// </summary>
    public void MarkDetected(int team, int cell) => _detectedTick[(team * CellCount) + cell] = (int)_tick;

    /// <summary>True when a team currently has eyes on a cell.</summary>
    public bool IsVisible(int team, int cell)
        => (uint)team < SimConstants.TeamCount &&
           _tick - _visibleTick[(team * CellCount) + cell] < VisibleWindowTicks;

    /// <summary>
    /// True when a team has looked at this cell closely enough to find a stealthed
    /// enemy on it. The question a stealth check asks, and the only one it asks.
    /// </summary>
    public bool IsDetected(int team, int cell)
        => (uint)team < SimConstants.TeamCount &&
           _tick - _detectedTick[(team * CellCount) + cell] < VisibleWindowTicks;

    /// <summary>True when a team has ever seen a cell.</summary>
    public bool IsExplored(int team, int cell)
        => (uint)team < SimConstants.TeamCount && _explored[(team * CellCount) + cell];

    /// <summary>
    /// Projects a team's knowledge into a two-channel fog mask.
    /// <para>
    /// Channel 0 is how much fog covers the cell (zero where the team has eyes
    /// on it) and channel 1 is how much of the terrain the team remembers. The
    /// client uploads the result as a texture and blends it over the ground, so
    /// the boundary between seen and unseen is filtered by the GPU instead of
    /// snapping to the cell lattice — which is what makes fog of war look like
    /// fog rather than like tiles.
    /// </para>
    /// </summary>
    /// <param name="team">Team whose knowledge is projected.</param>
    /// <param name="destination">Two bytes per cell, row-major: fog, then memory.</param>
    public void BuildFogMask(int team, Span<byte> destination)
    {
        if (destination.Length < CellCount * 2)
        {
            throw new ArgumentException(
                $"Fog mask needs {CellCount * 2} bytes, got {destination.Length}.",
                nameof(destination));
        }

        bool known = (uint)team < SimConstants.TeamCount;
        int offset = known ? team * CellCount : 0;

        for (int cell = 0; cell < CellCount; cell++)
        {
            bool visible = known && IsVisible(team, cell);
            bool explored = known && _explored[offset + cell];

            destination[(cell * 2) + 0] = visible
                ? (byte)0
                : explored ? RememberedFogLevel : UnknownFogLevel;
            destination[(cell * 2) + 1] = explored ? (byte)255 : (byte)0;
        }
    }

    /// <summary>Number of cells a team can see right now, for diagnostics.</summary>
    public int CountVisible(int team)
    {
        int count = 0;
        int offset = team * CellCount;

        for (int i = 0; i < CellCount; i++)
        {
            if (IsVisible(team, i))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Number of cells a team has ever seen, for diagnostics.</summary>
    public int CountExplored(int team)
    {
        int count = 0;
        int offset = team * CellCount;

        for (int i = 0; i < CellCount; i++)
        {
            if (_explored[offset + i])
            {
                count++;
            }
        }

        return count;
    }
}
