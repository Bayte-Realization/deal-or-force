using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// The weekly budget income at which a diplomatic price is what the defines list (DiplomacyPrice).
    /// A country earning half of it pays half. 20 is the United States' income in 2024, so the
    /// largest economy pays what the prices always were and Poland no longer pays the same.
    /// </summary>
    public float DiplomacyReferenceIncome { get; set; } = 20f;

    /// <summary>The smallest share of the listed price anybody pays; a guard for a treasury with no income yet.</summary>
    public float DiplomacyPriceMinimumScale { get; set; } = 0.00002f;

    /// <summary>The largest multiple of the listed price anybody pays.</summary>
    public float DiplomacyPriceMaximumScale { get; set; } = 2f;

    /// <summary>
    /// The share of its weekly budget income a computer-run government spends on intelligence
    /// networks, covert operations, proxy wars and kin statecraft (CovertBudget). 0 = no cap.
    /// </summary>
    public float CovertAiIncomeShare { get; set; } = 0.05f;

    /// <summary>The longest one covert purchase may hold a computer-run service back, in days.</summary>
    public int CovertAiLongestPause { get; set; } = 365;
}

/// <summary>
/// What diplomacy costs a country: the listed price, times its income against
/// <see cref="Defines.DiplomacyReferenceIncome"/>.
///
/// Every diplomatic price used to be the same number for everybody. A cultural agreement cost 0.35
/// whether the United States asked or Albania, and the computer signs about seventy of them a year
/// whatever its size: SimBench --construction, 2024, seeds 1/7/42 - the United States spent 2.6 % of
/// its income on diplomacy, Poland 80 %, Nigeria 47 %, Albania up to 230 %; the median country 85 %.
/// </summary>
public static class DiplomacyPrice
{
    /// <summary>What this country pays for something the defines price at <paramref name="listed"/>.</summary>
    public static float Of(Campaign campaign, Nation nation, float listed)
    {
        if (!float.IsFinite(listed) || listed <= 0f) return 0f;
        return listed * Scale(campaign, nation);
    }

    /// <summary>The multiple of a listed price this country pays.</summary>
    public static float Scale(Campaign campaign, Nation nation)
    {
        var d = campaign?.Defines;
        if (d is null || nation is null) return 1f;
        var low = float.IsFinite(d.DiplomacyPriceMinimumScale) ? MathF.Max(0f, d.DiplomacyPriceMinimumScale) : 0f;
        var high = float.IsFinite(d.DiplomacyPriceMaximumScale) ? MathF.Max(low, d.DiplomacyPriceMaximumScale) : 1f;
        // A reference of nought or less switches the scaling off: the listed prices for everybody.
        if (!float.IsFinite(d.DiplomacyReferenceIncome) || d.DiplomacyReferenceIncome <= 0f) return 1f;
        var income = ConstructionQueue.WeeklyIncome(campaign, nation);
        if (!float.IsFinite(income) || income <= 0f) return low;
        return Math.Clamp(income / d.DiplomacyReferenceIncome, low, high);
    }
}

/// <summary>
/// How much a computer-run government lets its secret service spend: <see cref="Defines.CovertAiIncomeShare"/>
/// of its weekly budget income, less what its proxy wars already take every week. A purchase
/// (a network founded, an operation ordered, a movement founded or agitated) is paid at once and then
/// earned back from that allowance: the service waits until it has been, before it buys again.
///
/// Without it the computer bought whenever its treasury held four or five times the price, and a
/// network costs 0.08 % of a year's output - nearly a week of the United States' income. The
/// United States spent 15.5 % of its income on covert work in 2024, China up to 13 %.
///
/// The player's service is not capped: the treasury and the ledger are his own business. The
/// rule is stateless apart from one entry in <see cref="Nation.DecisionAvailableDay"/>, which saves.
/// </summary>
public static class CovertBudget
{
    public const string PauseKey = "covert_budget";

    /// <summary>What the service may spend a week, before its standing proxy payments.</summary>
    public static float WeeklyAllowance(Campaign campaign, Nation nation)
    {
        var share = campaign?.Defines?.CovertAiIncomeShare ?? 0f;
        if (!float.IsFinite(share) || share <= 0f || nation is null) return float.PositiveInfinity;
        var income = ConstructionQueue.WeeklyIncome(campaign, nation);
        return float.IsFinite(income) && income > 0f ? share * income : 0f;
    }

    /// <summary>What the country's proxy wars take a week now.</summary>
    public static float Committed(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null) return 0f;
        var weekly = 0f;
        foreach (var s in campaign.Sponsorships)
            if (!s.Ended && s.SponsorId == nation.Id) weekly += ProxyWars.WeeklyCost(campaign, nation, s.Level);
        return weekly;
    }

    /// <summary>What is left of the week's allowance once the proxy wars are paid. Infinity when uncapped.</summary>
    public static float Free(Campaign campaign, Nation nation) =>
        WeeklyAllowance(campaign, nation) - Committed(campaign, nation);

    /// <summary>Whether a computer-run service may buy something now. Always true for a player.</summary>
    public static bool AiMaySpend(Campaign campaign, Nation nation)
    {
        if (nation is null || campaign is null) return false;
        if (nation.IsHuman) return true;
        if (float.IsPositiveInfinity(WeeklyAllowance(campaign, nation))) return true;
        if (Free(campaign, nation) <= 0f) return false;
        return nation.DecisionAvailableDay.GetValueOrDefault(PauseKey) <= campaign.ElapsedDays;
    }

    /// <summary>Whether a computer-run sponsor can carry a proxy war costing this much more a week.</summary>
    public static bool AiMayCommit(Campaign campaign, Nation nation, float weekly) =>
        nation is not null && (nation.IsHuman || Free(campaign, nation) >= weekly);

    /// <summary>Record a purchase: the service waits while the allowance earns it back.</summary>
    public static void AiSpent(Campaign campaign, Nation nation, float cost)
    {
        if (campaign is null || nation is null || nation.IsHuman || !float.IsFinite(cost) || cost <= 0f) return;
        var free = Free(campaign, nation);
        if (float.IsPositiveInfinity(free)) return;
        var longest = Math.Max(1, campaign.Defines.CovertAiLongestPause);
        var days = free > 0f ? (int)MathF.Min(longest, MathF.Ceiling(cost / free * 7f)) : longest;
        var from = Math.Max(campaign.ElapsedDays, nation.DecisionAvailableDay.GetValueOrDefault(PauseKey));
        nation.DecisionAvailableDay[PauseKey] = Math.Min(campaign.ElapsedDays + longest, from + days);
    }
}
