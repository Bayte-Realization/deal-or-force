using System;
using System.Threading.Tasks;
using DealOrForce.Presentation;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyBranding(MainMenuScene menu)
    {
        Check(menu.FindChild("StartupIntro", true, false) is null, "ordinary probes do not replay an intro");
        Check(BrandAssets.Studio() is { } && BrandAssets.GamePlaque() is { }, "both canonical brand assets load");
        var keys = new[] { Key.Escape, Key.Space, Key.Enter, Key.KpEnter };
        var pass = 0;
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language; menu.VerificationReleaseLanguage(language);
            GetTree().Root.Size = size; await Wait(.2);
            var intro = new StartupIntro { Name = "BrandingProbe", VerificationBlockWarmup = true };
            menu.AddChild(intro);
            Check(await IntroUntil(() => intro.Timeline.CurrentPhase == StartupTimeline.Phase.Hold), "studio reaches full-opacity hold");
            Check(!intro.WarmupStarted, "resource preparation waits for the game card");
            await Capture("brand-studio-"+language+"-"+size.X);
            SendKey(keys[pass++]);
            Check(await IntroUntil(() => intro.Timeline.Current == StartupTimeline.Card.Game), "keyboard skips studio to game, not through both");
            Check(await IntroUntil(() => intro.Timeline.Opacity == 1), "game card fades to full opacity");
            Check(intro.WarmupStarted, "campaign scene / loading art preparation starts under game logo");
            await Capture("brand-game-"+language+"-"+size.X);
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex=MouseButton.Left,Pressed=true,Position=new Vector2(20,20) });
            Input.ParseInputEvent(new InputEventMouseButton { ButtonIndex=MouseButton.Left,Pressed=false,Position=new Vector2(20,20) });
            await Wait(.5);
            Check(intro.Timeline.Waiting && intro.Timeline.SkipRequested, "mouse skip cannot bypass pending preparation");
            Check(menu.FindChild("StartupPreparation",true,false) is Label { Visible:true } label && label.Text == Loc.Get("Skip requested. Finishing resource preparation…"), "waiting explains that the skip was accepted");
            intro.VerificationBlockWarmup = false;
            Check(await IntroUntil(() => intro.Finished, 12), "preparation completion releases the queued skip");
            Check(intro.ResourcesReady, "no requested warmup remains when menu is revealed");
            Check(MainMenuScene.Wanted == MenuIntent.None && SettingsCatalog.CurrentCampaign is null, "intro inputs never start or mutate a campaign");
            await Wait(.1);
            await Capture("brand-menu-"+language+"-"+size.X);
            var loading = new LoadingScreen { Name="BrandingLoadingProbe" };
            menu.AddChild(loading); await Wait(.3);
            Check(loading.FindChild("LoadingGameLogo",true,false) is TextureRect picture && picture.Texture == BrandAssets.GamePlaque(), "campaign loading uses the same updated game plaque");
            Check(HasLabelText(loading, Loc.Get("Preparing the world")) && HasLabelText(loading, Loc.Get("Opening the archive")), "initial loading text is localized before the first stage update");
            await Capture("brand-loading-"+language+"-"+size.X);
            loading.QueueFree(); await Wait(.1);
        }
        // The controller's normal, non-skipped path must also finish and reveal the menu.
        var automatic = new StartupIntro { Name="AutomaticBrandingProbe" };menu.AddChild(automatic);
        Check(await IntroUntil(() => automatic.Finished,12), "unskipped studio/game sequence finishes automatically");
        Check(MainMenuScene.Wanted == MenuIntent.None, "neither startup path leaked input into a menu action");
    }

    private static bool HasLabelText(Node node, string text)
    {
        if (node is Label label && label.Text == text) return true;
        foreach (var child in node.GetChildren()) if (HasLabelText(child, text)) return true;
        return false;
    }

    private async Task<bool> IntroUntil(Func<bool> condition, double seconds=5)
    {
        var end=Time.GetTicksMsec()+(ulong)(seconds*1000);
        while(!condition() && Time.GetTicksMsec()<end)await Wait(.04);
        return condition();
    }
    private static void SendKey(Key code)
    {
        Input.ParseInputEvent(new InputEventKey { Keycode=code,Pressed=true });
        Input.ParseInputEvent(new InputEventKey { Keycode=code,Pressed=false });
    }
}
