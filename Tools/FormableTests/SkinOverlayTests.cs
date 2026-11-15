using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The era overlay (step 1g of docs/INTERFACE_BUILD_PLAN_2026_11_15_RU.md): a scenario or a mod
/// re-dresses the interface with data alone, and the overlay changes the look and nothing else.
/// Fixture: fixtures/skins/test_antiquity and a mod correcting it, fixtures/skin_mod.
/// </summary>
static class SkinOverlayTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("skin overlay: " + label);
        Console.WriteLine("PASS skin overlay: " + label);
    }

    public static void Run()
    {
        var skins = Path.Combine(LayoutKeyPinTests.Fixtures, "skins");
        var mod = Path.Combine(LayoutKeyPinTests.Fixtures, "skin_mod");
        var noMods = Array.Empty<(string, string)>();
        var campaign = Campaign.CreateBootstrap(73);
        var fingerprint = CampaignFingerprint.Of(campaign);

        Check(SkinOverlays.Choose("", "scenario") == "" && SkinOverlays.Choose("antiquity", "scenario") == "antiquity" &&
              SkinOverlays.Choose("antiquity", "none") == "" && SkinOverlays.Choose("", "far_future") == "far_future" &&
              SkinOverlays.Choose("antiquity", null) == "antiquity",
            "the scenario chooses, unless the player keeps the base look or picks an overlay of their own");
        var entry = ScenarioLibrary.Discover(Array.Empty<string>(), null).FirstOrDefault(e => e.Id == "2024");
        Check(entry is not null && entry.Skin == "", "the base scenario names no overlay: it wears the neutral base");
        Check(campaign.SkinOverlay == "", "and the default new campaign, built without the picker, says the same");

        // The default new game is the 2024 scenario without its picker. A mod overriding that
        // scenario's manifest has to reach it too, not only the same scenario chosen by hand.
        var overrideRoot = Path.Combine(Path.GetTempPath(), "dof_skin_overlay_bootstrap_" + Environment.ProcessId);
        try
        {
            Directory.CreateDirectory(Path.Combine(overrideRoot, "scenarios", "2024"));
            File.WriteAllText(Path.Combine(overrideRoot, "scenarios", "2024", "scenario.json"),
                "{ \"id\": \"2024\", \"name\": \"The world in 2024\", \"skin\": \"test_antiquity\" }");
            Check(ScenarioLibrary.SkinOf("2024", new[] { overrideRoot }) == "test_antiquity" &&
                  ScenarioLibrary.SkinOf("2024", null) == "",
                "a mod's 2024 manifest names the overlay the way the scenario picker reads it");
            Check(Campaign.CreateBootstrap(74, new[] { overrideRoot }).SkinOverlay == "test_antiquity",
                "the default new campaign wears the overlay a mod's 2024 manifest names");
        }
        finally
        {
            if (Directory.Exists(overrideRoot)) Directory.Delete(overrideRoot, true);
        }
        Check(SkinOverlays.Discover(skins, new[] { mod }).Contains("test_antiquity"), "overlays are found by folder, in the game and in mods");

        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();
        var tokens = InterfaceSkin.Tokens.ToDictionary(t => t.Token, t => t.Value, StringComparer.OrdinalIgnoreCase);
        var panels = InterfaceSkin.Panels.ToList();
        var sprites = SpriteCatalog.Names.ToDictionary(name => name, name => SpriteCatalog.TextureOf(name), StringComparer.OrdinalIgnoreCase);
        var plate = InterfaceMaterials.Resolve("plate");
        Check(InterfaceSkin.Colour("material.plate.top") == "#2a2520" && SkinOverlays.Active == "",
            "without an overlay the plate is the base colour");

        var errors = new List<string>();
        Check(SkinOverlays.Apply("test_antiquity", skins, noMods, errors) == "test_antiquity" && errors.Count == 0 &&
              SkinOverlays.Active == "test_antiquity", "an overlay applies by id");
        Check(InterfaceSkin.Colour("material.plate.top") == "#5a4a36", "with the overlay the plate takes the overlay's colour");
        var changedTokens = InterfaceSkin.Tokens.Where(t => !tokens.TryGetValue(t.Token, out var was) ||
            !string.Equals(was, t.Value, StringComparison.OrdinalIgnoreCase)).Select(t => t.Token).ToList();
        var changedSprites = SpriteCatalog.Names.Where(name => !sprites.TryGetValue(name, out var was) ||
            was != SpriteCatalog.TextureOf(name)).ToList();
        var after = InterfaceMaterials.Resolve("plate");
        Check(changedTokens.SequenceEqual(new[] { "material.plate.top" }) && InterfaceSkin.Panels.SequenceEqual(panels) &&
              changedSprites.SequenceEqual(new[] { "icon_army_spear" }) &&
              after.EngraveInset == 3 && after.Top == plate.Top && after.Bevel == plate.Bevel &&
              after.DropShadow == plate.DropShadow && after.CornerRadius == plate.CornerRadius,
            "an overlay swaps the tokens, materials and pictures it names, and nothing else - no panel moves");
        Check(CampaignUiCatalog.Icon("nav.army", "military") == "army_spear" &&
              SpriteCatalog.Has("icon_army_spear") &&
              SpriteCatalog.TextureOf("icon_army_spear").StartsWith("res://game/skins/test_antiquity/gfx/", StringComparison.Ordinal),
            "an overlay re-points an icon role (nav.army) at its own picture");

        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();
        Check(SkinOverlays.Active == "", "reloading the skin takes the overlay off");
        SkinOverlays.Apply("test_antiquity", skins, new[] { (mod, "user://mods/skin_mod") }, errors);
        Check(InterfaceSkin.Colour("material.plate.top") == "#605040", "a mod's skins/<id> lands over the game's overlay of the same id");

        SkinOverlays.Rebuild("", skins, noMods, errors);
        Check(InterfaceSkin.Colour("material.plate.top") == "#2a2520" && SkinOverlays.Active == "" &&
              CampaignUiCatalog.Icon("nav.army", "military") == "military", "rebuilding with no overlay is the base look again");

        errors.Clear();
        Check(SkinOverlays.Apply("no_such_era", skins, noMods, errors) == "" && errors.Count == 1 && errors[0].Contains("no_such_era") &&
              InterfaceSkin.Colour("material.plate.top") == "#2a2520",
            "a scenario naming an overlay nobody ships gets the base look and one line in the log, not a crash");

        campaign.SkinOverlay = "test_antiquity";
        SkinOverlays.Apply(campaign.SkinOverlay, skins, noMods, errors);
        Check(fingerprint == CampaignFingerprint.Of(campaign), "the overlay and the campaign's choice of it never change the world fingerprint");
        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();
    }
}
