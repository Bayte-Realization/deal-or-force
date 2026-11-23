using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

public static partial class ModJsonPatcher
{
    private static bool ApplyPatchList(
        JsonObject rootObj,
        List<ModPatchFile> patches,
        HashSet<string> enabledModIds,
        string fileName,
        List<ModDiagnostic> diagnostics)
    {
        var mutated = false;
        for (var i = 0; i < patches.Count; i++)
        {
            var patch = patches[i];
            if (!MatchesFileFilter(patch.FileFilter, fileName)) continue;
            if (!AreModsLoaded(patch.IfModLoaded, enabledModIds)) continue;

            for (var opIdx = 0; opIdx < patch.Operations.Count; opIdx++)
            {
                if (ExecuteOperation(rootObj, patch.Operations[opIdx], enabledModIds, diagnostics, depth: 0))
                    mutated = true;
            }
        }
        return mutated;
    }

    private static bool ExecuteOperation(
        JsonObject rootObj,
        ModPatchOperation op,
        HashSet<string> enabledModIds,
        List<ModDiagnostic> diagnostics,
        int depth,
        string inheritedTarget = "")
    {
        if (depth > 8)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_DEPTH", op.SourceFile, $"operations[{op.Index}]",
                "Conditional patch nesting exceeded maximum depth (8).",
                "Flatten nested conditional patch blocks."));
            return false;
        }
        if (!AreModsLoaded(op.IfModLoaded, enabledModIds)) return false;

        var effectiveTarget = !string.IsNullOrEmpty(op.Target) ? op.Target : inheritedTarget;
        var diagPath = string.IsNullOrEmpty(effectiveTarget)
            ? (string.IsNullOrEmpty(op.Path) ? "$" : op.Path)
            : (string.IsNullOrEmpty(op.Path) ? effectiveTarget : $"{effectiveTarget}.{op.Path}");

        if (op.Op == ModPatchOpKind.Conditional)
        {
            var probeRoot = ResolveTargetObject(rootObj, effectiveTarget);
            var exists = probeRoot is not null && TryResolveNode(probeRoot, op.IfExists, out var found) && found is not null;
            var branch = exists ? op.ThenOps : op.ElseOps;
            var changed = false;
            for (var i = 0; i < branch.Count; i++)
                if (ExecuteOperation(rootObj, branch[i], enabledModIds, diagnostics, depth + 1, effectiveTarget))
                    changed = true;
            return changed;
        }

        // Remove whole target record when path is empty.
        if (op.Op == ModPatchOpKind.Remove && string.IsNullOrEmpty(op.Path) && !string.IsNullOrEmpty(effectiveTarget))
        {
            if (rootObj.ContainsKey(effectiveTarget))
            {
                rootObj.Remove(effectiveTarget);
                return true;
            }
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, effectiveTarget,
                $"Patch 'remove' could not find record '{effectiveTarget}'.",
                "Check the record ID or wrap the operation in a 'conditional' with 'if_exists'."));
            return false;
        }

        // Whole-record set/replace when path is empty.
        if (string.IsNullOrEmpty(op.Path) && !string.IsNullOrEmpty(effectiveTarget))
        {
            if (op.Op == ModPatchOpKind.Replace && !rootObj.ContainsKey(effectiveTarget))
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, effectiveTarget,
                    $"Patch 'replace' could not find record '{effectiveTarget}'.",
                    "Use 'set' to create missing records, or check the record ID spelling."));
                return false;
            }
            if (op.Op is ModPatchOpKind.Set or ModPatchOpKind.Replace)
            {
                rootObj[effectiveTarget] = op.Value?.DeepClone();
                return true;
            }
        }

        // Resolve target container (either rootObj[effectiveTarget] or rootObj itself).
        JsonObject scopeObj;
        if (string.IsNullOrEmpty(effectiveTarget))
        {
            scopeObj = rootObj;
        }
        else if (rootObj[effectiveTarget] is JsonObject foundObj)
        {
            scopeObj = foundObj;
        }
        else if (op.Op == ModPatchOpKind.Set && op.CreateTarget)
        {
            scopeObj = new JsonObject();
            rootObj[effectiveTarget] = scopeObj;
        }
        else
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, diagPath,
                $"Patch '{op.Op.ToString().ToLowerInvariant()}' target record '{effectiveTarget}' was not found.",
                "Verify 'target' matches an existing ID in this category, or set \"create_target\": true on 'set'."));
            return false;
        }

        var segments = ParsePathSegments(op.Path);
        if (segments.Count == 0)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_PATH", op.SourceFile, diagPath,
                $"Patch '{op.Op.ToString().ToLowerInvariant()}' requires a non-empty 'path'.",
                "Specify a dot-separated field path such as \"cost\" or \"effects.output\"."));
            return false;
        }

        var createIntermediates = op.Op is ModPatchOpKind.Set or ModPatchOpKind.Append or ModPatchOpKind.Add;
        if (!TryResolveParent(scopeObj, segments, createIntermediates, out var parentNode, out var leaf))
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, diagPath,
                $"Path '{op.Path}' could not be resolved on '{(string.IsNullOrEmpty(effectiveTarget) ? "$" : effectiveTarget)}'.",
                "Check intermediate field names or wrap in a 'conditional' with 'if_exists'."));
            return false;
        }

        return ApplyLeafOperation(parentNode, leaf, op, diagPath, diagnostics);
    }

    private static bool ApplyLeafOperation(
        JsonNode parent,
        PathSegment leaf,
        ModPatchOperation op,
        string diagPath,
        List<ModDiagnostic> diagnostics)
    {
        if (parent is JsonArray parentArr)
        {
            if (leaf.ArrayIndex < 0 || leaf.ArrayIndex >= parentArr.Count)
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, diagPath,
                    $"Array index [{leaf.ArrayIndex}] out of bounds (length {parentArr.Count}).",
                    "Check array length or use 'append' instead of indexing."));
                return false;
            }
            if (op.Op == ModPatchOpKind.Remove)
            {
                parentArr.RemoveAt(leaf.ArrayIndex);
                return true;
            }
            if (op.Op is ModPatchOpKind.Set or ModPatchOpKind.Replace)
            {
                parentArr[leaf.ArrayIndex] = op.Value?.DeepClone();
                return true;
            }
            return ApplyNumericOrArrayOp(parentArr[leaf.ArrayIndex], v => parentArr[leaf.ArrayIndex] = v, op, diagPath, diagnostics, existed: true);
        }

        if (parent is not JsonObject parentObj) return false;
        var key = leaf.Property;
        var existed = parentObj.ContainsKey(key);
        var current = existed ? parentObj[key] : null;

        switch (op.Op)
        {
            case ModPatchOpKind.Set:
                parentObj[key] = op.Value?.DeepClone();
                return true;

            case ModPatchOpKind.Replace:
                if (!existed)
                {
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, diagPath,
                        $"Patch 'replace' could not find existing field '{key}'.",
                        "Use 'set' to insert new fields, or check the property spelling."));
                    return false;
                }
                parentObj[key] = op.Value?.DeepClone();
                return true;

            case ModPatchOpKind.Remove:
                if (!existed)
                {
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, diagPath,
                        $"Patch 'remove' could not find field '{key}'.",
                        "Check field spelling or guard with 'conditional' + 'if_exists'."));
                    return false;
                }
                if (op.HasValue && current is JsonArray arr)
                {
                    var needle = op.Value?.ToJsonString();
                    var removedAny = false;
                    for (var i = arr.Count - 1; i >= 0; i--)
                    {
                        if (arr[i]?.ToJsonString() == needle)
                        {
                            arr.RemoveAt(i);
                            removedAny = true;
                        }
                    }
                    return removedAny;
                }
                parentObj.Remove(key);
                return true;

            default:
                return ApplyNumericOrArrayOp(current, v => parentObj[key] = v, op, diagPath, diagnostics, existed);
        }
    }

    private static bool ApplyNumericOrArrayOp(
        JsonNode current,
        Action<JsonNode> write,
        ModPatchOperation op,
        string diagPath,
        List<ModDiagnostic> diagnostics,
        bool existed)
    {
        if (op.Op is ModPatchOpKind.Add or ModPatchOpKind.Multiply)
        {
            if (!TryGetDouble(op.Value, out var delta))
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_VALUE", op.SourceFile, diagPath,
                    $"Patch '{op.Op.ToString().ToLowerInvariant()}' requires a finite numeric 'value'.",
                    "Provide a JSON number such as \"value\": 0.1 or \"value\": 1.25."));
                return false;
            }
            if (op.Op == ModPatchOpKind.Multiply && !existed)
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Warning, "WARN_PATCH_TARGET_MISSING", op.SourceFile, diagPath,
                    $"Patch 'multiply' could not find numeric field at '{diagPath}'.",
                    "Check the field name or initialize it first with 'set'."));
                return false;
            }
            double baseVal = 0.0;
            if (existed && current is not null && !TryGetDouble(current, out baseVal))
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_TYPE", op.SourceFile, diagPath,
                    $"Cannot apply '{op.Op.ToString().ToLowerInvariant()}' to non-numeric field '{diagPath}'.",
                    "Target a numeric property or use 'set'/'replace'."));
                return false;
            }
            var result = op.Op == ModPatchOpKind.Add ? baseVal + delta : baseVal * delta;
            if (!double.IsFinite(result)) return false;
            if (Math.Abs(result - Math.Truncate(result)) < 1e-9 && result is >= int.MinValue and <= int.MaxValue &&
                IsJsonInteger(current) && op.Op == ModPatchOpKind.Add &&
                Math.Abs(delta - Math.Truncate(delta)) < 1e-9)
                write(JsonValue.Create((int)Math.Truncate(result)));
            else
                write(JsonValue.Create(Math.Round(result, 6)));
            return true;
        }

        if (op.Op == ModPatchOpKind.Append)
        {
            JsonArray arr;
            if (!existed || current is null)
            {
                arr = new JsonArray();
                write(arr);
            }
            else if (current is JsonArray existingArr)
            {
                arr = existingArr;
            }
            else
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_TYPE", op.SourceFile, diagPath,
                    $"Cannot 'append' to non-array field '{diagPath}'.",
                    "Target an array property (e.g. 'requires', 'tags', 'options') or use 'set'."));
                return false;
            }

            if (op.Flatten && op.Value is JsonArray srcArr)
                foreach (var item in srcArr) AppendOne(arr, item, op.Unique);
            else
                AppendOne(arr, op.Value, op.Unique);
            return true;
        }

        return false;
    }

    private static void AppendOne(JsonArray arr, JsonNode item, bool unique)
    {
        if (item is null) return;
        if (unique)
        {
            var sig = item.ToJsonString();
            if (arr.Any(e => e is not null && e.ToJsonString() == sig)) return;
        }
        arr.Add(item.DeepClone());
    }

    private static void ParsePatchBlock(
        JsonNode node,
        string relativeFile,
        string modId,
        int modOrder,
        int blockIndex,
        string jsonPath,
        List<ModPatchFile> results,
        List<ModDiagnostic> diagnostics)
    {
        if (node is not JsonObject obj)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_SCHEMA", relativeFile, jsonPath,
                "Patch block must be a JSON object.",
                "Define { \"stage\": \"updates\", \"category\": \"...\", \"operations\": [ ... ] }."));
            return;
        }

        var category = NormalizeCategory(ReadString(obj, "category"));
        if (category.Length == 0)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_SCHEMA", relativeFile, $"{jsonPath}.category",
                "Patch block is missing required 'category' (e.g. 'common/technologies', 'events').",
                "Add \"category\": \"common/technologies\" (or another folder under game/)."));
            return;
        }

        var stage = ModPatchStage.Updates;
        var rawStage = ReadString(obj, "stage");
        if (rawStage.Length > 0 && !TryParseStage(rawStage, out stage))
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_STAGE", relativeFile, $"{jsonPath}.stage",
                $"Unknown patch stage '{rawStage}'.",
                "Use \"base\", \"updates\", or \"final_fixes\"."));
            return;
        }

        var fileFilter = ReadString(obj, "file");
        var ifModLoaded = ReadModGate(obj);
        var opsNode = obj["operations"] ?? obj["ops"];
        if (opsNode is not JsonArray opsArr || opsArr.Count == 0)
        {
            diagnostics?.Add(new ModDiagnostic(
                ModDiagnosticSeverity.Error, "ERR_PATCH_SCHEMA", relativeFile, $"{jsonPath}.operations",
                "Patch block must include a non-empty 'operations' array.",
                "Add \"operations\": [ { \"op\": \"set\", \"target\": \"...\", \"path\": \"...\", \"value\": ... } ]."));
            return;
        }

        var operations = ParseOperations(opsArr, relativeFile, modId, $"{jsonPath}.operations", diagnostics, depth: 0);
        results.Add(new ModPatchFile
        {
            ModId = modId ?? "",
            ModOrder = modOrder,
            RelativeFile = relativeFile ?? "",
            BlockIndex = blockIndex,
            Stage = stage,
            Category = category,
            FileFilter = fileFilter,
            IfModLoaded = ifModLoaded,
            Operations = operations
        });
    }

    private static List<ModPatchOperation> ParseOperations(
        JsonArray arr,
        string relativeFile,
        string modId,
        string basePath,
        List<ModDiagnostic> diagnostics,
        int depth)
    {
        var ops = new List<ModPatchOperation>();
        if (depth > 8) return ops;

        for (var i = 0; i < arr.Count; i++)
        {
            var itemPath = $"{basePath}[{i}]";
            if (arr[i] is not JsonObject opObj)
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_SCHEMA", relativeFile, itemPath,
                    "Each patch operation must be a JSON object.",
                    "Write { \"op\": \"set\", \"target\": \"id\", \"path\": \"field\", \"value\": 1 }."));
                continue;
            }

            var rawOp = ReadString(opObj, "op");
            if (!TryParseOp(rawOp, out var kind))
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_OP", relativeFile, $"{itemPath}.op",
                    $"Unknown patch operation '{rawOp}'.",
                    "Supported ops: set, replace, add, multiply, append, remove, conditional."));
                continue;
            }

            var target = ReadString(opObj, "target");
            if (target.Length == 0) target = ReadString(opObj, "record");
            if (target.Length == 0) target = ReadString(opObj, "id");
            var path = ReadString(opObj, "path");
            var hasValue = opObj.TryGetPropertyValue("value", out var valNode);
            var unique = ReadBool(opObj, "unique");
            var flatten = ReadBool(opObj, "flatten");
            var createTarget = ReadBool(opObj, "create_target");
            var ifModLoaded = ReadModGate(opObj);

            if (kind == ModPatchOpKind.Conditional)
            {
                var ifExists = ReadString(opObj, "if_exists");
                if (ifExists.Length == 0)
                {
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Error, "ERR_PATCH_CONDITIONAL", relativeFile, $"{itemPath}.if_exists",
                        "Conditional patch operation requires a non-empty 'if_exists' path.",
                        "Provide \"if_exists\": \"effects.output\" (or a record/field path)."));
                    continue;
                }
                var thenOps = opObj["then"] is JsonArray thenArr
                    ? ParseOperations(thenArr, relativeFile, modId, $"{itemPath}.then", diagnostics, depth + 1)
                    : new List<ModPatchOperation>();
                var elseOps = opObj["else"] is JsonArray elseArr
                    ? ParseOperations(elseArr, relativeFile, modId, $"{itemPath}.else", diagnostics, depth + 1)
                    : new List<ModPatchOperation>();
                if (thenOps.Count == 0 && elseOps.Count == 0)
                {
                    diagnostics?.Add(new ModDiagnostic(
                        ModDiagnosticSeverity.Warning, "WARN_PATCH_CONDITIONAL_EMPTY", relativeFile, itemPath,
                        "Conditional patch has neither 'then' nor 'else' operations.",
                        "Add at least one operation in 'then' or 'else'."));
                }
                ops.Add(new ModPatchOperation
                {
                    Op = kind, Target = target, Path = path, IfExists = ifExists,
                    IfModLoaded = ifModLoaded, ThenOps = thenOps, ElseOps = elseOps,
                    SourceFile = relativeFile, ModId = modId, Index = i
                });
                continue;
            }

            if (kind is ModPatchOpKind.Set or ModPatchOpKind.Replace or ModPatchOpKind.Append && !hasValue)
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_VALUE", relativeFile, $"{itemPath}.value",
                    $"Patch operation '{rawOp}' requires a 'value' field.",
                    "Provide a JSON \"value\" to assign or append."));
                continue;
            }
            if (kind is ModPatchOpKind.Add or ModPatchOpKind.Multiply && (!hasValue || !TryGetDouble(valNode, out _)))
            {
                diagnostics?.Add(new ModDiagnostic(
                    ModDiagnosticSeverity.Error, "ERR_PATCH_VALUE", relativeFile, $"{itemPath}.value",
                    $"Patch operation '{rawOp}' requires a numeric 'value'.",
                    "Set \"value\" to a finite number (e.g. 0.05 or 1.25)."));
                continue;
            }

            ops.Add(new ModPatchOperation
            {
                Op = kind, Target = target, Path = path,
                Value = hasValue ? valNode?.DeepClone() : null,
                HasValue = hasValue, Unique = unique, Flatten = flatten,
                CreateTarget = createTarget, IfModLoaded = ifModLoaded,
                SourceFile = relativeFile, ModId = modId, Index = i
            });
        }
        return ops;
    }

    private static bool TryParseStage(string raw, out ModPatchStage stage)
    {
        switch (raw.Trim().ToLowerInvariant().Replace('-', '_'))
        {
            case "base": stage = ModPatchStage.Base; return true;
            case "updates": case "update": stage = ModPatchStage.Updates; return true;
            case "final_fixes": case "final": stage = ModPatchStage.FinalFixes; return true;
            default: stage = ModPatchStage.Updates; return false;
        }
    }

    private static bool TryParseOp(string raw, out ModPatchOpKind kind)
    {
        switch (raw.Trim().ToLowerInvariant().Replace('-', '_'))
        {
            case "set": kind = ModPatchOpKind.Set; return true;
            case "replace": kind = ModPatchOpKind.Replace; return true;
            case "add": kind = ModPatchOpKind.Add; return true;
            case "multiply": case "mult": kind = ModPatchOpKind.Multiply; return true;
            case "append": kind = ModPatchOpKind.Append; return true;
            case "remove": case "delete": kind = ModPatchOpKind.Remove; return true;
            case "conditional": case "if": kind = ModPatchOpKind.Conditional; return true;
            default: kind = ModPatchOpKind.Set; return false;
        }
    }

    private static bool MatchesFileFilter(string filter, string fileName)
    {
        if (string.IsNullOrWhiteSpace(filter)) return true;
        if (string.IsNullOrWhiteSpace(fileName)) return false;
        if (filter.Equals(fileName, StringComparison.OrdinalIgnoreCase)) return true;
        return Path.GetFileNameWithoutExtension(filter)
            .Equals(Path.GetFileNameWithoutExtension(fileName), StringComparison.OrdinalIgnoreCase);
    }

    private static bool AreModsLoaded(List<string> required, HashSet<string> enabled)
    {
        if (required is null || required.Count == 0) return true;
        for (var i = 0; i < required.Count; i++)
            if (!enabled.Contains(required[i])) return false;
        return true;
    }

    private static JsonObject ResolveTargetObject(JsonObject rootObj, string target) =>
        string.IsNullOrEmpty(target) ? rootObj : rootObj[target] as JsonObject;

    private readonly record struct PathSegment(string Property, int ArrayIndex);

    private static List<PathSegment> ParsePathSegments(string path)
    {
        var list = new List<PathSegment>();
        if (string.IsNullOrWhiteSpace(path)) return list;
        foreach (var rawPart in path.Split('.', StringSplitOptions.RemoveEmptyEntries))
        {
            var part = rawPart.Trim();
            var bracket = part.IndexOf('[');
            if (bracket < 0) { list.Add(new PathSegment(part, -1)); continue; }
            var prop = part[..bracket].Trim();
            if (prop.Length > 0) list.Add(new PathSegment(prop, -1));
            var rest = part[bracket..];
            while (rest.StartsWith("[", StringComparison.Ordinal))
            {
                var close = rest.IndexOf(']');
                if (close <= 1) break;
                if (int.TryParse(rest[1..close], NumberStyles.Integer, CultureInfo.InvariantCulture, out var idx))
                    list.Add(new PathSegment("", idx));
                rest = rest[(close + 1)..];
            }
        }
        return list;
    }

    private static bool TryResolveNode(JsonObject root, string path, out JsonNode found)
    {
        found = root;
        var segments = ParsePathSegments(path);
        for (var i = 0; i < segments.Count; i++)
        {
            var seg = segments[i];
            if (seg.ArrayIndex >= 0)
            {
                if (found is not JsonArray arr || seg.ArrayIndex >= arr.Count) { found = null; return false; }
                found = arr[seg.ArrayIndex];
            }
            else if (found is not JsonObject obj || !obj.TryGetPropertyValue(seg.Property, out found))
            {
                found = null; return false;
            }
        }
        return true;
    }

    private static bool TryResolveParent(
        JsonObject root, List<PathSegment> segments, bool createMissingObjects,
        out JsonNode parent, out PathSegment leaf)
    {
        parent = root;
        leaf = segments[^1];
        for (var i = 0; i < segments.Count - 1; i++)
        {
            var seg = segments[i];
            if (seg.ArrayIndex >= 0)
            {
                if (parent is not JsonArray arr || seg.ArrayIndex >= arr.Count) return false;
                parent = arr[seg.ArrayIndex];
            }
            else
            {
                if (parent is not JsonObject obj) return false;
                if (!obj.TryGetPropertyValue(seg.Property, out var next) || next is null)
                {
                    if (!createMissingObjects) return false;
                    next = new JsonObject();
                    obj[seg.Property] = next;
                }
                parent = next;
            }
        }
        return parent is not null;
    }

    private static bool TryGetDouble(JsonNode node, out double value)
    {
        value = 0;
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<JsonElement>(out var je))
            return je.ValueKind == JsonValueKind.Number && je.TryGetDouble(out value) && double.IsFinite(value);
        if (jv.TryGetValue<double>(out var d) && double.IsFinite(d)) { value = d; return true; }
        if (jv.TryGetValue<int>(out var i)) { value = i; return true; }
        if (jv.TryGetValue<long>(out var l)) { value = l; return true; }
        if (jv.TryGetValue<decimal>(out var m)) { value = (double)m; return true; }
        return false;
    }

    private static bool IsJsonInteger(JsonNode node)
    {
        if (node is not JsonValue jv) return false;
        if (jv.TryGetValue<JsonElement>(out var je))
            return je.ValueKind == JsonValueKind.Number && je.TryGetInt32(out _);
        return jv.TryGetValue<int>(out _) || jv.TryGetValue<long>(out _);
    }

    private static string ReadString(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<string>(out var s) ? s.Trim() : "";

    private static bool ReadBool(JsonObject obj, string key) =>
        obj.TryGetPropertyValue(key, out var node) && node is JsonValue v && v.TryGetValue<bool>(out var b) && b;

    private static List<string> ReadModGate(JsonObject obj)
    {
        var list = new List<string>();
        if (!obj.TryGetPropertyValue("if_mod_loaded", out var node) || node is null) return list;
        if (node is JsonValue v && v.TryGetValue<string>(out var single) && !string.IsNullOrWhiteSpace(single))
            list.Add(single.Trim());
        else if (node is JsonArray arr)
            foreach (var item in arr)
                if (item is JsonValue iv && iv.TryGetValue<string>(out var s) && !string.IsNullOrWhiteSpace(s))
                    list.Add(s.Trim());
        else if (node is JsonObject gateObj && gateObj["mods"] is JsonArray modArr)
            foreach (var modItem in modArr)
                if (modItem is JsonValue mv && mv.TryGetValue<string>(out var ms) && !string.IsNullOrWhiteSpace(ms))
                    list.Add(ms.Trim());
        return list;
    }
}
