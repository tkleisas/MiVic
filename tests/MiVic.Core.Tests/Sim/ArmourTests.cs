using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Armour: damage reduction per hit, per faction and per role, and the one rule that composes it
/// with the ground the target is standing on.
/// <para>
/// <b>These tests assert relations rather than constants, and that is deliberate.</b> Every number
/// in this feature is a balance figure somebody will move — 720 will become 700 or 750 the first
/// time the campaign is tuned — and a test that pinned it would fail on the change and pass on the
/// bug. What is pinned instead is what the design actually claims: that Σοβιετικοί buildings take
/// less from the same weapon than Κινέζοι ones and that the Δυτικοί sit between them, that
/// Δυτικοί machines take less than Σοβιετικοί ones and that the two light schools are close, that a
/// Σοβιετικοί column crosses mud faster than a Δυτικοί one, and that no combination of ground and
/// plate can zero a hit. The same standard a stealth radius was pinned by — the relation that makes
/// the number mean something, not the number.
/// </para>
/// </summary>
public sealed class ArmourTests
{
    private const ulong Seed = 20250101;

    private static SimWorld World(int capacity = 32) => new(Seed, capacity);

    /// <summary>
    /// The first hit one gun lands on one target, in a world built for it, as the health it lost.
    /// <para>
    /// The first hit rather than a total over a window: the number under test is what one shell
    /// does, and a second shot would fold in everything the two of them did in between. The ground
    /// under the target is levelled to plain grass first, so the only term left in the composition
    /// that can differ between two arms of an experiment is the armour.
    /// </para>
    /// </summary>
    private static (int Cover, int Lost) FirstHit(
        Faction victimFaction,
        int victimTeam,
        UnitKind victimKind,
        Faction shooterFaction,
        int shooterTeam,
        UnitKind shooterKind)
    {
        SimWorld world = World();

        int cell = world.Navigation.NearestWalkable(0);

        TerrainAttributes level = world.TerrainTypes.AttributesAt(cell)
            .WithLandform(TerrainShape.Plain)
            .WithVegetation(0);

        world.TerrainTypes.SetType(cell, TerrainType.Grass);
        world.TerrainTypes.SetAttributes(cell, level);

        WorldPos stand = world.Navigation.CentreOf(cell);

        UnitDefinition victimDefinition = UnitCatalog.Get(victimKind);
        UnitDefinition shooterDefinition = UnitCatalog.Get(shooterKind);

        // A structure so it cannot walk away, and hit points enough to survive the measurement so
        // that "lost" is a subtraction rather than a death.
        EntityId victim = world.Spawn(victimFaction, victimTeam, victimKind, stand, Fix32.Zero, 1_000_000);

        world.Spawn(
            shooterFaction,
            shooterTeam,
            shooterKind,
            new WorldPos(stand.X + 40_000, stand.Y, stand.Z),
            Fix32.Zero,
            shooterDefinition.Health);

        int start = world.GetRefBySlot(victim.Slot).Health;
        int cover = world.TerrainTypes.CoverAt(cell, victimDefinition.Movement);

        for (int tick = 0; tick < 400; tick++)
        {
            world.Step();

            if (!world.IsValid(victim) || world.GetRefBySlot(victim.Slot).Health < start)
            {
                return (cover, start - world.GetRefBySlot(victim.Slot).Health);
            }
        }

        return (cover, 0);
    }

    /// <summary>
    /// The same weapon on the same kind of structure does measurably less to a Σοβιετικοί one than
    /// to a Κινέζοι one, and the Δυτικοί sit between them: the whole of the structures half of the
    /// design, as three measurements rather than as a table of permille.
    /// </summary>
    [Fact]
    public void SovietStructuresOutlastChineseOnesAndTheWesternSitBetween()
    {
        // A Δυτικοί gun against a Δυτικοί, Σοβιετικοί and Κινέζοι headquarters. Same weapon, same
        // role, same ground, three owners — so the only term that can differ is the armour.
        (int westernCover, int westernLost) = FirstHit(
            Faction.Western, 2, UnitKind.CommandCentre, Faction.Western, 3, UnitKind.Tank);

        (int sovietCover, int sovietLost) = FirstHit(
            Faction.Soviet, 2, UnitKind.CommandCentre, Faction.Western, 3, UnitKind.Tank);

        (int chineseCover, int chineseLost) = FirstHit(
            Faction.Chinese, 2, UnitKind.CommandCentre, Faction.Western, 3, UnitKind.Tank);

        string report =
            $"the same Δυτικοί tank, the same 850-permille command centre, levelled grass under all three: " +
            $"Σοβιετικοί lost {sovietLost} (cover {sovietCover}), Δυτικοί {westernLost} (cover {westernCover}), " +
            $"Κινέζοι {chineseLost} (cover {chineseCover})";

        Assert.True(sovietLost > 0 && westernLost > 0 && chineseLost > 0, $"nothing was hit, so there is nothing to compare. {report}");

        // The ground is held equal on purpose: if it were not, a difference in cover would be
        // readable as a difference in armour, which is the mistake this whole rule exists to make
        // impossible.
        Assert.True(
            sovietCover == westernCover && westernCover == chineseCover,
            $"the three did not stand on the same ground, so the measurement is of the map and not of the armour. {report}");

        Assert.True(sovietLost < chineseLost, $"a Σοβιετικοί building is not the most reinforced of the three. {report}");
        Assert.True(sovietLost < westernLost, $"a Σοβιετικοί building is not harder than a Δυτικοί one. {report}");
        Assert.True(westernLost < chineseLost, $"the Δυτικοί do not sit between the Σοβιετικοί and the Κινέζοι. {report}");
    }

