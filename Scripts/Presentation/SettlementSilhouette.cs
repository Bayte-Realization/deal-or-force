using System;
using System.Collections.Generic;

namespace DealOrForce.Presentation;

/// <summary>Stylised atlas miniatures, not a reconstruction of real street plans.
/// Angular north/east offsets keep the same footprint in both projections.
/// No campaign state, RNG, engine object, dependency or per-frame allocation.</summary>
public static class SettlementSilhouette
{
    public const int MaximumBlocks = 14;
    public readonly record struct Block(float East, float North, float Width, float Depth, float Height, float Shade);

    public static Block[] Build(string id, long population)
    {
        var seed = 2166136261u;
        foreach (var c in id ?? "") seed = unchecked((seed ^ c) * 16777619u);
        var people = Math.Clamp(population, 0, 100_000_000);
        var size = .52f + .17f * Math.Clamp((float)Math.Log10(Math.Max(10_000, people)) - 4f, 0, 3.5f);
        var result = new List<Block>(MaximumBlocks);
        var bearing = (seed & 1023) * (MathF.Tau / 1024f);
        for (var i = 0; i < MaximumBlocks; i++)
        {
            seed = unchecked(seed * 1664525u + 1013904223u);
            // A compact irregular district, not a repeated rectangular stamp. The real
            // city pin is drawn above it; do not make a conspicuous ring around the pin.
            var angle = bearing + i * 2.3999632f;
            var radius = (.020f + .062f * MathF.Sqrt((i + 1f) / MaximumBlocks)) * size;
            var width = (.026f + (seed & 7) * .0018f) * size;
            var depth = (.022f + ((seed >> 3) & 7) * .0016f) * size;
            var highrise = people >= 2_000_000 && i < 5 ? 1.5f : 1f;
            var height = (.0014f + ((seed >> 6) & 7) * .00022f) * highrise;
            result.Add(new(MathF.Cos(angle) * radius, MathF.Sin(angle) * radius * .8f,
                width, depth, height, .76f + ((seed >> 9) & 15) * .022f));
        }
        return result.ToArray();
    }

    public static float Visibility(bool flat, float distance, float flatZoom)
    {
        var value = flat ? flatZoom : distance;
        if (!float.IsFinite(value)) return 0;
        var t = Math.Clamp(flat ? (1.25f-value)/.75f : (6.05f-value)/.65f, 0, 1);
        return t*t*(3-2*t);
    }
}
