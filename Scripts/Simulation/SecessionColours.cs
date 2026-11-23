using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool MigrateLegacySecessionColoursOnLoad { get; set; } = true;
    public string[] SecessionColourPalette { get; set; } = (string[])SecessionColours.DefaultPaletteHex.Clone();
}

/// <summary>One-time, saved political colour selection. Never consumes campaign RNG.</summary>
public static class SecessionColours
{
    internal static readonly string[] DefaultPaletteHex =
    {
        "#D95F43", "#2E9D6F", "#E0A42B", "#B84C7D", "#3B9AB2", "#88B04B",
        "#E57C35", "#9B5DE5", "#52B788", "#D64550", "#D4B830", "#2A9D8F",
        "#E26D8A", "#7A6FF0", "#6BB344", "#CC5A2E", "#48A9D6", "#C88A2C",
        "#A84A98", "#3CA374", "#E6954C", "#446ED8", "#A3B836", "#C94A6A",
        "#2D8C9E", "#D86C58", "#7459C7", "#5BA854", "#DE8838", "#B554A8",
        "#3D9B8B", "#C73E4E", "#8EA638", "#5A86E0", "#CF7242", "#47AD76"
    };

    private static readonly int[] DefaultPaletteInts = DefaultPaletteHex
        .Select(Parse)
        .Where(x => x.HasValue)
        .Select(x => x!.Value)
        .ToArray();

    internal static int[] MergedPalette(string[] supplied)
    {
        var seen = new HashSet<int>();
        var list = new List<int>(DefaultPaletteInts.Length);
        if (supplied is not null)
        {
            for (var i = 0; i < supplied.Length; i++)
                if (Parse(supplied[i]) is { } parsed && seen.Add(parsed))
                    list.Add(parsed);
        }
        for (var i = 0; i < DefaultPaletteInts.Length; i++)
        {
            var fallback = DefaultPaletteInts[i];
            if (seen.Add(fallback)) list.Add(fallback);
        }
        return list.ToArray();
    }
    /// <summary>Disk-load migration only: recognise the old channel-rotation formula
    /// exactly. Preserve authored/custom colours and never run this on a network snapshot.</summary>
    internal static int MigrateLegacy(Campaign c)
    {
        if (!c.Defines.MigrateLegacySecessionColoursOnLoad) return 0;
        var original = c.Nations.ToDictionary(n => n.Id, n => Parse(n.Color));
        var changed = 0;
        foreach (var n in c.Nations.OrderBy(n => n.Id))
        {
            var parent = c.NationById(n.SecededFromId);
            if (parent is null || string.IsNullOrEmpty(n.FoundedInRevolt) ||
                original.GetValueOrDefault(parent.Id) is not { } oldParent) continue;
            var r = oldParent >> 16 & 255; var g = oldParent >> 8 & 255; var b = oldParent & 255;
            var oldRebel = (Math.Min(255,b+40) << 16) | (Math.Min(255,r+20) << 8) | g;
            if (Parse(n.Color) != oldRebel) continue;
            n.Color = Choose(c,parent,n.Tag,c.StatesOf(n).Select(s => s.MapIndex));
            changed++;
        }
        return changed;
    }

    public static string Choose(Campaign c, Nation parent, string tag, IEnumerable<int> regions, string preferred = "")
    {
        var near = CollectNearbyColours(c, parent, tag, regions, excludeParent: false);
        float Separation(int colour) => near.Length == 0 ? 1f : near.Min(other => VisualSeparation(colour, other));
        if (Parse(preferred) is { } own && !IsNeutralFallback(own) && Separation(own) >= 0.18f)
            return $"#{own:X6}";
        var palette = (c.Defines.SecessionColourPalette ?? Array.Empty<string>()).Select(Parse)
            .Where(x => x.HasValue).Select(x => x!.Value).Distinct().ToArray();
        uint hash = unchecked((uint)c.Seed ^ 2166136261u);
        foreach (var ch in (parent?.Tag ?? "") + "/" + (tag ?? "")) hash = unchecked((hash ^ ch) * 16777619u);
        if (palette.Length > 0)
        {
            var bestConfigured = palette.Max(Separation);
            if (bestConfigured >= 0.14f)
            {
                var floor = MathF.Max(0.14f, bestConfigured * 0.72f);
                var viable = palette.Where(x => Separation(x) >= floor).ToArray();
                if (viable.Length > 0) return $"#{viable[hash % (uint)viable.Length]:X6}";
            }
        }
        // Merge the full 36-colour built-in palette whenever the supplied list is empty/malformed
        // or all of its shades are already saturated by neighboring/sibling states.
        var merged = palette.Length >= DefaultPaletteInts.Length ? palette : MergedPalette(c.Defines.SecessionColourPalette);
        var bestMerged = merged.Max(Separation);
        if (bestMerged >= 0.14f)
        {
            var floor = MathF.Max(0.14f, bestMerged * 0.72f);
            var viable = merged.Where(x => Separation(x) >= floor).ToArray();
            if (viable.Length > 0) return $"#{viable[hash % (uint)viable.Length]:X6}";
        }
        // Deterministic golden-angle hue-rotation fallback when palette shades are saturated by many splinters.
        var bestColour = merged[0];
        var bestSep = Separation(bestColour);
        var baseHue = (hash % 3600u) / 10f;
        for (var step = 0; step < 72; step++)
        {
            var hue = (baseHue + step * 137.507764f) % 360f;
            var sat = 0.56f + ((step % 3) * 0.13f);
            var val = 0.58f + (((step / 3) % 3) * 0.15f);
            var candidate = HsvToRgbInt(hue, sat, val);
            var sep = Separation(candidate);
            if (sep > bestSep) { bestSep = sep; bestColour = candidate; }
            if (sep >= 0.22f) return $"#{candidate:X6}";
        }
        return $"#{bestColour:X6}";
    }

