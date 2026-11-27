using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Copy-on-write data authoring. Never writes into the source game or runs mod code.</summary>
public static class ContentWorkbench
{
    public static readonly string[] Roots = { "common", "events", "gui", "interface", "localization", "fonts", "sound", "music", "scenarios", "history", "map_data", "gfx", "maps" };
    /// <summary>
    /// Whether the editor may write this file: a structured document (JSON, or the YAML the
    /// loaders read just the same) in a declared content folder, or a province-ownership table in
    /// history/provinces. The workbench used to accept only .json, so a mod written in YAML -
    /// which every loader reads - could be neither listed, opened, validated nor saved by it.
    /// </summary>
    public static bool IsEditable(string relative)
    {
        var path = (relative ?? "").Replace('\\', '/');
        var parts = path.Split('/');
        if (parts.Length < 2 || Array.IndexOf(Roots, parts[0]) < 0 ||
            parts.Any(part => part.Length == 0 || part is "." or "..")) return false;
        // Scenario interchange remains JSON. Data overlays and event catalogues may use YAML.
        if (parts[0] == "scenarios" && StructuredData.IsYaml(path)) return false;
        if (StructuredData.IsDocument(path)) return true;
        return IsOwnershipTable(path);
    }

    /// <summary>True for a .yaml or .yml file, which is edited as text and validated through the YAML reader.</summary>
    public static bool IsYaml(string relative) => StructuredData.IsYaml(relative ?? "");

    private static bool IsOwnershipTable(string path) =>
        path.StartsWith("history/provinces/", StringComparison.OrdinalIgnoreCase) &&
        path.EndsWith(".csv", StringComparison.OrdinalIgnoreCase);

    private static void RequireEditable(string relative)
    {
        if (!IsEditable(relative))
            throw new InvalidDataException("Only JSON or YAML content in declared game folders (or history/provinces/*.csv) can be edited.");
    }

