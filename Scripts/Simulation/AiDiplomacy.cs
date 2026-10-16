using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Minimum opinion for discretionary aid to a country without an alliance.</summary>
    public float AiAidFriendshipThreshold { get; set; } = 50f;
    /// <summary>Reserve an AI country keeps before sending discretionary aid, as a share of its GDP.</summary>
    public float AiAidReserveOfGdp { get; set; } = 0.06f;
    /// <summary>A friend's treasury below this share of GDP counts as an aid-worthy crisis.</summary>
    public float AiAidCrisisTreasuryOfGdp { get; set; } = 0.015f;
    /// <summary>Opinion at which an AI country lifts its own embargo.</summary>
    public float AiEmbargoReconcileOpinion { get; set; } = 0f;
    /// <summary>A neighbour's region is bought in peace only when at least this share of its people are the buyer's own.</summary>
    public float AiKindredShare { get; set; } = 0.55f;
    /// <summary>The most of its treasury a country will spend to buy that region.</summary>
    public float AiKindredTreasuryShare { get; set; } = 0.25f;
    /// <summary>Days before the same neighbour is asked again for kindred land.</summary>
    public int AiKindredRetryDays { get; set; } = 540;
}

public static partial class AiStatecraft
{
    private static readonly List<Nation> FriendlyCandidates = new();

    /// <summary>
    /// Which land provinces each country owns, and which region a map index names - built once
    /// a day rather than rediscovered on every question.
    ///
    /// The kindred-land check asks "does any region that touches us belong to somebody else".
    /// It used to answer by walking all thirty-five thousand provinces and their ninety-two
    /// thousand edges, for every country, every week, and then searching the region list
    /// linearly once per bordering region. Measured, that was 784 ms of a 5.5-second thirty-day
    /// run - a seventh of the whole simulation, spent on a question that only ever concerns the
    /// asking country's own border. The answer is the same; only the walking is gone.
    ///
    /// Keyed on the day and on how many regions the world has, because ownership changes when a
    /// border does, and a border that moves almost always changes the region list with it. A
    /// stale entry can only ever cost the check one week's delay, and every later gate in it -
    /// who owns the region, whether we are at war with them, whether it is their capital - is
    /// asked fresh.
    /// </summary>
    private static Dictionary<int, int[]> _ownedProvinces;
    private static Dictionary<int, ScenarioState> _stateByMapIndex;
    private static Campaign _geographyCampaign;
    private static int _geographyDay = int.MinValue;
    private static int _geographyStates = -1;

