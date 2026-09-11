namespace MiVic.Map;

/// <summary>
/// What the map was drawn from, counted.
/// <para>
/// The counts exist for one reason: the picture is the instrument and the numbers are the
/// reading, and a layer whose whole purpose is to make a number visible has to print the
/// number as well or nobody can tell whether the picture agreed with the world. The pair that
/// matters is <see cref="WithRoute"/> against <see cref="Frozen"/> — units the pathfinder has
/// given a route to, and units that have not moved along it. That is ROADMAP §11's "a unit with
/// a drawn path and no trail is a unit that is not moving", as a figure the transcript can be
/// checked against and the picture can be looked at.
/// </para>
/// </summary>
/// <param name="Tick">Simulation tick the picture describes.</param>
/// <param name="Seed">World seed, so a picture can be redrawn from nothing else.</param>
/// <param name="Scenario">What kind of match this is.</param>
/// <param name="TerrainCells">Surface cells painted.</param>
/// <param name="Entities">Live entities drawn.</param>
/// <param name="Structures">Of those, how many are buildings rather than units.</param>
/// <param name="WithRoute">Entities the pathfinder has handed a route to right now.</param>
/// <param name="WithTrail">Entities with a trail behind them, whatever they are doing.</param>
/// <param name="Moving">Entities whose sampled window shows them covering ground.</param>
/// <param name="Frozen">Entities holding a route and not moving along it. The headline number.</param>
/// <param name="Unknown">
/// Entities holding a route whose sample window is not yet full, so nothing can be said about
/// whether they are moving. Counted separately rather than folded into either side.
/// </param>
/// <param name="Stalled">Entities holding a move goal and no route at all.</param>
/// <param name="Idle">Entities with no goal and nothing to shoot at.</param>
/// <param name="Fighting">Entities holding a target.</param>
/// <param name="TrailSamples">Sampled positions in hand across every unit, for the trail drawn.</param>
/// <param name="CoverageDiscs">Coverage discs drawn.</param>
/// <param name="ObjectiveAreas">Objective circles drawn.</param>
/// <param name="TriggersFired">Mission triggers that have fired by this tick.</param>
/// <param name="EventMarks">Event marks drawn, inside the window.</param>
/// <param name="EventWindowTicks">How far back the event marks reach.</param>
/// <param name="FrozenSlots">
/// The slots behind <paramref name="Frozen"/>, so the reading names its subjects. A count of
/// twelve frozen units is a symptom; twelve slot numbers is twelve questions a reader can put to
/// <c>unit</c> without having to hunt the picture for a ring the size of a pinhead.
/// </param>
/// <param name="SlowestSlot">
/// The moving unit getting the least out of the ground it is on, or -1 when nothing is moving.
/// This is the pace the trail makes legible: a column crossing mud is a column whose marks are
/// far apart, and the number behind the widest gap is this one.
/// </param>
/// <param name="SlowestPermille">What that unit is getting out of its ground, in permille of the step it is allowed.</param>
/// <param name="Width">Canvas width in pixels.</param>
/// <param name="Height">Canvas height in pixels.</param>
public readonly record struct MapCensus(
    long Tick,
    ulong Seed,
    string Scenario,
    int TerrainCells,
    int Entities,
    int Structures,
    int WithRoute,
    int WithTrail,
    int Moving,
    int Frozen,
    int Unknown,
    int Stalled,
    int Idle,
    int Fighting,
    int TrailSamples,
    int CoverageDiscs,
    int ObjectiveAreas,
    int TriggersFired,
    int EventMarks,
    long EventWindowTicks,
    IReadOnlyList<int> FrozenSlots,
    int SlowestSlot,
    int SlowestPermille,
    int Width,
    int Height)
{
    /// <summary>Slots named in <see cref="FrozenSlots"/> before the list is cut short.</summary>
    public const int NamedFrozenSlots = 16;

    /// <summary>
    /// The reading the trajectory layer exists for, in one line: units with somewhere to go and
    /// no ground covered. A transcript that prints this next to the picture is what turns "the
    /// map looks odd" into a number somebody can act on.
    /// </summary>
    public string TrajectoryReading => Frozen == 0
        ? $"{WithRoute} holding a route, every one of them moving"
        : $"{Frozen} of {WithRoute} holding a route and not moving along it";

    /// <summary>The census as transcript lines, in the order a reader wants them.</summary>
    public IReadOnlyList<string> Lines()
    {
        var lines = new List<string>
        {
            $"map: tick {Tick}, seed {Seed}, {Scenario}, {Width}x{Height} px",
            $"map:   drawn      {TerrainCells} surface cells, {Entities} entities ({Structures} structures), " +
            $"{TrailSamples} sampled positions",
            $"map:   routes     {WithRoute} with a route, {Stalled} stalled with a goal and no route, " +
            $"{Idle} idle with no target, {Fighting} fighting",
            $"map:   trails     {WithTrail} with a trail, {Moving} moving over the last " +
            $"{MapTrails.DefaultFrozenSamples} samples, {Frozen} frozen, {Unknown} too new to say",
            $"map:   reading    {TrajectoryReading}",
            $"map:   layers     {CoverageDiscs} coverage discs, {ObjectiveAreas} objective areas, " +
            $"{TriggersFired} triggers fired, {EventMarks} event marks in the last {EventWindowTicks} ticks",
        };

        if (FrozenSlots.Count > 0)
        {
            lines.Add(
                $"map:   frozen     slots {string.Join(", ", FrozenSlots)}" +
                (Frozen > FrozenSlots.Count ? $" and {Frozen - FrozenSlots.Count} more" : string.Empty) +
                $" — each covered under {MapTrails.FrozenThresholdMm / 1_000} m in " +
                $"{MapTrails.DefaultFrozenSamples * MapTrails.DefaultIntervalTicks / 20} s and under a " +
                $"{MapTrails.FrozenShortfallDivisor}th of what its ground allows");
        }

        if (SlowestSlot >= 0)
        {
            lines.Add($"map:   pace       the slowest mover is slot {SlowestSlot} at {SlowestPermille}‰ of its ground's allowance");
        }

        return lines;
    }
}
