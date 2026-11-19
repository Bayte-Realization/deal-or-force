using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// What being a partner at all adds to how a buyer ranks a seller, before any tariff is cut:
    /// the first term of K = 1 + this + relief x cut. With the old agreement (half the tariff off
    /// everything) K comes to exactly the 1.75 the flag used to give, so a save from before terms
    /// existed routes its goods as it did.
    /// </summary>
    public float TradeAgreementPartnerAffinity { get; set; } = 0.35f;

    /// <summary>What a whole tariff cut adds to the same ranking, per good, scaled by the cut.</summary>
    public float TradeAgreementReliefAffinity { get; set; } = 0.80f;

    /// <summary>
    /// How much of the import markup a relieved import sheds. At 1 a good bought entirely under a
    /// whole tariff cut costs the importer what it costs at home (1.06 becomes 1.00).
    /// </summary>
    public float TradeAgreementMarkupRelief { get; set; } = 1.0f;

    // The weights of one country's count of a set of terms (TradeTermsValue), in the deal table's
    // diplomatic points. Named after the lines of the count; docs/design/gameplay-2026-11-18.
    public float TradeValueExport { get; set; } = 100f;
    public float TradeValueImport { get; set; } = 60f;
    public float TradeValueShortage { get; set; } = 40f;
    public float TradeValueGuaranteeIn { get; set; } = 30f;
    public float TradeValueDuty { get; set; } = 200f;
    public float TradeValueDependence { get; set; } = 20f;
    public float TradeValueGuaranteeOut { get; set; } = 12f;
    public float TradeValueGuaranteeOutShort { get; set; } = 20f;
    public float TradeValueRelations { get; set; } = 60f;

    /// <summary>How a term's length scales what it gains and what it costs: 2, 5, 10 years and no end.</summary>
    public float[] TradeTermGain { get; set; } = { 0.7f, 1f, 1.2f, 1.25f };
    public float[] TradeTermCost { get; set; } = { 0.8f, 1f, 1.3f, 1.5f };

    /// <summary>The least of the breach memory a country pays for walking out of a term early.</summary>
    public float TradeAgreementBreakOpinionMin { get; set; } = 0.3f;

    /// <summary>What walking away from a supply guarantee costs the guarantor's name when the partner is short of the good.</summary>
    public float TradeGuaranteeBreakNotoriety { get; set; } = 3f;

    /// <summary>How many weeks in a row a guarantee may go under-delivered before its receiver walks away free.</summary>
    public int TradeGuaranteeFreeExitWeeks { get; set; } = 8;

    /// <summary>How many days before a term runs out a player is sent the renewal paper.</summary>
    public int TradeRenewalNoticeDays { get; set; } = 30;

    /// <summary>The short trial the computer offers when the preset's own length is refused; 0 offers none.</summary>
    public int TradeShortTrialYears { get; set; } = 2;
}

