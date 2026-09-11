using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The AI's defensive line: what it builds, where it puts it, and the two things it refuses to
/// do with the army.
/// <para>
/// The player could always raise a Πυροβολείο, an Αντιαεροπορικό Πυροβολείο and a Σταθμός
/// Ραντάρ; the opponent did not know any of them existed. It built the same economy in the same
/// order, its guns therefore never reached past 170 m, it never shed anything because it never
/// drew anything, and it walked a force at a prepared position exactly as it walked one at an
/// empty field. These are the tests that say it does now — and that it does so without making
/// itself worse, which is the failure mode a structure with a running cost can create.
/// </para>
/// <para>
/// The worlds are built rather than borrowed from the skirmish: a base with an economy and no
/// enemy within four hundred metres is the only way to ask what the AI builds without the answer
/// being a fact about somebody else's tanks. The real match is what the probe transcript is for.
/// </para>
/// </summary>
public sealed class AiDefenceTests
{
    private const ulong Seed = 20250101;

    /// <summary>
    /// Ground the map will hold a base on, found by asking rather than by knowing: the middle of
    /// the map is water on this seed, which is the whole reason <see cref="SimWorld.TryFindBaseSite"/>
    /// exists.
    /// </summary>
    private static WorldPos BaseGround(SimWorld world)
        => world.TryFindBaseSite(WorldPos.Origin, out WorldPos site)
            ? site
            : world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

