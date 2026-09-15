using ImGuiNET;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Game.Rendering;
using MiVic.Game.Sim;
using MiVic.Game.Ui;

namespace MiVic.Game;

/// <summary>
/// The editor screen: the author shapes the ground with a brush, paints surfaces, places
/// structures, and saves or test-plays what they shaped. ROADMAP §10's authoring half.
/// <para>
/// <b>The cursor is the client's own answer.</b> A click resolves through the same
/// screen-to-ground path a player's order takes, so the ground an edit touches is the ground
/// the picture draws — an editor whose cursor and whose rendering answered two different
/// questions would be exactly the second implementation of the rules the section warns
/// about. The structure ghost is the machinery the build panel arms, drawn in the faction
/// the placement will belong to.
/// </para>
/// <para>
/// <b>What the editor does not do is the mission.</b> The body it carries comes from the
/// file it opened or from the new-map template; objectives and triggers are edited as data
/// in the file itself. What the editor authors is the ground, the surfaces and the
/// placements — the part a cursor is for.
/// </para>
/// </summary>
public partial class MiVicGame
{
    /// <summary>The editor session: the map, the live world, the armed tool.</summary>
    private MapEditor? _editor;

    /// <summary>Where the current stroke has already been: a cell the button painted once is not painted again.</summary>
    private readonly HashSet<(int X, int Z)> _brushStroke = [];

    /// <summary>One frame of the editor: the panels, the ghost, and the tool's answer to the mouse.</summary>
    private void UpdateEditor(GameTime gameTime)
    {
        _editor ??= new MapEditor();

        // What the renderer draws is the map being authored, not the battle the
        // constructor built as its backdrop. The ground the author edits is the ground
        // the picture shows, which is the whole of what an editor is.
        if (!ReferenceEquals(_simulation, _editor.World))
        {
            _simulation = _editor.World;
            _terrainRevision = -1;
            _churnRevision = -1;
        }

        _imgui!.Update(gameTime);

        KeyboardState keyboard = Keyboard.GetState();
        MouseState mouse = Mouse.GetState();

        if (Pressed(keyboard, Keys.F11))
        {
            ToggleFullScreen();
        }

        // Undo on Z, when the author is not typing. The stroke is not undone
        // mid-drag: the edit list holds the stroke whole, so undo after a stroke takes the
        // stroke off in one piece. The gate is the text flag, not the keyboard flag —
        // a window that was clicked once holds keyboard focus and keeps reporting it,
        // and an undo gated on that flag dies the moment a panel has been touched.
        if (Pressed(keyboard, Keys.Z) && !_imgui.WantsTextInput && _brushStroke.Count == 0)
        {
            _editor.Undo();
        }

        // Escape leaves for the menu — but not while a caret is open: a question mark
        // typed into a briefing field is not a session the author meant to end.
        if (Pressed(keyboard, Keys.Escape) && !_imgui.WantsTextInput)
        {
            _screen = GameScreen.Menu;
            _editor = null;
            _brushStroke.Clear();
            base.Update(gameTime);
            return;
        }

        EditorCommand? raised = _editor.Draw() ?? _editor.DrawMission();

        if (raised is { } command)
        {
            switch (command.Kind)
            {
                case EditorCommandKind.Play:
                    StartMatch(new SimBridge(_editor.ToDefinition()));
                    _editor = null;
                    _brushStroke.Clear();
                    base.Update(gameTime);
                    return;

                case EditorCommandKind.ToMenu:
                    _screen = GameScreen.Menu;
                    _editor = null;
                    _brushStroke.Clear();
                    base.Update(gameTime);
                    return;
            }
        }

        // The author flies the same camera the player does: the wheel zooms, WASD and the
        // arrows walk the view across the map, Q/E or a middle-drag turns it — which is
        // the rotation the screen was missing, and the reason every zoom and pan was dead
        // before. The camera is fed before the cursor is resolved, because the cursor is
        // a ray through the camera and a stale camera would answer last frame's question.
        // The panels keep their own input: a wheel over a window scrolls the window, a
        // key while a caret is open types into the field, and neither moves the ground.
        if (!_imgui.WantsMouse && !_imgui.WantsTextInput)
        {
            int scroll = mouse.ScrollWheelValue - _previousScrollWheel;
            _camera!.Update(
                (float)gameTime.ElapsedGameTime.TotalSeconds,
                keyboard,
                _previousKeyboard,
                mouse,
                _previousMouse,
                scroll);
        }

        // The ghost of the armed structure, standing where the ground is: the same promise
        // a player's build panel makes, drawn in the colour of the side it will belong to.
        UpdateEditorPreview();

        HandleEditorTool(mouse);

        // The previous input is refreshed here, at the end of the editor's own frame —
        // it used to be refreshed only on the battle path, and the editor saw every
        // held frame as a fresh press: the delete tool stripped one structure per
        // frame, the undo popped one edit per keystroke, and the placements refused
        // with a lying notice on the second frame of a held click. The menu and the
        // pause panel now call the same helper.
        RememberInput(keyboard, mouse);

        base.Update(gameTime);
    }

