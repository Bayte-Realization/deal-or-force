using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Execution stage for declarative <c>patches/*.json</c> files across enabled mods.</summary>
public enum ModPatchStage
{
    Base = 0,
    Updates = 1,
    FinalFixes = 2
}

/// <summary>Supported declarative patch operations inside <c>patches/*.json</c>.</summary>
public enum ModPatchOpKind
{
    Set = 0,
    Replace = 1,
    Add = 2,
    Multiply = 3,
    Append = 4,
    Remove = 5,
    Conditional = 6
}

/// <summary>One enabled mod root handed to <see cref="ModJsonPatcher.Configure"/>.</summary>
public sealed record ModPatchSource(string ModId, string PhysicalRoot);

/// <summary>One parsed operation inside a <see cref="ModPatchFile"/>.</summary>
public sealed class ModPatchOperation
{
    public ModPatchOpKind Op { get; init; }
    public string Target { get; init; } = "";
    public string Path { get; init; } = "";
    public JsonNode Value { get; init; }
    public bool HasValue { get; init; }
    public bool Unique { get; init; }
    public bool Flatten { get; init; }
    public bool CreateTarget { get; init; }
    public string IfExists { get; init; } = "";
    public List<string> IfModLoaded { get; init; } = new();
    public List<ModPatchOperation> ThenOps { get; init; } = new();
    public List<ModPatchOperation> ElseOps { get; init; } = new();
    public string SourceFile { get; init; } = "";
    public string ModId { get; init; } = "";
    public int Index { get; init; }
}

/// <summary>One declarative patch block targeting a data category (and optional file).</summary>
public sealed class ModPatchFile
{
    public string ModId { get; init; } = "";
    public int ModOrder { get; init; }
    public string RelativeFile { get; init; } = "";
    public int BlockIndex { get; init; }
    public ModPatchStage Stage { get; init; } = ModPatchStage.Updates;
    public string Category { get; init; } = "";
    public string FileFilter { get; init; } = "";
    public List<string> IfModLoaded { get; init; } = new();
    public List<ModPatchOperation> Operations { get; init; } = new();
}

/// <summary>
/// Surgical keyed injection (<c>"$op": "inject" | "replace" | "create"</c>) and stackable
/// declarative JSON patching (<c>patches/*.json|yaml|yml</c>) with 3-stage startup pass order
/// (<c>base</c> -&gt; <c>updates</c> -&gt; <c>final_fixes</c>) and <c>if_mod_loaded</c> gates.
///
/// When no mods are active, <see cref="IsActive"/> stays <c>false</c> so base-2024 loading
/// takes zero extra allocations and preserves an identical <see cref="CampaignFingerprint"/>.
/// </summary>
public static partial class ModJsonPatcher
{
    private static readonly object Sync = new();
    private static volatile bool _active;
    private static List<ModPatchFile> _patches = new();
    private static Dictionary<string, List<ModPatchFile>> _byCategory = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _enabledModIds = new(StringComparer.OrdinalIgnoreCase);
    private static Dictionary<string, Dictionary<string, JsonObject>> _snapshots = new(StringComparer.OrdinalIgnoreCase);
    private static List<ModDiagnostic> _diagnostics = new();

    public static bool IsActive => _active;
    public static IReadOnlyList<ModPatchFile> ActivePatches { get { lock (Sync) return _patches.ToArray(); } }
    public static IReadOnlyList<ModDiagnostic> Diagnostics { get { lock (Sync) return _diagnostics.ToArray(); } }

    /// <summary>Reset all active patches, snapshots, and diagnostics back to clean base-2024 state.</summary>
    public static void Reset()
    {
        lock (Sync)
        {
            _active = false;
            _patches = new List<ModPatchFile>();
            _byCategory = new Dictionary<string, List<ModPatchFile>>(StringComparer.OrdinalIgnoreCase);
            _enabledModIds = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _snapshots = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.OrdinalIgnoreCase);
            _diagnostics = new List<ModDiagnostic>();
        }
    }

    /// <summary>
    /// Discover and compile declarative <c>patches/*.json|yaml|yml</c> across enabled mods in load order.
    /// Keeps <see cref="IsActive"/> false when <paramref name="sources"/> is empty.
    /// </summary>
    public static List<ModDiagnostic> Configure(IEnumerable<ModPatchSource> sources)
    {
        var diagnostics = new List<ModDiagnostic>();
        var list = sources?.Where(s => s is not null && !string.IsNullOrWhiteSpace(s.ModId)).ToList()
                   ?? new List<ModPatchSource>();
        if (list.Count == 0)
        {
            Reset();
            return diagnostics;
        }

        var enabled = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var collected = new List<ModPatchFile>();
        for (var order = 0; order < list.Count; order++)
        {
            var src = list[order];
            enabled.Add(src.ModId);
            if (string.IsNullOrWhiteSpace(src.PhysicalRoot)) continue;
            var patchDir = Path.Combine(src.PhysicalRoot, "patches");
            if (!Directory.Exists(patchDir)) continue;

            foreach (var file in StructuredData.Files(patchDir, SearchOption.AllDirectories)
                         .OrderBy(f => f, StringComparer.OrdinalIgnoreCase))
            {
                var rel = Path.GetRelativePath(src.PhysicalRoot, file).Replace('\\', '/');
                collected.AddRange(ParsePatchFile(file, rel, src.ModId, order, diagnostics));
            }
        }

        var sorted = SortPatches(collected);
        var byCategory = GroupByCategory(sorted);

        lock (Sync)
        {
            _enabledModIds = enabled;
            _patches = sorted;
            _byCategory = byCategory;
            _snapshots = new Dictionary<string, Dictionary<string, JsonObject>>(StringComparer.OrdinalIgnoreCase);
            _diagnostics = new List<ModDiagnostic>(diagnostics);
            _active = true;
        }
        return diagnostics;
    }

    /// <summary>Scoped configuration for tests and isolated validation; restores prior state on dispose.</summary>
    public static IDisposable PushScope(IEnumerable<ModPatchSource> sources, out List<ModDiagnostic> diagnostics)
    {
        bool prevActive;
        List<ModPatchFile> prevPatches;
        Dictionary<string, List<ModPatchFile>> prevByCategory;
        HashSet<string> prevEnabled;
        Dictionary<string, Dictionary<string, JsonObject>> prevSnapshots;
        List<ModDiagnostic> prevDiagnostics;

        lock (Sync)
        {
            prevActive = _active;
            prevPatches = _patches;
            prevByCategory = _byCategory;
            prevEnabled = _enabledModIds;
            prevSnapshots = _snapshots;
            prevDiagnostics = _diagnostics;
        }

        diagnostics = Configure(sources);
        return new ScopeGuard(prevActive, prevPatches, prevByCategory, prevEnabled, prevSnapshots, prevDiagnostics);
    }

    /// <summary>Normalize a category path such as <c>"game/common/technologies/"</c> to <c>"common/technologies"</c>.</summary>
    public static string NormalizeCategory(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var clean = raw.Trim().Replace('\\', '/');
        while (clean.Contains("//", StringComparison.Ordinal))
            clean = clean.Replace("//", "/", StringComparison.Ordinal);
        clean = clean.Trim('/');
        if (clean.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            clean = clean[5..];
        return clean.ToLowerInvariant();
    }

    /// <summary>Parse one declarative patch file (single object, <c>{ "patches": [...] }</c>, or array).</summary>
    public static List<ModPatchFile> ParsePatchFile(
        string physicalPath,
        string relativeFile,
        string modId,
        int modOrder,
        List<ModDiagnostic> diagnostics)
    {
        var results = new List<ModPatchFile>();
        try
        {
            var json = StructuredData.ReadJson(physicalPath);
            return ParsePatchJson(json, relativeFile, modId, modOrder, diagnostics);
        }
        catch (Exception ex)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_SYNTAX", relativeFile, "$",
                $"Failed to parse patch file: {ex.Message}",
                "Check JSON/YAML syntax (commas, quotes, indentation, and matching braces)."));
            return results;
        }
    }

    /// <summary>Parse declarative patch blocks from a JSON string.</summary>
    public static List<ModPatchFile> ParsePatchJson(
        string json,
        string relativeFile,
        string modId,
        int modOrder,
        List<ModDiagnostic> diagnostics)
    {
        var results = new List<ModPatchFile>();
        JsonNode root;
        try
        {
            root = JsonNode.Parse(json, documentOptions: GameDataLoader.ForgivingJson);
        }
        catch (Exception ex)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_SYNTAX", relativeFile, "$",
                $"Invalid JSON in patch file: {ex.Message}",
                "Fix the JSON syntax error (commas, quotes, or braces)."));
            return results;
        }

        if (root is JsonArray arr)
        {
            for (var i = 0; i < arr.Count; i++)
                ParsePatchBlock(arr[i], relativeFile, modId, modOrder, i, $"$[{i}]", results, diagnostics);
        }
        else if (root is JsonObject obj)
        {
            if (obj["patches"] is JsonArray wrapped)
            {
                for (var i = 0; i < wrapped.Count; i++)
                    ParsePatchBlock(wrapped[i], relativeFile, modId, modOrder, i, $"patches[{i}]", results, diagnostics);
            }
            else
            {
                ParsePatchBlock(obj, relativeFile, modId, modOrder, 0, "$", results, diagnostics);
            }
        }
        else
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_SCHEMA", relativeFile, "$",
                "Patch file root must be a JSON object or an array of patch blocks.",
                "Use { \"stage\": \"updates\", \"category\": \"common/technologies\", \"operations\": [ ... ] }."));
        }
        return results;
    }

    /// <summary>
    /// Called by <see cref="GameDataLoader.DataDocuments"/> on base documents ONLY when <see cref="IsActive"/> is true.
    /// Records base snapshots for subsequent inline <c>$op: inject/create</c> resolution and applies
    /// matching declarative <c>patches/*.json</c> in deterministic 3-stage order.
    /// </summary>
    public static JsonDocument ApplyToBaseDocument(string category, string fileName, JsonDocument source)
    {
        if (!_active || source is null) return source;
        var normCat = NormalizeCategory(category);
        if (normCat.Length == 0 || source.RootElement.ValueKind != JsonValueKind.Object) return source;

        JsonObject rootObj;
        try
        {
            rootObj = JsonNode.Parse(source.RootElement.GetRawText(), documentOptions: GameDataLoader.ForgivingJson) as JsonObject;
        }
        catch
        {
            return source;
        }
        if (rootObj is null) return source;

        List<ModPatchFile> matching;
        HashSet<string> enabled;
        lock (Sync)
        {
            RecordSnapshotLocked(normCat, rootObj);
            if (!_byCategory.TryGetValue(normCat, out var catPatches) || catPatches.Count == 0)
                return source;
            matching = catPatches;
            enabled = _enabledModIds;
        }

        var localDiags = new List<ModDiagnostic>();
        var mutated = ApplyPatchList(rootObj, matching, enabled, fileName, localDiags);
        lock (Sync)
        {
            if (mutated) RecordSnapshotLocked(normCat, rootObj);
            RecordDiagnosticsLocked(localDiags, null);
        }

        if (!mutated) return source;
        var patched = JsonDocument.Parse(rootObj.ToJsonString(), GameDataLoader.ForgivingJson);
        source.Dispose();
        return patched;
    }

    /// <summary>
    /// Called by <c>ModLoader</c> when reading a mod's JSON/YAML document in <c>common/</c>,
    /// <c>events/</c>, or <c>history/</c>. Resolves inline <c>"$op": "inject" | "replace" | "create"</c>
    /// against accumulated category snapshots and applies any matching <c>patches/*.json</c>.
    /// </summary>
    public static string TransformModJson(
        string category,
        string fileName,
        string rawJson,
        string modId,
        List<string> packageErrors = null)
    {
        if (!_active || string.IsNullOrWhiteSpace(rawJson)) return rawJson;
        var normCat = NormalizeCategory(category);
        if (normCat.Length == 0) return rawJson;

        JsonObject modObj;
        try
        {
            modObj = JsonNode.Parse(rawJson, documentOptions: GameDataLoader.ForgivingJson) as JsonObject;
        }
        catch
        {
            return rawJson;
        }
        if (modObj is null) return rawJson;

        var localDiags = new List<ModDiagnostic>();
        var relFile = $"{normCat}/{fileName}";
        bool mutated;
        List<ModPatchFile> matching;
        HashSet<string> enabled;

        lock (Sync)
        {
            if (!_snapshots.TryGetValue(normCat, out var catSnap))
                _snapshots[normCat] = catSnap = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);

            mutated = ResolveInlineOpsOnObject(catSnap, modObj, relFile, localDiags);
            _byCategory.TryGetValue(normCat, out matching);
            enabled = _enabledModIds;
        }

        if (matching is { Count: > 0 })
        {
            if (ApplyPatchList(modObj, matching, enabled, fileName, localDiags))
                mutated = true;
        }

        lock (Sync)
        {
            RecordSnapshotLocked(normCat, modObj);
            RecordDiagnosticsLocked(localDiags, packageErrors);
        }

        return mutated ? modObj.ToJsonString() : rawJson;
    }

    /// <summary>
    /// Direct pure-C# helper: apply inline <c>$op</c> directives from <paramref name="overlayRoot"/>
    /// onto <paramref name="baseRoot"/> in memory, returning the merged output object.
    /// </summary>
    public static JsonObject ApplyInlineOpsDirect(
        JsonObject baseRoot,
        JsonObject overlayRoot,
        string sourceFile,
        List<ModDiagnostic> diagnostics)
    {
        var snap = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        if (baseRoot is not null)
        {
            foreach (var kv in baseRoot)
                if (kv.Value is JsonObject rec)
                    snap[kv.Key] = (JsonObject)rec.DeepClone();
        }
        var working = overlayRoot is not null ? (JsonObject)overlayRoot.DeepClone() : new JsonObject();
        ResolveInlineOpsOnObject(snap, working, sourceFile ?? "", diagnostics);
        return working;
    }

    /// <summary>
    /// Direct pure-C# helper: apply sorted <paramref name="patches"/> onto <paramref name="rootObj"/>
    /// in memory with the given <paramref name="enabledModIds"/>.
    /// </summary>
    public static bool ApplyDirect(
        JsonObject rootObj,
        IEnumerable<ModPatchFile> patches,
        IReadOnlySet<string> enabledModIds,
        string category,
        string fileName,
        List<ModDiagnostic> diagnostics)
    {
        if (rootObj is null || patches is null) return false;
        var normCat = NormalizeCategory(category);
        var sorted = SortPatches(patches.Where(p =>
            string.IsNullOrEmpty(normCat) ||
            string.Equals(p.Category, normCat, StringComparison.OrdinalIgnoreCase)));
        var enabled = enabledModIds is not null
            ? new HashSet<string>(enabledModIds, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        return ApplyPatchList(rootObj, sorted, enabled, fileName ?? "", diagnostics);
    }

    /// <summary>Validate inline <c>$op</c> declarations inside a parsed document without mutating anything.</summary>
    public static void ValidateInlineOps(JsonElement root, string relativeFile, ModValidationReport report)
    {
        if (root.ValueKind != JsonValueKind.Object || report is null) return;
        foreach (var prop in root.EnumerateObject())
        {
            if (prop.Value.ValueKind != JsonValueKind.Object) continue;
            if (!prop.Value.TryGetProperty("$op", out var opEl)) continue;
            var opStr = opEl.ValueKind == JsonValueKind.String ? (opEl.GetString()?.Trim() ?? "") : "";
            if (!IsValidInlineOp(opStr))
            {
                report.Add(ModDiagnosticSeverity.Error, "ERR_INLINE_OP", relativeFile, $"{prop.Name}.$op",
                    $"Unsupported inline $op '{opStr}' on record '{prop.Name}'.",
                    "Use \"$op\": \"inject\" (merge fields/arrays), \"replace\" (overwrite record), or \"create\" (add only if missing).");
            }
        }
    }

    public static bool IsValidInlineOp(string raw) =>
        raw.Equals("inject", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("replace", StringComparison.OrdinalIgnoreCase) ||
        raw.Equals("create", StringComparison.OrdinalIgnoreCase);

    private static bool ResolveInlineOpsOnObject(
        Dictionary<string, JsonObject> snapshot,
        JsonObject overlay,
        string relFile,
        List<ModDiagnostic> diagnostics)
    {
        var mutated = false;
        var toRemove = new List<string>();
        var toReplace = new List<(string Key, JsonObject Value)>();

        foreach (var kv in overlay)
        {
            if (kv.Value is not JsonObject rec) continue;
            if (!rec.TryGetPropertyValue("$op", out var opNode)) continue;

            var opStr = opNode is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";
            rec.Remove("$op");
            mutated = true;

            if (opStr.Equals("create", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.ContainsKey(kv.Key))
                {
                    toRemove.Add(kv.Key);
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Warning, "WARN_OP_CREATE_EXISTS", relFile, kv.Key,
                        $"Record '{kv.Key}' already exists; skipping \"$op\": \"create\".",
                        "Use \"$op\": \"inject\" to merge fields into the existing record or \"$op\": \"replace\" to overwrite it."));
                }
                else
                {
                    snapshot[kv.Key] = (JsonObject)rec.DeepClone();
                }
            }
            else if (opStr.Equals("inject", StringComparison.OrdinalIgnoreCase))
            {
                if (snapshot.TryGetValue(kv.Key, out var existing))
                {
                    var merged = (JsonObject)existing.DeepClone();
                    DeepMergeInject(merged, rec);
                    toReplace.Add((kv.Key, merged));
                    snapshot[kv.Key] = (JsonObject)merged.DeepClone();
                }
                else
                {
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Info, "INFO_OP_INJECT_NEW", relFile, kv.Key,
                        $"Record '{kv.Key}' was not found in base data; \"$op\": \"inject\" created it.",
                        "Verify the record ID matches an existing entry if you intended to patch one."));
                    snapshot[kv.Key] = (JsonObject)rec.DeepClone();
                }
            }
            else if (opStr.Equals("replace", StringComparison.OrdinalIgnoreCase))
            {
                if (!snapshot.ContainsKey(kv.Key))
                {
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Info, "INFO_OP_REPLACE_NEW", relFile, kv.Key,
                        $"Record '{kv.Key}' did not exist in prior layers; \"$op\": \"replace\" created it.",
                        "Verify the record ID if you intended to replace an existing base record."));
                }
                snapshot[kv.Key] = (JsonObject)rec.DeepClone();
            }
            else
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_INLINE_OP", relFile, $"{kv.Key}.$op",
                    $"Unknown inline $op '{opStr}' on record '{kv.Key}'.",
                    "Use one of: \"inject\", \"replace\", or \"create\"."));
            }
        }

        foreach (var key in toRemove) overlay.Remove(key);
        foreach (var (key, val) in toReplace) overlay[key] = val;
        return mutated;
    }

    private static void DeepMergeInject(JsonObject target, JsonObject patch)
    {
        foreach (var kv in patch)
        {
            if (kv.Value is null)
            {
                target[kv.Key] = null;
                continue;
            }
            if (kv.Value is JsonObject patchChild && target[kv.Key] is JsonObject existingChild)
            {
                DeepMergeInject(existingChild, patchChild);
            }
            else if (kv.Value is JsonArray patchArr && target[kv.Key] is JsonArray existingArr)
            {
                foreach (var item in patchArr)
                {
                    if (item is null) continue;
                    var serialized = item.ToJsonString();
                    var dup = existingArr.Any(e => e is not null && e.ToJsonString() == serialized);
                    if (!dup) existingArr.Add(item.DeepClone());
                }
            }
            else
            {
                target[kv.Key] = kv.Value.DeepClone();
            }
        }
    }

    private static List<ModPatchFile> SortPatches(IEnumerable<ModPatchFile> items) =>
        items.OrderBy(p => (int)p.Stage)
             .ThenBy(p => p.ModOrder)
             .ThenBy(p => p.RelativeFile, StringComparer.OrdinalIgnoreCase)
             .ThenBy(p => p.BlockIndex)
             .ToList();

    private static Dictionary<string, List<ModPatchFile>> GroupByCategory(List<ModPatchFile> sorted)
    {
        var map = new Dictionary<string, List<ModPatchFile>>(StringComparer.OrdinalIgnoreCase);
        foreach (var p in sorted)
        {
            if (!map.TryGetValue(p.Category, out var bucket))
                map[p.Category] = bucket = new List<ModPatchFile>();
            bucket.Add(p);
        }
        return map;
    }

    private static void RecordSnapshotLocked(string category, JsonObject rootObj)
    {
        if (!_snapshots.TryGetValue(category, out var catSnap))
            _snapshots[category] = catSnap = new Dictionary<string, JsonObject>(StringComparer.OrdinalIgnoreCase);
        foreach (var kv in rootObj)
            if (kv.Value is JsonObject rec)
                catSnap[kv.Key] = (JsonObject)rec.DeepClone();
    }

    private static void RecordDiagnosticsLocked(List<ModDiagnostic> local, List<string> packageErrors)
    {
        if (local is null || local.Count == 0) return;
        foreach (var d in local)
        {
            if (_diagnostics.Count < 512) _diagnostics.Add(d);
            if (d.Severity != ModDiagnosticSeverity.Info)
            {
                var text = $"[{d.Code}] {d.File} ({d.Path}): {d.Message}";
                GameDataLoader.RecordWarning(text);
                if (d.Severity == ModDiagnosticSeverity.Error) packageErrors?.Add(text);
            }
        }
    }

    private sealed class ScopeGuard : IDisposable
    {
        private readonly bool _prevActive;
        private readonly List<ModPatchFile> _prevPatches;
        private readonly Dictionary<string, List<ModPatchFile>> _prevByCategory;
        private readonly HashSet<string> _prevEnabled;
        private readonly Dictionary<string, Dictionary<string, JsonObject>> _prevSnapshots;
        private readonly List<ModDiagnostic> _prevDiagnostics;
        private bool _disposed;

        public ScopeGuard(
            bool prevActive,
            List<ModPatchFile> prevPatches,
            Dictionary<string, List<ModPatchFile>> prevByCategory,
            HashSet<string> prevEnabled,
            Dictionary<string, Dictionary<string, JsonObject>> prevSnapshots,
            List<ModDiagnostic> prevDiagnostics)
        {
            _prevActive = prevActive;
            _prevPatches = prevPatches;
            _prevByCategory = prevByCategory;
            _prevEnabled = prevEnabled;
            _prevSnapshots = prevSnapshots;
            _prevDiagnostics = prevDiagnostics;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Sync)
            {
                _active = _prevActive;
                _patches = _prevPatches;
                _byCategory = _prevByCategory;
                _enabledModIds = _prevEnabled;
                _snapshots = _prevSnapshots;
                _diagnostics = _prevDiagnostics;
            }
        }
    }
}
