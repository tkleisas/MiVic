using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

public sealed class AiTests
{
    private static SimWorld World() => new(seed: 20250101, capacity: 256);

    private static EntityId Spawn(SimWorld world, Faction faction, int team, UnitKind kind, int x, int z)
        => world.Spawn(
            faction,
            team,
            kind,
            new WorldPos(x, 0, z),
            Fix32.FromInt(UnitCatalog.Get(kind).SpeedMmPerTick),
            UnitCatalog.Get(kind).Health);

    private static int CountKind(SimWorld world, int team, UnitKind kind)
    {
        int count = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (world.IsAliveSlot(slot))
            {
                ref Entity entity = ref world.GetRefBySlot(slot);

                if (entity.TeamId == team && entity.Kind == kind)
                {
                    count++;
                }
            }
        }

        return count;
    }

    [Fact]
    public void AiBuildsPowerWhenEnergyIsNegative()
    {
        SimWorld world = World();
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 0, 0);

        ref TeamState state = ref world.TeamRef(2);
        state.Materials = 5_000;
        state.Water = 5_000;

        // A headquarters alone draws more energy than it makes.
        Assert.True(world.Team(2).EnergyPerTick <= 0);

        world.RunTicks(300);

        Assert.True(CountKind(world, 2, UnitKind.PowerPlant) > 0, "The AI never built a power plant.");
    }

    [Fact]
    public void AiProducesAnArmy()
    {
        SimWorld world = World();
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 0, 0);
        Spawn(world, Faction.Western, 2, UnitKind.Factory, 60_000, 0);
        Spawn(world, Faction.Western, 2, UnitKind.DesignBureau, 0, 60_000);
        Spawn(world, Faction.Western, 2, UnitKind.PowerPlant, -60_000, 0);

        ref TeamState state = ref world.TeamRef(2);
        state.Materials = 50_000;
        state.Water = 50_000;
        state.Energy = 5_000;
        state.TechTier = 2;

        world.RunTicks(1_200);

        int army = CountKind(world, 2, UnitKind.Tank) + CountKind(world, 2, UnitKind.Infantry);

        Assert.True(army > 0, "The AI never produced a combat unit.");
    }

    [Fact]
    public void AiAttacksOnceItsArmyIsLargeEnough()
    {
        SimWorld world = World();
        Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 0, 0);
        EntityId enemyBuilding = Spawn(world, Faction.Soviet, 0, UnitKind.CommandCentre, 200_000, 200_000);

        for (int i = 0; i < AiSystem.AttackArmySize; i++)
        {
            Spawn(world, Faction.Western, 2, UnitKind.Tank, 20_000 + (i * 4_000), 20_000);
        }

        world.RunTicks(80);

        int ordered = 0;

        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity unit = ref world.GetRefBySlot(slot);

            if (unit.TeamId == 2 && (unit.HasAttackOrder || unit.TargetSlot == enemyBuilding.Slot))
            {
                ordered++;
            }
        }

        Assert.True(ordered > 0, "The AI never ordered an attack with a full army.");
    }

    [Fact]
    public void AiIsDeterministic()
    {
        static ulong Run()
        {
            SimWorld world = World();
            Spawn(world, Faction.Western, 2, UnitKind.CommandCentre, 0, 0);
            Spawn(world, Faction.Western, 2, UnitKind.Factory, 60_000, 0);
            Spawn(world, Faction.Western, 2, UnitKind.PowerPlant, -60_000, 0);

            ref TeamState state = ref world.TeamRef(2);
            state.Materials = 20_000;
            state.Water = 20_000;
            state.Energy = 2_000;

            world.RunTicks(400);
            return StateHash.Compute(world);
        }

        Assert.Equal(Run(), Run());
    }

    [Fact]
    public void AiDoesNotPlayThePlayersTeam()
    {
        // The standard skirmish: the computer plays the two teams that are not the player's.
        SimWorld world = new(seed: 1, capacity: 4);

        Assert.False(AiSystem.Plays(world, 0));
        Assert.True(AiSystem.Plays(world, 1));
        Assert.True(AiSystem.Plays(world, 2));

        // The fourth slot is not in the match at all, and a team that is not playing is not
        // played: this used to be answered "no" by luck, because the list happened to stop at 2.
        Assert.False(AiSystem.Plays(world, 3));
    }
}