    private static void EnsureGeography(Campaign campaign)
    {
        // Keyed on the campaign, the day and how many regions the world has. The day is in the
        // key because a border can move without the region list changing - a region sold from
        // one country to another leaves the count alone - and ownership is what this table is.
        // One rebuild a day costs about half a millisecond and keeps it honest.
        if (ReferenceEquals(_geographyCampaign, campaign) && _geographyDay == campaign.ElapsedDays &&
            _geographyStates == campaign.States.Count) return;
        _geographyCampaign = campaign;
        _geographyDay = campaign.ElapsedDays;
        _geographyStates = campaign.States.Count;
        _stateByMapIndex = new Dictionary<int, ScenarioState>(campaign.States.Count);
        foreach (var state in campaign.States) _stateByMapIndex[state.MapIndex] = state;
        _ownedProvinces = new Dictionary<int, int[]>();
        var map = campaign.ProvinceMap;
        if (map is null || campaign.ProvinceController is null) return;
        var buckets = new Dictionary<int, List<int>>();
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (map.IsWater(province)) continue;
            var owner = map.ProvinceToNation[province];
            if (!buckets.TryGetValue(owner, out var list)) buckets[owner] = list = new List<int>();
            list.Add(province);
        }
        foreach (var (owner, list) in buckets) _ownedProvinces[owner] = list.ToArray();
    }

    /// <summary>Use the same bilateral rules as a player, spending at most one diplomatic move.</summary>
    internal static bool TryFriendlyMove(Campaign campaign, Nation nation, Nation friend)
    {
        if (campaign is null || nation is null || friend is null || nation.Id == friend.Id) return false;
        var relation = CampaignDiplomacy.Get(campaign, nation.Id, friend.Id);
        if (relation.Stance == DiplomaticStance.AtWar) return false;
        var opinion = relation.Opinion;
        var defines = campaign.Defines;
        // How readily this country reaches out at all. A sociable state offers trade, a pact
        // and an alliance at a lower opinion than a hermit does, and the profile says which it
        // is; the thresholds stay the world's, this only bends them per country.
        var reach = M.Clamp(AiStrategyCatalog.Of(campaign, nation).Sociability, 0.25f, 2.5f);
        var tradeThreshold = defines.AiTradeThreshold / reach;
        var pactThreshold = defines.AiPactThreshold / reach;
        var allianceThreshold = defines.AiAllianceThreshold / reach;

        if (relation.EmbargoBy(nation.Id) && opinion >= defines.AiEmbargoReconcileOpinion)
        {
            new SetEmbargoCommand { IssuerId = nation.Id, TargetId = friend.Id, Embargo = false }.Apply(campaign);
            Count("embargo_lifted");
            return true;
        }

        if (relation.Stance == DiplomaticStance.Rival && opinion >= 35f &&
            campaign.ElapsedDays - relation.LastActionDay >= 14 && nation.Treasury >= 0.1f)
        {
            new ImproveRelationsCommand { IssuerId = nation.Id, TargetId = friend.Id }.Apply(campaign);
            if (relation.Stance == DiplomaticStance.Friendly)
            {
                Count("rivalry_reconciled");
                return true;
            }
        }

        if (opinion >= tradeThreshold &&
            TryAgreement(campaign, nation, friend, AgreementKind.TradeAgreement)) return true;
        if (opinion >= pactThreshold &&
            TryAgreement(campaign, nation, friend, AgreementKind.NonAggressionPact)) return true;

        if (relation.Stance != DiplomaticStance.Allied && opinion >= allianceThreshold &&
            AllianceSystem.CanPropose(campaign, nation, friend, out _))
        {
            // Human recipients get a real offer; an AI recipient may refuse it.
            AllianceSystem.Propose(campaign, nation, friend, out _);
            Count("alliance_offered");
            return true;
        }

        if (TryAid(campaign, nation, friend, relation)) return true;
        if (relation.Stance == DiplomaticStance.Neutral)
        {
            relation.Stance = DiplomaticStance.Friendly;
            campaign.MarkStrategicChange();
            return true;
        }
        return false;
    }

    private static bool TryAgreement(Campaign campaign, Nation asking, Nation asked, AgreementKind kind)
    {
        // An AI has no authority to sign for a player. The command also guards this, but
        // checking before answering avoids burning a turn on an impossible offer.
        if (!AgreementSystem.CanPropose(campaign, asking, asked, kind, out _)) return false;
        if (AgreementSystem.Willingness(campaign, asked, asking, kind) < AgreementSystem.Needed(campaign, kind))
            return false;
        var offer = new ProposeAgreementCommand
        {
            IssuerId = asking.Id, TargetId = asked.Id, Kind = kind
        };
        offer.Apply(campaign);
        if (offer.Result != AgreementResult.Signed) return false;
        Count(kind == AgreementKind.TradeAgreement ? "trade_signed" : "pact_signed");
        return true;
    }

    private static bool TryAid(Campaign campaign, Nation giver, Nation recipient, DiplomaticRelation relation)
    {
        var defines = campaign.Defines;
        if (relation.Stance != DiplomaticStance.Allied &&
            relation.Opinion < defines.AiAidFriendshipThreshold) return false;
        var fiscal = AiFiscalPolicy.Assess(campaign, giver, defines.AiAidReserveOfGdp);
        if (!fiscal.CanSpend) { Count("aid_fiscal_" + fiscal.Reason); return false; }
        var crisis = recipient.Treasury < recipient.Gdp * defines.AiAidCrisisTreasuryOfGdp;
        if (!crisis)
            foreach (var war in campaign.Wars)
                if (!war.Ended && war.Involves(recipient.Id) && !war.Involves(giver.Id))
                {
                    crisis = true;
                    break;
                }
        if (!crisis) return false;
        var package = AgreementSystem.AidAmount(campaign, giver, recipient, out _);
        // The ordinary aid command sends a full package. Do not invent a different
        // transfer here, or spend the reserve which was checked only before payment.
        if (!float.IsFinite(package) || package <= 0f || package > fiscal.DiscretionaryCash)
        { Count("aid_reserved_cash"); return false; }
        var aid = new SendAidCommand { IssuerId = giver.Id, TargetId = recipient.Id };
        aid.Apply(campaign);
        if (aid.Sent <= 0f) return false;
        Count("aid_sent");
        return true;
    }

    /// <summary>
    /// Buy one neighbouring region where our own people are the majority. Cash on the
    /// deal table, not an ultimatum: a player who owns it gets the paper and decides.
    /// The region has to touch us, so the purchase does not plant a new exclave.
    /// </summary>
    internal static bool TryBuyKindredLand(Campaign campaign, Nation buyer)
    {
        if (campaign?.ProvinceMap is null || buyer is null || buyer.Population <= 0) return false;
        if (!campaign.FeatureEnabled("deals")) return false;
        if (DemographyCatalog.PrimarySet(buyer.Tag) is not { Count: > 0 }) return false;
        EnsureGeography(campaign);
        var map = campaign.ProvinceMap;
        var touching = new HashSet<int>();
        // Only our own ground, and only its edges. The old walk of every province in the world
        // is gone; what is left is the same question asked of the same provinces.
        if (!_ownedProvinces.TryGetValue(buyer.MapIndex, out var ours)) return false;
        foreach (var province in ours)
        {
            foreach (var next in map.Neighbors[province])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                if (map.ProvinceToNation[next] == (ushort)buyer.MapIndex) continue;
                touching.Add(map.ProvinceToState[next]);
            }
        }
        if (touching.Count == 0) return false;

        ScenarioState best = null;
        Nation seller = null;
        var bestShare = campaign.Defines.AiKindredShare;
        foreach (var region in touching)
        {
            if (!_stateByMapIndex.TryGetValue(region, out var state)) continue;
            if (state is null || state.Population <= 0 || state.NationMapIndex == buyer.MapIndex) continue;
            // The day's own index rather than a linear search of every country in the world for
            // every region on the border.
            var owner = ByMapIndex(state.NationMapIndex);
            if (owner is null || owner.Id == buyer.Id || owner.Movement is not null) continue;
            if (state.MapIndex == owner.CapitalStateMapIndex) continue;
            if (CampaignDiplomacy.AtWar(campaign, buyer.Id, owner.Id)) continue;
            var key = "kindred/" + owner.Id;
            if (buyer.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) continue;
            long ourPeople = 0;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(buyer.Tag, group.Culture)) ourPeople += group.Size;
            var share = ourPeople / (float)state.Population;
            if (share < bestShare) continue;
            bestShare = share;
            best = state;
            seller = owner;
        }
        if (best is null || seller is null) return false;
        if (!Ultimatum.CanNegotiateDeal(campaign, buyer, seller, out _)) return false;
        var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = new[] { best.MapIndex } });
        var price = Ultimatum.CashToBalance(campaign, buyer, seller, deal);
        if (!float.IsFinite(price) || price < 0f) return false;
        var paying = MathF.Max(0.01f, price * 1.05f);
        if (paying > buyer.Treasury * M.Clamp01(campaign.Defines.AiKindredTreasuryShare)) return false;
        deal = deal with { From = deal.From with { Cash = paying } };
        buyer.DecisionAvailableDay["kindred/" + seller.Id] = campaign.ElapsedDays + Math.Max(30, campaign.Defines.AiKindredRetryDays);
        var outcome = DealNegotiations.Submit(campaign, buyer, seller, deal);
        if (!outcome.Accepted && !campaign.DealOffers.Any(offer => offer.Pending && offer.FromId == buyer.Id && offer.ToId == seller.Id))
            return false;
        Count("kindred_land");
        return true;
    }
}
