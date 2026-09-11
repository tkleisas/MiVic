using MiVic.Core.Numerics;
using MiVic.Core.Replay;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// A match of two factions: Σοβιετικοί against Δυτικοί with no ally, or Σοβιετικοί against Κινέζοι
/// with the Δυτικοί absent.
/// <para>
/// The victory condition used to be written as <c>HasStructures(0) || HasStructures(1)</c> against
/// <c>HasStructures(2)</c>, which is a description of the standard three-faction skirmish rather
/// than of who is playing. A two-faction match was therefore undecidable in both directions: a team
/// that is not on the map has no structures, so a missing enemy read as a dead one and the match was
/// called on the first check — or the teams it did require destroyed were not playing at all. These
/// tests are the two-faction match run to a verdict, and the three-faction match asserted unchanged.
/// </para>
/// </summary>
public sealed class TwoFactionMatchTests
{
    /// <summary>Capacity of a skirmish, which the standard match is built at.</summary>
    private const int SkirmishCapacity = 1024;

    /// <summary>The initial hash of the standard skirmish, from <c>ScenarioTests</c>.</summary>
    private const ulong SkirmishInitialHash = 7771467923509982711UL;

    [Fact]
    public void ATwoFactionMatchRunsAndReachesAVerdict()
    {
        // Σοβιετικοί against Κινέζοι. The Δυτικοί are not in this match: two bases, two forces, and
        // nothing at all on the third team.
        SimWorld world = Scenario.NewWorld(ScenarioKind.Rivals, 20250101, SkirmishCapacity);
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Rivals);

        Assert.Equal(2, setup.CommandCentres.Count);
        Assert.True(world.IsTeamInPlay(0));
        Assert.True(world.IsTeamInPlay(1));
        Assert.False(world.IsTeamInPlay(2));
        Assert.Equal(0, TeamCensus(world, 2));

        // Five seconds of it, and the verdict is *not* reached. That is the headline: the Δυτικοί
        // have no structures and never will, and the old rule called the match over on its very
        // first check because a team that is not playing has nothing standing. The match has to be
        // decided by the team that is in it.
        world.RunTicks(100);

        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
        Assert.Equal(0, VictorySystem.CountStructures(world, 2));

