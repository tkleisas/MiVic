using MiVic.Core.Campaign;
using MiVic.Game.Rendering;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game.Cutscene;

/// <summary>
/// Plays one scripted scene: a set, the figures standing in it, a camera that moves between
/// framings, and the lines that are said.
/// <para>
/// <b>Presentation, and nothing else.</b> The director has no world, issues no commands and
/// reads no simulation state; a scene can be played before a match exists at all. That is the
/// same one-way rule the particles and the sound effects follow, and it is why a cutscene can
/// never change the outcome of a tick.
/// </para>
/// <para>
/// <b>It is a diorama, not a performance.</b> The figures are rigid parts with no skeleton, so
/// the only motion available is a pose on a named joint: a head that turns and nods, arms that
/// breathe. That is the honest ceiling of the model pipeline, and the director works inside it
/// rather than pretending otherwise — a scene is carried by the room, the light, the camera
/// and the writing.
/// </para>
/// </summary>
public sealed class CutsceneDirector : IDisposable
{
    /// <summary>
    /// Typing speed, in characters a second. A line is typed over at most its own duration, so
    /// a short line is never still being typed when the next one arrives.
    /// </summary>
    public const int CharactersPerSecond = 42;

    /// <summary>
    /// Field of view, in degrees. Narrower than a battlefield camera's: a room seen at 38° has
    /// some depth to it, and a wide lens would bow the walls.
    /// </summary>
    public const float FieldOfViewDegrees = 38f;

    private readonly CutsceneDefinition _scene;
    private readonly InstancedRenderer _renderer;
    private readonly CutsceneAssets _assets;
    private readonly List<Actor> _actors = [];
    private readonly List<string> _shown = [];
    private readonly InstanceData[] _one = new InstanceData[1];

    private int _total;
    private int _lineElapsed;
    private int _lineIndex;
    private bool _finished;

    public CutsceneDirector(
        CutsceneDefinition scene,
        InstancedRenderer renderer,
        GraphicsDevice device,
        string baseDirectory)
    {
        _scene = scene ?? throw new ArgumentNullException(nameof(scene));
        _renderer = renderer ?? throw new ArgumentNullException(nameof(renderer));
        _assets = new CutsceneAssets(renderer, device, baseDirectory);

        // The set stands still and is not posed; a figure is placed by the scene file, facing
        // the way the file turns it, and is posed every frame.
        _actors.Add(MakeActor(_assets.Load(scene.Set), Matrix.Identity, posed: false));

        foreach (CutsceneFigure figure in scene.Figures)
        {
            Matrix entity = Matrix.CreateRotationY(MathHelper.ToRadians(figure.FacingDegrees))
                * Matrix.CreateTranslation(figure.X / 1000f, 0f, figure.Z / 1000f);

            _actors.Add(MakeActor(_assets.Load(figure.Asset), entity, posed: true));
        }
    }

    /// <summary>The scene being played.</summary>
    public CutsceneDefinition Scene => _scene;

    /// <summary>How long the scene has been running, in milliseconds.</summary>
    public int ElapsedMilliseconds => _total;

    /// <summary>Index of the line being typed, or the line count once the script has run out.</summary>
    public int LineIndex => _lineIndex;

    /// <summary>True once the last line has been held for its full time.</summary>
    public bool IsFinished => _finished;

    /// <summary>The lines that have finished, in order — what a transcript reads.</summary>
    public IReadOnlyList<string> ShownLines => _shown;

    /// <summary>The line being typed, or null when the script has run out.</summary>
    public CutsceneLine? CurrentLine => _lineIndex < _scene.Lines.Count ? _scene.Lines[_lineIndex] : null;

    /// <summary>How much of the current line has been typed.</summary>
    public int VisibleCharacters
    {
        get
        {
            if (CurrentLine is not { } line)
            {
                return 0;
            }

            int typing = TypingMilliseconds(line);

            if (typing <= 0 || _lineElapsed >= typing)
            {
                return line.GreekText.Length;
            }

            return (int)Math.Min(line.GreekText.Length, (long)_lineElapsed * line.GreekText.Length / typing);
        }
    }

    /// <summary>The part of the current line that has been typed so far.</summary>
    public string VisibleText
        => CurrentLine is { } line ? line.GreekText[..VisibleCharacters] : string.Empty;

    /// <summary>Who is speaking the current line, or empty when nobody is.</summary>
    public string Speaker => CurrentLine is { } line ? line.Speaker : string.Empty;

    /// <summary>
    /// What is standing in the scene, as a transcript can print it: the asset name and how many
    /// parts it is drawn from, set first and then the cast. A scene that renders an empty room
    /// should say so in numbers rather than in a screenshot.
    /// </summary>
    public IReadOnlyList<(string Asset, int Parts, bool Posed)> DrawnModels
    {
        get
        {
            var models = new List<(string Asset, int Parts, bool Posed)>(_actors.Count);

            foreach (Actor actor in _actors)
            {
                models.Add((actor.Model.Asset, actor.Model.Parts.Length, actor.Posed));
            }

            return models;
        }
    }

