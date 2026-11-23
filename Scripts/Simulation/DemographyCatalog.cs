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
    /// What a state this people founds is called, when it has a name of its own: Kurdistan,
    /// Catalonia, Ichkeria. Empty for most peoples, whose new state takes the name of the
    /// region it is founded on instead.
    /// </summary>
    public string Country = "";
    /// <summary>
    /// The adjective a state of this people wears: "Ukrainian", "Kurdish", "Crimean". A name
    /// built on it reads as the country is spoken of - "the Ukrainian Republic" - rather than
    /// as a form of state with a noun pushed into it. Empty for peoples whose state name is
    /// already the only way to say it.
    /// </summary>
    public string Adjective = "";

    /// <summary>
    /// Which of the two reads better for this people. "adjective" gives "Ukrainian Republic"
    /// / "Украинская Республика"; "noun" (the default) gives "Republic of Kurdistan" /
    /// "Республика Курдистан". Data decides, because it is a fact about the name rather than
    /// about the politics.
    /// </summary>
    public string Style = "";
    /// <summary>Optional homeland region prefixes. A diaspora elsewhere uses its local seat's name.</summary>
    public readonly List<string> CountryRegions = new();

    /// <summary>
    /// What this people believes, as weights. One entry is the usual case; two is a people
    /// genuinely split down the middle, which is a fact about Yoruba and Albanians and
    /// Lebanese and pretending otherwise would lose the most interesting thing about them.
    /// </summary>
    public readonly Dictionary<string, float> Religions = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>FaithOf's answer for this people, valid while the faith catalogue is unchanged.</summary>
    internal (string Religion, float Share)[] FaithCache;
    internal int FaithCacheVersion = -1;
}

public sealed class ReligionDefinition
{
    public string Id = "";
    public string Name = "";
    public string Group = "";
    public string Colour = "#8a8f98";

    /// <summary>
    /// The family this faith belongs to: what every mechanic asking "the same faith?" compares.
    /// Empty means the faith is a family of its own - every id that existed before
    /// denominations did, so "protestant" in a save or a mod still means what it meant.
    /// A denomination (Lutheran, Zaydi, Haredi) names its family here and is what the map and
    /// the population screen show; it is never stored on a population group.
    /// </summary>
    public string Family = "";

