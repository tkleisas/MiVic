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
        var world = new SimWorld(MissionCatalog.Require(id).Seed, Capacity);
        Scenario.BuildMission(world, MissionCatalog.Require(id));
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
    public void CatalogHasThreePlayableMissions()
    {
        Assert.Equal(3, MissionCatalog.All.Length);

        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            Assert.NotEmpty(mission.Id);
            Assert.NotEmpty(mission.GreekTitle);
            Assert.NotEmpty(mission.Objectives);
            Assert.Contains(mission.Objectives, objective => objective.IsPrimary);
            Assert.NotNull(MissionCatalog.Find(mission.Id));
        }
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
        Assert.Equal(GameOutcome.AllianceVictory, world.Outcome);
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
    public void LosingTheCommandCentreFailsTheProtectionObjective()
    {
        SimWorld world = Mission("m3_industry");
        world.Despawn(FirstStructureOf(world, 0));
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[2].IsFailed, "The protection objective did not fail.");
        Assert.Equal(GameOutcome.WesternVictory, world.Outcome);
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
        Assert.Equal(GameOutcome.AllianceVictory, world.Outcome);
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
        Assert.Equal(GameOutcome.WesternVictory, world.Outcome);
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
        // Golden hashes of the three missions' starting worlds: this is what
        // catches a change to the mission layouts that a replay would otherwise
        // reproduce faithfully but wrongly.
        //
        // Last changed by the crossings a team builds becoming state: a bridge records its span,
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
            13455289978335290768UL,
            11658923710920883436UL,
            17860173042412434573UL,
        ];

        for (int i = 0; i < MissionCatalog.All.Length; i++)
        {
            Assert.Equal(expected[i], StateHash.Compute(Mission(MissionCatalog.All[i].Id)));
        }
    }
}
