using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Deposits and volcanoes: the two surfaces that are places rather than ground.
/// A mine is worth holding and a crater is worth avoiding, and both have to survive
/// the connectivity pass — anything impassable added after it can strand a unit.
/// </summary>
public sealed class HazardTests
{
    private const ulong Seed = 20250101;

    [Fact]
    public void TheMapHasDeposits()
    {
        SimWorld world = new(Seed, capacity: 8);
        int mines = world.TerrainTypes.CountOf(TerrainType.Mine);

        Assert.True(mines > 0, "No deposits were generated at all.");

        // A deposit is a place worth fighting over, so it has to be findable: a
        // scatter of single cells would be neither.
        Assert.True(mines > 20, $"Only {mines} deposit cells — too scattered to matter.");
    }

    [Fact]
    public void DepositsAreOnGroundThatCanBeWorked()
    {
        SimWorld world = new(Seed, capacity: 8);
        TerrainLayer terrain = world.TerrainTypes;

        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (terrain.TypeAt(index) != TerrainType.Mine)
            {
                continue;
            }

            // A deposit in a lake or on a cliff is a deposit nobody can reach.
            Assert.True(terrain.IsPassable(index, MovementClass.Wheeled), "A deposit is unreachable.");
            Assert.True(world.Navigation.IsWalkable(index), "A deposit is on impassable terrain.");
        }
    }

    [Fact]
    public void AHarvesterOnADepositEarnsMaterials()
    {
        SimWorld world = new(Seed, capacity: 16);
        TerrainLayer terrain = world.TerrainTypes;

        int deposit = -1;

        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (terrain.TypeAt(index) == TerrainType.Mine)
            {
                deposit = index;
                break;
            }
        }

        Assert.True(deposit >= 0, "No deposit to test with.");

        WorldPos position = world.Navigation.CentreOf(deposit);
        world.Spawn(Faction.Soviet, 0, UnitKind.Harvester, position, Fix32.Zero, 600);
        world.Step();

        // The ground is the income; the vehicle is what collects it.
        Assert.Equal(EconomySystem.HarvesterDepositMaterials, world.Team(0).MaterialsPerTick);
    }

    [Fact]
    public void AHarvesterOffADepositEarnsNothing()
    {
        SimWorld world = new(Seed, capacity: 16);
        TerrainLayer terrain = world.TerrainTypes;

        int plain = -1;

        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (terrain.TypeAt(index) == TerrainType.Grass && world.Navigation.IsWalkable(index))
            {
                plain = index;
                break;
            }
        }

        Assert.True(plain >= 0, "No plain ground to test with.");

        world.Spawn(Faction.Soviet, 0, UnitKind.Harvester, world.Navigation.CentreOf(plain), Fix32.Zero, 600);
        world.Step();

        Assert.Equal(0, world.Team(0).MaterialsPerTick);
    }

    [Fact]
    public void StandingInLavaBurns()
    {
        SimWorld world = new(Seed, capacity: 16);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        // Put the lava under the unit rather than the unit in the lava: lava is
        // impassable, so nothing can be ordered into it, but a cell can become lava
        // under something already standing there.
        EntityId victim = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, centre, Fix32.Zero, 5_000);

        world.TerrainTypes.SetType(world.Navigation.IndexOfWorld(centre), TerrainType.Lava);
        world.RunTicks(10);

        Assert.True(world.GetRefBySlot(victim.Slot).Health < 5_000, "Lava did not burn a unit standing in it.");
    }

    [Fact]
    public void AircraftOverLavaAreFine()
    {
        SimWorld world = new(Seed, capacity: 16);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId plane = world.Spawn(Faction.Soviet, 0, UnitKind.Aircraft, centre, Fix32.Zero, 5_000);

        world.TerrainTypes.SetType(world.Navigation.IndexOfWorld(centre), TerrainType.Lava);
        world.RunTicks(10);

        // Airborne is over the lava, not in it.
        Assert.Equal(5_000, world.GetRefBySlot(plane.Slot).Health);
    }

    [Fact]
    public void LavaAndDepositsDoNotStrandAnyone()
    {
        // The connectivity pass runs after the volcanoes and before the deposits,
        // so a crater can never cut a patch of ground off. This is the regression
        // test for a pathfinding bug that made A* exhaust its budget on
        // unreachable goals.
        SimWorld world = new(Seed, capacity: 8);
        TerrainLayer terrain = world.TerrainTypes;
        PathFinder finder = new(world.Navigation.CellCount) { MaxExpansions = 100_000 };
        int[] buffer = new int[world.Navigation.CellCount];

        int start = -1;

        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (world.Navigation.IsWalkable(index) && terrain.IsPassable(index, MovementClass.Tracked))
            {
                start = index;
                break;
            }
        }

        Assert.True(start >= 0);

        PathContext context = SimWorld.PathContextFor(Faction.Soviet, UnitKind.Tank);

        for (int goal = 0; goal < terrain.CellCount; goal++)
        {
            if (goal == start || !world.Navigation.IsWalkable(goal) ||
                !terrain.IsPassable(goal, MovementClass.Tracked))
            {
                continue;
            }

            int length = finder.FindPath(world.Navigation, terrain, context, start, goal, buffer);

            Assert.True(length > 0, $"Ground at {goal} cannot be reached from {start}.");
        }
    }
}
