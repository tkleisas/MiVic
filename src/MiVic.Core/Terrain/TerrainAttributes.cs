using MiVic.Core.Numerics;

namespace MiVic.Core.Terrain;

/// <summary>
/// What the ground is like, as one 32-bit word per cell.
/// <para>
/// The surface byte stays what it always was — the *movement* classification, with a
/// categorical cost and a cover value keyed on it — because pathfinding being cheap
/// matters more than it being expressive. Everything the surface cannot say goes here
/// instead: a cell can be rocky ground with trees on it, or a muddy wood, or sand with
/// scrub, which one byte could never express.
/// </para>
/// <para>
/// Fields are packed low to high: vegetation in bits 0-7, moisture 8-11, aspect 12-15,
/// landform 16-19, fuel 20-23, then the flags <see cref="IsBurning"/> through
/// <see cref="IsFlooded"/> at bits 24-28. Bits 29-31 are spare. Access is by name only:
/// a shift written at a call site is a shift that will eventually be written wrong.
/// </para>
/// </summary>
public readonly struct TerrainAttributes : IEquatable<TerrainAttributes>
{
    /// <summary>Canopy density of closed woodland: the most the eight bits can hold.</summary>
    public const int MaxVegetation = 255;

    /// <summary>Wettest ground the four moisture bits can express.</summary>
    public const int MaxMoisture = 15;

    /// <summary>Compass points an aspect can name: 0-7, then <see cref="FlatAspect"/>.</summary>
    public const int AspectPoints = 8;

    /// <summary>Aspect of ground with no fall line to speak of. 9-15 are unused.</summary>
    public const int FlatAspect = 8;

    /// <summary>Highest landform id: 0 plain, 1 slope, 2 ridge, 3 valley, 4 pass, 5 plateau, 6 basin, 7 shelf.</summary>
    public const int MaxLandform = 7;

    /// <summary>Most there is to burn in a cell.</summary>
    public const int MaxFuel = 15;

    private const int VegetationShift = 0;
    private const int VegetationBits = 8;
    private const int MoistureShift = 8;
    private const int MoistureBits = 4;
    private const int AspectShift = 12;
    private const int AspectBits = 4;
    private const int LandformShift = 16;
    private const int LandformBits = 4;
    private const int FuelShift = 20;
    private const int FuelBits = 4;
    private const int BurningBit = 24;
    private const int BurnedBit = 25;
    private const int CrateredBit = 26;
    private const int RubbleBit = 27;
    private const int FloodedBit = 28;

    private readonly uint _word;

    /// <summary>Wraps a stored or loaded word. Every field of a default word is zero.</summary>
    public TerrainAttributes(uint word) => _word = word;

    /// <summary>The packed word, as stored and hashed.</summary>
    public uint Raw => _word;

    /// <summary>Canopy density: 0 bare, 255 closed.</summary>
    public int Vegetation => Read(VegetationShift, VegetationBits);

    /// <summary>Returns a copy with the canopy density set. Out-of-range values saturate.</summary>
    public TerrainAttributes WithVegetation(int value) => new(Write(VegetationShift, VegetationBits, value));

    /// <summary>How wet the ground is: 0 dry, 15 standing water.</summary>
    public int Moisture => Read(MoistureShift, MoistureBits);

    /// <summary>Returns a copy with the moisture set.</summary>
    public TerrainAttributes WithMoisture(int value) => new(Write(MoistureShift, MoistureBits, value));

    /// <summary>The way the ground faces, 0-7, or <see cref="FlatAspect"/> where it does not.</summary>
    public int Aspect => Read(AspectShift, AspectBits);

    /// <summary>Returns a copy with the aspect set.</summary>
    public TerrainAttributes WithAspect(int value) => new(Write(AspectShift, AspectBits, value));

    /// <summary>Which landform the cell sits on, 0-7. See <see cref="MaxLandform"/>.</summary>
    public int Landform => Read(LandformShift, LandformBits);

    /// <summary>Returns a copy with the landform set.</summary>
    public TerrainAttributes WithLandform(int value) => new(Write(LandformShift, LandformBits, value));

    /// <summary>
    /// How much there is left to burn. Starts at the vegetation and falls as it burns;
    /// zero everywhere until the fire step generates it.
    /// </summary>
    public int Fuel => Read(FuelShift, FuelBits);

    /// <summary>Returns a copy with the fuel set.</summary>
    public TerrainAttributes WithFuel(int value) => new(Write(FuelShift, FuelBits, value));

    /// <summary>Fire is in this cell now.</summary>
    public bool IsBurning => Flag(BurningBit);

    /// <summary>Returns a copy with the burning flag set or cleared.</summary>
    public TerrainAttributes WithBurning(bool value) => new(Flag(BurningBit, value));

    /// <summary>It has been on fire and is charred; it regrows far more slowly.</summary>
    public bool IsBurned => Flag(BurnedBit);

    /// <summary>Returns a copy with the burned flag set or cleared.</summary>
    public TerrainAttributes WithBurned(bool value) => new(Flag(BurnedBit, value));

    /// <summary>Shelled ground: extra cover, worse going.</summary>
    public bool IsCratered => Flag(CrateredBit);

    /// <summary>Returns a copy with the cratered flag set or cleared.</summary>
    public TerrainAttributes WithCratered(bool value) => new(Flag(CrateredBit, value));

    /// <summary>Collapsed structure: extra cover, impassable to tracked vehicles.</summary>
    public bool IsRubble => Flag(RubbleBit);

    /// <summary>Returns a copy with the rubble flag set or cleared.</summary>
    public TerrainAttributes WithRubble(bool value) => new(Flag(RubbleBit, value));

    /// <summary>Standing water laid by weather control.</summary>
    public bool IsFlooded => Flag(FloodedBit);

    /// <summary>Returns a copy with the flooded flag set or cleared.</summary>
    public TerrainAttributes WithFlooded(bool value) => new(Flag(FloodedBit, value));

    /// <inheritdoc />
    public bool Equals(TerrainAttributes other) => _word == other._word;

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is TerrainAttributes other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode() => (int)_word;

    /// <summary>Two words are the same ground.</summary>
    public static bool operator ==(TerrainAttributes left, TerrainAttributes right) => left._word == right._word;

    /// <summary>Two words are not the same ground.</summary>
    public static bool operator !=(TerrainAttributes left, TerrainAttributes right) => left._word != right._word;

    /// <summary>Readable form, so a test failure prints the ground rather than a number.</summary>
    public override string ToString()
    {
        string flags =
            (IsBurning ? " burning" : string.Empty) +
            (IsBurned ? " burned" : string.Empty) +
            (IsCratered ? " cratered" : string.Empty) +
            (IsRubble ? " rubble" : string.Empty) +
            (IsFlooded ? " flooded" : string.Empty);

        return $"veg {Vegetation}, moisture {Moisture}, aspect {Aspect}, landform {Landform}, fuel {Fuel}," +
            (flags.Length > 0 ? flags : " no flags");
    }

    /// <summary>Reads a packed field.</summary>
    private int Read(int shift, int bits) => (int)((_word >> shift) & ((1u << bits) - 1u));

    /// <summary>
    /// Writes a packed field, saturating rather than wrapping.
    /// <para>
    /// Saturation is the honest failure: a caller asking for moisture 20 on a four-bit
    /// field has made a mistake, and 15 shows it where a wrap to 4 would quietly put a
    /// desert where a marsh was.
    /// </para>
    /// </summary>
    private uint Write(int shift, int bits, int value)
    {
        uint mask = ((1u << bits) - 1u) << shift;
        uint clamped = (uint)IntMath.Clamp(value, 0, (int)((1u << bits) - 1u));

        return (_word & ~mask) | (clamped << shift);
    }

    /// <summary>Reads one flag.</summary>
    private bool Flag(int bit) => (_word & (1u << bit)) != 0;

    /// <summary>Sets or clears one flag.</summary>
    private uint Flag(int bit, bool value)
        => value ? _word | (1u << bit) : _word & ~(1u << bit);
}
