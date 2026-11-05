using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public static partial class FlagCatalog
{
    private sealed record ContextRule(Dictionary<string,string[]> Requires, string Otherwise);
    private static readonly Dictionary<string,ContextRule> ContextRules = new(StringComparer.OrdinalIgnoreCase);

    private static void MergeContextRules(JsonElement root, List<string> errors)
    {
        if (!root.TryGetProperty("flag_context",out var declarations) || declarations.ValueKind != JsonValueKind.Object) return;
        foreach (var declaration in declarations.EnumerateObject())
        {
            var body = declaration.Value;
            if (body.ValueKind != JsonValueKind.Object || !body.TryGetProperty("requires",out var requires) || requires.ValueKind != JsonValueKind.Object)
            { errors?.Add($"common/flags: {declaration.Name} needs a requires object."); continue; }
            var conditions = new Dictionary<string,string[]>(StringComparer.OrdinalIgnoreCase);
            var valid = true;
            foreach (var term in requires.EnumerateObject())
            {
                if (!IsKnownSource(term.Name))
                { errors?.Add($"common/flags: unknown context source {term.Name}."); valid = false; continue; }
                if (term.Value.ValueKind == JsonValueKind.String) conditions[term.Name] = new[]{Slug(term.Value.GetString())};
                else if (term.Value.ValueKind == JsonValueKind.Array && term.Value.EnumerateArray().All(x=>x.ValueKind==JsonValueKind.String))
                    conditions[term.Name] = term.Value.EnumerateArray().Select(x=>Slug(x.GetString())).ToArray();
                else { errors?.Add($"common/flags: invalid context values for {declaration.Name}/{term.Name}."); valid = false; }
            }
            if (!valid) continue;
            if (conditions.Count == 0) { ContextRules.Remove(declaration.Name); continue; }
            var fallback = body.TryGetProperty("otherwise",out var alternate) && alternate.ValueKind == JsonValueKind.String
                ? Slug(alternate.GetString()) : "";
            ContextRules[declaration.Name] = new(conditions,fallback);
        }
    }

    private static List<string> ApplyContextRules(Campaign c, Nation n, List<string> names)
    {
        for (var i = names.Count-1; i >= 0; i--)
        {
            var name = names[i];
            var stableIdentity = PreferCountryBase && n.Movement is null &&
                (name == "flag_" + Slug(n.Tag) || name == "flag_formable_" + Slug(n.FormedNationId) ||
                 name == "flag_" + Slug(n.Tag) + "_" + Slug(n.FormedNationId));
            var resolved = false;
            // Bounded even for a mod that accidentally creates a fallback cycle.
            for (var depth = 0; depth < 16; depth++)
            {
                if (!ContextRules.TryGetValue(name,out var rule) || rule.Requires.All(pair =>
                    (stableIdentity && pair.Key is "ideology" or "government") ||
                    pair.Value.Contains(ValueOf(pair.Key,c,n),StringComparer.OrdinalIgnoreCase)))
                { resolved = true; break; }
                name = rule.Otherwise;
                if (name.Length == 0) break;
            }
            if (!resolved || name.Length == 0) names.RemoveAt(i); else names[i] = name;
        }
        return names;
    }
}
