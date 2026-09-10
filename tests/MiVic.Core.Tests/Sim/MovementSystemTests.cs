using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class MovementSystemTests
{
    /// <summary>
    /// Builds a world with one unit ordered to a destination that is genuinely
    /// reachable, so the tests do not depend on where the generated terrain
    /// happens to be walkable.
    /// </summary>
    private static (SimWorld World, EntityId Unit, WorldPos Goal) OrderedUnit(
        int speedMmPerTick,
        UnitKind kind = UnitKind.Tank,
        ulong seed = 20250101)
    {
        SimWorld world = new(seed, capacity: 16);
        NavGrid grid = world.Navigation;
        PathFinder finder = new(grid.CellCount);
        int[] buffer = new int[SimConstants.MaxPathCells];
        PathContext context = SimWorld.PathContextFor(Faction.Soviet, kind);

        int start = grid.NearestWalkable(grid.IndexOfWorld(WorldPos.Origin), world.TerrainTypes, context);
        int goal = -1;

        for (int i = 0; i < grid.CellCount; i++)
        {
            if (!grid.IsWalkable(i) || i == start || !world.TerrainTypes.IsPassable(i, context.Movement))
            {
                continue;
            }

            int manhattan = Math.Abs(grid.CellX(i) - grid.CellX(start)) + Math.Abs(grid.CellZ(i) - grid.CellZ(start));

            if (manhattan is < 12 or > 40)
            {
                continue;
            }

            if (finder.FindPath(grid, world.TerrainTypes, context, start, i, buffer) > 0)
            {
                goal = i;
                break;
            }
        }

        Assert.True(goal >= 0, "No reachable destination was found for the test.");

        WorldPos startPosition = grid.CentreOf(start);
        EntityId unit = world.Spawn(Faction.Soviet, 0, kind, startPosition, Fix32.FromInt(speedMmPerTick), 100);
        WorldPos destination = grid.CentreOf(goal);

        world.OrderMove(unit, destination, 0);

        return (world, unit, destination);
    }

    [Fact]
    public void UnitFollowsItsPathAndArrives()
    {
        (SimWorld world, EntityId unit, WorldPos goal) = OrderedUnit(speedMmPerTick: 400);

        world.RunTicks(3_000);

        Assert.True(world.TryGet(unit, out Entity e));
        Assert.True(
            e.Position.HorizontalDistanceTo(goal) <= SimConstants.ArrivalRadiusMm,
            $"Unit stopped {e.Position.HorizontalDistanceTo(goal)} mm from the goal.");
        Assert.False(e.HasMoveGoal);
    }

    [Fact]
    public void UnitMakesProgressEachTick()
    {
        (SimWorld world, EntityId unit, _) = OrderedUnit(speedMmPerTick: 400);

        Assert.True(world.TryGet(unit, out Entity before));
        int startX = before.Position.X;
        int startZ = before.Position.Z;

        world.RunTicks(5);

        Assert.True(world.TryGet(unit, out Entity after));
        Assert.NotEqual((startX, startZ), (after.Position.X, after.Position.Z));
    }

    [Fact]
    public void GroundUnitStaysOnTheTerrainSurface()
    {
        (SimWorld world, EntityId unit, _) = OrderedUnit(speedMmPerTick: 400);

        for (int tick = 0; tick < 200; tick++)
        {
            world.Step();

            Assert.True(world.TryGet(unit, out Entity e));
            Assert.Equal(world.Terrain.SampleHeightMm(e.Position.X, e.Position.Z), e.Position.Y);
        }
    }

    [Fact]
    public void AircraftKeepsItsAltitudeAboveTheTerrain()
    {
        (SimWorld world, EntityId unit, _) = OrderedUnit(speedMmPerTick: 1_500, kind: UnitKind.Aircraft);

        world.RunTicks(50);

        Assert.True(world.TryGet(unit, out Entity e));
        Assert.Equal(60_000, e.AltitudeMm);
        Assert.Equal(world.Terrain.SampleHeightMm(e.Position.X, e.Position.Z) + 60_000, e.Position.Y);
    }

    [Fact]
    public void UnitFacesItsDirectionOfTravel()
    {
        (SimWorld world, EntityId unit, _) = OrderedUnit(speedMmPerTick: 400);

        world.RunTicks(20);

        Assert.True(world.TryGet(unit, out Entity e));
        Assert.True(e.HasMoveGoal || e.Heading != 0, "Unit never recorded a heading.");
    }

    [Fact]
    public void ZeroSpeedUnitsDoNotMove()
    {
        SimWorld world = new(seed: 3, capacity: 4);
        WorldPos start = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        EntityId unit = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, start, Fix32.Zero, 100);

        world.OrderMove(unit, new WorldPos(start.X + 50_000, 0, start.Z), 0);
        world.RunTicks(50);

        Assert.True(world.TryGet(unit, out Entity e));
        Assert.Equal((start.X, start.Z), (e.Position.X, e.Position.Z));
    }

    [Fact]
    public void StopCommandClearsTheRoute()
    {
        (SimWorld world, EntityId unit, _) = OrderedUnit(speedMmPerTick: 400);

        world.RunTicks(10);
        world.Enqueue(SimCommand.Stop(unit, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.TryGet(unit, out Entity e));
        Assert.False(e.HasMoveGoal);
        Assert.Equal(0, e.PathLength);
        Assert.Equal(0, e.PathCursor);
    }

    [Fact]
    public void DistanceTravelledAccumulates()
    {
        // The odometer is what turns the wheels, and it lives in the simulation
        // rather than in the client so that a replay spins them identically. A
        // client-side odometer would depend on frame rate instead.
        (SimWorld world, EntityId unit, _) = OrderedUnit(speedMmPerTick: 400);

        long before = world.GetRefBySlot(unit.Slot).DistanceTravelledMm;
        Assert.Equal(0, before);

        world.RunTicks(40);

        long after = world.GetRefBySlot(unit.Slot).DistanceTravelledMm;

        Assert.True(after > 0, "A moving unit covered no ground.");
        Assert.True(after <= 40 * 400L, $"Odometer outran the unit's speed ({after} mm in 40 ticks).");
    }

    [Fact]
    public void UnitWithoutAnOrderKeepsItsPathEmpty()    {
        SimWorld world = new(seed: 5, capacity: 4);
        PathContext context = SimWorld.PathContextFor(Faction.Soviet, UnitKind.Tank);
        WorldPos start = world.Navigation.CentreOf(
            world.Navigation.NearestWalkable(0, world.TerrainTypes, context));
        EntityId unit = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, start, Fix32.FromInt(400), 100);

        world.RunTicks(20);

        Assert.True(world.TryGet(unit, out Entity e));
        Assert.Equal(0, e.PathLength);
        Assert.Equal(0, e.PathCursor);
        Assert.Equal((start.X, start.Z), (e.Position.X, e.Position.Z));
    }

    [Theory]
    [InlineData(1, 0, 0)]        // +X
    [InlineData(0, 1, 16384)]    // +Z
    [InlineData(-1, 0, 32768)]   // -X
    [InlineData(0, -1, 49152)]   // -Z
    public void Heading_UsesCardinalConventions(int dx, int dz, int expected)
        => Assert.Equal(expected, MovementSystem.HeadingFromDirection(dx, dz));

    [Fact]
    public void Heading_OppositeDirectionsDifferByHalfATurn()
    {
        int[] directions = [-1000, -37, 0, 1, 42, 999];

        foreach (int dx in directions)
        {
            foreach (int dz in directions)
            {
                if (dx == 0 && dz == 0)
                {
                    continue;
                }

                int forward = MovementSystem.HeadingFromDirection(dx, dz);
                int backward = MovementSystem.HeadingFromDirection(-dx, -dz);
                int delta = (backward - forward + 65536) & 0xFFFF;

                Assert.Equal(32768, delta);
            }
        }
    }

    [Fact]
    public void Heading_ZeroVectorIsZero()
        => Assert.Equal(0, MovementSystem.HeadingFromDirection(0, 0));

    [Fact]
    public void Heading_ApproximatesTrueAtan2WithinTwoDegrees()
    {
        int[][] samples =
        [
            [1, 1], [1, 2], [3, 1], [10, 1], [1, 10], [-7, 3], [5, -9], [-4, -6], [100, 3], [2, 100],
        ];

        foreach (int[] s in samples)
        {
            int dx = s[0];
            int dz = s[1];

            double expected = Math.Atan2(dz, dx);
            if (expected < 0)
            {
                expected += 2 * Math.PI;
            }

            int expectedBrads = (int)Math.Round(expected * 65536.0 / (2 * Math.PI));
            int actual = MovementSystem.HeadingFromDirection(dx, dz);

            int delta = Math.Abs(((actual - expectedBrads + 32768) & 0xFFFF) - 32768);

            // 2 degrees is 364 brads.
            Assert.True(delta <= 364, $"dir ({dx},{dz}): got {actual}, expected {expectedBrads}, delta {delta}");
        }
    }
}
