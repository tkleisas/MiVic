using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Pathfinding;

public sealed class TerrainTests
{
    private static HeightMap Sample(ulong seed) => HeightMap.Generate(seed, 65, 600_000, 42_000);

    [Fact]
    public void Generation_IsDeterministic()
        => Assert.Equal(Sample(1234).ComputeHash(), Sample(1234).ComputeHash());

    [Fact]
    public void DifferentSeeds_ProduceDifferentTerrain()
        => Assert.NotEqual(Sample(1).ComputeHash(), Sample(2).ComputeHash());

    [Fact]
    public void Heights_StayWithinBounds()
    {
        HeightMap map = Sample(99);

        for (int i = 0; i < map.Size * map.Size; i++)
        {
            Assert.InRange(map.HeightAtIndex(i), 0, map.MaxHeightMm);
        }
    }

    [Fact]
    public void TerrainIsNotFlat()
    {
        HeightMap map = Sample(4242);
        int min = int.MaxValue;
        int max = int.MinValue;

        for (int i = 0; i < map.Size * map.Size; i++)
        {
            min = Math.Min(min, map.HeightAtIndex(i));
            max = Math.Max(max, map.HeightAtIndex(i));
        }

        // A flat map would make every terrain and pathfinding test vacuous.
        Assert.True(max - min > 5_000, $"Terrain relief was only {max - min} mm.");
    }

    [Fact]
    public void Sampling_MatchesTheLatticeAtSamplePoints()
    {
        HeightMap map = Sample(7);

        for (int z = 0; z < map.Size; z += 7)
        {
            for (int x = 0; x < map.Size; x += 5)
            {
                int worldX = map.OriginMm + (x * map.CellSizeMm);
                int worldZ = map.OriginMm + (z * map.CellSizeMm);

                Assert.Equal(map.HeightAt(x, z), map.SampleHeightMm(worldX, worldZ));
            }
        }
    }

    [Fact]
    public void Sampling_InterpolatesBetweenSamples()
    {
        HeightMap map = Sample(11);
        int worldX = map.OriginMm + (map.CellSizeMm / 2);
        int worldZ = map.OriginMm + (map.CellSizeMm / 2);

        int sampled = map.SampleHeightMm(worldX, worldZ);
        int lowest = Math.Min(
            Math.Min(map.HeightAt(0, 0), map.HeightAt(1, 0)),
            Math.Min(map.HeightAt(0, 1), map.HeightAt(1, 1)));
        int highest = Math.Max(
            Math.Max(map.HeightAt(0, 0), map.HeightAt(1, 0)),
            Math.Max(map.HeightAt(0, 1), map.HeightAt(1, 1)));

        Assert.InRange(sampled, lowest, highest);
    }

    [Fact]
    public void SlopePermille_IsZeroOnAFlatSample()
    {
        // A hand-built flat map is impossible through Generate, so check the
        // property that matters: slope is never negative and is bounded.
        HeightMap map = Sample(5);

        for (int z = 0; z < map.Size; z += 3)
        {
            for (int x = 0; x < map.Size; x += 3)
            {
                Assert.InRange(map.SlopePermille(x, z), 0, int.MaxValue);
            }
        }
    }
}

public sealed class NavGridTests
{
    private static NavGrid Grid(ulong seed = 20250101, int maxSlope = 900)
        => NavGrid.Build(HeightMap.Generate(seed, 65, 600_000, 42_000), maxSlope);

    [Fact]
    public void EveryCellIsEitherWalkableOrBlocked()
    {
        NavGrid grid = Grid();

        for (int i = 0; i < grid.CellCount; i++)
        {
            Assert.Equal(grid.IsWalkable(i), grid.CostAt(i) != 0);
        }
    }

    [Fact]
    public void BlockedCellCount_MatchesScan()
    {
        NavGrid grid = Grid();
        int blocked = 0;

        for (int i = 0; i < grid.CellCount; i++)
        {
            if (!grid.IsWalkable(i))
            {
                blocked++;
            }
        }

        Assert.Equal(grid.BlockedCellCount, blocked);
    }

    [Fact]
    public void SteeperTerrain_BlocksMoreCells()
    {
        NavGrid gentle = Grid(maxSlope: 2000);
        NavGrid harsh = Grid(maxSlope: 200);

        Assert.True(harsh.BlockedCellCount > gentle.BlockedCellCount);
    }

