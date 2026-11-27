using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Stage A4 gate (<c>--content-shape-only</c>): enforces <c>docs/modding/DATA_RULES_RU.md</c>
/// across <c>game/</c> and <c>mods/_templates/</c> with an explicit, closed allowlist for shipped
/// historical exceptions, zero tolerance for new unprefixed files / wrappers / non-snake_case ids /
/// legacy deletion aliases (<c>removed</c>, <c>disabled</c>), plus negative self-tests.
/// </summary>
static class ContentShapeTests
{
    private static readonly Regex FileNameRule = new(@"^\d{2}_[a-z0-9_]+\.(json|yml|yaml)$", RegexOptions.Compiled);
    private static readonly Regex StateIsoFileRule = new(@"^history/states/[^/]+/[A-Z0-9]{3}\.json$", RegexOptions.Compiled);
    private static readonly Regex SnakeCaseIdRule = new(@"^[a-z0-9_]+(\.[a-z0-9_]+)*$", RegexOptions.Compiled);
    private static readonly Regex EraSkinFileRule = new(@"^skins/[a-z0-9_]+/skin\.json$", RegexOptions.Compiled);
    private static readonly Regex MapPackJsonFileRule = new(@"^maps/[a-z0-9_]+/(map|compiled)\.json$", RegexOptions.Compiled);

    private static readonly HashSet<string> SkippedMetaNames = new(StringComparer.OrdinalIgnoreCase)
    {
        "manifest.json", "scenario.json", "dlc.json"
    };

    private static readonly HashSet<string> LegacyGameFileNames = new(StringComparer.Ordinal)
    {
        "common/defines/100_peace_beneficiary.json",
        "common/defines/101_formable_proclamation_ai.json",
        "common/defines/102_parliamentary_politics.json",
        "common/defines/103_reintegration_recovery.json",
        "common/defines/104_arms_shipments.json",
        "common/defines/105_strait_crossings.json",
        "common/defines/106_money_drains.json",
        "common/defines/107_pockets_partisans.json",
        "common/defines/108_conquest_aftermath.json",
        "common/defines/108_long_range_movement.json",
        "common/modifiers/49b_diplomatic_acts_extra.json",
        "common/modifiers/49c_ultimatum_terms.json",
        "common/opinion_modifiers/100_kin_statecraft.json",
        "common/opinion_modifiers/101_expeditions.json",
        "gfx/interface/icons/minimal_palette.json",
        "gfx/interface/icons/shell_palette.json",
        "gfx/leaders/anonymous_portrait.source.json",
        "gfx/models/trade/container_ship.json",
        "gfx/models/trade/lorry_blue.json",
        "gfx/models/trade/lorry_red.json",
        "gfx/models/trade/tanker.json",
        "history/states/2024/lakes.json",
        "history/states/2024/unclaimed.json",
        "history/states/2024/zz_strategic_deposits.json",
        "history/states/2024/zz_world_resources.json",
        "map_data/compiled.json",
        "map_data/hydrography/lakes.json",
        "map_data/hydrography/rivers.json",
        "map_data/source_tables.json",
        "map_data/terrain_provenance.json",
        "map_data/terrain_settings.json",
        "map_data/water_settings.json",
    };

    private static readonly HashSet<string> LegacyTemplateFileNames = new(StringComparer.Ordinal)
    {
        "balance_and_script/common/countries/korea.json",
        "balance_and_script/common/defines/balance.json",
        "balance_and_script/common/goods/market.json",
        "budget_and_taxes/common/budget/taxes.json",
        "cabinet_and_trade/common/government_focus/cabinets.json",
        "cabinet_and_trade/common/trade_policies/stances.json",
        "politics/common/governments/forms.json",
        "politics/common/ideologies/beliefs.json",
        "production_chains/common/buildings/new_building.json",
        "production_chains/common/buildings/works.json",
        "production_chains/common/defines/market.json",
        "production_chains/common/goods/chains.json",
        "production_chains/common/goods/new_good.json",
        "production_chains/common/production/chains.json",
        "production_chains/common/production/new_chain.json",
        "recruitment_workbench/interface/roomier.json",
        "statecraft/common/defines/statecraft.json",
        "statecraft/common/opinion_modifiers/memory.json",
    };

