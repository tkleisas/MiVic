using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Aspect and landform: the two fields that are facts about a cell <em>and its
/// neighbours</em>, so the two fields a single height sample cannot answer for.
/// <para>
/// The fixtures come first, and they are hand-built height fields, because a classifier is
/// only proved by ground whose shape is already known: a ramp, a peak, a pit, a saddle and a
/// flat top each have exactly one right answer, and a statistical test over a generated map
/// would have none. The counting tests at the end are the other half of the argument: this
/// project has three times shipped a shape that existed and never happened, and the only kind
/// of test that notices is one that counts.
/// </para>
/// </summary>
public sealed class TerrainShapeTests
{
    private const ulong Seed = 20250101;

    /// <summary>
    /// Fixture geometry, taken from the shipped constants rather than typed in, so a fixture
    /// still means what a map means if the lattice or the stride ever move. A fixture is a
    /// height field and the offsets are samples; the thresholds are millimetres per metre, so
    /// the two only agree if the cell size does.
    /// </summary>
    private const int Samples = SimConstants.TerrainResolution;

    private const int SampleMm = SimConstants.MapExtentMm / (SimConstants.TerrainResolution - 1);

    /// <summary>Ring radius, in samples: one navigation cell, which is what the layer uses.</summary>
    private const int Radius = SimConstants.NavGridStride;

    /// <summary>Wider fetch, in samples: where the layer looks for a cell's surroundings.</summary>
    private const int Fetch = TerrainLayer.ShapeFetchCells * Radius;

    private const int RingMm = Radius * SampleMm;

    private const int FarMm = Fetch * SampleMm;

    /// <summary>Ground a fixture is built on, and the step of a ramp across one sample.</summary>
    private const int BaseMm = 20_000;

    private const int RampStepMm = 1_500;

    /// <summary>Summit height of a cone fixture, and the plain its flanks stand on.</summary>
    private const int SummitMm = 24_000;

    private const int PlainMm = 1_000;

    /// <summary>Rise per sample along a cone's flank: 5 m across a ring, so a ring is steep.</summary>
    private const int ConeStepMm = 2_500;

    /// <summary>Height of a flat top above the ground around it, and of a flat floor below it.</summary>
    private const int StepMm = 12_000;

    /// <summary>
    /// Fewest cells a landform may cover on the standard map and still be a feature rather
    /// than an accident. One cell is not a shape: it is a rounding error that happens to be
    /// drawn, and a later step cannot make a tactical decision on it.
    /// </summary>
    private const int MinimumCellsPerLandform = 5;

    private static readonly string[] AspectNames =
        ["east", "south-east", "south", "south-west", "west", "north-west", "north", "north-east", "flat"];

    private static readonly string[] LandformNames =
        ["plain", "slope", "ridge", "valley", "pass", "plateau", "basin", "shelf"];

