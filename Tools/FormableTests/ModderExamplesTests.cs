using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Modding;
using DealOrForce.Simulation;

/// <summary>Executable examples referenced by docs/MODDER_CODE_MAP_RU.md.
/// Keep examples here when changing the mod contract; comments alone do not prove it.</summary>
static class ModderExamplesTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("modder example: " + label); Console.WriteLine("PASS modder example: " + label); }

    static ScriptedEffects.Effect Effect(string text, string path = "events/guide.yml")
    {
        using var document = StructuredData.ParseDocument(path, text);
        var errors = new List<string>();
        var result = ScriptedEffects.Parse(document.RootElement, path, errors);
        Check(errors.Count == 0, "example parses without diagnostics: " + path);
        return result;
    }

    public static void Run()
    {
        var (c, nation, other) = TerritorialRecoveryTests.World();
        nation.Treasury = 100; nation.Stability = .5f;
        var effect = Effect("treasury_percent: -0.02\nstability: 0.018\n");
        effect.Apply(c, nation);
        Check(Math.Abs(nation.Treasury - 98) < .00001, "-0.02 percent modifier spends 2 percent of positive treasury");
        Check(Math.Abs(nation.Stability - .518f) < .00001, "bare 0.018 adds 1.8 percentage points, not 1.8 percent");
        nation.Stability = .5f;
        Effect("stability_percent: 0.02\n").Apply(c, nation);
        Check(Math.Abs(nation.Stability - .51f) < .00001, "percent suffix scales the original share");
        Effect("stability_set: 2\n").Apply(c, nation);
        Check(nation.Stability == .98f, "explicit assignment respects the stability effect's 10–98 percent limits");

        foreach (var region in c.States) region.Radicalism = .5f;
        Effect("state.radicalism: -0.01\n").Apply(c, nation);
        Check(c.StatesOf(nation).All(s => Math.Abs(s.Radicalism - .49f) < .00001), "state prefix changes every owned region");
        Check(c.StatesOf(other).All(s => s.Radicalism == .5f), "state prefix never changes the neighbour's regions");

        var root = Path.Combine(Path.GetTempPath(), "dof-guide-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(root, "common", "scripted_effects"));
        try
        {
            File.WriteAllText(Path.Combine(root, "common", "scripted_effects", "90_guide.yml"),
                "guide_double:\n  treasury_mult: 2\n");
            var errors = new List<string>(); ScriptLibrary.LoadDirectories(root, errors);
            Check(errors.Count == 0, "named YAML library loads through the real package reader");
            nation.Treasury = 100;
            Effect("use: [guide_double]\ntreasury_set: 5\n").Apply(c, nation);
            Check(nation.Treasury == 5, "use first lets a later set override its numeric result");
            nation.Treasury = 100;
            Effect("treasury_set: 5\nuse: [guide_double]\n").Apply(c, nation);
            Check(nation.Treasury == 10, "use expands in place instead of being silently moved to the front");
            nation.Treasury = 100;
            Effect("{\"treasury_set\":5,\"use\":[\"guide_double\"]}", "events/guide.json").Apply(c, nation);
            Check(nation.Treasury == 10, "JSON and YAML preserve the same effect ordering");
        }
        finally
        {
            ScriptLibrary.ResetCatalog();
            // Exact, owned GUID directory, never a mod directory or a user's workspace.
            Directory.Delete(root, recursive: true);
        }

        using (var document = StructuredData.ParseDocument("events/guide.yml", "min_stability: 0.45\nmax_war_support: 0.8\nat_war: false\n"))
        {
            var errors = new List<string>();
            var trigger = ScriptedTrigger.Parse(document.RootElement, "guide trigger", errors);
            nation.Stability = .5f; nation.WarSupport = .75f;
            var before = CampaignFingerprint.Of(c); var random = c.Random.State;
            Check(errors.Count == 0 && trigger.Matches(c, nation), "all numeric bounds and the peaceful condition hold together");
            Check(before == CampaignFingerprint.Of(c) && random == c.Random.State, "trigger evaluation reads without spending or drawing randomness");
            nation.WarSupport = .9f;
            Check(!trigger.Matches(c, nation), "one failed bound rejects the trigger");
        }
        using (var document = StructuredData.ParseDocument("events/guide.yml", "not_a_real_effect: 1\n"))
        {
            var errors = new List<string>();
            var bad = ScriptedEffects.Parse(document.RootElement, "events/guide.yml", errors);
            Check(bad.IsEmpty && errors.Any(e => e.Contains("events/guide.yml") && e.Contains("not_a_real_effect")),
                "unknown fields report their content path instead of acting as successful examples");
        }
        nation.Treasury = 100;
        var queued = ModOperations.QueueDebt(c, nation.Tag, "borrow", .1);
        Check(queued && c.Commands.Count == 1 && nation.Treasury == 100, "operation true means queued, not already paid or applied");
        Check(ModScalarAccess.SetNationNumber(c, nation.Tag, "treasury", 12) && nation.Treasury == 12,
            "legacy scalar setter is immediate, unlike the queued operation");
        Check(!ModScalarAccess.SetNationNumber(c, nation.Tag, "treasury", double.NaN) && nation.Treasury == 12,
            "a rejected scalar does not partially write an invalid number");

        VerifyShippedTemplates();
    }

    private static void VerifyShippedTemplates()
    {
        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var repoRoot = Path.GetDirectoryName(gameRoot)!;
        var templatesDir = Path.Combine(repoRoot, "mods", "_templates");
        var temp = Path.Combine(Path.GetTempPath(), "dof-templates-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(temp);
        try
        {
            // Stage A2 campaign-reaching templates
            RunCampaignTemplate(temp, templatesDir, "politics", "example_politics", (ignored, pkg) =>
            {
                Check(pkg.Errors.Count == 0, "template politics loads without errors");
                Check(GovernmentCatalog.Knows("corporate_state") && IdeologyCatalog.Knows("syndicalism"),
                    "template politics registers new government and ideology");
            });

            RunCampaignTemplate(temp, templatesDir, "central_bank", "example_central_bank", (ignored, pkg) =>
            {
                Check(pkg.Errors.Count == 0, "template central_bank loads without errors");
                Check(Math.Abs(InflationCore.Weights.Base - 0.040f) < 0.001f &&
                      Math.Abs(CentralBank.Rule.NeutralRate - 0.07f) < 0.001f,
                    "template central_bank adjusts inflation weights and central bank neutral rate");
            });

            RunCampaignTemplate(temp, templatesDir, "trade_instructions", "example_trade_instructions", (ignored, pkg) =>
            {
                Check(pkg.Errors.Count == 0, "template trade_instructions loads without errors");
                Check(TradeDesk.Of("strategic_partner") is not null &&
                      Math.Abs((TradeDesk.Of("import_ceiling")?.Minimum ?? 0f) - 0.34f) < 0.001f,
                    "template trade_instructions merges trade controls");
            });

            RunCampaignTemplate(temp, templatesDir, "long_range_forces", "example_long_range_forces", (ignored, pkg) =>
            {
                Check(pkg.Errors.Count == 0, "template long_range_forces loads without errors");
                Check(SupportTypeCatalog.TryGet("loitering_munition_battery", out _),
                    "template long_range_forces registers support formation");
            });

            RunCampaignTemplate(temp, templatesDir, "rename_the_world", "example_rename_the_world", (world, pkg) =>
            {
                Check(pkg.Errors.Count == 0, "template rename_the_world loads without errors");
                Check(world.NationByTag("GBR")?.Name == "Albion" &&
                      world.States.Any(s => s.Id == "it_to" && s.Name == "Augusta Taurinorum"),
                    "template rename_the_world renames nations and states");
            });

            RunCampaignTemplate(temp, templatesDir, "yaml-data", "example_yaml_data", (ignored, pkg) =>
            {
                Check(pkg.Errors.Count == 0, "template yaml-data loads without errors");
                Check(TechnologyCatalog.Of("yaml_research_liaison") is not null &&
                      EventCatalog.All.Any(e => e.Id == "yaml_workshop_bottleneck") &&
                      ProductionProgramme.UnitsPerFactory == 12 &&
                      Loc.Get("yaml.example.message") == "This text was loaded from YAML.",
                    "template yaml-data loads YAML tech, events, programme and localization");
            });

            // Stage A6 presentation, skin, scenario and compiled C# templates
            Check(File.Exists(Path.Combine(templatesDir, "interface_reskin", "interface", "00_skin.json")) &&
                  !File.Exists(Path.Combine(templatesDir, "interface_reskin", "interface", "skin.json")),
                "template interface_reskin uses normalized interface/00_skin.json filename");

            RunPresentationTemplate(temp, templatesDir, "interface_reskin", "example_interface_reskin", pkg =>
            {
                Check(pkg.Errors.Count == 0, "template interface_reskin applies without errors");
                Check(InterfaceSkin.Colour("text.accent") == "#7a4b1e", "template interface_reskin tints skin tokens");
            });

            RunPresentationTemplate(temp, templatesDir, "icons_and_buttons", "example_icons_and_buttons", pkg =>
            {
                Check(pkg.Errors.Count == 0, "template icons_and_buttons applies without errors");
                Check(InterfaceSkin.Colour("icon.category.economy") == "#d4ad54" &&
                      Math.Abs(InterfaceSkin.Metric("button.corner_radius", 0f) - 6f) < 0.01f &&
                      CampaignUiCatalog.Icon("hud.TREASURY", "") == "balance" &&
                      CampaignUiCatalog.IconCategory("hud.GDP") == "state" &&
                      SpriteCatalog.Get("icon_treasury").Tint == "#e6c36b",
                    "template icons_and_buttons overrides icon families, roles, tints and button metrics");
            });

            RunPresentationTemplate(temp, templatesDir, "main_menu", "example_main_menu", pkg =>
            {
                Check(pkg.Errors.Count == 0, "template main_menu applies without errors");
                Check(SpriteCatalog.TextureOf("menu_world") == "res://game/gfx/atmosphere/night_signal.png" &&
                      SpriteCatalog.Get("brand_logo_banner").Tint == "#dfd1b4" &&
                      MusicCatalog.All.Any(t => t.Name == "menu_theme" &&
                                                t.File.EndsWith("afterimage_republic.ogg", StringComparison.Ordinal)) &&
                      InterfaceSkin.Colour("text.accent") == "#e8d9b8",
                    "template main_menu updates menu backdrop sprite, logo tint, theme track and tokens");
            });

            RunPresentationTemplate(temp, templatesDir, "fonts", "example_fonts", pkg =>
            {
                Check(pkg.Errors.Count == 0, "template fonts applies without errors");
                Check(FontCatalog.FamiliesOf("ui.display").FirstOrDefault() == "Palatino Linotype",
                    "template fonts overrides display font family stack");
            });

            RunPresentationTemplate(temp, templatesDir, "sounds", "example_sounds", pkg =>
            {
                Check(pkg.Errors.Count == 0, "template sounds applies without errors");
                Check(SoundCatalog.Has("ui_select") &&
                      SoundCatalog.HighWindFile.EndsWith("mountain_snow.ogg", StringComparison.Ordinal),
                    "template sounds overrides cue and ambience paths");
            });

            VerifyEraSkinTemplate(temp, templatesDir);
            VerifyScenarioMinimalTemplate(temp, templatesDir);
            VerifyCSharpTemplate(templatesDir);
        }
        finally
        {
            new ModLoader().ApplyCampaignData(Campaign.CreateBootstrap(20261118));
            if (Directory.Exists(temp)) Directory.Delete(temp, true);
        }
    }

    private static string StageTemplateCopy(string temp, string templatesDir, string folderName)
    {
        var runRoot = Path.Combine(temp, "run_" + folderName);
        if (Directory.Exists(runRoot)) Directory.Delete(runRoot, true);
        var dst = Path.Combine(runRoot, folderName);
        CopyDirectory(Path.Combine(templatesDir, folderName), dst);
        return runRoot;
    }

    private static void CopyDirectory(string source, string target)
    {
        Directory.CreateDirectory(target);
        foreach (var file in Directory.EnumerateFiles(source))
            File.Copy(file, Path.Combine(target, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.EnumerateDirectories(source))
        {
            var name = Path.GetFileName(dir);
            if (name is "bin" or "obj") continue;
            CopyDirectory(dir, Path.Combine(target, name));
        }
    }

    private static void RunCampaignTemplate(
        string temp, string templatesDir, string folderName, string manifestId, Action<Campaign, ModPackage> verify)
    {
        var root = StageTemplateCopy(temp, templatesDir, folderName);
        var selection = new ContentSelection();
        selection.Mods[manifestId] = true;
        var loader = new ModLoader();
        loader.Discover(new[] { root }, selection, includeDlc: false);
        var pkg = loader.Packages.Single(p => string.Equals(p.Manifest.Id, manifestId, StringComparison.OrdinalIgnoreCase));
        Check(pkg is not null, $"template {folderName} activates via ContentSelection");
        var world = Campaign.CreateBootstrap(20261118);
        loader.ApplyCampaignData(world);
        verify(world, pkg);
    }

    private static void RunPresentationTemplate(
        string temp, string templatesDir, string folderName, string manifestId, Action<ModPackage> verify)
    {
        var root = StageTemplateCopy(temp, templatesDir, folderName);
        var selection = new ContentSelection();
        selection.Mods[manifestId] = true;
        var loader = new ModLoader();
        loader.Discover(new[] { root }, selection, includeDlc: false);
        var pkg = loader.Packages.Single(p => string.Equals(p.Manifest.Id, manifestId, StringComparison.OrdinalIgnoreCase));
        Check(pkg is not null, $"template {folderName} activates via ContentSelection");
        loader.ApplyPresentation();
        verify(pkg);
    }

    private static void VerifyEraSkinTemplate(string temp, string templatesDir)
    {
        var root = StageTemplateCopy(temp, templatesDir, "era_skin");
        var selection = new ContentSelection();
        selection.Mods["example_era_skin"] = true;
        var loader = new ModLoader();
        loader.Discover(new[] { root }, selection, includeDlc: false);
        var pkg = loader.Packages.Single(p => p.Manifest.Id == "example_era_skin");
        Check(pkg is not null && loader.Packages.Any(), "template era_skin discovered cleanly");
        var baseSkins = Path.Combine(GameDataLoader.DataFolder(), "skins");
        Check(SkinOverlays.Discover(baseSkins, new[] { pkg.PhysicalRoot }).Contains("bronze_atlas"),
            "template era_skin exposes bronze_atlas overlay id");
        InterfaceSkin.ResetCatalog();
        SpriteCatalog.Load();
        var errors = new List<string>();
        var applied = SkinOverlays.Apply("bronze_atlas", baseSkins, new[] { (pkg.PhysicalRoot, pkg.VirtualRoot) }, errors);
        Check(applied == "bronze_atlas" && errors.Count == 0 &&
              InterfaceSkin.Colour("material.plate.top") == "#4b3d2c" &&
              CampaignUiCatalog.IconCategory("hud.GDP") == "state",
            "template era_skin bronze_atlas applies material and icon category overrides");
    }

    private static void VerifyScenarioMinimalTemplate(string temp, string templatesDir)
    {
        var root = StageTemplateCopy(temp, templatesDir, "scenario_minimal");
        var pkgDir = Path.Combine(root, "scenario_minimal");
        var found = ScenarioLibrary.Discover(new[] { pkgDir }, null);
        var entry = found.FirstOrDefault(e => e.Id == "minimal_sandbox");
        Check(entry is not null, "template scenario_minimal discovers minimal_sandbox");
        var created = ScenarioLibrary.Create(entry!, 73);
        Check(created.NationByTag("ALF")?.Name == "Alpha" && created.States.Any(s => s.Id == "alf_01"),
            "template scenario_minimal boots playable minimal world via ScenarioLibrary.Create");
    }

    private static void VerifyCSharpTemplate(string templatesDir)
    {
        var dir = Path.Combine(templatesDir, "csharp_mod");
        var manifest = File.ReadAllText(Path.Combine(dir, "manifest.json"));
        var csproj = File.ReadAllText(Path.Combine(dir, "corecode", "ExampleCSharpMod.csproj"));
        var readme = File.ReadAllText(Path.Combine(dir, "README.md"));
        Check(manifest.Contains("net10.0", StringComparison.Ordinal) &&
              csproj.Contains("<TargetFramework>net10.0</TargetFramework>", StringComparison.Ordinal) &&
              readme.Contains("net10.0", StringComparison.Ordinal),
            "template csharp_mod consistently targets net10.0");
    }
}
