using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class VictoryTests
{
    private static SimWorld World() => new(seed: 4242, capacity: 32);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
        => world.Spawn(
            faction,
            team,
            kind,
            new WorldPos(x, 0, z),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            UnitCatalog.Get(kind).Health);

    [Fact]
    public void OutcomeStartsOngoing()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 100_000, 0);

        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
    }

    [Fact]
    public void AllianceWinsWhenTheWestHasNoStructuresLeft()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);

        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void WesternWinsWhenBothAlliedTeamsAreWipedOut()
    {
        SimWorld world = World();
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 100_000, 0);

        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    [Fact]
    public void TheChineseAloneCanStillWin()
    {
        SimWorld world = World();
        Spawn(world, Faction.Chinese, 1, UnitKind.Factory, 0, 0);

        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Victory, world.Outcome);
    }

    [Fact]
    public void MutualAnnihilationIsADraw()
    {
        SimWorld world = World();
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Draw, world.Outcome);
    }

    [Fact]
    public void LosingTheHeadquartersIsNotDefeatWhileOtherStructuresStand()
    {
        SimWorld world = World();
        EntityId headquarters = Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, 40_000, 0);
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 200_000, 0);

        world.Despawn(headquarters);
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Ongoing, world.Outcome);
        Assert.True(VictorySystem.HasStructures(world, 0));
    }

    [Fact]
    public void LosingEveryStructureEndsTheGame()
    {
        SimWorld world = World();
        EntityId headquarters = Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 200_000, 0);

        world.Despawn(headquarters);
        world.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Defeat, world.Outcome);
    }

    [Fact]
    public void OutcomeIsPartOfTheStateHash()
    {
        SimWorld ongoing = World();
        Spawn(ongoing, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);
        Spawn(ongoing, Faction.Western, 2, UnitKind.CommandCentre, 100_000, 0);
        ongoing.RunTicks(VictorySystem.CheckInterval + 1);

        SimWorld decided = World();
        Spawn(decided, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);
        decided.RunTicks(VictorySystem.CheckInterval + 1);

        Assert.Equal(GameOutcome.Ongoing, ongoing.Outcome);
        Assert.Equal(GameOutcome.Victory, decided.Outcome);
        Assert.NotEqual(StateHash.Compute(ongoing), StateHash.Compute(decided));
    }

    [Fact]
    public void CountStructuresMatchesReality()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, 0, 0);
        Spawn(world, Faction.Soviet, 0, UnitKind.Factory, 40_000, 0);
        Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 80_000, 0);

        Assert.Equal(2, VictorySystem.CountStructures(world, 0));
    }
}
