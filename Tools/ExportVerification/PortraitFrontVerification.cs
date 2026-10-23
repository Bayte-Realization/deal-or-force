using System;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private void VerifyLeaderPhotoFormats()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dof-portrait-probe-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        using var source = Image.CreateEmpty(1024, 768, false, Image.Format.Rgb8);
        source.Fill(new Color(.8f, .12f, .4f));
        Check(source.SavePng(Path.Combine(directory, "leader_png_probe.png")) == Error.Ok, "PNG portrait fixture created");
        Check(source.SaveJpg(Path.Combine(directory, "leader_jpeg_probe.jpg")) == Error.Ok, "JPEG portrait fixture created");
        Check(source.SaveWebp(Path.Combine(directory, "leader_webp_probe.webp")) == Error.Ok, "WebP portrait fixture created");
        SpriteCatalog.LoadTree(directory, directory, null);
        var method = typeof(DealOrForceGame).GetMethod("LeaderPortraitTexture", BindingFlags.NonPublic | BindingFlags.Static);
        foreach (var format in new[] { "PNG", "JPEG", "WebP" })
        {
            var person = new LeaderProfile(format + " probe", "President", 50, 1974, "hawk", "Hawk", 1, true);
            var texture = (Texture2D)method.Invoke(null, new object[] { person, "#254465" });
            using var image = texture.GetImage(); var colour = image.GetPixel(0, 0);
            Check(image.GetWidth() == 512 && image.GetHeight() == 384 && colour.R > .7f && colour.G < .2f,
                format + " photo loads loose without editor import and is downscaled preserving aspect ratio");
        }
        SpriteCatalog.Load();
        var missing = new LeaderProfile("Missing photo probe", "President", 50, 1974, "hawk", "Hawk", 1, true);
        var fallback = (Texture2D)method.Invoke(null, new object[] { missing, "#254465" });
        Check(fallback.GetWidth() > 0 && fallback.GetWidth() <= 512 && fallback.GetHeight() <= 512,
            "missing photo falls back to the bounded neutral silhouette asset");
    }

    private async Task VerifyFrontOverlay(DealOrForceGame game)
    {
        var world = game.VerificationWorld; var map = world.ProvinceMap;
        world.IsPaused = true; world.ObserverMode = true; world.Player.IsHuman = false;
        var russia = world.NationByTag("RUS"); var ukraine = world.NationByTag("UKR");
        var war = world.Wars.First(w => !w.Ended && w.Involves(russia.Id) && w.Involves(ukraine.Id));
        foreach (var other in world.Wars.Where(w => w != war)) other.Ended = true;
        var plain = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && map.ProvinceToNation[p] == russia.MapIndex)
            .SelectMany(p => map.Neighbors[p].Where(q => !map.IsWater(q) && map.ProvinceToNation[q] == ukraine.MapIndex).Select(q => (P:p,Q:q))).First();
        var occupied = Enumerable.Range(1, map.ProvinceCount).Where(p => !map.IsWater(p) && map.ProvinceToNation[p] == ukraine.MapIndex)
            .SelectMany(p => map.Neighbors[p].Where(q => !map.IsWater(q) && map.ProvinceToNation[q] == ukraine.MapIndex).Select(q => (P:p,Q:q))).First();
        game.Call("CloseMapSelection"); game.Call("CloseStateAffairs");
        foreach (var isOccupied in new[] { false, true })
        foreach (var flat in new[] { false, true })
        {
            var pair = isOccupied ? occupied : plain;
            world.SetProvinceController(pair.P, (ushort)russia.MapIndex);
            world.SetProvinceController(pair.Q, (ushort)ukraine.MapIndex);
            var a = map.CenterOf(pair.P); var b = map.CenterOf(pair.Q);
            GetTree().Root.Size = new Vector2I(1920,1080);
            game.VerificationOccupationView(flat, (a.Latitude+b.Latitude)/2, (a.Longitude+b.Longitude)/2, 5.5f);
            war.Ended = true; Warfare.RebuildFronts(world); game.VerificationFrontRefresh(); await Wait(.7);
            var baseline = CountFrontRed();
            war.Ended = false; Warfare.RebuildFronts(world); game.VerificationFrontRefresh(); await Wait(.7);
            var before = CampaignFingerprint.Of(world); var red = CountFrontRed();
            Check(red > baseline + 20, $"red hostile front remains visible ({isOccupied}/{flat}: {baseline}->{red})");
            await Capture("red-front-" + (isOccupied ? "occupied" : "unoccupied") + "-" + (flat ? "flat" : "globe"));
            Check(before == CampaignFingerprint.Of(world), "front rendering does not alter ownership or campaign state");
        }
    }

    private int CountFrontRed()
    {
        using var image = GetTree().Root.GetTexture().GetImage(); var total = 0;
        for (var y = 140; y < 920; y++)
        for (var x = 620; x < 1400; x++)
        {
            var c = image.GetPixel(x,y);
            // Display tonemapping lifts a pure shader red into salmon; measure its
            // hue separation, not one pre-tonemap RGB triplet.
            if (c.R > .65f && c.G < .65f && c.B < .65f && c.R > c.G * 1.5f && c.R > c.B * 1.5f) total++;
        }
        return total;
    }
}

public partial class DealOrForceGame
{
    internal void VerificationFrontRefresh() => _globe.RefreshBattlePlans();
}
