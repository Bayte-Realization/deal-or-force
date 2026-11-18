using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>One row of a money ledger: a heading group, a caption, the week's signed amount, and why.</summary>
public sealed class MoneyLedgerLine
{
    /// <summary>The heading it sits under (an English key the tables translate).</summary>
    public string Group = "";
    /// <summary>What it is called on screen, already translated.</summary>
    public string Caption = "";
    /// <summary>The closed week's amount: income positive, spending negative, in billions.</summary>
    public float Weekly;
    /// <summary>One or two plain sentences, already translated: shown under the row, never only on hover.</summary>
    public string Explanation = "";
    /// <summary>A row that is a total or a summary rather than a flow.</summary>
    public bool Total;
    /// <summary>A row with no amount of its own (a list, a note).</summary>
    public bool Note;

    /// <summary>The same week at this pace for a year.</summary>
    public float Yearly => Weekly * 52f;
}

/// <summary>
/// Reads a country's, a movement's or an organization's money ledger (MoneyLedger) into rows a
/// screen can draw. Pure: it reads closed weeks and composes text, it never moves money, so any
/// screen may call it any number of times.
/// </summary>
public static class MoneyLedgerView
{
    private readonly struct FlowText
    {
        public FlowText(string group, string incoming, string outgoing, string explanation)
        {
            Group = group; Incoming = incoming; Outgoing = outgoing; Explanation = explanation;
        }
        public readonly string Group, Incoming, Outgoing, Explanation;
    }

    public const string GroupIncome = "Income";
    public const string GroupSpending = "Spending";
    public const string GroupConstruction = "Building sites";
    public const string GroupPenalties = "Income penalties";
    public const string GroupBetweenStates = "Between states";
    public const string GroupProgrammes = "Government programmes";
    public const string GroupReserves = "Reserves";
    public const string GroupDebt = "Borrowing and debt";
    public const string GroupOther = "Other";
    public const string GroupTotal = "The week in sum";

    /// <summary>The order the groups appear in.</summary>
    public static readonly string[] GroupOrder =
    {
        GroupIncome, GroupSpending, GroupConstruction, GroupPenalties, GroupBetweenStates, GroupProgrammes,
        GroupReserves, GroupDebt, GroupOther, GroupTotal
    };

