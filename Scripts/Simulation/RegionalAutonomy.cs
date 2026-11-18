using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>Political points a grant of autonomy costs at once: a third of a mobilisation decision.</summary>
    public float AutonomyPoliticalCost { get; set; } = 15f;
    /// <summary>The share of its own taxes an autonomous region keeps, paid every week out of the treasury.</summary>
    public float AutonomyRevenueShare { get; set; } = 0.4f;
    /// <summary>What an autonomous region weighs in imperial overstretch, as a share of what it would.</summary>
    public float AutonomyOverstretchWeight { get; set; } = 0.4f;
    /// <summary>The share of minority and overstretch radicalism a self-governing region still takes in.</summary>
    public float AutonomyRadicalismInflow { get; set; } = 0.6f;
    /// <summary>How much faster a self-governing region comes to accept the state (ConquestAcceptance).</summary>
    public float AutonomyAcceptanceFactor { get; set; } = 2f;
    /// <summary>What taking autonomy back adds to the region's radicalism at once.</summary>
    public float AutonomyRevokeRadicalism { get; set; } = 0.10f;
    /// <summary>Days after a revocation before the same region can be granted autonomy again.</summary>
    public int AutonomyRegrantDays { get; set; } = 1095;
    /// <summary>The computer does not hand autonomy to a region calmer than this.</summary>
    public float AiAutonomyRadicalism { get; set; } = 0.15f;
    /// <summary>The fewest days between two grants by one computer-run country.</summary>
    public int AiAutonomyIntervalDays { get; set; } = 90;
    /// <summary>Weeks of the price a computer-run treasury must hold when the grant would put its week in deficit.</summary>
    public float AiAutonomyReserveWeeks { get; set; } = 26f;
}

public sealed partial class ScenarioState
{
    /// <summary>The day this region was given autonomy by the state that holds it, or -1.</summary>
    public int AutonomousSinceDay = -1;
    /// <summary>The day its autonomy was last taken back, or -1: a region does not get a second grant at once.</summary>
    public int AutonomyRevokedDay = -1;
}

/// <summary>
/// Regional autonomy: the one active lever a state has over a region that does not want it.
///
/// The ways out that existed were a payment that cools one region for a season (ConciliateRegion),
/// an amnesty that the stability formula forgets in months, and giving the land away. Between "pay
/// for quiet" and "let it go" there was nothing, and that is exactly where Scotland, Catalonia,
/// South Tyrol and Åland sit: they govern themselves inside a state, keep a share of their own
/// money, and stop being the empire's problem without stopping being its land.
///
/// So a grant costs political points once and a share of the region's taxes every week; in return
/// the region weighs less than half in the overstretch, takes in less grievance, and comes to accept
/// the state twice as fast. Taking it back is free and remembered: the region's radicalism jumps, its
/// acceptance halves, and it cannot be granted again for three years. One refusal function answers
/// for the command, the province menu and the computer, which uses the same lever by the same rules
/// (ConsiderGrant) and never takes a grant back.
/// </summary>
public static class RegionalAutonomy
{
    /// <summary>The decision-ledger key of a computer-run country's next look at autonomy.</summary>
    internal const string NextKey = "autonomy/next";

    public static bool Autonomous(ScenarioState state) => state is not null && state.AutonomousSinceDay >= 0;

    /// <summary>The refusal that only waiting for points answers, as an interface key.</summary>
    public const string NoPoints = "Not enough political points.";

