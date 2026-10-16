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

    /// <summary>
    /// The lightest panel background the interface actually renders.
    ///
    /// Sampled from a captured frame (tmp_playtest/ui-scenarios-1, the scenario library). A
    /// panel is a texture tinted by `surface.panel_tint`, so its finished colour is not a
    /// palette token and cannot be looked up in the skin - which is why this is a measured
    /// constant with its provenance written down rather than a token.
    /// </summary>
    private const string PanelBackground = "#21272E";

    private static float Channel(int value)
    {
        var c = value / 255f;
        return c <= 0.03928f ? c / 12.92f : MathF.Pow((c + 0.055f) / 1.055f, 2.4f);
    }

    /// <summary>WCAG relative luminance of a `#rrggbb` string.</summary>
    private static float Luminance(string hex)
    {
        var r = Convert.ToInt32(hex.Substring(1, 2), 16);
        var g = Convert.ToInt32(hex.Substring(3, 2), 16);
        var b = Convert.ToInt32(hex.Substring(5, 2), 16);
        return 0.2126f * Channel(r) + 0.7152f * Channel(g) + 0.0722f * Channel(b);
    }

    private static float Contrast(string foreground, string background)
    {
        var a = Luminance(foreground);
        var b = Luminance(background);
        return (MathF.Max(a, b) + 0.05f) / (MathF.Min(a, b) + 0.05f);
    }

    public static void Run()
    {
        InterfaceSkin.ResetCatalog();

        // The text ramp has to be readable, not only coherent.
        //
        // The two dimmest steps are what the smallest text uses - footnotes and captions,
        // where a low ratio is hardest to read - and they were the two that failed: measured
        // against the lightest panel the interface renders, `text.faint` sat at 3.84:1 and
        // `text.hint` at 4.62:1 against WCAG AA's 4.5:1. Both were raised. This covers the
        // whole ramp rather than the two that happened to be wrong, so a future retint cannot
        // quietly dim the bottom of it again.
        var ramp = new[] { "text.bright", "text.body", "text.body_soft", "text.muted", "text.caption", "text.hint", "text.faint" };
        foreach (var token in ramp)
        {
            Check(InterfaceSkin.Has(token), "the text ramp declares " + token);
            var ratio = Contrast(InterfaceSkin.Colour(token), PanelBackground);
            Check(ratio >= 4.5f, token + " clears WCAG AA on a panel at " + ratio.ToString("0.00") + ":1");
        }
        for (var i = 1; i < ramp.Length; i++)
            Check(Luminance(InterfaceSkin.Colour(ramp[i - 1])) > Luminance(InterfaceSkin.Colour(ramp[i])),
                "the ramp stays in order: " + ramp[i - 1] + " is brighter than " + ramp[i]);

        foreach (var file in new[] { "DiplomacyScreenUI.cs", "NationalCourseUI.cs", "PolicyWorkspaceUI.cs", "PolicyWorkspaceDetails.cs", "SovereignFinanceUI.cs" })
        {
            var source = Path.Combine(GameDataLoader.DataFolder(), "..", "Scripts", "ui", file);
            var matches = System.Text.RegularExpressions.Regex.Matches(File.ReadAllText(source), "Ui\\.Colour\\(\"([^\"]+)\"\\)");
            Check(matches.All(m => InterfaceSkin.Has(m.Groups[1].Value)),
                "governance UI uses declared palette tokens, not diagnostic magenta: " + file);
        }
        Check(CampaignUiCatalog.Icon("field.Public debt", "") == "public_debt" &&
            CampaignUiCatalog.Icon("field.Established course", "") == "agenda",
            "finance and national course field icons resolve from editable GUI data");
        Check(CampaignUiCatalog.For("Economy").Tabs.Any(t => t.Id == "Finance") &&
            CampaignUiCatalog.For("Laws").Tabs.Any(t => t.Id == "Course"), "dedicated finance and national course tabs are declared in data");
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
        PoliticalPolicyCatalog.ResetCatalog();
        Check(PoliticalPolicyCatalog.AllLaws.Concat(PoliticalPolicyCatalog.AllAgendas).All(p =>
            !string.IsNullOrWhiteSpace(p.Description) && SpriteCatalog.Has("icon_" + p.Icon)),
            "every vanilla law and promise has an explanation and an existing sprite");
        Check(NationalCourseCatalog.All.All(p => SpriteCatalog.Has("icon_" + p.Icon)),
            "every organic national course resolves an actual icon, including postwar renewal");
        Check(SpriteCatalog.Has("icon_" + CampaignUiCatalog.ActionIcon("Review deal")) &&
            SpriteCatalog.Has("icon_" + CampaignUiCatalog.ActionIcon("Confirm response")),
            "new deal and crisis actions use existing semantic artwork without doubled prefixes");
        using (var actionDocument = System.Text.Json.JsonDocument.Parse(File.ReadAllText(
            Path.Combine(GameDataLoader.DataFolder(), "gui", "31_action_icons.json"))))
        {
            var roles = actionDocument.RootElement.GetProperty("campaign").GetProperty("icons").EnumerateObject().ToArray();
            Check(roles.Count(role => role.Name.StartsWith("action.", StringComparison.Ordinal)) == 35,
                "thirty-five action roles are declared in moddable data");
            Check(roles.Count(role => role.Name.StartsWith("social.", StringComparison.Ordinal)) == 7,
                "seven social metric roles are declared separately from actions");
            Check(roles.All(role => SpriteCatalog.Has("icon_" + role.Value.GetString())),
                "every action and social metric resolves an existing PNG sprite");
            Check(CampaignUiCatalog.ActionIcon("Improve relations") == "diplomacy" &&
                CampaignUiCatalog.ActionIcon("Start programme") == "plan_execute",
                "source captions resolve their semantic action icons");
            Check(CampaignUiCatalog.ActionIcon("+10") == "" && CampaignUiCatalog.ActionIcon("Unknown mod action") == "",
                "numeric and unknown captions do not receive guessed icons");
            var originalLanguage = Loc.Language;
            try
            {
                Loc.ResetCatalog(); Loc.Language = "russian";
                Check(Loc.Get("Improve relations") != "Improve relations" &&
                    CampaignUiCatalog.ActionIcon(Loc.Get("Improve relations")) == "diplomacy",
                    "already-localised Russian buttons retain the same semantic icon");
            }
            finally { Loc.Language = originalLanguage; }
            errors.Clear();
            InterfaceSkin.Merge("{\"campaign\":{\"icons\":{\"action.Start programme\":\"clock\"}}}", "action-mod-test", errors);
            Check(errors.Count == 0 && CampaignUiCatalog.ActionIcon("Start programme") == "clock" &&
                CampaignUiCatalog.Icon("social.funding", "") == "treasury",
                "action mod override preserves metric roles");
            InterfaceSkin.ResetCatalog();
            Check(CampaignUiCatalog.ActionIcon("Start programme") == "plan_execute", "action mappings restore on reload");
        }
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
            Path.Combine(root, "..", "Tools", "IconCompiler", "Source", "Icons", "minimal-icons.json")));
        var authored = recipe.RootElement.EnumerateArray().Select(item => item.GetProperty("name").GetString()).ToArray();
        Check(authored.Length == 51 && authored.Distinct().Count() == 51,
            "editable geometry defines fifty-one distinct PNG assets");
        Check(CommodityCatalog.All.All(good => authored.Contains(CommodityCatalog.IconNameOf(good))),
            "all thirty-four goods have editable artwork, not just the newer strategic goods");
        Check(authored.All(name => SpriteCatalog.Get("icon_" + name).SourceColour ==
            (name.StartsWith("good_", StringComparison.Ordinal) ? "#b48732" : "#e7bd69")),
            "all commodity PNGs declare bronze while command artwork keeps its gold palette");
        Check(InterfaceSkin.Colour("icon.commodity") == "#b48732", "commodity token uses the requested darker gold");
        errors.Clear();
        var commandColour = InterfaceSkin.Colour("text.accent_dim");
        InterfaceSkin.Merge("{\"colours\":{\"icon.commodity\":\"#997733\"}}", "commodity-palette-test", errors);
        Check(errors.Count == 0 && InterfaceSkin.Colour("icon.commodity") == "#997733" &&
            InterfaceSkin.Colour("text.accent_dim") == commandColour,
            "modders can recolour goods independently of commands and headings");
        InterfaceSkin.ResetCatalog();
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
                SpriteCatalog.Get("icon_good_arms").SourceColour == "#b48732",
                "drop-in PNG override clears only its own reference, preserving unrelated bronze sprites");
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
