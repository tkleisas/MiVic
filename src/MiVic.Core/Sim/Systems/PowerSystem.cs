namespace MiVic.Core.Sim;

/// <summary>
/// Which structures a team's generation can actually run, and therefore which radars
/// are turning.
/// <para>
/// <b>This is the first continuous draw in the game, and the first brown-out.</b>
/// Until now a structure's energy was an income: a factory "cost" four energy a tick
/// and a power plant produced ten, both of them folded into one signed rate and paid
/// out of the stockpile, and nothing ever ran out of anything. What that model cannot
/// express is a structure that is <em>switched on</em> — a radar does not make the
/// team poorer, it occupies generation, and a base with more load than capacity has to
/// shed something.
/// </para>
/// <para>
/// So the ledger here is capacity, not money: <see cref="TeamState.PowerGeneration"/>
/// against <see cref="TeamState.PowerDraw"/>, both recomputed from the structures a team
/// owns on every tick. Nothing is stored that a replay could disagree about, which is
/// why none of it is hashed — the state hash covers the stockpile in
/// <see cref="TeamState.Energy"/>, and this is a pure function of which buildings are
/// standing, exactly like <see cref="TeamState.EnergyPerTick"/> beside it and for the
/// same reason.
/// </para>
/// <para>
/// <b>Detection is shed first, and that is the whole point.</b> A deficit does not slow
/// a factory and does not silence a gun — those are later brown-out steps and are
/// deliberately not built. What it does is take radars off the air, lowest slot first,
/// which is to say oldest first. A strike on a base's generation therefore does not
/// merely slow its factories: it collapses the reach of every gun in the line, because
/// a gun can only shoot as far as something can see for it. That is the loop this
/// system exists to close.
/// </para>
/// </summary>
public static class PowerSystem
{
    /// <summary>
    /// Energy a radar station occupies while it is running, per tick. The same figure a
    /// factory occupies, because a radar is the same size of load: an active set, a
    /// generator set to back it, and a crew.
    /// </summary>
    public const int RadarDraw = 4;

    /// <summary>
    /// Generation a command centre provides for nothing, from the standby set every
    /// headquarters is built with.
    /// <para>
    /// It is six so that a bare base can run exactly one thing that matters — a factory
    /// (four) or a radar (four) — and no more. That is the rule that keeps a lost power
    /// plant from being a lost game: a team that has just had its generation bombed flat
    /// still has a headquarters, still has materials and water coming in, and can still
    /// raise the power plant that fixes it. It is deliberately <em>not</em> scaled by the
    /// faction's income multiplier, unlike the plants: a standby set runs the lights, it
    /// is not an economic output, and a Δυτικοί headquarters that exported power at two
    /// and a half times the rate of a Σοβιετικοί one would be an accident of the wealth
    /// table rather than a design decision.
    /// </para>
    /// </summary>
    public const int CommandCentreStandby = 6;

    /// <summary>
    /// Ranks a structure's load in the ledger. A structure that is not in the table draws
    /// nothing, which is the honest answer for a building with no moving parts: a
    /// Πυροβολείο is a concrete pit with a gun in it and does not need the grid to fire,
    /// and giving it a load would have made every defensive line a power problem before
    /// the radar — the one building whose load is the point — ever reached the field.
    /// <para>
    /// It is public because the figure is not only this ledger's business: the same four energy a
    /// factory occupies here is the four the economy charges its rate, and a buyer deciding whether
    /// it can afford another yard has to ask about it — see <c>AiSystem.TryRaiseCapacity</c>, which
    /// buys the generation before the load rather than freezing its own queue with it.
    /// </para>
    /// </summary>
    public static int DrawOf(UnitKind kind) => kind switch
    {
        UnitKind.Factory => 4,
        UnitKind.DesignBureau => 3,
        UnitKind.RadarStation => RadarDraw,
        _ => 0,
    };

    /// <summary>Generation a structure contributes, before the faction's income multiplier.</summary>
    private static int GenerationOf(UnitKind kind) => kind switch
    {
        UnitKind.PowerPlant => EconomySystem.PowerPlantEnergy,
        UnitKind.NuclearPlant => EconomySystem.NuclearPlantEnergy,
        _ => 0,
    };

    /// <summary>
    /// Recomputes every team's power position: how much it generates, how much it draws,
    /// and which of its radars are lit.
    /// <para>
    /// Runs before <see cref="VisionSystem"/> in the tick, because the vision system stamps
    /// a radar's disc and a dark radar stamps nothing. Radars are lit in ascending slot
    /// order — oldest first — while generation remains, so the answer is a function of the
    /// world and not of the order a container happened to hand things over in: two peers
    /// that built the same three buildings run the same one of them.
    /// </para>
    /// </summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        RadarNetwork radars = world.Radars;
        int capacity = world.Capacity;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            ref TeamState state = ref world.TeamRef(team);

