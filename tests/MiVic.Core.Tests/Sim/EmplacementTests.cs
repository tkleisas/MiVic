using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The first structures in the game that shoot.
/// <para>
/// A defensive structure that has to be told to fire is a structure that never fires, because
/// the player has no reason to think of it: it is scenery until the tick it opens up on its
/// own. So these are the tests of a gun that picks its own target — what it will and will not
/// shoot, which one it picks when two are equally good, that it does it without a command, and
/// that what it is standing on still decides how hard it is to kill.
/// </para>
/// </summary>
public sealed class EmplacementTests
{
    private const ulong Seed = 20250101;

    private static SimWorld World() => new(Seed, capacity: 64);

    /// <summary>
    /// Places a unit at an exact point, whatever the ground there is.
    /// <para>
    /// The position is written after the spawn rather than handed to it: a mobile unit that
    /// would appear on impassable ground is moved to the nearest cell that will hold it, which
    /// is right for a game and wrong for a measurement — a tank nudged by a cell would move the
    /// distance this file is measuring. Height is zero for everything here, so that two enemies
    /// placed symmetrically really are the same distance away: <c>DistanceSquaredTo</c> counts
    /// altitude.
    /// </para>
    /// </summary>
    private static EntityId Place(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        EntityId id = world.Spawn(
            faction,
            team,
            kind,
            new WorldPos(x, 0, z),
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);

        ref Entity entity = ref world.GetRefBySlot(id.Slot);
        entity.Position = new WorldPos(x, 0, z);
        entity.MoveGoal = entity.Position;
        return id;
    }

    private static int HealthOf(SimWorld world, EntityId id)
        => world.TryGet(id, out Entity entity) ? entity.Health : 0;

