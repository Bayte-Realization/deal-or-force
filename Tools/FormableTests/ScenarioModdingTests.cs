using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Modding;
using DealOrForce.Simulation;

/// <summary>
/// Proves the multi-era &amp; total-conversion modding contract:
/// 1. Base 2024 campaigns, saves, and fingerprints are 100% unchanged when no scenario mod is active.
/// 2. Custom CalendarProfile (era labels, year offsets, 12 custom months, EN/RU localization) works
///    while keeping internal Gregorian GameDate/ElapsedHours deterministic, and rejects invalid dates.
/// 3. Scoped VFS layering (replace_paths / exclude_paths / total_conversion) suppresses 2024 data
///    cleanly and round-trips 2024 -> Total Conversion -> 2024 in the same process with zero leaks.
/// 4. Both showcase templates (historical_1914_era and chronicles_of_aethelgard) pass ModValidator
///    with 0 errors &amp; 0 warnings, load cleanly, tick 30+ days with 0 crashes/NaNs, and round-trip
///    through SaveSystem with identical CampaignFingerprint.
/// 5. ModValidator catches broken syntax, bad dates, invalid calendars, unknown tags, diplomacy
///    conflicts, era-feature conflicts, missing tech prereqs/cycles, unknown effects, disabled
///    feature refs, EN/RU loc mismatches, dependency cycles, and replace_paths conflicts.
/// </summary>
static class ScenarioModdingTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL scenario modding: " + label);
        Console.WriteLine("PASS scenario modding: " + label);
    }

    [TestSuite("--validate-mod", IncludeInDefaultRun = false,
        Description = "Validate one or more mod directories with pure-C# ModValidator (used by Tools/validate-mod.ps1).")]
    public static void ValidateModCli(string[] args)
    {
        var paths = new List<string>();
        var strict = false;
        for (var i = 0; i < args.Length; i++)
        {
            var a = args[i];
            if (string.Equals(a, "--validate-mod", StringComparison.OrdinalIgnoreCase)) continue;
            if (string.Equals(a, "--strict", StringComparison.OrdinalIgnoreCase)) { strict = true; continue; }
            if (!a.StartsWith("--", StringComparison.Ordinal)) paths.Add(a);
        }

        if (paths.Count == 0)
        {
            Console.Error.WriteLine("Usage: --validate-mod <modDir> [<modDir2> ...] [--strict]");
            Environment.ExitCode = 2;
            return;
        }

        var report = paths.Count == 1
            ? ModValidator.ValidateMod(paths[0])
            : ModValidator.ValidateModSet(paths);

        Console.WriteLine(report.FormatSummary());
        var failed = !report.IsValid || (strict && report.WarningCount > 0);
        Environment.ExitCode = failed ? 1 : 0;
    }

    [TestSuite("--scenario-modding-only", Order = 2630, IncludeInDefaultRun = true,
        Description = "Multi-era scenarios, CalendarProfile, VFS replace_paths/exclude_paths, ModJsonPatcher, MOD-02 scripted effects, ModValidator, and starter templates.")]
    public static void Run()
    {
        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var repoRoot = Path.GetDirectoryName(gameRoot)!;
        var templatesDir = Path.Combine(repoRoot, "mods", "_templates");

        try
        {
            TestBase2024Unchanged();
            TestCalendarProfileAndStrictDates();
            TestOverlayPolicySafetyAndExclude();
            TestHistorical1914Template(templatesDir);
            TestAethelgardTotalConversionAndRoundTrip(templatesDir);
            TestNegativeModValidatorSuite();
            TestMultiModPatchStackingAndStages();
            TestInlineOpInjectReplaceCreate();
            TestMod02ScriptedEffectsAndTriggers();
            TestPatchUnloadRestoresBase2024Fingerprint();
        }
        finally
        {
            // Restore clean base-game catalogs in case any test left a mod active.
            ModOverlayPolicy.Reset();
            ModJsonPatcher.Reset();
            var resetLoader = new ModLoader();
            resetLoader.Discover(Array.Empty<string>(), new ContentSelection(), includeDlc: false);
            resetLoader.ApplyCampaignData(Campaign.CreateBootstrap(20261121));
        }
    }

    private static void TestBase2024Unchanged()
    {
        ModOverlayPolicy.Reset();
        var entry = ScenarioLibrary.Discover(Array.Empty<string>(), "").FirstOrDefault(e => e.Id == "2024");
        Check(entry is not null, "base 2024 scenario is discovered");
        var a = ScenarioLibrary.Create(entry!, 20261121);
        var b = ScenarioLibrary.Create(entry!, 20261121);

        Check(!ModOverlayPolicy.IsActive, "base 2024 keeps ModOverlayPolicy inactive");
        Check(a.Calendar is null, "base 2024 leaves Campaign.Calendar null");
        Check(a.ScenarioMeta is null, "base 2024 leaves Campaign.ScenarioMeta null");
        Check(a.RestrictedFeatures is null, "base 2024 leaves Campaign.RestrictedFeatures null");
        Check(a.DisplayDate() == a.Date.Display(), "base 2024 DisplayDate matches legacy GameDate.Display");
        Check(CampaignFingerprint.Of(a) == CampaignFingerprint.Of(b), "base 2024 fingerprints are deterministic");

        // Ensure default 2024 save JSON omits Calendar / ScenarioMeta / RestrictedFeatures completely.
        var rack = ExclaveSupplyTests.Rack();
        var json = SaveSystem.ExportScenarioJson(rack, "2024");
        Check(!json.Contains("\"Calendar\"", StringComparison.Ordinal), "base 2024 save omits Calendar field");
        Check(!json.Contains("\"ScenarioMeta\"", StringComparison.Ordinal), "base 2024 save omits ScenarioMeta field");
        Check(!json.Contains("\"RestrictedFeatures\"", StringComparison.Ordinal), "base 2024 save omits RestrictedFeatures field");
    }

    private static void TestCalendarProfileAndStrictDates()
    {
        // Sci-Fi year offset + era suffix
        var scifi = new CalendarProfile
        {
            CalendarId = "galactic_common",
            EraName = "Galactic Common",
            EraShort = "G.C.",
            YearOffset = 0,
            DateFormat = "{day} {month} {year} {era_short}",
            ShowEraInDate = true
        };
        Check(scifi.Validate().Count == 0, "valid sci-fi CalendarProfile validates cleanly");
        var scifiDate = new GameDate(2240, 5, 19);
        Check(scifi.FormatDate(scifiDate, withTime: false, localized: false) == "19 May 2240 G.C.",
            "sci-fi calendar formats date cleanly");
        Check(scifi.FormatDate(new GameDate(2240, 5, 19) { Hour = 14 }, withTime: true, localized: false) == "19 May 2240 G.C., 14:00",
            "sci-fi calendar preserves hour display when requested");

        // Fantasy 12-month calendar
        var fantasy = new CalendarProfile
        {
            CalendarId = "third_sun",
            EraName = "Third Sun",
            EraShort = "3S",
            DateFormat = "{day} {month}, Year {year} of the {era}",
            YearFormat = "Year {year} of the {era}",
            MonthNames = new[]
            {
                "Frostfall", "Deepwinter", "Thawmoon", "Blossomtide", "Suncrest", "Highsummer",
                "Goldharvest", "Emberwane", "Mistveil", "Galewatch", "Duskfrost", "Starquiet"
            }
        };
        Check(fantasy.Validate().Count == 0, "valid 12-month fantasy CalendarProfile validates cleanly");
        Check(fantasy.FormatDate(new GameDate(402, 3, 14), withTime: false, localized: false) == "14 Thawmoon, Year 402 of the Third Sun",
            "fantasy calendar formats custom month and era template");
        Check(fantasy.FormatYear(402, localized: false) == "Year 402 of the Third Sun",
            "fantasy calendar formats YearFormat cleanly");

        // Invalid month_names count rejected
        var brokenCal = new CalendarProfile { MonthNames = new[] { "One", "Two" } };
        Check(brokenCal.Validate().Count > 0, "CalendarProfile rejects month_names with length != 12");

        // Strict date parsing rejects invalid dates instead of silently rolling
        var badFeb = new ScenarioManifest { Id = "bad", StartYear = 1914, StartMonth = 2, StartDay = 30 };
        Check(!badFeb.TryResolveStartDate(out _, out var errFeb) && errFeb.Length > 0,
            "ScenarioManifest strictly rejects 1914-02-30");
        var badZero = new ScenarioManifest { Id = "bad0", StartDateRaw = "0000-01-01 BCE" };
        Check(!badZero.TryResolveStartDate(out _, out _),
            "ScenarioManifest strictly rejects civil year 0 BCE");
        var goodBce = new ScenarioManifest { Id = "bce", StartDateRaw = "0490-09-12 BCE" };
        Check(goodBce.TryResolveStartDate(out var parsedBce, out _) && parsedBce.Year == -489 && parsedBce.Month == 9 && parsedBce.Day == 12,
            "ScenarioManifest resolves civil BCE dates into astronomical years");

        // EraFeatureRegistry alias expansion and conflict rejection
        var featErrors = new List<string>();
        var resolved = EraFeatureRegistry.Resolve(
            new[] { "nukes", "modern_air_force", "drones" }, // overlapping drones coalesces cleanly
            new Dictionary<string, string> { ["missiles"] = "restricted" },
            featErrors,
            requireKnownBuiltIn: true);
        Check(featErrors.Count == 0, "overlapping identical alias expansions coalesce cleanly");
        Check(resolved["defcon"] == EraFeatureState.Unavailable &&
              resolved["nuclear_strikes"] == EraFeatureState.Unavailable &&
              resolved["aviation"] == EraFeatureState.Unavailable &&
              resolved["drones"] == EraFeatureState.Unavailable &&
              resolved["long_range_forces"] == EraFeatureState.Restricted,
            "EraFeatureRegistry expands nukes/modern_air_force/missiles into canonical MechanicFeatures");

        var conflictErrors = new List<string>();
        EraFeatureRegistry.Resolve(
            new[] { "modern_air_force" },
            new Dictionary<string, string> { ["drones"] = "available" },
            conflictErrors,
            requireKnownBuiltIn: true);
        Check(conflictErrors.Any(e => e.Contains("Conflicting", StringComparison.OrdinalIgnoreCase)),
            "EraFeatureRegistry flags contradictory feature states as an error");
    }

    private static void TestOverlayPolicySafetyAndExclude()
    {
        Check(!ModOverlayPolicy.TryNormalizePath("../secrets", true, out _, out _),
            "ModOverlayPolicy rejects path traversal");
        Check(!ModOverlayPolicy.TryNormalizePath("Scripts/Simulation", true, out _, out _),
            "ModOverlayPolicy rejects non-content root in replace_paths");
        Check(ModOverlayPolicy.TryNormalizePath("common/technologies", true, out var norm, out _) && norm == "common/technologies",
            "ModOverlayPolicy normalizes allowed replace_paths root");

        var baseTechCount = GameDataLoader.DataFiles("common", "technologies").Count;
        Check(baseTechCount > 0, "base game ships technology files");

        using (ModOverlayPolicy.PushScope(new[]
               {
                   new ModLayerSpec("scope_test", false, new[] { "common/technologies" }, new[] { "events/00_events.json" })
               }, out var scopeErrors))
        {
            Check(scopeErrors.Count == 0 && ModOverlayPolicy.IsActive, "PushScope activates cleanly");
            Check(GameDataLoader.DataFiles("common", "technologies").Count == 0,
                "replace_paths suppresses base common/technologies files");
            Check(ModOverlayPolicy.IsBaseFileExcluded("events/00_events.json"),
                "exclude_paths suppresses targeted base file");
            Check(ModOverlayPolicy.Provenance.Any(p => p.VirtualPath == "common/technologies" && p.OwnerModId == "scope_test"),
                "ModOverlayPolicy records provenance for replaced root");
        }
        Check(!ModOverlayPolicy.IsActive && GameDataLoader.DataFiles("common", "technologies").Count == baseTechCount,
            "disposing PushScope restores base DataFiles cleanly");
    }

    private static void TestHistorical1914Template(string templatesDir)
    {
        var templateDir = Path.Combine(templatesDir, "historical_1914_era");
        Check(Directory.Exists(templateDir), "historical_1914_era template exists");

        // 1. Pre-flight ModValidator: 0 errors & 0 warnings
        var report = ModValidator.ValidateMod(templateDir);
        Check(report.IsValid && report.WarningCount == 0,
            $"historical_1914_era passes ModValidator cleanly (errors={report.ErrorCount}, warnings={report.WarningCount}): {report.FormatSummary()}");

        // 2. Discover & launch via ModLoader + ScenarioLibrary (using base_world: true, no world.dfsave!)
        var tempRoot = Path.Combine(Path.GetTempPath(), "dof-1914-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            var pkgDir = CopyTemplateEnabled(templateDir, tempRoot, "historical_1914_era");
            var selection = new ContentSelection();
            selection.Mods["example_historical_1914"] = true;
            var loader = new ModLoader();
            loader.Discover(new[] { tempRoot }, selection, includeDlc: false);
            Check(loader.EnabledCount == 1, "historical_1914_era discovered by ModLoader");

            var entry = ScenarioLibrary.Discover(new[] { pkgDir }, null)
                .FirstOrDefault(e => e.Id == "great_war_1914");
            Check(entry is not null && entry.WorldFile.Length == 0,
                "great_war_1914 discovered via base_world: true without needing world.dfsave");

            var campaign = ScenarioLibrary.Create(entry!, 19140728);
            loader.ApplyCampaignData(campaign);
            var pkg = loader.Packages.First(p => p.Manifest.Id == "example_historical_1914");
            Check(pkg.Errors.Count == 0,
                $"historical_1914_era applies with 0 package errors (got: {string.Join("; ", pkg.Errors)})");

            // Check custom date, calendar, features, diplomacy, wars, and replaced catalogs!
            Check(campaign.Date.Year == 1914 && campaign.Date.Month == 7 && campaign.Date.Day == 28,
                "great_war_1914 starts on 1914-07-28");
            Check(campaign.Player?.Tag == "DEU", "great_war_1914 opens on DEU");
            Check(campaign.Calendar is not null && campaign.Calendar.EraName == "Great War Era",
                "great_war_1914 attaches Great War Era calendar");
            Check(campaign.DisplayDate().Contains("1914", StringComparison.Ordinal),
                $"great_war_1914 DisplayDate renders cleanly ('{campaign.DisplayDate()}')");

            Check(!campaign.FeatureEnabled("defcon") &&
                  !campaign.FeatureEnabled("nuclear_posture") &&
                  !campaign.FeatureEnabled("nuclear_strikes") &&
                  !campaign.FeatureEnabled("drones") &&
                  !campaign.FeatureEnabled("long_range_forces"),
                "great_war_1914 disables nukes, drones, and missiles");
            Check(!campaign.FeatureEnabled("aviation") &&
                  campaign.RestrictedFeatures is not null &&
                  campaign.RestrictedFeatures.Contains("aviation"),
                "great_war_1914 marks aviation as restricted");

            Check(TechnologyCatalog.Of("reconnaissance_biplanes") is not null &&
                  TechnologyCatalog.Of("artificial_intelligence") is null,
                "historical_1914_era replace_paths replaces 2024 technologies with 1914 tech tree");
            Check(DecisionCatalog.All.Any(d => d.Id == "war_bonds_1914") &&
                  DecisionCatalog.All.All(d => d.Id != "economic_stimulus"),
                "historical_1914_era replace_paths replaces 2024 decisions including compiled defaults");

            Check(campaign.Wars.Any(w => !w.Ended && w.Name == "The Great War"),
                "great_war_1914 seeds The Great War and clears 2024 Russo-Ukrainian war");
            Check(!campaign.Wars.Any(w => w.Name == "Russo-Ukrainian War"),
                "clear_base_wars removes base 2024 war cleanly");

            var gbr = campaign.NationByTag("GBR");
            var usa = campaign.NationByTag("USA");
            Check(gbr is not null && usa is not null &&
                  CampaignDiplomacy.Get(campaign, gbr.Id, usa.Id).TruceUntil >= 365,
                "great_war_1914 seeds 365-day GBR-USA truce");

            // Tick 30+ days cleanly with zero crashes / NaNs
            TickAndVerifyHealthy(campaign, 32, "historical_1914_era");

            // Save/load round-trip preserves Calendar, ScenarioMeta, RestrictedFeatures & CampaignFingerprint!
            var topo1914 = BuildMinimalTopologyFor(campaign);
            campaign.AttachProvinceTopology(topo1914);
            var fpBefore = CampaignFingerprint.Of(campaign);
            var saveDir = Path.Combine(tempRoot, "saves_1914");
            SaveSystem.Save(saveDir, "slot1914", campaign, "test_1914");
            var loaded = SaveSystem.Load(saveDir, "slot1914", "test_1914", topo1914.ProvinceCount).Campaign;
            Check(loaded is not null, "great_war_1914 save/load succeeds");
            Check(loaded!.Date.Year == campaign.Date.Year && loaded.Date.Month == campaign.Date.Month && loaded.Date.Day == campaign.Date.Day,
                "great_war_1914 save preserves 1914 date");
            Check(loaded.Calendar is not null && loaded.Calendar.EqualsProfile(campaign.Calendar),
                "great_war_1914 save preserves CalendarProfile");
            Check(loaded.RestrictedFeatures is not null && loaded.RestrictedFeatures.Contains("aviation"),
                "great_war_1914 save preserves RestrictedFeatures");
            Check(loaded.ScenarioMeta is not null && loaded.ScenarioMeta.MusicPlaylist == "great_war_orchestra",
                "great_war_1914 save preserves ScenarioMeta music/theme hooks");
            Check(CampaignFingerprint.Of(loaded) == fpBefore,
                "great_war_1914 save/load preserves identical CampaignFingerprint");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static void TestAethelgardTotalConversionAndRoundTrip(string templatesDir)
    {
        var templateDir = Path.Combine(templatesDir, "chronicles_of_aethelgard");
        Check(Directory.Exists(templateDir), "chronicles_of_aethelgard template exists");

        // 1. Pre-flight ModValidator: 0 errors & 0 warnings
        var report = ModValidator.ValidateMod(templateDir);
        Check(report.IsValid && report.WarningCount == 0,
            $"chronicles_of_aethelgard passes ModValidator cleanly (errors={report.ErrorCount}, warnings={report.WarningCount}): {report.FormatSummary()}");

        var tempRoot = Path.Combine(Path.GetTempPath(), "dof-aethelgard-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        var prevLang = Loc.Language;
        try
        {
            var pkgDir = CopyTemplateEnabled(templateDir, tempRoot, "chronicles_of_aethelgard");
            var selection = new ContentSelection();
            selection.Mods["example_chronicles_of_aethelgard"] = true;
            var loader = new ModLoader();
            loader.Discover(new[] { tempRoot }, selection, includeDlc: false);

            var entry = ScenarioLibrary.Discover(new[] { pkgDir }, null)
                .FirstOrDefault(e => e.Id == "aethelgard_402");
            Check(entry is not null, "aethelgard_402 scenario discovered");

            var campaign = ScenarioLibrary.Create(entry!, 4020314, expectedProvinceCount: 3);
            loader.ApplyCampaignData(campaign);
            var pkg = loader.Packages.First(p => p.Manifest.Id == "example_chronicles_of_aethelgard");
            Check(pkg.Errors.Count == 0,
                $"chronicles_of_aethelgard applies with 0 package errors (got: {string.Join("; ", pkg.Errors)})");

            // Verify custom fantasy calendar in both EN and RU!
            Loc.Language = "english";
            Check(campaign.DisplayDate() == "14 Thawmoon, Year 402 of the Third Sun",
                $"Aethelgard EN custom calendar renders cleanly ('{campaign.DisplayDate()}')");
            Loc.Language = "russian";
            Check(campaign.DisplayDate() == "14 Талолунь, год 402 Третьего Солнца",
                $"Aethelgard RU custom calendar renders cleanly ('{campaign.DisplayDate()}')");

            // Verify total-conversion replace_paths suppressed base 2024 tech/decisions/ideologies/governments/events!
            Check(TechnologyCatalog.Of("guild_metallurgy") is not null &&
                  TechnologyCatalog.Of("industrial_automation") is null,
                "Aethelgard replaces base 2024 technologies completely");
            Check(IdeologyCatalog.Knows("chivalric_order") && !IdeologyCatalog.Knows("liberalism"),
                "Aethelgard replaces base 2024 ideologies completely");
            Check(GovernmentCatalog.Knows("high_monarchy") && !GovernmentCatalog.Knows("parliamentary_republic"),
                "Aethelgard replaces base 2024 governments completely");
            Check(EventCatalog.All.Any(e => e.Id == "comet_of_third_sun") &&
                  EventCatalog.All.All(e => !e.Id.Contains("ukraine", StringComparison.OrdinalIgnoreCase)),
                "Aethelgard replaces base 2024 events completely");
            Check(!campaign.FeatureEnabled("defcon") &&
                  !campaign.FeatureEnabled("aviation") &&
                  !campaign.FeatureEnabled("drones") &&
                  !campaign.FeatureEnabled("long_range_forces") &&
                  !campaign.FeatureEnabled("intelligence"),
                "Aethelgard disables nukes, air force, missiles, drones, and modern intelligence");

            // Verify deterministic 35-day simulation soak across two independent runs under identical catalog state!
            Loc.Language = "english";
            var run1 = ScenarioLibrary.Create(entry!, 4020314, expectedProvinceCount: 3);
            loader.ApplyCampaignData(run1);
            var run2 = ScenarioLibrary.Create(entry!, 4020314, expectedProvinceCount: 3);
            loader.ApplyCampaignData(run2);
            Check(CampaignFingerprint.Of(run1) == CampaignFingerprint.Of(run2),
                "Aethelgard day-0 state is 100% identical across fresh runs");

            TickAndVerifyHealthy(run1, 35, "chronicles_of_aethelgard_run1");
            TickAndVerifyHealthy(run2, 35, "chronicles_of_aethelgard_run2");
            var fpRun1 = CampaignFingerprint.Of(run1);
            Check(CampaignFingerprint.Of(run2) == fpRun1,
                "Aethelgard 35-day soak is 100% deterministic across runs");

            // Save/load round-trip preserves fantasy 12-month calendar + ScenarioMeta + CampaignFingerprint!
            var topoAethelgard = ProvinceTopology.Build(new ushort[] { 1, 2, 3 }, new ushort[] { 1, 2, 3 }, 3, 1, _ => false);
            run1.AttachProvinceTopology(topoAethelgard);
            var fpSaved = CampaignFingerprint.Of(run1);
            var saveDir = Path.Combine(tempRoot, "saves_aethelgard");
            SaveSystem.Save(saveDir, "slot_aethelgard", run1, "test_aethelgard");
            var loaded = SaveSystem.Load(saveDir, "slot_aethelgard", "test_aethelgard", topoAethelgard.ProvinceCount).Campaign;
            Check(loaded is not null && loaded.Calendar is not null && loaded.Calendar.EqualsProfile(run1.Calendar),
                "Aethelgard save/load preserves 12-month CalendarProfile");
            Check(loaded!.ScenarioMeta is not null && loaded.ScenarioMeta.TotalConversion,
                "Aethelgard save/load preserves ScenarioMeta.TotalConversion");
            Check(CampaignFingerprint.Of(loaded) == fpSaved,
                "Aethelgard save/load preserves identical CampaignFingerprint");

            // Process round-trip test (2024 -> Aethelgard -> 2024): verify zero leaked state!
            var cleanLoader = new ModLoader();
            cleanLoader.Discover(Array.Empty<string>(), new ContentSelection(), includeDlc: false);
            var restored2024 = Campaign.CreateBootstrap(20261121);
            cleanLoader.ApplyCampaignData(restored2024);
            Check(!ModOverlayPolicy.IsActive, "returning to base 2024 deactivates ModOverlayPolicy");
            Check(TechnologyCatalog.Of("industrial_automation") is not null &&
                  TechnologyCatalog.Of("guild_metallurgy") is null,
                "returning to base 2024 restores 2024 technologies and drops Aethelgard technologies");
            Check(IdeologyCatalog.Knows("liberalism") && !IdeologyCatalog.Knows("chivalric_order"),
                "returning to base 2024 restores 2024 ideologies cleanly");
            Check(GovernmentCatalog.Knows("parliamentary_republic") && !GovernmentCatalog.Knows("high_monarchy"),
                "returning to base 2024 restores 2024 governments cleanly");
            Check(DecisionCatalog.All.Any(d => d.Id == "economic_stimulus") &&
                  DecisionCatalog.All.All(d => d.Id != "summon_royal_bannermen"),
                "returning to base 2024 restores 2024 decisions cleanly");
        }
        finally
        {
            Loc.Language = prevLang;
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static void TestNegativeModValidatorSuite()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "dof-negval-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempRoot);
        try
        {
            // Mod A: intentionally packed with beginner mistakes across every category
            var modA = Path.Combine(tempRoot, "broken_mod_a");
            Directory.CreateDirectory(Path.Combine(modA, "scenarios", "broken"));
            Directory.CreateDirectory(Path.Combine(modA, "common", "technologies"));
            Directory.CreateDirectory(Path.Combine(modA, "events"));
            Directory.CreateDirectory(Path.Combine(modA, "localization", "english"));
            Directory.CreateDirectory(Path.Combine(modA, "localization", "russian"));

            File.WriteAllText(Path.Combine(modA, "manifest.json"), """
            {
              "id": "broken_mod_a",
              "name": "Broken Mod A",
              "version": "1.0.0",
              "dependencies": ["broken_mod_b"],
              "replace_paths": ["../escape", "common/technologies"],
              "disabled_features": ["modern_air_force"],
              "era_features": { "drones": "available" }
            }
            """);

            File.WriteAllText(Path.Combine(modA, "scenarios", "broken", "scenario.json"), """
            {
              "id": "broken_scen",
              "name": "Broken Scenario",
              "base_world": true,
              "player_country": "ZZZ",
              "start_year": 1914,
              "start_month": 2,
              "start_day": 30,
              "calendar": { "era_name": "Bad", "month_names": ["OnlyOne"] },
              "starting_wars": [{ "attacker": "DEU", "defender": "FRA" }],
              "starting_alliances": [{ "name": "Contradiction", "members": ["DEU", "FRA"] }],
              "starting_truces": [{ "between": ["DEU", "FRA"], "days": 100 }]
            }
            """);

            File.WriteAllText(Path.Combine(modA, "common", "technologies", "50_bad_tech.json"), """
            {
              "tech_alpha": { "name": "Alpha", "requires": ["missing_prereq", "tech_beta"] },
              "tech_beta": { "name": "Beta", "requires": ["tech_alpha"] }
            }
            """);

            File.WriteAllText(Path.Combine(modA, "events", "50_bad_event.json"), """
            {
              "bad_evt": {
                "title": "Bad",
                "date": "1914-02-30",
                "trigger": { "min_feature.aviation": 1 },
                "effects": { "totally_made_up_effect": 5 }
              }
            }
            """);

            File.WriteAllText(Path.Combine(modA, "events", "51_syntax_error.json"), "{ invalid json ");

            File.WriteAllText(Path.Combine(modA, "localization", "english", "50_loc.json"), """
            {
              "only_en_key": "Only English",
              "fmt_key": "Cost: {0} gold"
            }
            """);

            File.WriteAllText(Path.Combine(modA, "localization", "russian", "50_loc.json"), """
            {
              "fmt_key": "Цена без плейсхолдера"
            }
            """);

            // Mod B: creates a dependency cycle with Mod A + replaces common/technologies without Mod C
            var modB = Path.Combine(tempRoot, "broken_mod_b");
            Directory.CreateDirectory(modB);
            File.WriteAllText(Path.Combine(modB, "manifest.json"), """
            {
              "id": "broken_mod_b",
              "name": "Broken Mod B",
              "version": "1.0.0",
              "dependencies": ["broken_mod_a"]
            }
            """);

            // Mod C: uncoordinated replace_paths conflict with Mod A on common/technologies
            var modC = Path.Combine(tempRoot, "broken_mod_c");
            Directory.CreateDirectory(modC);
            File.WriteAllText(Path.Combine(modC, "manifest.json"), """
            {
              "id": "broken_mod_c",
              "name": "Broken Mod C",
              "version": "1.0.0",
              "replace_paths": ["common/technologies"]
            }
            """);

            var single = ModValidator.ValidateMod(modA);
            Check(!single.IsValid, "broken mod fails ModValidator");
            Check(HasCode(single, "ERR_INVALID_REPLACE_PATH"), "catches unsafe replace_paths traversal");
            Check(HasCode(single, "ERR_FEATURE_CONFLICT"), "catches conflicting era_features vs disabled_features");
            Check(HasCode(single, "ERR_INVALID_DATE"), "catches invalid 1914-02-30 start/event date");
            Check(HasCode(single, "ERR_INVALID_CALENDAR"), "catches calendar with != 12 month names");
            Check(HasCode(single, "ERR_UNKNOWN_COUNTRY_TAG"), "catches unknown ZZZ country tag");
            Check(HasCode(single, "ERR_DIPLOMACY_CONFLICT"), "catches war+alliance and war+truce contradictions");
            Check(HasCode(single, "ERR_TECH_MISSING_PREREQ"), "catches missing technology prerequisite");
            Check(HasCode(single, "ERR_TECH_CYCLE"), "catches circular technology prerequisites");
            Check(HasCode(single, "ERR_UNKNOWN_EFFECT"), "catches unknown scripted effect key");
            Check(HasCode(single, "WARN_DISABLED_FEATURE_REF"), "warns on event referencing disabled era feature");
            Check(HasCode(single, "ERR_DATA_SYNTAX"), "catches malformed JSON syntax");
            Check(HasCode(single, "WARN_LOC_MISSING_KEY"), "warns on missing Russian translation key");
            Check(HasCode(single, "ERR_LOC_PLACEHOLDER_MISMATCH"), "catches dropped {0} format placeholder");
            Check(single.Diagnostics.All(d => !string.IsNullOrWhiteSpace(d.SuggestedFix)),
                "every ModValidator diagnostic provides an actionable SuggestedFix");

            var setReport = ModValidator.ValidateModSet(new[] { modA, modB, modC });
            Check(HasCode(setReport, "ERR_DEPENDENCY_CYCLE"), "ValidateModSet detects circular mod dependencies");
            Check(HasCode(setReport, "WARN_REPLACE_PATH_CONFLICT"), "ValidateModSet warns on uncoordinated replace_paths conflict");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static void TestMultiModPatchStackingAndStages()
    {
        // Base JSON document simulating common/technologies
        var doc = JsonNode.Parse("""
        {
          "alpha_tech": {
            "name": "Alpha Tech",
            "cost": 100,
            "requires": ["root_tech"],
            "effects": { "output": 0.10 }
          },
          "obsolete_tech": {
            "name": "Obsolete",
            "cost": 50
          }
        }
        """)!.AsObject();

        var parseDiags = new List<ModDiagnostic>();
        // Mod 0 declares a final_fixes patch; Mod 1 declares a base patch and an updates patch.
        // Stage order MUST run base -> updates -> final_fixes regardless of mod load order!
        var mod0Final = ModJsonPatcher.ParsePatchJson("""
        {
          "stage": "final_fixes",
          "category": "common/technologies",
          "operations": [
            { "op": "multiply", "target": "alpha_tech", "path": "cost", "value": 2.0 },
            { "op": "remove", "target": "obsolete_tech" }
          ]
        }
        """, "patches/99_final.json", "mod_alpha", 0, parseDiags);

        var mod1BaseAndUpdates = ModJsonPatcher.ParsePatchJson("""
        {
          "patches": [
            {
              "stage": "base",
              "category": "common/technologies",
              "operations": [
                { "op": "add", "target": "alpha_tech", "path": "cost", "value": 20 },
                { "op": "append", "target": "alpha_tech", "path": "requires", "value": "optics", "unique": true },
                { "op": "append", "target": "alpha_tech", "path": "requires", "value": "optics", "unique": true }
              ]
            },
            {
              "stage": "updates",
              "category": "common/technologies",
              "if_mod_loaded": ["mod_alpha"],
              "operations": [
                { "op": "add", "target": "alpha_tech", "path": "cost", "value": 30 },
                { "op": "replace", "target": "alpha_tech", "path": "name", "value": "Alpha Evolved" },
                {
                  "op": "conditional",
                  "target": "alpha_tech",
                  "if_exists": "effects.output",
                  "then": [{ "op": "add", "path": "effects.output", "value": 0.05 }],
                  "else": [{ "op": "set", "path": "effects.output", "value": 0.99 }]
                },
                {
                  "op": "conditional",
                  "target": "alpha_tech",
                  "if_exists": "effects.nonexistent",
                  "then": [{ "op": "set", "path": "effects.branch", "value": "then" }],
                  "else": [{ "op": "set", "path": "effects.branch", "value": "else" }]
                },
                { "op": "remove", "target": "alpha_tech", "path": "requires", "value": "root_tech" }
              ]
            },
            {
              "stage": "updates",
              "category": "common/technologies",
              "if_mod_loaded": ["mod_not_installed"],
              "operations": [
                { "op": "set", "target": "alpha_tech", "path": "cost", "value": 99999 }
              ]
            }
          ]
        }
        """, "patches/10_stack.json", "mod_beta", 1, parseDiags);

        Check(parseDiags.Count == 0, "valid multi-stage patches parse with 0 diagnostics");

        var allPatches = mod0Final.Concat(mod1BaseAndUpdates).ToList();
        var applyDiags = new List<ModDiagnostic>();
        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase) { "mod_alpha", "mod_beta" };
        var mutated = ModJsonPatcher.ApplyDirect(doc, allPatches, enabled, "common/technologies", "00_technologies.json", applyDiags);
        Check(mutated, "ApplyDirect mutates target document");
        Check(applyDiags.Count == 0, "clean patch stacking produces 0 warnings/errors");

        // Cost sequence: 100 -> base (+20 = 120) -> updates (+30 = 150) -> final_fixes (*2 = 300)!
        var alpha = doc["alpha_tech"]!.AsObject();
        var finalCost = alpha["cost"]!.GetValue<double>();
        Check(Math.Abs(finalCost - 300.0) < 0.001, $"3-stage order base->updates->final_fixes yields cost 300 (got {finalCost})");
        Check(alpha["name"]!.GetValue<string>() == "Alpha Evolved", "replace op updates existing name");
        var reqs = alpha["requires"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Check(reqs.Length == 1 && reqs[0] == "optics", "append(unique) + remove(value) yields ['optics']");
        Check(Math.Abs(alpha["effects"]!["output"]!.GetValue<double>() - 0.15) < 0.001, "conditional then-branch executed when if_exists matched");
        Check(alpha["effects"]!["branch"]!.GetValue<string>() == "else", "conditional else-branch executed when if_exists absent");
        Check(!doc.ContainsKey("obsolete_tech"), "remove op with empty path removes whole record");

        // Diagnostics on missing target / invalid op
        var badDiags = new List<ModDiagnostic>();
        var badPatches = ModJsonPatcher.ParsePatchJson("""
        {
          "stage": "updates",
          "category": "common/technologies",
          "operations": [
            { "op": "replace", "target": "does_not_exist", "path": "cost", "value": 10 },
            { "op": "bogus_op", "target": "alpha_tech", "path": "cost", "value": 10 },
            { "op": "multiply", "target": "alpha_tech", "path": "name", "value": 2 }
          ]
        }
        """, "patches/bad.json", "mod_bad", 2, badDiags);
        Check(badDiags.Any(d => d.Code == "ERR_PATCH_OP" && !string.IsNullOrWhiteSpace(d.SuggestedFix)),
            "unknown patch op produces ERR_PATCH_OP with SuggestedFix");
        ModJsonPatcher.ApplyDirect(doc, badPatches, enabled, "common/technologies", "00_technologies.json", badDiags);
        Check(badDiags.Any(d => d.Code == "WARN_PATCH_TARGET_MISSING" && !string.IsNullOrWhiteSpace(d.SuggestedFix)),
            "missing patch target produces WARN_PATCH_TARGET_MISSING with SuggestedFix");
        Check(badDiags.Any(d => d.Code == "ERR_PATCH_TYPE" && !string.IsNullOrWhiteSpace(d.SuggestedFix)),
            "multiply on string property produces ERR_PATCH_TYPE with SuggestedFix");
    }

    private static void TestInlineOpInjectReplaceCreate()
    {
        var baseDoc = JsonNode.Parse("""
        {
          "existing_tech": {
            "name": "Existing",
            "cost": 100,
            "requires": ["req_a"],
            "effects": { "output": 0.05 }
          },
          "replaceable_tech": {
            "name": "Old Name",
            "cost": 50,
            "legacy_flag": true
          },
          "keep_me": {
            "name": "Original Owner",
            "cost": 80
          }
        }
        """)!.AsObject();

        var overlayDoc = JsonNode.Parse("""
        {
          "existing_tech": {
            "$op": "inject",
            "requires": ["req_a", "req_b"],
            "effects": { "research_speed": 0.10 }
          },
          "replaceable_tech": {
            "$op": "replace",
            "name": "Replaced Name",
            "cost": 75
          },
          "keep_me": {
            "$op": "create",
            "name": "Should Not Overwrite",
            "cost": 999
          },
          "brand_new": {
            "$op": "create",
            "name": "Brand New",
            "cost": 120
          }
        }
        """)!.AsObject();

        var diags = new List<ModDiagnostic>();
        var merged = ModJsonPatcher.ApplyInlineOpsDirect(baseDoc, overlayDoc, "common/technologies/50_mod.json", diags);

        var injected = merged["existing_tech"]!.AsObject();
        Check(!injected.ContainsKey("$op"), "$op directive is stripped before catalog ingestion");
        Check(injected["name"]!.GetValue<string>() == "Existing", "$op:inject preserves unmodified base fields");
        Check(Math.Abs(injected["effects"]!["output"]!.GetValue<double>() - 0.05) < 0.001 &&
              Math.Abs(injected["effects"]!["research_speed"]!.GetValue<double>() - 0.10) < 0.001,
            "$op:inject deep-merges nested effects object");
        var injectedReqs = injected["requires"]!.AsArray().Select(n => n!.GetValue<string>()).ToArray();
        Check(injectedReqs.Length == 2 && injectedReqs[0] == "req_a" && injectedReqs[1] == "req_b",
            "$op:inject appends unique array entries without duplicating req_a");

        var replaced = merged["replaceable_tech"]!.AsObject();
        Check(replaced["name"]!.GetValue<string>() == "Replaced Name" && !replaced.ContainsKey("legacy_flag"),
            "$op:replace overwrites record cleanly and drops old fields");

        Check(!merged.ContainsKey("keep_me"), "$op:create omits record when ID already exists");
        Check(diags.Any(d => d.Code == "WARN_OP_CREATE_EXISTS" && !string.IsNullOrWhiteSpace(d.SuggestedFix)),
            "$op:create collision emits WARN_OP_CREATE_EXISTS with SuggestedFix");
        Check(merged.ContainsKey("brand_new") && merged["brand_new"]!["cost"]!.GetValue<int>() == 120,
            "$op:create inserts record when ID does not exist");

        // Verify ModValidator catches bad patches/* and bad inline $op + bad trigger
        var tempRoot = Path.Combine(Path.GetTempPath(), "dof_patch_val_" + Guid.NewGuid().ToString("N"));
        try
        {
            var modDir = Path.Combine(tempRoot, "bad_patch_mod");
            Directory.CreateDirectory(Path.Combine(modDir, "patches"));
            Directory.CreateDirectory(Path.Combine(modDir, "common", "technologies"));
            Directory.CreateDirectory(Path.Combine(modDir, "events"));
            File.WriteAllText(Path.Combine(modDir, "manifest.json"), """
            { "id": "bad_patch_mod", "name": "Bad Patch Mod", "version": "1.0.0", "enabled": true }
            """);
            File.WriteAllText(Path.Combine(modDir, "patches", "10_broken.json"), """
            { "stage": "wrong_stage", "category": "common/technologies", "operations": [{ "op": "set", "target": "x", "path": "cost", "value": 1 }] }
            """);
            File.WriteAllText(Path.Combine(modDir, "common", "technologies", "50_bad_op.json"), """
            { "tech_x": { "$op": "explode", "name": "X" } }
            """);
            File.WriteAllText(Path.Combine(modDir, "events", "50_bad_trig.json"), """
            { "evt_x": { "title": "T", "trigger": { "min_stabilty": 0.5 }, "effects": { "stabilty": 0.02 } } }
            """);

            var report = ModValidator.ValidateMod(modDir);
            Check(HasCode(report, "ERR_PATCH_STAGE"), "ModValidator catches invalid patch stage");
            Check(HasCode(report, "ERR_INLINE_OP"), "ModValidator catches invalid inline $op");
            Check(HasCode(report, "ERR_UNKNOWN_TRIGGER"), "ModValidator catches unknown trigger key");
            Check(report.Diagnostics.Any(d => d.Code == "ERR_UNKNOWN_TRIGGER" && d.Message.Contains("stability", StringComparison.OrdinalIgnoreCase)),
                "unknown trigger diagnostic suggests 'stability' for typo 'stabilty'");
            Check(report.Diagnostics.Any(d => d.Code == "ERR_UNKNOWN_EFFECT" && d.Message.Contains("stability", StringComparison.OrdinalIgnoreCase)),
                "unknown effect diagnostic suggests 'stability' for typo 'stabilty'");
        }
        finally
        {
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static void TestMod02ScriptedEffectsAndTriggers()
    {
        var campaign = Campaign.CreateBootstrap(20261122);
        var usa = campaign.NationByTag("USA") ?? campaign.Nations.First(n => n.Movement is null);
        var supplier = campaign.Nations.First(n => n.Id != usa.Id && n.Movement is null);

        // 1. EraFeatures: restrict_feature ("nukes" alias) + unlock_feature + has_feature / missing_feature
        var errors = new List<string>();
        using (var restrictDoc = JsonDocument.Parse("""{ "restrict_feature": "nukes" }"""))
        {
            var eff = ScriptedEffects.Parse(restrictDoc.RootElement, "test:restrict", errors);
            Check(errors.Count == 0, "restrict_feature parses cleanly with alias 'nukes'");
            eff.Apply(campaign, usa);
            Check(!campaign.FeatureEnabled("defcon") && !campaign.FeatureEnabled("nuclear_strikes"),
                "restrict_feature 'nukes' disables canonical nuclear switches");

            // Round-trip via ScriptedEffectSaveConverter
            var savedJson = JsonSerializer.Serialize(eff);
            var restored = JsonSerializer.Deserialize<ScriptedEffects.Effect>(savedJson);
            Check(restored is not null && restored.Entries.Count == eff.Entries.Count,
                "ScriptedEffectSaveConverter preserves expanded feature entries");
        }
        using (var trigDoc = JsonDocument.Parse("""{ "missing_feature": "nukes", "has_feature": "aviation" }"""))
        {
            var trig = ScriptedTrigger.Parse(trigDoc.RootElement, "test:feat_trig", errors);
            Check(errors.Count == 0 && trig.Matches(campaign, usa), "missing_feature/has_feature triggers match live campaign");
        }
        using (var unlockDoc = JsonDocument.Parse("""{ "unlock_feature": "nukes" }"""))
        {
            var eff = ScriptedEffects.Parse(unlockDoc.RootElement, "test:unlock", errors);
            eff.Apply(campaign, usa);
            Check(campaign.FeatureEnabled("defcon") && campaign.FeatureEnabled("nuclear_strikes"),
                "unlock_feature 'nukes' re-enables nuclear switches");
        }

        // 2. GrandProjects: active/completed triggers, values, and grand_project_progress effect
        campaign.GrandProjects.Add(new GrandProjectInstance
        {
            Id = campaign.NextGrandProjectId++,
            DefinitionId = "orbital_corridor",
            HostNationId = usa.Id,
            StateMapIndex = 1,
            Stage = 1,
            ProgressDays = 10f,
            StageDurationDays = 90,
            Status = GrandProjectStatus.Active
        });
        using (var gpTrigDoc = JsonDocument.Parse("""{ "has_grand_project": "orbital_corridor", "min_active_grand_projects": 1, "min_grand_project.orbital_corridor": 0.5 }"""))
        {
            var gpTrig = ScriptedTrigger.Parse(gpTrigDoc.RootElement, "test:gp_trig", errors);
            Check(errors.Count == 0 && gpTrig.Matches(campaign, usa), "GrandProjects trigger matches active megaproject");
        }
        using (var gpEffDoc = JsonDocument.Parse("""{ "grand_project_progress": 25 }"""))
        {
            var gpEff = ScriptedEffects.Parse(gpEffDoc.RootElement, "test:gp_eff", errors);
            Check(errors.Count == 0, "grand_project_progress parses cleanly");
            Check(ScriptedEffects.Lines(gpEff).Any(l => l.Contains("25")), "grand_project_progress renders preview line");
            gpEff.Apply(campaign, usa);
            Check(MathF.Abs(campaign.GrandProjects[^1].ProgressDays - 35f) < 0.01f,
                "grand_project_progress advances active megaproject progress by 25 days");
        }

        // 3. StrategicChokepoints: squeezed_chokepoint trigger & counts
        campaign.TradeRoutes.Clear();
        campaign.TradeRoutesDay = campaign.ElapsedDays;
        campaign.TradeRoutes.Add(new TradeRoute { SellerId = supplier.Id, BuyerId = usa.Id, Commodity = Commodity.Oil, Value = 100f, Volume = 10f });
        var rel = CampaignDiplomacy.Get(campaign, usa.Id, supplier.Id);
        rel.EmbargoByA = true;
        using (var cpTrigDoc = JsonDocument.Parse("""{ "squeezed_chokepoint": "Oil", "min_squeezed_chokepoints": 1, "min_chokepoint_exposures": 1 }"""))
        {
            var cpTrig = ScriptedTrigger.Parse(cpTrigDoc.RootElement, "test:cp_trig", errors);
            Check(errors.Count == 0 && cpTrig.Matches(campaign, usa), "StrategicChokepoints trigger detects squeezed Oil supply");
        }

        // 4. Deterrence / NuclearPosture / NuclearDoctrine + Economy Stockpiles + Diplomacy / Movements readouts
        Check(ScriptedValues.TryRead(campaign, usa, "deterrence_tier", out var detTier) &&
              ScriptedValues.TryRead(campaign, usa, "nuclear_tier", out var nucTier) &&
              MathF.Abs(detTier - nucTier) < 0.001f, "deterrence_tier matches nuclear_tier");
        usa.NuclearDoctrine = NuclearDoctrine.NoFirstUse;
        Check(ScriptedValues.TryRead(campaign, usa, "nuclear_doctrine", out var docVal) && MathF.Abs(docVal - 1f) < 0.001f,
            "nuclear_doctrine reads NoFirstUse enum value");
        using (var docTrigDoc = JsonDocument.Parse("""{ "nuclear_doctrine": "no_first_use" }"""))
        {
            var docTrig = ScriptedTrigger.Parse(docTrigDoc.RootElement, "test:doc_trig", errors);
            Check(errors.Count == 0 && docTrig.Matches(campaign, usa), "nuclear_doctrine trigger matches NoFirstUse");
        }
        Check(ScriptedValues.TryRead(campaign, usa, "ally_count", out _) &&
              ScriptedValues.TryRead(campaign, usa, "active_wars", out _) &&
              ScriptedValues.TryRead(campaign, usa, "organization_count", out _) &&
              ScriptedValues.TryRead(campaign, usa, "subject_count", out _) &&
              ScriptedValues.TryRead(campaign, usa, "active_movements", out _) &&
              ScriptedValues.TryRead(campaign, usa, "weekly_balance", out _),
            "MOD-02 diplomacy/movement/economy ScriptedValues resolve cleanly");

        using (var stockEffDoc = JsonDocument.Parse("""{ "stockpile.ammunition": 40, "stockpile.oil_set": 125 }"""))
        {
            var stockEff = ScriptedEffects.Parse(stockEffDoc.RootElement, "test:stock", errors);
            Check(errors.Count == 0, "stockpile.<good> effects parse cleanly");
            StrategicStockpiles.EnsureSeeded(campaign, usa);
            var beforeAmmo = usa.Stockpiles.GetValueOrDefault("Ammunition", 0f);
            stockEff.Apply(campaign, usa);
            Check(MathF.Abs(usa.Stockpiles["Ammunition"] - (beforeAmmo + 40f)) < 0.01f &&
                  MathF.Abs(usa.Stockpiles["Oil"] - 125f) < 0.01f,
                "stockpile.<good> add and _set modify Nation.Stockpiles accurately");
        }

        // 5. Unknown keys never fail silently and include closest-key suggestion
        var typoErrors = new List<string>();
        using (var badEff = JsonDocument.Parse("""{ "war_suport": 0.05 }"""))
            ScriptedEffects.Parse(badEff.RootElement, "test:typo_eff", typoErrors);
        Check(typoErrors.Count == 1 && typoErrors[0].Contains("Did you mean 'war_support'?", StringComparison.OrdinalIgnoreCase),
            "unknown effect key emits closest-key suggestion");

        var trigTypoErrors = new List<string>();
        using (var badTrig = JsonDocument.Parse("""{ "min_ally_cout": 2 }"""))
            ScriptedTrigger.Parse(badTrig.RootElement, "test:typo_trig", trigTypoErrors);
        Check(trigTypoErrors.Count == 1 && trigTypoErrors[0].Contains("Did you mean 'ally_count'?", StringComparison.OrdinalIgnoreCase),
            "unknown trigger key emits closest-key suggestion");
    }

    private static void TestPatchUnloadRestoresBase2024Fingerprint()
    {
        ModOverlayPolicy.Reset();
        ModJsonPatcher.Reset();
        var cleanLoader = new ModLoader();
        cleanLoader.Discover(Array.Empty<string>(), new ContentSelection(), includeDlc: false);
        cleanLoader.ApplyCampaignData(Campaign.CreateBootstrap(20261123));

        var entry2024 = ScenarioLibrary.Discover(Array.Empty<string>(), "").First(e => e.Id == "2024");
        var baseline = ScenarioLibrary.Create(entry2024, 20261123);
        var baselineFp = CampaignFingerprint.Of(baseline);
        var sampleTech = TechnologyCatalog.All.First();
        var sampleTechId = sampleTech.Id;
        var baselineCost = sampleTech.Cost;

        var tempRoot = Path.Combine(Path.GetTempPath(), "dof_patch_roundtrip_" + Guid.NewGuid().ToString("N"));
        try
        {
            var modDir = Path.Combine(tempRoot, "cost_scaler");
            Directory.CreateDirectory(Path.Combine(modDir, "patches"));
            File.WriteAllText(Path.Combine(modDir, "manifest.json"), """
            { "id": "cost_scaler", "name": "Cost Scaler", "version": "1.0.0", "enabled": true }
            """);
            File.WriteAllText(Path.Combine(modDir, "patches", "10_double_tech.json"), $$"""
            {
              "stage": "updates",
              "category": "common/technologies",
              "operations": [
                { "op": "multiply", "target": "{{sampleTechId}}", "path": "cost", "value": 2.0 }
              ]
            }
            """);

            var sel = new ContentSelection();
            sel.Mods["cost_scaler"] = true;
            var modLoader = new ModLoader();
            modLoader.Discover(new[] { tempRoot }, sel, includeDlc: false);
            var modCampaign = Campaign.CreateBootstrap(20261123);
            modLoader.ApplyCampaignData(modCampaign);

            var patchedTech = TechnologyCatalog.All.First(t => t.Id == sampleTechId);
            Check(ModJsonPatcher.IsActive, "ModJsonPatcher activates when mod is loaded");
            Check(MathF.Abs(patchedTech.Cost - baselineCost * 2f) < 0.05f,
                $"live patches/*.json doubled '{sampleTechId}' cost ({baselineCost} -> {patchedTech.Cost})");

            // Unload all mods and confirm 100% clean reset back to base-2024 state & fingerprint.
            var resetLoader = new ModLoader();
            resetLoader.Discover(Array.Empty<string>(), new ContentSelection(), includeDlc: false);
            resetLoader.ApplyCampaignData(Campaign.CreateBootstrap(20261123));

            Check(!ModJsonPatcher.IsActive, "ModJsonPatcher resets cleanly to inactive when mods are disabled");
            var restoredTech = TechnologyCatalog.All.First(t => t.Id == sampleTechId);
            Check(MathF.Abs(restoredTech.Cost - baselineCost) < 0.01f,
                $"unloading patch mod restores '{sampleTechId}' base cost ({restoredTech.Cost})");

            var afterReset = ScenarioLibrary.Create(entry2024, 20261123);
            Check(CampaignFingerprint.Of(afterReset) == baselineFp,
                "unloading patched mod restores 100% bit-identical base-2024 CampaignFingerprint");
        }
        finally
        {
            ModJsonPatcher.Reset();
            ModOverlayPolicy.Reset();
            if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, true);
        }
    }

    private static bool HasCode(ModValidationReport report, string code) =>
        report.Diagnostics.Any(d => string.Equals(d.Code, code, StringComparison.Ordinal));

    private static ProvinceTopology BuildMinimalTopologyFor(Campaign campaign)
    {
        var sample = campaign.States.Where(s => s.MapIndex > 0 && s.NationMapIndex > 0).Take(3).ToArray();
        var states = sample.Select(s => (ushort)s.MapIndex).ToArray();
        var owners = sample.Select(s => (ushort)s.NationMapIndex).ToArray();
        return ProvinceTopology.Build(states, owners, states.Length, 1, _ => false);
    }

    private static void TickAndVerifyHealthy(Campaign campaign, int days, string label)
    {
        campaign.IsPaused = false;
        var startDay = campaign.ElapsedDays;
        for (var i = 0; i < days; i++)
            campaign.TickDay();

        Check(campaign.ElapsedDays >= startDay + days, $"{label} advanced {days} days cleanly");
        foreach (var n in campaign.Nations)
        {
            Check(float.IsFinite(n.Gdp) && float.IsFinite(n.Treasury) && float.IsFinite(n.Stability) && float.IsFinite(n.WarSupport),
                $"{label} nation '{n.Tag}' has zero NaN/Infinity values after {days} days");
        }
    }

    private static string CopyTemplateEnabled(string sourceDir, string targetRoot, string folderName)
    {
        var dest = Path.Combine(targetRoot, folderName);
        CopyDirectory(sourceDir, dest);
        var manifestPath = Path.Combine(dest, "manifest.json");
        var text = File.ReadAllText(manifestPath).Replace("\"enabled\": false", "\"enabled\": true", StringComparison.Ordinal);
        File.WriteAllText(manifestPath, text);
        return dest;
    }

    private static void CopyDirectory(string source, string destination)
    {
        Directory.CreateDirectory(destination);
        foreach (var file in Directory.GetFiles(source))
            File.Copy(file, Path.Combine(destination, Path.GetFileName(file)), overwrite: true);
        foreach (var dir in Directory.GetDirectories(source))
            CopyDirectory(dir, Path.Combine(destination, Path.GetFileName(dir)));
    }
}