    /// <summary>
    /// A ramp has one aspect and it is the direction it falls, whichever of the eight it is.
    /// <para>
    /// Eight ramps, one per compass point, because the sector test is the part of this that a
    /// single example cannot check: the signs name a quadrant and the comparison between the
    /// two gradients decides whether the answer is an axis or a diagonal, and a ramp built to
    /// disagree with one of those two decisions is the only thing that can catch it.
    /// </para>
    /// <para>
    /// Each ramp also has an opposite that it must <em>not</em> be: an aspect that is one
    /// sector out is a small bug, and an aspect that is four sectors out is an upside-down
    /// sign, and both of them pass a test that only asks for "an aspect".
    /// </para>
    /// </summary>
    [Fact]
    public void ARampFacesTheWayItFalls()
    {
        // Rise per sample to the east and to the south: the fall runs the other way, which
        // is what an aspect names.
        (int RiseEast, int RiseSouth, int Expected)[] ramps =
        [
            (0, -RampStepMm, TerrainShape.South),
            (0, RampStepMm, TerrainShape.North),
            (RampStepMm, 0, TerrainShape.West),
            (-RampStepMm, 0, TerrainShape.East),
            (RampStepMm, RampStepMm, TerrainShape.NorthWest),
            (RampStepMm, -RampStepMm, TerrainShape.SouthWest),
            (-RampStepMm, -RampStepMm, TerrainShape.SouthEast),
            (-RampStepMm, RampStepMm, TerrainShape.NorthEast),
        ];

        List<string> failures = [];

        foreach ((int riseEast, int riseSouth, int expected) in ramps)
        {
            int[] heights = Field((x, z) => BaseMm + (riseEast * x) + (riseSouth * z));

            foreach ((int x, int z) in ((int, int)[])[(Samples / 2, 20), (Samples / 2, Samples / 2), (Samples / 2, Samples - 21)])
            {
                (int aspect, int landform) = Classify(heights, x, z);
                string report = Cell(heights, x, z) + $", expected {Named(AspectNames, expected)}";

                if (aspect != expected)
                {
                    failures.Add($"a ramp rising {Named(AspectNames, Opposite(expected))} came out facing " +
                        $"{Named(AspectNames, aspect)}, not {Named(AspectNames, expected)}: {report}");
                }

                if (aspect == Opposite(expected))
                {
                    failures.Add($"a ramp rising {Named(AspectNames, Opposite(expected))} faces the way it rises: {report}");
                }

                // The landform is a separate field, and a ramp is not a level place whatever
                // its angle, so nothing here may come back as one of the level shapes.
                if (landform is TerrainShape.Plateau or TerrainShape.Basin or TerrainShape.Shelf)
                {
                    failures.Add($"a uniform ramp came out as {Named(LandformNames, landform)}: {report}");
                }
            }
        }

        Assert.True(failures.Count == 0, string.Join("; ", failures));
    }

    /// <summary>
    /// Ground too gentle to face anywhere is flat, which is the rule that keeps a "facing"
    /// from flipping between neighbours on a plain and calling the flip information.
    /// </summary>
    [Fact]
    public void AShallowRampIsFlat()
    {
        // The fall across the whole span, two ring distances, is what the threshold is
        // written against: four times the step of one sample.
        int[] shallow = Field((x, z) => BaseMm + (ShallowStep() * z));
        int[] steep = Field((x, z) => BaseMm + (ShallowStep() * 4 * z));

        (int flatAspect, _) = Classify(shallow, Samples / 2, Samples / 2);
        (int steepAspect, _) = Classify(steep, Samples / 2, Samples / 2);
        int fall = ShallowStep() * 2 * Radius;

        string report =
            $"a fall of {fall} mm over {2 * RingMm} mm is {fall * 1_000 / (2 * RingMm)} permille against a threshold of " +
            $"{TerrainShape.AspectFallPermille}; the same ramp four times steeper falls " +
            $"{fall * 4 * 1_000 / (2 * RingMm)} permille: {Cell(shallow, Samples / 2, Samples / 2)}";

        Assert.True(
            flatAspect == TerrainAttributes.FlatAspect,
            $"ground that falls {fall} mm over {2 * RingMm} mm came out facing {Named(AspectNames, flatAspect)} " +
            $"instead of flat. {report}");

        Assert.True(
            steepAspect != TerrainAttributes.FlatAspect,
            $"ground four times steeper than the flat threshold came out flat, so AspectFallPermille is not " +
            $"what decides. {report}");
    }

