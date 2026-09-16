namespace MiVic.Core.Campaign;

/// <summary>What a cutscene is for: the situation before a mission, or what came of it.</summary>
public enum CutsceneKind : byte
{
    /// <summary>Played before a mission: the situation, and what the player is being sent to do.</summary>
    Briefing = 0,

    /// <summary>Played after a mission: what the outcome was, and what it cost.</summary>
    Debrief = 1,
}

/// <summary>
/// One framing of the camera, and the moment it is reached.
/// <para>
/// Millimetres and milliseconds, like everything else in this project's data: a cutscene is
/// authored as numbers a person can measure, not as a curve a tool has to re-evaluate. The
/// director interpolates between consecutive keys and holds the last one, so a scene with a
/// single key is a locked-off shot and one with three is a slow move through a room.
/// </para>
/// </summary>
/// <param name="Milliseconds">When this framing is reached, from the start of the scene.</param>
/// <param name="X">Camera position, in millimetres.</param>
/// <param name="Y">Camera height, in millimetres.</param>
/// <param name="Z">Camera position, in millimetres.</param>
/// <param name="TargetX">Where the camera looks, in millimetres.</param>
/// <param name="TargetY">Height of what it looks at, in millimetres.</param>
/// <param name="TargetZ">Where the camera looks, in millimetres.</param>
public readonly record struct CutsceneCameraKey(
    int Milliseconds,
    int X,
    int Y,
    int Z,
    int TargetX,
    int TargetY,
    int TargetZ);

/// <summary>
/// One figure standing in the set.
/// <para>
/// <see cref="Asset"/> names a generated model — <c>personality_elder</c>, not a person — and
/// is also the name a line's <see cref="CutsceneLine.Speaker"/> uses, so a scene's cast and its
/// script cannot disagree about who is on screen.
/// </para>
/// </summary>
/// <param name="Asset">Model asset name, without a path or an extension.</param>
/// <param name="X">Where the figure stands, in millimetres.</param>
/// <param name="Z">Where the figure stands, in millimetres.</param>
/// <param name="FacingDegrees">
/// Which way it faces, in degrees clockwise from the way the model is authored. A figure
/// authored facing the camera is left at zero.
/// </param>
public readonly record struct CutsceneFigure(
    string Asset,
    int X,
    int Z,
    int FacingDegrees);

/// <summary>
/// One line of the script: who says it, what the subtitle says, and how long it is on screen.
/// <para>
/// A monologue is a scene with one figure in it, which is why <see cref="Speaker"/> may be
/// empty — a line the room says rather than a person — but a line with a name has to name
/// somebody standing in the scene.
/// </para>
/// </summary>
/// <param name="Speaker">The figure's asset name, or empty for a line nobody is speaking.</param>
/// <param name="GreekText">The line, as the subtitle shows it.</param>
/// <param name="Milliseconds">How long the line is held once it has finished being typed.</param>
public readonly record struct CutsceneLine(
    string Speaker,
    string GreekText,
    int Milliseconds);

/// <summary>
/// A scripted scene: a set, the figures standing in it, a camera that moves between framings,
/// and the lines that are said.
/// <para>
/// <b>A cutscene is presentation, and the simulation never sees one.</b> Nothing in this
/// record can affect a tick: it has no seed, it produces no commands, and no system reads it.
/// A mission can raise a cue that the <em>client</em> answers with a scene, exactly as it
/// raises a music cue the score answers — the order is one-way, and a cutscene is on the
/// presentation side of it.
/// </para>
/// <para>
/// <b>Nobody in one is named.</b> The campaign is an alternate history whose figures are
/// recognisable archetypes, and the fiction says who they are through a greatcoat, a
/// moustache and a pipe — never through a caption. That is a rule of the fiction rather than
/// a naming preference: the repository, the asset names and the script all describe a build,
/// and no line of dialogue ever says a real person's name.
/// </para>
/// </summary>
/// <param name="Id">Stable identifier, used by the command line and by a mission's briefing.</param>
/// <param name="Set">Model asset name of the set the scene stands in.</param>
/// <param name="Music">Leitmotiv to score the scene with, from the faction's own score.</param>
/// <param name="Camera">Framings, in order, starting at zero milliseconds.</param>
/// <param name="Figures">Who is in the scene, and where they stand.</param>
/// <param name="Lines">The script, in order.</param>
/// <param name="Kind">Whether this briefs a mission or debriefs it.</param>
/// <param name="MissionId">The mission this belongs to, or null for a scene that stands alone.</param>
public sealed record CutsceneDefinition(
    string Id,
    string Set,
    string Music,
    IReadOnlyList<CutsceneCameraKey> Camera,
    IReadOnlyList<CutsceneFigure> Figures,
    IReadOnlyList<CutsceneLine> Lines,
    CutsceneKind Kind = CutsceneKind.Briefing,
    string? MissionId = null)
{
    /// <summary>
    /// Which side's score the scene is played over.
    /// <para>
    /// A scene is filmed from a side: the briefing before a Σοβιετικοί mission is scored by the
    /// Σοβιετικοί, and the track it names is a leitmotiv in <em>that</em> faction's score file.
    /// Carrying the side here rather than reading it off whatever match is loaded behind the
    /// scene is what lets a briefing play before a match exists at all.
    /// </para>
    /// </summary>
    public Sim.Faction Faction { get; init; } = Sim.Faction.Soviet;

    /// <summary>
    /// How long the script runs, in milliseconds: the lines back to back, which is what the
    /// director plays. The camera track is not counted — a move that would outlast the script
    /// is held at its last key rather than extending the scene.
    /// </summary>
    public int ScriptMilliseconds
    {
        get
        {
            int total = 0;

            foreach (CutsceneLine line in Lines)
            {
                total += line.Milliseconds;
            }

            return total;
        }
    }

    /// <summary>True when a figure with this asset name is standing in the scene.</summary>
    public bool HasFigure(string asset)
    {
        foreach (CutsceneFigure figure in Figures)
        {
            if (string.Equals(figure.Asset, asset, StringComparison.Ordinal))
            {
                return true;
            }
        }

        return false;
    }
}
