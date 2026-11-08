using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Conditions and consequences that have names: `common/scripted_triggers` and
/// `common/scripted_effects`.
///
/// The game already had a scripted language - `min_stability`, `treasury_percent`, and the
/// rest - and every event option was written in it. What it did not have was a way to
/// write a rule once. "This country is at war and can afford it" was spelled out again in
/// every piece of content that needed it, and a modder rebalancing the game had to find
/// each copy. That is the gap `scripted_triggers` and `scripted_effects` fill in a Paradox
/// title, and it is why they are folders rather than a section of one file: a mod adds its
/// own library beside the base game's and refers to both.
///
/// The two libraries are used the same way. Anything that takes a trigger or an effect
/// also takes a `use` list. Effects expand at the position of that key; put `use` first
/// when local entries should run afterwards. Numeric trigger bounds combine with AND;
/// tags are alternatives, and contradictory at_war clauses should not be composed:
///
///   "trigger": { "use": [ "at_war", "solvent" ], "min_war_support": 0.30 }
///   "effects": { "use": [ "war_mobilisation" ], "treasury_percent": -0.01 }
///
/// A name nobody wrote is reported and skipped, so one typo costs that line rather than
/// the file it is in.
/// </summary>
public static class ScriptLibrary
{
    private static readonly Dictionary<string, ScriptedTrigger> FeatureTriggers = new(StringComparer.OrdinalIgnoreCase);

    public static bool MatchesTrigger(Campaign campaign, Nation nation, string name)
    {
        EnsureLoaded();
        if (!FeatureTriggers.TryGetValue(name, out var trigger))
        {
            if (!TryTrigger(name, out var block)) return false;
            var errors = new List<string>();
            trigger = ScriptedTrigger.Parse(block, "feature unlock/" + name, errors);
            // Invalid conditions fail closed rather than enabling a mechanic by accident.
            if (errors.Count > 0 || trigger.IsEmpty) trigger = null;
            FeatureTriggers[name] = trigger;
        }
        return trigger is not null && trigger.Matches(campaign, nation);
    }
    private static readonly Dictionary<string, JsonDocument> TriggerBlocks =
        new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, JsonDocument> EffectBlocks =
        new(StringComparer.OrdinalIgnoreCase);
    /// <summary>
    /// Triggers inside AI scores, parsed once instead of once per question.
    ///
    /// A score is asked of two hundred and sixty cabinets, for every decision they have,
    /// every month - and every one of those asks was re-parsing the score's conditions out
    /// of JSON and composing a fresh context string to blame if the parse failed. The
    /// conditions never change between asks; only the country does. Parsing them once and
    /// keeping them is the whole optimisation, and it costs one dictionary that is emptied
    /// whenever the scripts are reloaded.
    /// </summary>
    private static readonly Dictionary<(string Name, int Index), ScriptedTrigger> ScoreTriggers = new();

    private static readonly Dictionary<string, JsonDocument> ScoreBlocks =
        new(StringComparer.OrdinalIgnoreCase);

    public static int TriggerCount => TriggerBlocks.Count;
    public static int EffectCount => EffectBlocks.Count;
    public static int ScoreCount => ScoreBlocks.Count;

    public static IEnumerable<string> TriggerNames => TriggerBlocks.Keys;
    public static IEnumerable<string> EffectNames => EffectBlocks.Keys;
    public static IEnumerable<string> ScoreNames => ScoreBlocks.Keys;

    public static bool HasTrigger(string name) =>
        !string.IsNullOrEmpty(name) && TriggerBlocks.ContainsKey(name);

    public static bool HasEffect(string name) =>
        !string.IsNullOrEmpty(name) && EffectBlocks.ContainsKey(name);

    public static bool HasScore(string name) =>
        !string.IsNullOrEmpty(name) && ScoreBlocks.ContainsKey(name);

    internal static bool TryTrigger(string name, out JsonElement block) => Lookup(TriggerBlocks, name, out block);

    internal static bool TryEffect(string name, out JsonElement block) => Lookup(EffectBlocks, name, out block);

    private static bool Lookup(Dictionary<string, JsonDocument> blocks, string name, out JsonElement block)
    {
        if (!string.IsNullOrEmpty(name) && blocks.TryGetValue(name, out var document))
        {
            block = document.RootElement;
            return true;
        }
        block = default;
        return false;
    }

    private static bool _loaded;

    /// <summary>
    /// Load the libraries once, on the first thing that needs them.
    ///
    /// Decisions refer to named triggers while they are being read, and a catalogue's
    /// static constructor runs on whichever line touches it first - so the library cannot
    /// wait to be told.
    /// </summary>
    public static void EnsureLoaded()
    {
        if (!_loaded) ResetCatalog();
    }

