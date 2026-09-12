using MiVic.Core.Numerics;
using MiVic.Core.Random;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Detection, radar coverage and the power that runs a radar.
/// <para>
/// The three are one subject. A weapon may only fire at what its side can see, a radar is
/// what lets a fixed gun see further than the pit it stands in, and a radar is a machine
/// that has to be switched on. These tests measure the chain end to end — a gun that does
/// not shoot at 190 m, the same gun shooting at 190 m with a radar behind it, and the same
/// gun going quiet again when the grid cannot run that radar — because each link on its own
/// is a number in a table and only the chain is a rule a player can learn.
/// </para>
/// <para>
/// <b>Everything here is placed along one lane, at z = -70 m.</b> That is not decoration.
/// This map has lava — which burns anything standing in it — and snow, which blinds whoever
/// stands on it, and both are scattered across the middle of the standard map. A test that
/// put a gun on a snowdrift would be measuring the weather instead of the sensor, and one
/// that put a tank in a crater would be measuring a six-damage-a-tick burn. The lane below
/// has neither, at every x. Positions are written after the spawn so the distances measured
/// are the distances asked for, exactly as the emplacement tests do.
/// </para>
/// </summary>
public sealed class DetectionTests
{
    private const ulong Seed = 20250101;

    /// <summary>The lane every test fights along: clean of lava and snow for the whole width.</summary>
    private const int Lane = -70_000;

    private static SimWorld World() => new(Seed, capacity: 64);

    private static EntityId Place(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        EntityId id = world.Spawn(
            faction, team, kind, new WorldPos(x, 0, z), Fix32.FromInt(definition.SpeedMmPerTick), definition.Health);

        ref Entity entity = ref world.GetRefBySlot(id.Slot);
        entity.Position = new WorldPos(x, 0, z);
        entity.MoveGoal = entity.Position;
        return id;
    }

    private static EntityId At(SimWorld world, Faction faction, int team, UnitKind kind, int x)
        => Place(world, faction, team, kind, x, Lane);

    private static int HealthOf(SimWorld world, EntityId id)
        => world.TryGet(id, out Entity entity) ? entity.Health : 0;

