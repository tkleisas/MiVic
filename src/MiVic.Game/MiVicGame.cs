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
using MiVic.Game.Rendering.Props;
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
/// <para>
/// The probe — the scripted inspection channel behind <c>--probe</c> — is the other
/// half of this class, in <c>Probe/MiVicGame.Probe.cs</c>. It is a partial rather
/// than a separate type because its whole job is to answer out of the code below:
/// a probe that reimplemented the animation would be a probe that could disagree
/// with what is drawn.
/// </para>
/// </summary>
public sealed partial class MiVicGame : XnaGame
{
    private const int WarmupFrames = 120;
    private const int MaxReportedFrames = 100_000;

    /// <summary>Team the human player commands: the Σοβιετικοί.</summary>
    private const int PlayerTeam = 0;

    private static readonly Color BackgroundColor = new(14, 16, 20);

    /// <summary>
    /// Whether this run draws fog of war at all.
    /// <para>
    /// A fixture is a laboratory rather than a view: it has no player, so the fog over it is
    /// a rule nobody in it is playing by — and that fog is not a harmless dimming. The client
    /// culls an enemy the player cannot see, so a fixture whose subject stands on another team
    /// photographs an empty clearing. Measured at the emplacement fixture's own camera, every
    /// one of its entities projected inside the viewport and the command centre was the only
    /// one drawn; the armour fixture drew none of its six, and the mud fixture none of the
    /// three tanks it exists to measure. The ground under them was black for the same reason:
    /// the overlay that says "nobody is looking here" covered half the armour fixture's frame
    /// and a third of the emplacement one.
    /// </para>
    /// <para>
    /// A fixture that is <em>about</em> what a player can see still has to buy its own eyes,
    /// which is what the wood and ground fixtures do by ringing their frame with observers.
    /// This is for the rest: fog over the subject is fog over the answer, exactly as the HUD
    /// over it would be, and both are off in an inspection run.
    /// </para>
    /// <para>
    /// Presentation only. The simulation's visibility grid is untouched, so a probe's
    /// <c>exposure</c>, <c>detect</c> and <c>range</c> answers are still the ones the guns ask
    /// for, and a match is unchanged.
    /// </para>
    /// </summary>
    private bool DrawsFogOfWar => !_options.IsFixture;

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

    /// <summary>The window title with the build's version on it.</summary>
    private static readonly string FullWindowTitle = $"{GreekWindowTitle}  {GameVersion.Display}";

    private readonly GraphicsDeviceManager _graphics;
    private readonly LaunchOptions _options;
    private readonly Stopwatch _frameStopwatch = Stopwatch.StartNew();
    private readonly List<double> _frameTimes = [];
    private readonly GameHud _hud = new();
    private readonly Dictionary<(Faction Faction, UnitKind Kind), MeshBatch> _batches = [];
    private readonly InstanceData[] _singleInstance = new InstanceData[1];

    private InstancedRenderer? _renderer;
    private ModelCatalog? _catalog;
    private RtsCamera? _camera;
    private SimBridge? _simulation;
    private ImGuiController? _imgui;

    private InstancedRenderer.Mesh? _terrainMesh;
    private ForestRenderer? _forest;
    private InstancedRenderer.Mesh? _selectionMarkerMesh;
    private InstancedRenderer.Mesh? _healthBackMesh;
    private InstancedRenderer.Mesh? _healthFillMesh;
    private SingleBatch? _healthBackBatch;
    private SingleBatch? _healthFillBatch;
    private InstancedRenderer.Mesh? _axisMesh;
    private InstancedRenderer.Mesh? _particleMesh;
    private InstancedRenderer.Mesh? _ringMesh;
    private InstancedRenderer.Mesh? _blastMesh;
    private InstancedRenderer.Mesh? _waterMesh;
    private InstancedRenderer.Mesh? _lavaMesh;
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

    /// <summary>
    /// The structure the player is choosing a site for, or <see cref="UnitKind.None"/>.
    /// <para>
    /// A structure used to be ordered from the panel and to arrive at a fixed offset from
    /// whatever made it, which is a position the player neither chose nor saw before paying
    /// for it. The kind is carried here rather than a flag, because a placement is a placement
    /// <em>of something</em>: the ghost is that building's model, the plan is that role's rule,
    /// and the order names it.
    /// </para>
    /// </summary>
    private UnitKind _pendingStructure = UnitKind.None;

    /// <summary>The ghost of the placement the player has not committed to yet.</summary>
    private PlacementPreview? _placementPreview;

    /// <summary>The ring that shows how far the selected structure reaches. Owns its own mesh.</summary>
    private PlacementPreview? _coveragePreview;

    /// <summary>Ring mesh for the selected structure, rebuilt only when the reach changes.</summary>
    private InstancedRenderer.Mesh? _coverageMesh;

    /// <summary>Slot the ring mesh was built for, so a selection change rebuilds it.</summary>
    private int _coverageSlot = -1;

    /// <summary>Radius the ring mesh was built for, so a radar coming back on rebuilds it.</summary>
    private int _coverageRadiusMm;

    /// <summary>
    /// Whether the last frame told the player about a dark radar, so the notice is raised on
    /// the change rather than on every frame. The HUD restarts its four-second timer on every
    /// call, so a notice raised per frame would sit on the screen for ever.
    /// </summary>
    private bool _radarNoticeShown;

    /// <summary>Draws the decks of the crossings that have been built.</summary>
    private BridgeRenderer? _bridges;

    /// <summary>Footprint mesh of the crossing currently being previewed; owned here.</summary>
    private InstancedRenderer.Mesh? _bridgePreviewMesh;

    /// <summary>Cells of that footprint, reused every frame rather than reallocated per frame.</summary>
    private readonly int[] _bridgePreviewCells = new int[SimWorld.MaxBridgeCells];

    /// <summary>Cell the current footprint mesh was built for, so it is rebuilt only on a move.</summary>
    private int _bridgePreviewCell = -1;

    /// <summary>Whether the site under the cursor would be accepted, which colours the ghost.</summary>
    private bool _bridgeSiteAllowed;

    /// <summary>Whether the structure under the cursor would be accepted, which colours its ghost.</summary>
    private bool _structureSiteAllowed;

    /// <summary>Why the structure site under the cursor would be refused, for the panel and the probe.</summary>
    private string _structureSiteReason = string.Empty;

    /// <summary>Why not, when it would not. Shown beside the armed button, where the player is looking.</summary>
    private string _bridgeSiteReason = string.Empty;

    /// <summary>Cells the crossing under the cursor would span, which is what its price is for.</summary>
    private int _bridgeSiteCells;

    /// <summary>
    /// Where the client believes the pointer is, when a script rather than a mouse is driving it.
    /// A probe has no mouse, and the preview and the click path both have to be askable about a
    /// point on the screen.
    /// </summary>
    private Vector2? _scriptedCursor;

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

