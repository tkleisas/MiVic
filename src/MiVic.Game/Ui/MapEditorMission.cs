using ImGuiNET;
using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace MiVic.Game.Ui;

/// <summary>
/// The mission-body half of the editor: the roster, the forces, the objectives and the
/// script — the part that has no cursor, edited as the data it is.
/// <para>
/// Every change goes through <see cref="ReplaceMission"/>, which re-lays the world out when
/// the change touches the layout (a roster row, a unit count, an objective or a trigger) and
/// asks the mission the same questions the file loader asks: <see cref="TriggerSystem.Validate"/> —
/// the script that can never fire, the objective the opening world has already decided, the
/// side that stands in nothing. The report is the validator's own sentences, and the save
/// refuses while the report is non-empty: the same loop the placements run, over the part of
/// the map the file carries as data.
/// </para>
/// <para>
/// The validator runs on a committed change rather than on every frame of a slider: numbers
/// commit when the widget is released, words when the field says they are finished, because
/// a world laid out per frame of a drag is a world laid out a hundred times a second.
/// </para>
/// </summary>
public sealed partial class MapEditor
{
    /// <summary>The validator's problems with the mission as it stands.</summary>
    private readonly List<string> _missionProblems = [];

    /// <summary>The mission body as it stands.</summary>
    public MissionDefinition Mission => _mission;

    /// <summary>What the validator says about the mission as it stands.</summary>
    public IReadOnlyList<string> MissionProblems => _missionProblems;

    /// <summary>
    /// Replaces the mission body: the validator is asked, the report is its own sentences,
    /// and the world is re-laid when the change touches the layout.
    /// </summary>
    public void ReplaceMission(MissionDefinition mission, bool relayout)
    {
        _mission = mission;
        _missionProblems.Clear();
        _missionProblems.AddRange(TriggerSystem.Validate(_mission));

        if (relayout)
        {
            RebuildWorld();
        }

        Dirty = true;
    }

