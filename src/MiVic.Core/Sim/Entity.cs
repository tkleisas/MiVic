using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// A simulated unit or structure. Plain data, no behaviour: systems act on it.
/// <para>
/// Field order matters for the state hash, which walks the struct
/// deterministically. Add fields at the end and update
/// <see cref="StateHash"/> at the same time.
/// </para>
/// </summary>
public struct Entity
{
    /// <summary>Reuse counter matching <see cref="EntityId.Generation"/>.</summary>
    public int Generation;

    /// <summary>False once the entity is destroyed; the slot is then recycled.</summary>
    public bool Alive;

    /// <summary>Which power owns this entity.</summary>
    public Faction Faction;

    /// <summary>Diplomatic team. Entities on the same team never fight.</summary>
    public int TeamId;

    /// <summary>Coarse role, used for movement envelope and later for combat.</summary>
    public UnitKind Kind;

    /// <summary>Current position in integer millimetres.</summary>
    public WorldPos Position;

    /// <summary>Destination of the current move order.</summary>
    public WorldPos MoveGoal;

    /// <summary>True while <see cref="MoveGoal"/> is being pursued.</summary>
    public bool HasMoveGoal;

    /// <summary>Ground speed in millimetres per tick.</summary>
    public Fix32 SpeedMmPerTick;

    /// <summary>
    /// Morale in [0, 1]. The signature weakness of Δυτικοί and the reason the
    /// Western army can out-tech everyone and still lose. Present from M0 so it
    /// is part of the state hash from the start.
    /// </summary>
    public Fix32 Morale;

    /// <summary>Hit points. Combat arrives in M3.</summary>
    public int Health;

    /// <summary>Facing in binary radians (65536 = full turn), for the renderer.</summary>
    public ushort Heading;

    /// <summary>Height above the terrain surface, in millimetres. Zero for ground units.</summary>
    public int AltitudeMm;

    /// <summary>Waypoints in this entity's current path.</summary>
    public int PathLength;

    /// <summary>Next waypoint to walk towards.</summary>
    public int PathCursor;

    /// <summary>Production jobs queued at this building.</summary>
    public int QueueLength;

    /// <summary>Slot of the entity this unit is engaging, or -1.</summary>
    public int TargetSlot;

    /// <summary>Ticks until this unit may fire again.</summary>
    public int AttackCooldown;

    /// <summary>True when the player explicitly ordered an attack on <see cref="TargetSlot"/>.</summary>
    public bool HasAttackOrder;

    /// <summary>True when morale has collapsed and the unit is falling back.</summary>
    public bool Routed;

    /// <summary>
    /// Cached morale target, recomputed a few times a second. The proximity scan
    /// behind it is the most expensive query in the tick, and morale does not need
    /// 20 Hz precision to feel right.
    /// </summary>
    public int MoraleTargetRaw;

    /// <summary>
    /// True when the entity is waiting for a route to be computed. Path searches
    /// are budgeted per tick, so an order does not stall the frame it arrives on.
    /// </summary>
    public bool NeedsPath;

    /// <summary>Consecutive routing failures; the move order is dropped after a few.</summary>
    public int PathFailures;

    /// <summary>
    /// Tick until which a stealthy entity is visible to the enemy. Set when it
    /// fires: a shot gives away a position, which is the whole cost of stealth.
    /// </summary>
    public long RevealedUntilTick;

    /// <summary>
    /// Ground distance this entity has covered since it spawned, in millimetres.
    /// <para>
    /// The simulation carries it because the wheels are a presentation concern but
    /// the distance is not: a wheel's angle is a pure function of how far the unit
    /// has travelled, so replaying a match turns the wheels exactly as the original
    /// did. Deriving it in the client from frame-to-frame movement would make the
    /// animation depend on frame rate instead.
    /// </para>
    /// </summary>
    public long DistanceTravelledMm;
}
