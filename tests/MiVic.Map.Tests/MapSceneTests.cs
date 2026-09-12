using System.Buffers.Binary;
using System.IO.Compression;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Map.Tests;

/// <summary>
/// What the map is: a picture of a world, drawn from the world and from nothing else.
/// <para>
/// The tests are about three properties and no more. That the same world draws the same bytes
/// twice, because that is the property the rest of the project lives on and a picture that
/// drifted would be a picture nobody could compare across runs. That the two formats are the
/// same picture, because the PNG exists only so the SVG can be looked at. And that a layer
/// draws what it says it draws — the trajectory layer most of all, since a route with no trail
/// behind it is the reading the whole tool was built for.
/// </para>
/// </summary>
public sealed class MapSceneTests
{
    /// <summary>A scene of a world with one tank on it, with the census that came with it.</summary>
    private static MapDrawing Draw(
        SimWorld world,
        MapTrails trails,
        MapLayers layers = MapLayers.All,
        double pixelsPerMetre = 1.0,
        IReadOnlyList<MapEventMark>? events = null)
        => MapSceneBuilder.Build(
            world,
            trails,
            TestWorld.Palette,
            new MapRequest { Layers = layers, PixelsPerMetre = pixelsPerMetre, Scenario = "test" },
            events ?? []);

    [Fact]
    public void TheSameWorldDrawsTheSameSvgBytes()
    {
        SimWorld first = TestWorld.NewWorld();
        SimWorld second = TestWorld.NewWorld();

        TestWorld.SpawnTank(first, Faction.Soviet, 0);
        TestWorld.SpawnTank(second, Faction.Soviet, 0);

        MapTrails firstTrails = TestWorld.Sampled(first, 8);
        MapTrails secondTrails = TestWorld.Sampled(second, 8);

        string one = SvgMapWriter.Render(Draw(first, firstTrails).Scene);
        string two = SvgMapWriter.Render(Draw(second, secondTrails).Scene);

        Assert.Equal(one, two);
        Assert.Contains("<g id=\"terrain\"", one, StringComparison.Ordinal);
    }

    [Fact]
    public void TheSameWorldDrawsTheSamePngBytes()
    {
        SimWorld first = TestWorld.NewWorld();
        SimWorld second = TestWorld.NewWorld();

        TestWorld.SpawnTank(first, Faction.Soviet, 0);
        TestWorld.SpawnTank(second, Faction.Soviet, 0);

        byte[] one = PngMapWriter.Render(Draw(first, TestWorld.Sampled(first, 8)).Scene);
        byte[] two = PngMapWriter.Render(Draw(second, TestWorld.Sampled(second, 8)).Scene);

        Assert.Equal(one, two);
    }

    [Fact]
    public void TheTwoFormatsAreTheSamePicture()
    {
        SimWorld world = TestWorld.NewWorld();
        TestWorld.SpawnTank(world, Faction.Soviet, 0);

        MapDrawing drawing = Draw(world, TestWorld.Sampled(world, 8));

        // The same scene twice through two encoders: the raster's pixel dimensions are the
        // scene's, and the SVG's declared size is too. A PNG that had been scaled, cropped or
        // produced from a second traversal of the world would show up here as a disagreement.
        RasterCanvas canvas = RasterCanvas.Paint(drawing.Scene);
        string svg = SvgMapWriter.Render(drawing.Scene);

        Assert.Equal(drawing.Scene.Width, canvas.Width);
        Assert.Equal(drawing.Scene.Height, canvas.Height);
        Assert.Contains($"width=\"{canvas.Width}\" height=\"{canvas.Height}\"", svg, StringComparison.Ordinal);
    }

    [Fact]
    public void ATerrainOnlyMapDrawsNoUnits()
    {
        SimWorld world = TestWorld.NewWorld();
        TestWorld.SpawnTank(world, Faction.Soviet, 0);

        MapDrawing drawing = Draw(world, TestWorld.Sampled(world, 8), MapLayers.Terrain);

        Assert.NotEmpty(drawing.Scene.Group("terrain"));
        Assert.Empty(drawing.Scene.Shapes.OfType<MapDisc>());
        Assert.Empty(drawing.Scene.Group("units"));
        Assert.Equal(world.Navigation.CellCount, drawing.Census.TerrainCells);

        // The census still says one entity, because there is one: a layer turned off is a thing
        // not drawn, not a thing that is not there. A transcript that reported an empty map here
        // would be a transcript that had started describing the picture instead of the match.
        Assert.Equal(1, drawing.Census.Entities);
    }

