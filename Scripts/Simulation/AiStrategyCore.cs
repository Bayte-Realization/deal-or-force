using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

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
        foreach (var file in GameDataLoader.DataFiles("common", "ai_strategy"))
        {
            try { Load(File.ReadAllText(file), null); }
            catch { /* One unreadable file costs that file, not the whole category. */ }
        }
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

    /// <summary>
    /// What one country's army amounts to: how many formations, and their organisation and
    /// supply - totalled while the table is built, averaged when it is read.
    /// </summary>
    private readonly record struct Posture(int Formations, float Organisation, float Supply);

    private static readonly Dictionary<int, Posture> Postures = new();
    private static Campaign _postureCampaign;
    private static int _postureDay = int.MinValue;

    /// <summary>
    /// Every country's army, counted once a day.
    ///
    /// Stamped with the campaign it describes as well as the day, because a client
    /// rebuilding from a host's snapshot holds two campaigns at once and a table that only
    /// knows what day it is would answer one of them with the other's army.
    /// </summary>
    private static Posture PostureOf(Campaign campaign, Nation nation)
    {
        if (_postureDay != campaign.ElapsedDays || !ReferenceEquals(_postureCampaign, campaign))
        {
            _postureDay = campaign.ElapsedDays;
            _postureCampaign = campaign;
            Postures.Clear();
            foreach (var army in campaign.Armies)
            {
                var running = Postures.GetValueOrDefault(army.NationId);
                Postures[army.NationId] = new Posture(running.Formations + 1,
                    running.Organisation + army.Organisation, running.Supply + army.SupplyLevel);
            }
        }

        // Totals go in, averages come out - dividing here rather than in a second pass over
        // the table, so the day's work is one walk of the order of battle and nothing else.
        var summed = Postures.GetValueOrDefault(nation.Id);
        return summed.Formations == 0
            ? summed
            : new Posture(summed.Formations,
                summed.Organisation / summed.Formations, summed.Supply / summed.Formations);
    }

    /// <summary>
    /// Which way this country intends to fight.
    ///
    /// Asked for every formation of every country in every live war, every day - which is
    /// tens of thousands of times a day in a busy world. It used to answer by filtering the
    /// whole world's order of battle into a fresh array and averaging it twice, with a
    /// closure and two delegates for each: three quarters of everything the war pass
    /// allocated, and by a distance the largest single source of garbage in the game.
    ///
    /// The answer only changes when the army does, so it is worked out once a day for
    /// everybody at once and read back here.
    /// </summary>
    public static AiStrategyDefinition Resolve(Campaign campaign, StrategicWar war, Nation nation)
    {
        if (CountryProfiles.TryGetValue(nation.Tag, out var explicitProfile)) return Definitions[explicitProfile];
        var posture = PostureOf(campaign, nation);
        if (nation.WarSupport < 0.45f || posture.Organisation < 0.46f || posture.Supply < 0.48f)
            return Definitions["defensive"];
        if (nation.Gdp >= 500f || posture.Formations >= 8) return Definitions["major_offensive"];
        return posture.Formations <= 2 ? Definitions["minor_power"] : Definitions["default"];
    }
}
