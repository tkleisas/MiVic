using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Bridges and snow sight: the last two pieces of the terrain design. A bridge is a
/// ford a player chose the site of; snow blinds as well as slows.
/// </summary>
public sealed class BridgeTests
{
    private const ulong Seed = 20250101;

    /// <summary>A world with a finished factory, so engineering work is allowed.</summary>
    private static SimWorld WithFactory(out int waterCell)
    {
        SimWorld world = new(Seed, capacity: 32);
        TerrainLayer terrain = world.TerrainTypes;
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        world.Spawn(Faction.Soviet, 0, UnitKind.Factory, centre, Fix32.Zero, 5_000);

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.Energy = 10_000;
        state.Water = 10_000;

        waterCell = -1;

        // A deep-water cell with dry land on both sides on one axis: exactly the
        // site a player would want to bridge.
        for (int index = 0; index < terrain.CellCount && waterCell < 0; index++)
        {
            if (terrain.TypeAt(index) != TerrainType.DeepWater)
            {
                continue;
            }

            int cx = world.Navigation.CellX(index);
            int cz = world.Navigation.CellZ(index);

            int left = world.Navigation.IndexOf(cx - 3, cz);
            int right = world.Navigation.IndexOf(cx + 3, cz);

            if (left >= 0 && right >= 0 &&
                terrain.IsPassable(left, MovementClass.Tracked) &&
                terrain.IsPassable(right, MovementClass.Tracked))
            {
                waterCell = index;
            }
        }

        return world;
    }

    [Fact]
    public void ABridgeMakesWaterCrossable()
    {
        SimWorld world = WithFactory(out int waterCell);

        Assert.True(waterCell >= 0, "No suitable water to bridge for this seed.");
        Assert.False(world.TerrainTypes.IsPassable(waterCell, MovementClass.Tracked));

        WorldPos target = world.Navigation.CentreOf(waterCell);

        Assert.True(world.CanBuildBridge(0, target, out _));

        BuildBridge(world, target);

        Assert.True(world.TerrainTypes.IsPassable(waterCell, MovementClass.Tracked), "The bridge went nowhere.");
    }

    /// <summary>
    /// A bridge is a chain of blocks, so losing one cuts it. Artillery does not delete a crossing:
    /// it knocks a hole in one, the water comes back through the hole, and what is left either side
    /// of it is deck that leads nowhere. That is the difference between a bridge a player can
    /// defend and a bridge that either exists or does not.
    /// </summary>
    [Fact]
    public void ABlastKnocksAHoleInTheCrossingAndTheWaterComesBack()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(TryFindCrossing(world, out WorldPos site, out int[] span, out _, out _));
        BuildBridge(world, site);

        int middle = span[span.Length / 2];
        Assert.True(world.Bridgeworks.HasBlock(middle));
        Assert.True(world.Bridgeworks.State(0).Intact);

        // A hit that is not enough leaves the crossing alone.
        Assert.False(world.Bridgeworks.Damage(world.TerrainTypes, middle, Bridgeworks.BlockHealth - 1, 2));
        Assert.True(world.Bridgeworks.HasBlock(middle));
        Assert.Equal(TerrainType.ShallowWater, world.TerrainTypes.TypeAt(middle));
        Assert.True(world.Bridgeworks.State(0).Intact);

        // One more and the block is gone.
        Assert.True(world.Bridgeworks.Damage(world.TerrainTypes, middle, 1, 2));
        Assert.False(world.Bridgeworks.HasBlock(middle));

        // The water it was built over is back, and the cell is impassable again.
        Assert.Equal(TerrainType.DeepWater, world.TerrainTypes.TypeAt(middle));
        Assert.Equal(0, world.TerrainTypes.CostPermille(middle, MovementClass.Tracked, 1_000));

        // The crossing is cut; the deck either side of the hole is still standing.
        BridgeState state = world.Bridgeworks.State(0);

        Assert.True(state.Cut);
        Assert.False(state.Intact);
        Assert.True(state.Complete);
        Assert.Equal(state.Built - 1, state.Standing);

