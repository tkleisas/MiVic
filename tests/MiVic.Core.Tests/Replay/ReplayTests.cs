using MiVic.Core.Numerics;
using MiVic.Core.Replay;
using MiVic.Core.Sim;

namespace MiVic.Core.Tests.Replay;

/// <summary>
/// A replay is the strongest end-to-end determinism test the project has: it
/// rebuilds a world from a seed, re-issues only the external commands, and
/// demands the same state hash. Anything that makes the simulation depend on
/// something outside its own state fails here.
/// </summary>
public sealed class ReplayTests
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

    private static byte[] Save(ReplayFile replay)
    {
        using var stream = new MemoryStream();
        replay.Save(stream);
        return stream.ToArray();
    }

    [Fact]
    public void RecordingCapturesExternalCommandsOnly()
    {
        SimWorld world = Skirmish();
        world.StartRecording();

        world.OrderMove(FirstTankOf(world, 0), new WorldPos(-120_000, 0, -120_000), 0);

        // The AI issues its own orders during the step; they must not be logged,
        // or replaying would apply each of them twice.
        world.RunTicks(40);

        SimCommandRecord record = Assert.Single(world.RecordedCommands);
        Assert.Equal(0, record.Command.IssuerTeam);
        Assert.Equal(SimCommandKind.Move, record.Command.Kind);
        Assert.Equal(0, record.Tick);
    }

    [Fact]
    public void StoppingRecordingStopsLogging()
    {
        SimWorld world = Skirmish();
        world.StartRecording();
        world.OrderMove(FirstTankOf(world, 0), new WorldPos(-100_000, 0, -100_000), 0);
        world.StopRecording();
        world.OrderMove(FirstTankOf(world, 0), new WorldPos(-90_000, 0, -90_000), 0);

        Assert.False(world.IsRecording);
        Assert.Single(world.RecordedCommands);
    }

    [Fact]
    public void ReplayReproducesARecordedMatch()
    {
        SimWorld world = Skirmish();
        world.StartRecording();

        EntityId tank = FirstTankOf(world, 0);
        world.OrderMove(tank, new WorldPos(-120_000, 0, -120_000), 0);
        world.RunTicks(60);

        world.OrderMove(tank, new WorldPos(-200_000, 0, -150_000), 0);
        world.RunTicks(60);

        ReplayFile replay = ReplayFile.Capture(world, ScenarioKind.Skirmish);
        ReplayResult result = replay.Verify();

        Assert.Equal(2, replay.Commands.Count);
        Assert.Equal(120, replay.FinalTick);
        Assert.Equal(2, result.CommandsApplied);
        Assert.True(result.Matches, $"Replay diverged: expected {result.ExpectedHash}, got {result.ActualHash}.");
    }

    [Fact]
    public void ReplaySurvivesALongMatchWithFullAiActivity()
    {
        SimWorld world = Skirmish();
        world.StartRecording();

        // Long enough for research, production, combat and the victory check to
        // all have run many times, with the AI issuing orders every tick.
        world.RunTicks(240);

        ReplayFile replay = ReplayFile.Capture(world, ScenarioKind.Skirmish);
        ReplayResult result = replay.Verify();

        Assert.Equal(240, replay.FinalTick);
        Assert.True(result.Matches, $"Replay diverged: expected {result.ExpectedHash}, got {result.ActualHash}.");
    }

    [Fact]
    public void AiOrdersAreRederivedRatherThanReplayed()
    {
        // Nothing external happened, so the log is empty — and the replay still
        // has to match, because the AI's orders come out of the same world state.
        SimWorld world = Skirmish();
        world.RunTicks(120);

        ReplayFile replay = ReplayFile.Capture(world, ScenarioKind.Skirmish);

        Assert.Empty(replay.Commands);
        Assert.True(replay.Verify().Matches);
    }

    [Fact]
    public void ACommandMissingFromTheLogMakesTheReplayDiverge()
    {
        SimWorld world = Skirmish();
        world.StartRecording();

        world.OrderMove(FirstTankOf(world, 0), new WorldPos(-120_000, 0, -120_000), 0);
        world.RunTicks(80);

        ReplayFile complete = ReplayFile.Capture(world, ScenarioKind.Skirmish);
        Assert.True(complete.Verify().Matches);

        ReplayFile withoutCommands = ReplayFile.Create(
            complete.Seed,
            complete.Capacity,
            complete.Scenario,
            complete.FinalTick,
            complete.FinalHash,
            []);

        // The replay now runs a different match, and the hash has to say so
        // rather than quietly "succeeding".
        Assert.False(withoutCommands.Verify().Matches);
    }

    [Fact]
    public void ReplayFileRoundTripsThroughAStream()
    {
        SimWorld world = Skirmish();
        world.StartRecording();

        world.OrderMove(FirstTankOf(world, 0), new WorldPos(-120_000, 0, -120_000), 0);
        world.RunTicks(30);

        ReplayFile original = ReplayFile.Capture(world, ScenarioKind.Skirmish);

        using var stream = new MemoryStream();
        original.Save(stream);
        stream.Position = 0;

        ReplayFile loaded = ReplayFile.Load(stream);

        Assert.Equal(original.Seed, loaded.Seed);
        Assert.Equal(original.Capacity, loaded.Capacity);
        Assert.Equal(original.Scenario, loaded.Scenario);
        Assert.Equal(original.FinalTick, loaded.FinalTick);
        Assert.Equal(original.FinalHash, loaded.FinalHash);
        Assert.Equal(original.Commands.Count, loaded.Commands.Count);

        for (int i = 0; i < original.Commands.Count; i++)
        {
            Assert.Equal(original.Commands[i].Tick, loaded.Commands[i].Tick);
            Assert.Equal(original.Commands[i].Command, loaded.Commands[i].Command);
        }

        Assert.True(loaded.Verify().Matches);
    }

    [Fact]
    public void CorruptFilesAreRejectedRatherThanReplayed()
    {
        SimWorld world = Skirmish();
        world.StartRecording();
        world.RunTicks(10);

        byte[] valid = Save(ReplayFile.Capture(world, ScenarioKind.Skirmish));

        // Bad magic.
        byte[] badMagic = (byte[])valid.Clone();
        badMagic[0] ^= 0xFF;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Load(new MemoryStream(badMagic)));

        // Truncated payload.
        Assert.Throws<InvalidDataException>(() => ReplayFile.Load(new MemoryStream(valid[..^3])));

        // Wrong version: refused rather than guessed at.
        byte[] wrongVersion = (byte[])valid.Clone();
        wrongVersion[8] = 99;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Load(new MemoryStream(wrongVersion)));

        // Out-of-range capacity would silently build a different world.
        byte[] badCapacity = (byte[])valid.Clone();
        BitConverter.GetBytes(0).CopyTo(badCapacity, 20);
        Assert.Throws<InvalidDataException>(() => ReplayFile.Load(new MemoryStream(badCapacity)));

        // Unknown scenario.
        byte[] badScenario = (byte[])valid.Clone();
        badScenario[24] = 200;
        Assert.Throws<InvalidDataException>(() => ReplayFile.Load(new MemoryStream(badScenario)));

        // The untouched file still loads.
        Assert.True(ReplayFile.Load(new MemoryStream(valid)).Verify().Matches);
    }

    [Fact]
    public void ReplayOfAGalleryRebuildsTheGallery()
    {
        var world = new SimWorld(Seed, Capacity);
        Scenario.Build(world, ScenarioKind.ModelGallery);
        world.StartRecording();
        world.RunTicks(20);

        ReplayFile replay = ReplayFile.Capture(world, ScenarioKind.ModelGallery);

        Assert.Equal(ScenarioKind.ModelGallery, replay.Scenario);
        Assert.True(replay.Verify().Matches);
    }
}
