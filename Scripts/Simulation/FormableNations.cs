using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record FormableNation(string Id, string Name, string Description, string[] EligibleTags,
    string[] RequiredTags, int[] RequiredProvinces, float MinimumStability, float CostShare,
    float GovernanceGain, float ProductivityGain,
    string[] RequiredIdeology, string[] RequiredGovernment, string Colour)
{
    public bool PeacefulAccession { get; init; }
    public bool SubjectIntegration { get; init; }
    public bool AiFoundation { get; init; }
    public string[] EligibleCultures { get; init; } = Array.Empty<string>();
    /// <summary>Families of peoples any of whose states may found it: "south_slavic" opens
    /// Yugoslavia to every South Slavic state without naming each (common/cultures "group").</summary>
    public string[] EligibleCultureGroups { get; init; } = Array.Empty<string>();
    /// <summary>Faiths whose states may found it - a caliphate, a Christian union - by the faith
    /// the founder's own people mostly hold.</summary>
    public string[] EligibleReligions { get; init; } = Array.Empty<string>();
    /// <summary>Optional civic mandate from actual residents, not an assumption about a tag.</summary>
    public string[] RequiredReligions { get; init; } = Array.Empty<string>();
    public string[] RequiredReligionGroups { get; init; } = Array.Empty<string>();
    public float MinimumReligionShare { get; init; } = .5f;
    /// <summary>For a restoration (FormableRestorations), the vanished country it brings back.</summary>
    public string Restores { get; init; } = "";
    /// <summary>Legacy duplicate identity: retained for saves, omitted from new catalogues.</summary>
    public string AliasOf { get; init; } = "";
    /// <summary>Regions by id, for a state whose ground the scenario writes under other
    /// countries' tags - a Kurdistan of Erbil, Duhok and Sulaymaniyah.</summary>
    public string[] RequiredRegions { get; init; } = Array.Empty<string>();
    public string[] Supersedes { get; init; } = Array.Empty<string>();
    /// <summary>
    /// The share of the foundation territory the founders must hold, as Victoria 3 asks for
    /// most of a homeland and not every last county of it. Negative means the defaults:
    /// FormableRequiredShare, or FormableRestorationShare for a restoration.
    /// </summary>
    public float RequiredShare { get; init; } = -1f;
    /// <summary>Optional per-component floor. A tiny partner must not disappear in a
    /// continental total (China cannot count Taiwan as a rounding error).</summary>
    public float MinimumComponentShare { get; init; } = .5f;
}
public sealed record FormationRequirement(string Label, bool Met);
public sealed record FormationStatus(FormationRequirement[] Requirements, int Owned, int Total, float Cost)
{
    /// <summary>How many of the Total provinces must be held: Total times the formable's share.</summary>
    public int Required { get; init; } = -1;
    public bool CanForm => Requirements.Length > 0 && Requirements.All(r => r.Met);
}

/// <summary>Territories refer to authored scenario ownership, not shifting live borders.</summary>
public static class FormableNations
{
    private static readonly Dictionary<string, FormableNation> Definitions = new(StringComparer.Ordinal);
    private static IReadOnlyList<FormableNation> _visible;
    internal static IReadOnlyList<FormableNation> AllList => _visible ??= Array.AsReadOnly(Definitions.Values
        .Where(d => d.AliasOf.Length == 0).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray());
    public static IEnumerable<FormableNation> All => AllList;
    static FormableNations() => ResetCatalog();
    public static void ResetCatalog()
    {
        Definitions.Clear();
        _visible = null;
        foreach (var doc in GameDataLoader.DataDocuments("common", "formable_nations"))
            using (doc) Merge(doc.RootElement, null);
    }
    public static FormableNation Find(string id) => id is not null ? Definitions.GetValueOrDefault(id) : null;

    /// <summary>One state by id, a restoration included: those exist only in a campaign.</summary>
    public static FormableNation Find(Campaign campaign, string id) =>
        Find(id) ?? FormableRestorations.Find(campaign, id);

    /// <summary>Every state that may be proclaimed in this campaign: the catalogue, and every
    /// country of the starting world that has vanished and may be brought back.</summary>
    public static IEnumerable<FormableNation> AvailableIn(Campaign campaign) =>
        All.Concat(FormableRestorations.In(campaign));
    /// <summary>
    /// Whether a listed faith is the one a population holds, compared as families: a formable
    /// naming "lutheran" is met by Protestant population groups, and one naming "protestant"
    /// by every Protestant denomination.
    /// </summary>
    internal static bool HasFaith(string[] items, string religion)
    {
        if (items is null || religion is null) return false;
        for (var i = 0; i < items.Length; i++)
            if (StateIdentity.SameFaith(items[i], religion)) return true;
        return false;
    }