    private static readonly Dictionary<MoneyFlow, FlowText> Texts = new()
    {
        [MoneyFlow.Borrowing] = new(GroupDebt, "New borrowing", "",
            "Bonds sold this week to cover a shortfall or keep a cash reserve. It is debt, and it pays interest."),
        [MoneyFlow.DebtRepayment] = new(GroupDebt, "", "Debt repaid",
            "Principal paid back out of spare cash."),
        [MoneyFlow.ArrearsPaid] = new(GroupDebt, "", "Overdue bills paid",
            "Interest and bills missed in earlier weeks, paid off now."),
        [MoneyFlow.BillsDeferred] = new(GroupDebt, "Bills left unpaid", "",
            "What the treasury could not pay this week. It was not spent: it is owed, and owing it costs credit."),
        [MoneyFlow.OverdraftFunded] = new(GroupDebt, "Overdraft turned into debt", "",
            "A payment taken when the cash was not there, booked as debt instead of a negative balance."),
        [MoneyFlow.MoneyPrinting] = new(GroupReserves, "Money printed", "",
            "The central bank creating money for the government. It raises inflation."),
        [MoneyFlow.RestructuringFee] = new(GroupDebt, "", "Debt restructuring fee",
            "The cost of negotiating a debt down while the restructuring lasts."),
        [MoneyFlow.ReserveErosion] = new(GroupReserves, "", "Reserves lost to inflation",
            "Cash loses what inflation takes above what reserves earn."),
        [MoneyFlow.SurplusSpending] = new(GroupReserves, "", "Surplus spent down",
            "A treasury above what voters tolerate is spent on handouts and works, and where nobody answers to voters part of it leaks away."),
        [MoneyFlow.AlertUpkeep] = new(GroupProgrammes, "", "Raised alert upkeep",
            "Keeping the forces at a higher alert level costs money every week."),
        [MoneyFlow.Disasters] = new(GroupReserves, "", "Disasters and crises",
            "Bills paid at once for a disaster. Most disasters now cut income for a time instead: see the income penalties."),
        [MoneyFlow.Tribute] = new(GroupBetweenStates, "Tribute received", "Tribute paid",
            "Money between a subject and its overlord: tribute, levies, gifts and support."),
        [MoneyFlow.Organizations] = new(GroupBetweenStates, "From organizations", "To organizations",
            "Dues to international organizations and founding deposits; grants voted to you; a dissolved fund handed back."),
        [MoneyFlow.ForeignAid] = new(GroupBetweenStates, "Aid received", "Aid given",
            "Aid, gifts, compensation and alliance programmes between states."),
        [MoneyFlow.StateLoans] = new(GroupBetweenStates, "Loan repayments received", "Loan repayments made",
            "Repayments of loans one state made to another."),
        [MoneyFlow.Reparations] = new(GroupBetweenStates, "Reparations received", "Reparations paid",
            "Money a peace treaty or an accepted ultimatum moves from the loser to the winner."),
        [MoneyFlow.Construction] = new(GroupProgrammes, "Demolition scrap", "Rebuilding and resettlement",
            "War damage rebuilt, a capital moved, land settled. A demolition returns part of a building's price as scrap. The building sites themselves have their own line."),
        [MoneyFlow.BuildingSites] = new(GroupConstruction, "Building sites", "Building sites",
            "The sites under construction, paid a day at a time as they progress. A site costs its price spread over its working days; more sites worked at once cost more a week and finish sooner."),
        [MoneyFlow.Military] = new(GroupProgrammes, "Military refunds", "Military purchases",
            "Recruiting, ships and equipment, training and drills, militias. A cancelled order returns part of its price."),
        [MoneyFlow.Covert] = new(GroupProgrammes, "Covert income", "Covert operations",
            "Covert operations, intelligence networks, proxy wars and kin statecraft abroad."),
        [MoneyFlow.Diplomacy] = new(GroupProgrammes, "Diplomatic payments received", "Diplomacy",
            "Missions, visits, shows of force, talks and mediation, and the prices of diplomatic acts."),
        [MoneyFlow.Movements] = new(GroupBetweenStates, "From movements and patrons", "To movements",
            "For a movement: donations, sponsors, contracts and levies, and what its actions cost. For a state: backing movements, and what movements take from it."),
        [MoneyFlow.EventsAndDecisions] = new(GroupProgrammes, "From events and decisions", "Events and decisions",
            "What events, decisions, policies, crisis responses and a ruler's purchases cost or brought in."),
        [MoneyFlow.LandLeases] = new(GroupBetweenStates, "Land rent received", "Land rent paid",
            "Rent for land held under a lease."),
        [MoneyFlow.RegionalAutonomy] = new(GroupProgrammes, "", "Regional autonomy",
            "The share of their own taxes self-governing regions keep. It ends when their autonomy is taken back."),
        [MoneyFlow.Other] = new(GroupOther, "Other receipts", "Other payments",
            "Money the treasury gained or lost this week that no line above records: a deal, a transfer, what goes with a new state."),
    };

