using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    // Presentation thresholds only. They never change a market, war or political rule.
    public float SituationSupplyWarning { get; set; } = .45f;
    public float SituationShortageWarning { get; set; } = .90f;
    public float SituationShortageCritical { get; set; } = .65f;
    public float SituationCashRunwayWeeks { get; set; } = 4f;
    public int SituationExpiringDays { get; set; } = 3;
    public int SituationItemLimit { get; set; } = 40;
}

public enum SituationPriority { Critical, Warning, Awaiting, Opportunity }
public enum SituationArea { Foreign, Society, Economy, Forces, Development }
public enum SituationRoute { Inbox, PeaceOffer, Organization, Crises, Finance, Market, Army, Conflict, Construction, Assistance }
public sealed record SituationFact(string Label, string Value);
public sealed record SituationItem(string Id, SituationPriority Priority, SituationArea Area,
    string Title, string Summary, string[] Causes, string[] Consequences, SituationFact[] Facts,
    SituationRoute Route, string Action, int TargetId = -1, Commodity? Good = null, int DeadlineDay = -1);

/// <summary>Read-only explanation of existing systems. No random rolls, commands or hidden state initialization.</summary>
public static class SituationReport
{
    public static IReadOnlyList<SituationItem> Read(Campaign c, Nation n, bool observer = false)
    {
        if (c is null || n is null) return Array.Empty<SituationItem>();
        var items = new List<SituationItem>();
        var d = c.Defines;
        var expiring = Math.Clamp(d.SituationExpiringDays, 0, 60);
        string T(string text) => Loc.Get(text);
        SituationFact F(string label, string value) => new(T(label), value);
        SituationPriority Due(int day) => day - c.ElapsedDays <= expiring
            ? SituationPriority.Critical : SituationPriority.Awaiting;

        if (!observer)
        {
            foreach (var request in c.AssistanceRequests.Where(r => r.State == ForeignAssistanceState.Pending &&
                         r.DonorId == n.Id && r.ExpiresDay > c.ElapsedDays).OrderBy(r => r.ExpiresDay).ThenBy(r => r.Id).Take(8))
            {
                var from = c.NationById(request.RequesterId);
                if (from is null) continue;
                var preview = ForeignAssistance.Assess(c, request.RequesterId, request.DonorId, request.Amount, request.WarId);
                items.Add(new("assistance:" + request.Id, Due(request.ExpiresDay), SituationArea.Foreign,
                    Loc.Format("Financial request from {0}", from.DisplayName), T("The donor's approval is still missing."),
                    new[] { T("Another government has asked for funds. The request reserves no money.") },
                    new[] { T("A cash grant is not a loan, arms shipment or entry into a war."),
                        T("Approval rechecks current cash and cannot exceed the amount you confirmed.") },
                    new[] { F("Requested", Fmt.Money(request.Amount)), F("Available package now", Fmt.Money(preview.PayableAmount)),
                        F("Answer within", Loc.Format("{0} days", request.ExpiresDay - c.ElapsedDays)) },
                    SituationRoute.Assistance, T("Review the financial request"), request.Id, DeadlineDay: request.ExpiresDay));
            }
            // Existing obligations stay visible even when new deals are disabled: decline is still legal.
            foreach (var offer in c.DealOffers.Where(o => o.Pending && o.ToId == n.Id && o.ExpiresDay > c.ElapsedDays)
                         .OrderBy(o => o.ExpiresDay).ThenBy(o => o.Id).Take(8))
            {
                var from = c.NationById(offer.FromId);
                if (from is null) continue;
                items.Add(new("deal:" + offer.Id, Due(offer.ExpiresDay), SituationArea.Foreign,
                    Loc.Format("Offer from {0}", from.DisplayName), T("Your signature is still missing."),
                    new[] { T("Another government submitted terms. Delivery did not transfer money or land.") },
                    new[] { T("Acceptance rechecks ownership and funds. You may decline or propose different terms."),
                        T("An unanswered offer expires without a transfer.") },
                    new[] { F("Answer within", Loc.Format("{0} days", offer.ExpiresDay - c.ElapsedDays)) },
                    SituationRoute.Inbox, T("Review the offer"), offer.Id, DeadlineDay: offer.ExpiresDay));
            }
            foreach (var war in PeaceOffers.AwaitingAnswer(c, n.Id)
                         .Where(w => w.PendingOfferExpiresDay > c.ElapsedDays).OrderBy(w => w.PendingOfferExpiresDay).Take(8))
                items.Add(new("peace:" + war.Id, Due(war.PendingOfferExpiresDay), SituationArea.Foreign,
                    T("Peace terms await an answer"), PeaceOffers.Describe(c, war),
                    new[] { T("The opposing government has proposed a settlement to your country.") },
                    new[] { T("The war continues until a valid agreement is signed. An expired offer does not end it.") },
                    new[] { F("Answer within", Loc.Format("{0} days", war.PendingOfferExpiresDay - c.ElapsedDays)) },
                    SituationRoute.PeaceOffer, T("Read the peace terms"), war.Id, DeadlineDay: war.PendingOfferExpiresDay));
        }

        if (c.FeatureEnabled("organizations"))
            foreach (var o in InternationalOrganizations.Active(c).OrderBy(o => o.Id))
            {
                var name = InternationalOrganizations.DisplayName(c, o);
                if (!observer && o.Invitations.Contains(n.Id))
                    items.Add(new("invitation:" + o.Id, SituationPriority.Awaiting, SituationArea.Foreign,
                        Loc.Format("Invitation: {0}", name), T("Membership needs your consent."),
                        new[] { T("The organization has approved an invitation to your country.") },
                        new[] { T("Joining brings the charter's obligations and weekly dues. An invitation alone creates no membership.") },
                        new[] { F("Common fund", Fmt.Money(o.Budget)) }, SituationRoute.Organization, T("Inspect the charter"), o.Id));
                if (!o.Members.Contains(n.Id)) continue;
                if (!observer && o.Motion is { } motion && motion.EndsDay > c.ElapsedDays && !motion.Votes.ContainsKey(n.Id))
                    items.Add(new("vote:" + o.Id + ":" + motion.Id, Due(motion.EndsDay), SituationArea.Foreign,
                        Loc.Format("Vote in {0}", name), T("Your assembly vote has not been recorded."),
                        new[] { T("A member submitted a motion and the voting period is open.") },
                        new[] { T("The charter determines the majority. Opening the assembly does not cast your vote.") },
                        new[] { F("Answer within", Loc.Format("{0} days", motion.EndsDay - c.ElapsedDays)),
                            F("Votes recorded", motion.Votes.Count.ToString()) },
                        SituationRoute.Organization, T("Open the assembly"), o.Id, DeadlineDay: motion.EndsDay));
                var unpaid = o.UnpaidWeeks.GetValueOrDefault(n.Id);
                if (unpaid > 0 || o.Cohesion < .25f)
                    items.Add(new("organization:" + o.Id, o.Cohesion < .25f ? SituationPriority.Critical : SituationPriority.Warning,
                        SituationArea.Foreign, Loc.Format("Strained institution: {0}", name), T("Membership is not the same as a functioning institution."),
                        new[] { unpaid > 0 ? T("Your country has missed weekly dues.") : T("Institutional cohesion has fallen below the defence threshold.") },
                        new[] { T("Arrears erode cohesion. Collective defence is suspended below 25% cohesion.") },
                        new[] { F("Unpaid weeks", unpaid.ToString()), F("Institutional cohesion", o.Cohesion.ToString("P0")) },
                        SituationRoute.Organization, T("Review obligations"), o.Id));
            }

        if (n.Movement is not { Landless: true } && c.FeatureEnabled("politics"))
        {
            if (c.FeatureEnabled("coups") && MechanicFeatures.SystemEnabled(c, nameof(Coups)) && n.CoupPlotDay >= 0)
                items.Add(new("military_plot", SituationPriority.Critical, SituationArea.Society,
                    T("Military plot under way"), T("Officers are organizing against the government."),
                    new[] { T("The military plot system has recorded an active plot."),
                        Loc.Format("Stability {0:P1}; the plot threshold is {1:P1}.", n.Stability, d.CoupStabilityThreshold) },
                    new[] { T("Restored stability can prevent the transition. Reviews are periodic, not a guaranteed coup date.") },
                    new[] { F("Plot age", Loc.Format("{0} days", Math.Max(0, c.ElapsedDays - n.CoupPlotDay))),
                        F("Plot maturation", Loc.Format("{0} days", d.CoupPlotDays)) }, SituationRoute.Crises, T("Review institutional responses")));
            if (c.FeatureEnabled("domestic_crises"))
            {
                var crisis = DomesticCrises.Read(c, n);
                if (crisis.CoupEligible && crisis.CooldownDays == 0 && crisis.CoupPressure >= crisis.CoupThreshold * .8f)
                    items.Add(new("institutional_pressure", crisis.CoupPressure >= crisis.CoupThreshold
                            ? SituationPriority.Critical : SituationPriority.Warning, SituationArea.Society,
                        T("Institutions under pressure"), T("Sustained strain can force a political transition."),
                        crisis.Drivers.Length > 0 ? crisis.Drivers : new[] { T("Accumulated institutional strain remains high.") },
                        new[] { T("Pressure must persist and political eligibility must hold. A response reduces pressure over time, not instantly.") },
                        new[] { F("Coup pressure", Loc.Format("{0:P1} / {1:P1}", crisis.CoupPressure, crisis.CoupThreshold)),
                            F("Critical pressure sustained", Loc.Format("{0} / {1} days", crisis.DaysAboveThreshold, crisis.RequiredDays)) },
                        SituationRoute.Crises, T("Review institutional responses")));
            }
            if (CivilWarOutbreak.EligibilityReason(c, n).Length == 0)
            {
                var chance = CivilWarOutbreak.Chance(c, n);
                if (chance > 0)
                    items.Add(new("civil_war", SituationPriority.Warning, SituationArea.Society,
                        T("Armed opposition could emerge"), T("The country meets the simulation's outbreak conditions."),
                        new[] { Loc.Format("Stability {0:P1}; the outbreak threshold is {1:P1}.", n.Stability, d.CivilWarStabilityThreshold) },
                        new[] { T("A weekly review may start a territorial civil war. This probability is not a countdown or a prediction.") },
                        new[] { F("Weekly outbreak chance", chance.ToString("P2")) }, SituationRoute.Crises, T("Review institutional responses")));
            }
        }

        if (n.Movement is not { Landless: true } && c.FeatureEnabled("public_debt"))
        {
            var budget = SovereignBalanceSheet.Read(c, n);
            var gap = budget.FundingGapAtCurrentLimits;
            if (budget.Arrears > .000001f || gap > .000001f ||
                budget.CashRunwayWeeks is { } runway && runway <= Math.Clamp(d.SituationCashRunwayWeeks, 0, 52))
                items.Add(new("financing", budget.Arrears > .000001f || gap > .000001f
                        ? SituationPriority.Critical : SituationPriority.Warning, SituationArea.Economy,
                    T("Public finances need attention"), gap > .000001f ? T("Current cash and credit do not cover the next budget settlement.") :
                        budget.Arrears > .000001f ? T("Public obligations are overdue.") : T("The current deficit is consuming the cash reserve."),
                    new[] { T("This assessment uses the current weekly budget, cash, arrears and available borrowing.") },
                    new[] { T("If conditions stay unchanged, an uncovered payment can add arrears. Revenue, spending and credit can change before settlement.") },
                    new[] { F("Weekly balance", Fmt.MoneyDelta(budget.BudgetBalance)), F("Overdue payments", Fmt.Money(budget.Arrears)),
                        F("Unfunded next settlement", Fmt.Money(gap)), F("Cash runway without borrowing", budget.CashRunwayWeeks is { } weeks
                            ? Loc.Format("{0:0.0} weeks at the current budget", weeks) : T("No current cash drawdown")) },
                    SituationRoute.Finance, T("Inspect the balance sheet")));
        }

        var shortageWarning = Threshold(d.SituationShortageWarning, .90f);
        var shortageCritical = MathF.Min(shortageWarning, Threshold(d.SituationShortageCritical, .65f));
        foreach (var quote in n.Market.Values.Where(q => q.Demand > .001f && float.IsFinite(q.Satisfaction) && q.Satisfaction < shortageWarning)
                     .OrderBy(q => q.Satisfaction).ThenBy(q => q.Commodity).Take(3))
        {
            var good = CommodityCatalog.Get(quote.Commodity);
            items.Add(new("shortage:" + quote.Commodity, quote.Satisfaction < shortageCritical
                    ? SituationPriority.Critical : SituationPriority.Warning, SituationArea.Economy,
                Loc.Format("Shortage: {0}", T(good.Name)), T("Available supply is below current demand."),
                new[] { T("The latest market clearing records production, imports, exports and demand."),
                    T("A missing good may be a production problem, a trade bottleneck or higher demand; inspect the market before choosing a response.") },
                new[] { T("Shortages can constrain households or production chains according to the commodity's uses.") },
                new[] { F("Demand met", quote.Satisfaction.ToString("P0")), F("Domestic production", quote.Production.ToString("N2")),
                    F("Imports / exports", Loc.Format("{0:N2} / {1:N2}", quote.Imports, quote.Exports)), F("Demand", quote.Demand.ToString("N2")) },
                SituationRoute.Market, T("Trace supply and demand"), Good: quote.Commodity));
        }

        var supplyThreshold = Threshold(d.SituationSupplyWarning, .45f);
        var armies = c.Armies.Where(a => a.NationId == n.Id && (a.CutOff || a.SupplyLevel < supplyThreshold))
            .OrderByDescending(a => a.CutOff).ThenBy(a => a.SupplyLevel).ThenBy(a => a.Id).ToArray();
        if (armies.Length > 0)
            items.Add(new("supply", armies.Any(a => a.CutOff) ? SituationPriority.Critical : SituationPriority.Warning,
                SituationArea.Forces, Loc.Format("Supply trouble: {0} formations", armies.Length), T("Field formations are undersupplied or cut off."),
                new[] { T("The army's current supply state is below the warning level or its route is cut.") },
                new[] { T("Supply shortages weaken formations. Inspect the route, congestion and local capacity before sending more troops.") },
                new[] { F("Cut off", armies.Count(a => a.CutOff).ToString()), F("Lowest supply", armies.Min(a => a.SupplyLevel).ToString("P0")) },
                SituationRoute.Army, T("Inspect the worst supplied formation"), armies[0].Id));

        if (c.FeatureEnabled("escalation"))
        {
            var hot = Escalation.Hottest(c, d.TensionCrisisAt).FirstOrDefault(r => (r.A == n.Id || r.B == n.Id) &&
                !CampaignDiplomacy.AtWar(c, r.A, r.B));
            if (hot is not null && c.NationById(hot.A == n.Id ? hot.B : hot.A) is { } rival)
                items.Add(new("standoff:" + rival.Id, (TensionStage)hot.TensionStage >= TensionStage.Clashes
                        ? SituationPriority.Critical : SituationPriority.Warning, SituationArea.Foreign,
                    Loc.Format("Dangerous standoff: {0}", rival.DisplayName), T("Tension has reached the crisis stage or beyond."),
                    new[] { T("This warning reads the actual bilateral escalation state, not diplomatic opinion alone.") },
                    new[] { T("Prolonged armed clashes can become a border war. Talks can reduce tension; success is not guaranteed.") },
                    new[] { F("Tension", hot.Tension.ToString("N1")) }, SituationRoute.Conflict, T("Inspect escalation and talks"), rival.Id));
        }

        if (c.FeatureEnabled("construction") && n.Movement is not { Landless: true } && c.StatesOf(n).Length > 0 &&
            !c.Construction.Any(p => p.NationId == n.Id))
            items.Add(new("idle_construction", SituationPriority.Opportunity, SituationArea.Development,
                T("Construction capacity is idle"), T("No construction project is running in your country."),
                new[] { T("The construction queue is empty. This is an opportunity, not an emergency.") },
                new[] { T("A new project consumes real money over time. Check the budget and the region's needs first.") },
                Array.Empty<SituationFact>(), SituationRoute.Construction, T("Choose a development project")));

        return items.OrderBy(i => i.Priority).ThenBy(i => i.DeadlineDay < 0 ? int.MaxValue : i.DeadlineDay)
            .ThenBy(i => i.Id, StringComparer.Ordinal).Take(Math.Clamp(d.SituationItemLimit, 8, 80)).ToArray();
    }

    private static float Threshold(float value, float fallback) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;
}

/// <summary>UI-local attention memory: changing a number or language is not a new emergency.</summary>
public sealed class SituationAttention
{
    private int _nation = -1;
    private HashSet<string> _critical = new(StringComparer.Ordinal);
    public void Reset() { _nation = -1; _critical.Clear(); }
    public bool Observe(int nationId, IReadOnlyList<SituationItem> items)
    {
        var now = items.Where(i => i.Priority == SituationPriority.Critical).Select(i => i.Id).ToHashSet(StringComparer.Ordinal);
        var newEmergency = nationId == _nation && now.Except(_critical).Any();
        _nation = nationId; _critical = now;
        return newEmergency;
    }
}
