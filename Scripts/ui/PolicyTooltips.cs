using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// What a lever actually does, written out from the data that defines it.
///
/// A slider labelled "Payroll levy" with one sentence under it tells a player what the words
/// mean and nothing about whether to move it. What they want is the two things every strategy
/// game is really about - what does this cost me, and what do I get - and both are already in
/// `common/budget`: the yield says what a point is worth, the effects say what a point does.
///
/// So the tooltip is composed rather than written. A mod that adds a tax gets the same
/// explanation of its own numbers, in the player's language, without writing a word of it;
/// and the explanation cannot drift from the behaviour, because it is the behaviour read
/// back out.
/// </summary>
public partial class DealOrForceGame
{
    /// <summary>
    /// The channels a policy pushes, one line each, in the units a player thinks in - the same
    /// "Stability: +0.12 points a week" shape events and decisions use (item 67). They were one
    /// run-on sentence joined with dots, which is readable once and not comparable at all.
    ///
    /// Growth is quoted in percentage points a year because that is how growth is quoted;
    /// the rest move slowly enough that a week is the honest unit. A channel that does
    /// nothing is left out rather than printed as zero - a list of five zeroes is a list
    /// nobody reads to the end of.
    /// </summary>
    private static List<string> EffectLines(float growth, float living, float literacy,
        float radicalism, float stability, float warSupport, float scale)
    {
        var lines = new List<string>();
        void Add(string label, string unit, float value)
        {
            // All six are stored as a fraction of a 0-to-1 quantity, so a hundred turns each
            // of them into the percentage points a player reads them in on every other screen.
            var amount = value * scale * 100f;
            // Matched to the two decimals it is about to be printed with: a smaller cutoff
            // lets through effects that render as "+0.00", which is a line of the tooltip
            // spent telling the player nothing.
            if (Math.Abs(amount) < 0.005f) return;
            lines.Add(Simulation.Loc.Format("{0}: {1}", Tr(label),
                Simulation.Loc.Format(unit, amount.ToString("+0.00;-0.00", CultureInfo.CurrentCulture))));
        }

        Add("Annual growth", "{0} points a year", growth);
        Add("Standard of living", "{0} points a week", living);
        Add("Literacy", "{0} points a week", literacy);
        Add("Radicalism", "{0} points a week", radicalism);
        Add("Stability", "{0} points a week", stability);
        Add("War support", "{0} points a week", warSupport);
        return lines;
    }

    /// <summary>A heading and its lines, indented under it the way every effect block is. Empty when there are none.</summary>
    private static string EffectBlock(string heading, IReadOnlyList<string> lines) =>
        lines.Count == 0 ? "" : heading + "\n" + string.Join("\n", lines.Select(line => "  " + line));

    private static string BasisName(BudgetLine line) => Tr(line.Charge switch
    {
        BudgetBasis.Debt => "what the treasury owes",
        BudgetBasis.Tariffs => "what the customs house collected",
        BudgetBasis.TradeBalance => "the week's trade balance",
        _ => "a week of the country's output"
    });

