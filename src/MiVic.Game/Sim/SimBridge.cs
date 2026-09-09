using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Random;
using MiVic.Core.Replay;
using MiVic.Core.Sim;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Sim;

/// <summary>Something the client wants to know about but the simulation does not track.</summary>
public enum SimEventType : byte
{
    /// <summary>An entity was destroyed.</summary>
    UnitDestroyed = 0,

    /// <summary>An entity took damage.</summary>
    UnitHit = 1,
}

/// <summary>A presentation event, in render-space metres.</summary>
/// <param name="Type">What happened.</param>
/// <param name="Slot">Slot the entity occupied.</param>
/// <param name="Position">Where it happened, in metres.</param>
/// <param name="PositionMm">Where it happened, in simulation millimetres, for fog checks.</param>
/// <param name="Faction">Which faction it belonged to.</param>
/// <param name="TeamId">Which team it belonged to.</param>
/// <param name="Kind">What kind of entity it was.</param>
/// <param name="Scale">Suggested effect size in metres.</param>
/// <param name="Damage">Damage taken, for hits; zero otherwise.</param>
public readonly record struct SimEvent(
    SimEventType Type,
    int Slot,
    Vector3 Position,
    WorldPos PositionMm,
    Faction Faction,
    int TeamId,
    UnitKind Kind,
    float Scale,
    int Damage = 0);

/// <summary>
/// Drives the headless simulation from the client and exposes interpolated
/// positions for rendering.
/// <para>
/// The separation matters: the simulation runs on its own fixed 20 Hz clock in
/// integer millimetres, while rendering runs at whatever rate the GPU manages in
/// floats. Nothing the renderer does can feed back into the simulation.
/// </para>
/// </summary>
public sealed class SimBridge
{
    /// <summary>Half-extent of the demo map, in metres.</summary>
    public const float MapHalfExtentMetres = 300f;

    /// <summary>
    /// Entity capacity of a skirmish. Part of the replay contract: slots are
    /// hashed, so a replay must be replayed into a world of the same size.
    /// </summary>
    public const int Capacity = 1024;

    private const int WanderIntervalTicks = 30;
    private const int WanderRadiusMm = 45_000;

    /// <summary>Only every Nth unit wanders, keeping pathfinding cost negligible.</summary>
    private const int WanderStride = 7;

    private readonly WorldPos[] _previousPositions;
    private readonly WorldPos[] _homePositions;
    private readonly Pcg32 _orderRng;
    private readonly ReplayFile? _replay;
    private readonly bool[] _wasAlive;
    private readonly int[] _previousHealth;
    private readonly List<SimEvent> _events = [];

    private int _wanderCountdown;
    private int _replayCursor;

    public SimBridge(ulong seed)
        : this(seed, modelGallery: false)
    {
    }

    /// <summary>Creates either the skirmish or a model gallery.</summary>
    public SimBridge(ulong seed, bool modelGallery)
        : this(seed, modelGallery ? ScenarioKind.ModelGallery : ScenarioKind.Skirmish, mission: null, replay: null)
    {
    }

    /// <summary>Creates a campaign mission from its definition.</summary>
    public SimBridge(MissionDefinition mission)
        : this(
            (mission ?? throw new ArgumentNullException(nameof(mission))).Seed,
            ScenarioKind.Mission,
            mission,
            replay: null)
    {
    }

    /// <summary>
    /// Plays back a recorded match instead of a live one: the world is rebuilt
    /// from the replay's seed and scenario, and the recorded commands are
    /// re-issued at the ticks they were issued on. Nothing else may enqueue, or
    /// the playback would diverge from the recording.
    /// </summary>
    public SimBridge(ReplayFile replay)
        : this(
            (replay ?? throw new ArgumentNullException(nameof(replay))).Seed,
            replay.Scenario,
            replay.MissionId is { } id ? MissionCatalog.Require(id) : null,
            replay)
    {
    }

