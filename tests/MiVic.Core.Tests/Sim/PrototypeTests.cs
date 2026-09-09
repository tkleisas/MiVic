using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The Σοβιετικοί design-bureau mechanic: a factory may only build a design the
/// bureau has proven with a prototype run. This is what stops the faction from
/// pivoting its army composition late.
/// </summary>
public sealed class PrototypeTests
{
    private static (SimWorld World, EntityId Bureau, EntityId Factory) SovietBase(int techTier = 2)
    {
        SimWorld world = new(seed: 909, capacity: 64);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId bureau = world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau, centre, Fix32.Zero, 5_000);
        EntityId factory = world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Factory,
            new WorldPos(centre.X + 30_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;
        state.TechTier = techTier;

        return (world, bureau, factory);
    }

    private static int CountOf(SimWorld world, int team, UnitKind kind)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    [Fact]
    public void SovietFactoriesCannotBuildAnUnprovenDesign()
    {
        (SimWorld world, _, EntityId factory) = SovietBase(techTier: 2);

        Assert.False(world.CanBuild(0, UnitKind.Tank));

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.GetRefBySlot(factory.Slot).QueueLength);
    }

    [Fact]
    public void ACompletedPrototypeApprovesTheDesignAndDeliversTheUnit()
    {
        (SimWorld world, EntityId bureau, EntityId factory) = SovietBase(techTier: 2);

        world.Enqueue(SimCommand.ApproveDesign(bureau, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.Team(0).IsPrototyping, "The prototype run did not start.");
        Assert.False(world.CanBuild(0, UnitKind.Tank), "An unproven design was buildable mid-run.");

        world.RunTicks(400);

        Assert.False(world.Team(0).IsPrototyping, "The prototype run never finished.");
        Assert.True(world.CanBuild(0, UnitKind.Tank), "The completed prototype did not approve the design.");
        Assert.Equal(1, CountOf(world, 0, UnitKind.Tank));

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(1, world.GetRefBySlot(factory.Slot).QueueLength);
    }

    [Fact]
    public void APrototypeNeedsTheResearchedTier()
    {
        (SimWorld world, EntityId bureau, _) = SovietBase(techTier: 1);

        world.Enqueue(SimCommand.ApproveDesign(bureau, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.False(world.Team(0).IsPrototyping);
    }

    [Fact]
    public void OnlyOnePrototypeRunsAtATime()
    {
        (SimWorld world, EntityId bureau, _) = SovietBase(techTier: 2);

        world.Enqueue(SimCommand.ApproveDesign(bureau, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        world.Enqueue(SimCommand.ApproveDesign(bureau, UnitKind.Artillery, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(UnitKind.Tank, world.Team(0).PrototypeKind);
    }

    [Fact]
    public void InfantryIsExemptBecauseItComesFromTheHeadquarters()
    {
        SimWorld world = new(seed: 12, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.Energy = 10_000;
        state.Water = 10_000;
        state.TechTier = 1;

        Assert.True(world.CanBuild(0, UnitKind.Infantry));
    }

    [Fact]
    public void OtherFactionsBuildWithoutApproval()
    {
        SimWorld world = new(seed: 13, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId factory = world.Spawn(Faction.Chinese, 1, UnitKind.Factory, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(1);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;
        state.TechTier = 2;

        Assert.True(world.CanBuild(1, UnitKind.Tank));

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 1));
        world.Step();

        Assert.Equal(1, world.GetRefBySlot(factory.Slot).QueueLength);
    }
}
