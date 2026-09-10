using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Random;
using MiVic.Core.Replay;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Sim;

/// <summary>Something the client wants to know about but the simulation does not track.</summary>
public enum SimEventType : byte
{
    /// <summary>An entity was destroyed.</summary>
    UnitDestroyed = 0,

    /// <summary>An entity took damage.</summary>
    UnitHit = 1,

    /// <summary>An entity fired at another. Nothing has landed yet.</summary>
    ShotFired = 2,
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
/// <param name="TargetSlot">For a shot, the slot being shot at; -1 otherwise.</param>
public readonly record struct SimEvent(
    SimEventType Type,
    int Slot,
    Vector3 Position,
    WorldPos PositionMm,
    Faction Faction,
    int TeamId,
    UnitKind Kind,
    float Scale,
    int Damage = 0,
    int TargetSlot = -1);

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
    private readonly int[] _previousCooldown;
    private readonly int[] _previousTarget;
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

    /// <summary>
    /// Creates the model fixture: an empty world with exactly one entity in it.
    /// <para>
    /// One model and nothing else, so what is on screen is the model and only the
    /// model — no neighbouring units to confuse the silhouette, no fog, no battle.
    /// </para>
    /// </summary>
    public SimBridge(ulong seed, Faction faction, UnitKind kind)
        : this(seed, ScenarioKind.Skirmish, mission: null, replay: null)
    {
        // Clear the skirmish layout, then place the one model at the origin.
        int capacity = World.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (World.IsAliveSlot(slot))
            {
                World.Despawn(new EntityId(slot, World.GetRefBySlot(slot).Generation));
            }
        }

        UnitDefinition definition = UnitCatalog.Get(kind);

        // The centre of the map, not cell zero: the fixture camera looks at the world
        // origin, and cell zero is a corner of the map roughly three hundred metres
        // away. Placing the model there put it off camera entirely.
        int centreCell = World.Navigation.IndexOf(World.Navigation.Size / 2, World.Navigation.Size / 2);

        WorldPos position = World.Navigation.CentreOf(World.Navigation.NearestWalkable(centreCell));