    /// <summary>
    /// One budget line, explained against the country actually holding it: what it is charged
    /// on, what it is worth at the rate it is set to, what that rate is doing besides moving
    /// money, and what it would be worth wound all the way up.
    /// </summary>
    private string PolicyTooltip(Nation nation, BudgetLine line)
    {
        if (nation is null || line is null) return "";
        var atWar = _campaign is not null &&
                    _campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        var weeklyOutput = nation.Gdp / 52f;
        var rate = BudgetCore.RateOf(nation, line);
        var now = BudgetCore.Amount(nation, line, weeklyOutput, atWar);

        var text = Tr(line.Description);
        text += "\n\n" + Simulation.Loc.Format(
            line.Side == BudgetSide.Revenue
                ? "Charged on {0}. At {1} it raises {2} a week."
                : "Charged on {0}. At {1} it costs {2} a week.",
            BasisName(line), rate.ToString("P1", CultureInfo.CurrentCulture), Fmt.Money(now));

        if (!line.PlayerSet)
        {
            text += "\n" + Tr("Nobody sets this one: it follows from what the country is like.");
            return Ui.Tip(text);
        }

        var doing = EffectLines(line.Growth, line.Living, line.Literacy,
            line.Radicalism, line.Stability, 0f, rate - line.Neutral);
        // A levy that moves none of the six channels really is only moving money. Spending is
        // the other way round: what it buys - an army, a road, a hospital - is the point of it,
        // and saying it does nothing would be the tooltip calling the money wasted.
        text += "\n" + (doing.Count > 0
            ? EffectBlock(Tr("At this rate it also does:"), doing)
            : Tr(line.Side == BudgetSide.Revenue
                ? "At this rate it does nothing but move money."
                : "What this buys is the thing itself, not a change to the figures above."));

        if (Math.Abs(line.Maximum - rate) > 0.0005f)
        {
            var atMax = BudgetCore.AmountAtRate(nation, line, weeklyOutput, atWar, line.Maximum);
            var wound = EffectLines(line.Growth, line.Living, line.Literacy,
                line.Radicalism, line.Stability, 0f, line.Maximum - line.Neutral);
            // The money is the heading and what the rate would do comes under it, line by line,
            // so the two rates can be read against each other.
            var ceiling = Simulation.Loc.Format("Wound up to {0}: {1} a week.",
                line.Maximum.ToString("P0", CultureInfo.CurrentCulture), Fmt.Money(atMax));
            text += "\n" + (wound.Count > 0 ? EffectBlock(ceiling, wound) : ceiling);
        }

        if (line.ResentedAbove > 0f)
            text += "\n" + Simulation.Loc.Format(
                "Nobody minds it below {0}. Above that it starts costing living standards on its own.",
                line.ResentedAbove.ToString("P0", CultureInfo.CurrentCulture));

        if (line.Side == BudgetSide.Revenue && line.Charge == BudgetBasis.Output)
            text += "\n" + Tr("It is charged on the same output as every other levy, so raising it " +
                              "also takes something off what the others collect.");
        return Ui.Tip(text);
    }

    /// <summary>A form of government or an ideology, explained the same way its taxes are.</summary>
    private static string GovernmentTooltip(GovernmentDefinition government)
    {
        var text = Tr(government.Description);
        text += "\n\n" + Simulation.Loc.Format("The centre decides {0} of it.",
            government.Centralisation.ToString("P0", CultureInfo.CurrentCulture));
        var doing = EffectLines(government.Growth, government.Living, government.Literacy,
            government.Radicalism, government.Stability, government.WarSupport, 1f);
        if (doing.Count > 0) text += "\n" + EffectBlock(Tr("While it lasts:"), doing);
        text += "\n" + Simulation.Loc.Format("Leaving it costs about {0} stability.",
            (PoliticsCore.BaseChangeCost * government.ChangeCost).ToString("P0", CultureInfo.CurrentCulture));
        return Ui.Tip(text);
    }

    private static string IdeologyTooltip(IdeologyDefinition ideology)
    {
        var text = Tr(ideology.Description);
        var doing = EffectLines(ideology.Growth, ideology.Living, ideology.Literacy,
            ideology.Radicalism, ideology.Stability, ideology.WarSupport, 1f);
        if (doing.Count > 0) text += "\n\n" + EffectBlock(Tr("While it lasts:"), doing);
        if (ideology.PrefersGovernments.Count > 0)
            text += "\n" + Simulation.Loc.Format("At home in: {0}. Anywhere else costs stability every week.",
                string.Join(", ", ideology.PrefersGovernments
                    .Select(id => Tr(GovernmentCatalog.Of(id).Name))));
        text += "\n" + Tr("Other countries can see this, and how far it stands from their own moves " +
                          "what they make of you.");
        return Ui.Tip(text);
    }
}
