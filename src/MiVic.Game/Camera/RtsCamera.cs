using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Input;

namespace MiVic.Game.Camera;

/// <summary>
/// Free-rotating strategy camera.
/// <para>
/// This is a full 3D camera, not a locked isometric one: the player can yaw all
/// the way around the battlefield and pitch down to near-ground level. Because
/// rotation hides things behind terrain, the camera also zooms with a strategic
/// range — from close-up inspection out to a whole-map view.
/// </para>
/// </summary>
public sealed class RtsCamera
{
    private const float MinPitch = -1.45f;
    private const float MaxPitch = -0.18f;

    /// <summary>Point on the ground the camera orbits, in metres.</summary>
    public Vector3 Target { get; set; }

    /// <summary>Rotation around the vertical axis, in radians.</summary>
    public float Yaw { get; set; } = MathHelper.PiOver4;

    /// <summary>Downward tilt, in radians. Negative values look down.</summary>
    public float Pitch { get; private set; } = -0.95f;

    /// <summary>Distance from the target, in metres.</summary>
    public float Distance { get; private set; } = 180f;

    /// <summary>Closest zoom, in metres.</summary>
    public float MinDistance { get; init; } = 25f;

    /// <summary>Farthest zoom, in metres.</summary>
    public float MaxDistance { get; init; } = 700f;

    /// <summary>Field of view, in radians.</summary>
    public float FieldOfView { get; init; } = MathHelper.PiOver4;

    /// <summary>Half-extent of the playable map, in metres. The camera target is clamped to it.</summary>
    public float MapHalfExtent { get; init; } = 300f;

    /// <summary>Radians per second when rotating with keys or dragging.</summary>
    public float RotationSpeed { get; init; } = 1.8f;

    /// <summary>Pan speed in metres per second at maximum zoom.</summary>
    public float PanSpeed { get; init; } = 260f;

    /// <summary>Eye position in metres.</summary>
    public Vector3 Position
    {
        get
        {
            float horizontal = Distance * MathF.Cos(Pitch);
            return Target + new Vector3(
                horizontal * MathF.Sin(Yaw),
                -Distance * MathF.Sin(Pitch),
                horizontal * MathF.Cos(Yaw));
        }
    }

    /// <summary>Camera shake, in metres, applied on top of the position and view.</summary>
    private Vector3 _shakeOffset;

    /// <summary>Amplitude left to decay, in metres.</summary>
    private float _shakeAmplitude;

    /// <summary>How much of the shake is left, 0..1. For the HUD to read if it wants to.</summary>
    public float ShakeFraction => MaxShake > 0f ? _shakeAmplitude / MaxShake : 0f;

    /// <summary>Largest shake this camera will accept, so one nuke cannot throw it off the map.</summary>
    private const float MaxShake = 6f;

    /// <summary>
    /// Impacts the camera. The largest request wins rather than the sum, so a
    /// hundred rifles firing at once produce a rumble and not a seizure.
    /// </summary>
    public void Shake(float metres)
    {
        float wanted = Math.Clamp(metres, 0f, MaxShake);
        _shakeAmplitude = MathF.Max(_shakeAmplitude, wanted);
    }

    /// <summary>Decays the shake and picks this frame's offset.</summary>
    private void UpdateShake(float deltaSeconds)
    {
        if (_shakeAmplitude <= 0.0001f)
        {
            _shakeAmplitude = 0f;
            _shakeOffset = Vector3.Zero;
            return;
        }

        // Exponential decay: a shake that faded linearly would stop dead, and the
        // stop is more noticeable than the shake.
        _shakeAmplitude *= MathF.Pow(0.12f, deltaSeconds);

        // A deterministic wobble rather than a random one: the camera is not game
        // state, but a replay that shook differently every time would still look
        // like a different replay.
        float phase = _clock * 47f;
        _clock += deltaSeconds;

        _shakeOffset = new Vector3(
            MathF.Sin(phase * 1.7f) * _shakeAmplitude,
            MathF.Sin(phase * 2.3f) * _shakeAmplitude * 0.7f,
            MathF.Cos(phase * 1.3f) * _shakeAmplitude);
    }

    private float _clock;

    /// <summary>Fraction of the way between the closest and farthest zoom, 0..1.</summary>
    public float ZoomFraction => MathHelper.Clamp((Distance - MinDistance) / (MaxDistance - MinDistance), 0f, 1f);

    /// <summary>True when the camera is far enough out that unit icons should replace meshes.</summary>
    public bool IsStrategicZoom => ZoomFraction > 0.82f;

