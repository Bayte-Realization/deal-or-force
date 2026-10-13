using System;
using System.Collections.Generic;
using System.Linq;

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
        public bool IsEmpty => (Regions?.Length ?? 0) == 0 && Cash <= 0 && !LiftEmbargo && !NonAggression && !TradeAgreement;
    }

    /// <summary>The whole table: <see cref="From"/> is what the proposer gives.</summary>
    public sealed record Deal(Side From, Side To);

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
    public static float DealValue(Campaign c, Nation recipient, Nation counterparty, Side receive, Side give)
        => ValueDealSide(c, recipient, counterparty, receive, give).Total;

    private static DealValuation ValueDealSide(Campaign c, Nation recipient, Nation counterparty, Side receive, Side give)
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
        foreach (var id in RegionIds(new Demand { Regions = receive.Regions }))
        {
            var state = c.States.FirstOrDefault(s => s.MapIndex == id);
            if (state is null || state.NationMapIndex != counterparty.MapIndex) continue;
            // Independence gives the recipient diplomatic room, not ownership or taxes.
            land += RegionWorth(c, recipient, state, receiving: true) * (receive.ReleaseRegions ? .25f : 1f);
        }
        foreach (var id in RegionIds(new Demand { Regions = give.Regions }))
        {
            var state = c.States.FirstOrDefault(s => s.MapIndex == id);
            if (state is null || state.NationMapIndex != recipient.MapIndex) continue;
            land -= RegionWorth(c, recipient, state, receiving: false);
        }
        // Independence changes the donor's shape but gives its counterparty no land.
        // Evaluate the complete swap so a border adjustment is not mistaken for two enclaves.
        var borders = -TerritorialCoherence.Penalty(c, recipient,
            receive.ReleaseRegions ? Array.Empty<int>() : receive.Regions, give.Regions);
        // A promise received is worth what its maker's name can carry.
        var trust = 1f - M.Clamp01(counterparty.Notoriety / 100f) * .5f;
        var treaties = 0f;
        if (receive.NonAggression) treaties += 10f * trust;
        if (receive.TradeAgreement) treaties += 8f * trust;
        if (receive.LiftEmbargo) treaties += 5f;
        if (give.NonAggression) treaties -= 10f;
        if (give.TradeAgreement) treaties -= 8f;
        if (give.LiftEmbargo) treaties -= 5f;
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
        var largest = EconomySystem.Cultures(state).FirstOrDefault().Culture;
        if (largest is not null && DemographyCatalog.PrimaryCultures(nation.Tag)
                .Contains(largest, StringComparer.OrdinalIgnoreCase))
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
        if (deal.From.IsEmpty && deal.To.IsEmpty) { reason = Loc.Get("Select at least one demand."); return false; }
        if (deal.From.Cash == deal.To.Cash && (deal.From.Regions?.Length ?? 0) == 0 &&
            (deal.To.Regions?.Length ?? 0) == 0 && !deal.From.LiftEmbargo && !deal.To.LiftEmbargo &&
            !deal.From.NonAggression && !deal.To.NonAggression && !deal.From.TradeAgreement && !deal.To.TradeAgreement)
        { reason = Loc.Get("The deal must change at least one term."); return false; }
        if (!float.IsFinite(deal.From.Cash) || !float.IsFinite(deal.To.Cash) ||
            deal.From.Cash < 0 || deal.To.Cash < 0)
        { reason = Loc.Get("Payments must be finite and non-negative."); return false; }
        if (!float.IsFinite(from.Treasury) || !float.IsFinite(to.Treasury))
        { reason = Loc.Get("The treasury balance is invalid."); return false; }
        if (deal.From.Cash > MathF.Max(0f, from.Treasury))
        { reason = Loc.Get("The offer exceeds our treasury."); return false; }
        var maxShare = float.IsFinite(c.Defines.DealMaxTreasuryShare)
            ? Math.Clamp(c.Defines.DealMaxTreasuryShare, 0f, 1f) : 1f;
        if (deal.To.Cash > MathF.Max(0f, to.Treasury) * maxShare)
        { reason = Loc.Get("They cannot pay that."); return false; }
        var fromAfter = (double)from.Treasury - deal.From.Cash + deal.To.Cash;
        var toAfter = (double)to.Treasury - deal.To.Cash + deal.From.Cash;
        if (Math.Abs(fromAfter) > float.MaxValue || Math.Abs(toAfter) > float.MaxValue)
        { reason = Loc.Get("The resulting treasury balance is too large."); return false; }
        if (!ValidateSide(c, from, deal.From, out reason)) return false;
        if (!ValidateSide(c, to, deal.To, out reason)) return false;
        // Both cannot promise the same arrangement; that is not a deal but a treaty draft
        // nobody owns. One side writes the pact; the other pays for it.
        if ((deal.From.NonAggression && deal.To.NonAggression) ||
            (deal.From.TradeAgreement && deal.To.TradeAgreement))
        { reason = Loc.Get("Only one side may offer this term."); return false; }
        var relation = CampaignDiplomacy.Peek(c, from.Id, to.Id);
        if ((deal.From.NonAggression || deal.To.NonAggression) && relation?.NonAggressionPact == true)
        { reason = Loc.Get("Already signed."); return false; }
        if ((deal.From.TradeAgreement || deal.To.TradeAgreement) && relation?.TradeAgreement == true)
        { reason = Loc.Get("Already signed."); return false; }
        // Lifting an embargo one has not imposed is promising a shadow, and shadows do not
        // buy land.
        if (deal.From.LiftEmbargo && relation?.EmbargoBy(from.Id) != true)
        { reason = Loc.Get("We have not imposed an embargo on them."); return false; }
        if (deal.To.LiftEmbargo && relation?.EmbargoBy(to.Id) != true)
        { reason = Loc.Get("They have not imposed an embargo on us."); return false; }
        return true;
    }

    private static bool ValidateSide(Campaign c, Nation owner, Side side, out string reason)
    {
        reason = "";
        var ids = RegionIds(new Demand { Regions = side.Regions });
        // Cash and treaty checks are handled by ValidateDeal. No territorial terms,
        // no reason to scan every province in the world for this side.
        if (ids.Length == 0 && !side.ReleaseRegions) return true;
        if (side.ReleaseRegions)
        {
            reason = Dismantlement.Refusal(c, owner, side.Regions);
            if (reason.Length > 0) return false;
        }
        if (ids.Length > Math.Clamp(c.Defines.DealMaxRegions, 1, 100))
        { reason = Loc.Get("Too many regions in this demand."); return false; }
        foreach (var id in ids)
        {
            if (!c.States.Any(s => s.MapIndex == id && s.NationMapIndex == owner.MapIndex))
            { reason = Loc.Get("They cannot yield what they do not hold."); return false; }
            // A capital is not for sale; it can only be lost. (Releasing it is Dismantlement's
            // question, which moves the government first.)
            if (id == owner.CapitalStateMapIndex && !side.ReleaseRegions)
            { reason = Loc.Get("A capital is not for sale."); return false; }
        }
        var selected = ids.ToHashSet();
        var mapped = new HashSet<int>();
        var retained = false;
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
        {
            if (!selected.Contains(c.ProvinceMap.ProvinceToState[p]))
            {
                if (c.ProvinceMap.ProvinceToNation[p] == owner.MapIndex) retained = true;
                continue;
            }
            if (c.ProvinceMap.ProvinceToNation[p] != owner.MapIndex ||
                (c.ProvinceController ?? c.ProvinceMap.ProvinceToNation)[p] != owner.MapIndex)
            { reason = Loc.Get("A demand must leave them territory and cannot include occupied land."); return false; }
            mapped.Add(c.ProvinceMap.ProvinceToState[p]);
        }
        // Check actual ground, not obsolete state records without mapped provinces.
        if (ids.Length > 0 && (!retained || mapped.Count != ids.Length))
        { reason = Loc.Get("A demand must leave them territory and cannot include occupied land."); return false; }
        return true;
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
        var theirs = ValueDealSide(c, to, from, deal.From, deal.To);
        var ours = ValueDealSide(c, from, to, deal.To, deal.From);
        var relation = CampaignDiplomacy.Peek(c, from.Id, to.Id);
        var grudgeThreshold = float.IsFinite(c.Defines.DealGrudgeOpinion) ? c.Defines.DealGrudgeOpinion : -25f;
        var penalty = relation is not null && relation.Opinion <= grudgeThreshold
            ? float.IsFinite(c.Defines.DealGrudgePenalty) ? MathF.Max(0, c.Defines.DealGrudgePenalty) : 15f : 0;
        var acceptable = theirs.Total - penalty >= 0;
        var fromCash = (float)((double)from.Treasury - deal.From.Cash + deal.To.Cash);
        var toCash = (float)((double)to.Treasury - deal.To.Cash + deal.From.Cash);
        if (deal.From.ReleaseRegions) fromCash -= Math.Max(0, fromCash) * Dismantlement.Share(c, from, deal.From.Regions);
        if (deal.To.ReleaseRegions) toCash -= Math.Max(0, toCash) * Dismantlement.Share(c, to, deal.To.Regions);
        return new(true, acceptable ? "" : DealRefusalNote(penalty - theirs.Total, penalty > 0), acceptable,
            ours, theirs, penalty, fromCash, toCash);
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
        if (!assessment.Valid || (!explicitConsent && !assessment.Acceptable)) return new(false, assessment.Reason);
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
        if (fromGround.Length > 0 && !deal.From.ReleaseRegions) StatePartition.Annex(c, fromGround, to, asConquest: false);
        if (toGround.Length > 0 && !deal.To.ReleaseRegions) StatePartition.Annex(c, toGround, from, asConquest: false);
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
        if (deal.From.TradeAgreement || deal.To.TradeAgreement) relation.TradeAgreement = true;
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
            var names = RegionIds(new Demand { Regions = side.Regions })
                .Select(id => Loc.Get(c.States.FirstOrDefault(s => s.MapIndex == id)?.Name ?? "")).Where(n => n.Length > 0)
                .ToList();
            if (names.Count > 0) parts.Add(side.ReleaseRegions
                ? Loc.Format("{0}: establish an independent country in {1}", giver.DisplayName, string.Join(", ", names))
                : Loc.Format("{0}: {1}", giver.DisplayName, string.Join(", ", names)));
            if (side.Cash > 0) parts.Add(Loc.Format("{0}: {1}", giver.DisplayName,
                Loc.Format("One-time payment: {0}", Fmt.Money(side.Cash))));
            if (side.NonAggression) parts.Add(Loc.Get("Non-aggression pact"));
            if (side.TradeAgreement) parts.Add(Loc.Get("Trade agreement"));
            if (side.LiftEmbargo) parts.Add(Loc.Format("{0}: lift embargo against {1}", giver.DisplayName, receiver.DisplayName));
        }
        Half(from, to, deal.From);
        Half(to, from, deal.To);
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
    public int[] ToRegions = Array.Empty<int>();
    public bool ToReleaseRegions;
    public float ToCash;
    public bool ToLiftEmbargo;
    public bool ToNonAggression;
    public bool ToTradeAgreement;

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
                LiftEmbargo = FromLiftEmbargo, NonAggression = FromNonAggression, TradeAgreement = FromTradeAgreement },
            new Ultimatum.Side { Regions = ToRegions ?? Array.Empty<int>(), Cash = ToCash,
                ReleaseRegions = ToReleaseRegions,
                LiftEmbargo = ToLiftEmbargo, NonAggression = ToNonAggression, TradeAgreement = ToTradeAgreement });
        if (to?.IsHuman == true || ParentOfferId != 0)
        {
            Outcome = DealNegotiations.Submit(campaign, from, to, deal, ParentOfferId);
            return;
        }
        var assessment = Ultimatum.AssessDeal(campaign, from, to, deal);
        if (!assessment.Acceptable) { Outcome = new(false, assessment.Reason); return; }
        Outcome = Ultimatum.ResolveDeal(campaign, from, to, deal);
    }
}
