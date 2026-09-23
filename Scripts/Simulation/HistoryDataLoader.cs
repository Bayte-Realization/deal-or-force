using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Paradox-style scenario history. Common catalogues define what may exist; history
/// chooses the 2024 starting values. Files are ordinary JSON and mods use the same schema.
/// </summary>
public static class HistoryDataLoader
{
    /// <summary>
    /// What a country was in 2024, before anything is worked out from it.
    ///
    /// This has to run before the population is dealt out, because it is the input to that
    /// arithmetic rather than a correction to it: how urban a country is and how much of it
    /// works the land decide what trades its regions hold, and a figure applied afterwards
    /// would be a figure the regions never saw.
    /// </summary>
    public static void ApplyBaseCountries(Campaign campaign)
    {
        // A bookmark is a folder, so a mod that adds a hundred countries to 2024 adds a
        // file beside the base game's rather than editing it.
        foreach (var file in GameDataLoader.DataFiles("history", "countries", "2024"))
            ApplyOptional(campaign, file, true, null);
    }

    /// <summary>
    /// What particular regions were, which can only be said once the regions have people:
    /// these are corrections to the deal, so they come after it.
    /// </summary>
    public static void ApplyBaseStates(Campaign campaign)
    {
        foreach (var file in GameDataLoader.DataFiles("history", "states", "2024"))
            ApplyOptional(campaign, file, false, null);
    }

    /// <summary>
    /// What the world had already signed when the scenario opens.
    ///
    /// Without this, 1 January 2024 is a world of three hundred countries that have never
    /// met: no NATO, no CSTO, nobody guaranteeing anybody. Every alliance a campaign had
    /// was one the computer grew from nothing over years, which is a world that runs but
    /// is not the world the scenario is about.
    ///
    /// An alliance here is a list of members and a name, and it is written out pairwise,
    /// because that is what an alliance IS in this engine: an attack on one is answered by
    /// everybody allied to it. The name is for the file to read like what it describes -
    /// nothing in the simulation needs it.
    /// </summary>
    public static void ApplyBaseDiplomacy(Campaign campaign)
    {
        foreach (var file in GameDataLoader.DataFiles("history", "diplomacy", "2024"))
        {
            if (file is null || !File.Exists(file)) continue;
            try { ApplyDiplomacy(campaign, File.ReadAllText(file), null); }
            catch (Exception exception)
            {
                DiagnosticLogs.Write("simulation",
                    $"HISTORY_DIPLOMACY_FAILED file={Path.GetFileName(file)} error={exception.Message}");
            }
        }
    }

    public static void ApplyDiplomacy(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object) return;

        if (root.TryGetProperty("alliances", out var alliances) && alliances.ValueKind == JsonValueKind.Array)
            foreach (var bloc in alliances.EnumerateArray())
            {
                if (!bloc.TryGetProperty("members", out var members) ||
                    members.ValueKind != JsonValueKind.Array) continue;
                var signatories = new List<Nation>();
                foreach (var member in members.EnumerateArray())
                {
                    var nation = campaign.NationByTag(member.GetString() ?? "");
                    if (nation is null) errors?.Add($"Unknown country '{member.GetString()}' in an alliance.");
                    else signatories.Add(nation);
                }
                for (var one = 0; one < signatories.Count; one++)
                for (var two = one + 1; two < signatories.Count; two++)
                {
                    var relation = CampaignDiplomacy.Get(campaign, signatories[one].Id, signatories[two].Id);
                    relation.Stance = DiplomaticStance.Allied;
                    relation.AllianceSinceDay = campaign.ElapsedDays;
                    OpinionSystem.Remember(campaign, relation, "alliance");
                }
            }

        if (root.TryGetProperty("guarantees", out var guarantees) && guarantees.ValueKind == JsonValueKind.Array)
            foreach (var entry in guarantees.EnumerateArray())
            {
                if (!entry.TryGetProperty("by", out var byTag)) continue;
                var guarantor = campaign.NationByTag(byTag.GetString() ?? "");
                if (guarantor is null)
                {
                    errors?.Add($"Unknown country '{byTag.GetString()}' guaranteeing somebody.");
                    continue;
                }
                if (!entry.TryGetProperty("to", out var targets) || targets.ValueKind != JsonValueKind.Array) continue;
                foreach (var target in targets.EnumerateArray())
                {
                    var sheltered = campaign.NationByTag(target.GetString() ?? "");
                    if (sheltered is null || sheltered.Id == guarantor.Id)
                    {
                        errors?.Add($"Unknown country '{target.GetString()}' being guaranteed.");
                        continue;
                    }
                    var relation = CampaignDiplomacy.Get(campaign, guarantor.Id, sheltered.Id);
                    if (guarantor.Id == relation.A) relation.GuaranteeByA = true;
                    else relation.GuaranteeByB = true;
                    OpinionSystem.Remember(campaign, relation, "guaranteed_us");
                }
            }

