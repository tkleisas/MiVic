using MiVic.Core.Campaign;

namespace MiVic.Core.Sim;

/// <summary>
/// One team's place in a match: which faction it plays and which side it is on.
/// </summary>
/// <param name="Team">Team slot being declared, in <c>[0, SimConstants.TeamCount)</c>.</param>
/// <param name="Faction">
/// The faction this team plays. It is an assignment rather than the team's number: an entity already
/// carries <see cref="Entity.Faction"/> and <see cref="Entity.TeamId"/> as separate fields, so a
/// team was never obliged to be its index — what a team may build, research and call in has always
/// been read from a faction, and this is where that faction is said out loud.
/// </param>
/// <param name="Side">
/// Which side the team fights for. Two teams on the same side are allies; a team on its own side has
/// no ally at all, which is what a one-against-one match is.
/// </param>
/// <param name="Judged">
/// <b>Whether the last-side-standing rule judges this team, which is the one thing a side can be that
/// is not a faction.</b> True for everything that plays for the map, which is every team this game
/// shipped before this field existed — so the default is true and a match that says nothing about it
/// is the match it always was.
/// <para>
/// False declares a <em>non-player force</em>: a side that holds objectives rather than ground. The
/// scientists of a research outpost waiting to be carried out of it, a remnant that exists to be
/// reached rather than defeated, a generator that belongs to nobody and is at war with everybody —
/// each is a side with no base and no structures, and each is a side the rule cannot tell from one
/// that has been destroyed. See <see cref="VictorySystem.Decide"/>, where that reading is refused.
/// </para>
/// <para>
/// It is not an exemption from being fought: such a side is still hostile to everyone it is not
/// allied to, still stands on the map and can still be shot at. What it is exempt from is being
/// <em>won against</em> — the objectives of the mission decide whether it was carried out or
/// prevented, and the rule has no verdict to give about it either way.
/// </para>
/// </param>
public readonly record struct MatchTeam(int Team, Faction Faction, int Side, bool Judged = true);

