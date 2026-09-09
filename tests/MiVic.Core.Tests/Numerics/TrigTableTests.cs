using MiVic.Core.Numerics;

namespace MiVic.Core.Tests.Numerics;

public sealed class TrigTableTests
{
    [Fact]
    public void Atan2Brads_MatchesCardinalDirections()
    {
        Assert.Equal(0, TrigTable.Atan2Brads(0, 1));
        Assert.Equal(16384, TrigTable.Atan2Brads(1, 0));
        Assert.Equal(32768, TrigTable.Atan2Brads(0, -1));
        Assert.Equal(49152, TrigTable.Atan2Brads(-1, 0));
    }

    [Fact]
    public void Atan2Brads_ZeroVectorIsZero()
        => Assert.Equal(0, TrigTable.Atan2Brads(0, 0));

    [Fact]
    public void Atan2Brads_IsConsistentUnderScaling()
    {
        // Direction is scale invariant, so these must agree exactly.
        Assert.Equal(TrigTable.Atan2Brads(3, 4), TrigTable.Atan2Brads(3000, 4000));
        Assert.Equal(TrigTable.Atan2Brads(-7, 2), TrigTable.Atan2Brads(-700_000, 200_000));
    }

    [Fact]
    public void Atan2Brads_OppositeVectorsDifferByHalfATurn()
    {
        (int Dz, int Dx)[] samples = [(1, 1), (5, 2), (-3, 8), (-9, -4), (0, 6), (7, 0), (1000, 7)];

        foreach ((int dz, int dx) in samples)
        {
            int forward = TrigTable.Atan2Brads(dz, dx);
            int backward = TrigTable.Atan2Brads(-dz, -dx);
            Assert.Equal(32768, (backward - forward + 65536) & 0xFFFF);
        }
    }

    [Fact]
    public void Atan2Brads_ApproximatesTrueAtan2WithinOneDegree()
    {
        for (int i = 0; i < 360; i++)
        {
            double radians = i * Math.PI / 180.0;
            int dx = (int)Math.Round(Math.Cos(radians) * 100_000);
            int dz = (int)Math.Round(Math.Sin(radians) * 100_000);

            double expected = Math.Atan2(dz, dx);
            if (expected < 0)
            {
                expected += 2 * Math.PI;
            }

            int expectedBrads = (int)Math.Round(expected * 65536.0 / (2 * Math.PI));
            int actual = TrigTable.Atan2Brads(dz, dx);
            int delta = Math.Abs(((actual - expectedBrads + 32768) & 0xFFFF) - 32768);

            Assert.True(delta <= 183, $"angle {i} deg: got {actual}, expected {expectedBrads}, delta {delta}");
        }
    }

    [Fact]
    public void SinBrads_HitsTheAxisValuesExactly()
    {
        Assert.Equal(0, TrigTable.SinBrads(0).Raw);
        Assert.Equal(65536, TrigTable.SinBrads(16384).Raw);
        Assert.Equal(0, TrigTable.SinBrads(32768).Raw);
        Assert.Equal(-65536, TrigTable.SinBrads(49152).Raw);
    }

    [Fact]
    public void SinBrads_MatchesSineWithinAFixedPointStep()
    {
        for (int i = 0; i < 360; i += 7)
        {
            double radians = i * Math.PI / 180.0;
            int brads = (int)(radians * 65536.0 / (2 * Math.PI));
            double expected = Math.Sin(radians);
            double actual = TrigTable.SinBrads(brads).ToDouble();

            Assert.True(Math.Abs(actual - expected) < 0.002, $"sin({i} deg): {actual} vs {expected}");
        }
    }

    [Fact]
    public void CosBrads_MatchesSineShiftedByAQuarterTurn()
    {
        Assert.Equal(65536, TrigTable.CosBrads(0).Raw);
        Assert.Equal(0, TrigTable.CosBrads(16384).Raw);
        Assert.Equal(-65536, TrigTable.CosBrads(32768).Raw);
    }

    [Fact]
    public void SinBrads_WrapsAroundTheFullTurn()
    {
        Assert.Equal(TrigTable.SinBrads(1000).Raw, TrigTable.SinBrads(1000 + 65536).Raw);
        Assert.Equal(TrigTable.SinBrads(-1000).Raw, TrigTable.SinBrads(65536 - 1000).Raw);
    }

    /// <summary>
    /// The tables are built once from <see cref="Math.Atan"/> and
    /// <see cref="Math.Sin"/> and rounded to integers. This golden value fails
    /// loudly if a platform ever rounds them differently, which would otherwise
    /// show up as an unexplained multiplayer desync.
    /// </summary>
    [Fact]
    public void InitializationHash_IsStable()
        => Assert.Equal(11609425135955895156UL, TrigTable.InitializationHash);
}
