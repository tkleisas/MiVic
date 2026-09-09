using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>
/// Resolves targeting, firing and death.
/// <para>
/// Every step is integer arithmetic and every choice is made in ascending slot
/// order, so two machines running the same commands kill the same units on the
/// same tick. Targeting is sticky: a unit keeps its target until it dies or
/// leaves range, which keeps the per-tick cost linear instead of quadratic.
/// </para>
/// </summary>
public static class CombatSystem
{
    /// <summary>Ticks an unarmed-of-targets unit waits before searching again.</summary>
    public const int SearchRetryTicks = 5;

    /// <summary>Runs one combat tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        int capacity = world.Capacity;

        // Casualties decay so a fight two minutes ago no longer shakes morale.
        // One casualty is forgotten every two seconds, which keeps a bad
        // engagement painful long enough to matter but not permanent.
        if (world.Tick % 40 == 0)
        {
            for (int team = 0; team < SimConstants.TeamCount; team++)
            {
                ref TeamState state = ref world.TeamRef(team);

                if (state.RecentCasualties > 0)
                {
                    state.RecentCasualties--;
                }
            }
        }

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity attacker = ref world.GetRefBySlot(slot);
            UnitDefinition weapon = UnitCatalog.Get(attacker.Kind);

            if (!weapon.IsArmed || attacker.Routed)
            {
                continue;
            }

            // Validate the current target; a dead or illegal one is dropped.
            if (attacker.TargetSlot >= 0 && !IsEnemyAndTargetable(world, ref attacker, attacker.TargetSlot, weapon))
            {
                attacker.TargetSlot = -1;
                attacker.HasAttackOrder = false;
            }

            // Acquiring a target is the expensive part — a grid query over the
            // weapon's range — so a unit with nothing to shoot at waits a few
            // ticks before looking again instead of scanning every tick.
            if (attacker.TargetSlot < 0)
            {
                if (attacker.AttackCooldown > 0)
                {
                    attacker.AttackCooldown--;
                    continue;
                }

                attacker.TargetSlot = AcquireTarget(world, slot, ref attacker, weapon);

                if (attacker.TargetSlot < 0)
                {
                    attacker.AttackCooldown = SearchRetryTicks;
                    continue;
                }
            }

            ref Entity target = ref world.GetRefBySlot(attacker.TargetSlot);

            if (!InRange(ref attacker, ref target, weapon.AttackRangeMm))
            {
                // An ordered attack closes the distance; auto-acquired targets do
                // not drag a unit across the map.
                if (attacker.HasAttackOrder && world.Tick % 10 == 0)
                {
                    attacker.MoveGoal = target.Position;
                    attacker.HasMoveGoal = true;
                    attacker.PathLength = 0;
                    attacker.PathCursor = 0;
                    attacker.PathFailures = 0;
                    attacker.NeedsPath = true;
                }

                continue;
            }

            if (attacker.AttackCooldown > 0)
            {
                attacker.AttackCooldown--;
                continue;
            }

            // Completed research scales damage per team.
            TeamState attackerTeam = world.Team(attacker.TeamId);
            int damageScale = attackerTeam.DamagePermille > 0 ? attackerTeam.DamagePermille : 1_000;
            int damage = Math.Max(1, (weapon.AttackDamage * damageScale) / 1_000);

            if (weapon.ScatterMm > 0)
            {
                FireScattered(world, slot, ref attacker, weapon, damage);
            }
            else if (target.Health <= damage)
            {
                Kill(world, attacker.TargetSlot);
                attacker.TargetSlot = -1;
                attacker.HasAttackOrder = false;
            }
            else
            {
                target.Health -= damage;
            }

            // Morale scales reload speed: a shaken unit is already worse, not just
            // closer to breaking. Automata have no morale at all, so they reload at
            // exactly the catalogue rate — no bonus for being unshakeable, no
            // penalty for being unmanned.
            int factor = weapon.IsAutomaton
                ? 1_000
                : 1_500 - ((attacker.Morale.Raw * 1_000) / 65_536);

