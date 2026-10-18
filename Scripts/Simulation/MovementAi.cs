using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Days into a campaign before a movement nobody plays will rise at all.</summary>
    public int MovementAiUprisingAfterDays { get; set; } = 365;
    /// <summary>The chance of success below which it will not risk an uprising.</summary>
    public float MovementAiUprisingChance { get; set; } = 0.55f;
    /// <summary>The chance, in a week it could rise, that it does.</summary>
    public float MovementAiUprisingAppetite { get; set; } = 0.15f;
    public bool MovementAiUseHomelandOpenings { get; set; } = true;
    public float MovementAiHomelandOpeningChance { get; set; } = 0.8f;
    public float MovementAiHomelandOpeningAppetite { get; set; } = 0.45f;
}

/// <summary>
/// What a movement nobody plays does with its week. It gives the same orders a player
/// would (<see cref="MovementActionCommand"/>), at most two a week, in the order a sensible
/// leadership would: keep the host sweet, get out if it has to, rise where it would win, be
/// recognised, use what makes it itself, and otherwise build its following.
/// </summary>
public static class MovementAi
{
    /// <summary>A public crisis is an opening, not free support, cadres or a successful
    /// uprising. Think still validates every command, cost, occupation and methods gate.</summary>
    internal static float UprisingAppetite(Campaign c, Nation movement, MovementDefinition definition,
        ScenarioState region, float chance)
    {
        if (c is null || movement is null || definition is null ||
            !MovementMethods.WillRise(c, movement, definition)) return 0f;
        var basic = M.Clamp01(c.Defines.MovementAiUprisingAppetite);
        if (!c.Defines.MovementAiUseHomelandOpenings || region is null ||
            !MovementDefinition.Listed(definition.Homeland,region.Id) ||
            !float.IsFinite(chance) || chance < c.Defines.MovementAiHomelandOpeningChance) return basic;
        var host = MovementRules.OwnerOf(c,region);
        if (host is null || host.Id == movement.Id ||
            (host.Stability >= .4f && !c.Wars.Any(w => !w.Ended && w.Involves(host.Id)))) return basic;
        return MathF.Max(basic,M.Clamp01(c.Defines.MovementAiHomelandOpeningAppetite));
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The movements' thinking in stages, one movement at a time, for the smoothed step.</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MovementAi))) yield break;
        if (campaign.ElapsedDays == 0) yield break;
        var week = Math.Max(1, campaign.Defines.EconomyTickDays);
        // On the same day as its week (MovementRules.Tick), so it acts on what that week made of it.
        foreach (var nation in campaign.Nations.ToList())
        {
            if (nation.Movement is not { Dissolved: false } || nation.IsHuman ||
                !MovementRules.OnItsDay(campaign, nation.Id, week)) continue;
            using (SimProfiler.Measure("movements"))
            using (SimProfiler.Measure("  mov:ai"))
            using (SimProfiler.Measure("    mov:think")) Think(campaign, nation);
            yield return 0;
        }
        // And what the countries do about them.
        using (SimProfiler.Measure("movements"))
        using (SimProfiler.Measure("  mov:ai"))
        using (SimProfiler.Measure("    mov:dealings")) MovementDealings.Ai(campaign);
    }

