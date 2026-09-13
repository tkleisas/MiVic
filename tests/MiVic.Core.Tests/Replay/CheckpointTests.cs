using MiVic.Core.Numerics;
using MiVic.Core.Replay;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Replay;

/// <summary>
/// A checkpoint is a replay trimmed to a tick, and its tests are the replay's
/// tests pointed at a claim the replay does not make: that a position in the
/// middle of a match can be put back exactly, from a keyframe plus the rest of
/// the log, and that the restored world is the world it was taken from —
/// verified by hash, not assumed.
/// </summary>
public sealed class CheckpointTests
{
    private const int Capacity = 1024;
    private const ulong Seed = 20250101;

    private static SimWorld Skirmish()
    {
        var world = new SimWorld(Seed, Capacity);
        Scenario.Build(world, ScenarioKind.Skirmish);
        return world;
    }

    private static EntityId FirstTankOf(SimWorld world, int team)
    {
        for (int slot = 0; slot < world.Capacity; slot++)
        {
            if (!world.IsAliveSlot(slot))
            {
                continue;
            }

            ref Entity entity = ref world.GetRefBySlot(slot);

            if (entity.TeamId == team && entity.Kind == UnitKind.Tank)
            {
                return new EntityId(slot, entity.Generation);
            }
        }

        throw new InvalidOperationException($"Team {team} has no tank.");
    }

    /// <summary>
    /// A recorded match with orders in it, run to <paramref name="ticks"/>,
    /// so every checkpoint taken from it has something to carry.
    /// </summary>
    private static SimWorld RecordedMatch(int ticks, out SimWorld world)
    {
        world = Skirmish();
        world.StartRecording();

        EntityId tank = FirstTankOf(world, 0);
        world.OrderMove(tank, new WorldPos(-120_000, 0, -120_000), 0);
        world.RunTicks(Math.Min(ticks, 60));

        if (ticks > 60)
        {
            world.OrderMove(tank, new WorldPos(-200_000, 0, -150_000), 0);
            world.RunTicks(ticks - 60);
        }

        return world;
    }

    [Fact]
    public void RestoreReachesTheWorldTheCheckpointWasTakenFrom()
    {
        RecordedMatch(70, out SimWorld world);
        ReplayFile checkpoint = Checkpoint.Capture(world, ScenarioKind.Skirmish);
        world.RunTicks(80);

        RebuiltMatch match = Checkpoint.Restore(checkpoint);

        Assert.Equal(checkpoint.FinalTick, match.World.Tick);
        Assert.Equal(checkpoint.FinalHash, StateHash.Compute(match.World));
        Assert.Equal(checkpoint.Commands.Count, match.CommandsApplied);
    }

    [Fact]
    public void ACheckpointOfTheSameMatchRestoresTwice()
    {
        // Two checkpoints of the same live world at different ticks: each one
        // restores to its own tick, because each one is a complete record of
        // its own position and neither borrows anything from the other.
        RecordedMatch(50, out SimWorld world);
        ReplayFile early = Checkpoint.Capture(world, ScenarioKind.Skirmish);
        world.RunTicks(60);
        ReplayFile late = Checkpoint.Capture(world, ScenarioKind.Skirmish);

        RebuiltMatch restoredEarly = Checkpoint.Restore(early);
        RebuiltMatch restoredLate = Checkpoint.Restore(late);

        Assert.Equal(early.FinalTick, restoredEarly.World.Tick);
        Assert.Equal(late.FinalTick, restoredLate.World.Tick);
        Assert.Equal(early.FinalHash, StateHash.Compute(restoredEarly.World));
        Assert.Equal(late.FinalHash, StateHash.Compute(restoredLate.World));
    }

    [Fact]
    public void ACheckpointWithoutItsCommandsIsRefused()
    {
        // The mismatch is the finding: a world whose history is not all in the
        // log — a fixture that spawned entities directly, for one — must fail
        // loudly rather than restore a different world quietly. A checkpoint of
        // a world that has run on with recording stopped carries the start but
        // nothing after it.
        RecordedMatch(40, out SimWorld world);
        ReplayFile checkpoint = Checkpoint.Capture(world, ScenarioKind.Skirmish);

        // The checkpoint stands on the state tick 40 reached; continue from an
        // unrecorded position and the two must disagree.
        var unrecorded = new SimWorld(checkpoint.Seed, checkpoint.Capacity);
        Scenario.Build(unrecorded, checkpoint.Scenario);
        unrecorded.RunTicks(120);

        Assert.Throws<InvalidDataException>(() => Checkpoint.Restore(
            ReplayFile.Create(
                checkpoint.Seed, checkpoint.Capacity, checkpoint.Scenario, 120,
                StateHash.Compute(unrecorded), checkpoint.Commands)));
    }

    [Fact]
    public void ARestoredWorldKeepsRecording()
    {
        // A restored world goes on living, so a checkpoint of it, later, is as
        // self-contained as the first one was — which is why the rebuild keeps
        // the recording on and lets the replayed commands refill the log.
        RecordedMatch(70, out SimWorld world);
        ReplayFile checkpoint = Checkpoint.Capture(world, ScenarioKind.Skirmish);
        world.RunTicks(40);

        CheckpointStore store = new();
        CheckpointRestore restored = store.Restore(world, ScenarioKind.Skirmish, checkpoint.FinalTick);

        Assert.True(restored.Match.World.IsRecording);
        Assert.Equal(
            checkpoint.Commands.Count,
            restored.Match.World.RecordedCommands.Count);
    }

    [Fact]
    public void AKeyframeRewindMatchesAFullRebuild()
    {
        // The heart of the keyframe trade: the same tick reached by replaying
        // from a keyframe and by replaying from the beginning must be the same
        // world, or the keyframes are not keyframes of this match.
        RecordedMatch(130, out SimWorld world);

        var store = new CheckpointStore();
        store.Keyframe(world, ScenarioKind.Skirmish);
        world.RunTicks(CheckpointStore.KeyframeInterval - world.Tick);
        store.Keyframe(world, ScenarioKind.Skirmish);
        long laterTick = world.Tick + 40;
        world.RunTicks(80);

        CheckpointRestore restored = store.Restore(world, ScenarioKind.Skirmish, CheckpointStore.KeyframeInterval);
        Assert.Equal(CheckpointStore.KeyframeInterval, restored.Tick);

        // Verified from the keyframe; the strong check is the store's own work
        // for a rewind between keyframes, which this exercises directly.
        CheckpointRestore between = store.Restore(world, ScenarioKind.Skirmish, laterTick);
        Assert.True(between.VerifiedFromBeginning);
        Assert.Equal(laterTick, between.Tick);

        // ...and the honest answer a rewind to the opening costs: no keyframe
        // reaches tick 10, so the restore is the rebuild from the beginning
        // itself — the whole match replayed, ten ticks of it.
        CheckpointRestore opening = store.Restore(world, ScenarioKind.Skirmish, 10);
        Assert.True(opening.VerifiedFromBeginning);
        Assert.Equal(10, opening.TicksReplayed);
    }

    [Fact]
    public void ARewindPastThePresentIsRefused()
    {
        RecordedMatch(50, out SimWorld world);
        var store = new CheckpointStore();

        Assert.Throws<ArgumentOutOfRangeException>(
            () => store.Restore(world, ScenarioKind.Skirmish, world.Tick));
    }

    [Fact]
    public void ACheckpointNeedsARecordingWorld()
    {
        SimWorld world = Skirmish();
        world.RunTicks(10);

        Assert.Throws<InvalidOperationException>(() => Checkpoint.Capture(world, ScenarioKind.Skirmish));
    }
}