    internal static bool ContainsIgnoreCase(string[] items, string value)
    {
        if (items is null || value is null) return false;
        for (var i = 0; i < items.Length; i++)
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool ContainsIgnoreCase(List<string> items, string value)
    {
        if (items is null || value is null) return false;
        for (var i = 0; i < items.Count; i++)
            if (string.Equals(items[i], value, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    private static bool MatchesCultureGroupOrFaith(string people, FormableNation definition)
    {
        if (definition.EligibleCultureGroups.Length > 0)
        {
            var culture = DemographyCatalog.Culture(people);
            if (culture is not null && ContainsIgnoreCase(definition.EligibleCultureGroups, culture.Group))
                return true;
        }
        if (definition.EligibleReligions.Length > 0)
        {
            var faiths = DemographyCatalog.FaithOf(people);
            if (faiths.Count > 0 && faiths[0].Share >= 0.5f &&
                HasFaith(definition.EligibleReligions, faiths[0].Religion))
                return true;
        }
        return false;
    }

    public static bool CompatibleProclamation(Nation nation, FormableNation definition) =>
        nation is not null && definition is not null && definition.AliasOf.Length == 0 &&
        !nation.FormedNationHistory.Contains(definition.Id) &&
        (string.IsNullOrEmpty(nation.FormedNationId) || ContainsIgnoreCase(definition.Supersedes, nation.FormedNationId));
    public static bool Eligible(Nation nation, FormableNation definition) => Eligible(null, nation, definition);

    /// <summary>
    /// Who may found it: a country named; a state of a people named; a state of a family of
    /// peoples named; a state whose people mostly hold a faith named. For a restoration, the
    /// rules of FormableRestorations.MayRestore - its own people, a rising on its ground, its
    /// government in exile - and never the country that holds it otherwise.
    /// </summary>
    public static bool Eligible(Campaign campaign, Nation nation, FormableNation definition)
    {
        if (nation is null || definition is null) return false;
        if (!string.IsNullOrEmpty(definition.Restores))
            return FormableRestorations.MayRestore(campaign, nation, definition.Restores);
        if (ContainsIgnoreCase(definition.EligibleTags, nation.Tag)) return true;
        var cultures = definition.EligibleCultures;
        for (var i = 0; i < cultures.Length; i++)
        {
            var c = cultures[i];
            if (DemographyCatalog.IsPrimary(nation.Tag, c) || ContainsIgnoreCase(nation.FoundedCultures, c))
                return true;
        }
        if (definition.EligibleCultureGroups.Length == 0 && definition.EligibleReligions.Length == 0) return false;
        var primary = DemographyCatalog.PrimaryCultures(nation.Tag);
        for (var i = 0; i < primary.Count; i++)
            if (MatchesCultureGroupOrFaith(primary[i], definition)) return true;
        var founded = nation.FoundedCultures;
        for (var i = 0; i < founded.Count; i++)
            if (MatchesCultureGroupOrFaith(founded[i], definition)) return true;
        return false;
    }
    public static void Merge(JsonElement root, List<string> errors)
    {
        _visible = null;
        MergeInto(root, errors, Definitions);
    }

    /// <summary>The declared faiths/groups form one union: no resident is counted twice.
    /// Only current, owned-state population groups count. Missing religion data fails closed;
    /// it must not turn a culture stereotype or an empty state into a founding mandate.</summary>
    public static float ReligionShare(Campaign campaign, Nation nation, FormableNation definition)
    {
        if (campaign is null || nation is null || definition is null) return 0f;
        var hasReligions = definition.RequiredReligions.Length > 0;
        var hasGroups = definition.RequiredReligionGroups.Length > 0;
        double total = 0, matching = 0;
        foreach (var state in campaign.StatesOf(nation))
        {
            double represented = 0;
            foreach (var population in state.PopulationGroups)
            {
                if (population is null || population.Size <= 0) continue;
                represented += population.Size;
                if ((hasReligions && HasFaith(definition.RequiredReligions, population.Religion ?? "")) ||
                    (hasGroups && ContainsIgnoreCase(definition.RequiredReligionGroups, DemographyCatalog.Religion(population.Religion)?.Group ?? "")))
                    matching += population.Size;
            }
            // A partial scenario profile must not make its unrepresented residents vanish
            // from the denominator. Counts above the regional total remain bounded too.
            total += Math.Max(Math.Max(0L, state.Population), represented);
        }
        return total > 0 ? (float)(matching / total) : 0f;
    }

    public static bool ReligionMandate(Campaign campaign, Nation nation, FormableNation definition) =>
        definition is not null && (definition.RequiredReligions.Length == 0 && definition.RequiredReligionGroups.Length == 0 ||
            ReligionShare(campaign, nation, definition) >= definition.MinimumReligionShare);

    /// <summary>Validate an editor overlay against current data without changing the
    /// live catalogue. Bad drafts cannot delete or rewrite countries in a running game.</summary>
    public static void Validate(JsonElement root, List<string> errors) =>
        MergeInto(root, errors, new Dictionary<string, FormableNation>(Definitions, StringComparer.Ordinal));

    private static void MergeInto(JsonElement root, List<string> errors, Dictionary<string, FormableNation> definitions)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("formable_nations: expected object"); return; }
        foreach (var entry in root.EnumerateObject())
        {
            try
            {
                var e = entry.Value;
                if (e.ValueKind != JsonValueKind.Object) throw new FormatException("expected object");
                if (e.TryGetProperty("disabled", out var disabled) && disabled.ValueKind == JsonValueKind.True)
                { definitions.Remove(entry.Name); continue; }
                var old = definitions.GetValueOrDefault(entry.Name);
                string Text(string key, string fallback) => e.TryGetProperty(key, out var x) ? x.GetString() : fallback;
                string[] Tags(string key, string[] fallback) => e.TryGetProperty(key, out var x)
                    ? x.EnumerateArray().Select(v => v.GetString()?.ToUpperInvariant() ?? "").Where(v => v.Length > 0).Distinct().ToArray()
                    : fallback ?? Array.Empty<string>();
                float Number(string key, float fallback) => e.TryGetProperty(key, out var x) ? x.GetSingle() : fallback;
                // Lower case rather than upper: these name ideologies and forms of
                // government, which are ids in common/, not country tags.
                string[] Ids(string key, string[] fallback) => e.TryGetProperty(key, out var x)
                    ? x.EnumerateArray().Select(v => v.GetString()?.ToLowerInvariant() ?? "").Where(v => v.Length > 0).Distinct().ToArray()
                    : fallback ?? Array.Empty<string>();
                var d = new FormableNation(entry.Name, Text("name", old?.Name ?? entry.Name), Text("description", old?.Description ?? ""),
                    Tags("eligible_tags", old?.EligibleTags), Tags("required_tags", old?.RequiredTags),
                    e.TryGetProperty("required_provinces", out var ps) ? ps.EnumerateArray().Select(p => p.GetInt32()).Distinct().ToArray() : old?.RequiredProvinces ?? Array.Empty<int>(),
                    Number("minimum_stability", old?.MinimumStability ?? .65f), Number("gdp_cost_share", old?.CostShare ?? .02f),
                    Number("governance_gain", old?.GovernanceGain ?? .04f), Number("productivity_gain", old?.ProductivityGain ?? .015f),
                    Ids("required_ideology", old?.RequiredIdeology), Ids("required_government", old?.RequiredGovernment),
                    Text("colour", old?.Colour ?? ""))
                {
                    EligibleCultures = Ids("eligible_cultures", old?.EligibleCultures),
                    EligibleCultureGroups = Ids("eligible_culture_groups", old?.EligibleCultureGroups),
                    EligibleReligions = Ids("eligible_religions", old?.EligibleReligions),
                    RequiredReligions = Ids("required_religions", old?.RequiredReligions),
                    RequiredReligionGroups = Ids("required_religion_groups", old?.RequiredReligionGroups),
                    MinimumReligionShare = Number("minimum_religion_share", old?.MinimumReligionShare ?? .5f),
                    RequiredRegions = Ids("required_regions", old?.RequiredRegions),
                    Supersedes = Ids("supersedes", old?.Supersedes),
                    RequiredShare = Number("required_share", old?.RequiredShare ?? -1f),
                    MinimumComponentShare = Number("minimum_component_share", old?.MinimumComponentShare ?? .5f),
                    AliasOf = Text("alias_of", old?.AliasOf ?? ""),
                    Restores = (Text("restores", old?.Restores ?? "") ?? "").ToUpperInvariant(),
                    PeacefulAccession = e.TryGetProperty("peaceful_accession", out var accession)
                        ? accession.GetBoolean() : old?.PeacefulAccession ?? false,
                    SubjectIntegration = e.TryGetProperty("subject_integration", out var integration)
                        ? integration.GetBoolean() : old?.SubjectIntegration ?? false,
                    AiFoundation = e.TryGetProperty("ai_foundation", out var ai)
                        ? ai.GetBoolean() : old?.AiFoundation ?? false
                };
                if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name) || (d.EligibleTags.Length == 0 && d.EligibleCultures.Length == 0 && d.EligibleCultureGroups.Length == 0 && d.EligibleReligions.Length == 0) || (d.RequiredTags.Length == 0 && d.RequiredProvinces.Length == 0 && d.RequiredRegions.Length == 0) ||
                    d.RequiredProvinces.Any(p => p <= 0) || new[] { d.MinimumStability, d.CostShare, d.GovernanceGain, d.ProductivityGain, d.MinimumReligionShare, d.MinimumComponentShare }.Any(v => !float.IsFinite(v) || v < 0 || v > 1))
                    throw new FormatException("empty eligibility/territory or values outside 0..1");
                if (d.RequiredIdeology.Any(id => !IdeologyCatalog.Knows(id)))
                    throw new FormatException("names an ideology nothing declares");
                if (d.Supersedes.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                    throw new FormatException("a state cannot supersede itself");
                if (d.AliasOf == d.Id) throw new FormatException("a state cannot alias itself");
                if (d.Restores.Length > 0 && !d.RequiredTags.Contains(d.Restores, StringComparer.OrdinalIgnoreCase))
                    throw new FormatException("a restoration must require the restored country's scenario territory");
                if (d.RequiredGovernment.Any(id => !GovernmentCatalog.Knows(id)))
                    throw new FormatException("names a form of government nothing declares");
                if (d.RequiredReligions.Any(id => DemographyCatalog.Religion(id) is null))
                    throw new FormatException("names a religion nothing declares");
                if (d.RequiredReligionGroups.Any(id => !DemographyCatalog.Religions.Values.Any(r =>
                    string.Equals(r.Group, id, StringComparison.OrdinalIgnoreCase))))
                    throw new FormatException("names a religion group nothing declares");
                if ((d.RequiredReligions.Length > 0 || d.RequiredReligionGroups.Length > 0) && d.MinimumReligionShare <= 0f)
                    throw new FormatException("a religion mandate needs a positive population share");
                definitions[entry.Name] = d;
            }
            catch (Exception ex) { errors?.Add($"formable '{entry.Name}': {ex.Message}"); }
        }
    }
    /// <summary>
    /// Whether proclaiming this state would make the country smaller than it already is.
    ///
    /// The comparison is the identity it still holds, not every province it has occupied.
    /// That identity is the bookmark ground of its tag, the ground of a state it has already
    /// proclaimed, and the whole of a larger union it is already pursuing. A formable that
    /// covers that ground is the same country or a bigger one — a union, a restored greater
    /// state — and stays legal. A formable that leaves the heartland out, and is smaller by
    /// the land or by the people on it, is a great power taking a breakaway's name. A holder
    /// whose own identity is only that smaller ground is the breakaway itself and is not
    /// refused here. A newly generated country without an authored identity is compared
    /// with the land it actually owns and controls, never with an occupier's conquests.
    /// </summary>
    public static bool SmallerThanCountry(Campaign campaign, Nation nation, FormableNation definition) =>
        SmallerThanCountry(campaign, nation, definition, cachedIdentity: null);

    internal static bool SmallerThanCountry(Campaign campaign, Nation nation, FormableNation definition,
        HashSet<int> cachedIdentity)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || nation is null || definition is null)
            return false;
        var formable = PeacefulFormation.LandTerritory(campaign, definition);
        if (formable.Count == 0) return false;
        var identity = cachedIdentity is not null &&
            !string.Equals(nation.FormedNationId, definition.Id, StringComparison.OrdinalIgnoreCase)
            ? cachedIdentity
            : CountryIdentity(campaign, nation, definition.Id);
        if (identity.Count == 0 || formable.IsSupersetOf(identity)) return false;
        if (formable.Count < identity.Count) return true;
        return People(campaign, formable) < People(campaign, identity);
    }

    // The on-load repair also needs to assess an already proclaimed restoration. It is
    // deliberately absent from AvailableIn: the restored tag is no longer vanished.
    internal static bool SmallerThanCountry(Campaign campaign, Nation nation, IEnumerable<int> territory, string definitionId)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || nation is null) return false;
        var formable = LandProvinces(campaign, territory);
        if (formable.Count == 0) return false;
        var identity = CountryIdentity(campaign, nation, definitionId);
        if (identity.Count == 0 || formable.IsSupersetOf(identity)) return false;
        if (formable.Count < identity.Count) return true;
        return People(campaign, formable) < People(campaign, identity);
    }

    internal static HashSet<int> CountryIdentity(Campaign campaign, Nation nation, string excludeDefinitionId = null)
    {
        var identity = new HashSet<int>();
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || nation is null) return identity;
        AddHeld(campaign, nation, FormableRestorations.Authored(campaign, nation.Tag), identity);
        if (!string.IsNullOrEmpty(nation.FormedNationId) &&
            (string.IsNullOrEmpty(excludeDefinitionId) ||
             !string.Equals(nation.FormedNationId, excludeDefinitionId, StringComparison.OrdinalIgnoreCase)))
        {
            if (Find(campaign, nation.FormedNationId) is { } formed)
                AddHeld(campaign, nation, PeacefulFormation.Territory(campaign, formed), identity);
            // Generated restorations disappear from Find once somebody has proclaimed
            // them. Their retained heartland remains the current state's identity even
            // when its original founding tag was only a tiny local predecessor.
            else if (FormableRestorations.RestoredTag(nation.FormedNationId) is { } restored)
                AddHeld(campaign, nation, FormableRestorations.Authored(campaign, restored), identity);
        }
        if (!string.IsNullOrEmpty(nation.ConsolidationProject) &&
            Find(campaign, nation.ConsolidationProject) is { } pursued)
            identity.UnionWith(PeacefulFormation.LandTerritory(campaign, pursued));
        if (identity.Count == 0)
        {
            // A dynamic major power has no bookmark tag to protect it. Its own settled
            // ground is still an identity; cultural eligibility for a tiny restoration
            // must not turn the whole successor state into that one annexed strip.
            // ProvinceSetOf(Campaign, ...) returns region map indices, not province ids.
            // The comparison below is in provinces and must also exclude occupied ground.
            var map = campaign.ProvinceMap;
            var controllers = campaign.ProvinceController;
            for (var province = 1; province <= map.ProvinceCount && province < controllers.Length; province++)
                if (!map.WaterByProvince[province] && map.ProvinceToNation[province] == nation.MapIndex &&
                    controllers[province] == nation.MapIndex) identity.Add(province);
        }
        return identity;
    }

    private static HashSet<int> LandProvinces(Campaign campaign, IEnumerable<int> provinces)
    {
        var land = new HashSet<int>();
        var map = campaign.ProvinceMap;
        if (provinces is null || map is null) return land;
        foreach (var province in provinces)
        {
            if (province <= 0 || province > map.ProvinceCount || map.WaterByProvince[province]) continue;
            land.Add(province);
        }
        return land;
    }

    /// <summary>Bookmark or formable ground the country itself owns and controls.</summary>
    private static void AddHeld(Campaign campaign, Nation nation, IEnumerable<int> provinces, HashSet<int> into)
    {
        var map = campaign.ProvinceMap;
        var controller = campaign.ProvinceController;
        if (provinces is null || map is null || controller is null) return;
        var owner = (ushort)nation.MapIndex;
        foreach (var province in provinces)
        {
            if (province <= 0 || province > map.ProvinceCount || province >= controller.Length || map.WaterByProvince[province]) continue;
            if (map.ProvinceToNation[province] != owner || controller[province] != owner) continue;
            into.Add(province);
        }
    }

    private static ProvinceTopology _peopleMap;
    private static Campaign _peopleCampaign;
    private static int _peopleMapRevision = int.MinValue;
    private static Dictionary<int, int> _stateLandProvinceTotals;

    public static Dictionary<int, int> StateLandProvinceTotals(Campaign campaign)
    {
        if (campaign?.ProvinceMap is null || campaign.States.Count == 0) return new Dictionary<int, int>();
        var map = campaign.ProvinceMap;
        // Land provinces per region change only when regions are re-cut (OwnershipRevision), not
        // when a controller takes one in battle.
        if (!ReferenceEquals(_peopleMap, map) || !ReferenceEquals(_peopleCampaign, campaign) ||
            _peopleMapRevision != campaign.OwnershipRevision || _stateLandProvinceTotals is null)
        {
            _peopleMap = map;
            _peopleCampaign = campaign;
            _peopleMapRevision = campaign.OwnershipRevision;
            _stateLandProvinceTotals = new Dictionary<int, int>(campaign.States.Count);
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                if (map.WaterByProvince[province]) continue;
                var state = map.ProvinceToState[province];
                _stateLandProvinceTotals[state] = _stateLandProvinceTotals.GetValueOrDefault(state) + 1;
            }
        }
        return _stateLandProvinceTotals;
    }

    [ThreadStatic] private static Dictionary<int, int> _peopleInsideScratch;
    [ThreadStatic] private static HashSet<int> _evalProvincesScratch;
    [ThreadStatic] private static HashSet<int> _evalPartnersScratch;
    [ThreadStatic] private static HashSet<int> _evalNonPartnersScratch;

    /// <summary>People living on these provinces, by each region's share of its own ground.</summary>
    private static long People(Campaign campaign, HashSet<int> provinces)
    {
        if (provinces.Count == 0 || campaign.States.Count == 0) return 0;
        var map = campaign.ProvinceMap;
        var totals = StateLandProvinceTotals(campaign);
        var inside = _peopleInsideScratch ??= new Dictionary<int, int>();
        inside.Clear();
        foreach (var province in provinces)
        {
            if (province <= 0 || province > map.ProvinceCount || map.WaterByProvince[province]) continue;
            var state = map.ProvinceToState[province];
            inside[state] = inside.GetValueOrDefault(state) + 1;
        }
        long people = 0;
        foreach (var (stateMapIndex, part) in inside)
        {
            if (part <= 0) continue;
            var state = campaign.StateByMapIndex(stateMapIndex);
            if (state is null || state.Population <= 0) continue;
            if (!totals.TryGetValue(stateMapIndex, out var all) || all <= 0) continue;
            people += (long)Math.Round(state.Population * (part / (double)all));
        }
        return people;
    }

    /// <summary>The land provinces of a formable's foundation territory, for the map.</summary>
    public static int[] Land(Campaign campaign, Nation nation, FormableNation d)
    {
        if (campaign is null || d is null) return Array.Empty<int>();
        var map = campaign.ProvinceMap;
        var provinces = new HashSet<int>(d.RequiredProvinces);
        provinces.UnionWith(PeacefulFormation.RegionProvinces(campaign, d));
        foreach (var tag in d.RequiredTags) provinces.UnionWith(FormableRestorations.Authored(campaign, tag));
        return provinces.Where(p => map is not null && p > 0 && p <= map.ProvinceCount && !map.WaterByProvince[p]).ToArray();
    }

    internal static bool IsAtPeace(Campaign campaign, int nationId)
    {
        for (var i = 0; i < campaign.Wars.Count; i++)
        {
            var w = campaign.Wars[i];
            if (!w.Ended && w.Involves(nationId)) return false;
        }
        return true;
    }

    private static bool AnyFormedNation(Campaign campaign, string formableId)
    {
        for (var i = 0; i < campaign.Nations.Count; i++)
            if (campaign.Nations[i].FormedNationId == formableId) return true;
        return false;
    }

    internal static bool EvaluateCanForm(Campaign campaign, Nation nation, FormableNation d, out float cost,
        HashSet<int> cachedIdentity = null)
    {
        cost = 0f;
        if (campaign is null || nation is null || d is null) return false;
        if (!Eligible(campaign, nation, d)) return false;
        if (nation.FormedNationHistory.Contains(d.Id)) return false;
        if (d.AliasOf.Length != 0 || (!string.IsNullOrEmpty(nation.FormedNationId) &&
            !d.Supersedes.Contains(nation.FormedNationId, StringComparer.OrdinalIgnoreCase))) return false;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null) return false;
        if (nation.Population <= 0 || nation.IsSubject || nation.Landless || campaign.Fallen.Contains(nation.Id)) return false;
        if (nation.Stability < d.MinimumStability) return false;
        cost = Math.Max(.05f, nation.Gdp * d.CostShare);
        if (nation.Treasury < cost) return false;
        if (d.RequiredIdeology.Length > 0 && !d.RequiredIdeology.Contains(nation.Ideology, StringComparer.OrdinalIgnoreCase)) return false;
        if (d.RequiredGovernment.Length > 0 && !d.RequiredGovernment.Contains(nation.Government, StringComparer.OrdinalIgnoreCase)) return false;
        if (!IsAtPeace(campaign, nation.Id)) return false;
        for (var i = 0; i < d.RequiredTags.Length; i++)
            if (FormableRestorations.Authored(campaign, d.RequiredTags[i]).Length == 0) return false;
        if (string.IsNullOrEmpty(d.Restores)
            ? AnyFormedNation(campaign, d.Id)
            : FormableRestorations.HasLivingIdentity(campaign, d.Restores)) return false;
        if ((d.RequiredReligions.Length > 0 || d.RequiredReligionGroups.Length > 0) &&
            ReligionShare(campaign, nation, d) < d.MinimumReligionShare) return false;

        var provinces = _evalProvincesScratch ??= new HashSet<int>();
        provinces.Clear();
        for (var i = 0; i < d.RequiredProvinces.Length; i++) provinces.Add(d.RequiredProvinces[i]);
        foreach (var p in PeacefulFormation.RegionProvinces(campaign, d)) provinces.Add(p);
        for (var i = 0; i < d.RequiredTags.Length; i++)
        {
            var authored = FormableRestorations.Authored(campaign, d.RequiredTags[i]);
            for (var j = 0; j < authored.Length; j++) provinces.Add(authored[j]);
        }

        var landCount = 0;
        var owned = 0;
        var partners = _evalPartnersScratch ??= new HashSet<int>();
        var nonPartners = _evalNonPartnersScratch ??= new HashSet<int>();
        partners.Clear();
        nonPartners.Clear();
        foreach (var p in provinces)
        {
            if (p <= 0 || p > map.ProvinceCount) { landCount++; continue; }
            if (map.WaterByProvince[p]) continue;
            landCount++;
            if (p >= controllers.Length) continue;
            var owner = (int)map.ProvinceToNation[p];
            if (controllers[p] != owner || owner <= 0) continue;
            if (!partners.Contains(owner))
            {
                if (nonPartners.Contains(owner)) continue;
                var candidate = campaign.NationByMapIndex(owner);
                if (candidate is not null && PeacefulFormation.Participates(campaign, nation, candidate, d))
                    partners.Add(owner);
                else
                {
                    nonPartners.Add(owner);
                    continue;
                }
            }
            owned++;
        }
        if (landCount == 0) return false;

        if (d.MinimumComponentShare > 0f && string.IsNullOrEmpty(d.Restores))
            for (var i = 0; i < d.RequiredTags.Length; i++)
            {
                var authored = FormableRestorations.Authored(campaign, d.RequiredTags[i]);
                var compLen = 0;
                var compSecured = 0;
                for (var j = 0; j < authored.Length; j++)
                {
                    var p = authored[j];
                    if (p <= 0 || p > map.ProvinceCount) { compLen++; continue; }
                    if (map.WaterByProvince[p]) continue;
                    compLen++;
                    if (p < controllers.Length && partners.Contains(map.ProvinceToNation[p]) &&
                        controllers[p] == map.ProvinceToNation[p]) compSecured++;
                }
                var needed = (int)MathF.Ceiling(compLen * d.MinimumComponentShare);
                if (compLen == 0 || compSecured < needed) return false;
            }

        var heldShare = d.RequiredShare > 0f && d.RequiredShare <= 1f ? d.RequiredShare
            : !string.IsNullOrEmpty(d.Restores) ? campaign.Defines.FormableRestorationShare
            : campaign.Defines.FormableRequiredShare;
        var requiredCount = landCount <= 1 ? landCount
            : Math.Clamp((int)MathF.Ceiling(landCount * Math.Clamp(heldShare, 0.05f, 1f)), 1, landCount);
        if (owned < requiredCount) return false;
        return !SmallerThanCountry(campaign, nation, d, cachedIdentity);
    }

    public static FormationStatus Evaluate(Campaign campaign, Nation nation, FormableNation d)
    {
        if (campaign is null || nation is null || d is null) return new(Array.Empty<FormationRequirement>(), 0, 0, 0);
        var map = campaign.ProvinceMap;
        var requirements = new List<FormationRequirement>
        {
            new(Loc.Get("Eligible founding country"), Eligible(campaign, nation, d)),
            new(Loc.Get("This proclamation has not already been made"), !nation.FormedNationHistory.Contains(d.Id)),
            new(Loc.Get("Compatible previous proclamation"), d.AliasOf.Length == 0 &&
                (string.IsNullOrEmpty(nation.FormedNationId) ||
                d.Supersedes.Contains(nation.FormedNationId, StringComparer.OrdinalIgnoreCase))),
            new(Loc.Get("At peace"), IsAtPeace(campaign, nation.Id)),
            new(Loc.Format("Stability: {0:P0} / {1:P0}", nation.Stability, d.MinimumStability), nation.Stability >= d.MinimumStability),
            new(Loc.Get("Territorial map ready"), map is not null && campaign.ProvinceController is not null),
            new(Loc.Get("An independent country must lead the proclamation"), nation.Population > 0 &&
                !nation.IsSubject && !nation.Landless && !campaign.Fallen.Contains(nation.Id))
        };
        var provinces = new HashSet<int>(d.RequiredProvinces);
        provinces.UnionWith(PeacefulFormation.RegionProvinces(campaign, d));
        foreach (var tag in d.RequiredTags)
        {
            var authored = FormableRestorations.Authored(campaign, tag);
            requirements.Add(new(Loc.Format("Scenario territory: {0}", tag), authored.Length > 0));
            provinces.UnionWith(authored);
        }
        // Unresolved IDs fail closed rather than silently disappearing.
        var land = provinces.Where(p => map is null || p <= 0 || p > map.ProvinceCount || !map.WaterByProvince[p]).ToArray();
        // Only a country holding ground in this definition can make that ground count, so only
        // those are asked whether they take part. Asking all five hundred, every one of whose
        // answers walks every live war, for every definition of every founding country, was the
        // most expensive thing this pass did on a late world. The owners are read straight off
        // the land list: no index is built, and the walk of the world below is a membership test.
        var wanted = new HashSet<int>();
        if (map is not null)
            foreach (var province in land)
            {
                // The land list deliberately keeps unresolved ids so they fail closed; an
                // out-of-range province has no owner to ask about.
                if (province <= 0 || province > map.ProvinceCount) continue;
                wanted.Add(map.ProvinceToNation[province]);
            }
        var partners = new HashSet<int>();
        foreach (var mapIdx in wanted)
        {
            if (mapIdx <= 0) continue;
            var candidate = campaign.NationByMapIndex(mapIdx);
            if (candidate is not null && PeacefulFormation.Participates(campaign, nation, candidate, d))
                partners.Add(candidate.MapIndex);
        }
        bool Held(int p) => map is not null && p > 0 && p <= map.ProvinceCount && !map.WaterByProvince[p] &&
            campaign.ProvinceController is not null && p < campaign.ProvinceController.Length &&
            partners.Contains(map.ProvinceToNation[p]) && campaign.ProvinceController[p] == map.ProvinceToNation[p];
        var owned = land.Count(Held);
        // Authored components are separate founding mandates, not province-weighted votes.
        // Restorations retain their aggregate rule. Mods can explicitly opt out of
        // the component floor with minimum_component_share: 0.
        if (d.MinimumComponentShare > 0f && string.IsNullOrEmpty(d.Restores))
            foreach (var tag in d.RequiredTags)
            {
                var component = FormableRestorations.Authored(campaign, tag)
                    .Where(p => map is not null && p > 0 && p <= map.ProvinceCount && !map.WaterByProvince[p]).ToArray();
                var secured = component.Count(Held);
                var needed = (int)MathF.Ceiling(component.Length * d.MinimumComponentShare);
                requirements.Add(new(Loc.Format("Founding component {0} secured: {1}/{2}", tag, secured, needed),
                    component.Length > 0 && secured >= needed));
            }
        // Most of it, not all of it: every formable used to demand each last province, so one
        // island or one rebel-held valley kept a union from ever being proclaimed. The share is
        // the formable's own (required_share), or the defaults.
        var heldShare = d.RequiredShare > 0f && d.RequiredShare <= 1f ? d.RequiredShare
            : !string.IsNullOrEmpty(d.Restores) ? campaign.Defines.FormableRestorationShare
            : campaign.Defines.FormableRequiredShare;
        var requiredCount = land.Length <= 1 ? land.Length
            : Math.Clamp((int)MathF.Ceiling(land.Length * Math.Clamp(heldShare, 0.05f, 1f)), 1, land.Length);
        requirements.Add(new(Loc.Format("Foundation territory secured: {0}/{1}", owned, requiredCount),
            land.Length > 0 && owned >= requiredCount));
        var cost = Math.Max(.05f, nation.Gdp * d.CostShare);
        requirements.Add(new(Loc.Get("Treasury covers the founding cost"), nation.Treasury >= cost));
        requirements.Add(string.IsNullOrEmpty(d.Restores)
            ? new(Loc.Get("No other country has proclaimed this state"), !AnyFormedNation(campaign, d.Id))
            : new(Loc.Get("No competing state or government in exile claims this identity"),
                !FormableRestorations.HasLivingIdentity(campaign, d.Restores)));
        requirements.Add(new(Loc.Get("The new state is not smaller than the country already is"),
            !SmallerThanCountry(campaign, nation, d)));

        // What kind of state may proclaim it. A caliphate is not something a parliamentary
        // republic declares, and the Soviet Union is not proclaimed by a monarchy: for the
        // states where that is the whole character of the thing, the politics are part of
        // the price. Most formables name neither and are open to anybody.
        if (d.RequiredIdeology.Length > 0)
            requirements.Add(new(Loc.Format("Ideology: {0}",
                    string.Join(", ", d.RequiredIdeology.Select(id => Loc.Get(IdeologyCatalog.Of(id).Name)))),
                d.RequiredIdeology.Contains(nation.Ideology, StringComparer.OrdinalIgnoreCase)));
        if (d.RequiredGovernment.Length > 0)
            requirements.Add(new(Loc.Format("Government: {0}",
                    string.Join(", ", d.RequiredGovernment.Select(id => Loc.Get(GovernmentCatalog.Of(id).Name)))),
                d.RequiredGovernment.Contains(nation.Government, StringComparer.OrdinalIgnoreCase)));
        if (d.RequiredReligions.Length > 0 || d.RequiredReligionGroups.Length > 0)
        {
            string GroupName(string id)
            {
                var key = "religion_group." + id;
                var translated = Loc.Get(key);
                return translated == key ? id : translated;
            }
            var beliefs = d.RequiredReligions.Select(DemographyCatalog.ReligionName)
                .Concat(d.RequiredReligionGroups.Select(GroupName));
            var share = ReligionShare(campaign, nation, d);
            requirements.Add(new(Loc.Format("Religious founding mandate ({0}): {1:P0} / {2:P0}",
                string.Join(", ", beliefs), share, d.MinimumReligionShare), share >= d.MinimumReligionShare));
        }
        return new(requirements.ToArray(), owned, land.Length, cost) { Required = requiredCount };
    }

    internal static bool CanForm(Campaign campaign, Nation nation, FormableNation d)
    {
        if (campaign is null || nation is null || d is null) return false;
        var map = campaign.ProvinceMap;
        var controllers = campaign.ProvinceController;
        if (map is null || controllers is null) return false;
        if (nation.Population <= 0 || nation.IsSubject || nation.Landless || campaign.Fallen.Contains(nation.Id)) return false;
        if (nation.Stability < d.MinimumStability) return false;
        if (d.AliasOf.Length > 0) return false;
        if (nation.FormedNationHistory.Contains(d.Id)) return false;
        if (!string.IsNullOrEmpty(nation.FormedNationId) &&
            !ContainsIgnoreCase(d.Supersedes, nation.FormedNationId)) return false;
        if (!Eligible(campaign, nation, d)) return false;
        if (!IsAtPeace(campaign, nation.Id)) return false;
        var cost = Math.Max(.05f, nation.Gdp * d.CostShare);
        if (nation.Treasury < cost) return false;
        if (d.RequiredIdeology.Length > 0 && !ContainsIgnoreCase(d.RequiredIdeology, nation.Ideology)) return false;
        if (d.RequiredGovernment.Length > 0 && !ContainsIgnoreCase(d.RequiredGovernment, nation.Government)) return false;
        if (string.IsNullOrEmpty(d.Restores) ? AnyFormedNation(campaign, d.Id)
                : FormableRestorations.HasLivingIdentity(campaign, d.Restores))
            return false;
        if (SmallerThanCountry(campaign, nation, d)) return false;
        if ((d.RequiredReligions.Length > 0 || d.RequiredReligionGroups.Length > 0) &&
            ReligionShare(campaign, nation, d) < d.MinimumReligionShare)
            return false;

        foreach (var tag in d.RequiredTags)
            if (FormableRestorations.Authored(campaign, tag).Length == 0)
                return false;

        var land = PeacefulFormation.LandTerritory(campaign, d);
        var unresolved = 0;
        var provCount = map.ProvinceCount;
        for (var i = 0; i < d.RequiredProvinces.Length; i++)
        {
            var p = d.RequiredProvinces[i];
            if (p <= 0 || p > provCount) unresolved++;
        }
        var landTotal = land.Count + unresolved;
        if (landTotal == 0) return false;

        var participants = _canFormPartners ??= new Dictionary<int, bool>();
        participants.Clear();
        var owned = 0;
        var provToNation = map.ProvinceToNation;
        var ctrlLen = controllers.Length;
        foreach (var p in land)
        {
            if (p <= 0 || p >= ctrlLen || p > provCount || map.WaterByProvince[p]) continue;
            var ownerMap = provToNation[p];
            if (controllers[p] != ownerMap || ownerMap == 0) continue;
            if (!participants.TryGetValue(ownerMap, out var ok))
            {
                var candidate = campaign.NationByMapIndex(ownerMap);
                ok = candidate is not null && PeacefulFormation.Participates(campaign, nation, candidate, d);
                participants[ownerMap] = ok;
            }
            if (ok) owned++;
        }

        var heldShare = d.RequiredShare > 0f && d.RequiredShare <= 1f ? d.RequiredShare
            : !string.IsNullOrEmpty(d.Restores) ? campaign.Defines.FormableRestorationShare
            : campaign.Defines.FormableRequiredShare;
        var requiredCount = landTotal <= 1 ? landTotal
            : Math.Clamp((int)MathF.Ceiling(landTotal * Math.Clamp(heldShare, 0.05f, 1f)), 1, landTotal);
        if (owned < requiredCount) return false;

        if (d.MinimumComponentShare > 0f && string.IsNullOrEmpty(d.Restores))
        {
            var water = map.WaterByProvince;
            foreach (var tag in d.RequiredTags)
            {
                var raw = FormableRestorations.Authored(campaign, tag);
                var total = 0;
                var secured = 0;
                for (var i = 0; i < raw.Length; i++)
                {
                    var p = raw[i];
                    if (p <= 0 || p > provCount || water[p]) continue;
                    total++;
                    if (p < ctrlLen && provToNation[p] == controllers[p] &&
                        participants.TryGetValue(provToNation[p], out var ok) && ok)
                        secured++;
                }
                var needed = (int)MathF.Ceiling(total * d.MinimumComponentShare);
                if (total == 0 || secured < needed) return false;
            }
        }
        return true;
    }

    [ThreadStatic] private static Dictionary<int, bool> _canFormPartners;
}

