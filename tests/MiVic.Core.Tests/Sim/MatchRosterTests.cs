using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Sim;

/// <summary>
/// A match declares its teams: which ones are playing, which faction each one plays and which side
/// each one is on. Every rule in this file is a sentence that used to be an assumption about the
/// numbers — a faction was a team's index, an alliance was <c>0 and 1 against the rest</c>, and a
/// team nobody was playing was still a team.
/// </summary>
public sealed class MatchRosterTests
{
    [Fact]
    public void TheStandardSkirmishDeclaresThreeTeamsOnTwoSides()
    {
        MatchRoster roster = MatchRoster.StandardSkirmish;

        Assert.Equal(3, roster.TeamsInPlay);
        Assert.True(roster.IsInPlay(0) && roster.IsInPlay(1) && roster.IsInPlay(2));

        // The fourth slot carries no faction and is not in the match — it was never a side, and the
        // roster says so rather than leaving it to whoever happens to be counting.
        Assert.False(roster.IsInPlay(3));
        Assert.Equal(Faction.None, roster.FactionOf(3));

        // The faction is the assignment, and for the match this game shipped with the assignment is
        // the index. That is what makes the three-faction match byte-for-byte what it always was.
        Assert.Equal(Faction.Soviet, roster.FactionOf(0));
        Assert.Equal(Faction.Chinese, roster.FactionOf(1));
        Assert.Equal(Faction.Western, roster.FactionOf(2));

        Assert.Equal(roster.SideOf(0), roster.SideOf(1));
        Assert.NotEqual(roster.SideOf(0), roster.SideOf(2));
        Assert.Equal(0, roster.PlayerSide);
    }

    [Fact]
    public void TheTwoFactionMatchesDeclareTwoTeamsAndLeaveTheOthersOut()
    {
        // Σοβιετικοί against Δυτικοί: the player has no ally in the match at all.
        Assert.Equal(2, MatchRoster.Duel.TeamsInPlay);
        Assert.True(MatchRoster.Duel.IsInPlay(0) && MatchRoster.Duel.IsInPlay(2));
        Assert.False(MatchRoster.Duel.IsInPlay(1));
        Assert.True(MatchRoster.Duel.IsHostile(0, 2));
        Assert.False(MatchRoster.Duel.AreAllied(0, 1));

        // Σοβιετικοί against Κινέζοι: the enemy is the team that was always the ally, and the
        // Δυτικοί are nowhere on the map.
        Assert.Equal(2, MatchRoster.Rivals.TeamsInPlay);
        Assert.True(MatchRoster.Rivals.IsInPlay(0) && MatchRoster.Rivals.IsInPlay(1));
        Assert.False(MatchRoster.Rivals.IsInPlay(2));
        Assert.True(MatchRoster.Rivals.IsHostile(0, 1));
        Assert.False(MatchRoster.Rivals.AreAllied(0, 1));
        Assert.False(MatchRoster.Rivals.AreAllied(1, 2), "The Δυτικοί are not in this match either.");
    }

    [Fact]
    public void ATeamTheMatchDoesNotDeclareIsInNobodysAlliance()
    {
        // The fourth slot: not in the match, so nobody's ally — which is exactly what it has always
        // been, and why the detection fixture can stand its measurement subjects on it and have
        // every gun in the game treat them as enemies.
        SimWorld world = new(seed: 1, capacity: 4);

        Assert.False(world.AreAllied(0, 3));
        Assert.True(world.IsHostile(0, 3));
        Assert.True(world.IsHostile(3, 0));
        Assert.True(world.IsHostile(1, 3));
        Assert.True(world.IsHostile(2, 3));

        // And a team is always its own ally, in the match or out of it: the one clause the old
        // comparison of team ids got right.
        Assert.True(world.AreAllied(3, 3));

        // Being in nobody's alliance is not the same as being a side the match is fought against.
        // That is the question IsInPlay answers, and the victory check is where it matters: a team
        // the match does not declare is never something the check requires destroyed.
        Assert.False(world.IsTeamInPlay(3));
    }

    [Fact]
    public void AFactionIsWhatATeamPlaysRatherThanItsNumber()
    {
        // A team playing the Δυτικοί, on a slot whose own number says Κινέζοι. The Μισθοφόρος is
        // the proof: it is Δυτικοί-only and needs no research, so a team's answer to "may I build
        // one" is its faction and nothing else.
        MatchRoster swapped = MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(1, Faction.Western, 1));

        SimWorld world = new(seed: 1, capacity: 8, swapped);
        SimWorld standard = new(seed: 1, capacity: 8);

        Assert.Equal(Faction.Western, world.FactionOfTeam(1));
        Assert.Equal(Faction.Chinese, standard.FactionOfTeam(1));

        Assert.True(world.CanBuild(1, UnitKind.Mercenary), "Team 1 plays the Δυτικοί, whose infantry this is.");
        Assert.False(standard.CanBuild(1, UnitKind.Mercenary), "Team 1 plays the Κινέζοι, whose infantry this is not.");

        // An entity still says what it is as well as who owns it, which is the pair of fields that
        // made this possible in the first place: the assignment is a fact about the team.
        EntityId mercenary = world.Spawn(
            Faction.Western,
            1,
            UnitKind.Mercenary,
            WorldPos.GroundMetres(0, 0),
            default,
            100);

