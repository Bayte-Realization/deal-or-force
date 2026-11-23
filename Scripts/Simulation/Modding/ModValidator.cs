using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace DealOrForce.Simulation;

public enum ModDiagnosticSeverity
{
    Info = 0,
    Warning = 1,
    Error = 2
}

/// <summary>One structured, beginner-friendly validation diagnostic produced by <see cref="ModValidator"/>.</summary>
public sealed record ModDiagnostic(
    ModDiagnosticSeverity Severity,
    string Code,
    string File,
    string Path,
    string Message,
    string SuggestedFix);

public sealed class ModValidationReport
{
    public string ModId { get; init; } = "";
    public string ModRoot { get; init; } = "";
    public List<ModDiagnostic> Diagnostics { get; } = new();
    public List<ModProvenanceEntry> Provenance { get; } = new();

    public IEnumerable<ModDiagnostic> Errors => Diagnostics.Where(d => d.Severity == ModDiagnosticSeverity.Error);
    public IEnumerable<ModDiagnostic> Warnings => Diagnostics.Where(d => d.Severity == ModDiagnosticSeverity.Warning);
    public IEnumerable<ModDiagnostic> Infos => Diagnostics.Where(d => d.Severity == ModDiagnosticSeverity.Info);

    public int ErrorCount => Diagnostics.Count(d => d.Severity == ModDiagnosticSeverity.Error);
    public int WarningCount => Diagnostics.Count(d => d.Severity == ModDiagnosticSeverity.Warning);
    public bool IsValid => ErrorCount == 0;

    public void Add(ModDiagnosticSeverity severity, string code, string file, string path, string message, string suggestedFix) =>
        Diagnostics.Add(new ModDiagnostic(severity, code, file ?? "", path ?? "", message ?? "", suggestedFix ?? ""));

    public string FormatSummary()
    {
        var sb = new StringBuilder();
        sb.AppendLine($"[ModValidator] {(string.IsNullOrEmpty(ModId) ? Path.GetFileName(ModRoot) : ModId)}: {(IsValid ? "VALID" : "INVALID")} ({ErrorCount} error(s), {WarningCount} warning(s))");
        foreach (var d in Diagnostics)
        {
            var loc = string.IsNullOrEmpty(d.Path) ? d.File : $"{d.File} ({d.Path})";
            sb.AppendLine($"  [{d.Severity.ToString().ToUpperInvariant()}] {d.Code} {loc}: {d.Message}");
            if (!string.IsNullOrWhiteSpace(d.SuggestedFix))
                sb.AppendLine($"      Fix: {d.SuggestedFix}");
        }
        return sb.ToString();
    }
}

/// <summary>
/// Pure-C# pre-flight validator for Deal or Force mods and multi-era total-conversion scenarios.
///
/// Validates manifest &amp; scenario schemas, JSON/YAML syntax, dates, calendar profiles, dependency
/// cycles, <c>replace_paths</c>/<c>exclude_paths</c> allowlists &amp; multi-mod conflicts, resolved
/// country tags, diplomacy contradictions, technology prerequisite graphs, scripted effect keys,
/// disabled era-feature references, and EN/RU localization parity.
/// </summary>
public static class ModValidator
{
    private static readonly Regex PlaceholderRx = new(@"\{(\d+)(?:[^}]*)\}", RegexOptions.Compiled);

    private static readonly string[] ScannedSubfolders =
    {
        "common", "events", "history", "localization", "patches", "scenarios", "skins", "interface", "gui", "music"
    };

    /// <summary>Validate a single mod directory on disk.</summary>
    public static ModValidationReport ValidateMod(string modRoot, IReadOnlyCollection<string> knownCountryTags = null)
    {
        var report = new ModValidationReport { ModRoot = modRoot ?? "" };
        if (string.IsNullOrWhiteSpace(modRoot) || !Directory.Exists(modRoot))
        {
            report.Add(ModDiagnosticSeverity.Error, "ERR_MOD_DIR_MISSING", modRoot ?? "", "",
                "Mod directory does not exist.", "Create a folder containing manifest.json inside mods/.");
            return report;
        }

        var manifestPath = Path.Combine(modRoot, "manifest.json");
        if (!File.Exists(manifestPath))
        {
            report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_MISSING", "manifest.json", "",
                "Missing required manifest.json at mod root.",
                "Add a manifest.json with at least \"id\", \"name\", \"version\", and \"enabled\".");
            return report;
        }

        var info = ParseAndValidateManifest(modRoot, manifestPath, report);
        var layerSpec = new ModLayerSpec(
            info.Id.Length > 0 ? info.Id : Path.GetFileName(modRoot),
            info.TotalConversion,
            info.ReplacePaths,
            info.ExcludePaths);

        // Compute provenance for this single mod.
        var layerErrors = new List<string>();
        using (ModOverlayPolicy.PushScope(new[] { layerSpec }, out layerErrors))
        {
            foreach (var prov in ModOverlayPolicy.Provenance)
                report.Provenance.Add(prov);
        }

        // Collect all known country tags (base world + mod common/countries + custom scenario worlds).
        var tagPool = BuildCountryTagPool(modRoot, knownCountryTags);

