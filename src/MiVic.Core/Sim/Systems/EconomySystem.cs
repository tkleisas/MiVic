namespace MiVic.Core.Sim;

/// <summary>
/// Turns structures into income.
/// <para>
/// Rates are recomputed from scratch every tick rather than cached, so a building
/// that finishes or is destroyed changes income immediately and the simulation
/// never carries hidden state that a replay could disagree about.
/// </para>
/// </summary>
public static class EconomySystem
{
    /// <summary>Materials per tick from a command centre.</summary>
    public const int CommandCentreMaterials = 3;

    /// <summary>Energy per tick from a power plant.</summary>
    public const int PowerPlantEnergy = 10;

    /// <summary>Water per tick from a command centre: wells and purification.</summary>
    public const int CommandCentreWater = 1;

    /// <summary>Water per tick from a power plant: cooling towers recondense a lot.</summary>
    public const int PowerPlantWater = 2;

    /// <summary>Runs one economy tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            ref TeamState state = ref world.TeamRef(team);
            state.MaterialsPerTick = 0;
            state.EnergyPerTick = 0;
            state.WaterPerTick = 0;
            state.ArmedCount = 0;
            state.WagesPerTick = 0;
        }

        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if ((uint)entity.TeamId >= SimConstants.TeamCount)
            {
                continue;
            }

            ref TeamState state = ref world.TeamRef(entity.TeamId);

            // Income scales with the faction's wealth multiplier; upkeep does not.
            // A rich faction should be able to afford more, not run cheaper.
            int income = FactionProfile.For(entity.Faction).IncomePermille;
            income = income > 0 ? income : 1_000;

            UnitDefinition definition = UnitCatalog.Get(entity.Kind);

            if (definition.IsArmed && !definition.IsBuilding)
            {
                state.ArmedCount++;
                state.WagesPerTick += definition.WagePerTick;
            }

            switch (entity.Kind)
            {
                case UnitKind.CommandCentre:
                    // A headquarters is energy neutral on purpose. If it drew
                    // power, a team with nothing but a headquarters would sit at
                    // zero energy, and production halts at zero energy — the base
                    // could never build the power plant that would fix it.
                    state.MaterialsPerTick += Scale(CommandCentreMaterials, income);
                    state.WaterPerTick += Scale(CommandCentreWater, income);
                    break;

                case UnitKind.PowerPlant:
                    state.EnergyPerTick += Scale(PowerPlantEnergy, income);
                    state.WaterPerTick += Scale(PowerPlantWater, income);
                    break;

                case UnitKind.Factory:
                    state.EnergyPerTick -= 4;
                    break;

                case UnitKind.DesignBureau:
                    state.EnergyPerTick -= 3;
                    break;
            }
        }

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            ref TeamState state = ref world.TeamRef(team);

            state.Materials = Math.Max(0, state.Materials + state.MaterialsPerTick);
            state.Energy = Math.Max(0, state.Energy + state.EnergyPerTick);
            state.Water = Math.Max(0, state.Water + state.WaterPerTick);

            PayUpkeep(ref state, SimWorld.FactionOfTeam(team));
        }
    }

    /// <summary>
    /// Pays this tick's political and contractual bills.
    /// <para>
    /// Wages come first, because contract troops are the first to notice an empty
    /// treasury. If a bill cannot be paid, the flag that says so is what the morale
    /// system reads — an army that is not being paid does not merely get poorer, it
    /// stops wanting to fight.
    /// </para>
    /// </summary>
    private static void PayUpkeep(ref TeamState state, Faction faction)
    {
        // The fourth team slot is neutral and has no profile, so it owes nothing.
        if (faction == Faction.None)
        {
            state.UpkeepPerTick = 0;
            state.WagesPaid = true;
            state.PropagandaPaid = true;
            return;
        }

        FactionProfile profile = FactionProfile.For(faction);

        int propaganda = profile.PropagandaDivisor > 0
            ? (state.ArmedCount + profile.PropagandaDivisor - 1) / profile.PropagandaDivisor
            : 0;

        state.UpkeepPerTick = propaganda + state.WagesPerTick;

        if (state.Materials >= state.WagesPerTick)
        {
            state.Materials -= state.WagesPerTick;
            state.WagesPaid = true;
        }
        else
        {
            state.Materials = 0;
            state.WagesPaid = state.WagesPerTick == 0;
        }

        if (state.Materials >= propaganda)
        {
            state.Materials -= propaganda;
            state.PropagandaPaid = true;
        }
        else
        {
            state.PropagandaPaid = propaganda == 0;
        }
    }

    /// <summary>Applies a faction's income multiplier to a per-tick yield.</summary>
    private static int Scale(int amount, int incomePermille)
        => (amount * incomePermille) / 1_000;
}
