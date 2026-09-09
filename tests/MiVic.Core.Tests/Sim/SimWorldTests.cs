using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class SimWorldTests
{
    private static SimWorld NewWorld(int capacity = 16) => new(seed: 1234, capacity: capacity);

    [Fact]
    public void Spawn_AssignsSlotsInAscendingOrder()
    {
        SimWorld world = NewWorld();

        EntityId a = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.FromInt(1), 100);
        EntityId b = world.Spawn(Faction.Chinese, 1, UnitKind.Infantry, WorldPos.Origin, Fix32.FromInt(1), 100);
        EntityId c = world.Spawn(Faction.Western, 2, UnitKind.Aircraft, WorldPos.Origin, Fix32.FromInt(1), 100);

        Assert.Equal(0, a.Slot);
        Assert.Equal(1, b.Slot);
        Assert.Equal(2, c.Slot);
        Assert.Equal(3, world.AliveCount);
    }

    [Fact]
    public void Spawn_InitialisesMoraleFromTheFactionProfile()
    {
        SimWorld world = NewWorld();

        EntityId western = world.Spawn(Faction.Western, 2, UnitKind.Tank, WorldPos.Origin, Fix32.FromInt(1), 100);
        EntityId soviet = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.FromInt(1), 100);

        Assert.True(world.TryGet(western, out Entity west));
        Assert.True(world.TryGet(soviet, out Entity sov));

        // Δυτικοί are the best-equipped and the most brittle; Σοβιετικοί never break.
        Assert.Equal(FactionProfile.Western.MoraleFloor.Raw, west.Morale.Raw);
        Assert.Equal(FactionProfile.Soviet.MoraleFloor.Raw, sov.Morale.Raw);
        Assert.True(sov.Morale > west.Morale);
    }

    [Fact]
    public void Spawn_ThrowsWhenCapacityIsExhausted()
    {
        SimWorld world = NewWorld(2);
        world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 1);
        world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 1);

        Assert.Throws<InvalidOperationException>(
            () => world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 1));
    }

    [Fact]
    public void Despawn_RecyclesSlotAndInvalidatesStaleHandle()
    {
        SimWorld world = NewWorld();
        EntityId first = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 100);

        Assert.True(world.Despawn(first));
        Assert.False(world.IsValid(first));
        Assert.Equal(0, world.AliveCount);

        EntityId second = world.Spawn(Faction.Chinese, 1, UnitKind.Infantry, WorldPos.Origin, Fix32.One, 100);

        // Same slot, new generation: the old handle must not resolve.
        Assert.Equal(first.Slot, second.Slot);
        Assert.NotEqual(first.Generation, second.Generation);
        Assert.False(world.IsValid(first));
        Assert.True(world.IsValid(second));
    }

    [Fact]
    public void Despawn_ReturnsFalseForUnknownOrStaleHandles()
    {
        SimWorld world = NewWorld();
        Assert.False(world.Despawn(EntityId.None));
        Assert.False(world.Despawn(new EntityId(9999, 0)));

        EntityId id = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 1);
        world.Despawn(id);
        Assert.False(world.Despawn(id));
    }

    [Fact]
    public void OrderMove_SetsGoalOnTheNextTick()
    {
        SimWorld world = NewWorld();
        EntityId id = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.FromInt(1), 100);
        WorldPos goal = WorldPos.GroundMetres(10, 0);

        world.OrderMove(id, goal, issuerTeam: 0);

        Assert.True(world.TryGet(id, out Entity before));
        Assert.False(before.HasMoveGoal);

        world.Step();

        Assert.True(world.TryGet(id, out Entity after));
        Assert.True(after.HasMoveGoal);
        Assert.Equal((goal.X, goal.Z), (after.MoveGoal.X, after.MoveGoal.Z));

        // The destination is pinned to the terrain surface, not the caller's Y.
        Assert.Equal(world.Terrain.SampleHeightMm(goal.X, goal.Z), after.MoveGoal.Y);
    }

    [Fact]
    public void OrderMove_IgnoresCommandsFromAnotherTeam()
    {
        SimWorld world = NewWorld();
        EntityId id = world.Spawn(Faction.Soviet, teamId: 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 100);

        world.OrderMove(id, WorldPos.GroundMetres(50, 50), issuerTeam: 1);
        world.Step();

        Assert.True(world.TryGet(id, out Entity e));
        Assert.False(e.HasMoveGoal);
    }

    [Fact]
    public void Enqueue_RejectsCommandsScheduledInThePast()
    {
        SimWorld world = NewWorld();
        EntityId id = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 100);
        world.RunTicks(10);

        Assert.Throws<ArgumentOutOfRangeException>(
            () => world.Enqueue(SimCommand.Move(id, WorldPos.Origin, executeTick: 5, issuerTeam: 0)));
    }

    [Fact]
    public void StopCommand_ClearsTheMoveGoal()
    {
        SimWorld world = NewWorld();
        EntityId id = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.FromInt(1), 100);

        world.OrderMove(id, WorldPos.GroundMetres(100, 0), 0);
        world.Step();
        world.Enqueue(SimCommand.Stop(id, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.TryGet(id, out Entity e));
        Assert.False(e.HasMoveGoal);
    }

    [Fact]
    public void Step_IncrementsTickAndDrainsAppliedCommands()
    {
        SimWorld world = NewWorld();
        EntityId id = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, WorldPos.Origin, Fix32.One, 100);

        world.OrderMove(id, WorldPos.GroundMetres(5, 5), 0);
        Assert.Equal(1, world.PendingCommandCount);

        world.Step();

        Assert.Equal(1, world.Tick);
        Assert.Equal(0, world.PendingCommandCount);
    }

    [Fact]
    public void RunTicks_RejectsNegativeCount()
    {
        SimWorld world = NewWorld();
        Assert.Throws<ArgumentOutOfRangeException>(() => world.RunTicks(-1));
    }

    [Fact]
    public void Constructor_ValidatesCapacity()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimWorld(1, 0));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SimWorld(1, SimConstants.MaxEntities + 1));
    }

    [Fact]
    public void FactionProfiles_EncodeTheAsymmetricDesign()
    {
        // Throughput ordering is Κινέζοι > Σοβιετικοί > Δυτικοί: the ordering is
        // units produced, not wealth.
        Assert.True(FactionProfile.Chinese.ProductionSlots > FactionProfile.Soviet.ProductionSlots);
        Assert.True(FactionProfile.Soviet.ProductionSlots > FactionProfile.Western.ProductionSlots);
        Assert.True(FactionProfile.Chinese.BuildSpeedPermille > FactionProfile.Soviet.BuildSpeedPermille);
        Assert.True(FactionProfile.Soviet.BuildSpeedPermille > FactionProfile.Western.BuildSpeedPermille);

        // Cost runs the same way round, which is why the West's wealth is the only
        // reason it can field its units at all.
        Assert.True(FactionProfile.Chinese.CostPermille < FactionProfile.Soviet.CostPermille);
        Assert.True(FactionProfile.Soviet.CostPermille < FactionProfile.Western.CostPermille);
        Assert.True(FactionProfile.Western.IncomePermille > FactionProfile.Soviet.IncomePermille);

        // Tech ceilings: Κινέζοι lowest, Δυτικοί highest.
        Assert.True(FactionProfile.Chinese.TechCeiling < FactionProfile.Soviet.TechCeiling);
        Assert.True(FactionProfile.Western.TechCeiling > FactionProfile.Soviet.TechCeiling);

        // Δυτικοί have the most brittle morale.
        Assert.True(FactionProfile.Western.MoraleFloor < FactionProfile.Chinese.MoraleFloor);
        Assert.True(FactionProfile.Western.MoraleFloor < FactionProfile.Soviet.MoraleFloor);

        // The mud is the Σοβιετικοί's friend and the Κινέζοι's enemy.
        Assert.True(FactionProfile.Soviet.GroundPressurePermille < FactionProfile.Western.GroundPressurePermille);
        Assert.True(FactionProfile.Western.GroundPressurePermille < FactionProfile.Chinese.GroundPressurePermille);
    }

    [Fact]
    public void TheChineseOutProduceEveryonePerFactory()
    {
        static double Throughput(FactionProfile profile)
            => (double)profile.ProductionSlots * profile.BuildSpeedPermille / profile.CostPermille;

        Assert.True(Throughput(FactionProfile.Chinese) > Throughput(FactionProfile.Soviet));
        Assert.True(Throughput(FactionProfile.Soviet) > Throughput(FactionProfile.Western));
    }

    [Fact]
    public void FactionProfiles_HaveGreekNames()
    {
        Assert.Equal("Σοβιετικοί", FactionProfile.Soviet.GreekName);
        Assert.Equal("Κινέζοι", FactionProfile.Chinese.GreekName);
        Assert.Equal("Δυτικοί", FactionProfile.Western.GreekName);
    }
}