    public Matrix GetView() => Matrix.CreateLookAt(Position + _shakeOffset, Target + _shakeOffset, Vector3.Up);

    public Matrix GetProjection(float aspectRatio)
        => Matrix.CreatePerspectiveFieldOfView(FieldOfView, aspectRatio, 0.5f, 6000f);

    /// <summary>Applies one frame of input.</summary>
    public void Update(
        float deltaSeconds,
        KeyboardState keyboard,
        KeyboardState previousKeyboard,
        MouseState mouse,
        MouseState previousMouse,
        int scrollWheelDelta)
    {
        HandleZoom(deltaSeconds, scrollWheelDelta);
        HandleRotation(deltaSeconds, keyboard, mouse, previousMouse);
        HandlePan(deltaSeconds, keyboard, mouse, previousMouse);
        ClampTarget();
        UpdateShake(deltaSeconds);
    }

    /// <summary>
    /// Turns wheel movement into a zoom <em>target</em>, then glides towards it.
    /// <para>
    /// The wheel delta is in eighths of a notch — MonoGame reports 120 per detent —
    /// so the old code computed <c>0.88^120</c> and slammed the camera to its
    /// minimum distance on a single click. It now takes a few percent per detent
    /// and eases in over about a tenth of a second, which reads as a smooth zoom
    /// instead of a cut.
    /// </para>
    /// </summary>
    private void HandleZoom(float deltaSeconds, int scrollWheelDelta)
    {
        if (scrollWheelDelta != 0)
        {
            float notches = scrollWheelDelta / 120f;

            // 0.93 per notch: about 7 % closer each detent, so a full sweep of the
            // range takes a deliberate roll of the wheel.
            _targetDistance = MathHelper.Clamp(_targetDistance * MathF.Pow(0.93f, notches), MinDistance, MaxDistance);
            _zoomSettled = false;
        }

        float blend = Math.Clamp(deltaSeconds * 12f, 0f, 1f);
        Distance = MathHelper.Lerp(Distance, _targetDistance, blend);

        if (MathF.Abs(Distance - _targetDistance) < 0.05f)
        {
            Distance = _targetDistance;
            _zoomSettled = true;
        }

        // Ease the pitch toward the value that reads best at this zoom, but only
        // while the player is actually zooming so manual tilt is never fought.
        if (!_zoomSettled)
        {
            float idealPitch = MathHelper.Lerp(-0.42f, -1.12f, ZoomFraction);
            Pitch = MathHelper.Clamp(
                MathHelper.Lerp(Pitch, idealPitch, Math.Clamp(deltaSeconds * 6f, 0f, 1f)),
                MinPitch,
                MaxPitch);
        }
    }

    private void HandleRotation(float deltaSeconds, KeyboardState keyboard, MouseState mouse, MouseState previousMouse)
    {
        float rotation = 0f;

        if (keyboard.IsKeyDown(Keys.Q))
        {
            rotation -= 1f;
        }

        if (keyboard.IsKeyDown(Keys.E))
        {
            rotation += 1f;
        }

        // Middle-drag is the familiar RTS rotate gesture.
        if (mouse.MiddleButton == ButtonState.Pressed)
        {
            rotation += (mouse.X - previousMouse.X) * 0.005f;
        }

        if (rotation != 0f)
        {
            Yaw = MathHelper.WrapAngle(Yaw + (rotation * RotationSpeed * deltaSeconds));
        }
    }

    private void HandlePan(float deltaSeconds, KeyboardState keyboard, MouseState mouse, MouseState previousMouse)
    {
        Vector2 movement = Vector2.Zero;

        if (keyboard.IsKeyDown(Keys.W) || keyboard.IsKeyDown(Keys.Up))
        {
            movement.Y += 1f;
        }

        if (keyboard.IsKeyDown(Keys.S) || keyboard.IsKeyDown(Keys.Down))
        {
            movement.Y -= 1f;
        }

        if (keyboard.IsKeyDown(Keys.A) || keyboard.IsKeyDown(Keys.Left))
        {
            movement.X -= 1f;
        }

        if (keyboard.IsKeyDown(Keys.D) || keyboard.IsKeyDown(Keys.Right))
        {
            movement.X += 1f;
        }

        if (mouse.MiddleButton == ButtonState.Pressed && keyboard.IsKeyDown(Keys.LeftShift))
        {
            movement.X -= (mouse.X - previousMouse.X) * 0.35f;
            movement.Y += (mouse.Y - previousMouse.Y) * 0.35f;
        }

        if (movement == Vector2.Zero)
        {
            return;
        }

        if (movement.LengthSquared() > 1f)
        {
            movement.Normalize();
        }

        // Pan in the camera's ground basis so "up" is always away from the viewer.
        float speed = PanSpeed * (0.35f + ZoomFraction) * deltaSeconds;
        Vector3 forward = new(-MathF.Sin(Yaw), 0f, -MathF.Cos(Yaw));
        Vector3 right = new(MathF.Cos(Yaw), 0f, -MathF.Sin(Yaw));

        Target += ((forward * movement.Y) + (right * movement.X)) * speed;
    }

