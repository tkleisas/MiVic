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

    /// <summary>
    /// Two factions, no ally: Σοβιετικοί against Δυτικοί. See <see cref="MatchRoster.Duel"/>.
    /// </summary>
    Duel = 3,

    /// <summary>
    /// Two factions, and the enemy is the one that is normally the ally: Σοβιετικοί against
    /// Κινέζοι, with the Δυτικοί absent. See <see cref="MatchRoster.Rivals"/>.
    /// </summary>
    Rivals = 4,
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
/// <para>
/// <b>Who is playing comes from the world, not from this file.</b> The layout is a force per team
/// <em>the match declares</em> (<see cref="SimWorld.Roster"/>), placed at the base site of the
/// faction that team plays, so a two-faction match is the same code path as a three-faction one and
/// a team that is not in the match is not on the map at all. The three-faction skirmish is the same
/// three teams in the same order as it has always been, which is why its golden hash does not move.
/// </para>
/// </summary>
public static class Scenario
{
    /// <summary>Units per team in a skirmish, excluding structures.</summary>
    public const int UnitsPerFaction = 166;

    /// <summary>Half-extent of the map in millimetres.</summary>
    public const int MapHalfExtentMm = SimConstants.MapExtentMm / 2;

    /// <summary>
    /// The roster a scenario is fought under. The world a scenario is laid out in must have been
    /// built with this one, or <see cref="Build"/> refuses to lay anything out — a world whose
    /// sides disagree with the layout is a desync the state hash cannot see, because the match is
    /// not part of it.
    /// </summary>
    public static MatchRoster RosterFor(ScenarioKind kind, MissionDefinition? mission = null)
        => MatchRoster.For(kind, mission);

    /// <summary>
    /// Creates the world a scenario is played in: same seed, same capacity, and the teams that
    /// scenario declares. The one call that builds a world for a match, so that the layout and the
    /// sides cannot be chosen separately.
    /// </summary>
    public static SimWorld NewWorld(ScenarioKind kind, ulong seed, int capacity, MissionDefinition? mission = null)
        => new(seed, capacity, RosterFor(kind, mission));