    /// <summary>Back to the first frame of the first line, with everything forgotten.</summary>
    public void Restart() => Seek(0);

    /// <summary>
    /// Puts the scene at a moment, as if it had played up to there: the camera track, the line
    /// being read and the lines already said are all derived from the script, so a scene can be
    /// photographed frame by frame without waiting for it.
    /// </summary>
    public void Seek(int milliseconds)
    {
        _total = Math.Max(0, milliseconds);
        _lineElapsed = 0;
        _lineIndex = 0;
        _finished = false;
        _shown.Clear();

        int remaining = _total;

        while (_lineIndex < _scene.Lines.Count)
        {
            int duration = _scene.Lines[_lineIndex].Milliseconds;

            if (remaining < duration)
            {
                _lineElapsed = remaining;
                return;
            }

            remaining -= duration;
            _shown.Add(_scene.Lines[_lineIndex].GreekText);
            _lineIndex++;
        }

        _finished = true;
        _lineElapsed = 0;
    }

    /// <summary>
    /// Runs the scene on by this many milliseconds. A long step can cross several lines, so the
    /// loop is a loop: a stalled frame must not swallow a line.
    /// </summary>
    public void Advance(int milliseconds)
    {
        if (milliseconds <= 0 || _finished)
        {
            return;
        }

        _total += milliseconds;
        _lineElapsed += milliseconds;

        while (!_finished && _lineIndex < _scene.Lines.Count && _lineElapsed >= _scene.Lines[_lineIndex].Milliseconds)
        {
            _lineElapsed -= _scene.Lines[_lineIndex].Milliseconds;
            _shown.Add(_scene.Lines[_lineIndex].GreekText);
            _lineIndex++;
        }

        if (_lineIndex >= _scene.Lines.Count)
        {
            _finished = true;
            _lineElapsed = 0;
        }
    }

    /// <summary>
    /// Ends the current line now. The scene clock is advanced by the rest of the line, so the
    /// camera track keeps pace with the script instead of standing still while the words jump.
    /// </summary>
    public void SkipLine()
    {
        if (_finished || CurrentLine is not { } line)
        {
            return;
        }

        _total += Math.Max(0, line.Milliseconds - _lineElapsed);
        _shown.Add(line.GreekText);
        _lineIndex++;
        _lineElapsed = 0;

        if (_lineIndex >= _scene.Lines.Count)
        {
            _finished = true;
        }
    }

    /// <summary>Ends the scene: every remaining line counts as said, and the camera reaches its last framing.</summary>
    public void Finish()
    {
        while (_lineIndex < _scene.Lines.Count)
        {
            _shown.Add(_scene.Lines[_lineIndex].GreekText);
            _lineIndex++;
        }

        _total = Math.Max(_total, _scene.ScriptMilliseconds);
        _lineElapsed = 0;
        _finished = true;
    }

    /// <summary>Where the camera stands and what it looks at, in metres, at a moment in the scene.</summary>
    public (Vector3 Position, Vector3 Target) CameraAt(int milliseconds)
    {
        IReadOnlyList<CutsceneCameraKey> keys = _scene.Camera;

        if (keys.Count == 0)
        {
            return (Vector3.Zero, Vector3.UnitZ);
        }

        if (keys.Count == 1 || milliseconds <= keys[0].Milliseconds)
        {
            return Frame(keys[0]);
        }

        for (int i = 0; i < keys.Count - 1; i++)
        {
            CutsceneCameraKey from = keys[i];
            CutsceneCameraKey to = keys[i + 1];

            if (milliseconds > to.Milliseconds)
            {
                continue;
            }

            int span = to.Milliseconds - from.Milliseconds;
            float t = span <= 0 ? 1f : Math.Clamp((milliseconds - from.Milliseconds) / (float)span, 0f, 1f);

            // Eased, so a move starts and stops rather than switching on. A camera that steps
            // from one framing to the next reads as a cut, and a cut is not what a slow room is.
            t = t * t * (3f - (2f * t));

            return (
                Vector3.Lerp(Frame(from).Position, Frame(to).Position, t),
                Vector3.Lerp(Frame(from).Target, Frame(to).Target, t));
        }

        return Frame(keys[^1]);
    }

    /// <summary>Draw calls the director issued on its last frame: one per part drawn.</summary>
    public int DrawCalls { get; private set; }

