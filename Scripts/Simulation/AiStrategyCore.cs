using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace ByDealOrForce.Simulation;

public sealed record AiStrategyDefinition(
    string Id,
    float SpearheadShare,
    float AdvanceShareAttacker,
    float AdvanceShareDefender,
    float DefendShare,
    ArmyStance OffensiveStance,
    ArmyStance DefensiveStance,
    float ExecutePlanningThreshold);

/// <summary>Moddable operational doctrine profiles used by the same order commands as players.</summary>
public static class AiStrategyCatalog
{
    private static readonly Dictionary<string, AiStrategyDefinition> Defaults = new(StringComparer.OrdinalIgnoreCase)
    {
        ["default"] = new("default", 0.12f, 0.58f, 0.34f, 0.26f,
            ArmyStance.Balanced, ArmyStance.Cautious, 0.18f),
        ["major_offensive"] = new("major_offensive", 0.20f, 0.72f, 0.46f, 0.18f,
            ArmyStance.Aggressive, ArmyStance.Balanced, 0.28f),
        ["defensive"] = new("defensive", 0.06f, 0.38f, 0.18f, 0.44f,
            ArmyStance.Balanced, ArmyStance.Cautious, 0.42f),
        ["minor_power"] = new("minor_power", 0.10f, 0.52f, 0.28f, 0.30f,
            ArmyStance.Balanced, ArmyStance.Cautious, 0.24f)
    };

    private static readonly Dictionary<string, AiStrategyDefinition> Definitions = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, string> CountryProfiles = new(StringComparer.OrdinalIgnoreCase);

    static AiStrategyCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        foreach (var entry in Defaults) Definitions[entry.Key] = entry.Value;
        CountryProfiles.Clear();
        var file = GameDataLoader.FindDataFile("common", "ai_strategy", "strategies.json");
        if (file is null) return;
        try { Load(File.ReadAllText(file), null); }
        catch { /* Compiled defaults deliberately remain bootable. */ }
    }

    public static void Load(string json, List<string> errors)
    {
        using var document = JsonDocument.Parse(json, new JsonDocumentOptions
        {
            AllowTrailingCommas = true,
            CommentHandling = JsonCommentHandling.Skip
        });
        if (document.RootElement.TryGetProperty("profiles", out var profiles) && profiles.ValueKind == JsonValueKind.Object)
        foreach (var entry in profiles.EnumerateObject())
        {
            var value = entry.Value;
            var baseline = Definitions.GetValueOrDefault(entry.Name) ?? Definitions["default"] with { Id = entry.Name };
            float Number(string key, float fallback) => value.TryGetProperty(key, out var element) && element.TryGetSingle(out var number)
                ? number : fallback;
            ArmyStance Stance(string key, ArmyStance fallback) => value.TryGetProperty(key, out var element) &&
                element.ValueKind == JsonValueKind.String && Enum.TryParse<ArmyStance>(element.GetString(), true, out var stance)
                ? stance : fallback;
            var profile = baseline with
            {
                SpearheadShare = M.Clamp01(Number("spearhead_share", baseline.SpearheadShare)),
                AdvanceShareAttacker = M.Clamp01(Number("advance_share_attacker", baseline.AdvanceShareAttacker)),
                AdvanceShareDefender = M.Clamp01(Number("advance_share_defender", baseline.AdvanceShareDefender)),
                DefendShare = M.Clamp01(Number("defend_share", baseline.DefendShare)),
                OffensiveStance = Stance("offensive_stance", baseline.OffensiveStance),
                DefensiveStance = Stance("defensive_stance", baseline.DefensiveStance),
                ExecutePlanningThreshold = M.Clamp01(Number("execute_planning_threshold", baseline.ExecutePlanningThreshold))
            };
            if (profile.SpearheadShare > profile.AdvanceShareAttacker || profile.AdvanceShareAttacker + profile.DefendShare > 1.05f)
            {
                errors?.Add($"AI profile '{entry.Name}' has overlapping force shares.");
                continue;
            }
            Definitions[entry.Name] = profile;
        }
        if (document.RootElement.TryGetProperty("country_profiles", out var countries) && countries.ValueKind == JsonValueKind.Object)
            foreach (var entry in countries.EnumerateObject())
                if (entry.Value.ValueKind == JsonValueKind.String && Definitions.ContainsKey(entry.Value.GetString() ?? ""))
                    CountryProfiles[entry.Name] = entry.Value.GetString()!;
                else errors?.Add($"Country '{entry.Name}' references an unknown AI profile.");
    }

    public static AiStrategyDefinition Resolve(Campaign campaign, StrategicWar war, Nation nation)
    {
        if (CountryProfiles.TryGetValue(nation.Tag, out var explicitProfile)) return Definitions[explicitProfile];
        var armies = campaign.Armies.Where(army => army.NationId == nation.Id).ToArray();
        var organisation = armies.Length == 0 ? 0f : armies.Average(army => army.Organisation);
        var supply = armies.Length == 0 ? 0f : armies.Average(army => army.SupplyLevel);
        if (nation.WarSupport < 0.45f || organisation < 0.46f || supply < 0.48f) return Definitions["defensive"];
        if (nation.Gdp >= 500f || armies.Length >= 8) return Definitions["major_offensive"];
        return armies.Length <= 2 ? Definitions["minor_power"] : Definitions["default"];
    }
}
