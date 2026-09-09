using MiVic.Core.Campaign;
using MiVic.Core.Numerics;

namespace MiVic.Core.Sim;

/// <summary>Which world to lay out for a match, a replay or a model inspection.</summary>
public enum ScenarioKind : byte
{
    /// <summary>Three-faction skirmish: the socialist alliance against the Δυτικοί.</summary>
    Skirmish = 0,

    /// <summary>One of every faction and role on parade, for model inspection.</summary>
    ModelGallery = 1,

    /// <summary>
    /// A campaign mission. The layout comes from the mission definition rather
    /// than from this enum, so a replay also stores the mission id.
    /// </summary>
    Mission = 2,
}

/// <summary>An entity the scenario created, with the position it was asked for.</summary>
/// <param name="Id">Handle to the spawned entity.</param>
/// <param name="RequestedPosition">
/// The position passed to <see cref="SimWorld.Spawn"/>. <see cref="SimWorld.Spawn"/>
/// replaces the Y component with the terrain height, so callers that need to
/// remember where a unit started must use this value rather than the entity's
/// own position.
/// </param>
public readonly record struct SpawnedEntity(EntityId Id, WorldPos RequestedPosition);

/// <summary>What a scenario build produced, for the caller to keep track of.</summary>
/// <param name="CommandCentres">Command centre of each faction, in spawn order.</param>
/// <param name="Spawned">Every entity spawned, in spawn order.</param>
public readonly record struct ScenarioSetup(
    IReadOnlyList<EntityId> CommandCentres,
    IReadOnlyList<SpawnedEntity> Spawned);

/// <summary>
/// Builds the starting world for a scenario.
/// <para>
/// This lives in the simulation rather than in the client because a replay is
/// only a seed plus a command log: to reproduce a match, the player must be able
/// to rebuild the identical starting world with no client involved. Spawn order
/// is part of that contract — slots are handed out in order and the initial
/// jitter is drawn from the world's own generator, so the layout is a pure
/// function of the seed.
/// </para>
/// </summary>
public static class Scenario
{
    /// <summary>Units per faction in the skirmish, excluding structures.</summary>
    public const int UnitsPerFaction = 166;

    /// <summary>Half-extent of the map in millimetres.</summary>
    public const int MapHalfExtentMm = SimConstants.MapExtentMm / 2;

    /// <summary>
    /// Lays out <paramref name="kind"/> in <paramref name="world"/>. The world
    /// must be empty; callers create it with the capacity the scenario needs.
    /// </summary>
    public static ScenarioSetup Build(SimWorld world, ScenarioKind kind)
    {
        ArgumentNullException.ThrowIfNull(world);

        var commandCentres = new List<EntityId>(3);
        var spawned = new List<SpawnedEntity>(4 + (UnitsPerFaction * 3));

        if (kind == ScenarioKind.ModelGallery)
        {
            BuildGallery(world, spawned);
        }
        else if (kind == ScenarioKind.Mission)
        {
            throw new InvalidOperationException("A mission scenario needs its definition: use Scenario.BuildMission.");
        }
        else
        {
            BuildSkirmish(world, commandCentres, spawned);
        }

        return new ScenarioSetup(commandCentres, spawned);
    }

    /// <summary>
    /// One unit of every faction and role, laid out in a grid with even spacing
    /// so each model can be looked at individually.
    /// </summary>
    private static void BuildGallery(SimWorld world, List<SpawnedEntity> spawned)
    {
        const int SpacingMm = 26_000;

        UnitKind[] kinds =
        [
            UnitKind.Infantry, UnitKind.Tank, UnitKind.Artillery, UnitKind.RocketArtillery,
            UnitKind.AntiAir, UnitKind.Commissar, UnitKind.RobotInfantry, UnitKind.Drone, UnitKind.Mercenary,
            UnitKind.Aircraft, UnitKind.CommandCentre, UnitKind.PowerPlant, UnitKind.NuclearPlant,
            UnitKind.Factory, UnitKind.DesignBureau,
        ];

        int row = 0;

        foreach (FactionProfile profile in FactionProfile.All)
        {
            for (int column = 0; column < kinds.Length; column++)
            {
                int x = (column - (kinds.Length / 2)) * SpacingMm;
                int z = (row - 1) * SpacingMm * 2;

                UnitDefinition definition = UnitCatalog.Get(kinds[column]);

                // Everything in the gallery belongs to team 0 so fog of war never
                // hides a model the player is trying to inspect.
                Spawn(
                    world,
                    profile.Faction,
                    0,
                    kinds[column],
                    new WorldPos(x, 0, z),
                    definition.SpeedMmPerTick,
                    definition.Health,
                    spawned);
            }

            row++;
        }
    }

    /// <summary>
    /// The skirmish: Σοβιετικοί (player, team 0) and Κινέζοι (ally, team 1)
    /// against Δυτικοί (team 2). This mirrors the game's premise — the two
    /// socialist powers must cooperate to defeat the Western empire.
    /// </summary>
    private static void BuildSkirmish(SimWorld world, List<EntityId> commandCentres, List<SpawnedEntity> spawned)
    {
        SpawnForce(world, commandCentres, spawned, Faction.Soviet, teamId: 0, centre: new WorldPos(-180_000, 0, -180_000));
        SpawnForce(world, commandCentres, spawned, Faction.Chinese, teamId: 1, centre: new WorldPos(180_000, 0, -180_000));
        SpawnForce(world, commandCentres, spawned, Faction.Western, teamId: 2, centre: new WorldPos(0, 0, 200_000));
    }

    /// <summary>
    /// Lays out a campaign mission: a small force for each side, a base apiece,
    /// and the objectives attached to the world. The layout is a pure function of
    /// the mission definition and the world's seed, so a replay reproduces it.
    /// </summary>
    public static ScenarioSetup BuildMission(SimWorld world, MissionDefinition mission)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(mission);