    [Fact]
    public void EveryPlaceOnTheGroundIsPaintedExactlyOnce()
    {
        SimWorld world = TestWorld.NewWorld();
        const double pixelsPerMetre = 2.0;
        MapDrawing drawing = Draw(world, TestWorld.Sampled(world, 1), MapLayers.Terrain, pixelsPerMetre);

        var cells = drawing.Scene.Group("terrain").Cast<MapRect>().ToList();
        int expected = (int)Math.Round(world.Navigation.Size * world.Navigation.CellSizeMm * pixelsPerMetre / WorldPos.MmPerMetre);

        Assert.Equal(world.Navigation.CellCount, cells.Count);

        int left = (int)cells.Min(cell => cell.X);
        int top = (int)cells.Min(cell => cell.Y);
        int right = (int)cells.Max(cell => cell.X + cell.W);
        int bottom = (int)cells.Max(cell => cell.Y + cell.H);

        Assert.Equal(14, left);
        Assert.Equal(14, top);
        Assert.Equal(14 + expected, right);
        Assert.Equal(14 + expected, bottom);

        // The cells tile the map's square exactly. Whole-pixel edges are what makes them abut,
        // and the areas adding up to the square's own area is what proves there is neither a
        // seam between two of them nor two of them drawn on top of each other — which at 4 225
        // cells is not something a reader can check by looking at the picture.
        foreach (MapRect cell in cells)
        {
            Assert.Equal(cell.W, Math.Floor(cell.W));
            Assert.Equal(cell.H, Math.Floor(cell.H));
            Assert.True(cell.W > 0 && cell.H > 0);
        }

        Assert.Equal(expected * expected, (int)cells.Sum(cell => cell.W * cell.H));
    }

    [Fact]
    public void AUnitWithAGoalAndNoRouteIsDrawnAsStalled()
    {
        SimWorld world = TestWorld.NewWorld();
        (EntityId id, int slot) = TestWorld.SpawnTank(world, Faction.Soviet, 0);

        // Ordered somewhere and never repathed: the command has run, so the goal is held, and no
        // route search has answered it. That is the stall this layer exists to make visible.
        TestWorld.OrderTo(world, id, TestWorld.FarGround(world, MovementClass.Tracked));

        Assert.True(world.GetRefBySlot(slot).HasMoveGoal);
        Assert.Equal(0, world.GetRefBySlot(slot).PathLength);

        MapDrawing drawing = Draw(world, TestWorld.Sampled(world, 8));

        Assert.Equal(1, drawing.Census.Stalled);
        Assert.Equal(0, drawing.Census.WithRoute);

        // The box round the unit and the cross at the goal it cannot reach.
        Assert.Contains(drawing.Scene.Group("stuck"), shape => shape is MapPolyline);
        Assert.Contains(drawing.Scene.Group("stuck"), shape => shape is MapDisc);
    }

    [Fact]
    public void AUnitHoldingARouteAndStandingStillIsDrawnAsFrozen()
    {
        SimWorld world = TestWorld.NewWorld();
        (EntityId id, int slot) = TestWorld.SpawnTank(world, Faction.Soviet, 0);

        TestWorld.OrderTo(world, id, TestWorld.FarGround(world, MovementClass.Tracked));

        // The route is asked for directly rather than waited for, so the test holds a world with
        // a path in hand and no ticks behind it — which is exactly the state the diagnosis is
        // about, reached without having to reproduce the bug that produces it in a real match.
        Assert.True(world.RepathFrom(slot));
        Assert.True(world.GetRefBySlot(slot).PathLength > 0);

        MapTrails trails = TestWorld.Sampled(world, 8);
        Assert.True(trails.IsFrozen(world, slot, holdingRoute: true));

        MapDrawing drawing = Draw(world, trails);

        Assert.Equal(1, drawing.Census.WithRoute);
        Assert.Equal(1, drawing.Census.Frozen);
        Assert.Equal(0, drawing.Census.Moving);
        Assert.Contains("holding a route and not moving along it", drawing.Census.TrajectoryReading, StringComparison.Ordinal);

        // The mark is the point: the census number and the magenta ring on the map are the same
        // fact, and a reader looking at the picture alone is told which unit it was.
        MapRgb warning = TestWorld.Palette.Warning;

        Assert.Contains(
            drawing.Scene.Group("stuck"),
            shape => shape is MapDisc { Hollow: true } ring && ring.Colour == warning);
    }