            attacker.AttackCooldown = Math.Max(1, (weapon.AttackCooldownTicks * factor) / 1_000);
        }
    }

    /// <summary>
    /// Fires an inaccurate salvo at a target's position.
    /// <para>
    /// The impact point is offset from the target by a deterministic pseudo-random
    /// vector derived from the tick and the two slots, so a salvo scatters the same
    /// way in a replay without needing a random source in the hot loop. Every
    /// hostile entity inside the splash radius takes full damage, and the intended
    /// target may be missed entirely — which is the whole point of the Κατιούσα.
    /// </para>
    /// </summary>
    private static void FireScattered(
        SimWorld world,
        int slot,
        ref Entity attacker,
        in UnitDefinition weapon,
        int damage)
    {
        ref Entity target = ref world.GetRefBySlot(attacker.TargetSlot);

        int seed = (int)(world.Tick & 0xFFFF) ^ (slot << 8) ^ (attacker.TargetSlot * 31);
        int impactX = target.Position.X + ScatterOffset(seed, weapon.ScatterMm);
        int impactZ = target.Position.Z + ScatterOffset(seed + 7_919, weapon.ScatterMm);

        long radiusSquared = (long)weapon.SplashRadiusMm * weapon.SplashRadiusMm;
        int capacity = world.Capacity;

        for (int other = 0; other < capacity; other++)
        {
            if (!world.IsAliveSlot(other))
            {
                continue;
            }

            ref Entity victim = ref world.GetRefBySlot(other);

            if (victim.TeamId == attacker.TeamId)
            {
                continue;
            }

            int dx = victim.Position.X - impactX;
            int dz = victim.Position.Z - impactZ;

            if (((long)dx * dx) + ((long)dz * dz) > radiusSquared)
            {
                continue;
            }

            if (victim.Health <= damage)
            {
                Kill(world, other);
            }
            else
            {
                victim.Health -= damage;
            }
        }

        // The blast may have killed the sticky target, or missed it entirely.
        if (attacker.TargetSlot >= 0 && !world.IsAliveSlot(attacker.TargetSlot))
        {
            attacker.TargetSlot = -1;
            attacker.HasAttackOrder = false;
        }
    }

    /// <summary>
    /// An integer hash in <c>[-magnitude, magnitude]</c>. Deliberately not a PRNG
    /// object: the combat loop must not allocate, and the value has to depend only
    /// on simulation state so replays match.
    /// </summary>
    private static int ScatterOffset(int seed, int magnitude)
    {
        if (magnitude <= 0)
        {
            return 0;
        }

        int hash = (seed * 1_103_515_245) + 12_345;
        hash ^= hash >> 13;
        hash *= 0x5BD1E995;
        hash ^= hash >> 15;

        int span = (magnitude * 2) + 1;
        return ((hash & 0x7FFFFFFF) % span) - magnitude;
    }

    /// <summary>True when the slot holds a live enemy this weapon may engage.</summary>
    private static bool IsEnemyAndTargetable(SimWorld world, ref Entity attacker, int slot, in UnitDefinition weapon)
    {
        if (!world.IsAliveSlot(slot))
        {
            return false;
        }

        ref Entity target = ref world.GetRefBySlot(slot);

        if (target.TeamId == attacker.TeamId)
        {
            return false;
        }

        return !UnitCatalog.Flies(target.Kind) || weapon.CanHitAir;
    }

    /// <summary>
    /// Picks the nearest legal enemy in range. The spatial index limits the scan
    /// to the cells the weapon can actually reach, instead of every entity on the
    /// map — the difference between a linear and a quadratic tick.
    /// </summary>
    private static int AcquireTarget(SimWorld world, int slot, ref Entity attacker, in UnitDefinition weapon)
    {
        int best = -1;
        long bestDistance = long.MaxValue;
        SpatialIndex index = world.Spatial;

        int minX = index.CoordinateOf(attacker.Position.X - weapon.AttackRangeMm);
        int maxX = index.CoordinateOf(attacker.Position.X + weapon.AttackRangeMm);
        int minZ = index.CoordinateOf(attacker.Position.Z - weapon.AttackRangeMm);
        int maxZ = index.CoordinateOf(attacker.Position.Z + weapon.AttackRangeMm);

        for (int cellZ = minZ; cellZ <= maxZ; cellZ++)
        {
            for (int cellX = minX; cellX <= maxX; cellX++)
            {
                foreach (int candidate in index.Cell(index.IndexOf(cellX, cellZ)))
                {
                    if (candidate == slot || !IsEnemyAndTargetable(world, ref attacker, candidate, weapon))
                    {
                        continue;
                    }

                    ref Entity target = ref world.GetRefBySlot(candidate);

                    if (!InRange(ref attacker, ref target, weapon.AttackRangeMm))
                    {
                        continue;
                    }

                    long distance = attacker.Position.DistanceSquaredTo(target.Position);

                    if (distance < bestDistance)
                    {
                        bestDistance = distance;
                        best = candidate;
                    }
                }
            }
        }

        return best;
    }

    /// <summary>
    /// Range is measured horizontally: a tank on a hill can still shoot a tank in
    /// the valley, and aircraft are engaged by horizontal distance at altitude.
    /// </summary>
    private static bool InRange(ref Entity attacker, ref Entity target, int rangeMm)
    {
        int dx = attacker.Position.X - target.Position.X;
        int dz = attacker.Position.Z - target.Position.Z;

        return ((long)dx * dx) + ((long)dz * dz) <= (long)rangeMm * rangeMm;
    }

    /// <summary>Removes a destroyed entity and records the loss for morale.</summary>
    private static void Kill(SimWorld world, int slot)
    {
        ref Entity victim = ref world.GetRefBySlot(slot);

        if ((uint)victim.TeamId < SimConstants.TeamCount)
        {
            // Losing a structure hurts morale far more than losing a scout.
            ref TeamState state = ref world.TeamRef(victim.TeamId);
            state.RecentCasualties += victim.Kind is UnitKind.CommandCentre ? 8 : 1;
        }

        world.Despawn(new EntityId(slot, victim.Generation));
    }
}
