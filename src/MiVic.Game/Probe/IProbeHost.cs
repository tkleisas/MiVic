using MiVic.Core.Sim;
using MiVic.Game.Data;
using MiVic.Game.Sim;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Probe;

/// <summary>
/// What a probe script can ask the client to do, and what it can ask the client for.
/// <para>
/// The runner below is the script interpreter; this is the boundary between it and the
/// game. Keeping the two apart is what stops the probe becoming a second renderer: every
/// answer that depends on how something is drawn — a part's animated transform, the
/// entity transform it hangs off, the frame a shot is taken of — is asked for through
/// here, and the client answers out of the code it already draws with.
/// </para>
/// </summary>
public interface IProbeHost
{
    /// <summary>The running simulation.</summary>
    SimBridge Simulation { get; }

    /// <summary>The model catalogue, which is what <c>model</c> and <c>parts</c> describe.</summary>
    ModelCatalog Catalog { get; }

    /// <summary>
    /// The team the client draws for. Fog is answered from this team's point of view, so a
    /// probe says what the player can see rather than what exists.
    /// </summary>
    int ViewerTeam { get; }

    /// <summary>What the last submitted frame drew, without submitting another one.</summary>
    ProbeFrameStats Stats { get; }

    /// <summary>
    /// Advances the simulation by exactly one tick, through the same path a frame uses.
    /// <para>
    /// One tick per call rather than "n ticks" so the caller can stamp the events of each
    /// tick with the tick they happened on. The clock is driven here rather than by the
    /// wall clock, which is what makes a transcript identical on a slow machine and a
    /// fast one.
    /// </para>
    /// </summary>
    void AdvanceTick();

    /// <summary>
    /// Runs the client's per-frame presentation work for one frame of fixed length:
    /// effects, smoke, rounds in flight, and the clock the liquid shaders read. It never
    /// advances the simulation.
    /// </summary>
    void SettleFrame(float elapsedSeconds);

    /// <summary>Renders the current state to the probe's render target and reports what it drew.</summary>
    ProbeFrameStats RenderFrame();

    /// <summary>Writes the most recently rendered frame to a PNG.</summary>
    void SaveFrame(string path);

    /// <summary>The camera as the renderer has it, including the lazily-solved view and projection.</summary>
    ProbeCamera ReadCamera();

    /// <summary>Sets the camera's distance and returns the distance it actually took.</summary>
    float SetZoom(float metres);

    /// <summary>Sets the camera's downward tilt and returns the tilt it actually took.</summary>
    float SetPitch(float radians);

    /// <summary>Sets the camera's rotation about the vertical and returns the value it took.</summary>
    float SetYaw(float radians);

    /// <summary>Aims the camera at a world position. A null height aims at the ground plane.</summary>
    void SetFocus(float x, float z, float? y);

    /// <summary>
    /// Everything the renderer knows about one entity's parts, animated by the renderer's
    /// own code path. False when the slot holds nothing alive.
    /// </summary>
    bool TryDescribeParts(int slot, out ProbeEntityParts parts);

    /// <summary>
    /// The client's own projection of a screen pixel onto the battlefield: the first point where
    /// the ray meets the surface the renderer draws. False when the ray never meets it at all.
    /// </summary>
    /// <param name="ray">The ray's own numbers, for a transcript that has to explain a miss.</param>
    bool TryGroundAtPixel(Vector2 pixel, out Vector3 ground, out string ray);

    /// <summary>The pixel a world point projects to, through the camera's own matrices.</summary>
    bool TryGroundToPixel(Vector3 ground, out Vector2 pixel);

    /// <summary>
    /// Height in metres of the surface the renderer draws at a ground position: the water line
    /// over water, the height field everywhere else. A cursor is over a surface rather than over
    /// a coordinate, and this is that surface's height.
    /// </summary>
    float DrawnHeightMetres(float x, float z);

    /// <summary>
    /// Puts the script's cursor at a screen pixel and reports what the client makes of it. A
    /// probe has no mouse, and the preview and the click path both need a pointer to be
    /// answerable at all.
    /// </summary>
    ProbeCursor SetCursor(Vector2 pixel);

    /// <summary>
    /// Clicks the bridge button in the support panel, through the HUD's own path, and says
    /// whether the client is now waiting for a site.
    /// </summary>
    bool ArmBridge(out string note);

    /// <summary>
    /// Presses a structure row in the production panel, through the HUD's own path, and says
    /// whether the client is now waiting for a site. A structure row arms a placement rather
    /// than ordering anything, so a script that wants to ask what placing one would do has to
    /// arm it the way a player does.
    /// </summary>
    bool ArmStructure(UnitKind kind, out string note);

    /// <summary>
    /// Releases the left button at the script's cursor, through the same path a real release
    /// takes, and reports what the client did with it.
    /// </summary>
    ProbeClick ClickAtCursor();

    /// <summary>Whether probe frames include the HUD, which they do not by default.</summary>
    bool HudDrawn { get; set; }

    /// <summary>The transient notice the HUD is showing, or an empty string.</summary>
    string HudNotice { get; }

    /// <summary>Whether a bridge is armed right now.</summary>
    bool BridgeArmed { get; }
}