        // And the gap is water, so the same order the player used the first time can be given
        // again: repairing a cut crossing costs a crossing, at the hole.
        Assert.True(world.CanBuildBridge(0, world.Navigation.CentreOf(middle), out string reason), reason);
    }

    /// <summary>
    /// The deck belongs to a side, and only the other side breaks it. Falling in with the rule the
    /// splash damage on units already follows: a stray shell should not cut the crossing your own
    /// army is using, and an enemy's should.
    /// </summary>
    [Fact]
    public void OnlyTheOtherSideBreaksYourDeck()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(TryFindCrossing(world, out WorldPos site, out int[] span, out _, out _));
        BuildBridge(world, site);

        int cell = span[0];

        // The owner's own fire, and an ally's: neither touches it.
        Assert.False(world.Bridgeworks.Damage(world.TerrainTypes, cell, 10_000, 0));
        Assert.False(world.Bridgeworks.Damage(world.TerrainTypes, cell, 10_000, 1));
        Assert.True(world.Bridgeworks.HasBlock(cell));

        // The enemy's does.
        Assert.True(world.Bridgeworks.Damage(world.TerrainTypes, cell, 10_000, 2));
        Assert.False(world.Bridgeworks.HasBlock(cell));
    }

    /// <summary>
    /// Two crossings that meet at a cell share the deck there, and losing it cuts both.
    /// <para>
    /// The simulation allows spans to cross — the rule that a span runs the narrow way does not
    /// prevent it, and on this seed a great many pairs of sites do — so the choice is whether the
    /// shared cell is one block or two decks drawn through each other. One block is what a deck
    /// physically is, and it makes the crossroads fall out of the same damage rule as everything
    /// else: knock the junction out and neither crossing can be used, because there is one deck
    /// under them and it is gone.
    /// </para>
    /// </summary>
    [Fact]
    public void TwoCrossingsAtOneCellShareOneBlockAndLosingItCutsBoth()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(
            TryFindCrossroads(world, out int shared, out WorldPos firstSite, out WorldPos secondSite),
            "No two spans cross on this seed.");

        BuildBridge(world, firstSite);
        BuildBridge(world, secondSite);

        Assert.Equal(2, world.Bridgeworks.Count);

        // One block, laid once, running both ways.
        Assert.True(world.Bridgeworks.TryGetBlock(shared, out BridgeBlock block));
        Assert.Equal(BridgeAxis.Junction, block.Axis);
        Assert.Equal(Bridgeworks.BlockHealth, block.Health);

        BridgeState first = world.Bridgeworks.State(0);
        BridgeState second = world.Bridgeworks.State(1);

        Assert.True(first.Intact, "The first crossing is not whole.");
        Assert.True(second.Intact, "The second crossing is not whole.");

        // Knock the junction out: the cell goes back to water, and both crossings are cut.
        Assert.True(world.Bridgeworks.Damage(world.TerrainTypes, shared, Bridgeworks.BlockHealth, 2));
        Assert.False(world.Bridgeworks.HasBlock(shared));
        Assert.Equal(TerrainType.DeepWater, world.TerrainTypes.TypeAt(shared));

        Assert.True(world.Bridgeworks.State(0).Cut, "The first crossing survived losing the junction.");
        Assert.True(world.Bridgeworks.State(1).Cut, "The second crossing survived losing the junction.");
    }

    /// <summary>
    /// Two sites whose spans meet at one cell running different ways: the whole question of bridges
    /// crossing bridges, found on the map rather than assumed.
    /// </summary>
    private static bool TryFindCrossroads(SimWorld world, out int shared, out WorldPos first, out WorldPos second)
    {
        int size = world.TerrainTypes.Size;
        int[] plan = new int[SimWorld.MaxBridgeCells];
        var coverage = new Dictionary<int, (bool AlongX, int Site)>();

        for (int cell = 0; cell < world.TerrainTypes.CellCount; cell++)
        {
            if (world.TerrainTypes.TypeAt(cell) is not (TerrainType.ShallowWater or TerrainType.DeepWater))
            {
                continue;
            }

            WorldPos site = world.Navigation.CentreOf(cell);

            if (!world.TryPlanBridge(0, site, plan, out int count, out _) || count < 2)
            {
                continue;
            }

            bool alongX = Math.Abs(plan[1] - plan[0]) == 1;

            for (int i = 0; i < count; i++)
            {
                if (coverage.TryGetValue(plan[i], out (bool AlongX, int Site) other))
                {
                    if (other.AlongX != alongX)
                    {
                        shared = plan[i];
                        first = world.Navigation.CentreOf(other.Site);
                        second = site;
                        _ = size;
                        return true;
                    }

                    continue;
                }

                coverage[plan[i]] = (alongX, cell);
            }
        }

        shared = -1;
        first = default;
        second = default;
        return false;
    }

    /// <summary>
    /// Orders a crossing and runs the simulation until its deck is across. A bridge is
    /// engineering work rather than a surface edit, so a test that looks at the ground one tick
    /// after the order is looking at a site that has been paid for and not yet built.
    /// </summary>
    private static void BuildBridge(SimWorld world, WorldPos site, int team = 0)
    {
        int[] cells = new int[SimWorld.MaxBridgeCells];

        Assert.True(world.TryPlanBridge(team, site, cells, out int count, out string reason), reason);

        world.Enqueue(SimCommand.Bridge(site, world.Tick + 1, team));
        world.Step();
        world.RunTicks(Bridgeworks.TicksFor(count) + 1);
    }

    [Fact]
    public void ABridgeCostsMoreTheLongerItIs()
    {
        SimWorld world = WithFactory(out _);

        // The two ends of the price list, on a map that has both: a one-cell site and the widest
        // crossing there is.
        int[] plan = new int[SimWorld.MaxBridgeCells];
        WorldPos single = default;
        WorldPos widest = default;
        int widestCells = 0;

        for (int cell = 0; cell < world.TerrainTypes.CellCount; cell++)
        {
            if (world.TerrainTypes.TypeAt(cell) is not (TerrainType.ShallowWater or TerrainType.DeepWater))
            {
                continue;
            }

            WorldPos candidate = world.Navigation.CentreOf(cell);

            if (!world.TryPlanBridge(0, candidate, plan, out int count, out _))
            {
                continue;
            }

            if (count == 1)
            {
                single = candidate;
            }
            else if (count > widestCells)
            {
                widestCells = count;
                widest = candidate;
            }
        }

        Assert.True(widestCells > 1, "This seed has no crossing longer than one cell.");

        // The price is the setup plus so much a cell, cell for cell.
        BridgeCost one = Bridgeworks.Cost(1);

        Assert.Equal(Bridgeworks.SetupMaterials + Bridgeworks.MaterialsPerCell, one.Materials);
        Assert.Equal(Bridgeworks.SetupEnergy + Bridgeworks.EnergyPerCell, one.Energy);
        Assert.Equal(Bridgeworks.SetupWater + Bridgeworks.WaterPerCell, one.Water);
        Assert.Equal(
            new BridgeCost(
                Bridgeworks.SetupMaterials + (widestCells * Bridgeworks.MaterialsPerCell),
                Bridgeworks.SetupEnergy + (widestCells * Bridgeworks.EnergyPerCell),
                Bridgeworks.SetupWater + (widestCells * Bridgeworks.WaterPerCell)),
            Bridgeworks.Cost(widestCells));

        Assert.True(Bridgeworks.Cost(widestCells).Materials > one.Materials);
        Assert.True(Bridgeworks.Cost(widestCells).Energy > one.Energy);
        Assert.True(Bridgeworks.Cost(widestCells).Water > one.Water);

        // And the simulation charges what it quoted, for the span it actually planned.
        int before = world.Team(0).Materials;
        OrderBridge(world, widest, out int planned);
        world.Step();

        Assert.Equal(widestCells, planned);
        Assert.Equal(before - Bridgeworks.Cost(planned).Materials, world.Team(0).Materials);

        // A short crossing is cheaper than a long one, and a team that can afford the first may
        // not be able to afford the second — which is the whole reason the price is quoted per
        // site rather than on the button.
        SimWorld poor = WithFactory(out _);
        poor.TeamRef(0).Materials = one.Materials;
        poor.TeamRef(0).Energy = one.Energy;
        poor.TeamRef(0).Water = one.Water;

        Assert.True(poor.CanBuildAnyBridge(0, out _), "The cheapest crossing should be affordable.");
        Assert.True(poor.CanBuildBridge(0, single, out string shortReason), shortReason);
        Assert.False(poor.CanBuildBridge(0, widest, out string longReason));
        Assert.Contains("λείπουν", longReason);
    }

    [Fact]
    public void ABridgeCostsResources()
    {
        SimWorld world = WithFactory(out int waterCell);
        WorldPos target = world.Navigation.CentreOf(waterCell);

        int before = world.Team(0).Materials;

        OrderBridge(world, target, out int cells);
        world.Step();

        Assert.Equal(before - Bridgeworks.Cost(cells).Materials, world.Team(0).Materials);
    }

    [Fact]
    public void ABridgeNeedsWaterAndAFactory()
    {
        SimWorld world = WithFactory(out int waterCell);

        // Dry ground is not a crossing.
        int dry = world.Navigation.IndexOfWorld(WorldPos.Origin);
        WorldPos plain = world.Navigation.CentreOf(world.Navigation.NearestWalkable(dry));

        Assert.False(world.CanBuildBridge(0, plain, out string reason));
        Assert.Contains("νερό", reason);

        // And a team with no industry cannot span a river.
        WorldPos water = world.Navigation.CentreOf(waterCell);
        world.TeamRef(0).Materials = 0;

        Assert.False(world.CanBuildBridge(0, water, out _));

        world.TeamRef(0).Materials = 10_000;
        Assert.False(world.CanBuildBridge(1, water, out string noFactory));
        Assert.Contains("εργοστάσιο", noFactory);
    }

    [Fact]
    public void ARefusedBridgeIsNotChargedFor()
    {
        // A misplaced click must cost nothing but the click.
        SimWorld world = WithFactory(out _);

        int before = world.Team(0).Materials;
        int dry = world.Navigation.IndexOfWorld(WorldPos.Origin);

        world.Enqueue(SimCommand.Bridge(
            world.Navigation.CentreOf(world.Navigation.NearestWalkable(dry)),
            world.Tick + 1,
            0));
        world.Step();

        Assert.Equal(before, world.Team(0).Materials);
    }

    [Fact]
    public void SnowShortensSight()
    {
        SimWorld world = new(Seed, capacity: 32);
        WorldPos centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

        EntityId scout = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, centre, Fix32.Zero, 5_000);

        world.RunTicks(VisionSystem.UpdateInterval + 1);
        int clearCells = VisibleCells(world, centre);

        // Snow under the same unit: the same position, the same team, less seen.
        world.TerrainTypes.SetType(world.Navigation.IndexOfWorld(centre), TerrainType.Snow);
        world.RunTicks(VisionSystem.UpdateInterval + 1);

        int snowCells = VisibleCells(world, centre);

        Assert.True(snowCells > 0, "Snow blinded the unit completely.");
        Assert.True(snowCells < clearCells, $"Snow did not shorten sight ({clearCells} -> {snowCells}).");
        Assert.True(world.IsAliveSlot(scout.Slot));
    }

    private static int VisibleCells(SimWorld world, WorldPos centre)
    {
        // Scan the whole grid: the point is to compare two runs, not to be clever
        // about the window.
        int count = 0;

        for (int index = 0; index < world.Navigation.CellCount; index++)
        {
            if (world.Visibility.IsVisible(0, index))
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// A crossing on the generated map: a run of water with drivable land at both ends of it,
    /// which is what a player clicks on when they want a bridge and what the report was about.
    /// </summary>
    /// <param name="Site">Where the click would land.</param>
    /// <param name="Span">The cells the crossing would convert, ends included.</param>
    /// <param name="NearBank">Drivable land at one end.</param>
    /// <param name="FarBank">Drivable land at the other, beyond the water.</param>
    private static bool TryFindCrossing(SimWorld world, out WorldPos site, out int[] span, out int nearBank, out int farBank)
    {
        NavGrid grid = world.Navigation;
        TerrainLayer terrain = world.TerrainTypes;
        int[] cells = new int[SimWorld.MaxBridgeCells];

        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (terrain.TypeAt(index) is not (TerrainType.ShallowWater or TerrainType.DeepWater))
            {
                continue;
            }

            site = grid.CentreOf(index);

            if (!world.TryPlanBridge(0, site, cells, out int count, out _) || count < 3)
            {
                continue;
            }

            int size = terrain.Size;
            int low = cells[0];
            int high = cells[0];

            for (int i = 1; i < count; i++)
            {
                low = Math.Min(low, cells[i]);
                high = Math.Max(high, cells[i]);
            }

            // The span runs along one axis, so the cells just outside it are the two banks.
            bool alongX = high - low < size;
            int before = alongX ? low - 1 : low - size;
            int after = alongX ? high + 1 : high + size;

            if (before < 0 || after >= terrain.CellCount)
            {
                continue;
            }

            if (!Drivable(terrain, before) || !Drivable(terrain, after))
            {
                continue;
            }

            span = cells[..count];
            nearBank = before;
            farBank = after;
            return true;
        }

        site = default;
        span = [];
        nearBank = -1;
        farBank = -1;
        return false;
    }

    /// <summary>True when a tank could stand there: solid ground it can cross.</summary>
    private static bool Drivable(TerrainLayer terrain, int cell)
        => terrain.TypeAt(cell) is not (TerrainType.DeepWater or TerrainType.ShallowWater)
            && terrain.IsPassable(cell, MovementClass.Tracked);

    /// <summary>
    /// The whole point of a bridge: a genuine crossing becomes ground a vehicle can drive
    /// over, and every cell the span covers is part of it. The report that started this was a
    /// player clicking on water; what they were asking for is this.
    /// </summary>
    [Fact]
    public void ABridgeTurnsANarrowCrossingIntoDrivableGround()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(
            TryFindCrossing(world, out WorldPos site, out int[] span, out int nearBank, out int farBank),
            "No crossing with drivable land at both ends for this seed.");

        // Before: the water is impassable, and the two banks are not connected.
        foreach (int cell in span)
        {
            Assert.Equal(0, world.TerrainTypes.CostPermille(cell, MovementClass.Tracked, 1_000));
        }

        Assert.True(world.CanBuildBridge(0, site, out _));

        BuildBridge(world, site);

        foreach (int cell in span)
        {
            Assert.Equal(TerrainType.ShallowWater, world.TerrainTypes.TypeAt(cell));
            Assert.True(
                world.TerrainTypes.IsPassable(cell, MovementClass.Tracked),
                $"The span left cell {cell} impassable.");
        }

        Assert.True(Drivable(world.TerrainTypes, nearBank) && Drivable(world.TerrainTypes, farBank));
    }

    /// <summary>
    /// And the crossing is usable: a tank on one bank drives to the other. The same order
    /// without the bridge leaves it standing at the water, which is what makes this a test of
    /// the bridge rather than of the map.
    /// </summary>
    [Fact]
    public void AUnitDrivesAcrossTheBridgeAndNotWithoutIt()
    {
        Assert.True(CrossesTheWater(bridge: true), "The tank did not cross the bridge it was given.");
        Assert.False(CrossesTheWater(bridge: false), "The tank crossed water it had no bridge over.");
    }

    /// <summary>Orders a tank from one bank of the crossing to the other and reports whether it arrives.</summary>
    private static bool CrossesTheWater(bool bridge)
    {
        SimWorld world = WithFactory(out _);

        Assert.True(TryFindCrossing(world, out WorldPos site, out _, out int nearBank, out int farBank));

        if (bridge)
        {
            BuildBridge(world, site);
        }

        WorldPos start = world.Navigation.CentreOf(nearBank);
        WorldPos goal = world.Navigation.CentreOf(farBank);
        EntityId tank = world.Spawn(Faction.Soviet, 0, UnitKind.Tank, start, Fix32.FromInt(400), 1_000);

        world.OrderMove(tank, goal, 0);
        world.RunTicks(800);

        Assert.True(world.TryGet(tank, out Entity entity));

        return entity.Position.HorizontalDistanceTo(goal) <= SimConstants.ArrivalRadiusMm;
    }

    /// <summary>
    /// A refusal has to explain itself. The reason is what the interface puts in front of the
    /// player, so a refusal that returns false with nothing to say is the bug the bug report
    /// was made of — four different rules, four different sentence.
    /// </summary>
    [Fact]
    public void EveryRefusalNamesTheRuleThatRefusedIt()
    {
        SimWorld world = WithFactory(out int waterCell);
        Assert.True(waterCell >= 0, "No water to bridge for this seed.");

        WorldPos water = world.Navigation.CentreOf(waterCell);

        // Dry ground is not a crossing.
        int dry = world.Navigation.IndexOfWorld(WorldPos.Origin);
        WorldPos plain = world.Navigation.CentreOf(world.Navigation.NearestWalkable(dry));

        Assert.False(world.CanBuildBridge(0, plain, out string dryReason));
        Assert.Contains("νερό", dryReason);

        // Off the map entirely.
        Assert.False(world.CanBuildBridge(0, new WorldPos(SimConstants.MapExtentMm, 0, 0), out string offMapReason));
        Assert.Contains("χάρτη", offMapReason);

        // No industry.
        Assert.False(world.CanBuildBridge(1, water, out string industryReason));
        Assert.Contains("εργοστάσιο", industryReason);

        // Each resource on its own, because a player looking at a greyed-out button needs to
        // know which one they are short of — and how much of it, since the price depends on the
        // length of the crossing they are pointing at.
        ref TeamState state = ref world.TeamRef(0);

        state.Materials = Bridgeworks.SetupMaterials - 1;
        Assert.False(world.CanBuildBridge(0, water, out string materialsReason));
        Assert.Contains("λείπουν", materialsReason);
        Assert.Contains("Π", materialsReason);

        state.Materials = 10_000;
        state.Energy = Bridgeworks.SetupEnergy - 1;
        Assert.False(world.CanBuildBridge(0, water, out string energyReason));
        Assert.Contains("λείπουν", energyReason);
        Assert.Contains("Ε", energyReason);

        state.Energy = 10_000;
        state.Water = Bridgeworks.SetupWater - 1;
        Assert.False(world.CanBuildBridge(0, water, out string waterReason));
        Assert.Contains("λείπουν", waterReason);
        Assert.Contains("Ν", waterReason);

        state.Water = 10_000;

        // And water too wide to span, which is the one refusal a site can earn on its own.
        Assert.True(TryFindWideWater(world, out WorldPos middle, out int width));
        Assert.True(width > SimWorld.MaxBridgeSpan, "The wide site is not wider than a span.");
        Assert.False(world.CanBuildBridge(0, middle, out string widthReason));
        Assert.Contains("φαρδύ", widthReason);
    }

    /// <summary>
    /// Water wider than any span, with the width the rule measured, so the test can say what
    /// "too wide" means rather than trusting that some site somewhere is refused.
    /// </summary>
    private static bool TryFindWideWater(SimWorld world, out WorldPos site, out int width)
    {
        NavGrid grid = world.Navigation;
        TerrainLayer terrain = world.TerrainTypes;

        for (int index = 0; index < terrain.CellCount; index++)
        {
            if (terrain.TypeAt(index) is not (TerrainType.ShallowWater or TerrainType.DeepWater))
            {
                continue;
            }

            WorldPos candidate = grid.CentreOf(index);

            if (world.CanBuildBridge(0, candidate, out string reason) || !reason.Contains("φαρδύ"))
            {
                continue;
            }

            // The width the plan measured, taken from the plan itself.
            world.TryPlanBridge(0, candidate, default, out width, out _);
            site = candidate;
            return true;
        }

        site = default;
        width = 0;
        return false;
    }

    /// <summary>
    /// The plan a preview is drawn from is the plan the command carries out. The client ghosts
    /// the cells it was promised and the simulation converts the cells it walked: if the two
    /// lists can differ, the ghost can promise a crossing that is not the one built.
    /// </summary>
    [Fact]
    public void ThePlanIsExactlyTheGroundTheCommandCarves()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(TryFindCrossing(world, out WorldPos site, out int[] span, out _, out _));

        int[] planned = new int[SimWorld.MaxBridgeCells];
        Assert.True(world.TryPlanBridge(0, site, planned, out int count, out _));
        Assert.Equal(span.Length, count);

        TerrainType[] before = new TerrainType[world.TerrainTypes.CellCount];

        for (int cell = 0; cell < before.Length; cell++)
        {
            before[cell] = world.TerrainTypes.TypeAt(cell);
        }

        BuildBridge(world, site);

        for (int cell = 0; cell < before.Length; cell++)
        {
            bool plannedCell = planned.AsSpan(0, count).Contains(cell);
            bool changed = before[cell] != world.TerrainTypes.TypeAt(cell);

            Assert.True(
                plannedCell == changed,
                plannedCell
                    ? $"Cell {cell} was planned but not carved."
                    : $"Cell {cell} was carved but was never planned.");
        }
    }

    /// <summary>
    /// The work happens, and it can be watched happening: a crossing is recorded the moment it
    /// is ordered, its deck goes up a cell at a time from one bank, and the water it has reached
    /// is ford and no more. Before this the whole thing was one tick and a surface change, with
    /// nothing on screen and nothing to see.
    /// </summary>
    [Fact]
    public void ABridgeIsBuiltOneCellAtATimeFromOneBank()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(TryFindCrossing(world, out WorldPos site, out int[] span, out _, out _));

        OrderBridge(world, site, out int total);
        world.Step();
        Assert.Equal(span.Length, total);

        // Ordered and paid for, and not one cell of it is up yet.
        BridgeState start = world.Bridgeworks.State(0);

        Assert.Equal(total, start.Total);
        Assert.Equal(0, start.Built);
        Assert.False(start.Complete);
        Assert.Equal(0, start.ProgressPermille);
        Assert.Equal(TerrainType.DeepWater, world.TerrainTypes.TypeAt(span[0]));

        // Two cells in: the ford is a prefix of the span, from one bank, and the rest is water.
        world.RunTicks(Bridgeworks.SetupTicks + (Bridgeworks.TicksPerCell * 2) + 1);
        BridgeState middle = world.Bridgeworks.State(0);

        Assert.Equal(2, middle.Built);
        Assert.True(middle.ProgressPermille is > 0 and < 1_000);
        Assert.True(middle.RemainingTicks(world.Tick) > 0);

        for (int i = 0; i < total; i++)
        {
            Assert.Equal(
                i < middle.Built ? TerrainType.ShallowWater : TerrainType.DeepWater,
                world.TerrainTypes.TypeAt(span[i]));
        }

        // And finished, on the tick it said it would be.
        world.RunTicks(Bridgeworks.TicksFor(total));

        BridgeState done = world.Bridgeworks.State(0);

        Assert.True(done.Complete);
        Assert.Equal(total, done.Built);
        Assert.Equal(0, done.RemainingTicks(world.Tick));
        Assert.Equal(1_000, done.ProgressPermille);

        foreach (int cell in span)
        {
            Assert.Equal(TerrainType.ShallowWater, world.TerrainTypes.TypeAt(cell));
        }
    }

    /// <summary>
    /// A wider crossing takes longer, which is the whole reason the work is measured in cells:
    /// a fifteen-cell span is not the same afternoon's work as a three-cell one.
    /// </summary>
    [Fact]
    public void AWiderCrossingTakesLongerToBuild()
    {
        SimWorld world = WithFactory(out _);

        Assert.True(TryFindCrossing(world, out WorldPos narrow, out int[] narrowSpan, out _, out _));
        Assert.True(TryFindWidestCrossing(world, out WorldPos wide, out int wideCells));
        Assert.True(wideCells > narrowSpan.Length, "This seed has no crossing wider than the first one found.");

        // Ordered in the same tick, so the two crossings' clocks start together and the
        // difference between them is the work and nothing else.
        OrderBridge(world, narrow, out int narrowCells);
        OrderBridge(world, wide, out _);
        world.Step();

        long narrowWork = world.Bridgeworks.State(0).RemainingTicks(world.Tick);
        long wideWork = world.Bridgeworks.State(1).RemainingTicks(world.Tick);

        Assert.Equal(Bridgeworks.TicksFor(narrowCells), narrowWork);
        Assert.Equal(narrowWork + ((wideCells - narrowCells) * Bridgeworks.TicksPerCell), wideWork);

        // And the difference is real: the narrow crossing is across on a tick when the wide
        // one still has water in the middle of it.
        world.RunTicks(narrowWork + 1);

        Assert.True(world.Bridgeworks.State(0).Complete);
        Assert.False(world.Bridgeworks.State(1).Complete);
        Assert.True(world.Bridgeworks.State(1).Built < wideCells);
    }

    /// <summary>Queues a crossing for the next tick, and reports the span it planned.</summary>
    private static void OrderBridge(SimWorld world, WorldPos site, out int cells)
    {
        Assert.True(world.TryPlanBridge(0, site, default, out cells, out string reason), reason);
        Assert.True(world.CanBuildBridge(0, site, out _));

        world.Enqueue(SimCommand.Bridge(site, world.Tick + 1, 0));
    }

    /// <summary>The widest crossing on the map, which is the longest job a player could order.</summary>
    private static bool TryFindWidestCrossing(SimWorld world, out WorldPos site, out int cells)
    {
        int[] plan = new int[SimWorld.MaxBridgeCells];
        site = default;
        cells = 0;

        for (int cell = 0; cell < world.TerrainTypes.CellCount; cell++)
        {
            if (world.TerrainTypes.TypeAt(cell) is not (TerrainType.ShallowWater or TerrainType.DeepWater))
            {
                continue;
            }

            WorldPos candidate = world.Navigation.CentreOf(cell);

            if (world.TryPlanBridge(0, candidate, plan, out int count, out _) && count > cells)
            {
                cells = count;
                site = candidate;
            }
        }

        return cells > 0;
    }

    /// <summary>
    /// The button and the command answer the same question. The interface greys the button out
    /// with <see cref="SimWorld.CanBuildAnyBridge"/> and the order is accepted or refused with
    /// <see cref="SimWorld.CanBuildBridge"/>: a button that looks available and does nothing is
    /// the same bug as a refusal with no words, and this is the pair staying in step.
    /// </summary>
    [Fact]
    public void TheButtonRuleIsTheCommandsRule()
    {
        SimWorld world = WithFactory(out int waterCell);
        WorldPos water = world.Navigation.CentreOf(waterCell);

        Assert.True(world.CanBuildAnyBridge(0, out string allowed));
        Assert.Equal(string.Empty, allowed);
        Assert.True(world.CanBuildBridge(0, water, out _));

        // A team with no factory: neither answer may be yes, and both say the same thing.
        Assert.False(world.CanBuildAnyBridge(1, out string noIndustry));
        Assert.False(world.CanBuildBridge(1, water, out string noIndustrySite));
        Assert.Equal(noIndustry, noIndustrySite);

        // And out of resources. What the button measures is the cheapest crossing there is, since
        // it cannot know the site — and that is the first thing the site check looks at too, so
        // the two agree on the shortfall. What a particular span costs on top of it is asked in
        // the test above, where a team can afford a ditch and not a river.
        world.TeamRef(0).Materials = 0;
        Assert.False(world.CanBuildAnyBridge(0, out string cheapest));
        Assert.Equal($"λείπουν {Bridgeworks.Cost(1).Materials} Π", cheapest);

        Assert.False(world.CanBuildBridge(0, water, out string nothing));
        Assert.Equal(cheapest, nothing);
    }
}
