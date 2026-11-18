using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// What a set of trade terms is worth to one of the two countries, in the deal table's points.
///
/// One count for everybody: the computer's answer, the deal table's line, the button on a country
/// page, the computer's own proposals, a player's standing answer and every preview call this, so a
/// player and a government looking at the same terms see the same number. There is no column: each
/// side counts the same terms from where it stands, and "who wrote it down" changes nothing.
///
/// The lines are the design's table 2.6 (docs/design/gameplay-2026-11-18/trade-agreements.md): what
/// the cuts let us sell and buy, the shortage they close, the guarantee we get or give, the duty we
/// forgo, how much our trade policy dislikes agreements, the dependence we accept - and how we regard
/// them, which is today's willingness less its bar. With every economic line at zero the answer is
/// the old button's, so the old rule is a special case of this one rather than a second rule beside it.
/// </summary>
public static class TradeTermsValue
{
    /// <summary>
    /// The week's numbers one country counts with, read once for a pair so a composer can weigh six
    /// candidates without walking the routes six times. Read-only: building one changes nothing.
    /// </summary>
    public sealed class View
    {
        public Nation X, Y;
        public Commodity[] Goods = Array.Empty<Commodity>();
        public float[] ShortX, SpareX, ShortY, SpareY, SatX, ImportsFromY, ExportsToY, MainOtherShare, BasePrice, Essential;
        public bool[] Strategic;
        public float Turnover, Revenue, Rate, Aversion, LeverageYOverX, Willingness, Needed, Trust;
        internal Campaign Campaign;
    }

    private sealed class EvalScratch
    {
        public readonly View Ours = new();
        public readonly View Theirs = new();
        public readonly View Single = new();
        public readonly Dictionary<(int Good, int Seller), float> FromOthers = new();
        public float[] Total = Array.Empty<float>();
    }

    [ThreadStatic] private static EvalScratch _scratch;

    internal static View ReadScratchOurs(Campaign c, Nation x, Nation y) =>
        ReadInto(c, x, y, (_scratch ??= new EvalScratch()).Ours);

    internal static View ReadScratchTheirs(Campaign c, Nation x, Nation y) =>
        ReadInto(c, x, y, (_scratch ??= new EvalScratch()).Theirs);

    public static View Read(Campaign c, Nation x, Nation y) =>
        ReadInto(c, x, y, new View());

