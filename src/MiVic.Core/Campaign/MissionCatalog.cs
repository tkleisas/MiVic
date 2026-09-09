using MiVic.Core.Numerics;

namespace MiVic.Core.Campaign;

/// <summary>
/// The campaign, as data.
/// <para>
/// Three missions of the alternate-history war: a bridgehead against the Δυτικοί
/// line, a ridge that decides the front, and an industrial race that decides
/// whether the alliance can keep fighting at all. Each one exercises a different
/// objective kind, so the campaign is also the acceptance test for the mission
/// system.
/// </para>
/// </summary>
public static class MissionCatalog
{
    /// <summary>Every mission, in campaign order.</summary>
    public static readonly MissionDefinition[] All =
    [
        new MissionDefinition(
            Id: "m1_bridgehead",
            GreekTitle: "Αποστολή 1 — Το Προγεφύρωμα",
            GreekBriefing:
                "Οι Δυτικοί έχουν στήσει φυλάκιο βόρεια της γραμμής. " +
                "Διασπάστε την άμυνα και καταστρέψτε το κέντρο διοίκησής τους. " +
                "Οι Κινέζοι σύμμαχοι καλύπτουν την ανατολική πλευρά.",
            Seed: 20250101UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 18,
            AllyUnits: 10,
            EnemyUnits: 26,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το κέντρο διοίκησης των Δυτικών.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    DeadlineTick: 7_200),
                new ObjectiveDefinition(
                    ObjectiveKind.ReachTechTier,
                    "Προαιρετικά: φτάστε σε τεχνολογικό επίπεδο 2.",
                    Team: 0,
                    TierTarget: 2,
                    IsPrimary: false),
            ],
            TimeLimitTicks: 7_200),

        new MissionDefinition(
            Id: "m2_ridge",
            GreekTitle: "Αποστολή 2 — Η Κορυφογραμμή",
            GreekBriefing:
                "Η κορυφογραμμή στο κέντρο του χάρτη ελέγχει τον δρόμο προς τα νότια. " +
                "Κρατήστε έξι μονάδες πάνω της για τριάντα δευτερόλεπτα, " +
                "όσο οι σύμμαχοι αναδιοργανώνονται.",
            Seed: 20250102UL,
            PlayerBase: new WorldPos(-160_000, 0, -160_000),
            AllyBase: new WorldPos(160_000, 0, -160_000),
            EnemyBase: new WorldPos(0, 0, 190_000),
            PlayerUnits: 24,
            AllyUnits: 12,
            EnemyUnits: 30,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.HoldArea,
                    "Κρατήστε 6 μονάδες στο κέντρο για 30 δευτερόλεπτα.",
                    Team: 0,
                    TargetCount: 6,
                    HoldTicks: 600,
                    CentreX: 0,
                    CentreZ: 0,
                    RadiusMm: 60_000,
                    DeadlineTick: 9_000),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Προαιρετικά: καταστρέψτε 2 εχθρικές κατασκευές.",
                    TargetTeam: 2,
                    TargetCount: 2,
                    IsPrimary: false),
            ],
            TimeLimitTicks: 9_000),

        new MissionDefinition(
            Id: "m3_industry",
            GreekTitle: "Αποστολή 3 — Η Βιομηχανία της Νίκης",
            GreekBriefing:
                "Ο πόλεμος κρίνεται στα εργοστάσια. " +
                "Ανεβάστε την τεχνολογία στο επίπεδο 3 και συγκεντρώστε 4000 πόρους " +
                "χωρίς να χάσετε το κέντρο διοίκησής σας.",
            Seed: 20250103UL,
            PlayerBase: new WorldPos(-170_000, 0, -170_000),
            AllyBase: new WorldPos(170_000, 0, -170_000),
            EnemyBase: new WorldPos(0, 0, 195_000),
            PlayerUnits: 26,
            AllyUnits: 14,
            EnemyUnits: 34,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.ReachTechTier,
                    "Φτάστε σε τεχνολογικό επίπεδο 3.",
                    Team: 0,
                    TierTarget: 3,
                    DeadlineTick: 12_000),
                new ObjectiveDefinition(
                    ObjectiveKind.AccumulateMaterials,
                    "Συγκεντρώστε 4000 πόρους.",
                    Team: 0,
                    MaterialsTarget: 4_000,
                    DeadlineTick: 12_000),
                new ObjectiveDefinition(
                    ObjectiveKind.ProtectCommandCentre,
                    "Το κέντρο διοίκησής σας πρέπει να επιβιώσει.",
                    Team: 0,
                    Constraint: true),
            ],
            TimeLimitTicks: 12_000),
    ];

    /// <summary>Looks a mission up by id, or returns null when there is no such mission.</summary>
    public static MissionDefinition? Find(string id)
    {
        ArgumentNullException.ThrowIfNull(id);

        foreach (MissionDefinition mission in All)
        {
            if (string.Equals(mission.Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return mission;
            }
        }

        return null;
    }

    /// <summary>Looks a mission up by id, throwing when it does not exist.</summary>
    public static MissionDefinition Require(string id)
        => Find(id) ?? throw new ArgumentException($"Unknown mission '{id}'.", nameof(id));

    /// <summary>Index of a mission in <see cref="All"/>, or -1.</summary>
    public static int IndexOf(string id)
    {
        for (int i = 0; i < All.Length; i++)
        {
            if (string.Equals(All[i].Id, id, StringComparison.OrdinalIgnoreCase))
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>A human-readable list of the campaign, for <c>--mission-list</c>.</summary>
    public static string Describe()
    {
        System.Text.StringBuilder text = new();

        text.AppendLine("MiVic — Εκστρατεία");
        text.AppendLine("==================");

        foreach (MissionDefinition mission in All)
        {
            text.AppendLine();
            text.AppendLine($"{mission.Id}  —  {mission.GreekTitle}");
            text.AppendLine($"  {mission.GreekBriefing}");

            foreach (ObjectiveDefinition objective in mission.Objectives)
            {
                string kind = objective.IsPrimary ? "κύριος" : "προαιρετικός";
                text.AppendLine($"  [{kind}] {objective.GreekDescription}");
            }
        }

        return text.ToString();
    }
}
