using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>Which of the reasons a match records a defeat for.</summary>
public enum DefeatCause : byte
{
    /// <summary>
    /// A primary objective failed, and <see cref="DefeatVerdict.Objective"/> is the one that did.
    /// </summary>
    ObjectiveFailed = 0,

    /// <summary>
    /// The mission's clock ran out with a primary objective still open and none failed — the second
    /// and last way <see cref="MissionSystem"/> reaches a defeat, and the one with nothing else to name.
    /// </summary>
    TimeExpired = 1,

    /// <summary>
    /// The player's side holds no structures, which is the victory rule's own defeat.
    /// </summary>
    SideDestroyed = 2,

    /// <summary>
    /// A defeat with the side still holding something that neither a failed objective nor an expired
    /// clock accounts for. The one world that reaches it is the rule's defeat over a side that holds
    /// ground the rule never weighed — a match declaring a team it does not judge beside one it does,
    /// on the player's own side, where the judged team's fall ends the match and the unjudged team's
    /// buildings are still standing. It is the reason a line over this verdict may claim no
    /// destruction: the ground is there to be walked through.
    /// </summary>
    SideStanding = 3,
}

/// <summary>What the world records about a defeat: why it happened, and which objective it happened by.</summary>
/// <param name="Cause">The reason the world reached.</param>
/// <param name="Objective">
/// The primary objective that failed, for <see cref="DefeatCause.ObjectiveFailed"/>; null for every
/// other cause, which has no objective to name.
/// </param>
public readonly record struct DefeatVerdict(DefeatCause Cause, ObjectiveDefinition? Objective)
{
    /// <summary>
    /// The name of the objective that failed, or an empty string for a cause that has none — the
    /// interface's half of <see cref="Objective"/>, so a line of copy naming the objective is written
    /// without a condition of its own over a fact only this layer can answer.
    /// </summary>
    public string FailedObjectiveDescription => Objective?.GreekDescription ?? string.Empty;
}

/// <summary>
/// <b>Why the player's side lost, asked of the world rather than worked out from the outcome.</b>
/// <para>
/// A defeat is one word and several events. The victory rule calls a side beaten when it holds no
/// structures; a mission is lost by a primary objective failing, or by its clock running out with the
/// objectives still open and both armies standing — the demonstration mission is lost with sixteen
/// units and four structures alive on each side, and <c>m4_pass</c> is lost that way by a player who
/// lets the ambush stand until the clock runs out. The interface has to say which of those happened,
/// and it may not guess: the banner drawn over that first match said the player's side had been
/// destroyed, which is a destruction that did not happen. So the fact is answered here, the way
/// <see cref="VictorySystem.CountSideStructures"/> was added for the banner's earlier question — and
/// answered where a test can ask it, so the copy and the facts cannot drift apart.
/// </para>
/// <para>
/// <b>The questions are asked in the order the simulation asks them, and every answer is a fact the
/// world records rather than an inference from the outcome.</b> <see cref="MissionSystem"/> reports a
/// failed primary objective before it looks at its clock, so a match that lost an objective is
/// reported as having lost that objective even where the clock would also have run out — and at the
/// tick a mission's limit arrives, an objective whose own deadline is that same limit fails on the
/// same evaluation, which is why naming the objective is the more specific of the two true answers.
/// The rule's defeat is asked last and only of a match no mission decided, because a mission replaces
/// the rule rather than joining it.
/// </para>
/// <para>
/// It describes a defeat; asked about a world still being fought it describes what that world records
/// at that moment, which is all it claims to do — an objective that has failed, or a clock that has
/// passed, or a side that holds nothing. It reads state and writes none, so asking costs a replay
/// nothing and moves no hash.
/// </para>
/// </summary>
public static class DefeatReport
{
    /// <summary>The defeat this world records, or its nearest thing while a match is still being played.</summary>
    public static DefeatVerdict Of(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        MissionDefinition? mission = world.Mission;

        if (mission is not null)
        {
            if (FailedPrimaryObjective(world, mission) is { } failed)
            {
                return new DefeatVerdict(DefeatCause.ObjectiveFailed, failed);
            }

            // The clock is read rather than assumed from the defeat. A mission's defeat is reached by
            // one of these two questions and by nothing else, but a report that inferred the clock
            // from the outcome would announce a time that ran out of a match lost some other way, and
            // the whole point of asking the world is that the answer is the world's.
            if (mission.TimeLimitTicks > 0 && world.Tick >= mission.TimeLimitTicks)
            {
                return new DefeatVerdict(DefeatCause.TimeExpired, null);
            }
        }

        // And the rule's own question, which is the only one the destroyed line may rest on: a side
        // that holds nothing. Asked of the side rather than of a team, because a side is what the rule
        // counts — and asked side-wide, so it answers "nothing" only where there is nothing at all to
        // stand on, whatever the rule did or did not weigh.
        return VictorySystem.SideHasStructures(world, world.Roster.PlayerSide)
            ? new DefeatVerdict(DefeatCause.SideStanding, null)
            : new DefeatVerdict(DefeatCause.SideDestroyed, null);
    }

    /// <summary>
    /// The first primary objective that has failed, in declaration order, or null when none has.
    /// <para>
    /// Primary, because a secondary objective failing loses nothing: <see cref="MissionSystem"/> skips
    /// those when it asks whether the mission is lost, and a report that counted them would name an
    /// objective the match was never lost by. In declaration order, so a mission with two failures
    /// always reports the same one.
    /// </para>
    /// </summary>
    private static ObjectiveDefinition? FailedPrimaryObjective(SimWorld world, MissionDefinition mission)
    {
        ReadOnlySpan<ObjectiveState> states = world.Objectives;

        for (int i = 0; i < mission.Objectives.Count && i < states.Length; i++)
        {
            if (states[i].IsFailed && mission.Objectives[i].IsPrimary)
            {
                return mission.Objectives[i];
            }
        }

        return null;
    }
}
