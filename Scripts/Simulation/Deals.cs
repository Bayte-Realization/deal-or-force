using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public int DealMaxRegions { get; set; } = 8;
    public float DealMaxTreasuryShare { get; set; } = 1f;
    public float DealGrudgePenalty { get; set; } = 15f;
    public float DealGrudgeOpinion { get; set; } = -25f;
    public int DealSettlementTruceDays { get; set; } = 180;
    /// <summary>How much more a region of the country's own people, its homeland, or a
    /// region it claims is worth to it than any other region of the same population.</summary>
    public float DealKinRegionWeight { get; set; } = 1.8f;
    public float DealHomelandRegionWeight { get; set; } = 1.5f;
    public float DealClaimRegionWeight { get; set; } = 1.2f;
    public bool DealProtectHomelandSales { get; set; } = true;
    /// <summary>A multiethnic homeland is not spare land merely because its residents
    /// differ from the government's primary culture. Applies to ordinary AI cash sales.</summary>
    public bool DealProtectCivicHomelandSales { get; set; } = true;
    public float DealHomelandSaleCultureShare { get; set; } = .5f;
    /// <summary>
    /// What ridding a country of its whole grievance over the shape of a border (BorderShape, 0..1)
    /// is worth to it in a deal, in diplomatic utility points. One wrapped region is a third of it.
    /// 0 leaves deals blind to the shape of borders, as before.
    /// </summary>
    public float DealBorderReliefWorth { get; set; } = 36f;
    /// <summary>
    /// A pocket - a region wrapped by one neighbour and cut off from the rest of its owner - whose
    /// people are mostly not its owner's own may be sold for money although it was held on the
    /// first day: an exclave of strangers is not the national heartland.
    /// </summary>
    public bool DealForeignPocketSales { get; set; } = true;
}

/// <summary>
/// A bargain between two countries that are not shooting at each other.
///
/// The ultimatum is one voice: a demand with an implied gun. A deal is the same table
/// turned level - both sides put something on it, and neither holds the other's pen. The
/// pieces are the ones the simulation already knows how to move: cash, provinces, the
/// lifting of an embargo, a promise not to attack, a trade agreement written down. What the
/// ultimatum takes by threat, the deal buys by payment - and the machinery behind every
/// piece is the same machinery the ultimatum already trusts, so nothing here can do more
/// than the gun could.
///
/// Validation is atomic, like <see cref="Ultimatum.Validate"/>: a package with one bad
/// piece signs nothing. Payment is one-off at signing, like a settlement's reparations.
/// </summary>
public static partial class Ultimatum
{
    /// <summary>One side's half of the table: what it puts on it.</summary>
    public sealed record Side
    {
        public int[] Regions = Array.Empty<int>();
        public bool ReleaseRegions;
        public float Cash;
        public bool LiftEmbargo;
        public bool NonAggression;
        public bool TradeAgreement;
        /// <summary>CONS-03: grant a sovereign land-transit corridor &amp; customs passage to partner enclaves.</summary>
        public bool EnclaveCorridor;
        /// <summary>DIP-01: share industrial/scientific blueprints and accelerate partner's active research.</summary>
        public bool TechnologyTransfer;
        /// <summary>DIP-01: lease naval and air basing / logistics access rights.</summary>
        public bool BaseAccessLease;
        /// <summary>DIP-01: underwrite a sovereign credit / debt-relief package from this side's treasury.</summary>
        public bool SovereignCredit;
        public bool IsEmpty => (Regions?.Length ?? 0) == 0 && Cash <= 0 && !LiftEmbargo && !NonAggression &&
                               !TradeAgreement && !EnclaveCorridor && !TechnologyTransfer &&
                               !BaseAccessLease && !SovereignCredit;
    }

    /// <summary>
    /// The whole table: <see cref="From"/> is what the proposer gives. <see cref="Trade"/> is the
    /// trade agreement, a term of the pair rather than of a side (TradeTerms): written the same
    /// whichever column it was ticked in, and counted by both countries from where each stands.
    /// A side's old TradeAgreement tick is read as the ordinary terms (<see cref="TradeOf"/>).
    /// </summary>
    public sealed record Deal(Side From, Side To)
    {
        public TradeTerms Trade { get; init; }
        /// <summary>DIP-01: mutual chokepoint / strait commercial transit guarantee.</summary>
        public bool ChokepointTransit { get; init; }
        /// <summary>DIP-01: multilateral ceasefire mediation and extended 2-year protected truce.</summary>
        public bool CeasefireMediation { get; init; }
    }

    /// <summary>The trade terms on a table: the shared term, or the old terms for a side's tick, or null.</summary>
    public static TradeTerms TradeOf(Deal deal) =>
        deal is null ? null : deal.Trade ??
        (deal.From?.TradeAgreement == true || deal.To?.TradeAgreement == true ? TradeTerms.Legacy() : null);

    /// <summary>A table whose only term is a trade agreement: what the button on a country page puts.</summary>
    public static bool IsTradeOnly(Deal deal)
    {
        if (TradeOf(deal) is null || deal.ChokepointTransit || deal.CeasefireMediation) return false;
        static bool Bare(Side s) => s is null || ((s.Regions?.Length ?? 0) == 0 && s.Cash <= 0f &&
            !s.LiftEmbargo && !s.NonAggression && !s.EnclaveCorridor && !s.TechnologyTransfer &&
            !s.BaseAccessLease && !s.SovereignCredit);
        return Bare(deal.From) && Bare(deal.To);
    }

    public sealed record DealOutcome(bool Accepted, string Note, int PendingOfferId = 0);

    /// <summary>Utility points, not currency or predicted profit. Cash is paid only at face value.</summary>
    public sealed record DealValuation(float Cash, float Land, float Treaties, float Borders = 0f)
    {
        public float Total => Cash + Land + Treaties + Borders;
    }

    public sealed record DealAssessment(bool Valid, string Reason, bool Acceptable,
        DealValuation FromValuation, DealValuation ToValuation, float GrudgePenalty,
        float FromTreasuryAfter, float ToTreasuryAfter)
    {
        public float FromValue => FromValuation.Total;
        public float ToValue => ToValuation.Total - GrudgePenalty;
        public bool TerritoryProtected { get; init; }
    }

    /// <summary>Peaceful negotiation has no border, fleet, alliance or truce prerequisite.</summary>
    public static bool CanOfferDeal(Campaign c, Nation from, Nation to, out string reason)
        => CanOfferDealCore(c, from, to, out reason, allowHuman: false);

    public static bool CanNegotiateDeal(Campaign c, Nation from, Nation to, out string reason)
        => CanOfferDealCore(c, from, to, out reason, allowHuman: true);

