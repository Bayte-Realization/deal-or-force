using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>Presentation of the same read-only gate the frontline uses. No cached world state.</summary>
public static class ArmyAssaultPresentation
{
    public static string Description(Campaign campaign, Army army)
    {
        if (army?.InBattle == true) return Loc.Get("Already in battle; readiness is checked before a new assault.");
        if (army?.EmbarkedFleetId >= 0)
            return Loc.Get("Embarked: this formation cannot launch a land assault until it disembarks.");
        if (army?.SupportOnly == true)
            return Loc.Get("Support fire only: this order helps a battle but does not occupy empty ground.");
        if (army?.Route.Count > 0)
            return Loc.Format("March order: {0:N0} route step(s) remain. Arrival at the assigned position is still pending.", army.Route.Count);
        var assessment = AssaultReadiness.Assess(campaign, army);
        return assessment.Reason switch
        {
            AssaultReadiness.Reason.Ready => Loc.Get("Minimum assault readiness met; a reachable defended target is still needed. This is not a prediction of victory."),
            AssaultReadiness.Reason.DirectOrder => Loc.Get("An explicit order overrides the automatic readiness floor. Low supply or organisation still weakens the attack."),
            AssaultReadiness.Reason.PlanPaused => PausedPlan(campaign, army),
            AssaultReadiness.Reason.Recovering => Loc.Format("Recovering: {0:N0} day(s) before a new automatic assault.", assessment.RecoveryDays),
            AssaultReadiness.Reason.LowOrganisation => Loc.Format("Waiting for organisation: {0:P1} available; more than {1:P1} required. Hold the line or use reserves to recover.", assessment.Organisation, assessment.MinimumOrganisation),
            AssaultReadiness.Reason.LowSupply => Loc.Format("Waiting for supply: {0:P1} available; more than {1:P1} required. Reduce crowding or improve the supply corridor.", assessment.Supply, assessment.MinimumSupply),
            AssaultReadiness.Reason.NoAssaultOrder => Loc.Get("This formation has no automatic assault order."),
            _ => Loc.Get("No current formation is available for an assault.")
        };
    }

    private static string PausedPlan(Campaign campaign, Army army)
    {
        var nation = campaign?.NationById(army.NationId);
        if (nation is not null && (!nation.IsHuman || Delegation.Holds(nation, Delegation.Command)))
            return Loc.Format("The AI has not started this offensive plan ({0:P0} prepared). Readiness and strategy still determine when it can attack.", army.Planning);
        return Loc.Get("The offensive plan is paused. Execute resumes it; Prepare keeps the formation on its line.");
    }

    public static string Summary(Campaign campaign, IReadOnlyList<Army> armies)
    {
        if (armies is null || armies.Count == 0) return Loc.Get("No current formation is available for an assault.");
        if (armies.Count == 1) return Description(campaign, armies[0]);
        var engaged = 0; var embarked = 0; var support = 0; var marching = 0;
        var ready = 0; var direct = 0; var paused = 0;
        var recovering = 0; var organisation = 0; var supply = 0;
        // One live census; do not allocate a readiness array and traverse it six times
        // whenever the selection refreshes. Activity takes precedence just as above.
        foreach (var army in armies)
        {
            if (army is null) continue;
            if (army.InBattle) { engaged++; continue; }
            if (army.EmbarkedFleetId >= 0) { embarked++; continue; }
            if (army.SupportOnly) { support++; continue; }
            if (army.Route.Count > 0) { marching++; continue; }
            switch (AssaultReadiness.Assess(campaign, army).Reason)
            {
                case AssaultReadiness.Reason.Ready: ready++; break;
                case AssaultReadiness.Reason.DirectOrder: direct++; break;
                case AssaultReadiness.Reason.PlanPaused: paused++; break;
                case AssaultReadiness.Reason.Recovering: recovering++; break;
                case AssaultReadiness.Reason.LowOrganisation: organisation++; break;
                case AssaultReadiness.Reason.LowSupply: supply++; break;
            }
        }
        var lines = new List<string> { Loc.Format("Automatic assault: {0:N0} of {1:N0} formations meet the minimum.", ready, armies.Count) };
        if (engaged > 0) lines.Add(Loc.Format("Already fighting: {0:N0}", engaged));
        if (embarked > 0) lines.Add(Loc.Format("Embarked: {0:N0}", embarked));
        if (support > 0) lines.Add(Loc.Format("Support fire only: {0:N0}", support));
        if (marching > 0) lines.Add(Loc.Format("Marching: {0:N0}", marching));
        if (direct > 0) lines.Add(Loc.Format("Explicit orders: {0:N0}; automatic health floors do not apply.", direct));
        if (paused > 0) lines.Add(Loc.Format("Plans paused: {0:N0}", paused));
        if (recovering > 0) lines.Add(Loc.Format("Recovering: {0:N0}", recovering));
        if (organisation > 0) lines.Add(Loc.Format("Waiting for organisation: {0:N0}", organisation));
        if (supply > 0) lines.Add(Loc.Format("Waiting for supply: {0:N0}", supply));
        return string.Join("\n", lines);
    }
}
