using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One land a state can be named for: a historical region, a borderland, a whole side of a
/// continent. It exists because the direction of a compass is the weakest thing a founding
/// knows. A republic proclaimed in Donetsk is the Donetsk Republic whether it lies west,
/// south or east of Moscow; calling it "South Russia" is not a name, it is a bearing.
/// </summary>
public sealed class LandDefinition
{
    public string Id = "";

    /// <summary>The land's own name in English, used when no translation row exists.</summary>
    public string Name = "";
    public string Adjective = "";

    /// <summary>Exact region ids, when the editor wants to be certain.</summary>
    public readonly HashSet<string> RegionIds = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Region names as `common/states` spells them, for a land nobody listed by id.</summary>
    public readonly HashSet<string> RegionNames = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Countries the land is in, by tag. Empty means anywhere its shape says.</summary>
    public readonly HashSet<string> Countries = new(StringComparer.OrdinalIgnoreCase);

    public bool HasBox;
    public float LatMin = 90f, LatMax = -90f, LonMin = 180f, LonMax = -180f;

    /// <summary>A land this broad is only used when it holds most of the ground leaving.</summary>
    public bool Macro;

    public int Order;
}

/// <summary>
/// Where a piece of ground is, said in a way that can go into a country's name.
///
/// The naming composer asks three questions about a breakaway: whose people it is, what the
/// land is called, and what used to stand there. The second question had no table before
/// this - "Kurdistan", "Transnistria" and "Somaliland" were known only because a *people*
/// claimed the whole of them, and a state founded on Donbas, Crimea, Silesia or Sindh was
/// left with a compass bearing and its parent's name. This is that table.
///
/// A land declares any of three ways to recognise it, strongest first: region ids, region
/// names with an optional country filter, and a latitude/longitude box. The box is what
/// makes the catalogue practical - it does not need a hundred region ids to be right, and a
/// scenario that redraws a border still puts its capital in the same half of the world.
/// </summary>
public static class LandAtlas
{
    private static List<LandDefinition> _lands;

    public static IReadOnlyList<LandDefinition> All => Lands;

    private static List<LandDefinition> Lands => _lands ??= Load();

    /// <summary>Read the catalogue again after a mod changed it.</summary>
    public static void ResetCatalog() => _lands = null;

    public static LandDefinition Find(string id) =>
        string.IsNullOrWhiteSpace(id)
            ? null
            : Lands.FirstOrDefault(land => string.Equals(land.Id, id, StringComparison.OrdinalIgnoreCase));

    /// <summary>The land's name as a reader should see it: "Donbas", "Kurdistan".</summary>
    public static string Name(LandDefinition land) => Text(land, "");
    /// <summary>The land's adjective: "Donetsk", "Crimean", "Kurdish".</summary>
    public static string Adjective(LandDefinition land) => Text(land, ".adj");

    private static string Text(LandDefinition land, string suffix)
    {
        if (land is null) return "";
        var key = "name.land." + land.Id + suffix;
        if (Loc.Has(key)) return Loc.Get(key);
        if (suffix.Length > 0 && !string.IsNullOrWhiteSpace(land.Adjective)) return Loc.Get(land.Adjective);
        return Loc.GetOr(land.Name, land.Name);
    }

    /// <summary>
    /// Which land this ground is, or null when it is not one the catalogue names.
    ///
    /// The score is the share of the leaving population inside the land, so a breakaway that
    /// takes Donbas and three Russian oblasts is still the Donbas one, and a secession that
    /// takes a whole third of a continent is named for that third rather than for its largest
    /// province. A land must hold a real share of the ground to name it: half for a land the
    /// catalogue lists precisely, more for a macro-region, which would otherwise be right by
    /// accident about every small revolt inside it.
    /// </summary>
    /// <summary>The share of these regions' people living in a land, 0 to 1.</summary>
    private sealed class CentresCache
    {
        public ProvinceTopology Topology;
        public int Revision = -1;
        public (float Latitude, float Longitude)[] Centres = Array.Empty<(float, float)>();
    }
    private static readonly ConditionalWeakTable<Campaign, CentresCache> CentresByCampaign = new();

    [ThreadStatic] private static HashSet<int> _seenRegionScratch;
    [ThreadStatic] private static List<ScenarioState> _statesScratch;

    public static float Share(Campaign campaign, LandDefinition land, IReadOnlyCollection<int> regions)
    {
        if (campaign is null || land is null || regions is null || regions.Count == 0) return 0f;
        var centres = CentresOf(campaign);
        var seen = regions.Count > 1 ? (_seenRegionScratch ??= new HashSet<int>()) : null;
        seen?.Clear();
        long total = 0, covered = 0;
        foreach (var regionId in regions)
        {
            if (seen is not null && !seen.Add(regionId)) continue;
            var state = campaign.StateByMapIndex(regionId);
            if (state is null) continue;
            var people = Math.Max(0L, state.Population);
            total += people;
            var centre = (uint)state.MapIndex < (uint)centres.Length ? centres[state.MapIndex] : (0f, 0f);
            if (Matches(land, state, centre, true, out _)) covered += people;
        }
        seen?.Clear();
        return total <= 0 ? 0f : covered / (float)total;
    }