    private static bool CanOfferDealCore(Campaign c, Nation from, Nation to, out string reason, bool allowHuman)
    {
        reason = "";
        if (c?.ProvinceMap is null || from is null || to is null || from.Id == to.Id ||
            !ReferenceEquals(c.NationById(from.Id), from) || !ReferenceEquals(c.NationById(to.Id), to))
        { reason = Loc.Get("Invalid target."); return false; }
        if (!c.FeatureEnabled("deals")) { reason = Loc.Get("Deals are disabled in this campaign."); return false; }
        if (c.Defines.FeatureCommands.TryGetValue(nameof(ProposeDealCommand), out var gate) &&
            !string.IsNullOrWhiteSpace(gate) && !c.FeatureEnabled(gate))
        { reason = Loc.Get("This action is disabled in this campaign."); return false; }
        if (from.Landless || to.Landless || !c.HasOwnedLand(from) || !c.HasOwnedLand(to))
        { reason = Loc.Get("A country without territory cannot sign a territorial deal."); return false; }
        if (CampaignDiplomacy.AtWar(c, from.Id, to.Id))
        { reason = Loc.Get("Use the peace conference while at war."); return false; }
        if (to.IsHuman && !allowHuman) { reason = Loc.Get("They answer for themselves."); return false; }
        return true;
    }

    /// <summary>
    /// How much the addressed country values this table, in diplomatic utility points.
    ///
    /// <paramref name="receive"/> is the package addressed to it - what the counterparty
    /// hands over; <paramref name="give"/> is its own side of the table. Everything received
    /// adds, everything given subtracts, and a promise made by a notorious country is worth
    /// less than the paper it is written on.
    /// </summary>
    public static float DealValue(Campaign c, Nation recipient, Nation counterparty, Side receive, Side give,
        TradeTerms trade = null)
        => ValueDealSide(c, recipient, counterparty, receive, give, trade, false, false).Total;

    /// <summary>Deterministic sovereign credit / debt-relief transfer amount ($B) from giver to receiver.</summary>
    public static float SovereignCreditAmount(Nation giver, Nation receiver)
    {
        if (giver is null || receiver is null) return 0f;
        var need = MathF.Max(0.25f, MathF.Max(0f, receiver.PublicDebt) * 0.12f);
        var cap = MathF.Max(0f, giver.Treasury) * 0.25f;
        var amount = MathF.Min(need, cap);
        return amount >= 0.05f && float.IsFinite(amount) ? MathF.Floor(amount * 100f) / 100f : 0f;
    }

    /// <summary>Single source of truth for why a strategic deal clause cannot be selected, or empty if allowed.</summary>
    public static string ClauseRefusal(Campaign c, Nation giver, Nation receiver, string clauseId)
    {
        if (c?.ProvinceMap is null || giver is null || receiver is null || giver.Id == receiver.Id)
            return Loc.Get("Invalid target.");
        var relation = CampaignDiplomacy.Peek(c, giver.Id, receiver.Id);
        int CooldownLeft(string key) => giver.DecisionAvailableDay.TryGetValue(key, out var d) && d > c.ElapsedDays ? d - c.ElapsedDays : 0;
        switch (clauseId)
        {
            case "enclave_corridor":
                return BorderDiplomacy.CorridorRefusal(c, giver, receiver);
            case "tech_transfer":
            {
                var left = CooldownLeft($"deal_tech/{giver.Id}/{receiver.Id}");
                return left > 0 ? Loc.Format("deal.refuse.clause_cooldown_fmt", left) : "";
            }
            case "base_access":
            {
                var left = CooldownLeft($"deal_base/{giver.Id}/{receiver.Id}");
                if (left > 0) return Loc.Format("deal.refuse.clause_cooldown_fmt", left);
                if (relation?.GrantsMilitaryAccess(giver.Id) == true &&
                    receiver.DecisionAvailableDay.TryGetValue($"deal_base_active/{giver.Id}", out var act) && act > c.ElapsedDays)
                    return Loc.Get("deal.refuse.base_already_leased");
                return "";
            }
            case "sovereign_credit":
            {
                var left = CooldownLeft($"deal_credit/{giver.Id}/{receiver.Id}");
                if (left > 0) return Loc.Format("deal.refuse.clause_cooldown_fmt", left);
                var amount = SovereignCreditAmount(giver, receiver);
                if (amount < 0.05f) return Loc.Get("deal.refuse.credit_no_funds");
                return "";
            }
            case "chokepoint_transit":
            {
                var lo = Math.Min(giver.Id, receiver.Id);
                var hi = Math.Max(giver.Id, receiver.Id);
                var left = Math.Max(CooldownLeft($"deal_chokepoint/{lo}/{hi}"),
                    receiver.DecisionAvailableDay.TryGetValue($"deal_chokepoint/{lo}/{hi}", out var d2) && d2 > c.ElapsedDays ? d2 - c.ElapsedDays : 0);
                return left > 0 ? Loc.Format("deal.refuse.clause_cooldown_fmt", left) : "";
            }
            case "ceasefire_mediation":
            {
                var lo = Math.Min(giver.Id, receiver.Id);
                var hi = Math.Max(giver.Id, receiver.Id);
                var left = Math.Max(CooldownLeft($"deal_mediation/{lo}/{hi}"),
                    receiver.DecisionAvailableDay.TryGetValue($"deal_mediation/{lo}/{hi}", out var d2) && d2 > c.ElapsedDays ? d2 - c.ElapsedDays : 0);
                return left > 0 ? Loc.Format("deal.refuse.clause_cooldown_fmt", left) : "";
            }
            default:
                return "";
        }
    }

    /// <summary>Transparent line-by-line AI diplomatic utility breakdown for the strategic clauses on a deal table.</summary>
    public static IReadOnlyList<(string Label, float Points)> ExplainClauseWeights(
        Campaign c, Nation recipient, Nation counterparty, Deal deal)
    {
        var list = new List<(string Label, float Points)>();
        if (c?.ProvinceMap is null || recipient is null || counterparty is null || deal?.From is null || deal.To is null)
            return list;
        ValueDealSide(c, recipient, counterparty, deal.From, deal.To, TradeOf(deal), deal.ChokepointTransit, deal.CeasefireMediation, list);
        return list;
    }

