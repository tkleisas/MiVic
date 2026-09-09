using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// The surface layer: it must be a pure function of the seed, it must make water
/// impassable to ground units and irrelevant to aircraft, and it must charge a
/// heavy hull more for mud than a light one — which is the whole point of the
/// ground-pressure decision.
/// </summary>
public sealed class TerrainTests
{
    private const ulong Seed = 20250101;

    [Fact]
    public void GenerationIsDeterministic()
    {
        SimWorld a = new(Seed, capacity: 8);
        SimWorld b = new(Seed, capacity: 8);

        Assert.Equal(a.TerrainTypes.CellCount, b.TerrainTypes.CellCount);

        for (int index = 0; index < a.TerrainTypes.CellCount; index++)
        {
            Assert.Equal(a.TerrainTypes.TypeAt(index), b.TerrainTypes.TypeAt(index));
        }
    }

    [Fact]
    public void TheMapHasBothWaterAndDryGround()
    {
        SimWorld world = new(Seed, capacity: 8);
        TerrainLayer terrain = world.TerrainTypes;

        int water = terrain.CountOf(TerrainType.ShallowWater) + terrain.CountOf(TerrainType.DeepWater);

        Assert.True(water > 0, "No water was generated at all.");
        Assert.True(terrain.CountOf(TerrainType.Grass) > 0, "No open ground was generated at all.");
        Assert.True(water < terrain.CellCount, "The whole map is water.");
    }

    [Fact]
    public void DeepWaterIsImpassableToGroundAndIrrelevantToAircraft()
    {
        TerrainLayer terrain = new SimWorld(Seed, capacity: 8).TerrainTypes;

        Assert.Equal(0, TerrainLayer.BaseCostPermille(MovementClass.Tracked, TerrainType.DeepWater));
        Assert.Equal(0, TerrainLayer.BaseCostPermille(MovementClass.Foot, TerrainType.DeepWater));
        Assert.Equal(TerrainLayer.BasePermille, TerrainLayer.BaseCostPermille(MovementClass.Air, TerrainType.DeepWater));

        // Shallow water is a ford: crossable, but never cheap.
        int ford = TerrainLayer.BaseCostPermille(MovementClass.Tracked, TerrainType.ShallowWater);

        Assert.True(ford > 0, "Shallow water is not crossable, so fords do not work.");
        Assert.True(ford > TerrainLayer.BaseCostPermille(MovementClass.Tracked, TerrainType.Mud),
            "A ford is cheaper than mud, which cannot be right.");
    }

    [Fact]
    public void EveryUnitOfGroundIsReachableFromEveryOther()
    {
        // Deep water is impassable, so the generator has to guarantee a crossing.
        // Without the ford pass, a flooded map strands units on islands and they
        // simply never move.
        SimWorld world = new(Seed, capacity: 64);
        NavGrid grid = world.Navigation;
        TerrainLayer terrain = world.TerrainTypes;

        bool[] seen = new bool[grid.CellCount];
        int[] queue = new int[grid.CellCount];
        int head = 0;
        int tail = 0;

        for (int seed = 0; seed < grid.CellCount; seed++)
        {
            if (grid.IsWalkable(seed) && terrain.IsPassable(seed, MovementClass.Tracked))
            {
                seen[seed] = true;
                queue[tail++] = seed;
                break;
            }
        }

        Assert.True(tail > 0, "The map has no ground at all.");

        while (head < tail)
        {
            int cell = queue[head++];
            int cx = grid.CellX(cell);
            int cz = grid.CellZ(cell);

            for (int dz = -1; dz <= 1; dz++)
            {
                for (int dx = -1; dx <= 1; dx++)
                {
                    int next = grid.IndexOf(cx + dx, cz + dz);

                    if (next >= 0 && !seen[next] && grid.IsWalkable(next) &&
                        terrain.IsPassable(next, MovementClass.Tracked))
                    {
                        seen[next] = true;
                        queue[tail++] = next;
                    }
                }
            }
        }

        for (int index = 0; index < grid.CellCount; index++)
        {
            if (grid.IsWalkable(index) && terrain.IsPassable(index, MovementClass.Tracked))
            {
                Assert.True(seen[index], $"Ground at cell {index} is cut off from the rest of the map.");
            }
        }
    }