    private SimBridge(ulong seed, ScenarioKind scenario, MissionDefinition? mission, ReplayFile? replay)
    {
        int capacity = replay?.Capacity ?? Capacity;

        World = new SimWorld(seed, capacity);
        _previousPositions = new WorldPos[World.Capacity];
        _homePositions = new WorldPos[World.Capacity];
        _wasAlive = new bool[World.Capacity];
        _previousHealth = new int[World.Capacity];
        _orderRng = new Pcg32(seed ^ 0x5DEE_CE66_D1CE_F00DUL);
        Scenario = scenario;
        _replay = replay;
        IsGallery = scenario == ScenarioKind.ModelGallery;

        // The starting world is built by the simulation, not here, so a replay
        // can rebuild it with no client involved.
        ScenarioSetup setup = mission is not null
            ? MiVic.Core.Sim.Scenario.BuildMission(World, mission)
            : MiVic.Core.Sim.Scenario.Build(World, scenario);

        _commandCentres.AddRange(setup.CommandCentres);

        foreach (SpawnedEntity spawned in setup.Spawned)
        {
            if (World.TryGetRef(spawned.Id, out _, out int slot))
            {
                // The requested position, not the entity's: Spawn() replaces the
                // Y component with the terrain height, and the wander orders must
                // stay in the same plane they were generated from.
                _homePositions[slot] = spawned.RequestedPosition;
            }
        }

        CapturePreviousPositions();

        for (int slot = 0; slot < World.Capacity; slot++)
        {
            _wasAlive[slot] = World.IsAliveSlot(slot);
            _previousHealth[slot] = _wasAlive[slot] ? World.GetRefBySlot(slot).Health : 0;
        }
    }

    /// <summary>True when this world is a model gallery rather than a skirmish.</summary>
    public bool IsGallery { get; }

    /// <summary>True when a campaign mission is being played.</summary>
    public bool IsMission => World.HasMission;

    /// <summary>True when a recorded match is being played back.</summary>
    public bool IsPlayback => _replay is not null;

    /// <summary>The replay being played back, if any.</summary>
    public ReplayFile? Playback => _replay;

    /// <summary>True once playback has reached the end of the recording.</summary>
    public bool IsPlaybackFinished => _replay is not null && World.Tick >= _replay.FinalTick;

    /// <summary>Which scenario was built, for recording a replay.</summary>
    public ScenarioKind Scenario { get; }

    /// <summary>The authoritative simulation state.</summary>
    public SimWorld World { get; }

    /// <summary>Command centre of each faction, in the order they were spawned.</summary>
    public IReadOnlyList<EntityId> CommandCentres => _commandCentres;

    private readonly List<EntityId> _commandCentres = [];

    /// <summary>Fixed-step clock that decides when to tick.</summary>
    public SimClock Clock { get; } = new();

    /// <summary>Interpolation factor between the previous and current tick.</summary>
    public float Alpha => (float)Clock.InterpolationAlpha;

    /// <summary>Advances the simulation by however many ticks the elapsed time allows.</summary>
    public void Update(long elapsedMicroseconds)
    {
        int ticks = Clock.Advance(elapsedMicroseconds);

        for (int i = 0; i < ticks; i++)
        {
            if (_replay is not null)
            {
                if (World.Tick >= _replay.FinalTick)
                {
                    // The recording has been played out; stop rather than run on.
                    return;
                }

                ApplyReplayCommands();
                CapturePreviousPositions();
                World.Step();
                CollectEvents();
                continue;
            }

            CapturePreviousPositions();
            World.Step();
            CollectEvents();
            IssueWanderOrders();
        }
    }