        // Break the only enemy team, and the match is decided: the player's side has no enemies
        // left, which is the whole of the rule.
        BreakStructures(world, 1);
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Victory, world.Outcome);

        // And it was decided without the absent team being anything at all: nothing of its was
        // destroyed, because there was nothing of its to destroy.
        Assert.False(VictorySystem.HasStructures(world, 2));
        Assert.Equal(0, TeamCensus(world, 2));
    }

    [Fact]
    public void AMatchWithOneEnemyAndNoAllyIsWonByBreakingThatEnemy()
    {
        SimWorld world = Scenario.NewWorld(ScenarioKind.Duel, 20250101, SkirmishCapacity);
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Duel);

        Assert.Equal(2, setup.CommandCentres.Count);
        Assert.True(world.IsTeamInPlay(2));
        Assert.False(world.IsTeamInPlay(1));

        // No ally: the player is the only team on its side, which is what the roster says and what
        // the interface and the licence panel now ask rather than assuming team 1.
        Assert.False(world.AreAllied(0, 1));
        Assert.Equal(0, TeamCensus(world, 1));

        world.RunTicks(100);
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);

        BreakStructures(world, 2);
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void ThePlayersSideLosesWhenItsOwnStructuresAreGone()
    {
        // The other half of the verdict, in a match with one enemy: the enemy is the last side
        // standing, and the outcome says so from the player's point of view rather than naming the
        // faction that won.
        SimWorld world = Scenario.NewWorld(ScenarioKind.Rivals, 20250101, SkirmishCapacity);
        Scenario.Build(world, ScenarioKind.Rivals);

        world.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);

        BreakStructures(world, 0);
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    /// <summary>
    /// The two teams an alliance used to be, on the same ground, in two different matches. Nothing
    /// about the units changes between them — same faction, same roles, same distance — so the only
    /// thing that can make one pair fight and the other not is the match each world was built with.
    /// </summary>
    [Fact]
    public void WhetherTwoTeamsAreEnemiesIsTheMatchsAnswerAndNotTheirNumbers()
    {
        static (SimWorld World, int First, int Second) Duel()
        {
            SimWorld world = new(seed: 20250101, capacity: 64, MatchRoster.Rivals);
            WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

            EntityId soviet = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, centre);
            EntityId chinese = Spawn(world, Faction.Chinese, 1, UnitKind.Tank, Offset(centre, 60_000));

            return (world, soviet.Slot, chinese.Slot);
        }

        static (SimWorld World, int First, int Second) Alliance()
        {
            SimWorld world = new(seed: 20250101, capacity: 64);
            WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

            EntityId soviet = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, centre);
            EntityId chinese = Spawn(world, Faction.Chinese, 1, UnitKind.Tank, Offset(centre, 60_000));

            return (world, soviet.Slot, chinese.Slot);
        }

        (SimWorld rivals, int rivalSoviet, int rivalChinese) = Duel();
        (SimWorld allies, int alliedSoviet, int alliedChinese) = Alliance();

        int health = UnitCatalog.Get(UnitKind.Tank).Health;

        Assert.True(rivals.IsHostile(0, 1));
        Assert.False(allies.IsHostile(0, 1));

        rivals.RunTicks(200);
        allies.RunTicks(200);

        Assert.True(
            rivals.GetRefBySlot(rivalChinese).Health < health,
            "In a match where teams 0 and 1 are enemies, the Σοβιετικοί tank never hit the Κινέζοι one.");
        Assert.True(
            rivals.GetRefBySlot(rivalSoviet).Health < health,
            "In a match where teams 0 and 1 are enemies, the Κινέζοι tank never hit the Σοβιετικοί one.");

        Assert.Equal(health, allies.GetRefBySlot(alliedChinese).Health);
        Assert.Equal(health, allies.GetRefBySlot(alliedSoviet).Health);
    }

    /// <summary>
    /// The rule counts <em>sides</em>, so two enemies and one enemy resolve through the same line.
    /// Three sides of one team each is a free-for-all, which this game does not ship and which is
    /// exactly why the rule must not be written for the match that is shipped.
    /// </summary>
    [Fact]
    public void TheVerdictCountsSidesAndNotTeams()
    {
        MatchRoster freeForAll = MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(1, Faction.Chinese, 1),
            new MatchTeam(2, Faction.Western, 2));

        SimWorld world = new(seed: 20250101, capacity: 16, freeForAll);

        Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-200, -200));
        Spawn(world, Faction.Chinese, 1, UnitKind.CommandCentre, WorldPos.GroundMetres(200, -200));
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, WorldPos.GroundMetres(0, 200));

        world.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);

        // One enemy left: still fought.
        BreakStructures(world, 1);
        world.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);

        // Both enemies broken: the player's side has no enemies left.
        BreakStructures(world, 2);
        world.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    /// <summary>
    /// <b>The regression this job must not break.</b> The standard three-faction match, built through
    /// the same path a two-faction one takes, is byte-for-byte the world it was: the number below is
    /// the golden already in the repository (<c>ScenarioTests.SkirmishInitialHash_IsStable</c>) and it
    /// was not regenerated. The three teams are laid out in the same order from the same generator,
    /// because the layout now walks the teams the match declares and the standard match declares
    /// three of them, in slot order, as it always spawned them.
    /// </summary>
    [Fact]
    public void AThreeFactionMatchIsUnchanged()
    {
        SimWorld world = Scenario.NewWorld(ScenarioKind.Skirmish, 20250101, SkirmishCapacity);
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

        Assert.Equal(MatchRoster.StandardSkirmish, world.Roster);
        Assert.Equal(3, setup.CommandCentres.Count);
        Assert.Equal(SkirmishInitialHash, StateHash.Compute(world));

        // And the sides it plays by are the ones it always played by.
        Assert.True(world.AreAllied(0, 1));
        Assert.True(world.IsHostile(0, 2));
        Assert.True(world.IsHostile(1, 2));
    }

    /// <summary>
    /// The ally dying does not decide the standard match, and the enemy dying does — the same rule as
    /// before, now read off the sides the match declares rather than off teams 0, 1 and 2.
    /// </summary>
    [Fact]
    public void TheStandardMatchIsStillDecidedByTheEnemySideAndNotByTheAlly()
    {
        static SimWorld World()
        {
            SimWorld world = new(seed: 4242, capacity: 16);

            Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-200, -200));
            Spawn(world, Faction.Chinese, 1, UnitKind.CommandCentre, WorldPos.GroundMetres(200, -200));
            Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, WorldPos.GroundMetres(0, 200));

            return world;
        }

        SimWorld allyLost = World();
        BreakStructures(allyLost, 1);
        allyLost.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Ongoing, allyLost.Outcome);

        SimWorld enemyLost = World();
        BreakStructures(enemyLost, 2);
        enemyLost.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Victory, enemyLost.Outcome);
    }

    /// <summary>
    /// The AI in a one-against-one. It plays the one enemy the match declares, and the team that is
    /// not in the match is left alone: nothing of its is ordered, nothing of its is produced, and its
    /// ledger is untouched. The witness that the AI really did play is the attack order it issued
    /// against the enemy's base, which is a decision no script asked for.
    /// </summary>
    [Fact]
    public void TheAiPlaysTheOneEnemyInAMatchThatHasNoAlly()
    {
        SimWorld world = Scenario.NewWorld(ScenarioKind.Rivals, 20250101, SkirmishCapacity);
        Scenario.Build(world, ScenarioKind.Rivals);

        Assert.True(AiSystem.Plays(world, 1), "Team 1 is the enemy, and nobody is playing it but the computer.");
        Assert.False(AiSystem.Plays(world, 0), "The player's team is not played by the computer.");
        Assert.False(AiSystem.Plays(world, 2), "Team 2 is not in this match, so there is nothing to play.");

        // Ten seconds of decisions, because the loop buys its defensive line before it commands its
        // army: the first few decisions are spent on a Πυροβολείο and the radar that gives it its
        // reach, and each of those is a decision that returns without touching a unit.
        world.RunTicks(200);

        int ordered = 0;
        int aimedAtTheEnemy = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity unit = ref world.GetRefBySlot(slot);

            if (unit.TeamId != 1 || !unit.HasAttackOrder)
            {
                continue;
            }

            ordered++;

            if (world.GetRefBySlot(unit.TargetSlot).TeamId == 0)
            {
                aimedAtTheEnemy++;
            }
        }

        Assert.True(ordered > 0, "The computer opponent issued no order at all in a match it is playing.");
        Assert.Equal(ordered, aimedAtTheEnemy);

        // The absent team: no units, no orders, and a ledger nothing has touched. A team that is not
        // in the match is not a team the AI decides for, which is what "team 1 or team 2" used to
        // mean and what it must not mean now.
        Assert.Equal(0, TeamCensus(world, 2));
        Assert.Equal(0, world.Team(2).Materials);
        Assert.Equal(0, world.Team(2).Energy);
    }

    /// <summary>
    /// A two-faction match records and replays. The roster is not part of the state hash, so the only
    /// thing that puts the same sides back on the map for a replay is the scenario the file stores —
    /// which is why <see cref="ReplayFile.Run"/> builds the world with the roster that scenario
    /// declares, and why this is the test that would catch it if it did not.
    /// </summary>
    [Fact]
    public void ATwoFactionMatchRecordsAndReplays()
    {
        SimWorld world = Scenario.NewWorld(ScenarioKind.Rivals, 20250101, SkirmishCapacity);
        Scenario.Build(world, ScenarioKind.Rivals);

        world.StartRecording();
        world.RunTicks(60);

        ReplayFile replay = ReplayFile.Capture(world, ScenarioKind.Rivals);

        Assert.Equal(ScenarioKind.Rivals, replay.Scenario);

        ReplayResult result = replay.Verify();

        Assert.True(result.Matches, $"The replay diverged: expected {result.ExpectedHash}, got {result.ActualHash}.");
    }

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, WorldPos position)        => world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(position),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            UnitCatalog.Get(kind).Health);

    private static WorldPos Offset(WorldPos from, int millimetres)
        => new(from.X + millimetres, 0, from.Z);

    /// <summary>Number of live entities a team owns.</summary>
    private static int TeamCensus(SimWorld world, int team)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).TeamId == team)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>Destroys every structure a team owns, as a battle would have to.</summary>
    private static void BreakStructures(SimWorld world, int team)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                world.Despawn(new EntityId(slot, entity.Generation));
            }
        }
    }
}
