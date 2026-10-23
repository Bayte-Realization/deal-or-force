using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public float CoupInstabilityWeight { get; set; } = .65f;
    public float CoupRadicalismWeight { get; set; } = .30f;
    public float CoupCapitalUnrestWeight { get; set; } = .10f;
    public float CoupDefeatWeight { get; set; } = .25f;
    public float CoupDefaultWeight { get; set; } = .10f;
    public float CoupPoliticalMismatchWeight { get; set; } = .05f;
    public float CoupOverstretchWeight { get; set; } = .15f;
    public float CrisisDialoguePressureRelief { get; set; } = .12f;
    public float CrisisReviewPressureRelief { get; set; } = .18f;
}

public static partial class DomesticCrises
{
    private static float Target(Campaign c, Nation n, float radicalism, float capitalAnger, List<string> drivers = null)
    {
        var d = c.Defines;
        var target = (1f - M.Clamp01(n.Stability)) * d.CoupInstabilityWeight +
            radicalism * d.CoupRadicalismWeight + capitalAnger * d.CoupCapitalUnrestWeight;
        if (n.Stability < .4f) drivers?.Add("Low institutional stability");
        if (radicalism > .25f) drivers?.Add("Widespread radicalisation");
        if (capitalAnger > .35f) drivers?.Add("Capital unrest");
        if (HasDefeat(c, n))
        { target += d.CoupDefeatWeight; drivers?.Add("War defeat"); }
        if (n.DebtCondition is DebtCondition.Default or DebtCondition.Insolvent && c.FeatureEnabled("public_debt"))
        { target += d.CoupDefaultWeight; drivers?.Add("Debt payment crisis"); }
        if (PoliticsCore.IsMismatched(n))
        { target += d.CoupPoliticalMismatchWeight; drivers?.Add("Political mismatch"); }
        if (n.ImperialOverstretch > 0.05f)
        { target += n.ImperialOverstretch * d.CoupOverstretchWeight; drivers?.Add("Imperial overstretch"); }
        var mood = PoliticalCohorts.Mood(c, n);
        if (mood.Reviewed && mood.Government < d.ScandalApproval)
        {
            target += (d.ScandalApproval - mood.Government) * MathF.Max(0f, d.CoupGovernmentDisapprovalWeight);
            drivers?.Add("The governed do not accept the government");
        }
        var memory = n.DomesticCrisis;
        if (memory is not null && memory.ResponseUntilDay > c.ElapsedDays)
        {
            if (memory.ResponseId == "public_dialogue")
            { target -= d.CrisisDialoguePressureRelief; drivers?.Add("Public dialogue in progress"); }
            if (memory.ResponseId == "institutional_review")
            { target -= d.CrisisReviewPressureRelief; drivers?.Add("Institutional review in progress"); }
            if (memory.ConciliationUntilDay > c.ElapsedDays)
                drivers?.Add("A region is being settled with money");
        }
        return M.Clamp01(target);
    }

    private static bool HasDefeat(Campaign c, Nation n)
    {
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(n.Id) && (war.IsAttacking(n.Id) ? war.WarScore : -war.WarScore) < -15f)
                return true;
        return false;
    }

    // The simulation reads numbers, not UI row objects for all 1,700 regions every day.
    private static float PressureTarget(Campaign c, Nation n)
    {
        double people = 0, radicalism = 0;
        var capitalAnger = 0f;
        foreach (var region in c.StatesOf(n))
        {
            if (region.MapIndex == n.CapitalStateMapIndex) capitalAnger = M.Clamp01(region.Radicalism);
            if (region.Population <= 0) continue;
            people += region.Population;
            radicalism += region.Population * (double)M.Clamp01(region.Radicalism);
        }
        return Target(c, n, people > 0 ? (float)(radicalism / people) : 0f, capitalAnger);
    }

    private static bool Eligible(Campaign c, Nation n) => c.FeatureEnabled("domestic_crises") &&
        c.FeatureEnabled("politics") && MechanicFeatures.SystemEnabled(c, nameof(Coups)) &&
        !n.IsSubject && n.Movement is null && n.FactionId.Length == 0 && !c.Fallen.Contains(n.Id) &&
        n.CoupPlotDay < 0 && c.ElapsedDays - (long)n.LastCoupDay >= Math.Max(0, c.Defines.CoupGraceDays) &&
        c.StatesOf(n).Length > 0 &&
        GovernanceEvents.All.Any(t => t.Crisis == "coup" && t.Weight > 0f && GovernanceEvents.Eligible(c, n, t));

    private static void ReviewPressure(Campaign c, Nation nation, int days)
    {
        var memory = nation.DomesticCrisis;
        var target = PressureTarget(c, nation);
        var before = memory.CoupPressure;
        memory.CoupPressure = Move(M.Clamp01(before), target, days * MathF.Max(0f,
            target > before ? c.Defines.CoupPressureRisePerDay : c.Defines.CoupPressureRecoveryPerDay));
        if (target < Threshold(c) || memory.CooldownUntilDay > c.ElapsedDays)
        { memory.DaysAboveThreshold = 0; return; }
        if (memory.CoupPressure < Threshold(c)) { memory.DaysAboveThreshold = 0; return; }
        if (!Eligible(c, nation)) { memory.DaysAboveThreshold = 0; return; }
        if (before < Threshold(c)) c.Chronicle.Record(c, "institutional_crisis_warning", nation.Id);
        var pressuredDays = days;
        if (before < Threshold(c))
            pressuredDays = Math.Clamp(days - (int)MathF.Ceiling((Threshold(c) - before) /
                MathF.Max(.000001f, c.Defines.CoupPressureRisePerDay)) + 1, 0, days);
        var required = Math.Max(1, c.Defines.CoupRequiredPressureDays);
        memory.DaysAboveThreshold = (int)Math.Min(required, (long)Math.Max(0, memory.DaysAboveThreshold) + pressuredDays);
        if (memory.DaysAboveThreshold < required) return;
        var choices = GovernanceEvents.All.Where(t => t.Crisis == "coup" && t.Weight > 0f &&
            GovernanceEvents.Eligible(c, nation, t)).ToArray();
        if (choices.Length == 0) return;
        var total = choices.Sum(t => t.Weight);
        var roll = c.Random.NextFloat() * total;
        var chosen = choices[^1];
        foreach (var candidate in choices)
        { roll -= candidate.Weight; if (roll <= 0f) { chosen = candidate; break; } }
        if (!GovernanceEvents.ApplyTransition(c, nation, chosen)) return;
        Coups.RegisterOutcome(c, nation, chosen.Id);
        c.Chronicle.Record(c, "institutional_coup", nation.Id);
        c.MarkStrategicChange();
    }
}
