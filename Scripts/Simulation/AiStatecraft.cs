using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country does when nobody is playing it.
///
/// Until now: nothing. The operational AI moved divisions once a war existed, but no war
/// ever started - `ai_war_chance` was declared in the defines and read by nothing at all,
/// so two hundred and sixty countries sat at peace forever, never raised a tax, never
/// built anything, never improved or soured a relationship, and never signed a peace they
/// were winning. The scenario's one war was the only thing that ever happened.
///
/// This is the other half of an AI: the half that decides. Every country forms opinions of
/// its neighbours and acts on them; keeps its budget solvent; builds what it is short of;
/// and, when a neighbour is weak, hostile and worth taking, declares war on it and later
/// writes a peace out of what it won.
///
/// It is deliberately cheap. Countries are staggered across the week and the month so no
/// single day pays for all of them, and the border graph every decision leans on is built
/// once per change to the map rather than once per question.
/// </summary>
public static class AiStatecraft
{
    /// <summary>
    /// A running count of what the AI decided and why it stopped deciding. Not campaign
    /// state and not saved: it exists so "the AI never does anything" can be answered with
    /// the step it actually fails at rather than with a shrug.
    /// </summary>
    public static readonly Dictionary<string, int> Tally = new(StringComparer.Ordinal);

    private static void Count(string what) => Tally[what] = Tally.GetValueOrDefault(what) + 1;

    /// <summary>Military strength and a map-index lookup, both good for exactly one day.</summary>
    private static readonly Dictionary<int, float> StrengthCache = new();
    private static readonly Dictionary<int, Nation> NationByMapIndex = new();
    private static int _cacheDay = -1;

    public static void Tick(Campaign campaign)
    {
        if (campaign.ProvinceMap is null || campaign.ProvinceController is null) return;

        // Military strength is a sum over every division in the world, and it was being
        // recomputed for each neighbour of each country every week - which made the AI the
        // most expensive system in the simulation by a factor of three. It changes once a
        // day at most, so it is worked out once a day.
        if (_cacheDay != campaign.ElapsedDays)
        {
            _cacheDay = campaign.ElapsedDays;
            StrengthCache.Clear();
            NationByMapIndex.Clear();
            foreach (var candidate in campaign.Nations)
            {
                NationByMapIndex[candidate.MapIndex] = candidate;
                StrengthCache[candidate.Id] = candidate.Gdp * campaign.Defines.AiEconomicWeight;
            }
            foreach (var army in campaign.Armies)
                if (StrengthCache.ContainsKey(army.NationId))
                    StrengthCache[army.NationId] += army.Divisions * army.Strength *
                                                    M.Max(0.15f, army.Organisation);
        }

        foreach (var nation in campaign.Nations)
        {
            if (nation.IsHuman || nation.Population <= 0) continue;

            // Staggered: a nation thinks about its neighbours once a week and about its
            // budget once a month, on its own day, so the cost is spread rather than spiked.
            if ((campaign.ElapsedDays + nation.Id) % 7 == 0) Diplomacy(campaign, nation);
            if ((campaign.ElapsedDays + nation.Id) % 30 == 0) Economy(campaign, nation);
            if ((campaign.ElapsedDays + nation.Id) % 11 == 0) Warmaking(campaign, nation);
        }
    }

    /// <summary>Countries this one can walk to. One shared index, built once per map change.</summary>
    public static IReadOnlyCollection<int> NeighboursOf(Campaign campaign, int nationMapIndex) =>
        CampaignDiplomacy.Neighbours(campaign, nationMapIndex);

    // --------------------------------------------------------------- diplomacy ---------