    private void ClampTarget()
    {
        Target = new Vector3(
            MathHelper.Clamp(Target.X, -MapHalfExtent, MapHalfExtent),
            0f,
            MathHelper.Clamp(Target.Z, -MapHalfExtent, MapHalfExtent));
    }

    /// <summary>Moves the target directly, e.g. when jumping to a selected unit.</summary>
    public void FocusOn(Vector3 groundPosition) => Target = new Vector3(groundPosition.X, 0f, groundPosition.Z);

    /// <summary>
    /// Aims at a point **keeping its height**.
    /// <para>
    /// <see cref="FocusOn"/> flattens the target to the ground plane, which is right
    /// for a camera that follows the battlefield and wrong for one that has to look
    /// at a particular object: the terrain rises tens of metres, so aiming at sea
    /// level puts whatever stands on the hill above the frame — or off it entirely.
    /// </para>
    /// </summary>
    public void LookAt(Vector3 position)
        => Target = new Vector3(
            MathHelper.Clamp(position.X, -MapHalfExtent, MapHalfExtent),
            position.Y,
            MathHelper.Clamp(position.Z, -MapHalfExtent, MapHalfExtent));

    /// <summary>Sets the zoom distance, clamped to the configured range.</summary>
    public void ZoomTo(float distance)
    {
        Distance = MathHelper.Clamp(distance, MinDistance, MaxDistance);
        _targetDistance = Distance;
        _zoomSettled = true;
    }

    /// <summary>Sets the downward tilt, clamped to the configured range.</summary>
    public void TiltTo(float pitch) => Pitch = MathHelper.Clamp(pitch, MinPitch, MaxPitch);

    /// <summary>Distance the camera is easing towards, set by the wheel.</summary>
    private float _targetDistance = 180f;

    private bool _zoomSettled = true;

    /// <summary>
    /// Casts a ray through a screen position and returns where it meets the
    /// ground plane, or null when the ray points at the sky. This is the basis of
    /// unit selection and of ground-targeted orders.
    /// </summary>
    public Vector3? ScreenToGround(Vector2 screenPosition, int viewportWidth, int viewportHeight)
    {
        if (!TryGetRay(screenPosition, viewportWidth, viewportHeight, out Vector3 near, out Vector3 far))
        {
            return null;
        }

        float directionY = far.Y - near.Y;
        if (MathF.Abs(directionY) < 1e-6f)
        {
            return null;
        }

        float t = -near.Y / directionY;
        if (t is < 0f or > 1f)
        {
            return null;
        }

        return near + ((far - near) * t);
    }

    /// <summary>
    /// The world-space ray through a screen pixel, from the near plane to the far
    /// plane. Callers that know the terrain can march it themselves; the plane
    /// intersection above is only correct on flat ground.
    /// </summary>
    public bool TryGetRay(Vector2 screenPosition, int viewportWidth, int viewportHeight, out Vector3 near, out Vector3 far)
    {
        near = default;
        far = default;

        if (viewportWidth <= 0 || viewportHeight <= 0)
        {
            return false;
        }

        // Screen pixels must become normalised device coordinates first. Feeding
        // pixel coordinates straight into the inverse projection (as this did)
        // produces a ray that has nothing to do with the pixel: every ground
        // order landed somewhere arbitrary, or missed the ground entirely.
        float ndcX = ((screenPosition.X / viewportWidth) * 2f) - 1f;
        float ndcY = 1f - ((screenPosition.Y / viewportHeight) * 2f);

        Matrix inverse = Matrix.Invert(GetView() * GetProjection((float)viewportWidth / viewportHeight));

        near = Unproject(new Vector3(ndcX, ndcY, 0f), inverse);
        far = Unproject(new Vector3(ndcX, ndcY, 1f), inverse);
        return true;
    }

    private static Vector3 Unproject(Vector3 source, Matrix inverseViewProjection)
    {
        Vector4 transformed = Vector4.Transform(new Vector4(source, 1f), inverseViewProjection);
        return new Vector3(transformed.X / transformed.W, transformed.Y / transformed.W, transformed.Z / transformed.W);
    }
}
