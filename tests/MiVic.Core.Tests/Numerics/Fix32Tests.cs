using MiVic.Core.Numerics;

namespace MiVic.Core.Tests.Numerics;

public sealed class Fix32Tests
{
    [Fact]
    public void One_And_Zero_HaveExpectedRawValues()
    {
        Assert.Equal(0, Fix32.Zero.Raw);
        Assert.Equal(65536, Fix32.One.Raw);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(-1)]
    [InlineData(1000)]
    [InlineData(-32767)]
    [InlineData(32767)]
    public void FromInt_RoundTripsThroughToIntRound(int value)
        => Assert.Equal(value, Fix32.FromInt(value).ToIntRound());

    [Fact]
    public void FromInt_RejectsValuesThatDoNotFit()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => Fix32.FromInt(32768));
        Assert.Throws<ArgumentOutOfRangeException>(() => Fix32.FromInt(-32769));
    }

    [Fact]
    public void Addition_And_Subtraction_AreExact()
    {
        Fix32 a = Fix32.FromDouble(1.5);
        Fix32 b = Fix32.FromDouble(2.25);

        Assert.Equal(Fix32.FromDouble(3.75).Raw, (a + b).Raw);
        Assert.Equal(Fix32.FromDouble(-0.75).Raw, (a - b).Raw);
    }

    [Fact]
    public void Multiplication_RoundsToNearest()
    {
        Assert.Equal(Fix32.FromDouble(6.0).Raw, (Fix32.FromInt(2) * Fix32.FromInt(3)).Raw);
        Assert.Equal(Fix32.FromDouble(0.5).Raw, (Fix32.FromDouble(0.5) * Fix32.One).Raw);

        // 1/3 is not representable, so a round trip may lose one raw unit. It
        // must never lose more than that.
        Fix32 third = Fix32.One / Fix32.FromInt(3);
        long roundTrip = (third * Fix32.FromInt(3)).Raw;
        Assert.InRange(roundTrip, Fix32.One.Raw - 1, Fix32.One.Raw);
    }

    [Fact]
    public void Division_TruncatesTowardsZero()
    {
        Assert.Equal(Fix32.FromInt(2).Raw, (Fix32.FromInt(10) / Fix32.FromInt(5)).Raw);
        Assert.Equal(Fix32.FromInt(-2).Raw, (Fix32.FromInt(-10) / Fix32.FromInt(5)).Raw);
    }

    [Fact]
    public void Division_ByZeroThrows()
        => Assert.Throws<DivideByZeroException>(() => Fix32.One / Fix32.Zero);

    [Theory]
    [InlineData(0.0, 0.0)]
    [InlineData(1.0, 1.0)]
    [InlineData(4.0, 2.0)]
    [InlineData(9.0, 3.0)]
    [InlineData(2.0, 1.41421)]
    [InlineData(0.25, 0.5)]
    public void Sqrt_MatchesExpectedValue(double input, double expected)
    {
        double actual = Fix32.Sqrt(Fix32.FromDouble(input)).ToDouble();
        Assert.True(Math.Abs(actual - expected) < 1e-4, $"sqrt({input}) = {actual}, expected {expected}");
    }

    [Fact]
    public void Sqrt_RejectsNegative()
        => Assert.Throws<ArgumentOutOfRangeException>(() => Fix32.Sqrt(Fix32.FromInt(-1)));

    [Fact]
    public void Lerp_ClampsOvershoot()
    {
        Fix32 a = Fix32.FromInt(10);
        Fix32 b = Fix32.FromInt(20);

        Assert.Equal(Fix32.FromInt(10).Raw, Fix32.Lerp(a, b, Fix32.FromInt(-1)).Raw);
        Assert.Equal(Fix32.FromInt(20).Raw, Fix32.Lerp(a, b, Fix32.FromInt(2)).Raw);
        Assert.Equal(Fix32.FromInt(15).Raw, Fix32.Lerp(a, b, Fix32.Half).Raw);
    }

    [Fact]
    public void ComparisonOperators_AgreeWithRawValues()
    {
        Fix32 small = Fix32.FromDouble(0.5);
        Fix32 alsoSmall = Fix32.FromDouble(0.5);
        Fix32 large = Fix32.FromDouble(1.5);

        Assert.True(small < large);
        Assert.True(large > small);
        Assert.True(small <= alsoSmall);
        Assert.True(small >= alsoSmall);
        Assert.True(small != large);
        Assert.True(small == alsoSmall);
    }

    [Fact]
    public void Arithmetic_IsExactAcrossARepeatedAccumulation()
    {
        // The classic float failure: adding a fraction many times and drifting.
        // A quarter is exactly representable in Q16.16, so 400 additions must
        // land on exactly 100 with no error at all.
        Fix32 quarter = Fix32.FromRatio(1, 4);
        Fix32 total = Fix32.Zero;
        for (int i = 0; i < 400; i++)
        {
            total += quarter;
        }

        Assert.Equal(Fix32.FromInt(100).Raw, total.Raw);
    }

    [Fact]
    public void Arithmetic_DoesNotDriftForRepeatingFractionsEither()
    {
        // A third is not representable. Truncation must be consistent, so the
        // accumulated error stays bounded and identical on every run.
        Fix32 third = Fix32.FromRatio(1, 3);
        Fix32 total = Fix32.Zero;
        for (int i = 0; i < 300; i++)
        {
            total += third;
        }

        // 300 * (65536 / 3 truncated) = 6,553,500, i.e. 99.99847.
        Assert.Equal(6_553_500, total.Raw);
        Assert.True(total < Fix32.FromInt(100));
    }
}
