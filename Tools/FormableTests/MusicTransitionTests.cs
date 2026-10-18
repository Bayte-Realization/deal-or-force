using System;
using DealOrForce.Simulation;

static class MusicTransitionTests
{
    private static int _checks;
    private static void Check(bool ok, string message)
    {
        if (!ok) throw new Exception("music transition: " + message);
        _checks++; Console.WriteLine("PASS music transition: " + message);
    }
    private static bool Near(float a, float b) => MathF.Abs(a - b) < .00001f;
    private static float Power(MusicPlaybackTransition mix) =>
        mix.Mix(0) * mix.Mix(0) + mix.Mix(1) * mix.Mix(1);

    public static void Run()
    {
        _checks = 0;
        var mix = new MusicPlaybackTransition();
        Check(!mix.IsChanging && !mix.SelectionPending && mix.Mix(0) == 0 && mix.Mix(1) == 0,
            "unstarted director is silent");
        mix.RequestSelection(advance: false);
        Check(mix.TryTakeSelection(currentTrackCompatible: false) && !mix.SelectionPending,
            "empty context takes its first selection once");
        var first = mix.BeginTrack(7, currentIsPlaying: false);
        Check(first == 1 && mix.IsChanging && mix.Mix(first) == 0, "first stream begins on the other silent deck");
        Check(mix.Advance(3.5) == MusicTransitionCompletion.None && Near(mix.Mix(first), MathF.Sqrt(.5f)) && mix.Mix(0) == 0,
            "initial fade preserves the existing square-root envelope");
        Check(mix.Advance(3.5) == MusicTransitionCompletion.Track && !mix.IsChanging && mix.Mix(first) == 1,
            "first envelope finishes exactly once");
        Check(mix.Advance(1) == MusicTransitionCompletion.None, "completed envelope cannot emit a stale second completion");

        mix.RequestSelection(advance: true);
        Check(mix.TryTakeSelection(currentTrackCompatible: true), "natural end advances even when the old track remains compatible");
        var second = mix.BeginTrack(7, currentIsPlaying: true);
        mix.Advance(.7);
        var loud = mix.Mix(first); var incoming = mix.Mix(second);
        Check(Near(loud, MathF.Sqrt(.9f)) && Near(incoming, MathF.Sqrt(.1f)) && Near(Power(mix), 1),
            "normal crossfade keeps equal power at ten percent");

        // Previously Crossfade killed its tween and stopped 1-_active here. That was
        // the loud outgoing deck, reducing total squared gain from 1 to .1 in one call.
        var oldRestartPower = incoming * incoming;
        Check(oldRestartPower < .11f && Power(mix) > .99f, "fixture reaches the real loud-outgoing interruption case");
        mix.RequestSelection(advance: false);
        Check(!mix.TryTakeSelection(currentTrackCompatible: false) && mix.SelectionPending,
            "context change cannot reuse an audible deck mid-fade");
        Check(Near(mix.Mix(first), loud) && Near(mix.Mix(second), incoming),
            "queued request does not cut the loud deck or change either gain");
        for (var i = 0; i < 100; i++) mix.RequestSelection(advance: false);
        Check(mix.Advance(6.3) == MusicTransitionCompletion.Track && !mix.IsChanging,
            "rapid contexts do not restart the clock or postpone transition beyond its seven seconds");
        Check(!mix.TryTakeSelection(currentTrackCompatible: true) && !mix.SelectionPending,
            "latest compatible context cancels obsolete intermediate regions without restarting audio");

        mix.RequestSelection(advance: false);
        Check(mix.TryTakeSelection(currentTrackCompatible: false), "latest incompatible context takes a fresh selection");
        var third = mix.BeginTrack(7, currentIsPlaying: true);
        mix.Advance(2);
        mix.RequestSelection(advance: true); // A catalog reload cannot be cancelled by a later compatible region.
        mix.RequestSelection(advance: false);
        Check(!mix.TryTakeSelection(currentTrackCompatible: true), "catalog refresh waits for the envelope rather than cutting it");
        mix.Advance(5);
        Check(mix.TryTakeSelection(currentTrackCompatible: true) && !mix.SelectionPending,
            "queued catalog reload survives later context-only requests");

        var snapshot = mix.Mix(third);
        Check(mix.Gain(third, 1, 1, suppressed: true) == 0 && mix.Gain(third, 0, 1, suppressed: false) == 0,
            "manual audition and master mute suppress immediately");
        Check(mix.Gain(third, 1, 0, suppressed: false) == 0 && Near(mix.Gain(third, .5f, .4f, suppressed: false), .2f),
            "music mute and both volume sliders retain multiplicative gain");
        Check(Near(mix.Mix(third), snapshot) && mix.Gain(third, 1, 1, suppressed: false) == snapshot,
            "audition release restores current envelope without restarting the recording");

        mix.BeginSilence(7);
        Check(mix.Advance(3.5) == MusicTransitionCompletion.None && Near(mix.Mix(third), .5f),
            "empty playlist uses the existing linear silence envelope");
        mix.RequestSelection(advance: false);
        Check(!mix.TryTakeSelection(currentTrackCompatible: false), "new playlist waits for pending silence to complete");
        Check(mix.Advance(3.5) == MusicTransitionCompletion.Silence && mix.Mix(0) == 0 && mix.Mix(1) == 0,
            "silence completion identifies that both engine players must stop");
        Check(mix.TryTakeSelection(currentTrackCompatible: false), "latest playlist can resume immediately after silence");
        mix.BeginTrack(7, currentIsPlaying: false); mix.Advance(1);
        mix.RequestSelection(advance: true); mix.Reset();
        Check(!mix.IsChanging && !mix.SelectionPending && mix.Mix(0) == 0 && mix.Mix(1) == 0,
            "scene release clears gains, pending request and running envelope");
        Check(mix.Advance(100) == MusicTransitionCompletion.None && !mix.TryTakeSelection(false),
            "released director cannot receive a stale completion or selection");

        mix.BeginTrack(7, currentIsPlaying: false);
        Check(mix.Advance(double.NaN) == MusicTransitionCompletion.None && mix.Advance(-1) == MusicTransitionCompletion.None &&
            mix.Advance(double.PositiveInfinity) == MusicTransitionCompletion.None && mix.Mix(mix.ActiveDeck) == 0,
            "invalid frame durations do not poison presentation gain");
        mix.Advance(7);
        mix.BeginTrack(7, currentIsPlaying: false); mix.Advance(3.5);
        Check(mix.Mix(1 - mix.ActiveDeck) == 0, "finished outgoing voice is not revived by its stored gain");
        var rejected = false;
        try { mix.BeginSilence(7); } catch (InvalidOperationException) { rejected = true; }
        Check(rejected, "invalid direct interruption is rejected instead of silently stopping a loud deck");
        Focus();
        Console.WriteLine($"Music transition checks: {_checks}");
    }

