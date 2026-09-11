namespace MiVic.Core.Campaign;

/// <summary>What an objective asks of the player.</summary>
public enum ObjectiveKind : byte
{
    /// <summary>Destroy <see cref="ObjectiveDefinition.TargetCount"/> enemy structures.</summary>
    DestroyStructures = 0,

    /// <summary>Keep units inside a circle for a stretch of time.</summary>
    HoldArea = 1,

    /// <summary>Reach a tick with the team still standing.</summary>
    SurviveTicks = 2,

    /// <summary>Your command centre must still exist at the deadline.</summary>
    ProtectCommandCentre = 3,

    /// <summary>Stockpile materials.</summary>
    AccumulateMaterials = 4,

    /// <summary>Reach a technology tier.</summary>
    ReachTechTier = 5,

    /// <summary>
    /// <b>Denial: the target team must not get <see cref="ObjectiveDefinition.TargetCount"/> units
    /// into the circle before the deadline.</b>
    /// <para>
    /// Every other kind here is something a player <em>does</em> — destroy, hold, accumulate,
    /// reach — and none of them can say "the enemy must not". That is the shape a campaign keeps
    /// needing: one side wins by getting something out and the other wins by preventing it, and
    /// there was no way to write the second half of that sentence.
    /// </para>
    /// <para>
    /// It is the mirror of <see cref="HoldArea"/>, and deliberately not its twin. Holding is
    /// something you keep doing, so the hold clock can be lost and started again; denial is a
    /// fact about what did or did not happen, so it fails the moment the enemy achieves it and
    /// cannot be recovered — the units that got through got through. What completes it is the
    /// deadline passing with the area still clear, which is why a denial with no
    /// <see cref="ObjectiveDefinition.DeadlineTick"/> can never be won: there is no moment at
    /// which "they never arrived" becomes true.
    /// </para>
    /// <para>
    /// It is asked of <see cref="ObjectiveDefinition.TargetTeam"/> — the team that must be
    /// denied — and judged for <see cref="ObjectiveDefinition.Team"/>, so a mission can hand the
    /// denial to either side.
    /// </para>
    /// </summary>
    DenyArea = 6,

    /// <summary>
    /// <b>An objective the mission itself decides.</b> Nothing in the world satisfies it: it is
    /// brought to complete by <see cref="TriggerActionKind.CompleteObjective"/>, which is how a
    /// campaign hangs an objective on an event rather than on a predicate — "silence the guns on
    /// the ridge" is a thing the mission knows happened and the objective table does not.
    /// <para>
    /// It is the one kind that can be authored and never happen, which is a bug this project has
    /// been bitten by three times over, so every shipped mission's scripted objectives are
    /// checked against the triggers that complete them —
    /// <c>EveryScriptedObjectiveIsCompletedByATrigger</c>. A deadline on one behaves as a
    /// deadline does anywhere else: past it, a scripted objective fails.
    /// </para>
    /// </summary>
    Scripted = 7,
}

/// <summary>Where an objective stands.</summary>
public enum ObjectiveStatus : byte
{
    /// <summary>Not yet satisfied, and not yet failed.</summary>
    Pending = 0,

    /// <summary>Satisfied.</summary>
    Complete = 1,

    /// <summary>Failed, which loses the mission if the objective is primary.</summary>
    Failed = 2,
}

/// <summary>
/// One mission objective, as data.
/// <para>
/// Objectives are declarative rather than scripted callbacks: every one of them
/// is a predicate over world state, evaluated on a fixed interval. That keeps the
/// campaign inside the determinism contract — a mission cannot do anything a
/// replay could not reproduce — and it means the same definitions drive the
/// simulation, the HUD and the tests.
/// </para>
/// </summary>
/// <param name="Kind">What is being asked.</param>
/// <param name="GreekDescription">Player-facing text.</param>
/// <param name="Team">Team the objective judges.</param>
/// <param name="TargetTeam">Team the objective is about, for enemy-facing goals.</param>
/// <param name="TargetCount">Units, structures or ticks required.</param>
/// <param name="HoldTicks">Ticks the area must be held, for <see cref="ObjectiveKind.HoldArea"/>.</param>
/// <param name="CentreX">Area centre X in millimetres.</param>
/// <param name="CentreZ">Area centre Z in millimetres.</param>
/// <param name="RadiusMm">Area radius in millimetres.</param>
/// <param name="MaterialsTarget">Stockpile required, for <see cref="ObjectiveKind.AccumulateMaterials"/>.</param>
/// <param name="TierTarget">Tier required, for <see cref="ObjectiveKind.ReachTechTier"/>.</param>
/// <param name="DeadlineTick">Tick by which the objective must be satisfied; zero means no deadline.</param>
/// <param name="IsPrimary">Whether failing this objective loses the mission.</param>
/// <param name="Constraint">
/// A constraint never gates victory: it can only fail the mission. "Keep your
/// command centre alive" is a constraint — the mission is won by the other
/// objectives, and this one just has to hold until then. A non-constraint
/// primary objective must be satisfied for the mission to be won.
/// </param>
public sealed record ObjectiveDefinition(
    ObjectiveKind Kind,
    string GreekDescription,
    int Team = 0,
    int TargetTeam = 2,
    int TargetCount = 1,
    int HoldTicks = 0,
    int CentreX = 0,
    int CentreZ = 0,
    int RadiusMm = 0,
    int MaterialsTarget = 0,
    int TierTarget = 0,
    int DeadlineTick = 0,
    bool IsPrimary = true,
    bool Constraint = false);

/// <summary>
/// Runtime progress of one objective. Part of the simulation state, so it is
/// hashed: two replays of a mission must agree on when each objective flipped.
/// </summary>
public struct ObjectiveState
{
    /// <summary>Where the objective stands.</summary>
    public ObjectiveStatus Status;

    /// <summary>Counter the objective is accumulating, e.g. structures destroyed.</summary>
    public int Progress;

    /// <summary>Ticks the area has been held so far.</summary>
    public int HoldProgress;

    /// <summary>Last tick the objective was evaluated, for diagnostics.</summary>
    public int LastEvaluatedTick;

    /// <summary>True when the objective has been satisfied.</summary>
    public readonly bool IsComplete => Status == ObjectiveStatus.Complete;

    /// <summary>True when the objective has been failed.</summary>
    public readonly bool IsFailed => Status == ObjectiveStatus.Failed;

    /// <summary>True while the objective is still open.</summary>
    public readonly bool IsPending => Status == ObjectiveStatus.Pending;
}
