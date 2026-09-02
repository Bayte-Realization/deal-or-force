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
    public static void ApplyBase(Campaign campaign)
    {
        ApplyOptional(campaign, GameDataLoader.FindDataFile("history", "countries", "2024", "countries.json"), true, null);
        ApplyOptional(campaign, GameDataLoader.FindDataFile("history", "states", "2024", "states.json"), false, null);
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
                    if (Enum.TryParse<BuildingKind>(building.Name, true, out var kind) && building.Value.TryGetInt32(out var level))
                        state.Buildings[kind] = Math.Max(0, level);
                    else errors?.Add($"Invalid building '{building.Name}' in state '{entry.Name}'.");
            if (value.TryGetProperty("resources", out var resources) && resources.ValueKind == JsonValueKind.Object)
                foreach (var resource in resources.EnumerateObject())
                    if (CommodityCatalog.TryParse(resource.Name, out var commodity) && resource.Value.TryGetSingle(out var potential))
                        state.ResourceDeposits[commodity] = Math.Max(0f, potential);
                    else errors?.Add($"Invalid resource '{resource.Name}' in state '{entry.Name}'.");
            if (value.TryGetProperty("nationalities", out var nationalities) && nationalities.ValueKind == JsonValueKind.Object)
                ApplyNationalities(state, nationalities, errors);
        }

        foreach (var mapIndex in populationChanged)
        {
            var nation = campaign.Nations.FirstOrDefault(item => item.MapIndex == mapIndex);
            if (nation is not null)
                nation.Population = campaign.States.Where(state => state.NationMapIndex == mapIndex).Sum(state => state.Population);
        }
    }

    private static void ApplyNationalities(ScenarioState state, JsonElement nationalities, List<string> errors)
    {
        var shares = new List<(string Tag, float Share)>();
        foreach (var entry in nationalities.EnumerateObject())
            if (entry.Value.TryGetSingle(out var share) && share > 0f) shares.Add((entry.Name, share));
            else errors?.Add($"Invalid nationality share '{entry.Name}' in state '{state.Id}'.");
        var total = shares.Sum(item => item.Share);
        if (total <= 0f) return;
        var classShares = Enum.GetValues<SocialClass>().ToDictionary(kind => kind, kind =>
            state.PopulationGroups.Where(group => group.Class == kind).Sum(group => group.Size) /
            (double)Math.Max(1, state.Population));
        state.PopulationGroups.Clear();
        long assigned = 0;
        foreach (var nationality in shares)
        foreach (var socialClass in Enum.GetValues<SocialClass>())
        {
            var size = (long)Math.Round(state.Population * nationality.Share / total * classShares[socialClass]);
            assigned += size;
            state.PopulationGroups.Add(new PopulationGroup
            {
                Nationality = nationality.Tag,
                Class = socialClass,
                Size = size,
                Employment = socialClass == SocialClass.Farmers ? 0.82f : 0.90f,
                Wealth = socialClass == SocialClass.Owners ? 0.86f : socialClass == SocialClass.MiddleClass ? 0.62f : 0.38f,
                StandardOfLiving = state.StandardOfLiving,
                Radicalism = state.Radicalism
            });
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