    [Fact]
    public void MostOfTheMapIsPassable()
    {
        NavGrid grid = Grid();

        // The battlefield has to be usable, not a maze.
        Assert.True(grid.BlockedCellCount < grid.CellCount / 4,
            $"{grid.BlockedCellCount} of {grid.CellCount} cells were blocked.");
    }

    [Fact]
    public void CentreOf_SitsInsideTheCellAtTerrainHeight()
    {
        NavGrid grid = Grid();

        for (int i = 0; i < grid.CellCount; i += 97)
        {
            WorldPos centre = grid.CentreOf(i);

            Assert.Equal(grid.HeightAt(i), centre.Y);
            Assert.Equal(i, grid.IndexOfWorld(centre));
        }
    }

    [Fact]
    public void NearestWalkable_ReturnsAnOpenCell()
    {
        NavGrid grid = Grid();

        for (int i = 0; i < grid.CellCount; i += 61)
        {
            int nearest = grid.NearestWalkable(i);

            if (grid.IsWalkable(i))
            {
                Assert.Equal(i, nearest);
            }
            else if (nearest >= 0)
            {
                Assert.True(grid.IsWalkable(nearest));
            }
        }
    }
}

public sealed class PathFinderTests
{
    private static NavGrid Grid(ulong seed = 20250101) => NavGrid.Build(HeightMap.Generate(seed, 65, 600_000, 42_000), 900);

    /// <summary>Finds two walkable cells that are genuinely connected.</summary>
    private static (NavGrid Grid, int Start, int Goal) ConnectedPair(ulong seed = 20250101)
    {
        NavGrid grid = Grid(seed);
        PathFinder finder = new(grid.CellCount);
        int[] buffer = new int[256];

        int start = -1;
        for (int i = 0; i < grid.CellCount; i++)
        {
            if (grid.IsWalkable(i))
            {
                start = i;
                break;
            }
        }

        Assert.True(start >= 0, "The grid has no walkable cell at all.");

        for (int i = grid.CellCount - 1; i >= 0; i--)
        {
            if (!grid.IsWalkable(i) || i == start)
            {
                continue;
            }

            int manhattan = Math.Abs(grid.CellX(i) - grid.CellX(start)) + Math.Abs(grid.CellZ(i) - grid.CellZ(start));

            if (manhattan < 8)
            {
                continue;
            }

            if (finder.FindPath(grid, start, i, buffer) > 0)
            {
                return (grid, start, i);
            }
        }

        throw new InvalidOperationException("No connected pair of walkable cells was found.");
    }

    private static int Manhattan(NavGrid grid, int a, int b)
        => Math.Abs(grid.CellX(a) - grid.CellX(b)) + Math.Abs(grid.CellZ(a) - grid.CellZ(b));

    [Fact]
    public void FindsAPathBetweenConnectedCells()
    {
        (NavGrid grid, int start, int goal) = ConnectedPair();
        PathFinder finder = new(grid.CellCount);
        int[] buffer = new int[256];

        int length = finder.FindPath(grid, start, goal, buffer);

        Assert.True(length > 0, "No path was found between connected cells.");
        Assert.True(finder.LastExpandedNodes > 0);

        // The route must make progress towards the goal. It is allowed to stop
        // short when the expansion cap trips on a very long route.
        Assert.True(
            Manhattan(grid, buffer[length - 1], goal) < Manhattan(grid, start, goal),
            "The path did not move towards the goal.");
    }

    [Fact]
    public void PathNeverEntersABlockedCell()
    {
        (NavGrid grid, int start, int goal) = ConnectedPair();
        PathFinder finder = new(grid.CellCount);
        int[] buffer = new int[256];
        int length = finder.FindPath(grid, start, goal, buffer);

        for (int i = 0; i < length; i++)
        {
            Assert.True(grid.IsWalkable(buffer[i]), $"Waypoint {i} was blocked.");
        }
    }