    /// <summary>Why this country cannot grant this region autonomy now, as an interface key, or "".</summary>
    public static string Refusal(Campaign c, Nation nation, ScenarioState state)
    {
        if (c is null || nation is null) return "Choose a country.";
        if (state is null || state.NationMapIndex != nation.MapIndex) return "That region is not governed by this country.";
        if (state.MapIndex == nation.CapitalStateMapIndex) return "The capital cannot govern itself apart from the state.";
        if (state.Population <= 0) return "Nobody lives there to govern themselves.";
        if (StabilityCore.OccupiedShare(c, state, nation) >= 0.5f) return "A foreign army holds most of the region.";
        if (Autonomous(state)) return "The region already governs itself.";
        if (state.AutonomyRevokedDay >= 0 && c.ElapsedDays - state.AutonomyRevokedDay < c.Defines.AutonomyRegrantDays)
            return "Its autonomy was taken back too recently to be granted again.";
        if (nation.PoliticalPoints < c.Defines.AutonomyPoliticalCost) return NoPoints;
        return "";
    }

    /// <summary>Why this autonomy cannot be taken back, or "".</summary>
    public static string RevokeRefusal(Campaign c, Nation nation, ScenarioState state)
    {
        if (c is null || nation is null) return "Choose a country.";
        if (state is null || state.NationMapIndex != nation.MapIndex) return "That region is not governed by this country.";
        if (!Autonomous(state)) return "The region does not govern itself.";
        return "";
    }

    /// <summary>The days left before a revoked region may be granted autonomy again; 0 when it may.</summary>
    public static int RegrantDaysLeft(Campaign c, ScenarioState state) =>
        c is null || state is null || state.AutonomyRevokedDay < 0 ? 0
            : Math.Max(0, state.AutonomyRevokedDay + c.Defines.AutonomyRegrantDays - c.ElapsedDays);

    /// <summary>
    /// What this region's autonomy costs a week: its share of the country's people, times the share
    /// of taxes it keeps, of the tax income the budget actually raised last week (Nation.WeeklyTaxIncome).
    /// The price of a grant not yet made is the same figure, so the menu quotes what will be paid.
    /// </summary>
    public static float WeeklyPrice(Campaign c, Nation nation, ScenarioState state)
    {
        if (c is null || nation is null || state is null || nation.Population <= 0 || state.Population <= 0) return 0f;
        var share = Math.Clamp(state.Population / (double)nation.Population, 0d, 1d);
        var price = MathF.Max(0f, c.Defines.AutonomyRevenueShare) * share * MathF.Max(0f, nation.WeeklyTaxIncome);
        return float.IsFinite((float)price) ? (float)price : 0f;
    }

    /// <summary>What every autonomous region of the country costs together, a week.</summary>
    public static float WeeklyCost(Campaign c, Nation nation)
    {
        if (c is null || nation is null) return 0f;
        var total = 0f;
        foreach (var state in c.StatesOf(nation))
            if (Autonomous(state)) total += WeeklyPrice(c, nation, state);
        return total;
    }

    /// <summary>
    /// The week's price, out of the treasury on the economic week and before the week is settled,
    /// so a treasury it empties borrows for it the way it borrows for any other bill.
    /// </summary>
    internal static void PayWeek(Campaign c, Nation nation)
    {
        var cost = WeeklyCost(c, nation);
        if (cost <= 0f) return;
        var before = nation.Treasury;
        nation.Treasury -= cost;
        MoneyLedger.Book(nation, MoneyFlow.RegionalAutonomy, nation.Treasury - before);
    }

    internal static bool Grant(Campaign c, Nation nation, ScenarioState state)
    {
        if (Refusal(c, nation, state).Length > 0) return false;
        nation.PoliticalPoints = MathF.Max(0f, nation.PoliticalPoints - c.Defines.AutonomyPoliticalCost);
        state.AutonomousSinceDay = c.ElapsedDays;
        c.Chronicle.Record(c, "autonomy_granted", nation.Id, -1, place: state.Name);
        c.MarkStrategicChange();
        return true;
    }

