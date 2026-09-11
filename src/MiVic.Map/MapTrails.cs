using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Map;

/// <summary>
/// Where every unit has been, sampled over time.
/// <para>
/// The simulation does not keep this and does not need to: a trail is a fact about a stretch of
/// ticks rather than about the state of the world, and putting it in the world would put it in
/// the state hash. So the tool that draws the trail steps the world itself and samples as it
/// goes — <see cref="Advance"/> is called once per tick, from the same place the tick is run.
/// </para>
/// <para>
/// <b>Sampled, not recorded.</b> Every tick would be sixty samples a second of a unit that moves
/// a few centimetres between them, and the ticks in the trail would stop meaning anything. One
/// sample every <see cref="IntervalTicks"/> makes the spacing between them a reading of pace —
/// which is the whole point of drawing them rather than a plain line — and it makes a window of
/// <see cref="SampleCount"/> samples a useful span of game time.
/// </para>
/// <para>
/// A slot is recycled when its unit dies, so each sample carries the generation it was taken
/// under and a slot whose generation has moved starts again with an empty trail. Without that a
/// platoon that spawns into the slots of a platoon that died would be drawn walking the dead
/// one's march.
/// </para>
/// </summary>
public sealed class MapTrails
{
    /// <summary>Ticks between samples: half a second at the simulation's 20 Hz.</summary>
    public const int DefaultIntervalTicks = 10;

    /// <summary>Samples kept per slot: 64 samples is a little over half a minute of memory.</summary>
    public const int DefaultSampleCount = 64;

    /// <summary>
    /// Movement below which a unit holding a route counts as not moving, in millimetres, over
    /// <see cref="DefaultFrozenSamples"/> samples.
    /// </summary>
    public const int FrozenThresholdMm = 2_000;

    /// <summary>Samples over which the movement is measured: four seconds of game time.</summary>
    public const int DefaultFrozenSamples = 8;

    /// <summary>
    /// The share of the ground's own allowance a unit has to fall short of to count as frozen,
    /// as a divisor. A unit that has covered less than a quarter of what the surface under it
    /// allows is not moving, whatever the surface is.
    /// </summary>
    public const int FrozenShortfallDivisor = 4;

    /// <summary>
    /// Samples over which the unit must also be still <em>now</em>: one second. A unit that was
    /// standing and has just been given a route is a unit that is moving, and the question the
    /// layer asks is whether it is moving at the moment the picture was drawn.
    /// </summary>
    public const int RecentSamples = 2;

    private readonly int _interval;
    private readonly int _count;

    private WorldPos[] _samples = [];
    private int[] _cursor = [];
    private int[] _filled = [];
    private int[] _generation = [];
    private int _capacity;

    /// <param name="intervalTicks">Ticks between samples. Must be positive.</param>
    /// <param name="sampleCount">Samples kept per slot. Must be at least two.</param>
    public MapTrails(int intervalTicks = DefaultIntervalTicks, int sampleCount = DefaultSampleCount)
    {
        if (intervalTicks < 1)
        {
            throw new ArgumentOutOfRangeException(nameof(intervalTicks), intervalTicks, "Must be at least one tick.");
        }

        if (sampleCount < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(sampleCount), sampleCount, "At least two samples are needed to draw a line.");
        }