        // Collect mod-level disabled features.
        var disabledFeatureSet = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (feat, state) in info.ResolvedFeatures)
            if (state == EraFeatureState.Unavailable)
                disabledFeatureSet.Add(feat);

        // 1. Syntax scan every JSON/YAML data file under standard mod roots.
        ValidateSyntaxTree(modRoot, report);

        // 2. Validate every scenario.json inside scenarios/.
        ValidateScenarios(modRoot, tagPool, disabledFeatureSet, report);

        // 3. Validate resolved technologies (missing prerequisites + cycles).
        var replacesTechs = info.TotalConversion && info.ReplacePaths.Count == 0
            || info.ReplacePaths.Any(p => p.Equals("common/technologies", StringComparison.OrdinalIgnoreCase));
        ValidateTechnologies(modRoot, replacesTechs, report);

        // 4. Validate events & decisions (syntax, dated events, scripted effects, disabled era features).
        ValidateEventsAndDecisions(modRoot, disabledFeatureSet, report);

        // 5. Validate EN + RU localization parity & placeholders.
        ValidateLocalizationParity(modRoot, report);

        // 6. Validate declarative patches/*.json|yaml|yml and inline $op directives.
        ValidatePatchesAndInlineOps(modRoot, info.Id.Length > 0 ? info.Id : Path.GetFileName(modRoot), report);

        return report;
    }

    /// <summary>
    /// Validate a set of mods together: runs single-mod validation on each plus cross-mod
    /// dependency resolution, cycle detection, duplicate IDs, and <c>replace_paths</c> conflict checks.
    /// </summary>
    public static ModValidationReport ValidateModSet(IEnumerable<string> modRoots, IReadOnlyCollection<string> knownCountryTags = null)
    {
        var combined = new ModValidationReport { ModId = "mod_set", ModRoot = "" };
        var roots = (modRoots ?? Array.Empty<string>()).Where(r => !string.IsNullOrWhiteSpace(r)).ToList();
        var infos = new List<ParsedManifestInfo>();
        var byId = new Dictionary<string, ParsedManifestInfo>(StringComparer.OrdinalIgnoreCase);

        foreach (var root in roots)
        {
            var single = ValidateMod(root, knownCountryTags);
            combined.Diagnostics.AddRange(single.Diagnostics);

            var manifestPath = Path.Combine(root, "manifest.json");
            if (!File.Exists(manifestPath)) continue;
            var scratch = new ModValidationReport();
            var info = ParseAndValidateManifest(root, manifestPath, scratch);
            if (string.IsNullOrWhiteSpace(info.Id)) continue;
            if (!byId.TryAdd(info.Id, info))
            {
                combined.Add(ModDiagnosticSeverity.Error, "ERR_DUPLICATE_MOD_ID", "manifest.json", "id",
                    $"Duplicate mod id '{info.Id}' found in '{root}'.",
                    "Assign a unique lowercase id to each mod.");
            }
            else
            {
                infos.Add(info);
            }
        }

        // Check missing dependencies and dependency cycles.
        foreach (var info in infos)
        {
            foreach (var dep in info.Dependencies)
            {
                if (!byId.ContainsKey(dep))
                {
                    combined.Add(ModDiagnosticSeverity.Error, "ERR_MISSING_DEPENDENCY", $"{info.Id}/manifest.json", "dependencies",
                        $"Mod '{info.Id}' depends on '{dep}', which is not present in the mod set.",
                        $"Install/enable mod '{dep}' or remove it from dependencies.");
                }
            }
        }
        DetectDependencyCycles(infos, byId, combined);

        // Check replace_paths conflicts between unrelated mods (neither depends on the other).
        var replaceOwners = new Dictionary<string, List<ParsedManifestInfo>>(StringComparer.OrdinalIgnoreCase);
        foreach (var info in infos)
        {
            var effectiveReplace = info.ReplacePaths.Count > 0
                ? info.ReplacePaths
                : (info.TotalConversion ? ModOverlayPolicy.TotalConversionDefaultReplacePaths.ToList() : new List<string>());
            foreach (var raw in effectiveReplace)
            {
                if (!ModOverlayPolicy.TryNormalizePath(raw, true, out var norm, out _)) continue;
                if (!replaceOwners.TryGetValue(norm, out var list))
                    replaceOwners[norm] = list = new List<ParsedManifestInfo>();
                list.Add(info);
            }
        }
        foreach (var (vpath, owners) in replaceOwners)
        {
            if (owners.Count < 2) continue;
            for (var i = 0; i < owners.Count; i++)
            for (var j = i + 1; j < owners.Count; j++)
            {
                var a = owners[i];
                var b = owners[j];
                var orderedByDep = a.Dependencies.Contains(b.Id, StringComparer.OrdinalIgnoreCase) ||
                                   b.Dependencies.Contains(a.Id, StringComparer.OrdinalIgnoreCase);
                if (!orderedByDep)
                {
                    combined.Add(ModDiagnosticSeverity.Warning, "WARN_REPLACE_PATH_CONFLICT",
                        $"{a.Id} vs {b.Id}", $"replace_paths:{vpath}",
                        $"Both '{a.Id}' and '{b.Id}' replace '{vpath}' without declaring a dependency between them.",
                        $"Declare '{a.Id}' or '{b.Id}' in dependencies (or use a compatibility patch) so overwrite precedence is explicit.");
                }
            }
        }

        var specs = infos.Select(i => new ModLayerSpec(i.Id, i.TotalConversion, i.ReplacePaths, i.ExcludePaths)).ToList();
        using (ModOverlayPolicy.PushScope(specs, out _))
        {
            foreach (var prov in ModOverlayPolicy.Provenance)
                combined.Provenance.Add(prov);
        }

        return combined;
    }

    private sealed class ParsedManifestInfo
    {
        public string Id = "";
        public string Name = "";
        public bool TotalConversion;
        public List<string> Dependencies = new();
        public List<string> ReplacePaths = new();
        public List<string> ExcludePaths = new();
        public Dictionary<string, EraFeatureState> ResolvedFeatures = new(StringComparer.OrdinalIgnoreCase);
    }

    private static ParsedManifestInfo ParseAndValidateManifest(string modRoot, string manifestPath, ModValidationReport report)
    {
        var info = new ParsedManifestInfo();
        try
        {
            using var doc = JsonDocument.Parse(File.ReadAllText(manifestPath), GameDataLoader.ForgivingJson);
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_SCHEMA", "manifest.json", "$",
                    "manifest.json root must be a JSON object.", "Wrap fields in { ... }.");
                return info;
            }

            info.Id = ReadString(root, "id");
            info.Name = ReadString(root, "name");
            var version = ReadString(root, "version");

            if (string.IsNullOrWhiteSpace(info.Id))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_ID", "manifest.json", "id",
                    "manifest.json is missing a non-empty 'id'.", "Add \"id\": \"my_scenario_mod\" (lowercase letters, digits, underscores).");
            }
            else if (!IsValidManifestId(info.Id))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_ID_FORMAT", "manifest.json", "id",
                    $"Mod id '{info.Id}' contains unsupported characters.",
                    "Use lowercase letters, digits, underscores or hyphens (1-64 chars, starting with a letter).");
            }

            if (string.IsNullOrWhiteSpace(info.Name))
                report.Add(ModDiagnosticSeverity.Warning, "WARN_MANIFEST_NAME", "manifest.json", "name",
                    "manifest.json has no display 'name'.", "Add a human-readable \"name\": \"My Scenario\".");

            if (string.IsNullOrWhiteSpace(version))
                report.Add(ModDiagnosticSeverity.Warning, "WARN_MANIFEST_VERSION", "manifest.json", "version",
                    "manifest.json has no 'version'.", "Add \"version\": \"1.0.0\".");

            if (root.TryGetProperty("total_conversion", out var tc) && tc.ValueKind is JsonValueKind.True or JsonValueKind.False)
                info.TotalConversion = tc.GetBoolean();

            info.Dependencies = ReadStringList(root, "dependencies", "manifest.json", report);
            info.ReplacePaths = ReadStringList(root, "replace_paths", "manifest.json", report);
            info.ExcludePaths = ReadStringList(root, "exclude_paths", "manifest.json", report);

            foreach (var rp in info.ReplacePaths)
                if (!ModOverlayPolicy.TryNormalizePath(rp, isReplaceRoot: true, out _, out var err))
                    report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_REPLACE_PATH", "manifest.json", $"replace_paths:{rp}", err,
                        "Use a declared game content root such as 'common/technologies', 'events', 'common/decisions', or 'history/diplomacy'.");

            foreach (var ep in info.ExcludePaths)
                if (!ModOverlayPolicy.TryNormalizePath(ep, isReplaceRoot: false, out _, out var err))
                    report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_EXCLUDE_PATH", "manifest.json", $"exclude_paths:{ep}", err,
                        "Use a clean relative path like 'events/20_ukraine.json' without '..' or drive letters.");

            if (root.TryGetProperty("calendar", out var calEl) && calEl.ValueKind == JsonValueKind.Object)
            {
                var calErrors = new List<string>();
                ScenarioManifestLoader.ParseCalendar(calEl, calErrors);
                foreach (var err in calErrors)
                    report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_CALENDAR", "manifest.json", "calendar", err,
                        "Provide 12 non-empty month_names (or omit month_names to inherit defaults) and a valid year_offset.");
            }

            var disabled = ReadStringList(root, "disabled_features", "manifest.json", report);
            var eraFeatures = ReadEraFeatureMap(root, "manifest.json", report);
            var featureErrors = new List<string>();
            info.ResolvedFeatures = EraFeatureRegistry.Resolve(disabled, eraFeatures, featureErrors, requireKnownBuiltIn: true);
            foreach (var err in featureErrors)
            {
                var code = err.Contains("Conflicting", StringComparison.OrdinalIgnoreCase) ? "ERR_FEATURE_CONFLICT" : "ERR_UNKNOWN_FEATURE";
                report.Add(ModDiagnosticSeverity.Error, code, "manifest.json", "era_features", err,
                    "Use known feature ids ('aviation', 'drones', 'defcon', 'nuclear_strikes', 'long_range_forces') or aliases ('nukes', 'missiles', 'modern_air_force'), and avoid conflicting states.");
            }
        }
        catch (Exception ex)
        {
            report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_SYNTAX", "manifest.json", "$",
                $"Failed to parse manifest.json: {ex.Message}",
                "Fix the JSON syntax error (check commas, quotes, and matching braces).");
        }
        return info;
    }

    private static void ValidateSyntaxTree(string modRoot, ModValidationReport report)
    {
        foreach (var sub in ScannedSubfolders)
        {
            var folder = Path.Combine(modRoot, sub);
            if (!Directory.Exists(folder)) continue;
            foreach (var file in StructuredData.Files(folder, SearchOption.AllDirectories))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                try
                {
                    var json = StructuredData.ReadJson(file);
                    using var _ = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
                }
                catch (Exception ex)
                {
                    report.Add(ModDiagnosticSeverity.Error, "ERR_DATA_SYNTAX", rel, "$",
                        $"Invalid JSON/YAML syntax: {ex.Message}",
                        "Validate the file syntax (missing colon, unclosed quote, invalid indentation, or stray comma).");
                }
            }
        }
    }

    private static void ValidateScenarios(
        string modRoot,
        HashSet<string> baseTagPool,
        HashSet<string> disabledFeatureAccumulator,
        ModValidationReport report)
    {
        var scenariosDir = Path.Combine(modRoot, "scenarios");
        if (!Directory.Exists(scenariosDir)) return;

        foreach (var file in Directory.EnumerateFiles(scenariosDir, "scenario.*", SearchOption.AllDirectories)
                     .Where(f => f.EndsWith(".json", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".yaml", StringComparison.OrdinalIgnoreCase) ||
                                 f.EndsWith(".yml", StringComparison.OrdinalIgnoreCase))
                     .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
        {
            var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
            ScenarioManifestLoader.TryParseFile(file, out var manifest, out var parseErrors);
            foreach (var err in parseErrors)
            {
                var code = err.Contains("month_names", StringComparison.OrdinalIgnoreCase) || err.Contains("calendar", StringComparison.OrdinalIgnoreCase)
                    ? "ERR_INVALID_CALENDAR"
                    : "ERR_SCENARIO_SCHEMA";
                report.Add(ModDiagnosticSeverity.Error, code, rel, "scenario", err,
                    "Check scenario.json field types against docs/MODDING_SCENARIOS_GUIDE_RU.md.");
            }
            if (manifest is null) continue;

            if (string.IsNullOrWhiteSpace(manifest.Id))
                report.Add(ModDiagnosticSeverity.Error, "ERR_SCENARIO_ID", rel, "id",
                    "Scenario manifest is missing required 'id'.", "Add \"id\": \"my_scenario\".");
            if (string.IsNullOrWhiteSpace(manifest.Name))
                report.Add(ModDiagnosticSeverity.Error, "ERR_SCENARIO_NAME", rel, "name",
                    "Scenario manifest is missing required 'name'.", "Add \"name\": \"My Scenario Name\".");

            // World file existence (required unless base_world: true or id == "2024").
            var scenarioTagPool = new HashSet<string>(baseTagPool, StringComparer.OrdinalIgnoreCase);
            if (!string.IsNullOrWhiteSpace(manifest.World))
            {
                try
                {
                    var worldPath = ScenarioLibrary.SafePath(Path.GetDirectoryName(file)!, manifest.World);
                    if (!File.Exists(worldPath))
                    {
                        report.Add(ModDiagnosticSeverity.Error, "ERR_SCENARIO_WORLD_MISSING", rel, "world",
                            $"Declared world file '{manifest.World}' does not exist next to {rel}.",
                            "Export world.bdfsave into the scenario folder, or remove 'world' and set \"base_world\": true.");
                    }
                    else
                    {
                        TryReadWorldTags(worldPath, scenarioTagPool, rel, report);
                    }
                }
                catch (Exception ex)
                {
                    report.Add(ModDiagnosticSeverity.Error, "ERR_SCENARIO_WORLD_PATH", rel, "world",
                        $"Unsafe world path '{manifest.World}': {ex.Message}",
                        "Keep world.bdfsave inside the scenario folder (e.g. \"world\": \"world.bdfsave\").");
                }
            }
            else if (!manifest.BaseWorld && !string.Equals(manifest.Id, "2024", StringComparison.OrdinalIgnoreCase))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_SCENARIO_NO_WORLD", rel, "world",
                    "Scenario specifies neither a 'world' file nor '\"base_world\": true'.",
                    "Add \"base_world\": true to build on the Earth map, or provide \"world\": \"world.bdfsave\".");
            }

            // Start date validation.
            if (manifest.HasExplicitStartDate && !manifest.TryResolveStartDate(out _, out var dateErr))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_DATE", rel, "start_date", dateErr,
                    "Use a real calendar date such as \"start_year\": 1914, \"start_month\": 7, \"start_day\": 28 (or \"start_date\": \"1914-07-28\").");
            }

            // Replace/exclude path checks on scenario level too.
            foreach (var rp in manifest.ReplacePaths)
                if (!ModOverlayPolicy.TryNormalizePath(rp, true, out _, out var rpErr))
                    report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_REPLACE_PATH", rel, $"replace_paths:{rp}", rpErr,
                        "Choose a declared virtual content root such as 'events' or 'common/technologies'.");
            foreach (var ep in manifest.ExcludePaths)
                if (!ModOverlayPolicy.TryNormalizePath(ep, false, out _, out var epErr))
                    report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_EXCLUDE_PATH", rel, $"exclude_paths:{ep}", epErr,
                        "Use a clean relative path under game/ without '..'.");

            // Era features & conflict detection.
            var featErrors = new List<string>();
            var resolvedFeats = EraFeatureRegistry.Resolve(manifest.DisabledFeatures, manifest.EraFeatures, featErrors, requireKnownBuiltIn: true);
            foreach (var err in featErrors)
            {
                var code = err.Contains("Conflicting", StringComparison.OrdinalIgnoreCase) ? "ERR_FEATURE_CONFLICT" : "ERR_UNKNOWN_FEATURE";
                report.Add(ModDiagnosticSeverity.Error, code, rel, "era_features", err,
                    "Verify feature names against MechanicFeatures or EraFeatureRegistry aliases and remove contradictory assignments.");
            }
            foreach (var (feat, state) in resolvedFeats)
                if (state == EraFeatureState.Unavailable)
                    disabledFeatureAccumulator.Add(feat);

            // Country tag checks (only if we have a populated tag pool).
            if (scenarioTagPool.Count > 0)
            {
                void CheckTag(string tag, string jsonPath)
                {
                    if (string.IsNullOrWhiteSpace(tag)) return;
                    if (!scenarioTagPool.Contains(tag))
                        report.Add(ModDiagnosticSeverity.Error, "ERR_UNKNOWN_COUNTRY_TAG", rel, jsonPath,
                            $"Country tag '{tag}' does not exist in the scenario's world.",
                            "Check the 3-letter ISO/country tag in game/common/countries/00_countries.json or your custom world.bdfsave.");
                }

                CheckTag(manifest.PlayerCountry, "player_country");
                foreach (var fc in manifest.FeaturedCountries)
                    CheckTag(fc.Tag, $"featured_countries:{fc.Tag}");

                var warPairs = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var war in manifest.StartingWars)
                {
                    CheckTag(war.Attacker, "starting_wars.attacker");
                    CheckTag(war.Defender, "starting_wars.defender");
                    if (string.Equals(war.Attacker, war.Defender, StringComparison.OrdinalIgnoreCase))
                    {
                        report.Add(ModDiagnosticSeverity.Error, "ERR_DIPLOMACY_CONFLICT", rel, "starting_wars",
                            $"Country '{war.Attacker}' cannot start at war with itself.",
                            "Specify two different country tags for attacker and defender.");
                    }
                    else
                    {
                        warPairs.Add(CanonicalPair(war.Attacker, war.Defender));
                    }
                }

                foreach (var bloc in manifest.StartingAlliances)
                {
                    foreach (var m in bloc.Members)
                        CheckTag(m, $"starting_alliances:{bloc.Name}");
                    for (var i = 0; i < bloc.Members.Count; i++)
                    for (var j = i + 1; j < bloc.Members.Count; j++)
                    {
                        var pair = CanonicalPair(bloc.Members[i], bloc.Members[j]);
                        if (warPairs.Contains(pair))
                            report.Add(ModDiagnosticSeverity.Error, "ERR_DIPLOMACY_CONFLICT", rel, $"starting_alliances:{bloc.Name}",
                                $"Countries '{bloc.Members[i]}' and '{bloc.Members[j]}' are listed in both starting_wars and starting_alliances.",
                                "Remove the pair from either starting_wars or starting_alliances.");
                    }
                }

                foreach (var truce in manifest.StartingTruces)
                {
                    if (truce.Between.Count != 2) continue;
                    CheckTag(truce.Between[0], "starting_truces");
                    CheckTag(truce.Between[1], "starting_truces");
                    if (string.Equals(truce.Between[0], truce.Between[1], StringComparison.OrdinalIgnoreCase))
                        report.Add(ModDiagnosticSeverity.Error, "ERR_DIPLOMACY_CONFLICT", rel, "starting_truces",
                            $"Country '{truce.Between[0]}' cannot hold a truce with itself.",
                            "Specify two distinct tags in 'between'.");
                    if (truce.Days <= 0)
                        report.Add(ModDiagnosticSeverity.Error, "ERR_DIPLOMACY_CONFLICT", rel, "starting_truces.days",
                            $"Truce duration must be positive (got {truce.Days}).",
                            "Set 'days' to a positive integer such as 365.");
                    if (warPairs.Contains(CanonicalPair(truce.Between[0], truce.Between[1])))
                        report.Add(ModDiagnosticSeverity.Error, "ERR_DIPLOMACY_CONFLICT", rel, "starting_truces",
                            $"Countries '{truce.Between[0]}' and '{truce.Between[1]}' are listed in both starting_wars and starting_truces.",
                            "Remove the pair from either starting_wars or starting_truces.");
                }
            }
        }
    }

    private static void ValidateTechnologies(string modRoot, bool replacesBaseTechs, ModValidationReport report)
    {
        var techDir = Path.Combine(modRoot, "common", "technologies");
        if (!Directory.Exists(techDir)) return;

        var knownIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (!replacesBaseTechs)
        {
            foreach (var t in TechnologyCatalog.All)
                knownIds.Add(t.Id);
        }

        var modReqs = new Dictionary<string, (string File, List<string> Requires)>(StringComparer.OrdinalIgnoreCase);
        foreach (var file in StructuredData.Files(techDir, SearchOption.TopDirectoryOnly))
        {
            var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
            try
            {
                using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Name.StartsWith("//", StringComparison.Ordinal) || entry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (GameDataLoader.IsRemoved(entry.Value))
                    {
                        knownIds.Remove(entry.Name);
                        modReqs.Remove(entry.Name);
                        continue;
                    }
                    knownIds.Add(entry.Name);
                    var reqs = new List<string>();
                    if (entry.Value.TryGetProperty("requires", out var reqEl) && reqEl.ValueKind == JsonValueKind.Array)
                    {
                        foreach (var r in reqEl.EnumerateArray())
                            if (r.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(r.GetString()))
                                reqs.Add(r.GetString()!.Trim());
                    }
                    modReqs[entry.Name] = (rel, reqs);
                }
            }
            catch
            {
                // Syntax error already reported by ValidateSyntaxTree.
            }
        }

        foreach (var (techId, (file, reqs)) in modReqs)
        {
            foreach (var req in reqs)
            {
                if (!knownIds.Contains(req))
                {
                    report.Add(ModDiagnosticSeverity.Error, "ERR_TECH_MISSING_PREREQ", file, $"{techId}.requires",
                        $"Technology '{techId}' requires unknown technology '{req}'.",
                        replacesBaseTechs
                            ? $"Define '{req}' inside your mod's common/technologies/ (base technologies are replaced) or fix the typo."
                            : $"Check the spelling of '{req}' or define it in common/technologies/.");
                }
            }
        }

        // Cycle check across mod technologies.
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase); // 1 = visiting, 2 = done
        bool HasCycle(string id)
        {
            if (state.TryGetValue(id, out var s)) return s == 1;
            state[id] = 1;
            if (modReqs.TryGetValue(id, out var item))
            {
                foreach (var req in item.Requires)
                    if (HasCycle(req)) return true;
            }
            state[id] = 2;
            return false;
        }
        foreach (var (techId, (file, _)) in modReqs)
        {
            if (state.ContainsKey(techId)) continue;
            if (HasCycle(techId))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_TECH_CYCLE", file, techId,
                    $"Technology '{techId}' participates in a circular prerequisite loop.",
                    "Break the cycle in 'requires' so the technology tree is a DAG.");
            }
        }
    }

    private static void ValidateEventsAndDecisions(string modRoot, HashSet<string> disabledFeatures, ModValidationReport report)
    {
        // Validate events/*.json|yml
        var eventsDir = Path.Combine(modRoot, "events");
        if (Directory.Exists(eventsDir))
        {
            foreach (var file in StructuredData.Files(eventsDir, SearchOption.TopDirectoryOnly))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                try
                {
                    using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var entry in doc.RootElement.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.Object || GameDataLoader.IsRemoved(entry.Value)) continue;
                        var obj = entry.Value;
                        if (obj.TryGetProperty("date", out var dateEl) && dateEl.ValueKind == JsonValueKind.String)
                        {
                            var rawDate = dateEl.GetString() ?? "";
                            if (!string.IsNullOrWhiteSpace(rawDate) && !HistoricalDate.TryParse(rawDate, out _))
                                report.Add(ModDiagnosticSeverity.Error, "ERR_INVALID_DATE", rel, $"{entry.Name}.date",
                                    $"Event '{entry.Name}' has invalid date '{rawDate}'.",
                                    "Use 'YYYY-MM-DD' or 'YYYY-MM-DD BCE' (e.g. '1914-07-28' or '0490-09-12 BCE').");
                        }
                        ValidateTriggerAndEffects(obj, rel, entry.Name, disabledFeatures, report);
                        if (obj.TryGetProperty("options", out var opts) && opts.ValueKind == JsonValueKind.Array)
                        {
                            var idx = 0;
                            foreach (var opt in opts.EnumerateArray())
                            {
                                if (opt.ValueKind == JsonValueKind.Object)
                                    ValidateTriggerAndEffects(opt, rel, $"{entry.Name}.options[{idx}]", disabledFeatures, report);
                                idx++;
                            }
                        }
                    }
                }
                catch { /* syntax error already caught */ }
            }
        }

        // Validate common/decisions/*.json|yml
        var decisionsDir = Path.Combine(modRoot, "common", "decisions");
        if (Directory.Exists(decisionsDir))
        {
            foreach (var file in StructuredData.Files(decisionsDir, SearchOption.TopDirectoryOnly))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                try
                {
                    using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var entry in doc.RootElement.EnumerateObject())
                    {
                        if (entry.Value.ValueKind != JsonValueKind.Object || GameDataLoader.IsRemoved(entry.Value)) continue;
                        ValidateTriggerAndEffects(entry.Value, rel, entry.Name, disabledFeatures, report);
                    }
                }
                catch { /* syntax error already caught */ }
            }
        }
    }

    private static void ValidateTriggerAndEffects(
        JsonElement obj,
        string relFile,
        string recordPath,
        HashSet<string> disabledFeatures,
        ModValidationReport report)
    {
        if (obj.TryGetProperty("effects", out var effEl) && effEl.ValueKind == JsonValueKind.Object)
        {
            var effectErrors = new List<string>();
            ScriptedEffects.Parse(effEl, $"{relFile}:{recordPath}", effectErrors);
            foreach (var err in effectErrors)
                report.Add(ModDiagnosticSeverity.Error, "ERR_UNKNOWN_EFFECT", relFile, $"{recordPath}.effects", err,
                    "Use supported effect keys (e.g. treasury, stability, war_support, manpower, feature.<id>_set) or declare a scripted_effect in common/scripted_effects/.");

            CheckDisabledFeatureReferences(effEl, relFile, $"{recordPath}.effects", disabledFeatures, report);
        }

        if (obj.TryGetProperty("trigger", out var trigEl) && trigEl.ValueKind == JsonValueKind.Object)
        {
            var triggerErrors = new List<string>();
            ScriptedTrigger.Parse(trigEl, $"{relFile}:{recordPath}", triggerErrors);
            foreach (var err in triggerErrors)
                report.Add(ModDiagnosticSeverity.Error, "ERR_UNKNOWN_TRIGGER", relFile, $"{recordPath}.trigger", err,
                    "Use supported trigger directives (at_war, is_movement, tags, has_feature, missing_feature, has_grand_project, completed_grand_project, squeezed_chokepoint, officeholders, min_<key>, max_<key>).");

            CheckDisabledFeatureReferences(trigEl, relFile, $"{recordPath}.trigger", disabledFeatures, report);
        }
    }

    private static void CheckDisabledFeatureReferences(
        JsonElement obj,
        string relFile,
        string recordPath,
        HashSet<string> disabledFeatures,
        ModValidationReport report)
    {
        if (disabledFeatures.Count == 0 || obj.ValueKind != JsonValueKind.Object) return;
        foreach (var prop in obj.EnumerateObject())
        {
            var k = prop.Name;
            string referencedFeature = null;
            if (k.StartsWith("min_feature.", StringComparison.OrdinalIgnoreCase))
                referencedFeature = k[12..];
            else if (k.StartsWith("feature.", StringComparison.OrdinalIgnoreCase))
            {
                referencedFeature = k[8..];
                if (referencedFeature.EndsWith("_set", StringComparison.OrdinalIgnoreCase))
                    referencedFeature = referencedFeature[..^4];
            }
            else if (k.Contains("nuclear", StringComparison.OrdinalIgnoreCase) && disabledFeatures.Contains("nuclear_strikes"))
                referencedFeature = "nuclear_strikes";

            if (!string.IsNullOrWhiteSpace(referencedFeature) && disabledFeatures.Contains(referencedFeature))
            {
                report.Add(ModDiagnosticSeverity.Warning, "WARN_DISABLED_FEATURE_REF", relFile, $"{recordPath}.{k}",
                    $"References feature '{referencedFeature}', which is marked unavailable in this mod's era settings.",
                    "Change the feature to 'restricted' if events should unlock/use it, or remove this reference.");
            }
        }
    }

    private static void ValidatePatchesAndInlineOps(string modRoot, string modId, ModValidationReport report)
    {
        var patchDir = Path.Combine(modRoot, "patches");
        if (Directory.Exists(patchDir))
        {
            foreach (var file in StructuredData.Files(patchDir, SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                var diags = new List<ModDiagnostic>();
                ModJsonPatcher.ParsePatchFile(file, rel, modId, 0, diags);
                foreach (var d in diags)
                    report.Diagnostics.Add(d);
            }
        }

        foreach (var sub in new[] { "common", "events", "history" })
        {
            var dir = Path.Combine(modRoot, sub);
            if (!Directory.Exists(dir)) continue;
            foreach (var file in StructuredData.Files(dir, SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
                try
                {
                    using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                    ModJsonPatcher.ValidateInlineOps(doc.RootElement, rel, report);
                }
                catch { /* syntax error already caught by ValidateSyntaxTree */ }
            }
        }
    }

    private static void ValidateLocalizationParity(string modRoot, ModValidationReport report)
    {
        var locRoot = Path.Combine(modRoot, "localization");
        if (!Directory.Exists(locRoot)) return;

        var en = ReadLocaleMap(Path.Combine(locRoot, "english"), modRoot, report);
        var ru = ReadLocaleMap(Path.Combine(locRoot, "russian"), modRoot, report);

        if (en.Count == 0 && ru.Count == 0) return;

        foreach (var (key, (enFile, enVal)) in en)
        {
            if (!ru.TryGetValue(key, out var ruPair))
            {
                report.Add(ModDiagnosticSeverity.Warning, "WARN_LOC_MISSING_KEY", enFile, $"localization/russian:{key}",
                    $"Key '{key}' exists in English localization but is missing in Russian.",
                    $"Add a Russian translation for \"{key}\" under localization/russian/.");
                continue;
            }
            var enPlaceholders = ExtractPlaceholders(enVal);
            var ruPlaceholders = ExtractPlaceholders(ruPair.Value);
            if (!enPlaceholders.SetEquals(ruPlaceholders))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_LOC_PLACEHOLDER_MISMATCH", ruPair.File, key,
                    $"Placeholder mismatch for key '{key}': English has [{string.Join(", ", enPlaceholders.OrderBy(x => x))}], Russian has [{string.Join(", ", ruPlaceholders.OrderBy(x => x))}].",
                    "Keep identical format placeholders (like {0}, {1}) in both English and Russian translations.");
            }
        }

        foreach (var (key, (ruFile, _)) in ru)
        {
            if (!en.ContainsKey(key))
            {
                report.Add(ModDiagnosticSeverity.Warning, "WARN_LOC_MISSING_KEY", ruFile, $"localization/english:{key}",
                    $"Key '{key}' exists in Russian localization but is missing in English.",
                    $"Add an English entry for \"{key}\" under localization/english/.");
            }
        }
    }

    private static Dictionary<string, (string File, string Value)> ReadLocaleMap(string dir, string modRoot, ModValidationReport report)
    {
        var map = new Dictionary<string, (string File, string Value)>(StringComparer.Ordinal);
        if (!Directory.Exists(dir)) return map;
        foreach (var file in StructuredData.Files(dir, SearchOption.TopDirectoryOnly))
        {
            var rel = Path.GetRelativePath(modRoot, file).Replace('\\', '/');
            try
            {
                using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in doc.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.String) continue;
                    var val = entry.Value.GetString() ?? "";
                    if (string.IsNullOrWhiteSpace(val))
                    {
                        report.Add(ModDiagnosticSeverity.Warning, "WARN_LOC_EMPTY_VALUE", rel, entry.Name,
                            $"Localization key '{entry.Name}' has an empty value.",
                            "Provide a non-empty translation string.");
                    }
                    map[entry.Name] = (rel, val);
                }
            }
            catch { /* syntax error already caught */ }
        }
        return map;
    }

    private static HashSet<string> ExtractPlaceholders(string text)
    {
        var set = new HashSet<string>(StringComparer.Ordinal);
        if (string.IsNullOrEmpty(text)) return set;
        foreach (Match m in PlaceholderRx.Matches(text))
            set.Add("{" + m.Groups[1].Value + "}");
        return set;
    }

    private static HashSet<string> BuildCountryTagPool(string modRoot, IReadOnlyCollection<string> knownCountryTags)
    {
        var tags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        if (knownCountryTags is not null)
        {
            foreach (var t in knownCountryTags)
                if (!string.IsNullOrWhiteSpace(t)) tags.Add(t.Trim());
        }
        else
        {
            if (WorldMapTables.CountryRows.Length == 0)
                WorldMapTables.ResetCatalog();
            foreach (var row in WorldMapTables.CountryRows)
            {
                var parts = row.Split('|');
                if (parts.Length >= 10)
                    tags.Add(ScenarioCatalog.CountryTag(parts));
            }
        }

        // Also include any country tags declared in mod's common/countries/.
        var countriesDir = Path.Combine(modRoot, "common", "countries");
        if (Directory.Exists(countriesDir))
        {
            foreach (var file in StructuredData.Files(countriesDir, SearchOption.TopDirectoryOnly))
            {
                try
                {
                    using var doc = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                    if (doc.RootElement.ValueKind != JsonValueKind.Object) continue;
                    foreach (var prop in doc.RootElement.EnumerateObject())
                        if (!prop.Name.StartsWith("//", StringComparison.Ordinal))
                            tags.Add(prop.Name);
                }
                catch { /* syntax error reported elsewhere */ }
            }
        }
        return tags;
    }

    private static void TryReadWorldTags(string worldPath, HashSet<string> target, string relScenario, ModValidationReport report)
    {
        try
        {
            var raw = File.ReadAllText(worldPath);
            if (!raw.TrimStart().StartsWith("{", StringComparison.Ordinal)) return; // Binary BDF3 save — skip lightweight JSON scan.
            using var doc = JsonDocument.Parse(raw, GameDataLoader.ForgivingJson);
            if (doc.RootElement.TryGetProperty("Nations", out var nations) && nations.ValueKind == JsonValueKind.Array)
            {
                // When a custom world.bdfsave defines its own Nations array, validate scenario tags strictly against it!
                var customTags = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (var n in nations.EnumerateArray())
                    if (n.ValueKind == JsonValueKind.Object && n.TryGetProperty("Tag", out var tagEl) && tagEl.ValueKind == JsonValueKind.String)
                    {
                        var t = tagEl.GetString()?.Trim();
                        if (!string.IsNullOrWhiteSpace(t)) customTags.Add(t);
                    }
                if (customTags.Count > 0)
                {
                    target.Clear();
                    foreach (var t in customTags) target.Add(t);
                }
            }
        }
        catch (Exception ex)
        {
            report.Add(ModDiagnosticSeverity.Warning, "WARN_WORLD_INSPECT", relScenario, "world",
                $"Could not inspect world file '{Path.GetFileName(worldPath)}': {ex.Message}",
                "Verify the world file is a valid JSON or BDF3 scenario snapshot.");
        }
    }

    private static void DetectDependencyCycles(
        List<ParsedManifestInfo> infos,
        Dictionary<string, ParsedManifestInfo> byId,
        ModValidationReport report)
    {
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        bool Visit(string id, List<string> trail)
        {
            if (state.TryGetValue(id, out var st))
            {
                if (st == 1)
                {
                    trail.Add(id);
                    return true;
                }
                return false;
            }
            state[id] = 1;
            trail.Add(id);
            if (byId.TryGetValue(id, out var mod))
            {
                foreach (var dep in mod.Dependencies)
                    if (byId.ContainsKey(dep) && Visit(dep, trail))
                        return true;
            }
            trail.RemoveAt(trail.Count - 1);
            state[id] = 2;
            return false;
        }

        foreach (var info in infos)
        {
            if (state.ContainsKey(info.Id)) continue;
            var trail = new List<string>();
            if (Visit(info.Id, trail))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_DEPENDENCY_CYCLE", $"{info.Id}/manifest.json", "dependencies",
                    $"Circular mod dependency detected: {string.Join(" -> ", trail)}.",
                    "Remove one of the dependency links to break the cycle.");
            }
        }
    }

    private static bool IsValidManifestId(string id)
    {
        if (string.IsNullOrWhiteSpace(id) || id.Length > 64) return false;
        if (id[0] is not (>= 'a' and <= 'z')) return false;
        return id.All(c => c is (>= 'a' and <= 'z') or (>= '0' and <= '9') or '_' or '-');
    }

    private static string CanonicalPair(string a, string b)
    {
        var left = (a ?? "").Trim().ToUpperInvariant();
        var right = (b ?? "").Trim().ToUpperInvariant();
        return string.CompareOrdinal(left, right) <= 0 ? $"{left}|{right}" : $"{right}|{left}";
    }

    private static string ReadString(JsonElement el, string key) =>
        el.TryGetProperty(key, out var prop) && prop.ValueKind == JsonValueKind.String ? (prop.GetString()?.Trim() ?? "") : "";

    private static List<string> ReadStringList(JsonElement el, string key, string file, ModValidationReport report)
    {
        var list = new List<string>();
        if (!el.TryGetProperty(key, out var prop) || prop.ValueKind == JsonValueKind.Null) return list;
        if (prop.ValueKind != JsonValueKind.Array)
        {
            report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_SCHEMA", file, key,
                $"Field '{key}' must be an array of strings.", $"Write \"{key}\": [ ... ].");
            return list;
        }
        foreach (var item in prop.EnumerateArray())
        {
            if (item.ValueKind == JsonValueKind.String)
            {
                var s = item.GetString()?.Trim() ?? "";
                if (s.Length > 0) list.Add(s);
            }
            else
                report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_SCHEMA", file, key,
                    $"Field '{key}' contains a non-string element.", "Ensure every entry in the array is a quoted string.");
        }
        return list;
    }

    private static Dictionary<string, string> ReadEraFeatureMap(JsonElement el, string file, ModValidationReport report)
    {
        var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!el.TryGetProperty("era_features", out var prop) || prop.ValueKind == JsonValueKind.Null) return map;
        if (prop.ValueKind != JsonValueKind.Object)
        {
            report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_SCHEMA", file, "era_features",
                "Field 'era_features' must be a JSON object.", "Map feature ids to 'available', 'restricted', or 'unavailable'.");
            return map;
        }
        foreach (var entry in prop.EnumerateObject())
        {
            if (entry.Value.ValueKind == JsonValueKind.String) map[entry.Name] = entry.Value.GetString() ?? "";
            else if (entry.Value.ValueKind == JsonValueKind.True) map[entry.Name] = "available";
            else if (entry.Value.ValueKind == JsonValueKind.False) map[entry.Name] = "unavailable";
            else report.Add(ModDiagnosticSeverity.Error, "ERR_MANIFEST_SCHEMA", file, $"era_features.{entry.Name}",
                $"Invalid value type for era_features.{entry.Name}.", "Use 'available', 'restricted', 'unavailable', true, or false.");
        }
        return map;
    }
}