    internal static bool Revoke(Campaign c, Nation nation, ScenarioState state)
    {
        if (RevokeRefusal(c, nation, state).Length > 0) return false;
        // Taken back is remembered longer than given: Scotland 1997 to 2014, Catalonia 2006 to 2017.
        state.Radicalism = M.Clamp01(state.Radicalism + MathF.Max(0f, c.Defines.AutonomyRevokeRadicalism));
        state.Acceptance = ConquestAcceptance.Of(state, nation) * 0.5f;
        state.AcceptanceOf = nation.Id;
        state.AutonomousSinceDay = -1;
        state.AutonomyRevokedDay = c.ElapsedDays;
        c.Chronicle.Record(c, "autonomy_revoked", nation.Id, -1, place: state.Name);
        c.MarkStrategicChange();
        return true;
    }

    /// <summary>
    /// The computer's use of the same lever, beside its paid settlements in the domestic-crisis
    /// review: a country that is overstretched or unsettled gives its angriest region it has no
    /// title to the right to run itself, if it can pay for it. The player's government is never
    /// touched, and a grant is never taken back.
    /// </summary>
    internal static void ConsiderGrant(Campaign c, Nation nation)
    {
        if (c is null || nation is null || nation.IsHuman || nation.Movement is not null) return;
        // A week's look is enough for a decision taken at most once a quarter.
        if ((c.ElapsedDays + nation.Id) % 7 != 0) return;
        var d = c.Defines;
        if (nation.ImperialOverstretch <= 0f && nation.Stability >= 0.50f) return;
        if (nation.DecisionAvailableDay.GetValueOrDefault(NextKey, -1) > c.ElapsedDays) return;
        if (nation.PoliticalPoints < d.AutonomyPoliticalCost * 1.5f) return;
        var candidate = Angriest(c, nation);
        if (candidate is null) return;
        var price = WeeklyPrice(c, nation, candidate);
        if (nation.WeeklyBalance - WeeklyCost(c, nation) - price < 0f &&
            nation.Treasury < price * MathF.Max(0f, d.AiAutonomyReserveWeeks)) return;
        new GrantRegionalAutonomyCommand { IssuerId = nation.Id, StateMapIndex = candidate.MapIndex }.Apply(c);
        if (Autonomous(candidate))
            nation.DecisionAvailableDay[NextKey] = c.ElapsedDays + Math.Max(1, d.AiAutonomyIntervalDays);
    }

    /// <summary>
    /// The region the computer would grant: unentitled, not yet self-governing, grantable, at least
    /// AiAutonomyRadicalism angry, and the most of its people angry or pulling away; ties to the
    /// lower map index, so every machine picks the same one.
    /// </summary>
    public static ScenarioState Angriest(Campaign c, Nation nation, bool evenWithoutPoints = false)
    {
        if (c is null || nation is null || nation.Population <= 0) return null;
        ScenarioState best = null;
        var bestScore = 0f;
        foreach (var state in c.StatesOf(nation))
        {
            if (Autonomous(state) || state.Radicalism < c.Defines.AiAutonomyRadicalism) continue;
            if (!ConquestAcceptance.Unentitled(c, nation, state)) continue;
            var refusal = Refusal(c, nation, state);
            if (refusal.Length > 0 && !(evenWithoutPoints && refusal == NoPoints)) continue;
            var score = (state.Radicalism + MathF.Max(0f, state.SecessionPressure)) *
                        (float)(state.Population / (double)nation.Population);
            if (best is null || score > bestScore || score == bestScore && state.MapIndex < best.MapIndex)
            {
                best = state;
                bestScore = score;
            }
        }
        return best;
    }
}

/// <summary>
/// The line in the player's list of things to do that says the lever exists.
///
/// Measured on 17 November 2026: the same Serbia, the same conquests, the same rules. Run by the
/// computer it gave ten regions autonomy and spent 183 of ten years' days below the civil-war line;
/// run as a seat nobody presses anything for, it gave none and spent 3147 there, and lost two regions.
/// The rules were fair; the knowledge was not - the grant lived in a region's right-click menu, and
/// nothing said so. So whenever conquered ground strains the state and a region could be given
/// self-government, the situation report (and with it the "Should do" alerts) says which region the
/// computer would choose in the same place, what it costs, and opens the crisis screen on it, where
/// the grant is one press away. Read-only, like every other line of the report.
/// </summary>
public static class ConquestStrainNotice
{
    public const string Id = "conquest_strain";

