using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float CivilAwarenessWeight { get; set; } = 0.28f;
    public float CivilStandingBase { get; set; } = 0.25f;
    public float CivilStandingSupportWeight { get; set; } = 0.5f;
    public float CivilReferendumMajority { get; set; } = 0.5f;
    public float CivilReferendumLegitimacy { get; set; } = 0.55f;
    public float CivilReferendumRestraint { get; set; } = 0.65f;
    public int CivilReferendumDays { get; set; } = 180;
    public int CivilReferendumConsentDays { get; set; } = 365;
    public int CivilReferendumTruceDays { get; set; } = 365;
    /// <summary>
    /// The share of carried votes counted region by region, each region going only if its own
    /// residents voted yes; the rest are one question for the whole homeland the government
    /// holds, and carry all of it (MovementRules.ZoneOf).
    /// </summary>
    public float CivilReferendumByRegionShare { get; set; } = 0.25f;
    /// <summary>
    /// The residents' support at which a vote may be held at all, and how far the count may land
    /// from it either way: a campaign at 47 % can win (Montenegro, 2006: 55.5 % against a 55 %
    /// bar) and one at 53 % can lose (Quebec, 1995: 49.4 %).
    /// </summary>
    public float CivilReferendumHoldAt { get; set; } = 0.42f;
    public float CivilReferendumSwing { get; set; } = 0.06f;
    /// <summary>A lost vote: the standing it costs, the share of the following that drifts away, and how long until the question can be asked again.</summary>
    public float CivilReferendumLostLegitimacy { get; set; } = 0.12f;
    public float CivilReferendumLostSupportKept { get; set; } = 0.85f;
    public int CivilReferendumRetryDays { get; set; } = 1825;
}

/// <summary>Political aspirations are not a synonym for poverty or armed revolt.</summary>
public static class CivilIndependence
{
    public static float AwarenessTarget(Campaign c, MovementDefinition definition, MovementState movement,
        ScenarioState region, Nation owner)
    {
        if (!definition.Has("civic_independence") || owner is null ||
            !MovementDefinition.Listed(definition.Homeland, region.Id) ||
            string.Equals(owner.Tag, definition.Tag, StringComparison.OrdinalIgnoreCase)) return 0f;
        var awareness = c.FeatureEnabled("resident_awareness")
            ? ResidentAwareness.Of(c, region) : M.Clamp01(region.Literacy);
        return M.Clamp(0.14f + awareness * c.Defines.CivilAwarenessWeight + movement.Legitimacy * 0.24f, 0f, 0.75f);
    }

    public static bool HasClaim(Campaign c, Nation movement, Nation owner) =>
        Movements.DefinitionOf(movement) is { } d && d.Has("civic_independence") &&
        d.Homeland.Any(id => Movements.StateById(c, id)?.NationMapIndex == owner.MapIndex);

    public static string ConsentKey(int ownerId) => "referendum_consent:" + ownerId;

    public static float StandingTarget(Campaign c, Nation movement)
    {
        var homes = Movements.DefinitionOf(movement).Homeland.Select(id => Movements.StateById(c, id))
            .Where(r => r is not null && r.Population > 0).ToArray();
        var population = homes.Sum(r => (double)r.Population);
        var support = population > 0 ? homes.Sum(r => r.Population * (double)ResidentSupport(movement, r)) / population : 0;
        return M.Clamp01(c.Defines.CivilStandingBase + (float)support * c.Defines.CivilStandingSupportWeight);
    }

    public static float ResidentSupport(Nation movement, ScenarioState region)
    {
        var definition = Movements.DefinitionOf(movement);
        if (definition is null || region is null || region.Population <= 0) return 0f;
        return M.Clamp01(MovementRules.PeopleOf(region, definition, out _) *
            movement.Movement.Support.GetValueOrDefault(region.MapIndex) / region.Population);
    }

    public static bool ParentConsents(Campaign c, Nation movement, Nation owner)
    {
        if (movement.Movement.ReadyOnDay.GetValueOrDefault(ConsentKey(owner.Id), -1) >= c.ElapsedDays) return true;
        if (owner.IsHuman) return false;
        return PoliticsCore.GovernmentOf(owner).WarRestraint >= c.Defines.CivilReferendumRestraint &&
            owner.Stability >= 0.5f && CampaignDiplomacy.Peek(c, movement.Id, owner.Id)?.EmbargoBy(owner.Id) != true;
    }

