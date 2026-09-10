using ImGuiNET;
using MiVic.Core.Campaign;
using MiVic.Core.Sim;
using MiVic.Game.Camera;
using MiVic.Game.Data;
using MiVic.Game.Sim;
using Microsoft.Xna.Framework;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace MiVic.Game.Ui;

/// <summary>What the HUD is asking the client to do.</summary>
public enum HudCommandKind
{
    /// <summary>Nothing requested.</summary>
    None = 0,

    /// <summary>Queue a unit at the selected building.</summary>
    QueueUnit = 1,

    /// <summary>Start research at the selected design bureau.</summary>
    Research = 2,

    /// <summary>Grant the ally a licence for one design.</summary>
    Licence = 3,

    /// <summary>Run a prototype so factories may build the design.</summary>
    ApproveDesign = 4,

    /// <summary>Call in an off-map ability; the client then asks for a target.</summary>
    UseAbility = 5,

    /// <summary>Place a bridge; the client then asks for a target.</summary>
    BuildBridge = 6,
}

/// <summary>A request raised by a HUD button, applied by the client as a command.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="Unit">Role to queue, for <see cref="HudCommandKind.QueueUnit"/>.</param>
/// <param name="Tech">Project to start, for <see cref="HudCommandKind.Research"/>.</param>
/// <param name="Ability">Off-map support to call in, for <see cref="HudCommandKind.UseAbility"/>.</param>
public readonly record struct HudCommand(
    HudCommandKind Kind,
    UnitKind Unit = UnitKind.None,
    TechId Tech = TechId.None,
    AbilityId Ability = AbilityId.None);

/// <summary>Everything the HUD needs for one frame, captured by the client.</summary>
/// <param name="Simulation">Simulation bridge to read state from.</param>
/// <param name="Camera">Active camera, for the zoom readout.</param>
/// <param name="FramesPerSecond">Smoothed frames per second.</param>
/// <param name="FrameMilliseconds">Smoothed frame time in milliseconds.</param>
/// <param name="InstanceCount">Units submitted this frame.</param>
/// <param name="DrawCalls">Instanced draw calls issued this frame.</param>
/// <param name="GreekGlyphsOk">Whether the UI font actually carries Greek glyphs.</param>
/// <param name="ModelsLoaded">Number of imported 3D models in use.</param>
/// <param name="ModelsFailed">Number of model slots that fell back to procedural geometry.</param>
/// <param name="SelectedCount">Units currently selected.</param>
/// <param name="SelectedBuildingSlot">Slot of the single selected building, or -1.</param>
/// <param name="SelectedUnitSlot">Slot of the single selected unit, or -1.</param>
/// <param name="AllyBuildingSlot">Slot of the ally's first building, or -1.</param>
/// <param name="LargeFont">Headline font, loaded at a larger size.</param>
/// <param name="Playback">True when a recorded match is being played back.</param>
/// <param name="PlaybackFinished">True once playback has run past the end of the recording.</param>
/// <param name="BridgeArmed">True while a bridge is waiting for the player to pick a site.</param>
/// <param name="BridgeSiteReason">
/// Why the site under the cursor would be refused, or an empty string when it would be taken.
/// The ghost says <em>that</em> a site fails; this is the words for why.
/// </param>
/// <param name="BridgeSiteCells">
/// Cells the crossing under the cursor would span, or zero when there is no site. The price of
/// a bridge depends on its length, so this is what the panel quotes.
/// </param>
public readonly record struct HudSnapshot(
    SimBridge Simulation,
    RtsCamera Camera,
    float FramesPerSecond,
    float FrameMilliseconds,
    int InstanceCount,
    int DrawCalls,
    bool GreekGlyphsOk,
    int ModelsLoaded,
    int ModelsFailed,
    int SelectedCount,
    int SelectedBuildingSlot,
    int SelectedUnitSlot,
    int AllyBuildingSlot,
    ImFontPtr LargeFont,
    bool Playback,
    bool PlaybackFinished,
    bool BridgeArmed = false,
    string BridgeSiteReason = "",
    int BridgeSiteCells = 0);

/// <summary>
/// The in-game HUD. Every player-facing string is Greek, which is also the
/// smoke test for the font pipeline: if the glyph ranges are wrong, this window
/// is where it shows up first.
/// </summary>
public sealed class GameHud
{
    private static readonly NVec4 WarningColor = new(0.95f, 0.45f, 0.30f, 1f);
    private static readonly NVec4 MutedColor = new(0.62f, 0.66f, 0.70f, 1f);

    private const ImGuiWindowFlags PanelFlags =
        ImGuiWindowFlags.AlwaysAutoResize |
        ImGuiWindowFlags.NoSavedSettings |
        ImGuiWindowFlags.NoFocusOnAppearing |
        ImGuiWindowFlags.NoNav |
        ImGuiWindowFlags.NoNavFocus;

    /// <summary>Whether the controls panel is shown.</summary>
    public bool ShowHelp { get; set; } = true;

    /// <summary>Whether the mission briefing is expanded. Objectives stay visible either way.</summary>
    public bool ShowBriefing { get; set; } = true;

    /// <summary>How long a transient notice stays on screen, in seconds.</summary>
    private const float NoticeSeconds = 4f;

    private string _notice = string.Empty;
    private float _noticeRemaining;

