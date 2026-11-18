using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How many diplomatic points in its own favour, by the game's own count
    /// (<see cref="Ultimatum.DealAssessment.ToValue"/>), a deal must show before a
    /// "sign the beneficial ones" rule signs it for a player. Above zero on purpose: a deal that
    /// only breaks even is a question for the player, not for the ministry.
    /// </summary>
    public float AutoAcceptMargin { get; set; } = 5f;

    /// <summary>How many waiting papers the standing rules may close in one day (signed or declined).</summary>
    public int AutoAnswerDailyBatch { get; set; } = 32;

    /// <summary>How many decisions taken under a standing rule each country keeps to show its player.</summary>
    public int AutoAnswerLogLimit { get; set; } = 50;

    /// <summary>
    /// How long a country whose agreement offer lapsed unanswered on a player's table waits
    /// before asking that player again. Without it the same offer came back every thirty days
    /// for ever. Shorter than a refusal (AgreementRefusalDays): silence is not a no.
    /// </summary>
    public int AgreementLapseCooldownDays { get; set; } = 90;
}

/// <summary>What a player's foreign ministry does with one kind of incoming offer.</summary>
public enum AutoAnswerMode { Ask = 0, Beneficial = 1, Decline = 2 }

/// <summary>What <see cref="DealAnswer.Decide(Campaign, Nation, Nation, Ultimatum.Deal, Ultimatum.DealAssessment)"/> answered.</summary>
public enum DealVerdict { Wait, Accept, Decline }

public sealed partial class Nation
{
    /// <summary>
    /// This country's standing answers to incoming offers, by answer kind
    /// (common/deals). A kind that is not here is answered by its default, which is "ask".
    /// Changed only by <see cref="SetAutoAnswerCommand"/>. Every country has it; only a country
    /// a player answers for reads it - a government always signs what is worth it to it.
    /// </summary>
    public readonly Dictionary<string, int> AutoAnswer = new(StringComparer.Ordinal);

    /// <summary>The latest offers the standing rules answered for this country, newest last.</summary>
    public readonly List<AutoAnswerNote> AutoAnswerLog = new();
}

/// <summary>One answer given by a standing rule, kept for the player to read.</summary>
public sealed class AutoAnswerNote
{
    public int Day, ProposerId;
    /// <summary>The deal paper answered, or 0 for an agreement offer.</summary>
    public int OfferId;
    /// <summary>The agreement answered (<see cref="AgreementKind"/>), or -1 for a deal paper.</summary>
    public int Agreement = -1;
    public string Kind = "";
    public AutoAnswerMode Mode;
    public bool Accepted;
    /// <summary>Whether <see cref="Benefit"/> was counted (a "decline" rule answers without counting).</summary>
    public bool Counted;
    /// <summary>The deal's worth to the answering country by the game's own count when <see cref="Counted"/>.</summary>
    public float Benefit;
}

/// <summary>One kind of incoming offer a standing rule can be set for (common/deals).</summary>
public sealed record DealAnswerKind(string Id, int Priority, string[] RecipientGivesAny, string[] ProposerGivesAny,
    string[] Only, bool RecipientGivesNone, AutoAnswerMode[] Modes, AutoAnswerMode Default)
{
    public bool Allows(AutoAnswerMode mode) => Array.IndexOf(Modes, mode) >= 0;
}

/// <summary>
/// The answer kinds, read from <c>game/common/deals</c>. A deal belongs to the matching kind with
/// the highest priority, judged by its terms and never by which template sent it. Land can never be
/// given by a rule: a kind that might match a deal in which the answering country gives up a
/// region is refused "beneficial" when it loads, and <see cref="DealAnswer.Decide(Campaign, Nation, Nation, Ultimatum.Deal, Ultimatum.DealAssessment)"/>
/// checks it again on the terms themselves.
/// </summary>
public static class DealAnswerKinds
{
    /// <summary>Term ids a match may name. The last two are terms the table does not have yet
    /// (a lease, a stream of payments); naming them is allowed and simply never matches today.</summary>
    public static readonly string[] Terms =
        { "regions", "release_regions", "cash", "lift_embargo", "non_aggression", "trade_agreement", "lease_region", "cash_stream" };

    public static readonly string[] LandTerms = { "regions", "release_regions", "lease_region" };

