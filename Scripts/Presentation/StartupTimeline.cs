using System;

namespace DealOrForce.Presentation;

/// <summary>Presentation time only. Never advances a campaign or invents loading progress.</summary>
public sealed class StartupTimeline
{
    public enum Card { Studio, Game, Complete }
    public enum Phase { In, Hold, Waiting, Out }
    public const double FadeSeconds = .38;
    public const double HoldSeconds = 1.35;
    public Card Current { get; private set; } = Card.Studio;
    public Phase CurrentPhase { get; private set; } = Phase.In;
    public double Opacity { get; private set; }
    public bool SkipRequested { get; private set; }
    public bool Waiting => Current == Card.Game && CurrentPhase == Phase.Waiting;
    private double _time;
    private double _outFrom = 1;

    public void Skip()
    {
        if (Current != Card.Complete && CurrentPhase != Phase.Out) SkipRequested = true;
    }

    public void Tick(double seconds, bool resourcesReady)
    {
        if (!double.IsFinite(seconds) || seconds < 0 || Current == Card.Complete) return;
        // A stalled frame must not consume both logos without ever presenting them.
        var dt = Math.Min(seconds, .1);
        var mayLeave = Current == Card.Studio || resourcesReady;
        if (SkipRequested && mayLeave && CurrentPhase != Phase.Out) BeginOut();
        _time += dt;
        switch (CurrentPhase)
        {
            case Phase.In:
                Opacity = Smooth(_time / FadeSeconds);
                if (_time >= FadeSeconds) { CurrentPhase = Phase.Hold; _time = 0; Opacity = 1; }
                break;
            case Phase.Hold:
                if (_time >= HoldSeconds || SkipRequested)
                {
                    if (mayLeave) BeginOut();
                    else { CurrentPhase = Phase.Waiting; Opacity = 1; }
                }
                break;
            case Phase.Waiting:
                if (mayLeave) BeginOut();
                break;
            case Phase.Out:
                Opacity = _outFrom * (1 - Smooth(_time / FadeSeconds));
                if (_time >= FadeSeconds)
                {
                    Current = Current == Card.Studio ? Card.Game : Card.Complete;
                    CurrentPhase = Phase.In; _time = 0; Opacity = 0; SkipRequested = false;
                }
                break;
        }
    }

    private void BeginOut() { CurrentPhase = Phase.Out; _time = 0; _outFrom = Opacity; }
    private static double Smooth(double t) { t = Math.Clamp(t, 0, 1); return t * t * (3 - 2 * t); }
}
