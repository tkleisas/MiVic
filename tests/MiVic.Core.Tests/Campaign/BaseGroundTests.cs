using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// <b>The fourth question asked of the world a mission opens in, and the first one that is about the
/// ground rather than about anything the mission says.</b>
/// <para>
/// The trigger layer was the first thing found by asking "what reads the world and is never asked
/// about the world it opens in", the objectives were the second and the victory rule the third. This
/// is the fourth, and it is not a rule at all: the scenario's base-site search has been reporting
/// whether it found room for each base since it was written — <see cref="BaseSitePlacement"/> — and
/// nothing in Core, the client or the tests ever read it. A false there is the search saying "there
/// was no room here, I fell back to the nearest single solid cell", which is a headquarters on
/// ground nothing can be built from or reached over: the same class of bug the roadmap calls a
/// headquarters standing in deep water, and one any mission can be handed by its seed.
/// </para>
/// <para>
/// The mission below is built for the case rather than bent to fit, the way the other three layers'
/// tests are: one definition, and a second copy of it with the enemy's base coordinate moved onto
/// ground the layout has to fall back for. The coordinate was found by sweeping this map with the
/// world's own search rather than picked out of the air — the ground is a function of the seed, so
/// a coordinate is only landless on the map it was found on — and the test asserts that premise
/// before it asserts anything else, so a generator that gave that corner land would fail this test
/// loudly instead of quietly testing nothing.
/// </para>
/// </summary>
public sealed class BaseGroundTests
{
    private const int Capacity = 1024;

    /// <summary>
    /// The mission's seed, and it is this one because its map has a corner with no room for a base:
    /// the standard seeds have land within the search radius of every cell on them. See
    /// <see cref="StrandedBase"/>.
    /// </summary>
    private const ulong Seed = 12345UL;

    /// <summary>
    /// A coordinate on the map this seed generates with no room for a base anywhere within
    /// <see cref="SimWorld.BaseSearchRadiusCells"/> cells of it: the search falls back to the
    /// nearest single solid cell, which on this map is 9 cells of a 121-cell yard. Found by asking
    /// <see cref="SimWorld.TryFindBaseSite"/> about every position on the map, and pinned here
    /// because a sweep at test time costs seconds and says the same thing.
    /// </summary>
    private static readonly WorldPos StrandedBase = new(-290_000, 0, 220_000);

    /// <summary>
    /// A sound mission with a base coordinate a test can move: this repository's own layout, one
    /// objective a good opening play satisfies, and a match of two teams.
    /// </summary>
    private static MissionDefinition Mission() => new(
        Id: "base_ground_test",
        GreekTitle: "Δοκιμή εδάφους βάσης",
        GreekBriefing: "Μια αποστολή γραμμένη για τη δοκιμή που την κρίνει.",
        Seed: Seed,
        PlayerBase: new WorldPos(-180_000, 0, -180_000),
        AllyBase: default,
        EnemyBase: new WorldPos(0, 0, 200_000),
        PlayerUnits: 6,
        AllyUnits: 0,
        EnemyUnits: 8,
        Objectives:
        [
            new ObjectiveDefinition(
                ObjectiveKind.ReachTechTier,
                "Φτάστε σε τεχνολογικό επίπεδο 2.",
                Team: 0,
                TierTarget: 2,
                DeadlineTick: 6_000),
        ],
        TimeLimitTicks: 7_200)
    {
        Roster = MatchRoster.Duel,
    };

