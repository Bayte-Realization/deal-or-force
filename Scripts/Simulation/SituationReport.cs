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
            foreach (var demand in c.UltimatumPapers.Where(paper => paper.Pending && paper.ToId == n.Id && paper.ExpiresDay > c.ElapsedDays)
                         .OrderBy(paper => paper.ExpiresDay).ThenBy(paper => paper.Id).Take(4))
            {
                var demander = c.NationById(demand.FromId);
                if (demander is null) continue;
                items.Add(new("ultimatum:" + demand.Id, Due(demand.ExpiresDay), SituationArea.Foreign,
                    Loc.Format("Ultimatum from {0}", demander.DisplayName),
                    T("They want this before a war. Nothing has been handed over."),
                    new[] { T("The demand is the land on the paper. Yielding gives it over and buys a truce. Refusing keeps the land and gives them a reason.") },
                    new[] { T("If the days run out, the demand is refused. The land stays, and they may then go to war.") },
                    new[] { F("Demand", Ultimatum.Summary(c, demand.Terms)),
                        F("Answer within", Loc.Format("{0} days", demand.ExpiresDay - c.ElapsedDays)) },
                    SituationRoute.Inbox, T("Read the ultimatum"), demand.Id, DeadlineDay: demand.ExpiresDay));
            }
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
                    new[] { T("Officers are plotting a coup."),
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
                {
                    var causes = new List<string>
                    {
                        Loc.Format("Stability {0:P1}; the outbreak threshold is {1:P1}.", n.Stability, d.CivilWarStabilityThreshold)
                    };
                    foreach (var factor in CivilWarOutbreak.Factors(c, n)) causes.Add(T(factor));
                    items.Add(new("civil_war", SituationPriority.Warning, SituationArea.Society,
                        T("Armed opposition could emerge"), T("The country meets the simulation's outbreak conditions."),
                        causes.ToArray(),
                        new[] { T("A weekly review may start a territorial civil war. This probability is not a countdown or a prediction.") },
                        new[] { F("Weekly outbreak chance", chance.ToString("P2")) }, SituationRoute.Crises, T("Review institutional responses")));
                }
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
                    EconomicExposure.Cause(c, n, quote.Commodity) is { Length: > 0 } why
                        ? why
                        : T("The week's partners are not on the books yet.") },
                new[] { T("Shortages can constrain households or production chains according to the commodity's uses.") },
                new[] { F("Demand met", quote.Satisfaction.ToString("P0")), F("Domestic production", quote.Production.ToString("N2")),
                    F("Imports / exports", Loc.Format("{0:N2} / {1:N2}", quote.Imports, quote.Exports)), F("Demand", quote.Demand.ToString("N2")) },
                SituationRoute.Market, T("Trace supply and demand"), Good: quote.Commodity));
        }

        var supplyThreshold = Threshold(d.SituationSupplyWarning, .45f);
        var armies = c.Armies.Where(a => a.NationId == n.Id && (a.CutOff || a.SupplyLevel < supplyThreshold))
            .OrderByDescending(a => a.CutOff).ThenBy(a => a.SupplyLevel).ThenBy(a => a.Id).ToArray();
        if (armies.Length > 0)
        {
            var causes = new List<string> { T("The army's current supply state is below the warning level or its route is cut.") };
            if (armies[0].ProvinceId > 0 && c.ProvinceMap is { } roads)
            {
                var season = WaterConditions.RoadThroughput(c, roads.CenterOf(armies[0].ProvinceId).Latitude);
                if (season < 0.97f)
                    causes.Add(Loc.Format("Winter roads there carry {0:P0} of fair-weather traffic.", season));
            }
            items.Add(new("supply", armies.Any(a => a.CutOff) ? SituationPriority.Critical : SituationPriority.Warning,
                SituationArea.Forces, Loc.Format("Supply trouble: {0} formations", armies.Length), T("Field formations are undersupplied or cut off."),
                causes.ToArray(),
                new[] { T("Supply shortages weaken formations. Inspect the route, congestion and local capacity before sending more troops.") },
                new[] { F("Cut off", armies.Count(a => a.CutOff).ToString()), F("Lowest supply", armies.Min(a => a.SupplyLevel).ToString("P0")) },
                SituationRoute.Army, T("Inspect the worst supplied formation"), armies[0].Id));
        }

        if (c.FeatureEnabled("escalation"))
        {
            // One pair: the hottest crisis or clashes this country is in. A limited war past
            // that list still counts, because its ceasefire day is already on the war.
            // A full war is a peace conference, and a calm world adds nothing.
            var hot = PlayerEscalation(c, n);
            if (hot is not null && c.NationById(hot.A == n.Id ? hot.B : hot.A) is { } rival)
            {
                var stage = Escalation.StageOf(hot);
                var stageName = T(EscalationStageName(stage));
                var war = CampaignDiplomacy.WarBetween(c, hot.A, hot.B);
                int? ceasefire = war is { Limited: true, LimitedUntilDay: >= 0 }
                    ? Math.Max(0, war.LimitedUntilDay - c.ElapsedDays) : null;
                int? talks = hot.TalksUntilDay > c.ElapsedDays ? hot.TalksUntilDay - c.ElapsedDays : null;
                var incident = IncidentText(hot);
                var summary = Loc.Format("{0}: {1}.", rival.DisplayName, stageName);
                if (talks is int talkDays)
                    summary += " " + Loc.Format("Talks run for another {0} days.", talkDays);
                if (ceasefire is int due)
                    summary += " " + Loc.Format("Border war: the ceasefire on the old lines falls due in {0} days.", due);
                if (incident.Length > 0)
                    summary += " " + Loc.Format("The last incident was {0}.", incident);
                var facts = new List<SituationFact> { F("Stage", stageName), F("Tension", hot.Tension.ToString("N1")) };
                if (talks is int talkLeft) facts.Add(F("Talks", Loc.Format("{0} days", talkLeft)));
                if (ceasefire is int ceasefireLeft) facts.Add(F("Ceasefire due", ceasefireLeft.ToString()));
                if (incident.Length > 0) facts.Add(F("Last incident", incident));
                var causes = new List<string> { T("Hostility, old grievances, disputed land and rivalry all raise tension.") };
                if (incident.Length > 0) causes.Add(Loc.Format("The last incident was {0}.", incident));
                // Hold talks spends the existing talks cost. The room only offers it when that
                // command would succeed. Otherwise the paper goes to the deal inbox.
                var canTalk = !observer && Escalation.TalksBlock(c, n, rival).Length == 0;
                var deadline = talks is int ? hot.TalksUntilDay : war is { Limited: true } ? war.LimitedUntilDay : -1;
                items.Add(new("standoff:" + rival.Id,
                    stage >= TensionStage.Clashes || ceasefire is not null
                        ? SituationPriority.Critical : SituationPriority.Warning,
                    SituationArea.Foreign,
                    Loc.Format("Dangerous standoff: {0}", rival.DisplayName),
                    summary,
                    causes.ToArray(),
                    new[] { T("Prolonged armed clashes can become a border war. Talks can reduce tension; success is not guaranteed.") },
                    facts.ToArray(),
                    canTalk || observer ? SituationRoute.Conflict : SituationRoute.Inbox,
                    canTalk ? Loc.Format("Hold talks ({0})", Fmt.Money(c.Defines.TalksCost))
                        : observer ? T("Inspect escalation and talks") : T("Open the deal inbox"),
                    rival.Id, DeadlineDay: deadline));
            }
        }

        if (n.Movement is not { Landless: true } && c.Defines.BrainDrainEnabled)
        {
            if (n.BrainDrainRate <= -0.08f && n.WeeklyNetMigration < 0)
            {
                var dest = c.NationById(n.BrainDrainDestinationId);
                var worstState = c.StatesOf(n).OrderByDescending(s => s.BrainDrainPressure).FirstOrDefault();
                items.Add(new("brain_drain", n.BrainDrainRate <= -0.22f ? SituationPriority.Critical : SituationPriority.Warning,
                    SituationArea.Society, T("Brain drain"), T("Skilled specialists and households are leaving the country."),
                    new[] { T("War damage, instability, low living standards or a lack of research and university positions are pushing educated residents abroad."),
                        dest is not null ? Loc.Format("The primary destination is {0}.", dest.DisplayName) : T("Migrants are relocating to safer, wealthier economies.") },
                    new[] { T("Continued emigration erodes population, literacy and long-term industrial productivity."),
                        T("Universities, research laboratories, higher stability and talent retention programmes anchor specialists at home.") },
                    new[] { F("Weekly outflow", Fmt.People(Math.Abs(n.WeeklyNetMigration))),
                        F("Cumulative migration", Fmt.PeopleDelta(n.CumulativeNetMigration)),
                        F("Brain drain pressure", Fmt.Percent(Math.Abs(n.BrainDrainRate))),
                        F("Most affected region", worstState?.DisplayName ?? "—") },
                    SituationRoute.Construction, T("Review regional development")));
            }
            else if (n.BrainDrainRate >= 0.08f && n.WeeklyNetMigration > 0)
            {
                var from = c.NationById(n.BrainGainSourceId);
                items.Add(new("brain_gain", SituationPriority.Opportunity, SituationArea.Development,
                    T("Talent inflow"), T("Skilled specialists from abroad are settling in your regions."),
                    new[] { from is not null ? Loc.Format("Peace, stability and higher living standards are attracting specialists from {0} and other states.", from.DisplayName)
                        : T("Peace, stability and higher living standards are attracting foreign specialists.") },
                    new[] { T("Incoming professionals expand the workforce and raise literacy and productivity.") },
                    new[] { F("Weekly inflow", "+" + Fmt.People(n.WeeklyNetMigration)),
                        F("Cumulative migration", Fmt.PeopleDelta(n.CumulativeNetMigration)) },
                    SituationRoute.Construction, T("Expand industrial and research capacity")));
            }
        }

        if (PoliticalStrain(c, n) is { } politicalStrain)
            items.Add(politicalStrain);

        if (Conciliation(c, n) is { } conciliation)
            items.Add(conciliation);

        if (c.FeatureEnabled("government_promises") && n.Promises.Count > 0)
        {
            foreach (var promise in n.Promises)
            {
                if (PromiseCatalog.Find(promise.Id) is not { } def) continue;
                var daysLeft = promise.DueDay - c.ElapsedDays;
                var met = def.Check is not null && def.Check.Matches(c, n);
                var priority = met
                    ? SituationPriority.Opportunity
                    : daysLeft <= 30 ? SituationPriority.Critical
                    : daysLeft <= 90 ? SituationPriority.Warning
                    : SituationPriority.Awaiting;

                items.Add(new("promise:" + promise.Id, priority, SituationArea.Society,
                    met ? Loc.Format("Promise on track: {0}", T(def.Name))
                        : Loc.Format("Government promise: {0}", T(def.Name)),
                    met ? T("The promised outcome is met and will conclude.")
                        : Loc.Format("{0} days remaining to achieve the outcome.", daysLeft),
                    new[] { T(def.Description) },
                    new[] { T("Fulfilling the promise raises public trust; breaking it damages government credibility.") },
                    new[] { F("Promised outcome", T(def.Name)),
                            F("Deadline in", Loc.Format("{0} days", daysLeft)),
                            F("Status now", met ? T("Fulfilled (pending conclusion)") : T("In progress")) },
                    SituationRoute.Crises, T("Review the government commitment"), DeadlineDay: promise.DueDay));
            }
        }

        if (n.Movement is not { Landless: true })
        {
            var ownedStates = c.StatesOf(n);
            var hotState = ownedStates.Where(s => PoliticalCohorts.UnrestHeat(s) >= 0.55f)
                .OrderByDescending(s => PoliticalCohorts.UnrestHeat(s)).FirstOrDefault();
            if (hotState is not null)
            {
                items.Add(new("regional_unrest:" + hotState.MapIndex, SituationPriority.Warning, SituationArea.Society,
                    Loc.Format("Regional unrest: {0}", hotState.DisplayName),
                    T("Local radicalism and secession pressure are reaching critical levels."),
                    new[] { T("Failure to conciliate or protect the region could provoke riots or separatism.") },
                    new[] { T("A break still needs a weak centre, a region that rejects it, and an economic stress already on the books.") },
                    new[] { F("Unrest heat", PoliticalCohorts.UnrestHeat(hotState).ToString("P0")),
                            F("Secession pressure", hotState.SecessionPressure.ToString("P0")),
                            F("Government approval", hotState.GovernmentApproval.ToString("P0")) },
                    SituationRoute.Crises, T("Inspect regional crisis"), hotState.MapIndex));
            }

            var ruinedState = ownedStates.Where(s => s.WarDamage >= 0.35f)
                .OrderByDescending(s => s.WarDamage).FirstOrDefault();
            if (ruinedState is not null)
            {
                items.Add(new("regional_damage:" + ruinedState.MapIndex, SituationPriority.Warning, SituationArea.Development,
                    Loc.Format("Devastated region: {0}", ruinedState.DisplayName),
                    T("Extensive war destruction is paralyzing the regional economy."),
                    new[] { T("Unreconstructed infrastructure retards supply routes and living conditions.") },
                    new[] { T("Rebuilding war damage restores production capacity and regional stability.") },
                    new[] { F("War damage", ruinedState.WarDamage.ToString("P0")),
                            F("People", Fmt.People(ruinedState.Population)),
                            F("Standard of living", Fmt.Percent(ruinedState.StandardOfLiving)) },
                    SituationRoute.Construction, T("Open regional construction"), ruinedState.MapIndex));
            }
        }

        if (DetachedLand(c, n, observer) is { } detached)
            items.Add(detached);

        if (!observer && Corridors.TryReportSwap(c, n, out var swap))
            items.Add(new("enclave_swap", SituationPriority.Opportunity, SituationArea.Foreign,
                T("Two enclaves can be exchanged"),
                Loc.Format("{0} holds {1} of our provinces cut off from the rest, and we hold {2} of theirs.",
                    swap.Partner, swap.OurProvinces, swap.TheirProvinces),
                new[] { T("Each piece sits against the other country. One paper hands both back, instead of a sale and a purchase.") },
                new[] { T("Nothing moves until they sign. A player who is asked still has the paper. A capital is not on the table.") },
                new[] { F("Our piece", swap.OurProvinces.ToString()), F("Their piece", swap.TheirProvinces.ToString()), F("The other side", swap.Partner) },
                SituationRoute.Inbox, T("Offer the exchange")));

        if (!observer && Corridors.TryReportPore(c, n, out var poreHolder, out var poreCount))
            items.Add(new("border_pore", SituationPriority.Opportunity, SituationArea.Foreign,
                T("A neighbour is shut inside the border"),
                Loc.Format("{0} holds {1} province(s) with no way out except across us.", poreHolder, poreCount),
                new[] { T("It is not a corridor to somewhere else. It is a dot in the line. Buying it asks for that region whole, and only when the region is the dot.") },
                new[] { T("Asking spends nothing until they accept. Their capital is never the dot.") },
                new[] { F("Inside the border", poreCount.ToString()), F("Held by", poreHolder) },
                SituationRoute.Inbox, T("Ask to buy it out")));

        if (c.FeatureEnabled("movements") && c.FeatureEnabled("movement_lifecycle"))
        {
            Nation loudMovement = null;
            var loudSupport = 0.12f;
            foreach (var movement in c.Nations)
            {
                var following = movement.Movement;
                if (following is null || following.Dissolved) continue;
                var definition = Movements.DefinitionOf(movement);
                if (definition is not null && (definition.Has("for_hire") || definition.Has("humanitarian"))) continue;
                foreach (var (region, support) in following.Support)
                {
                    if (support < loudSupport) continue;
                    var place = c.States.Find(state => state.MapIndex == region);
                    if (place is null || place.NationMapIndex != n.MapIndex) continue;
                    loudSupport = support;
                    loudMovement = movement;
                }
            }
            if (loudMovement is not null)
            {
                var stage = MovementMethods.StageOf(c, loudMovement);
                var leaning = MovementMethods.Target(c, loudMovement, Movements.DefinitionOf(loudMovement)) - MovementMethods.Of(loudMovement);
                if (stage != MovementStage.Civic || leaning >= 0.05f)
                {
                    var why = MovementMethods.LoudestPressure(c, loudMovement);
                    items.Add(new("movement_methods", stage >= MovementStage.Armed ? SituationPriority.Warning : SituationPriority.Opportunity,
                        SituationArea.Society,
                        stage >= MovementStage.Armed ? T("A movement in the country is armed") : T("A movement in the country is hardening"),
                        Loc.Format("{0}: {1}. {2}", loudMovement.DisplayName, T(MovementMethods.StageName(stage)), why.Length > 0 ? T(why) : ""),
                        new[] { why.Length > 0 ? T(why) : T("It is still close to the way it began.") },
                        new[] { T("Standing, a hearing and ground of its own pull a movement back. Crackdowns and weapons push it on.") },
                        new[] { F("Movement", loudMovement.DisplayName), F("Methods", T(MovementMethods.StageName(stage))) },
                        SituationRoute.Crises, T("Open the political pressure")));
                }
            }
        }

        if (HostWelcome(c, n) is { } welcome)
            items.Add(welcome);

        var waiting = c.Armies.Where(a => a.NationId == n.Id && !a.InBattle &&
                (a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead) &&
                !AssaultReadiness.Assess(c, a).Ready)
            .OrderBy(a => a.SupplyLevel).ThenBy(a => a.Organisation).ThenBy(a => a.Id).ToArray();
        if (waiting.Length > 0)
            items.Add(new("assault_waiting", SituationPriority.Warning, SituationArea.Forces,
                Loc.Format("Waiting to assault: {0} formations", waiting.Length),
                T("They have an attack order, and they are not yet fit to spend it."),
                new[] { ArmyAssaultPresentation.Description(c, waiting[0]) },
                new[] { T("A direct order still goes. Holding the line lets organisation and supply recover.") },
                new[] { F("Waiting", waiting.Length.ToString()), F("First formation", waiting[0].Name) },
                SituationRoute.Army, T("Inspect the formation"), waiting[0].Id));

        if (c.FeatureEnabled("construction") && n.Movement is not { Landless: true } && c.StatesOf(n).Length > 0 &&
            !c.Construction.Any(p => p.NationId == n.Id))
            items.Add(new("idle_construction", SituationPriority.Opportunity, SituationArea.Development,
                T("Construction capacity is idle"), T("No construction project is running in your country."),
                new[] { T("Nothing is being built.") },
                new[] { T("Construction capacity is going to waste.") },
                Array.Empty<SituationFact>(), SituationRoute.Construction, T("Choose a development project")));

        return items.OrderBy(i => i.Priority).ThenBy(i => i.DeadlineDay < 0 ? int.MaxValue : i.DeadlineDay)
            .ThenBy(i => i.Id, StringComparer.Ordinal).Take(Math.Clamp(d.SituationItemLimit, 8, 80)).ToArray();
    }

    /// <summary>
    /// The centre and a region that no longer agree, or null when that fact is absent.
    /// The situation room and the society chair of the desk both read this. Nothing is written.
    /// </summary>
    public static SituationItem PoliticalStrain(Campaign c, Nation n)
    {
        if (c is null || n is null) return null;
        string T(string text) => Loc.Get(text);
        SituationFact F(string label, string value) => new(T(label), value);
        var moodLine = PoliticalCohorts.StrainCaption(c, n);
        var loud = PoliticalCohorts.Loudest(c, n);
        var warning = moodLine is "A region of its own has stopped belonging to the centre."
            or "The laws still hold. The government is losing the room.";
        if (!(warning || (loud is not null && loud.Cause.Length > 0 && loud.Heat >= 0.45f
                && moodLine != "Liked enough to argue in the open.")))
            return null;
        var mood = PoliticalCohorts.Mood(c, n);
        var why = loud?.Cause ?? "";
        var facts = new List<SituationFact>
        {
            F("Law approval", mood.Law.ToString("P0")), F("Government approval", mood.Government.ToString("P0")),
            F("Political divergence", mood.Divergence.ToString("P0")), F("Loudest region", loud?.Name ?? "—")
        };
        if (why.Length > 0) facts.Add(F("Why", T(why)));
        return new("political_strain", SituationPriority.Warning, SituationArea.Society,
            T("The centre and a region no longer agree"),
            warning ? T(moodLine) : T(why),
            new[]
            {
                T("This is accumulated opinion: laws, the government, and how far a region has drifted. It is not a date for a new border."),
                why.Length > 0 ? T(why) : T("Nothing else on the books is pushing it today.")
            },
            new[] { T("A break still needs a weak centre, a region that rejects it, and an economic stress already on the books.") },
            facts.ToArray(),
            SituationRoute.Crises, T("Open the political pressure"));
    }

    /// <summary>
    /// A conciliation programme already on the books, or null when none is running.
    /// The situation room and the society chair both read this. Nothing is paid.
    /// </summary>
    public static SituationItem Conciliation(Campaign c, Nation n)
    {
        if (c is null || n is null) return null;
        string T(string text) => Loc.Get(text);
        SituationFact F(string label, string value) => new(T(label), value);
        var settling = n.DomesticCrisis;
        if (settling is not { ConciliationUntilDay: >= 0 } || settling.ConciliationUntilDay <= c.ElapsedDays)
            return null;
        var place = c.States.Find(state => state.MapIndex == settling.ConciliationRegion);
        var left = settling.ConciliationUntilDay - c.ElapsedDays;
        return new("conciliation", SituationPriority.Awaiting, SituationArea.Society,
            T("A region is being settled"),
            place is null
                ? Loc.Format("The payment has {0} days left.", left)
                : Loc.Format("{0} is being settled. {1} days of it are left.", place.DisplayName, left),
            new[] { T("The pull to leave, the anger, and the political distance cool while the money lasts. A border is not redrawn.") },
            new[] { T("When the season ends, the region's own life decides whether the quiet holds.") },
            new[] { F("Region", place?.DisplayName ?? "—"), F("Days left", left.ToString()) },
            SituationRoute.Crises, T("Open the political pressure"));
    }

    /// <summary>
    /// A piece of this country cut off from the rest, or null when the country is in one piece.
    /// Reading it does not ask for the way and does not buy a pore. An observer is not offered the paper.
    /// </summary>
    public static SituationItem DetachedLand(Campaign c, Nation n, bool observer = false)
    {
        if (observer || c is null || n is null || c.ProvinceMap is null) return null;
        if (!Corridors.TryReport(c, n, out var cutOff)) return null;
        string T(string text) => Loc.Get(text);
        SituationFact F(string label, string value) => new(T(label), value);
        var summary = cutOff.CanAsk
            ? Loc.Format("{0} provinces are cut off from the rest. {1} holds the shortest way, {2} provinces, for about {3}.",
                cutOff.Provinces, cutOff.Holder, cutOff.WayProvinces, Fmt.Money(cutOff.Price))
            : Loc.Format("{0} provinces are cut off from the rest. {1}", cutOff.Provinces,
                cutOff.Reason.Length > 0 ? T(cutOff.Reason) : T("No short way through a neighbour can be bought."));
        return new("detached_land", cutOff.CanAsk ? SituationPriority.Opportunity : SituationPriority.Warning,
            SituationArea.Foreign, T("A piece of the country is cut off"), summary,
            new[] { T("The piece is not joined to the rest by land. A purchase asks for the regions the way runs through, not a war.") },
            new[] { cutOff.CanAsk
                ? T("Asking spends nothing until they accept. The detached piece itself is not offered for sale.")
                : T("There is nothing to send just now. The piece stays where it is.") },
            new[] { F("Cut off", cutOff.Provinces.ToString()), F("The way", cutOff.CanAsk ? cutOff.Holder : "—") },
            SituationRoute.Inbox, cutOff.CanAsk ? T("Ask for the way through") : T("See why it cannot be asked"));
    }

    /// <summary>
    /// A headquarters whose welcome on this country's ground is wearing thin, or null while patience remains.
    /// The same fact the situation room files. Reading it does not expel anyone and spends nothing.
    /// </summary>
    public static SituationItem HostWelcome(Campaign c, Nation n)
    {
        if (c is null || n is null) return null;
        var worn = MovementRules.WornWelcome(c, n);
        if (worn is null) return null;
        string T(string text) => Loc.Get(text);
        SituationFact F(string label, string value) => new(T(label), value);
        var welcome = MovementRules.Patience(c, worn);
        var left = Math.Max(0f, c.Defines.MovementExpelHeat - worn.Movement.Heat);
        return new("host_temper", worn.Movement.Heat >= c.Defines.MovementExpelHeat * 0.75f
                ? SituationPriority.Warning : SituationPriority.Opportunity,
            SituationArea.Society, T("A headquarters is wearing out its welcome"),
            Loc.Format("{0}. {1}", worn.DisplayName, T(welcome)),
            new[] { T(welcome) },
            new[] { T("Throwing it out is a decision. Leaving it spends the patience until the headquarters goes on its own.") },
            new[] { F("Patience spent", Fmt.Percent(worn.Movement.Heat)), F("Left before it goes", Fmt.Percent(left)) },
            SituationRoute.Crises, T("Open the movement"), worn.Id);
    }

    private static float Threshold(float value, float fallback) => float.IsFinite(value) ? Math.Clamp(value, 0, 1) : fallback;

    /// <summary>
    /// The player's hottest crisis or armed clashes, or the limited war whose ceasefire
    /// is soonest when no such pair is on the tension list. Never creates a relation.
    /// </summary>
    private static DiplomaticRelation PlayerEscalation(Campaign c, Nation n)
    {
        var hot = Escalation.Hottest(c, c.Defines.TensionCrisisAt).FirstOrDefault(relation =>
            (relation.A == n.Id || relation.B == n.Id) &&
            Escalation.StageOf(relation) is TensionStage.Crisis or TensionStage.Clashes &&
            !FullWar(c, relation));
        if (hot is not null) return hot;
        StrategicWar soonest = null;
        foreach (var war in c.Wars)
        {
            if (war.Ended || !war.Limited || war.LimitedUntilDay < 0) continue;
            if (war.AttackerId != n.Id && war.DefenderId != n.Id) continue;
            if (soonest is null || war.LimitedUntilDay < soonest.LimitedUntilDay ||
                (war.LimitedUntilDay == soonest.LimitedUntilDay && war.Id < soonest.Id))
                soonest = war;
        }
        if (soonest is null) return null;
        var other = soonest.AttackerId == n.Id ? soonest.DefenderId : soonest.AttackerId;
        return CampaignDiplomacy.Peek(c, n.Id, other);
    }

    private static bool FullWar(Campaign c, DiplomaticRelation relation)
    {
        var war = CampaignDiplomacy.WarBetween(c, relation.A, relation.B);
        return war is not null && !war.Limited;
    }

    private static string EscalationStageName(TensionStage stage) => stage switch
    {
        TensionStage.Clashes => "Armed clashes",
        TensionStage.Crisis => "Crisis",
        TensionStage.Friction => "Friction",
        TensionStage.War => "At war",
        _ => "Calm"
    };

    private static string IncidentText(DiplomaticRelation relation)
    {
        if (relation?.LastIncident is not { Length: > 0 } id) return "";
        var found = IncidentCatalog.Find(id);
        var name = found is null || found.Name.Length == 0 ? id : found.Name;
        return Loc.Get(name);
    }
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
