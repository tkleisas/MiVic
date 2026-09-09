using MiVic.Core.Numerics;

namespace MiVic.Core.Random;

/// <summary>
/// PCG-XSH-RR 32-bit generator (O'Neill, 2014).
/// <para>
/// Chosen over <see cref="System.Random"/> because its output is a pure function
/// of its 64-bit state: no hidden seeding, no platform-dependent behaviour, and
/// therefore no desync in a lockstep simulation. Because this is a mutable
/// struct, always call it through a field or a <c>ref</c> — calling it through a
/// property returns a copy and silently discards the advanced state.
/// </para>
/// </summary>
public struct Pcg32
{
    private const ulong Multiplier = 6364136223846793005UL;

    /// <summary>Default stream selector; a different value gives an independent sequence.</summary>
    public const ulong DefaultSequence = 0xDA3E39CB94B95BDBUL;

    private ulong _state;
    private readonly ulong _increment;

    /// <summary>Creates a generator from a seed and an independent stream selector.</summary>
    public Pcg32(ulong seed, ulong sequence = DefaultSequence)
    {
        _increment = (sequence << 1) | 1UL;
        _state = 0UL;
        NextUInt();
        _state += seed;
        NextUInt();
    }

    /// <summary>The current 64-bit state. Exposed so replays can be hashed.</summary>
    public readonly ulong State => _state;

    /// <summary>Advances the generator and returns the next 32-bit value.</summary>
    public uint NextUInt()
    {
        ulong old = _state;
        _state = (old * Multiplier) + _increment;
        uint xorShifted = (uint)(((old >> 18) ^ old) >> 27);
        int rot = (int)(old >> 59);
        return (xorShifted >> rot) | (xorShifted << ((-rot) & 31));
    }

    /// <summary>Uniform value in [0, <paramref name="maxExclusive"/>). Rejects biased draws.</summary>
    public int NextInt(int maxExclusive)
    {
        if (maxExclusive <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "Must be positive.");
        }

        uint bound = (uint)maxExclusive;
        uint threshold = (uint)(-(int)bound) % bound;

        while (true)
        {
            uint r = NextUInt();
            if (r >= threshold)
            {
                return (int)(r % bound);
            }
        }
    }

    /// <summary>Uniform value in [<paramref name="minInclusive"/>, <paramref name="maxExclusive"/>).</summary>
    public int NextInt(int minInclusive, int maxExclusive)
    {
        if (maxExclusive <= minInclusive)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "Must be greater than minInclusive.");
        }

        return minInclusive + NextInt(maxExclusive - minInclusive);
    }

    /// <summary>Uniform fixed-point value in [0, 1).</summary>
    public Fix32 NextFix() => Fix32.FromRaw((int)(NextUInt() >> (32 - Fix32.FractionalBits)));

    /// <summary>Uniform fixed-point value in [0, <paramref name="maxExclusive"/>).</summary>
    public Fix32 NextFix(Fix32 maxExclusive)
    {
        if (maxExclusive.Raw < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(maxExclusive), maxExclusive, "Must not be negative.");
        }

        return maxExclusive * NextFix();
    }

    /// <summary>True with probability <paramref name="numerator"/> / <paramref name="denominator"/>.</summary>
    public bool Chance(int numerator, int denominator)
    {
        if (denominator <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(denominator), denominator, "Must be positive.");
        }

        return NextInt(denominator) < numerator;
    }

    /// <summary>Returns a copy advanced past <paramref name="count"/> draws.</summary>
    public readonly Pcg32 Peek(int count)
    {
        Pcg32 copy = this;
        for (int i = 0; i < count; i++)
        {
            copy.NextUInt();
        }

        return copy;
    }

    /// <summary>
    /// Derives an independent generator, so that per-entity randomness cannot
    /// change the main sequence when entities are spawned or destroyed.
    /// </summary>
    public static Pcg32 Derive(ulong seed, int stream) => new(seed, DefaultSequence ^ ((ulong)stream * 0x9E3779B97F4A7C15UL));

    public override readonly string ToString()
        => string.Create(System.Globalization.CultureInfo.InvariantCulture, $"Pcg32(state=0x{_state:X16})");
}
