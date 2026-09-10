using MiVic.Core.Numerics;

namespace MiVic.Core.Terrain;

/// <summary>
/// The shape of the ground: which way a cell faces, and what kind of place it is.
/// <para>
/// Both fields are <em>neighbourhood</em> facts — a cell alone cannot be a ridge or a
/// slope, any more than one number can be a hill — so both are decided from a ring of
/// heights sampled around the cell. Every step of it is integer arithmetic on height
/// differences: eight sectors need the signs of two gradients and one comparison between
/// them, and <c>atan2</c> over floats would be a determinism bug waiting for a runtime
/// to change its mind about rounding.
/// </para>
/// <para>
/// <b>The aspect encoding, which everything downstream keys on.</b> Ids 0-7 are the eight
/// compass points, in the same rotation as <c>Entity.Heading</c>: 0 is +X, which is
/// <b>east</b>, and every step of one turns 45° towards +Z, which is south. So,
/// reading clockwise on the map as it is drawn with X to the right and Z downwards:
/// 0 east, 1 south-east, 2 south, 3 south-west, 4 west, 5 north-west, 6 north,
/// 7 north-east. <see cref="TerrainAttributes.FlatAspect"/> (8) is ground with no fall
/// line to speak of, and 9-15 are unused.
/// </para>
/// <para>
/// An aspect is the direction the ground <em>falls towards</em> — the way a slope faces —
/// so a hill that rises to the north has an aspect of south. The rotation is deliberately
/// the one <c>Heading</c> already uses: a unit walking downhill has the aspect of the cell
/// it is on as its own heading octant, which is what a later step needs to tell a reverse
/// slope from a forward one without introducing a second angle convention to get wrong.
/// </para>
/// <para>
/// Thresholds are written in permille of distance — millimetres of height per metre of
/// ground, the same unit <see cref="HeightMap.SlopePermille"/> returns and
/// <see cref="TerrainLayer.RockSlopePermille"/> is written in — so they mean the same
/// thing whatever radius the ring is sampled at.
/// </para>
/// </summary>
public static class TerrainShape
{
    /// <summary>
    /// Heights sampled around a cell: its eight neighbours, one per aspect id, so
    /// <c>ring[<see cref="TerrainShape.East"/>]</c> is the ground one ring to the east.
    /// </summary>
    public const int RingSamples = 8;

    /// <summary>+X, the aspect of ground falling towards the east.</summary>
    public const int East = 0;

    /// <summary>+X+Z, the aspect of ground falling towards the south-east.</summary>
    public const int SouthEast = 1;

    /// <summary>+Z, the aspect of ground falling towards the south.</summary>
    public const int South = 2;

    /// <summary>-X+Z, the aspect of ground falling towards the south-west.</summary>
    public const int SouthWest = 3;

    /// <summary>-X, the aspect of ground falling towards the west.</summary>
    public const int West = 4;

    /// <summary>-X-Z, the aspect of ground falling towards the north-west.</summary>
    public const int NorthWest = 5;

    /// <summary>-Z, the aspect of ground falling towards the north.</summary>
    public const int North = 6;

    /// <summary>+X-Z, the aspect of ground falling towards the north-east.</summary>
    public const int NorthEast = 7;

    /// <summary>Ordinary ground: neither steep nor any more specific shape.</summary>
    public const int Plain = 0;

    /// <summary>Ground tilted enough to be a slope, and not part of any sharper shape.</summary>
    public const int Slope = 1;

    /// <summary>A crest: this cell stands above the mean of the ground around it.</summary>
    public const int Ridge = 2;

    /// <summary>A hollow: this cell sits below the mean of the ground around it.</summary>
    public const int Valley = 3;

    /// <summary>A saddle: high along one axis of the cross and low along the other.</summary>
    public const int Pass = 4;

    /// <summary>Level ground with lower ground on most sides: a flat-topped height.</summary>
    public const int Plateau = 5;

    /// <summary>Level ground with higher ground on most sides: a flat-bottomed hollow.</summary>
    public const int Basin = 6;

    /// <summary>Level ground with steep ground beside it: a foot, a shoulder, a terrace.</summary>
    public const int Shelf = 7;

