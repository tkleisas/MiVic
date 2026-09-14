using System.Numerics;
using ImGuiNET;
using MiVic.Audio;
using MiVic.Core.Campaign;
using MiVic.Core.Replay;
using MiVic.Core.Sim;
using MiVic.Game.Audio;
using MiVic.Game.Data;
using MiVic.Game.Sim;
using MiVic.Game.Ui;

namespace MiVic.Game;

/// <summary>
/// The front end: the screen state, the menu, the pause panel, and the two doors
/// between them. ROADMAP §1.
/// <para>
/// A plain launch opens on the menu over a frozen battlefield; every ask that names a
/// match — a probe, a fixture, a screenshot, a mission — skips it and plays. The menu's
/// answers go through <see cref="StartMatch"/>, which replaces the running battle
/// wholesale: a mission, a skirmish, or a saved match restored from its own command
/// log, which is what a save is here — a checkpoint a player makes on purpose, bytes
/// rather than a state dump, and the same mechanism the probe's save command uses.
/// </para>
/// <para>
/// Recording is on for the whole of a live match for the same reason: a save needs the
/// command log, and a log that started halfway down the match saves a position that
/// cannot be restored. The cost is memory proportional to the orders a player has
/// given, which is what the checkpoint work already spends.
/// </para>
/// </summary>
public partial class MiVicGame
{
    /// <summary>What is on screen: the menu, or a battle being played.</summary>
    private GameScreen _screen;

    /// <summary>The menu, when the front end is showing.</summary>
    private readonly MainMenu _menu = new();

    /// <summary>The campaign record, loaded once and written on a victory.</summary>
    private CampaignProgress? _progress;

    /// <summary>True once this match's victory has been recorded, so a decided match records once.</summary>
    private bool _victoryRecorded;

    /// <summary>True while the pause panel is open, which stops the clock rather than the world.</summary>
    private bool _paused;

    private GameScreen Screen => _options.Menu || _options.Editor ? _screen : GameScreen.Battle;

    /// <summary>
    /// Reads the campaign record, or starts a fresh one when the file cannot be read.
    /// A progress file that refuses to load must never stop the game: the record is not
    /// simulation state and nothing is desynced by starting over — so the unreadable file
    /// is moved aside, where the player can find it, and the campaign begins empty. The
    /// file is kept rather than overwritten precisely because it may hold missions the
    /// player has earned, and a fresh campaign that silently ate a finished one would be
    /// the worst thing this screen could do.
    /// </summary>
    private static CampaignProgress LoadProgressOrFresh()
    {
        string path = MiVicPaths.ProgressFile;

        try
        {
            return CampaignProgress.Load(path);
        }
        catch (Exception failure) when (failure is InvalidDataException or IOException)
        {
            try
            {
                string aside = $"{path}.unreadable-{DateTime.Now:yyyyMMdd-HHmmss}";
                File.Move(path, aside);
                Console.WriteLine($"campaign: progress file unreadable ({failure.Message}); kept as {aside}");
            }
            catch (IOException)
            {
                // Nothing to preserve and nothing to say if even the rename is refused.
            }

            return new CampaignProgress();
        }
    }

    /// <summary>
    /// Handles one menu command, on the frame the menu raised it. Null does nothing,
    /// because the menu answers only when a button is pressed.
    /// </summary>
    private void PumpMenu()
    {
        _progress ??= LoadProgressOrFresh();

        MenuCommand? raised = _menu.Draw(_progress, MiVicPaths.SavedMatches());

        if (raised is not MenuCommand command)
        {
            return;
        }

        switch (command.Kind)
        {
            case MenuCommandKind.StartMission:
                if (command.ResetProgress)
                {
                    // Starting over is an act, not a navigation: the record is cleared
                    // and written now, so a crash the second later leaves an empty one.
                    _progress.Reset();
                    _progress.Save(MiVicPaths.ProgressFile);
                }

                StartMatch(new SimBridge(MissionCatalog.Require(command.MissionId)));
                break;

            case MenuCommandKind.StartSkirmish:
                StartMatch(new SimBridge(_options.Seed, ScenarioKind.Skirmish));
                break;

            case MenuCommandKind.LoadSave:
                StartMatch(OpenSave(command.SavePath));
                break;

            case MenuCommandKind.Exit:
                Exit();
                break;
        }

        _menu.Clear();
    }

    /// <summary>
    /// Restores a saved match: rebuilds the world from its seed, its scenario and its
    /// command log to the tick the save was taken on, verifies the hash by the same
    /// throw the checkpoint machinery raises, and hands the bridge over as a live match.
    /// </summary>
    private static SimBridge OpenSave(string path)
    {
        ReplayFile save = ReplayFile.Load(path);
        RebuiltMatch match = ReplayFile.Rebuild(save, save.FinalTick, keepRecording: true);

        return new SimBridge(match, save.Scenario);
    }

