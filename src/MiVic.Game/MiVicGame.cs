using System.Diagnostics;
using System.Text;
using ImGuiNET;
using MiVic.Audio;
using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Replay;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;
using MiVic.Game.Audio;
using MiVic.Game.Camera;
using MiVic.Game.Data;
using MiVic.Game.Rendering;
using MiVic.Game.Rendering.Combat;
using MiVic.Game.Rendering.Particles;
using MiVic.Game.Sim;
using MiVic.Game.Ui;
using NVec2 = System.Numerics.Vector2;
using NVec4 = System.Numerics.Vector4;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;
using Microsoft.Xna.Framework.Input;
using XnaGame = Microsoft.Xna.Framework.Game;

namespace MiVic.Game;

/// <summary>
/// The MiVic client: a full-3D real-time strategy view of the deterministic
/// simulation in <c>MiVic.Core</c>.
/// <para>
/// The client owns no game state. It reads the simulation, interpolates between
/// ticks for smooth motion, and submits instanced draws. Rendering can never
/// change the outcome of a tick.
/// </para>
/// </summary>
public sealed class MiVicGame : XnaGame
{
    private const int WarmupFrames = 120;
    private const int MaxReportedFrames = 100_000;

    /// <summary>Team the human player commands: the Σοβιετικοί.</summary>
    private const int PlayerTeam = 0;

    private static readonly Color BackgroundColor = new(14, 16, 20);

    /// <summary>
    /// The window title, in Greek.
    /// <para>
    /// MonoGame creates the window through <c>SDL_CreateWindow</c>, whose title
    /// argument is a plain <c>string</c> and therefore marshalled as ANSI: a
    /// Greek title arrives at SDL as invalid bytes and the OS draws replacement
    /// diamonds. <c>Sdl.Window.SetTitle</c> does not have that problem because it
    /// encodes UTF-8 explicitly, so the title is re-applied once the window
    /// exists.
    /// </para>
    /// </summary>
    private const string GreekWindowTitle = "MiVic — Στρατηγική Πραγματικού Χρόνου";

    private readonly GraphicsDeviceManager _graphics;
    private readonly LaunchOptions _options;
    private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();
    private readonly List<double> _frameTimes = [];
    private readonly GameHud _hud = new();
    private readonly Dictionary<int, MeshBatch> _batches = [];
    private readonly InstanceData[] _singleInstance = new InstanceData[1];

    private InstancedRenderer? _renderer;
    private ModelCatalog? _catalog;
    private RtsCamera? _camera;
    private SimBridge? _simulation;
    private ImGuiController? _imgui;

    private InstancedRenderer.Mesh? _terrainMesh;
    private InstancedRenderer.Mesh? _selectionMarkerMesh;
    private InstancedRenderer.Mesh? _healthBackMesh;
    private InstancedRenderer.Mesh? _healthFillMesh;
    private SingleBatch? _healthBackBatch;
    private SingleBatch? _healthFillBatch;
    private InstancedRenderer.Mesh? _axisMesh;
    private InstancedRenderer.Mesh? _particleMesh;
    private InstancedRenderer.Mesh? _ringMesh;
    private InstancedRenderer.Mesh? _blastMesh;
    private InstancedRenderer.Mesh? _projectileMesh;
    private SingleBatch? _axisBatch;
    private SingleBatch? _markerBatch;
    private FogOverlayRenderer? _fog;
    private ParticleSystem? _particles;
    private ProjectileSystem? _projectiles;
    private AudioDirector? _audio;
    private SfxDirector? _sfx;
    private float[] _smokeTimers = [];
    private RenderTarget2D? _screenshotTarget;
    private SpriteFont? _uiFont;
    private WorldLabelRenderer? _worldLabels;
    private readonly List<WorldLabel> _labelBuffer = [];
    private readonly List<OrderMarker> _orderMarkers = [];
    private readonly List<PendingStrike> _pendingStrikes = [];
    private InstancedRenderer.Mesh? _orderMesh;
    private SingleBatch? _orderBatch;

    /// <summary>How long a move-order ring stays on screen, in seconds.</summary>
    private const float OrderMarkerSeconds = 0.9f;

    /// <summary>A fading ring where the player last ordered a move.</summary>
    private readonly record struct OrderMarker(Vector3 Position, float Age);
    private readonly SelectionController _selection = new();
    private readonly List<EntityId>[] _controlGroups = new List<EntityId>[10];
    private Vector2 _dragStart;
    private bool _dragging;

    /// <summary>Off-map ability waiting for the player to click a target.</summary>
    private AbilityId _pendingAbility = AbilityId.None;

    /// <summary>True while the next click places a bridge.</summary>
    private bool _pendingBridge;

    /// <summary>Health bars submitted on the last frame, for the self-test report.</summary>
    private int _healthBarsDrawn;

    /// <summary>Every role the fixture can show, in catalogue order.</summary>
    private static readonly (Faction Faction, UnitKind Kind)[] ViewerModels = BuildViewerList();

    private int _viewerIndex;
    private static readonly NVec4 ViewerMuted = new(0.62f, 0.66f, 0.70f, 1f);
    private static readonly NVec4 ViewerWarning = new(0.95f, 0.45f, 0.30f, 1f);

    private float _viewerYaw;

    /// <summary>Camera elevation in the fixture, in degrees, held every frame.</summary>
    private float _viewerPitch = 62f;
    private bool _viewerOrbit;

    private static (Faction, UnitKind)[] BuildViewerList()
    {
        var models = new List<(Faction, UnitKind)>();

        foreach (FactionProfile profile in FactionProfile.All)
        {
            foreach (UnitDefinition definition in UnitCatalog.All)
            {
                // Only what the faction can actually field: a Κινέζοι tank shown in
                // Σοβιετικοί colours would be a review of nothing.
                if (definition.OnlyFor == Faction.None || definition.OnlyFor == profile.Faction)
                {
                    models.Add((profile.Faction, definition.Kind));
                }
            }
        }

        return [.. models];
    }

    private Faction ViewerFaction => ViewerModels[_viewerIndex].Faction;

    private UnitKind ViewerKind => ViewerModels[_viewerIndex].Kind;

