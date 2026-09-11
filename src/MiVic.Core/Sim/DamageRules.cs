using MiVic.Core.Terrain;

namespace MiVic.Core.Sim;

/// <summary>
/// <b>The one place a hit becomes damage.</b> Every path by which a weapon, a salvo or an off-map
/// strike takes health off a unit comes through <see cref="Against"/>, so there is one answer to
/// "how much did that hurt" rather than one per caller.
/// <para>
/// <b>Two multipliers, and the order they apply in is settled here and nowhere else.</b>
/// </para>
/// <list type="number">
/// <item><description>
/// <b>Cover first</b> — <see cref="TerrainLayer.CoverPermille"/> for the ground the target is
/// standing on, asked of its own movement class. It is <em>where the target is</em>: a wood, a
/// basin, a crest that skylines it.
/// </description></item>
/// <item><description>
/// <b>Armour second</b> — <see cref="UnitCatalog.ArmourPermille"/>, the role's own construction
/// times its owner's philosophy. It is <em>what the target is made of</em>: poured concrete, a
/// light hull, an aircraft's skin.
/// </description></item>
/// </list>
/// <para>
/// <b>Why that order, when two permille multipliers ought to commute.</b> In exact arithmetic they
/// do, and in integer arithmetic they do not: a 35-damage shell through a 927 wood against 970
/// armour is 31 the one way round and 30 the other, because each step truncates. So the order has
/// to be chosen and written down rather than left to whichever line a future edit happens to put
/// first. Cover goes outside because <em>cover is the outer layer</em>: the round crosses the
/// ground before it meets the plate, and what the bank stops never reaches the armour at all.
/// It also keeps the crest honest — ground that <em>adds</em> damage (1 100 permille is a unit
/// skylined on a ridge) adds it to the shot, and the armour then reduces the larger number,
/// which is what a tank on a crest being easier to hit means.
/// </para>
/// <para>
/// <b>One floor, applied once, at the end.</b> A hit that lands always does at least
/// <see cref="MinimumDamage"/>, however much ground and plate stand in the way. The floor is not
/// applied between the two steps: a clamp in the middle would let each multiplier invent a point
/// of damage that the other one then reduces, and it would swallow the rounding difference that
/// makes the order observable at all. The reasoning is the same one
/// <see cref="TerrainLayer.MinCoverPermille"/> gives — a defender who cannot be hurt must be out
/// of range or unseen, never saved by a multiplier that rounded to nothing — and it is what makes
/// the "immune to rifles" case unreachable by construction rather than by care.
/// </para>
/// <para>
/// <b>A zero-damage hit does nothing rather than one point.</b> Nothing in the simulation calls
/// this with a non-positive number — an unarmed role never fires and
/// <see cref="AbilityCatalog"/> returns before its blast — but the floor is a statement about a
/// hit that <em>lands</em>, and a non-hit is not one.
/// </para>
/// </summary>
public static class DamageRules
{
    /// <summary>
    /// The least a hit that lands can do. One, because a shot that does nothing at all reads as a
    /// bug rather than as a rule, and because "untouchable" has to be a property of range or of
    /// sight rather than of a rounding.
    /// </summary>
    public const int MinimumDamage = 1;

    /// <summary>
    /// What a hit of <paramref name="damage"/> does to one live entity, after the ground it stands
    /// on and the armour it is made of.
    /// <para>
    /// This is the overload every damage path calls. It reads the target from the world rather than
    /// from a caller's copy of it, so a caller cannot pass the wrong cover — which is exactly the
    /// bug the salvo path had: one cover value was worked out for the sticky target and then
    /// applied to everybody caught in the blast, so a Κατιούσα fired at a tank in a wood did
    /// reduced damage to units standing in the open beside it.
    /// </para>
    /// </summary>
    /// <param name="world">The world the target is standing in.</param>
    /// <param name="slot">The target's slot. Must hold something alive.</param>
    /// <param name="damage">The shot's damage before anything reduces it.</param>
    public static int Against(SimWorld world, int slot, int damage)
    {
        ArgumentNullException.ThrowIfNull(world);

        ref Entity target = ref world.GetRefBySlot(slot);
        UnitDefinition definition = UnitCatalog.Get(target.Kind);

        int cover = world.TerrainTypes.CoverAt(
            world.TerrainTypes.IndexOfWorld(target.Position.X, target.Position.Z),
            definition.Movement);

        return Compose(damage, cover, UnitCatalog.ArmourPermille(target.Faction, target.Kind));
    }

    /// <summary>
    /// The arithmetic itself, with no world in it: the documented order, the integer truncation
    /// and the one floor. It is public because it is the rule a test should be able to state
    /// without building a battlefield, and because the interface and the probe can then show a
    /// player the same number the gun will use instead of a second copy of the sums.
    /// </summary>
    /// <param name="damage">The shot's damage before anything reduces it.</param>
    /// <param name="coverPermille">What the ground lets through, 1 000 being no cover at all.</param>
    /// <param name="armourPermille">What the target's construction lets through, 1 000 being none.</param>
    public static int Compose(int damage, int coverPermille, int armourPermille)
    {
        if (damage <= 0)
        {
            return 0;
        }

        long afterCover = ((long)damage * coverPermille) / TerrainLayer.NoCoverPermille;
        long afterArmour = (afterCover * armourPermille) / 1_000;

        return (int)Math.Max(MinimumDamage, afterArmour);
    }
}
