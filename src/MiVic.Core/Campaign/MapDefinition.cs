using MiVic.Core.Terrain;
using MiVic.Core.Sim;

namespace MiVic.Core.Campaign;

/// <summary>What one terrain edit does.</summary>
public enum TerrainEditKind : byte
{
    /// <summary>
    /// Raise or lower the ground at a cell, by <see cref="TerrainEdit.DeltaMm"/> — positive
    /// raises, negative lowers. The derived passes are re-run from the edited ground.
    /// </summary>
    AdjustHeight = 0,

    /// <summary>
    /// Paint the surface at a cell. Applied <em>after</em> the re-derivation, because the
    /// derived bands are the ground the author started from: a paint is a decision over
    /// them, and the same paint made before them would be overwritten by them.
    /// </summary>
    Paint = 1,
}

/// <summary>One edit to the ground, as the author wrote it.</summary>
/// <param name="Kind">What to do.</param>
/// <param name="CellX">Lattice cell on the navigation grid, or -1 to name it in metres.</param>
/// <param name="CellZ">Lattice cell on the navigation grid.</param>
/// <param name="X">World position in millimetres, when <see cref="CellX"/> is negative.</param>
/// <param name="Z">World position in millimetres, when <see cref="CellZ"/> is negative.</param>
/// <param name="DeltaMm">
/// How far to raise (positive) or lower (negative) the ground, in millimetres, for
/// <see cref="TerrainEditKind.AdjustHeight"/>. The edit is a delta rather than an absolute
/// height, because the author is shaping generated ground, not authoring a height field.
/// </param>
/// <param name="Type">The surface to paint, for <see cref="TerrainEditKind.Paint"/>.</param>
public readonly record struct TerrainEdit(
    TerrainEditKind Kind,
    int CellX = -1,
    int CellZ = -1,
    int X = 0,
    int Z = 0,
    int DeltaMm = 0,
    TerrainType Type = TerrainType.Grass)
{
    /// <summary>Resolves a height edit to a height-field sample. Metres or lattice alike; -1 when it falls outside the map.</summary>
    public int ResolveSample(HeightMap terrain)
    {
        if (CellX >= 0 && CellZ >= 0)
        {
            return IndexIn(terrain.Size, CellX, CellZ);
        }

        return IndexIn(
            terrain.Size,
            (X - terrain.OriginMm) / terrain.CellSizeMm,
            (Z - terrain.OriginMm) / terrain.CellSizeMm);
    }

    /// <summary>Resolves a paint to the surface layer's lattice. -1 when it falls outside the map.</summary>
    public int ResolveLayerCell(TerrainLayer layer)
    {
        if (CellX >= 0 && CellZ >= 0)
        {
            return IndexIn(layer.Size, CellX, CellZ);
        }

        return layer.IndexOfWorld(X, Z);
    }

    private static int IndexIn(int size, int x, int z)
        => (uint)x < (uint)size && (uint)z < (uint)size ? (z * size) + x : -1;
}

/// <summary>One structure the author placed on the edited ground.</summary>
/// <param name="Kind">The role to raise.</param>
/// <param name="X">World position, in millimetres.</param>
/// <param name="Z">World position, in millimetres.</param>
/// <param name="Team">Which side it belongs to.</param>
public readonly record struct StructurePlacement(
    UnitKind Kind,
    int X,
    int Z,
    int Team);

/// <summary>
/// A map: a seed, a list of edits over the ground that seed generates, and the mission the
/// ground is shaped for. §10's edit list, as data.
/// <para>
/// The seed comes first because the ground comes first: the edits are deltas over terrain
/// the simulation generates, not an authored height field, so an author starts from coherent
/// ground and adjusts it — and the derived passes re-run from the edited heights, because the
/// guarantees live in those passes. The list order is the order the world resolves in:
/// <b>shape the terrain first, then place on it</b>, and an island is drawn before anything
/// is put on the island because the island is what makes the placement legal.
/// </para>
/// </summary>
/// <param name="Seed">The terrain's seed, which everything derived starts from.</param>
/// <param name="TerrainEdits">Ground edits, in the order the author shaped them.</param>
/// <param name="Structures">Structures placed on the edited ground.</param>
/// <param name="Mission">The mission the map is: its roster, objectives and triggers.</param>
public sealed record MapDefinition(
    ulong Seed,
    IReadOnlyList<TerrainEdit> TerrainEdits,
    IReadOnlyList<StructurePlacement> Structures,
    MissionDefinition Mission)
{
    /// <summary>
    /// The mission this map plays, with the map's own seed — the ground and the battle are
    /// one world, and the file carries the seed once. The mission body inside a map may
    /// carry its own seed field; this is the one that wins, because it is the one the
    /// terrain was generated from.
    /// </summary>
    public MissionDefinition EffectiveMission => Mission with { Seed = Seed };
}