    /// <summary>Every row of one country's (or movement's) last closed week, in screen order.</summary>
    public static List<MoneyLedgerLine> ForNation(Campaign campaign, Nation nation)
    {
        var rows = new List<MoneyLedgerLine>(48);
        if (campaign is null || nation is null) return rows;
        var movement = nation.Movement is not null;
        var total = 0f;

        // ---- the budget, line by line -------------------------------------------------------
        var lines = BudgetCore.All;
        var closed = nation.LedgerBudgetLastWeek;
        var estimated = closed is null || closed.Length != lines.Count;
        var atWar = false;
        if (estimated)
            foreach (var war in Warfare.OpenWars(campaign))
                if (!war.Ended && war.Involves(nation.Id)) { atWar = true; break; }
        for (var index = 0; index < lines.Count; index++)
        {
            var line = lines[index];
            var amount = estimated
                ? BudgetCore.Amount(nation, line, nation.Gdp / 52f, atWar) * (line.Side == BudgetSide.Expense ? -1f : 1f)
                : closed[index];
            // A movement has no budget of its own; its rows would all read nothing.
            if (movement && MathF.Abs(amount) < 0.0000005f) continue;
            total += amount;
            rows.Add(new MoneyLedgerLine
            {
                Group = line.Side == BudgetSide.Revenue ? GroupIncome : GroupSpending,
                Caption = BudgetCore.Label(nation, line),
                Weekly = amount,
                Explanation = string.IsNullOrWhiteSpace(line.Description) ? "" : Loc.Get(line.Description)
            });
        }

        // ---- timed income penalties ---------------------------------------------------------
        var penalty = estimated ? -nation.WeeklyIncomePenalty : nation.LedgerPenaltyLastWeek;
        var active = nation.IncomePenalties;
        if (MathF.Abs(penalty) > 0f || active is { Count: > 0 })
        {
            total += penalty;
            rows.Add(new MoneyLedgerLine
            {
                Group = GroupPenalties, Caption = Loc.Get("Income penalties"), Weekly = penalty,
                Explanation = Loc.Format("A share of gross revenue taken for a time by recent events, now {0} of it. Expenses are untouched; the cap is {1}.",
                    Fmt.Percent(IncomePenalties.Share(nation)),
                    Fmt.Percent(campaign.Defines.IncomePenaltyCap))
            });
            if (active is not null)
            {
                var sum = 0f;
                foreach (var entry in active) sum += MathF.Max(0f, entry.Share);
                foreach (var entry in active)
                    rows.Add(new MoneyLedgerLine
                    {
                        Group = GroupPenalties, Caption = "  " + IncomePenalties.Describe(entry.Source),
                        Weekly = sum > 0f ? penalty * MathF.Max(0f, entry.Share) / sum : 0f,
                        Explanation = Loc.Format("−{0} of gross income for {1} more days.",
                            Fmt.Percent(entry.Share), Math.Max(0, entry.UntilDay - campaign.ElapsedDays)),
                        Note = true
                    });
            }
        }

        // ---- building sites: what the queue costs a week, and why it is that much --------------
        if (!movement)
        {
            var open = 0;
            foreach (var project in campaign.Construction) if (project.NationId == nation.Id) open++;
            if (open > 0 || MoneyLedger.LastOut(nation, MoneyFlow.BuildingSites) != 0f)
                rows.Add(new MoneyLedgerLine
                {
                    Group = GroupConstruction, Caption = Loc.Get("The construction queue"), Note = true,
                    Weekly = -ConstructionQueue.WeeklyBill(campaign, nation),
                    Explanation = ConstructionQueue.Summary(campaign, nation) + ". " +
                        Loc.Format("Sites worked at once: {0} plus one for every {1} civilian factories ({2} standing), at most {3}.",
                            campaign.Defines.ConstructionSitesBase,
                            campaign.Defines.ConstructionSitesPerCivilianIndustry > 0f
                                ? MathF.Round(1f / campaign.Defines.ConstructionSitesPerCivilianIndustry, 1) : 0f,
                            ConstructionCost.CivilianIndustry(campaign, nation), campaign.Defines.ConstructionSitesMax)
                });
        }

        // ---- everything outside the budget ----------------------------------------------------
        var quiet = new List<string>();
        var last = nation.LedgerLastWeek;
        foreach (MoneyFlow flow in Enum.GetValues<MoneyFlow>())
        {
            var text = Texts[flow];
            var incoming = MoneyLedger.LastIn(nation, flow);
            var outgoing = MoneyLedger.LastOut(nation, flow);
            if (incoming != 0f)
            {
                total += incoming;
                rows.Add(new MoneyLedgerLine { Group = text.Group, Caption = Loc.Get(Caption(text, true)), Weekly = incoming,
                    Explanation = Loc.Get(text.Explanation) + MovementDetail(nation, flow) });
            }
            if (outgoing != 0f)
            {
                total += outgoing;
                rows.Add(new MoneyLedgerLine { Group = text.Group, Caption = Loc.Get(Caption(text, false)), Weekly = outgoing,
                    Explanation = Loc.Get(text.Explanation) });
            }
            if (incoming == 0f && outgoing == 0f && last is not null)
                quiet.Add(Loc.Get(Caption(text, !string.IsNullOrEmpty(text.Incoming))));
        }
        if (last is null)
            rows.Add(new MoneyLedgerLine { Group = GroupOther, Caption = Loc.Get("Payments outside the budget"), Note = true,
                Explanation = Loc.Get("Written down from the first economic week that closes. Until then only the budget is shown, as it stands today.") });
        else if (quiet.Count > 0)
            rows.Add(new MoneyLedgerLine { Group = GroupOther, Caption = Loc.Get("Nothing this week"), Note = true,
                Explanation = string.Join(", ", quiet) });

        rows.Add(new MoneyLedgerLine
        {
            Group = GroupTotal, Caption = Loc.Get("Change in the treasury"), Weekly = total, Total = true,
            Explanation = estimated
                ? Loc.Get("The budget as it stands today. The first economic week has not closed yet.")
                : Loc.Format("Every line above added up: what the treasury did over the {0} days to day {1}.",
                    Math.Max(1, nation.LedgerClosedDays == 0 ? campaign.Defines.EconomyTickDays : nation.LedgerClosedDays),
                    nation.LedgerClosedDay)
        });
        Sort(rows);
        return rows;
    }