    /// <summary>
    /// A single peak: the summit is a crest, the flanks are slopes, the ground at the foot is
    /// a shelf, and the plain beyond it is plain.
    /// <para>
    /// A cone with straight flanks rather than a dome, and that is deliberate: on any convex
    /// surface the mean of the ring sits below the cell, so a dome is a crest all over and
    /// would make "the cells around it are slopes" an assertion about nothing. Straight flanks
    /// have a ring whose mean is the cell itself, which is exactly the difference between a
    /// crest and a hillside.
    /// </para>
    /// </summary>
    [Fact]
    public void APeakIsACrestWithSlopesAroundIt()
    {
        int centre = Samples / 2;
        int[] heights = Field((x, z) =>
        {
            int reach = Math.Max(Math.Abs(x - centre), Math.Abs(z - centre));

            return Math.Max(PlainMm, SummitMm - (ConeStepMm * reach));
        });

        (int summitAspect, int summit) = Classify(heights, centre, centre);
        int flankSample = SummitMm - (ConeStepMm * 4);

        (int flankAspect, int flank) = Classify(heights, centre + 4, centre);
        (int otherFlankAspect, _) = Classify(heights, centre - 4, centre);
        (int footAspect, int foot) = Classify(heights, centre + 12, centre);
        (int farAspect, int far) = Classify(heights, centre + 30, centre);

        string report =
            $"summit {Cell(heights, centre, centre)}; flank at 4 samples {Cell(heights, centre + 4, centre)}; " +
            $"foot at 12 samples {Cell(heights, centre + 12, centre)}; plain at 30 samples {Cell(heights, centre + 30, centre)}";

        Assert.True(
            summit == TerrainShape.Ridge,
            $"the summit of a {SummitMm} mm peak came out as {Named(LandformNames, summit)}. A peak whose flanks are " +
            $"straight falls away in every direction, and the ring is {SummitMm - flankSample} mm below it, so it is a " +
            $"crest; a summit only reads as a plateau when its top is level and wide enough to be one. {report}");

        Assert.True(
            flank == TerrainShape.Slope,
            $"the flank of a peak, {SummitMm - flankSample} mm below its summit and falling to a plain, came out as " +
            $"{Named(LandformNames, flank)} rather than a slope. {report}");

        Assert.True(
            foot == TerrainShape.Shelf,
            $"flat ground one cone-radius from the foot of a peak came out as {Named(LandformNames, foot)} rather " +
            $"than a shelf. {report}");

        Assert.True(
            far == TerrainShape.Plain,
            $"ground 30 samples from a peak, out of reach of anything steep, came out as " +
            $"{Named(LandformNames, far)} rather than plain. {report}");

        // The whole point of two fields: a cone's summit still faces somewhere, and the flanks
        // face outwards. If the summit were flat the classifier would be reading a different
        // neighbourhood than the one it was handed.
        Assert.True(
            summitAspect == TerrainAttributes.FlatAspect,
            $"the axis point of a cone came out facing {Named(AspectNames, summitAspect)}: on the summit itself the " +
            $"ring is symmetric, so there is no fall line to name. {report}");

        Assert.True(
            flankAspect == TerrainShape.East && otherFlankAspect == TerrainShape.West,
            $"a cone's flanks face away from its summit, so the flank east of it faces east and the flank west of it " +
            $"faces west; they came out facing {Named(AspectNames, flankAspect)} and " +
            $"{Named(AspectNames, otherFlankAspect)}. {report}");

        Assert.True(
            farAspect == TerrainAttributes.FlatAspect,
            $"the plain around a peak came out facing {Named(AspectNames, farAspect)}. {report}");
    }

    /// <summary>
    /// A single pit: steep-sided it is a hollow, and flat-bottomed it is a basin. The same two
    /// shapes as the peak, read downwards, and the pair is what makes the ridge and the shelf
    /// tests mean something: a classifier that answered "crest" to everything would pass the
    /// peak fixture and fail this one.
    /// </summary>
    [Fact]
    public void APitIsAHollow()
    {
        int centre = Samples / 2;
        int[] cone = Field((x, z) =>
        {
            int reach = Math.Max(Math.Abs(x - centre), Math.Abs(z - centre));

            return Math.Min(SummitMm, PlainMm + (ConeStepMm * reach));
        });

        int[] floor = Field((x, z) =>
            Math.Abs(x - centre) <= 4 && Math.Abs(z - centre) <= 4 ? PlainMm : PlainMm + StepMm);

        (int coneAspect, int bowl) = Classify(cone, centre, centre);
        (int floorAspect, int flat) = Classify(floor, centre, centre);

        string report =
            $"cone pit centre {Cell(cone, centre, centre)}; flat-bottomed pit centre {Cell(floor, centre, centre)}";

        Assert.True(
            bowl == TerrainShape.Valley,
            $"the bottom of a {SummitMm - PlainMm} mm deep pit with straight sides came out as " +
            $"{Named(LandformNames, bowl)} rather than a hollow. {report}");

        Assert.True(
            bowl != TerrainShape.Ridge && bowl != TerrainShape.Plateau,
            $"the bottom of a pit came out as {Named(LandformNames, bowl)}: the sign of the test is the wrong way " +
            $"round. {report}");

        Assert.True(
            flat == TerrainShape.Basin,
            $"the level floor of a pit, with {StepMm} mm of ground standing above it all round, came out as " +
            $"{Named(LandformNames, flat)} rather than a basin. {report}");

        Assert.True(
            coneAspect == TerrainAttributes.FlatAspect,
            $"the axis point of a cone pit came out facing {Named(AspectNames, coneAspect)}. {report}");

        Assert.True(
            floorAspect == TerrainAttributes.FlatAspect,
            $"the level floor of a pit came out facing {Named(AspectNames, floorAspect)}. {report}");
    }

