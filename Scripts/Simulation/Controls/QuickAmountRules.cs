using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;

namespace DealOrForce.Simulation.Controls;

public readonly record struct QuickRecruitQuote(
    int MaxDivisions,
    int QueueHeadroom,
    int MaxByTreasury,
    int MaxByManpower,
    int MaxByBatch,
    int Days,
    float SingleCost,
    string Refusal);

public readonly record struct EverywhereRecruitItem(
    ScenarioState Region,
    int Divisions,
    float Cost,
    int Manpower);

public readonly record struct EverywhereRecruitPlan(
    IReadOnlyList<EverywhereRecruitItem> Items,
    int TotalDivisions,
    float TotalCost,
    int TotalManpower,
    string Refusal);

public readonly record struct EverywhereBuildItem(
    ScenarioState Region,
    int Count,
    float Cost);

public readonly record struct EverywhereBuildPlan(
    IReadOnlyList<EverywhereBuildItem> Items,
    int TotalBuildings,
    float TotalCost,
    string Refusal);

public readonly record struct DebtPreviewOutcome(
    float CashAfter,
    float PrincipalAfter,
    float ArrearsAfter,
    float DebtRatioAfter,
    bool Valid,
    string Refusal);

public readonly record struct PolicyRateChange(
    string LineId,
    string Name,
    float OldRate,
    float NewRate);

public readonly record struct ZeroBudgetPlan(
    IReadOnlyList<PolicyRateChange> Changes,
    float BalanceBefore,
    float BalanceAfter,
    bool AdjustTaxes,
    string Summary);

/// <summary>
/// Pure simulation math for QuickAmount widgets: 25/50/75/100% shares, direct numeric / % / B parsing,
/// recruitment/construction capacity caps, "Do Everywhere" dry-run quotes, debt previews,
/// and exact Laffer-safe "Balance to zero" budget solver.
/// </summary>
public static class QuickAmountRules
{
    public static int QuarterInteger(int max, int quarterIndex)
    {
        if (max <= 0) return 0;
        var q = Math.Clamp(quarterIndex, 1, 4);
        if (q == 4) return max;
        return Math.Clamp((int)MathF.Round(max * (q * 0.25f), MidpointRounding.AwayFromZero), 1, max);
    }

    public static float QuarterFloat(float max, int quarterIndex, float step = 0.01f)
    {
        if (!float.IsFinite(max) || max <= 0f) return 0f;
        var q = Math.Clamp(quarterIndex, 1, 4);
        if (q == 4) return RoundToStep(max, step);
        var raw = max * (q * 0.25f);
        return Math.Clamp(RoundToStep(raw, step), 0f, max);
    }

    public static float RoundToStep(float value, float step)
    {
        if (step <= 0f || !float.IsFinite(value)) return value;
        return MathF.Round(value / step, MidpointRounding.AwayFromZero) * step;
    }

    /// <summary>
    /// Parses direct entries such as "5", "50%", "1.5B", "1,5 млрд", "250M", "250 млн".
    /// Percent values resolve relative to <paramref name="max"/>.
    /// </summary>
    public static bool TryParseDirectValue(string raw, float min, float max, float step, bool integerOnly,
        out float value)
    {
        value = min;
        if (string.IsNullOrWhiteSpace(raw)) return false;
        var text = raw.Trim();
        var isPercent = false;
        var scale = 1f;

        if (text.EndsWith('%'))
        {
            isPercent = true;
            text = text[..^1].Trim();
        }
        else if (EndsWithToken(text, "млрд", out var trimmedB) || EndsWithToken(text, "b", out trimmedB))
        {
            scale = 1f; // Treasury & debt amounts are already in billions.
            text = trimmedB;
        }
        else if (EndsWithToken(text, "млн", out var trimmedM) || EndsWithToken(text, "m", out trimmedM))
        {
            scale = 0.001f;
            text = trimmedM;
        }
        else if (EndsWithToken(text, "тыс.", out var trimmedK) || EndsWithToken(text, "тыс", out trimmedK) ||
                 EndsWithToken(text, "k", out trimmedK))
        {
            scale = 1000f;
            text = trimmedK;
        }

        text = text.Replace(" ", "").Replace('\u00A0'.ToString(), "").Replace(',', '.');
        if (!float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out var parsed) ||
            !float.IsFinite(parsed))
            return false;

