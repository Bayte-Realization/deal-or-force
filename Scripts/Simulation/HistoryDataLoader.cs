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
        }
    }

    public static void ApplyStates(Campaign campaign, string json, List<string> errors)
    {
        using var document = Parse(json);
        var populationChanged = new HashSet<int>();
        foreach (var entry in document.RootElement.EnumerateObject())
        {
            var state = campaign.States.FirstOrDefault(candidate =>
                string.Equals(candidate.Id, entry.Name, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(candidate.Iso, entry.Name, StringComparison.OrdinalIgnoreCase));
            if (state is null || entry.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"Unknown history state '{entry.Name}'.");
                continue;
            }
            var value = entry.Value;
            if (value.TryGetProperty("population", out var population) && population.TryGetInt64(out var people))
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
            .GroupBy(group => (group.Profession, group.Class))
            .Select(group => (group.Key.Profession, group.Key.Class,
                Weight: group.Sum(entry => entry.Size) / (double)Math.Max(1, state.Population),
                Employment: group.Sum(entry => entry.Size * entry.Employment) / group.Sum(entry => entry.Size),
                Wealth: group.Sum(entry => entry.Size * entry.Wealth) / group.Sum(entry => entry.Size)))
            .ToList();
        if (trades.Count == 0)
            trades.Add(("labourers", SocialClass.Workers, 1.0, 0.9f, 0.38f));

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