    internal static bool ClashesWithNeighbors(Campaign c, Nation nation, IEnumerable<int> regions = null)
    {
        if (c is null || nation is null) return false;
        if (Parse(nation.Color) is not { } own || IsNeutralFallback(own)) return true;
        var parent = c.NationById(nation.SecededFromId) ?? c.NationById(nation.RevoltParentId);
        var near = CollectNearbyColours(c, parent ?? nation, nation.Tag,
            regions ?? c.StatesOf(nation).Select(s => s.MapIndex), excludeParent: false, selfId: nation.Id);
        return near.Any(other => VisualSeparation(own, other) < 0.14f);
    }

    internal static bool ClashesWithLivingExceptParent(Campaign c, Nation nation, Nation parent)
    {
        if (c is null || nation is null || Parse(nation.Color) is not { } own) return false;
        var near = CollectNearbyColours(c, parent, nation.Tag,
            c.StatesOf(nation).Select(s => s.MapIndex), excludeParent: true, selfId: nation.Id);
        return near.Any(other => VisualSeparation(own, other) < 0.14f);
    }

    private static bool IsNeutralFallback(int rgb) =>
        rgb == 0x6E7B8B || rgb == 0x888888;

    private static int[] CollectNearbyColours(
        Campaign c, Nation parent, string tag, IEnumerable<int> regions, bool excludeParent, int selfId = -1)
    {
        var adjacent = new HashSet<int>();
        var regionIds = new HashSet<int>(regions ?? Array.Empty<int>());
        var map = c.ProvinceMap;
        if (map is not null && regionIds.Count > 0)
        {
            var hop1 = new HashSet<int>();
            for (var p = 1; p <= map.ProvinceCount; p++)
            {
                if (!regionIds.Contains(map.ProvinceToState[p])) continue;
                foreach (var next in map.LandNeighbors[p])
                {
                    if (regionIds.Contains(map.ProvinceToState[next])) continue;
                    adjacent.Add(map.ProvinceToNation[next]);
                    hop1.Add(next);
                }
            }
            // 2-hop land neighbors prevent identical shades across narrow buffer corridors.
            foreach (var p in hop1)
                foreach (var next in map.LandNeighbors[p])
                    if (!regionIds.Contains(map.ProvinceToState[next])) adjacent.Add(map.ProvinceToNation[next]);
        }
        // Also walk 2-hop region adjacency so multi-province states still see neighboring countries
        // across borders before Partition/Annex updates ProvinceToNation.
        var stateAdj = c.Caches?.Secession?.Adjacent;
        if (stateAdj is not null && regionIds.Count > 0)
        {
            var adjRegions1 = new HashSet<int>();
            foreach (var r in regionIds)
            {
                if (!stateAdj.TryGetValue(r, out var neighbors)) continue;
                foreach (var nr in neighbors)
                {
                    if (regionIds.Contains(nr)) continue;
                    adjRegions1.Add(nr);
                    if (c.StateByMapIndex(nr) is { } ns && ns.NationMapIndex > 0) adjacent.Add(ns.NationMapIndex);
                }
            }
            foreach (var nr in adjRegions1)
            {
                if (!stateAdj.TryGetValue(nr, out var neighbors2)) continue;
                foreach (var nr2 in neighbors2)
                {
                    if (regionIds.Contains(nr2)) continue;
                    if (c.StateByMapIndex(nr2) is { } ns2 && ns2.NationMapIndex > 0) adjacent.Add(ns2.NationMapIndex);
                }
            }
        }
        var rootParentId = parent is null ? -1 : (parent.RevoltParentId >= 0 ? parent.RevoltParentId : parent.Id);
        var parentCulture = parent is null ? ""
            : !string.IsNullOrEmpty(parent.DeclaredCulture)
                ? parent.DeclaredCulture
                : DemographyCatalog.PrimaryCultures(parent.Tag).FirstOrDefault() ?? "";
        var subRegion = parent?.Region ?? "";
        var nearList = new List<int>();
        foreach (var n in c.Nations)
        {
            if (n.Id == selfId || (!string.IsNullOrEmpty(tag) && string.Equals(n.Tag, tag, StringComparison.OrdinalIgnoreCase)))
                continue;
            if (excludeParent && parent is not null && n.Id == parent.Id) continue;
            if (n.Population <= 0 && (parent is null || n.Id != parent.Id)) continue;
            var sameLineage = parent is not null &&
                              (n.Id == parent.Id || n.Id == rootParentId ||
                               n.SecededFromId == parent.Id || n.RevoltParentId == parent.Id ||
                               (rootParentId >= 0 && (n.SecededFromId == rootParentId || n.RevoltParentId == rootParentId)));
            var sameCulture = parentCulture.Length > 0 &&
                              (string.Equals(n.DeclaredCulture, parentCulture, StringComparison.OrdinalIgnoreCase) ||
                               n.FoundedCultures.Contains(parentCulture, StringComparer.OrdinalIgnoreCase));
            var sameSubRegion = subRegion.Length > 0 &&
                                string.Equals(n.Region, subRegion, StringComparison.OrdinalIgnoreCase);
            if (sameLineage || sameCulture || sameSubRegion || adjacent.Contains(n.MapIndex))
                if (Parse(n.Color) is { } parsed) nearList.Add(parsed);
        }
        return nearList.ToArray();
    }

