using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Bridges and snow sight: the last two pieces of the terrain design. A bridge is a
/// ford a player chose the site of; snow blinds as well as slows.
/// </summary>
public sealed class BridgeTests
{
    private const ulong Seed = 20250101;

    /// <summary>A world with a finished factory, so engineering work is allowed.</summary>
    private static SimWorld WithFactory(out int waterCell)
    {
        SimWorld world = new(Seed, capacity: 32);
        TerrainLayer terrain = world.TerrainTypes;
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.Energy = 10_000;
        state.Water = 10_000;

        waterCell = -1;

        // A deep-water cell with dry land on both sides on one axis: exactly the
        // site a player would want to bridge.
        for (int index = 0; index < terrain.CellCount && waterCell < 0; index++)
        {
            if (terrain.TypeAt(index) != TerrainType.DeepWater)
            {
                continue;
            }

            int cx = world.Navigation.CellX(index);
            int cz = world.Navigation.CellZ(index);

            int left = world.Navigation.IndexOf(cx - 3, cz);
            int right = world.Navigation.IndexOf(cx + 3, cz);

            if (left >= 0 && right >= 0 &&
                terrain.IsPassable(left, MovementClass.Tracked) &&
                terrain.IsPassable(right, MovementClass.Tracked))
            {
                waterCell = index;
            }
        }

        return world;
    }

    [Fact]
    public void ABridgeMakesWaterCrossable()
    {
        SimWorld world = WithFactory(out int waterCell);

        Assert.True(waterCell >= 0, "No suitable water to bridge for this seed.");
        Assert.False(world.TerrainTypes.IsPassable(waterCell, MovementClass.Tracked));

        WorldPos target = world.Navigation.CentreOf(waterCell);

        Assert.True(world.CanBuildBridge(0, target, out _));

        world.Enqueue(SimCommand.Bridge(target, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.TerrainTypes.IsPassable(waterCell, MovementClass.Tracked), "The bridge went nowhere.");
    }

    [Fact]
    public void ABridgeCostsResources()
    {
        SimWorld world = WithFactory(out int waterCell);
        WorldPos target = world.Navigation.CentreOf(waterCell);

        int before = world.Team(0).Materials;

        world.Enqueue(SimCommand.Bridge(target, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(before - SimWorld.BridgeMaterials, world.Team(0).Materials);
    }

    [Fact]
    public void ABridgeNeedsWaterAndAFactory()
    {
        SimWorld world = WithFactory(out int waterCell);

        // Dry ground is not a crossing.
        int dry = world.Navigation.IndexOfWorld(WorldPos.Origin);
        WorldPos plain = world.Navigation.CentreOf(world.Navigation.NearestWalkable(dry));

        Assert.False(world.CanBuildBridge(0, plain, out string reason));
        Assert.Contains("νερό", reason);

        // And a team with no industry cannot span a river.
        WorldPos water = world.Navigation.CentreOf(waterCell);
        world.TeamRef(0).Materials = 0;

        Assert.False(world.CanBuildBridge(0, water, out _));

        world.TeamRef(0).Materials = 10_000;
        Assert.False(world.CanBuildBridge(1, water, out string noFactory));
        Assert.Contains("εργοστάσιο", noFactory);
    }

    [Fact]
    public void ARefusedBridgeIsNotChargedFor()
    {
        // A misplaced click must cost nothing but the click.
        SimWorld world = WithFactory(out _);

        int before = world.Team(0).Materials;
        int dry = world.Navigation.IndexOfWorld(WorldPos.Origin);

        world.Enqueue(SimCommand.Bridge(
            world.Navigation.CentreOf(world.Navigation.NearestWalkable(dry)),
            world.Tick + 1,
            0));
        world.Step();

        Assert.Equal(before, world.Team(0).Materials);
    }

    [Fact]
    public void SnowShortensSight()
    {
        SimWorld world = new(Seed, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId scout = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, centre, Fix32.Zero, 5_000);

        world.RunTicks(VisionSystem.UpdateInterval + 1);
        int clearCells = VisibleCells(world, centre);

        // Snow under the same unit: the same position, the same team, less seen.
        world.TerrainTypes.SetType(world.Navigation.IndexOfWorld(centre), TerrainType.Snow);
        world.RunTicks(VisionSystem.UpdateInterval + 1);

        int snowCells = VisibleCells(world, centre);

        Assert.True(snowCells > 0, "Snow blinded the unit completely.");
        Assert.True(snowCells < clearCells, $"Snow did not shorten sight ({clearCells} -> {snowCells}).");
        Assert.True(world.IsAliveSlot(scout.Slot));
    }

    private static int VisibleCells(SimWorld world, WorldPos centre)
    {
        // Scan the whole grid: the point is to compare two runs, not to be clever
        // about the window.
        int count = 0;

        for (int index = 0; index < world.Navigation.CellCount; index++)
        {
            if (world.Visibility.IsVisible(0, index))
            {
                count++;
            }
        }

        return count;
    }
}
