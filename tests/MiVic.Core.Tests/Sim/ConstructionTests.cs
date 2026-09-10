using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Structures are raised rather than spawned. This is the simulation half of the
/// construction animation: the client can show a building rising out of the ground
/// only because the simulation says it is not finished yet — and a half-built
/// building must not be doing anything.
/// </summary>
public sealed class ConstructionTests
{
    private static (SimWorld World, EntityId HQ) Base()
    {
        SimWorld world = new(seed: 5150, capacity: 64);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId hq = world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;

        return (world, hq);
    }

    private static int Find(SimWorld world, UnitKind kind)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == kind)
            {
                return slot;
            }
        }

        return -1;
    }

    [Fact]
    public void AStructureArrivesUnfinished()
    {
        (SimWorld world, EntityId hq) = Base();

        world.Enqueue(SimCommand.QueueUnit(hq, UnitKind.Factory, world.Tick + 1, 0));
        world.RunTicks(UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Factory) + 4);

        int factory = Find(world, UnitKind.Factory);

        Assert.True(factory >= 0, "The factory never appeared.");
        Assert.True(
            world.GetRefBySlot(factory).ConstructionTicksRemaining > 0,
            "The factory arrived fully built.");

        // And it finishes on its own.
        world.RunTicks(world.GetRefBySlot(factory).ConstructionTicksRemaining + 2);

        Assert.Equal(0, world.GetRefBySlot(factory).ConstructionTicksRemaining);
    }

    [Fact]
    public void AnUnfinishedStructureProducesNothing()
    {
        (SimWorld world, EntityId hq) = Base();

        world.Enqueue(SimCommand.QueueUnit(hq, UnitKind.Factory, world.Tick + 1, 0));
        world.RunTicks(UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Factory) + 4);

        int factory = Find(world, UnitKind.Factory);
        var id = new EntityId(factory, world.GetRefBySlot(factory).Generation);

        // Queue work at a building site and give it time to run.
        world.Enqueue(SimCommand.QueueUnit(id, UnitKind.Tank, world.Tick + 1, 0));
        world.RunTicks(20);

        Assert.True(world.GetRefBySlot(factory).ConstructionTicksRemaining > 0, "It finished too soon to test.");
        Assert.Equal(0, world.GetRefBySlot(factory).QueueLength);
    }

    [Fact]
    public void ScenarioStructuresStartComplete()
    {
        // A mission's opening layout is a standing base, not a building site.
        // Anything else would make every scenario start with a dead HQ.
        SimWorld world = new(20250101, capacity: 512);
        Scenario.Build(world, ScenarioKind.Skirmish);

        int seen = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (!UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            seen++;
            Assert.Equal(0, entity.ConstructionTicksRemaining);
        }

        Assert.True(seen > 0, "The skirmish laid out no structures at all.");
    }

    [Fact]
    public void AnUnfinishedStructureIsNotCountedAsPresent()
    {
        (SimWorld world, EntityId hq) = Base();

        world.Enqueue(SimCommand.QueueUnit(hq, UnitKind.Factory, world.Tick + 1, 0));
        world.RunTicks(UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Factory) + 4);

        int factory = Find(world, UnitKind.Factory);

        // Abilities ask whether a structure is standing, and a building site is not
        // one. The Σοβιετικοί orbital strike needs a finished design bureau.
        Assert.False(world.HasStructure(0, UnitKind.Factory));

        world.RunTicks(world.GetRefBySlot(factory).ConstructionTicksRemaining + 2);

        Assert.True(world.HasStructure(0, UnitKind.Factory));
    }
}
