namespace MiVic.Core.Sim;

/// <summary>Off-map support a team can call in. Values are stable because they are saved.</summary>
public enum AbilityId : byte
{
    None = 0,

    /// <summary>Σοβιετικοί: a kinetic strike from the orbital platform.</summary>
    OrbitalStrike = 1,

    /// <summary>Tactical nuclear weapon. Both superpowers have one; the Κινέζοι never do.</summary>
    TacticalNuke = 2,
}

/// <summary>
/// A targeted ability: what it costs, how often it may be used, and what it does.
/// <para>
/// Abilities are data for the same reason units are. The alternative — a switch on
/// faction inside the simulation — is exactly the special-casing the design keeps
/// out of the systems.
/// </para>
/// </summary>
/// <param name="Id">Stable identifier.</param>
/// <param name="Faction">The only faction that may use it, or <see cref="Faction.None"/> for any.</param>
/// <param name="GreekName">Player-facing name.</param>
/// <param name="GreekDescription">One-line explanation, in Greek.</param>
/// <param name="RequiredTechTier">Era the team must have reached.</param>
/// <param name="RequiredTech">Project that must be complete, if any.</param>
/// <param name="RequiredStructure">Structure the team must have standing, if any.</param>
/// <param name="MaterialCost">Materials consumed per use.</param>
/// <param name="CooldownTicks">Ticks before it may be used again.</param>
/// <param name="Damage">Damage dealt to everything inside the radius.</param>
/// <param name="RadiusMm">Blast radius.</param>
/// <param name="DamagesFriendlies">True when the blast does not distinguish friend from foe.</param>
public readonly record struct AbilityDefinition(
    AbilityId Id,
    Faction Faction,
    string GreekName,
    string GreekDescription,
    int RequiredTechTier,
    TechId RequiredTech,
    UnitKind RequiredStructure,
    int MaterialCost,
    int CooldownTicks,
    int Damage,
    int RadiusMm,
    bool DamagesFriendlies);

/// <summary>Every off-map support option in the game.</summary>
public static class AbilityCatalog
{
    private static readonly AbilityDefinition[] Abilities =
    [
        // The orbital strike is the payoff of the Σοβιετικοί deep tech chain: it
        // needs the Κόκκινος Ουρανός project, so it arrives late and only for the
        // faction that went all the way down that branch.
        new(
            AbilityId.OrbitalStrike,
            Faction.Soviet,
            "Τροχιακό Πλήγμα",
            "Κινητική βολή από την τροχιακή πλατφόρμα. Πλήττει μόνο εχθρούς.",
            RequiredTechTier: 3,
            RequiredTech: TechId.SovietOrbital,
            RequiredStructure: UnitKind.DesignBureau,
            MaterialCost: 250,
            CooldownTicks: 1_200,
            Damage: 350,
            RadiusMm: 55_000,
            DamagesFriendlies: false),

        // A nuke does not distinguish friend from foe, and it needs a nuclear power
        // plant standing — which makes the plant a target worth raiding, the first
        // structure in the game whose value is not its income.
        new(
            AbilityId.TacticalNuke,
            Faction.None,
            "Τακτικό Πυρηνικό Όπλο",
            "Πλήγμα μεγάλης ακτίνας. Δεν ξεχωρίζει φίλους από εχθρούς.",
            RequiredTechTier: 4,
            RequiredTech: TechId.None,
            RequiredStructure: UnitKind.NuclearPlant,
            MaterialCost: 600,
            CooldownTicks: 2_400,
            Damage: 900,
            RadiusMm: 110_000,
            DamagesFriendlies: true),
    ];

    /// <summary>Every ability, in catalogue order.</summary>
    public static ReadOnlySpan<AbilityDefinition> All => Abilities;

    /// <summary>Number of abilities, for fixed per-team cooldown arrays.</summary>
    public static int Count => Abilities.Length;

    /// <summary>Looks up an ability.</summary>
    public static bool TryGet(AbilityId id, out AbilityDefinition definition)
    {
        foreach (AbilityDefinition candidate in Abilities)
        {
            if (candidate.Id == id)
            {
                definition = candidate;
                return true;
            }
        }

        definition = default;
        return false;
    }

    /// <summary>Index of an ability in <see cref="All"/>, or -1.</summary>
    public static int IndexOf(AbilityId id)
    {
        for (int i = 0; i < Abilities.Length; i++)
        {
            if (Abilities[i].Id == id)
            {
                return i;
            }
        }

        return -1;
    }

    /// <summary>Abilities a faction could ever use, in catalogue order.</summary>
    public static IEnumerable<AbilityDefinition> AvailableTo(Faction faction, int techTier)
    {
        foreach (AbilityDefinition ability in Abilities)
        {
            if (ability.RequiredTechTier <= techTier &&
                (ability.Faction == Faction.None || ability.Faction == faction))
            {
                yield return ability;
            }
        }
    }
}
