using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// The strategic HUD in the form that fits beside the clock.
///
/// The long report already says these things: a war, a formation out of supply, a paper
/// waiting, a coup, a shortage. This reads the same facts and keeps one fixed line each.
/// The full sentence is the tooltip. Nothing here writes an order, a treasury, or the map.
/// </summary>
public static class StrategicHudBrief
{
    /// <summary>What the rail says when none of these facts are present.</summary>
    public const string QuietSentence = "Nothing needs attention.";

    /// <summary>The same cutoff the alert rail already uses for "out of supply".</summary>
    public const float LowSupply = 0.45f;

    public readonly record struct Line(
        string Id, string Short, string Full, string ColourToken, string Screen,
        int ArmyId = -1, int WarId = -1, int BattleId = -1);

    /// <summary>The first short line, or the quiet sentence when the week has none.</summary>
    public static string Shown(IReadOnlyList<Line> lines) =>
        lines is { Count: > 0 } && !string.IsNullOrWhiteSpace(lines[0].Short)
            ? lines[0].Short
            : Loc.Get(QuietSentence);

    /// <summary>Every short line for this country, loudest first. A read: the campaign is not changed.</summary>
    public static List<Line> Read(Campaign campaign, Nation nation)
    {
        var lines = new List<Line>();
        if (campaign is null || nation is null) return lines;
        Events(campaign, lines);
        Peace(campaign, nation, lines);
        Coup(campaign, nation, lines);
        Standoff(campaign, nation, lines);
        Shortage(nation, lines);
        Supply(campaign, nation, lines);
        Battles(campaign, nation, lines);
        Inflation(nation, lines);
        Recession(campaign, nation, lines);
        Treasury(campaign, nation, lines);
        BrainDrain(nation, lines);
        Papers(campaign, nation, lines);
        Wars(campaign, nation, lines);
        lines.Sort((left, right) =>
        {
            var byRank = Rank(left.Id).CompareTo(Rank(right.Id));
            return byRank != 0 ? byRank : string.CompareOrdinal(left.Id, right.Id);
        });
        return lines;
    }

    private static int Rank(string id) => id switch
    {
        "event" or "peace" or "coup" or "clashes" or "shortage" or "supply" or "papers" => 0,
        "crisis" or "battles" or "inflation" or "recession" or "brain" => 1,
        _ => 2
    };

    private static void Events(Campaign campaign, List<Line> lines)
    {
        if (campaign.PendingEvents.Count == 0) return;
        lines.Add(new Line("event", Loc.Get("Event"),
            Loc.Get("A campaign event is waiting for a decision."),
            "state.critical", "Decisions"));
    }

    private static void Peace(Campaign campaign, Nation nation, List<Line> lines)
    {
        var waiting = PeaceOffers.AwaitingAnswer(campaign, nation.Id).Count;
        if (waiting == 0) return;
        lines.Add(new Line("peace", Loc.Format("Peace offer: {0}", waiting),
            Loc.Get("Another government has put a settlement to you and is waiting on an answer."),
            "state.critical", "Wars"));
    }

    private static void Coup(Campaign campaign, Nation nation, List<Line> lines)
    {
        if (nation.CoupPlotDay >= 0)
        {
            lines.Add(new Line("coup", Loc.Get("Coup plot"),
                Loc.Format("Officers are plotting against your government. Raise stability above {0} within {1} days - spending, laws, a war ended - or the army takes power.",
                    Fmt.Percent(campaign.Defines.CoupStabilityThreshold),
                    Math.Max(0, campaign.Defines.CoupPlotDays - (campaign.ElapsedDays - nation.CoupPlotDay))),
                "state.critical", "Nation"));
            return;
        }
        if (Coups.Outlook(campaign, nation) != "high") return;
        lines.Add(new Line("coup", Loc.Get("Coup risk"),
            Loc.Format("Stability is low enough for officers to start plotting. Above {0} nobody plots.",
                Fmt.Percent(campaign.Defines.CoupStabilityThreshold)),
            "state.warning", "Nation"));
    }

    private static void Standoff(Campaign campaign, Nation nation, List<Line> lines)
    {
        DiplomaticRelation hottest = null;
        foreach (var relation in Escalation.Hottest(campaign, campaign.Defines.TensionCrisisAt))
        {
            if (relation.A != nation.Id && relation.B != nation.Id) continue;
            if (Fighting(campaign, relation.A, relation.B)) continue;
            hottest = relation;
            break;
        }
        if (hottest is null) return;
        var rivalId = hottest.A == nation.Id ? hottest.B : hottest.A;
        var rival = campaign.NationById(rivalId);
        var name = string.IsNullOrEmpty(rival?.Tag) ? "?" : rival.Tag;
        var clashing = (TensionStage)hottest.TensionStage >= TensionStage.Clashes;
        lines.Add(new Line(clashing ? "clashes" : "crisis",
            Loc.Format(clashing ? "Clashes with {0}" : "Crisis with {0}", name),
            clashing
                ? Loc.Get("Shooting on the border every week. A border that keeps shooting for months can become a border war; talks in the Conflicts screen cool it.")
                : Loc.Get("A crisis between us. It can grow into armed clashes; talks in the Conflicts screen cool it."),
            clashing ? "state.critical" : "state.warning", "Conflicts"));
    }

    private static void Shortage(Nation nation, List<Line> lines)
    {
        if (nation.ShortageIndex <= 0.10f) return;
        lines.Add(new Line("shortage", Loc.Format("Shortages: {0}", Fmt.Percent(nation.ShortageIndex)),
            Loc.Get("Essential goods are in shortage. Open Economy for production, demand, imports and prices."),
            "state.critical", "Economy"));
    }