    [Fact]
    public void GroundPressureDecidesHowBadMudIs()
    {
        // The historical asymmetry: a light hull on wide tracks barely notices the
        // mud; a heavy one bogs down in it.
        int soviet = UnitCatalog.GroundPressure(Faction.Soviet, UnitKind.Tank);
        int western = UnitCatalog.GroundPressure(Faction.Western, UnitKind.Tank);
        int chinese = UnitCatalog.GroundPressure(Faction.Chinese, UnitKind.Tank);

        Assert.True(soviet < western, $"Σοβιετικοί {soviet} is not lighter than Δυτικοί {western}.");
        Assert.True(western < chinese, $"Δυτικοί {western} is not lighter than Κινέζοι {chinese}.");
    }

    [Fact]
    public void MudCostsMoreTheHeavierTheMover()
    {
        TerrainLayer terrain = new SimWorld(Seed, capacity: 8).TerrainTypes;

        int index = FirstCellOfType(terrain, TerrainType.Mud);
        Assert.True(index >= 0, "No mud was generated for this seed.");

        int light = terrain.CostPermille(index, MovementClass.Tracked, UnitCatalog.GroundPressure(Faction.Soviet, UnitKind.Tank));
        int heavy = terrain.CostPermille(index, MovementClass.Tracked, UnitCatalog.GroundPressure(Faction.Chinese, UnitKind.Tank));

        Assert.True(light < heavy, $"Mud cost did not rise with ground pressure ({light} vs {heavy}).");
    }

    [Fact]
    public void AircraftIgnoreTerrainCost()
    {
        TerrainLayer terrain = new SimWorld(Seed, capacity: 8).TerrainTypes;

        for (int index = 0; index < terrain.CellCount; index++)
        {
            Assert.Equal(
                TerrainLayer.BasePermille,
                terrain.CostPermille(index, MovementClass.Air, 0));
        }
    }

    [Fact]
    public void APathNeverCrossesWater()
    {
        SimWorld world = new(Seed, capacity: 16);
        TerrainLayer terrain = world.TerrainTypes;
        NavGrid grid = world.Navigation;
        PathContext context = SimWorld.PathContextFor(Faction.Soviet, UnitKind.Tank);

        // The detour around a lake can be far longer than the simulation's per-tick
        // path buffer, so this search gets a bigger budget and a bigger buffer. A
        // route that does not fit the buffer is reported as length zero.
        PathFinder finder = new(grid.CellCount) { MaxExpansions = 100_000 };
        int[] buffer = new int[grid.CellCount];
        int length = 0;

        // Look for a lake with dry ground on both sides on one row: a straight line
        // between those two cells would cross the water, so any route found has to
        // go around. Endpoints that turn out to be islands are skipped.
        for (int z = 0; z < grid.Size && length == 0; z++)
        {
            for (int x = 2; x < grid.Size - 2 && length == 0; x++)
            {
                int middle = grid.IndexOf(x, z);

                if (terrain.TypeAt(middle) is not (TerrainType.ShallowWater or TerrainType.DeepWater))
                {
                    continue;
                }

                int left = grid.IndexOf(x - 2, z);
                int right = grid.IndexOf(x + 2, z);

                if (!grid.IsWalkable(left) || !grid.IsWalkable(right) ||
                    !terrain.IsPassable(left, context.Movement) ||
                    !terrain.IsPassable(right, context.Movement))
                {
                    continue;
                }

                length = finder.FindPath(grid, terrain, context, left, right, buffer);
            }
        }

        Assert.True(length > 0, "No lake with a route around it was found for this seed.");

        // Shallow water is a ford and may be crossed; deep water never may be.
        for (int i = 0; i < length; i++)
        {
            Assert.NotEqual(TerrainType.DeepWater, terrain.TypeAt(buffer[i]));
        }
    }

    private static int FirstCellOfType(TerrainLayer terrain, TerrainType type)
    {
        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (terrain.TypeAt(index) == type)
            {
                return index;
            }
        }

        return -1;
    }
}
