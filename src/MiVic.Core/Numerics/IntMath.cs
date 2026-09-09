namespace MiVic.Core.Numerics;

/// <summary>
/// Integer-only maths helpers used by the simulation.
/// <para>
/// Every method here is exact and deterministic on any runtime and any CPU.
/// Floating point must never appear in simulation code: a single ULP of drift
/// between two machines desynchronises a lockstep game irrecoverably.
/// </para>
/// </summary>
public static class IntMath
{
    /// <summary>
    /// Largest input <see cref="SqrtLong"/> accepts. The intermediate
    /// <c>x + value / x</c> must not overflow <see cref="ulong"/>.
    /// </summary>
    public const ulong MaxSqrtInput = (1UL << 62) - 1;

    /// <summary>Integer square root, truncated towards zero. Deterministic.</summary>
    public static ulong SqrtLong(ulong value)
    {
        if (value == 0)
        {
            return 0;
        }

        if (value > MaxSqrtInput)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Input exceeds MaxSqrtInput.");
        }

        // Newton's method on integers. Converges monotonically from above.
        ulong x = value;
        ulong y = (x + 1) >> 1;

        while (y < x)
        {
            x = y;
            y = (x + (value / x)) >> 1;
        }

        return x;
    }

    /// <summary>Integer square root of a non-negative <see cref="long"/>.</summary>
    public static long SqrtLong(long value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot take the square root of a negative value.");
        }

        return (long)SqrtLong((ulong)value);
    }

    /// <summary>Integer square root of a non-negative <see cref="int"/>.</summary>
    public static int Sqrt(int value)
    {
        if (value < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot take the square root of a negative value.");
        }

        return (int)SqrtLong((ulong)value);
    }

    /// <summary>Absolute value that is correct for <see cref="int.MinValue"/>.</summary>
    public static int Abs(int value) => value >= 0 ? value : (value == int.MinValue ? int.MaxValue : -value);

    /// <summary>Clamps <paramref name="value"/> into the inclusive range.</summary>
    public static int Clamp(int value, int min, int max)
    {
        if (min > max)
        {
            throw new ArgumentException("min must not be greater than max.", nameof(min));
        }

        return value < min ? min : value > max ? max : value;
    }

    /// <summary>
    /// Rounds <paramref name="numerator"/> / <paramref name="denominator"/> to the
    /// nearest integer, halves away from zero. Used where a rounded quotient is
    /// part of the simulation contract.
    /// </summary>
    public static int DivRound(long numerator, long denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException();
        }

        long half = denominator / 2;
        return numerator >= 0
            ? (int)((numerator + half) / denominator)
            : (int)((numerator - half) / denominator);
    }

    /// <summary>Sign of <paramref name="value"/> as -1, 0 or 1.</summary>
    public static int Sign(int value) => value > 0 ? 1 : value < 0 ? -1 : 0;

    /// <summary>Multiplies two non-negative <see cref="int"/> values without overflowing.</summary>
    public static long MulLong(int a, int b) => (long)a * b;

    /// <summary>
    /// Distance between two points in millimetres, using 64-bit intermediates so
    /// that world-scale coordinates (up to a few kilometres) never overflow.
    /// </summary>
    public static int Distance(int dx, int dy, int dz)
    {
        long sq = ((long)dx * dx) + ((long)dy * dy) + ((long)dz * dz);
        return (int)SqrtLong((ulong)sq);
    }
}