/// <summary>What a scripted click resolved to and what the client did with it.</summary>
/// <param name="Resolved">False when the ray never met the ground — the cursor was in the sky.</param>
/// <param name="Ground">Where it resolved to, in metres.</param>
/// <param name="GroundMm">Where it resolved to, in simulation millimetres.</param>
/// <param name="Armed">True when a placement was waiting for a target at the moment of the click.</param>
/// <param name="Queued">True when the click enqueued a command.</param>
/// <param name="Message">What the player was told, or an empty string when nothing needed saying.</param>
public readonly record struct ProbeClick(
    bool Resolved,
    Vector3 Ground,
    MiVic.Core.Numerics.WorldPos GroundMm,
    bool Armed,
    bool Queued,
    string Message);

/// <summary>Where a scripted cursor is, and what the client resolves it to.</summary>
/// <param name="Pixel">The pixel the script put the cursor on.</param>
/// <param name="Resolved">False when the ray never met the ground.</param>
/// <param name="Ground">Where the client resolves that pixel to, in metres.</param>
/// <param name="Cell">Cell the resolved point falls in, or -1 when it is off the map.</param>
/// <param name="Armed">True when a placement is waiting for a target.</param>
/// <param name="SiteAllowed">Whether the armed placement would be accepted at this cursor.</param>
/// <param name="SiteReason">Why it would not, or an empty string.</param>
/// <param name="Footprint">Cells the armed placement would take.</param>
/// <param name="ArmedKind">
/// The structure being placed, or <see cref="UnitKind.None"/> when nothing is armed or what is
/// armed is a bridge. A placement is a placement of something, and a transcript that only said
/// "accepted" would not say what was accepted.
/// </param>
public readonly record struct ProbeCursor(
    Vector2 Pixel,
    bool Resolved,
    Vector3 Ground,
    int Cell,
    bool Armed,
    bool SiteAllowed,
    string SiteReason,
    int Footprint,
    UnitKind ArmedKind = UnitKind.None);

/// <summary>The camera's pose, as a probe reports it.</summary>
/// <param name="Target">The point it orbits, in metres.</param>
/// <param name="Position">The eye, in metres.</param>
/// <param name="Distance">Distance from the target, in metres.</param>
/// <param name="Pitch">Downward tilt, in radians; negative looks down.</param>
/// <param name="Yaw">Rotation about the vertical, in radians.</param>
/// <param name="MinDistance">Closest the camera will come, in metres.</param>
/// <param name="MaxDistance">Farthest the camera will go, in metres.</param>
/// <param name="View">The view matrix the renderer uses.</param>
/// <param name="Projection">The projection matrix the renderer uses.</param>
/// <param name="Width">Viewport width, in pixels.</param>
/// <param name="Height">Viewport height, in pixels.</param>
public readonly record struct ProbeCamera(
    Vector3 Target,
    Vector3 Position,
    float Distance,
    float Pitch,
    float Yaw,
    float MinDistance,
    float MaxDistance,
    Matrix View,
    Matrix Projection,
    int Width,
    int Height);

/// <summary>What a rendered frame submitted, which is what <c>visible</c> is asking about.</summary>
/// <param name="DrawCalls">Instanced draw calls issued.</param>
/// <param name="InstancesSubmitted">Instances across every draw call.</param>
/// <param name="Particles">Live particles drawn as billboards.</param>
/// <param name="Rounds">Rounds in flight drawn as geometry.</param>
/// <param name="HealthBars">Health bar fills drawn.</param>
public readonly record struct ProbeFrameStats(
    int DrawCalls,
    int InstancesSubmitted,
    int Particles,
    int Rounds,
    int HealthBars);

/// <summary>
/// One part of one entity, in the three states that answer "what is this part doing":
/// where the model declares it, what the renderer's animator made of it, and where it
/// ends up in the world.
/// </summary>
/// <param name="Index">Position in the model's part list, which is what parents point at.</param>
/// <param name="Name">The part contract name, which is what animation keys on.</param>
/// <param name="ParentIndex">Index of the enclosing part, or -1 for a root.</param>
/// <param name="Declared">The part's transform as the model declares it.</param>
/// <param name="Animated">The transform after <c>AnimatePart</c>, before the parent chain.</param>
/// <param name="World">The final transform: animated chain, model alignment, and the entity's place.</param>
/// <param name="BoundsMax">Top of the part in its own space, which a limb swings about.</param>
public readonly record struct ProbePart(
    int Index,
    string Name,
    int ParentIndex,
    Matrix Declared,
    Matrix Animated,
    Matrix World,
    Vector3 BoundsMax);

/// <summary>One entity's parts, plus the transforms every part is composed from.</summary>
/// <param name="Slot">The slot it occupies.</param>
/// <param name="Faction">Which power owns it.</param>
/// <param name="Kind">Its role.</param>
/// <param name="TeamId">Which team it fights for.</param>
/// <param name="Position">Its render position in metres — exact for the tick, not interpolated.</param>
/// <param name="HeadingRadians">Its hull facing, in radians, ready for the renderer's rotation.</param>
/// <param name="EntityTransform">The hull's place in the world, exactly as the submission loop builds it.</param>
/// <param name="ModelTransform">The model's own alignment, scale and grounding.</param>
/// <param name="TurretYawRadians">
/// The turret's angle relative to its hull, from the renderer's own aiming function, or
/// null for a model with no part called <c>turret</c>.
/// </param>
/// <param name="Parts">Every part of the model, in model order.</param>
public readonly record struct ProbeEntityParts(
    int Slot,
    Faction Faction,
    UnitKind Kind,
    int TeamId,
    Vector3 Position,
    float HeadingRadians,
    Matrix EntityTransform,
    Matrix ModelTransform,
    float? TurretYawRadians,
    ProbePart[] Parts);
