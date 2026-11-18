using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Shared overlay loading helpers for JSON/YAML catalog folders under `common/` and tree
/// asset folders (`fonts/`, `music/`, `sound/`), enforcing the single rule that later files
/// override earlier ones and mods only write what they change.
/// </summary>
public static class DataCatalogLoader
{
    /// <summary>Read every document in `common/<folder>` in file-name order and pass its root to <paramref name="merge"/>.</summary>
    public static void LoadCommon(string folder, Action<JsonElement> merge)
    {
        if (string.IsNullOrWhiteSpace(folder) || merge is null) return;
        foreach (var document in GameDataLoader.DataDocuments("common", folder))
            using (document)
                merge(document.RootElement);
    }

    /// <summary>Parse one JSON/YAML overlay string with forgiving comments/commas and pass its root to <paramref name="merge"/>.</summary>
    public static void ParseAndMerge(string json, Action<JsonElement> merge)
    {
        if (merge is null) return;
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        merge(document.RootElement);
    }

    /// <summary>
    /// Layer one package's tree folder (`fonts/`, `music/`, `sound/`) in normalized path order,
    /// collecting per-file errors without aborting the rest of the package.
    /// </summary>
    public static void LoadTree(
        string category,
        string directory,
        List<string> errors,
        Action<string, string> mergeFile,
        Action onCompleted = null)
    {
        if (string.IsNullOrWhiteSpace(directory) || !Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = StructuredData.Files(directory, SearchOption.AllDirectories)
                .OrderBy(path => path.Replace('\\', '/'), StringComparer.OrdinalIgnoreCase)
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
            try { mergeFile(StructuredData.ReadJson(file), origin); }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }

        onCompleted?.Invoke();
    }

    /// <summary>Load the base game's own tree folder when present.</summary>
    public static void LoadBaseTree(string category, Action<string, string> loadTree)
    {
        var root = GameDataLoader.DataFolder(category);
        if (root is not null) loadTree(root, $"res://game/{category}");
    }

    /// <summary>Merge keyed object entries (`id -> { ... }`), replacing earlier definitions with the same id.</summary>
    public static void MergeNamedDefinitions<T>(
        JsonElement root,
        List<T> target,
        Func<string, JsonElement, T> read,
        Func<T, string> getId) where T : class
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = read(entry.Name, entry.Value);
            if (definition is null) continue;
            var id = getId(definition);
            target.RemoveAll(existing => string.Equals(getId(existing), id, StringComparison.OrdinalIgnoreCase));
            target.Add(definition);
        }
    }

    /// <summary>Validate keyed object entries without mutating the active catalog.</summary>
    public static void ValidateNamedDefinitions(JsonElement root, Action<string, JsonElement> read)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Object)
                read(entry.Name, entry.Value);
    }

    /// <summary>Sort definitions deterministically by <paramref name="getOrder"/> then ordinal id, and rebuild <paramref name="byId"/>.</summary>
    public static void RebuildSortedById<T>(
        List<T> definitions,
        Dictionary<string, T> byId,
        Func<T, int> getOrder,
        Func<T, string> getId)
    {
        definitions.Sort((left, right) =>
        {
            var leftOrder = getOrder(left);
            var rightOrder = getOrder(right);
            return leftOrder != rightOrder
                ? leftOrder.CompareTo(rightOrder)
                : string.CompareOrdinal(getId(left), getId(right));
        });
        byId.Clear();
        foreach (var definition in definitions) byId[getId(definition)] = definition;
    }
}
