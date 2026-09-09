using MiVic.Core.Random;

namespace MiVic.Core.Tests.Random;

public sealed class Pcg32Tests
{
    /// <summary>
    /// Reference vectors from the upstream PCG demo (pcg32-demo), initialised
    /// with <c>pcg32_srandom_r(state, 42, 54)</c>. Matching these proves our
    /// generator is the real PCG-XSH-RR and not an accidental variant — which
    /// matters, because a different sequence would break every existing replay.
    /// </summary>
    [Fact]
    public void NextUInt_MatchesReferenceVectors()
    {
        uint[] expected =
        [
            0xA15C02B7u,
            0x7B47F409u,
            0xBA1D3330u,
            0x83D2F293u,
            0xBFA4784Bu,
            0xCBED606Eu,
        ];

        Pcg32 rng = new(42UL, 54UL);
        foreach (uint want in expected)
        {
            Assert.Equal(want, rng.NextUInt());
        }
    }

    [Fact]
    public void SameSeed_ProducesSameSequence()
    {
        Pcg32 a = new(12345);
        Pcg32 b = new(12345);

        for (int i = 0; i < 1000; i++)
        {
            Assert.Equal(a.NextUInt(), b.NextUInt());
        }
    }

    [Fact]
    public void DifferentSeeds_DivergeImmediately()
    {
        Pcg32 a = new(1);
        Pcg32 b = new(2);

        bool differs = false;
        for (int i = 0; i < 8; i++)
        {
            if (a.NextUInt() != b.NextUInt())
            {
                differs = true;
                break;
            }
        }

        Assert.True(differs);
    }

    [Fact]
    public void Derive_ProducesIndependentStreams()
    {
        Pcg32 main = new(999);
        Pcg32 streamA = Pcg32.Derive(999, 1);
        Pcg32 streamB = Pcg32.Derive(999, 2);

        Assert.NotEqual(streamA.NextUInt(), streamB.NextUInt());

        // A derived stream must not disturb the parent sequence.
        uint expectedParent = new Pcg32(999).NextUInt();
        Assert.Equal(expectedParent, main.NextUInt());
    }

    [Fact]
    public void NextInt_StaysInRangeAndCoversIt()
    {
        Pcg32 rng = new(7);
        bool[] seen = new bool[10];

        for (int i = 0; i < 10_000; i++)
        {
            int value = rng.NextInt(10);
            Assert.InRange(value, 0, 9);
            seen[value] = true;
        }

        Assert.All(seen, Assert.True);
    }

    [Fact]
    public void NextInt_RejectsNonPositiveBound()
    {
        Pcg32 rng = new(1);
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(0));
        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(-5));
    }

    [Fact]
    public void NextInt_RangeOverloadIsInclusiveExclusive()
    {
        Pcg32 rng = new(3);
        for (int i = 0; i < 1000; i++)
        {
            Assert.InRange(rng.NextInt(5, 8), 5, 7);
        }

        Assert.Throws<ArgumentOutOfRangeException>(() => rng.NextInt(5, 5));
    }

    [Fact]
    public void NextFix_StaysInUnitInterval()
    {
        Pcg32 rng = new(11);
        for (int i = 0; i < 10_000; i++)
        {
            int raw = rng.NextFix().Raw;
            Assert.InRange(raw, 0, 65535);
        }
    }

    [Fact]
    public void NextFix_IsUniformAcrossQuartiles()
    {
        Pcg32 rng = new(4242);
        int[] buckets = new int[4];

        for (int i = 0; i < 40_000; i++)
        {
            buckets[rng.NextFix().Raw >> 14]++;
        }

        // 40k draws across 4 buckets: expect ~10k each; allow generous slack.
        foreach (int count in buckets)
        {
            Assert.InRange(count, 9_000, 11_000);
        }
    }

    [Fact]
    public void Chance_ApproximatesTheRequestedProbability()
    {
        Pcg32 rng = new(2024);
        int hits = 0;

        for (int i = 0; i < 100_000; i++)
        {
            if (rng.Chance(1, 4))
            {
                hits++;
            }
        }

        Assert.InRange(hits, 24_000, 26_000);
    }

    [Fact]
    public void Peek_DoesNotAdvanceTheOriginal()
    {
        Pcg32 rng = new(77);
        Pcg32 peeked = rng.Peek(5);

        Assert.Equal(peeked.NextUInt(), rng.Peek(5).NextUInt());
        Assert.Equal(new Pcg32(77).NextUInt(), rng.NextUInt());
    }
}
