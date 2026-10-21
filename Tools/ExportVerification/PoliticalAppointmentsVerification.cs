using System;
using System.Linq;
using System.Collections.Generic;
using System.Collections;
using System.Reflection;
using System.Text.Json.Nodes;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyPoliticalAppointments(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        world.ConfigureSinglePlayer(world.NationByTag("POL").Id); world.IsPaused = true;
        var nation = world.Player;
        nation.Cabinet.Clear(); nation.CabinetPeople.Clear(); nation.LastCabinetChangeDay = -9999;
        nation.Treasury = 100000;
        game.Call("OpenPoliticalAppointments"); await Wait(.3);
        var before = CampaignFingerprint.Of(world);
        game.Call("RefreshStateAffairs"); await Wait(.15);
        Check(before == CampaignFingerprint.Of(world), "politician inspection changes no campaign state");
        var candidate = Rulers.CandidatePool(world, nation)[0];
        var price = Rulers.CabinetAppointmentCost(world, nation);
        var money = nation.Treasury;
        var button = game.FindChild("AppointPolitician0", true, false) as Button;
        Check(button is { Disabled: false }, "real cabinet exposes a named generated candidate");
        button.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.2);
        Check(nation.CabinetPeople.TryGetValue(candidate.PortfolioId, out var person) && person.Name == candidate.Name,
            "actual candidate button appoints that exact person");
        Check(Math.Abs(nation.Treasury - (money - price)) < .02f, "actual candidate button pays the quoted price");
        var rng = world.Random.State;
        before = CampaignFingerprint.Of(world);
        var snapshot = SaveSystem.ExportNetworkSnapshot(world, "");
        var loaded = SaveSystem.ImportNetworkSnapshot(snapshot, "");
        // Import retains political arrays pending the normal map-binding stage. Compare
        // adopted worlds, not a fully bound source against an unbound payload. Own the
        // political layer: attaching the source topology itself would mutate that world.
        loaded.Campaign?.AttachProvinceTopology(world.ProvinceMap.CloneForOverrides());
        Check(loaded.Campaign is not null && loaded.Campaign.Player.CabinetPeople[candidate.PortfolioId].Name == person.Name,
            "real campaign snapshot retains recruited minister identity");
        if (loaded.Campaign is not null && CampaignFingerprint.Of(loaded.Campaign) != before)
        {
            var differences = new List<string>();
            PoliticalSnapshotDifferences(JsonNode.Parse(SaveSystem.ExportScenarioJson(world, "personnel_probe")),
                JsonNode.Parse(SaveSystem.ExportScenarioJson(loaded.Campaign, "personnel_probe")), "$", differences);
            foreach (var difference in differences) GD.Print("PERSONNEL_SNAPSHOT_DIFF " + difference);
        }
        Check(loaded.Campaign is not null && CampaignFingerprint.Of(loaded.Campaign) == before,
            "personnel roundtrip retains the authoritative fingerprint");
        Check(world.Random.State == rng && CampaignFingerprint.Of(world) == before,
            "personnel snapshot does not mutate the source campaign");
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language; game.Call("RefreshLocalisedText"); GetTree().Root.Size = size;
            game.Call("OpenPoliticalAppointments"); await Wait(.25);
            var cabinet = game.FindChild("RulersCabinetHost", true, false) as Control;
            Check(cabinet is not null && cabinet.IsVisibleInTree(), "cabinet route opens in " + language + " " + size.X);
            var cabinetScroll = cabinet.GetParent();
            while (cabinetScroll is not null && cabinetScroll is not ScrollContainer) cabinetScroll = cabinetScroll.GetParent();
            Check(cabinetScroll is Control viewport && Math.Abs(cabinet.GlobalPosition.Y - viewport.GlobalPosition.Y) < 8,
                "cabinet shortcut aligns the leadership section inside the scroll viewport");
            await Capture("political-cabinet-" + language + "-" + size.X);
            game.Call("CloseStateAffairs");
            game.VerificationInspectPick(new GlobePick(nation, null, -1, nation.CapitalLatitude, nation.CapitalLongitude));
            (game.FindChild("CountryPageGovernment", true, false) as Button).EmitSignal(BaseButton.SignalName.Pressed);
            await Wait(.2);
            var card = game.FindChild("CountryCard", true, false) as Control;
            CheckFits(card, "political country government " + language + " " + size.X);
            foreach (var label in card.FindChildren("*", "Label", true, false).OfType<Label>()
                .Where(l => l.IsVisibleInTree() && l.Text.Length > 0 && l.ClipText))
                Check(label.Size.Y >= 10, "clipped country label has a readable line: " + label.Text);
            Check(card.FindChildren("*", "Label", true, false).OfType<Label>().Any(l => l.Text.Contains(person.Name)),
                "country government displays its actual recruited minister");
            await Capture("political-government-" + language + "-" + size.X);
            game.Call("CloseMapSelection");
        }
        game.VerificationInspectPick(new GlobePick(nation, null, -1, nation.CapitalLatitude, nation.CapitalLongitude));
        Loc.Language = "english"; game.Call("RefreshLocalisedText"); await Wait(.1);
        var languageCard = game.FindChild("CountryCard", true, false) as Control;
        Check(languageCard.FindChildren("CountryFactCaption", "Label", true, false).OfType<Label>()
            .Any(l => l.IsVisibleInTree() && l.Text == "Form of government"),
            "a paused open country card retranslates its retained captions immediately");
        game.Call("CloseMapSelection");
        Loc.Language = "english"; GetTree().Root.Size = new(1920,1080); game.Call("RefreshLocalisedText");
        var portraitMethod = typeof(DealOrForceGame).GetMethod("LeaderPortraitTexture", BindingFlags.NonPublic | BindingFlags.Static);
        var remember = typeof(DealOrForceGame).GetMethod("RememberPortrait", BindingFlags.NonPublic | BindingFlags.Static);
        var preview = new LeaderProfile("Personnel cache probe", "President", 50, 1974, "technocrat", "Technocrat", int.MinValue, true);
        var texture = (Texture2D)portraitMethod.Invoke(null, new object[] { preview, "#435a78" });
        var olderTexture = (Texture2D)portraitMethod.Invoke(null, new object[] { preview with { Age = 70 }, "#435a78" });
        Check(texture is not null && texture == olderTexture,
            "unknown leaders share one neutral silhouette regardless of age or negative seed");
        using (var anonymous = texture.GetImage())
        {
            var centre = anonymous.GetPixel(anonymous.GetWidth()/2, anonymous.GetHeight()*2/5); var corner = anonymous.GetPixel(0, 0);
            Check(centre.R > .8f && Math.Abs(centre.R - centre.G) < .08f && centre.R > corner.R + .1f,
                "anonymous portrait is a light head-and-shoulders silhouette, not an invented face");
        }
        for (var i = 0; i < 320; i++) remember.Invoke(null, new object[] { "bounded-personnel-probe-" + i, texture });
        var cache = (IDictionary)typeof(DealOrForceGame).GetField("PortraitCache", BindingFlags.NonPublic | BindingFlags.Static).GetValue(null);
        Check(cache.Count <= 256, "portrait cache stays bounded after many character generations");
    }

    private static void PoliticalSnapshotDifferences(JsonNode a, JsonNode b, string path, List<string> differences)
    {
        if (differences.Count >= 20 || JsonNode.DeepEquals(a, b)) return;
        if (a is JsonObject oa && b is JsonObject ob)
        {
            foreach (var key in oa.Select(p => p.Key).Union(ob.Select(p => p.Key)))
            { PoliticalSnapshotDifferences(oa[key], ob[key], path + "." + key, differences); if (differences.Count >= 20) break; }
        }
        else if (a is JsonArray aa && b is JsonArray ab)
        {
            if (aa.Count != ab.Count) differences.Add(path + ".Count " + aa.Count + " -> " + ab.Count);
            for (var i = 0; i < Math.Min(aa.Count, ab.Count) && differences.Count < 20; i++)
                PoliticalSnapshotDifferences(aa[i], ab[i], path + "[" + i + "]", differences);
        }
        else
        {
            string Short(JsonNode v) { var text = v?.ToJsonString() ?? "null"; return text.Length > 100 ? text[..100] : text; }
            differences.Add(path + " " + Short(a) + " -> " + Short(b));
        }
    }
}
