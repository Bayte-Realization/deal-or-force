using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Controls;
using DealOrForce.Simulation.Shell;

static class NoCompetitorNamesTests
{
    private static readonly Regex CompetitorRegex = new(
        @"(?:\bParadox\b|Парадокс|Age\s+of\s+History|\bAoH[23]?\b|\bАоХ\b|\bHOI\s*4?\b|Hearts\s+of\s+Iron|Хойк[аиеу]|\bVictoria\s*[23]\b|\bVic\s*[23]\b|Виктори[яиюей]\s*[23]\b|Europa\s+Universalis|\bEU\s*[345]\b|Европа\s+Универсалис|Crusader\s+Kings|\bCK\s*[23]\b|Крусейдер\s+Кингс)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex PlayerFacingCallRegex = new(
        @"\b(?:Tr|Loc\.Get|Loc\.Format|ActionButton)\s*\(\s*(@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*"")",
        RegexOptions.Compiled);

    private static readonly Regex TooltipOrTextAssignRegex = new(
        @"\b(?:TooltipText|PlaceholderText)\s*=\s*(@""(?:""""|[^""])*""|""(?:\\.|[^""\\])*"")",
        RegexOptions.Compiled);

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL no-competitor-names: " + label);
        Console.WriteLine("PASS no-competitor-names: " + label);
    }

    [TestSuite("--no-competitor-names-only", Description = "Ensures player-visible settings, localization, and UI Tr/Loc strings contain no competitor game/studio names.")]
    public static void Run()
    {
        var root = FindRepoRoot();

        // 0. Sanity-check regex: catches competitor names while keeping real geography untouched.
        Check(CompetitorRegex.IsMatch("Paradox (auto-pause)") &&
              CompetitorRegex.IsMatch("Age of History (LMB pan)") &&
              CompetitorRegex.IsMatch("AoH (map feed)") &&
              CompetitorRegex.IsMatch("Victoria 3") &&
              CompetitorRegex.IsMatch("Vic3") &&
              CompetitorRegex.IsMatch("HOI4") &&
              CompetitorRegex.IsMatch("Hearts of Iron") &&
              CompetitorRegex.IsMatch("Europa Universalis") &&
              CompetitorRegex.IsMatch("EU4") &&
              CompetitorRegex.IsMatch("EU5") &&
              CompetitorRegex.IsMatch("Crusader Kings"),
            "detector catches all competitor titles and abbreviations");
        Check(!CompetitorRegex.IsMatch("Lake Victoria") &&
              !CompetitorRegex.IsMatch("Victoria Nile") &&
              !CompetitorRegex.IsMatch("Port Victoria") &&
              !CompetitorRegex.IsMatch("Victoria") &&
              !CompetitorRegex.IsMatch("Озеро Виктория"),
            "real geographic names (Lake Victoria, Victoria Nile, Port Victoria) remain untouched");

        // 1. Scan all game/localization/**/*.json keys and values.
        var locRoot = Path.Combine(root, "game", "localization");
        var locHits = new List<string>();
        var locFileCount = 0;
        foreach (var file in Directory.EnumerateFiles(locRoot, "*.json", SearchOption.AllDirectories).OrderBy(p => p))
        {
            locFileCount++;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var prop in doc.RootElement.EnumerateObject())
            {
                if (CompetitorRegex.IsMatch(prop.Name))
                    locHits.Add($"{rel} key '{Truncate(prop.Name)}'");
                if (prop.Value.ValueKind == JsonValueKind.String)
                {
                    var val = prop.Value.GetString() ?? "";
                    if (CompetitorRegex.IsMatch(val))
                        locHits.Add($"{rel} ['{Truncate(prop.Name)}'] = '{Truncate(val)}'");
                }
            }
        }
        Check(locFileCount > 0 && locHits.Count == 0,
            $"0 competitor names in {locFileCount} game/localization JSON files (found {locHits.Count}: {string.Join("; ", locHits.Take(5))})");

        // 2. Scan all game/common/settings/**/*.json name, description, values, labels, default.
        var settingsRoot = Path.Combine(root, "game", "common", "settings");
        var settingsHits = new List<string>();
        var settingsFileCount = 0;
        foreach (var file in Directory.EnumerateFiles(settingsRoot, "*.json", SearchOption.AllDirectories).OrderBy(p => p))
        {
            settingsFileCount++;
            var rel = Path.GetRelativePath(root, file).Replace('\\', '/');
            using var doc = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
            if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                foreach (var field in entry.Value.EnumerateObject())
                {
                    var fn = field.Name.ToLowerInvariant();
                    if (fn is "name" or "description" or "default" && field.Value.ValueKind == JsonValueKind.String)
                    {
                        var text = field.Value.GetString() ?? "";
                        if (CompetitorRegex.IsMatch(text))
                            settingsHits.Add($"{rel} {entry.Name}.{field.Name}: '{Truncate(text)}'");
                    }
                    else if (fn is "labels" or "values" && field.Value.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var item in field.Value.EnumerateArray())
                        {
                            if (item.ValueKind != JsonValueKind.String) continue;
                            var text = item.GetString() ?? "";
                            if (CompetitorRegex.IsMatch(text))
                                settingsHits.Add($"{rel} {entry.Name}.{field.Name}[]: '{Truncate(text)}'");
                        }
                    }
                }
            }
        }
        Check(settingsFileCount > 0 && settingsHits.Count == 0,
            $"0 competitor names in {settingsFileCount} game/common/settings files (found {settingsHits.Count}: {string.Join("; ", settingsHits.Take(5))})");

        // 3. Scan all player-facing Tr(...) / Loc.Get(...) / Loc.Format(...) / ActionButton(...) / TooltipText literals in Scripts/.
        var scriptsRoot = Path.Combine(root, "Scripts");
        var scriptHits = new List<string>();
        foreach (var cs in Directory.EnumerateFiles(scriptsRoot, "*.cs", SearchOption.AllDirectories).OrderBy(p => p))
        {
            if (cs.Contains(Path.DirectorySeparatorChar + "bin" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                cs.Contains(Path.DirectorySeparatorChar + "obj" + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                continue;
            var rel = Path.GetRelativePath(root, cs).Replace('\\', '/');
            var lines = File.ReadAllLines(cs);
            for (var i = 0; i < lines.Length; i++)
            {
                var line = lines[i];
                var trimmed = line.TrimStart();
                if (trimmed.StartsWith("//", StringComparison.Ordinal)) continue;
                foreach (Match m in PlayerFacingCallRegex.Matches(line))
                {
                    var lit = m.Groups[1].Value;
                    if (CompetitorRegex.IsMatch(lit))
                        scriptHits.Add($"{rel}:{i + 1}: {lit}");
                }
                foreach (Match m in TooltipOrTextAssignRegex.Matches(line))
                {
                    var lit = m.Groups[1].Value;
                    if (CompetitorRegex.IsMatch(lit))
                        scriptHits.Add($"{rel}:{i + 1}: {lit}");
                }
            }
        }
        Check(scriptHits.Count == 0,
            $"0 competitor names in Scripts/ player-facing Tr/Loc/Tooltip literals (found {scriptHits.Count}: {string.Join("; ", scriptHits.Take(5))})");

        // 4. Verify neutral setting labels localize cleanly in EN + RU and legacy saved aliases migrate silently.
        SettingsCatalog.ResetCatalog();
        Loc.ResetCatalog();
        Check(Loc.TextIn("english", "Direct Pan (LMB pan)") is not null &&
              Loc.TextIn("english", "Box Select (LMB box)") is not null &&
              Loc.TextIn("english", "Focus (auto-pause)") is not null &&
              Loc.TextIn("english", "Dispatch (map feed)") is not null,
            "neutral control & message preset labels exist in English localization");
        var ruHandbook = Loc.TextIn("russian", "handbook.controls.body") ?? "";
        Check(Loc.TextIn("russian", "Direct Pan (LMB pan)") is not null &&
              Loc.TextIn("russian", "Box Select (LMB box)") is not null &&
              Loc.TextIn("russian", "Focus (auto-pause)") is not null &&
              Loc.TextIn("russian", "Dispatch (map feed)") is not null &&
              ruHandbook.Length > 0 && !CompetitorRegex.IsMatch(ruHandbook),
            "neutral control & message preset labels and handbook exist in Russian localization");

        PlayerSettings.Set("mouse_scheme", "aoh");
        Check(SettingsCatalog.Chosen("mouse_scheme") == "direct_pan" &&
              ControlSchemeRules.ActiveMouseScheme == MouseControlScheme.DirectPan,
            "legacy mouse_scheme='aoh' resolves to 'direct_pan'");
        PlayerSettings.Set("mouse_scheme", "paradox");
        Check(SettingsCatalog.Chosen("mouse_scheme") == "box_select" &&
              ControlSchemeRules.ActiveMouseScheme == MouseControlScheme.BoxSelect,
            "legacy mouse_scheme='paradox' resolves to 'box_select'");
        SettingsCatalog.Reset("mouse_scheme");

        PlayerSettings.Set("ui_shell_message_preset", "paradox");
        Check(SettingsCatalog.Chosen("ui_shell_message_preset") == "focus" &&
              ShellMessages.ParsePreset(SettingsCatalog.Chosen("ui_shell_message_preset")) == ShellMessagePreset.Focus,
            "legacy ui_shell_message_preset='paradox' resolves to 'focus'");
        PlayerSettings.Set("ui_shell_message_preset", "aoh");
        Check(SettingsCatalog.Chosen("ui_shell_message_preset") == "dispatch" &&
              ShellMessages.ParsePreset(SettingsCatalog.Chosen("ui_shell_message_preset")) == ShellMessagePreset.Dispatch,
            "legacy ui_shell_message_preset='aoh' resolves to 'dispatch'");
        SettingsCatalog.Reset("ui_shell_message_preset");
    }

    static string Truncate(string s) => s.Length <= 64 ? s : s[..64] + "...";

    static string FindRepoRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate DealOrForce.csproj from current or app base directory.");
    }
}
