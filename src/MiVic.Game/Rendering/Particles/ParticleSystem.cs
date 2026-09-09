using Microsoft.Xna.Framework;

namespace MiVic.Game.Rendering.Particles;

/// <summary>What a particle represents, which decides how it moves and blends.</summary>
public enum ParticleKind : byte
{
    /// <summary>Dark smoke: rises, expands, fades.</summary>
    Smoke = 0,

    /// <summary>Bright spark: falls fast, short life, additive.</summary>
    Spark = 1,

    /// <summary>Tumbling debris chunk: gravity, no fade until the end.</summary>
    Debris = 2,

    /// <summary>Light dust: hangs near the ground and drifts.</summary>
    Dust = 3,
}

/// <summary>One live particle. Plain data, integrated every frame.</summary>
public struct Particle
{
    public Vector3 Position;
    public Vector3 Velocity;
    public float Life;
    public float MaxLife;
    public float StartSize;
    public float EndSize;
    public Vector3 StartColor;
    public Vector3 EndColor;
    public float StartAlpha;
    public float EndAlpha;
    public float Gravity;
    public float Drag;
    public ParticleKind Kind;
}

/// <summary>
/// A CPU particle system for explosions, smoke and dust.
/// <para>
/// This is presentation only: it reads simulation events and never writes to the
/// simulation, and it uses its own generator rather than the simulation's, so
/// nothing here can change the outcome of a tick or a replay. A fixed-capacity
/// ring buffer keeps the cost bounded no matter how much explodes at once — the
/// oldest particle is recycled rather than the frame being allowed to grow.
/// </para>
/// </summary>
public sealed class ParticleSystem
{
    /// <summary>Hard cap on live particles. The oldest are recycled past this.</summary>
    public const int Capacity = 4096;

    private readonly Particle[] _particles = new Particle[Capacity];
    private readonly InstanceData[] _alphaInstances = new InstanceData[Capacity];
    private readonly InstanceData[] _additiveInstances = new InstanceData[Capacity];
    private readonly Random _random = new(20250101);

    private int _next;
    private int _live;

    /// <summary>Particles created since construction, for diagnostics.</summary>
    public int TotalSpawned { get; private set; }

    /// <summary>Number of live particles.</summary>
    public int LiveCount => _live;

    /// <summary>Instances queued for the straight-alpha pass this frame.</summary>
    public int AlphaCount { get; private set; }

    /// <summary>Instances queued for the additive pass this frame.</summary>
    public int AdditiveCount { get; private set; }

    /// <summary>Alpha-blended instances: smoke, dust, debris.</summary>
    public InstanceData[] AlphaInstances => _alphaInstances;

    /// <summary>Additively blended instances: sparks and fire.</summary>
    public InstanceData[] AdditiveInstances => _additiveInstances;

    /// <summary>Removes every particle, e.g. when loading a different world.</summary>
    public void Clear()
    {
        Array.Clear(_particles);
        _next = 0;
        _live = 0;
        AlphaCount = 0;
        AdditiveCount = 0;
    }

