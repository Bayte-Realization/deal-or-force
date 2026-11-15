using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Icon families (step 1d of docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md): every top-bar figure
/// and every button of the screen rail belongs to a family, every family has a colour, and the
/// icons the roles name exist. The icons themselves stay the existing white masks.
/// </summary>
static class IconCategoryTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("icon categories: " + label);
        Console.WriteLine("PASS icon categories: " + label);
    }

    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(73);
        var fingerprint = CampaignFingerprint.Of(campaign);
        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();

        var families = new[] { "economy", "military", "diplomacy", "society", "state" };
        Check(families.All(family => InterfaceSkin.Has("icon.category." + family)), "each of the five families has a colour token");

        var roles = CampaignUiCatalog.IconRoles.Where(role => role.StartsWith("hud.", StringComparison.Ordinal))
            .Concat(CampaignUiCatalog.NavigationItems.Select(item => "nav." + item.Screen)).Distinct().ToList();
        var homeless = roles.Where(role => CampaignUiCatalog.IconCategoryToken(role) is not { Length: > 0 } token || !InterfaceSkin.Has(token)).ToList();
        Check(roles.Count >= 20 && homeless.Count == 0, $"all {roles.Count} hud.* and nav.* roles have a family with a colour" +
            (homeless.Count == 0 ? "" : ": missing " + string.Join(", ", homeless)));
        var strays = CampaignUiCatalog.IconCategoryEntries.Where(pair => !families.Contains(pair.Value)).Select(pair => pair.Key).ToList();
        Check(strays.Count == 0, "the shipped data uses only the five families" + (strays.Count == 0 ? "" : ": " + string.Join(", ", strays)));

        using var document = JsonDocument.Parse(File.ReadAllText(Path.Combine(GameDataLoader.DataFolder(), "gui", "30_icon_roles.json")),
            GameDataLoader.ForgivingJson);
        var named = document.RootElement.GetProperty("campaign").GetProperty("icons").EnumerateObject()
            .Select(entry => (Role: entry.Name, Sprite: "icon_" + entry.Value.GetString())).ToList();
        var missing = named.Where(entry => !SpriteCatalog.Has(entry.Sprite)).Select(entry => entry.Role).ToList();
        Check(named.Count > 0 && missing.Count == 0, $"every role in gui/30_icon_roles.json resolves to an existing icon ({named.Count})" +
            (missing.Count == 0 ? "" : ": missing " + string.Join(", ", missing)));

        var errors = new List<string>();
        InterfaceSkin.Merge("{\"campaign\":{\"icon_categories\":{\"hud.GDP\":\"military\",\"hud.BAD\":7}}}", "mod", errors);
        Check(CampaignUiCatalog.IconCategory("hud.GDP") == "military" && errors.Count == 1 &&
              CampaignUiCatalog.IconCategory("hud.TREASURY") == "economy",
            "a mod moves one role to another family; a malformed entry is reported and the rest stand");
        Check(fingerprint == CampaignFingerprint.Of(campaign), "icon families never change the world fingerprint");
        InterfaceSkin.ResetCatalog();
    }
}