    [Fact]
    public void AUnitThatMarchesIsNotFrozenAndItsTrailGrows()
    {
        SimWorld world = TestWorld.NewWorld();
        (EntityId id, int slot) = TestWorld.SpawnTank(world, Faction.Soviet, 0, TestWorld.OpenGround(world, MovementClass.Tracked));

        TestWorld.OrderTo(world, id, TestWorld.FarGround(world, MovementClass.Tracked));
        Assert.True(world.RepathFrom(slot));

        var trails = new MapTrails();
        var events = new List<MapEventMark>();

        for (int tick = 0; tick < 120; tick++)
        {
            world.Step();
            trails.Advance(world);
        }

        MapDrawing drawing = Draw(world, trails, MapLayers.All, 2.0, events);

        Assert.True(trails.NetMovementMm(slot, MapTrails.DefaultFrozenSamples) >= MapTrails.FrozenThresholdMm);
        Assert.False(trails.IsFrozen(world, slot, holdingRoute: true));
        Assert.Equal(0, drawing.Census.Frozen);
        Assert.Equal(1, drawing.Census.Moving);
        Assert.True(drawing.Census.TrailSamples > 8, "a marching unit should have a trail to draw");

        // The pace reading, which is the other half of what a trail is for: this unit is on open
        // ground and getting the whole of what the ground allows it.
        Assert.Equal(slot, drawing.Census.SlowestSlot);
        Assert.InRange(drawing.Census.SlowestPermille, 1, 1_000);

        // A trail is a line with a mark at every sample, and the marks are what makes pace legible,
        // so both have to be there — and the marks have to be one shape rather than sixty-four.
        Assert.Contains(drawing.Scene.Group("trails"), shape => shape is MapPolyline);
        MapDots marks = Assert.IsType<MapDots>(Assert.Single(drawing.Scene.Group("trails").OfType<MapDots>()));
        Assert.True(marks.Points.Count >= 16, "a full window of samples should be a full trail of marks");
    }

    [Fact]
    public void AUnitThatHasOnlyJustStartedWalkingIsNotFrozen()
    {
        // The regression that a picture found. Six units on a skirmish were reported as holding a
        // route and not moving; they had been stalled, been handed a route, and walked a metre and
        // a half of the last four seconds — and would walk twelve in the next six. The window said
        // "not moving" about units that were, at that moment, moving, so the diagnosis now asks
        // whether the unit is still still, and this is that case: seven seconds of standing, then
        // one second of walking at the full speed the ground allows.
        SimWorld world = TestWorld.NewWorld();
        (EntityId id, int slot) = TestWorld.SpawnTank(world, Faction.Soviet, 0);

        ref Entity unit = ref world.GetRefBySlot(slot);
        unit.SpeedMmPerTick = Fix32.FromInt(100);

        WorldPos goal = TestWorld.FarGround(world, MovementClass.Tracked);
        TestWorld.OrderTo(world, id, goal);
        Assert.True(world.RepathFrom(slot));

        // The route is held but not pursued: what a stalled unit that has just been given one
        // looks like from the trail's side, without the stall having to be reproduced.
        unit.HasMoveGoal = false;

        var trails = new MapTrails();

        for (int tick = 0; tick < 80; tick++)
        {
            world.Step();
            trails.Advance(world);
        }

        Assert.Equal(8, trails.Filled(slot));
        Assert.True(trails.IsFrozen(world, slot, holdingRoute: true), "eight seconds of standing is a stall");

        // And then it starts walking, for one second.
        world.GetRefBySlot(slot).HasMoveGoal = true;

        for (int tick = 0; tick < 10; tick++)
        {
            world.Step();
            trails.Advance(world);
        }

        int moved = trails.NetMovementMm(slot, MapTrails.DefaultFrozenSamples);

        Assert.Equal(9, trails.Filled(slot));
        Assert.InRange(moved, 1, MapTrails.FrozenThresholdMm - 1);
        Assert.False(trails.IsFrozen(world, slot, holdingRoute: true), "a unit that has just moved is moving");
    }

    [Fact]
    public void ARadarWithNoPowerDrawsNoCoverage()
    {
        // A brown-out is the one combination that would make the picture lie: the guns go blind
        // and, if the map still drew the set's 260 metres, the radar would still look like it was
        // watching. A bare base generates six and a radar draws four, so one factory is enough to
        // take the set off the air — the same arithmetic tools/probe/map-coverage.probe reads off
        // a whole match, in one line. The fog pass skips a radar station that is not lit, and so
        // does this.
        SimWorld world = TestWorld.NewWorld();
        WorldPos site = TestWorld.OpenGround(world, MovementClass.None);

        (EntityId _, int tank) = TestWorld.SpawnTank(world, Faction.Soviet, 0, site);

        EntityId radar = world.Spawn(Faction.Soviet, 0, UnitKind.RadarStation, site, Fix32.Zero, 900);
        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, site, Fix32.Zero, 2_000);

