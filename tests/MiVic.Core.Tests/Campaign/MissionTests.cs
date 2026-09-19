using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Replay;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// The campaign is simulation state, not client scripting: objectives are
/// predicates evaluated on a fixed interval, so a mission is as replayable as a
/// skirmish and its progress is part of the state hash.
/// </summary>
public sealed class MissionTests
{
    private const int Capacity = 1024;

    private static SimWorld Mission(string id)
    {
        MissionDefinition mission = MissionCatalog.Require(id);

        // Through Scenario.NewWorld, because a mission declares its own match: the demonstration
        // mission is fought by two sides, so a world built for the standard three-team skirmish
        // refuses to lay it out — which is the check that stops a mission's layout and its sides
        // from being chosen separately.
        var world = Scenario.NewWorld(ScenarioKind.Mission, mission.Seed, Capacity, mission);
        Scenario.BuildMission(world, mission);
        return world;
    }

    private static EntityId FirstStructureOf(SimWorld world, int team)
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
                return new EntityId(slot, entity.Generation);
            }
        }

        throw new InvalidOperationException($"Team {team} has no structures.");
    }

    [Fact]
    public void CatalogHasPlayableMissions()
    {
        // Twenty-one: the five of the Berlin chapter — the campaign's first chapter is its
        // tutorial, and it landed with the era machinery — then Επιχείρηση Συνδετήρας, the five
        // of the Korean chapter that introduces the ally, the four the campaign already had,
        // and the six of the modern era that close it. A mission cannot be shipped without
        // being in the catalog, and the catalog is what --mission <id> looks a mission up in.
        Assert.Equal(21, MissionCatalog.All.Length);

        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            Assert.NotEmpty(mission.Id);
            Assert.NotEmpty(mission.GreekTitle);
            Assert.NotEmpty(mission.Objectives);
            Assert.Contains(mission.Objectives, objective => objective.IsPrimary);
            Assert.NotNull(MissionCatalog.Find(mission.Id));
        }

        // The three missions the campaign has always had are fought by the standard
        // three-faction match. Everything since declares its own sides — the demonstration by
        // design, the Berlin chapter by era — and every declaration puts the player on side 0,
        // which is what the victory rule and the client both read as "us".
        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            Assert.Equal(0, mission.Roster.PlayerSide);

            if (mission.Id is "m1_bridgehead" or "m2_ridge" or "m3_industry")
            {
                Assert.Equal(MatchRoster.StandardSkirmish, mission.Roster);
            }
            else
            {
                Assert.True(mission.Roster.TeamsInPlay >= 2,
                    $"{mission.Id} declares fewer than the two sides a mission is fought between.");
            }
        }
    }

    /// <summary>
    /// A mission that declares two sides is laid out with two forces. The campaign's ally used to be
    /// team 1 given <c>AllyBase</c> and <c>AllyUnits</c>, always and only that team, so a mission
    /// without an ally was a mission with a third base on the map that nobody was playing. It is a
    /// side the mission says it has: one line of data, and the layout follows it.
    /// </summary>
    [Fact]
    public void AMissionThatDeclaresTwoSidesIsLaidOutWithTwoForces()
    {
        MissionDefinition solo = MissionCatalog.Require("m1_bridgehead") with { Roster = MatchRoster.Duel };

        SimWorld world = Scenario.NewWorld(ScenarioKind.Mission, solo.Seed, Capacity, solo);
        ScenarioSetup setup = Scenario.BuildMission(world, solo);

        Assert.Equal(2, setup.CommandCentres.Count);
        Assert.True(world.IsTeamInPlay(0));
        Assert.False(world.IsTeamInPlay(1));
        Assert.True(world.IsTeamInPlay(2));

        // The absent team owns nothing at all — not even the base the mission still carries data
        // for, which is what makes this a change of who is playing rather than of where they stand.
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                Assert.NotEqual(1, world.GetRefBySlot(slot).TeamId);
            }
        }

        // And the mission is still attached: the objectives decide it, not the last side standing.
        Assert.True(world.HasMission);
    }

    /// <summary>
    /// The layout refuses a world whose sides disagree with the mission's, for the same reason a
    /// scenario does: the match is not part of the state hash, so a disagreement would be a desync
    /// nothing could see.
    /// </summary>
    [Fact]
    public void AMissionRefusesAWorldBuiltForAnotherMatch()
    {
        MissionDefinition solo = MissionCatalog.Require("m1_bridgehead") with { Roster = MatchRoster.Duel };

        var world = new SimWorld(solo.Seed, Capacity);

        Assert.Throws<InvalidOperationException>(() => Scenario.BuildMission(world, solo));
    }

    [Fact]
    public void MissionLookupIsCaseInsensitiveAndThrowsForUnknown()
    {
        Assert.Same(MissionCatalog.All[0], MissionCatalog.Find(MissionCatalog.All[0].Id.ToUpperInvariant()));
        Assert.Null(MissionCatalog.Find("no_such_mission"));
        Assert.Throws<ArgumentException>(() => MissionCatalog.Require("no_such_mission"));
    }

    [Fact]
    public void AMissionWorldCarriesItsObjectives()
    {
        SimWorld world = Mission("m1_bridgehead");

        Assert.True(world.HasMission);
        Assert.Equal("m1_bridgehead", world.Mission!.Id);
        Assert.Equal(world.Mission.Objectives.Count, world.Objectives.Length);
        Assert.All(world.Objectives.ToArray(), state => Assert.True(state.IsPending));
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
    }

    [Fact]
    public void MissionStartsSmallerThanTheSkirmish()
    {
        SimWorld world = Mission("m1_bridgehead");

        Assert.Equal(65, world.AliveCount);
        Assert.Equal(0, world.TeamRef(0).StructuresLost);
    }

    [Fact]
    public void DestroyingEnemyStructuresCompletesTheObjective()
    {
        SimWorld world = Mission("m1_bridgehead");
        int before = world.TeamRef(2).StructuresLost;

        world.Despawn(FirstStructureOf(world, 2));
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.Equal(before + 1, world.TeamRef(2).StructuresLost);
        Assert.True(world.Objectives[0].IsComplete, "Destroying the enemy command centre did not complete the objective.");
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void RebuildingDoesNotUndoDestroyedProgress()
    {
        SimWorld world = Mission("m1_bridgehead");
        world.Despawn(FirstStructureOf(world, 2));

        // A structure spawned later must not reduce the count.
        world.Spawn(Faction.Western, 2, UnitKind.Factory, new WorldPos(0, 0, 200_000), default, 2_000);

        Assert.Equal(1, world.TeamRef(2).StructuresLost);
    }

    [Fact]
    public void ARoleFilteredObjectiveIgnoresOtherStructures()
    {
        // The m1 auto-win, as a test: the objective asks for the command centre, so
        // anything else the ally's AI happens to kill must not decide the mission.
        SimWorld world = Mission("m1_bridgehead");

        EntityId emplacement = world.Spawn(Faction.Western, 2, UnitKind.GunEmplacement,
            new WorldPos(0, 0, 200_000), default, 1_400);
        world.Despawn(emplacement);
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.Equal(1, world.TeamRef(2).StructuresLost);
        Assert.Equal(1, world.TeamRef(2).StructuresLostByKind[(int)UnitKind.GunEmplacement]);
        Assert.Equal(0, world.TeamRef(2).StructuresLostByKind[(int)UnitKind.CommandCentre]);
        Assert.True(world.Objectives[0].IsPending,
            "A structure that is not the command centre completed a command-centre objective.");
        Assert.Equal(GameOutcome.Ongoing, world.Outcome);

        world.Despawn(FirstStructureOf(world, 2));
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.Equal(1, world.TeamRef(2).StructuresLostByKind[(int)UnitKind.CommandCentre]);
        Assert.True(world.Objectives[0].IsComplete,
            "The command centre falling did not complete the command-centre objective.");
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    private static SimWorld Synthetic(MissionDefinition mission)
    {
        var world = Scenario.NewWorld(ScenarioKind.Mission, mission.Seed, Capacity, mission);
        Scenario.BuildMission(world, mission);
        return world;
    }

    private static MissionDefinition EscortMission(int deadline, int target) => new(
        Id: "test_escort",
        GreekTitle: "δοκιμή συνοδείας",
        GreekBriefing: "δοκιμή",
        Seed: 20250101UL,
        PlayerBase: new WorldPos(-180_000, 0, -180_000),
        AllyBase: default,
        EnemyBase: new WorldPos(0, 0, 200_000),
        PlayerUnits: 10,
        AllyUnits: 0,
        EnemyUnits: 4,
        Objectives:
        [
            new ObjectiveDefinition(
                ObjectiveKind.EscortArea,
                "Φτάστε στο αεροδρόμιο.",
                TargetTeam: 2,
                TargetCount: target,
                CentreX: 0,
                CentreZ: 0,
                RadiusMm: 40_000,
                DeadlineTick: deadline),
        ],
        TimeLimitTicks: 1_200)
    {
        Roster = MatchRoster.Duel,
    };

    [Fact]
    public void EscortAreaCompletesWhenTheConvoyArrives()
    {
        SimWorld world = Synthetic(EscortMission(deadline: 600, target: 2));

        Assert.True(world.Objectives[0].IsPending);
        Assert.Equal(0, world.Objectives[0].Progress);

        world.Spawn(Faction.Western, 2, UnitKind.Infantry, new WorldPos(0, 0, 0), default, 100);
        world.Spawn(Faction.Western, 2, UnitKind.Infantry, new WorldPos(0, 0, 0), default, 100);
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.Equal(2, world.Objectives[0].Progress);
        Assert.True(world.Objectives[0].IsComplete, "Two arrivals did not complete the escort.");
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void EscortAreaFailsAtTheDeadline()
    {
        SimWorld world = Synthetic(EscortMission(deadline: 200, target: 2));

        world.Spawn(Faction.Western, 2, UnitKind.Infantry, new WorldPos(0, 0, 0), default, 100);
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].Progress == 1,
            "One arrival was read as a high-water mark of more than one.");
        Assert.True(world.Objectives[0].IsPending);

        world.RunTicks(210);

        Assert.True(world.Objectives[0].IsFailed, "A convoy that did not make the deadline did not fail.");
        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    [Fact]
    public void AMissionCanCapItsEra()
    {
        MissionDefinition era = EscortMission(deadline: 600, target: 2) with { MaxTechTier = 1 };
        SimWorld world = Synthetic(era);

        EntityId bureau = world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau,
            new WorldPos(-180_000, 0, -180_000), default, 1_500);
        world.TeamRef(0).TechTier = 2;
        world.TeamRef(0).Materials = 10_000;

        world.Enqueue(SimCommand.Research(bureau, TechId.SovietAdvance2, world.Tick + 1, 0));
        int treasury = world.Team(0).Materials;
        world.Step();

        Assert.False(world.Team(0).IsResearching,
            "A tier-2 project ran in a mission whose era stops at tier 1.");
        Assert.True(world.Team(0).Materials >= treasury,
            "The refused project was still paid for.");
    }

    [Fact]
    public void LosingTheCommandCentreFailsTheProtectionObjective()
    {
        SimWorld world = Mission("m3_industry");
        world.Despawn(FirstStructureOf(world, 0));
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[2].IsFailed, "The protection objective did not fail.");
        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    [Fact]
    public void HoldingAnAreaNeedsTheFullDuration()
    {
        MissionDefinition source = MissionCatalog.Require("m2_ridge");
        ObjectiveDefinition hold = source.Objectives[0];

        // The circle is put over the player's own base rather than over the middle of the
        // map. What is under test is the hold clock — half the required time is not enough
        // and the full time is — and answering that needs units still alive at the end of
        // it: the middle of the map is where the two sides meet, and a dozen units dropped
        // into it are dead inside ten seconds, which measures the battle rather than the
        // clock. The base position comes from a first build, because the scenario puts the
        // base on the nearest ground that will hold it rather than where it is asked for.
        WorldPos playerBase = Scenario.BuildMission(new SimWorld(source.Seed, Capacity), source).BaseSites[0].Placed;

        MissionDefinition mission = source with
        {
            Objectives = [hold with { CentreX = playerBase.X, CentreZ = playerBase.Z }],
        };

        ObjectiveDefinition definition = mission.Objectives[0];

        var world = new SimWorld(mission.Seed, Capacity);
        Scenario.BuildMission(world, mission);

        // Teleport twice the required number of units into the objective circle:
        // one unit drifting out (combat, terrain) must not invalidate the test.
        int moved = 0;

        for (int slot = 0; slot < world.Capacity && moved < definition.TargetCount * 2; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != 0 || UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            entity.Position = new WorldPos(definition.CentreX, entity.Position.Y, definition.CentreZ);
            entity.HasMoveGoal = false;
            moved++;
        }

        Assert.Equal(definition.TargetCount * 2, moved);

        // Not yet: half the required time is not enough.
        world.RunTicks(definition.HoldTicks / 2);
        Assert.True(world.Objectives[0].IsPending, "The area objective completed too early.");
        Assert.True(world.Objectives[0].HoldProgress > 0, "The hold clock never started.");

        world.RunTicks((definition.HoldTicks / 2) + (MissionSystem.CheckInterval * 2));
        Assert.True(
            world.Objectives[0].IsComplete,
            $"The area objective did not complete after the full hold (held {world.Objectives[0].Progress}, " +
            $"clock {world.Objectives[0].HoldProgress}/{definition.HoldTicks}).");
    }

    [Fact]
    public void TechAndMaterialsObjectivesTrackTheTeam()
    {
        SimWorld world = Mission("m3_industry");

        world.TeamRef(0).TechTier = 3;
        world.TeamRef(0).Materials = 4_000;
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsComplete, "Tech objective did not complete.");
        Assert.True(world.Objectives[1].IsComplete, "Materials objective did not complete.");
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void MissingADeadlineFailsTheMission()
    {
        // The primary objective asks for ninety-nine enemy structures, so no amount of
        // fighting can satisfy it and the clock is the only thing that can decide the
        // mission — which is what this test is about.
        //
        // The catalog's own m1 used to serve, on the assumption that a match left alone
        // destroys nothing. That assumption was a symptom rather than a fact: both bases
        // stood in deep water, nothing could reach anything, and the alliance spent six
        // minutes doing precisely nothing. With the bases on ground the alliance takes a
        // Western structure inside half a minute, so the deadline is tested where the
        // deadline is the only thing that can end it.
        MissionDefinition source = MissionCatalog.Require("m1_bridgehead");

        MissionDefinition mission = source with
        {
            Objectives = [source.Objectives[0] with { TargetCount = 99, DeadlineTick = 200 }],
            TimeLimitTicks = 400,
        };

        var world = new SimWorld(mission.Seed, Capacity);
        Scenario.BuildMission(world, mission);

        world.RunTicks(mission.TimeLimitTicks + MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsFailed);
        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    [Fact]
    public void MissionProgressIsPartOfTheStateHash()
    {
        static ulong Run(bool destroy)
        {
            SimWorld world = Mission("m1_bridgehead");

            if (destroy)
            {
                world.Despawn(FirstStructureOf(world, 2));
            }

            world.RunTicks(MissionSystem.CheckInterval * 2);
            return StateHash.Compute(world);
        }

        Assert.NotEqual(Run(destroy: false), Run(destroy: true));
        Assert.Equal(Run(destroy: true), Run(destroy: true));
    }

    [Fact]
    public void MissionIsDeterministic()
    {
        static ulong Run()
        {
            SimWorld world = Mission("m2_ridge");
            world.RunTicks(60);
            return StateHash.Compute(world);
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void AMissionReplaysExactly()
    {
        SimWorld world = Mission("m1_bridgehead");
        world.StartRecording();

        // Something a player would do.
        EntityId attacker = FirstStructureOf(world, 0);
        world.OrderMove(attacker, new WorldPos(-100_000, 0, -100_000), 0);
        world.RunTicks(120);

        ReplayFile replay = ReplayFile.Capture(world, ScenarioKind.Mission);
        ReplayResult result = replay.Verify();

        Assert.Equal("m1_bridgehead", replay.MissionId);
        Assert.Equal(ScenarioKind.Mission, replay.Scenario);
        Assert.True(result.Matches, $"Mission replay diverged: expected {result.ExpectedHash}, got {result.ActualHash}.");
    }

    [Fact]
    public void EveryMissionReplaysExactly()
    {
        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            SimWorld world = Mission(mission.Id);
            world.StartRecording();
            world.RunTicks(80);

            ReplayResult result = ReplayFile.Capture(world, ScenarioKind.Mission).Verify();

            Assert.True(result.Matches, $"Mission {mission.Id} diverged: expected {result.ExpectedHash}, got {result.ActualHash}.");
        }
    }

    [Fact]
    public void MissionInitialHashesAreStable()
    {
        // Golden hashes of the missions' starting worlds: this is what
        // catches a change to the mission layouts that a replay would otherwise
        // reproduce faithfully but wrongly.
        //
        // The first three are unchanged by the trigger layer, and that is the point of the fourth
        // entry being *appended* rather than the three being regenerated: a mission that declares
        // no triggers hashes nothing new, so the layer cannot disturb a mission that does not use
        // it. The fourth hash is a new mission's, not a moved one's.
        //
        // Last changed by the modern era's last six missions landing: the list grew to twenty-one,
        // x5 to x10 appended their own six fingerprints after m1 to m4, and no earlier mission
        // moved when they did. Before that it was the structure-loss ledger gaining a per-kind
        // breakdown: the hash now folds the 32 slots of it for every team, so every world's hash
        // moved with the field set again — no mission changed, the fingerprint did. Before that it
        // was the audit that closed the state hash: every world's hash moved because the hash now
        // folds in the entity fields it had been skipping, the route's own waypoints, and the
        // corrected rounding of negative fixed-point products.
        //
        // Before that it was the production queues becoming state: a mission's starting buildings have
        // empty queues and their hashes moved anyway, which is exactly why the queue field is mixed
        // for every live slot rather than only for the ones with something in them. Before that it
        // was the crossings a team builds becoming state: a bridge records its span,
        // the tick its work started and how much of the deck is up, and all three are hashed, so
        // every world's hash moved with the new field. Before that it was the bases moving onto
        // ground that can hold them: a mission lays
        // its three bases out through the same search the skirmish uses, and seven of the
        // nine moved — 92.9 m, 88.6 m and 9.2 m in the first mission, 166.6 m and 28.7 m in
        // the second, 142.0 m and 66.8 m in the third — with the structures and the
        // starting force of each following its base. Before that it was aspect and
        // landform: every cell of every mission's ground now carries the way it faces and
        // the shape it is, and both are part of the state hash. Before that it was the
        // terrain attributes, which put canopy density and moisture in a second word per
        // cell.
        ulong[] expected =
        [
            9847799334465133416UL,
            3283873620813569964UL,
            4508879147646755874UL,
            14817049138732711902UL,
            1444733445562834875UL,
            2403530285048726468UL,
            3585420072741336489UL,
            13890578630252119789UL,
            15018273258975708685UL,
            921221812242837666UL,
            12987784401365476443UL,
            10506566667300618048UL,
            2314134954811554108UL,
            13962516197684823025UL,
            5050561699448869838UL,
            9492141598485630500UL,
            7506501707841203820UL,
            3680081011739275840UL,
            2195014991599519820UL,
            2876269079344585704UL,
            4729437655215668420UL,
        ];

        for (int i = 0; i < MissionCatalog.All.Length; i++)
        {
            Assert.Equal(expected[i], StateHash.Compute(Mission(MissionCatalog.All[i].Id)));
        }
    }
}