    /// <summary>The mission panel, to the right of the ground tools.</summary>
    public EditorCommand? DrawMission()
    {
        EditorCommand? raised = null;

        NVec2 display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new NVec2(display.X - 372f, 12f), ImGuiCond.FirstUseEver);
        ImGui.SetNextWindowSize(new NVec2(360f, 0f));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0.020f, 0.030f, 0.050f, 0.94f));
        ImGui.PushStyleColor(ImGuiCol.Border, new NVec4(0.30f, 0.38f, 0.48f, 0.85f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(14f, 12f));

        if (ImGui.Begin("##editor-mission", PanelFlags))
        {
            ImGui.TextColored(new NVec4(0.90f, 0.88f, 0.80f, 1f), "Η αποστολή");
            ImGui.Separator();

            DrawMissionHead();
            DrawRoster();
            DrawForces();
            DrawObjectives();
            DrawTriggers();

            if (_missionProblems.Count > 0)
            {
                ImGui.Separator();
                ImGui.TextColored(new NVec4(0.95f, 0.55f, 0.45f, 1f), "Η αποστολή αρνείται:");

                foreach (string problem in _missionProblems)
                {
                    ImGui.TextWrapped($"· {problem}");
                }
            }

            ImGui.Separator();

            if (ImGui.Button("Δοκιμή παιχνιδιού", new NVec2(-1f, 0f)))
            {
                raised = new EditorCommand(EditorCommandKind.Play);
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);

        return raised;
    }

    /// <summary>The identity words: what the campaign calls it and what the briefing says.</summary>
    private void DrawMissionHead()
    {
        string id = _mission.Id;

        if (InputText("Κωδικός", ref id, 48))
        {
            Commit(_mission with { Id = id }, relayout: false);
        }

        string title = _mission.GreekTitle;

        if (InputText("Τίτλος", ref title, 64))
        {
            Commit(_mission with { GreekTitle = title }, relayout: false);
        }

        string briefing = _mission.GreekBriefing;

        if (ImGui.InputTextMultiline("Σενάριο", ref briefing, 1_200, new NVec2(-1f, 60f)))
        {
            Commit(_mission with { GreekBriefing = briefing }, relayout: false);
        }

        int limitMinutes = _mission.TimeLimitTicks / (SimConstants.TickRate * 60);

        if (ImGui.SliderInt("Χρόνος (min)", ref limitMinutes, 0, 120) && ImGui.IsItemDeactivatedAfterEdit())
        {
            Commit(_mission with { TimeLimitTicks = limitMinutes * SimConstants.TickRate * 60 }, relayout: false);
        }
    }

    /// <summary>
    /// The match's teams: which slots play, what faction each plays, which side each is on,
    /// and whether the victory rule judges it — the declaration a match is, edited as the
    /// list the roster's own Declare builds it from.
    /// </summary>
    private void DrawRoster()
    {
        ImGui.Separator();
        ImGui.Text("Οι ομάδες:");

        var teams = new List<MatchTeam>();
        bool touched = false;

        Span<int> playing = stackalloc int[SimConstants.TeamCount];
        int count = _mission.Roster.TeamsInPlayInto(playing);

        var inPlay = new bool[SimConstants.TeamCount];

        for (int i = 0; i < count; i++)
        {
            inPlay[playing[i]] = true;
        }

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            bool plays = inPlay[team];
            Faction faction = plays ? _mission.Roster.FactionOf(team) : Faction.Soviet;
            int side = plays ? _mission.Roster.SideOf(team) : team == 0 ? 0 : 1;
            bool judged = plays && _mission.Roster.IsJudged(team);

            string header = plays
                ? $"Ομάδα {team} — {GreekFaction(faction)}{(plays && !judged ? ", δεν κρίνεται" : string.Empty)}"
                : $"Ομάδα {team} — δεν παίζει";

            ImGui.PushID($"roster{team}");

            if (!ImGui.CollapsingHeader(header))
            {
                ImGui.PopID();
                continue;
            }

            bool nowPlays = ImGui.Checkbox("παίζει", ref plays);
            touched |= nowPlays != plays;

            if (nowPlays && ImGui.BeginCombo("Παράταξη", GreekFaction(faction)))
            {
                foreach (Faction choice in new[] { Faction.Soviet, Faction.Chinese, Faction.Western })
                {
                    if (ImGui.Selectable(GreekFaction(choice), choice == faction))
                    {
                        faction = choice;
                        touched = true;
                    }
                }

                ImGui.EndCombo();
            }

            if (nowPlays && ImGui.SliderInt("Πλευρά", ref side, 0, SimConstants.TeamCount - 1))
            {
                touched = true;
            }

            if (nowPlays && ImGui.Checkbox("κρίνεται από τη νίκη", ref judged))
            {
                touched = true;
            }

            if (nowPlays)
            {
                teams.Add(new MatchTeam(team, faction, side, judged));
            }

            ImGui.PopID();
        }

        if (touched && teams.Count > 0)
        {
            Commit(_mission with { Roster = MatchRoster.Declare([.. teams]) }, relayout: true);
        }
    }

    /// <summary>
    /// The starting forces: where each side's base is wished and how many units march with
    /// it. The ground answers where the base actually stands — the wish is the mission's
    /// data, and the search is the world's.
    /// </summary>
    private void DrawForces()
    {
        ImGui.Separator();
        ImGui.Text("Δυνάμεις:");

        int playerUnits = _mission.PlayerUnits;

        if (ImGui.SliderInt("Μονάδες σου", ref playerUnits, 0, 40) && ImGui.IsItemDeactivatedAfterEdit())
        {
            Commit(_mission with { PlayerUnits = playerUnits }, relayout: true);
        }

        if (_mission.Roster.IsInPlay(1))
        {
            int allyUnits = _mission.AllyUnits;

            if (ImGui.SliderInt("Σύμμαχος", ref allyUnits, 0, 40) && ImGui.IsItemDeactivatedAfterEdit())
            {
                Commit(_mission with { AllyUnits = allyUnits }, relayout: true);
            }
        }

        int enemyUnits = _mission.EnemyUnits;

        if (ImGui.SliderInt("Εχθρός", ref enemyUnits, 0, 40) && ImGui.IsItemDeactivatedAfterEdit())
        {
            Commit(_mission with { EnemyUnits = enemyUnits }, relayout: true);
        }
    }

    /// <summary>The objectives, one block each, with the fields their kind reads.</summary>
    private void DrawObjectives()
    {
        ImGui.Separator();
        ImGui.Text("Στόχοι:");

        for (int i = 0; i < _mission.Objectives.Count; i++)
        {
            ObjectiveDefinition objective = _mission.Objectives[i];

            ImGui.PushID($"obj{i}");

            string header = $"#{i} {GreekObjectiveKind(objective.Kind)} — {(objective.IsPrimary ? "κύριος" : "προαιρετικός")}";

            if (!ImGui.CollapsingHeader(header))
            {
                ImGui.PopID();
                continue;
            }

            string description = objective.GreekDescription;

            if (InputText("Περιγραφή", ref description, 128))
            {
                Commit(ObjectiveAt(i, objective with { GreekDescription = description }), relayout: false);
            }

            ObjectiveKind kind = objective.Kind;

            if (ImGui.BeginCombo("Είδος", GreekObjectiveKind(kind)))
            {
                foreach (ObjectiveKind candidate in Enum.GetValues<ObjectiveKind>())
                {
                    if (ImGui.Selectable(GreekObjectiveKind(candidate), candidate == kind))
                    {
                        Commit(ObjectiveAt(i, objective with { Kind = candidate }), relayout: false);
                    }
                }

                ImGui.EndCombo();
            }

            if (Field("Ομάδα", objective.Team, SimConstants.TeamCount - 1, out int team))
            {
                Commit(ObjectiveAt(i, objective with { Team = team }), relayout: false);
            }

            if (objective.Kind is ObjectiveKind.DestroyStructures or ObjectiveKind.DenyArea)
            {
                if (Field("Επιτιθέμενος", objective.TargetTeam, SimConstants.TeamCount - 1, out int targetTeam))
                {
                    Commit(ObjectiveAt(i, objective with { TargetTeam = targetTeam }), relayout: false);
                }
            }

            if (objective.Kind is ObjectiveKind.DestroyStructures or ObjectiveKind.HoldArea or
                ObjectiveKind.DenyArea or ObjectiveKind.AccumulateMaterials)
            {
                if (Field("Πλήθος", objective.TargetCount, 50, out int targetCount))
                {
                    Commit(ObjectiveAt(i, objective with { TargetCount = targetCount }), relayout: false);
                }
            }

            if (objective.Kind is ObjectiveKind.HoldArea or ObjectiveKind.DenyArea)
            {
                float centreX = objective.CentreX / (float)WorldPos.MmPerMetre;
                float centreZ = objective.CentreZ / (float)WorldPos.MmPerMetre;
                float radius = objective.RadiusMm / (float)WorldPos.MmPerMetre;

                bool areaChanged = Field("Κέντρο X (m)", centreX, -300f, 300f, out float outX) |
                    Field("Κέντρο Z (m)", centreZ, -300f, 300f, out float outZ) |
                    Field("Ακτίνα (m)", radius, 10f, 200f, out float outRadius);

                if (areaChanged)
                {
                    Commit(ObjectiveAt(i, objective with
                    {
                        CentreX = (int)(outX * WorldPos.MmPerMetre),
                        CentreZ = (int)(outZ * WorldPos.MmPerMetre),
                        RadiusMm = (int)(outRadius * WorldPos.MmPerMetre),
                    }), relayout: false);
                }

                if (objective.Kind == ObjectiveKind.HoldArea &&
                    Field("Κράτηση (s)", objective.HoldTicks / SimConstants.TickRate, 300, out int hold))
                {
                    Commit(ObjectiveAt(i, objective with { HoldTicks = hold * SimConstants.TickRate }), relayout: false);
                }
            }

            if (objective.Kind is ObjectiveKind.AccumulateMaterials &&
                Field("Απόθεμα (×100)", objective.MaterialsTarget / 100, 300, out int materials))
            {
                Commit(ObjectiveAt(i, objective with { MaterialsTarget = materials * 100 }), relayout: false);
            }

            if (objective.Kind is ObjectiveKind.ReachTechTier &&
                Field("Τεχνολογία", objective.TierTarget, 5, out int tier))
            {
                Commit(ObjectiveAt(i, objective with { TierTarget = tier }), relayout: false);
            }

            if (Field("Προθεσμία (s)", objective.DeadlineTick / SimConstants.TickRate, 7_200, out int deadline))
            {
                Commit(ObjectiveAt(i, objective with { DeadlineTick = deadline * SimConstants.TickRate }), relayout: false);
            }

            bool primary = objective.IsPrimary;

            if (ImGui.Checkbox("κύριος", ref primary))
            {
                Commit(ObjectiveAt(i, objective with { IsPrimary = primary }), relayout: false);
            }

            if (ImGui.SmallButton("Διαγραφή"))
            {
                var objectives = _mission.Objectives.ToList();
                objectives.RemoveAt(i);
                Commit(_mission with { Objectives = objectives }, relayout: false);
                ImGui.PopID();
                return;
            }

            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ Στόχος"))
        {
            var objectives = _mission.Objectives.ToList();
            objectives.Add(new ObjectiveDefinition(
                ObjectiveKind.DestroyStructures,
                "Καταστρέψτε μία εχθρική θέση.",
                Team: 0,
                TargetTeam: FirstEnemyTeam(),
                TargetCount: 1,
                DeadlineTick: _mission.TimeLimitTicks));

            Commit(_mission with { Objectives = objectives }, relayout: false);
        }
    }

    /// <summary>
    /// The script, one block per trigger, the vocabulary the file carries: the condition
    /// with the fields its kind reads, and the actions with theirs. The validator's refusal
    /// is the loop here, exactly as it is for the placements.
    /// </summary>
    private void DrawTriggers()
    {
        ImGui.Separator();
        ImGui.Text("Σενάριο:");

        for (int i = 0; i < _mission.Triggers.Count; i++)
        {
            TriggerDefinition trigger = _mission.Triggers[i];

            ImGui.PushID($"trg{i}");

            string header = $"{trigger.Id} — {GreekCondition(trigger.Condition.Kind)}" +
                (trigger.DependsOnOpeningWorld ? " (από τον αρχικό κόσμο)" : string.Empty);

            if (!ImGui.CollapsingHeader(header))
            {
                ImGui.PopID();
                continue;
            }

            string id = trigger.Id;

            if (InputText("Κωδικός", ref id, 32))
            {
                Commit(TriggerAt(i, trigger with { Id = id }), relayout: false);
            }

            TriggerCondition condition = trigger.Condition;

            if (ImGui.BeginCombo("Πότε", GreekCondition(condition.Kind)))
            {
                foreach (TriggerConditionKind candidate in Enum.GetValues<TriggerConditionKind>())
                {
                    if (ImGui.Selectable(GreekCondition(candidate), candidate == condition.Kind))
                    {
                        condition = condition with { Kind = candidate };
                    }
                }

                ImGui.EndCombo();
            }

            bool conditionChanged = false;

            if (Field("Ομάδα όρου", condition.Team, SimConstants.TeamCount - 1, out int conditionTeam))
            {
                condition = condition with { Team = conditionTeam };
                conditionChanged = true;
            }

            if (condition.Kind is TriggerConditionKind.UnitInArea or TriggerConditionKind.StructuresStandingBelow &&
                Field("Πλήθος όρου", condition.Count, 30, out int conditionCount))
            {
                condition = condition with { Count = conditionCount };
                conditionChanged = true;
            }

            if (condition.Kind == TriggerConditionKind.TimeElapsed &&
                Field("Στιγμή (s)", condition.Tick / SimConstants.TickRate, 7_200, out int conditionTick))
            {
                condition = condition with { Tick = conditionTick * SimConstants.TickRate };
                conditionChanged = true;
            }

            if (condition.Kind == TriggerConditionKind.FlagSet &&
                Field("Σημαία", condition.Flag, 7, out int conditionFlag))
            {
                condition = condition with { Flag = conditionFlag };
                conditionChanged = true;
            }

            if (conditionChanged)
            {
                Commit(TriggerAt(i, trigger with { Condition = condition }), relayout: false);
            }

            for (int a = 0; a < trigger.Actions.Count; a++)
            {
                ImGui.PushID($"act{a}");
                ImGui.Text("· ενέργεια");

                TriggerAction action = trigger.Actions[a];

                if (ImGui.BeginCombo("Είδος", GreekAction(action.Kind)))
                {
                    foreach (TriggerActionKind candidate in Enum.GetValues<TriggerActionKind>())
                    {
                        if (ImGui.Selectable(GreekAction(candidate), candidate == action.Kind))
                        {
                            Commit(ActionAt(i, a, action with { Kind = candidate }), relayout: false);
                        }
                    }

                    ImGui.EndCombo();
                }

                if (Field("Ομάδα", action.Team, SimConstants.TeamCount - 1, out int actionTeam))
                {
                    Commit(ActionAt(i, a, action with { Team = actionTeam }), relayout: false);
                }

                if (action.Kind == TriggerActionKind.Message)
                {
                    string text = action.GreekText;

                    if (InputText("Κείμενο", ref text, 160))
                    {
                        Commit(ActionAt(i, a, action with { GreekText = text }), relayout: false);
                    }
                }

                if (action.Kind == TriggerActionKind.Spawn)
                {
                    if (ImGui.BeginCombo("Ρόλος", UnitCatalog.GreekName(action.Role)))
                    {
                        foreach (UnitDefinition definition in UnitCatalog.All)
                        {
                            if (definition.IsBuilding)
                            {
                                continue;
                            }

                            if (ImGui.Selectable(UnitCatalog.GreekName(definition.Kind), definition.Kind == action.Role))
                            {
                                Commit(ActionAt(i, a, action with { Role = definition.Kind }), relayout: false);
                            }
                        }

                        ImGui.EndCombo();
                    }

                    if (Field("Πλήθος", action.Count, 30, out int spawnCount))
                    {
                        Commit(ActionAt(i, a, action with { Count = spawnCount }), relayout: false);
                    }

                    float spawnX = action.CentreX / (float)WorldPos.MmPerMetre;
                    float spawnZ = action.CentreZ / (float)WorldPos.MmPerMetre;

                    bool spawnMoved = Field("Κέντρο X (m)", spawnX, -300f, 300f, out float outX) |
                        Field("Κέντρο Z (m)", spawnZ, -300f, 300f, out float outZ);

                    if (spawnMoved)
                    {
                        Commit(ActionAt(i, a, action with
                        {
                            CentreX = (int)(outX * WorldPos.MmPerMetre),
                            CentreZ = (int)(outZ * WorldPos.MmPerMetre),
                        }), relayout: false);
                    }
                }

                if (action.Kind == TriggerActionKind.CompleteObjective &&
                    Field("Στόχος", action.Objective, Math.Max(0, _mission.Objectives.Count - 1), out int completed))
                {
                    Commit(ActionAt(i, a, action with { Objective = completed }), relayout: false);
                }

                if (action.Kind == TriggerActionKind.ChangeSide &&
                    Field("Νέα πλευρά", action.Side, 3, out int newSide))
                {
                    Commit(ActionAt(i, a, action with { Side = newSide }), relayout: false);
                }

                if (ImGui.SmallButton("Διαγραφή"))
                {
                    var actions = trigger.Actions.ToList();
                    actions.RemoveAt(a);
                    Commit(TriggerAt(i, trigger with { Actions = actions }), relayout: false);
                    ImGui.PopID();
                    break;
                }

                ImGui.PopID();
            }

            if (ImGui.SmallButton("+ Ενέργεια"))
            {
                var actions = trigger.Actions.ToList();
                actions.Add(new TriggerAction(TriggerActionKind.Message, GreekText: "…"));
                Commit(TriggerAt(i, trigger with { Actions = actions }), relayout: false);
            }

            if (ImGui.SmallButton("Διαγραφή σκανδάλης"))
            {
                var triggers = _mission.Triggers.ToList();
                triggers.RemoveAt(i);
                Commit(_mission with { Triggers = triggers }, relayout: false);
                ImGui.PopID();
                return;
            }

            ImGui.PopID();
        }

        if (ImGui.SmallButton("+ Σκανδάλη"))
        {
            var triggers = _mission.Triggers.ToList();
            triggers.Add(new TriggerDefinition(
                $"trigger{_mission.Triggers.Count + 1}",
                new TriggerCondition(TriggerConditionKind.TimeElapsed, Tick: 60),
                [new TriggerAction(TriggerActionKind.Message, GreekText: "…")],
                Note: "Γραμμένο από τον συντελεστή."));

            Commit(_mission with { Triggers = triggers }, relayout: false);
        }
    }

    private int FirstEnemyTeam()
    {
        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (_mission.Roster.IsInPlay(team) && team != MatchRoster.PlayerTeam)
            {
                return team;
            }
        }

        return 2;
    }

    /// <summary>One int field, committed when the drag ends rather than per frame.</summary>
    private static bool Field(string label, int value, int max, out int committed)
    {
        int current = value;

        if (ImGui.SliderInt(label, ref current, 0, Math.Max(value, max)) && ImGui.IsItemDeactivatedAfterEdit())
        {
            committed = current;
            return true;
        }

        committed = value;
        return false;
    }

    /// <summary>One float field, committed when the drag ends rather than per frame.</summary>
    private static bool Field(string label, float value, float min, float max, out float committed)
    {
        float current = value;

        if (ImGui.SliderFloat(label, ref current, min, max) && ImGui.IsItemDeactivatedAfterEdit())
        {
            committed = current;
            return true;
        }

        committed = value;
        return false;
    }

    /// <summary>The text field, committed when its content is done rather than per keystroke.</summary>
    private static bool InputText(string label, ref string value, uint maxLength)
        => ImGui.InputText(label, ref value, maxLength);

    /// <summary>Commits a mission-body change: the validator is asked and the world is re-laid when the layout moved.</summary>
    private void Commit(MissionDefinition mission, bool relayout) => ReplaceMission(mission, relayout);

    private static string GreekFaction(Faction faction) => faction switch
    {
        Faction.Soviet => "Σοβιετικοί",
        Faction.Chinese => "Κινέζοι",
        Faction.Western => "Δυτικοί",
        _ => faction.ToString(),
    };

    /// <summary>The mission with one objective replaced — the shape every objective widget commits through.</summary>
    private MissionDefinition ObjectiveAt(int index, ObjectiveDefinition objective) => _mission with
    {
        Objectives = ReplacedObjective(index, objective),
    };

    /// <summary>The mission with one trigger replaced.</summary>
    private MissionDefinition TriggerAt(int index, TriggerDefinition trigger) => _mission with
    {
        Triggers = ReplacedTrigger(index, trigger),
    };

    /// <summary>The mission with one action of one trigger replaced.</summary>
    private MissionDefinition ActionAt(int triggerIndex, int actionIndex, TriggerAction action) => _mission with
    {
        Triggers = ReplacedTrigger(
            triggerIndex,
            _mission.Triggers[triggerIndex] with { Actions = ReplacedAction(_mission.Triggers[triggerIndex], actionIndex, action) }),
    };

    private IReadOnlyList<ObjectiveDefinition> ReplacedObjective(int index, ObjectiveDefinition objective)
    {
        var objectives = _mission.Objectives.ToList();
        objectives[index] = objective;
        return objectives;
    }

    private IReadOnlyList<TriggerDefinition> ReplacedTrigger(int index, TriggerDefinition trigger)
    {
        var triggers = _mission.Triggers.ToList();
        triggers[index] = trigger;
        return triggers;
    }

    private IReadOnlyList<TriggerAction> ReplacedAction(TriggerDefinition trigger, int index, TriggerAction action)
    {
        var actions = trigger.Actions.ToList();
        actions[index] = action;
        return actions;
    }

    private static string GreekObjectiveKind(ObjectiveKind kind) => kind switch
    {
        ObjectiveKind.DestroyStructures => "Καταστροφή",
        ObjectiveKind.HoldArea => "Κράτηση",
        ObjectiveKind.SurviveTicks => "Επιβίωση",
        ObjectiveKind.ProtectCommandCentre => "Προστασία",
        ObjectiveKind.AccumulateMaterials => "Συσσώρευση",
        ObjectiveKind.ReachTechTier => "Τεχνολογία",
        ObjectiveKind.DenyArea => "Άρνηση",
        ObjectiveKind.Scripted => "Σενάριο",
        _ => kind.ToString(),
    };

    private static string GreekCondition(TriggerConditionKind kind) => kind switch
    {
        TriggerConditionKind.TimeElapsed => "Χρόνος",
        TriggerConditionKind.UnitInArea => "Μονάδα σε περιοχή",
        TriggerConditionKind.StructuresLost => "Χάθηκαν κτίρια",
        TriggerConditionKind.StructuresStandingBelow => "Λιγότερα στέκονται",
        TriggerConditionKind.FlagSet => "Σημαία",
        _ => kind.ToString(),
    };

    private static string GreekAction(TriggerActionKind kind) => kind switch
    {
        TriggerActionKind.Message => "Μήνυμα",
        TriggerActionKind.SetFlag => "Σημαία",
        TriggerActionKind.Spawn => "Γέννηση",
        TriggerActionKind.Reveal => "Αποκάλυψη",
        TriggerActionKind.AdjustResources => "Πόροι",
        TriggerActionKind.OrderGroup => "Διαταγή",
        TriggerActionKind.CompleteObjective => "Ολοκλήρωση",
        TriggerActionKind.ChangeSide => "Αλλαγή πλευράς",
        _ => kind.ToString(),
    };
}