    /// <summary>
    /// A gun emplacement shoots a tank that walks into its reach, and it does it with no order
    /// from anybody.
    /// <para>
    /// The enemy is 150 m away: inside the emplacement's 200 m and outside the 110 m a tank can
    /// shoot back from, which is the whole point of a fixed gun — reach a hull does not have.
    /// <c>HasAttackOrder</c> is asserted false at the moment it fires, because "nobody told it
    /// to" is the feature rather than a detail of it.
    /// </para>
    /// </summary>
    [Fact]
    public void AGunEmplacementEngagesAGroundTargetItWasNeverOrderedToAttack()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, 0);
        EntityId tank = Place(world, Faction.Western, 2, UnitKind.Tank, 150_000, 0);

        WorldPos stood = world.GetRefBySlot(emplacement.Slot).Position;

        Assert.Equal(-1, world.GetRefBySlot(emplacement.Slot).TargetSlot);

        world.RunTicks(2);

        ref Entity gun = ref world.GetRefBySlot(emplacement.Slot);

        Assert.Equal(tank.Slot, gun.TargetSlot);
        Assert.False(gun.HasAttackOrder, "the emplacement was given an order, so this proves nothing about firing unaided");

        world.RunTicks(60);

        int lost = UnitCatalog.Get(UnitKind.Tank).Health - HealthOf(world, tank);

        Assert.True(lost > 0, $"a tank 150 m from a gun emplacement took no damage at all in three seconds");

        // It shoots and nothing else: a structure has no route to walk and must never be handed
        // one, or every ordered emplacement would burn a path search for a journey it cannot make.
        // The height is the terrain's — a building is snapped to the ground it stands on — so what
        // "it did not move" means here is that it is still on the same spot of ground.
        Assert.False(gun.HasMoveGoal);
        Assert.Equal(stood.X, gun.Position.X);
        Assert.Equal(stood.Z, gun.Position.Z);
    }

    /// <summary>
    /// A gun emplacement does not shoot what it cannot reach, and does not drive after it.
    /// </summary>
    [Fact]
    public void AGunEmplacementIgnoresATargetOutOfItsRange()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, 0);
        EntityId tank = Place(world, Faction.Western, 2, UnitKind.Tank, 250_000, 0);

        world.RunTicks(60);

        ref Entity gun = ref world.GetRefBySlot(emplacement.Slot);

        Assert.Equal(-1, gun.TargetSlot);
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, HealthOf(world, tank));
        Assert.False(gun.HasMoveGoal, "an auto-acquired target dragged a building across the map");
    }

    /// <summary>
    /// A gun cannot be pointed at an aeroplane, and says so by shooting the ground instead: with
    /// a tank and an aircraft both inside its 200 m, it takes the tank.
    /// <para>
    /// The aircraft is nearer on purpose. A gun that picked its target by distance alone and then
    /// discovered it could not hit it would leave the tank standing while it tracked a plane it
    /// can never touch, which is the shape this test is written to catch.
    /// </para>
    /// </summary>
    [Fact]
    public void AGunEmplacementIgnoresAircraftEvenWhenTheyAreNearer()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, 0);
        EntityId aircraft = Place(world, Faction.Western, 2, UnitKind.Aircraft, 80_000, 0);
        EntityId infantry = Place(world, Faction.Western, 2, UnitKind.Infantry, 150_000, 0);

        world.RunTicks(60);

        ref Entity gun = ref world.GetRefBySlot(emplacement.Slot);

        Assert.Equal(infantry.Slot, gun.TargetSlot);
        Assert.True(HealthOf(world, infantry) < UnitCatalog.Get(UnitKind.Infantry).Health, "the gun never engaged the infantryman");
        Assert.Equal(UnitCatalog.Get(UnitKind.Aircraft).Health, HealthOf(world, aircraft));
    }

    /// <summary>
    /// An anti-aircraft emplacement engages aircraft and refuses the ground, which is the other
    /// half of the same rule and the half a specialist weapon is actually for.
    /// </summary>
    [Fact]
    public void AnAntiAirEmplacementEngagesAircraftAndIgnoresGround()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.AntiAirEmplacement, 0, 0);
        EntityId aircraft = Place(world, Faction.Western, 2, UnitKind.Aircraft, 150_000, 0);
        EntityId tank = Place(world, Faction.Western, 2, UnitKind.Tank, 170_000, 0);

        world.RunTicks(60);

        ref Entity gun = ref world.GetRefBySlot(emplacement.Slot);

        Assert.Equal(aircraft.Slot, gun.TargetSlot);
        Assert.True(
            HealthOf(world, aircraft) < UnitCatalog.Get(UnitKind.Aircraft).Health,
            "the anti-aircraft emplacement never engaged the aircraft");

        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, HealthOf(world, tank));
    }

    /// <summary>
    /// With nothing in the air, an anti-aircraft emplacement is a building: it does not fire at
    /// the tank parked inside its range, and it does not acquire it either.
    /// </summary>
    [Fact]
    public void AnAntiAirEmplacementWithNoAircraftPresentFiresAtNothing()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.AntiAirEmplacement, 0, 0);
        EntityId tank = Place(world, Faction.Western, 2, UnitKind.Tank, 100_000, 0);

        world.RunTicks(120);

        Assert.Equal(-1, world.GetRefBySlot(emplacement.Slot).TargetSlot);
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, HealthOf(world, tank));
    }

    /// <summary>
    /// Two enemies at exactly the same distance: the lower slot is engaged.
    /// <para>
    /// The pair is placed so that the answer cannot come out right by luck. The slot order and the
    /// order the spatial index visits cells in are opposite here — the first enemy spawned is the
    /// lower slot and sits in the later cell — so a scan that took whichever it met first would
    /// pick the other one, and so would a rule that preferred the higher slot. This is the
    /// tie-break stated in the code, and it is the only thing this test can be measuring.
    /// </para>
    /// <para>
    /// The tie is written in by hand for the tick that decides it, because an exact tie is exactly
    /// what a generated height field is least likely to hand over: the two enemies are put on one
    /// level line for the one tick the acquisition happens in, and every other tick of the run is
    /// the map's own ground. <c>DistanceSquaredTo</c> counts altitude, so "the same distance" means
    /// the same height as well.
    /// </para>
    /// </summary>
    [Fact]
    public void AnExactTieIsBrokenByTheLowestSlot()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, 0);

        EntityId lowerSlot = Place(world, Faction.Western, 2, UnitKind.Tank, 40_000, 0);
        EntityId higherSlot = Place(world, Faction.Western, 2, UnitKind.Tank, -40_000, 0);

        Assert.True(lowerSlot.Slot < higherSlot.Slot, "the spawn order did not produce the slots this test is built on");

        // A tick first, so nothing is anywhere the map did not put it.
        world.Step();

        // Then the level line, and no target and no reload left over from the tick before: the
        // next acquisition is the one being asked about.
        int ground = world.Terrain.SampleHeightMm(0, 0);

        ref Entity gun = ref world.GetRefBySlot(emplacement.Slot);
        gun.Position = new WorldPos(0, ground, 0);
        gun.TargetSlot = -1;
        gun.AttackCooldown = 0;

        world.GetRefBySlot(lowerSlot.Slot).Position = new WorldPos(40_000, ground, 0);
        world.GetRefBySlot(higherSlot.Slot).Position = new WorldPos(-40_000, ground, 0);

        long toLower = gun.Position.DistanceSquaredTo(world.GetRefBySlot(lowerSlot.Slot).Position);
        long toHigher = gun.Position.DistanceSquaredTo(world.GetRefBySlot(higherSlot.Slot).Position);

        Assert.Equal(toHigher, toLower);

        world.Step();

        Assert.Equal(lowerSlot.Slot, world.GetRefBySlot(emplacement.Slot).TargetSlot);
    }

    /// <summary>
    /// The same emplacement, the same enemies, a different seed: the same target.
    /// <para>
    /// Acquisition may read the world and nothing else — no seed, no spawn history, no clock — so
    /// the world is rebuilt from three different seeds with the same line-up in it, and the answer
    /// has to be the same every time. The world's random source is checked as well, because
    /// "deterministic" and "draws no randomness" are two different promises and this is the one
    /// that keeps a replay identical.
    /// </para>
    /// </summary>
    [Fact]
    public void TheTargetChosenDependsOnTheWorldAndNotOnTheSeed()
    {
        static (int Chosen, int Near, ulong Rng) Engage(ulong seed)
        {
            SimWorld world = new(seed, capacity: 64);

            EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, 0);
            EntityId near = Place(world, Faction.Western, 2, UnitKind.Tank, 60_000, 0);

            Place(world, Faction.Western, 2, UnitKind.Tank, -120_000, 0);

            ulong rng = world.Rng.State;
            world.RunTicks(20);

            return (world.GetRefBySlot(emplacement.Slot).TargetSlot, near.Slot, rng);
        }

        (int first, int near, ulong rng) = Engage(Seed);
        (int again, _, ulong rngAgain) = Engage(Seed);
        (int other, _, _) = Engage(7);
        (int third, _, _) = Engage(999_001);

        Assert.Equal(near, first);
        Assert.Equal(first, again);
        Assert.Equal(first, other);
        Assert.Equal(first, third);

        // Nothing in the engagement consumed the world's own randomness, which is what lets two
        // machines agree about it without agreeing about anything else.
        Assert.Equal(rngAgain, rng);
    }

    /// <summary>
    /// A gun emplacement that is still being raised does not shoot.
    /// <para>
    /// A structure under construction is a building site: it produces nothing, researches nothing
    /// and is not counted by anything that asks for a structure. A gun on it that fired would be
    /// the one exception to that, and a player watching a half-poured foundation snipe a passing
    /// tank would read it as a bug rather than as a rule. The same emplacement, once up, fires.
    /// </para>
    /// </summary>
    [Fact]
    public void AnEmplacementThatIsStillBeingRaisedDoesNotShoot()
    {
        SimWorld world = World();
        EntityId emplacement = Place(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, 0);
        EntityId tank = Place(world, Faction.Western, 2, UnitKind.Tank, 150_000, 0);

        ref Entity gun = ref world.GetRefBySlot(emplacement.Slot);
        gun.ConstructionTicksTotal = 100;
        gun.ConstructionTicksRemaining = 100;

        world.RunTicks(60);

        Assert.False(world.IsComplete(emplacement.Slot));
        Assert.Equal(-1, world.GetRefBySlot(emplacement.Slot).TargetSlot);
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, HealthOf(world, tank));

        // Finished, and it opens fire on its own on the next tick it is asked about.
        world.GetRefBySlot(emplacement.Slot).ConstructionTicksRemaining = 0;
        world.RunTicks(2);

        Assert.Equal(tank.Slot, world.GetRefBySlot(emplacement.Slot).TargetSlot);
    }

    /// <summary>
    /// The roster facts the acquisition rule is built on, asserted where they are written rather
    /// than inferred from a battle: both are structures, both carry a gun, each can reach exactly
    /// one of the two kinds of target, and they are priced and timed apart.
    /// </summary>
    [Fact]
    public void TheTwoEmplacementsAreStructuresThatCoverTheTwoHalvesOfTheAir()
    {
        UnitDefinition gun = UnitCatalog.Get(UnitKind.GunEmplacement);
        UnitDefinition antiAir = UnitCatalog.Get(UnitKind.AntiAirEmplacement);

        Assert.True(gun.IsBuilding && antiAir.IsBuilding);
        Assert.True(gun.IsArmed && antiAir.IsArmed);

        // The gun is the generalist and the anti-aircraft emplacement is the specialist: one
        // cannot touch the air and the other cannot touch the ground, and between them they cover
        // everything that moves.
        Assert.False(gun.CanHitAir);
        Assert.True(gun.CanHitGround);
        Assert.True(antiAir.CanHitAir);
        Assert.False(antiAir.CanHitGround);

        // What the extra research buys: more reach, a faster reload and a heavier shell than the
        // gun gets, in exchange for a gun that can only shoot at one thing.
        Assert.True(antiAir.AttackCooldownTicks < gun.AttackCooldownTicks);
        Assert.True(antiAir.RequiredTechTier > gun.RequiredTechTier, "the emplacement behind research is not behind research");

        // Health in the same band as the structures already in the game, and the same footprint
        // as a power plant: the smallest square of cells that holds a building.
        Assert.InRange(gun.Health, 1_000, 4_000);
        Assert.InRange(antiAir.Health, 1_000, 4_000);
        Assert.Equal(1, gun.FootprintRadiusCells);
        Assert.Equal(1, antiAir.FootprintRadiusCells);

        // Raised from the headquarters, like every other structure, which is what puts them on the
        // build panel at all.
        Assert.Equal(UnitKind.CommandCentre, gun.ProducedAt);
        Assert.Equal(UnitKind.CommandCentre, antiAir.ProducedAt);
    }
}

