using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Scenario building availability. No compiled "industrial age" or mandatory modern
/// technology: a total conversion supplies common/building_rules as JSON or YAML.
/// Omitted properties keep the earlier rule; null dates clear a date restriction.
/// Dates use HistoricalDate (including BCE), not DateTime's year-one lower bound.
/// This catalogue is content, not mutable campaign state.
/// </summary>
public static class BuildingRules
{
    private sealed record Rule(bool Enabled = true, HistoricalDate? From = null,
        HistoricalDate? Until = null, string[] Technologies = null);
    private static readonly Dictionary<string, Rule> Rules = new(StringComparer.OrdinalIgnoreCase);
    public static readonly List<string> Problems = new();
    static BuildingRules() => ResetCatalog();
    private static string Key(string id) => id.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    public static void ResetCatalog()
    {
        Rules.Clear(); Problems.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "building_rules"))
            using (document) Merge(document.RootElement, Problems);
    }

    /// <summary>Reject a malformed override atomically; never erase a valid prior rule.</summary>
    public static void Merge(JsonElement root, List<string> problems)
    {
        // Ensure the building catalogue's first reset cannot erase this override
        // later, when the first availability preview resolves a building ID.
        _ = BuildingCatalog.All;
        if (root.ValueKind != JsonValueKind.Object)
        { problems?.Add("Building rules must be an object keyed by building ID."); return; }
        foreach (var entry in root.EnumerateObject())
        {
            var key = Key(entry.Name);
            var previous = Rules.GetValueOrDefault(key) ?? new Rule();
            var value = entry.Value;
            var enabled = previous.Enabled; var from = previous.From; var until = previous.Until;
            var technologies = previous.Technologies;
            var valid = value.ValueKind == JsonValueKind.Object;
            if (valid && value.TryGetProperty("enabled", out var toggle))
            {
                if (toggle.ValueKind is JsonValueKind.True or JsonValueKind.False) enabled = toggle.GetBoolean();
                else valid = false;
            }
            bool Date(string key, ref HistoricalDate? date)
            {
                if (!value.TryGetProperty(key, out var item)) return true;
                if (item.ValueKind == JsonValueKind.Null) { date = null; return true; }
                if (item.ValueKind != JsonValueKind.String || !HistoricalDate.TryParse(item.GetString(), out var parsed)) return false;
                date = parsed; return true;
            }
            if (valid) valid = Date("available_from", ref from) && Date("available_until", ref until);
            if (valid && from is { } first && until is { } last && first.DayNumber > last.DayNumber) valid = false;
            if (valid && value.TryGetProperty("requires_technologies", out var required))
            {
                valid = required.ValueKind == JsonValueKind.Array;
                if (valid)
                {
                    var list = new List<string>();
                    foreach (var item in required.EnumerateArray())
                    {
                        if (item.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(item.GetString())) { valid = false; break; }
                        list.Add(item.GetString());
                    }
                    technologies = list.ToArray();
                }
            }
            if (valid) Rules[key] = new Rule(enabled, from, until, technologies);
            else problems?.Add($"Invalid building rule '{entry.Name}': check enabled, dates and requires_technologies.");
        }
    }

    public static string Refusal(Campaign c, Nation n, BuildingKind kind)
    {
        if (!Rules.TryGetValue(Key(BuildingCatalog.IdOf(kind)), out var rule)) return "";
        if (!rule.Enabled) return Loc.Get("This building is disabled by the scenario.");
        var today = new HistoricalDate(c.Date.Year, c.Date.Month, c.Date.Day);
        if (rule.From is { } from && today.DayNumber < from.DayNumber)
            return Loc.Format("Available from {0}.", Display(from));
        if (rule.Until is { } until && today.DayNumber > until.DayNumber)
            return Loc.Format("Available only until {0}.", Display(until));
        if (rule.Technologies is not null)
            foreach (var id in rule.Technologies)
                if (!n.HasTechnology(id))
                    return Loc.Format("Requires technology: {0}.", Loc.Get(TechnologyCatalog.Of(id)?.Name ?? id));
        return "";
    }

    private static string Display(HistoricalDate date) => new GameDate(date.Year, date.Month, date.Day).Display();
}

/// <summary>One admission check for buttons, map placement, AI and received commands.
/// It is read-only: in particular it does not reserve cash or a construction slot.</summary>
public static class ConstructionAdmission
{
    public static string Refusal(Campaign c, Nation n, ScenarioState state, BuildingKind kind)
    {
        if (c is null || n is null || state is null) return Loc.Get("Select an owned region.");
        if (!c.FeatureEnabled("construction")) return Loc.Get("Construction is disabled by the scenario.");
        if (!BuildingCatalog.Contains(kind)) return Loc.Get("Unknown building.");
        if (state.NationMapIndex != n.MapIndex &&
            !(state.HeldById == n.Id && HeldRegions.Administered(c, state))) return Loc.Get("Select an owned region.");
        if (LakeRegions.IsWaterState(state)) return Loc.Get("Buildings need a land region.");
        var rule = BuildingRules.Refusal(c, n, kind);
        if (rule.Length > 0) return rule;
        var water = Waterfront.Refusal(c, state.MapIndex, kind);
        if (water.Length > 0) return water;
        var running = 0;
        foreach (var project in c.Construction) if (project.NationId == n.Id) running++;
        if (running >= ConstructionCost.Limit(c, n, c.Defines.MaxConstructionProjects))
            return Loc.Format("The national construction limit is reached: {0} projects are under way.", running);
        if (n.Treasury < MathF.Min(.05f, ConstructionCost.Of(c, n, kind))) return Loc.Get("The treasury cannot start it.");
        return "";
    }
}
