using System;
using System.IO;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Copy-on-write data authoring. Never writes into the source game or runs mod code.</summary>
public static class ContentWorkbench
{
    public static readonly string[] Roots = { "common", "gui", "interface", "localization", "fonts", "sound", "music", "scenarios", "history", "map_data", "gfx" };
    public static string Validate(string relative, string json)
    {
        using var doc = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        if (doc.RootElement.ValueKind is not (JsonValueKind.Object or JsonValueKind.Array))
            throw new InvalidDataException("A content document must be an object or array.");
        if (relative.Replace('\\', '/').EndsWith("/world.json", StringComparison.OrdinalIgnoreCase))
            SaveSystem.ImportScenarioJson(json, "validation");
        if (relative.Replace('\\', '/').EndsWith("/scenario.json", StringComparison.OrdinalIgnoreCase))
        {
            var r = doc.RootElement;
            if (!r.TryGetProperty("id", out var id) || string.IsNullOrWhiteSpace(id.GetString()) ||
                !r.TryGetProperty("name", out var name) || string.IsNullOrWhiteSpace(name.GetString()))
                throw new InvalidDataException("Scenario requires id and name.");
        }
        return "JSON valid. Domain rules are also checked when the game loads this content.";
    }

    public static string Write(string modRoot, string relative, string json)
    {
        var parts = relative.Replace('\\', '/').Split('/');
        if (parts.Length < 2 || Array.IndexOf(Roots, parts[0]) < 0 || !relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
            throw new InvalidDataException("Only JSON content in declared game folders can be edited.");
        Validate(relative, json);
        var target = ScenarioLibrary.SafePath(modRoot, relative);
        RejectLinks(target);
        Directory.CreateDirectory(Path.GetDirectoryName(target));
        var temporary = target + "." + Guid.NewGuid().ToString("N") + ".tmp";
        try
        {
            File.WriteAllText(temporary, json);
            if (File.Exists(target)) File.Replace(temporary, target, target + ".bak");
            else File.Move(temporary, target);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
        return target;
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

    public static string SetValue(string json, string pointer, string value)
    {
        var root = JsonNode.Parse(json, documentOptions: GameDataLoader.ForgivingJson);
        var parts = pointer.Split('/');
        JsonNode node = root;
        for (var i = 1; i < parts.Length - 1; i++) node = Child(node, Decode(parts[i]));
        var key = Decode(parts[^1]);
        var replacement = JsonNode.Parse(value);
        if (node is JsonArray a) a[int.Parse(key)] = replacement;
        else if (node is JsonObject o) o[key] = replacement;
        else throw new InvalidDataException("Select a property, not the document root.");
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }
    private static JsonNode Child(JsonNode node, string key) => node is JsonArray a ? a[int.Parse(key)] : node[key];
    private static string Decode(string key) => key.Replace("~1", "/").Replace("~0", "~");
}
