namespace MiVic.Core.Sim;

/// <summary>
/// Advances the research project in progress and applies its effect when it
/// completes.
/// <para>
/// A completed project either raises the team's tech tier — unlocking the next
/// era of hardware — or permanently improves its units. The improvements are
/// cached on the team as multipliers so the hot systems never walk the tree.
/// </para>
/// </summary>
public static class ResearchSystem
{
    /// <summary>Runs one research tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            ref TeamState state = ref world.TeamRef(team);

            if (!state.IsResearching)
            {
                continue;
            }

            state.ResearchTicksRemaining--;

            if (state.ResearchTicksRemaining > 0)
            {
                continue;
            }

            Complete(ref state, state.ResearchingTech);
            state.ResearchTicksRemaining = 0;
            state.ResearchingTech = TechId.None;

            RefreshModifiers(ref state);
        }
    }

    /// <summary>Marks a project finished and applies its immediate effect.</summary>
    private static void Complete(ref TeamState state, TechId id)
    {
        if (id == TechId.None || !TechCatalog.TryGet(id, out TechProject project))
        {
            return;
        }

        state.TechMask |= 1UL << (int)id;

        if (project.Effect == TechEffect.AdvanceTier && project.Value > state.TechTier)
        {
            state.TechTier = project.Value;
        }
    }

    /// <summary>
    /// Recomputes the team's cached multipliers from its completed projects.
    /// Baseline is 1000; projects multiply, so later research compounds.
    /// </summary>
    public static void RefreshModifiers(ref TeamState state)
    {
        int damage = 1_000;
        int armor = 1_000;
        int speed = 1_000;
        int production = 1_000;
        int morale = 0;

        foreach (TechProject project in TechCatalog.All)
        {
            if (!TechCatalog.IsCompleted(state.TechMask, project.Id))
            {
                continue;
            }

            switch (project.Effect)
            {
                case TechEffect.Damage:
                    damage = (damage * project.Value) / 1_000;
                    break;
                case TechEffect.Armor:
                    armor = (armor * project.Value) / 1_000;
                    break;
                case TechEffect.Speed:
                    speed = (speed * project.Value) / 1_000;
                    break;
                case TechEffect.Production:
                    production = (production * project.Value) / 1_000;
                    break;
                case TechEffect.Morale:
                    morale += project.Value;
                    break;
            }
        }

        state.DamagePermille = damage;
        state.ArmorPermille = armor;
        state.SpeedPermille = speed;
        state.ProductionPermille = production;
        state.MoraleBonusRaw = morale;
    }
}
