using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class MenuChromeTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("menu-chrome: " + label);
        Console.WriteLine("PASS menu-chrome: " + label);
    }

    public static void Run()
    {
        CheckFlagFraming();
        CheckWarmPaletteAndFonts();
        CheckLocalizationParity();
        CheckSourceChromeIntegrity();
        CheckFingerprintInvariance();
    }

    private static void CheckFlagFraming()
    {
        var kor = ShellLayout.FitFlag(300, 200, 82f, 52f, "KOR");
        Check(kor.Width <= 82.01f && kor.Height <= 52.01f && !kor.CutoutSilhouette &&
              Math.Abs(kor.Width / kor.Height - 1.5f) < 0.02f,
            "KOR flag fits natural 3:2 proportion without distortion");

        var che = ShellLayout.FitFlag(256, 256, 82f, 52f, "CHE");
        Check(che.Width <= 52.05f && Math.Abs(che.Width - che.Height) < 0.05f && !che.CutoutSilhouette,
            "CHE square flag preserves 1:1 proportion");

        var qat = ShellLayout.FitFlag(280, 110, 82f, 52f, "QAT");
        Check(qat.Width <= 82.05f && qat.Height < 40f && !qat.CutoutSilhouette,
            "QAT wide 28:11 flag scales down horizontally instead of cropping");

        var npl = ShellLayout.FitFlag(200, 240, 82f, 52f, "NPL");
        Check(npl.CutoutSilhouette && npl.Height <= 52.05f,
            "NPL keeps pennant cutout silhouette");
    }

    private static void CheckWarmPaletteAndFonts()
    {
        InterfaceSkin.ResetCatalog();
        FontCatalog.Load();

        Check(InterfaceSkin.Colour("shell.plate_top").Equals("#332c25", StringComparison.OrdinalIgnoreCase),
            "90_shell.json declares warm umber plate_top #332c25");
        Check(InterfaceSkin.Colour("shell.plate_bottom").Equals("#221d19", StringComparison.OrdinalIgnoreCase),
            "90_shell.json declares warm umber plate_bottom #221d19");
        Check(InterfaceSkin.Colour("shell.well_top").Equals("#0e0b08", StringComparison.OrdinalIgnoreCase),
            "90_shell.json declares sunken well_top #0e0b08");
        Check(InterfaceSkin.Colour("shell.brass_hi").Equals("#a48d68", StringComparison.OrdinalIgnoreCase),
            "90_shell.json declares antique brass highlight #a48d68");
        Check(InterfaceSkin.Colour("shell.paper").Equals("#efe4c8", StringComparison.OrdinalIgnoreCase),
            "90_shell.json declares warm paper ink #efe4c8");
        Check(InterfaceSkin.Colour("shell.rule").Equals("#3b3228", StringComparison.OrdinalIgnoreCase),
            "90_shell.json declares warm rule #3b3228");

        var dataRoot = GameDataLoader.DataFolder();
        var repoRoot = Path.GetFullPath(Path.Combine(dataRoot, ".."));
        var fontsDoc = File.ReadAllText(Path.Combine(dataRoot, "fonts", "90_shell_fonts.json"));
        Check(fontsDoc.Contains("SourceSerif4-SemiBold.ttf", StringComparison.Ordinal) &&
              fontsDoc.Contains("SourceSerif4-Bold.ttf", StringComparison.Ordinal) &&
              fontsDoc.Contains("SourceSerif4-Regular.ttf", StringComparison.Ordinal),
            "90_shell_fonts.json maps Source Serif 4 variants");

        foreach (var fontName in new[] { "SourceSerif4-Regular.ttf", "SourceSerif4-SemiBold.ttf", "SourceSerif4-Bold.ttf" })
            Check(File.Exists(Path.Combine(dataRoot, "fonts", fontName)), $"font asset present: {fontName}");

        Check(File.Exists(Path.Combine(dataRoot, "gfx", "interface", "brand_logo_banner.png")),
            "worn parchment logo banner exists");
        Check(File.Exists(Path.Combine(repoRoot, "Scripts", "ui", "MenuChromeTheme.cs")),
            "standalone MenuChromeTheme exists");
        Check(File.Exists(Path.Combine(repoRoot, "Scripts", "ui", "CountryPickerDossier.cs")),
            "standalone CountryPickerDossier exists");
    }

    private static void CheckLocalizationParity()
    {
        var dataRoot = GameDataLoader.DataFolder();
        var enFile = Path.Combine(dataRoot, "localization", "english", "zz_440_menu_chrome.json");
        var ruFile = Path.Combine(dataRoot, "localization", "russian", "zz_440_menu_chrome.json");
        Check(File.Exists(enFile) && File.Exists(ruFile), "zz_440_menu_chrome.json exists in EN and RU");

        using var enDoc = JsonDocument.Parse(File.ReadAllText(enFile), GameDataLoader.ForgivingJson);
        using var ruDoc = JsonDocument.Parse(File.ReadAllText(ruFile), GameDataLoader.ForgivingJson);
        var enKeys = enDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
        var ruKeys = ruDoc.RootElement.EnumerateObject().Select(p => p.Name).OrderBy(k => k).ToArray();
        Check(enKeys.SequenceEqual(ruKeys, StringComparer.Ordinal), "EN and RU zz_440_menu_chrome keys match 1:1");

        var prevLang = Loc.Language;
        try
        {
            Loc.LoadDirectory(Path.Combine(dataRoot, "localization"), new List<string>());
            Loc.Language = "russian";
            foreach (var key in new[]
                     {
                         "Headquarters", "Capital", "Population", "GDP", "Treasury",
                         "Regions", "Divisions", "Cadres", "Standing",
                         "No land: it campaigns, raises money and rises up to take some.",
                         "OK", "Continue", "New campaign", "CAMPAIGN PAUSED", "SETTINGS", "CAMPAIGN ARCHIVE"
                     })
            {
                var translated = Loc.Get(key);
                Check(!string.IsNullOrWhiteSpace(translated) && translated != key,
                    $"Russian localization differs cleanly from key: {key}");
            }
            var ranked = Loc.Format("№ {0} · {1} · {2}", 1, "KOR", Loc.Get("great power"));
            Check(ranked.Contains("№ 1 · KOR", StringComparison.Ordinal), "Rank format preserves numeral pattern");
        }
        finally
        {
            Loc.Language = prevLang;
        }
    }

    private static void CheckSourceChromeIntegrity()
    {
        var repoRoot = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        var forbiddenLiterals = new[]
        {
            "#1d4477", "#0c1720", "#070b11", "#010204",
            "new Color(\"0c1720\")", "new Color(\"293642\")",
            "0.045f, 0.070f, 0.105f", "0.047f, 0.09f, 0.125f",
            ".065f, .10f, .135f"
        };

        var ownedFiles = new[]
        {
            "Scripts/ui/MenuChromeTheme.cs",
            "Scripts/ui/CountryPickerDossier.cs",
            "Scripts/MainMenuScene.cs",
            "Scripts/MenuGlobe.cs",
            "Scripts/ui/MainMenuUI.cs",
            "Scripts/ui/CountrySelectionUI.cs",
            "Scripts/ui/SaveLoadUI.cs",
            "Scripts/ui/PauseMenuUI.cs",
            "Scripts/ui/ModFrontendMenu.cs",
            "Scripts/ui/ModFrontendActionUI.cs",
            "Scripts/ui/SettingsUI.cs",
            "Scripts/ui/SettingsRows.cs",
            "Scripts/ui/EditorModRows.cs",
            "Scripts/ui/LoadingScreen.cs",
            "Scripts/ui/LoadingScreenUI.cs"
        };

        foreach (var rel in ownedFiles)
        {
            var full = Path.Combine(repoRoot, rel.Replace('/', Path.DirectorySeparatorChar));
            Check(File.Exists(full), $"owned file exists: {rel}");
            Check(File.Exists(full + ".uid"), $"uid sidecar exists: {rel}.uid");
            var text = File.ReadAllText(full);
            foreach (var bad in forbiddenLiterals)
                Check(!text.Contains(bad, StringComparison.OrdinalIgnoreCase),
                    $"{rel} free of cold literal '{bad}'");
        }

        foreach (var helper in new[] { "Scripts/ui/MenuChromeTheme.cs", "Scripts/ui/CountryPickerDossier.cs" })
        {
            var full = Path.Combine(repoRoot, helper.Replace('/', Path.DirectorySeparatorChar));
            var lines = File.ReadAllLines(full);
            var text = string.Join("\n", lines);
            Check(lines.Length < 800, $"{helper} under 800 lines ({lines.Length})");
            Check(!text.Contains("partial class DealOrForceGame", StringComparison.Ordinal) &&
                  !text.Contains("partial class GlobePresentation", StringComparison.Ordinal),
                $"{helper} does not extend god-class partials");
        }

        var pauseText = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "PauseMenuUI.cs"));
        Check(pauseText.Contains("PauseOpenHandbook", StringComparison.Ordinal) &&
              pauseText.Contains("PauseOpenNavigator", StringComparison.Ordinal) &&
              pauseText.Contains("MusicShortcut(", StringComparison.Ordinal),
            "PauseMenuUI preserves required handbook/navigator/music controls");

        var pickerText = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "CountrySelectionUI.cs"));
        Check(pickerText.Contains("BEGIN AS {0}", StringComparison.Ordinal) &&
              pickerText.Contains("START HOTSEAT", StringComparison.Ordinal),
            "CountrySelectionUI preserves campaign start button patterns");

        var loadingText = File.ReadAllText(Path.Combine(repoRoot, "Scripts", "ui", "LoadingScreen.cs"));
        Check(loadingText.Contains("LoadingGameLogo", StringComparison.Ordinal) &&
              loadingText.Contains("LoadingMotto", StringComparison.Ordinal) &&
              loadingText.Contains("SpriteCatalog.TextureOf(\"panel_dossier\"", StringComparison.Ordinal),
            "LoadingScreen preserves branding nodes and uses SpriteCatalog for dossier paper");

        var missingUids = Directory.EnumerateFiles(Path.Combine(repoRoot, "Scripts"), "*.cs", SearchOption.AllDirectories)
            .Count(cs => !File.Exists(cs + ".uid"));
        Check(missingUids == 0, $"zero missing Scripts/**/*.cs.uid (found {missingUids})");
    }

    private static void CheckFingerprintInvariance()
    {
        var campaign = Campaign.CreateBootstrap(77);
        var before = CampaignFingerprint.Of(campaign);
        var prev = Loc.Language;
        try
        {
            Loc.Language = "russian";
            SettingsCatalog.Store("language", "russian");
            Loc.Language = "english";
            SettingsCatalog.Store("language", "english");
        }
        finally
        {
            Loc.Language = prev;
        }
        Check(before == CampaignFingerprint.Of(campaign),
            "outer-menu language switches never change CampaignFingerprint");
    }
}