    /// <summary>
    /// Swaps the single model the fixture is showing. The world is not rebuilt: one
    /// entity leaves and another arrives, so switching is instant.
    /// </summary>
    private void ShowViewerModel(int index)
    {
        if (_simulation is null)
        {
            return;
        }

        int count = ViewerModels.Length;
        _viewerIndex = ((index % count) + count) % count;

        SimWorld world = _simulation.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                world.Despawn(new EntityId(slot, world.GetRefBySlot(slot).Generation));
            }
        }

        UnitDefinition definition = UnitCatalog.Get(ViewerKind);

        int centreCell = world.Navigation.IndexOf(world.Navigation.Size / 2, world.Navigation.Size / 2);

        world.Spawn(
            ViewerFaction,
            0,
            ViewerKind,
            world.Navigation.CentreOf(world.Navigation.NearestWalkable(centreCell)),
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);
    }

    /// <summary>
    /// Where the fixture's model actually is, in render metres. The camera is aimed
    /// at this rather than at the world origin, so the model is in frame wherever it
    /// ended up standing.
    /// </summary>
    private bool TryViewerTarget(out Vector3 position)
    {
        position = Vector3.Zero;

        if (_simulation is null)
        {
            return false;
        }

        SimWorld world = _simulation.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            position = _simulation.GetRenderPosition(slot, interpolate: false);
            return true;
        }

        return false;
    }

    /// <summary>Terrain revision the current ground mesh was built from.</summary>
    private int _terrainRevision;

    /// <summary>Per slot: whether the structure was still being raised last frame.</summary>
    private bool[] _wasBuilding = [];

    /// <summary>Seconds since the game started, for throttled presentation work.</summary>
    private double _totalSeconds;

    /// <summary>Ground-wear revision the current mesh was built from.</summary>
    private int _churnRevision;

    /// <summary>Seconds since the last churn-driven re-mesh.</summary>
    private double _lastChurnMesh;

    /// <summary>Minimum seconds between churn-driven re-meshes.</summary>
    private const double ChurnMeshSeconds = 1.5;
    private double _lastClickSeconds;
    private int _lastClickedSlot = -1;

    private KeyboardState _previousKeyboard;
    private MouseState _previousMouse;
    private int _previousScrollWheel;
    private int _frameCount;
    private int _drawCalls;
    private int _instancesSubmitted;
    private bool _greekGlyphsOk;
    private bool _spriteFontHasGreek;
    private FontCoverage _fontCoverage;
    private double _worstFrameMilliseconds;
    private int _peakParticles;
    private int _peakParticleInstances;
    private int _peakShotsInFlight;
    private int _startGen0;
    private int _startGen1;
    private int _startGen2;
    private long _startAllocatedBytes;
    private double _worstFogMaskMilliseconds;
    private double _fogMaskTotalMilliseconds;
    private int _fogMaskRebuilds;
    private int _worstFrameIndex;

    public MiVicGame(LaunchOptions options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));

        _graphics = new GraphicsDeviceManager(this)
        {
            PreferredBackBufferWidth = options.WindowWidth,
            PreferredBackBufferHeight = options.WindowHeight,
            PreferredDepthStencilFormat = DepthFormat.Depth24,
            PreferMultiSampling = true,
            // Vertical sync would cap the self-test at the display refresh rate.
            SynchronizeWithVerticalRetrace = !options.IsSelfTest,
            IsFullScreen = options.FullScreen,
        };

        IsFixedTimeStep = false;
        IsMouseVisible = true;
        Content.RootDirectory = "Content";
        Window.AllowUserResizing = true;

        // The title is deliberately NOT set here. See Initialize.
    }

    /// <summary>
    /// Toggles fullscreen. Worth a key rather than a launch flag alone: the
    /// difference between judging a model and guessing at it is how many pixels
    /// it gets, and that is a decision made while looking at it.
    /// </summary>
    private void ToggleFullScreen()
    {
        _graphics.HardwareModeSwitch = false;
        _graphics.ToggleFullScreen();
    }

    protected override void Initialize()
    {
        _graphics.ApplyChanges();
        base.Initialize();

        // Set the title only now that the native window exists.
        //
        // MonoGame creates the window through SDL_CreateWindow, whose title
        // parameter is marshalled as ANSI, so a Greek title arrives as invalid
        // bytes and the OS draws replacement diamonds. Sdl.Window.SetTitle does
        // not have that problem because it encodes UTF-8 explicitly, but
        // GameWindow.Title ignores an assignment that does not change the value —
        // so the title must be left unset until this point for the setter to fire.
        Window.Title = GreekWindowTitle;
    }

    protected override void LoadContent()
    {
        _camera = new RtsCamera
        {
            Target = Vector3.Zero,
            MapHalfExtent = SimBridge.MapHalfExtentMetres,

            // A match never lets the camera inside a unit — it is 25 m out or it is
            // unusable. The model fixture has to get closer than that: an
            // infantryman is under two metres tall and a headless review of one has
            // to be able to see his helmet.
            MinDistance = _options.Viewer ? 2f : 25f,
        };

        // A screenshot wants the whole battlefield in frame.
        if (_options.Viewer)
        {
            // The fixture locks the camera on one model. Nothing else in a match may
            // move it, so a given view is reproducible and two models can be compared
            // from exactly the same angle.
            _camera.ZoomTo(_options.ViewerDistance);
            _viewerPitch = _options.ViewerPitch;
            _camera.TiltTo(-MathHelper.ToRadians(_viewerPitch));
            _camera.Yaw = _options.ViewerAngle is { } angle ? MathHelper.ToRadians(angle) : 0.62f;
            _camera.FocusOn(Vector3.Zero);
            _viewerYaw = _camera.Yaw;
            // Open on whatever was asked for, so a headless shot and a person at the
            // keyboard see the same model.
            if (_options.ViewerModel is { Length: > 0 } wanted)
            {
                for (int i = 0; i < ViewerModels.Length; i++)
                {
                    string name = $"{ViewerModels[i].Faction}/{ViewerModels[i].Kind}";

                    if (string.Equals(name, wanted, StringComparison.OrdinalIgnoreCase) ||
                        string.Equals(ViewerModels[i].Kind.ToString(), wanted, StringComparison.OrdinalIgnoreCase))
                    {
                        _viewerIndex = i;
                        break;
                    }
                }
            }
        }
        else if (_options.IsModelGallery)
        {
            // Far enough out that all seventeen columns fit with the labels
            // legible; the pitch is re-applied every frame by ApplyGalleryCamera.
            _camera.ZoomTo(420f);
            _camera.TiltTo(-1.44f);
            _camera.Yaw = 0f;
            _camera.FocusOn(Vector3.Zero);
            PrintGalleryLegend();
        }
        else if (_options.WatchPath is not null)
        {
            // Playback is a spectator view: the whole map, so the recorded
            // manoeuvres are all visible.
            _camera.ZoomTo(620f);
            _camera.TiltTo(-1.02f);
            _camera.Yaw = 0.62f;
            _camera.FocusOn(Vector3.Zero);
        }
        else if (_options.MissionId is not null)
        {
            // A mission starts looking at the player's own base.
            MissionDefinition mission = MissionCatalog.Require(_options.MissionId);
            _camera.ZoomTo(420f);
            _camera.TiltTo(-0.92f);
            _camera.Yaw = 0.62f;
            _camera.FocusOn(new Vector3(
                mission.PlayerBase.X / (float)WorldPos.MmPerMetre,
                0f,
                mission.PlayerBase.Z / (float)WorldPos.MmPerMetre));
        }
        else if (_options.FireDemo)
        {
            // From the side and slightly above, looking along the firing line: the
            // trajectories run away from the camera, which is how a tracer's shape and
            // a rocket's trail are actually read.
            _camera.ZoomTo(300f);
            _camera.TiltTo(-0.34f);
            _camera.Yaw = 1.15f;
        }
        else if (_options.NukeDemo)
        {
            // Far enough out for a two-hundred-metre cloud, at an angle that shows the
            // column rather than looking straight down on it.
            _camera.ZoomTo(560f);
            _camera.TiltTo(-0.42f);
            _camera.Yaw = 0.35f;
            _camera.FocusOn(new Vector3(0f, 0f, 20f));
        }
        else if (_options.ParticleDemo)
        {
            // Straight down over the effect grid, close enough that a muzzle flash is
            // more than a pixel. A fixture that does not frame itself is a fixture
            // that costs a render per guess.
            _camera.ZoomTo(150f);
            _camera.TiltTo(-1.36f);
            _camera.Yaw = 0f;
        }
        else if (_options.CombatDemo)
        {
            // Close in and low: a firefight is read from the side, at the distance
            // where a tracer is a streak rather than a pixel.
            _camera.ZoomTo(135f);
            _camera.TiltTo(-0.48f);
            _camera.Yaw = 1.05f;
            _camera.FocusOn(Vector3.Zero);
        }
        else if (_options.ScreenshotPath is not null)
        {
            _camera.ZoomTo(_options.ScreenshotZoom ?? 430f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.92f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0.62f;
            _camera.FocusOn(new Vector3(_options.ScreenshotTargetX ?? 0f, 0f, _options.ScreenshotTargetZ ?? 0f));
        }

        _simulation = _options.WatchPath is { } watchPath
            ? new SimBridge(ReplayFile.Load(watchPath))
            : _options.MissionId is { } missionId
                ? new SimBridge(MissionCatalog.Require(missionId))
                : _options.Viewer
                    ? new SimBridge(_options.Seed, ViewerFaction, ViewerKind)
                    : _options.CombatDemo
                        ? SimBridge.CreateCombatDemo(_options.Seed)
                        : new SimBridge(_options.Seed, _options.IsModelGallery);

        _renderer = new InstancedRenderer(GraphicsDevice, Content);
        _catalog = new ModelCatalog(_renderer, AppContext.BaseDirectory);

        // The client meshes the simulation's own height field and surface layer, so
        // what is drawn is exactly what pathfinding reasons about.
        _terrainMesh = _renderer.CreateMesh(
            TerrainMeshBuilder.FromHeightMap(_simulation.World.Terrain, _simulation.World.TerrainTypes));
        _terrainRevision = _simulation.World.TerrainTypes.Revision;
        _selectionMarkerMesh = _renderer.CreateMesh(MeshBuilder.Cylinder(2.6f, 0.45f, 12));
        _markerBatch = new SingleBatch(_selectionMarkerMesh, _simulation.World.Capacity);

        // Order markers reuse the selection ring's geometry at a larger scale.
        _orderMesh = _selectionMarkerMesh;
        _orderBatch = new SingleBatch(_orderMesh, 32);

        // Fog of war is a terrain-shaped overlay textured by the team's
        // visibility mask, so its edge follows the ground instead of the cell
        // lattice pathfinding uses.
        _fog = new FogOverlayRenderer(
            GraphicsDevice,
            Content,
            _simulation.World.Terrain,
            _simulation.World.Navigation);

        // Axis markers for the model gallery: a unit-cube scaled into bars.
        _axisMesh = _renderer.CreateMesh(MeshBuilder.Box(1f, 1f, 1f));
        _axisBatch = new SingleBatch(_axisMesh, _simulation.World.Capacity * 4);

        // Particles are billboards: a unit quad the CPU orients per particle.
        _particleMesh = _renderer.CreateMesh(MeshBuilder.Quad(1f, 1f));

        // A shockwave needs to be a ring rather than a filled square, so it gets its
        // own geometry and its own instance list.
        _ringMesh = _renderer.CreateMesh(MeshBuilder.Ring(0.74f, 1f, 28));

        // Blast bodies are spheres, for the same reason: the first instant of an
        // explosion has a volume, and a billboard does not.
        _blastMesh = _renderer.CreateMesh(MeshBuilder.Sphere(12, 8));

        // Rounds in flight are solid geometry, so they need a cube rather than a
        // quad: centred on the origin, because a round is placed by where it is.
        _projectileMesh = _renderer.CreateMesh(MeshBuilder.Cube(1f));

        // Health bars reuse the particle quad: a bar is two camera-facing quads, one
        // behind the other, and there is no reason to build a mesh for that.
        _healthBackMesh = _renderer.CreateMesh(MeshBuilder.Quad(1f, 1f));
        _healthBackBatch = new SingleBatch(_healthBackMesh, _simulation.World.Capacity);
        _healthFillMesh = _renderer.CreateMesh(MeshBuilder.Quad(1f, 1f));
        _healthFillBatch = new SingleBatch(_healthFillMesh, _simulation.World.Capacity);
        _particles = new ParticleSystem();
        _projectiles = new ProjectileSystem(_particles);
        _smokeTimers = new float[_simulation.World.Capacity];
        _wasBuilding = new bool[_simulation.World.Capacity];

        if (_options.ParticleDemo)
        {
            SpawnParticleDemo();
        }

        if (_options.NukeDemo)
        {
            SpawnNukeDemo();
        }

        if (_options.FireDemo)
        {
            BuildFireDemo();
        }

        string fontPath = Path.Combine(AppContext.BaseDirectory, "Content", "Fonts", "NotoSans-Regular.ttf");
        _imgui = new ImGuiController(GraphicsDevice, Window, fontPath, _options.FontSize);
        _fontCoverage = _imgui.MeasureCoverage(SelfTestReport.GreekSample);
        _greekGlyphsOk = _fontCoverage.IsComplete;

        // The compiled SpriteFont, used for text that lives in the world.
        _uiFont = Content.Load<SpriteFont>("Fonts/UiText");
        _worldLabels = new WorldLabelRenderer(GraphicsDevice, _uiFont);
        _spriteFontHasGreek = _worldLabels.HasGreekGlyphs;

        _hud.ShowHelp = _options.ShowHelp;

        // The soundtrack is generated, not loaded, so it is skipped in the modes
        // that never open a window for long: a headless run does not need thirty
        // seconds of music synthesised before it can measure a frame.
        _audio = new AudioDirector(_options.Seed);
        _sfx = new SfxDirector(_options.Seed) { IsMuted = _options.NoAudio };

        if (!_options.NoAudio && !_options.IsSelfTest && _options.ScreenshotPath is null)
        {
            _audio.Play(FactionStyle.Soviet);
        }

        // Recording has to start before the first tick, otherwise the wander
        // orders of the opening seconds are missing from the log and the replay
        // cannot reproduce the match. A playback is not recorded: its own
        // commands come from the log it is replaying.
        if ((_options.IsSelfTest || _options.RecordPath is not null) && !_simulation.IsPlayback)
        {
            _simulation.World.StartRecording();
        }

        if (_options.VictoryDemo)
        {
            // Knock out every rival structure so the victory system decides the
            // battle within a second, purely so the banner can be seen.
            SimWorld world = _simulation.World;

            for (int slot = 0; slot < world.Capacity; slot++)
            {
                if (!world.IsAliveSlot(slot))
                {
                    continue;
                }

                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId != PlayerTeam && IsBuilding(entity.Kind))
                {
                    world.Despawn(new EntityId(slot, entity.Generation));
                }
            }
        }

        if (_options.SelectHeadquarters)
        {
            SelectPlayerHeadquarters();
        }

        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        _frameStopwatch.Stop();
        double frameMilliseconds = _frameStopwatch.Elapsed.TotalMilliseconds;
        _frameStopwatch.Restart();

        if (_frameCount == WarmupFrames)
        {
            // Allocation is sampled over the measured window: a sporadic multi-
            // millisecond pause that lands in a different system every run is
            // usually the collector, not the system.
            _startGen0 = GC.CollectionCount(0);
            _startGen1 = GC.CollectionCount(1);
            _startGen2 = GC.CollectionCount(2);
            _startAllocatedBytes = GC.GetTotalAllocatedBytes(precise: false);
        }

        _frameCount++;
        if (_frameCount > WarmupFrames && _frameTimes.Count < MaxReportedFrames)
        {
            _frameTimes.Add(frameMilliseconds);

            if (frameMilliseconds > _worstFrameMilliseconds)
            {
                _worstFrameMilliseconds = frameMilliseconds;
                _worstFrameIndex = _frameCount;
            }
        }

        KeyboardState keyboard = Keyboard.GetState();
        MouseState mouse = Mouse.GetState();

        if (Pressed(keyboard, Keys.F1))
        {
            _hud.ShowHelp = !_hud.ShowHelp;
        }

        if (Pressed(keyboard, Keys.F11))
        {
            ToggleFullScreen();
        }

        if (Pressed(keyboard, Keys.M))
        {
            _audio?.ToggleMute();

            if (_sfx is not null)
            {
                _sfx.IsMuted = _audio?.IsMuted ?? false;
            }
        }

        if (Pressed(keyboard, Keys.Escape) && !_options.IsSelfTest)
        {
            Exit();
        }

        long elapsedMicroseconds = gameTime.ElapsedGameTime.Ticks / 10L;
        _simulation!.Update(elapsedMicroseconds);
        UpdateParticles((float)gameTime.ElapsedGameTime.TotalSeconds);

        // Mouse actions are blocked only when ImGui actually wants the mouse.
        // Gating them on the keyboard flag too was a bug: with keyboard navigation
        // enabled, any focused HUD window reports WantCaptureKeyboard, and from
        // then on the player could neither select nor order anything.
        bool uiWantsMouse = _imgui!.WantsMouse;
        bool uiWantsKeyboard = _imgui.WantsKeyboard;

        // Once the battle is decided the player may look around but not fight on.
        if (!uiWantsMouse && _simulation!.World.Outcome == GameOutcome.Ongoing)
        {
            _totalSeconds = gameTime.TotalGameTime.TotalSeconds;

            if (_options.Viewer)
            {
                HandleViewerInput(keyboard, mouse, _previousMouse);
            }
            else
            {
                HandleSelectionInput(keyboard, mouse, _totalSeconds);
            }
        }

        // The camera runs even in the fixture, because its position is recomputed
        // from distance, pitch and target on update. Its own pitch easing and WASD
        // handling are then overwritten below: a reviewer must not be able to fly the
        // camera away from the model, and a fixed angle is the point of the fixture.
        if (!uiWantsMouse && !uiWantsKeyboard)
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

        if (_options.Viewer)
        {
            ApplyViewerCamera();
        }
        else if (_options.IsModelGallery)
        {
            ApplyGalleryCamera();
        }

        _selection.PruneDead(_simulation.World);
        UpdateOrderMarkers((float)gameTime.ElapsedGameTime.TotalSeconds);

        _previousScrollWheel = mouse.ScrollWheelValue;
        _previousKeyboard = keyboard;
        _previousMouse = mouse;

        _imgui.Update(gameTime);

        // Every ImGui call has to follow the frame's NewFrame, which Update issues.
        // Drawing the fixture panel before it trips ImGui's own assertion — and that
        // assertion opens a modal dialog, so the game appears to hang rather than to
        // fail.
        if (_options.Viewer)
        {
            DrawViewerPanel();
        }

        // The gallery and the model viewer are inspection fixtures, not a game: a
        // HUD over a contact sheet hides half the models, and the victory banner
        // that a team with no opposition triggers covers the rest.
        HudCommand? command = _options.Viewer || _options.IsModelGallery ? null : _hud.Draw(BuildSnapshot());

        if (command is HudCommand requested && !IsPlayback)
        {
            ApplyHudCommand(requested);
        }

        if (_dragging)
        {
            System.Numerics.Vector2 cursor = _imgui.MousePosition;
            GameHud.DrawSelectionBox(
                new System.Numerics.Vector2(_dragStart.X, _dragStart.Y),
                cursor);
        }

        // The self-test runs frames as fast as it can, so the number of simulated
        // ticks depends on how fast the renderer is — a lighter scene reaches the
        // frame target with far fewer ticks. The AI only acts every
        // DecisionInterval ticks, so the checks wait for at least two decision
        // points before running, with a frame cap so a paused world still exits.
        bool enoughTicks = _simulation.World.Tick >= AiSystem.DecisionInterval * 2;

        if (_options.IsSelfTest &&
            _frameCount >= _options.SelfTestFrames &&
            (enoughTicks || _frameCount >= _options.SelfTestFrames * 6))
        {
            (int pickHits, int pickTotal) = CheckPickingRoundTrip();
            string hudCommandCheck = CheckHudCommandPath();
            string clickCheck = CheckClickSelection();
            string moveOrderCheck = CheckMoveOrderPath();
            string combatCheck = CheckCombatPath();
            string aiCheck = CheckAiActivity();
            string replayCheck = CheckReplayRoundTrip();
            string missionCheck = CheckMissionObjectives();
            string particleCheck = _particles is null
                ? "n/a"
                : $"{_particles.TotalSpawned} spawned, peak {_peakParticles} live / {_peakParticleInstances} drawn";
            string sfxCheck = _sfx is null
                ? "n/a"
                : $"{_sfx.PlayedCount} played, {_sfx.DroppedCount} dropped, {_sfx.GeneratedCount} effects" +
                  (_sfx.IsUnavailable ? ", no audio device" : string.Empty);

            string gcCheck =
                $"gen0 {GC.CollectionCount(0) - _startGen0}, gen1 {GC.CollectionCount(1) - _startGen1}, " +
                $"gen2 {GC.CollectionCount(2) - _startGen2}, " +
                $"{(GC.GetTotalAllocatedBytes(precise: false) - _startAllocatedBytes) / (1024d * 1024d):0.0} MB allocated";

            // ImGui capture state: if this ever reports mouse capture while the
            // cursor is over the battlefield, input is being swallowed.
            string captureCheck =
                $"mouse {_imgui.WantsMouse}, keyboard {_imgui.WantsKeyboard}, " +
                $"window focused {ImGui.IsWindowFocused(ImGuiFocusedFlags.AnyWindow)}";

            SelfTestReport.Write(
                _options,
                _frameTimes,
                _simulation,
                _instancesSubmitted,
                _drawCalls,
                _imgui.GlyphCount,
                _imgui.FontCount,
                _greekGlyphsOk,
                Path.Combine("Content", "Fonts", "NotoSans-Regular.ttf"),
                _catalog!.LoadedModels,
                _catalog.FailedModels,
                _fontCoverage,
                _spriteFontHasGreek,
                Window.Title,
                WindowTitleProbe.ReadFromSdl(Window),
                pickHits,
                pickTotal,
                hudCommandCheck,
                clickCheck,
                moveOrderCheck,
                combatCheck,
                _healthBarsDrawn,
                aiCheck,
                replayCheck,
                missionCheck,
                particleCheck,
                sfxCheck,
                gcCheck,
                captureCheck,
                _worstFogMaskMilliseconds,
                _fogMaskRebuilds > 0 ? _fogMaskTotalMilliseconds / _fogMaskRebuilds : 0d,
                _worstFrameIndex,
                _simulation.World.Profiler);

            Exit();
        }

        base.Update(gameTime);
    }

    protected override void Draw(GameTime gameTime)
    {
        bool capturing = _options.ScreenshotPath is not null && _frameCount >= _options.ScreenshotFrame;

        if (capturing)
        {
            _screenshotTarget ??= new RenderTarget2D(
                GraphicsDevice,
                GraphicsDevice.PresentationParameters.BackBufferWidth,
                GraphicsDevice.PresentationParameters.BackBufferHeight,
                mipMap: false,
                SurfaceFormat.Color,
                DepthFormat.Depth24);

            GraphicsDevice.SetRenderTarget(_screenshotTarget);
        }

        GraphicsDevice.Clear(BackgroundColor);
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
        GraphicsDevice.BlendState = BlendState.Opaque;

        DrawScene();
        DrawWorldLabels();

        if (_options.FontSample && _worldLabels is not null)
        {
            _worldLabels.DrawSample("Σοβιετικοί Κινέζοι Δυτικοί", new Vector2(60f, 520f), 3f, Color.White);
        }

        _imgui!.Render();

        if (capturing)
        {
            GraphicsDevice.SetRenderTarget(null);
            SaveScreenshot(_options.ScreenshotPath!);
            Exit();
        }

        base.Draw(gameTime);
    }

    private void DrawScene()
    {
        float aspect = GraphicsDevice.Viewport.AspectRatio;
        Matrix view = _camera!.GetView();
        Matrix projection = _camera.GetProjection(aspect);

        var environment = new InstancedRenderer.Environment(
            LightDirection: new Vector3(-0.58f, -0.62f, -0.52f),
            // Bright enough that a unit's own material contrast survives the
            // hemisphere term: a tank read from an RTS camera is mostly side
            // faces, and those sit at the dark end of the ambient ramp.
            AmbientColor: new Color(96, 101, 110),
            FogColor: BackgroundColor,
            FogStart: 620f,
            FogEnd: 2000f);

        _renderer!.Begin(view, projection, _camera.Position, environment);

        _drawCalls = 0;
        _instancesSubmitted = 0;

        DrawSingle(_terrainMesh!, Matrix.Identity, Color.White);
        CollectUnitInstances();
        CollectSelectionMarkers();
        CollectOrderMarkers();
        CollectHealthBars();

        if (_options.IsModelGallery)
        {
            CollectGalleryAxes();
        }

        foreach (MeshBatch batch in _batches.Values)
        {
            foreach (PartBatch part in batch.Parts)
            {
                if (part.Count == 0)
                {
                    continue;
                }

                _renderer.Draw(part.Mesh, part.Instances, part.Count);
                _drawCalls++;
                _instancesSubmitted += part.Count;
            }
        }

        if (_markerBatch is { Count: > 0 })
        {
            _renderer.Draw(_markerBatch.Mesh, _markerBatch.Instances, _markerBatch.Count);
            _drawCalls++;
            _instancesSubmitted += _markerBatch.Count;
        }

        // Health bars go over the markers but under the fog: a bar for a unit the
        // player cannot see would give away that something is there.
        if (_healthBackBatch is { Count: > 0 })
        {
            _renderer.Draw(_healthBackBatch.Mesh, _healthBackBatch.Instances, _healthBackBatch.Count);
            _drawCalls++;
            _instancesSubmitted += _healthBackBatch.Count;
        }

        if (_healthFillBatch is { Count: > 0 })
        {
            _renderer.Draw(_healthFillBatch.Mesh, _healthFillBatch.Instances, _healthFillBatch.Count);
            _drawCalls++;
            _instancesSubmitted += _healthFillBatch.Count;
            _healthBarsDrawn = _healthFillBatch.Count;
        }

        if (_orderBatch is { Count: > 0 })
        {
            _renderer.Draw(_orderBatch.Mesh, _orderBatch.Instances, _orderBatch.Count);
            _drawCalls++;
        }

        // Fog goes on top so it darkens terrain and units alike, but only over
        // ground the player cannot see — their own units are never fogged.
        if (_axisBatch is { Count: > 0 } && _options.IsModelGallery)
        {
            _renderer.Draw(_axisBatch.Mesh, _axisBatch.Instances, _axisBatch.Count);
            _drawCalls++;
        }

        // Particles sit between the units and the fog overlay: an explosion
        // behind fog is darkened by it like everything else.
        if (_particles is { } particles && _particleMesh is not null && !_options.IsModelGallery)
        {
            Matrix viewMatrix = _camera.GetView();
            Vector3 right = new(viewMatrix.M11, viewMatrix.M21, viewMatrix.M31);
            Vector3 up = new(viewMatrix.M12, viewMatrix.M22, viewMatrix.M32);

            if (particles.AlphaCount > 0)
            {
                _renderer.BeginParticles(InstancedRenderer.ParticleBlend.Alpha);
                _renderer.Draw(_particleMesh, particles.AlphaInstances, particles.AlphaCount);
                _renderer.EndParticles();
                _drawCalls++;
                _instancesSubmitted += particles.AlphaCount;
            }

            if (particles.AdditiveCount > 0)
            {
                _renderer.BeginParticles(InstancedRenderer.ParticleBlend.Additive);
                _renderer.Draw(_particleMesh, particles.AdditiveInstances, particles.AdditiveCount);
                _renderer.EndParticles();
                _drawCalls++;
                _instancesSubmitted += particles.AdditiveCount;
            }

            if (particles.RingCount > 0 && _ringMesh is not null)
            {
                _renderer.BeginParticles(InstancedRenderer.ParticleBlend.Additive);
                _renderer.Draw(_ringMesh, particles.RingInstances, particles.RingCount);
                _renderer.EndParticles();
                _drawCalls++;
                _instancesSubmitted += particles.RingCount;
            }

            if (particles.BlastCount > 0 && _blastMesh is not null)
            {
                // The blast bodies are the one particle effect with a surface, so they
                // go through the lit-technique slot with their own pixel shader rather
                // than through the billboard passes.
                _renderer.BeginParticles(InstancedRenderer.ParticleBlend.Additive, forBlast: true);
                _renderer.Draw(_blastMesh, particles.BlastInstances, particles.BlastCount);
                _renderer.EndParticles();
                _drawCalls++;
                _instancesSubmitted += particles.BlastCount;
            }
        }

        DrawProjectiles();

        if (_simulation is not null)
        {
            RefreshTerrainMeshIfChanged();
        }

        if (_fog is not null && !_options.IsModelGallery)
        {
            // Visibility only changes on its own interval, so the mask is rebuilt
            // then rather than every frame — the texture upload is the expensive
            // part, not the draw.
            SimWorld world = _simulation!.World;

            if (world.Tick % VisionSystem.UpdateInterval == 0)
            {
                long start = Stopwatch.GetTimestamp();
                _fog.Update(world, PlayerTeam, (long)world.Tick);
                double elapsed = Stopwatch.GetElapsedTime(start).TotalMilliseconds;
                _worstFogMaskMilliseconds = Math.Max(_worstFogMaskMilliseconds, elapsed);
                _fogMaskTotalMilliseconds += elapsed;
                _fogMaskRebuilds++;
            }

            _fog.Draw(view, projection);
            _drawCalls++;
        }

        _renderer.End();
    }

    /// <summary>
    /// Draws a forward axis and a right axis from every unit in the gallery:
    /// cyan points +X, magenta points +Z. A model is oriented correctly when its
    /// nose or gun barrel follows the cyan bar.
    /// </summary>
    private void CollectGalleryAxes()
    {
        if (_axisBatch is null || _simulation is null)
        {
            return;
        }

        _axisBatch.Count = 0;

        const float Length = 13f;
        const float Thickness = 0.7f;
        Vector4 forward = new(0.2f, 1f, 1f, 1f);
        Vector4 right = new(1f, 0.35f, 1f, 1f);

        SimWorld world = _simulation.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            Vector3 position = _simulation.GetRenderPosition(slot, interpolate: false);
            float x = position.X;
            float y = position.Y + 0.6f;
            float z = position.Z;

            // +X bar and its tip.
            _axisBatch.Instances[_axisBatch.Count++] = new InstanceData(
                Matrix.CreateScale(Length, Thickness, Thickness) * Matrix.CreateTranslation(x + (Length * 0.5f), y, z),
                forward);

            _axisBatch.Instances[_axisBatch.Count++] = new InstanceData(
                Matrix.CreateScale(2f, 2f, 2f) * Matrix.CreateTranslation(x + Length, y, z),
                forward);

            // +Z bar and its tip.
            _axisBatch.Instances[_axisBatch.Count++] = new InstanceData(
                Matrix.CreateScale(Thickness, Thickness, Length) * Matrix.CreateTranslation(x, y, z + (Length * 0.5f)),
                right);

            _axisBatch.Instances[_axisBatch.Count++] = new InstanceData(
                Matrix.CreateScale(2f, 2f, 2f) * Matrix.CreateTranslation(x, y, z + Length),
                right);
        }
    }

    /// <summary>Draws a marker under every selected unit.</summary>
    private void CollectSelectionMarkers()
    {
        if (_markerBatch is null)
        {
            return;
        }

        _markerBatch.Count = 0;
        Vector4 color = new(0.30f, 1f, 0.45f, 1f);

        foreach (EntityId id in _selection.Selected)
        {
            if (!_simulation!.World.TryGetRef(id, out _, out int slot) || _markerBatch.Count >= _markerBatch.Instances.Length)
            {
                continue;
            }

            Vector3 position = _simulation.GetRenderPosition(slot, interpolate: true);
            Matrix transform = Matrix.CreateTranslation(position.X, position.Y + 0.3f, position.Z);

            _markerBatch.Instances[_markerBatch.Count] = new InstanceData(transform, color);
            _markerBatch.Count++;
        }
    }

    /// <summary>
    /// Builds a health bar over everything that needs one.
    /// <para>
    /// Shown for anything damaged and for anything selected, and hidden at full
    /// health otherwise: a bar over every unit is noise, and a bar over a unit the
    /// player just selected is exactly when the number matters. Two quads per bar —
    /// a dark backing and a coloured fill that shrinks from the right, because a
    /// length reads at a glance where a number would not.
    /// </para>
    /// </summary>
    private void CollectHealthBars()
    {
        if (_healthBackBatch is null || _healthFillBatch is null || _simulation is null || _camera is null)
        {
            return;
        }

        _healthBackBatch.Count = 0;
        _healthFillBatch.Count = 0;

        SimWorld world = _simulation.World;

        if (_options.IsModelGallery)
        {
            return;
        }

        // The bars face the camera, so they are built from the same view vectors the
        // particle system uses for its billboards.
        Matrix view = _camera.GetView();
        Vector3 right = new(view.M11, view.M21, view.M31);
        Vector3 up = new(view.M12, view.M22, view.M32);
        Vector3 forward = new(view.M13, view.M23, view.M33);

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot) || _healthBackBatch.Count >= _healthBackBatch.Instances.Length)
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            if (definition.Health <= 0)
            {
                continue;
            }

            bool selected = _selection.Contains(new EntityId(slot, entity.Generation));
            float fraction = Math.Clamp((float)entity.Health / definition.Health, 0f, 1f);

            // Nothing to say about a healthy unit nobody asked about.
            if (fraction >= 1f && !selected)
            {
                continue;
            }

            if (entity.TeamId != PlayerTeam &&
                (world.IsHiddenFrom(PlayerTeam, slot) ||
                 !world.Visibility.IsVisible(PlayerTeam, world.Navigation.IndexOfWorld(entity.Position))))
            {
                continue;
            }

            Vector3 position = _simulation.GetRenderPosition(slot, interpolate: true) +
                new Vector3(0f, HealthBarHeight(entity.Kind), 0f);

            const float Width = 2.8f;
            const float Height = 0.36f;

            _healthBackBatch.Instances[_healthBackBatch.Count++] = new InstanceData(
                Billboard(right, up, forward, position, Width, Height),
                new Vector4(0.05f, 0.06f, 0.07f, 0.80f));

            // Anchored to the left edge, so the bar empties from the right, which is
            // how every player already reads one.
            Vector3 offset = position - (right * ((Width * (1f - fraction)) * 0.5f));

            _healthFillBatch.Instances[_healthFillBatch.Count++] = new InstanceData(
                Billboard(right, up, forward, offset, Width * fraction, Height * 0.68f),
                HealthColor(fraction));
        }
    }

    /// <summary>A camera-facing quad of a given size at a position.</summary>
    private static Matrix Billboard(
        Vector3 right,
        Vector3 up,
        Vector3 forward,
        Vector3 position,
        float width,
        float height)
        => new(
            right.X * width, right.Y * width, right.Z * width, 0f,
            up.X * height, up.Y * height, up.Z * height, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            position.X, position.Y, position.Z, 1f);

    /// <summary>How high above a unit its bar floats, so it clears the model.</summary>
    private static float HealthBarHeight(UnitKind kind) => UnitCatalog.Get(kind).IsBuilding ? 14f : 5f;

    /// <summary>Green when healthy, amber when hurt, red when nearly gone.</summary>
    private static Vector4 HealthColor(float fraction) => fraction switch
    {
        > 0.6f => new Vector4(0.35f, 0.85f, 0.35f, 0.95f),
        > 0.3f => new Vector4(0.92f, 0.78f, 0.25f, 0.95f),
        _ => new Vector4(0.90f, 0.28f, 0.22f, 0.95f),
    };

    /// <summary>Draws a fading ring at each recent move destination.</summary>
    private void CollectOrderMarkers()
    {
        if (_orderBatch is null)
        {
            return;
        }

        _orderBatch.Count = 0;

        foreach (OrderMarker marker in _orderMarkers)
        {
            if (_orderBatch.Count >= _orderBatch.Instances.Length)
            {
                break;
            }

            float t = Math.Clamp(marker.Age / OrderMarkerSeconds, 0f, 1f);
            float scale = 0.6f + (t * 1.6f);
            float alpha = 1f - t;

            Matrix transform = Matrix.CreateScale(scale) * Matrix.CreateTranslation(marker.Position);

            _orderBatch.Instances[_orderBatch.Count] = new InstanceData(
                transform,
                new Vector4(0.35f, 1f, 0.55f, alpha * 0.55f));
            _orderBatch.Count++;
        }
    }

    private void DrawSingle(InstancedRenderer.Mesh mesh, Matrix transform, Color color)
    {
        _singleInstance[0] = new InstanceData(transform, color.ToVector4());
        _renderer!.Draw(mesh, _singleInstance, 1);
        _drawCalls++;
    }

    private void CollectUnitInstances()
    {
        foreach (MeshBatch batch in _batches.Values)
        {
            foreach (PartBatch part in batch.Parts)
            {
                part.Count = 0;
            }
        }

        SimWorld world = _simulation!.World;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            // Enemies the player cannot see are simply not drawn: that is the
            // whole point of fog of war. A stealthed enemy outside detection range
            // is hidden for the same reason, and stays hidden until it fires.
            if (entity.TeamId != PlayerTeam &&
                (world.IsHiddenFrom(PlayerTeam, slot) ||
                 !world.Visibility.IsVisible(PlayerTeam, world.Navigation.IndexOfWorld(entity.Position))))
            {
                continue;
            }

            MeshBatch batch = GetBatch(entity.Faction, entity.Kind);

            Vector3 position = _simulation.GetRenderPosition(slot, interpolate: true);
            float heading = SimBridge.HeadingRadians(entity.Heading);

            // A structure being raised: the progress is the simulation's, so a
            // replay builds at the same rate.
            float build = BuildFraction(ref entity);

            Matrix transform =
                Matrix.CreateRotationY(-heading) *
                Matrix.CreateTranslation(position);

            if (build < 1f)
            {
                // Rising out of the ground, not fading in: a partially built
                // structure should look like one.
                float rise = 0.25f + (build * 0.75f);
                transform *= Matrix.CreateScale(1f, rise, 1f);
            }

            Vector4 tint = FactionPalette.ForUnit(entity.Faction, entity.Kind).ToVector4();

            if (build < 1f)
            {
                tint = Vector4.Lerp(new Vector4(0.42f, 0.44f, 0.46f, 1f), tint, build);
            }

            // One instance per part, each carrying its own place in the model. The
            // chain is composed here rather than at load time, because that is what
            // lets a turret turn and take its barrel with it.
            for (int i = 0; i < batch.Parts.Length; i++)
            {
                batch.Locals[i] = AnimatePart(batch.Parts[i], ref entity, world);
            }

            for (int i = 0; i < batch.Parts.Length; i++)
            {
                PartBatch part = batch.Parts[i];

                if (part.Count >= part.Instances.Length)
                {
                    continue;
                }

                Matrix local = batch.Locals[i];
                int parent = part.ParentIndex;

                while (parent >= 0 && parent < batch.Locals.Length)
                {
                    local *= batch.Locals[parent];
                    parent = batch.Parts[parent].ParentIndex;
                }

                part.Instances[part.Count++] = new InstanceData(
                    local * batch.ModelTransform * transform,
                    tint);
            }
        }
    }

    /// <summary>
    /// How far along a structure's construction is: 1 for anything that is not being
    /// built, including every unit.
    /// </summary>
    private static float BuildFraction(ref Entity entity)
    {
        if (entity.ConstructionTicksRemaining <= 0 || entity.ConstructionTicksTotal <= 0)
        {
            return 1f;
        }

        return 1f - ((float)entity.ConstructionTicksRemaining / entity.ConstructionTicksTotal);
    }

    /// <summary>
    /// A structure's world position for one of its named parts, so effects can come
    /// out of a chimney rather than out of the middle of the building.
    /// </summary>
    private bool TryPartWorldPosition(MeshBatch batch, string partName, Matrix entityTransform, out Vector3 position)
    {
        for (int i = 0; i < batch.Parts.Length; i++)
        {
            if (!batch.Parts[i].Name.Equals(partName, StringComparison.Ordinal))
            {
                continue;
            }

            Matrix local = batch.Parts[i].LocalTransform;
            int parent = batch.Parts[i].ParentIndex;

            while (parent >= 0 && parent < batch.Parts.Length)
            {
                local *= batch.Parts[parent].LocalTransform;
                parent = batch.Parts[parent].ParentIndex;
            }

            position = Vector3.Transform(Vector3.Zero, local * batch.ModelTransform * entityTransform);
            return true;
        }

        position = Vector3.Zero;
        return false;
    }
    /// <summary>The part a structure's exhaust comes out of, if it has one.</summary>
    private static string? ChimneyPart(UnitKind kind) => kind switch
    {
        // The generator puts the stacks on one side, so both are plumbed.
        UnitKind.Factory => "barrel",
        UnitKind.PowerPlant => "stack_l",
        UnitKind.NuclearPlant => "stack_l",
        UnitKind.DesignBureau => "barrel",
        _ => null,
    };

    /// <summary>
    /// A part's transform for this entity, after animation.
    /// <para>
    /// Every animated part is a pure function of simulation state — the turret aims
    /// at what the unit is shooting at, the wheels turn with the distance it has
    /// actually travelled, the radar turns with the tick — so a replay shows the
    /// same animation without any of it being recorded.
    /// </para>
    /// </summary>
    private Matrix AnimatePart(PartBatch part, ref Entity entity, SimWorld world)
    {
        if (part.ParentIndex < 0 && part.LocalTransform == Matrix.Identity)
        {
            return part.LocalTransform;
        }

        string name = part.Name;

        if (name.StartsWith("wheel_", StringComparison.Ordinal))
        {
            // Rolling, not sliding: the angle is the arc the wheel has covered.
            float radius = _catalog!.WheelRadius(entity.Faction, entity.Kind);

            if (radius > 0.01f)
            {
                float angle = (entity.DistanceTravelledMm / 1000f) / radius;
                return Matrix.CreateRotationX(-angle) * part.LocalTransform;
            }
        }
        else if (name.Equals("turret", StringComparison.Ordinal))
        {
            return Matrix.CreateRotationY(TurretYaw(ref entity, world)) * part.LocalTransform;
        }
        else if (name.StartsWith("radar", StringComparison.Ordinal))
        {
            // A dish sweeps continuously; the tick is the clock, so it is identical
            // in a replay.
            //
            // Matched by prefix, not equality: the contract is `radar*` because a
            // model can have more than one thing that turns. The drone has four
            // rotors and they cannot all be called `radar` in one Blender scene, so
            // an equality test left every one of them still.
            float sweep = world.Tick * 0.02f;
            return Matrix.CreateRotationZ(sweep) * part.LocalTransform;
        }
        else if (IsLimb(name))
        {
            return SwingLimb(part, name, ref entity);
        }

        return part.LocalTransform;
    }

    /// <summary>Parts that belong to a walking rig, matched by the contract name.</summary>
    private static bool IsLimb(string name)
        => name.EndsWith("Legs", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Feet", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Body", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Head", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Shoulders", StringComparison.OrdinalIgnoreCase)
            // The generated figures ship one part per limb — LegLeft and LegRight,
            // ShinLeft, ArmRight — which is what makes a real alternating stride
            // possible. Feet, forearms, hands and anything else hanging off a limb
            // inherit its motion through the parent chain and must not be animated
            // twice.
            //
            // A side suffix is required, not just the prefix: `Arm` alone also
            // matches `Armour` (a Δυτικοί chest plate) and `Arms` (a loader
            // linkage), and both of those flapped about as if they were walking.
            || (HasSideSuffix(name)
                && (name.StartsWith("Leg", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Shin", StringComparison.OrdinalIgnoreCase)
                    || name.StartsWith("Arm", StringComparison.OrdinalIgnoreCase)));

    /// <summary>True for a part named for one side of a figure.</summary>
    private static bool HasSideSuffix(string name)
        => name.EndsWith("Left", StringComparison.OrdinalIgnoreCase)
            || name.EndsWith("Right", StringComparison.OrdinalIgnoreCase);

    /// <summary>
    /// A procedural walk cycle for a parts-rigged figure.
    /// <para>
    /// The phase comes from the distance the unit has actually covered, so the legs
    /// keep step with the ground rather than with the frame rate, and a unit that
    /// has stopped stands still. Limbs swing about the top of their own mesh, which
    /// is where the joint is on an imported rig with no skeleton to query.
    /// </para>
    /// <para>
    /// A single <c>Legs</c> mesh cannot alternate legs, so this reads as a march
    /// rather than a stride. It is sized for the normal camera distance, where a
    /// unit is a dozen pixels tall.
    /// </para>
    /// </summary>
    private static Matrix SwingLimb(PartBatch part, string name, ref Entity entity)
    {
        if (!entity.HasMoveGoal || entity.DistanceTravelledMm <= 0)
        {
            return part.LocalTransform;
        }

        // One full cycle every 1.4 m of ground covered — roughly a stride.
        const float StrideMm = 1_400f;
        float phase = ((entity.DistanceTravelledMm % (long)StrideMm) / StrideMm) * MathF.Tau;

        float pivot = part.BoundsMax.Y;

        // A two-part leg rig: one part per leg, swinging in antiphase about its own
        // top, which is where the hip is on a mesh with no skeleton to query.
        if (name.StartsWith("Leg", StringComparison.OrdinalIgnoreCase)
            && !name.EndsWith("Legs", StringComparison.OrdinalIgnoreCase))
        {
            float direction = name.EndsWith("Right", StringComparison.OrdinalIgnoreCase) ? 1f : -1f;
            float stride = MathF.Sin(phase * direction) * 0.34f;
            return Pivot(pivot, Matrix.CreateRotationZ(stride)) * part.LocalTransform;
        }

        // Arms swing against the leg on the same side. A figure whose arms hang
        // still while its legs walk looks like it is being pushed along.
        if (name.StartsWith("Arm", StringComparison.OrdinalIgnoreCase))
        {
            float direction = name.EndsWith("Right", StringComparison.OrdinalIgnoreCase) ? 1f : -1f;
            float swing = MathF.Sin(phase * -direction) * 0.22f;
            return Pivot(pivot, Matrix.CreateRotationZ(swing)) * part.LocalTransform;
        }

        // A shin bends at the knee, behind the thigh that carries it, and only
        // while that leg is swinging forward: a stiff-legged walk is the single
        // most obvious way for a low-poly figure to look wrong. Its mesh hangs
        // below its own origin, so the pivot is the knee with no offset at all.
        if (name.StartsWith("Shin", StringComparison.OrdinalIgnoreCase))
        {
            float direction = name.EndsWith("Right", StringComparison.OrdinalIgnoreCase) ? 1f : -1f;
            float bend = MathF.Max(0f, MathF.Sin(phase * direction)) * 0.55f;
            return Pivot(0f, Matrix.CreateRotationZ(-bend)) * part.LocalTransform;
        }

        if (name.EndsWith("Legs", StringComparison.OrdinalIgnoreCase))
        {
            float swing = MathF.Sin(phase) * 0.28f;
            return Pivot(pivot / 2f, Matrix.CreateRotationZ(swing)) * part.LocalTransform;
        }

        if (name.EndsWith("Feet", StringComparison.OrdinalIgnoreCase))
        {
            float swing = MathF.Sin(phase + 1.2f) * 0.18f;
            return Pivot(pivot / 2f, Matrix.CreateRotationZ(swing)) * part.LocalTransform;
        }

        // Body and head ride the same bounce, at half the rate: two footfalls per
        // cycle rather than one.
        float bob = MathF.Abs(MathF.Sin(phase)) * 0.05f;
        float roll = MathF.Sin(phase) * 0.03f;

        return Matrix.CreateTranslation(0f, bob, 0f)
            * Matrix.CreateRotationZ(roll)
            * part.LocalTransform;
    }

    /// <summary>A rotation about a point on the vertical axis, rather than the origin.</summary>
    private static Matrix Pivot(float y, Matrix rotation)
        => Matrix.CreateTranslation(0f, -y, 0f) * rotation * Matrix.CreateTranslation(0f, y, 0f);

    /// <summary>
    /// Turret angle relative to the hull, in radians, aiming at whatever the unit is
    /// engaging. A turret with nothing to shoot at returns to centre rather than
    /// freezing wherever it was pointing when the target died.
    /// </summary>
    private float TurretYaw(ref Entity entity, SimWorld world)
    {
        int target = entity.TargetSlot;

        if (target < 0 || !world.IsAliveSlot(target))
        {
            return 0f;
        }

        ref Entity victim = ref world.GetRefBySlot(target);

        int dx = victim.Position.X - entity.Position.X;
        int dz = victim.Position.Z - entity.Position.Z;

        if (dx == 0 && dz == 0)
        {
            return 0f;
        }

        // The simulation's heading is a brad-style angle; the renderer's yaw is the
        // same convention negated, which is what every other transform here uses.
        float bearing = MathF.Atan2(dz, dx);
        float hull = SimBridge.HeadingRadians(entity.Heading);

        return -(bearing - hull);
    }

    private MeshBatch GetBatch(Faction faction, UnitKind kind)
    {
        int key = ((int)faction * 8) + (int)kind;

        if (!_batches.TryGetValue(key, out MeshBatch? batch))
        {
            batch = new MeshBatch(_catalog!.GetParts(faction, kind), _simulation!.World.Capacity);
            _batches[key] = batch;
        }

        return batch;
    }

    /// <summary>Draws each faction's name above its command centre, in world space.</summary>
    private void DrawWorldLabels()
    {
        if (_worldLabels is null || _simulation is null || _camera is null)
        {
            return;
        }

        _labelBuffer.Clear();

        if (_options.IsModelGallery)
        {
            SimWorld gallery = _simulation.World;

            // Grid coordinates rather than names. Seventeen labels across a
            // contact sheet have to fit the column spacing, and "Σοβιετικοί
            // Ρομποτικό Πεζικό" is four times wider than the gap between two
            // models. The legend is printed to the console instead.
            int faction = 0;

            foreach (FactionProfile profile in FactionProfile.All)
            {
                for (int column = 0; column < Scenario.GalleryKinds.Length; column++)
                {
                    int x = (column - (Scenario.GalleryKinds.Length / 2)) * Scenario.GallerySpacingMm;
                    int z = (faction - 1) * Scenario.GallerySpacingMm * 2;

                    _labelBuffer.Add(new WorldLabel(
                        SimBridge.ToMetres(new WorldPos(x, 0, z)) + new Vector3(0f, 26f, 0f),
                        $"{faction + 1}{column + 1:00}",
                        Color.Lerp(FactionPalette.Primary(profile.Faction), Color.White, 0.35f)));
                }

                faction++;
            }
        }
        else
        {
            foreach (EntityId id in _simulation.CommandCentres)
            {
                if (!_simulation.World.TryGet(id, out Entity headquarters))
                {
                    continue;
                }

                Vector3 anchor = SimBridge.ToMetres(headquarters.Position) + new Vector3(0f, 24f, 0f);

                // Faction colours are dark enough to disappear against the terrain, so
                // labels are lightened for legibility while keeping the faction hue.
                Color tint = Color.Lerp(FactionPalette.Primary(headquarters.Faction), Color.White, 0.55f);

                _labelBuffer.Add(new WorldLabel(
                    anchor,
                    FactionProfile.For(headquarters.Faction).GreekName,
                    tint));
            }
        }

        Viewport viewport = GraphicsDevice.Viewport;
        _worldLabels.Draw(_labelBuffer, _camera.GetView(), _camera.GetProjection(viewport.AspectRatio), viewport);
    }

    /// <summary>Handles click selection, control groups and right-click orders.</summary>
    private void HandleSelectionInput(KeyboardState keyboard, MouseState mouse, double now)
    {
        bool leftDown = mouse.LeftButton == ButtonState.Pressed;
        bool leftWasDown = _previousMouse.LeftButton == ButtonState.Pressed;
        bool rightDown = mouse.RightButton == ButtonState.Pressed;
        bool rightWasDown = _previousMouse.RightButton == ButtonState.Pressed;

        bool additive = keyboard.IsKeyDown(Keys.LeftShift) || keyboard.IsKeyDown(Keys.RightShift);
        bool control = keyboard.IsKeyDown(Keys.LeftControl) || keyboard.IsKeyDown(Keys.RightControl);

        HandleControlGroups(keyboard, additive, control);

        if (leftDown && !leftWasDown)
        {
            _dragStart = new Vector2(mouse.X, mouse.Y);
            _dragging = true;
        }

        if (!leftDown && leftWasDown && _dragging)
        {
            _dragging = false;
            Vector2 end = new(mouse.X, mouse.Y);

            // A pending ability turns the next click into a target, not a
            // selection. Escape cancels it; see the keyboard handler.
            if (_pendingBridge)
            {
                if (Vector2.Distance(_dragStart, end) < 6f)
                {
                    IssueBridgeAtCursor(end);
                }
                else
                {
                    _pendingBridge = false;
                }
            }
            else if (_pendingAbility != AbilityId.None)
            {
                if (Vector2.Distance(_dragStart, end) < 6f)
                {
                    IssueAbilityAtCursor(end);
                }
                else
                {
                    _pendingAbility = AbilityId.None;
                }
            }
            else if (Vector2.Distance(_dragStart, end) < 6f)
            {
                SelectSingle(end, additive, now);
            }
            else
            {
                SelectInBox(_dragStart, end, additive);
            }
        }

        if (rightDown && !rightWasDown)
        {
            IssueOrderAtCursor(new Vector2(mouse.X, mouse.Y));
        }
    }

    /// <summary>
    /// Fixture input: step through the catalogue, orbit, zoom. The camera is not
    /// otherwise driveable here, so a given model and angle are reproducible.
    /// </summary>
    private void HandleViewerInput(KeyboardState keyboard, MouseState mouse, MouseState previousMouse)
    {
        if (Pressed(keyboard, Keys.Right) || Pressed(keyboard, Keys.D))
        {
            ShowViewerModel(_viewerIndex + 1);
        }

        if (Pressed(keyboard, Keys.Left) || Pressed(keyboard, Keys.A))
        {
            ShowViewerModel(_viewerIndex - 1);
        }

        // Faction jumps: 1, 2, 3 for the three powers in profile order.
        for (int i = 0; i < FactionProfile.All.Length; i++)
        {
            if (!Pressed(keyboard, Keys.D1 + i))
            {
                continue;
            }

            Faction wanted = FactionProfile.All[i].Faction;

            for (int m = 0; m < ViewerModels.Length; m++)
            {
                if (ViewerModels[m].Faction == wanted && ViewerModels[m].Kind == ViewerKind)
                {
                    ShowViewerModel(m);
                    break;
                }
            }
        }

        if (Pressed(keyboard, Keys.Space))
        {
            _viewerOrbit = !_viewerOrbit;
        }

        if (keyboard.IsKeyDown(Keys.Q))
        {
            _viewerYaw += 0.6f * (float)_totalSeconds;
        }

        if (keyboard.IsKeyDown(Keys.E))
        {
            _viewerYaw -= 0.6f * (float)_totalSeconds;
        }

        if (_viewerOrbit)
        {
            _viewerYaw += 0.02f;
        }
    }

    /// <summary>
    /// Pins the fixture's camera, after the RTS camera has recomputed its position.
    /// Doing it here rather than before means the camera's own pitch easing cannot
    /// quietly override the angle the reviewer asked for.
    /// </summary>
    private void ApplyViewerCamera()
    {
        if (_camera is null)
        {
            return;
        }

        _camera.Yaw = _viewerYaw;
        _camera.TiltTo(-MathHelper.ToRadians(_viewerPitch));

        if (TryViewerTarget(out Vector3 target))
        {
            // Aim at the model, not at the ground under it: FocusOn flattens the
            // target to the ground plane, and the terrain rises tens of metres, so a
            // flattened target leaves the model above the frame — or off it.
            _camera.LookAt(target + new Vector3(0f, 1.5f, 0f));
        }
    }

    /// <summary>
    /// Writes the contact sheet's legend to the console: which faction is which
    /// row, and which role is which column. The image can only carry two digits
    /// per model, so the rest has to live somewhere.
    /// </summary>
    private static void PrintGalleryLegend()
    {
        Console.WriteLine("gallery: rows 1=Σοβιετικοί 2=Κινέζοι 3=Δυτικοί, columns:");

        for (int i = 0; i < Scenario.GalleryKinds.Length; i++)
        {
            Console.WriteLine($"  {i + 1:00}  {FactionPalette.UnitLabel(Scenario.GalleryKinds[i])}");
        }
    }

    /// <summary>
    /// Pins the contact sheet's camera straight down over the grid.
    /// <para>
    /// The RTS camera eases its pitch towards the ideal angle for its distance,
    /// and that ideal is a shallow, cinematic tilt — which foreshortens the far
    /// rows of a grid into an unreadable band. Same fix as the viewer's, for the
    /// same reason: set the angle after the camera's own update, not before.
    /// </para>
    /// </summary>
    private void ApplyGalleryCamera()
    {
        if (_camera is null)
        {
            return;
        }

        _camera.Yaw = 0f;
        _camera.TiltTo(-1.44f);
        _camera.FocusOn(Vector3.Zero);
    }

    /// <summary>
    /// The fixture's readout: what is on screen, where it came from, and what may be
    /// animated on it. The part list is the animation contract, so a model with no
    /// drivable parts is a model nothing can move — which is worth seeing before
    /// noticing it in a battle.
    /// </summary>
    private void DrawViewerPanel()
    {
        if (_simulation is null || _catalog is null)
        {
            return;
        }

        ImGui.SetNextWindowPos(new NVec2(12f, 12f), ImGuiCond.Always);
        ImGui.SetNextWindowSize(new NVec2(440f, 0f), ImGuiCond.Always);

        if (!ImGui.Begin("Μοντέλο##viewer", ImGuiWindowFlags.NoResize | ImGuiWindowFlags.NoCollapse))
        {
            ImGui.End();
            return;
        }

        FactionProfile profile = FactionProfile.For(ViewerFaction);
        string label = FactionPalette.UnitLabel(ViewerKind);

        ImGui.TextColored(
            new NVec4(
                FactionPalette.Primary(ViewerFaction).R / 255f,
                FactionPalette.Primary(ViewerFaction).G / 255f,
                FactionPalette.Primary(ViewerFaction).B / 255f,
                1f),
            $"{profile.GreekName} — {label}");

        ImGui.TextColored(ViewerMuted, $"{_viewerIndex + 1} / {ViewerModels.Length}    {ViewerFaction}/{ViewerKind}");
        ImGui.Separator();

        ModelCatalog.ModelParts model = _catalog.GetParts(ViewerFaction, ViewerKind);

        ImGui.Text($"Μέρη: {model.Parts.Length}");

        if (model.WheelRadiusMetres > 0f)
        {
            ImGui.SameLine();
            ImGui.TextColored(ViewerMuted, $"  τροχός ⌀{model.WheelRadiusMetres * 2f:0.00} m");
        }

        var animated = new List<string>();

        foreach (ModelCatalog.PartMesh part in model.Parts)
        {
            string name = part.Name;

            // The same rules the animator uses, rather than a second copy of them: a
            // list that says a part moves when it does not is worse than no list.
            if (name.StartsWith("wheel_", StringComparison.Ordinal) ||
                name is "turret" ||
                name.StartsWith("radar", StringComparison.Ordinal) ||
                IsLimb(name))
            {
                animated.Add(name);
            }
        }

        ImGui.TextColored(
            animated.Count > 0 ? ViewerMuted : ViewerWarning,
            animated.Count > 0
                ? $"Κινούνται: {string.Join(", ", animated)}"
                : "Κανένα μέρος δεν κινείται σε αυτό το μοντέλο.");

        ImGui.Separator();
        ImGui.TextColored(ViewerMuted, "← →  προηγούμενο / επόμενο    1 2 3  παράταξη");
        ImGui.TextColored(ViewerMuted, "Space  περιστροφή    Q / E  χειροκίνητα    Τροχός  ζουμ");
        ImGui.TextColored(ViewerMuted, $"Απόσταση {(_camera?.Distance ?? 0f):0} m    γωνία {MathHelper.ToDegrees(_viewerYaw):0}°");

        ImGui.End();
    }

    /// <summary>Ctrl + digit stores the selection; a bare digit recalls it.</summary>
    private void HandleControlGroups(KeyboardState keyboard, bool additive, bool control)
    {
        for (int index = 1; index < _controlGroups.Length; index++)
        {
            Keys key = Keys.D1 + (index - 1);

            if (!Pressed(keyboard, key))
            {
                continue;
            }

            _controlGroups[index] ??= [];

            if (control)
            {
                _controlGroups[index]!.Clear();
                _controlGroups[index]!.AddRange(_selection.Selected);
                continue;
            }

            if (!additive)
            {
                _selection.Clear();
            }

            foreach (EntityId id in _controlGroups[index]!)
            {
                if (_simulation!.World.IsValid(id))
                {
                    _selection.Add(id);
                }
            }
        }
    }

    /// <summary>
    /// Converts a screen pixel to a point on the battlefield, marching the ray
    /// against the height field.
    /// <para>
    /// The camera's own <see cref="RtsCamera.ScreenToGround"/> intersects the
    /// y = 0 plane, which is the lowest point of the terrain — so a click that
    /// visibly lands on a hillside resolves tens of metres <em>past</em> it, and a
    /// click near the horizon misses entirely and silently drops the order. This
    /// march returns the first point where the ray meets the ground, and falls
    /// back to the plane when the ray never does (clicking beyond the map edge).
    /// </para>
    /// </summary>
    private bool TryScreenToGround(Vector2 screen, out Vector3 ground)
    {
        ground = default;

        if (_camera is null || _simulation is null)
        {
            return false;
        }

        Viewport viewport = GraphicsDevice.Viewport;

        if (!_camera.TryGetRay(screen, viewport.Width, viewport.Height, out Vector3 near, out Vector3 far))
        {
            return false;
        }

        HeightMap terrain = _simulation.World.Terrain;
        Vector3 direction = far - near;
        const float Step = 2f;
        float length = direction.Length();

        if (length <= 0.001f)
        {
            return false;
        }

        direction /= length;

        _groundRayDebug =
            $"near {near.X:0},{near.Y:0},{near.Z:0} far {far.X:0},{far.Y:0},{far.Z:0} " +
            $"dir {direction.X:0.00},{direction.Y:0.00},{direction.Z:0.00} len {length:0} " +
            $"gap {near.Y - HeightAtMetres(terrain, near.X, near.Z):0.0}";

        // A ray pointing at the sky never meets the ground: reject it rather than
        // inventing a destination behind the camera.
        if (direction.Y > -0.0001f)
        {
            return false;
        }

        float maxDistance = Math.Min(length, 4000f);
        Vector3 previous = near;
        float previousGap = near.Y - HeightAtMetres(terrain, near.X, near.Z);

        for (float travelled = Step; travelled <= maxDistance; travelled += Step)
        {
            Vector3 point = near + (direction * travelled);
            float gap = point.Y - HeightAtMetres(terrain, point.X, point.Z);

            if (gap <= 0f)
            {
                // Refine the crossing by bisection: the step is 2 m, which would
                // otherwise show up as a visible offset when ordering units.
                Vector3 low = previous;
                Vector3 high = point;

                for (int i = 0; i < 12; i++)
                {
                    Vector3 mid = (low + high) * 0.5f;

                    if (mid.Y - HeightAtMetres(terrain, mid.X, mid.Z) <= 0f)
                    {
                        high = mid;
                    }
                    else
                    {
                        low = mid;
                    }
                }

                ground = high;
                return true;
            }

            previous = point;
            previousGap = gap;
        }

        _ = previousGap;

        // Never hit the terrain: fall back to the flat plane so orders into the
        // distance still work.
        return _camera.ScreenToGround(screen, viewport.Width, viewport.Height) is Vector3 plane && Set(out ground, plane);
    }

    private static bool Set(out Vector3 target, Vector3 value)
    {
        target = value;
        return true;
    }

    private string _groundRayDebug = string.Empty;

    /// <summary>Terrain height in metres at a world position, clamped to the map.</summary>
    private static float HeightAtMetres(HeightMap terrain, float x, float z)
        => terrain.SampleHeightMm((int)(x * WorldPos.MmPerMetre), (int)(z * WorldPos.MmPerMetre)) / (float)WorldPos.MmPerMetre;

    /// <summary>
    /// Right-click: attack the enemy under the cursor, otherwise move there.
    /// </summary>
    private void IssueOrderAtCursor(Vector2 cursor)
    {
        // A playback must issue nothing of its own: any extra command would
        // desync the replayed match from the recording.
        if (IsPlayback)
        {
            return;
        }

        if (_selection.IsEmpty || _simulation is null)
        {
            return;
        }

        int targetSlot = PickSlotAt(cursor, teamFilter: -1);

        if (targetSlot >= 0)
        {
            ref Entity picked = ref _simulation.World.GetRefBySlot(targetSlot);

            // Only enemies are attacked. An ally under the cursor used to produce
            // an attack order that the simulation rejected, so the click appeared
            // to do nothing at all.
            if (picked.TeamId != PlayerTeam && !SimWorld.AreAllied(picked.TeamId, PlayerTeam))
            {
                IssueAttackOrders(targetSlot);
                return;
            }
        }

        IssueMoveOrder(cursor);
    }

    /// <summary>
    /// Spans the water under the cursor. The simulation decides whether the site is
    /// any good — not water, too wide, no factory, no resources — and refuses without
    /// charging, so a misplaced click costs nothing but the click.
    /// </summary>
    private void IssueBridgeAtCursor(Vector2 cursor)
    {
        _pendingBridge = false;

        if (IsPlayback || _simulation is null || !TryScreenToGround(cursor, out Vector3 ground))
        {
            return;
        }

        WorldPos target = WorldPos.FromMetres((int)ground.X, 0, (int)ground.Z);

        _simulation.World.Enqueue(SimCommand.Bridge(target, _simulation.World.Tick + 1, PlayerTeam));
    }

    /// <summary>
    /// Calls the pending ability in at the ground point under the cursor. The
    /// ability is data, so this does not need to know which one it is.
    /// </summary>
    private void IssueAbilityAtCursor(Vector2 cursor)
    {
        AbilityId ability = _pendingAbility;
        _pendingAbility = AbilityId.None;

        if (IsPlayback || _simulation is null || ability == AbilityId.None)
        {
            return;
        }

        if (!TryScreenToGround(cursor, out Vector3 ground))
        {
            return;
        }

        WorldPos target = WorldPos.FromMetres((int)ground.X, 0, (int)ground.Z);

        _simulation.World.Enqueue(SimCommand.UseAbility(ability, target, _simulation.World.Tick + 1, PlayerTeam));

        // Off-map support is drawn from the order rather than from the simulation,
        // and this is the one place in the client that works that way. The
        // simulation records that an ability was used and what it damaged, but not
        // where it was aimed, so a strike's position exists only in the command —
        // which means a strike replayed from a log lands its damage without its
        // flash. Making it exact would mean hashing a purely visual coordinate.
        if (ability is AbilityId.TacticalNuke or AbilityId.OrbitalStrike)
        {
            _pendingStrikes.Add(new PendingStrike(ability, ground, _simulation.World.Tick + 1));
        }
    }

    /// <summary>An off-map strike that has been ordered and not yet drawn.</summary>
    private readonly record struct PendingStrike(AbilityId Ability, Vector3 Ground, long Tick);

    /// <summary>
    /// The radius an off-map ability actually damages, in metres. Read from the
    /// catalogue rather than guessed, so an effect cannot quietly describe a smaller
    /// or larger circle than the one that kills.
    /// </summary>
    private static float AbilityRadiusMetres(AbilityId ability)
        => AbilityCatalog.TryGet(ability, out AbilityDefinition definition)
            ? definition.RadiusMm / (float)WorldPos.MmPerMetre
            : 40f;

    /// <summary>
    /// Draws the strikes whose tick has arrived.
    /// <para>
    /// The blast is drawn on the tick the simulation applies it, so the flash and the
    /// damage are the same event as far as the eye is concerned even though only one
    /// of them is in the simulation.
    /// </para>
    /// </summary>
    private void UpdatePendingStrikes()
    {
        if (_pendingStrikes.Count == 0 || _simulation is null || _particles is null)
        {
            return;
        }

        long tick = _simulation.World.Tick;

        for (int i = _pendingStrikes.Count - 1; i >= 0; i--)
        {
            PendingStrike strike = _pendingStrikes[i];

            if (strike.Tick > tick)
            {
                continue;
            }

            _pendingStrikes.RemoveAt(i);

            switch (strike.Ability)
            {
                case AbilityId.TacticalNuke:
                    // The catalogue's radius, so what is drawn and what is killed are
                    // the same circle.
                    _particles.SpawnNuke(strike.Ground, AbilityRadiusMetres(AbilityId.TacticalNuke));
                    _camera?.Shake(6f);
                    _sfx?.Play(SoundEffectKind.ExplosionLarge, strike.Ground, _camera?.Target ?? Vector3.Zero, 1f, -0.55f);
                    break;

                case AbilityId.OrbitalStrike:
                    // A barrage rather than one hit: a run of ground bursts walking
                    // across the target, which is what "from orbit" should look like.
                    for (int shot = 0; shot < 7; shot++)
                    {
                        float offset = (shot - 3) * 9f;
                        float spread = AbilityRadiusMetres(AbilityId.OrbitalStrike) * 0.16f;

                        _particles.SpawnGroundBurst(
                            strike.Ground + new Vector3(offset * spread, 0.5f + (shot * 0.15f), offset * spread * 0.35f),
                            2.4f);
                    }

                    _camera?.Shake(3.2f);
                    _sfx?.Play(SoundEffectKind.ExplosionLarge, strike.Ground, _camera?.Target ?? Vector3.Zero, 1f, -0.4f);
                    break;
            }
        }
    }

    /// <summary>
    /// Re-meshes the ground when the simulation's surface layer changes.
    /// <para>
    /// Weather control writes to the terrain, and a player who calls down mud has
    /// to be able to see where it landed. Rebuilding is driven by the layer's
    /// revision rather than by a timer, so the cost is paid only when something
    /// actually changed — which is rare.
    /// </para>
    /// </summary>
    private void RefreshTerrainMeshIfChanged()
    {
        SimWorld world = _simulation!.World;

        // Ground wear changes every tick an army moves, so it is throttled: a
        // weathered route is worth showing, but not at the cost of re-meshing
        // sixteen thousand vertices every frame.
        bool churnDue = world.TerrainTypes.ChurnRevision != _churnRevision &&
            _totalSeconds - _lastChurnMesh > ChurnMeshSeconds;

        if (world.TerrainTypes.Revision == _terrainRevision && !churnDue)
        {
            return;
        }

        _terrainRevision = world.TerrainTypes.Revision;
        _churnRevision = world.TerrainTypes.ChurnRevision;
        _lastChurnMesh = _totalSeconds;
        _terrainMesh?.Dispose();
        _terrainMesh = _renderer!.CreateMesh(
            TerrainMeshBuilder.FromHeightMap(world.Terrain, world.TerrainTypes));
    }

    /// <summary>
    /// Orders every selected unit to engage one enemy.
    /// </summary>
    private void IssueAttackOrders(int targetSlot)
    {
        SimWorld world = _simulation!.World;
        ref Entity target = ref world.GetRefBySlot(targetSlot);
        var victim = new EntityId(targetSlot, target.Generation);
        long executeTick = world.Tick + 1;

        foreach (EntityId id in _selection.Selected)
        {
            if (world.TryGet(id, out Entity attacker) && UnitCatalog.Get(attacker.Kind).IsArmed)
            {
                world.Enqueue(SimCommand.Attack(id, victim, executeTick, attacker.TeamId));
            }
        }
    }

    /// <summary>
    /// Selects the closest own unit to the cursor. A second click on the same
    /// unit within a third of a second selects every unit of that role on screen.
    /// </summary>
    private void SelectSingle(Vector2 cursor, bool additive, double now)
    {
        int slot = PickSlotAt(cursor);

        if (slot >= 0 && slot == _lastClickedSlot && now - _lastClickSeconds < 0.35d)
        {
            SelectAllOfKind(slot);
            _lastClickedSlot = -1;
            return;
        }

        _lastClickSeconds = now;
        _lastClickedSlot = slot;

        if (!additive)
        {
            _selection.Clear();
        }

        if (slot >= 0)
        {
            ref Entity selected = ref _simulation!.World.GetRefBySlot(slot);
            _selection.Add(new EntityId(slot, selected.Generation));
        }
    }

    /// <summary>Selects every own unit of the same role that is currently on screen.</summary>
    private void SelectAllOfKind(int referenceSlot)
    {
        SimWorld world = _simulation!.World;

        if (!world.IsAliveSlot(referenceSlot))
        {
            return;
        }

        UnitKind kind = world.GetRefBySlot(referenceSlot).Kind;
        _selection.Clear();

        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != PlayerTeam || entity.Kind != kind)
            {
                continue;
            }

            if (TryProjectToScreen(UnitAnchor(slot, ref entity), out Vector2 screen) &&
                screen.X >= 0f && screen.Y >= 0f &&
                screen.X < GraphicsDevice.Viewport.Width && screen.Y < GraphicsDevice.Viewport.Height)
            {
                _selection.Add(new EntityId(slot, entity.Generation));
            }
        }
    }

    /// <summary>
    /// Returns the own unit whose screen position is nearest to the cursor, or -1.
    /// <para>
    /// This is the inverse of <see cref="TryProjectToScreen"/>, and the self-test
    /// round-trips the two against each other: project a unit, pick at that exact
    /// pixel, and the same unit must come back.
    /// </para>
    /// </summary>
    private int PickSlotAt(Vector2 cursor, int teamFilter = PlayerTeam)
    {
        SimWorld world = _simulation!.World;
        int capacity = world.Capacity;

        int bestSlot = -1;
        float bestDistance = 30f;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (teamFilter >= 0 && entity.TeamId != teamFilter)
            {
                continue;
            }

            if (!TryProjectToScreen(UnitAnchor(slot, ref entity), out Vector2 screen))
            {
                continue;
            }

            float distance = Vector2.Distance(screen, cursor);

            if (distance < bestDistance)
            {
                bestDistance = distance;
                bestSlot = slot;
            }
        }

        return bestSlot;
    }

    /// <summary>Selects every own unit whose screen position falls inside the drag rectangle.</summary>
    private void SelectInBox(Vector2 start, Vector2 end, bool additive)
    {
        Rectangle box = new(
            (int)MathF.Min(start.X, end.X),
            (int)MathF.Min(start.Y, end.Y),
            (int)MathF.Abs(end.X - start.X),
            (int)MathF.Abs(end.Y - start.Y));

        SimWorld world = _simulation!.World;
        int capacity = world.Capacity;

        if (!additive)
        {
            _selection.Clear();
        }

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != PlayerTeam)
            {
                continue;
            }

            if (!TryProjectToScreen(UnitAnchor(slot, ref entity), out Vector2 screen))
            {
                continue;
            }

            if (box.Contains((int)screen.X, (int)screen.Y))
            {
                _selection.Add(new EntityId(slot, entity.Generation));
            }
        }
    }

    /// <summary>Orders every selected unit to the ground point under the cursor.</summary>
    private void IssueMoveOrder(Vector2 cursor)
    {
        if (_selection.IsEmpty || _camera is null)
        {
            return;
        }

        Viewport viewport = GraphicsDevice.Viewport;

        if (!TryScreenToGround(cursor, out Vector3 ground))
        {
            return;
        }

        WorldPos target = new(
            (int)(ground.X * WorldPos.MmPerMetre),
            0,
            (int)(ground.Z * WorldPos.MmPerMetre));

        // Feedback: without a marker the player cannot tell an accepted order
        // from a click that landed on a cliff and was refused.
        _orderMarkers.Add(new OrderMarker(new Vector3(ground.X, ground.Y + 0.4f, ground.Z), 0f));

        SimWorld world = _simulation!.World;

        // Spread the group over a loose grid so units do not pile onto one point.
        int count = _selection.Count;
        int columns = Math.Max(1, (int)Math.Ceiling(Math.Sqrt(count)));
        int rows = Math.Max(1, (int)Math.Ceiling(count / (double)columns));
        const int SpacingMm = 14_000;

        int index = 0;

        foreach (EntityId id in _selection.Selected)
        {
            if (!world.TryGet(id, out Entity entity))
            {
                continue;
            }

            int column = index % columns;
            int row = index / columns;

            WorldPos destination = new(
                target.X + ((column - ((columns - 1) / 2)) * SpacingMm),
                0,
                target.Z + ((row - ((rows - 1) / 2)) * SpacingMm));

            world.OrderMove(id, destination, entity.TeamId);
            index++;
        }
    }

    /// <summary>A point roughly at a unit's centre, used for picking.</summary>
    private Vector3 UnitAnchor(int slot, ref Entity entity)
        => _simulation!.GetRenderPosition(slot, interpolate: true) + new Vector3(0f, 2f, 0f);

    /// <summary>Projects a world position to screen pixels, rejecting points behind the camera.</summary>
    private bool TryProjectToScreen(Vector3 world, out Vector2 screen)
    {
        Viewport viewport = GraphicsDevice.Viewport;
        Matrix viewProjection = _camera!.GetView() * _camera.GetProjection(viewport.AspectRatio);
        Vector4 clip = Vector4.Transform(new Vector4(world, 1f), viewProjection);

        if (clip.W <= 0.001f)
        {
            screen = default;
            return false;
        }

        screen = new Vector2(
            ((clip.X / clip.W * 0.5f) + 0.5f) * viewport.Width,
            ((-clip.Y / clip.W * 0.5f) + 0.5f) * viewport.Height);

        return true;
    }

    /// <summary>Selects the player's headquarters, for screenshots and quick starts.</summary>
    private void SelectPlayerHeadquarters()
    {
        SimWorld world = _simulation!.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == PlayerTeam && entity.Kind == UnitKind.CommandCentre)
            {
                _selection.Select(new EntityId(slot, entity.Generation));
                _camera?.FocusOn(SimBridge.ToMetres(entity.Position));
                return;
            }
        }
    }

    /// <summary>
    /// Slot of the single selected building, or -1 when the selection is empty or
    /// holds something that is not a structure.
    /// </summary>
    private int SelectedBuildingSlot()
    {
        if (_selection.Count != 1 || _simulation is null)
        {
            return -1;
        }

        if (!_simulation.World.TryGetRef(_selection.Selected[0], out _, out int slot))
        {
            return -1;
        }

        return IsBuilding(_simulation.World.GetRefBySlot(slot).Kind) ? slot : -1;
    }

    /// <summary>Slot of the single selected entity when it is a unit, else -1.</summary>
    private int SelectedUnitSlot()
    {
        int slot = SelectedBuildingSlot();

        if (slot >= 0)
        {
            return -1;
        }

        if (_selection.Count != 1 || _simulation is null)
        {
            return -1;
        }

        return _simulation.World.TryGetRef(_selection.Selected[0], out _, out int unitSlot) ? unitSlot : -1;
    }

    /// <summary>True for structures, which are the only things with a build menu.</summary>
    private static bool IsBuilding(UnitKind kind)
        => kind is UnitKind.CommandCentre or UnitKind.PowerPlant or UnitKind.Factory or UnitKind.DesignBureau;

    /// <summary>Turns a HUD button press into a simulation command.</summary>
    private void ApplyHudCommand(HudCommand command)
    {
        int slot = SelectedBuildingSlot();

        if (slot < 0 || _simulation is null)
        {
            return;
        }

        ref Entity building = ref _simulation.World.GetRefBySlot(slot);
        var id = new EntityId(slot, building.Generation);
        long executeTick = _simulation.World.Tick + 1;

        switch (command.Kind)
        {
            case HudCommandKind.QueueUnit:
                _simulation.World.Enqueue(SimCommand.QueueUnit(id, command.Unit, executeTick, building.TeamId));
                break;

            case HudCommandKind.Research:
                _simulation.World.Enqueue(SimCommand.Research(id, command.Tech, executeTick, building.TeamId));
                break;

            case HudCommandKind.ApproveDesign:
                _simulation.World.Enqueue(SimCommand.ApproveDesign(id, command.Unit, executeTick, building.TeamId));
                break;

            case HudCommandKind.UseAbility:
                _pendingAbility = command.Ability;
                break;

            case HudCommandKind.BuildBridge:
                _pendingAbility = AbilityId.None;
                _pendingBridge = true;
                break;

            case HudCommandKind.Licence:
                int ally = AllyBuildingSlot();

                if (ally >= 0)
                {
                    ref Entity allyBuilding = ref _simulation.World.GetRefBySlot(ally);
                    var allyId = new EntityId(ally, allyBuilding.Generation);

                    _simulation.World.Enqueue(SimCommand.Licence(allyId, command.Unit, executeTick, building.TeamId));
                }

                break;
        }
    }

    /// <summary>Slot of the ally's first building, which is where licences land.</summary>
    private int AllyBuildingSlot()
    {
        if (_simulation is null)
        {
            return -1;
        }

        SimWorld world = _simulation.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == 1 && IsBuilding(entity.Kind))
            {
                return slot;
            }
        }

        return -1;
    }

    /// <summary>
    /// Turns a shot the simulation has already resolved into something to look at:
    /// a flash at the muzzle and a round on its way to the target.
    /// <para>
    /// The muzzle is approximated rather than read off the model's barrel. The barrel
    /// has been rotated to face the target by then, and its own transform lives in
    /// the render pass; what matters visually is only that the flash is at the front
    /// of the shooter at roughly gun height, which is the model's size and the
    /// direction to the target.
    /// </para>
    /// </summary>
    private void FireShot(ref SimWorld world, in SimEvent shot, Vector3 listener)
    {
        if (_projectiles is null || _simulation is null)
        {
            return;
        }

        if (FireProfiles.For(shot.Kind) is not { } profile)
        {
            return;
        }

        float size = ModelCatalog.NominalSizeMetres(shot.Faction, shot.Kind);

        if (!world.IsAliveSlot(shot.TargetSlot))
        {
            return;
        }

        Vector3 origin = shot.Position;
        Vector3 target = _simulation.GetRenderPosition(shot.TargetSlot, interpolate: true);

        Vector3 delta = target - origin;
        float range = delta.Length();

        if (range < 0.05f)
        {
            return;
        }

        Vector3 direction = delta / range;

        // Stand the flash off the front of the shooter, and lift it to gun height:
        // a third of the way up a person, rather less of a tank.
        float reach = size * 0.45f;
        float height = Math.Clamp(size * 0.28f, 0.7f, 2.6f);

        Vector3 muzzle = origin
            + (direction * reach)
            + new Vector3(0f, height, 0f);

        Vector3 impact = target + new Vector3(0f, Math.Clamp(size * 0.20f, 0.4f, 1.8f), 0f);

        _projectiles.Fire(profile, muzzle, impact, MathF.Max(size / 6.4f, 0.42f));

        // The shot is heard where it was fired, not where it is going.
        _sfx?.Play(profile.Launch, muzzle, listener, 0.9f, 0f);

        _peakShotsInFlight = Math.Max(_peakShotsInFlight, _projectiles.LiveCount);
    }

    /// <summary>
    /// Draws the rounds in flight.
    /// <para>
    /// Solid rounds go through the lit pass, because a rocket is a thing with a
    /// shape. Tracers go through the additive pass, because a bullet is a streak of
    /// light and lighting it like a box makes it look like a flying brick.
    /// </para>
    /// </summary>
    private void DrawProjectiles()
    {
        if (_projectiles is not { } shells ||
            _projectileMesh is null ||
            _options.IsModelGallery)
        {
            return;
        }

        if (shells.AlphaCount > 0)
        {
            _renderer!.Draw(_projectileMesh, shells.AlphaInstances, shells.AlphaCount);
            _drawCalls++;
            _instancesSubmitted += shells.AlphaCount;
        }

        if (shells.AdditiveCount > 0)
        {
            _renderer!.BeginParticles(InstancedRenderer.ParticleBlend.Additive);
            _renderer.Draw(_projectileMesh, shells.AdditiveInstances, shells.AdditiveCount);
            _renderer.EndParticles();
            _drawCalls++;
            _instancesSubmitted += shells.AdditiveCount;
        }
    }

    /// <summary>
    /// Lays out one of every effect in the game, so a single screenshot shows the
    /// whole catalogue side by side under the same light.
    /// <para>
    /// Explosions were once the entire vocabulary, and a row of them of increasing
    /// size was enough. There are now separate effects for a muzzle flash, a rifle
    /// round on armour, a small burst, a shell, a crater, a flak burst and an
    /// electric discharge, and they are only worth having if each is visibly its own
    /// thing — which is a claim that has to be looked at, not asserted.
    /// </para>
    /// </summary>
    private void SpawnParticleDemo()
    {
        if (_particles is null || _simulation is null)
        {
            return;
        }

        MiVic.Core.Terrain.HeightMap terrain = _simulation.World.Terrain;

        float Ground(float x, float z) => terrain.SampleHeightMm((int)(x * 1000f), (int)(z * 1000f)) / 1000f;

        // Somewhere the player can actually see. Particles are drawn under the fog
        // overlay — an explosion on ground the player cannot see must not give away
        // that something is there — so a demo laid out on unseen ground is a demo of
        // the fog, and looks like nothing at all.
        _effectGridCentre = FindVisibleFixtureCentre();

        void At(int column, int row, Action<Vector3> spawn)
        {
            float x = _effectGridCentre.X + ((column - 1) * EffectGridPitch);
            float z = _effectGridCentre.Z + ((row - 1) * EffectGridPitch);

            spawn(new Vector3(x, Ground(x, z), z));
        }

        _effectGridSpawn = At;

        // The camera was set up before the world existed, so it could not be pointed at
        // the grid then. Pointed now, once the grid knows where it is.
        _camera?.FocusOn(_effectGridCentre);

        SpawnEffectGrid();
    }

    /// <summary>
    /// Sets up the firing line: one of every weapon in the game, firing on a repeating
    /// cycle so that any frame has rounds of several kinds in the air at once.
    /// <para>
    /// Rounds are in flight for between a fifth of a second and most of one, so a
    /// battle is a poor place to try to look at one: whether a rocket is on screen at
    /// the moment a screenshot is taken is luck. Firing on a timer turns luck into a
    /// certainty.
    /// </para>
    /// </summary>
    private void BuildFireDemo()
    {
        if (_simulation is null)
        {
            return;
        }

        MiVic.Core.Terrain.HeightMap terrain = _simulation.World.Terrain;

        // Every weapon the game has, in the order the profiles table lists them.
        UnitKind[] kinds =
        [
            UnitKind.Infantry, UnitKind.Tank, UnitKind.Artillery,
            UnitKind.RocketArtillery, UnitKind.AntiAir, UnitKind.Aircraft, UnitKind.ElectroPrototype,
        ];

        Vector3 centre = FindVisibleFixtureCentre();
        var line = new (UnitKind Kind, Vector3 Origin, Vector3 Target, float Timer)[kinds.Length];

        for (int i = 0; i < kinds.Length; i++)
        {
            float x = centre.X + ((i - ((kinds.Length - 1) * 0.5f)) * 34f);
            float z = centre.Z - 90f;
            float ground = terrain.SampleHeightMm((int)(x * 1000f), (int)(z * 1000f)) / 1000f;

            line[i] = (
                kinds[i],
                new Vector3(x, ground + 1.6f, z),
                new Vector3(x, ground + 1.2f, z + 150f),
                i * 0.09f);
        }

        _fireDemo = line;
        _camera?.FocusOn(new Vector3(centre.X, 0f, centre.Z - 15f));
    }

    /// <summary>Runs the firing line. Demo fixture only.</summary>
    private void UpdateFireDemo(float elapsedSeconds)
    {
        if (!_options.FireDemo || _projectiles is null || _fireDemo.Length == 0)
        {
            return;
        }

        for (int i = 0; i < _fireDemo.Length; i++)
        {
            (UnitKind kind, Vector3 origin, Vector3 target, float timer) = _fireDemo[i];

            timer -= elapsedSeconds;

            if (timer <= 0f)
            {
                if (FireProfiles.For(kind) is { } profile)
                {
                    float size = ModelCatalog.NominalSizeMetres(Faction.Soviet, kind);

                    _projectiles.Fire(profile, origin, target, MathF.Max(size / 6.4f, 0.55f));
                }

                timer += FireDemoInterval;
            }

            _fireDemo[i] = (kind, origin, target, timer);
        }
    }

    /// <summary>Seconds between volleys in the firing-line fixture.</summary>
    private const float FireDemoInterval = 0.55f;

    /// <summary>The firing line's weapons, positions and timers.</summary>
    private (UnitKind Kind, Vector3 Origin, Vector3 Target, float Timer)[] _fireDemo = [];

    /// <summary>
    /// The tactical nuke on its own, for the fixture.
    /// <para>
    /// It is not part of the effect grid and never can be: its cloud is two hundred
    /// metres across and climbs a hundred metres, so from any camera that frames a
    /// muzzle flash it fills the entire screen. A catalogue and a catastrophe do not
    /// belong in the same photograph.
    /// </para>
    /// </summary>
    private void SpawnNukeDemo()
    {
        if (_particles is null || _simulation is null)
        {
            return;
        }

        MiVic.Core.Terrain.HeightMap terrain = _simulation.World.Terrain;
        float ground = terrain.SampleHeightMm(0, 0) / 1000f;

        _particles.SpawnNuke(
            new Vector3(0f, ground + 1f, 0f),
            AbilityRadiusMetres(AbilityId.TacticalNuke));
    }

    /// <summary>Distance between the effect grid's cells, in metres.</summary>
    private const float EffectGridPitch = 25f;

    /// <summary>
    /// How long one cell of the effect grid runs before restarting. Longer than the
    /// longest short-lived effect, so a cell is never empty for long.
    /// </summary>
    private const float EffectCellCycle = 2.4f;

    /// <summary>The ground-height lookup the grid was built against.</summary>
    private Action<int, int, Action<Vector3>>? _effectGridSpawn;

    /// <summary>Where the effect grid is centred: near the player, so it is not fogged.</summary>
    private Vector3 _effectGridCentre;

    /// <summary>
    /// Finds a spot the player's team can see, on dry land, to lay the effect grid on.
    /// <para>
    /// Three things have to be true of it, and each one cost a render to find out. It
    /// has to be somewhere the player can see, because particles are drawn under the
    /// fog overlay and a grid on unseen ground is a grid of fog. It has to be on land,
    /// because a grid laid out on a lake draws its effects on the lake bed, under the
    /// water. And it has to be anchored to a ground unit, because an aircraft's
    /// position is tens of metres up and moving.
    /// </para>
    /// </summary>
    private Vector3 FindVisibleFixtureCentre()
    {
        if (_simulation is null)
        {
            return Vector3.Zero;
        }

        SimWorld world = _simulation.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != PlayerTeam || entity.Kind is UnitKind.Aircraft or UnitKind.Drone)
            {
                continue;
            }

            // One cell diagonally from the unit itself: clear of the unit, and well
            // inside the vision it provides.
            Vector3 position = _simulation.GetRenderPosition(slot, interpolate: false);
            var centre = new Vector3(position.X + EffectGridPitch, 0f, position.Z + EffectGridPitch);

            if (IsDryLand(world, centre))
            {
                return centre;
            }
        }

        return Vector3.Zero;
    }

    /// <summary>
    /// True when the effect grid laid out at <paramref name="centre"/> would be on dry
    /// land: every cell, not just the middle, since one corner in a lake is one effect
    /// drawn underwater.
    /// </summary>
    private static bool IsDryLand(SimWorld world, Vector3 centre)
    {
        TerrainLayer terrain = world.TerrainTypes;

        for (int column = -1; column <= 1; column++)
        {
            for (int row = -1; row <= 2; row++)
            {
                int x = (int)(centre.X + (column * EffectGridPitch));
                int z = (int)(centre.Z + (row * EffectGridPitch));
                int index = terrain.IndexOfWorld(x * WorldPos.MmPerMetre, z * WorldPos.MmPerMetre);

                if (index < 0)
                {
                    return false;
                }

                TerrainType type = terrain.TypeAt(index);

                if (type is TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava)
                {
                    return false;
                }
            }
        }

        return true;
    }

    /// <summary>
    /// One cell of the demo grid: where it is, what it spawns, and how long until it
    /// spawns it again.
    /// <para>
    /// Per cell, and staggered, because the alternative is a fixture that can only be
    /// photographed by luck. These effects last between a tenth of a second and four
    /// seconds, and a headless run's frame times are not predictable, so a single
    /// shared timer means every screenshot lands with either everything or nothing
    /// alive. Offsetting each cell by a slice of the cycle guarantees that whatever
    /// frame is taken, some cells are mid-effect.
    /// </para>
    /// </summary>
    private (int Column, int Row, Action<Vector3> Spawn, float Timer)[] _effectCells = [];

    /// <summary>
    /// Lays out one of every weapon effect the game has, in a grid: the effects are
    /// of wildly different sizes, and a single overhead view has to hold all of them
    /// at a distance where the smallest is still more than a few pixels.
    /// </summary>
    private void SpawnEffectGrid()
    {
        if (_particles is null || _effectGridSpawn is not { } at)
        {
            return;
        }

        var cells = new List<(int Column, int Row, Action<Vector3> Spawn)>
        {
            // Row one: damage.
            (-1, -1, p => _particles.SpawnExplosion(p + new Vector3(0f, 1.5f, 0f), 2.2f)),
            (0, -1, p => _particles.SpawnShellBurst(p + new Vector3(0f, 1f, 0f), 1.5f)),
            (1, -1, p => _particles.SpawnGroundBurst(p + new Vector3(0f, 1f, 0f), 2.6f)),

            // Row two: the weapon effects that are not explosions.
            (-1, 0, p => _particles.SpawnMuzzleFlash(p + new Vector3(0f, 1.4f, 0f), new Vector3(1f, 0.86f, 0.48f), 1.1f)),
            (0, 0, p => _particles.SpawnImpactSparks(p + new Vector3(0f, 1.2f, 0f), new Vector3(1f, 0.86f, 0.48f), 0.45f)),
            (1, 0, p => _particles.SpawnSmallBurst(p + new Vector3(0f, 1f, 0f), 0.9f)),

            // Row three: the air, the odd ones out, and the two that outlive their blast.
            (-1, 1, p => _particles.SpawnAirburst(p + new Vector3(0f, 12f, 0f), 1.3f, new Vector3(1f, 0.94f, 0.62f))),
            (0, 1, p => _particles.SpawnElectricBurst(p + new Vector3(0f, 1.6f, 0f), 1.2f)),
            (1, 1, p => _particles.SpawnShockwave(p + new Vector3(0f, 0f, 0f), 3.4f)),

            (-1, 2, p => _particles.SpawnScorch(p + new Vector3(0f, 0.2f, 0f), 2.2f)),
            (0, 2, p => _particles.SpawnSmokePlume(p + new Vector3(0f, 3f, 0f), 0.8f)),
            (1, 2, p => _particles.SpawnSmokePlume(p + new Vector3(0f, 3f, 0f), 1.1f)),
        };

        _effectCells = new (int, int, Action<Vector3>, float)[cells.Count];

        for (int i = 0; i < cells.Count; i++)
        {
            // Staggered across the cycle, so the grid is never all-alive or all-dead.
            float offset = EffectCellCycle * i / cells.Count;

            _effectCells[i] = (cells[i].Column, cells[i].Row, cells[i].Spawn, offset);
        }
    }

    /// <summary>
    /// Advances every cell of the effect grid, restarting each one on its own cycle.
    /// Demo fixture only.
    /// </summary>
    private void UpdateEffectDemo(float elapsedSeconds)
    {
        if (!_options.ParticleDemo || _particles is null || _effectGridSpawn is not { } at || _effectCells.Length == 0)
        {
            return;
        }

        for (int i = 0; i < _effectCells.Length; i++)
        {
            (int column, int row, Action<Vector3> spawn, float timer) = _effectCells[i];

            timer -= elapsedSeconds;

            if (timer <= 0f)
            {
                at(column, row, spawn);
                timer += EffectCellCycle;
            }

            _effectCells[i] = (column, row, spawn, timer);
        }
    }

    /// <summary>Ages the move-order rings and drops the expired ones.</summary>
    private void UpdateOrderMarkers(float elapsedSeconds)
    {
        for (int i = _orderMarkers.Count - 1; i >= 0; i--)
        {
            OrderMarker marker = _orderMarkers[i] with { Age = _orderMarkers[i].Age + elapsedSeconds };

            if (marker.Age >= OrderMarkerSeconds)
            {
                _orderMarkers.RemoveAt(i);
            }
            else
            {
                _orderMarkers[i] = marker;
            }
        }
    }

    /// <summary>
    /// Turns simulation events into particles and keeps the ambient smoke going.
    /// <para>
    /// Everything here is one-way: the simulation reports deaths, the client
    /// decides what they look like. Nothing in this method can affect a tick, so
    /// particles are free to use a wall-clock delta and their own generator.
    /// </para>
    /// </summary>
    private void UpdateParticles(float elapsedSeconds)
    {
        if (_particles is null || _simulation is null)
        {
            return;
        }

        SimWorld world = _simulation.World;
        Vector3 listener = _camera!.Target;
        _sfx?.BeginFrame();

        if (_sfx is not null)
        {
            // A strategic-zoom camera is looking at the whole battlefield, so the
            // audible radius has to grow with it or the far half of the map would
            // be silent.
            _sfx.FalloffDistance = MathF.Max(SfxDirector.DefaultFalloffDistance, _camera.Distance * 1.8f);
        }

        foreach (SimEvent simEvent in _simulation.Events)
        {
            // An enemy dying where the player cannot see must not produce a
            // visible explosion, or fog of war would leak information.
            bool visible = simEvent.TeamId == PlayerTeam ||
                SimWorld.AreAllied(simEvent.TeamId, PlayerTeam) ||
                world.Visibility.IsVisible(PlayerTeam, world.Navigation.IndexOfWorld(simEvent.PositionMm));

            if (!visible)
            {
                continue;
            }

            if (simEvent.Type == SimEventType.ShotFired)
            {
                // A shot: a muzzle flash and a round on its way. Nothing has landed
                // yet — the damage was applied when the weapon fired, and the round
                // drawn here is the picture of that.
                FireShot(ref world, in simEvent, listener);
                continue;
            }

            if (simEvent.Type == SimEventType.UnitHit)
            {
                // The impact is drawn by the round that arrives, so this only needs
                // the sound. A rifle round hitting infantry should not sound like a
                // shell hitting a factory.
                float pitch = simEvent.Kind switch
                {
                    UnitKind.CommandCentre or UnitKind.Factory or UnitKind.PowerPlant => -0.55f,
                    UnitKind.DesignBureau => -0.4f,
                    UnitKind.Tank or UnitKind.Artillery or UnitKind.AntiAir or UnitKind.RocketArtillery => -0.1f,
                    UnitKind.Aircraft => 0.25f,
                    UnitKind.Drone => 0.4f,
                    _ => 0.5f,
                };

                float volume = Math.Clamp(0.25f + (simEvent.Damage / 60f), 0.25f, 1f);
                _sfx?.Play(SoundEffectKind.Impact, simEvent.Position, listener, volume, pitch);
                continue;
            }

            Vector3 tint = FactionPalette.Primary(simEvent.Faction).ToVector3() * 0.35f;
            _particles!.SpawnExplosion(simEvent.Position, simEvent.Scale, new Vector3(
                0.28f + (tint.X * 0.4f),
                0.27f + (tint.Y * 0.3f),
                0.26f + (tint.Z * 0.3f)));

            // A wreck is not finished exploding. Vehicles and structures carry
            // something that burns, and a couple of delayed bangs is the cheapest
            // way to say so — and the reason a battlefield keeps moving after the
            // shooting stops.
            bool structure = simEvent.Kind is UnitKind.CommandCentre or UnitKind.Factory
                or UnitKind.PowerPlant or UnitKind.NuclearPlant or UnitKind.DesignBureau;

            bool vehicle = simEvent.Kind is UnitKind.Tank or UnitKind.Artillery
                or UnitKind.AntiAir or UnitKind.RocketArtillery or UnitKind.Harvester
                or UnitKind.Aircraft or UnitKind.ElectroPrototype;

            if (structure || vehicle)
            {
                _projectiles?.ScheduleCookOff(
                    simEvent.Position,
                    simEvent.Scale,
                    structure ? 6 : 3,
                    new Vector3(0.30f, 0.27f, 0.25f));

                _camera?.Shake(structure ? simEvent.Scale * 0.22f : simEvent.Scale * 0.10f);
            }

            if (simEvent.Kind is UnitKind.CommandCentre or UnitKind.Factory)
            {
                // A big structure keeps burning for a moment after it goes up.
                for (int i = 0; i < 4; i++)
                {
                    _particles.SpawnSmokePlume(simEvent.Position, 0.9f);
                }

                _sfx?.Play(SoundEffectKind.ExplosionLarge, simEvent.Position, listener, 1f, -0.3f);
            }
            else
            {
                _sfx?.Play(SoundEffectKind.ExplosionSmall, simEvent.Position, listener, 0.9f, 0.1f - (simEvent.Scale * 0.05f));
            }
        }

        _simulation.ClearEvents();

        _peakParticles = Math.Max(_peakParticles, _particles.LiveCount);
        _peakParticleInstances = Math.Max(_peakParticleInstances, _particles.AlphaCount + _particles.AdditiveCount);
        _peakShotsInFlight = Math.Max(_peakShotsInFlight, _projectiles?.LiveCount ?? 0);

        // Ambient smoke: structures that are working or damaged, and vehicles
        // that are badly hurt. Timers stagger the puffs so the battlefield does
        // not pulse in unison.
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity && slot < _smokeTimers.Length; slot++)
        {
            _smokeTimers[slot] -= elapsedSeconds;

            if (_smokeTimers[slot] > 0f || !world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            bool visible = entity.TeamId == PlayerTeam ||
                world.Visibility.IsVisible(PlayerTeam, world.Navigation.IndexOfWorld(entity.Position));

            if (!visible)
            {
                continue;
            }

            float healthFraction = definition.Health > 0 ? (float)entity.Health / definition.Health : 1f;
            Vector3 position = _simulation.GetRenderPosition(slot, interpolate: true);

            if (definition.IsBuilding)
            {
                MeshBatch batch = GetBatch(entity.Faction, entity.Kind);
                Matrix entityTransform = Matrix.CreateTranslation(position);
                float build = BuildFraction(ref entity);

                if (build < 1f)
                {
                    // Being raised: dust off the ground, heavier the less of it
                    // there is.
                    _particles.SpawnDust(position + new Vector3(0f, 1.2f, 0f), 1.2f + ((1f - build) * 1.8f));
                    _smokeTimers[slot] = 0.18f;
                    _wasBuilding[slot] = true;
                    continue;
                }

                if (_wasBuilding[slot])
                {
                    // Just finished: one good puff, so the moment it comes up reads.
                    _wasBuilding[slot] = false;
                    _particles.SpawnDust(position + new Vector3(0f, 2.5f, 0f), 5f);
                }

                // Working smoke, out of the chimney rather than out of the middle
                // of the roof. The part contract names it, so a building smokes
                // properly by exporting a part called `stack` or `barrel`.
                if (healthFraction >= 0.65f && ChimneyPart(entity.Kind) is { } chimney &&
                    TryPartWorldPosition(batch, chimney, entityTransform, out Vector3 vent))
                {
                    _particles.SpawnSmokePlume(vent, 0.22f);
                    _smokeTimers[slot] = 0.30f + ((slot % 7) * 0.06f);
                }
                else
                {
                    // Damaged industry smokes hard, from wherever it is burning.
                    float intensity = healthFraction < 0.65f ? 0.35f + ((1f - healthFraction) * 0.65f) : 0.16f;
                    _particles.SpawnSmokePlume(position + new Vector3(0f, 4f, 0f), intensity);
                    _smokeTimers[slot] = 0.25f + ((slot % 7) * 0.06f);
                }
            }
            else if (healthFraction < 0.45f)
            {
                _particles.SpawnSmokePlume(position + new Vector3(0f, 1.6f, 0f), 0.45f);
                _smokeTimers[slot] = 0.7f + ((slot % 5) * 0.1f);
            }
            else
            {
                _smokeTimers[slot] = 1f;
            }
        }

        Matrix view = _camera!.GetView();
        float sizeScale = Math.Clamp(_camera.Distance / 140f, 1f, 4f);

        _particles.Update(
            elapsedSeconds,
            new Vector3(view.M11, view.M21, view.M31),
            new Vector3(view.M12, view.M22, view.M32),
            sizeScale);

        _projectiles?.Update(
            elapsedSeconds,
            new Vector3(view.M11, view.M21, view.M31),
            new Vector3(view.M12, view.M22, view.M32),
            sizeScale);

        UpdatePendingStrikes();
        UpdateEffectDemo(elapsedSeconds);
        UpdateFireDemo(elapsedSeconds);

        // Shake is requested by whatever was loudest this frame, and applied to the
        // camera for the *next* one. Applying it here would move the view under a
        // frame that has already been built from it.
        if (_projectiles is { PendingShake: > 0f } shells)
        {
            _camera.Shake(shells.PendingShake);
        }
    }

    /// <summary>
    /// Writes the recorded match when the player quits, so an interactive
    /// <c>--record</c> run produces a replay without any extra step.
    /// </summary>
    private void SaveRecording()
    {
        if (_options.RecordPath is not { } path || _simulation is null || !_simulation.World.IsRecording)
        {
            return;
        }

        try
        {
            ReplayFile.Capture(_simulation.World, _simulation.Scenario).Save(path);
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // A failed save must not take the shutdown path down with it.
        }
    }

    /// <summary>
    /// Drives click selection the way a player does: project a unit to a pixel
    /// and hand that pixel to the same single-click handler the mouse path calls.
    /// </summary>
    private string CheckClickSelection()
    {
        if (_simulation is null)
        {
            return "FAIL: no simulation";
        }

        SimWorld world = _simulation.World;
        int slot = -1;

        for (int i = 0; i < world.Capacity; i++)
        {
            if (!world.IsAliveSlot(i))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(i);

            if (candidate.TeamId == PlayerTeam && !UnitCatalog.Flies(candidate.Kind))
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            return "FAIL: no player unit";
        }

        ref Entity unit = ref world.GetRefBySlot(slot);

        if (!TryProjectToScreen(UnitAnchor(slot, ref unit), out Vector2 screen))
        {
            return "FAIL: unit is not on screen";
        }

        _selection.Clear();

        // now = 0 so this cannot be mistaken for a double click.
        SelectSingle(screen, additive: false, now: 0d);

        bool selected = _selection.Selected.Contains(new EntityId(slot, unit.Generation));
        _selection.Clear();

        return selected ? "OK (click selected the unit)" : "FAIL (click did not select the unit)";
    }

    /// <summary>
    /// Drives the right-click move order exactly as the player does: project a
    /// point on the ground to a pixel, feed that pixel to the same handler a
    /// right-click calls, and confirm the unit actually walks there.
    /// </summary>
    private string CheckMoveOrderPath()
    {
        if (_simulation is null)
        {
            return "FAIL: no simulation";
        }

        if (IsPlayback)
        {
            return "SKIPPED (playback)";
        }

        SimWorld world = _simulation.World;
        int slot = -1;

        for (int i = 0; i < world.Capacity; i++)
        {
            if (!world.IsAliveSlot(i))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(i);

            if (candidate.TeamId == PlayerTeam &&
                !UnitCatalog.Get(candidate.Kind).IsBuilding &&
                !UnitCatalog.Flies(candidate.Kind))
            {
                slot = i;
                break;
            }
        }

        if (slot < 0)
        {
            return "FAIL: no player ground unit";
        }

        ref Entity unit = ref world.GetRefBySlot(slot);
        var id = new EntityId(slot, unit.Generation);
        WorldPos before = unit.Position;

        // Pick a destination the unit can actually reach: a passable cell a good
        // distance away. A fixed offset from the camera target can land in a lake,
        // in which case the goal snaps back onto the unit and the check measures
        // nothing.
        PathContext pathContext = SimWorld.PathContextFor(unit.Faction, unit.Kind);
        int unitCell = world.Navigation.IndexOfWorld(unit.Position);
        int destinationCell = -1;

        for (int radius = 10; radius <= 30 && destinationCell < 0; radius++)
        {
            for (int dz = -radius; dz <= radius && destinationCell < 0; dz++)
            {
                for (int dx = -radius; dx <= radius && destinationCell < 0; dx++)
                {
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = world.Navigation.IndexOf(
                        world.Navigation.CellX(unitCell) + dx,
                        world.Navigation.CellZ(unitCell) + dz);

                    if (candidate >= 0 && world.TerrainTypes.IsPassable(candidate, pathContext.Movement))
                    {
                        destinationCell = candidate;
                    }
                }
            }
        }

        if (destinationCell < 0)
        {
            return "FAIL: no reachable destination near the unit";
        }

        // Geometric check first: project a point on the ground, then unproject
        // that pixel. If the two disagree, every click lands somewhere else — the
        // bug that made right-click orders go nowhere.
        HeightMap terrain = world.Terrain;
        WorldPos destination = world.Navigation.CentreOf(destinationCell);
        Vector3 probe = new(destination.X / 1000f, 0f, destination.Z / 1000f);
        probe.Y = HeightAtMetres(terrain, probe.X, probe.Z);

        if (!TryProjectToScreen(probe, out Vector2 probePixel))
        {
            return "FAIL: probe point is not on screen";
        }

        if (!TryScreenToGround(probePixel, out Vector3 probeGround))
        {
            return $"FAIL: ground ray missed [{_groundRayDebug}]";
        }

        float error = Vector2.Distance(new Vector2(probe.X, probe.Z), new Vector2(probeGround.X, probeGround.Z));

        if (error > 2f)
        {
            return $"FAIL: click mapping is off by {error:0.0} m [{_groundRayDebug}]";
        }

        // Then the behaviour: click that pixel and confirm the unit walks.
        _selection.Clear();
        _selection.Select(id);
        IssueOrderAtCursor(probePixel);

        if (world.PendingCommandCount == 0)
        {
            _selection.Clear();
            return "FAIL: no command was queued";
        }

        world.RunTicks(40);

        int distanceMm = IntMath.Abs(unit.Position.X - before.X) + IntMath.Abs(unit.Position.Z - before.Z);
        _selection.Clear();

        return distanceMm > 5_000
            ? $"OK (click mapping {error:0.00} m, unit moved {distanceMm / 1000} m)"
            : $"FAIL (unit did not move; {distanceMm} mm, path {unit.PathLength}, goal {unit.HasMoveGoal}, " +
              $"cell {unitCell} {world.TerrainTypes.TypeAt(unitCell)}, dest {destinationCell} " +
              $"{world.TerrainTypes.TypeAt(destinationCell)}, {pathContext.Movement})";
    }

    /// <summary>
    /// Reports the mission's objective states, so a headless run proves the
    /// campaign logic actually ran rather than only that it compiled.
    /// </summary>
    private string CheckMissionObjectives()
    {
        if (_simulation is null)
        {
            return "n/a (skirmish)";
        }

        MissionDefinition? mission = _simulation.World.Mission;

        if (mission is null)
        {
            return "n/a (skirmish)";
        }

        ReadOnlySpan<ObjectiveState> states = _simulation.World.Objectives;
        StringBuilder text = new();
        text.Append(mission.Id).Append(": ");

        for (int i = 0; i < mission.Objectives.Count && i < states.Length; i++)
        {
            if (i > 0)
            {
                text.Append(", ");
            }

            text.Append(states[i].Status switch
            {
                ObjectiveStatus.Complete => "√",
                ObjectiveStatus.Failed => "×",
                _ => "•",
            });

            text.Append(' ');
            text.Append(states[i].Progress);
        }

        return text.ToString();
    }

    /// <summary>
    /// Records the match so far as a replay and immediately replays it into a
    /// fresh world. This is the end-to-end determinism check: the same seed, the
    /// same scenario and the same external commands must produce the same state
    /// hash, with the AI re-deriving its own orders.
    /// </summary>
    private string CheckReplayRoundTrip()
    {
        if (_simulation is null)
        {
            return "FAIL: no simulation";
        }

        if (_options.VictoryDemo)
        {
            // The demo removes rival structures directly instead of through
            // commands, so its state is deliberately not reproducible from a log.
            return "SKIPPED (victory demo)";
        }

        // Playing a recording back is verified differently: the world has been
        // driven entirely by the log, so it must land on the recorded hash.
        if (_simulation.Playback is { } playback)
        {
            if (!_simulation.IsPlaybackFinished)
            {
                return $"SKIPPED (playback at tick {_simulation.World.Tick} of {playback.FinalTick})";
            }

            ulong actual = StateHash.Compute(_simulation.World);

            return actual == playback.FinalHash
                ? $"OK (playback reached recorded hash at tick {playback.FinalTick})"
                : $"FAIL (expected {playback.FinalHash}, got {actual})";
        }

        try
        {
            ReplayFile replay = ReplayFile.Capture(_simulation.World, _simulation.Scenario);
            ReplayResult result = replay.Verify();

            if (_options.RecordPath is { } path)
            {
                replay.Save(path);
            }

            return result.Matches
                ? $"OK ({replay.Commands.Count} commands, {replay.FinalTick} ticks, hash match)"
                : $"FAIL (expected {result.ExpectedHash}, replayed {result.ActualHash})";
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException or InvalidDataException)
        {
            return $"FAIL ({exception.GetType().Name}: {exception.Message})";
        }
    }

    /// <summary>
    /// Confirms the AI is actually playing: by the end of the self-test an AI team
    /// should be researching, building or producing something.
    /// </summary>
    private string CheckAiActivity()
    {
        if (_simulation is null)
        {
            return "FAIL: no simulation";
        }

        if (_options.VictoryDemo)
        {
            // The demo removes every rival structure, so the AI has nothing left
            // to research or build. The outcome check below covers this mode.
            return "SKIPPED (victory demo)";
        }

        if (IsPlayback)
        {
            // Playback is verified against the recorded hash; whether the AI has
            // got going by the end of a short recording says nothing about it.
            return "SKIPPED (playback)";
        }

        SimWorld world = _simulation.World;

        for (int team = 1; team < 3; team++)
        {
            TeamState state = world.Team(team);

            if (state.IsResearching)
            {
                return $"OK (team {team} researching tier {state.ResearchTargetTier})";
            }

            for (int slot = 0; slot < world.Capacity; slot++)
            {
                if (!world.IsAliveSlot(slot))
                {
                    continue;
                }

                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId == team && entity.QueueLength > 0)
                {
                    return $"OK (team {team} producing {entity.QueueLength} job(s))";
                }
            }
        }

        return "FAIL (no AI activity)";
    }

    /// <summary>
    /// Projects every player unit to the screen and picks at that exact pixel.
    /// The same unit must come back, unless another unit projects closer to the
    /// cursor — which happens when a formation is dense on screen. The hit rate
    /// therefore catches a broken projection without being defeated by overlap.
    /// </summary>
    private (int Hits, int Total) CheckPickingRoundTrip()
    {
        SimWorld world = _simulation!.World;
        int capacity = world.Capacity;
        int hits = 0;
        int total = 0;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != PlayerTeam)
            {
                continue;
            }

            if (!TryProjectToScreen(UnitAnchor(slot, ref entity), out Vector2 screen))
            {
                continue;
            }

            total++;

            if (PickSlotAt(screen) == slot)
            {
                hits++;
            }
        }

        return (hits, total);
    }

    /// <summary>
    /// Drives the build panel's command path end to end: select a headquarters,
    /// raise a queue request exactly as a button press would, step the simulation
    /// and confirm the job exists and the resources were spent.
    /// </summary>
    private string CheckHudCommandPath()
    {
        if (_simulation is null)
        {
            return "FAIL: no simulation";
        }

        if (IsPlayback)
        {
            // This check issues a command and steps the world, which would push a
            // playback past its own recording. Playback has its own hash check.
            return "SKIPPED (playback)";
        }

        SimWorld world = _simulation.World;
        int slot = -1;

        for (int i = 0; i < world.Capacity; i++)
        {
            if (world.IsAliveSlot(i))
            {
                ref Entity candidate = ref world.GetRefBySlot(i);

                if (candidate.TeamId == PlayerTeam && candidate.Kind == UnitKind.CommandCentre)
                {
                    slot = i;
                    break;
                }
            }
        }

        if (slot < 0)
        {
            return "FAIL: no player command centre";
        }

        // Pick something the team can actually afford. The check used to top up
        // the stockpile to force the issue, which silently broke mission replays:
        // a direct resource write is not a command, so a replay could not
        // reproduce it.
        UnitKind affordable = UnitKind.None;

        foreach (UnitDefinition definition in UnitCatalog.All)
        {
            if (definition.IsBuilding || definition.RequiredTechTier > world.Team(PlayerTeam).TechTier)
            {
                continue;
            }

            int cost = definition.MaterialCost * FactionProfile.For(SimWorld.FactionOfTeam(PlayerTeam)).CostPermille / 1_000;

            if (world.Team(PlayerTeam).Materials >= cost)
            {
                affordable = definition.Kind;
                break;
            }
        }

        if (affordable == UnitKind.None)
        {
            return "SKIPPED (nothing affordable)";
        }

        ref Entity building = ref world.GetRefBySlot(slot);
        _selection.Select(new EntityId(slot, building.Generation));

        int before = world.Team(PlayerTeam).Materials;
        ApplyHudCommand(new HudCommand(HudCommandKind.QueueUnit, affordable));
        world.Step();

        bool queued = world.JobsOf(slot).Length > 0;
        int after = world.Team(PlayerTeam).Materials;

        _selection.Clear();

        return queued && after < before
            ? $"OK (queued {affordable}, materials {before} -> {after})"
            : $"FAIL (queued={queued}, materials {before} -> {after})";
    }

    /// <summary>
    /// Drives the attack order path end to end: select an armed unit, raise the
    /// order exactly as a right-click on an enemy would, and confirm the unit
    /// locked onto that target.
    /// </summary>
    private string CheckCombatPath()
    {
        if (_simulation is null)
        {
            return "FAIL: no simulation";
        }

        if (_options.VictoryDemo)
        {
            // The demo has already destroyed every rival structure, which is the
            // target this check needs; the outcome check covers this mode instead.
            return "SKIPPED (victory demo)";
        }

        if (IsPlayback)
        {
            // Ordering an attack would add a command the recording never had.
            return "SKIPPED (playback)";
        }

        SimWorld world = _simulation.World;
        int attackerSlot = -1;
        int enemySlot = -1;

        for (int slot = 0; slot < world.Capacity && attackerSlot < 0; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(slot);

            if (candidate.TeamId == PlayerTeam && UnitCatalog.Get(candidate.Kind).IsArmed)
            {
                attackerSlot = slot;
            }
        }

        if (attackerSlot < 0)
        {
            return "FAIL: no armed player unit";
        }

        long best = long.MaxValue;
        ref Entity attacker = ref world.GetRefBySlot(attackerSlot);

        // Target a structure rather than a unit: buildings do not die mid-check,
        // so the assertion is about the order path, not about the battle.
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity candidate = ref world.GetRefBySlot(slot);

            if (candidate.TeamId == PlayerTeam || !IsBuilding(candidate.Kind))
            {
                continue;
            }

            long distance = attacker.Position.DistanceSquaredTo(candidate.Position);

            if (distance < best)
            {
                best = distance;
                enemySlot = slot;
            }
        }

        if (enemySlot < 0)
        {
            return "FAIL: no enemy structure";
        }

        var attackerId = new EntityId(attackerSlot, attacker.Generation);
        _selection.Select(attackerId);
        IssueAttackOrders(enemySlot);
        world.Step();

        bool locked = world.TryGet(attackerId, out Entity ordered) &&
                      ordered.HasAttackOrder &&
                      ordered.TargetSlot == enemySlot;

        _selection.Clear();

        return locked ? "OK (unit locked onto target)" : "FAIL (order did not take)";
    }

    private void SaveScreenshot(string path)
    {
        if (_screenshotTarget is null)
        {
            return;
        }

        string fullPath = Path.IsPathRooted(path) ? path : Path.Combine(AppContext.BaseDirectory, path);
        string? directory = Path.GetDirectoryName(fullPath);

        if (!string.IsNullOrEmpty(directory))
        {
            Directory.CreateDirectory(directory);
        }

        using FileStream stream = File.Create(fullPath);
        _screenshotTarget.SaveAsPng(stream, _screenshotTarget.Width, _screenshotTarget.Height);
    }

    private HudSnapshot BuildSnapshot()
        => new(
            _simulation!,
            _camera!,
            _frameTimes.Count > 0 ? (float)(1000d / _frameTimes[^1]) : 0f,
            _frameTimes.Count > 0 ? (float)_frameTimes[^1] : 0f,
            _instancesSubmitted,
            _drawCalls,
            _greekGlyphsOk,
            _catalog!.LoadedModels.Count,
            _catalog.FailedModels.Count,
            _selection.Count,
            SelectedBuildingSlot(),
            SelectedUnitSlot(),
            AllyBuildingSlot(),
            _imgui!.LargeFont,
            _simulation!.IsPlayback,
            _simulation!.IsPlaybackFinished);

    private bool Pressed(KeyboardState keyboard, Keys key)
        => keyboard.IsKeyDown(key) && !_previousKeyboard.IsKeyDown(key);

    /// <summary>True when the client is replaying a recorded match.</summary>
    private bool IsPlayback => _simulation?.IsPlayback == true;

    protected override void UnloadContent()
    {
        SaveRecording();

        _imgui?.Dispose();
        _worldLabels?.Dispose();
        _screenshotTarget?.Dispose();
        _audio?.Dispose();
        _sfx?.Dispose();

        _terrainMesh?.Dispose();
        _selectionMarkerMesh?.Dispose();
        _healthBackMesh?.Dispose();
        _healthFillMesh?.Dispose();
        _axisMesh?.Dispose();
        _fog?.Dispose();

        _catalog?.Dispose();
        _renderer?.Dispose();
        base.UnloadContent();
    }

    /// <summary>
    /// One mesh drawn as a single instanced batch: selection rings, order markers,
    /// gallery axes. Everything that is not a multi-part unit model.
    /// </summary>
    private sealed class SingleBatch
    {
        public SingleBatch(InstancedRenderer.Mesh mesh, int capacity)
        {
            Mesh = mesh;
            Instances = new InstanceData[capacity];
        }

        public InstancedRenderer.Mesh Mesh { get; }

        public InstanceData[] Instances { get; }

        public int Count { get; set; }
    }

    /// <summary>One part of one role: a mesh, its place in its parent, and its instances.</summary>
    private sealed class PartBatch
    {
        public PartBatch(ModelCatalog.PartMesh part, int capacity)
        {
            Mesh = part.Mesh;
            Name = part.Name;
            LocalTransform = part.LocalTransform;
            ParentIndex = part.ParentIndex;
            BoundsMax = part.BoundsMax;
            Instances = new InstanceData[capacity];
        }

        public InstancedRenderer.Mesh Mesh { get; }

        /// <summary>The part contract name, which is what animation keys on.</summary>
        public string Name { get; }

        /// <summary>Top of the part in its own space, used as the joint for a limb.</summary>
        public Vector3 BoundsMax { get; }

        /// <summary>Where the part sits inside its parent, before animation.</summary>
        public Matrix LocalTransform { get; }

        public int ParentIndex { get; }

        public InstanceData[] Instances { get; }

        public int Count { get; set; }
    }

    /// <summary>
    /// Every part of one faction's role, each drawn as its own instanced batch.
    /// <para>
    /// A model is a handful of parts, so a role costs a handful of draw calls
    /// instead of one per unit — which is what keeps hundreds of units affordable
    /// while still allowing a turret to point somewhere other than forward.
    /// </para>
    /// </summary>
    private sealed class MeshBatch
    {
        public MeshBatch(ModelCatalog.ModelParts model, int capacity)
        {
            ModelTransform = model.ModelTransform;
            Parts = new PartBatch[model.Parts.Length];

            for (int i = 0; i < model.Parts.Length; i++)
            {
                Parts[i] = new PartBatch(model.Parts[i], capacity);
            }

            Locals = new Matrix[model.Parts.Length];
        }

        public PartBatch[] Parts { get; }

        /// <summary>Alignment, scale, centring and grounding for the whole model.</summary>
        public Matrix ModelTransform { get; }

        /// <summary>Scratch space for one entity's animated part transforms.</summary>
        public Matrix[] Locals { get; }
    }
}
