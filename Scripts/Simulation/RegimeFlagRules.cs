using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class FlagCatalog
{
    private sealed record RegimeFlag(string Sprite, Dictionary<string, string[]> Requires);
    private static readonly Dictionary<string, List<RegimeFlag>> RegimeFlags = new(StringComparer.OrdinalIgnoreCase);

    // Explicit authored exceptions, not the old blanket ideology-first fallback. An empty
    // per-country list removes inherited rules. A formable/restoration and a custom flag
    // retain their identities; previews use the same resolver as actual countries.
    private static void MergeRegimeFlags(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("regime_flags", out var table) || table.ValueKind != JsonValueKind.Object) return;
        foreach (var country in table.EnumerateObject())
        {
            if (country.Value.ValueKind != JsonValueKind.Array)
            { errors?.Add($"regime_flags/{country.Name}: expected a list."); continue; }
            var entries = new List<RegimeFlag>(); var valid = true;
            foreach (var entry in country.Value.EnumerateArray())
            {
                if (entry.ValueKind != JsonValueKind.Object || !entry.TryGetProperty("sprite", out var sprite) ||
                    sprite.ValueKind != JsonValueKind.String || !(sprite.GetString() ?? "").StartsWith("flag_", StringComparison.Ordinal) ||
                    !entry.TryGetProperty("requires", out var requires) || requires.ValueKind != JsonValueKind.Object)
                { errors?.Add($"regime_flags/{country.Name}: sprite and requires object are required."); valid = false; continue; }
                var conditions = new Dictionary<string, string[]>(StringComparer.OrdinalIgnoreCase);
                foreach (var term in requires.EnumerateObject())
                {
                    var values = term.Value.ValueKind == JsonValueKind.String ? new[] { Slug(term.Value.GetString()) } :
                        term.Value.ValueKind == JsonValueKind.Array && term.Value.EnumerateArray().All(v => v.ValueKind == JsonValueKind.String)
                            ? term.Value.EnumerateArray().Select(v => Slug(v.GetString())).ToArray() : Array.Empty<string>();
                    if (!IsKnownSource(term.Name) || values.Length == 0 || values.Any(v => v.Length == 0))
                    { errors?.Add($"regime_flags/{country.Name}: invalid condition {term.Name}."); valid = false; continue; }
                    conditions[term.Name] = values;
                }
                if (conditions.Count == 0)
                { errors?.Add($"regime_flags/{country.Name}: unconditional overrides belong in gfx sprites."); valid = false; continue; }
                entries.Add(new RegimeFlag(Slug(sprite.GetString()), conditions));
            }
            if (valid) RegimeFlags[Slug(country.Name)] = entries;
        }
    }

    private static void AddRegimeFlags(Campaign c, Nation n, List<string> names)
    {
        if (!KeepsNationalIdentity(n) || !string.IsNullOrEmpty(n.FormedNationId) ||
            !RegimeFlags.TryGetValue(Slug(n.Tag), out var rules)) return;
        foreach (var rule in rules)
            if (rule.Requires.All(term => term.Value.Contains(ValueOf(term.Key, c, n), StringComparer.OrdinalIgnoreCase)))
                names.Add(rule.Sprite);
    }
}
