using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Copy-on-write data authoring. Never writes into the source game or runs mod code.</summary>
public static class ContentWorkbench
{
    public static readonly string[] Roots = { "common", "events", "gui", "interface", "localization", "fonts", "sound", "music", "scenarios", "history", "map_data", "gfx" };

    public static bool IsEditable(string relative)
    {
        var parts = (relative ?? "").Replace('\\', '/').Split('/');
        return parts.Length >= 2 && Array.IndexOf(Roots, parts[0]) >= 0 &&
            !parts.Any(part => part.Length == 0 || part is "." or "..") && StructuredData.IsDocument(relative) &&
            (parts[0] != "scenarios" || !StructuredData.IsYaml(relative));
    }

    public static string Validate(string relative, string text)
    {
        // Проверяем черновик, а не устанавливаем мод. Валидатор не должен вызывать
        // Load/Merge живого каталога: простая кнопка проверки не меняет текущую партию.
        // Общая синтаксическая проверка не заменяет доменный валидатор нового каталога.
        if (!IsEditable(relative))
            throw new InvalidDataException("Use JSON or YAML in a declared content folder. Scenario documents remain JSON.");
        using var doc = StructuredData.ParseDocument(relative, text);
        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new InvalidDataException("A content document must be an object or array.");
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

    private static void RejectLinks(string target)
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
}
