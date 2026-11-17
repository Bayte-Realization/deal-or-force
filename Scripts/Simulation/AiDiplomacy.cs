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
    [ThreadStatic] private static List<Nation> _friendlyCandidatesScratch;
    [ThreadStatic] private static Dictionary<int, float> _friendlyOpinionScratch;
    [ThreadStatic] private static HashSet<int> _touchingRegionsScratch;

    private static List<Nation> FriendlyCandidates => _friendlyCandidatesScratch ??= new();
    private static Dictionary<int, float> FriendlyOpinion => _friendlyOpinionScratch ??= new();
    private static HashSet<int> TouchingRegions => _touchingRegionsScratch ??= new();

    private static void EnsureGeography(Campaign campaign)
    {
        var tables = TablesOf(campaign);
        var week = campaign.ElapsedDays / 7;
        if (tables.GeographyWeek == week && tables.GeographyStates == campaign.States.Count) return;
        tables.GeographyWeek = week;
        tables.GeographyStates = campaign.States.Count;
        var stateByMap = tables.StateByMapIndex ??= new Dictionary<int, ScenarioState>(campaign.States.Count);
        stateByMap.Clear();
        foreach (var state in campaign.States) stateByMap[state.MapIndex] = state;
        var owned = tables.OwnedProvinces ??= new Dictionary<int, int[]>();
        owned.Clear();
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
        foreach (var (owner, list) in buckets) owned[owner] = list.ToArray();
    }

    /// <summary>Use the same bilateral rules as a player, spending at most one diplomatic move.</summary>
    internal static bool TryFriendlyMove(Campaign campaign, Nation nation, Nation friend,
        DiplomaticOutreach.OfferScan missionScan = null, AllianceSystem.OfferScan allianceScan = null)
    {
        if (campaign is null || nation is null || friend is null || nation.Id == friend.Id) return false;
        // The weekly list already kept only countries we have a tie with. Reading it must not
        // wake or insert another. A move that writes gets its own relation at that command.
        var relation = CampaignDiplomacy.Peek(campaign, nation.Id, friend.Id);
        if (relation is null || relation.Stance == DiplomaticStance.AtWar) return false;
        var opinion = relation.Opinion;
        var defines = campaign.Defines;
        // Ask only for a useful bridge to a live opponent, never collect transit treaties
        // around the world. Human neighbours still decide through their inbox.
        using (SimProfiler.Measure("      dip:transit"))
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

        using (SimProfiler.Measure("      dip:missions"))
        if (campaign.Defines.DiplomaticMissionsEnabled && nation.PoliticalPoints >= 18f && nation.Treasury >= 0.2f &&
            opinion < allianceThreshold && DiplomaticOutreach.Of(relation, nation.Id) is null &&
            DiplomaticOutreach.Refusal(campaign, nation.Id, friend.Id, true, missionScan).Length == 0 &&
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

        using (SimProfiler.Measure("      dip:agreements"))
        {
            if (opinion >= tradeThreshold &&
                TryAgreement(campaign, nation, friend, AgreementKind.TradeAgreement)) return true;
            if (opinion >= pactThreshold &&
                TryAgreement(campaign, nation, friend, AgreementKind.NonAggressionPact)) return true;
        }

        using (SimProfiler.Measure("      dip:alliances"))
        if (relation.Stance != DiplomaticStance.Allied && opinion >= allianceThreshold &&
            AllianceSystem.CanProposeQuiet(campaign, nation, friend, preview: true) &&
            !HasId(InternationalOrganizations.DefencePartners(campaign, nation.Id), friend.Id) &&
            AllianceSystem.Willingness(campaign, nation, friend, null, false, allianceScan) > defines.AllianceWillingness &&
            (friend.IsHuman || AllianceSystem.Willingness(campaign, friend, nation, null, false, allianceScan) > defines.AllianceWillingness))
        {
            // Ask only when the commitment benefits us and does not duplicate a defence
            // charter. An AI recipient uses the same acceptance score as a player offer;
            // a human recipient keeps their own decision, even when that score is low.
            AllianceSystem.Propose(campaign, nation, friend, out _);
            Count("alliance_offered");
            return true;
        }

        using (SimProfiler.Measure("      dip:aid"))
            if (TryAid(campaign, nation, friend, relation)) return true;
        // Optional visits and exchanges must not pre-empt the ally's existing
        // reserve-aware aid decision (or spend the operating cash it protects).
        using (SimProfiler.Measure("      dip:acts"))
        if (opinion >= 15f && relation.Stance != DiplomaticStance.Allied &&
            DiplomaticActs.TryAi(campaign, nation, friend)) return true;
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
            relation = CampaignDiplomacy.Get(campaign, nation.Id, friend.Id);
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
        if (!AgreementSystem.CanProposeQuiet(campaign, asking, asked, kind)) return false;
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
            // A player's standing rule may sign it on the spot; that is the move spent well.
            if (request.Result != AgreementResult.Offered && request.Result != AgreementResult.Signed) return false;
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
        DealTally.Count(kind == AgreementKind.TradeAgreement ? "trade_agreement.source.friendship" : "pact.source.friendship");
        return true;
    }

    private static bool TryAid(Campaign campaign, Nation giver, Nation recipient, DiplomaticRelation relation)
    {
        var defines = campaign.Defines;
        if (relation.Stance != DiplomaticStance.Allied &&
            relation.Opinion < defines.AiAidFriendshipThreshold) return false;
        var fiscal = AiFiscalPolicy.Assess(campaign, giver, defines.AiAidReserveOfGdp);
        if (!fiscal.CanSpend)
        {
            Count(fiscal.Reason switch
            {
                AiFiscalReason.Ready => "aid_fiscal_Ready",
                AiFiscalReason.InvalidAccounts => "aid_fiscal_InvalidAccounts",
                AiFiscalReason.OverdueBills => "aid_fiscal_OverdueBills",
                AiFiscalReason.DebtWorkout => "aid_fiscal_DebtWorkout",
                AiFiscalReason.ShortRunway => "aid_fiscal_ShortRunway",
                _ => "aid_fiscal_" + fiscal.Reason
            });
            return false;
        }
        var crisis = recipient.Treasury < recipient.Gdp * defines.AiAidCrisisTreasuryOfGdp;
        if (!crisis)
            foreach (var war in Warfare.LiveWars(campaign))
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
        if (buyer.Treasury * M.Clamp01(campaign.Defines.AiKindredTreasuryShare) < 0.01f) return false;
        if (DemographyCatalog.PrimarySet(buyer.Tag) is not { Count: > 0 } &&
            (!campaign.Homelands.TryGetValue(buyer.Id, out var homes) || homes.Count == 0)) return false;
        EnsureDailyTables(campaign);
        EnsureGeography(campaign);
        var geoTables = TablesOf(campaign);
        var map = campaign.ProvinceMap;
        // Instance state, reused: this runs for every country with a kindred border, and a
        // fresh HashSet per call was pure garbage for an answer that is consumed immediately.
        TouchingRegions.Clear();
        // Only our own ground, and only its edges. The old walk of every province in the world
        // is gone; what is left is the same question asked of the same provinces.
        if (geoTables.OwnedProvinces is null || !geoTables.OwnedProvinces.TryGetValue(buyer.MapIndex, out var ours)) return false;
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
            if (geoTables.StateByMapIndex is null || !geoTables.StateByMapIndex.TryGetValue(region, out var state)) continue;
            if (state is null || state.Population <= 0 || state.NationMapIndex == buyer.MapIndex) continue;
            // The day's own index rather than a linear search of every country in the world for
            // every region on the border.
            var owner = ByMapIndex(campaign, state.NationMapIndex);
            if (owner is null || owner.Id == buyer.Id || owner.Movement is not null) continue;
            if (state.MapIndex == owner.CapitalStateMapIndex) continue;
            if (CampaignDiplomacy.AtWar(campaign, buyer.Id, owner.Id)) continue;
            if (!owner.IsHuman && Ultimatum.ProtectsHomelandSale(campaign, owner, state)) continue;
            long ourPeople = 0;
            foreach (var group in state.PopulationGroups)
                if (DemographyCatalog.IsPrimary(buyer.Tag, group.Culture)) ourPeople += group.Size;
            var share = ourPeople / (float)state.Population;
            var recovery = campaign.Defines.AiPrioritisesLostHomeland &&
                TerritorialClaims.TitleOf(campaign, buyer, state) == TerritorialTitle.Homeland;
            if (!recovery && share < campaign.Defines.AiKindredShare) continue;
            var score = share + (recovery ? 2f : 0f);
            if (score < bestScore || score == bestScore && best is not null && state.MapIndex > best.MapIndex) continue;
            if (buyer.DecisionAvailableDay.Count > 0 &&
                buyer.DecisionAvailableDay.GetValueOrDefault("kindred/" + owner.Id) > campaign.ElapsedDays) continue;
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

    private static readonly string[] SupplyHedgeKeys = BuildSupplyHedgeKeys();
    private static string[] BuildSupplyHedgeKeys()
    {
        var values = (Commodity[])Enum.GetValues(typeof(Commodity));
        var max = 0;
        for (var i = 0; i < values.Length; i++)
            if ((int)values[i] > max) max = (int)values[i];
        var keys = new string[max + 1];
        for (var i = 0; i < values.Length; i++)
        {
            var v = (int)values[i];
            if (v >= 0) keys[v] = "supply_hedge/" + v;
        }
        return keys;
    }

    private static readonly Func<Commodity, string> SupplyHedgeKeyLookup = SupplyHedgeKey;

    private static bool HasId(IReadOnlyList<int> ids, int targetId)
    {
        for (var i = 0; i < ids.Count; i++)
            if (ids[i] == targetId) return true;
        return false;
    }

    private static string SupplyHedgeKey(Commodity good)
    {
        var idx = (int)good;
        return (uint)idx < (uint)SupplyHedgeKeys.Length ? SupplyHedgeKeys[idx] ?? ("supply_hedge/" + idx) : "supply_hedge/" + idx;
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

        // Quick pre-check: if no imported good is scarce or heavily imported, skip building the full route exposure table!
        var anyCandidateGood = false;
        foreach (var quote in nation.Market.Values)
        {
            if (!float.IsFinite(quote.Demand) || quote.Demand <= .001f ||
                !float.IsFinite(quote.Imports) || quote.Imports <= .001f) continue;
            if (nation.DecisionAvailableDay.GetValueOrDefault(SupplyHedgeKey(quote.Commodity)) > campaign.ElapsedDays)
                continue;
            if (quote.Satisfaction < shortAt || quote.Imports / quote.Demand >= 0.5f)
            {
                anyCandidateGood = true;
                break;
            }
        }
        if (!anyCandidateGood) return false;

        if (!EconomicExposure.TryPickHedgeCandidate(campaign, nation, line, shortAt,
                nation.DecisionAvailableDay, campaign.ElapsedDays, SupplyHedgeKeyLookup, out var chosen))
            return false;

        var blockedOffers = CollectPendingDealPartners(campaign, nation.Id);
        var dominant = chosen.TopSupplierId;
        Nation alternate = chosen.SecondSupplierId >= 0 ? campaign.NationById(chosen.SecondSupplierId) : null;
        if (!CanHedgeWith(campaign, nation, alternate, dominant, blockedOffers))
        {
            alternate = null;
            // One pass on this thread, lowest id wins. This scan used to be spread over the
            // workers once a week for every country: 494 fan-outs in fourteen late days, and on
            // a machine with anything else to do each cost 0.33 ms against 0.26 ms for the same
            // scan left on one thread - the body is a few dictionary reads, and waking eight
            // threads for it cost more than it saved. The serial walk also stops asking once a
            // lower id has already qualified.
            var nations = campaign.Nations;
            var count = nations.Count;
            for (var i = 0; i < count; i++)
            {
                var other = nations[i];
                if (other.Id == nation.Id || other.Id == dominant) continue;
                if (other.Population <= 0 || other.Movement is not null) continue;
                if (alternate is not null && other.Id >= alternate.Id) continue;
                if (blockedOffers is not null && blockedOffers.Contains(other.Id)) continue;
                if (!other.Market.TryGetValue(chosen.Good, out var quote)) continue;
                if (quote.Production - quote.Demand <= 0.0001f) continue;
                if (!CanHedgeWith(campaign, nation, other, dominant, blockedOffers)) continue;
                alternate = other;
            }
        }
        if (alternate is null) return false;

        var key = SupplyHedgeKey(chosen.Good);
        if (alternate.IsHuman)
        {
            if (!campaign.FeatureEnabled("deals") || campaign.ProvinceMap is null) return false;
            var deal = new Ultimatum.Deal(new Ultimatum.Side { TradeAgreement = true }, new Ultimatum.Side());
            var submitted = DealNegotiations.Submit(campaign, nation, alternate, deal);
            var pending = false;
            foreach (var offer in campaign.DealOffers)
                if (offer.Pending && offer.FromId == nation.Id && offer.ToId == alternate.Id &&
                    offer.From.TradeAgreement && offer.From.Cash == 0f && (offer.From.Regions?.Length ?? 0) == 0 &&
                    (offer.To.Regions?.Length ?? 0) == 0)
                    pending = true;
            // Answered at once by the player's standing rule, either way: the question was put,
            // and asking again tomorrow would only fill their ministry's ledger.
            var answeredByRule = !pending && campaign.DealOffers.Any(offer => offer.ByRule &&
                offer.FromId == nation.Id && offer.ToId == alternate.Id && offer.ClosedDay == campaign.ElapsedDays);
            if (!pending && !answeredByRule && !submitted.Accepted) return false;
        }
        else
        {
            if (!AgreementSystem.CanProposeQuiet(campaign, nation, alternate, AgreementKind.TradeAgreement))
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
        DealTally.Count("trade_agreement.source.supply_hedge");
        return true;
    }

    [ThreadStatic]
    private static HashSet<int> _hedgeBlockedPartners;

    private static HashSet<int> CollectPendingDealPartners(Campaign campaign, int buyerId)
    {
        var offers = campaign.DealOffers;
        if (offers.Count == 0) return null;
        HashSet<int> set = null;
        var day = campaign.ElapsedDays;
        for (var i = 0; i < offers.Count; i++)
        {
            var offer = offers[i];
            if (!offer.Pending || offer.ExpiresDay <= day) continue;
            if (offer.FromId == buyerId)
            {
                if (set is null) { set = _hedgeBlockedPartners ??= new HashSet<int>(); set.Clear(); }
                set.Add(offer.ToId);
            }
            else if (offer.ToId == buyerId)
            {
                if (set is null) { set = _hedgeBlockedPartners ??= new HashSet<int>(); set.Clear(); }
                set.Add(offer.FromId);
            }
        }
        return set;
    }

    private static bool CanHedgeWith(Campaign campaign, Nation buyer, Nation other, int dominantId,
        HashSet<int> blockedOffers)
    {
        if (other is null || other.Id == buyer.Id || other.Id == dominantId) return false;
        if (other.Population <= 0 || other.Movement is not null) return false;
        if (blockedOffers is not null && blockedOffers.Contains(other.Id)) return false;
        var relation = CampaignDiplomacy.Peek(campaign, buyer.Id, other.Id);
        if (relation is { Stance: DiplomaticStance.AtWar }) return false;
        if (relation is { EmbargoByA: true } or { EmbargoByB: true }) return false;
        if (relation?.TradeAgreement == true) return false;
        if (other.IsHuman) return campaign.FeatureEnabled("deals") && campaign.ProvinceMap is not null;
        // The relation read above is the one both questions are about. Asking each of them to
        // look the pair up again made three lookups of one fact for every country with a surplus.
        return AgreementSystem.CanProposeQuietWith(campaign, relation, AgreementKind.TradeAgreement) &&
            AgreementSystem.WillingnessWith(campaign, other, buyer, AgreementKind.TradeAgreement, relation) >=
            AgreementSystem.Needed(campaign, AgreementKind.TradeAgreement);
    }
}
