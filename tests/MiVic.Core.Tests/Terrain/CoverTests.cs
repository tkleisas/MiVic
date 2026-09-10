using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Terrain;

/// <summary>
/// Cover as a property of the ground rather than of the surface byte: what is growing on
/// the cell, what shape the cell is, and what the cell is made of, combined in one formula
/// that lives in <see cref="TerrainLayer.CoverPermille"/>.
/// <para>
/// A formula is proved with numbers, not with a call that returns without throwing, so every
/// test here prints the arithmetic it checked: the canopy's own worth in permille, the gap
/// between what a wood does for a man and for a tank, the ordering the landform terms claim,
/// and the two ends of the scale over the whole input space. The last test is the claim the
/// step exists for, measured where the player would see it — two identical battles that
/// differ only in whether the ground under the defender's feet has a wood on it.
/// </para>
/// </summary>
public sealed class CoverTests
{
    private const ulong Seed = 20250101;

    /// <summary>
    /// Every movement class, in the schema's order. Spelled out rather than enumerated so that
    /// a class added later shows up here as a compile-time omission rather than as a class the
    /// sweep quietly stopped covering.
    /// </summary>
    private static readonly MovementClass[] Classes =
    [
        MovementClass.None,
        MovementClass.Foot,
        MovementClass.Tracked,
        MovementClass.Wheeled,
        MovementClass.Air,
    ];

    private static readonly string[] LandformNames =
        ["plain", "slope", "ridge", "valley", "pass", "plateau", "basin", "shelf"];

    /// <summary>
    /// A wood shelters infantry and does not shelter tanks, and it does it by *how much is
    /// growing there*: writing the canopy to zero — which is what fire, tracks and regrowth
    /// will all do to this field — takes the shelter away without touching the surface byte.
    /// <para>
    /// This is the burned wood, tested before the fire exists to cause it. The magnitude is
    /// asserted as well as the sign: the cover has to move by exactly what the density was
    /// worth, or the field is being consulted rather than followed.
    /// </para>
    /// </summary>
    [Fact]
    public void BurningTheCanopyTakesTheShelterWithIt()
    {
        TerrainLayer terrain = Build().TerrainTypes;
        int cell = DensestWoodlandCell(terrain);

        Assert.True(
            cell >= 0,
            $"the map has no woodland on ground that carries no cover term of its own, so there is nothing here to " +
            $"burn. {Woodland(terrain)}");

        TerrainAttributes before = terrain.AttributesAt(cell);
        TerrainType surface = terrain.TypeAt(cell);
        int sheltered = terrain.CoverAt(cell, MovementClass.Foot);

        Assert.True(
            before.Vegetation >= 190,
            $"the densest wood on the map has a canopy of only {before.Vegetation}/255, so \"a wood shelters\" is not " +
            $"being tested against a wood. {Woodland(terrain)}");

        // Fire, tracks and regrowth all write this word, and so does this. The surface byte is
        // deliberately left alone: the ground has changed and the terrain has not, which is the
        // whole difference between this step and the table it replaced.
        terrain.SetAttributes(cell, before.WithVegetation(0));

        int cleared = terrain.CoverAt(cell, MovementClass.Foot);
        int worthOfTheCanopy = (TerrainLayer.FootCanopyShelterPermille * before.Vegetation) / TerrainAttributes.MaxVegetation;

        string report =
            $"cell {cell} at ({cell % terrain.Size},{cell / terrain.Size}) is {surface} with canopy " +
            $"{before.Vegetation}/255 on {Named(LandformNames, before.Landform)} ground: foot cover {sheltered} with the " +
            $"wood standing and {cleared} with the canopy written to zero, a difference of {cleared - sheltered} against " +
            $"the {worthOfTheCanopy} a canopy of {before.Vegetation} is worth at {TerrainLayer.FootCanopyShelterPermille} " +
            $"permille for a closed one";

        Assert.True(terrain.TypeAt(cell) == surface, $"clearing the canopy changed the surface, which it must not: {report}");

        Assert.True(
            sheltered < TerrainLayer.NoCoverPermille,
            $"a wood with a canopy of {before.Vegetation}/255 shelters nobody at all. {report}");

        Assert.True(
            cleared > sheltered,
            $"a wood shelters exactly as much with its canopy gone, so cover is not following the vegetation field: " +
            $"{report}");

        Assert.True(
            cleared - sheltered == worthOfTheCanopy,
            $"clearing the canopy moved the cover by {cleared - sheltered} permille, not by the {worthOfTheCanopy} its " +
            $"density is worth. {report}");

        // And what it burned down to is worth what any other bare ground of the same shape is
        // worth: the surface is a base, and it is no longer what shelters anybody.
        Assert.True(
            cleared == TerrainLayer.CoverPermille(MovementClass.Foot, TerrainType.Grass, before.WithVegetation(0)),
            $"a cleared wood is not worth what bare open ground of the same shape is worth — the surface is still " +
            $"carrying cover of its own: {report}");

        Assert.Equal(1, terrain.AttributeRevision);
    }

