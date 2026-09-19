using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>
/// The Soviet campaign's missions, as data, written separately from the catalog that
/// composes them.
/// <para>
/// <b>Chapter 1 — Η Πτώση του Βερολίνου.</b> Spring of 1945: the Σοβιετικοί against the
/// Ναζί, and nobody else. The chapter is also the campaign's tutorial: each mission adds one
/// mechanic to the one before — the base and the attack, the economy and the era, the ground
/// itself, the emplacement and the fog, and then the assault. The enemy is the Δυτικοί army
/// under its era's name (see <see cref="MatchTeam.GreekNameOverride"/>), the roster is the
/// duel, and the era's ceiling (<see cref="MissionDefinition.MaxTechTier"/>) keeps 1945 out
/// of the era of drones and orbit.
/// </para>
/// </summary>
public static class MissionsSoviet
{
    /// <summary>The enemy of the chapter: the Δυτικοί, as the era called them.</summary>
    public static MatchRoster EraDuel { get; } = MatchRoster.Declare(
        new MatchTeam(0, Faction.Soviet, 0),
        new MatchTeam(2, Faction.Western, 1, GreekNameOverride: "Ναζί"));

    /// <summary>
    /// The Paperclip roster: the Σοβιετικοί against the Δυτικοί, and a third side that is
    /// not a faction at all. The scientists of the outpost stand with the West — the agents
    /// came to carry them out, not to shoot them — but the victory rule does not judge them:
    /// they hold being carried out, not ground, which is what <see cref="MatchTeam.Judged"/>
    /// false declares. Their name is what the interface calls them instead of a faction's.
    /// </summary>
    public static MatchRoster PaperclipRoster { get; } = MatchRoster.Declare(
        new MatchTeam(0, Faction.Soviet, 0),
        new MatchTeam(2, Faction.Western, 1),
        new MatchTeam(3, Faction.Chinese, 1, Judged: false, GreekNameOverride: "Γερμανοί Επιστήμονες"));

