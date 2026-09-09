namespace MiVic.Core.Sim;

/// <summary>Named research projects. Values are stable because they are saved.</summary>
public enum TechId : byte
{
    None = 0,

    // ---- Σοβιετικοί: narrow and deep ----
    SovietDeepBattle = 1,
    SovietAdvance2 = 2,
    SovietElectro = 3,
    SovietAdvance3 = 4,
    SovietOgAs = 5,
    SovietOrbital = 6,
    SovietPartisans = 7,
    SovietRecon = 8,
    SovietAdvance4 = 9,

    /// <summary>Command automation. Numbered above the Κινέζοι block, which owns 10–13.</summary>
    SovietAiCommand = 14,

    // ---- Κινέζοι: wide and shallow ----
    ChineseMassMobilisation = 10,
    ChineseAdvance2 = 11,
    ChineseMilitia = 12,
    ChineseSwarm = 13,

    /// <summary>The Κινέζοι reach the aircraft era, but no further.</summary>
    ChineseAdvance3 = 15,

    // ---- Δυτικοί: deep and broad, but expensive ----
    WesternNco = 20,
    WesternAdvance2 = 21,
    WesternPrecision = 22,
    WesternAdvance3 = 23,
    WesternComposite = 24,
    WesternNetwork = 25,
}

/// <summary>What completing a project does.</summary>
public enum TechEffect : byte
{
    /// <summary>Raises the team's tech tier, unlocking the next era's hardware.</summary>
    AdvanceTier = 0,

    /// <summary>Increases weapon damage, in thousandths.</summary>
    Damage = 1,

    /// <summary>Increases unit hit points, in thousandths.</summary>
    Armor = 2,

    /// <summary>Increases movement speed, in thousandths.</summary>
    Speed = 3,

    /// <summary>Speeds up production, in thousandths.</summary>
    Production = 4,

    /// <summary>Raises the morale floor, in Q16.16 raw units.</summary>
    Morale = 5,

    /// <summary>Increases sight radius, in thousandths.</summary>
    Vision = 6,

    /// <summary>
    /// Reduces the mud and snow penalty, in thousandths of it paid. 500 means the
    /// team's units pay half — the mobility doctrine that makes terrain the
    /// Σοβιετικοί weapon rather than a tax.
    /// </summary>
    TerrainResistance = 7,

    /// <summary>Adds parallel production slots per building.</summary>
    ParallelSlots = 8,
}

/// <summary>A research project: what it needs, what it costs, what it does.</summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Faction">Who can research it.</param>
/// <param name="GreekName">Player-facing name.</param>
/// <param name="GreekDescription">One-line explanation, in Greek.</param>
/// <param name="RequiredTier">Tech tier the team must already have.</param>
/// <param name="Cost">Material cost.</param>
/// <param name="Ticks">Base research time before the faction's research speed.</param>
/// <param name="Prerequisite">Another project that must be finished first.</param>
/// <param name="Effect">What it does.</param>
/// <param name="Value">Effect strength: permille for modifiers, raw morale for <see cref="TechEffect.Morale"/>.</param>
public readonly record struct TechProject(
    TechId Id,
    Faction Faction,
    string GreekName,
    string GreekDescription,
    int RequiredTier,
    int Cost,
    int Ticks,
    TechId Prerequisite,
    TechEffect Effect,
    int Value);