/// <summary>
/// What a trade agreement says, now that it says something.
///
/// It was a flag. Two countries "had a trade agreement" and the market ranked each as the other's
/// favourite seller - nothing about which goods, how much duty, for how long, or what either side
/// owed. Now the pair has two halves, one per importer, because duty is collected by whoever
/// imports: each half is a cut of that country's own tariff (0 to all of it, in quarters) on the
/// partner's goods, all of them or a list. One side may promise the other a share of its spare
/// output of one good, delivered before anybody else is served. And it may run for a number of
/// years, at the end of which it is renewed or lapses.
///
/// A and B are the relation's own: A is the lower country id. A draft on a deal table uses the
/// same orientation, so where a term was written - "our column" or "theirs" - makes no difference
/// to what it says, which is the point (the old table valued one flag two ways by column).
///
/// The relation's <see cref="DiplomaticRelation.TradeAgreement"/> flag stays and still means "in
/// force": fifteen places read it. It is true exactly when <see cref="DiplomaticRelation.Trade"/>
/// is set, and <see cref="AgreementSystem.Sign"/> is the only thing that sets either.
/// </summary>
public sealed class TradeTerms
{
    public int SignedDay;
    /// <summary>The day it runs out, or -1 for no end.</summary>
    public int EndsDay = -1;
    /// <summary>The length it was signed for; 0 is no end.</summary>
    public int Years;
    /// <summary>The share of its tariff each importer takes off the other's goods (A is the lower id).</summary>
    public float CutA, CutB;
    /// <summary>The goods each importer's cut covers, by id; empty is every good.</summary>
    public string[] GoodsA = Array.Empty<string>(), GoodsB = Array.Empty<string>();
    /// <summary>The country that promised a share of its spare output of one good, or -1.</summary>
    public int GuarantorId = -1;
    public string GuaranteeGood = "";
    public float GuaranteeShare;
    /// <summary>How fully the guarantee has been kept: a moving average of each week's delivered share, 1 at signing.</summary>
    public float Delivered = 1f;
    /// <summary>Weeks in a row the guarantee delivered under half of what it owed.</summary>
    public int ShortWeeks;
    /// <summary>Weeks in a row the guarantor was itself short of the good it promised.</summary>
    public int GuarantorShortWeeks;
    /// <summary>The preset it was drawn from, for the bench and the screen; nothing reads it as a rule.</summary>
    public string Preset = "";
    /// <summary>When the renewal paper was last sent to a player party, so it is sent once.</summary>
    public int RenewalOfferedDay = -9999;
    /// <summary>A draft only: this paper replaces the terms in force rather than signing new ones.</summary>
    public bool Renewal;
    /// <summary>
    /// The composer's working mark on a short trial of a preset (TradeTermsComposer.Candidates):
    /// asked for only when the preset's own length is refused. Never saved, sent or signed.
    /// </summary>
    [JsonIgnore] internal bool Trial;

    [JsonIgnore] public bool HasGuarantee =>
 GuarantorId >= 0 && GuaranteeShare > 0f && GuaranteeGood.Length > 0;

    /// <summary>The terms every agreement had before terms existed: half the tariff off everything, both ways, for good.</summary>
    public static TradeTerms Legacy(int signedDay = 0) => new()
    {
        SignedDay = signedDay, CutA = 0.5f, CutB = 0.5f, Preset = "ordinary"
    };

    public static TradeTerms Copy(TradeTerms t) => t is null ? null : new TradeTerms
    {
        SignedDay = t.SignedDay, EndsDay = t.EndsDay, Years = t.Years, CutA = t.CutA, CutB = t.CutB,
        GoodsA = (t.GoodsA ?? Array.Empty<string>()).ToArray(), GoodsB = (t.GoodsB ?? Array.Empty<string>()).ToArray(),
        GuarantorId = t.GuarantorId, GuaranteeGood = t.GuaranteeGood ?? "", GuaranteeShare = t.GuaranteeShare,
        Delivered = t.Delivered, ShortWeeks = t.ShortWeeks, GuarantorShortWeeks = t.GuarantorShortWeeks,
        Preset = t.Preset ?? "", RenewalOfferedDay = t.RenewalOfferedDay, Renewal = t.Renewal
    };

    /// <summary>The terms written on a relation, or the old ones for a flag that was never given any.</summary>
    public static TradeTerms Of(DiplomaticRelation relation) =>
        relation is null ? null : relation.Trade ?? (relation.TradeAgreement ? Legacy() : null);

    public float CutOf(int importerId, int partnerId) => importerId < partnerId ? CutA : CutB;
    public string[] GoodsOf(int importerId, int partnerId) =>
        (importerId < partnerId ? GoodsA : GoodsB) ?? Array.Empty<string>();

    public void SetHalf(int importerId, int partnerId, float cut, string[] goods)
    {
        if (importerId < partnerId) { CutA = cut; GoodsA = goods ?? Array.Empty<string>(); }
        else { CutB = cut; GoodsB = goods ?? Array.Empty<string>(); }
    }

    /// <summary>Whether this importer's cut covers this good.</summary>
    public bool Covers(int importerId, int partnerId, Commodity good)
    {
        var goods = GoodsOf(importerId, partnerId);
        if (goods.Length == 0) return true;
        var id = CommodityCatalog.IdOf(good);
        foreach (var g in goods) if (string.Equals(g, id, StringComparison.OrdinalIgnoreCase)) return true;
        return false;
    }

    /// <summary>The cut this importer gives the partner's good: its half, or nothing for a good outside its list.</summary>
    public float CutFor(int importerId, int partnerId, Commodity good) =>
        Covers(importerId, partnerId, good) ? CutOf(importerId, partnerId) : 0f;

