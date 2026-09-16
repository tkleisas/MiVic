using ImGuiNET;
using MiVic.Core.Campaign;
using MiVic.Core.Sim;
using MiVic.Game.Audio;
using MiVic.Game.Cutscene;
using MiVic.Game.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;

namespace MiVic.Game;

/// <summary>
/// The cutscene screen: a scripted scene played from a file, in front of a mission or on its
/// own from the command line.
/// <para>
/// A screen of its own rather than an overlay on the battle, for the same reason the menu and
/// the editor are: a scene has no world behind it. The camera is the scene's, the light is the
/// scene's, and the frames the match would have drawn are not drawn at all — a briefing that
/// played over a frozen battlefield would be a briefing over a battlefield.
/// </para>
/// </summary>
public sealed partial class MiVicGame
{
    /// <summary>Nothing behind the letterbox: the room is the only thing lit.</summary>
    private static readonly Color CutsceneBackground = new(6, 6, 7);

    private CutsceneDirector? _cutscene;
    private IReadOnlyList<CutsceneDefinition>? _cutsceneScenes;
    private Action? _cutsceneNext;

    /// <summary>Where the scene files are copied to, beside the executable.</summary>
    public static string CutsceneDirectory => Path.Combine(AppContext.BaseDirectory, "Content", "Cutscenes");

    /// <summary>Every scene this build carries, loaded once and sorted by id.</summary>
    private IReadOnlyList<CutsceneDefinition> Cutscenes => _cutsceneScenes ??= CutsceneFile.LoadAll(CutsceneDirectory);

    /// <summary>The scene being played, or null when none is.</summary>
    public CutsceneDirector? Cutscene => _cutscene;