    private static void Think(Campaign campaign, Nation nation)
    {
        var state = nation.Movement;
        var definition = Movements.DefinitionOf(nation);
        if (definition is null || state.MovingToStateMapIndex >= 0) return;
        var defines = campaign.Defines;
        var done = 0;

        bool Try(string action, int region = -1, int target = -1)
        {
            if (done >= 2) return false;
            if (MovementActions.Refusal(campaign, nation, action, region, target).Length > 0) return false;
            new MovementActionCommand { IssuerId = nation.Id, Action = action, StateMapIndex = region, TargetNationId = target }
                .Apply(campaign);
            done++;
            return true;
        }

        // The host first: a movement that is thrown out loses a fifth of its people.
        if (state.Heat >= 0.6f) Try("lobby");
        // A government in exile's whole purpose is the way home: it takes it the moment it is open.
        if (state.Exile)
        {
            var home = Restoration.BestRegion(campaign, nation);
            if (home is not null) Try("restore", home.MapIndex);
        }
        if (definition.Has("civic_independence"))
            foreach (var id in definition.Homeland)
                if (Movements.StateById(campaign, id) is { } home && Try("referendum", home.MapIndex)) break;
        // A civic campaign lives where its voters do: it lobbies its way out of a host's temper,
        // and never packs up for a country on the other side of the world. The Quebec campaign
        // used to move to India when Ottawa grew tired of it.
        if (state.Landless && state.Heat >= 0.72f && !definition.Has("civic_independence"))
        {
            var refuge = MovementRules.BestRefuge(campaign, nation, Movements.HostOf(campaign, nation));
            if (refuge is not null) Try("move", refuge.MapIndex);
        }

        // Rise where it would win, and not in the first year of the world - and never, for one
        // that does not take up arms (the humanitarian trait: an order of knights runs hospitals).
        if (campaign.ElapsedDays >= defines.MovementAiUprisingAfterDays && !definition.Has("humanitarian") &&
            MovementMethods.WillRise(campaign, nation, definition))
        {
            // The homeland first, then the likeliest.
            var forHire = definition.Has("for_hire");
            ScenarioState target = null;
            var targetHome = false;
            var targetChance = 0f;
            foreach (var region in campaign.States)
            {
                if (region.NationMapIndex == nation.MapIndex) continue;
                if (forHire ? region.Stability > 0.35f
                            : state.Support.GetValueOrDefault(region.MapIndex) < defines.MovementUprisingSupport) continue;
                var chance = MovementRules.UprisingChance(campaign, nation, region);
                if (chance < defines.MovementAiUprisingChance) continue;
                var home = MovementDefinition.Listed(definition.Homeland, region.Id);
                if (target is not null && (targetHome && !home || targetHome == home && chance <= targetChance)) continue;
                target = region;
                targetHome = home;
                targetChance = chance;
            }
            if (target is not null &&
                MovementRules.Roll(campaign.Seed, nation.Id, campaign.ElapsedDays, 17) <
                    UprisingAppetite(campaign,nation,definition,target,targetChance))
                Try("rise", target.MapIndex);
        }

        // Recognition from whoever thinks best of it.
        Nation asked = null;
        var warmest = float.MinValue;
        foreach (var other in campaign.Nations)
        {
            if (other.Movement is not null || other.Id == nation.Id || state.RecognisedBy.Contains(other.Id) ||
                campaign.StatesOf(other).Length == 0) continue;
            var opinion = CampaignDiplomacy.Get(campaign, other.Id, nation.Id).Opinion;
            if (opinion <= warmest) continue;
            warmest = opinion;
            asked = other;
        }
        if (asked is not null && CampaignDiplomacy.Get(campaign, asked.Id, nation.Id).Opinion >=
            MovementActions.RecognitionNeeds(defines, state))
            Try("recognition", target: asked.Id);

        // The street is its army: at war with ground of its own, it hands out rifles where it is
        // followed, and before a rising it sends fighters in ahead.
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        if (!state.Landless && atWar)
            foreach (var held in campaign.StatesOf(nation))
                if (state.Support.GetValueOrDefault(held.MapIndex) >= 0.2f && Try("call_to_arms", held.MapIndex)) break;
        if (!definition.Has("humanitarian") && !definition.Has("nonviolence") && !definition.Has("for_hire") &&
            state.Cadres >= defines.MovementInfiltrateFighters * 2f)
        {
            ScenarioState ahead = null;
            var aheadSupport = 0.25f;
            foreach (var id in definition.Homeland)
            {
                var region = Movements.StateById(campaign, id);
                if (region is null || region.NationMapIndex == nation.MapIndex) continue;
                var support = state.Support.GetValueOrDefault(region.MapIndex);
                if (support < aheadSupport || state.Infiltrated.GetValueOrDefault(region.MapIndex) >= defines.MovementInfiltrateFighters * 2f) continue;
                aheadSupport = support;
                ahead = region;
            }
            if (ahead is not null) Try("infiltrate", ahead.MapIndex);
        }

        // What makes it itself.
        var homeland = definition.Homeland
            .Select(id => Movements.StateById(campaign, id))
            .Where(region => region is not null && region.NationMapIndex != nation.MapIndex)
            .OrderBy(region => state.Support.GetValueOrDefault(region.MapIndex))
            .FirstOrDefault();
        if (definition.Has("charity_network") && homeland is not null) Try("services", homeland.MapIndex);
        if (definition.Has("government_in_exile")) Try("appeal");
        if (definition.Has("street_patrols") && state.Heat < 0.45f) Try("patrols");
        if (definition.Has("hospitallers"))
        {
            // The neediest region it may work in. Ranked by need first and asked about in that
            // order, because asking every one of the world's regions whether it may - each
            // answer a sentence composed for nobody - was a fifth of a second a week for one
            // order of knights. The same region wins: the ranking is stable, so of two equally
            // needy regions the earlier still comes first, as it did.
            ScenarioState needy = null;
            foreach (var region in campaign.States
                         .Where(region => region.Population > 0)
                         .OrderByDescending(region => region.WarDamage * 2f + (1f - region.StandardOfLiving)))
            {
                if (MovementActions.Refusal(campaign, nation, "hospital", region.MapIndex, -1).Length > 0) continue;
                needy = region;
                break;
            }
            if (needy is not null) Try("hospital", needy.MapIndex);
        }
        if (definition.Has("for_hire") && state.ContractNationId < 0)
        {
            var client = campaign.Nations
                .Where(other => other.Movement is null && campaign.Wars.Any(war => !war.Ended && war.Involves(other.Id)))
                .OrderByDescending(other => other.Treasury)
                .FirstOrDefault();
            if (client is not null) Try("contract", target: client.Id);
        }

        // Otherwise the following: where most of its people are still to be won over.
        ScenarioState ground = null;
        var groundScore = 0f;
        foreach (var region in campaign.States)
        {
            if (region.NationMapIndex == nation.MapIndex) continue;
            var people = MovementRules.PeopleOf(campaign, region, definition, out _);
            if (people <= 0 || !MovementReach.CanOperate(campaign, nation, region, definition)) continue;
            var score = people * (1f - state.Support.GetValueOrDefault(region.MapIndex)) *
                        (MovementDefinition.Listed(definition.Homeland, region.Id) ? 3f : 1f);
            if (score <= groundScore) continue;
            groundScore = score;
            ground = region;
        }
        if (ground is not null && state.Heat < 0.6f) Try("campaign", ground.MapIndex);

        if (state.Cadres < defines.MovementUprisingCadres * 2f && nation.Treasury > 0.1f) Try("recruit");
        if (nation.Treasury < 0.05f) Try("fundraise");
    }
}
