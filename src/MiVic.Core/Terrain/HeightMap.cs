using MiVic.Core.Numerics;

namespace MiVic.Core.Terrain;

/// <summary>
/// The battlefield height field: a square lattice of heights in millimetres.
/// <para>
/// Heights are generated from integer value noise, so the same seed always
/// produces the same terrain on every machine. That matters because terrain
/// shapes pathfinding, pathfinding shapes movement, and movement is part of the
/// state hash — a drifting height would desync a replay.
/// </para>
/// </summary>
public sealed class HeightMap
{
    private readonly int[] _heights;

    private HeightMap(int size, int cellSizeMm, int originMm, int maxHeightMm, int[] heights)
    {
        Size = size;
        CellSizeMm = cellSizeMm;
        OriginMm = originMm;
        MaxHeightMm = maxHeightMm;
        _heights = heights;
    }

    /// <summary>Number of samples along each axis.</summary>
    public int Size { get; }

    /// <summary>Distance between samples, in millimetres.</summary>
    public int CellSizeMm { get; }

    /// <summary>World coordinate of sample zero, in millimetres (negative half-extent).</summary>
    public int OriginMm { get; }

    /// <summary>Highest possible terrain height, in millimetres.</summary>
    public int MaxHeightMm { get; }

    /// <summary>World extent covered by the map, in millimetres.</summary>
    public int ExtentMm => (Size - 1) * CellSizeMm;

    /// <summary>Height at a lattice sample.</summary>
    public int HeightAt(int cellX, int cellZ)
    {
        cellX = IntMath.Clamp(cellX, 0, Size - 1);
        cellZ = IntMath.Clamp(cellZ, 0, Size - 1);
        return _heights[(cellZ * Size) + cellX];
    }

    /// <summary>Height at a lattice index.</summary>
    public int HeightAtIndex(int index) => _heights[index];

    /// <summary>
    /// Every height, row major. The attribute generator reads a neighbourhood around each
    /// cell, and a span it can index is what lets that walk be eight lookups rather than
    /// eight calls that clamp a coordinate each.
    /// </summary>
    public ReadOnlySpan<int> RawHeights => _heights;

    /// <summary>Converts a world coordinate to the nearest lattice index.</summary>
    public int CellOf(int worldMm) => IntMath.Clamp((worldMm - OriginMm) / CellSizeMm, 0, Size - 1);

    /// <summary>Bilinearly sampled terrain height at a world position, in millimetres.</summary>
    public int SampleHeightMm(int worldX, int worldZ)
    {
        int localX = worldX - OriginMm;
        int localZ = worldZ - OriginMm;

        int cellX = FloorDiv(localX, CellSizeMm);
        int cellZ = FloorDiv(localZ, CellSizeMm);

        // Fraction of the way across the cell, as a Q16 value.
        int tx = (int)(((long)(localX - (cellX * CellSizeMm)) << 16) / CellSizeMm);
        int tz = (int)(((long)(localZ - (cellZ * CellSizeMm)) << 16) / CellSizeMm);

        int top = Lerp(HeightAt(cellX, cellZ), HeightAt(cellX + 1, cellZ), tx);
        int bottom = Lerp(HeightAt(cellX, cellZ + 1), HeightAt(cellX + 1, cellZ + 1), tx);

        return Lerp(top, bottom, tz);
    }

    /// <summary>Height at a world position, in millimetres.</summary>
    public int SampleHeightMm(WorldPos position) => SampleHeightMm(position.X, position.Z);

    /// <summary>Highest slope from this sample to its four neighbours, in millimetres per metre.</summary>
    public int SlopePermille(int cellX, int cellZ)
    {
        int height = HeightAt(cellX, cellZ);
        int worst = 0;

        worst = Math.Max(worst, Math.Abs(HeightAt(cellX - 1, cellZ) - height));
        worst = Math.Max(worst, Math.Abs(HeightAt(cellX + 1, cellZ) - height));
        worst = Math.Max(worst, Math.Abs(HeightAt(cellX, cellZ - 1) - height));
        worst = Math.Max(worst, Math.Abs(HeightAt(cellX, cellZ + 1) - height));

        // mm of rise per cell, expressed as mm per metre.
        return (int)((long)worst * 1000 / CellSizeMm);
    }