    public static string Refusal(Campaign c, Nation movement, ScenarioState region)
    {
        if (!c.FeatureEnabled("secession")) return Loc.Get("Peaceful secession is disabled in this scenario.");
        var d = Movements.DefinitionOf(movement);
        if (d is null || !d.Has("civic_independence") || region is null || !MovementDefinition.Listed(d.Homeland, region.Id))
            return Loc.Get("A referendum must be in this movement's homeland.");
        var owner = c.Nations.FirstOrDefault(n => n.MapIndex == region.NationMapIndex);
        if (owner is null || owner.Id == movement.Id || c.ProvinceMap is null) return Loc.Get("There is no parent state to negotiate with.");
        if (!float.IsFinite(ResidentSupport(movement, region)) || !float.IsFinite(movement.Movement.Legitimacy) ||
            !float.IsFinite(region.Stability) || !float.IsFinite(owner.Stability) || !float.IsFinite(movement.Treasury))
            return Loc.Get("The campaign data is damaged, so this cannot be done.");
        if (CampaignDiplomacy.AtWar(c, movement.Id, owner.Id) || owner.IsSubject)
            return Loc.Get("Not during war or without a sovereign parent.");
        var held = c.StatesOf(owner);
        if (held.Length <= 1 || region.Population > owner.Population * 0.5)
            return Loc.Get("The settlement cannot dissolve the parent state.");
        var votes = ResidentSupport(movement, region);
        if (movement.Movement.ReadyOnDay.GetValueOrDefault("referendum_lost", -1) > c.ElapsedDays)
            return Loc.Get("The question was asked and lost; it cannot be asked again so soon.");
        if (votes < c.Defines.CivilReferendumHoldAt) return Loc.Get("Too few residents support independence to hold a vote.");
        if (movement.Movement.Legitimacy < c.Defines.CivilReferendumLegitimacy)
            return Loc.Get("The movement needs greater public standing.");
        return ParentConsents(c, movement, owner) ? "" : Loc.Get("The parent must authorise a referendum in the movements panel.");
    }

    public static bool Ratify(Campaign c, Nation movement, ScenarioState region)
    {
        if (Refusal(c, movement, region).Length != 0) return false;
        var owner = c.Nations.First(n => n.MapIndex == region.NationMapIndex);
        var place = region.DisplayName;
        // The count: what the residents think, give or take what the campaign's last weeks do.
        var swing = c.Defines.CivilReferendumSwing * (2f * Escalation.Roll(c.Seed, movement.Id, region.MapIndex, c.ElapsedDays, 0x7E1) - 1f);
        var result = M.Clamp01(ResidentSupport(movement, region) + swing);
        if (result <= c.Defines.CivilReferendumMajority)
        {
            var state = movement.Movement;
            state.Legitimacy = M.Clamp01(state.Legitimacy - c.Defines.CivilReferendumLostLegitimacy);
            foreach (var key in state.Support.Keys.ToList())
                state.Support[key] *= M.Clamp01(c.Defines.CivilReferendumLostSupportKept);
            state.ReadyOnDay["referendum_lost"] = c.ElapsedDays + Math.Max(1, c.Defines.CivilReferendumRetryDays);
            state.ReadyOnDay.Remove(ConsentKey(owner.Id));
            state.LastReport = Loc.Format("The vote in {0} was lost: {1} for independence.", place, Fmt.Percent(result));
            c.Chronicle.Record(c, "referendum_lost", movement.Id, owner.Id, Fmt.Percent(result), place);
            return false;
        }
        // A question that carried once is not re-asked county by county for a decade: the
        // rest of the movement's homeland this same government controls votes with it, and
        // every region whose own majority carried comes along. Regions below the majority
        // stay put - the map stays honest about who actually voted for this.
        var taken = MovementRules.TakeZone(c, movement, region, owner, peacefully: true);
        if (taken == 0) return false;
        var relation = CampaignDiplomacy.Get(c, owner.Id, movement.Id);
        relation.Stance = DiplomaticStance.Truce;
        relation.TruceUntil = c.ElapsedDays + Math.Max(1, c.Defines.CivilReferendumTruceDays);
        relation.NonAggressionPact = true;
        if (movement.Movement is not null)
            movement.Movement.LastReport = taken > 1
                ? Loc.Format("A negotiated vote in {0} carried: {1} region(s) are independent.", place, taken)
                : Loc.Format("A negotiated vote in {0} established peaceful independence.", place);
        c.Chronicle.Record(c, "peaceful_independence", movement.Id, owner.Id, place);
        c.MarkMapChanged();
        return true;
    }
}
