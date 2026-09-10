using MiVic.Game.Rendering.Particles;
using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering.Combat;

/// <summary>
/// Draws the shots the simulation has already resolved.
/// <para>
/// A projectile here is a picture of a shot, not a shot: the damage landed on the
/// tick the weapon fired, exactly as it did before this existed. That is a
/// deliberate choice rather than a shortcut — giving rounds a real flight time would
/// put them in the simulation, where they would change the outcome of a tick and
/// therefore every recorded hash in the project.
/// </para>
/// <para>
/// What it buys is the thing that was missing: a battle you can read. A rifle crack
/// and a howitzer are otherwise the same event, an integer subtracted from a health
/// pool somewhere off screen.
/// </para>
/// </summary>
public sealed class ProjectileSystem
{
    /// <summary>Hard cap on rounds in flight, and on scheduled secondary bursts.</summary>
    public const int Capacity = 768;

    private const int CookOffCapacity = 256;

    /// <summary>
    /// How long an electric arc stays drawn, in seconds.
    /// <para>
    /// It is not travelling — it appears along its whole length at once — so this is
    /// how long it *persists*. A single frame is what lightning physically does and is
    /// also invisible: at sixty frames a second a one-frame arc is on screen for
    /// sixteen milliseconds, which the eye reads as a flicker on the monitor rather
    /// than as a weapon firing. A tenth of a second is long enough to see the shape of
    /// it and short enough to still read as instantaneous.
    /// </para>
    /// </summary>
    private const float BoltVisibleSeconds = 0.10f;

    /// <summary>One round in flight, or one bolt being drawn.</summary>
    private struct Round
    {
        public Vector3 Position;
        public Vector3 Previous;
        public Vector3 Origin;
        public Vector3 Destination;
        public Vector3 Color;
        public Vector3 Lateral;
        public float Age;
        public float FlightTime;
        public float TrailTimer;
        public float Speed;
        public float Length;
        public float Width;
        public float Arc;
        public float Trail;
        public float Shake;
        public float TrailCarry;
        public FireStyle Style;
        public FireImpact Impact;
        public float ImpactScale;
    }

    /// <summary>A secondary burst scheduled for a vehicle or a structure burning out.</summary>
    private struct CookOff
    {
        public Vector3 Position;
        public float Delay;
        public float Scale;
        public Vector3 Tint;
    }

    private readonly Round[] _rounds = new Round[Capacity];
    private readonly InstanceData[] _alphaInstances = new InstanceData[Capacity];
    private readonly InstanceData[] _additiveInstances = new InstanceData[Capacity];
    private readonly CookOff[] _cookOffs = new CookOff[CookOffCapacity];
    private readonly ParticleSystem _particles;
    private readonly Random _random = new(20250101);

    private int _next;
    private int _live;

    public ProjectileSystem(ParticleSystem particles)
        => _particles = particles ?? throw new ArgumentNullException(nameof(particles));

    /// <summary>Rounds drawn as solid geometry, i.e. shells and rockets.</summary>
    public InstanceData[] AlphaInstances => _alphaInstances;

    /// <summary>Rounds drawn additively, i.e. tracers and bolts.</summary>
    public InstanceData[] AdditiveInstances => _additiveInstances;

    /// <summary>How many solid rounds to draw this frame.</summary>
    public int AlphaCount { get; private set; }

    /// <summary>How many additive rounds to draw this frame.</summary>
    public int AdditiveCount { get; private set; }

    /// <summary>Rounds in flight.</summary>
    public int LiveCount => _live;

    /// <summary>Rounds drawn since construction, for diagnostics.</summary>
    public int TotalFired { get; private set; }

    /// <summary>
    /// Screen shake requested this frame, in metres of camera offset. The largest
    /// request wins rather than the sum: ten rifles firing together should not
    /// throw the camera across the map.
    /// </summary>
    public float PendingShake { get; private set; }

    /// <summary>Drops every round in flight, e.g. when loading a different world.</summary>
    public void Clear()
    {
        Array.Clear(_rounds);
        Array.Clear(_cookOffs);
        _next = 0;
        _live = 0;
        AlphaCount = 0;
        AdditiveCount = 0;
        PendingShake = 0f;
    }

