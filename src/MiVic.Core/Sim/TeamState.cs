namespace MiVic.Core.Sim;

/// <summary>
/// Stockpiles and research progress for one team.
/// <para>
/// Resources are per team rather than per faction so the 2v1 slice can model a
/// player, an ally and an enemy without special cases.
/// </para>
/// </summary>
public struct TeamState
{
    /// <summary>Πόροι — the material stockpile.</summary>
    public int Materials;

    /// <summary>Ενέργεια — the energy stockpile.</summary>
    public int Energy;

    /// <summary>Νερό — water. Every construction job and every infantryman drinks it.</summary>
    public int Water;

    /// <summary>Materials produced per tick by this team's structures.</summary>
    public int MaterialsPerTick;

    /// <summary>Net energy per tick: generation minus structure upkeep.</summary>
    public int EnergyPerTick;

    /// <summary>Water produced per tick by this team's structures.</summary>
    public int WaterPerTick;

    /// <summary>Highest tech tier reached. Gates what may be built.</summary>
    public int TechTier;

    /// <summary>Ticks left on the research in progress; zero when idle.</summary>
    public int ResearchTicksRemaining;

    /// <summary>Ticks the current research project originally required.</summary>
    public int ResearchTicksTotal;

    /// <summary>Tier the current project will unlock.</summary>
    public int ResearchTargetTier;

    /// <summary>
    /// Friendly units lost in the last few ticks, weighted by decay. This is what
    /// makes a bad engagement cascade: losses in one place lower morale nearby.
    /// </summary>
    public int RecentCasualties;

    /// <summary>
    /// Bitmask of <see cref="UnitKind"/> values this team may build under licence.
    /// A licensed design bypasses the faction's tech ceiling, which is exactly
    /// how the Κινέζοι get high-tier hardware they could never research.
    /// </summary>
    public uint LicenceMask;

    /// <summary>Bitmask of completed <see cref="TechId"/> projects.</summary>
    public ulong TechMask;

    /// <summary>
    /// Bitmask of <see cref="UnitKind"/> designs a Σοβιετικοί team has approved for
    /// ordinary factory production. A design must first be run as a prototype at
    /// the design bureau; until then factories cannot build it. This is the
    /// faction's signature mechanic: the army cannot pivot.
    /// </summary>
    public uint ApprovedMask;

    /// <summary>Design currently being prototyped at the bureau, if any.</summary>
    public UnitKind PrototypeKind;

    /// <summary>Ticks left on the prototype run; zero when idle.</summary>
    public int PrototypeTicksRemaining;

    /// <summary>Ticks the prototype run originally required.</summary>
    public int PrototypeTicksTotal;

    /// <summary>Project currently being researched.</summary>
    public TechId ResearchingTech;

    /// <summary>Weapon damage multiplier in thousandths; 1000 is baseline.</summary>
    public int DamagePermille;

    /// <summary>Hit-point multiplier in thousandths; 1000 is baseline.</summary>
    public int ArmorPermille;

    /// <summary>Speed multiplier in thousandths; 1000 is baseline.</summary>
    public int SpeedPermille;

    /// <summary>Production-speed multiplier in thousandths; 1000 is baseline.</summary>
    public int ProductionPermille;

    /// <summary>Sight-radius multiplier in thousandths; 1000 is baseline.</summary>
    public int VisionPermille;

    /// <summary>
    /// Multiplier on the mud and snow penalty in thousandths; 1000 is baseline and
    /// 500 means the team's units pay half. This is what "Βαθιά Μάχη" buys.
    /// </summary>
    public int TerrainResistancePermille;

    /// <summary>Extra parallel production slots per building, from command automation.</summary>
    public int BonusSlots;

    /// <summary>
    /// Morale gained per nearby friendly unit, in Q16.16 raw units. This is the
    /// Κινέζοι answer to per-unit morale: the swarm is steadier than its parts.
    /// </summary>
    public int CohesionPerFriendRaw;

    /// <summary>True when this tick's propaganda budget was paid.</summary>
    public bool PropagandaPaid;

    /// <summary>True when this tick's contract wages were paid.</summary>
    public bool WagesPaid;

    /// <summary>Materials this team owed this tick: propaganda plus contract wages.</summary>
    public int UpkeepPerTick;

    /// <summary>Live armed units, recomputed by the economy each tick.</summary>
    public int ArmedCount;

    /// <summary>Materials owed per tick to contract units, recomputed each tick.</summary>
    public int WagesPerTick;

    /// <summary>
    /// Tick each ability becomes available again, indexed by
    /// <see cref="AbilityCatalog.IndexOf"/>. Allocated by the world, one array per
    /// team, because the number of abilities is data.
    /// </summary>
    public long[] AbilityReadyTick;

    /// <summary>Morale floor bonus in Q16.16 raw units.</summary>
    public int MoraleBonusRaw;

    /// <summary>
    /// Structures this team has lost since the world was created. Mission
    /// objectives count this rather than comparing against a starting total, so
    /// rebuilding a lost factory does not undo the enemy's progress.
    /// </summary>
    public int StructuresLost;

    /// <summary>True while a research project is running.</summary>
    public readonly bool IsResearching => ResearchTicksRemaining > 0;

    /// <summary>True while a prototype run is in progress.</summary>
    public readonly bool IsPrototyping => PrototypeTicksRemaining > 0;
}

/// <summary>A unit or structure being built at a specific building.</summary>
public struct ProductionJob
{
    /// <summary>What is being built.</summary>
    public UnitKind Kind;

    /// <summary>Ticks left before it completes.</summary>
    public int RemainingTicks;

    /// <summary>Ticks the job originally required, for progress bars.</summary>
    public int TotalTicks;

    /// <summary>Fraction complete in [0, 1], for the UI.</summary>
    public readonly float Progress => TotalTicks <= 0 ? 1f : 1f - ((float)RemainingTicks / TotalTicks);
}