        if (root.TryGetProperty("pacts", out var pacts) && pacts.ValueKind == JsonValueKind.Array)
            foreach (var pair in pacts.EnumerateArray())
            {
                if (pair.ValueKind != JsonValueKind.Array) continue;
                var tags = new List<Nation>();
                foreach (var tag in pair.EnumerateArray())
                {
                    var nation = campaign.NationByTag(tag.GetString() ?? "");
                    if (nation is null) errors?.Add($"Unknown country '{tag.GetString()}' in a pact.");
                    else tags.Add(nation);
                }
                if (tags.Count != 2) continue;
                var relation = CampaignDiplomacy.Get(campaign, tags[0].Id, tags[1].Id);
                relation.NonAggressionPact = true;
                OpinionSystem.Remember(campaign, relation, "non_aggression_pact");
            }
    }

    public static void ApplyCountries(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var nation = campaign.NationByTag(entry.Name);
            if (nation is null || entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"Unknown history country '{entry.Name}'.");
                continue;
            }
            var value = entry.Value;
            SetFloat(value, "treasury", number => nation.Treasury = number);
            SetFloat(value, "gdp", number => nation.Gdp = Math.Max(0.01f, number));
            SetLong(value, "population", number => nation.Population = Math.Max(0, number));
            SetFloat(value, "tax_rate", number => nation.TaxRate = M.Clamp(number, 0.05f, 0.55f));
            SetFloat(value, "military_spending", number => nation.MilitarySpending = M.Clamp01(number));
            SetFloat(value, "literacy", number => nation.Literacy = M.Clamp01(number));
            SetFloat(value, "urbanisation", number => nation.Urbanisation = M.Clamp01(number));
            SetFloat(value, "agriculture", number => nation.AgricultureShare = M.Clamp01(number));
            SetFloat(value, "standard_of_living", number => nation.StandardOfLiving = M.Clamp01(number));
            SetFloat(value, "stability", number => nation.Stability = M.Clamp01(number));
            SetFloat(value, "war_support", number => nation.WarSupport = M.Clamp01(number));
            SetFloat(value, "manpower", number => nation.ManpowerReserve = Math.Max(0f, number));
            if (value.TryGetProperty("trade_policy", out var policy) && policy.ValueKind == JsonValueKind.String &&
                Enum.TryParse<TradePolicy>(policy.GetString(), true, out var parsed)) nation.TradePolicy = parsed;
            // An id the catalogue does not have is a scenario naming a government or an
            // ideology that a mod removed, and is worth complaining about rather than
            // silently leaving the country as whatever it was.
            if (value.TryGetProperty("government", out var government) && government.ValueKind == JsonValueKind.String)
            {
                var id = government.GetString() ?? "";
                if (GovernmentCatalog.Knows(id)) nation.Government = id;
                else errors?.Add($"{entry.Name}: no form of government called '{id}'.");
            }
            if (value.TryGetProperty("ideology", out var ideology) && ideology.ValueKind == JsonValueKind.String)
            {
                var id = ideology.GetString() ?? "";
                if (IdeologyCatalog.Knows(id)) nation.Ideology = id;
                else errors?.Add($"{entry.Name}: no ideology called '{id}'.");
            }
        }
    }

    /// <summary>
    /// Every region history/states gives a head-count for, by map index, the later file winning.
    ///
    /// Read by the deal itself (EconomySystem.Seed), before anybody is placed, rather than only
    /// applied afterwards: a figure applied after the deal is one the region's peoples, trades
    /// and buildings were never dealt from, and it moved the country's total by whatever it
    /// added. ApplyStates still sets the same number once the deal is done, and finds nothing
    /// left to change.
    /// </summary>
    public static Dictionary<int, long> StatedPopulations(Campaign campaign) =>
        StatedRegions(campaign).Where(pair => pair.Value.Population.HasValue)
            .ToDictionary(pair => pair.Key, pair => pair.Value.Population.Value);

    /// <summary>What history/states writes down about a region that the deal needs before it starts.</summary>
    public readonly record struct RegionFigures(long? Population, float? Urbanisation);

    /// <summary>
    /// Head-count and how much of it lives in towns, for every region history/states gives
    /// either for, the later file winning field by field. How urban a region is decides which
    /// trades its people follow and whether its share of the country's buildings is farms or
    /// factories, so it has to be known at the deal too - otherwise Moscow was dealt as a
    /// region of the national average, three quarters urban, with farms to match.
    /// </summary>
    public static Dictionary<int, RegionFigures> StatedRegions(Campaign campaign)
    {
        var stated = new Dictionary<int, RegionFigures>();
        var regions = RegionLookup(campaign);
        foreach (var file in GameDataLoader.DataFiles("history", "states", "2024"))
        {
            try
            {
                using var document = Parse(File.ReadAllText(file));
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object || !regions.TryGetValue(entry.Name, out var state))
                        continue;
                    stated.TryGetValue(state.MapIndex, out var figures);
                    if (entry.Value.TryGetProperty("population", out var population) &&
                        population.ValueKind == JsonValueKind.Number && population.TryGetInt64(out var people))
                        figures = figures with { Population = Math.Max(0, people) };
                    if (entry.Value.TryGetProperty("urbanization", out var urbanisation) &&
                        urbanisation.ValueKind == JsonValueKind.Number && urbanisation.TryGetSingle(out var share))
                        figures = figures with { Urbanisation = M.Clamp01(share) };
                    if (figures.Population.HasValue || figures.Urbanisation.HasValue) stated[state.MapIndex] = figures;
                }
            }
            catch (Exception exception) when (exception is IOException or JsonException)
            {
                // ApplyStates reports the same file when it reaches it; saying so twice helps nobody.
            }
        }
        return stated;
    }

    /// <summary>
    /// Regions by the names a history file may use for them: the id, or the ISO code. An id
    /// wins over an ISO code of the same spelling. Built once per file rather than searching
    /// eighteen hundred regions for each of the sixteen hundred a population file names.
    /// </summary>
    private static Dictionary<string, ScenarioState> RegionLookup(Campaign campaign)
    {
        var regions = new Dictionary<string, ScenarioState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in campaign.States)
            if (!string.IsNullOrEmpty(state.Iso)) regions.TryAdd(state.Iso, state);
        foreach (var state in campaign.States)
            if (!string.IsNullOrEmpty(state.Id)) regions[state.Id] = state;
        return regions;
    }

    public static void ApplyStates(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        var populationChanged = new HashSet<int>();
        var regions = RegionLookup(campaign);
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            regions.TryGetValue(entry.Name, out var state);
            if (state is null || entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"Unknown history state '{entry.Name}'.");
                continue;
            }
            var value = entry.Value;
            // The kind first: TryGetInt64 throws rather than answering false on a string, and a
            // quoted number in a mod's file would otherwise cost the whole file.
            if (value.TryGetProperty("population", out var population) &&
                population.ValueKind == JsonValueKind.Number && population.TryGetInt64(out var people))
            {
                EconomySystem.AdjustStatePopulation(state, Math.Max(0, people) - state.Population);
                populationChanged.Add(state.NationMapIndex);
            }
            SetFloat(value, "literacy", number => state.Literacy = M.Clamp01(number));
            SetFloat(value, "standard_of_living", number => state.StandardOfLiving = M.Clamp01(number));
            SetFloat(value, "radicalism", number => state.Radicalism = M.Clamp01(number));
            SetFloat(value, "urbanization", number => state.Urbanization = M.Clamp01(number));
            if (value.TryGetProperty("buildings", out var buildings) && buildings.ValueKind == JsonValueKind.Object)
                foreach (var building in buildings.EnumerateObject())
                    if (BuildingCatalog.TryParse(building.Name, out var kind) && building.Value.TryGetInt32(out var level))
                        state.Buildings[kind] = Math.Max(0, level);
                    else errors?.Add($"Invalid building '{building.Name}' in state '{entry.Name}'.");
            if (value.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
                foreach (var resource in resources.EnumerateObject())
                    if (CommodityCatalog.TryParse(resource.Name, out var commodity) && resource.Value.TryGetSingle(out var potential))
                        state.ResourceDeposits[commodity] = Math.Max(0f, potential);
                    else errors?.Add($"Invalid resource '{resource.Name}' in state '{entry.Name}'.");
            if (value.TryGetProperty("cultures", out var cultures) && cultures.ValueKind == JsonValueKind.Object)
                ApplyCultureMix(state, cultures, errors);
            else if (value.TryGetProperty("nationalities", out var nationalities) && nationalities.ValueKind == JsonValueKind.Object)
                ApplyCultureMix(state, nationalities, errors);
        }

        foreach (var mapIndex in populationChanged)
        {
            var nation = campaign.Nations.FirstOrDefault(item => item.MapIndex == mapIndex);
            if (nation is not null)
                nation.Population = campaign.States.Where(state => state.NationMapIndex == mapIndex).Sum(state => state.Population);
        }
    }

    /// <summary>
    /// Replace who lives in one region, keeping what they do for a living.
    ///
    /// A scenario that says Kirkuk is half Kurdish is making a claim about peoples, not
    /// about trades: the fields and the workshops are where they were, and the same share
    /// of the region still works them. So the trades are measured before the swap and the
    /// new peoples are spread across exactly the same ones - which also means a region
    /// rewritten by history still draws a sensible population screen.
    ///
    /// Keys are culture ids. A nation tag is accepted too and resolves to that country's
    /// own people, because scenarios written before there were cultures said "IND".
    /// </summary>
    private static void ApplyCultureMix(ScenarioState state, JsonElement cultures, List<string> errors)
    {
        var shares = new List<(string Culture, float Share)>();
        foreach (var entry in cultures.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (!entry.Value.TryGetSingle(out var share) || share <= 0f)
            {
                errors?.Add($"Invalid culture share '{entry.Name}' in state '{state.Id}'.");
                continue;
            }
            var id = DemographyCatalog.Culture(entry.Name) is not null
                ? entry.Name
                : DemographyCatalog.PrimaryCultures(entry.Name).FirstOrDefault() ?? entry.Name.ToLowerInvariant();
            shares.Add((id, share));
        }
        var total = shares.Sum(item => item.Share);
        if (total <= 0f) return;

        // The trades as they stand, so the swap changes who works rather than what work exists.
        var trades = state.PopulationGroups
            .Where(group => group.Size > 0)
            .GroupBy(group => (group.Profession, group.Class, group.Sector))
            .Select(group => (group.Key.Profession, group.Key.Class, group.Key.Sector,
                Weight: group.Sum(entry => entry.Size) / (double)Math.Max(1, state.Population),
                Employment: group.Sum(entry => entry.Size * entry.Employment) / group.Sum(entry => entry.Size),
                Wealth: group.Sum(entry => entry.Size * entry.Wealth) / group.Sum(entry => entry.Size)))
            .ToList();
        if (trades.Count == 0)
            trades.Add(("labourers", SocialClass.Lower, Sector.Industry, 1.0, 0.9f, 0.38f));

        state.PopulationGroups.Clear();
        long assigned = 0;
        foreach (var (culture, share) in shares)
        {
            var faith = DemographyCatalog.FaithOf(culture);
            foreach (var trade in trades)
            foreach (var (religion, faithShare) in faith)
            {
                var size = (long)Math.Round(state.Population * (share / total) * trade.Weight * faithShare);
                if (size <= 0) continue;
                assigned += size;
                state.PopulationGroups.Add(new PopulationGroup
                {
                    Culture = culture,
                    Religion = religion,
                    Profession = trade.Profession,
                    Class = trade.Class,
                    Sector = trade.Sector,
                    Size = size,
                    Employment = trade.Employment,
                    Wealth = trade.Wealth,
                    StandardOfLiving = state.StandardOfLiving,
                    Radicalism = state.Radicalism
                });
            }
        }
        if (state.PopulationGroups.Count > 0)
            state.PopulationGroups[^1].Size += state.Population - assigned;
    }

    private static JsonDocument Parse(string json) => JsonDocument.Parse(json, new JsonDocumentOptions
    {
        AllowTrailingCommas = true,
        CommentHandling = JsonCommentHandling.Skip
    });

    private static void ApplyOptional(Campaign campaign, string path, bool countries, List<string> errors)
    {
        if (path is null || !File.Exists(path)) return;
        try
        {
            var json = File.ReadAllText(path);
            if (countries) ApplyCountries(campaign, json, errors); else ApplyStates(campaign, json, errors);
        }
        catch (Exception exception) { errors?.Add($"{Path.GetFileName(path)}: {exception.Message}"); }
    }

    private static void SetFloat(JsonElement value, string name, Action<float> assign)
    {
        if (value.TryGetProperty(name, out var element) && element.TryGetSingle(out var number)) assign(number);
    }

    private static void SetLong(JsonElement value, string name, Action<long> assign)
    {
        if (value.TryGetProperty(name, out var element) && element.TryGetInt64(out var number)) assign(number);
    }
}
