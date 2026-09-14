using Microsoft.Xna.Framework.Audio;
using MiVic.Audio;
using MiVic.Core.Sim;

namespace MiVic.Game.Audio;

/// <summary>Which rung of the live ladder the battlefield is standing on.</summary>
public enum ScoreRung : byte
{
    /// <summary>Nothing is being said; the score rests.</summary>
    Calm = 0,

    /// <summary>The viewer's own structures are under fire.</summary>
    Alert = 1,

    /// <summary>The viewer's own units are firing, or being fired on.</summary>
    Combat = 2,
}

/// <summary>One frame's worth of what the game wants the music to say.</summary>
/// <param name="Outcome">The victory system's decision, when there is one.</param>
/// <param name="Rung">The live ladder's rung this frame.</param>
/// <param name="Pinned">
/// The leitmotiv a mission trigger pinned, or null. A pin is the mission's own decision and
/// outranks the ladder until the mission releases it or another trigger takes over.
/// </param>
public readonly record struct ScoreCue(GameOutcome Outcome, ScoreRung Rung, string? Pinned);

/// <summary>
/// Plays a faction's pattern score, switched by the game's own state.
/// <para>
/// <b>Cues are raised by the game, spent by the music.</b> The ladder — a mission trigger's
/// pin over the live rungs of combat, alert and calm, over the decided outcome that locks the
/// score — is read every frame, but the answer never arrives mid-bar: a change waits for the
/// current bar's edge, plays the fill the score names for that pair, and enters the next
/// leitmotiv on its first bar. The switches are scheduled on elapsed playback time rather than
/// by reading the sound card, which is close enough that a frame's error is smaller than a
/// note, and the arrangement stays the same every play.
/// </para>
/// <para>
/// The buffers are rendered once per faction on first ask and looped. Everything here is
/// presentation — the director keeps no state the simulation knows about, and a replay replays
/// the same cues because the cues come from the same commands.
/// </para>
/// </summary>
public sealed class ScoreDirector : IDisposable
{
    private readonly Dictionary<FactionStyle, Score> _scores = [];
    private readonly Dictionary<string, SoundEffect> _buffers = [];
    private readonly List<SoundEffectInstance> _retired = [];

    private SoundEffectInstance? _current;
    private SoundEffectInstance? _fill;
    private Score? _score;
    private FactionStyle? _style;

    private string? _currentLeitmotiv;
    private string? _pending;
    private string? _afterFill;
    private string? _locked;
    private string? _lastWanted;

    private ScoreRung _rung;
    private double _elapsed;
    private double _hold;
    private double _switchAt = double.PositiveInfinity;
    private double _fillEndsAt = double.PositiveInfinity;

    /// <summary>True when the soundtrack is muted.</summary>
    public bool IsMuted { get; private set; }

    /// <summary>Which faction's score is playing, if any.</summary>
    public FactionStyle? Playing => _style;

    /// <summary>The leitmotiv currently sounding, for diagnostics.</summary>
    public string? Leitmotiv => _currentLeitmotiv;

    /// <summary>True when no audio device was available and the soundtrack is off.</summary>
    public bool IsUnavailable { get; private set; }

    /// <summary>
    /// Starts a faction's score at its calm leitmotiv, rendering every leitmotiv and fill of
    /// the score on first ask. A score already playing is left alone.
    /// </summary>
    public void Play(FactionStyle style)
    {
        if (IsMuted || IsUnavailable || _style == style)
        {
            return;
        }

        Stop();

        try
        {
            if (!_scores.TryGetValue(style, out Score? score))
            {
                score = Score.Load(Score.PathFor(style.ToString().ToLowerInvariant()));
                _scores[style] = score;
            }

            _score = score;
            _style = style;

            // Every leitmotiv and every fill is a buffer: a switch is a start, not a render.
            foreach (Leitmotiv leitmotiv in score.Leitmotivs)
            {
                Render(leitmotiv.Name, Sequencer.RenderLeitmotiv(score, leitmotiv));
            }

            foreach (ScoreFill fill in score.Fills)
            {
                Render(fill.Name, Sequencer.RenderFill(score, fill));
            }

            StartLeitmotiv(score.Cues.Calm);
        }
        catch (Exception exception) when (exception is NoAudioHardwareException or InvalidOperationException or ArgumentException or ScoreException)
        {
            // A machine without a sound device is a supported configuration, and a score that
            // fails to render is a soundtrack the game runs without — a broken score should
            // not take the match down with it. The report the exporter writes is where the
            // refusal is read.
            IsUnavailable = true;
            _current?.Dispose();
            _current = null;
            _style = null;
        }
    }

    private void Render(string name, short[] pcm)
    {
        byte[] bytes = new byte[pcm.Length * sizeof(short)];
        Buffer.BlockCopy(pcm, 0, bytes, 0, bytes.Length);

        _buffers[$"{_style}:{name}"] = new SoundEffect(bytes, MiVic.Audio.SoundBank.SampleRate, AudioChannels.Mono);
    }

