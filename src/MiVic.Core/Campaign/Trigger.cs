using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// What a trigger waits for.
/// <para>
/// Five questions, and each one is a predicate over world state rather than a callback: a
/// mission is data, so it may only ask things the simulation can answer from itself, and
/// the answer has to be the same on two machines running the same match.
/// </para>
/// </summary>
public enum TriggerConditionKind : byte
{
    /// <summary><see cref="TriggerCondition.Tick"/> ticks have elapsed.</summary>
    TimeElapsed = 0,

    /// <summary>
    /// At least <see cref="TriggerCondition.Count"/> units of
    /// <see cref="TriggerCondition.Team"/> stand inside the circle.
    /// </summary>
    UnitInArea = 1,

    /// <summary>
    /// <see cref="TriggerCondition.Team"/> has lost at least
    /// <see cref="TriggerCondition.Count"/> structures. This reads the same ledger
    /// <see cref="ObjectiveKind.DestroyStructures"/> reads, so a structure rebuilt later does not
    /// undo it.
    /// </summary>
    StructureDestroyed = 2,

    /// <summary>
    /// Fewer than <see cref="TriggerCondition.Count"/> of <see cref="TriggerCondition.Team"/>'s
    /// structures stand. <see cref="TriggerCondition.Role"/> narrows it to one role, and
    /// <see cref="UnitKind.None"/> counts every structure the team has.
    /// </summary>
    StructuresBelow = 3,

    /// <summary>An earlier trigger set the flag; see <see cref="TriggerActionKind.SetFlag"/>.</summary>
    FlagSet = 4,
}

/// <summary>What a trigger does when it fires.</summary>
public enum TriggerActionKind : byte
{
    /// <summary>Show the player <see cref="TriggerAction.GreekText"/>.</summary>
    Message = 0,

    /// <summary>Raise <see cref="TriggerAction.Flag"/>, which a later trigger may wait on.</summary>
    SetFlag = 1,

    /// <summary>
    /// Put <see cref="TriggerAction.Count"/> of <see cref="TriggerAction.Role"/> on the map for
    /// <see cref="TriggerAction.Team"/>, around the point.
    /// </summary>
    Spawn = 2,

    /// <summary>
    /// Lift the fog for <see cref="TriggerAction.Team"/> over the disc, so the ground inside it
    /// is watched — and remembered afterwards, as any ground a team has seen is.
    /// </summary>
    Reveal = 3,

    /// <summary>
    /// Add <see cref="TriggerAction.Materials"/>, <see cref="TriggerAction.Energy"/> and
    /// <see cref="TriggerAction.Water"/> to a team's stockpiles. An amount may be negative, and
    /// a stockpile floors at zero.
    /// </summary>
    AdjustResources = 4,

    /// <summary>
    /// Give every unit of <see cref="TriggerAction.Team"/> inside the circle an order: move to
    /// the target point, or attack the nearest hostile thing to it.
    /// </summary>
    OrderGroup = 5,

    /// <summary>Bring objective <see cref="TriggerAction.Objective"/> to complete.</summary>
    CompleteObjective = 6,
}

/// <summary>What a group order asks for.</summary>
public enum GroupOrder : byte
{
    /// <summary>March to <see cref="TriggerAction.TargetX"/>, <see cref="TriggerAction.TargetZ"/>.</summary>
    Move = 0,

    /// <summary>Attack the nearest hostile entity to the target point.</summary>
    Attack = 1,
}

/// <summary>
/// One question a mission asks of the world, every tick, until it is answered.
/// <para>
/// One condition per trigger rather than a tree of them: a condition that needs two facts is
/// two triggers and a flag, which is the composition the flag exists for — and a list of
/// small questions is a list a reader can check against the mission's own writing, which a
/// boolean expression is not.
/// </para>
/// </summary>
/// <param name="Kind">The question being asked.</param>
/// <param name="Team">The team the question is about.</param>
/// <param name="Count">Units or structures required, for the counting conditions.</param>
/// <param name="Tick">Ticks that must have elapsed, for <see cref="TriggerConditionKind.TimeElapsed"/>.</param>
/// <param name="CentreX">Centre of the area, in millimetres.</param>
/// <param name="CentreZ">Centre of the area, in millimetres.</param>
/// <param name="RadiusMm">Radius of the area, in millimetres.</param>
/// <param name="Role">
/// A structure role to count, for <see cref="TriggerConditionKind.StructuresBelow"/>;
/// <see cref="UnitKind.None"/> counts every structure the team has.
/// </param>
/// <param name="Flag">Flag index, for <see cref="TriggerConditionKind.FlagSet"/>.</param>
public readonly record struct TriggerCondition(
    TriggerConditionKind Kind,
    int Team = 0,
    int Count = 1,
    int Tick = 0,
    int CentreX = 0,
    int CentreZ = 0,
    int RadiusMm = 0,
    UnitKind Role = UnitKind.None,
    int Flag = 0);

