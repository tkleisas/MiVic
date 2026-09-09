namespace MiVic.Core.Numerics;

/// <summary>
/// Deterministic integer trigonometry, expressed in binary radians ("brads"),
/// where 65536 brads is a full turn.
/// <para>
/// The tables are built once at type-initialisation from <see cref="Math.Atan"/>
/// and <see cref="Math.Sin"/> and then rounded to integers. Every runtime value
/// afterwards comes from integer interpolation, so per-tick simulation maths is
/// exact. <see cref="InitializationHash"/> exists so the determinism tests can
/// assert that the tables are identical everywhere: if a platform ever rounded a
/// table entry differently, that test fails loudly instead of the game silently
/// desyncing.
/// </para>
/// </summary>
public static class TrigTable
{
    /// <summary>Brads in a full turn.</summary>
    public const int BradsPerTurn = 65536;

    /// <summary>Brads in a quarter turn (90 degrees).</summary>
    public const int BradsPerQuarterTurn = BradsPerTurn / 4;

    /// <summary>Number of interpolation steps across [0, 1].</summary>
    private const int Steps = 1024;

    /// <summary>atan(z) for z in [0, 1], in brads. Length <see cref="Steps"/> + 1.</summary>
    private static readonly int[] AtanTable = new int[Steps + 1];

    /// <summary>sin(theta) in Q16.16 for theta in [0, quarter turn].</summary>
    private static readonly int[] SinTable = new int[Steps + 1];

    static TrigTable()
    {
        for (int i = 0; i <= Steps; i++)
        {
            double t = (double)i / Steps;

            // atan(t) mapped to brads: full turn is 2*pi.
            AtanTable[i] = (int)Math.Round(BradsPerTurn * Math.Atan(t) / (2.0 * Math.PI), MidpointRounding.ToEven);

            // sin over the first quadrant, in Q16.16.
            SinTable[i] = (int)Math.Round(65536.0 * Math.Sin(t * Math.PI / 2.0), MidpointRounding.ToEven);
        }

        ulong hash = Sim.StateHash.OffsetBasis;
        foreach (int value in AtanTable)
        {
            Sim.StateHash.Mix(ref hash, value);
        }

        foreach (int value in SinTable)
        {
            Sim.StateHash.Mix(ref hash, value);
        }

        InitializationHash = hash;
    }

    /// <summary>Hash of every table entry. Guarded by a golden-value test.</summary>
    public static ulong InitializationHash { get; }

    /// <summary>
    /// Angle of the vector (<paramref name="dx"/>, <paramref name="dz"/>) in
    /// brads, measured from +X towards +Z. Returns 0 for the zero vector.
    /// Accurate to about one degree, which is far below what is visible.
    /// </summary>
    public static ushort Atan2Brads(int dz, int dx)
    {
        if (dx == 0 && dz == 0)
        {
            return 0;
        }

        long ax = Math.Abs((long)dx);
        long az = Math.Abs((long)dz);

        int a;
        if (az <= ax)
        {
            // Ratio in [0, 1] as Q16.16, then atan of it.
            int ratio = ax == 0 ? 65536 : (int)((az << 16) / ax);
            a = AtanQ16(ratio);
        }
        else
        {
            // atan(az/ax) = pi/2 - atan(ax/az)
            int ratio = az == 0 ? 65536 : (int)((ax << 16) / az);
            a = BradsPerQuarterTurn - AtanQ16(ratio);
        }

        long angle = dx >= 0
            ? (dz >= 0 ? a : BradsPerTurn - a)
            : (dz >= 0 ? (BradsPerTurn / 2) - a : (BradsPerTurn / 2) + a);

        return (ushort)(angle & (BradsPerTurn - 1));
    }

    /// <summary>sin(theta) in Q16.16 for an angle in brads.</summary>
    public static Fix32 SinBrads(int brads)
    {
        int sign = 1;
        int quadrant = FoldToFirstQuadrant(ref brads, ref sign);
        return Fix32.FromRaw(sign * SinQ16(quadrant));
    }

    /// <summary>cos(theta) in Q16.16 for an angle in brads.</summary>
    public static Fix32 CosBrads(int brads) => SinBrads(brads + BradsPerQuarterTurn);

    /// <summary>atan of a Q16.16 ratio in [0, 1], in brads.</summary>
    private static int AtanQ16(int ratio)
    {
        if (ratio <= 0)
        {
            return 0;
        }

        if (ratio >= 65536)
        {
            return AtanTable[Steps];
        }

        long scaled = (long)ratio * Steps;
        int index = (int)(scaled >> 16);
        int fraction = (int)(scaled & 0xFFFF);

        int low = AtanTable[index];
        int high = AtanTable[index + 1];
        return low + (int)(((long)(high - low) * fraction) >> 16);
    }

    /// <summary>sin of an angle in the first quadrant, in Q16.16.</summary>
    private static int SinQ16(int brads)
    {
        if (brads <= 0)
        {
            return 0;
        }

        if (brads >= BradsPerQuarterTurn)
        {
            return SinTable[Steps];
        }

        // Map a quarter-turn angle onto the table's [0, 1] domain.
        long scaled = (long)brads * Steps * 65536 / BradsPerQuarterTurn;
        int index = (int)(scaled >> 16);
        int fraction = (int)(scaled & 0xFFFF);

        int low = SinTable[index];
        int high = SinTable[index + 1];
        return low + (int)(((long)(high - low) * fraction) >> 16);
    }

    /// <summary>Reduces an arbitrary angle to the first quadrant, tracking sign.</summary>
    private static int FoldToFirstQuadrant(ref int brads, ref int sign)
    {
        brads &= BradsPerTurn - 1;

        if (brads >= BradsPerTurn / 2)
        {
            brads -= BradsPerTurn / 2;
            sign = -sign;
        }

        if (brads >= BradsPerQuarterTurn)
        {
            brads = (BradsPerTurn / 2) - brads;
        }

        return brads;
    }
}
