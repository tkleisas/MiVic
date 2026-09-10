using MiVic.Core.Numerics;
using MiVic.Core.Sim;
using MiVic.Game.Data;
using MiVic.Game.Probe;
using MiVic.Game.Sim;
using MiVic.Game.Ui;
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

        // The ghost of a placement is staged for the frame being composed, exactly as it is on
        // a played frame: the same update, from the same cursor, so a photographed preview is
        // the preview the player would have seen.
        UpdatePlacementPreview();

        // And the selected structure's reach, for the same reason: the ring is what tells a
        // player where a gun stops being able to shoot, and a script that could not photograph
        // it could not show that a radar had changed it.
        UpdateCoverageRing();

        DrawScene();
        DrawWorldLabels();

        // The HUD is off unless the script asked for it, and its commands are never applied
        // here: a probe observes, and a button pressed by the person watching the window would
        // otherwise be able to write a command into the middle of a transcript. ImGui is
        // composited into the shot itself, because the panels are drawn into ImGui's own draw
        // list and would otherwise be recorded and never rendered — a screenshot is a file, and
        // a probe frame never goes through the client's back buffer.
        //
        // Drawn twice on purpose: ImGui lays a window out on the frame it first appears in and
        // draws nothing in it until the next, so a single pass photographs empty panels. Played,
        // that first frame is 16 ms; photographed on demand, it is the whole picture.
        if (_hudDrawnInProbeFrames)
        {
            _hud.Draw(BuildSnapshot());
            _imgui!.Render();

            _imgui.BeginAnotherFrame();
            _hud.Draw(BuildSnapshot());
            _imgui.Render();
        }

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
            batch.Locals[i] = AnimatePart(batch.Parts[i], ref entity, world, slot);
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

    // ------------------------------------------------------- placement and clicking
    //
    // These answer the questions the bridge report could not be diagnosed without: what the
    // client makes of a pixel over water, what the button does when it is pressed, and what
    // happens when the player clicks. Every one of them goes through the client's own code —
    // the projection that resolves clicks, the HUD path that arms a placement, the click path
    // that issues it — because a probe that worked out any of it for itself could disagree with
    // the game, and a report that disagrees with the game sends someone to the wrong file.

    bool IProbeHost.TryGroundAtPixel(Vector2 pixel, out Vector3 ground, out string ray)
    {
        bool hit = TryScreenToGround(pixel, out ground);
        ray = _groundRayDebug;
        return hit;
    }

    bool IProbeHost.TryGroundToPixel(Vector3 ground, out Vector2 pixel) => TryProjectToScreen(ground, out pixel);

    float IProbeHost.DrawnHeightMetres(float x, float z)
        => DrawnHeightAtMetres(_simulation!.World, _simulation.World.TerrainTypes, x, z);

    /// <summary>
    /// Puts the script's cursor down and answers what the client makes of it, including whether
    /// an armed placement would be accepted there and how many cells the ghost would cover.
    /// </summary>
    ProbeCursor IProbeHost.SetCursor(Vector2 pixel)
    {
        _scriptedCursor = pixel;
        UpdatePlacementPreview();

        bool resolved = TryPlacementTarget(pixel, out WorldPos target);
        int cell = resolved ? _simulation!.World.TerrainTypes.IndexOfWorld(target.X, target.Z) : -1;

        // The footprint is the span the client drew from: for a crossing, the cells
        // TryPlanBridge says would become ford — a preview that reported a different span from
        // the one it drew would be a second answer to the question this command exists to ask.
        // For a structure it is the ground its own footprint covers, which is the number the
        // placement rule asks about and the one its refusal is about. That used to be reported as
        // the single cell the ghost's model stands on, which was true while a structure was judged
        // on the cell it was clicked on rather than on the ground a building of its role needs.
        int footprint = 0;

        if (resolved)
        {
            _simulation!.World.TryPlanBridge(PlayerTeam, target, _bridgePreviewCells, out footprint, out _);
        }

        bool structure = _pendingStructure != UnitKind.None;

        if (structure)
        {
            int side = (2 * UnitCatalog.FootprintRadiusCells(_pendingStructure)) + 1;
            footprint = side * side;
        }

        return new ProbeCursor(
            pixel,
            resolved,
            resolved ? GroundAt(target) : default,
            cell,
            structure || _pendingBridge,
            structure ? _structureSiteAllowed : _bridgeSiteAllowed,
            structure ? _structureSiteReason : _bridgeSiteReason,
            structure || _pendingBridge ? (resolved ? footprint : 0) : 0,
            structure ? _pendingStructure : UnitKind.None);
    }

    /// <summary>The drawn surface height at a simulation position, which is where a ghost sits.</summary>
    private Vector3 GroundAt(WorldPos target)
    {
        SimWorld world = _simulation!.World;

        return new Vector3(
            target.X / (float)WorldPos.MmPerMetre,
            DrawnHeightAtMetres(world, world.TerrainTypes, target.X / (float)WorldPos.MmPerMetre, target.Z / (float)WorldPos.MmPerMetre),
            target.Z / (float)WorldPos.MmPerMetre);
    }

    /// <summary>
    /// Presses the bridge button in the support panel, through the HUD's own command path
    /// rather than by setting the flag here.
    /// </summary>
    bool IProbeHost.ArmBridge(out string note)
    {
        bool wasArmed = _pendingBridge;

        ApplyHudCommand(new HudCommand(HudCommandKind.BuildBridge));

        if (_pendingBridge)
        {
            note = "armed — the next left click picks the site";
            return true;
        }

        note = wasArmed
            ? "still armed from before"
            : "the client did not arm a bridge: clicking the button had no effect";

        return wasArmed;
    }

    /// <summary>
    /// Presses a structure row in the production panel, through the HUD's own command path
    /// rather than by setting the flag here. The row is the one the panel draws for the role, so
    /// what a script arms is what a player would press.
    /// </summary>
    bool IProbeHost.ArmStructure(UnitKind kind, out string note)
    {
        if (!UnitCatalog.TryGet(kind, out UnitDefinition definition) || !definition.IsBuilding)
        {
            note = $"{kind} is not a structure, so no row offers it — the panel arms CommandCentre, " +
                   "PowerPlant, Factory, DesignBureau or NuclearPlant";
            return false;
        }

        ApplyHudCommand(new HudCommand(HudCommandKind.PlaceStructure, kind));

        if (_pendingStructure == kind)
        {
            note = $"armed {FactionPalette.UnitLabel(kind)} — the next left click picks the site";
            return true;
        }

        note = "the client did not arm a placement: pressing the row had no effect";
        return false;
    }

    /// <summary>
    /// Releases the left button at the script's cursor, down the same branch a real release
    /// takes in <see cref="HandleSelectionInput"/>.
    /// </summary>
    ProbeClick IProbeHost.ClickAtCursor()
    {
        Vector2 pixel = CursorPosition;
        bool resolved = TryPlacementTarget(pixel, out WorldPos target);
        bool armed = _pendingBridge || _pendingStructure != UnitKind.None || _pendingAbility != AbilityId.None;
        int commandsBefore = _simulation!.World.PendingCommandCount;
        string noticeBefore = _hud.Notice;

        if (_pendingStructure != UnitKind.None)
        {
            IssueStructureAtCursor(pixel);
        }
        else if (_pendingBridge)
        {
            IssueBridgeAtCursor(pixel);
        }
        else if (_pendingAbility != AbilityId.None)
        {
            IssueAbilityAtCursor(pixel);
        }
        else
        {
            SelectSingle(pixel, additive: false, _totalSeconds);
        }

        // What the player was told, which is the point of the click being reported at all: the
        // old path produced a refused order and not one word about it.
        string message = _hud.Notice;
        bool queued = _simulation.World.PendingCommandCount > commandsBefore;

        return new ProbeClick(
            resolved,
            resolved ? GroundAt(target) : default,
            resolved ? target : default,
            armed,
            queued,
            message == noticeBefore ? string.Empty : message);
    }

    bool IProbeHost.HudDrawn
    {
        get => _hudDrawnInProbeFrames;
        set => _hudDrawnInProbeFrames = value;
    }

    string IProbeHost.HudNotice => _hud.Notice;

    bool IProbeHost.BridgeArmed => _pendingBridge;

    /// <summary>
    /// Whether probe frames draw the HUD. Off by default: a probe reads the world, and a panel
    /// over the frame is a panel over the answer. On when the answer <em>is</em> the panel — the
    /// line of text a refused order now puts in front of the player, for instance.
    /// </summary>
    private bool _hudDrawnInProbeFrames;
}