    private static DealValuation ValueDealSide(Campaign c, Nation recipient, Nation counterparty, Side receive, Side give,
        TradeTerms trade, bool chokepointTransit, bool ceasefireMediation,
        List<(string Label, float Points)> breakdown = null)
    {
        if (c?.ProvinceMap is null || recipient is null || counterparty is null || receive is null || give is null)
            return new(0, 0, 0);
        // Cash is worth its face, scaled by how much of the recipient's treasury it is:
        // a poor country signs for a smaller cheque than a rich one shrugs at.
        // Value the net transfer. Exchanging two identical cheques creates no value.
        var cash = (float)Math.Clamp(((double)receive.Cash - give.Cash) * CashWeight(recipient),
            -float.MaxValue / 2d, float.MaxValue / 2d);
        var land = 0f;
        // Land is priced the way the peace conference prices it: by the people on it.
        var receiveCount = NormalizeSideRegions(receive.Regions, out var receiveBuf, out var receiveSingle);
        if (receiveCount > 0)
        {
            try
            {
                var ids = receiveBuf ?? MemoryMarshal.CreateReadOnlySpan(ref receiveSingle, 1);
                for (var i = 0; i < receiveCount; i++)
                {
                    var state = c.StateByMapIndex(ids[i]);
                    if (state is null || state.NationMapIndex != counterparty.MapIndex) continue;
                    // Independence gives the recipient diplomatic room, not ownership or taxes.
                    land += RegionWorth(c, recipient, state, receiving: true) * (receive.ReleaseRegions ? .25f : 1f);
                }
            }
            finally { if (receiveBuf is not null) System.Buffers.ArrayPool<int>.Shared.Return(receiveBuf); }
        }
        var giveCount = NormalizeSideRegions(give.Regions, out var giveBuf, out var giveSingle);
        if (giveCount > 0)
        {
            try
            {
                var ids = giveBuf ?? MemoryMarshal.CreateReadOnlySpan(ref giveSingle, 1);
                for (var i = 0; i < giveCount; i++)
                {
                    var state = c.StateByMapIndex(ids[i]);
                    if (state is null || state.NationMapIndex != recipient.MapIndex) continue;
                    land -= RegionWorth(c, recipient, state, receiving: false);
                }
            }
            finally { if (giveBuf is not null) System.Buffers.ArrayPool<int>.Shared.Return(giveBuf); }
        }
        // Independence changes the donor's shape but gives its counterparty no land.
        // Evaluate the complete swap so a border adjustment is not mistaken for two enclaves.
        var borders = -TerritorialCoherence.Penalty(c, recipient,
            receive.ReleaseRegions ? Array.Empty<int>() : receive.Regions, give.Regions);
        // An ugly border fixed is worth something to both sides of it: the one whose pocket goes
        // as much as the one who gets it (BorderShape). A package that makes a new pocket costs
        // the same in reverse. Released land changes the donor's shape but hands nobody anything.
        if (c.Defines.DealBorderReliefWorth > 0f && float.IsFinite(c.Defines.DealBorderReliefWorth))
            borders += BorderShape.Relief(c, recipient, counterparty,
                receive.ReleaseRegions ? Array.Empty<int>() : receive.Regions,
                give.ReleaseRegions ? Array.Empty<int>() : give.Regions) * MathF.Min(200f, c.Defines.DealBorderReliefWorth);
        // A promise received is worth what its maker's name can carry.
        var trust = 1f - M.Clamp01(counterparty.Notoriety / 100f) * .5f;
        var treaties = 0f;
        var relation = CampaignDiplomacy.Peek(c, recipient.Id, counterparty.Id);
        var isRival = relation?.Stance == DiplomaticStance.Rival;

        if (receive.NonAggression) { var v = 10f * trust; treaties += v; breakdown?.Add((Loc.Get("Non-aggression pact"), v)); }
        if (give.NonAggression) { treaties -= 10f; breakdown?.Add((Loc.Get("Non-aggression pact"), -10f)); }
        if (receive.LiftEmbargo || give.LiftEmbargo)
        {
            var partnerLeverage = CommodityLeverage.Calculate(c, counterparty, recipient);
            var ourLeverage = CommodityLeverage.Calculate(c, recipient, counterparty);
            if (receive.LiftEmbargo) { var v = 5f + partnerLeverage * 10f; treaties += v; breakdown?.Add((Loc.Get("Lift embargo"), v)); }
            if (give.LiftEmbargo) { var v = -(5f + ourLeverage * 8f); treaties += v; breakdown?.Add((Loc.Get("Lift embargo"), v)); }
        }

        // ---- Wave 5F Strategic Clauses (DIP-01 + CONS-03)
        if (receive.EnclaveCorridor)
        {
            var v = BorderDiplomacy.CorridorReliefValue(c, recipient, counterparty) * trust;
            borders += v;
            breakdown?.Add((Loc.Get("deal.clause.enclave_corridor"), v));
        }
        if (give.EnclaveCorridor)
        {
            var v = -(7f + (isRival ? 5f : 0f));
            borders += v;
            breakdown?.Add((Loc.Get("deal.clause.enclave_corridor"), v));
        }
        if (receive.TechnologyTransfer)
        {
            var lead = Math.Max(0, counterparty.Technologies.Count - recipient.Technologies.Count);
            var v = (11f + Math.Min(6, lead) * 1.8f) * trust;
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.tech_transfer"), v));
        }
        if (give.TechnologyTransfer)
        {
            var lead = Math.Max(0, recipient.Technologies.Count - counterparty.Technologies.Count);
            var v = -(10f + Math.Min(6, lead) * 1.5f + (isRival ? 4f : 0f));
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.tech_transfer"), v));
        }
        if (receive.BaseAccessLease)
        {
            var hasForces = recipient.AirExperience > 0f;
            if (!hasForces)
            {
                var fleets = c.Fleets;
                for (var fi = 0; fi < fleets.Count; fi++)
                    if (fleets[fi].NationId == recipient.Id) { hasForces = true; break; }
            }
            var v = (hasForces ? 14f : 10f) * trust;
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.base_access"), v));
        }
        if (give.BaseAccessLease)
        {
            var v = -(9f + (isRival ? 7f : 0f) + (relation is { Opinion: < -15f } ? 3f : 0f));
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.base_access"), v));
        }
        if (receive.SovereignCredit)
        {
            var amt = SovereignCreditAmount(counterparty, recipient);
            var debtBonus = recipient.PublicDebt > 0.5f ? 6f : 2f;
            var v = amt * CashWeight(recipient) * 1.25f + debtBonus;
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.sovereign_credit"), v));
        }
        if (give.SovereignCredit)
        {
            var amt = SovereignCreditAmount(recipient, counterparty);
            var v = -(amt * CashWeight(recipient) + 3f);
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.sovereign_credit"), v));
        }
        if (chokepointTransit)
        {
            var tradeDep = recipient.TradeDependenceOn(counterparty.Id);
            var v = isRival ? -3f : (4f + MathF.Min(10f, tradeDep * 35f)) * trust;
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.chokepoint_transit"), v));
        }
        if (ceasefireMediation)
        {
            var thirdWar = DiplomaticGestures.HasActiveThirdPartyWar(c, recipient, counterparty);
            var weary = 1f - M.Clamp01(recipient.WarSupport);
            var v = isRival && !thirdWar ? -4f : ((thirdWar ? 11f : 5f) + weary * 6f) * trust;
            treaties += v;
            breakdown?.Add((Loc.Get("deal.clause.ceasefire_mediation"), v));
        }

        // A trade agreement is one shared term, counted by this country from its own seat - the
        // same count the button, the computer and the standing answers use (TradeTermsValue). It
        // used to be a gain in one column and a loss in the other for the same signature.
        if (trade is not null)
        {
            var tv = TradeTermsValue.For(c, recipient, counterparty, trade);
            treaties += tv;
            breakdown?.Add((Loc.Get("Trade agreement"), tv));
        }
        return new(cash, land, treaties, borders);
    }

    /// <summary>What a billion of net payment is worth to this country: a poor treasury signs
    /// for a smaller cheque than a rich one shrugs at.</summary>
    public static float CashWeight(Nation recipient) =>
        1f + 2f * (1f - M.Clamp01(recipient.Treasury / MathF.Max(1f, recipient.Treasury + 50f)));

