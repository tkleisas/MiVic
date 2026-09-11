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

    /// <summary>
    /// Πυροβολείο — a gun emplacement: the first structure in the game that shoots.
    /// Long-ranged, slow to reload, and blind to anything in the air.
    /// </summary>
    GunEmplacement = 19,

    /// <summary>
    /// Αντιαεροπορικό Πυροβολείο — an anti-aircraft emplacement: an airfield's worth
    /// of range bottled into one building, and no answer at all to anything on the ground.
    /// </summary>
    AntiAirEmplacement = 20,

    /// <summary>
    /// Σταθμός Ραντάρ — a radar station: an unarmed structure that projects detection
    /// over the ground around it, and draws power for as long as it does. It is the one
    /// structure whose weapon is everybody else's.
    /// </summary>
    RadarStation = 21,
}

/// <summary>
/// The two kinds of thing a faction's engineering philosophy armours, and the third that it does
/// not. Which class a role falls into is answered by <see cref="UnitCatalog.ArmourClassOf"/>.
/// </summary>
public enum ArmourClass : byte
{
    /// <summary>
    /// Nothing of its own: a man on foot. He is not plated, and his protection is the ground he
    /// stands on — which is why cover and armour are two multipliers rather than one.
    /// </summary>
    None = 0,

    /// <summary>A building. Reinforced where it stands, because it cannot choose not to be there.</summary>
    Structure = 1,

    /// <summary>A machine: a hull, a track, a wheel or a wing. Plated, and light where the school says so.</summary>
    Vehicle = 2,
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
/// <param name="StructureArmourPermille">
/// What one hit on this faction's <em>buildings</em> is multiplied by, in permille: 1 000 is
/// nothing at all, 720 is a shot that keeps 72 % of itself, and nothing here can reach zero.
/// </param>
/// <param name="VehicleArmourPermille">
/// The same figure for this faction's <em>machines</em> — anything with a hull, a track, a wheel
/// or a wing, and not a man on foot. See <see cref="UnitCatalog.ArmourClassOf"/>.
/// </param>
/// <remarks>
/// <b>The two armour figures run in opposite directions, and that is the design.</b> Σοβιετικοί
/// buildings are the most reinforced of the three and their machines the least; the Δυτικοί are
/// the other way about — a middleweight building and the heaviest vehicle on the map — and the
/// Κινέζοι build the lightest structures and a hull on a par with the Soviet one. It is one
/// sentence a player can hold: <em>heavy where it does not move, light where it does</em>. A
/// faction that pours its industry into poured concrete and deep reveals has no tonnage left for
/// its tanks, and a faction whose doctrine is a fast armoured thrust spends it the other way.
/// <para>
/// A figure below 1 000 is armour and above it is a role or an owner that is easier to hurt than
/// the baseline — see <see cref="UnitCatalog"/> for the per-role figures that multiply these.
/// The two do not compose as "armour plus armour": they are two permille multipliers on one
/// damage path, and the order they apply in is settled in one place, <see cref="DamageRules"/>.
/// </para>
/// <para>
/// This is not <see cref="TeamState.ArmorPermille"/>, which despite the name is a multiplier on
/// a unit's hit <em>points</em> at the moment it is built — a bigger pool, bought with research.
/// Armour here is damage <em>reduction</em> per hit, which is a different texture: it is what
/// makes a reinforced building something you bring the right weapon for rather than something
/// that merely takes longer to kill.
/// </para>
/// </remarks>
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
    int PropagandaPenaltyRaw = 0,
    int StructureArmourPermille = 1_000,
    int VehicleArmourPermille = 1_000)
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
        IncomePermille: 1_000,
        // The most reinforced buildings in the game and the thinnest hulls. A Soviet base is
        // poured concrete with deep reveals, and a Soviet tank is the same doctrine read the
        // other way: light, wide-tracked, and built to keep moving. Thirty per cent off every
        // hit on a structure is a large number on purpose — it has to be visible in a
        // transcript, and the compensation is on the vehicle figure beside it.
        StructureArmourPermille: 720,
        VehicleArmourPermille: 970);

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
        IncomePermille: 900,
        // The lightest buildings of the three — mass production does not pay for thickness, and
        // a factory that comes off a line in a third of the time is a factory built to a price.
        // Their hulls sit a hair above the Soviet ones, which is the "on a par with the Κινέζοι"
        // half of the design: the two light-tank schools arrive at the same place from opposite
        // directions, one by doctrine and one by economy.
        StructureArmourPermille: 930,
        VehicleArmourPermille: 960);

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
        PropagandaPenaltyRaw: 13_107, // -0.20 when it is not
        // In between on buildings and heaviest on the ground: sixteen per cent off a hit on a
        // structure and fifteen off a hit on a machine. The machine figure is the one that
        // matters, because it is what the Soviet light armoured thrust has to answer — and the
        // answer it is given in return is the rasputitsa, where 1 100 ground pressure against
        // the Soviet 750 costs a Δυτικοί column a third of its speed in mud. Heavy armour is
        // only a weakness if the mud can reach it, which is what <see cref="AbilityId.WeatherControl"/>
        // is for.
        StructureArmourPermille: 840,
        VehicleArmourPermille: 850);

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

    /// <summary>
    /// This faction's figure for one class of thing, or 1 000 for a class it does not armour.
    /// <para>
    /// It exists so that the two figures are selected in one place rather than by a switch at
    /// every call site: a weapon, a blast and an off-map strike all need the same number, and
    /// three copies of "structures use this one, machines use that one" is three chances for the
    /// opposite ordering — which is the whole design — to be written down the wrong way round in
    /// one of them.
    /// </para>
    /// </summary>
    public int ArmourPermilleFor(ArmourClass armourClass) => armourClass switch
    {
        ArmourClass.Structure => StructureArmourPermille,
        ArmourClass.Vehicle => VehicleArmourPermille,
        _ => 1_000,
    };
}
