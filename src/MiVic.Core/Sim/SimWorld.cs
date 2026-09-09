using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Random;
using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// The authoritative simulation state: entities, tick counter, RNG and the
/// command queue.
/// <para>
/// Everything here is deterministic. There are no dictionaries, no LINQ, no
/// floating point and no wall-clock reads in <see cref="Step"/>; iteration is
/// always in ascending slot order so that two machines running the same commands
/// produce bit-identical state.
/// </para>
/// </summary>
public sealed class SimWorld
{
    private readonly Entity[] _entities;
    private readonly int[] _freeSlots;
    private readonly int[] _pathCells;
    private readonly ProductionJob[] _jobs;
    private readonly TeamState[] _teams;
    private readonly PathFinder _pathFinder;
    private readonly SpatialIndex _spatial;
    private readonly VisibilityGrid _visibility;
    private readonly List<SimCommand> _commandQueue = new();
    private readonly List<SimCommandRecord> _recordedCommands = [];
    private ObjectiveState[] _objectives = [];
    private MissionDefinition? _mission;
    private bool _recording;
    private bool _insideStep;
    private int _freeCount;

    /// <summary>Creates a world with the maximum entity capacity.</summary>
    public SimWorld(ulong seed)
        : this(seed, SimConstants.MaxEntities)
    {
    }