    public static LandDefinition BestFor(Campaign campaign, IReadOnlyCollection<int> regions, string parentTag = "")
    {
        if (campaign is null || regions is null || regions.Count == 0) return null;
        var seen = regions.Count > 1 ? (_seenRegionScratch ??= new HashSet<int>()) : null;
        seen?.Clear();
        var states = _statesScratch ??= new List<ScenarioState>();
        states.Clear();
        long total = 0;
        foreach (var regionId in regions)
        {
            if (seen is not null && !seen.Add(regionId)) continue;
            var state = campaign.StateByMapIndex(regionId);
            if (state is null) continue;
            states.Add(state);
            total += Math.Max(0, state.Population);
        }
        seen?.Clear();
        if (states.Count == 0) return null;
        if (total <= 0) total = states.Count;

        var centres = CentresOf(campaign);

        // How much of the parent a macro-region has to hold before it may name a state.
        //
        // The share below is measured over the ground that is leaving, so a single province
        // inside a land that spans five countries counts as fully covered - one Moroccan
        // region is 100% of the Maghreb by that arithmetic, and the country it founded was
        // called the Kingdom of the Maghreb. A macro-region is a side of a continent; it may
        // only name a state that really is a large part of it, which means a large part of
        // what it is leaving as well.
        var parent = string.IsNullOrEmpty(parentTag) ? null : campaign.NationByTag(parentTag);
        long parentPeople = 0;
        if (parent is not null)
        {
            var parentStates = campaign.StatesOf(parent);
            for (var i = 0; i < parentStates.Length; i++) parentPeople += Math.Max(0L, parentStates[i].Population);
        }

        LandDefinition best = null;
        var bestScore = 0f;
        foreach (var land in Lands)
        {
            var countryMatchesParent = land.Countries.Count == 0 || string.IsNullOrEmpty(parentTag) ||
                land.Countries.Contains(parentTag);
            long covered = 0;
            var precise = false;
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                var centre = (uint)state.MapIndex < (uint)centres.Length ? centres[state.MapIndex] : (0f, 0f);
                if (!Matches(land, state, centre, countryMatchesParent, out var exact)) continue;
                covered += Math.Max(0, state.Population);
                precise |= exact;
            }
            if (covered <= 0) continue;
            var share = covered / (float)total;
            if (share < (land.Macro ? 0.55f : 0.45f)) continue;

            // A macro-region that spans multiple sovereign countries (like the Maghreb or the Balkans)
            // cannot be claimed by a single discontented province. But an internal macro-region within
            // a country (like Siberia, Urals, Far East, Volga, Texas, Dixie, Manchuria, Sichuan)
            // does not need 60% of the entire parent empire to bear its authentic regional name.
            if (land.Macro)
            {
                var isTransnational = land.Countries.Count > 1;
                if (isTransnational)
                {
                    if (states.Count < 2 && total < 10_000_000L) continue;
                    if (parentPeople > 0 && covered < parentPeople * 0.40d && states.Count < 3) continue;
                }
                else
                {
                    if (states.Count < 2 && land.RegionIds.Count != 1) continue;
                }
            }

            // Prefer the narrower land when both cover the ground, and a listed region over a
            // shape that happens to contain it.
            var score = share + (precise ? 0.35f : 0f) + (land.Macro ? 0f : 0.15f);
            if (score < bestScore) continue;
            best = land;
            bestScore = score;
        }
        states.Clear();
        return best;
    }

    private static bool Matches(LandDefinition land, ScenarioState state,
        (float Latitude, float Longitude) centre, bool countryMatchesParent, out bool exact)
    {
        exact = false;
        if (!string.IsNullOrWhiteSpace(state.Id) && land.RegionIds.Contains(state.Id))
        {
            // Even an exact region match names a state after the land only when the land's
            // own countries agree the ground is their kind. Without this, a shard of a
            // collapsed great power that happened to sit on one foreign province inherited
            // that province's tiny land name - a Russia answering to "Transnistria".
            exact = countryMatchesParent || StateBelongsToLandCountry(land, state);
            return exact;
        }
        if (!string.IsNullOrWhiteSpace(state.Name) && land.RegionNames.Contains(state.Name))
        {
            exact = true;
            return true;
        }
        if (!land.Macro && land.RegionIds.Count > 0) return false;
        if (!countryMatchesParent && !StateBelongsToLandCountry(land, state)) return false;
        if (!land.HasBox) return false;
        if (Math.Abs(centre.Latitude) < 0.01f && Math.Abs(centre.Longitude) < 0.01f) return false;
        var latitude = centre.Latitude;
        var longitude = centre.Longitude;
        if (land.LonMin <= land.LonMax)
            return latitude >= land.LatMin && latitude <= land.LatMax &&
                longitude >= land.LonMin && longitude <= land.LonMax;
        // A box across the antimeridian: Siberia's far east and the Pacific.
        return latitude >= land.LatMin && latitude <= land.LatMax &&
            (longitude >= land.LonMin || longitude <= land.LonMax);
    }

    private static bool StateBelongsToLandCountry(LandDefinition land, ScenarioState state)
    {
        if (land.Countries.Count == 0 || state is null) return true;
        if (state.CoreTags is not null)
            foreach (var core in state.CoreTags)
                if (land.Countries.Contains(core)) return true;
        if (state.Claims is not null)
            foreach (var claim in state.Claims)
                if (land.Countries.Contains(claim)) return true;
        var id = state.Id ?? "";
        var cut = id.IndexOf('_');
        if (cut <= 0) return false;
        var tag = Iso2ToTag(id.AsSpan(0, cut));
        return tag.Length > 0 && land.Countries.Contains(tag);
    }

    public static string Iso2ToTag(string iso2) => iso2 is null ? "" : Iso2ToTag(iso2.AsSpan());

    internal static string Iso2ToTag(ReadOnlySpan<char> iso2)
    {
        if (iso2.Length != 2) return "";
        var c0 = iso2[0] is >= 'A' and <= 'Z' ? (char)(iso2[0] + 32) : iso2[0];
        var c1 = iso2[1] is >= 'A' and <= 'Z' ? (char)(iso2[1] + 32) : iso2[1];
        return (c0, c1) switch
        {
            ('u', 'a') => "UKR", ('r', 'u') => "RUS", ('b', 'y') => "BLR", ('p', 'l') => "POL", ('u', 's') => "USA",
            ('c', 'a') => "CAN", ('m', 'x') => "MEX", ('b', 'r') => "BRA", ('a', 'r') => "ARG", ('c', 'l') => "CHL",
            ('d', 'e') => "DEU", ('f', 'r') => "FRA", ('i', 't') => "ITA", ('e', 's') => "ESP", ('g', 'b') => "GBR",
            ('r', 'o') => "ROU", ('t', 'r') => "TUR", ('i', 'r') => "IRN", ('i', 'q') => "IRQ", ('s', 'y') => "SYR",
            ('s', 'a') => "SAU", ('y', 'e') => "YEM", ('e', 'g') => "EGY", ('l', 'y') => "LBY", ('d', 'z') => "DZA",
            ('m', 'a') => "MAR", ('s', 'd') => "SDN", ('e', 't') => "ETH", ('s', 'o') => "SOM", ('n', 'g') => "NGA",
            ('c', 'd') => "COD", ('a', 'o') => "AGO", ('t', 'z') => "TZA", ('c', 'm') => "CMR", ('m', 'l') => "MLI",
            ('c', 'n') => "CHN", ('i', 'n') => "IND", ('p', 'k') => "PAK", ('m', 'm') => "MMR", ('i', 'd') => "IDN",
            ('p', 'h') => "PHL", ('j', 'p') => "JPN", ('k', 'z') => "KAZ", ('u', 'z') => "UZB", ('g', 'e') => "GEO",
            ('a', 'z') => "AZE", ('a', 'm') => "ARM", ('m', 'd') => "MDA", ('f', 'i') => "FIN", ('s', 'e') => "SWE",
            ('n', 'o') => "NOR", ('g', 'r') => "GRC", ('r', 's') => "SRB", ('h', 'r') => "HRV", ('b', 'a') => "BIH",
            _ => ""
        };
    }

    internal static (float Latitude, float Longitude)[] CentresOf(Campaign campaign)
    {
        var topology = campaign?.ProvinceMap;
        if (topology is null) return Array.Empty<(float, float)>();
        var cache = CentresByCampaign.GetOrCreateValue(campaign);
        var revision = campaign.MapRevision;
        if (cache.Revision == revision && ReferenceEquals(cache.Topology, topology))
            return cache.Centres;
        lock (cache)
        {
            if (cache.Revision == revision && ReferenceEquals(cache.Topology, topology))
                return cache.Centres;
            var maxState = 0;
            var provinceToState = topology.ProvinceToState;
            var count = topology.ProvinceCount;
            for (var province = 1; province <= count; province++)
            {
                var s = provinceToState[province];
                if (s > maxState) maxState = s;
            }
            var latSum = new double[maxState + 1];
            var lonSum = new double[maxState + 1];
            var counts = new int[maxState + 1];
            for (var province = 1; province <= count; province++)
            {
                var s = provinceToState[province];
                if (s <= 0) continue;
                var centre = topology.CenterOf(province);
                latSum[s] += centre.Latitude;
                lonSum[s] += centre.Longitude;
                counts[s]++;
            }
            var centres = new (float Latitude, float Longitude)[maxState + 1];
            for (var s = 1; s <= maxState; s++)
            {
                var c = counts[s];
                if (c > 0) centres[s] = ((float)(latSum[s] / c), (float)(lonSum[s] / c));
            }
            cache.Topology = topology;
            cache.Revision = revision;
            cache.Centres = centres;
            return centres;
        }
    }

    internal static (float Latitude, float Longitude) CentreOf(Campaign campaign, int mapIndex)
    {
        if (campaign?.ProvinceMap is null || mapIndex <= 0) return (0f, 0f);
        var centres = CentresOf(campaign);
        return (uint)mapIndex < (uint)centres.Length ? centres[mapIndex] : (0f, 0f);
    }

    private static List<LandDefinition> Load()
    {
        var lands = new List<LandDefinition>();
        var byId = new Dictionary<string, LandDefinition>(StringComparer.OrdinalIgnoreCase);
        // common/naming holds every land; "lands" is still read for mods written before the two were one.
        foreach (var category in new[] { "naming", "lands" })
            foreach (var document in GameDataLoader.DataDocuments("common", category))
                using (document)
                {
                    var root = document.RootElement;
                    if (root.ValueKind != JsonValueKind.Object) continue;
                    if (root.TryGetProperty("lands", out var entries) && entries.ValueKind == JsonValueKind.Object)
                        Merge(entries, lands, byId);
                    else
                        Merge(root, lands, byId);
                }
        lands.Sort((a, b) => a.Order != b.Order ? a.Order.CompareTo(b.Order)
            : string.CompareOrdinal(a.Id, b.Id));
        return lands;
    }

    private static void Merge(JsonElement entries, List<LandDefinition> lands,
        Dictionary<string, LandDefinition> byId)
    {
        foreach (var entry in entries.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!byId.TryGetValue(entry.Name, out var land))
                byId[entry.Name] = land = new LandDefinition { Id = entry.Name, Order = lands.Count };
            // Later files win, the way every other category loads: drop the old row and put
            // this one at the end so a mod's ordering is the one that survives.
            lands.Remove(land);
            lands.Add(land);
            var source = entry.Value;
            if (source.TryGetProperty("name", out var name)) land.Name = name.GetString() ?? land.Name;
            if (source.TryGetProperty("adjective", out var adjective))
                land.Adjective = adjective.GetString() ?? land.Adjective;
            if (source.TryGetProperty("macro", out var macro))
                land.Macro = macro.ValueKind == JsonValueKind.True;
            ReadStrings(source, "regions", land.RegionIds);
            ReadStrings(source, "region_ids", land.RegionIds);
            ReadStrings(source, "names", land.RegionNames);
            ReadStrings(source, "region_names", land.RegionNames);
            ReadStrings(source, "countries", land.Countries);
            if (source.TryGetProperty("bbox", out var box) && box.ValueKind == JsonValueKind.Array &&
                box.GetArrayLength() >= 4)
            {
                var values = box.EnumerateArray()
                    .Select(value => value.TryGetSingle(out var single) ? single : 0f).ToArray();
                land.HasBox = true;
                land.LatMin = Math.Min(values[0], values[1]);
                land.LatMax = Math.Max(values[0], values[1]);
                land.LonMin = values[2];
                land.LonMax = values[3];
            }
            else if (source.TryGetProperty("box", out var boxObj) && boxObj.ValueKind == JsonValueKind.Object)
            {
                if (boxObj.TryGetProperty("lat_min", out var latMin) && latMin.TryGetSingle(out var l1)) land.LatMin = l1;
                if (boxObj.TryGetProperty("lat_max", out var latMax) && latMax.TryGetSingle(out var l2)) land.LatMax = l2;
                if (boxObj.TryGetProperty("lon_min", out var lonMin) && lonMin.TryGetSingle(out var o1)) land.LonMin = o1;
                if (boxObj.TryGetProperty("lon_max", out var lonMax) && lonMax.TryGetSingle(out var o2)) land.LonMax = o2;
                land.HasBox = true;
            }
        }
    }

    private static void ReadStrings(JsonElement source, string property, ISet<string> into)
    {
        if (!source.TryGetProperty(property, out var values)) return;
        if (values.ValueKind == JsonValueKind.String)
        {
            var text = values.GetString();
            if (!string.IsNullOrWhiteSpace(text)) into.Add(text);
            return;
        }
        if (values.ValueKind != JsonValueKind.Array) return;
        foreach (var value in values.EnumerateArray())
            if (value.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(value.GetString()))
                into.Add(value.GetString());
    }
}
