using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Rules;
using DealOrForce.Simulation;

/// <summary>
/// Exhaustive differential oracle tests between C# and pure F# PeaceRules implementations.
/// </summary>
public static class PeaceRulesDifferentialTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL peace rules differential: " + text);
        Console.WriteLine("PASS peace rules differential: " + text);
    }

    public static void Run()
    {
        CompactnessScoreDifferential();
        IsPocketDifferential();
        FindPartsDifferential();
        IsLeafDifferential();
        HookIntegrationCheck();
    }

    private static void CompactnessScoreDifferential()
    {
        var discrepancies = 0;
        var cases = 0;

        for (var shared = 0; shared <= 15; shared++)
        {
            for (var total = 0; total <= 15; total++)
            {
                for (var weightIdx = 0; weightIdx <= 10; weightIdx++)
                {
                    var weight = weightIdx * 0.5f;
                    cases++;

                    // Reference C# formula from PeaceShape
                    var expected = 0.35f + MathF.Max(0f, weight) * shared / MathF.Max(1f, total);
                    var actual = PeaceRules.CompactnessScore(shared, total, weight);

                    if (MathF.Abs(expected - actual) > 0.0001f)
                    {
                        discrepancies++;
                    }
                }
            }
        }

        Check(discrepancies == 0, $"CompactnessScore matched across all {cases} cases (0 discrepancies)");
    }

    private static void IsPocketDifferential()
    {
        var discrepancies = 0;
        var cases = 0;
        long[] populations = { 0, 10_000, 100_000, 500_000, 1_000_000, 5_000_000 };
        float[] shares = { 0.05f, 0.10f, 0.20f, 0.30f, 0.50f };

        foreach (var pocket in populations)
        {
            foreach (var main in populations)
            {
                foreach (var share in shares)
                {
                    cases++;
                    var expected = (float)pocket <= (float)main * share;
                    var actual = PeaceRules.IsPocket(pocket, main, share);

                    if (expected != actual)
                    {
                        discrepancies++;
                    }
                }
            }
        }

        Check(discrepancies == 0, $"IsPocket matched across all {cases} cases (0 discrepancies)");
    }

    private static void FindPartsDifferential()
    {
        // Construct a synthetic adjacency graph
        // Component 1: 1 - 2 - 3
        // Component 2: 4 - 5
        // Component 3: 6
        var adj = new Dictionary<int, List<int>>
        {
            [1] = new() { 2 },
            [2] = new() { 1, 3 },
            [3] = new() { 2 },
            [4] = new() { 5 },
            [5] = new() { 4 },
            [6] = new()
        };

        IEnumerable<int> Neighbors(int node) => adj.TryGetValue(node, out var list) ? list : Enumerable.Empty<int>();

        var allNodes = new[] { 1, 2, 3, 4, 5, 6 };
        var parts = PeaceRules.FindParts(Neighbors, allNodes);

        Check(parts.Length == 3, $"FindParts found 3 components (got {parts.Length})");

        var c1 = parts.FirstOrDefault(p => p.Contains(1));
        Check(c1 is not null && c1.Length == 3 && c1.Contains(2) && c1.Contains(3), "Component 1 is {1, 2, 3}");

        var c2 = parts.FirstOrDefault(p => p.Contains(4));
        Check(c2 is not null && c2.Length == 2 && c2.Contains(5), "Component 2 is {4, 5}");

        var c3 = parts.FirstOrDefault(p => p.Contains(6));
        Check(c3 is not null && c3.Length == 1, "Component 3 is {6}");

        // Subset of nodes: {1, 3} (2 is omitted, so 1 and 3 become disconnected)
        var subset = new[] { 1, 3 };
        var subParts = PeaceRules.FindParts(Neighbors, subset);
        Check(subParts.Length == 2, $"Subset without bridge {1, 3} breaks into 2 components (got {subParts.Length})");
    }

    private static void IsLeafDifferential()
    {
        // Linear chain: 1 - 2 - 3 - 4 - 5
        // Anchor: { 1 }
        var adj = new Dictionary<int, List<int>>
        {
            [1] = new() { 2 },
            [2] = new() { 1, 3 },
            [3] = new() { 2, 4 },
            [4] = new() { 3, 5 },
            [5] = new() { 4 }
        };

        IEnumerable<int> Neighbors(int node) => adj.TryGetValue(node, out var list) ? list : Enumerable.Empty<int>();

        var chosen = new HashSet<int> { 1, 2, 3, 4, 5 };
        var anchors = new HashSet<int> { 1 };

        // Node 5 is the tip: removing it leaves {1, 2, 3, 4} connected to anchor 1 => Leaf == true
        Check(PeaceRules.IsLeaf(Neighbors, chosen, anchors, 5), "Node 5 is a leaf (can be returned without disconnecting)");

        // Node 3 is in the middle: removing it leaves {4, 5} disconnected from anchor 1 => Leaf == false
        Check(!PeaceRules.IsLeaf(Neighbors, chosen, anchors, 3), "Node 3 is articulation point (cannot be returned)");

        // Node 2 is near anchor: removing it leaves {3, 4, 5} disconnected => Leaf == false
        Check(!PeaceRules.IsLeaf(Neighbors, chosen, anchors, 2), "Node 2 is articulation point (cannot be returned)");

        // Node 1 is the anchor itself: removing it leaves rest without anchors => Leaf == false
        Check(!PeaceRules.IsLeaf(Neighbors, chosen, anchors, 1), "Removing only anchor leaves rest unanchored");

        // Star topology: center 10 connected to {11, 12, 13}, anchor 10
        adj[10] = new() { 11, 12, 13 };
        adj[11] = new() { 10 };
        adj[12] = new() { 10 };
        adj[13] = new() { 10 };

        var starChosen = new HashSet<int> { 10, 11, 12, 13 };
        var starAnchors = new HashSet<int> { 10 };

        Check(PeaceRules.IsLeaf(Neighbors, starChosen, starAnchors, 11), "Star arm 11 is a leaf");
        Check(PeaceRules.IsLeaf(Neighbors, starChosen, starAnchors, 12), "Star arm 12 is a leaf");
        Check(PeaceRules.IsLeaf(Neighbors, starChosen, starAnchors, 13), "Star arm 13 is a leaf");
        Check(!PeaceRules.IsLeaf(Neighbors, starChosen, starAnchors, 10), "Star center 10 is not a leaf");
    }

    private static void HookIntegrationCheck()
    {
        // Verify PeaceRulesHook delegates to F#
        var adj = new Dictionary<int, List<int>>
        {
            [100] = new() { 101 },
            [101] = new() { 100 }
        };
        IEnumerable<int> Neighbors(int node) => adj.TryGetValue(node, out var list) ? list : Enumerable.Empty<int>();

        var chosen = new HashSet<int> { 100, 101 };
        var anchors = new HashSet<int> { 100 };

        var hooked = PeaceRulesHook.TryLeaf(Neighbors, chosen, anchors, 101, out var isLeaf);
        Check(hooked, "PeaceRulesHook is successfully installed and invoked");
        Check(isLeaf, "Hook correctly determined leaf status");
    }
}