public sealed class LicenceTests
{
    private static (SimWorld World, EntityId SovietHq, EntityId ChineseFactory) Alliance()
    {
        SimWorld world = new(seed: 4242, capacity: 64);

        EntityId sovietHq = world.Spawn(Faction.Soviet, 0, UnitKind.CommandCentre, new WorldPos(0, 0, 0), Fix32.Zero, 5_000);
        EntityId chineseFactory = world.Spawn(Faction.Chinese, 1, UnitKind.Factory, new WorldPos(60_000, 0, 0), Fix32.Zero, 2_000);

        world.TeamRef(0).Materials = 10_000;
        world.TeamRef(1).Materials = 10_000;
        world.TeamRef(0).Water = 10_000;
        world.TeamRef(1).Water = 10_000;
        world.TeamRef(1).Energy = 10_000;

        return (world, sovietHq, chineseFactory);
    }

    [Fact]
    public void LicenceLetsTheChineseBuildAboveTheirCeiling()
    {
        (SimWorld world, EntityId sovietHq, EntityId chineseFactory) = Alliance();

        // The Κινέζοι ceiling is 2; aircraft need tier 3.
        world.TeamRef(0).TechTier = 3;
        Assert.False(world.CanBuild(1, UnitKind.Aircraft));

        world.Enqueue(SimCommand.Licence(chineseFactory, UnitKind.Aircraft, world.Tick + 1, 0));
        world.Step();

        Assert.True(world.CanBuild(1, UnitKind.Aircraft), "The licence did not unlock the design.");

        // And the factory can now actually queue it.
        world.Enqueue(SimCommand.QueueUnit(chineseFactory, UnitKind.Aircraft, world.Tick + 1, 1));
        world.Step();

        Assert.Equal(1, world.JobsOf(chineseFactory.Slot).Length);
        _ = sovietHq;
    }

    [Fact]
    public void LicenceCannotBeGrantedToAnEnemy()
    {
        (SimWorld world, _, EntityId chineseFactory) = Alliance();

        EntityId westernFactory = world.Spawn(Faction.Western, 2, UnitKind.Factory, new WorldPos(-60_000, 0, 0), Fix32.Zero, 2_000);
        world.TeamRef(0).TechTier = 3;

        world.Enqueue(SimCommand.Licence(westernFactory, UnitKind.Aircraft, world.Tick + 1, 0));
        world.Step();

        Assert.False(world.CanBuild(2, UnitKind.Aircraft));
    }

    [Fact]
    public void LicenceRequiresTheIssuerToHaveTheDesign()
    {
        (SimWorld world, _, EntityId chineseFactory) = Alliance();

        // The Σοβιετικοί are only at tier 1, so they have no aircraft to licence.
        world.TeamRef(0).TechTier = 1;

        world.Enqueue(SimCommand.Licence(chineseFactory, UnitKind.Aircraft, world.Tick + 1, 0));
        world.Step();

        Assert.False(world.CanBuild(1, UnitKind.Aircraft));
    }

    [Fact]
    public void TeamsCannotLicenseToThemselves()
    {
        (SimWorld world, EntityId sovietHq, _) = Alliance();

        world.TeamRef(0).TechTier = 3;
        world.TeamRef(0).ApprovedMask |= 1u << (int)UnitKind.Aircraft;

        world.Enqueue(SimCommand.Licence(sovietHq, UnitKind.Aircraft, world.Tick + 1, 0));
        world.Step();

        // Nothing changed: the licence is a transfer, not a self-grant.
        Assert.True(world.CanBuild(0, UnitKind.Aircraft));
        Assert.False(world.CanBuild(1, UnitKind.Aircraft));
    }

    [Fact]
    public void AllianceIsSovietAndChineseOnly()
    {
        // A licence needs an ally, and the ally is whatever the match says is on the same side.
        SimWorld world = new(seed: 1, capacity: 4);

        Assert.True(world.AreAllied(0, 1));
        Assert.True(world.AreAllied(1, 0));
        Assert.True(world.AreAllied(2, 2));
        Assert.False(world.AreAllied(0, 2));
        Assert.False(world.AreAllied(1, 2));
    }
}
