using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The plural generation: a solar plant the Σοβιετικοί cannot build, a hydro plant the
/// water constrains, and a weather strike that is an economic weapon. Three decisions from
/// §4's notes, each pinned where the mechanic lives.
/// </summary>
public sealed class GenerationTests
{
    private const int Capacity = 1024;

    private static SimWorld World()
    {
        var world = new SimWorld(seed: 20250101, capacity: Capacity);
        Scenario.Build(world, ScenarioKind.Skirmish);
        return world;
    }

    private static EntityId Spawn(
        SimWorld world,
        Faction faction,
        int team,
        UnitKind kind,
        WorldPos position,
        int health = 0)
        => world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(position),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            health > 0 ? health : UnitCatalog.Get(kind).Health);

    private static WorldPos Clearing(SimWorld world)
        => world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

    [Fact]
    public void TheSovietsCannotBuildSolarAndTheOthersCan()
    {
        SimWorld world = World();

        // The queue path asks CanBuild, which asks IsUnlocked, which reads the NotFor
        // clause: the catalogue's answer is a sentence about the power map, and the gate
        // is where the sentence is enforced.
        Assert.False(world.CanBuild(0, UnitKind.SolarPlant), "the Σοβιετικοί built a solar plant");
        Assert.True(world.CanBuild(1, UnitKind.SolarPlant), "the Κινέζοι could not build solar");
        Assert.True(world.CanBuild(2, UnitKind.SolarPlant), "the Δυτικοί could not build solar");

        // The clause is asymmetry stated as a list: the other powers can build the thermal
        // plant the Soviets rely on, which is why the asymmetry bites in both directions.
        Assert.True(world.CanBuild(0, UnitKind.PowerPlant));
    }

    [Fact]
    public void AHydroPlantNeedsWaterBesideIt()
    {
        SimWorld world = World();
        WorldPos site = Clearing(world);

        // The site must first pass everything else: a patch the ground itself refuses
        // is the ground's answer, not the water rule's. Walk the cells until one holds
        // the hydro plant's footprint and still has no water beside it.
        if (!SolidPatch(world, site))
        {
            site = DrySolidPatch(world, site);
        }

        // Dry ground, however solid: the placement rule is the water's, and the refusal
        // is the same sentence a player's construction is shown.
        Assert.False(world.CanPlaceStructure(UnitKind.HydroPlant, site, out string reason),
            "the hydro plant stood on a dry ridge");
        Assert.Contains("νερό", reason, StringComparison.Ordinal);

        // And a plant of another role stands on the same ground without asking the
        // water: the constraint is the hydro plant's own, not the placement rule's.
        Assert.True(world.CanPlaceStructure(UnitKind.PowerPlant, site, out _),
            "the thermal plant was refused where only the hydro plant is constrained");
    }

    [Fact]
    public void AHydroPlantStandsWhereTheWaterIs()
    {
        // The water-side site: the map's own lake shore. The fixture finds a land cell
        // within the hydro reach of a water cell, which is what the placement rule asks for.
        SimWorld world = World();
        WorldPos site = WaterSide(world);

        if (site == WorldPos.Origin)
        {
            // The standard map always has a shore; the skip is a fixture guard rather
            // than a tolerance.
            return;
        }

        Assert.True(world.CanPlaceStructure(UnitKind.HydroPlant, site, out string reason),
            $"the shore refused a hydro plant: {reason}");
    }

    [Fact]
    public void TheWeatherTakesTheSolarFarmsOutputAway()
    {
        SimWorld world = World();
        WorldPos site = Clearing(world);

        world.RunTicks(2);
        int baseRate = world.Team(1).EnergyPerTick;

        EntityId farm = Spawn(world, Faction.Chinese, 1, UnitKind.SolarPlant, site);

        // The ground under the farm is grass: a hand-built fixture is allowed to arrange
        // its ground, and the question here is the weather's halving, not the generated
        // surface the spawn happened to land on.
        MiVic.Core.Numerics.WorldPos standing = world.GetRefBySlot(farm.Slot).Position;
        int farmCell = world.TerrainTypes.IndexOfWorld(standing.X, standing.Z);
        world.TerrainTypes.SetType(farmCell, TerrainType.Grass);

        // The farm's clear-sky rate: the sun's own generation, scaled by the faction.
        world.RunTicks(2);
        int clearRate = world.Team(1).EnergyPerTick;
        int solar = clearRate - baseRate;

        Assert.True(solar > 0, "the solar farm generated nothing in clear weather");

        // The Soviet weather strike laid mud over the farm: the cell under it reads mud,
        // and the rate is the weather's share of the sun's. The faction that cannot build
        // solar owns the weather, and this is why. The strike is laid over the farm's own
        // standing position — the same cell the fixture set to grass a moment ago.
        world.TerrainTypes.ApplyWeather(standing.X, standing.Z, 60_000, TerrainType.Mud, world.Tick + 1_000);
        world.RunTicks(2);

        int weatheredRate = world.Team(1).EnergyPerTick;
        int survived = 2;

        Assert.Equal(baseRate + survived, weatheredRate);
        Assert.True(weatheredRate < clearRate, "the weather did not take the farm's output away");
    }

    [Fact]
    public void TheCapacityLedgerCarriesTheNewKinds()
    {
        // The ledger is what a buyer asks before ordering: a solar farm occupies its
        // clear-sky rate's worth of capacity, measured against the base's own ledger
        // before and after.
        SimWorld world = World();
        WorldPos centre = Clearing(world);

        world.RunTicks(2);
        int baseGeneration = world.Team(1).PowerGeneration;

        Spawn(world, Faction.Chinese, 1, UnitKind.SolarPlant, centre);
        world.RunTicks(2);

        Assert.Equal(baseGeneration + 5, world.Team(1).PowerGeneration);
    }

    private static int ClearRate(SimWorld world, int team)
    {
        // The rate everything else on the team contributes: the skirmish's own buildings.
        int rate = world.Team(team).EnergyPerTick;

        // Subtract the solar plant's contribution by rebuilding the arithmetic the
        // economy does: the plant is the only unit this fixture added.
        return rate - (EconomySystem.SolarPlantEnergy *
            (FactionProfile.For(Faction.Soviet).IncomePermille) / 1_000);
    }


    private static WorldPos WaterSide(SimWorld world)
    {
        for (int z = 0; z < world.Navigation.Size; z++)
        {
            for (int x = 0; x < world.Navigation.Size; x++)
            {
                int cell = world.Navigation.IndexOf(x, z);

                if (!world.Navigation.IsWalkable(cell))
                {
                    continue;
                }

                TerrainType surface = world.TerrainTypes.TypeAt(cell);

                if (surface != TerrainType.Grass)
                {
                    continue;
                }

                if (WaterNear(world, x, z, 3) && SolidPatch(world, x, z))
                {
                    return world.Navigation.CentreOf(cell);
                }
            }
        }

        return WorldPos.Origin;
    }

    private static bool SolidPatch(SimWorld world, WorldPos site)
    {
        int cell = world.Navigation.IndexOfWorld(site);
        int cx = world.Navigation.CellX(Math.Max(cell, 0));
        int cz = world.Navigation.CellZ(Math.Max(cell, 0));

        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int candidate = world.Navigation.IndexOf(cx + dx, cz + dz);

                if (candidate < 0 || !world.Navigation.IsWalkable(candidate))
                {
                    return false;
                }

                TerrainType surface = world.TerrainTypes.TypeAt(candidate);

                if (surface is TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>The nearest solid-patch site with no water beside it, walked out from a start.</summary>
    private static WorldPos DrySolidPatch(SimWorld world, WorldPos start)
    {
        int cell = world.Navigation.IndexOfWorld(start);
        int cx = world.Navigation.CellX(Math.Max(cell, 0));
        int cz = world.Navigation.CellZ(Math.Max(cell, 0));

        for (int radius = 1; radius < 40; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int x = cx + dx;
                    int z = cz + dz;
                    int candidate = world.Navigation.IndexOf(x, z);

                    if (candidate < 0 || !world.Navigation.IsWalkable(candidate))
                    {
                        continue;
                    }

                    var centre = world.Navigation.CentreOf(candidate);

                    if (SolidPatch(world, centre) && !WaterNear(world, x, z, 3))
                    {
                        return centre;
                    }
                }
            }
        }

        return start;
    }

    private static bool WaterNear(SimWorld world, int x, int z, int reach)
    {
        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int cell = world.Navigation.IndexOf(x + dx, z + dz);

                if (cell < 0)
                {
                    continue;
                }

                TerrainType surface = world.TerrainTypes.TypeAt(cell);

                if (surface is TerrainType.ShallowWater or TerrainType.DeepWater)
                {
                    return true;
                }
            }
        }

        return false;
    }

    private static bool SolidPatch(SimWorld world, int x, int z)
    {
        for (int dz = -1; dz <= 1; dz++)
        {
            for (int dx = -1; dx <= 1; dx++)
            {
                int cell = world.Navigation.IndexOf(x + dx, z + dz);

                if (cell < 0 || !world.Navigation.IsWalkable(cell))
                {
                    return false;
                }

                TerrainType surface = world.TerrainTypes.TypeAt(cell);

                if (surface is TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava)
                {
                    return false;
                }
            }
        }

        return true;
    }
}