    /// <summary>
    /// What one region is worth to a country, gained or given: its people first, then whose
    /// people they are and whose land the country believes it to be. A region of one's own
    /// nation is not a parcel - Serbia does not value a Serbian district as it values a
    /// Hungarian one - and a homeland title doubles down on it. The same weights price it
    /// for the side that gives it up, so a country sells its heartland dear and a remote,
    /// foreign-peopled province cheap. A title counts only for land coming back: every region
    /// a country held on the first day is remembered as its homeland, so weighing titles on
    /// what it gives would price every exchange of equal regions as a loss.
    /// </summary>
    public static float RegionWorth(Campaign c, Nation nation, ScenarioState state, bool receiving = true)
    {
        var worth = 6f + 12f * M.Clamp(state.Population / 2_000_000f, 0f, 3f);
        var largest = EconomySystem.DominantCulture(state).Culture;
        if (largest is not null && DemographyCatalog.IsPrimary(nation.Tag, largest))
            worth *= c.Defines.DealKinRegionWeight;
        if (!receiving) return worth;
        worth *= TerritorialClaims.TitleOf(c, nation, state) switch
        {
            TerritorialTitle.Homeland => c.Defines.DealHomelandRegionWeight,
            TerritorialTitle.Claim => c.Defines.DealClaimRegionWeight,
            _ => 1f
        };
        return worth;
    }

    /// <summary>
    /// The cheque that would turn a refusal into a signature: the shortfall, in the
    /// recipient's own points, divided by what a billion is worth to it. Zero when the table
    /// already stands; negative when even our whole treasury would not close the gap.
    /// </summary>
    public static float CashToBalance(Campaign c, Nation from, Nation to, Deal deal)
    {
        var assessment = AssessDeal(c, from, to, deal);
        if (!assessment.Valid || assessment.Acceptable) return 0f;
        if (assessment.TerritoryProtected) return -1f;
        var shortfall = assessment.GrudgePenalty - assessment.ToValuation.Total;
        var needed = shortfall / CashWeight(to) + 0.01f;
        // A cheque from them back to us is cancelled first: asking less is paying more.
        var room = MathF.Max(0f, from.Treasury) - deal.From.Cash + deal.To.Cash;
        return needed <= room ? MathF.Ceiling(needed * 100f) / 100f : -1f;
    }

    /// <summary>How much more could be asked of them in cash before the table tips into a
    /// refusal - the haggling margin a minister reads off the other side's face.</summary>
    public static float CashToAskMore(Campaign c, Nation from, Nation to, Deal deal)
    {
        var assessment = AssessDeal(c, from, to, deal);
        if (!assessment.Valid || !assessment.Acceptable) return 0f;
        var surplus = assessment.ToValuation.Total - assessment.GrudgePenalty;
        var more = MathF.Floor(surplus / CashWeight(to) * 100f) / 100f;
        var maxShare = float.IsFinite(c.Defines.DealMaxTreasuryShare) ? Math.Clamp(c.Defines.DealMaxTreasuryShare, 0f, 1f) : 1f;
        var room = MathF.Max(0f, to.Treasury) * maxShare - deal.To.Cash;
        return MathF.Max(0f, MathF.Min(more, room));
    }

    /// <summary>
    /// Whether this table may be laid at all, and if not, why not. One bad piece signs
    /// nothing: the same atomic rule the ultimatum settles by. A term already in force
    /// cannot be promised again - a pact that exists buys nothing, and a deal that counted
    /// on it would be buying land with a shadow.
    /// </summary>
    public static bool ValidateDeal(Campaign c, Nation from, Nation to, Deal deal, out string reason)
        => ValidateDealCore(c, from, to, deal, out reason, allowHuman: false);

    public static bool ValidateNegotiatedDeal(Campaign c, Nation from, Nation to, Deal deal, out string reason)
        => ValidateDealCore(c, from, to, deal, out reason, allowHuman: true);

    private static bool ValidateDealCore(Campaign c, Nation from, Nation to, Deal deal, out string reason, bool allowHuman)
    {
        reason = "";
        if (deal?.From is null || deal.To is null)
        { reason = Loc.Get("Invalid target."); return false; }
        if (!CanOfferDealCore(c, from, to, out reason, allowHuman)) return false;
        if (deal.From.ReleaseRegions && deal.To.ReleaseRegions)
        { reason = Loc.Get("Only one independent settlement may be established per deal."); return false; }
        var trade = TradeOf(deal);
        var hasSharedClause = deal.ChokepointTransit || deal.CeasefireMediation;
        if (deal.From.IsEmpty && deal.To.IsEmpty && trade is null && !hasSharedClause) { reason = Loc.Get("Select at least one demand."); return false; }
        var hasSideClause = deal.From.EnclaveCorridor || deal.To.EnclaveCorridor ||
                            deal.From.TechnologyTransfer || deal.To.TechnologyTransfer ||
                            deal.From.BaseAccessLease || deal.To.BaseAccessLease ||
                            deal.From.SovereignCredit || deal.To.SovereignCredit;
        if (deal.From.Cash == deal.To.Cash && (deal.From.Regions?.Length ?? 0) == 0 &&
            (deal.To.Regions?.Length ?? 0) == 0 && !deal.From.LiftEmbargo && !deal.To.LiftEmbargo &&
            !deal.From.NonAggression && !deal.To.NonAggression && !hasSideClause && trade is null && !hasSharedClause)
        { reason = Loc.Get("The deal must change at least one term."); return false; }
        if (!float.IsFinite(deal.From.Cash) || !float.IsFinite(deal.To.Cash) ||
            deal.From.Cash < 0 || deal.To.Cash < 0)
        { reason = Loc.Get("A payment cannot be negative."); return false; }
        if (!float.IsFinite(from.Treasury) || !float.IsFinite(to.Treasury))
        { reason = Loc.Get("This deal cannot be made."); return false; }
        var fromCredit = deal.From.SovereignCredit ? SovereignCreditAmount(from, to) : 0f;
        var toCredit = deal.To.SovereignCredit ? SovereignCreditAmount(to, from) : 0f;
        if (deal.From.Cash + fromCredit > MathF.Max(0f, from.Treasury) + 0.001f)
        { reason = Loc.Get("The offer exceeds our treasury."); return false; }
        var maxShare = float.IsFinite(c.Defines.DealMaxTreasuryShare)
            ? Math.Clamp(c.Defines.DealMaxTreasuryShare, 0f, 1f) : 1f;
        if (deal.To.Cash + toCredit > MathF.Max(0f, to.Treasury) * maxShare + 0.001f)
        { reason = Loc.Get("They cannot pay that."); return false; }
        var fromAfter = (double)from.Treasury - deal.From.Cash - fromCredit + deal.To.Cash;
        var toAfter = (double)to.Treasury - deal.To.Cash - toCredit + deal.From.Cash;
        if (Math.Abs(fromAfter) > float.MaxValue || Math.Abs(toAfter) > float.MaxValue)
        { reason = Loc.Get("The sums in this deal are too large."); return false; }
        if (!ValidateSide(c, from, deal.From, out reason)) return false;
        if (!ValidateSide(c, to, deal.To, out reason)) return false;
        // Both cannot promise the same pact; that is not a deal but a treaty draft nobody owns.
        // One side writes the pact; the other pays for it. A trade agreement is the pair's own
        // term (TradeTerms) and has no side to be doubled on.
        if (deal.From.NonAggression && deal.To.NonAggression)
        { reason = Loc.Get("Only one side may offer this term."); return false; }
        if (deal.From.SovereignCredit && deal.To.SovereignCredit)
        { reason = Loc.Get("Only one side may offer this term."); return false; }
        var relation = CampaignDiplomacy.Peek(c, from.Id, to.Id);
        if ((deal.From.NonAggression || deal.To.NonAggression) && relation?.NonAggressionPact == true)
        { reason = Loc.Get("Already signed."); return false; }
        if (deal.From.EnclaveCorridor && (reason = ClauseRefusal(c, from, to, "enclave_corridor")).Length > 0) return false;
        if (deal.To.EnclaveCorridor && (reason = ClauseRefusal(c, to, from, "enclave_corridor")).Length > 0) return false;
        if (deal.From.TechnologyTransfer && (reason = ClauseRefusal(c, from, to, "tech_transfer")).Length > 0) return false;
        if (deal.To.TechnologyTransfer && (reason = ClauseRefusal(c, to, from, "tech_transfer")).Length > 0) return false;
        if (deal.From.BaseAccessLease && (reason = ClauseRefusal(c, from, to, "base_access")).Length > 0) return false;
        if (deal.To.BaseAccessLease && (reason = ClauseRefusal(c, to, from, "base_access")).Length > 0) return false;
        if (deal.From.SovereignCredit && (reason = ClauseRefusal(c, from, to, "sovereign_credit")).Length > 0) return false;
        if (deal.To.SovereignCredit && (reason = ClauseRefusal(c, to, from, "sovereign_credit")).Length > 0) return false;
        if (deal.ChokepointTransit && (reason = ClauseRefusal(c, from, to, "chokepoint_transit")).Length > 0) return false;
        if (deal.CeasefireMediation && (reason = ClauseRefusal(c, from, to, "ceasefire_mediation")).Length > 0) return false;
        if (trade is not null)
        {
            if (trade.Renewal ? relation?.TradeAgreement != true : relation?.TradeAgreement == true)
            {
                reason = trade.Renewal ? Loc.Get("There is no trade agreement in force to revise.") : Loc.Get("Already signed.");
                return false;
            }
            var draft = TradeTerms.Copy(trade);
            reason = TradeTerms.Sanitize(draft, Math.Min(from.Id, to.Id), Math.Max(from.Id, to.Id));
            if (reason.Length > 0) return false;
            // The pause a refusal buys holds for the same trade terms put on the table instead of
            // the button: one question, one answer, by either route. Money or another term on the
            // table makes it a different question, which may be put.
            if (!trade.Renewal && IsTradeOnly(deal))
            {
                var since = c.ElapsedDays - (relation?.AgreementRefusedDay ?? -9999);
                if (since < c.Defines.AgreementRefusalDays)
                {
                    reason = Loc.Format("They said no {0} day(s) ago; they will hear it again in {1}.",
                        since, c.Defines.AgreementRefusalDays - since);
                    return false;
                }
            }
        }
        // Lifting an embargo one has not imposed is promising a shadow, and shadows do not
        // buy land.
        if (deal.From.LiftEmbargo && relation?.EmbargoBy(from.Id) != true)
        { reason = Loc.Get("We have not imposed an embargo on them."); return false; }
        if (deal.To.LiftEmbargo && relation?.EmbargoBy(to.Id) != true)
        { reason = Loc.Get("They have not imposed an embargo on us."); return false; }
        return true;
    }