    public static string Validate(string relative, string text)
    {
        // Validate only the draft, never Load/Merge a live catalogue while inspecting it.
        RequireEditable(relative);
        var editedPath = (relative ?? "").Replace('\\', '/');
        if (IsOwnershipTable(editedPath))
        {
            var problems = new List<string>();
            var line = 0;
            foreach (var raw in text.Split('\n'))
            {
                line++;
                var row = raw.Trim();
                if (row.Length == 0 || row[0] == '#' || row.StartsWith("province;", StringComparison.OrdinalIgnoreCase)) continue;
                var parts = row.Split(';');
                if (parts.Length < 2 || !int.TryParse(parts[0].Trim(), out var province) || province <= 0 || parts[1].Trim().Length == 0)
                    problems.Add($"line {line}: expected province;owner[;controller]");
            }
            if (problems.Count > 0) throw new InvalidDataException(string.Join("\n", problems.Take(20)));
            return "Province table valid. Provinces are checked against the map when the game loads it.";
        }
        // Shared draft/file parsing retains path and line diagnostics for malformed YAML.
        using var doc = StructuredData.ParseDocument(relative, text);
        if (editedPath.StartsWith("common/flag_designs/", StringComparison.OrdinalIgnoreCase))
        {
            var errors = new List<string>();
            var designs = FlagDesignCatalog.Read(doc.RootElement, errors);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return $"Flag designs valid ({designs.Count}).";
        }
        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new InvalidDataException("A content document must be an object or array.");
        if (editedPath.StartsWith("events/", StringComparison.OrdinalIgnoreCase) && doc.RootElement.ValueKind == JsonValueKind.Object)
        {
            var errors = new List<string>();
            void CheckScript(JsonElement body, string context)
            {
                if (body.ValueKind != JsonValueKind.Object) return;
                if (body.TryGetProperty("effects", out var effects)) ScriptedEffects.Parse(effects, context, errors);
                if (body.TryGetProperty("trigger", out var trigger)) ScriptedTrigger.Parse(trigger, context, errors);
            }
            foreach (var entry in doc.RootElement.EnumerateObject())
            {
                if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                var context = relative + "/" + entry.Name;
                CheckScript(entry.Value, context);
                if (entry.Value.TryGetProperty("options", out var options) && options.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var option in options.EnumerateArray()) CheckScript(option, context + "/options/" + index++);
                }
            }
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return "Event scripts valid. The live campaign has not been changed.";
        }
        if (relative.Replace('\\', '/').StartsWith("common/formable_nations/", StringComparison.OrdinalIgnoreCase))
        {
            var errors = new List<string>();
            FormableNations.Validate(doc.RootElement, errors);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return "Formable nation rules valid. The live catalogue has not been changed.";
        }
        var contentPath = relative.Replace('\\', '/');
        if (contentPath.StartsWith("common/editor/", StringComparison.OrdinalIgnoreCase))
        {
            var errors = new List<string>(); ScenarioInspectorCatalog.Validate(doc.RootElement, errors);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return "Scenario inspector rules valid. The live catalogue has not been changed.";
        }
        if (contentPath.StartsWith("common/production/", StringComparison.OrdinalIgnoreCase) ||
            contentPath.StartsWith("common/programme/", StringComparison.OrdinalIgnoreCase))
        {
            var errors = new List<string>();
            if (contentPath.StartsWith("common/production/", StringComparison.OrdinalIgnoreCase))
                ProductionChains.Validate(doc.RootElement, errors);
            else ProductionProgramme.Validate(doc.RootElement, errors);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return "Production rules valid. The live catalogue has not been changed.";
        }
        if (contentPath.StartsWith("maps/", StringComparison.OrdinalIgnoreCase))
        {
            if (doc.RootElement.ValueKind != JsonValueKind.Object)
                throw new InvalidDataException("A map pack manifest must be a JSON object.");
            var r = doc.RootElement;
            if (!r.TryGetProperty("id", out var idEl) || idEl.ValueKind != JsonValueKind.String ||
                !System.Text.RegularExpressions.Regex.IsMatch((idEl.GetString() ?? "").Trim(), "^[a-z0-9_]{2,48}$"))
                throw new InvalidDataException("Map pack 'id' must be 2-48 lowercase letters, digits, or underscores.");
            if (!r.TryGetProperty("name", out var nameEl) || nameEl.ValueKind != JsonValueKind.String ||
                string.IsNullOrWhiteSpace(nameEl.GetString()))
                throw new InvalidDataException("Map pack requires a non-empty 'name'.");
            var errors = new List<string>();
            CheckInt(r, "map_pack", "cells_per_province", 12, 10000, errors);
            CheckInt(r, "map_pack", "disputed_cells_per_province", 12, 10000, errors);
            CheckFloat(r, "map_pack", "siberia_multiplier", 0.25f, 16f, errors);
            CheckFloat(r, "map_pack", "polar_multiplier", 0.25f, 16f, errors);
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return "Map pack manifest valid. Restart or start a new campaign to generate the world.";
        }
        if (contentPath.StartsWith("common/countries/", StringComparison.OrdinalIgnoreCase))
        {
            var errors = new List<string>();
            foreach (var (id, body) in EnumerateDefinitions(doc.RootElement, "common/countries", errors))
            {
                if ((body.TryGetProperty("color", out var col) || body.TryGetProperty("colour", out col)) &&
                    col.ValueKind == JsonValueKind.String &&
                    !System.Text.RegularExpressions.Regex.IsMatch((col.GetString() ?? "").Trim(), "^#[0-9A-Fa-f]{6}$"))
                    errors.Add($"country '{id}': colour must be a 6-digit hex literal such as #3A7CA5.");
                CheckFloat(body, $"country '{id}'", "starting_treasury", -100000f, 1000000f, errors);
                CheckFloat(body, $"country '{id}'", "treasury", -100000f, 1000000f, errors);
            }
            if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors));
            return "Country definitions valid. The live catalogue has not been changed.";
        }
        if (TryValidateCatalogDocument(contentPath, doc.RootElement, out var domainMessage))
            return domainMessage;
        if (relative.Replace('\\', '/').EndsWith("/world.json", StringComparison.OrdinalIgnoreCase))
            SaveSystem.ImportScenarioJson(text, "validation");
        if (relative.Replace('\\', '/').EndsWith("/scenario.json", StringComparison.OrdinalIgnoreCase))
        {
            var r = doc.RootElement;
            if (!r.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()) ||
                !r.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidDataException("Scenario requires id and name.");
        }
        return StructuredData.IsYaml(relative)
            ? "YAML valid. Domain rules are also checked when the game loads this content."
            : "JSON valid. Domain rules are also checked when the game loads this content.";
    }

    public static string Write(string modRoot, string relative, string text)
    {
        RequireEditable(relative);
        var target = ScenarioLibrary.SafePath(modRoot, relative);
        RejectLinks(target);
        return Write(modRoot, relative, text, File.Exists(target) ? File.ReadAllText(target) : null);
    }

    /// <summary>Null expects a new file; otherwise refuse an edit made since the draft opened.</summary>
    public static string Write(string modRoot, string relative, string text, string expected)
    {
        // expected — точный текст на момент открытия. null значит «файла ещё нет»,
        // а не «перезаписывать без проверки». Это защищает работу соседнего редактора.
        WriteBatch(modRoot, new Dictionary<string, string> { [relative] = text },
            new Dictionary<string, string> { [relative] = expected });
        var target = ScenarioLibrary.SafePath(modRoot, relative);
        return target;
    }

    /// <summary>Publish a small related document set, with conflict checks and rollback.
    /// Null expected contents mean a new file: never silently overwrite a colliding ID.</summary>
    public static void WriteBatch(string modRoot, IReadOnlyDictionary<string, string> documents,
        IReadOnlyDictionary<string, string> expected)
    {
        // Сначала подготовка/проверка всех файлов, затем публикация. Не сокращать до
        // цикла File.WriteAllText: при ошибке второго файла первый уже станет новым,
        // и связанный набор документов распадётся на несовместимые версии.
        var staged = new List<(string Target, string Temp, byte[] Previous, byte[] Backup, string Expected)>();
        var targets = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var committed = 0;
        try
        {
            foreach (var (relative, text) in documents)
            {
                RequireEditable(relative);
                Validate(relative, text);
                if (!expected.TryGetValue(relative, out var baseline)) throw new InvalidDataException("Missing document baseline.");
                var target = ScenarioLibrary.SafePath(modRoot, relative);
                if (!targets.Add(target)) throw new InvalidDataException("A document target was declared twice.");
                RejectLinks(target); RejectLinks(target + ".bak");
                foreach (var file in new[] { target, target + ".bak" })
                    if (File.Exists(file) && (File.GetAttributes(file) & FileAttributes.ReadOnly) != 0)
                        throw new InvalidDataException("A scenario file or backup is read-only: " + file);
                CheckBaseline(target, baseline);
                Directory.CreateDirectory(Path.GetDirectoryName(target));
                var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
                staged.Add((target, temporary, File.Exists(target) ? File.ReadAllBytes(target) : null,
                    File.Exists(target + ".bak") ? File.ReadAllBytes(target + ".bak") : null, baseline));
                File.WriteAllText(temporary, text);
            }
            // Recheck after preparing all files. No discoverable document is changed until
            // validation, path guards, write preparation and conflict checks all succeeded.
            foreach (var file in staged) CheckBaseline(file.Target, file.Expected);
            foreach (var file in staged)
            {
                if (file.Previous is not null) File.Replace(file.Temp, file.Target, file.Target + ".bak");
                else File.Move(file.Temp, file.Target);
                committed++;
            }
        }
        catch (Exception failure)
        {
            var rollbackFailures = new List<Exception>();
            for (var i = committed - 1; i >= 0; i--)
            {
                var file = staged[i];
                try
                {
                    if (file.Previous is null) File.Delete(file.Target);
                    else RestoreFile(file.Target, file.Previous);
                    if (file.Backup is null) File.Delete(file.Target + ".bak");
                    else RestoreFile(file.Target + ".bak", file.Backup);
                }
                catch (Exception error) { rollbackFailures.Add(error); }
            }
            if (rollbackFailures.Count > 0)
                throw new AggregateException("Save failed and could not fully roll back. Keep the draft open and inspect the scenario files and .bak backups.",
                    new[] { failure }.Concat(rollbackFailures));
            throw;
        }
        finally
        {
            foreach (var file in staged)
                if (File.Exists(file.Temp)) File.Delete(file.Temp);
        }
    }
    private static void CheckBaseline(string target, string expected)
    {
        if (expected is null ? File.Exists(target) : !File.Exists(target) || File.ReadAllText(target) != expected)
            throw new InvalidDataException("Content changed outside this editor, or the document already exists. Nothing was overwritten. Reopen it from the file list.");
    }
    private static void RestoreFile(string target, byte[] bytes)
    {
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllBytes(temporary, bytes);
            if (File.Exists(target)) File.Replace(temporary, target, null);
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    internal static void RejectLinks(string target)
    {
        for (var p = target; !string.IsNullOrEmpty(p); p = Path.GetDirectoryName(p))
            if ((Directory.Exists(p) || File.Exists(p)) && (File.GetAttributes(p) & FileAttributes.ReparsePoint) != 0)
                throw new InvalidDataException("Linked files and folders are not writable by the editor.");
    }
    public static void EnsureMod(string root)
    {
        var manifest = Path.Combine(root, "manifest.json");
        RejectLinks(manifest);
        Directory.CreateDirectory(root);
        if (!File.Exists(manifest)) File.WriteAllText(manifest,
            "{\"id\":\"editor_workspace\",\"name\":\"Editor workspace\",\"version\":\"1.0.0\",\"enabled\":true,\"load_order\":10000}");
    }

    public static string SetValue(string json, string pointer, string value, string relative = "content.json")
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/') throw new InvalidDataException("Select a property, not the document root.");
        var root = JsonNode.Parse(StructuredData.ToJson(relative, json), documentOptions: GameDataLoader.ForgivingJson);
        var parts = pointer.Split('/');
        JsonNode node = root;
        for (var i = 1; i < parts.Length - 1; i++) node = Child(node, Decode(parts[i]));
        var key = Decode(parts[^1]);
        var replacement = JsonNode.Parse(value);
        if (node is JsonArray a) a[int.Parse(key)] = replacement;
        else if (node is JsonObject o && o.ContainsKey(key)) o[key] = replacement;
        else throw new InvalidDataException("Selected property no longer exists. Refresh the property tree.");
        return StructuredData.FormatJson(relative, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    public static string AddValue(string json, string pointer, string key, string value, string relative = "content.json")
    {
        var root = JsonNode.Parse(StructuredData.ToJson(relative, json), documentOptions: GameDataLoader.ForgivingJson);
        var node = Resolve(root, pointer);
        var replacement = JsonNode.Parse(value);
        if (node is JsonObject o)
        {
            if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Enter a property name.");
            if (o.ContainsKey(key)) throw new InvalidDataException("Property already exists. Edit its value instead.");
            o.Add(key, replacement);
        }
        else if (node is JsonArray a) a.Add(replacement);
        else throw new InvalidDataException("Choose an object or array to add a child.");
        return StructuredData.FormatJson(relative, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    public static string RemoveValue(string json, string pointer, string relative = "content.json")
    {
        if (string.IsNullOrEmpty(pointer) || pointer[0] != '/') throw new InvalidDataException("The document root cannot be deleted.");
        var split = pointer.LastIndexOf('/'); var node = Resolve(JsonNode.Parse(StructuredData.ToJson(relative, json), documentOptions: GameDataLoader.ForgivingJson), pointer[..split]);
        var key = Decode(pointer[(split + 1)..]);
        var root = node;
        while (root.Parent is not null) root = root.Parent;
        if (node is JsonArray a) a.RemoveAt(int.Parse(key));
        else if (node is JsonObject o && o.ContainsKey(key)) o.Remove(key);
        else throw new InvalidDataException("Selected property no longer exists.");
        return StructuredData.FormatJson(relative, root.ToJsonString(new JsonSerializerOptions { WriteIndented = true }));
    }
    private static JsonNode Resolve(JsonNode root, string pointer)
    {
        if (pointer.Length == 0) return root;
        if (pointer[0] != '/') throw new InvalidDataException("Invalid property path.");
        var node = root;
        foreach (var part in pointer[1..].Split('/')) node = Child(node, Decode(part)) ?? throw new InvalidDataException("Property path no longer exists.");
        return node;
    }
    private static JsonNode Child(JsonNode node, string key) => node is JsonArray a ? a[int.Parse(key)] : node[key];
    private static string Decode(string key) => key.Replace("~1", "/").Replace("~0", "~");

    private static bool TryValidateCatalogDocument(string path, JsonElement root, out string message)
    {
        var errors = new List<string>();
        message = "";
        if (path.StartsWith("common/ideologies/", StringComparison.OrdinalIgnoreCase))
        {
            IdeologyCatalog.Validate(root, errors);
            message = "Ideology rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/governments/", StringComparison.OrdinalIgnoreCase))
        {
            GovernmentCatalog.Validate(root, errors);
            message = "Government rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/decisions/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDecisions(root, errors);
            message = "Decision rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/modifiers/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateModifiers(root, errors);
            message = "Modifier rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/strategic_doctrines/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDoctrines(root, errors);
            message = "Strategic doctrine rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/diseases/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("common/disasters/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateHazards(path, root, errors);
            message = "Crisis catalogue valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/technologies/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateTechnologies(root, errors);
            message = "Technology catalogue valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/units/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateUnits(root, errors);
            message = "Unit type catalogue valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/buildings/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateBuildings(root, errors);
            message = "Building catalogue valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/laws/", StringComparison.OrdinalIgnoreCase) ||
                 path.StartsWith("common/agendas/", StringComparison.OrdinalIgnoreCase))
        {
            ValidatePolicies(root, path.StartsWith("common/agendas/", StringComparison.OrdinalIgnoreCase), errors);
            message = "Policy rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/grand_projects/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateGrandProjects(root, errors);
            message = "Grand project rules valid. The live catalogue has not been changed.";
        }
        else if (path.StartsWith("common/defines/", StringComparison.OrdinalIgnoreCase))
        {
            ValidateDefines(root, errors);
            message = "Defines overrides valid. The live campaign has not been changed.";
        }
        else
        {
            return false;
        }
        if (errors.Count > 0) throw new InvalidDataException(string.Join("\n", errors.Take(20)));
        return true;
    }

    private static IEnumerable<(string Id, JsonElement Body)> EnumerateDefinitions(JsonElement root, string section, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors.Add($"{section}: expected a JSON/YAML object keyed by entry id.");
            yield break;
        }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors.Add($"{section}/{entry.Name}: entry must be an object.");
                continue;
            }
            if (GameDataLoader.IsRemoved(entry.Value)) continue;
            yield return (entry.Name, entry.Value);
        }
    }

    private static void CheckFloat(JsonElement body, string ctx, string key, float min, float max, List<string> errors, bool strictMin = false)
    {
        if (!body.TryGetProperty(key, out var raw)) return;
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetSingle(out var val) || !float.IsFinite(val) ||
            (strictMin ? val <= min : val < min) || val > max)
            errors.Add($"{ctx}: `{key}` must be a finite number in {(strictMin ? "(" : "[")}{min}, {max}].");
    }

    private static void CheckInt(JsonElement body, string ctx, string key, long min, long max, List<string> errors)
    {
        if (!body.TryGetProperty(key, out var raw)) return;
        if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetInt64(out var val) || val < min || val > max)
            errors.Add($"{ctx}: `{key}` must be an integer in [{min}, {max}].");
    }

    private static void ValidateDecisions(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/decisions", errors))
        {
            var ctx = $"decision '{id}'";
            CheckInt(body, ctx, "cooldown_days", 1, 36500, errors);
            CheckFloat(body, ctx, "gdp_cost_share", 0f, 1f, errors);
            if (body.TryGetProperty("kind", out var kindRaw) &&
                (kindRaw.ValueKind != JsonValueKind.String || !Enum.TryParse<DecisionKind>(kindRaw.GetString(), true, out _)))
                errors.Add($"{ctx}: unknown kind '{kindRaw}'.");
            if (body.TryGetProperty("resource_costs", out var rc)) DecisionResources.Parse(rc, $"{ctx} resource_costs", errors, out _);
            if (body.TryGetProperty("ai_resource_reserve", out var ar)) DecisionResources.Parse(ar, $"{ctx} ai_resource_reserve", errors, out _);
            if (body.TryGetProperty("trigger", out var trig)) ScriptedTrigger.Parse(trig, ctx, errors);
            if (body.TryGetProperty("effects", out var eff)) ScriptedEffects.Parse(eff, ctx, errors);
        }
    }

    private static void ValidateModifiers(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/modifiers", errors))
        {
            var ctx = $"modifier '{id}'";
            CheckInt(body, ctx, "days", 0, 36500, errors);
            if (body.TryGetProperty("positive", out var pos) && pos.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
                errors.Add($"{ctx}: `positive` must be boolean.");
            if (body.TryGetProperty("effects", out var eff)) ScriptedEffects.Parse(eff, ctx, errors);
            if (body.TryGetProperty("modifiers", out var mods)) NationalModifiers.Read(mods, ctx, errors);
        }
    }

    private static void ValidateDoctrines(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/strategic_doctrines", errors))
        {
            var ctx = $"strategic_doctrine '{id}'";
            if (!MechanicFeatures.ValidId(id)) errors.Add($"{ctx}: invalid id.");
            CheckFloat(body, ctx, "political_cost", 0f, 100000f, errors);
            CheckFloat(body, ctx, "training_slots", 0f, 100f, errors, strictMin: true);
            CheckFloat(body, ctx, "drill_experience", 0f, 100f, errors, strictMin: true);
            CheckFloat(body, ctx, "battle_experience", 0f, 100f, errors, strictMin: true);
            CheckFloat(body, ctx, "air_experience", 0f, 100f, errors, strictMin: true);
            CheckFloat(body, ctx, "naval_experience", 0f, 100f, errors, strictMin: true);
            if (body.TryGetProperty("modifiers", out var mods)) NationalModifiers.Read(mods, ctx, errors);
        }
    }

    private static void ValidateHazards(string path, JsonElement root, List<string> errors)
    {
        var label = path.StartsWith("common/diseases/", StringComparison.OrdinalIgnoreCase) ? "diseases" : "disasters";
        foreach (var (id, body) in EnumerateDefinitions(root, "common/" + label, errors))
        {
            var ctx = $"{label}/{id}";
            if (!MechanicFeatures.ValidId(id)) errors.Add($"{ctx}: invalid id.");
            CheckFloat(body, ctx, "annual_chance", 0f, 1f, errors);
            CheckFloat(body, ctx, "starting_severity", 0f, 100f, errors);
            CheckFloat(body, ctx, "min_abs_lat", 0f, 90f, errors);
            CheckFloat(body, ctx, "max_abs_lat", 0f, 90f, errors);
            if (body.TryGetProperty("min_abs_lat", out var minL) && minL.TryGetSingle(out var minVal) &&
                body.TryGetProperty("max_abs_lat", out var maxL) && maxL.TryGetSingle(out var maxVal) && minVal > maxVal)
                errors.Add($"{ctx}: `min_abs_lat` ({minVal}) cannot exceed `max_abs_lat` ({maxVal}).");
        }
    }

    private static void ValidateTechnologies(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/technologies", errors))
        {
            var ctx = $"technologies/{id}";
            CheckFloat(body, ctx, "cost", 0f, 1000000f, errors, strictMin: true);
            if (body.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String &&
                !TechnologyCatalog.Categories.Contains(cat.GetString() ?? "", StringComparer.OrdinalIgnoreCase))
                errors.Add($"{ctx}: unknown category '{cat.GetString()}' (expected: {string.Join(", ", TechnologyCatalog.Categories)}).");
            if (body.TryGetProperty("requires", out var req))
            {
                if (req.ValueKind != JsonValueKind.Array) errors.Add($"{ctx}: `requires` must be an array of technology ids.");
                else foreach (var item in req.EnumerateArray())
                    if (item.ValueKind != JsonValueKind.String) errors.Add($"{ctx}: `requires` entries must be strings.");
            }
            if (body.TryGetProperty("effects", out var eff)) NationalModifiers.Read(eff, ctx, errors);
        }
    }

    private static void ValidateUnits(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/units", errors))
        {
            var ctx = $"unit '{id}'";
            CheckFloat(body, ctx, "attack", 0f, 1000f, errors, strictMin: true);
            CheckFloat(body, ctx, "defense", 0f, 1000f, errors, strictMin: true);
            CheckFloat(body, ctx, "speed", 0f, 1000f, errors, strictMin: true);
            CheckFloat(body, ctx, "combat_width", 0f, 1000f, errors, strictMin: true);
            CheckFloat(body, ctx, "supply_use", 0f, 1000f, errors, strictMin: true);
            CheckFloat(body, ctx, "organisation", 0f, 100f, errors, strictMin: true);
            CheckFloat(body, ctx, "hardness", 0f, 1f, errors);
            CheckFloat(body, ctx, "amphibious", 0f, 1f, errors);
            CheckFloat(body, ctx, "recruitment_cost", 0f, 1000f, errors, strictMin: true);
            CheckInt(body, ctx, "training_days", 1, 3650, errors);
        }
    }

    private static void ValidateBuildings(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/buildings", errors))
        {
            var ctx = $"building '{id}'";
            var isNew = !BuildingCatalog.TryParse(id, out _);
            if (isNew && (!body.TryGetProperty("name", out var n) || n.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(n.GetString()) ||
                          !body.TryGetProperty("cost", out _) || !body.TryGetProperty("days", out _)))
                errors.Add($"{ctx}: a new building requires `name`, `cost`, and `days`.");
            CheckFloat(body, ctx, "cost", 0f, 100000f, errors, strictMin: true);
            CheckInt(body, ctx, "days", 1, 36500, errors);
            CheckFloat(body, ctx, "capacity_gain", 0f, 1000f, errors);
            CheckFloat(body, ctx, "jobs", 0f, 1000f, errors);
        }
    }

    private static void ValidatePolicies(JsonElement root, bool agenda, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, agenda ? "common/agendas" : "common/laws", errors))
        {
            var ctx = $"policies.{id}";
            if (!MechanicFeatures.ValidId(id)) errors.Add($"{ctx}: invalid policy id.");
            var existing = PoliticalPolicyCatalog.Find(id, agenda);
            var scope = body.TryGetProperty("scope", out var sc) && sc.ValueKind == JsonValueKind.String
                ? sc.GetString() ?? ""
                : (existing?.Scope ?? "");
            var category = body.TryGetProperty("category", out var cat) && cat.ValueKind == JsonValueKind.String
                ? cat.GetString() ?? ""
                : (existing?.Category ?? "");
            if (scope is not ("nation" or "region" or "foreign") || string.IsNullOrWhiteSpace(category))
                errors.Add($"{ctx}: `scope` must be nation/region/foreign and `category` is required.");
            CheckFloat(body, ctx, "cost_share", 0f, 0.01f, errors);
            CheckInt(body, ctx, "cooldown_days", 7, 3650, errors);
            CheckInt(body, ctx, "duration_days", 28, 3650, errors);
            CheckFloat(body, ctx, "foreign_trust", -0.3f, 0.3f, errors);
            if (body.TryGetProperty("modifiers", out var mods))
            {
                var parsed = NationalModifiers.Read(mods, ctx, errors);
                if (scope == "region" && parsed.Keys.Any(k => k is not ("radicalism_weekly" or "living_weekly" or "stability_weekly")))
                    errors.Add($"{ctx}: regional laws support radicalism_weekly, living_weekly, or stability_weekly only.");
                if (scope == "foreign" && parsed.Count > 0)
                    errors.Add($"{ctx}: foreign laws use foreign_trust, not national modifiers.");
            }
        }
    }

    private static void ValidateGrandProjects(JsonElement root, List<string> errors)
    {
        foreach (var (id, body) in EnumerateDefinitions(root, "common/grand_projects", errors))
        {
            var ctx = $"grand_projects/{id}";
            if (!MechanicFeatures.ValidId(id)) errors.Add($"{ctx}: invalid id.");
            CheckFloat(body, ctx, "base_cost_billions", 0.05f, 500f, errors);
            CheckFloat(body, ctx, "gdp_cost_share", 0f, 0.25f, errors);
            CheckFloat(body, ctx, "political_cost", 0f, 500f, errors);
            CheckFloat(body, ctx, "experience_cost", 0f, 500f, errors);
            CheckInt(body, ctx, "min_population", 0, 2_000_000_000L, errors);
            CheckFloat(body, ctx, "min_literacy", 0f, 1f, errors);
            if (body.TryGetProperty("stage_days", out var days))
            {
                if (days.ValueKind != JsonValueKind.Array || days.GetArrayLength() != 3 ||
                    days.EnumerateArray().Any(x => x.ValueKind != JsonValueKind.Number || !x.TryGetInt32(out var d) || d < 1))
                    errors.Add($"{ctx}: `stage_days` must be an array of 3 positive integers.");
            }
            if (body.TryGetProperty("experience_currency", out var ec) && ec.ValueKind == JsonValueKind.String)
            {
                var s = (ec.GetString() ?? "").Trim().ToLowerInvariant();
                if (s.Length > 0 && s is not ("army_experience" or "air_experience" or "naval_experience"))
                    errors.Add($"{ctx}: unsupported `experience_currency` '{s}'.");
            }
            if (body.TryGetProperty("key_goods", out var goods))
            {
                if (goods.ValueKind != JsonValueKind.Array) errors.Add($"{ctx}: `key_goods` must be an array of commodity ids.");
                else foreach (var g in goods.EnumerateArray())
                {
                    if (g.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(g.GetString())) continue;
                    if (!CommodityCatalog.TryParse(g.GetString()!.Trim(), out _))
                        errors.Add($"{ctx}: unknown commodity '{g.GetString()}' in `key_goods`.");
                }
            }
            if (body.TryGetProperty("host_modifiers", out var hm)) NationalModifiers.Read(hm, $"{ctx}.host_modifiers", errors);
            if (body.TryGetProperty("partner_modifiers", out var pm)) NationalModifiers.Read(pm, $"{ctx}.partner_modifiers", errors);
        }
    }

    private static void ValidateDefines(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors.Add("common/defines: expected an object."); return; }
        static string Norm(string s) => (s ?? "").Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal).ToLowerInvariant();
        var props = typeof(Defines).GetProperties(BindingFlags.Instance | BindingFlags.Public)
            .Where(p => p.CanWrite)
            .ToDictionary(p => Norm(p.Name), p => p, StringComparer.Ordinal);
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!props.TryGetValue(Norm(entry.Name), out var prop))
            {
                errors.Add($"common/defines: unknown define '{entry.Name}'.");
                continue;
            }
            var t = prop.PropertyType;
            var v = entry.Value;
            var ok = t == typeof(bool) ? v.ValueKind is JsonValueKind.True or JsonValueKind.False
                : t == typeof(string) ? v.ValueKind == JsonValueKind.String
                : t == typeof(int) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt32(out _)
                : t == typeof(long) ? v.ValueKind == JsonValueKind.Number && v.TryGetInt64(out _)
                : t == typeof(float) ? v.ValueKind == JsonValueKind.Number && v.TryGetSingle(out var fv) && float.IsFinite(fv)
                : t == typeof(double) ? v.ValueKind == JsonValueKind.Number && v.TryGetDouble(out var dv) && double.IsFinite(dv)
                : (t == typeof(Dictionary<string, bool>) || t == typeof(Dictionary<string, string>)) && v.ValueKind == JsonValueKind.Object;
            if (!ok) errors.Add($"common/defines: `{entry.Name}` expects {t.Name}.");
        }
    }
}
