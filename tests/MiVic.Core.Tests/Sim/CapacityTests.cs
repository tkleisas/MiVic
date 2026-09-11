using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The command capacity: a ceiling on how many units a side may field, granted by the structures
/// that support an army rather than by the ones that are one, and spent by every unit standing on
/// the map.
/// <para>
/// What these tests pin, in the order a player meets it: the ceiling is a sum over what a team owns
/// and it moves when a building is raised or destroyed; a unit costs places and a structure costs
/// none; a side at its ceiling is refused a unit and told how far over it is; a side at its ceiling
/// is <em>not</em> refused a structure, which is the deadlock guard; the standard match opens over
/// the ceiling for all three powers; and the AI buys the ceiling rather than sitting under it.
/// </para>
/// </summary>
public sealed class CapacityTests
{
    private const int WorldCapacity = 256;

    /// <summary>
    /// A world the computer does not play: the AI plays every team that is not the player's, and a
    /// rule about one team's own ledger should not be shoved about by an opponent deciding things
    /// while a test runs. Team 0 is the player's, so a world declaring only team 0 is a world where
    /// nothing moves but what the test moves.
    /// </summary>
    private static SimWorld Ledger(Faction faction, ulong seed)
        => new(seed, WorldCapacity, MatchRoster.Declare(new MatchTeam(0, faction, 0)));

    /// <summary>A world the computer plays team 1 in, and nobody plays anything else.</summary>
    private static SimWorld AiPlays(Faction faction, ulong seed)
        => new(seed, WorldCapacity, MatchRoster.Declare(new MatchTeam(1, faction, 0)));

