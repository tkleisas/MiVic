using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Map.Tests;

/// <summary>
/// The fixtures the map tests draw from, and the one colour table they draw with.
/// <para>
/// The palette is a fixed set of colours that appear nowhere in the game, and that is deliberate:
/// a test that asserted "a Σοβιετικοί tank is red" would be a second copy of the client's palette
/// held to the client's palette, and would fail the day somebody changed the tint on purpose.
/// What the tests are about is that the drawing reads the tables it is given, not what is in them.
/// </para>
/// </summary>
internal static class TestWorld
{
    /// <summary>Colours a test can recognise in a rendered pixel, one per surface.</summary>
    public static readonly MapPalette Palette = MapPalette.Of(
        surface => surface switch
        {
            TerrainType.Mud => new MapRgb(20, 40, 60),
            TerrainType.DeepWater => new MapRgb(0, 0, 200),
            TerrainType.ShallowWater => new MapRgb(0, 0, 120),
            TerrainType.Lava => new MapRgb(200, 0, 0),
            TerrainType.Forest => new MapRgb(0, 90, 0),
            TerrainType.Rock => new MapRgb(90, 90, 90),
            TerrainType.Snow => new MapRgb(240, 240, 240),
            TerrainType.Sand => new MapRgb(190, 170, 90),
            _ => new MapRgb(30, 70, 30),
        },
        (faction, _) => faction switch
        {
            Faction.Soviet => new MapRgb(255, 0, 0),
            Faction.Chinese => new MapRgb(255, 255, 0),
            Faction.Western => new MapRgb(0, 0, 255),
            _ => new MapRgb(128, 128, 128),
        });

    /// <summary>A world small enough to draw in a test and real enough to have terrain in it.</summary>
    public static SimWorld NewWorld(ulong seed = 20_260_214UL, int capacity = 256) => new(seed, capacity);

    /// <summary>
    /// A spot on the map where a ground mover of this class can actually stand, found by walking
    /// the lattice. Terrain is generated from the seed, so a test that hard-coded a position would
    /// be a test that passes until the terrain generator changes.
    /// </summary>
    public static WorldPos OpenGround(SimWorld world, MovementClass movement, int fromCell = 0)
    {
        for (int cell = fromCell; cell < world.Navigation.CellCount; cell++)
        {
            if (world.Navigation.IsWalkable(cell) && world.TerrainTypes.IsPassable(cell, movement))
            {
                return world.Navigation.CentreOf(cell);
            }
        }

        throw new InvalidOperationException("This world has nowhere to stand, which is a terrain bug rather than a map bug.");
    }

    /// <summary>The far corner of the map a mover of this class can stand on, for a long march.</summary>
    public static WorldPos FarGround(SimWorld world, MovementClass movement)
    {
        for (int cell = world.Navigation.CellCount - 1; cell >= 0; cell--)
        {
            if (world.Navigation.IsWalkable(cell) && world.TerrainTypes.IsPassable(cell, movement))
            {
                return world.Navigation.CentreOf(cell);
            }
        }

        throw new InvalidOperationException("This world has no far side, which is a terrain bug rather than a map bug.");
    }

    /// <summary>A tank standing on open ground, with the slot it landed in.</summary>
    public static (EntityId Id, int Slot) SpawnTank(SimWorld world, Faction faction, int team, WorldPos? at = null)
    {
        WorldPos position = at ?? OpenGround(world, MovementClass.Tracked);

        EntityId id = world.Spawn(
            faction,
            team,
            UnitKind.Tank,
            position,
            Fix32.FromInt(UnitCatalog.Get(UnitKind.Tank).SpeedMmPerTick),
            UnitCatalog.Get(UnitKind.Tank).Health);

        return world.TryGetRef(id, out _, out int slot)
            ? (id, slot)
            : throw new InvalidOperationException("A freshly spawned entity did not resolve to a slot.");
    }

    /// <summary>
    /// Gives a unit a move goal through the simulation's own command path, without running a tick.
    /// <para>
    /// The command is stamped for the tick the world is already on and applied immediately, so
    /// the unit holds a goal and a route search it has not been given yet — which is the state
    /// <c>routes</c> calls waiting and the map's <c>stuck</c> layer calls stalled. Reaching it by
    /// playing would mean stepping into the tick that answers the search, and the answer is
    /// instant for a single unit: path searches are budgeted at four a tick and this world has
    /// one unit in it.
    /// </para>
    /// </summary>
    public static void OrderTo(SimWorld world, EntityId id, WorldPos destination)
    {
        int slot = SlotOf(world, id);
        world.Enqueue(SimCommand.Move(id, destination, world.Tick, world.GetRefBySlot(slot).TeamId));
        world.ApplyPendingCommands();
    }

    /// <summary>The slot a live handle resolves to.</summary>
    public static int SlotOf(SimWorld world, EntityId id)
        => world.TryGetRef(id, out _, out int slot)
            ? slot
            : throw new InvalidOperationException("That handle does not resolve to anything alive.");

    /// <summary>
    /// Samples a world's units <paramref name="times"/> times at a standstill, which is how a
    /// still world with a route in hand is built: the sampler's guard is on the tick, so calling
    /// it repeatedly between ticks fills a window without any unit having to move.
    /// </summary>
    public static MapTrails Sampled(SimWorld world, int times, int sampleCount = 8)
    {
        if (world.Tick % MapTrails.DefaultIntervalTicks != 0)
        {
            throw new InvalidOperationException(
                "The sampler only fills its window on a sample tick, and this world is between two of them.");
        }

        var trails = new MapTrails(MapTrails.DefaultIntervalTicks, sampleCount);

        for (int i = 0; i < times; i++)
        {
            trails.Advance(world);
        }

        return trails;
    }
}
