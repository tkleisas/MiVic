using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Mutable terrain and the ability built on it. Weather control is the payoff of
/// the whole ground-pressure system: the Σοβιετικοί call the rasputitsa down on
/// ground the enemy has to cross, and it dries out again afterwards.
/// </summary>
public sealed class WeatherTests
{
    private static SimWorld SovietWeatherBase(out WorldPos centre)
    {
        SimWorld world = new(seed: 2024, capacity: 32);
        centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.TechTier = 4;
        state.TechMask |= 1UL << (int)TechId.SovietAdvance4;

        return world;
    }

    /// <summary>Centre of the first cell of a given surface, so a test never starts on mud.</summary>
    private static WorldPos CellOfType(SimWorld world, TerrainType type)
    {
        for (int index = 0; index < world.TerrainTypes.CellCount; index++)
        {
            if (world.TerrainTypes.TypeAt(index) == type)
            {
                return world.Navigation.CentreOf(index);
            }
        }

        throw new InvalidOperationException($"No {type} cell was generated for this seed.");
    }

    [Fact]
    public void WeatherTurnsGroundToMudAndReverts()
    {
        SimWorld world = SovietWeatherBase(out _);

        // Grass, not mud: the generator already lays mud near water, and starting
        // on it would make the test pass without the ability doing anything.
        WorldPos target = CellOfType(world, TerrainType.Grass);
        int cell = world.Navigation.IndexOfWorld(target);

        Assert.Equal(TerrainType.Grass, world.TerrainTypes.TypeAt(cell));

        world.Enqueue(SimCommand.UseAbility(AbilityId.WeatherControl, target, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(TerrainType.Mud, world.TerrainTypes.TypeAt(cell));
        Assert.True(world.TerrainTypes.HasWeather);

        // The ground dries out when the effect expires.
        AbilityCatalog.TryGet(AbilityId.WeatherControl, out AbilityDefinition definition);
        world.RunTicks(definition.WeatherDurationTicks + 2);

        Assert.Equal(TerrainType.Grass, world.TerrainTypes.TypeAt(cell));
        Assert.False(world.TerrainTypes.HasWeather);
    }

    [Fact]
    public void MudSlowsTheEnemyButDoesNotStrandIt()
    {
        SimWorld world = SovietWeatherBase(out _);
        WorldPos target = CellOfType(world, TerrainType.Grass);
        int cell = world.Navigation.IndexOfWorld(target);

        TerrainLayer terrain = world.TerrainTypes;
        PathContext context = SimWorld.PathContextFor(Faction.Western, UnitKind.Tank);

        int before = terrain.CostPermille(cell, context.Movement, context.GroundPressurePermille);

        world.Enqueue(SimCommand.UseAbility(AbilityId.WeatherControl, target, world.Tick + 1, 0));
        world.Step();

        int after = terrain.CostPermille(cell, context.Movement, context.GroundPressurePermille);

        Assert.True(after > before, $"Mud did not slow the cell ({before} -> {after}).");
        Assert.True(terrain.IsPassable(cell, context.Movement), "Weather made the ground impassable.");
    }

    [Fact]
    public void TheChineseCannotControlTheWeather()
    {
        SimWorld world = new(seed: 2025, capacity: 16);

        ref TeamState state = ref world.TeamRef(1);
        state.TechTier = 3;
        state.Materials = 10_000;

        // Denied on faction before anything else is even considered.
        Assert.False(world.CanUseAbility(1, AbilityId.WeatherControl, out string reason));
        Assert.Contains("παράταξη", reason);

        // A Σοβιετικοί team at era III is denied on the era instead.
        ref TeamState soviet = ref world.TeamRef(0);
        soviet.TechTier = 3;
        soviet.Materials = 10_000;

        Assert.False(world.CanUseAbility(0, AbilityId.WeatherControl, out string eraReason));
        Assert.Contains("τεχνολογία 4", eraReason);
    }

    [Fact]
    public void TerrainChangesArePartOfTheStateHash()
    {
        // Terrain used to be a pure function of the seed and needed no hashing.
        // Once weather can write to it, two worlds that differ only in surface must
        // hash differently, or a replay could diverge unnoticed.
        SimWorld a = SovietWeatherBase(out WorldPos centre);
        SimWorld b = SovietWeatherBase(out centre);

        Assert.Equal(StateHash.Compute(a), StateHash.Compute(b));

        WorldPos target = new(centre.X + 120_000, 0, centre.Z + 60_000);
        a.Enqueue(SimCommand.UseAbility(AbilityId.WeatherControl, target, a.Tick + 1, 0));
        a.Step();

        Assert.NotEqual(StateHash.Compute(a), StateHash.Compute(b));
    }
}
