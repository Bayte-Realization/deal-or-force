using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The tunables of a timed income penalty (common/defines/93_income_penalties.json, where each
/// number has its reasoning written beside it).
/// </summary>
public sealed partial class Defines
{
    /// <summary>The most of a week's gross income all penalties together may take, 0..1.</summary>
    public float IncomePenaltyCap { get; set; } = 0.5f;

    /// <summary>The most a single penalty converted from a lump may take, 0..1.</summary>
    public float IncomePenaltySingleMax { get; set; } = 0.25f;

    /// <summary>The shortest and longest a converted penalty may run, in days.</summary>
    public int IncomePenaltyMinDays { get; set; } = 28;
    public int IncomePenaltyMaxDays { get; set; } = 1095;

    /// <summary>How many penalties one country carries at once; past it, a new one joins the latest.</summary>
    public int IncomePenaltyMaxEntries { get; set; } = 16;

    /// <summary>
    /// Gross budget revenue as a share of GDP, for a country whose first budget week has not
    /// closed yet. Measured on the soak: about six per cent in this model.
    /// </summary>
    public float IncomePenaltyReferenceRevenueShare { get; set; } = 0.06f;

    /// <summary>How long the hard-coded crises spread their cost over, in days.</summary>
    public int CrisisIncomePenaltyDays { get; set; } = 540;
    public int PandemicIncomePenaltyDays { get; set; } = 365;
    public int DisasterIncomePenaltyDays { get; set; } = 180;
}

/// <summary>One timed cut to a country's gross income.</summary>
public sealed class IncomePenalty
{
    /// <summary>The share of gross weekly income it takes, 0..1, before the overall cap.</summary>
    public float Share;

    /// <summary>The campaign day it was imposed.</summary>
    public int StartDay;

    /// <summary>The first day it no longer applies; the weekly tick removes it on or after this day.</summary>
    public int UntilDay;

    /// <summary>What caused it: an event id, a crisis kind - read by the ledger, never by the arithmetic.</summary>
    public string Source = "";

    /// <summary>
    /// The campaign's IncomePenaltyCap, copied here when the penalty is imposed and again by every
    /// weekly tick. BudgetCore.Evaluate is called with a country and no campaign (the AI's fiscal
    /// reading, radicalism, the balance sheet), and a process-wide cap would carry the previous
    /// campaign's rules into the next one; carried on the penalty, it is saved and loaded with it,
    /// so host and client read the same number. 0.5 is the default rule, for an entry saved
    /// before the field existed.
    /// </summary>
    public float Cap = 0.5f;
}

public sealed partial class Nation
{
    /// <summary>
    /// Timed cuts to gross income (IncomePenalties). Null when there are none, so the two hundred
    /// countries that carry nothing carry no list; an old save loads with none.
    /// </summary>
    public List<IncomePenalty> IncomePenalties;

    /// <summary>Last closed week's gross budget revenue before any penalty, in billions.</summary>
    public float WeeklyGrossIncome;

    /// <summary>Last closed week's money the penalties took off that revenue, in billions.</summary>
    public float WeeklyIncomePenalty;
}

/// <summary>
/// A timed income penalty: instead of taking a lump out of the treasury, a consequence takes a
/// share of the country's gross budget revenue - revenue before any expense - for a number of days.
///
/// It is harsher where a country lives on its revenue and gentler where it sits on a large
/// reserve, which is the owner's point: a scandal or a sanction should hurt the economy that
/// produced it, not punish whoever happened to have saved. Expenses are untouched, so a
/// country that keeps its spending must borrow or cut through the penalty.
///
/// Rules, in one place:
///   • it reduces gross revenue only, in exactly one spot - BudgetCore.Evaluate - so the weekly
///     balance, the AI's fiscal reading and the player's budget all see the same figure;
///   • penalties add up, and the sum is capped at Defines.IncomePenaltyCap;
///   • each expires on its UntilDay, removed by the weekly economy tick (Expire), never by a
///     screen, so the world does not depend on who is looking;
///   • it applies to every country alike: no player branch.
/// </summary>
public static class IncomePenalties
{
    /// <summary>
    /// The share all of a country's current penalties take together, held at the cap they carry
    /// (the campaign's IncomePenaltyCap). The budget reads this one: it needs no campaign.
    /// </summary>
    public static float Share(Nation nation)
    {
        var list = nation?.IncomePenalties;
        if (list is null || list.Count == 0) return 0f;
        // Every entry carries the same cap once a weekly tick has passed; until then, the
        // smallest, so a rule that was tightened is never exceeded.
        var cap = 1f;
        for (var index = 0; index < list.Count; index++)
            if (list[index] is { } penalty && float.IsFinite(penalty.Cap)) cap = MathF.Min(cap, penalty.Cap);
        return Share(nation, cap);
    }

    /// <summary>The share all of a country's current penalties take together, after the given cap.</summary>
    public static float Share(Nation nation, float cap)
    {
        var list = nation?.IncomePenalties;
        if (list is null || list.Count == 0) return 0f;
        var total = 0f;
        for (var index = 0; index < list.Count; index++)
        {
            var share = list[index]?.Share ?? 0f;
            if (float.IsFinite(share) && share > 0f) total += share;
        }
        return M.Clamp(total, 0f, M.Clamp01(cap));
    }

    /// <summary>Impose a penalty. Shares outside 0..1 and non-positive lengths are refused.</summary>
    public static bool Add(Campaign campaign, Nation nation, float share, int days, string source)
    {
        if (campaign is null || nation is null || !float.IsFinite(share) || share <= 0f || days <= 0) return false;
        var cap = M.Clamp01(campaign.Defines.IncomePenaltyCap);
        share = M.Clamp01(share);
        var list = nation.IncomePenalties ??= new List<IncomePenalty>(2);
        var until = campaign.ElapsedDays + days;
        var limit = Math.Max(1, campaign.Defines.IncomePenaltyMaxEntries);
        if (list.Count >= limit)
        {
            // Full: the new cut joins the one that runs longest, so nothing is lost and the list
            // stays bounded. The joined entry keeps the later end.
            var latest = list[0];
            for (var index = 1; index < list.Count; index++)
                if (list[index].UntilDay > latest.UntilDay) latest = list[index];
            latest.Share = M.Clamp01(latest.Share + share);
            latest.UntilDay = Math.Max(latest.UntilDay, until);
            latest.Cap = cap;
            return true;
        }
        list.Add(new IncomePenalty { Share = share, StartDay = campaign.ElapsedDays, UntilDay = until, Source = source ?? "", Cap = cap });
        return true;
    }

    /// <summary>
    /// Turn a one-off loss into a penalty of the same expected size: the share of this country's
    /// gross weekly income that, held for <paramref name="days"/>, adds up to <paramref name="loss"/>.
    /// A share above IncomePenaltySingleMax is held at it and the penalty runs longer instead, up to
    /// IncomePenaltyMaxDays; past that the remainder is forgiven rather than turned into a lump.
    /// </summary>
    public static bool FromLoss(Campaign campaign, Nation nation, float loss, int days, string source)
    {
        if (campaign is null || nation is null || !float.IsFinite(loss) || loss <= 0f) return false;
        var d = campaign.Defines;
        var weekly = WeeklyBase(campaign, nation);
        if (weekly <= 0f) return false;
        var minDays = Math.Max(7, d.IncomePenaltyMinDays);
        var maxDays = Math.Max(minDays, d.IncomePenaltyMaxDays);
        days = Math.Clamp(days, minDays, maxDays);
        var share = loss / (weekly * days / 7f);
        var single = M.Clamp01(d.IncomePenaltySingleMax);
        if (share > single && single > 0f)
        {
            days = Math.Clamp((int)MathF.Ceiling(loss / (weekly * single) * 7f), minDays, maxDays);
            share = single;
        }
        return Add(campaign, nation, share, days, source);
    }

    /// <summary>The gross weekly income a penalty is a share of: last week's, or a reference one before the first week.</summary>
    public static float WeeklyBase(Campaign campaign, Nation nation)
    {
        if (nation is null) return 0f;
        if (nation.WeeklyGrossIncome > 0f && float.IsFinite(nation.WeeklyGrossIncome)) return nation.WeeklyGrossIncome;
        var reference = campaign?.Defines.IncomePenaltyReferenceRevenueShare ?? 0.06f;
        return MathF.Max(0f, nation.Gdp) / 52f * MathF.Max(0f, reference);
    }

    /// <summary>
    /// Remove what has run out, and give what remains the campaign's current cap, so a save
    /// loaded under rules with a different IncomePenaltyCap follows them from its first week.
    /// Called by the weekly economy tick for every country, on host and client alike.
    /// </summary>
    public static void Expire(Campaign campaign, Nation nation)
    {
        var list = nation?.IncomePenalties;
        if (campaign is null || list is null) return;
        var cap = M.Clamp01(campaign.Defines.IncomePenaltyCap);
        for (var index = list.Count - 1; index >= 0; index--)
        {
            if (list[index] is null || list[index].UntilDay <= campaign.ElapsedDays || !(list[index].Share > 0f))
                list.RemoveAt(index);
            else list[index].Cap = cap;
        }
        if (list.Count == 0) nation.IncomePenalties = null;
    }

    /// <summary>The days the longest current penalty still has to run, 0 when there is none.</summary>
    public static int DaysLeft(Campaign campaign, Nation nation)
    {
        var list = nation?.IncomePenalties;
        if (campaign is null || list is null) return 0;
        var days = 0;
        foreach (var penalty in list) days = Math.Max(days, penalty.UntilDay - campaign.ElapsedDays);
        return Math.Max(0, days);
    }

    /// <summary>
    /// A penalty's source read back as something a player can. A penalty from content carries
    /// the parse context of the file that imposed it ("Event 'id'", "decision 'id'",
    /// "promise 'id'", "promise 'id' failure", "Modifier 'id'"); each is shown by the name its
    /// catalogue gives it, and by its id when the catalogue no longer has it, never as the raw
    /// context. Anything else is a caption already ("Financial crisis", a disaster's name).
    /// </summary>
    public static string Describe(string source)
    {
        if (string.IsNullOrWhiteSpace(source)) return Loc.Get("An earlier event");
        var text = source.Trim();
        if (Quoted(text, "Event '", "", out var id))
        {
            var all = EventCatalog.All;
            for (var index = 0; index < all.Count; index++)
                if (all[index].Id == id && !string.IsNullOrWhiteSpace(all[index].Title)) return Loc.Get(all[index].Title);
            return id;
        }
        if (Quoted(text, "decision '", "", out id))
            return Named(DecisionCatalog.Find(id)?.Name, id);
        if (Quoted(text, "promise '", " failure", out id))
            return Loc.Format("Broken promise: {0}", Named(PromiseCatalog.Find(id)?.Name, id));
        if (Quoted(text, "promise '", "", out id))
            return Named(PromiseCatalog.Find(id)?.Name, id);
        if (Quoted(text, "Modifier '", "", out id))
            return Named(ModifierCatalog.Get(id)?.Name, id);
        return Loc.Get(text);
    }

    private static string Named(string name, string id) =>
        string.IsNullOrWhiteSpace(name) ? id : Loc.Get(name);

    /// <summary>The id in <c>prefix + id + "'" + suffix</c>, the way the content loaders write a parse context.</summary>
    private static bool Quoted(string text, string prefix, string suffix, out string id)
    {
        id = "";
        var tail = "'" + suffix;
        if (!text.StartsWith(prefix, StringComparison.Ordinal) || !text.EndsWith(tail, StringComparison.Ordinal) ||
            text.Length <= prefix.Length + tail.Length) return false;
        id = text[prefix.Length..^tail.Length];
        return id.Length > 0 && !id.Contains('\'');
    }
}
