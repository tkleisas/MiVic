using MiVic.Core.Numerics;
using MiVic.Core.Sim;

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
/// <para>
/// The fourth is the mission that uses the <em>trigger</em> layer — see
/// <see cref="TriggerSystem"/> — and it is the one that shows what a campaign can be once a
/// mission can say when something happens. The first three are deliberately left triggerless:
/// they are laid out, fought and decided exactly as they were before the layer existed, and
/// their state hashes are pinned to prove it.
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

        // ---------------------------------------------------------------------------------
        // The mission that uses the trigger layer, and the only one that does.
        //
        // The fiction first, because the triggers are written against it: the Δυτικοί are
        // pushing a reconnaissance column south through the only pass in the valley, and there
        // is a road out of it behind our own line. If four of their units reach that road, they
        // have found the way to our rear and the position is lost — so the mission is won by
        // *preventing* something, which is the one kind of objective the campaign could not
        // express before (see ObjectiveKind.DenyArea) — and then by breaking the ambush they
        // have waiting in the pass, which is the half of the mission the trigger layer tells.
        //
        // Two sides and no ally (MatchRoster.Duel), deliberately: the campaign's Κινέζοι ally is
        // played by the AI, and an ally fighting its own war three hundred metres away would be
        // deciding when this mission's triggers fire. A demonstration mission has to be a
        // script, and a script with a second army in it is not one.
        // ---------------------------------------------------------------------------------
        new MissionDefinition(
            Id: "m4_pass",
            GreekTitle: "Αποστολή 4 — Η Ενέδρα στο Πέρασμα",
            GreekBriefing:
                "Οι Δυτικοί ανιχνεύουν το πέρασμα με φάλαγγα. Δεν πρέπει να βγουν στην οδό πίσω " +
                "από τη γραμμή μας: αν φτάσουν τέσσερις μονάδες τους στο σημείο διαφυγής, το " +
                "νότιο άκρο του χάρτη, η θέση χάνεται. " +
                "Το πέρασμα όμως είναι ύποπτα ήσυχο, και οι πρόσκοποι αναφέρουν κίνηση. " +
                "Διαλύστε την ενέδρα πριν κλείσει ο δρόμος.",
            Seed: 20250104UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(30_000, 0, 200_000),
            PlayerUnits: 26,
            AllyUnits: 0,
            EnemyUnits: 30,
            Objectives:
            [
                // The denial. It is judged for the player and about the Δυτικοί, and it is only
                // decided by the clock: the deadline arriving with the road still clear is the
                // fact that they never got through. Four units rather than one, so that a stray
                // scout wandering into the corner is not a lost mission.
                new ObjectiveDefinition(
                    ObjectiveKind.DenyArea,
                    "Οι Δυτικοί δεν πρέπει να φτάσουν στο σημείο διαφυγής με 4 μονάδες.",
                    Team: 0,
                    TargetTeam: 2,
                    TargetCount: 4,
                    CentreX: -270_000,
                    CentreZ: -270_000,
                    RadiusMm: 45_000,
                    DeadlineTick: 3_600),

                // The objective the mission decides for itself: no predicate in the world can
                // say "the ambush is broken", so the trigger that sees the second Western
                // position fall is what completes it. The description names that condition out
                // loud, because a player who cannot see what would complete an objective is
                // being asked to guess.
                new ObjectiveDefinition(
                    ObjectiveKind.Scripted,
                    "Διαλύστε την ενέδρα: όταν πέσουν δύο δυτικές θέσεις, η αντεπίθεση εξουσιοδοτείται."),
            ],
            TimeLimitTicks: 7_200)
        {
            Roster = MatchRoster.Duel,
            Triggers =
            [
                // 1. The board.
                //
                // The scenario lays out a base and a starting force, and it cannot put a gun on a
                // ridge: that is a position no column of a mission definition has a slot for. So
                // the mission's first trigger is its opening move — one tick has passed, and the
                // ambush is where an ambush is.
                //
                // It is *first in the list* for a reason that the list's own order gives it: the
                // conditions below that count Western gun emplacements are evaluated after it, on
                // the same tick, so the count they see is the count this trigger just created.
                // A trigger that had to be second for that to hold would be a trigger whose
                // correctness depended on where a reader put it, which is the kind of thing this
                // list is meant to make visible rather than hide.
                new TriggerDefinition(
                    Id: "preparation",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 1),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.GunEmplacement,
                            Count: 2,
                            CentreX: -75_000,
                            CentreZ: 60_000),
                    ],
                    Note: "The ambush is already there: two guns on the rock above the pass — " +
                          "thirteen metres of it against the pass's eight — placed by the mission " +
                          "because a scenario can lay out a base and cannot put a gun on a ridge."),

                // 2. The warning.
                //
                // Twenty seconds of quiet, then the scouts report. The reveal is what makes the
                // ambush a scene rather than an event: the pass itself is uncovered, so the player
                // can see the ground they are about to walk into. Forty-five metres, and the
                // number is the design — the guns stand fifty metres up the rock, so the warning
                // shows the ground and *not* what is waiting on it. The ridge comes out of the fog
                // with the ambush, which is the difference between a warning and a briefing.
                new TriggerDefinition(
                    Id: "warning",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 400),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Οι πρόσκοποι αναφέρουν κίνηση βόρεια του περάσματος. Ο αυχένας είναι ύποπτα ήσυχος."),
                        new TriggerAction(
                            TriggerActionKind.Reveal,
                            Team: 0,
                            CentreX: -75_000,
                            CentreZ: 10_000,
                            RadiusMm: 45_000),
                    ],
                    Note: "Twenty seconds in, the fog over the pass lifts: the ground the column " +
                          "must cross is shown before it is crossed, and the rock above it is not."),

                // 3. The ambush springs.
                //
                // The condition is the player's own column, not the enemy's: the ambush is
                // waiting for *us*. Three units rather than one, because one scout riding ahead
                // is a scout, and the trigger is written to fire when a column is in the pass.
                new TriggerDefinition(
                    Id: "ambush",
                    Condition: new TriggerCondition(
                        TriggerConditionKind.UnitInArea,
                        Team: 0,
                        Count: 3,
                        CentreX: -75_000,
                        CentreZ: 10_000,
                        RadiusMm: 70_000),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Ενέδρα! Πυροβολεία στο ύψωμα και τεθωρακισμένα στα πλευρά."),
                        new TriggerAction(
                            TriggerActionKind.Reveal,
                            Team: 0,
                            CentreX: -75_000,
                            CentreZ: 10_000,
                            RadiusMm: 150_000),
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.Tank,
                            Count: 4,
                            CentreX: -150_000,
                            CentreZ: 0),
                        new TriggerAction(TriggerActionKind.SetFlag, Flag: 0),
                    ],
                    Note: "The ambush itself: armour appears on the flank, the fog comes off the " +
                          "whole crossing, and the flag tells the next trigger that it happened."),

                // 4. The ambush attacks.
                //
                // The flag is the whole reason this is a second trigger rather than four more
                // actions on the one above: an order to attack is only worth giving if there is
                // something to attack, and "the ambush has sprung" is exactly the fact that says
                // so. The list order is the order of the events — this fires on the same tick as
                // the trigger that raised the flag, immediately after it.
                new TriggerDefinition(
                    Id: "counterattack",
                    Condition: new TriggerCondition(TriggerConditionKind.FlagSet, Flag: 0),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Οι Δυτικοί επιτίθενται. Ανεφοδιασμός από τα μετόπισθεν: +200 Π, +120 Ν."),
                        new TriggerAction(
                            TriggerActionKind.AdjustResources,
                            Team: 0,
                            Materials: 200,
                            Water: 120),
                        new TriggerAction(
                            TriggerActionKind.OrderGroup,
                            Team: 2,
                            Order: GroupOrder.Attack,
                            CentreX: -150_000,
                            CentreZ: 0,
                            RadiusMm: 80_000,
                            TargetX: -75_000,
                            TargetZ: 10_000),
                    ],
                    Note: "The armour on the flank is sent at the column, and the column is " +
                          "resupplied — an ambush the player is expected to survive and answer."),

                // 5. The first gun falls.
                //
                // The present-tense count — fewer than two stand now — and the number is the two
                // guns the mission put there itself: below two is one, which is the first gun
                // silenced. What it costs the Δυτικοί is their stores, which is the *removal* half
                // of the resources action; the other half lands on the trigger below.
                //
                // *This is the trigger with a dependency on the opening world, declared rather
                // than hidden.* In the world the mission *opens* in no Western gun stands at all —
                // the two of them are spawned by the trigger above — so "fewer than two stand" is
                // already true of that world, and the validation says so. By the time this
                // condition is asked, on the first tick, the count is two: `preparation` is earlier
                // in the list, and the list order is the order of the events. So the trigger is
                // correct and the complaint is a statement about a dependency — the one this layer
                // has carried as an open item since it was built, a condition whose answer is
                // decided by what an earlier trigger has just spawned. The flag is where that
                // dependency is written down instead of being assumed.
                new TriggerDefinition(
                    Id: "first-gun",
                    Condition: new TriggerCondition(
                        TriggerConditionKind.StructuresStandingBelow,
                        Team: 2,
                        Count: 2,
                        Role: UnitKind.GunEmplacement),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Το πρώτο πυροβολείο σίγησε. Τα εφόδια της δυτικής φάλαγγας χάθηκαν."),
                        new TriggerAction(
                            TriggerActionKind.AdjustResources,
                            Team: 2,
                            Materials: -300,
                            Energy: -150),
                    ],
                    Note: "One gun down: fewer than two stand, and the detachment's stores go " +
                          "with it.",
                    DependsOnOpeningWorld: true),

                // 6. The ambush is broken.
                //
                // The ledger the DestroyStructures objective reads, asked of the mission instead
                // of by an objective: two Western structures destroyed. In this mission those two
                // are the guns, because nothing else of the Δυτικοί is ever in reach — and it is
                // written as the past-tense count, a number of losses, rather than as a number of
                // survivors on purpose, so that a position the enemy rebuilds cannot un-break the
                // ambush. It is also the half that cannot fire early, because the ledger starts at
                // zero; the trigger above it is the half that can, and says so.
                new TriggerDefinition(
                    Id: "ambush-broken",
                    Condition: new TriggerCondition(TriggerConditionKind.StructuresLost, Team: 2, Count: 2),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Η ενέδρα διαλύθηκε. Η αντεπίθεση εξουσιοδοτείται: +250 Π λάφυρα."),
                        new TriggerAction(TriggerActionKind.AdjustResources, Team: 0, Materials: 250),
                        new TriggerAction(TriggerActionKind.CompleteObjective, Objective: 1),
                    ],
                    Note: "The scripted objective completes here and nowhere else: the mission " +
                          "knows the ambush is broken, and no predicate in the world can say it."),
            ],
        },
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

            if (mission.HasTriggers)
            {
                // The mission's own scenes, listed by what they are called rather than by what
                // they do: a campaign that can say when something happens is worth being able to
                // read at a glance, and the ids are the mission author's names for those moments.
                text.AppendLine($"  [σενάριο] {mission.Triggers.Count} σκηνές: " +
                                string.Join(", ", mission.Triggers.Select(trigger => trigger.Id)));
            }
        }

        return text.ToString();
    }
}
