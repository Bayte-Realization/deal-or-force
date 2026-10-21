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
}
public sealed record FormationRequirement(string Label, bool Met);
public sealed record FormationStatus(FormationRequirement[] Requirements, int Owned, int Total, float Cost)
{
    public bool CanForm => Requirements.Length > 0 && Requirements.All(r => r.Met);
}

/// <summary>Territories refer to authored scenario ownership, not shifting live borders.</summary>
public static class FormableNations
{
    private static readonly Dictionary<string, FormableNation> Definitions = new(StringComparer.Ordinal);
    private static IReadOnlyList<FormableNation> _visible;
    public static IEnumerable<FormableNation> All => _visible ??= Array.AsReadOnly(Definitions.Values
        .Where(d => d.AliasOf.Length == 0).OrderBy(d => d.Id, StringComparer.Ordinal).ToArray());
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
    public static bool CompatibleProclamation(Nation nation, FormableNation definition) =>
        nation is not null && definition is not null && definition.AliasOf.Length == 0 &&
        !nation.FormedNationHistory.Contains(definition.Id) &&
        (string.IsNullOrEmpty(nation.FormedNationId) || definition.Supersedes.Contains(nation.FormedNationId, StringComparer.OrdinalIgnoreCase));
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
        if (definition.EligibleTags.Contains(nation.Tag, StringComparer.OrdinalIgnoreCase)) return true;
        if (definition.EligibleCultures.Any(c => DemographyCatalog.IsPrimary(nation.Tag, c) ||
                nation.FoundedCultures.Contains(c, StringComparer.OrdinalIgnoreCase))) return true;
        if (definition.EligibleCultureGroups.Length == 0 && definition.EligibleReligions.Length == 0) return false;
        var peoples = DemographyCatalog.PrimaryCultures(nation.Tag).Concat(nation.FoundedCultures);
        foreach (var people in peoples)
        {
            var culture = DemographyCatalog.Culture(people);
            if (culture is not null && definition.EligibleCultureGroups.Contains(culture.Group, StringComparer.OrdinalIgnoreCase))
                return true;
            if (definition.EligibleReligions.Length > 0)
            {
                var faith = DemographyCatalog.FaithOf(people).FirstOrDefault();
                if (faith.Share >= 0.5f && definition.EligibleReligions.Contains(faith.Religion, StringComparer.OrdinalIgnoreCase))
                    return true;
            }
        }
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
        double total = 0, matching = 0;
        foreach (var state in campaign.StatesOf(nation))
        {
            double represented = 0;
            foreach (var population in state.PopulationGroups)
            {
                if (population is null || population.Size <= 0) continue;
                represented += population.Size;
                if (definition.RequiredReligions.Contains(population.Religion ?? "", StringComparer.OrdinalIgnoreCase) ||
                    definition.RequiredReligionGroups.Contains(DemographyCatalog.Religion(population.Religion)?.Group ?? "", StringComparer.OrdinalIgnoreCase))
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
                    AliasOf = Text("alias_of", old?.AliasOf ?? ""),
                    PeacefulAccession = e.TryGetProperty("peaceful_accession", out var accession)
                        ? accession.GetBoolean() : old?.PeacefulAccession ?? false,
                    SubjectIntegration = e.TryGetProperty("subject_integration", out var integration)
                        ? integration.GetBoolean() : old?.SubjectIntegration ?? false,
                    AiFoundation = e.TryGetProperty("ai_foundation", out var ai)
                        ? ai.GetBoolean() : old?.AiFoundation ?? false
                };
                if (string.IsNullOrWhiteSpace(d.Id) || string.IsNullOrWhiteSpace(d.Name) || (d.EligibleTags.Length == 0 && d.EligibleCultures.Length == 0 && d.EligibleCultureGroups.Length == 0 && d.EligibleReligions.Length == 0) || (d.RequiredTags.Length == 0 && d.RequiredProvinces.Length == 0 && d.RequiredRegions.Length == 0) ||
                    d.RequiredProvinces.Any(p => p <= 0) || new[] { d.MinimumStability, d.CostShare, d.GovernanceGain, d.ProductivityGain, d.MinimumReligionShare }.Any(v => !float.IsFinite(v) || v < 0 || v > 1))
                    throw new FormatException("empty eligibility/territory or values outside 0..1");
                if (d.RequiredIdeology.Any(id => !IdeologyCatalog.Knows(id)))
                    throw new FormatException("names an ideology nothing declares");
                if (d.Supersedes.Contains(d.Id, StringComparer.OrdinalIgnoreCase))
                    throw new FormatException("a state cannot supersede itself");
                if (d.AliasOf == d.Id) throw new FormatException("a state cannot alias itself");
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
    /// whose own identity is only that smaller ground, or who has no prior identity at all,
    /// is the breakaway itself and is not refused here.
    /// </summary>
    public static bool SmallerThanCountry(Campaign campaign, Nation nation, FormableNation definition)
    {
        if (campaign?.ProvinceMap is null || campaign.ProvinceController is null || nation is null || definition is null)
            return false;
        var formable = LandProvinces(campaign, PeacefulFormation.Territory(campaign, definition));
        if (formable.Count == 0) return false;

        var identity = new HashSet<int>();
        AddHeld(campaign, nation, FormableRestorations.Authored(campaign, nation.Tag), identity);
        if (!string.IsNullOrEmpty(nation.FormedNationId) &&
            Find(campaign, nation.FormedNationId) is { } formed &&
            !string.Equals(formed.Id, definition.Id, StringComparison.OrdinalIgnoreCase))
            AddHeld(campaign, nation, PeacefulFormation.Territory(campaign, formed), identity);
        if (!string.IsNullOrEmpty(nation.ConsolidationProject) &&
            Find(campaign, nation.ConsolidationProject) is { } pursued)
            foreach (var province in LandProvinces(campaign, PeacefulFormation.Territory(campaign, pursued)))
                identity.Add(province);
        if (identity.Count == 0 || formable.IsSupersetOf(identity)) return false;
        if (formable.Count < identity.Count) return true;
        return People(campaign, formable) < People(campaign, identity);
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

    /// <summary>People living on these provinces, by each region's share of its own ground.</summary>
    private static long People(Campaign campaign, HashSet<int> provinces)
    {
        if (provinces.Count == 0 || campaign.States.Count == 0) return 0;
        var map = campaign.ProvinceMap;
        var totals = new Dictionary<int, int>();
        var inside = new Dictionary<int, int>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.WaterByProvince[province]) continue;
            var state = map.ProvinceToState[province];
            totals[state] = totals.GetValueOrDefault(state) + 1;
            if (provinces.Contains(province)) inside[state] = inside.GetValueOrDefault(state) + 1;
        }
        long people = 0;
        foreach (var state in campaign.States)
        {
            if (state is null || state.Population <= 0) continue;
            if (!inside.TryGetValue(state.MapIndex, out var part) || part <= 0) continue;
            if (!totals.TryGetValue(state.MapIndex, out var all) || all <= 0) continue;
            people += (long)Math.Round(state.Population * (part / (double)all));
        }
        return people;
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
            new(Loc.Get("At peace"), !campaign.Wars.Any(w => !w.Ended && w.Involves(nation.Id))),
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
        var partners = campaign.Nations.Where(n => PeacefulFormation.Participates(campaign, nation, n, d))
            .Select(n => n.MapIndex).ToHashSet();
        bool Held(int p) => map is not null && p > 0 && p <= map.ProvinceCount && !map.WaterByProvince[p] &&
            campaign.ProvinceController is not null && p < campaign.ProvinceController.Length &&
            partners.Contains(map.ProvinceToNation[p]) && campaign.ProvinceController[p] == map.ProvinceToNation[p];
        // Unresolved IDs fail closed rather than silently disappearing.
        var land = provinces.Where(p => map is null || p <= 0 || p > map.ProvinceCount || !map.WaterByProvince[p]).ToArray();
        var owned = land.Count(Held);
        var requiredCount = !string.IsNullOrEmpty(d.Restores) && land.Length > 1
            ? Math.Max(1, (int)MathF.Ceiling(land.Length * 0.5f))
            : land.Length;
        requirements.Add(new(Loc.Format("Foundation territory secured: {0}/{1}", owned, requiredCount),
            land.Length > 0 && owned >= requiredCount));
        var cost = Math.Max(.05f, nation.Gdp * d.CostShare);
        requirements.Add(new(Loc.Get("Treasury covers the founding cost"), nation.Treasury >= cost));
        requirements.Add(new(Loc.Get("No other country has proclaimed this state"), !campaign.Nations.Any(n => n.FormedNationId == d.Id)));
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
        return new(requirements.ToArray(), owned, land.Length, cost);
    }
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
        nation.Treasury -= status.Cost;
        PeacefulFormation.Integrate(campaign, nation, d);
        if (!string.IsNullOrEmpty(nation.FormedNationId)) nation.FormedNationHistory.Add(nation.FormedNationId);
        nation.FormedNationHistory.Add(d.Id);
        nation.FormedNationId = d.Id;
        nation.FormedNationDay = campaign.ElapsedDays;
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
