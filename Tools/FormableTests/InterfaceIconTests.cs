using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class InterfaceIconTests
{
    private static void Check(bool valid, string message)
    {
        if (!valid) throw new Exception(message);
        Console.WriteLine("PASS icon roles: " + message);
    }

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();
        Check(CampaignUiCatalog.Icon("map.Economy", "") == "gdp", "economic map uses output, not cash");
        Check(CampaignUiCatalog.Icon("hud.BALANCE", "") != CampaignUiCatalog.Icon("hud.STABILITY", ""),
            "balance and stability have distinct roles");
        var errors = new List<string>();
        InterfaceSkin.Merge("{\"campaign\":{\"icons\":{\"map.Economy\":\"industry\"}}}", "icons-test", errors);
        Check(errors.Count == 0 && CampaignUiCatalog.Icon("map.Economy", "") == "industry" &&
            CampaignUiCatalog.Icon("hud.GDP", "") == "gdp", "partial mod override preserves unrelated icons");
        InterfaceSkin.Merge("{\"campaign\":{\"icons\":{\"map.Economy\":null,\"hud.GDP\":\" \"}}}", "icons-test", errors);
        Check(errors.Count == 2 && CampaignUiCatalog.Icon("map.Economy", "") == "industry",
            "invalid mappings are reported without destroying inherited values");
        Check(CampaignUiCatalog.Icon("not.declared", "fallback") == "fallback", "missing role retains fallback");
        InterfaceSkin.ResetCatalog();
        Check(CampaignUiCatalog.Icon("map.Economy", "") == "gdp", "reload clears old overrides");
        SpriteCatalog.Load();
        var icons = new[] { "army_halt", "army_defend", "army_advance", "army_spearhead", "army_reserve",
            "army_withdraw", "plan_prepare", "plan_execute", "auto_deploy", "plan_front", "plan_attack",
            "plan_fallback", "plan_clear", "plan_erase", "stance_cautious", "stance_aggressive" };
        Check(icons.All(name => SpriteCatalog.Has("icon_" + name)), "all sixteen army command sprites resolve");
        CommodityCatalog.ResetCatalog();
        Check(CommodityCatalog.All.All(good => SpriteCatalog.Has("icon_" + CommodityCatalog.IconNameOf(good))),
            "every shipped good has a named raster icon, including data-defined strategic goods");
        Check(CommodityCatalog.TryParse("Ammunition", out var ammunition) &&
            CommodityCatalog.IconNameOf(ammunition) == "good_ammunition", "ammunition uses its stable ID rather than enum number");
        var root = GameDataLoader.DataFolder();
        foreach (var name in icons.Concat(CommodityCatalog.All.Select(CommodityCatalog.IconNameOf)))
        {
            var path = Path.Combine(root, "gfx", "interface", "icons", "icon_" + name + ".png");
            Check(File.Exists(path) && File.ReadAllBytes(path).Take(8).SequenceEqual(new byte[] { 137, 80, 78, 71, 13, 10, 26, 10 }),
                "PNG file exists: " + name);
        }
        using var recipe = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(root, "..", "Assets", "ArtSource", "Icons", "minimal-icons.json")));
        var authored = recipe.RootElement.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Check(authored.Length == 33 && authored.Distinct().Count() == 33,
            "editable geometry defines thirty-three distinct PNG assets");
        Check(authored.All(name => SpriteCatalog.Get("icon_" + name).SourceColour == "#e7bd69"),
            "gold PNGs declare their authored palette for theme compensation");
        Check(!File.ReadAllBytes(Path.Combine(root, "gfx/interface/icons/icon_good_steel.png"))
            .SequenceEqual(File.ReadAllBytes(Path.Combine(root, "gfx/interface/icons/icon_good_industrialmaterials.png"))),
            "steel and construction materials have distinct artwork");
        errors.Clear();
        SpriteCatalog.Merge("{\"icon_good_steel\":{\"source_colour\":\"#ffffff\"}}", "test", "test", errors);
        Check(errors.Count == 0 && SpriteCatalog.Get("icon_good_steel").SourceColour == "#ffffff",
            "mod can declare a white icon reference");
        SpriteCatalog.Merge("{\"icon_good_steel\":{\"source_colour\":\"#ff0000\"}}", "test", "test", errors);
        Check(errors.Count == 1 && SpriteCatalog.Get("icon_good_steel").SourceColour == "#ffffff",
            "invalid zero-channel references report a problem and preserve prior colour");
        SpriteCatalog.Load();
        errors.Clear();
        SpriteCatalog.Merge("{\"icon_good_steel\":{\"texture\":\"my_white_steel.png\"}}", "test", "test", errors);
        Check(errors.Count == 0 && SpriteCatalog.Get("icon_good_steel").SourceColour == "",
            "explicit replacement texture does not inherit the old gold reference");
        SpriteCatalog.Load();
        var fixture = Path.Combine(Path.GetTempPath(), "dof-icon-overlay-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(fixture);
        try
        {
            File.Copy(Path.Combine(root, "gfx/interface/icons/icon_good_steel.png"), Path.Combine(fixture, "icon_good_steel.png"));
            SpriteCatalog.LoadTree(fixture, "test", errors);
            Check(errors.Count == 0 && SpriteCatalog.Get("icon_good_steel").SourceColour == "" &&
                SpriteCatalog.Get("icon_good_arms").SourceColour == "#e7bd69",
                "drop-in PNG override clears only its own reference, preserving unrelated gold sprites");
            SpriteCatalog.Merge("{\"icon_good_steel\":{\"source_colour\":\"#e7bd69\"}}", "test", "test", errors);
            Check(errors.Count == 0 && SpriteCatalog.Get("icon_good_steel").SourceColour == "#e7bd69",
                "gold replacement can opt into compensation after a filename scan");
        }
        finally
        {
            // Only our uniquely named single-file fixture, never a user directory.
            File.Delete(Path.Combine(fixture, "icon_good_steel.png"));
            Directory.Delete(fixture);
            SpriteCatalog.Load();
        }
        Check(icons.All(authored.Contains), "every army command has editable minimal geometry");
        foreach (var name in authored)
        {
            var png = File.ReadAllBytes(Path.Combine(root, "gfx", "interface", "icons", "icon_" + name + ".png"));
            int ReadBigEndian(int offset) => (png[offset] << 24) | (png[offset + 1] << 16) |
                (png[offset + 2] << 8) | png[offset + 3];
            Check(png.Length > 33 && ReadBigEndian(16) == 96 && ReadBigEndian(20) == 96 &&
                png[24] == 8 && png[25] == 6, "minimal PNG is 96x96 with RGBA transparency: " + name);
        }
        CommodityCatalog.Override("TestCrystalFuel", "Test crystal fuel", "CF", .01f, .01f, "test");
        CommodityCatalog.TryParse("TestCrystalFuel", out var custom);
        Check(CommodityCatalog.IconNameOf(custom) == "good_testcrystalfuel", "mod-added good uses the same PNG naming convention");
        CommodityCatalog.ResetCatalog();
    }
}
