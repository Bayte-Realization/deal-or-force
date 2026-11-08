using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyNuclearAlertLayout(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        game.Call("CloseSidePanel"); await Wait(.5);
        foreach (var language in new[] { "english", "russian" })
        foreach (var sample in new (int Width, int Height, float Scale)[] { (1920, 1080, 1f), (1280, 720, 1f), (1280, 720, 1.75f) })
        {
            Loc.Language = language; GetTree().Root.Size = new(sample.Width, sample.Height);
            SettingsCatalog.Store("interface_scale", sample.Scale); game.Call("ApplySetting", "interface_scale");
            game.Call("RefreshLocalisedText"); await Wait(.3);
            var before = CampaignFingerprint.Of(c);
            var stamp = language + "-" + sample.Width + "-" + sample.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
            game.VerificationShowNuclearDispatch(true); await Wait(.8);
            var card = game.FindChild("NuclearAlert", true, false) as Control;
            var instance = card.GetInstanceId();
            CheckFits(card, "bounded long nuclear dispatch " + stamp);
            CheckFits(game.FindChild("NuclearAlertDismiss", true, false) as Control, "pinned nuclear dismiss " + stamp);
            var scroll = game.FindChild("NuclearAlertCopyScroll", true, false) as ScrollContainer;
            Check(scroll.GetVScrollBar().MaxValue > scroll.Size.Y + 10, "long copy scrolls instead of stretching the alert " + stamp);
            await Capture("nuclear-alert-long-" + stamp);
            scroll.ScrollVertical = (int)scroll.GetVScrollBar().MaxValue;
            await Wait(.1);
            Check(scroll.ScrollVertical > 0, "long dispatch was actually scrolled before replacement " + stamp);

            game.VerificationShowNuclearDispatch(false); await Wait(.8);
            Check(card.GetInstanceId() == instance, "a second message reuses the same alert " + stamp);
            CheckFits(card, "short nuclear dispatch after long copy " + stamp);
            var natural = game.VerificationNuclearDispatchNaturalHeight();
            Check(Math.Abs(card.Size.Y - natural) < 12, "short dispatch shrinks to content, with no retained blank height " + stamp);
            Check(scroll.ScrollVertical == 0, "new dispatch resets the previous scroll position " + stamp);
            await Capture("nuclear-alert-short-" + stamp);
            (game.FindChild("NuclearAlertDismiss", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            Check(!card.Visible, "real dismiss action hides the dispatch " + stamp);
            Check(CampaignFingerprint.Of(c) == before, "nuclear notification layout never mutates campaign state " + stamp);
        }
        game.VerificationShowNuclearDispatch(false); game.VerificationAgeNuclearDispatch(16);
        Check((game.FindChild("NuclearAlert", true, false) as Control) is { Visible: false },
            "notification lifetime remains fifteen real seconds, not simulation days");
        SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale");
        GetTree().Root.Size = new(1920, 1080); Loc.Language = "english";
        Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "dispatch probe returns to the real menu");
        await Wait(2);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationShowNuclearDispatch(bool longCopy)
    {
        var actor = _campaign.NationByTag("GBR"); var target = _campaign.NationByTag("SYR");
        ShowNuclearAlert(new ChronicleEntry(0, _campaign.ElapsedDays, _campaign.Date, "nuclear_strike",
            actor.Id, target.Id, actor.DisplayName, target.DisplayName, "", "As-Suwayda", true));
        if (longCopy) _nuclearAlertText.Text = string.Join("\n", Enumerable.Repeat(_nuclearAlertText.Text, 14));
        _nuclearAlertReflowFrames = 4;
    }
    internal void VerificationAgeNuclearDispatch(double seconds) => TickNuclearAlert(seconds);
    internal float VerificationNuclearDispatchNaturalHeight() => _nuclearAlertPicture.CustomMinimumSize.Y +
        32 + _nuclearAlertMast.GetCombinedMinimumSize().Y + _nuclearAlertCopy.GetCombinedMinimumSize().Y +
        _nuclearAlert.GetThemeStylebox("panel").GetMinimumSize().Y;
}