    /// <summary>
    /// Draws one shot: a muzzle flash, and the round or rounds leaving the barrel.
    /// </summary>
    /// <param name="profile">How this weapon fires.</param>
    /// <param name="origin">Muzzle position, in metres.</param>
    /// <param name="destination">Where the shot is going.</param>
    /// <param name="scale">Size of the shooter, which scales the effects.</param>
    public void Fire(in FireProfile profile, Vector3 origin, Vector3 destination, float scale)
    {
        TotalFired++;

        Vector3 delta = destination - origin;
        float range = delta.Length();

        if (range < 0.05f)
        {
            return;
        }

        Vector3 direction = delta / range;

        // A bolt is drawn between two points rather than thrown between them: it
        // covers the distance in one frame, which is what lightning does.
        if (profile.Style == FireStyle.Bolt)
        {
            Spawn(profile, origin, destination, direction, range, Vector3.Zero, BoltVisibleSeconds, scale);
            _particles.SpawnMuzzleFlash(origin, profile.Color, profile.Muzzle * scale);
            _particles.SpawnElectricBurst(destination, scale);
            PendingShake = MathF.Max(PendingShake, profile.Shake);
            return;
        }

        float speed = profile.Speed;
        float flight = MathF.Max(range / MathF.Max(speed, 1f), 1f / 60f);

        // A twin mount throws its rounds from two points on the mount, a salvo from
        // a rack: the spread is what makes four rockets read as four and not one.
        for (int i = 0; i < profile.Shots; i++)
        {
            Vector3 lateral = Vector3.Zero;

            if (profile.Shots > 1)
            {
                float t = profile.Shots == 1 ? 0f : ((i / (float)(profile.Shots - 1)) - 0.5f) * 2f;

                // Spread across the barrel line first, then alternate high and low,
                // which is how a salvo leaves a rack of tubes in two rows.
                Vector3 side = Vector3.Normalize(Vector3.Cross(Vector3.Up, direction) + new Vector3(0.0001f, 0f, 0f));
                lateral = (side * t * profile.Spread * scale)
                    + (Vector3.Up * (((i % 2) == 0 ? -1f : 1f) * profile.Spread * 0.35f * scale));
            }

            Spawn(profile, origin + lateral, destination, direction, range, lateral, flight, scale);
        }

        if (profile.Muzzle > 0f)
        {
            _particles.SpawnMuzzleFlash(origin, profile.Color, profile.Muzzle * scale);
        }

        PendingShake = MathF.Max(PendingShake, profile.Shake);
    }

    private void Spawn(
        in FireProfile profile,
        Vector3 origin,
        Vector3 destination,
        Vector3 direction,
        float range,
        Vector3 lateral,
        float flight,
        float scale)
    {
        int index = _next;

        if (_live >= Capacity)
        {
            // Overwrite the oldest rather than refuse to draw: a shot that does not
            // appear is a bug the player sees, and a missing round in a barrage is
            // not.
            index = FindOldest();
        }
        else
        {
            _next = (_next + 1) % Capacity;
            _live++;
        }

        // A bolt is not a round travelling: it is an arc that exists along its whole
        // length at once. Its profile has no length of its own for that reason, and it
        // is drawn across the entire path from the muzzle to the target rather than at
        // a point on it. Scaling a zero-length box by its length is a degenerate
        // sliver, which is exactly what an electric weapon used to look like.
        float length = profile.Length * MathF.Max(scale, 0.6f);
        Vector3 position = origin;

        if (profile.Style == FireStyle.Bolt)
        {
            length = range;
            position = (origin + destination) * 0.5f;
        }

        _rounds[index] = new Round
        {
            Position = position,
            Previous = origin,
            Origin = origin,
            Destination = destination,
            Color = profile.Color,
            Lateral = lateral,
            Age = 0f,
            FlightTime = flight,
            TrailTimer = 0f,
            Speed = profile.Speed,
            Length = length,
            Width = profile.Width * MathF.Max(scale, 0.6f),
            Arc = profile.Arc,
            Trail = profile.Trail,
            Shake = profile.Shake,
            TrailCarry = 0f,
            Style = profile.Style,
            Impact = profile.Impact,
            ImpactScale = MathF.Max(scale, 0.7f) * ImpactScaleFor(profile.Impact),
        };
    }