    /// <summary>Read the base game's three libraries, replacing whatever was loaded.</summary>
    public static void ResetCatalog()
    {
        _loaded = true;
        Clear(TriggerBlocks);
        Clear(EffectBlocks);
        Clear(ScoreBlocks);
        ScoreTriggers.Clear();
        FeatureTriggers.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "scripted_triggers"))
            using (document) Absorb(TriggerBlocks, document.RootElement);
        foreach (var document in GameDataLoader.DataDocuments("common", "scripted_effects"))
            using (document) Absorb(EffectBlocks, document.RootElement);
        foreach (var document in GameDataLoader.DataDocuments("common", "scripted_modifiers"))
            using (document) Absorb(ScoreBlocks, document.RootElement);
    }

    /// <summary>Layer one package's libraries over what is already loaded.</summary>
    public static void LoadDirectories(string packageRoot, List<string> errors)
    {
        FeatureTriggers.Clear();
        Read(Path.Combine(packageRoot, "common", "scripted_triggers"), "scripted_triggers",
            TriggerBlocks, errors);
        Read(Path.Combine(packageRoot, "common", "scripted_effects"), "scripted_effects",
            EffectBlocks, errors);
        Read(Path.Combine(packageRoot, "common", "scripted_modifiers"), "scripted_modifiers",
            ScoreBlocks, errors);
    }

    public static void MergeTriggers(JsonElement root)
    {
        EnsureLoaded();
        FeatureTriggers.Clear();
        Absorb(TriggerBlocks, root);
    }

    /// <summary>
    /// What this country makes of a named score.
    ///
    /// A base and a list of conditions that add to it or scale it - the shape Paradox uses
    /// for every "how much does the AI want this" number in the game. It exists so that
    /// "the AI mobilises too eagerly" is a line in a file rather than a rebuild.
    /// </summary>
    public static float Score(Campaign campaign, Nation nation, string name, float fallback = 0f)
    {
        if (nation is null || string.IsNullOrEmpty(name) ||
            !ScoreBlocks.TryGetValue(name, out var document)) return fallback;

        var root = document.RootElement;
        if (root.ValueKind == JsonValueKind.Number && root.TryGetSingle(out var flat)) return flat;
        if (root.ValueKind != JsonValueKind.Object) return fallback;

        var score = root.TryGetProperty("base", out var start) && start.TryGetSingle(out var opening)
            ? opening
            : 0f;
        if (!root.TryGetProperty("modifiers", out var modifiers) ||
            modifiers.ValueKind != JsonValueKind.Array) return score;

        var index = -1;
        foreach (var entry in modifiers.EnumerateArray())
        {
            index++;
            if (entry.ValueKind != JsonValueKind.Object) continue;
            if (entry.TryGetProperty("trigger", out var condition))
            {
                var key = (name, index);
                if (!ScoreTriggers.TryGetValue(key, out var trigger))
                    ScoreTriggers[key] = trigger = ScriptedTrigger.Parse(condition, "scripted_modifiers/" + name, null);
                if (!trigger.Matches(campaign, nation)) continue;
            }
            if (entry.TryGetProperty("add", out var add) && add.TryGetSingle(out var addition))
                score += addition;
            if ((entry.TryGetProperty("factor", out var factor) || entry.TryGetProperty("multiply", out factor)) && factor.TryGetSingle(out var multiplier))
                score *= multiplier;
            if (entry.TryGetProperty("luau_formula", out var rowFormula) && rowFormula.ValueKind == JsonValueKind.String)
                score = LuauFormulaEvaluator.Evaluate(rowFormula.GetString(), NationFormulaSnapshot.From(campaign, nation, score), score);
        }
        if (root.TryGetProperty("luau_formula", out var finalFormula) && finalFormula.ValueKind == JsonValueKind.String)
            score = LuauFormulaEvaluator.Evaluate(finalFormula.GetString(), NationFormulaSnapshot.From(campaign, nation, score), score);
        return score;
    }

    private static void Clear(Dictionary<string, JsonDocument> blocks)
    {
        foreach (var document in blocks.Values) document.Dispose();
        blocks.Clear();
    }

    private static void Absorb(Dictionary<string, JsonDocument> blocks, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (blocks.TryGetValue(entry.Name, out var previous)) previous.Dispose();
            blocks[entry.Name] = JsonDocument.Parse(entry.Value.GetRawText(), GameDataLoader.ForgivingJson);
        }
    }

    private static void Read(string directory, string category,
        Dictionary<string, JsonDocument> blocks, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = StructuredData.Files(directory, SearchOption.TopDirectoryOnly)
                .OrderBy(Path.GetFileName, StringComparer.OrdinalIgnoreCase)
                .ToArray();
        }
        catch (Exception exception)
        {
            errors?.Add($"{category}: {exception.Message}");
            return;
        }

        foreach (var file in files)
        {
            var origin = $"{category}/{Path.GetFileName(file)}";
            try
            {
                using var document = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                Absorb(blocks, document.RootElement);
            }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }
    }
}