    /// <summary>
    /// A saddle is a pass, and the cells off the crossing are not: two ridges arriving from
    /// opposite sides and two valleys leaving on the other two, which is what the four-neighbour
    /// sign test describes. The corners are raised and sunk as well, so the fixture is a col
    /// between two high shoulders rather than a coincidence of four samples.
    /// </summary>
    [Fact]
    public void ASaddleIsAPass()
    {
        int centre = Samples / 2;
        int ridge = 2 * RampStepMm;
        int corner = 2 * RampStepMm;

        int[] heights = Field((x, z) =>
        {
            int dx = x - centre;
            int dz = z - centre;
            int saddle = ridge * (Math.Abs(dx) - Math.Abs(dz));

            // The two high corners and the two low ones: north-east and south-west stand with
            // the ridges, north-west and south-east with the valleys.
            int shoulders = dx == 0 || dz == 0 ? 0 : dx * dz < 0 ? corner : -corner;

            return BaseMm + saddle + shoulders;
        });

        (int aspect, int middle) = Classify(heights, centre, centre);
        (int ridgeSide, _) = Classify(heights, centre + 2, centre);
        (int valleySide, _) = Classify(heights, centre, centre + 2);

        string report =
            $"saddle centre {Cell(heights, centre, centre)}; two cells east, on the ridge {Cell(heights, centre + 2, centre)}; " +
            $"two cells south, in the valley {Cell(heights, centre, centre + 2)}";

        Assert.True(
            middle == TerrainShape.Pass,
            $"the middle of a saddle — the ring {ridge * 2} mm high to the east and west and {ridge * 2} mm low to " +
            $"the north and south — came out as {Named(LandformNames, middle)}. {report}");

        Assert.True(
            ridgeSide != TerrainShape.Pass && valleySide != TerrainShape.Pass,
            $"a cell on the ridge of a saddle came out as {Named(LandformNames, ridgeSide)} and a cell in its valley " +
            $"as {Named(LandformNames, valleySide)}, so the sign test is matching more than the crossing. {report}");

        // A saddle point is exactly where the gradient vanishes, which is why a pass is not a
        // flat cell and a flat cell is not a pass.
        Assert.True(
            aspect == TerrainAttributes.FlatAspect,
            $"the middle of a saddle came out facing {Named(AspectNames, aspect)}, but the fall line of a saddle is " +
            $"undefined. {report}");
    }

