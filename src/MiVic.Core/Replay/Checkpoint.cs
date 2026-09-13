using MiVic.Core.Campaign;
using MiVic.Core.Numerics;
using MiVic.Core.Sim;

namespace MiVic.Core.Replay;

/// <summary>
/// A saved position in a match: a replay trimmed to a tick.
/// <para>
/// The simulation is deterministic and reconstructible from a seed, a scenario
/// and the external command log — that is what a replay already proves — so a
/// checkpoint is those three things plus the tick it was taken on: bytes rather
/// than megabytes, restored by replaying forward. There is no state dump here,
/// and there must never be one: a second copy of the simulation state that
/// silently disagrees with the simulation is the bug this project closes, not
/// the tool it ships.
/// </para>
/// <para>
/// Because a checkpoint <em>is</em> a replay, it verifies the same way: the
/// hash the checkpoint carries must be the hash a rebuild reaches. A checkpoint
/// whose history is incomplete — a world whose external inputs were not all in
/// the command log — is reported as a mismatch rather than restored quietly.
/// </para>
/// <para>
/// This is also the mechanism the front end's save system will use: a save is
/// a checkpoint a player makes on purpose, and the two are one thing rather
/// than two.
/// </para>
/// </summary>
public static class Checkpoint
{
    /// <summary>
    /// Captures the live world as a checkpoint. The world must have been
    /// recording from its first tick, because a checkpoint taken from an
    /// incomplete command log cannot be restored to the world it was taken
    /// from — and a checkpoint that restores something else is worse than no
    /// checkpoint at all.
    /// </summary>
    public static ReplayFile Capture(SimWorld world, ScenarioKind scenario)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (!world.IsRecording)
        {
            throw new InvalidOperationException(
                "A checkpoint can only be taken from a world that is recording its commands — " +
                "restore replays the log forward, and a world whose log is empty restores to nothing.");
        }

        return ReplayFile.Capture(world, scenario);
    }

    /// <summary>
    /// Restores a checkpoint: rebuilds the world from scratch, replays its
    /// command log forward to the tick the checkpoint was taken on, and
    /// verifies the state hash. The world returned is the world the checkpoint
    /// was taken from — the same units, the same orders, the same hash —
    /// verified, not assumed. The scenario metadata rides beside it, because
    /// the client hangs what it draws off the setup as well as off the world.
    /// </summary>
    /// <exception cref="InvalidDataException">
    /// When the rebuild does not reach the hash the checkpoint recorded: the
    /// command log does not carry everything this world's state was built from.
    /// </exception>
    public static RebuiltMatch Restore(ReplayFile checkpoint)
    {
        ArgumentNullException.ThrowIfNull(checkpoint);

        RebuiltMatch match = ReplayFile.Rebuild(checkpoint, checkpoint.FinalTick);
        VerifyHash(match.World, checkpoint.FinalHash);

        return match;
    }

    internal static void VerifyHash(SimWorld world, ulong expected)
    {
        ulong actual = StateHash.Compute(world);

        if (actual != expected)
        {
            throw new InvalidDataException(
                $"Checkpoint does not restore to itself: expected hash 0x{expected:X16}, " +
                $"rebuilt hash 0x{actual:X16}. The command log does not carry everything " +
                "this world's state was built from.");
        }
    }
}

/// <summary>
/// The checkpoints of one running world: named saves and the automatic
/// keyframes a rewind is made of.
/// <para>
/// The interval is the trade the roadmap named — store size against replay
/// cost — and it is measured in ticks, because a tick is what the clock is. A
/// keyframe captured every <see cref="KeyframeInterval"/> ticks means a rewind
/// replays at most that many ticks rather than the whole match, which is the
/// whole point of having keyframes at all.
/// </para>
/// <para>
/// The keyframes are trimmed replays, not snapshots: each one is the match's
/// own seed and command log up to its tick, so each one is self-verifying and
/// none of them can drift out of step with the simulation they describe.
/// </para>
/// </summary>
public sealed class CheckpointStore
{
    /// <summary>
    /// Ticks between automatic keyframes: 600 ticks is 30 seconds of the
    /// standard clock, and a rewind therefore replays at most half a minute of
    /// match to reach any tick it is asked for.
    /// </summary>
    public const int KeyframeInterval = 600;

    private readonly List<ReplayFile> _keyframes = [];
    private readonly Dictionary<string, ReplayFile> _named = [];
    private long _nextKeyframeTick;

    /// <summary>Every keyframe captured so far, oldest first.</summary>
    public IReadOnlyList<ReplayFile> Keyframes => _keyframes;

    /// <summary>The named saves, in capture order.</summary>
    public IReadOnlyDictionary<string, ReplayFile> Named => _named;

    /// <summary>
    /// Captures the world as a named save. Overwriting a name replaces what it
    /// stood for: a name is a handle, not a history.
    /// </summary>
    public ReplayFile Save(SimWorld world, ScenarioKind scenario, string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);

