using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The query and the fog are one rule, and these tests are the measurement rather than the claim.
/// <para>
/// "Is this inside a radar's coverage" was answered twice: once by the fog pass, which marks whole
/// navigation cells, and once by <see cref="PowerSystem.RadarNetwork.Covers"/>, which tested one
/// distance between two points. Those are not the same question — the fog has no answer about a
/// point, and the query had none about a cell — so the two disagreed by whatever the lattice and
/// the position happened to be worth, up to a navigation cell (9.4 m) of ground at the rim of a
/// 260 m disc. <see cref="CombatSystem.CanEngage"/> asks the query twice, so that band was a strip
/// of the battlefield where a target could be shot by one rule and be invisible to the other.
/// </para>
/// <para>
/// <b>So the test is exhaustive and not a sample.</b> Every cell of a map is asked about, because a
/// rim is a boundary and a boundary is exactly where two implementations of it differ: a handful
/// of chosen positions can only ever show that the two agree where they were already expected to.
/// The same shape pins the discs that are refused — a building site, a radar the grid has shed —
/// because a rule about who watches is a rule about who does not.
/// </para>
/// </summary>
public sealed class CoverageQueryTests
{
    private const ulong Seed = 20250101;

    /// <summary>The lane the sensor tests work along: no lava and no snow anywhere on it.</summary>
    private const int Lane = -70_000;

    /// <summary>
    /// Two full stagger cycles: more than the ten ticks the visibility pass takes to reach every
    /// slot, and inside the eleven a stamped cell stays visible for, so the fog has had its say
    /// about every entity in a world that has not moved.
    /// </summary>
    private const int Ticks = VisionSystem.UpdateInterval * 2;

    private static SimWorld World(int capacity = 64) => new(Seed, capacity);

    /// <summary>One entity on the lane at one x, written after the spawn as the detection tests do.</summary>
    private static EntityId At(SimWorld world, int team, UnitKind kind, int x)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        EntityId id = world.Spawn(
            Faction.Soviet, team, kind, new WorldPos(x, 0, Lane), Fix32.FromInt(definition.SpeedMmPerTick),
            definition.Health);