    public bool TryGuaranteeGood(out Commodity good)
    {
        good = default;
        return HasGuarantee && CommodityCatalog.TryParse(GuaranteeGood, out good);
    }

    /// <summary>Equal terms, the bookkeeping (dates, delivery, preset name) left out.</summary>
    public static bool SameTerms(TradeTerms x, TradeTerms y)
    {
        if (x is null || y is null) return x is null && y is null;
        static bool Same(string[] a, string[] b) =>
            (a ?? Array.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal)
            .SequenceEqual((b ?? Array.Empty<string>()).OrderBy(s => s, StringComparer.Ordinal), StringComparer.OrdinalIgnoreCase);
        return x.Years == y.Years && x.CutA == y.CutA && x.CutB == y.CutB && Same(x.GoodsA, y.GoodsA) &&
               Same(x.GoodsB, y.GoodsB) && x.GuarantorId == y.GuarantorId &&
               string.Equals(x.GuaranteeGood ?? "", y.GuaranteeGood ?? "", StringComparison.OrdinalIgnoreCase) &&
               x.GuaranteeShare == y.GuaranteeShare && x.Renewal == y.Renewal;
    }

    /// <summary>
    /// A draft cleaned to what the catalogue allows: cuts to the step, years to the list, goods to
    /// known ids, the guarantee to the range and to one of the two parties. Returns the reason a
    /// draft cannot be cleaned into anything, or an empty string.
    /// </summary>
    public static string Sanitize(TradeTerms t, int aId, int bId)
    {
        if (t is null) return Loc.Get("Invalid target.");
        var spec = TradeTermsCatalog.Spec;
        if (!float.IsFinite(t.CutA) || !float.IsFinite(t.CutB) || !float.IsFinite(t.GuaranteeShare))
            return Loc.Get("These trade terms cannot be written.");
        float Step(float cut) => Math.Clamp(MathF.Round(Math.Clamp(cut, 0f, 1f) / spec.CutStep) * spec.CutStep, 0f, 1f);
        t.CutA = Step(t.CutA);
        t.CutB = Step(t.CutB);
        if (!spec.Years.Contains(t.Years)) return Loc.Get("That term length is not one a trade agreement can have.");
        static string[] Goods(string[] goods)
        {
            if (goods is null || goods.Length == 0) return Array.Empty<string>();
            var clean = new List<string>(goods.Length);
            foreach (var g in goods)
                if (CommodityCatalog.TryParse(g, out var c))
                {
                    var id = CommodityCatalog.IdOf(c);
                    if (!clean.Contains(id, StringComparer.OrdinalIgnoreCase)) clean.Add(id);
                }
            clean.Sort(StringComparer.Ordinal);
            return clean.Count == 0 ? Array.Empty<string>() : clean.ToArray();
        }
        if ((t.GoodsA?.Length ?? 0) > 64 || (t.GoodsB?.Length ?? 0) > 64) return Loc.Get("These trade terms cannot be written.");
        var hadA = (t.GoodsA?.Length ?? 0) > 0;
        var hadB = (t.GoodsB?.Length ?? 0) > 0;
        t.GoodsA = Goods(t.GoodsA);
        t.GoodsB = Goods(t.GoodsB);
        // A list that named only goods nobody has heard of is not "every good".
        if ((hadA && t.GoodsA.Length == 0) || (hadB && t.GoodsB.Length == 0))
            return Loc.Get("A trade agreement can only name goods the market knows.");
        if (t.GuarantorId >= 0 || t.GuaranteeShare > 0f || !string.IsNullOrEmpty(t.GuaranteeGood))
        {
            if (t.GuarantorId != aId && t.GuarantorId != bId) return Loc.Get("Only one of the two countries can guarantee supplies.");
            if (!CommodityCatalog.TryParse(t.GuaranteeGood ?? "", out var good)) return Loc.Get("A supply guarantee must name a good.");
            t.GuaranteeGood = CommodityCatalog.IdOf(good);
            var share = MathF.Round(t.GuaranteeShare / spec.GuaranteeStep) * spec.GuaranteeStep;
            if (share < spec.GuaranteeMin - 1e-4f || share > spec.GuaranteeMax + 1e-4f)
                return Loc.Format("A supply guarantee promises between {0} and {1} of the spare output.",
                    Fmt.Percent(spec.GuaranteeMin), Fmt.Percent(spec.GuaranteeMax));
            t.GuaranteeShare = Math.Clamp(share, spec.GuaranteeMin, spec.GuaranteeMax);
        }
        else
        {
            t.GuarantorId = -1; t.GuaranteeGood = ""; t.GuaranteeShare = 0f;
        }
        // Terms that change nothing for either importer and promise nothing are not an agreement.
        if (t.CutA <= 0f && t.CutB <= 0f && !t.HasGuarantee) return Loc.Get("The agreement must cut a tariff or guarantee a supply.");
        if (t.Preset is { Length: > 64 }) t.Preset = t.Preset[..64];
        return "";
    }
}