    /// <summary>
    /// Δυτικοί machines take less than Σοβιετικοί ones, and the two light schools are close: the
    /// vehicles half of the design, which runs the opposite way to the structures half.
    /// <para>
    /// The gauge is the <em>same</em> Δυτικοί tank firing at a Σοβιετικοί, a Κινέζοι and a Δυτικοί
    /// tank, so the weapon and the 35 damage are common to all three arms and the only term that
    /// can move is the owner's armour.
    /// </para>
    /// </summary>
    [Fact]
    public void WesternMachinesTakeLessThanSovietOnesAndTheLightTwoAreClose()
    {
        (int sovietCover, int sovietLost) = FirstHit(
            Faction.Soviet, 2, UnitKind.Tank, Faction.Western, 3, UnitKind.Tank);

        (int chineseCover, int chineseLost) = FirstHit(
            Faction.Chinese, 2, UnitKind.Tank, Faction.Western, 3, UnitKind.Tank);

        (int westernCover, int westernLost) = FirstHit(
            Faction.Western, 2, UnitKind.Tank, Faction.Soviet, 3, UnitKind.Tank);

        string report =
            $"the same 35-damage tank gun, on levelled grass under every subject: Σοβιετικοί lost {sovietLost} " +
            $"(cover {sovietCover}), Κινέζοι {chineseLost} (cover {chineseCover}), Δυτικοί {westernLost} (cover {westernCover})";

        Assert.True(sovietLost > 0 && chineseLost > 0 && westernLost > 0, $"nothing was hit. {report}");
        Assert.True(
            sovietCover == chineseCover && chineseCover == westernCover,
            $"the three did not stand on the same ground. {report}");

        Assert.True(westernLost < sovietLost, $"a Δυτικοί hull is not the best armoured of the three. {report}");
        Assert.True(westernLost < chineseLost, $"a Δυτικοί hull is not better armoured than a Κινέζοι one. {report}");

        // "On a par with the Κινέζοι": the two light schools must be close enough that a player
        // reads them as the same weight, which is a statement about a ratio and not about a
        // difference — a tenth of the Δυτικοί figure is the outside edge of "the same".
        int gap = Math.Abs(sovietLost - chineseLost);
        int spread = Math.Abs(westernLost - Math.Max(sovietLost, chineseLost));

        Assert.True(
            gap * 10 <= Math.Max(sovietLost, chineseLost),
            $"the Σοβιετικοί and Κινέζοι hulls are not on a par with each other. {report}");

        Assert.True(
            spread > gap,
            $"the Δυτικοί hull is not a separate weight class from the two light ones. {report}");
    }