    /// <summary>
    /// An explosion: a bright core of sparks, a fireball of dust and smoke, and
    /// a few tumbling chunks. <paramref name="scale"/> is roughly the radius in
    /// metres, so a building and an infantryman do not look the same.
    /// </summary>
    public void SpawnExplosion(Vector3 position, float scale, Vector3? tint = null)
    {
        float clamped = Math.Clamp(scale, 0.6f, 14f);
        int sparks = (int)(18f * clamped);
        int smoke = (int)(10f * clamped);

        for (int i = 0; i < sparks; i++)
        {
            Vector3 direction = RandomUnitVector();
            float speed = (2.5f + (float)_random.NextDouble() * 9f) * clamped * 0.6f;

            Spawn(new Particle
            {
                Position = position,
                Velocity = (direction * speed) + new Vector3(0f, 1.5f * clamped, 0f),
                Life = 0f,
                MaxLife = 0.35f + ((float)_random.NextDouble() * 0.45f),
                StartSize = 0.35f * clamped,
                EndSize = 0.05f,
                StartColor = new Vector3(1f, 0.85f, 0.35f),
                EndColor = new Vector3(0.85f, 0.18f, 0.05f),
                StartAlpha = 1f,
                EndAlpha = 0f,
                Gravity = -14f,
                Drag = 1.6f,
                Kind = ParticleKind.Spark,
            });
        }

        for (int i = 0; i < smoke; i++)
        {
            Vector3 direction = RandomUnitVector();
            Vector3 baseColor = tint ?? new Vector3(0.30f, 0.29f, 0.28f);

            Spawn(new Particle
            {
                Position = position + (direction * clamped * 0.35f),
                Velocity = (direction * clamped * 0.9f) + new Vector3(0f, 1.8f + ((float)_random.NextDouble() * 1.6f), 0f),
                Life = 0f,
                MaxLife = 1.6f + ((float)_random.NextDouble() * 1.8f),
                StartSize = 0.9f * clamped,
                EndSize = 2.6f * clamped,
                StartColor = baseColor,
                EndColor = baseColor * 0.45f,
                StartAlpha = 0.55f,
                EndAlpha = 0f,
                Gravity = 1.1f,
                Drag = 0.7f,
                Kind = ParticleKind.Smoke,
            });
        }

        for (int i = 0; i < (int)(4f * clamped); i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position + new Vector3(0f, 0.4f * clamped, 0f),
                Velocity = (direction * (2f + ((float)_random.NextDouble() * 5f)) * clamped * 0.5f) + new Vector3(0f, 4f * clamped, 0f),
                Life = 0f,
                MaxLife = 0.9f + ((float)_random.NextDouble() * 0.9f),
                StartSize = 0.22f * clamped,
                EndSize = 0.18f * clamped,
                StartColor = new Vector3(0.32f, 0.30f, 0.27f),
                EndColor = new Vector3(0.16f, 0.15f, 0.14f),
                StartAlpha = 1f,
                EndAlpha = 0.6f,
                Gravity = -18f,
                Drag = 0.3f,
                Kind = ParticleKind.Debris,
            });
        }
    }

    /// <summary>
    /// A slow plume of smoke, used as smog over burning or working structures.
    /// <paramref name="intensity"/> in [0, 1] scales size, life and darkness.
    /// </summary>
    public void SpawnSmokePlume(Vector3 position, float intensity)
    {
        float strength = Math.Clamp(intensity, 0.05f, 1f);
        int count = 1 + (int)(strength * 2f);

        for (int i = 0; i < count; i++)
        {
            float shade = 0.34f - (strength * 0.18f);

            Spawn(new Particle
            {
                Position = position + new Vector3(
                    ((float)_random.NextDouble() - 0.5f) * 1.6f,
                    0f,
                    ((float)_random.NextDouble() - 0.5f) * 1.6f),
                Velocity = new Vector3(
                    ((float)_random.NextDouble() - 0.5f) * 0.5f,
                    1.1f + ((float)_random.NextDouble() * 1.4f * strength),
                    ((float)_random.NextDouble() - 0.5f) * 0.5f),
                Life = 0f,
                MaxLife = 2.6f + ((float)_random.NextDouble() * 2.2f),
                StartSize = 1.1f + (strength * 1.2f),
                EndSize = 3.6f + (strength * 2.4f),
                StartColor = new Vector3(shade, shade * 0.98f, shade * 0.95f),
                EndColor = new Vector3(shade * 0.4f, shade * 0.4f, shade * 0.42f),
                StartAlpha = 0.30f + (strength * 0.25f),
                EndAlpha = 0f,
                Gravity = 0.5f,
                Drag = 0.5f,
                Kind = ParticleKind.Smoke,
            });
        }
    }

    /// <summary>A puff of dust where something heavy just landed or ground moved.</summary>
    public void SpawnDust(Vector3 position, float scale)
    {
        int count = 3 + (int)(scale * 2f);

        for (int i = 0; i < count; i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position,
                Velocity = new Vector3(direction.X, 0.4f, direction.Z) * (1.2f + ((float)_random.NextDouble() * 2.2f)),
                Life = 0f,
                MaxLife = 1.1f + ((float)_random.NextDouble() * 0.8f),
                StartSize = 0.7f * scale,
                EndSize = 1.8f * scale,
                StartColor = new Vector3(0.55f, 0.52f, 0.44f),
                EndColor = new Vector3(0.45f, 0.43f, 0.38f),
                StartAlpha = 0.34f,
                EndAlpha = 0f,
                Gravity = -0.2f,
                Drag = 1.2f,
                Kind = ParticleKind.Dust,
            });
        }
    }

    /// <summary>
    /// Integrates every particle and builds the two instance lists.
    /// <paramref name="sizeScale"/> grows the particles with the camera distance:
    /// a 40 cm spark is a pixel wide at strategic zoom, which reads as nothing.
    /// </summary>
    public void Update(float elapsedSeconds, Vector3 cameraRight, Vector3 cameraUp, float sizeScale = 1f)
    {
        float dt = Math.Clamp(elapsedSeconds, 0f, 0.1f);
        float scale = Math.Clamp(sizeScale, 0.5f, 6f);
        AlphaCount = 0;
        AdditiveCount = 0;

        for (int i = 0; i < Capacity; i++)
        {
            ref Particle particle = ref _particles[i];

            if (particle.MaxLife <= 0f)
            {
                continue;
            }

            particle.Life += dt;

            if (particle.Life >= particle.MaxLife)
            {
                particle.MaxLife = 0f;
                _live--;
                continue;
            }

            float t = particle.Life / particle.MaxLife;
            float drag = 1f - Math.Clamp(particle.Drag * dt, 0f, 0.95f);

            particle.Velocity *= drag;
            particle.Velocity.Y += particle.Gravity * dt;
            particle.Position += particle.Velocity * dt;

            float size = MathHelper.Lerp(particle.StartSize, particle.EndSize, t) * scale;
            float alpha = MathHelper.Lerp(particle.StartAlpha, particle.EndAlpha, t);
            Vector3 color = Vector3.Lerp(particle.StartColor, particle.EndColor, t);

            if (alpha <= 0.004f)
            {
                continue;
            }

            // Billboard: a quad facing the camera, built from the camera's own
            // basis so the CPU cost stays one matrix per particle.
            Matrix transform = new(
                cameraRight.X * size, cameraRight.Y * size, cameraRight.Z * size, 0f,
                cameraUp.X * size, cameraUp.Y * size, cameraUp.Z * size, 0f,
                0f, 0f, 0f, 0f,
                particle.Position.X, particle.Position.Y, particle.Position.Z, 1f);

            var instance = new InstanceData(transform, new Vector4(color, alpha));

            bool additive = particle.Kind == ParticleKind.Spark;

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
    }

    private void Spawn(in Particle particle)
    {
        _particles[_next] = particle;
        TotalSpawned++;

        if (_particles[_next].MaxLife > 0f && _live < Capacity)
        {
            _live++;
        }

        _next = (_next + 1) % Capacity;
    }

    private Vector3 RandomUnitVector()
    {
        double theta = _random.NextDouble() * Math.PI * 2d;
        double z = (_random.NextDouble() * 2d) - 1d;
        double r = Math.Sqrt(Math.Max(0d, 1d - (z * z)));

        return new Vector3(
            (float)(r * Math.Cos(theta)),
            (float)z,
            (float)(r * Math.Sin(theta)));
    }
}
