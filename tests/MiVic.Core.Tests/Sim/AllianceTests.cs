using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Core.Terrain;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// The allied pair: teams 0 and 1, which stand on the same side in every scenario this
/// game ships. "An ally is not a target" has to hold in every path by which damage reaches
/// a unit — direct fire, artillery splash, an attack order, off-map support — and it did
/// not: the guns compared team ids, so the two allies shot each other in every match while
/// the crossing rule beside them spared an ally's bridge, because that one had been written
/// by asking about the alliance.
/// <para>
/// The fixtures are chosen so that each test can only pass for the right reason. The
/// artillery one puts the ally and the enemy at the <em>same position</em>, so a single
/// salvo proves both halves: the blast reached that square (the enemy lost health) and the
/// same blast spared the unit standing on it (the ally did not) — the two are exactly
/// equidistant from the impact, so the radius test cannot have answered differently for
/// them unless the friend-or-foe rule did.
/// </para>
/// </summary>
public sealed class AllianceTests
{
    /// <summary>Capacity of a skirmish, which the standard match is built at.</summary>
    private const int SkirmishCapacity = 1024;

    private static EntityId Spawn(
        SimWorld world,
        Faction faction,
        int team,
        UnitKind kind,
        WorldPos position,
        int health = 0)
        => world.Spawn(
            faction,
            team,
            kind,
            world.LegalSpawnSite(position),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            health > 0 ? health : UnitCatalog.Get(kind).Health);

    /// <summary>A point on solid ground, so a fixture is never quietly moved by Spawn.</summary>
    private static WorldPos Clearing(SimWorld world)
        => world.Navigation.CentreOf(world.Navigation.NearestWalkable(0));

    private static WorldPos Offset(WorldPos from, int millimetres)
        => new(from.X + millimetres, 0, from.Z);

    [Fact]
    public void TeamsZeroAndOneAreAlliedAndTeamTwoIsNot()
    {
        // The premise of every test in this file, asserted rather than assumed: if the
        // alliance ever changes, these tests stop meaning what they say. It is the standard
        // skirmish's own answer — the sides a match declares, not a law about team numbers —
        // which is why it is asked of a world.
        SimWorld world = new(seed: 1, capacity: 4);

        Assert.Equal(MatchRoster.StandardSkirmish, world.Roster);

        Assert.True(world.AreAllied(0, 1));
        Assert.True(world.AreAllied(1, 0));
        Assert.False(world.IsHostile(0, 1));
        Assert.False(world.IsHostile(1, 0));

        Assert.True(world.IsHostile(0, 2));
        Assert.True(world.IsHostile(1, 2));
        Assert.True(world.IsHostile(2, 0));
        Assert.True(world.IsHostile(2, 1));

        // A team is never at war with itself, which is the one thing the comparison of
        // team ids got right and the reason the mistake was invisible.
        Assert.False(world.IsHostile(1, 1));
        Assert.False(world.IsHostile(2, 2));
    }

    /// <summary>
    /// Two allied tanks inside each other's killing range, with acquisition free to choose
    /// and nothing else on the map to choose: ten seconds of a weapon searching every five
    /// ticks finds nothing, because the only thing it could find is on its own side.
    /// </summary>
    [Fact]
    public void TwoAlliedUnitsInKillingRangeDoNotFireAtEachOther()
    {
        SimWorld world = new(seed: 20250101, capacity: 64);
        WorldPos centre = Clearing(world);

        EntityId soviet = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, centre);
        EntityId chinese = Spawn(world, Faction.Chinese, 1, UnitKind.Tank, Offset(centre, 60_000));

        int health = UnitCatalog.Get(UnitKind.Tank).Health;
        int reach = UnitCatalog.Get(UnitKind.Tank).AttackRangeMm;
        int gap = world.GetRefBySlot(soviet.Slot).Position.HorizontalDistanceTo(
            world.GetRefBySlot(chinese.Slot).Position);