/// <summary>
/// <b>What is playing this match: which teams are in it, what faction each one plays, and who is on
/// whose side.</b>
/// <para>
/// Every match used to be described by the shape of its numbers. A team's faction was its index
/// (<c>0 = Σοβιετικοί, 1 = Κινέζοι, 2 = Δυτικοί</c>), the alliance was two ids written into a
/// predicate (<c>AreAllied(a, b) = 0 and 1 against everything else</c>), the victory condition asked
/// after teams 0, 1 and 2 by number, and the interface drew three faction rows whether or not three
/// factions were on the map. None of that could say "Σοβιετικοί against Κινέζοι with the Δυτικοί
/// absent", because two of those facts are not facts about a team id at all: they are facts about
/// <em>this match</em>.
/// </para>
/// <para>
/// So a match declares them. The declaration is one object, made once, held by the world it belongs
/// to (<see cref="SimWorld.Roster"/>) and read from there by everything that needs to know who is
/// playing: the victory check, the AI, the interface, the client's palette and labels, and the
/// probe. Nothing else in the engine is allowed to answer the question by index — an entity's own
/// <see cref="Entity.Faction"/> still says what the unit <em>is</em>, and this says who is
/// <em>playing</em>.
/// </para>
/// <para>
/// <b>The rule of the alliance is written here and nowhere else.</b>
/// <see cref="SimWorld.AreAllied"/> and <see cref="SimWorld.IsHostile"/> forward to
/// <see cref="AreAllied"/> and <see cref="IsHostile"/> below, which is still the single question
/// every weapon, salvo, attack order, bridge and off-map strike asks; what changed is that the
/// answer now comes from the match rather than from a comparison of team numbers.
/// </para>
/// <para>
/// <b>A team the match does not declare is nobody's ally.</b> It is hostile to every team in the
/// match and to every other undeclared team, which is not a new idea: the fourth slot has always
/// been exactly that. <c>SimBridge</c> stands the subjects of the detection fixture on it precisely
/// because it is "enemies of everybody, ordered about by nobody", and the old rule agreed — a pair
/// that was not <c>0+1</c> was at war. What a match must never do is <em>require</em> such a team to
/// be destroyed, and that is a different statement, made by
/// <see cref="IsInPlay"/> where the victory check asks it.
/// </para>
/// <para>
/// <b>It is not part of the state hash.</b> Like the terrain and the navigation grid it is a pure
/// function of what the world was built from — the scenario a replay already stores — and it never
/// changes once the world exists, so hashing it would move every golden hash in the repository
/// without a single unit having moved. A roster that disagreed with the scenario it was built for
/// is refused where the scenario is laid out, which is the check that would otherwise be missing.
/// </para>
/// <para>
/// <b>A side can be declared one the victory rule does not judge, and that is a fact about a side
/// rather than a new system.</b> <see cref="MatchTeam.Judged"/> is the fourth fact a declaration
/// carries beside the team, its faction and its side, and it exists for the one kind of side that is
/// not a faction: a non-player force that holds objectives rather than ground — the scientists of
/// <c>docs/ROADMAP.md</c> §8's Operation Paperclip, a remnant that exists to be reached, a generator
/// that belongs to nobody. Such a side owns no base and therefore no structures, and the
/// last-side-standing rule would otherwise read it as already beaten; <see cref="VictorySystem"/> is
/// where that reading is refused, and <see cref="Campaign.TriggerSystem.Validate"/> is where a side
/// that stands in nothing without saying so is refused as the authoring mistake it is.
/// </para>
/// </summary>
public sealed class MatchRoster : IEquatable<MatchRoster>
{
    /// <summary>
    /// The team the local player owns.
    /// <para>
    /// The client has always had exactly one notion of "us", and it is this one: the bottom-left
    /// panels, the fog, the selection filter and the camera all read team 0. What a match declares
    /// is who the player is <em>fighting</em>, so this stays a constant while the sides move around
    /// it — which is also what the victory check needs, since "the player's side has no enemies
    /// left" is a sentence about a side and not about a team number.
    /// </para>
    /// </summary>
    public const int PlayerTeam = 0;

    private readonly Faction[] _faction;
    private readonly int[] _side;
    private readonly bool[] _inPlay;
    private readonly bool[] _judged;
    private readonly int _teamsInPlay;

    private MatchRoster(Faction[] faction, int[] side, bool[] inPlay, bool[] judged, int teamsInPlay)
    {
        _faction = faction;
        _side = side;
        _inPlay = inPlay;
        _judged = judged;
        _teamsInPlay = teamsInPlay;
    }

    /// <summary>
    /// Three factions, two sides: Σοβιετικοί (player, team 0) and Κινέζοι (ally, team 1) against
    /// Δυτικοί (team 2). This is the match every scenario shipped before a match could declare
    /// anything, described out loud.
    /// </summary>
    public static MatchRoster StandardSkirmish { get; } = Declare(
        new MatchTeam(0, Faction.Soviet, 0),
        new MatchTeam(1, Faction.Chinese, 0),
        new MatchTeam(2, Faction.Western, 1));

    /// <summary>
    /// Σοβιετικοί against Δυτικοί, and nobody else: the Κινέζοι are not in this match at all. The
    /// player has no ally, and the enemy is the team it has always been — which is the case that
    /// proves an absent faction is not a side that has to be destroyed.
    /// </summary>
    public static MatchRoster Duel { get; } = Declare(
        new MatchTeam(0, Faction.Soviet, 0),
        new MatchTeam(2, Faction.Western, 1));

    /// <summary>
    /// Σοβιετικοί against Κινέζοι, with the Δυτικοί absent: the case a fixed alliance cannot
    /// express, because teams 0 and 1 are the two that were always on the same side. Two sides of
    /// one team each, and the enemy is the team that used to be the ally.
    /// </summary>
    public static MatchRoster Rivals { get; } = Declare(
        new MatchTeam(0, Faction.Soviet, 0),
        new MatchTeam(1, Faction.Chinese, 1));

