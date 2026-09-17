using MiVic.Core.Campaign;
using MiVic.Game.Cutscene.Skinning;
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
    /// <summary>
    /// The clip a rigged figure stands in until a scene says otherwise. Authored assets name
    /// their clips, and every Mixamo-derived one calls the standing-still loop "Idle"; a scene
    /// will grow a per-figure clip field when there is more than one rigged figure to pose.
    /// </summary>
    private const string SkinnedIdleClip = "Idle";

    /// <summary>The node the pipe smoke rises from: an empty the asset carries, bone-parented
    /// to the head. See tools/blender/makehuman_pipe.py.</summary>
    private const string PipeBowlNode = "pipe_bowl";

    // The smoke is analytic rather than simulated: a wisp is a pure function of the
    // scene's clock, so a probe that seeks still photographs the same smoke. Eight wisps
    // on a 0.55 s cadence, three seconds each, so two or three are always on the rise.
    private const float SmokePeriodSeconds = 0.55f;
    private const float SmokeLifeSeconds = 3.0f;
    private const int SmokeWisps = 8;

    private readonly List<Actor> _actors = [];
    private readonly List<string> _shown = [];
    private readonly InstanceData[] _one = new InstanceData[1];
    private readonly InstanceData[] _smoke = new InstanceData[SmokeWisps];
    private InstancedRenderer.Mesh? _smokeMesh;

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

            _actors.Add(MakeActor(_assets.Load(figure.Asset), entity, posed: true, clip: figure.Clip));
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
                // A skinned figure keeps its meshes on the skeleton rather than in `Parts`,
                // so a count taken from `Parts` alone reports the cast as nought parts —
                // and the check that reads this to catch an empty frame stops counting the
                // cast at all, which is the failure it exists for.
                int parts = actor.Model.Skin is { } skin ? skin.Parts.Count : actor.Model.Parts.Length;
                models.Add((actor.Model.Asset, parts, actor.Posed));
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

    /// <summary>Where the pipe smoke rose from on the last frame, in world metres, or null
    /// when no figure in the scene carries a `pipe_bowl` node.</summary>
    public Vector3? LastSmokeAnchor { get; private set; }

    /// <summary>Smoke wisps drawn on the last frame.</summary>
    public int SmokeDrawn { get; private set; }

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

        // The room is lit by the petrol lamp on the desk and nothing else. The light
        // points at the lamp — front-right of the man, a little below his chin, which is
        // the underlight a flame gives a face at night — and the whole room breathes with
        // the flame: a deterministic flicker on the scene's own clock, so two probes of
        // the same moment photograph the same light. It used to come from the front,
        // above and to the left, in a bright room; see the note in the history of this
        // file for what a dark tunic did under that.
        float sceneSeconds = _total / 1000f;
        float flame = 0.90f
            + (0.06f * MathF.Sin(sceneSeconds * 8.7f))
            + (0.03f * MathF.Sin(sceneSeconds * 23.3f))
            + (0.02f * MathF.Sin(sceneSeconds * 39.7f));

        var environment = new InstancedRenderer.Environment(
            // Nearly level with his face, not below it: an underlit flame threw hard
            // bright edges off his brow and moustache. The lamp is on the desk at his
            // right; the flame's height, not the fount's, is what lights a face.
            LightDirection: Vector3.Normalize(new Vector3(-0.80f, 0.02f, -0.30f)),

            // Dark and warm: a lamp-lit room at night, with the window a cold blue
            // rectangle that does not reach the man. Not so dark that his face is a
            // mask: the ambient is the room the lamp warms, not the absence of one.
            AmbientColor: new Color(
                (int)(54 * flame),
                (int)(40 * flame),
                (int)(28 * flame)),
            FogColor: new Color(6, 4, 3),
            FogStart: 26f,
            FogEnd: 90f,
            Time: sceneSeconds);

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

        // Skinned figures are drawn after the instanced pass rather than between two of its
        // draws. SkinnedEffect is a different effect with its own rasteriser, depth and blend
        // state, so drawing it inside the pass would leave the instanced technique holding
        // state the skinned one had changed. The room, the props and the rigid figures are
        // all still in the instanced pass; only the rigged ones come out here.
        foreach (Actor actor in _actors)
        {
            if (actor.Skin is null)
            {
                continue;
            }

            actor.Skin.Environment = environment;
            actor.Skin.PoseAt(_total / 1000f);
            actor.Skin.Draw(
                _renderer.Device,
                actor.Model.ModelTransform * actor.Entity,
                view,
                projection);

            DrawCalls++;
        }

        DrawPipeSmoke(view);
    }

    /// <summary>
    /// The pipe's smoke, drawn last so it lies over the man when it drifts in front of
    /// him. Each wisp is a billboard born at the bowl on a fixed cadence: it rises,
    /// widens and thins, all as a function of the scene clock — there is no integration
    /// to drift and no state for a seek to lose.
    /// </summary>
    private void DrawPipeSmoke(in Matrix view)
    {
        Vector3? bowl = null;
        foreach (Actor actor in _actors)
        {
            if (actor.Skin is null || !actor.Skin.TryGetNodeWorld(PipeBowlNode, out Matrix nodeWorld))
            {
                continue;
            }

            bowl = (nodeWorld * actor.Model.ModelTransform * actor.Entity).Translation;
            break;
        }

        if (bowl is null)
        {
            LastSmokeAnchor = null;
            SmokeDrawn = 0;
            return;
        }

        LastSmokeAnchor = bowl;

        _smokeMesh ??= _renderer.CreateMesh(MeshBuilder.Quad(1f, 1f));

        float now = _total / 1000f;
        Vector3 right = new(view.M11, view.M21, view.M31);
        Vector3 up = new(view.M12, view.M22, view.M32);

        int count = 0;
        for (int k = 0; k < SmokeWisps; k++)
        {
            float age = now - ((now - (now % SmokePeriodSeconds)) - (k * SmokePeriodSeconds));
            float t = age / SmokeLifeSeconds;
            if (t < 0f || t >= 1f)
            {
                continue;
            }

            // A slow, thin rise with a sway that grows as the wisp does: pipe smoke
            // hangs rather than billows.
            Vector3 position = bowl.Value + new Vector3(
                MathF.Sin((age * 1.9f) + (k * 2.39f)) * 0.012f * t,
                (0.055f * age) + 0.005f,
                MathF.Cos((age * 1.6f) + (k * 1.73f)) * 0.009f * t);
            float size = MathHelper.Lerp(0.014f, 0.075f, t);
            float alpha = 0.42f * (1f - t) * Math.Min(1f, age / 0.4f);

            var transform = new Matrix(
                right.X * size, right.Y * size, right.Z * size, 0f,
                up.X * size, up.Y * size, up.Z * size, 0f,
                0f, 0f, 0f, 0f,
                position.X, position.Y, position.Z, 1f);
            _smoke[count++] = new InstanceData(transform, new Vector4(0.60f, 0.58f, 0.55f, alpha));
        }

        if (count > 0)
        {
            _renderer.BeginParticles(InstancedRenderer.ParticleBlend.Alpha);
            _renderer.Draw(_smokeMesh, _smoke, count);
            _renderer.EndParticles();
            DrawCalls++;
        }

        SmokeDrawn = count;
    }

    public void Dispose()
    {
        _smokeMesh?.Dispose();
        _assets.Dispose();
    }

    private Actor MakeActor(CutsceneModel model, Matrix entity, bool posed, string? clip = null)
    {
        SkinnedModelInstance? skin = null;
        if (model.Skin is not null)
        {
            skin = new SkinnedModelInstance(model.Skin);

            // A rigged figure with no clip would stand in its bind pose, which is a T-pose
            // and reads as a mannequin. Anything the asset has is better than that, and the
            // stitched clip is chosen by name from the figure's own script.
            skin.Play(clip ?? SkinnedIdleClip);
        }

        return new Actor
        {
        Model = model,
        Entity = entity,
        Locals = new Matrix[model.Parts.Length],
        World = new Matrix[model.Parts.Length],
            Posed = posed,
            Skin = skin,
        };
    }

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

        /// <summary>
        /// The pose and the bone palette, for a figure that is rigged. Created once per
        /// actor because the palette is per-instance state — two figures from one asset
        /// are two poses — and null for everything made of rigid parts.
        /// </summary>
        public SkinnedModelInstance? Skin { get; init; }
    }
}
