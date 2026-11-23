using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>One mod's declared virtual filesystem replacement/exclusion policy.</summary>
public sealed record ModLayerSpec(
    string ModId,
    bool TotalConversion,
    IReadOnlyList<string> ReplacePaths,
    IReadOnlyList<string> ExcludePaths);

/// <summary>Provenance record answering which mod replaced or excluded a virtual content path.</summary>
public sealed record ModProvenanceEntry(
    string VirtualPath,
    string OwnerModId,
    string Mode,
    IReadOnlyList<string> SupersededModIds);

/// <summary>
/// Scoped virtual-filesystem layering for total-conversion mods.
///
/// When no mod declares <c>replace_paths</c>, <c>exclude_paths</c>, or <c>total_conversion</c>,
/// <see cref="IsActive"/> stays <c>false</c> and base-game loading runs untouched with zero
/// allocations and zero overhead.
///
/// When active, <see cref="GameDataLoader"/> suppresses replaced base-game roots and excluded
/// base files cleanly so a historical, sci-fi, or fantasy total conversion never leaks 2024
/// technologies, events, decisions, or diplomacy without needing hundreds of dummy files.
/// </summary>
public static class ModOverlayPolicy
{
    /// <summary>
    /// Declared virtual content roots permitted in <c>replace_paths</c>.
    /// Restricting replacement to safe content roots prevents mods from accidentally wiping
    /// engine internals or escaping the <c>game/</c> virtual tree.
    /// </summary>
    public static readonly HashSet<string> AllowedReplaceRoots = new(StringComparer.OrdinalIgnoreCase)
    {
        "common/technologies",
        "common/decisions",
        "common/ideologies",
        "common/governments",
        "common/goods",
        "common/units",
        "common/buildings",
        "common/production",
        "common/budget",
        "common/government_focus",
        "common/trade_policies",
        "common/laws",
        "common/agendas",
        "common/national_courses",
        "common/goals",
        "common/grand_projects",
        "common/promises",
        "common/modifiers",
        "common/opinion_modifiers",
        "common/ai_strategy",
        "common/formable_nations",
        "common/organizations",
        "common/religions",
        "common/cultures",
        "common/professions",
        "common/rulers",
        "common/deterrence",
        "common/support",
        "common/tactical_doctrines",
        "common/strategic_doctrines",
        "events",
        "history/countries",
        "history/states",
        "history/diplomacy",
        "history/conflicts",
        "history/factions",
        "history/units",
        "history/pops",
        "history/faiths",
        "history/provinces",
        "localization",
        "music",
        "interface",
        "gui",
        "skins"
    };

    /// <summary>
    /// Narrative/era roots suppressed automatically when a mod sets <c>"total_conversion": true</c>
    /// without specifying an explicit <c>replace_paths</c> list. Economic/structural roots stay
    /// inherited unless explicitly listed in <c>replace_paths</c>.
    /// </summary>
    public static readonly string[] TotalConversionDefaultReplacePaths =
    {
        "events",
        "common/technologies",
        "common/decisions",
        "history/diplomacy",
        "history/conflicts",
        "history/factions"
    };

    private static readonly object Sync = new();
    private static volatile bool _active;
    private static HashSet<string> _replacedRoots = new(StringComparer.OrdinalIgnoreCase);
    private static HashSet<string> _excludedPaths = new(StringComparer.OrdinalIgnoreCase);
    private static ModProvenanceEntry[] _provenance = Array.Empty<ModProvenanceEntry>();
    private static string _contentSignature = "";

    public static bool IsActive => _active;
    public static string ContentSignature => _contentSignature;
    public static IReadOnlyList<ModProvenanceEntry> Provenance => _provenance;

    public static void Reset()
    {
        lock (Sync)
        {
            _active = false;
            _replacedRoots = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _excludedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            _provenance = Array.Empty<ModProvenanceEntry>();
            _contentSignature = "";
        }
    }

