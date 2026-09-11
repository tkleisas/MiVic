using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Replay;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Campaign;

/// <summary>
/// The mission's script: a condition and its actions, evaluated on the tick, in list order.
/// <para>
/// Three things are under test here and they are not the same thing. The first is that a trigger
/// fires <em>once</em> — the memory, and the reason it is state. The second is that every
/// condition and every action does what its name says, because a vocabulary that is half-wired is
/// worse than a smaller one. The third is the failure this project keeps meeting: a trigger that
/// is authored and can never fire, which the counting test on the shipped mission and the script
/// validation both exist to catch.
/// </para>
/// </summary>
public sealed class TriggerTests
{
    private const int Capacity = 1024;

    /// <summary>
    /// A world playing a mission of this repository's own layout with a script written for the
    /// test. The objectives are a single scripted one, which nothing in the world can satisfy and
    /// no time limit can end: a test about a trigger must not have the mission deciding its own
    /// outcome in the middle of it.
    /// </summary>
    private static SimWorld Scripted(params TriggerDefinition[] triggers)
        => Build(MissionCatalog.Require("m1_bridgehead") with
        {
            Objectives = [new ObjectiveDefinition(ObjectiveKind.Scripted, "Δοκιμή.")],
            TimeLimitTicks = 0,
            Triggers = triggers,
        });

    /// <summary>The same world with no script at all, as the control a measurement is taken against.</summary>
    private static SimWorld Unscripted() => Build(MissionCatalog.Require("m1_bridgehead") with
    {
        Objectives = [new ObjectiveDefinition(ObjectiveKind.Scripted, "Δοκιμή.")],
        TimeLimitTicks = 0,
        Triggers = [],
    });

    private static SimWorld Build(MissionDefinition mission)
    {
        var world = Scenario.NewWorld(ScenarioKind.Mission, mission.Seed, Capacity, mission);
        Scenario.BuildMission(world, mission);
        return world;
    }

    private static SimWorld Pass() => Build(MissionCatalog.Require("m4_pass"));

    private static int IndexOf(SimWorld world, string triggerId)
    {
        int index = world.Mission!.IndexOfTrigger(triggerId);
        Assert.True(index >= 0, $"The mission has no trigger called '{triggerId}'.");
        return index;
    }

    private static long Fired(SimWorld world, string triggerId)
    {
        TriggerState state = world.TriggerStates[IndexOf(world, triggerId)];
        Assert.True(state.HasFired, $"The trigger '{triggerId}' never fired.");
        return state.FiredTick;
    }

    /// <summary>
    /// Puts <paramref name="count"/> of a team's mobile units inside a circle, skipping any that
    /// are already there: a test that says "three units are in the pass" has to move three units,
    /// and moving the same one twice is the mistake this parameter exists to prevent.
    /// </summary>
    private static int TeleportInto(SimWorld world, int team, int count, int centreX, int centreZ, int skipWithinMm = 0)
    {
        int moved = 0;

        for (int slot = 0; slot < world.Capacity && moved < count; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team || UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            if (skipWithinMm > 0 &&
                Math.Abs(entity.Position.X - centreX) + Math.Abs(entity.Position.Z - centreZ) < skipWithinMm)
            {
                continue;
            }

            entity.Position = new WorldPos(centreX, entity.Position.Y, centreZ);
            entity.HasMoveGoal = false;
            moved++;
        }

        return moved;
    }

    /// <summary>
    /// Where a team's headquarters actually stands. The scenario searches for a base site and
    /// moves the base onto ground that can hold it, so a test that assumed the coordinates the
    /// mission asked for would be testing the map rather than the trigger.
    /// </summary>
    private static WorldPos Headquarters(SimWorld world, int team)
    {
        List<int> centres = Structures(world, team, UnitKind.CommandCentre);
        Assert.NotEmpty(centres);

        return world.GetRefBySlot(centres[0]).Position;
    }

