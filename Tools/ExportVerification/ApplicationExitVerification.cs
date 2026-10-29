using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyApplicationExit(MainMenuScene menu, string[] args)
    {
        var index = Array.IndexOf(args, "--exit-case");
        var mode = index >= 0 && index + 1 < args.Length ? args[index + 1] : "menu";
        var languageAt = Array.IndexOf(args, "--verify-language");
        if (languageAt >= 0 && languageAt + 1 < args.Length) menu.VerificationReleaseLanguage(args[languageAt + 1]);
        var music = GetTree().Root.GetNode<MusicDirector>("MusicDirector");
        var decks = music.GetChildren().OfType<AudioStreamPlayer>().ToArray();
        Check(decks.Length == 2 && decks.Any(deck => deck.Stream is not null && deck.Playing),
            "quit regression starts with real active audio, not a silent fixture");
        Check(!GetTree().AutoAcceptQuit && !ApplicationExit.Requested,
            "desktop close is owned by the production shutdown path");
        if (mode == "crossfade")
        {
            // The menu may still be fading in its first track. Wait for that
            // real envelope; never reset it or reuse a busy deck just for a test.
            var deadline = Time.GetTicksMsec() + 15_000;
            while (music.VerificationExitMusicIsChanging && Time.GetTicksMsec() < deadline) await Wait(.1);
            Check(!music.VerificationExitMusicIsChanging, "initial music envelope finishes within the probe budget");
            Check(music.VerificationStartExitCrossfade(), "second catalogue track starts a real crossfade");
            Check(decks.Count(deck => deck.Playing) == 2, "both music decks play before crossfade exit");
        }
        if (mode == "raw")
        {
            // Deliberately bypass the product only for reproducing the old engine
            // teardown race. A nonzero cleanup log makes this a red run, not a pass.
            GD.Print("EXIT_PROBE raw SceneTree.Quit baseline");
            GetTree().Quit(_failures == 0 ? 0 : 1);
            return;
        }
        var expectedCode = mode == "repeat" ? 7 : 0;
        if (mode == "paused") GetTree().Paused = true;
        if (mode == "window") GetTree().Root.EmitSignal(Window.SignalName.CloseRequested);
        else if (mode == "repeat")
        {
            ApplicationExit.Request(this);
            ApplicationExit.Request(this, 7);
            ApplicationExit.Request(this);
        }
        else
        {
            var button = menu.FindChildren("*", "Button", true, false).OfType<Button>()
                .FirstOrDefault(item => item.Text == Loc.Get("Quit to desktop"));
            Check(button is not null, "real localized desktop quit button exists");
            button?.EmitSignal(BaseButton.SignalName.Pressed);
        }
        Check(ApplicationExit.Requested && ApplicationExit.ExitCode == expectedCode,
            "request uses one production shutdown and preserves the exit code");
        Check(decks.All(deck => !deck.Playing && deck.Stream is null),
            "shutdown synchronously stops and releases both audio decks");
        music.FollowScene(menu, () => MusicCatalog.Moment.Menu);
        music._Process(10);
        Check(decks.All(deck => !deck.Playing && deck.Stream is null),
            "late scene follow and frame cannot restart audio during shutdown");
        GD.Print($"EXPORT_VERIFY failures={_failures}");
        GD.Print($"EXIT_PROBE case={mode} expected_code={expectedCode}");
        if (_failures > 0) ApplicationExit.Request(this, 1);
        // Do not call cleanup, GC, remove the director, or quit here. The actual
        // button/window path must finish the process; the runner has a deadline.
        await Task.CompletedTask;
    }

    private void VerifyCampaignApplicationExit(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id);
        world.IsPaused = true;
        game.VerificationSelectPlayerCountry();
        Check(DealOrForceGame.VerificationRetireFlagWarmups(),
            "cache invalidation retains every owned flag request, including duplicate paths");
        if (OS.GetCmdlineUserArgs().Contains("--verify-exit-gc-diagnostic"))
        {
            // Diagnostic comparison only: distinguish managed wrapper finalization
            // from nodes/resources that genuinely remain owned by the game.
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
            GD.Print("EXIT_PROBE managed wrapper diagnostic completed");
            TraceExitWrappers();
        }
        var button = game.VerificationPauseQuitButton();
        Check(button is not null && button.IsVisibleInTree(), "campaign pause menu exposes the real desktop quit button");
        GD.Print("EXIT_PROBE pending_flag_warmups=" + DealOrForceGame.PendingFlagWarmups);
        var stamp = CampaignFingerprint.Of(world);
        button?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(ApplicationExit.Requested, "campaign desktop action uses the production shutdown");
        Check(stamp == CampaignFingerprint.Of(world), "shutdown does not alter campaign data, commands or RNG");
        GD.Print($"EXPORT_VERIFY failures={_failures}");
        GD.Print("EXIT_PROBE case=campaign expected_code=0");
        if (_failures > 0) ApplicationExit.Request(this, 1);
    }

    private static void TraceExitWrappers()
    {
        // Read-only development diagnostic, never a production dependency or a
        // request to free another owner's objects. Correlate live managed IDs
        // with the engine's verbose exit report, rather than guessing ownership.
        var tracker = typeof(GodotObject).Assembly.GetType("Godot.DisposablesTracker");
        var field = tracker?.GetField("<GodotObjectInstances>k__BackingField",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic);
        if (field?.GetValue(null) is not System.Collections.Concurrent.ConcurrentDictionary<WeakReference<GodotObject>, byte> instances)
        {
            GD.Print("EXIT_WRAPPER tracker unavailable on this engine");
            return;
        }
        var counts = new System.Collections.Generic.Dictionary<string, int>();
        foreach (var reference in instances.Keys)
        {
            if (!reference.TryGetTarget(out var target) || !IsInstanceValid(target) || target is not RefCounted counted) continue;
            var kind = target.GetClass() + "/" + target.GetType().FullName;
            counts[kind] = (counts.TryGetValue(kind, out var seen) ? seen : 0) + 1;
            if (target.GetClass() == "RefCounted")
                GD.Print($"EXIT_WRAPPER id={target.GetInstanceId()} references={counted.GetReferenceCount()} type={kind}");
        }
        foreach (var item in counts.OrderBy(item => item.Key))
            GD.Print($"EXIT_WRAPPER count={item.Value} type={item.Key}");
    }
}