    private static WorldPos Offset(WorldPos centre, int dx, int dz)
        => new(centre.X + dx, centre.Y, centre.Z + dz);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, WorldPos position)
        => world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(position),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            UnitCatalog.Get(kind).Health);

    /// <summary>
    /// A base with the economy the skirmish starts every faction with — a headquarters, a power
    /// plant, a factory and a design bureau — and enough material to pay for what it decides to
    /// build. The four buildings are the real ledger: generation, load, and a yard that can raise
    /// a structure of any role.
    /// </summary>
    private static SimWorld Base(int team, Faction faction)
    {
        SimWorld world = new(Seed, capacity: 256);
        WorldPos home = BaseGround(world);

        Spawn(world, faction, team, UnitKind.CommandCentre, home);
        Spawn(world, faction, team, UnitKind.PowerPlant, Offset(home, 45_000, 45_000));
        Spawn(world, faction, team, UnitKind.Factory, Offset(home, -45_000, 45_000));
        Spawn(world, faction, team, UnitKind.DesignBureau, Offset(home, 45_000, -45_000));

        ref TeamState state = ref world.TeamRef(team);
        state.Materials = 50_000;
        state.Energy = 50_000;
        state.Water = 50_000;

        return world;
    }

    /// <summary>
    /// A headquarters, a given number of combat units, and no material at all — so the AI cannot
    /// pay for a structure or another unit and the only thing left for it to decide is what the
    /// army does. Everything these tests ask about the army is asked here.
    /// </summary>
    private static SimWorld Army(int team, Faction faction, int units, out EntityId headquarters)
    {
        SimWorld world = new(Seed, capacity: 256);
        WorldPos home = BaseGround(world);

        headquarters = Spawn(world, faction, team, UnitKind.CommandCentre, home);

        for (int i = 0; i < units; i++)
        {
            // Ranked on one side of the headquarters, so that where the army stands is a fact
            // the test chose rather than one the formation happened to produce.
            int column = i % 4;
            int row = i / 4;

            Spawn(world, faction, team, UnitKind.Tank, Offset(home, -60_000 + (column * 8_000), -60_000 + (row * 8_000)));
        }

        world.TeamRef(team).Materials = 0;
        return world;
    }

    private static int CountKind(SimWorld world, int team, UnitKind kind)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == kind)
            {
                count++;
            }
        }

        return count;
    }

    private static int FindFirst(SimWorld world, int team, UnitKind kind)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId == team && entity.Kind == kind)
                {
                    return slot;
                }
            }
        }

        return -1;
    }

    /// <summary>Every structure a team has, as role and position, in ascending slot order.</summary>
    private static List<(UnitKind Kind, WorldPos Position)> Structures(SimWorld world, int team)
    {
        var found = new List<(UnitKind, WorldPos)>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                found.Add((entity.Kind, entity.Position));
            }
        }

        return found;
    }

    /// <summary>The slots the team's units have been ordered to attack, with no duplicates.</summary>
    private static List<int> AttackOrderTargets(SimWorld world, int team)
    {
        var targets = new List<int>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity unit = ref world.GetRefBySlot(slot);

            if (unit.TeamId == team && unit.HasAttackOrder && !targets.Contains(unit.TargetSlot))
            {
                targets.Add(unit.TargetSlot);
            }
        }

        return targets;
    }

    /// <summary>Where everything of a team is standing, in ascending slot order.</summary>
    private static List<WorldPos> Positions(SimWorld world, int team)
    {
        var found = new List<WorldPos>();

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).TeamId == team)
            {
                found.Add(world.GetRefBySlot(slot).Position);
            }
        }

        return found;
    }

    /// <summary>
    /// Ground the map will actually hold a unit on, at a distance from a point that the test
    /// chose, and clear of everything the test named.
    /// <para>
    /// The distances in the stealth test are the whole question — 120 m is a Καταδρομέας's weapon,
    /// 150 m is a headquarters' eyes and half of that is what finds him — and a spawn the terrain
    /// moves is a spawn at a distance nobody asked for, which is exactly what happened the first
    /// time this test was written: the spot 120 m east of the base was water, the map moved the man
    /// to 108 m, and he opened fire.
    /// </para>
    /// </summary>
    private static WorldPos OpenGround(
        SimWorld world,
        WorldPos centre,
        int minMm,
        int maxMm,
        ReadOnlySpan<WorldPos> avoid,
        int clearMm)
    {
        int cell = world.Navigation.IndexOfWorld(centre);
        int centreX = world.Navigation.CellX(cell);
        int centreZ = world.Navigation.CellZ(cell);
        int minCells = minMm / world.Navigation.CellSizeMm;
        int maxCells = maxMm / world.Navigation.CellSizeMm;

        for (int radius = minCells; radius <= maxCells; radius++)
        {
            for (int dz = -radius; dz <= radius; dz++)
            {
                for (int dx = -radius; dx <= radius; dx++)
                {
                    // The ring only: everything inside it was searched already.
                    if (Math.Abs(dx) != radius && Math.Abs(dz) != radius)
                    {
                        continue;
                    }

                    int index = world.Navigation.IndexOf(centreX + dx, centreZ + dz);

                    if (index < 0 || !world.Navigation.IsWalkable(index))
                    {
                        continue;
                    }

                    WorldPos candidate = world.Navigation.CentreOf(index);
                    int distance = candidate.HorizontalDistanceTo(centre);

                    if (distance < minMm || distance > maxMm || world.LegalSpawnSite(candidate) != candidate)
                    {
                        continue;
                    }

                    bool clear = true;

                    foreach (WorldPos point in avoid)
                    {
                        if (candidate.HorizontalDistanceTo(point) < clearMm)
                        {
                            clear = false;
                            break;
                        }
                    }

                    if (clear)
                    {
                        return candidate;
                    }
                }
            }
        }

        return default;
    }

    // ------------------------------------------------------------------ what it builds, and when

    /// <summary>
    /// The AI raises an emplacement and a radar — the two buildings whose absence is what "the
    /// opponent does not know defence exists" meant. The radar is the load-bearing half of this:
    /// a Πυροβολείο sees 170 m and shoots 200, so an AI with guns and no radar has bought the
    /// weaker half of every gun it owns.
    /// </summary>
    [Fact]
    public void TheAiRaisesAnEmplacementAndARadar()
    {
        SimWorld world = Base(1, Faction.Chinese);

        world.RunTicks(1_200);

        int guns = CountKind(world, 1, UnitKind.GunEmplacement);
        int radars = CountKind(world, 1, UnitKind.RadarStation);

        Assert.True(guns >= 1, "The AI never raised a gun emplacement.");
        Assert.True(radars >= 1, "The AI never raised a radar station.");

        // Up rather than merely ordered: a building site does nothing, and this is a question
        // about what is standing at the end of a minute.
        Assert.Equal(0, world.GetRefBySlot(FindFirst(world, 1, UnitKind.GunEmplacement)).ConstructionTicksRemaining);
        Assert.Equal(0, world.GetRefBySlot(FindFirst(world, 1, UnitKind.RadarStation)).ConstructionTicksRemaining);
    }

    /// <summary>
    /// Every structure the AI raises stands where the player's own click would be accepted. The
    /// AI chooses a site; it does not get to place better than a player, and the only way to be
    /// sure of that is to ask the rule the click is judged by about every building it owns.
    /// </summary>
    [Fact]
    public void EveryStructureTheAiChoosesStandsWhereTheRuleAllows()
    {
        SimWorld world = Base(1, Faction.Chinese);

        world.RunTicks(1_200);

        List<(UnitKind Kind, WorldPos Position)> structures = Structures(world, 1);

        Assert.True(structures.Count > 4, "the AI raised nothing, so this test proves nothing");

        foreach ((UnitKind kind, WorldPos position) in structures)
        {
            Assert.True(
                world.CanPlaceStructure(kind, position, out string reason),
                $"{kind} at ({position.X / 1000}, {position.Z / 1000}) m: {reason}");
        }
    }

    /// <summary>
    /// The line is chosen to cover what is worth attacking: each emplacement is inside its own
    /// reach of the base, which is to say it was picked for the ground it commands rather than for
    /// being an offset from whatever built it. A Πυροβολείο placed anywhere else is a building
    /// with a gun that never covers the thing it was bought for.
    /// </summary>
    [Fact]
    public void TheAiStandsItsGunsWhereTheyCoverTheBase()
    {
        SimWorld world = Base(1, Faction.Chinese);
        int headquarters = FindFirst(world, 1, UnitKind.CommandCentre);
        WorldPos home = world.GetRefBySlot(headquarters).Position;

        world.RunTicks(1_200);

        int reach = UnitCatalog.Get(UnitKind.GunEmplacement).AttackRangeMm;
        int seen = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != 1 || entity.Kind != UnitKind.GunEmplacement)
            {
                continue;
            }

            seen++;
            Assert.True(
                entity.Position.DistanceSquaredTo(home) <= (long)reach * reach,
                $"the emplacement stands {entity.Position.HorizontalDistanceTo(home) / 1000} m from the base "
                + $"and reaches {reach / 1000} m, so it covers nothing it was bought for");
        }

        Assert.True(seen > 0, "the AI raised no emplacement to check");
    }

    /// <summary>
    /// The ground is part of the choice, and this is the test that the AI reads it rather than
    /// pretending to. Cover is planted on a neighbourhood that had none — closed canopy in a basin,
    /// which is the most protective ground this engine can express — and the emplacement that comes
    /// up is asserted to stand on ground that takes damage off a shot at it.
    /// <para>
    /// The counterfactual is what makes the assertion mean something: an AI that ignored the ground
    /// would take the first cell it looked at, which is the top-left corner of its search and, in
    /// this world, plain ground at 1000 ‰ — every shot lands. The planted ground is also asserted to
    /// be placeable, so the site was there to be taken rather than being a coincidence of the map.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiStandsItsGunsOnGroundWorthStandingOn()
    {
        SimWorld world = Base(1, Faction.Chinese);
        int headquarters = FindFirst(world, 1, UnitKind.CommandCentre);
        WorldPos home = world.GetRefBySlot(headquarters).Position;

        int reach = UnitCatalog.Get(UnitKind.GunEmplacement).AttackRangeMm;
        int reachCells = reach / world.Navigation.CellSizeMm;
        int homeCell = world.Navigation.IndexOfWorld(home);
        int homeX = world.Navigation.CellX(homeCell);
        int homeZ = world.Navigation.CellZ(homeCell);

        // A block of ground to the east of the base, planted as the best cover there is.
        int planted = 0;
        int placeable = 0;

        for (int dz = -6; dz <= 6; dz++)
        {
            for (int dx = 6; dx <= 12; dx++)
            {
                int cell = world.Navigation.IndexOf(homeX + dx, homeZ + dz);

                if (cell < 0 || !world.CanPlaceStructure(UnitKind.GunEmplacement, world.Navigation.CentreOf(cell), out _))
                {
                    continue;
                }

                TerrainAttributes attributes = world.TerrainTypes.AttributesAt(cell);
                world.TerrainTypes.SetAttributes(
                    cell,
                    attributes.WithVegetation(TerrainAttributes.MaxVegetation).WithLandform(TerrainShape.Basin));

                planted++;
                placeable += world.IsSiteClear(UnitKind.GunEmplacement, world.Navigation.CentreOf(cell), out _) ? 1 : 0;
            }
        }

        Assert.True(planted > 0, "no ground east of the base would take an emplacement, so nothing was planted");
        Assert.True(placeable > 0, "every planted cell was occupied, so the AI could not have chosen one");
        Assert.True(reachCells > 12, "the planted block is outside the emplacement's own reach");

        world.RunTicks(1_200);

        int gun = FindFirst(world, 1, UnitKind.GunEmplacement);
        Assert.True(gun >= 0, "the AI raised no emplacement.");

        ref Entity emplacement = ref world.GetRefBySlot(gun);
        int cover = world.TerrainTypes.CoverAt(
            world.TerrainTypes.IndexOfWorld(emplacement.Position.X, emplacement.Position.Z),
            UnitCatalog.Get(UnitKind.GunEmplacement).Movement);

        Assert.True(
            cover < TerrainLayer.NoCoverPermille,
            $"the emplacement stands on ground worth {cover} ‰, which is no cover at all — the AI did not "
            + "choose the ground it was offered");
    }

    /// <summary>
    /// The AI does not brown itself out. A radar is the only structure in the game whose price is
    /// not its whole cost: it occupies generation for as long as it is on the air, and the ledger
    /// sheds detection before it sheds anything else — so an AI that adds a dish to a base that
    /// cannot run it has paid 240 Π for a building that switches itself off, which is a way for
    /// this feature to make the opponent *worse* than one that never built it.
    /// <para>
    /// The base here is one load short on purpose: three factories and a bureau against a single
    /// power plant is 15 against 16, so there is no room for a radar's 4. The AI has to raise the
    /// generation first, and the test watches the ledger on every tick from the moment the dish is
    /// up rather than only at the end.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiBuildsTheGenerationForItsRadarRatherThanSheddingIt()
    {
        SimWorld world = Base(1, Faction.Chinese);
        WorldPos home = world.GetRefBySlot(FindFirst(world, 1, UnitKind.CommandCentre)).Position;

        // Two more factories: 15 of load against the 16 the base generates.
        Spawn(world, Faction.Chinese, 1, UnitKind.Factory, Offset(home, 0, 90_000));
        Spawn(world, Faction.Chinese, 1, UnitKind.Factory, Offset(home, 0, -90_000));

        Assert.False(
            PowerSystem.HasRoomForRadar(world, 1),
            "the base can already run a radar, so nothing here tests the generation rule");

        int darkTicks = 0;
        int litTicks = 0;

        for (int tick = 0; tick < 3_000; tick++)
        {
            world.Step();

            if (world.Team(1).RadarsDark > 0)
            {
                darkTicks++;
            }

            if (world.Team(1).RadarsLit > 0)
            {
                litTicks++;
            }
        }

        Assert.True(CountKind(world, 1, UnitKind.PowerPlant) >= 2, "the AI never built the generation its radar needed");
        Assert.True(CountKind(world, 1, UnitKind.RadarStation) >= 1, "the AI never raised a radar at all");

        int radar = FindFirst(world, 1, UnitKind.RadarStation);
        Assert.Equal(0, world.GetRefBySlot(radar).ConstructionTicksRemaining);

        Assert.Equal(0, darkTicks);
        Assert.True(litTicks > 0, "the radar was up but never on the air");
        Assert.True(world.IsRadarLit(radar), "the AI's radar is dark at the end of the run");

        // And the ledger is solvent, which is the thing a radar is at risk of breaking.
        Assert.True(
            world.Team(1).PowerGeneration >= world.Team(1).PowerDraw,
            $"generation {world.Team(1).PowerGeneration} against a load of {world.Team(1).PowerDraw}");
    }

    /// <summary>
    /// And a radar that has been put out is put back on. The ledger sheds detection first, so a
    /// strike on a base's generation — which is what a raid on a power plant is for — takes the
    /// dishes off the air and drops every emplacement back to its own 170 m of eyes. An AI that did
    /// not notice would keep a line whose guns had quietly lost 30 m of reach, which is the loss this
    /// half of the rule exists to make temporary rather than permanent.
    /// </summary>
    [Fact]
    public void TheAiRebuildsTheGenerationItsRadarLost()
    {
        SimWorld world = Base(1, Faction.Chinese);

        world.RunTicks(600);

        int radar = FindFirst(world, 1, UnitKind.RadarStation);
        Assert.True(radar >= 0, "the AI never raised a radar to lose");
        Assert.True(world.IsRadarLit(radar), "the radar was never lit, so there is nothing to restore");

        // The plant goes, as a raid would take it: 16 generation becomes 6 against a load of 11.
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot) && world.GetRefBySlot(slot).TeamId == 1 &&
                world.GetRefBySlot(slot).Kind == UnitKind.PowerPlant)
            {
                world.Despawn(new EntityId(slot, world.GetRefBySlot(slot).Generation));
            }
        }

        Assert.Equal(0, CountKind(world, 1, UnitKind.PowerPlant));

        world.RunTicks(1);
        Assert.True(world.Team(1).RadarsDark > 0, "the radar is still lit with no generation, so the ledger is wrong");

        world.RunTicks(600);

        Assert.True(CountKind(world, 1, UnitKind.PowerPlant) >= 1, "the AI never rebuilt its generation");
        Assert.Equal(0, world.Team(1).RadarsDark);
        Assert.True(world.IsRadarLit(FindFirst(world, 1, UnitKind.RadarStation)), "the radar is still dark");
    }

    /// <summary>
    /// Same seed, same line. The AI is part of the deterministic simulation: it reads no clock and
    /// no random source, so two runs of the same world must choose the same sites for the same
    /// roles on the same ticks, and end in the same state.
    /// </summary>
    [Fact]
    public void TheAiRaisesTheSameLineInTheSamePlacesEveryRun()
    {
        static (List<(UnitKind Kind, WorldPos Position)> Structures, ulong Hash) Run()
        {
            SimWorld world = Base(1, Faction.Chinese);
            world.RunTicks(1_200);
            return (Structures(world, 1), StateHash.Compute(world));
        }

        (List<(UnitKind Kind, WorldPos Position)> first, ulong firstHash) = Run();
        (List<(UnitKind Kind, WorldPos Position)> second, ulong secondHash) = Run();

        Assert.Equal(first, second);
        Assert.Equal(firstHash, secondHash);

        // And it did build the thing this test is about in both runs.
        Assert.Contains(first, s => s.Kind == UnitKind.GunEmplacement);
        Assert.Contains(first, s => s.Kind == UnitKind.RadarStation);
    }

    // ------------------------------------------------------------------ looking before it moves

    /// <summary>
    /// An enemy the AI can see standing on the AI's own ground is attacked. This is the other half
    /// of the rule the next test is about — a raid inside the reach of its own line is the thing
    /// the line exists to answer — and without it "it does not attack what it cannot see" would be
    /// satisfied by an AI that attacks nothing at all.
    /// </summary>
    [Fact]
    public void TheAiSendsItsArmyAtAVisibleRaiderOnItsOwnGround()
    {
        SimWorld world = Army(1, Faction.Chinese, AiSystem.AttackArmySize, out EntityId headquarters);
        WorldPos home = world.GetRefBySlot(headquarters.Slot).Position;

        EntityId raider = Spawn(world, Faction.Western, 2, UnitKind.Tank, Offset(home, 60_000, 0));

        world.RunTicks(60);

        Assert.Contains(raider.Slot, AttackOrderTargets(world, 1));
    }

    /// <summary>
    /// And it does not attack what it cannot see. A Καταδρομέας inside the AI's own defence radius,
    /// on ground the AI's own headquarters is watching, is not a target: it is invisible, nothing of
    /// the team's has detected it, and an order against it would send a company of tanks to a patch
    /// of empty ground — a unit that drives somewhere and does nothing, which reads as a bug rather
    /// than as a decision.
    /// <para>
    /// Both halves of that are asserted, because either one alone proves nothing: the man is hidden
    /// from the team (so it is detection and not the fog that stops the order) and the cell he stands
    /// on is *visible* to the team (so the fog is not doing the work). The order that appears once
    /// something finds him is the next test.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiDoesNotOrderAnAttackOnAUnitItCannotSee()
    {
        SimWorld world = Army(1, Faction.Chinese, AiSystem.AttackArmySize, out EntityId headquarters);
        WorldPos home = world.GetRefBySlot(headquarters.Slot).Position;

        // Inside the headquarters' own 150 m of sight, outside the 75 m its sensors find a hidden
        // man at, and outside the reach of the man's own weapon — a stalker that can shoot something
        // reveals himself by doing it, and then this is a test about firing.
        List<WorldPos> clear = Positions(world, 1);
        clear.Add(new WorldPos(home.X - AiSystem.RallyDistanceMm, 0, home.Z + AiSystem.RallyDistanceMm));

        WorldPos lair = OpenGround(world, home, 130_000, 148_000, clear.ToArray(), 130_000);

        Assert.NotEqual(default, lair);

        EntityId stalker = Spawn(world, Faction.Western, 2, UnitKind.StealthRecon, lair);

        world.RunTicks(120);

        ref Entity hidden = ref world.GetRefBySlot(stalker.Slot);
        int cell = world.TerrainTypes.IndexOfWorld(hidden.Position.X, hidden.Position.Z);

        Assert.True(world.IsHiddenFrom(1, stalker.Slot), "the stalker is not hidden, so this test proves nothing");
        Assert.True(world.Visibility.IsVisible(1, cell), "the ground is not even watched, so the fog is what refused");
        Assert.True(hidden.RevealedUntilTick <= world.Tick, "the stalker gave itself away by firing, so this is not stealth");

        Assert.DoesNotContain(stalker.Slot, AttackOrderTargets(world, 1));
    }

    /// <summary>
    /// The other side of the same rule: once something finds him, he is a target.
    /// <para>
    /// What finds him here is a Σταθμός Ραντάρ, and the distance is the design: a radar's coverage
    /// is 260 m and half of that is what detects, which is 130 m — ten metres past the 120 m a
    /// Καταδρομέας's own weapon reaches. So the man is found by the dish and never fires a shot, and
    /// the test asserts exactly that (<see cref="Entity.RevealedUntilTick"/> has not been set),
    /// because a stalker that has given itself away is visible to anybody and would prove nothing
    /// about detection.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiAttacksAHiddenEnemyOnceItHasBeenDetected()
    {
        SimWorld world = Army(1, Faction.Chinese, AiSystem.AttackArmySize, out EntityId headquarters);
        WorldPos home = world.GetRefBySlot(headquarters.Slot).Position;

        List<WorldPos> clear = Positions(world, 1);
        clear.Add(new WorldPos(home.X - AiSystem.RallyDistanceMm, 0, home.Z + AiSystem.RallyDistanceMm));

        WorldPos lair = OpenGround(world, home, 130_000, 148_000, clear.ToArray(), 130_000);

        Assert.NotEqual(default, lair);

        EntityId stalker = Spawn(world, Faction.Western, 2, UnitKind.StealthRecon, lair);

        // A radar ten metres outside the stalker's reach: close enough for its half-radius detection
        // disc, too far for the man to shoot it, and unarmed — so nothing here can reveal him.
        WorldPos post = OpenGround(world, world.GetRefBySlot(stalker.Slot).Position, 122_000, 128_000, [], 0);

        Assert.NotEqual(default, post);

        EntityId radar = Spawn(world, Faction.Chinese, 1, UnitKind.RadarStation, post);
        world.TeamRef(1).Materials = 0;

        world.RunTicks(60);

        Assert.True(world.IsRadarLit(radar.Slot), "the radar is not on the air, so there is no detection to test");
        Assert.False(world.IsHiddenFrom(1, stalker.Slot), "the radar never found the stalker");
        Assert.True(
            world.GetRefBySlot(stalker.Slot).RevealedUntilTick <= world.Tick,
            "the stalker revealed himself by firing, so this is a firing reveal and not detection");

        Assert.Contains(stalker.Slot, AttackOrderTargets(world, 1));
    }

    /// <summary>
    /// The AI does not assault a position its force cannot answer. Two enemy emplacements covering
    /// an enemy headquarters mean four more units than the ordinary attacking force, and the AI's
    /// army — exactly the ordinary attacking force — is not sent: it holds at its rally point
    /// instead of feeding itself into the guns one decision at a time.
    /// <para>
    /// The rule is a preference for ground that is not covered and a floor under the force that
    /// goes onto ground that is; what it is not is a permanent refusal, which is why the second
    /// half of this test grows the army and expects the attack to be ordered.
    /// </para>
    /// </summary>
    [Fact]
    public void TheAiHoldsBackFromADefendedPositionUntilItHasTheForceForIt()
    {
        SimWorld world = Army(1, Faction.Chinese, AiSystem.AttackArmySize, out EntityId headquarters);
        WorldPos home = world.GetRefBySlot(headquarters.Slot).Position;

        WorldPos enemy = Offset(home, 300_000, 0);
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, enemy);
        Spawn(world, Faction.Western, 2, UnitKind.GunEmplacement, Offset(enemy, 0, 50_000));
        Spawn(world, Faction.Western, 2, UnitKind.GunEmplacement, Offset(enemy, 0, -50_000));

        world.RunTicks(60);

        Assert.Equal(AiSystem.AttackArmySize, AiSystem.RequiredArmyAgainst(0));
        Assert.Equal(AiSystem.AttackArmySize + 4, AiSystem.RequiredArmyAgainst(2));
        Assert.Empty(AttackOrderTargets(world, 1));

        // Four more tanks, and the position is one the force can answer.
        for (int i = 0; i < 4; i++)
        {
            Spawn(world, Faction.Chinese, 1, UnitKind.Tank, Offset(home, -40_000 - (i * 8_000), 40_000));
        }

        world.RunTicks(60);

        List<int> targets = AttackOrderTargets(world, 1);

        Assert.NotEmpty(targets);
        Assert.All(targets, slot => Assert.Equal(2, world.GetRefBySlot(slot).TeamId));
    }

    /// <summary>
    /// Given a headquarters behind two guns and a power plant behind none, the AI takes the power
    /// plant. That is the whole of "a preference for targets that are not covered": the point of the
    /// line the player built is that it changes where the opponent goes, and an AI that walks at the
    /// nearest building regardless has not noticed that anything was built.
    /// </summary>
    [Fact]
    public void TheAiPrefersThePositionThatIsNotCovered()
    {
        SimWorld world = Army(1, Faction.Chinese, AiSystem.AttackArmySize, out EntityId headquarters);
        WorldPos home = world.GetRefBySlot(headquarters.Slot).Position;

        WorldPos defended = Offset(home, 250_000, 0);
        EntityId enemyHeadquarters = Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, defended);
        Spawn(world, Faction.Western, 2, UnitKind.GunEmplacement, Offset(defended, 0, 50_000));
        Spawn(world, Faction.Western, 2, UnitKind.GunEmplacement, Offset(defended, 0, -50_000));

        WorldPos exposed = Offset(home, -250_000, 0);
        EntityId enemyPlant = Spawn(world, Faction.Western, 2, UnitKind.PowerPlant, exposed);

        world.RunTicks(60);

        List<int> targets = AttackOrderTargets(world, 1);

        Assert.Equal([enemyPlant.Slot], targets);
        Assert.DoesNotContain(enemyHeadquarters.Slot, targets);
    }
}