    private static float ImpactScaleFor(FireImpact impact) => impact switch
    {
        FireImpact.Sparks => 0.45f,
        FireImpact.SmallBurst => 0.9f,
        FireImpact.ShellBurst => 1.5f,
        FireImpact.GroundBurst => 2.6f,
        FireImpact.Airburst => 1.3f,
        FireImpact.HeBurst => 2.2f,
        FireImpact.Electric => 1.2f,
        _ => 1f,
    };

    private int FindOldest()
    {
        int oldest = 0;
        float best = float.MinValue;

        for (int i = 0; i < Capacity; i++)
        {
            if (_rounds[i].Age > best)
            {
                best = _rounds[i].Age;
                oldest = i;
            }
        }

        return oldest;
    }

    /// <summary>
    /// Schedules the secondary bursts a wreck throws off. A tank that has just been
    /// destroyed is not finished exploding: something inside it is still burning,
    /// and a couple of delayed bangs is the cheapest way to say so.
    /// </summary>
    public void ScheduleCookOff(Vector3 position, float scale, int count, Vector3 tint)
    {
        for (int i = 0; i < count; i++)
        {
            for (int slot = 0; slot < CookOffCapacity; slot++)
            {
                if (_cookOffs[slot].Scale > 0f)
                {
                    continue;
                }

                _cookOffs[slot] = new CookOff
                {
                    Position = position + new Vector3(
                        ((float)_random.NextDouble() - 0.5f) * scale * 0.6f,
                        0.3f + ((float)_random.NextDouble() * scale * 0.3f),
                        ((float)_random.NextDouble() - 0.5f) * scale * 0.6f),
                    Delay = 0.25f + ((float)_random.NextDouble() * 1.4f) + (i * 0.12f),
                    Scale = scale * (0.28f + ((float)_random.NextDouble() * 0.30f)),
                    Tint = tint,
                };

                break;
            }
        }
    }

    /// <summary>A single shell landing, with no round in flight: used by the demo fixture.</summary>
    public void SpawnImpact(Vector3 position, FireImpact impact, float scale, Vector3 color)
        => ApplyImpact(position, impact, scale, color);

    /// <summary>
    /// Advances every round, emits its trail, and lands whatever arrived this frame.
    /// <paramref name="sizeScale"/> grows the drawn rounds with the camera distance,
    /// because a tracer is one pixel wide at strategic zoom and reads as nothing.
    /// </summary>
    public void Update(float elapsedSeconds, Vector3 cameraRight, Vector3 cameraUp, float sizeScale = 1f)
    {
        float dt = Math.Clamp(elapsedSeconds, 0f, 0.1f);
        float scale = Math.Clamp(sizeScale, 0.5f, 6f);
        AlphaCount = 0;
        AdditiveCount = 0;
        PendingShake = 0f;

        for (int i = 0; i < Capacity; i++)
        {
            ref Round round = ref _rounds[i];

            if (round.FlightTime <= 0f)
            {
                continue;
            }

            round.Previous = round.Position;
            round.Age += dt;

            float t = Math.Clamp(round.Age / round.FlightTime, 0f, 1f);
            round.Position = Path(in round, t);

            if (round.Trail > 0f)
            {
                round.TrailCarry += round.Trail * dt;

                while (round.TrailCarry >= 1f)
                {
                    round.TrailCarry -= 1f;
                    _particles.SpawnRocketTrail(round.Position, round.Color, round.ImpactScale);
                }
            }

            if (t >= 1f)
            {
                ApplyImpact(round.Position, round.Impact, round.ImpactScale, round.Color);
                PendingShake = MathF.Max(PendingShake, round.Shake * 0.6f);
                round.FlightTime = 0f;
                _live--;
                continue;
            }

            AddInstance(in round, cameraRight, cameraUp, scale);
        }

        for (int i = 0; i < CookOffCapacity; i++)
        {
            ref CookOff cook = ref _cookOffs[i];

            if (cook.Scale <= 0f)
            {
                continue;
            }

            cook.Delay -= dt;

            if (cook.Delay > 0f)
            {
                continue;
            }

            _particles.SpawnExplosion(cook.Position, cook.Scale, cook.Tint);
            cook.Scale = 0f;
        }
    }

