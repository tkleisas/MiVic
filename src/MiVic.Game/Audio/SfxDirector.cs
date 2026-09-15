using MiVic.Audio;
using Microsoft.Xna.Framework;
using Microsoft.Xna.Framework.Audio;
using ChipSoundBank = MiVic.Audio.SoundBank;

namespace MiVic.Game.Audio;

/// <summary>
/// Plays procedural sound effects for combat.
/// <para>
/// The bank is generated once, on first use: ten short effects cost a few
/// hundred kilobytes and no disk. Playback is deliberately rate-limited and
/// distance-attenuated — a battle between five hundred units produces far more
/// events than a sound card can mix, and the useful information is "something is
/// happening over there", not every individual round.
/// </para>
/// <para>
/// <b>Every kind carries takes.</b> One waveform per kind means a rifle platoon firing
/// four rounds plays one sample four times, which the ear hears as a loop within the
/// first second — the single biggest reason a firefight sounds like a loop. Each kind
/// is rendered a few times from derived seeds, and the play picks a take by the count
/// of everything played so far: deterministic, so two machines agree on which take
/// was where, and varied enough that no two neighbouring rifles sound alike.
/// </para>
/// </summary>
public sealed class SfxDirector : IDisposable
{
    /// <summary>Maximum effects started per frame, so a firefight cannot flood the mixer.</summary>
    public const int MaxSoundsPerFrame = 6;

    /// <summary>Takes rendered per kind: three reads as variation, not as a different weapon.</summary>
    public const int TakesPerKind = 3;

    /// <summary>Falloff for a close-in camera, in metres.</summary>
    public const float DefaultFalloffDistance = 220f;

    /// <summary>
    /// Distance at which an effect becomes inaudible, in metres. The client
    /// raises this as the camera pulls back, so strategic zoom does not silence
    /// half the battlefield.
    /// </summary>
    public float FalloffDistance { get; set; } = DefaultFalloffDistance;

    private readonly Dictionary<SoundEffectKind, SoundEffect[]> _takes = [];
    private readonly ulong _seed;

    private int _playedThisFrame;

    public SfxDirector(ulong seed)
    {
        _seed = seed;
    }

    /// <summary>True when no audio device was available and effects are off.</summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>True when the mixer is muted.</summary>
    public bool IsMuted { get; set; }

    /// <summary>Effects started since construction, for diagnostics.</summary>
    public int PlayedCount { get; private set; }

    /// <summary>Effects dropped because the per-frame budget was exhausted.</summary>
    public int DroppedCount { get; private set; }

    /// <summary>Number of effects generated so far.</summary>
    public int GeneratedCount => _takes.Count * TakesPerKind;

    /// <summary>Resets the per-frame budget. Call once at the start of a frame.</summary>
    public void BeginFrame() => _playedThisFrame = 0;

    /// <summary>
    /// Plays <paramref name="kind"/> at a world position, attenuated by distance
    /// from <paramref name="listener"/> and panned by where it sits on screen.
    /// The take is the play count's: deterministic, and varied between neighbours.
    /// </summary>
    public void Play(SoundEffectKind kind, Vector3 position, Vector3 listener, float volume = 1f, float pitch = 0f)
    {
        if (IsMuted || IsUnavailable)
        {
            return;
        }

        if (_playedThisFrame >= MaxSoundsPerFrame)
        {
            DroppedCount++;
            return;
        }

        float distance = Vector3.Distance(position, listener);
        float attenuation = 1f - Math.Clamp(distance / MathF.Max(FalloffDistance, 1f), 0f, 1f);

        if (attenuation <= 0.03f)
        {
            return;
        }

        // Pan by the horizontal offset relative to the listener, which is what
        // stereo is for: knowing which flank the shooting is on.
        float pan = Math.Clamp((position.X - listener.X) / 90f, -1f, 1f);

        try
        {
            SoundEffect[] takes = GetOrCreate(kind);
            SoundEffect effect = takes[PlayedCount % takes.Length];

            // The take carries most of the variation; the pitch wander carries the rest,
            // and keeps even a same-take repeat from landing identically.
            float jitter = (((PlayedCount * 37) % 5) - 2) * 0.015f;

            effect.Play(Math.Clamp(volume * attenuation, 0f, 1f), Math.Clamp(pitch + jitter, -1f, 1f), pan);
            _playedThisFrame++;
            PlayedCount++;
        }
        catch (Exception exception) when (exception is NoAudioHardwareException or InvalidOperationException or ArgumentException)
        {
            IsUnavailable = true;
        }
    }

    private SoundEffect[] GetOrCreate(SoundEffectKind kind)
    {
        if (_takes.TryGetValue(kind, out SoundEffect[]? existing))
        {
            return existing;
        }

        var takes = new SoundEffect[TakesPerKind];

        for (int take = 0; take < TakesPerKind; take++)
        {
            // Each take is the kind's own seed salted with the take's index, so the three
            // are the same weapon on different days and not three different weapons.
            ulong takeSeed = _seed ^ (((ulong)kind * TakesPerKind) + (ulong)take + 1UL) * 0x9E37_79B9_7F4A_7C15UL;

            short[] pcm = ChipSoundBank.GeneratePcm16(kind, takeSeed);
            byte[] bytes = new byte[pcm.Length * sizeof(short)];
            Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

            takes[take] = new SoundEffect(bytes, ChipSoundBank.SampleRate, AudioChannels.Mono);
        }

        _takes[kind] = takes;
        return takes;
    }

    public void Dispose()
    {
        foreach (SoundEffect[] group in _takes.Values)
        {
            foreach (SoundEffect effect in group)
            {
                effect.Dispose();
            }
        }

        _takes.Clear();
    }
}