        var commandCentres = new List<EntityId>(3);
        var spawned = new List<SpawnedEntity>(mission.PlayerUnits + mission.AllyUnits + mission.EnemyUnits + 11);

        SpawnForce(world, commandCentres, spawned, Faction.Soviet, teamId: 0, mission.PlayerBase, mission.PlayerUnits, fullBase: true);
        SpawnForce(world, commandCentres, spawned, Faction.Chinese, teamId: 1, mission.AllyBase, mission.AllyUnits, fullBase: false);
        SpawnForce(world, commandCentres, spawned, Faction.Western, teamId: 2, mission.EnemyBase, mission.EnemyUnits, fullBase: true);

        world.AttachMission(mission);

        return new ScenarioSetup(commandCentres, spawned);
    }

    private static void SpawnForce(
        SimWorld world,
        List<EntityId> commandCentres,
        List<SpawnedEntity> spawned,
        Faction faction,
        int teamId,
        WorldPos centre,
        int unitCount,
        bool fullBase)
    {
        commandCentres.Add(Spawn(world, faction, teamId, UnitKind.CommandCentre, centre, 0, health: 5000, spawned));

        // A starting base so every faction can act from the first tick: power for
        // energy, a factory for vehicles and a design bureau for research.
        Spawn(world, faction, teamId, UnitKind.PowerPlant, Offset(centre, 45_000, 45_000), 0, health: 1200, spawned);
        Spawn(world, faction, teamId, UnitKind.Factory, Offset(centre, -45_000, 45_000), 0, health: 2000, spawned);

        if (fullBase)
        {
            Spawn(world, faction, teamId, UnitKind.DesignBureau, Offset(centre, 45_000, -45_000), 0, health: 1500, spawned);
        }

        ref TeamState economy = ref world.TeamRef(teamId);
        economy.Materials = 1_500;
        economy.Energy = 300;
        economy.Water = 250;

        SpawnFormation(world, faction, teamId, centre, unitCount, spawned);
    }

    /// <summary>
    /// A deployed formation reads as an army; a tight blob does not. Units stand
    /// in ranks with deterministic jitter, so the layout is identical for a given
    /// seed.
    /// </summary>
    private static void SpawnFormation(
        SimWorld world,
        Faction faction,
        int teamId,
        WorldPos centre,
        int unitCount,
        List<SpawnedEntity> spawned)
    {
        const int Columns = 14;
        const float ColumnSpacingMetres = 5.5f;
        const float RowSpacingMetres = 6.5f;

        int rows = ((unitCount + Columns) - 1) / Columns;

        for (int i = 0; i < unitCount; i++)
        {
            int column = i % Columns;
            int row = i / Columns;

            float offsetX = (column - ((Columns - 1) * 0.5f)) * ColumnSpacingMetres;
            float offsetZ = (row - ((rows - 1) * 0.5f)) * RowSpacingMetres;

            int jitterX = world.Rng.NextInt(-1400, 1401);
            int jitterZ = world.Rng.NextInt(-1400, 1401);

            WorldPos position = new(
                centre.X + (int)(offsetX * 1000f) + jitterX,
                0,
                centre.Z + (int)(offsetZ * 1000f) + jitterZ);

            (UnitKind kind, int speed) = (i % 12) switch
            {
                0 or 1 or 2 => (UnitKind.Tank, 400),
                3 => (UnitKind.Artillery, 300),
                4 => (UnitKind.AntiAir, 350),
                5 => (UnitKind.Aircraft, 1500),
                _ => (UnitKind.Infantry, 100),
            };

            // Aircraft sit above the battlefield; the air layer is simulated as a
            // height on the same entity rather than as a separate domain.
            if (UnitCatalog.Flies(kind))
            {
                position = new WorldPos(position.X, 60_000, position.Z);
            }

            Spawn(world, faction, teamId, kind, position, speed, health: 100, spawned);
        }
    }

    private static void SpawnForce(
        SimWorld world,
        List<EntityId> commandCentres,
        List<SpawnedEntity> spawned,
        Faction faction,
        int teamId,
        WorldPos centre)
    {
        commandCentres.Add(Spawn(world, faction, teamId, UnitKind.CommandCentre, centre, 0, health: 5000, spawned));

        // A starting base so every faction can act from the first tick: power for
        // energy, a factory for vehicles and a design bureau for research.
        Spawn(world, faction, teamId, UnitKind.PowerPlant, Offset(centre, 45_000, 45_000), 0, health: 1200, spawned);
        Spawn(world, faction, teamId, UnitKind.Factory, Offset(centre, -45_000, 45_000), 0, health: 2000, spawned);
        Spawn(world, faction, teamId, UnitKind.DesignBureau, Offset(centre, 45_000, -45_000), 0, health: 1500, spawned);

        ref TeamState economy = ref world.TeamRef(teamId);
        economy.Materials = 2_500;
        economy.Energy = 400;
        economy.Water = 400;

        SpawnFormation(world, faction, teamId, centre, UnitsPerFaction, spawned);
    }

    private static WorldPos Offset(WorldPos centre, int dx, int dz)
        => new(centre.X + dx, centre.Y, centre.Z + dz);

    private static EntityId Spawn(
        SimWorld world,
        Faction faction,
        int teamId,
        UnitKind kind,
        WorldPos position,
        int speedMmPerTick,
        int health,
        List<SpawnedEntity> spawned)
    {
        EntityId id = world.Spawn(faction, teamId, kind, position, Fix32.FromInt(speedMmPerTick), health);
        spawned.Add(new SpawnedEntity(id, position));
        return id;
    }
}