    /// <summary>
    /// A structure's armour is a fact about the role and not about its owner, and it multiplies
    /// rather than replacing the faction's figure: a nuclear plant is thicker than a power plant
    /// for every faction, and the faction ordering survives it.
    /// </summary>
    [Fact]
    public void ARoleFigureMultipliesWithAFactionFigureRatherThanReplacingIt()
    {
        foreach (Faction faction in new[] { Faction.Soviet, Faction.Western })
        {
            int plant = UnitCatalog.ArmourPermille(faction, UnitKind.NuclearPlant);
            int ordinary = UnitCatalog.ArmourPermille(faction, UnitKind.PowerPlant);

            Assert.True(
                plant < ordinary,
                $"{faction}: a nuclear plant ({plant}) is not thicker than the power plant it is an upgrade of ({ordinary}), " +
                "which is a fact about the role rather than about who poured it");
        }

        // And the faction ordering is still the faction ordering underneath it: the same role built
        // by a different power is a different number, whichever role it is.
        foreach (UnitKind kind in new[] { UnitKind.PowerPlant, UnitKind.NuclearPlant, UnitKind.CommandCentre, UnitKind.RadarStation })
        {
            int soviet = UnitCatalog.ArmourPermille(Faction.Soviet, kind);
            int western = UnitCatalog.ArmourPermille(Faction.Western, kind);
            int chinese = UnitCatalog.ArmourPermille(Faction.Chinese, kind);

            Assert.True(
                soviet < western && western < chinese,
                $"{kind}: Σοβιετικοί {soviet}, Δυτικοί {western}, Κινέζοι {chinese} is not the ordering the design asks for");
        }

        // A radar is the one structure whose role figure runs the other way, and it is the
        // catalogue's own claim — "deliberately fragile" — made mechanical.
        Assert.True(
            UnitCatalog.Get(UnitKind.RadarStation).RoleArmourPermille > 1_000,
            "the radar station is no longer documented as being thinner than ordinary construction");
    }

    /// <summary>
    /// Armour is not health. A unit built by an armoured faction has exactly the hit points the
    /// catalogue gives its role, and takes less from a hit instead — which is the whole difference
    /// between "this building is bigger" and "this building is something you bring the right
    /// weapon for".
    /// </summary>
    [Fact]
    public void ArmourIsNotABiggerHealthPool()
    {
        SimWorld world = World();

        WorldPos stand = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        UnitDefinition centre = UnitCatalog.Get(UnitKind.CommandCentre);

        foreach (Faction faction in new[] { Faction.Soviet, Faction.Western, Faction.Chinese })
        {
            EntityId id = world.Spawn(faction, 0, UnitKind.CommandCentre, stand, Fix32.Zero, centre.Health);

            Assert.Equal(centre.Health, world.GetRefBySlot(id.Slot).Health);
            Assert.True(
                UnitCatalog.ArmourPermille(faction, UnitKind.CommandCentre) < 1_000,
                $"{faction} owns no armour at all, so this test is measuring nothing");
        }
    }

    /// <summary>
    /// Cover and armour both apply, in the documented order, and each is worth something on its
    /// own: a target in cover behind armour takes strictly less than the same target with only one
    /// of the two.
    /// </summary>
    [Fact]
    public void CoverAndArmourBothApplyAndInTheDocumentedOrder()
    {
        const int Damage = 45;
        const int Cover = 840;
        const int Armour = 720;

        int both = DamageRules.Compose(Damage, Cover, Armour);
        int coverOnly = DamageRules.Compose(Damage, Cover, 1_000);
        int armourOnly = DamageRules.Compose(Damage, 1_000, Armour);
        int neither = DamageRules.Compose(Damage, 1_000, 1_000);

        Assert.Equal(Damage, neither);
        Assert.True(both < coverOnly, $"armour added nothing to a shot that already met cover: {both} against {coverOnly}");
        Assert.True(both < armourOnly, $"cover added nothing to a shot that already met armour: {both} against {armourOnly}");

        // The order is observable, because each step truncates — and this pair is chosen so that
        // it is: 35 damage through 927 cover and 970 armour keeps 31 the documented way round
        // (cover first) and 30 the other. A test that did not say which way round it is would pass
        // whichever line a future edit happened to put first, and the published order would be a
        // comment rather than a rule.
        const int Observable = 35;
        const int WoodCover = 927;
        const int LightArmour = 970;

        int documented = DamageRules.Compose(Observable, WoodCover, LightArmour);
        int reversed = ((Observable * LightArmour) / 1_000 * WoodCover) / TerrainLayer.NoCoverPermille;

        Assert.Equal(((Observable * WoodCover) / TerrainLayer.NoCoverPermille * LightArmour) / 1_000, documented);
        Assert.NotEqual(reversed, documented);

        // Ground that exposes a target rather than hiding it still runs through the same product:
        // a crest lets more through, and the armour reduces the larger number.
        int skylined = DamageRules.Compose(Damage, TerrainLayer.MaxCoverPermille, Armour);
        Assert.True(skylined > both, "standing on a crest did not make the target easier to hurt");
    }