    // Categories whose files legitimately contain scalar settings, arrays, or historical wrappers.
    private static readonly HashSet<string> NonEntityObjectCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "common/coups", "common/deals", "common/defines", "common/deterrence", "common/diagnostics",
        "common/flags", "common/governance", "common/inflation", "common/militia", "common/news",
        "common/performance", "common/programme", "common/secession", "common/stability",
        "common/world_layers", "history/conflicts", "history/diplomacy", "history/factions",
        "history/provinces", "history/units", "map_data", "maps", "interface", "gui", "fonts", "sound",
        "music", "gfx", "presentation", "skins"
    };

    private static readonly HashSet<string> WrapperAllowedCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "common/ai_strategy", "common/budget", "common/coups", "common/deals", "common/deal_terms",
        "common/deterrence", "common/dynasties", "common/editor", "common/event_pictures",
        "common/flag_designs", "common/flags",
        "common/governance", "common/handbook", "common/naming", "common/news",
        "common/organizations", "common/peace_terms", "common/programme", "common/rulers",
        "history/conflicts", "history/diplomacy", "history/factions", "history/faiths", "history/pops",
        "map_data", "maps", "interface", "gui", "fonts", "sound", "music", "gfx", "presentation", "skins"
    };

    private static readonly HashSet<string> ForbiddenWrapperKeys = new(StringComparer.OrdinalIgnoreCase)
    {
        "items", "entries", "records", "definitions", "list", "catalog",
        "governments", "ideologies", "technologies", "tactical_doctrines", "strategic_doctrines",
        "support", "subjects", "trade", "events", "decisions", "promises", "goals", "modifiers",
        "opinion_modifiers", "religions", "cultures", "professions", "movements", "laws", "agendas",
        "national_courses", "social_memory", "public_outreach", "alliance_programs",
        "formable_nations", "escalation", "addresses", "covert_operations", "generals",
        "dynasties", "character_names", "economic_shocks", "buildings", "goods", "units",
        "states", "countries"
    };

    private static readonly HashSet<string> NonSnakeKeyCategories = new(StringComparer.OrdinalIgnoreCase)
    {
        "common/buildings", "common/countries", "common/country_merges", "common/defines",
        "common/editor", "common/goods", "history/countries", "history/units"
    };

    // Shipped legacy keys preserved temporarily on individual files in game/ or templates.
    private static readonly HashSet<string> AllowedNonSnakeFiles = new(StringComparer.Ordinal)
    {
        "common/countries/05_faction_states.json",
        "gfx/leaders/anonymous_portrait.source.json",
    };

    private static readonly HashSet<string> AllowedLegacyKeyTokens = new(StringComparer.Ordinal)
    {
        "_comment"
    };

    private static readonly string[] ForbiddenTemplateCompetitorWords =
    {
        "Paradox", "Age of History", "HOI4", "Victoria 3", "Europa Universalis", "Crusader Kings"
    };

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("content shape: " + label);
        Console.WriteLine("PASS content shape: " + label);
    }

    [TestSuite("--content-shape-only", Description = "Enforces DATA_RULES_RU.md naming, snake_case IDs, wrapper rules, and competitor checks across game/ and mods/_templates/.")]
    public static void Run()
    {
        var gameRoot = Path.GetFullPath(GameDataLoader.DataFolder());
        var repoRoot = Path.GetDirectoryName(gameRoot)!;
        var templatesRoot = Path.Combine(repoRoot, "mods", "_templates");

        var acceptedExceptions = new Dictionary<string, int>(StringComparer.Ordinal);
        var violations = new List<string>();

        int gameCount = ScanTree(gameRoot, isTemplateTree: false, acceptedExceptions, violations);
        int templateCount = ScanTree(templatesRoot, isTemplateTree: true, acceptedExceptions, violations);
        ScanTemplatesCompetitorMentions(templatesRoot, violations);

        foreach (var pair in acceptedExceptions.OrderBy(p => p.Key, StringComparer.Ordinal))
            Console.WriteLine($"INFO content shape exception [{pair.Key}]: {pair.Value}");

        if (violations.Count > 0)
        {
            foreach (var v in violations.Take(20))
                Console.WriteLine("FAIL content shape: " + v);
            throw new Exception($"content shape: {violations.Count} violation(s) found; first: {violations[0]}");
        }

        Check(gameCount >= 300, $"validated {gameCount} data files under game/");
        Check(templateCount >= 30, $"validated {templateCount} data files under mods/_templates/");
        RunNegativeSelfTests();
    }

    private static int ScanTree(
        string root,
        bool isTemplateTree,
        Dictionary<string, int> acceptedExceptions,
        List<string> violations)
    {
        int count = 0;
        foreach (var path in Directory.EnumerateFiles(root, "*.*", SearchOption.AllDirectories).OrderBy(p => p, StringComparer.Ordinal))
        {
            var ext = Path.GetExtension(path);
            if (!ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase))
                continue;

            var relFromTree = Path.GetRelativePath(root, path).Replace('\\', '/');
            var dataRel = isTemplateTree ? StripTemplatePrefix(relFromTree) : relFromTree;
            if (ShouldSkipNonDataFile(dataRel, isTemplateTree)) continue;

            count++;
            var content = File.ReadAllText(path);
            ValidateDataFile(relFromTree, dataRel, isTemplateTree, content, acceptedExceptions, violations);
        }
        return count;
    }

    private static string StripTemplatePrefix(string relFromTemplates)
    {
        var slash = relFromTemplates.IndexOf('/');
        return slash > 0 ? relFromTemplates[(slash + 1)..] : relFromTemplates;
    }

    private static bool ShouldSkipNonDataFile(string dataRel, bool isTemplateTree)
    {
        var name = Path.GetFileName(dataRel);
        if (SkippedMetaNames.Contains(name)) return true;
        if (dataRel.StartsWith("localization/", StringComparison.OrdinalIgnoreCase)) return true;
        if (isTemplateTree && (dataRel.StartsWith("corecode/", StringComparison.OrdinalIgnoreCase) ||
                               dataRel.StartsWith("csharp/", StringComparison.OrdinalIgnoreCase) ||
                               dataRel.StartsWith("scripts/", StringComparison.OrdinalIgnoreCase) ||
                               dataRel.Contains("/bin/", StringComparison.OrdinalIgnoreCase) ||
                               dataRel.Contains("/obj/", StringComparison.OrdinalIgnoreCase)))
            return true;
        return false;
    }

    internal static void ValidateDataFile(
        string relFromTree,
        string dataRel,
        bool isTemplateTree,
        string rawContent,
        Dictionary<string, int> acceptedExceptions,
        List<string> violations)
    {
        var fileName = Path.GetFileName(dataRel);
        var category = CategoryOf(dataRel);

        // 1. File naming: NN_topic.{json,yml,yaml} (or skins/<id>/skin.json)
        if (!FileNameRule.IsMatch(fileName))
        {
            if (EraSkinFileRule.IsMatch(dataRel))
                CountException(acceptedExceptions, "filename:skins/<id>/skin.json");
            else if (MapPackJsonFileRule.IsMatch(dataRel))
                CountException(acceptedExceptions, "filename:maps/<id>/(map|compiled).json");
            else if (StateIsoFileRule.IsMatch(dataRel))
                CountException(acceptedExceptions, "filename:history/states/<TAG>");
            else if (!isTemplateTree && LegacyGameFileNames.Contains(dataRel))
                CountException(acceptedExceptions, "filename:legacy_game");
            else if (isTemplateTree && LegacyTemplateFileNames.Contains(relFromTree))
                CountException(acceptedExceptions, "filename:legacy_template");
            else
                violations.Add($"{relFromTree}: file name '{fileName}' must follow NN_topic.json|yml|yaml");
        }

        // 2. Parse via game's own StructuredData parser (supports JSON + comments/trailing commas + YAML)
        JsonDocument doc;
        try
        {
            doc = StructuredData.ParseDocument(relFromTree, rawContent);
        }
        catch (Exception ex)
        {
            violations.Add($"{relFromTree}: parse failed: {ex.Message}");
            return;
        }

        using (doc)
        {
            var root = doc.RootElement;
            if (root.ValueKind != JsonValueKind.Object)
            {
                violations.Add($"{relFromTree}: root must be JSON object (got {root.ValueKind})");
                return;
            }

            // 3. No legacy deletion keys ("removed" / "disabled") anywhere; only "remove": bool
            CheckDeletionKeys(relFromTree, root, violations);

            // 4. Value shape & wrapper & top-level key rules
            var topKeys = new List<(string Name, JsonValueKind Kind)>();
            foreach (var prop in root.EnumerateObject())
            {
                if (prop.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                topKeys.Add((prop.Name, prop.Value.ValueKind));

                if (!SnakeCaseIdRule.IsMatch(prop.Name) && !AllowedLegacyKeyTokens.Contains(prop.Name))
                {
                    if (NonSnakeKeyCategories.Contains(category) || AllowedNonSnakeFiles.Contains(dataRel))
                        CountException(acceptedExceptions, $"key_casing:{category}");
                    else
                        violations.Add($"{relFromTree}: top-level key '{prop.Name}' must be snake_case");
                }

                if (prop.Value.ValueKind != JsonValueKind.Object)
                {
                    bool allowedScalarOrList =
                        NonEntityObjectCategories.Contains(category) ||
                        (dataRel == "common/countries/05_faction_states.json" && prop.Name == "_comment");
                    if (allowedScalarOrList)
                        CountException(acceptedExceptions, $"non_object_value:{category}");
                    else
                        violations.Add($"{relFromTree}: entry '{prop.Name}' must be an object (got {prop.Value.ValueKind})");
                }
            }

            // 5. Reject new wrapper keys in flat catalog folders
            if (!WrapperAllowedCategories.Contains(category) && topKeys.Count > 0 && topKeys.Count <= 2)
            {
                foreach (var (k, kind) in topKeys)
                {
                    if (kind == JsonValueKind.Object && ForbiddenWrapperKeys.Contains(k))
                        violations.Add($"{relFromTree}: disallowed wrapper key '{k}' (use flat id -> object entries)");
                }
            }
        }
    }

    private static void CheckDeletionKeys(string relFromTree, JsonElement element, List<string> violations)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            foreach (var prop in element.EnumerateObject())
            {
                if (prop.Name.Equals("removed", StringComparison.OrdinalIgnoreCase) ||
                    prop.Name.Equals("disabled", StringComparison.OrdinalIgnoreCase))
                {
                    // Note: common/settings defines real field keys? Verify: earlier LEGACY_DEL was literally []!
                    violations.Add($"{relFromTree}: legacy deletion alias '{prop.Name}' is forbidden; use \"remove\": true");
                }
                else if (prop.Name.Equals("remove", StringComparison.Ordinal) &&
                         prop.Value.ValueKind is not JsonValueKind.True and not JsonValueKind.False)
                {
                    violations.Add($"{relFromTree}: \"remove\" value must be boolean");
                }
                CheckDeletionKeys(relFromTree, prop.Value, violations);
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            foreach (var item in element.EnumerateArray())
                CheckDeletionKeys(relFromTree, item, violations);
        }
    }

    private static string CategoryOf(string dataRel)
    {
        var parts = dataRel.Split('/', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2 &&
            (parts[0].Equals("common", StringComparison.OrdinalIgnoreCase) ||
             parts[0].Equals("history", StringComparison.OrdinalIgnoreCase)))
            return parts[0].ToLowerInvariant() + "/" + parts[1].ToLowerInvariant();
        return parts.Length > 0 ? parts[0].ToLowerInvariant() : "";
    }

    private static void CountException(Dictionary<string, int> counts, string key) =>
        counts[key] = counts.TryGetValue(key, out var existing) ? existing + 1 : 1;

    private static void ScanTemplatesCompetitorMentions(string templatesRoot, List<string> violations)
    {
        foreach (var path in Directory.EnumerateFiles(templatesRoot, "*.*", SearchOption.AllDirectories))
        {
            var ext = Path.GetExtension(path);
            if (!ext.Equals(".md", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".json", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".yml", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".yaml", StringComparison.OrdinalIgnoreCase) &&
                !ext.Equals(".txt", StringComparison.OrdinalIgnoreCase))
                continue;
            var text = File.ReadAllText(path);
            foreach (var forbidden in ForbiddenTemplateCompetitorWords)
            {
                if (text.Contains(forbidden, StringComparison.OrdinalIgnoreCase))
                    violations.Add($"{Path.GetRelativePath(templatesRoot, path)}: contains forbidden third-party reference '{forbidden}'");
            }
        }
    }

    private static void RunNegativeSelfTests()
    {
        AssertRejects("common/governments/bad_name.json", "{\"valid_id\":{\"name\":\"Ok\"}}", "NN_topic");
        AssertRejects("common/governments/90_bad_id.json", "{\"CamelId\":{\"name\":\"Ok\"}}", "snake_case");
        AssertRejects("common/governments/90_wrapper.json", "{\"governments\":{\"my_gov\":{\"name\":\"Ok\"}}}", "wrapper");
        AssertRejects("common/governments/90_scalar.json", "{\"my_gov\":42}", "must be an object");
        AssertRejects("common/governments/90_legacy_del.json", "{\"theocracy\":{\"disabled\":true}}", "forbidden");
        AssertRejects("common/governments/90_legacy_del2.json", "{\"theocracy\":{\"removed\":true}}", "forbidden");
        AssertAccepts("common/governments/90_good_remove.json", "{\"theocracy\":{\"remove\":true}}");
        AssertAccepts("skins/classic/skin.json", "{\"id\":\"classic\",\"name\":\"Classic\"}");
        Console.WriteLine("PASS content shape: negative self-tests reject bad filename, bad key, wrapper, scalar, legacy delete");
    }

    private static void AssertRejects(string rel, string json, string expectedFragment)
    {
        var violations = new List<string>();
        ValidateDataFile(rel, rel, isTemplateTree: false, json, new Dictionary<string, int>(), violations);
        if (!violations.Any(v => v.Contains(expectedFragment, StringComparison.OrdinalIgnoreCase)))
            throw new Exception($"content shape self-test: expected rejection containing '{expectedFragment}' for {rel}, got: {string.Join("; ", violations)}");
    }

    private static void AssertAccepts(string rel, string json)
    {
        var violations = new List<string>();
        ValidateDataFile(rel, rel, isTemplateTree: false, json, new Dictionary<string, int>(), violations);
        if (violations.Count > 0)
            throw new Exception($"content shape self-test: unexpected violation for {rel}: {string.Join("; ", violations)}");
    }
}
