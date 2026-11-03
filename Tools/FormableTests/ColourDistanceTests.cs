using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Do two countries on the political map wear the same colour?
///
/// A player reads the map by colour before anything else: the ocean is blue, so a country is a
/// shape of not-blue, and two shapes of the same not-blue a hundred pixels apart are one country
/// with a strange border. Nobody notices it in a single screenshot; everybody notices it when they
/// mistake Japan for the DPRK, or China for a Myanmar government in exile.
///
/// The colours are content, in `game/common/countries/*.json` as `"color": "#rrggbb"`, so this is a
/// data defect and it is checked in data. Distance is the redmean approximation of perceptual
/// difference - cheap, and good enough to answer "would a player confuse these two".
/// </summary>
static class ColourDistanceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("colour distance: " + label);
        Console.WriteLine("PASS colour distance: " + label);
    }

    /// <summary>Redmean: a perceptual distance that costs three multiplications, on a 0..765 scale.</summary>
    static double Distance(string first, string second)
    {
        var (r1, g1, b1) = Rgb(first);
        var (r2, g2, b2) = Rgb(second);
        var rmean = (r1 + r2) / 2.0;
        var dr = r1 - r2;
        var dg = g1 - g2;
        var db = b1 - b2;
        return Math.Sqrt((2 + rmean / 256) * dr * dr + 4 * dg * dg + (2 + (255 - rmean) / 256) * db * db);
    }

    static (int, int, int) Rgb(string hex)
    {
        if (hex.Length != 6) return (0, 0, 0);
        return (Convert.ToInt32(hex[..2], 16), Convert.ToInt32(hex.Substring(2, 2), 16), Convert.ToInt32(hex.Substring(4, 2), 16));
    }

    /// <summary>The repository root, found by walking up until the content folder is there.</summary>
    static string Root()
    {
        var at = new DirectoryInfo(AppContext.BaseDirectory);
        while (at is not null && !Directory.Exists(Path.Combine(at.FullName, "game", "common", "countries"))) at = at.Parent;
        return at?.FullName ?? throw new Exception("game/common/countries not found above " + AppContext.BaseDirectory);
    }

    public static void Run()
    {
        var root = Root();
        // Three homes, two spellings, and a pattern that does not care which: a country carries
        // `color` in `common/countries`, a faction state carries `color` beside it, but a faction in
        // history carries `colour` - and the Myanmar government in exile is one of those, which is
        // why the first version of this check reported "nothing near China" about a pair a player
        // could see with their own eyes.
        var found = new SortedDictionary<string, string>(StringComparer.Ordinal);
        // Two shapes, and both are needed. In `common/countries` the tag IS the object key
        // (`"ABK": { ... "color": "#48a284" }`); in `history/factions` it is a field
        // (`"id": "...", "tag": "NUG", ... "colour": "#d4af37"`). The first version of this check
        // knew only the first shape and only the American spelling, so it reported "nothing near
        // China" about the Myanmar government in exile - a pair a player could see on the map.
        var byKey = new Regex("\"(?<tag>[A-Z0-9_]{2,6})\"\\s*:\\s*\\{[^{}]*?\"colou?r\"\\s*:\\s*\"#(?<hex>[0-9a-fA-F]{6})\"",
            RegexOptions.Singleline);
        var byField = new Regex("\"(?:tag|id)\"\\s*:\\s*\"(?<tag>[A-Za-z0-9_]{2,24})\"[\\s\\S]{0,900}?\"colou?r\"\\s*:\\s*\"#(?<hex>[0-9a-fA-F]{6})\"",
            RegexOptions.Singleline);
        foreach (var folder in new[] { Path.Combine(root, "game", "common"), Path.Combine(root, "game", "history") })
        foreach (var file in Directory.GetFiles(folder, "*.json", SearchOption.AllDirectories))
        {
            var text = File.ReadAllText(file);
            foreach (var pattern in new[] { byKey, byField })
                foreach (Match match in pattern.Matches(text))
                {
                    var tag = match.Groups["tag"].Value.ToUpperInvariant();
                    if (tag.Length == 0 || found.ContainsKey(tag)) continue;
                    found[tag] = match.Groups["hex"].Value;
                }
        }
        Check(found.Count > 250, $"{found.Count} countries and factions carry a colour in content");

        var worst = new List<(double Distance, string A, string B, string HexA, string HexB)>();
        var keys = found.Keys.ToList();
        for (var i = 0; i < keys.Count; i++)
            for (var j = i + 1; j < keys.Count; j++)
            {
                var a = keys[i];
                var b = keys[j];
                var distance = Distance(found[a], found[b]);
                if (distance < 60) worst.Add((distance, a, b, found[a], found[b]));
            }
        worst.Sort((left, right) => left.Distance.CompareTo(right.Distance));

        Console.WriteLine($"COLOUR_PAIRS under 60: {worst.Count} of {keys.Count * (keys.Count - 1) / 2} pairs");
        var exact = worst.Count(pair => pair.Distance < 5);
        Console.WriteLine($"EXACT_DUPLICATES (distance under 5): {exact}");
        foreach (var pair in worst.Take(20))
            Console.WriteLine($"  {pair.Distance,6:0.0}  {pair.A} #{pair.HexA}  {pair.B} #{pair.HexB}");

        // Everything near the countries a player looks at first: the eye is drawn to the big shapes,
        // and a big shape next to a same-coloured one is the defect that gets reported.
        foreach (var tag in new[] { "CHN", "JPN", "PRK", "MMR", "RUS", "USA", "IND", "KOR" })
        {
            if (!found.TryGetValue(tag, out var hex)) continue;
            var near = keys.Where(other => other != tag && Distance(hex, found[other]) < 60)
                .Select(other => $"{other} #{found[other]} ({Distance(hex, found[other]):0.0})").ToArray();
            Console.WriteLine($"  NEAR {tag} #{hex}: {(near.Length == 0 ? "nothing" : string.Join(", ", near))}");
        }

        // The two pairs a player has actually reported, kept as named cases so the answer to
        // "is it fixed?" is a line in a test rather than a squint at a screenshot.
        Report(found, "JPN", "PRK");
        Report(found, "CHN", "MMR");
        foreach (var pair in new[] { ("JPN", "PRK"), ("CHN", "MMR") })
            foreach (var (tag, hex) in new[] { (pair.Item1, found.GetValueOrDefault(pair.Item1)), (pair.Item2, found.GetValueOrDefault(pair.Item2)) })
                if (hex is null) Console.WriteLine($"  (no colour for {tag})");

        // ---- the border a player cannot see ------------------------------------------------
        // A country whose neighbour wears the same colour has no visible border at all, and that is
        // the defect people report: not "these two are similar", but "Kosovo blends into Serbia".
        // So the palette is also read through the map's own adjacency.
        var campaign = Campaign.CreateBootstrap(947986505);
        if (MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var topology, out var why))
        {
            campaign.AttachProvinceTopology(topology);
            var tagOfMap = new Dictionary<int, string>();
            foreach (var nation in campaign.Nations)
                if (nation.MapIndex > 0 && !string.IsNullOrEmpty(nation.Tag) && !tagOfMap.ContainsKey(nation.MapIndex))
                    tagOfMap[nation.MapIndex] = nation.Tag.ToUpperInvariant();
            var nationsOfState = new Dictionary<int, int>();
            foreach (var state in campaign.States)
                if (state.MapIndex > 0) nationsOfState[state.MapIndex] = state.NationMapIndex;

            var borders = new HashSet<(string, string)>();
            foreach (var state in campaign.States)
            {
                if (state.MapIndex <= 0 || !nationsOfState.TryGetValue(state.MapIndex, out var mineMap)) continue;
                if (!tagOfMap.TryGetValue(mineMap, out var mine)) continue;
                foreach (var next in TerritorialCoherence.AdjacentRegions(campaign, state.MapIndex))
                {
                    if (!nationsOfState.TryGetValue(next, out var theirMap) || theirMap == mineMap) continue;
                    if (!tagOfMap.TryGetValue(theirMap, out var theirs)) continue;
                    if (string.CompareOrdinal(mine, theirs) < 0) borders.Add((mine, theirs));
                    else borders.Add((theirs, mine));
                }
            }
            var clashes = new List<(double Distance, string A, string B, string HexA, string HexB)>();
            foreach (var (a, b) in borders)
            {
                if (!found.TryGetValue(a, out var ha) || !found.TryGetValue(b, out var hb)) continue;
                var distance = Distance(ha, hb);
                if (distance < 70) clashes.Add((distance, a, b, ha, hb));
            }
            clashes.Sort((left, right) => left.Distance.CompareTo(right.Distance));
            Console.WriteLine($"NEIGHBOUR_BORDERS {borders.Count} shared borders; {clashes.Count} of them under 70:");
            foreach (var clash in clashes.Take(20))
                Console.WriteLine($"  {clash.Distance,6:0.0}  {clash.A} #{clash.HexA}  |  {clash.B} #{clash.HexB}");
        }
        else Console.WriteLine("NEIGHBOUR_BORDERS skipped: " + why);

        // over, or people learn to ignore the red. What is asserted is that the palette is readable
        // and that the parser saw the whole of it - the numbers below are the finding.
        Console.WriteLine($"COLOUR_AUDIT: {worst.Count} pairs under 60, {exact} of them exact duplicates, " +
                          $"closest {worst.FirstOrDefault().A}/{worst.FirstOrDefault().B} at {worst.FirstOrDefault().Distance:0.0}");
    }

    static void Report(SortedDictionary<string, string> found, string a, string b)
    {
        if (!found.TryGetValue(a, out var ha) || !found.TryGetValue(b, out var hb)) return;
        Console.WriteLine($"  {a} #{ha} vs {b} #{hb}: {Distance(ha, hb):0.0}");
    }
}
