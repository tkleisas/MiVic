using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;

using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// Why an entity stamps no sensor disc, when it stamps none — the answer
/// <see cref="VisionSystem.SensorOf"/> gives instead of a radius.
/// <para>
/// Three of these are the stamp loop's own <c>continue</c>s, written down as a value rather than as
/// a silence, and the fourth is the normal case: an entity that is watching, which needs no reason.
/// A caller that has to know <em>why</em> nothing is drawn — a half-raised building is a thing that
/// will watch, a shed radar is a thing that would be watching if the grid could run it — asks here
/// rather than working it out from two of the simulation's other predicates.
/// </para>
/// </summary>
public enum SensorRefusal : byte
{
    /// <summary>It senses: the query answered with a radius.</summary>
    None = 0,

    /// <summary>The slot holds nothing alive.</summary>
    NoEntity = 1,

    /// <summary>It is not on a team the simulation has, so there is nobody for it to watch for.</summary>
    NoTeam = 2,

    /// <summary>
    /// A structure still being raised. A building site is not watching anything yet, which is the
    /// same rule that keeps a half-raised emplacement from firing: the dish goes on the roof when
    /// the roof goes on.
    /// </summary>
    UnderConstruction = 3,

    /// <summary>
    /// A radar station the grid has shed. The set is not running, which is the same fact as the
    /// guns losing their reach — and it has to be said here as well as in the power ledger, or a
    /// base that had lost its generation would keep the radar picture it can no longer pay for.
    /// </summary>
    RadarDark = 4,
}

/// <summary>
/// Recomputes what each team can see and what it can detect.
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
/// <para>
/// <b>This is the only place that decides how far anything can be sensed, and the
/// only place that writes what a team knows.</b> Fog of war reads the disc each
/// entity stamps; a weapon reads <see cref="SensorRadiusMm"/> to decide whether it
/// can shoot at what it is looking at, and the same disc, reduced by
/// <see cref="StealthDetectionPermille"/>, decides whether a stealthed enemy is
/// found. A radar station is not a special case in any of that: it stamps a large
/// disc like anything else, and its coverage is the disc. Adding a second answer to
/// "can team 0 see this cell" — a scan beside the grid, or a detection field beside
/// the fog — is how the two answers come to disagree.
/// </para>
/// <para>
/// <b>That includes the entities that sense nothing, which is why <see cref="SensorOf"/> is the
/// same function the stamp loop runs.</b> A rule about who watches is a rule about who does not,
/// and a caller drawing what a side can see has to reverse the exemptions to draw it correctly:
/// the map's coverage layer did exactly that, by hand, until the query existed. A second copy of
/// "a building site is not watching yet" is right until the day the first one changes.
/// </para>
/// </summary>
public static class VisionSystem
{
    /// <summary>Ticks between visibility updates for a given entity.</summary>
    public const int UpdateInterval = 10;

    /// <summary>
    /// Sight radius in snow, in permille. Snow blinds as well as slows: a unit in it
    /// sees roughly two thirds as far, so an advance through snow is made nearly
    /// blind and scouting matters more.
    /// </summary>
    public const int SnowSightPermille = 650;

    /// <summary>
    /// The radius at which a sensor finds a <em>stealthed</em> enemy, as a fraction of the
    /// radius it finds an ordinary one, in permille.
    /// <para>
    /// This is the whole of what stealth buys, and the number is not free. The only stealthed
    /// role in the game is the Δυτικοί Καταδρομέας, whose weapon reaches 120 m, so a stealth
    /// modifier is worth nothing unless it changes what happens at that distance: a gun's own
    /// eyes are 170 m, and half of that is 85 m — well inside 120 m, so a stalker walking up
    /// to a lone emplacement is invisible until the moment it opens fire and still gets the
    /// first shot. A radar's coverage is 260 m, and half of that is 130 m, which is *past* the
    /// stalker's reach: under an umbrella, the defence sees it ten metres before it can shoot,
    /// and the first shot is the defender's.
    /// </para>
    /// <para>
    /// A half rather than a third for exactly that reason: a third puts a radar's detection at
    /// 91 m, inside the stalker's own reach, and stealth detection then never decides anything
    /// a gunfight has not already decided by the stalker firing. Stealth that cannot be beaten
    /// to the trigger is not a mechanic, it is a delay.
    /// </para>
    /// </summary>
    public const int StealthDetectionPermille = 500;

