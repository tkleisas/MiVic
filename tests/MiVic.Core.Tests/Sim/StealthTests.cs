using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// Δυτικοί stealth: the era-V edge. You cannot shoot what you cannot see — but a
/// shot gives the position away, and something close enough will find you.
/// </summary>
public sealed class StealthTests
{
    private static SimWorld World(out WorldPos centre)
    {
        SimWorld world = new(seed: 616, capacity: 32);
        centre = world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));
        return world;
    }

    [Fact]
    public void OnlyTheWestHasAStalker()
    {
        Assert.True(UnitCatalog.Get(UnitKind.StealthRecon).Stealthy);
        Assert.Equal(Faction.Western, UnitCatalog.Get(UnitKind.StealthRecon).OnlyFor);

        Assert.False(UnitCatalog.IsUnlocked(Faction.Soviet, UnitKind.StealthRecon, techTier: 5));
        Assert.False(UnitCatalog.IsUnlocked(Faction.Chinese, UnitKind.StealthRecon, techTier: 3));
    }

    [Fact]
    public void AStealthedUnitIsHiddenFromTheEnemyAtDistance()
    {
        SimWorld world = World(out WorldPos centre);

        EntityId stalker = world.Spawn(
            Faction.Western,
            2,
            UnitKind.StealthRecon,
            new WorldPos(centre.X + 200_000, 0, centre.Z),
            Fix32.Zero,
            1_000);

        world.Spawn(Faction.Soviet, 0, UnitKind.Tank, centre, Fix32.Zero, 5_000);
        world.Step();

        Assert.True(world.IsHiddenFrom(0, stalker.Slot), "A distant stalker was visible.");

        // Its own side always sees it.
        Assert.False(world.IsHiddenFrom(2, stalker.Slot));
    }

    [Fact]
    public void SomethingCloseEnoughDetectsIt()
    {
        SimWorld world = World(out WorldPos centre);

        EntityId stalker = world.Spawn(Faction.Western, 2, UnitKind.StealthRecon, centre, Fix32.Zero, 1_000);

        // Inside the radius at which the tank's own eyes find a stealthed enemy, which is a
        // third of the 130 m it sees an ordinary one at — see
        // VisionSystem.StealthDetectionPermille. Twenty metres is well inside that, so this
        // is a test of detection and not a test of where the boundary happens to fall.
        world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Tank,
            new WorldPos(centre.X + 20_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        // Detection is stamped by the vision pass, which is spread over ticks: a unit
        // contributes its disc once every UpdateInterval ticks, so a single Step is not
        // enough for anything to have looked. It is the same stagger the fog has always
        // had, and the reason it is now visible here is that detection reads the same grid
        // the fog does instead of scanning for a nearby enemy on demand.
        world.RunTicks(VisionSystem.UpdateInterval);

        Assert.False(world.IsHiddenFrom(0, stalker.Slot), "A nearby enemy failed to detect it.");
    }

    [Fact]
    public void FiringRevealsIt()
    {
        SimWorld world = World(out WorldPos centre);

        EntityId stalker = world.Spawn(
            Faction.Western,
            2,
            UnitKind.StealthRecon,
            new WorldPos(centre.X + 200_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        // An enemy far outside detection range but inside weapon range, so the
        // stalker opens fire and gives itself away.
        world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Tank,
            new WorldPos(centre.X + 200_000 + 100_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        world.RunTicks(60);

        ref Entity entity = ref world.GetRefBySlot(stalker.Slot);

        Assert.True(entity.RevealedUntilTick > world.Tick, "Firing did not reveal the stalker.");
        Assert.False(world.IsHiddenFrom(0, stalker.Slot));
    }

    [Fact]
    public void AHiddenStalkerCannotBeTargeted()
    {
        SimWorld world = World(out WorldPos centre);

        EntityId stalker = world.Spawn(
            Faction.Western,
            2,
            UnitKind.StealthRecon,
            new WorldPos(centre.X + 200_000, 0, centre.Z),
            Fix32.Zero,
            1_000);

        // Artillery out-ranges the stalker (220 m against 120 m), so the stalker
        // never fires and never gives itself away. The artillery can reach it and
        // still must not shoot, because it cannot see it.
        EntityId gun = world.Spawn(
            Faction.Soviet,
            0,
            UnitKind.Artillery,
            new WorldPos(centre.X + 350_000, 0, centre.Z),
            Fix32.Zero,
            5_000);

        world.RunTicks(20);

        Assert.Equal(-1, world.GetRefBySlot(gun.Slot).TargetSlot);
        Assert.Equal(1_000, world.GetRefBySlot(stalker.Slot).Health);
    }
}
