using System;
using System.Linq;
using System.IO;
using System.IO.Compression;
using System.Text.Json;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyConstructionPlacement(DealOrForceGame game)
    {
        var c = game.VerificationWorld; c.IsPaused = true;
        var player = c.NationByTag("RUS"); c.ConfigureSinglePlayer(player.Id); player.Treasury = 1000;
        var own = c.StatesOf(player).First(s => !LakeRegions.IsWaterState(s));
        var other = c.NationByTag("USA"); var foreign = c.StatesOf(other).First();
        game.VerificationSelectPlayerCountry(); await Wait(.5);
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale"); game.Call("RefreshLocalisedText");
            var before = CampaignFingerprint.Of(c);
            var snapshot = Snapshot(c);
            game.VerificationOpenConstructionCatalogue(); await Wait(.3);
            if (CampaignFingerprint.Of(c) != before) SnapshotDiff(snapshot, Snapshot(c));
            Check(CampaignFingerprint.Of(c) == before, "construction catalogue does not mutate the paused world " + language + size.X);
            var place = game.FindChild("PlaceBuildingCivilianIndustry", true, false) as Button;
            Check(place is { Disabled: false } && place.IsVisibleInTree(), "actual building catalogue exposes map placement " + language + size.X);
            place.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.4);
            Check(CampaignFingerprint.Of(c) == before, "choosing map tool reserves neither money nor a project " + language + size.X);
            game.VerificationConstructionPick(other, foreign); await Wait(.1);
            Check(CampaignFingerprint.Of(c) == before, "foreign map click is refused without opening diplomacy or changing campaign state " + language + size.X);
            var count = c.Construction.Count;
            game.VerificationConstructionPick(player, own); await Wait(.2);
            Check(c.Construction.Count == count + 1 && c.Construction[^1].StateMapIndex == own.MapIndex && c.Construction[^1].Building == BuildingKind.CivilianIndustry,
                "one actual map dispatch queues exactly one requested project " + language + size.X);
            var panel = game.FindChild("ConstructionPlacement", true, false) as Control;
            CheckFits(panel, "compact construction placement " + language + size.X);
            var feedback = game.FindChild("ConstructionPlacementFeedback", true, false) as Label;
            var pixels = feedback.LabelSettings.FontSize * (GetTree().Root.Size.Y / game.GetViewport().GetVisibleRect().Size.Y) * feedback.GetGlobalTransformWithCanvas().Scale.Y;
            Check(pixels >= 13.99f, "map tool explanations retain 14px physical text " + language + size.X);
            await Capture("construction-placement-" + language + "-" + size.X);
            before = CampaignFingerprint.Of(c);
            game._Input(new InputEventKey { Pressed = true, Keycode = Key.Escape }); await Wait(.1);
            Check(game.FindChild("ConstructionPlacement", true, false) is null && CampaignFingerprint.Of(c) == before,
                "Escape ends construction placement without cancelling paid projects " + language + size.X);
        }
        game.VerificationStartConstructionTool(); await Wait(.2);
        var last = CampaignFingerprint.Of(c);
        game._Input(new InputEventMouseButton { Pressed = true, ButtonIndex = MouseButton.Right }); await Wait(.1);
        Check(game.FindChild("ConstructionPlacement", true, false) is null && CampaignFingerprint.Of(c) == last, "right-click cancels placement before it can issue an army order");
        game.VerificationRiverCamera(4.5f); await Wait(2); await Capture("rivers-png-volga-don");
        game.VerificationRiverCamera(3.6f); await Wait(2); await Capture("rivers-png-volgograd-close");
        last = CampaignFingerprint.Of(c);
        Check(game.VerificationTags("on").Length > 0 && SettingsCatalog.Flag("show_country_tags"), "tags on console command enables the saved display option");
        await Wait(.3); await Capture("country-tags-on");
        Check(game.VerificationTags("off").Length > 0 && !SettingsCatalog.Flag("show_country_tags"), "tags off restores the clean display");
        game.VerificationTags(""); Check(SettingsCatalog.Flag("show_country_tags"), "tags without an argument toggles"); game.VerificationTags("off");
        Check(game.VerificationTags("nope") == "tags [on|off]" && CampaignFingerprint.Of(c) == last, "display commands and malformed tag arguments do not mutate campaign state");
        await VerifySurfaceBattlesAndNationalFlags(game);
        Check(GetTree().ChangeSceneToFile("res://Scenes/MainMenu.tscn") == Error.Ok, "construction probe returns to the real menu"); await Wait(2);
    }

    private static string Snapshot(Campaign c)
    {
        using var input = new MemoryStream(SaveSystem.ExportNetworkSnapshot(c, "construction-verification"));
        using var gzip = new GZipStream(input, CompressionMode.Decompress);
        using var reader = new StreamReader(gzip); return reader.ReadToEnd();
    }
    private static void SnapshotDiff(string before, string after)
    {
        using var first = JsonDocument.Parse(before); using var last = JsonDocument.Parse(after);
        var reported = 0;
        void Walk(JsonElement a, JsonElement b, string path)
        {
            if (reported >= 15 || a.GetRawText() == b.GetRawText()) return;
            if (a.ValueKind == JsonValueKind.Object && b.ValueKind == JsonValueKind.Object)
            {
                foreach (var p in a.EnumerateObject()) if (b.TryGetProperty(p.Name, out var next)) Walk(p.Value, next, path + "." + p.Name);
            }
            else if (a.ValueKind == JsonValueKind.Array && b.ValueKind == JsonValueKind.Array && a.GetArrayLength() == b.GetArrayLength())
                for (var i = 0; i < a.GetArrayLength(); i++) Walk(a[i], b[i], path + "[" + i + "]");
            else { reported++; GD.Print("CONSTRUCTION_READ_DIFF " + path + " " + a.GetRawText()[..Math.Min(90, a.GetRawText().Length)] + " -> " + b.GetRawText()[..Math.Min(90, b.GetRawText().Length)]); }
        }
        Walk(first.RootElement, last.RootElement, "world");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationOpenConstructionCatalogue() => OpenEconomyTab("Construction");
    internal void VerificationConstructionPick(Nation n, ScenarioState s) => OnCountryPicked(new GlobePick(n, s, -1, 0, 0));
    internal void VerificationStartConstructionTool() => BeginConstructionPlacement(BuildingKind.CivilianIndustry);
    internal void VerificationRiverCamera(float distance)
    { CloseMapSelection(); _globe.SetFlatMap(true); _globe.FocusOn(48.8f, 44.5f, distance); }
    internal string VerificationTags(string argument) => ConsoleCountryTags(argument);
}
