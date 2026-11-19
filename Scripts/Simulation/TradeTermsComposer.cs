using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// How the computer writes trade terms: one composer for every reason it has to ask.
///
/// It builds the presets that fit the pair (common/deals "trade_presets", in their order), counts each
/// from both seats with the same count every answer uses (TradeTermsValue), and asks for the one best
/// for itself among those the partner would sign. Friendship tries all of them; a supply hedge tries
/// the ones about the scarce good, with the largest guarantee the partner would give. Toward a player
/// it sends exactly what it would send that country under the computer, and only if the same count
/// says the player's country would sign it - the old paper went to players with no bar at all.
/// </summary>
public static class TradeTermsComposer
{
    public sealed record Candidate(TradeTerms Terms, float ProposerValue, float PartnerValue, int Order);

    /// <summary>
    /// The presets made into terms for this pair, in order; those that do not fit are left out. Each
    /// fixed-term one is followed by the same terms for a short trial (TradeShortTrialYears) - the length is a term like
    /// any other, and "the same, but for two years" is the first thing a government that would not
    /// bind itself for five will still sign. The trial is asked for only where the partner would
    /// refuse the preset's own length (<see cref="Ranked"/>), so the presets keep their lengths.
    /// </summary>
    public static List<TradeTerms> Candidates(Campaign c, Nation proposer, Nation partner,
        IReadOnlyCollection<string> presets = null, Commodity? good = null, float[] shares = null)
    {
        var list = new List<TradeTerms>();
        var trial = c?.Defines.TradeShortTrialYears ?? 0;
        if (!TradeTermsCatalog.Spec.Years.Contains(trial)) trial = 0;
        void Add(TradeTerms t)
        {
            list.Add(t);
            // Only a fixed term has a shorter version: an agreement with no end binds by its exit
            // price alone and is the open-ended kind already, not a long commitment to cut down.
            if (trial <= 0 || t.Years <= trial) return;
            var shorter = TradeTerms.Copy(t);
            shorter.Years = trial;
            shorter.Trial = true;
            list.Add(shorter);
        }
        foreach (var preset in TradeTermsCatalog.Composed)
        {
            if (presets is not null && !presets.Contains(preset.Id)) continue;
            if (preset.Guarantee != "none" && shares is { Length: > 0 })
            {
                foreach (var share in shares)
                    if (TradeTermsCatalog.Build(c, preset, proposer, partner, good, share) is { } t) Add(t);
            }
            else if (TradeTermsCatalog.Build(c, preset, proposer, partner, good) is { } terms) Add(terms);
        }
        return list;
    }

    /// <summary>
    /// Each candidate counted from both seats, the ones both would sign, best for the proposer first;
    /// a tie keeps the preset order. The partner's count is the one its answer is made of (the deal
    /// table's, grudge floor included), so for a government the list holds no surprises.
    /// </summary>
    public static List<Candidate> Ranked(Campaign c, Nation proposer, Nation partner, List<TradeTerms> candidates)
    {
        var ranked = new List<Candidate>(candidates.Count);
        if (candidates.Count == 0) return ranked;
        var ours = TradeTermsValue.ReadScratchOurs(c, proposer, partner);
        var theirs = TradeTermsValue.ReadScratchTheirs(c, partner, proposer);
        var grudge = Ultimatum.GrudgePenalty(c, proposer, partner);
        var previousSignable = false;
        for (var i = 0; i < candidates.Count; i++)
        {
            var mine = TradeTermsValue.Value(ours, candidates[i]);
            var yours = TradeTermsValue.Value(theirs, candidates[i]) - grudge;
            // A trial follows its preset in the list, and is put only where the preset itself would
            // be refused: a shorter term is what one asks for after "not for five years".
            var trialNotNeeded = candidates[i].Trial && previousSignable;
            if (!candidates[i].Trial) previousSignable = yours >= 0f;
            if (trialNotNeeded) continue;
            if (mine >= 0f && yours >= 0f) ranked.Add(new Candidate(candidates[i], mine, yours, i));
        }
        return ranked.OrderByDescending(r => r.ProposerValue).ThenBy(r => r.Order).ToList();
    }

