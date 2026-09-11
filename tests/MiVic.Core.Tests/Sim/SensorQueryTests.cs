using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// "Does this entity sense anything, how far, and if not, why not" is one question with one answer.
/// <para>
/// The fog is stamped from the entities that sense and from no others, and which entities those are
/// is decided in exactly one loop. Anything that has to draw what a side can see — the map's
/// coverage layer is the only caller today — has to reverse the entities that loop skips, and
/// reversing a rule by hand is keeping a second copy of it. <see cref="VisionSystem.SensorOf"/> is
/// the rule, in the place that owns it, and <see cref="VisionSystem.Tick"/> runs through it.
/// </para>
/// <para>
/// <b>So these tests pin agreement rather than the rule.</b> Each case asserts the two answers that
/// have to be the same answer — what the query says about one entity, and whether team 0 currently
/// has eyes on the ground that entity stands on — because the failure being guarded against is not
/// "the radius is wrong", it is "the query and the fog disagree". The three cases are the two the
/// stamp loop skips and the ordinary one it does not: a structure under construction, a radar
/// station the grid has shed, and an armed structure standing finished. Everything is placed on the
/// clean lane <see cref="DetectionTests"/> uses, so no case is measuring the weather.
/// </para>
/// </summary>
public sealed class SensorQueryTests
{
    private const ulong Seed = 20250101;

    /// <summary>The lane the sensor tests work along: no lava and no snow anywhere on it.</summary>
    private const int Lane = -70_000;

    /// <summary>
    /// Ticks run before the two answers are compared: more than the ten the visibility stagger takes
    /// to reach every slot, and inside the eleven a stamped cell stays visible for, so the fog has
    /// had its say about every entity in the world by the time it is asked.
    /// </summary>
    private const int Ticks = 20;

    private static SimWorld World() => new(Seed, capacity: 64);

    /// <summary>
    /// One entity on the lane at one x. The position is written after the spawn, as the detection
    /// tests do, so the distance measured is the distance asked for.
    /// </summary>
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

    /// <summary>Whether team 0 currently has eyes on the cell the entity stands on — the fog's answer.</summary>
    private static bool TheFogHasEyesOn(SimWorld world, EntityId id)
    {
        ref Entity entity = ref world.GetRefBySlot(id.Slot);
        int cell = world.Navigation.IndexOfWorld(entity.Position);

        return cell >= 0 && world.Visibility.IsVisible(entity.TeamId, cell);
    }

    /// <summary>The query's answer about one entity, as the radius it watches to.</summary>
    private static int Watches(SimWorld world, EntityId id, out SensorRefusal refusal)
        => VisionSystem.SensorOf(world, id.Slot, out refusal);

    /// <summary>
    /// An armed structure standing finished senses, and the fog has the ground it says it watches.
    /// <para>
    /// The ordinary case, and it is here because a query that answered "nothing" for everything
    /// would pass the two tests below it. The subject is a Πυροβολείο: a building, with a gun in it.
    /// </para>
    /// </summary>
    [Fact]
    public void AFinishedStructureSensesAndTheFogAgrees()
    {
        SimWorld world = World();
        EntityId gun = At(world, team: 0, UnitKind.GunEmplacement, x: 0);

        Assert.True(UnitCatalog.Get(UnitKind.GunEmplacement).IsArmed, "the subject is meant to be an armed structure");
        Assert.True(UnitCatalog.Get(UnitKind.GunEmplacement).IsBuilding, "the subject is meant to be a structure");

        world.RunTicks(Ticks);

        int radius = Watches(world, gun, out SensorRefusal refusal);

        Assert.Equal(SensorRefusal.None, refusal);
        Assert.True(radius > 0, "a finished emplacement senses nothing");

        // The two answers, which are one answer: it says it is watching, and the team has eyes on
        // the cell it stands on. What the fog was stamped with is the same number — the disc is not
        // read here cell by cell, because the count is the honest summary of a disc.
        Assert.True(TheFogHasEyesOn(world, gun), "the query says it watches and the fog has nothing");
        Assert.True(world.Visibility.CountVisible(0) > 0, "a world with one sensor on it has no visible ground");
    }

