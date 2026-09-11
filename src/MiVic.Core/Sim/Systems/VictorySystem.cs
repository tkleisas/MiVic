namespace MiVic.Core.Sim;

/// <summary>How the battle ended.</summary>
public enum GameOutcome : byte
{
    /// <summary>Still fighting.</summary>
    Ongoing = 0,

    /// <summary>The player's side is the last one standing.</summary>
    Victory = 1,

    /// <summary>The player's side has been broken.</summary>
    Defeat = 2,

    /// <summary>Every side was wiped out.</summary>
    Draw = 3,
}

/// <summary>
/// Decides when the battle is over.
/// <para>
/// A side is defeated when it has no structures left. Losing a headquarters is not the end — a
/// player with a factory can still rebuild — so the check is on every building, not just the
/// command centre. The outcome is part of the simulation state, which means a replay reaches the
/// same verdict on the same tick.
/// </para>
/// <para>
/// <b>The rule is "the player's side has no enemies left", asked of the match.</b> It used to be
/// written as <c>HasStructures(0) || HasStructures(1)</c> against <c>HasStructures(2)</c>, which
/// answers a question about the standard three-faction skirmish rather than about who is on the
/// map. Two things followed from that, and both are the reason this asks
/// <see cref="SimWorld.Roster"/> instead. A match with a different cast — Σοβιετικοί against
/// Κινέζοι with the Δυτικοί absent, or Σοβιετικοί against Δυτικοί with no ally — was either
/// decided on the first check, because a team that is not playing has no structures and a missing
/// enemy reads as a dead one, or could never be decided at all, because the teams it did require
/// destroyed were not on the map. And a team that is not in the match must never be something the
/// check requires destroying, which is the same requirement a neutral third party will have.
/// </para>
/// <para>
/// What is counted is <em>sides</em>, not teams, and the sides come from the match
/// (<see cref="MatchRoster.SideOf"/>). One enemy, two enemies or three resolve through the same
/// line, and so does an enemy that is not the one this game used to assume: a side is alive when
/// any team playing on it still owns a building, and the match is over when at most one side is.
/// The enemy's identity is never consulted — only whether the player's side still has one.
/// </para>
/// <para>
/// <b>And one kind of side is not judged at all.</b> A match can declare a team as a
/// <em>non-player force</em> — <see cref="MatchTeam.Judged"/> is false — which is a side that holds
/// objectives rather than ground: the scientists of an outpost waiting to be carried out of it, a
/// remnant that exists to be reached rather than defeated. Such a side owns no base and therefore no
/// structures, and asking this rule about it would be asking a question it cannot answer: a side
/// that never held ground is indistinguishable, in the only currency this rule has, from a side
/// that has been destroyed. So those teams are skipped, and when the skipping leaves the rule with
/// nothing to measure at one end — no judged team on the player's side, or none off it — the rule
/// gives no verdict and says <see cref="GameOutcome.Ongoing"/>, because the objectives are what
/// decide a match staged around such a side. A match that declares none of them is walked exactly
/// as it always was, which is why the three-faction skirmish and the two-faction matches are
/// unaffected by any of this.
/// </para>
/// </summary>
public static class VictorySystem
{
    /// <summary>Ticks between checks. Once a second is plenty.</summary>
    public const int CheckInterval = 20;

    /// <summary>Runs one victory check if this tick is due.</summary>
    public static void Tick(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Outcome != GameOutcome.Ongoing || world.Tick % CheckInterval != 0)
        {
            return;
        }