    /// <summary>
    /// Fall line, in permille of the distance between samples, below which ground faces
    /// nowhere: 45 mm per metre is about 2.6°, and a "facing" that flips between neighbours
    /// on ground with no gradient is noise pretending to be information.
    /// </summary>
    public const int AspectFallPermille = 45;

    /// <summary>
    /// How far, in permille of the ring distance, a cell must sit above the mean of the
    /// ring around it to be a crest — and below it to be a hollow.
    /// <para>
    /// One knob for both, because they are one test with the sign turned round, and two
    /// knobs for one shape would only be two knobs to get out of step.
    /// </para>
    /// </summary>
    public const int RidgePermille = 160;

    /// <summary>
    /// Margin, in permille of the ring distance, on each of the four cross tests that
    /// make a saddle. A pass is where two highs and two lows nearly meet, so the sign
    /// alone would call every gentle crossing a pass; this is how much of a crossing it
    /// has to be.
    /// </summary>
    public const int SaddlePermille = 25;

    /// <summary>
    /// Ring difference, in permille of the ring distance, up to which a cell's own ground
    /// counts as level.
    /// <para>
    /// 200 — an eighth of a right angle, or 11° — sounds generous for a word that means
    /// "level", and is: it is cut against this generator rather than against a spirit level.
    /// The median cell on the standard map differs from its ring by 389 mm per metre and falls
    /// 188 mm per metre across itself, so a threshold tight enough to please a surveyor
    /// describes ground the map does not contain, and a shape that never occurs is a shape
    /// nothing downstream can be tested on. That is how the sand band, the snow line and the
    /// volcano line each came to be empty. What makes a plateau a plateau is the
    /// <see cref="EnclosurePermille"/> test, not this one: this only says the top is not itself
    /// a hillside.
    /// </para>
    /// <para>
    /// Half of the level test. <see cref="RidgePermille"/> is the other half, and a cell has
    /// to pass both before it is described by the ground it sits among rather than by its own.
    /// </para>
    /// </summary>
    public const int FlatPermille = 200;

    /// <summary>
    /// Ring difference, in permille of the ring distance, at which ground is steep enough
    /// to be called a slope at all. Ground between this and <see cref="FlatPermille"/> is
    /// neither flat nor steep, and comes out as plain.
    /// </summary>
    public const int SteepPermille = 400;

    /// <summary>
    /// How far, in permille of the distance to the wider fetch, ground has to differ from
    /// a cell before that ground counts as being around it rather than beside it.
    /// </summary>
    public const int EnclosurePermille = 90;

    /// <summary>
    /// Far samples out of eight that have to lie above a cell (for a basin) or below it
    /// (for a plateau): a majority, rather than all eight.
    /// <para>
    /// "High relative to its surroundings" is a claim about the ground around a cell on
    /// balance, and a plateau with one gully running off its edge is still a plateau. On
    /// this terrain, demanding all eight describes nothing that occurs — the same empty
    /// shape in different clothes.
    /// </para>
    /// </summary>
    public const int EnclosedSamples = 5;

    /// <summary>
    /// How far, in permille of the distance to the wider fetch, steep ground beside a
    /// level cell has to differ from it before the cell is a shelf — the foot or the
    /// shoulder of something, rather than the middle of a plain.
    /// </summary>
    public const int ShelfPermille = 200;

    /// <summary>
    /// Fills <paramref name="ring"/> with a height field's eight neighbours of a lattice
    /// point, indexed by aspect so that a caller can read the ground in the direction it
    /// is asking about.
    /// <para>
    /// Samples outside the field take the height of its edge, which is what
    /// <see cref="HeightMap.HeightAt"/> does for every other neighbourhood rule in the
    /// layer. The alternative — dropping them — would fold a false cliff into the border
    /// on top of the cliff the height generator may already have put there.
    /// </para>
    /// </summary>
    /// <param name="heights">The height field, row major, <paramref name="size"/> per row.</param>
    /// <param name="size">Samples per side of the height field.</param>
    /// <param name="x">Lattice column of the centre.</param>
    /// <param name="z">Lattice row of the centre.</param>
    /// <param name="radius">Distance to each ring sample, in height-field samples.</param>
    /// <param name="ring">Receives eight heights, indexed by aspect.</param>
    public static void SampleRing(
        ReadOnlySpan<int> heights,
        int size,
        int x,
        int z,
        int radius,
        Span<int> ring)
    {
        ring[East] = Sample(heights, size, x + radius, z);
        ring[SouthEast] = Sample(heights, size, x + radius, z + radius);
        ring[South] = Sample(heights, size, x, z + radius);
        ring[SouthWest] = Sample(heights, size, x - radius, z + radius);
        ring[West] = Sample(heights, size, x - radius, z);
        ring[NorthWest] = Sample(heights, size, x - radius, z - radius);
        ring[North] = Sample(heights, size, x, z - radius);
        ring[NorthEast] = Sample(heights, size, x + radius, z - radius);
    }