    /// <summary>
    /// The floor holds: however much ground and however much plate stand in the way, a hit that
    /// lands does at least one point, and no combination of the two can produce an immovable
    /// object. This is the case a flat subtraction would have produced and a permille cannot.
    /// </summary>
    [Fact]
    public void NoAmountOfGroundAndPlateCanReduceAHitToNothing()
    {
        // Beyond anything the game can build, on purpose: the claim is about the shape of the
        // arithmetic, not about today's numbers.
        Assert.Equal(DamageRules.MinimumDamage, DamageRules.Compose(1, TerrainLayer.MinCoverPermille, 1));
        Assert.Equal(DamageRules.MinimumDamage, DamageRules.Compose(1, 1, 1));
        Assert.Equal(DamageRules.MinimumDamage, DamageRules.Compose(2, TerrainLayer.MinCoverPermille, 500));

        // And through a real world: the smallest hit the rule will ever be handed, against the most
        // reinforced thing a faction can build, standing in the best cover the ground can give.
        SimWorld world = World();
        int cell = world.Navigation.NearestWalkable(0);

        world.TerrainTypes.SetType(cell, TerrainType.Forest);
        world.TerrainTypes.SetAttributes(
            cell,
            world.TerrainTypes.AttributesAt(cell)
                .WithLandform(TerrainShape.Valley)
                .WithVegetation(TerrainAttributes.MaxVegetation));

        EntityId centre = world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.CommandCentre,
            world.Navigation.CentreOf(cell),
            Fix32.Zero,
            1_000_000);

        int cover = world.TerrainTypes.CoverAt(cell, MovementClass.Foot);

        Assert.True(cover < TerrainLayer.NoCoverPermille, $"the ground gave no cover at all, so the floor is untested: {cover}");

        Assert.Equal(DamageRules.MinimumDamage, DamageRules.Against(world, centre.Slot, 1));

        // And a real weapon in the same place is reduced by the ground and the plate without ever
        // reaching nothing: an infantryman's eight points of rifle fire against a Σοβιετικοί
        // headquarters in a wood.
        int rifle = DamageRules.Against(world, centre.Slot, 8);