    /// <summary>
    /// Applies the armed tool at the cursor. A terrain brush applies while the button is
    /// held, one application per position the stroke crosses — a stroke is one sweep of the
    /// brush, and the whole of it is one shape on the ground. A placement applies on the
    /// press. A deletion deletes.
    /// </summary>
    private void HandleEditorTool(MouseState mouse)
    {
        MapEditor editor = _editor!;
        bool uiWantsMouse = _imgui!.WantsMouse;

        bool pressed = mouse.LeftButton == ButtonState.Pressed && _previousMouse.LeftButton != ButtonState.Pressed;
        bool held = mouse.LeftButton == ButtonState.Pressed;

        if (uiWantsMouse || editor.Tool == EditorTool.None)
        {
            _brushStroke.Clear();
            return;
        }

        if (!TryPlacementTarget(CursorPosition, out WorldPos target))
        {
            _brushStroke.Clear();
            return;
        }

        switch (editor.Tool)
        {
            case EditorTool.Raise or EditorTool.Lower or EditorTool.Paint:
            {
                if (!held)
                {
                    _brushStroke.Clear();
                    return;
                }

                int sample = editor.SampleAt(target);

                if (sample < 0)
                {
                    return;
                }

                // One application per position the stroke crosses, keyed by the world
                // position the cursor resolved to: the cell under a slow cursor is touched
                // once, the cells under a fast cursor are each touched once, and the next
                // stroke over the same ground applies again.
                if (_brushStroke.Add((sample % editor.World.World.Terrain.Size, sample / editor.World.World.Terrain.Size)))
                {
                    editor.ApplyBrush(sample % editor.World.World.Terrain.Size, sample / editor.World.World.Terrain.Size);
                }

                break;
            }

            case EditorTool.Structure when pressed:
                editor.PlaceStructure(editor.PlacementKind, target);
                break;

            case EditorTool.Delete when pressed:
                if (!editor.DeleteNearest(target))
                {
                    editor.Notify("Τίποτα κοντά για διαγραφή.");
                }

                break;
        }
    }

    /// <summary>
    /// The armed structure's ghost, standing where the ground allows and tinted by whether
    /// it does: the same promise the build panel draws, asked here about the author's own
    /// placement, in the colour of the side it will belong to.
    /// </summary>
    private void UpdateEditorPreview()
    {
        MapEditor editor = _editor!;
        SimWorld world = editor.World.World;

        if (editor.Tool != EditorTool.Structure ||
            !TryPlacementTarget(CursorPosition, out WorldPos target))
        {
            _placementPreview?.Hide();
            return;
        }

        bool allowed = world.CanPlaceStructure(editor.PlacementKind, target, out _) &&
            world.IsSiteClear(editor.PlacementKind, target, out _);

        WorldPos stand = world.Navigation.CentreOf(world.Navigation.IndexOfWorld(target));

        float x = stand.X / (float)WorldPos.MmPerMetre;
        float z = stand.Z / (float)WorldPos.MmPerMetre;
        float y = DrawnHeightAtMetres(world, world.TerrainTypes, x, z) + PlacementPreview.LiftMetres;

        InstancedRenderer.Mesh ghost = _catalog!.Whole(world.FactionOfTeam(editor.PlacementTeam), editor.PlacementKind);

        _placementPreview!.Show(ghost, Matrix.CreateTranslation(x, y, z), allowed, StructureGhostOpacity);
    }
}