    /// <summary>
    /// One country's weekly view of its neighbourhood. Opinions drift toward what the two
    /// countries actually are to each other - a shared border is friction, a shared trade
    /// agreement is not - and then the country does one thing about the neighbour it feels
    /// most strongly about, in either direction.
    /// </summary>
    private static void Diplomacy(Campaign campaign, Nation nation)
    {
        var neighbours = NeighboursOf(campaign, nation.MapIndex);
        if (neighbours.Count == 0) return;
        var defines = campaign.Defines;

        Nation bestFriend = null;
        Nation worstRival = null;
        var bestOpinion = float.MinValue;
        var worstOpinion = float.MaxValue;

        foreach (var mapIndex in neighbours)
        {
            var other = ByMapIndex(mapIndex);
            if (other is null || other.Id == nation.Id) continue;
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
            if (relation.Stance == DiplomaticStance.AtWar) continue;

            // Where this relationship naturally sits: friction from sharing a border,
            // warmth from trade and from a pact somebody bothered to sign.
            var baseline = defines.AiBorderFriction;
            if (relation.TradeAgreement) baseline += defines.AiTradeWarmth;
            if (relation.NonAggressionPact) baseline += defines.AiPactWarmth;
            if (relation.Stance == DiplomaticStance.Allied) baseline += defines.AiAllianceWarmth;
            var strength = Strength(campaign, other) / M.Max(0.01f, Strength(campaign, nation));
            baseline -= M.Clamp(strength - 1f, 0f, 2f) * defines.AiFearOfStrength;

            relation.Opinion = M.Clamp(
                M.Lerp(relation.Opinion, baseline, defines.AiOpinionDrift), -100f, 100f);

            if (relation.Opinion > bestOpinion) { bestOpinion = relation.Opinion; bestFriend = other; }
            if (relation.Opinion < worstOpinion) { worstOpinion = relation.Opinion; worstRival = other; }
        }

        // One action a week, on whichever relationship is furthest from neutral. A country
        // that spent the week courting its friend did not also spend it insulting its rival.
        if (bestFriend is not null && bestOpinion >= defines.AiFriendshipThreshold &&
            MathF.Abs(bestOpinion) >= MathF.Abs(worstOpinion))
        {
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, bestFriend.Id);
            if (!relation.TradeAgreement && bestOpinion >= defines.AiTradeThreshold)
            {
                relation.TradeAgreement = true;
                relation.LastActionDay = campaign.ElapsedDays;
            }
            else if (!relation.NonAggressionPact && bestOpinion >= defines.AiPactThreshold)
            {
                relation.NonAggressionPact = true;
                relation.LastActionDay = campaign.ElapsedDays;
            }
            else if (relation.Stance != DiplomaticStance.Allied && bestOpinion >= defines.AiAllianceThreshold)
            {
                relation.Stance = DiplomaticStance.Allied;
                relation.LastActionDay = campaign.ElapsedDays;
            }
            else if (relation.Stance == DiplomaticStance.Neutral)
            {
                relation.Stance = DiplomaticStance.Friendly;
            }
            return;
        }