        Assert.True(rifle >= DamageRules.MinimumDamage, "a rifle did nothing at all to a building in a wood, which is the immunity case");
        Assert.True(rifle < 8, $"the wood and the concrete took nothing off a rifle shot: {rifle}");
    }

    /// <summary>
    /// Splash and off-map support reduce damage exactly as a direct shot does.
    /// <para>
    /// <b>Asserted as agreement rather than as three separate truths.</b> The three paths are given
    /// the same nominal damage — a tank's 35 scaled by ten, a Κατιούσα's 95 scaled to match, and a
    /// Τροχιακό Πλήγμα's catalogue 350 — and fired at the same role of building, owned by the same
    /// faction, on the same levelled ground. If any of the three had kept a damage path of its own
    /// the three numbers would part company here, which is exactly the failure this test exists to
    /// catch: a rule that holds on two paths out of three is not a rule.
    /// </para>
    /// <para>
    /// The artillery arm has to be read off a health bar over time rather than off one shot,
    /// because a Κατιούσα scatters: what is measured is the largest single-tick loss the victim
    /// ever takes, which is one salvo landing on it.
    /// </para>
    /// </summary>
    [Fact]
    public void SplashAndAbilitiesReduceDamageExactlyAsDirectFireDoes()
    {
        const int Nominal = 350;

        int direct = DirectHit(Faction.Soviet, 2, UnitKind.CommandCentre, Faction.Western, 3, UnitKind.Tank, Nominal);
        int splash = LargestSalvoHit(Faction.Soviet, 2, UnitKind.CommandCentre, Nominal);
        int strike = OrbitalStrikeHit(Faction.Soviet, 2, UnitKind.CommandCentre, Nominal);

        string report =
            $"the same 350-damage hit on a Σοβιετικοί command centre on levelled grass: a direct shot took {direct}, " +
            $"a Κατιούσα salvo took {splash}, a Τροχιακό Πλήγμα took {strike}";

        Assert.True(direct > 0, $"the direct shot never landed. {report}");
        Assert.True(splash > 0, $"no salvo ever landed on the target, so the splash path is unmeasured. {report}");
        Assert.True(strike > 0, $"the strike never landed. {report}");

        Assert.True(direct == splash && splash == strike, $"the three damage paths do not agree. {report}");

        // And the number they agree on is the composed one, not the catalogue's: the agreement
        // above would also hold if all three paths had quietly stopped applying armour.
        int armour = UnitCatalog.ArmourPermille(Faction.Soviet, UnitKind.CommandCentre);

        Assert.True(armour < 1_000, "Σοβιετικοί buildings carry no armour, so this test is measuring nothing");
        Assert.Equal(DamageRules.Compose(Nominal, TerrainLayer.NoCoverPermille, armour), direct);
    }

    /// <summary>One direct shot of <paramref name="nominal"/> damage at a building on plain grass.</summary>
    private static int DirectHit(
        Faction victimFaction,
        int victimTeam,
        UnitKind victimKind,
        Faction shooterFaction,
        int shooterTeam,
        UnitKind shooterKind,
        int nominal)
    {
        SimWorld world = Levelled(out int cell);
        WorldPos stand = world.Navigation.CentreOf(cell);
        UnitDefinition shooter = UnitCatalog.Get(shooterKind);

        // The research-derived damage multiplier is the mechanism that turns a catalogue figure
        // into a shot's damage, so it is what is set to arrange a nominal value: a 35-damage gun
        // at 10 000 permille fires a 350-damage shell, through the same line the gun always uses.
        // Rounded up, because the gun truncates after multiplying and the three arms of this test
        // have to be handed the same number.
        world.TeamRef(shooterTeam).DamagePermille =
            ((nominal * 1_000) + shooter.AttackDamage - 1) / shooter.AttackDamage;

        EntityId victim = world.Spawn(victimFaction, victimTeam, victimKind, stand, Fix32.Zero, 1_000_000);

        world.Spawn(
            shooterFaction,
            shooterTeam,
            shooterKind,
            new WorldPos(stand.X + 40_000, stand.Y, stand.Z),
            Fix32.Zero,
            shooter.Health);

        int start = world.GetRefBySlot(victim.Slot).Health;

        return FirstDrop(world, victim, start, ticks: 200);
    }

    /// <summary>
    /// The largest single-tick loss a Κατιούσα ever inflicts on a building on plain grass, with the
    /// gun's own damage multiplier arranged so that one salvo is worth <paramref name="nominal"/>.
    /// </summary>
    private static int LargestSalvoHit(Faction victimFaction, int victimTeam, UnitKind victimKind, int nominal)
    {
        SimWorld world = Levelled(out int cell);
        WorldPos stand = world.Navigation.CentreOf(cell);
        UnitDefinition katyusha = UnitCatalog.Get(UnitKind.RocketArtillery);

        // The gun's damage multiplier is chosen so that the *product* is the nominal figure and not
        // the quotient: 95 damage at 3 684 permille is a 349-damage shell, because the gun truncates
        // after multiplying, and a test comparing three paths has to give them the same number to
        // compare. Rounded up, 3 685 is 350.
        world.TeamRef(SalvoTeam).DamagePermille =
            ((nominal * 1_000) + katyusha.AttackDamage - 1) / katyusha.AttackDamage;

        EntityId victim = world.Spawn(victimFaction, victimTeam, victimKind, stand, Fix32.Zero, 1_000_000);

        // Forty metres, and the reason is not tactical. A Κατιούσα sees 170 m and shoots 260, so a
        // battery parked two hundred metres out would be firing only while something else was
        // looking for it — and the sensor chain is DetectionTests' subject, not this one. Inside its
        // own eyes the salvo path is the only thing between the gun and the health bar.
        //
        // The spawn is checked rather than trusted: a ground unit that lands on impassable terrain
        // is moved to the nearest free cell, and the first version of this test put its battery on
        // water, watched the simulation relocate it two hundred metres away, and measured a gun
        // that could not see what it was aiming at.
        EntityId gun = world.Spawn(
            Faction.Western,
            SalvoTeam,
            UnitKind.RocketArtillery,
            new WorldPos(stand.X + 40_000, stand.Y, stand.Z),
            Fix32.Zero,
            katyusha.Health);

        Assert.True(
            world.GetRefBySlot(gun.Slot).Position.HorizontalDistanceTo(stand) <= 45_000,
            "the Κατιούσα was not spawned where the test put it, so the salvo would be measured from the wrong place");

        int previous = world.GetRefBySlot(victim.Slot).Health;
        int largest = 0;

        for (int tick = 0; tick < 4_000; tick++)
        {
            world.Step();

            if (!world.IsValid(victim))
            {
                break;
            }

            int health = world.GetRefBySlot(victim.Slot).Health;
            largest = Math.Max(largest, previous - health);
            previous = health;
        }

        return largest;
    }

    /// <summary>
    /// What a Τροχιακό Πλήγμα does to a building on plain grass, through the world's own ability
    /// path rather than through a second copy of it.
    /// <para>
    /// It is cast by team 0, which the standard roster makes Σοβιετικοί, because the ability is
    /// theirs and no other team may call it — and the building it lands on carries the Σοβιετικοί
    /// <em>faction</em> while standing on team 2, which is hostile to them. Faction and team are
    /// separate fields on an entity precisely so that a test can ask for that.
    /// </para>
    /// </summary>
    private static int OrbitalStrikeHit(Faction victimFaction, int victimTeam, UnitKind victimKind, int nominal)
    {
        SimWorld world = Levelled(out int cell);
        WorldPos stand = world.Navigation.CentreOf(cell);

        AbilityCatalog.TryGet(AbilityId.OrbitalStrike, out AbilityDefinition definition);

        // The ability's damage is a catalogue figure and not a scaled one, so the test can only
        // proceed if the two happen to agree — which is why the nominal chosen above is 350.
        Assert.Equal(nominal, definition.Damage);

        // The caster's own prerequisites, met the way a player would have met them: the era, the
        // project, the bureau and the materials. The bureau stands inside the strike's radius on
        // purpose: an ability documented as sparing its own side should be shown sparing one, and
        // the friend it spares is right there beside the enemy it does not.
        world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau, new WorldPos(stand.X - 40_000, stand.Y, stand.Z), Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.TechTier = definition.RequiredTechTier;
        state.TechMask |= 1UL << (int)definition.RequiredTech;

        Assert.True(world.CanUseAbility(0, AbilityId.OrbitalStrike, out string reason), $"the strike is not available to its own faction: {reason}");

        // On the levelled cell itself: the strike has to land on the same ground the other two arms
        // were measured on, or the comparison is of two pieces of map rather than of two damage
        // paths. Twenty metres away is a different cell, and a different cell is a different cover.
        EntityId victim = world.Spawn(victimFaction, victimTeam, victimKind, stand, Fix32.Zero, 1_000_000);

        int start = world.GetRefBySlot(victim.Slot).Health;

        world.Enqueue(SimCommand.UseAbility(AbilityId.OrbitalStrike, world.GetRefBySlot(victim.Slot).Position, world.Tick + 1, 0));
        world.Step();

        return start - world.GetRefBySlot(victim.Slot).Health;
    }

    /// <summary>
    /// The team that fires the salvo: the fourth slot, which no match declares.
    /// <para>
    /// It has to be a team the AI does not play, and the first version of this test was not — the
    /// computer opponent took the Κατιούσα and drove it a hundred metres away from the building it
    /// was supposed to be shelling, so the splash path measured a march instead of a salvo. The
    /// team beside it is the one the building stands on, which the slot is hostile to and which
    /// cannot walk away from a building.
    /// </para>
    /// </summary>
    private const int SalvoTeam = 3;

    /// <summary>Runs until the victim's health first drops, and answers by how much.</summary>
    private static int FirstDrop(SimWorld world, EntityId victim, int start, int ticks)
    {
        for (int tick = 0; tick < ticks; tick++)
        {
            world.Step();

            if (!world.IsValid(victim) || world.GetRefBySlot(victim.Slot).Health < start)
            {
                return start - (world.IsValid(victim) ? world.GetRefBySlot(victim.Slot).Health : start);
            }
        }

        return 0;
    }

    /// <summary>A world with one cell of plain, bare grass at its first walkable ground.</summary>
    private static SimWorld Levelled(out int cell)
    {
        SimWorld world = World();
        cell = world.Navigation.NearestWalkable(0);

        world.TerrainTypes.SetType(cell, TerrainType.Grass);
        world.TerrainTypes.SetAttributes(
            cell,
            world.TerrainTypes.AttributesAt(cell)
                .WithLandform(TerrainShape.Plain)
                .WithVegetation(0));

        return world;
    }
}

