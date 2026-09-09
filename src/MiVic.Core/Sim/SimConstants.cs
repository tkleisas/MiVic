namespace MiVic.Core.Sim;

/// <summary>
/// Fixed constants of the simulation. The tick rate is part of the determinism
/// contract: replays and lockstep peers must agree on it exactly.
/// </summary>
public static class SimConstants
{
    /// <summary>Simulation ticks per second. Rendering interpolates between ticks.</summary>
    public const int TickRate = 20;

    /// <summary>Wall-clock duration of one tick, in milliseconds.</summary>
    public const int TickMilliseconds = 1000 / TickRate;

    /// <summary>Wall-clock duration of one tick, in microseconds (exact).</summary>
    public const long TickMicroseconds = 1_000_000L / TickRate;

    /// <summary>
    /// Upper bound on catch-up ticks per frame. Prevents the classic death
    /// spiral when a frame takes far longer than a tick.
    /// </summary>
    public const int MaxTicksPerAdvance = 5;

    /// <summary>Hard cap on live entities. Keeps the state hash and replays bounded.</summary>
    public const int MaxEntities = 8192;

    /// <summary>
    /// Largest single-tick step any unit may take, in millimetres. Bounds the
    /// <c>delta * step</c> intermediate in the movement system well inside
    /// <see cref="long"/>.
    /// </summary>
    public const int MaxStepPerTickMm = 100_000;

    /// <summary>Side length of the battlefield, in millimetres.</summary>
    public const int MapExtentMm = 600_000;

    /// <summary>Terrain samples per side.</summary>
    public const int TerrainResolution = 129;

    /// <summary>Highest terrain elevation, in millimetres.</summary>
    public const int TerrainMaxHeightMm = 42_000;

    /// <summary>Slope above which ground is impassable, in millimetres per metre.</summary>
    public const int MaxSlopePermille = 900;

    /// <summary>Waypoints stored per entity. Longer routes are walked in legs.</summary>
    public const int MaxPathCells = 96;

    /// <summary>Distance at which a unit considers itself to have arrived, in millimetres.</summary>
    public const int ArrivalRadiusMm = 4_700;

    /// <summary>Production jobs a single building may hold.</summary>
    public const int MaxQueueLength = 8;

    /// <summary>Number of teams the simulation tracks resources for.</summary>
    public const int TeamCount = 4;

    /// <summary>
    /// Path searches allowed per tick. A hundred units receiving orders in the
    /// same tick would otherwise run a hundred A* searches in that tick, which is
    /// visible as a multi-hundred-millisecond hitch.
    /// </summary>
    public const int MaxPathsPerTick = 4;

    /// <summary>Height-map samples per navigation cell.</summary>
    public const int NavGridStride = 2;
}