    /// <summary>
    /// The asymmetry the old lookup table had, kept: at one canopy density, a man on foot gets
    /// more out of it than a tank does, and the two numbers are far enough apart that it is a
    /// rule rather than a rounding.
    /// <para>
    /// Checked twice, because the interesting failure is not "the table is wrong" but "the
    /// table is right in a fixture and the map never produces it": the second half measures
    /// every wooded cell the standard map actually has.
    /// </para>
    /// </summary>
    [Fact]
    public void TheSameCanopySheltersInfantryAndObstructsArmour()
    {
        int[] densities = [0, 64, 128, 192, 224, 255];
        List<string> rows = [];
        List<string> failures = [];

        foreach (int vegetation in densities)
        {
            TerrainAttributes ground = new TerrainAttributes().WithVegetation(vegetation);
            int foot = TerrainLayer.CoverPermille(MovementClass.Foot, TerrainType.Grass, ground);
            int tracked = TerrainLayer.CoverPermille(MovementClass.Tracked, TerrainType.Grass, ground);
            int wheeled = TerrainLayer.CoverPermille(MovementClass.Wheeled, TerrainType.Grass, ground);
            int air = TerrainLayer.CoverPermille(MovementClass.Air, TerrainType.Grass, ground);

            rows.Add(
                $"canopy {vegetation}/255: foot {foot}, tracked {tracked}, wheeled {wheeled}, air {air} " +
                $"(foot is {tracked - foot} better off than tracks)");

            if (vegetation > 0 && foot >= tracked)
            {
                failures.Add($"at canopy {vegetation} a man on foot gets {foot} and a tank gets {tracked}, so trees are not cover to one and an obstruction to the other");
            }

            // A canopy dense enough to be called a wood has to be worth a real difference, not
            // three permille of rounding.
            if (vegetation >= 128 && tracked - foot < 100)
            {
                failures.Add($"at canopy {vegetation} the gap between foot and tracks is only {tracked - foot} permille");
            }

            if (air != TerrainLayer.NoCoverPermille)
            {
                failures.Add($"canopy {vegetation} gives an aircraft cover of {air}: a wood is the one thing that does not shelter a plane");
            }
        }

        string table = string.Join("; ", rows);

        Assert.True(failures.Count == 0, $"{string.Join("; ", failures)} — {table}");

        // And on the map, where a wood is whatever the generator grew: every single wooded cell
        // has to have the asymmetry, not just the fixture.
        TerrainLayer terrain = Build().TerrainTypes;
        int cells = 0;
        int shelters = 0;
        int totalGap = 0;
        int narrowest = int.MaxValue;
        int emptiest = TerrainAttributes.MaxVegetation;

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            if (terrain.TypeAt(cell) != TerrainType.Forest)
            {
                continue;
            }

            int vegetation = terrain.AttributesAt(cell).Vegetation;
            int foot = terrain.CoverAt(cell, MovementClass.Foot);
            int tracked = terrain.CoverAt(cell, MovementClass.Tracked);

            cells++;
            totalGap += tracked - foot;
            narrowest = Math.Min(narrowest, tracked - foot);
            emptiest = Math.Min(emptiest, vegetation);

            if (foot < tracked)
            {
                shelters++;
            }
        }

        string woods =
            $"{cells} woodland cells on the map, canopies {emptiest}..255, mean gap between foot and tracks " +
            $"{Mean(totalGap, cells)} permille, narrowest {narrowest}";

        Assert.True(cells > 0, $"the map has no woodland at all. {woods}");
        Assert.True(shelters == cells, $"only {shelters} of {cells} wooded cells shelter infantry better than armour. {woods}");
        Assert.True(Mean(totalGap, cells) >= 100, $"the infantry advantage in a wood is only {Mean(totalGap, cells)} permille on average. {woods}");