/// <summary>
/// One thing a trigger does.
/// <para>
/// A wide record with a field per action kind, the way <see cref="ObjectiveDefinition"/> is: the
/// alternative is a class hierarchy for something that is only ever data, and every field a
/// mission does not use is a default rather than a mistake.
/// </para>
/// </summary>
/// <param name="Kind">What to do.</param>
/// <param name="Team">Team the action is for.</param>
/// <param name="Role">What to spawn, for <see cref="TriggerActionKind.Spawn"/>.</param>
/// <param name="Count">How many to spawn.</param>
/// <param name="CentreX">Where to spawn, or the centre of a group or a reveal, in millimetres.</param>
/// <param name="CentreZ">Where to spawn, or the centre of a group or a reveal, in millimetres.</param>
/// <param name="RadiusMm">Radius of the group or the revealed disc, in millimetres.</param>
/// <param name="Ticks">
/// How long a reveal lasts, in ticks. Zero reveals the ground for the rest of the mission; the
/// cells stay on the team's map either way, which is what <see cref="VisibilityGrid"/> already
/// does with ground a unit has looked at.
/// </param>
/// <param name="Materials">Materials added, for <see cref="TriggerActionKind.AdjustResources"/>.</param>
/// <param name="Energy">Energy added, for <see cref="TriggerActionKind.AdjustResources"/>.</param>
/// <param name="Water">Water added, for <see cref="TriggerActionKind.AdjustResources"/>.</param>
/// <param name="Order">What the group is ordered to do, for <see cref="TriggerActionKind.OrderGroup"/>.</param>
/// <param name="TargetX">Where the group is sent, or the point it attacks towards, in millimetres.</param>
/// <param name="TargetZ">Where the group is sent, or the point it attacks towards, in millimetres.</param>
/// <param name="GreekText">What the player is told, for <see cref="TriggerActionKind.Message"/>.</param>
/// <param name="Flag">Flag to raise, for <see cref="TriggerActionKind.SetFlag"/>.</param>
/// <param name="Objective">
/// Index into <see cref="MissionDefinition.Objectives"/>, for
/// <see cref="TriggerActionKind.CompleteObjective"/>.
/// </param>
public readonly record struct TriggerAction(
    TriggerActionKind Kind,
    int Team = 0,
    UnitKind Role = UnitKind.None,
    int Count = 1,
    int CentreX = 0,
    int CentreZ = 0,
    int RadiusMm = 0,
    int Ticks = 0,
    int Materials = 0,
    int Energy = 0,
    int Water = 0,
    GroupOrder Order = GroupOrder.Move,
    int TargetX = 0,
    int TargetZ = 0,
    string GreekText = "",
    int Flag = 0,
    int Objective = 0);

/// <summary>
/// One line of a mission's script: a condition, the actions it carries out, and the note that
/// says why the mission's author put it there.
/// <para>
/// <see cref="Id"/> is not an index: it is what the transcript, the test that counts the fires
/// and the validation that every trigger <em>can</em> fire all name the trigger by, so that a
/// list of five triggers stays a piece of writing rather than a list of five numbers.
/// </para>
/// <para>
/// <b>A trigger fires at most once, and that is the whole of what it remembers.</b> A condition
/// that is still true next tick does nothing, because the trigger has already happened — which is
/// what makes "when the enemy reaches the ridge" one event rather than an event every tick.
/// </para>
/// </summary>
/// <param name="Id">Stable name, unique within the mission.</param>
/// <param name="Condition">What the trigger waits for.</param>
/// <param name="Actions">What it does, in order, when the condition holds.</param>
/// <param name="Note">Why the mission needs this trigger, in the author's own words.</param>
public sealed record TriggerDefinition(
    string Id,
    TriggerCondition Condition,
    IReadOnlyList<TriggerAction> Actions,
    string Note = "");

/// <summary>
/// What one trigger remembers. Part of the simulation state, so it is hashed.
/// <para>
/// <b>This is memory, and memory has to be hashed.</b> A trigger that fired once must never fire
/// again, so two peers that disagreed about it would disagree about the mission from that tick
/// on — one of them springing an ambush the other had already sprung. It is the same kind of
/// field as <see cref="ObjectiveState"/> and the production queues, and the same rule applies:
/// what a system <em>remembers</em> goes in the hash, and what a system can <em>recompute</em>
/// from the state around it — a capacity ledger, a count of what is standing — does not, because
/// hashing a derived number would put one fact in the hash twice and make the hash the thing
/// that is wrong the day the two disagree.
/// </para>
/// </summary>
public struct TriggerState
{
    /// <summary>
    /// Tick the trigger fired on, or zero for a trigger that has not fired. Zero is a sentinel
    /// rather than a tick: the first evaluation happens on tick one, so no trigger can ever fire
    /// on tick zero — the same convention <see cref="ObjectiveDefinition.DeadlineTick"/> uses.
    /// </summary>
    public long FiredTick;

    /// <summary>True once the trigger has fired.</summary>
    public readonly bool HasFired => FiredTick > 0;
}
