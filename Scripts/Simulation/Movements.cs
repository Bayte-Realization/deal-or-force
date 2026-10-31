using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// One movement as `common/movements` declares it: a movement, a government in exile,
/// an order or a company that is a country in every way the game knows except that it holds no
/// ground. See <see cref="Movements"/>.
/// </summary>
public sealed partial class MovementDefinition
{
    public string Id = "";
    public string Tag = "";
    public string Name = "";
    public string Description = "";
    public string Colour = "#808080";
    /// <summary>The peoples it speaks for: its support, recruits and donations come from them.</summary>
    public readonly List<string> Peoples = new();
    /// <summary>Faiths whose believers lend it a little support even when they are not its peoples.</summary>
    public readonly List<string> Faiths = new();
    private HashSet<string> _peopleLookup;
    private HashSet<string> _faithLookup;
    private string[] _distinctPeoples;
    private string[] _distinctFaiths;

    // The catalogue can spell a people twice. Deduplicate once for the day's regional sums
    // rather than once for every movement's week.
    internal IReadOnlyList<string> DistinctPeoples =>
        _distinctPeoples ??= Peoples.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
    internal IReadOnlyList<string> DistinctFaiths =>
        _distinctFaiths ??= Faiths.Distinct(StringComparer.OrdinalIgnoreCase).ToArray();

    /// <summary>
    /// O(1) membership for the walk over every population group in the world.
    ///
    /// The lists are short, so a linear scan is not a problem once; it is a problem when it
    /// is asked for every group of eighteen hundred regions, which is how a movement's
    /// weekly support drift used to spend its time. The sets are built lazily and dropped
    /// whenever the lists change.
    /// </summary>
    internal bool HasPeople(string name) =>
        name is not null && (_peopleLookup ??= new HashSet<string>(Peoples, StringComparer.OrdinalIgnoreCase)).Contains(name);

    internal bool HasFaith(string name) =>
        name is not null && (_faithLookup ??= new HashSet<string>(Faiths, StringComparer.OrdinalIgnoreCase)).Contains(name);

    internal void ForgetLookups()
    {
        _peopleLookup = null;
        _faithLookup = null;
        _distinctPeoples = null;
        _distinctFaiths = null;
    }
    /// <summary>The region its headquarters stands in on the first day.</summary>
    public string Headquarters = "";
    /// <summary>Territorial homeland. Offices, sponsorship and diaspora presence do not extend it.</summary>
    public readonly List<string> Homeland = new();
    public List<string> Traits { get; private set; } = new();
    /// <summary>Countries that fund it, in billions a year, while they think well enough of it.</summary>
    public readonly Dictionary<string, float> Sponsors = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Support among its peoples in a region on the first day, by region id.</summary>
    public readonly Dictionary<string, float> Support = new(StringComparer.OrdinalIgnoreCase);
    /// <summary>Countries that recognise it on the first day.</summary>
    public readonly List<string> RecognisedBy = new();
    public float Treasury = 0.2f;
    public float Cadres = 5000f;
    public float Legitimacy = 0.2f;
    public string Government = "provisional_government";
    public string Ideology = "nationalism";
    /// <summary>What it calls the country it founds, if not itself.</summary>
    public string StateName = "";
    /// <summary>Optional history/factions id of the same organisation's territorial administration.</summary>
    public string TerritorialFaction = "";
    public bool Playable = true;
    /// <summary>
    /// Not founded with the world: the pattern a country that falls and goes into exile is made
    /// from (`government_in_exile`), its peoples and homeland filled in from the country itself.
    /// </summary>
    public bool Template;
    /// <summary>Some organisations appear only once conditions justify them.</summary>
    public bool StartActive = true;
    public bool CanEmerge;
    public float EmergenceThreshold = .35f;
    public float EmergenceChance = .25f;
    /// <summary>Operational reach from headquarters or a homeland, not a radius of automatic popularity.</summary>
    public float OutreachKm = 800f;
    public float DiasporaReachKm = 3500f;

    public bool Has(string trait) => Listed(Traits, trait);

    /// <summary>Contract work is not a territorial claim. Warlord-company mods must opt in.</summary>
    public bool CanStageUprising => !Has("civic_independence") && (!Has("for_hire") || Has("territorial_ambition"));