    private static void Focus()
    {
        var focus = new AudioFocusEnvelope();
        Check(focus.Gain == 1, "cue focus leaves an idle soundtrack unchanged");
        focus.Request(2, .5f);
        Check(focus.Gain == 1, "cue focus attacks smoothly rather than stepping the recording's level");
        focus.Advance(.3);
        Check(focus.Gain > .5f && focus.Gain < .51f, "important cue makes room within a fraction of a second");
        focus.Request(.5, .8f);
        focus.Advance(.2);
        Check(focus.Gain < .51f, "a weaker overlapping report cannot pump the music back up");
        focus.Advance(1.5);
        var low = focus.Gain;
        focus.Advance(.75);
        Check(focus.Gain > low && focus.Gain < 1, "quiet returns gradually after the actual report ends");
        focus.Advance(30);
        Check(focus.Gain == 1, "finished focus fully releases with no stale callbacks");
        var longFrame = new AudioFocusEnvelope();
        var shortFrames = new AudioFocusEnvelope();
        longFrame.Request(2, .4f); shortFrames.Request(2, .4f);
        longFrame.Advance(3);
        for (var i = 0; i < 300; i++) shortFrames.Advance(.01);
        Check(Near(longFrame.Gain, shortFrames.Gain), "focus timing is independent of the render frame rate");
        focus.Request(double.NaN, .2f); focus.Request(2, float.NaN);
        focus.Advance(double.NaN); focus.Advance(-1);
        Check(focus.Gain == 1, "invalid times and levels cannot poison or silence the mix");
        focus.Request(10000, -1); focus.Advance(100);
        Check(focus.Gain == 1, "even a malformed long cue cannot hold ducking indefinitely");
        focus.Request(2, .1f); focus.Advance(.1); focus.Reset();
        Check(focus.Gain == 1, "changing scene or resetting the campaign clears old cue focus");
        var channel = new SoundCatalog.Channel("test", 0, 1, false, 8, -100);
        Check(channel.MusicDuckDb == 0 && channel.AmbienceDuckDb == -24,
            "modded duck levels cannot amplify the score or exceed the attenuation limit");
        var errors = new System.Collections.Generic.List<string>();
        try
        {
            SoundCatalog.Merge("{\"channels\":{\"focus_test\":{\"music_duck_db\":-5,\"ambience_duck_db\":-9}}}",
                "res://test", "focus", errors);
            SoundCatalog.Merge("{\"channels\":{\"focus_test\":-12}}", "res://test", "focus", errors);
            var patched = SoundCatalog.GetChannel("focus_test");
            Check(errors.Count == 0 && patched.MusicDuckDb == -5 && patched.AmbienceDuckDb == -9 && patched.VolumeDb == -12,
                "a volume-only mod overlay preserves inherited focus rules");
            SoundCatalog.Merge("{\"channels\":{\"focus_test\":{\"music_duck_db\":0,\"ambience_duck_db\":0}}}",
                "res://test", "focus", errors);
            Check(SoundCatalog.GetChannel("focus_test").MusicDuckDb == 0 &&
                SoundCatalog.GetChannel("focus_test").AmbienceDuckDb == 0, "mods can opt a channel out of ducking");
        }
        finally { SoundCatalog.Load(); }
    }
}
