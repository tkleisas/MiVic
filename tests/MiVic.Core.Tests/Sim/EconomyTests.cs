using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class UnitCatalogTests
{
    [Fact]
    public void SovietUnitsCostMoreThanChinese()
    {
        Assert.True(
            UnitCatalog.MaterialCost(Faction.Soviet, UnitKind.Tank) >
            UnitCatalog.MaterialCost(Faction.Chinese, UnitKind.Tank));
    }

    [Fact]
    public void ChineseBuildFasterThanSoviets()
    {
        Assert.True(
            UnitCatalog.BuildTicks(Faction.Chinese, UnitKind.Tank) <
            UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Tank));
    }

    [Fact]
    public void ChineseCannotUnlockHighTiers()
    {
        // Tier 3 is the aircraft tier; the Κινέζοι ceiling is 2.
        Assert.False(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.Aircraft, techTier: 5));
        Assert.False(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.Aircraft, techTier: 2));
        Assert.True(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.Tank, techTier: 2));
    }

    [Fact]
    public void SovietsReachHigherTechThanChinese()
    {
        Assert.True(UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.Aircraft, techTier: 3));
        Assert.True(UnitCatalog.IsUnlocked(Faction.Western, UnitKind.Aircraft, techTier: 3));
    }

    [Fact]
    public void SovietsResearchFasterThanChinese()
    {
        Assert.True(FactionProfile.Soviet.ResearchSpeedPermille > FactionProfile.Chinese.ResearchSpeedPermille);

        // The same base project takes longer for the Κινέζοι.
        Assert.True(TechCatalog.TryGet(TechId.SovietAdvance2, out TechProject soviet));
        Assert.True(TechCatalog.TryGet(TechId.ChineseAdvance2, out TechProject chinese));

        Assert.True(
            TechCatalog.TicksFor(Faction.Soviet, soviet) < TechCatalog.TicksFor(Faction.Chinese, chinese));
    }

    [Fact]
    public void StructuresAreProducedAtTheCommandCentre()
    {
        foreach (UnitDefinition definition in UnitCatalog.All)
        {
            if (definition.IsBuilding)
            {
                Assert.Equal(UnitKind.CommandCentre, definition.ProducedAt);
            }
        }
    }

    [Fact]
    public void VehiclesAreProducedAtFactories()
    {
        Assert.Equal(UnitKind.Factory, UnitCatalog.Get(UnitKind.Tank).ProducedAt);
        Assert.Equal(UnitKind.Factory, UnitCatalog.Get(UnitKind.Aircraft).ProducedAt);
        Assert.Equal(UnitKind.CommandCentre, UnitCatalog.Get(UnitKind.Infantry).ProducedAt);
    }
}

public sealed class EconomyTests
{
    private static SimWorld WorldWithHeadquarters(Faction faction, int team)
    {
        SimWorld world = new(seed: 4242, capacity: 32);
        WorldPos position = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        world.Spawn(faction, team, UnitKind.CommandCentre, position, Fix32.Zero, 5_000);
        return world;
    }

    [Fact]
    public void HeadquartersGeneratesMaterials()
    {
        SimWorld world = WorldWithHeadquarters(Faction.Soviet, 0);

        world.RunTicks(10);

        Assert.True(world.Team(0).Materials >= EconomySystem.CommandCentreMaterials * 10);
    }

    [Fact]
    public void PowerPlantsGenerateEnergy()
    {
        SimWorld world = WorldWithHeadquarters(Faction.Soviet, 0);
        WorldPos position = world.Navigation.CentreOf(world.Navigation.NearestWalkable(500));
        world.Spawn(Faction.Soviet, 0, UnitKind.PowerPlant, position, Fix32.Zero, 1_200);

        world.RunTicks(10);

        // A power plant out-produces the headquarters' upkeep.
        Assert.True(world.Team(0).EnergyPerTick > 0);
    }

    [Fact]
    public void EnergyNeverGoesNegative()
    {
        SimWorld world = new(seed: 7, capacity: 16);
        WorldPos position = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        // Three factories and no power: heavy upkeep, no generation.
        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, position, Fix32.Zero, 2_000);
        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, position, Fix32.Zero, 2_000);
        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, position, Fix32.Zero, 2_000);

        world.RunTicks(100);

        Assert.Equal(0, world.Team(0).Energy);
    }

    [Fact]
    public void TeamsKeepSeparateStockpiles()
    {
        SimWorld world = new(seed: 9, capacity: 16);
        WorldPos position = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, position, Fix32.Zero, 5_000);

        world.RunTicks(20);

        Assert.True(world.Team(0).Materials > 0);
        Assert.Equal(0, world.Team(2).Materials);
    }
}