    /// <summary>
    /// Whether a list of names holds this one, whatever its case. A loop rather than
    /// Enumerable.Contains, which with a comparer boxes an enumerator on every call - and this
    /// is asked of every population group of every region, by every movement, every week.
    /// </summary>
    public static bool Listed(List<string> names, string name)
    {
        if (names is null || name is null) return false;
        for (var index = 0; index < names.Count; index++)
            if (string.Equals(names[index], name, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }
}

/// <summary>`common/movements`, keyed by id, later files winning key by key.</summary>
public static class MovementCatalog
{
    private static readonly SortedDictionary<string, MovementDefinition> ById = new(StringComparer.Ordinal);

    static MovementCatalog() => ResetCatalog();

    public static IEnumerable<MovementDefinition> All => ById.Values;

    public static MovementDefinition Of(string id) =>
        id is not null && ById.TryGetValue(id, out var definition) ? definition : null;

    public static void ResetCatalog()
    {
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "movements"))
            using (document)
                Merge(document.RootElement, null);
    }

    /// <summary>Read one file's worth. A key already known is changed field by field.</summary>
    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!ById.TryGetValue(entry.Name, out var definition))
                ById[entry.Name] = definition = new MovementDefinition { Id = entry.Name };
            var value = entry.Value;
            string Text(string name, string fallback) =>
                value.TryGetProperty(name, out var property) && property.ValueKind == JsonValueKind.String
                    ? property.GetString() ?? fallback : fallback;
            float Number(string name, float fallback) =>
                value.TryGetProperty(name, out var property) && property.TryGetSingle(out var number) ? number : fallback;
            void Strings(string name, List<string> into)
            {
                if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Array) return;
                into.Clear();
                foreach (var item in property.EnumerateArray())
                    if (item.ValueKind == JsonValueKind.String && !string.IsNullOrWhiteSpace(item.GetString()))
                        into.Add(item.GetString());
            }
            void Numbers(string name, Dictionary<string, float> into)
            {
                if (!value.TryGetProperty(name, out var property) || property.ValueKind != JsonValueKind.Object) return;
                into.Clear();
                foreach (var item in property.EnumerateObject())
                    if (item.Value.TryGetSingle(out var number)) into[item.Name] = number;
            }

            definition.Tag = Text("tag", definition.Tag);
            definition.Name = Text("name", definition.Name);
            definition.Description = Text("description", definition.Description);
            definition.Colour = Text("colour", definition.Colour);
            definition.Headquarters = Text("headquarters", definition.Headquarters);
            definition.Government = Text("government", definition.Government);
            definition.Ideology = Text("ideology", definition.Ideology);
            definition.StateName = Text("state_name", definition.StateName);
            definition.TerritorialFaction = Text("territorial_faction", definition.TerritorialFaction);
            definition.Treasury = Number("treasury", definition.Treasury);
            definition.Cadres = Number("cadres", definition.Cadres);
            definition.Legitimacy = M.Clamp01(Number("legitimacy", definition.Legitimacy));
            if (value.TryGetProperty("playable", out var playable) && playable.ValueKind is JsonValueKind.True or JsonValueKind.False)
                definition.Playable = playable.GetBoolean();
            if (value.TryGetProperty("template", out var template) && template.ValueKind is JsonValueKind.True or JsonValueKind.False)
                definition.Template = template.GetBoolean();
            if (value.TryGetProperty("start_active", out var startActive) && startActive.ValueKind is JsonValueKind.True or JsonValueKind.False)
                definition.StartActive = startActive.GetBoolean();
            if (value.TryGetProperty("can_emerge", out var emerge) && emerge.ValueKind is JsonValueKind.True or JsonValueKind.False)
                definition.CanEmerge = emerge.GetBoolean();
            definition.EmergenceThreshold = M.Clamp01(Number("emergence_threshold", definition.EmergenceThreshold));
            definition.EmergenceChance = M.Clamp01(Number("emergence_chance", definition.EmergenceChance));
            definition.OutreachKm = M.Clamp(Number("outreach_km", definition.OutreachKm), 50f, 20000f);
            definition.DiasporaReachKm = M.Clamp(Number("diaspora_reach_km", definition.DiasporaReachKm), 50f, 20000f);
            Strings("peoples", definition.Peoples);
            Strings("faiths", definition.Faiths);
            definition.ForgetLookups();
            Strings("homeland", definition.Homeland);
            Strings("traits", definition.Traits);
            Strings("recognised_by", definition.RecognisedBy);
            Numbers("sponsors", definition.Sponsors);
            Numbers("support", definition.Support);
            if (string.IsNullOrWhiteSpace(definition.Tag) && !definition.Template)
                errors?.Add($"movements/{entry.Name}: needs a \"tag\", the three letters its country goes by.");
        }
        Version++;
    }

    /// <summary>Bumped on every change, so what is made from a template is made again.</summary>
    internal static int Version { get; private set; }

    /// <summary>The pattern a fallen country's government in exile is made from.</summary>
    public const string ExileTemplate = "government_in_exile";
    /// <summary>A beaten rising that went to ground (MovementFall.GoUnderground): its id begins so, and the template fills it in.</summary>
    public const string UndergroundTemplate = "insurgency";
    public const string UndergroundPrefix = "underground_";
}

