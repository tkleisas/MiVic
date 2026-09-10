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

/// <summary>
/// What became of one faction's base position: where it was asked to stand, where it
/// stands, and how good the ground under it is.
/// <para>
/// A base is the one thing in a scenario that cannot be moved afterwards — the whole
/// opening is played from it, and a headquarters the player cannot reach is a match
/// that cannot be played — so the search reports what it found rather than leaving the
/// caller to assume it worked.
/// </para>
/// </summary>
/// <param name="Faction">The faction whose base this is.</param>
/// <param name="Requested">The position the scenario asked for.</param>
/// <param name="Placed">
/// The site the search settled on. The same value as <paramref name="Requested"/> when
/// that position already had room for a base, so that a seed whose hardcoded spot is
/// good keeps the layout it always had.
/// </param>
/// <param name="PatchFound">
/// False when nothing within <see cref="SimWorld.BaseSearchRadiusCells"/> had enough
/// solid ground, and the fallback — the nearest single solid cell — was used. A false
/// here is the report the search owes its caller: the base is out of the water but not
/// standing on the area it needs.
/// </param>
/// <param name="SolidCells">
/// Solid cells in the patch around <paramref name="Placed"/>, out of
/// <see cref="SimWorld.BaseSitePatchCells"/>.
/// </param>
public readonly record struct BaseSitePlacement(
    Faction Faction,
    WorldPos Requested,
    WorldPos Placed,
    bool PatchFound,
    int SolidCells);

