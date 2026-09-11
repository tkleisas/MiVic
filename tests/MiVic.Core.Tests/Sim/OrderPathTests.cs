using MiVic.Core.Numerics;
using MiVic.Core.Pathfinding;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// An order is one command, whichever hand gave it.
/// <para>
/// The probe's <c>order move</c> and <c>order attack</c> exist so that a script can demonstrate a
/// unit <em>moving</em>, which it could not do while the only way to order one was a click: every
/// question about movement had to be asked of a fixture that had already ordered the unit, which
/// made the order part of the premise instead of part of the demonstration. The price of a second
/// way to give an order is that it is a second way to give an order — so both go through
/// <see cref="SimWorld.OrderMove"/> and <see cref="SimWorld.OrderAttack"/>, the calls the client's
/// own right-click and attack click make, and these tests pin the two forms to one order.
/// </para>
/// <para>
/// <b>What is compared, and what is not.</b> There is no test project for the client, so the probe
/// cannot be run from here: what is compared is the command the shared call queues against the
/// command the order is otherwise spelled out as — <c>SimCommand.Move(…, Tick + 1, …)</c> handed to
/// <see cref="SimWorld.Enqueue"/> — read back out of the world's own command log, and then the two
/// worlds after three hundred ticks of play. That the probe and the click are one call is a fact
/// about the two call sites; what these tests catch is that call changing under one of them.
/// </para>
/// </summary>
public sealed class OrderPathTests
{
    /// <summary>The capacity the standard skirmish is laid out in, as the client builds it.</summary>
    private const int SkirmishCapacity = 1024;

    /// <summary>Ticks both worlds are run for before their states are compared.</summary>
    private const int Ticks = 300;

    /// <summary>The standard skirmish, which is a world with an army in it that nothing ordered.</summary>
    private static SimWorld Skirmish()
    {
        SimWorld world = new(20250101UL, SkirmishCapacity, MatchRoster.For(ScenarioKind.Skirmish));
        Scenario.Build(world, ScenarioKind.Skirmish);
        return world;
    }

    /// <summary>The first live tank of a team, with the handle and team an order needs.</summary>
    private static (EntityId Id, int Team) FirstTank(SimWorld world)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot) || world.GetRefBySlot(slot).Kind != UnitKind.Tank)
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);
            return (new EntityId(slot, entity.Generation), entity.TeamId);
        }

        throw new InvalidOperationException("The scenario laid out no tank to order.");
    }

    /// <summary>
    /// The first hostile structure, which is a target the world's own attack rule accepts without a
    /// second question being asked of it: a lock is only refused for an ally or for a weapon that
    /// cannot reach the target's kind, and neither is what this test is about.
    /// </summary>
    private static EntityId HostileStructure(SimWorld world, int team)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot) || world.GetRefBySlot(slot).TeamId == team)
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                return new EntityId(slot, entity.Generation);
            }
        }

        throw new InvalidOperationException("The scenario laid out no hostile structure to attack.");
    }

    /// <summary>
    /// Ground the tank can actually walk to, asked of the pathfinder rather than chosen here — for
    /// the reason <see cref="MovementSystemTests"/> asks the same way: a destination that happened to
    /// be water would pin the terrain instead of the order, and the route search clips goals it
    /// cannot enter.
    /// </summary>
    private static WorldPos ReachableGoal(SimWorld world, EntityId unit)
    {
        ref Entity entity = ref world.GetRefBySlot(unit.Slot);
        NavGrid grid = world.Navigation;
        PathContext context = SimWorld.PathContextFor(entity.Faction, UnitKind.Tank);
        var finder = new PathFinder(grid.CellCount);
        int[] buffer = new int[SimConstants.MaxPathCells];
        int start = grid.NearestWalkable(grid.IndexOfWorld(entity.Position), world.TerrainTypes, context);

        for (int cell = 0; cell < grid.CellCount; cell++)
        {
            if (!grid.IsWalkable(cell) || cell == start || !world.TerrainTypes.IsPassable(cell, context.Movement))
            {
                continue;
            }

            int manhattan = Math.Abs(grid.CellX(cell) - grid.CellX(start)) + Math.Abs(grid.CellZ(cell) - grid.CellZ(start));

            if (manhattan is < 12 or > 40)
            {
                continue;
            }

            if (finder.FindPath(grid, world.TerrainTypes, context, start, cell, buffer) > 0)
            {
                return grid.CentreOf(cell);
            }
        }

        throw new InvalidOperationException("No reachable destination was found for the test.");
    }

    /// <summary>
    /// The one command a world was handed, so the two spellings are compared as orders rather than
    /// as the states they happen to lead to.
    /// </summary>
    private static SimCommand OnlyCommand(SimWorld world)
    {
        Assert.Single(world.RecordedCommands);
        return world.RecordedCommands[0].Command;
    }

    [Fact]
    public void AMoveOrderIsTheSameOrderWhicheverWayItIsGiven()
    {
        SimWorld shared = Skirmish();
        SimWorld spelled = Skirmish();

        (EntityId unit, int team) = FirstTank(shared);
        WorldPos goal = ReachableGoal(shared, unit);

        // The one call both the client's right-click and the probe's `order move` make…
        shared.StartRecording();
        shared.OrderMove(unit, goal, team);

        // …against the order written out where it is given, on the same tick of the same world.
        spelled.StartRecording();
        spelled.Enqueue(SimCommand.Move(unit, goal, spelled.Tick + 1, team));

        // The command first, which is the sharp half: two commands that are equal cannot have been
        // answered by two different simulations.
        Assert.Equal(OnlyCommand(spelled), OnlyCommand(shared));

        shared.RunTicks(Ticks);
        spelled.RunTicks(Ticks);

        Assert.Equal(StateHash.Compute(spelled), StateHash.Compute(shared));
    }

    [Fact]
    public void AnAttackOrderIsTheSameOrderWhicheverWayItIsGiven()
    {
        SimWorld shared = Skirmish();
        SimWorld spelled = Skirmish();

        (EntityId gunner, int team) = FirstTank(shared);
        EntityId victim = HostileStructure(shared, team);

        shared.StartRecording();
        shared.OrderAttack(gunner, victim, team);

        spelled.StartRecording();
        spelled.Enqueue(SimCommand.Attack(gunner, victim, spelled.Tick + 1, team));

        Assert.Equal(OnlyCommand(spelled), OnlyCommand(shared));

        shared.RunTicks(Ticks);
        spelled.RunTicks(Ticks);

        Assert.Equal(StateHash.Compute(spelled), StateHash.Compute(shared));
    }
}