    private static int NormalizeSideRegions(int[] raw, out int[] rented, out int single)
    {
        rented = null;
        single = 0;
        if (raw is null || raw.Length == 0) return 0;
        if (raw.Length == 1)
        {
            single = raw[0];
            return 1;
        }
        var buf = System.Buffers.ArrayPool<int>.Shared.Rent(raw.Length);
        Array.Copy(raw, buf, raw.Length);
        Array.Sort(buf, 0, raw.Length);
        var unique = 0;
        for (var i = 0; i < raw.Length; i++)
        {
            var v = buf[i];
            if (unique == 0 || buf[unique - 1] != v) buf[unique++] = v;
        }
        rented = buf;
        return unique;
    }

    private static bool ContainsNormalizedRegion(ReadOnlySpan<int> ids, int id) =>
        ids.Length == 1 ? ids[0] == id : ids.BinarySearch(id) >= 0;

    private static bool ValidateSide(Campaign c, Nation owner, Side side, out string reason)
    {
        reason = "";
        var count = NormalizeSideRegions(side.Regions, out var rented, out var single);
        // Cash and treaty checks are handled by ValidateDeal. No territorial terms,
        // no reason to scan every province in the world for this side.
        if (count == 0 && !side.ReleaseRegions) return true;
        try
        {
            if (side.ReleaseRegions)
            {
                reason = Dismantlement.Refusal(c, owner, side.Regions);
                if (reason.Length > 0) return false;
            }
            if (count > Math.Clamp(c.Defines.DealMaxRegions, 1, 100))
            { reason = Loc.Get("Too many regions in this demand."); return false; }
            var ids = rented is not null ? rented.AsSpan(0, count) : MemoryMarshal.CreateReadOnlySpan(ref single, 1);
            for (var ii = 0; ii < ids.Length; ii++)
            {
                var id = ids[ii];
                if (c.StateByMapIndex(id) is not { } candidate || candidate.NationMapIndex != owner.MapIndex)
                { reason = Loc.Get("They cannot yield what they do not hold."); return false; }
                // A capital is not for sale; it can only be lost. (Releasing it is Dismantlement's
                // question, which moves the government first.)
                if (id == owner.CapitalStateMapIndex && !side.ReleaseRegions)
                { reason = Loc.Get("A capital is not for sale."); return false; }
            }
            if (count == 0) return true;
            var map = c.ProvinceMap;
            var controllers = c.ProvinceController ?? map.ProvinceToNation;

            for (var ii = 0; ii < ids.Length; ii++)
            {
                var provinces = map.ProvincesOfState(ids[ii]);
                if (provinces.IsEmpty)
                { reason = Loc.Get("A demand must leave them territory and cannot include occupied land."); return false; }
                foreach (var p in provinces)
                {
                    if (map.ProvinceToNation[p] != owner.MapIndex || controllers[p] != owner.MapIndex)
                    { reason = Loc.Get("A demand must leave them territory and cannot include occupied land."); return false; }
                }
            }

            var retained = false;
            foreach (var st in c.StatesOf(owner))
            {
                if (ContainsNormalizedRegion(ids, st.MapIndex)) continue;
                foreach (var p in map.ProvincesOfState(st.MapIndex))
                    if (map.ProvinceToNation[p] == owner.MapIndex) { retained = true; break; }
                if (retained) break;
            }
            // Check actual ground if state records are out of sync with province ownership.
            if (!retained)
            {
                for (var p = 1; p <= map.ProvinceCount; p++)
                {
                    if (!ContainsNormalizedRegion(ids, map.ProvinceToState[p]) && map.ProvinceToNation[p] == owner.MapIndex)
                    { retained = true; break; }
                }
            }
            if (!retained)
            { reason = Loc.Get("A demand must leave them territory and cannot include occupied land."); return false; }
            return true;
        }
        finally
        {
            if (rented is not null) System.Buffers.ArrayPool<int>.Shared.Return(rented);
        }
    }

    /// <summary>
    /// The target's answer, computed exactly as the ultimatum's is: a value threshold the
    /// mod moves through defines, plus a grudge floor. This automatic answer is for
    /// governments only; addressed players choose through the negotiation inbox.
    /// </summary>
    public static bool DealAcceptable(Campaign c, Nation from, Nation to, Deal deal)
        => AssessDeal(c, from, to, deal).Acceptable;

