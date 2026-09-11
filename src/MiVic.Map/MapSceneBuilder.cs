using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Map;

/// <summary>Everything a caller can vary about a map, with the defaults a script gets.</summary>
public sealed record MapRequest
{
    /// <summary>Which layers to draw.</summary>
    public MapLayers Layers { get; init; } = MapLayers.All;

    /// <summary>
    /// Pixels per metre. Two fits a whole 600 m battlefield in about 1 200 pixels, which is the
    /// picture to read a match in; six or more is the picture to read a fight in.
    /// </summary>
    public double PixelsPerMetre { get; init; } = 2.0;

    /// <summary>Border between the canvas edge and the map, in pixels.</summary>
    public int Margin { get; init; } = 14;

    /// <summary>Width of the legend panel to the right of the map, in pixels.</summary>
    public int Panel { get; init; } = 340;

    /// <summary>Height of the caption band under the map, in pixels.</summary>
    public int Footer { get; init; } = 26;

    /// <summary>
    /// One team's coverage only, or null for every team's. The sensor-chain question is almost
    /// always asked of one side — "does this radar reach these guns" — and three teams' discs at
    /// once is a picture of overlap rather than of coverage.
    /// </summary>
    public int? CoverageTeam { get; init; }

    /// <summary>How far back event marks reach, in ticks. Five seconds by default.</summary>
    public long EventWindowTicks { get; init; } = 100;

    /// <summary>What kind of match this is, for the caption and the census.</summary>
    public string Scenario { get; init; } = "skirmish";
}

/// <summary>
/// Draws a picture of a match: simulation state in, a scene of shapes out.
/// <para>
/// This is the whole of the tool's knowledge of the world. It reads — positions, headings in
/// brads, the route the pathfinder returned, the surface grid, the attribute-free half of the
/// terrain, the mission's objectives and triggers — and it writes nothing back. That is not a
/// stylistic choice: a diagnostic that can change the thing it is measuring is not a
/// diagnostic, and the state hash is the proof that it did not.
/// </para>
/// <para>
/// Everything is projected from millimetres to pixels exactly once, here, and rounded to whole
/// pixels for anything axis-aligned. Two writers then paint the same integers, so the SVG and
/// the PNG cannot disagree about where a unit is.
/// </para>
/// </summary>
public static class MapSceneBuilder
{
    /// <summary>Half-width of a unit's mark in pixels, before the outline.</summary>
    private const double UnitRadius = 3.4;

    /// <summary>Half-width of a structure's square in pixels, before the outline.</summary>
    private const double StructureRadius = 5.0;

    /// <summary>Length of a heading arrow in pixels.</summary>
    private const double HeadingLength = 8.5;

    /// <summary>Radius of the ring a fighting unit carries.</summary>
    private const double FightingRing = 6.5;

    /// <summary>Radius of the ring a frozen unit carries.</summary>
    private const double FrozenRing = 8.8;

    /// <summary>Half-width of the square a stalled unit carries.</summary>
    private const double StalledBox = 8.8;

    /// <summary>Radius of a sampled position's tick.</summary>
    private const double TrailTickRadius = 1.5;

    /// <summary>How far a route line or a trail is drawn in from the map's edges, in pixels.</summary>
    private const int Inset = 1;

    /// <summary>
    /// Builds the scene for a world as it stands now.
    /// </summary>
    /// <param name="world">The world to draw. Read only.</param>
    /// <param name="trails">Where its units have been, sampled by the caller as it stepped them.</param>
    /// <param name="palette">The client's own colours, plus this tool's marks.</param>
    /// <param name="request">Layers, scale and windows.</param>
    /// <param name="events">
    /// Recent events to mark. Empty is normal: a match with no shooting has nothing to draw, and a
    /// fixture that never fires should not need a second code path.
    /// </param>
    public static MapDrawing Build(
        SimWorld world,
        MapTrails trails,
        MapPalette palette,
        MapRequest request,
        IReadOnlyList<MapEventMark> events)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(trails);
        ArgumentNullException.ThrowIfNull(palette);
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(events);

        if (request.PixelsPerMetre <= 0 || request.PixelsPerMetre > 64)
        {
            throw new ArgumentOutOfRangeException(
                nameof(request),
                request.PixelsPerMetre,
                "Pixels per metre must be in (0, 64]: past that a 600 m map is not a picture.");
        }

        var projection = new Projection(world, request);
        var groups = new List<MapGroup>();
        var drawn = new Drawn();

        // The census is counted from the world before a single shape is built, and deliberately
        // not from what the shapes turned out to be. A transcript whose numbers changed because
        // a script drew the trails but not the routes would be a transcript that could not be
        // compared with the one from the run before it, and comparing two runs is the whole
        // reason a picture of a moving match is worth drawing.
        MapCensus census = Count(world, trails, request, projection);

        if (request.Layers.HasFlag(MapLayers.Terrain))
        {
            groups.Add(new MapGroup("terrain", Terrain(world, palette, projection, ref drawn)));
        }

        if (request.Layers.HasFlag(MapLayers.Coverage))
        {
            groups.Add(new MapGroup("coverage", Coverage(world, palette, projection, request, ref drawn)));
        }

        if (request.Layers.HasFlag(MapLayers.Script))
        {
            groups.Add(new MapGroup("script", Script(world, palette, projection, ref drawn)));
        }

        if (request.Layers.HasFlag(MapLayers.Trails))
        {
            groups.Add(new MapGroup("trails", Trails(world, trails, palette, projection)));
        }

        if (request.Layers.HasFlag(MapLayers.Paths))
        {
            groups.Add(new MapGroup("paths", Paths(world, palette, projection)));
        }

        if (request.Layers.HasFlag(MapLayers.Units))
        {
            groups.Add(new MapGroup("units", Units(world, palette, projection)));
        }