public sealed partial class DiplomaticRelation
{
    /// <summary>What the trade agreement in force says, or null when there is none. See <see cref="TradeTerms"/>.</summary>
    public TradeTerms Trade;

    /// <summary>
    /// The terms of a trade agreement waiting on a player's table (<see cref="AgreementOfferedBy"/>),
    /// or null for the old offer that named none - read as the ordinary preset.
    /// </summary>
    public TradeTerms AgreementOfferedTrade;
}

/// <summary>One named set of trade terms, from the proposer's seat (common/deals "trade_presets").</summary>
public sealed record TradePreset(string Id, string Name, int Order, float OurCut, string OurScope,
    float TheirCut, string TheirScope, string Guarantee, float GuaranteeShare, int Years);

/// <summary>What a trade agreement may say (common/deal_terms) and the named presets (common/deals).</summary>
public static class TradeTermsCatalog
{
    public sealed class TermSpec
    {
        public int[] Years = { 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 0 };
        public float CutStep = 0.25f;
        public float GuaranteeMin = 0.05f, GuaranteeMax = 0.5f, GuaranteeStep = 0.05f;
        public string ExitOpinion = "broke_trade_agreement";
        public float FreeExitBelow = 0.5f;
        // The exit's floor and its free weeks, when the term catalogue names them; otherwise the
        // defines (TradeAgreementBreakOpinionMin, TradeGuaranteeFreeExitWeeks) decide. A mod that
        // edits the term reads its change where it made it.
        public float? MinimumScale;
        public int? FreeExitWeeks;
        public bool Shared = true;
    }

    /// <summary>The least share of the breach memory an early exit pays.</summary>
    public static float BreakOpinionMin(Defines d) => M.Clamp01(Spec.MinimumScale ?? d.TradeAgreementBreakOpinionMin);

    /// <summary>Weeks in a row a guarantee may go under-delivered before its receiver leaves for free.</summary>
    public static int FreeExitWeeks(Defines d) => Math.Max(1, Spec.FreeExitWeeks ?? d.TradeGuaranteeFreeExitWeeks);

    public static readonly string[] Scopes = { "all", "best_for_us", "best_for_them", "top3_theirs", "guarantee_good" };

    public static TermSpec Spec { get; private set; } = new();
    private static readonly Dictionary<string, TradePreset> Presets = new(StringComparer.Ordinal);
    private static TradePreset[] _ordered = Array.Empty<TradePreset>();

    static TradeTermsCatalog() => ResetCatalog();

    public static IReadOnlyList<TradePreset> All => _ordered;
    public static TradePreset Find(string id) => id is null ? null : Presets.GetValueOrDefault(id);