        world.Step();

        Assert.False(
            world.IsRadarLit(TestWorld.SlotOf(world, radar)),
            "the factory has taken the last of the grid's six, and the set is off the air");

        MapDrawing drawing = Draw(world, new MapTrails(), MapLayers.Coverage, 2.0);

        // The tank's own eyes and the factory's, both 130 metres, and nothing at 260: the set is
        // dark and the map draws no rim for it, which is the whole reading. A picture that drew
        // one would be the brown-out drawn as though it had not happened.
        var radii = drawing.Scene.Group("coverage").Cast<MapDisc>().Select(rim => rim.R / 2.0).ToList();

        Assert.Equal([130.0, 130.0], radii.Select(r => Math.Round(r, 3)));
        Assert.DoesNotContain(radii, metres => Math.Abs(metres - 260.0) < 0.001);
        Assert.Equal(2, drawing.Census.CoverageDiscs);
        _ = tank;
    }

    [Fact]
    public void ACoverageRimIsTheDiscTheFogIsStampedFrom()
    {
        // Not "the radius is the sensor radius", which the test above pins, but that the ground
        // inside the circle that was drawn and the ground the fog has painted are the same ground,
        // cell for cell. A layer drawing a rim from some other number — the catalogue's sight
        // figure, or a radar's 260 m with no power behind it — would pass that and fail this, and
        // the reading it would break is the one the layer exists for: whether a ring encloses the
        // ground the guns can actually be told about.
        //
        // A radar on the grid's standby and nothing else on the map, so the only rim and the only
        // disc are the same sensor's.
        SimWorld world = TestWorld.NewWorld();
        WorldPos site = TestWorld.OpenGround(world, MovementClass.None);
        EntityId radar = world.Spawn(Faction.Soviet, 0, UnitKind.RadarStation, site, Fix32.Zero, 900);
        int slot = TestWorld.SlotOf(world, radar);

        world.RunTicks(VisionSystem.UpdateInterval * 2);

        Assert.True(world.IsRadarLit(slot), "the set is not on the air, so there is no rim to read");

        MapDrawing drawing = Draw(world, new MapTrails(), MapLayers.Coverage, 2.0);
        MapDisc rim = Assert.IsType<MapDisc>(Assert.Single(drawing.Scene.Group("coverage")));

        // The rim's own radius, as drawn and read back into the simulation's millimetres: the layer
        // projects the world, so this is the number the picture is a picture of.
        long rimMm = (long)Math.Round(rim.R * WorldPos.MmPerMetre / 2.0);

        Assert.Equal(VisionSystem.RadarCoverageMm, rimMm);

        ref Entity entity = ref world.GetRefBySlot(slot);
        long radiusSquared = rimMm * rimMm;
        int painted = 0;

        for (int cell = 0; cell < world.Navigation.CellCount; cell++)
        {
            WorldPos centre = world.Navigation.CentreOf(cell);
            long dx = (long)centre.X - entity.Position.X;
            long dz = (long)centre.Z - entity.Position.Z;
            bool insideTheRing = ((dx * dx) + (dz * dz)) <= radiusSquared;
            bool inTheFog = world.Visibility.IsVisible(0, cell);

            Assert.True(
                insideTheRing == inTheFog,
                $"cell ({world.Navigation.CellX(cell)},{world.Navigation.CellZ(cell)}) is inside the ring: " +
                $"{insideTheRing}, in the fog: {inTheFog}");

            painted += inTheFog ? 1 : 0;
        }

        Assert.True(painted > 0, "the rim was drawn around ground the fog has not painted");
    }

    [Fact]
    public void AStructureUnderConstructionDrawsNoCoverage()
    {
        SimWorld world = TestWorld.NewWorld();
        WorldPos site = TestWorld.OpenGround(world, MovementClass.None);

        (EntityId id, _) = TestWorld.SpawnTank(world, Faction.Soviet, 0);
        _ = id;

        EntityId building = world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, site, Fix32.Zero, 5_000);
        world.GetRefBySlot(TestWorld.SlotOf(world, building)).ConstructionTicksRemaining = 40;

        MapDrawing drawing = Draw(world, new MapTrails(), MapLayers.Coverage, 2.0);

        // The tank's own eyes are the only rim on the map: a building site stamps no fog, which is
        // the same rule that keeps a half-raised emplacement from firing.
        MapDisc rim = Assert.IsType<MapDisc>(Assert.Single(drawing.Scene.Group("coverage")));

        Assert.Equal(130.0, rim.R / 2.0, 0.001);
    }

    [Fact]
    public void TheCensusDescribesTheWorldAndNotTheLayersThatWereDrawn()
    {
        SimWorld world = TestWorld.NewWorld();
        (EntityId id, int slot) = TestWorld.SpawnTank(world, Faction.Soviet, 0);

        TestWorld.OrderTo(world, id, TestWorld.FarGround(world, MovementClass.Tracked));
        Assert.True(world.RepathFrom(slot));

        MapTrails trails = TestWorld.Sampled(world, 8);

        MapDrawing everything = Draw(world, trails);
        MapDrawing unitsOnly = Draw(world, trails, MapLayers.Units);

        // The numbers a reader compares between two pictures of one tick. They are counted from
        // the world rather than from the shapes, so turning a layer off cannot move them — and a
        // script that drew the routes in one file and the trails in another is comparing the same
        // match rather than two different questions about it.
        Assert.Equal(everything.Census.WithRoute, unitsOnly.Census.WithRoute);
        Assert.Equal(everything.Census.Frozen, unitsOnly.Census.Frozen);
        Assert.Equal(everything.Census.Entities, unitsOnly.Census.Entities);
        Assert.Equal(everything.Census.TrailSamples, unitsOnly.Census.TrailSamples);

        Assert.Empty(unitsOnly.Scene.Group("trails"));
        Assert.NotEmpty(everything.Scene.Group("trails"));

        // The reading names its subjects, so a count is a list of slots somebody can ask about.
        Assert.Equal([slot], everything.Census.FrozenSlots);
        Assert.Contains("frozen     slots", string.Join('\n', everything.Census.Lines()), StringComparison.Ordinal);
    }

    [Fact]
    public void ACoverageDiscIsDrawnWhereTheSensorIs()
    {
        SimWorld world = TestWorld.NewWorld();
        (_, int slot) = TestWorld.SpawnTank(world, Faction.Western, 2);
        ref Entity entity = ref world.GetRefBySlot(slot);

        MapDrawing drawing = Draw(world, TestWorld.Sampled(world, 1), MapLayers.Coverage, 2.0);

        MapDisc disc = Assert.IsType<MapDisc>(Assert.Single(drawing.Scene.Group("coverage")));

        // The disc's centre is the unit's own ground, and its radius is the sensor radius the
        // fog pass stamps — not the catalogue's sight figure, which the vision system scales by
        // research and by snow. Drawn as a rim: a filled disc saturates into a wash over an army.
        double expectedRadius = MiVic.Core.Sim.VisionSystem.SensorRadiusMm(world, entity) * 2.0 / WorldPos.MmPerMetre;
        double expectedX = 14 + ((entity.Position.X - world.Navigation.OriginMm) * 2.0 / WorldPos.MmPerMetre);

        Assert.True(disc.Hollow);
        Assert.Equal(expectedRadius, disc.R, 0.001);
        Assert.Equal(expectedX, disc.X, 0.001);
        Assert.Equal(1, drawing.Census.CoverageDiscs);
    }

    [Fact]
    public void AFightIsDrawnAsMarksRatherThanNothing()
    {
        SimWorld world = TestWorld.NewWorld();
        (EntityId id, int slot) = TestWorld.SpawnTank(world, Faction.Soviet, 0);

        ref Entity entity = ref world.GetRefBySlot(slot);
        WorldPos where = entity.Position;

        _ = id;

        var events = new List<MapEventMark>
        {
            new(MapEventKind.Shot, world.Tick, Faction.Soviet, 0, UnitKind.Tank, where, where, 0),
            new(MapEventKind.Hit, world.Tick, Faction.Soviet, 0, UnitKind.Tank, where, null, 12),
            new(MapEventKind.Death, world.Tick, Faction.Soviet, 0, UnitKind.Tank, where, null, 0),
        };

        MapTrails trails = TestWorld.Sampled(world, 1);
        MapDrawing drawing = Draw(world, trails, MapLayers.Events, 2.0, events);

        Assert.Equal(3, drawing.Census.EventMarks);
        Assert.Contains(drawing.Scene.Group("events"), shape => shape is MapLine);
        Assert.Contains(drawing.Scene.Group("events"), shape => shape is MapDisc);

        // Ten seconds later the marks are gone, and the window is why: the layer is a picture of
        // what just happened rather than a graph of the whole match, and a shot fired in the
        // first exchange must not still be on the map at the end of it.
        world.RunTicks(200);

        MapDrawing later = Draw(world, trails, MapLayers.Events, 2.0, events);

        Assert.Equal(0, later.Census.EventMarks);

        // ...unless the window is opened wide enough to hold them, which is the same picture
        // with a different question asked of it.
        MapDrawing wide = MapSceneBuilder.Build(
            world,
            trails,
            TestWorld.Palette,
            new MapRequest { Layers = MapLayers.Events, EventWindowTicks = 1_000, PixelsPerMetre = 2.0 },
            events);

        Assert.Equal(3, wide.Census.EventMarks);
    }

    [Fact]
    public void TheLayerNamesAreTheOnesAProbeCanAskFor()
    {
        MapLayers all = MapLayerText.Parse("all");

        Assert.Equal(MapLayers.All, all);
        Assert.Equal(MapLayers.None, MapLayerText.Parse("none"));
        Assert.Equal(MapLayers.Paths | MapLayers.Trails, MapLayerText.Parse("paths,trails"));
        Assert.Equal(MapLayers.Trails, MapLayerText.Parse(" TRAILS "));
        Assert.Equal("terrain, coverage", MapLayerText.Describe(MapLayers.Terrain | MapLayers.Coverage));

        // Every name in the table parses, and the description names every bit that is set: a
        // layer a script can ask for by one name and not find in the transcript is a layer
        // nobody can tell had been left off.
        foreach ((string name, MapLayers layer) in MapLayerText.Table)
        {
            Assert.Equal(layer, MapLayerText.Parse(name));
            Assert.Contains(name, MapLayerText.Describe(layer), StringComparison.Ordinal);
        }

        Assert.Throws<ArgumentException>(() => MapLayerText.Parse("trail"));
        Assert.Throws<ArgumentException>(() => MapLayerText.Parse(string.Empty));
    }
}