            int generation = 0;
            int load = 0;
            int radarCount = 0;

            // First pass: everything that is not a radar. The load a radar must fit
            // inside is what the rest of the base has already taken, which is what makes
            // detection the first thing shed rather than the last.
            for (int slot = 0; slot < capacity; slot++)
            {
                if (!IsPoweredStructure(world, slot, team, out UnitKind kind))
                {
                    continue;
                }

                generation += GenerationOf(kind);

                if (kind == UnitKind.RadarStation)
                {
                    radarCount++;
                    continue;
                }

                load += DrawOf(kind);
            }

            generation += CommandCentreStandby;

            // Second pass: the radars, in ascending slot order, while there is room for
            // them. A radar that does not fit draws nothing, which is both true — it is
            // off — and necessary: a dark radar that still counted against the ledger
            // would keep itself dark for ever, and one that flickered on would flicker.
            int remaining = generation - load;
            int lit = 0;

            radars.BeginTeam(team, radarCount, generation, load);

            for (int slot = 0; slot < capacity; slot++)
            {
                if (!IsPoweredStructure(world, slot, team, out UnitKind kind) || kind != UnitKind.RadarStation)
                {
                    continue;
                }

                if (remaining >= RadarDraw)
                {
                    remaining -= RadarDraw;
                    load += RadarDraw;
                    lit++;
                    radars.AddLit(team, slot);
                }
            }

