namespace MiVic.Core.Sim;

/// <summary>
/// How many units a side may field, and how many it is fielding: a ceiling granted by the
/// structures a team owns, and the supply its live units spend against it.
/// <para>
/// <b>The ceiling comes from buildings, and only from buildings that support an army.</b> A
/// headquarters is what a side commands from — its staff, its signals, its depots, and the only
/// structure in the game whose whole purpose is the army rather than the economy — so it is worth
/// two hundred places. A factory is a yard: it supports the armour it makes and it is not a
/// headquarters, so it is worth sixty. A power plant and a design bureau run a base rather than an
/// army and are worth thirty each, and the nuclear plant — the largest industrial site outside a
/// headquarters, five cells by five — sits with the yard at sixty. What grants nothing is
/// everything that <em>is</em> the army: a Πυροβολείο is a concrete pit with a gun in it, an
/// Αντιαεροπορικό Πυροβολείο is the same pit looking up, and a Σταθμός Ραντάρ is a dish on a mast.
/// A gun is not a headquarters and a radar is not either, and a rule that let a defensive line pay
/// for the army standing behind it would make the ceiling a function of how much concrete a player
/// could pour rather than of how much of an army they could command.
/// </para>
/// <para>
/// <b>Nothing here is stored, and the difference from <see cref="PowerSystem"/> is worth stating
/// because the two are otherwise the same kind of thing.</b> The power ledger is recomputed every
/// tick and kept, because its answer decides something — which dishes are on the air — and every
/// reader for the rest of the tick has to see that same answer. Capacity decides nothing on its own
/// behalf: it is a ceiling that one question is asked against, and the question is asked at the
/// instant of asking. So it is derived at the instant of asking as well, and that is why the figures
/// are not fields of <see cref="TeamState"/>, are not folded into <see cref="StateHash"/>, and have
/// no tick order to get right: the state hash covers what a replay could disagree about, and two
/// peers cannot disagree about a sum over the entities they already agree on. It also means a world
/// at tick zero already knows what its army costs before a single step has run, and a structure
/// destroyed this tick lowers the ceiling this tick rather than next.
/// </para>
/// <para>
/// The one number that is stored anywhere is the faction's own multiplier,
/// <see cref="FactionProfile.CapacityPermille"/>: the Κινέζοι field more out of the same four
/// buildings, the Σοβιετικοί fewer, and the Δυτικοί are the baseline the other two are measured
/// against.
/// </para>
/// </summary>
public static class CapacitySystem
{
    /// <summary>
    /// Places a finished structure of this role supports, before the owner's own multiplier.
    /// <para>
    /// The table is the whole of "a structure grants capacity when it supports an army rather than
    /// being one", and anything not in it grants nothing — which is the honest answer for a
    /// building whose job is to shoot rather than to command. The nuclear plant is the one entry
    /// that needs saying out loud: it is a power structure like the plant it upgrades, but it is
    /// also five cells of ground and the largest industrial undertaking on the roster, so it
    /// supports what a factory supports rather than what a shed supports.
    /// </para>
    /// </summary>
    public static int GrantOf(UnitKind kind) => kind switch
    {
        UnitKind.CommandCentre => 200,
        UnitKind.Factory => 60,
        UnitKind.NuclearPlant => 60,
        UnitKind.PowerPlant => 30,
        UnitKind.DesignBureau => 30,
        _ => 0,
    };

    /// <summary>
    /// How many places a team's structures support: every finished structure it owns, its grant
    /// scaled by the faction's own figure.
    /// <para>
    /// A building site grants nothing, which is the same rule the economy, the guns and the power
    /// ledger already follow — a structure under construction produces nothing, draws nothing and
    /// watches nothing — and it is the honest reading of "a structure supports an army": a hole in
    /// the ground supports nobody. A finished structure raises the ceiling on the tick the last of
    /// its rise is done, and not before.
    /// </para>
    /// </summary>
    public static int CapacityOf(SimWorld world, int team)
    {
        ArgumentNullException.ThrowIfNull(world);

        if ((uint)team >= SimConstants.TeamCount)
        {
            return 0;
        }

        Faction faction = world.FactionOfTeam(team);
        int permille = faction == Faction.None ? 1_000 : FactionProfile.For(faction).CapacityPermille;
        permille = permille > 0 ? permille : 1_000;

        int capacity = 0;
        int slots = world.Capacity;

        for (int slot = 0; slot < slots; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId != team || entity.ConstructionTicksRemaining > 0)
            {
                continue;
            }

            capacity += GrantOf(entity.Kind);
        }

        // Scaled once, at the end, rather than per building: the multiplier is a fact about the
        // owner, so a headquarters and a factory are worth the same fraction of each other
        // whoever poured them, and rounding cannot make two buildings worth less together than
        // they are apart.
        return (capacity * permille) / 1_000;
    }

    /// <summary>
    /// What a team's army costs against its ceiling: every live unit it owns, priced by
    /// <see cref="UnitCatalog.SupplyCost"/>. Structures are worth nothing here, for the reason
    /// that method gives.
    /// <para>
    /// <b>What is counted is what is fielded, not what is ordered.</b> A hull on a factory's pad
    /// is not a unit on the map, and the ceiling is about the map: a team that queues past its
    /// limit simply stops producing when the units land and the sum goes over. Counting the queue
    /// would make the answer to "how much have I got" depend on a building's progress bar, and a
    /// player cannot see that number anywhere.
    /// </para>
    /// </summary>
    public static int SupplyOf(SimWorld world, int team)
    {
        ArgumentNullException.ThrowIfNull(world);

        if ((uint)team >= SimConstants.TeamCount)
        {
            return 0;
        }

        int supply = 0;
        int slots = world.Capacity;

        for (int slot = 0; slot < slots; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team)
            {
                supply += UnitCatalog.SupplyCost(entity.Kind);
            }
        }

        return supply;
    }

    /// <summary>
    /// How far over its ceiling a team is, in places, or zero when it is within it. Being
    /// <em>at</em> the ceiling is not over it: the ceiling is how many a side may field, so a team
    /// whose army costs exactly what its structures support is legal and may keep producing until
    /// the next unit would take it past.
    /// </summary>
    public static int OverCapacityOf(SimWorld world, int team)
        => Math.Max(0, SupplyOf(world, team) - CapacityOf(world, team));

    /// <summary>True when a team is fielding more than its structures support.</summary>
    public static bool IsOver(SimWorld world, int team) => OverCapacityOf(world, team) > 0;

    /// <summary>
    /// Why a team may not field another unit, in the game's own Greek, given how far over it is.
    /// <para>
    /// It lives in the simulation rather than in the client for the same reason
    /// <see cref="PowerSystem.DimmedReason"/> and <see cref="UnitCatalog.GreekName"/> do: a reason
    /// assembled in the interface would be the one sentence in the player's own language that no
    /// test could reach. It is written in the same voice as the refusals it joins — "λείπουν 120 Π"
    /// for a resource and "λείπει ισχύς 3 Ε" for a brown-out — and names the rule the player has to
    /// act on. <c>λείπει</c> rather than <c>λείπουν</c>, because capacity is a quantity rather than
    /// a count of things: what is missing is 174 places, not 174 places' worth of something.
    /// </para>
    /// </summary>
    public static string OverCapacityReason(int over)
        => $"λείπει δυναμικότητα {over}";
}
