using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;

namespace MiVic.Core.Sim;

/// <summary>
/// Recomputes what each team can see.
/// <para>
/// Vision is stamped from every entity into the navigation grid: a circle of the
/// unit's sight radius. The work is <em>staggered</em> rather than done in one
/// burst: each tick, only the entities whose slot matches the tick's phase stamp
/// their disc, so every unit contributes once per
/// <see cref="UpdateInterval"/> ticks and the cost is spread evenly. Stamping all
/// five hundred units on one tick cost about sixteen milliseconds every half
/// second, which showed up as a visible hitch; the same work spread over ten
/// ticks costs a fraction of a millisecond each.
/// </para>
/// <para>
/// <see cref="VisibilityGrid"/> keeps the tick a cell was last stamped and treats
/// it as visible until the unit that saw it comes round again, so nothing needs
/// clearing and the answer is identical from the player's point of view.
/// </para>
/// </summary>
public static class VisionSystem
{
    /// <summary>Ticks between visibility updates for a given entity.</summary>
    public const int UpdateInterval = 10;

    /// <summary>Sight radius for a role, in millimetres.</summary>
    public static int SightRadiusMm(UnitKind kind) => kind switch
    {
        UnitKind.Infantry => 110_000,
        UnitKind.Tank => 130_000,
        UnitKind.Artillery => 160_000,
        UnitKind.RocketArtillery => 170_000,
        UnitKind.Commissar => 90_000,
        UnitKind.AntiAir => 140_000,
        UnitKind.Aircraft => 200_000,
        UnitKind.Drone => 150_000,
        UnitKind.RobotInfantry => 100_000,
        UnitKind.CommandCentre => 150_000,
        UnitKind.PowerPlant => 110_000,
        UnitKind.Factory => 130_000,
        UnitKind.DesignBureau => 120_000,
        _ => 100_000,
    };

    /// <summary>Stamps this tick's share of the entities.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        VisibilityGrid grid = world.Visibility;

        // The grid's clock drives the visibility window, so it advances every
        // tick even though each entity is only stamped every tenth.
        grid.BeginTick(world.Tick);

        int phase = (int)(world.Tick % UpdateInterval);
        int capacity = world.Capacity;

        for (int slot = phase; slot < capacity; slot += UpdateInterval)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if ((uint)entity.TeamId >= SimConstants.TeamCount)
            {
                continue;
            }

            int visionPermille = world.Team(entity.TeamId).VisionPermille;
            int sight = visionPermille > 0
                ? (SightRadiusMm(entity.Kind) * visionPermille) / 1_000
                : SightRadiusMm(entity.Kind);

            Stamp(world, entity.TeamId, entity.Position, sight);
        }
    }

    /// <summary>
    /// Marks every navigation cell whose centre lies within the radius.
    /// <para>
    /// The loop walks lattice coordinates directly and computes each cell centre
    /// incrementally. Calling <c>CentreOf</c> per cell would do two integer
    /// divisions — index to x and z — and with half a million cells per update
    /// that alone cost over a hundred milliseconds.
    /// </para>
    /// </summary>
    private static void Stamp(SimWorld world, int team, WorldPos centre, int radiusMm)
    {
        NavGrid nav = world.Navigation;
        VisibilityGrid grid = world.Visibility;

        int cell = nav.CellSizeMm;
        int half = cell / 2;
        int origin = nav.OriginMm;
        int size = nav.Size;
        long radiusSquared = (long)radiusMm * radiusMm;

        int minCellX = Math.Max(0, (centre.X - radiusMm - origin) / cell);
        int maxCellX = Math.Min(size - 1, (centre.X + radiusMm - origin) / cell);
        int minCellZ = Math.Max(0, (centre.Z - radiusMm - origin) / cell);
        int maxCellZ = Math.Min(size - 1, (centre.Z + radiusMm - origin) / cell);

        for (int z = minCellZ; z <= maxCellZ; z++)
        {
            int worldZ = origin + (z * cell) + half;
            long dz = (long)worldZ - centre.Z;
            long dzSquared = dz * dz;

            if (dzSquared > radiusSquared)
            {
                continue;
            }

            // Half-width of the circle on this row, so the inner loop is a
            // straight fill instead of a distance test per cell.
            int halfSpan = (int)IntMath.SqrtLong(radiusSquared - dzSquared);

            int firstX = Math.Max(0, FloorDiv(centre.X - halfSpan - origin, cell));
            int lastX = Math.Min(size - 1, FloorDiv(centre.X + halfSpan - origin, cell));
            int rowBase = z * size;

            for (int x = firstX; x <= lastX; x++)
            {
                grid.MarkVisible(team, rowBase + x);
            }
        }
    }

    /// <summary>Integer division that rounds towards negative infinity.</summary>
    private static int FloorDiv(int value, int divisor)
        => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
}