/// <summary>
/// The raster writer and the PNG encoder, checked by reading the file back.
/// <para>
/// A PNG that nothing decodes is thirty lines of arithmetic nobody has tested, and the way to
/// test arithmetic is to undo it. The reader below is deliberately a separate implementation of
/// the format from the writer — it inflates, it defilters, it checks a CRC — because a reader
/// that shared the writer's way of looking at the bytes would agree with it about a mistake.
/// </para>
/// </summary>
public sealed class PngWriterTests
{
    [Fact]
    public void TheBytesAreAPngThatDecodesToTheScene()
    {
        SimWorld world = TestWorld.NewWorld();
        TestWorld.SpawnTank(world, Faction.Soviet, 0);
        MapDrawing drawing = MapSceneBuilder.Build(
            world,
            TestWorld.Sampled(world, 1),
            TestWorld.Palette,
            new MapRequest { PixelsPerMetre = 1.0, Scenario = "test" },
            []);

        byte[] bytes = PngMapWriter.Render(drawing.Scene);
        Png decoded = Png.Read(bytes);

        Assert.Equal(drawing.Scene.Width, decoded.Width);
        Assert.Equal(drawing.Scene.Height, decoded.Height);
        Assert.Equal(drawing.Scene.Width * drawing.Scene.Height * 4, decoded.Pixels.Length);

        // With no layers at all the canvas is the background and nothing else, which is both the
        // cheapest statement of "the background is where it was put" and the check that a layer
        // this script did not ask for was not drawn anyway.
        MapDrawing bare = MapSceneBuilder.Build(
            world,
            TestWorld.Sampled(world, 1),
            TestWorld.Palette,
            new MapRequest { Layers = MapLayers.None, PixelsPerMetre = 1.0, Scenario = "test" },
            []);

        Png blank = Png.Read(PngMapWriter.Render(bare.Scene));

        Assert.Equal(TestWorld.Palette.Background, blank.At(1, 1));
        Assert.Equal(TestWorld.Palette.Background, blank.At(blank.Width - 2, blank.Height - 2));
    }