    internal static float VisualSeparation(int a, int b)
    {
        var dist = MathF.Sqrt(MathF.Max(0f, Distance(a, b)));
        var (h1, s1, v1) = RgbToHsv(a);
        var (h2, s2, v2) = RgbToHsv(b);
        var dv = MathF.Abs(v1 - v2);
        if (MathF.Max(s1, s2) >= 0.18f && MathF.Min(s1, s2) >= 0.18f)
        {
            var dh = MathF.Abs(h1 - h2);
            if (dh > 180f) dh = 360f - dh;
            if (dh < 20f && dv < 0.22f)
                return MathF.Min(dist, 0.09f * (dh / 20f));
        }
        else if (MathF.Max(s1, s2) < 0.22f && dv < 0.22f)
        {
            return MathF.Min(dist, 0.08f);
        }
        return dist;
    }

    private static (float H, float S, float V) RgbToHsv(int rgb)
    {
        var r = ((rgb >> 16) & 255) / 255f;
        var g = ((rgb >> 8) & 255) / 255f;
        var b = (rgb & 255) / 255f;
        var max = MathF.Max(r, MathF.Max(g, b));
        var min = MathF.Min(r, MathF.Min(g, b));
        var d = max - min;
        var s = max > 1e-5f ? d / max : 0f;
        var h = 0f;
        if (d > 1e-5f)
        {
            if (max == r) h = 60f * (((g - b) / d) % 6f);
            else if (max == g) h = 60f * ((b - r) / d + 2f);
            else h = 60f * ((r - g) / d + 4f);
            if (h < 0f) h += 360f;
        }
        return (h, s, max);
    }

    private static int HsvToRgbInt(float h, float s, float v)
    {
        var c = v * s;
        var x = c * (1f - MathF.Abs((h / 60f) % 2f - 1f));
        var m = v - c;
        float r, g, b;
        if (h < 60f) { r = c; g = x; b = 0f; }
        else if (h < 120f) { r = x; g = c; b = 0f; }
        else if (h < 180f) { r = 0f; g = c; b = x; }
        else if (h < 240f) { r = 0f; g = x; b = c; }
        else if (h < 300f) { r = x; g = 0f; b = c; }
        else { r = c; g = 0f; b = x; }
        var ri = Math.Clamp((int)MathF.Round((r + m) * 255f), 0, 255);
        var gi = Math.Clamp((int)MathF.Round((g + m) * 255f), 0, 255);
        var bi = Math.Clamp((int)MathF.Round((b + m) * 255f), 0, 255);
        return (ri << 16) | (gi << 8) | bi;
    }

    internal static float Distance(int a, int b)
    {
        var r = ((a >> 16 & 255) - (b >> 16 & 255)) / 255f;
        var g = ((a >> 8 & 255) - (b >> 8 & 255)) / 255f;
        var blue = ((a & 255) - (b & 255)) / 255f;
        return (3*r*r + 4*g*g + 2*blue*blue) / 9f;
    }

    private static int? Parse(string text) => text is not null && text.Length == 7 && text[0] == '#' &&
        int.TryParse(text.AsSpan(1), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out var value) ? value : null;
}