/// <summary>Fighters on their way to a region ahead of an uprising (the infiltrate action).</summary>
public sealed class Infiltration
{
    public int Region;
    public float Fighters;
    public int ArrivesOnDay;
}

/// <summary>What makes a country a movement, kept on the country and saved with it.</summary>
public sealed partial class MovementState
{
    /// <summary>The id in common/movements.</summary>
    public string Id = "";

    /// <summary>True while it holds no ground; an uprising that takes a region makes it a country like any other.</summary>
    public bool Landless = true;

    /// <summary>Where its headquarters stands: a region, and the point in it the map draws.</summary>
    public int HeadquartersStateMapIndex;
    public float HeadquartersLatitude;
    public float HeadquartersLongitude;
    /// <summary>The province it stands on, so headquarters in one city are drawn together.</summary>
    public int HeadquartersProvinceId;

    /// <summary>The region it is moving the headquarters to, and the day it arrives; -1 for none.</summary>
    public int MovingToStateMapIndex = -1;
    public int ArrivesOnDay = -1;

    /// <summary>People it can call on - organisers, volunteers, fighters. Uprisings are made of them.</summary>
    public float Cadres;

    /// <summary>How far the world takes it seriously, 0 to 1. Donations, recognition and hosts answer to it.</summary>
    public float Legitimacy;

    /// <summary>How much its host wants it gone, 0 to 1. At the top the host expels it.</summary>
    public float Heat;

    /// <summary>How far it will go, 0 (a peaceful campaign) to 1 (terror); -1 until the first
    /// scheduled review. Reads use its initial traits without changing the saved value.</summary>
    public float Militancy = -1f;

    /// <summary>Support among its peoples, by region map index, 0 to 1.</summary>
    public readonly Dictionary<int, float> Support = new();

    /// <summary>The countries that recognise it, by id.</summary>
    public readonly HashSet<int> RecognisedBy = new();

    /// <summary>The first day each action may be used again, by action id (and region for the regional ones).</summary>
    public readonly Dictionary<string, int> ReadyOnDay = new(StringComparer.Ordinal);

    /// <summary>Last week's income, in billions, and where it came from - for the panel.</summary>
    public float IncomeWeekly;
    public float DonationsWeekly;
    public float SponsorsWeekly;

    /// <summary>The day it first held ground, or -1.</summary>
    public int StatehoodDay = -1;

    /// <summary>A company's client and the last day it pays; -1 for none (the for_hire trait).</summary>
    public int ContractNationId = -1;
    public int ContractUntilDay = -1;

    /// <summary>What happened last, for the panel and the news.</summary>
    public string LastReport = "";

    /// <summary>
    /// A country that lost the last of its ground and governs from abroad. It has no catalogue
    /// entry: it is made from the exile template, with its own peoples and homeland below.
    /// </summary>
    public bool Exile;
    public readonly List<string> Peoples = new();
    /// <summary>The regions, by id, it held and means to go home to.</summary>
    public readonly List<string> Homeland = new();

    /// <summary>No more: nowhere left that would have it, or nothing left of it.</summary>
    public bool Dissolved;
    /// <summary>Weeks in a row it has had nowhere to stand.</summary>
    public int HomelessWeeks;

    /// <summary>Fighters already hidden in a region, by map index, and those still on the way.</summary>
    public readonly Dictionary<int, float> Infiltrated = new();
    public readonly List<Infiltration> OnTheWay = new();

