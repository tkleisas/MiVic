using MiVic.Core.Numerics;

namespace MiVic.Core.Tests.Numerics;

public sealed class IntMathTests
{
    [Theory]
    [InlineData(0UL, 0UL)]
    [InlineData(1UL, 1UL)]
    [InlineData(2UL, 1UL)]
    [InlineData(3UL, 1UL)]
    [InlineData(4UL, 2UL)]
    [InlineData(8UL, 2UL)]
    [InlineData(9UL, 3UL)]
    [InlineData(15UL, 3UL)]
    [InlineData(16UL, 4UL)]
    [InlineData(1_000_000UL, 1000UL)]
    [InlineData(999_999UL, 999UL)]
    [InlineData(4_294_967_296UL, 65_536UL)]
    public void SqrtLong_TruncatesTowardsZero(ulong input, ulong expected)
        => Assert.Equal(expected, IntMath.SqrtLong(input));

    [Fact]
    public void SqrtLong_IsExactForPerfectSquares()
    {
        for (ulong n = 0; n < 5000; n++)
        {
            Assert.Equal(n, IntMath.SqrtLong(n * n));
            if (n > 0)
            {
                Assert.Equal(n - 1, IntMath.SqrtLong((n * n) - 1));
            }
        }
    }

    [Fact]
    public void SqrtLong_RejectsNegativeAndOverflow()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.SqrtLong(-1L));
        Assert.Throws<ArgumentOutOfRangeException>(() => IntMath.SqrtLong(IntMath.MaxSqrtInput + 1));
    }

    [Theory]
    [InlineData(10, 3, 3)]
    [InlineData(-10, 3, -3)]
    [InlineData(7, 2, 4)]
    [InlineData(-7, 2, -4)]
    [InlineData(0, 5, 0)]
    public void DivRound_RoundsHalfAwayFromZero(long numerator, long denominator, int expected)
        => Assert.Equal(expected, IntMath.DivRound(numerator, denominator));

    [Fact]
    public void DivRound_RejectsZeroDenominator()
        => Assert.Throws<DivideByZeroException>(() => IntMath.DivRound(1, 0));

    [Fact]
    public void Abs_HandlesMinValueWithoutOverflow()
    {
        Assert.Equal(5, IntMath.Abs(5));
        Assert.Equal(5, IntMath.Abs(-5));
        Assert.Equal(int.MaxValue, IntMath.Abs(int.MinValue));
    }

    [Theory]
    [InlineData(5, 0, 10, 5)]
    [InlineData(-5, 0, 10, 0)]
    [InlineData(15, 0, 10, 10)]
    public void Clamp_ConstrainsToRange(int value, int min, int max, int expected)
        => Assert.Equal(expected, IntMath.Clamp(value, min, max));

    [Fact]
    public void Clamp_RejectsInvertedRange()
        => Assert.Throws<ArgumentException>(() => IntMath.Clamp(1, 10, 0));

    [Fact]
    public void Distance_MatchesPythagorasOnAxisAlignedOffsets()
    {
        Assert.Equal(0, IntMath.Distance(0, 0, 0));
        Assert.Equal(5, IntMath.Distance(5, 0, 0));
        Assert.Equal(5, IntMath.Distance(0, -5, 0));
        Assert.Equal(13, IntMath.Distance(3, 4, 12));
    }

    [Fact]
    public void Distance_DoesNotOverflowAtMapScale()
    {
        // 2 km map, full diagonal in millimetres.
        int delta = 2_000_000;
        int d = IntMath.Distance(delta, delta, delta);
        Assert.Equal(3_464_101, d);
    }
}
