using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// Border diplomacy (BorderDiplomacy): how often a country looks at the borders it resents, in
    /// days (staggered by country), and how badly it must resent one (BorderShape, 0..1) to act.
    /// 0 days switches it off.
    /// </summary>
    public int BorderDealReviewDays { get; set; } = 120;
    public float BorderDealMinGrievance { get; set; } = 0.3f;
    /// <summary>After an approach - signed, refused or pressed - the same pair is left alone this long.</summary>
    public int BorderDealRetryDays { get; set; } = 900;
    /// <summary>The most of its treasury a country will pay for a region that fixes its border.</summary>
    public float BorderDealTreasuryShare { get; set; } = 0.3f;
    /// <summary>
    /// A refusal turns into pressure only for a grievance this bad, and into an ultimatum only for a
    /// country this much stronger than the one refusing; a weaker one lodges a dispute instead,
    /// which raises the tension between them by so much and leaves a grievance of so much.
    /// </summary>
    public float BorderPressureGrievance { get; set; } = 0.33f;
    public float BorderUltimatumStrength { get; set; } = 1.5f;
    public float BorderDisputeTension { get; set; } = 10f;
    public float BorderDisputeGrievance { get; set; } = 6f;
}

/// <summary>
/// Ugly borders, and what governments do about them short of war.
///
/// BorderShape already knew which borders nobody can live with - the enclave, the salient, the
/// region of ours the neighbour has cut off - and let that grievance feed tension and the appetite
/// for war. That left war as the only cure. Real governments reach for the cheaper ones first:
/// India and Bangladesh swapped a hundred and sixty enclaves in 2015; the Netherlands and Belgium
/// traded land on the Meuse in 2018; borders have been bought (the Gadsden Purchase) and redrawn
/// under pressure when the other side would neither swap nor sell.
///
/// So every few months a country with a bad border looks at it and reaches for the cheapest thing
/// that works: a swap that cleans both sides' borders (each side's pocket for the other's, or one
/// of ours that juts into them for theirs that juts into us), then a purchase of the region it
/// wants, then a sale of the pocket it cannot hold. Every offer goes over the ordinary deal table -
/// the same valuation, which now counts what a cleaner border is worth to each side (Deals,
/// <see cref="Defines.DealBorderReliefWorth"/>), so the other side accepts a fair swap by the same
/// rule a player's proposal is judged by, and a player receives the paper in the inbox. When
/// nothing on the table is acceptable and the grievance is bad, the stronger side presses with an
/// ultimatum through the ordinary ultimatum machinery (a refusal is a reason for war), and the
/// weaker lodges a dispute that raises the tension - the road to a crisis. Both sides of a border
/// run the same review, so the one with the pocket and the one around it each try their own fix.
/// </summary>
public static class BorderDiplomacy
{
    // What happened, for benches. Not campaign state: never read by the simulation.
    public static int Reviews, Swaps, Purchases, Sales, Ultimatums, UltimatumsYielded, Disputes, Offered;

    public static void ResetCounters() => Reviews = Swaps = Purchases = Sales = Ultimatums = UltimatumsYielded = Disputes = Offered = 0;

    public static void Tick(Campaign c)
    {
        foreach (var _ in TickStages(c)) { }
    }

