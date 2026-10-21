using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One published observation of a region's people: a date, a source, and the head-counts a
/// census or survey actually recorded.
///
/// Absolute head-counts, not percentages. Averaging shares would let a district of two
/// thousand people outvote a province of two million, and the whole point of citing a source
/// is that the numbers are the ones that were published.
/// </summary>
public sealed class DemographicReference
{
    public string RegionId = "";
    public DateOnly Date;
    public string Source = "";
    public string Url = "";
    public string Table = "";
    public long Population;
    public long Mapped;
    public long Unmapped;
    public long Unlisted => Math.Max(0L, Population - Mapped - Unmapped);
    public float Coverage => Population <= 0 ? 0f : (float)(Mapped / (double)Population);
    public IReadOnlyList<DemographicArea> Areas = Array.Empty<DemographicArea>();
    public IReadOnlyList<(string Culture, float Share)> CultureMix = Array.Empty<(string, float)>();

    internal DemographicReference(string regionId, DateOnly date, string source, string url,
        string table, List<DemographicArea> areas)
    {
        RegionId = regionId; Date = date; Source = source; Url = url; Table = table;
        Areas = areas.AsReadOnly();
        var counts = new SortedDictionary<string, long>(StringComparer.Ordinal);
        foreach (var area in areas)
        {
            Population = checked(Population + area.Population);
            Unmapped = checked(Unmapped + area.Unmapped);
            foreach (var (culture, people) in area.Counts)
                counts[culture] = checked(counts.GetValueOrDefault(culture) + people);
        }
        Mapped = counts.Values.Aggregate(0L, (sum, people) => checked(sum + people));
        // Add absolute head-counts first. Averaging percentages would overweight a small area.
        CultureMix = counts.Select(pair => (pair.Key, (float)(pair.Value / (double)Mapped)))
            .ToList().AsReadOnly();
    }
}

/// <summary>
/// Source records in common/regional_demography. History may explicitly select one through
/// culture_profile; old saves are not reseeded. Unlisted and unmapped responses stay visible
/// in the reference but do not become an invented culture, religion or political grievance.
/// </summary>
public static class RegionalDemography
{
    private static readonly Dictionary<string, DemographicReference> References = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    /// <summary>
    /// Read the catalogue the first time anything asks for it, and never from a static
    /// constructor.
    ///
    /// A static constructor runs the moment any member of this type is touched, which for a
    /// catalogue this size was in the middle of seeding a world's economy - the point where
    /// the process had the most already allocated - and it took the whole suite down with an
    /// out-of-memory. Loading on first use puts it where every other catalogue loads, and
    /// where a caller can see it happen.
    /// </summary>
    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _loaded = true;
        ResetCatalog();
    }

    public static DemographicReference Find(string regionId)
    {
        EnsureLoaded();
        return regionId is not null && References.TryGetValue(regionId, out var reference) ? reference : null;
    }

    /// <summary>Split regions inherit a reference at the parent's scale, not a new local census.</summary>
    public static DemographicReference ReferenceFor(Campaign campaign, ScenarioState state)
    {
        if (state is null) return null;
        var direct = Find(state.Id);
        if (direct is not null || campaign is null || state.ParentMapIndex == 0) return direct;
        return Find(campaign.States.FirstOrDefault(parent => parent.MapIndex == state.ParentMapIndex)?.Id);
    }

    public static void ResetCatalog()
    {
        _loaded = true;
        References.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "regional_demography"))
            using (document) Merge(document.RootElement, null);
    }

    /// <summary>Each complete record replaces that id atomically; a bad overlay preserves the old record.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        { errors?.Add("regional_demography: expected an object of region records."); return; }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            try { References[entry.Name] = Read(entry.Name, entry.Value); }
            catch (Exception exception) when (exception is FormatException or OverflowException or InvalidOperationException)
            { errors?.Add($"regional_demography '{entry.Name}': {exception.Message}"); }
        }
    }

    private static DemographicReference Read(string id, JsonElement value)
    {
        var date = DateOnly.ParseExact(Text(value, "date"), "yyyy-MM-dd", CultureInfo.InvariantCulture);
        var source = Text(value, "source");
        var url = Text(value, "url");
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || (uri.Scheme != "https" && uri.Scheme != "http"))
            throw new FormatException("url must be an absolute HTTP(S) source link.");
        var table = Text(value, "table");
        if (!value.TryGetProperty("areas", out var array) || array.ValueKind != JsonValueKind.Array ||
            array.GetArrayLength() is < 1 or > 128) throw new FormatException("areas must contain 1..128 observations.");
        var areas = new List<DemographicArea>();
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var area in array.EnumerateArray())
        {
            var name = Text(area, "name");
            if (!names.Add(name)) throw new FormatException("duplicate observation area: " + name);
            var page = Number(area, "page");
            if (page < 0) throw new FormatException("page must not be negative.");
            var population = Long(area, "population");
            if (population < 0) throw new FormatException("population must not be negative.");
            var unmapped = Long(area, "unmapped");
            if (unmapped < 0 || unmapped > population) throw new FormatException("unmapped must be between zero and the area's population.");
            var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
            if (area.TryGetProperty("counts", out var observed) && observed.ValueKind == JsonValueKind.Object)
            {
                foreach (var entry in observed.EnumerateObject())
                {
                    if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (DemographyCatalog.Culture(entry.Name) is null)
                        throw new FormatException($"unknown culture '{entry.Name}'.");
                    var people = Count(entry.Value, entry.Name);
                    if (people < 0) throw new FormatException($"count '{entry.Name}' must not be negative.");
                    counts[entry.Name] = people;
                }
            }
            var total = counts.Values.Sum();
            if (total + unmapped > population) throw new FormatException("counted and unmapped people exceed the area's population.");
            areas.Add(new DemographicArea(name, page, population, unmapped, counts));
        }
        if (areas.Count == 0) throw new FormatException("at least one observation area is required.");
        return new DemographicReference(id, date, source, url, table, areas);
    }

    private static string Text(JsonElement value, string property) =>
        value.TryGetProperty(property, out var text) && text.ValueKind == JsonValueKind.String
            ? (text.GetString() ?? "").Trim() : "";

    private static long Long(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var number)) return 0;
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetInt64(out var count) || count < 0)
            throw new FormatException($"{property} must be a non-negative whole number.");
        return count;
    }

    private static long Count(JsonElement value, string name)
    {
        if (value.ValueKind != JsonValueKind.Number || !value.TryGetInt64(out var count) || count < 0)
            throw new FormatException($"count '{name}' must be a non-negative whole number.");
        return count;
    }

    private static float Number(JsonElement value, string property)
    {
        if (!value.TryGetProperty(property, out var number)) return 0f;
        if (number.ValueKind != JsonValueKind.Number || !number.TryGetSingle(out var page) || page < 0f || !float.IsFinite(page))
            throw new FormatException($"{property} must be a non-negative number.");
        return page;
    }
}

/// <summary>One area a source observed: a province, a district, a survey stratum.</summary>
public sealed record DemographicArea(string Name, float Page, long Population, long Unmapped,
    IReadOnlyDictionary<string, long> Counts);