    private static View ReadInto(Campaign c, Nation x, Nation y, View v)
    {
        var snap = CommodityCatalog.Snapshot;
        var goods = snap.AllGoods;
        var slots = snap.SlotByCommodity;
        var byIndex = snap.ByIndex;
        var n = goods.Length;
        v.Campaign = c;
        v.X = x;
        v.Y = y;
        v.Goods = goods;
        if (v.ShortX is null || v.ShortX.Length != n)
        {
            v.ShortX = new float[n];
            v.SpareX = new float[n];
            v.ShortY = new float[n];
            v.SpareY = new float[n];
            v.SatX = new float[n];
            v.ImportsFromY = new float[n];
            v.ExportsToY = new float[n];
            v.MainOtherShare = new float[n];
            v.BasePrice = new float[n];
            v.Essential = new float[n];
            v.Strategic = new bool[n];
        }
        else
        {
            Array.Clear(v.ImportsFromY, 0, n);
            Array.Clear(v.ExportsToY, 0, n);
            Array.Clear(v.MainOtherShare, 0, n);
        }

        // What X bought this week, from Y and from its largest other supplier, by good.
        var routes = TradeNetwork.RoutesTouching(c, x.Id);
        var scratch = _scratch ??= new EvalScratch();
        var fromOthers = scratch.FromOthers;
        fromOthers.Clear();
        if (scratch.Total.Length < n) scratch.Total = new float[n];
        else Array.Clear(scratch.Total, 0, n);
        var total = scratch.Total;
        for (var ri = 0; ri < routes.Count; ri++)
        {
            var r = routes[ri];
            var cIdx = (int)r.Commodity;
            if ((uint)cIdx >= (uint)slots.Length) continue;
            var i = slots[cIdx];
            if (i < 0 || !float.IsFinite(r.Volume)) continue;
            if (r.SellerId == x.Id && r.BuyerId == y.Id) { v.ExportsToY[i] += r.Volume; continue; }
            if (r.BuyerId != x.Id) continue;
            total[i] += r.Volume;
            if (r.SellerId == y.Id) v.ImportsFromY[i] += r.Volume;
            else fromOthers[(i, r.SellerId)] = fromOthers.GetValueOrDefault((i, r.SellerId)) + r.Volume;
        }
        foreach (var ((i, _), volume) in fromOthers)
            if (total[i] > 0.0001f) v.MainOtherShare[i] = MathF.Max(v.MainOtherShare[i], volume / total[i]);
        for (var i = 0; i < n; i++)
        {
            var good = goods[i];
            var def = byIndex[(int)good];
            v.BasePrice[i] = def.BasePrice;
            v.Essential[i] = M.Clamp01(def.EssentialWeight);
            v.Strategic[i] = def.Leverage != CommodityLeverageTier.None;
            v.ShortX[i] = TradeTermsCatalog.Shortfall(x, good);
            v.ShortY[i] = TradeTermsCatalog.Shortfall(y, good);
            v.SpareX[i] = TradeTermsCatalog.Spare(x, good);
            v.SpareY[i] = TradeTermsCatalog.Spare(y, good);
            v.SatX[i] = x.Market.TryGetValue(good, out var q) && float.IsFinite(q.Satisfaction) ? M.Clamp01(q.Satisfaction) : 1f;
        }
        v.Turnover = MathF.Max(0.01f, (float.IsFinite(x.ImportValue) ? x.ImportValue : 0f) + (float.IsFinite(x.ExportValue) ? x.ExportValue : 0f));
        v.Revenue = MathF.Max(0.001f, MathF.Max(0f, x.WeeklyTaxIncome) + MathF.Max(0f, x.WeeklyTariffIncome));
        v.Rate = MathF.Max(0f, TradePolicyRules.TariffRate(x.TradePolicy));
        v.Aversion = MathF.Max(0f, TradePolicyCatalog.Of(x.TradePolicy).AgreementAversion);
        v.LeverageYOverX = CommodityLeverage.Calculate(c, y, x);
        var relation = CampaignDiplomacy.Peek(c, x.Id, y.Id);
        v.Willingness = AgreementSystem.WillingnessWith(c, x, y, AgreementKind.TradeAgreement, relation);
        // An agreement in force does not vote for itself: the goodwill it bought ("trade_agreement")
        // is taken out of how X regards Y when X weighs keeping it, renewing it or revising it -
        // otherwise every renewal starts twenty-eight points of opinion ahead of the question.
        if (relation is { TradeAgreement: true })
            foreach (var reason in relation.Reasons)
                if (string.Equals(reason.Id, "trade_agreement", StringComparison.OrdinalIgnoreCase))
                    v.Willingness -= OpinionSystem.Weight(c, reason) / 100f;
        v.Needed = AgreementSystem.Needed(c, AgreementKind.TradeAgreement);
        v.Trust = 1f - M.Clamp01(MathF.Max(0f, y.Notoriety) / 200f);
        return v;
    }

    /// <summary>The terms' worth to <paramref name="x"/>, dealing with <paramref name="y"/>.</summary>
    public static float For(Campaign c, Nation x, Nation y, TradeTerms terms, Breakdown why = null)
    {
        if (c is null || x is null || y is null || terms is null) return 0f;
        return Value(ReadInto(c, x, y, (_scratch ??= new EvalScratch()).Single), terms, why);
    }

