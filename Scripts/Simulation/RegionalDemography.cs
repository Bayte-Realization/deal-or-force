using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>A dated observation, not a second, live population model.</summary>
public sealed record DemographicArea(string Name, int Page, long Population,
    IReadOnlyDictionary<string, long> Counts, long Unmapped);

public sealed class DemographicReference
{
    public string RegionId { get; }
    public DateOnly Date { get; }
    public string Source { get; }
    public string Url { get; }
    public string Table { get; }
    public IReadOnlyList<DemographicArea> Areas { get; }
    public long Population { get; }
    public long Mapped { get; }
    public long Unmapped { get; }
    public long Unlisted => Population - Mapped - Unmapped;
    public double Coverage => Mapped / (double)Population;
    public IReadOnlyList<(string Culture, float Share)> CultureMix { get; }

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
    private static readonly Dictionary<string, DemographicReference> References = new(StringComparer.Ordinal);
    static RegionalDemography() => ResetCatalog();

    public static DemographicReference Find(string regionId) =>
        regionId is not null && References.TryGetValue(regionId, out var reference) ? reference : null;

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
            if (page is < 1 or > int.MaxValue) throw new FormatException("page must be positive.");
            var population = Number(area, "population");
            if (population <= 0) throw new FormatException("population must be positive.");
            var unmapped = Number(area, "unmapped");
            if (!area.TryGetProperty("counts", out var entries) || entries.ValueKind != JsonValueKind.Object ||
                entries.EnumerateObject().Count() > 512) throw new FormatException("counts must contain at most 512 cultures.");
            var counts = new Dictionary<string, long>(StringComparer.Ordinal);
            var total = unmapped;
            foreach (var count in entries.EnumerateObject())
            {
                if (DemographyCatalog.Culture(count.Name) is null || count.Value.ValueKind != JsonValueKind.Number ||
                    !count.Value.TryGetInt64(out var people) || people < 0 || !counts.TryAdd(count.Name, people))
                    throw new FormatException("invalid or duplicate culture count: " + count.Name);
                total = checked(total + people);
            }
            if (total > population || total == unmapped) throw new FormatException("counts must be positive in sum and not exceed population.");
            areas.Add(new DemographicArea(name, (int)page, population,
                new ReadOnlyDictionary<string, long>(counts), unmapped));
        }
        return new DemographicReference(id, date, source, url, table, areas);
    }

    private static string Text(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) ||
            field.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(field.GetString()))
            throw new FormatException(key + " must be non-empty text.");
        return field.GetString();
    }

    private static long Number(JsonElement value, string key)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty(key, out var field) ||
            field.ValueKind != JsonValueKind.Number || !field.TryGetInt64(out var number) || number < 0)
            throw new FormatException(key + " must be a non-negative integer.");
        return number;
    }
}
