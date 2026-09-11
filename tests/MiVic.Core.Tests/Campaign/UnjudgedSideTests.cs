using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// <b>A side the victory rule does not judge: the non-player force.</b>
/// <para>
/// The trigger layer was the first thing found by asking "what reads the world and is never asked
/// about the world it opens in", and the objectives were the second. The victory rule is the third,
/// and it has been safe until now by construction rather than by being asked: the scenario lays a
/// base down for every team a match declares, so a declared side with no structures could not exist
/// and the rule never met a side with no ground. <c>MatchTeam.Judged</c> is what a match says when it
/// means such a side — a force that holds objectives rather than ground, which is §8's Operation
/// Paperclip and the scientists who are an objective rather than an army — and these tests are the
/// two halves of it: the mission the declaration enables, and the mission the check catches when the
/// declaration is missing. The two are the same mission, which is the point: the declaration is the
/// whole of the difference between a side that is a design and a side that is a mistake.
/// </para>
/// <para>
/// The mission below is built for the case rather than bent to fit, the way the other two layers'
/// tests are: a mission staged around a non-player force is a mission with a cast of its own, and the
/// thing under test is what the rule makes of a cast that has one.
/// </para>
/// </summary>
public sealed class UnjudgedSideTests
{
    private const int Capacity = 1024;

    /// <summary>
    /// The non-player force's team: the fourth slot. No mission column describes it — a definition
    /// carries the player's force, the ally's and the enemy's — so the scenario lays nothing out for
    /// it and the mission's own script is what puts it on the map, which is the door a gun on a ridge
    /// comes through.
    /// </summary>
    private const int ForceTeam = 3;

    /// <summary>Where the mission's opening scene puts the force, in millimetres east and north.</summary>
    private const int OutpostX = 60_000;
    private const int OutpostZ = 60_000;

    /// <summary>The mission's opening scene: the force is standing in the outpost on the first tick.</summary>
    private const int ForceSize = 6;

    /// <summary>
    /// <b>The same mission twice: with the declaration, and without it.</b>
    /// <para>
    /// Two sides and nothing else — the player's, which opens with a base, and the force's, which
    /// opens with nothing at all and is given six unarmed units by the mission's script. That is the
    /// cast the landmine is about: with the force judged, the rule's two questions are "does the
    /// player's side hold ground" (yes) and "does any other side" (no, because the only other side
    /// has none and never did), so it answers <see cref="GameOutcome.Victory"/> on the check that
    /// first asks it. <paramref name="judged"/> is the one line that says the force is not a side
    /// playing for the map: false is the declaration, and true is the same mission as its author
    /// first wrote it.
    /// </para>
    /// <para>
    /// Everything else about the mission is sound on purpose, because the point is that a mission can
    /// be right in every objective and still be unplayable for a reason no objective mentions: one
    /// reachable objective inside the time limit, one trigger that puts the force on the map, and no
    /// time limit trickery.
    /// </para>
    /// </summary>
    private static MissionDefinition Mission(bool judged) => new(
        Id: judged ? "judged_force" : "unjudged_force",
        GreekTitle: "Δοκιμή μη εμπόλεμης πλευράς",
        GreekBriefing:
            "Μια αποστολή στημένη γύρω από μια δύναμη που δεν κρατά έδαφος: " +
            "οι άνθρωποι του φυλακίου δεν είναι στρατός, είναι ο στόχος.",
        Seed: 20250104UL,
        PlayerBase: new WorldPos(-180_000, 0, -180_000),
        AllyBase: default,
        EnemyBase: default,
        PlayerUnits: 8,
        AllyUnits: 0,
        EnemyUnits: 0,
        Objectives:
        [
            new ObjectiveDefinition(
                ObjectiveKind.ReachTechTier,
                "Φτάστε σε τεχνολογικό επίπεδο 3.",
                Team: 0,
                TierTarget: 3,
                DeadlineTick: 6_000),
        ],
        TimeLimitTicks: 7_200)
    {
        Roster = MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(ForceTeam, Faction.Chinese, 1, Judged: judged)),