    /// <summary>
    /// How far a radar station projects detection, in millimetres. Also the station's own
    /// sight radius: a radar is one disc, not a coverage field beside an eyesight number, so
    /// the ground it lights for the team is exactly the ground it lights for the guns.
    /// <para>
    /// 260 m against a gun emplacement's 200 m of reach, and the margin is the design. A radar
    /// whose coverage merely matched the longest gun would only have to be dropped near enough
    /// to the gun, and placement would stop being a decision; 60 m of umbrella beyond the
    /// farthest gun means a station covers a *position* — several guns, an approach, a flank —
    /// rather than one emplacement.
    /// </para>
    /// </summary>
    public const int RadarCoverageMm = 260_000;

    /// <summary>
    /// Sight radius for a role, in millimetres — which is also its sensor radius, and the
    /// number a weapon is measured against. There is one table because there is one question.
    /// <para>
    /// Two structures carry a number smaller than the gun they carry: a Πυροβολείο reaches
    /// 200 m and sees 170, and an Αντιαεροπορικό Πυροβολείο reaches 180 m and sees 160. That
    /// gap is the entire reason a radar station is worth 240 Π, and it is deliberately a
    /// property of the role rather than a special case in the combat system: a mobile hull is
    /// assumed to have its own optics good enough for its own gun, and a fixed emplacement is
    /// assumed not to, because a concrete pit has no observer in it.
    /// </para>
    /// </summary>
    public static int SightRadiusMm(UnitKind kind) => kind switch
    {
        UnitKind.Infantry => 110_000,
        UnitKind.Tank => 130_000,
        UnitKind.Artillery => 160_000,
        UnitKind.RocketArtillery => 170_000,
        UnitKind.Commissar => 90_000,
        UnitKind.Mercenary => 120_000,
        UnitKind.StealthRecon => 150_000,
        UnitKind.ElectroPrototype => 180_000,
        UnitKind.AntiAir => 140_000,
        UnitKind.Aircraft => 200_000,
        UnitKind.Drone => 150_000,
        UnitKind.RobotInfantry => 100_000,
        UnitKind.CommandCentre => 150_000,
        UnitKind.PowerPlant => 110_000,
        UnitKind.NuclearPlant => 140_000,
        UnitKind.Factory => 130_000,
        UnitKind.DesignBureau => 120_000,
        UnitKind.GunEmplacement => 170_000,
        UnitKind.AntiAirEmplacement => 160_000,
        UnitKind.RadarStation => RadarCoverageMm,
        _ => 100_000,
    };

    /// <summary>
    /// How far this entity can sense an ordinary enemy, in millimetres: its role's eyes,
    /// scaled by the team's optics research and shortened by standing in snow.
    /// <para>
    /// This is the number the whole sensor chain asks about, and it is answered in one place
    /// for that reason: fog stamps a disc of it, a weapon compares its range against it, and
    /// the stealth disc is a fraction of it. A caller that recomputed any of those three from
    /// the catalogue would be a second answer waiting to disagree with the first.
    /// </para>
    /// </summary>
    public static int SensorRadiusMm(SimWorld world, in Entity entity)
    {
        int visionPermille = world.Team(entity.TeamId).VisionPermille;
        int radius = visionPermille > 0
            ? (SightRadiusMm(entity.Kind) * visionPermille) / 1_000
            : SightRadiusMm(entity.Kind);

        // Snow shortens how far a unit can see. It also hides: the ground that
        // slows a column is the ground that conceals it, which is what makes
        // fighting in snow a different problem from fighting in mud. Aircraft are
        // above the weather, so only something on the ground pays it.
        if (entity.AltitudeMm == 0)
        {
            int cell = world.Navigation.IndexOfWorld(entity.Position);

            if (cell >= 0 && world.TerrainTypes.TypeAt(cell) == TerrainType.Snow)
            {
                radius = (radius * SnowSightPermille) / 1_000;
            }
        }

        return radius;
    }