        if (request.Layers.HasFlag(MapLayers.Stuck))
        {
            groups.Add(new MapGroup("stuck", Stuck(world, trails, palette, projection)));
        }

        if (request.Layers.HasFlag(MapLayers.Events))
        {
            groups.Add(new MapGroup("events", Events(world, palette, projection, request, events, ref drawn)));
        }

        census = census with
        {
            TerrainCells = drawn.TerrainCells,
            CoverageDiscs = drawn.CoverageDiscs,
            ObjectiveAreas = drawn.ObjectiveAreas,
            EventMarks = drawn.EventMarks,
        };

        if (request.Layers.HasFlag(MapLayers.Frame))
        {
            groups.Add(new MapGroup("frame", Frame(world, palette, projection, request, census)));
        }

        var scene = new MapScene(
            projection.Width,
            projection.Height,
            palette.Background,
            groups,
            $"MiVic map — tick {world.Tick}, seed {world.Seed}, {request.Scenario}");

        return new MapDrawing(scene, census);
    }

    // ---------------------------------------------------------------- the census

    /// <summary>
    /// What is on the map, counted from the world rather than from the drawing.
    /// <para>
    /// This walk is the tool's reading of a match and it happens whatever layers were asked for,
    /// because the numbers have to be comparable between two pictures of the same tick — one with
    /// the routes on and one with the trails — and a number that only existed when its layer was
    /// drawn would make that comparison a comparison of two different questions.
    /// </para>
    /// </summary>
    private static MapCensus Count(
        SimWorld world,
        MapTrails trails,
        MapRequest request,
        Projection projection)
    {
        int entities = 0;
        int structures = 0;
        int withRoute = 0;
        int withTrail = 0;
        int moving = 0;
        int frozen = 0;
        int unknown = 0;
        int stalled = 0;
        int idle = 0;
        int fighting = 0;
        int trailSamples = 0;
        int triggersFired = 0;
        int slowestSlot = -1;
        int slowestPermille = int.MaxValue;
        var frozenSlots = new List<int>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            entities++;

            // A building is not a mover and has no place in a ledger about movement: counting one
            // as idle would put a headquarters in the same column as a platoon with nothing to do.
            if (UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                structures++;
                continue;
            }

            bool holdingRoute = entity.PathLength > 0;

            if (holdingRoute)
            {
                withRoute++;
            }

            int filled = trails.Filled(slot);

            if (filled > 0)
            {
                withTrail++;
                trailSamples += filled;
            }

            if (entity.TargetSlot >= 0)
            {
                fighting++;
            }

            if (entity.HasMoveGoal && !holdingRoute)
            {
                stalled++;
            }
            else if (!entity.HasMoveGoal && entity.TargetSlot < 0)
            {
                idle++;
            }

            if (!holdingRoute)
            {
                continue;
            }

            int moved = trails.NetMovementMm(slot, MapTrails.DefaultFrozenSamples);

            if (moved < 0)
            {
                unknown++;
                continue;
            }

            if (trails.IsFrozen(world, slot, holdingRoute: true))
            {
                frozen++;

                if (frozenSlots.Count < MapCensus.NamedFrozenSlots)
                {
                    frozenSlots.Add(slot);
                }

                continue;
            }

            moving++;

            // The other half of what the trajectory layer is for: the trails make pace legible,
            // and this is the number behind the widest gap between two marks. A unit in mud is a
            // unit using a fraction of what clear ground would have given it, and the smallest
            // fraction on the map is worth naming.
            int allowed = trails.AllowedMm(world, slot);

            if (allowed > 0)
            {
                int permille = (int)Math.Min(1_000, (long)moved * 1_000 / allowed);

                if (permille < slowestPermille)
                {
                    slowestPermille = permille;
                    slowestSlot = slot;
                }
            }
        }

        for (int i = 0; i < world.TriggerStates.Length; i++)
        {
            if (world.TriggerStates[i].HasFired)
            {
                triggersFired++;
            }
        }

        return new MapCensus(
            world.Tick,
            world.Seed,
            request.Scenario,
            0,
            entities,
            structures,
            withRoute,
            withTrail,
            moving,
            frozen,
            unknown,
            stalled,
            idle,
            fighting,
            trailSamples,
            0,
            0,
            triggersFired,
            0,
            request.EventWindowTicks,
            frozenSlots,
            slowestSlot,
            slowestSlot < 0 ? 0 : slowestPermille,
            projection.Width,
            projection.Height);
    }

    // ---------------------------------------------------------------- terrain

    /// <summary>
    /// The surface grid in its own colours.
    /// <para>
    /// Cell edges are projected and rounded to whole pixels and the width is taken as the
    /// difference, so neighbouring cells share an edge exactly and the ground has no seams. Open
    /// land is shaded by its height and water and lava are not, because a lake is flat: shading a
    /// lake by the height of the ground under it would draw a sea with hills in it.
    /// </para>
    /// </summary>
    private static List<MapShape> Terrain(SimWorld world, MapPalette palette, Projection projection, ref Drawn drawn)
    {
        TerrainLayer surfaces = world.TerrainTypes;
        NavGrid grid = world.Navigation;
        var shapes = new List<MapShape>(grid.CellCount);

        int water = surfaces.WaterLevelMm;
        int highest = Math.Max(water + 1, world.Terrain.MaxHeightMm);

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            TerrainType type = surfaces.TypeAt(cell);
            MapRgb colour = palette.Surface(type);

            if (type is not (TerrainType.ShallowWater or TerrainType.DeepWater or TerrainType.Lava))
            {
                int rise = Math.Clamp(grid.HeightAt(cell) - water, 0, highest - water);
                colour = colour.Shade(770 + ((rise * 450) / (highest - water)));
            }

            int x0 = projection.PixelX(projection.CellMinXMm(cell));
            int z0 = projection.PixelZ(projection.CellMinZMm(cell));
            int x1 = projection.PixelX(projection.CellMaxXMm(cell));
            int z1 = projection.PixelZ(projection.CellMaxZMm(cell));

            shapes.Add(new MapRect(x0, z0, Math.Max(1, x1 - x0), Math.Max(1, z1 - z0), colour));
            drawn.TerrainCells++;
        }

        return shapes;
    }

    // ---------------------------------------------------------------- coverage

    /// <summary>
    /// What can see what: the rim of every sensor's disc, in the colour of the power that owns it.
    /// <para>
    /// The radius comes from <see cref="VisionSystem.SensorRadiusMm"/> — the same function the fog
    /// pass stamps and the weapon comparison reads — rather than from the catalogue, because a
    /// second copy of "how far can this see" is a second answer waiting to disagree with the first.
    /// A radar station's own figure <em>is</em> the radar coverage, so a lit set draws a 260-metre
    /// rim and a heavier one, which is what makes "a radar whose coverage did not reach the guns it
    /// was bought for" a thing you can see rather than a thing you work out.
    /// </para>
    /// <para>
    /// <b>A rim rather than a filled disc, and the fill was tried first.</b> Translucent discs are
    /// the right drawing of three sensors on a battlefield and the wrong drawing of five hundred:
    /// an army's worth of 130-metre discs saturates to a flat wash at any opacity faint enough to
    /// read through, and a wash says "somebody can see here" across the whole map, which is the one
    /// thing that was already obvious. The question the layer is asked is whether a ring encloses
    /// the guns it was bought for, and the ring is the reach.
    /// </para>
    /// </summary>
    private static List<MapShape> Coverage(
        SimWorld world,
        MapPalette palette,
        Projection projection,
        MapRequest request,
        ref Drawn drawn)
    {
        var shapes = new List<MapShape>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (request.CoverageTeam is { } wanted && entity.TeamId != wanted)
            {
                continue;
            }

            // The two entities the fog pass does not stamp, and the layer draws no rim for the
            // same two reasons it does not. A building site is not watching anything yet — the
            // dish goes on the roof when the roof goes on — and a radar with no power is not
            // watching anything either, which is the whole of what a brown-out means. Drawing
            // either would put a rim on the map where the world has no coverage, and the reading
            // this layer exists for is "does the radar reach the guns": a dark radar that still
            // drew its 260 metres would answer yes to a question the simulation answers no.
            if (!world.IsComplete(slot))
            {
                continue;
            }

            bool radar = entity.Kind == UnitKind.RadarStation;

            if (radar && !world.IsRadarLit(slot))
            {
                continue;
            }

            MapRgb colour = palette.Unit(entity.Faction, entity.Kind);
            int radiusMm = VisionSystem.SensorRadiusMm(world, entity);

            if (radiusMm <= 0)
            {
                continue;
            }

            shapes.Add(new MapDisc(
                projection.X(entity.Position.X),
                projection.Z(entity.Position.Z),
                projection.Pixels(radiusMm),
                colour,
                radar ? (byte)235 : palette.CoverageAlpha,
                Hollow: true,
                Thickness: radar ? 2.0 : 1.0));

            drawn.CoverageDiscs++;
        }

        return shapes;
    }

    // ---------------------------------------------------------------- script

    /// <summary>
    /// The mission's own geometry: an objective's circle, coloured by where the objective stands,
    /// with its index, kind and status written across it. An objective that asks about a place is
    /// a shape on the map; one that asks about a number is not, and is left to the legend.
    /// </summary>
    private static List<MapShape> Script(SimWorld world, MapPalette palette, Projection projection, ref Drawn drawn)
    {
        var shapes = new List<MapShape>();

        if (world.Mission is not { } mission)
        {
            return shapes;
        }

        ReadOnlySpan<ObjectiveState> states = world.Objectives;

        for (int i = 0; i < mission.Objectives.Count; i++)
        {
            ObjectiveDefinition definition = mission.Objectives[i];
            ObjectiveStatus status = i < states.Length ? states[i].Status : ObjectiveStatus.Pending;

            if (definition.RadiusMm <= 0)
            {
                continue;
            }

            MapRgb colour = status switch
            {
                ObjectiveStatus.Complete => palette.ObjectiveDone,
                ObjectiveStatus.Failed => palette.ObjectiveFailed,
                _ => palette.ObjectiveOpen,
            };

            double x = projection.X(definition.CentreX);
            double y = projection.Z(definition.CentreZ);
            double radius = projection.Pixels(definition.RadiusMm);

            shapes.Add(new MapDisc(x, y, radius, colour, palette.AreaAlpha));
            shapes.Add(new MapDisc(x, y, radius, colour, palette.AreaAlpha, Hollow: true, Thickness: 2));

            string label = $"#{i} {definition.Kind.ToString().ToUpperInvariant()} {status.ToString().ToUpperInvariant()}";
            shapes.Add(new MapText(x - (radius * 0.5), y, 11, label, palette.Text));

            drawn.ObjectiveAreas++;
        }

        shapes.AddRange(TriggerMarks(world, palette, projection));

        return shapes;
    }

    /// <summary>
    /// A mark at every place a trigger has acted on: the units it spawned, the ground it revealed.
    /// <para>
    /// A trigger is a fact about the script, and most of the script has no position at all — but
    /// the ones that do are the ones that surprise a player, and a spawn with no mark on the map is
    /// an ambush that looks like a rendering fault. The mark is a ring at the fired trigger's radius
    /// where the definition carries one, and a small tick at the point where it does not.
    /// </para>
    /// </summary>
    private static List<MapShape> TriggerMarks(SimWorld world, MapPalette palette, Projection projection)
    {
        var shapes = new List<MapShape>();

        if (world.Mission is not { } mission)
        {
            return shapes;
        }

        for (int i = 0; i < mission.Triggers.Count && i < world.TriggerStates.Length; i++)
        {
            TriggerState state = world.TriggerStates[i];

            if (!state.HasFired)
            {
                continue;
            }

            TriggerDefinition definition = mission.Triggers[i];

            int centreX = 0;
            int centreZ = 0;
            int radiusMm = 0;

            foreach (TriggerAction action in definition.Actions)
            {
                if (action.RadiusMm > 0 || action.CentreX != 0 || action.CentreZ != 0)
                {
                    centreX = action.CentreX;
                    centreZ = action.CentreZ;
                    radiusMm = Math.Max(radiusMm, action.RadiusMm);
                }
            }

            double x = projection.X(centreX);
            double y = projection.Z(centreZ);

            if (radiusMm > 0)
            {
                shapes.Add(new MapDisc(x, y, projection.Pixels(radiusMm), palette.Marker, 190, Hollow: true, Thickness: 1));
            }
            else
            {
                shapes.Add(new MapDisc(x, y, 2.5, palette.Marker, 190));
            }

            shapes.Add(new MapText(x + 4, y - 4, 10, $"T{i}", palette.Marker));
        }

        return shapes;
    }

    // ---------------------------------------------------------------- trajectories

    /// <summary>
    /// Where each unit has been: the sampled positions joined, with a tick at every sample.
    /// <para>
    /// <b>The ticks are the layer, not the line.</b> They are one sample apart in time, so the
    /// distance between two of them is the ground covered in half a second — a column crossing mud
    /// shows as the ticks spreading out, which is the <em>rasputitsa</em> read off the picture
    /// without a cost figure being looked up anywhere. The line is there so the ticks are a path
    /// rather than a scatter.
    /// </para>
    /// <para>
    /// The unit's own position closes the trail, so the trail always ends where the unit is; the
    /// last sample is up to half a second old and a trail that stopped short of its own unit would
    /// read as a unit that had left its trail behind.
    /// </para>
    /// </summary>
    private static List<MapShape> Trails(
        SimWorld world,
        MapTrails trails,
        MapPalette palette,
        Projection projection)
    {
        var shapes = new List<MapShape>();
        WorldPos[] samples = new WorldPos[trails.SampleCount];
        var points = new List<double>((trails.SampleCount + 1) * 2);
        var ticks = new List<double>(trails.SampleCount * 2);

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (UnitCatalog.Get(entity.Kind).IsBuilding || trails.Filled(slot) == 0)
            {
                continue;
            }

            MapRgb colour = palette.Unit(entity.Faction, entity.Kind);
            int filled = trails.CopySamples(slot, samples);

            if (filled == 0)
            {
                continue;
            }

            points.Clear();
            ticks.Clear();

            for (int i = 0; i < filled; i++)
            {
                double x = projection.X(samples[i].X);
                double y = projection.Z(samples[i].Z);

                points.Add(x);
                points.Add(y);
                ticks.Add(x);
                ticks.Add(y);
            }

            // The covered ground joins the unit to where it stands now. The last tick is left
            // where it was sampled, because a mark under the unit's own dot is a mark nobody can
            // see, and the gap between the last sample and the unit is the half second of walking
            // that has happened since.
            points.Add(projection.X(entity.Position.X));
            points.Add(projection.Z(entity.Position.Z));

            shapes.Add(new MapPolyline([.. points], 1.4, colour, palette.TrailAlpha));
            shapes.Add(new MapDots([.. ticks], TrailTickRadius, colour, palette.TrailTickAlpha));
        }

        return shapes;
    }

    /// <summary>
    /// Where each unit is trying to go: the polyline the pathfinder actually returned.
    /// <para>
    /// Read from <see cref="SimWorld.PathOf"/> from the cursor onwards, which is the route the
    /// mover has left to walk rather than the one it was given — a route that has been walked is
    /// not where the unit is going. The goal itself is not drawn here: a unit whose route reaches
    /// its goal ends at the goal cell, and a unit whose route stops short of the goal is a unit
    /// that is stalled, which the <c>stuck</c> layer draws on purpose.
    /// </para>
    /// </summary>
    private static List<MapShape> Paths(SimWorld world, MapPalette palette, Projection projection)
    {
        var shapes = new List<MapShape>();
        var points = new List<double>(SimConstants.MaxPathCells * 2);

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.PathLength <= 0)
            {
                continue;
            }

            MapRgb colour = palette.Unit(entity.Faction, entity.Kind);
            ReadOnlySpan<int> route = world.PathOf(slot);

            points.Clear();
            points.Add(projection.X(entity.Position.X));
            points.Add(projection.Z(entity.Position.Z));

            double lastX = points[0];
            double lastY = points[1];

            for (int i = Math.Max(0, entity.PathCursor); i < route.Length; i++)
            {
                WorldPos waypoint = world.Navigation.CentreOf(route[i]);
                double x = projection.X(waypoint.X);
                double y = projection.Z(waypoint.Z);

                // Cell centres are the lattice's own resolution, so consecutive waypoints can
                // project onto the same pixel. Joining them would be a zero-length segment and a
                // hundred of them is a file twice the size with nothing in it.
                if (Math.Abs(x - lastX) < 0.05 && Math.Abs(y - lastY) < 0.05)
                {
                    continue;
                }

                points.Add(x);
                points.Add(y);
                lastX = x;
                lastY = y;
            }

            if (points.Count < 4)
            {
                // A route whose every waypoint is inside the unit's own pixel still has to be
                // drawn as something, or "this unit has a route" and "this unit has none" look
                // the same on the map — which is the one distinction this layer exists to make.
                shapes.Add(new MapDisc(points[0], points[1], 2.0, colour, palette.PathAlpha, Hollow: true, Thickness: 1));
            }
            else
            {
                shapes.Add(new MapPolyline([.. points], 1.8, colour, palette.PathAlpha));
            }
        }

        return shapes;
    }

    // ---------------------------------------------------------------- units

    /// <summary>
    /// Units as dots with a heading arrow, structures as squares, in the owning power's colour.
    /// A dark outline sits under every mark, because the terrain palette contains both snow and
    /// deep water and a mark that vanishes on one of them is worse than no mark at all.
    /// </summary>
    private static List<MapShape> Units(SimWorld world, MapPalette palette, Projection projection)
    {
        var shapes = new List<MapShape>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            UnitDefinition definition = UnitCatalog.Get(entity.Kind);
            MapRgb colour = palette.Unit(entity.Faction, entity.Kind);
            double x = projection.X(entity.Position.X);
            double y = projection.Z(entity.Position.Z);

            if (definition.IsBuilding)
            {
                double half = StructureRadius;
                shapes.Add(new MapRect(x - half - 1, y - half - 1, (half * 2) + 2, (half * 2) + 2, palette.Marker));
                shapes.Add(new MapRect(x - half, y - half, half * 2, half * 2, colour));
            }
            else
            {
                shapes.Add(new MapDisc(x, y, UnitRadius + 1, palette.Marker));
                shapes.Add(new MapDisc(x, y, UnitRadius, colour));

                if (definition.Movement != MovementClass.None && definition.SpeedMmPerTick > 0)
                {
                    shapes.AddRange(HeadingArrow(entity.Heading, colour, palette, projection, x, y));
                }
            }
        }

        return shapes;
    }

    /// <summary>
    /// The arrow that says which way a hull is facing, from the simulation's own brads.
    /// <para>
    /// Brads are a full turn in 65 536, and the simulation sets them with <c>Atan2Brads(z, x)</c> —
    /// so zero points along +X and a quarter turn along +Z, which is exactly the map's own axes.
    /// The renderer negates the angle because it turns a model about a left-handed Y; a picture
    /// drawn from above has no such axis to be wrong about, and the arrow is the blunter evidence
    /// that the heading in the world and the heading on the page are the same number.
    /// </para>
    /// </summary>
    private static IEnumerable<MapShape> HeadingArrow(
        ushort heading,
        MapRgb colour,
        MapPalette palette,
        Projection projection,
        double x,
        double y)
    {
        double theta = heading * (Math.Tau / 65536.0);
        double length = HeadingLength * Math.Max(1, projection.PixelsPerMetre / 2.0);
        double tipX = x + (Math.Cos(theta) * length);
        double tipY = y + (Math.Sin(theta) * length);

        yield return new MapLine(x, y, tipX, tipY, 2.0, palette.Marker, 190);
        yield return new MapLine(x, y, tipX, tipY, 1.2, colour.Towards(palette.Text, 450), 235);

        // A short barb either side, so the arrow reads as a direction rather than as a spoke.
        double wing = length * 0.34;
        double back = theta + Math.PI;

        foreach (double spread in new[] { 0.5, -0.5 })
        {
            yield return new MapLine(
                tipX,
                tipY,
                tipX + (Math.Cos(back + spread) * wing),
                tipY + (Math.Sin(back + spread) * wing),
                1.2,
                colour.Towards(palette.Text, 450),
                235);
        }
    }

    // ---------------------------------------------------------------- stuck

    /// <summary>
    /// Who is stuck, who is idle and who is fighting, drawn as the state ring on each mark.
    /// <para>
    /// Four states, four shapes, and the reason each is drawn differently is that they call for
    /// four different investigations. <b>Frozen</b> — a unit holding a route that has not moved
    /// along it — is the one the trajectory layer exists for: a drawn path with no trail behind
    /// it, marked so that finding two hundred of them in one frame is looking rather than
    /// searching. <b>Stalled</b> is the other door to the same symptom: a unit with somewhere to
    /// go and no route at all, so its goal is drawn instead of a route that does not exist.
    /// <b>Idle</b> is a unit with nothing to do, which is a different bug. <b>Fighting</b> is
    /// drawn so that "not moving" and "busy" cannot be mistaken for each other.
    /// </para>
    /// </summary>
    private static List<MapShape> Stuck(
        SimWorld world,
        MapTrails trails,
        MapPalette palette,
        Projection projection)
    {
        var shapes = new List<MapShape>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            if (definition.IsBuilding)
            {
                continue;
            }

            double x = projection.X(entity.Position.X);
            double y = projection.Z(entity.Position.Z);
            bool holdingRoute = entity.PathLength > 0;

            if (entity.TargetSlot >= 0)
            {
                shapes.Add(new MapDisc(x, y, FightingRing, palette.Text, 200, Hollow: true, Thickness: 1));
            }

            if (entity.HasMoveGoal && !holdingRoute)
            {
                shapes.Add(StallBox(x, y, palette.Stall));
                shapes.AddRange(GoalCross(entity.MoveGoal, palette.Stall, projection));
                continue;
            }

            if (!entity.HasMoveGoal && entity.TargetSlot < 0)
            {
                // A hole rather than a ring: the mark is still a dot of the owner's colour — this
                // unit exists and is theirs — and it is empty, which is what having nothing to do
                // looks like.
                shapes.Add(new MapDisc(x, y, UnitRadius * 0.42, palette.Marker, 255));
            }

            // The mark the whole layer is for. A unit that holds a route and has not moved along
            // it is drawn a second time, brighter and larger than anything else on the map, so
            // that two hundred of them in one frame is looking rather than searching.
            if (trails.IsFrozen(world, slot, holdingRoute))
            {
                shapes.Add(new MapDisc(x, y, FrozenRing, palette.Warning, 255, Hollow: true, Thickness: 2));
            }
        }

        return shapes;
    }

    /// <summary>A square outline, which is what "no route" is drawn as.</summary>
    private static MapPolyline StallBox(double x, double y, MapRgb colour, double scale = 1)
    {
        double half = StalledBox * scale;

        return new MapPolyline(
            [
                x - half, y - half,
                x + half, y - half,
                x + half, y + half,
                x - half, y + half,
                x - half, y - half,
            ],
            2.0 * scale,
            colour);
    }

    /// <summary>Where a stalled unit was trying to get to, which is the half of the story it has.</summary>
    private static IEnumerable<MapShape> GoalCross(WorldPos goal, MapRgb colour, Projection projection)
    {
        double x = projection.X(goal.X);
        double y = projection.Z(goal.Z);

        yield return new MapDisc(x, y, 5.0, colour, 255, Hollow: true, Thickness: 1.5);
        yield return new MapLine(x - 4, y, x + 4, y, 1.5, colour);
        yield return new MapLine(x, y - 4, x, y + 4, 1.5, colour);
    }

    // ---------------------------------------------------------------- events

    /// <summary>
    /// Shots, hits and deaths as marks, so a fight reads as a diagram rather than as a film.
    /// <para>
    /// Every mark fades with age across the window rather than being switched off at the end of
    /// it: a shot that disappeared between two frames would leave a reader unable to tell a lull
    /// from a reload, and a map of one instant with the last five seconds still on it is the
    /// picture that says what is happening and what just happened.
    /// </para>
    /// </summary>
    private static List<MapShape> Events(
        SimWorld world,
        MapPalette palette,
        Projection projection,
        MapRequest request,
        IReadOnlyList<MapEventMark> events,
        ref Drawn drawn)
    {
        var shapes = new List<MapShape>();
        long window = Math.Max(1, request.EventWindowTicks);

        foreach (MapEventMark mark in events)
        {
            long age = world.Tick - mark.Tick;

            if (age < 0 || age > window)
            {
                continue;
            }

            byte alpha = (byte)Math.Clamp(
                (long)palette.EventAlpha * (window - age) / window,
                40,
                palette.EventAlpha);

            MapRgb colour = palette.Unit(mark.Faction, mark.Role);
            double x = projection.X(mark.Position.X);
            double y = projection.Z(mark.Position.Z);

            switch (mark.Kind)
            {
                case MapEventKind.Shot:
                    if (mark.Target is { } target)
                    {
                        shapes.Add(new MapLine(
                            x,
                            y,
                            projection.X(target.X),
                            projection.Z(target.Z),
                            1.0,
                            colour,
                            alpha));
                    }

                    shapes.Add(new MapDisc(x, y, 1.8, colour, alpha));
                    break;

                case MapEventKind.Hit:
                    shapes.Add(new MapLine(x - 3.5, y - 3.5, x + 3.5, y + 3.5, 1.5, palette.Strike, alpha));
                    shapes.Add(new MapLine(x - 3.5, y + 3.5, x + 3.5, y - 3.5, 1.5, palette.Strike, alpha));
                    break;

                default:
                    shapes.Add(new MapDisc(x, y, 7.0, palette.Marker, alpha, Hollow: true, Thickness: 1.5));
                    shapes.Add(new MapLine(x - 6, y - 6, x + 6, y + 6, 2.0, colour, alpha));
                    shapes.Add(new MapLine(x - 6, y + 6, x + 6, y - 6, 2.0, colour, alpha));
                    break;
            }

            drawn.EventMarks++;
        }

        return shapes;
    }

    // ---------------------------------------------------------------- frame

    /// <summary>
    /// The furniture: a border, a scale bar, the map's own orientation, and a legend that says
    /// what every mark in the picture means and how many of them there are. A diagnostic drawing
    /// whose reader has to be told separately what the rings mean is a drawing that will be
    /// misread.
    /// </summary>
    private static List<MapShape> Frame(
        SimWorld world,
        MapPalette palette,
        Projection projection,
        MapRequest request,
        MapCensus census)
    {
        var shapes = new List<MapShape>();

        double left = request.Margin;
        double top = request.Margin;
        double right = request.Margin + projection.MapWidth;
        double bottom = request.Margin + projection.MapHeight;

        shapes.Add(new MapPolyline(
            [left, top, right, top, right, bottom, left, bottom, left, top],
            1.0,
            palette.Frame));

        // The scale bar: a whole number of metres that fits inside a quarter of the map, chosen
        // from the round numbers so the label is a distance rather than an arithmetic exercise.
        int metres = ScaleBarMetres(projection.MapWidth / projection.PixelsPerMetre);
        double barPixels = projection.Pixels(metres * WorldPos.MmPerMetre);
        double barY = bottom + 14;

        shapes.Add(new MapLine(left, barY - 4, left, barY + 4, 1.5, palette.Text));
        shapes.Add(new MapLine(left + barPixels, barY - 4, left + barPixels, barY + 4, 1.5, palette.Text));
        shapes.Add(new MapLine(left, barY, left + barPixels, barY, 1.5, palette.Text));
        shapes.Add(new MapText(left + barPixels + 6, barY + 4, 11, $"{metres} M", palette.Text));

        // Orientation. The map is world +X to the right and world +Z downward, which is a choice
        // and therefore has to be written on the picture rather than remembered by its author.
        double axisX = right - 150;
        shapes.Add(new MapLine(axisX, barY, axisX + 22, barY, 1.5, palette.Text));
        shapes.Add(new MapLine(axisX + 22, barY, axisX + 17, barY - 3, 1.5, palette.Text));
        shapes.Add(new MapLine(axisX + 22, barY, axisX + 17, barY + 3, 1.5, palette.Text));
        shapes.Add(new MapText(axisX + 26, barY + 4, 11, "+X", palette.Text));

        shapes.Add(new MapLine(axisX + 60, barY - 8, axisX + 60, barY + 14, 1.5, palette.Text));
        shapes.Add(new MapLine(axisX + 60, barY + 14, axisX + 57, barY + 9, 1.5, palette.Text));
        shapes.Add(new MapLine(axisX + 60, barY + 14, axisX + 63, barY + 9, 1.5, palette.Text));
        shapes.Add(new MapText(axisX + 66, barY + 4, 11, "+Z", palette.Text));

        shapes.AddRange(Legend(world, palette, request, census, projection, right + request.Margin, top));

        return shapes;
    }

    /// <summary>Metres for the scale bar: the largest round number that fits a quarter of the map.</summary>
    private static int ScaleBarMetres(double mapMetres)
    {
        double wanted = mapMetres / 4;
        int[] choices = [10, 20, 25, 50, 100, 200, 250, 500, 1_000];

        int best = choices[0];

        foreach (int choice in choices)
        {
            if (choice <= wanted)
            {
                best = choice;
            }
        }

        return best;
    }

    /// <summary>
    /// The legend: what the marks mean, and the census that is the picture's own reading of
    /// itself. The numbers are written here rather than only into the transcript because a
    /// picture read on its own — which is how a picture is read — has to carry the one number
    /// that says whether it is showing a healthy match or a starving one.
    /// <para>
    /// Its size comes from the map rather than from taste: a picture is read after it has been
    /// scaled down to fit whatever is showing it, so a legend of eleven pixels on a
    /// thirteen-hundred-pixel canvas is a legend nobody has read, and one that walks off the
    /// bottom of a six-hundred-pixel map is a legend that is not there at all. The step is one
    /// whole multiple of the writer's five-by-seven face, so the raster's glyphs stay crisp and
    /// the SVG's font size lands on the same figure.
    /// </para>
    /// </summary>
    private static List<MapShape> Legend(
        SimWorld world,
        MapPalette palette,
        MapRequest request,
        MapCensus census,
        Projection projection,
        double x,
        double y)
    {
        var shapes = new List<MapShape>();
        int step = projection.LegendStep;
        double size = MapFont.Height * step;
        double line = (MapFont.Height + 3) * step;
        double swatch = x + (4 * step);

        void Head(string text)
        {
            shapes.Add(new MapText(x, y, size, text, palette.Text));
            y += line;
        }

        void Row(string text, Action<double, double> mark)
        {
            mark(swatch, y - (step * 2));
            shapes.Add(new MapText(x + (12 * step), y, size, text, palette.Text));
            y += line;
        }

        Head("MIVIC MAP");
        Head($"TICK {world.Tick}");
        Head($"SEED {world.Seed}");
        Head(request.Scenario.ToUpperInvariant());
        Head(world.Outcome.ToString().ToUpperInvariant());

        // The unit marks are drawn in one power's colour and labelled with the counts from the
        // census, which is the point: the legend is not a key to a picture somebody drew, it is
        // the reading of the picture standing next to it.
        MapRgb sample = palette.Unit(Faction.Soviet, UnitKind.Tank);

        y += step * 2;
        Head("UNITS");

        Row($"MOVING {census.Moving}", (mx, my) => Dot(mx, my, sample));
        Row($"FIGHTING {census.Fighting}", (mx, my) =>
        {
            Dot(mx, my, sample);
            shapes.Add(new MapDisc(mx, my, Ring(FightingRing), palette.Text, 200, Hollow: true, Thickness: step * 0.5));
        });
        Row($"IDLE {census.Idle}", (mx, my) =>
        {
            Dot(mx, my, sample);
            shapes.Add(new MapDisc(mx, my, UnitRadius * 0.42 * step, palette.Marker));
        });
        Row($"FROZEN {census.Frozen}", (mx, my) =>
        {
            Dot(mx, my, sample);
            shapes.Add(new MapDisc(mx, my, Ring(FrozenRing), palette.Warning, 255, Hollow: true, Thickness: step));
        });
        Row($"STALLED {census.Stalled}", (mx, my) =>
        {
            Dot(mx, my, sample);
            shapes.Add(StallBox(mx, my, palette.Stall, step));
        });

        y += step * 2;
        Head("TRAJECTORY");

        Row($"PATHS {census.WithRoute}", (mx, my) =>
            shapes.Add(new MapLine(mx - (2 * step), my, mx + (4 * step), my, step * 0.8, sample, palette.PathAlpha)));
        Row($"TRAILS {census.WithTrail}", (mx, my) =>
        {
            shapes.Add(new MapLine(mx - (2 * step), my, mx + (4 * step), my, step * 0.6, sample, palette.TrailAlpha));

            for (int i = 0; i < 4; i++)
            {
                shapes.Add(new MapDisc(mx - (2 * step) + (i * step * 2), my, TrailTickRadius * step * 0.66, sample, palette.TrailTickAlpha));
            }
        });
        Row($"SAMPLES {census.TrailSamples}", (mx, my) => shapes.Add(
            new MapText(mx - step, my + (step * 2), size * 0.7, "=", palette.Frame)));

        y += step * 2;
        Head("ELSEWHERE");

        Row($"COVERAGE {census.CoverageDiscs}", (mx, my) =>
            shapes.Add(new MapDisc(mx + (step * 2), my, Ring(6), sample, palette.CoverageAlpha, Hollow: true, Thickness: step * 0.5)));
        Row($"OBJECTIVES {census.ObjectiveAreas}", (mx, my) =>
            shapes.Add(new MapDisc(mx + (step * 2), my, Ring(6), palette.ObjectiveOpen, palette.AreaAlpha, Hollow: true, Thickness: step)));
        Row($"EVENTS {census.EventMarks}", (mx, my) =>
        {
            double arm = step * 2;

            shapes.Add(new MapLine(mx - arm, my - arm, mx + arm, my + arm, step * 0.6, palette.Strike));
            shapes.Add(new MapLine(mx - arm, my + arm, mx + arm, my - arm, step * 0.6, palette.Strike));
        });

        y += step * 2;
        Head($"TRIGGERS {census.TriggersFired}");
        Head($"{MapTrails.DefaultFrozenSamples} SAMPLES {MapTrails.DefaultFrozenSamples * MapTrails.DefaultIntervalTicks / SimConstants.TickRate} S");
        Head($"FROZEN UNDER {MapTrails.FrozenThresholdMm / 1_000} M");

        return shapes;

        void Dot(double mx, double my, MapRgb colour) => shapes.Add(new MapDisc(mx, my, UnitRadius * step * 0.9, colour));

        // The legend's marks are the map's marks, drawn at the legend's own size rather than at
        // the map's: the ring round a unit is eight pixels because a unit is eight pixels, and a
        // legend that used the same figure would show a row of dots a reader cannot tell apart.
        double Ring(double radius) => radius * step * 0.8;
    }

    // ---------------------------------------------------------------- helpers

    /// <summary>
    /// What the drawing actually put on the page, as opposed to what the world contains.
    /// <para>
    /// Only the four numbers that are about the page live here. Everything else the census reports
    /// is counted from the world in <see cref="Count"/>, because a picture with the trails turned
    /// off is still a picture of the same match and has to say so.
    /// </para>
    /// </summary>
    private struct Drawn()
    {
        public int TerrainCells;
        public int CoverageDiscs;
        public int ObjectiveAreas;
        public int EventMarks;
    }

    /// <summary>
    /// The world's millimetres to the page's pixels, worked out once.
    /// <para>
    /// The map is the navigation grid — the same 65-cell lattice the pathfinder routes on and the
    /// terrain layer is built on — rather than the height field, because every position this
    /// picture draws is either a unit's millimetres or a cell of that lattice, and a canvas that
    /// was one cell short of the grid would draw units outside its own frame.
    /// </para>
    /// </summary>
    private readonly struct Projection
    {
        private readonly double _minXMm;
        private readonly double _minZMm;

        public Projection(SimWorld world, MapRequest request)
        {
            NavGrid grid = world.Navigation;

            _minXMm = grid.OriginMm;
            _minZMm = grid.OriginMm;
            PixelsPerMetre = request.PixelsPerMetre;
            MapWidth = (int)Math.Round(grid.Size * grid.CellSizeMm * request.PixelsPerMetre / (double)WorldPos.MmPerMetre);
            MapHeight = MapWidth;

            // The legend's size and the panel's width, solved together and from the map alone:
            // the step has to leave the legend inside the map's own height — some twenty-three
            // lines of it — and the panel has to be wide enough for the longest line at that step,
            // which is sixteen characters of a six-pixel advance. Anything smaller and the picture
            // is either unreadable or has a legend running off the edge of it.
            LegendStep = Math.Clamp(MapHeight / 250, 2, 4);
            Panel = Math.Max(request.Panel, 36 + (96 * LegendStep));

            Width = request.Margin + MapWidth + request.Margin + Panel + request.Margin;
            Height = request.Margin + MapHeight + request.Footer + request.Margin;
            Margin = request.Margin;
            GridSize = grid.Size;
            CellSizeMm = grid.CellSizeMm;
        }

        /// <summary>Whole multiple of the writer's face the legend is drawn at.</summary>
        public int LegendStep { get; }

        /// <summary>Width of the panel the legend is drawn in, in pixels.</summary>
        public int Panel { get; }

        public double PixelsPerMetre { get; }

        public int MapWidth { get; }

        public int MapHeight { get; }

        public int Width { get; }

        public int Height { get; }

        public int Margin { get; }

        private int GridSize { get; }

        private int CellSizeMm { get; }

        /// <summary>World X in millimetres to a pixel column.</summary>
        public double X(int mm) => Margin + ((mm - _minXMm) * PixelsPerMetre / WorldPos.MmPerMetre);

        /// <summary>World Z in millimetres to a pixel row.</summary>
        public double Z(int mm) => Margin + ((mm - _minZMm) * PixelsPerMetre / WorldPos.MmPerMetre);

        /// <summary>A length in millimetres to a length in pixels.</summary>
        public double Pixels(int mm) => mm * PixelsPerMetre / WorldPos.MmPerMetre;

        /// <summary>A pixel column from a world X in millimetres, as a whole number.</summary>
        public int PixelX(int mm) => (int)Math.Round(X(mm));

        /// <summary>A pixel row from a world Z in millimetres, as a whole number.</summary>
        public int PixelZ(int mm) => (int)Math.Round(Z(mm));

        /// <summary>The world millimetres at a cell's low edge.</summary>
        public int CellMinXMm(int cell) => (int)(_minXMm + ((cell % GridSize) * (long)CellSizeMm));

        /// <summary>The world millimetres at a cell's high edge.</summary>
        public int CellMaxXMm(int cell) => (int)(_minXMm + (((cell % GridSize) + 1) * (long)CellSizeMm));

        /// <summary>The world millimetres at a cell's low edge, along Z.</summary>
        public int CellMinZMm(int cell) => (int)(_minZMm + ((cell / GridSize) * (long)CellSizeMm));

        /// <summary>The world millimetres at a cell's high edge, along Z.</summary>
        public int CellMaxZMm(int cell) => (int)(_minZMm + (((cell / GridSize) + 1) * (long)CellSizeMm));
    }
}