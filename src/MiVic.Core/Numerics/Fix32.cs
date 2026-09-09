namespace MiVic.Core.Numerics;

/// <summary>
/// Q16.16 fixed-point number backed by an <see cref="int"/>.
/// <para>
/// Range is [-32768.0, +32767.99998] with a resolution of 1/65536.
/// Multiplication rounds to nearest (halves away from zero); division truncates
/// towards zero. Both rules are part of the simulation contract and must not be
/// changed without regenerating the determinism golden hashes.
/// </para>
/// </summary>
public readonly struct Fix32 : IEquatable<Fix32>, IComparable<Fix32>
{
    /// <summary>Number of fractional bits.</summary>
    public const int FractionalBits = 16;

    /// <summary>Raw value of 1.0.</summary>
    public const int OneRaw = 1 << FractionalBits;

    private const long HalfRaw = 1L << (FractionalBits - 1);

    /// <summary>The underlying Q16.16 bit pattern.</summary>
    public readonly int Raw;

    private Fix32(int raw) => Raw = raw;

    public static readonly Fix32 Zero = new(0);
    public static readonly Fix32 Half = new(OneRaw / 2);
    public static readonly Fix32 One = new(OneRaw);
    public static readonly Fix32 Two = new(OneRaw * 2);
    public static readonly Fix32 MinValue = new(int.MinValue);
    public static readonly Fix32 MaxValue = new(int.MaxValue);
    public static readonly Fix32 Epsilon = new(1);

    /// <summary>Wraps a raw Q16.16 bit pattern.</summary>
    public static Fix32 FromRaw(int raw) => new(raw);

    /// <summary>
    /// Converts an integer to fixed point. Throws when the result would exceed
    /// the representable range (|value| &gt; 32767).
    /// </summary>
    public static Fix32 FromInt(int value)
    {
        if (value > (int.MaxValue >> FractionalBits) || value < (int.MinValue >> FractionalBits))
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value does not fit in Q16.16.");
        }

        return new Fix32(value << FractionalBits);
    }

    /// <summary>Exact ratio conversion, truncating towards zero.</summary>
    public static Fix32 FromRatio(int numerator, int denominator)
    {
        if (denominator == 0)
        {
            throw new DivideByZeroException();
        }

        return new Fix32((int)(((long)numerator << FractionalBits) / denominator));
    }

    /// <summary>
    /// Converts a double to fixed point, rounding to nearest. Intended for data
    /// loading and tests only — never for per-tick simulation maths.
    /// </summary>
    public static Fix32 FromDouble(double value)
    {
        double scaled = Math.Round(value * OneRaw, MidpointRounding.ToEven);
        if (scaled > int.MaxValue || scaled < int.MinValue)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Value does not fit in Q16.16.");
        }

        return new Fix32((int)scaled);
    }

    /// <summary>Truncates towards negative infinity.</summary>
    public int ToIntFloor() => Raw >> FractionalBits;

    /// <summary>Rounds to nearest integer, halves away from zero.</summary>
    public int ToIntRound() => DivRoundToInt(Raw, OneRaw);

    private static int DivRoundToInt(int numerator, int denominator)
        => numerator >= 0
            ? (int)(((long)numerator + (denominator / 2)) / denominator)
            : (int)(((long)numerator - (denominator / 2)) / denominator);

    public double ToDouble() => (double)Raw / OneRaw;

    public float ToFloat() => Raw / (float)OneRaw;

    public static Fix32 operator +(Fix32 a, Fix32 b) => new(a.Raw + b.Raw);

    public static Fix32 operator -(Fix32 a, Fix32 b) => new(a.Raw - b.Raw);

    public static Fix32 operator -(Fix32 a) => new(-a.Raw);

    public static Fix32 operator *(Fix32 a, Fix32 b)
    {
        long product = (long)a.Raw * b.Raw;
        // Round to nearest, halves away from zero.
        return new Fix32((int)((product + (product >= 0 ? HalfRaw : -HalfRaw)) >> FractionalBits));
    }

    public static Fix32 operator /(Fix32 a, Fix32 b)
    {
        if (b.Raw == 0)
        {
            throw new DivideByZeroException();
        }

        return new Fix32((int)(((long)a.Raw << FractionalBits) / b.Raw));
    }

    public static bool operator ==(Fix32 a, Fix32 b) => a.Raw == b.Raw;

    public static bool operator !=(Fix32 a, Fix32 b) => a.Raw != b.Raw;

    public static bool operator <(Fix32 a, Fix32 b) => a.Raw < b.Raw;

    public static bool operator >(Fix32 a, Fix32 b) => a.Raw > b.Raw;

    public static bool operator <=(Fix32 a, Fix32 b) => a.Raw <= b.Raw;

    public static bool operator >=(Fix32 a, Fix32 b) => a.Raw >= b.Raw;

    public static Fix32 Abs(Fix32 value) => value.Raw >= 0 ? value : new Fix32(-value.Raw);

    public static Fix32 Min(Fix32 a, Fix32 b) => a.Raw <= b.Raw ? a : b;

    public static Fix32 Max(Fix32 a, Fix32 b) => a.Raw >= b.Raw ? a : b;

    public static Fix32 Clamp(Fix32 value, Fix32 min, Fix32 max)
    {
        if (min > max)
        {
            throw new ArgumentException("min must not be greater than max.", nameof(min));
        }

        return Min(Max(value, min), max);
    }

    /// <summary>Square root of a non-negative fixed-point value.</summary>
    public static Fix32 Sqrt(Fix32 value)
    {
        if (value.Raw < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(value), value, "Cannot take the square root of a negative value.");
        }

        // result_raw = sqrt(value_raw * 2^16), so shift the raw value up first.
        ulong scaled = (ulong)value.Raw << FractionalBits;
        return new Fix32((int)IntMath.SqrtLong(scaled));
    }

    /// <summary>
    /// Linear interpolation. <paramref name="t"/> is expected in [0, 1] but is
    /// clamped, so callers cannot overshoot.
    /// </summary>
    public static Fix32 Lerp(Fix32 a, Fix32 b, Fix32 t)
    {
        t = Clamp(t, Zero, One);
        return a + ((b - a) * t);
    }

    public bool Equals(Fix32 other) => Raw == other.Raw;

    public override bool Equals(object? obj) => obj is Fix32 other && Equals(other);

    public int CompareTo(Fix32 other) => Raw.CompareTo(other.Raw);

    public override int GetHashCode() => Raw;

    public override string ToString() => ToDouble().ToString("0.#####", System.Globalization.CultureInfo.InvariantCulture);
}
