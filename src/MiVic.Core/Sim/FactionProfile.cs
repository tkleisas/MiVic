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
public readonly record struct FactionProfile(
    Faction Faction,
    string GreekName,
    int ProductionSlots,
    int BuildSpeedPermille,
    int TechCeiling,
    MiVic.Core.Numerics.Fix32 MoraleFloor,
    int CostPermille,
    int ResearchSpeedPermille)
{
    /// <summary>
    /// The three playable powers, ordered so that iteration is deterministic.
    /// Numbers are first-pass design values from the M0 design pass and are
    /// expected to move during balance work.
    /// </summary>
    public static readonly FactionProfile Soviet = new(
        Faction.Soviet,
        "Σοβιετικοί",
        ProductionSlots: 2,
        BuildSpeedPermille: 750,
        TechCeiling: 4,
        MoraleFloor: MiVic.Core.Numerics.Fix32.FromRaw(58982), // 0.90
        CostPermille: 1350,
        ResearchSpeedPermille: 1250);

    public static readonly FactionProfile Chinese = new(
        Faction.Chinese,
        "Κινέζοι",
        ProductionSlots: 6,
        BuildSpeedPermille: 1500,
        TechCeiling: 2,
        MoraleFloor: MiVic.Core.Numerics.Fix32.FromRaw(52428), // 0.80
        CostPermille: 700,
        ResearchSpeedPermille: 700);

    public static readonly FactionProfile Western = new(
        Faction.Western,
        "Δυτικοί",
        ProductionSlots: 4,
        BuildSpeedPermille: 1200,
        TechCeiling: 5,
        MoraleFloor: MiVic.Core.Numerics.Fix32.FromRaw(32768), // 0.50
        CostPermille: 1100,
        ResearchSpeedPermille: 1000);

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