    /// <summary>
    /// Ask for the best candidate, and if it is refused the next, in the same breath (at most two).
    /// The refusal pause applies after the second, as it would after any refusal. Returns the
    /// result of the last ask, or NotAsked when no candidate is worth putting.
    /// </summary>
    public static AgreementResult Propose(Campaign c, Nation proposer, Nation partner, List<TradeTerms> candidates,
        out TradeTerms signed)
    {
        signed = null;
        var ranked = Ranked(c, proposer, partner, candidates);
        var result = AgreementResult.NotAsked;
        var before = CampaignDiplomacy.Peek(c, proposer.Id, partner.Id);
        var refusedBefore = before?.AgreementRefusedDay ?? -9999;
        var actedBefore = before?.LastActionDay ?? -30;
        for (var attempt = 0; attempt < Math.Min(2, ranked.Count); attempt++)
        {
            if (attempt > 0 && CampaignDiplomacy.Peek(c, proposer.Id, partner.Id) is { } relation)
            {
                // The second try is part of the same visit, not a new one: the first refusal's
                // date and the mission it counted are taken back so the second may be heard.
                relation.AgreementRefusedDay = refusedBefore;
                relation.LastActionDay = actedBefore;
            }
            var ask = new ProposeAgreementCommand
            {
                IssuerId = proposer.Id, TargetId = partner.Id, Kind = AgreementKind.TradeAgreement,
                Trade = ranked[attempt].Terms
            };
            ask.Apply(c);
            result = ask.Result;
            if (result is AgreementResult.Signed or AgreementResult.Offered)
            {
                signed = ranked[attempt].Terms;
                return result;
            }
            if (result != AgreementResult.Refused) return result;
        }
        return result;
    }

    /// <summary>
    /// What the composer would ask a player's country for, and whether the same count says that
    /// country would sign it - the gate every computer-to-player trade paper passes.
    /// </summary>
    public static Candidate BestFor(Campaign c, Nation proposer, Nation partner, List<TradeTerms> candidates) =>
        Ranked(c, proposer, partner, candidates).FirstOrDefault();
}

/// <summary>
/// A term running out, a guarantee not kept, a flag found without terms: the daily sweep of every
/// trade agreement in force (AgreementSystem.TickStages calls it).
///
/// Two computers renew what both still want, try two smaller versions (a smaller guarantee, fewer
/// goods, two years) when one of them does not, and let it lapse otherwise. A player is sent the
/// renewal as an ordinary paper thirty days ahead, by the computer side and only if it still wants
/// it; the player's standing answer applies, and an unanswered paper lets the term lapse on its day.
/// A computer leaves a guarantee its partner has not delivered for eight weeks, free, and walks away
/// from one it can no longer keep - at the price anybody pays for that.
/// </summary>
public static class TradeTermsLifecycle
{
    private enum Act { Normalize, Expire, RenewOrLapse, RenewalPaper, ReceiverExit, GuarantorExit }

    public static void Normalize(Campaign c)
    {
        if (c is null) return;
        foreach (var r in c.Relations.Values.Concat(c.DormantRelations.Values)) NormalizeOne(c, r);
    }

    /// <summary>The flag is up exactly when there are terms: an old flag gets the old terms, stray terms go.</summary>
    private static bool NormalizeOne(Campaign c, DiplomaticRelation r)
    {
        if (r.TradeAgreement && r.Trade is null) { r.Trade = TradeTerms.Legacy(c.ElapsedDays); return true; }
        if (!r.TradeAgreement && r.Trade is not null) { r.Trade = null; return true; }
        return false;
    }