        World.Spawn(
            faction,
            0,
            kind,
            position,
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);
    }

    /// <summary>
    /// A small battle arranged in weapon range of itself, for looking at the
    /// shooting.
    /// <para>
    /// A skirmish eventually produces a firefight somewhere on a 600 m map, which is
    /// no use at all for checking whether a tracer looks like a tracer. This puts two
    /// lines of units forty metres apart on the map centre: close enough that every
    /// weapon in the game is in range from the first tick, and small enough that one
    /// screenshot holds all of it.
    /// </para>
    /// </summary>
    public static SimBridge CreateCombatDemo(ulong seed)
    {
        var bridge = new SimBridge(seed, ScenarioKind.Skirmish, mission: null, replay: null);

        SimWorld world = bridge.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                world.Despawn(new EntityId(slot, world.GetRefBySlot(slot).Generation));
            }
        }

        // Attackers on one side, defenders on the other, eighty-five metres apart:
        // inside rifle range (90 m) so the smallest weapon in the game is firing too,
        // and outside the reach of nothing. Teams 0 and 2 are enemies in every
        // scenario, so the combat system acquires targets without being told to.
        //
        // Weighted towards infantry on purpose. Tanks and artillery kill each other in
        // about four seconds, which leaves nothing to look at; riflemen take half a
        // minute over the same job and keep the tracers coming.
        (Faction Faction, int Team, UnitKind Kind, int X, int Z)[] line =
        [
            (Faction.Soviet, 0, UnitKind.Infantry, -42, -16),
            (Faction.Soviet, 0, UnitKind.Infantry, -42, -6),
            (Faction.Soviet, 0, UnitKind.Infantry, -42, 4),
            (Faction.Soviet, 0, UnitKind.Infantry, -42, 14),
            (Faction.Soviet, 0, UnitKind.Tank, -52, 0),
            (Faction.Soviet, 0, UnitKind.Artillery, -66, 20),
            (Faction.Soviet, 0, UnitKind.RocketArtillery, -70, -24),
            (Faction.Soviet, 0, UnitKind.AntiAir, -46, 28),

            // One flyer a side, so the missile trails are in the picture too. Aircraft
            // are fast and die quickly if they close, so they are held back at the
            // edge of their range.
            (Faction.Soviet, 0, UnitKind.Aircraft, -74, 6),

            (Faction.Western, 2, UnitKind.Infantry, 42, -16),
            (Faction.Western, 2, UnitKind.Infantry, 42, -6),
            (Faction.Western, 2, UnitKind.Infantry, 42, 4),
            (Faction.Western, 2, UnitKind.Infantry, 42, 14),
            (Faction.Western, 2, UnitKind.Tank, 52, 0),
            (Faction.Western, 2, UnitKind.AntiAir, 46, 28),
        ];

        foreach ((Faction faction, int team, UnitKind kind, int x, int z) in line)
        {
            UnitDefinition definition = UnitCatalog.Get(kind);

            world.Spawn(
                faction,
                team,
                kind,
                WorldPos.FromMetres(x, 0, z),
                Fix32.FromInt(definition.SpeedMmPerTick),
                definition.Health);
        }

        return bridge;
    }

    /// <summary>
    /// An empty map with one observer on it, for the firing-line fixture.
    /// <para>
    /// Empty because a round in flight is centimetres across and a battlefield is
    /// full of things that look like rounds in flight; one observer because vision
    /// comes from units, so an entirely empty map is an entirely fogged one, and
    /// particles are drawn under the fog.
    /// </para>
    /// </summary>
    public static SimBridge CreateFiringRange(ulong seed)
    {
        var bridge = new SimBridge(seed, ScenarioKind.Skirmish, mission: null, replay: null);

        SimWorld world = bridge.World;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                world.Despawn(new EntityId(slot, world.GetRefBySlot(slot).Generation));
            }
        }

        UnitDefinition observer = UnitCatalog.Get(UnitKind.Infantry);

        world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Infantry,
            WorldPos.FromMetres(0, 0, 0),
            Fix32.FromInt(observer.SpeedMmPerTick),
            observer.Health);

        return bridge;
    }

    /// <summary>
    /// Two tanks facing each other on the clearest ground the map has, for looking at
    /// where a turret points while its gun is firing.
    /// <para>
    /// The combat demo has tanks in it, but it is framed for a battle and the middle
    /// of the map is woodland: a tank at that distance is a dozen pixels across and
    /// half behind a tree, which is not enough to see a gun. This puts one shooter and
    /// one target on open ground seventy metres apart — inside the tank's own 110 m
    /// range from the first tick — on a line that is not along X, so both turrets have
    /// a real traverse to make and the direction they make it in can be read against
    /// the round in flight.
    /// </para>
    /// </summary>
    public static SimBridge CreateTurretDemo(ulong seed)
    {
        var bridge = new SimBridge(seed, ScenarioKind.Skirmish, mission: null, replay: null);

        SimWorld world = bridge.World;
        Clear(world);

        if (!TryFindClearing(world.TerrainTypes, metres: 90f, out int bestCell, out int bestScore))
        {
            return bridge;
        }

        (int centreX, int centreZ) = CellCentreMetres(world.TerrainTypes, bestCell);

        Console.WriteLine($"turret-demo: open ground at {centreX}, {centreZ} (score {bestScore})");

        // Seventy metres apart, which fills a close frame and is well inside the
        // 110 m a tank can shoot: the pair engages without either having to move. The
        // line between them is deliberately *not* along X: a hull faces +X when it
        // spawns, so a target straight ahead is a turret at zero traverse, and a
        // traverse of zero looks identical whichever way round the sign is. On a
        // diagonal the turret has to swing, and a mirrored traverse points somewhere
        // else entirely.
        Spawn(world, Faction.Soviet, TeamOf(Faction.Soviet), UnitKind.Tank, centreX - 30, centreZ - 20);
        Spawn(world, Faction.Western, TeamOf(Faction.Western), UnitKind.Tank, centreX + 30, centreZ + 20);

        return bridge;
    }

    /// <summary>
    /// A gun emplacement's position and the things it is there to shoot at, on the
    /// clearest ground the map has.
    /// <para>
    /// The emplacement is not placed here: the whole question this fixture exists to
    /// answer is whether a building shoots <em>on its own</em>, so it is put down by the
    /// probe's own `structure … build` — the same command a player's click issues — and
    /// what the fixture supplies is the situation it is put down into. Three enemies at
    /// three different distances from the middle of the clearing: a tank 150 m out, which
    /// is inside a gun emplacement's reach and outside the 110 m a tank can shoot back
    /// from; an aircraft 100 m out, and nearer on purpose, so that a gun which picked its
    /// target by distance alone would be seen tracking something it cannot touch; and a
    /// second tank 60 m out, to give the anti-aircraft emplacement something it must
    /// ignore.
    /// </para>
    /// <para>
    /// Team 0 is put at era II with resources to match, because that is what the
    /// anti-aircraft emplacement is behind — a fixture that could not build the thing
    /// under test would be a fixture that proves nothing about it.
    /// </para>
    /// </summary>
    public static SimBridge CreateEmplacementDemo(ulong seed)
    {
        var bridge = new SimBridge(seed, ScenarioKind.Skirmish, mission: null, replay: null);

        SimWorld world = bridge.World;
        Clear(world);

        if (!TryFindClearing(world.TerrainTypes, metres: 90f, out int bestCell, out int bestScore))
        {
            return bridge;
        }

        (int centreX, int centreZ) = CellCentreMetres(world.TerrainTypes, bestCell);

        Console.WriteLine($"emplacement-demo: open ground at {centreX}, {centreZ} (score {bestScore})");

        ref TeamState state = ref world.TeamRef(0);
        state.TechTier = 2;
        state.Materials = 4_000;
        state.Energy = 1_000;
        state.Water = 1_000;

        // A command centre, because nothing raises itself: a structure is ordered from a
        // finished building of the role that makes it, and an emplacement with no yard to
        // come from is refused as χρειάζεται Κέντρο Διοίκησης before the site is ever
        // looked at. It is set back behind the position rather than beside it, so it is
        // not in the enemy's reach and not in the way of the site the probe picks.
        SpawnAbsolute(world, Faction.Soviet, 0, UnitKind.CommandCentre, centreX - 90, centreZ - 60);

        // The far tank is 150 m away, on the +X side: an emplacement built at the search
        // centre reaches it and it cannot reach back. The aircraft orbits nothing and is
        // simply parked at 60 m, which is what "in range of the gun and inside the anti-
        // aircraft emplacement's reach" looks like from the ground.
        SpawnAbsolute(world, Faction.Western, 2, UnitKind.Tank, centreX + 150, centreZ);
        SpawnAbsolute(world, Faction.Western, 2, UnitKind.Aircraft, centreX + 60, centreZ - 40);
        SpawnAbsolute(world, Faction.Western, 2, UnitKind.Tank, centreX + 60, centreZ + 40);

        return bridge;
    }

    /// <summary>
    /// The largest patch of open ground on the map, as a cell index and the number of open
    /// cells within <paramref name="metres"/> of it.
    /// <para>
    /// Woodland is what blocks the view, and where it is is a property of the seed, so a
    /// fixture looks for the largest clearing rather than hoping the map's middle is one.
    /// Water and lava count against a cell as much as trees do: the first version of this
    /// scored the sea as the clearest ground on the map and framed two tanks standing in it.
    /// </para>
    /// </summary>
    private static bool TryFindClearing(TerrainLayer terrain, float metres, out int cell, out int score)
    {
        int clearCells = ClearRadiusCells(terrain, metres);
        int bestCell = -1;
        int bestScore = -1;

        for (int z = clearCells; z < terrain.Size - clearCells; z++)
        {
            for (int x = clearCells; x < terrain.Size - clearCells; x++)
            {
                if (!IsOpenGround(terrain, x, z))
                {
                    continue;
                }

                int open = 0;

                for (int dz = -clearCells; dz <= clearCells; dz++)
                {
                    for (int dx = -clearCells; dx <= clearCells; dx++)
                    {
                        if (IsOpenGround(terrain, x + dx, z + dz))
                        {
                            open++;
                        }
                    }
                }

                if (open > bestScore)
                {
                    bestScore = open;
                    bestCell = (z * terrain.Size) + x;
                }
            }
        }

        cell = bestCell;
        score = bestScore;
        return bestCell >= 0;
    }

    /// <summary>
    /// Spawns one unit at an exact metre position, without the legal-ground search a
    /// formation spawn uses.
    /// <para>
    /// The emplacement demo's whole geometry is the distance from the gun to each of its
    /// three targets, so a spawn nudged onto the nearest walkable cell would move the
    /// numbers the fixture was built to produce. The clearing this is called into is open
    /// ground already, which is why the search is not needed here.
    /// </para>
    /// </summary>
    private static EntityId SpawnAbsolute(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);

        return world.Spawn(
            faction,
            team,
            kind,
            WorldPos.GroundMetres(x, z),
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);
    }

    /// <summary>
    /// One flyer of every aircraft model, and the drone, crossing the map in a
    /// straight line along X, so a picture of two moments says whether a model
    /// travels nose-first.
    /// <para>
    /// Motion is the only honest reference for "which way is this model facing":
    /// the simulation sets a unit's heading from the step it actually took, so a
    /// unit flying along +X is flying along its own forward axis whatever the model
    /// looks like. The displacement between two frames is therefore a heading the
    /// renderer cannot lie about, and it needs no assumption about which way the
    /// camera is pointing.
    /// </para>
    /// <para>
    /// All four fly over the player's own team, whichever faction's model they
    /// wear, because the client draws what that team can see: a model owned by
    /// anyone else is drawn as fog unless something is watching it. Being on one
    /// team also means nothing shoots anything, so the frame holds aeroplanes and
    /// nothing else.
    /// </para>
    /// </summary>
    public static SimBridge CreateFlightDemo(ulong seed)
    {
        var bridge = new SimBridge(seed, ScenarioKind.Skirmish, mission: null, replay: null);

        SimWorld world = bridge.World;
        Clear(world);

        // One lane each, so a model cannot hide behind the one in front of it.
        (Faction Faction, UnitKind Kind, int Z)[] flyers =
        [
            (Faction.Soviet, UnitKind.Aircraft, -36),
            (Faction.Chinese, UnitKind.Aircraft, -12),
            (Faction.Western, UnitKind.Aircraft, 12),
            (Faction.Chinese, UnitKind.Drone, 36),
        ];

        foreach ((Faction faction, UnitKind kind, int z) in flyers)
        {
            // Placed exactly, not via the legal-site search a ground unit needs: a
            // flyer is sixty metres over whatever is underneath it, and snapping it to
            // the nearest walkable cell would put the lanes — which are the whole
            // geometry of this fixture — wherever the terrain felt like.
            EntityId id = world.Spawn(
                faction,
                0,
                kind,
                WorldPos.FromMetres(-140, 0, z),
                Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
                UnitCatalog.Get(kind).Health);

            world.OrderMove(id, WorldPos.FromMetres(140, 0, z), 0);
        }

        return bridge;
    }

    /// <summary>Empties the world, so a fixture's own line-up is the whole scene.</summary>
    private static void Clear(SimWorld world)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                world.Despawn(new EntityId(slot, world.GetRefBySlot(slot).Generation));
            }
        }
    }

    /// <summary>Spawns one unit of a kind at a metre position, on legal ground.</summary>
    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);

        return world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(WorldPos.FromMetres(x, 0, z)),
            Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);
    }

    /// <summary>
    /// Which team a faction fights for. The skirmish's own sides are 0 and 2, and
    /// the combat system only engages units on opposing teams.
    /// </summary>
    private static int TeamOf(Faction faction) => faction switch
    {
        Faction.Soviet => 0,
        Faction.Western => 2,
        _ => 1,
    };

    /// <summary>Navigation cells that cover a distance in metres, at least one.</summary>
    private static int ClearRadiusCells(TerrainLayer terrain, float metres)
        => Math.Max(1, (int)MathF.Ceiling(metres * WorldPos.MmPerMetre / terrain.CellSizeMm));

    /// <summary>
    /// Ground a tank can stand on with a clear view over it: dry land, and not
    /// woodland, which is the one surface that puts something in the way.
    /// </summary>
    private static bool IsOpenGround(TerrainLayer terrain, int x, int z)
        => terrain.TypeAtCell(x, z) switch
        {
            TerrainType.Grass or TerrainType.Mud or TerrainType.Sand or TerrainType.Snow
                or TerrainType.Rock or TerrainType.Mine => true,
            _ => false,
        };

    /// <summary>A terrain cell's centre in render metres.</summary>
    private static (int X, int Z) CellCentreMetres(TerrainLayer terrain, int cell)
    {
        int x = cell % terrain.Size;
        int z = cell / terrain.Size;

        return (
            (int)((terrain.OriginMm + (x * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre),
            (int)((terrain.OriginMm + (z * terrain.CellSizeMm) + (terrain.CellSizeMm / 2)) / (float)WorldPos.MmPerMetre));
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

        // Firing is detected by watching these two: the combat system sets the
        // cooldown only on the tick a weapon actually fires, and the target is who
        // it was aiming at. Together they are a complete record of a shot, and both
        // are plain simulation state the client is already allowed to read.
        _previousCooldown = new int[World.Capacity];
        _previousTarget = new int[World.Capacity];
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

                // A shot, found without the simulation having to know that shots are
                // drawn. CombatSystem sets the cooldown to the weapon's reload only
                // on the tick it fires; on every other tick it counts that cooldown
                // down. So a cooldown that was zero last tick and is positive now,
                // with a target to shoot at, is a shot — exactly, and with no
                // duplicated weapon table here to drift out of step.
                //
                // A cooldown set because no target could be found is excluded by the
                // target check: that path never leaves a target behind. There used to be a
                // second clause here — the unit had to have had a target on the previous
                // tick as well — which read as a safeguard and was in fact a hole: the
                // first shot of a weapon that had just acquired something was never
                // reported, so the tick an emplacement finished rising and opened fire on
                // its own was the one tick missing from the record of it doing so.
                if (entity.TargetSlot >= 0 &&
                    _previousCooldown[slot] == 0 &&
                    entity.AttackCooldown > 0)
                {
                    _events.Add(new SimEvent(
                        SimEventType.ShotFired,
                        slot,
                        GetRenderPosition(slot, interpolate: false),
                        entity.Position,
                        entity.Faction,
                        entity.TeamId,
                        entity.Kind,
                        ExplosionScale(entity.Kind),
                        Damage: 0,
                        TargetSlot: entity.TargetSlot));
                }

                _previousCooldown[slot] = entity.AttackCooldown;
                _previousTarget[slot] = entity.TargetSlot;

                if (_wasAlive[slot] && health < _previousHealth[slot])
                {
                    // A hit is a shot that landed. The two are separate events because
                    // they are separate things to draw: a shot gets a tracer from the
                    // muzzle, a hit gets an impact where it arrived.
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

                // Clear the firing trackers too. A slot is reused by the next unit
                // spawned into it, and a dead unit's cooldown left behind would read
                // as the new arrival firing on its first tick.
                _previousCooldown[slot] = 0;
                _previousTarget[slot] = -1;
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
        UnitKind.NuclearPlant => 8.5f,
        UnitKind.DesignBureau => 5.5f,
        UnitKind.Aircraft => 3.2f,
        UnitKind.Drone => 1.6f,
        UnitKind.RobotInfantry => 1.1f,
        UnitKind.Mercenary => 1.2f,
        UnitKind.StealthRecon => 1.2f,
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