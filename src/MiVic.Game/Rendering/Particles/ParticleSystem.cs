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

    /// <summary>The fireball itself: bright, expands hard, dies in a fraction of a second.</summary>
    Fire = 4,

    /// <summary>A flat ring on the ground, expanding: the shockwave of a big blast.</summary>
    Ring = 5,

    /// <summary>A flat scorch mark, left on the ground where something burned.</summary>
    Scorch = 6,
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

    /// <summary>Rings are a separate list because they need different geometry.</summary>
    public const int RingCapacity = 64;

    private readonly Particle[] _particles = new Particle[Capacity];
    private readonly InstanceData[] _alphaInstances = new InstanceData[Capacity];
    private readonly InstanceData[] _additiveInstances = new InstanceData[Capacity];
    private readonly InstanceData[] _ringInstances = new InstanceData[RingCapacity];
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

    /// <summary>Shockwave rings, drawn with an annulus rather than a quad.</summary>
    public InstanceData[] RingInstances => _ringInstances;

    /// <summary>How many rings to draw this frame.</summary>
    public int RingCount { get; private set; }

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

    // ------------------------------------------------------------------------
    // Weapon fire and impact.
    //
    // These are the profiles a shot is made of. They are deliberately separate
    // from SpawnExplosion, which is about something being destroyed; a rifle
    // round landing on a tank is not a small explosion, it is a spark and a
    // puff, and drawing it as a scaled-down fireball is what makes a firefight
    // read as one continuous mush of orange.
    // ------------------------------------------------------------------------

    /// <summary>The flash at the muzzle of a weapon that has just fired.</summary>
    public void SpawnMuzzleFlash(Vector3 position, Vector3 color, float scale)
    {
        if (scale <= 0.01f)
        {
            return;
        }

        // Three particles: a bright core, a wider flash around it, and one puff of
        // smoke that is still there when the light has gone.
        Spawn(new Particle
        {
            Position = position,
            Velocity = Vector3.Zero,
            Life = 0f,
            MaxLife = 0.055f,
            StartSize = scale * 1.5f,
            EndSize = scale * 2.1f,
            StartColor = new Vector3(1f, 0.97f, 0.86f),
            EndColor = new Vector3(1f, 0.92f, 0.62f),
            StartAlpha = 0.95f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Fire,
        });

        Spawn(new Particle
        {
            Position = position,
            Velocity = Vector3.Zero,
            Life = 0f,
            MaxLife = 0.10f,
            StartSize = scale * 1.1f,
            EndSize = scale * 2.6f,
            StartColor = color,
            EndColor = color * 0.55f,
            StartAlpha = 0.55f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Fire,
        });

        SpawnSmokePuff(position, scale * 0.30f);
    }

    /// <summary>
    /// A round striking armour or dirt: sparks and a small puff. This is what most
    /// shots in the game land as.
    /// </summary>
    public void SpawnImpactSparks(Vector3 position, Vector3 color, float scale)
    {
        int sparks = 4 + (int)(scale * 5f);

        for (int i = 0; i < sparks; i++)
        {
            Vector3 direction = RandomUnitVector();
            float speed = 3.5f + ((float)_random.NextDouble() * 12f);

            Spawn(new Particle
            {
                Position = position,
                Velocity = new Vector3(direction.X, MathF.Abs(direction.Y) * 0.9f, direction.Z) * speed * scale,
                Life = 0f,
                MaxLife = 0.10f + ((float)_random.NextDouble() * 0.16f),
                StartSize = 0.16f * scale,
                EndSize = 0.03f,
                StartColor = new Vector3(1f, 0.90f, 0.62f),
                EndColor = new Vector3(1f, 0.42f, 0.14f),
                StartAlpha = 1f,
                EndAlpha = 0f,
                Gravity = -22f,
                Drag = 0.6f,
                Kind = ParticleKind.Spark,
            });
        }

        SpawnSmokePuff(position, scale * 0.22f);
        SpawnDust(position, scale * 0.25f);
    }

    /// <summary>A small dirty burst: autocannon fire and light rockets.</summary>
    public void SpawnSmallBurst(Vector3 position, float scale)
    {
        SpawnFireball(position, scale * 0.55f, 0.22f);
        SpawnDust(position, scale * 0.5f);

        for (int i = 0; i < 5; i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position,
                Velocity = (new Vector3(direction.X, MathF.Abs(direction.Y), direction.Z) * 6f * scale)
                    + new Vector3(0f, 2f, 0f),
                Life = 0f,
                MaxLife = 0.2f + ((float)_random.NextDouble() * 0.3f),
                StartSize = 0.25f * scale,
                EndSize = 0.05f,
                StartColor = new Vector3(1f, 0.82f, 0.45f),
                EndColor = new Vector3(0.9f, 0.28f, 0.08f),
                StartAlpha = 1f,
                EndAlpha = 0f,
                Gravity = -16f,
                Drag = 1.1f,
                Kind = ParticleKind.Spark,
            });
        }
    }

    /// <summary>A tank round landing: a hard flash, thrown dirt, and a scorch.</summary>
    public void SpawnShellBurst(Vector3 position, float scale)
    {
        SpawnFireball(position, scale * 0.85f, 0.20f);
        SpawnDust(position, scale * 1.0f);
        SpawnScorch(position, scale * 0.55f);

        for (int i = 0; i < 12; i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position,
                Velocity = new Vector3(direction.X, 0.5f + (MathF.Abs(direction.Y) * 1.4f), direction.Z)
                    * (5f + ((float)_random.NextDouble() * 14f)) * scale,
                Life = 0f,
                MaxLife = 0.25f + ((float)_random.NextDouble() * 0.55f),
                StartSize = 0.3f * scale,
                EndSize = 0.06f,
                StartColor = new Vector3(0.62f, 0.55f, 0.44f),
                EndColor = new Vector3(0.36f, 0.32f, 0.27f),
                StartAlpha = 0.9f,
                EndAlpha = 0f,
                Gravity = -12f,
                Drag = 0.9f,
                Kind = ParticleKind.Debris,
            });
        }

        SpawnSmokePuff(position, scale * 0.6f);
    }

    /// <summary>
    /// An artillery shell landing: a deep burst, a long column of dirt, a crater
    /// mark, and a shockwave, which together are the loudest thing in the game
    /// short of a nuke.
    /// </summary>
    public void SpawnGroundBurst(Vector3 position, float scale)
    {
        SpawnFireball(position, scale * 1.15f, 0.30f);
        SpawnShockwave(position, scale * 1.5f);
        SpawnScorch(position, scale * 0.9f);

        for (int i = 0; i < 16; i++)
        {
            Vector3 direction = RandomUnitVector();
            float speed = 7f + ((float)_random.NextDouble() * 20f);

            Spawn(new Particle
            {
                Position = position,
                Velocity = new Vector3(direction.X, 0.9f + (MathF.Abs(direction.Y) * 1.8f), direction.Z) * speed * scale,
                Life = 0f,
                MaxLife = 0.6f + ((float)_random.NextDouble() * 1.1f),
                StartSize = 0.5f * scale,
                EndSize = 0.1f,
                StartColor = new Vector3(0.48f, 0.41f, 0.31f),
                EndColor = new Vector3(0.30f, 0.26f, 0.21f),
                StartAlpha = 0.95f,
                EndAlpha = 0f,
                Gravity = -13f,
                Drag = 0.7f,
                Kind = ParticleKind.Debris,
            });
        }

        // The column: smoke that goes up rather than out, which is what separates a
        // shell crater from a campfire.
        for (int i = 0; i < 10; i++)
        {
            float shade = 0.26f + ((float)_random.NextDouble() * 0.14f);

            Spawn(new Particle
            {
                Position = position + new Vector3(
                    ((float)_random.NextDouble() - 0.5f) * scale,
                    0f,
                    ((float)_random.NextDouble() - 0.5f) * scale),
                Velocity = new Vector3(
                    ((float)_random.NextDouble() - 0.5f) * 1.2f,
                    4f + ((float)_random.NextDouble() * 5f),
                    ((float)_random.NextDouble() - 0.5f) * 1.2f),
                Life = 0f,
                MaxLife = 2.2f + ((float)_random.NextDouble() * 2.4f),
                StartSize = 1.2f * scale,
                EndSize = 4.2f * scale,
                StartColor = new Vector3(shade, shade * 0.97f, shade * 0.93f),
                EndColor = new Vector3(shade * 0.4f, shade * 0.4f, shade * 0.42f),
                StartAlpha = 0.55f,
                EndAlpha = 0f,
                Gravity = 1.4f,
                Drag = 0.9f,
                Kind = ParticleKind.Smoke,
            });
        }

        SpawnDust(position, scale * 1.6f);
    }

    /// <summary>A fused round bursting in the air: a hard puff with nothing under it.</summary>
    public void SpawnAirburst(Vector3 position, float scale, Vector3 color)
    {
        SpawnFireball(position, scale * 0.5f, 0.13f);

        for (int i = 0; i < 9; i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position,
                Velocity = direction * (6f + ((float)_random.NextDouble() * 16f)) * scale,
                Life = 0f,
                MaxLife = 0.18f + ((float)_random.NextDouble() * 0.22f),
                StartSize = 0.22f * scale,
                EndSize = 0.04f,
                StartColor = color,
                EndColor = color * 0.4f,
                StartAlpha = 1f,
                EndAlpha = 0f,
                Gravity = -6f,
                Drag = 1.4f,
                Kind = ParticleKind.Spark,
            });
        }

        // A flak burst is a ball of black smoke, not a fireball: it hangs where the
        // fuse went off and is gone in a second.
        for (int i = 0; i < 4; i++)
        {
            Spawn(new Particle
            {
                Position = position,
                Velocity = RandomUnitVector() * 2.4f * scale,
                Life = 0f,
                MaxLife = 0.5f + ((float)_random.NextDouble() * 0.5f),
                StartSize = 0.7f * scale,
                EndSize = 2.4f * scale,
                StartColor = new Vector3(0.20f, 0.19f, 0.19f),
                EndColor = new Vector3(0.34f, 0.33f, 0.34f),
                StartAlpha = 0.7f,
                EndAlpha = 0f,
                Gravity = -0.4f,
                Drag = 2.2f,
                Kind = ParticleKind.Smoke,
            });
        }
    }

    /// <summary>A crackling electric discharge, for the Ηλεκτροπυροβόλο.</summary>
    public void SpawnElectricBurst(Vector3 position, float scale)
    {
        Spawn(new Particle
        {
            Position = position,
            Velocity = Vector3.Zero,
            Life = 0f,
            MaxLife = 0.09f,
            StartSize = scale * 1.8f,
            EndSize = scale * 3.4f,
            StartColor = new Vector3(0.80f, 0.94f, 1f),
            EndColor = new Vector3(0.35f, 0.60f, 1f),
            StartAlpha = 0.95f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Fire,
        });

        for (int i = 0; i < 14; i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position,
                Velocity = direction * (9f + ((float)_random.NextDouble() * 22f)) * scale,
                Life = 0f,
                MaxLife = 0.10f + ((float)_random.NextDouble() * 0.20f),
                StartSize = 0.14f * scale,
                EndSize = 0.03f,
                StartColor = new Vector3(0.86f, 0.96f, 1f),
                EndColor = new Vector3(0.30f, 0.52f, 1f),
                StartAlpha = 1f,
                EndAlpha = 0f,
                Gravity = -4f,
                Drag = 1.0f,
                Kind = ParticleKind.Spark,
            });
        }

        SpawnSmokePuff(position, scale * 0.25f);
    }

    /// <summary>One puff of a rocket's exhaust, left behind as it flies.</summary>
    public void SpawnRocketTrail(Vector3 position, Vector3 color, float scale)
    {
        float shade = 0.42f + ((float)_random.NextDouble() * 0.12f);

        Spawn(new Particle
        {
            Position = position + new Vector3(
                ((float)_random.NextDouble() - 0.5f) * 0.4f * scale,
                ((float)_random.NextDouble() - 0.5f) * 0.4f * scale,
                ((float)_random.NextDouble() - 0.5f) * 0.4f * scale),
            Velocity = new Vector3(0f, 0.5f + ((float)_random.NextDouble() * 1.1f), 0f),
            Life = 0f,
            MaxLife = 0.6f + ((float)_random.NextDouble() * 0.9f),
            StartSize = 0.28f * scale,
            EndSize = 1.5f * scale,
            StartColor = new Vector3(shade + 0.30f, shade + 0.16f, shade * 0.7f),
            EndColor = new Vector3(shade * 0.7f, shade * 0.7f, shade * 0.72f),
            StartAlpha = 0.55f,
            EndAlpha = 0f,
            Gravity = 0.4f,
            Drag = 1.8f,
            Kind = ParticleKind.Smoke,
        });
    }

    /// <summary>An expanding ring on the ground: the shockwave of a large blast.</summary>
    public void SpawnShockwave(Vector3 position, float scale)
    {
        // Capped in metres, not in multiples of the blast: the rings are the one
        // effect whose size is a multiple of whatever scale it is handed, and a
        // nuclear blast handed its own radius produced a ring two hundred metres
        // across that swept the camera like a searchlight.
        float endSize = MathF.Min(7.5f * scale, 70f);

        Spawn(new Particle
        {
            // Just above the ground so it is not fighting the terrain for the depth
            // buffer, which at a shallow camera angle produces a stipple of holes.
            Position = new Vector3(position.X, position.Y + 0.35f, position.Z),
            Velocity = Vector3.Zero,
            Life = 0f,
            MaxLife = 0.42f + (scale * 0.05f),
            StartSize = MathF.Min(1.2f * scale, 14f),
            EndSize = endSize,
            StartColor = new Vector3(1f, 0.90f, 0.72f),
            EndColor = new Vector3(1f, 0.72f, 0.45f),
            StartAlpha = 0.60f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Ring,
        });
    }

    /// <summary>A burn mark on the ground, left for a while after a blast.</summary>
    public void SpawnScorch(Vector3 position, float scale)
    {
        Spawn(new Particle
        {
            Position = new Vector3(position.X, position.Y + 0.18f, position.Z),
            Velocity = Vector3.Zero,
            Life = 0f,
            MaxLife = 14f + ((float)_random.NextDouble() * 8f),
            StartSize = 1.6f * scale,
            EndSize = 2.4f * scale,
            StartColor = new Vector3(0.10f, 0.09f, 0.08f),
            EndColor = new Vector3(0.16f, 0.15f, 0.13f),
            StartAlpha = 0.55f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Scorch,
        });
    }

    /// <summary>A small puff of smoke, used as punctuation by several effects.</summary>
    public void SpawnSmokePuff(Vector3 position, float scale)
    {
        for (int i = 0; i < 2; i++)
        {
            float shade = 0.38f + ((float)_random.NextDouble() * 0.12f);

            Spawn(new Particle
            {
                Position = position,
                Velocity = new Vector3(
                    ((float)_random.NextDouble() - 0.5f) * 1.4f,
                    0.8f + ((float)_random.NextDouble() * 1.4f),
                    ((float)_random.NextDouble() - 0.5f) * 1.4f),
                Life = 0f,
                MaxLife = 0.7f + ((float)_random.NextDouble() * 0.8f),
                StartSize = 0.5f * scale,
                EndSize = 1.9f * scale,
                StartColor = new Vector3(shade, shade * 0.97f, shade * 0.94f),
                EndColor = new Vector3(shade * 0.5f, shade * 0.5f, shade * 0.52f),
                StartAlpha = 0.45f,
                EndAlpha = 0f,
                Gravity = 0.9f,
                Drag = 1.6f,
                Kind = ParticleKind.Smoke,
            });
        }
    }

    /// <summary>The core of an explosion: a fireball that is bright for a few frames.</summary>
    private void SpawnFireball(Vector3 position, float scale, float life)
    {
        Spawn(new Particle
        {
            Position = position,
            Velocity = new Vector3(0f, 0.6f, 0f),
            Life = 0f,
            MaxLife = life * 0.55f,
            StartSize = scale * 1.1f,
            EndSize = scale * 1.9f,
            StartColor = new Vector3(1f, 0.98f, 0.90f),
            EndColor = new Vector3(1f, 0.85f, 0.45f),
            StartAlpha = 1f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Fire,
        });

        Spawn(new Particle
        {
            Position = position,
            Velocity = new Vector3(0f, 1.2f, 0f),
            Life = 0f,
            MaxLife = life,
            StartSize = scale * 1.3f,
            EndSize = scale * 3.1f,
            StartColor = new Vector3(1f, 0.74f, 0.26f),
            EndColor = new Vector3(0.62f, 0.16f, 0.05f),
            StartAlpha = 0.85f,
            EndAlpha = 0f,
            Gravity = 0.6f,
            Drag = 0.8f,
            Kind = ParticleKind.Fire,
        });
    }

    /// <summary>
    /// A tactical nuclear detonation.
    /// <para>
    /// Deliberately unlike every other effect in the game, because it is the one
    /// event a player has to be able to identify instantly and from any distance: a
    /// white flash, a shockwave that crosses the map, a scorched ring, and a column
    /// that goes up and then spreads into a cap. Nothing else here is allowed to
    /// last five seconds.
    /// </para>
    /// </summary>
    /// <param name="blastRadius">
    /// The radius the blast actually damages, in metres, as the ability catalogue
    /// states it. Everything here is derived from that number rather than from a
    /// chosen size: an effect that does not match the circle it kills in is a lie the
    /// player will notice the first time they stand outside it and die anyway.
    /// </param>
    public void SpawnNuke(Vector3 position, float blastRadius)
    {
        float radius = Math.Clamp(blastRadius, 20f, 200f);

        // The flash: everything is white for a quarter of a second.
        Spawn(new Particle
        {
            Position = position,
            Velocity = Vector3.Zero,
            Life = 0f,
            MaxLife = 0.30f,
            StartSize = radius * 0.18f,
            EndSize = radius * 0.38f,
            StartColor = new Vector3(1f, 1f, 0.98f),
            EndColor = new Vector3(1f, 0.92f, 0.62f),
            StartAlpha = 1f,
            EndAlpha = 0f,
            Gravity = 0f,
            Drag = 0f,
            Kind = ParticleKind.Fire,
        });

        // Rings out to the weapon's real radius, which is what tells a player how far
        // away is far enough.
        for (int i = 0; i < 3; i++)
        {
            SpawnShockwave(position, radius * (0.35f + (i * 0.325f)));
        }

        SpawnScorch(position, radius * 0.20f);

        // The stem: dense smoke thrown straight up, which is the shape everyone
        // recognises and the reason this is a column and not a ball.
        for (int i = 0; i < 40; i++)
        {
            float shade = 0.30f + ((float)_random.NextDouble() * 0.20f);
            float angle = (float)_random.NextDouble() * MathF.Tau;
            float spread = (float)_random.NextDouble() * radius * 0.25f;

            Spawn(new Particle
            {
                Position = position + new Vector3(
                    MathF.Cos(angle) * spread,
                    (float)_random.NextDouble() * radius * 0.15f,
                    MathF.Sin(angle) * spread),
                Velocity = new Vector3(
                    MathF.Cos(angle) * (0.6f + ((float)_random.NextDouble() * 2f)),
                    7f + ((float)_random.NextDouble() * 12f) + (radius * 0.05f),
                    MathF.Sin(angle) * (0.6f + ((float)_random.NextDouble() * 2f))),
                Life = 0f,
                MaxLife = 3.4f + ((float)_random.NextDouble() * 2.6f),
                StartSize = 3.5f + (radius * 0.03f),
                EndSize = 9f + (radius * 0.10f),
                StartColor = new Vector3(shade + 0.22f, shade + 0.10f, shade * 0.8f),
                EndColor = new Vector3(shade * 0.5f, shade * 0.5f, shade * 0.55f),
                StartAlpha = 0.80f,
                EndAlpha = 0f,
                Gravity = 1.6f,
                Drag = 0.55f,
                Kind = ParticleKind.Smoke,
            });
        }

        // The cap, high above the stem and spreading outwards rather than up.
        float capHeight = 55f + (radius * 0.5f);

        for (int i = 0; i < 34; i++)
        {
            float shade = 0.34f + ((float)_random.NextDouble() * 0.22f);
            float angle = (float)_random.NextDouble() * MathF.Tau;
            float spread = (float)_random.NextDouble();

            Spawn(new Particle
            {
                Position = position + new Vector3(
                    MathF.Cos(angle) * radius * 0.5f * spread,
                    capHeight + (((float)_random.NextDouble() - 0.5f) * radius * 0.4f),
                    MathF.Sin(angle) * radius * 0.5f * spread),
                Velocity = new Vector3(
                    MathF.Cos(angle) * (2f + ((float)_random.NextDouble() * 5f)),
                    1.4f + ((float)_random.NextDouble() * 3.4f),
                    MathF.Sin(angle) * (2f + ((float)_random.NextDouble() * 5f))),
                Life = 0f,
                MaxLife = 4.2f + ((float)_random.NextDouble() * 3.4f),
                StartSize = 6f + (radius * 0.08f),
                EndSize = 14f + (radius * 0.16f),
                StartColor = new Vector3(shade, shade * 0.96f, shade * 0.92f),
                EndColor = new Vector3(shade * 0.45f, shade * 0.45f, shade * 0.5f),
                StartAlpha = 0.72f,
                EndAlpha = 0f,
                Gravity = 0.8f,
                Drag = 0.9f,
                Kind = ParticleKind.Smoke,
            });
        }

        // Debris thrown out of the crater, on real ballistic arcs.
        for (int i = 0; i < 36; i++)
        {
            Vector3 direction = RandomUnitVector();

            Spawn(new Particle
            {
                Position = position + new Vector3(0f, 1f, 0f),
                Velocity = new Vector3(direction.X, 1.2f + (MathF.Abs(direction.Y) * 2.2f), direction.Z)
                    * (10f + ((float)_random.NextDouble() * 26f)),
                Life = 0f,
                MaxLife = 1.2f + ((float)_random.NextDouble() * 2.2f),
                StartSize = 0.6f,
                EndSize = 0.12f,
                StartColor = new Vector3(0.44f, 0.38f, 0.30f),
                EndColor = new Vector3(0.26f, 0.23f, 0.19f),
                StartAlpha = 1f,
                EndAlpha = 0f,
                Gravity = -14f,
                Drag = 0.35f,
                Kind = ParticleKind.Debris,
            });
        }

        SpawnDust(position, radius * 0.30f);
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
        RingCount = 0;

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
            //
            // Rings and scorch marks are the exception: they lie flat on the ground,
            // because a billboarded shockwave is a ring standing on its edge, which
            // reads as a hoop rolling past rather than as a blast going outwards.
            Matrix transform;

            if (particle.Kind is ParticleKind.Ring or ParticleKind.Scorch)
            {
                transform = Matrix.CreateScale(size, size, 1f)
                    * Matrix.CreateRotationX(MathHelper.PiOver2)
                    * Matrix.CreateTranslation(particle.Position);
            }
            else
            {
                transform = new Matrix(
                    cameraRight.X * size, cameraRight.Y * size, cameraRight.Z * size, 0f,
                    cameraUp.X * size, cameraUp.Y * size, cameraUp.Z * size, 0f,
                    0f, 0f, 0f, 0f,
                    particle.Position.X, particle.Position.Y, particle.Position.Z, 1f);
            }

            var instance = new InstanceData(transform, new Vector4(color, alpha));

            // Rings go to their own list: they are the one effect that is not a quad.
            if (particle.Kind == ParticleKind.Ring)
            {
                if (RingCount < _ringInstances.Length)
                {
                    _ringInstances[RingCount++] = instance;
                }

                continue;
            }

            bool additive = particle.Kind is ParticleKind.Spark or ParticleKind.Fire;

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