    public static DealAssessment AssessDeal(Campaign c, Nation from, Nation to, Deal deal)
        => AssessDealCore(c, from, to, deal, allowHuman: false);

    public static DealAssessment AssessNegotiatedDeal(Campaign c, Nation from, Nation to, Deal deal)
        => AssessDealCore(c, from, to, deal, allowHuman: true);

    private static DealAssessment AssessDealCore(Campaign c, Nation from, Nation to, Deal deal, bool allowHuman)
    {
        if (!ValidateDealCore(c, from, to, deal, out var reason, allowHuman))
            return new(false, reason, false, new(0, 0, 0), new(0, 0, 0), 0,
                from?.Treasury ?? 0, to?.Treasury ?? 0);
        var trade = TradeOf(deal);
        var theirs = ValueDealSide(c, to, from, deal.From, deal.To, trade, deal.ChokepointTransit, deal.CeasefireMediation);
        var ours = ValueDealSide(c, from, to, deal.To, deal.From, trade, deal.ChokepointTransit, deal.CeasefireMediation);
        var penalty = GrudgePenalty(c, from, to);
        // A bigger cheque cannot substitute for consent to sell a national heartland.
        // Genuine territorial swaps retain the existing valuation; human recipients
        // still choose explicitly. Culture shared by BOTH Koreas is not spare land.
        var protectedLand = false;
        if (!to.IsHuman && (deal.From.Regions?.Length ?? 0) == 0 && deal.To.Regions is { Length: > 0 } toRegions)
        {
            for (var ri = 0; ri < toRegions.Length; ri++)
            {
                if (ProtectsHomelandSale(c, to, c.StateByMapIndex(toRegions[ri])))
                {
                    protectedLand = true;
                    break;
                }
            }
        }
        var acceptable = !protectedLand && theirs.Total - penalty >= 0;
        var fromCredit = deal.From.SovereignCredit ? SovereignCreditAmount(from, to) : 0f;
        var toCredit = deal.To.SovereignCredit ? SovereignCreditAmount(to, from) : 0f;
        var fromCash = (float)((double)from.Treasury - deal.From.Cash - fromCredit + deal.To.Cash + MathF.Max(0f, toCredit - MathF.Max(0f, from.PublicDebt)));
        var toCash = (float)((double)to.Treasury - deal.To.Cash - toCredit + deal.From.Cash + MathF.Max(0f, fromCredit - MathF.Max(0f, to.PublicDebt)));
        if (deal.From.ReleaseRegions) fromCash -= Math.Max(0, fromCash) * Dismantlement.Share(c, from, deal.From.Regions);
        if (deal.To.ReleaseRegions) toCash -= Math.Max(0, toCash) * Dismantlement.Share(c, to, deal.To.Regions);
        return new(true, protectedLand ? Loc.Get("They will not sell their national heartland for money or treaty promises.") :
            acceptable ? "" : DealRefusalNote(penalty - theirs.Total, penalty > 0), acceptable,
            ours, theirs, penalty, fromCash, toCash) { TerritoryProtected = protectedLand };
    }

    /// <summary>The old grievance a table must clear before it signs (the grudge floor), or 0.</summary>
    public static float GrudgePenalty(Campaign c, Nation from, Nation to)
    {
        var relation = CampaignDiplomacy.Peek(c, from.Id, to.Id);
        var grudgeThreshold = float.IsFinite(c.Defines.DealGrudgeOpinion) ? c.Defines.DealGrudgeOpinion : -25f;
        return relation is not null && relation.Opinion <= grudgeThreshold
            ? float.IsFinite(c.Defines.DealGrudgePenalty) ? MathF.Max(0, c.Defines.DealGrudgePenalty) : 15f : 0;
    }

    internal static bool ProtectsHomelandSale(Campaign c, Nation owner, ScenarioState state)
    {
        if (c is null || owner is null || state is null || !c.Defines.DealProtectHomelandSales ||
            state.NationMapIndex != owner.MapIndex || TerritorialClaims.TitleOf(c, owner, state) != TerritorialTitle.Homeland)
            return false;
        if (c.Defines.DealForeignPocketSales && BorderShape.IsCutOffPocket(c, state))
        {
            var largest = EconomySystem.DominantCulture(state).Culture;
            if (largest is null || !DemographyCatalog.IsPrimary(owner.Tag, largest)) return false;
        }
        if (c.Defines.DealProtectCivicHomelandSales) return true;
        long total = 0, primary = 0;
        foreach (var group in state.PopulationGroups)
        {
            var people = Math.Max(0, group.Size); total += people;
            if (DemographyCatalog.IsPrimary(owner.Tag, group.Culture)) primary += people;
        }
        var threshold = float.IsFinite(c.Defines.DealHomelandSaleCultureShare)
            ? Math.Clamp(c.Defines.DealHomelandSaleCultureShare, .01f, 1f) : .5f;
        return total > 0 && primary / (double)total >= threshold;
    }

    /// <summary>
    /// What a refused table says to the one who laid it. The shortfall is what the answer
    /// actually turned on - the value they weighed, after the grudge floor - so a refusal
    /// with the table already leaning their way says that, and does not invent a figure.
    /// A grudge-struck answer names the grudge: no cheque covers an insult.
    /// </summary>
    public static string DealRefusalNote(float shortfall, bool grudged)
    {
        var note = shortfall > 0f
            ? Loc.Format("They refused: the table fell short by about {0} for them. Sweeten it and ask again.",
                Loc.Format("{0} diplomatic points", MathF.Round(shortfall, 1)))
            : Loc.Get("They refused, though the table already favours them. Only time mends a grudge.");
        if (grudged) note += "\n" + Loc.Get("Old grievances weigh on this table.");
        return note;
    }

    /// <summary>
    /// The proposer consents by submitting the package; the AI recipient consents through
    /// the same assessment the preview shows. Human recipients are not auto-consented.
    /// Mutations happen only after every check passes; a refusal moves nothing.
    /// </summary>
    public static DealOutcome ResolveDeal(Campaign c, Nation from, Nation to, Deal deal)
        => ResolveDealCore(c, from, to, deal, explicitConsent: false);

    // Only the negotiation command boundary calls this after checking the recipient,
    // live offer, deadline and exact unchanged package. A preview is not consent.
    internal static DealOutcome ResolveAgreedDeal(Campaign c, Nation from, Nation to, Deal deal)
        => ResolveDealCore(c, from, to, deal, explicitConsent: true);

