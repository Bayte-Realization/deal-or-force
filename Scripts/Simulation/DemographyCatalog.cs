using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>A people, and what most of them believe.</summary>
public sealed class CultureDefinition
{
    public string Id = "";
    public string Name = "";
    public string Group = "";
    public string Colour = "#8a8f98";

    /// <summary>
    /// What this people believes, as weights. One entry is the usual case; two is a people
    /// genuinely split down the middle, which is a fact about Yoruba and Albanians and
    /// Lebanese and pretending otherwise would lose the most interesting thing about them.
    /// </summary>
    public readonly Dictionary<string, float> Religions = new(StringComparer.OrdinalIgnoreCase);
}

public sealed class ReligionDefinition
{
    public string Id = "";
    public string Name = "";
    public string Group = "";
    public string Colour = "#8a8f98";
}

/// <summary>
/// What people do for a living.
///
/// A profession is the thing the population screen is actually about, and it is also the
/// bridge to the economy: every profession names the social class whose wages, wealth and
/// politics it shares, so adding a profession does not mean rewriting the market.
///
/// The two shares are the same profession in two different countries. A farming country is
/// mostly peasants; an industrial one is mostly wage labour and clerks. Rather than author
/// a profile per country, each profession says what it is worth in the countryside and what
/// it is worth in a city, and every region blends the two by how urban it actually is.
/// </summary>
public sealed class ProfessionDefinition
{
    public string Id = "";
    public string Name = "";
    public string Colour = "#8a8f98";
    public SocialClass Class = SocialClass.Workers;
    public float RuralShare;
    public float UrbanShare;
    public float Wealth = 0.4f;
    public float Employment = 0.9f;

    /// <summary>
    /// How fast the countryside half of a trade fades as towns grow, and how fast the city
    /// half arrives.
    ///
    /// One each means both move in a straight line, which is wrong for the one trade it
    /// matters most for. Work on the land does not decline evenly with urbanisation; it
    /// collapses - a country that is three quarters urban has almost nobody left farming,
    /// while one that is a quarter urban still has half its people in the fields. Two
    /// exponents fit that curve where a straight line put a fifth of Germany behind a
    /// plough, and they cost one number each in the data.
    /// </summary>
    public float RuralFalloff = 1f;

    public float UrbanRise = 1f;

    /// <summary>How much of the workforce this is, in a region of the given urbanisation.</summary>
    public float ShareAt(float urbanisation)
    {
        var urban = M.Clamp01(urbanisation);
        return Math.Max(0f,
            RuralShare * MathF.Pow(1f - urban, Math.Max(0.05f, RuralFalloff)) +
            UrbanShare * MathF.Pow(urban, Math.Max(0.05f, UrbanRise)));
    }
}

/// <summary>One people's presence in a country, and where in it they live.</summary>
public sealed class CultureShare
{
    public string Culture = "";
    public float Share;

    /// <summary>
    /// The regions this people actually lives in, by ISO code.
    ///
    /// Without it a minority is smeared evenly over the whole country, which is how a game
    /// ends up with Tibetans in Shanghai and Chechens in Kaliningrad. Naming the regions
    /// costs one line and puts the whole national head-count where it belongs, which is
    /// also what makes the map worth looking at: minorities are somewhere.
    ///
    /// Empty means everywhere - the right answer for a majority, and for the genuinely
    /// dispersed.
    /// </summary>
    public readonly List<string> Where = new();
}

/// <summary>
/// Who lives where, and what they do.
///
/// The three catalogues - peoples, faiths, trades - say what may exist. `history/pops` says
/// what was true in 2024: a composition per country, and an override per region for the
/// places where the country's own average is a lie. Everything here is data; the simulation
/// reads it and has no opinion of its own about who anybody is.
/// </summary>
public static class DemographyCatalog
{
    private static readonly Dictionary<string, CultureDefinition> CultureById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ReligionDefinition> ReligionById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ProfessionDefinition> ProfessionList = new();

    private static readonly Dictionary<string, List<CultureShare>> ByCountry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<CultureShare>> ByRegion = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> PrimaryByCountry = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, CultureDefinition> Cultures => CultureById;
    public static IReadOnlyDictionary<string, ReligionDefinition> Religions => ReligionById;
    public static IReadOnlyList<ProfessionDefinition> Professions => ProfessionList;
    public static int CountryCount => ByCountry.Count;
    public static int RegionOverrideCount => ByRegion.Count;

    public static CultureDefinition Culture(string id) =>
        id is not null && CultureById.TryGetValue(id, out var found) ? found : null;