    /// <summary>The same border review, yielded between batches of countries.</summary>
    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c?.ProvinceMap is null || c.Defines.BorderDealReviewDays <= 0 || !c.FeatureEnabled("deals")) yield break;
        var every = Math.Max(7, c.Defines.BorderDealReviewDays);
        var count = c.Nations.Count;
        var ticked = 0;
        var warmed = false;
        for (var i = 0; i < count && i < c.Nations.Count; i++)
        {
            var nation = c.Nations[i];
            if ((c.ElapsedDays + (long)nation.Id * 13) % every != 5) continue;
            if (nation.IsHuman && !Delegation.Holds(nation, Delegation.Diplomacy)) continue;
            if (nation.Movement is not null || nation.IsSubject || nation.Population <= 0 || !c.HasOwnedLand(nation)) continue;
            if (!warmed)
            {
                warmed = true;
                foreach (var _ in BorderShape.EnsureStages(c)) yield return 0;
            }
            if (++ticked % 2 == 0 || Campaign.StageOverdue()) yield return 0;
            // A country weighing a swap prices every pair of regions it might trade: tens of
            // milliseconds for a long border, in one piece. Last time's cost says which (StageCosts).
            if (StageCosts.Heavy("border deal", nation.Id)) { Campaign.HeavyNext(); yield return 0; }
            var started = StageCosts.Start();
            // And the review itself is staged between the deals it prices (ReviewStages): one
            // long border was a hundred to six hundred milliseconds in one piece, the worst
            // frame of a late save at the top speed.
            foreach (var _ in ReviewStages(c, nation, null))
            {
                StageCosts.Stop("border deal", nation.Id, started);
                yield return 0;
                started = StageCosts.Start();
            }
            StageCosts.Stop("border deal", nation.Id, started);
        }
    }

    private static readonly List<int> Wanted = new(), Pockets = new();

    /// <summary>One look at this country's worst border, and one approach at most.</summary>
    public static bool Review(Campaign c, Nation nation)
    {
        var reviewed = new bool[1];
        foreach (var _ in ReviewStages(c, nation, reviewed)) { }
        return reviewed[0];
    }

    /// <summary>
    /// The same review, yielded between the deals it prices. A yield is not part of the world:
    /// nothing else runs at one, so the deals are priced in the same order against the same
    /// world, and the same one is chosen.
    /// </summary>
    private static IEnumerable<int> ReviewStages(Campaign c, Nation nation, bool[] reviewed)
    {
        var byIndex = c.NationsByMapIndex();
        foreach (var (otherIndex, grievance) in BorderShape.Against(c, nation))
        {
            if (grievance < c.Defines.BorderDealMinGrievance) break;
            if (!byIndex.TryGetValue((ushort)otherIndex, out var other) || other.Id == nation.Id) continue;
            if (other.Movement is not null || other.Population <= 0) continue;
            var key = "border_deal/" + other.Id;
            if (nation.DecisionAvailableDay.GetValueOrDefault(key) > c.ElapsedDays) continue;
            if (!Ultimatum.CanNegotiateDeal(c, nation, other, out _)) continue;
            Reviews++;
            nation.DecisionAvailableDay[key] = c.ElapsedDays + Math.Max(30, c.Defines.BorderDealRetryDays);
            foreach (var _ in ApproachStages(c, nation, other, grievance, null)) yield return 0;
            if (reviewed is not null) reviewed[0] = true;
            yield break;
        }
    }

    private enum Means { Swap, Purchase, Sale }

    /// <summary>
    /// Lay the cheapest acceptable fix on the table, or press when there is none. Public so a test
    /// can ask for one pair; the weekly review calls it for the worst border it has.
    /// </summary>
    public static string Approach(Campaign c, Nation nation, Nation other, float grievance)
    {
        var result = new string[] { "" };
        foreach (var _ in ApproachStages(c, nation, other, grievance, result)) { }
        return result[0];
    }

    /// <summary>
    /// The approach, yielded after every deal it prices: each is a full assessment of both
    /// sides' books, and there can be twenty-four of them. The answer goes into
    /// <paramref name="result"/>[0] when a caller wants it.
    /// </summary>
    private static IEnumerable<int> ApproachStages(Campaign c, Nation nation, Nation other, float grievance, string[] result)
    {
        BorderShape.Awkward(c, nation, other, Wanted, Pockets);
        var wanted = Wanted.Where(id => Transferable(c, other, id)).Take(4).ToList();
        var pockets = Pockets.Where(id => Transferable(c, nation, id)).Take(4).ToList();
        if (wanted.Count == 0 && pockets.Count == 0) yield break;

        var choice = new Choice();

        // 1. A swap: each side's pocket for the other's, or one of our bulges into them for theirs into us.
        var offers = pockets.Count > 0 ? pockets : Bulges(c, nation, other);
        foreach (var take in wanted)
            foreach (var give in offers)
            {
                var swap = new Ultimatum.Deal(new Ultimatum.Side { Regions = new[] { give } }, new Ultimatum.Side { Regions = new[] { take } });
                var balanced = Balance(c, nation, other, swap, out var pre);
                choice.Consider(c, nation, other, balanced, Means.Swap, pre);
                yield return 0;
            }
        // 2. A purchase of what we want.
        foreach (var take in wanted)
        {
            var balanced = Balance(c, nation, other, new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side { Regions = new[] { take } }), out var pre);
            choice.Consider(c, nation, other, balanced, Means.Purchase, pre);
            yield return 0;
        }
        // 3. A sale of what we cannot hold, at what it is worth to them.
        foreach (var give in pockets)
        {
            var asking = Asking(c, nation, other, give, out var pre);
            choice.Consider(c, nation, other, asking, Means.Sale, pre);
            yield return 0;
        }

        var answer = Settle(c, nation, other, grievance, wanted, choice);
        if (result is not null) result[0] = answer;
    }

    /// <summary>The best deal priced so far: the cheapest means is the one that leaves us best off.</summary>
    private sealed class Choice
    {
        public Ultimatum.Deal Best;
        public Means BestMeans = Means.Swap;
        public float BestValue = float.NegativeInfinity;

        public void Consider(Campaign c, Nation nation, Nation other, Ultimatum.Deal deal, Means means,
            Ultimatum.DealAssessment precomputed = null)
        {
            if (deal is null) return;
            var assessment = precomputed ?? Ultimatum.AssessNegotiatedDeal(c, nation, other, deal);
            if (!assessment.Valid || !assessment.Acceptable || assessment.FromValue < 0f) return;
            // What it costs us, in our own points.
            if (assessment.FromValue <= BestValue) return;
            BestValue = assessment.FromValue; Best = deal; BestMeans = means;
        }
    }

    /// <summary>The chosen deal laid on the table, or the grievance pressed when there is none.</summary>
    private static string Settle(Campaign c, Nation nation, Nation other, float grievance, List<int> wanted, Choice choice)
    {
        var best = choice.Best;
        var bestMeans = choice.BestMeans;
        if (best is not null)
        {
            var summary = Ultimatum.DealSummary(c, nation, other, best);
            var outcome = DealNegotiations.Submit(c, nation, other, best);
            if (outcome.PendingOfferId > 0) { Offered++; return "offered"; }
            if (outcome.Accepted)
            {
                switch (bestMeans)
                {
                    case Means.Swap: Swaps++; break;
                    case Means.Purchase: Purchases++; break;
                    default: Sales++; break;
                }
                c.Chronicle.Record(c, bestMeans switch { Means.Swap => "border_swap", Means.Purchase => "border_purchase", _ => "border_sale" },
                    nation.Id, other.Id, subject: summary);
                return bestMeans.ToString().ToLowerInvariant();
            }
        }
        return Press(c, nation, other, grievance, wanted);
    }

    /// <summary>
    /// Nothing on the table works. A bad enough grievance is pressed: the stronger side demands the
    /// region by ultimatum, the weaker lodges a dispute that raises the temperature.
    /// </summary>
    private static string Press(Campaign c, Nation nation, Nation other, float grievance, List<int> wanted)
    {
        if (grievance < c.Defines.BorderPressureGrievance || wanted.Count == 0) return "";
        if (CampaignDiplomacy.AtWar(c, nation.Id, other.Id)) return "";
        var mine = AiStatecraft.StrengthOf(c, nation);
        var theirs = MathF.Max(0.01f, AiStatecraft.StrengthOf(c, other));
        var key = "ultimatum/" + other.Id;
        var region = wanted.OrderByDescending(id => c.StateByMapIndex(id)?.Population ?? 0).ThenBy(id => id).First();
        if (c.Defines.AiUltimatumsBeforeWar && mine / theirs >= c.Defines.BorderUltimatumStrength &&
            nation.DecisionAvailableDay.GetValueOrDefault(key) <= c.ElapsedDays && !Ultimatum.PendingBetween(c, nation.Id, other.Id) &&
            Ultimatum.CanDemand(c, other, region))
        {
            var demand = new Ultimatum.Demand { Regions = new[] { region } };
            if (other.IsHuman)
            {
                if (!Ultimatum.ValidateTerms(c, nation, other, demand, out _)) return Dispute(c, nation, other);
                nation.DecisionAvailableDay[key] = c.ElapsedDays + Math.Max(30, c.Defines.AiUltimatumIntervalDays);
                if (!Ultimatum.Deliver(c, nation, other, demand).Accepted) return Dispute(c, nation, other);
                Ultimatums++;
                return "ultimatum";
            }
            var assessment = Ultimatum.Assess(c, nation, other, demand);
            if (!assessment.Valid) return Dispute(c, nation, other);
            Ultimatum.Resolve(c, nation, other, demand, assessment.Response);
            Ultimatums++;
            if (assessment.Response == Ultimatum.Answer.Yield) UltimatumsYielded++;
            return assessment.Response == Ultimatum.Answer.Yield ? "ultimatum_yielded" : "ultimatum_refused";
        }
        return Dispute(c, nation, other);
    }

    /// <summary>A border dispute lodged: the tension rises and the grievance is written down - the road to a crisis.</summary>
    private static string Dispute(Campaign c, Nation nation, Nation other)
    {
        var relation = CampaignDiplomacy.Get(c, nation.Id, other.Id);
        if (relation is null) return "";
        relation.Tension = MathF.Min(100f, relation.Tension + MathF.Max(0f, c.Defines.BorderDisputeTension));
        var amount = MathF.Max(0f, c.Defines.BorderDisputeGrievance);
        if (relation.A == nation.Id) relation.GrievanceA = MathF.Min(100f, relation.GrievanceA + amount);
        else relation.GrievanceB = MathF.Min(100f, relation.GrievanceB + amount);
        OpinionSystem.Remember(c, relation, "refused_our_border_settlement");
        Disputes++;
        c.Chronicle.Record(c, "border_dispute", nation.Id, other.Id);
        c.MarkStrategicChange();
        return "dispute";
    }

    /// <summary>A region that can change hands at all: not a capital, wholly held and controlled by its owner, and not all it has.</summary>
    private static bool Transferable(Campaign c, Nation owner, int region) =>
        region != owner.CapitalStateMapIndex && Ultimatum.CanDemand(c, owner, region);

    /// <summary>
    /// Our regions that jut into the other: more than half their land border is theirs. What we can
    /// offer in a swap when we have no pocket of our own to give, largest share first.
    /// </summary>
    private static List<int> Bulges(Campaign c, Nation nation, Nation other)
    {
        var result = new List<(int Region, float Share)>();
        foreach (var state in c.StatesOf(nation))
        {
            var total = 0; var theirs = 0;
            foreach (var (next, edges) in TerritorialCoherence.RegionBorders(c, state.MapIndex))
            {
                var nextState = c.StateByMapIndex(next);
                var by = nextState?.NationMapIndex ?? 0;
                if (by <= 0) continue;
                total += edges;
                if (by == other.MapIndex) theirs += edges;
            }
            if (total <= 0 || theirs * 2 <= total) continue;
            if (!Transferable(c, nation, state.MapIndex)) continue;
            result.Add((state.MapIndex, theirs / (float)total));
        }
        return result.OrderByDescending(r => r.Share).ThenBy(r => r.Region).Take(3).Select(r => r.Region).ToList();
    }

    /// <summary>
    /// The table as it stands if they would sign it; else the same table with the cheque that would
    /// make them sign, if we can and would pay it. Null when it cannot be balanced.
    /// </summary>
    private static Ultimatum.Deal Balance(Campaign c, Nation nation, Nation other, Ultimatum.Deal deal,
        out Ultimatum.DealAssessment precomputed)
    {
        precomputed = null;
        var assessment = Ultimatum.AssessNegotiatedDeal(c, nation, other, deal);
        if (!assessment.Valid) return null;
        if (assessment.Acceptable) { precomputed = assessment; return deal; }
        if (assessment.TerritoryProtected) return null;
        var shortfall = assessment.GrudgePenalty - assessment.ToValuation.Total;
        var weight = Ultimatum.CashWeight(other);
        if (!float.IsFinite(shortfall) || !float.IsFinite(weight) || weight <= 0f) return null;
        var cash = MathF.Ceiling((shortfall / weight + 0.01f) * 1.05f * 100f) / 100f;
        if (cash <= 0f || cash > MathF.Max(0f, nation.Treasury) * M.Clamp01(c.Defines.BorderDealTreasuryShare)) return null;
        return deal with { From = deal.From with { Cash = cash } };
    }

    /// <summary>Our pocket for their money: half of what it is worth to them above nothing, as far as they can pay.</summary>
    private static Ultimatum.Deal Asking(Campaign c, Nation nation, Nation other, int pocket,
        out Ultimatum.DealAssessment precomputed)
    {
        precomputed = null;
        var deal = new Ultimatum.Deal(new Ultimatum.Side { Regions = new[] { pocket } }, new Ultimatum.Side());
        var assessment = Ultimatum.AssessNegotiatedDeal(c, nation, other, deal);
        if (!assessment.Valid || !assessment.Acceptable) return null;
        var surplus = assessment.ToValuation.Total - assessment.GrudgePenalty;
        var weight = Ultimatum.CashWeight(other);
        if (!float.IsFinite(surplus) || weight <= 0f) { precomputed = assessment; return deal; }
        var room = MathF.Max(0f, other.Treasury) * M.Clamp01(c.Defines.DealMaxTreasuryShare) * 0.5f;
        var asking = MathF.Floor(MathF.Min(room, surplus * 0.5f / weight) * 100f) / 100f;
        if (asking <= 0f) { precomputed = assessment; return deal; }
        return deal with { To = deal.To with { Cash = asking } };
    }

    // ------------------------------------------------------------------ CONS-03 enclave land-corridor transit

    /// <summary>
    /// Counts cut-off pockets belonging to <paramref name="owner"/> that touch <paramref name="neighbour"/>.
    /// Read-only and deterministic; never mutates campaign state.
    /// </summary>
    public static int CountIsolatedPockets(Campaign c, Nation owner, Nation neighbour)
    {
        if (c?.ProvinceMap is null || owner is null || neighbour is null || owner.Id == neighbour.Id) return 0;
        var count = 0;
        foreach (var state in c.StatesOf(owner))
        {
            if (state.Population <= 0 || state.Unclaimed) continue;
            if (!BorderShape.IsCutOffPocket(c, state)) continue;
            foreach (var (adj, _) in TerritorialCoherence.RegionBorders(c, state.MapIndex))
            {
                if (c.StateByMapIndex(adj)?.NationMapIndex == neighbour.MapIndex)
                {
                    count++;
                    break;
                }
            }
        }
        return count;
    }

    /// <summary>True when <paramref name="grantor"/> and <paramref name="grantee"/> share a land border
    /// or either holds a cut-off enclave touching the other.</summary>
    public static bool HasCorridorNeed(Campaign c, Nation grantor, Nation grantee)
    {
        if (c?.ProvinceMap is null || grantor is null || grantee is null || grantor.Id == grantee.Id) return false;
        if (CampaignDiplomacy.SharesLandBorder(c, grantor.MapIndex, grantee.MapIndex)) return true;
        return CountIsolatedPockets(c, grantee, grantor) > 0 || CountIsolatedPockets(c, grantor, grantee) > 0;
    }

    /// <summary>Why <paramref name="grantor"/> cannot grant a land-transit corridor to <paramref name="grantee"/>, or empty.</summary>
    public static string CorridorRefusal(Campaign c, Nation grantor, Nation grantee)
    {
        if (c?.ProvinceMap is null || grantor is null || grantee is null || grantor.Id == grantee.Id)
            return Loc.Get("Invalid target.");
        if (!HasCorridorNeed(c, grantor, grantee))
            return Loc.Get("deal.refuse.corridor_no_border");
        var key = $"deal_corridor/{grantor.Id}/{grantee.Id}";
        if (grantor.DecisionAvailableDay.TryGetValue(key, out var avail) && avail > c.ElapsedDays)
            return Loc.Format("deal.refuse.clause_cooldown_fmt", avail - c.ElapsedDays);
        return "";
    }

    /// <summary>
    /// Diplomatic utility points a land-transit corridor across <paramref name="grantor"/> is worth to
    /// <paramref name="grantee"/>: base border transit plus strong relief for cut-off enclaves.
    /// </summary>
    public static float CorridorReliefValue(Campaign c, Nation grantee, Nation grantor)
    {
        if (!HasCorridorNeed(c, grantor, grantee)) return 0f;
        var pockets = CountIsolatedPockets(c, grantee, grantor);
        var grievance = Math.Clamp(BorderShape.Grievance(c, grantee, grantor), 0f, 1f);
        return 10f + Math.Min(3, pockets) * 5.5f + grievance * 12f;
    }
}