    private static DealOutcome ResolveDealCore(Campaign c, Nation from, Nation to, Deal deal, bool explicitConsent)
    {
        var assessment = AssessDealCore(c, from, to, deal, explicitConsent);
        if (!assessment.Valid || (!explicitConsent && !DealAnswer.GovernmentAccepts(assessment))) return new(false, assessment.Reason);
        var summary = DealSummary(c, from, to, deal);
        // Capture both halves before any annex can reunify or renumber a split region.
        // Otherwise the first cession can erase the ID of the second promised region.
        int[] Ground(Side side) => (side.Regions?.Length ?? 0) == 0 ? Array.Empty<int>() :
            Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
                .Where(p => side.Regions.Contains(c.ProvinceMap.ProvinceToState[p])).ToArray();
        var fromGround = Ground(deal.From);
        var toGround = Ground(deal.To);
        // Cash moves once, both ways, and is conserved: the settlement's own rule.
        var fromCash = (float)((double)from.Treasury - deal.From.Cash + deal.To.Cash);
        var toCash = (float)((double)to.Treasury - deal.To.Cash + deal.From.Cash);
        from.Treasury = fromCash; to.Treasury = toCash;
        // Release before cessions: an ordinary annexation can reunify sibling regions.
        if (deal.From.ReleaseRegions) Dismantlement.Apply(c, from, deal.From.Regions, voluntary: true);
        if (deal.To.ReleaseRegions) Dismantlement.Apply(c, to, deal.To.Regions, voluntary: true);
        if (fromGround.Length > 0 && !deal.From.ReleaseRegions) StatePartition.Annex(c, fromGround, to, asConquest: false, byConsent: true);
        if (toGround.Length > 0 && !deal.To.ReleaseRegions) StatePartition.Annex(c, toGround, from, asConquest: false, byConsent: true);
        var relation = CampaignDiplomacy.Get(c, from.Id, to.Id);
        if (deal.From.LiftEmbargo)
        {
            if (relation.A == from.Id) relation.EmbargoByA = false; else relation.EmbargoByB = false;
        }
        if (deal.To.LiftEmbargo)
        {
            if (relation.A == to.Id) relation.EmbargoByA = false; else relation.EmbargoByB = false;
        }
        // One side's promise is the pair's pact: the flag is on the relation, not on a side.
        if (deal.From.NonAggression || deal.To.NonAggression) relation.NonAggressionPact = true;
        // ---- Wave 5F Strategic Clauses execution (DIP-01 + CONS-03)
        void ApplyStrategicHalf(Nation giver, Nation receiver, Side side)
        {
            if (side.EnclaveCorridor)
            {
                if (relation.A == giver.Id) relation.MilitaryAccessByA = true; else relation.MilitaryAccessByB = true;
                ModifierSystem.Grant(c, giver, "border_commission", 365);
                ModifierSystem.Grant(c, receiver, "border_commission", 365);
                relation.Tension = MathF.Max(0f, relation.Tension - 12f);
                OpinionSystem.Remember(c, relation, "border_commission");
                giver.DecisionAvailableDay[$"deal_corridor/{giver.Id}/{receiver.Id}"] = c.ElapsedDays + 365;
            }
            if (side.TechnologyTransfer)
            {
                ModifierSystem.Grant(c, receiver, "technology_licence", 365);
                ModifierSystem.Grant(c, giver, "research_cooperation", 365);
                ModifierSystem.Grant(c, receiver, "research_cooperation", 365);
                if (!string.IsNullOrEmpty(receiver.CurrentResearch) && TechnologyCatalog.Of(receiver.CurrentResearch) is { } tech)
                {
                    var cur = receiver.ResearchProgress.GetValueOrDefault(tech.Id);
                    receiver.ResearchProgress[tech.Id] = MathF.Min(tech.Cost, cur + tech.Cost * 0.15f);
                }
                OpinionSystem.Remember(c, relation, "technology_licence");
                giver.DecisionAvailableDay[$"deal_tech/{giver.Id}/{receiver.Id}"] = c.ElapsedDays + 365;
            }
            if (side.BaseAccessLease)
            {
                if (relation.A == giver.Id) relation.MilitaryAccessByA = true; else relation.MilitaryAccessByB = true;
                ModifierSystem.Grant(c, receiver, "airspace_cooperation", 365);
                receiver.NavalExperience += 3f;
                receiver.AirExperience += 3f;
                OpinionSystem.Remember(c, relation, "port_visit");
                giver.DecisionAvailableDay[$"deal_base/{giver.Id}/{receiver.Id}"] = c.ElapsedDays + 365;
                receiver.DecisionAvailableDay[$"deal_base_active/{giver.Id}"] = c.ElapsedDays + 365;
            }
            if (side.SovereignCredit)
            {
                var amt = SovereignCreditAmount(giver, receiver);
                if (amt > 0f && giver.Treasury + 0.001f >= amt)
                {
                    giver.Treasury = MathF.Max(0f, giver.Treasury - amt);
                    MoneyLedger.Book(giver, MoneyFlow.Diplomacy, -amt);
                    var debtPay = MathF.Min(MathF.Max(0f, receiver.PublicDebt), amt);
                    receiver.PublicDebt = MathF.Max(0f, receiver.PublicDebt - debtPay);
                    var remainder = amt - debtPay;
                    if (remainder > 0f)
                    {
                        receiver.Treasury += remainder;
                        MoneyLedger.Book(receiver, MoneyFlow.Diplomacy, remainder);
                    }
                    receiver.CreditConfidence = Math.Clamp(receiver.CreditConfidence + 0.04f, 0f, 1f);
                    receiver.Stability = Math.Clamp(receiver.Stability + 0.025f, 0f, 1f);
                    OpinionSystem.Remember(c, relation, "subsidy_paid");
                    giver.DecisionAvailableDay[$"deal_credit/{giver.Id}/{receiver.Id}"] = c.ElapsedDays + 365;
                }
            }
        }
        ApplyStrategicHalf(from, to, deal.From);
        ApplyStrategicHalf(to, from, deal.To);
        var pairLo = Math.Min(from.Id, to.Id);
        var pairHi = Math.Max(from.Id, to.Id);
        if (deal.ChokepointTransit)
        {
            ModifierSystem.Grant(c, from, "consular_convention", 365);
            ModifierSystem.Grant(c, to, "consular_convention", 365);
            relation.Tension = MathF.Max(0f, relation.Tension - 8f);
            OpinionSystem.Remember(c, relation, "port_visit");
            from.DecisionAvailableDay[$"deal_chokepoint/{pairLo}/{pairHi}"] = c.ElapsedDays + 365;
            to.DecisionAvailableDay[$"deal_chokepoint/{pairLo}/{pairHi}"] = c.ElapsedDays + 365;
        }
        if (deal.CeasefireMediation)
        {
            relation.TruceUntil = Math.Max(relation.TruceUntil, c.ElapsedDays + 730);
            relation.TalksUntilDay = Math.Max(relation.TalksUntilDay, c.ElapsedDays + 180);
            relation.Tension = MathF.Max(0f, relation.Tension - 10f);
            OpinionSystem.Remember(c, relation, "staff_talks");
            from.DecisionAvailableDay[$"deal_mediation/{pairLo}/{pairHi}"] = c.ElapsedDays + 365;
            to.DecisionAvailableDay[$"deal_mediation/{pairLo}/{pairHi}"] = c.ElapsedDays + 365;
        }
        // The trade agreement is signed where every trade agreement is signed, with its terms.
        var trade = TradeOf(deal);
        if (trade is not null)
        {
            var terms = TradeTerms.Copy(trade);
            TradeTerms.Sanitize(terms, relation.A, relation.B);
            AgreementSystem.Sign(c, relation, AgreementKind.TradeAgreement, terms);
        }
        // A settlement buys a truce; a trade agreement alone does not. It was the cheapest way to
        // buy half a year of safety from a neighbour, and that is not what it is for.
        if (!IsTradeOnly(deal))
            relation.TruceUntil = Math.Max(relation.TruceUntil,
                c.ElapsedDays + Math.Clamp(c.Defines.DealSettlementTruceDays, 0, 3650));
        relation.LastActionDay = c.ElapsedDays;
        c.Chronicle.Record(c, "deal_signed", from.Id, to.Id, subject: summary);
        c.MarkStrategicChange();
        return new(true, summary);
    }