    /// <summary>Creates a world with an explicit capacity, for tests.</summary>
    public SimWorld(ulong seed, int capacity)
    {
        if (capacity <= 0 || capacity > SimConstants.MaxEntities)
        {
            throw new ArgumentOutOfRangeException(nameof(capacity), capacity, "Capacity must be in (0, MaxEntities].");
        }

        Seed = seed;
        _entities = new Entity[capacity];
        _freeSlots = new int[capacity];
        _freeCount = capacity;

        // Reverse order so the first spawn lands in slot 0.
        for (int i = 0; i < capacity; i++)
        {
            _freeSlots[i] = capacity - 1 - i;
        }

        Rng = new Pcg32(seed);

        // Terrain is a pure function of the seed, so it never needs to be part of
        // the per-tick state hash — only of the initial world hash.
        Terrain = HeightMap.Generate(seed, SimConstants.TerrainResolution, SimConstants.MapExtentMm, SimConstants.TerrainMaxHeightMm);
        Navigation = NavGrid.Build(Terrain, SimConstants.MaxSlopePermille, SimConstants.NavGridStride);
        TerrainTypes = TerrainLayer.Build(Terrain, Navigation);
        _pathFinder = new PathFinder(Navigation.CellCount);
        _pathCells = new int[capacity * SimConstants.MaxPathCells];
        _jobs = new ProductionJob[capacity * SimConstants.MaxQueueLength];
        _teams = new TeamState[SimConstants.TeamCount];
        _spatial = new SpatialIndex(capacity, SimConstants.MapExtentMm);
        _visibility = new VisibilityGrid(Navigation.Size);

        // Every faction starts able to build its tier-1 hardware: infantry and
        // structures. Research raises the tier from there.
        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            _teams[team].TechTier = 1;
            ResearchSystem.RefreshModifiers(ref _teams[team]);
        }
    }

    /// <summary>The battlefield height field.</summary>
    public HeightMap Terrain { get; }

    /// <summary>Walkability and cost derived from <see cref="Terrain"/>.</summary>
    public NavGrid Navigation { get; }

    /// <summary>
    /// Surface types and their movement costs, on the same lattice as
    /// <see cref="Navigation"/>. Like the height field it is a pure function of the
    /// seed, so it is reproducible without being hashed every tick.
    /// </summary>
    public TerrainLayer TerrainTypes { get; }

    /// <summary>Uniform grid of live entities, rebuilt each tick for proximity queries.</summary>
    public SpatialIndex Spatial => _spatial;

    /// <summary>Per-team visibility and explored map.</summary>
    public VisibilityGrid Visibility => _visibility;

    /// <summary>How the battle ended; <see cref="GameOutcome.Ongoing"/> while it lasts.</summary>
    public GameOutcome Outcome { get; private set; }

    /// <summary>
    /// The campaign mission this world is playing, or null for a skirmish.
    /// A mission replaces the last-team-standing victory rule with objectives.
    /// </summary>
    public MissionDefinition? Mission => _mission;

    /// <summary>True when a campaign mission is attached.</summary>
    public bool HasMission => _mission is not null;

    /// <summary>Runtime state of the mission's objectives, in definition order.</summary>
    public ReadOnlySpan<ObjectiveState> Objectives => _objectives;

    /// <summary>Mutable objective states, for <see cref="MissionSystem"/>.</summary>
    internal Span<ObjectiveState> ObjectiveStatesSpan => _objectives;

    /// <summary>
    /// Attaches a mission and resets its objective state. Called once, when the
    /// world is built, before the first tick.
    /// </summary>
    public void AttachMission(MissionDefinition mission)
    {
        ArgumentNullException.ThrowIfNull(mission);

        if (_mission is not null)
        {
            throw new InvalidOperationException("A mission is already attached.");
        }

        _mission = mission;
        _objectives = new ObjectiveState[mission.Objectives.Count];
    }

    /// <summary>Sets the final outcome. Called by <see cref="VictorySystem"/>.</summary>
    internal void SetOutcome(GameOutcome outcome) => Outcome = outcome;

    /// <summary>Per-system timing, for diagnosing frame hitches.</summary>
    public SimProfiler Profiler { get; } = new();

    /// <summary>Seed this world was created with.</summary>
    public ulong Seed { get; }

    /// <summary>Ticks elapsed. Zero before the first <see cref="Step"/>.</summary>
    public long Tick { get; private set; }

    /// <summary>Number of live entities.</summary>
    public int AliveCount { get; private set; }

    /// <summary>Number of slots, live or free.</summary>
    public int Capacity => _entities.Length;

    /// <summary>Direct access to a team's resources and research progress.</summary>
    public ref TeamState TeamRef(int team) => ref _teams[team];

    /// <summary>Copy of a team's resources.</summary>
    public TeamState Team(int team) => _teams[team];

    /// <summary>Production jobs queued at an entity.</summary>
    public ReadOnlySpan<ProductionJob> JobsOf(int slot)
        => _jobs.AsSpan(slot * SimConstants.MaxQueueLength, _entities[slot].QueueLength);

    /// <summary>Mutable reference to one job in an entity's queue.</summary>
    public ref ProductionJob JobRef(int slot, int index)
        => ref _jobs[(slot * SimConstants.MaxQueueLength) + index];

    /// <summary>Appends a job to a building's queue. Returns false when full.</summary>
    public bool AddJob(int slot, UnitKind kind, int totalTicks)
    {
        ref Entity e = ref _entities[slot];

        if (e.QueueLength >= SimConstants.MaxQueueLength)
        {
            return false;
        }

        ref ProductionJob job = ref JobRef(slot, e.QueueLength);
        job.Kind = kind;
        job.TotalTicks = totalTicks;
        job.RemainingTicks = totalTicks;
        e.QueueLength++;
        return true;
    }

    /// <summary>Removes the most recently queued job, refunding nothing.</summary>
    public bool RemoveLastJob(int slot)
    {
        ref Entity e = ref _entities[slot];

        if (e.QueueLength == 0)
        {
            return false;
        }

        e.QueueLength--;
        return true;
    }

    /// <summary>Removes a specific job and shifts the rest forward.</summary>
    public void RemoveJobAt(int slot, int index)
    {
        ref Entity e = ref _entities[slot];

        for (int i = index; i < e.QueueLength - 1; i++)
        {
            JobRef(slot, i) = JobRef(slot, i + 1);
        }

        if (e.QueueLength > 0)
        {
            e.QueueLength--;
        }
    }

    /// <summary>
    /// Simulation random source. Declared as a field, not a property: mutating
    /// methods on a struct must not be invoked through a property, or the
    /// advanced state would be silently discarded.
    /// </summary>
    public Pcg32 Rng;

    /// <summary>Commands waiting to execute, in issue order.</summary>
    public int PendingCommandCount => _commandQueue.Count;

    /// <summary>True while external commands are being logged for a replay.</summary>
    public bool IsRecording => _recording;

    /// <summary>
    /// External commands recorded so far, in issue order.
    /// <para>
    /// Only commands that arrive from outside <see cref="Step"/> are logged: the
    /// AI is part of the simulation and re-derives its own orders from the world
    /// state, so recording them would double them on replay.
    /// </para>
    /// </summary>
    public IReadOnlyList<SimCommandRecord> RecordedCommands => _recordedCommands;

    /// <summary>Begins logging external commands. Clears any previous log.</summary>
    public void StartRecording()
    {
        _recordedCommands.Clear();
        _recording = true;
    }

    /// <summary>Stops logging external commands.</summary>
    public void StopRecording() => _recording = false;

    /// <summary>Queues a command. It runs when its execute tick is reached.</summary>
    public void Enqueue(in SimCommand command)
    {
        if (command.ExecuteTick < Tick)
        {
            throw new ArgumentOutOfRangeException(nameof(command), command.ExecuteTick, "Cannot schedule a command in the past.");
        }

        if (_recording && !_insideStep)
        {
            _recordedCommands.Add(new SimCommandRecord(Tick, command));
        }

        _commandQueue.Add(command);
    }

    /// <summary>Queues a move order for the next tick.</summary>
    public void OrderMove(EntityId target, WorldPos destination, int issuerTeam)
        => Enqueue(SimCommand.Move(target, destination, Tick + 1, issuerTeam));

    /// <summary>True when <paramref name="slot"/> holds a live entity.</summary>
    public bool IsAliveSlot(int slot) => (uint)slot < (uint)_entities.Length && _entities[slot].Alive;

    /// <summary>
    /// Direct slot access for systems and for the renderer's read pass.
    /// The caller must have checked <see cref="IsAliveSlot"/> first.
    /// </summary>
    public ref Entity GetRefBySlot(int slot) => ref _entities[slot];

    /// <summary>Resolves a handle to a live entity, or false if it is stale.</summary>
    public bool TryGet(EntityId id, out Entity entity)
    {
        if (TryResolve(id, out int slot))
        {
            entity = _entities[slot];
            return true;
        }

        entity = default;
        return false;
    }

    /// <summary>Resolves a handle to a live entity reference.</summary>
    public bool TryGetRef(EntityId id, out EntityId resolved, out int slot)
    {
        if (TryResolve(id, out slot))
        {
            resolved = id;
            return true;
        }

        resolved = EntityId.None;
        return false;
    }

    /// <summary>True when the handle still refers to the entity it was made for.</summary>
    public bool IsValid(EntityId id) => TryResolve(id, out _);

    /// <summary>
    /// Creates an entity in the first free slot. Slot order is deterministic,
    /// so spawn order is part of the replay contract.
    /// </summary>
    public EntityId Spawn(Faction faction, int teamId, UnitKind kind, WorldPos position, Fix32 speedMmPerTick, int health)
    {
        if (_freeCount == 0)
        {
            throw new InvalidOperationException("Entity capacity exhausted.");
        }

        int slot = _freeSlots[--_freeCount];
        ref Entity e = ref _entities[slot];
        int generation = e.Generation;

        e.Alive = true;
        e.Generation = generation;
        e.Faction = faction;
        e.TeamId = teamId;
        e.Kind = kind;
        e.AltitudeMm = kind == UnitKind.Aircraft ? 60_000 : 0;

        UnitDefinition definition = UnitCatalog.Get(kind);
        int spawnX = position.X;
        int spawnZ = position.Z;

        // A mobile unit that would appear in a lake is moved to the nearest dry
        // ground. Spawning inside impassable terrain would leave it unable to path
        // anywhere at all, which reads as a broken unit rather than a terrain rule.
        if (!definition.IsBuilding && definition.Movement != MovementClass.Air)
        {
            PathContext context = new(definition.Movement, UnitCatalog.GroundPressure(faction, kind));
            int cell = Navigation.IndexOfWorld(new WorldPos(spawnX, 0, spawnZ));

            if (!TerrainTypes.IsPassable(cell, definition.Movement))
            {
                int dry = FindFreeCell(cell, definition.Movement);

                if (dry >= 0)
                {
                    WorldPos centre = Navigation.CentreOf(dry);
                    spawnX = centre.X;
                    spawnZ = centre.Z;
                }
            }
        }

        e.Position = new WorldPos(spawnX, Terrain.SampleHeightMm(spawnX, spawnZ) + e.AltitudeMm, spawnZ);
        e.MoveGoal = e.Position;
        e.HasMoveGoal = false;

        // Completed research is baked in at spawn: armour into hit points, speed
        // into the movement envelope.
        TeamState owner = (uint)teamId < SimConstants.TeamCount ? _teams[teamId] : default;
        int armor = owner.ArmorPermille > 0 ? owner.ArmorPermille : 1_000;
        int speedScale = owner.SpeedPermille > 0 ? owner.SpeedPermille : 1_000;

        e.SpeedMmPerTick = Fix32.FromRaw((int)(((long)speedMmPerTick.Raw * speedScale) / 1_000));
        e.Morale = FactionProfile.For(faction).MoraleFloor;
        e.Health = (health * armor) / 1_000;
        e.Heading = 0;
        e.PathLength = 0;
        e.PathCursor = 0;
        e.QueueLength = 0;
        e.TargetSlot = -1;
        e.AttackCooldown = 0;
        e.HasAttackOrder = false;
        e.Routed = false;
        e.MoraleTargetRaw = e.Morale.Raw;
        e.NeedsPath = false;
        e.PathFailures = 0;

        AliveCount++;
        return new EntityId(slot, generation);
    }

    /// <summary>
    /// Nearest cell a mover can stand on that is not already taken.
    /// <para>
    /// When a whole formation spawns inside a lake, the nearest dry cell is the
    /// same cell for every unit in it, and the whole formation stacks on one point
    /// — which then breaks picking, because the units are literally co-located.
    /// Spreading them out over the first free cells keeps the formation readable.
    /// </para>
    /// </summary>
    private int FindFreeCell(int cell, MovementClass movement)
    {
        int cx = Navigation.CellX(Math.Max(cell, 0));
        int cz = Navigation.CellZ(Math.Max(cell, 0));

        for (int radius = 0; radius <= Navigation.Size; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    if (radius > 0 && Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int candidate = Navigation.IndexOf(cx + dx, cz + dz);

                    if (candidate < 0 || !Navigation.IsWalkable(candidate) ||
                        !TerrainTypes.IsPassable(candidate, movement) ||
                        IsCellOccupied(candidate))
                    {
                        continue;
                    }

                    return candidate;
                }
            }
        }

        return -1;
    }

    /// <summary>True when a live entity already stands in this cell.</summary>
    private bool IsCellOccupied(int cell)
    {
        for (int slot = 0; slot < _entities.Length; slot++)
        {
            if (_entities[slot].Alive && Navigation.IndexOfWorld(_entities[slot].Position) == cell)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// How a faction's version of a role moves, for terrain cost and passability.
    /// </summary>
    public static PathContext PathContextFor(Faction faction, UnitKind kind)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        return new PathContext(definition.Movement, UnitCatalog.GroundPressure(faction, kind));
    }

    /// <summary>
    /// A team's version of the same context, after its completed research. The
    /// mobility doctrine «Βαθιά Μάχη» lowers effective ground pressure, which is
    /// how the Σοβιετικοί turn the mud from a tax into an advantage.
    /// </summary>
    public PathContext PathContextOf(int team, Faction faction, UnitKind kind)
    {
        PathContext context = PathContextFor(faction, kind);

        if ((uint)team >= SimConstants.TeamCount || context.GroundPressurePermille <= 0)
        {
            return context;
        }

        int resistance = _teams[team].TerrainResistancePermille;

        return resistance > 0
            ? context with { GroundPressurePermille = (context.GroundPressurePermille * resistance) / 1_000 }
            : context;
    }

    /// <summary>Waypoints of an entity's current path, in walking order.</summary>
    public ReadOnlySpan<int> PathOf(int slot)
    {
        ref Entity e = ref _entities[slot];
        return _pathCells.AsSpan(slot * SimConstants.MaxPathCells, Math.Min(e.PathLength, SimConstants.MaxPathCells));
    }

    /// <summary>
    /// Recomputes a path from the entity's current position to its move goal.
    /// Called when a long route is walked in legs.
    /// </summary>
    /// <returns>True when at least one waypoint was found.</returns>
    public bool RepathFrom(int slot)
    {
        ref Entity e = ref _entities[slot];

        if (!e.HasMoveGoal)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return false;
        }

        int start = Navigation.IndexOfWorld(e.Position);
        PathContext context = PathContextOf(e.TeamId, e.Faction, e.Kind);
        int goal = Navigation.NearestWalkable(Navigation.IndexOfWorld(e.MoveGoal), TerrainTypes, context);

        if (goal < 0)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return false;
        }

        // Arriving inside the goal cell needs no path at all.
        if (start == goal)
        {
            e.PathLength = 0;
            e.PathCursor = 0;
            return true;
        }

        Span<int> path = _pathCells.AsSpan(slot * SimConstants.MaxPathCells, SimConstants.MaxPathCells);
        int length = _pathFinder.FindPath(Navigation, TerrainTypes, context, start, goal, path);

        if (length > 1)
        {
            // Straighten the route: raw A* output follows cell centres and reads
            // as a zigzag. Smoothing writes back over the same buffer.
            length = Navigation.Smooth(path, length, start, path, TerrainTypes, context);
        }

        e.PathLength = length;
        e.PathCursor = 0;
        return length > 0;
    }

    /// <summary>Destroys an entity and recycles its slot with a new generation.</summary>
    public bool Despawn(EntityId id)
    {
        if (!TryResolve(id, out int slot))
        {
            return false;
        }

        ref Entity e = ref _entities[slot];

        // Mission objectives ask "how many enemy structures have you destroyed",
        // so the count is kept as state rather than inferred from a baseline:
        // that way a rebuilt structure does not undo the progress.
        if (UnitCatalog.Get(e.Kind).IsBuilding && (uint)e.TeamId < SimConstants.TeamCount)
        {
            _teams[e.TeamId].StructuresLost++;
        }

        e.Alive = false;
        e.Generation = id.Generation + 1;
        _freeSlots[_freeCount++] = slot;
        AliveCount--;
        return true;
    }

    /// <summary>Applies every queued command whose execute tick has arrived.</summary>
    public int ApplyPendingCommands()
    {
        int applied = 0;
        int write = 0;
        int count = _commandQueue.Count;

        // Single pass with a write cursor: commands that are not due yet are
        // compacted to the front, keeping their relative order. Removing with
        // RemoveAt inside the loop would be quadratic in the queue length, which
        // matters once a player can order hundreds of units in one tick.
        for (int read = 0; read < count; read++)
        {
            SimCommand command = _commandQueue[read];

            if (command.ExecuteTick > Tick)
            {
                _commandQueue[write++] = command;
                continue;
            }

            if (Apply(command))
            {
                applied++;
            }
        }

        if (write < count)
        {
            _commandQueue.RemoveRange(write, count - write);
        }

        return applied;
    }

    /// <summary>Advances the simulation by exactly one tick.</summary>
    public void Step()
    {
        // Tick is incremented first so that a command stamped "next tick" at
        // Tick N executes on the very next Step, and so that Tick always counts
        // the number of ticks already simulated.
        Tick++;
        Profiler.Begin();

        // Commands issued from inside a tick come from systems such as the AI and
        // are not recorded: they are re-derived on replay from the same state.
        _insideStep = true;

        try
        {
            ApplyPendingCommands();
            Profiler.Mark(ref Profiler.Commands, ref Profiler.WorstCommands);
            AiSystem.Tick(this);
            Profiler.Mark(ref Profiler.Ai, ref Profiler.WorstAi);
            _spatial.Rebuild(this);
            Profiler.Mark(ref Profiler.Spatial, ref Profiler.WorstSpatial);
            VisionSystem.Tick(this);
            Profiler.Mark(ref Profiler.Vision, ref Profiler.WorstVision);
            EconomySystem.Tick(this);
            Profiler.Mark(ref Profiler.Economy, ref Profiler.WorstEconomy);
            ResearchSystem.Tick(this);
            Profiler.Mark(ref Profiler.Research, ref Profiler.WorstResearch);
            PrototypeSystem.Tick(this);
            Profiler.Mark(ref Profiler.Research, ref Profiler.WorstResearch);
            ProductionSystem.Tick(this);
            Profiler.Mark(ref Profiler.Production, ref Profiler.WorstProduction);
            PathingSystem.Tick(this);
            Profiler.Mark(ref Profiler.Pathing, ref Profiler.WorstPathing);
            MoraleSystem.Tick(this);
            Profiler.Mark(ref Profiler.Morale, ref Profiler.WorstMorale);
            CombatSystem.Tick(this);
            Profiler.Mark(ref Profiler.Combat, ref Profiler.WorstCombat);
            MovementSystem.Tick(this);
            Profiler.Mark(ref Profiler.Movement, ref Profiler.WorstMovement);

            // A mission decides its own outcome; the skirmish rule only applies
            // when there is no mission attached.
            if (_mission is not null)
            {
                MissionSystem.Tick(this);
            }
            else
            {
                VictorySystem.Tick(this);
            }

            Profiler.EndStep(Tick);
        }
        finally
        {
            _insideStep = false;
        }
    }

    /// <summary>Advances the simulation by <paramref name="count"/> ticks.</summary>
    public void RunTicks(long count)
    {
        if (count < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(count), count, "Must not be negative.");
        }

        for (long i = 0; i < count; i++)
        {
            Step();
        }
    }

    private bool Apply(in SimCommand command)
    {
        if (!TryResolve(command.Target, out int slot))
        {
            return false;
        }

        ref Entity e = ref _entities[slot];

        // A licence names a building owned by the *recipient*, so it cannot go
        // through the "your own units only" check below.
        if (command.Kind == SimCommandKind.LicenceProduction)
        {
            return TryLicence(command.Target, command.UnitKind, command.IssuerTeam);
        }

        // A team may only command its own units.
        if (e.TeamId != command.IssuerTeam)
        {
            return false;
        }

        switch (command.Kind)
        {
            case SimCommandKind.Move:
                // Ground units are pinned to the terrain surface at the destination;
                // aircraft keep the altitude they were ordered to.
                int goalY = e.Kind == UnitKind.Aircraft
                    ? command.Destination.Y
                    : Terrain.SampleHeightMm(command.Destination.X, command.Destination.Z) + e.AltitudeMm;

                e.MoveGoal = new WorldPos(command.Destination.X, goalY, command.Destination.Z);
                e.HasMoveGoal = true;
                e.PathLength = 0;
                e.PathCursor = 0;
                e.PathFailures = 0;

                // Route on a later tick: path searches are budgeted.
                e.NeedsPath = true;
                return true;

            case SimCommandKind.Stop:
                e.HasMoveGoal = false;
                e.MoveGoal = e.Position;
                e.PathLength = 0;
                e.PathCursor = 0;
                e.NeedsPath = false;
                return true;

            case SimCommandKind.QueueUnit:
                return TryQueueUnit(slot, ref e, command.UnitKind);

            case SimCommandKind.CancelProduction:
                return RemoveLastJob(slot);

            case SimCommandKind.Research:
                return TryStartResearch(ref e, command.Tech);

            case SimCommandKind.Attack:
                return TryAttack(slot, ref e, command.AttackTarget);

            case SimCommandKind.ApproveDesign:
                return TryApproveDesign(ref e, command.UnitKind);

            default:
                return false;
        }
    }

    /// <summary>The faction that owns a team slot.</summary>
    public static Faction FactionOfTeam(int team) => team switch
    {
        0 => Faction.Soviet,
        1 => Faction.Chinese,
        2 => Faction.Western,
        _ => Faction.None,
    };

    /// <summary>True when two teams may not attack each other.</summary>
    public static bool AreAllied(int a, int b) => a == b || (a is 0 or 1 && b is 0 or 1);

    /// <summary>
    /// True when a team may build a role: either it researched the tier, or an
    /// ally licensed the design to it. A licence deliberately bypasses the
    /// faction's tech ceiling — that is the whole point of the alliance.
    /// <para>
    /// Σοβιετικοί factory output needs one more thing: the design bureau must have
    /// run the prototype first. Infantry is exempt because it comes from the
    /// command centre, and a licence is exempt because an ally has already proven
    /// the design. This is what stops a Σοβιετικοί army from pivoting.
    /// </para>
    /// </summary>
    public bool CanBuild(int team, UnitKind kind)
    {
        if ((uint)team >= SimConstants.TeamCount || !UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        ref TeamState state = ref _teams[team];
        uint bit = 1u << (int)kind;

        if ((state.LicenceMask & bit) != 0)
        {
            return true;
        }

        if (!UnitCatalog.IsUnlocked(FactionOfTeam(team), kind, state.TechTier))
        {
            return false;
        }

        return FactionOfTeam(team) != Faction.Soviet
            || definition.ProducedAt != UnitKind.Factory
            || (state.ApprovedMask & bit) != 0;
    }

    /// <summary>Cost and time of a prototype run, as a multiple of the unit's own.</summary>
    public const int PrototypeCostPermille = 2_000;

    /// <summary>
    /// Starts a prototype run for a design at a design bureau. Σοβιετικοί only,
    /// and only for factory output: infantry comes from the command centre and
    /// needs no proving. On completion the design is approved for production.
    /// </summary>
    private bool TryApproveDesign(ref Entity bureau, UnitKind kind)
    {
        if (bureau.Kind != UnitKind.DesignBureau || (uint)bureau.TeamId >= SimConstants.TeamCount)
        {
            return false;
        }

        if (bureau.Faction != Faction.Soviet || !UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        if (definition.ProducedAt != UnitKind.Factory)
        {
            return false;
        }

        ref TeamState team = ref _teams[bureau.TeamId];
        uint bit = 1u << (int)kind;

        if ((team.ApprovedMask & bit) != 0 || team.IsPrototyping)
        {
            return false;
        }

        // A licensed design is already proven by the ally who gave it.
        if ((team.LicenceMask & bit) == 0 && !UnitCatalog.IsUnlocked(bureau.Faction, kind, team.TechTier))
        {
            return false;
        }

        int materials = (UnitCatalog.MaterialCost(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;
        int energy = (UnitCatalog.EnergyCost(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;
        int water = (UnitCatalog.WaterCost(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;

        if (team.Materials < materials || team.Energy < energy || team.Water < water)
        {
            return false;
        }

        team.Materials -= materials;
        team.Energy -= energy;
        team.Water -= water;

        int ticks = (UnitCatalog.BuildTicks(bureau.Faction, kind) * PrototypeCostPermille) / 1_000;

        team.PrototypeKind = kind;
        team.PrototypeTicksTotal = Math.Max(1, ticks);
        team.PrototypeTicksRemaining = team.PrototypeTicksTotal;
        return true;
    }

    /// <summary>Grants an allied team a licence for a design the issuer already has.</summary>
    private bool TryLicence(EntityId recipientBuilding, UnitKind kind, int issuerTeam)
    {
        if (!TryResolve(recipientBuilding, out int slot) || (uint)issuerTeam >= SimConstants.TeamCount)
        {
            return false;
        }

        ref Entity building = ref _entities[slot];

        if (issuerTeam == building.TeamId || !AreAllied(issuerTeam, building.TeamId))
        {
            return false;
        }

        if (!UnitCatalog.TryGet(kind, out _))
        {
            return false;
        }

        // The licensor must actually possess the design.
        if (!UnitCatalog.IsUnlocked(FactionOfTeam(issuerTeam), kind, _teams[issuerTeam].TechTier))
        {
            return false;
        }

        _teams[building.TeamId].LicenceMask |= 1u << (int)kind;
        return true;
    }

    /// <summary>
    /// Locks a unit onto an enemy. If the target is out of range the unit is sent
    /// towards it, and the combat system keeps the approach updated as the enemy
    /// moves.
    /// </summary>
    private bool TryAttack(int slot, ref Entity attacker, EntityId victim)
    {
        UnitDefinition weapon = UnitCatalog.Get(attacker.Kind);

        if (!weapon.IsArmed || !TryResolve(victim, out int victimSlot))
        {
            return false;
        }

        ref Entity target = ref _entities[victimSlot];

        if (target.TeamId == attacker.TeamId)
        {
            return false;
        }

        attacker.TargetSlot = victimSlot;
        attacker.HasAttackOrder = true;

        long dx = attacker.Position.X - target.Position.X;
        long dz = attacker.Position.Z - target.Position.Z;
        bool inRange = ((dx * dx) + (dz * dz)) <= ((long)weapon.AttackRangeMm * weapon.AttackRangeMm);

        if (!inRange)
        {
            attacker.MoveGoal = target.Position;
            attacker.HasMoveGoal = true;
            attacker.PathLength = 0;
            attacker.PathCursor = 0;
            attacker.PathFailures = 0;
            attacker.NeedsPath = true;
        }

        return true;
    }

    /// <summary>
    /// Queues a unit if the building can make it, the team has reached the
    /// required tech tier, and it can pay. Costs are taken up front so a queue
    /// cannot be filled with resources the team does not have.
    /// </summary>
    private bool TryQueueUnit(int slot, ref Entity building, UnitKind kind)
    {
        if (!UnitCatalog.TryGet(kind, out UnitDefinition definition))
        {
            return false;
        }

        if (definition.ProducedAt != building.Kind || (uint)building.TeamId >= SimConstants.TeamCount)
        {
            return false;
        }

        ref TeamState team = ref _teams[building.TeamId];

        if (!CanBuild(building.TeamId, kind))
        {
            return false;
        }

        int materials = UnitCatalog.MaterialCost(building.Faction, kind);
        int energy = UnitCatalog.EnergyCost(building.Faction, kind);
        int water = UnitCatalog.WaterCost(building.Faction, kind);

        // Every job costs all three: minerals for the hull, power for the tools
        // and water for the cooling and the crews. Running one dry stops
        // construction rather than silently producing for free.
        if (team.Materials < materials || team.Energy < energy || team.Water < water)
        {
            return false;
        }

        int ticks = UnitCatalog.BuildTicks(building.Faction, kind);
        int production = team.ProductionPermille > 0 ? team.ProductionPermille : 1_000;
        ticks = Math.Max(1, (ticks * 1_000) / production);

        if (!AddJob(slot, kind, ticks))
        {
            return false;
        }

        team.Materials -= materials;
        team.Energy -= energy;
        team.Water -= water;
        return true;
    }

    /// <summary>Starts a research project at a design bureau.</summary>
    private bool TryStartResearch(ref Entity building, TechId tech)
    {
        if (building.Kind != UnitKind.DesignBureau || (uint)building.TeamId >= SimConstants.TeamCount)
        {
            return false;
        }

        if (!TechCatalog.TryGet(tech, out TechProject project) || project.Faction != building.Faction)
        {
            return false;
        }

        ref TeamState team = ref _teams[building.TeamId];

        if (team.IsResearching || TechCatalog.IsCompleted(team.TechMask, tech) || project.RequiredTier > team.TechTier)
        {
            return false;
        }

        if (project.Prerequisite != TechId.None && !TechCatalog.IsCompleted(team.TechMask, project.Prerequisite))
        {
            return false;
        }

        if (team.Materials < project.Cost)
        {
            return false;
        }

        team.Materials -= project.Cost;
        team.ResearchingTech = tech;
        team.ResearchTargetTier = project.RequiredTier;
        team.ResearchTicksTotal = TechCatalog.TicksFor(building.Faction, project);
        team.ResearchTicksRemaining = team.ResearchTicksTotal;
        return true;
    }

    private bool TryResolve(EntityId id, out int slot)
    {
        slot = id.Slot;

        if (id.IsNone || (uint)slot >= (uint)_entities.Length)
        {
            return false;
        }

        ref Entity e = ref _entities[slot];
        return e.Alive && e.Generation == id.Generation;
    }
}
