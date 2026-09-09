namespace MiVic.Core.Sim;

/// <summary>
/// Converts real elapsed time into a whole number of fixed simulation ticks.
/// <para>
/// The accumulator is integer microseconds rather than a float, so a given
/// sequence of frame times always produces the same tick schedule — required for
/// replay and lockstep determinism.
/// </para>
/// </summary>
public sealed class SimClock
{
    private long _accumulatedMicroseconds;

    /// <summary>Number of ticks that have been issued since construction.</summary>
    public long CurrentTick { get; private set; }

    /// <summary>Maximum catch-up ticks per call. Defaults to <see cref="SimConstants.MaxTicksPerAdvance"/>.</summary>
    public int MaxTicksPerAdvance { get; init; } = SimConstants.MaxTicksPerAdvance;

    /// <summary>Total real time discarded by the catch-up cap. Non-zero means the sim is starved.</summary>
    public long DroppedMicroseconds { get; private set; }

    /// <summary>
    /// Fraction of the way into the next tick, in [0, 1). The renderer uses this
    /// to interpolate entity positions so motion looks smooth at any frame rate.
    /// </summary>
    public double InterpolationAlpha
    {
        get
        {
            double alpha = (double)_accumulatedMicroseconds / SimConstants.TickMicroseconds;
            return alpha is >= 0d and < 1d ? alpha : 0d;
        }
    }

    /// <summary>Leftover time not yet consumed by a tick, in microseconds.</summary>
    public long PendingMicroseconds => _accumulatedMicroseconds;

    /// <summary>
    /// Adds elapsed real time and returns how many ticks should now be run.
    /// Negative or zero elapsed time is ignored.
    /// </summary>
    public int Advance(long elapsedMicroseconds)
    {
        if (elapsedMicroseconds > 0)
        {
            _accumulatedMicroseconds += elapsedMicroseconds;
        }

        int ticks = 0;
        while (_accumulatedMicroseconds >= SimConstants.TickMicroseconds && ticks < MaxTicksPerAdvance)
        {
            _accumulatedMicroseconds -= SimConstants.TickMicroseconds;
            ticks++;
        }

        // If the cap stopped us, drop the backlog instead of letting it grow.
        if (_accumulatedMicroseconds >= SimConstants.TickMicroseconds)
        {
            long excess = _accumulatedMicroseconds - (SimConstants.TickMicroseconds - 1);
            _accumulatedMicroseconds -= excess;
            DroppedMicroseconds += excess;
        }

        CurrentTick += ticks;
        return ticks;
    }

    /// <summary>Issues ticks directly, for headless runs and tests.</summary>
    public void AdvanceTicks(long ticks)
    {
        if (ticks < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(ticks), ticks, "Must not be negative.");
        }

        CurrentTick += ticks;
    }

    /// <summary>Clears the accumulator and tick counter, e.g. when loading a replay.</summary>
    public void Reset()
    {
        _accumulatedMicroseconds = 0;
        DroppedMicroseconds = 0;
        CurrentTick = 0;
    }
}