    /// <summary>Where a round is along its path: straight, or bowed if it arcs.</summary>
    private static Vector3 Path(in Round round, float t)
    {
        Vector3 straight = Vector3.Lerp(round.Origin, round.Destination, t);

        if (round.Arc <= 0f)
        {
            return straight + round.Lateral;
        }

        // A parabola through the two ends: the drawn path is lifted in the middle by
        // a fraction of the range, which is enough to read as a thrown shell.
        float lift = 4f * t * (1f - t) * round.Arc * Vector3.Distance(round.Origin, round.Destination);

        return straight + round.Lateral + new Vector3(0f, lift, 0f);
    }

    private void AddInstance(in Round round, Vector3 cameraRight, Vector3 cameraUp, float scale)
    {
        Vector3 forward = round.Position - round.Previous;

        if (forward.LengthSquared() < 1e-8f)
        {
            return;
        }

        forward.Normalize();

        bool additive = round.Style is FireStyle.Bullet or FireStyle.Flak or FireStyle.Bolt;

        Vector3 right;
        Vector3 up;

        if (additive)
        {
            // A tracer is a streak facing the camera, so it keeps its width at every
            // angle. Anything drawn as a real tube would vanish edge-on.
            Vector3 toCamera = Vector3.Cross(cameraRight, cameraUp);
            right = Vector3.Normalize(Vector3.Cross(forward, toCamera));
            up = Vector3.Normalize(Vector3.Cross(right, forward));
        }
        else
        {
            Vector3 reference = MathF.Abs(forward.Y) > 0.99f ? Vector3.Right : Vector3.Up;
            right = Vector3.Normalize(Vector3.Cross(reference, forward));
            up = Vector3.Normalize(Vector3.Cross(forward, right));
        }

        float size = scale;
        float length = round.Length * size;
        float width = round.Width * size;

        Matrix rotation = new(
            right.X, right.Y, right.Z, 0f,
            up.X, up.Y, up.Z, 0f,
            forward.X, forward.Y, forward.Z, 0f,
            0f, 0f, 0f, 1f);

        // Trails and shells fade out over the last of the flight only when they are
        // very short-lived; a shell that dimmed in flight would look like it was
        // running out of energy, not like it was travelling.
        float alpha = round.Style switch
        {
            FireStyle.Bullet or FireStyle.Flak => 0.92f,
            FireStyle.Bolt => 1f,
            _ => 1f,
        };

        Matrix transform = Matrix.CreateScale(width, width, length) * rotation * Matrix.CreateTranslation(round.Position);
        var instance = new InstanceData(transform, new Vector4(round.Color, alpha));

        if (additive)
        {
            if (AdditiveCount < _additiveInstances.Length)
            {
                _additiveInstances[AdditiveCount++] = instance;
            }
        }
        else if (AlphaCount < _alphaInstances.Length)
        {
            _alphaInstances[AlphaCount++] = instance;
        }
    }

    private void ApplyImpact(Vector3 position, FireImpact impact, float scale, Vector3 color)
    {
        switch (impact)
        {
            case FireImpact.Sparks:
                _particles.SpawnImpactSparks(position, color, scale);
                break;

            case FireImpact.SmallBurst:
                _particles.SpawnSmallBurst(position, scale);
                break;

            case FireImpact.ShellBurst:
                _particles.SpawnShellBurst(position, scale);
                break;

            case FireImpact.GroundBurst:
                _particles.SpawnGroundBurst(position, scale);
                break;

            case FireImpact.Airburst:
                _particles.SpawnAirburst(position, scale, color);
                break;

            case FireImpact.HeBurst:
                _particles.SpawnExplosion(position, scale, new Vector3(0.30f, 0.27f, 0.24f));
                _particles.SpawnShockwave(position, scale * 0.7f);
                break;

            case FireImpact.Electric:
                _particles.SpawnElectricBurst(position, scale);
                break;
        }
    }
}
