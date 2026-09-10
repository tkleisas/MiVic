using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Where a player's structure goes.
/// <para>
/// A structure used to be produced by another structure and to appear at a fixed offset from
/// whatever made it: a position the player did not choose, could not see before paying for it,
/// and discovered by watching a building pop up somewhere they did not intend. The order now
/// carries a site, and the site is the player's — these are the tests that say so, and that the
/// preview, the panel and the command are all answering the same question about it.
/// </para>
/// </summary>
public sealed class StructurePlacementTests
{
    private const ulong Seed = 20250101;

    /// <summary>
    /// The standard skirmish, with the player's team able to pay for whatever it orders.
    /// <para>
    /// The scenario is the point: this is a real battlefield with the three bases on it, and a
    /// site has to be found on the ground the generator actually produced rather than on a
    /// patch of test terrain chosen to make the rule easy to satisfy.
    /// </para>
    /// </summary>
    private static SimWorld Skirmish(out ScenarioSetup setup)
    {
        SimWorld world = new(Seed, capacity: 1024);
        setup = Scenario.Build(world, ScenarioKind.Skirmish);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;
        state.TechTier = 4;

        return world;
    }

    /// <summary>
    /// Ground the map will hold a structure on: the patch the base-site search settles on for the
    /// middle of the map. A test that invented a coordinate would be asserting against a patch of
    /// terrain nobody chose, and on this seed the middle of the map is water.
    /// </summary>
    private static WorldPos BaseGround(SimWorld world)
        => world.TryFindBaseSite(WorldPos.Origin, out WorldPos site)
            ? site
            : world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

    /// <summary>A world with a headquarters and nothing else, for the rules about a whole team.</summary>
    private static SimWorld BareBase(int team, Faction faction)
    {
        SimWorld world = new(Seed, capacity: 256);

        world.Spawn(faction, team, UnitKind.CommandCentre, BaseGround(world), Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(team);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;

        return world;
    }

    /// <summary>
    /// Ground beside a point where a structure of a role may actually be raised: the nearest cell the
    /// whole placement rule accepts, found by asking rather than by knowing.
    /// <para>
    /// These tests used to put their structure on the base's own cell, because the site rule judged
    /// the ground and never what was standing on it. A building may not be raised inside another one
    /// now, so a site beside the base is something a test has to ask for — which is what the client
    /// does with the same call, and what keeps this fixture from quietly asserting that two
    /// structures in one cell is still allowed.
    /// </para>
    /// </summary>
    private static WorldPos SiteNear(SimWorld world, int team, UnitKind kind, WorldPos near)
    {
        int centre = Math.Max(world.Navigation.IndexOfWorld(near), 0);
        int centreX = world.Navigation.CellX(centre);
        int centreZ = world.Navigation.CellZ(centre);

        for (int radius = 0; radius <= 16; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // The ring only: everything inside it was searched already.
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = world.Navigation.IndexOf(centreX + dx, centreZ + dz);

                    if (candidate < 0)
                    {
                        continue;
                    }

                    WorldPos position = world.Navigation.CentreOf(candidate);

                    if (world.TryPlanStructure(team, kind, position, out WorldPos planned, out _))
                    {
                        return planned;
                    }
                }
            }
        }