/// <summary>
/// The rasputitsa: ground pressure, which was already per-faction, finally costing a heavy column
/// something. These are the tests that make the mud season a mechanic rather than an intention.
/// </summary>
public sealed class RasputitsaTests
{
    private const ulong Seed = 20250101;

    /// <summary>Half the width, in cells, of the block the three columns are driven across.</summary>
    private const int BlockRadius = 12;

    /// <summary>
    /// Three parallel lanes on one block of ground, every cell of it the same surface.
    /// <para>
    /// The block is searched for rather than assumed, and every cell of it has to be
    /// <em>walkable</em> as well as flat-surfaced: a navigation cell that the slope made
    /// impassable is an obstacle whatever the surface is, and a route that went round one would be
    /// a measurement of the map rather than of the two columns' weight. So this picks a square the
    /// pathfinder has no opinion about, lays the surface over the whole of it, and puts one tank in
    /// each of its three lanes.
    /// </para>
    /// </summary>
    private static (SimWorld World, EntityId[] Lanes) ThreeLanes(TerrainType surface)
    {
        SimWorld world = new(Seed, capacity: 16);

        if (!TryFindOpenBlock(world, out int centreX, out int centreZ))
        {
            throw new InvalidOperationException("the standard map has no open block wide enough for three lanes.");
        }

        for (int dz = -BlockRadius; dz <= BlockRadius; dz++)
        {
            for (int dx = -BlockRadius; dx <= BlockRadius; dx++)
            {
                world.TerrainTypes.SetType(world.Navigation.IndexOf(centreX + dx, centreZ + dz), surface);
            }
        }

        UnitDefinition tank = UnitCatalog.Get(UnitKind.Tank);

        Faction[] schools = [Faction.Soviet, Faction.Western, Faction.Chinese];
        var lanes = new EntityId[schools.Length];

        for (int i = 0; i < schools.Length; i++)
        {
            // Four cells between lanes: enough that no column churns the rut the next one is using,
            // because the churn surcharge is a second term on the same cost and would be a
            // confound if the three of them shared a track.
            WorldPos start = LaneCentre(world, centreX, centreZ, (i - 1) * 4);

            lanes[i] = world.Spawn(
                schools[i], LaneTeam, UnitKind.Tank, start, Fix32.FromInt(tank.SpeedMmPerTick), tank.Health);

            // A goal eight cells down the lane, in a straight line, so the three lanes are the same
            // length and the only thing that can differ is how fast each column covers it.
            world.OrderMove(lanes[i], new WorldPos(start.X, start.Y, start.Z + (8 * 9_375)), LaneTeam);
        }

        return (world, lanes);
    }

