using System;

namespace DealOrForce.Simulation;

public enum MusicTransitionCompletion { None, Track, Silence }

/// <summary>
/// Presentation-only two-deck envelope. A requested selection waits for the current
/// envelope, then the caller resolves its latest playlist, not a captured old context.
/// No asynchronous engine callbacks or campaign random state are retained here.
/// </summary>
public sealed class MusicPlaybackTransition
{
    private enum Selection { None, Context, Advance }
    private readonly float[] _mix = new float[2];
    private readonly float[] _start = new float[2];
    private Selection _pending;
    private MusicTransitionCompletion _kind;
    private double _elapsed, _duration;

    public int ActiveDeck { get; private set; }
    public bool IsChanging => _kind != MusicTransitionCompletion.None;
    public bool SelectionPending => _pending != Selection.None;
    public float Mix(int deck) => _mix[deck];

    /// <summary>Advance/reload requests survive coalescing; context is evaluated when taken.</summary>
    public void RequestSelection(bool advance)
    {
        if (advance || _pending == Selection.None)
            _pending = advance ? Selection.Advance : Selection.Context;
    }

    public bool TryTakeSelection(bool currentTrackCompatible)
    {
        if (IsChanging || _pending == Selection.None) return false;
        var request = _pending; _pending = Selection.None;
        return request == Selection.Advance || !currentTrackCompatible;
    }

    /// <summary>Only reuse the silent deck. Replacing one during a fade cuts audible music.</summary>
    public int BeginTrack(double seconds, bool currentIsPlaying)
    {
        Begin(MusicTransitionCompletion.Track, seconds);
        var old = ActiveDeck;
        _mix[old] = _start[old] = currentIsPlaying ? _mix[old] : 0;
        ActiveDeck = 1 - old;
        _mix[ActiveDeck] = _start[ActiveDeck] = 0;
        return ActiveDeck;
    }

    public void BeginSilence(double seconds) => Begin(MusicTransitionCompletion.Silence, seconds);

    private void Begin(MusicTransitionCompletion kind, double seconds)
    {
        if (IsChanging) throw new InvalidOperationException("A music envelope must finish before a deck is reused.");
        _kind = kind; _elapsed = 0;
        _duration = double.IsFinite(seconds) && seconds > 0 ? seconds : .1;
        _start[0] = _mix[0]; _start[1] = _mix[1];
    }

    public MusicTransitionCompletion Advance(double seconds)
    {
        if (!IsChanging || !double.IsFinite(seconds) || seconds <= 0) return MusicTransitionCompletion.None;
        _elapsed = Math.Min(_duration, _elapsed + seconds);
        var progress = (float)(_elapsed / _duration);
        if (_kind == MusicTransitionCompletion.Track)
        {
            _mix[1 - ActiveDeck] = _start[1 - ActiveDeck] * MathF.Sqrt(1 - progress);
            _mix[ActiveDeck] = MathF.Sqrt(progress);
        }
        else
        {
            _mix[0] = _start[0] * (1 - progress);
            _mix[1] = _start[1] * (1 - progress);
        }
        if (_elapsed < _duration) return MusicTransitionCompletion.None;
        var completed = _kind; _kind = MusicTransitionCompletion.None;
        return completed;
    }

    /// <summary>Muting/auditioning only changes gain, never the envelope or its pending context.</summary>
    public float Gain(int deck, float master, float music, bool suppressed) =>
        suppressed ? 0 : Math.Clamp(master * music * _mix[deck], 0, 1);

    public void Reset()
    {
        _pending = Selection.None; _kind = MusicTransitionCompletion.None;
        _elapsed = _duration = 0; ActiveDeck = 0;
        _mix[0] = _mix[1] = _start[0] = _start[1] = 0;
    }
}