    private static readonly Dictionary<string, DealAnswerKind> Entries = new(StringComparer.Ordinal);
    private static DealAnswerKind[] _ordered = Array.Empty<DealAnswerKind>();

    static DealAnswerKinds() => ResetCatalog();

    /// <summary>Highest priority first, then by id: the order a deal is matched in and the order the inbox lists them.</summary>
    public static IReadOnlyList<DealAnswerKind> All => _ordered;

    public static DealAnswerKind Find(string id) => id is not null ? Entries.GetValueOrDefault(id) : null;

    public static void ResetCatalog()
    {
        Entries.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "deals"))
            using (document)
            {
                var errors = new List<string>();
                Merge(document.RootElement, errors);
                foreach (var error in errors) DiagnosticLogs.Write("errors", "DEAL_ANSWER_KIND " + error);
            }
        Order();
    }

    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("deals: expected an object"); return; }
        if (!root.TryGetProperty("answer_kinds", out var kinds)) return;
        if (kinds.ValueKind != JsonValueKind.Array) { errors?.Add("deals/answer_kinds: expected an array"); return; }
        foreach (var entry in kinds.EnumerateArray())
        {
            var id = entry.ValueKind == JsonValueKind.Object && entry.TryGetProperty("id", out var raw) &&
                raw.ValueKind == JsonValueKind.String ? raw.GetString() : null;
            try
            {
                if (!MechanicFeatures.ValidId(id)) throw new FormatException("every answer kind needs a plain id");
                var current = Find(id);
                if (entry.TryGetProperty("remove", out var remove) && remove.ValueKind == JsonValueKind.True)
                {
                    Entries.Remove(id);
                    continue;
                }
                var priority = current?.Priority ?? 0;
                if (entry.TryGetProperty("priority", out var p))
                {
                    if (!p.TryGetInt32(out priority) || priority < -1000 || priority > 1000)
                        throw new FormatException("priority must be a whole number from -1000 to 1000");
                }
                string[] TermList(JsonElement match, string key, string[] fallback)
                {
                    if (!match.TryGetProperty(key, out var list)) return fallback;
                    if (list.ValueKind != JsonValueKind.Array || list.EnumerateArray().Any(t => t.ValueKind != JsonValueKind.String))
                        throw new FormatException(key + " must be a list of term ids");
                    var terms = list.EnumerateArray().Select(t => t.GetString()).ToArray();
                    var unknown = terms.FirstOrDefault(t => Array.IndexOf(Terms, t) < 0);
                    if (unknown is not null) throw new FormatException($"{key} names '{unknown}', which is not a deal term");
                    return terms;
                }
                var recipientGives = current?.RecipientGivesAny ?? Array.Empty<string>();
                var proposerGives = current?.ProposerGivesAny ?? Array.Empty<string>();
                var only = current?.Only ?? Array.Empty<string>();
                var givesNone = current?.RecipientGivesNone ?? false;
                if (entry.TryGetProperty("match", out var match))
                {
                    if (match.ValueKind != JsonValueKind.Object) throw new FormatException("match must be an object");
                    recipientGives = TermList(match, "recipient_gives_any", Array.Empty<string>());
                    proposerGives = TermList(match, "proposer_gives_any", Array.Empty<string>());
                    only = TermList(match, "only", Array.Empty<string>());
                    givesNone = match.TryGetProperty("recipient_gives_none", out var none) && none.ValueKind == JsonValueKind.True;
                }
                if (recipientGives.Length == 0 && proposerGives.Length == 0 && only.Length == 0 && !givesNone)
                    throw new FormatException("match names no condition, so it would never match anything");
                var modes = current?.Modes ?? new[] { AutoAnswerMode.Ask, AutoAnswerMode.Decline };
                if (entry.TryGetProperty("modes", out var modeList))
                {
                    if (modeList.ValueKind != JsonValueKind.Array) throw new FormatException("modes must be a list");
                    modes = modeList.EnumerateArray().Select(m => ParseMode(m.ValueKind == JsonValueKind.String ? m.GetString() : null))
                        .Distinct().OrderBy(m => m).ToArray();
                }
                if (!modes.Contains(AutoAnswerMode.Ask)) modes = modes.Prepend(AutoAnswerMode.Ask).ToArray();
                // The one rule no data file may lift: a kind that can match a deal in which the
                // answering country gives up land never signs on its own. A gift-shaped match cannot
                // contain land (the recipient gives nothing), and an "only" match can only if it names
                // a land term; anything else that names land for the recipient is land.
                var mayGiveLand = recipientGives.Any(t => Array.IndexOf(LandTerms, t) >= 0) ||
                    only.Any(t => Array.IndexOf(LandTerms, t) >= 0) ||
                    (!givesNone && only.Length == 0 && recipientGives.Length == 0);
                if (mayGiveLand && modes.Contains(AutoAnswerMode.Beneficial))
                {
                    errors?.Add($"deals/{id}: 'beneficial' is not allowed for a kind in which the answering country may give up land; removed");
                    modes = modes.Where(m => m != AutoAnswerMode.Beneficial).ToArray();
                }
                var fallbackDefault = current?.Default ?? AutoAnswerMode.Ask;
                var defaultMode = entry.TryGetProperty("default", out var d)
                    ? ParseMode(d.ValueKind == JsonValueKind.String ? d.GetString() : null) : fallbackDefault;
                if (!modes.Contains(defaultMode))
                {
                    errors?.Add($"deals/{id}: default '{defaultMode}' is not one of its modes; 'ask' used");
                    defaultMode = AutoAnswerMode.Ask;
                }
                Entries[id] = new DealAnswerKind(id, priority, recipientGives, proposerGives, only, givesNone, modes, defaultMode);
            }
            catch (Exception e) { errors?.Add($"deals/{id ?? "?"}: {e.Message}"); }
        }
        Order();
    }

    private static void Order() =>
        _ordered = Entries.Values.OrderByDescending(k => k.Priority).ThenBy(k => k.Id, StringComparer.Ordinal).ToArray();

    public static AutoAnswerMode ParseMode(string text) => text?.Trim().ToLowerInvariant() switch
    {
        "ask" => AutoAnswerMode.Ask,
        "beneficial" => AutoAnswerMode.Beneficial,
        "decline" => AutoAnswerMode.Decline,
        _ => throw new FormatException($"'{text}' is not a mode (ask, beneficial, decline)")
    };

    public static string ModeId(AutoAnswerMode mode) => mode switch
    {
        AutoAnswerMode.Beneficial => "beneficial",
        AutoAnswerMode.Decline => "decline",
        _ => "ask"
    };

    /// <summary>The term ids one side of the table puts down.</summary>
    public static HashSet<string> TermsOf(Ultimatum.Side side)
    {
        var terms = new HashSet<string>(StringComparer.Ordinal);
        if (side is null) return terms;
        if ((side.Regions?.Length ?? 0) > 0) terms.Add(side.ReleaseRegions ? "release_regions" : "regions");
        if (side.Cash > 0f) terms.Add("cash");
        if (side.LiftEmbargo) terms.Add("lift_embargo");
        if (side.NonAggression) terms.Add("non_aggression");
        if (side.TradeAgreement) terms.Add("trade_agreement");
        return terms;
    }

    /// <summary>
    /// The kind a deal belongs to, for the country it is addressed to: <c>deal.To</c> is what that
    /// country is asked to give, <c>deal.From</c> what it is offered. Null when no kind matches -
    /// such a deal is always a question for the player.
    /// </summary>
    public static DealAnswerKind Classify(Ultimatum.Deal deal)
    {
        if (deal?.From is null || deal.To is null) return null;
        var gives = TermsOf(deal.To);
        var gets = TermsOf(deal.From);
        // A trade agreement is the pair's term: each country signs up to it, so it is on both halves.
        if (Ultimatum.TradeOf(deal) is not null) { gives.Add("trade_agreement"); gets.Add("trade_agreement"); }
        foreach (var kind in _ordered)
            if (Matches(kind, gives, gets)) return kind;
        return null;
    }

    private static bool Matches(DealAnswerKind kind, HashSet<string> gives, HashSet<string> gets)
    {
        if (kind.RecipientGivesNone && gives.Count > 0) return false;
        if (kind.RecipientGivesAny.Length > 0 && !kind.RecipientGivesAny.Any(gives.Contains)) return false;
        if (kind.ProposerGivesAny.Length > 0 && !kind.ProposerGivesAny.Any(gets.Contains)) return false;
        if (kind.Only.Length > 0)
        {
            if (gives.Count + gets.Count == 0) return false;
            if (gives.Concat(gets).Any(t => Array.IndexOf(kind.Only, t) < 0)) return false;
        }
        return true;
    }
}

