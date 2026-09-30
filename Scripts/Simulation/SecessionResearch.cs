using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Scientists and technical institutions do not vanish when a border moves, but a new
/// government cannot instantly operate every parent programme. Local education, urban
/// industry, the relevant facilities and the size of the departing population determine
/// what continues functioning; the rest survives as research progress, not a free unlock.
/// </summary>
public static class SecessionResearch
{
    public static void Inherit(Campaign campaign, Nation parent, Nation born,
        IReadOnlyList<ScenarioState> departed, long parentPopulationBeforeSecession)
    {
        if (campaign is null || parent is null || born is null || departed is null || departed.Count == 0) return;
        var people = Math.Max(1L, departed.Sum(state => Math.Max(0L, state.Population)));
        var literacy = 0f;
        var urban = 0f;
        var radicalism = 0f;
        var universities = 0;
        var labs = 0;
        foreach (var state in departed)
        {
            var share = Math.Max(0L, state.Population) / (float)people;
            literacy += M.Clamp01(state.Literacy) * share;
            urban += M.Clamp01(state.Urbanization) * share;
            radicalism += M.Clamp01(state.Radicalism) * share;
            universities += state.Buildings.GetValueOrDefault(BuildingKind.University);
            labs += state.Buildings.GetValueOrDefault(BuildingKind.ResearchLab);
        }
        var millions = MathF.Max(0.1f, people / 1_000_000f);
        var institutions = M.Clamp01((universities + labs * 2f) / MathF.Max(1f, millions * 2f));
        var footprint = MathF.Sqrt(M.Clamp01(people / (float)Math.Max(1L, parentPopulationBeforeSecession)));
        var common = 0.30f + 0.23f * literacy + 0.10f * urban +
            0.16f * institutions + 0.05f * footprint - 0.06f * radicalism;
        var known = parent.Technologies
            .Select(TechnologyCatalog.Of)
            .Where(technology => technology is not null)
            .OrderBy(technology => technology.Cost)
            .ThenBy(technology => technology.Id, StringComparer.Ordinal)
            .ToArray();

        // Repeat because a mod can give a prerequisite a greater cost than its child.
        // No completed technology may arrive without its own prerequisites.
        var changed = true;
        while (changed)
        {
            changed = false;
            foreach (var technology in known)
            {
                if (born.HasTechnology(technology.Id) || !technology.Requires.All(born.HasTechnology)) continue;
                var capacity = Capacity(common, millions, departed, technology.Category);
                var difficulty = 0.32f + MathF.Max(0f, campaign.Defines.ResearchSecessionComplexity) *
                    M.Clamp01((technology.Cost - 100f) / 500f) + technology.Requires.Count * 0.025f;
                if (capacity < difficulty) continue;
                born.Technologies.Add(technology.Id);
                changed = true;
            }
        }

        // The parent retains its own work. Knowledge is copied, not deducted: the new
        // border did not make books or unfinished experiments disappear on either side.
        foreach (var technology in TechnologyCatalog.All)
        {
            if (born.HasTechnology(technology.Id)) continue;
            var capacity = Capacity(common, millions, departed, technology.Category);
            var fraction = M.Clamp01(campaign.Defines.ResearchSecessionProgressShare) *
                M.Clamp(0.20f + capacity * 0.65f, 0.1f, 0.8f);
            var cost = ResearchSystem.CostFor(campaign, born, technology);
            var progress = parent.HasTechnology(technology.Id) ? cost * fraction : 0f;
            progress = MathF.Max(progress, parent.ResearchProgress.GetValueOrDefault(technology.Id) * fraction);
            if (progress > 0f) born.ResearchProgress[technology.Id] = MathF.Min(cost * 0.8f, progress);
        }

        var inheritedProject = TechnologyCatalog.Of(parent.CurrentResearch);
        if (ResearchSystem.Available(born, inheritedProject)) born.CurrentResearch = inheritedProject.Id;
    }

    private static float Capacity(float common, float millions, IReadOnlyList<ScenarioState> states, string category)
    {
        var facilities = 0f;
        foreach (var state in states)
            facilities += category switch
            {
                "military" => state.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry) * 2f +
                    state.Buildings.GetValueOrDefault(BuildingKind.Barracks),
                "logistics" => state.Buildings.GetValueOrDefault(BuildingKind.RailNetwork) * 2f +
                    state.Buildings.GetValueOrDefault(BuildingKind.Port) +
                    state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) * 0.5f,
                "society" => state.Buildings.GetValueOrDefault(BuildingKind.University) * 2f +
                    state.Buildings.GetValueOrDefault(BuildingKind.Hospital),
                _ => state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) +
                    state.Buildings.GetValueOrDefault(BuildingKind.ElectronicsPlant) * 2f +
                    state.Buildings.GetValueOrDefault(BuildingKind.PowerPlant)
            };
        return M.Clamp01(common + 0.13f * M.Clamp01(facilities / MathF.Max(1f, millions * 2f)));
    }
}