    private static string Caption(FlowText text, bool incoming) =>
        incoming ? (string.IsNullOrEmpty(text.Incoming) ? text.Outgoing : text.Incoming)
                 : (string.IsNullOrEmpty(text.Outgoing) ? text.Incoming : text.Outgoing);

    /// <summary>For a movement, where this week's movement money came from, in its own figures.</summary>
    private static string MovementDetail(Nation nation, MoneyFlow flow)
    {
        if (flow != MoneyFlow.Movements || nation.Movement is not { } state) return "";
        return " " + Loc.Format("This week: {0} in donations and {1} from sponsors and contracts.",
            Fmt.Money(state.DonationsWeekly), Fmt.Money(state.SponsorsWeekly));
    }

    /// <summary>Every row of an organization's common fund for its last closed week.</summary>
    public static List<MoneyLedgerLine> ForOrganization(Campaign campaign, InternationalOrganization org)
    {
        var rows = new List<MoneyLedgerLine>(16);
        if (campaign is null || org is null) return rows;
        var last = org.LedgerLastWeek;
        float Read(OrganizationFlow flow) => last is null || (int)flow >= last.Length ? 0f : last[(int)flow];
        var total = 0f;
        void Add(string group, string caption, float amount, string explanation)
        {
            total += amount;
            rows.Add(new MoneyLedgerLine { Group = group, Caption = Loc.Get(caption), Weekly = amount, Explanation = Loc.Get(explanation) });
        }
        Add(GroupIncome, "Members' dues", Read(OrganizationFlow.Dues),
            "What the members paid into the common fund this week. A member that cannot pay falls into arrears, and arrears cost cohesion.");
        if (Read(OrganizationFlow.FoundingDeposit) != 0f)
            Add(GroupIncome, "Founding deposit", Read(OrganizationFlow.FoundingDeposit), "The founder's deposit that opened the fund.");
        Add(GroupSpending, "Grants to members", Read(OrganizationFlow.Grants),
            "Grants the assembly voted to a member in trouble.");
        if (Read(OrganizationFlow.Returned) != 0f)
            Add(GroupSpending, "Fund handed back", Read(OrganizationFlow.Returned), "The fund returned to the members when the organization dissolved.");
        if (Read(OrganizationFlow.Other) != 0f)
            Add(GroupOther, "Other movements of the fund", Read(OrganizationFlow.Other), "A change to the fund no line above records.");

        // What each member owes a week, so the dues line can be read member by member.
        foreach (var id in org.Members)
        {
            var member = campaign.NationById(id);
            if (member is null) continue;
            var dues = InternationalOrganizations.Dues(campaign, org, member);
            var arrears = org.UnpaidWeeks.GetValueOrDefault(id);
            rows.Add(new MoneyLedgerLine
            {
                Group = GroupIncome, Caption = "  " + member.DisplayName, Weekly = float.IsFinite(dues) ? dues : 0f, Note = true,
                Explanation = arrears > 0
                    ? Loc.Format("Dues owed each week; {0} weeks unpaid.", arrears)
                    : Loc.Get("Dues owed each week, paid.")
            });
        }
        rows.Add(new MoneyLedgerLine
        {
            Group = GroupTotal, Caption = Loc.Get("Change in the common fund"), Weekly = total, Total = true,
            Explanation = last is null
                ? Loc.Get("The fund's first week has not closed yet.")
                : Loc.Format("Every line above added up. The fund now holds {0}.", Fmt.Money(org.Budget))
        });
        Sort(rows);
        return rows;
    }

    /// <summary>Into group order, keeping the order within a group.</summary>
    private static void Sort(List<MoneyLedgerLine> rows)
    {
        var ordered = new List<MoneyLedgerLine>(rows.Count);
        foreach (var group in GroupOrder)
            foreach (var row in rows)
                if (row.Group == group) ordered.Add(row);
        foreach (var row in rows)
            if (Array.IndexOf(GroupOrder, row.Group) < 0) ordered.Add(row);
        rows.Clear();
        rows.AddRange(ordered);
    }
}
