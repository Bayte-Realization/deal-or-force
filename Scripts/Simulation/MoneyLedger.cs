using System;

namespace DealOrForce.Simulation;

/// <summary>
/// What a treasury's money moved for, outside the budget lines of common/budget (those are
/// recorded line by line, see <see cref="Nation.LedgerBudgetWeek"/>).
///
/// The numbers are positions in a saved array: APPEND ONLY. Renumbering one rewrites the
/// meaning of every saved week.
/// </summary>
public enum MoneyFlow
{
    /// <summary>New sovereign debt raised: the week's shortfall, liquidity top-ups, a script's loan.</summary>
    Borrowing = 0,
    /// <summary>Principal paid back.</summary>
    DebtRepayment = 1,
    /// <summary>Overdue interest and bills paid off.</summary>
    ArrearsPaid = 2,
    /// <summary>Bills the treasury could not pay this week: not spent, owed instead.</summary>
    BillsDeferred = 3,
    /// <summary>An overdraft turned into debt (positive: the cash hole is filled by principal).</summary>
    OverdraftFunded = 4,
    /// <summary>A government printing money through its central bank.</summary>
    MoneyPrinting = 5,
    /// <summary>The fee of a debt restructuring.</summary>
    RestructuringFee = 6,
    /// <summary>Reserves eaten by inflation above what they earn.</summary>
    ReserveErosion = 7,
    /// <summary>A surplus spent down by voters' demands, and the part of it that leaked.</summary>
    SurplusSpending = 8,
    /// <summary>The upkeep of a raised alert level.</summary>
    AlertUpkeep = 9,
    /// <summary>Disasters, crises and pandemics paid as a lump (most are income penalties now).</summary>
    Disasters = 10,
    /// <summary>Tribute and levies between a subject and its overlord, both ways.</summary>
    Tribute = 11,
    /// <summary>International organizations: dues, founding deposits, grants, a dissolved fund returned.</summary>
    Organizations = 12,
    /// <summary>Aid, gifts, compensation and alliance programmes between states.</summary>
    ForeignAid = 13,
    /// <summary>Loans between states repaid.</summary>
    StateLoans = 14,
    /// <summary>Reparations and peace payments.</summary>
    Reparations = 15,
    /// <summary>Construction paid, rebuilding, a capital moved, land colonised, demolition refunds.</summary>
    Construction = 16,
    /// <summary>Recruiting, ships, equipment, training, drills, militias - and refunds.</summary>
    Military = 17,
    /// <summary>Covert operations, intelligence networks, proxy wars, kin statecraft.</summary>
    Covert = 18,
    /// <summary>Diplomatic missions, gestures, talks, mediation.</summary>
    Diplomacy = 19,
    /// <summary>A movement's patrons, fees and levies, and a state's backing of movements.</summary>
    Movements = 20,
    /// <summary>Events, decisions, policies, crisis responses, a ruler's purchases.</summary>
    EventsAndDecisions = 21,
    /// <summary>Rent for land held under a lease.</summary>
    LandLeases = 22,
    /// <summary>Everything the treasury did that no line above recorded: the remainder.</summary>
    Other = 23,
}

public sealed partial class Nation
{
    /// <summary>
    /// The open week: money in by <see cref="MoneyFlow"/> in the first half, money out (negative)
    /// in the second, so a line that both receives and pays shows both. Null until the first booking.
    /// </summary>
    public float[] LedgerWeek;

    /// <summary>The last closed week, laid out like <see cref="LedgerWeek"/>; what the ledger screen shows.</summary>
    public float[] LedgerLastWeek;

    /// <summary>The open week's budget lines, by position in BudgetCore.All (revenue +, expense -).</summary>
    public float[] LedgerBudgetWeek;

    /// <summary>The last closed week's budget lines.</summary>
    public float[] LedgerBudgetLastWeek;

    /// <summary>The last closed week's income penalty (negative), kept beside the lines.</summary>
    public float LedgerPenaltyLastWeek;

    /// <summary>The open week's income penalty (negative).</summary>
    public float LedgerPenaltyWeek;

    /// <summary>The treasury when the open week began, so the remainder can be worked out.</summary>
    public float LedgerTreasuryAtOpen;

    /// <summary>False until a week has been opened: an old save does not invent a remainder.</summary>
    public bool LedgerOpen;

    /// <summary>The day the last week closed, and how many days it covered.</summary>
    public int LedgerClosedDay = -1;
    public int LedgerClosedDays;
}