    /// <summary>Countries that fund it by their own choice, in billions a year, by id.</summary>
    public readonly Dictionary<int, float> SponsoredBy = new();
    /// <summary>Countries that have listed it as a terrorist organisation, by id.</summary>
    public readonly HashSet<int> DesignatedBy = new();
    /// <summary>Countries that have offered its headquarters a home, by id: the move there needs no opinion.</summary>
    public readonly HashSet<int> InvitedBy = new();
}

public sealed partial class Nation
{
    /// <summary>Set for a movement - landless or not - and null for every other country.</summary>
    public MovementState Movement;

    /// <summary>A movement that holds no ground.</summary>
    [JsonIgnore]
    public bool Landless => Movement is not null && Movement.Landless;
}

public sealed partial class Campaign
{
    /// <summary>Bumped whenever a headquarters moves, appears or goes, so the map can redraw its markers.</summary>
    [JsonIgnore]
    public int MovementRevision { get; internal set; }

    /// <summary>
    /// The regions each country held when it was first seen holding any, by nation id and region
    /// map index: the ground a government in exile means to go home to.
    /// </summary>
    public readonly Dictionary<int, List<int>> Homelands = new();

    /// <summary>Countries whose fall has been decided - into exile, or gone - by id.</summary>
    public readonly HashSet<int> Fallen = new();
}

/// <summary>
/// Countries without land: movements, governments in exile, orders and companies.
///
/// A movement is a country - it has a tag, a treasury, relations, opinions, spirits and
/// decisions, and a player can be it - that holds no ground. Instead it has a headquarters,
/// a point on the map in somebody else's country that it can move; a following among the
/// peoples it speaks for, region by region; cadres; legitimacy; and a host whose patience it
/// spends. It lives off donations and sponsors, and it acts: it campaigns, raises money,
/// recruits, seeks recognition - and, where its support runs high enough, it rises. An
/// uprising that succeeds gives it the region: it becomes a country like any other, with a
/// capital where the headquarters was, militia from its cadres and a war with the country it
/// rose against. One that loses all its ground again goes back to being landless.
///
/// Each one has traits (common/movements) that change the rules for it; see
/// <see cref="MovementRules"/>.
/// </summary>
public static partial class Movements
{
    /// <summary>
    /// Found every movement the catalogue declares that the world does not have yet - on
    /// a new campaign, once its map is attached. A save brings its own back and is left alone.
    /// </summary>
    public static void Found(Campaign campaign)
    {
        if (campaign is null || campaign.ElapsedDays > 0) return;
        FoundDefinitions(campaign, MovementCatalog.All.Where(d => d.StartActive));
    }

    public static void Activate(Campaign campaign, MovementDefinition definition)
    {
        if (campaign is not null && definition is not null && campaign.FeatureEnabled("movements"))
            FoundDefinitions(campaign, new[] { definition });
    }

    private static void FoundDefinitions(Campaign campaign, IEnumerable<MovementDefinition> definitions)
    {
        var founded = 0;
        foreach (var definition in definitions)
        {
            if (string.IsNullOrWhiteSpace(definition.Tag) || definition.Template) continue;
            if (TerritorialActor(campaign, definition) is not null) continue;
            var existing = campaign.Nations.FirstOrDefault(nation => nation.Movement?.Id == definition.Id ||
                                               string.Equals(nation.Tag, definition.Tag, StringComparison.OrdinalIgnoreCase));
            if (existing is not null && (existing.Movement is not { Dissolved: true } || existing.IsHuman || campaign.StatesOf(existing).Length > 0))
                continue;
            var headquarters = StateById(campaign, definition.Headquarters);
            if (headquarters is null) continue;
            var mapIndex = existing?.MapIndex ?? FreeMapIndex(campaign);
            if (mapIndex <= 0) continue;

            var nation = existing ?? campaign.AddNation(new Nation
            {
                Tag = definition.Tag,
                Iso = definition.Tag,
                Name = definition.Name,
                Color = definition.Colour,
                MapIndex = mapIndex,
                CapitalName = headquarters.Name,
                Population = 0,
                Gdp = 0.01f,
                Treasury = definition.Treasury,
                ManpowerReserve = definition.Cadres,
                OutputScale = 1f,
                Stability = 0.6f,
                WarSupport = 0.8f,
                Government = definition.Government,
                Ideology = definition.Ideology,
                PoliticsChangedDay = 0
            });
            nation.Movement = new MovementState
            {
                Id = definition.Id,
                HeadquartersStateMapIndex = headquarters.MapIndex,
                Cadres = definition.Cadres,
                Legitimacy = definition.Legitimacy
            };
            nation.Movement.ReadyOnDay["lifecycle_born"] = campaign.ElapsedDays;
            if (existing is not null)
            {
                nation.Treasury = definition.Treasury;
                nation.ManpowerReserve = definition.Cadres;
                nation.Government = definition.Government;
                nation.Ideology = definition.Ideology;
                campaign.Fallen.Remove(nation.Id);
            }
            foreach (var (regionId, share) in definition.Support)
                if (StateById(campaign, regionId) is { } region) nation.Movement.Support[region.MapIndex] = M.Clamp01(share);
            foreach (var tag in definition.RecognisedBy)
                if (campaign.NationByTag(tag) is { } recogniser) nation.Movement.RecognisedBy.Add(recogniser.Id);
            if (definition.Peoples.Count > 0) DemographyCatalog.Declare(nation.Tag, definition.Peoples);
            nation.FoundedCultures.Clear();
            nation.FoundedCultures.AddRange(definition.Peoples);
            founded++;
            campaign.MovementRevision++;
            if (campaign.ElapsedDays > 0)
            {
                nation.Movement.LastReport = Loc.Get("A new civic organisation has formed around local demands.");
                campaign.Chronicle.Record(campaign, "movement_emerged", nation.Id);
            }
        }
        if (founded > 0) { Place(campaign); campaign.MarkStrategicChange(); }
    }