    /// <summary>Declares a match, team by team. At least one team must be declared.</summary>
    /// <exception cref="ArgumentException">
    /// A team is outside the slots the simulation has, or is declared twice.
    /// </exception>
    public static MatchRoster Declare(params MatchTeam[] teams)
    {
        ArgumentNullException.ThrowIfNull(teams);

        if (teams.Length == 0)
        {
            throw new ArgumentException("A match declares at least one team.", nameof(teams));
        }

        var faction = new Faction[SimConstants.TeamCount];
        var side = new int[SimConstants.TeamCount];
        var inPlay = new bool[SimConstants.TeamCount];
        var judged = new bool[SimConstants.TeamCount];

        // A slot nobody declared keeps its own number as its faction and as its side. The faction is
        // the convention the game shipped with — slot 3 has none — and the side is only ever read
        // for a team that is in the match, which this one is not. Judged is the default of the
        // declaration itself: a slot nobody declared is not a side the rule asks about at all,
        // because the rule only ever walks the teams the match declares.
        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            faction[team] = SlotFaction(team);
            side[team] = team;
            judged[team] = true;
        }

        foreach (MatchTeam declared in teams)
        {
            if ((uint)declared.Team >= SimConstants.TeamCount)
            {
                throw new ArgumentException(
                    $"Team {declared.Team} is not one of the {SimConstants.TeamCount} slots the simulation has.",
                    nameof(teams));
            }

            if (inPlay[declared.Team])
            {
                throw new ArgumentException($"Team {declared.Team} is declared twice.", nameof(teams));
            }

            if (declared.Side < 0)
            {
                throw new ArgumentException($"Team {declared.Team} was given side {declared.Side}.", nameof(teams));
            }

            faction[declared.Team] = declared.Faction;
            side[declared.Team] = declared.Side;
            inPlay[declared.Team] = true;
            judged[declared.Team] = declared.Judged;
        }