    /// <summary>
    /// The way a cell faces: the compass point its ground falls towards, or
    /// <see cref="TerrainAttributes.FlatAspect"/> when it does not fall anywhere in
    /// particular.
    /// </summary>
    /// <param name="ring">The eight neighbours, indexed by aspect.</param>
    /// <param name="ringMm">Distance from the cell to a ring sample, in millimetres.</param>
    public static int AspectOf(ReadOnlySpan<int> ring, int ringMm)
    {
        // Two axis gradients, each over its own two ring samples. Positive means the
        // ground is higher on that side, so the fall line runs the other way — which is
        // the whole of the trigonometry this needs: the sign of the two gradients and
        // which of them is bigger names one of eight sectors.
        int fallEast = ring[West] - ring[East];
        int fallSouth = ring[North] - ring[South];

        int across = Math.Abs(fallEast);
        int along = Math.Abs(fallSouth);

        // The gradient is measured over the whole span, two ring distances, because that
        // is the ground the difference was taken across.
        if (Math.Max(across, along) * 1_000 < AspectFallPermille * 2 * ringMm)
        {
            return TerrainAttributes.FlatAspect;
        }

        // Diagonals win only when neither axis dominates: the boundary is at 22.5° from
        // an axis, and tan(22.5°) = sqrt(2) - 1 is irrational, so it is compared against
        // two fifths instead. That puts the corner at 21.8°, which is 0.7° of a sector
        // that is 45° wide, and costs two integer multiplies rather than a table.
        if (across * 5 >= along * 2 && along * 5 >= across * 2)
        {
            return fallEast > 0
                ? (fallSouth > 0 ? SouthEast : NorthEast)
                : (fallSouth > 0 ? SouthWest : NorthWest);
        }

        return across > along
            ? (fallEast > 0 ? East : West)
            : (fallSouth > 0 ? South : North);
    }