/// <summary>
/// Every income and expense of a treasury, written down where it happens, a week at a time.
///
/// Cheap by construction: one fixed-size array per country (and per organization), a float
/// added per booking, the week closed by copying it - no strings, no lists, no allocation after
/// the first booking. The labels are composed only by the screen that reads them.
///
/// Complete by construction too: when a week closes, whatever the treasury did that no booking
/// explains is written to <see cref="MoneyFlow.Other"/>, so the lines always add up to the change
/// in the treasury the player can see. A line that grows large there is a place in the code that
/// moves money without saying why.
/// </summary>
public static class MoneyLedger
{
    public static readonly int FlowCount = Enum.GetValues<MoneyFlow>().Length;

    /// <summary>The length of a week's array: money in, then money out.</summary>
    private static int Slots => FlowCount * 2;

    private static float[] Week(Nation nation)
    {
        var week = nation.LedgerWeek;
        if (week is not null && week.Length == Slots) return week;
        // A save written by a build with fewer flows: the old halves are not where the new ones
        // are, so the open week starts again rather than mixing them up.
        return nation.LedgerWeek = new float[Slots];
    }

    /// <summary>Book a signed amount (income positive, spending negative) on one country's open week.</summary>
    public static void Book(Nation nation, MoneyFlow flow, float amount)
    {
        if (nation is null || !float.IsFinite(amount) || amount == 0f) return;
        var week = Week(nation);
        week[amount > 0f ? (int)flow : FlowCount + (int)flow] += amount;
    }

    /// <summary>The last closed week's money in on one line (positive or nought).</summary>
    public static float LastIn(Nation nation, MoneyFlow flow)
    {
        var last = nation?.LedgerLastWeek;
        return last is null || last.Length != Slots ? 0f : last[(int)flow];
    }

    /// <summary>The last closed week's money out on one line (negative or nought).</summary>
    public static float LastOut(Nation nation, MoneyFlow flow)
    {
        var last = nation?.LedgerLastWeek;
        return last is null || last.Length != Slots ? 0f : last[FlowCount + (int)flow];
    }

    /// <summary>The same amount leaving one treasury and arriving in another.</summary>
    public static void Transfer(Nation from, Nation to, MoneyFlow flow, float amount)
    {
        Book(from, flow, -amount);
        Book(to, flow, amount);
    }

    /// <summary>The array the budget writes its lines into this week, sized to the budget.</summary>
    public static float[] BudgetWeek(Nation nation)
    {
        var count = BudgetCore.All.Count;
        var lines = nation.LedgerBudgetWeek;
        if (lines is null || lines.Length != count)
        {
            var grown = new float[count];
            if (lines is not null) Array.Copy(lines, grown, Math.Min(lines.Length, count));
            nation.LedgerBudgetWeek = lines = grown;
        }
        return lines;
    }

    /// <summary>Record what the week's budget penalties took.</summary>
    public static void BookPenalty(Nation nation, float penalty)
    {
        if (nation is null || !float.IsFinite(penalty) || penalty <= 0f) return;
        nation.LedgerPenaltyWeek -= penalty;
    }

    /// <summary>
    /// Close the open week and open the next. The remainder is worked out against the treasury
    /// the week opened with. Called once per country per economic week, by the economy tick.
    /// </summary>
    public static void Close(Campaign campaign, Nation nation)
    {
        if (nation is null) return;
        var week = Week(nation);
        var budget = nation.LedgerBudgetWeek;
        var otherIn = (int)MoneyFlow.Other;
        var otherOut = FlowCount + (int)MoneyFlow.Other;
        week[otherIn] = week[otherOut] = 0f;
        if (nation.LedgerOpen && float.IsFinite(nation.Treasury) && float.IsFinite(nation.LedgerTreasuryAtOpen))
        {
            var booked = nation.LedgerPenaltyWeek;
            for (var index = 0; index < week.Length; index++) booked += week[index];
            if (budget is not null)
                for (var index = 0; index < budget.Length; index++) booked += budget[index];
            var remainder = nation.Treasury - nation.LedgerTreasuryAtOpen - booked;
            // Rounding of a float treasury is not a payment.
            var noise = MathF.Max(0.0005f, MathF.Abs(nation.Treasury) * 0.00002f);
            if (MathF.Abs(remainder) > noise) week[remainder > 0f ? otherIn : otherOut] = remainder;
        }

        var last = nation.LedgerLastWeek;
        if (last is null || last.Length != week.Length) nation.LedgerLastWeek = last = new float[week.Length];
        Array.Copy(week, last, week.Length);
        Array.Clear(week);

        if (budget is not null)
        {
            var lastBudget = nation.LedgerBudgetLastWeek;
            if (lastBudget is null || lastBudget.Length != budget.Length)
                nation.LedgerBudgetLastWeek = lastBudget = new float[budget.Length];
            Array.Copy(budget, lastBudget, budget.Length);
            Array.Clear(budget);
        }
        else nation.LedgerBudgetLastWeek = null;
        nation.LedgerPenaltyLastWeek = nation.LedgerPenaltyWeek;
        nation.LedgerPenaltyWeek = 0f;

        var day = campaign?.ElapsedDays ?? 0;
        nation.LedgerClosedDays = nation.LedgerClosedDay >= 0 ? Math.Max(1, day - nation.LedgerClosedDay) : 0;
        nation.LedgerClosedDay = day;
        nation.LedgerTreasuryAtOpen = float.IsFinite(nation.Treasury) ? nation.Treasury : 0f;
        nation.LedgerOpen = true;
    }