    /// <summary>
    /// The same structure as a building site senses nothing, and the fog agrees by having nothing at
    /// all: the site is the only thing in this world, so a team with eyes anywhere has been given
    /// them by something that is not watching.
    /// </summary>
    [Fact]
    public void ABuildingSiteSensesNothingAndTheFogAgrees()
    {
        SimWorld world = World();
        EntityId site = At(world, team: 0, UnitKind.GunEmplacement, x: 0);

        // The same role, the same ground, one flag different — which is the whole of the rule.
        world.GetRefBySlot(site.Slot).ConstructionTicksRemaining = 40;

        world.RunTicks(Ticks);

        int radius = Watches(world, site, out SensorRefusal refusal);

        Assert.Equal(0, radius);
        Assert.Equal(SensorRefusal.UnderConstruction, refusal);
        Assert.False(TheFogHasEyesOn(world, site), "a building site stamped fog");
        Assert.Equal(0, world.Visibility.CountVisible(0));
    }

    /// <summary>
    /// A radar station the grid cannot run senses nothing, and the fog agrees — and the same
    /// station on a grid that can run it senses its full coverage, so what is being measured is the
    /// power and not the dish.
    /// <para>
    /// The arithmetic is the ledger's own: a bare base has six units of standby, a factory takes
    /// four of them, and a radar needs four. The factory stands 240 m away, so the cell the radar
    /// stands on is not inside the factory's own eyes either — the ground is unwatched because
    /// nothing is watching it, not because the wrong thing is.
    /// </para>
    /// </summary>
    [Fact]
    public void ADarkRadarSensesNothingAndTheFogAgrees()
    {
        static (SimWorld World, EntityId Radar) Base(bool plant)
        {
            SimWorld world = World();
            At(world, team: 0, UnitKind.Factory, x: -120_000);

            if (plant)
            {
                At(world, team: 0, UnitKind.PowerPlant, x: -60_000);
            }

            return (world, At(world, team: 0, UnitKind.RadarStation, x: 120_000));
        }

        (SimWorld dark, EntityId darkRadar) = Base(plant: false);
        dark.RunTicks(Ticks);

        Assert.False(dark.IsRadarLit(darkRadar.Slot), "the grid had room for the set after all");

        Assert.Equal(0, Watches(dark, darkRadar, out SensorRefusal refusal));
        Assert.Equal(SensorRefusal.RadarDark, refusal);
        Assert.False(TheFogHasEyesOn(dark, darkRadar), "a dark set stamped fog");
        Assert.True(dark.Visibility.CountVisible(0) > 0, "the factory beside it is watching and should be");

        (SimWorld lit, EntityId litRadar) = Base(plant: true);
        lit.RunTicks(Ticks);

        Assert.True(lit.IsRadarLit(litRadar.Slot), "the power plant did not get the set on the air");

        int radius = Watches(lit, litRadar, out SensorRefusal refusal2);

        Assert.Equal(SensorRefusal.None, refusal2);
        Assert.Equal(VisionSystem.RadarCoverageMm, radius);
        Assert.True(TheFogHasEyesOn(lit, litRadar), "a lit set watches and the fog has nothing");
    }

    /// <summary>
    /// The two cases that are not about an entity watching but about there being nothing to ask:
    /// a slot with nothing alive in it, and something that is on no team the simulation has.
    /// <para>
    /// The second is why the stamp loop's team test exists at all: the visibility grid is indexed by
    /// team, so an entity on a team the world does not have would be stamping a disc nobody could
    /// read. The world is not ticked here, because nothing else in it has a sensible answer about an
    /// entity that is on no team — the question being asked is only whether the query has one.
    /// </para>
    /// </summary>
    [Fact]
    public void TheQueryAnswersForWhatIsNotThereAndForWhatIsOnNoTeam()
    {
        SimWorld world = World();

        Assert.Equal(0, Watches(world, new EntityId(slot: 40, generation: 0), out SensorRefusal empty));
        Assert.Equal(SensorRefusal.NoEntity, empty);

        UnitDefinition definition = UnitCatalog.Get(UnitKind.Tank);
        EntityId orphan = world.Spawn(
            Faction.Soviet, SimConstants.TeamCount, UnitKind.Tank, new WorldPos(0, 0, Lane), Fix32.Zero, definition.Health);

        Assert.True(world.IsAliveSlot(orphan.Slot), "the subject is meant to be alive and on no team");
        Assert.Equal(0, Watches(world, orphan, out SensorRefusal teamless));
        Assert.Equal(SensorRefusal.NoTeam, teamless);
    }
}