    public static IEnumerable<int> TickStages(Campaign c)
    {
        if (c is null) yield break;
        var day = c.ElapsedDays;
        var weeks = TradeTermsCatalog.FreeExitWeeks(c.Defines);
        var notice = Math.Clamp(c.Defines.TradeRenewalNoticeDays, 0, 365);
        List<(DiplomaticRelation Relation, Act Act)> acts = null;
        var scanned = 0;
        foreach (var r in c.Relations.Values)
        {
            if (++scanned % 1024 == 0) yield return 0;
            if (!r.TradeAgreement && r.Trade is null) continue;
            NormalizeOne(c, r);
            var t = r.Trade;
            if (t is null) continue;
            var a = c.NationById(r.A);
            var b = c.NationById(r.B);
            if (a is null || b is null) continue;
            if (t.HasGuarantee)
            {
                var receiver = t.GuarantorId == r.A ? b : a;
                var guarantor = t.GuarantorId == r.A ? a : b;
                if (!receiver.IsHuman && t.ShortWeeks >= weeks) { (acts ??= new()).Add((r, Act.ReceiverExit)); continue; }
                if (!guarantor.IsHuman && t.GuarantorShortWeeks >= weeks) { (acts ??= new()).Add((r, Act.GuarantorExit)); continue; }
            }
            if (t.EndsDay < 0) continue;
            if (a.IsHuman || b.IsHuman)
            {
                if (day >= t.EndsDay) (acts ??= new()).Add((r, Act.Expire));
                else if (!(a.IsHuman && b.IsHuman) && t.RenewalOfferedDay < t.SignedDay && day >= t.EndsDay - notice)
                    (acts ??= new()).Add((r, Act.RenewalPaper));
            }
            else if (day >= t.EndsDay) (acts ??= new()).Add((r, Act.RenewOrLapse));
        }
        if (acts is null) yield break;
        foreach (var (r, act) in acts)
        {
            if (!r.TradeAgreement || r.Trade is null) continue;
            var t = r.Trade;
            switch (act)
            {
                case Act.ReceiverExit:
                    AgreementSystem.End(c, r, AgreementSystem.TradeEnd.Cancelled, r.Other(t.GuarantorId));
                    c.Chronicle.Record(c, "trade_guarantee_unkept", r.Other(t.GuarantorId), t.GuarantorId);
                    break;
                case Act.GuarantorExit:
                    AgreementSystem.End(c, r, AgreementSystem.TradeEnd.Cancelled, t.GuarantorId);
                    break;
                case Act.Expire:
                    AgreementSystem.End(c, r, AgreementSystem.TradeEnd.Expired);
                    break;
                case Act.RenewalPaper:
                    SendRenewal(c, r);
                    break;
                case Act.RenewOrLapse:
                    RenewOrLapse(c, r);
                    break;
            }
            yield return 0;
        }
    }

    /// <summary>The same terms again, as a renewal draft.</summary>
    public static TradeTerms RenewalOf(TradeTerms t)
    {
        var again = TradeTerms.Copy(t);
        again.Renewal = true;
        return again;
    }

    private static bool BothSign(Campaign c, Nation a, Nation b, TradeTerms terms) =>
        TradeTermsValue.For(c, a, b, terms) - Ultimatum.GrudgePenalty(c, b, a) >= 0f &&
        TradeTermsValue.For(c, b, a, terms) - Ultimatum.GrudgePenalty(c, a, b) >= 0f;

    private static void RenewOrLapse(Campaign c, DiplomaticRelation r)
    {
        var a = c.NationById(r.A);
        var b = c.NationById(r.B);
        var same = RenewalOf(r.Trade);
        if (BothSign(c, a, b, same))
        {
            AgreementSystem.Sign(c, r, AgreementKind.TradeAgreement, same);
            c.Chronicle.Record(c, "trade_agreement_renewed", r.A, r.B);
            return;
        }
        foreach (var smaller in Concessions(c, a, b, r.Trade))
        {
            if (!BothSign(c, a, b, smaller)) continue;
            AgreementSystem.Sign(c, r, AgreementKind.TradeAgreement, smaller);
            DealTally.Count("trade_terms.renewed.concession");
            c.Chronicle.Record(c, "trade_agreement_renewed", r.A, r.B);
            return;
        }
        AgreementSystem.End(c, r, AgreementSystem.TradeEnd.Expired);
    }