    public static void ResetCatalog()
    {
        Spec = new TermSpec();
        Presets.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "deal_terms"))
            using (document)
            {
                var errors = new List<string>();
                MergeTerms(document.RootElement, errors);
                foreach (var error in errors) DiagnosticLogs.Write("errors", "DEAL_TERMS " + error);
            }
        foreach (var document in GameDataLoader.DataDocuments("common", "deals"))
            using (document)
            {
                var errors = new List<string>();
                MergePresets(document.RootElement, errors);
                foreach (var error in errors) DiagnosticLogs.Write("errors", "TRADE_PRESET " + error);
            }
        Order();
    }

    public static void MergeTerms(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("trade_agreement", out var t)) return;
        if (t.ValueKind != JsonValueKind.Object) { errors?.Add("deal_terms/trade_agreement: expected an object"); return; }
        var spec = Spec;
        try
        {
            if (t.TryGetProperty("shared", out var shared)) spec.Shared = shared.ValueKind == JsonValueKind.True;
            if (t.TryGetProperty("years", out var years))
            {
                var list = years.EnumerateArray().Select(y => y.GetInt32()).Distinct().ToArray();
                if (list.Length == 0 || list.Any(y => y < 0 || y > 50)) throw new FormatException("years must be whole numbers from 0 to 50");
                spec.Years = list;
            }
            if (t.TryGetProperty("cut_step", out var step))
            {
                var s = step.GetSingle();
                if (!(s > 0f && s <= 1f)) throw new FormatException("cut_step must be above 0 and at most 1");
                spec.CutStep = s;
            }
            if (t.TryGetProperty("guarantee", out var g))
            {
                if (g.TryGetProperty("min", out var min)) spec.GuaranteeMin = min.GetSingle();
                if (g.TryGetProperty("max", out var max)) spec.GuaranteeMax = max.GetSingle();
                if (g.TryGetProperty("step", out var gs)) spec.GuaranteeStep = gs.GetSingle();
                if (!(spec.GuaranteeMin > 0f && spec.GuaranteeMax <= 1f && spec.GuaranteeMin <= spec.GuaranteeMax && spec.GuaranteeStep > 0f))
                    throw new FormatException("guarantee needs 0 < min <= max <= 1 and a step above 0");
            }
            if (t.TryGetProperty("exit", out var exit))
            {
                if (exit.TryGetProperty("opinion", out var op) && op.ValueKind == JsonValueKind.String) spec.ExitOpinion = op.GetString() ?? spec.ExitOpinion;
                if (exit.TryGetProperty("free_exit_below", out var below)) spec.FreeExitBelow = Math.Clamp(below.GetSingle(), 0f, 1f);
                if (exit.TryGetProperty("minimum_scale", out var floor)) spec.MinimumScale = Math.Clamp(floor.GetSingle(), 0f, 1f);
                if (exit.TryGetProperty("free_exit_weeks", out var free)) spec.FreeExitWeeks = Math.Clamp(free.GetInt32(), 1, 520);
            }
        }
        catch (Exception e) when (e is FormatException or InvalidOperationException)
        {
            errors?.Add("deal_terms/trade_agreement: " + e.Message);
        }
    }

    public static void MergePresets(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("trade_presets", out var list)) return;
        if (list.ValueKind != JsonValueKind.Array) { errors?.Add("deals/trade_presets: expected an array"); return; }
        foreach (var entry in list.EnumerateArray())
        {
            var id = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var raw) &&
                     raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
            try
            {
                if (!MechanicFeatures.ValidId(id)) throw new FormatException("every preset needs a plain id");
                if (GameDataLoader.IsRemoved(entry, $"common/deals:{id}"))
                {
                    Presets.Remove(id);
                    continue;
                }
                var current = Find(id);
                string Text(string key, string fallback) =>
                    entry.TryGetProperty(key, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : fallback;
                float Number(JsonElement e, string key, float fallback) =>
                    e.ValueKind == JsonValueKind.Object && e.TryGetProperty(key, out var v) && v.TryGetSingle(out var f) ? f : fallback;
                string Scope(JsonElement e, string fallback)
                {
                    var s = e.ValueKind == JsonValueKind.Object && e.TryGetProperty("scope", out var v) && v.ValueKind == JsonValueKind.String
                        ? v.GetString() : fallback;
                    if (Array.IndexOf(Scopes, s) < 0) throw new FormatException($"scope '{s}' is not one of {string.Join(", ", Scopes)}");
                    return s;
                }
                var ours = entry.TryGetProperty("ours", out var o) ? o : default;
                var theirs = entry.TryGetProperty("theirs", out var th) ? th : default;
                var guarantee = entry.TryGetProperty("guarantee", out var gu) ? gu : default;
                var to = guarantee.ValueKind == JsonValueKind.Object && guarantee.TryGetProperty("to", out var gt) &&
                         gt.ValueKind == JsonValueKind.String ? gt.GetString() : current?.Guarantee ?? "none";
                if (to is not ("us" or "them" or "none")) throw new FormatException("guarantee.to must be us, them or none");
                var years = entry.TryGetProperty("years", out var y) && y.TryGetInt32(out var yy) ? yy : current?.Years ?? 0;
                var preset = new TradePreset(id, Text("name", current?.Name ?? id),
                    entry.TryGetProperty("order", out var ord) && ord.TryGetInt32(out var oi) ? oi : current?.Order ?? 0,
                    Math.Clamp(Number(ours, "cut", current?.OurCut ?? 0f), 0f, 1f), Scope(ours, current?.OurScope ?? "all"),
                    Math.Clamp(Number(theirs, "cut", current?.TheirCut ?? 0f), 0f, 1f), Scope(theirs, current?.TheirScope ?? "all"),
                    to, Math.Clamp(Number(guarantee, "share", current?.GuaranteeShare ?? 0f), 0f, 1f), years);
                if (preset.Years < 0 || preset.Years > 50) throw new FormatException("years must be from 0 to 50");
                Presets[id] = preset;
            }
            catch (Exception e) when (e is FormatException or InvalidOperationException)
            {
                errors?.Add($"deals/trade_presets/{id ?? "?"}: {e.Message}");
            }
        }
        Order();
    }

    private static void Order() =>
        _ordered = Presets.Values.OrderBy(p => p.Order <= 0 ? int.MaxValue : p.Order).ThenBy(p => p.Id, StringComparer.Ordinal).ToArray();

    /// <summary>The presets the computer composes from, in the order it tries them.</summary>
    public static IEnumerable<TradePreset> Composed => _ordered.Where(p => p.Order > 0);

    /// <summary>
    /// A preset made into terms between a proposer and a partner. The scopes name goods from the
    /// proposer's seat: "best_for_us" is the partner's good the proposer needs most, "best_for_them"
    /// the proposer's good the partner needs most. Null when a scope finds no good, or the guarantee
    /// has nothing to promise: the preset does not fit this pair.
    /// </summary>
    public static TradeTerms Build(Campaign c, TradePreset preset, Nation proposer, Nation partner,
        Commodity? good = null, float? share = null, int? years = null)
    {
        if (preset is null || proposer is null || partner is null) return null;
        var terms = new TradeTerms { Preset = preset.Id, Years = years ?? preset.Years };
        string[] Scope(string scope)
        {
            switch (scope)
            {
                case "all": return Array.Empty<string>();
                case "best_for_us":
                    // A hedge names the good it is short of; "best for us" is then that good.
                    if (good is { } named) return Spare(partner, named) > 0f ? new[] { CommodityCatalog.IdOf(named) } : null;
                    return Best(c, proposer, partner, 1) is { Length: > 0 } a ? a : null;
                case "best_for_them":
                    return Best(c, partner, proposer, 1) is { Length: > 0 } b ? b : null;
                case "top3_theirs":
                    return Best(c, proposer, partner, 3) is { Length: > 0 } d ? d : null;
                case "guarantee_good":
                    var g = good ?? GuaranteeCandidate(c, proposer, partner);
                    return g is null ? null : new[] { CommodityCatalog.IdOf(g.Value) };
                default: return null;
            }
        }
        if (preset.OurCut > 0f)
        {
            var ours = Scope(preset.OurScope);
            if (ours is null) return null;
            terms.SetHalf(proposer.Id, partner.Id, preset.OurCut, ours);
        }
        if (preset.TheirCut > 0f)
        {
            var theirs = Scope(preset.TheirScope);
            if (theirs is null) return null;
            terms.SetHalf(partner.Id, proposer.Id, preset.TheirCut, theirs);
        }
        if (preset.Guarantee is "us" or "them" && preset.GuaranteeShare > 0f)
        {
            var receiver = preset.Guarantee == "us" ? proposer : partner;
            var giver = preset.Guarantee == "us" ? partner : proposer;
            var g = good ?? GuaranteeCandidate(c, receiver, giver);
            // A guarantee is only written for a good the receiver lacks and the giver can spare.
            if (g is null || Shortfall(receiver, g.Value) <= 0f || Spare(giver, g.Value) <= 0f) return null;
            terms.GuarantorId = giver.Id;
            terms.GuaranteeGood = CommodityCatalog.IdOf(g.Value);
            terms.GuaranteeShare = share ?? preset.GuaranteeShare;
        }
        return TradeTerms.Sanitize(terms, Math.Min(proposer.Id, partner.Id), Math.Max(proposer.Id, partner.Id)).Length == 0
            ? terms : null;
    }

    /// <summary>
    /// The unequal treaty of a lost war or a yielded ultimatum: the loser takes its whole tariff off
    /// the winner's goods and gets nothing back (preset "concession"), for the given years.
    /// </summary>
    public static TradeTerms Concession(Campaign c, Nation winner, Nation loser, int years)
    {
        if (winner is null || loser is null) return TradeTerms.Legacy();
        var terms = Build(c, Find("concession"), winner, loser, years: years);
        if (terms is not null) return terms;
        terms = new TradeTerms { Preset = "concession", Years = years };
        terms.SetHalf(loser.Id, winner.Id, 1f, Array.Empty<string>());
        return TradeTerms.Sanitize(terms, Math.Min(winner.Id, loser.Id), Math.Max(winner.Id, loser.Id)).Length == 0
            ? terms : TradeTerms.Legacy();
    }

    /// <summary>The ordinary terms by id, or the old ones when the data lost the preset.</summary>
    public static TradeTerms Ordinary(Campaign c, Nation proposer, Nation partner) =>
        Build(c, Find("ordinary"), proposer, partner) ?? TradeTerms.Legacy();

    /// <summary>
    /// The goods the buyer most needs from the seller, best first: what the buyer lacks against what
    /// the seller can spare, at base price. "Best good" in the presets and in the deal window.
    /// </summary>
    public static string[] Best(Campaign c, Nation buyer, Nation seller, int count)
    {
        var scored = new List<(string Id, float Score)>();
        foreach (var good in CommodityCatalog.All)
        {
            var score = MathF.Min(Shortfall(buyer, good), Spare(seller, good)) * CommodityCatalog.Get(good).BasePrice;
            if (score > 0.0001f) scored.Add((CommodityCatalog.IdOf(good), score));
        }
        return scored.OrderByDescending(s => s.Score).ThenBy(s => s.Id, StringComparer.Ordinal)
            .Take(Math.Max(0, count)).Select(s => s.Id).ToArray();
    }

    /// <summary>The good a receiver would want guaranteed: the one it is shortest of that the giver can spare.</summary>
    public static Commodity? GuaranteeCandidate(Campaign c, Nation receiver, Nation giver)
    {
        Commodity? best = null;
        var bestScore = 0f;
        foreach (var good in CommodityCatalog.All)
        {
            if (!receiver.Market.TryGetValue(good, out var q) || q.Satisfaction >= 0.985f && q.Demand <= q.Production) continue;
            var score = MathF.Min(Shortfall(receiver, good), Spare(giver, good)) * CommodityCatalog.Get(good).BasePrice *
                        (0.5f + CommodityCatalog.Get(good).EssentialWeight);
            if (score > bestScore + 1e-6f) { best = good; bestScore = score; }
        }
        return best;
    }

    /// <summary>What a country lacks of a good each week, before anybody sells it any.</summary>
    public static float Shortfall(Nation nation, Commodity good) =>
        nation?.Market.TryGetValue(good, out var q) == true && float.IsFinite(q.Demand - q.Production)
            ? MathF.Max(0f, q.Demand - q.Production) : 0f;

    /// <summary>What a country can sell of a good: the same capacity the market's seller list uses.</summary>
    public static float Spare(Nation nation, Commodity good)
    {
        if (nation?.Market.TryGetValue(good, out var q) != true) return 0f;
        var capacity = (q.Production - q.Demand) * TradePolicyRules.ExportAccess(nation.TradePolicy) *
                       (1f - M.Clamp01(TradeDesk.ForGood(nation, good, TradeDesk.ExportReserve)));
        return float.IsFinite(capacity) ? MathF.Max(0f, capacity) : 0f;
    }

    /// <summary>The scale a term of this many years puts on gains (or costs).</summary>
    public static float TermFactor(Defines d, int years, bool cost)
    {
        var table = cost ? d.TradeTermCost : d.TradeTermGain;
        if (table is not { Length: 4 } || table.Any(v => !float.IsFinite(v))) table = cost ? new[] { .8f, 1f, 1.3f, 1.5f } : new[] { .7f, 1f, 1.2f, 1.25f };
        if (years <= 0) return table[3];
        if (years <= 5) return table[0] + (table[1] - table[0]) * (years - 2) / 3f;
        return MathF.Min(table[2], table[1] + (table[2] - table[1]) * (years - 5) / 5f);
    }
}
