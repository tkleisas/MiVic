using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class SimClockTests
{
    [Fact]
    public void TickConstants_AreConsistent()
    {
        Assert.Equal(20, SimConstants.TickRate);
        Assert.Equal(50, SimConstants.TickMilliseconds);
        Assert.Equal(50_000, SimConstants.TickMicroseconds);
    }

    [Fact]
    public void Advance_IssuesOneTickPerTickDuration()
    {
        SimClock clock = new();

        Assert.Equal(0, clock.Advance(49_999));
        Assert.Equal(1, clock.Advance(1));
        Assert.Equal(1, clock.Advance(50_000));
        Assert.Equal(3, clock.Advance(150_000));
        Assert.Equal(5, clock.CurrentTick);
    }

    [Fact]
    public void Advance_AccumulatesSubTickFramesExactly()
    {
        // 1000 frames of 3 ms is exactly 3 seconds: 60 ticks, no drift.
        SimClock clock = new();
        long issued = 0;

        for (int i = 0; i < 1000; i++)
        {
            issued += clock.Advance(3_000);
        }

        Assert.Equal(60, issued);
        Assert.Equal(60, clock.CurrentTick);
        Assert.Equal(0, clock.PendingMicroseconds);
    }

    [Fact]
    public void Advance_IgnoresZeroAndNegativeElapsed()
    {
        SimClock clock = new();

        Assert.Equal(0, clock.Advance(0));
        Assert.Equal(0, clock.Advance(-5_000));
        Assert.Equal(0, clock.CurrentTick);
    }

    [Fact]
    public void Advance_CapsCatchUpAndRecordsTheDroppedTime()
    {
        SimClock clock = new();

        // One second of stall would be 20 ticks; the cap is 5.
        int ticks = clock.Advance(1_000_000);

        Assert.Equal(SimConstants.MaxTicksPerAdvance, ticks);
        Assert.True(clock.DroppedMicroseconds > 0);
        Assert.True(clock.PendingMicroseconds < SimConstants.TickMicroseconds);
    }

    [Fact]
    public void InterpolationAlpha_StaysInUnitInterval()
    {
        SimClock clock = new();

        clock.Advance(25_000);
        Assert.Equal(0.5, clock.InterpolationAlpha, 6);

        clock.Advance(24_999);
        Assert.InRange(clock.InterpolationAlpha, 0.0, 1.0);

        clock.Advance(50_000);
        Assert.InRange(clock.InterpolationAlpha, 0.0, 1.0);
    }

    [Fact]
    public void Reset_ClearsEverything()
    {
        SimClock clock = new();
        clock.Advance(1_000_000);
        clock.Reset();

        Assert.Equal(0, clock.CurrentTick);
        Assert.Equal(0, clock.PendingMicroseconds);
        Assert.Equal(0, clock.DroppedMicroseconds);
    }

    [Fact]
    public void AdvanceTicks_RejectsNegative()
    {
        SimClock clock = new();
        Assert.Throws<ArgumentOutOfRangeException>(() => clock.AdvanceTicks(-1));
        clock.AdvanceTicks(7);
        Assert.Equal(7, clock.CurrentTick);
    }

    [Fact]
    public void Advance_IsReproducibleForTheSameFrameTimeSequence()
    {
        long[] frameTimes = [16_667, 16_667, 33_333, 8_000, 16_667, 100_000, 16_667];

        SimClock a = new();
        SimClock b = new();

        foreach (long frame in frameTimes)
        {
            Assert.Equal(a.Advance(frame), b.Advance(frame));
        }

        Assert.Equal(a.CurrentTick, b.CurrentTick);
        Assert.Equal(a.PendingMicroseconds, b.PendingMicroseconds);
    }
}