    [Fact]
    public void TheTerrainIsPaintedInThePaletteItWasGiven()
    {
        SimWorld world = TestWorld.NewWorld();
        MapDrawing drawing = MapSceneBuilder.Build(
            world,
            TestWorld.Sampled(world, 1),
            TestWorld.Palette,
            new MapRequest { Layers = MapLayers.Terrain, PixelsPerMetre = 1.0, Scenario = "test" },
            []);

        Png png = Png.Read(PngMapWriter.Render(drawing.Scene));

        // Every distinct surface on the map has to appear in the raster as its own colour. This
        // is the test that the terrain layer is a map of the world rather than a wash: a
        // rasteriser that dropped the fills, or a palette lookup that fell through to one
        // colour, would still produce a valid PNG of the right size.
        var painted = new HashSet<MapRgb>();
        var surfaces = new HashSet<MapRgb>();

        for (int z = 0; z < world.Navigation.Size; z++)
        {
            for (int x = 0; x < world.Navigation.Size; x++)
            {
                surfaces.Add(drawing.Scene.Group("terrain").Cast<MapRect>().ElementAt((z * world.Navigation.Size) + x).Colour);
            }
        }

        foreach (MapRect cell in drawing.Scene.Group("terrain").Cast<MapRect>())
        {
            painted.Add(png.At((int)cell.X + 1, (int)cell.Y + 1));
        }

        Assert.True(surfaces.Count > 1, "a generated map should have more than one surface on it");
        Assert.Equal(surfaces.Count, painted.Count);
        Assert.Subset(surfaces, painted);
    }

