using System;
using System.Collections.Generic;
using System.Globalization;

namespace DealOrForce.Simulation;

public static partial class ResearchSystem
{
    /// <summary>
    /// What the research card says about using a technology the country already has.
    /// Reading it does not write familiarity.
    /// </summary>
    public readonly record struct FieldUseView(float Points, float Ceiling, bool Enabled, bool Practicing, float ThisWeek);

    /// <summary>The building a technology's icon names, when it names one.</summary>
    private static readonly Dictionary<string, BuildingKind> IndustryByIcon = new(StringComparer.OrdinalIgnoreCase)
    {
        ["icon_building_agriculture"] = BuildingKind.Agriculture,
        ["icon_building_civilianindustry"] = BuildingKind.CivilianIndustry,
        ["icon_building_infrastructure"] = BuildingKind.Infrastructure,
        ["icon_building_extractionindustry"] = BuildingKind.ExtractionIndustry,
        ["icon_building_powerplant"] = BuildingKind.PowerPlant,
        ["icon_building_armsindustry"] = BuildingKind.ArmsIndustry,
        ["icon_building_refinery"] = BuildingKind.Refinery,
        ["icon_building_chemicalplant"] = BuildingKind.ChemicalPlant,
        ["icon_building_smelter"] = BuildingKind.Smelter,
        ["icon_building_electronicsplant"] = BuildingKind.ElectronicsPlant,
        ["icon_building_shipyard"] = BuildingKind.Shipyard,
        // Machinery is made in civilian plants. A machinery icon with no such plant is idle.
        ["icon_good_machinery"] = BuildingKind.CivilianIndustry,
    };

    public static bool FieldUseEnabled(Campaign campaign) =>
        UseShare(campaign) > 0f && UseCeilingShare(campaign) > 0f;

    /// <summary>Familiarity already stored, or zero when the technology has never been practiced.</summary>
    public static float Familiarity(Nation nation, string technologyId)
    {
        if (nation is null || string.IsNullOrEmpty(technologyId)) return 0f;
        return nation.TechnologyFamiliarity.TryGetValue(technologyId, out var value) && float.IsFinite(value) && value > 0f
            ? value : 0f;
    }

    /// <summary>The most research points practice can put on this technology.</summary>
    public static float FamiliarityCeiling(Campaign campaign, TechnologyDefinition technology)
    {
        if (!FieldUseEnabled(campaign) || technology is null) return 0f;
        return MathF.Max(0f, technology.Cost) * UseCeilingShare(campaign);
    }

    /// <summary>
    /// Points of this week's research spent on technologies actually in use.
    /// Idle known technologies are left untouched. The caller keeps the remainder for the open project.
    /// </summary>
    public static float ApplyFieldUse(Campaign campaign, Nation nation, float weeklyPoints)
    {
        if (!FieldUseEnabled(campaign) || nation is null || !(weeklyPoints > 0f) || !float.IsFinite(weeklyPoints)) return 0f;
        var practiced = Practiced(campaign, nation);
        if (practiced.Count == 0) return 0f;
        var each = weeklyPoints * UseShare(campaign) / practiced.Count;
        var spent = 0f;
        foreach (var technology in practiced)
        {
            var gain = Room(campaign, nation, technology, each);
            if (gain <= 0f) continue;
            nation.TechnologyFamiliarity[technology.Id] = Familiarity(nation, technology.Id) + gain;
            spent += gain;
        }
        return spent;
    }

    public static FieldUseView FieldUseOf(Campaign campaign, Nation nation, TechnologyDefinition technology, float weeklyPoints)
    {
        if (campaign is null || nation is null || technology is null) return default;
        var points = Familiarity(nation, technology.Id);
        if (!FieldUseEnabled(campaign)) return new FieldUseView(points, 0f, false, false, 0f);
        var ceiling = FamiliarityCeiling(campaign, technology);
        var practiced = Practiced(campaign, nation);
        var practicing = false;
        foreach (var candidate in practiced)
            if (string.Equals(candidate.Id, technology.Id, StringComparison.OrdinalIgnoreCase)) { practicing = true; break; }
        var week = practicing ? Room(campaign, nation, technology, practiced.Count == 0 ? 0f : weeklyPoints * UseShare(campaign) / practiced.Count) : 0f;
        return new FieldUseView(points, ceiling, true, practicing, week);
    }

