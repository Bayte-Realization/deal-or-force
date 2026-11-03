using System;
using System.Collections.Generic;
using System.Linq;

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
/// </summary>
public static class NationNaming
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

        // 7. A macro or regional land that covers this ground.
        var anyLand = LandAtlas.BestFor(campaign, regions, parent?.Tag);
        if (anyLand is not null && !NameAlreadyTaken(campaign, LandAtlas.Name(anyLand)))
            return ("land", anyLand.Id);

        // 8. The region's own seat/city name (Rostov, Samara, Novosibirsk, Odesa, Lyon, Hamburg, etc.)
        //    - one nobody already wears: Nigeria's Niger State does not found a second Niger, it
        //    looks to the next region of the rising, as four Nigerian risings in one bench run
        //    each called themselves the Republic of Niger beside the real one.
        if (region is not null)
        {
            var wanted = new HashSet<int>(regions ?? Array.Empty<int>());
            foreach (var candidate in campaign.States.Where(s => wanted.Contains(s.MapIndex) || s == region)
                         .OrderByDescending(s => s == region).ThenByDescending(s => s.Population).ThenBy(s => s.MapIndex))
            {
                var place = StripAdministrativeSuffix(RegionNames.Resolve(candidate));
                if (place.Length > 0 && CompassDirection(place).Length == 0 &&
                    !string.Equals(place, parent?.Name, StringComparison.OrdinalIgnoreCase) &&
                    !NameAlreadyTaken(campaign, place))
                    return ("region", candidate.Id);
            }
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

    private static string ForeignCoreTag(Campaign campaign, Nation parent, int[] regions)
    {
        if (campaign?.States is null || regions is null || regions.Length == 0) return null;
        var wanted = new HashSet<int>(regions);
        var states = campaign.States.Where(s => wanted.Contains(s.MapIndex)).ToList();
        if (states.Count == 0) return null;

        var coreCounts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        foreach (var s in states)
        {
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
                var iso2 = id[..cut].ToLowerInvariant();
                var originTag = LandAtlas.Iso2ToTag(iso2);
                if (!string.IsNullOrWhiteSpace(originTag) &&
                    (parent is null || !string.Equals(originTag, parent.Tag, StringComparison.OrdinalIgnoreCase)) &&
                    Loc.Has("country." + originTag + ".name"))
                {
                    coreCounts[originTag] = coreCounts.GetValueOrDefault(originTag) + weight;
                }
            }
        }
        if (coreCounts.Count == 0) return null;
        return coreCounts.OrderByDescending(p => p.Value).First().Key;
    }


    /// <summary>
    /// The country name of the largest culture living in these regions that has one. This
    /// is what makes a direction name mean something: a region of Ukrainians rising from
    /// Russia anchors on "Ukraine", a region of Arabs on the Arab country of their own.
    /// </summary>
    private static string MajorityCountry(Campaign campaign, int[] regions)
    {
        if (campaign is null || regions is null || regions.Length == 0) return "";
        var totals = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0;
        foreach (var id in regions)
        {
            var state = campaign.States.Find(candidate => candidate.MapIndex == id);
            if (state is null || state.Population <= 0) continue;
            foreach (var (people, size) in EconomySystem.Cultures(state))
            {
                totals[people] = totals.GetValueOrDefault(people) + size;
                total += size;
            }
        }
        foreach (var (people, size) in totals.OrderByDescending(pair => pair.Value))
        {
            if (size * 2 < total) break;
            var country = DemographyCatalog.Culture(people)?.Country;
            if (!string.IsNullOrWhiteSpace(country)) return country;
        }
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
            var state = campaign.States.Find(candidate => candidate.MapIndex == index);
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
    public static string CompassDirection(string name)
    {
        var word = (name ?? "").Trim().ToLowerInvariant();
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
        var trimmed = name.Trim();
        if (trimmed.EndsWith(" Republic", StringComparison.OrdinalIgnoreCase) ||
            trimmed.StartsWith("Republic ", StringComparison.OrdinalIgnoreCase) ||
            trimmed.Contains("Республика", StringComparison.OrdinalIgnoreCase)) return true;
        foreach (var phrase in FormPhrases)
        {
            if (phrase.Length >= 3 && name.Contains(phrase, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        foreach (var form in StateFormIds)
        {
            var key = "name.found." + form;
            if (!Loc.Has(key)) continue;
            var pattern = Loc.Get(key);
            var head = pattern.Split("{0}")[0].Trim();
            if (head.Length >= 3 && name.Contains(head, StringComparison.OrdinalIgnoreCase)) return true;
        }
        return false;
    }

    /// <summary>Whether any word of a name is a side of the compass: "Matabeleland South", "North Kivu", "Восточный Казахстан".</summary>
    public static bool NamesASide(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return false;
        foreach (var word in name.Split(' ', '-', ','))
            if (CompassDirection(word).Length > 0) return true;
        return false;
    }

    private static readonly string[] StateFormIds =
    {
        "kingdom", "republic", "islamic_republic", "holy_state", "people_republic",
        "junta", "federation", "commune", "technate", "provisional", "free", "emirate", "host", "patriarchate"
    };


    private static (float Latitude, float Longitude) CenterOf(Campaign campaign, ScenarioState region)
    {
        var map = campaign.ProvinceMap;
        float latitude = 0f, longitude = 0f;
        var counted = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.ProvinceToState[province] != (ushort)region.MapIndex) continue;
            var centre = map.CenterOf(province);
            latitude += centre.Latitude;
            longitude += centre.Longitude;
            counted++;
        }
        return counted == 0 ? (0f, 0f) : (latitude / counted, longitude / counted);
    }

    private static bool HasOwnCountryName(string culture) =>
        !string.IsNullOrWhiteSpace(DemographyCatalog.Culture(culture)?.Country);

    private static ScenarioState LargestRegion(Campaign campaign, int[] regions)
    {
        ScenarioState best = null;
        foreach (var id in regions ?? Array.Empty<int>())
        {
            var state = campaign?.States.Find(candidate => candidate.MapIndex == id);
            if (state is null) continue;
            if (best is null || state.Population > best.Population) best = state;
        }
        return best;
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
            var state = campaign.States.Find(candidate => candidate.MapIndex == id);
            if (state is null || state.Population <= 0) continue;
            total += state.Population;
            foreach (var (people, size) in EconomySystem.Cultures(state))
                if (string.Equals(people, culture, StringComparison.OrdinalIgnoreCase)) theirs += size;
        }
        return total > 0 && theirs * 2 >= total;
    }

    /// <summary>Whether the ground is this people's by plurality, whatever the movement says.</summary>
    private static string GroundPeople(Campaign campaign, int[] regions)
    {
        var tally = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        if (campaign is null || regions is null) return "";
        foreach (var id in regions)
        {
            var state = campaign.States.Find(candidate => candidate.MapIndex == id);
            if (state is null) continue;
            foreach (var (people, size) in EconomySystem.Cultures(state))
                if (!string.IsNullOrWhiteSpace(people))
                    tally[people] = tally.GetValueOrDefault(people) + Math.Max(0, size);
        }
        return tally.Count == 0 ? ""
            : tally.OrderByDescending(pair => pair.Value)
                .ThenBy(pair => pair.Key, StringComparer.OrdinalIgnoreCase).First().Key;
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

    /// <summary>
    /// The adjective a people wears in the reader's language: "Ukrainian", "Украинская".
    /// A translation row of its own wins; then the authored English word; then the people's
    /// own name, which in this table is already an adjective for most of the world.
    /// </summary>
    public static string CultureAdjective(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return "";
        if (Loc.Has("name.adj." + culture)) return Loc.Get("name.adj." + culture);
        var definition = DemographyCatalog.Culture(culture);
        if (definition is null) return "";
        if (!string.IsNullOrWhiteSpace(definition.Adjective)) return Loc.Get(definition.Adjective);
        return Loc.Get(definition.Name);
    }

    /// <summary>Whether the tables can name a state after this people at all.</summary>
    private static bool HasIdentity(string culture)
    {
        if (string.IsNullOrWhiteSpace(culture)) return false;
        return Loc.Has("name.adj." + culture) ||
            DemographyCatalog.Culture(culture) is { } definition &&
            (!string.IsNullOrWhiteSpace(definition.Country) ||
             !string.IsNullOrWhiteSpace(definition.Adjective) ||
             !string.IsNullOrWhiteSpace(definition.Name));
    }

    /// <summary>Whether two tags hold the same people. Identity, not the language family.</summary>
    private static bool SamePeople(string culture, Nation parent)
    {
        if (string.IsNullOrWhiteSpace(culture) || parent is null) return false;
        // A country can be several peoples at once - Russia is Russian and Tatar and Chechen in
        // the same legal sense - so a state carved out of it by one of its own peoples is a
        // region leaving, not a nation being born. Only a people outside that set is new.
        foreach (var people in DemographyCatalog.PrimaryCultures(parent.Tag))
            if (string.Equals(culture, people, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>A base built on an adjective rather than a noun: "the Ukrainian Republic".</summary>
    private static bool IsAdjectiveBase(Nation nation) =>
        nation is not null && nation.FoundNameKind is "culture_adj" or "land";

    /// <summary>
    /// Take an administrative word off the end of a region's name.
    ///
    /// "Greater Poland Voivodeship" is what a province is called; a country is not called the
    /// Republic of Greater Poland Voivodeship. The same goes for a prefecture, a state, an
    /// oblast, a governorate: they are the shape of a subdivision, not the name of a nation,
    /// and a player reading them hears a spreadsheet. Only a trailing word is removed, so
    /// "West Virginia" keeps its name.
    /// </summary>
    public static string StripAdministrativeSuffix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        var trimmed = StripRepublicPrefix(name.TrimEnd());
        foreach (var (suffix, cut) in AdministrativeSuffixes)
        {
            if (!trimmed.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var inner = trimmed[..^cut].TrimEnd();
            // Only when something is left and it is a name rather than the word alone: a
            // region actually called "Province" keeps it, and "West Virginia" is not touched.
            if (inner.Length >= 3) return inner;
        }
        return trimmed;
    }

    /// <summary>Trailing administrative words, in both shipped languages, and how long each is.</summary>
    private static readonly (string Suffix, int Cut)[] AdministrativeSuffixes =
    {
        (" Voivodeship", 12), (" Province", 9), (" Prefecture", 11), (" Governorate", 12),
        (" Department", 11), (" Oblast", 7), (" Region", 7), (" County", 7),
        (" State", 6), (" District", 9), (" Territory", 10), (" Municipality", 13),
        (" Воеводство", 10), (" Провинция", 10), (" Префектура", 11), (" Губерния", 9),
        (" Область", 8), (" Регион", 7), (" Округ", 6), (" Район", 6),
        // Russia's republics are regions called "Mari El Republic" or "Republic of Dagestan":
        // a state founded on one was "Republic of Mari El Republic".
        (" Republic", 9), (" Республика", 11)
    };

    /// <summary>A leading "Republic of" ("Республика"), which the state's own form will say again.</summary>
    public static string StripRepublicPrefix(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        foreach (var prefix in new[] { "Republic of ", "Республика " })
            if (name.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) && name.Length - prefix.Length >= 3)
                return name[prefix.Length..].TrimStart();
        return name;
    }

    /// <summary>The short name of a people's state for a map label: "Ukraine", "Kurdistan".</summary>
    private static string ShortPeopleName(string culture, string adjective)
    {
        var definition = DemographyCatalog.Culture(culture);
        return !string.IsNullOrWhiteSpace(definition?.Country)
            ? Loc.Get(definition.Country)
            : adjective;
    }

    /// <summary>
    /// Whether a region name is itself an adjective, as Russian oblast names are. Such a name
    /// takes the form of state after it, not in front of it.
    /// </summary>
    private static bool LooksAdjectival(string name)
    {
        var word = (name ?? "").Trim();
        return word.Length >= 4 &&
            (word.EndsWith("ая", StringComparison.OrdinalIgnoreCase) ||
             word.EndsWith("яя", StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>
    /// The grammatical gender a form of state has, in a language whose adjectives agree with
    /// it. A khanate is neuter and an emirate masculine, so "Казахское ханство" and "Аварский
    /// имамат" are both the adjective the tables hold, wearing the right ending. A language
    /// without agreement ignores the question entirely.
    /// </summary>
    private static char GenderOf(string form) => form switch
    {
        "kingdom" or "khanate" or "holy_state" or "junta" or "provisional" or "host" => 'n',
        "emirate" or "imamate" or "sultanate" or "patriarchate" => 'm',
        _ => 'f'
    };

    /// <summary>The feminine adjective a people or land carries, put into another gender.</summary>
    private static string Agree(string adjective, char gender)
    {
        if (!string.Equals(Loc.Language, "russian", StringComparison.OrdinalIgnoreCase) ||
            string.IsNullOrEmpty(adjective) || gender == 'f') return adjective;
        var word = adjective.Trim();
        if (word.Length < 4) return adjective;
        var stem = word[..^2];
        if (!word.EndsWith("ая", StringComparison.OrdinalIgnoreCase) &&
            !word.EndsWith("яя", StringComparison.OrdinalIgnoreCase)) return adjective;
        return gender == 'm' ? stem + "ий" : stem + "ое";
    }

    /// <summary>
    /// Turn a Russian city or regional seat noun (e.g. "Ростов", "Самара", "Одесса", "Новосибирск")
    /// into a clean adjective ("Ростовская", "Самарская", "Одесская", "Новосибирская") so that
    /// the full title reads naturally: "Ростовская Республика", not "Республика Ростов".
    /// </summary>
    public static string MakeRussianRegionAdjective(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return "";
        var trimmed = name.Trim();
        if (LooksAdjectival(trimmed)) return trimmed;
        var hasCyrillic = trimmed.Any(c => (c >= 'а' && c <= 'я') || (c >= 'А' && c <= 'Я'));
        if (!hasCyrillic) return "";

        var lower = trimmed.ToLowerInvariant();
        if (lower == "москва") return "Московская";
        if (lower is "санкт-петербург" or "петербург") return "Петербургская";
        if (lower == "уфа") return "Уфимская";
        if (lower == "чита") return "Читинская";
        if (lower == "тверь") return "Тверская";
        if (lower == "пермь") return "Пермская";
        if (lower == "вологда") return "Вологодская";
        if (lower == "калуга") return "Калужская";
        if (lower == "казань") return "Казанская";
        if (lower == "рязань") return "Рязанская";
        if (lower == "астрахань") return "Астраханская";
        if (lower == "тюмень") return "Тюменская";
        if (lower == "псков") return "Псковская";

        if (lower.EndsWith("ск") || lower.EndsWith("цк") || lower.EndsWith("шк"))
            return trimmed + "ая";
        if (lower.EndsWith("град") || lower.EndsWith("город") || lower.EndsWith("бург"))
            return trimmed + "ская";
        if (lower.EndsWith("ань") || lower.EndsWith("ень") || lower.EndsWith("онь") || lower.EndsWith("ь"))
            return trimmed[..^1] + "ская";
        if (lower.EndsWith("а") || lower.EndsWith("я") || lower.EndsWith("о") || lower.EndsWith("е"))
            return trimmed[..^1] + "ская";

        return trimmed + "ская";
    }

    /// <summary>
    /// The form of state the politics give a name: what turns "Kurdistan" into a republic,
    /// a kingdom or an Islamic republic.
    /// </summary>
    public static string FormFor(string government, string ideology, string religion, string culture = "")
    {
        var faith = string.IsNullOrWhiteSpace(religion) || StateIdentity.IsSecular(religion) ? "" : religion;
        var muslim = faith is "sunni" or "shia" or "ibadi" or "sufi";
        var christian = faith is "orthodox" or "catholic" or "protestant";
        // A throne is not called the same thing on the steppe, in the Caucasus and in Arabia.
        // The culture decides which word for a ruler a state wears, the way "Kazakh Khanate"
        // and "Sultanate of Oman" are facts about those countries rather than about monarchy.
        var group = DemographyCatalog.Culture(culture)?.Group ?? "";
        var steppe = group is "turkic" or "mongolic" or "tungusic" or "siberian";
        var caucasian = group is "caucasian";
        var arabian = group is "arabic";
        return government switch
        {
            "absolute_monarchy" => muslim
                ? steppe ? "khanate" : arabian ? "sultanate" : "emirate"
                : steppe ? "khanate" : "kingdom",
            "constitutional_monarchy" => steppe ? "khanate" : "kingdom",
            // A theocracy is rule by the faith. In the Caucasus that has a name of its own -
            // the Imamate - and elsewhere the modern word is an Islamic republic. A Christian
            // theocracy is a patriarchate.
            "theocracy" => muslim
                ? caucasian ? "imamate" : "islamic_republic"
                : christian ? "patriarchate" : "holy_state",
            "one_party_state" when ideology == "communism" => "people_republic",
            "one_party_state" => "republic",
            "military_junta" => "junta",
            "federation" => "federation",
            "direct_democracy" or "anarchism" => "commune",
            "technocracy" => "technate",
            "provisional_government" => "republic",
            _ => ideology == "communism" ? "people_republic" : "republic"
        };
    }

    /// <summary>The base of a country's name, in the reader's language.</summary>
    public static string BaseName(Campaign campaign, Nation nation)
    {
        if (nation is null) return "";
        switch (nation.FoundNameKind)
        {
            case "culture":
                var definition = DemographyCatalog.Culture(nation.FoundNameId);
                return !string.IsNullOrWhiteSpace(definition?.Country)
                    ? Loc.Get(definition.Country)
                    : DemographyCatalog.CultureName(nation.FoundNameId);
            case "culture_adj":
                var adjective = CultureAdjective(nation.FoundNameId);
                if (adjective.Length > 0) return adjective;
                return DemographyCatalog.Culture(nation.FoundNameId) is { Country.Length: > 0 } people
                    ? Loc.Get(people.Country)
                    : nation.FoundNameBase;
            case "land":
                return LandAtlas.Find(nation.FoundNameId) is { } land
                    ? LandAtlas.Name(land)
                    : nation.FoundNameBase;
            case "free_tag":
                var freeCountry = CountryNames.Of(nation.FoundNameId);
                return Loc.Has("name.found.free")
                    ? Loc.Format("name.found.free", freeCountry)
                    : "Free " + freeCountry;
            case "region":
                if (campaign?.States.Find(state => state.Id == nation.FoundNameId) is { } region)
                {
                    var resolved = RegionNames.Resolve(region);
                    // A subdivision's administrative word is not part of a country's name: a
                    // state founded on the Greater Poland Voivodeship is the Republic of
                    // Greater Poland, not of Greater Poland Voivodeship.
                    resolved = StripAdministrativeSuffix(resolved);
                    // "Eastern" is a side of a country, not a country. A save written by the
                    // older composer can still hold that recipe, so it is repaired on the way
                    // out: the side, anchored on the country it was a side of.
                    if (CompassDirection(resolved) is { Length: > 0 } compass &&
                        campaign.NationById(nation.ReleasedFromId) is { } parent)
                    {
                        var source = IdentitySource(parent);
                        var anchor = SourceName(source.Kind, source.Id, "");
                        if (anchor.Length > 0) return Loc.Format("name.direction." + compass, anchor).Trim();
                    }
                    return resolved;
                }
                return nation.FoundNameBase;
            case "tag":
                return CountryNames.Of(nation.FoundNameId);
            case "formable":
                return FormableNations.Find(nation.FoundNameId) is { Name.Length: > 0 } catalogued
                    ? Loc.GetOr(catalogued.Name, catalogued.Name)
                    : nation.FoundNameBase;
            case "literal":
                // A recipe nothing defines is not a name. The formable's own row in the data is.
                if (TryTranslate(nation.FoundNameId, out var literal)) return literal;
                if (FormableIdOf(nation.FoundNameId) is { Length: > 0 } fId &&
                    FormableNations.Find(fId) is { Name.Length: > 0 } fDef)
                    return Loc.GetOr(fDef.Name, fDef.Name);
                return nation.FoundNameBase;
            case "direction":
                if (!ReadDirection(nation.FoundNameId, out var direction, out var kind, out var id)) return nation.FoundNameBase;
                var sided = SourceName(kind, id, nation.FoundNameBase);
                // A source that already names a side takes no second one: "Northern
                // Matabeleland South" was a state founded on Matabeleland South.
                return NamesASide(sided) ? sided : Loc.Format("name.direction." + direction, sided);
            default:
                // An unknown recipe from an older save: its stored name is a name unless it
                // is a key, in which case the country's own name is the only honest answer.
                return LooksLikeKey(nation.Name) ? CountryNames.Of(nation.Tag) : nation.Name ?? "";
        }
    }

    /// <summary>
    /// The base of a country's name with no form of state in it.
    ///
    /// A base is an ingredient: `Render` wraps it in the form the politics call for, so a base
    /// that already carries one produces "Republic of South Republic of India". This is the
    /// one place that guarantee is made, and every route into a base goes through here.
    /// </summary>
    public static string BaseOf(Campaign campaign, Nation nation)
    {
        var basis = BaseName(campaign, nation);
        return string.IsNullOrWhiteSpace(basis) ? basis : WithoutStackedWrappers(basis);
    }

    private static readonly string[] CompassSides =
        { "Northeast", "Northwest", "Southeast", "Southwest", "North", "South", "East", "West" };

    /// <summary>
    /// Peel every form of state a previous composition put on, and nothing else.
    ///
    /// Recomposing a form over a base that already wears one produced "Republic of South
    /// Republic of India", so the form comes off here - repeatedly, because a save written by
    /// the older composer can carry two.
    ///
    /// A compass side is NOT a wrapper. "South India" is a name: a country released from
    /// India's south is South India, and peeling the "South" leaves a one-region state called
    /// India, which is the same name as the country it left and says nothing about where it
    /// is. Only the form of state comes off.
    /// </summary>
    private static string WithoutStackedWrappers(string basis)
    {
        var current = basis;
        for (var pass = 0; pass < 4; pass++)
        {
            var before = current;
            current = WithoutFormOfState(current);
            if (current.Equals(before, StringComparison.Ordinal)) break;
        }
        return current.Trim();
    }

    // A tag is an identifier, not necessarily a localisation key: released countries and
    // movements have tags too. Keep a translatable identity recipe, not "West SHA" or a
    // frozen English sentence. Strip a predecessor's directional wrapper on later splits.
    private static (string Kind, string Id) IdentitySource(Nation parent)
    {
        if (!string.IsNullOrWhiteSpace(parent.CustomName)) return ("literal", parent.CustomName);
        var restored = FormableRestorations.RestoredTag(parent.FormedNationId);
        if (restored is not null) return ("tag", restored);
        if (!string.IsNullOrEmpty(parent.FormedNationId))
        {
            // The catalogue row is the name — always. A raw "formable.<id>.name" key is a
            // recipe, not a name: whether a table has the row depends on the language, and
            // handing the key onward printed "formable.gulf_union.name" on a country card.
            // When no row exists at all, the key still carries history for the render path
            // (TryTranslate → the old formable's data), so it is kept as a recipe.
            if (FormableNations.Find(parent.FormedNationId) is not null)
                return ("formable", parent.FormedNationId);
            return ("literal", "formable." + parent.FormedNationId + ".name");
        }
        if (parent.FoundNameKind == "direction" &&
            ReadDirection(parent.FoundNameId, out _, out var kind, out var id)) return (kind, id);
        if (parent.FoundNameKind == "culture") return ("culture", parent.FoundNameId);
        if (parent.FoundNameKind == "region") return ("literal", "region." + parent.FoundNameId + ".name");
        if (parent.FoundNameKind == "literal") return ("literal", parent.FoundNameId);
        if (parent.FoundNameKind == "tag" && Loc.Has("country." + parent.FoundNameId + ".name"))
            return ("tag", parent.FoundNameId);
        // A movement's tag row is the movement's label ("Belarusian democratic forces"), not a
        // country: what it founds is called by the state name the movement fights for.
        if (parent.Movement is not null && MovementCatalog.Of(parent.Movement.Id)?.StateName is { Length: > 0 } stateName)
            return ("literal", stateName);
        if (Loc.Has("country." + parent.Tag + ".name")) return ("tag", parent.Tag);
        // Civil opposition's raw name is a recipe of its own. Prefer its country base,
        // not a new state called "Republic of National Salvation Front of ...".
        if (parent.Name?.StartsWith("rising:", StringComparison.Ordinal) == true)
        {
            var parts = parent.Name.Split(':', 4);
            if (parts.Length == 4)
                return Loc.Has("country." + parts[2] + ".name") ? ("tag", parts[2]) : ("literal", parts[3]);
        }
        // A split is named for what it split from, so the ingredient has to be the parent's
        // base and not its displayed name: India's own name reads "Republic of India" once its
        // politics have changed, and a south that took that as its base would be the Republic
        // of South Republic of India.
        return ("literal", WithoutFormOfState(parent.Name ?? ""));
    }

    private static bool ReadDirection(string recipe, out string direction, out string kind, out string id)
    {
        var parts = (recipe ?? "").Split(':', 3);
        direction = parts.Length > 0 ? parts[0] : "";
        kind = parts.Length > 1 ? parts[1] : "";
        id = parts.Length > 2 ? parts[2] : "";
        return direction is "north" or "south" or "east" or "west" &&
            kind is "tag" or "culture" or "literal" or "formable" && id.Length > 0;
    }

    private static string SourceName(string kind, string id, string fallback = "")
    {
        if (kind == "tag") return CountryNames.Of(id);
        if (kind == "culture")
            return DemographyCatalog.Culture(id)?.Country is { Length: > 0 } name
                ? Loc.Get(name) : DemographyCatalog.CultureName(id);
        if (kind == "formable")
            return FormableNations.Find(id) is { Name.Length: > 0 } catalogued
                ? Loc.GetOr(catalogued.Name, catalogued.Name) : fallback;
        // A recipe nothing defines is not a name. The formable's own row in the data is the
        // honest answer: all eighty-six of them carry one, and eighty-one of those have no
        // localisation row, which is how a country came to be called "formable.gulf_union.name".
        if (TryTranslate(id, out var translated)) return translated;
        if (FormableIdOf(id) is { Length: > 0 } formableId &&
            FormableNations.Find(formableId) is { Name.Length: > 0 } formable)
            return Loc.GetOr(formable.Name, formable.Name);
        return fallback;
    }

    /// <summary>The formable id a recipe names, or nothing when it names something else.</summary>
    private static string FormableIdOf(string id)
    {
        if (string.IsNullOrEmpty(id) || !id.StartsWith("formable.", StringComparison.Ordinal)) return "";
        var body = id["formable.".Length..];
        var cut = body.LastIndexOf(".name", StringComparison.Ordinal);
        return cut > 0 ? body[..cut] : "";
    }

    /// <summary>
    /// Translate a recipe, or say plainly that it is not one.
    ///
    /// `Loc.Get` answers a key it does not have with the key itself, which is the right
    /// behaviour for a screen - a missing label should be visible to whoever is testing - and
    /// the wrong one for a name, because a country called "formable.gulf_union.name" is a
    /// country nobody can read. Every name in the game is resolved through here, so a recipe
    /// nothing defines can no longer reach the map, a dossier or a chronicle line as raw text.
    /// </summary>
    public static bool TryTranslate(string id, out string name)
    {
        name = "";
        if (string.IsNullOrEmpty(id)) return false;
        // A recipe is a key; a literal is a sentence. A literal that happens to contain a dot
        // is still a literal, so only something shaped like a key is asked of the tables.
        if (!LooksLikeKey(id)) { name = id; return true; }
        if (!Loc.Has(id)) return false;
        var text = Loc.Get(id);
        if (text.Length == 0 || string.Equals(text, id, StringComparison.Ordinal)) return false;
        name = text;
        return true;
    }

    /// <summary>Whether a string is shaped like one of this game's localisation keys.</summary>
    public static bool LooksLikeKey(string text)
    {
        if (string.IsNullOrEmpty(text) || !text.Contains('.')) return false;
        foreach (var prefix in KeyPrefixes)
            if (text.StartsWith(prefix, StringComparison.Ordinal)) return true;
        return false;
    }

    private static readonly string[] KeyPrefixes =
    {
        "formable.", "country.", "region.", "name.", "city.", "culture.", "religion.",
        "government.", "ideology.", "rising.", "subject.", "war.", "good.", "building.",
        "technology.", "decision.", "event.", "modifier.", "handbook."
    };

    /// <summary>
    /// Take a form of state back out of a name, so it can be put in exactly once.
    ///
    /// A base that already reads "Republic of India" composed with "Republic of {0}" gives
    /// "Republic of South Republic of India", which is the naming equivalent of "ATM machine".
    /// The form is a wrapper the politics add at the end, so anything used as an ingredient
    /// has to arrive without one - and a name a mod wrote that legitimately contains the word
    /// "republic" in the middle is left alone, because only a wrapper is stripped.
    /// </summary>
    public static string WithoutFormOfState(string name)
    {
        if (string.IsNullOrWhiteSpace(name)) return name ?? "";
        var current = name.Trim();
        bool changed;
        do
        {
            changed = false;
            foreach (var (prefix, cut) in FormPrefixes)
            {
                if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    var inner = current[cut..].Trim();
                    if (inner.Length > 0 && inner.Length <= 64)
                    {
                        current = inner;
                        changed = true;
                        break;
                    }
                }
            }
        } while (changed);

        // Also check if a directional prefix was attached to a country that carried a state form:
        // e.g. "South Republic of India" -> "South India", "Южная Республика Индия" -> "Южная Индия".
        foreach (var dir in CompassSides)
        {
            var prefix = dir + " ";
            if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = current[prefix.Length..].Trim();
                var cleanedRest = WithoutFormOfState(rest);
                if (!cleanedRest.Equals(rest, StringComparison.OrdinalIgnoreCase))
                    current = prefix + cleanedRest;
            }
        }
        foreach (var (prefix, _) in LegacyDirections)
        {
            if (current.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
            {
                var rest = current[prefix.Length..].Trim();
                var cleanedRest = WithoutFormOfState(rest);
                if (!cleanedRest.Equals(rest, StringComparison.OrdinalIgnoreCase))
                    current = prefix + cleanedRest;
            }
        }

        return current.Trim();
    }

    /// <summary>Every form of state a name can be wrapped in, and how long the wrapper is.</summary>
    private static readonly (string Prefix, int Cut)[] FormPrefixes =
    {
        ("Republic of ", 12), ("Kingdom of ", 11), ("Islamic Republic of ", 20),
        ("Holy State of ", 14), ("People's Republic of ", 21), ("Military Government of ", 22),
        ("Federation of ", 14), ("Commune of ", 12), ("Technate of ", 12),
        ("Provisional Government of ", 25), ("Free ", 5), ("Islamic Emirate of ", 19),
        ("Cossack Host of ", 16), ("Patriarchate of ", 15), ("Military Command of ", 20),
        ("Provisional Council of ", 23), ("Liberation Front of ", 20), ("Военное Командование ", 21),
        ("Республика ", 11), ("Королевство ", 12), ("Исламская Республика ", 21),
        ("Святое Государство ", 19), ("Священное Государство ", 22), ("Народная Республика ", 21),
        ("Военное Правительство ", 23), ("Федерация ", 11), ("Коммуна ", 9), ("Технат ", 8),
        ("Вольная ", 7), ("Вольное ", 8), ("Вольный ", 8), ("Исламский Эмират ", 17),
        ("Всевеликое Войско ", 18), ("Патриархат ", 11), ("Временное Правительство ", 24)
    };

    /// <summary>The same wrappers as plain phrases, for asking whether a name already has one.</summary>
    private static readonly string[] FormPhrases = FormPrefixes.Select(pair => pair.Prefix.Trim()).ToArray();

    /// <summary>Host-only migration of the old generated tag leak. Never rewrites player
    /// custom names, arbitrary prose, or a network snapshot independently on a client.</summary>
    public static int MigrateLegacy(Campaign campaign)
    {
        if (campaign is null) return 0;
        var changed = 0;
        foreach (var nation in campaign.Nations)
        {
            if (!string.IsNullOrWhiteSpace(nation.CustomName)) continue;
            if (nation.FoundNameKind == "tag" && !Loc.Has("country." + nation.FoundNameId + ".name") &&
                campaign.NationByTag(nation.FoundNameId) is { } source && source != nation)
            {
                var identity = IdentitySource(source);
                nation.FoundNameKind = identity.Kind;
                nation.FoundNameId = identity.Id;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
            }
            // A save written while "Eastern" was treated as a country's whole name repairs
            // the recipe: the side, anchored on the country it was a side of.
            if (nation.FoundNameKind == "region" &&
                campaign.States.Find(state => state.Id == nation.FoundNameId) is { } oldRegion &&
                CompassDirection(RegionNames.Resolve(oldRegion)) is { Length: > 0 } oldCompass &&
                campaign.NationById(nation.ReleasedFromId) is { } oldParent)
            {
                var parentIdentity = IdentitySource(oldParent);
                nation.FoundNameKind = "direction";
                nation.FoundNameId = oldCompass + ":" + parentIdentity.Kind + ":" + parentIdentity.Id;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
            }
            // A name that was persisted as a key - "formable.gulf_union.name" - is written
            // back as the catalogue's name, so history and war labels stop carrying it too.
            if (LooksLikeKey(nation.Name) && !Loc.Has(nation.Name) &&
                FormableIdOf(nation.Name) is { Length: > 0 } keyFormable &&
                FormableNations.Find(keyFormable) is { Name.Length: > 0 } keyDefinition)
            {
                nation.Name = keyDefinition.Name;
                nation.NameProclaimed = true;
                changed++;
            }
            // The same repair one level down: a base stored as a formable key.
            if (nation.FoundNameKind == "literal" && LooksLikeKey(nation.FoundNameId) &&
                !Loc.Has(nation.FoundNameId) &&
                FormableIdOf(nation.FoundNameId) is { Length: > 0 } literalFormable &&
                FormableNations.Find(literalFormable) is not null)
            {
                nation.FoundNameKind = "formable";
                nation.FoundNameId = literalFormable;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
            }
            if (nation.FoundNameKind != "literal") continue;
            // Exact legacy prefixes only. Localised strings were formerly persisted as
            // literal text, so recognise both shipped languages independent of UI language.
            foreach (var (prefix, direction) in LegacyDirections)
            {
                if (!(nation.FoundNameId ?? "").StartsWith(prefix, StringComparison.Ordinal)) continue;
                var tag = nation.FoundNameId[prefix.Length..];
                if (Loc.Has("country." + tag + ".name")) continue;
                var parent = campaign.NationByTag(tag);
                if (parent is null || parent == nation) continue;
                var identity = IdentitySource(parent);
                nation.FoundNameKind = "direction";
                nation.FoundNameId = direction + ":" + identity.Kind + ":" + identity.Id;
                nation.FoundNameBase = BaseName(campaign, nation);
                changed++;
                break;
            }
        }
        if (changed > 0) { campaign.MarkStrategicChange(); campaign.MarkMapChanged(); }
        return changed;
    }

    private static readonly (string Prefix, string Direction)[] LegacyDirections =
    {
        ("North ", "north"), ("South ", "south"), ("East ", "east"), ("West ", "west"),
        ("Северная ", "north"), ("Южная ", "south"), ("Восточная ", "east"), ("Западная ", "west")
    };

    /// <summary>The composed name, in the reader's language, with the form the politics call for.</summary>
    public static string Render(Campaign campaign, Nation nation, bool map)
    {
        if (nation is null) return "";
        if (!string.IsNullOrWhiteSpace(nation.CustomName)) return Loc.Get(nation.CustomName);
        if (string.IsNullOrWhiteSpace(nation.FoundNameKind)) return "";
        var basis = BaseOf(campaign, nation);
        if (string.IsNullOrWhiteSpace(basis)) return "";
        var form = string.IsNullOrWhiteSpace(nation.FoundNameForm)
            ? FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, nation.DeclaredCulture)
            : nation.FoundNameForm;
        // A base that already reads as a state takes no second form of state.
        if (!map && CarriesStateForm(basis)) return basis;

        // An adjective base reads the way the country is spoken of - "the Ukrainian Republic",
        // "the Donetsk Republic" - instead of pushing a noun into a form of state. Its noun is
        // kept for the short map label, so a map never carries a form of state.
        var adjectiveBase = nation.FoundNameKind switch
        {
            "land" when !string.Equals(Loc.Language, "english", StringComparison.OrdinalIgnoreCase) =>
                LandAtlas.Find(nation.FoundNameId) is { } landDef ? LandAtlas.Adjective(landDef) : "",
            "culture_adj" => CultureAdjective(nation.FoundNameId),
            // Russian regions are named with adjectives ("Костанайская"), so the form of state
            // goes after them: "Костанайская Республика", not "Республика Костанайская".
            "region" when LooksAdjectival(basis) => basis,
            "region" when string.Equals(Loc.Language, "russian", StringComparison.OrdinalIgnoreCase) =>
                MakeRussianRegionAdjective(basis),
            _ => ""
        };
        if (adjectiveBase.Length > 0)
        {
            if (map)
            {
                var shortName = nation.FoundNameKind == "land"
                    ? LandAtlas.Name(LandAtlas.Find(nation.FoundNameId))
                    : nation.FoundNameKind == "region"
                        ? basis
                        : ShortPeopleName(nation.FoundNameId, adjectiveBase);
                if (shortName.Length > 0) return shortName;
            }
            else
            {
                var adjectiveKey = "name.found." + form + ".adj";
                if (Loc.Has(adjectiveKey))
                    return Loc.Format(adjectiveKey, Agree(adjectiveBase, GenderOf(form)));
            }
        }

        var key = "name.found." + form + (map ? ".map" : "");
        if (Loc.Has(key)) return Loc.Format(key, basis);
        key = "name.found." + form;
        return Loc.Has(key) ? Loc.Format(key, basis) : basis;
    }

    /// <summary>
    /// Give a state that has just been released the name its people, ground and politics
    /// call for. Called after the country exists, because that is the first moment all three
    /// are known; a custom name the player typed is left exactly as typed.
    /// </summary>
    public static void ApplyTo(Campaign campaign, Nation born, Nation parent, int[] regions,
        string convention, string culture)
    {
        if (campaign is null || born is null) return;
        born.ReleasedFromId = parent?.Id ?? -1;
        if (!string.IsNullOrWhiteSpace(born.CustomName))
        {
            born.NameSettledDay = campaign.ElapsedDays;
            return;
        }
        convention = KnownConvention(convention) ? convention : "auto";
        var (kind, id) = BaseFor(campaign, parent, regions ?? Array.Empty<int>(), culture, convention,
            !string.IsNullOrEmpty(born.FoundedInRevolt));
        born.NameConvention = convention;
        born.FoundNameKind = kind;
        born.FoundNameId = id;
        born.FoundNameForm = FormFor(born.Government, born.Ideology, born.DeclaredReligion, culture);
        born.FoundNameBase = BaseName(campaign, born);
        born.NameSettledDay = -1;
        AvoidClash(campaign, born, regions);
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// A name another living state already wears is not worn twice. Three Eritreas and four
    /// Ukraines at once were what happens when every piece of the same people's ground leaves on
    /// its own: the second piece is called for its largest region instead.
    /// </summary>
    private static void AvoidClash(Campaign campaign, Nation nation, int[] regions)
    {
        if (campaign is null || nation is null || !string.IsNullOrWhiteSpace(nation.CustomName)) return;
        var name = Render(campaign, nation, false);
        if (name.Length == 0) return;
        // The base as well as the whole name: "Republic of Zimbabwe" does not equal "Zimbabwe",
        // and a Shona rising in Mozambique wore it beside the real one. The country it rose from
        // is left out - a claimant to its own state carries that state's name on purpose.
        var basis = BaseOf(campaign, nation);
        var parentId = nation.SecededFromId >= 0 ? nation.SecededFromId : nation.ReleasedFromId;
        var clash = false;
        foreach (var other in campaign.Nations)
        {
            if (ReferenceEquals(other, nation) || other.Id == parentId ||
                (!campaign.HasOwnedLand(other) && other.Movement is not { Dissolved: false })) continue;
            var full = CountryNames.Resolve(other, false);
            var shortName = CountryNames.Resolve(other, true);
            if (!string.Equals(full, name, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(full, basis, StringComparison.OrdinalIgnoreCase) &&
                !string.Equals(shortName, basis, StringComparison.OrdinalIgnoreCase)) continue;
            clash = true;
            break;
        }
        if (!clash) return;
        // Its people's adjective first - the Shona Republic - and only then its largest region.
        // Never the people of the country it rose from, nor a people that already has a state of
        // its own: Russians rising in Siberia held by Kazakhstan were the "Kazakh Republic" for a
        // while, because the parent's people had been written down as theirs.
        var parentNation = parentId >= 0 ? campaign.NationById(parentId) : null;
        var people = nation.FoundedCultures.FirstOrDefault(c => CultureAdjective(c).Length > 0 &&
            !SamePeople(c, parentNation) && !CountryNameTaken(campaign, c));
        if (people is not null && !(nation.FoundNameKind == "culture_adj" && nation.FoundNameId == people))
        {
            var keptKind = nation.FoundNameKind;
            var keptId = nation.FoundNameId;
            nation.FoundNameKind = "culture_adj";
            nation.FoundNameId = people;
            var renamed = Render(campaign, nation, false);
            if (!NameAlreadyTaken(campaign, renamed)) { nation.FoundNameBase = BaseName(campaign, nation); return; }
            nation.FoundNameKind = keptKind;
            nation.FoundNameId = keptId;
        }
        var region = LargestRegion(campaign, regions);
        if (region is null || (nation.FoundNameKind == "region" && nation.FoundNameId == region.Id)) return;
        nation.FoundNameKind = "region";
        nation.FoundNameId = region.Id;
        nation.FoundNameBase = BaseName(campaign, nation);
    }

    /// <summary>
    /// The name a country takes when it stops being a provisional committee and becomes a
    /// state: the same people and ground, the form the government now has.
    /// </summary>
    public static void Settle(Campaign campaign, Nation nation, string reason)
    {
        if (campaign is null || nation is null) return;
        if (!string.IsNullOrWhiteSpace(nation.CustomName)) { nation.NameSettledDay = campaign.ElapsedDays; return; }
        if (string.IsNullOrEmpty(nation.FoundNameKind)) return;
        var form = FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, nation.DeclaredCulture);
        if (string.Equals(form, nation.FoundNameForm, StringComparison.OrdinalIgnoreCase))
        {
            nation.NameSettledDay = campaign.ElapsedDays;
            return;
        }
        var before = Render(campaign, nation, false);
        nation.FoundNameForm = form;
        nation.NameSettledDay = campaign.ElapsedDays;
        var after = Render(campaign, nation, false);
        if (!string.Equals(before, after, StringComparison.Ordinal))
            campaign.Chronicle.Record(campaign, "state_named", nation.Id, place: after);
        DiagnosticLogs.Write("simulation",
            $"FOUNDING_NAME nation={nation.Tag} reason={reason} form={form} before={before} after={after} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
    }

    /// <summary>
    /// What a civil-war victor calls the state it has just taken. The ground and the people
    /// are the old country's; the form of state is the victor's, which is the whole point of
    /// having won.
    /// </summary>
    public static string TakeoverName(Campaign campaign, Nation victor, Nation fallen)
    {
        if (campaign is null || victor is null || fallen is null) return fallen?.Name ?? "";
        var victorCulture = DemographyCatalog.PrimaryCulture(victor.Tag);
        var religion = victor.DeclaredReligion;
        if (string.IsNullOrWhiteSpace(religion))
        {
            // A movement that has just taken the state has not declared a faith on paper;
            // the people it rose from have one, and that is what the form of state answers to.
            var culture = DemographyCatalog.Culture(victorCulture);
            if (culture is not null && culture.Religions.Count > 0)
                religion = culture.Religions.OrderByDescending(pair => pair.Value).First().Key;
        }
        var form = FormFor(victor.Government, victor.Ideology, religion, victorCulture);
        var key = "name.found." + form;
        var identity = IdentitySource(fallen);
        // The fallen country's base, never its displayed name: a victor that took over the
        // Republic of India calls the state it now holds the Republic of India, not the
        // Republic of Republic of India.
        var basis = WithoutFormOfState(SourceName(identity.Kind, identity.Id, fallen.FoundNameBase));
        return Loc.Has(key) ? Loc.Format(key, basis) : basis;
    }

    /// <summary>
    /// How much of a founded state's people still live on what its name is for, 0 to 1: the
    /// people's share for a people's name, the land's share for a land, the region's for a
    /// region, the share of ground that is that country's core for a country's. A kind this
    /// cannot measure answers 1 and is never recomposed.
    /// </summary>
    public static float NameHold(Campaign campaign, Nation nation, int[] regions)
    {
        if (campaign is null || nation is null || regions is null || regions.Length == 0) return 1f;
        var wanted = new HashSet<int>(regions);
        var states = campaign.States.Where(state => wanted.Contains(state.MapIndex) && state.Population > 0).ToArray();
        long total = states.Sum(state => (long)state.Population);
        if (total <= 0) return 1f;
        var id = nation.FoundNameId ?? "";
        long held = 0;
        switch (nation.FoundNameKind)
        {
            case "culture":
            case "culture_adj":
                foreach (var state in states)
                    foreach (var (people, size) in EconomySystem.Cultures(state))
                        if (string.Equals(people, id, StringComparison.OrdinalIgnoreCase)) held += size;
                break;
            case "land":
                return LandAtlas.Find(id) is { } land ? LandAtlas.Share(campaign, land, regions) : 1f;
            case "region":
                foreach (var state in states)
                    if (string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase) ||
                        campaign.States.Find(s => s.MapIndex == state.ParentMapIndex) is { } whole &&
                        string.Equals(whole.Id, id, StringComparison.OrdinalIgnoreCase))
                        held += state.Population;
                // A region names the state it is the seat of, while a fifth of the people live in
                // it. A "Military Command of Maharashtra" ruling 712 million is not Maharashtra's.
                return M.Clamp01(held / (float)total * 1.5f);
            case "tag":
            case "free_tag":
                foreach (var state in states)
                    if (state.CoreTags.Contains(id, StringComparer.OrdinalIgnoreCase)) held += state.Population;
                break;
            case "literal" when MovementCatalog.Of(nation.Movement?.Id) is { Peoples.Count: > 0 } movement:
                // The state name a movement fought for ("Belarus") is its peoples' name.
                foreach (var state in states)
                    foreach (var (people, size) in EconomySystem.Cultures(state))
                        if (movement.Peoples.Contains(people, StringComparer.OrdinalIgnoreCase)) held += size;
                break;
            default:
                return 1f;
        }
        return M.Clamp01(held / (float)total);
    }

    /// <summary>
    /// Recompose a founded state's name from the ground it holds now, if the name it has stopped
    /// describing that ground. True when it changed. A typed name is never touched.
    /// </summary>
    public static bool Refit(Campaign campaign, Nation nation, string reason)
    {
        if (campaign is null || nation is null || !string.IsNullOrWhiteSpace(nation.CustomName) ||
            string.IsNullOrEmpty(nation.FoundNameKind)) return false;
        var regions = campaign.StatesOf(nation).Select(state => state.MapIndex).ToArray();
        if (regions.Length == 0) return false;
        if (NameHold(campaign, nation, regions) >= campaign.Defines.NameHoldMinimum) return false;
        var parent = campaign.NationById(nation.SecededFromId) ?? campaign.NationById(nation.ReleasedFromId);
        var culture = !string.IsNullOrWhiteSpace(nation.DeclaredCulture) && IsPeoplesGround(campaign, regions, nation.DeclaredCulture)
            ? nation.DeclaredCulture : GroundPeople(campaign, regions);
        var (kind, id) = BaseFor(campaign, parent, regions, culture, "auto");
        if (string.Equals(kind, nation.FoundNameKind, StringComparison.Ordinal) &&
            string.Equals(id, nation.FoundNameId, StringComparison.OrdinalIgnoreCase)) return false;
        var before = Render(campaign, nation, false);
        nation.FoundNameKind = kind;
        nation.FoundNameId = id;
        nation.FoundNameForm = FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, culture);
        nation.FoundNameBase = BaseName(campaign, nation);
        nation.NameSettledDay = campaign.ElapsedDays;
        AvoidClash(campaign, nation, regions);
        var after = Render(campaign, nation, false);
        if (string.Equals(before, after, StringComparison.Ordinal)) return false;
        campaign.Chronicle.Record(campaign, "state_named", nation.Id, place: after);
        DiagnosticLogs.Write("simulation",
            $"FOUNDING_NAME nation={nation.Tag} reason={reason} kind={kind} id={id} before={before} after={after} day={campaign.ElapsedDays}");
        campaign.MarkStrategicChange();
        campaign.MarkMapChanged();
        return true;
    }

    /// <summary>
    /// Once a month: a founded state that has survived and settled stops calling itself a
    /// council and takes the name its politics have earned; one whose name no longer describes
    /// the ground it holds is renamed from that ground; and once settled, the form of state
    /// follows a government that has held for half a year, so a junta is not a "Republic" and a
    /// restored republic is not a "Military Command".
    /// </summary>
    public static void Tick(Campaign campaign)
    {
        if (campaign is null) return;
        if (campaign.ElapsedDays % 30 != 11) return;
        foreach (var nation in campaign.Nations)
        {
            if (string.IsNullOrEmpty(nation.FoundNameKind) || !string.IsNullOrWhiteSpace(nation.CustomName)) continue;
            if (nation.Movement is null || !nation.Movement.Landless) Refit(campaign, nation, "ground");
            if (nation.NameSettledDay >= 0)
            {
                if (nation.Government != "provisional_government" &&
                    campaign.ElapsedDays - nation.PoliticsChangedDay >= campaign.Defines.NameFormFollowsDays &&
                    !string.Equals(FormFor(nation.Government, nation.Ideology, nation.DeclaredReligion, nation.DeclaredCulture),
                        nation.FoundNameForm, StringComparison.OrdinalIgnoreCase))
                    Settle(campaign, nation, "regime");
                continue;
            }
            if (campaign.ElapsedDays < campaign.Defines.FoundingNameSettleDays) continue;
            if (nation.Stability < campaign.Defines.FoundingSettleStability) continue;
            if (nation.Government == "provisional_government") continue;
            Settle(campaign, nation, "settled");
        }
    }
}

/// <summary>Switch the school a country's name is built from, without typing a name.</summary>
public sealed class NameConventionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int TargetId { get; init; } = -1;
    public string Convention { get; init; } = "auto";

    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign?.NationById(TargetId >= 0 ? TargetId : IssuerId);
        if (nation is null || !NationNaming.KnownConvention(Convention)) return;
        var regions = campaign.StatesOf(nation).Select(state => state.MapIndex).ToArray();
        var (kind, id) = NationNaming.BaseFor(campaign, nation, regions,
            nation.DeclaredCulture, Convention);
        nation.NameConvention = Convention;
        nation.FoundNameKind = kind;
        nation.FoundNameId = id;
        nation.CustomName = "";
        nation.NameSettledDay = -1;
        nation.FoundNameBase = NationNaming.BaseName(campaign, nation);
        campaign.MarkStrategicChange();
        campaign.Chronicle.Record(campaign, "state_restyled", nation.Id,
            place: CountryNames.Resolve(nation, false));
    }
}