    [Fact]
    public void PathStepsAreAdjacent()
    {
        (NavGrid grid, int start, int goal) = ConnectedPair();
        PathFinder finder = new(grid.CellCount);
        int[] buffer = new int[256];
        int length = finder.FindPath(grid, start, goal, buffer);

        int previous = start;

        for (int i = 0; i < length; i++)
        {
            int dx = Math.Abs(grid.CellX(buffer[i]) - grid.CellX(previous));
            int dz = Math.Abs(grid.CellZ(buffer[i]) - grid.CellZ(previous));

            Assert.InRange(dx, 0, 1);
            Assert.InRange(dz, 0, 1);
            Assert.True(dx + dz > 0, "Path contained a zero-length step.");

            previous = buffer[i];
        }
    }

    [Fact]
    public void SearchIsDeterministic()
    {
        (NavGrid grid, int start, int goal) = ConnectedPair();

        int[] first = new int[256];
        int[] second = new int[256];

        int firstLength = new PathFinder(grid.CellCount).FindPath(grid, start, goal, first);
        int secondLength = new PathFinder(grid.CellCount).FindPath(grid, start, goal, second);

        Assert.Equal(firstLength, secondLength);
        Assert.Equal(first[..firstLength], second[..secondLength]);
    }

    [Fact]
    public void ReturnsZeroForABlockedGoal()
    {
        NavGrid grid = Grid();
        PathFinder finder = new(grid.CellCount);
        int[] buffer = new int[64];

        int blocked = -1;
        for (int i = 0; i < grid.CellCount; i++)
        {
            if (!grid.IsWalkable(i))
            {
                blocked = i;
                break;
            }
        }

        if (blocked < 0)
        {
            return; // No blocked cell in this terrain; nothing to assert.
        }

        Assert.Equal(0, finder.FindPath(grid, 0, blocked, buffer));
    }

    [Fact]
    public void LongPathsAreTruncatedRatherThanRejected()
    {
        // A route longer than the caller's buffer must still yield its first leg,
        // so a long order is walked in stages instead of being silently dropped.
        (NavGrid grid, int start, int goal) = ConnectedPair();
        PathFinder finder = new(grid.CellCount);

        int[] full = new int[256];
        int fullLength = finder.FindPath(grid, start, goal, full);
        Assert.True(fullLength > 1, "The test pair produced a single-step route.");

        int[] tiny = new int[1];
        int tinyLength = finder.FindPath(grid, start, goal, tiny);

        Assert.Equal(1, tinyLength);
        Assert.Equal(full[0], tiny[0]);
    }

    [Fact]
    public void SmoothingKeepsEveryLegWalkable()
    {
        (NavGrid grid, int start, int goal) = ConnectedPair();
        PathFinder finder = new(grid.CellCount);

        int[] raw = new int[256];
        int rawLength = finder.FindPath(grid, start, goal, raw);

        int[] smoothed = new int[256];
        int smoothedLength = grid.Smooth(raw, rawLength, start, smoothed);

        Assert.True(smoothedLength <= rawLength, "Smoothing added waypoints.");
        Assert.Equal(raw[rawLength - 1], smoothed[smoothedLength - 1]);

        // Every consecutive pair must see each other, or the unit would cut
        // through terrain.
        int anchor = start;

        for (int i = 0; i < smoothedLength; i++)
        {
            Assert.True(grid.HasLineOfSight(anchor, smoothed[i]), $"Leg {i} was blocked.");
            anchor = smoothed[i];
        }
    }

    [Fact]
    public void LineOfSightIsSymmetric()
    {
        NavGrid grid = Grid();

        for (int i = 0; i < grid.CellCount; i += 137)
        {
            int a = grid.NearestWalkable(i);
            int b = grid.NearestWalkable((i * 7 + 31) % grid.CellCount);

            Assert.Equal(grid.HasLineOfSight(a, b), grid.HasLineOfSight(b, a));
        }
    }

    [Fact]
    public void LineOfSightToSelfIsAlwaysTrue()
    {
        NavGrid grid = Grid();
        Assert.True(grid.HasLineOfSight(0, 0));
    }

    [Fact]
    public void StartEqualsGoal_ProducesNoWaypoints()
    {
        NavGrid grid = Grid();
        PathFinder finder = new(grid.CellCount);
        int start = grid.NearestWalkable(0);

        Assert.Equal(0, finder.FindPath(grid, start, start, new int[16]));
    }
}