        _interval = intervalTicks;
        _count = sampleCount;
    }

    /// <summary>Samples kept per slot.</summary>
    public int SampleCount => _count;

    /// <summary>Ticks between samples.</summary>
    public int IntervalTicks => _interval;

    /// <summary>
    /// Samples the world if this tick is due one. Called once per tick, after it has been run,
    /// so a sample describes the world at the end of a tick rather than between two.
    /// </summary>
    public void Advance(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Tick % _interval != 0)
        {
            return;
        }

        EnsureCapacity(world.Capacity);

        for (int slot = 0; slot < _capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                _filled[slot] = 0;
                _generation[slot] = -1;
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (_generation[slot] != entity.Generation)
            {
                _generation[slot] = entity.Generation;
                _filled[slot] = 0;
                _cursor[slot] = 0;
            }

            _samples[(slot * _count) + _cursor[slot]] = entity.Position;
            _cursor[slot] = (_cursor[slot] + 1) % _count;

            if (_filled[slot] < _count)
            {
                _filled[slot]++;
            }
        }
    }

    /// <summary>Samples in hand for a slot, at most <see cref="SampleCount"/>; zero for an empty one.</summary>
    public int Filled(int slot) => (uint)slot < (uint)_capacity ? _filled[slot] : 0;

    /// <summary>
    /// Copies a slot's samples, oldest first, and returns how many were written. Oldest first is
    /// the order a line is drawn in, and the ring is why this copies rather than handing out a span.
    /// </summary>
    public int CopySamples(int slot, Span<WorldPos> destination)
    {
        if ((uint)slot >= (uint)_capacity || destination.Length == 0)
        {
            return 0;
        }

        int filled = Math.Min(_filled[slot], destination.Length);
        int oldest = (_cursor[slot] - filled + _count) % _count;

        for (int i = 0; i < filled; i++)
        {
            destination[i] = _samples[(slot * _count) + ((oldest + i) % _count)];
        }

        return filled;
    }

    /// <summary>
    /// The greatest distance between any of the last <paramref name="window"/> samples and the
    /// sample that opened the window: how far the unit has actually got, in millimetres, rather
    /// than how far its odometer has turned. Returns -1 when the window is not full, because a
    /// unit that spawned four samples ago has not been still and the honest answer is "unknown".
    /// </summary>
    public int NetMovementMm(int slot, int window)
    {
        if ((uint)slot >= (uint)_capacity || window < 2 || _filled[slot] < window)
        {
            return -1;
        }

        Span<WorldPos> buffer = stackalloc WorldPos[_count];
        int filled = CopySamples(slot, buffer);

        if (filled < window)
        {
            return -1;
        }

        WorldPos first = buffer[filled - window];
        int worst = 0;

        for (int i = filled - window + 1; i < filled; i++)
        {
            worst = Math.Max(worst, first.HorizontalDistanceTo(buffer[i]));
        }

        return worst;
    }

    /// <summary>
    /// How far a unit's ground would have let it walk over one window, in millimetres: what the
    /// movement system says it steps in a tick, times the ticks the window spans.
    /// <para>
    /// This is the denominator the diagnosis needs, and getting it from
    /// <see cref="MovementSystem.StepMmPerTick"/> rather than from the catalogue is the point: a
    /// catalogued speed is what a hull does on clear ground, and the question is whether the unit
    /// is moving <em>where it is</em>. A man on foot in deep mud steps 27 mm a tick and costs the
    /// surface 359 permille of his nominal speed, so a hundred and sixty millimetres a second is
    /// what that ground allows — and a unit doing exactly that is moving, however little it looks
    /// like it from above.
    /// </para>
    /// </summary>
    public int AllowedMm(SimWorld world, int slot, int window = DefaultFrozenSamples)
        => world.IsAliveSlot(slot) ? MovementSystem.StepMmPerTick(world, slot) * window * _interval : 0;

    /// <summary>
    /// Whether a unit is holding a route and going nowhere along it.
    /// <para>
    /// This is the reading the whole trajectory layer exists for — a drawn path with no ground
    /// behind it — and it is asked here rather than guessed at from a picture so that the census
    /// in the transcript and the mark on the map are the same number. A unit with a route whose
    /// window is not yet full is not frozen; it is unknown, and it is counted as neither.
    /// </para>
    /// <para>
    /// <b>Three conditions, and the second and third were put there by looking at a picture.</b>
    /// An absolute threshold alone called twelve Chinese infantrymen frozen on a skirmish at tick
    /// 600 — men crossing mud at 27 mm a tick, which is every millimetre their ground allows them,
    /// walking at 278 permille of their own speed because the surface says so. They had moved 1.9
    /// metres in four seconds against a threshold of 2, and the map was reporting a stall that was
    /// the <em>rasputitsa</em>. So the shortfall is measured against what the movement system would
    /// have given them. That left six more, who were units that had been stalled and had just been
    /// handed a route: they had walked a metre and a half of the last four seconds and would walk
    /// twelve in the next six, so the window said "not moving" about units that were, at that
    /// moment, moving. Hence the second window: the unit must be still over the wide window
    /// <em>and</em> still over the last second, which is the difference between a unit that is not
    /// going anywhere and a unit that has only just started to.
    /// </para>
    /// </summary>
    public bool IsFrozen(
        SimWorld world,
        int slot,
        bool holdingRoute,
        int window = DefaultFrozenSamples,
        int recent = RecentSamples)
    {
        if (!holdingRoute)
        {
            return false;
        }

        int moved = NetMovementMm(slot, window);
        int now = NetMovementMm(slot, recent);

        // A unit whose window is not full is unknown rather than frozen, and says so by refusing
        // to answer either way: a platoon that spawned four seconds ago has not been still.
        if (moved < 0 || now < 0 || moved >= FrozenThresholdMm)
        {
            return false;
        }

        int allowed = AllowedMm(world, slot, window);
        int allowedNow = AllowedMm(world, slot, recent);

        return allowed > 0
            && (moved * FrozenShortfallDivisor) < allowed
            && (allowedNow <= 0 || (now * FrozenShortfallDivisor) < allowedNow);
    }

    private void EnsureCapacity(int capacity)
    {
        if (capacity <= _capacity)
        {
            return;
        }

        _samples = new WorldPos[capacity * _count];
        _cursor = new int[capacity];
        _filled = new int[capacity];
        _generation = new int[capacity];

        Array.Fill(_generation, -1);
        _capacity = capacity;
    }
}