/// <summary>A decision, with what it was made from - for the inbox and for the paper's history.</summary>
public sealed record DealDecision(DealVerdict Verdict, DealAnswerKind Kind, AutoAnswerMode Mode,
    Ultimatum.DealAssessment Assessment, float Margin, string Reason)
{
    /// <summary>The deal's worth to the answering country by the game's own count, or NaN.</summary>
    public float Benefit => Assessment is { Valid: true } ? Assessment.ToValue : float.NaN;
}

/// <summary>
/// The one place where an addressed country answers a deal.
///
/// A government answers by the assessment the deal table already shows: it signs what is worth
/// at least nothing to it (<see cref="GovernmentAccepts"/>) - exactly the rule it always used, so a
/// world without players runs as it did. A country a player answers for asks its player, unless
/// the player has left a standing answer for that kind of offer: decline it, or sign it when the
/// game's own count puts it at least <see cref="Defines.AutoAcceptMargin"/> points in the player's
/// favour. The count is the same <see cref="Ultimatum.AssessNegotiatedDeal"/> the inbox shows on
/// every offer, so a player always knows in advance what the rule would do. Land is never given
/// by a rule, whatever the data says; ultimatums and peace never come here.
/// </summary>
public static class DealAnswer
{
    /// <summary>A government's own answer: the deal is valid and worth at least nothing to it.</summary>
    public static bool GovernmentAccepts(Ultimatum.DealAssessment assessment) => assessment?.Acceptable == true;