    /// <summary>
    /// Validate and normalize one declared <c>replace_paths</c> or <c>exclude_paths</c> entry.
    /// Rejects path traversal (<c>..</c>), rooted paths, and undeclared replacement roots.
    /// </summary>
    public static bool TryNormalizePath(string raw, bool isReplaceRoot, out string normalized, out string error)
    {
        normalized = "";
        error = "";
        if (string.IsNullOrWhiteSpace(raw))
        {
            error = "Path entry cannot be empty.";
            return false;
        }
        var clean = raw.Trim().Replace('\\', '/');
        while (clean.Contains("//", StringComparison.Ordinal))
            clean = clean.Replace("//", "/", StringComparison.Ordinal);
        clean = clean.Trim('/');
        if (clean.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            clean = clean[5..];

        if (clean.Length == 0 || clean.Contains(':') || clean.Split('/').Any(seg => seg is ".." or "."))
        {
            error = $"Unsafe virtual path '{raw}'. Paths must be relative to game/ without '..' or drive letters.";
            return false;
        }

        if (isReplaceRoot)
        {
            if (!IsAllowedReplacePath(clean))
            {
                error = $"Unsupported replace_paths root '{raw}'. Allowed roots include: common/technologies, common/decisions, common/ideologies, common/governments, common/goods, common/units, events, history/diplomacy, history/conflicts, localization, music, skins.";
                return false;
            }
        }

        normalized = clean;
        return true;
    }

    public static bool IsAllowedReplacePath(string normalized)
    {
        if (string.IsNullOrWhiteSpace(normalized)) return false;
        if (AllowedReplaceRoots.Contains(normalized)) return true;
        // Allow bookmark subfolders under an allowed history root (e.g. "history/diplomacy/2024").
        foreach (var root in AllowedReplaceRoots)
            if (root.StartsWith("history/", StringComparison.OrdinalIgnoreCase) &&
                normalized.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Configure the active VFS policy from the enabled packages in load order.
    /// Returns any validation errors discovered in declared paths.
    /// </summary>
    public static List<string> Configure(IEnumerable<ModLayerSpec> layers)
    {
        var errors = new List<string>();
        var replaced = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var excluded = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var ownerByPath = new Dictionary<string, (string Mode, string Owner, List<string> Superseded)>(StringComparer.OrdinalIgnoreCase);

        if (layers is not null)
        {
            foreach (var layer in layers)
            {
                if (layer is null) continue;
                var effectiveReplace = layer.ReplacePaths is { Count: > 0 }
                    ? layer.ReplacePaths
                    : (layer.TotalConversion ? (IReadOnlyList<string>)TotalConversionDefaultReplacePaths : Array.Empty<string>());

                foreach (var raw in effectiveReplace)
                {
                    if (!TryNormalizePath(raw, isReplaceRoot: true, out var norm, out var err))
                    {
                        errors.Add($"[{layer.ModId}] {err}");
                        continue;
                    }
                    replaced.Add(norm);
                    RecordProvenance(ownerByPath, norm, "replace", layer.ModId);
                }

                if (layer.ExcludePaths is not null)
                {
                    foreach (var raw in layer.ExcludePaths)
                    {
                        if (!TryNormalizePath(raw, isReplaceRoot: false, out var norm, out var err))
                        {
                            errors.Add($"[{layer.ModId}] {err}");
                            continue;
                        }
                        excluded.Add(norm);
                        RecordProvenance(ownerByPath, norm, "exclude", layer.ModId);
                    }
                }
            }
        }

        var hasRules = replaced.Count > 0 || excluded.Count > 0;
        var provenanceList = ownerByPath
            .OrderBy(p => p.Key, StringComparer.OrdinalIgnoreCase)
            .Select(p => new ModProvenanceEntry(p.Key, p.Value.Owner, p.Value.Mode, p.Value.Superseded.ToArray()))
            .ToArray();

        var sig = hasRules
            ? BuildSignature(replaced, excluded)
            : "";

        lock (Sync)
        {
            _replacedRoots = replaced;
            _excludedPaths = excluded;
            _provenance = provenanceList;
            _contentSignature = sig;
            _active = hasRules;
        }
        return errors;
    }

    /// <summary>Temporarily apply a layer policy and restore the previous state on dispose (ideal for tests and isolated validation).</summary>
    public static IDisposable PushScope(IEnumerable<ModLayerSpec> layers, out List<string> errors)
    {
        HashSet<string> prevReplaced;
        HashSet<string> prevExcluded;
        ModProvenanceEntry[] prevProvenance;
        string prevSig;
        bool prevActive;
        lock (Sync)
        {
            prevReplaced = new HashSet<string>(_replacedRoots, StringComparer.OrdinalIgnoreCase);
            prevExcluded = new HashSet<string>(_excludedPaths, StringComparer.OrdinalIgnoreCase);
            prevProvenance = _provenance;
            prevSig = _contentSignature;
            prevActive = _active;
        }
        errors = Configure(layers);
        return new ScopeGuard(prevReplaced, prevExcluded, prevProvenance, prevSig, prevActive);
    }

    /// <summary>True when a base-game virtual category folder is suppressed by an active <c>replace_paths</c> rule.</summary>
    public static bool IsBasePathReplaced(params string[] relativeFolder)
    {
        if (!_active || relativeFolder is null || relativeFolder.Length == 0) return false;
        var joined = NormalizeJoined(relativeFolder);
        if (joined.Length == 0) return false;
        var roots = _replacedRoots;
        if (roots.Contains(joined)) return true;
        foreach (var root in roots)
            if (joined.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>True when a specific base-game file is suppressed by <c>exclude_paths</c> or its parent folder is replaced.</summary>
    public static bool IsBaseFileExcluded(string relativeFilePath)
    {
        if (!_active || string.IsNullOrWhiteSpace(relativeFilePath)) return false;
        var clean = relativeFilePath.Trim().Replace('\\', '/').Trim('/');
        if (clean.StartsWith("game/", StringComparison.OrdinalIgnoreCase))
            clean = clean[5..];

        var excluded = _excludedPaths;
        if (excluded.Contains(clean)) return true;
        foreach (var prefix in excluded)
            if (clean.StartsWith(prefix + "/", StringComparison.OrdinalIgnoreCase))
                return true;

        var roots = _replacedRoots;
        foreach (var root in roots)
            if (clean.Equals(root, StringComparison.OrdinalIgnoreCase) ||
                clean.StartsWith(root + "/", StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    private static string NormalizeJoined(string[] parts)
    {
        var segments = new List<string>(parts.Length);
        foreach (var part in parts)
        {
            if (string.IsNullOrWhiteSpace(part)) continue;
            var piece = part.Trim().Replace('\\', '/').Trim('/');
            if (piece.Length > 0) segments.Add(piece);
        }
        return string.Join("/", segments);
    }

    private static void RecordProvenance(
        Dictionary<string, (string Mode, string Owner, List<string> Superseded)> map,
        string path, string mode, string modId)
    {
        if (map.TryGetValue(path, out var existing))
        {
            if (!string.Equals(existing.Owner, modId, StringComparison.OrdinalIgnoreCase))
                existing.Superseded.Add(existing.Owner);
            map[path] = (mode, modId, existing.Superseded);
        }
        else
        {
            map[path] = (mode, modId, new List<string>());
        }
    }

    private static string BuildSignature(HashSet<string> replaced, HashSet<string> excluded)
    {
        var hash = 14695981039346656037UL;
        void MixText(string value)
        {
            foreach (var c in value)
            {
                hash = (hash ^ (byte)c) * 1099511628211UL;
                hash = (hash ^ (byte)(c >> 8)) * 1099511628211UL;
            }
            hash = (hash ^ 0xFF) * 1099511628211UL;
        }
        MixText("replace");
        foreach (var r in replaced.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) MixText(r.ToLowerInvariant());
        MixText("exclude");
        foreach (var e in excluded.OrderBy(x => x, StringComparer.OrdinalIgnoreCase)) MixText(e.ToLowerInvariant());
        return hash.ToString("X16");
    }

    private sealed class ScopeGuard : IDisposable
    {
        private readonly HashSet<string> _prevReplaced;
        private readonly HashSet<string> _prevExcluded;
        private readonly ModProvenanceEntry[] _prevProvenance;
        private readonly string _prevSig;
        private readonly bool _prevActive;
        private bool _disposed;

        public ScopeGuard(HashSet<string> replaced, HashSet<string> excluded, ModProvenanceEntry[] provenance, string sig, bool wasActive)
        {
            _prevReplaced = replaced;
            _prevExcluded = excluded;
            _prevProvenance = provenance;
            _prevSig = sig;
            _prevActive = wasActive;
        }

        public void Dispose()
        {
            if (_disposed) return;
            _disposed = true;
            lock (Sync)
            {
                _replacedRoots = _prevReplaced;
                _excludedPaths = _prevExcluded;
                ModOverlayPolicy._provenance = _prevProvenance;
                _contentSignature = _prevSig;
                _active = _prevActive;
            }
        }
    }
}