            state.PowerGeneration = generation;
            state.PowerDraw = load;
            state.RadarsLit = lit;
            state.RadarsDark = radarCount - lit;
            state.PowerShortfall = state.RadarsDark == 0
                ? 0
                : ((state.RadarsDark * RadarDraw) - Math.Max(0, remaining));
        }
    }

    /// <summary>
    /// Why a team's radars are dark, in the game's own Greek, or an empty string when they
    /// are not. It lives in the simulation rather than in the client for the same reason
    /// <see cref="UnitCatalog.GreekName"/> and the refusals in <see cref="SimWorld"/> do: a
    /// reason assembled in the interface would be the one sentence in the player's own
    /// language that no test could reach.
    /// </summary>
    public static string DimmedReason(in TeamState state)
        => state.RadarsDark == 0
            ? string.Empty
            : $"λείπει ισχύς {state.PowerShortfall} Ε";

    /// <summary>
    /// True when a team could add one more radar station to what it has and still run it.
    /// <para>
    /// <b>The question a buyer has to ask, answered from the table the dishes are lit from.</b>
    /// A radar is the only structure in the game whose price is not its whole cost: it also
    /// occupies generation for as long as it is on, and a radar that does not fit is not a
    /// weak radar, it is a dark one — the team has paid for a building that does nothing and
    /// that takes a power plant to redeem. So whoever is deciding whether to buy one asks
    /// here, of <see cref="DrawOf"/> and <see cref="GenerationOf"/> rather than of a second
    /// copy of them, and gets the same answer the lighting pass gives.
    /// </para>
    /// <para>
    /// <b>Everything the team has committed to counts, including a building site.</b> The
    /// question is what the ledger will be once the work standing on the ground is finished:
    /// a generation that counted only finished plants would let a caller order a radar on the
    /// strength of a power plant that is still a hole in the ground, and shed the dish on the
    /// tick it came up. A radar already committed counts as load for the same reason — the
    /// answer is "one more than what I have", not "one".
    /// </para>
    /// <para>
    /// What is <em>not</em> counted is a structure still in a production queue: it has not
    /// been committed to the ground and has no site yet. A caller that has one coming asks
    /// about its own queue as well — see <c>AiSystem.TryQueuePowerPlant</c>, which does
    /// exactly that before ordering a second plant.
    /// </para>
    /// </summary>
    public static bool HasRoomForRadar(SimWorld world, int team)
    {
        ArgumentNullException.ThrowIfNull(world);

        if ((uint)team >= SimConstants.TeamCount)
        {
            return false;
        }

        int generation = CommandCentreStandby;
        int load = 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!IsCommittedStructure(world, slot, team, out UnitKind kind))
            {
                continue;
            }

            generation += GenerationOf(kind);
            load += DrawOf(kind);
        }

        return generation - load >= RadarDraw;
    }

    /// <summary>
    /// True when a slot is a structure of a team — finished, or still being raised. The
    /// difference from <see cref="IsPoweredStructure"/> is the tense: the lighting pass asks
    /// what is switched on now, and a projection asks what the team has already paid for.
    /// </summary>
    private static bool IsCommittedStructure(SimWorld world, int slot, int team, out UnitKind kind)
    {
        kind = UnitKind.None;

        if (!world.IsAliveSlot(slot))
        {
            return false;
        }

        ref Entity entity = ref world.GetRefBySlot(slot);

        if (entity.TeamId != team)
        {
            return false;
        }

        kind = entity.Kind;
        return UnitCatalog.Get(kind).IsBuilding;
    }

    /// <summary>
    /// True when a slot is a finished, working structure of a team. A building site is
    /// not: it produces nothing, draws nothing and watches nothing until it is up, which
    /// is the same rule the economy and the guns already follow.
    /// </summary>
    private static bool IsPoweredStructure(SimWorld world, int slot, int team, out UnitKind kind)
    {
        kind = UnitKind.None;

        if (!world.IsAliveSlot(slot))
        {
            return false;
        }

        ref Entity entity = ref world.GetRefBySlot(slot);

        if (entity.TeamId != team || entity.ConstructionTicksRemaining > 0)
        {
            return false;
        }

        kind = entity.Kind;
        return UnitCatalog.Get(kind).IsBuilding;
    }

    /// <summary>
    /// Where every lit radar stands, one team at a time, rebuilt each tick.
    /// <para>
    /// A flat array of slots rather than a list per team, so that asking "is this point
    /// under a radar" in the combat loop allocates nothing and iterates a handful of
    /// integers. Only lit radars are in it: the coverage a dark radar defines is the
    /// coverage nobody has.
    /// </para>
    /// </summary>
    public sealed class RadarNetwork
    {
        private readonly int[] _slots;
        private readonly int[] _count = new int[SimConstants.TeamCount];
        private readonly int[] _generation = new int[SimConstants.TeamCount];
        private readonly int[] _load = new int[SimConstants.TeamCount];
        private readonly int _stride;

        /// <summary>Creates a network sized for a world's entity capacity.</summary>
        public RadarNetwork(int capacity)
        {
            _stride = capacity;
            _slots = new int[capacity * SimConstants.TeamCount];
        }

        /// <summary>Number of lit radars on a team.</summary>
        public int Count(int team) => _count[team];

        /// <summary>The slot of a team's nth lit radar, in ascending slot order.</summary>
        public int SlotAt(int team, int index) => _slots[(team * _stride) + index];

        /// <summary>
        /// True when one particular radar is on the air. Used by the client to decide
        /// whether the dish is still turning, so it asks the same question the sim
        /// answered rather than re-deriving it from the energy stockpile.
        /// </summary>
        public bool IsLit(int team, int slot)
        {
            if ((uint)team >= SimConstants.TeamCount)
            {
                return false;
            }

            int count = _count[team];
            int offset = team * _stride;

            for (int i = 0; i < count; i++)
            {
                if (_slots[offset + i] == slot)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Clears a team's radars and records the ledger the lighting was decided from.</summary>
        internal void BeginTeam(int team, int capacity, int generation, int load)
        {
            _count[team] = 0;
            _generation[team] = generation;
            _load[team] = load;
        }

        /// <summary>Records a radar as lit.</summary>
        internal void AddLit(int team, int slot)
        {
            if (_count[team] < _stride)
            {
                _slots[(team * _stride) + _count[team]] = slot;
            }

            _count[team]++;
        }

        /// <summary>
        /// True when a position is inside the coverage of a lit radar of this team.
        /// <para>
        /// This is what a radar's coverage <em>is</em>: a disc of
        /// <see cref="VisionSystem.RadarCoverageMm"/> around each station that has power.
        /// The vision system stamps the same disc into the fog, so the ground a gun can
        /// shoot across and the ground the player can see are the same ground by
        /// construction, not by two rules being kept in step.
        /// </para>
        /// </summary>
        public bool Covers(SimWorld world, int team, Numerics.WorldPos position)
        {
            if ((uint)team >= SimConstants.TeamCount)
            {
                return false;
            }

            long radiusSquared = (long)VisionSystem.RadarCoverageMm * VisionSystem.RadarCoverageMm;
            int count = _count[team];
            int offset = team * _stride;

            for (int i = 0; i < count; i++)
            {
                int slot = _slots[offset + i];

                if (!world.IsAliveSlot(slot))
                {
                    continue;
                }

                int dx = world.GetRefBySlot(slot).Position.X - position.X;
                int dz = world.GetRefBySlot(slot).Position.Z - position.Z;

                // Horizontally, like range and like the disc the fog is stamped from: a
                // radar on a ridge covers the valley under it, and a gun in the valley is
                // under the umbrella whether or not it is standing at the same height.
                if (((long)dx * dx) + ((long)dz * dz) <= radiusSquared)
                {
                    return true;
                }
            }

            return false;
        }

        /// <summary>Generation a team had when its radars were last lit, for the interface.</summary>
        public int Generation(int team) => _generation[team];

        /// <summary>Load a team had when its radars were last lit, for the interface.</summary>
        public int Load(int team) => _load[team];
    }
}
