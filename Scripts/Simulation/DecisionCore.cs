using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum DecisionKind
{
    EconomicStimulus,
    EducationReform,
    PublicHealthCampaign,
    AntiCorruptionDrive,
    EmergencyMobilisation
}

public readonly record struct DecisionDefinition(
    string Id, string Name, string Description, int CooldownDays, float GdpCostShare, DecisionKind Kind);

public static class DecisionCatalog
{
    private static readonly DecisionDefinition[] Defaults =
    {
        new("economic_stimulus", "Economic stimulus",
            "Fund domestic demand and industrial orders. Immediate GDP and stability, paid from the treasury.",
            365, 0.0040f, DecisionKind.EconomicStimulus),
        new("education_reform", "National education reform",
            "Raise literacy and productivity; universities amplify the result over time.",
            730, 0.0025f, DecisionKind.EducationReform),
        new("public_health", "Public health campaign",
            "Improve living standards and population growth throughout the country.",
            540, 0.0018f, DecisionKind.PublicHealthCampaign),
        new("anti_corruption", "Anti-corruption drive",
            "Improve tax collection and state stability at a short-term administrative cost.",
            600, 0.0012f, DecisionKind.AntiCorruptionDrive),
        new("emergency_mobilisation", "Emergency mobilisation",
            "Restore frontline readiness during an active war, at a cost to stability and the economy.",
            180, 0.0020f, DecisionKind.EmergencyMobilisation)
    };
    private static readonly List<DecisionDefinition> Definitions = new();

    static DecisionCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        Definitions.AddRange(Defaults);
        foreach (var document in GameDataLoader.DataDocuments("common", "decisions"))
            using (document)
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                var value = entry.Value;
                if (!value.TryGetProperty("kind", out var kindValue) || kindValue.ValueKind != JsonValueKind.String ||
                    !Enum.TryParse<DecisionKind>(kindValue.GetString(), true, out var kind)) continue;
                Override(entry.Name,
                    value.TryGetProperty("name", out var name) ? name.GetString() : null,
                    value.TryGetProperty("description", out var description) ? description.GetString() : null,
                    value.TryGetProperty("cooldown_days", out var cooldown) && cooldown.TryGetInt32(out var days) ? days : null,
                    value.TryGetProperty("gdp_cost_share", out var cost) && cost.TryGetSingle(out var share) ? share : null,
                    kind);
            }
    }

    public static IReadOnlyList<DecisionDefinition> All => Definitions;
    public static DecisionDefinition? Find(string id)
    {
        foreach (var definition in Definitions)
            if (string.Equals(definition.Id, id, StringComparison.Ordinal)) return definition;
        return null;
    }

    public static bool Override(string id, string name, string description, int? cooldownDays,
        float? gdpCostShare, DecisionKind kind)
    {
        if (string.IsNullOrWhiteSpace(id) || cooldownDays is <= 0 || gdpCostShare is < 0f) return false;
        var index = Definitions.FindIndex(definition => string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase));
        var current = index >= 0 ? Definitions[index] : new DecisionDefinition(id, id, "", 365, 0.001f, kind);
        var updated = current with
        {
            Name = string.IsNullOrWhiteSpace(name) ? current.Name : name,
            Description = string.IsNullOrWhiteSpace(description) ? current.Description : description,
            CooldownDays = cooldownDays ?? current.CooldownDays,
            GdpCostShare = gdpCostShare ?? current.GdpCostShare,
            Kind = kind
        };
        if (index >= 0) Definitions[index] = updated;
        else Definitions.Add(updated);
        return true;
    }

    public static float Cost(Nation nation, DecisionDefinition definition) =>
        Math.Max(0.05f, nation.Gdp * definition.GdpCostShare);

    public static string BlockReason(Campaign campaign, Nation nation, DecisionDefinition definition)
    {
        if (nation.DecisionAvailableDay.GetValueOrDefault(definition.Id) > campaign.ElapsedDays)
            return $"Available in {nation.DecisionAvailableDay[definition.Id] - campaign.ElapsedDays} days";
        if (nation.Treasury < Cost(nation, definition)) return "Insufficient treasury";
        if (definition.Kind == DecisionKind.EmergencyMobilisation &&
            !campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id))) return "Requires an active war";
        return "";
    }
}

public sealed class TakeDecisionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string DecisionId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        var nation = campaign.NationById(IssuerId);
        var definition = DecisionCatalog.Find(DecisionId);
        if (nation is null || definition is null ||
            DecisionCatalog.BlockReason(campaign, nation, definition.Value).Length > 0) return;

        var item = definition.Value;
        nation.Treasury -= DecisionCatalog.Cost(nation, item);
        nation.DecisionAvailableDay[item.Id] = campaign.ElapsedDays + item.CooldownDays;
        switch (item.Kind)
        {
            case DecisionKind.EconomicStimulus:
                nation.Gdp *= 1.006f;
                nation.Stability = M.Clamp01(nation.Stability + 0.015f);
                nation.ConstructionCapacity += 0.12f;
                break;
            case DecisionKind.EducationReform:
                nation.Literacy = M.Clamp01(nation.Literacy + 0.018f);
                nation.Productivity += 0.014f;
                foreach (var state in campaign.StatesOf(nation))
                    state.Literacy = M.Clamp01(state.Literacy + 0.012f);
                break;
            case DecisionKind.PublicHealthCampaign:
                nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + 0.018f);
                foreach (var state in campaign.StatesOf(nation))
                    state.StandardOfLiving = M.Clamp01(state.StandardOfLiving + 0.014f);
                break;
            case DecisionKind.AntiCorruptionDrive:
                nation.Stability = M.Clamp01(nation.Stability + 0.025f);
                nation.TaxRate = M.Clamp(nation.TaxRate + 0.005f, 0f, 0.60f);
                break;
            case DecisionKind.EmergencyMobilisation:
                nation.Stability = M.Clamp01(nation.Stability - 0.025f);
                nation.WarSupport = M.Clamp01(nation.WarSupport + 0.08f);
                nation.Gdp *= 0.998f;
                foreach (var army in campaign.Armies.Where(army => army.NationId == nation.Id))
                {
                    army.Organisation = M.Clamp01(army.Organisation + 0.22f);
                    army.Strength = M.Clamp01(army.Strength + 0.08f);
                }
                break;
        }
        campaign.MarkStrategicChange();
    }
}