    /// <summary>
    /// Where the turret and flight fixtures are looking. <see cref="RtsCamera"/>
    /// flattens its target to the ground plane on every update, and both of those
    /// fixtures look at something that is not on the ground — a pair of hulls, or a
    /// formation sixty metres up — so the aim is held here and re-applied after the
    /// camera has recomputed itself, which is what the viewer fixture does too.
    /// </summary>
    private Vector3? _fixtureAim;

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
        Window.Title = FullWindowTitle;
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
            // Looking along the line from behind and above, with the camera on the far
            // side of it: yaw zero puts the camera *ahead* of the line looking back, so
            // every trajectory comes at the viewer and a seventy-metre round is
            // foreshortened into a stub. Turned around, the line runs left to right and
            // every round recedes, which is the only angle at which its length shows.
            _camera.ZoomTo(120f);
            _camera.TiltTo(-0.62f);
            _camera.Yaw = MathHelper.Pi;
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
        else if (_options.TurretDemo)
        {
            // Side on and low. The line between the two tanks then runs across the
            // screen, so a gun that is off that line is off it by an angle the eye can
            // measure against the tracer beside it; a view from above flattens the gun
            // into the turret roof, which is where a turret is least legible.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 95f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.30f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0f;
        }
        else if (_options.FlightDemo)
        {
            // Steeply down from above, and fixed rather than following the flyers: a
            // swept wing only reads as an arrowhead from overhead, and an aircraft's
            // motion is read against the ground it is moving over, which a moving
            // camera would displace as well.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 130f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.95f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0f;
        }
        else if (_options.EmplacementDemo)
        {
            // From above and to one side. The claim being looked at is a building firing across
            // a hundred and fifty metres, and a frame that showed either end of that on its own
            // would show nothing — but the distance to hold is the claim's own hundred and fifty
            // metres and not more. Two hundred and sixty was chosen when the camera was aimed
            // forty-five metres east of the middle of the shot and needed the slack; at a hundred
            // and ninety the two ends still sit well inside the frame and a tank is a third larger
            // on screen, which is the difference between a machine and a speck.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 190f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.72f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0.55f;
        }
        else if (_options.DetectionDemo)
        {
            // Down the column the demonstration is laid along, tilted enough to see the
            // ground between the radar and the stalker: what is being looked at is where a
            // boundary falls, and a boundary is a distance rather than a thing.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 320f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.80f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0f;
            _camera.FocusOn(new Vector3(0f, 0f, -80f));
        }
        else if (_options.AllianceDemo)
        {
            // Along the line the three tanks stand on, from above: the claim is a distance
            // between two of them, so both the ally and the enemy beyond it have to be in the
            // frame with the machine that ignored one of them.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 220f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.78f);
            _camera.Yaw = _options.ScreenshotYaw ?? 1.5708f;
        }
        else if (_options.ArmourDemo)
        {
            // Across the three gun-and-headquarters pairs, from above and high enough out to hold
            // all six in one frame: the claim is a difference between three health bars, and a
            // frame showing one pair would show three numbers and no comparison.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 300f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.85f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0.35f;
        }
        else if (_options.MudDemo)
        {
            // Down the lanes, from above and to one side, past the far end of them: the claim is
            // how far three columns got in the same number of seconds, and that is a length along
            // the ground rather than a thing standing on it.
            _camera.ZoomTo(_options.ScreenshotZoom ?? 260f);
            _camera.TiltTo(_options.ScreenshotPitch ?? -0.88f);
            _camera.Yaw = _options.ScreenshotYaw ?? 0.25f;
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
                    : _options.FireDemo
                        ? SimBridge.CreateFiringRange(_options.Seed)
                        : _options.CombatDemo
                            ? SimBridge.CreateCombatDemo(_options.Seed)
                            : _options.TurretDemo
                                ? SimBridge.CreateTurretDemo(_options.Seed)
                                : _options.FlightDemo
                                    ? SimBridge.CreateFlightDemo(_options.Seed)
                                    : _options.EmplacementDemo
                                        ? SimBridge.CreateEmplacementDemo(_options.Seed)
                                        : _options.DetectionDemo
                                            ? SimBridge.CreateDetectionDemo(_options.Seed)
                                            : _options.AllianceDemo
                                                ? SimBridge.CreateAllianceDemo(_options.Seed)
                                                : _options.ArmourDemo
                                                    ? SimBridge.CreateArmourDemo(_options.Seed)
                                                    : _options.MudDemo
                                                        ? SimBridge.CreateMudDemo(_options.Seed)
                                                        : new SimBridge(_options.Seed, _options.IsModelGallery ? ScenarioKind.ModelGallery : _options.Match);

        _renderer = new InstancedRenderer(GraphicsDevice, Content);
        _catalog = new ModelCatalog(_renderer, AppContext.BaseDirectory);

        // The client meshes the simulation's own height field and surface layer, so
        // what is drawn is exactly what pathfinding reasons about.
        _terrainMesh = _renderer.CreateMesh(
            TerrainMeshBuilder.FromHeightMap(_simulation.World.Terrain, _simulation.World.TerrainTypes));
        _terrainRevision = _simulation.World.TerrainTypes.Revision;

        // Water and lava are their own surfaces, drawn over the terrain with animated
        // shaders. Built from the same layers the simulation uses, so the sea a player
        // sees is the sea the pathfinder refuses to walk into.
        BuildLiquidMeshes();

        // Trees stand on the woodland the surface layer already has: how many, where,
        // which shape and how big are all read from the cell lattice below, so they
        // are placed with the terrain and rebuilt with it.
        _forest = new ForestRenderer(_renderer, AppContext.BaseDirectory);
        _forest.Rebuild(_simulation.World.Terrain, _simulation.World.TerrainTypes);

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

        // The ghost of a placement the player has not committed to yet. It owns no mesh of
        // its own: the footprint is built from the cells the simulation says would be taken,
        // and rebuilt only when the site moves to another cell.
        _placementPreview = new PlacementPreview(GraphicsDevice, _renderer);

        // The coverage ring: how far the selected structure reaches, which the simulation
        // answers and this draws. A second preview rather than a share of the first, because a
        // placement is one ghost at a time and a selected gun's reach is not a placement at all
        // — the two can be on screen together the moment a player selects a gun and then arms
        // one, which is exactly when they are comparing the two.
        _coveragePreview = new PlacementPreview(GraphicsDevice, _renderer);

        // The decks of the crossings the simulation has built, drawn from its own record of
        // where each span runs and how much of it is up.
        _bridges = new BridgeRenderer(_renderer, AppContext.BaseDirectory);

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

        if (_options.LavaDemo)
        {
            FocusOnLava();
        }

        if (_options.ForestDemo)
        {
            FocusOnForest();
        }

        if (_options.TurretDemo)
        {
            FocusOnTurrets();
        }

        if (_options.FlightDemo)
        {
            FocusOnFlight();
        }

        if (_options.EmplacementDemo)
        {
            // The enemy line, named by team, with no offset. The claim is the shot a gun fires
            // across from the middle of the clearing to a tank a hundred and fifty metres east of
            // it, and the three machines to the east are the only entities in the claim: the
            // command centre that raises the gun stands two hundred metres behind the clearing and
            // used to drag a centroid taken over everything into the western half of the map, which
            // a hand-picked +75 m on the X axis then had to cancel. Measured, that left the frame
            // forty-five metres east of the middle of the shot; dropped, it leaves the gun's own
            // site ninety metres west of the middle of a frame that holds both ends of it.
            FocusOnClearing(Vector3.Zero, team: 2);
        }

        if (_options.ArmourDemo)
        {
            // Centred on the six of them: the claim is a difference between three buildings, and a
            // frame that cut one of the pairs off would show three numbers and no comparison.
            FocusOnClearing(Vector3.Zero);
        }

        if (_options.AllianceDemo)
        {
            // Centred on the five of them, which is the only thing this fixture can be framed on:
            // its subject stands on three teams — the machine that ignored the ally, the ally it
            // ignored and the enemy beyond both — so there is no team to name. The fixture used to
            // name none and aim at nothing, which left the camera on the middle of the map with the
            // whole line of three two hundred metres off to one side: measured, two of the five
            // machines were inside the viewport and both of them were from the control pair to the
            // south, which is a picture of the half of the demonstration that proves the least.
            FocusOnClearing(Vector3.Zero);
        }

        if (_options.MudDemo)
        {
            // The three columns only — the design bureau beside them unlocks the weather strike and
            // has nothing to do with the distance being measured — and framed forty metres up the
            // lanes, because the claim is a length along the ground rather than a thing on it.
            FocusOnClearing(new Vector3(0f, 0f, 40f), team: 3);
        }

        if (_options.GroundDemo)
        {
            FocusOnGround();
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

        if (!_options.NoAudio && !_options.IsSelfTest && _options.ScreenshotPath is null && !_options.IsProbe)
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

        // The probe is loaded last, because its first command may ask about any of the
        // above: the world, the models, the camera, the renderer.
        LoadProbe();

        base.LoadContent();
    }

    protected override void Update(GameTime gameTime)
    {
        // Raised before the probe's own return, because a probe reads it: `hud` reports the
        // notice currently on screen, and a notice the script could never trigger would be a
        // line of interface no test could reach. It reads the world and writes one string, so
        // running it on every frame including a script's own costs nothing.
        NotifyDimmedRadars();

        // A probe script owns the frame while it runs. It advances the simulation from its
        // own commands rather than from the wall clock, so the same script produces the same
        // transcript on a slow machine and a fast one — and the client's own per-frame work
        // is not run on the script's behalf either: `settle` is what asks for that, and
        // asking for it explicitly is what lets a script see the world between two frames.
        if (IsProbing)
        {
            // ImGui still needs the frame that its Render in Draw expects, even though a
            // probe never draws a panel.
            _imgui?.Update(gameTime);

            if (StepProbe())
            {
                Exit();
            }

            base.Update(gameTime);
            return;
        }

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

        // Escape backs out of a pending order before it does anything more drastic. The two had
        // been the same keystroke, so a player who armed a bridge and changed their mind either
        // left the game or had to place it somewhere to be rid of it — and the comment beside
        // the click path had claimed for some time that Escape cancelled the pending ability,
        // which it did not.
        if (Pressed(keyboard, Keys.Escape) && !_options.IsSelfTest)
        {
            if (_pendingBridge || _pendingAbility != AbilityId.None || _pendingStructure != UnitKind.None)
            {
                _pendingBridge = false;
                _pendingAbility = AbilityId.None;
                _pendingStructure = UnitKind.None;
                _hud.Notify("Ακυρώθηκε.");
            }
            else
            {
                Exit();
            }
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
        else if (_fixtureAim is { } aim)
        {
            // The turret and flight fixtures aim at something that is not on the
            // ground — a pair of hulls, or an aircraft sixty metres up — and the RTS
            // camera flattens its target to the ground plane on every update. Pinning
            // the aim here, after the camera has recomputed itself, is what the viewer
            // fixture does for the same reason.
            _camera!.LookAt(aim);
        }

        _selection.PruneDead(_simulation.World);
        UpdateOrderMarkers((float)gameTime.ElapsedGameTime.TotalSeconds);

        // Before the HUD is drawn, because the panel beside the armed bridge button reports the
        // same verdict the ghost is coloured by, and two answers on one frame must be one answer.
        VerifyPendingBridge();
        VerifyPendingStructure();
        UpdatePlacementPreview();

        // The cover the player's own defences are standing under. The notice is raised at the
        // top of the frame instead — a script has to be able to read it — and this is the half
        // that has to happen before the HUD is drawn.
        UpdateCoverageRing();

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

        // The gallery, the viewer and the effect fixtures are inspection tools, not a
        // game: a HUD over a contact sheet hides half the models, and the victory
        // banner that a team with no opposition triggers covers the rest.
        HudCommand? command = _options.IsFixture ? null : _hud.Draw(BuildSnapshot());

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
            FogEnd: 2000f,
            // The animated surfaces read this. Frame time, not simulation time: a
            // shimmering sea must never be able to reach a tick.
            Time: (float)_totalSeconds);

        _renderer!.Begin(view, projection, _camera.Position, environment);

        _drawCalls = 0;
        _instancesSubmitted = 0;

        // The ground is drawn with its own technique: the shader classifies each
        // pixel's vertex colour against the surface palette and gives grass, mud,
        // sand, snow, rock and ore their own treatment. It goes back to the lit
        // technique immediately, because a unit drawn with it would be treated as
        // whatever surface its hull resembles.
        _renderer.BeginTerrain();
        DrawSingle(_terrainMesh!, Matrix.Identity, Color.White);
        _renderer.EndTerrain();

        // Immediately over the ground they lie on, and under everything else: water
        // drawn after the units would put a lake in front of the tanks standing in it.
        DrawLiquids();

        // The decks over that water, one block per cell of every crossing. Under the fog like
        // every other piece of the world: a bridge is a thing on the map, not the interface.
        if (_bridges is not null && _simulation is not null)
        {
            int decks = _bridges.Collect(_simulation.World);

            if (decks > 0)
            {
                _drawCalls += _bridges.Draw();
                _instancesSubmitted += decks;
            }
        }

        // Trees are scenery on walkable ground, so they go here: over the surfaces
        // they stand on, and under the units that drive through them. The model
        // fixtures skip them — they frame one vehicle at a time on a plain grid, and
        // a wood in the background of a model shot is a wood in the way.
        if (!_options.IsModelGallery && !_options.Viewer)
        {
            (int treeCalls, int treeInstances) = _forest?.Draw(_camera!.IsStrategicZoom) ?? (0, 0);

            _drawCalls += treeCalls;
            _instancesSubmitted += treeInstances;
        }

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
                _renderer.BeginParticles(InstancedRenderer.ParticleBlend.Additive, InstancedRenderer.ParticlePass.Blast);
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

        if (_fog is not null && DrawsFogOfWar && !_options.IsModelGallery)
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

        // The placement ghost goes over the fog, not under it, which is the opposite of every
        // other thing on the ground. It is not part of the world: it is the interface pointing
        // at a place, and what it is for is being read while the player decides. Under the fog
        // — where it sat first — a site on remembered ground was dimmed to the point of being
        // invisible, which is the same nothing-happened silence the ghost exists to end. It
        // gives away nothing either: whether a site would be accepted depends on the water
        // under it and on the player's own resources, never on anything hidden.
        if (_placementPreview?.Draw() == true)
        {
            _drawCalls++;
        }

        if (_coveragePreview?.Draw() == true)
        {
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

            if (DrawsFogOfWar &&
                entity.TeamId != PlayerTeam &&
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
            if (DrawsFogOfWar &&
                entity.TeamId != PlayerTeam &&
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
            Matrix transform = EntityTransform(position, heading, build);

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
                batch.Locals[i] = AnimatePart(batch.Parts[i], ref entity, world, slot);
            }

            for (int i = 0; i < batch.Parts.Length; i++)
            {
                PartBatch part = batch.Parts[i];

                if (part.Count >= part.Instances.Length)
                {
                    continue;
                }

                part.Instances[part.Count++] = new InstanceData(
                    PartWorldTransform(batch, i, transform),
                    tint);
            }
        }
    }

    /// <summary>
    /// Where and how an entity's model sits in the world: the hull's facing, its position,
    /// and the squash a half-built structure carries.
    /// <para>
    /// Factored out of the submission loop so the probe can ask for it rather than build its
    /// own: a probe that composed the entity transform itself would go on answering for a
    /// world the renderer had stopped drawing the moment this changed.
    /// </para>
    /// </summary>
    private static Matrix EntityTransform(Vector3 position, float heading, float build)
    {
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

        return transform;
    }

    /// <summary>
    /// Where one part of one entity ends up: its animated transform, its parents' animated
    /// transforms, the model's own alignment, and the hull's place in the world.
    /// <para>
    /// One function rather than two copies, for the same reason as the entity transform
    /// above: <c>parts</c> answers "where is this part, and which way does it point" with
    /// this, and a second copy of the chain would let the probe report a transform that is
    /// not the one being drawn.
    /// </para>
    /// </summary>
    private static Matrix PartWorldTransform(MeshBatch batch, int index, Matrix entityTransform)
    {
        Matrix local = batch.Locals[index];
        int parent = batch.Parts[index].ParentIndex;

        while (parent >= 0 && parent < batch.Locals.Length)
        {
            local *= batch.Locals[parent];
            parent = batch.Parts[parent].ParentIndex;
        }

        return local * batch.ModelTransform * entityTransform;
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
    private Matrix AnimatePart(PartBatch part, ref Entity entity, SimWorld world, int slot)
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
            //
            // About Y, which is vertical, and not about Z. Everything on this contract
            // is built to turn around Blender's up axis — a dish on a mast, a drone
            // rotor as a horizontal disc, a harvester's beacon — and Z is not up in the
            // world the game draws in. Turning about it swung the dish through the
            // vertical plane instead: it climbed over the tower, went through the roof
            // and came back out, which is exactly what it looked like from a distance.
            //
            // <b>And a dish with no power behind it stops.</b> A Σταθμός Ραντάρ draws its
            // generation continuously and the grid sheds it before anything else, so the
            // one building whose whole purpose is the sweep is also the one that loses it.
            // Stopping rather than slowing: a dish crawling round reads as a slow radar
            // rather than as a dead one, and a player who has to ask whether it is moving
            // has been given no signal at all. It stops where it stood, which is a pose a
            // still frame can be read from — see tools/probe/detection.probe, which samples
            // this part twice and compares.
            //
            // Only structures pay it. The same part name is on a drone's four rotors and on
            // a harvester's beacon, and neither of them is plugged into a base: stopping the
            // rotors of an aircraft because a power plant elsewhere on the map was bombed
            // would be a bug a player would report as one.
            float sweep = IsRadarDark(in entity, world, slot) ? 0f : world.Tick * 0.02f;
            return Matrix.CreateRotationY(sweep) * part.LocalTransform;
        }
        else if (IsLimb(name))
        {
            return SwingLimb(part, name, ref entity);
        }

        return part.LocalTransform;
    }

    /// <summary>
    /// True when this entity is a radar station whose dish is not turning: the team's
    /// generation could not cover it and the grid shed it.
    /// <para>
    /// It asks the simulation rather than working it out, which is the point of asking at all.
    /// The client has no opinion about power: <see cref="SimWorld.IsRadarLit"/> is the same
    /// answer the guns get, so a dish that has stopped and a gun that has lost its reach are
    /// always the same event. The alternative — re-deriving a brown-out in the renderer from
    /// the energy stockpile — is how a picture comes to disagree with the game it is drawing.
    /// </para>
    /// <para>
    /// It is deliberately only the radar station. A <c>radar*</c> part is also a factory's
    /// extractor fan, a power plant's cooling fan and a drone's four rotors, and none of those
    /// is a sensor: stopping a hall's ventilation because the grid is short would say
    /// "production has stopped", which is a rule this game does not have and a later brown-out
    /// step at that.
    /// </para>
    /// </summary>
    private static bool IsRadarDark(in Entity entity, SimWorld world, int slot)
        => entity.Kind == UnitKind.RadarStation &&
           entity.ConstructionTicksRemaining <= 0 &&
           !world.IsRadarLit(slot);

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
        // Keyed by the pair itself rather than by an integer packed out of them. The
        // packed key was `faction * 8 + kind`, where eight was the number of unit kinds
        // when it was written; there are more than twice that now, so keys ran together
        // and a batch built for one entity was handed to another. The first one drawn
        // won, and everything that collided wore its model — which is how a Δυτικοί
        // Συλλέκτης came to be drawn as a Κινέζοι command centre, and a Δυτικοί tank as
        // a Κινέζοι design bureau driving around like a vehicle.
        //
        // A tuple cannot collide, whatever the enums grow to. The previous fix was one
        // multiplier away from the same bug.
        var key = (faction, kind);

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
            if (_pendingStructure != UnitKind.None)
            {
                if (Vector2.Distance(_dragStart, end) < 6f)
                {
                    IssueStructureAtCursor(end);
                }
                else
                {
                    _pendingStructure = UnitKind.None;
                }
            }
            else if (_pendingBridge)
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
    /// <para>
    /// It marches the surface the player can <em>see</em>, which is not the height
    /// field over water: a lake is drawn on the water line, and the bed beneath it is
    /// as much as three and a half metres lower. Marched against the bed, a ray aimed
    /// at the water carries on past the surface it visibly hit and comes to rest
    /// further away — and at a shoreline that is far enough to land on the bank
    /// instead of the water, so the click arrives as "no water here" on ground the
    /// player did not point at. Every order that targets water was misplaced by up to
    /// a quarter of a cell because of it, which is exactly the size of error that only
    /// shows up at the one place it matters: the edge of the water, where a bridge
    /// gets built.
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

        SimWorld world = _simulation.World;
        TerrainLayer surfaces = world.TerrainTypes;
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
            $"gap {near.Y - DrawnHeightAtMetres(world, surfaces, near.X, near.Z):0.0}";

        // A ray pointing at the sky never meets the ground: reject it rather than
        // inventing a destination behind the camera.
        if (direction.Y > -0.0001f)
        {
            return false;
        }

        float maxDistance = Math.Min(length, 4000f);
        Vector3 previous = near;
        float previousGap = near.Y - DrawnHeightAtMetres(world, surfaces, near.X, near.Z);

        for (float travelled = Step; travelled <= maxDistance; travelled += Step)
        {
            Vector3 point = near + (direction * travelled);
            float gap = point.Y - DrawnHeightAtMetres(world, surfaces, point.X, point.Z);

            if (gap <= 0f)
            {
                // Refine the crossing by bisection: the step is 2 m, which would
                // otherwise show up as a visible offset when ordering units.
                Vector3 low = previous;
                Vector3 high = point;

                for (int i = 0; i < 12; i++)
                {
                    Vector3 mid = (low + high) * 0.5f;

                    if (mid.Y - DrawnHeightAtMetres(world, surfaces, mid.X, mid.Z) <= 0f)
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

    /// <summary>
    /// Height of the surface the renderer draws at a world position, in metres.
    /// <para>
    /// The same rule <see cref="TerrainMeshBuilder"/> builds both the ground mesh and the
    /// liquid quads with: water is drawn on the water line rather than on the bed it fills, and
    /// everything else is drawn on the height field. Sharing the rule is the point — a click
    /// has to land on the surface the player aimed at, and the only authority on where that
    /// surface is is the code that draws it.
    /// </para>
    /// </summary>
    private static float DrawnHeightAtMetres(SimWorld world, TerrainLayer surfaces, float x, float z)
    {
        int worldX = (int)(x * WorldPos.MmPerMetre);
        int worldZ = (int)(z * WorldPos.MmPerMetre);

        return DrawnHeightMm(
            world,
            surfaces,
            surfaces.IndexOfWorld(worldX, worldZ),
            new WorldPos(worldX, 0, worldZ)) / (float)WorldPos.MmPerMetre;
    }

    /// <summary>
    /// Height the renderer draws at, in millimetres, for a cell and a position in it: water on
    /// the water line, because a lake is drawn as a surface rather than as the basin it fills,
    /// and everything else on the height field.
    /// </summary>
    private static int DrawnHeightMm(SimWorld world, TerrainLayer surfaces, int cell, WorldPos position)
        => cell >= 0 && surfaces.TypeAt(cell) is TerrainType.ShallowWater or TerrainType.DeepWater
            ? surfaces.WaterLevelMm
            : world.Terrain.SampleHeightMm(position.X, position.Z);

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
            if (picked.TeamId != PlayerTeam && !_simulation.World.AreAllied(picked.TeamId, PlayerTeam))
            {
                IssueAttackOrders(targetSlot);
                return;
            }
        }

        IssueMoveOrder(cursor);
    }

    /// <summary>
    /// Spans the water under the cursor. The simulation decides whether the site is
    /// any good — not water, too wide, longer than any span, more than the team can afford — and
    /// refuses without charging, so a misplaced click costs nothing but the click.
    /// <para>
    /// The site is <em>asked about</em> here before the order is sent, with the same call the
    /// order will be answered by, for two reasons. The player is told why in the same frame
    /// rather than a tick later, and a refusal that the player can act on — move the cursor a
    /// cell and try again — is worth more than a refusal discovered after the fact. The answer
    /// cannot drift from the command's because it is the same function on the same world.
    /// </para>
    /// <para>
    /// A refused click leaves the placement armed. The player is in the middle of choosing a
    /// site, has just been told what is wrong with the one they chose, and the next thing they
    /// will do is point somewhere else; disarming there would make them press the button again
    /// for every cell they try. Escape, or a site that is accepted, ends the mode.
    /// </para>
    /// </summary>
    private void IssueBridgeAtCursor(Vector2 cursor)
    {
        if (IsPlayback || _simulation is null)
        {
            _pendingBridge = false;
            return;
        }

        WorldPos target;

        if (!TryPlacementTarget(cursor, out target))
        {
            // The ray never met the ground: the player is pointing at the sky or past the edge
            // of the map, which is a refusal like any other and is now said rather than done.
            _hud.Notify("Γέφυρα: ο δείκτης δεν δείχνει έδαφος.");
            _bridgeAwaitingTick = -1;
            return;
        }

        SimWorld world = _simulation.World;

        if (!world.CanBuildBridge(PlayerTeam, target, out string reason))
        {
            _hud.Notify($"Γέφυρα: {reason}.");
            _bridgeAwaitingTick = -1;
            return;
        }

        _pendingBridge = false;
        world.Enqueue(SimCommand.Bridge(target, world.Tick + 1, PlayerTeam));

        // The command lands on the next tick, and the world can move in between. Remembering
        // the cell it was asked for is what lets the arrival be checked rather than assumed:
        // a bridge that never appeared is otherwise the same silence this path was fixed for.
        _bridgeAwaitingCell = world.TerrainTypes.IndexOfWorld(target.X, target.Z);
        _bridgeAwaitingTick = world.Tick + 1;
    }

    /// <summary>
    /// Checks on the tick after a bridge was ordered that the crossing actually exists, and
    /// says why not when it does not. A site that was legal at the click and refused one tick
    /// later is rare, but it is the same silent failure by a different route.
    /// </summary>
    private void VerifyPendingBridge()
    {
        if (_bridgeAwaitingTick < 0 || _simulation is null || _simulation.World.Tick < _bridgeAwaitingTick)
        {
            return;
        }

        int cell = _bridgeAwaitingCell;
        SimWorld world = _simulation.World;
        _bridgeAwaitingTick = -1;
        _bridgeAwaitingCell = -1;

        if (cell < 0 || world.TerrainTypes.TypeAt(cell) == TerrainType.ShallowWater)
        {
            return;
        }

        // The site was legal when it was asked for and is not now: the resources went, or the
        // ground under it changed. Asking again reports whichever of those it was.
        WorldPos target = world.Navigation.CentreOf(cell);

        _hud.Notify(world.CanBuildBridge(PlayerTeam, target, out string reason)
            ? "Γέφυρα: η εντολή δεν εκτελέστηκε."
            : $"Γέφυρα: {reason}.");
    }

    /// <summary>Cell the last bridge order is waiting to see built, or -1.</summary>
    private int _bridgeAwaitingCell = -1;

    /// <summary>Tick that order was asked to execute on, or -1.</summary>
    private long _bridgeAwaitingTick = -1;

    /// <summary>
    /// Raises the armed structure at the site under the cursor. The simulation decides whether
    /// the ground will take it — water, lava, a cliff, a patch too rough for a building, or a
    /// team that cannot pay — and refuses without charging, so a misplaced click costs nothing
    /// but the click.
    /// <para>
    /// The site is <em>asked about</em> here before the order is sent, with the same call the
    /// order will be answered by, for two reasons. The player is told why in the same frame
    /// rather than a tick later, and a refusal they can act on — move the cursor a cell and try
    /// again — is worth more than one discovered after the fact. The answer cannot drift from
    /// the command's because it is the same function on the same world, and what is enqueued is
    /// the position the plan named rather than the millimetre the cursor resolved to.
    /// </para>
    /// <para>
    /// A refused click leaves placement armed, exactly as the bridge's does: the player is in
    /// the middle of choosing a site, has just been told what is wrong with the one they chose,
    /// and the next thing they will do is point somewhere else.
    /// </para>
    /// </summary>
    private void IssueStructureAtCursor(Vector2 cursor)
    {
        if (IsPlayback || _simulation is null)
        {
            _pendingStructure = UnitKind.None;
            return;
        }

        UnitKind kind = _pendingStructure;
        string name = FactionPalette.UnitLabel(kind);

        if (!TryPlacementTarget(cursor, out WorldPos target))
        {
            _hud.Notify($"{name}: ο δείκτης δεν δείχνει έδαφος.");
            return;
        }

        SimWorld world = _simulation.World;

        if (!world.TryPlanStructure(PlayerTeam, kind, target, out WorldPos planned, out string reason))
        {
            _hud.Notify($"{name}: {reason}.");
            return;
        }

        _pendingStructure = UnitKind.None;
        world.Enqueue(SimCommand.Structure(kind, planned, world.Tick + 1, PlayerTeam));

        // The command lands on the next tick, and the world can move in between. Remembering what
        // was asked for is what lets the arrival be checked rather than assumed: a building that
        // never appeared is otherwise the same silence this whole path was fixed for, and the
        // bridge carries the identical note beside it.
        _structureAwaitingKind = kind;
        _structureAwaitingCell = world.Navigation.IndexOfWorld(planned);
        _structureAwaitingTick = world.Tick + 1;
    }

    /// <summary>The structure order waiting to be seen standing, or <see cref="UnitKind.None"/>.</summary>
    private UnitKind _structureAwaitingKind = UnitKind.None;

    /// <summary>The cell that order named, and the tick it was asked to execute on.</summary>
    private int _structureAwaitingCell = -1;
    private long _structureAwaitingTick = -1;

    /// <summary>
    /// Checks on the tick after a structure was ordered that it is actually standing there, and
    /// says why not when it is not. A site that was legal at the click and refused one tick later
    /// is rare — the resources moved, or the team's last command centre was lost — but it is the
    /// same silent failure by a different route, and a player who has just paid for a building is
    /// entitled to hear about it.
    /// </summary>
    private void VerifyPendingStructure()
    {
        if (_structureAwaitingKind == UnitKind.None || _simulation is null ||
            _simulation.World.Tick < _structureAwaitingTick)
        {
            return;
        }

        UnitKind kind = _structureAwaitingKind;
        int cell = _structureAwaitingCell;
        SimWorld world = _simulation.World;

        _structureAwaitingKind = UnitKind.None;
        _structureAwaitingCell = -1;
        _structureAwaitingTick = -1;

        if (cell < 0 || IsStructureAt(world, PlayerTeam, kind, cell))
        {
            return;
        }

        // Asking again reports whichever of the rules refused it this time; a plan that would now
        // accept leaves only one thing unsaid, and that is that the order never ran.
        _hud.Notify(world.TryPlanStructure(PlayerTeam, kind, world.Navigation.CentreOf(cell), out _, out string reason)
            ? $"{FactionPalette.UnitLabel(kind)}: η εντολή δεν εκτελέστηκε."
            : $"{FactionPalette.UnitLabel(kind)}: {reason}.");
    }

    /// <summary>True when a live structure of a role for a team stands on a cell.</summary>
    private static bool IsStructureAt(SimWorld world, int team, UnitKind kind, int cell)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team || entity.Kind != kind)
            {
                continue;
            }

            if (world.Navigation.IndexOfWorld(entity.Position) == cell)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// Where on the ground the player is pointing, in simulation millimetres. Between the
    /// cursor and the simulation sits the client's projection, and this is the one place it is
    /// converted, so a preview and the order it previews cannot be aimed at different cells.
    /// </summary>
    private bool TryPlacementTarget(Vector2 cursor, out WorldPos target)
    {
        target = default;

        if (!TryScreenToGround(cursor, out Vector3 ground))
        {
            return false;
        }

        target = WorldPos.FromMetres((int)ground.X, 0, (int)ground.Z);
        return true;
    }

    /// <summary>
    /// Where the pointer is. A probe script sets this, because it has no mouse; otherwise it is
    /// ImGui's own idea of the pointer, which is the one the panels are drawn against.
    /// </summary>
    private Vector2 CursorPosition => _scriptedCursor ?? new Vector2(_imgui?.MousePosition.X ?? 0f, _imgui?.MousePosition.Y ?? 0f);

    /// <summary>
    /// Stages the ghost of the crossing the player is about to place: the cells the simulation
    /// says a span here would turn into ford, tinted by whether it would take them.
    /// <para>
    /// Presentation only. Nothing here enqueues, spends or edits anything — the simulation is
    /// asked a question and the answer is drawn, which is what lets the whole preview be as
    /// bold as it likes without a mis-click being able to change the world. The mesh is rebuilt
    /// only when the site moves to another cell, because the footprint is a function of that
    /// cell and re-uploading it per frame would allocate on the render path for nothing.
    /// </para>
    /// </summary>
    private void UpdatePlacementPreview()
    {
        if (_placementPreview is null || _simulation is null)
        {
            return;
        }

        // One ghost at a time, and the structure's is the newer of the two modes: arming a row
        // clears the other two, so both being armed at once is not a state this can be in.
        if (_pendingStructure != UnitKind.None)
        {
            UpdateStructurePreview();
            return;
        }

        if (!_pendingBridge || IsPlayback)
        {
            _placementPreview.Hide();
            _bridgeSiteAllowed = false;
            _bridgeSiteReason = string.Empty;
            _structureSiteAllowed = false;
            _structureSiteReason = string.Empty;
            return;
        }

        SimWorld world = _simulation.World;

        if (!TryPlacementTarget(CursorPosition, out WorldPos target))
        {
            // The cursor is not over the ground at all, so there is nothing to diagram and
            // nothing to promise.
            _placementPreview.Hide();
            _bridgeSiteAllowed = false;
            _bridgeSiteReason = "ο δείκτης δεν δείχνει έδαφος";
            _bridgePreviewCell = -1;
            return;
        }

        int site = world.TerrainTypes.IndexOfWorld(target.X, target.Z);

        _bridgeSiteAllowed = world.TryPlanBridge(PlayerTeam, target, _bridgePreviewCells, out int count, out _bridgeSiteReason);
        _bridgeSiteCells = site >= 0 ? count : 0;

        if (site != _bridgePreviewCell || _bridgePreviewMesh is null)
        {
            // A refused site still gets a footprint, of the one cell under the cursor: a red
            // patch on the cell the player is pointing at is the clearest possible statement
            // that it is that cell which is wrong, and a site refused because it is dry land
            // has no span at all to draw.
            if (!_bridgeSiteAllowed || count == 0)
            {
                _bridgePreviewCells[0] = site >= 0 ? site : 0;
                count = site >= 0 ? 1 : 0;
            }

            _bridgePreviewMesh?.Dispose();
            _bridgePreviewMesh = count == 0
                ? null
                : _renderer!.CreateMesh(PlacementPreview.Footprint(
                    world.Navigation,
                    _bridgePreviewCells.AsSpan(0, count),
                    cell => DrawnHeightMm(world, world.TerrainTypes, cell, world.Navigation.CentreOf(cell))));

            _bridgePreviewCell = site;
        }

        if (_bridgePreviewMesh is null)
        {
            _placementPreview.Hide();
            return;
        }

        _placementPreview.Show(_bridgePreviewMesh, Matrix.Identity, _bridgeSiteAllowed);
    }

    /// <summary>
    /// Stages the ghost of the structure the player is about to place: the building itself,
    /// translucent, standing on the site the simulation says it would take — tinted by whether
    /// it would take it.
    /// <para>
    /// The ghost is the model rather than a footprint because that is the question the player is
    /// asking. A rectangle of ground says which cells are being spent; the building says what is
    /// being built, how big it is and which way round it lands, which is what choosing a site
    /// between two hills is actually about. Colour is the verdict — green when the plan accepts,
    /// red when it does not — and the reason is written beside the armed row and in the notice a
    /// refused click raises.
    /// </para>
    /// <para>
    /// Presentation only. Nothing here enqueues, spends or edits anything — the simulation is
    /// asked a question and the answer is drawn, which is what lets the ghost be as bold as it
    /// likes without a mis-click being able to change the world. The mesh is the catalogue's and
    /// is cached there; only the transform changes from frame to frame.
    /// </para>
    /// <para>
    /// A refused site still gets a ghost, standing on the cell under the cursor. Water, lava and
    /// rough ground are exactly the sites a player needs to see refused — a red building over
    /// the lake says the lake is the problem, and a preview that vanished there would be the
    /// silence this whole path exists to end.
    /// </para>
    /// </summary>
    private void UpdateStructurePreview()
    {
        SimWorld world = _simulation!.World;

        if (IsPlayback)
        {
            _placementPreview!.Hide();
            _structureSiteAllowed = false;
            _structureSiteReason = string.Empty;
            return;
        }

        if (!TryPlacementTarget(CursorPosition, out WorldPos target))
        {
            // The cursor is not over the ground at all, so there is nothing to stand a ghost on
            // and nothing to promise.
            _placementPreview!.Hide();
            _structureSiteAllowed = false;
            _structureSiteReason = "ο δείκτης δεν δείχνει έδαφος";
            return;
        }

        _structureSiteAllowed = world.TryPlanStructure(
            PlayerTeam,
            _pendingStructure,
            target,
            out WorldPos planned,
            out _structureSiteReason);

        // Where it would stand: the cell the plan chose, or the cell under the cursor when the
        // plan refused it, so that the red building is over the ground that is wrong.
        WorldPos stand = _structureSiteAllowed
            ? planned
            : world.Navigation.CentreOf(world.Navigation.IndexOfWorld(target));

        float x = stand.X / (float)WorldPos.MmPerMetre;
        float z = stand.Z / (float)WorldPos.MmPerMetre;
        float y = DrawnHeightAtMetres(world, world.TerrainTypes, x, z) + PlacementPreview.LiftMetres;

        InstancedRenderer.Mesh ghost = _catalog!.Whole(world.FactionOfTeam(PlayerTeam), _pendingStructure);

        // No rotation: a structure is raised facing its model's own forward, which is what the
        // entity transform does with a heading of zero.
        _placementPreview!.Show(ghost, Matrix.CreateTranslation(x, y, z), _structureSiteAllowed, StructureGhostOpacity);
    }

    /// <summary>
    /// How solid the ghost of a building is drawn.
    /// <para>
    /// Higher than the footprint's own opacity because a building is a volume rather than a
    /// diagram: its near wall, its far wall and its roof all lie between the eye and the ground, so
    /// the tint is diluted three times over and a red refusal at the footprint's four tenths reads
    /// as a pale pink blur — which is indistinguishable from a preview that has not made up its
    /// mind. Two thirds still shows the ground through the building and still says red.
    /// </para>
    /// </summary>
    private const float StructureGhostOpacity = 0.66f;

    /// <summary>
    /// Draws the reach of the single selected structure on the ground.
    /// <para>
    /// <b>A radius a player cannot see is a radius a player cannot use.</b> Detection is not
    /// firing range in this game, a radar is what turns one into the other, and the whole
    /// decision a Σταθμός Ραντάρ creates — put the guns under the umbrella — is invisible
    /// without a ring on the ground. So a selected gun draws the distance it can actually
    /// engage at, and a selected radar draws the ground it lights. Select a gun, watch its ring
    /// grow when a radar is lit beside it and shrink when the grid sheds it, and the mechanic
    /// has been taught without a word of explanation.
    /// </para>
    /// <para>
    /// The number comes from the simulation — <see cref="CombatSystem.EngagementRadiusMm"/> and
    /// <see cref="VisionSystem.RadarCoverageMm"/> — so the ring and the gun cannot disagree.
    /// A dark radar draws nothing at all, because its coverage is zero and a ring would be a
    /// promise the guns cannot keep; the notice in the HUD says why it went.
    /// </para>
    /// </summary>
    private void UpdateCoverageRing()
    {
        if (_coveragePreview is null || _simulation is null || IsPlayback)
        {
            _coveragePreview?.Hide();
            return;
        }

        SimWorld world = _simulation.World;
        int slot = SelectedCoverageSlot();

        if (slot < 0)
        {
            _coveragePreview.Hide();
            _coverageSlot = -1;
            return;
        }

        ref Entity entity = ref world.GetRefBySlot(slot);

        int radiusMm = entity.Kind == UnitKind.RadarStation
            ? (world.IsRadarLit(slot) ? VisionSystem.RadarCoverageMm : 0)
            : CombatSystem.EngagementRadiusMm(world, slot);

        if (radiusMm <= 0)
        {
            _coveragePreview.Hide();
            _coverageSlot = slot;
            _coverageRadiusMm = 0;
            return;
        }

        if (slot != _coverageSlot || radiusMm != _coverageRadiusMm || _coverageMesh is null)
        {
            _coverageMesh?.Dispose();
            _coverageMesh = _renderer!.CreateMesh(PlacementPreview.CoverageRing(
                world.Navigation,
                entity.Position,
                radiusMm,
                CoverageRingBandMm,
                cell => DrawnHeightMm(world, world.TerrainTypes, cell, world.Navigation.CentreOf(cell))));

            _coverageSlot = slot;
            _coverageRadiusMm = radiusMm;
        }

        // Identity, like the bridge footprint: the ring's vertices are already where they go.
        _coveragePreview.Show(_coverageMesh, Matrix.Identity, CoverageTint);
    }

    /// <summary>
    /// The slot whose reach should be drawn: a structure the player could aim at something
    /// with, or a radar station — whose reach is everybody else's.
    /// <para>
    /// It deliberately does not go through <see cref="SelectedBuildingSlot"/>, which answers a
    /// different question and filters through a narrower client-side idea of what a building is.
    /// Every role here is one a player has to place rather than order about, and one this ring
    /// has something to say about.
    /// </para>
    /// </summary>
    private int SelectedCoverageSlot()
    {
        if (_selection.Count != 1 || _simulation is null)
        {
            return -1;
        }

        if (!_simulation.World.TryGetRef(_selection.Selected[0], out _, out int slot))
        {
            return -1;
        }

        ref Entity entity = ref _simulation.World.GetRefBySlot(slot);
        UnitDefinition definition = UnitCatalog.Get(entity.Kind);

        // A radar, whose reach is everybody else's, or a structure with a gun on it, whose
        // reach is the thing the radar changes. Not every unit: a hull carries its range with
        // it and a ring under the whole selection would be a map covered in circles.
        bool interesting = entity.Kind == UnitKind.RadarStation || (definition.IsArmed && definition.IsBuilding);

        return interesting ? slot : -1;
    }

    /// <summary>
    /// How thick the coverage band is: about three navigation cells, which is 28 m of ground.
    /// One cell is a dotted suggestion from the height this game is played at, and a band wide
    /// enough to be seen without squinting stops reading as a line and starts reading as a
    /// filled disc with a hole in it.
    /// </summary>
    private const int CoverageRingBandMm = 28_000;

    /// <summary>
    /// The coverage ring's colour: a bright cold blue at more than half opacity, and neither of
    /// the two the placement ghost uses. Green means a site would be accepted and red that it
    /// would not, and a ring that borrowed either would read as a verdict about something the
    /// player is not placing.
    /// <para>
    /// Bright and opaque because the ghost pass is fogged like everything else on the ground,
    /// and remembered ground washes a thin colour out to the colour of the fog — which on this
    /// map is a warm haze, so a faint blue ring arrives as a brown one. This is the ring that
    /// matters most at the edge of what a player knows, so it has to survive the walk.
    /// </para>
    /// </summary>
    private static readonly Vector4 CoverageTint = new(0.35f, 0.95f, 1f, 0.55f);

    /// <summary>
    /// The Greek line the interface shows when the grid cannot run every radar a team has
    /// built, raised once per change.
    /// <para>
    /// Once per change and not once per frame: <see cref="GameHud.Notify"/> restarts its
    /// four-second timer every time it is called, so a notice raised on every frame of a
    /// brown-out would never leave the screen. The player is told when it happens, which is
    /// what a notice is for; the dish that has stopped is what tells them it is still true.
    /// </para>
    /// </summary>
    private void NotifyDimmedRadars()
    {
        if (_simulation is null)
        {
            return;
        }

        string reason = PowerSystem.DimmedReason(_simulation.World.Team(PlayerTeam));

        if (reason.Length == 0)
        {
            _radarNoticeShown = false;
            return;
        }

        if (_radarNoticeShown)
        {
            return;
        }

        _radarNoticeShown = true;
        _hud.Notify($"{FactionPalette.UnitLabel(UnitKind.RadarStation)}: {reason}.");
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

        // The liquid surfaces move with the ground under them: a bridge turns water
        // into road, and mud control does not, but a volcano's lava field does.
        BuildLiquidMeshes();

        // The woodland moves with the ground too, and for the same reason: a wood is
        // a property of the cells, and weather control can turn one into a bog.
        _forest?.Rebuild(world.Terrain, world.TerrainTypes);
    }

    /// <summary>
    /// Draws the water and lava surfaces with their animated shaders.
    /// </summary>
    private void DrawLiquids()
    {
        if (_renderer is null)
        {
            return;
        }

        if (_waterMesh is not null)
        {
            _renderer.BeginLiquids(InstancedRenderer.LiquidPass.Water);
            DrawSingle(_waterMesh, Matrix.Identity, Color.White);
        }

        // Lava is deliberately not drawn as a surface. It sits on the ground it flowed
        // down, and a flat quad sampled at its cell's centre height is buried by any
        // gradient of more than a few centimetres across nine metres — which is every
        // slope a flow has run down. The Terrain technique shades it instead, on the mesh
        // that follows the slope, which is also why the terrain shader had to learn a
        // lava treatment rather than leave it to this mesh.
        if (_lavaMesh is not null)
        {
            _lavaMesh.Dispose();
            _lavaMesh = null;
        }

        if (_waterMesh is not null || _lavaMesh is not null)
        {
            // Back to the lit technique for everything drawn after this.
            _renderer.EndParticles();
        }
    }

    /// <summary>
    /// Rebuilds the water and lava surfaces from the simulation's own layers. Cheap
    /// enough to redo whenever the terrain mesh is redone, and it must be redone with
    /// it: they are the same ground.
    /// </summary>
    private void BuildLiquidMeshes()
    {
        if (_renderer is null || _simulation is null)
        {
            return;
        }

        SimWorld world = _simulation.World;

        (MeshData water, MeshData lava) = TerrainMeshBuilder.BuildLiquids(world.Terrain, world.TerrainTypes);

        _waterMesh?.Dispose();
        _lavaMesh?.Dispose();

        _waterMesh = water.PrimitiveCount > 0 ? _renderer.CreateMesh(water) : null;
        _lavaMesh = lava.PrimitiveCount > 0 ? _renderer.CreateMesh(lava) : null;
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

    /// <summary>
    /// Turns a HUD button press into a simulation command.
    /// <para>
    /// What needs a selected building is looked up inside the case that needs it, rather than
    /// once at the top for every case. Demanding a selection up front meant the two buttons
    /// that do not belong to a building — the off-map abilities and the bridge, both of which
    /// are drawn in the support panel whenever the player has a factory — did nothing at all
    /// unless a structure happened to be selected. The button lit up under the cursor and then
    /// silently did nothing, which is the report this path was fixed for: a player is entitled
    /// to assume that a button that looks pressable is one.
    /// </para>
    /// </summary>
    private void ApplyHudCommand(HudCommand command)
    {
        if (_simulation is null)
        {
            return;
        }

        // The support panel is not about a building; it is about the map. Neither of these
        // commands is issued by an entity, so neither has any use for a selected one. Nor is a
        // structure placement: what it needs from the panel is the role whose row was pressed,
        // and the site comes from the click that follows.
        switch (command.Kind)
        {
            case HudCommandKind.UseAbility:
                _pendingAbility = command.Ability;
                _pendingBridge = false;
                _pendingStructure = UnitKind.None;
                return;

            case HudCommandKind.BuildBridge:
                _pendingAbility = AbilityId.None;
                _pendingBridge = true;
                _pendingStructure = UnitKind.None;
                return;

            case HudCommandKind.PlaceStructure:
                _pendingAbility = AbilityId.None;
                _pendingBridge = false;
                _pendingStructure = command.Unit;
                return;
        }

        int slot = SelectedBuildingSlot();

        if (slot < 0)
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

            case HudCommandKind.Licence:
                // A licence goes to whoever is on the player's side, which is the match's answer
                // rather than a team number: a one-against-one match has no ally and this does
                // nothing, which is the honest outcome rather than an order at an absent team.
                int ally = AllyTeam();

                if (ally >= 0 && AllyBuildingSlot(ally) is int allySlot and >= 0)
                {
                    ref Entity allyBuilding = ref _simulation.World.GetRefBySlot(allySlot);
                    var allyId = new EntityId(allySlot, allyBuilding.Generation);

                    _simulation.World.Enqueue(SimCommand.Licence(allyId, command.Unit, executeTick, building.TeamId));
                }

                break;
        }
    }

    /// <summary>
    /// The team on the player's side in this match, or -1 when there is none. Only teams that are
    /// <em>playing</em> can be allies, so a match of two factions answers -1 and the interface
    /// offers nobody a licence.
    /// </summary>
    private int AllyTeam()
    {
        if (_simulation is null)
        {
            return -1;
        }

        SimWorld world = _simulation.World;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (team != PlayerTeam && world.AreAllied(PlayerTeam, team))
            {
                return team;
            }
        }

        return -1;
    }

