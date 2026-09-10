using MiVic.Core.Sim;
using MiVic.Game.Data;
using MiVic.Game.Probe;
using MiVic.Game.Sim;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Graphics;

namespace MiVic.Game;

/// <summary>
/// The probe: the scripted inspection and control channel behind <c>--probe</c>.
/// <para>
/// This half of the class exists to answer the probe's questions out of the code the client
/// already draws with. A part's animated transform comes from <see cref="AnimatePart"/>, the
/// transform it hangs off comes from the same composition the submission loop uses, the frame
/// a shot is taken of goes through <see cref="DrawScene"/>, and the turret's angle comes from
/// <see cref="TurretYaw"/>. Nothing here re-derives what something looks like, which is the
/// only way a report of what is on screen can be trusted when it disagrees with the screen.
/// </para>
/// </summary>
public sealed partial class MiVicGame : IProbeHost
{
    /// <summary>The script being run, or null when this client is not a probe.</summary>
    private ProbeRunner? _probe;

    /// <summary>True while a probe script is in charge of the frame.</summary>
    private bool IsProbing => _probe is not null;

    /// <summary>
    /// Loads the probe script, once the world, the models and the renderer exist: a script
    /// asks about them from its first command.
    /// </summary>
    private void LoadProbe()
    {
        if (_options.ProbeScript is not { } script)
        {
            return;
        }

        string path = Path.GetFullPath(script);
        string report = _options.ProbeOutputPath is { Length: > 0 } wanted
            ? Path.GetFullPath(wanted)
            : Path.Combine(AppContext.BaseDirectory, ProbeRunner.DefaultReportName);

        _probe = new ProbeRunner(this, ProbeScript.Load(path), report);

        // A probe is a batch tool with an exit code, and the soundtrack is thirty seconds of
        // synthesis before its first line of output. Silenced for the same reason a self-test
        // does not play it.
        if (_sfx is not null)
        {
            _sfx.IsMuted = true;
        }
    }

    /// <summary>
    /// Runs one command of the script. True when the script is exhausted, which is when the
    /// transcript is written and the process exits with a status that says whether every
    /// command succeeded.
    /// </summary>
    private bool StepProbe()
    {
        if (_probe is null || !_probe.Step())
        {
            return false;
        }

        _probe.Write();
        Environment.ExitCode = _probe.Failed ? 1 : 0;
        return true;
    }

    SimBridge IProbeHost.Simulation => _simulation!;

    ModelCatalog IProbeHost.Catalog => _catalog!;

    int IProbeHost.ViewerTeam => PlayerTeam;

    ProbeFrameStats IProbeHost.Stats => ProbeStats();

    /// <summary>
    /// One tick, through the same call the frame loop makes, so a probe world is the world a
    /// played match would have had.
    /// </summary>
    void IProbeHost.AdvanceTick() => _simulation!.Update(SimConstants.TickMicroseconds);

    /// <summary>
    /// One frame of presentation work, of a fixed length rather than a measured one: a
    /// transcript may not depend on how fast the machine is.
    /// </summary>
    void IProbeHost.SettleFrame(float elapsedSeconds)
    {
        _totalSeconds += elapsedSeconds;
        UpdateParticles(elapsedSeconds);
    }

    /// <summary>
    /// Renders the current state to the screenshot target and reports what it drew.
    /// <para>
    /// This is <see cref="Draw"/>'s scene with the same device state and without the
    /// capture-and-exit path: a script photographs the tick it is standing on rather than
    /// waiting for the loop's next frame, and it may take as many frames as it likes, at
    /// whatever camera, without the process ending after the first one.
    /// </para>
    /// </summary>
    ProbeFrameStats IProbeHost.RenderFrame()
    {
        _screenshotTarget ??= new RenderTarget2D(
            GraphicsDevice,
            GraphicsDevice.PresentationParameters.BackBufferWidth,
            GraphicsDevice.PresentationParameters.BackBufferHeight,
            mipMap: false,
            SurfaceFormat.Color,
            DepthFormat.Depth24);

        GraphicsDevice.SetRenderTarget(_screenshotTarget);
        GraphicsDevice.Clear(BackgroundColor);
        GraphicsDevice.DepthStencilState = DepthStencilState.Default;
        GraphicsDevice.RasterizerState = RasterizerState.CullCounterClockwise;
        GraphicsDevice.BlendState = BlendState.Opaque;

        DrawScene();
        DrawWorldLabels();

        GraphicsDevice.SetRenderTarget(null);

        return ProbeStats();
    }

