using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>What a command asks the simulation to do.</summary>
public enum SimCommandKind : byte
{
    /// <summary>No-op, used to pad or to mark an empty queue slot.</summary>
    None = 0,

    /// <summary>Move the target entity to <see cref="SimCommand.Destination"/>.</summary>
    Move = 1,

    /// <summary>Cancel any move order on the target entity.</summary>
    Stop = 2,

    /// <summary>Add a unit to a building's production queue.</summary>
    QueueUnit = 3,

    /// <summary>Remove the last job from a building's production queue.</summary>
    CancelProduction = 4,

    /// <summary>Start research at a design bureau, raising the team's tech tier.</summary>
    Research = 5,

    /// <summary>Engage a specific enemy entity.</summary>
    Attack = 6,

    /// <summary>Grant an allied team a licence to build one of your designs.</summary>
    LicenceProduction = 7,

    /// <summary>
    /// Run a prototype of a design at a design bureau, approving it for factory
    /// production when it completes. Σοβιετικοί only.
    /// </summary>
    ApproveDesign = 8,

    /// <summary>Call in an off-map support ability at a ground position.</summary>
    UseAbility = 9,

    /// <summary>Build a crossing over the water at a ground position.</summary>
    BuildBridge = 10,

    /// <summary>Raise a structure at a ground position the player chose.</summary>
    BuildStructure = 11,
}

/// <summary>
/// A player intent stamped with the tick it must execute on.
/// <para>
/// Commands are the only way the outside world may influence the simulation.
/// Because they are plain data and carry their execution tick, a replay is just
/// a seed plus a command log, and a lockstep peer only needs the same log.
/// </para>
/// </summary>
/// <param name="Kind">The action requested.</param>
/// <param name="Target">Entity the command applies to.</param>
/// <param name="Destination">Destination in millimetres; ignored by most commands.</param>
/// <param name="ExecuteTick">Simulation tick on which to apply the command.</param>
/// <param name="IssuerTeam">Team that issued the command, for validation.</param>
/// <param name="UnitKind">Role being produced, for production commands.</param>
/// <param name="AttackTarget">Enemy to engage, for <see cref="SimCommandKind.Attack"/>.</param>
/// <param name="Tech">Project to research, for <see cref="SimCommandKind.Research"/>.</param>
/// <param name="Ability">Off-map support to call in, for <see cref="SimCommandKind.UseAbility"/>.</param>
public readonly record struct SimCommand(
    SimCommandKind Kind,
    EntityId Target,
    WorldPos Destination,
    long ExecuteTick,
    int IssuerTeam,
    UnitKind UnitKind = UnitKind.None,
    EntityId AttackTarget = default,
    TechId Tech = TechId.None,
    AbilityId Ability = AbilityId.None)
{
    /// <summary>Creates a move order executing on <paramref name="executeTick"/>.</summary>
    public static SimCommand Move(EntityId target, WorldPos destination, long executeTick, int issuerTeam)
        => new(SimCommandKind.Move, target, destination, executeTick, issuerTeam);

    /// <summary>Creates a stop order executing on <paramref name="executeTick"/>.</summary>
    public static SimCommand Stop(EntityId target, long executeTick, int issuerTeam)
        => new(SimCommandKind.Stop, target, WorldPos.Origin, executeTick, issuerTeam);

    /// <summary>Creates a production order for a building.</summary>
    public static SimCommand QueueUnit(EntityId building, UnitKind kind, long executeTick, int issuerTeam)
        => new(SimCommandKind.QueueUnit, building, WorldPos.Origin, executeTick, issuerTeam, kind);

    /// <summary>Cancels the last job queued at a building.</summary>
    public static SimCommand CancelProduction(EntityId building, long executeTick, int issuerTeam)
        => new(SimCommandKind.CancelProduction, building, WorldPos.Origin, executeTick, issuerTeam);

    /// <summary>Starts research on a project at a design bureau.</summary>
    public static SimCommand Research(EntityId building, TechId tech, long executeTick, int issuerTeam)
        => new(SimCommandKind.Research, building, WorldPos.Origin, executeTick, issuerTeam, UnitKind.None, default, tech);

    /// <summary>Orders a unit to engage an enemy.</summary>
    public static SimCommand Attack(EntityId attacker, EntityId victim, long executeTick, int issuerTeam)
        => new(SimCommandKind.Attack, attacker, WorldPos.Origin, executeTick, issuerTeam, UnitKind.None, victim);

    /// <summary>
    /// Grants the team owning <paramref name="recipientBuilding"/> a licence to
    /// build <paramref name="kind"/>. The issuer must be an ally that already has
    /// the design.
    /// </summary>
    public static SimCommand Licence(EntityId recipientBuilding, UnitKind kind, long executeTick, int issuerTeam)
        => new(SimCommandKind.LicenceProduction, recipientBuilding, WorldPos.Origin, executeTick, issuerTeam, kind);

    /// <summary>
    /// Starts a prototype run for <paramref name="kind"/> at
    /// <paramref name="bureau"/>. On completion the design is approved and
    /// factories may build it.
    /// </summary>
    public static SimCommand ApproveDesign(EntityId bureau, UnitKind kind, long executeTick, int issuerTeam)
        => new(SimCommandKind.ApproveDesign, bureau, WorldPos.Origin, executeTick, issuerTeam, kind);

    /// <summary>
    /// Calls in an off-map ability at a ground position. <paramref name="Target"/> is
    /// unused: support arrives from off the map, not from a unit.
    /// </summary>
    public static SimCommand UseAbility(AbilityId ability, WorldPos target, long executeTick, int issuerTeam)
        => new(
            SimCommandKind.UseAbility,
            EntityId.None,
            target,
            executeTick,
            issuerTeam,
            UnitKind.None,
            default,
            TechId.None,
            ability);

    /// <summary>Spans the water at <paramref name="target"/> so ground units can cross.</summary>
    public static SimCommand Bridge(WorldPos target, long executeTick, int issuerTeam)
        => new(SimCommandKind.BuildBridge, EntityId.None, target, executeTick, issuerTeam);

    /// <summary>
    /// Raises a structure of <paramref name="kind"/> at <paramref name="site"/>.
    /// <para>
    /// The site is the order rather than a place the simulation works out for itself: a
    /// structure raised from another structure used to appear at a fixed offset from whatever
    /// made it, which is a position the player did not choose and cannot see before paying for
    /// it. <see cref="Target"/> is unused — a structure is raised <em>on the map</em>, not by
    /// an entity, and which building it is raised from is an unlock rather than a producer.
    /// </para>
    /// </summary>
    public static SimCommand Structure(UnitKind kind, WorldPos site, long executeTick, int issuerTeam)
        => new(SimCommandKind.BuildStructure, EntityId.None, site, executeTick, issuerTeam, kind);
}

/// <summary>
/// A command together with the tick it was issued on.
/// <para>
/// The issue tick, not the execute tick, is what a replay needs: replaying the
/// log means re-issuing each command at the same moment it was issued, after
/// which the simulation's own scheduling does the rest.
/// </para>
/// </summary>
/// <param name="Tick">World tick when the command was enqueued.</param>
/// <param name="Command">The command itself.</param>
public readonly record struct SimCommandRecord(long Tick, SimCommand Command);