        var computed = isPercent ? max * (parsed * 0.01f) : parsed * scale;
        if (integerOnly)
            computed = MathF.Round(computed, MidpointRounding.AwayFromZero);
        else if (step > 0f)
            computed = RoundToStep(computed, step);

        value = Math.Clamp(computed, min, Math.Max(min, max));
        return true;
    }

    private static bool EndsWithToken(string text, string suffix, out string head)
    {
        if (text.EndsWith(suffix, StringComparison.OrdinalIgnoreCase))
        {
            head = text[..^suffix.Length].Trim();
            return head.Length > 0;
        }
        head = text;
        return false;
    }

    // ------------------------------------------------------------------ Recruitment

    public static QuickRecruitQuote EvaluateRecruitmentCap(Campaign campaign, Nation nation, ScenarioState state,
        string templateId, MobilisationMode mode = MobilisationMode.Standard, bool allowDeficit = false)
    {
        if (campaign is null || nation is null || state is null)
            return new QuickRecruitQuote(0, 0, 0, 0, 0, 0, 0f, Loc.Get("Campaign map is not ready."));
        if (!RecruitmentSystem.Enabled(campaign))
            return new QuickRecruitQuote(0, 0, 0, 0, 0, 0, 0f, Loc.Get("Recruitment is disabled in this campaign."));
        if (state.NationMapIndex != nation.MapIndex)
            return new QuickRecruitQuote(0, 0, 0, 0, 0, 0, 0f, Loc.Get("Choose an owned training region."));
        if (RecruitmentSystem.DeploymentProvince(campaign, nation.Id, state.MapIndex) <= 0)
            return new QuickRecruitQuote(0, 0, 0, 0, 0, 0, 0f, Loc.Get("No controlled province in this region."));

        var template = UnitTypeCatalog.All.FirstOrDefault(t => t.Id == templateId);
        if (template is null)
            return new QuickRecruitQuote(0, 0, 0, 0, 0, 0, 0f, Loc.Get("Choose a valid template."));

        var maxBatch = RecruitmentSystem.MaxDivisionsPerBatch(campaign, nation, state, mode);
        var cap = RecruitmentSystem.DynamicQueueCap(campaign, nation, mode);
        var used = campaign.Recruitment.Count(o => o.NationId == nation.Id);
        var headroom = Math.Max(0, cap - used);
        var days = RecruitmentSystem.TrainingDaysOf(nation, template, mode);
        var singleCost = RecruitmentSystem.TotalBatchCost(campaign, nation, template, 1, mode, out _);

        var maxByManpower = template.RecruitmentPersonnel <= 0
            ? maxBatch
            : (int)MathF.Floor(Math.Max(0f, nation.ManpowerReserve) / template.RecruitmentPersonnel);

        var maxByTreasury = 0;
        for (var count = 1; count <= maxBatch; count++)
        {
            var refusal = RecruitmentSystem.Validate(campaign, nation.Id, state.MapIndex, templateId, count,
                allowDeficit, mode);
            if (refusal.Length == 0)
            {
                maxByTreasury = count;
                continue;
            }
            if (count == 1)
            {
                // Still report individual ceilings accurately.
                maxByTreasury = 0;
            }
            break;
        }

        var maxAllowed = headroom <= 0 ? 0 : Math.Clamp(Math.Min(maxBatch, Math.Min(maxByTreasury, maxByManpower)), 0, maxBatch);
        var refusalReason = headroom <= 0
            ? Loc.Format("Training capacity reached: {0} batches.", cap)
            : maxAllowed <= 0
                ? RecruitmentSystem.Validate(campaign, nation.Id, state.MapIndex, templateId, 1, allowDeficit, mode)
                : "";
        return new QuickRecruitQuote(maxAllowed, headroom, maxByTreasury, maxByManpower, maxBatch, days, singleCost, refusalReason);
    }

    public static IReadOnlyList<ScenarioState> EligibleRecruitRegions(Campaign campaign, Nation nation,
        IReadOnlyList<ScenarioState> explicitRegions = null, bool preferBarracks = true)
    {
        if (campaign is null || nation is null) return Array.Empty<ScenarioState>();
        IEnumerable<ScenarioState> source = explicitRegions is { Count: > 0 }
            ? explicitRegions
            : campaign.StatesOf(nation);
        var owned = source
            .Where(s => s is not null && !s.Unclaimed && s.NationMapIndex == nation.MapIndex &&
                        !LakeRegions.IsWaterState(s) &&
                        RecruitmentSystem.DeploymentProvince(campaign, nation.Id, s.MapIndex) > 0)
            .Distinct()
            .ToList();
        if (explicitRegions is not { Count: > 0 } && preferBarracks)
        {
            var withBarracks = owned.Where(s => s.Buildings.GetValueOrDefault(BuildingKind.Barracks) > 0).ToList();
            if (withBarracks.Count > 0) owned = withBarracks;
        }
        return owned
            .OrderByDescending(s => s.Buildings.GetValueOrDefault(BuildingKind.Barracks))
            .ThenByDescending(s => s.Population)
            .ThenBy(s => s.MapIndex)
            .ToList();
    }

    /// <summary>
    /// Simulates sequential recruitment across eligible regions so the tooltip and execution
    /// quote exact total divisions, manpower, and treasury cost without mutating the campaign.
    /// </summary>
    public static EverywhereRecruitPlan PlanEverywhereRecruitment(Campaign campaign, Nation nation,
        IReadOnlyList<ScenarioState> regions, string templateId, MobilisationMode mode,
        int quarterOrCount, bool isQuarter, bool allowDeficit = false)
    {
        var items = new List<EverywhereRecruitItem>();
        if (campaign is null || nation is null)
            return new EverywhereRecruitPlan(items, 0, 0f, 0, Loc.Get("Campaign map is not ready."));
        if (!RecruitmentSystem.Enabled(campaign))
            return new EverywhereRecruitPlan(items, 0, 0f, 0, Loc.Get("Recruitment is disabled in this campaign."));
        var template = UnitTypeCatalog.All.FirstOrDefault(t => t.Id == templateId);
        if (template is null)
            return new EverywhereRecruitPlan(items, 0, 0f, 0, Loc.Get("Choose a valid template."));

        var eligible = EligibleRecruitRegions(campaign, nation, regions);
        if (eligible.Count == 0)
            return new EverywhereRecruitPlan(items, 0, 0f, 0, Loc.Get("No eligible training regions."));

        var cap = RecruitmentSystem.DynamicQueueCap(campaign, nation, mode);
        var usedBatches = campaign.Recruitment.Count(o => o.NationId == nation.Id);
        var remainingTreasury = nation.Treasury;
        var remainingManpower = nation.ManpowerReserve;
        var atWar = Warfare.LiveWars(campaign).Any(w => w.Involves(nation.Id));
        var projectedDebt = SovereignFinance.Outstanding(nation);
        var debtLimit = MathF.Max(0f, nation.Gdp * campaign.Defines.WarDebtLimitShare);

        var totalDivs = 0;
        var totalCost = 0f;
        var totalManpower = 0;
        string lastRefusal = "";

        foreach (var state in eligible)
        {
            if (usedBatches >= cap)
            {
                lastRefusal = Loc.Format("Training capacity reached: {0} batches.", cap);
                break;
            }
            var maxBatch = RecruitmentSystem.MaxDivisionsPerBatch(campaign, nation, state, mode);
            var maxMan = template.RecruitmentPersonnel <= 0 ? maxBatch : (int)MathF.Floor(Math.Max(0f, remainingManpower) / template.RecruitmentPersonnel);
            var maxCash = 0;
            for (var c = 1; c <= maxBatch; c++)
            {
                if (c > maxMan) break;
                var cost = RecruitmentSystem.TotalBatchCost(campaign, nation, template, c, mode, out _);
                if (remainingTreasury >= cost)
                {
                    maxCash = c;
                    continue;
                }
                if (allowDeficit && atWar)
                {
                    var borrowed = cost - MathF.Max(0f, remainingTreasury);
                    if (float.IsFinite(projectedDebt + borrowed) && projectedDebt + borrowed <= debtLimit)
                    {
                        maxCash = c;
                        continue;
                    }
                }
                break;
            }

            var regionMax = Math.Min(maxBatch, Math.Min(maxMan, maxCash));
            if (regionMax <= 0)
            {
                if (lastRefusal.Length == 0)
                    lastRefusal = maxMan <= 0 ? Loc.Get("Insufficient manpower reserve.") : Loc.Get("Insufficient treasury.");
                continue;
            }

            var count = isQuarter
                ? QuarterInteger(regionMax, quarterOrCount)
                : Math.Clamp(quarterOrCount, 1, regionMax);
            if (count <= 0) continue;

            var finalCost = RecruitmentSystem.TotalBatchCost(campaign, nation, template, count, mode, out _);
            var finalMan = template.RecruitmentPersonnel * count;

            if (remainingTreasury < finalCost && allowDeficit)
                projectedDebt += finalCost - MathF.Max(0f, remainingTreasury);
            remainingTreasury -= finalCost;
            remainingManpower -= finalMan;
            usedBatches++;

            items.Add(new EverywhereRecruitItem(state, count, finalCost, finalMan));
            totalDivs += count;
            totalCost += finalCost;
            totalManpower += finalMan;
        }

        return new EverywhereRecruitPlan(items, totalDivs, totalCost, totalManpower,
            items.Count == 0 ? (lastRefusal.Length > 0 ? lastRefusal : Loc.Get("No eligible training regions.")) : "");
    }

    // ------------------------------------------------------------------ Construction

    public static IReadOnlyList<ScenarioState> EligibleBuildRegions(Campaign campaign, Nation nation,
        BuildingKind kind, IReadOnlyList<ScenarioState> explicitRegions = null)
    {
        if (campaign is null || nation is null) return Array.Empty<ScenarioState>();
        IEnumerable<ScenarioState> source = explicitRegions is { Count: > 0 }
            ? explicitRegions
            : campaign.States;
        return source
            .Where(s => s is not null && !s.Unclaimed &&
                        (s.NationMapIndex == nation.MapIndex ||
                         (s.HeldById == nation.Id && HeldRegions.Administered(campaign, s))) &&
                        ConstructionAdmission.Refusal(campaign, nation, s, kind).Length == 0)
            .Distinct()
            .OrderByDescending(s => s.Population)
            .ThenBy(s => s.MapIndex)
            .ToList();
    }

    public static EverywhereBuildPlan PlanEverywhereConstruction(Campaign campaign, Nation nation,
        IReadOnlyList<ScenarioState> regions, BuildingKind kind, int multiplierPerRegion)
    {
        var items = new List<EverywhereBuildItem>();
        if (campaign is null || nation is null)
            return new EverywhereBuildPlan(items, 0, 0f, Loc.Get("Campaign map is not ready."));
        if (!campaign.FeatureEnabled("construction"))
            return new EverywhereBuildPlan(items, 0, 0f, Loc.Get("Construction is disabled by the scenario."));
        var ruleRefusal = BuildingRules.Refusal(campaign, nation, kind);
        if (ruleRefusal.Length > 0)
            return new EverywhereBuildPlan(items, 0, 0f, ruleRefusal);

        var limit = ConstructionCost.Limit(campaign, nation, campaign.Defines.MaxConstructionProjects);
        var running = campaign.Construction.Count(p => p.NationId == nation.Id);
        var slotsLeft = Math.Max(0, limit - running);
        if (slotsLeft <= 0)
            return new EverywhereBuildPlan(items, 0, 0f, Loc.Get("All construction slots are full."));

        var unitCost = ConstructionCost.Of(campaign, nation, kind);
        if (nation.Treasury < MathF.Min(0.05f, unitCost))
            return new EverywhereBuildPlan(items, 0, 0f, Loc.Get("The treasury cannot start it."));

        var eligible = EligibleBuildRegions(campaign, nation, kind, regions);
        if (eligible.Count == 0)
            return new EverywhereBuildPlan(items, 0, 0f, Loc.Get("No eligible region can build this."));

        var perRegion = Math.Max(1, multiplierPerRegion);
        var totalBuildings = 0;
        var totalCost = 0f;

        foreach (var state in eligible)
        {
            if (slotsLeft <= 0) break;
            var take = Math.Min(perRegion, slotsLeft);
            if (take <= 0) break;
            slotsLeft -= take;
            totalBuildings += take;
            var cost = unitCost * take;
            totalCost += cost;
            items.Add(new EverywhereBuildItem(state, take, cost));
        }

        return new EverywhereBuildPlan(items, totalBuildings, totalCost,
            items.Count == 0 ? Loc.Get("No eligible region can build this.") : "");
    }

    // ------------------------------------------------------------------ Debt

    public static DebtPreviewOutcome PreviewDebtAction(Campaign campaign, Nation nation, string action, float amount)
    {
        if (campaign is null || nation is null || nation.Movement is { Landless: true })
            return new DebtPreviewOutcome(0f, 0f, 0f, 0f, false, Loc.Get("Unavailable."));
        var sheet = SovereignBalanceSheet.Read(campaign, nation);
        var cleanAmount = MathF.Max(0f, amount);
        var gdp = MathF.Max(0.01f, nation.Gdp);
        switch ((action ?? "").ToLowerInvariant())
        {
            case "repay":
            {
                var actual = MathF.Min(cleanAmount, MathF.Min(sheet.Cash, sheet.Principal));
                var cash = MathF.Max(0f, sheet.Cash - actual);
                var principal = MathF.Max(0f, sheet.Principal - actual);
                return new DebtPreviewOutcome(cash, principal, sheet.Arrears, principal / gdp,
                    actual >= 0.005f, actual < 0.005f ? Loc.Get("Nothing to repay, or no cash to repay it with.") : "");
            }
            case "arrears":
            {
                var actual = MathF.Min(cleanAmount, MathF.Min(sheet.Cash, sheet.Arrears));
                var cash = MathF.Max(0f, sheet.Cash - actual);
                var arrears = MathF.Max(0f, sheet.Arrears - actual);
                return new DebtPreviewOutcome(cash, sheet.Principal, arrears, sheet.Principal / gdp,
                    actual >= 0.005f, actual < 0.005f ? Loc.Get("No overdue bills, or no cash to pay them with.") : "");
            }
            case "borrow":
            {
                var actual = MathF.Min(cleanAmount, MathF.Max(0f, sheet.NewCredit));
                var cash = sheet.Cash + actual;
                var principal = sheet.Principal + actual;
                return new DebtPreviewOutcome(cash, principal, sheet.Arrears, principal / gdp,
                    actual >= 0.005f, actual < 0.005f ? Loc.Get("No new credit is available this week.") : "");
            }
            default:
                return new DebtPreviewOutcome(sheet.Cash, sheet.Principal, sheet.Arrears, sheet.Principal / gdp, false, "");
        }
    }

    // ------------------------------------------------------------------ Budget

    /// <summary>
    /// Computes rate adjustments to bring the weekly budget as close to zero balance as possible
    /// without mutating campaign state on return.
    /// </summary>
    public static ZeroBudgetPlan PlanBalanceToZero(Campaign campaign, Nation nation, bool adjustTaxes)
    {
        var changes = new List<PolicyRateChange>();
        if (campaign is null || nation is null)
            return new ZeroBudgetPlan(changes, 0f, 0f, adjustTaxes, "");

        var atWar = Warfare.LiveWars(campaign).Any(w => w.Involves(nation.Id));
        var weeklyOutput = nation.Gdp / 52f;
        var allAdjustable = BudgetCore.PlayerLines
            .Where(l => adjustTaxes
                ? l.Side == BudgetSide.Revenue && l.Charge == BudgetBasis.Output
                : l.Side == BudgetSide.Expense)
            .ToArray();
        if (allAdjustable.Length == 0)
            return new ZeroBudgetPlan(changes, 0f, 0f, adjustTaxes, "");

        var saved = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        foreach (var line in allAdjustable)
            saved[line.Id] = BudgetCore.RateOf(nation, line);

        try
        {
            var initial = BudgetCore.Evaluate(nation, weeklyOutput, atWar);
            var startBalance = initial.Balance;
            if (MathF.Abs(startBalance) < 0.0005f)
                return new ZeroBudgetPlan(changes, startBalance, startBalance, adjustTaxes, Loc.Get("Budget is already balanced."));

            float EvalWithT(float t, bool raising)
            {
                foreach (var line in allAdjustable)
                {
                    var oldRate = saved[line.Id];
                    var target = raising ? line.Maximum : line.Minimum;
                    var next = oldRate + (target - oldRate) * t;
                    BudgetCore.SetRate(nation, line, MathF.Round(next / 0.005f) * 0.005f);
                }
                return BudgetCore.Evaluate(nation, weeklyOutput, atWar).Balance;
            }

            // Direction:
            // - Deficit (< 0): if adjusting taxes -> raise taxes; if adjusting expenses -> lower expenses.
            // - Surplus (> 0): if adjusting taxes -> lower taxes; if adjusting expenses -> raise expenses.
            var raising = adjustTaxes ? startBalance < 0f : startBalance > 0f;

            // Grid search first so Laffer revenue peaks never trick binary search.
            var bestT = 0f;
            var bestBalance = startBalance;
            var bestAbs = MathF.Abs(startBalance);
            var prevT = 0f;
            var prevBal = startBalance;
            var foundCross = false;
            float crossLo = 0f, crossHi = 1f;

            const int samples = 32;
            for (var s = 1; s <= samples; s++)
            {
                var t = s / (float)samples;
                var bal = EvalWithT(t, raising);
                var abs = MathF.Abs(bal);
                if (abs < bestAbs)
                {
                    bestAbs = abs;
                    bestT = t;
                    bestBalance = bal;
                }
                if (!foundCross && ((prevBal < 0f && bal >= 0f) || (prevBal > 0f && bal <= 0f)))
                {
                    foundCross = true;
                    crossLo = prevT;
                    crossHi = t;
                }
                prevT = t;
                prevBal = bal;
            }

            if (foundCross)
            {
                var lo = crossLo;
                var hi = crossHi;
                var balLo = EvalWithT(lo, raising);
                for (var iter = 0; iter < 16; iter++)
                {
                    var mid = (lo + hi) * 0.5f;
                    var balMid = EvalWithT(mid, raising);
                    if (MathF.Abs(balMid) < bestAbs)
                    {
                        bestAbs = MathF.Abs(balMid);
                        bestT = mid;
                        bestBalance = balMid;
                    }
                    if ((balLo < 0f && balMid < 0f) || (balLo > 0f && balMid > 0f))
                    {
                        lo = mid;
                        balLo = balMid;
                    }
                    else
                    {
                        hi = mid;
                    }
                }
            }

            bestBalance = EvalWithT(bestT, raising);
            foreach (var line in allAdjustable)
            {
                var oldRate = saved[line.Id];
                var newRate = BudgetCore.RateOf(nation, line);
                if (MathF.Abs(newRate - oldRate) >= 0.0001f)
                    changes.Add(new PolicyRateChange(line.Id, line.Name, oldRate, newRate));
            }

            var summary = changes.Count == 0
                ? Loc.Get("No adjustable lines can move the balance further.")
                : Loc.Format("{0} line(s) adjusted · projected balance {1}", changes.Count, Fmt.MoneyDelta(bestBalance));
            return new ZeroBudgetPlan(changes, startBalance, bestBalance, adjustTaxes, summary);
        }
        finally
        {
            foreach (var line in allAdjustable)
                BudgetCore.SetRate(nation, line, saved[line.Id]);
        }
    }
}