    /// <summary>
    /// <b>Whether one entity is watching, how far, and — when it is not — why not.</b>
    /// <para>
    /// The stamp loop below skips four entities, and none of those skips could be asked about: a
    /// slot with nothing in it, something that is not on a team the simulation has, a structure
    /// still being raised, and a radar station the grid has shed. A caller that wanted to know what
    /// a side's fog is made of had to write those exclusions out again — the map's coverage layer
    /// did, in its own words, to draw a rim per disc — and a rule kept in two places is right only
    /// until one of them changes. This is that rule, in the place that owns it, and
    /// <see cref="Tick"/> runs through it rather than beside it.
    /// </para>
    /// <para>
    /// The radius is <see cref="SensorRadiusMm"/>'s, so the rim a caller draws and the disc the fog
    /// stamps are one number rather than two that agree today. Zero means no disc at all, which is
    /// how the stamp loop reads it as well.
    /// </para>
    /// </summary>
    /// <param name="world">The world the entity stands in.</param>
    /// <param name="slot">The entity's slot.</param>
    /// <param name="refusal">
    /// Why it senses nothing, or <see cref="SensorRefusal.None"/> when it does — <see cref="SensorRefusal"/>
    /// for what each case means.
    /// </param>
    /// <returns>The radius it watches to, in millimetres, or zero when it watches nothing.</returns>
    public static int SensorOf(SimWorld world, int slot, out SensorRefusal refusal)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAliveSlot(slot))
        {
            refusal = SensorRefusal.NoEntity;
            return 0;
        }

        ref Entity entity = ref world.GetRefBySlot(slot);

        // A team the simulation does not have has no fog to write into: the grid is indexed by team,
        // so an entity that is on none of them would be stamping a disc nobody could read.
        if ((uint)entity.TeamId >= SimConstants.TeamCount)
        {
            refusal = SensorRefusal.NoTeam;
            return 0;
        }

        if (entity.ConstructionTicksRemaining > 0)
        {
            refusal = SensorRefusal.UnderConstruction;
            return 0;
        }

        if (entity.Kind == UnitKind.RadarStation && !world.IsRadarLit(slot))
        {
            refusal = SensorRefusal.RadarDark;
            return 0;
        }

        refusal = SensorRefusal.None;
        return SensorRadiusMm(world, in entity);
    }

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

            // Whether this entity is watching at all, and how far, is one question and it is asked
            // in one place: a building site is not watching anything yet — the dish goes on the roof
            // when the roof goes on — and neither is a radar station the grid has shed, which is the
            // same fact as the guns losing their reach. It is the question the map's coverage layer
            // asks as well, so the disc a team gets and the rim a picture draws cannot be two rules.
            int sensor = SensorOf(world, slot, out _);

            if (sensor <= 0)
            {
                continue;
            }

            Stamp(world, entity.TeamId, entity.Position, sensor, false);

            int stealth = (sensor * StealthDetectionPermille) / 1_000;

            // The same disc again, a third of the size, into the channel that decides
            // whether a stealthed enemy is found. It is stamped by the same loop from the
            // same number so that "what can team 0 see" and "can team 0 see the stalker"
            // cannot drift apart: a radar lights both, because a radar lights both.
            if (stealth > 0)
            {
                Stamp(world, entity.TeamId, entity.Position, stealth, true);
            }
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
    /// <para>
    /// Internal rather than private because a mission's reveals are stamped through it: a
    /// scripted reveal is a disc of ground a team is watching, and it has to be the same disc,
    /// written the same way, as the one a unit's own eyes write.
    /// </para>
    /// </summary>
    internal static void Stamp(SimWorld world, int team, WorldPos centre, int radiusMm, bool stealth)
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
                if (stealth)
                {
                    grid.MarkDetected(team, rowBase + x);
                }
                else
                {
                    grid.MarkVisible(team, rowBase + x);
                }
            }
        }
    }

    /// <summary>Integer division that rounds towards negative infinity.</summary>
    private static int FloorDiv(int value, int divisor)
        => value >= 0 ? value / divisor : -(((-value) + divisor - 1) / divisor);
}