    public static AutoAnswerMode ModeFor(Nation nation, DealAnswerKind kind)
    {
        if (nation is null || kind is null) return AutoAnswerMode.Ask;
        if (!nation.AutoAnswer.TryGetValue(kind.Id, out var raw)) return kind.Default;
        var mode = (AutoAnswerMode)raw;
        return Enum.IsDefined(mode) && kind.Allows(mode) ? mode : AutoAnswerMode.Ask;
    }

    /// <summary>Whether this country has told its ministry to answer anything by itself.</summary>
    public static bool HasStandingRules(Nation nation) =>
        nation is not null && (nation.AutoAnswer.Count > 0 || DealAnswerKinds.All.Any(k => k.Default != AutoAnswerMode.Ask));

    /// <summary>Whether the addressed country gives up or releases a region under these terms.</summary>
    public static bool RecipientGivesLand(Ultimatum.Deal deal) => (deal?.To?.Regions?.Length ?? 0) > 0;

    public static float Margin(Campaign c) =>
        c?.Defines is { } d && float.IsFinite(d.AutoAcceptMargin) ? Math.Clamp(d.AutoAcceptMargin, 0f, 1000f) : 5f;

    public static DealDecision Decide(Campaign c, Nation recipient, DealOffer offer) =>
        Decide(c, recipient, c?.NationById(offer?.FromId ?? -1), offer?.Terms);

    /// <param name="known">The assessment the caller already made, in the recipient's seat; computed when null.</param>
    public static DealDecision Decide(Campaign c, Nation recipient, Nation proposer, Ultimatum.Deal terms,
        Ultimatum.DealAssessment known = null)
    {
        if (recipient is null || terms is null)
            return new(DealVerdict.Wait, null, AutoAnswerMode.Ask, known, 0f, "");
        if (!recipient.IsHuman)
        {
            var assessment = known ?? Ultimatum.AssessNegotiatedDeal(c, proposer, recipient, terms);
            return new(GovernmentAccepts(assessment) ? DealVerdict.Accept : DealVerdict.Decline, null,
                AutoAnswerMode.Beneficial, assessment, 0f, assessment.Reason);
        }
        var kind = DealAnswerKinds.Classify(terms);
        var mode = ModeFor(recipient, kind);
        var margin = Margin(c);
        switch (mode)
        {
            case AutoAnswerMode.Decline:
                return new(DealVerdict.Decline, kind, mode, known, margin,
                    "Declined by your foreign ministry under a standing rule. Nothing was transferred.");
            case AutoAnswerMode.Beneficial:
                // Checked on the terms as well as by the data: no rule gives land away.
                if (RecipientGivesLand(terms))
                    return new(DealVerdict.Wait, kind, AutoAnswerMode.Ask, known, margin, "");
                var assessment = known ?? Ultimatum.AssessNegotiatedDeal(c, proposer, recipient, terms);
                if (!assessment.Valid) return new(DealVerdict.Wait, kind, mode, assessment, margin, assessment.Reason);
                return assessment.ToValue >= margin
                    ? new(DealVerdict.Accept, kind, mode, assessment, margin,
                        "The bargain is signed: your foreign ministry answered under a standing rule.")
                    : new(DealVerdict.Wait, kind, mode, assessment, margin, "");
            default:
                return new(DealVerdict.Wait, kind, AutoAnswerMode.Ask, known, margin, "");
        }
    }

