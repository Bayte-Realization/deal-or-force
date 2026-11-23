using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How long a released country has to stand before it keeps the name it was founded
    /// under. A state born last Tuesday and renamed twice is a state whose government is
    /// still arguing about what it is.
    /// </summary>
    public int FoundingNameSettleDays { get; set; } = 730;

    /// <summary>
    /// The least share of a founded state's people that must still live on what its name is for
    /// - its people, its land, its region - before the name is recomposed from the ground it now
    /// holds. A Dagestani republic that has become mostly Rostov and Voronezh is the Don.
    /// </summary>
    public float NameHoldMinimum { get; set; } = 0.30f;

    /// <summary>Days a new government must hold before the form of state in the name follows it.</summary>
    public int NameFormFollowsDays { get; set; } = 180;

    /// <summary>The stability below which a newborn state still calls itself provisional.</summary>
    public float FoundingSettleStability { get; set; } = 0.50f;
}

public sealed partial class Nation
{
    /// <summary>
    /// How a founded country builds its name: a base (a people, a region, a predecessor) and
    /// a form of state. Kept apart from the name so the reader's language still applies and
    /// the form can change when the government does.
    /// </summary>
    public string FoundNameKind = "";
    public string FoundNameId = "";
    public string FoundNameForm = "";

    /// <summary>
    /// The base name as it read on the day it was given. A people or a predecessor is looked
    /// up again on every draw, so a language switch renames them; a region is kept, because
    /// the table can be asked for it only while the campaign is in hand.
    /// </summary>
    public string FoundNameBase = "";

    /// <summary>The naming school chosen at founding, or empty for automatic.</summary>
    public string NameConvention = "";

    /// <summary>Whether a founded state has already moved from its first name to its settled one.</summary>
    public int NameSettledDay = -1;

    /// <summary>The country this one was released from, or -1 for a country the scenario shipped.</summary>
    public int ReleasedFromId = -1;
}

/// <summary>
/// What a country is called before and after it has settled.
///
/// The campaign used to name every breakaway after the city its capital happened to sit in:
/// a country that seceded from Yemen and took Sanaa was called "Sanaa" for the rest of the
/// game, and the sides of a civil war kept the names of the committees that founded them.
/// A name is part of what a state is, not an artefact of the map. This composes one from the
/// three things a founding actually knows - the people, the ground, and the predecessor -
/// and leaves the form of state to the politics the state adopts.
///
/// Split cleanly across partial modules:
/// - <c>NationNaming.cs</c>: founding base selection, predecessor/core/ground identity resolution.
/// - <c>NationNaming.Grammar.cs</c>: adjectives, Russian agreement, state-form wrappers &amp; key translation.
/// - <c>NationNaming.Lifecycle.cs</c>: rendering, clash avoidance, legacy migration, monthly refit &amp; command.
/// </summary>
public static partial class NationNaming
{
    public static readonly string[] Conventions = { "auto", "historic", "people", "land", "region", "movement" };

    public static bool KnownConvention(string id) =>
        !string.IsNullOrEmpty(id) && Array.IndexOf(Conventions, id) >= 0;

