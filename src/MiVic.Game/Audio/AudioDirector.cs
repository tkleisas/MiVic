using MiVic.Audio;
using Microsoft.Xna.Framework.Audio;

namespace MiVic.Game.Audio;

/// <summary>
/// Plays the procedural soundtrack.
/// <para>
/// Themes are generated once, on demand, and looped. They are never loaded from
/// disk: the same seed always produces the same music, which keeps the game's
/// audio as reproducible as its simulation, and keeps the repository free of
/// sound assets. Generation takes a few hundred milliseconds per theme, so it
/// happens when a theme is first requested rather than at startup.
/// </para>
/// </summary>
public sealed class AudioDirector : IDisposable
{
    private readonly Dictionary<FactionStyle, SoundEffect> _effects = [];
    private readonly ulong _seed;

    private SoundEffectInstance? _current;
    private FactionStyle? _playing;

    public AudioDirector(ulong seed)
    {
        _seed = seed;
    }

    /// <summary>Length of a generated theme, in seconds.</summary>
    public const double ThemeSeconds = 30d;

    /// <summary>True when the soundtrack is muted.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>Which faction's theme is currently playing, if any.</summary>
    public FactionStyle? Playing => _playing;

    /// <summary>Number of themes generated so far, for diagnostics.</summary>
    public int GeneratedCount => _effects.Count;

    /// <summary>True when no audio device was available and the soundtrack is off.</summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>
    /// Starts <paramref name="style"/>'s theme, generating it if necessary. A
    /// theme already playing is left alone.
    /// </summary>
    public void Play(FactionStyle style)
    {
        if (IsMuted || IsUnavailable || _playing == style)
        {
            return;
        }

        Stop();

        try
        {
            if (!_effects.TryGetValue(style, out SoundEffect? effect))
            {
                short[] pcm = MusicGenerator.GeneratePcm16(style, _seed, ThemeSeconds, out _);

                byte[] bytes = new byte[pcm.Length * sizeof(short)];
                Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

                effect = new SoundEffect(bytes, MusicGenerator.SampleRate, AudioChannels.Mono);
                _effects[style] = effect;
            }

            _current = effect.CreateInstance();
            _current.IsLooped = true;
            _current.Volume = 0.55f;
            _current.Play();
            _playing = style;
        }
        catch (Exception exception) when (exception is NoAudioHardwareException or InvalidOperationException or ArgumentException)
        {
            // A machine without a sound device is a supported configuration: the
            // game runs silent rather than refusing to start.
            IsUnavailable = true;
            _current?.Dispose();
            _current = null;
            _playing = null;
        }
    }

    /// <summary>Stops whatever is playing.</summary>
    public void Stop()
    {
        if (_current is null)
        {
            return;
        }

        _current.Stop();
        _current.Dispose();
        _current = null;
        _playing = null;
    }

    /// <summary>Mutes or unmutes, stopping playback while muted.</summary>
    public void ToggleMute()
    {
        IsMuted = !IsMuted;

        if (IsMuted)
        {
            Stop();
        }
    }

    public void Dispose()
    {
        Stop();

        foreach (SoundEffect effect in _effects.Values)
        {
            effect.Dispose();
        }

        _effects.Clear();
    }
}