    /// <summary>
    /// The deal an agreement offer amounts to. A pact: the asker writes it, the asked country gives
    /// nothing. A trade agreement: one shared term with its terms (the ordinary ones when none are named).
    /// </summary>
    public static Ultimatum.Deal AgreementDeal(AgreementKind kind, TradeTerms terms = null) =>
        kind == AgreementKind.TradeAgreement
            ? new Ultimatum.Deal(new Ultimatum.Side(), new Ultimatum.Side()) { Trade = TradeTerms.Copy(terms) ?? TradeTerms.Legacy() }
            : new Ultimatum.Deal(new Ultimatum.Side { NonAggression = true }, new Ultimatum.Side());

    /// <summary>
    /// A government's own answer to an agreement asked of it. A pact: the bar it always used. A trade
    /// agreement: the same count and the same answer as the deal table gives the same terms.
    /// </summary>
    public static bool GovernmentSignsAgreement(Campaign c, Nation asked, Nation asking, AgreementKind kind,
        TradeTerms terms = null)
    {
        if (kind != AgreementKind.TradeAgreement)
            return AgreementSystem.Willingness(c, asked, asking, kind) >= AgreementSystem.Needed(c, kind);
        return GovernmentSignsTrade(c, asked, asking, terms ?? TradeTermsCatalog.Ordinary(c, asking, asked), out _);
    }

    /// <summary>
    /// A government's answer to trade terms put on their own: the deal table's answer to a table
    /// with that one term (<see cref="Decide(Campaign, Nation, Nation, Ultimatum.Deal, Ultimatum.DealAssessment)"/>).
    /// Where no table can be laid - deals switched off, or a world without a map - the same count is
    /// read without one: a trade-only table's whole value is that count less the grudge floor, so
    /// the answer is the same number either way and trade agreements do not depend on the deal table.
    /// </summary>
    public static bool GovernmentSignsTrade(Campaign c, Nation asked, Nation asking, TradeTerms terms,
        out Ultimatum.DealAssessment assessment)
    {
        assessment = null;
        if (c is null || asked is null || asking is null || terms is null) return false;
        var deal = AgreementDeal(AgreementKind.TradeAgreement, terms);
        assessment = Ultimatum.AssessNegotiatedDeal(c, asking, asked, deal);
        if (assessment.Valid) return Decide(c, asked, asking, deal, assessment).Verdict == DealVerdict.Accept;
        if (Ultimatum.CanOfferDeal(c, asking, asked, out _) || asked.IsHuman) return false;
        return TradeTermsValue.For(c, asked, asking, terms) - Ultimatum.GrudgePenalty(c, asking, asked) >= 0f;
    }

    /// <summary>
    /// An agreement offer on a player's table, answered by that player's standing rule if there is
    /// one. Returns <see cref="AgreementResult.Offered"/> when it stays on the table.
    /// </summary>
    internal static AgreementResult AnswerAgreementByRule(Campaign c, Nation asked, Nation asking, AgreementKind kind,
        out string outcome)
    {
        outcome = "";
        if (c is null || asked is null || asking is null || !asked.IsHuman || !HasStandingRules(asked))
            return AgreementResult.Offered;
        var offered = kind == AgreementKind.TradeAgreement ? CampaignDiplomacy.Peek(c, asked.Id, asking.Id)?.AgreementOfferedTrade : null;
        var decision = Decide(c, asked, asking, AgreementDeal(kind, offered));
        if (decision.Verdict == DealVerdict.Wait) return AgreementResult.Offered;
        var result = AgreementSystem.Answer(c, asked, asking.Id, decision.Verdict == DealVerdict.Accept, out outcome);
        if (result is AgreementResult.Signed or AgreementResult.Refused)
            Remember(c, asked, new AutoAnswerNote
            {
                Day = c.ElapsedDays, ProposerId = asking.Id, Agreement = (int)kind, Kind = decision.Kind?.Id ?? "",
                Mode = decision.Mode, Accepted = result == AgreementResult.Signed, Benefit = decision.Benefit
            });
        return result;
    }

