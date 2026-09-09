using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The fog mask is what the client uploads as a texture, so its encoding is a
/// contract between the simulation and the renderer: channel 0 is fog, channel 1
/// is memory.
/// </summary>
public sealed class FogMaskTests
{
    private static SimWorld World() => new(seed: 20250101, capacity: 32);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
        => world.Spawn(
            faction,
            team,
            kind,
            new WorldPos(x, 0, z),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            UnitCatalog.Get(kind).Health);

    private static byte[] Mask(SimWorld world, int team)
    {
        byte[] mask = new byte[world.Visibility.CellCount * 2];
        world.Visibility.BuildFogMask(team, mask);
        return mask;
    }

    [Fact]
    public void VisibleGroundCarriesNoFogAndFullMemory()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, 0, 0);
        world.RunTicks(VisionSystem.UpdateInterval);

        byte[] mask = Mask(world, 0);
        int cell = world.Navigation.IndexOfWorld(WorldPos.Origin);

        Assert.Equal(0, mask[(cell * 2) + 0]);
        Assert.Equal(255, mask[(cell * 2) + 1]);
    }

    [Fact]
    public void GroundThatWasNeverSeenIsHeavilyFoggedAndUnremembered()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, -100_000, -100_000);
        world.RunTicks(VisionSystem.UpdateInterval);

        byte[] mask = Mask(world, 0);
        int unseen = world.Navigation.IndexOfWorld(new WorldPos(100_000, 0, 100_000));

        Assert.Equal(VisibilityGrid.UnknownFogLevel, mask[(unseen * 2) + 0]);
        Assert.Equal(0, mask[(unseen * 2) + 1]);
    }

    [Fact]
    public void GroundSeenOnceStaysDimlyLitInsteadOfBlack()
    {
        SimWorld world = World();
        EntityId scout = Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, -100_000, 0);
        world.RunTicks(VisionSystem.UpdateInterval);

        int cell = world.Navigation.IndexOfWorld(new WorldPos(-100_000, 0, 0));

        world.GetRefBySlot(scout.Slot).Position = new WorldPos(200_000, 0, 200_000);
        world.RunTicks(VisionSystem.UpdateInterval + 1);

        byte[] mask = Mask(world, 0);

        Assert.Equal(VisibilityGrid.RememberedFogLevel, mask[(cell * 2) + 0]);
        Assert.Equal(255, mask[(cell * 2) + 1]);
        Assert.True(
            VisibilityGrid.RememberedFogLevel < VisibilityGrid.UnknownFogLevel,
            "Remembered ground must be less fogged than ground never seen.");
    }

    [Fact]
    public void MaskIsPerTeam()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, -100_000, 0);
        Spawn(world, Faction.Western, 2, UnitKind.Infantry, 100_000, 0);
        world.RunTicks(VisionSystem.UpdateInterval);

        byte[] soviet = Mask(world, 0);
        byte[] western = Mask(world, 2);

        int sovietCell = world.Navigation.IndexOfWorld(new WorldPos(-100_000, 0, 0));
        int westernCell = world.Navigation.IndexOfWorld(new WorldPos(100_000, 0, 0));

        Assert.Equal(0, soviet[(sovietCell * 2) + 0]);
        Assert.Equal(VisibilityGrid.UnknownFogLevel, soviet[(westernCell * 2) + 0]);
        Assert.Equal(0, western[(westernCell * 2) + 0]);
        Assert.Equal(VisibilityGrid.UnknownFogLevel, western[(sovietCell * 2) + 0]);
    }

    [Fact]
    public void AnUnknownTeamSeesNothing()
    {
        SimWorld world = World();
        Spawn(world, Faction.Soviet, 0, UnitKind.Infantry, 0, 0);
        world.RunTicks(VisionSystem.UpdateInterval);

        byte[] mask = Mask(world, 99);

        for (int cell = 0; cell < world.Visibility.CellCount; cell++)
        {
            Assert.Equal(VisibilityGrid.UnknownFogLevel, mask[(cell * 2) + 0]);
            Assert.Equal(0, mask[(cell * 2) + 1]);
        }
    }

    [Fact]
    public void UndersizedDestinationIsRejected()
    {
        SimWorld world = World();
        byte[] tooSmall = new byte[8];

        Assert.Throws<ArgumentException>(() => world.Visibility.BuildFogMask(0, tooSmall));
    }

    [Fact]
    public void MaskIsDeterministic()
    {
        static byte[] Run()
        {
            SimWorld world = World();
            Spawn(world, Faction.Soviet, 0, UnitKind.Tank, 20_000, 20_000);
            Spawn(world, Faction.Western, 2, UnitKind.Tank, -20_000, -20_000);
            world.RunTicks(40);
            return Mask(world, 0);
        }

        Assert.Equal(Run(), Run());
    }
}
