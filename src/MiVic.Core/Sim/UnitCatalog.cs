namespace MiVic.Core.Sim;

using MiVic.Core.Terrain;

/// <summary>
/// What a unit costs, how long it takes, what it needs before it can be built
/// and how it fights.
/// </summary>
/// <param name="Kind">The role.</param>
/// <param name="MaterialCost">Base material cost before faction modifiers.</param>
/// <param name="EnergyCost">Base energy cost before faction modifiers.</param>
/// <param name="BuildTicks">Base build time before faction modifiers.</param>
/// <param name="Health">Starting hit points.</param>
/// <param name="SpeedMmPerTick">Ground speed in millimetres per tick.</param>
/// <param name="RequiredTechTier">Tech tier a faction must have reached.</param>
/// <param name="ProducedAt">Building role that can build this unit.</param>
/// <param name="IsBuilding">True for structures.</param>
/// <param name="AttackDamage">Damage per shot; zero for unarmed roles.</param>
/// <param name="AttackRangeMm">Maximum firing range in millimetres.</param>
/// <param name="AttackCooldownTicks">Ticks between shots.</param>
/// <param name="CanHitAir">Whether this role can engage aircraft.</param>
/// <param name="WaterCost">Water the unit consumes when it is built.</param>
/// <param name="SplashRadiusMm">Radius around the impact point that also takes damage; zero for single-target weapons.</param>
/// <param name="ScatterMm">Maximum distance the shot lands from its target; zero for accurate weapons.</param>
/// <param name="MoraleAuraRaw">Morale bonus this unit grants to nearby friends, in Q16.16 raw units.</param>
/// <param name="Movement">How the unit travels, which is what terrain costs key on.</param>
/// <param name="GroundPressurePermille">Nominal ground pressure for this role, 1000 being baseline.</param>
/// <param name="IsAutomaton">
/// True for unmanned hardware. Automata have no morale at all: they never rout and
/// they get no morale-driven reload bonus, because there is nobody aboard to steady
/// or to break.
/// </param>
/// <param name="OnlyFor">
/// The one faction that may build this role, or <see cref="Faction.None"/> for the
/// shared roster. Most roles are shared; robots and drones are Κινέζοι only.
/// </param>
/// <param name="WagePerTick">
/// Materials this unit costs every tick just to keep. Contract troops stop fighting
/// when the money stops; everyone else has no wage.
/// </param>
/// <param name="Stealthy">
/// True when the unit is invisible to the enemy until it fires or is detected at
/// close range. You cannot shoot what you cannot see.
/// </param>
/// <param name="MaxAlive">
/// How many of these a team may have at once, counting those already queued. Zero
/// means unlimited. This is what makes a prototype a prototype: the technology
/// cannot be mass-produced, so losing one is a real loss.
/// </param>
/// <param name="RequiredTech">Project a team must have completed before it may build this role.</param>
/// <param name="FootprintRadiusCells">
/// The ground the role needs around the cell it is placed on, as a radius in navigation cells: the
/// building is judged on a square patch <c>(2r+1)</c> cells on a side, and two structures may not
/// share one. Zero for anything that drives or flies — a unit needs no ground of its own, and a
/// structure that asked for none would be a building standing on a single cell with its walls in
/// the next one.
/// </param>
/// <param name="CanHitGround">
/// Whether this role can engage anything that is not airborne. True for nearly everything, and the
/// mirror of <paramref name="CanHitAir"/>: a weapon that can only reach into the air is the other
/// half of the same fact, and an anti-aircraft mount that fires at tanks is a bug rather than a
/// bonus. A role that could hit neither would be unarmed, which is a different field.
/// </param>
public readonly record struct UnitDefinition(
    UnitKind Kind,
    int MaterialCost,
    int EnergyCost,
    int BuildTicks,
    int Health,
    int SpeedMmPerTick,
    int RequiredTechTier,
    UnitKind ProducedAt,
    bool IsBuilding,
    int AttackDamage = 0,
    int AttackRangeMm = 0,
    int AttackCooldownTicks = 0,
    bool CanHitAir = false,
    int WaterCost = 0,
    int SplashRadiusMm = 0,
    int ScatterMm = 0,
    int MoraleAuraRaw = 0,
    MovementClass Movement = MovementClass.Foot,
    int GroundPressurePermille = 1_000,
    bool IsAutomaton = false,
    Faction OnlyFor = Faction.None,
    int WagePerTick = 0,
    bool Stealthy = false,
    int MaxAlive = 0,
    TechId RequiredTech = TechId.None,
    int FootprintRadiusCells = 0,
    bool CanHitGround = true)
{
    /// <summary>True when the role can shoot at anything.</summary>
    public bool IsArmed => AttackDamage > 0 && AttackRangeMm > 0;

    /// <summary>True when the weapon damages everything near the impact point.</summary>
    public bool HasSplash => SplashRadiusMm > 0;

    /// <summary>True when this unit steadies the morale of nearby friends.</summary>
    public bool HasMoraleAura => MoraleAuraRaw > 0;

    /// <summary>Damage per tick, for balance comparisons.</summary>
    public readonly float DamagePerTick => AttackCooldownTicks > 0 ? (float)AttackDamage / AttackCooldownTicks : 0f;
}

