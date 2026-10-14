using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>Presentation of the same read-only gate the frontline uses. No cached world state.</summary>
public static class ArmyAssaultPresentation
{
    public static string Description(Campaign campaign, Army army)
    {
        if (army?.InBattle == true) return Loc.Get("Already in battle; readiness is checked before a new assault.");
        var assessment = AssaultReadiness.Assess(campaign, army);
        return assessment.Reason switch
        {
            AssaultReadiness.Reason.Ready => Loc.Get("Minimum assault readiness met; a reachable defended target is still needed. This is not a prediction of victory."),
            AssaultReadiness.Reason.DirectOrder => Loc.Get("An explicit order overrides the automatic readiness floor. Low supply or organisation still weakens the attack."),
            AssaultReadiness.Reason.PlanPaused => Loc.Get("The offensive plan is paused. Execute resumes it; Prepare keeps the formation on its line."),
            AssaultReadiness.Reason.Recovering => Loc.Format("Recovering: {0:N0} day(s) before a new automatic assault.", assessment.RecoveryDays),
            AssaultReadiness.Reason.LowOrganisation => Loc.Format("Waiting for organisation: {0:P1} available; more than {1:P1} required. Hold the line or use reserves to recover.", assessment.Organisation, assessment.MinimumOrganisation),
            AssaultReadiness.Reason.LowSupply => Loc.Format("Waiting for supply: {0:P1} available; more than {1:P1} required. Reduce crowding or improve the supply corridor.", assessment.Supply, assessment.MinimumSupply),
            AssaultReadiness.Reason.NoAssaultOrder => Loc.Get("This formation has no automatic assault order."),
            _ => Loc.Get("No current formation is available for an assault.")
        };
    }

    public static string Summary(Campaign campaign, IReadOnlyList<Army> armies)
    {
        if (armies is null || armies.Count == 0) return Loc.Get("No current formation is available for an assault.");
        if (armies.Count == 1) return Description(campaign, armies[0]);
        var engaged = armies.Count(a => a?.InBattle == true);
        var assessments = armies.Where(a => a is not null && !a.InBattle)
            .Select(a => AssaultReadiness.Assess(campaign, a)).ToArray();
        var ready = assessments.Count(a => a.Reason == AssaultReadiness.Reason.Ready);
        var direct = assessments.Count(a => a.Reason == AssaultReadiness.Reason.DirectOrder);
        var paused = assessments.Count(a => a.Reason == AssaultReadiness.Reason.PlanPaused);
        var recovering = assessments.Count(a => a.Reason == AssaultReadiness.Reason.Recovering);
        var organisation = assessments.Count(a => a.Reason == AssaultReadiness.Reason.LowOrganisation);
        var supply = assessments.Count(a => a.Reason == AssaultReadiness.Reason.LowSupply);
        var lines = new List<string> { Loc.Format("Automatic assault: {0:N0} of {1:N0} formations meet the minimum.", ready, armies.Count) };
        if (engaged > 0) lines.Add(Loc.Format("Already fighting: {0:N0}", engaged));
        if (direct > 0) lines.Add(Loc.Format("Explicit orders: {0:N0}; automatic health floors do not apply.", direct));
        if (paused > 0) lines.Add(Loc.Format("Plans paused: {0:N0}", paused));
        if (recovering > 0) lines.Add(Loc.Format("Recovering: {0:N0}", recovering));
        if (organisation > 0) lines.Add(Loc.Format("Waiting for organisation: {0:N0}", organisation));
        if (supply > 0) lines.Add(Loc.Format("Waiting for supply: {0:N0}", supply));
        return string.Join("\n", lines);
    }
}
