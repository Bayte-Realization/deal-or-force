using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyNuclearTargetSelection(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var russia = c.NationByTag("RUS"); var ukraine = c.NationByTag("UKR"); var usa = c.NationByTag("USA");
        c.ConfigureSinglePlayer(russia.Id); russia.NuclearPosture = 1; russia.PoliticalPoints = 500;
        c.Wars.Add(new StrategicWar { Id = c.Wars.Max(w => w.Id) + 1, AttackerId = russia.Id, DefenderId = usa.Id }); c.MarkWarsChanged();
        game.VerificationSelectPlayerCountry(); game.Call("CloseSidePanel"); await Wait(1);
        var originalIdeology = russia.Ideology;
        russia.Ideology = "communism"; game.VerificationRefreshHud(); await Wait(.2);
        Check(DealOrForceGame.FlagSpriteFor("RUS") == "flag_rus_communism", "live flag resolver automatically adopts the authored Russian Soviet banner");
        game.VerificationSelectPlayerCountry(); await Wait(.2); await Capture("automatic-russian-soviet-banner");
        russia.Ideology = originalIdeology; game.VerificationRefreshHud();
        Check(DealOrForceGame.FlagSpriteFor("RUS") == "flag_rus", "live flag resolver returns to the national tricolour after an actual ideology change");
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale"); game.Call("RefreshLocalisedText");
            var before = CampaignFingerprint.Of(c);
            game.VerificationOpenNuclearReview(); await Wait(.4);
            var country = game.FindChild("NuclearTargetCountry", true, false) as OptionButton;
            var send = game.FindChild("SendChosenNuclearStrike", true, false) as Button;
            Check(country is { ItemCount: >= 3 } && country.Selected == 0 && send is { Disabled: true }, "nuclear review requires an explicit country selection " + language + size.X);
            var selected = Enumerable.Range(1, country.ItemCount - 1).First(i => country.GetItemMetadata(i).AsInt32() == ukraine.Id);
            country.Select(selected); country.EmitSignal(OptionButton.SignalName.ItemSelected, selected); await Wait(.2);
            Check(!send.Disabled && (game.FindChild("NuclearStrikePreview", true, false) as Label).Text.Contains(ukraine.DisplayName), "chosen country appears in the actual order preview " + language + size.X);
            CheckFits(game.FindChild("NuclearStrikePanel", true, false) as Control, "nuclear country document " + language + size.X);
            CheckFits(send, "nuclear country final action " + language + size.X);
            send.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.1);
            Check(CampaignFingerprint.Of(c) == before && send.Text.Contains(ukraine.DisplayName), "first click only reviews the named target " + language + size.X);
            await Capture("nuclear-country-" + language + "-" + size.X);
            game._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape }); await Wait(.1);
            Check(CampaignFingerprint.Of(c) == before, "cancelling nuclear selection sends no command " + language + size.X);
        }
        game.VerificationOpenNuclearReview(); await Wait(.3);
        var picker = game.FindChild("NuclearTargetCountry", true, false) as OptionButton;
        var index = Enumerable.Range(1, picker.ItemCount - 1).First(i => picker.GetItemMetadata(i).AsInt32() == ukraine.Id);
        picker.Select(index); picker.EmitSignal(OptionButton.SignalName.ItemSelected, index);
        var go = game.FindChild("SendChosenNuclearStrike", true, false) as Button;
        var targetPopulation = c.StatesOf(ukraine).Sum(s => s.Population); var untouchedPopulation = c.StatesOf(usa).Sum(s => s.Population);
        go.EmitSignal(BaseButton.SignalName.Pressed); go.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.4);
        Check(c.StatesOf(ukraine).Sum(s => s.Population) < targetPopulation && c.StatesOf(usa).Sum(s => s.Population) == untouchedPopulation,
            "actual nuclear UI command strikes the chosen country rather than the larger default opponent");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "nuclear_devastation" && e.TargetId == ukraine.Id), "actual nuclear UI chronicle identifies the selected victim");
        Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "nuclear probe returns to the actual menu"); await Wait(2);
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenNuclearReview() => OpenNuclearStrikeReview(true);
}