    [Fact]
    public void TheUnknownCharacterIsDrawnRatherThanSkipped()
    {
        // A word with a character missing from the middle of it is a different word, so the
        // face has to draw something for a character it does not have. Greek is the honest
        // test: half the labels in this game are in it.
        Assert.False(MapFont.TryGet('Ω', out _));
        Assert.True(MapFont.TryGet('a', out MapFont.Glyph folded));
        Assert.True(MapFont.TryGet('A', out MapFont.Glyph upper));
        Assert.Equal(upper.Rows, folded.Rows);
    }
}

/// <summary>
/// Two writers, one picture, asserted on the pixels rather than on the shapes.
/// <para>
/// The two formats have to agree about where things are, and the only way to know is to read the
/// raster at the coordinate the scene claims. These are the tests that would catch the classic
/// mistake — a rasteriser with its rows and columns the wrong way round, which every shape-level
/// test passes.
/// </para>
/// </summary>
public sealed class RasterCanvasTests
{
    [Fact]
    public void ARectangleLandsWhereTheSceneSaysItDoes()
    {
        var scene = new MapScene(
            40,
            40,
            new MapRgb(0, 0, 0),
            [new MapGroup("terrain", [new MapRect(10, 12, 6, 8, new MapRgb(255, 0, 0))])],
            "test");

        RasterCanvas canvas = RasterCanvas.Paint(scene);

        Assert.Equal(new MapRgb(255, 0, 0), Pixel(canvas, 10, 12));
        Assert.Equal(new MapRgb(255, 0, 0), Pixel(canvas, 15, 19));
        Assert.Equal(new MapRgb(0, 0, 0), Pixel(canvas, 9, 12));
        Assert.Equal(new MapRgb(0, 0, 0), Pixel(canvas, 10, 20));
        Assert.Equal(new MapRgb(0, 0, 0), Pixel(canvas, 16, 12));
    }

    [Fact]
    public void ADiscIsRoundAndARingIsHollow()
    {
        var scene = new MapScene(
            40,
            40,
            new MapRgb(0, 0, 0),
            [
                new MapGroup("coverage", [new MapDisc(20, 20, 10, new MapRgb(0, 255, 0), 255)]),
                new MapGroup("stuck", [new MapDisc(20, 20, 10, new MapRgb(255, 255, 0), 255, Hollow: true, Thickness: 2)]),
            ],
            "test");

        RasterCanvas canvas = RasterCanvas.Paint(scene);

        // The middle of the disc, then the ring painted over its rim: a hollow disc that did
        // not hollow would have been drawn over the whole thing.
        Assert.Equal(new MapRgb(0, 255, 0), Pixel(canvas, 20, 20));
        Assert.Equal(new MapRgb(255, 255, 0), Pixel(canvas, 30, 20));

        // Round, not square: the diagonal of the bounding box is outside the circle.
        Assert.Equal(new MapRgb(0, 0, 0), Pixel(canvas, 28, 28));
    }

    [Fact]
    public void OpacityBlendsTowardsTheColourItIsPaintedWith()
    {
        var scene = new MapScene(
            10,
            10,
            new MapRgb(0, 0, 0),
            [new MapGroup("units", [new MapDisc(5, 5, 4, new MapRgb(255, 255, 255), 128)])],
            "test");

        RasterCanvas canvas = RasterCanvas.Paint(scene);
        MapRgb centre = Pixel(canvas, 5, 5);

        Assert.InRange(centre.R, 126, 130);
        Assert.Equal(centre.R, centre.G);
        Assert.Equal(centre.R, centre.B);
        Assert.Equal(new MapRgb(0, 0, 0), Pixel(canvas, 0, 0));
    }