    /// <summary>The base a new state is named for, as a kind and an id the save can hold.</summary>
    public static (string Kind, string Id) BaseFor(Campaign campaign, Nation parent, int[] regions,
        string culture, string convention, bool rising = false)
    {
        var region = LargestRegion(campaign, regions);
        var historic = Predecessor(campaign, regions, culture);
        switch (convention)
        {
            case "historic" when historic is not null: return IdentitySource(historic);
            case "land" when LandAtlas.BestFor(campaign, regions, parent?.Tag) is { } chosenLand: return ("land", chosenLand.Id);
            case "region" when region is not null: return ("region", region.Id);
            case "people" when !string.IsNullOrWhiteSpace(culture) && HasOwnCountryName(culture): return ("culture", culture);
            case "movement" when !string.IsNullOrWhiteSpace(culture) && HasOwnCountryName(culture): return ("culture", culture);
        }
        // Auto: the strongest, most authentic claim first.
        // 1. A people with a state name nobody else is using: Ichkeria, Kurdistan, Tatarstan.
        // That name is a nation being restored or born, and it is sharper than the land's
        // adjective. A name an existing country already wears is not founded twice.
        if (!string.IsNullOrWhiteSpace(culture) && HasOwnCountryName(culture) &&
            !CountryNameTaken(campaign, culture) &&
            IsPeoplesGround(campaign, regions, culture) && !SamePeople(culture, parent))
            return ("culture", culture);

        // 2. The land the ground actually is. Donbas and Crimea are named for themselves even
        //    when the people declaring the state are Ukrainian: the land is smaller and truer
        //    than the country it belongs to. But it has to be a land that says this ground is
        //    part of it - Scotland's box contains the Highlands without Scotland declaring
        //    them, and a state founded on the Highlands is Highland, not a second Scotland.
        var land = LandAtlas.BestFor(campaign, regions, parent?.Tag);
        if (land is { Macro: false } && DeclaresGround(land, campaign, regions) &&
            !NameAlreadyTaken(campaign, LandAtlas.Name(land)))
            return ("land", land.Id);

        // 2. Foreign core / homeland tag: if ground has homeland cores of a distinct country
        // (e.g. Ukraine, Belarus, Poland, Georgia, Kazakhstan) not belonging to the parent.
        var foreignCore = ForeignCoreTag(campaign, parent, regions);
        // A core is a claim, not a people: the ground is named for that country only where its
        // people are the ones living there, or are the ones rising. A Russian rising in southern
        // Siberia, on ground Kazakhstan's data claims, called itself the "Kazakh Republic".
        if (foreignCore is not null && DemographyCatalog.PrimaryCulture(foreignCore) is { Length: > 0 } coreOwnPeople &&
            !string.Equals(coreOwnPeople, culture, StringComparison.OrdinalIgnoreCase) &&
            !IsPeoplesGround(campaign, regions, coreOwnPeople))
            foreignCore = null;
        if (foreignCore is not null)
        {
            var alive = campaign.NationByTag(foreignCore);
            var isAlive = alive is not null && campaign.StatesOf(alive).Length > 0;
            if (!isAlive) return ("tag", foreignCore);
            if (LandAtlas.BestFor(campaign, regions, foreignCore) is { } landInCore &&
                !NameAlreadyTaken(campaign, LandAtlas.Name(landInCore)))
                return ("land", landInCore.Id);
            // A rising calls itself free - "Free Ukraine" is what an uprising writes on its banners.
            if (rising) return ("free_tag", foreignCore);
            var foreignPeople = DemographyCatalog.PrimaryCulture(foreignCore);
            if (HasIdentity(foreignPeople)) return PeopleBase(campaign, foreignPeople);
            return ("free_tag", foreignCore);
        }

        // 3. A distinct culture that leads this ground and can carry a name.
        if (!string.IsNullOrWhiteSpace(culture) && HasIdentity(culture) &&
            IsPeoplesGround(campaign, regions, culture) && !SamePeople(culture, parent))
            return PeopleBase(campaign, culture);

        // 4. The people who live there, when they are not the parent's own.
        var groundPeople = GroundPeople(campaign, regions);
        if (HasIdentity(groundPeople) && !SamePeople(groundPeople, parent))
            return PeopleBase(campaign, groundPeople);

        // 5. A historic predecessor that once held this ground.
        if (historic is not null) return IdentitySource(historic);

        // 6. A land the catalogue declares or recognises (Siberia, Urals, Far East, Donbas, Texas, etc.).
        if (land is not null && !NameAlreadyTaken(campaign, LandAtlas.Name(land)))
            return ("land", land.Id);

        // 7. A macro or regional land that covers this ground (same BestFor result as step 6).
        //    Already checked via `land` above when not Macro/DeclaresGround.
        if (land is not null && !NameAlreadyTaken(campaign, LandAtlas.Name(land)))
            return ("land", land.Id);

        // 8. The region's own seat/city name (Rostov, Samara, Novosibirsk, Odesa, Lyon, Hamburg, etc.)
        //    - one nobody already wears: Nigeria's Niger State does not found a second Niger, it
        //    looks to the next region of the rising, as four Nigerian risings in one bench run
        //    each called themselves the Republic of Niger beside the real one.
        if (region is not null)
        {
            var candidates = _candidateStatesScratch ??= new List<ScenarioState>();
            candidates.Clear();
            var seen = _seenRegionScratch ??= new HashSet<int>();
            seen.Clear();
            if (regions is not null)
            {
                foreach (var rId in regions)
                {
                    if (!seen.Add(rId)) continue;
                    var s = campaign.StateByMapIndex(rId);
                    if (s is not null) candidates.Add(s);
                }
            }
            if (seen.Add(region.MapIndex) && !candidates.Contains(region)) candidates.Add(region);
            seen.Clear();
            candidates.Sort((a, b) =>
            {
                var regCmp = (b == region).CompareTo(a == region);
                if (regCmp != 0) return regCmp;
                var popCmp = b.Population.CompareTo(a.Population);
                return popCmp != 0 ? popCmp : a.MapIndex.CompareTo(b.MapIndex);
            });
            for (var i = 0; i < candidates.Count; i++)
            {
                var candidate = candidates[i];
                var place = StripAdministrativeSuffix(RegionNames.Resolve(candidate));
                if (place.Length > 0 && CompassDirection(place).Length == 0 &&
                    !string.Equals(place, parent?.Name, StringComparison.OrdinalIgnoreCase) &&
                    !NameAlreadyTaken(campaign, place))
                {
                    candidates.Clear();
                    return ("region", candidate.Id);
                }
            }
            candidates.Clear();
        }

        // 9. Region fallback: authentic regional identity rather than an occupier's compass direction -
        //    unless a country already wears the region's name, when the people who live there name
        //    it instead (Nigeria's Niger State was founding a second Niger beside the real one).
        if (region is not null)
        {
            var place = StripAdministrativeSuffix(RegionNames.Resolve(region));
            if (NameAlreadyTaken(campaign, place) && HasIdentity(groundPeople)) return PeopleBase(campaign, groundPeople);
            return ("region", region.Id);
        }
        if (!string.IsNullOrWhiteSpace(culture)) return ("culture", culture);
        return parent is null ? ("literal", "") : IdentitySource(parent);
    }

