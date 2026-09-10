using MiVic.Core.Numerics;
using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// Resolves targeting, firing and death.
/// <para>
/// Every step is integer arithmetic and every choice is made in ascending slot
/// order, so two machines running the same commands kill the same units on the
/// same tick. Targeting is sticky: a unit keeps its target until it dies or
/// leaves range, which keeps the per-tick cost linear instead of quadratic.
/// </para>
/// <para>
/// <b>Nobody has to be told to shoot.</b> This is also the system that fires the
/// structures: an armed building with no target acquires one the same way a tank
/// does, because a player who has to order a turret to fire will never order it —
/// the building is scenery to them until the tick it opens up on its own. The
/// order a player *can* give one is a preference rather than a command: see
/// <see cref="SimWorld"/>'s attack order, which locks a structure onto a target
/// without pretending it can drive at it.
/// </para>
/// </summary>
public static class CombatSystem
{
    /// <summary>Ticks an unarmed-of-targets unit waits before searching again.</summary>
    public const int SearchRetryTicks = 5;

    /// <summary>
    /// How far a target that has already been chosen may be and still be kept. Not
    /// <c>int.MaxValue</c>, which would square to a value no distance can exceed but is
    /// one multiplication away from an overflow somebody would have to reason about: this
    /// is a thousand kilometres, which is beyond every corner of a six-hundred-metre map.
    /// </summary>
    private const int HeldTargetReachMm = 1_000_000_000;