/// <summary>
/// The same emplacements, asked the two questions placement asks: may this be raised here, and
/// how much ground does it take.
/// </summary>
public sealed class EmplacementPlacementTests
{
    private const ulong Seed = 20250101;
    private const int CellSizeMm = 9_375;

    /// <summary>
    /// The standard skirmish with the player's team able to pay, because a structure is refused
    /// for want of a headquarters, a tier or a resource before the ground is ever mentioned.
    /// </summary>
    private static SimWorld Skirmish()
    {
        SimWorld world = new(Seed, capacity: 1024);
        Scenario.Build(world, ScenarioKind.Skirmish);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;
        state.TechTier = 4;

        return world;
    }

    /// <summary>The nearest cell the whole placement rule accepts, found by asking.</summary>
    private static bool TryFindSite(SimWorld world, UnitKind kind, WorldPos near, out WorldPos site)
    {
        int centre = Math.Max(world.Navigation.IndexOfWorld(near), 0);
        int centreX = world.Navigation.CellX(centre);
        int centreZ = world.Navigation.CellZ(centre);

        for (int radius = 0; radius <= 24; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
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

                    if (world.TryPlanStructure(0, kind, position, out site, out _))
                    {
                        return true;
                    }
                }
            }
        }

        site = default;
        return false;
    }

    private static int CountKind(SimWorld world, int team, UnitKind kind)
    {
        int found = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) &&
                world.GetRefBySlot(slot).TeamId == team &&
                world.GetRefBySlot(slot).Kind == kind)
            {
                found++;
            }
        }

        return found;
    }

    /// <summary>
    /// Both emplacements can be raised by the ordinary placement rule, at the site the plan named,
    /// and each takes the 3 × 3 patch its role declares.
    /// <para>
    /// The footprint is measured rather than read back: after one is standing, a cell two away is
    /// refused — two footprints of radius one reach two cells — and a cell three away is clear,
    /// because that is where the two 3 × 3 squares stop sharing a cell. A radius of zero or two
    /// would move both of those answers, so the test would notice.
    /// </para>
    /// </summary>
    [Theory]
    [InlineData(UnitKind.GunEmplacement)]
    [InlineData(UnitKind.AntiAirEmplacement)]
    public void AnEmplacementIsRaisedWhereItWasPlannedAndTakesItsOwnGround(UnitKind kind)
    {
        SimWorld world = Skirmish();

        WorldPos near = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        Assert.True(TryFindSite(world, kind, near, out WorldPos site), $"the map has no site for a {kind} near the base");

        int cells = UnitCatalog.FootprintRadiusCells(kind);
        Assert.Equal(1, cells);

        world.Enqueue(SimCommand.Structure(kind, site, world.Tick + 1, 0));
        world.Step();
        world.Step();

        Assert.Equal(1, CountKind(world, 0, kind));

        int raised = -1;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Kind == kind && world.GetRefBySlot(slot).TeamId == 0)
            {
                raised = slot;
            }
        }

        Assert.True(raised >= 0);

        // The cell the plan named, to the millimetre in plan: the height is the terrain's, since
        // a structure stands on the ground rather than at the height the plan was measured at.
        ref Entity standing = ref world.GetRefBySlot(raised);

        Assert.Equal(site.X, standing.Position.X);
        Assert.Equal(site.Z, standing.Position.Z);

        // Its own patch is taken, either by itself or by whatever it shares a wall with.
        Assert.False(world.IsSiteClear(kind, site, out string occupied), "a structure does not occupy its own footprint");
        Assert.False(string.IsNullOrEmpty(occupied));

        // Two cells away is still inside the square it asked for; three is the next free ground.
        var twoAway = new WorldPos(site.X + (2 * CellSizeMm), 0, site.Z);
        var threeAway = new WorldPos(site.X + (3 * CellSizeMm), 0, site.Z);

        Assert.False(
            world.IsSiteClear(kind, twoAway, out _),
            "two cells away is a different building's ground, so this structure took less than a 3 x 3 patch");

        Assert.True(
            world.IsSiteClear(kind, threeAway, out string reason),
            $"three cells away is inside the footprint of the new {kind}, so its footprint is larger than radius 1: {reason}");
    }

    /// <summary>
    /// The anti-aircraft emplacement is behind an era, and the gun is not: a team at era I can
    /// raise the gun and is refused the anti-aircraft mount by name, in Greek.
    /// </summary>
    [Fact]
    public void TheAntiAirEmplacementNeedsTheSecondEraAndTheGunDoesNot()
    {
        SimWorld world = Skirmish();

        world.TeamRef(0).TechTier = 1;

        WorldPos near = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        Assert.True(TryFindSite(world, UnitKind.GunEmplacement, near, out _), "era I cannot raise the first-minute defensive structure");

        Assert.False(
            TryFindSite(world, UnitKind.AntiAirEmplacement, near, out _),
            "the anti-aircraft emplacement is buildable at era I, so there is nothing to wait for");

        Assert.False(world.CanBuildStructure(0, UnitKind.AntiAirEmplacement, out string reason));
        Assert.Equal("χρειάζεται τεχνολογία 2", reason);
    }

    /// <summary>
    /// The emplacements are the only structures in the game with a gun on them, and this is the
    /// assertion that keeps it that way: industry that could shoot would be industry nobody has
    /// to defend, which is a balance decision nobody has made.
    /// </summary>
    [Fact]
    public void OnlyTheEmplacementsAreArmedStructures()
    {
        foreach (UnitDefinition definition in UnitCatalog.All)
        {
            if (!definition.IsBuilding)
            {
                continue;
            }

            bool expected = definition.Kind is UnitKind.GunEmplacement or UnitKind.AntiAirEmplacement;

            Assert.True(
                definition.IsArmed == expected,
                $"{definition.Kind} is {(definition.IsArmed ? "armed" : "unarmed")} and should not be");
        }
    }
}

