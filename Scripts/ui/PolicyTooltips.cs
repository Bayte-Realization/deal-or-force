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
    /// The channels a policy pushes, in the units a player thinks in.
    ///
    /// Growth is quoted in percentage points a year because that is how growth is quoted;
    /// the rest move slowly enough that a week is the honest unit. A channel that does
    /// nothing is left out rather than printed as zero - a list of five zeroes is a list
    /// nobody reads to the end of.
    /// </summary>
    private static string DescribeEffects(float growth, float living, float literacy,
        float radicalism, float stability, float warSupport, float scale)
    {
        var parts = new List<string>();
        void Add(string key, float value, float factor)
        {
            var amount = value * scale * factor;
            // Matched to the two decimals it is about to be printed with: a smaller cutoff
            // lets through effects that render as "+0.00", which is a line of the tooltip
            // spent telling the player nothing.
            if (Math.Abs(amount) < 0.005f) return;
            parts.Add(Simulation.Loc.Format(key, amount.ToString("+0.00;-0.00", CultureInfo.CurrentCulture)));
        }

        // All six are stored as a fraction of a 0-to-1 quantity, so a hundred turns each of
        // them into the percentage points a player reads them in on every other screen.
        Add("annual growth {0} points", growth, 100f);
        Add("standard of living {0} points a week", living, 100f);
        Add("literacy {0} points a week", literacy, 100f);
        Add("radicalism {0} points a week", radicalism, 100f);
        Add("stability {0} points a week", stability, 100f);
        Add("war support {0} points a week", warSupport, 100f);
        return parts.Count == 0 ? "" : string.Join(" · ", parts);
    }

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

        var doing = DescribeEffects(line.Growth, line.Living, line.Literacy,
            line.Radicalism, line.Stability, 0f, rate - line.Neutral);
        // A levy that moves none of the six channels really is only moving money. Spending is
        // the other way round: what it buys - an army, a road, a hospital - is the point of it,
        // and saying it does nothing would be the tooltip calling the money wasted.
        text += "\n" + (doing.Length > 0
            ? Simulation.Loc.Format("At this rate it is also doing: {0}.", doing)
            : Tr(line.Side == BudgetSide.Revenue
                ? "At this rate it does nothing but move money."
                : "What this buys is the thing itself, not a change to the figures above."));

        if (Math.Abs(line.Maximum - rate) > 0.0005f)
        {
            var atMax = BudgetCore.AmountAtRate(nation, line, weeklyOutput, atWar, line.Maximum);
            var wound = DescribeEffects(line.Growth, line.Living, line.Literacy,
                line.Radicalism, line.Stability, 0f, line.Maximum - line.Neutral);
            // Two frames rather than one frame with a hole that is sometimes empty: the
            // join between the money and the effects is a piece of language, and ", and "
            // is only one language's way of writing it.
            var ceiling = line.Maximum.ToString("P0", CultureInfo.CurrentCulture);
            text += "\n" + (wound.Length > 0
                ? Simulation.Loc.Format("Wound up to {0}: {1} a week, and {2}.",
                    ceiling, Fmt.Money(atMax), wound)
                : Simulation.Loc.Format("Wound up to {0}: {1} a week.", ceiling, Fmt.Money(atMax)));
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
        var doing = DescribeEffects(government.Growth, government.Living, government.Literacy,
            government.Radicalism, government.Stability, government.WarSupport, 1f);
        if (doing.Length > 0) text += "\n" + Simulation.Loc.Format("While it lasts: {0}.", doing);
        text += "\n" + Simulation.Loc.Format("Leaving it costs about {0} stability.",
            (PoliticsCore.BaseChangeCost * government.ChangeCost).ToString("P0", CultureInfo.CurrentCulture));
        return Ui.Tip(text);
    }

    private static string IdeologyTooltip(IdeologyDefinition ideology)
    {
        var text = Tr(ideology.Description);
        var doing = DescribeEffects(ideology.Growth, ideology.Living, ideology.Literacy,
            ideology.Radicalism, ideology.Stability, ideology.WarSupport, 1f);
        if (doing.Length > 0) text += "\n\n" + Simulation.Loc.Format("While it lasts: {0}.", doing);
        if (ideology.PrefersGovernments.Count > 0)
            text += "\n" + Simulation.Loc.Format("At home in: {0}. Anywhere else costs stability every week.",
                string.Join(", ", ideology.PrefersGovernments
                    .Select(id => Tr(GovernmentCatalog.Of(id).Name))));
        text += "\n" + Tr("Other countries can see this, and how far it stands from their own moves " +
                          "what they make of you.");
        return Ui.Tip(text);
    }
}