    /// <summary>
    /// The furthest this structure can engage anything at, right now, in millimetres — zero
    /// for a role with no gun on it or one that is still being raised.
    /// <para>
    /// It is the same chain <see cref="CanEngage"/> walks, answered as a distance instead of as
    /// a yes: the weapon's range, capped by the shooter's own eyes unless a powered radar
    /// covers the ground it stands on. A Πυροβολείο therefore reports 170 m on its own and
    /// 200 m under an umbrella, and that difference is the whole reason a Σταθμός Ραντάρ is
    /// worth 240 Π.
    /// </para>
    /// <para>
    /// It exists so the interface can draw the reach without deriving it. A ring drawn from a
    /// second copy of this arithmetic is a ring that will one day disagree with the gun — and
    /// "the circle said I could shoot it" is the kind of bug a player never forgives. The
    /// figure is the <em>furthest</em> the weapon reaches, which is the radius of a circle
    /// rather than the exact shape of the reach: under an umbrella the last few metres of it
    /// exist only where the radar also paints, and a ring that showed that would be a
    /// crescent.
    /// </para>
    /// </summary>
    public static int EngagementRadiusMm(SimWorld world, int slot)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsAliveSlot(slot))
        {
            return 0;
        }

        ref Entity entity = ref world.GetRefBySlot(slot);
        UnitDefinition weapon = UnitCatalog.Get(entity.Kind);

        if (!weapon.IsArmed || (weapon.IsBuilding && !world.IsComplete(slot)))
        {
            return 0;
        }

        int eyes = VisionSystem.SensorRadiusMm(world, in entity);

        return world.Radars.Covers(world, entity.TeamId, entity.Position)
            ? weapon.AttackRangeMm
            : Math.Min(weapon.AttackRangeMm, eyes);
    }

    /// <summary>
    /// Resolves one combat tick.
    /// </summary>
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

            // A structure that is still being raised has no gun on it yet. Everything
            // else a building does waits for it to be up — income, production, research —
            // and a gun emplacement that fired out of a half-poured foundation would be
            // the one exception, which is exactly the sort of exception a player reads as
            // a bug rather than as a rule.
            if (weapon.IsBuilding && !world.IsComplete(slot))
            {
                continue;
            }

            // Validate the current target; a dead or illegal one is dropped. A held
            // target is allowed to be out of reach — see CanEngage — because a unit
            // under an attack order is on its way to it, and the order would be thrown
            // away by the first tick the enemy stepped back. It is also allowed to be
            // one this weapon cannot currently *see*: losing sight of a target is not
            // the same as being done with it, and a gun whose radar has just gone dark
            // should go quiet for as long as the power is out and open up again when it
            // comes back, rather than forget what it was shooting at.
            if (attacker.TargetSlot >= 0 &&
                !CanEngage(world, ref attacker, attacker.TargetSlot, weapon, HeldTargetReachMm, mustSee: false))
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

            // Firing asks the same question acquisition asked, with the weapon's own range:
            // a gun shoots what it can both reach *and* see, and nothing else. The two used
            // to be a bare range test here and the full predicate there, which was harmless
            // while reach and sight could not part company — and they can now, because a
            // radar can be switched off under a gun that is already shooting.
            if (!CanEngage(world, ref attacker, attacker.TargetSlot, weapon, weapon.AttackRangeMm))
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

            // What the target is standing behind counts. Trees are cover to a man who
            // can lie in them and nothing to a tank sitting on top of them, which is the
            // same asymmetry the movement costs have: this is where infantry hold a
            // wood against armour.
            damage = ApplyCover(world, ref target, damage);

            if (weapon.ScatterMm > 0)
            {
                FireScattered(world, slot, ref attacker, weapon, damage);
            }
            else if (target.Health <= damage)
            {
                // The shot lands on the man, and what is behind him is the deck he is standing on.
                world.Bridgeworks.DamageAt(
                    world.TerrainTypes,
                    target.Position,
                    Bridgeworks.DeckDamage(damage),
                    attacker.TeamId);

                Kill(world, attacker.TargetSlot);
                attacker.TargetSlot = -1;
                attacker.HasAttackOrder = false;
            }
            else
            {
                world.Bridgeworks.DamageAt(
                    world.TerrainTypes,
                    target.Position,
                    Bridgeworks.DeckDamage(damage),
                    attacker.TeamId);

                target.Health -= damage;
            }

            // Firing gives away a position: a stealthed unit is visible to the enemy
            // for a while after every shot, which is the cost of using stealth.
            if (weapon.Stealthy)
            {
                attacker.RevealedUntilTick = world.Tick + SimWorld.StealthRevealTicks;
            }

            // Morale scales reload speed: a shaken unit is already worse, not just
            // closer to breaking. Automata have no morale at all, so they reload at
            // exactly the catalogue rate — no bonus for being unshakeable, no
            // penalty for being unmanned. Neither has a structure: a gun crew is
            // either at its post or the gun is silent, and a building that inherited
            // its faction's morale floor would reload forty per cent faster for the
            // Σοβιετικοί than for the Δυτικοί for no reason a player could see.
            int factor = weapon.IsAutomaton || weapon.IsBuilding
                ? 1_000
                : 1_500 - ((attacker.Morale.Raw * 1_000) / 65_536);

            attacker.AttackCooldown = Math.Max(1, (weapon.AttackCooldownTicks * factor) / 1_000);
        }
    }

    /// <summary>
    /// Scales damage by whatever cover the target is standing in, never below one.
    /// <para>
    /// The multiplier is not capped at "no cover": ground that hides nothing leaves the shot
    /// alone, and a crest, which is the opposite of cover, makes it land harder. One is the
    /// floor because a shot that does nothing at all reads as a bug, and because a defender
    /// who is genuinely untouchable should be untouchable by rule — out of range, or unseen —
    /// rather than by a rounding of the damage. <see cref="TerrainLayer.CoverPermille"/>
    /// guarantees a positive multiplier, so a hit can never be zeroed out here.
    /// </para>
    /// </summary>
    private static int ApplyCover(SimWorld world, ref Entity target, int damage)
    {
        MovementClass movement = UnitCatalog.Get(target.Kind).Movement;
        int cover = world.TerrainTypes.CoverAt(
            world.TerrainTypes.IndexOfWorld(target.Position.X, target.Position.Z),
            movement);

        return Math.Max(1, (damage * cover) / TerrainLayer.NoCoverPermille);
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

        // A salvo is aimed at the ground rather than at a man, so it is the weapon that cuts a
        // crossing: every block of an enemy's deck inside the radius takes the full damage.
        world.Bridgeworks.DamageArea(
            world.TerrainTypes,
            impactX,
            impactZ,
            weapon.SplashRadiusMm,
            damage,
            attacker.TeamId);

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

    /// <summary>
    /// <b>The one question targeting asks: can this attacker engage that slot?</b>
    /// <para>
    /// Everything that decides it lives here — whether the slot holds a live enemy, whether
    /// the attacker can see it at all, what class of thing the weapon is allowed to shoot,
    /// and whether it is close enough — so that "in range", "the right kind of target" and
    /// "actually visible" are one predicate rather than three tests that a caller can forget
    /// to pair. The bugs this shape prevents are the obvious ones: a gun that shoots aircraft
    /// because the air clause was left out of one of the two call sites, and an anti-aircraft
    /// emplacement that kills tanks for the same reason.
    /// </para>
    /// <para>
    /// <b>Detection is not firing range, and this is where the difference is settled.</b> A
    /// weapon reaches <paramref name="reachMm"/>, and it may fire at what it can see within
    /// that. What it can see is the sensor chain: the shooter's own eyes —
    /// <see cref="VisionSystem.SensorRadiusMm"/>, the same number the fog is drawn from — or,
    /// failing that, a friendly radar's coverage over <em>both</em> the shooter and the
    /// target. So a Πυροβολείο's 200 m of gun is worth 170 m on its own and the whole 200
    /// under a Σταθμός Ραντάρ, which is what makes a radar a multiplier for the guns around
    /// it instead of a lone sensor in a corner.
    /// </para>
    /// <para>
    /// The radar clause asks the question of the ground rather than of the shooter, and both
    /// ends of the shot have to be inside the same umbrella: a gun may shoot anything the
    /// radar paints, and nothing a radar standing next to it does not. That is deliberately
    /// stricter than "the shooter is under coverage", because the alternative lets a gun at
    /// the edge of the umbrella reach 200 m in the one direction nobody is looking.
    /// </para>
    /// </summary>
    /// <param name="world">The world both of them stand in.</param>
    /// <param name="attacker">The shooter.</param>
    /// <param name="slot">The candidate target's slot.</param>
    /// <param name="weapon">The shooter's definition, which carries range and target class.</param>
    /// <param name="reachMm">
    /// How far this question reaches. Acquisition passes the weapon's own range, because a
    /// target it cannot shoot yet is not a target. A target already chosen is asked about at
    /// <see cref="HeldTargetReachMm"/> instead, because holding and choosing are different
    /// questions sharing one body: the held target may legitimately be out of reach while
    /// the attacker closes on it.
    /// </param>
    /// <param name="mustSee">
    /// Whether the sensor chain has to be satisfied. True for everything a weapon is about to
    /// do — acquiring and firing — and false for the one question that is only about
    /// remembering: a unit under an attack order keeps its target while it cannot see it,
    /// because the order outlives the radar that was helping it.
    /// </param>
    private static bool CanEngage(
        SimWorld world,
        ref Entity attacker,
        int slot,
        in UnitDefinition weapon,
        int reachMm,
        bool mustSee = true)
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

        // A stealthed enemy is not a target until it fires or something gets close
        // enough to detect it. The answer comes from the visibility grid, which is the
        // same grid the fog is built from, so a cell the player can see is a cell the
        // guns can shoot into and the two cannot drift apart.
        if (world.IsHiddenFrom(attacker.TeamId, slot))
        {
            return false;
        }

        // What the weapon can reach, which is two questions rather than one. A tank
        // gun cannot be pointed at an aeroplane; an anti-aircraft mount that can
        // shoot a tank is not a specialist weapon, it is the tank's replacement, and
        // the roster says which of the two a role is rather than leaving the client
        // or the balance table to guess.
        if (!(UnitCatalog.Flies(target.Kind) ? weapon.CanHitAir : weapon.CanHitGround))
        {
            return false;
        }

        if (!InRange(ref attacker, ref target, reachMm))
        {
            return false;
        }

        return !mustSee || InSensorChain(world, ref attacker, target.Position);
    }

    /// <summary>
    /// True when the point a weapon is aiming at is in view of the side that owns the weapon:
    /// inside the shooter's own eyes, or inside a powered radar's coverage that covers the
    /// shooter as well.
    /// <para>
    /// Both halves go through <see cref="VisionSystem"/> and <see cref="PowerSystem"/>, which
    /// own the two numbers respectively — one role's sensor radius, and which radars the grid
    /// can run. Nothing here re-derives either, which is what keeps "how far can this gun
    /// shoot" and "what can this team see" the same question asked twice rather than two
    /// questions that happen to have similar answers.
    /// </para>
    /// </summary>
    private static bool InSensorChain(SimWorld world, ref Entity attacker, WorldPos target)
    {
        int sensor = VisionSystem.SensorRadiusMm(world, in attacker);
        int dx = attacker.Position.X - target.X;
        int dz = attacker.Position.Z - target.Z;

        if (((long)dx * dx) + ((long)dz * dz) <= (long)sensor * sensor)
        {
            return true;
        }

        return world.Radars.Covers(world, attacker.TeamId, attacker.Position) &&
               world.Radars.Covers(world, attacker.TeamId, target);
    }

    /// <summary>
    /// Picks the nearest legal enemy in range. The spatial index limits the scan
    /// to the cells the weapon can actually reach, instead of every entity on the
    /// map — the difference between a linear and a quadratic tick.
    /// <para>
    /// <b>The tie-break is the lowest slot.</b> The index hands cells back in its own
    /// order and each cell's contents in ascending slot order, so "nearest, and among
    /// equals the lowest slot" is the answer an ascending slot-order scan of the whole
    /// map would give — without paying for that scan. It matters because two enemies
    /// are exactly equidistant far more often than arithmetic suggests: a defender
    /// shooting down a road at two trucks nose to tail, or a battery on a grid, sees
    /// ties every few seconds, and "which one" has to be the same answer on both
    /// machines even though the cell a candidate sits in depends on the terrain.
    /// Nothing outside the world is consulted: no spawn order, no seed, no clock.
    /// </para>
    /// <para>
    /// The only test a candidate is put through is <see cref="CanEngage"/>, which is
    /// where the range and the target class are decided. A scan that tested range here
    /// as well would be a second copy of a rule that already exists one method up.
    /// </para>
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
                    if (candidate == slot ||
                        !CanEngage(world, ref attacker, candidate, weapon, weapon.AttackRangeMm))
                    {
                        continue;
                    }

                    ref Entity target = ref world.GetRefBySlot(candidate);
                    long distance = attacker.Position.DistanceSquaredTo(target.Position);

                    // Strictly nearer, or equally near and lower-numbered: the second
                    // clause is the tie-break, and without it the winner depends on
                    // which cell the index happened to visit first.
                    if (distance < bestDistance || (distance == bestDistance && candidate < best))
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
    /// <para>
    /// <b>There is no line of sight in this engine, and that is a known simplification rather
    /// than an oversight.</b> An emplacement on one side of a ridge can shoot a target on the
    /// other side of it: height is a term in the cover arithmetic and nowhere else, and the
    /// landform fields the terrain layer already computes are what a sight test would be built
    /// from when somebody builds one. Until then the rule a player can rely on is "in range is
    /// in range", which is at least a rule they can predict.
    /// </para>
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