        // Without this the test could pass by putting them out of range of each other,
        // which is not the rule under test.
        Assert.True(gap <= reach, $"The two allies are {gap} mm apart, which is outside the {reach} mm tank range.");

        world.RunTicks(200);

        Assert.Equal(-1, world.GetRefBySlot(soviet.Slot).TargetSlot);
        Assert.Equal(-1, world.GetRefBySlot(chinese.Slot).TargetSlot);
        Assert.Equal(health, world.GetRefBySlot(soviet.Slot).Health);
        Assert.Equal(health, world.GetRefBySlot(chinese.Slot).Health);
    }

    /// <summary>
    /// The same pair with an enemy behind the ally: acquisition has a choice, and what it
    /// picks is the enemy. Every tick, so a target found on the first tick and held by a gun
    /// that never re-acquires cannot hide a wrong one.
    /// </summary>
    [Fact]
    public void AnAllyInRangeIsSkippedForTheEnemyBehindIt()
    {
        SimWorld world = new(seed: 20250101, capacity: 64);
        WorldPos centre = Clearing(world);

        EntityId shooter = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, centre);
        EntityId ally = Spawn(world, Faction.Chinese, 1, UnitKind.Tank, Offset(centre, 40_000));

        // A headquarters as the enemy: 5 000 hit points and no gun on it, so the only thing
        // that can take health off the ally is the shooter beside it. An armed enemy would
        // make the assertion below meaningless — it would shoot the nearer allied tank and
        // the test would read that as friendly fire.
        EntityId enemy = Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, Offset(centre, 90_000));

        int allyHealth = world.GetRefBySlot(ally.Slot).Health;
        int enemyHealth = world.GetRefBySlot(enemy.Slot).Health;

        for (int tick = 0; tick < 120; tick++)
        {
            world.Step();

            // The ally is *nearer*, so a weapon that sorts candidates by distance rather than
            // by side picks it — which is the whole of the bug this pins.
            Assert.True(
                world.GetRefBySlot(shooter.Slot).TargetSlot == enemy.Slot,
                $"On tick {tick + 1} the shooter was aiming at slot " +
                $"{world.GetRefBySlot(shooter.Slot).TargetSlot}, not the enemy at slot {enemy.Slot}.");

            Assert.True(
                world.GetRefBySlot(ally.Slot).Health == allyHealth,
                $"On tick {tick + 1} the shooter had damaged its ally.");
        }

        Assert.True(
            world.GetRefBySlot(enemy.Slot).Health < enemyHealth,
            "The shooter never hit the enemy it picked, so the run proves only that it was idle.");
    }

    /// <summary>
    /// An ally and an enemy standing on the same square, under a Κατιούσα salvo. The salvo
    /// scatters, so whether any given shell lands on that square is a coin toss; over four
    /// hundred ticks one does, and that single event is the whole test — the two units are
    /// the same distance from the same impact, so the only thing that can spare one and not
    /// the other is the side it is on.
    /// </summary>
    [Fact]
    public void AnAlliedSalvoSparesTheAllyStandingOnTheTarget()
    {
        SimWorld world = new(seed: 4711, capacity: 64);
        WorldPos site = Clearing(world);

        // Unarmed victims on purpose: with weapons on the field the two of them would shoot
        // back and the salvo would not be the only thing doing damage. 5 000 hit points each
        // so the measurement window is about the blast and not about survival.
        Spawn(world, Faction.Soviet, 0, UnitKind.RocketArtillery, site);
        EntityId ally = Spawn(world, Faction.Chinese, 1, UnitKind.Commissar, site, health: 5_000);
        EntityId enemy = Spawn(world, Faction.Western, 2, UnitKind.Commissar, site, health: 5_000);

        world.RunTicks(400);

        Assert.True(
            world.GetRefBySlot(enemy.Slot).Health < 5_000,
            "No salvo landed on the target's own square, so this run proves nothing about who a blast spares.");

        Assert.Equal(5_000, world.GetRefBySlot(ally.Slot).Health);
    }

    /// <summary>
    /// The orbital strike is the ability that <em>does</em> distinguish friend from foe, and
    /// an ally inside its radius is a friend. The order itself is legitimate all the same:
    /// it was called on a position, and calling fire down on a piece of ground is not an
    /// attack on anybody — which is why the ally standing there does not make it illegal, it
    /// makes it harmless.
    /// </summary>
    [Fact]
    public void AStrikeCalledOntoAnAllyIsLegalAndSparesIt()
    {
        SimWorld world = SovietWithOrbital();
        WorldPos centre = Clearing(world);

        EntityId ally = Spawn(world, Faction.Chinese, 1, UnitKind.Commissar, Offset(centre, 20_000), health: 5_000);
        EntityId enemy = Spawn(world, Faction.Western, 2, UnitKind.Commissar, Offset(centre, -20_000), health: 5_000);

        int materials = world.Team(0).Materials;

        world.Enqueue(SimCommand.UseAbility(AbilityId.OrbitalStrike, centre, world.Tick + 1, 0));
        world.Step();

        // The order was taken, not refused for want of a legal victim: it was paid for.
        Assert.True(world.Team(0).Materials < materials, "The strike was refused, so nothing here was tested.");

        Assert.Equal(5_000, world.GetRefBySlot(ally.Slot).Health);
        Assert.True(world.GetRefBySlot(enemy.Slot).Health < 5_000, "The strike missed the enemy.");
    }

    /// <summary>
    /// A strike on empty ground — no enemy, no ally, nothing — is a legal order that does
    /// nothing, which is the half of "attacking the ground is not an attack on anyone" that
    /// a friend-or-foe check placed on the wrong side of the blast would break.
    /// </summary>
    [Fact]
    public void AStrikeOnEmptyGroundIsStillAnOrder()
    {
        SimWorld world = SovietWithOrbital();
        WorldPos empty = Offset(Clearing(world), 200_000);

        int materials = world.Team(0).Materials;

        world.Enqueue(SimCommand.UseAbility(AbilityId.OrbitalStrike, empty, world.Tick + 1, 0));
        world.Step();

        AbilityCatalog.TryGet(AbilityId.OrbitalStrike, out AbilityDefinition definition);

        Assert.Equal(materials - definition.MaterialCost, world.Team(0).Materials);
        Assert.False(world.CanUseAbility(0, AbilityId.OrbitalStrike, out _));
    }

    /// <summary>
    /// The design decision this must not remove: an ability documented as
    /// <see cref="AbilityDefinition.DamagesFriendlies"/> hits everyone in its radius, its own
    /// side included, and an ally is its own side. Nothing about the alliance rule touches
    /// that clause — the check is skipped for such an ability rather than asked differently —
    /// and this is the test that says so, in the language of the alliance rather than in the
    /// language of team ids. Compare
    /// <see cref="AbilityTests.ANukeDoesNotDistinguishFriendFromFoe"/>, which pins the same
    /// decision on the caster's own team.
    /// </summary>
    [Fact]
    public void ANukeStillHurtsTheCastersAlly()
    {
        // The nuke names no faction, so team 0 may call one — which is the only way to aim a
        // DamagesFriendlies ability at a team that actually has an ally.
        SimWorld world = new(seed: 14, capacity: 32);
        WorldPos site = Clearing(world);

        Spawn(world, Faction.Soviet, 0, UnitKind.NuclearPlant, site);

        ref TeamState state = ref world.TeamRef(0);
        state.TechTier = 4;
        state.Materials = 10_000;

        EntityId ally = Spawn(world, Faction.Chinese, 1, UnitKind.Commissar, Offset(site, 20_000), health: 5_000);
        EntityId enemy = Spawn(world, Faction.Western, 2, UnitKind.Commissar, Offset(site, -20_000), health: 5_000);

        AbilityCatalog.TryGet(AbilityId.TacticalNuke, out AbilityDefinition nuke);

        Assert.True(nuke.DamagesFriendlies);
        Assert.True(world.CanUseAbility(0, AbilityId.TacticalNuke, out string reason), reason);

        world.Enqueue(SimCommand.UseAbility(AbilityId.TacticalNuke, site, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.GetRefBySlot(ally.Slot).Health < 5_000, "The nuke spared its caster's ally.");
        Assert.True(world.GetRefBySlot(enemy.Slot).Health < 5_000, "The nuke missed the enemy.");
    }

    /// <summary>
    /// <b>A held target is re-asked about, not remembered as an enemy.</b>
    /// <para>
    /// Alliances are fixed at compile time today — <see cref="SimWorld.AreAllied"/> is a pure
    /// function of two team ids — so a test cannot flip one without building the feature. What
    /// it can do is move a unit onto the other side of the line, which is the same fact the
    /// predicate reads, and ask what happens to the target the other one is already holding.
    /// </para>
    /// <para>
    /// The answer has to be "it goes quiet on the next tick", and it is, because
    /// <c>CombatSystem</c> validates a target it already holds with the same
    /// <c>CanEngage</c> that acquired it, on every tick, before firing. An alliance clause
    /// inside that predicate therefore inherits the held-target re-validation the sensor chain
    /// already relies on: a gun whose radar dies goes quiet and resumes, and a gun whose enemy
    /// becomes an ally stops shooting rather than quietly shooting a friend for as long as the
    /// order stands. Anything that cached "this target is an enemy" — on the entity, in the
    /// order, or in a set of allies kept beside it — would pass every other test in this file
    /// and fail only this one.
    /// </para>
    /// </summary>
    [Fact]
    public void AWeaponStopsShootingWhenItsTargetStopsBeingAnEnemy()
    {
        SimWorld world = new(seed: 20250101, capacity: 64);
        WorldPos centre = Clearing(world);

        EntityId shooter = Spawn(world, Faction.Soviet, 0, UnitKind.Tank, centre);
        EntityId target = Spawn(world, Faction.Western, 2, UnitKind.Tank, Offset(centre, 60_000), health: 5_000);

        world.RunTicks(60);

        Assert.Equal(target.Slot, world.GetRefBySlot(shooter.Slot).TargetSlot);
        Assert.True(
            world.GetRefBySlot(target.Slot).Health < 5_000,
            "The two never fought, so there was no held target to re-validate.");

        // The flip. A team id is part of the state hash, so this is a change of the same kind
        // a real alliance change would be: the sides the predicate reads, moved.
        world.GetRefBySlot(target.Slot).TeamId = 1;
        int healthAtTheFlip = world.GetRefBySlot(target.Slot).Health;

        Assert.False(world.IsHostile(0, 1));

        world.Step();

        Assert.Equal(-1, world.GetRefBySlot(shooter.Slot).TargetSlot);
        Assert.False(world.GetRefBySlot(shooter.Slot).HasAttackOrder);

        world.RunTicks(60);

        Assert.Equal(healthAtTheFlip, world.GetRefBySlot(target.Slot).Health);
    }

    /// <summary>
    /// <b>The test that would have caught this.</b> The standard skirmish, nobody playing, four
    /// hundred ticks of it — teams 0 and 1 allied, team 2 against them, which is the match every
    /// player actually starts.
    /// <para>
    /// Two things are watched on every tick, and they are two different doors. The first is
    /// what every armed unit is pointing at: a target of an allied team is the bug itself, and
    /// no weapon has any business holding one. The second is damage: every point of health lost
    /// and every unit lost has to be explained by fire from a team hostile to the victim on that
    /// same tick, which is the door artillery comes through — a scattered salvo aimed at an
    /// enemy can catch an ally without ever naming one, and the targeted check cannot see it.
    /// </para>
    /// <para>
    /// <b>The seed is 4711 and not the skirmish's usual 20250101, because of what the witness
    /// below asks for.</b> The two allied armies start far apart on most seeds and only meet
    /// when the AI's attack route carries one of them past the other, which on the canonical
    /// seed happens — under the *broken* rule and only there, because friendly fire is what
    /// dragged the two formations into each other. With the fix the forces never come within
    /// weapon reach on that seed at all, and a test that runs there would pass without ever
    /// having had the opportunity to fail. On this seed they are in reach of each other from
    /// tick 40, and the broken rule turned that into 62 116 ticks of an allied unit holding an
    /// allied target over the same 1 600 ticks.
    /// </para>
    /// <para>
    /// That is also why the witness is asserted: if the two allied forces stop coming within
    /// reach of one another, this test fails and says so, rather than quietly becoming a test
    /// of nothing. An encounter is sampled once a second — a formation in reach for one tick
    /// and gone is not an encounter, and a match's worth of them is not worth a spatial query
    /// on every unit on every tick.
    /// </para>
    /// <para>
    /// Lava is the one exception to the damage clause, and it is asked about rather than
    /// assumed: it has no owner and burns whoever stands in it, so damage taken on a lava cell
    /// is counted as terrain rather than as a shot. The run is also required to have been a
    /// battle — the allies hitting team 2, team 2 hitting back, and the allied pair being hit
    /// by team 2 — because a match in which nothing happened would satisfy every clause above
    /// while proving nothing.
    /// </para>
    /// </summary>
    [Fact]
    public void TheStandardSkirmishNeverDamagesAnAlly()
    {
        SimWorld world = new(seed: 4711, capacity: SkirmishCapacity);
        Scenario.Build(world, ScenarioKind.Skirmish);

        int capacity = world.Capacity;
        var previousCooldown = new int[capacity];
        var previousHealth = new int[capacity];
        var wasAlive = new bool[capacity];
        var firedTeams = new int[capacity];

        for (int slot = 0; slot < capacity; slot++)
        {
            wasAlive[slot] = world.IsAliveSlot(slot);
            previousHealth[slot] = wasAlive[slot] ? world.GetRefBySlot(slot).Health : 0;
        }

        int alliedTargets = 0;
        int unexplained = 0;
        int hazardDamage = 0;
        int shotsAtTeamTwo = 0;
        int shotsByTeamTwo = 0;
        int damageOnTeamTwo = 0;
        int damageOnAllies = 0;
        int firstUnexplainedTick = -1;
        int inReachTicks = 0;
        int firstInReachTick = -1;

        const int Ticks = 400;

        // One second of game time: the encounter witness is a statement about the match, not
        // about a tick, and asking it of all five hundred units on every tick would cost more
        // than the rest of this test put together.
        const int EncounterInterval = 20;

        for (int tick = 0; tick < Ticks; tick++)
        {
            world.Step();

            // First every weapon that fired, because damage anywhere on the field has to be
            // matched against the whole tick's fire rather than against the slots scanned so
            // far. A weapon fires exactly when its cooldown was zero last tick and is not now
            // — the same detection the client draws tracers from — and a search that found
            // nothing leaves no target behind, which is what the target clause excludes.
            int fired = 0;

            for (int slot = 0; slot < capacity; slot++)
            {
                if (!world.IsAliveSlot(slot))
                {
                    previousCooldown[slot] = 0;
                    continue;
                }

                ref Entity shooter = ref world.GetRefBySlot(slot);
                UnitDefinition weapon = UnitCatalog.Get(shooter.Kind);
                bool armed = weapon.IsArmed && !shooter.Routed && (!weapon.IsBuilding || world.IsComplete(slot));

                if (armed && shooter.TargetSlot >= 0)
                {
                    int targetTeam = world.GetRefBySlot(shooter.TargetSlot).TeamId;

                    if (!world.IsHostile(shooter.TeamId, targetTeam))
                    {
                        alliedTargets++;
                    }

                    if (targetTeam == 2)
                    {
                        shotsAtTeamTwo++;
                    }
                }

                if (armed && previousCooldown[slot] == 0 && shooter.AttackCooldown > 0)
                {
                    firedTeams[fired++] = shooter.TeamId;

                    if (shooter.TeamId == 2)
                    {
                        shotsByTeamTwo++;
                    }
                }

                if (armed && tick % EncounterInterval == 0 &&
                    HasAllyInReach(world, slot, ref shooter, weapon))
                {
                    inReachTicks++;

                    if (firstInReachTick < 0)
                    {
                        firstInReachTick = tick;
                    }
                }

                previousCooldown[slot] = shooter.AttackCooldown;
            }

            for (int slot = 0; slot < capacity; slot++)
            {
                bool alive = world.IsAliveSlot(slot);

                if (alive)
                {
                    ref Entity victim = ref world.GetRefBySlot(slot);
                    int health = victim.Health;
                    bool hurt = wasAlive[slot] && health < previousHealth[slot];

                    if (hurt)
                    {
                        if (IsLava(world, victim.Position))
                        {
                            hazardDamage++;
                        }
                        else if (!ExplainedByHostileFire(world, firedTeams, fired, victim.TeamId))
                        {
                            // Damage no hostile weapon can account for. On an ally this is
                            // friendly fire, whether it was aimed at one or scattered onto
                            // one; anywhere else it is a hole in the accounting.
                            unexplained++;

                            if (firstUnexplainedTick < 0)
                            {
                                firstUnexplainedTick = tick;
                            }
                        }

                        if (victim.TeamId == 2)
                        {
                            damageOnTeamTwo++;
                        }
                        else
                        {
                            damageOnAllies++;
                        }
                    }

                    previousHealth[slot] = health;
                }
                else if (wasAlive[slot])
                {
                    // A death is a health loss that arrived all at once: the combat system
                    // despawns a unit instead of subtracting the last of its hit points.
                    ref Entity fallen = ref world.GetRefBySlot(slot);

                    if (!IsLava(world, fallen.Position) &&
                        !ExplainedByHostileFire(world, firedTeams, fired, fallen.TeamId))
                    {
                        unexplained++;

                        if (firstUnexplainedTick < 0)
                        {
                            firstUnexplainedTick = tick;
                        }
                    }

                    if (fallen.TeamId == 2)
                    {
                        damageOnTeamTwo++;
                    }
                    else
                    {
                        damageOnAllies++;
                    }

                    previousHealth[slot] = 0;
                }

                wasAlive[slot] = alive;
            }
        }

        Assert.Equal(0, alliedTargets);
        Assert.True(
            unexplained == 0,
            $"{unexplained} damage events in {Ticks} ticks were not explained by hostile fire, " +
            $"the first on tick {firstUnexplainedTick}; {hazardDamage} were terrain damage.");

        // The witness. Without it every clause above is satisfied by a match in which the two
        // allied armies never came near each other, which is most seeds and was this test's
        // first version — it passed against the broken rule, which is the one thing a test for
        // this bug must not do.
        Assert.True(
            inReachTicks > 0,
            $"No armed unit of team 0 or team 1 ever had an armed ally of the other team within " +
            $"its weapon range in {Ticks} ticks, so this run proves nothing.");

        // A battle, or the three clauses above are satisfied by a map nobody is fighting on:
        // the allies hit team 2, team 2 hit back, and the allied pair was hit by team 2 rather
        // than only by each other — which is the "damage between them and team 2 continues"
        // half of the claim.
        Assert.True(shotsAtTeamTwo > 0, $"No weapon of team 0 or 1 was ever aimed at team 2 in {Ticks} ticks.");
        Assert.True(shotsByTeamTwo > 0, $"Team 2 fired no shot in {Ticks} ticks.");
        Assert.True(damageOnTeamTwo > 0, $"Team 2 took no damage in {Ticks} ticks.");
        Assert.True(damageOnAllies > 0, $"The allied pair took no damage in {Ticks} ticks.");
    }

    /// <summary>
    /// True when an armed unit of the other allied team is inside this weapon's reach: the
    /// encounter the whole test depends on having happened. It is a range test rather than the
    /// full <c>CanEngage</c> — reach is the part the two armies' positions decide, and asking
    /// for visibility as well would make the witness fail on a stealth unit the shooter simply
    /// could not see, which is not the same as the armies never having met.
    /// </summary>
    private static bool HasAllyInReach(SimWorld world, int slot, ref Entity shooter, UnitDefinition weapon)
    {
        if (shooter.TeamId is not (0 or 1))
        {
            return false;
        }

        SpatialIndex index = world.Spatial;
        int range = weapon.AttackRangeMm;
        int minX = index.CoordinateOf(shooter.Position.X - range);
        int maxX = index.CoordinateOf(shooter.Position.X + range);
        int minZ = index.CoordinateOf(shooter.Position.Z - range);
        int maxZ = index.CoordinateOf(shooter.Position.Z + range);

        for (int cellZ = minZ; cellZ <= maxZ; cellZ++)
        {
            for (int cellX = minX; cellX <= maxX; cellX++)
            {
                foreach (int other in index.Cell(index.IndexOf(cellX, cellZ)))
                {
                    if (other == slot || !world.IsAliveSlot(other))
                    {
                        continue;
                    }

                    ref Entity candidate = ref world.GetRefBySlot(other);

                    // An ally, and not a brother on the same team: the encounter that can turn
                    // into friendly fire is between the two teams, not inside one.
                    if (candidate.TeamId == shooter.TeamId || candidate.TeamId is not (0 or 1))
                    {
                        continue;
                    }

                    int dx = shooter.Position.X - candidate.Position.X;
                    int dz = shooter.Position.Z - candidate.Position.Z;

                    if (((long)dx * dx) + ((long)dz * dz) <= (long)range * range)
                    {
                        return true;
                    }
                }
            }
        }

        return false;
    }

    /// <summary>
    /// True when some weapon that fired this tick belongs to a team hostile to the victim's.
    /// <para>
    /// The firing set is deliberately looser than the one above: a search that came up empty
    /// also sets a cooldown, and a shot that kills its target clears the target in the same
    /// tick, so the strict test — a cooldown that rose with a target still held — would miss
    /// exactly the shots that did the most damage. Loosening it can only add hostile teams to
    /// the set, so it can never excuse an ally's fire: it is the direction that keeps the
    /// check conservative rather than the direction that makes it pass.
    /// </para>
    /// </summary>
    private static bool ExplainedByHostileFire(SimWorld world, int[] firedTeams, int count, int victimTeam)
    {
        for (int i = 0; i < count; i++)
        {
            if (world.IsHostile(firedTeams[i], victimTeam))
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsLava(SimWorld world, WorldPos position)
    {
        int cell = world.Navigation.IndexOfWorld(position);
        return cell >= 0 && world.TerrainTypes.TypeAt(cell) == TerrainType.Lava;
    }

    /// <summary>A team that can call the orbital strike, as <c>AbilityTests</c> builds one.</summary>
    private static SimWorld SovietWithOrbital()
    {
        SimWorld world = new(seed: 4711, capacity: 32);

        Spawn(world, Faction.Soviet, 0, UnitKind.DesignBureau, Clearing(world));

        ref TeamState state = ref world.TeamRef(0);
        state.Materials = 10_000;
        state.TechTier = 3;
        state.TechMask |= 1UL << (int)TechId.SovietOrbital;

        return world;
    }
}