        ref Entity entity = ref world.GetRefBySlot(id.Slot);
        entity.Position = new WorldPos(x, 0, Lane);
        entity.MoveGoal = entity.Position;
        return id;
    }

    /// <summary>The fog's answer about one cell, which is the answer everything else has to match.</summary>
    private static bool Painted(SimWorld world, int team, int cell) => world.Visibility.IsVisible(team, cell);

    /// <summary>
    /// The team's own answer about a cell, as the sensor query gives it: whether any entity that
    /// <see cref="VisionSystem.SensorOf"/> says is watching paints that cell's centre.
    /// </summary>
    private static bool TheTeamWatches(SimWorld world, int team, int cell)
    {
        WorldPos centre = world.Navigation.CentreOf(cell);

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            int radius = VisionSystem.SensorOf(world, slot, out _);

            if (radius <= 0)
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && VisionSystem.Covers(world, entity.Position, radius, centre))
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>
    /// A cell that disagrees with the query, as a sentence naming the cell and by how much — so a
    /// failure says which piece of ground the two rules parted company over rather than that
    /// something, somewhere, is false.
    /// </summary>
    private static string Disagreement(SimWorld world, int cell, bool query, bool fog)
    {
        WorldPos centre = world.Navigation.CentreOf(cell);
        int metreX = centre.X / WorldPos.MmPerMetre;
        int metreZ = centre.Z / WorldPos.MmPerMetre;

        return $"cell ({world.Navigation.CellX(cell)},{world.Navigation.CellZ(cell)}) at " +
               $"({metreX}, {metreZ}) m: the query says {query}, the fog says {fog}";
    }

    /// <summary>
    /// <b>Every navigation cell of a map, asked of the fog and of the radar query.</b>
    /// <para>
    /// One lit radar stands alone in the world — a bare base's standby generation runs one, so it
    /// needs no power plant and no headquarters — and nothing else is there to paint a cell the
    /// radar did not, which is what makes the two answers comparable cell by cell.
    /// </para>
    /// <para>
    /// This is the test that fails if either half of the rule is changed without the other. It
    /// fails against the query this replaced in a way worth recording: the fog painted cells whose
    /// centres are outside the disc (the rasteriser filled the chord's own ends along x) while the
    /// query denied ground the fog had not painted, and the two parted company over 49 and 44
    /// cells of a 2 410-cell disc.
    /// </para>
    /// </summary>
    [Fact]
    public void TheRadarQueryAndTheFogAgreeOnEveryCellOfTheMap()
    {
        SimWorld world = World();
        EntityId radar = At(world, team: 0, UnitKind.RadarStation, x: 0);

        world.RunTicks(Ticks);

        Assert.True(world.IsRadarLit(radar.Slot), "the radar has no power, so there is no disc here to compare");
        Assert.Equal(1, world.AliveCount);

        List<string> disagreements = [];

        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            bool query = world.Radars.Covers(world, 0, world.Navigation.CentreOf(cell));
            bool fog = Painted(world, 0, cell);

            if (query != fog && disagreements.Count < 5)
            {
                disagreements.Add(Disagreement(world, cell, query, fog));
            }
        }

        Assert.Empty(disagreements);
    }

    /// <summary>
    /// The same agreement stated for the whole sensor chain rather than for radars: the fog is the
    /// union of the discs <see cref="VisionSystem.SensorOf"/> describes, on every cell of the map.
    /// <para>
    /// The world carries both answers to "does this watch" — a lit set, a set the grid has shed, a
    /// structure still being raised, and ordinary units — so the exclusions are inside the
    /// comparison rather than beside it. If the stamp loop ever marked a cell for an entity the
    /// query refuses, or stopped marking one it accepts, this is what says so.
    /// </para>
    /// </summary>
    [Fact]
    public void TheFogIsTheUnionOfTheDiscsTheSensorQueryDescribes()
    {
        SimWorld world = World();
        EntityId lit = At(world, team: 0, UnitKind.RadarStation, x: 60_000);
        EntityId dark = At(world, team: 0, UnitKind.RadarStation, x: 240_000);
        EntityId site = At(world, team: 0, UnitKind.AntiAirEmplacement, x: -200_000);

        At(world, team: 0, UnitKind.CommandCentre, x: -120_000);
        At(world, team: 0, UnitKind.Tank, x: 30_000);
        At(world, team: 0, UnitKind.Infantry, x: -40_000);
        At(world, team: 0, UnitKind.GunEmplacement, x: 120_000);
        At(world, team: 2, UnitKind.Tank, x: 0);

        // A building site at the end of the run as well as at its start: it senses nothing while it
        // is a hole in the ground, and the ticks left are set high enough that it is still one when
        // the two answers are compared.
        world.GetRefBySlot(site.Slot).ConstructionTicksRemaining = 40;

        world.RunTicks(Ticks);

        Assert.True(world.Visibility.CountVisible(0) > 0, "nothing is watching, so this proves nothing");
        Assert.True(world.IsRadarLit(lit.Slot), "the first set is not on the air");
        Assert.False(world.IsRadarLit(dark.Slot), "the second set was meant to be the one the grid sheds");
        Assert.True(world.GetRefBySlot(site.Slot).ConstructionTicksRemaining > 0, "the site finished after all");

        List<string> disagreements = [];

        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            bool query = TheTeamWatches(world, 0, cell);
            bool fog = Painted(world, 0, cell);

            if (query != fog && disagreements.Count < 5)
            {
                disagreements.Add(Disagreement(world, cell, query, fog));
            }
        }

        Assert.Empty(disagreements);
    }

    /// <summary>
    /// A target at the rim of a radar is covered by the fog and shot at by the gun, or covered by
    /// neither.
    /// <para>
    /// The geometry is the one a radar exists for: the gun stands 70 m from the set, so it is under
    /// the umbrella, and the rim lands about 190 m from the gun — outside the 170 m the gun's own
    /// eyes reach and inside the 200 m its gun reaches. The radar is therefore the only thing that
    /// can decide the shot, and the two cells either side of the rim are the measurement: the last
    /// centre inside the disc is engaged, the first one outside it is not.
    /// </para>
    /// <para>
    /// The cells are found by walking the lane and asking the rule at each cell centre, rather than
    /// written down as coordinates: the lattice is 9 374 mm wide and belongs to the navigation grid,
    /// so a test holding its own copy of where the rim falls is a third answer to the question.
    /// </para>
    /// </summary>
    [Fact]
    public void ATargetAtTheRimIsCoveredByBothOrByNeitherAndTheGunAgrees()
    {
        (int inside, int outside) = RimCells();

        AtTheRim(inside, covered: true);
        AtTheRim(outside, covered: false);
    }

    /// <summary>
    /// <b>The same position is covered by both rules or by neither, and where the two could part
    /// company is exactly where this test looks.</b>
    /// <para>
    /// A painted cell can hold ground beyond the rim — the rows are chosen by their centre, so the
    /// outer row reaches half a cell past the disc — and an unpainted cell can hold ground inside
    /// it. Those are the two kinds of ground on which a distance test and a cell test give opposite
    /// answers, so the test walks the map and asks about the ground of each kind: a position a
    /// couple of metres past the rim in a cell the fog has painted is still covered, and a position
    /// a couple of metres inside the rim in a cell the fog has not is not. It asserts that both
    /// kinds were found, so it cannot pass by measuring nothing.
    /// </para>
    /// <para>
    /// This is the test that fails if the query ever goes back to testing the distance between two
    /// positions: the two answers would then be the geometry's and the fog's, and the geometry is
    /// the one that is wrong, because the fog is a grid and a grid has no answer about a point.
    /// </para>
    /// </summary>
    [Fact]
    public void TheQueryAnswersAboutTheCellRatherThanThePointItIsAskedAbout()
    {
        SimWorld world = World();
        EntityId radar = At(world, team: 0, UnitKind.RadarStation, x: 0);

        world.RunTicks(Ticks);

        WorldPos set = world.GetRefBySlot(radar.Slot).Position;
        long radiusSquared = (long)VisionSystem.RadarCoverageMm * VisionSystem.RadarCoverageMm;
        int cell = world.Navigation.CellSizeMm;
        int origin = world.Navigation.OriginMm;
        int past = 0;
        int shortOf = 0;

        for (int index = 0; index < world.Navigation.CellCount; index++)
        {
            int x0 = origin + (world.Navigation.CellX(index) * cell);
            int z0 = origin + (world.Navigation.CellZ(index) * cell);
            var nearest = new WorldPos(x0, 0, z0);
            var farthest = new WorldPos(x0 + cell - 1, 0, z0 + cell - 1);
            bool fog = Painted(world, 0, index);

            Assert.Equal(index, world.Navigation.IndexOfWorld(nearest));
            Assert.Equal(index, world.Navigation.IndexOfWorld(farthest));

            if (fog && HorizontalSquared(farthest, set) > radiusSquared)
            {
                // Ground past the rim, in a cell the fog paints: covered, because the query is
                // asked about the cell and the cell's centre is inside the disc.
                past++;
                Assert.True(
                    world.Radars.Covers(world, 0, farthest),
                    $"painted cell ({world.Navigation.CellX(index)},{world.Navigation.CellZ(index)}) has ground " +
                    "past the rim that the query refuses");
            }

            if (!fog && HorizontalSquared(nearest, set) < radiusSquared)
            {
                // Ground inside the rim, in a cell the fog leaves alone: not covered, for the same
                // reason in the other direction.
                shortOf++;
                Assert.False(
                    world.Radars.Covers(world, 0, nearest),
                    $"unpainted cell ({world.Navigation.CellX(index)},{world.Navigation.CellZ(index)}) has ground " +
                    "inside the rim that the query calls covered");
            }
        }

        Assert.True(past > 0, "the disc paints no ground past its own rim, so nothing above was measured");
        Assert.True(shortOf > 0, "the disc refuses no ground inside its own rim, so nothing above was measured");
    }

    /// <summary>
    /// The two cells either side of the rim, on the lane, for the geometry above: the last cell
    /// centre within <see cref="VisionSystem.RadarCoverageMm"/> of the set and the first one beyond.
    /// </summary>
    private static (int Inside, int Outside) RimCells()
    {
        SimWorld world = World();
        EntityId radar = At(world, team: 0, UnitKind.RadarStation, x: -70_000);
        WorldPos set = world.GetRefBySlot(radar.Slot).Position;
        int z = world.Navigation.CellZ(world.Navigation.IndexOfWorld(set));
        long radiusSquared = (long)VisionSystem.RadarCoverageMm * VisionSystem.RadarCoverageMm;
        int inside = -1;
        int outside = -1;

        for (int x = 0; x < world.Navigation.Size; x++)
        {
            int cell = world.Navigation.IndexOf(x, z);

            if (HorizontalSquared(world.Navigation.CentreOf(cell), set) <= radiusSquared)
            {
                inside = cell;
                outside = x + 1 < world.Navigation.Size ? world.Navigation.IndexOf(x + 1, z) : -1;
            }
        }

        Assert.True(inside >= 0 && outside >= 0, "the lane does not cross the rim, so there is nothing to measure");

        return (inside, outside);
    }

    /// <summary>Squared horizontal distance between two positions, in mm².</summary>
    private static long HorizontalSquared(WorldPos a, WorldPos b)
    {
        long dx = (long)a.X - b.X;
        long dz = (long)a.Z - b.Z;

        return (dx * dx) + (dz * dz);
    }

    /// <summary>
    /// One world with the gun and the set on the lane and a tank in the cell named, ticked long
    /// enough for the gun to acquire and fire — and every answer about that tank has to be the one
    /// the fog gives: the query, the acquisition, and the shot.
    /// </summary>
    private static void AtTheRim(int tankCell, bool covered)
    {
        SimWorld world = World();
        EntityId gun = At(world, team: 0, UnitKind.GunEmplacement, x: 0);
        EntityId radar = At(world, team: 0, UnitKind.RadarStation, x: -70_000);
        WorldPos position = world.Navigation.CentreOf(tankCell);
        UnitDefinition definition = UnitCatalog.Get(UnitKind.Tank);
        EntityId tank = world.Spawn(
            Faction.Western, 2, UnitKind.Tank, position, Fix32.FromInt(definition.SpeedMmPerTick), definition.Health);

        ref Entity held = ref world.GetRefBySlot(tank.Slot);
        held.Position = position;
        held.MoveGoal = position;

        world.RunTicks(60);

        bool fogSaysCovered = Painted(world, 0, tankCell);
        bool querySaysCovered = world.Radars.Covers(world, 0, position);
        bool engaged = world.GetRefBySlot(gun.Slot).TargetSlot == tank.Slot;
        bool damaged = world.GetRefBySlot(tank.Slot).Health < definition.Health;

        // The cell is on the fog's boundary, so the four answers about it have to be one answer:
        // what the fog has, what a query says, whether the gun acquired, and whether it landed.
        Assert.Equal(covered, fogSaysCovered);
        Assert.Equal(fogSaysCovered, querySaysCovered);
        Assert.Equal(fogSaysCovered, engaged);
        Assert.Equal(engaged, damaged);
        Assert.True(world.IsRadarLit(radar.Slot), "the set went dark, so the radar never decided this");
    }

    /// <summary>
    /// A radar the grid has shed senses nothing, covers nothing, and stops painting the ground it
    /// was lighting: the query answers with the reason rather than with the role's 260 m, and the
    /// fog loses the ground at the same moment.
    /// <para>
    /// The ground the test watches is 240 m from the set, which is inside its disc and outside
    /// everything else the base owns — the factory that takes the last of the grid reaches 130 m —
    /// so what appears and disappears with the brown-out is the radar's coverage and nothing else.
    /// </para>
    /// </summary>
    [Fact]
    public void ARadarWithoutPowerSensesNothingAndTheFogLosesItsGround()
    {
        SimWorld world = World();
        EntityId radar = At(world, team: 0, UnitKind.RadarStation, x: 0);
        int ground = world.Navigation.IndexOfWorld(new WorldPos(240_000, 0, Lane));

        world.RunTicks(Ticks);

        Assert.True(world.IsRadarLit(radar.Slot), "the set is not on the air, so there is no coverage to lose");
        Assert.True(Painted(world, 0, ground), "a lit radar did not paint the ground its own coverage is for");
        Assert.True(world.Radars.Covers(world, 0, world.Navigation.CentreOf(ground)));

        // One Πυροβολείου factory's worth of load more than the grid can carry: the same arithmetic
        // the probe reads as a brown-out, and the same event the picture draws as a missing rim.
        At(world, team: 0, UnitKind.Factory, x: 60_000);
        world.RunTicks(Ticks);

        Assert.False(world.IsRadarLit(radar.Slot), "the grid had room for the set after all");
        Assert.Equal(0, VisionSystem.SensorOf(world, radar.Slot, out SensorRefusal refusal));
        Assert.Equal(SensorRefusal.RadarDark, refusal);

        Assert.False(
            world.Radars.Covers(world, 0, world.Navigation.CentreOf(ground)),
            "a dark radar covered ground");

        Assert.False(Painted(world, 0, ground), "a dark set was still painting into the fog");

        // And the whole map still agrees, with the set's disc gone from both answers at once.
        List<string> disagreements = [];

        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            bool query = TheTeamWatches(world, 0, cell);
            bool fog = Painted(world, 0, cell);

            if (query != fog && disagreements.Count < 5)
            {
                disagreements.Add(Disagreement(world, cell, query, fog));
            }
        }

        Assert.Empty(disagreements);
    }
}