    [ThreadStatic] private static HashSet<int> _seenRegionScratch;
    [ThreadStatic] private static List<ScenarioState> _candidateStatesScratch;
    [ThreadStatic] private static Dictionary<string, long> _foreignCoreScratch;
    [ThreadStatic] private static Dictionary<string, long> _cultureTallyScratch;

    private static string ForeignCoreTag(Campaign campaign, Nation parent, int[] regions)
    {
        if (campaign?.States is null || regions is null || regions.Length == 0) return null;
        var seen = regions.Length > 1 ? (_seenRegionScratch ??= new HashSet<int>()) : null;
        seen?.Clear();
        var coreCounts = _foreignCoreScratch ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        coreCounts.Clear();
        var anyState = false;
        foreach (var rId in regions)
        {
            if (seen is not null && !seen.Add(rId)) continue;
            var s = campaign.StateByMapIndex(rId);
            if (s is null) continue;
            anyState = true;
            var weight = Math.Max(1L, s.Population);
            foreach (var core in s.CoreTags)
            {
                if (string.IsNullOrWhiteSpace(core)) continue;
                if (parent is not null && string.Equals(core, parent.Tag, StringComparison.OrdinalIgnoreCase)) continue;
                if (!Loc.Has("country." + core + ".name")) continue;
                coreCounts[core] = coreCounts.GetValueOrDefault(core) + weight;
            }
            if (campaign.Homelands is not null)
            {
                var origin = TerritorialClaims.Origin(s);
                foreach (var (nationId, homeRegions) in campaign.Homelands)
                {
                    if (homeRegions is null || (!homeRegions.Contains(s.MapIndex) && !homeRegions.Contains(origin))) continue;
                    var claimant = campaign.NationById(nationId);
                    if (claimant is null || claimant.Movement is not null || string.IsNullOrWhiteSpace(claimant.Tag)) continue;
                    if (parent is not null && string.Equals(claimant.Tag, parent.Tag, StringComparison.OrdinalIgnoreCase)) continue;
                    if (!Loc.Has("country." + claimant.Tag + ".name")) continue;
                    coreCounts[claimant.Tag] = coreCounts.GetValueOrDefault(claimant.Tag) + weight;
                }
            }
            // Check state.Id origin prefix (e.g. ua_ => UKR, by_ => BLR, pl_ => POL, etc.)
            var id = s.Id ?? "";
            var cut = id.IndexOf('_');
            if (cut > 0)
            {
                var originTag = LandAtlas.Iso2ToTag(id.AsSpan(0, cut));
                if (!string.IsNullOrWhiteSpace(originTag) &&
                    (parent is null || !string.Equals(originTag, parent.Tag, StringComparison.OrdinalIgnoreCase)) &&
                    Loc.Has("country." + originTag + ".name"))
                {
                    coreCounts[originTag] = coreCounts.GetValueOrDefault(originTag) + weight;
                }
            }
        }
        seen?.Clear();
        if (!anyState || coreCounts.Count == 0)
        {
            coreCounts.Clear();
            return null;
        }
        string bestTag = null;
        long bestWeight = long.MinValue;
        foreach (var pair in coreCounts)
        {
            if (bestTag is null || pair.Value > bestWeight)
            {
                bestTag = pair.Key;
                bestWeight = pair.Value;
            }
        }
        coreCounts.Clear();
        return bestTag;
    }