    public static SituationItem Read(Campaign c, Nation n)
    {
        if (c is null || n is null || n.Movement is not null) return null;
        // Asked quietly: a report read every frame must not log a disabled command every frame.
        if (c.Defines.FeatureCommands.TryGetValue(nameof(GrantRegionalAutonomyCommand), out var feature) &&
            !string.IsNullOrWhiteSpace(feature) && !c.FeatureEnabled(feature)) return null;
        var d = c.Defines;
        if (n.ImperialOverstretch <= 0f && n.Stability >= d.CivilWarStabilityThreshold) return null;
        var region = RegionalAutonomy.Angriest(c, n, evenWithoutPoints: true);
        if (region is null) return null;
        string T(string text) => Loc.Get(text);
        SituationFact F(string label, string value) => new(T(label), value);
        var refusal = RegionalAutonomy.Refusal(c, n, region);
        var unentitled = 0;
        foreach (var state in c.StatesOf(n))
            if (ConquestAcceptance.Unentitled(c, n, state) && !RegionalAutonomy.Autonomous(state)) unentitled++;
        return new SituationItem(Id, n.Stability < d.CivilWarStabilityThreshold ? SituationPriority.Critical : SituationPriority.Warning,
            SituationArea.Society,
            T("Conquered ground strains the state"),
            Loc.Format("{0} could govern itself. Autonomy makes it weigh {1} as much in imperial overstretch, cuts the grievance it takes in, and lets it accept the state twice as fast.",
                region.DisplayName, Fmt.Percent(d.AutonomyOverstretchWeight)),
            new[] { T("Regions whose people are not ours and that we have no title to weigh on the state by their anger: stability falls, and radicalism, risings and coups grow with the overstretch."),
                    T("Their acceptance of our rule grows only in quiet years; above half radicalism it stops.") },
            new[] { Loc.Format("A grant costs {0} political points now and {1} a week: the region keeps {2} of its taxes.",
                        d.AutonomyPoliticalCost.ToString("0"), Fmt.Money(RegionalAutonomy.WeeklyPrice(c, n, region)), Fmt.Percent(d.AutonomyRevenueShare)),
                    T("The same grant is on the region's map menu. Settling a region pays to cool it for one season instead."),
                    refusal.Length > 0 ? T(refusal) : T("The computer uses the same lever on the same terms.") },
            new[] { F("Imperial overstretch", Fmt.Percent(n.ImperialOverstretch)),
                    F("Regions not yet self-governing", unentitled.ToString()),
                    F("Radicalism there", Fmt.Percent(region.Radicalism)),
                    F("Acceptance of rule", Fmt.Percent(ConquestAcceptance.Of(region, n))) },
            SituationRoute.Crises, Loc.Format("Grant autonomy to {0}", region.DisplayName), region.MapIndex);
    }
}

/// <summary>Give one governed region the right to run itself (RegionalAutonomy).</summary>
public sealed class GrantRegionalAutonomyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var nation = c.NationById(IssuerId);
        var region = c.StateByMapIndex(StateMapIndex);
        RegionalAutonomy.Grant(c, nation, region);
    }
}

/// <summary>Take a region's autonomy back. Free, and remembered (RegionalAutonomy).</summary>
public sealed class RevokeRegionalAutonomyCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public int StateMapIndex { get; init; }

    public void Apply(Campaign c)
    {
        if (!MechanicFeatures.Allows(c, this)) return;
        var nation = c.NationById(IssuerId);
        var region = c.StateByMapIndex(StateMapIndex);
        RegionalAutonomy.Revoke(c, nation, region);
    }
}