    /// <summary>Every live structure of a team and a role, as slots in ascending order.</summary>
    private static List<int> Structures(SimWorld world, int team, UnitKind role)
    {
        var slots = new List<int>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == role && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                slots.Add(slot);
            }
        }

        return slots;
    }

    // ---------------------------------------------------------------- the memory

    /// <summary>
    /// <b>A trigger fires exactly once, and its condition being true again does not fire it
    /// again.</b> The condition is time, so it stays true for the rest of the match; the evidence
    /// is the mission's message ledger, which has one line in it however long the match runs.
    /// </summary>
    [Fact]
    public void ATriggerFiresExactlyOnce()
    {
        SimWorld world = Scripted(new TriggerDefinition(
            "once",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 10),
            [new TriggerAction(TriggerActionKind.Message, GreekText: "Μία φορά.")]));

        world.RunTicks(9);
        Assert.False(world.TriggerStates[0].HasFired, "The trigger fired before its condition held.");
        Assert.Empty(world.MissionMessages);

        world.RunTicks(1);
        Assert.True(world.TriggerStates[0].HasFired);
        Assert.Equal(10, world.TriggerStates[0].FiredTick);
        Assert.Single(world.MissionMessages);

        // Ten seconds later the condition is still true, and that changes nothing.
        world.RunTicks(200);
        Assert.Single(world.MissionMessages);
        Assert.Equal(10, world.TriggerStates[0].FiredTick);
    }

    /// <summary>
    /// The memory is written before the trigger's own actions run, so a trigger whose action makes
    /// its condition true again — a spawn into the very area it watches — is still a trigger that
    /// fired once.
    /// </summary>
    [Fact]
    public void ATriggerThatSpawnsIntoItsOwnConditionStillFiresOnce()
    {
        // A point the test chooses, because the scenario moves a base onto ground that can hold
        // it: the circle is put where a unit is put, rather than where the mission asked.
        SimWorld world = Scripted(new TriggerDefinition(
            "self",
            new TriggerCondition(
                TriggerConditionKind.UnitInArea,
                Team: 0,
                Count: 1,
                CentreX: 0,
                CentreZ: 0,
                RadiusMm: 40_000),
            [new TriggerAction(
                TriggerActionKind.Spawn,
                Team: 0,
                Role: UnitKind.Infantry,
                Count: 1,
                CentreX: 0,
                CentreZ: 0)]));

        int before = CountInfantry(world, 0);

        // Nobody is in the circle, so nothing fires however long the match runs.
        world.RunTicks(40);
        Assert.False(world.TriggerStates[0].HasFired);

        // One unit walks in: the trigger fires once, and the infantryman it spawns into its own
        // circle does not fire it again.
        Assert.Equal(1, TeleportInto(world, 0, 1, 0, 0));
        world.RunTicks(60);

        Assert.True(world.TriggerStates[0].HasFired);
        Assert.Equal(41, Fired(world, "self"));
        Assert.Equal(before + 1, CountInfantry(world, 0));
    }

    private static int CountInfantry(SimWorld world, int team)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) &&
                world.GetRefBySlot(slot).TeamId == team &&
                world.GetRefBySlot(slot).Kind == UnitKind.Infantry)
            {
                count++;
            }
        }

        return count;
    }

    // ---------------------------------------------------------------- the hash

    /// <summary>
    /// <b>The fired state is in the hash: two worlds that differ only in a fired trigger hash
    /// differently.</b>
    /// <para>
    /// The two worlds are the same seed, the same layout, the same tick and the same entities —
    /// the two missions differ only in <em>when</em> their one trigger fires, and a mission
    /// definition is not part of the hash (only its id is). The one action is a message, which
    /// changes nothing about the world. So the only thing that can make these two hashes differ is
    /// the trigger's memory of having fired, and on which tick: exactly the hole this test exists
    /// to keep closed, in the shape of the two this project closed the week before.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFiredStateIsInTheStateHash()
    {
        static SimWorld Firing(uint atTick) => Scripted(new TriggerDefinition(
            "when",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: (int)atTick),
            [new TriggerAction(TriggerActionKind.Message, GreekText: "Τότε.")]));

        // One has fired and the other has not, at the same tick of the same world.
        SimWorld early = Firing(10);
        SimWorld late = Firing(100);
        early.RunTicks(50);
        late.RunTicks(50);

        Assert.True(early.TriggerStates[0].HasFired);
        Assert.False(late.TriggerStates[0].HasFired);
        Assert.NotEqual(StateHash.Compute(early), StateHash.Compute(late));

        // And the tick it fired on is part of that memory rather than only the yes: two worlds
        // that both fired, twenty ticks apart, still hash differently.
        SimWorld first = Firing(10);
        SimWorld second = Firing(30);
        first.RunTicks(40);
        second.RunTicks(40);

        Assert.Equal(10, first.TriggerStates[0].FiredTick);
        Assert.Equal(30, second.TriggerStates[0].FiredTick);
        Assert.NotEqual(StateHash.Compute(first), StateHash.Compute(second));

        // The control: the same world twice is the same hash, so the two above differ because of
        // what is in them and not because hashing is unstable.
        SimWorld once = Firing(10);
        SimWorld twice = Firing(10);
        once.RunTicks(40);
        twice.RunTicks(40);

        Assert.Equal(StateHash.Compute(once), StateHash.Compute(twice));
    }

    /// <summary>
    /// <b>A mission flag is in the hash too.</b> The two worlds below are identical in everything
    /// else — same seed, same layout, same entities, same tick, and the same trigger firing on the
    /// same tick — and differ only in that one of them raised a flag and the other sent a message.
    /// If the flag were not hashed these two would hash the same, which is why this is the test
    /// that would fail the day someone dropped the flag word out of <see cref="StateHash"/>.
    /// </summary>
    [Fact]
    public void AMissionFlagIsInTheStateHash()
    {
        SimWorld raised = Scripted(new TriggerDefinition(
            "mark",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 5),
            [new TriggerAction(TriggerActionKind.SetFlag, Flag: 0)]));

        SimWorld talked = Scripted(new TriggerDefinition(
            "mark",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 5),
            [new TriggerAction(TriggerActionKind.Message, GreekText: "Τίποτα.")]));

        raised.RunTicks(20);
        talked.RunTicks(20);

        Assert.Equal(Fired(raised, "mark"), Fired(talked, "mark"));
        Assert.True(raised.IsMissionFlagSet(0));
        Assert.False(talked.IsMissionFlagSet(0));
        Assert.NotEqual(StateHash.Compute(raised), StateHash.Compute(talked));
    }

    /// <summary>
    /// The other half of the hashing decision, stated as a test: the world keeps <em>no</em> script
    /// state for a match that has no script. A skirmish has no trigger states and no flag words to
    /// hash, which is why adding the layer moved no golden hash in the repository.
    /// </summary>
    [Fact]
    public void AMatchWithNoScriptKeepsNoScriptState()
    {
        var skirmish = new SimWorld(20250101UL, Capacity);

        Assert.Null(skirmish.Mission);
        Assert.Equal(0, skirmish.TriggerStates.Length);
        Assert.Equal(0, skirmish.MissionFlags.Length);
        Assert.Empty(skirmish.MissionMessages);

        // And the same for the three campaign missions, which are triggerless on purpose.
        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            if (mission.HasTriggers)
            {
                continue;
            }

            SimWorld world = Build(mission);

            Assert.Equal(0, world.TriggerStates.Length);
            Assert.Equal(0, world.MissionFlags.Length);
        }
    }

    // ---------------------------------------------------------------- conditions

    [Fact]
    public void TimeElapsedCountsTicks()
    {
        SimWorld world = Scripted();
        var condition = new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 120);

        world.RunTicks(119);
        Assert.False(TriggerSystem.Satisfied(world, condition));

        world.RunTicks(1);
        Assert.True(TriggerSystem.Satisfied(world, condition));
    }

    [Fact]
    public void UnitInAreaCountsOnlyTheTeamAsked()
    {
        SimWorld world = Scripted();
        var condition = new TriggerCondition(
            TriggerConditionKind.UnitInArea, Team: 0, Count: 3, CentreX: 0, CentreZ: 0, RadiusMm: 30_000);

        Assert.Equal(0, world.CountUnitsInArea(0, 0, 0, 30_000));
        Assert.False(TriggerSystem.Satisfied(world, condition));

        Assert.Equal(2, TeleportInto(world, 0, 2, 0, 0));
        Assert.Equal(2, world.CountUnitsInArea(0, 0, 0, 30_000));
        Assert.False(TriggerSystem.Satisfied(world, condition));

        // An enemy inside the same circle is not one of the three: the condition counts the team
        // it names, which is the whole reason the team is a field.
        Assert.Equal(1, TeleportInto(world, 2, 1, 0, 0));
        Assert.False(TriggerSystem.Satisfied(world, condition));

        Assert.Equal(1, TeleportInto(world, 0, 1, 0, 0, skipWithinMm: 30_000));
        Assert.Equal(3, world.CountUnitsInArea(0, 0, 0, 30_000));
        Assert.True(TriggerSystem.Satisfied(world, condition));

        // And a circle somewhere else counts nobody, however many units are in this one.
        Assert.False(TriggerSystem.Satisfied(
            world, new TriggerCondition(
                TriggerConditionKind.UnitInArea, Team: 0, Count: 1, CentreX: 250_000, CentreZ: 250_000, RadiusMm: 10_000)));
    }

    [Fact]
    public void StructureDestroyedCountsLossesAndSurvivesARebuild()
    {
        SimWorld world = Scripted();
        var condition = new TriggerCondition(TriggerConditionKind.StructureDestroyed, Team: 2, Count: 2);

        Assert.False(TriggerSystem.Satisfied(world, condition));

        List<int> enemy = Structures(world, 2, UnitKind.CommandCentre);
        Assert.Single(enemy);
        world.Despawn(new EntityId(enemy[0], world.GetRefBySlot(enemy[0]).Generation));

        Assert.False(TriggerSystem.Satisfied(world, condition));
        Assert.Equal(1, world.TeamRef(2).StructuresLost);

        // A structure the enemy raises afterwards does not undo the loss, which is why the
        // condition reads a ledger rather than comparing a count of what is standing.
        world.Spawn(Faction.Western, 2, UnitKind.CommandCentre, new WorldPos(0, 0, 250_000), default, 5_000);
        Assert.Equal(1, world.CountStructures(2, UnitKind.CommandCentre));
        Assert.Equal(1, world.TeamRef(2).StructuresLost);

        world.Despawn(new EntityId(
            Structures(world, 2, UnitKind.CommandCentre)[^1],
            world.GetRefBySlot(Structures(world, 2, UnitKind.CommandCentre)[^1]).Generation));

        Assert.True(TriggerSystem.Satisfied(world, condition));
    }

    [Fact]
    public void StructuresBelowCountsWhatIsStandingAndCanBeNarrowedToOneRole()
    {
        SimWorld world = Scripted();

        int factories = world.CountStructures(2, UnitKind.Factory);
        Assert.Equal(1, factories);

        // Every structure the team has: the enemy's base, which is four buildings.
        Assert.Equal(4, world.CountStructures(2, UnitKind.None));
        Assert.False(TriggerSystem.Satisfied(
            world, new TriggerCondition(TriggerConditionKind.StructuresBelow, Team: 2, Count: 4)));
        Assert.False(TriggerSystem.Satisfied(
            world, new TriggerCondition(TriggerConditionKind.StructuresBelow, Team: 2, Count: 1, Role: UnitKind.Factory)));

        // A role the team does not field is already below any positive number, which is the trap
        // the mission validation warns about rather than a fault in the condition.
        Assert.True(TriggerSystem.Satisfied(
            world, new TriggerCondition(TriggerConditionKind.StructuresBelow, Team: 2, Count: 1, Role: UnitKind.RadarStation)));

        List<int> guns = [.. Structures(world, 2, UnitKind.Factory)];
        world.Despawn(new EntityId(guns[0], world.GetRefBySlot(guns[0]).Generation));

        Assert.True(TriggerSystem.Satisfied(
            world, new TriggerCondition(TriggerConditionKind.StructuresBelow, Team: 2, Count: 1, Role: UnitKind.Factory)));

        // A team the match does not declare stands in nothing at all, so "fewer than one of their
        // structures stands" is literally true of it. That is not a fault in the condition — it
        // answers what it was asked — but it is exactly why the mission validation refuses a
        // condition about a team the match does not declare, rather than leaving the author to
        // discover a trigger that fires at once.
        Assert.False(world.IsTeamInPlay(3));
        Assert.True(TriggerSystem.Satisfied(
            world, new TriggerCondition(TriggerConditionKind.StructuresBelow, Team: 3, Count: 1)));
    }

    /// <summary>
    /// A flag set by an earlier trigger, read by a later one — on the same tick, because that is
    /// what "the list order is the order of the events" means. The reader's action is a message,
    /// so the two lines in the ledger are the two triggers, in the order the list gives them.
    /// </summary>
    [Fact]
    public void AFlagSetByAnEarlierTriggerIsReadOnTheSameTick()
    {
        SimWorld world = Scripted(
            new TriggerDefinition(
                "raise",
                new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 7),
                [
                    new TriggerAction(TriggerActionKind.Message, GreekText: "Πρώτο."),
                    new TriggerAction(TriggerActionKind.SetFlag, Flag: 0),
                ]),
            new TriggerDefinition(
                "read",
                new TriggerCondition(TriggerConditionKind.FlagSet, Flag: 0),
                [new TriggerAction(TriggerActionKind.Message, GreekText: "Δεύτερο.")]));

        world.RunTicks(6);
        Assert.False(world.TriggerStates[1].HasFired);
        Assert.False(world.IsMissionFlagSet(0));

        world.RunTicks(1);
        Assert.True(world.IsMissionFlagSet(0));
        Assert.Equal(7, Fired(world, "raise"));
        Assert.Equal(7, Fired(world, "read"));
        Assert.Equal(["Πρώτο.", "Δεύτερο."], world.MissionMessages.Select(message => message.GreekText));
    }

    // ---------------------------------------------------------------- actions

    [Fact]
    public void SpawnPutsUnitsAndStructuresOnTheMap()
    {
        SimWorld world = Scripted(new TriggerDefinition(
            "reinforce",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 3),
            [
                new TriggerAction(
                    TriggerActionKind.Spawn, Team: 0, Role: UnitKind.Tank, Count: 3, CentreX: -180_000, CentreZ: -180_000),
                new TriggerAction(
                    TriggerActionKind.Spawn, Team: 0, Role: UnitKind.GunEmplacement, Count: 1, CentreX: -150_000, CentreZ: -150_000),
            ]));

        // The scenario lays out a force of its own, so the spawn is measured as a difference: a
        // count taken on its own would be a count of the mission's opening army.
        int tanks = world.CountOf(0, UnitKind.Tank);
        int guns = world.CountStructures(0, UnitKind.GunEmplacement);

        Assert.Equal(0, guns);
        world.RunTicks(3);

        Assert.Equal(tanks + 3, world.CountOf(0, UnitKind.Tank));
        Assert.Equal(guns + 1, world.CountStructures(0, UnitKind.GunEmplacement));

        // The army is the team the mission named, and the units are the role it named: a spawn
        // that produced the wrong side's tanks would still pass a count.
        foreach (int slot in Structures(world, 0, UnitKind.GunEmplacement))
        {
            ref Entity gun = ref world.GetRefBySlot(slot);
            Assert.Equal(0, gun.TeamId);
            Assert.Equal(Faction.Soviet, gun.Faction);
        }
    }

    /// <summary>
    /// A scripted spawn obeys the world's own ground rule: a gun asked for in the middle of a lake
    /// stands on the nearest solid ground instead, because a building in the water is a building
    /// nobody can reach or use.
    /// </summary>
    [Fact]
    public void ASpawnThatWouldLandInWaterIsMovedOntoGround()
    {
        SimWorld probe = Scripted();
        int water = -1;

        for (int cell = 0; cell < probe.TerrainTypes.CellCount; cell++)
        {
            if (probe.TerrainTypes.TypeAt(cell) == TerrainType.DeepWater)
            {
                water = cell;
                break;
            }
        }

        Assert.True(water >= 0, "The test map has no deep water to test the ground rule against.");
        WorldPos centre = probe.Navigation.CentreOf(water);

        SimWorld world = Scripted(new TriggerDefinition(
            "bridgehead",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 2),
            [new TriggerAction(
                TriggerActionKind.Spawn,
                Team: 0,
                Role: UnitKind.GunEmplacement,
                Count: 1,
                CentreX: centre.X,
                CentreZ: centre.Z)]));

        world.RunTicks(2);

        List<int> guns = Structures(world, 0, UnitKind.GunEmplacement);
        Assert.Single(guns);

        ref Entity gun = ref world.GetRefBySlot(guns[0]);
        int landed = world.Navigation.IndexOfWorld(gun.Position);

        Assert.NotEqual(TerrainType.DeepWater, world.TerrainTypes.TypeAt(landed));
        Assert.NotEqual(TerrainType.Lava, world.TerrainTypes.TypeAt(landed));
        Assert.True(gun.Position.X != centre.X || gun.Position.Z != centre.Z, "The gun was left in the water.");
    }

    [Fact]
    public void RevealLiftsTheFogAndThenLetsItBackButKeepsTheMap()
    {
        SimWorld world = Scripted(new TriggerDefinition(
            "look",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 5),
            [new TriggerAction(
                TriggerActionKind.Reveal, Team: 0, CentreX: 200_000, CentreZ: 200_000, RadiusMm: 50_000, Ticks: 100)]));

        int cell = world.Navigation.IndexOfWorld(new WorldPos(200_000, 0, 200_000));

        Assert.False(world.Visibility.IsVisible(0, cell));
        Assert.False(world.Visibility.IsExplored(0, cell));

        world.RunTicks(5);

        Assert.True(world.Visibility.IsVisible(0, cell), "The reveal did not lift the fog.");
        Assert.True(world.Visibility.IsExplored(0, cell));

        // Still lit while the reveal lasts, and let go when its time is up.
        world.RunTicks(90);
        Assert.True(world.Visibility.IsVisible(0, cell));

        world.RunTicks(20);
        Assert.False(world.Visibility.IsVisible(0, cell), "The reveal outlived its duration.");

        // The ground stays on the team's map: a reveal is watching, and what has been seen is
        // remembered, which is what the fog grid already does with ground a unit walked away from.
        Assert.True(world.Visibility.IsExplored(0, cell));
    }

    /// <summary>
    /// One action for granting and for removing, because the arithmetic and the floor are the same
    /// either way: a negative amount takes resources away, and no amount can take a stockpile
    /// below zero — a negative stockpile is a debt nothing in the economy can express.
    /// </summary>
    [Fact]
    public void AdjustResourcesGrantsAndRemovesAndFloorsAtZero()
    {
        SimWorld granted = Scripted(new TriggerDefinition(
            "grant",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 4),
            [new TriggerAction(TriggerActionKind.AdjustResources, Team: 0, Materials: 250, Water: -100)]));

        SimWorld control = Unscripted();

        granted.RunTicks(4);
        control.RunTicks(4);

        Assert.Equal(250, granted.TeamRef(0).Materials - control.TeamRef(0).Materials);
        Assert.Equal(-100, granted.TeamRef(0).Water - control.TeamRef(0).Water);

        SimWorld stripped = Scripted(new TriggerDefinition(
            "strip",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 4),
            [new TriggerAction(TriggerActionKind.AdjustResources, Team: 2, Materials: -1_000_000)]));

        stripped.RunTicks(4);

        Assert.Equal(0, stripped.TeamRef(2).Materials);
        Assert.True(stripped.TeamRef(2).Energy > 0, "Removing materials took the energy with it.");
    }

    [Fact]
    public void ATriggerCanCompleteAScriptedObjective()
    {
        SimWorld world = Scripted(new TriggerDefinition(
            "done",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 6),
            [new TriggerAction(TriggerActionKind.CompleteObjective, Objective: 0)]));

        Assert.True(world.Objectives[0].IsPending);

        // Objective evaluation runs every tenth tick, so the trigger firing on tick six is
        // followed by the objective check that sees it on tick ten.
        world.RunTicks(10);

        Assert.True(world.Objectives[0].IsComplete);
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    /// <summary>
    /// A scripted objective is judged by the mission and by nothing else: no amount of time
    /// satisfies it, which is what makes "complete this objective" an action worth having rather
    /// than a way of restating a predicate.
    /// </summary>
    [Fact]
    public void AScriptedObjectiveIsNotSatisfiedByTime()
    {
        SimWorld world = Build(MissionCatalog.Require("m1_bridgehead") with
        {
            Objectives = [new ObjectiveDefinition(ObjectiveKind.Scripted, "Ποτέ μόνος του.", DeadlineTick: 50)],
            TimeLimitTicks = 0,
            Triggers = [],
        });

        world.RunTicks(60);

        Assert.True(world.Objectives[0].IsFailed, "A scripted objective completed itself.");
        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    [Fact]
    public void MessageIsShownToThePlayerWithTheTickItArrived()
    {
        SimWorld world = Scripted(new TriggerDefinition(
            "speak",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 12),
            [new TriggerAction(TriggerActionKind.Message, GreekText: "Ο αυχένας είναι ήσυχος.")]));

        world.RunTicks(30);

        MissionMessage message = Assert.Single(world.MissionMessages);
        Assert.Equal(12, message.Tick);
        Assert.Equal("Ο αυχένας είναι ήσυχος.", message.GreekText);
    }

    /// <summary>
    /// A group order goes through the command queue like any other order, and it is one order per
    /// unit inside the circle — so the unit that was outside it is left alone.
    /// </summary>
    [Fact]
    public void OrderGroupMovesTheUnitsInsideTheCircleAndNobodyElse()
    {
        SimWorld probe = Scripted();
        WorldPos baseSite = Headquarters(probe, 0);
        int groupX = baseSite.X;
        int groupZ = baseSite.Z;

        SimWorld world = Scripted(new TriggerDefinition(
            "march",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 4),
            [new TriggerAction(
                TriggerActionKind.OrderGroup,
                Team: 0,
                Order: GroupOrder.Move,
                CentreX: groupX,
                CentreZ: groupZ,
                RadiusMm: 45_000,
                TargetX: groupX + 60_000,
                TargetZ: groupZ + 60_000)]));

        // One unit is deliberately outside the circle the order names.
        TeleportInto(world, 0, 1, 250_000, 250_000);
        int inside = world.CountUnitsInArea(0, groupX, groupZ, 45_000);
        Assert.True(inside >= 3, $"Only {inside} units were inside the group.");

        world.RunTicks(5);

        int marching = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != 0 || UnitCatalog.Get(entity.Kind).IsBuilding || !entity.HasMoveGoal)
            {
                continue;
            }

            // The goal is the ordered point, which the movement layer has not reached yet in the
            // one tick that has passed since the command executed.
            if (Math.Abs(entity.MoveGoal.X - (groupX + 60_000)) < 20_000 &&
                Math.Abs(entity.MoveGoal.Z - (groupZ + 60_000)) < 20_000)
            {
                marching++;
            }
        }

        Assert.Equal(inside, marching);
    }

    [Fact]
    public void OrderGroupAttacksTheNearestEnemyToThePoint()
    {
        SimWorld probe = Scripted();
        WorldPos baseSite = Headquarters(probe, 0);
        WorldPos enemyBase = Headquarters(probe, 2);

        SimWorld world = Scripted(new TriggerDefinition(
            "engage",
            new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 4),
            [new TriggerAction(
                TriggerActionKind.OrderGroup,
                Team: 0,
                Order: GroupOrder.Attack,
                CentreX: baseSite.X,
                CentreZ: baseSite.Z,
                RadiusMm: 45_000,
                TargetX: enemyBase.X,
                TargetZ: enemyBase.Z)]));

        world.RunTicks(6);

        int ordered = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != 0 || !entity.HasAttackOrder)
            {
                continue;
            }

            // The target is an enemy of the team that was ordered, chosen by the world rather
            // than named by the mission: a mission is data and can name a place, not an entity.
            ref Entity victim = ref world.GetRefBySlot(entity.TargetSlot);
            Assert.True(world.IsHostile(0, victim.TeamId), "A unit was ordered to attack a friend.");
            ordered++;
        }

        Assert.True(ordered > 0, "No unit was given an attack order.");
    }

    // ---------------------------------------------------------------- the shipped mission

    /// <summary>
    /// <b>Every trigger in the demonstration mission fires, and fires when it should.</b>
    /// <para>
    /// The count is the point. This project has been bitten three times by a feature that exists
    /// and never happens — a volcano line above every cell, a sand band below the mud line, an
    /// entire mud mechanic that was inert — and a trigger that is authored and never fires is that
    /// same bug wearing a script. So the mission is played out here: the clock, the column walking
    /// into the pass, and the two guns being destroyed are all driven, and then the number of
    /// triggers that fired is compared against the number the mission declares.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryTriggerInTheShippedMissionFiresWhenItShould()
    {
        MissionDefinition mission = MissionCatalog.Require("m4_pass");
        SimWorld world = Pass();

        Assert.Equal(6, mission.Triggers.Count);
        Assert.Equal(mission.Triggers.Count, world.TriggerStates.Length);

        // The mission places its own ambush: two guns on the ridge, on the first tick.
        world.RunTicks(1);
        Assert.Equal(1, Fired(world, "preparation"));
        Assert.Equal(2, world.CountStructures(2, UnitKind.GunEmplacement));
        Assert.False(world.TriggerStates[IndexOf(world, "warning")].HasFired);

        // Twenty seconds later the scouts report, and the pass comes out of the fog.
        int passCell = world.Navigation.IndexOfWorld(new WorldPos(-75_000, 0, 10_000));
        Assert.False(world.Visibility.IsVisible(0, passCell));

        world.RunTicks(399);
        Assert.Equal(400, Fired(world, "warning"));
        Assert.True(world.Visibility.IsVisible(0, passCell), "The warning did not reveal the pass.");
        Assert.Single(world.MissionMessages);

        // The column enters the pass. A test cannot march one, and what is under test is the
        // trigger rather than the pathfinder, so three units are put where the column would be.
        int westernTanks = world.CountOf(2, UnitKind.Tank);
        Assert.Equal(3, TeleportInto(world, 0, 3, -75_000, 10_000));
        world.RunTicks(1);

        Assert.Equal(401, Fired(world, "ambush"));

        // The flag the ambush raised is read by the next trigger on the very same tick: the list's
        // order is the order of the events.
        Assert.Equal(401, Fired(world, "counterattack"));
        Assert.True(world.IsMissionFlagSet(0));
        Assert.Equal(westernTanks + 4, world.CountOf(2, UnitKind.Tank));
        Assert.True(world.Visibility.IsVisible(0, world.Navigation.IndexOfWorld(new WorldPos(21_000, 0, -43_000))));

        // The fight, which a headless test cannot fight: the two guns on the ridge are destroyed.
        List<int> guns = Structures(world, 2, UnitKind.GunEmplacement);
        Assert.Equal(2, guns.Count);

        world.Despawn(new EntityId(guns[0], world.GetRefBySlot(guns[0]).Generation));
        world.RunTicks(1);

        Assert.Equal(402, Fired(world, "first-gun"));
        Assert.False(world.TriggerStates[IndexOf(world, "ambush-broken")].HasFired);

        List<int> remaining = Structures(world, 2, UnitKind.GunEmplacement);
        Assert.Single(remaining);
        world.Despawn(new EntityId(remaining[0], world.GetRefBySlot(remaining[0]).Generation));
        world.RunTicks(1);

        Assert.Equal(403, Fired(world, "ambush-broken"));

        // All six, in the order the mission lists them, each on the tick its condition held.
        for (int i = 0; i < mission.Triggers.Count; i++)
        {
            Assert.True(
                world.TriggerStates[i].HasFired,
                $"Trigger '{mission.Triggers[i].Id}' never fired: {mission.Triggers[i].Note}");
        }

        Assert.Equal(
            [1L, 400L, 401L, 401L, 402L, 403L],
            Enumerable.Range(0, mission.Triggers.Count).Select(i => world.TriggerStates[i].FiredTick));

        // And what they did: the scripted objective completed, the messages were shown, and the
        // resources moved the way the mission says they did.
        Assert.True(world.Objectives[1].IsComplete, "The scripted objective did not complete.");
        Assert.Equal(5, world.MissionMessages.Count);
        Assert.Contains(world.MissionMessages, message => message.GreekText.StartsWith("Ενέδρα!", StringComparison.Ordinal));
    }

    /// <summary>
    /// A trigger is a memory, and a mission replayed from its own log must reproduce it: a world
    /// that spawned its ambush on one machine and not on the other is a desync nothing else in the
    /// hash could show. Worth its own test because the actions go through the command queue from
    /// inside a tick and are deliberately not recorded, so a replay re-derives them.
    /// </summary>
    [Fact]
    public void AScriptedMissionReplaysExactly()
    {
        SimWorld world = Pass();
        world.StartRecording();
        world.RunTicks(600);

        Assert.Equal(2, world.CountStructures(2, UnitKind.GunEmplacement));

        ReplayResult result = ReplayFile.Capture(world, ScenarioKind.Mission).Verify();

        Assert.True(result.Matches, $"The scripted mission diverged: {result.ExpectedHash} != {result.ActualHash}.");
    }

    /// <summary>
    /// No shipped trigger fires before the tick its author meant it to. The check is made after
    /// the mission's opening tick rather than before it, because the opening tick is part of the
    /// script: a mission places what the scenario cannot — a gun on a ridge — with a trigger that
    /// fires on tick one, and the conditions that count what it placed are evaluated after it, in
    /// list order, on the same tick. What this catches is the other thing: a condition that is
    /// already true of the map and fires the moment it is first asked, which is a trigger whose
    /// author meant something else.
    /// </summary>
    [Fact]
    public void NoShippedTriggerFiresBeforeItsTime()
    {
        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            if (!mission.HasTriggers)
            {
                continue;
            }

            SimWorld world = Build(mission);
            world.RunTicks(1);

            for (int i = 0; i < mission.Triggers.Count; i++)
            {
                if (!world.TriggerStates[i].HasFired)
                {
                    continue;
                }

                TriggerCondition condition = mission.Triggers[i].Condition;

                Assert.True(
                    condition.Kind == TriggerConditionKind.TimeElapsed && condition.Tick <= world.Tick,
                    $"Trigger '{mission.Triggers[i].Id}' of '{mission.Id}' fired on the first tick " +
                    $"without waiting for the clock: {condition.Kind}.");
            }
        }
    }

    /// <summary>Every shipped mission's script is self-consistent: nothing waits on something that can never happen.</summary>
    [Fact]
    public void EveryShippedScriptPassesValidation()
    {
        foreach (MissionDefinition mission in MissionCatalog.All)
        {
            Assert.Empty(TriggerSystem.Validate(mission));
        }
    }

    /// <summary>
    /// The validation earns its place by finding the failures it was written for: a scripted
    /// objective nothing completes, a denial with no clock to be decided by, a flag nothing raises,
    /// a flag its own trigger raises, a completion pointing at an objective that does not exist, and
    /// a condition asked of a team the match does not declare. Each of them is a trigger that can
    /// never fire, written as a sentence that reads perfectly well.
    /// </summary>
    [Fact]
    public void ValidationCatchesScriptsThatCanNeverHappen()
    {
        MissionDefinition template = MissionCatalog.Require("m4_pass");

        static string Joined(IReadOnlyList<string> problems) => string.Join(" | ", problems);

        // A scripted objective with no trigger to complete it.
        MissionDefinition orphan = template with
        {
            Triggers = [template.Triggers[0] with { Actions = [new TriggerAction(TriggerActionKind.Message, GreekText: "…")] }],
        };

        Assert.Contains("no trigger completes it", Joined(TriggerSystem.Validate(orphan)));

        // A denial with no deadline.
        MissionDefinition endless = template with
        {
            Objectives =
            [
                template.Objectives[0] with { DeadlineTick = 0 },
                template.Objectives[1],
            ],
        };

        Assert.Contains("no deadline", Joined(TriggerSystem.Validate(endless)));

        // A flag nothing raises, and a flag that only the trigger waiting on it raises.
        MissionDefinition unraised = template with
        {
            Triggers =
            [
                new TriggerDefinition(
                    "lonely",
                    new TriggerCondition(TriggerConditionKind.FlagSet, Flag: 3),
                    [new TriggerAction(TriggerActionKind.Message, GreekText: "…")]),
            ],
        };

        Assert.Contains("which no trigger raises", Joined(TriggerSystem.Validate(unraised)));

        MissionDefinition circular = template with
        {
            Triggers =
            [
                new TriggerDefinition(
                    "ouroboros",
                    new TriggerCondition(TriggerConditionKind.FlagSet, Flag: 0),
                    [new TriggerAction(TriggerActionKind.SetFlag, Flag: 0)]),
            ],
        };

        Assert.Contains("only that same trigger raises", Joined(TriggerSystem.Validate(circular)));

        // A completion that names an objective the mission does not have, and a condition on a
        // team the match does not declare.
        MissionDefinition misnumbered = template with
        {
            Triggers =
            [
                new TriggerDefinition(
                    "stray",
                    new TriggerCondition(TriggerConditionKind.UnitInArea, Team: 1, Count: 1, RadiusMm: 10_000),
                    [new TriggerAction(TriggerActionKind.CompleteObjective, Objective: 9)]),
            ],
        };

        string problems = Joined(TriggerSystem.Validate(misnumbered));

        Assert.Contains("does not declare", problems);
        Assert.Contains("which this mission does not have", problems);

        // And the shipped mission itself is clean, so the validator is not simply saying no.
        Assert.Empty(TriggerSystem.Validate(template));
    }

    // ---------------------------------------------------------------- denial

    /// <summary>
    /// <b>The denial objective decides both ways.</b> It is the one objective the vocabulary did
    /// not have: every other kind is something a player does to the enemy, and this is the enemy
    /// being prevented from doing something — which is the shape a mission about getting something
    /// out, and stopping it, is made of.
    /// </summary>
    [Fact]
    public void TheDenialObjectiveIsCompleteWhenTheEnemyNeverArrives()
    {
        SimWorld world = Denial(deadlineTick: 200, out _);

        world.RunTicks(190);
        Assert.True(world.Objectives[0].IsPending, "The denial completed before its deadline.");

        world.RunTicks(10 + MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsComplete, "The denial did not complete at its deadline.");
        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void TheDenialObjectiveFailsTheMomentTheEnemyIsThrough()
    {
        SimWorld world = Denial(deadlineTick: 200, out WorldPos road);

        // Four of the enemy inside the circle: the number the objective names.
        Assert.Equal(4, TeleportInto(world, 2, 4, road.X, road.Z));
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsFailed, "Four enemy units inside the circle did not fail the denial.");
        Assert.Equal(4, world.Objectives[0].Progress);
        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    /// <summary>
    /// Three is not four, and the ground the enemy walked over on the way stays on the panel: the
    /// objective's progress is the high-water mark of the intrusion and does not fall back when
    /// they leave, because "three of them were through a moment ago" is the warning a player needs.
    /// </summary>
    [Fact]
    public void TheDenialObjectiveCountsTheIntrusionAndDoesNotForgetIt()
    {
        SimWorld world = Denial(deadlineTick: 200, out WorldPos road);

        Assert.Equal(3, TeleportInto(world, 2, 3, road.X, road.Z));
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsPending);
        Assert.Equal(3, world.Objectives[0].Progress);

        // They leave. The objective is still pending, and the panel still says what nearly
        // happened.
        TeleportInto(world, 2, 3, 250_000, 250_000, skipWithinMm: 45_000);
        world.RunTicks(MissionSystem.CheckInterval);

        Assert.True(world.Objectives[0].IsPending);
        Assert.Equal(3, world.Objectives[0].Progress);
    }

    /// <summary>
    /// A mission that is one denial and nothing else, so that the outcome is decided by it — and a
    /// mission with <b>no units in it at all</b>, so that the only units the circle can ever contain
    /// are the ones the test puts there.
    /// <para>
    /// That emptiness is the point rather than a convenience. A denial is a question about a
    /// <em>place</em>, and on a map with two armies on it a place is a place somebody is shooting
    /// across: the demonstration mission's own corner is inside the reach of the player's artillery,
    /// and four enemy tanks moved into it are four tanks under fire, one of which dies on the first
    /// tick. The objective would then be measuring a battle instead of itself.
    /// </para>
    /// </summary>
    private static SimWorld Denial(int deadlineTick, out WorldPos road)
    {
        road = new WorldPos(-280_000, 0, -280_000);

        MissionDefinition mission = new(
            Id: "denial_test",
            GreekTitle: "Δοκιμή άρνησης",
            GreekBriefing: "Μια αποστολή με έναν στόχο: ο εχθρός δεν πρέπει να περάσει.",
            Seed: 20250104UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: default,
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 0,
            AllyUnits: 0,

            // Eight units for the enemy and none for the player: the intruders have to exist, and
            // nothing must be able to shoot at them while they are being counted. The enemy's base
            // is 570 m from the circle and its units gather within 90 m of it, so nothing of the
            // enemy's wanders into the place under test either.
            EnemyUnits: 8,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DenyArea,
                    "Οι Δυτικοί δεν πρέπει να φτάσουν στο σημείο διαφυγής.",
                    Team: 0,
                    TargetTeam: 2,
                    TargetCount: 4,
                    CentreX: road.X,
                    CentreZ: road.Z,
                    RadiusMm: 45_000,
                    DeadlineTick: deadlineTick),
            ],
            TimeLimitTicks: 0)
        {
            Roster = MatchRoster.Duel,
        };

        SimWorld world = Build(mission);
        Assert.Equal(0, world.CountUnitsInArea(0, road.X, road.Z, 45_000));
        Assert.Equal(0, world.CountUnitsInArea(2, road.X, road.Z, 45_000));

        return world;
    }
}