    /// <summary>Hash of every height, used by the determinism tests.</summary>
    public ulong ComputeHash()
    {
        ulong hash = Sim.StateHash.OffsetBasis;

        foreach (int height in _heights)
        {
            Sim.StateHash.Mix(ref hash, height);
        }

        return hash;
    }

    /// <summary>
    /// Generates a terrain from a seed using summed integer value noise. Three
    /// octaves give broad hills, medium ridges and fine bumps.
    /// </summary>
    /// <param name="seed">World seed.</param>
    /// <param name="size">Samples per side.</param>
    /// <param name="extentMm">World extent covered, in millimetres.</param>
    /// <param name="maxHeightMm">Ceiling on terrain height.</param>
    public static HeightMap Generate(ulong seed, int size, int extentMm, int maxHeightMm)
    {
        if (size < 2)
        {
            throw new ArgumentOutOfRangeException(nameof(size), size, "At least two samples are required.");
        }

        if (extentMm <= 0 || maxHeightMm <= 0)
        {
            throw new ArgumentOutOfRangeException(nameof(extentMm), "Extent and height must be positive.");
        }

        int cellSizeMm = extentMm / (size - 1);
        int originMm = -(extentMm / 2);
        int[] heights = new int[size * size];

        // Feature sizes scale with the map so the same seed gives similar terrain
        // regardless of resolution.
        int[] spacings = [extentMm / 4, extentMm / 10, extentMm / 28];
        int[] amplitudes = [maxHeightMm / 2, maxHeightMm / 4, maxHeightMm / 8];

        for (int z = 0; z < size; z++)
        {
            int worldZ = originMm + (z * cellSizeMm);

            for (int x = 0; x < size; x++)
            {
                int worldX = originMm + (x * cellSizeMm);
                int height = maxHeightMm / 5;

                for (int octave = 0; octave < spacings.Length; octave++)
                {
                    height += ValueNoise(worldX, worldZ, spacings[octave], amplitudes[octave], seed, octave);
                }

                heights[(z * size) + x] = IntMath.Clamp(height, 0, maxHeightMm);
            }
        }

        return new HeightMap(size, cellSizeMm, originMm, maxHeightMm, heights);
    }

    private static int ValueNoise(int x, int z, int spacing, int amplitude, ulong seed, int octave)
    {
        if (spacing < 1)
        {
            spacing = 1;
        }

        int gx = FloorDiv(x, spacing);
        int gz = FloorDiv(z, spacing);

        int fx = (int)(((long)(x - (gx * spacing)) << 16) / spacing);
        int fz = (int)(((long)(z - (gz * spacing)) << 16) / spacing);

        int sx = Smoothstep(fx);
        int sz = Smoothstep(fz);

        ulong octaveSeed = seed + ((ulong)octave * 0x9E3779B97F4A7C15UL);

        int v00 = Lattice(gx, gz, octaveSeed);
        int v10 = Lattice(gx + 1, gz, octaveSeed);
        int v01 = Lattice(gx, gz + 1, octaveSeed);
        int v11 = Lattice(gx + 1, gz + 1, octaveSeed);

        int top = Lerp(v00, v10, sx);
        int bottom = Lerp(v01, v11, sx);
        int value = Lerp(top, bottom, sz);

        // Map the [0, 65535] lattice value onto [-amplitude, amplitude].
        return (int)(((long)(value - 32768) * amplitude) >> 15);
    }

    /// <summary>Deterministic lattice value in [0, 65535] from integer coordinates.</summary>
    private static int Lattice(int gx, int gz, ulong seed)
    {
        ulong h = seed;
        h ^= (ulong)(uint)gx * 0x9E3779B97F4A7C15UL;
        h ^= (ulong)(uint)gz * 0xC2B2AE3D27D4EB4FUL;
        h ^= h >> 30;
        h *= 0xBF58476D1CE4E5B9UL;
        h ^= h >> 27;
        h *= 0x94D049BB133111EBUL;
        h ^= h >> 31;
        return (int)((h >> 33) & 0xFFFF);
    }

    /// <summary>Smoothstep on a Q16 fraction: f*f*(3-2f).</summary>
    private static int Smoothstep(int t)
    {
        long f = t;
        long squared = (f * f) >> 16;
        long result = (squared * ((3L * 65536) - (2 * f))) >> 16;
        return (int)result;
    }

    private static int Lerp(int a, int b, int t) => a + (int)(((long)(b - a) * t) >> 16);

    private static int FloorDiv(int value, int divisor)
        => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
}
