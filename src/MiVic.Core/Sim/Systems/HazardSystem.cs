namespace MiVic.Core.Sim;

/// <summary>
/// Burns whatever is standing in a volcano's crater.
/// <para>
/// Lava is already impassable, so nothing walks into it — but units can be ordered
/// into a cell that becomes lava, be pushed there, or be built where a crater later
/// opens. Without this, lava would be a wall; with it, it is a hazard, which is what
/// makes a volcano a piece of terrain rather than scenery.
/// </para>
/// </summary>
public static class HazardSystem
{
    /// <summary>Damage a tick in lava. Fast enough to be fatal, slow enough to escape.</summary>
    public const int LavaDamagePerTick = 6;

    /// <summary>Structures are not immune, and a base built on a crater should hurt.</summary>
    public const int StructureDamageDivisor = 4;

    /// <summary>Runs one hazard tick.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            // Anything airborne is over the lava, not in it.
            if (entity.AltitudeMm > 0)
            {
                continue;
            }

            int cell = world.Navigation.IndexOfWorld(entity.Position);

            if (cell < 0 || world.TerrainTypes.TypeAt(cell) != MiVic.Core.Terrain.TerrainType.Lava)
            {
                continue;
            }

            UnitDefinition definition = UnitCatalog.Get(entity.Kind);
            int damage = definition.IsBuilding ? Math.Max(1, LavaDamagePerTick / StructureDamageDivisor) : LavaDamagePerTick;

            // Nothing is asked about friend or foe here, and that is the point: lava has no
            // owner, so it is the one damage path that cannot point a weapon at anybody. It
            // burns whoever is standing in it — the caster's own units included — which is the
            // same "does not care who it hits" case an ability declares with
            // DamagesFriendlies, and not a hostility question answered wrongly.
            if (entity.Health <= damage)
            {
                world.Despawn(new EntityId(slot, entity.Generation));
            }
            else
            {
                entity.Health -= damage;
            }
        }
    }
}