    /// <summary>Draws the scene with its own camera and its own light.</summary>
    public void Draw(float aspect)
    {
        (Vector3 position, Vector3 target) = CameraAt(_total);

        Matrix view = Matrix.CreateLookAt(position, target, Vector3.Up);
        Matrix projection = Matrix.CreatePerspectiveFieldOfView(
            MathHelper.ToRadians(FieldOfViewDegrees),
            aspect,
            0.05f,
            150f);

        var environment = new InstancedRenderer.Environment(
            // From the front, above and to the left of the camera. It used to come from
            // +z, which is *behind* the man: the camera looks down +z, so a light
            // pointing that way lit the back of his head and left his face and tunic in
            // their own shadow. That is why a tunic the right olive rendered almost
            // black, and why every paint correction to it did nothing.
            LightDirection: Vector3.Normalize(new Vector3(0.42f, 0.52f, -0.74f)),

            // Warmer and dimmer than the battlefield, but not so dim that a room lit by one lamp
            // has a wall of pure black in it: the lamp is the warm note, the window the cold one.
            AmbientColor: new Color(112, 102, 86),
            FogColor: new Color(9, 8, 7),
            FogStart: 26f,
            FogEnd: 90f,
            Time: _total / 1000f);

        _renderer.Begin(view, projection, position, environment);

        DrawCalls = 0;

        foreach (Actor actor in _actors)
        {
            Pose(actor);
            CutsceneAssets.Accumulate(actor.Model, actor.Entity, actor.Locals, actor.World);

            for (int i = 0; i < actor.Model.Parts.Length; i++)
            {
                _one[0] = new InstanceData(actor.World[i], Vector4.One);
                _renderer.Draw(actor.Model.Parts[i].Mesh, _one, 1);
                DrawCalls++;
            }
        }

        _renderer.End();
    }

    public void Dispose() => _assets.Dispose();

    private static Actor MakeActor(CutsceneModel model, Matrix entity, bool posed) => new()
    {
        Model = model,
        Entity = entity,
        Locals = new Matrix[model.Parts.Length],
        World = new Matrix[model.Parts.Length],
        Posed = posed,
    };

    /// <summary>Types a line over at most its own duration, so it is never still typing when the next arrives.</summary>
    private static int TypingMilliseconds(in CutsceneLine line)
        => (int)Math.Min(line.Milliseconds, (long)line.GreekText.Length * 1000 / CharactersPerSecond);

    private static (Vector3 Position, Vector3 Target) Frame(in CutsceneCameraKey key)
        => (
            new Vector3(key.X / 1000f, key.Y / 1000f, key.Z / 1000f),
            new Vector3(key.TargetX / 1000f, key.TargetY / 1000f, key.TargetZ / 1000f));

    /// <summary>
    /// Fills in every part's local transform, with a pose on the moving joints of a figure.
    /// <para>
    /// <b>Every actor, posed or not.</b> The local array starts as zero matrices, and a model
    /// whose locals are never filled collapses to the origin — which is an invisible set, not a
    /// stationary one. A set is therefore "posed" too, with an identity pose: the difference
    /// between a figure and a room is which parts move, not whether the array is written.
    /// </para>
    /// </summary>
    private void Pose(Actor actor)
    {
        float seconds = _total / 1000f;

        for (int i = 0; i < actor.Model.Parts.Length; i++)
        {
            CutscenePart part = actor.Model.Parts[i];
            Matrix pose = actor.Posed ? PoseFor(part.Name, seconds, _lineIndex) : Matrix.Identity;

            actor.Locals[i] = pose * part.LocalTransform;
        }
    }

    private static Matrix PoseFor(string name, float seconds, int line)
    {
        if (name.Equals("Head", StringComparison.Ordinal))
        {
            // A slow look across the room, with a small turn towards whoever he is talking to
            // as each line begins. Slow on purpose: a head that snaps reads as a fault.
            float yaw = (MathF.Sin(seconds * 0.37f) * 0.10f) + (line % 2 == 0 ? 0.025f : -0.025f);
            float pitch = MathF.Sin(seconds * 0.23f) * 0.045f;

            return Matrix.CreateRotationX(pitch) * Matrix.CreateRotationY(yaw);
        }

        if (name.Equals("ArmRight", StringComparison.Ordinal))
        {
            return Matrix.CreateRotationX(MathF.Sin(seconds * 0.51f) * 0.055f);
        }

        if (name.Equals("ArmLeft", StringComparison.Ordinal))
        {
            return Matrix.CreateRotationX(MathF.Sin((seconds * 0.51f) + 2.1f) * 0.035f);
        }

        return Matrix.Identity;
    }

    /// <summary>One thing standing in the scene: its model, where it stands, and its scratch transforms.</summary>
    private sealed class Actor
    {
        public required CutsceneModel Model { get; init; }

        public required Matrix Entity { get; init; }

        public required Matrix[] Locals { get; init; }

        public required Matrix[] World { get; init; }

        public required bool Posed { get; init; }
    }
}