    /// <summary>Put every landless headquarters where its region is on the map.</summary>
    public static void Place(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null) return;
        foreach (var nation in campaign.Nations)
        {
            var movement = nation.Movement;
            if (movement is null || !movement.Landless) continue;
            var region = campaign.States.Find(state => state.MapIndex == movement.HeadquartersStateMapIndex);
            if (region is null) continue;
            // On a province, not on the region's average: the middle of a coastline or an
            // archipelago is the sea, and a government in exile in London was drawn in the
            // North Sea. The host's capital when it stands in this region - that is where
            // such offices are - and otherwise the province nearest the region's middle.
            var seat = SeatOf(campaign, region);
            movement.HeadquartersProvinceId = seat;
            var (latitude, longitude) = seat > 0
                ? (campaign.ProvinceMap.CenterOf(seat).Latitude, campaign.ProvinceMap.CenterOf(seat).Longitude)
                : CentreOf(campaign, region);
            // In a city, where offices are: the region's own main city, or the great city the
            // region surrounds - the Mejlis and Ichkeria sat in a field south-west of Kyiv, at
            // the middle of Kyiv oblast, with Kyiv itself twenty kilometres away.
            var city = HeadquartersCity(campaign, region, latitude, longitude);
            if (city is not null)
            {
                (latitude, longitude) = (city.Latitude, city.Longitude);
                var under = CityCatalog.ProvinceUnder(campaign.ProvinceMap, latitude, longitude);
                if (under > 0) movement.HeadquartersProvinceId = under;
            }
            movement.HeadquartersLatitude = latitude;
            movement.HeadquartersLongitude = longitude;
            nation.CapitalLatitude = latitude;
            nation.CapitalLongitude = longitude;
            nation.CapitalName = city?.Name ?? region.DisplayName;
        }
        campaign.MovementRevision++;
    }

    /// <summary>Every movement in the world, landless or not, dissolved or not. An exile that has returned home and holds sovereign land is governed as a state, not an active movement.</summary>
    public static IEnumerable<Nation> Of(Campaign campaign) =>
        campaign.Nations.Where(nation => nation.Movement is not null && !(nation.Movement.Exile && !nation.Movement.Landless));

    /// <summary>The movements that still exist.</summary>
    public static IEnumerable<Nation> Active(Campaign campaign) =>
        campaign.Nations.Where(nation => nation.Movement is { Dissolved: false } && !(nation.Movement.Exile && !nation.Movement.Landless));

    public static MovementDefinition DefinitionOf(Nation nation)
    {
        var basic = BaseDefinitionOf(nation);
        var state = nation?.Movement;
        if (basic is null || state.GainedTraits.Count == 0 && state.LostTraits.Count == 0) return basic;
        // What it has become (MovementTraits): the catalogue's traits, less those shed, plus those
        // taken on. Kept until either list or the catalogue changes.
        var key = state.TraitsVersion;
        if (Become.TryGetValue(state, out var cached) && ReferenceEquals(cached.Item1, basic) && cached.Item2 == key)
            return cached.Item3;
        var traits = basic.Traits.Where(t => !state.LostTraits.Contains(t)).Concat(state.GainedTraits.Where(t => !MovementDefinition.Listed(basic.Traits, t)));
        var become = basic.WithTraits(traits);
        Become.AddOrUpdate(state, Tuple.Create(basic, key, become));
        return become;
    }

    /// <summary>The movement as the catalogue (or its exile template) wrote it, before play changed it.</summary>
    public static MovementDefinition BaseDefinitionOf(Nation nation)
    {
        var state = nation?.Movement;
        if (state is null) return null;
        return state.Exile ? ExileDefinition(nation) : MovementCatalog.Of(state.Id);
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MovementState, Tuple<MovementDefinition, int, MovementDefinition>> Become = new();

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<MovementState, Tuple<int, MovementDefinition>> Exiles = new();

    /// <summary>A government in exile, as the template makes it and its own country fills it in.</summary>
    private static MovementDefinition ExileDefinition(Nation nation)
    {
        var state = nation.Movement;
        if (Exiles.TryGetValue(state, out var cached) && cached.Item1 == MovementCatalog.Version) return cached.Item2;
        var underground = state.Id.StartsWith(MovementCatalog.UndergroundPrefix, StringComparison.Ordinal);
        var template = MovementCatalog.Of(underground ? MovementCatalog.UndergroundTemplate : MovementCatalog.ExileTemplate);
        var description = template?.Description ?? "";
        if (!state.Landless)
            description = Loc.Format("The legal government of {0} has returned to its homeland and resumed governance over its people.", nation.Name);
        var definition = new MovementDefinition
        {
            Id = state.Id,
            Tag = nation.Tag,
            Name = nation.Name,
            Description = description,
            Colour = nation.Color,
            Government = nation.Government,
            Ideology = nation.Ideology,
            Legitimacy = template?.Legitimacy ?? 0.5f,
            Playable = true
        };
        definition.Peoples.AddRange(state.Peoples);
        definition.Homeland.AddRange(state.Homeland);
        definition.Traits.AddRange(template is not null ? template.Traits
            : underground ? new[] { "armed_wing", "tunnel_network" } : new[] { "government_in_exile" });
        if (template is not null) definition.Faiths.AddRange(template.Faiths);
        definition.ForgetLookups();
        Exiles.AddOrUpdate(state, Tuple.Create(MovementCatalog.Version, definition));
        return definition;
    }

    /// <summary>The country whose ground the headquarters stands on.</summary>
    public static Nation HostOf(Campaign campaign, Nation movement)
    {
        var state = movement?.Movement is null ? null :
            campaign.States.Find(candidate => candidate.MapIndex == movement.Movement.HeadquartersStateMapIndex);
        return state is null ? null : campaign.Nations.Find(nation => nation.MapIndex == state.NationMapIndex);
    }

    private static readonly Dictionary<string, ScenarioState> StatesById = new(StringComparer.OrdinalIgnoreCase);
    private static List<ScenarioState> _statesByIdList;
    private static int _statesByIdCount = -1;

    /// <summary>
    /// A region by its id, the first in the list with it - which is what a search of the list
    /// gave. From an index now: the countries' weekly look at every movement asked this for
    /// every region every movement claims, for every pair of country and movement, twice -
    /// tens of millions of string comparisons a week, the week's largest frame. A hit is
    /// checked against the region's id as it is now, and a miss searches the list as before,
    /// so a region renamed or added since the index was built is still found.
    /// </summary>
    internal static ScenarioState StateById(Campaign campaign, string id)
    {
        if (string.IsNullOrWhiteSpace(id)) return null;
        var states = campaign.States;
        if (!ReferenceEquals(_statesByIdList, states) || _statesByIdCount != states.Count)
        {
            StatesById.Clear();
            foreach (var state in states) StatesById.TryAdd(state.Id ?? "", state);
            _statesByIdList = states;
            _statesByIdCount = states.Count;
        }
        if (StatesById.TryGetValue(id, out var found) && string.Equals(found.Id, id, StringComparison.OrdinalIgnoreCase))
            return found;
        return states.Find(state => string.Equals(state.Id, id, StringComparison.OrdinalIgnoreCase));
    }

    /// <summary>The province a headquarters in this region stands on: the host's capital if it is here, else the one nearest the middle.</summary>
    /// <summary>
    /// The city a headquarters in this region stands in: the largest town of the region, or a
    /// greater city just outside it that the region surrounds - weighed so that Kyiv beats Bila
    /// Tserkva for an office "in Kyiv oblast". Nothing when the map has no city near.
    /// </summary>
    internal static CityDefinition HeadquartersCity(Campaign campaign, ScenarioState region, float latitude, float longitude)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || region is null) return null;
        CityDefinition best = null;
        var bestScore = 0.0;
        foreach (var city in CityCatalog.All)
        {
            if (city.Tier > 2) continue;
            var dLat = city.Latitude - latitude;
            var dLon = (city.Longitude - longitude) * MathF.Cos(latitude * MathF.PI / 180f);
            var distance = MathF.Sqrt(dLat * dLat + dLon * dLon);
            if (distance > 1.6f) continue;
            var province = CityCatalog.ProvinceUnder(topology, city.Latitude, city.Longitude);
            var inside = province > 0 && topology.ProvinceToState[province] == region.MapIndex;
            if (!inside && distance > 0.9f) continue;
            var score = Math.Max(1L, city.Population) * (inside ? 1.0 : 0.6) / (1.0 + distance);
            if (score <= bestScore) continue;
            bestScore = score;
            best = city;
        }
        return best;
    }

    internal static int SeatOf(Campaign campaign, ScenarioState region)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || region is null) return 0;
        var host = campaign.Nations.Find(nation => nation.MapIndex == region.NationMapIndex);
        if (host is not null && host.CapitalProvinceId > 0 && host.CapitalProvinceId <= topology.ProvinceCount &&
            topology.ProvinceToState[host.CapitalProvinceId] == region.MapIndex)
            return host.CapitalProvinceId;
        // The middle averaged on the circle, so a region across the date line does not average
        // to the far side of the world; then the region's own province closest to it.
        double latitude = 0, x = 0, y = 0;
        var count = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)region.MapIndex) continue;
            var centre = topology.CenterOf(province);
            latitude += centre.Latitude;
            var radians = centre.Longitude * Math.PI / 180.0;
            x += Math.Cos(radians);
            y += Math.Sin(radians);
            count++;
        }
        if (count == 0) return 0;
        var midLatitude = (float)(latitude / count);
        var midLongitude = (float)(Math.Atan2(y, x) * 180.0 / Math.PI);
        var scale = MathF.Max(0.12f, MathF.Cos(midLatitude * MathF.PI / 180f));
        var best = float.MaxValue;
        var seat = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)region.MapIndex) continue;
            var centre = topology.CenterOf(province);
            var dx = MathF.Abs(centre.Longitude - midLongitude);
            dx = MathF.Min(dx, 360f - dx) * scale;
            var dy = centre.Latitude - midLatitude;
            var distance = dx * dx + dy * dy;
            if (distance >= best) continue;
            best = distance;
            seat = province;
        }
        return seat;
    }

    internal static (float Latitude, float Longitude) CentreOf(Campaign campaign, ScenarioState region)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null || region is null) return (0f, 0f);
        double latitude = 0, longitude = 0;
        var counted = 0;
        for (var province = 1; province <= topology.ProvinceCount; province++)
        {
            if (topology.ProvinceToState[province] != (ushort)region.MapIndex) continue;
            var centre = topology.CenterOf(province);
            latitude += centre.Latitude;
            longitude += centre.Longitude;
            counted++;
        }
        return counted == 0 ? (0f, 0f) : ((float)(latitude / counted), (float)(longitude / counted));
    }

    /// <summary>
    /// The lowest map index no country and no movement is using. Shared with anything that
    /// founds a new country at runtime, so two founders cannot be handed the same index.
    /// </summary>
    internal static int FreeMapIndex(Campaign campaign)
    {
        var taken = new HashSet<int>();
        foreach (var nation in campaign.Nations) taken.Add(nation.MapIndex);
        for (var index = FirstMapIndex; index < 65535; index++)
            if (!taken.Contains(index)) return index;
        return 0;
    }

    /// <summary>Where movements number from: above the authored countries and below the ones a campaign founds.</summary>
    public const int FirstMapIndex = 900;
}