        ReplayFile checkpoint = Checkpoint.Capture(world, scenario);
        _named[name] = checkpoint;
        return checkpoint;
    }

    /// <summary>
    /// Captures the world as a keyframe when the interval says so, and does
    /// nothing when it does not. Called once per tick by whoever is stepping
    /// the world; cheap when it declines, because a keyframe is a copy of the
    /// command log and nothing else.
    /// </summary>
    public void Keyframe(SimWorld world, ScenarioKind scenario)
    {
        ArgumentNullException.ThrowIfNull(world);

        if (world.Tick < _nextKeyframeTick)
        {
            return;
        }

        _keyframes.Add(Checkpoint.Capture(world, scenario));
        _nextKeyframeTick = world.Tick + KeyframeInterval;
    }

    /// <summary>
    /// The newest keyframe taken at or before <paramref name="tick"/>, or null
    /// when there is none — in which case the restore replays the match from
    /// its beginning, which is always available in a recorded world and is
    /// what a rewind to the opening seconds costs.
    /// </summary>
    public ReplayFile? Nearest(long tick)
    {
        ReplayFile? best = null;

        foreach (ReplayFile keyframe in _keyframes)
        {
            if (keyframe.FinalTick <= tick && (best is null || keyframe.FinalTick > best.FinalTick))
            {
                best = keyframe;
            }
        }

        return best;
    }

    /// <summary>
    /// Restores the recorded world to <paramref name="targetTick"/>: from the
    /// nearest keyframe when one exists, from the beginning of the match when
    /// one does not, and with the commands the live world issued after the
    /// keyframe's tick carried along the way.
    /// <para>
    /// The restored world is verified, and the verification it got is stated
    /// rather than implied: a restore that lands exactly on a keyframe or a
    /// named save compares against the hash that checkpoint recorded; a
    /// restore to a tick between keyframes has no recorded hash to compare
    /// against, so it verifies the expensive way — the keyframe path and a
    /// rebuild from the very beginning of the match must agree on the hash, or
    /// the restore refuses. That is what makes a rewind between keyframes
    /// worth the cost it reports.
    /// </para>
    /// <para>
    /// The restored world goes on recording from tick zero — its log refills
    /// itself with the replayed commands — so a checkpoint of it, later, is as
    /// self-contained as the first one was.
    /// </para>
    /// </summary>
    public CheckpointRestore Restore(SimWorld live, ScenarioKind scenario, long targetTick)
    {
        ArgumentNullException.ThrowIfNull(live);

        if (!live.IsRecording)
        {
            throw new InvalidOperationException(
                "A rewind can only be asked of a world that is recording its commands — " +
                "the ticks between the keyframe and the target are carried from that log.");
        }

        if (targetTick >= live.Tick)
        {
            throw new ArgumentOutOfRangeException(
                nameof(targetTick), targetTick,
                $"Cannot rewind to tick {targetTick}: the live world stands on tick {live.Tick}, " +
                "and a rewind goes backwards.");
        }

        if (targetTick < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(targetTick), targetTick, "Ticks are not negative.");
        }

        // The continuation is everything the live world has recorded since the
        // base stopped: it is the difference between a keyframe and the tick
        // being restored to, and without it those ticks are unreconstructible.
        List<SimCommandRecord> continuation = [];
        int next = 0;
        ReplayFile? baseCheckpoint = Nearest(targetTick);

        if (baseCheckpoint is not null)
        {
            IReadOnlyList<SimCommandRecord> recorded = live.RecordedCommands;

            while (next < recorded.Count && recorded[next].Tick <= baseCheckpoint.FinalTick)
            {
                next++;
            }
        }

        for (int i = next; i < live.RecordedCommands.Count; i++)
        {
            SimCommandRecord record = live.RecordedCommands[i];

            if (record.Tick <= targetTick)
            {
                continuation.Add(record);
            }
        }

        long ticksReplayed;
        bool fromBeginning = false;
        RebuiltMatch match;

        if (baseCheckpoint is not null && baseCheckpoint.FinalTick == targetTick)
        {
            // Exactly on a keyframe: the hash it recorded is the answer, and
            // the restore costs only the rebuild of the keyframe itself.
            match = ReplayFile.Rebuild(baseCheckpoint, targetTick, keepRecording: true);
            Checkpoint.VerifyHash(match.World, baseCheckpoint.FinalHash);
            ticksReplayed = targetTick;
        }
        else
        {
            IReadOnlyList<SimCommandRecord> upToTarget =
                live.RecordedCommands.TakeWhile(r => r.Tick <= targetTick).ToList();

            ReplayFile baseReplay = baseCheckpoint ?? ReplayFile.Create(
                live.Seed, live.Capacity, scenario, targetTick, finalHash: 0, upToTarget, live.Mission?.Id);

            match = ReplayFile.Rebuild(baseReplay, targetTick, continuation, keepRecording: true);

            // Between keyframes there is no recorded hash, so the check is the
            // complete one: the same position rebuilt from the very beginning
            // must carry the same hash. It costs the match length in ticks,
            // which is exactly what the keyframe was there to save — and it is
            // paid once per rewind, as the transcript of it says.
            RebuiltMatch reference = ReplayFile.Rebuild(ReplayFile.Create(
                live.Seed, live.Capacity, scenario, targetTick, finalHash: 0, upToTarget, live.Mission?.Id),
                targetTick);

            Checkpoint.VerifyHash(match.World, StateHash.Compute(reference.World));
            ticksReplayed = targetTick - (baseCheckpoint?.FinalTick ?? 0);
            fromBeginning = true;
        }

        return new CheckpointRestore(
            match, targetTick, HashVerified: true, fromBeginning, ticksReplayed);
    }
}

/// <summary>What a restore did and what it cost.</summary>
/// <param name="Match">The rebuilt world with the scenario metadata beside it.</param>
/// <param name="Tick">The tick the restore reached.</param>
/// <param name="HashVerified">True when the rebuilt hash was checked and held.</param>
/// <param name="VerifiedFromBeginning">
/// True when the hash was verified against a rebuild from tick zero rather than
/// against a hash a checkpoint recorded — the expensive but complete check a
/// rewind between keyframes does.
/// </param>
/// <param name="TicksReplayed">
/// Ticks the restore simulated from the keyframe, which is the cost the
/// keyframes bought: the match's whole length when no keyframe reaches the
/// target tick.
/// </param>
public sealed record CheckpointRestore(
    RebuiltMatch Match,
    long Tick,
    bool HashVerified,
    bool VerifiedFromBeginning,
    long TicksReplayed);