    /// <summary>
    /// Plays a scene by id. Returns false when there is no such scene, so a caller can fall
    /// through to what it would have done anyway.
    /// </summary>
    public bool PlayCutscene(string id, Action? next = null)
    {
        foreach (CutsceneDefinition scene in Cutscenes)
        {
            if (string.Equals(scene.Id, id, StringComparison.Ordinal))
            {
                BeginCutscene(scene, next);
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Plays the briefing a mission belongs to, if the build carries one. False means the
    /// mission should start straight away, which is the case for every mission that has no
    /// scene — and every mission before scenes existed.
    /// </summary>
    private bool PlayBriefing(string missionId)
    {
        CutsceneDefinition? scene = CutsceneFile.ForMission(Cutscenes, missionId, CutsceneKind.Briefing);

        if (scene is null)
        {
            return false;
        }

        BeginCutscene(scene, () => StartMatch(new SimBridge(MissionCatalog.Require(missionId))));
        return true;
    }

    private void BeginCutscene(CutsceneDefinition scene, Action? next)
    {
        _cutscene?.Dispose();
        _cutscene = new CutsceneDirector(scene, _renderer!, GraphicsDevice, AppContext.BaseDirectory);
        _cutsceneNext = next;
        _screen = GameScreen.Cutscene;
        _paused = false;

        // Scored from the scene's own side, because a briefing can be the first thing a launch
        // plays and there is no match behind it to take a side from.
        if (!_options.NoAudio)
        {
            _scoreDirector ??= new ScoreDirector();
            _scoreDirector.Play(StyleOf(scene.Faction));
        }
    }

    /// <summary>
    /// One frame of a scene: time runs, the score keeps to the scene's own track, and the input
    /// is the two things a viewer can do — go on, or leave.
    /// </summary>
    private void UpdateCutscene(GameTime gameTime)
    {
        CutsceneDirector director = _cutscene!;
        float seconds = (float)gameTime.ElapsedGameTime.TotalSeconds;

        director.Advance((int)(seconds * 1000f));

        KeyboardState keyboard = Keyboard.GetState();
        MouseState mouse = Mouse.GetState();

        bool advance =
            Pressed(keyboard, Keys.Space) ||
            Pressed(keyboard, Keys.Enter) ||
            (mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton != ButtonState.Pressed);

        if (advance)
        {
            // Space ends the line being read rather than the whole scene: a viewer who reads
            // faster than the typewriter should not be made to skip what comes next.
            director.SkipLine();
        }

        if (Pressed(keyboard, Keys.Escape) && !_options.IsSelfTest)
        {
            director.Finish();
        }

        _scoreDirector?.Update(seconds, new ScoreCue(GameOutcome.Ongoing, ScoreRung.Calm, director.Scene.Music));

        // A screen that returns before the battle path refreshes its input sees every held key
        // as a fresh press — the bug the menu, the pause panel and the editor each had.
        RememberInput(keyboard, mouse);

        if (director.IsFinished)
        {
            EndCutscene();
        }
    }

    /// <summary>
    /// Leaves the scene. A scene with somewhere to go hands over and is disposed; a scene
    /// played for its own sake — a probe, a screenshot — is left standing at its last frame
    /// rather than dropping the viewer into a menu behind it.
    /// </summary>
    private void EndCutscene()
    {
        Action? next = _cutsceneNext;
        _cutsceneNext = null;

        if (next is null)
        {
            return;
        }

        CutsceneDirector finished = _cutscene!;
        _cutscene = null;
        finished.Dispose();

        _scoreDirector?.Stop();
        next();
    }

    private void DrawCutscene(CutsceneDirector director)
    {
        // No culling: a room is looked at from inside it, so the surface the camera sees is the
        // back of the box the wall was built from. The battlefield culls because every unit is a
        // solid seen from outside; a set is a shell seen from within, and culling it leaves the
        // frame empty and the clear colour showing through.
        GraphicsDevice.RasterizerState = RasterizerState.CullNone;

        director.Draw(GraphicsDevice.Viewport.AspectRatio);

        // One part, one draw call, one instance: the numbers every other screen reports.
        _drawCalls = director.DrawCalls;
        _instancesSubmitted = director.DrawCalls;

        DrawCutsceneFrame(director);
    }

    /// <summary>
    /// The letterbox and the subtitle, drawn over the scene. ImGui rather than the world-label
    /// renderer: the one thing on screen that must always be legible is the writing.
    /// </summary>
    private void DrawCutsceneFrame(CutsceneDirector director)
    {
        Vector2 display = ImGui.GetIO().DisplaySize;
        float bar = display.Y * 0.09f;

        ImDrawListPtr foreground = ImGui.GetForegroundDrawList();
        uint black = ImGui.ColorConvertFloat4ToU32(new NVec4(0f, 0f, 0f, 1f));

        foreground.AddRectFilled(new NVec2(0f, 0f), new NVec2(display.X, bar), black);
        foreground.AddRectFilled(new NVec2(0f, display.Y - bar), new NVec2(display.X, display.Y), black);

        string text = director.VisibleText;

        if (text.Length > 0)
        {
            ImGui.SetNextWindowPos(
                new NVec2(display.X * 0.5f, display.Y - bar - 132f),
                ImGuiCond.Always,
                new NVec2(0.5f, 0f));

            ImGui.SetNextWindowSize(new NVec2(MathF.Max(360f, display.X - 240f), 0f));

            ImGui.PushStyleColor(ImGuiCol.WindowBg, new NVec4(0f, 0f, 0f, 0.34f));
            ImGui.PushStyleVar(ImGuiStyleVar.WindowPadding, new NVec2(20f, 14f));

            if (ImGui.Begin("##cutscene", SubtitleFlags))
            {
                // Nobody is captioned. The fiction says who a figure is with a greatcoat and a
                // pipe, and a name over the subtitle would be the one place it broke its rule.
                ImGui.PushFont(_imgui!.LargeFont);
                ImGui.PushTextWrapPos(0f);
                ImGui.TextWrapped(text);
                ImGui.PopTextWrapPos();
                ImGui.PopFont();
            }

            ImGui.End();
            ImGui.PopStyleVar();
            ImGui.PopStyleColor();
        }

        // The two things a viewer can do, said once and quietly.
        ImGui.SetNextWindowPos(new NVec2(display.X - 24f, display.Y - bar - 30f), ImGuiCond.Always, new NVec2(1f, 0f));
        ImGui.SetNextWindowSize(NVec2.Zero);

        if (ImGui.Begin("##cutscene-hint", SubtitleFlags | ImGuiWindowFlags.NoBackground | ImGuiWindowFlags.AlwaysAutoResize))
        {
            ImGui.TextDisabled("Διάστημα: συνέχεια   ·   Esc: παράλειψη");
        }

        ImGui.End();
    }

    private const ImGuiWindowFlags SubtitleFlags =
        ImGuiWindowFlags.NoTitleBar |
        ImGuiWindowFlags.NoResize |
        ImGuiWindowFlags.NoMove |
        ImGuiWindowFlags.NoCollapse |
        ImGuiWindowFlags.NoScrollbar |
        ImGuiWindowFlags.NoSavedSettings;
}