    /// <summary>
    /// Puts a new match where the old one was. The renderer is sized against the world
    /// once, at the frame it first drew, and everything it sized — the batches, the
    /// meshes — fits every world the menu can hand over; what has to follow the world is
    /// the ground, which is forced to re-mesh by dropping the revision the refresh
    /// compares against, and the player's own view, which re-centres on their new
    /// headquarters.
    /// </summary>
    private void StartMatch(SimBridge bridge)
    {
        _simulation = bridge;
        _screen = GameScreen.Battle;
        _paused = false;
        _victoryRecorded = false;

        // Recording starts with the match, not with the process: the menu built the
        // battle behind it from the same constructor the initial one came through, and
        // a save of this match is a replay prefix only if the log runs from tick zero.
        if (!bridge.IsPlayback)
        {
            bridge.World.StartRecording();
        }

        // A fresh world's terrain layer starts at revision zero, the same number the
        // old one started at, so the refresh would see no change and draw the last
        // match's ground under this one. Forcing the comparison to miss re-meshes once.
        _terrainRevision = -1;
        _churnRevision = -1;

        _selection.Clear();
        _dragging = false;
        _pendingBridge = false;
        _pendingAbility = AbilityId.None;
        _pendingStructure = UnitKind.None;

        if (bridge.CommandCentres.Count > 0 &&
            bridge.World.TryGet(bridge.CommandCentres[0], out Entity headquarters))
        {
            Microsoft.Xna.Framework.Vector3 centre = SimBridge.ToMetres(headquarters.Position);
            _camera!.FocusOn(new Vector3(centre.X, 0f, centre.Z));
        }

        // The score belongs to the match, not to the process: a menu session with no
        // battle playing has no score, and starting one starts its own. The bytebeat that
        // sat behind the menu is stopped for it — the front end's texture and the match's
        // arrangement are different instruments, and the match has the say while it plays.
        if (!_options.NoAudio)
        {
            _audio?.Stop();
            (_scoreDirector ??= new ScoreDirector()).Play(StyleOf(bridge.World.FactionOfTeam(PlayerTeam)));
        }
    }

    /// <summary>
    /// Records a campaign victory the moment the rule reaches it, rather than when the
    /// match is left: a crash after the banner must not cost a player their mission.
    /// </summary>
    private void RecordCampaignVictory()
    {
        if (IsPlayback ||
            _victoryRecorded ||
            _simulation?.World.Mission is not MissionDefinition mission ||
            _simulation.World.Outcome != GameOutcome.Victory)
        {
            return;
        }

        _progress ??= LoadProgressOrFresh();
        _progress.MarkWon(mission.Id);
        _progress.Save(MiVicPaths.ProgressFile);
        _victoryRecorded = true;
    }

    /// <summary>
    /// Writes the running match as a save: a replay trimmed to the tick it was asked
    /// for, which is what a checkpoint is and what the front end's save system was
    /// always going to be — the roadmap's "a save is a checkpoint a player makes on
    /// purpose". The name is the moment it was taken, because a name a player has to
    /// invent mid-battle is a pause the battle did not need.
    /// </summary>
    private void SaveMatch()
    {
        SimBridge bridge = _simulation!;

        string name = $"{DateTime.Now:yyyy-MM-dd-HHmmss}" +
            (bridge.World.Mission is null ? "-μαχη" : $"-{bridge.World.Mission.Id}");

        string path = Path.Combine(MiVicPaths.SavesDirectory, $"{name}.mvsav");

        ReplayFile.Capture(bridge.World, bridge.Scenario).Save(path);

        _hud.Notify($"Αποθηκεύτηκε: {Path.GetFileName(path)}");
    }

    /// <summary>The pause panel: what Esc opens, and the doors out of a running match.</summary>
    private void DrawPausePanel()
    {
        Vector2 display = ImGui.GetIO().DisplaySize;

        ImGui.SetNextWindowPos(new Vector2(display.X * 0.5f, display.Y * 0.5f), ImGuiCond.Always, new Vector2(0.5f, 0.5f));
        ImGui.SetNextWindowSize(new Vector2(320f, 0f));

        ImGui.PushStyleColor(ImGuiCol.WindowBg, new Vector4(0.020f, 0.030f, 0.050f, 0.97f));
        ImGui.PushStyleColor(ImGuiCol.Border, new Vector4(0.30f, 0.38f, 0.48f, 0.85f));
        ImGui.PushStyleVar(ImGuiStyleVar.WindowBorderSize, 1f);
        ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new Vector2(24f, 20f));

        if (ImGui.Begin("##pause", ImGuiWindowFlags.NoTitleBar | ImGuiWindowFlags.NoResize |
            ImGuiWindowFlags.NoMove | ImGuiWindowFlags.NoCollapse | ImGuiWindowFlags.NoSavedSettings))
        {
            ImGui.TextColored(new Vector4(0.90f, 0.88f, 0.80f, 1f), "Παύση");
            ImGui.Separator();

            if (ImGui.Button("Συνέχεια", new Vector2(-1f, 0f)))
            {
                _paused = false;
            }

            if (ImGui.Button("Αποθήκευση", new Vector2(-1f, 0f)))
            {
                SaveMatch();
            }

            if (ImGui.Button("Στο μενού", new Vector2(-1f, 0f)))
            {
                _paused = false;
                _screen = GameScreen.Menu;
                _scoreDirector?.Stop();
                _musicCuesRead = 0;
                _pinned = null;
                _audio?.Play(FactionStyle.Soviet);
            }

            if (ImGui.Button("Έξοδος", new Vector2(-1f, 0f)))
            {
                Exit();
            }
        }

        ImGui.End();
        ImGui.PopStyleVar(2);
        ImGui.PopStyleColor(2);
    }

    private enum GameScreen : byte
    {
        /// <summary>The front end: the menu over a frozen battlefield.</summary>
        Menu = 0,

        /// <summary>A battle being played.</summary>
        Battle = 1,

        /// <summary>The map editor: an authored map being shaped.</summary>
        Editor = 2,
    }
}
