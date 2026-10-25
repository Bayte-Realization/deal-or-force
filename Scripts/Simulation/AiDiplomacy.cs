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

    /// <summary>
    /// A government that buys a scarce good mostly from one partner asks someone else
    /// for an ordinary trade agreement. Off: that week is spent on courtship instead.
    /// </summary>
    public bool AiSupplyHedges { get; set; } = true;

    /// <summary>One partner's share of this good's imports, above which a second supplier is worth asking.</summary>
    public float AiSupplyHedgeShare { get; set; } = 0.75f;

    /// <summary>Below this satisfaction the shortage itself is reason enough to ask, even when the good is not half imported.</summary>
    public float AiSupplyHedgeSatisfaction { get; set; } = 0.875f;

    /// <summary>Days before the same good is hedged again.</summary>
    public int AiSupplyHedgeRetryDays { get; set; } = 180;
}

public static partial class AiStatecraft
{
    private static readonly List<Nation> FriendlyCandidates = new();
    private static readonly HashSet<int> TouchingRegions = new();

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
    private static int _geographyWeek = int.MinValue;
    private static int _geographyStates = -1;

    private static void EnsureGeography(Campaign campaign)
    {
        // Keyed on the campaign, the week and how many regions the world has. Ownership can
        // move without the region count changing - a region sold from one country to another
        // leaves the count alone - and a week's staleness only ever costs this optional
        // purchase one week's delay. The old day key rebuilt a 35,000 province dictionary
        // and its owner arrays every morning even when nothing had moved; the same table
        // answers all week, and every later gate asks fresh who owns the region.
        var week = campaign.ElapsedDays / 7;
        if (ReferenceEquals(_geographyCampaign, campaign) && _geographyWeek == week &&
            _geographyStates == campaign.States.Count) return;
        _geographyCampaign = campaign;
        _geographyWeek = week;
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
        // Ask only for a useful bridge to a live opponent, never collect transit treaties
        // around the world. Human neighbours still decide through their inbox.
        if (opinion >= 0f && MilitaryAccess.TryWarTransit(campaign, nation, friend)) return true;
        // How readily this country reaches out at all. A sociable state offers trade, a pact
        // and an alliance at a lower opinion than a hermit does, and the profile says which it
        // is; the thresholds stay the world's, this only bends them per country.
        var reach = M.Clamp(AiStrategyCatalog.Of(campaign, nation).Sociability, 0.25f, 2.5f);
        var tradeThreshold = defines.AiTradeThreshold / reach;
        var pactThreshold = defines.AiPactThreshold / reach;
        var allianceThreshold = defines.AiAllianceThreshold / reach;
        if (GeopoliticalCycle.IsDetenteEra(campaign))
        {
            tradeThreshold *= 0.88f;
            pactThreshold *= 0.90f;
        }
        else if (GeopoliticalCycle.IsFrictionEra(campaign))
        {
            tradeThreshold *= 1.10f;
            pactThreshold *= 1.10f;
        }

        if (relation.EmbargoBy(nation.Id) && opinion >= defines.AiEmbargoReconcileOpinion)
        {
            new SetEmbargoCommand { IssuerId = nation.Id, TargetId = friend.Id, Embargo = false }.Apply(campaign);
            Count("embargo_lifted");
            return true;
        }

        if (relation.Stance == DiplomaticStance.Rival && opinion >= 35f &&
            campaign.ElapsedDays - relation.LastActionDay >= 14 && nation.Treasury >= 0.1f)
        {
            var reconcile = new ImproveRelationsCommand { IssuerId = nation.Id, TargetId = friend.Id };
            reconcile.Apply(campaign);
            if (relation.Stance == DiplomaticStance.Friendly)
            {
                Count("rivalry_reconciled");
                return true;
            }
            // With diplomatic missions the overture is a paid assignment and the rivalry ends at
            // its first weekly payment (DiplomaticOutreach.Advance), not today. Sending it is
            // still this call's one move: the caller must not go on to court somebody else.
            if (reconcile.Changed)
            {
                Count("rivalry_mission_dispatched");
                return true;
            }
        }

        if (campaign.Defines.DiplomaticMissionsEnabled && nation.PoliticalPoints >= 18f && nation.Treasury >= 0.2f &&
            opinion < allianceThreshold && DiplomaticOutreach.Of(relation, nation.Id) is null &&
            DiplomaticOutreach.Refusal(campaign, nation.Id, friend.Id).Length == 0 &&
            (nation.TradeDependenceOn(friend.Id) > 0.02f || CampaignDiplomacy.SharesLandBorder(campaign, nation.MapIndex, friend.MapIndex) || opinion >= 10f))
        {
            var command = new ImproveRelationsCommand { IssuerId = nation.Id, TargetId = friend.Id, Active = true };
            command.Apply(campaign);
            if (command.Changed)
            {
                Count("diplomatic_mission_dispatched");
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
        // Warming a tie into friendship is a real move with a real price, and the price is the
        // player's: the same overture ImproveRelationsCommand charges for - money out of the
        // treasury, a fortnight's silence, and an opinion high enough to carry it. Setting the
        // stance here for nothing let the computer buy, with air, what a player has to buy with
        // a mission. With missions enabled there is no immediate route for either side at all:
        // the mission is the route, and it pays out on its own schedule
        // (DiplomaticOutreach.Advance), which the computer already uses above.
        if (!defines.DiplomaticMissionsEnabled && relation.Stance == DiplomaticStance.Neutral &&
            opinion >= 35f && campaign.ElapsedDays - relation.LastActionDay >= 14 && nation.Treasury >= 0.1f)
        {
            nation.Treasury -= 0.1f;
            relation.LastActionDay = campaign.ElapsedDays;
            relation.Stance = DiplomaticStance.Friendly;
            campaign.MarkStrategicChange();
            Count("stance_warmed");
            return true;
        }
        return false;
    }

    private static bool TryAgreement(Campaign campaign, Nation asking, Nation asked, AgreementKind kind)
    {
        if (!AgreementSystem.CanPropose(campaign, asking, asked, kind, out _)) return false;
        // A player answers for themselves: the offer goes on their table and waits, and the
        // computer spends its move on it exactly as it does on an offer to another computer. It
        // used to be that the computer never asked a player for anything short of an alliance,
        // so the whole of ordinary diplomacy ran one way.
        if (asked.IsHuman)
        {
            var request = new ProposeAgreementCommand
            {
                IssuerId = asking.Id, TargetId = asked.Id, Kind = kind
            };
            request.Apply(campaign);
            if (request.Result != AgreementResult.Offered) return false;
            Count("agreement_offered");
            return true;
        }
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
        if (DemographyCatalog.PrimarySet(buyer.Tag) is not { Count: > 0 } &&
            (!campaign.Homelands.TryGetValue(buyer.Id, out var homes) || homes.Count == 0)) return false;
        EnsureDailyTables(campaign);
        EnsureGeography(campaign);
        var map = campaign.ProvinceMap;
        // Instance state, reused: this runs for every country with a kindred border, and a
        // fresh HashSet per call was pure garbage for an answer that is consumed immediately.
        TouchingRegions.Clear();
        // Only our own ground, and only its edges. The old walk of every province in the world
        // is gone; what is left is the same question asked of the same provinces.
        if (!_ownedProvinces.TryGetValue(buyer.MapIndex, out var ours)) return false;
        foreach (var province in ours)
        {
            foreach (var next in map.Neighbors[province])
            {
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                if (map.ProvinceToNation[next] == (ushort)buyer.MapIndex) continue;
                TouchingRegions.Add(map.ProvinceToState[next]);
            }
        }
        if (TouchingRegions.Count == 0) return false;

        ScenarioState best = null;
        Nation seller = null;
        var bestScore = float.NegativeInfinity;
        foreach (var region in TouchingRegions)
        {
            if (!_stateByMapIndex.TryGetValue(region, out var state)) continue;
            if (state is null || state.Population <= 0 || state.NationMapIndex == buyer.MapIndex) continue;
            // The day's own index rather than a linear search of every country in the world for
            // every region on the border.
            var owner = ByMapIndex(state.NationMapIndex);
            if (owner is null || owner.Id == buyer.Id || owner.Movement is not null) continue;
            if (state.MapIndex == owner.CapitalStateMapIndex) continue;
            if (CampaignDiplomacy.AtWar(campaign, buyer.Id, owner.Id)) continue;
            if (!owner.IsHuman && Ultimatum.ProtectsHomelandSale(campaign, owner, state)) continue;
            var key = "kindred/" + owner.Id;
            if (buyer.DecisionAvailableDay.GetValueOrDefault(key) > campaign.ElapsedDays) continue;
            long ourPeople = 0;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(buyer.Tag, group.Culture)) ourPeople += group.Size;
            var share = ourPeople / (float)state.Population;
            var recovery = campaign.Defines.AiPrioritisesLostHomeland &&
                TerritorialClaims.TitleOf(campaign, buyer, state) == TerritorialTitle.Homeland;
            if (!recovery && share < campaign.Defines.AiKindredShare) continue;
            var score = share + (recovery ? 2f : 0f);
            if (score < bestScore || score == bestScore && best is not null && state.MapIndex > best.MapIndex) continue;
            bestScore = score;
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

    /// <summary>
    /// Ask one other country for a trade agreement when a scarce good is mostly one partner's,
    /// or is arriving with no partner's name on it. The agreement is the ordinary one.
    /// An AI partner answers by the same willingness bar as any other trade agreement.
    /// A player gets the paper and nothing is signed until they do. No land is on it.
    /// </summary>
    internal static bool TryHedgeSupply(Campaign campaign, Nation nation)
    {
        var defines = campaign?.Defines;
        if (defines is null || nation is null || nation.IsHuman || nation.Movement is not null) return false;
        if (!defines.AiSupplyHedges) return false;
        if (nation.Population <= 0) return false;

        var line = EconomicExposure.DominantShare(defines);
        var shortAt = defines.AiSupplyHedgeSatisfaction;
        if (!float.IsFinite(shortAt)) shortAt = 0.875f;
        shortAt = Math.Clamp(shortAt, 0.05f, 0.99f);

        EconomicExposure.Row chosen = null;
        foreach (var row in EconomicExposure.Read(campaign, nation))
        {
            if (nation.DecisionAvailableDay.GetValueOrDefault("supply_hedge/" + (int)row.Good) > campaign.ElapsedDays)
                continue;
            var shortOf = float.IsFinite(row.Satisfaction) && row.Satisfaction < shortAt;
            var concentrated = row.Suppliers.Length > 0 && row.LargestImportShare + 0.0001f >= line;
            var unnamed = row.Suppliers.Length == 0 && row.Imports > 0.001f;
            if (concentrated && (shortOf || row.ImportShare >= 0.5f) || unnamed && shortOf)
            {
                if (chosen is null || row.LargestImportShare > chosen.LargestImportShare ||
                    row.LargestImportShare == chosen.LargestImportShare && (int)row.Good < (int)chosen.Good)
                    chosen = row;
            }
        }
        if (chosen is null) return false;

        var dominant = chosen.Suppliers.Length > 0 ? chosen.Suppliers[0].NationId : -1;
        Nation alternate = null;
        if (chosen.Suppliers.Length > 1)
            alternate = campaign.NationById(chosen.Suppliers[1].NationId);
        if (!CanHedgeWith(campaign, nation, alternate, dominant))
        {
            alternate = null;
            foreach (var other in campaign.Nations)
            {
                if (other.Id == nation.Id || other.Id == dominant) continue;
                if (alternate is not null && other.Id >= alternate.Id) continue;
                if (!other.Market.TryGetValue(chosen.Good, out var quote)) continue;
                if (quote.Production - quote.Demand <= 0.0001f) continue;
                if (!CanHedgeWith(campaign, nation, other, dominant)) continue;
                alternate = other;
            }
        }
        if (alternate is null) return false;

        var key = "supply_hedge/" + (int)chosen.Good;
        if (alternate.IsHuman)
        {
            if (!campaign.FeatureEnabled("deals") || campaign.ProvinceMap is null) return false;
            var deal = new Ultimatum.Deal(new Ultimatum.Side { TradeAgreement = true }, new Ultimatum.Side());
            DealNegotiations.Submit(campaign, nation, alternate, deal);
            var pending = false;
            foreach (var offer in campaign.DealOffers)
                if (offer.Pending && offer.FromId == nation.Id && offer.ToId == alternate.Id &&
                    offer.From.TradeAgreement && offer.From.Cash == 0f && (offer.From.Regions?.Length ?? 0) == 0 &&
                    (offer.To.Regions?.Length ?? 0) == 0)
                    pending = true;
            if (!pending) return false;
        }
        else
        {
            if (!AgreementSystem.CanPropose(campaign, nation, alternate, AgreementKind.TradeAgreement, out _))
                return false;
            var offer = new ProposeAgreementCommand
            {
                IssuerId = nation.Id, TargetId = alternate.Id, Kind = AgreementKind.TradeAgreement
            };
            offer.Apply(campaign);
            if (offer.Result != AgreementResult.Signed) return false;
        }

        nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiSupplyHedgeRetryDays);
        Count("supply_hedge");
        return true;
    }

    private static bool CanHedgeWith(Campaign campaign, Nation buyer, Nation other, int dominantId)
    {
        if (other is null || other.Id == buyer.Id || other.Id == dominantId) return false;
        if (other.Population <= 0 || other.Movement is not null) return false;
        var relation = CampaignDiplomacy.Peek(campaign, buyer.Id, other.Id);
        if (relation is { Stance: DiplomaticStance.AtWar }) return false;
        if (relation is { EmbargoByA: true } or { EmbargoByB: true }) return false;
        if (relation?.TradeAgreement == true) return false;
        foreach (var offer in campaign.DealOffers)
            if (offer.Pending && offer.ExpiresDay > campaign.ElapsedDays &&
                (offer.FromId == buyer.Id && offer.ToId == other.Id || offer.FromId == other.Id && offer.ToId == buyer.Id))
                return false;
        if (other.IsHuman) return campaign.FeatureEnabled("deals") && campaign.ProvinceMap is not null;
        return AgreementSystem.CanPropose(campaign, buyer, other, AgreementKind.TradeAgreement, out _, preview: true) &&
            AgreementSystem.Willingness(campaign, other, buyer, AgreementKind.TradeAgreement, preview: true) >=
            AgreementSystem.Needed(campaign, AgreementKind.TradeAgreement);
    }
}