    /// <summary>
    /// The team the lanes are driven on: the fourth slot, which no match declares and which the
    /// computer therefore does not play.
    /// <para>
    /// It is not tidiness. The three arms of the experiment have to differ in one thing — the
    /// school that built the tank — and a column that the AI has given a gather order to is a
    /// column whose distance travelled is a fact about the AI. This is the same slot, for the same
    /// reason, that the detection fixture stands its measurement subjects on.
    /// </para>
    /// </summary>
    private const int LaneTeam = 3;

    private static WorldPos LaneCentre(SimWorld world, int centreX, int centreZ, int offsetX)
        => world.Navigation.CentreOf(world.Navigation.IndexOf(centreX + offsetX, centreZ - (BlockRadius - 2)));

    /// <summary>
    /// A square of ground, <see cref="BlockRadius"/> cells in every direction, on which every cell
    /// is walkable.
    /// </summary>
    private static bool TryFindOpenBlock(SimWorld world, out int centreX, out int centreZ)
    {
        NavGrid grid = world.Navigation;

        for (int z = BlockRadius; z < grid.Size - BlockRadius; z++)
        {
            for (int x = BlockRadius; x < grid.Size - BlockRadius; x++)
            {
                bool open = true;

                for (int dz = -BlockRadius; dz <= BlockRadius && open; dz++)
                {
                    for (int dx = -BlockRadius; dx <= BlockRadius; dx++)
                    {
                        if (!grid.IsWalkable(grid.IndexOf(x + dx, z + dz)))
                        {
                            open = false;
                            break;
                        }
                    }
                }

                if (open)
                {
                    centreX = x;
                    centreZ = z;
                    return true;
                }
            }
        }

        centreX = 0;
        centreZ = 0;
        return false;
    }

    /// <summary>
    /// A Σοβιετικοί vehicle crosses mud faster than a Δυτικοί one, and the Κινέζοι — whose designs
    /// press hardest of the three — is slower again. The relation is asserted, not the distance:
    /// the distances are what the ground-pressure figures will change.
    /// </summary>
    [Fact]
    public void ASovietVehicleCrossesMudFasterThanAWesternOne()
    {
        const int Ticks = 200;

        (SimWorld world, EntityId[] lanes) = ThreeLanes(TerrainType.Mud);

        world.RunTicks(Ticks);

        int soviet = (int)world.GetRefBySlot(lanes[0].Slot).DistanceTravelledMm;
        int western = (int)world.GetRefBySlot(lanes[1].Slot).DistanceTravelledMm;
        int chinese = (int)world.GetRefBySlot(lanes[2].Slot).DistanceTravelledMm;

        string report =
            $"three identical tanks, the same 400 mm per tick, {Ticks} ticks of the same mud on separate lanes: " +
            $"Σοβιετικοί covered {soviet} mm, Δυτικοί {western} mm, Κινέζοι {chinese} mm";

        Assert.True(soviet > 0 && western > 0, $"one of the columns never moved at all, so nothing crossed the mud. {report}");

        Assert.True(soviet > western, $"the rasputitsa is not a Σοβιετικοί advantage. {report}");
        Assert.True(western > chinese, $"the heaviest school is not the slowest in the mud. {report}");

        // And it is a real advantage rather than a rounding: a tenth is the least a player would
        // notice, and the design says a Δυτικοί push "slows to a crawl" where the Σοβιετικοί one
        // does not.
        Assert.True(
            soviet * 10 >= western * 11,
            $"the Σοβιετικοί advantage in the mud is too small to be a faction being played rather than a statistic. {report}");
    }

