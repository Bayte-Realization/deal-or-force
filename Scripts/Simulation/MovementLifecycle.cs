using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int MovementEmergenceReviewDays { get; set; } = 30;
    public int MovementRebirthDays { get; set; } = 365;
    public int MovementQuietDissolutionDays { get; set; } = 240;
    public int MovementEmergencePerReview { get; set; } = 2;
    public float MovementMinimumAudienceShare { get; set; } = .08f;
}

/// <summary>Organic civic organisation, not a disguised government-in-exile generator.
/// Reviews a bounded data-defined pool, records dormancy in the existing saved action
/// clock, and reuses identity when an organisation reforms.</summary>
public static class MovementLifecycle
{
    public static float Pressure(Campaign campaign, MovementDefinition definition)
    {
        var strongest = 0f;
        foreach (var id in definition.Homeland)
        {
            var region = Movements.StateById(campaign, id);
            if (region is null || region.Population <= 0) continue;
            var owner = MovementRules.OwnerOf(campaign, region);
            if (owner is null || owner.Landless) continue;
            var people = MovementRules.PeopleOf(campaign, region, definition, out _);
            if (people / (float)region.Population < campaign.Defines.MovementMinimumAudienceShare) continue;
            var grievance = region.Radicalism * .6f + (1f - region.Stability) * .25f;
            if (campaign.Wars.Any(w => !w.Ended && w.Involves(owner.Id))) grievance += .08f;
            // Self-government debates can organise peacefully without requiring misery.
            if (definition.Has("civic_independence")) grievance += .14f;
            strongest = MathF.Max(strongest, M.Clamp01(grievance * NationalModifiers.Factor(owner, "movement_activity")));
        }
        return strongest;
    }

    public static void Tick(Campaign campaign)
    {
        if (!campaign.FeatureEnabled("movement_lifecycle") || !campaign.FeatureEnabled("movements")) return;
        var interval = Math.Max(7, campaign.Defines.MovementEmergenceReviewDays);
        if (campaign.ElapsedDays <= 0 || campaign.ElapsedDays % interval != 0) return;
        var created = 0;
        foreach (var definition in MovementCatalog.All)
        {
            if (!definition.CanEmerge || definition.Template) continue;
            var nation = campaign.Nations.FirstOrDefault(n => n.Movement?.Id == definition.Id);
            var pressure = Pressure(campaign, definition);
            if (nation?.Movement is { Dissolved: false } state)
            {
                if (!state.Landless || nation.IsHuman) continue;
                var following = state.Support.Count > 0 ? state.Support.Values.Max() : 0f;
                var quiet = pressure < definition.EmergenceThreshold * .6f && following < .15f;
                if (!quiet) { state.ReadyOnDay.Remove("lifecycle_quiet"); continue; }
                if (!state.ReadyOnDay.TryGetValue("lifecycle_quiet", out var since))
                    state.ReadyOnDay["lifecycle_quiet"] = campaign.ElapsedDays;
                else if (campaign.ElapsedDays - since >= Math.Max(interval, campaign.Defines.MovementQuietDissolutionDays))
                    MovementFall.Dissolve(campaign, nation, Loc.Get("Its demands have lost their following; the organisation has quietly disbanded."));
                continue;
            }
            if (created >= Math.Clamp(campaign.Defines.MovementEmergencePerReview, 0, 10) || pressure < definition.EmergenceThreshold) continue;
            if (nation is not null && (nation.IsHuman || nation.Movement.ReadyOnDay.GetValueOrDefault("lifecycle_rebirth") > campaign.ElapsedDays)) continue;
            // Stable ID hash, not process-random string.GetHashCode; same peers draw the same result.
            var hash = 17;
            foreach (var character in definition.Id) hash = unchecked(hash * 31 + character);
            var chance = definition.EmergenceChance * (.5f + .5f * pressure);
            if (MovementRules.Roll(campaign.Seed, hash, campaign.ElapsedDays, 131) >= chance) continue;
            var before = campaign.MovementRevision;
            Movements.Activate(campaign, definition);
            if (campaign.MovementRevision > before) created++;
        }
    }
}