/// <summary>What a scenario build produced, for the caller to keep track of.</summary>
/// <param name="CommandCentres">Command centre of each faction, in spawn order.</param>
/// <param name="Spawned">Every entity spawned, in spawn order.</param>
/// <param name="BaseSites">Where each faction's base ended up, and on what ground.</param>
public readonly record struct ScenarioSetup(
    IReadOnlyList<EntityId> CommandCentres,
    IReadOnlyList<SpawnedEntity> Spawned,
    IReadOnlyList<BaseSitePlacement> BaseSites);

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
        var baseSites = new List<BaseSitePlacement>(3);

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
            BuildSkirmish(world, commandCentres, spawned, baseSites);
        }

        return new ScenarioSetup(commandCentres, spawned, baseSites);
    }

    /// <summary>
    /// The roles the gallery lays out, one column each, in this order. Public
    /// because the renderer labels the grid with the same column indices, and a
    /// legend that could drift out of step with the layout would be worse than no
    /// legend at all.
    /// </summary>
    public static readonly UnitKind[] GalleryKinds =
    [
        UnitKind.Infantry, UnitKind.Tank, UnitKind.Artillery, UnitKind.RocketArtillery,
        UnitKind.AntiAir, UnitKind.Commissar, UnitKind.RobotInfantry, UnitKind.Drone, UnitKind.Mercenary,
        UnitKind.StealthRecon, UnitKind.ElectroPrototype, UnitKind.Harvester,
        UnitKind.Aircraft, UnitKind.CommandCentre, UnitKind.PowerPlant, UnitKind.NuclearPlant,
        UnitKind.Factory, UnitKind.DesignBureau,
    ];

    /// <summary>Metres between gallery columns, and twice that between its rows.</summary>
    public const int GallerySpacingMm = 26_000;

    /// <summary>
    /// One unit of every faction and role, laid out in a grid with even spacing
    /// so each model can be looked at individually.
    /// </summary>
    private static void BuildGallery(SimWorld world, List<SpawnedEntity> spawned)
    {
        UnitKind[] kinds = GalleryKinds;

        int row = 0;

        foreach (FactionProfile profile in FactionProfile.All)
        {
            for (int column = 0; column < kinds.Length; column++)
            {
                int x = (column - (kinds.Length / 2)) * GallerySpacingMm;
                int z = (row - 1) * GallerySpacingMm * 2;

                UnitKind kind = kinds[column];
                UnitDefinition definition = UnitCatalog.Get(kind);

                // Everything in the gallery belongs to team 0 so fog of war never
                // hides a model the player is trying to inspect.
                SpawnPlaced(
                    world,
                    profile.Faction,
                    0,
                    kind,
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
    private static void BuildSkirmish(
        SimWorld world,
        List<EntityId> commandCentres,
        List<SpawnedEntity> spawned,
        List<BaseSitePlacement> baseSites)
    {
        SpawnForce(
            world, commandCentres, spawned, baseSites,
            Faction.Soviet, teamId: 0, centre: new WorldPos(-180_000, 0, -180_000),
            unitCount: UnitsPerFaction, fullBase: true, materials: 2_500, energy: 400, water: 400);

        SpawnForce(
            world, commandCentres, spawned, baseSites,
            Faction.Chinese, teamId: 1, centre: new WorldPos(180_000, 0, -180_000),
            unitCount: UnitsPerFaction, fullBase: true, materials: 2_500, energy: 400, water: 400);

        SpawnForce(
            world, commandCentres, spawned, baseSites,
            Faction.Western, teamId: 2, centre: new WorldPos(0, 0, 200_000),
            unitCount: UnitsPerFaction, fullBase: true, materials: 2_500, energy: 400, water: 400);
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
        var baseSites = new List<BaseSitePlacement>(3);

        SpawnForce(
            world, commandCentres, spawned, baseSites,
            Faction.Soviet, teamId: 0, centre: mission.PlayerBase,
            unitCount: mission.PlayerUnits, fullBase: true, materials: 1_500, energy: 300, water: 250);

        SpawnForce(
            world, commandCentres, spawned, baseSites,
            Faction.Chinese, teamId: 1, centre: mission.AllyBase,
            unitCount: mission.AllyUnits, fullBase: false, materials: 1_500, energy: 300, water: 250);

        SpawnForce(
            world, commandCentres, spawned, baseSites,
            Faction.Western, teamId: 2, centre: mission.EnemyBase,
            unitCount: mission.EnemyUnits, fullBase: true, materials: 1_500, energy: 300, water: 250);

        world.AttachMission(mission);

        return new ScenarioSetup(commandCentres, spawned, baseSites);
    }

    private static void SpawnForce(
        SimWorld world,
        List<EntityId> commandCentres,
        List<SpawnedEntity> spawned,
        List<BaseSitePlacement> baseSites,
        Faction faction,
        int teamId,
        WorldPos centre,
        int unitCount,
        bool fullBase,
        int materials,
        int energy,
        int water)
    {
        // The position is a wish rather than a fact. Terrain comes from the seed, so
        // whether a hardcoded base site is on land is luck, and on the standard seed the
        // luck runs out: the headquarters, the power plant, the factory and the design
        // bureau all stand in deep water, which is a base nothing can reach. The site is
        // therefore searched for, and the search says whether it found what it wanted.
        bool patchFound = world.TryFindBaseSite(centre, out WorldPos site);

        baseSites.Add(new BaseSitePlacement(faction, centre, site, patchFound, world.BaseSiteSolidCells(site)));

        commandCentres.Add(SpawnStructure(world, faction, teamId, UnitKind.CommandCentre, site, health: 5000, spawned));

        // A starting base so every faction can act from the first tick: power for
        // energy, a factory for vehicles and a design bureau for research. The offsets
        // put them inside the patch the site was chosen for — and each one is checked
        // anyway, because the patch is allowed to be mostly solid rather than entirely
        // solid, and a structure gets no relocation of its own.
        SpawnStructure(world, faction, teamId, UnitKind.PowerPlant, Offset(site, 45_000, 45_000), health: 1200, spawned);
        SpawnStructure(world, faction, teamId, UnitKind.Factory, Offset(site, -45_000, 45_000), health: 2000, spawned);

        if (fullBase)
        {
            SpawnStructure(world, faction, teamId, UnitKind.DesignBureau, Offset(site, 45_000, -45_000), health: 1500, spawned);
        }

        ref TeamState economy = ref world.TeamRef(teamId);
        economy.Materials = materials;
        economy.Energy = energy;
        economy.Water = water;

        SpawnFormation(world, faction, teamId, site, unitCount, spawned);
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
        const int ColumnSpacingMm = 5_500;
        const int RowSpacingMm = 6_500;

        int rows = ((unitCount + Columns) - 1) / Columns;

        for (int i = 0; i < unitCount; i++)
        {
            int column = i % Columns;
            int row = i / Columns;

            // Integer millimetres, with the half-step written as a half rather than as
            // a fraction: an even column count puts the centre of the rank between two
            // units, and multiplying by two before dividing by two keeps that exact.
            // The offsets are the ones the float arithmetic produced, to the millimetre,
            // so nothing about the formation moved except the arithmetic that found it.
            int offsetX = ((((2 * column) - (Columns - 1)) * ColumnSpacingMm) / 2);
            int offsetZ = ((((2 * row) - (rows - 1)) * RowSpacingMm) / 2);

            int jitterX = world.Rng.NextInt(-1400, 1401);
            int jitterZ = world.Rng.NextInt(-1400, 1401);

            WorldPos position = new(
                centre.X + offsetX + jitterX,
                0,
                centre.Z + offsetZ + jitterZ);

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

            SpawnPlaced(world, faction, teamId, kind, position, speed, health: 100, spawned);
        }
    }

    private static WorldPos Offset(WorldPos centre, int dx, int dz)
        => new(centre.X + dx, centre.Y, centre.Z + dz);

    /// <summary>
    /// Spawns something the scenario places, on ground it can occupy.
    /// </summary>
    private static EntityId SpawnPlaced(
        SimWorld world,
        Faction faction,
        int teamId,
        UnitKind kind,
        WorldPos position,
        int speedMmPerTick,
        int health,
        List<SpawnedEntity> spawned)
    {
        // Aircraft are the exception and the reason this is not simply a call to
        // LegalSpawnSite: they fly, so water under them is nothing, and a wing pushed
        // onto the nearest shore to satisfy a rule about ground would be a formation
        // pulled out of shape for no reason.
        if (!UnitCatalog.Flies(kind))
        {
            position = world.LegalSpawnSite(position);
        }

        return Spawn(world, faction, teamId, kind, position, speedMmPerTick, health, spawned);
    }

    /// <summary>
    /// Plants a structure where it can stand.
    /// <para>
    /// A structure is the one thing <see cref="SimWorld.Spawn"/> deliberately leaves where
    /// it is put — a building in the sea should stay visible rather than be quietly moved,
    /// which is the whole reason this placement exists — so the ground under a structure
    /// the scenario places is checked here instead.
    /// </para>
    /// </summary>
    private static EntityId SpawnStructure(
        SimWorld world,
        Faction faction,
        int teamId,
        UnitKind kind,
        WorldPos position,
        int health,
        List<SpawnedEntity> spawned)
        => Spawn(world, faction, teamId, kind, world.LegalSpawnSite(position), speedMmPerTick: 0, health, spawned);

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
