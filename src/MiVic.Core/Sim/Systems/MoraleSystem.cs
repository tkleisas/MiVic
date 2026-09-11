using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// The morale system: the reason the Δυτικοί can field the best hardware in the
/// game and still lose with it.
/// <para>
/// Every armed unit carries a morale value in [0, 1] that drifts towards a target
/// set by three things: the faction's floor (Σοβιετικοί never break, Δυτικοί
/// start brittle), how badly the unit is outnumbered nearby, and how many friends
/// the team has lost recently. Below <see cref="RoutThresholdRaw"/> the unit
/// breaks and falls back; it will not fire again until it rallies.
/// </para>
/// <para>
/// Morale also scales reload speed, so a shaken unit is not merely closer to
/// running — it is already worse in the fight.
/// </para>
/// </summary>
public static class MoraleSystem
{
    /// <summary>Morale below this (0.25) makes a unit rout.</summary>
    public const int RoutThresholdRaw = 16_384;

    /// <summary>Morale above this (0.40) rallies a routed unit.</summary>
    public const int RallyThresholdRaw = 26_214;

    /// <summary>Radius in millimetres within which friends and enemies are counted.</summary>
    public const int ScanRadiusMm = 80_000;

    /// <summary>Distance a broken unit falls back, in millimetres.</summary>
    public const int RetreatDistanceMm = 140_000;

    /// <summary>Morale closes this fraction of the gap to its target each tick.</summary>
    private const int ChangeDivisor = 24;

    /// <summary>
    /// Ticks between proximity scans. The neighbours around a unit change slowly
    /// compared with the tick rate, and the scan is the most expensive query in
    /// the simulation, so it runs at 2.5 Hz while the drift runs at 20 Hz.
    /// </summary>
    public const int ScanInterval = 8;

    /// <summary>Weight applied to the local force balance, giving at most ±0.25.</summary>
    private const int BalanceWeightDivisor = 4;

    /// <summary>Morale lost per recent casualty, capped at half the bar.</summary>
    private const int CasualtyPenaltyRaw = 2_048;

    /// <summary>Ceiling on the cohesion bonus (0.20), so numbers steady a unit but never make it unbreakable.</summary>
    public const int MaxCohesionRaw = 13_107;

    /// <summary>Runs one morale tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        bool scan = world.Tick % ScanInterval == 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            if (!definition.IsArmed)
            {
                continue;
            }

            // Automata have nobody aboard to steady or to break. Leaving them out
            // entirely means they never rout and never accumulate a morale target,
            // which is exactly what "no morale" should mean. A structure is the same
            // fact for a different reason: there is no crew to shake, so an armed
            // building must not be able to rout out of its own gun, and its reload
            // must not drift with a bar nobody is watching.
            if (definition.IsAutomaton || definition.IsBuilding)
            {
                continue;
            }

            if (scan)
            {
                Scan(world, slot, ref entity);
            }

            int delta = entity.MoraleTargetRaw - entity.Morale.Raw;
            entity.Morale = Fix32.FromRaw(entity.Morale.Raw + (delta / ChangeDivisor));