/// <summary>
/// The catalogue of everything that can be built, with the faction modifiers
/// from <see cref="FactionProfile"/> applied at query time.
/// <para>
/// This is where the asymmetry becomes concrete. The same tank costs
/// <c>MaterialCost * CostPermille / 1000</c> and takes
/// <c>BuildTicks * 1000 / BuildSpeedPermille</c> ticks, so the Σοβιετικοί pay
/// more for each hull and wait longer, while the Κινέζοι build cheap and fast but
/// cannot reach the higher tech tiers at all.
/// </para>
/// </summary>
public static class UnitCatalog
{
    private static readonly UnitDefinition[] Definitions =
    [
        // Roles. Cost, energy, ticks, health, speed, tier, produced at, building,
        // damage, range mm, cooldown ticks, can hit air, water, then movement and
        // ground pressure — which is what the terrain layer charges for.
        new(UnitKind.Infantry, 50, 0, 60, 100, 100, 1, UnitKind.CommandCentre, false, 8, 90_000, 10, false, 12,
            Movement: MovementClass.Foot, GroundPressurePermille: 900),
        new(UnitKind.Tank, 150, 20, 120, 320, 400, 2, UnitKind.Factory, false, 35, 110_000, 24, false, 18,
            Movement: MovementClass.Tracked, GroundPressurePermille: 1_000),
        new(UnitKind.Artillery, 180, 30, 140, 210, 300, 2, UnitKind.Factory, false, 60, 220_000, 60, false, 22,
            Movement: MovementClass.Tracked, GroundPressurePermille: 1_200),
        new(UnitKind.AntiAir, 120, 20, 100, 190, 350, 2, UnitKind.Factory, false, 25, 150_000, 16, true, 16,
            Movement: MovementClass.Tracked, GroundPressurePermille: 1_000),
        new(UnitKind.Aircraft, 260, 60, 200, 160, 1_500, 3, UnitKind.Factory, false, 30, 100_000, 20, true, 45,
            Movement: MovementClass.Air, GroundPressurePermille: 0),

        // Συλλέκτης: the role that makes a deposit worth anything. It has no weapon
        // and no place in a fight — its whole job is to sit on ore, which is why it
        // is the unit an opponent raids rather than shoots.
        new(UnitKind.Harvester, 200, 20, 150, 300, 260, 1, UnitKind.CommandCentre, false,
            0, 0, 0, false, 25,
            Movement: MovementClass.Wheeled, GroundPressurePermille: 1_100),

        // Κατιούσα: one salvo is worth more than a howitzer's, but it lands
        // scattered over an area. Devastating against formations and buildings,
        // poor against a single moving tank — which is why the Σοβιετικοί want
        // the enemy to come to them in the open.
        new(UnitKind.RocketArtillery, 170, 25, 130, 160, 260, 2, UnitKind.Factory, false,
            95, 260_000, 90, false, 24, SplashRadiusMm: 22_000, ScatterMm: 26_000,
            Movement: MovementClass.Wheeled, GroundPressurePermille: 1_100),

        // Κομισάριος: unarmed, cheap and worth killing. Steadies the morale of
        // friends around it; the initiative cost is not modelled yet.
        new(UnitKind.Commissar, 60, 0, 50, 90, 110, 1, UnitKind.CommandCentre, false,
            WaterCost: 10, MoraleAuraRaw: 6_554, Movement: MovementClass.Foot, GroundPressurePermille: 900),

        // Κινέζοι automata: the faction cannot out-tech anyone, so its advanced
        // hardware is machines instead of people. No morale, no crews to feed, and
        // an energy bill instead of a water one.
        new(UnitKind.RobotInfantry, 70, 25, 70, 110, 130, 3, UnitKind.Factory, false,
            12, 95_000, 12, false, 0,
            Movement: MovementClass.Foot, GroundPressurePermille: 950,
            IsAutomaton: true, OnlyFor: Faction.Chinese),
        new(UnitKind.Drone, 90, 30, 90, 70, 1_300, 3, UnitKind.Factory, false,
            14, 80_000, 14, false, 0,
            Movement: MovementClass.Air, GroundPressurePermille: 0,
            IsAutomaton: true, OnlyFor: Faction.Chinese),

        // Μισθοφόρος: the best infantry in the game, and the only unit that has to
        // be paid every tick to keep fighting. Hired, not trained — so it needs no
        // research, only money.
        new(UnitKind.Mercenary, 140, 0, 80, 140, 130, 1, UnitKind.CommandCentre, false,
            14, 100_000, 10, false, 14,
            Movement: MovementClass.Foot, GroundPressurePermille: 1_000,
            OnlyFor: Faction.Western, WagePerTick: 1),

        // Καταδρομέας: the Δυτικοί era-V edge. Fast, hard-hitting, fragile, and
        // invisible until it opens fire — you cannot shoot what you cannot see.
        new(UnitKind.StealthRecon, 200, 20, 120, 120, 420, 5, UnitKind.Factory, false,
            40, 120_000, 20, false, 18,
            Movement: MovementClass.Foot, GroundPressurePermille: 850,
            OnlyFor: Faction.Western, Stealthy: true),

        // Ηλεκτροπυροβόλο: the payoff of Ηλεκτροτεχνία, and the thing the two-tier
        // cost was decided for. It hits harder than anything else on the field and
        // a team may have at most two, counting the one in the queue — so it is a
        // capability rather than a unit type, and losing one is a campaign loss.
        new(UnitKind.ElectroPrototype, 320, 60, 260, 220, 280, 2, UnitKind.Factory, false,
            110, 240_000, 70, false, 40,
            SplashRadiusMm: 16_000, Movement: MovementClass.Tracked, GroundPressurePermille: 1_000,
            OnlyFor: Faction.Soviet, MaxAlive: 2, RequiredTech: TechId.SovietElectro),

        // Structures were unarmed until the emplacements below arrived; industry still
        // carries no gun, because a factory that could shoot would be a factory nobody
        // has to defend. Industry needs a great deal of water, which is what makes a
        // second command centre or power plant a real economic decision.
        //
        // The last number on each of them is its footprint: the radius, in navigation cells, of the
        // square of ground the building stands on. The number comes from how big the buildings
        // actually are. A navigation cell is 9 375 mm across, and the models were fitted to longest
        // sides of 12 m (a power plant), 14 m (a design bureau), 16 m (a factory) and 20 to 22 m (a
        // command centre) — so one cell is not enough for any of them: a 16 m factory is wider than
        // the 9.4 m cell it would stand on. One cell of radius is 28 m of ground, the smallest square
        // of cells that contains every building in the game, and it is what they get; anything more
        // would be a parade square rather than the ground under a building. Only the nuclear plant
        // asks for more, and not because of its walls — see below.
        new(UnitKind.CommandCentre, 600, 0, 400, 5_000, 0, 1, UnitKind.CommandCentre, true, WaterCost: 180,
            FootprintRadiusCells: 1),
        new(UnitKind.PowerPlant, 160, 0, 200, 1_200, 0, 1, UnitKind.CommandCentre, true, WaterCost: 90,
            FootprintRadiusCells: 1),
        new(UnitKind.Factory, 280, 0, 300, 2_000, 0, 1, UnitKind.CommandCentre, true, WaterCost: 120,
            FootprintRadiusCells: 1),
        new(UnitKind.DesignBureau, 320, 0, 320, 1_500, 0, 1, UnitKind.CommandCentre, true, WaterCost: 100,
            FootprintRadiusCells: 1),

        // The nuclear plant is the one structure whose value is not its income: it
        // is the prerequisite for a tactical nuclear weapon, so it is worth raiding.
        // The Κινέζοι can never build it — their ceiling stops at era III. It is also the one
        // building whose footprint is larger than its own walls, which is the whole difference
        // between it and the power plant it is an upgrade of: a reactor is 22 m of containment
        // ring, cooling pond and exclusion zone, and the ground that has to be clear around it is
        // 5 × 5 cells. That is why a nuclear plant and a power plant are not interchangeable sites
        // even though both are one building with one job.
        new(UnitKind.NuclearPlant, 900, 0, 600, 4_000, 0, 4, UnitKind.CommandCentre, true, WaterCost: 320,
            FootprintRadiusCells: 2),

        // Πυροβολείο: the first structure in the game with a gun on it, and deliberately the
        // weakest thing a defensive line can be made of. Forty-five damage every two and a half
        // seconds is *less* than a tank's sustained fire and less than a Κατιούσα's, it cannot
        // move, and it cannot touch anything in the air — so a player who buys one instead of a
        // tank is buying reach and nothing else. Reach is what makes it worth having: 200 m is
        // further than any tank can shoot back from (110 m) and further than a mobile anti-air
        // mount reaches, so an emplacement covers an approach that a hull cannot. That is the
        // whole of the decision the placement rule created — the same gun on a ridge sees the
        // road for two hundred metres and the same gun in a basin sees a hillside.
        //
        // An entry in the tech tree that means something: this one is at era I, so it can be up
        // inside the first minute, and it is priced to be a real purchase early on (180 Π is most
        // of a command centre's opening stockpile) rather than something to sprinkle. What it is
        // not is a gun behind research: a player who has not researched anything yet has no
        // aeroplanes to fear and no armour of their own to protect, so the cheap turret is the
        // right shape for the first minute.
        new(UnitKind.GunEmplacement, 180, 30, 180, 1_400, 0, 1, UnitKind.CommandCentre, true,
            45, 200_000, 50, false, 50,
            FootprintRadiusCells: 1),

        // Αντιαεροπορικό Πυροβολείο: the same building with the other half of the problem in
        // mind, and worth waiting for. It is behind each faction's own «Επίπεδο 2» project — the
        // research every faction has to do anyway to reach the armour era — which is the honest
        // place for it: aircraft do not exist until era III, so a player cannot be caught without
        // one, and a player who has just spent five hundred ticks on an era has something to show
        // for it.
        //
        // What the wait buys, against the mobile Αντιαεροπορικό the factions can already build at
        // era II: thirty metres more range (180 m against 150), a faster reload (12 ticks against
        // 16) and a heavier shell (30 against 25), and it never has to be driven anywhere. A
        // mobile mount has to be *at* the raid when the raid arrives; an emplacement is already
        // there, which is the difference between a defence and a reaction.
        //
        // It cannot shoot at anything on the ground at all, and says so in the catalogue rather
        // than in a comment: CanHitGround false. An anti-aircraft mount that kills tanks is not a
        // strong anti-aircraft mount, it is the tank's replacement.
        new(UnitKind.AntiAirEmplacement, 200, 45, 200, 1_200, 0, 2, UnitKind.CommandCentre, true,
            30, 180_000, 12, true, 60, CanHitGround: false,
            FootprintRadiusCells: 1),

        // Σταθμός Ραντάρ: the first structure in the game with no gun on it whose whole
        // purpose is what the guns around it can do.
        //
        // What it sells is reach. A gun emplacement sees 170 m and shoots 200; inside a radar's
        // coverage it shoots the 200 it was built for, because the radar is what is looking. So a
        // radar is never bought for its own sake — it is bought because a defensive line is
        // several guns short of their range without one, and the enemy knows it.
        //
        // It is deliberately unarmed. A radar with a gun would be a gun emplacement that also
        // multiplies the other guns, which is not a decision, it is a purchase everyone makes. It
        // is also deliberately fragile (900 hit points, less than a power plant) and it is priced
        // above the emplacement it serves, because the thing worth raiding should be the thing
        // that costs the raider something to ignore — see PowerSystem, where the same building is
        // the first load a base sheds when its generation is short.
        //
        // Era I, like the gun emplacement, and for the same reason: detection is the *first*
        // problem, not a reward for having solved the others. A player who has just built a gun
        // and cannot work out why it is not shooting at the tank 190 m away has a radar to build,
        // and can afford neither the wait nor the research.
        new(UnitKind.RadarStation, 240, 40, 220, 900, 0, 1, UnitKind.CommandCentre, true, WaterCost: 70,
            FootprintRadiusCells: 1),
    ];