    private static void Supply(Campaign campaign, Nation nation, List<Line> lines)
    {
        Army first = null;
        var count = 0;
        foreach (var army in campaign.Armies)
        {
            if (army.NationId != nation.Id || (army.SupplyLevel >= LowSupply && !army.CutOff)) continue;
            count++;
            if (first is null || army.Id < first.Id) first = army;
        }
        if (count == 0) return;
        var full = Loc.Get("Formations are undersupplied or cut off.");
        var reason = StaffReasons.Explain(campaign, first);
        if (reason.Length > 0 && reason != Loc.Get("No staff order has replaced this formation."))
            full = full + "\n" + reason;
        lines.Add(new Line("supply", Loc.Format("Out of supply: {0}", count), full,
            "state.warning", "Military", first.Id));
    }

    private static void Battles(Campaign campaign, Nation nation, List<Line> lines)
    {
        LandBattle latest = null;
        var count = 0;
        foreach (var battle in campaign.Battles)
        {
            if (battle.Ended || (battle.AttackerId != nation.Id && battle.DefenderId != nation.Id)) continue;
            count++;
            if (latest is null || battle.StartedDay > latest.StartedDay ||
                battle.StartedDay == latest.StartedDay && battle.Id < latest.Id)
                latest = battle;
        }
        if (count == 0) return;
        lines.Add(new Line("battles", Loc.Format("Battles: {0}", count),
            Loc.Get("Active battles involving your country."),
            "state.warning", "Wars", BattleId: latest.Id));
    }

    private static void Inflation(Nation nation, List<Line> lines)
    {
        if (nation.Inflation <= InflationCore.Weights.Comfort * 2f) return;
        lines.Add(new Line("inflation", Loc.Format("Inflation: {0}", nation.Inflation.ToString("P0")),
            Loc.Get("Prices are rising well past the comfortable rate. The policy rate in the budget is the lever."),
            "state.warning", "Economy"));
    }

    private static void Recession(Campaign campaign, Nation nation, List<Line> lines)
    {
        var depth = 1f - TreasuryDynamics.OutputFactor(campaign, nation);
        if (depth <= 0.005f) return;
        lines.Add(new Line("recession", Loc.Format("Recession: output -{0}", Fmt.Percent(depth)),
            Loc.Format("A financial crisis, its contagion or a pandemic has the economy contracting until {0}.",
                LandArrangementSystem.DateOfDay(campaign, nation.RecessionUntilDay)),
            depth >= 0.05f ? "state.critical" : "state.warning", "Economy"));
    }

    private static void Treasury(Campaign campaign, Nation nation, List<Line> lines)
    {
        if (!campaign.Defines.TreasuryDynamicsEnabled || nation.Gdp <= 0f ||
            nation.Treasury <= nation.Gdp * campaign.Defines.TreasuryComfortShare) return;
        lines.Add(new Line("treasury", Loc.Get("Treasury overflowing"),
            Loc.Format("Reserves above {0} of GDP are spent down every week - handouts and tax cuts where voters rule, leakage where nobody answers to them. Invest it, or watch it go.",
                Fmt.Percent(campaign.Defines.TreasuryComfortShare)),
            "state.info", "Economy"));
    }

    private static void BrainDrain(Nation nation, List<Line> lines)
    {
        if (nation.BrainDrainRate > -0.10f || nation.WeeklyNetMigration >= 0) return;
        lines.Add(new Line("brain", Loc.Format("Brain drain: {0}", Fmt.Percent(MathF.Abs(nation.BrainDrainRate))),
            Loc.Get("Skilled specialists and households are emigrating. Inspect the Situation Room or Decisions to anchor talent at home."),
            nation.BrainDrainRate <= -0.22f ? "state.critical" : "state.warning", "Situation"));
    }

    private static void Papers(Campaign campaign, Nation nation, List<Line> lines)
    {
        var incoming = DiplomaticActs.Incoming(campaign, nation.Id);
        if (incoming == 0) return;
        lines.Add(new Line("papers", Loc.Format("hud.brief.papers", incoming),
            Loc.Format("{0} papers are waiting for an answer", incoming) + "\n" +
            Loc.Get("a government that does not answer has decided"),
            "state.critical", "Diplomacy"));
    }

    private static void Wars(Campaign campaign, Nation nation, List<Line> lines)
    {
        var found = new List<Line>();
        foreach (var war in campaign.Wars)
        {
            if (war.Ended || !war.Involves(nation.Id)) continue;
            var attacking = war.IsAttacking(nation.Id);
            var enemy = campaign.NationById(attacking ? war.DefenderId : war.AttackerId);
            var tag = string.IsNullOrEmpty(enemy?.Tag) ? "?" : enemy.Tag;
            var score = attacking ? war.WarScore : -war.WarScore;
            var support = attacking ? war.AttackerWarSupport : war.DefenderWarSupport;
            found.Add(new Line("war_" + war.Id,
                Loc.Format("hud.brief.war", tag, score),
                Loc.Format("{0}  score {1:+0;-0;0}  support {2:P0}", tag, score, support),
                "state.negative", "Wars", WarId: war.Id));
        }
        found.Sort((left, right) => string.CompareOrdinal(left.Id, right.Id));
        if (found.Count <= 4)
        {
            lines.AddRange(found);
            return;
        }
        var detail = new System.Text.StringBuilder();
        foreach (var line in found)
        {
            if (detail.Length > 0) detail.Append('\n');
            detail.Append(line.Full);
        }
        lines.Add(new Line("wars", Loc.Format("hud.brief.wars", found.Count), detail.ToString(),
            "state.negative", "Wars", WarId: found[0].WarId));
    }

    private static bool Fighting(Campaign campaign, int a, int b)
    {
        foreach (var war in campaign.Wars)
            if (!war.Ended && war.Opposes(a, b)) return true;
        return false;
    }
}
