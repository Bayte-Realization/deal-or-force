using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyCharacterPortraits(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var n = c.Player;
        var folder = Path.Combine(Path.GetTempPath(), "dof-portrait-proof-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            using (var image = Image.CreateEmpty(48, 64, false, Image.Format.Rgba8))
            {
                image.Fill(new Color(.85f, .15f, .1f));
                Check(image.SavePng(Path.Combine(folder, "portrait_probe_a.png")) == Error.Ok, "create owned loose portrait A");
                image.Fill(new Color(.1f, .25f, .85f));
                Check(image.SavePng(Path.Combine(folder, "portrait_probe_b.png")) == Error.Ok, "create owned loose portrait B");
            }
            var errors = new List<string>();
            SpriteCatalog.LoadTree(folder, folder.Replace('\\', '/'), errors);
            Check(errors.Count == 0, "the real sprite overlay reads loose portraits without editor imports");
            void Apply(string sprite)
            {
                using var document = JsonDocument.Parse("{\"head_of_state\":{\"portrait\":\"" + sprite + "\"}}");
                ScriptedEffects.Parse(document.RootElement, "portrait proof", errors).Apply(c, n);
            }
            Apply("portrait_probe_a");
            var a = game.VerificationLeaderTexture(n);
            using (var pixels = a.GetImage())
            {
                var color = pixels.GetPixel(24, 32);
                Check(color.R > .7f && color.B < .3f, "the actual head-of-state renderer shows the chosen loose red portrait");
            }
            game.VerificationPeoplePanel(n); await Wait(.3); await Capture("character-portrait-a");
            var before = CampaignFingerprint.Of(c);
            Apply("portrait_probe_b");
            var b = game.VerificationLeaderTexture(n);
            using (var pixels = b.GetImage())
            {
                var color = pixels.GetPixel(24, 32);
                Check(color.B > .7f && color.R < .3f, "an event switches the actual image to loose blue portrait B");
            }
            Check(a.GetInstanceId() != b.GetInstanceId() && before != CampaignFingerprint.Of(c),
                "portrait-only effects invalidate the presentation lookup and enter saved state");
            game.VerificationPeoplePanel(n); await Wait(.3); await Capture("character-portrait-b");
            Check(errors.Count == 0, "both structured portrait effects parse without diagnostics");
            Apply("portrait_does_not_exist");
            Check(game.VerificationLeaderTexture(n) is not null, "a missing sprite falls back instead of breaking the UI");
            game.VerificationPeoplePanel(null); await Wait(.2);
            var previewHash = CampaignFingerprint.Of(c);
            game.VerificationOpenCountryEditor(n.Tag); await Wait(.8);
            var choice = game.FindChild("CountryStatePortrait", true, false) as OptionButton;
            Check(choice is not null && choice.Visible, "the real country editor exposes a portrait selector");
            var index = -1;
            for (var i = 0; choice is not null && i < choice.ItemCount; i++)
                if (choice.GetItemMetadata(i).AsString() == "portrait_probe_b") index = i;
            Check(index >= 0, "a mod-resolved loose image appears in the no-code selector");
            if (index >= 0)
            {
                choice.Select(index); choice.EmitSignal(OptionButton.SignalName.ItemSelected, index); await Wait(.2);
                var preview = game.FindChild("CountryStatePortraitPreview", true, false) as TextureRect;
                using var pixels = preview.Texture.GetImage();
                Check(pixels.GetPixel(24, 32).B > .7f, "selecting a portrait updates the editor preview immediately");
                foreach (var node in game.FindChildren("*", "Button", true, false))
                    if (node is Button button && button.Text == Loc.Get("Save country to my mod"))
                    { button.EmitSignal(BaseButton.SignalName.Pressed); break; }
                await Wait(.2);
                var saved = game.VerificationEditedCountry(n.Tag);
                Check(saved is { HasPortraitSettings: true, HeadOfStatePortrait: "portrait_probe_b" },
                    "the actual editor Save button writes a portable portrait choice into the isolated mod");
                Check(previewHash == CampaignFingerprint.Of(c), "editing/saving the portrait never rewrites the running campaign");
                await Capture("character-country-editor");
            }
            GetTree().Root.Size = new Vector2I(1280, 720); Loc.Language = "russian";
            game.VerificationOpenCountryEditor(n.Tag); await Wait(.5);
            var smallChoice = game.FindChild("CountryStatePortrait", true, false) as OptionButton;
            CheckFits(smallChoice, "portrait selector fits the 720p Russian editor");
            var physicalScale = (GetViewport().GetStretchTransform() * smallChoice.GetGlobalTransformWithCanvas()).Scale.Abs().X;
            Check(smallChoice.GetThemeFontSize("font_size") * physicalScale >= 13.9f,
                "720p editor text uses at least fourteen physical pixels, not a downscaled desktop font");
            Check(smallChoice.Size.Y * physicalScale >= 31.9f, "720p portrait choice retains a thirty-two-pixel hit target");
            foreach (var node in game.FindChildren("*", "Button", true, false))
                if (node is Button button && button.Text == Loc.Get("Save country to my mod"))
                { CheckFits(button, "the real editor Save action remains reachable at 720p"); break; }
            await Capture("character-country-editor-russian-720");
            Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "portrait probe returns to the actual main menu");
            await Wait(1);
        }
        finally { Directory.Delete(folder, true); }
    }
}

public partial class DealOrForceGame
{
    private Control _verificationPeoplePanel;
    internal void VerificationOpenCountryEditor(string tag) => OpenCountryEditor(tag);
    internal CountryEditorDocument VerificationEditedCountry(string tag) => CountryEditorDocument.Read(AuthorRoot, tag);
    internal Texture2D VerificationLeaderTexture(Nation nation) => LeaderPortraitTexture(Rulers.HeadOfState(_campaign, nation), nation.Color);
    internal void VerificationPeoplePanel(Nation nation)
    {
        _verificationPeoplePanel?.QueueFree(); _verificationPeoplePanel = null;
        if (nation is null) return;
        var panel = new PanelContainer { Name = "VerificationPeoplePanel", Position = new Vector2(140, 180), CustomMinimumSize = new Vector2(420, 120) };
        panel.AddThemeStyleboxOverride("panel", ShellPanelStyle());
        panel.AddChild(BuildGrandStrategyLeaderHeader(nation));
        _hudLayer.AddChild(panel); _verificationPeoplePanel = panel;
    }
}
