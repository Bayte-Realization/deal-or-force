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
    [ThreadStatic] private static List<(Nation Friend, float Opinion)> _friendlyPairsScratch;
    [ThreadStatic] private static HashSet<int> _touchingRegionsScratch;

    private static List<(Nation Friend, float Opinion)> FriendlyPairs => _friendlyPairsScratch ??= new();
    private static HashSet<int> TouchingRegions => _touchingRegionsScratch ??= new();

    private sealed class GeoCsr
    {
        public ProvinceTopology Map;
        public ulong OwnerHash;
        public int[] Counts = Array.Empty<int>();
        public int[] Offsets = Array.Empty<int>();
        public int[] Cursor = Array.Empty<int>();
        public int[] Provinces = Array.Empty<int>();
        public int MaxOwner = -1;
    }

    private static readonly System.Runtime.CompilerServices.ConditionalWeakTable<Campaign, GeoCsr> GeoCsrTables = new();

    private static ulong HashUShorts(ushort[] values, int count)
    {
        if (values is null || count <= 0) return 0UL;
        var len = Math.Min(count + 1, values.Length);
        var span = System.Runtime.InteropServices.MemoryMarshal.Cast<ushort, ulong>(values.AsSpan(0, len));
        var h = (ulong)len * 14695981039346656037UL;
        for (var i = 0; i < span.Length; i++)
        {
            h ^= span[i] + 0x9E3779B97F4A7C15UL;
            h *= 1099511628211UL;
        }
        var tailStart = len - (len & 3);
        for (var i = tailStart; i < len; i++)
        {
            h ^= values[i];
            h *= 1099511628211UL;
        }
        return h;
    }

    private static GeoCsr EnsureGeography(Campaign campaign)
    {
        var tables = TablesOf(campaign);
        var week = campaign.ElapsedDays / 7;
        var csr = GeoCsrTables.GetOrCreateValue(campaign);
        if (tables.GeographyWeek == week && tables.GeographyStates == campaign.States.Count && csr.MaxOwner >= 0)
            return csr;
        tables.GeographyWeek = week;
        tables.GeographyStates = campaign.States.Count;
        var stateByMap = tables.StateByMapIndex ??= new Dictionary<int, ScenarioState>(campaign.States.Count);
        stateByMap.Clear();
        var states = campaign.States;
        for (var i = 0; i < states.Count; i++)
        {
            var s = states[i];
            stateByMap[s.MapIndex] = s;
        }
        var map = campaign.ProvinceMap;
        if (map is null || campaign.ProvinceController is null)
        {
            csr.MaxOwner = -1;
            return csr;
        }
        var pCount = map.ProvinceCount;
        var ptn = map.ProvinceToNation;
        var ownerHash = HashUShorts(ptn, pCount);
        if (ReferenceEquals(csr.Map, map) && csr.OwnerHash == ownerHash && csr.MaxOwner >= 0)
            return csr;

        var maxOwner = 0;
        var landTotal = 0;
        for (var province = 1; province <= pCount; province++)
        {
            if (map.IsWater(province)) continue;
            landTotal++;
            var owner = ptn[province];
            if (owner > maxOwner) maxOwner = owner;
        }
        var ownerSlots = maxOwner + 1;
        if (csr.Counts.Length < ownerSlots)
        {
            var cap = Math.Max(ownerSlots, 256);
            csr.Counts = new int[cap];
            csr.Offsets = new int[cap + 1];
            csr.Cursor = new int[cap];
        }
        else
        {
            Array.Clear(csr.Counts, 0, ownerSlots);
        }
        for (var province = 1; province <= pCount; province++)
        {
            if (map.IsWater(province)) continue;
            csr.Counts[ptn[province]]++;
        }
        if (csr.Provinces.Length < landTotal) csr.Provinces = new int[landTotal];
        var sum = 0;
        for (var owner = 0; owner < ownerSlots; owner++)
        {
            csr.Offsets[owner] = sum;
            csr.Cursor[owner] = sum;
            sum += csr.Counts[owner];
        }
        csr.Offsets[ownerSlots] = sum;
        for (var province = 1; province <= pCount; province++)
        {
            if (map.IsWater(province)) continue;
            var owner = ptn[province];
            csr.Provinces[csr.Cursor[owner]++] = province;
        }
        csr.Map = map;
        csr.OwnerHash = ownerHash;
        csr.MaxOwner = maxOwner;
        return csr;
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
        var blocScale = M.Clamp(1f - GeopoliticalCycle.BilateralRealignmentBias(campaign, nation, friend) * 0.22f, 0.72f, 1.32f);
        var tradeThreshold = (defines.AiTradeThreshold / reach) * blocScale;
        var pactThreshold = (defines.AiPactThreshold / reach) * blocScale;
        var allianceThreshold = (defines.AiAllianceThreshold / reach) * blocScale;
        if (GeopoliticalCycle.IsDetenteEra(campaign))
        {
            tradeThreshold *= 0.88f;
            pactThreshold *= 0.90f;
            allianceThreshold *= 0.94f;
        }
        else if (GeopoliticalCycle.IsFrictionEra(campaign))
        {
            tradeThreshold *= 1.10f;
            pactThreshold *= 1.10f;
            allianceThreshold *= 1.08f;
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
            DiplomaticOutreach.CanStartQuiet(campaign, nation.Id, friend.Id, missionScan) &&
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
            !InternationalOrganizations.HasDefencePartner(campaign, nation.Id, friend.Id) &&
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
        if (kind == AgreementKind.TradeAgreement)
        {
            // Terms, not a flag: the composer writes the presets that fit the pair and asks for the
            // best one the friend would sign. A player gets the same paper under the same bar.
            var candidates = TradeTermsComposer.CandidatesScratch(campaign, asking, asked);
            if (asked.IsHuman)
            {
                var best = TradeTermsComposer.BestFor(campaign, asking, asked, candidates);
                if (best is null) return false;
                var paper = new ProposeAgreementCommand
                {
                    IssuerId = asking.Id, TargetId = asked.Id, Kind = kind, Trade = best.Terms
                };
                paper.Apply(campaign);
                if (paper.Result != AgreementResult.Offered && paper.Result != AgreementResult.Signed) return false;
                Count("agreement_offered");
                return true;
            }
            if (TradeTermsComposer.Propose(campaign, asking, asked, candidates, out _) != AgreementResult.Signed) return false;
            Count("trade_signed");
            DealTally.Count("trade_agreement.source.friendship");
            return true;
        }
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
        var willing = AgreementSystem.Willingness(campaign, asked, asking, kind);
        var eraWilling = AiTemper.EraWilling(campaign, asked, asking, willing);
        var effectiveWilling = M.Lerp(willing, eraWilling, 0.65f);
        if (effectiveWilling < AgreementSystem.Needed(campaign, kind))
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
        var csr = EnsureGeography(campaign);
        var geoTables = TablesOf(campaign);
        var map = campaign.ProvinceMap;
        // Instance state, reused: this runs for every country with a kindred border, and a
        // fresh HashSet per call was pure garbage for an answer that is consumed immediately.
        TouchingRegions.Clear();
        // Only our own ground, and only its edges. The old walk of every province in the world
        // is gone; what is left is the same question asked of the same provinces.
        if (buyer.MapIndex < 0 || buyer.MapIndex > csr.MaxOwner) return false;
        var ownedStart = csr.Offsets[buyer.MapIndex];
        var ownedEnd = csr.Offsets[buyer.MapIndex + 1];
        if (ownedStart >= ownedEnd) return false;
        var ownedProvinces = csr.Provinces;
        for (var oi = ownedStart; oi < ownedEnd; oi++)
        {
            var province = ownedProvinces[oi];
            var neighbors = map.Neighbors[province];
            for (var ni = 0; ni < neighbors.Length; ni++)
            {
                var next = neighbors[ni];
                if (next <= 0 || next > map.ProvinceCount || map.IsWater(next)) continue;
                if (map.ProvinceToNation[next] == (ushort)buyer.MapIndex) continue;
                TouchingRegions.Add(map.ProvinceToState[next]);
            }
        }
        if (TouchingRegions.Count == 0) return false;

        ScenarioState best = null;
        Nation seller = null;
        var bestScore = float.NegativeInfinity;
        var hasCooldowns = buyer.DecisionAvailableDay.Count > 0;
        foreach (var region in TouchingRegions)
        {
            if (geoTables.StateByMapIndex is null || !geoTables.StateByMapIndex.TryGetValue(region, out var state)) continue;
            if (state is null || state.Population <= 0 || state.NationMapIndex == buyer.MapIndex) continue;
            // The day's own index rather than a linear search of every country in the world for
            // every region on the border.
            var owner = ByMapIndex(campaign, state.NationMapIndex);
            if (owner is null || owner.Id == buyer.Id || owner.Movement is not null) continue;
            if (state.MapIndex == owner.CapitalStateMapIndex) continue;
            if (hasCooldowns &&
                buyer.DecisionAvailableDay.GetValueOrDefault(KindredKey(owner.Id)) > campaign.ElapsedDays) continue;
            if (CampaignDiplomacy.AtWar(campaign, buyer.Id, owner.Id)) continue;
            if (!owner.IsHuman && Ultimatum.ProtectsHomelandSale(campaign, owner, state)) continue;
            long ourPeople = 0;
            for (var gi = 0; gi < state.PopulationGroups.Count; gi++)
            {
                var group = state.PopulationGroups[gi];
                if (DemographyCatalog.IsPrimary(buyer.Tag, group.Culture)) ourPeople += group.Size;
            }
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
        buyer.DecisionAvailableDay[KindredKey(seller.Id)] = campaign.ElapsedDays + Math.Max(30, campaign.Defines.AiKindredRetryDays);
        var outcome = DealNegotiations.Submit(campaign, buyer, seller, deal);
        if (!outcome.Accepted)
        {
            var pendingOffer = false;
            var offers = campaign.DealOffers;
            for (var i = 0; i < offers.Count; i++)
            {
                var offer = offers[i];
                if (offer.Pending && offer.FromId == buyer.Id && offer.ToId == seller.Id)
                {
                    pendingOffer = true;
                    break;
                }
            }
            if (!pendingOffer) return false;
        }
        Count("kindred_land");
        return true;
    }

    private static readonly string[] KindredKeys = BuildPrefixedIdKeys("kindred/", 384);

    private static string[] BuildPrefixedIdKeys(string prefix, int count)
    {
        var keys = new string[count];
        for (var i = 0; i < count; i++) keys[i] = prefix + i;
        return keys;
    }

    private static string KindredKey(int ownerId) =>
        (uint)ownerId < (uint)KindredKeys.Length ? KindredKeys[ownerId] : "kindred/" + ownerId;

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

        if (!EconomicExposure.TryPickHedgeCandidate(campaign, nation, line, shortAt,
                nation.DecisionAvailableDay, campaign.ElapsedDays, SupplyHedgeKeys.AsSpan(), out var chosen))
            return false;

        var blockedOffers = CollectPendingDealPartners(campaign, nation.Id);
        var dominant = chosen.TopSupplierId;
        Nation alternate = chosen.SecondSupplierId >= 0 ? campaign.NationById(chosen.SecondSupplierId) : null;
        if (!CanHedgeWith(campaign, nation, alternate, dominant, blockedOffers))
        {
            alternate = null;
            var tables = TablesOf(campaign);
            var sorted = tables.LiveNationsSorted;
            if (tables.LiveNationsDay != campaign.ElapsedDays || tables.LiveNationsCount != campaign.ActiveNations.Count)
            {
                sorted.Clear();
                var nations = campaign.ActiveNations;
                for (var i = 0; i < nations.Count; i++)
                {
                    var candidate = nations[i];
                    if (candidate.Population > 0 && candidate.Movement is null) sorted.Add(candidate);
                }
                sorted.Sort(static (a, b) => a.Id.CompareTo(b.Id));
                tables.LiveNationsDay = campaign.ElapsedDays;
                tables.LiveNationsCount = campaign.ActiveNations.Count;
            }
            if (tables.InStatecraftTick)
            {
                var exporters = tables.ExportersByGood;
                var numGoods = SupplyHedgeKeys.Length;
                if (exporters is null || exporters.Length < numGoods)
                {
                    exporters = new List<(Nation Nation, float Spare)>[numGoods];
                    for (var g = 0; g < numGoods; g++) exporters[g] = new List<(Nation, float)>();
                    tables.ExportersByGood = exporters;
                }
                if (tables.ExportersDay != campaign.ElapsedDays || tables.ExportersNationCount != campaign.ActiveNations.Count)
                {
                    for (var g = 0; g < exporters.Length; g++) exporters[g].Clear();
                    for (var i = 0; i < sorted.Count; i++)
                    {
                        var supplier = sorted[i];
                        foreach (var kv in supplier.Market)
                        {
                            var gIdx = (int)kv.Key;
                            if ((uint)gIdx >= (uint)exporters.Length) continue;
                            var q = kv.Value;
                            var spare = q.Production - q.Demand;
                            if (spare > 0.0001f) exporters[gIdx].Add((supplier, spare));
                        }
                    }
                    for (var g = 0; g < exporters.Length; g++)
                        if (exporters[g].Count > 1)
                            exporters[g].Sort(static (a, b) =>
                            {
                                var cmp = b.Spare.CompareTo(a.Spare);
                                return cmp != 0 ? cmp : a.Nation.Id.CompareTo(b.Nation.Id);
                            });
                    tables.ExportersDay = campaign.ElapsedDays;
                    tables.ExportersNationCount = campaign.ActiveNations.Count;
                }
                var goodIdx = (int)chosen.Good;
                if ((uint)goodIdx < (uint)exporters.Length)
                {
                    var byGood = exporters[goodIdx];
                    for (var i = 0; i < byGood.Count; i++)
                    {
                        var other = byGood[i].Nation;
                        if (!CanHedgeWith(campaign, nation, other, dominant, blockedOffers)) continue;
                        alternate = other;
                        break;
                    }
                }
            }
            else
            {
                var bestSpare = 0f;
                for (var i = 0; i < sorted.Count; i++)
                {
                    var other = sorted[i];
                    if (other.Id == nation.Id || other.Id == dominant) continue;
                    if (other.Population <= 0 || other.Movement is not null) continue;
                    if (blockedOffers is not null && blockedOffers.Contains(other.Id)) continue;
                    if (!other.Market.TryGetValue(chosen.Good, out var quote)) continue;
                    var spare = quote.Production - quote.Demand;
                    if (spare <= 0.0001f) continue;
                    if (alternate is not null && (spare < bestSpare || spare == bestSpare && other.Id >= alternate.Id)) continue;
                    if (!CanHedgeWith(campaign, nation, other, dominant, blockedOffers)) continue;
                    alternate = other;
                    bestSpare = spare;
                }
            }
        }
        if (alternate is null) return false;

        var key = SupplyHedgeKey(chosen.Good);
        // Check the quiet proposal gate BEFORE constructing candidate TradeTerms presets for AI targets.
        if (!alternate.IsHuman && !AgreementSystem.CanProposeQuiet(campaign, nation, alternate, AgreementKind.TradeAgreement))
            return false;
        // The terms are about the scarce good: that good for one of theirs, a guarantee of it with
        // our market opened on it, or a guarantee for half our tariff on everything - with the
        // largest guarantee they would give. A player's country gets the same paper under the same bar.
        var candidates = TradeTermsComposer.CandidatesScratch(campaign, nation, alternate, HedgePresets, chosen.Good, HedgeShares);
        if (alternate.IsHuman)
        {
            var best = TradeTermsComposer.BestFor(campaign, nation, alternate, candidates);
            if (best is null)
            {
                nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiSupplyHedgeRetryDays / 2);
                return false;
            }
            var paper = new ProposeAgreementCommand
            {
                IssuerId = nation.Id, TargetId = alternate.Id, Kind = AgreementKind.TradeAgreement, Trade = best.Terms
            };
            paper.Apply(campaign);
            if (paper.Result is not (AgreementResult.Offered or AgreementResult.Signed or AgreementResult.Refused)) return false;
        }
        else
        {
            if (TradeTermsComposer.Propose(campaign, nation, alternate, candidates, out _) != AgreementResult.Signed)
            {
                // Nobody would sign anything about this good with us: ask around again later, not tomorrow.
                nation.DecisionAvailableDay[key] = campaign.ElapsedDays + Math.Max(30, defines.AiSupplyHedgeRetryDays / 2);
                return false;
            }
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
        // Whether they would sign is the composer's question now (TradeTermsComposer): terms about a
        // good they can spare may carry an agreement a bare flag could not, so the friendliness bar is
        // one line of their count rather than a gate in front of it. Here only whether one may ask.
        // The relation read above is the one the question is about: looking it up again made three
        // lookups of one fact for every country with a surplus.
        return AgreementSystem.CanProposeQuietWith(campaign, relation, AgreementKind.TradeAgreement) &&
               (!other.IsHuman || AgreementSystem.LapseWait(campaign, relation, buyer.Id) <= 0);
    }

    private static readonly string[] HedgePresets = { "goods_for_goods", "supply_guarantee", "guarantee_for_access" };
    private static readonly float[] HedgeShares = { 0.4f, 0.3f, 0.2f, 0.1f };
}
