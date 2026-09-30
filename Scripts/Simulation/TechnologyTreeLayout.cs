using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Deterministic grid layout; handles malformed mod graphs without recursive traversal.</summary>
public static class TechnologyTreeLayout
{
    public sealed record Node(string Id, int Column, int Row);
    public static Node[] Build(IEnumerable<TechnologyDefinition> definitions, string category = "")
    {
        var all = definitions.ToArray();
        var depth = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Missing/cyclic prerequisites stay locked in the simulation and are diagnosed by
        // the catalogue. Still show their nodes so the modder can inspect what is wrong.
        for (var pass = 0; pass < all.Length; pass++)
        {
            var added = false;
            foreach (var tech in all)
            {
                if (depth.ContainsKey(tech.Id) || !tech.Requires.All(depth.ContainsKey)) continue;
                depth[tech.Id] = tech.Requires.Count == 0 ? 0 : Math.Min(128, tech.Requires.Max(id => depth[id]) + 1);
                added = true;
            }
            if (!added) break;
        }
        var occupied = new HashSet<(int, int)>();
        var result = new List<Node>();
        var visible = all.Where(t => category.Length == 0 || string.Equals(t.Category, category, StringComparison.OrdinalIgnoreCase)).ToArray();
        // Explicit slots first. Collisions move down instead of making buttons overlap.
        foreach (var tech in visible.OrderBy(t => t.TreeRow.HasValue ? 0 : 1))
        {
            var column = tech.TreeColumn ?? depth.GetValueOrDefault(tech.Id);
            var row = tech.TreeRow ?? 0;
            while (!occupied.Add((column, row))) row++;
            result.Add(new(tech.Id, column, row));
        }
        return result.ToArray();
    }
}