        Triggers =
        [
            new TriggerDefinition(
                Id: "the-force-arrives",
                Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 1),
                Actions:
                [
                    // Unarmed, wheeled and no part of a fight: the catalogue has no scientist, and
                    // the role nearest to what these people are is the one that cannot shoot back.
                    new TriggerAction(
                        TriggerActionKind.Spawn,
                        Team: ForceTeam,
                        Role: UnitKind.Harvester,
                        Count: ForceSize,
                        CentreX: OutpostX,
                        CentreZ: OutpostZ),
                ]),
        ],
    };

    private static SimWorld Build(MissionDefinition mission)
    {
        var world = Scenario.NewWorld(ScenarioKind.Mission, mission.Seed, Capacity, mission);
        Scenario.BuildMission(world, mission);
        return world;
    }

    /// <summary>Ticks the clock mission's limit is written in.</summary>
    private const int ClockMissionLimit = 200;

    /// <summary>
    /// <b>A match that can only be lost by its clock:</b> a two-faction mission, both armies standing,
    /// and one primary objective that no deadline can fail — so the limit is the only thing left that
    /// can end it. The limit is short because a test has to reach it, and what its length changes is
    /// how long that takes and nothing else.
    /// </summary>
    private static MissionDefinition ClockMission() => new(
        Id: "clock_mission",
        GreekTitle: "Δοκιμή: η αποστολή που χάνεται με το ρολόι",
        GreekBriefing:
            "Μια αποστολή που δεν μπορεί να χαθεί από τους στόχους της: " +
            "ο μόνος στόχος δεν έχει διορία, οπότε το ρολόι είναι αυτό που την κρίνει.",
        Seed: 20250104UL,
        PlayerBase: new WorldPos(-180_000, 0, -180_000),
        AllyBase: default,
        EnemyBase: new WorldPos(180_000, 0, 180_000),
        PlayerUnits: 8,
        AllyUnits: 0,
        EnemyUnits: 8,
        Objectives:
        [
            new ObjectiveDefinition(
                ObjectiveKind.ReachTechTier,
                "Φτάστε σε τεχνολογικό επίπεδο 3.",
                Team: 0,
                TierTarget: 3),
        ],
        TimeLimitTicks: ClockMissionLimit)
    {
        Roster = MatchRoster.Duel,
    };

    private static string Joined(IReadOnlyList<string> problems) => string.Join(" | ", problems);

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

    // ---------------------------------------------------------------- the accident

    /// <summary>
    /// <b>The same mission without the declaration, refused — with the side named, the number the
    /// world answered with, and what the rule makes of it.</b>
    /// <para>
    /// A declared side that stands in no structures in the world the mission opens in is the accident
    /// the rule's safety has always rested on: the scenario lays a base for every team a match
    /// declares, so reaching this state means a team has been declared that nothing was laid out for,
    /// and the rule cannot tell such a side from one that has been destroyed. The complaint says
    /// which side, that team 3 stands in nothing, and that the rule's whole verdict on that world is
    /// already <c>victory</c> — which is the landmine the declaration was added for, measured rather
    /// than described.
    /// </para>
    /// </summary>
    [Fact]
    public void ADeclaredSideWithNoStructuresIsRefusedUntilTheMatchSaysItDoesNotJudgeIt()
    {
        string problems = Joined(TriggerSystem.Validate(Mission(judged: true)));

        Assert.Contains("side 1 of this mission's match stands in no structures", problems);
        Assert.Contains("team 3 stands in 0 structures", problems);
        Assert.Contains("reads the side as already beaten", problems);
        Assert.Contains("its verdict on that world is victory already", problems);
        Assert.Contains("Judged = false", problems);

        // The declaration is the whole of the difference, and with it the mission is a mission.
        Assert.Empty(TriggerSystem.Validate(Mission(judged: false)));
    }

    // ---------------------------------------------------------------- the mission it enables

    /// <summary>
    /// <b>The mission with the declaration validates clean and plays: the match is ongoing at tick
    /// one rather than won by the rule.</b>
    /// <para>
    /// This is the test that proves the declaration enables the mission rather than merely permitting
    /// it, and it is written around the fact that a mission decides its own outcome — the rule is not
    /// ticked while a mission is attached — so "the rule did not decide it" has to be asked of the
    /// rule directly. Three answers are asserted, and the first is the control: the same mission
    /// <em>without</em> the word is won by the rule on its first check, with both forces alive; with
    /// it, the rule has nothing to say about a world it can measure at both ends; and the match the
    /// game actually plays is ongoing a hundred ticks later, with the force standing in nothing and
    /// the objective still open.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSameMissionIsOngoingAtTickOneRatherThanDecided()
    {
        SimWorld bare = Build(Mission(judged: true));

        Assert.Equal(Faction.Soviet, bare.FactionOfTeam(0));
        Assert.Equal(0, VictorySystem.CountStructures(bare, ForceTeam));
        Assert.Equal(GameOutcome.Victory, VictorySystem.Decide(bare));

        SimWorld world = Build(Mission(judged: false));

        // Tick one: the rule's own answer about this world, which is the question that used to be
        // answered for the player before they had done anything.
        world.RunTicks(1);

        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
        Assert.Equal(GameOutcome.Ongoing, VictorySystem.Decide(world));

        // And the force is really on the map: six units of a side that owns nothing at all, which is
        // the shape of side this rule has never had to read before.
        Assert.Equal(ForceSize, TeamCensus(world, ForceTeam));
        Assert.False(VictorySystem.SideHasStructures(world, world.Roster.SideOf(ForceTeam)));
        Assert.Equal(0, VictorySystem.CountSideStructures(world, world.Roster.SideOf(ForceTeam)));

        // Five seconds of it — five victory checks, had a mission been a skirmish — and nothing has
        // been decided for the player: the objective is still open and the match is still being
        // played, which is the difference between a fix that enables a mission and one that only
        // stops it being refused.
        world.RunTicks(VictorySystem.CheckInterval * 5);

        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
        Assert.Equal(GameOutcome.Ongoing, VictorySystem.Decide(world));
        Assert.True(world.Objectives[0].IsPending);
    }

    // ---------------------------------------------------------------- why a defeat records

    /// <summary>
    /// <b>The rule's defeat and the objectives' defeat are not the same event, and what tells them
    /// apart is what the banner names.</b>
    /// <para>
    /// The client's defeat line said <em>your side was destroyed</em> for every defeat there is —
    /// including a mission lost by an objective failing or by its clock running out with both armies
    /// standing, over a panel showing sixteen units and four structures alive on each side. The fix was
    /// never new wording for the rule's defeat; it is that the banner asks the world why the match was
    /// lost before it says anything about the side, and the answers are exactly the two asserted here:
    /// a defeat the rule reached has the side in nothing, and a defeat the objectives reached has it
    /// standing. <see cref="DefeatReport.Of"/> is that question, and the banner is one line of copy
    /// over it; this is the answer's honesty in the layer that can be asked.
    /// </para>
    /// <para>
    /// <b>The invariant the interface rests on is asserted as an equivalence rather than as a pair of
    /// examples:</b> a verdict of <see cref="DefeatCause.SideDestroyed"/> is reachable exactly when the
    /// side holds nothing, so no wording over it can state a destruction that did not happen, and the
    /// line that claims one has nowhere else to come from.
    /// </para>
    /// </summary>
    [Fact]
    public void ADefeatByTheObjectivesIsNotADestroyedSide()
    {
        // The rule's defeat, in a match with no mission: the player's side is beaten because it holds
        // no ground, which is what "destroyed" has always meant here.
        SimWorld duel = Scenario.NewWorld(ScenarioKind.Duel, 20250101, Capacity);
        Scenario.Build(duel, ScenarioKind.Duel);

        BreakStructures(duel, 0);
        duel.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Defeat, duel.Outcome);
        Assert.False(VictorySystem.SideHasStructures(duel, duel.Roster.PlayerSide));
        Assert.Equal(DefeatCause.SideDestroyed, DefeatReport.Of(duel).Cause);

        // The objectives' defeat: a mission whose objective runs out of time with nothing at all
        // destroyed on either side. The outcome is the same word and the world is not. The deadline
        // that arrives first is the objective's own — 6 000 ticks against the mission's limit of
        // 7 200 — and the run below passes both, so this also pins the order: the report asks the
        // objectives before the clock, and names the objective that failed rather than the limit that
        // arrived after it.
        SimWorld mission = Build(Mission(judged: false));
        mission.RunTicks(7_200 + MissionSystem.CheckInterval);

        Assert.Equal(GameOutcome.Defeat, mission.Outcome);
        Assert.Equal(0, mission.TeamRef(ForceTeam).StructuresLost);
        Assert.True(
            VictorySystem.SideHasStructures(mission, mission.Roster.PlayerSide),
            "The mission was lost with the player's side destroyed, which is not the case this asserts.");

        DefeatVerdict verdict = DefeatReport.Of(mission);

        Assert.Equal(DefeatCause.ObjectiveFailed, verdict.Cause);

        // Named, and the one that failed: the copy over this verdict quotes this description, so the
        // banner and the mission panel cannot be talking about two different objectives.
        Assert.Same(mission.Mission!.Objectives[0], verdict.Objective);
        Assert.Equal("Φτάστε σε τεχνολογικό επίπεδο 3.", verdict.FailedObjectiveDescription);

        // And the equivalence the interface rests on, over both worlds: the destruction line is
        // reachable exactly when the side really holds nothing.
        foreach (SimWorld world in new[] { duel, mission })
        {
            Assert.Equal(
                !VictorySystem.SideHasStructures(world, world.Roster.PlayerSide),
                DefeatReport.Of(world).Cause == DefeatCause.SideDestroyed);
        }
    }

    /// <summary>
    /// <b>The clock is the third reason, and the one no objective can be named for.</b>
    /// <para>
    /// A mission is lost by time when the limit arrives with a primary objective still open and none
    /// failed, which takes an objective with no deadline of its own: an objective whose deadline is the
    /// mission's limit fails on the very evaluation that applies the limit, and the report names it.
    /// That is the shipped shape and not a curiosity — <c>m4_pass</c> is lost exactly this way by a
    /// player who lets the ambush stand, because its scripted objective has no deadline and only the
    /// trigger that sees the ambush broken can complete it. The mission here is built for the case
    /// rather than borrowed, so the test does not depend on what the AI does with a column in ten
    /// minutes of game time.
    /// </para>
    /// <para>
    /// What the banner must be able to say about it is that the clock ran out rather than that an
    /// objective failed — a distinction the report has to make out of the world's own records, since
    /// the outcome word is the same and the panel shows a side that is entirely intact.
    /// </para>
    /// </summary>
    [Fact]
    public void AClockThatRunsOutWithAnObjectiveOpenIsItsOwnReason()
    {
        SimWorld world = Build(ClockMission());
        world.RunTicks(ClockMissionLimit + MissionSystem.CheckInterval);

        Assert.Equal(GameOutcome.Defeat, world.Outcome);

        // Nothing failed: the objective is still open, which is the state that made the clock the
        // reason — had its deadline passed instead, this would be the objective's defeat.
        Assert.True(world.Objectives[0].IsPending);
        Assert.True(VictorySystem.SideHasStructures(world, world.Roster.PlayerSide));

        DefeatVerdict verdict = DefeatReport.Of(world);

        Assert.Equal(DefeatCause.TimeExpired, verdict.Cause);

        // No objective to name, which is what the interface's copy for this cause rests on: it is the
        // one of the three that names nothing, and an empty description is how the verdict says so.
        Assert.Null(verdict.Objective);
        Assert.Equal(string.Empty, verdict.FailedObjectiveDescription);
    }

    /// <summary>
    /// <b>The hard corner of the invariant: a side can lose the ground the rule weighed and still be
    /// standing on ground it never did.</b>
    /// <para>
    /// The rule asks a side's question of the teams it judges, so a match declaring a team it does not
    /// judge *on the player's own side* has two grounds on that side and only one of them counted:
    /// break the judged team's base and the rule calls the side beaten, while the outpost beside it —
    /// a building of a team that holds objectives rather than ground — is still standing on the map in
    /// front of the player. This is the world in which a banner asking the rule's question narrowly
    /// would say the side had been destroyed over a building the player is looking at, so
    /// <see cref="DefeatCause.SideStanding"/> exists to answer it and the destroyed line rests on a
    /// question asked of the whole side instead.
    /// </para>
    /// <para>
    /// No shipped mission stages it — a non-player force has no base by design — which is exactly why
    /// the copy must not depend on one never being written. See the declaration tests above for the
    /// same arrangement without the building: the rule is asked the same question there and answers
    /// nothing at all.
    /// </para>
    /// </summary>
    [Fact]
    public void ADefeatOverGroundTheRuleNeverWeighedIsNotADestroyedSide()
    {
        MatchRoster roster = MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(ForceTeam, Faction.Chinese, 0, Judged: false),
            new MatchTeam(2, Faction.Western, 1));

        var world = new SimWorld(seed: 4242, capacity: 64, roster);

        int centreHealth = UnitCatalog.Get(UnitKind.CommandCentre).Health;

        // Both on the player's side of the line: the base the rule measures, and the outpost it does
        // not — which is the whole of the case.
        world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-180, -180), default, centreHealth);
        world.Spawn(Faction.Chinese, ForceTeam, UnitKind.CommandCentre, WorldPos.GroundMetres(60, 60), default, centreHealth);

        // The enemy's side, intact, so the match is decided on the player's side and nowhere else.
        world.Spawn(Faction.Western, 2, UnitKind.CommandCentre, WorldPos.GroundMetres(200, 0), default, centreHealth);

        BreakStructures(world, 0);
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Defeat, world.Outcome);
        Assert.False(VictorySystem.HasStructures(world, 0));
        Assert.True(VictorySystem.SideHasStructures(world, world.Roster.PlayerSide));

        DefeatVerdict verdict = DefeatReport.Of(world);

        Assert.Equal(DefeatCause.SideStanding, verdict.Cause);

        // And not the destruction, which is the assertion this test exists for: the side the banner is
        // describing still has a building on the map.
        Assert.NotEqual(DefeatCause.SideDestroyed, verdict.Cause);
    }

    /// <summary>
    /// The other end of the same rule: <b>a side the match does not judge is not counted for the
    /// player either.</b> A mission can be a raiding party with no base — the agents of the
    /// extraction, who hold objectives rather than ground — and the rule must not read their
    /// emptiness as a defeat, which is what the line written for factions did with every team that
    /// had no buildings. With the prisoner released from judgement at both ends there is nothing left
    /// for it to decide, so it decides nothing, whatever happens to the ground.
    /// </summary>
    [Fact]
    public void ASideTheRuleDoesNotJudgeIsNotCountedAtEitherEnd()
    {
        MatchRoster roster = MatchRoster.Declare(
            new MatchTeam(0, Faction.Western, 0, Judged: false),
            new MatchTeam(2, Faction.Soviet, 1));

        SimWorld world = new(seed: 4242, capacity: 32, roster);

        world.Spawn(
            Faction.Soviet,
            2,
            UnitKind.CommandCentre,
            WorldPos.GroundMetres(200, 0),
            default,
            UnitCatalog.Get(UnitKind.CommandCentre).Health);

        // The player's side owns nothing and never will; the enemy's is standing. The old line called
        // this defeat on the first check, with the party alive on the map.
        Assert.Equal(GameOutcome.Ongoing, VictorySystem.Decide(world));

        // And breaking the enemy's ground does not win it either: the rule has no measurement to make
        // at one end, so the objectives are what will decide this match.
        BreakStructures(world, 2);

        Assert.Equal(GameOutcome.Ongoing, VictorySystem.Decide(world));
    }

    // ---------------------------------------------------------------- the match that declares nothing new

    /// <summary>
    /// <b>The regression: an ordinary match is decided exactly as it was, and its opening is the
    /// world it always was.</b>
    /// <para>
    /// The declaration is a fact about a side that a match does not have to mention, so the rule's
    /// walk has to be the walk it always was for every match that does not — which is asserted the
    /// only way a claim about "unchanged" can be: the golden initial hash the repository already
    /// pins, and the verdicts reached for the reasons they were always reached. The three-faction
    /// skirmish is decided by the enemy side and not by the ally, and the two-faction duel by the one
    /// enemy it has; both are matches whose rosters judge every team in them.
    /// </para>
    /// </summary>
    [Fact]
    public void AnOrdinaryMatchIsDecidedExactlyAsItAlwaysWas()
    {
        // The skirmish's initial hash, as ScenarioTests pins it. Written here rather than regenerated:
        // a fact about a side changes nothing for a match that does not declare it.
        SimWorld skirmish = Scenario.NewWorld(ScenarioKind.Skirmish, 20250101, Capacity);
        Scenario.Build(skirmish, ScenarioKind.Skirmish);

        Assert.Equal(7771467923509982711UL, StateHash.Compute(skirmish));
        Assert.True(skirmish.Roster.IsJudged(0));
        Assert.True(skirmish.Roster.IsJudged(1));
        Assert.True(skirmish.Roster.IsJudged(2));

        skirmish.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Ongoing, skirmish.Outcome);

        // The ally dying has never decided this match, and the enemy dying always has.
        BreakStructures(skirmish, 1);
        skirmish.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Ongoing, skirmish.Outcome);

        BreakStructures(skirmish, 2);
        skirmish.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Victory, skirmish.Outcome);

        // Σοβιετικοί against Δυτικοί, nobody else: the player's side falls and the match says so.
        SimWorld duel = Scenario.NewWorld(ScenarioKind.Duel, 20250101, Capacity);
        Scenario.Build(duel, ScenarioKind.Duel);

        duel.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Ongoing, duel.Outcome);

        BreakStructures(duel, 0);
        duel.RunTicks(VictorySystem.CheckInterval + 1);
        Assert.Equal(GameOutcome.Defeat, duel.Outcome);
    }

    /// <summary>
    /// <b>The declaration is part of the match, not a decoration of it.</b> A world is laid out for
    /// the roster it was built with and refuses a mission that disagrees with it — the guard that
    /// keeps a roster from being a desync the state hash cannot see, since the match is not in it.
    /// The word being part of that comparison is what makes it a fact about the world rather than a
    /// remark about it. See <c>MatchRosterTests</c> for what the roster itself answers.
    /// </summary>
    [Fact]
    public void AWorldBuiltForOneDeclarationRefusesTheOther()
    {
        MissionDefinition judged = Mission(judged: true);
        MissionDefinition declared = Mission(judged: false);

        Assert.False(declared.Roster.Equals(judged.Roster));

        SimWorld world = Scenario.NewWorld(ScenarioKind.Mission, judged.Seed, Capacity, judged);

        Assert.Throws<InvalidOperationException>(() => Scenario.BuildMission(world, declared));
    }
}