    public static float Value(View v, TradeTerms terms, Breakdown why = null)
    {
        if (v is null || terms is null) return 0f;
        var d = v.Campaign.Defines;
        int x = v.X.Id, y = v.Y.Id;
        float exp = 0f, imp = 0f, shortage = 0f, duty = 0f, strategicCut = 0f;
        var strategicCount = 0;
        // A renewal is judged on what the agreement did, not on what it could do: the goods that
        // actually moved between the two under it this week stand in for what might have. A trial
        // that carried nothing is not renewed on its promise.
        var judged = terms.Renewal;
        for (var i = 0; i < v.Goods.Length; i++)
        {
            var g = v.Goods[i];
            var theirCut = terms.CutFor(y, x, g);
            var ourCut = terms.CutFor(x, y, g);
            var sold = judged ? v.ExportsToY[i] : MathF.Min(v.SpareX[i], v.ShortY[i]);
            if (theirCut > 0f) exp += theirCut * sold * v.BasePrice[i];
            if (v.Strategic[i]) { strategicCut += ourCut; strategicCount++; }
            if (ourCut <= 0f) continue;
            var reachable = MathF.Min(v.ShortX[i], v.SpareY[i]);
            var bought = judged ? MathF.Min(v.ShortX[i], v.ImportsFromY[i]) : reachable;
            imp += ourCut * bought * v.BasePrice[i] * (0.5f + v.Essential[i]);
            if (v.ShortX[i] > 0.0001f)
                shortage = MathF.Max(shortage, ourCut * (1f - v.SatX[i]) * v.Essential[i] *
                                               MathF.Min(1f, (judged ? v.ImportsFromY[i] : v.SpareY[i]) / v.ShortX[i]));
            duty += ourCut * (judged ? v.ImportsFromY[i] : MathF.Max(v.ImportsFromY[i], reachable)) * v.BasePrice[i] * v.Rate;
        }
        exp = d.TradeValueExport * exp / v.Turnover * v.Trust;
        imp = d.TradeValueImport * imp / v.Turnover;
        shortage *= d.TradeValueShortage;
        // A share of revenue: never more than all of it, so a government with next to no revenue
        // (a ruin, a fixture) counts its customs house lost and not an infinite loss.
        duty = -d.TradeValueDuty * MathF.Min(1f, duty / v.Revenue);

        var ourHalf = terms.CutOf(x, y);
        var goods = terms.GoodsOf(x, y);
        var breadth = goods.Length == 0 ? 1f : MathF.Min(1f, 0.25f + 0.15f * goods.Length);
        var aversion = -v.Aversion * ourHalf * breadth;
        var dependence = strategicCount == 0 ? 0f : -d.TradeValueDependence * (strategicCut / strategicCount) * v.LeverageYOverX;

        float guaranteeIn = 0f, guaranteeOut = 0f;
        if (terms.TryGuaranteeGood(out var good))
        {
            var i = Array.IndexOf(v.Goods, good);
            if (i >= 0 && terms.GuarantorId == y)
            {
                var cover = v.ShortX[i] > 0.0001f ? MathF.Min(1f, terms.GuaranteeShare * v.SpareY[i] / v.ShortX[i]) : 0f;
                // A guarantee renewed is worth what it was kept: a promise half honoured covers half.
                if (judged) cover *= M.Clamp01(terms.Delivered);
                guaranteeIn = d.TradeValueGuaranteeIn * v.Essential[i] * cover *
                              (0.25f + (1f - v.SatX[i]) + v.MainOtherShare[i]) * v.Trust;
            }
            else if (i >= 0 && terms.GuarantorId == x)
            {
                guaranteeOut = -d.TradeValueGuaranteeOut * terms.GuaranteeShare * (1f + 2f * GoodLeverage(v.Campaign, v.X, v.Y, good));
                if (v.ShortX[i] > 0.0001f) guaranteeOut -= d.TradeValueGuaranteeOutShort;
            }
        }
        var gain = TradeTermsCatalog.TermFactor(d, terms.Years, cost: false);
        var cost = TradeTermsCatalog.TermFactor(d, terms.Years, cost: true);
        var relations = d.TradeValueRelations * (v.Willingness - v.Needed);
        if (why is not null)
        {
            why.Add("We sell more", exp * gain);
            why.Add("We buy cheaper", imp * gain);
            why.Add("It closes a shortage", shortage * gain);
            why.Add("Their supply guarantee", guaranteeIn * gain);
            why.Add("Duty we forgo", duty * cost);
            why.Add("Our trade policy", aversion * cost);
            why.Add("Dependence on them", dependence * cost);
            why.Add("Our supply guarantee", guaranteeOut * cost);
            why.Add("How we regard them", relations);
        }
        var total = (exp + imp + shortage + guaranteeIn) * gain + (duty + aversion + dependence + guaranteeOut) * cost + relations;
        return float.IsFinite(total) ? total : 0f;
    }

