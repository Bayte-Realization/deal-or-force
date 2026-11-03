using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A ministry's reading aid, not a research queue. Lists the still-needed discoveries in
/// prerequisite order, counting a shared prerequisite once. Never writes a country, spends
/// points, consumes campaign randomness or guesses what future diplomacy will make cheaper.
/// </summary>
public static class ResearchRoute
{
    public sealed record Path(TechnologyDefinition[] Steps, string[] Missing, string[] Cyclic,
        TechnologyDefinition Next)
    {
        public bool Valid => Missing.Length == 0 && Cyclic.Length == 0;
    }

    public readonly record struct Estimate(double RemainingPoints, int? Weeks);

    public static Path Build(IEnumerable<TechnologyDefinition> definitions, Nation nation, string target)
    {
        var catalog = definitions.ToDictionary(t => t.Id, StringComparer.OrdinalIgnoreCase);
        var steps = new List<TechnologyDefinition>();
        var missing = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var cyclic = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var known = nation is null ? new HashSet<string>(StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(nation.Technologies, StringComparer.OrdinalIgnoreCase);
        var state = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        var stack = new Stack<(string Id, bool Finish)>();
        if (string.IsNullOrWhiteSpace(target) || !catalog.ContainsKey(target))
            return new(Array.Empty<TechnologyDefinition>(), new[] { target ?? "" }, Array.Empty<string>(), null);
        stack.Push((target, false));
        // Explicit frames keep a deep modded chain off the C# call stack. Grey nodes are
        // ancestors of the current frame; black nodes are shared work already counted.
        while (stack.TryPop(out var frame))
        {
            if (known.Contains(frame.Id)) continue;
            if (!catalog.TryGetValue(frame.Id, out var technology)) { missing.Add(frame.Id); continue; }
            if (frame.Finish) { state[frame.Id] = 2; steps.Add(technology); continue; }
            if (state.TryGetValue(frame.Id, out var seen))
            {
                if (seen == 1) cyclic.Add(frame.Id);
                continue;
            }
            state[frame.Id] = 1;
            stack.Push((frame.Id, true));
            // Authored prerequisite order is the deterministic tie-break, not hash order.
            for (var i = technology.Requires.Count - 1; i >= 0; i--)
                stack.Push((technology.Requires[i], false));
        }
        var valid = missing.Count == 0 && cyclic.Count == 0;
        var next = valid && nation is not null ? steps.FirstOrDefault(t => t.Requires.All(known.Contains)) : null;
        return new(steps.ToArray(), missing.ToArray(), cyclic.ToArray(), next);
    }

    /// <summary>
    /// Today's remaining prices and today's pace. Partial investment is subtracted per project;
    /// known branches were pruned by Build. Research-speed discoveries, diffusion and funding
    /// may change later, so this is explicitly a snapshot, never an arrival-date promise.
    /// </summary>
    public static Estimate Price(Path path, Nation nation, Func<TechnologyDefinition, float> effectiveCost, float pace)
    {
        if (!path.Valid) return new(double.NaN, null);
        double total = 0;
        foreach (var step in path.Steps)
        {
            var cost = effectiveCost(step);
            var invested = nation?.ResearchProgress.GetValueOrDefault(step.Id) ?? 0;
            if (!float.IsFinite(cost) || cost < 0 || !float.IsFinite(invested)) return new(double.NaN, null);
            total += Math.Max(0, (double)cost - Math.Max(0, invested));
        }
        return new(total, float.IsFinite(pace) && pace > 0
            ? (int)Math.Min(int.MaxValue, Math.Ceiling(total / pace)) : null);
    }
}
