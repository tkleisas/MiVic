using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The two-tier Σοβιετικοί cost, finished. Standard hulls are cheap; the advanced
/// design is gated behind a prototype run *and* capped, so it is a capability the
/// faction owns rather than a unit type it can spam.
/// </summary>
public sealed class PrototypeCapTests
{
    private static (SimWorld World, EntityId Factory) SovietFactory(ulong techMask)
    {
        SimWorld world = new(seed: 8080, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId factory = world.Spawn(Faction.Soviet, 0, UnitKind.Factory, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 100_000;
        state.Energy = 100_000;
        state.Water = 100_000;
        state.TechTier = 3;
        state.TechMask = techMask;

        // The design has been proven, so this test measures the cap and the
        // research gate rather than the design bureau.
        state.ApprovedMask |= 1u << (int)UnitKind.ElectroPrototype;

        return (world, factory);
    }

    [Fact]
    public void TheElectroPrototypeNeedsItsResearch()
    {
        (SimWorld world, EntityId factory) = SovietFactory(techMask: 0);

        Assert.False(
            UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.ElectroPrototype, 3, techMask: 0),
            "The prototype was available without Ηλεκτροτεχνία.");

        Assert.False(world.CanBuild(0, UnitKind.ElectroPrototype));

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.ElectroPrototype, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.GetRefBySlot(factory.Slot).QueueLength);

        // With the research done it becomes available.
        world.TeamRef(0).TechMask |= 1UL << (int)TechId.SovietElectro;

        Assert.True(world.CanBuild(0, UnitKind.ElectroPrototype));
    }

    [Fact]
    public void OnlyTheSovietsHaveIt()
    {
        Assert.Equal(Faction.Soviet, UnitCatalog.Get(UnitKind.ElectroPrototype).OnlyFor);

        Assert.False(UnitCatalog.IsUnlocked(
            Faction.Western,
            UnitKind.ElectroPrototype,
            techTier: 5,
            techMask: ulong.MaxValue));
    }

    [Fact]
    public void TheCapCountsUnitsAlreadyQueued()
    {
        (SimWorld world, EntityId factory) = SovietFactory(1UL << (int)TechId.SovietElectro);

        UnitDefinition definition = UnitCatalog.Get(UnitKind.ElectroPrototype);
        Assert.True(definition.MaxAlive > 0, "The prototype is not capped at all.");

        // Queue up to the cap: a player must not be able to queue ten in one tick
        // and only be stopped once they start appearing.
        for (int i = 0; i < definition.MaxAlive; i++)
        {
            world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.ElectroPrototype, world.Tick + 1, 0));
            world.Step();
        }

        Assert.Equal(definition.MaxAlive, world.CountOf(0, UnitKind.ElectroPrototype));
        Assert.Equal(definition.MaxAlive, world.GetRefBySlot(factory.Slot).QueueLength);

        // One more is refused.
        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.ElectroPrototype, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(definition.MaxAlive, world.CountOf(0, UnitKind.ElectroPrototype));
        Assert.Equal(definition.MaxAlive, world.GetRefBySlot(factory.Slot).QueueLength);
    }

    [Fact]
    public void LosingOneFreesACapSlot()
    {
        // The cap is on how many exist, not how many were ever built: a prototype
        // that dies can be replaced.
        (SimWorld world, EntityId factory) = SovietFactory(1UL << (int)TechId.SovietElectro);

        UnitDefinition definition = UnitCatalog.Get(UnitKind.ElectroPrototype);

        for (int i = 0; i < definition.MaxAlive; i++)
        {
            world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.ElectroPrototype, world.Tick + 1, 0));
            world.Step();
        }

        Assert.Equal(definition.MaxAlive, world.CountOf(0, UnitKind.ElectroPrototype));
        Assert.False(world.CanBuild(0, UnitKind.ElectroPrototype));

        // Clear the queue so only fielded units count, then lose one.
        world.Enqueue(SimCommand.CancelProduction(factory, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(definition.MaxAlive - 1, world.CountOf(0, UnitKind.ElectroPrototype));
        Assert.True(world.CanBuild(0, UnitKind.ElectroPrototype));
    }

    /// <summary>
    /// A queue on somebody else's factory is not this team's cap.
    /// <para>
    /// The queue half of <c>CountOf</c> used to walk every slot without asking who owned
    /// the building or whether it was even alive, so an opponent's prototype on the pad
    /// counted against this team — and in a Σοβιετικοί mirror match that locked a player
    /// out of a role because the other player had one queued.
    /// </para>
    /// </summary>
    [Fact]
    public void AnEnemysQueueDoesNotCountAgainstTheCap()
    {
        (SimWorld world, EntityId _) = SovietFactory(1UL << (int)TechId.SovietElectro);

        // A second Σοβιετικοί factory, on team 1, with a job on its pad. The job is added
        // directly rather than through a command, because a command from a team the match
        // does not have in play is refused before it ever reaches the queue — and the point
        // here is what CountOf does with a queue that exists, not how it came to exist.
        EntityId theirs = world.Spawn(Faction.Soviet, 1, UnitKind.Factory, WorldPos.FromMetres(200, 0, 200), Fix32.Zero, 5_000);
        Assert.True(world.AddJob(theirs.Slot, UnitKind.ElectroPrototype, totalTicks: 120));

        Assert.Equal(1, world.GetRefBySlot(theirs.Slot).QueueLength);
        Assert.Equal(0, world.CountOf(0, UnitKind.ElectroPrototype));
        Assert.True(world.CanBuild(0, UnitKind.ElectroPrototype));
    }

    /// <summary>
    /// A destroyed factory leaves its queue bytes in the slot, and a dead slot is not a
    /// slot that is building anything.
    /// </summary>
    [Fact]
    public void ADestroyedBuildingsQueueStopsCounting()
    {
        (SimWorld world, EntityId factory) = SovietFactory(1UL << (int)TechId.SovietElectro);

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.ElectroPrototype, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(1, world.CountOf(0, UnitKind.ElectroPrototype));

        Assert.True(world.Despawn(factory));
        Assert.Equal(0, world.CountOf(0, UnitKind.ElectroPrototype));
    }
}