/// <summary>
/// What the emplacements are standing on.
/// <para>
/// Cover already applies to whatever occupies a cell, so a gun in woodland is harder to kill than
/// the same gun on sand without a line of code being written for it — and that is worth a test
/// precisely because it is the property that makes placement a decision rather than a formality.
/// A defensive structure the ground cannot shelter is a defensive structure that only has range
/// to offer.
/// </para>
/// </summary>
public sealed class EmplacementCoverTests
{
    private const ulong Seed = 20250101;
    private const int Spacing = 5;

    /// <summary>
    /// Fights one arm of the experiment: one gun emplacement on a cell whose surface and canopy
    /// the caller sets, and one tank five cells away. The landform is levelled in both arms so
    /// that the only difference between them is the ground the emplacement stands on.
    /// </summary>
    private static (int Cover, int Lost) FirstHit(bool woodland, int defenderCell, int attackerCell)
    {
        SimWorld world = new(Seed, capacity: 8);
        TerrainLayer terrain = world.TerrainTypes;

        TerrainAttributes ground = terrain.AttributesAt(defenderCell).WithLandform(TerrainShape.Plain);
        terrain.SetType(defenderCell, woodland ? TerrainType.Forest : TerrainType.Sand);
        terrain.SetAttributes(defenderCell, ground.WithVegetation(woodland ? TerrainAttributes.MaxVegetation : 0));

        UnitDefinition emplacement = UnitCatalog.Get(UnitKind.GunEmplacement);
        UnitDefinition tank = UnitCatalog.Get(UnitKind.Tank);

        EntityId defender = world.Spawn(
            Faction.Western,
            2,
            UnitKind.GunEmplacement,
            world.Navigation.CentreOf(defenderCell),
            Fix32.Zero,
            emplacement.Health);

        world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Tank,
            world.Navigation.CentreOf(attackerCell),
            Fix32.FromInt(tank.SpeedMmPerTick),
            tank.Health);