    /// <summary>
    /// Level ground is never a crest and never faces anywhere.
    /// <para>
    /// A flat top standing above the ground around it is a plateau, and the cells of its rim —
    /// level ground with a drop inside their own ring — are not "perfectly flat ground" and are
    /// allowed to be something else. So the guarantee is stated for the cells whose eight
    /// neighbours are all at their own height, and the fixture is built wide enough that there
    /// are nine of them rather than one.
    /// </para>
    /// </summary>
    [Fact]
    public void LevelGroundNeverClaimsACrest()
    {
        int centre = Samples / 2;
        int[] mesa = Field((x, z) =>
            Math.Abs(x - centre) <= 4 && Math.Abs(z - centre) <= 4 ? PlainMm + StepMm : PlainMm);

        int[] dead = Field((x, z) => BaseMm);

        List<string> failures = [];

        for (int dz = -2; dz <= 2; dz += 2)
        {
            for (int dx = -2; dx <= 2; dx += 2)
            {
                (int aspect, int landform) = Classify(mesa, centre + dx, centre + dz);

                if (landform != TerrainShape.Plateau)
                {
                    failures.Add($"the middle of a flat top came out as {Named(LandformNames, landform)}: " +
                        Cell(mesa, centre + dx, centre + dz));
                }

                if (aspect != TerrainAttributes.FlatAspect)
                {
                    failures.Add($"level ground came out facing {Named(AspectNames, aspect)}: " +
                        Cell(mesa, centre + dx, centre + dz));
                }
            }
        }

        Assert.True(
            failures.Count == 0,
            $"a flat top {StepMm} mm above the ground around it is not being read as a plateau: {string.Join("; ", failures)}");

        // And the guarantee itself, counted over both fixtures rather than argued: every cell
        // whose ring is its own height is level ground, and level ground that claims a crest is
        // the noise this threshold exists to suppress.
        int level = 0;
        int crests = 0;
        int facings = 0;

        foreach (int[] heights in new[] { mesa, dead })
        {
            for (int z = 2; z < Samples - 2; z++)
            {
                for (int x = 2; x < Samples - 2; x++)
                {
                    if (!IsLevel(heights, x, z))
                    {
                        continue;
                    }

                    level++;

                    (int aspect, int landform) = Classify(heights, x, z);

                    if (landform is TerrainShape.Ridge or TerrainShape.Valley)
                    {
                        crests++;
                    }

                    if (aspect != TerrainAttributes.FlatAspect)
                    {
                        facings++;
                    }
                }
            }
        }

        string report =
            $"{level} cells of the two fixtures have all eight neighbours at their own height; {crests} of them claim " +
            $"a crest and {facings} of them face somewhere";

        Assert.True(level > 8, $"the fixtures hold only {level} perfectly level cells, so this proves nothing. {report}");
        Assert.True(crests == 0, $"ground with no gradient claims a ridge or a valley. {report}");
        Assert.True(facings == 0, $"ground with no gradient claims a facing. {report}");
    }

    /// <summary>
    /// Every aspect appears on the standard map, flat included, and nothing writes an id the
    /// schema does not define. Eight sectors are eight ways for the answer to be wrong, and a
    /// sector that no cell ever lands in is exactly the failure this suite exists to catch.
    /// </summary>
    [Fact]
    public void EveryAspectAppearsOnTheStandardMap()
    {
        TerrainLayer terrain = Build().TerrainTypes;
        int[] counts = new int[16];

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            counts[terrain.AttributesAt(cell).Aspect]++;
        }

        string report = $"aspect histogram over {terrain.CellCount} cells — {Histogram(counts, AspectNames)}";

        for (int aspect = 0; aspect < TerrainShape.RingSamples; aspect++)
        {
            Assert.True(counts[aspect] > 0, $"no cell on the map faces {Named(AspectNames, aspect)}. {report}");
        }

        Assert.True(
            counts[TerrainAttributes.FlatAspect] > 0,
            $"no cell on the map is flat, so the flat rule never fires and every cell faces somewhere. {report}");

        for (int aspect = TerrainAttributes.FlatAspect + 1; aspect < counts.Length; aspect++)
        {
            Assert.True(
                counts[aspect] == 0,
                $"{counts[aspect]} cells carry aspect {aspect}, which the schema leaves unused. {report}");
        }

        // A facing that every cell had would be as useless as one no cell had: the flat
        // threshold has to be somewhere the map actually reaches.
        int facing = terrain.CellCount - counts[TerrainAttributes.FlatAspect];