    /// <summary>
    /// Finds what died during the last tick and records it as a presentation
    /// event. The client needs to know about deaths to spawn explosions, and the
    /// simulation deliberately does not know that explosions exist.
    /// </summary>
    private void CollectEvents()
    {
        int capacity = World.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            bool alive = World.IsAliveSlot(slot);

            if (alive)
            {
                ref Entity entity = ref World.GetRefBySlot(slot);
                int health = entity.Health;

                if (_wasAlive[slot] && health < _previousHealth[slot])
                {
                    // Damage is the only reliable signal that a shot landed: the
                    // cooldown also moves when a unit fails to find a target.
                    _events.Add(new SimEvent(
                        SimEventType.UnitHit,
                        slot,
                        GetRenderPosition(slot, interpolate: false),
                        entity.Position,
                        entity.Faction,
                        entity.TeamId,
                        entity.Kind,
                        ExplosionScale(entity.Kind),
                        _previousHealth[slot] - health));
                }

                _previousHealth[slot] = health;
            }
            else if (_wasAlive[slot])
            {
                // The slot still holds the dead entity's data until it is reused,
                // and the previous-tick position is the last place it stood.
                ref Entity entity = ref World.GetRefBySlot(slot);

                _events.Add(new SimEvent(
                    SimEventType.UnitDestroyed,
                    slot,
                    ToMetres(_previousPositions[slot]),
                    _previousPositions[slot],
                    entity.Faction,
                    entity.TeamId,
                    entity.Kind,
                    ExplosionScale(entity.Kind)));

                _previousHealth[slot] = 0;
            }

            _wasAlive[slot] = alive;
        }
    }

    /// <summary>Visual size of an explosion, roughly in metres of radius.</summary>
    private static float ExplosionScale(UnitKind kind) => kind switch
    {
        UnitKind.CommandCentre => 9f,
        UnitKind.Factory => 7f,
        UnitKind.PowerPlant => 6.5f,
        UnitKind.DesignBureau => 5.5f,
        UnitKind.Aircraft => 3.2f,
        UnitKind.Tank => 2.4f,
        UnitKind.Artillery => 2.2f,
        UnitKind.RocketArtillery => 2.3f,
        UnitKind.Commissar => 0.9f,
        UnitKind.AntiAir => 2f,
        _ => 1.1f,
    };

    /// <summary>
    /// Presentation events from the simulation since the last drain. The client
    /// reads these; the simulation never sees them.
    /// </summary>
    public IReadOnlyList<SimEvent> Events => _events;

    /// <summary>Clears the drained event list. Call after handling them.</summary>
    public void ClearEvents() => _events.Clear();

    /// <summary>
    /// Re-issues every command recorded for the tick the world is currently on,
    /// in the order they were issued. Wander orders are part of the log during
    /// playback, so the client must not generate them again.
    /// </summary>
    private void ApplyReplayCommands()
    {
        IReadOnlyList<SimCommandRecord> commands = _replay!.Commands;

        while (_replayCursor < commands.Count && commands[_replayCursor].Tick == World.Tick)
        {
            World.Enqueue(commands[_replayCursor].Command);
            _replayCursor++;
        }
    }

    /// <summary>
    /// Render position in metres, interpolated between the last two ticks so
    /// motion is smooth regardless of frame rate.
    /// </summary>
    public Vector3 GetRenderPosition(int slot, bool interpolate)
    {
        ref Entity entity = ref World.GetRefBySlot(slot);
        Vector3 current = ToMetres(entity.Position);

        if (!interpolate)
        {
            return current;
        }

        Vector3 previous = ToMetres(_previousPositions[slot]);
        return Vector3.Lerp(previous, current, Alpha);
    }

    /// <summary>Heading in radians, ready for a Y-rotation.</summary>
    public static float HeadingRadians(ushort headingBrads)
        => headingBrads * (MathHelper.TwoPi / 65536f);

    /// <summary>Converts a simulation position to render-space metres.</summary>
    public static Vector3 ToMetres(WorldPos position)
    {
        (float x, float y, float z) = position.ToMetres();
        return new Vector3(x, y, z);
    }

    private void CapturePreviousPositions()
    {
        int capacity = World.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (World.IsAliveSlot(slot))
            {
                _previousPositions[slot] = World.GetRefBySlot(slot).Position;
            }
        }
    }

    /// <summary>
    /// Sends idle units wandering so the demo always has motion. Orders are
    /// generated from a client-side generator, deliberately not the simulation's
    /// RNG, so replaying a fixed command log stays reproducible.
    /// </summary>
    private void IssueWanderOrders()
    {
        if (_replay is not null || --_wanderCountdown > 0)
        {
            return;
        }

        _wanderCountdown = WanderIntervalTicks;

        int capacity = World.Capacity;
        for (int slot = 0; slot < capacity; slot++)
        {
            if (!World.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref World.GetRefBySlot(slot);

            if (entity.HasMoveGoal || entity.Kind == UnitKind.CommandCentre || (slot % WanderStride) != 0)
            {
                continue;
            }

            WorldPos home = _homePositions[slot];
            int dx = _orderRng.NextInt(-WanderRadiusMm, WanderRadiusMm + 1);
            int dz = _orderRng.NextInt(-WanderRadiusMm, WanderRadiusMm + 1);

            WorldPos destination = new(
                ClampToMap(home.X + dx),
                home.Y,
                ClampToMap(home.Z + dz));

            World.OrderMove(new EntityId(slot, entity.Generation), destination, entity.TeamId);
        }
    }

    private static int ClampToMap(int millimetres)
    {
        int limit = (int)(MapHalfExtentMetres * WorldPos.MmPerMetre);
        return IntMath.Clamp(millimetres, -limit, limit);
    }
}