    private static EntityId Spawn(
        SimWorld world,
        Faction faction,
        int team,
        UnitKind kind,
        WorldPos position,
        bool rising = false)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);

        EntityId id = world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(position),
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);

        if (rising)
        {
            ref Entity structure = ref world.GetRefBySlot(id.Slot);
            structure.ConstructionTicksTotal = 100;
            structure.ConstructionTicksRemaining = 100;
        }

        return id;
    }

    /// <summary>A rank of units, offset from a point so nothing is stacked on one cell.</summary>
    private static void SpawnRank(SimWorld world, Faction faction, int team, UnitKind kind, int count, WorldPos from)
    {
        for (int i = 0; i < count; i++)
        {
            Spawn(
                world,
                faction,
                team,
                kind,
                new WorldPos(from.X + ((i % 12) * 4_000), 0, from.Z + ((i / 12) * 4_000)));
        }
    }

    private static EntityId Find(SimWorld world, int team, UnitKind kind)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == kind)
            {
                return new EntityId(slot, entity.Generation);
            }
        }

        return EntityId.None;
    }

    private static int CountAlive(SimWorld world, int team, bool buildings)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding == buildings)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// A site the placement rule accepts, found by asking the rule about a ring of cells around a
    /// point rather than by knowing one: a test that placed a building by hand would be a test of a
    /// world the game cannot produce.
    /// </summary>
    private static WorldPos SiteNear(SimWorld world, int team, UnitKind kind, WorldPos near)
    {
        int centre = Math.Max(world.Navigation.IndexOfWorld(near), 0);
        int centreX = world.Navigation.CellX(centre);
        int centreZ = world.Navigation.CellZ(centre);

        for (int radius = 3; radius <= 20; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
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

    // ------------------------------------------------------------------ what grants a ceiling, and how much

    /// <summary>
    /// Which structures support an army and which <em>are</em> one. This is the whole of the
    /// decision the feature asked for, written as a table: a headquarters commands, a yard and a
    /// reactor support what they supply, a power plant and a bureau keep a base running — and a gun,
    /// an anti-aircraft mount and a radar, which are the army rather than the thing that supports
    /// it, support nothing at all.
    /// </summary>
    [Fact]
    public void AStructureGrantsCapacityWhenItSupportsAnArmyRatherThanBeingOne()
    {
        Assert.Equal(200, CapacitySystem.GrantOf(UnitKind.CommandCentre));
        Assert.Equal(60, CapacitySystem.GrantOf(UnitKind.Factory));
        Assert.Equal(60, CapacitySystem.GrantOf(UnitKind.NuclearPlant));
        Assert.Equal(30, CapacitySystem.GrantOf(UnitKind.PowerPlant));
        Assert.Equal(30, CapacitySystem.GrantOf(UnitKind.DesignBureau));

        Assert.Equal(0, CapacitySystem.GrantOf(UnitKind.GunEmplacement));
        Assert.Equal(0, CapacitySystem.GrantOf(UnitKind.AntiAirEmplacement));
        Assert.Equal(0, CapacitySystem.GrantOf(UnitKind.RadarStation));

        // And nothing that fights supports anything: the ceiling is about the army, not the base.
        foreach (UnitDefinition definition in UnitCatalog.All)
        {
            if (!definition.IsBuilding)
            {
                Assert.Equal(0, CapacitySystem.GrantOf(definition.Kind));
            }
        }
    }

    /// <summary>
    /// The ceiling is the sum of what a team's finished structures support, at its own faction's
    /// figure. The four buildings a skirmish starts every side with are worth 200 + 60 + 30 + 30 =
    /// 320 places, which is 272 for the Σοβιετικοί at 850 ‰, 320 for the Δυτικοί at 1 000 and 368
    /// for the Κινέζοι at 1 150 — the axis on which the three powers differ about mass production,
    /// stated as a number of places rather than as a price.
    /// </summary>
    [Fact]
    public void TheCeilingIsWhatTheStructuresSupportAtTheOwnersOwnFigure()
    {
        SimWorld match = new(seed: 7, capacity: WorldCapacity, MatchRoster.For(ScenarioKind.Skirmish));

        Assert.Equal(0, match.CommandCapacity(0));

        Spawn(match, Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        Spawn(match, Faction.Soviet, 0, UnitKind.PowerPlant, WorldPos.GroundMetres(-100, -120));
        Spawn(match, Faction.Soviet, 0, UnitKind.Factory, WorldPos.GroundMetres(-120, -100));
        Spawn(match, Faction.Soviet, 0, UnitKind.DesignBureau, WorldPos.GroundMetres(-140, -120));

        Spawn(match, Faction.Chinese, 1, UnitKind.CommandCentre, WorldPos.GroundMetres(120, -120));
        Spawn(match, Faction.Chinese, 1, UnitKind.PowerPlant, WorldPos.GroundMetres(100, -120));
        Spawn(match, Faction.Chinese, 1, UnitKind.Factory, WorldPos.GroundMetres(120, -100));
        Spawn(match, Faction.Chinese, 1, UnitKind.DesignBureau, WorldPos.GroundMetres(140, -120));

        Spawn(match, Faction.Western, 2, UnitKind.CommandCentre, WorldPos.GroundMetres(0, 180));
        Spawn(match, Faction.Western, 2, UnitKind.PowerPlant, WorldPos.GroundMetres(20, 180));
        Spawn(match, Faction.Western, 2, UnitKind.Factory, WorldPos.GroundMetres(0, 160));
        Spawn(match, Faction.Western, 2, UnitKind.DesignBureau, WorldPos.GroundMetres(-20, 180));

        Assert.Equal(272, match.CommandCapacity(0));
        Assert.Equal(368, match.CommandCapacity(1));
        Assert.Equal(320, match.CommandCapacity(2));

        // What a headquarters alone supports, and what a gun beside it adds: nothing.
        SimWorld lone = Ledger(Faction.Soviet, seed: 8);
        Spawn(lone, Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        Assert.Equal(170, lone.CommandCapacity(0));

        Spawn(lone, Faction.Soviet, 0, UnitKind.GunEmplacement, WorldPos.GroundMetres(-100, -120));
        Spawn(lone, Faction.Soviet, 0, UnitKind.RadarStation, WorldPos.GroundMetres(-120, -100));
        Assert.Equal(170, lone.CommandCapacity(0));
    }

    /// <summary>
    /// What one unit costs against the ceiling: a man is one place, an armoured vehicle four — its
    /// crew and the tail that keeps it running — and an aircraft six, because an airframe is a crew,
    /// a ground crew and a fuel bowser. Everything a team can field costs something, and no
    /// structure costs anything, which is what keeps a side that is over its ceiling able to build.
    /// </summary>
    [Fact]
    public void AUnitCostsWhatItIsAndAStructureCostsNothing()
    {
        Assert.Equal(1, UnitCatalog.SupplyCost(UnitKind.Infantry));
        Assert.Equal(1, UnitCatalog.SupplyCost(UnitKind.Commissar));
        Assert.Equal(1, UnitCatalog.SupplyCost(UnitKind.RobotInfantry));
        Assert.Equal(1, UnitCatalog.SupplyCost(UnitKind.Mercenary));
        Assert.Equal(2, UnitCatalog.SupplyCost(UnitKind.StealthRecon));
        Assert.Equal(2, UnitCatalog.SupplyCost(UnitKind.Harvester));
        Assert.Equal(3, UnitCatalog.SupplyCost(UnitKind.Drone));
        Assert.Equal(4, UnitCatalog.SupplyCost(UnitKind.Tank));
        Assert.Equal(4, UnitCatalog.SupplyCost(UnitKind.Artillery));
        Assert.Equal(4, UnitCatalog.SupplyCost(UnitKind.AntiAir));
        Assert.Equal(4, UnitCatalog.SupplyCost(UnitKind.RocketArtillery));
        Assert.Equal(6, UnitCatalog.SupplyCost(UnitKind.Aircraft));
        Assert.Equal(6, UnitCatalog.SupplyCost(UnitKind.ElectroPrototype));

        foreach (UnitDefinition definition in UnitCatalog.All)
        {
            if (definition.IsBuilding)
            {
                Assert.Equal(0, UnitCatalog.SupplyCost(definition.Kind));
            }
            else
            {
                Assert.True(
                    UnitCatalog.SupplyCost(definition.Kind) > 0,
                    $"{definition.Kind} costs no places at all, so a team could field it for ever");
            }
        }
    }

    /// <summary>
    /// The ceiling moves with the buildings rather than being a number kept somewhere: raising one
    /// raises it, a building site does not count until it is up, and losing one takes its places
    /// away again on the tick it is destroyed.
    /// </summary>
    [Fact]
    public void TheCeilingFollowsTheBuildings()
    {
        SimWorld world = Ledger(Faction.Western, seed: 11);

        Spawn(world, Faction.Western, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        Assert.Equal(200, world.CommandCapacity(0));

        EntityId factory = Spawn(world, Faction.Western, 0, UnitKind.Factory, WorldPos.GroundMetres(-100, -120));
        Assert.Equal(260, world.CommandCapacity(0));

        // A structure still rising supports nobody: it is a hole in the ground.
        EntityId rising = Spawn(world, Faction.Western, 0, UnitKind.Factory, WorldPos.GroundMetres(-120, -100), rising: true);
        Assert.Equal(260, world.CommandCapacity(0));

        // The same building, finished, is sixty places.
        world.GetRefBySlot(rising.Slot).ConstructionTicksRemaining = 0;
        Assert.Equal(320, world.CommandCapacity(0));

        Assert.True(world.Despawn(factory));
        Assert.Equal(260, world.CommandCapacity(0));
    }

    /// <summary>
    /// The supply side of the ledger: every unit a team owns, priced by its role, and nothing else.
    /// A hull on a factory's pad is not a unit on the map, so the queue is not counted — the ceiling
    /// is about what has been fielded, which is the number a player can see.
    /// </summary>
    [Fact]
    public void SupplyIsWhatIsFieldedRatherThanWhatIsOrdered()
    {
        SimWorld world = Ledger(Faction.Western, seed: 12);

        Spawn(world, Faction.Western, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        EntityId factory = Spawn(world, Faction.Western, 0, UnitKind.Factory, WorldPos.GroundMetres(-100, -120));

        Assert.Equal(0, world.ArmySupply(0));

        Spawn(world, Faction.Western, 0, UnitKind.Tank, WorldPos.GroundMetres(-140, -100));
        Spawn(world, Faction.Western, 0, UnitKind.Infantry, WorldPos.GroundMetres(-144, -100));
        Assert.Equal(5, world.ArmySupply(0));

        ref TeamState team = ref world.TeamRef(0);
        team.Materials = 50_000;
        team.Energy = 50_000;
        team.Water = 50_000;
        team.TechTier = 2;

        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(1, world.GetRefBySlot(factory.Slot).QueueLength);
        Assert.Equal(5, world.ArmySupply(0));

        // And losing a unit gives its places back.
        EntityId tank = Find(world, 0, UnitKind.Tank);
        Assert.True(world.Despawn(tank));
        Assert.Equal(1, world.ArmySupply(0));
    }

    // ------------------------------------------------------------------ the standard match

    /// <summary>
    /// <b>The arithmetic the whole decision rests on: a standard match opens over every side's
    /// ceiling.</b> A side starts with 166 units, and the formation the scenario lays out is 82
    /// infantry at one place, 42 tanks at four, 14 artillery at four, 14 anti-aircraft mounts at
    /// four and 14 aircraft at six — 82 + 168 + 56 + 56 + 84 = 446 places against the 272, 320 and
    /// 368 that four buildings support. So the mobilisation every side opens with is one its
    /// industry cannot sustain, production is refused from the first tick rather than after a
    /// balance change nobody asked for, and the margin is widest on the Σοβιετικοί — the faction the
    /// standard match puts the player on.
    /// </summary>
    [Fact]
    public void TheStandardMatchOpensOverEverySidesCeiling()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 1024, MatchRoster.For(ScenarioKind.Skirmish));
        Scenario.Build(world, ScenarioKind.Skirmish);

        Assert.Equal(Scenario.UnitsPerFaction, CountAlive(world, 0, buildings: false));
        Assert.Equal(446, world.ArmySupply(0));
        Assert.Equal(446, world.ArmySupply(1));
        Assert.Equal(446, world.ArmySupply(2));

        Assert.Equal(272, world.CommandCapacity(0));
        Assert.Equal(368, world.CommandCapacity(1));
        Assert.Equal(320, world.CommandCapacity(2));

        Assert.Equal(174, world.OverCapacity(0));
        Assert.Equal(78, world.OverCapacity(1));
        Assert.Equal(126, world.OverCapacity(2));

        // The order a player gives first is refused, in the words the panel shows...
        EntityId headquarters = Find(world, 0, UnitKind.CommandCentre);
        Assert.False(world.CanProduce(headquarters, UnitKind.Infantry, out string refused));
        Assert.Equal("λείπει δυναμικότητα 174", refused);

        // ...and a structure is not, which is the other half of the rule — see the guard below.
        Assert.True(world.CanBuildStructure(0, UnitKind.Factory, out string allowed), allowed);
    }

    /// <summary>
    /// A side over its ceiling is refused a unit, and the refusal names the rule and says how far
    /// over the side is. It joins the refusals that already exist — <c>λείπουν 120 Π</c> for a
    /// resource, <c>λείπει ισχύς 3 Ε</c> for a brown-out — in the same voice and from one place, so
    /// the panel, the probe and the order itself cannot say three different things.
    /// </summary>
    [Fact]
    public void ASideOverItsCeilingIsRefusedAUnitAndToldHowFarOverItIs()
    {
        SimWorld world = Ledger(Faction.Western, seed: 13);

        Spawn(world, Faction.Western, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        EntityId factory = Spawn(world, Faction.Western, 0, UnitKind.Factory, WorldPos.GroundMetres(-100, -120));

        ref TeamState team = ref world.TeamRef(0);
        team.Materials = 50_000;
        team.Energy = 50_000;
        team.Water = 50_000;
        team.TechTier = 2;

        // The ceiling is 260 places, so the sixty-sixth tank is the one that does not fit.
        SpawnRank(world, Faction.Western, 0, UnitKind.Tank, 65, WorldPos.GroundMetres(-260, -260));

        Assert.Equal(260, world.ArmySupply(0));
        Assert.Equal(0, world.OverCapacity(0));

        // Exactly at the ceiling is legal: the ceiling is how many a side may field.
        Assert.True(world.CanProduce(factory, UnitKind.Tank, out string room), room);

        SpawnRank(world, Faction.Western, 0, UnitKind.Tank, 1, WorldPos.GroundMetres(200, 200));

        Assert.Equal(264, world.ArmySupply(0));
        Assert.Equal(4, world.OverCapacity(0));

        Assert.False(world.CanProduce(factory, UnitKind.Tank, out string refused));
        Assert.Equal("λείπει δυναμικότητα 4", refused);

        // And the order is thrown away rather than queued: the pad stays empty.
        world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.Tank, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(0, world.GetRefBySlot(factory.Slot).QueueLength);

        // The treasury is not what refused it: this team has fifty thousand materials.
        Assert.True(world.Team(0).Materials > 1_000);
    }

    /// <summary>
    /// <b>The deadlock guard.</b> A side over its ceiling that could not raise a building would be
    /// stuck for ever: the only thing that lifts a ceiling is a structure, and a side that is over
    /// its ceiling and losing nothing is exactly the side with no other way out — a stalemate is
    /// when nothing dies. So the ceiling is the one clause of the production rule that asks whether
    /// the role is a building first, and answers that the question does not apply.
    /// <para>
    /// This test is that side: over its ceiling, refused a rifleman, and then it raises a yard
    /// through the order a player's click sends, the yard finishes, and it is a hundred and twenty
    /// places better off while still over the ceiling it started under. The order that carries it
    /// out is accepted twice over — through the placement rule and through the production queue,
    /// which is the path the AI takes.
    /// </para>
    /// </summary>
    [Fact]
    public void ACappedTeamCanStillRaiseItsCeiling()
    {
        SimWorld world = Ledger(Faction.Western, seed: 14);

        EntityId headquarters = Spawn(world, Faction.Western, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));

        ref TeamState team = ref world.TeamRef(0);
        team.Materials = 50_000;
        team.Energy = 50_000;
        team.Water = 50_000;

        SpawnRank(world, Faction.Western, 0, UnitKind.Tank, 70, WorldPos.GroundMetres(-260, -260));

        Assert.Equal(200, world.CommandCapacity(0));
        Assert.Equal(280, world.ArmySupply(0));
        Assert.Equal(80, world.OverCapacity(0));

        // The unit is refused...
        Assert.False(world.CanProduce(headquarters, UnitKind.Infantry, out string refused));
        Assert.Equal("λείπει δυναμικότητα 80", refused);

        // ...and the building that lifts the ceiling is not, by the question the panel asks...
        Assert.True(world.CanBuildStructure(0, UnitKind.Factory, out string allowed), allowed);

        // ...nor by the one the order is judged by.
        WorldPos site = SiteNear(world, 0, UnitKind.Factory, world.GetRefBySlot(headquarters.Slot).Position);
        Assert.True(world.TryPlanStructure(0, UnitKind.Factory, site, out WorldPos planned, out string plan), plan);

        world.Enqueue(SimCommand.Structure(UnitKind.Factory, planned, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(2, CountAlive(world, 0, buildings: true));
        Assert.Equal(80, world.OverCapacity(0));

        // It rises, and the ceiling rises with it while the team is still over the old one.
        world.RunTicks(UnitCatalog.BuildTicks(Faction.Western, UnitKind.Factory) + 200);

        Assert.Equal(260, world.CommandCapacity(0));
        Assert.Equal(20, world.OverCapacity(0));
        Assert.True(world.OverCapacity(0) > 0, "the team is no longer over its ceiling, so this proves nothing");

        // And the ceiling can be bought again while it is still over the old one: the same route
        // through the production queue, which is the path the AI takes and the reason the gate has
        // to let a structure through.
        world.Enqueue(SimCommand.QueueUnit(headquarters, UnitKind.Factory, world.Tick + 1, 0));
        world.Step();

        Assert.Equal(1, world.GetRefBySlot(headquarters.Slot).QueueLength);
    }

    /// <summary>
    /// A prototype run delivers a real unit — see <see cref="PrototypeSystem"/> — so the ceiling
    /// refuses it as it refuses a factory queue, and it refuses it at the order rather than at the
    /// delivery: the materials are charged when a run starts, and a run that was paid for and could
    /// not deliver would be a unit bought and thrown away.
    /// </summary>
    [Fact]
    public void APrototypeRunIsRefusedWhileTheTeamIsOverItsCeiling()
    {
        SimWorld world = Ledger(Faction.Soviet, seed: 15);

        Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        EntityId bureau = Spawn(world, Faction.Soviet, 0, UnitKind.DesignBureau, WorldPos.GroundMetres(-100, -120));
        EntityId factory = Spawn(world, Faction.Soviet, 0, UnitKind.Factory, WorldPos.GroundMetres(-120, -100));

        ref TeamState team = ref world.TeamRef(0);
        team.Materials = 50_000;
        team.Energy = 50_000;
        team.Water = 50_000;
        team.TechTier = 3;
        team.TechMask = 1UL << (int)TechId.SovietElectro;

        SpawnRank(world, Faction.Soviet, 0, UnitKind.Tank, 65, WorldPos.GroundMetres(-260, -260));

        Assert.Equal(260, world.ArmySupply(0));
        Assert.Equal(246, world.CommandCapacity(0));
        Assert.Equal(14, world.OverCapacity(0));

        int materialsBefore = world.Team(0).Materials;

        world.Enqueue(SimCommand.ApproveDesign(bureau, UnitKind.ElectroPrototype, world.Tick + 1, 0));
        world.Step();

        Assert.False(world.Team(0).IsPrototyping, "a prototype run was started by a team that is over its ceiling");
        Assert.True(
            world.Team(0).Materials >= materialsBefore,
            "the refused run was charged for anyway: the team paid for a prototype it does not get");

        // And with the design proven, the factory is refused the same hull in the same words.
        team.ApprovedMask |= 1u << (int)UnitKind.ElectroPrototype;

        Assert.False(world.CanProduce(factory, UnitKind.ElectroPrototype, out string refused));
        Assert.Equal("λείπει δυναμικότητα 14", refused);
    }

    /// <summary>
    /// The ceiling is not <see cref="UnitDefinition.MaxAlive"/>, and a reader who meets one must not
    /// read the other. The prototype cap is about how many of one <em>role</em> a team may ever have
    /// — a capability rather than a unit type — and it refuses with <c>όριο 2</c>. The ceiling is
    /// about the whole army and refuses in its own words. Each clause is asked on its own terms, and
    /// each names itself.
    /// </summary>
    [Fact]
    public void TheCeilingIsNotThePrototypeCap()
    {
        SimWorld world = Ledger(Faction.Soviet, seed: 16);

        Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        EntityId factory = Spawn(world, Faction.Soviet, 0, UnitKind.Factory, WorldPos.GroundMetres(-120, -100));

        ref TeamState team = ref world.TeamRef(0);
        team.Materials = 50_000;
        team.Energy = 50_000;
        team.Water = 50_000;
        team.TechTier = 3;
        team.TechMask = 1UL << (int)TechId.SovietElectro;
        team.ApprovedMask |= 1u << (int)UnitKind.ElectroPrototype;

        // Inside its ceiling — a headquarters and a yard support 221 places and this team has
        // nothing on the map — the only thing that stops the third prototype is its own cap.
        UnitDefinition definition = UnitCatalog.Get(UnitKind.ElectroPrototype);

        for (int i = 0; i < definition.MaxAlive; i++)
        {
            world.Enqueue(SimCommand.QueueUnit(factory, UnitKind.ElectroPrototype, world.Tick + 1, 0));
            world.Step();
        }

        Assert.Equal(0, world.OverCapacity(0));
        Assert.Equal(definition.MaxAlive, world.CountOf(0, UnitKind.ElectroPrototype));
        Assert.False(world.CanProduce(factory, UnitKind.ElectroPrototype, out string capped));
        Assert.Equal($"όριο {definition.MaxAlive}", capped);
    }

    /// <summary>
    /// Where the ceiling sits among the clauses a player meets: after the design, before the purse.
    /// A team that is over its ceiling <em>and</em> cannot pay is told about the ceiling, because
    /// that is the one it has to act on first — there is no point saving up for a unit that may not
    /// be fielded. Within the ceiling, the empty treasury is what refuses it, in its own words.
    /// </summary>
    [Fact]
    public void TheCeilingIsNamedBeforeTheTreasury()
    {
        SimWorld world = Ledger(Faction.Western, seed: 17);

        Spawn(world, Faction.Western, 0, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120));
        EntityId factory = Spawn(world, Faction.Western, 0, UnitKind.Factory, WorldPos.GroundMetres(-100, -120));

        ref TeamState team = ref world.TeamRef(0);
        team.TechTier = 2;
        team.Materials = 0;

        SpawnRank(world, Faction.Western, 0, UnitKind.Tank, 70, WorldPos.GroundMetres(-260, -260));

        Assert.True(world.OverCapacity(0) > 0);
        Assert.False(world.CanProduce(factory, UnitKind.Tank, out string refused));
        Assert.Equal($"λείπει δυναμικότητα {world.OverCapacity(0)}", refused);

        for (int i = 0; i < 10; i++)
        {
            Assert.True(world.Despawn(Find(world, 0, UnitKind.Tank)));
        }

        Assert.Equal(0, world.OverCapacity(0));
        Assert.False(world.CanProduce(factory, UnitKind.Tank, out string poor));
        Assert.StartsWith("λείπουν ", poor);
        Assert.EndsWith(" Π", poor);
    }

    // ------------------------------------------------------------------ the AI

    /// <summary>
    /// <b>The failure mode: an AI that is capped and stalls.</b> This team is in the one state that
    /// puts the two rules in contact — its ceiling is smaller than the army it wants — for a reason
    /// worth stating: the gate can only refuse an order the AI actually wants to give, the AI stops
    /// producing at eighteen combat units, and eighteen of the heaviest thing it can buy is a
    /// hundred and eight places, while the smallest ceiling a finished headquarters grants is a
    /// hundred and seventy. So here its headquarters is still rising, its one yard supports sixty
    /// places, and it is fielding sixteen tanks — sixty-four — and wants more.
    /// <para>
    /// What it must do is buy the ceiling. It cannot produce a unit, so a decision loop that only
    /// ever tried to produce would sit there for the rest of the match doing nothing at all; what it
    /// does instead is raise the generation and the yards that lift the ceiling, and then produce
    /// again the moment its army fits.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiRaisesItsCeilingRatherThanStallingUnderIt()
    {
        SimWorld world = AiPlays(Faction.Western, seed: 18);

        EntityId headquarters = Spawn(
            world, Faction.Western, 1, UnitKind.CommandCentre, WorldPos.GroundMetres(-120, -120), rising: true);

        Spawn(world, Faction.Western, 1, UnitKind.Factory, WorldPos.GroundMetres(-100, -120));

        ref TeamState team = ref world.TeamRef(1);
        team.Materials = 40_000;
        team.Energy = 5_000;
        team.Water = 40_000;
        team.TechTier = 2;

        SpawnRank(world, Faction.Western, 1, UnitKind.Tank, 16, WorldPos.GroundMetres(-260, -260));

        // Over its ceiling, wanting to produce, and refused.
        Assert.Equal(64, world.ArmySupply(1));
        Assert.Equal(60, world.CommandCapacity(1));
        Assert.Equal(4, world.OverCapacity(1));
        Assert.False(world.CanProduce(headquarters, UnitKind.Infantry, out string refused));
        Assert.Equal("λείπει δυναμικότητα 4", refused);

        int unitsBefore = CountAlive(world, 1, buildings: false);

        world.RunTicks(600);

        // It is not sitting under its limit: something it decided to raise has lifted the ceiling.
        Assert.True(
            world.CommandCapacity(1) > 60,
            $"the AI stayed at its ceiling: {world.CommandCapacity(1)} places at tick {world.Tick}");

        // And it is not producing nothing: once the ceiling covered the army it wanted, it produced.
        Assert.True(
            CountAlive(world, 1, buildings: false) > unitsBefore,
            "the AI never produced a unit after raising its ceiling");
    }

    /// <summary>
    /// The same behaviour in a real match rather than a constructed one. A standard skirmish opens
    /// with every side over its ceiling, so the AI's first decisions are made under one, and the
    /// answer to that is capacity: within a minute the Κινέζοι — the solvent half of the alliance,
    /// and the AI team the line probe reads — support more places than the four buildings they
    /// started with, with no script ordering anything.
    /// </summary>
    [Fact]
    public void TheAiRaisesItsCeilingInAStandardMatch()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 1024, MatchRoster.For(ScenarioKind.Skirmish));
        Scenario.Build(world, ScenarioKind.Skirmish);

        Assert.Equal(446, world.ArmySupply(1));
        Assert.Equal(368, world.CommandCapacity(1));

        world.RunTicks(600);

        Assert.True(
            world.CommandCapacity(1) > 368,
            $"the AI never raised its ceiling: {world.CommandCapacity(1)} places at tick {world.Tick}");

        // Built rather than lost: the ceiling rose because there is more standing than the four
        // buildings the scenario laid out.
        int structures = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).TeamId == 1 &&
                UnitCatalog.Get(world.GetRefBySlot(slot).Kind).IsBuilding)
            {
                structures++;
            }
        }

        Assert.True(structures > 4, $"the AI still has only its four starting buildings ({structures})");
    }

    /// <summary>
    /// <b>The trap the ceiling sets for a base, which is a power problem and not a capacity one.</b>
    /// A yard is four energy a tick for as long as it stands, and a team running a deficit builds
    /// nothing at all: production halts when a negative rate empties the stockpile, and the power
    /// plant queued to fix it is then sitting in the queue the deficit has just stopped. So an AI
    /// that lifted its ceiling with load would be an AI that could freeze itself — and the way in is
    /// losing a plant it already had, which is one raid away at any moment.
    /// <para>
    /// This watches a real match rather than a constructed one, every tick, because the failure is a
    /// countdown: a deficit is survivable until the stockpile runs out, so a test that looked only at
    /// the end of the run would see a base that is merely poor for four hundred ticks and then dead
    /// for ever. What it pins is the rule the AI buys generation by — a yard only when the rate would
    /// still be sound with a power plant shot away.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiNeverBuysACeilingItsOwnGridCannotCarry()
    {
        SimWorld world = new(seed: 20250101UL, capacity: 1024, MatchRoster.For(ScenarioKind.Skirmish));
        Scenario.Build(world, ScenarioKind.Skirmish);

        for (int tick = 0; tick < 1_200; tick++)
        {
            world.Step();

            int rate = world.Team(1).EnergyPerTick;

            Assert.True(
                rate >= 0,
                $"team 1 is running {rate} energy a tick at tick {world.Tick}: every queue it has " +
                "stops the moment the stockpile empties, including the plant queued to fix it");
        }

        // And it spent the time building rather than sitting still: the ceiling it opened with is
        // not the ceiling it has.
        Assert.True(
            world.CommandCapacity(1) > 368,
            $"the AI never raised its ceiling: {world.CommandCapacity(1)} places at tick {world.Tick}");
    }

    // ------------------------------------------------------------------ determinism

    /// <summary>
    /// Same seed, same refusals, same state. The ceiling is derived from what a team owns at the
    /// moment it is asked, so a replay reaches the same answer on the same tick — and because
    /// nothing about it is stored, there is nothing new in the state hash for two peers to disagree
    /// about.
    /// </summary>
    [Fact]
    public void TheSameSeedGivesTheSameRefusalsAndTheSameHash()
    {
        static (ulong Hash, string UnitReason, string StructureReason, int Supply, int Ceiling) Run()
        {
            SimWorld world = new(seed: 20250101UL, capacity: 1024, MatchRoster.For(ScenarioKind.Skirmish));
            Scenario.Build(world, ScenarioKind.Skirmish);

            EntityId headquarters = Find(world, 0, UnitKind.CommandCentre);
            WorldPos site = SiteNear(world, 0, UnitKind.Factory, world.GetRefBySlot(headquarters.Slot).Position);

            world.CanProduce(headquarters, UnitKind.Infantry, out string unitReason);
            world.CanBuildStructure(0, UnitKind.Factory, out string structureReason);

            world.Enqueue(SimCommand.QueueUnit(headquarters, UnitKind.Infantry, world.Tick + 1, 0));
            world.Enqueue(SimCommand.Structure(UnitKind.Factory, site, world.Tick + 1, 0));
            world.RunTicks(120);

            return (
                StateHash.Compute(world),
                unitReason,
                structureReason,
                world.ArmySupply(0),
                world.CommandCapacity(0));
        }

        (ulong first, string firstUnit, string firstStructure, int firstSupply, int firstCeiling) = Run();
        (ulong second, string secondUnit, string secondStructure, int secondSupply, int secondCeiling) = Run();

        Assert.Equal(first, second);
        Assert.Equal(firstUnit, secondUnit);
        Assert.Equal(firstStructure, secondStructure);
        Assert.Equal(firstSupply, secondSupply);
        Assert.Equal(firstCeiling, secondCeiling);

        // The unit was refused and the structure was not, on both runs and in those words. The
        // ceiling is the four starting buildings' worth on both runs too: the yard that was ordered
        // is still rising at the end of two hundred ticks, and a site supports nobody.
        Assert.Equal("λείπει δυναμικότητα 174", firstUnit);
        Assert.Equal(string.Empty, firstStructure);
        Assert.Equal(272, firstCeiling);
    }
}
