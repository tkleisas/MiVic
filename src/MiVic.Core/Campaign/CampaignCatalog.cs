namespace MiVic.Core.Campaign;

/// <summary>
/// One campaign: an ordered list of mission ids and the title the front end gives them.
/// <para>
/// The campaign used to be "the catalog, in the order it is written", which is one campaign
/// by construction and no place to hang a second. The Soviet track is the first to need the
/// ordering said out loud — its chapters are written separately and composed here — and the
/// Western and Chinese tracks are the same shape once their missions exist. A mission keeps
/// its id and its definition in <see cref="MissionCatalog"/>; what a campaign adds is which
/// missions are its own and in what order they are handed out, so progress stays per mission
/// id and a mission shared by two campaigns is won under both names.
/// </para>
/// </summary>
/// <param name="Id">Stable name, for saves and for the campaign selection the tracks will need.</param>
/// <param name="GreekTitle">Player-facing title.</param>
/// <param name="MissionIds">The missions of this campaign, in the order they unlock.</param>
public sealed record CampaignDefinition(string Id, string GreekTitle, IReadOnlyList<string> MissionIds);

/// <summary>The campaigns this build ships, and their order of play.</summary>
public static class CampaignCatalog
{
    /// <summary>
    /// The Soviet campaign: all four chapters, in the order they are played. Berlin and Korea
    /// are the eras the campaign teaches with, Επιχείρηση Συνδετήρας is the operation between
    /// them, and the modern era's ten missions close it — m1 to m4 are the missions the game
    /// already shipped, and x5 to x10 are what the campaign grew into around them.
    /// </summary>
    public static readonly CampaignDefinition Soviet = new(
        Id: "soviet",
        GreekTitle: "Η Σοβιετική Εκστρατεία",
        MissionIds:
        [
            "b1_vistula",
            "b2_oder",
            "b3_seelow",
            "b4_berlin",
            "b5_reichstag",
            "pc_paperclip",
            "k1_yalu",
            "k2_chosin",
            "k3_hill",
            "k4_offensive",
            "k5_38th",
            "m1_bridgehead",
            "m2_ridge",
            "m3_industry",
            "m4_pass",
            "x5_border",
            "x6_winter",
            "x7_rivals",
            "x8_steam",
            "x9_overture",
            "x10_fullscale",
        ]);

    /// <summary>Every campaign, in menu order.</summary>
    public static readonly CampaignDefinition[] All = [Soviet];
}
