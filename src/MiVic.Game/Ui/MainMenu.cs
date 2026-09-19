using ImGuiNET;
using MiVic.Core.Campaign;
using MiVic.Game.Data;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace MiVic.Game.Ui;

/// <summary>What a menu screen asks the client to do.</summary>
public enum MenuCommandKind
{
    /// <summary>Nothing requested.</summary>
    None = 0,

    /// <summary>Start the campaign mission named by <see cref="MenuCommand.MissionId"/>.</summary>
    StartMission = 1,

    /// <summary>Start a skirmish, which is the match this game has always opened on.</summary>
    StartSkirmish = 2,

    /// <summary>Restore the saved match at <see cref="MenuCommand.SavePath"/>.</summary>
    LoadSave = 3,

    /// <summary>Leave.</summary>
    Exit = 4,
}

/// <summary>A request raised by a menu button, applied by the client.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="MissionId">Which mission to start, for <see cref="MenuCommandKind.StartMission"/>.</param>
/// <param name="SavePath">Which saved match to restore, for <see cref="MenuCommandKind.LoadSave"/>.</param>
/// <param name="ResetProgress">
/// True when the choice was <em>Νέα εκστρατεία</em>, which clears the record before it starts:
/// starting over is an act, not a navigation.
/// </param>
public readonly record struct MenuCommand(
    MenuCommandKind Kind,
    string MissionId = "",
    string SavePath = "",
    bool ResetProgress = false);

/// <summary>
/// The front end: the screen the game opens on, offering what a player sits down to.
/// <para>
/// The menu is a screen over the game the build always opened straight into, which stays
/// on the map behind it — paused, because a battle nobody is watching is not a battle —
/// and the menu's answers replace that battle wholesale: a mission, a skirmish, or a saved
/// match restored from its own command log. The items are the ones §1 asks for, minus the
/// two that do not exist yet: the map editor and multiplayer are listed as later rather
/// than as buttons that do nothing, because a button that does nothing is a lie with a
/// border around it.
/// </para>
/// <para>
/// <b>Συνέχεια</b> continues the campaign: the first mission the player has not won yet.
/// <b>Νέα εκστρατεία</b> clears the record and starts the first. <b>Αποστολές</b> lists what
/// has been won — a single mission is playable once the campaign has decided it, which is
/// the reading §1 gives <em>Μεμονομενες αποστολές</em>. <b>Μάχη</b> is the skirmish.
/// <b>Φόρτωση</b> offers the saved matches, newest first.
/// </para>
/// </summary>
public sealed class MainMenu
{
    /// <summary>Which menu layer is showing: the front page, the missions or the saves.</summary>
    private MenuPage _page = MenuPage.Front;