        // The two numbers the old table was anchored on, at a closed canopy: this is the check
        // that the new scale did not quietly flatten the mechanic the table encoded.
        Assert.Equal(600, TerrainLayer.CoverPermille(MovementClass.Foot, TerrainType.Forest, ClosedCanopy()));
        Assert.Equal(880, TerrainLayer.CoverPermille(MovementClass.Tracked, TerrainType.Forest, ClosedCanopy()));
    }

    /// <summary>
    /// What the shape of the ground does, in the order this step claims: a basin hides best,
    /// then a valley, then the four shapes that say nothing about being seen, then a plateau,
    /// then a crest — which is not merely no cover but the opposite of it.
    /// <para>
    /// Held at the same surface and the same canopy at three densities, so the whole difference
    /// between the eight numbers is the landform. The four neutral shapes are asserted
    /// <em>equal</em>, not merely ordered: a pass, a shelf, a slope and a plain are neutral for
    /// reasons that are written down in <see cref="TerrainLayer"/>, and if one of them drifted
    /// apart from the others it would be a rule nobody designed.
    /// </para>
    /// </summary>
    [Fact]
    public void AValleyHidesAndACrestSkyLines()
    {
        int[] bestCoverFirst =
        [
            TerrainShape.Basin,
            TerrainShape.Valley,
            TerrainShape.Plain,
            TerrainShape.Slope,
            TerrainShape.Pass,
            TerrainShape.Shelf,
            TerrainShape.Plateau,
            TerrainShape.Ridge,
        ];

        List<string> rows = [];
        List<string> failures = [];

        foreach (int vegetation in (int[])[0, 128, 255])
        {
            int[] cover = new int[bestCoverFirst.Length];

            for (int i = 0; i < bestCoverFirst.Length; i++)
            {
                cover[i] = TerrainLayer.CoverPermille(
                    MovementClass.Foot,
                    TerrainType.Grass,
                    new TerrainAttributes().WithVegetation(vegetation).WithLandform(bestCoverFirst[i]));
            }

            rows.Add(
                $"canopy {vegetation}/255 — " +
                string.Join(", ", bestCoverFirst.Select((landform, i) => $"{Named(LandformNames, landform)} {cover[i]}")));

            // basin < valley < neutral < plateau < ridge
            if (cover[0] >= cover[1])
            {
                failures.Add($"at canopy {vegetation} a basin ({cover[0]}) does not hide better than a valley ({cover[1]})");
            }

            if (cover[1] >= cover[2])
            {
                failures.Add($"at canopy {vegetation} a valley ({cover[1]}) does not hide better than level ground ({cover[2]})");
            }

            if (cover[2] != cover[3] || cover[2] != cover[4] || cover[2] != cover[5])
            {
                failures.Add(
                    $"at canopy {vegetation} plain, slope, pass and shelf are not the same amount of cover " +
                    $"({cover[2]}, {cover[3]}, {cover[4]}, {cover[5]}), and something is claiming a direction it does not have");
            }

            if (cover[5] >= cover[6])
            {
                failures.Add($"at canopy {vegetation} a plateau ({cover[6]}) is not more exposed than level ground ({cover[5]})");
            }

            if (cover[6] >= cover[7])
            {
                failures.Add($"at canopy {vegetation} a crest ({cover[7]}) is not more exposed than a plateau ({cover[6]})");
            }
        }

        string table = string.Join("; ", rows);

        Assert.True(failures.Count == 0, $"{string.Join("; ", failures)} — {table}");

        // "The opposite of cover", which is the claim the scale has to be able to express: a
        // crest with nothing growing on it is worse than flat open ground, not equal to it.
        int bareCrest = TerrainLayer.CoverPermille(
            MovementClass.Foot,
            TerrainType.Grass,
            new TerrainAttributes().WithLandform(TerrainShape.Ridge));
        int barePlain = TerrainLayer.CoverPermille(MovementClass.Foot, TerrainType.Grass, default);

        Assert.True(
            bareCrest > TerrainLayer.NoCoverPermille,
            $"a bare crest gives {bareCrest} against {barePlain} on flat open ground, so being skylined is not a rule " +
            $"at all — it is cover that has merely been taken away. {table}");

        // And the term has to fire on the map, in both directions, rather than only in a fixture.
        TerrainLayer terrain = Build().TerrainTypes;
        int[] cellsPerLandform = new int[16];
        int better = 0;
        int same = 0;
        int worse = 0;

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            TerrainAttributes attributes = terrain.AttributesAt(cell);
            int shaped = TerrainLayer.CoverPermille(MovementClass.Foot, terrain.TypeAt(cell), attributes);
            int level = TerrainLayer.CoverPermille(
                MovementClass.Foot,
                terrain.TypeAt(cell),
                attributes.WithLandform(TerrainShape.Plain));

            cellsPerLandform[attributes.Landform]++;

            if (shaped < level)
            {
                better++;
            }
            else if (shaped > level)
            {
                worse++;
            }
            else
            {
                same++;
            }
        }

        string shapes =
            $"{terrain.CellCount} cells — " +
            string.Join(", ", Enumerable.Range(0, 16).Where(id => cellsPerLandform[id] > 0).Select(id => $"{Named(LandformNames, id)}: {cellsPerLandform[id]}")) +
            $"; the shape of the ground hides {better} of them better than level ground would, exposes {worse} of them " +
            $"and leaves {same} of them alone";

        int neutral = cellsPerLandform[TerrainShape.Plain] + cellsPerLandform[TerrainShape.Slope] +
            cellsPerLandform[TerrainShape.Pass] + cellsPerLandform[TerrainShape.Shelf];

        Assert.True(better > 0, $"no cell on the map is hidden by the shape of the ground. {shapes}");
        Assert.True(worse > 0, $"no cell on the map is exposed by the shape of the ground. {shapes}");
        Assert.True(
            same == neutral,
            $"{same} cells get the level-ground number but only {neutral} of them are on a landform that claims to be " +
            $"neutral, so a shape is carrying a term it should not. {shapes}");
    }

    /// <summary>
    /// The whole input space, every point of it: five movement classes, ten surfaces, all 256
    /// canopy densities and all sixteen values the four-bit landform field can hold, which
    /// includes the ids the schema does not define.
    /// <para>
    /// This is where an arithmetic slip is caught before it becomes "infantry are invulnerable
    /// on this one hill": the multiplier stays on the documented scale, it never reaches zero or
    /// goes negative, more canopy never means less cover, and an aircraft is never sheltered by
    /// ground it is flying above.
    /// </para>
    /// </summary>
    [Fact]
    public void EveryPointInTheInputSpaceStaysWithinTheCoverScale()
    {
        List<string> failures = [];
        int combinations = 0;
        int atTheFloor = 0;
        int lowest = int.MaxValue;
        int highest = int.MinValue;
        string lowestWhere = "nothing";
        string highestWhere = "nothing";

        foreach (MovementClass movement in Classes)
        {
            foreach (TerrainType type in Enum.GetValues<TerrainType>())
            {
                int unsheltered = TerrainLayer.CoverPermille(MovementClass.Air, type, default);

                foreach (int landform in Enumerable.Range(0, 16))
                {
                    int previous = int.MaxValue;

                    for (int vegetation = 0; vegetation <= TerrainAttributes.MaxVegetation; vegetation++)
                    {
                        TerrainAttributes ground = new TerrainAttributes()
                            .WithVegetation(vegetation)
                            .WithLandform(landform);

                        int cover = TerrainLayer.CoverPermille(movement, type, ground);
                        string where = $"{movement} on {type}, canopy {vegetation}, {Named(LandformNames, landform)}";

                        combinations++;

                        if (cover < TerrainLayer.MinCoverPermille || cover > TerrainLayer.MaxCoverPermille)
                        {
                            failures.Add($"{where}: {cover} is off the scale, which is {TerrainLayer.MinCoverPermille} to {TerrainLayer.MaxCoverPermille}");
                        }

                        if (cover <= 0)
                        {
                            failures.Add($"{where}: a multiplier of {cover} would make the target untouchable by arithmetic");
                        }

                        // More canopy can never mean less cover. A term with the sign turned
                        // round passes every spot check and fails here.
                        if (cover > previous)
                        {
                            failures.Add(
                                $"{where}: thickening the canopy from {vegetation - 1} to {vegetation} took cover away, " +
                                $"{previous} to {cover}");
                        }

                        previous = cover;

                        if (movement == MovementClass.Air && cover != unsheltered)
                        {
                            failures.Add($"{where}: an aircraft is sheltered by the ground it is flying over, {cover} against {unsheltered}");
                        }

                        if (cover < lowest)
                        {
                            lowest = cover;
                            lowestWhere = where;
                        }

                        if (cover > highest)
                        {
                            highest = cover;
                            highestWhere = where;
                        }

                        if (cover == TerrainLayer.MinCoverPermille)
                        {
                            atTheFloor++;
                        }
                    }
                }
            }
        }

        string report =
            $"{combinations} combinations of movement class, surface, canopy density and landform: cover from {lowest} " +
            $"({lowestWhere}) to {highest} ({highestWhere}), with {atTheFloor} of them resting on the floor of " +
            $"{TerrainLayer.MinCoverPermille} and none of them off the scale of {TerrainLayer.MinCoverPermille} to " +
            $"{TerrainLayer.MaxCoverPermille}";

        Assert.True(
            failures.Count == 0,
            $"{failures.Count} points of the input space are wrong, first few: {string.Join("; ", failures.Take(5))} — {report}");

        // Both ends of the scale have to be positions the input space actually holds, or the
        // clamp is a number nothing reaches and the design is a claim about nothing. That is
        // the mistake the sand band, the volcano line and the snow line each made.
        Assert.True(
            lowest == TerrainLayer.MinCoverPermille,
            $"the deepest cover the input space can produce is {lowest}, not the floor of {TerrainLayer.MinCoverPermille} " +
            $"the scale promises. {report}");

        Assert.True(
            highest == TerrainLayer.MaxCoverPermille,
            $"the least cover the input space can produce is {highest}, not the ceiling of {TerrainLayer.MaxCoverPermille} " +
            $"the scale promises. {report}");

        Assert.True(
            atTheFloor > 0,
            $"nothing reaches the floor, so the clamp is a guard on arithmetic that never happens. {report}");
    }

    /// <summary>
    /// The claim this whole step exists for, at the level the player sees it: the same unit on
    /// the same ground, under the same gun, loses less health when there is a wood on the cell
    /// than when the canopy has been written to zero.
    /// <para>
    /// Two worlds from the same seed, differing in exactly one thing — the attribute word of the
    /// one cell the defender is standing on — so the only path from that difference to the
    /// health bars is cover. The surface byte and the landform are held equal between the two
    /// runs as well, which is deliberate: the ground is the same ground, and the wood is the
    /// only thing that changed. That is what makes the ratio below an assertion about the
    /// canopy rather than about two different pieces of map.
    /// </para>
    /// </summary>
    [Fact]
    public void AUnitInAWoodTakesLessDamageThanTheSameUnitOnTheSameGroundCleared()
    {
        (int defenderCell, int attackerCell) = FightingGround();

        (int woodedCover, int woodedHealth, int woodedLost) = Exchange(wooded: true, defenderCell, attackerCell);
        (int clearedCover, int clearedHealth, int clearedLost) = Exchange(wooded: false, defenderCell, attackerCell);

        string report =
            $"the same infantryman on cell {defenderCell}, under the same tank {ExchangeTicks} ticks, in a world built " +
            $"from the same seed: with a closed canopy (cover {woodedCover}) he lost {woodedLost} health and has " +
            $"{woodedHealth} left; with the canopy written to zero (cover {clearedCover}) he lost {clearedLost} and has " +
            $"{clearedHealth} left";

        Assert.True(
            woodedHealth > 0 && clearedHealth > 0,
            $"one of the two defenders did not survive the measurement window, so there are no two numbers to compare. {report}");

        Assert.True(
            clearedLost > woodedLost,
            $"clearing the canopy made no difference to the damage at all: {report}");

        // And by the right amount: damage is linear in the multiplier, so the health lost in the
        // wood has to be the health lost in the open scaled by the ratio of the two covers.
        Assert.True(
            woodedLost * clearedCover == clearedLost * woodedCover,
            $"he lost {woodedLost} in the wood and {clearedLost} in the open, which is not the ratio of {woodedCover} to " +
            $"{clearedCover} that the cover formula predicts. {report}");
    }

    /// <summary>Ticks the exchange runs for: two shots at the catalogue's reload, both survived.</summary>
    private const int ExchangeTicks = 20;

    /// <summary>
    /// Fights one arm of the experiment: one infantryman on a cell, one tank five cells away,
    /// and the canopy of the defender's cell set by the caller.
    /// </summary>
    private static (int Cover, int Health, int Lost) Exchange(bool wooded, int defenderCell, int attackerCell)
    {
        var world = new SimWorld(Seed, capacity: 8);
        TerrainLayer terrain = world.TerrainTypes;

        // The landform is levelled in both arms, and that is the point: it removes the one term
        // that would otherwise differ between two cells of a generated map, leaving the canopy
        // as the only difference between the two runs.
        TerrainAttributes ground = terrain.AttributesAt(defenderCell).WithLandform(TerrainShape.Plain);

        terrain.SetType(defenderCell, wooded ? TerrainType.Forest : TerrainType.Grass);
        terrain.SetAttributes(
            defenderCell,
            ground.WithVegetation(wooded ? TerrainAttributes.MaxVegetation : 0));

        UnitDefinition infantry = UnitCatalog.Get(UnitKind.Infantry);
        UnitDefinition tank = UnitCatalog.Get(UnitKind.Tank);

        EntityId defender = world.Spawn(
            Faction.Western,
            2,
            UnitKind.Infantry,
            world.Navigation.CentreOf(defenderCell),
            Fix32.FromInt(infantry.SpeedMmPerTick),
            infantry.Health);

        world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Tank,
            world.Navigation.CentreOf(attackerCell),
            Fix32.FromInt(tank.SpeedMmPerTick),
            tank.Health);

        int start = world.GetRefBySlot(defender.Slot).Health;
        int cover = terrain.CoverAt(defenderCell, MovementClass.Foot);

        world.RunTicks(ExchangeTicks);

        return world.TryGet(defender, out Entity survivor)
            ? (cover, survivor.Health, start - survivor.Health)
            : (cover, 0, start);
    }

    /// <summary>
    /// Two cells of open ground five cells apart — 47 m, inside the tank's 110 m range — in a
    /// world built from the same seed the two arms are fought in. The first such pair in cell
    /// order, so the choice is deterministic and needs no seed of its own.
    /// </summary>
    private static (int Defender, int Attacker) FightingGround()
    {
        TerrainLayer terrain = new SimWorld(Seed, capacity: 8).TerrainTypes;
        const int Spacing = 5;

        for (int z = 0; z < terrain.Size; z++)
        {
            for (int x = 0; x + Spacing < terrain.Size; x++)
            {
                int defender = (z * terrain.Size) + x;

                if (terrain.TypeAt(defender) == TerrainType.Grass &&
                    terrain.TypeAt(defender + Spacing) == TerrainType.Grass)
                {
                    return (defender, defender + Spacing);
                }
            }
        }

        throw new InvalidOperationException("the standard map has no two cells of open ground five cells apart.");
    }

    /// <summary>
    /// The densest wood on ground whose landform carries no cover term of its own, so that what
    /// the test measures is the canopy and not the hill the wood happens to be standing on.
    /// </summary>
    private static int DensestWoodlandCell(TerrainLayer terrain)
    {
        int best = -1;
        int densest = -1;

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            if (terrain.TypeAt(cell) != TerrainType.Forest)
            {
                continue;
            }

            TerrainAttributes attributes = terrain.AttributesAt(cell);

            if (attributes.Landform is TerrainShape.Basin or TerrainShape.Valley or
                TerrainShape.Plateau or TerrainShape.Ridge)
            {
                continue;
            }

            if (attributes.Vegetation > densest)
            {
                densest = attributes.Vegetation;
                best = cell;
            }
        }

        return best;
    }

    /// <summary>Woodland on the map, by canopy band, for failure messages.</summary>
    private static string Woodland(TerrainLayer terrain)
    {
        int[] bands = new int[4];
        int cells = 0;
        int densest = 0;
        int emptiest = TerrainAttributes.MaxVegetation;

        for (int cell = 0; cell < terrain.CellCount; cell++)
        {
            if (terrain.TypeAt(cell) != TerrainType.Forest)
            {
                continue;
            }

            int vegetation = terrain.AttributesAt(cell).Vegetation;

            cells++;
            densest = Math.Max(densest, vegetation);
            emptiest = Math.Min(emptiest, vegetation);
            bands[Math.Min(3, vegetation / 64)]++;
        }

        return $"woodland on the map: {cells} cells, canopies {emptiest}..{densest}, bands 0-63/64-127/128-191/192-255 " +
            $"of {bands[0]}/{bands[1]}/{bands[2]}/{bands[3]}";
    }

    private static TerrainAttributes ClosedCanopy()
        => new TerrainAttributes().WithVegetation(TerrainAttributes.MaxVegetation);

    private static string Named(string[] names, int value)
        => value >= 0 && value < names.Length ? $"{names[value]} ({value})" : $"undefined ({value})";

    private static int Mean(int total, int count) => count == 0 ? 0 : total / count;

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