public sealed class ProductionTests
{
    private static (SimWorld World, EntityId Building) WorldWith(
        Faction faction,
        int team,
        UnitKind building,
        int materials = 10_000,
        int energy = 10_000,
        int techTier = 1,
        int water = 10_000)
    {
        SimWorld world = new(seed: 4242, capacity: 64);
        WorldPos position = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        EntityId id = world.Spawn(faction, team, building, position, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(team);
        state.Materials = materials;
        state.Energy = energy;
        state.Water = water;
        state.TechTier = techTier;

        return (world, id);
    }

    [Fact]
    public void QueuedUnitCostsResourcesUpFront()
    {
        (SimWorld world, EntityId building) = WorldWith(Faction.Soviet, 0, UnitKind.CommandCentre);

        int before = world.Team(0).Materials;
        world.Enqueue(SimCommand.QueueUnit(building, UnitKind.Infantry, world.Tick + 1, 0));
        world.Step();

        // The tick also pays out the headquarters' income.
        int expected = before + EconomySystem.CommandCentreMaterials - UnitCatalog.MaterialCost(Faction.Soviet, UnitKind.Infantry);

        Assert.Equal(expected, world.Team(0).Materials);
        Assert.Equal(1, world.JobsOf(0).Length);
    }

    [Fact]
    public void TeamsStartAtTechTierOne()
    {
        SimWorld world = new(seed: 1, capacity: 4);

        // Tier-1 hardware must be buildable from the first tick.
        Assert.Equal(1, world.Team(0).TechTier);
        Assert.True(UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.Infantry, world.Team(0).TechTier));
        Assert.True(UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.PowerPlant, world.Team(0).TechTier));
    }

    [Fact]
    public void QueuedUnitIsProducedAfterItsBuildTime()
    {
        (SimWorld world, EntityId building) = WorldWith(Faction.Soviet, 0, UnitKind.CommandCentre);
        int ticks = UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Infantry);

        world.Enqueue(SimCommand.QueueUnit(building, UnitKind.Infantry, world.Tick + 1, 0));
        world.RunTicks(ticks + 2);

        Assert.Equal(0, world.JobsOf(0).Length);
        Assert.Equal(2, world.AliveCount); // headquarters plus the new infantry.

        bool found = false;
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == UnitKind.Infantry)
            {
                found = true;
                Assert.Equal(0, world.GetRefBySlot(slot).TeamId);
            }
        }

        Assert.True(found, "The infantry unit was never produced.");
    }

    [Fact]
    public void QueueingFailsWithoutResources()
    {
        (SimWorld world, EntityId building) = WorldWith(Faction.Soviet, 0, UnitKind.CommandCentre, materials: 0, energy: 0);

        world.Enqueue(SimCommand.QueueUnit(building, UnitKind.Infantry, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.JobsOf(0).Length);
    }

    [Fact]
    public void BuildingCannotProduceAnotherRole()
    {
        (SimWorld world, EntityId building) = WorldWith(Faction.Soviet, 0, UnitKind.CommandCentre, techTier: 2);

        // A tank needs a factory, not a headquarters.
        world.Enqueue(SimCommand.QueueUnit(building, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.JobsOf(0).Length);
    }

    [Fact]
    public void TechGatingBlocksLockedUnits()
    {
        (SimWorld world, EntityId factory) = WorldWith(Faction.Soviet, 0, UnitKind.Factory, techTier: 1);

        // The design is approved, so this test measures the tier gate alone; the
        // design-bureau gate has its own tests.
        world.TeamRef(0).ApprovedMask |= 1u << (int)UnitKind.Tank;

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.JobsOf(0).Length);

        world.TeamRef(0).TechTier = 2;
        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(1, world.JobsOf(0).Length);
    }

    [Fact]
    public void CancelRemovesTheLastJob()
    {
        (SimWorld world, EntityId building) = WorldWith(Faction.Soviet, 0, UnitKind.CommandCentre);

        world.Enqueue(SimCommand.QueueUnit(building, UnitKind.Infantry, world.Tick + 1, 0));
        world.Step();
        world.Enqueue(SimCommand.CancelProduction(building, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.JobsOf(0).Length);
    }

    [Fact]
    public void QueueIsCapped()
    {
        (SimWorld world, EntityId building) = WorldWith(Faction.Soviet, 0, UnitKind.CommandCentre);

        for (int i = 0; i < SimConstants.MaxQueueLength + 4; i++)
        {
            world.Enqueue(SimCommand.QueueUnit(building, UnitKind.Infantry, world.Tick + 1, 0));
            world.Step();
        }

        Assert.Equal(SimConstants.MaxQueueLength, world.JobsOf(0).Length);
    }

    [Fact]
    public void ChineseParallelSlotsOutProduceSoviets()
    {
        // Six cheap fast hulls at once against two expensive slow ones is the
        // whole point of the Κινέζοι design.
        (SimWorld chinese, EntityId chineseFactory) = WorldWith(Faction.Chinese, 1, UnitKind.Factory, techTier: 2);
        (SimWorld soviet, EntityId sovietFactory) = WorldWith(Faction.Soviet, 0, UnitKind.Factory, techTier: 2);

        for (int i = 0; i < 6; i++)
        {
            chinese.Enqueue(SimCommand.QueueUnit(chineseFactory, UnitKind.Tank, chinese.Tick + 1, 1));
            chinese.Step();
            soviet.Enqueue(SimCommand.QueueUnit(sovietFactory, UnitKind.Tank, soviet.Tick + 1, 0));
            soviet.Step();
        }

        chinese.RunTicks(100);
        soviet.RunTicks(100);

        int chineseTanks = CountKind(chinese, UnitKind.Tank);
        int sovietTanks = CountKind(soviet, UnitKind.Tank);

        Assert.True(chineseTanks > sovietTanks, $"Κινέζοι produced {chineseTanks}, Σοβιετικοί {sovietTanks}.");
        Assert.True(chineseTanks >= 4, $"Κινέζοι only produced {chineseTanks} tanks in 100 ticks.");
    }

    private static int CountKind(SimWorld world, UnitKind kind)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == kind)
            {
                count++;
            }
        }

        return count;
    }
}