    /// <summary>Slot of one team's first building, which is where a licence lands.</summary>
    private int AllyBuildingSlot(int team)
    {
        if (_simulation is null || team < 0)
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

            if (entity.TeamId == team && IsBuilding(entity.Kind))
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
            // Tracers get their own shader. The billboard one shades radially from the
            // mesh's own coordinates, and a round is drawn with a cube — which has no
            // vertex anywhere near the middle of a face, so its falloff is zero at every
            // vertex and interpolates to zero everywhere. Every tracer, flak round and
            // electric arc was drawn completely transparent.
            _renderer!.BeginParticles(InstancedRenderer.ParticleBlend.Additive, InstancedRenderer.ParticlePass.Tracer);
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
            float x = centre.X + ((i - ((kinds.Length - 1) * 0.5f)) * 12f);
            float z = centre.Z - 30f;
            float ground = terrain.SampleHeightMm((int)(x * 1000f), (int)(z * 1000f)) / 1000f;

            line[i] = (
                kinds[i],
                new Vector3(x, ground + 1.6f, z),
                new Vector3(x, ground + 1.2f, z + 70f),
                i * 0.09f);
        }

        _fireDemo = line;
        _camera?.FocusOn(new Vector3(centre.X, 0f, centre.Z + 5f));
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

