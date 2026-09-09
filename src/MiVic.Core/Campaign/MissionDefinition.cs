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
}