    /// <summary>
    /// Chapter 2 — Επιχείρηση Συνδετήρας. Germany has fallen, and the victors are taking its
    /// scientists. The outpost's people are not an army: the mission's script stands them in
    /// it and walks them to the runway, and the Σοβιετικοί are what stands between them and
    /// the airplane. The convoy is the moving objective the roadmap's §8 was waiting on: it
    /// is written with the denial and nothing new, because "four of them must not board" is
    /// already a sentence the objective table knows how to say.
    /// </summary>
    public static readonly MissionDefinition Paperclip = new(
        Id: "pc_paperclip",
        GreekTitle: "Επιχείρηση Συνδετήρας — Το Φυλάκιο",
        GreekBriefing:
            "Η Γερμανία έπεσε, και οι Δυτικοί μαζεύουν τους επιστήμονές της. " +
            "Ένα φυλάκιο στο δυτικό τομέα κρατά τους καλύτερους: φάλαγγα πράκτορες " +
            "θα τους περάσει στο αεροδρόμιο ανατολικά, όπου αεροπλάνο περιμένει. " +
            "Κανείς τους δεν πρέπει να πετάξει. Το εργοστάσιο που τους κρύβει πρέπει " +
            "να σιγήσει.",
        Seed: 20250206UL,
        PlayerBase: new WorldPos(-200_000, 0, -200_000),
        AllyBase: default,
        EnemyBase: new WorldPos(0, 0, 200_000),
        PlayerUnits: 26,
        AllyUnits: 0,
        EnemyUnits: 8,
        Objectives:
        [
            // The denial: four of them through the runway circle is the agents winning. The
            // outpost's own team is the one counted, so a western escort standing in the circle
            // means nothing beside a scientist standing in it.
            new ObjectiveDefinition(
                ObjectiveKind.DenyArea,
                "Οι Δυτικοί δεν πρέπει να πετάξουν 4 επιστήμονες από το αεροδρόμιο.",
                Team: 0,
                TargetTeam: 3,
                TargetCount: 4,
                CentreX: 260_000,
                CentreZ: -60_000,
                RadiusMm: 50_000,
                DeadlineTick: 10_800),
            new ObjectiveDefinition(
                ObjectiveKind.DestroyStructures,
                "Καταστρέψτε το εργοστάσιο του φυλακίου.",
                TargetTeam: 3,
                TargetCount: 1,
                IsPrimary: false,
                Role: UnitKind.DerelictFactory),
            new ObjectiveDefinition(
                ObjectiveKind.ProtectCommandCentre,
                "Το κέντρο διοίκησής σας πρέπει να επιβιώσει.",
                Team: 0,
                Constraint: true),
        ],
        TimeLimitTicks: 14_400)
    {
        Roster = PaperclipRoster,
        MaxTechTier = 3,

        Triggers =
        [
            // The outpost and its people. The factory belongs to the scientists' side — its
            // own wardens are the outpost's guard, the generator's own mechanics turned to a
            // garrison — and the scientists are six unarmed Harvesters, because what is carried
            // out of an outpost is people, and people here are a side, not a cargo.
            new TriggerDefinition(
                Id: "the-outpost",
                Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 1),
                Actions:
                [
                    new TriggerAction(
                        TriggerActionKind.Spawn,
                        Team: 3,
                        Role: UnitKind.DerelictFactory,
                        Count: 1,
                        CentreX: -120_000,
                        CentreZ: 60_000),
                    new TriggerAction(
                        TriggerActionKind.Spawn,
                        Team: 3,
                        Role: UnitKind.Harvester,
                        Count: 6,
                        CentreX: -120_000,
                        CentreZ: 75_000),
                    new TriggerAction(
                        TriggerActionKind.Spawn,
                        Team: 2,
                        Role: UnitKind.Tank,
                        Count: 3,
                        CentreX: -120_000,
                        CentreZ: 95_000),
                    new TriggerAction(
                        TriggerActionKind.Message,
                        GreekText: "Οι Δυτικοί έφτασαν στο φυλάκιο. Οι επιστήμονες φορτώνονται στη φάλαγγα."),
                    new TriggerAction(
                        TriggerActionKind.Reveal,
                        Team: 0,
                        CentreX: -120_000,
                        CentreZ: 60_000,
                        RadiusMm: 60_000),
                ],
                Note: "Everything that is somebody's: the outpost, the six who are carried " +
                      "out, and the escort. A scenario can lay out a base and cannot put " +
                      "people in an outpost, so the script does — the same door the ambush's " +
                      "guns and the parliament came through."),

            // The convoy moves. The scientists and their escort are sent east, to the runway —
            // one order each, because a waypoint is a pathing question and the A* already
            // answers those. The mission's tension is the five hundred metres between here and
            // the airplane.
            new TriggerDefinition(
                Id: "the-convoy",
                Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 60),
                Actions:
                [
                    new TriggerAction(
                        TriggerActionKind.OrderGroup,
                        Team: 3,
                        Order: GroupOrder.Move,
                        CentreX: -120_000,
                        CentreZ: 60_000,
                        RadiusMm: 120_000,
                        TargetX: 250_000,
                        TargetZ: -60_000),
                    new TriggerAction(
                        TriggerActionKind.OrderGroup,
                        Team: 2,
                        Order: GroupOrder.Move,
                        CentreX: -120_000,
                        CentreZ: 60_000,
                        RadiusMm: 120_000,
                        TargetX: 240_000,
                        TargetZ: -60_000),
                    new TriggerAction(
                        TriggerActionKind.Message,
                        GreekText: "Η φάλαγγα κινείται ανατολικά, προς το αεροδρόμιο. Αναχαιτίστε την."),
                    new TriggerAction(
                        TriggerActionKind.Reveal,
                        Team: 0,
                        CentreX: 250_000,
                        CentreZ: -60_000,
                        RadiusMm: 60_000),
                ],
                Note: "The moving objective, as one order rather than a system: the convoy " +
                      "walks to the runway, and the mission is what happens on the way."),
        ],
    };

    /// <summary>
    /// Chapter 3 — Κορέα. Five years after Berlin, and the alliance fights its first war
    /// together: the Σοβιετικοί and the Κινέζοι against the Δυτικοί. The chapter introduces
    /// the ally the way the game means the word — a side fighting its own war beside yours,
    /// with its own economy, its own ceiling, and the licence that moves a design between
    /// them. The era stops at three: this is 1950, not the age of orbit.
    /// </summary>
    public static readonly MissionDefinition[] Korea =
    [
        // k1 — the ally. A line held by two armies at once: the player holds the point, the
        // Κινέζοι hold their own war, and the mission is lost only if the point is lost.
        new MissionDefinition(
            Id: "k1_yalu",
            GreekTitle: "Αποστολή 6 — Ο Γιάλου",
            GreekBriefing:
                "Ο Γιάλου είναι πίσω μας. Οι Δυτικοί περνούν και η Κίνα απαντά: " +
                "κρατήστε το πέρασμα με έξι μονάδες για τριάντα δευτερόλεπτα. " +
                "Οι Κινέζοι σύμμαχοι μάχονται στη δική τους πλευρά — δεν είστε μόνοι.",
            Seed: 20250207UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 190_000),
            PlayerUnits: 22,
            AllyUnits: 14,
            EnemyUnits: 26,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.HoldArea,
                    "Κρατήστε 6 μονάδες στο πέρασμα για 30 δευτερόλεπτα.",
                    Team: 0,
                    TargetCount: 6,
                    HoldTicks: 600,
                    CentreX: 0,
                    CentreZ: 0,
                    RadiusMm: 60_000,
                    DeadlineTick: 9_000),
            ],
            TimeLimitTicks: 10_800)
        {
            Roster = MatchRoster.StandardSkirmish,
            MaxTechTier = 3,
        },

        // k2 — the winter. Snow shortens sight and slows everything, so the mission is the
        // wait itself: survive the cold, and when the line holds, break their guns.
        new MissionDefinition(
            Id: "k2_chosin",
            GreekTitle: "Αποστολή 7 — Η Λίμνη Τσόσιν",
            GreekBriefing:
                "Χειμώνας στη λίμνη. Οι Δυτικοί περικυκλώνουν και το κρύο σκοτώνει όσο τα " +
                "όπλα: αντέξτε μέχρι να αλλάξει ο καιρός. Και όταν κρατήσετε, σιγήστε τα " +
                "πυροβολεία τους — με το χιόνι στα γόνατά τους δεν τρέχουν μακριά.",
            Seed: 20250208UL,
            PlayerBase: new WorldPos(-160_000, 0, -160_000),
            AllyBase: new WorldPos(160_000, 0, -160_000),
            EnemyBase: new WorldPos(0, 0, 195_000),
            PlayerUnits: 24,
            AllyUnits: 14,
            EnemyUnits: 30,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.SurviveTicks,
                    "Αντέξτε στο χειμώνα δύο λεπτά.",
                    Team: 0,
                    DeadlineTick: 2_400),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Προαιρετικά: σιγήστε 2 δυτικά πυροβολεία.",
                    TargetTeam: 2,
                    TargetCount: 2,
                    IsPrimary: false,
                    Role: UnitKind.GunEmplacement),
            ],
            TimeLimitTicks: 10_800)
        {
            Roster = MatchRoster.StandardSkirmish,
            MaxTechTier = 3,
        },

        // k3 — the hill. Take it, hold it, and read what a wave looks like before the next
        // one comes: the west answers in waves, and the script says when.
        new MissionDefinition(
            Id: "k3_hill",
            GreekTitle: "Αποστολή 8 — Ο Λόφος",
            GreekBriefing:
                "Ο λόφος κρατά τον δρόμο νότια. Πάρτε τον και κρατήστε τον: " +
                "οι Δυτικοί θα έρθουν κύμα κύμα να τον πάρουν πίσω. " +
                "Το Σχεδιαστικό Γραφείο προσφέρει άδεια στους Κινέζους — η συμμαχία " +
                "δουλεύει έτσι.",
            Seed: 20250209UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 195_000),
            PlayerUnits: 26,
            AllyUnits: 16,
            EnemyUnits: 26,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.HoldArea,
                    "Κρατήστε 6 μονάδες στο λόφο για 45 δευτερόλεπτα.",
                    Team: 0,
                    TargetCount: 6,
                    HoldTicks: 900,
                    CentreX: 0,
                    CentreZ: 20_000,
                    RadiusMm: 60_000,
                    DeadlineTick: 12_000),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,
            MaxTechTier = 3,

            Triggers =
            [
                new TriggerDefinition(
                    Id: "first-wave",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 3_600),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.Tank,
                            Count: 4,
                            CentreX: 0,
                            CentreZ: 200_000),
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Πρώτο κύμα από βόρεια. Κρατήστε το λόφο."),
                    ],
                    Note: "The west answers in waves, and the player should hear them named: " +
                          "the first wave is four tanks at three minutes."),

                new TriggerDefinition(
                    Id: "second-wave",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 7_200),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.Tank,
                            Count: 6,
                            CentreX: -60_000,
                            CentreZ: 200_000),
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.Artillery,
                            Count: 2,
                            CentreX: 60_000,
                            CentreZ: 200_000),
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Δεύτερο κύμα, με πυροβολικό. Το λόφο δεν πέφτει σήμερα."),
                    ],
                    Note: "The second wave is bigger and brings guns, and it comes at six " +
                          "minutes — a mission that reads its own escalation."),
            ],
        },

        // k4 — the convoy, from the other side of it: this time the ally's people are the
        // ones walking, and the player's job is the road. The new objective kind is the
        // mission's own sentence: six of theirs through, not four of the enemy's stopped.
        new MissionDefinition(
            Id: "k4_offensive",
            GreekTitle: "Αποστολή 9 — Η Εθνική Οδός",
            GreekBriefing:
                "Η Εθνική Οδός τρέφει το μέτωπο. Κινεζικός εφοδιασμός κινείται νότια και " +
                "οι Δυτικοί θα τον κόψουν: έξι μονάδες του εφοδιασμού πρέπει να φτάσουν. " +
                "Η δουλειά σας είναι ο δρόμος.",
            Seed: 20250210UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 190_000),
            PlayerUnits: 24,
            AllyUnits: 14,
            EnemyUnits: 28,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.EscortArea,
                    "Οδηγήστε 6 Κινέζους του εφοδιασμού στο νότιο τέρμα.",
                    Team: 0,
                    TargetTeam: 1,
                    TargetCount: 6,
                    CentreX: 200_000,
                    CentreZ: 240_000,
                    RadiusMm: 60_000,
                    DeadlineTick: 10_800),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,
            MaxTechTier = 3,

            Triggers =
            [
                new TriggerDefinition(
                    Id: "the-supply-convoy",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 1),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 1,
                            Role: UnitKind.Harvester,
                            Count: 7,
                            CentreX: 180_000,
                            CentreZ: -160_000),
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Ο εφοδιασμός ξεκίνησε από τη βόρεια αποθήκη. Κρατήστε τον δρόμο ανοιχτό."),
                    ],
                    Note: "The ally's convoy is people too, and the same mechanic as the " +
                          "outpost's: a side walking its own road, and the mission on top of it."),

                new TriggerDefinition(
                    Id: "convoy-rolls",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 60),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.OrderGroup,
                            Team: 1,
                            Order: GroupOrder.Move,
                            CentreX: 180_000,
                            CentreZ: -160_000,
                            RadiusMm: 100_000,
                            TargetX: 200_000,
                            TargetZ: 230_000),
                    ],
                    Note: "South, to the terminal — one order, and the mission is the road."),
            ],
        },

        // k5 — the parallel. Full war economy on both sides, and the old objective in its
        // honest form: their command centre, by name, or it is not a victory.
        new MissionDefinition(
            Id: "k5_38th",
            GreekTitle: "Αποστολή 10 — Ο 38ος Παράλληλος",
            GreekBriefing:
                "Ο 38ος παράλληλος είναι το τέλος της γραμμής και η αρχή της νίκης. " +
                "Οι Δυτικοί κρατούν το τελευταίο τους κέντρο διοίκησης νότια του. " +
                "Με τους Κινέζους στο ανατολικό, σπάστε την τελευταία γραμμή.",
            Seed: 20250211UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 28,
            AllyUnits: 18,
            EnemyUnits: 32,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το δυτικό κέντρο διοίκησης.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    DeadlineTick: 12_000,
                    Role: UnitKind.CommandCentre),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,
            MaxTechTier = 3,
        },
    ];

    /// <summary>The Berlin chapter, in the order it is played.</summary>
    public static readonly MissionDefinition[] Berlin =
    [
        // b1 — the base and the attack. Nothing to learn but the war itself: build, move,
        // destroy. The objective is the one m1 used to fail for the wrong reason, written
        // against the command centre and nothing else, so the ally-less mission cannot be won
        // by a stray emplacement dying to the enemy's own accidents.
        new MissionDefinition(
            Id: "b1_vistula",
            GreekTitle: "Αποστολή 1 — Ο Βιστούλας",
            GreekBriefing:
                "Η πρωτομαγιά του 1945. Ο στρατός στέκεται στον Βιστούλα και το Βερολίνο " +
                "είναι μπροστά μας. Οι Ναζί κρατούν ένα φυλάκιο απέναντι: " +
                "διασπάστε τη γραμμή και καταστρέψτε το κέντρο διοίκησής τους.",
            Seed: 20250201UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: default,
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 14,
            AllyUnits: 0,
            EnemyUnits: 16,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το κέντρο διοίκησης των Ναζί.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    DeadlineTick: 9_000,
                    Role: UnitKind.CommandCentre),
            ],
            TimeLimitTicks: 10_800)
        {
            Roster = EraDuel,
            MaxTechTier = 1,
        },

        // b2 — the economy and the era: hold the bridgehead with what you have while the
        // bureau brings the army into its second tier. The hold is the primary; the tier is
        // the bonus, because the cap's whole point here is that tier 2 is reachable, slowly,
        // and the player chooses when to pay for it.
        new MissionDefinition(
            Id: "b2_oder",
            GreekTitle: "Αποστολή 2 — Ο Όντερ",
            GreekBriefing:
                "Πέρα από τον Όντερ η γη είναι δική μας αν την κρατήσουμε. " +
                "Οι Ναζί θα αντεπιτεθούν: κρατήστε το προγεφύρωμα με έξι μονάδες για τριάντα " +
                "δευτερόλεπτα, και αν προλαβαίνετε, βγάλτε τον στρατό στο δεύτερο τεχνολογικό " +
                "επίπεδο — θα το χρειαστείτε στα υψώματα.",
            Seed: 20250202UL,
            PlayerBase: new WorldPos(-160_000, 0, -160_000),
            AllyBase: default,
            EnemyBase: new WorldPos(0, 0, 190_000),
            PlayerUnits: 20,
            AllyUnits: 0,
            EnemyUnits: 24,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.HoldArea,
                    "Κρατήστε 6 μονάδες στο προγεφύρωμα για 30 δευτερόλεπτα.",
                    Team: 0,
                    TargetCount: 6,
                    HoldTicks: 600,
                    CentreX: 0,
                    CentreZ: 0,
                    RadiusMm: 60_000,
                    DeadlineTick: 10_800),
                new ObjectiveDefinition(
                    ObjectiveKind.ReachTechTier,
                    "Προαιρετικά: φτάστε σε τεχνολογικό επίπεδο 2.",
                    Team: 0,
                    TierTarget: 2,
                    IsPrimary: false),
            ],
            TimeLimitTicks: 10_800)
        {
            Roster = EraDuel,
            MaxTechTier = 2,
        },

        // b3 — the ground itself: the Seelow heights and the water between. The ford is the
        // only way the counterattack comes, so the denial is written on it; the guns on the
        // heights are the second half of the sentence.
        new MissionDefinition(
            Id: "b3_seelow",
            GreekTitle: "Αποστολή 3 — Τα Υψώματα του Ζέελοβ",
            GreekBriefing:
                "Τα υψώματα κρατούν τον δρόμο για το Βερολίνο. Οι Ναζί πυροβολούν από ψηλά " +
                "και θα στείλουν εφόδους από το πέρασμα: κανένας τους δεν πρέπει να περάσει " +
                "το αυχενικό σημείο με τέσσερις μονάδες. Σιγήστε τα πυροβολεία τους.",
            Seed: 20250203UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: default,
            EnemyBase: new WorldPos(30_000, 0, 200_000),
            PlayerUnits: 22,
            AllyUnits: 0,
            EnemyUnits: 28,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DenyArea,
                    "Οι Ναζί δεν πρέπει να περάσουν το αυχενικό σημείο με 4 μονάδες.",
                    Team: 0,
                    TargetTeam: 2,
                    TargetCount: 4,
                    CentreX: -270_000,
                    CentreZ: -270_000,
                    RadiusMm: 45_000,
                    DeadlineTick: 5_400),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Προαιρετικά: σιγήστε 2 πυροβολεία των υψωμάτων.",
                    TargetTeam: 2,
                    TargetCount: 2,
                    IsPrimary: false,
                    Role: UnitKind.GunEmplacement),
            ],
            TimeLimitTicks: 10_800)
        {
            Roster = EraDuel,
            MaxTechTier = 2,
        },

        // b4 — the city: emplacements in the fog, and the factory that feeds the defence.
        // Two ways of saying "destroy": anything they own, and the factory by name — the
        // role filter carries both, and a stray loss cannot decide either.
        new MissionDefinition(
            Id: "b4_berlin",
            GreekTitle: "Αποστολή 4 — Η Πόλη",
            GreekBriefing:
                "Το Βερολίνο πλέον ακούγεται στις οθόνες μας. Οι Ναζί υπερασπίζονται την " +
                "πόλη σπίτι σπίτι: γκρεμίστε την άμυνα και το εργοστάσιο που την τρέφει. " +
                "Ο χρόνος μετρά — η πόλη δεν κρατά για πάντα.",
            Seed: 20250204UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: default,
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 30,
            AllyUnits: 0,
            EnemyUnits: 26,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Γκρεμίστε 3 κτίρια της δυτικής άμυνας.",
                    TargetTeam: 2,
                    TargetCount: 3,
                    DeadlineTick: 12_000),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το εργοστάσιο της πόλης.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    DeadlineTick: 12_000,
                    Role: UnitKind.Factory),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = EraDuel,
            MaxTechTier = 3,
        },

        // b5 — the assault, and the flag. The Reichstag is put on the map by the mission
        // itself (a scenario can lay out a base and cannot stand a parliament), the guns of
        // the garrison with it. Destroying it completes one objective; the other is the
        // mission's own sentence: the trigger knows the building has fallen and nobody else
        // does, and the flag detail goes up on the spot. See the m4 pattern for the
        // opening-world dependency the second trigger declares.
        new MissionDefinition(
            Id: "b5_reichstag",
            GreekTitle: "Αποστολή 5 — Το Ράιχσταγκ",
            GreekBriefing:
                "Ο Απρίλιος τελειώνει στο κέντρο της πόλης. Οι Ναζί κρατούν το Ράιχσταγκ " +
                "με ό,τι τους έμεινε: σπάστε την τελευταία τους γραμμή, γκρεμίστε το, " +
                "και υψώστε τη σημαία πάνω από την πόλη.",
            Seed: 20250205UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: default,
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 28,
            AllyUnits: 0,
            EnemyUnits: 30,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το Ράιχσταγκ.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    Role: UnitKind.DerelictFactory),
                new ObjectiveDefinition(
                    ObjectiveKind.Scripted,
                    "Υψώστε τη σοβιετική σημαία πάνω από την πόλη."),
            ],
            TimeLimitTicks: 10_800)
        {
            Roster = EraDuel,
            MaxTechTier = 3,

            Triggers =
            [
                // The Reichstag, and its garrison. First in the list, for the same reason the
                // ambush's guns were first in m4's: the condition below counts what stands, and
                // it is asked on the first tick, after this spawn.
                new TriggerDefinition(
                    Id: "the-reichstag",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 1),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.DerelictFactory,
                            Count: 1,
                            CentreX: 0,
                            CentreZ: 20_000),
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 2,
                            Role: UnitKind.GunEmplacement,
                            Count: 2,
                            CentreX: 0,
                            CentreZ: 40_000),
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Η τελευταία γραμμή του Βερολίνου: το Ράιχσταγκ και η φρουρά του."),
                    ],
                    Note: "The building and the guns around it are the mission's opening move: " +
                          "placed by the script because a scenario can lay out a base and cannot " +
                          "stand a parliament in a city square."),

                // The flag. Fewer than one DerelictFactory stands: true of the opening world
                // only because the spawn above has not happened yet, and false by the time the
                // condition is asked on the same first tick — the list order is the order of
                // events, and the dependency is declared as the m4 pattern has it.
                new TriggerDefinition(
                    Id: "the-flag",
                    Condition: new TriggerCondition(
                        TriggerConditionKind.StructuresStandingBelow,
                        Team: 2,
                        Count: 1,
                        Role: UnitKind.DerelictFactory),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Το Ράιχσταγκ έπεσε. Η σημαία υψώνεται πάνω από την πόλη."),
                        new TriggerAction(
                            TriggerActionKind.Spawn,
                            Team: 0,
                            Role: UnitKind.Commissar,
                            Count: 1,
                            CentreX: 0,
                            CentreZ: 20_000),
                        new TriggerAction(
                            TriggerActionKind.Reveal,
                            Team: 0,
                            CentreX: 0,
                            CentreZ: 20_000,
                            RadiusMm: 60_000),
                        new TriggerAction(TriggerActionKind.CompleteObjective, Objective: 1),
                    ],
                    Note: "The scripted objective completes here and nowhere else: the mission " +
                          "knows the building fell, and no predicate in the world can say the " +
                          "flag is up.",
                    DependsOnOpeningWorld: true),
            ],
        },
    ];

    /// <summary>
    /// Chapter 4 — Η Σύγχρονη Εποχή. The alternate future at full scale: all three powers,
    /// no era ceiling, and the war fought for real. The first four of the chapter are the
    /// missions the game already shipped — folded in with their ids kept, so a campaign
    /// already won stays won. These six are the difficulty curve proper: bigger armies,
    /// harder clocks, and the two kinds of war the campaign had not fought yet — the one
    /// against the Κινέζοι, and the one where the alliance itself breaks.
    /// </summary>
    public static readonly MissionDefinition[] Modern =
    [
        // x5 — the border war, properly: more of everything, and the old objective kept
        // honest. Where the campaign stops introducing and starts testing.
        new MissionDefinition(
            Id: "x5_border",
            GreekTitle: "Αποστολή 15 — Τα Σύνορα",
            GreekBriefing:
                "Η σύγχρονη εποχή δεν κάνει δωρεάν: τρεις στρατοί, κανένα ταβανί, " +
                "και η γραμμή κρατά ό,τι έχετε μάθει. Γκρεμίστε την άμυνά τους, " +
                "και το κέντρο διοίκησής τους μαζί της.",
            Seed: 20250212UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 28,
            AllyUnits: 16,
            EnemyUnits: 34,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Γκρεμίστε 4 κτίρια της δυτικής άμυνας.",
                    TargetTeam: 2,
                    TargetCount: 4,
                    DeadlineTick: 12_000),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το δυτικό κέντρο διοίκησης.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    Role: UnitKind.CommandCentre),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,
        },

        // x6 — the winter again, at speed: survive, then take their guns away.
        new MissionDefinition(
            Id: "x6_winter",
            GreekTitle: "Αποστολή 16 — Η Παγωμένη Γραμμή",
            GreekBriefing:
                "Ο χειμώνας γύρισε και η γραμμή πάγωσε. Αντέξτε δύο λεπτά στο κρύο, " +
                "και μετά σιγήστε τα πυροβολεία τους — το χιόνι είναι δικό μας.",
            Seed: 20250213UL,
            PlayerBase: new WorldPos(-160_000, 0, -160_000),
            AllyBase: new WorldPos(160_000, 0, -160_000),
            EnemyBase: new WorldPos(0, 0, 195_000),
            PlayerUnits: 26,
            AllyUnits: 16,
            EnemyUnits: 34,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.SurviveTicks,
                    "Αντέξτε δύο λεπτά.",
                    Team: 0,
                    DeadlineTick: 2_400),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Σιγήστε 3 δυτικά πυροβολεία.",
                    TargetTeam: 2,
                    TargetCount: 3,
                    DeadlineTick: 10_800,
                    Role: UnitKind.GunEmplacement),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,
        },

        // x7 — the other war: against the Κινέζοι this time, and the alliance is somebody
        // else's problem. The rivals roster, and the same honest objective.
        new MissionDefinition(
            Id: "x7_rivals",
            GreekTitle: "Αποστολή 17 — Οι Αντίπαλοι",
            GreekBriefing:
                "Η συμμαχία δεν κράτησε παντού. Στα ανατολικά οι Κινέζοι χτίζουν τη δική " +
                "τους γραμμή — και οι Δυτικοί δεν είναι εδώ να σας σώσουν από αυτή τη " +
                "δουλειά. Μόνοι σας απέναντι στους Κινέζους: σπάστε το κέντρο τους.",
            Seed: 20250214UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: default,
            EnemyBase: new WorldPos(180_000, 0, -180_000),
            PlayerUnits: 30,
            AllyUnits: 0,
            EnemyUnits: 32,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το κινέζικο κέντρο διοίκησης.",
                    TargetTeam: 1,
                    TargetCount: 1,
                    DeadlineTick: 12_000,
                    Role: UnitKind.CommandCentre),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.Rivals,
        },

        // x8 — the betrayal: the pact breaks mid-war. The Κινέζοι change sides on a trigger,
        // and the mission is to survive what that does to the front — then win it anyway.
        new MissionDefinition(
            Id: "x8_steam",
            GreekTitle: "Αποστολή 18 — Η Ρήξη",
            GreekBriefing:
                "Η νίκη στον 38ο παράλληλο δεν αγοράζει τη συμμαχία για πάντα. " +
                "Οι Κινέζοι διαπραγματεύονται με τους Δυτικούς — όταν σπάσει, " +
                "το μέτωπο θα σπάσει μαζί της. Αντέξτε, και κρατήστε τη νίκη.",
            Seed: 20250215UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 30,
            AllyUnits: 16,
            EnemyUnits: 30,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.SurviveTicks,
                    "Αντέξτε στη ρήξη τέσσερα λεπτά.",
                    Team: 0,
                    DeadlineTick: 4_800),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το δυτικό κέντρο διοίκησης.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    Role: UnitKind.CommandCentre),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,

            Triggers =
            [
                new TriggerDefinition(
                    Id: "the-break",
                    Condition: new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 2_400),
                    Actions:
                    [
                        new TriggerAction(
                            TriggerActionKind.Message,
                            GreekText: "Οι Κινέζοι υπέγραψαν με τους Δυτικούς. Η συμμαχία τέλειωσε — το μέτωπο αλλάζει."),
                        new TriggerAction(
                            TriggerActionKind.ChangeSide,
                            Team: 1,
                            Side: 1),
                        new TriggerAction(
                            TriggerActionKind.Music,
                            Team: 0,
                            Leitmotiv: "battle"),
                    ],
                    Note: "The betrayal, at two minutes: the Κινέζοι join the western side, " +
                          "and from this tick every weapon in the game answers the new question. " +
                          "The ally's army is not despawned and its base is not moved — they " +
                          "simply stopped being yours."),
            ],
        },

        // x9 — the overture: the war's last lesson is industry. Out-tech them or out-lose them.
        new MissionDefinition(
            Id: "x9_overture",
            GreekTitle: "Αποστολή 19 — Η Εισαγωγή",
            GreekBriefing:
                "Η τελευταία μάχη αποφασίζεται πριν χτιστούν τα άρματα. " +
                "Βγάλτε τον στρατό στο τέταρτο επίπεδο, χωρίς να πέσει το κέντρο σας — " +
                "και κρατήστε το εργοστάσιό τους κλειστό, για πάντα.",
            Seed: 20250216UL,
            PlayerBase: new WorldPos(-170_000, 0, -170_000),
            AllyBase: new WorldPos(170_000, 0, -170_000),
            EnemyBase: new WorldPos(0, 0, 195_000),
            PlayerUnits: 28,
            AllyUnits: 16,
            EnemyUnits: 38,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.ReachTechTier,
                    "Φτάστε σε τεχνολογικό επίπεδο 4.",
                    Team: 0,
                    TierTarget: 4,
                    DeadlineTick: 12_000),
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το δυτικό εργοστάσιο.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    Role: UnitKind.Factory),
                new ObjectiveDefinition(
                    ObjectiveKind.ProtectCommandCentre,
                    "Το κέντρο διοίκησής σας πρέπει να επιβιώσει.",
                    Team: 0,
                    Constraint: true),
            ],
            TimeLimitTicks: 14_400)
        {
            Roster = MatchRoster.StandardSkirmish,
        },

        // x10 — full scale. Every mechanic the campaign taught, one answer: their command
        // centre falls and yours stands. The finale of the soviet campaign.
        new MissionDefinition(
            Id: "x10_fullscale",
            GreekTitle: "Αποστολή 20 — Ο Παγκόσμιος Πόλεμος",
            GreekBriefing:
                "Κανένα κεφάλαιο δεν τελειώνει ήσυχο. Τρεις στρατοί σε πλήρη πόλεμο: " +
                "το δυτικό κέντρο διοίκησης πέφτει, το δικό σας στέκεται. " +
                "Για τον ειρηνικό, σοσιαλιστικό κόσμο.",
            Seed: 20250217UL,
            PlayerBase: new WorldPos(-180_000, 0, -180_000),
            AllyBase: new WorldPos(180_000, 0, -180_000),
            EnemyBase: new WorldPos(0, 0, 200_000),
            PlayerUnits: 32,
            AllyUnits: 20,
            EnemyUnits: 44,
            Objectives:
            [
                new ObjectiveDefinition(
                    ObjectiveKind.DestroyStructures,
                    "Καταστρέψτε το δυτικό κέντρο διοίκησης.",
                    TargetTeam: 2,
                    TargetCount: 1,
                    DeadlineTick: 14_000,
                    Role: UnitKind.CommandCentre),
                new ObjectiveDefinition(
                    ObjectiveKind.ProtectCommandCentre,
                    "Το κέντρο διοίκησής σας πρέπει να επιβιώσει.",
                    Team: 0,
                    Constraint: true),
            ],
            TimeLimitTicks: 16_200)
        {
            Roster = MatchRoster.StandardSkirmish,
        },
    ];
}
