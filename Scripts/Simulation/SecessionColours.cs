using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool MigrateLegacySecessionColoursOnLoad { get; set; } = true;
    public string[] SecessionColourPalette { get; set; } = {
        "#C57857", "#4F94C0", "#BA87B5", "#8BA654", "#D5AD57", "#4CA894",
        "#B35F80", "#8B8ACB", "#C9854B", "#65A9B6", "#A9A168", "#6685B7" };
}

/// <summary>One-time, saved political colour selection. Never consumes campaign RNG.</summary>
public static class SecessionColours
{
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
        var adjacent = new HashSet<int>();
        var regionIds = new HashSet<int>(regions ?? Array.Empty<int>());
        var map = c.ProvinceMap;
        if (map is not null)
            for (var p = 1; p <= map.ProvinceCount; p++)
            {
                if (!regionIds.Contains(map.ProvinceToState[p])) continue;
                foreach (var next in map.Neighbors[p])
                    if (!regionIds.Contains(map.ProvinceToState[next])) adjacent.Add(map.ProvinceToNation[next]);
            }
        var near = c.Nations.Where(n => n.Tag != tag && (n.Id == parent.Id || adjacent.Contains(n.MapIndex)))
            .Select(n => Parse(n.Color)).Where(x => x.HasValue).Select(x => x.Value).ToArray();
        float Separation(int colour) => near.Length == 0 ? 1f : near.Min(other => Distance(colour, other));
        if (Parse(preferred) is { } own && Separation(own) >= .065f) return $"#{own:X6}";
        var palette = (c.Defines.SecessionColourPalette ?? Array.Empty<string>()).Select(Parse)
            .Where(x => x.HasValue).Select(x => x.Value).Distinct().ToArray();
        // A malformed/empty mod palette still gets a readable deterministic colour.
        if (palette.Length == 0) palette = new[] { 0x4F94C0, 0xC57857, 0x8BA654, 0xBA87B5 };
        var best = palette.Max(Separation);
        // Pick among sufficiently distinct colours, not always the single farthest
        // one: otherwise thirty revolts against the same grey parent all turn ochre.
        var viable = palette.Where(x => Separation(x) >= Math.Min(.035f, best * .45f)).ToArray();
        uint hash = unchecked((uint)c.Seed ^ 2166136261u);
        foreach (var ch in parent.Tag + "/" + tag) hash = unchecked((hash ^ ch) * 16777619u);
        return $"#{viable[hash % (uint)viable.Length]:X6}";
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