        world.SetOutcome(Decide(world));
    }

    /// <summary>
    /// The verdict this world has earned right now, without waiting for the next check.
    /// <para>
    /// Split out from <see cref="Tick"/> so that a test can ask what the rule says about a world it
    /// has just arranged, and so the interface can read the same verdict the simulation reached
    /// rather than a second opinion about it.
    /// </para>
    /// <para>
    /// <b>A team the match declares as not judged is not counted at either end</b>, and the rule
    /// answers <see cref="GameOutcome.Ongoing"/> when that leaves it with nothing to measure: a
    /// verdict needs a side to be the last one standing <em>of</em>. See the class comment for what
    /// such a side is and why this rule must not read its missing ground as a defeat.
    /// </para>
    /// </summary>
    public static GameOutcome Decide(SimWorld world)
    {
        ArgumentNullException.ThrowIfNull(world);

        MatchRoster roster = world.Roster;
        int playerSide = roster.PlayerSide;
        bool playerSideAlive = false;
        bool enemySideAlive = false;
        bool playerSideJudged = false;
        bool enemySideJudged = false;
        bool anUnjudgedTeamIsPlaying = false;

        // Teams in slot order, so two runs of the same world reach the verdict in the same order
        // and a `||` over the sides cannot depend on which team happened to be scanned first.
        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (!roster.IsInPlay(team))
            {
                // Not in this match, so nothing about it can be required of anybody. This is the
                // clause that lets a match be fought with one faction absent.
                continue;
            }

            if (!roster.IsJudged(team))
            {
                // A non-player force: a side that holds objectives rather than ground. It owns no
                // structures by design, so its emptiness is not a defeat and its survival is not a
                // victory — the mission's objectives decide what became of it.
                anUnjudgedTeamIsPlaying = true;
                continue;
            }

            bool alive = HasStructures(world, team);

            if (roster.SideOf(team) == playerSide)
            {
                playerSideJudged = true;
                playerSideAlive |= alive;
            }
            else
            {
                enemySideJudged = true;
                enemySideAlive |= alive;
            }
        }

        // Nothing to measure at one end: the match is staged around a side this rule does not judge,
        // so the rule has no verdict to give and the objectives are what decide. Reachable only
        // where the match declares such a team, which is what keeps a match that declares none
        // decided by exactly the lines below.
        if (anUnjudgedTeamIsPlaying && (!playerSideJudged || !enemySideJudged))
        {
            return GameOutcome.Ongoing;
        }

        return (playerSideAlive, enemySideAlive) switch
        {
            (true, false) => GameOutcome.Victory,
            (false, true) => GameOutcome.Defeat,
            (false, false) => GameOutcome.Draw,
            _ => GameOutcome.Ongoing,
        };
    }

    /// <summary>True when a team still owns at least one structure.</summary>
    public static bool HasStructures(SimWorld world, int team)
    {
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                return true;
            }
        }

        return false;
    }

    /// <summary>Number of structures a team still owns.</summary>
    public static int CountStructures(SimWorld world, int team)
    {
        int count = 0;
        int capacity = world.Capacity;

        for (int slot = 0; slot < capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && UnitCatalog.Get(entity.Kind).IsBuilding)
            {
                count++;
            }
        }

        return count;
    }

    /// <summary>
    /// <b>How many structures a side owns between the teams the match declares on it</b> — the number
    /// the rule's own question is asked with, and asked of a side rather than of a team because a
    /// side is what it counts.
    /// <para>
    /// It sums <see cref="CountStructures"/> over the declared teams on the side, in slot order, so
    /// it cannot disagree with the verdict about what standing in something means. A side with no
    /// declared team on it owns nothing, which is the same answer an undeclared team's own count
    /// gives.
    /// </para>
    /// <para>
    /// Two callers outside the verdict, and both want it for the same reason: the mission validator,
    /// which reads a side that stands in nothing as the authoring mistake it is unless the match
    /// declared it a non-player force, and the defeat banner, which may only say the player's side
    /// was destroyed when it holds nothing — the same fact the rule calls a defeat.
    /// </para>
    /// </summary>
    public static int CountSideStructures(SimWorld world, int side)
    {
        ArgumentNullException.ThrowIfNull(world);

        MatchRoster roster = world.Roster;
        int count = 0;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (roster.IsInPlay(team) && roster.SideOf(team) == side)
            {
                count += CountStructures(world, team);
            }
        }

        return count;
    }

    /// <summary>True when any team the match declares on this side still owns a structure.</summary>
    public static bool SideHasStructures(SimWorld world, int side) => CountSideStructures(world, side) > 0;
}