    void IProbeHost.SaveFrame(string path) => SaveScreenshot(path);

    ProbeCamera IProbeHost.ReadCamera()
    {
        Viewport viewport = GraphicsDevice.Viewport;

        return new ProbeCamera(
            _camera!.Target,
            _camera.Position,
            _camera.Distance,
            _camera.Pitch,
            _camera.Yaw,
            _camera.MinDistance,
            _camera.MaxDistance,
            _camera.GetView(),
            _camera.GetProjection(viewport.AspectRatio),
            viewport.Width,
            viewport.Height);
    }

    float IProbeHost.SetZoom(float metres)
    {
        _camera!.ZoomTo(metres);
        return _camera.Distance;
    }

    float IProbeHost.SetPitch(float radians)
    {
        _camera!.TiltTo(radians);
        return _camera.Pitch;
    }

    float IProbeHost.SetYaw(float radians)
    {
        _camera!.Yaw = radians;
        return _camera.Yaw;
    }

    /// <summary>
    /// Aims the camera. A height is honoured when the script gives one: the fixtures that
    /// look at an aircraft frame something that is not standing on the ground, and the RTS
    /// camera flattens its target to the ground plane on every update.
    /// </summary>
    void IProbeHost.SetFocus(float x, float z, float? y)
    {
        if (y is { } height)
        {
            _camera!.LookAt(new Vector3(x, height, z));
            return;
        }

        _camera!.FocusOn(new Vector3(x, 0f, z));
    }

    /// <summary>
    /// Every part of one entity, described through the renderer's own animation.
    /// <para>
    /// The position is the tick's own and not an interpolated one, so that a transform in the
    /// transcript and a model in the PNG describe the same instant.
    /// </para>
    /// </summary>
    bool IProbeHost.TryDescribeParts(int slot, out ProbeEntityParts parts)
    {
        parts = default;

        SimWorld world = _simulation!.World;

        if ((uint)slot >= (uint)world.Capacity || !world.IsAliveSlot(slot))
        {
            return false;
        }

        ref Entity entity = ref world.GetRefBySlot(slot);
        MeshBatch batch = GetBatch(entity.Faction, entity.Kind);

        // The renderer's animation, called rather than copied: a probe that worked out the
        // turret's aim for itself would be able to disagree with the turret.
        for (int i = 0; i < batch.Parts.Length; i++)
        {
            batch.Locals[i] = AnimatePart(batch.Parts[i], ref entity, world);
        }

        Vector3 position = _simulation.GetRenderPosition(slot, interpolate: false);
        float heading = SimBridge.HeadingRadians(entity.Heading);
        Matrix transform = EntityTransform(position, heading, BuildFraction(ref entity));

        var described = new ProbePart[batch.Parts.Length];
        bool hasTurret = false;

        for (int i = 0; i < described.Length; i++)
        {
            PartBatch part = batch.Parts[i];
            hasTurret |= part.Name.Equals("turret", StringComparison.Ordinal);

            described[i] = new ProbePart(
                i,
                part.Name,
                part.ParentIndex,
                part.LocalTransform,
                batch.Locals[i],
                PartWorldTransform(batch, i, transform),
                part.BoundsMax);
        }

        parts = new ProbeEntityParts(
            slot,
            entity.Faction,
            entity.Kind,
            entity.TeamId,
            position,
            heading,
            transform,
            batch.ModelTransform,
            hasTurret ? TurretYaw(ref entity, world) : null,
            described);

        return true;
    }

    /// <summary>
    /// What the last submitted frame drew.
    /// <para>
    /// The health-bar count comes from the batch itself rather than from the self-test's
    /// tally, which is only written on a frame that had a bar to draw: a probe asking about a
    /// frame with none would otherwise be told about the frame before it.
    /// </para>
    /// </summary>
    private ProbeFrameStats ProbeStats() => new(
        _drawCalls,
        _instancesSubmitted,
        _particles?.LiveCount ?? 0,
        _projectiles?.LiveCount ?? 0,
        _healthFillBatch?.Count ?? 0);
}
