using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool BeliefConversionEnabled { get; set; } = true;
    /// <summary>Annual aggregate game rate, not a real-world forecast. A neutral secular state has no target.</summary>
    public float BeliefConversionAnnual { get; set; } = .008f;
}

/// <summary>Beliefs use real population cohorts, including atheists. A declaration is not an instant census rewrite.</summary>
public static class BeliefDynamics
{
    public static long Review(Campaign campaign, Nation owner, ScenarioState region, int days)
    {
        if (campaign is null || owner is null || region is null || region.Unclaimed || region.Population <= 0 ||
            region.NationMapIndex != owner.MapIndex || !campaign.Defines.BeliefConversionEnabled ||
            !campaign.FeatureEnabled("state_identity") || StateIdentity.IsSecular(owner.DeclaredReligion)) return 0;
        var target = StateIdentity.CanonicalFaith(owner.DeclaredReligion);
        var annual = campaign.Defines.BeliefConversionAnnual;
        if (DemographyCatalog.Religion(target) is null || !float.IsFinite(annual) || annual <= 0 ||
            !float.IsFinite(region.Literacy) || !float.IsFinite(region.Radicalism) || !float.IsFinite(region.Stability)) return 0;
        var rate = Math.Clamp(annual, 0, .1f) * Math.Clamp(days, 1, 365) / 365d *
            (.25 + .75 * M.Clamp01(region.Literacy)) *
            (1 - .75 * M.Clamp01(region.Radicalism)) * (.5 + .5 * M.Clamp01(region.Stability));
        var groups = region.PopulationGroups;
        var originalCount = groups.Count;
        Dictionary<(SocialClass, Sector, string, string), PopulationGroup> targets = null;
        long changed = 0;
        for (var i = 0; i < originalCount; i++)
        {
            var source = groups[i];
            if (source.Size <= 0 || StateIdentity.SameFaith(source.Religion, target)) continue;
            var moved = Math.Min(source.Size, (long)Math.Floor(source.Size * rate));
            if (moved <= 0) continue;
            // A late save can have hundreds of cohorts in one region. Index target
            // counterparts once, rather than scanning every cohort for every convert.
            if (targets is null)
            {
                targets = new();
                foreach (var candidate in groups)
                    if (StateIdentity.SameFaith(candidate.Religion, target))
                        targets.TryAdd((candidate.Class, candidate.Sector, candidate.Culture, candidate.Profession), candidate);
            }
            var key = (source.Class, source.Sector, source.Culture, source.Profession);
            if (!targets.TryGetValue(key, out var destination))
            {
                destination = new PopulationGroup { Class = source.Class, Sector = source.Sector,
                    Culture = source.Culture, Profession = source.Profession, Religion = target };
                groups.Add(destination);
                targets.Add(key, destination);
            }
            var count = destination.Size + moved;
            float Mean(float old, float added) => (float)((old * (double)destination.Size + added * (double)moved) / count);
            destination.Employment = Mean(destination.Employment, source.Employment);
            destination.Wealth = Mean(destination.Wealth, source.Wealth);
            destination.StandardOfLiving = Mean(destination.StandardOfLiving, source.StandardOfLiving);
            destination.Radicalism = Mean(destination.Radicalism, source.Radicalism);
            destination.Size = count; source.Size -= moved; changed += moved;
        }
        if (changed > 0) campaign.MovementPeople.Week = -1;
        return changed;
    }

    /// <summary>Existing civil-war gates/budget choose whether anyone rises. This only identifies
    /// a locally rooted worldview for an already justified revolt, symmetrically for every creed.</summary>
    public static string RebelFaith(Campaign campaign, Nation parent, IReadOnlyList<ScenarioState> regions)
    {
        if (campaign is null || parent is null || regions is null || !campaign.FeatureEnabled("state_identity") ||
            string.IsNullOrEmpty(parent.DeclaredReligion) || StateIdentity.IsSecular(parent.DeclaredReligion)) return "";
        var counts = new Dictionary<string, long>(StringComparer.OrdinalIgnoreCase);
        long total = 0; double anger = 0;
        foreach (var region in regions)
            foreach (var group in region.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                total += group.Size; anger += group.Size * (double)region.Radicalism;
                var faith = StateIdentity.CanonicalFaith(group.Religion);
                counts[faith] = counts.GetValueOrDefault(faith) + group.Size;
            }
        if (total <= 0 || anger / total < .5) return "";
        string best = ""; long support = 0;
        foreach (var (faith, size) in counts)
            if (!StateIdentity.SameFaith(faith, parent.DeclaredReligion) && DemographyCatalog.Religion(faith) is not null &&
                (size > support || size == support && string.CompareOrdinal(faith, best) < 0))
            { best = faith; support = size; }
        return support > total / 2 ? best : "";
    }
}
