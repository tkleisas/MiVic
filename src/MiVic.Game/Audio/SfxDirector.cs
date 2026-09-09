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
/// </summary>
public sealed class SfxDirector : IDisposable
{
    /// <summary>Maximum effects started per frame, so a firefight cannot flood the mixer.</summary>
    public const int MaxSoundsPerFrame = 6;

    /// <summary>Falloff for a close-in camera, in metres.</summary>
    public const float DefaultFalloffDistance = 220f;

    /// <summary>
    /// Distance at which an effect becomes inaudible, in metres. The client
    /// raises this as the camera pulls back, so strategic zoom does not silence
    /// half the battlefield.
    /// </summary>
    public float FalloffDistance { get; set; } = DefaultFalloffDistance;

    private readonly Dictionary<SoundEffectKind, SoundEffect> _effects = [];
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
    public int GeneratedCount => _effects.Count;

    /// <summary>Resets the per-frame budget. Call once at the start of a frame.</summary>
    public void BeginFrame() => _playedThisFrame = 0;

    /// <summary>
    /// Plays <paramref name="kind"/> at a world position, attenuated by distance
    /// from <paramref name="listener"/> and panned by where it sits on screen.
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
            SoundEffect effect = GetOrCreate(kind);
            effect.Play(Math.Clamp(volume * attenuation, 0f, 1f), Math.Clamp(pitch, -1f, 1f), pan);
            _playedThisFrame++;
            PlayedCount++;
        }
        catch (Exception exception) when (exception is NoAudioHardwareException or InvalidOperationException or ArgumentException)
        {
            IsUnavailable = true;
        }
    }

    private SoundEffect GetOrCreate(SoundEffectKind kind)
    {
        if (_effects.TryGetValue(kind, out SoundEffect? effect))
        {
            return effect;
        }

        short[] pcm = ChipSoundBank.GeneratePcm16(kind, _seed);
        byte[] bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        effect = new SoundEffect(bytes, ChipSoundBank.SampleRate, AudioChannels.Mono);
        _effects[kind] = effect;
        return effect;
    }

    public void Dispose()
    {
        foreach (SoundEffect effect in _effects.Values)
        {
            effect.Dispose();
        }

        _effects.Clear();
    }
}
