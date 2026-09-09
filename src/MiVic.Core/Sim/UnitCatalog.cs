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
    int GroundPressurePermille = 1_000)
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

        // Structures are unarmed for now; defensive buildings come with M3 balance.
        // Industry needs a great deal of water, which is what makes a second
        // command centre or power plant a real economic decision.
        new(UnitKind.CommandCentre, 600, 0, 400, 5_000, 0, 1, UnitKind.CommandCentre, true, WaterCost: 180),
        new(UnitKind.PowerPlant, 160, 0, 200, 1_200, 0, 1, UnitKind.CommandCentre, true, WaterCost: 90),
        new(UnitKind.Factory, 280, 0, 300, 2_000, 0, 1, UnitKind.CommandCentre, true, WaterCost: 120),
        new(UnitKind.DesignBureau, 320, 0, 320, 1_500, 0, 1, UnitKind.CommandCentre, true, WaterCost: 100),
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

    /// <summary>
    /// True when a faction at <paramref name="techTier"/> may build the role.
    /// This is what stops the Κινέζοι from ever fielding tier-3 hardware.
    /// </summary>
    public static bool IsUnlocked(Faction faction, UnitKind kind, int techTier)
    {
        if (!TryGet(kind, out UnitDefinition definition))
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
            if (definition.RequiredTechTier <= profile.TechCeiling)
            {
                yield return definition;
            }
        }
    }

    private static int Scale(int value, int permille) => (value * permille) / 1_000;
}
