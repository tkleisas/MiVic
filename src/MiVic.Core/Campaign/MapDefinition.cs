using System.Text.Json.Serialization;
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
/// <param name="RadiusCells">
/// How many lattice cells around the resolved one the edit covers, in a square: zero is the
/// single cell, which is what a file author writes and what the default says. The editor's
/// brush writes the radius it worked with, so the file carries the stroke rather than fifty
/// single-cell copies of it.
/// </param>
public readonly record struct TerrainEdit(
    TerrainEditKind Kind,
    int CellX = -1,
    int CellZ = -1,
    int X = 0,
    int Z = 0,
    int DeltaMm = 0,
    TerrainType Type = TerrainType.Grass,
    int RadiusCells = 0)
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

    /// <summary>
    /// Every lattice index the edit covers, its centre and its radius as a square — the
    /// shape a footprint already has in this engine, and the shape a brush paints with.
    /// Off-map cells are clamped out rather than refused: a brush whose centre sits at the
    /// map's edge covers what it covers, and the caller's own bounds checks refuse what
    /// fell entirely outside.
    /// </summary>
    public IEnumerable<int> ResolveCoverage(HeightMap terrain)
    {
        int centre = ResolveSample(terrain);

        if (centre < 0)
        {
            yield break;
        }

        if (RadiusCells <= 0)
        {
            yield return centre;
            yield break;
        }

        int cx = centre % terrain.Size;
        int cz = centre / terrain.Size;

        for (int dz = -RadiusCells; dz <= RadiusCells; dz++)
        {
            for (int dx = -RadiusCells; dx <= RadiusCells; dx++)
            {
                int x = cx + dx;
                int z = cz + dz;

                if ((uint)x < (uint)terrain.Size && (uint)z < (uint)terrain.Size)
                {
                    yield return (z * terrain.Size) + x;
                }
            }
        }
    }
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
/// One mobile unit the author placed on the edited ground: a vehicle or a soldier, at an
/// exact position.
/// <para>
/// The same shape as <see cref="StructurePlacement"/> and deliberately a different type,
/// because the two are asked different questions: a structure must stand on ground it can
/// be founded on and must not overlap another, while a unit must stand on ground its own
/// movement class can cross. A role that is a building belongs in <c>Structures</c> and a
/// role that moves belongs here; the loader refuses the other way round rather than
/// guessing which rule the author meant.
/// </para>
/// </summary>
/// <param name="Kind">The role to place.</param>
/// <param name="X">World position, in millimetres.</param>
/// <param name="Z">World position, in millimetres.</param>
/// <param name="Team">Which side it belongs to.</param>
public readonly record struct UnitPlacement(
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
/// <para>
/// <b>The force is optionally the author's rather than the generator's.</b> By default a map
/// lays out the mission's generated base and formation and then adds the author's placements,
/// which is what every map did before this was a choice. With <see cref="ExactForce"/> the
/// placements <em>are</em> the starting force: nothing is generated for any team, and what
/// the file lists is what stands on the ground. That is the difference between "add a gun on
/// the ridge" and "this is the order of battle". The mission's base coordinates and unit
/// counts are ignored in that mode, and its objectives, triggers and roster are not — a map
/// is still a mission, and an authored force still has to be able to win it.
/// </para>
/// </summary>
/// <param name="Seed">The terrain's seed, which everything derived starts from.</param>
/// <param name="TerrainEdits">Ground edits, in the order the author shaped them.</param>
/// <param name="Structures">Structures placed on the edited ground.</param>
/// <param name="Mission">The mission the map is: its roster, objectives and triggers.</param>
/// <param name="ExactForce">
/// True when <see cref="Structures"/> and <see cref="Units"/> are the whole starting force,
/// so the scenario generates no base and no formation for any team. False by default, which
/// generates the mission's layout and adds the placements to it.
/// </param>
public sealed record MapDefinition(
    ulong Seed,
    IReadOnlyList<TerrainEdit> TerrainEdits,
    IReadOnlyList<StructurePlacement> Structures,
    MissionDefinition Mission,
    bool ExactForce = false)
{
    /// <summary>
    /// The mobile units the author placed, an empty list by default. Kept off the primary
    /// constructor so a map that places none writes no field, exactly as a mission with no
    /// triggers writes none.
    /// </summary>
    public IReadOnlyList<UnitPlacement> Units { get; init; } = [];

    /// <summary>True when the author has placed any part of the starting force.</summary>
    public bool HasAuthoredForce => Structures.Count > 0 || Units.Count > 0;

    /// <summary>
    /// Where a tolerant build reports its refused placements, or null to refuse by
    /// exception — the file load's choice. The editor's live world sets this before it
    /// builds, because the author is working and an invalid placement is a report rather
    /// than a refusal: the next edit may make it legal again.
    /// <para>
    /// A refusal is a description and a reason rather than the placement itself, because a
    /// map places two shapes — a structure and a unit — that are refused for different
    /// reasons, and the report a cursor is looking at should not have to know which list the
    /// line came from.
    /// </para>
    /// </summary>
    [JsonIgnore]
    public List<(string What, string Reason)>? RefusalsSink { get; set; }

    /// <summary>
    /// The mission this map plays, with the map's own seed — the ground and the battle are
    /// one world, and the file carries the seed once. The mission body inside a map may
    /// carry its own seed field; this is the one that wins, because it is the one the
    /// terrain was generated from.
    /// </summary>
    public MissionDefinition EffectiveMission => Mission with { Seed = Seed };
}