    /// <summary>
    /// Lays out <paramref name="kind"/> in <paramref name="world"/>. The world
    /// must be empty; callers create it with the capacity the scenario needs.
    /// </summary>
    public static ScenarioSetup Build(SimWorld world, ScenarioKind kind)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (kind != ScenarioKind.Mission && !world.Roster.Equals(RosterFor(kind)))
        {
            throw new InvalidOperationException(
                $"A {kind} world is built with {RosterFor(kind).TeamsInPlay} declared teams; this one " +
                $"declares {world.Roster.TeamsInPlay}. Build it with Scenario.NewWorld.");
        }

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
        UnitKind.SolarPlant, UnitKind.HydroPlant, UnitKind.Factory, UnitKind.DesignBureau,
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
    /// The skirmish: a base and a starting force for every team the match declares. With the
    /// standard roster that is Σοβιετικοί (player, team 0) and Κινέζοι (ally, team 1) against
    /// Δυτικοί (team 2) — the game's premise, the two socialist powers cooperating against the
    /// Western empire — and with a roster of two teams it is a one-against-one: Σοβιετικοί against
    /// Δυτικοί, or Σοβιετικοί against Κινέζοι with the Δυτικοί nowhere on the map.
    /// <para>
    /// The teams are walked in slot order, which is the order the forces have always been spawned
    /// in, so the standard match draws exactly the same jitter from the world's generator in exactly
    /// the same order as before.
    /// </para>
    /// </summary>
    private static void BuildSkirmish(
        SimWorld world,
        List<EntityId> commandCentres,
        List<SpawnedEntity> spawned,
        List<BaseSitePlacement> baseSites)
    {
        MatchRoster roster = world.Roster;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (!roster.IsInPlay(team))
            {
                continue;
            }

            Faction faction = roster.FactionOf(team);

            SpawnForce(
                world, commandCentres, spawned, baseSites,
                faction, team, BaseSiteOf(faction),
                unitCount: UnitsPerFaction, fullBase: true, materials: 2_500, energy: 400, water: 400);
        }
    }

    /// <summary>
    /// Where a faction's base belongs on the standard map: Σοβιετικοί in the south-west, Κινέζοι in
    /// the south-east and Δυτικοί across the middle of the north. A base is a position rather than a
    /// role, so it is keyed by the faction that stands there — which is what makes a match without
    /// the Κινέζοι a map with two bases on it rather than three with one empty.
    /// </summary>
    public static WorldPos BaseSiteOf(Faction faction) => faction switch
    {
        Faction.Soviet => new WorldPos(-180_000, 0, -180_000),
        Faction.Chinese => new WorldPos(180_000, 0, -180_000),
        Faction.Western => new WorldPos(0, 0, 200_000),

        // A team playing no faction at all is not a match this game ships, but the layout still has
        // to put it somewhere rather than at the corner of the map, and the middle is the one site
        // that belongs to nobody.
        _ => default,
    };

    /// <summary>
    /// Lays out a campaign mission: a small force for each side, a base apiece, and the objectives
    /// attached to the world. The layout is a pure function of the mission definition and the
    /// world's seed, so a replay reproduces it.
    /// <para>
    /// <b>The ally is a team in a match rather than a column of this method.</b> What gets spawned
    /// is a force for every team the <em>mission</em> declares — see
    /// <see cref="MissionDefinition.Roster"/> — so the campaign's ally is a side the mission says it
    /// has, and a mission without one lays out two forces on the same map instead of a third that
    /// has nothing to do. Each team's base, its starting force and whether it gets a full base are
    /// the mission's own data, in the same order they have always been spawned.
    /// </para>
    /// <para>
    /// <b>A team the mission's columns do not describe is laid out as nothing, and that is the one
    /// way a declared side can stand in no structures.</b> A mission has three forces in it — the
    /// player's, the ally's and the enemy's — and a match may declare a fourth team that none of the
    /// three describes: the non-player force of a mission staged around one, which is a team whose
    /// side <see cref="MatchTeam.Judged"/> says the victory rule does not judge. Nothing is invented
    /// for it here. A base placed for a team the mission says nothing about would be a base standing
    /// at the middle of the map — the one site that belongs to nobody — for a team that may have
    /// been declared precisely because it holds no ground at all; what such a side starts with, the
    /// mission's script gives it, which is the same door a gun on a ridge comes through.
    /// </para>
    /// </summary>
    public static ScenarioSetup BuildMission(SimWorld world, MissionDefinition mission)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(mission);

        if (!world.Roster.Equals(mission.Roster))
        {
            throw new InvalidOperationException(
                $"Mission '{mission.Id}' declares {mission.Roster.TeamsInPlay} teams; this world was " +
                $"built with {world.Roster.TeamsInPlay}. Build it with Scenario.NewWorld.");
        }

        var commandCentres = new List<EntityId>(3);
        var spawned = new List<SpawnedEntity>(mission.PlayerUnits + mission.AllyUnits + mission.EnemyUnits + 11);
        var baseSites = new List<BaseSitePlacement>(3);

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (!mission.Roster.IsInPlay(team))
            {
                continue;
            }

            if (!TryMissionForceOf(mission, team, out WorldPos centre, out int units, out bool fullBase))
            {
                continue;
            }

            SpawnForce(
                world, commandCentres, spawned, baseSites,
                mission.Roster.FactionOf(team), team, centre,
                unitCount: units, fullBase: fullBase, materials: 1_500, energy: 300, water: 250);
        }

        world.AttachMission(mission);

        return new ScenarioSetup(commandCentres, spawned, baseSites);
    }

    /// <summary>
    /// One team's part in a mission, as the mission's own data: the base it starts from, how many
    /// units it begins with and whether it starts with a design bureau as well as the essentials.
    /// The ally is the team that starts light, which is a fact about the campaign's missions rather
    /// than about team 1.
    /// <para>
    /// False for a team the mission has no column for — the player's, the ally's and the enemy's are
    /// the three a definition carries — so the caller lays nothing out for it rather than inventing a
    /// base at the origin. See <see cref="BuildMission"/> for why that is the honest answer.
    /// </para>
    /// </summary>
    private static bool TryMissionForceOf(
        MissionDefinition mission,
        int team,
        out WorldPos centre,
        out int units,
        out bool fullBase)
    {
        switch (team)
        {
            case 0:
                centre = mission.PlayerBase;
                units = mission.PlayerUnits;
                fullBase = true;
                return true;

            case 1:
                centre = mission.AllyBase;
                units = mission.AllyUnits;
                fullBase = false;
                return true;

            case 2:
                centre = mission.EnemyBase;
                units = mission.EnemyUnits;
                fullBase = true;
                return true;

            default:
                centre = default;
                units = 0;
                fullBase = false;
                return false;
        }
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

    /// <summary>
    /// Lays out a map: the ground first, then the mission the ground was shaped for.
    /// <para>
    /// <b>The order is the world's own.</b> The height edits move the ground, the derived
    /// passes are re-run from it — the bands, the fords, the connectivity the pathfinder
    /// relies on live in those passes, and they are the reason re-derivation is the honest
    /// default rather than a choice — the surface paints go on the re-derived ground, and
    /// <em>then</em> the mission layout searches the edited land for its bases and the
    /// author's placements are asked the questions a player's construction is asked. An
    /// island is drawn before anything is put on the island, because the island is what
    /// makes the placement legal.
    /// </para>
    /// <para>
    /// The setup returns the same metadata a mission build does, because the client hangs
    /// what it draws off the setup as well as off the world. The placements are checked
    /// with <see cref="SimWorld.CanPlaceStructure"/> and the site rules — the same questions,
    /// and the same reasons, a player's order gets — and a refusal is an
    /// <see cref="InvalidDataException"/> carrying the sentence, because a map the author
    /// cannot place things on is a file this build refuses where the author is looking.
    /// </para>
    /// </summary>
    /// <summary>
    /// Applies a map's ground edits: the heights, then the re-derived passes, then the
    /// paints. The first three steps of <see cref="BuildMap"/>, public because the editor's
    /// live world applies them one edit at a time while the author works.
    /// </summary>
    public static void ApplyMapGround(SimWorld world, IReadOnlyList<TerrainEdit> edits)
    {
        ArgumentNullException.ThrowIfNull(world);

        // 1. Shape the ground. Each edit resolves to a lattice sample; the height field is
        //    clamped to its own range, and the passes below are what decide what the shape
        //    means.
        foreach (TerrainEdit edit in edits)
        {
            if (edit.Kind != TerrainEditKind.AdjustHeight)
            {
                continue;
            }

            foreach (int sample in edit.ResolveCoverage(world.Terrain))
            {
                world.Terrain.AdjustHeight(sample, edit.DeltaMm);
            }
        }

        // 2. Re-derive. The same builders the world was constructed with, run over the
        //    edited ground: this is not a second implementation of the passes, it is the
        //    passes. The re-derivation only runs when the shape moved — a paint-only
        //    edit writes the surface on the ground as it stands, and a re-derive here
        //    would re-band the map and wipe the paint it is about to apply.
        // The shape moved when any of these edits is a height edit; a paint-only call
        // writes the surface on the ground as it stands.
        if (edits.Any(edit => edit.Kind == TerrainEditKind.AdjustHeight))
        {
            world.RebuildDerivedTerrain();
        }

        // 3. Paint. After the derivation, because the bands are the ground the author
        //    started from and a paint is a decision over them.
        foreach (TerrainEdit edit in edits)
        {
            if (edit.Kind != TerrainEditKind.Paint)
            {
                continue;
            }

            int layerCell = edit.ResolveLayerCell(world.TerrainTypes);

            if (layerCell < 0)
            {
                throw new InvalidDataException(
                    $"A paint edit at ({edit.CellX}, {edit.CellZ}) cells / ({edit.X}, {edit.Z}) mm falls outside the map.");
            }

            if (edit.RadiusCells <= 0)
            {
                world.TerrainTypes.SetType(layerCell, edit.Type);
                continue;
            }

            int cx = layerCell % world.TerrainTypes.Size;
            int cz = layerCell / world.TerrainTypes.Size;

            for (int dz = -edit.RadiusCells; dz <= edit.RadiusCells; dz++)
            {
                for (int dx = -edit.RadiusCells; dx <= edit.RadiusCells; dx++)
                {
                    int x = cx + dx;
                    int z = cz + dz;

                    if ((uint)x < (uint)world.TerrainTypes.Size && (uint)z < (uint)world.TerrainTypes.Size)
                    {
                        world.TerrainTypes.SetType((z * world.TerrainTypes.Size) + x, edit.Type);
                    }
                }
            }
        }
    }

    /// <summary>
    /// Lays out a map: the ground first, then the mission the ground was shaped for.
    /// <para>
    /// <b>The order is the world's own.</b> See the class comment on
    /// <see cref="MapDefinition"/> and the ground application above. The refusals the
    /// author's placements earn are the environment's own — when <paramref name="refusals"/>
    /// is null a refusal is an exception, which is what a file load wants; when a list is
    /// given, an invalid placement is reported into it and skipped, which is what an
    /// editor's live world does, because the author is working and the next edit may make
    /// the placement legal again.
    /// </para>
    /// </summary>
    public static ScenarioSetup BuildMap(
        SimWorld world,
        MapDefinition map,
        List<(StructurePlacement Placement, string Reason)>? refusals = null)
    {
        ArgumentNullException.ThrowIfNull(world);
        ArgumentNullException.ThrowIfNull(map);

        if (map.Seed == 0)
        {
            // The seed is the ground's identity, and zero is no seed at all — the same
            // refusal a replay gives a seedless recording, because a map that regenerates
            // differently on the next machine is a map that lies.
            throw new InvalidDataException("A map carries its terrain seed.");
        }

        if (!world.Seed.Equals(map.Seed))
        {
            throw new InvalidOperationException(
                $"The map's ground is seed {map.Seed}; this world was generated from {world.Seed}. " +
                "Build it with the map's own seed.");
        }

        ApplyMapGround(world, map.TerrainEdits);

        // 4. The mission, laid out on the edited land: the same layout a campaign mission
        //    gets, searched for on the ground that now exists rather than the one the seed
        //    used to generate.
        ScenarioSetup setup = BuildMission(world, map.EffectiveMission);

        // 5. The author's placements, asked the same questions the player's construction
        //    is asked — the ground under them is the edited ground, which is the point of
        //    shaping it first.
        foreach (StructurePlacement placement in map.Structures)
        {
            var site = new WorldPos(placement.X, 0, placement.Z);

            if (!world.CanPlaceStructure(placement.Kind, site, out string reason) ||
                !world.IsSiteClear(placement.Kind, site, out reason))
            {
                if (refusals is null)
                {
                    throw new InvalidDataException(
                        $"A placed {placement.Kind} at ({placement.X}, {placement.Z}) is refused: {reason}.");
                }

                refusals.Add((placement, reason));
                continue;
            }

            SpawnStructure(world, world.FactionOfTeam(placement.Team), placement.Team, placement.Kind, site, health: 0, spawned: []);
        }

        return setup;
    }
}