    /// <summary>
    /// The country name of the largest culture living in these regions that has one. This
    /// is what makes a direction name mean something: a region of Ukrainians rising from
    /// Russia anchors on "Ukraine", a region of Arabs on the Arab country of their own.
    /// </summary>
    private static string MajorityCountry(Campaign campaign, int[] regions)
    {
        if (campaign is null || regions is null || regions.Length == 0) return "";
        var totals = _cultureTallyScratch ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        totals.Clear();
        long total = 0;
        foreach (var id in regions)
        {
            var state = campaign.StateByMapIndex(id);
            if (state is null || state.Population <= 0) continue;
            var groups = state.PopulationGroups;
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Size <= 0) continue;
                var key = string.IsNullOrEmpty(group.Culture) ? "?" : group.Culture;
                totals[key] = totals.GetValueOrDefault(key) + group.Size;
                total += group.Size;
            }
        }
        // Only cultures holding >= 50% can qualify; at most two exist, preserving exact tie behavior.
        string firstCulture = null, secondCulture = null;
        long firstSize = -1, secondSize = -1;
        foreach (var (people, size) in totals)
        {
            if (size * 2 < total) continue;
            if (size > firstSize)
            {
                secondCulture = firstCulture; secondSize = firstSize;
                firstCulture = people; firstSize = size;
            }
            else if (size > secondSize)
            {
                secondCulture = people; secondSize = size;
            }
        }
        if (firstCulture is not null && DemographyCatalog.Culture(firstCulture)?.Country is { Length: > 0 } c1 && !string.IsNullOrWhiteSpace(c1))
            return c1;
        if (secondCulture is not null && DemographyCatalog.Culture(secondCulture)?.Country is { Length: > 0 } c2 && !string.IsNullOrWhiteSpace(c2))
            return c2;
        return "";
    }

    /// <summary>Which side of its parent's capital a region lies on, or nothing when it is the capital.</summary>
    private static string Direction(Campaign campaign, Nation parent, ScenarioState region)
    {
        if (campaign?.ProvinceMap is null || parent is null || region is null) return "";
        var (latitude, longitude) = CenterOf(campaign, region);
        if (Math.Abs(latitude) < 0.01f && Math.Abs(longitude) < 0.01f) return "";
        var north = latitude - parent.CapitalLatitude;
        var east = longitude - parent.CapitalLongitude;
        if (Math.Abs(north) < 0.4f && Math.Abs(east) < 0.4f) return "";
        if (Math.Abs(north) >= Math.Abs(east)) return north >= 0f ? "north" : "south";
        return east >= 0f ? "east" : "west";
    }

    /// <summary>
    /// Whether a land declares this ground as part of itself, rather than merely containing it.
    ///
    /// A land's own region list is a statement: Scotland declares `gb_sct`, so the Highlands
    /// are inside Scotland's box without being the region Scotland says it is. Only the first
    /// kind of knowledge may outrank the province's own name.
    /// </summary>
    internal static bool DeclaresGround(LandDefinition land, Campaign campaign, int[] regions)
    {
        if (land is null || campaign is null || regions is null || regions.Length == 0) return false;
        if (land.RegionIds.Count == 0 && land.RegionNames.Count == 0) return false;
        foreach (var index in regions)
        {
            var state = campaign.StateByMapIndex(index);
            if (state is null) continue;
            if (land.RegionIds.Contains(state.Id)) return true;
            if (land.RegionNames.Contains(state.Name)) return true;
        }
        return false;
    }

    /// <summary>
    /// A direction a region is named for, when its whole name is one: "East", "Eastern",
    /// "Восточная". Such a word is a side of something, not a country; the name composer
    /// anchors it on the country it came from instead of using it alone.
    /// </summary>
    public static string CompassDirection(string name) =>
        string.IsNullOrEmpty(name) ? "" : CompassDirection(name.AsSpan());

    private static string CompassDirection(ReadOnlySpan<char> name)
    {
        var trimmed = name.Trim();
        if (trimmed.Length is < 2 or > 9) return "";
        Span<char> lower = stackalloc char[trimmed.Length];
        trimmed.ToLowerInvariant(lower);
        ReadOnlySpan<char> word = lower;
        return word switch
        {
            "north" or "northern" or "север" or "северный" or "северная" or "северное" => "north",
            "south" or "southern" or "юг" or "южный" or "южная" or "южное" => "south",
            "east" or "eastern" or "восток" or "восточный" or "восточная" or "восточное" => "east",
            "west" or "western" or "запад" or "западный" or "западная" or "западное" => "west",
            _ => ""
        };
    }

    /// <summary>
    /// Whether a name already says what kind of state it is. Applying a second form to
    /// "United Republic of Tanzania" is how a country becomes "Republic of United Republic
    /// of Tanzania"; the name is left as it is instead.
    /// </summary>
    public static bool CarriesStateForm(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        // A name that is itself a republic - "Central African Republic", "Czech Republic",
        // "Республика Дагестан" - was becoming "Republic of Central African Republic".
        var trimmed = name.AsSpan().Trim();
        if (trimmed.EndsWith(" Republic", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Republic ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Республика", StringComparison.OrdinalIgnoreCase)) return true;
        var nameSpan = name.AsSpan();
        foreach (var phrase in FormPhrases)
        {
            if (phrase.Length >= 3 && nameSpan.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var key in StateFormKeys)
        {
            if (!Loc.Has(key)) continue;
            var pattern = Loc.Get(key);
            var idx = pattern.IndexOf("{0}", StringComparison.Ordinal);
            var head = (idx >= 0 ? pattern.AsSpan(0, idx) : pattern.AsSpan()).Trim();
            if (head.Length >= 3 && nameSpan.Contains(head, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether any word of a name is a side of the compass: "Matabeleland South", "North Kivu", "Восточный Казахстан".</summary>
    public static bool NamesASide(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        var span = name.AsSpan();
        while (!span.IsEmpty)
        {
            var sep = span.IndexOfAny(' ', '-', ',');
            var token = sep < 0 ? span : span[..sep];
            if (CompassDirection(token).Length > 0) return true;
            if (sep < 0) break;
            span = span[(sep + 1)..];
        }
        return false;
    }

    private static readonly string[] StateFormIds =
    {
        "kingdom", "republic", "islamic_republic", "holy_state", "people_republic",
        "junta", "federation", "commune", "technate", "provisional", "free", "emirate", "host", "patriarchate"
    };

    private static readonly string[] StateFormKeys =
        Array.ConvertAll(StateFormIds, static form => "name.found." + form);

    private static (float Latitude, float Longitude) CenterOf(Campaign campaign, ScenarioState region) =>
        region is null ? (0f, 0f) : LandAtlas.CentreOf(campaign, region.MapIndex);

    private static bool HasOwnCountryName(string culture) =>
        !string.IsNullOrWhiteSpace(DemographyCatalog.Culture(culture)?.Country);

    private static ScenarioState LargestRegion(Campaign campaign, int[] regions)
    {
        ScenarioState best = null;
        if (campaign is null || regions is null) return null;
        foreach (var id in regions)
        {
            var state = campaign.StateByMapIndex(id);
            if (state is null) continue;
            if (best is null || state.Population > best.Population) best = state;
        }
        return best;
    }

    private static readonly string[] RepublicPrefixes = { "Republic of ", "Республика " };

    private static ScenarioState FindStateById(Campaign campaign, string id)
    {
        if (campaign?.States is null || string.IsNullOrEmpty(id)) return null;
        var states = campaign.States;
        for (var i = 0; i < states.Count; i++)
            if (string.Equals(states[i].Id, id, StringComparison.Ordinal)) return states[i];
        return null;
    }

    /// <summary>
    /// The vanished country whose old ground overlaps these regions most, if its people are
    /// the people who live there now. The restoration catalogue's own test, applied to a
    /// state that does not exist yet.
    /// </summary>
    private static Nation Predecessor(Campaign campaign, int[] regions, string culture)
    {
        if (campaign?.ProvinceMap is null || regions is null || regions.Length == 0) return null;
        var wanted = new HashSet<int>(regions);
        var provinces = new HashSet<int>();
        for (var province = 1; province <= campaign.ProvinceMap.ProvinceCount; province++)
            if (wanted.Contains(campaign.ProvinceMap.ProvinceToState[province])) provinces.Add(province);

        var group = DemographyCatalog.Culture(culture)?.Group ?? "";
        Nation best = null;
        var bestOverlap = 0;
        foreach (var old in campaign.Nations)
        {
            if (old.Movement is not null || !FormableRestorations.Vanished(campaign, old)) continue;
            var authored = FormableRestorations.Authored(campaign, old.Tag);
            if (authored is null || authored.Length == 0) continue;
            var overlap = 0;
            foreach (var province in authored) if (provinces.Contains(province)) overlap++;
            if (overlap <= bestOverlap) continue;
            var kindred = string.IsNullOrWhiteSpace(culture);
            foreach (var people in DemographyCatalog.PrimaryCultures(old.Tag))
            {
                if (string.Equals(people, culture, StringComparison.OrdinalIgnoreCase) ||
                    (group.Length > 0 && string.Equals(DemographyCatalog.Culture(people)?.Group, group,
                        StringComparison.OrdinalIgnoreCase)))
                { kindred = true; break; }
            }
            if (!kindred) continue;
            best = old;
            bestOverlap = overlap;
        }
        return best;
    }

    /// <summary>Whether this people is the majority somewhere in the ground being released.</summary>
    private static bool IsPeoplesGround(Campaign campaign, int[] regions, string culture)
    {
        if (campaign is null || regions is null) return false;
        long total = 0, theirs = 0;
        foreach (var id in regions)
        {
            var state = campaign.StateByMapIndex(id);
            if (state is null || state.Population <= 0) continue;
            total += state.Population;
            theirs += EconomySystem.CultureSize(state, culture);
        }
        return total > 0 && theirs * 2 >= total;
    }

    /// <summary>Whether the ground is this people's by plurality, whatever the movement says.</summary>
    private static string GroundPeople(Campaign campaign, int[] regions)
    {
        if (campaign is null || regions is null) return "";
        var tally = _cultureTallyScratch ??= new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        tally.Clear();
        foreach (var id in regions)
        {
            var state = campaign.StateByMapIndex(id);
            if (state is null) continue;
            var groups = state.PopulationGroups;
            for (var i = 0; i < groups.Count; i++)
            {
                var group = groups[i];
                if (group.Size > 0 && !string.IsNullOrWhiteSpace(group.Culture))
                    tally[group.Culture] = tally.GetValueOrDefault(group.Culture) + group.Size;
            }
        }
        if (tally.Count == 0) return "";
        string bestKey = null;
        long bestSize = -1;
        foreach (var (people, size) in tally)
        {
            var cmp = size.CompareTo(bestSize);
            if (bestKey is null || cmp > 0 || (cmp == 0 && StringComparer.OrdinalIgnoreCase.Compare(people, bestKey) < 0))
            {
                bestKey = people;
                bestSize = size;
            }
        }
        return bestKey ?? "";
    }

    /// <summary>
    /// How a people becomes the base of a state's name: as a noun when it has a state name of
    /// its own ("Kurdistan"), as an adjective when that is how the country is spoken of
    /// ("Ukrainian", "Татарская"). Data decides, because it is a fact about the name.
    /// </summary>
    private static (string Kind, string Id) PeopleBase(Campaign campaign, string culture)
    {
        var definition = DemographyCatalog.Culture(culture);
        var noun = definition?.Country ?? "";
        var adjective = CultureAdjective(culture);
        // A state already wearing this name is not founded twice: a Ukrainian region leaving
        // Russia is the Ukrainian Republic, not a second Ukraine.
        var taken = CountryNameTaken(campaign, culture);
        var style = definition?.Style ?? (noun.Length > 0 ? "" : "adjective");
        if ((taken || string.Equals(style, "adjective", StringComparison.OrdinalIgnoreCase)) &&
            adjective.Length > 0)
            return ("culture_adj", culture);
        if (noun.Length > 0) return ("culture", culture);
        return adjective.Length > 0 ? ("culture_adj", culture) : ("culture", culture);
    }

    /// <summary>Whether an existing country already wears this people's state name.</summary>
    private static bool CountryNameTaken(Campaign campaign, string culture)
    {
        var definition = DemographyCatalog.Culture(culture);
        if (definition is null || string.IsNullOrWhiteSpace(definition.Country) || campaign is null)
            return false;
        var name = Loc.Get(definition.Country);
        foreach (var nation in campaign.Nations)
            if (campaign.StatesOf(nation).Length > 0 &&
                string.Equals(CountryNames.Resolve(nation, false), name, StringComparison.OrdinalIgnoreCase))
                return true;
        return false;
    }

    /// <summary>
    /// Whether some country in the world is already called this.
    ///
    /// A base that names a living country is not a base: it names the thing the new state is
    /// leaving, and two countries with one name on the same map is a bug a player reads
    /// immediately. The composer has to say where the ground is instead.
    ///
    /// A landless movement counts too. The Scottish civic campaign is a country in every sense
    /// that matters here - it has a name, a flag and a claim - so a state founded on the
    /// Highlands calling itself Scotland would be the second thing with that name, which is
    /// exactly the duplication this is written against.
    /// </summary>
    internal static bool NameAlreadyTaken(Campaign campaign, string name)
    {
        if (campaign is null || string.IsNullOrWhiteSpace(name)) return false;
        foreach (var nation in campaign.Nations)
        {
            var holds = campaign.StatesOf(nation).Length > 0;
            if (!holds && nation.Movement is not { Dissolved: false }) continue;
            if (string.Equals(CountryNames.Resolve(nation, false), name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(CountryNames.Resolve(nation, true), name, StringComparison.OrdinalIgnoreCase))
                return true;
            if (string.Equals(BaseOf(campaign, nation), name, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }
}