    /// <summary>
    /// What kind of place a cell is, from the ground around it.
    /// <para>
    /// The tests are applied most specific first, and the order is the whole design,
    /// because a cell is usually several of these at once:
    /// </para>
    /// <list type="number">
    /// <item><description>
    /// <b>Pass.</b> A saddle is a specific arrangement of all four cross neighbours — two
    /// opposite ones above the cell and two below — and it is also high on one axis, low
    /// on the other and tilted along both, so every later test would claim it. Nothing
    /// beyond the subtractions of the sign tests is needed to recognise one.
    /// </description></item>
    /// <item><description>
    /// <b>Plateau, basin and shelf.</b> The level shapes: ground that neither tilts at the
    /// cell nor stands away from the mean of the ground around it, and whose description is
    /// therefore about what it sits among. Enclosure is tested before the shelf, so level
    /// ground with lower ground on most sides is a plateau rather than a shelf, and level
    /// ground with high ground on one side and low on the other — the foot of a hill — is a
    /// shelf rather than a basin.
    /// </description></item>
    /// <item><description>
    /// <b>Ridge and valley.</b> A crest: ground that falls away at the cell, which is why
    /// these are only reachable from the tilted branch. Testing them before the level
    /// shapes would take the whole of every plateau's top as a ridge, and no plateau would
    /// ever appear on a map — which is the failure this project has already had three
    /// times, and the reason the counting tests exist.
    /// </description></item>
    /// <item><description>
    /// <b>Slope</b>, for ground tilted enough that being tilted is the most useful thing
    /// about it, and <b>plain</b>, which is the floor of the classification rather than a
    /// description of flatness: ground can be level on a plateau, on a shelf or in a basin,
    /// and only one of those four words fits in the field.
    /// </description></item>
    /// </list>
    /// </summary>
    /// <param name="ring">The eight neighbours one ring away, indexed by aspect.</param>
    /// <param name="far">The eight neighbours a wider fetch away, indexed by aspect.</param>
    /// <param name="centre">Height of the cell itself, in millimetres.</param>
    /// <param name="ringMm">Distance from the cell to a ring sample, in millimetres.</param>
    /// <param name="farMm">Distance from the cell to a far sample, in millimetres.</param>
    public static int LandformOf(
        ReadOnlySpan<int> ring,
        ReadOnlySpan<int> far,
        int centre,
        int ringMm,
        int farMm)
    {
        int ridgeMm = RidgePermille * ringMm / 1_000;
        int flatMm = FlatPermille * ringMm / 1_000;

        if (IsSaddle(ring, centre, SaddlePermille * ringMm / 1_000))
        {
            return Pass;
        }

        // How tilted the cell's own ground is, how far and which way the ground around it
        // reaches, and where the cell sits against the mean of the ring.
        //
        // The diagonals are sqrt(2) further away than the cross neighbours, so the same
        // millimetre difference there is a gentler gradient, and every test below is
        // therefore harder to satisfy on a diagonal than on an axis. That is the direction
        // a threshold should be wrong in, and it costs less than scaling each sample by
        // its distance.
        int total = 0;
        int steepest = 0;
        int reach = 0;
        int enclosedAbove = 0;
        int enclosedBelow = 0;
        int enclosureMm = EnclosurePermille * farMm / 1_000;

        for (int i = 0; i < RingSamples; i++)
        {
            total += ring[i];
            steepest = Math.Max(steepest, Math.Abs(ring[i] - centre));

            int difference = far[i] - centre;

            reach = Math.Max(reach, Math.Abs(difference));

            if (difference >= enclosureMm)
            {
                enclosedAbove++;
            }
            else if (-difference >= enclosureMm)
            {
                enclosedBelow++;
            }
        }

        // Against the *mean* of the eight, not a count of how many of them are lower. A crest
        // is a line, so the two neighbours along it sit at the same height as the cell: "most
        // of the neighbours are below" is false on exactly the cells that are a ridge, while
        // the mean still drops under the cell.
        int above = centre - ((total + (RingSamples / 2)) / RingSamples);

        // Level means the cell's own ground neither tilts nor stands away from the ground
        // around it. The second half of that is not redundant: the apex of a sharp cone has a
        // ring that is perfectly symmetric, so it tilts nowhere and is not standing on a
        // pedestal either, and calling a mountain top a plateau because the sampling happened
        // to be even would be a classifier reading its own lattice rather than the ground.
        bool level = steepest <= flatMm && Math.Abs(above) < ridgeMm;

        if (level)
        {
            if (enclosedBelow >= EnclosedSamples)
            {
                return Plateau;
            }

            if (enclosedAbove >= EnclosedSamples)
            {
                return Basin;
            }

            if (reach >= ShelfPermille * farMm / 1_000)
            {
                return Shelf;
            }
        }
        else
        {
            if (above >= ridgeMm)
            {
                return Ridge;
            }

            if (-above >= ridgeMm)
            {
                return Valley;
            }
        }

        return steepest >= SteepPermille * ringMm / 1_000 ? Slope : Plain;
    }

    /// <summary>
    /// True when the cross around a cell is high on one axis and low on the other:
    /// two opposite neighbours above it and two below, both by a margin.
    /// </summary>
    private static bool IsSaddle(ReadOnlySpan<int> ring, int centre, int marginMm)
    {
        bool northHigh = ring[North] - centre >= marginMm;
        bool southHigh = ring[South] - centre >= marginMm;
        bool eastHigh = ring[East] - centre >= marginMm;
        bool westHigh = ring[West] - centre >= marginMm;
        bool northLow = centre - ring[North] >= marginMm;
        bool southLow = centre - ring[South] >= marginMm;
        bool eastLow = centre - ring[East] >= marginMm;
        bool westLow = centre - ring[West] >= marginMm;

        return (northHigh && southHigh && eastLow && westLow) ||
            (eastHigh && westHigh && northLow && southLow);
    }

    /// <summary>A height-field sample, clamped to the field's edge.</summary>
    private static int Sample(ReadOnlySpan<int> heights, int size, int x, int z)
        => heights[(IntMath.Clamp(z, 0, size - 1) * size) + IntMath.Clamp(x, 0, size - 1)];
}