    /// <summary>The research-detail sentence. Numbers are already formatted, so the translation keeps the placeholders.</summary>
    public static string FieldUseLine(Campaign campaign, Nation nation, TechnologyDefinition technology, float weeklyPoints)
    {
        var view = FieldUseOf(campaign, nation, technology, weeklyPoints);
        if (!view.Enabled) return Loc.Get("Field use is off.");
        var points = view.Points.ToString("0.0", CultureInfo.InvariantCulture);
        var ceiling = view.Ceiling.ToString("0.0", CultureInfo.InvariantCulture);
        if (view.Ceiling > 0f && view.Points + 0.0001f >= view.Ceiling)
            return Loc.Format("Field use: {0} / {1} points. The ceiling is reached.", points, ceiling);
        if (!view.Practicing)
            return Loc.Format("Field use: {0} / {1} points. It is not in use, so it does not grow.", points, ceiling);
        var take = view.ThisWeek.ToString("0.00", CultureInfo.InvariantCulture);
        return Loc.Format("Field use: {0} / {1} points. In use; this week it takes {2} from the laboratories.", points, ceiling, take);
    }

    private static float UseShare(Campaign campaign)
    {
        if (campaign is null) return 0f;
        var share = campaign.Defines.ResearchUseShare;
        return float.IsFinite(share) && share > 0f ? MathF.Min(1f, share) : 0f;
    }

    private static float UseCeilingShare(Campaign campaign)
    {
        if (campaign is null) return 0f;
        var ceiling = campaign.Defines.ResearchUseCeiling;
        return float.IsFinite(ceiling) && ceiling > 0f ? MathF.Min(1f, ceiling) : 0f;
    }

    private static float Room(Campaign campaign, Nation nation, TechnologyDefinition technology, float offered)
    {
        if (!(offered > 0f) || !float.IsFinite(offered)) return 0f;
        var room = FamiliarityCeiling(campaign, technology) - Familiarity(nation, technology.Id);
        if (!(room > 0f)) return 0f;
        return MathF.Min(offered, room);
    }

    private static List<TechnologyDefinition> Practiced(Campaign campaign, Nation nation)
    {
        var list = new List<TechnologyDefinition>();
        if (nation.Technologies.Count == 0) return list;
        var fighting = IsFighting(campaign, nation);
        var shipping = IsShipping(nation);
        var industry = new Dictionary<BuildingKind, bool>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var id in nation.Technologies)
        {
            if (string.IsNullOrEmpty(id) || !seen.Add(id)) continue;
            var technology = TechnologyCatalog.Of(id);
            if (technology is not null && Practices(campaign, nation, technology, fighting, shipping, industry))
                list.Add(technology);
        }
        list.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        return list;
    }

    /// <summary>
    /// A known technology is in use only while the country is doing the thing it is for.
    /// A military technology needs a fight, a logistics technology a shipment, and an
    /// industry technology the works its icon names. Society, and an industry icon that
    /// names no works, stay idle.
    /// </summary>
    private static bool Practices(Campaign campaign, Nation nation, TechnologyDefinition technology,
        bool fighting, bool shipping, Dictionary<BuildingKind, bool> industry)
    {
        if (!nation.HasTechnology(technology.Id)) return false;
        var category = technology.Category ?? "";
        if (category.Equals("military", StringComparison.OrdinalIgnoreCase)) return fighting;
        if (category.Equals("logistics", StringComparison.OrdinalIgnoreCase)) return shipping;
        if (!category.Equals("industry", StringComparison.OrdinalIgnoreCase)) return false;
        if (!IndustryByIcon.TryGetValue(technology.Icon ?? "", out var kind)) return false;
        if (!industry.TryGetValue(kind, out var running))
            industry[kind] = running = RunsIndustry(campaign, nation, kind);
        return running;
    }

    private static bool IsFighting(Campaign campaign, Nation nation)
    {
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || army.Divisions <= 0f) continue;
            if (army.InBattle) return true;
            if (army.AssignedWarId < 0) continue;
            foreach (var war in campaign.Wars)
                if (war.Id == army.AssignedWarId && !war.Ended && war.Involves(nation.Id)) return true;
        }
        return false;
    }

    /// <summary>A shipment the country itself sent. Imports are someone else's hulls.</summary>
    private static bool IsShipping(Nation nation)
    {
        foreach (var quote in nation.Market.Values)
            if (quote.Exports > 0.001f) return true;
        return false;
    }

    /// <summary>The named works still stand, still have people, and the war has not flattened them.</summary>
    private static bool RunsIndustry(Campaign campaign, Nation nation, BuildingKind kind)
    {
        foreach (var state in campaign.StatesOf(nation))
        {
            if (state.Buildings.GetValueOrDefault(kind) <= 0) continue;
            if (state.Population <= 0) continue;
            if (WarDamage.OutputFactor(campaign, state) <= 0f) continue;
            return true;
        }
        return false;
    }
}
