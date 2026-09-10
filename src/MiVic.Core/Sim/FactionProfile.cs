namespace MiVic.Core.Sim;

/// <summary>
/// The three powers of the alternate future. Values are explicit and stable
/// because they are written into replays and saved games.
/// </summary>
public enum Faction : byte
{
    /// <summary>Neutral, uncaptured or decorative.</summary>
    None = 0,

    /// <summary>Σοβιετικοί — rugged, cheap, mobile designs; advanced weapons are few and gated.</summary>
    Soviet = 1,

    /// <summary>Κινέζοι — immense manufacturing, lagging technology.</summary>
    Chinese = 2,

    /// <summary>Δυτικοί — abundant resources and the best technology, but the lowest production throughput; undermined by greed.</summary>
    Western = 3,
}

/// <summary>
/// Coarse unit roles. The M0 slice only needs the classification and its
/// movement envelope; full stat blocks arrive with the data layer in M2.
/// </summary>
public enum UnitKind : byte
{
    None = 0,
    Infantry = 1,
    Tank = 2,
    Artillery = 3,
    AntiAir = 4,
    Aircraft = 5,
    Harvester = 6,

    /// <summary>Headquarters. Produces infantry, generates materials.</summary>
    CommandCentre = 7,

    /// <summary>Generates energy.</summary>
    PowerPlant = 8,

    /// <summary>Produces vehicles. Parallel slots are a faction trait.</summary>
    Factory = 9,

    /// <summary>Researches tech tiers. The Σοβιετικοί design bureau.</summary>
    DesignBureau = 10,

    /// <summary>Κατιούσα — rocket artillery: heavy area damage, poor accuracy.</summary>
    RocketArtillery = 11,

    /// <summary>Κομισάριος — unarmed support that steadies the morale of nearby units.</summary>
    Commissar = 12,

    /// <summary>Ρομποτικό Πεζικό — Κινέζοι automaton infantry: no morale, no crews.</summary>
    RobotInfantry = 13,

    /// <summary>Ντρόουν — Κινέζοι expendable unmanned aircraft.</summary>
    Drone = 14,

    /// <summary>Μισθοφόρος — Δυτικοί contract infantry, paid by the tick.</summary>
    Mercenary = 15,

    /// <summary>
    /// Πυρηνικός Σταθμός — nuclear power plant. Produces a great deal of energy,
    /// and is the prerequisite for a tactical nuclear weapon.
    /// </summary>
    NuclearPlant = 16,

    /// <summary>Καταδρομέας — Δυτικοί stealth raider: invisible until it fires.</summary>
    StealthRecon = 17,

    /// <summary>Ηλεκτροπυροβόλο — Σοβιετικοί electro prototype. Capped, and expensive to lose.</summary>
    ElectroPrototype = 18,
}