    /// <summary>
    /// A gun emplacement does not shoot at a tank inside its 200 m of gun and outside the
    /// 170 m it can see, and shoots the same tank the moment it crosses that line.
    /// <para>
    /// This is the whole of "detection is not firing range" in one measurement: the same
    /// weapon, the same tank, the same lane, and the only thing that decides it is whether
    /// anything is looking.
    /// </para>
    /// </summary>
    [Fact]
    public void AGunInsideItsFiringRangeButOutsideItsSightDoesNotFire()
    {
        SimWorld blind = World();
        EntityId blindGun = At(blind, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        EntityId far = At(blind, Faction.Western, 2, UnitKind.Tank, 190_000);

        int eyes = VisionSystem.SightRadiusMm(UnitKind.GunEmplacement);

        Assert.True(eyes < UnitCatalog.Get(UnitKind.GunEmplacement).AttackRangeMm, "the gun has no blind ground to test");
        Assert.True(190_000 > eyes, "the target is not in the gun's blind ground");

        blind.RunTicks(40);

        Assert.Equal(-1, blind.GetRefBySlot(blindGun.Slot).TargetSlot);
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, HealthOf(blind, far));

        SimWorld seen = World();
        EntityId seenGun = At(seen, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        EntityId near = At(seen, Faction.Western, 2, UnitKind.Tank, 150_000);

        seen.RunTicks(60);

        Assert.Equal(near.Slot, seen.GetRefBySlot(seenGun.Slot).TargetSlot);
        Assert.True(HealthOf(seen, near) < UnitCatalog.Get(UnitKind.Tank).Health, "the gun never fired at a target it could see");
    }

    /// <summary>
    /// A radar station standing behind a gun gives it the ground it could not see: the same
    /// shot at 190 m that was refused above is taken, because the radar is what is looking.
    /// </summary>
    [Fact]
    public void ARadarStationExtendsTheGunThatStandsUnderIt()
    {
        SimWorld world = World();

        // The gun first and the radar second, because the power ledger lights radars in
        // ascending slot order and this test is about coverage rather than about which
        // building the base can afford to run.
        EntityId gun = At(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        EntityId radar = At(world, Faction.Soviet, 0, UnitKind.RadarStation, -60_000);

        // 250 m from the radar, so the gun and the target are both inside the umbrella, and
        // 190 m from the gun: beyond its eyes, inside its gun.
        EntityId tank = At(world, Faction.Western, 2, UnitKind.Tank, 190_000);

        world.RunTicks(60);

        Assert.True(world.IsRadarLit(radar.Slot), "the base had no power for the radar this test is about");
        Assert.True(world.Radars.Covers(world, 0, new WorldPos(190_000, 0, Lane)), "the target is outside the coverage");

        Assert.Equal(tank.Slot, world.GetRefBySlot(gun.Slot).TargetSlot);
        Assert.True(HealthOf(world, tank) < UnitCatalog.Get(UnitKind.Tank).Health, "the radar did not buy the gun its reach");
    }

    /// <summary>
    /// A gun shoots what the umbrella covers and nothing it does not: with the gun standing
    /// under a radar and the target outside that radar's 260 m, the target is refused at a
    /// distance where a covered one is taken.
    /// <para>
    /// Both ends of the shot are asked about. A rule that only asked whether the <em>gun</em>
    /// was under coverage would let it reach 200 m in the one direction nobody is looking,
    /// which is the shape of a radar that lights a corner of the map and shoots across the
    /// rest of it.
    /// </para>
    /// </summary>
    [Fact]
    public void ARadarDoesNotReachPastItsOwnCoverage()
    {
        SimWorld world = World();

        // The radar 100 m behind the gun, so the gun is well inside the umbrella, and the
        // target 290 m from the radar: outside it, and inside the gun's 200 m all the same.
        At(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        EntityId radar = At(world, Faction.Soviet, 0, UnitKind.RadarStation, -100_000);
        EntityId tank = At(world, Faction.Western, 2, UnitKind.Tank, 190_000);

        world.RunTicks(60);

        Assert.True(world.IsRadarLit(radar.Slot), "the radar this test is about had no power");
        Assert.True(world.Radars.Covers(world, 0, new WorldPos(0, 0, Lane)), "the gun is not under the coverage it is supposed to have");
        Assert.False(world.Radars.Covers(world, 0, new WorldPos(190_000, 0, Lane)), "the target is not outside the coverage");

        Assert.Equal(-1, world.GetRefBySlot(0).TargetSlot);
        Assert.Equal(UnitCatalog.Get(UnitKind.Tank).Health, HealthOf(world, tank));
    }

    /// <summary>
    /// The detection disc is the sight disc at <see cref="VisionSystem.StealthDetectionPermille"/>,
    /// stamped around the same viewpoint on the same tick — which is what makes stealth a
    /// smaller radius rather than a second set of rules.
    /// <para>
    /// Measured on the grid rather than through a stalker, because a stalker that gets close
    /// enough to be found is close enough to shoot, and shooting reveals it: a behavioural
    /// test at 80 m would only ever be able to observe the reveal. The grid is where the
    /// radius actually lives, and the cells either side of the boundary are the measurement.
    /// </para>
    /// </summary>
    [Fact]
    public void TheDetectionDiscIsHalfTheSightDisc()
    {
        SimWorld world = World();
        At(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);

        world.RunTicks(VisionSystem.UpdateInterval);

        int cellAt(int metres)
            => world.Navigation.IndexOfWorld(new WorldPos(metres * WorldPos.MmPerMetre, 0, Lane));

        int eyes = VisionSystem.SightRadiusMm(UnitKind.GunEmplacement);
        int finds = (eyes * VisionSystem.StealthDetectionPermille) / 1_000;

        // Inside the detection radius: seen, and detectable. Outside it and inside the eyes:
        // seen, and not detectable — which is the gap stealth lives in.
        foreach (int metres in new[] { 20, 40, finds / WorldPos.MmPerMetre - 10 })
        {
            Assert.True(world.Visibility.IsVisible(0, cellAt(metres)), $"cell at {metres} m is not even visible");
            Assert.True(world.Visibility.IsDetected(0, cellAt(metres)), $"cell at {metres} m is not detected");
        }

        Assert.True(world.Visibility.IsVisible(0, cellAt(finds / WorldPos.MmPerMetre + 10)), "the sight disc is not bigger than the detection disc");
        Assert.False(world.Visibility.IsDetected(0, cellAt(finds / WorldPos.MmPerMetre + 10)), "the detection disc is as big as the sight disc");

        Assert.True(world.Visibility.IsVisible(0, cellAt(eyes / WorldPos.MmPerMetre - 10)), "the sight disc does not reach its own radius");
        Assert.False(world.Visibility.IsVisible(0, cellAt(eyes / WorldPos.MmPerMetre + 10)), "the sight disc reaches past its own radius");
    }

    /// <summary>
    /// What stealth buys is the first shot: a gun engages a tank at 100 m on the first tick it
    /// looks, and cannot acquire a stalker at the same distance before the stalker has fired.
    /// <para>
    /// The measurement is one tick long on purpose. After it the stalker has fired, and firing
    /// reveals it to the whole enemy team — so a longer run would be measuring the reveal and
    /// not the radius, which is the mistake this test exists to avoid.
    /// </para>
    /// </summary>
    [Fact]
    public void AStealthedUnitGetsTheFirstShot()
    {
        const int Distance = 100_000;

        SimWorld ordinary = World();
        EntityId ordinaryGun = At(ordinary, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        EntityId tank = At(ordinary, Faction.Western, 2, UnitKind.Tank, Distance);

        ordinary.Step();

        Assert.Equal(tank.Slot, ordinary.GetRefBySlot(ordinaryGun.Slot).TargetSlot);

        SimWorld stealthy = World();
        EntityId stealthyGun = At(stealthy, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        EntityId stalker = At(stealthy, Faction.Western, 2, UnitKind.StealthRecon, Distance);

        stealthy.Step();

        Assert.Equal(-1, stealthy.GetRefBySlot(stealthyGun.Slot).TargetSlot);
        Assert.True(
            stealthy.GetRefBySlot(stalker.Slot).RevealedUntilTick > stealthy.Tick,
            "the stalker had not fired, so 'the gun did not see it' was measuring a gun with nothing to shoot at");
    }

    /// <summary>
    /// A radar station finds a stealthed enemy where no gun could, and the ten metres it wins
    /// are the difference between being shot at first and shooting first.
    /// <para>
    /// The geometry is the whole argument. A Καταδρομέας reaches 120 m and a radar finds one at
    /// half of 260 m, so a stalker standing 124 m from the radar is inside its detection and
    /// outside its own reach: it has not fired, it is not going to fire yet, and it is visible
    /// anyway. The gun is placed at right angles so that the stalker is beyond its 120 m too —
    /// without that, the test could only observe the stalker's own reveal, which any enemy sees.
    /// </para>
    /// </summary>
    [Fact]
    public void ARadarFindsAStealthedUnitNoGunCould()
    {
        // 124 m from the radar: inside half of 260 m, and outside the 120 m the stalker shoots at.
        const int StalkerX = 124_000;

        SimWorld bare = World();
        EntityId bareGun = Place(bare, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, Lane - 70_000);
        EntityId bareStalker = At(bare, Faction.Western, 2, UnitKind.StealthRecon, StalkerX);

        SimWorld covered = World();
        EntityId coveredGun = Place(covered, Faction.Soviet, 0, UnitKind.GunEmplacement, 0, Lane - 70_000);
        EntityId radar = At(covered, Faction.Soviet, 0, UnitKind.RadarStation, 0);
        EntityId coveredStalker = At(covered, Faction.Western, 2, UnitKind.StealthRecon, StalkerX);

        bare.RunTicks(VisionSystem.UpdateInterval * 3);
        covered.RunTicks(VisionSystem.UpdateInterval * 3);

        int find = (VisionSystem.RadarCoverageMm * VisionSystem.StealthDetectionPermille) / 1_000;
        int reach = UnitCatalog.Get(UnitKind.StealthRecon).AttackRangeMm;

        Assert.True(find > reach, "a radar's detection has to reach past the stalker's own reach, or being detected never decides anything");
        Assert.True(StalkerX < find, "the stalker is outside the radius the radar finds one at, so this proves nothing");

        // The premise, asserted rather than assumed: the stalker is out of reach of both
        // defenders, so neither arm can be decided by a shot having been fired.
        Assert.Equal(0, bare.GetRefBySlot(bareStalker.Slot).RevealedUntilTick);
        Assert.Equal(0, covered.GetRefBySlot(coveredStalker.Slot).RevealedUntilTick);

        Assert.True(
            ((long)StalkerX * StalkerX) + (70_000L * 70_000) > (long)reach * reach,
            "the stalker is close enough to the gun to have fired at it");

        // Without a radar: invisible, and the gun has nothing.
        Assert.True(bare.IsHiddenFrom(0, bareStalker.Slot), "a gun with no radar found a stalker that never fired");
        Assert.Equal(-1, bare.GetRefBySlot(bareGun.Slot).TargetSlot);

        // With one: found, and the gun shoots at a stalker that has still not fired.
        Assert.True(covered.IsRadarLit(radar.Slot), "the radar this test is about had no power");
        Assert.False(covered.IsHiddenFrom(0, coveredStalker.Slot), "the radar did not find the stalker it was covering");
        Assert.Equal(coveredStalker.Slot, covered.GetRefBySlot(coveredGun.Slot).TargetSlot);
        Assert.Equal(0, covered.GetRefBySlot(coveredStalker.Slot).RevealedUntilTick);
    }
}

/// <summary>
/// The power ledger: what a team generates, what its structures take, and which radars are
/// therefore turning.
/// </summary>
public sealed class PowerTests
{
    private const ulong Seed = 20250101;

    /// <summary>The same clean lane the detection tests use, so no building stands in lava.</summary>
    private const int Lane = -70_000;

    private static SimWorld World() => new(Seed, capacity: 64);

    private static EntityId At(SimWorld world, int team, UnitKind kind, int x)
    {
        UnitDefinition definition = UnitCatalog.Get(kind);
        EntityId id = world.Spawn(
            Faction.Soviet, team, kind, new WorldPos(x, 0, Lane), Fix32.FromInt(definition.SpeedMmPerTick), definition.Health);

        ref Entity entity = ref world.GetRefBySlot(id.Slot);
        entity.Position = new WorldPos(x, 0, Lane);
        entity.MoveGoal = entity.Position;
        return id;
    }

    /// <summary>
    /// A bare headquarters runs one thing, which is what keeps a base that has lost its
    /// generation from being a base that cannot rebuild it.
    /// <para>
    /// A command centre on its own has six units of standby and a load of nothing, so a radar
    /// (four) is lit. A factory and a radar together are more than the standby set provides,
    /// and something has to give — and the something is the radar, because detection is the
    /// first thing shed.
    /// </para>
    /// </summary>
    [Fact]
    public void ACommandCentreOnItsOwnKeepsAMinimalBaseLit()
    {
        SimWorld bare = World();
        At(bare, 0, UnitKind.CommandCentre, 0);
        EntityId radar = At(bare, 0, UnitKind.RadarStation, 60_000);

        bare.RunTicks(2);

        Assert.Equal(PowerSystem.CommandCentreStandby, bare.Team(0).PowerGeneration);
        Assert.Equal(PowerSystem.RadarDraw, bare.Team(0).PowerDraw);
        Assert.True(bare.IsRadarLit(radar.Slot), "a headquarters with nothing else on it could not run one radar");
        Assert.Equal(0, bare.Team(0).RadarsDark);

        // And the load the standby set cannot carry is shed rather than run at a loss.
        SimWorld loaded = World();
        At(loaded, 0, UnitKind.CommandCentre, 0);
        At(loaded, 0, UnitKind.Factory, 60_000);
        EntityId dark = At(loaded, 0, UnitKind.RadarStation, 120_000);

        loaded.RunTicks(2);

        Assert.Equal(2, loaded.Team(0).PowerGeneration - loaded.Team(0).PowerDraw);
        Assert.False(loaded.IsRadarLit(dark.Slot), "the radar ran on a grid that had already spent the power");
        Assert.Equal(1, loaded.Team(0).RadarsDark);
        Assert.Equal(PowerSystem.RadarDraw - 2, loaded.Team(0).PowerShortfall);
    }

    /// <summary>
    /// A power plant is what runs a second radar: with one, the same base runs its industry and
    /// two radars; without one, the grid cannot run either.
    /// </summary>
    [Fact]
    public void APowerPlantIsWhatRunsASecondRadar()
    {
        static (int Lit, int Dark) Base(bool plant)
        {
            SimWorld world = World();
            At(world, 0, UnitKind.CommandCentre, -120_000);
            At(world, 0, UnitKind.Factory, -60_000);

            if (plant)
            {
                At(world, 0, UnitKind.PowerPlant, 0);
            }

            EntityId first = At(world, 0, UnitKind.RadarStation, 60_000);
            EntityId second = At(world, 0, UnitKind.RadarStation, 120_000);

            world.RunTicks(2);

            int lit = 0;

            if (world.IsRadarLit(first.Slot))
            {
                lit++;
            }

            if (world.IsRadarLit(second.Slot))
            {
                lit++;
            }

            return (lit, 2 - lit);
        }

        (int bareLit, int bareDark) = Base(plant: false);
        (int lit, int dark) = Base(plant: true);

        Assert.Equal(0, bareLit);
        Assert.Equal(2, bareDark);

        Assert.Equal(2, lit);
        Assert.Equal(0, dark);
    }

    /// <summary>
    /// With generation short, coverage goes: the same gun at the same distance loses the reach
    /// the radar was buying it, without anything else about the world changing.
    /// <para>
    /// This is the loop the whole system exists for. A strike on a base's generation does not
    /// merely slow its factories — take the power plant out of this base and the dish beside
    /// the gun stops, and 200 m of gun is 170 m of what it can see.
    /// </para>
    /// </summary>
    [Fact]
    public void LosingThePowerPlantCostsTheGunItsRadarReach()
    {
        static (bool Lit, int Target, int Lost) Arm(bool plant)
        {
            SimWorld world = World();
            EntityId gun = At(world, 0, UnitKind.GunEmplacement, 0);
            EntityId radar = At(world, 0, UnitKind.RadarStation, -60_000);

            // The load that decides whether the radar fits: the plant is what pays for the
            // factory, and the factory is what the radar then has to fit in behind.
            At(world, 0, UnitKind.Factory, -120_000);

            if (plant)
            {
                At(world, 0, UnitKind.PowerPlant, -180_000);
            }

            EntityId tank = At(world, 2, UnitKind.Tank, 190_000);

            world.RunTicks(60);

            UnitDefinition tankDefinition = UnitCatalog.Get(UnitKind.Tank);

            return (
                world.IsRadarLit(radar.Slot),
                world.GetRefBySlot(gun.Slot).TargetSlot,
                tankDefinition.Health - (world.TryGet(tank, out Entity entity) ? entity.Health : 0));
        }

        (bool darkLit, int darkTarget, int darkLost) = Arm(plant: false);
        (bool litLit, int litTarget, int litLost) = Arm(plant: true);

        Assert.False(darkLit, "the base this arm is about had power");
        Assert.Equal(-1, darkTarget);
        Assert.Equal(0, darkLost);

        Assert.True(litLit, "the base with a power plant could not run its radar");
        Assert.True(litTarget >= 0, "the gun never acquired the tank the radar was lighting");
        Assert.True(litLost > 0, "the gun never fired at the tank the radar was lighting");
    }

    /// <summary>
    /// The reason a dark radar is dark, in Greek, and nothing at all when it is not. It is
    /// asserted rather than left to the interface because it is a sentence a player reads: a
    /// refusal in the game's own voice is part of the rule, not decoration on it.
    /// </summary>
    [Fact]
    public void ADarkRadarSaysWhyInGreek()
    {
        SimWorld healthy = World();
        At(healthy, 0, UnitKind.CommandCentre, 0);
        At(healthy, 0, UnitKind.RadarStation, 60_000);
        healthy.RunTicks(2);

        Assert.Equal(string.Empty, PowerSystem.DimmedReason(healthy.Team(0)));

        SimWorld dimmed = World();
        At(dimmed, 0, UnitKind.CommandCentre, 0);
        At(dimmed, 0, UnitKind.Factory, 60_000);
        At(dimmed, 0, UnitKind.RadarStation, 120_000);
        dimmed.RunTicks(2);

        TeamState state = dimmed.Team(0);

        Assert.True(state.IsDimmed);
        Assert.Equal($"λείπει ισχύς {state.PowerShortfall} Ε", PowerSystem.DimmedReason(state));
        Assert.Equal("λείπει ισχύς 2 Ε", PowerSystem.DimmedReason(state));
    }

    /// <summary>
    /// Coverage is nothing without power and everything with it, and both answers come from
    /// the simulation rather than from the interface.
    /// </summary>
    [Fact]
    public void ACoveredPointIsCoveredOnlyWhileTheRadarHasPower()
    {
        SimWorld world = World();
        At(world, 0, UnitKind.CommandCentre, 0);
        EntityId radar = At(world, 0, UnitKind.RadarStation, 0);

        world.RunTicks(2);

        Assert.True(world.Radars.Covers(world, 0, new WorldPos(0, 0, Lane)));
        Assert.True(world.Radars.Covers(world, 0, new WorldPos(VisionSystem.RadarCoverageMm - 1, 0, Lane)));

        // A millimetre past the radius is covered too, and this assertion is the one that moved when
        // coverage stopped being a distance test: the fog marks whole cells, so the answer about a
        // position is the answer about the centre of the cell it stands in, and this position stands
        // in the last cell the disc paints. One navigation cell is 9.4 m, so the rim is the
        // lattice's rather than the millimetre's, and the boundary itself is pinned cell by cell in
        // CoverageQueryTests rather than by a millimetre here.
        Assert.True(
            world.Radars.Covers(world, 0, new WorldPos(VisionSystem.RadarCoverageMm + 1, 0, Lane)),
            "a position inside the last painted cell is covered, whatever the millimetres say");

        // Not covered: well outside the radius, and not covered by the *other* team either.
        Assert.False(world.Radars.Covers(world, 0, new WorldPos(VisionSystem.RadarCoverageMm + 20_000, 0, Lane)));
        Assert.False(world.Radars.Covers(world, 2, new WorldPos(0, 0, Lane)));

        // What takes it away is the load, and the load is shed rather than run at a loss.
        At(world, 0, UnitKind.Factory, 60_000);
        world.RunTicks(2);

        Assert.False(world.IsRadarLit(radar.Slot));
        Assert.False(world.Radars.Covers(world, 0, new WorldPos(0, 0, Lane)), "a radar with no power still painted the ground");
    }
}

/// <summary>
/// The sensor chain has to be as reproducible as everything else: two machines that agree
/// about the world must agree about who can see whom, and about who they then shoot.
/// </summary>
public sealed class DetectionDeterminismTests
{
    private const int Lane = -70_000;

    /// <summary>Builds the same line-up from a seed, and reports what came of it.</summary>
    private static (int HiddenStalker, int GunTarget, ulong Rng) Resolve(ulong seed)
    {
        SimWorld world = new(seed, capacity: 64);

        static EntityId At(SimWorld world, Faction faction, int team, UnitKind kind, int x)
        {
            UnitDefinition definition = UnitCatalog.Get(kind);
            EntityId id = world.Spawn(
                faction, team, kind, new WorldPos(x, 0, Lane), Fix32.FromInt(definition.SpeedMmPerTick), definition.Health);

            ref Entity entity = ref world.GetRefBySlot(id.Slot);
            entity.Position = new WorldPos(x, 0, Lane);
            entity.MoveGoal = entity.Position;
            return id;
        }

        EntityId gun = At(world, Faction.Soviet, 0, UnitKind.GunEmplacement, 0);
        At(world, Faction.Soviet, 0, UnitKind.RadarStation, -60_000);
        EntityId tank = At(world, Faction.Western, 2, UnitKind.Tank, 190_000);
        EntityId stalker = At(world, Faction.Western, 2, UnitKind.StealthRecon, 124_000);

        ulong rng = world.Rng.State;
        world.RunTicks(60);

        return (world.IsHiddenFrom(0, stalker.Slot) ? 1 : 0, world.GetRefBySlot(gun.Slot).TargetSlot == tank.Slot ? 1 : 0, rng);
    }

    /// <summary>
    /// The same world sees the same things and picks the same targets whatever the seed —
    /// which also says that none of the sensor chain draws a random number, because two
    /// different seeds agree about both answers.
    /// </summary>
    [Fact]
    public void TheSensorChainDoesNotDependOnTheSeed()
    {
        (int hidden, int target, ulong rng) = Resolve(20250101);
        (int hiddenAgain, int targetAgain, ulong rngAgain) = Resolve(20250101);
        (int otherHidden, int otherTarget, _) = Resolve(7);
        (int thirdHidden, int thirdTarget, _) = Resolve(999_001);

        Assert.Equal(hidden, hiddenAgain);
        Assert.Equal(target, targetAgain);
        Assert.Equal(rng, rngAgain);

        Assert.Equal(hidden, otherHidden);
        Assert.Equal(target, otherTarget);
        Assert.Equal(hidden, thirdHidden);
        Assert.Equal(target, thirdTarget);

        // The scenario has to be one where the answers are not all the same, or agreeing
        // about them proves nothing about the chain.
        Assert.Equal(1, target);
    }

    /// <summary>
    /// And the whole sensor chain leaves the world's own random source untouched, which is what
    /// lets two machines agree about a firefight without agreeing about anything else.
    /// </summary>
    [Fact]
    public void SeeingAndShootingDrawNoRandomness()
    {
        (_, _, ulong first) = Resolve(4242);
        (_, _, ulong second) = Resolve(4242);

        Assert.Equal(first, second);
        Assert.Equal(new Pcg32(4242).State, first);
    }
}