    /// <summary>
    /// Whether the rule said yes and the signature still failed (the terms no longer held, the
    /// regions changed shape). A "sign if beneficial" rule never declines - below the margin it
    /// leaves the paper to the player - so an unsigned answer under it can only be that, and the
    /// player must not read it as their ministry turning a good deal down.
    /// </summary>
    public static bool CouldNotSign(AutoAnswerNote note) =>
        note is { Accepted: false, Mode: AutoAnswerMode.Beneficial };

    internal static void Remember(Campaign c, Nation nation, AutoAnswerNote note)
    {
        // A save is JSON, and JSON has no NaN: an uncounted answer is written as "not counted".
        note.Counted = float.IsFinite(note.Benefit);
        if (!note.Counted) note.Benefit = 0f;
        nation.AutoAnswerLog.Add(note);
        var keep = Math.Clamp(c.Defines.AutoAnswerLogLimit, 0, 256);
        if (nation.AutoAnswerLog.Count > keep) nation.AutoAnswerLog.RemoveRange(0, nation.AutoAnswerLog.Count - keep);
        c.MarkStrategicChange();
        DiagnosticLogs.Write("simulation",
            $"AUTO_ANSWER nation={nation.Tag} from={note.ProposerId} kind={note.Kind} mode={DealAnswerKinds.ModeId(note.Mode)} " +
            $"accepted={note.Accepted} benefit={note.Benefit:0.0} offer={note.OfferId} agreement={note.Agreement} day={note.Day}");
    }

    /// <summary>The standing answers of every country a player answers for, swept once a day.</summary>
    internal static IEnumerable<int> TickAgreements(Campaign c)
    {
        if (c is null || !c.Nations.Any(n => n.IsHuman && HasStandingRules(n))) yield break;
        var waiting = new List<(Nation Asked, Nation Asking, AgreementKind Kind)>();
        foreach (var asked in c.Nations.Where(n => n.IsHuman && HasStandingRules(n)).OrderBy(n => n.Id))
            foreach (var (proposer, kind) in AgreementSystem.OfferedTo(c, asked).OrderBy(o => o.Proposer.Id))
                waiting.Add((asked, proposer, kind));
        var batch = Math.Clamp(c.Defines.AutoAnswerDailyBatch, 0, 1024);
        var closed = 0;
        foreach (var (asked, asking, kind) in waiting)
        {
            if (closed >= batch) yield break;
            if (AnswerAgreementByRule(c, asked, asking, kind, out _) != AgreementResult.Offered) closed++;
            yield return 0;
        }
    }
}

/// <summary>
/// Set, or take back, one standing answer. The only way the rule changes, so it travels through
/// replays, hotseat and the network the way every other order does.
/// </summary>
public sealed class SetAutoAnswerCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string Kind { get; init; } = "";
    public AutoAnswerMode Mode { get; init; }

    public bool Applied { get; private set; }
    public string Reason { get; private set; } = "";

    public void Apply(Campaign campaign)
    {
        Applied = false;
        if (!MechanicFeatures.Allows(campaign, this)) { Reason = Loc.Get("This action is disabled in this campaign."); return; }
        var nation = campaign?.NationById(IssuerId);
        var kind = DealAnswerKinds.Find(Kind);
        if (nation is null || kind is null || !Enum.IsDefined(Mode))
        {
            Reason = Loc.Get("There is no such kind of offer.");
            return;
        }
        if (!kind.Allows(Mode))
        {
            Reason = Mode == AutoAnswerMode.Beneficial
                ? Loc.Get("No standing rule may give land away. Such offers always wait for you, or are declined.")
                : Loc.Get("This answer cannot be set for this kind of offer.");
            return;
        }
        if (Mode == kind.Default) nation.AutoAnswer.Remove(kind.Id);
        else nation.AutoAnswer[kind.Id] = (int)Mode;
        Applied = true;
        campaign.DealInboxRevision++;
        campaign.MarkStrategicChange();
        DiagnosticLogs.Write("orders",
            $"AUTO_ANSWER_RULE nation={nation.Tag} kind={kind.Id} mode={DealAnswerKinds.ModeId(Mode)} day={campaign.ElapsedDays}");
    }
}