/// <summary>
/// The asymmetric balance of a faction, expressed as data rather than special
/// cases, so that the numbers live in one auditable place.
/// </summary>
/// <remarks>
/// The values below are the pre-decision first pass. <c>docs/DESIGN.md</c> §7
/// records a decided replacement that is not yet applied: production ordering
/// Κινέζοι &gt; Σοβιετικοί &gt; Δυτικοί, Δυτικοί rich but least productive, cheap
/// two-tier Σοβιετικοί hulls, and a new <c>IncomePermille</c> applied to per-tick
/// income. Apply it here and regenerate the golden state hashes in the same change,
/// because every income and cost number feeds the hash.
/// </remarks>
/// <param name="Faction">Which power this describes.</param>
/// <param name="GreekName">Player-facing name, in Greek.</param>
/// <param name="ProductionSlots">Parallel production a faction can sustain.</param>
/// <param name="BuildSpeedPermille">Build-rate multiplier, in thousandths.</param>
/// <param name="TechCeiling">Highest tech tier reachable.</param>
/// <param name="MoraleFloor">Baseline morale, 0..1 fixed point.</param>
/// <param name="CostPermille">Unit cost multiplier, in thousandths.</param>
/// <param name="ResearchSpeedPermille">Research rate multiplier, in thousandths.</param>
/// <param name="GroundPressurePermille">
/// Design-philosophy multiplier on every mobile role's ground pressure. Below 1000
/// means a light hull on wide tracks, which is what keeps an army moving in mud.
/// </param>
/// <param name="IncomePermille">
/// Multiplier on what a team's structures <em>produce</em> per tick, in thousandths.
/// Above 1000 means the faction is rich; it is deliberately separate from
/// <paramref name="CostPermille"/> so a faction can be wealthy without being able to
/// turn that wealth into units quickly.
/// </param>
/// <param name="PropagandaDivisor">
/// Armed units per point of propaganda upkeep per tick. Zero means the faction does
/// not pay for its army's willingness to fight.
/// </param>
/// <param name="PropagandaBonusRaw">Morale, in Q16.16 raw units, while propaganda is funded.</param>
/// <param name="PropagandaPenaltyRaw">Morale lost while it is not.</param>
public readonly record struct FactionProfile(
    Faction Faction,
    string GreekName,
    int ProductionSlots,
    int BuildSpeedPermille,
    int TechCeiling,
    MiVic.Core.Numerics.Fix32 MoraleFloor,
    int CostPermille,
    int ResearchSpeedPermille,
    int GroundPressurePermille = 1_000,
    int IncomePermille = 1_000,
    int PropagandaDivisor = 0,
    int PropagandaBonusRaw = 0,
    int PropagandaPenaltyRaw = 0)
{
    /// <summary>
    /// The three playable powers, ordered so that iteration is deterministic.
    /// Numbers are first-pass design values from the M0 design pass and are
    /// expected to move during balance work.
    /// </summary>
    public static readonly FactionProfile Soviet = new(
        Faction.Soviet,
        "Σοβιετικοί",
        ProductionSlots: 4,
        BuildSpeedPermille: 1_100,
        TechCeiling: 4,
        MoraleFloor: MiVic.Core.Numerics.Fix32.FromRaw(58982), // 0.90
        CostPermille: 900,
        ResearchSpeedPermille: 1250,
        GroundPressurePermille: 750,
        IncomePermille: 1_000);

    public static readonly FactionProfile Chinese = new(
        Faction.Chinese,
        "Κινέζοι",
        ProductionSlots: 6,
        BuildSpeedPermille: 1_500,
        TechCeiling: 3,
        MoraleFloor: MiVic.Core.Numerics.Fix32.FromRaw(52428), // 0.80
        CostPermille: 700,
        ResearchSpeedPermille: 700,
        GroundPressurePermille: 1250,
        IncomePermille: 900);

    public static readonly FactionProfile Western = new(
        Faction.Western,
        "Δυτικοί",
        ProductionSlots: 3,
        BuildSpeedPermille: 800,
        TechCeiling: 5,
        MoraleFloor: MiVic.Core.Numerics.Fix32.FromRaw(32768), // 0.50
        CostPermille: 2_200,
        ResearchSpeedPermille: 1000,
        GroundPressurePermille: 1100,
        IncomePermille: 2_500,
        PropagandaDivisor: 8,
        PropagandaBonusRaw: 6_554,   // +0.10 while the propaganda budget is paid
        PropagandaPenaltyRaw: 13_107); // -0.20 when it is not

    /// <summary>All playable factions in stable order.</summary>
    public static readonly FactionProfile[] All = [Soviet, Chinese, Western];

    /// <summary>Looks up a profile. Throws for <see cref="Faction.None"/>.</summary>
    public static FactionProfile For(Faction faction) => faction switch
    {
        Faction.Soviet => Soviet,
        Faction.Chinese => Chinese,
        Faction.Western => Western,
        _ => throw new ArgumentOutOfRangeException(nameof(faction), faction, "No profile for this faction."),
    };
}