    /// <summary>What was agreed, as one line for the chronicle and the ledger. The sides
    /// are named by the template; the summary lists only the terms.</summary>
    public static string DealSummary(Campaign c, Nation from, Nation to, Deal deal)
    {
        var parts = new List<string>();
        void Half(Nation giver, Nation receiver, Side side)
        {
            if (side.Regions is { Length: > 0 })
            {
                var names = new List<string>(side.Regions.Length);
                foreach (var id in RegionIds(new Demand { Regions = side.Regions }))
                {
                    var label = Loc.Get(c.StateByMapIndex(id)?.Name ?? "");
                    if (label.Length > 0) names.Add(label);
                }
                if (names.Count > 0) parts.Add(side.ReleaseRegions
                    ? Loc.Format("{0}: establish an independent country in {1}", giver.DisplayName, string.Join(", ", names))
                    : Loc.Format("{0}: {1}", giver.DisplayName, string.Join(", ", names)));
            }
            if (side.Cash > 0) parts.Add(Loc.Format("{0}: {1}", giver.DisplayName,
                Loc.Format("One-time payment: {0}", Fmt.Money(side.Cash))));
            if (side.NonAggression) parts.Add(Loc.Get("Non-aggression pact"));
            if (side.LiftEmbargo) parts.Add(Loc.Format("{0}: lift embargo against {1}", giver.DisplayName, receiver.DisplayName));
            if (side.EnclaveCorridor) parts.Add(Loc.Format("deal.summary.corridor_fmt", giver.DisplayName, receiver.DisplayName));
            if (side.TechnologyTransfer) parts.Add(Loc.Format("deal.summary.tech_fmt", giver.DisplayName, receiver.DisplayName));
            if (side.BaseAccessLease) parts.Add(Loc.Format("deal.summary.base_fmt", giver.DisplayName, receiver.DisplayName));
            if (side.SovereignCredit) parts.Add(Loc.Format("deal.summary.credit_fmt", giver.DisplayName, Fmt.Money(SovereignCreditAmount(giver, receiver)), receiver.DisplayName));
        }
        Half(from, to, deal.From);
        Half(to, from, deal.To);
        if (deal.ChokepointTransit) parts.Add(Loc.Get("deal.clause.chokepoint_transit"));
        if (deal.CeasefireMediation) parts.Add(Loc.Get("deal.clause.ceasefire_mediation"));
        if (TradeOf(deal) is { } trade) parts.Add(TradeTermsText.Describe(c, trade, from, to));
        return string.Join(" · ", parts);
    }
}

/// <summary>
/// A proposed exchange through the replay/network queue, shaped exactly like the
/// ultimatum's own command: a whole table, validated once inside Apply, with the outcome
/// and the answer kept on the command for whoever issued it.
/// </summary>
public sealed class ProposeDealCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int ToId { get; init; }
    public int ParentOfferId { get; init; }
    // What the proposer puts on the table, and what it asks for in return.
    public int[] FromRegions = Array.Empty<int>();
    public bool FromReleaseRegions;
    public float FromCash;
    public bool FromLiftEmbargo;
    public bool FromNonAggression;
    public bool FromTradeAgreement;
    public bool FromEnclaveCorridor;
    public bool FromTechnologyTransfer;
    public bool FromBaseAccessLease;
    public bool FromSovereignCredit;
    public int[] ToRegions = Array.Empty<int>();
    public bool ToReleaseRegions;
    public float ToCash;
    public bool ToLiftEmbargo;
    public bool ToNonAggression;
    public bool ToTradeAgreement;
    public bool ToEnclaveCorridor;
    public bool ToTechnologyTransfer;
    public bool ToBaseAccessLease;
    public bool ToSovereignCredit;
    /// <summary>The trade agreement on the table, a term of the pair (TradeTerms). The two side
    /// ticks above are the old way of saying "the ordinary terms" and are read as that.</summary>
    public TradeTerms Trade;
    public bool ChokepointTransit;
    public bool CeasefireMediation;

    public Ultimatum.DealOutcome Outcome { get; private set; } = new(false, "No deal was proposed.");

    public void Apply(Campaign campaign)
    {
        Outcome = new(false, Loc.Get("No deal was proposed."));
        if (!MechanicFeatures.Allows(campaign, this))
        { Outcome = new(false, Loc.Get("This action is disabled in this campaign.")); return; }
        var from = campaign?.NationById(IssuerId);
        var to = campaign?.NationById(ToId);
        var deal = new Ultimatum.Deal(
            new Ultimatum.Side { Regions = FromRegions ?? Array.Empty<int>(), Cash = FromCash,
                ReleaseRegions = FromReleaseRegions,
                LiftEmbargo = FromLiftEmbargo, NonAggression = FromNonAggression, TradeAgreement = FromTradeAgreement,
                EnclaveCorridor = FromEnclaveCorridor, TechnologyTransfer = FromTechnologyTransfer,
                BaseAccessLease = FromBaseAccessLease, SovereignCredit = FromSovereignCredit },
            new Ultimatum.Side { Regions = ToRegions ?? Array.Empty<int>(), Cash = ToCash,
                ReleaseRegions = ToReleaseRegions,
                LiftEmbargo = ToLiftEmbargo, NonAggression = ToNonAggression, TradeAgreement = ToTradeAgreement,
                EnclaveCorridor = ToEnclaveCorridor, TechnologyTransfer = ToTechnologyTransfer,
                BaseAccessLease = ToBaseAccessLease, SovereignCredit = ToSovereignCredit })
        { Trade = TradeTerms.Copy(Trade), ChokepointTransit = ChokepointTransit, CeasefireMediation = CeasefireMediation };
        if (to?.IsHuman == true || ParentOfferId != 0)
        {
            Outcome = DealNegotiations.Submit(campaign, from, to, deal, ParentOfferId);
            return;
        }
        var assessment = Ultimatum.AssessDeal(campaign, from, to, deal);
        // Counted for the bench (DealTally), never read by the world.
        var tallyKey = $"deal.{DealTally.KindOf(deal)}.{DealTally.Seat(from)}>{DealTally.Seat(to)}";
        DealTally.Count(tallyKey + ".put");
        if (DealAnswer.Decide(campaign, to, from, deal, assessment).Verdict != DealVerdict.Accept)
        {
            Outcome = new(false, assessment.Reason);
            DealTally.Count(tallyKey + ".declined");
            // The same silence a refused button buys (AgreementSystem): one answer, by either route.
            if (assessment.Valid && Ultimatum.IsTradeOnly(deal) && Ultimatum.TradeOf(deal) is { Renewal: false })
                CampaignDiplomacy.Get(campaign, from.Id, to.Id).AgreementRefusedDay = campaign.ElapsedDays;
            return;
        }
        Outcome = Ultimatum.ResolveDeal(campaign, from, to, deal);
        DealTally.Count(tallyKey + (Outcome.Accepted ? ".signed" : ".invalid"));
    }
}