    /// <summary>
    /// <b>A mission whose base had to fall back is refused, and the refusal names what happened.</b>
    /// <para>
    /// The mission is sound before the coordinate is moved, which is what makes the complaint a fact
    /// about the ground: the same definition, one wish changed, and the side opens with a
    /// headquarters that stands on solid ground with no yard around it. That is why nothing in the
    /// finished world shows it — a fallback base is not a base in the water, it is a base on a rock
    /// — and why the report the layout has always made is the only thing that can be asked.
    /// </para>
    /// <para>
    /// The sentence has to be actionable rather than true: it names the side, the ground the mission
    /// asked for, the ground it got and how much of that yard a base can use, because the remedy is
    /// the author's own and they need the coordinate they wrote.
    /// </para>
    /// </summary>
    [Fact]
    public void AMissionWhoseBaseHadToFallBackIsRefused()
    {
        MissionDefinition sound = Mission();

        Assert.Empty(TriggerSystem.Validate(sound));

        MissionDefinition stranded = sound with { EnemyBase = StrandedBase };

        // What the layout made of that coordinate, read from the same report the check reads: the
        // headquarters stands, on ground a unit can stand on, with no room for a base around it.
        // The first assertion is the test's own premise — the corner has not grown a shore — and it
        // is made here rather than assumed, so a map that changed fails by name.
        TriggerSystem.OpeningWorld(stranded, out ScenarioSetup setup);

        BaseSitePlacement site = setup.BaseSites.Single(placement => placement.Faction == Faction.Western);

        Assert.False(
            site.PatchFound,
            $"the coordinate this test calls landless at {site.Requested} has room for a base now: " +
            "sweep the map for another one");

        Assert.NotEqual(site.Requested, site.Placed);
        Assert.True(
            site.SolidCells < SimWorld.BaseSitePatchCells,
            $"the fallback yard is solid enough to be a base ({site.SolidCells} of {SimWorld.BaseSitePatchCells})");

        string complaint = Assert.Single(
            TriggerSystem.Validate(stranded), problem => problem.Contains("had to fall back", StringComparison.Ordinal));

        // Which side it is, in the two words a reader can act on.
        Assert.Contains("team 2", complaint, StringComparison.Ordinal);
        Assert.Contains("Δυτικοί", complaint, StringComparison.Ordinal);

        // The ground it asked for, and the ground it got: the metre the coordinate falls in, quoted
        // with its tenth so the fragment cannot match a neighbour.
        Assert.Contains($"(x {site.Requested.X / WorldPos.MmPerMetre}.", complaint, StringComparison.Ordinal);
        Assert.Contains($"z {site.Requested.Z / WorldPos.MmPerMetre}.", complaint, StringComparison.Ordinal);
        Assert.Contains($"(x {site.Placed.X / WorldPos.MmPerMetre}.", complaint, StringComparison.Ordinal);
        Assert.Contains($"z {site.Placed.Z / WorldPos.MmPerMetre}.", complaint, StringComparison.Ordinal);

        // And what it means: the yard it got instead of one.
        Assert.Contains(
            $"{site.SolidCells} of {SimWorld.BaseSitePatchCells} cells",
            complaint,
            StringComparison.Ordinal);
    }

    /// <summary>
    /// <b>No mission the campaign ships opens on a base the layout had to fall back for</b>, which is
    /// the claim the check exists to keep true and the reason its refusals need a mission built for
    /// the case to be seen at all.
    /// <para>
    /// Asked of the layout's own report rather than of the validator's sentences, so a mission that
    /// began falling back is caught here even if the sentence that says so changes — and the
    /// validator is asked as well, because "the content that exists validates clean" is the claim a
    /// new check is judged by.
    /// </para>
    /// </summary>
    [Fact]
    public void NoShippedMissionOpensOnABaseTheLayoutFellBackFor()
    {
        Assert.NotEmpty(MissionCatalog.All);

        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            TriggerSystem.OpeningWorld(mission, out ScenarioSetup setup);

            foreach (BaseSitePlacement site in setup.BaseSites)
            {
                Assert.True(
                    site.PatchFound,
                    $"'{mission.Id}' opens with the {site.Faction} base on ground the layout fell back for: " +
                    $"asked for {site.Requested}, stands at {site.Placed}, {site.SolidCells} of " +
                    $"{SimWorld.BaseSitePatchCells} cells of its yard solid.");
            }

            Assert.Empty(TriggerSystem.Validate(mission));
        }
    }
}