    [Fact]
    public void TextIsDrawnAsGlyphsAndNotAsNothing()
    {
        var scene = new MapScene(
            80,
            20,
            new MapRgb(0, 0, 0),
            [new MapGroup("frame", [new MapText(2, 14, 11, "TICK 600", new MapRgb(255, 255, 255))])],
            "test");

        RasterCanvas canvas = RasterCanvas.Paint(scene);

        int lit = 0;

        for (int y = 0; y < 20; y++)
        {
            for (int x = 0; x < 80; x++)
            {
                if (Pixel(canvas, x, y) != new MapRgb(0, 0, 0))
                {
                    lit++;
                }
            }
        }

        // Eight characters of a 5x7 face at scale 1 cannot light fewer than thirty pixels or more
        // than three hundred: the bounds are loose on purpose, because the point is that a label
        // renders at all rather than that a particular glyph is a particular shape.
        Assert.InRange(lit, 30, 300);
    }

    private static MapRgb Pixel(RasterCanvas canvas, int x, int y)
    {
        ReadOnlySpan<byte> pixels = canvas.Pixels;
        int index = ((y * canvas.Width) + x) * 4;

        return new MapRgb(pixels[index], pixels[index + 1], pixels[index + 2]);
    }
}

/// <summary>A PNG read back: the format's own decoder, written independently of the encoder.</summary>
internal sealed record Png(int Width, int Height, byte[] Pixels)
{
    /// <summary>The colour of one pixel, from the top left.</summary>
    public MapRgb At(int x, int y)
    {
        int index = ((y * Width) + x) * 4;

        return new MapRgb(Pixels[index], Pixels[index + 1], Pixels[index + 2]);
    }

    /// <summary>Reads a PNG this project wrote, refusing anything that does not add up.</summary>
    public static Png Read(byte[] bytes)
    {
        ReadOnlySpan<byte> signature = [0x89, 0x50, 0x4E, 0x47, 0x0D, 0x0A, 0x1A, 0x0A];

        Assert.True(bytes.AsSpan(0, 8).SequenceEqual(signature), "not a PNG by its signature");

        int offset = 8;
        int width = 0;
        int height = 0;
        var raw = new MemoryStream();

        while (offset < bytes.Length)
        {
            int length = BinaryPrimitives.ReadInt32BigEndian(bytes.AsSpan(offset, 4));
            string type = System.Text.Encoding.ASCII.GetString(bytes, offset + 4, 4);

            ReadOnlySpan<byte> data = bytes.AsSpan(offset + 8, length);
            uint stored = BinaryPrimitives.ReadUInt32BigEndian(bytes.AsSpan(offset + 8 + length, 4));

            Assert.Equal(Crc(bytes.AsSpan(offset + 4, 4 + length)), stored);

            switch (type)
            {
                case "IHDR":
                    width = BinaryPrimitives.ReadInt32BigEndian(data[..4]);
                    height = BinaryPrimitives.ReadInt32BigEndian(data[4..8]);
                    Assert.Equal(8, data[8]);
                    Assert.Equal(6, data[9]);
                    Assert.Equal(0, data[12]);
                    break;

                case "IDAT":
                    raw.Write(data);
                    break;

                case "IEND":
                    offset = bytes.Length;
                    continue;
            }

            offset += 12 + length;
        }

        raw.Position = 0;

        using var zlib = new ZLibStream(raw, CompressionMode.Decompress);
        var inflated = new MemoryStream();
        zlib.CopyTo(inflated);

        byte[] scanlines = inflated.ToArray();
        int stride = width * 4;
        var pixels = new byte[stride * height];

        Assert.Equal((stride + 1) * height, scanlines.Length);

        for (int row = 0; row < height; row++)
        {
            // Filter zero is the writer's whole filter strategy, and a reader that accepted any
            // filter would not notice the day that stopped being true.
            Assert.Equal(0, scanlines[row * (stride + 1)]);
            scanlines.AsSpan((row * (stride + 1)) + 1, stride).CopyTo(pixels.AsSpan(row * stride, stride));
        }

        return new Png(width, height, pixels);
    }

    private static uint Crc(ReadOnlySpan<byte> data)
    {
        uint crc = 0xFFFF_FFFF;

        foreach (byte value in data)
        {
            crc ^= value;

            for (int bit = 0; bit < 8; bit++)
            {
                crc = (crc & 1) != 0 ? 0xEDB8_8320 ^ (crc >> 1) : crc >> 1;
            }
        }

        return crc ^ 0xFFFF_FFFF;
    }
}