    /// <summary>
    /// One frame of the cue ladder and of the schedule it keeps. The cue is what the game says;
    /// the director decides what the music says, and says it on a bar boundary.
    /// </summary>
    public void Update(float deltaSeconds, ScoreCue cue)
    {
        if (_score is null || IsUnavailable || _current is null)
        {
            return;
        }

        _elapsed += deltaSeconds;

        // The outcome is the one cue that cannot be demoted: a decided battle's music is the
        // score's last word, and the ladder yields to it forever after.
        if (_locked is null && cue.Outcome is not GameOutcome.Ongoing)
        {
            _locked = cue.Outcome == GameOutcome.Victory ? _score.Cues.Victory : _score.Cues.Defeat;
            _hold = 0d;
            _pending = _locked;
            _switchAt = _currentLeitmotiv is null ? _elapsed : NextBarEdge(_currentLeitmotiv);
        }
        else if (_locked is null)
        {
            // The pin is the mission's own decision and outranks the rungs; the rungs answer
            // with the score's own names for what the battlefield is doing.
            string? wanted = cue.Pinned ?? cue.Rung switch
            {
                ScoreRung.Combat => _score.Cues.Combat,
                ScoreRung.Alert => _score.Cues.Alert,
                _ => _score.Cues.Calm,
            };

            if (wanted == _currentLeitmotiv)
            {
                _hold = 0d;
            }
            else if (wanted == _lastWanted)
            {
                // Hysteresis: the rung must hold its bars before the music follows it — a
                // skirmish that flares for a bar is not a battle, and a battle that stops for
                // a bar is not over. Which hold applies is the direction: a higher rung has to
                // earn the climb, a lower one has to prove the descent.
                _hold += deltaSeconds;

                int bars = HigherRung(cue.Rung, _rung) ? _score.Cues.UpBars : _score.Cues.DownBars;
                double required = bars * _score.SecondsPerBar;

                if (_hold >= required && _switchAt == double.PositiveInfinity)
                {
                    _pending = wanted;
                    _switchAt = NextBarEdge(_currentLeitmotiv!);
                }
            }

            _lastWanted = wanted;
            _rung = cue.Rung;
        }

        // The switch itself: at the bar's edge, the fill if one is authored for the pair, then
        // the next leitmotiv on its first bar. A pair with no fill switches directly.
        if (_pending is not null && _elapsed >= _switchAt)
        {
            StartTransition(_currentLeitmotiv!, _pending);
            _pending = null;
            _switchAt = double.PositiveInfinity;
        }

        if (_afterFill is not null && _elapsed >= _fillEndsAt)
        {
            StartLeitmotiv(_afterFill);
            _afterFill = null;
            _fillEndsAt = double.PositiveInfinity;
        }
    }

    /// <summary>True when the new rung stands above the one the music is on.</summary>
    private static bool HigherRung(ScoreRung wanted, ScoreRung current) => wanted > current;

    /// <summary>When the leitmotiv's next bar ends, in playback seconds from now.</summary>
    private double NextBarEdge(string leitmotiv)
    {
        Leitmotiv? definition = _score!.LeitmotivByName(leitmotiv);

        if (definition is null)
        {
            return _elapsed;
        }

        double barSeconds = _score.SecondsPerBar;
        double barsIn = Math.Floor(_elapsed / barSeconds);

        return (barsIn + 1d) * barSeconds;
    }

    /// <summary>Plays the pair's fill, then the leitmotiv it leads into.</summary>
    private void StartTransition(string from, string into)
    {
        ScoreFill? fill = _score!.Fill(FillName(from, into));

        if (fill is not null)
        {
            PlayBuffer(fill.Name, looped: false);
            _fillEndsAt = _elapsed + _score.SecondsPerBar;
            _afterFill = into;
        }
        else
        {
            StartLeitmotiv(into);
        }
    }

    /// <summary>The fill a pair is answered with: <c>to-&lt;leitmotiv&gt;</c> when the score has one.</summary>
    private static string FillName(string from, string into) => $"to-{into}";

    private void StartLeitmotiv(string name)
    {
        if (_score!.LeitmotivByName(name) is null)
        {
            return;
        }

        PlayBuffer(name, looped: true);
        _currentLeitmotiv = name;
        _elapsed = 0d;
        _hold = 0d;
        _lastWanted = null;
    }

    private void PlayBuffer(string name, bool looped)
    {
        if (!_buffers.TryGetValue($"{_style}:{name}", out SoundEffect? effect))
        {
            return;
        }

        SoundEffectInstance? previous = _current;

        if (previous is not null)
        {
            // Retired rather than disposed mid-play: stopping a buffer that is ringing into
            // the next one cuts the switch dead, and a retired instance is one frame of
            // overlap, not a pop.
            previous.Stop();
            _retired.Add(previous);
            _retired.RemoveAll(instance => !instance.IsDisposed);
        }

        _current = effect.CreateInstance();
        _current.IsLooped = looped;
        _current.Volume = 0.5f;
        _current.Play();
    }

    /// <summary>Stops whatever is playing.</summary>
    public void Stop()
    {
        _current?.Stop();
        _current?.Dispose();
        _current = null;
        _fill?.Stop();
        _fill?.Dispose();
        _fill = null;
        _style = null;
        _score = null;
        _currentLeitmotiv = null;
        _locked = null;
        _elapsed = 0d;
    }

    /// <summary>Where the score was when it was muted, so the unmute resumes it.</summary>
    private (FactionStyle Style, string Leitmotiv)? _mutedState;

    /// <summary>Mutes or unmutes, stopping playback while muted and resuming where it was.</summary>
    public void ToggleMute()
    {
        IsMuted = !IsMuted;

        if (IsMuted)
        {
            _mutedState = _style is null || _currentLeitmotiv is null
                ? null
                : (_style.Value, _currentLeitmotiv);
            Stop();
        }
        else if (_mutedState is { } resume)
        {
            _mutedState = null;
            Play(resume.Style);

            // The leitmotiv the score was in is where the mute left it, not where the calm
            // rung would start it again.
            if (_score is not null && _score.LeitmotivByName(resume.Leitmotiv) is not null)
            {
                StartLeitmotiv(resume.Leitmotiv);
            }
        }
    }

    public void Dispose()
    {
        Stop();

        foreach (SoundEffect effect in _buffers.Values)
        {
            effect.Dispose();
        }

        _buffers.Clear();

        foreach (SoundEffectInstance instance in _retired)
        {
            instance.Dispose();
        }

        _retired.Clear();
    }
}