/// <summary>
/// The alternate-history tech tree.
/// <para>
/// The shape of each faction's tree is the design: the Σοβιετικοί go narrow and
/// deep (few branches, expensive, high ceiling), the Κινέζοι wide and shallow
/// (cheap bonuses, no high era at all), and the Δυτικοί broad but expensive.
/// Every entry is data — no system knows a faction by name.
/// </para>
/// </summary>
public static class TechCatalog
{
    private static readonly TechProject[] Projects =
    [
        // ---------------- Σοβιετικοί ----------------
        // Βαθιά Μάχη is the keystone: the faction that handles mud best can
        // research its way to ignoring it, which is what makes the terrain system
        // a Σοβιετικοί mechanic rather than a tax on everyone.
        new(TechId.SovietDeepBattle, Faction.Soviet, "Βαθιά Μάχη", "-50% ποινή λάσπης και χιονιού", 1, 300, 300, TechId.None, TechEffect.TerrainResistance, 500),
        new(TechId.SovietPartisans, Faction.Soviet, "Παρτιζάνοι", "+25% ορατότητα πεζικού", 1, 250, 250, TechId.None, TechEffect.Vision, 1250),
        new(TechId.SovietAdvance2, Faction.Soviet, "Επίπεδο 2: Τεθωρακισμένα", "Ξεκλειδώνει άρματα και πυροβολικό", 1, 400, 500, TechId.None, TechEffect.AdvanceTier, 2),
        new(TechId.SovietElectro, Faction.Soviet, "Ηλεκτροτεχνία", "+25% ζημιά — τόξα και παλμικά όπλα", 2, 550, 600, TechId.SovietAdvance2, TechEffect.Damage, 1250),
        new(TechId.SovietRecon, Faction.Soviet, "Αναγνωριστικοί Δορυφόροι", "+30% ορατότητα", 2, 500, 500, TechId.SovietAdvance2, TechEffect.Vision, 1300),
        new(TechId.SovietAdvance3, Faction.Soviet, "Επίπεδο 3: Αεροπορία", "Ξεκλειδώνει αεροσκάφη", 2, 650, 700, TechId.SovietAdvance2, TechEffect.AdvanceTier, 3),
        new(TechId.SovietOgAs, Faction.Soviet, "Κυβερνητική (OGAS)", "+1 παράλληλη γραμμή παραγωγής", 3, 800, 800, TechId.SovietElectro, TechEffect.ParallelSlots, 1),
        new(TechId.SovietOrbital, Faction.Soviet, "Κόκκινος Ουρανός", "+20% θωράκιση — τροχιακή υποστήριξη", 3, 900, 900, TechId.SovietOgAs, TechEffect.Armor, 1200),
        new(TechId.SovietAdvance4, Faction.Soviet, "Επίπεδο 4: Κόκκινος Λογισμός", "Ανοίγει την εποχή της αυτόματης διοίκησης", 3, 1_000, 1_000, TechId.SovietOgAs, TechEffect.AdvanceTier, 4),
        new(TechId.SovietAiCommand, Faction.Soviet, "AI Διοίκηση", "+1 παράλληλη γραμμή παραγωγής", 4, 1_200, 1_100, TechId.SovietAdvance4, TechEffect.ParallelSlots, 1),

        // ---------------- Κινέζοι ----------------
        new(TechId.ChineseMassMobilisation, Faction.Chinese, "Μαζική Επιστράτευση", "+20% ταχύτητα παραγωγής", 1, 200, 250, TechId.None, TechEffect.Production, 1200),
        new(TechId.ChineseAdvance2, Faction.Chinese, "Επίπεδο 2: Τεθωρακισμένα", "Ξεκλειδώνει άρματα και πυροβολικό", 1, 300, 400, TechId.None, TechEffect.AdvanceTier, 2),
        new(TechId.ChineseMilitia, Faction.Chinese, "Λαϊκή Πολιτοφυλακή", "+15% θωράκιση", 2, 250, 350, TechId.ChineseAdvance2, TechEffect.Armor, 1150),
        new(TechId.ChineseSwarm, Faction.Chinese, "Τακτική Πλήθους", "+10% ζημιά", 2, 250, 350, TechId.ChineseAdvance2, TechEffect.Damage, 1100),
        new(TechId.ChineseAdvance3, Faction.Chinese, "Επίπεδο 3: Αεροπορία", "Ξεκλειδώνει αεροσκάφη", 2, 500, 700, TechId.ChineseAdvance2, TechEffect.AdvanceTier, 3),

        // ---------------- Δυτικοί ----------------
        new(TechId.WesternNco, Faction.Western, "Επαγγελματίες Αξιωματικοί", "+0.10 ηθικό", 1, 350, 400, TechId.None, TechEffect.Morale, 6_554),
        new(TechId.WesternAdvance2, Faction.Western, "Επίπεδο 2: Τεθωρακισμένα", "Ξεκλειδώνει άρματα και πυροβολικό", 1, 450, 450, TechId.None, TechEffect.AdvanceTier, 2),
        new(TechId.WesternPrecision, Faction.Western, "Πυρομαχικά Ακριβείας", "+25% ζημιά", 2, 600, 600, TechId.WesternAdvance2, TechEffect.Damage, 1250),
        new(TechId.WesternAdvance3, Faction.Western, "Επίπεδο 3: Αεροπορία", "Ξεκλειδώνει αεροσκάφη", 2, 700, 700, TechId.WesternAdvance2, TechEffect.AdvanceTier, 3),
        new(TechId.WesternComposite, Faction.Western, "Σύνθετη Θωράκιση", "+30% θωράκιση", 3, 850, 800, TechId.WesternPrecision, TechEffect.Armor, 1300),
        new(TechId.WesternNetwork, Faction.Western, "Δικτυοκεντρικός Πόλεμος", "+25% ταχύτητα παραγωγής", 3, 900, 850, TechId.WesternAdvance3, TechEffect.Production, 1250),
    ];

    /// <summary>Every project in the tree.</summary>
    public static ReadOnlySpan<TechProject> All => Projects;

    /// <summary>Looks up a project.</summary>
    public static bool TryGet(TechId id, out TechProject project)
    {
        foreach (TechProject candidate in Projects)
        {
            if (candidate.Id == id)
            {
                project = candidate;
                return true;
            }
        }

        project = default;
        return false;
    }

    /// <summary>True when the team has already finished the project.</summary>
    public static bool IsCompleted(ulong mask, TechId id) => (mask & (1UL << (int)id)) != 0;

    /// <summary>
    /// Projects a team may start right now: right faction, tier reached,
    /// prerequisite finished, and not already done.
    /// </summary>
    public static IEnumerable<TechProject> Available(Faction faction, int techTier, ulong mask)
    {
        foreach (TechProject project in Projects)
        {
            if (project.Faction != faction || IsCompleted(mask, project.Id) || project.RequiredTier > techTier)
            {
                continue;
            }

            if (project.Prerequisite != TechId.None && !IsCompleted(mask, project.Prerequisite))
            {
                continue;
            }

            yield return project;
        }
    }

    /// <summary>Research time for a project, after the faction's research speed.</summary>
    public static int TicksFor(Faction faction, TechProject project)
    {
        int speed = FactionProfile.For(faction).ResearchSpeedPermille;
        return Math.Max(1, (project.Ticks * 1_000) / Math.Max(1, speed));
    }
}