    /// <summary>Every defined role.</summary>
    public static ReadOnlySpan<UnitDefinition> All => Definitions;

    /// <summary>Looks up a definition. Throws for an unknown role.</summary>
    public static UnitDefinition Get(UnitKind kind)
        => TryGet(kind, out UnitDefinition definition)
            ? definition
            : throw new ArgumentOutOfRangeException(nameof(kind), kind, "No definition for this role.");

    /// <summary>Looks up a definition.</summary>
    public static bool TryGet(UnitKind kind, out UnitDefinition definition)
    {
        foreach (UnitDefinition candidate in Definitions)
        {
            if (candidate.Kind == kind)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    /// <summary>Material cost for a faction, after its cost multiplier.</summary>
    public static int MaterialCost(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = Get(kind);
        return Scale(definition.MaterialCost, FactionProfile.For(faction).CostPermille);
    }

    /// <summary>Energy cost for a faction, after its cost multiplier.</summary>
    public static int EnergyCost(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = Get(kind);
        return Scale(definition.EnergyCost, FactionProfile.For(faction).CostPermille);
    }

    /// <summary>Water cost for a faction, after its cost multiplier.</summary>
    public static int WaterCost(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = Get(kind);
        return Scale(definition.WaterCost, FactionProfile.For(faction).CostPermille);
    }

    /// <summary>Build time in ticks for a faction, after its build-speed multiplier.</summary>
    public static int BuildTicks(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = Get(kind);
        int speed = FactionProfile.For(faction).BuildSpeedPermille;
        int ticks = (definition.BuildTicks * 1_000) / Math.Max(1, speed);
        return Math.Max(1, ticks);
    }

    /// <summary>
    /// Effective ground pressure for a faction's version of a role: the role's
    /// baseline scaled by the faction's design philosophy. This is what makes a
    /// light, wide-tracked hull cheap to move through mud and a heavy one expensive.
    /// </summary>
    public static int GroundPressure(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = Get(kind);

        if (definition.Movement == MovementClass.Air || definition.IsBuilding)
        {
            return 0;
        }

        int factionPressure = FactionProfile.For(faction).GroundPressurePermille;
        return (definition.GroundPressurePermille * (factionPressure > 0 ? factionPressure : 1_000)) / 1_000;
    }

    /// <summary>True when a role is airborne: it flies over terrain and only anti-air can hit it.</summary>
    public static bool Flies(UnitKind kind) => TryGet(kind, out UnitDefinition definition)
        && definition.Movement == MovementClass.Air;

    /// <summary>
    /// How much ground a role needs around the cell it is placed on, as a radius in navigation
    /// cells — the footprint the placement rule and the occupancy rule are both measured with.
    /// <para>
    /// It lives in the catalogue because it is a per-role fact, like cost and speed: a nuclear plant
    /// and a power plant are not the same undertaking, and a table of footprint numbers in the
    /// placement code would be a second roster that could disagree with this one. An unknown role
    /// answers zero, which is what a unit's footprint is: none.
    /// </para>
    /// </summary>
    public static int FootprintRadiusCells(UnitKind kind)
        => TryGet(kind, out UnitDefinition definition) ? definition.FootprintRadiusCells : 0;

    /// <summary>True when a role is unmanned: no morale, no crews, no water.</summary>
    public static bool IsAutomaton(UnitKind kind) => TryGet(kind, out UnitDefinition definition)
        && definition.IsAutomaton;

    /// <summary>
    /// True when a faction at <paramref name="techTier"/>, with
    /// <paramref name="techMask"/> of completed projects, may build the role. This
    /// is what stops the Κινέζοι from ever fielding tier-4 hardware, what keeps
    /// their automata out of everyone else's hands, and what holds a prototype back
    /// until the research behind it exists.
    /// </summary>
    public static bool IsUnlocked(Faction faction, UnitKind kind, int techTier, ulong techMask = 0)
    {
        if (!TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        if (definition.OnlyFor != Faction.None && definition.OnlyFor != faction)
        {
            return false;
        }

        if (definition.RequiredTech != TechId.None && !TechCatalog.IsCompleted(techMask, definition.RequiredTech))
        {
            return false;
        }

        FactionProfile profile = FactionProfile.For(faction);

        return definition.RequiredTechTier <= techTier && definition.RequiredTechTier <= profile.TechCeiling;
    }

    /// <summary>Everything a faction can eventually build, in catalogue order.</summary>
    public static IEnumerable<UnitDefinition> BuildableBy(Faction faction)
    {
        FactionProfile profile = FactionProfile.For(faction);

        foreach (UnitDefinition definition in Definitions)
        {
            if (definition.RequiredTechTier <= profile.TechCeiling &&
                (definition.OnlyFor == Faction.None || definition.OnlyFor == faction))
            {
                yield return definition;
            }
        }
    }

    /// <summary>
    /// The player-facing name of a role, in Greek, as the panels write it.
    /// <para>
    /// It lives here because the simulation needs it too: a refusal that names what is missing
    /// — "χρειάζεται κέντρο διοίκησης" — is a refusal in the same voice as "λείπουν 120 Π",
    /// and a reason assembled from an enum name would be the only English sentence in the
    /// player's interface. The client's own labels come from here as well, so there is one
    /// list of names rather than two that can drift apart.
    /// </para>
    /// </summary>
    public static string GreekName(UnitKind kind) => kind switch
    {
        UnitKind.Infantry => "Πεζικό",
        UnitKind.Tank => "Άρμα",
        UnitKind.Artillery => "Πυροβολικό",
        UnitKind.RocketArtillery => "Κατιούσα",
        UnitKind.Commissar => "Κομισάριος",
        UnitKind.AntiAir => "Αντιαεροπορικό",
        UnitKind.Aircraft => "Αεροσκάφος",
        UnitKind.Drone => "Ντρόουν",
        UnitKind.RobotInfantry => "Ρομποτικό Πεζικό",
        UnitKind.Mercenary => "Μισθοφόρος",
        UnitKind.StealthRecon => "Καταδρομέας",
        UnitKind.ElectroPrototype => "Ηλεκτροπυροβόλο",
        UnitKind.Harvester => "Συλλέκτης",
        UnitKind.CommandCentre => "Κέντρο Διοίκησης",
        UnitKind.PowerPlant => "Σταθμός Παραγωγής",
        UnitKind.NuclearPlant => "Πυρηνικός Σταθμός",
        UnitKind.Factory => "Εργοστάσιο",
        UnitKind.DesignBureau => "Γραφείο Σχεδιασμού",
        UnitKind.GunEmplacement => "Πυροβολείο",
        UnitKind.AntiAirEmplacement => "Αντιαεροπορικό Πυροβολείο",
        UnitKind.RadarStation => "Σταθμός Ραντάρ",
        _ => "Άγνωστο",
    };

    private static int Scale(int value, int permille) => (value * permille) / 1_000;
}