    /// <summary>
    /// Shows a transient line for a moment, for feedback about something the player just did
    /// that would otherwise leave no trace at all.
    /// <para>
    /// It exists because the alternative was silence. A refused order — a bridge site that is
    /// too wide to span, or clicked on dry ground — changed nothing on screen, so a player who
    /// had mis-clicked and a player whose click had never reached the game saw exactly the same
    /// thing: nothing. The simulation has always known the reason; this is where it is put in
    /// front of the person who needs it.
    /// </para>
    /// </summary>
    public void Notify(string text)
    {
        if (text.Length == 0)
        {
            return;
        }

        _notice = text;
        _noticeRemaining = NoticeSeconds;
    }

    /// <summary>The notice currently on screen, for the probe and for diagnostics.</summary>
    public string Notice => _noticeRemaining > 0f ? _notice : string.Empty;

    /// <summary>
    /// Height of the panel drawn in the bottom-left corner, measured as it is drawn. The
    /// corner holds two panels — production or its hint, and support — and the second of them
    /// starts above the first rather than on top of it.
    /// </summary>
    private float _bottomLeftHeight;

    /// <summary>Draws the HUD and returns any action the player triggered.</summary>
    public HudCommand? Draw(in HudSnapshot snapshot)
    {
        // The HUD sits on top of a bright, moving battlefield, so every panel gets
        // a near-opaque backing and a border: plain ImGui text was competing with
        // tanks and terrain for legibility.
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0.035f, 0.045f, 0.06f, 0.92f));
        ImGui.PushStyleColor(ImGuiCol.Border, new NVec4(0.32f, 0.40f, 0.50f, 0.70f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);

        DrawStatusPanel(snapshot);

        DrawMissionPanel(snapshot);

        HudCommand? command = DrawBuildPanel(snapshot);
        HudCommand? support = DrawSupportPanel(snapshot);

        if (support is not null)
        {
            command = support;
        }

        if (ShowHelp)
        {
            DrawHelpPanel();
        }

        // Before the outcome banner, which dims the whole screen: a notice about a click is
        // about the match that is still being played.
        DrawNotice(snapshot.FrameMilliseconds / 1000f);

        DrawOutcome(snapshot);

        ImGui.PopStyleVar(1);
        ImGui.PopStyleColor(2);

        return command;
    }

    /// <summary>
    /// The transient notice, bottom centre: last, so it is drawn over the panels rather than
    /// under them, and out of the corners the production and controls panels already own.
    /// </summary>
    /// <param name="elapsedSeconds">
    /// Wall-clock frame time, not simulation time. A message about a click belongs to the
    /// person who clicked, and it must not sit on screen for ever because the match is paused.
    /// </param>
    private void DrawNotice(float elapsedSeconds)
    {
        if (_noticeRemaining <= 0f)
        {
            return;
        }

        _noticeRemaining -= elapsedSeconds;

        if (_noticeRemaining <= 0f)
        {
            _notice = string.Empty;
            return;
        }

        NVec2 display = ImGui.GetIO().DisplaySize;

        // Above the bottom edge rather than on it, so it clears the help panel's own height
        // and the window border on a short screen.
        ImGui.SetNextWindowPos(new NVec2(display.X * 0.5f, display.Y - 12f), ImGuiCond.Always, new NVec2(0.5f, 1f));

        // Fades over its last second, so a message that is no longer news stops competing with
        // the map without vanishing between two frames.
        float alpha = MathF.Min(_noticeRemaining, 1f);

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0.05f, 0.03f, 0.03f, 0.92f * alpha));
        ImGui.PushStyleColor(ImGuiCol.Border, new NVec4(WarningColor.X, WarningColor.Y, WarningColor.Z, 0.75f * alpha));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(14f, 8f));

        if (ImGui.Begin("##notice", PanelFlags | ImGuiWindowFlags.NoTitleBar))
        {
            ImGui.TextColored(new NVec4(WarningColor.X, WarningColor.Y, WarningColor.Z, alpha), _notice);
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    /// <summary>Centred banner shown once the battle is decided.</summary>
    private static void DrawOutcome(in HudSnapshot snapshot)
    {
        GameOutcome outcome = snapshot.Simulation.World.Outcome;

        if (outcome == GameOutcome.Ongoing)
        {
            return;
        }

        string headline = outcome switch
        {
            GameOutcome.AllianceVictory => "ΝΙΚΗ",
            GameOutcome.WesternVictory => "ΗΤΤΑ",
            _ => "ΙΣΟΠΑΛΙΑ",
        };

        string detail = outcome switch
        {
            GameOutcome.AllianceVictory => "Η Δυτική αυτοκρατορία έπεσε. Ο δρόμος για έναν ειρηνικό, σοσιαλιστικό κόσμο είναι ανοιχτός.",
            GameOutcome.WesternVictory => "Η συμμαχία διαλύθηκε. Η Δύση κυριαρχεί.",
            _ => "Και οι δύο πλευρές εξοντώθηκαν.",
        };

        NVec4 accent = outcome == GameOutcome.AllianceVictory
            ? new NVec4(0.45f, 1f, 0.5f, 1f)
            : WarningColor;

        NVec2 display = ImGui.GetIO().DisplaySize;

        // Dim the whole battlefield first. The banner is only readable if the
        // scene behind it stops competing with it, and a scrim also reads as the
        // end of the match rather than as one more floating panel.
        ImGui.GetBackgroundDrawList().AddRectFilled(NVec2.Zero, display, 0x8C000000u);

        const float PanelWidth = 620f;

        ImGui.SetNextWindowPos(
            new NVec2(display.X * 0.5f, display.Y * 0.44f),
            ImGuiCond.Always,
            new NVec2(0.5f, 0.5f));

        // A fixed width keeps the long sentence on two short lines instead of one
        // strip running the width of the screen and across the units.
        ImGui.SetNextWindowSizeConstraints(
            new NVec2(PanelWidth, 0f),
            new NVec2(PanelWidth, 100_000f));

        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(24f, 20f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0.03f, 0.04f, 0.06f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, new NVec4(accent.X, accent.Y, accent.Z, 0.60f));

        const ImGuiWindowFlags Flags =
            ImGuiWindowFlags.AlwaysAutoResize |
            ImGuiWindowFlags.NoSavedSettings |
            ImGuiWindowFlags.NoNav |
            ImGuiWindowFlags.NoNavFocus |
            ImGuiWindowFlags.NoTitleBar |
            ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove;

        if (ImGui.Begin("##outcome", Flags))
        {
            ImGui.PushFont(snapshot.LargeFont);

            float offset = MathF.Max((ImGui.GetContentRegionAvail().X - ImGui.CalcTextSize(headline).X) * 0.5f, 0f);
            ImGui.SetCursorPosX(ImGui.GetCursorPosX() + offset);
            ImGui.TextColored(accent, headline);

            ImGui.PopFont();
            ImGui.Separator();
            ImGui.TextWrapped(detail);
        }

        ImGui.End();

        ImGui.PopStyleColor(2);
        ImGui.PopStyleVar(2);
    }

    /// <summary>
    /// The mission objectives panel. Status glyphs come from ranges the UI font
    /// actually carries (• √ ×) — the dingbat tick and cross are not in the atlas
    /// and would render as tofu.
    /// </summary>
    private void DrawMissionPanel(in HudSnapshot snapshot)
    {
        MissionDefinition? mission = snapshot.Simulation.World.Mission;

        if (mission is null)
        {
            return;
        }

        ReadOnlySpan<ObjectiveState> states = snapshot.Simulation.World.Objectives;
        NVec2 display = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(new NVec2(display.X - 12f, 12f), ImGuiCond.Always, new NVec2(1f, 0f));
        ImGui.SetNextWindowSizeConstraints(new NVec2(380f, 0f), new NVec2(440f, 100_000f));

        if (!ImGui.Begin("Αποστολή##mission", PanelFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(new NVec4(0.85f, 0.90f, 1f, 1f), mission.GreekTitle);

        if (ShowBriefing)
        {
            ImGui.Separator();
            ImGui.TextWrapped(mission.GreekBriefing);

            if (ImGui.SmallButton("Απόκρυψη ενημέρωσης"))
            {
                ShowBriefing = false;
            }
        }

        ImGui.Separator();
        ImGui.TextUnformatted("Στόχοι");

        for (int i = 0; i < mission.Objectives.Count && i < states.Length; i++)
        {
            ObjectiveDefinition definition = mission.Objectives[i];
            ObjectiveState state = states[i];

            (string glyph, NVec4 color) = state.Status switch
            {
                ObjectiveStatus.Complete => ("√", new NVec4(0.45f, 1f, 0.5f, 1f)),
                ObjectiveStatus.Failed => ("×", WarningColor),
                _ => ("•", new NVec4(0.80f, 0.82f, 0.86f, 1f)),
            };

            ImGui.TextColored(color, $"{glyph} {definition.GreekDescription}");

            string progress = ProgressText(definition, state);

            if (progress.Length > 0)
            {
                ImGui.TextColored(MutedColor, $"     {progress}");
            }
        }

        if (mission.TimeLimitTicks > 0)
        {
            long remainingTicks = mission.TimeLimitTicks - snapshot.Simulation.World.Tick;

            if (remainingTicks < 0)
            {
                remainingTicks = 0;
            }

            int seconds = (int)(remainingTicks / SimConstants.TickRate);
            NVec4 color = seconds <= 60 ? WarningColor : MutedColor;

            ImGui.Separator();
            ImGui.TextColored(color, $"Χρόνος που απομένει: {seconds / 60}:{seconds % 60:00}");
        }

        ImGui.End();
    }

    /// <summary>Progress readout for an objective, or an empty string when it needs none.</summary>
    private static string ProgressText(in ObjectiveDefinition definition, in ObjectiveState state)
        => definition.Kind switch
        {
            ObjectiveKind.DestroyStructures => $"{state.Progress}/{definition.TargetCount} κατασκευές",
            ObjectiveKind.HoldArea =>
                $"{state.Progress}/{definition.TargetCount} μονάδες, " +
                $"{state.HoldProgress / SimConstants.TickRate}/{definition.HoldTicks / SimConstants.TickRate} δευτ.",
            ObjectiveKind.AccumulateMaterials => $"{state.Progress}/{definition.MaterialsTarget} πόροι",
            ObjectiveKind.ReachTechTier => $"επίπεδο {state.Progress}/{definition.TierTarget}",
            _ => string.Empty,
        };

    private static void DrawStatusPanel(in HudSnapshot snapshot)
    {
        SimWorld world = snapshot.Simulation.World;

        ImGui.SetNextWindowPos(new NVec2(12f, 12f), ImGuiCond.Always);

        if (!ImGui.Begin("MiVic##status", PanelFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("MiVic — Στρατηγική Πραγματικού Χρόνου");
        ImGui.SameLine();
        ImGui.TextColored(MutedColor, GameVersion.Display);
        ImGui.TextColored(MutedColor, "Ο στόχος: η ήττα της Δυτικής αυτοκρατορίας.");
        ImGui.Separator();

        if (snapshot.Playback)
        {
            ImGui.TextColored(
                snapshot.PlaybackFinished ? MutedColor : new NVec4(0.95f, 0.80f, 0.35f, 1f),
                snapshot.PlaybackFinished ? "ΑΝΑΠΑΡΑΓΩΓΗ — τέλος" : "ΑΝΑΠΑΡΑΓΩΓΗ");
            ImGui.Separator();
        }

        ImGui.Text($"Τικ: {world.Tick}");
        ImGui.SameLine(160f);
        ImGui.Text($"FPS: {snapshot.FramesPerSecond:0}");

        ImGui.Text($"Χρόνος καρέ: {snapshot.FrameMilliseconds:0.00} ms");
        ImGui.Text($"Μονάδες: {world.AliveCount}    Στιγμιότυπα: {snapshot.InstanceCount}    Κλήσεις: {snapshot.DrawCalls}");

        ImGui.Separator();
        ImGui.TextUnformatted("Παράταξη");

        foreach (FactionProfile profile in FactionProfile.All)
        {
            int count = CountUnits(world, profile.Faction);
            NVec4 color = ToVector4(FactionPalette.Primary(profile.Faction));

            ImGui.TextColored(color, profile.GreekName);
            ImGui.SameLine(150f);
            ImGui.Text($"{count,4}");

            ImGui.SameLine(210f);
            ImGui.TextColored(MutedColor, $"Τεχνολογία {profile.TechCeiling}   Παραγωγή {profile.ProductionSlots}");

            // Faction ids map onto team slots by design: Soviet 1 -> team 0, and so on.
            int bonus = world.Team((int)profile.Faction - 1).BonusSlots;

            if (bonus > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(MutedColor, $"+{bonus}");
            }
        }

        ImGui.Separator();

        // Player resources.
        TeamState player = world.Team(0);
        ImGui.Text($"Πόροι: {player.Materials}  (+{player.MaterialsPerTick})");

        // The Western army has to be paid for. A player whose morale is collapsing
        // needs to see why, or it reads as a bug.
        if (player.UpkeepPerTick > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(MutedColor, $"  −{player.UpkeepPerTick} υποχρεώσεις");
        }

        if (!player.PropagandaPaid || !player.WagesPaid)
        {
            ImGui.TextColored(WarningColor, !player.WagesPaid
                ? "Ανεπλήρωτοι μισθοφόροι — αρνούνται να πολεμήσουν."
                : "Ανεπλήρωτη προπαγάνδα — το ηθικό πέφτει.");
        }

        ImGui.Text($"Ενέργεια: {player.Energy}  ({(player.EnergyPerTick >= 0 ? "+" : string.Empty)}{player.EnergyPerTick})");
        ImGui.Text($"Νερό: {player.Water}  (+{player.WaterPerTick})");
        ImGui.Text($"Τεχνολογία: {player.TechTier}");

        if (player.IsResearching)
        {
            ImGui.Text($"Έρευνα: {player.ResearchTicksRemaining} τικ");
        }

        ImGui.Separator();
        ImGui.Text($"Ζουμ: {snapshot.Camera.Distance:0} m    Επιλεγμένα: {snapshot.SelectedCount}");
        ImGui.Text($"Μοντέλα: {snapshot.ModelsLoaded}");

        if (snapshot.ModelsFailed > 0)
        {
            ImGui.SameLine();
            ImGui.TextColored(WarningColor, $"ελλιπή: {snapshot.ModelsFailed}");
        }

        if (!snapshot.GreekGlyphsOk)
        {
            ImGui.TextColored(WarningColor, "ΠΡΟΕΙΔΟΠΟΙΗΣΗ: η γραμματοσειρά δεν περιέχει ελληνικούς χαρακτήρες.");
        }

        DrawSelectedUnit(snapshot);

        ImGui.End();
    }

    /// <summary>Health and morale of the single selected unit.</summary>
    private static void DrawSelectedUnit(in HudSnapshot snapshot)
    {
        int slot = snapshot.SelectedUnitSlot;

        if (slot < 0)
        {
            return;
        }

        SimWorld world = snapshot.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            return;
        }

        ref Entity unit = ref world.GetRefBySlot(slot);
        UnitDefinition definition = UnitCatalog.Get(unit.Kind);

        ImGui.Separator();
        ImGui.TextColored(ToVector4(FactionPalette.Primary(unit.Faction)), FactionProfile.For(unit.Faction).GreekName);
        ImGui.SameLine();
        ImGui.TextUnformatted(FactionPalette.UnitLabel(unit.Kind));

        float health = definition.Health > 0 ? Math.Clamp((float)unit.Health / definition.Health, 0f, 1f) : 0f;
        ImGui.ProgressBar(health, new NVec2(-1f, 12f), $"Υγεία {unit.Health}/{definition.Health}");

        float morale = unit.Morale.ToFloat();
        ImGui.ProgressBar(morale, new NVec2(-1f, 12f), $"Ηθικό {morale:P0}");

        if (unit.Routed)
        {
            ImGui.TextColored(WarningColor, "Υποχωρεί!");
        }
    }

    /// <summary>
    /// Shows what the selected building can make and what it is already making.
    /// Returns a command when the player presses a button.
    /// </summary>
    private HudCommand? DrawBuildPanel(in HudSnapshot snapshot)
    {
        int slot = snapshot.SelectedBuildingSlot;

        if (slot < 0)
        {
            DrawBuildHint();
            return null;
        }

        SimWorld world = snapshot.Simulation.World;

        if (!world.IsAliveSlot(slot))
        {
            DrawBuildHint();
            return null;
        }

        ref Entity building = ref world.GetRefBySlot(slot);
        FactionProfile profile = FactionProfile.For(building.Faction);
        TeamState team = world.Team(building.TeamId);

        // Anchored to the bottom-left corner so the panel grows upwards. It used
        // to sit at a fixed y = 430, which put every build button below the
        // bottom of a 720-pixel window — the player could see the panel and not
        // click a single thing in it.
        NVec2 display = ImGui.GetIO().DisplaySize;
        ImGui.SetNextWindowPos(new NVec2(12f, display.Y - 12f), ImGuiCond.Always, new NVec2(0f, 1f));
        ImGui.SetNextWindowSizeConstraints(new NVec2(0f, 0f), new NVec2(520f, display.Y * 0.62f));

        HudCommand? command = null;

        if (!ImGui.Begin("Παραγωγή##build", PanelFlags))
        {
            ImGui.End();
            return null;
        }

        ImGui.TextColored(ToVector4(FactionPalette.Primary(building.Faction)), profile.GreekName);
        ImGui.SameLine();
        ImGui.TextColored(MutedColor, FactionPalette.UnitLabel(building.Kind));
        ImGui.Separator();

        ReadOnlySpan<ProductionJob> jobs = world.JobsOf(slot);

        if (jobs.Length == 0)
        {
            ImGui.TextColored(MutedColor, "Καμία παραγγελία");
        }
        else
        {
            for (int i = 0; i < jobs.Length; i++)
            {
                ProductionJob job = jobs[i];

                // Completed command automation adds slots, so the active marker has
                // to follow the team's effective slot count, not the faction's base.
                bool active = i < profile.ProductionSlots + team.BonusSlots;

                ImGui.Text($"{(active ? "▶" : "·")} {FactionPalette.UnitLabel(job.Kind)}");
                ImGui.SameLine(190f);
                ImGui.TextColored(MutedColor, $"{job.RemainingTicks} τικ");
            }
        }

        ImGui.Separator();

        IReadOnlyList<BuildOption> options = DescribeBuildOptions(world, slot);

        if (options.Count == 0)
        {
            // A power plant produces nothing, and a bare panel with no explanation
            // reads as a bug rather than as a fact about the building.
            ImGui.TextColored(MutedColor, "Το κτίριο δεν παράγει μονάδες.");
        }

        foreach (BuildOption option in options)
        {
            ImGui.BeginDisabled(!option.Enabled);

            if (ImGui.Button(option.Label))
            {
                command = new HudCommand(HudCommandKind.QueueUnit, option.Kind);
            }

            ImGui.EndDisabled();

            if (option.Reason.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(MutedColor, option.Reason);
            }
        }

        if (building.Kind == UnitKind.DesignBureau)
        {
            ImGui.Separator();
            ImGui.TextColored(MutedColor, "Έρευνα");

            if (team.IsResearching && TechCatalog.TryGet(team.ResearchingTech, out TechProject running))
            {
                ImGui.Text($"{running.GreekName} — {team.ResearchTicksRemaining} τικ");
            }

            foreach (TechProject project in TechCatalog.Available(building.Faction, team.TechTier, team.TechMask))
            {
                int ticks = TechCatalog.TicksFor(building.Faction, project);
                string label = $"{project.GreekName,-26} {project.Cost,4}Π {ticks / 20f,5:0.0}δ";

                ImGui.BeginDisabled(team.IsResearching || team.Materials < project.Cost);

                if (ImGui.Button(label))
                {
                    command = new HudCommand(HudCommandKind.Research, UnitKind.None, project.Id);
                }

                ImGui.EndDisabled();

                if (ImGui.IsItemHovered())
                {
                    ImGui.SetTooltip(project.GreekDescription);
                }
            }

            // Σοβιετικοί: a factory design must be proven by a prototype run before
            // any factory may build it. Without this the player cannot produce a
            // tank at all, and the panel would look broken rather than gated.
            if (building.Faction == Faction.Soviet)
            {
                ImGui.Separator();
                ImGui.TextColored(MutedColor, "Πρωτότυπο — εγκρίνει το σχέδιο για παραγωγή");

                if (team.IsPrototyping)
                {
                    ImGui.Text($"{FactionPalette.UnitLabel(team.PrototypeKind)} — {team.PrototypeTicksRemaining} τικ");
                }

                bool any = false;

                foreach (UnitDefinition definition in UnitCatalog.BuildableBy(building.Faction))
                {
                    uint bit = 1u << (int)definition.Kind;

                    if (definition.IsBuilding || definition.ProducedAt != UnitKind.Factory ||
                        (team.ApprovedMask & bit) != 0 ||
                        !UnitCatalog.IsUnlocked(building.Faction, definition.Kind, team.TechTier, team.TechMask))
                    {
                        continue;
                    }

                    any = true;

                    int cost = (UnitCatalog.MaterialCost(building.Faction, definition.Kind) * SimWorld.PrototypeCostPermille) / 1_000;
                    string label = $"Πρωτότυπο: {FactionPalette.UnitLabel(definition.Kind),-16} {cost,4}Π";

                    ImGui.BeginDisabled(team.IsPrototyping || team.Materials < cost);

                    if (ImGui.Button(label))
                    {
                        command = new HudCommand(HudCommandKind.ApproveDesign, definition.Kind);
                    }

                    ImGui.EndDisabled();
                }

                if (!any && !team.IsPrototyping)
                {
                    ImGui.TextColored(MutedColor, "Κανένα νέο σχέδιο προς έγκριση.");
                }
            }
        }

        // Licence production: hand the Κινέζοι a design they could never research
        // themselves. This is the alliance's whole strategic point.
        if (building.TeamId == 0 && snapshot.AllyBuildingSlot >= 0)
        {
            ImGui.Separator();
            ImGui.TextColored(MutedColor, "Παραχώρηση άδειας στους Κινέζους");

            bool any = false;

            foreach (UnitDefinition definition in UnitCatalog.BuildableBy(building.Faction))
            {
                if (definition.IsBuilding || !world.CanBuild(0, definition.Kind) || world.CanBuild(1, definition.Kind))
                {
                    continue;
                }

                any = true;

                if (ImGui.Button($"Άδεια: {FactionPalette.UnitLabel(definition.Kind)}"))
                {
                    command = new HudCommand(HudCommandKind.Licence, definition.Kind);
                }
            }

            if (!any)
            {
                ImGui.TextColored(MutedColor, "Δεν υπάρχει διαθέσιμο σχέδιο.");
            }
        }

        // Measured inside the window, because that is the only place ImGui will answer: asked
        // after End it returns whatever window it happens to be thinking about, which put the
        // support panel on top of the status panel rather than above this one.
        float height = ImGui.GetWindowSize().Y;
        ImGui.End();

        // The panel drawn above this one — the support panel — starts where this ends. ImGui
        // windows positioned by hand do not stack: two of them told to sit on the bottom-left
        // corner sit on each other, which is what the support panel and the production panel
        // did, and the bridge's own refusal text was drawn over the build hint.
        _bottomLeftHeight = height;
        return command;
    }

    /// <summary>One buildable unit as the production panel presents it.</summary>
    /// <param name="Kind">What would be built.</param>
    /// <param name="Label">Button text with cost and build time.</param>
    /// <param name="Enabled">Whether the player may click it now.</param>
    /// <param name="Reason">Why it is disabled, in Greek; empty when enabled.</param>
    public readonly record struct BuildOption(UnitKind Kind, string Label, bool Enabled, string Reason);

    /// <summary>
    /// What a building can produce, and why each option is or is not available.
    /// <para>
    /// Kept separate from the drawing so the self-test can report the same list
    /// the player sees. The panel was previously unverifiable: a headless check
    /// could queue a unit directly, but nothing proved a button existed to click.
    /// </para>
    /// </summary>
    public static IReadOnlyList<BuildOption> DescribeBuildOptions(SimWorld world, int slot)
    {
        ArgumentNullException.ThrowIfNull(world);

        var options = new List<BuildOption>();

        if (!world.IsAliveSlot(slot))
        {
            return options;
        }

        ref Entity building = ref world.GetRefBySlot(slot);
        TeamState team = world.Team(building.TeamId);

        foreach (UnitDefinition definition in UnitCatalog.BuildableBy(building.Faction))
        {
            if (definition.ProducedAt != building.Kind)
            {
                continue;
            }

            bool unlocked = UnitCatalog.IsUnlocked(building.Faction, definition.Kind, team.TechTier, team.TechMask);
            uint bit = 1u << (int)definition.Kind;
            bool licensed = (team.LicenceMask & bit) != 0;

            // Σοβιετικοί factories may only build a design the bureau has proven.
            bool approved = licensed
                || building.Faction != Faction.Soviet
                || definition.ProducedAt != UnitKind.Factory
                || (team.ApprovedMask & bit) != 0;

            // A capped design is a capability rather than a unit type.
            bool capped = definition.MaxAlive > 0 && world.CountOf(building.TeamId, definition.Kind) >= definition.MaxAlive;

            int materials = UnitCatalog.MaterialCost(building.Faction, definition.Kind);
            int energy = UnitCatalog.EnergyCost(building.Faction, definition.Kind);
            int water = UnitCatalog.WaterCost(building.Faction, definition.Kind);
            int ticks = UnitCatalog.BuildTicks(building.Faction, definition.Kind);
            bool affordable = team.Materials >= materials && team.Energy >= energy && team.Water >= water;

            string label =
                $"{FactionPalette.UnitLabel(definition.Kind),-20} {materials,4}Π {energy,3}Ε {water,3}Ν {ticks / 20f,5:0.0}δ";

            string reason = !unlocked
                ? definition.RequiredTech != TechId.None && !TechCatalog.IsCompleted(team.TechMask, definition.RequiredTech)
                    ? "χρειάζεται έρευνα"
                    : $"χρειάζεται τεχνολογία {definition.RequiredTechTier}"
                : capped ? $"όριο {definition.MaxAlive}"
                : !approved ? "χρειάζεται πρωτότυπο στο σχεδιαστικό γραφείο"
                : affordable ? string.Empty
                : team.Materials < materials ? $"λείπουν {materials - team.Materials} Π"
                : team.Energy < energy ? $"λείπουν {energy - team.Energy} Ε"
                : $"λείπουν {water - team.Water} Ν";

            options.Add(new BuildOption(definition.Kind, label, unlocked && approved && !capped && affordable, reason));
        }

        return options;
    }

    /// <summary>
    /// Off-map support. Only shown when the player's faction has an ability it
    /// could plausibly use, so the panel does not sit empty for most of a match.
    /// A disabled button states why, in the same way the build panel does.
    /// </summary>
    private HudCommand? DrawSupportPanel(in HudSnapshot snapshot)
    {
        SimWorld world = snapshot.Simulation.World;

        // The player owns team 0 throughout; the HUD has no other notion of "us".
        const int Player = 0;

        Faction faction = SimWorld.FactionOfTeam(Player);
        TeamState team = world.Team(Player);

        var options = new List<(AbilityDefinition Definition, bool Enabled, string Reason)>();

        foreach (AbilityDefinition ability in AbilityCatalog.AvailableTo(faction, team.TechTier))
        {
            bool enabled = world.CanUseAbility(Player, ability.Id, out string reason);
            options.Add((ability, enabled, reason));
        }

        // Engineering works sit beside support: both are things a player does to the
        // map rather than to a unit, and both need a target.
        bool bridgeAvailable = world.HasStructure(Player, UnitKind.Factory);

        if (options.Count == 0 && !bridgeAvailable)
        {
            return null;
        }

        NVec2 display = ImGui.GetIO().DisplaySize;

        // Stacked on top of the production panel — which is the build hint when nothing is
        // selected — because a hand-positioned ImGui window does not get out of another one's
        // way. The anchor is the bottom of the screen minus whatever that panel measured
        // itself at, so the two move together as its contents change.
        ImGui.SetNextWindowPos(
            new NVec2(12f, display.Y - 12f - _bottomLeftHeight - 6f),
            ImGuiCond.Always,
            new NVec2(0f, 1f));

        HudCommand? command = null;

        if (!ImGui.Begin("Υποστήριξη##support", PanelFlags))
        {
            ImGui.End();
            return null;
        }

        foreach ((AbilityDefinition definition, bool enabled, string reason) in options)
        {
            ImGui.BeginDisabled(!enabled);

            string label = $"{definition.GreekName,-24} {definition.MaterialCost,4}Π {definition.CooldownTicks / 20f,5:0.0}δ";

            if (ImGui.Button(label))
            {
                command = new HudCommand(HudCommandKind.UseAbility, Ability: definition.Id);
            }

            ImGui.EndDisabled();

            if (reason.Length > 0)
            {
                ImGui.SameLine();
                ImGui.TextColored(MutedColor, reason);
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(definition.GreekDescription);
            }
        }

        if (bridgeAvailable)
        {
            if (options.Count > 0)
            {
                ImGui.Separator();
            }

            // The simulation's own answer, not a copy of it: the button is available exactly
            // when a crossing could be paid for, because the two questions are the same
            // question and a second version of it here is a second version to keep in step.
            bool enabled = world.CanBuildAnyBridge(Player, out string bridgeReason);
            BridgeCost cheapest = Bridgeworks.Cost(1);

            ImGui.BeginDisabled(!enabled);

            // The price on the button is the price of the smallest crossing there is, plus the
            // rate for the rest: what a site costs is a question about the site, and the player
            // is told that below as soon as they point at one.
            if (ImGui.Button(
                $"{"Γέφυρα",-24} {cheapest.Materials,4}Π {cheapest.Energy,3}Ε {cheapest.Water,3}Ν " +
                $"+{Bridgeworks.MaterialsPerCell}Π/κύτταρο"))
            {
                command = new HudCommand(HudCommandKind.BuildBridge);
            }

            ImGui.EndDisabled();

            // While a site is being chosen the panel says so, because the player is now in a
            // mode where a left click does something other than select, and the only way out
            // of a mode has to be visible from inside it. When the cell under the cursor is
            // one the simulation would refuse, the words replace the instruction: the ghost is
            // red, and red says that it fails while this says why.
            string note = !enabled ? bridgeReason
                : snapshot.BridgeArmed && snapshot.BridgeSiteReason.Length > 0 ? $"× {snapshot.BridgeSiteReason}"
                : snapshot.BridgeArmed ? "διαλέξτε σημείο στο νερό — Esc ακυρώνει"
                : string.Empty;

            if (note.Length > 0)
            {
                bool refusing = snapshot.BridgeArmed && snapshot.BridgeSiteReason.Length > 0;
                ImGui.SameLine();
                ImGui.TextColored(
                    refusing ? WarningColor : snapshot.BridgeArmed ? new NVec4(0.55f, 0.95f, 0.60f, 1f) : MutedColor,
                    note);
            }

            // What the site under the cursor would cost and how long it would take, on its own
            // line, because those are the two numbers that decide whether the crossing the player
            // is looking at is worth ordering: a ditch and a hundred metres of water are not the
            // same undertaking, in money or in time, and a single price on the button could only
            // ever be right for one of them.
            if (snapshot.BridgeArmed && snapshot.BridgeSiteReason.Length == 0 && snapshot.BridgeSiteCells > 0)
            {
                BridgeCost price = Bridgeworks.Cost(snapshot.BridgeSiteCells);
                int seconds = Bridgeworks.TicksFor(snapshot.BridgeSiteCells) / SimConstants.TickRate;

                ImGui.TextColored(
                    MutedColor,
                    $"Σημείο: {snapshot.BridgeSiteCells} κύτταρα — {price.Materials} Π, {price.Energy} Ε, " +
                    $"{price.Water} Ν — {seconds} δευτ.");
            }

            if (ImGui.IsItemHovered())
            {
                ImGui.SetTooltip(
                    "Διασχίζει το νερό στο σημείο που θα δείξετε με κλικ. " +
                    $"Η τιμή μεγαλώνει με το μήκος: {Bridgeworks.SetupMaterials} Π, {Bridgeworks.SetupEnergy} Ε, " +
                    $"{Bridgeworks.SetupWater} Ν, και {Bridgeworks.MaterialsPerCell} Π, {Bridgeworks.EnergyPerCell} Ε, " +
                    $"{Bridgeworks.WaterPerCell} Ν για κάθε κύτταρο.");
            }
        }

        DrawBridgeWork(world);

        ImGui.End();
        return command;
    }

    /// <summary>
    /// What has happened to the crossings: one still going up, or one that has been cut.
    /// <para>
    /// The deck growing across the water is the live sign of the work, and a hole in it is the live
    /// sign of the damage; these are the lines that say how much of either. A bridge that took time
    /// and showed nothing would be as bad as one that showed nothing and took no time, which is what
    /// it did before: the order turned water into ford in a single tick with no trace of the work at
    /// all — and a crossing that has been cut is worth saying out loud, because the army on the far
    /// side of it has no way home until somebody notices.
    /// </para>
    /// </summary>
    private static void DrawBridgeWork(SimWorld world)
    {
        Bridgeworks bridgeworks = world.Bridgeworks;

        for (int bridge = 0; bridge < bridgeworks.Count; bridge++)
        {
            BridgeState state = bridgeworks.State(bridge);

            if (state.Cut)
            {
                ImGui.TextColored(
                    WarningColor,
                    $"Γέφυρα κομμένη: {state.Standing}/{state.Built} κύτταρα στέκουν.");
                continue;
            }

            if (state.Complete)
            {
                continue;
            }

            int seconds = (int)(state.RemainingTicks(world.Tick) / SimConstants.TickRate);

            ImGui.TextColored(
                MutedColor,
                $"Γέφυρα σε κατασκευή: {state.Built}/{state.Total} κύτταρα — {seconds} δευτ.");
        }
    }

    /// <summary>
    /// What to do when no building is selected. Production lives behind a
    /// selection, which is not obvious from an empty corner of the screen, so the
    /// panel says so instead of vanishing.
    /// </summary>
    private void DrawBuildHint()
    {
        NVec2 display = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(new NVec2(12f, display.Y - 12f), ImGuiCond.Always, new NVec2(0f, 1f));

        if (!ImGui.Begin("Παραγωγή##buildhint", PanelFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextColored(MutedColor, "Επιλέξτε ένα κτίριο σας για να παραγάγετε μονάδες.");
        ImGui.TextColored(MutedColor, "Κέντρο διοίκησης: πεζικό    Εργοστάσιο: οχήματα και αεροσκάφη");
        ImGui.TextColored(MutedColor, "Σχεδιαστικό γραφείο: έρευνα τεχνολογίας");

        float height = ImGui.GetWindowSize().Y;
        ImGui.End();

        // This hint is the bottom-left panel as far as anything stacked above it is concerned.
        _bottomLeftHeight = height;
    }

    private static void DrawHelpPanel()
    {
        NVec2 display = ImGui.GetIO().DisplaySize;

        // Bottom-right, so it never collides with the production panel and never
        // runs off the bottom of a short window.
        ImGui.SetNextWindowPos(new NVec2(display.X - 12f, display.Y - 12f), ImGuiCond.Always, new NVec2(1f, 1f));

        if (!ImGui.Begin("Χειριστήρια##help", PanelFlags))
        {
            ImGui.End();
            return;
        }

        ImGui.TextUnformatted("WASD / βέλη — κάμερα    Τροχός — ζουμ    Q/E — περιστροφή");
        ImGui.TextUnformatted("Αριστερό κλικ / σύρσιμο — επιλογή    Δεξί κλικ — κίνηση ή επίθεση");
        ImGui.TextUnformatted("Διπλό κλικ — όλες οι μονάδες του ίδιου τύπου στην οθόνη");
        ImGui.TextUnformatted("Ctrl + 1..9 — αποθήκευση ομάδας    1..9 — ανάκληση");
        ImGui.Separator();
        ImGui.TextUnformatted("Παραγωγή: κλικ σε ένα κτίριο, μετά κουμπί στον πίνακα «Παραγωγή».");
        ImGui.TextUnformatted("F1 — απόκρυψη    M — σίγαση    F11 — πλήρης οθόνη    Esc — έξοδος");

        ImGui.End();
    }

    /// <summary>Draws the drag-selection rectangle on top of everything.</summary>
    public static void DrawSelectionBox(NVec2 start, NVec2 end)
    {
        ImGui.GetForegroundDrawList().AddRect(start, end, 0xFF60FF60u, 0f, ImDrawFlags.None, 2f);
    }

    private static int CountUnits(SimWorld world, Faction faction)
    {
        int count = 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).Faction == faction)
            {
                count++;
            }
        }

        return count;
    }

    private static NVec4 ToVector4(Color color)
        => new(color.R / 255f, color.G / 255f, color.B / 255f, color.A / 255f);
}
