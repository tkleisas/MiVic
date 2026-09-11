using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// A campaign mission: a starting world plus the objectives that decide it.
/// <para>
/// The definition is immutable, compiled-in data. Everything that can change
/// during a mission lives in the world's objective states, so a mission is
/// replayable in exactly the same way a skirmish is: the seed, the mission id
/// and the command log are enough to reproduce it.
/// </para>
/// </summary>
/// <param name="Id">Stable identifier, used in replays and on the command line.</param>
/// <param name="GreekTitle">Mission name shown to the player.</param>
/// <param name="GreekBriefing">Situation report shown before the fighting starts.</param>
/// <param name="Seed">World seed; the terrain and layout derive from it.</param>
/// <param name="PlayerBase">Player command centre position.</param>
/// <param name="AllyBase">Chinese ally command centre position.</param>
/// <param name="EnemyBase">Western command centre position.</param>
/// <param name="PlayerUnits">Units the player starts with.</param>
/// <param name="AllyUnits">Units the ally starts with.</param>
/// <param name="EnemyUnits">Units the enemy starts with.</param>
/// <param name="Objectives">What the player must do.</param>
/// <param name="TimeLimitTicks">Ticks after which the mission is lost; zero means no limit.</param>
public sealed record MissionDefinition(
    string Id,
    string GreekTitle,
    string GreekBriefing,
    ulong Seed,
    WorldPos PlayerBase,
    WorldPos AllyBase,
    WorldPos EnemyBase,
    int PlayerUnits,
    int AllyUnits,
    int EnemyUnits,
    IReadOnlyList<ObjectiveDefinition> Objectives,
    int TimeLimitTicks = 0)
{
    /// <summary>Objectives that decide the mission, as opposed to bonus ones.</summary>
    public IEnumerable<ObjectiveDefinition> PrimaryObjectives => Objectives.Where(objective => objective.IsPrimary);

    /// <summary>
    /// What happens during the mission, and when: a list of triggers evaluated in order, every
    /// tick, by <see cref="TriggerSystem"/>.
    /// <para>
    /// <b>The script is data, and stays data.</b> A mission is a seed, a layout, a list of
    /// objectives and a list of triggers, all of it compiled-in and all of it hashable — so the
    /// campaign is as replayable as a skirmish, and the missions in this repository are readable
    /// by anyone editing them. A scripting language would be a project of its own and would buy
    /// nothing a list of triggers does not; see <c>docs/ROADMAP.md</c> §8.
    /// </para>
    /// <para>
    /// Empty for a mission that has nothing to say, which is most of them: the three campaign
    /// missions that shipped before this layer existed are laid out, fought and decided exactly
    /// as they were, and a match with no triggers costs nothing at all — not a tick of work, and
    /// not a byte of the state hash.
    /// </para>
    /// </summary>
    public IReadOnlyList<TriggerDefinition> Triggers { get; init; } = [];

    /// <summary>True when this mission uses the trigger layer at all.</summary>
    public bool HasTriggers => Triggers.Count > 0;

    /// <summary>
    /// How many flags this mission's triggers use between them: one more than the highest flag
    /// index any condition reads or action raises, and zero for a mission that uses none.
    /// <para>
    /// Flags are the mission's own memory — a trigger raises one so that a later trigger can wait
    /// on it — so the world allocates a word per flag this says the mission needs, and hashes
    /// exactly those words. A mission that declares no flags therefore has no flag state, which
    /// is what keeps a triggerless mission's hash byte-identical to what it was before flags
    /// existed.
    /// </para>
    /// </summary>
    public int DeclaredFlagCount
    {
        get
        {
            int highest = -1;

            foreach (TriggerDefinition trigger in Triggers)
            {
                if (trigger.Condition.Kind == TriggerConditionKind.FlagSet)
                {
                    highest = Math.Max(highest, trigger.Condition.Flag);
                }

                foreach (TriggerAction action in trigger.Actions)
                {
                    if (action.Kind == TriggerActionKind.SetFlag)
                    {
                        highest = Math.Max(highest, action.Flag);
                    }
                }
            }

            return highest + 1;
        }
    }

    /// <summary>
    /// Looks a trigger up by id, or -1 when the mission has no such trigger. By id rather than
    /// by index because a trigger is named by the test that counts its fires and by the
    /// transcript that shows it firing.
    /// </summary>
    public int IndexOfTrigger(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        for (int i = 0; i < Triggers.Count; i++)
        {
            if (string.Equals(Triggers[i].Id, id, StringComparison.Ordinal))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>
    /// Who is fighting this mission: which teams are in it, what faction each one plays and which
    /// side each one is on.
    /// <para>
    /// The campaign's ally used to be a column of the scenario builder — team 1, given
    /// <see cref="AllyBase"/> and <see cref="AllyUnits"/>, always and only that team. It is a side
    /// the mission says it has instead, so a mission can be fought with two sides rather than
    /// three, and <see cref="Sim.Scenario.BuildMission"/> lays out a force for every team this
    /// declares rather than for three teams by number.
    /// </para>
    /// <para>
    /// The base, the starting force and the objectives of each team are still the mission's data,
    /// keyed by team slot: this says <em>who is playing</em>, not where they stand. The three
    /// missions the campaign ships therefore declare the standard three-faction skirmish and are
    /// laid out exactly as they were.
    /// </para>
    /// </summary>
    public Sim.MatchRoster Roster { get; init; } = Sim.MatchRoster.StandardSkirmish;
}