        // Nothing has crossed a threshold yet, which is where every relationship starts:
        // neighbours dislike each other by default, so warmth has to be worked at. A
        // country spends the week courting whichever neighbour it is least unhappy with.
        // Left to drift alone, no two countries in the world would ever sign anything.
        if (bestFriend is not null && (worstRival is null || bestOpinion > defines.AiRivalryThreshold))
        {
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, bestFriend.Id);
            if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
            {
                relation.Opinion = M.Clamp(relation.Opinion + defines.AiCourtshipStep, -100f, 100f);
                relation.LastActionDay = campaign.ElapsedDays;
                return;
            }
        }

        if (worstRival is not null && worstOpinion <= defines.AiRivalryThreshold)
        {
            var relation = CampaignDiplomacy.Get(campaign, nation.Id, worstRival.Id);
            if (relation.Stance is DiplomaticStance.Neutral or DiplomaticStance.Friendly)
            {
                relation.Stance = DiplomaticStance.Rival;
                relation.LastActionDay = campaign.ElapsedDays;
            }
        }
    }

    // ---------------------------------------------------------------- economy ----------

    /// <summary>
    /// A month of governing. Nothing clever - a treasury that is emptying gets taxed, a
    /// country at war spends on its army, an angry country spends on its people, and
    /// whatever is left over is built with. It is the difference between a country that
    /// develops and one that is a fixed set of numbers with an army standing on it.
    /// </summary>
    private static void Economy(Campaign campaign, Nation nation)
    {
        var defines = campaign.Defines;
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        var reserves = nation.Gdp <= 0f ? 0f : nation.Treasury / nation.Gdp;

        // Tax. Broke countries tax more; comfortable ones with an unhappy population tax
        // less, because the alternative to a rebate is a riot.
        var taxTarget = nation.TaxRate;
        if (reserves < defines.AiTreasuryFloor) taxTarget += defines.AiTaxStep;
        else if (reserves > defines.AiTreasuryComfort && nation.Stability < 0.6f) taxTarget -= defines.AiTaxStep;
        nation.TaxRate = M.Clamp(taxTarget, defines.AiMinimumTaxRate, defines.AiMaximumTaxRate);

        // The army costs what the situation is worth.
        var militaryTarget = atWar
            ? defines.AiWartimeMilitarySpending
            : Threatened(campaign, nation) ? defines.AiTenseMilitarySpending : defines.AiPeacetimeMilitarySpending;
        nation.MilitarySpending = M.Lerp(nation.MilitarySpending, militaryTarget, 0.35f);

        // An unhappy country buys quiet, when it can afford it.
        if (nation.Stability < defines.AiStabilityConcern && reserves > defines.AiTreasuryFloor)
        {
            nation.WelfareSpending = M.Clamp(nation.WelfareSpending + defines.AiSocialStep, 0f, 0.20f);
            nation.EducationSpending = M.Clamp(nation.EducationSpending + defines.AiSocialStep * 0.6f, 0f, 0.15f);
        }
        else if (reserves < defines.AiTreasuryFloor * 0.5f)
        {
            nation.WelfareSpending = M.Clamp(nation.WelfareSpending - defines.AiSocialStep, 0.01f, 0.20f);
        }

        Build(campaign, nation, atWar, reserves);
    }

    /// <summary>
    /// One project a month, in the region that most needs it. What a country builds says
    /// what it thinks is wrong with itself: an army that cannot be fed builds roads, a
    /// country at war builds guns, and a country at peace builds the thing that makes it
    /// richer next year.
    /// </summary>
    private static void Build(Campaign campaign, Nation nation, bool atWar, float reserves)
    {
        var defines = campaign.Defines;
        if (reserves < defines.AiBuildTreasuryFloor) return;
        if (campaign.Construction.Count(project => project.NationId == nation.Id) >= defines.AiMaxConcurrentProjects)
            return;

        var states = campaign.StatesOf(nation);
        if (states.Length == 0) return;

        var congested = campaign.Armies.Any(army => army.NationId == nation.Id && army.SupplyCongestion < 0.75f);
        var wanted =
            congested ? BuildingKind.Infrastructure :
            atWar ? BuildingKind.ArmsIndustry :
            nation.Literacy < 0.72f ? BuildingKind.University :
            nation.StandardOfLiving < 0.55f ? BuildingKind.Agriculture :
            BuildingKind.CivilianIndustry;

        // Put it where there is most to gain: the biggest region that has fewest of these.
        ScenarioState target = null;
        var bestScore = float.MinValue;
        foreach (var state in states)
        {
            var already = state.Buildings.GetValueOrDefault(wanted);
            var score = state.Population / 1_000_000f - already * defines.AiBuildSaturation;
            if (score <= bestScore) continue;
            bestScore = score;
            target = state;
        }
        if (target is null) return;

        new QueueConstructionCommand
        {
            IssuerId = nation.Id,
            StateMapIndex = target.MapIndex,
            Building = wanted
        }.Apply(campaign);
    }

    // ------------------------------------------------------------- war and peace -------

    /// <summary>
    /// Whether to start a war, and whether to end one. Both questions are asked of the
    /// same country on the same day, because a country that is already fighting should not
    /// be shopping for a second front.
    /// </summary>
    private static void Warmaking(Campaign campaign, Nation nation)
    {
        var ownWar = campaign.Wars.Find(war => !war.Ended && war.Involves(nation.Id));
        if (ownWar is not null) { Negotiate(campaign, nation, ownWar); return; }
        if (!campaign.Defines.AiMayDeclareWar) return;

        var defines = campaign.Defines;
        if (nation.WarSupport < defines.AiWarSupportFloor) return;
        if (nation.Stability < defines.AiWarStabilityFloor) return;

        Nation target = null;
        var bestDesire = defines.AiWarDesireThreshold;
        foreach (var mapIndex in NeighboursOf(campaign, nation.MapIndex))
        {
            var other = ByMapIndex(mapIndex);
            if (other is null || other.Id == nation.Id) continue;
            Count("considered");
            if (other.IsHuman && !defines.AiMayAttackPlayer) continue;
            if (!CampaignDiplomacy.CanDeclareWar(campaign, nation.Id, other.Id, out _)) { Count("blocked"); continue; }

            var desire = WarDesire(campaign, nation, other);
            if (desire > 0f) Count("wanted");
            if (desire <= bestDesire) continue;
            bestDesire = desire;
            target = other;
        }
        if (target is null) return;
        Count("targeted");

        // Even a country that wants a war does not start one on a particular Tuesday.
        // The die keeps the map from lurching every time a balance of power tips.
        //
        // It is drawn from the country and the date rather than from the campaign's shared
        // stream on purpose. Sharing it meant that adding or removing one AI decision
        // reshuffled every later roll in the world - so a change to how countries build
        // roads moved the Russo-Ukrainian front by twenty provinces, and no measurement of
        // one system could be trusted after a change to any other.
        var roll = Roll(campaign.Seed, nation.Id, campaign.ElapsedDays);
        if (roll > defines.AiWarChance * bestDesire) return;

        if (CampaignDiplomacy.DeclareWar(campaign, nation.Id, target.Id) is not null) Count("declared");
    }

    /// <summary>
    /// How much one country wants another's ground. Weakness invites, hostility motivates,
    /// a neighbour already fighting somebody else is an opportunity, and a country that is
    /// itself unstable has other problems.
    /// </summary>
    private static float WarDesire(Campaign campaign, Nation nation, Nation other)
    {
        var defines = campaign.Defines;
        var relation = CampaignDiplomacy.Get(campaign, nation.Id, other.Id);
        if (relation.Stance == DiplomaticStance.Allied) return 0f;

        var mine = Strength(campaign, nation);
        var theirs = Strength(campaign, other);
        if (mine <= 0.01f) return 0f;

        // Below parity there is no appetite at all; the curve only starts once a country
        // is meaningfully stronger than what it is looking at.
        var advantage = mine / M.Max(0.01f, theirs);
        if (advantage < defines.AiWarMinimumAdvantage) return 0f;

        var desire = M.Clamp(advantage - defines.AiWarMinimumAdvantage, 0f, 4f);
        desire += M.Clamp(-relation.Opinion / 100f, 0f, 1f) * defines.AiHostilityAppetite;
        if (relation.Stance == DiplomaticStance.Rival) desire += defines.AiRivalryAppetite;
        if (campaign.Wars.Any(war => !war.Ended && war.Involves(other.Id)))
            desire += defines.AiOpportunismAppetite;
        desire *= M.Clamp(nation.WarSupport, 0.1f, 1f);
        desire *= M.Clamp(nation.Stability + 0.35f, 0.2f, 1.4f);
        return desire;
    }

    /// <summary>
    /// A country at war asks, periodically, whether it is done. Winning it writes a peace
    /// out of the ground it actually holds - through the same conference a player uses -
    /// and losing it offers to stop.
    /// </summary>
    private static void Negotiate(Campaign campaign, Nation nation, StrategicWar war)
    {
        var defines = campaign.Defines;
        if (campaign.ElapsedDays < war.AiNegotiationUnlockDay) return;
        if (campaign.ElapsedDays - war.StartedDay < defines.AiMinimumWarDays) return;
        if (campaign.ElapsedDays - war.LastPeaceProposalDay < defines.AiPeaceProposalInterval) return;

        var opponentId = war.AttackerId == nation.Id ? war.DefenderId : war.AttackerId;
        var opponent = campaign.NationById(opponentId);
        if (opponent is null || opponent.IsHuman) return;

        var ledger = new PeaceLedger(campaign, war, nation.Id);
        if (ledger.Budget < defines.AiPeaceMinimumBudget)
        {
            // Not winning - which is not the same as wanting to stop. A country only walks
            // away from a war it started or is defending when the country itself has had
            // enough, and that takes years rather than a bad season. Otherwise the map
            // settles every border it has within a year of the campaign opening.
            if (nation.WarSupport < defines.AiCapitulationWarSupport &&
                campaign.ElapsedDays - war.StartedDay > defines.AiMinimumWarDays * 3)
                PeaceNegotiation.Resolve(campaign, war, nation.Id, PeaceTerms.WhitePeace);
            return;
        }

        // Winning. Take what is held, cheapest first, up to most of the budget - the same
        // demand a player would write by claiming the ground they are standing on.
        var demand = new PeaceDemand { WarId = war.Id, ProposerId = nation.Id };
        var held = PeaceConference.Claimable(campaign, war, nation.Id)
            .Where(province => campaign.ProvinceController[province] == (ushort)nation.MapIndex)
            .OrderBy(province => ledger.Price(province).Cost)
            .ToList();
        foreach (var province in held)
        {
            demand.Provinces.Add(province);
            if (ledger.Cost(demand) <= ledger.Budget * defines.AiPeaceBudgetShare) continue;
            demand.Provinces.Remove(province);
            break;
        }

        if (demand.Provinces.Count == 0)
        {
            // Ahead on points and holding none of their ground. A country in that position
            // does not stop - it pushes, because stopping converts a war it is winning into
            // nothing at all. Only a genuinely broken opponent is worth an indemnity.
            var opponentSupport = war.AttackerId == nation.Id ? war.DefenderWarSupport : war.AttackerWarSupport;
            if (ledger.Budget >= defines.AiPeaceIndemnityBudget &&
                opponentSupport <= defines.AiCapitulationWarSupport)
                PeaceNegotiation.Resolve(campaign, war, nation.Id, PeaceTerms.WarReparations);
            return;
        }

        PeaceConference.Resolve(campaign, war, demand);
    }

    // ------------------------------------------------------------------ helpers --------

    /// <summary>
    /// What a country is worth in a fight: the divisions it has, weighted by what state
    /// they are in, plus what its economy could raise if the war lasted.
    /// </summary>
    private static float Strength(Campaign campaign, Nation nation) =>
        nation is not null && StrengthCache.TryGetValue(nation.Id, out var value)
            ? value
            : nation?.Gdp * campaign.Defines.AiEconomicWeight ?? 0f;

    /// <summary>A nation by its map index, without walking the whole list to find it.</summary>
    private static Nation ByMapIndex(int mapIndex) =>
        NationByMapIndex.TryGetValue(mapIndex, out var nation) ? nation : null;

    /// <summary>
    /// A deterministic value in [0, 1) for one country on one day. Same campaign, same
    /// answer, every time - and independent of everything else that draws randomness.
    /// </summary>
    private static float Roll(int seed, int nationId, int day)
    {
        var hash = (uint)seed * 2654435761u ^ (uint)nationId * 2246822519u ^ (uint)day * 3266489917u;
        hash ^= hash >> 15;
        hash *= 2246822519u;
        hash ^= hash >> 13;
        hash *= 3266489917u;
        hash ^= hash >> 16;
        return (hash >> 8) * (1f / 16777216f);
    }

    /// <summary>Whether anybody next door is at war, which is reason enough to spend.</summary>
    private static bool Threatened(Campaign campaign, Nation nation)
    {
        foreach (var mapIndex in NeighboursOf(campaign, nation.MapIndex))
        {
            var other = ByMapIndex(mapIndex);
            if (other is null) continue;
            if (campaign.Wars.Any(war => !war.Ended && war.Involves(other.Id))) return true;
            if (CampaignDiplomacy.Get(campaign, nation.Id, other.Id).Stance == DiplomaticStance.Rival) return true;
        }
        return false;
    }
}
