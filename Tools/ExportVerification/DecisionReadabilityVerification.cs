using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyDecisionDocuments(DealOrForceGame game)
    {
        var world = game.VerificationWorld; world.IsPaused = true;
        world.ConfigureSinglePlayer(world.NationByTag("KOR").Id);
        game.VerificationSelectPlayerCountry(); game.Call("CloseSidePanel");
        var target = world.NationByTag("RUS");
        var ally = world.NationByTag("USA");
        var relation = CampaignDiplomacy.Get(world, world.Player.Id, ally.Id);
        relation.Stance = DiplomaticStance.Allied; relation.Opinion = 100;
        await Wait(1); // Finish the first HUD/visibility refresh before measuring read-only UI.
        var originalScale = SettingsCatalog.Number("interface_scale");
        foreach (var language in new[] { "english", "russian" })
        foreach (var sample in new (int Width, int Height, float Scale)[] { (1920, 1080, 1f), (1280, 720, 1f), (1280, 720, 1.25f) })
        {
            Loc.Language = language; GetTree().Root.Size = new(sample.Width, sample.Height);
            SettingsCatalog.Store("interface_scale", sample.Scale); game.Call("ApplySetting", "interface_scale");
            game.Call("RefreshLocalisedText"); await Wait(.3);
            var opener = game.FindChild("OpenMediaLibrary", true, false) as Button;
            foreach (var kind in new[] { "War", "Deal", "Ultimatum" })
            {
                opener?.GrabFocus();
                var before = CampaignFingerprint.Of(world);
                game.VerificationOpenDecisionDocument(kind, target.Id); await Wait(.45);
                var rootName = kind == "War" ? "WarDeclarationReview" : kind + "Composer";
                var panelName = kind == "War" ? "WarDeclarationPanel" : kind + "Panel";
                var dialog = game.FindChild(rootName, true, false) as Control;
                var panel = game.FindChild(panelName, true, false) as Control;
                var stamp = kind + "-" + language + "-" + sample.Width + "-" + sample.Scale.ToString("0.##", System.Globalization.CultureInfo.InvariantCulture);
                CheckFits(panel, "readable " + stamp);
                var text = panel.FindChildren("*", "Control", true, false).OfType<Control>()
                    .Where(c => c is Label or Button or LineEdit).ToArray();
                var actualPixelsPerUnit = GetWindow().Size.Y / GetViewport().GetVisibleRect().Size.Y;
                float Pixels(Control c) => (c is Label { LabelSettings: not null } label ? label.LabelSettings.FontSize
                    : c.GetThemeFontSize("font_size")) * actualPixelsPerUnit * Math.Abs(c.GetGlobalTransformWithCanvas().Scale.Y);
                var minimum = text.Min(Pixels);
                GD.Print($"DECISION_METRICS {stamp} min-font-pixels={minimum:F2} canvas={GetTree().Root.GetVisibleRect().Size} rect={panel.GetGlobalRect()} minimum={panel.GetCombinedMinimumSize()} wars={world.Wars.Count}");
                Check(minimum >= 14 * sample.Scale - .1f, "all paragraphs and controls keep their physical font floor " + stamp);
                var send = game.FindChild(kind == "War" ? "ConfirmWarDeclaration" : "SendComposed" + kind, true, false) as Button;
                CheckFits(send, "pinned primary action " + stamp);
                Check(send.GetGlobalRect().End.Y <= panel.GetGlobalRect().End.Y &&
                    send.Size.Y * actualPixelsPerUnit * Math.Abs(send.GetGlobalTransformWithCanvas().Scale.Y) >= 32 * sample.Scale - .1f,
                    "primary action has a physical hit target inside its document " + stamp);
                Check(CampaignFingerprint.Of(world) == before, "opening the document does not alter intelligence or diplomatic state " + stamp);
                await Capture("decision-" + stamp);
                // All controls are legitimate real UI controls, not a synthetic picture. Tab
                // must remain in the current modal even after wrapping in either direction.
                var stopCount = dialog.FindChildren("*", "Control", true, false).OfType<Control>()
                    .Count(c => c.FocusMode == Control.FocusModeEnum.All);
                for (var tab = 0; tab < stopCount + 2; tab++)
                    game._Input(new InputEventKey { Pressed = true, Keycode = Key.Tab });
                Check(dialog.IsAncestorOf(GetViewport().GuiGetFocusOwner()), "Tab wraps inside the active document " + stamp);
                for (var tab = 0; tab < stopCount + 2; tab++)
                    game._Input(new InputEventKey { Pressed = true, Keycode = Key.Tab, ShiftPressed = true });
                Check(dialog.IsAncestorOf(GetViewport().GuiGetFocusOwner()), "Shift+Tab wraps inside the active document " + stamp);
                if (kind == "War")
                {
                    send.GrabFocus();
                    Input.ParseInputEvent(new InputEventKey { Pressed = true, Keycode = Key.Enter }); await Wait(.1);
                    Input.ParseInputEvent(new InputEventKey { Pressed = false, Keycode = Key.Enter }); await Wait(.1);
                    Check(CampaignFingerprint.Of(world) == before, "Enter on focused confirmation never declares war " + stamp);
                }
                await Wait(.15);
                game._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape }); await Wait(.1);
                Check(game.FindChild(rootName, true, false) is null && CampaignFingerprint.Of(world) == before,
                    "reading, scrolling and cancelling leave the campaign unchanged " + stamp);
                Check(GetViewport().GuiGetFocusOwner() == opener, "cancel restores the actual previous focus " + stamp);
            }
        }
        // Reflow an existing draft, not merely a newly constructed window of another size.
        game.VerificationOpenDecisionDocument("Ultimatum", target.Id); await Wait(.3);
        var draftFingerprint = CampaignFingerprint.Of(world);
        var picked = game.FindChildren("UltimatumRegion_*", "CheckBox", true, false).OfType<CheckBox>()
            .Where(b => !b.Disabled).Take(4).ToArray();
        foreach (var choice in picked) choice.ButtonPressed = true;
        var money = game.FindChild("UltimatumReparations", true, false) as SpinBox;
        money.Value = Math.Min(.05, money.MaxValue);
        var search = game.FindChild("UltimatumRegionSearch", true, false) as LineEdit;
        search.Text = "__filter__"; search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text);
        foreach (var scale in new[] { 1.75f, 1f, 1.25f })
        {
            GetTree().Root.Size = new(1280, 720); SettingsCatalog.Store("interface_scale", scale);
            game.Call("ApplySetting", "interface_scale"); await Wait(.4);
            Check(picked.All(c => c.ButtonPressed) && search.Text == "__filter__" && Math.Abs(money.Value - .05) < .001,
                "in-place resize preserves selected territory, payment and search at scale " + scale);
            CheckFits(game.FindChild("UltimatumPanel", true, false) as Control, "populated ultimatum at scale " + scale);
            CheckFits(game.FindChild("SendComposedUltimatum", true, false) as Control, "populated ultimatum pinned action at scale " + scale);
        }
        var termsScroll = game.FindChild("UltimatumTermsScroll", true, false) as ScrollContainer;
        termsScroll.ScrollVertical = int.MaxValue; await Wait(.2);
        CheckFits(game.FindChild("SendComposedUltimatum", true, false) as Control, "scrolling a long package never moves the final action");
        await Capture("decision-populated-ultimatum-russian-1280");
        Check(CampaignFingerprint.Of(world) == draftFingerprint, "editing the full ultimatum draft changes no campaign state");
        game._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape });
        game.VerificationOpenDecisionDocument("War", target.Id); await Wait(.25);
        var allyInvite = game.FindChild("InviteWarPartner_" + ally.Id, true, false) as CheckBox;
        allyInvite.ButtonPressed = false;
        (game.FindChild("RefreshWarForecast", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.25);
        Check((game.FindChild("InviteWarPartner_" + ally.Id, true, false) as CheckBox)?.ButtonPressed == false,
            "refreshing the war forecast preserves an explicitly unchecked ally");
        var warCount = world.Wars.Count;
        (game.FindChild("ConfirmWarDeclaration", true, false) as Button).GrabFocus();
        Input.ParseInputEvent(new InputEventKey { Pressed = true, Keycode = Key.Space }); await Wait(.1);
        Input.ParseInputEvent(new InputEventKey { Pressed = false, Keycode = Key.Space }); await Wait(.4);
        Check(world.Wars.Count == warCount + 1 && CampaignDiplomacy.AtWar(world, world.Player.Id, target.Id),
            "a deliberate focused Space press commits exactly one declaration through the real button");
        SettingsCatalog.Store("interface_scale", originalScale); game.Call("ApplySetting", "interface_scale");
        GetTree().Root.Size = new(1920, 1080); Loc.Language = "english";
        Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "decision probe returns through the actual menu scene");
        await Wait(2);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenDecisionDocument(string kind, int target)
    {
        var nation = _campaign.NationById(target);
        if (kind == "War") OpenWarDeclaration(nation);
        else if (kind == "Deal") OpenDealComposer(nation);
        else OpenUltimatumComposer(nation);
    }
}