        Assert.Equal(Faction.Western, world.GetRefBySlot(mercenary.Slot).Faction);
        Assert.Equal(1, world.GetRefBySlot(mercenary.Slot).TeamId);
    }

    /// <summary>
    /// <b>A match also says which of its sides the victory rule judges, which is the one fact a side
    /// can carry that is not a faction.</b>
    /// <para>
    /// True for everything that plays for the map, and false for a non-player force — a side that
    /// holds objectives rather than ground, with no base and no structures: the scientists of §8's
    /// Operation Paperclip, which are an objective and not an army. It is not an exemption from being
    /// fought, which is why the alliance is asserted beside it: such a side is still hostile to
    /// everyone it is not allied to, still on the map and still shootable. What it is exempt from is
    /// being <em>won against</em>, and <see cref="VictorySystem.Decide"/> is where that is read.
    /// </para>
    /// </summary>
    [Fact]
    public void AMatchDeclaresWhichOfItsSidesTheVictoryRuleJudges()
    {
        // Every match this game shipped before the fact existed judges every team in it, which is
        // what makes the default the old behaviour rather than a new one.
        foreach (int team in new[] { 0, 1, 2 })
        {
            Assert.True(MatchRoster.StandardSkirmish.IsJudged(team));
            Assert.True(MatchRoster.Duel.IsJudged(team));
        }

        MatchRoster judged = MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(2, Faction.Western, 1));

        MatchRoster unjudged = MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(2, Faction.Western, 1, Judged: false));

        Assert.True(judged.IsJudged(2));
        Assert.False(unjudged.IsJudged(2));

        // The teams that play for the map are judged in both, and a slot nobody declared answers the
        // default: the rule only ever walks the teams the match declares.
        Assert.True(unjudged.IsJudged(0));
        Assert.True(unjudged.IsJudged(1));

        // The word is part of the match and not a remark about it: two rosters that differ only in it
        // are two different matches, which is what makes a world built for one refuse the other.
        Assert.False(judged.Equals(unjudged));
        Assert.NotEqual(judged, unjudged);

        // And nothing else about the declaration moved: the two declared teams are still at war, and
        // a team the match does not declare is still nobody's ally.
        Assert.True(unjudged.IsHostile(0, 2));
        Assert.False(unjudged.AreAllied(0, 2));
        Assert.False(unjudged.AreAllied(0, 1));
    }

    [Fact]
    public void AMatchRefusesATeamItDoesNotHaveOrOneDeclaredTwice()
    {
        Assert.Throws<ArgumentException>(() => MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(SimConstants.TeamCount, Faction.Western, 1)));

        Assert.Throws<ArgumentException>(() => MatchRoster.Declare(
            new MatchTeam(0, Faction.Soviet, 0),
            new MatchTeam(0, Faction.Chinese, 1)));

        Assert.Throws<ArgumentException>(() => MatchRoster.Declare());
    }

    [Fact]
    public void TheInterfaceHasOnlyTheTeamsThatArePlayingToDraw()
    {
        // What the status panel iterates: one entry per declared team, in slot order. A panel that
        // walked the four slots and drew the ones it recognised is the panel that showed three
        // factions in a match of two.
        Span<int> teams = stackalloc int[SimConstants.TeamCount];

        int skirmishRows = MatchRoster.StandardSkirmish.TeamsInPlayInto(teams);

        Assert.Equal(3, skirmishRows);
        Assert.Equal(new[] { 0, 1, 2 }, teams[..skirmishRows].ToArray());

        int duelRows = MatchRoster.Duel.TeamsInPlayInto(teams);

        Assert.Equal(2, duelRows);
        Assert.Equal(new[] { 0, 2 }, teams[..duelRows].ToArray());

        int rivalRows = MatchRoster.Rivals.TeamsInPlayInto(teams);

        Assert.Equal(2, rivalRows);
        Assert.Equal(new[] { 0, 1 }, teams[..rivalRows].ToArray());

        // Every row the panel draws has a faction to label it with and a side to colour it by, and
        // neither is read from the row's number.
        for (int row = 0; row < rivalRows; row++)
        {
            Assert.NotEqual(Faction.None, MatchRoster.Rivals.FactionOf(teams[row]));
        }

        // The fourth slot is not drawn, in any of them.
        foreach (int row in teams[..skirmishRows].ToArray())
        {
            Assert.NotEqual(3, row);
        }
    }

    [Fact]
    public void AScenarioRefusesAWorldThatWasBuiltForAnotherMatch()
    {
        // The roster is not part of the state hash — see MatchRoster — so a world whose sides
        // disagree with the layout it is being given would be a desync nothing could see. The
        // layout refuses instead.
        SimWorld world = new(seed: 20250101, capacity: 1024);

        Assert.Throws<InvalidOperationException>(() => Scenario.Build(world, ScenarioKind.Duel));
        Assert.Throws<InvalidOperationException>(() => Scenario.Build(world, ScenarioKind.Rivals));

        // And the layout the world was built for is laid out, so the refusal above is a check
        // rather than a wall.
        ScenarioSetup setup = Scenario.Build(world, ScenarioKind.Skirmish);

        Assert.Equal(3, setup.CommandCentres.Count);
    }

    [Fact]
    public void TheVictoryRuleMeasuresTheEnemiesOfThePlayersSide()
    {
        SimWorld duel = Scenario.NewWorld(ScenarioKind.Duel, 20250101, 64);

        Assert.Same(MatchRoster.Duel, duel.Roster);
        Assert.Equal(2, duel.Roster.TeamsInPlay);

        // The side the player is on is asked of the match, which is what the victory rule measures
        // the enemies against. A match where the player is the only team on its side is a match
        // with no ally, and the same two lines answer both.
        Assert.Equal(duel.Roster.SideOf(0), duel.Roster.PlayerSide);
        Assert.True(duel.IsHostile(0, 2));
        Assert.False(duel.AreAllied(0, 1), "The Κινέζοι are not in this match, so they are not the player's ally.");
    }
}