    /// <summary>Draws the menu, and answers what was asked. One command per frame at most.</summary>
    public MenuCommand? Draw(CampaignProgress progress, IReadOnlyList<SaveEntry> saves)
    {
        NVec2 display = ImGui.GetIO().DisplaySize;

        // One window, centred: the menu is a piece of writing with buttons in it, not a
        // panel arrangement, and content sizing lets a small window fold rather than fight.
        ImGui.SetNextWindowPos(new NVec2(display.X * 0.5f, display.Y * 0.5f), ImGuiCond.Always, new NVec2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new NVec2(430f, 0f));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0.020f, 0.030f, 0.050f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, new NVec4(0.30f, 0.38f, 0.48f, 0.85f));
        ImGui.PushStyleColor(ImGuiCol.Button, new NVec4(0.10f, 0.13f, 0.17f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonHovered, new NVec4(0.16f, 0.20f, 0.26f, 1f));
        ImGui.PushStyleColor(ImGuiCol.ButtonActive, new NVec4(0.18f, 0.24f, 0.30f, 1f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(28f, 24f));

        MenuCommand? raised = null;

        if (ImGui.Begin("##menu", PanelFlags))
        {
            ImGui.TextColored(new NVec4(0.90f, 0.88f, 0.80f, 1f), "MiVic");
            ImGui.TextColored(new NVec4(0.62f, 0.68f, 0.74f, 1f), "Στρατηγική Πραγματικού Χρόνου");
            ImGui.Separator();

            switch (_page)
            {
                case MenuPage.Front:
                    raised = DrawFront(progress, saves);
                    break;
                case MenuPage.Missions:
                    raised = DrawMissions(progress);
                    break;
                case MenuPage.Saves:
                    raised = DrawSaves(saves);
                    break;
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(5);

        return raised;
    }

    /// <summary>Forgets a command the client has taken, and the page it was raised on.</summary>
    public void Clear()
    {
        _page = MenuPage.Front;
    }

    private MenuCommand? DrawFront(CampaignProgress progress, IReadOnlyList<SaveEntry> saves)
    {
        MenuCommand? raised = null;

        string? next = CampaignProgress.NextUnwon(CampaignCatalog.Soviet.MissionIds, progress.WonMissions);

        // Συνέχεια: the campaign where the player left it. When nothing has been won this
        // is the first mission, and when everything has been won the campaign is over and
        // the button says so rather than pretending.
        if (next is null)
        {
            DrawDisabled("Συνέχεια — η εκστρατεία τελείωσε");
        }
        else
        {
            string note = progress.WonMissions.Count > 0
                ? MissionCatalog.Find(next)?.GreekTitle ?? next
                : "η πρώτη αποστολή της εκστρατείας";

            if (DrawButton("Συνέχεια", note))
            {
                raised = new MenuCommand(MenuCommandKind.StartMission, next);
            }
        }

        if (DrawButton("Νέα εκστρατεία", "ξεκινήστε από την αρχή"))
        {
            raised = new MenuCommand(MenuCommandKind.StartMission, CampaignCatalog.Soviet.MissionIds[0], ResetProgress: true);
        }

        if (DrawButton("Αποστολές", "αυτές που η εκστρατεία έχει κρίνει"))
        {
            _page = MenuPage.Missions;
        }

        if (DrawButton("Μάχη", "η μονομαχία των τριών στρατών"))
        {
            raised = new MenuCommand(MenuCommandKind.StartSkirmish);
        }

        if (DrawButton("Φόρτωση", saves.Count > 0 ? $"{saves.Count} αποθηκευμένες μάχες" : "καμία αποθηκευμένη μάχη"))
        {
            _page = MenuPage.Saves;
        }

        if (DrawButton("Έξοδος", string.Empty))
        {
            raised = new MenuCommand(MenuCommandKind.Exit);
        }

        return raised;
    }

    private MenuCommand? DrawMissions(CampaignProgress progress)
    {
        MenuCommand? raised = null;

        bool unlocked = true;

        foreach (string missionId in CampaignCatalog.Soviet.MissionIds)
        {
            MissionDefinition? found = MissionCatalog.Find(missionId);

            if (found is not { } mission)
            {
                continue;
            }

            bool won = progress.HasWon(mission.Id);

            if (won || unlocked)
            {
                if (DrawButton(mission.GreekTitle, won ? "κερδισμένη — ξαναπαίξτε την" : "η επόμενη της εκστρατείας"))
                {
                    raised = new MenuCommand(MenuCommandKind.StartMission, mission.Id);
                }

                // The campaign hands its missions out in order: the next un-won one is
                // playable — Συνέχεια would take the player there — and the ones behind
                // it are not. A mission stays playable for ever once the campaign has
                // decided it.
                if (!won)
                {
                    unlocked = false;
                }
            }
            else
            {
                DrawLocked(mission.GreekTitle);
            }
        }

        if (DrawButton("Πίσω", string.Empty))
        {
            _page = MenuPage.Front;
        }

        return raised;
    }

    private MenuCommand? DrawSaves(IReadOnlyList<SaveEntry> saves)
    {
        MenuCommand? raised = null;

        if (saves.Count == 0)
        {
            ImGui.TextColored(new NVec4(0.55f, 0.60f, 0.66f, 1f), "Καμία αποθηκευμένη μάχη.");
        }

        foreach (SaveEntry save in saves)
        {
            string where = save.MissionId is null ? "μάχη" : MissionCatalog.Find(save.MissionId)?.GreekTitle ?? save.MissionId;
            string label = $"{save.Name}  —  {where}, tick {save.Tick}";

            if (ImGui.Button(label, new NVec2(-1f, 0f)))
            {
                raised = new MenuCommand(MenuCommandKind.LoadSave, SavePath: save.Path);
            }
        }

        if (DrawButton("Πίσω", string.Empty))
        {
            _page = MenuPage.Front;
        }

        return raised;
    }

    /// <summary>One menu row: the button and, when there is one, the sentence under it.</summary>
    private static bool DrawButton(string label, string note)
    {
        bool pressed = ImGui.Button(label, new NVec2(-1f, 0f));

        if (note.Length > 0)
        {
            ImGui.TextColored(new NVec4(0.50f, 0.56f, 0.62f, 1f), note);
        }

        ImGui.Spacing();
        return pressed;
    }

    private static void DrawDisabled(string label)
    {
        ImGui.BeginDisabled();
        ImGui.Button(label, new NVec2(-1f, 0f));
        ImGui.EndDisabled();
        ImGui.Spacing();
    }

    private static void DrawLocked(string label)
    {
        ImGui.BeginDisabled();
        ImGui.Button(label, new NVec2(-1f, 0f));
        ImGui.EndDisabled();
        ImGui.SameLine();
        ImGui.TextColored(new NVec4(0.62f, 0.50f, 0.32f, 1f), "κλειδωμένη");
        ImGui.Spacing();
    }

    private const ImGuiWindowFlags PanelFlags =
        ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings;

    private enum MenuPage : byte
    {
        Front = 0,
        Missions = 1,
        Saves = 2,
    }
}