                // The bolt is only drawn for a tenth of a second, so on the shared
                // cycle it is on screen about a fifth of the time and a photograph of
                // it is a coin toss. Everything else stays on the shared cycle, which
                // is what makes the line read as a volley.
                timer += kind == UnitKind.ElectroPrototype ? 0.16f : FireDemoInterval;
            }

            _fireDemo[i] = (kind, origin, target, timer);
        }
    }

    /// <summary>Seconds between volleys in the firing-line fixture.</summary>
    private const float FireDemoInterval = 0.55f;

    /// <summary>The firing line's weapons, positions and timers.</summary>
    private (UnitKind Kind, Vector3 Origin, Vector3 Target, float Timer)[] _fireDemo = [];

    /// <summary>
    /// Points the camera at the nearest lava on the map.
    /// <para>
    /// Lava is generated by volcanoes, which a given seed may place anywhere or
    /// nowhere, so "go and look at the lava" is not something a person can do by
    /// dragging the camera around a six-hundred-metre map. The fixture finds it.
    /// </para>
    /// </summary>
    private void FocusOnLava()
    {
        if (_simulation is null || _camera is null)
        {
            return;
        }

        TerrainLayer terrain = _simulation.World.TerrainTypes;
        int lavaCells = 0;

        for (int cell = 0; cell < terrain.Size * terrain.Size; cell++)
        {
            if (terrain.TypeAt(cell) == TerrainType.Lava)
            {
                lavaCells++;
            }
        }

        Console.WriteLine($"lava-demo: {lavaCells} lava cells on this map");

        for (int cell = 0; cell < terrain.Size * terrain.Size; cell++)
        {
            if (terrain.TypeAt(cell) != TerrainType.Lava)
            {
                continue;
            }

            int x = cell % terrain.Size;
            int z = cell / terrain.Size;

            var centre = new Vector3(
                (terrain.OriginMm + (x * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre,
                0f,
                (terrain.OriginMm + (z * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre);

            // Steeper and further back than a river needs: lava sits on a peak, and a
            // shallow camera aimed at a peak is aimed at the sky.
            _camera.ZoomTo(190f);
            _camera.TiltTo(-0.95f);
            _camera.Yaw = 0.5f;
            _camera.FocusOn(centre);

            // Vision comes from units, and liquids are drawn under the fog, so lava
            // nobody can see is lava drawn as fog. One watcher is not enough: a
            // volcano's own slopes are what it covers in lava, so a watcher placed beside
            // the crater is a watcher standing in it and gets moved down the mountain by
            // the same rule that keeps everything else off impassable ground — leaving
            // the crater itself outside anybody's sight, which is what two screenshots of
            // pure fog were telling me. Watchers are therefore placed on all four sides,
            // as close as solid ground allows.
            UnitDefinition observer = UnitCatalog.Get(UnitKind.Infantry);
            SimWorld world = _simulation.World;

            foreach ((int dx, int dz) in new[] { (38, 0), (-38, 0), (0, 38), (0, -38) })
            {
                WorldPos wanting = WorldPos.FromMetres((int)centre.X + dx, 0, (int)centre.Z + dz);

                world.Spawn(
                    Faction.Soviet,
                    PlayerTeam,
                    UnitKind.Infantry,
                    world.LegalSpawnSite(wanting),
                    Fix32.FromInt(observer.SpeedMmPerTick),
                    observer.Health);
            }

            return;
        }
    }

    /// <summary>
    /// Points the camera at the densest wood on the map.
    /// <para>
    /// Where the woodland is is a property of the seed, not of anything a person
    /// can find by dragging a camera over six hundred metres of ground, so the
    /// fixture looks for it. It takes the cell with the most woodland around it
    /// rather than the first one it meets: that is the middle of a wood instead of
    /// its ragged edge, which is the difference between a screenshot of trees and a
    /// screenshot of a treeline.
    /// </para>
    /// <para>
    /// It stands an observer nearby, because vision comes from units and props are
    /// drawn under the fog — a wood nobody can see is a wood drawn as fog, and this
    /// one exists to be looked at.
    /// </para>
    /// </summary>
    private void FocusOnForest()
    {
        if (_simulation is null || _camera is null)
        {
            return;
        }

        TerrainLayer terrain = _simulation.World.TerrainTypes;
        int forestCells = 0;
        int bestCell = -1;
        int bestScore = -1;

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x < terrain.Size; x++)
            {
                if (terrain.TypeAtCell(x, z) != TerrainType.Forest)
                {
                    continue;
                }

                forestCells++;

                // How much woodland surrounds it, out to the radius the generator
                // grows a wood to. Every cell of a wood scores at least itself, so
                // the deepest cell in the thickest wood wins.
                int score = 0;

                for (int dz = -2; dz <= 2; dz++)
                {
                    for (int dx = -2; dx <= 2; dx++)
                    {
                        if (terrain.TypeAtCell(x + dx, z + dz) == TerrainType.Forest)
                        {
                            score++;
                        }
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = (z * terrain.Size) + x;
                }
            }
        }

        Console.WriteLine(
            $"forest-demo: {forestCells} forest cells, {_forest?.Summary ?? "no forest loaded"}, " +
            $"placement digest {_forest?.PlacementDigest ?? 0:X16}");

        if (_forest is { } forest)
        {
            foreach (string shape in forest.LoadedShapes)
            {
                Console.WriteLine($"forest-demo:   shape {shape}");
            }
        }

        if (bestCell < 0)
        {
            Console.WriteLine("forest-demo: no woodland on this map to frame");
            return;
        }

        int cellX = bestCell % terrain.Size;
        int cellZ = bestCell / terrain.Size;

        var centre = new Vector3(
            (terrain.OriginMm + (cellX * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre,
            0f,
            (terrain.OriginMm + (cellZ * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre);

        Console.WriteLine(
            $"forest-demo: densest cell {cellX},{cellZ} with {bestScore} of 25 cells wooded, " +
            $"at {centre.X:0},{centre.Z:0} m");

        // Close enough that a nine-metre tree is a tree rather than a tuft, and far
        // enough back that the trunks are not hiding the crowns behind them. The
        // screenshot options win when they are given: a fixture with one fixed camera
        // cannot be looked at from closer in, and a wood is judged at two distances —
        // as a mass from above and as trees from near the ground.
        _camera.ZoomTo(_options.ScreenshotZoom ?? 135f);
        _camera.TiltTo(_options.ScreenshotPitch ?? -0.66f);
        _camera.Yaw = _options.ScreenshotYaw ?? 0.58f;
        _camera.FocusOn(centre);

        // Three watchers, spread round the wood. Infantry see a hundred and ten
        // metres and the frame is wider than that: one of them leaves the corners of
        // the picture under fog, which is not what looking at a wood is supposed to
        // show.
        foreach ((int dx, int dz) in new[] { (42, 42), (-52, 34), (10, -58) })
        {
            UnitDefinition watcher = UnitCatalog.Get(UnitKind.Infantry);
            WorldPos wanting = WorldPos.FromMetres((int)centre.X + dx, 0, (int)centre.Z + dz);

            _simulation.World.Spawn(
                Faction.Soviet,
                PlayerTeam,
                UnitKind.Infantry,
                _simulation.World.LegalSpawnSite(wanting),
                Fix32.FromInt(watcher.SpeedMmPerTick),
                watcher.Health);
        }
    }

    /// <summary>
    /// How far out the ground fixture scores the surfaces around a cell, in metres,
    /// and how far out it rings its observers. Roughly the half-width of the frame
    /// the camera ends up at, so the score counts the ground that will actually be
    /// in the picture rather than ground that merely exists somewhere nearby.
    /// </summary>
    private const float GroundFrameRadiusMetres = 110f;

    /// <summary>How many kinds of ground a cell's neighbourhood is worth, by surface.</summary>
    private static int GroundWeight(TerrainType type) => type switch
    {
        // The three that are picky about where they form: sand lies on a shore, rock on
        // a slope, mud where the water table reaches the surface. A frame holding all
        // three is the frame this fixture exists to find.
        TerrainType.Mud or TerrainType.Sand or TerrainType.Rock or TerrainType.Mine => 3,

        // Woodland and snow are less fussy but still read as a different place.
        TerrainType.Snow or TerrainType.Forest => 2,

        // Open ground is the baseline — it is everywhere, and a score that counted it
        // heavily would pick a cell in the middle of an empty field.
        TerrainType.Grass => 1,

        // Water and lava have had their own shaders for longer than this fixture has
        // existed, and a view that is mostly sea says nothing about the ground.
        _ => 0,
    };

    /// <summary>
    /// How good a frame of ground is, as the weighted variety in it.
    /// <para>
    /// Counted by area rather than by presence: a seven-cell patch of rock in the
    /// corner of a shot five hundred cells wide is not a frame that shows what rock
    /// looks like, and scoring mere presence picks exactly those frames. Water counts
    /// against the score, because a shoreline offers four kinds of ground in a thin
    /// strip along the top of a lake and would otherwise win every map with a coast.
    /// </para>
    /// </summary>
    private static float GroundScore(int[] counts, int frameCells)
    {
        float enough = frameCells * 0.03f;
        float score = 0f;

        for (int surface = 0; surface < counts.Length; surface++)
        {
            score += GroundWeight((TerrainType)surface) * Math.Min(1f, counts[surface] / enough);
        }

        int liquid = counts[(int)TerrainType.ShallowWater] +
            counts[(int)TerrainType.DeepWater] +
            counts[(int)TerrainType.Lava];

        return score - (10f * liquid / frameCells);
    }

    /// <summary>
    /// The middle of the thickest patch of one surface inside a frame.
    /// <para>
    /// Taken as the cell with the most of its own kind within two cells of it, the way
    /// the wood fixture takes the middle of a wood: aiming at the first cell of a kind
    /// that happens to be in the frame aims at its ragged edge, which is the one place
    /// a surface is least like itself.
    /// </para>
    /// </summary>
    private static (int X, int Z) DensestPatch(TerrainLayer terrain, int centreX, int centreZ, int reach, TerrainType type)
    {
        int bestX = centreX;
        int bestZ = centreZ;
        int bestScore = -1;

        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int x = centreX + dx;
                int z = centreZ + dz;

                if (terrain.TypeAtCell(x, z) != type)
                {
                    continue;
                }

                int score = 0;

                for (int nz = -2; nz <= 2; nz++)
                {
                    for (int nx = -2; nx <= 2; nx++)
                    {
                        if (terrain.TypeAtCell(x + nx, z + nz) == type)
                        {
                            score++;
                        }
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestZ = z;
                }
            }
        }

        return (bestX, bestZ);
    }

    /// <summary>
    /// Whether a cell lies within <paramref name="cells"/> of water, which is where a
    /// beach goes. Checked over the eight neighbours rather than the four so a cell on
    /// the diagonal of a shoreline counts as shore too.
    /// </summary>
    private static bool IsShore(TerrainLayer terrain, int cellX, int cellZ, int cells = 1)
    {
        for (int dz = -cells; dz <= cells; dz++)
        {
            for (int dx = -cells; dx <= cells; dx++)
            {
                TerrainType type = terrain.TypeAtCell(cellX + dx, cellZ + dz);

                if (type is TerrainType.ShallowWater or TerrainType.DeepWater)
                {
                    return true;
                }
            }
        }

        return false;
    }

    /// <summary>
    /// The middle of the widest patch of one surface on the map: the cell with the
    /// most of its own kind within four cells of it. Where <see cref="DensestPatch"/>
    /// answers "where in this frame", this answers "where on the map".
    /// </summary>
    private static (int X, int Z) WidestPatch(TerrainLayer terrain, TerrainType type)
    {
        const int Radius = 4;
        int bestX = 0;
        int bestZ = 0;
        int bestScore = -1;

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x < terrain.Size; x++)
            {
                if (terrain.TypeAtCell(x, z) != type)
                {
                    continue;
                }

                int score = 0;

                for (int nz = -Radius; nz <= Radius; nz++)
                {
                    for (int nx = -Radius; nx <= Radius; nx++)
                    {
                        if (terrain.TypeAtCell(x + nx, z + nz) == type)
                        {
                            score++;
                        }
                    }
                }

                if (score > bestScore)
                {
                    bestScore = score;
                    bestX = x;
                    bestZ = z;
                }
            }
        }

        return (bestX, bestZ);
    }

    /// <summary>
    /// Points the camera at the flight fixture's corridor, at the height the flyers
    /// are actually flying at. A flyer sits sixty metres over the ground underneath
    /// it, and the ground rises and falls by tens of metres across the map, so a
    /// fixed height aims at the sky on a hill and well under an aeroplane in a
    /// valley: the height has to come from the world, and the world does not exist
    /// until the fixture has been built.
    /// </summary>
    private void FocusOnFlight()
    {
        if (_simulation is null || _camera is null)
        {
            return;
        }

        SimWorld world = _simulation.World;
        int altitudeMm = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                altitudeMm = world.GetRefBySlot(slot).AltitudeMm;
                break;
            }
        }

        int xMm = (int)((_options.ScreenshotTargetX ?? 0f) * WorldPos.MmPerMetre);
        int zMm = (int)((_options.ScreenshotTargetZ ?? 0f) * WorldPos.MmPerMetre);

        _fixtureAim = new Vector3(
            xMm * WorldPos.MmToMetres,
            (world.Terrain.SampleHeightMm(xMm, zMm) + altitudeMm) * WorldPos.MmToMetres,
            zMm * WorldPos.MmToMetres);

        _camera.LookAt(_fixtureAim.Value);
    }

    /// <summary>
    /// Points the camera at the middle of whatever the turret fixture put on the map:
    /// two tanks and nothing else, so their midpoint is the line the shot travels
    /// along. Where they are is a property of the seed — the fixture picks the
    /// clearest ground it can find rather than taking the map's centre — so the camera
    /// has to look for them instead of being told where to look.
    /// </summary>
    private void FocusOnTurrets()
    {
        if (_simulation is null || _camera is null)
        {
            return;
        }

        SimWorld world = _simulation.World;
        Vector3 sum = Vector3.Zero;
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                sum += _simulation.GetRenderPosition(slot, interpolate: false);
                count++;
            }
        }

        if (count == 0)
        {
            Console.WriteLine("turret-demo: nothing on the map to frame");
            return;
        }

        Vector3 centre = sum / count;

        Console.WriteLine($"turret-demo: framing {count} units around {centre.X:0}, {centre.Z:0}");

        // A little above the hulls, so the frame is centred on the tanks rather than on
        // the ground under them.
        _fixtureAim = centre + new Vector3(0f, 1.5f, 0f);
        _camera.LookAt(_fixtureAim.Value);
    }

    /// <summary>
    /// Points the camera at the middle of what a fixture laid out, less whatever offset the claim
    /// being photographed needs.
    /// <para>
    /// The emplacement fixture's enemies are out to the +X side of its clearing and the emplacement
    /// is ordered into the middle of it, so its frame is centred on the enemy line — a gun, a tank a
    /// hundred and fifty metres away and the shot crossing between them — with the clearing that
    /// gun will stand in inside the same frame. The other fixtures that use this pass an offset
    /// along the ground, and, where a structure belongs to the scene but not to the claim — a design
    /// bureau that exists only to unlock an ability, a command centre that exists only to raise a
    /// building — a team, so that one building cannot drag the frame off the thing being looked at.
    /// That team is not decoration: an average over everything on the map is the middle of the scene
    /// and not of the claim, and the emplacement fixture's frame used to sit forty-five metres east
    /// of the shot it was meant to be centred on because a building two hundred metres behind the
    /// clearing was in the average.
    /// </para>
    /// </summary>
    private void FocusOnClearing(Vector3 backOff, int? team = null)
    {
        if (_simulation is null || _camera is null)
        {
            return;
        }

        SimWorld world = _simulation.World;
        Vector3 sum = Vector3.Zero;
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && (team is null || world.GetRefBySlot(slot).TeamId == team))
            {
                sum += _simulation.GetRenderPosition(slot, interpolate: false);
                count++;
            }
        }

        if (count == 0)
        {
            Console.WriteLine("fixture: nothing on the map to frame");
            return;
        }

        Vector3 centre = (sum / count) + backOff;

        Console.WriteLine($"fixture: framing {count} entities around {centre.X:0}, {centre.Z:0}");

        _fixtureAim = centre + new Vector3(0f, 1.5f, 0f);
        _camera.LookAt(_fixtureAim.Value);
    }

    /// <summary>
    /// Points the camera at the most varied ground on the map.
    /// <para>
    /// Grass, mud, sand and rock are not neighbours anywhere: sand lies along a shore,
    /// rock on a slope, mud where the ground is wet, and which of them a given seed
    /// puts where is not something anyone can find by dragging a camera over six
    /// hundred metres of map. So the fixture scores every cell by how many kinds of
    /// ground lie around it and frames the best one, and one screenshot then shows
    /// several surface treatments at once instead of one at a time.
    /// </para>
    /// <para>
    /// It rings the frame with observers, for the reason the wood fixture does: vision
    /// comes from units, and ground nobody can see is ground drawn as fog.
    /// </para>
    /// </summary>
    private void FocusOnGround()
    {
        if (_simulation is null || _camera is null)
        {
            return;
        }

        TerrainLayer terrain = _simulation.World.TerrainTypes;

        foreach (TerrainType type in Enum.GetValues<TerrainType>())
        {
            int cells = terrain.CountOf(type);

            if (cells > 0)
            {
                Console.WriteLine($"ground-demo: {cells} cells of {type}");
            }
        }

        int reach = Math.Max(1, (int)(GroundFrameRadiusMetres * WorldPos.MmPerMetre) / terrain.CellSizeMm);
        int frameCells = ((2 * reach) + 1) * ((2 * reach) + 1);
        int bestCell = -1;
        float bestScore = float.MinValue;

        int[] counts = new int[10];

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x < terrain.Size; x++)
            {
                Array.Clear(counts);

                for (int dz = -reach; dz <= reach; dz++)
                {
                    for (int dx = -reach; dx <= reach; dx++)
                    {
                        counts[(int)terrain.TypeAtCell(x + dx, z + dz)]++;
                    }
                }

                float score = GroundScore(counts, frameCells);

                if (score > bestScore)
                {
                    bestScore = score;
                    bestCell = (z * terrain.Size) + x;
                }
            }
        }

        if (bestCell < 0)
        {
            Console.WriteLine("ground-demo: nothing to frame");
            return;
        }

        int cellX = bestCell % terrain.Size;
        int cellZ = bestCell / terrain.Size;

        var centre = new Vector3(
            (terrain.OriginMm + (cellX * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre,
            0f,
            (terrain.OriginMm + (cellZ * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre);

        // What is actually in the frame, rather than what is merely on the map: the
        // census above counts the whole six hundred metres, and a treatment that is
        // missing from the picture is a treatment this fixture has not shown.
        int[] inFrame = new int[10];

        // A beach, laid by hand.
        //
        // The generator cannot currently produce sand at all: its sand band is the
        // heights between the mud line and an eighth of the map's relief, and at this
        // map's relief the mud line is the higher of the two, so every cell that could
        // be sand is claimed by mud first. Nothing in the simulation ever makes sand
        // either — the only terraforming is weather control, and that lays mud.
        //
        // So the one surface this fixture cannot find is the one it has to place. It
        // goes along the shore, which is where a beach belongs, and it goes in through
        // the same API a weather effect uses, so it takes the same route into the mesh
        // as ground the simulation changed itself.
        int sandCells = 0;

        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int x = cellX + dx;
                int z = cellZ + dz;

                // Two cells deep, so the beach is a strip wide enough to look at
                // rather than a line of paint along the water's edge.
                if (terrain.TypeAtCell(x, z) != TerrainType.Grass ||
                    !(IsShore(terrain, x, z) || IsShore(terrain, x, z, 2)))
                {
                    continue;
                }

                terrain.SetType((z * terrain.Size) + x, TerrainType.Sand);
                sandCells++;
            }
        }

        Console.WriteLine($"ground-demo: laid {sandCells} cells of beach along the shore");

        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                inFrame[(int)terrain.TypeAtCell(cellX + dx, cellZ + dz)]++;
            }
        }

        Console.WriteLine(
            $"ground-demo: cell {cellX},{cellZ} at {centre.X:0},{centre.Z:0} m scores {bestScore}, " +
            $"in frame " +
            string.Join(
                ", ",
                Enum.GetValues<TerrainType>()
                    .Where(type => inFrame[(int)type] > 0)
                    .Select(type => $"{type} {inFrame[(int)type]}")));

        // A named surface overrides the choice above. The default frame is picked for
        // variety, which makes it a good look at the ground in general and a poor look
        // at any one kind of it: a surface judged from a frame that contains a patch of
        // it is a surface judged at four pixels. A named one is searched for over the
        // whole map instead, taking the widest patch of it — the patch where it is most
        // itself rather than most bordered.
        if (_options.GroundSurface is { } wanted &&
            Enum.TryParse(wanted, ignoreCase: true, out TerrainType requested) &&
            terrain.CountOf(requested) > 0)
        {
            (int patchX, int patchZ) = WidestPatch(terrain, requested);

            centre = new Vector3(
                (terrain.OriginMm + (patchX * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre,
                0f,
                (terrain.OriginMm + (patchZ * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre);

            Console.WriteLine(
                $"ground-demo: framed {requested} at {patchX},{patchZ} — {centre.X:0},{centre.Z:0} m, " +
                $"{terrain.CountOf(requested)} cells on the map");
        }
        else if (_options.GroundSurface is { } missing)
        {
            Console.WriteLine($"ground-demo: no {missing} on this map to frame");
        }

        // The frame's own relief, because the rock treatment is a function of height
        // and a frame with no height in it cannot show strata.
        MiVic.Core.Terrain.HeightMap map = _simulation.World.Terrain;
        int low = int.MaxValue;
        int high = int.MinValue;

        for (int dz = -reach; dz <= reach; dz++)
        {
            for (int dx = -reach; dx <= reach; dx++)
            {
                int height = map.SampleHeightMm(
                    terrain.OriginMm + ((cellX + dx) * terrain.CellSizeMm),
                    terrain.OriginMm + ((cellZ + dz) * terrain.CellSizeMm));

                low = Math.Min(low, height);
                high = Math.Max(high, height);
            }
        }

        Console.WriteLine($"ground-demo: frame relief {low / 1000f:0.0} to {high / 1000f:0.0} m");

        // Where each kind of ground sits inside the frame, so a close-up can be aimed
        // at one surface. The default frame is two hundred metres wide and a treatment
        // judged from that distance is a treatment judged at four pixels.
        foreach (TerrainType type in Enum.GetValues<TerrainType>())
        {
            if (inFrame[(int)type] == 0)
            {
                continue;
            }

            (int patchX, int patchZ) = DensestPatch(terrain, cellX, cellZ, reach, type);

            var patch = new Vector3(
                (terrain.OriginMm + (patchX * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre,
                0f,
                (terrain.OriginMm + (patchZ * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre);

            Console.WriteLine(
                $"ground-demo:   {type} from {patch.X:0},{patch.Z:0} m " +
                $"({inFrame[(int)type]} cells in frame)");
        }

        // The screenshot options win when they are given, so the fixture can be looked
        // at from closer in, from further out and from over any patch of it without a
        // rebuild. The observers follow the target, or a close-up would be a close-up
        // of fog.
        var focus = new Vector3(
            _options.ScreenshotTargetX ?? centre.X,
            0f,
            _options.ScreenshotTargetZ ?? centre.Z);

        _camera.ZoomTo(_options.ScreenshotZoom ?? 210f);
        _camera.TiltTo(_options.ScreenshotPitch ?? -0.70f);
        _camera.Yaw = _options.ScreenshotYaw ?? 0.62f;
        _camera.FocusOn(focus);

        // Eight watchers round the frame. One at the middle would leave its corners
        // under fog, and a treatment nobody can see is a treatment drawn as fog.
        UnitDefinition watcher = UnitCatalog.Get(UnitKind.Infantry);

        for (int i = 0; i < 8; i++)
        {
            double angle = i * Math.PI / 4d;
            var wanting = WorldPos.FromMetres(
                (int)focus.X + (int)(Math.Cos(angle) * GroundFrameRadiusMetres * 0.62f),
                0,
                (int)focus.Z + (int)(Math.Sin(angle) * GroundFrameRadiusMetres * 0.62f));

            _simulation.World.Spawn(
                Faction.Soviet,
                PlayerTeam,
                UnitKind.Infantry,
                _simulation.World.LegalSpawnSite(wanting),
                Fix32.FromInt(watcher.SpeedMmPerTick),
                watcher.Health);
        }

        // Two vehicles in the middle of it. The treatments are all modulations of the
        // light, so the thing they have to be checked against is a unit: a tank that no
        // longer stands out against the ground it is standing on is a failed treatment
        // however good the ground looks on its own.
        UnitDefinition tank = UnitCatalog.Get(UnitKind.Tank);

        foreach ((int dx, int dz) in new[] { (-16, 12), (20, -14) })
        {
            _simulation.World.Spawn(
                Faction.Western,
                PlayerTeam,
                UnitKind.Tank,
                _simulation.World.LegalSpawnSite(WorldPos.FromMetres((int)focus.X + dx, 0, (int)focus.Z + dz)),
                Fix32.FromInt(tank.SpeedMmPerTick),
                tank.Health);
        }
    }

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
            // visible explosion, or fog of war would leak information. A fixture
            // has no player to leak it to, and a shell crossing the frame from a
            // machine it is not allowed to draw is half a firefight.
            bool visible = !DrawsFogOfWar ||
                simEvent.TeamId == PlayerTeam ||
                world.AreAllied(simEvent.TeamId, PlayerTeam) ||
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

            bool visible = !DrawsFogOfWar ||
                entity.TeamId == PlayerTeam ||
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
                else if (healthFraction < 0.65f)
                {
                    // Damaged industry smokes hard, from wherever it is burning. A structure
                    // with no exhaust part at all — a gun pit, which has nothing to burn and
                    // nothing to vent — sits under this branch only once it is hurt, so a
                    // healthy emplacement no longer trails the haze that a working factory
                    // does. Smoke coming out of a concrete pit reads as a fire.
                    float intensity = 0.35f + ((1f - healthFraction) * 0.65f);
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
        //
        // The point is put on the surface the renderer draws, not on the height field: a
        // destination may be a ford, which is passable ground the client draws at the water
        // line, and a probe point left on the bed under it is a point two and a half metres
        // from anywhere the click mapping could return. The check then measures the mapping
        // rather than the depth of the water it was aimed through.
        WorldPos destination = world.Navigation.CentreOf(destinationCell);
        Vector3 probe = new(destination.X / 1000f, 0f, destination.Z / 1000f);
        probe.Y = DrawnHeightAtMetres(world, world.TerrainTypes, probe.X, probe.Z);

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

        // Pick a row the panel would actually offer, which is the question the row itself asks: may
        // this building put that role on its pad *right now*. It used to be "a role the team can
        // afford", and that is no longer the whole answer — a standard skirmish opens with every side
        // over its command capacity, so the first thing a player meets is a row refused for a reason
        // that has nothing to do with the purse. Asking the gate is what lets this check report the
        // rule instead of calling the panel broken.
        //
        // The check used to top up the stockpile to force the issue, which silently broke mission
        // replays: a direct resource write is not a command, so a replay could not reproduce it.
        UnitKind affordable = UnitKind.None;
        string refusal = "nothing to queue";

        foreach (UnitDefinition definition in UnitCatalog.BuildableBy(world.FactionOfTeam(PlayerTeam)))
        {
            if (definition.IsBuilding || definition.ProducedAt != UnitKind.CommandCentre)
            {
                continue;
            }

            if (world.CanProduce(new EntityId(slot, world.GetRefBySlot(slot).Generation), definition.Kind, out refusal))
            {
                affordable = definition.Kind;
                break;
            }
        }

        ref Entity building = ref world.GetRefBySlot(slot);
        _selection.Select(new EntityId(slot, building.Generation));

        if (affordable == UnitKind.None)
        {
            // Every unit row is refused, and in the standard skirmish that is the rule rather than a
            // fault: the side opens with more army than its structures support. The command path is
            // still checked, through the row that survives it — a structure row is never refused for
            // want of capacity, because capacity is what structures are for — so pressing one must
            // arm a placement. Leaving the panel armed would change what every later check sees, so
            // it is disarmed again here.
            ApplyHudCommand(new HudCommand(HudCommandKind.PlaceStructure, UnitKind.Factory));

            bool armed = _pendingStructure != UnitKind.None;
            _pendingStructure = UnitKind.None;
            _selection.Clear();

            return armed
                ? $"OK (no unit row is offered — {refusal}; the structure row armed a placement)"
                : $"FAIL (no unit row is offered — {refusal}, and the structure row did not arm one)";
        }

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

            // An enemy's building, and an ally's is not one. This used to be "any team but
            // mine", which on the standard skirmish picks the Κινέζοι headquarters half the
            // time — the order was then refused by the simulation for aiming at an ally, and
            // the check reported the order path as broken. It was the check that was wrong:
            // it had its own answer to who an enemy is, which is the mistake the whole
            // friend-or-foe rule exists to prevent.
            if (!world.IsHostile(candidate.TeamId, PlayerTeam) || !IsBuilding(candidate.Kind))
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
            AllyTeam(),
            _imgui!.LargeFont,
            _simulation!.IsPlayback,
            _simulation!.IsPlaybackFinished,
            _pendingBridge,
            _pendingBridge && !_bridgeSiteAllowed ? _bridgeSiteReason : string.Empty,
            _pendingBridge && _bridgeSiteAllowed ? _bridgeSiteCells : 0,
            _pendingStructure,
            _pendingStructure != UnitKind.None && !_structureSiteAllowed ? _structureSiteReason : string.Empty);

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
        _forest?.Dispose();
        _selectionMarkerMesh?.Dispose();
        _healthBackMesh?.Dispose();
        _healthFillMesh?.Dispose();
        _axisMesh?.Dispose();
        _bridgePreviewMesh?.Dispose();
        _coverageMesh?.Dispose();
        _bridges?.Dispose();
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