public sealed class FormNationCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string FormableId { get; init; } = "";
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        var d = FormableNations.Find(campaign, FormableId);
        var status = FormableNations.Evaluate(campaign, nation, d);
        if (!status.CanForm) return;
        { var ledgerBefore = nation.Treasury; nation.Treasury -= status.Cost; MoneyLedger.Book(nation, MoneyFlow.EventsAndDecisions, nation.Treasury - ledgerBefore); }
        PeacefulFormation.Integrate(campaign, nation, d);
        if (!string.IsNullOrEmpty(nation.FormedNationId)) nation.FormedNationHistory.Add(nation.FormedNationId);
        nation.FormedNationHistory.Add(d.Id);
        nation.FormedNationId = d.Id;
        nation.FormedNationDay = campaign.ElapsedDays;
        FormableRestorations.Invalidate(campaign);
        // How well the proclaimed state holds together starts here: one people is one country, and
        // every people after that is a promise to be kept (UnionCohesion).
        nation.UnionCohesion = UnionCohesion.Start(campaign, nation, d);
        nation.Name = d.Name;
        nation.NameProclaimed = true;
        // A restored country is that country again: its people are the state's people, so the
        // land it took back is home rather than a province of strangers.
        // Declared rather than assumed: a declaration replaces what the catalogue said, so the
        // country's own peoples are written first and the restored ones after them.
        if (!string.IsNullOrEmpty(d.Restores))
        {
            if (nation.FoundedCultures.Count == 0) nation.FoundedCultures.AddRange(DemographyCatalog.PrimaryCultures(nation.Tag));
            foreach (var culture in DemographyCatalog.PrimaryCultures(d.Restores))
                if (!nation.FoundedCultures.Contains(culture, StringComparer.OrdinalIgnoreCase))
                    nation.FoundedCultures.Add(culture);
            DemographyCatalog.Declare(nation.Tag, nation.FoundedCultures);
            FormableRestorations.SeedLostHomelands(campaign, nation, d.Restores);
        }
        // And its own colour, where one was declared. A proclaimed state that keeps the
        // founder's colour has changed nothing on the one screen a player spends the game
        // looking at. The flag needs no line here: FlagCatalog reads what has been
        // proclaimed as a qualifier, so gfx/flags/flag_formable_<id>.png is the whole of it.
        if (!string.IsNullOrWhiteSpace(d.Colour)) nation.Color = d.Colour;
        var limit = StabilityCore.Weights.GovernanceLimit;
        nation.StabilityGovernance = M.Clamp(nation.StabilityGovernance + d.GovernanceGain, -limit, limit);
        nation.Productivity += d.ProductivityGain;
        campaign.Chronicle.Record(campaign, "state_formed", nation.Id, subject: d.Name);
        // Stable identity is retained. Territory changes only through validated accession.
        campaign.MarkStrategicChange();
    }
}