        int start = world.GetRefBySlot(defender.Slot).Health;
        int cover = terrain.CoverAt(defenderCell, MovementClass.Foot);

        // The first hit, not a total over a window: the number being compared is what one shell
        // does, and a second shot would fold in whatever the emplacement did in between.
        for (int tick = 0; tick < 400; tick++)
        {
            world.Step();

            if (!world.IsValid(defender) || world.GetRefBySlot(defender.Slot).Health < start)
            {
                return (cover, world.IsValid(defender) ? start - world.GetRefBySlot(defender.Slot).Health : start);
            }
        }

        return (cover, 0);
    }

    /// <summary>
    /// Two cells of open ground five cells apart — 47 m, inside the tank's range — on the standard
    /// map, so the pair is a piece of the map the generator actually produced.
    /// </summary>
    private static (int Defender, int Attacker) FightingGround()
    {
        TerrainLayer terrain = new SimWorld(Seed, capacity: 8).TerrainTypes;

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x + Spacing < terrain.Size; x++)
            {
                int defender = (z * terrain.Size) + x;

                if (terrain.TypeAt(defender) == TerrainType.Grass &&
                    terrain.TypeAt(defender + Spacing) == TerrainType.Grass)
                {
                    return (defender, defender + Spacing);
                }
            }
        }

        throw new InvalidOperationException("the standard map has no two cells of open ground five cells apart.");
    }

    /// <summary>
    /// The same emplacement, under the same gun, loses less health per hit in woodland than it
    /// does on sand — and loses exactly what the cover of the cell it is standing on says it
    /// should.
    /// <para>
    /// The arithmetic is asserted as well as the direction, and against the sim's own public
    /// cover lookup rather than a number copied out of a run: a damaged building that took the
    /// catalogue damage unchanged would pass a "the two are different" test on a rounding, and
    /// the claim being made here is that cover is what the shot is scaled by.
    /// </para>
    /// </summary>
    [Fact]
    public void AStructureInWoodlandTakesLessDamagePerHitThanTheSameStructureOnSand()
    {
        (int defenderCell, int attackerCell) = FightingGround();

        (int woodsCover, int woodsLost) = FirstHit(woodland: true, defenderCell, attackerCell);
        (int sandCover, int sandLost) = FirstHit(woodland: false, defenderCell, attackerCell);

        string report =
            $"the same gun emplacement on cell {defenderCell}, under the same tank: in woodland (cover {woodsCover}) it " +
            $"lost {woodsLost} health to the first shell, on sand (cover {sandCover}) it lost {sandLost}";

        Assert.True(woodsLost > 0 && sandLost > 0, $"one of the two emplacements was never hit, so there is nothing to compare. {report}");
        Assert.True(woodsCover < sandCover, $"woodland is not better cover than sand on this ground. {report}");
        Assert.True(sandLost > woodsLost, $"the ground the emplacement stands on made no difference to the damage it took. {report}");

        // Damage is the catalogue's, scaled once by the cover of the cell the target occupies —
        // the same arithmetic CombatSystem does, and the number the report above would be wrong
        // about if it did not hold.
        int damage = UnitCatalog.Get(UnitKind.Tank).AttackDamage;

        Assert.Equal(Math.Max(1, (damage * woodsCover) / TerrainLayer.NoCoverPermille), woodsLost);
        Assert.Equal(Math.Max(1, (damage * sandCover) / TerrainLayer.NoCoverPermille), sandLost);
    }
}
