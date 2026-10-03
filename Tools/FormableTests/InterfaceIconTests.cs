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
        CommodityCatalog.Override("TestCrystalFuel", "Test crystal fuel", "CF", .01f, .01f, "test");
        CommodityCatalog.TryParse("TestCrystalFuel", out var custom);
        Check(CommodityCatalog.IconNameOf(custom) == "good_testcrystalfuel", "mod-added good uses the same PNG naming convention");
        CommodityCatalog.ResetCatalog();
    }
}