            if (entity.Routed && entity.Morale.Raw > RallyThresholdRaw)
            {
                entity.Routed = false;
            }
        }
    }

    /// <summary>
    /// Recomputes the morale target from the faction floor, the local force
    /// balance and the team's recent casualties, and breaks the unit if its
    /// morale has already collapsed.
    /// </summary>
    private static void Scan(SimWorld world, int slot, ref Entity entity)
    {
        FactionProfile profile = FactionProfile.For(entity.Faction);
        UnitDefinition definition = UnitCatalog.Get(entity.Kind);

        int friends = 0;
        int enemies = 0;
        int auraRaw = 0;
        long nearestEnemySquared = long.MaxValue;
        int nearestEnemyX = 0;
        int nearestEnemyZ = 0;

        SpatialIndex index = world.Spatial;
        int minX = index.CoordinateOf(entity.Position.X - ScanRadiusMm);
        int maxX = index.CoordinateOf(entity.Position.X + ScanRadiusMm);
        int minZ = index.CoordinateOf(entity.Position.Z - ScanRadiusMm);
        int maxZ = index.CoordinateOf(entity.Position.Z + ScanRadiusMm);

        for (int cellZ = minZ; cellZ <= maxZ; cellZ++)
        {
            for (int cellX = minX; cellX <= maxX; cellX++)
            {
                foreach (int other in index.Cell(index.IndexOf(cellX, cellZ)))
                {
                    if (other == slot)
                    {
                        continue;
                    }

                    ref Entity candidate = ref world.GetRefBySlot(other);

                    long distanceSquared = entity.Position.DistanceSquaredTo(candidate.Position);

                    if (distanceSquared > (long)ScanRadiusMm * ScanRadiusMm)
                    {
                        continue;
                    }

                    // Friend or foe is the alliance and not the team id. Counting an ally as
                    // an enemy would make a man standing beside his brother-in-arms feel
                    // outnumbered by him — the same mistake the guns were making, one system
                    // over, and the same predicate settles it.
                    if (!world.IsHostile(entity.TeamId, candidate.TeamId))
                    {
                        friends++;

                        UnitDefinition friend = UnitCatalog.Get(candidate.Kind);

                        if (friend.HasMoraleAura && friend.MoraleAuraRaw > auraRaw)
                        {
                            auraRaw = friend.MoraleAuraRaw;
                        }
                    }
                    else
                    {
                        enemies++;

                        if (distanceSquared < nearestEnemySquared)
                        {
                            nearestEnemySquared = distanceSquared;
                            nearestEnemyX = candidate.Position.X;
                            nearestEnemyZ = candidate.Position.Z;
                        }
                    }
                }
            }
        }

        int balance = friends + enemies > 0
            ? ((friends - enemies) * 65_536) / (friends + enemies)
            : 0;

        ref TeamState team = ref world.TeamRef(entity.TeamId);
        int casualtyPenalty = Math.Min(32_768, team.RecentCasualties * CasualtyPenaltyRaw);

        // Numerical cohesion: numbers are the Κινέζοι answer to per-unit morale.
        // Capped, so a large swarm is steady but never unbreakable.
        int cohesion = Math.Min(
            MaxCohesionRaw,
            friends * team.CohesionPerFriendRaw);

        // Propaganda sets the baseline the army fights for. It is a bill, not a
        // switch: funded, the army is steadier than its raw floor; unfunded, it is
        // already halfway to breaking before the first shot is fired.
        int propaganda = profile.PropagandaDivisor > 0
            ? (team.PropagandaPaid ? profile.PropagandaBonusRaw : -profile.PropagandaPenaltyRaw)
            : 0;

        entity.MoraleTargetRaw = IntMath.Clamp(
            profile.MoraleFloor.Raw + team.MoraleBonusRaw + auraRaw + cohesion + propaganda +
            (balance / BalanceWeightDivisor) - casualtyPenalty,
            0,
            65_536);

        // Contract troops have no loyalty to the cause, only to the paymaster. An
        // unpaid mercenary is not shaken, it is out of contract — which reads as a
        // rout, because a unit that will not fight is what a rout is.
        if (definition.WagePerTick > 0 && !team.WagesPaid)
        {
            entity.MoraleTargetRaw = 0;
        }

        if (entity.Morale.Raw < RoutThresholdRaw && !entity.Routed)
        {
            entity.Routed = true;
            entity.TargetSlot = -1;
            entity.HasAttackOrder = false;
            IssueRetreat(world, slot, ref entity, nearestEnemySquared, nearestEnemyX, nearestEnemyZ);
        }
    }

    /// <summary>
    /// Sends a broken unit directly away from the nearest enemy. The direction is
    /// derived from positions rather than randomness, so a rout is reproducible.
    /// </summary>
    private static void IssueRetreat(
        SimWorld world,
        int slot,
        ref Entity entity,
        long nearestEnemySquared,
        int enemyX,
        int enemyZ)
    {
        int stepX;
        int stepZ;

        if (nearestEnemySquared != long.MaxValue)
        {
            int dx = entity.Position.X - enemyX;
            int dz = entity.Position.Z - enemyZ;

            if (dx == 0 && dz == 0)
            {
                dx = 1;
            }

            stepX = IntMath.Sign(dx);
            stepZ = IntMath.Sign(dz);
        }
        else
        {
            stepX = -1;
            stepZ = 0;
        }

        int limit = (SimConstants.MapExtentMm / 2) - 10_000;

        entity.MoveGoal = new WorldPos(
            IntMath.Clamp(entity.Position.X + (stepX * RetreatDistanceMm), -limit, limit),
            0,
            IntMath.Clamp(entity.Position.Z + (stepZ * RetreatDistanceMm), -limit, limit));

        entity.HasMoveGoal = true;
        entity.PathLength = 0;
        entity.PathCursor = 0;
        entity.PathFailures = 0;
        entity.NeedsPath = true;
    }
}