    public string FamilyId => string.IsNullOrEmpty(Family) ? Id : Family;
    public bool IsDenomination => !string.IsNullOrEmpty(Family) &&
        !string.Equals(Family, Id, StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// What history/faiths writes for one country, region, or one people inside either: a faith
/// mix that replaces the people's own where it applies, and how each family divides into
/// denominations.
/// </summary>
public sealed class FaithSetting
{
    /// <summary>Families and their shares, normalised; null when the place does not reweight faith.</summary>
    public (string Religion, float Share)[] Faiths;

    /// <summary>Per family, denominations and their shares of it. May add to less than one:
    /// the rest is divided by the next place down the chain, and finally kept as the family.</summary>
    public readonly Dictionary<string, (string Denomination, float Share)[]> Denominations =
        new(StringComparer.OrdinalIgnoreCase);

    /// <summary>The same, for one people living there.</summary>
    public readonly Dictionary<string, FaithSetting> Peoples = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>
/// What people do for a living.
///
/// A profession is the thing the population screen is actually about, and it is also the
/// bridge to the economy: a profession does not carry its own rules, it names the two groups
/// whose rules it already shares, so adding a profession does not mean rewriting anything.
///
/// It names two things, because there are two questions and they are not the same question.
/// The CLASS is how well off these people are; the SECTOR is what they live off. A landowner
/// and a peasant share a sector and share nothing else; a machinist and a clerk share
/// neither and stand at nearly the same height. A trade that answered only one of the two
/// left the other unanswerable, which is how good farmland came to grow peasants without
/// growing the people who owned the land.
///
/// What each one decides today, precisely, so that nobody assumes more: the SECTOR decides
/// who works the fields, which sets how much a country grows and how urban its regions read.
/// The CLASS decides who turns up for work - the upper class supplies a seventh of itself to
/// the labour force and lives off what it owns - and beyond that it is what the population
/// and ledger screens count by. Neither one drives politics yet. Radicalism is a property of
/// a region that its people merely inherit, and the obvious next thing to build is the one
/// this split makes possible: a demand that belongs to a class rather than to a place.
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
    public SocialClass Class = SocialClass.Lower;
    public Sector Sector = Sector.Services;
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
/// The three catalogues - peoples, faiths, trades - say what may exist. What was true in 2024
/// is written region by region in history/states/2024/, and whose state each country is in
/// history/countries/2024/30_peoples.json. A mod may still give a whole country's mix in
/// history/pops/ - a composition per country, placed by size and by the regions a people names -
/// for the regions it does not write one by one. Everything here is data; the simulation reads
/// it and has no opinion of its own about who anybody is.
/// </summary>
public static class DemographyCatalog
{
    private static readonly Dictionary<string, CultureDefinition> CultureById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, ReligionDefinition> ReligionById = new(StringComparer.OrdinalIgnoreCase);
    private static readonly List<ProfessionDefinition> ProfessionList = new();

    private static readonly Dictionary<string, List<CultureShare>> ByCountry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<CultureShare>> ByRegion = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> PrimaryByCountry = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The same peoples again, as a set, and the first of them.
    ///
    /// Kept beside the list because "is this people one of the state's own" is asked once
    /// per population group per region per week - about three million times a simulated
    /// year - and asking it of a list through LINQ allocated an enumerator every time. That
    /// single question was three quarters of everything the simulation gave the garbage
    /// collector; the answer is the same, it simply no longer costs anything to get.
    /// </summary>
    private static readonly Dictionary<string, HashSet<string>> PrimarySetByCountry =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<string, string> FirstPrimaryByCountry =
        new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, CultureDefinition> Cultures => CultureById;
    public static IReadOnlyDictionary<string, ReligionDefinition> Religions => ReligionById;
    public static IReadOnlyList<ProfessionDefinition> Professions => ProfessionList;
    public static int CountryCount => ByCountry.Count;
    public static int RegionOverrideCount => ByRegion.Count;

    public static CultureDefinition Culture(string id) =>
        id is not null && CultureById.TryGetValue(id, out var found) ? found : null;

    public static ReligionDefinition Religion(string id) =>
        id is not null && ReligionById.TryGetValue(id, out var found) ? found : null;

    // In the reader's language: the tables translate the peoples by their English name, and the
    // region panel said "Largest people: Danish" in Russian because nothing asked them.
    public static string CultureName(string id) => Loc.Get(CultureKey(id));
    /// <summary>The people's name as the tables key it, in English: for names that are stored, not shown.</summary>
    public static string CultureKey(string id) => Culture(id)?.Name ?? Titled(id);
    public static string ReligionName(string id) => Loc.Get(Religion(id)?.Name ?? Titled(id));

    private static readonly Dictionary<string, ProfessionDefinition> ProfessionById = new(StringComparer.OrdinalIgnoreCase);
    private static volatile System.Collections.Frozen.FrozenDictionary<string, ProfessionDefinition> _frozenProfessions;
    internal static volatile int ProfessionVersion;

    public static ProfessionDefinition Profession(string id)
    {
        if (id is null) return null;
        var frozen = _frozenProfessions;
        if (frozen is not null)
            return frozen.TryGetValue(id, out var cached) ? cached : null;
        return ProfessionById.TryGetValue(id, out var profession) ? profession : null;
    }

    public static string ProfessionName(string id) => Loc.Get(Profession(id)?.Name ?? Titled(id));

    /// <summary>
    /// The peoples a state is for, as a set - or null for a state that is for nobody. For a
    /// caller asking about every group in a region: one lookup of the country instead of one
    /// per group, which in the weekly pass over every region was most of what it cost.
    /// </summary>
    public static IReadOnlySet<string> PrimarySet(string countryTag) =>
        countryTag is not null && PrimarySetByCountry.TryGetValue(countryTag, out var primary) ? primary : null;

    /// <summary>Whether a people is one of the state's own, which is what makes the rest minorities.</summary>
    public static bool IsPrimary(string countryTag, string culture) =>
        countryTag is not null && culture is not null &&
        PrimarySetByCountry.TryGetValue(countryTag, out var primary) &&
        primary.Contains(culture);

    /// <summary>
    /// The people a state assimilates into: the first it is for, or its own tag if it says
    /// nothing. Looked up rather than taken off the front of a list, because the caller is
    /// in the weekly loop over every region in the world.
    /// </summary>
    public static string PrimaryCulture(string countryTag) =>
        countryTag is not null && FirstPrimaryByCountry.TryGetValue(countryTag, out var first)
            ? first
            : countryTag?.ToLowerInvariant() ?? "";

    public static IReadOnlyList<string> PrimaryCultures(string countryTag) =>
        countryTag is not null && PrimaryByCountry.TryGetValue(countryTag, out var primary)
            ? primary
            : Array.Empty<string>();

    /// <summary>
    /// Say who a country is for, for a country the scenario never declared.
    ///
    /// history/countries answers this for every country the world shipped with, and nothing
    /// else ever needed to ask - until a country could be founded in the middle of a
    /// campaign. A tag the file has never heard of is a state that is for nobody, which
    /// makes every person inside it a minority of it: maximally restive the day it is born,
    /// its own people rising against it, and gone into its own provinces inside a few years.
    ///
    /// That is not a rule anybody would write on purpose, and it is exactly what happened
    /// the first time a country seceded here - Malay left Indonesia with four regions of
    /// Kalimantan and all four of them promptly seceded from Malay.
    ///
    /// Declaring replaces what was there, because the country doing it is the authority on
    /// the question, and it is idempotent so that restoring a save can simply do it again.
    /// </summary>
    public static void Declare(string countryTag, IEnumerable<string> cultures)
    {
        if (string.IsNullOrWhiteSpace(countryTag) || cultures is null) return;

        var list = new List<string>();
        foreach (var culture in cultures)
        {
            if (string.IsNullOrWhiteSpace(culture)) continue;
            if (!list.Contains(culture, StringComparer.OrdinalIgnoreCase)) list.Add(culture);
        }
        if (list.Count == 0) return;

        PrimaryByCountry[countryTag] = list;
        PrimarySetByCountry[countryTag] = new HashSet<string>(list, StringComparer.OrdinalIgnoreCase);
        FirstPrimaryByCountry[countryTag] = list[0];
    }

    static DemographyCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        CultureById.Clear();
        ReligionById.Clear();
        ProfessionList.Clear();
        ProfessionById.Clear();
        _frozenProfessions = null;
        unchecked { ProfessionVersion++; }
        ByCountry.Clear();
        ByRegion.Clear();
        PrimaryByCountry.Clear();
        PrimarySetByCountry.Clear();
        FirstPrimaryByCountry.Clear();
        FaithByCountry.Clear();
        FaithByRegion.Clear();

        foreach (var document in GameDataLoader.DataDocuments("common", "religions"))
            using (document) MergeReligions(document.RootElement, null);
        RebuildFamilies();
        // Where a region or a country believes differently from its peoples' own data, and how
        // each faith family divides into denominations there (Pew, censuses - cited per file).
        foreach (var document in GameDataLoader.DataDocuments("history", "faiths"))
            using (document) MergeFaithSettings(document.RootElement, null);
        foreach (var document in GameDataLoader.DataDocuments("common", "cultures"))
            using (document) MergeCultures(document.RootElement, null);
        foreach (var document in GameDataLoader.DataDocuments("common", "professions"))
            using (document) MergeProfessions(document.RootElement, null);
        foreach (var document in GameDataLoader.DataDocuments("history", "pops"))
            using (document) MergePops(document.RootElement, null);
        // Whose state each country is: history/countries says it now, beside the rest of what a
        // country is on the day the bookmark opens. history/pops still may, for a mod written
        // before it moved; the later file wins either way.
        foreach (var document in GameDataLoader.DataDocuments("history", "countries", "2024"))
            using (document) MergePrimaries(document.RootElement);
    }

    private static void MergePrimaries(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object ||
                !entry.Value.TryGetProperty("primary_cultures", out var listed) ||
                listed.ValueKind != JsonValueKind.Array) continue;
            Declare(entry.Name, listed.EnumerateArray()
                .Where(value => value.ValueKind == JsonValueKind.String).Select(value => value.GetString()));
        }
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
            if (entry.Value.TryGetProperty("family", out var family)) religion.Family = family.GetString() ?? "";
        }
        RebuildFamilies();
    }

    // ------------------------------------------------------------------ families and denominations

    /// <summary>
    /// Denomination id to family id, for denominations only: a family is not in it, so asking
    /// about the ids population groups actually carry is one failed lookup.
    /// </summary>
    private static readonly Dictionary<string, string> FamilyByDenomination = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, List<string>> DenominationsByFamily = new(StringComparer.OrdinalIgnoreCase);
    private static int _faithVersion;

    private static void RebuildFamilies()
    {
        FamilyByDenomination.Clear();
        DenominationsByFamily.Clear();
        foreach (var religion in ReligionById.Values)
        {
            if (!religion.IsDenomination) continue;
            // A family is one level: a denomination naming another denomination is read as its family's.
            var family = religion.Family;
            if (ReligionById.TryGetValue(family, out var parent) && parent.IsDenomination) family = parent.Family;
            FamilyByDenomination[religion.Id] = family;
            if (!DenominationsByFamily.TryGetValue(family, out var list))
                DenominationsByFamily[family] = list = new List<string>();
            list.Add(religion.Id);
            // A denomination written without a group takes its family's, so formables'
            // required_religion_groups see a Lutheran as christian.
            if (string.IsNullOrEmpty(religion.Group) && ReligionById.TryGetValue(family, out var root))
                religion.Group = root.Group;
        }
        foreach (var list in DenominationsByFamily.Values) list.Sort(StringComparer.Ordinal);
        _faithVersion++;
    }

    /// <summary>
    /// The family a faith id belongs to: itself for a family (and for any id nobody declared),
    /// the family for a denomination. What every mechanic comparing faiths compares.
    /// </summary>
    public static string FamilyOf(string id) =>
        id is not null && FamilyByDenomination.TryGetValue(id, out var family) ? family : id ?? "";

    public static bool IsDenomination(string id) => id is not null && FamilyByDenomination.ContainsKey(id);

    /// <summary>Every denomination declared under a family, by id.</summary>
    public static IReadOnlyList<string> DenominationIdsIn(string familyId) =>
        familyId is not null && DenominationsByFamily.TryGetValue(familyId, out var list) ? list : Array.Empty<string>();

    public static int DenominationCount => FamilyByDenomination.Count;

    private static readonly Dictionary<string, FaithSetting> FaithByCountry = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, FaithSetting> FaithByRegion = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyDictionary<string, FaithSetting> CountryFaithSettings => FaithByCountry;
    public static IReadOnlyDictionary<string, FaithSetting> RegionFaithSettings => FaithByRegion;

    public static void LoadFaithSettings(string json, List<string> errors) => Read(json, errors, MergeFaithSettings);

    /// <summary>
    /// history/faiths: { "countries": { "USA": {...} }, "regions": { "us_ut": {...} } }, each
    /// place { "faiths": { family: share }, "denominations": { family: { denomination: share } },
    /// "peoples": { culture: { the same two } } }. Later files win place by place.
    /// </summary>
    private static void MergeFaithSettings(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("countries", out var countries) && countries.ValueKind == JsonValueKind.Object)
            foreach (var entry in countries.EnumerateObject())
                if (!entry.Name.StartsWith("//", StringComparison.Ordinal))
                    FaithByCountry[entry.Name] = ReadFaithSetting(entry.Value, entry.Name, errors);
        if (root.TryGetProperty("regions", out var regions) && regions.ValueKind == JsonValueKind.Object)
            foreach (var entry in regions.EnumerateObject())
                if (!entry.Name.StartsWith("//", StringComparison.Ordinal))
                    FaithByRegion[entry.Name] = ReadFaithSetting(entry.Value, entry.Name, errors);
    }

    private static FaithSetting ReadFaithSetting(JsonElement value, string where, List<string> errors)
    {
        var setting = new FaithSetting();
        if (value.ValueKind != JsonValueKind.Object) return setting;
        if (value.TryGetProperty("faiths", out var faiths) && faiths.ValueKind == JsonValueKind.Object)
        {
            // A denomination written as a faith counts toward its family, which is what a
            // population group carries; the denomination itself goes into the sub-shares.
            var byFamily = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
            foreach (var share in faiths.EnumerateObject())
            {
                if (share.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                if (!share.Value.TryGetSingle(out var weight) || !(weight > 0f) || !float.IsFinite(weight))
                {
                    errors?.Add($"faiths '{where}': share of '{share.Name}' must be a positive number.");
                    continue;
                }
                var family = FamilyOf(share.Name);
                byFamily[family] = byFamily.GetValueOrDefault(family) + weight;
                if (IsDenomination(share.Name))
                {
                    var list = setting.Denominations.TryGetValue(family, out var known) ? known.ToList() : new List<(string, float)>();
                    list.Add((share.Name, weight));
                    setting.Denominations[family] = list.ToArray();
                }
            }
            var total = byFamily.Values.Sum();
            if (total > 0f)
            {
                setting.Faiths = byFamily.OrderByDescending(entry => entry.Value)
                    .ThenBy(entry => entry.Key, StringComparer.Ordinal)
                    .Select(entry => (entry.Key, entry.Value / total)).ToArray();
                // Denominations written among the faiths are shares of the place; as a sub-share
                // they are of their family.
                foreach (var family in setting.Denominations.Keys.ToList())
                    setting.Denominations[family] = setting.Denominations[family]
                        .Select(entry => (entry.Item1, byFamily[family] > 0f ? entry.Item2 / byFamily[family] : 0f)).ToArray();
            }
        }
        if (value.TryGetProperty("denominations", out var denominations) && denominations.ValueKind == JsonValueKind.Object)
            foreach (var family in denominations.EnumerateObject())
            {
                if (family.Name.StartsWith("//", StringComparison.Ordinal) || family.Value.ValueKind != JsonValueKind.Object) continue;
                var list = new List<(string, float)>();
                foreach (var share in family.Value.EnumerateObject())
                {
                    if (share.Name.StartsWith("//", StringComparison.Ordinal)) continue;
                    if (!share.Value.TryGetSingle(out var weight) || weight < 0f || !float.IsFinite(weight))
                    {
                        errors?.Add($"faiths '{where}': denomination share '{share.Name}' must be a number from 0 to 1.");
                        continue;
                    }
                    if (weight > 0f) list.Add((share.Name, weight));
                }
                var sum = list.Sum(entry => entry.Item2);
                // Shares of the family; more than the whole is read as proportions of it.
                if (sum > 1.0001f) list = list.Select(entry => (entry.Item1, entry.Item2 / sum)).ToList();
                setting.Denominations[family.Name] = list.ToArray();
            }
        if (value.TryGetProperty("peoples", out var peoples) && peoples.ValueKind == JsonValueKind.Object)
            foreach (var people in peoples.EnumerateObject())
                if (!people.Name.StartsWith("//", StringComparison.Ordinal))
                    setting.Peoples[people.Name] = ReadFaithSetting(people.Value, $"{where}/{people.Name}", errors);
        return setting;
    }

    /// <summary>
    /// The faith of one people in one place, as at most three families: what history/faiths
    /// writes for this region or country, where it applies, and otherwise the people's own.
    ///
    /// A place's mix written for nobody in particular applies to every people whose own faiths
    /// all appear in it - Utah's Latter-day Saints reweight the Americans and the Hispanic
    /// Americans living there, and leave its Jews and its Hindus as they are. Three rather than
    /// two because the place was written on purpose and from a source; only the handful of
    /// places written gain a group, and the people's own data still keeps to two.
    /// </summary>
    public static IReadOnlyList<(string Religion, float Share)> FaithOf(string cultureId, string regionId, string countryTag)
    {
        var own = FaithOf(cultureId);
        if (FaithByRegion.Count == 0 && FaithByCountry.Count == 0) return own;
        return Placed(regionId is not null && FaithByRegion.TryGetValue(regionId, out var region) ? region : null)
            ?? Placed(countryTag is not null && FaithByCountry.TryGetValue(countryTag, out var country) ? country : null)
            ?? own;

        IReadOnlyList<(string, float)> Placed(FaithSetting place)
        {
            if (place is null) return null;
            if (cultureId is not null && place.Peoples.TryGetValue(cultureId, out var people) && people.Faiths is { Length: > 0 })
                return Top(people.Faiths);
            if (place.Faiths is not { Length: > 0 }) return null;
            foreach (var (religion, _) in own)
                if (!place.Faiths.Any(entry => string.Equals(entry.Religion, religion, StringComparison.OrdinalIgnoreCase)))
                    return null;
            return Top(place.Faiths);
        }

        static IReadOnlyList<(string, float)> Top((string Religion, float Share)[] mix)
        {
            var top = mix.Take(3).ToArray();
            var kept = top.Sum(entry => entry.Share);
            return kept <= 0f ? null : top.Select(entry => (entry.Religion, entry.Share / kept)).ToArray();
        }
    }

    /// <summary>
    /// How one family divides into denominations for one people in one region of one country,
    /// adding to one. Read from the most particular place down - the people in the region, the
    /// region, the people in the country, the country - each place dividing what the one above
    /// it left undivided; whatever nobody divides stays the family itself ("Protestant").
    /// A denomination id is its own single answer. Interface and specific mechanics only: it
    /// adds no population groups and costs the daily simulation nothing.
    /// </summary>
    public static IReadOnlyList<(string Denomination, float Share)> DenominationsOf(
        string religionId, string regionId, string countryTag, string cultureId)
    {
        if (string.IsNullOrEmpty(religionId)) return Array.Empty<(string, float)>();
        if (IsDenomination(religionId) || !DenominationsByFamily.ContainsKey(religionId))
            return new[] { (religionId, 1f) };
        var family = religionId;
        var result = new List<(string Id, float Share)>();
        var left = 1f;
        var region = regionId is not null && FaithByRegion.TryGetValue(regionId, out var r) ? r : null;
        var country = countryTag is not null && FaithByCountry.TryGetValue(countryTag, out var c) ? c : null;
        Divide(region is not null && cultureId is not null && region.Peoples.TryGetValue(cultureId, out var rp) ? rp : null);
        Divide(region);
        Divide(country is not null && cultureId is not null && country.Peoples.TryGetValue(cultureId, out var cp) ? cp : null);
        Divide(country);
        if (left > 0.0001f) Add(family, left);
        return result.OrderByDescending(entry => entry.Share).ThenBy(entry => entry.Id, StringComparer.Ordinal)
            .Select(entry => (entry.Id, entry.Share)).ToArray();

        void Divide(FaithSetting place)
        {
            if (place is null || left <= 0.0001f || !place.Denominations.TryGetValue(family, out var shares)) return;
            // What this place says about denominations already placed above is not said twice:
            // the rest of its table is scaled to the part still undivided.
            var named = result.Select(entry => entry.Id).ToHashSet(StringComparer.OrdinalIgnoreCase);
            var fresh = shares.Where(entry => !named.Contains(entry.Denomination)).ToArray();
            var claimed = shares.Where(entry => named.Contains(entry.Denomination)).Sum(entry => entry.Share);
            var room = 1f - claimed;
            if (room <= 0.0001f) return;
            var portion = left;
            foreach (var (denomination, share) in fresh)
            {
                var part = portion * share / room;
                Add(denomination, part);
                left -= part;
            }
            if (left < 0f) left = 0f;
        }

        void Add(string id, float share)
        {
            if (share <= 0f) return;
            for (var i = 0; i < result.Count; i++)
                if (string.Equals(result[i].Id, id, StringComparison.OrdinalIgnoreCase))
                {
                    result[i] = (result[i].Id, result[i].Share + share);
                    return;
                }
            result.Add((id, share));
        }
    }

    /// <summary>
    /// Every denomination held in a region, from its population groups, largest first - what
    /// the population screen and the map show. A family no source divides shows as itself.
    /// </summary>
    public static IReadOnlyList<(string Denomination, long Size)> DenominationsIn(ScenarioState state, string countryTag)
    {
        if (state is null || state.PopulationGroups.Count == 0) return Array.Empty<(string, long)>();
        var byPeopleAndFaith = new Dictionary<(string Culture, string Religion), long>();
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0) continue;
            var key = (group.Culture ?? "", group.Religion ?? "");
            byPeopleAndFaith[key] = byPeopleAndFaith.GetValueOrDefault(key) + group.Size;
        }
        var totals = new Dictionary<string, double>(StringComparer.OrdinalIgnoreCase);
        foreach (var ((culture, religion), size) in byPeopleAndFaith)
            foreach (var (denomination, share) in DenominationsOf(religion, state.Id, countryTag, culture))
                totals[denomination] = totals.GetValueOrDefault(denomination) + size * (double)share;
        return totals.Select(entry => (entry.Key, (long)Math.Round(entry.Value)))
            .Where(entry => entry.Item2 > 0)
            .OrderByDescending(entry => entry.Item2).ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .ToArray();
    }

    /// <summary>
    /// How many of a region's people hold one particular denomination - for a mechanic that
    /// cares about the denomination rather than the family. A family id counts the family.
    /// </summary>
    public static long DenominationSize(ScenarioState state, string countryTag, string denominationId)
    {
        if (state is null || string.IsNullOrEmpty(denominationId)) return 0;
        var family = FamilyOf(denominationId);
        var exact = !IsDenomination(denominationId);
        double total = 0;
        foreach (var group in state.PopulationGroups)
        {
            if (group.Size <= 0 || !string.Equals(FamilyOf(group.Religion), family, StringComparison.OrdinalIgnoreCase)) continue;
            if (exact) { total += group.Size; continue; }
            foreach (var (denomination, share) in DenominationsOf(group.Religion, state.Id, countryTag, group.Culture))
                if (string.Equals(denomination, denominationId, StringComparison.OrdinalIgnoreCase))
                    total += group.Size * (double)share;
        }
        return (long)Math.Round(total);
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
            if (entry.Value.TryGetProperty("country", out var country)) culture.Country = country.GetString() ?? "";
            if (entry.Value.TryGetProperty("adjective", out var adjective))
                culture.Adjective = adjective.GetString() ?? "";
            if (entry.Value.TryGetProperty("style", out var style))
                culture.Style = style.GetString() ?? "";
            if (entry.Value.TryGetProperty("country_regions", out var homeland) && homeland.ValueKind == JsonValueKind.Array)
            {
                culture.CountryRegions.Clear();
                foreach (var id in homeland.EnumerateArray())
                    if (id.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(id.GetString()))
                        culture.CountryRegions.Add(id.GetString());
            }

            // "religion": "orthodox", or a split: { "christian": 0.55, "sunni": 0.45 }
            if (!entry.Value.TryGetProperty("religion", out var faith)) continue;
            culture.Religions.Clear();
            culture.FaithCache = null;
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
            if (!ProfessionById.TryGetValue(entry.Name, out var profession))
            {
                profession = new ProfessionDefinition { Id = entry.Name, Name = Titled(entry.Name) };
                ProfessionList.Add(profession);
                ProfessionById[entry.Name] = profession;
            }
            var value = entry.Value;
            if (value.TryGetProperty("name", out var name)) profession.Name = name.GetString() ?? profession.Name;
            if (value.TryGetProperty("colour", out var colour)) profession.Colour = colour.GetString() ?? profession.Colour;
            if (value.TryGetProperty("class", out var social) &&
                Enum.TryParse<SocialClass>(social.GetString(), true, out var parsed)) profession.Class = parsed;
            if (value.TryGetProperty("sector", out var sector) &&
                Enum.TryParse<Sector>(sector.GetString(), true, out var worked)) profession.Sector = worked;
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
        _frozenProfessions = System.Collections.Frozen.FrozenDictionary.ToFrozenDictionary(ProfessionById, StringComparer.OrdinalIgnoreCase);
        unchecked { ProfessionVersion++; }
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
        PrimarySetByCountry[entry.Name] = new HashSet<string>(primary, StringComparer.OrdinalIgnoreCase);
        FirstPrimaryByCountry[entry.Name] = primary[0];
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
    /// <param name="stated">
    /// What history/states writes for each region, in the same order as <paramref name="states"/>,
    /// or null where it writes nothing. A region's own list is taken at its word ahead of
    /// anything a country-wide table would work out for it.
    /// </param>
    public static IReadOnlyList<(string Culture, float Share)>[] AllocateCultures(
        string countryTag, IReadOnlyList<ScenarioState> states, IReadOnlyList<long> populations,
        IReadOnlyList<IReadOnlyList<(string Culture, float Share)>> stated = null)
    {
        var result = new IReadOnlyList<(string, float)>[states.Count];
        IReadOnlyList<(string Culture, float Share)> Written(int index) =>
            stated is not null && index < stated.Count && stated[index] is { Count: > 0 } mix ? Normalise(mix)
            : ByRegion.TryGetValue(states[index].Iso ?? "", out var authored)
                ? Normalise(authored.Select(share => (share.Culture, share.Share)))
                : null;
        if (!ByCountry.TryGetValue(countryTag ?? "", out var country) || country.Count == 0)
        {
            // Nothing for the country as a whole: a region that says nothing is the state's own
            // people - its primary culture, or its tag's when it has not even that.
            var fallback = new[] { (PrimaryCulture(countryTag) is { Length: > 0 } own ? own : "unknown", 1f) };
            for (var index = 0; index < states.Count; index++)
                result[index] = Written(index) ?? fallback;
            return result;
        }

        // Regions that speak for themselves are settled first and take no further part.
        var open = new List<int>();
        for (var index = 0; index < states.Count; index++)
        {
            if (Written(index) is { } written) result[index] = written;
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
        if (culture is null || culture.Religions.Count == 0) return NoFaith;
        // Asked per region by the weekly assimilation pass: worked out once per people and
        // kept, instead of sorting the people's faiths again every time.
        var cached = culture.FaithCache;
        if (cached is not null && culture.FaithCacheVersion == _faithVersion) return cached;
        // Families, not denominations: a people written as "lutheran" is Protestant to every
        // mechanic and lives in Protestant population groups.
        var byFamily = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var (religion, weight) in culture.Religions)
        {
            var family = FamilyOf(religion);
            byFamily[family] = byFamily.GetValueOrDefault(family) + weight;
        }
        var top = byFamily
            .OrderByDescending(entry => entry.Value)
            .ThenBy(entry => entry.Key, StringComparer.Ordinal)
            .Take(2)
            .ToArray();
        var kept = top.Sum(entry => entry.Value);
        cached = kept <= 0f ? NoFaith : top.Select(entry => (entry.Key, entry.Value / kept)).ToArray();
        culture.FaithCache = cached;
        culture.FaithCacheVersion = _faithVersion;
        return cached;
    }

    private static readonly (string, float)[] NoFaith = { ("irreligion", 1f) };

    private static string Titled(string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return "";
        var words = id.Replace('_', ' ');
        return CultureInfo.InvariantCulture.TextInfo.ToTitleCase(words);
    }
}