    /// <summary>One closed-week line of a country's non-budget money, net of in and out.</summary>
    public static float LastWeek(Nation nation, MoneyFlow flow) => LastIn(nation, flow) + LastOut(nation, flow);
}

/// <summary>What an organization's common fund moved for. APPEND ONLY, like <see cref="MoneyFlow"/>.</summary>
public enum OrganizationFlow
{
    /// <summary>Members' weekly dues.</summary>
    Dues = 0,
    /// <summary>The founder's deposit.</summary>
    FoundingDeposit = 1,
    /// <summary>Grants voted to a member.</summary>
    Grants = 2,
    /// <summary>The fund handed back to the members when it dissolved.</summary>
    Returned = 3,
    /// <summary>Whatever moved the fund that no line above recorded.</summary>
    Other = 4,
}

public sealed partial class InternationalOrganization
{
    public float[] LedgerWeek;
    public float[] LedgerLastWeek;
    public float LedgerFundAtOpen;
    public bool LedgerOpen;
    public int LedgerClosedDay = -1;
}

public static class OrganizationLedger
{
    public static readonly int FlowCount = Enum.GetValues<OrganizationFlow>().Length;

    public static void Book(InternationalOrganization org, OrganizationFlow flow, float amount)
    {
        if (org is null || !float.IsFinite(amount) || amount == 0f) return;
        if (org.LedgerWeek is null || org.LedgerWeek.Length < FlowCount)
        {
            var grown = new float[FlowCount];
            if (org.LedgerWeek is not null) Array.Copy(org.LedgerWeek, grown, org.LedgerWeek.Length);
            org.LedgerWeek = grown;
        }
        org.LedgerWeek[(int)flow] += amount;
    }

    /// <summary>Close the fund's week: called where the dues are collected, once a week.</summary>
    public static void Close(Campaign campaign, InternationalOrganization org)
    {
        if (org is null) return;
        if (org.LedgerWeek is null || org.LedgerWeek.Length < FlowCount)
        {
            var grown = new float[FlowCount];
            if (org.LedgerWeek is not null) Array.Copy(org.LedgerWeek, grown, org.LedgerWeek.Length);
            org.LedgerWeek = grown;
        }
        var week = org.LedgerWeek;
        if (org.LedgerOpen && float.IsFinite(org.Budget))
        {
            var booked = 0f;
            for (var index = 0; index < week.Length; index++)
                if (index != (int)OrganizationFlow.Other) booked += week[index];
            var remainder = org.Budget - org.LedgerFundAtOpen - booked;
            week[(int)OrganizationFlow.Other] = MathF.Abs(remainder) > 0.0005f ? remainder : 0f;
        }
        else week[(int)OrganizationFlow.Other] = 0f;
        if (org.LedgerLastWeek is null || org.LedgerLastWeek.Length != week.Length) org.LedgerLastWeek = new float[week.Length];
        Array.Copy(week, org.LedgerLastWeek, week.Length);
        Array.Clear(week);
        org.LedgerFundAtOpen = float.IsFinite(org.Budget) ? org.Budget : 0f;
        org.LedgerOpen = true;
        org.LedgerClosedDay = campaign?.ElapsedDays ?? 0;
    }
}