    /// <summary>
    /// The same three tanks on hard ground are identical: the Σοβιετικοί buy mud-crossing with
    /// their armour and not general speed, so a faction that were faster <em>everywhere</em> would
    /// be a different design from the one that was decided — and the Δυτικοί tank, which is the
    /// best armoured thing on the map, would have no weakness at all.
    /// </summary>
    [Fact]
    public void OnHardGroundTheThreeSchoolsAreTheSameSpeed()
    {
        (SimWorld world, EntityId[] lanes) = ThreeLanes(TerrainType.Grass);

        world.RunTicks(150);

        long soviet = world.GetRefBySlot(lanes[0].Slot).DistanceTravelledMm;
        long western = world.GetRefBySlot(lanes[1].Slot).DistanceTravelledMm;
        long chinese = world.GetRefBySlot(lanes[2].Slot).DistanceTravelledMm;

        Assert.True(soviet > 0, "nothing moved on the grass, so this test measured nothing");
        Assert.Equal(soviet, western);
        Assert.Equal(soviet, chinese);
    }

    /// <summary>
    /// The Σοβιετικοί weather ability is what makes the advantage reachable. Nothing in this scene
    /// is mud until Έλεγχος Καιρού soaks it, and then the Δυτικοί column crossing that ground is
    /// measurably slower than the Σοβιετικοί one crossing the same ground.
    /// <para>
    /// The mud is laid through <see cref="SimCommand.UseAbility"/> and the world's own
    /// <c>TryUseAbility</c>, not by writing to the terrain, so this is a test of the loop the
    /// design describes end to end: the ability lays the surface, the surface charges the ground
    /// pressure, and the pressure is the difference between the two columns.
    /// </para>
    /// </summary>
    [Fact]
    public void AWeatherStrikeIsWhatMakesTheDifference()
    {
        (SimWorld world, EntityId[] lanes) = ThreeLanes(TerrainType.Grass);

        EntityId soviet = lanes[0];
        EntityId western = lanes[1];

        AbilityCatalog.TryGet(AbilityId.WeatherControl, out AbilityDefinition weather);

        // The ability's own prerequisites, met the way a player would have met them: the era, the
        // project, the bureau and the materials. The bureau stands well off the lanes, because a
        // Δυτικοί tank that found it in range would spend the whole measurement shooting at it.
        WorldPos stand = world.GetRefBySlot(soviet.Slot).Position;

        world.Spawn(Faction.Soviet, 0, UnitKind.DesignBureau, new WorldPos(stand.X - 150_000, stand.Y, stand.Z), Fix32.Zero, 5_000);

        ref TeamState caster = ref world.TeamRef(0);
        caster.TechTier = weather.RequiredTechTier;
        caster.TechMask |= 1UL << (int)weather.RequiredTech;
        caster.Materials = 10_000;

        Assert.True(world.CanUseAbility(0, AbilityId.WeatherControl, out string reason), $"the weather strike is not available: {reason}");

        // The strike goes down on the ground both columns are about to cross, at the ability's own
        // radius and for its own duration. Everything the mud costs them is the ability's doing.
        WorldPos lane = new(stand.X, stand.Y, stand.Z + 20_000);

        world.Enqueue(SimCommand.UseAbility(AbilityId.WeatherControl, lane, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.TerrainTypes.CountOf(TerrainType.Mud) > 0, "the weather strike laid no mud at all");

        world.RunTicks(200);

        long sovietMm = world.GetRefBySlot(soviet.Slot).DistanceTravelledMm;
        long westernMm = world.GetRefBySlot(western.Slot).DistanceTravelledMm;

        string report =
            $"an Έλεγχος Καιρού struck the lane and both columns drove into it: Σοβιετικοί covered {sovietMm} mm, Δυτικοί {westernMm} mm";

        Assert.True(sovietMm > 0 && westernMm > 0, $"one of the columns never moved. {report}");
        Assert.True(sovietMm > westernMm, $"the weather strike cost the two columns the same, so the ability has no faction to it. {report}");
    }
}