public sealed class ResearchTests
{
    private static (SimWorld World, EntityId Bureau) WorldWithBureau(Faction faction, int team, int materials = 10_000)
    {
        SimWorld world = new(seed: 4242, capacity: 32);
        WorldPos position = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        EntityId id = world.Spawn(faction, team, UnitKind.DesignBureau, position, Fix32.Zero, 1_500);

        world.TeamRef(team).Materials = materials;
        world.TeamRef(team).Water = 10_000;
        return (world, id);
    }

    private static TechProject Project(TechId id)
    {
        Assert.True(TechCatalog.TryGet(id, out TechProject project));
        return project;
    }

    [Fact]
    public void ResearchCompletesAndRaisesTheTechTier()
    {
        (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0);
        TechProject project = Project(TechId.SovietAdvance2);
        int ticks = TechCatalog.TicksFor(Faction.Soviet, project);

        world.Enqueue(SimCommand.Research(bureau, project.Id, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.Team(0).IsResearching);
        Assert.Equal(project.Id, world.Team(0).ResearchingTech);

        world.RunTicks(ticks + 2);

        Assert.Equal(2, world.Team(0).TechTier);
        Assert.False(world.Team(0).IsResearching);
        Assert.True(TechCatalog.IsCompleted(world.Team(0).TechMask, project.Id));
    }

    [Fact]
    public void ResearchCostsMaterials()
    {
        (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0, materials: 1_000);
        TechProject project = Project(TechId.SovietAdvance2);

        world.Enqueue(SimCommand.Research(bureau, project.Id, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(1_000 - project.Cost, world.Team(0).Materials);
    }

    [Fact]
    public void PrerequisitesAreEnforced()
    {
        (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0);
        world.TeamRef(0).TechTier = 3;

        // Ηλεκτροτεχνία must come before Κυβερνητική.
        world.Enqueue(SimCommand.Research(bureau, TechId.SovietOgAs, world.Tick + 1, 0));
        world.Step();

        Assert.False(world.Team(0).IsResearching);

        world.TeamRef(0).TechMask |= 1UL << (int)TechId.SovietElectro;
        world.Enqueue(SimCommand.Research(bureau, TechId.SovietOgAs, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.Team(0).IsResearching);
    }

    [Fact]
    public void TheChineseTreeHasNoThirdEra()
    {
        // The Κινέζοι cannot research their way into the aircraft tier at all.
        foreach (TechProject project in TechCatalog.All)
        {
            if (project.Faction != Faction.Chinese)
            {
                continue;
            }

            Assert.False(
                project.Effect == TechEffect.AdvanceTier && project.Value >= 3,
                $"Κινέζοι project '{project.GreekName}' unlocks era {project.Value}.");
        }
    }

    [Fact]
    public void ResearchRequiresADesignBureau()
    {
        (SimWorld world, EntityId building) = WorldWithBureau(Faction.Soviet, 0);
        world.GetRefBySlot(0).Kind = UnitKind.Factory;

        world.Enqueue(SimCommand.Research(building, TechId.SovietDeepBattle, world.Tick + 1, 0));
        world.Step();

        Assert.False(world.Team(0).IsResearching);
    }

    [Fact]
    public void OnlyOneProjectRunsAtATime()
    {
        (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0);

        world.Enqueue(SimCommand.Research(bureau, TechId.SovietDeepBattle, world.Tick + 1, 0));
        world.Step();
        int remaining = world.Team(0).ResearchTicksRemaining;

        world.Enqueue(SimCommand.Research(bureau, TechId.SovietAdvance2, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(remaining - 1, world.Team(0).ResearchTicksRemaining);
    }

    [Fact]
    public void DeepBattleHalvesTheMudPenalty()
    {
        (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0);
        TechProject project = Project(TechId.SovietDeepBattle);

        Assert.Equal(1_000, world.Team(0).TerrainResistancePermille);

        int before = world.PathContextOf(0, Faction.Soviet, UnitKind.Tank).GroundPressurePermille;

        world.Enqueue(SimCommand.Research(bureau, project.Id, world.Tick + 1, 0));
        world.RunTicks(TechCatalog.TicksFor(Faction.Soviet, project) + 2);

        int after = world.PathContextOf(0, Faction.Soviet, UnitKind.Tank).GroundPressurePermille;

        Assert.Equal(project.Value, world.Team(0).TerrainResistancePermille);
        Assert.Equal(before / 2, after);
    }

    [Fact]
    public void CommandAutomationAddsAParallelSlot()
    {
        (SimWorld world, _) = WorldWithBureau(Faction.Soviet, 0);

        Assert.Equal(0, world.Team(0).BonusSlots);

        world.TeamRef(0).TechMask |= 1UL << (int)TechId.SovietOgAs;
        ResearchSystem.RefreshModifiers(ref world.TeamRef(0));

        Assert.Equal(1, world.Team(0).BonusSlots);
    }

    [Fact]
    public void ReconnaissanceWidensTheTeamsSight()
    {
        (SimWorld world, _) = WorldWithBureau(Faction.Soviet, 0);

        world.TeamRef(0).TechMask |= 1UL << (int)TechId.SovietRecon;
        ResearchSystem.RefreshModifiers(ref world.TeamRef(0));

        Assert.Equal(1_300, world.Team(0).VisionPermille);
    }

    [Fact]
    public void EraFourExistsAndTheSovietCeilingReachesIt()
    {
        Assert.True(TechCatalog.TryGet(TechId.SovietAdvance4, out TechProject era4));
        Assert.Equal(TechEffect.AdvanceTier, era4.Effect);
        Assert.Equal(4, era4.Value);
        Assert.True(
            FactionProfile.Soviet.TechCeiling >= 4,
            "Era IV content exists but the Σοβιετικοί ceiling cannot reach it.");
    }

    [Fact]
    public void ArmorResearchIncreasesSpawnedHitPoints()
    {
        (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0);
        TechProject project = Project(TechId.SovietOrbital);

        // Grant the prerequisite and the tier directly; the tree is tested above.
        world.TeamRef(0).TechTier = 3;
        world.TeamRef(0).TechMask |= 1UL << (int)TechId.SovietOgAs;

        world.Enqueue(SimCommand.Research(bureau, project.Id, world.Tick + 1, 0));
        world.RunTicks(TechCatalog.TicksFor(Faction.Soviet, project) + 2);

        Assert.Equal(project.Value, world.Team(0).ArmorPermille);

        EntityId unit = world.Spawn(Faction.Soviet, 0, UnitKind.Infantry, new WorldPos(0, 0, 0), Fix32.FromInt(100), 100);
        Assert.True(world.TryGet(unit, out Entity spawned));

        Assert.Equal((100 * project.Value) / 1_000, spawned.Health);
    }

    [Fact]
    public void ResearchIsDeterministic()
    {
        static ulong Run()
        {
            (SimWorld world, EntityId bureau) = WorldWithBureau(Faction.Soviet, 0);
            world.Enqueue(SimCommand.Research(bureau, TechId.SovietAdvance2, world.Tick + 1, 0));
            world.RunTicks(700);
            return StateHash.Compute(world);
        }

        Assert.Equal(Run(), Run());
    }
}