    /// <summary>
    /// The two smaller versions tried before a term lapses: a guarantee a tenth smaller, then every
    /// "all goods" half narrowed to the three goods its importer needs most from the other - both for
    /// two years, so the pair is back at the table soon either way.
    /// </summary>
    public static IEnumerable<TradeTerms> Concessions(Campaign c, Nation a, Nation b, TradeTerms t)
    {
        var spec = TradeTermsCatalog.Spec;
        var first = RenewalOf(t);
        first.Years = 2;
        if (first.HasGuarantee) first.GuaranteeShare = MathF.Max(spec.GuaranteeMin, first.GuaranteeShare - 0.1f);
        if (TradeTerms.Sanitize(first, a.Id, b.Id).Length == 0) yield return first;
        var second = TradeTerms.Copy(first);
        var narrowed = false;
        foreach (var (importer, partner) in new[] { (a, b), (b, a) })
        {
            if (second.CutOf(importer.Id, partner.Id) <= 0f || second.GoodsOf(importer.Id, partner.Id).Length > 0) continue;
            var best = TradeTermsCatalog.Best(c, importer, partner, 3);
            if (best.Length == 0) continue;
            second.SetHalf(importer.Id, partner.Id, second.CutOf(importer.Id, partner.Id), best);
            narrowed = true;
        }
        if (narrowed && TradeTerms.Sanitize(second, a.Id, b.Id).Length == 0) yield return second;
    }

    /// <summary>
    /// The computer side of an agreement with a player sends the same terms again as a paper, if it
    /// still wants them. Sent once per term; the player's standing answer applies on delivery.
    /// </summary>
    private static void SendRenewal(Campaign c, DiplomaticRelation r)
    {
        var a = c.NationById(r.A);
        var b = c.NationById(r.B);
        var computer = a.IsHuman ? b : a;
        var player = a.IsHuman ? a : b;
        r.Trade.RenewalOfferedDay = c.ElapsedDays;
        if (!c.FeatureEnabled("deals") || c.ProvinceMap is null) return;
        var terms = RenewalOf(r.Trade);
        if (TradeTermsValue.For(c, computer, player, terms) - Ultimatum.GrudgePenalty(c, player, computer) < 0f) return;
        var deal = new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side()) { Trade = terms };
        DealNegotiations.Submit(c, computer, player, deal);
        DealTally.Count("trade_terms.renewal_paper");
    }
}

/// <summary>Trade terms in words, for the chronicle, the deal summary and the screens.</summary>
public static class TradeTermsText
{
    public static string GoodsList(string[] goods)
    {
        if (goods is not { Length: > 0 }) return Loc.Get("all goods");
        return string.Join(", ", goods.Select(id => CommodityCatalog.TryParse(id, out var g)
            ? Loc.Get(CommodityCatalog.Get(g).Name) : id));
    }

    /// <summary>One line: each importer's cut and its goods, the guarantee and the term.</summary>
    public static string Describe(Campaign c, TradeTerms t, Nation x, Nation y)
    {
        if (t is null || x is null || y is null) return "";
        var parts = new List<string>();
        foreach (var (importer, partner) in new[] { (x, y), (y, x) })
        {
            var cut = t.CutOf(importer.Id, partner.Id);
            if (cut <= 0f) continue;
            parts.Add(Loc.Format("{0}: tariff -{1} on {2}", importer.DisplayName, Fmt.Percent(cut),
                GoodsList(t.GoodsOf(importer.Id, partner.Id))));
        }
        if (t.TryGuaranteeGood(out var good))
        {
            var giver = t.GuarantorId == x.Id ? x : y;
            var receiver = giver == x ? y : x;
            parts.Add(Loc.Format("{0} guarantees {1} {2} of its spare {3}", giver.DisplayName, receiver.DisplayName,
                Fmt.Percent(t.GuaranteeShare), Loc.Get(CommodityCatalog.Get(good).Name)));
        }
        parts.Add(t.Years > 0 ? Loc.Format("for {0} year(s)", t.Years) : Loc.Get("with no end date"));
        var line = Loc.Format("Trade agreement ({0})", string.Join("; ", parts));
        return t.Renewal ? Loc.Format("Renewal: {0}", line) : line;
    }

    /// <summary>The preset's name, or "Custom terms".</summary>
    public static string PresetName(string id) =>
        TradeTermsCatalog.Find(id) is { } p ? Loc.Get(p.Name) : Loc.Get("Custom terms");
}