    public static ReligionDefinition Religion(string id) =>
        id is not null && ReligionById.TryGetValue(id, out var found) ? found : null;

    public static string CultureName(string id) => Culture(id)?.Name ?? Titled(id);
    public static string ReligionName(string id) => Religion(id)?.Name ?? Titled(id);

    public static ProfessionDefinition Profession(string id) =>
        ProfessionList.FirstOrDefault(entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));

    public static string ProfessionName(string id) => Profession(id)?.Name ?? Titled(id);

    /// <summary>Whether a people is one of the state's own, which is what makes the rest minorities.</summary>
    public static bool IsPrimary(string countryTag, string culture) =>
        countryTag is not null && culture is not null &&
        PrimaryByCountry.TryGetValue(countryTag, out var primary) &&
        primary.Contains(culture, StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<string> PrimaryCultures(string countryTag) =>
        countryTag is not null && PrimaryByCountry.TryGetValue(countryTag, out var primary)
            ? primary
            : Array.Empty<string>();

    static DemographyCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        CultureById.Clear();
        ReligionById.Clear();
        ProfessionList.Clear();
        ByCountry.Clear();
        ByRegion.Clear();
        PrimaryByCountry.Clear();

        foreach (var document in GameDataLoader.DataDocuments("common", "religions"))
            using (document) MergeReligions(document.RootElement, null);
        foreach (var document in GameDataLoader.DataDocuments("common", "cultures"))
            using (document) MergeCultures(document.RootElement, null);
        foreach (var document in GameDataLoader.DataDocuments("common", "professions"))
            using (document) MergeProfessions(document.RootElement, null);
        foreach (var document in GameDataLoader.DataDocuments("history", "pops"))
            using (document) MergePops(document.RootElement, null);
    }

    // ------------------------------------------------------------------ reading the data

    public static void LoadReligions(string json, List<string> errors) => Read(json, errors, MergeReligions);
    public static void LoadCultures(string json, List<string> errors) => Read(json, errors, MergeCultures);
    public static void LoadProfessions(string json, List<string> errors) => Read(json, errors, MergeProfessions);
    public static void LoadPops(string json, List<string> errors) => Read(json, errors, MergePops);

    private static void Read(string json, List<string> errors, Action<JsonElement, List<string>> merge)
    {
        try
        {
            using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
            merge(document.RootElement, errors);
        }
        catch (Exception exception) { errors?.Add($"demography: {exception.Message}"); }
    }

    private static void MergeReligions(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;
            var religion = ReligionById.TryGetValue(entry.Name, out var existing)
                ? existing
                : ReligionById[entry.Name] = new ReligionDefinition { Id = entry.Name, Name = Titled(entry.Name) };
            if (entry.Value.TryGetProperty("name", out var name)) religion.Name = name.GetString() ?? religion.Name;
            if (entry.Value.TryGetProperty("group", out var group)) religion.Group = group.GetString() ?? "";
            if (entry.Value.TryGetProperty("colour", out var colour)) religion.Colour = colour.GetString() ?? religion.Colour;
        }
    }

    private static void MergeCultures(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;
            var culture = CultureById.TryGetValue(entry.Name, out var existing)
                ? existing
                : CultureById[entry.Name] = new CultureDefinition { Id = entry.Name, Name = Titled(entry.Name) };
            if (entry.Value.TryGetProperty("name", out var name)) culture.Name = name.GetString() ?? culture.Name;
            if (entry.Value.TryGetProperty("group", out var group)) culture.Group = group.GetString() ?? "";
            if (entry.Value.TryGetProperty("colour", out var colour)) culture.Colour = colour.GetString() ?? culture.Colour;

            // "religion": "orthodox", or a split: { "christian": 0.55, "sunni": 0.45 }
            if (!entry.Value.TryGetProperty("religion", out var faith)) continue;
            culture.Religions.Clear();
            if (faith.ValueKind == JsonValueKind.String) culture.Religions[faith.GetString() ?? ""] = 1f;
            else if (faith.ValueKind == JsonValueKind.Object)
                foreach (var share in faith.EnumerateObject())
                    if (share.Value.TryGetSingle(out var weight) && weight > 0f)
                        culture.Religions[share.Name] = weight;
        }
    }

    private static void MergeProfessions(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
                entry.Value.ValueKind != JsonValueKind.Object) continue;
            var profession = ProfessionList.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (profession is null)
            {
                profession = new ProfessionDefinition { Id = entry.Name, Name = Titled(entry.Name) };
                ProfessionList.Add(profession);
            }
            var value = entry.Value;
            if (value.TryGetProperty("name", out var name)) profession.Name = name.GetString() ?? profession.Name;
            if (value.TryGetProperty("colour", out var colour)) profession.Colour = colour.GetString() ?? profession.Colour;
            if (value.TryGetProperty("class", out var social) &&
                Enum.TryParse<SocialClass>(social.GetString(), true, out var parsed)) profession.Class = parsed;
            if (value.TryGetProperty("rural_share", out var rural) && rural.TryGetSingle(out var ruralShare))
                profession.RuralShare = Math.Max(0f, ruralShare);
            if (value.TryGetProperty("urban_share", out var urban) && urban.TryGetSingle(out var urbanShare))
                profession.UrbanShare = Math.Max(0f, urbanShare);
            if (value.TryGetProperty("wealth", out var wealth) && wealth.TryGetSingle(out var wealthValue))
                profession.Wealth = M.Clamp01(wealthValue);
            if (value.TryGetProperty("employment", out var employed) && employed.TryGetSingle(out var employment))
                profession.Employment = M.Clamp01(employment);
            if (value.TryGetProperty("rural_falloff", out var falloff) &&
                falloff.TryGetSingle(out var fade) && fade > 0f) profession.RuralFalloff = fade;
            if (value.TryGetProperty("urban_rise", out var rise) &&
                rise.TryGetSingle(out var climb) && climb > 0f) profession.UrbanRise = climb;
        }
    }

    private static void MergePops(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("countries", out var countries) && countries.ValueKind == JsonValueKind.Object)
            foreach (var entry in countries.EnumerateObject())
                ReadComposition(entry, ByCountry, errors, true);
        if (root.TryGetProperty("regions", out var regions) && regions.ValueKind == JsonValueKind.Object)
            foreach (var entry in regions.EnumerateObject())
                ReadComposition(entry, ByRegion, errors, false);
    }

    private static void ReadComposition(JsonProperty entry, Dictionary<string, List<CultureShare>> into,
        List<string> errors, bool country)
    {
        if (entry.Name.StartsWith("//", StringComparison.Ordinal) ||
            entry.Value.ValueKind != JsonValueKind.Object) return;
        if (!entry.Value.TryGetProperty("cultures", out var cultures) ||
            cultures.ValueKind != JsonValueKind.Object) return;

        var shares = new List<CultureShare>();
        foreach (var culture in cultures.EnumerateObject())
        {
            if (culture.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            var share = new CultureShare { Culture = culture.Name };
            if (culture.Value.ValueKind == JsonValueKind.Number)
                share.Share = culture.Value.GetSingle();
            else if (culture.Value.ValueKind == JsonValueKind.Object)
            {
                if (culture.Value.TryGetProperty("share", out var value) && value.TryGetSingle(out var parsed))
                    share.Share = parsed;
                if (culture.Value.TryGetProperty("where", out var where) && where.ValueKind == JsonValueKind.Array)
                    foreach (var region in where.EnumerateArray())
                        if (region.ValueKind == JsonValueKind.String) share.Where.Add(region.GetString() ?? "");
            }
            if (share.Share > 0f) shares.Add(share);
        }
        if (shares.Count == 0)
        {
            errors?.Add($"pops: {entry.Name} lists no culture with a share");
            return;
        }
        into[entry.Name] = shares;

        if (!country) return;
        var primary = new List<string>();
        if (entry.Value.TryGetProperty("primary_cultures", out var listed) && listed.ValueKind == JsonValueKind.Array)
            foreach (var value in listed.EnumerateArray())
                if (value.ValueKind == JsonValueKind.String) primary.Add(value.GetString() ?? "");
        // Unsaid, the state's people are whoever most of it is. That is right far more often
        // than it is wrong, and where it is wrong the country says so in one line.
        if (primary.Count == 0) primary.Add(shares.OrderByDescending(share => share.Share).First().Culture);
        PrimaryByCountry[entry.Name] = primary;
    }

    // ------------------------------------------------------------------ using it

    /// <summary>
    /// Who lives in each of one country's regions.
    ///
    /// Done for the whole country at once, because it cannot be done a region at a time: a
    /// people who named the regions they live in have a national head-count to place, and
    /// placing it means knowing how big those regions are relative to each other.
    ///
    /// A region that states its own composition is taken at its word and left out of the
    /// arithmetic. Everywhere else: the concentrated peoples are dropped into the regions
    /// that hold them in proportion to size, and the dispersed ones share out whatever room
    /// is left. A country with no entry at all gets its own name as its one people, which is
    /// wrong in detail and right in shape, and says so plainly in the population screen.
    /// </summary>
    public static IReadOnlyList<(string Culture, float Share)>[] AllocateCultures(
        string countryTag, IReadOnlyList<ScenarioState> states, IReadOnlyList<long> populations)
    {
        var result = new IReadOnlyList<(string, float)>[states.Count];
        if (!ByCountry.TryGetValue(countryTag ?? "", out var country) || country.Count == 0)
        {
            var fallback = new[] { (countryTag?.ToLowerInvariant() ?? "unknown", 1f) };
            for (var index = 0; index < states.Count; index++)
                result[index] = ByRegion.TryGetValue(states[index].Iso ?? "", out var authored)
                    ? Normalise(authored.Select(share => (share.Culture, share.Share)))
                    : fallback;
            return result;
        }

        // Regions that speak for themselves are settled first and take no further part.
        var open = new List<int>();
        for (var index = 0; index < states.Count; index++)
        {
            if (ByRegion.TryGetValue(states[index].Iso ?? "", out var authored))
                result[index] = Normalise(authored.Select(share => (share.Culture, share.Share)));
            else open.Add(index);
        }
        if (open.Count == 0) return result;

        double roomTotal = open.Sum(index => (double)Math.Max(0, populations[index]));
        var placed = new Dictionary<int, Dictionary<string, double>>();
        foreach (var index in open) placed[index] = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        var spoken = new double[states.Count];

        foreach (var share in country.Where(entry => entry.Where.Count > 0))
        {
            var homes = open.Where(index =>
                share.Where.Contains(states[index].Iso ?? "", StringComparer.OrdinalIgnoreCase)).ToList();
            if (homes.Count == 0) continue;
            var people = share.Share * roomTotal;
            double homeTotal = homes.Sum(index => (double)Math.Max(1, populations[index]));
            foreach (var index in homes)
            {
                // Never more than the region holds: a people concentrated into one small
                // region cannot outnumber it, and the overflow simply does not fit.
                var portion = people * Math.Max(1, populations[index]) / homeTotal;
                var room = Math.Max(0, populations[index]) - spoken[index];
                var taken = Math.Min(portion, room * 0.97);
                if (taken <= 0) continue;
                placed[index][share.Culture] = placed[index].GetValueOrDefault(share.Culture) + taken;
                spoken[index] += taken;
            }
        }

        var dispersed = country.Where(entry => entry.Where.Count == 0).ToList();
        var dispersedTotal = dispersed.Sum(entry => entry.Share);
        foreach (var index in open)
        {
            var room = Math.Max(0, populations[index]) - spoken[index];
            if (room > 0 && dispersedTotal > 0)
                foreach (var share in dispersed)
                    placed[index][share.Culture] =
                        placed[index].GetValueOrDefault(share.Culture) + room * share.Share / dispersedTotal;
            result[index] = placed[index].Count > 0
                ? Normalise(placed[index].Select(entry => (entry.Key, (float)entry.Value)))
                : Normalise(country.Select(entry => (entry.Culture, entry.Share)));
        }
        return result;
    }

    private static IReadOnlyList<(string Culture, float Share)> Normalise(
        IEnumerable<(string Culture, float Share)> shares)
    {
        var list = shares.Where(entry => entry.Share > 0f)
            .OrderByDescending(entry => entry.Share)
            .ThenBy(entry => entry.Culture, StringComparer.Ordinal)
            .ToList();
        var total = list.Sum(entry => entry.Share);
        if (total <= 0f) return Array.Empty<(string, float)>();
        for (var index = 0; index < list.Count; index++)
            list[index] = (list[index].Culture, list[index].Share / total);
        return list;
    }

    /// <summary>
    /// The faith of one people, as at most two entries adding to their whole number.
    ///
    /// Two, not more: a people split three ways is still one people, and a region carrying
    /// forty groups instead of a dozen is a region nobody can read and a simulation nobody
    /// can afford.
    /// </summary>
    public static IReadOnlyList<(string Religion, float Share)> FaithOf(string cultureId)
    {
        var culture = Culture(cultureId);
        if (culture is null || culture.Religions.Count == 0) return new[] { ("irreligion", 1f) };
        var top = culture.Religions
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        var kept = top.Sum(entry => entry.Value);
        if (kept <= 0f) return new[] { ("irreligion", 1f) };
        return top.Select(entry => (entry.Key, entry.Value / kept)).ToArray();
    }

    private static string Titled(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var words = id.Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
    }
}
