using System.Diagnostics;

namespace MiVic.Core.Sim;

/// <summary>
/// Coarse per-system timing for the simulation.
/// <para>
/// The renderer reports frame times, but a frame hitch can come from the tick
/// that happened inside it. This measures each system so a spike can be
/// attributed instead of guessed at. Timestamps are taken with
/// <see cref="Stopwatch"/>, which is fine for diagnostics but never influences
/// simulation state.
/// </para>
/// </summary>
public sealed class SimProfiler
{
    private long _mark;
    private long _stepStart;

    /// <summary>Ticks profiled since the last reset.</summary>
    public long Ticks;

    /// <summary>Time spent applying commands, in stopwatch ticks.</summary>
    public long Commands;

    /// <summary>Time spent in the AI.</summary>
    public long Ai;

    /// <summary>Time spent rebuilding the spatial index.</summary>
    public long Spatial;

    /// <summary>Time spent recomputing team visibility.</summary>
    public long Vision;

    /// <summary>Time spent on income.</summary>
    public long Economy;

    /// <summary>Time spent on research.</summary>
    public long Research;

    /// <summary>Time spent advancing production.</summary>
    public long Production;

    /// <summary>Time spent computing routes.</summary>
    public long Pathing;

    /// <summary>Time spent on morale.</summary>
    public long Morale;

    /// <summary>Time spent on combat.</summary>
    public long Combat;

    /// <summary>Time spent moving units.</summary>
    public long Movement;

    /// <summary>Longest single tick seen, in milliseconds.</summary>
    public double WorstStepMilliseconds { get; private set; }

    /// <summary>Tick number of the longest single tick.</summary>
    public long WorstStepTick { get; private set; }

    /// <summary>Longest single sample seen in each system, in stopwatch ticks.</summary>
    public long WorstCommands;
    public long WorstAi;
    public long WorstSpatial;
    public long WorstVision;
    public long WorstEconomy;
    public long WorstResearch;
    public long WorstProduction;
    public long WorstPathing;
    public long WorstMorale;
    public long WorstCombat;
    public long WorstMovement;

    /// <summary>Starts timing a tick.</summary>
    public void Begin()
    {
        _mark = Stopwatch.GetTimestamp();
        _stepStart = _mark;
    }

    /// <summary>Adds the time since the last mark to an accumulator and re-marks.</summary>
    public void Mark(ref long accumulator)
    {
        long now = Stopwatch.GetTimestamp();
        accumulator += now - _mark;
        _mark = now;
    }

    /// <summary>Adds the time since the last mark, also tracking the worst sample.</summary>
    public void Mark(ref long accumulator, ref long worst)
    {
        long now = Stopwatch.GetTimestamp();
        long delta = now - _mark;
        accumulator += delta;

        if (delta > worst)
        {
            worst = delta;
        }

        _mark = now;
    }

    /// <summary>Converts a stopwatch interval to milliseconds.</summary>
    public static double ToMilliseconds(long stopwatchTicks) => stopwatchTicks * 1000d / Stopwatch.Frequency;

    /// <summary>Finishes timing a tick.</summary>
    public void EndStep(long tick)
    {
        // Measure from the start of the tick, not from the last system mark.
        long now = Stopwatch.GetTimestamp();
        double milliseconds = (now - _stepStart) * 1000d / Stopwatch.Frequency;

        if (milliseconds > WorstStepMilliseconds)
        {
            WorstStepMilliseconds = milliseconds;
            WorstStepTick = tick;
        }

        Ticks++;
    }

    /// <summary>Converts an accumulator to average milliseconds per tick.</summary>
    public double MillisecondsPerTick(long accumulator)
        => Ticks == 0 ? 0d : accumulator * 1000d / Stopwatch.Frequency / Ticks;

    /// <summary>Clears every accumulator.</summary>
    public void Reset()
    {
        Commands = Ai = Spatial = Vision = Economy = Research = Production = Pathing = Morale = Combat = Movement = 0;
        Ticks = 0;
        WorstStepMilliseconds = 0d;
        WorstStepTick = 0;
    }
}