        return new MatchRoster(faction, side, inPlay, judged, teams.Length);
    }

    /// <summary>The roster a scenario is fought under.</summary>
    /// <param name="kind">Scenario being laid out.</param>
    /// <param name="mission">
    /// The mission being played, when <paramref name="kind"/> is
    /// <see cref="ScenarioKind.Mission"/>: a mission declares its own match, so the campaign's ally
    /// is a team in a match rather than a column of the scenario builder.
    /// </param>
    public static MatchRoster For(ScenarioKind kind, MissionDefinition? mission = null) => kind switch
    {
        ScenarioKind.Duel => Duel,
        ScenarioKind.Rivals => Rivals,
        ScenarioKind.Mission => mission?.Roster
            ?? throw new ArgumentNullException(nameof(mission), "A mission scenario needs its definition."),
        _ => StandardSkirmish,
    };

    /// <summary>
    /// The faction a team slot carries when a match has not said otherwise: the convention the game
    /// was built on, <c>0 = Σοβιετικοί, 1 = Κινέζοι, 2 = Δυτικοί</c>, and nothing for the fourth
    /// slot, which no faction owns.
    /// </summary>
    public static Faction SlotFaction(int team) => team switch
    {
        0 => Faction.Soviet,
        1 => Faction.Chinese,
        2 => Faction.Western,
        _ => Faction.None,
    };

    /// <summary>Number of teams the match declares.</summary>
    public int TeamsInPlay => _teamsInPlay;

    /// <summary>
    /// The teams the match declares, in slot order, written into <paramref name="destination"/>.
    /// <para>
    /// The interface draws one row per entry here, and the test that says the panel shows only the
    /// teams that are playing is a test of this list: a panel that walked the four slots and drew
    /// the ones it recognised would have shown three factions in a match of two, and a helper that
    /// exists only inside the drawing code could not be asked about it.
    /// </para>
    /// </summary>
    /// <returns>How many teams were written; never more than <paramref name="destination"/> holds.</returns>
    public int TeamsInPlayInto(Span<int> destination)
    {
        int written = 0;

        for (int team = 0; team < SimConstants.TeamCount && written < destination.Length; team++)
        {
            if (_inPlay[team])
            {
                destination[written++] = team;
            }
        }

        return written;
    }

    /// <summary>True when this team is playing this match.</summary>
    public bool IsInPlay(int team) => (uint)team < SimConstants.TeamCount && _inPlay[team];

    /// <summary>
    /// <b>True when the last-side-standing rule judges this team — which is what decides whether its
    /// absence of ground is a defeat.</b>
    /// <para>
    /// False for a team declared as a non-player force: a side that holds objectives rather than
    /// ground, with no base and no structures, which the objectives of its mission decide the fate of
    /// rather than the rule. <see cref="VictorySystem.Decide"/> skips such a team when it asks which
    /// sides still hold ground, because a side that never held any is a side the rule cannot tell
    /// from one that has been destroyed — and reading it as destroyed is what would make the rule
    /// call a match that has not been fought.
    /// </para>
    /// <para>
    /// An undeclared team answers true, for the same reason it answers everything else about itself:
    /// a slot nobody declared is not a side in this match, and the only walk that asks this question
    /// is one that has already skipped it.
    /// </para>
    /// </summary>
    public bool IsJudged(int team) => (uint)team >= SimConstants.TeamCount || _judged[team];

    /// <summary>
    /// Which side a team fights for. Only meaningful for a team that is in the match; an undeclared
    /// team's own number is returned so the answer is total rather than exceptional.
    /// </summary>
    public int SideOf(int team) => (uint)team < SimConstants.TeamCount ? _side[team] : team;

    /// <summary>The faction a team plays, or <see cref="Faction.None"/> for a slot with nothing on it.</summary>
    public Faction FactionOf(int team)
        => (uint)team < SimConstants.TeamCount ? _faction[team] : Faction.None;

    /// <summary>The side the player is on. What the victory check measures the enemies against.</summary>
    public int PlayerSide => SideOf(PlayerTeam);

    /// <summary>True when two teams are on the same side, which is the whole of the alliance.</summary>
    /// <remarks>
    /// A team is always its own ally. Beyond that, both teams have to be <em>in this match</em> and
    /// on the same side: an ally is somebody who is playing on your side, and a team the match does
    /// not declare is playing nothing, so it is nobody's ally — see the class comment for why that
    /// is the old rule rather than a new one.
    /// </remarks>
    public bool AreAllied(int a, int b)
    {
        if (a == b)
        {
            return true;
        }

        if ((uint)a >= SimConstants.TeamCount || (uint)b >= SimConstants.TeamCount)
        {
            return false;
        }

        return _inPlay[a] && _inPlay[b] && _side[a] == _side[b];
    }

    /// <summary>True when two teams may shoot at each other. The negation of <see cref="AreAllied"/>.</summary>
    public bool IsHostile(int a, int b) => !AreAllied(a, b);

    /// <summary>True when the two rosters declare the same match.</summary>
    public bool Equals(MatchRoster? other)
    {
        if (other is null || other._teamsInPlay != _teamsInPlay)
        {
            return false;
        }

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            if (_inPlay[team] != other._inPlay[team] ||
                _faction[team] != other._faction[team] ||
                (other._inPlay[team] && (_side[team] != other._side[team] || _judged[team] != other._judged[team])))
            {
                return false;
            }
        }

        return true;
    }

    /// <inheritdoc />
    public override bool Equals(object? obj) => obj is MatchRoster other && Equals(other);

    /// <inheritdoc />
    public override int GetHashCode()
    {
        int hash = _teamsInPlay;

        for (int team = 0; team < SimConstants.TeamCount; team++)
        {
            hash = (hash * 31) +
                ((int)_faction[team] << 9 |
                 (int)(_side[team] & 0xFF) << 2 |
                 (_inPlay[team] ? 1 : 0) << 1 |
                 (_judged[team] ? 1 : 0));
        }

        return hash;
    }
}