public partial class MusicDirector
{
    internal bool VerificationExitMusicIsChanging => _transition.IsChanging;
    internal bool VerificationStartExitCrossfade()
    {
        if (_transition.IsChanging) return false;
        var track = MusicCatalog.For(MusicCatalog.Moment.Menu).FirstOrDefault(item => item.Name != _playing);
        if (string.IsNullOrEmpty(track.Name) || GD.Load<AudioStream>(track.File) is not { } stream) return false;
        Crossfade(track, stream);
        return true;
    }
}

public partial class DealOrForceGame
{
    internal static bool VerificationRetireFlagWarmups()
    {
        const string path = "res://game/gfx/flags/flag_pol.png";
        if (!ResourceLoader.Exists(path)) return false;
        var accepted = 0;
        for (var i = 0; i < 2; i++)
            if (ResourceLoader.LoadThreadedRequest(path) == Error.Ok)
            {
                FlagsLoading["__exit_verification_" + i] = path;
                accepted++;
            }
        var total = PendingFlagWarmups;
        ForgetFlags();
        return accepted == 2 && FlagsLoading.Count == 0 && RetiredFlagLoads.Count == total;
    }

    internal Button VerificationPauseQuitButton()
    {
        ShowPauseMenu();
        return _pauseMenuLayer.FindChildren("*", "Button", true, false).OfType<Button>()
            .FirstOrDefault(button => button.Text == Loc.Get("Quit to desktop"));
    }
}