        return near;
    }

    private static int CountKind(SimWorld world, int team, UnitKind kind)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId == team && entity.Kind == kind)
                {
                    count++;
                }
            }
        }

        return count;
    }

    /// <summary>
    /// The structure of a role standing <em>exactly</em> at a position, or -1. Exacting rather
    /// than nearest, because the scenario's own base already has one of every structure: a test
    /// that took the first factory it found would be reading the map rather than the order.
    /// </summary>
    private static int FindStructureAt(SimWorld world, int team, UnitKind kind, WorldPos site)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == kind &&
                entity.Position.X == site.X && entity.Position.Z == site.Z)
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>The lowest-numbered live structure of a role for a team, or -1.</summary>
    private static int FindStructure(SimWorld world, int team, UnitKind kind)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId == team && entity.Kind == kind)
                {
                    return slot;
                }
            }
        }

        return -1;
    }

    /// <summary>
    /// A site the rule accepts, at least <paramref name="atLeastMm"/> from a point — used to
    /// demand a site no offset from the base could ever have produced.
    /// </summary>
    private static bool TryFindSite(
        SimWorld world,
        int team,
        UnitKind kind,
        WorldPos farFrom,
        int atLeastMm,
        out WorldPos site)
    {
        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            WorldPos candidate = world.Navigation.CentreOf(cell);

            if (candidate.HorizontalDistanceTo(farFrom) < atLeastMm)
            {
                continue;
            }

            if (world.TryPlanStructure(team, kind, candidate, out WorldPos planned, out _))
            {
                site = planned;
                return true;
            }
        }

        site = default;
        return false;
    }

    /// <summary>
    /// A cell the rule refuses for a particular reason, found by asking rather than by knowing:
    /// the text is what a player reads, so it is what a test should look for.
    /// </summary>
    private static bool TryFindRefusedSite(SimWorld world, int team, UnitKind kind, string reason, out WorldPos site)
    {
        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            WorldPos candidate = world.Navigation.CentreOf(cell);

            if (world.TryPlanStructure(team, kind, candidate, out _, out _))
            {
                continue;
            }

            world.CanPlaceStructure(kind, candidate, out string actual);

            if (actual.Contains(reason, StringComparison.Ordinal))
            {
                site = candidate;
                return true;
            }
        }

        site = default;
        return false;
    }

    /// <summary>
    /// The point of the whole feature: an order naming a site raises the structure there.
    /// <para>
    /// The site is deliberately more than a hundred metres from the player's headquarters, which
    /// is the part a test of the old path could not have written: every offset a producer uses is
    /// under thirty metres, so a structure standing far away from anything is a structure that
    /// was placed rather than produced.
    /// </para>
    /// <para>
    /// The claim is measured on the tick the order executes rather than after the building is up,
    /// because this is a battlefield: a structure left standing in the open two hundred metres
    /// from its base is one the enemy is entitled to knock down, and that would be a fact about
    /// the war rather than about placement. What a structure does over the whole of its
    /// construction is <see cref="APlacedStructureRisesBeforeItWorks"/>'s question, asked on a
    /// map with nobody shooting.
    /// </para>
    /// </summary>
    [Fact]
    public void AStructureOrderedAtASiteIsBuiltAtThatSite()
    {
        SimWorld world = Skirmish(out ScenarioSetup setup);
        Assert.Equal(3, setup.CommandCentres.Count);

        ref Entity headquarters = ref world.GetRefBySlot(setup.CommandCentres[0].Slot);
        WorldPos home = headquarters.Position;

        Assert.True(
            TryFindSite(world, 0, UnitKind.Factory, home, 100_000, out WorldPos site),
            "The standard map has no site for a factory a hundred metres from the Soviet base.");

        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out WorldPos planned, out string reason), reason);

        world.Enqueue(SimCommand.Structure(UnitKind.Factory, site, world.Tick + 1, 0));
        world.Step();

        int factory = FindStructureAt(world, 0, UnitKind.Factory, planned);

        Assert.True(factory >= 0, "The order named a site and raised nothing at it.");

        ref Entity built = ref world.GetRefBySlot(factory);

        Assert.Equal(planned.X, built.Position.X);
        Assert.Equal(planned.Z, built.Position.Z);
        Assert.True(built.ConstructionTicksRemaining > 0, "It arrived finished rather than as a building site.");

        // And far from anything that could have made it, so no offset was involved.
        Assert.True(
            built.Position.HorizontalDistanceTo(home) > 100_000,
            $"The factory stands {built.Position.HorizontalDistanceTo(home) / 1000} m from the base, "
            + "which is an offset rather than a chosen site.");
    }

    /// <summary>
    /// The plan is the command. What <see cref="SimWorld.TryPlanStructure"/> says would happen —
    /// where the structure stands and what it costs — is what the order produces, cell for cell
    /// and rouble for rouble, measured against a world that was given no order at all.
    /// <para>
    /// The second world is what makes the bill exact: a base is earning materials every tick, so a
    /// single ledger read either side of the order would be income plus cost. Two worlds that
    /// differ only by the order differ by exactly the price of the structure.
    /// </para>
    /// </summary>
    [Fact]
    public void ThePlanIsWhatTheOrderBuildsAndWhatItCosts()
    {
        SimWorld ordered = Skirmish(out ScenarioSetup setup);
        SimWorld control = Skirmish(out _);

        ref Entity headquarters = ref ordered.GetRefBySlot(setup.CommandCentres[0].Slot);
        WorldPos site = default;

        Assert.True(TryFindSite(ordered, 0, UnitKind.Factory, headquarters.Position, 40_000, out site));
        Assert.True(ordered.TryPlanStructure(0, UnitKind.Factory, site, out WorldPos planned, out string reason), reason);

        int ticks = UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Factory);
        long executeTick = ordered.Tick + 1;

        ordered.Enqueue(SimCommand.Structure(UnitKind.Factory, site, executeTick, 0));

        // Halfway through the construction, where the site is standing and is not yet a building:
        // an unfinished structure draws nothing and earns nothing, so everything the two worlds
        // disagree about is the order itself.
        ordered.RunTicks(ticks / 2);
        control.RunTicks(ticks / 2);

        Assert.Equal(control.Team(0).Materials - UnitCatalog.MaterialCost(Faction.Soviet, UnitKind.Factory), ordered.Team(0).Materials);
        Assert.Equal(control.Team(0).Energy - UnitCatalog.EnergyCost(Faction.Soviet, UnitKind.Factory), ordered.Team(0).Energy);
        Assert.Equal(control.Team(0).Water - UnitCatalog.WaterCost(Faction.Soviet, UnitKind.Factory), ordered.Team(0).Water);

        int factory = FindStructureAt(ordered, 0, UnitKind.Factory, planned);
        Assert.True(factory >= 0, "The order was paid for and raised nothing at the planned site.");

        ref Entity built = ref ordered.GetRefBySlot(factory);
        Assert.True(built.ConstructionTicksRemaining > 0, "It was finished before the bill was read.");
        Assert.Equal(planned.X, built.Position.X);
        Assert.Equal(planned.Z, built.Position.Z);

        // And the site is the plan's cell, not the millimetre that was clicked: the ground that
        // was judged is the cell's, so that is where the structure belongs.
        Assert.Equal(ordered.Navigation.CentreOf(ordered.Navigation.IndexOfWorld(site)).X, built.Position.X);
    }

    /// <summary>
    /// A placed structure is a building site before it is a building: it takes the time the
    /// catalogue quotes, and it is not a structure of that role until it is up.
    /// </summary>
    [Fact]
    public void APlacedStructureRisesBeforeItWorks()
    {
        SimWorld world = BareBase(0, Faction.Soviet);
        WorldPos site = SiteNear(world, 0, UnitKind.Factory, BaseGround(world));

        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out WorldPos planned, out string reason), reason);

        world.Enqueue(SimCommand.Structure(UnitKind.Factory, site, world.Tick + 1, 0));
        world.Step();

        int factory = FindStructureAt(world, 0, UnitKind.Factory, planned);

        Assert.True(factory >= 0, "The order raised nothing.");
        Assert.True(world.GetRefBySlot(factory).ConstructionTicksRemaining > 0, "It arrived finished.");
        Assert.Equal(UnitCatalog.BuildTicks(Faction.Soviet, UnitKind.Factory), world.GetRefBySlot(factory).ConstructionTicksTotal);

        // A building site is not a factory: the team cannot queue armour at it yet.
        Assert.False(world.HasStructure(0, UnitKind.Factory));

        world.RunTicks(world.GetRefBySlot(factory).ConstructionTicksRemaining + 2);

        Assert.Equal(0, world.GetRefBySlot(factory).ConstructionTicksRemaining);
        Assert.True(world.HasStructure(0, UnitKind.Factory));
        Assert.Equal(planned.X, world.GetRefBySlot(factory).Position.X);
    }

    // ------------------------------------------------------------ every refusal names its rule
    //
    // The reason is what the interface puts in front of the player: beside the armed row while
    // they aim, and in the notice a refused click raises. A refusal that returns false with
    // nothing to say is the bug the bridge's own reasons were added for, one rule per sentence.

    [Fact]
    public void ASiteOnWaterIsRefusedAndSaysSo()
    {
        SimWorld world = Skirmish(out _);

        Assert.True(
            TryFindRefusedSite(world, 0, UnitKind.Factory, "στεριά", out WorldPos water),
            "No water cell on the standard map refuses a structure.");

        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, water, out _, out string reason));
        Assert.Contains("στεριά", reason);
    }

    [Fact]
    public void ASiteOnLavaIsRefusedAndSaysSo()
    {
        SimWorld world = Skirmish(out _);

        Assert.True(
            TryFindRefusedSite(world, 0, UnitKind.Factory, "λάβα", out WorldPos lava),
            "No lava cell on the standard map refuses a structure.");

        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, lava, out _, out string reason));
        Assert.Contains("λάβα", reason);
    }

    /// <summary>
    /// Ground that is dry and still will not do: the cell is walkable and not water, and the
    /// patch around it is too broken up for a building to stand on.
    /// </summary>
    [Fact]
    public void ASiteOnGroundThatWillNotHoldABuildingIsRefusedAndSaysSo()
    {
        SimWorld world = Skirmish(out _);

        Assert.True(
            TryFindRefusedSite(world, 0, UnitKind.Factory, "ανώμαλο", out WorldPos rough),
            "No roughed-up cell on the standard map refuses a structure.");

        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, rough, out _, out string reason));
        Assert.Contains("ανώμαλο", reason);
    }

    [Fact]
    public void ASiteOffTheMapIsRefusedAndSaysSo()
    {
        SimWorld world = Skirmish(out _);

        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, new WorldPos(SimConstants.MapExtentMm, 0, 0), out _, out string reason));
        Assert.Contains("χάρτη", reason);
    }

    /// <summary>
    /// Nothing raises itself. Every structure is raised from a command centre, and a team that
    /// has none — or has one that is still being built — cannot place anything.
    /// </summary>
    [Fact]
    public void AStructureWithoutACommandCentreIsRefusedAndSaysSo()
    {
        SimWorld world = new(Seed, capacity: 32);
        WorldPos site = BaseGround(world);

        world.TeamRef(2).Materials = 50_000;
        world.TeamRef(2).Energy = 50_000;
        world.TeamRef(2).Water = 50_000;

        Assert.False(world.CanBuildStructure(2, UnitKind.Factory, out string reason));
        Assert.Contains(UnitCatalog.GreekName(UnitKind.CommandCentre), reason);

        // And the whole plan says the same thing, because it is the same rule.
        Assert.False(world.TryPlanStructure(2, UnitKind.Factory, site, out _, out string planReason));
        Assert.Equal(reason, planReason);
    }

    [Fact]
    public void AStructureTheTeamHasNotReachedIsRefusedAndSaysSo()
    {
        // The nuclear plant is era IV, and every faction starts at era I.
        SimWorld world = BareBase(0, Faction.Soviet);

        Assert.Equal(1, world.Team(0).TechTier);
        Assert.False(world.CanBuildStructure(0, UnitKind.NuclearPlant, out string reason));
        Assert.Contains("τεχνολογία", reason);

        world.TeamRef(0).TechTier = 4;
        Assert.True(world.CanBuildStructure(0, UnitKind.NuclearPlant, out string allowed), allowed);
    }

    [Fact]
    public void SomethingThatIsNotAStructureIsRefusedAndSaysSo()
    {
        SimWorld world = BareBase(0, Faction.Soviet);

        Assert.False(world.CanBuildStructure(0, UnitKind.Tank, out string reason));
        Assert.Contains("κατασκευή", reason);
    }

    [Fact]
    public void AnUnknownTeamIsRefusedAndSaysSo()
    {
        SimWorld world = BareBase(0, Faction.Soviet);

        Assert.False(world.CanBuildStructure(SimConstants.TeamCount, UnitKind.Factory, out string reason));
        Assert.Contains("ομάδα", reason);
    }

    /// <summary>
    /// Each resource on its own, because a player looking at a greyed-out row needs to know which
    /// one they are short of and by how much — the same three sentences a crossing is refused
    /// with, and the same ones the build panel has always shown.
    /// </summary>
    [Fact]
    public void AStructureRefusalNamesTheResourceItIsShortOf()
    {
        SimWorld world = BareBase(0, Faction.Soviet);
        WorldPos site = SiteNear(world, 0, UnitKind.Factory, BaseGround(world));
        ref TeamState state = ref world.TeamRef(0);

        int materials = UnitCatalog.MaterialCost(Faction.Soviet, UnitKind.Factory);
        int energy = UnitCatalog.EnergyCost(Faction.Soviet, UnitKind.Factory);
        int water = UnitCatalog.WaterCost(Faction.Soviet, UnitKind.Factory);

        state.Materials = materials - 1;
        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string materialsReason));
        Assert.Equal("λείπουν 1 Π", materialsReason);

        state.Materials = 50_000;
        state.Energy = energy - 1;
        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string energyReason));
        Assert.Equal("λείπουν 1 Ε", energyReason);

        state.Energy = 50_000;
        state.Water = water - 1;
        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string waterReason));
        Assert.Equal("λείπουν 1 Ν", waterReason);

        state.Water = 50_000;
        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string allowed), allowed);
    }

    /// <summary>
    /// A world with no room in it refuses rather than throwing: raising a structure is a spawn,
    /// and a spawn with nowhere to land would take the client down instead of telling the player.
    /// </summary>
    [Fact]
    public void AFullWorldIsRefusedAndSaysSo()
    {
        SimWorld world = new(Seed, capacity: 4);
        WorldPos site = BaseGround(world);

        // A headquarters, so the role is unlocked and the world's own capacity is the only thing
        // left that can refuse — and three more, to fill it.
        world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, site, Fix32.Zero, 5_000);

        for (int i = 0; i < 3; i++)
        {
            world.Spawn(Faction.Soviet, 0, UnitKind.PowerPlant, site, Fix32.Zero, 1_200);
        }

        world.TeamRef(0).Materials = 50_000;
        world.TeamRef(0).Energy = 50_000;
        world.TeamRef(0).Water = 50_000;

        Assert.Equal(4, world.AliveCount);
        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string reason));
        Assert.Equal("ο χάρτης είναι γεμάτος", reason);
    }

    /// <summary>
    /// A refused order costs nothing but the click. The same promise the bridge makes, and the
    /// only thing that makes arming a placement safe to point at a lake. Measured against a world
    /// that was given no order, because a base is earning materials every tick.
    /// </summary>
    [Fact]
    public void ARefusedStructureIsNotChargedFor()
    {
        SimWorld world = Skirmish(out _);
        SimWorld control = Skirmish(out _);

        Assert.True(TryFindRefusedSite(world, 0, UnitKind.Factory, "στεριά", out WorldPos water));

        world.Enqueue(SimCommand.Structure(UnitKind.Factory, water, world.Tick + 1, 0));
        world.Step();
        control.Step();

        Assert.Equal(control.Team(0).Materials, world.Team(0).Materials);
        Assert.Equal(control.Team(0).Energy, world.Team(0).Energy);
        Assert.Equal(control.Team(0).Water, world.Team(0).Water);
    }

    /// <summary>
    /// The button and the command answer the same question. The panel's row is enabled by
    /// <see cref="SimWorld.CanBuildStructure"/> and the order is accepted or refused by the plan
    /// that contains it: a row that looks available and does nothing is the same bug as a refusal
    /// with no words, and this is the pair staying in step.
    /// </summary>
    [Fact]
    public void TheRowRuleIsTheCommandsRule()
    {
        SimWorld world = BareBase(0, Faction.Soviet);
        WorldPos site = SiteNear(world, 0, UnitKind.Factory, BaseGround(world));

        Assert.True(world.CanBuildStructure(0, UnitKind.Factory, out string allowed));
        Assert.Equal(string.Empty, allowed);
        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out _));

        // A team that cannot pay: neither answer may be yes, and both say the same thing.
        world.TeamRef(0).Materials = 0;

        Assert.False(world.CanBuildStructure(0, UnitKind.Factory, out string shortOf));
        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string shortOfSite));
        Assert.Equal(shortOf, shortOfSite);
        Assert.Contains("λείπουν", shortOf);

        // A team with no industry at all: the row is greyed and the order is refused.
        SimWorld bare = new(Seed, capacity: 32);
        bare.TeamRef(0).Materials = 50_000;
        bare.TeamRef(0).Energy = 50_000;
        bare.TeamRef(0).Water = 50_000;

        Assert.False(bare.CanBuildStructure(0, UnitKind.Factory, out string noYard));
        Assert.False(bare.TryPlanStructure(0, UnitKind.Factory, site, out _, out string noYardSite));
        Assert.Equal(noYard, noYardSite);
    }

    // ---------------------------------------------------------------------- and the AI still builds

    /// <summary>
    /// The AI has no way to choose a site and does not need one: it keeps the offset placement,
    /// and the ground has the last word through the same predicate the player's click is judged
    /// by. This is the AI's own order of business — a power plant at a bare headquarters — on the
    /// standard map, so the site is the one the map gives rather than one a test arranged.
    /// </summary>
    [Fact]
    public void TheAiStillRaisesStructuresOnItsOffsetPath()
    {
        SimWorld world = BareBase(1, Faction.Chinese);
        WorldPos home = world.GetRefBySlot(FindStructure(world, 1, UnitKind.CommandCentre)).Position;

        world.RunTicks(500);

        int plant = FindStructure(world, 1, UnitKind.PowerPlant);

        Assert.True(plant >= 0, "The AI raised no power plant, so the offset path is broken.");

        // Legal: the site passes the same rule the player's own placement is judged by.
        ref Entity structure = ref world.GetRefBySlot(plant);
        Assert.True(
            world.CanPlaceStructure(structure.Kind, structure.Position, out string reason),
            $"The AI's power plant stands where a player's click would be refused: {reason}");

        // And still an offset from what made it, which is the path the AI kept.
        Assert.True(
            structure.Position.HorizontalDistanceTo(home) < 100_000,
            "The AI's structure is nowhere near its base, so it is not the offset path.");
    }

    /// <summary>
    /// Every structure the AI raises stands where the rule allows, which is the whole point of
    /// sending the offset through the same validation: an AI structure may not land somewhere the
    /// player's own click would be refused for.
    /// </summary>
    [Fact]
    public void EveryStructureTheAiRaisesStandsSomewhereTheRuleAllows()
    {
        SimWorld world = BareBase(1, Faction.Chinese);

        world.TeamRef(1).Materials = 200_000;
        world.TeamRef(1).Energy = 200_000;
        world.TeamRef(1).Water = 200_000;
        world.TeamRef(1).TechTier = 2;

        world.RunTicks(3_000);

        int seen = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != 1 || !UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            seen++;

            Assert.True(
                world.CanPlaceStructure(entity.Kind, entity.Position, out string reason),
                $"{entity.Kind} at ({entity.Position.X / 1000}, {entity.Position.Z / 1000}) m: {reason}");
        }

        // More than the one it started with: the AI raised something across the run.
        Assert.True(seen > 1, "The AI raised no structures at all, so this test proves nothing.");
    }

    /// <summary>
    /// And the AI still builds armour, which is the behaviour the offset path is there to serve:
    /// structures land somewhere legal and production carries on from them.
    /// </summary>
    [Fact]
    public void TheAiStillProducesUnitsAfterPlacingItsStructures()
    {
        SimWorld world = BareBase(1, Faction.Chinese);

        world.TeamRef(1).Materials = 100_000;
        world.TeamRef(1).Energy = 100_000;
        world.TeamRef(1).Water = 100_000;

        world.RunTicks(2_500);

        Assert.True(CountKind(world, 1, UnitKind.Factory) > 0, "The AI never finished a factory.");
        Assert.True(
            CountKind(world, 1, UnitKind.Tank) + CountKind(world, 1, UnitKind.Infantry) > 0,
            "The AI placed its structures and produced nothing from them.");
    }

    // ------------------------------------------------------------------ a building's own footprint
    //
    // A structure used to be judged on a base's yard: an 11 × 11 patch with 90% of it solid and a
    // solid 2 × 2 core, which is what a base needs and what a factory does not. On the standard seed
    // that left fifteen cells accepted within a hundred metres of the Soviet base, and all fifteen
    // were inside the base's own yard — the cells just north of the headquarters — because the only
    // ground within reach that would hold an entire base was the base. Every other direction was
    // ανώμαλο έδαφος, so a factory twenty metres west of the player's own headquarters was refused
    // for failing a test meant for a base. The footprint is now the building's own, by kind.

    /// <summary>
    /// The cell a structure stands on, at an offset in navigation cells from a position — the same
    /// arithmetic the placement rule walks a footprint with. Returns false when the offset is off the
    /// map, which the Soviet base is close enough to the western edge for.
    /// </summary>
    private static bool TryOffsetCells(SimWorld world, WorldPos from, int dx, int dz, out WorldPos site)
    {
        int cell = Math.Max(world.Navigation.IndexOfWorld(from), 0);
        int index = world.Navigation.IndexOf(
            world.Navigation.CellX(cell) + dx,
            world.Navigation.CellZ(cell) + dz);

        if (index < 0)
        {
            site = default;
            return false;
        }

        site = world.Navigation.CentreOf(index);
        return true;
    }

    /// <summary>How many cells of the square of this radius around a point a plan accepts.</summary>
    private static int CountSites(SimWorld world, int team, UnitKind kind, WorldPos centre, int radiusCells)
    {
        int accepted = 0;
        int cell = Math.Max(world.Navigation.IndexOfWorld(centre), 0);
        int centreX = world.Navigation.CellX(cell);
        int centreZ = world.Navigation.CellZ(cell);

        for (int dz = -radiusCells; dz <= radiusCells; dz++)
        {
            for (int dx = -radiusCells; dx <= radiusCells; dx++)
            {
                int candidate = world.Navigation.IndexOf(centreX + dx, centreZ + dz);

                if (candidate < 0)
                {
                    continue;
                }

                if (world.TryPlanStructure(team, kind, world.Navigation.CentreOf(candidate), out _, out _))
                {
                    accepted++;
                }
            }
        }

        return accepted;
    }

    /// <summary>
    /// The point of the change: a factory can go right beside the player's own headquarters, and the
    /// ground there is the factory's own rather than a base's.
    /// <para>
    /// The nearest cell the rule accepts is asserted to be one building's width away, and the ground
    /// there is asserted <em>not</em> to be a base site — which is the evidence that the patch that
    /// was judged is the building's footprint rather than the base's yard. The base's yard is still
    /// there and still means what it meant: what changed is that a factory is no longer asked to
    /// satisfy it.
    /// </para>
    /// </summary>
    [Fact]
    public void AFactoryCanBePlacedBesideItsOwnHeadquarters()
    {
        SimWorld world = Skirmish(out ScenarioSetup setup);

        ref Entity headquarters = ref world.GetRefBySlot(setup.CommandCentres[0].Slot);
        WorldPos home = headquarters.Position;

        int nearest = int.MaxValue;
        WorldPos nearestSite = default;

        for (int dz = -6; dz <= 6; dz++)
        {
            for (int dx = -6; dx <= 6; dx++)
            {
                if (!TryOffsetCells(world, home, dx, dz, out WorldPos candidate))
                {
                    continue;
                }

                if (!world.TryPlanStructure(0, UnitKind.Factory, candidate, out WorldPos planned, out _))
                {
                    continue;
                }

                int distance = planned.HorizontalDistanceTo(home);

                if (distance < nearest)
                {
                    nearest = distance;
                    nearestSite = planned;
                }
            }
        }

        // Three cells, 28.1 m, which is one building's width from the headquarters: the distance at
        // which two 3 × 3 footprints stop sharing a cell. A factory could not be ordered twenty
        // metres west of the headquarters before this, and twenty metres is between the two.
        Assert.True(
            nearest <= 30_000,
            $"The nearest factory site is {nearest / 1000} m from the base, which is not beside it.");

        Assert.True(
            world.TryPlanStructure(0, UnitKind.Factory, nearestSite, out WorldPos plannedSite, out string reason),
            reason);

        // The patch that was judged is the factory's own: the base's yard does not hold here, and
        // that is exactly the cell this fix was for.
        Assert.False(
            world.IsBaseSite(plannedSite),
            "the base's yard accepts this site too, so the footprint is not what was asked about");
    }

    /// <summary>
    /// A nuclear plant is not a power plant with a different price tag: the reactor needs more clear
    /// ground, so there are cells that will take the one and not the other.
    /// </summary>
    [Fact]
    public void ANuclearPlantNeedsMoreRoomThanAPowerPlant()
    {
        SimWorld world = Skirmish(out ScenarioSetup setup);
        WorldPos home = world.GetRefBySlot(setup.CommandCentres[0].Slot).Position;

        int power = UnitCatalog.FootprintRadiusCells(UnitKind.PowerPlant);
        int nuclear = UnitCatalog.FootprintRadiusCells(UnitKind.NuclearPlant);

        Assert.True(nuclear > power, $"a nuclear plant takes {nuclear} cells of radius and a power plant {power}");

        int powerSites = CountSites(world, 0, UnitKind.PowerPlant, home, 10);
        int nuclearSites = CountSites(world, 0, UnitKind.NuclearPlant, home, 10);

        Assert.True(
            nuclearSites < powerSites,
            $"the reactor fits on {nuclearSites} cells around the base and the station on {powerSites}, "
            + "so the two footprints are not doing any work");
    }

    /// <summary>
    /// A base site is still a base site. The yard was not loosened to make room for buildings — the
    /// strictness moved nowhere, it simply stopped being asked of a factory.
    /// </summary>
    [Fact]
    public void ABaseSiteIsStillAsStrictAsItWas()
    {
        SimWorld world = Skirmish(out ScenarioSetup setup);
        WorldPos home = world.GetRefBySlot(setup.CommandCentres[0].Slot).Position;

        // The ground the base itself stands on is still a base site, and the rule still counts the
        // same patch it always did.
        Assert.True(world.IsBaseSite(home));
        Assert.Equal(121, SimWorld.BaseSitePatchCells);
        Assert.Equal(5, SimWorld.BaseSiteRadiusCells);
        Assert.Equal(900, SimWorld.BaseSiteMinSolidPermille);

        // And it is still much harder to satisfy than a building's footprint: there are cells that
        // take a factory and would not take a base.
        int factoryOnly = 0;

        for (int dz = -10; dz <= 10; dz++)
        {
            for (int dx = -10; dx <= 10; dx++)
            {
                if (!TryOffsetCells(world, home, dx, dz, out WorldPos candidate))
                {
                    continue;
                }

                if (world.TryPlanStructure(0, UnitKind.Factory, candidate, out WorldPos planned, out _) &&
                    !world.IsBaseSite(planned))
                {
                    factoryOnly++;
                }
            }
        }

        Assert.True(
            factoryOnly > 0,
            "every cell that takes a factory would also take a base, so the yard rule has been loosened");
    }

    // ------------------------------------------------------------------ nothing is built on top

    /// <summary>
    /// Two buildings in one cell used to be possible, because the site rule judged the ground and
    /// never what was standing on it — and two models rendering inside each other is what that
    /// looked like. The refusal names what is in the way, because that is the part a player can act
    /// on.
    /// </summary>
    [Fact]
    public void AStructureOnTopOfAStructureIsRefusedAndSaysSo()
    {
        SimWorld world = Skirmish(out ScenarioSetup setup);
        WorldPos home = world.GetRefBySlot(setup.CommandCentres[0].Slot).Position;

        Assert.False(world.TryPlanStructure(0, UnitKind.Factory, home, out _, out string reason));
        Assert.Equal("επικαλύπτεται με Κέντρο Διοίκησης", reason);

        // The ground under it is fine, which is the whole point: this refusal is not about the
        // ground, and it must not be confused with one that is.
        Assert.True(world.CanPlaceStructure(UnitKind.Factory, home, out string ground), ground);
        Assert.False(world.IsSiteClear(UnitKind.Factory, home, out _));
    }

    /// <summary>
    /// And beside one is accepted. A footprint is a square of ground, not a clearance zone: at two
    /// cells the two patches still share a cell and the site is refused, and at three they do not.
    /// </summary>
    [Fact]
    public void AStructureBesideAnotherIsAccepted()
    {
        SimWorld world = Skirmish(out ScenarioSetup setup);
        WorldPos home = world.GetRefBySlot(setup.CommandCentres[0].Slot).Position;
        int reach = UnitCatalog.FootprintRadiusCells(UnitKind.Factory)
            + UnitCatalog.FootprintRadiusCells(UnitKind.CommandCentre);

        Assert.True(TryOffsetCells(world, home, 0, reach, out WorldPos inside));
        Assert.True(TryOffsetCells(world, home, 0, reach + 1, out WorldPos beside));
        Assert.False(
            world.TryPlanStructure(0, UnitKind.Factory, inside, out _, out string insideReason),
            "two footprints that share a cell were allowed to overlap");
        Assert.Contains("επικαλύπτεται", insideReason);

        Assert.True(
            world.TryPlanStructure(0, UnitKind.Factory, beside, out WorldPos planned, out string reason),
            reason);

        Assert.Equal(reach + 1, Math.Abs(world.Navigation.CellZ(world.Navigation.IndexOfWorld(planned)) -
            world.Navigation.CellZ(world.Navigation.IndexOfWorld(home))));
    }

    /// <summary>
    /// A tank standing on the site does not block it.
    /// <para>
    /// A unit is the one thing on the map that is on its way somewhere, so a rule that waited for it
    /// would make placement a question about the traffic of the moment. Nothing is harmed either:
    /// the building rises around whoever is standing there and they drive out from under it. What is
    /// refused is the thing that will still be there tomorrow.
    /// </para>
    /// </summary>
    [Fact]
    public void AUnitStandingOnASiteDoesNotBlockIt()
    {
        SimWorld world = BareBase(0, Faction.Soviet);
        WorldPos home = world.GetRefBySlot(FindStructure(world, 0, UnitKind.CommandCentre)).Position;
        Assert.True(TryOffsetCells(world, home, 0, 3, out WorldPos site));

        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out WorldPos planned, out string reason), reason);

        // A tank parked exactly where the building is going.
        world.Spawn(Faction.Soviet, 0, UnitKind.Tank, planned, Fix32.FromInt(400), 320);

        Assert.True(world.IsSiteClear(UnitKind.Factory, planned, out string clear), clear);
        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out _, out string stillAllowed), stillAllowed);
    }

    /// <summary>
    /// Two orders for one cell in the same tick: the first raises its building and the second finds
    /// it there, so exactly one structure stands and the same one stands twice in a row.
    /// <para>
    /// The determinism is the point rather than the arithmetic. Commands are applied in the order
    /// they were issued, and both orders are applied before anything else in the tick moves, so the
    /// answer cannot depend on which of two machines got there first.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoOrdersForOneCellInTheSameTickLeaveOneStructure()
    {
        static WorldPos Run(out SimWorld world, out int built)
        {
            world = BareBase(0, Faction.Soviet);
            WorldPos home = world.GetRefBySlot(FindStructure(world, 0, UnitKind.CommandCentre)).Position;
            Assert.True(TryOffsetCells(world, home, 0, 3, out WorldPos site));

            world.Enqueue(SimCommand.Structure(UnitKind.Factory, site, world.Tick + 1, 0));
            world.Enqueue(SimCommand.Structure(UnitKind.Factory, site, world.Tick + 1, 0));
            world.Step();

            built = CountKind(world, 0, UnitKind.Factory);
            return site;
        }

        WorldPos first = Run(out SimWorld once, out int builtOnce);
        WorldPos second = Run(out SimWorld twice, out int builtTwice);

        Assert.Equal(1, builtOnce);
        Assert.Equal(builtOnce, builtTwice);
        Assert.Equal(first, second);
        Assert.Equal(StateHash.Compute(once), StateHash.Compute(twice));

        // And the one that stands is on the cell both orders named.
        Assert.True(FindStructureAt(once, 0, UnitKind.Factory, once.Navigation.CentreOf(once.Navigation.IndexOfWorld(first))) >= 0);

        // The same order a second time, later, is refused for occupancy rather than raising a second
        // building on the same ground.
        Assert.False(once.TryPlanStructure(0, UnitKind.Factory, first, out _, out string reason));
        Assert.Contains("επικαλύπτεται", reason);
    }

    /// <summary>
    /// And the AI never builds inside itself. It places at an offset and the offset is validated, so
    /// no two structures of a team may share ground — which is the property the AI's own placement
    /// would have broken on its own, since fourteen metres from the factory that made it is inside
    /// that factory.
    /// </summary>
    [Fact]
    public void TheAiNeverRaisesAStructureInsideAnother()
    {
        SimWorld world = BareBase(1, Faction.Chinese);

        world.TeamRef(1).Materials = 200_000;
        world.TeamRef(1).Energy = 200_000;
        world.TeamRef(1).Water = 200_000;
        world.TeamRef(1).TechTier = 2;

        world.RunTicks(3_000);

        var structures = new List<(UnitKind Kind, int X, int Z)>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != 1 || !UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                continue;
            }

            int cell = world.Navigation.IndexOfWorld(entity.Position);

            structures.Add((entity.Kind, world.Navigation.CellX(cell), world.Navigation.CellZ(cell)));
        }

        Assert.True(structures.Count > 1, "the AI raised nothing, so this proves nothing about its placement");

        for (int a = 0; a < structures.Count; a++)
        {
            for (int b = a + 1; b < structures.Count; b++)
            {
                int reach = UnitCatalog.FootprintRadiusCells(structures[a].Kind)
                    + UnitCatalog.FootprintRadiusCells(structures[b].Kind);

                int dx = Math.Abs(structures[a].X - structures[b].X);
                int dz = Math.Abs(structures[a].Z - structures[b].Z);

                Assert.True(
                    dx > reach || dz > reach,
                    $"{structures[a].Kind} and {structures[b].Kind} overlap: {dx} and {dz} cells apart, "
                    + $"which {reach} cells of footprint does not allow");
            }
        }
    }
}

