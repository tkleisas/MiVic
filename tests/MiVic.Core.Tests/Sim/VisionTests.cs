using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class VisionTests
{
    private static SimWorld World() => new(seed: 20250101, capacity: 32);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
        => world.Spawn(
            faction,
            team,
            kind,
            new WorldPos(x, 0, z),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            UnitCatalog.Get(kind).Health);

    [Fact]
    public void UnitsRevealTheGroundAroundThem()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, 0, 0);

        world.RunTicks(VisionSystem.UpdateInterval);

        int centre = world.Navigation.IndexOfWorld(WorldPos.Origin);

        Assert.True(world.Visibility.IsVisible(0, centre), "A unit did not reveal the cell it stands in.");
        Assert.True(world.Visibility.CountVisible(0) > 1, "Only the unit's own cell was revealed.");
    }

    [Fact]
    public void VisibilityIsPerTeam()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, -100_000, 0);

        world.RunTicks(VisionSystem.UpdateInterval);

        int sovietCell = world.Navigation.IndexOfWorld(new WorldPos(-100_000, 0, 0));
        int westernCell = world.Navigation.IndexOfWorld(new WorldPos(100_000, 0, 0));

        Assert.True(world.Visibility.IsVisible(0, sovietCell));
        Assert.False(world.Visibility.IsVisible(2, sovietCell), "Team 2 saw team 0's position.");
        Assert.False(world.Visibility.IsVisible(0, westernCell));
    }

    [Fact]
    public void ExploredGroundStaysExploredAfterTheUnitLeaves()
    {
        SimWorld world = World();
        EntityId scout = Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, -100_000, 0);

        world.RunTicks(VisionSystem.UpdateInterval);

        int cell = world.Navigation.IndexOfWorld(new WorldPos(-100_000, 0, 0));
        Assert.True(world.Visibility.IsExplored(0, cell));

        // Teleport far away and let visibility refresh.
        world.GetRefBySlot(scout.Slot).Position = new WorldPos(200_000, 0, 200_000);
        world.RunTicks(VisionSystem.UpdateInterval + 1);

        Assert.False(world.Visibility.IsVisible(0, cell), "Vision did not clear after the unit left.");
        Assert.True(world.Visibility.IsExplored(0, cell), "Explored ground was forgotten.");
    }

    [Fact]
    public void SightRadiusDependsOnTheRole()
    {
        Assert.True(VisionSystem.SightRadiusMm(UnitKind.Aircraft) > VisionSystem.SightRadiusMm(UnitKind.Infantry));
        Assert.True(VisionSystem.SightRadiusMm(UnitKind.Artillery) > VisionSystem.SightRadiusMm(UnitKind.Tank));
    }

    [Fact]
    public void AircraftSeeFurtherThanInfantry()
    {
        static int VisibleCells(UnitKind kind)
        {
            SimWorld world = World();
            Spawn(world, Faction.Soviet, 0, kind, 0, 0);
            world.RunTicks(VisionSystem.UpdateInterval);
            return world.Visibility.CountVisible(0);
        }

        Assert.True(VisibleCells(UnitKind.Aircraft) > VisibleCells(UnitKind.Infantry));
    }

    [Fact]
    public void NoUnitsMeansNoVisibility()
    {
        SimWorld world = World();
        world.RunTicks(VisionSystem.UpdateInterval + 1);

        Assert.Equal(0, world.Visibility.CountVisible(0));
        Assert.Equal(0, world.Visibility.CountExplored(0));
    }

    [Fact]
    public void VisionIsDeterministic()
    {
        static (int Visible, int Explored) Run()
        {
            SimWorld world = World();
            Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 20_000, 20_000);
            Spawn(world, Faction.Western, 2, UnitKind.Tank, -20_000, -20_000);
            world.RunTicks(40);

            return (world.Visibility.CountVisible(0), world.Visibility.CountExplored(0));
        }

        Assert.Equal(Run(), Run());
    }
}