    /// <summary>
    /// What one good from X is to Y: X's share of Y's imports of it, weighted by how much of its need
    /// Y imports - the per-good form of CommodityLeverage. A guarantee of a good the partner already
    /// cannot do without is a bigger hostage to hand over.
    /// </summary>
    public static float GoodLeverage(Campaign c, Nation x, Nation y, Commodity good)
    {
        if (!y.Market.TryGetValue(good, out var q)) return 0f;
        float total = 0f, fromX = 0f;
        foreach (var r in TradeNetwork.RoutesTouching(c, y.Id))
        {
            if (r.BuyerId != y.Id || r.Commodity != good || !float.IsFinite(r.Volume)) continue;
            total += r.Volume;
            if (r.SellerId == x.Id) fromX += r.Volume;
        }
        if (fromX <= 0.0001f) return 0f;
        var imports = MathF.Max(q.Imports, total);
        var reliance = MathF.Max(q.Demand, imports) > 0.001f ? M.Clamp01(imports / MathF.Max(q.Demand, imports)) : 1f;
        return M.Clamp01(fromX / MathF.Max(0.0001f, imports)) * (0.35f + 0.65f * reliance);
    }

    /// <summary>
    /// What the terms would change for X, in the numbers a player reads under the deal window: more
    /// units a week of a good from them, the import price as far as the markup goes, the duty forgone
    /// a year and how much of a shortage a guarantee would cover. Estimates on this week's market.
    /// </summary>
    public static IEnumerable<string> Effects(View v, TradeTerms terms, int max = 4)
    {
        if (v is null || terms is null) yield break;
        var d = v.Campaign.Defines;
        int x = v.X.Id, y = v.Y.Id;
        var lines = new List<(float Weight, string Text)>();
        var forgone = 0f;
        for (var i = 0; i < v.Goods.Length; i++)
        {
            var cut = terms.CutFor(x, y, v.Goods[i]);
            if (cut <= 0f) continue;
            var reachable = MathF.Min(v.ShortX[i], v.SpareY[i]);
            forgone += cut * MathF.Max(v.ImportsFromY[i], reachable) * v.BasePrice[i] * v.Rate * d.MarketTreasuryTradeShare * 52f;
            if (reachable <= 0.0001f) continue;
            var priceCut = (d.ImportMarkup - 1f) * d.TradeAgreementMarkupRelief * cut / MathF.Max(1f, d.ImportMarkup);
            lines.Add((reachable * v.BasePrice[i], Loc.Format("{0}: up to {1} a week from them, price here up to {2}",
                Loc.Get(CommodityCatalog.Get(v.Goods[i]).Name), reachable.ToString("0.##"), Fmt.PercentDelta(-priceCut))));
        }
        foreach (var line in lines.OrderByDescending(l => l.Weight).Take(Math.Max(0, max - 2))) yield return line.Text;
        if (forgone > 0.00001f) yield return Loc.Format("Duty we forgo: about {0} a year", Fmt.Money(forgone));
        if (terms.TryGuaranteeGood(out var good) && terms.GuarantorId == y)
        {
            var i = Array.IndexOf(v.Goods, good);
            if (i >= 0 && v.ShortX[i] > 0.0001f)
                yield return Loc.Format("Guarantee: up to {0} of our shortfall of {1}",
                    Fmt.Percent(MathF.Min(1f, terms.GuaranteeShare * v.SpareY[i] / v.ShortX[i])), Loc.Get(CommodityCatalog.Get(good).Name));
        }
    }
}
