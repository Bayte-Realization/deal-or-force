using System;

namespace DealOrForce.Simulation;

/// <summary>Presentation-only gain for an audible cue, never campaign time or RNG.
/// Overlapping reports coalesce instead of scheduling stale restore callbacks.</summary>
public sealed class AudioFocusEnvelope
{
    private double _hold;
    private float _target = 1;
    public float Gain { get; private set; } = 1;

    public void Request(double seconds, float gain)
    {
        if (!double.IsFinite(seconds) || seconds <= 0 || !float.IsFinite(gain) || gain >= 1) return;
        _hold = Math.Max(_hold, Math.Min(seconds, 30));
        _target = Math.Min(_target, Math.Clamp(gain, 0, 1));
    }

    public void Advance(double seconds)
    {
        if (!double.IsFinite(seconds) || seconds <= 0) return;
        var held = Math.Min(seconds, _hold);
        Gain = Approach(Gain, _target, held, .06);
        _hold -= held;
        if (_hold > 0) return;
        _target = 1;
        Gain = Approach(Gain, 1, seconds - held, 1.5);
        if (Gain > .9999f) Gain = 1;
    }

    private static float Approach(float from, float to, double seconds, double timeConstant) =>
        to + (from - to) * (float)Math.Exp(-seconds / timeConstant);

    public void Reset() { _hold = 0; _target = Gain = 1; }
}