        Assert.True(
            counts[TerrainAttributes.FlatAspect] * 4 < terrain.CellCount && facing * 4 > terrain.CellCount * 3,
            $"the flat threshold is at the wrong end of the map: {counts[TerrainAttributes.FlatAspect]} flat cells " +
            $"and {facing} facing ones. {report}");
    }

    /// <summary>
    /// Every landform appears on the standard map, and each of them on enough cells to be a
    /// place rather than an accident — a plateau of one cell is a shape a later step can key a
    /// tactical decision on only by mistake.
    /// </summary>
    [Fact]
    public void EveryLandformAppearsOnTheStandardMap()
    {
        TerrainLayer terrain = Build().TerrainTypes;
        int[] counts = new int[16];

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            counts[terrain.AttributesAt(cell).Landform]++;
        }

        string report = $"landform histogram over {terrain.CellCount} cells — {Histogram(counts, LandformNames)}";

        int populated = 0;

        for (int landform = 0; landform <= TerrainAttributes.MaxLandform; landform++)
        {
            Assert.True(
                counts[landform] >= MinimumCellsPerLandform,
                $"only {counts[landform]} cells of {terrain.CellCount} are {Named(LandformNames, landform)}, and a " +
                $"shape under {MinimumCellsPerLandform} cells is one nothing can key on. {report}");

            populated += counts[landform];
        }

        for (int landform = TerrainAttributes.MaxLandform + 1; landform < counts.Length; landform++)
        {
            Assert.True(
                counts[landform] == 0,
                $"{counts[landform]} cells carry landform {landform}, which the schema does not define. {report}");
        }

        Assert.True(populated == terrain.CellCount, $"the histogram accounts for {populated} of {terrain.CellCount} cells. {report}");
    }

    /// <summary>
    /// Aspect is coherent with the ground: the steepest fall on the map faces the way it falls,
    /// and the same is true of the whole map, not just of one clean example.
    /// <para>
    /// The example is the evidence — a cell whose west ring is high and whose east ring is low,
    /// and which must therefore face east — and the count is the backstop: for every cell whose
    /// aspect is one of the four axes, the ground in the aspect direction is <em>required</em>
    /// to be lower than the ground opposite, because those are the two samples the answer was
    /// computed from. A diagonal aspect is not computed from its diagonal samples, so it is
    /// counted and reported rather than demanded.
    /// </para>
    /// </summary>
    [Fact]
    public void AspectFacesTheWayTheGroundFalls()
    {
        SimWorld world = Build();
        TerrainLayer terrain = world.TerrainTypes;
        HeightMap map = world.Terrain;
        int stride = Math.Max(1, world.Navigation.CellSizeMm / map.CellSizeMm);

        // Four clean examples: the hardest fall on the map in each of the four axes.
        (int Expected, int Axis)[] searched =
        [
            (TerrainShape.East, 0),
            (TerrainShape.West, 1),
            (TerrainShape.South, 2),
            (TerrainShape.North, 3),
        ];

        Span<int> ring = new int[TerrainShape.RingSamples];

        foreach ((int expected, int axis) in searched)
        {
            (int cell, int fall) = SteepestFall(map, terrain, stride, axis);

            Assert.True(
                cell >= 0,
                $"the map has no cell whose fall is cleanly towards {Named(AspectNames, expected)}, so there is " +
                $"nothing here to check the example against.");

            int x = cell % terrain.Size;
            int z = cell / terrain.Size;
            int sampleX = Math.Min(x * stride, map.Size - 1);
            int sampleZ = Math.Min(z * stride, map.Size - 1);

            TerrainShape.SampleRing(map.RawHeights, map.Size, sampleX, sampleZ, Radius, ring);

            int aspect = terrain.AttributesAt(cell).Aspect;
            int downhill = ring[expected];
            int uphill = ring[Opposite(expected)];
            int centre = map.HeightAt(sampleX, sampleZ);

            string report =
                $"the strongest {Named(AspectNames, expected)} fall on the map is at cell ({x},{z}): " +
                $"{Named(AspectNames, Opposite(expected))} ring {uphill} mm, centre {centre} mm, " +
                $"{Named(AspectNames, expected)} ring {downhill} mm, a fall of {fall} mm ({fall * 1_000 / (2 * RingMm)} permille), " +
                $"and the cell's aspect is {Named(AspectNames, aspect)}";

            Assert.True(
                aspect == expected,
                $"ground falling {fall} mm towards {Named(AspectNames, expected)} came out facing " +
                $"{Named(AspectNames, aspect)}. {report}");
        }

        int axial = 0;
        int axialWrong = 0;
        int diagonal = 0;
        int diagonalWrong = 0;
        int level = 0;
        int levelFacing = 0;

        Span<int> probe = stackalloc int[TerrainShape.RingSamples];

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            int aspect = terrain.AttributesAt(cell).Aspect;
            int x = cell % terrain.Size;
            int z = cell / terrain.Size;
            int sampleX = Math.Min(x * stride, map.Size - 1);
            int sampleZ = Math.Min(z * stride, map.Size - 1);
            int centre = map.HeightAt(sampleX, sampleZ);

            TerrainShape.SampleRing(map.RawHeights, map.Size, sampleX, sampleZ, Radius, probe);

            bool flat = true;

            for (int i = 0; i < TerrainShape.RingSamples; i++)
            {
                flat &= probe[i] == centre;
            }

            if (flat)
            {
                level++;

                if (aspect != TerrainAttributes.FlatAspect)
                {
                    levelFacing++;
                }

                continue;
            }

            if (aspect == TerrainAttributes.FlatAspect)
            {
                continue;
            }

            bool wrong = probe[aspect] >= probe[Opposite(aspect)];

            if (aspect is TerrainShape.East or TerrainShape.South or TerrainShape.West or TerrainShape.North)
            {
                axial++;

                if (wrong)
                {
                    axialWrong++;
                }
            }
            else
            {
                diagonal++;

                if (wrong)
                {
                    diagonalWrong++;
                }
            }
        }

        string counts =
            $"{axial} cells face along an axis, of which {axialWrong} do not stand lower in that direction; " +
            $"{diagonal} face a diagonal, of which {diagonalWrong} do not; {level} cells have a ring level with " +
            $"themselves, of which {levelFacing} face somewhere";

        Assert.True(axial > 100, $"too few axial aspects on the map to check the sign of the answer. {counts}");
        Assert.True(axialWrong == 0, $"an axial aspect does not point downhill. {counts}");

        // The diagonal answer comes from the two axis gradients, so the diagonal sample itself
        // is not part of it and may disagree where the ground two cells out is not the ground
        // the gradient measured. It is still expected to agree nearly always.
        Assert.True(
            diagonalWrong * 10 <= diagonal,
            $"more than a tenth of the diagonal aspects contradict the ground in their own direction. {counts}");

        Assert.True(level > 0, $"no cell on the map has a ring level with itself, so the flat rule is untested here. {counts}");
        Assert.True(levelFacing == 0, $"level ground faces somewhere. {counts}");
    }

    /// <summary>The compass point four sectors round: what a cell faces uphill.</summary>
    private static int Opposite(int aspect) => (aspect + (TerrainShape.RingSamples / 2)) % TerrainShape.RingSamples;

    /// <summary>
    /// The cell with the largest fall along one axis, and the size of that fall, among the
    /// cells whose fall is unambiguously along that axis: a cell that falls hardest to the west
    /// and nearly as hard to the south faces south-west, which is right, so the clean example
    /// has to be one where the other gradient is a fifth of the one being asked about.
    /// Axis 0 to 3 is east, west, south, north, and the fall towards the axis is the height
    /// opposite it less the height towards it.
    /// </summary>
    private static (int Cell, int Fall) SteepestFall(
        HeightMap map,
        TerrainLayer terrain,
        int stride,
        int axis)
    {
        Span<int> ring = stackalloc int[TerrainShape.RingSamples];
        ReadOnlySpan<int> heights = map.RawHeights;
        int best = -1;
        int bestFall = int.MinValue;

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            int sampleX = Math.Min((cell % terrain.Size) * stride, map.Size - 1);
            int sampleZ = Math.Min((cell / terrain.Size) * stride, map.Size - 1);

            TerrainShape.SampleRing(heights, map.Size, sampleX, sampleZ, Radius, ring);

            int towards = axis switch
            {
                0 => TerrainShape.East,
                1 => TerrainShape.West,
                2 => TerrainShape.South,
                _ => TerrainShape.North,
            };

            int fall = ring[Opposite(towards)] - ring[towards];
            int across = axis <= 1
                ? Math.Abs(ring[TerrainShape.North] - ring[TerrainShape.South])
                : Math.Abs(ring[TerrainShape.East] - ring[TerrainShape.West]);

            if (fall <= 0 || across * 5 > fall)
            {
                continue;
            }

            if (fall > bestFall)
            {
                bestFall = fall;
                best = cell;
            }
        }

        return (best, bestFall);
    }

    /// <summary>True when a cell's eight ring neighbours are all at its own height.</summary>
    private static bool IsLevel(int[] heights, int x, int z)
    {
        Span<int> ring = stackalloc int[TerrainShape.RingSamples];

        TerrainShape.SampleRing(heights, Samples, x, z, Radius, ring);

        int centre = heights[(z * Samples) + x];

        for (int i = 0; i < TerrainShape.RingSamples; i++)
        {
            if (ring[i] != centre)
            {
                return false;
            }
        }

        return true;
    }

    /// <summary>
    /// Classifies a fixture cell the way the layer does: the ring and the wider fetch sampled
    /// at the same radii, and decision made by the shipped classifier.
    /// </summary>
    private static (int Aspect, int Landform) Classify(int[] heights, int x, int z)
    {
        Span<int> ring = stackalloc int[TerrainShape.RingSamples];
        Span<int> far = stackalloc int[TerrainShape.RingSamples];

        TerrainShape.SampleRing(heights, Samples, x, z, Radius, ring);
        TerrainShape.SampleRing(heights, Samples, x, z, Fetch, far);

        int centre = heights[(z * Samples) + x];

        return (
            TerrainShape.AspectOf(ring, RingMm),
            TerrainShape.LandformOf(ring, far, centre, RingMm, FarMm));
    }

    /// <summary>A hand-built height field, filled from a shape.</summary>
    private static int[] Field(Func<int, int, int> shape)
    {
        int[] heights = new int[Samples * Samples];

        for (int z = 0; z < Samples; z++)
        {
            for (int x = 0; x < Samples; x++)
            {
                heights[(z * Samples) + x] = shape(x, z);
            }
        }

        return heights;
    }

    /// <summary>A fixture cell in words, with the numbers it was decided from.</summary>
    private static string Cell(int[] heights, int x, int z)
    {
        Span<int> ring = stackalloc int[TerrainShape.RingSamples];

        TerrainShape.SampleRing(heights, Samples, x, z, Radius, ring);

        (int aspect, int landform) = Classify(heights, x, z);
        int centre = heights[(z * Samples) + x];
        List<string> ringText = [];

        for (int i = 0; i < TerrainShape.RingSamples; i++)
        {
            ringText.Add($"{Named(AspectNames, i)} {ring[i] - centre:+#;-#;0}");
        }

        return $"cell ({x},{z}) at {centre} mm is {Named(LandformNames, landform)} facing {Named(AspectNames, aspect)}, " +
            $"ring against the centre: {string.Join(", ", ringText)}";
    }

    private static string Named(string[] names, int value)
        => value >= 0 && value < names.Length ? $"{names[value]} ({value})" : $"unknown ({value})";

    private static string Histogram(int[] counts, string[] names)
        => string.Join(", ", Enumerable.Range(0, 16).Where(id => counts[id] > 0).Select(id => $"{Named(names, id)}: {counts[id]}"));

    /// <summary>
    /// The step of the shallowest ramp that is still a facing, in millimetres per sample: one
    /// quarter of the fall the flat threshold allows across a span.
    /// </summary>
    private static int ShallowStep()
        => (TerrainShape.AspectFallPermille * 2 * RingMm / 1_000) / (2 * Radius * 2);

    /// <summary>
    /// A real skirmish world: the scenario is what generates the terrain, so a bare
    /// <see cref="SimWorld"/> has nothing in it to measure.
    /// </summary>
    private static SimWorld Build(ulong seed = Seed)
    {
        var world = new SimWorld(seed, capacity: 1024);
        Scenario.Build(world, ScenarioKind.Skirmish);

        return world;
    }
}
