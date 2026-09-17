using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What decides how fast prices are rising, and what that costs.
///
/// Everything here is a weight, and every one of them is in `common/inflation`, so the
/// whole monetary model can be re-tuned - or switched off, by setting every cause to zero -
/// without a rebuild.
/// </summary>
public sealed class InflationWeights
{
    /// <summary>
    /// Where a settled country with a balanced budget, a supplied market and no war ends up.
    ///
    /// Not zero, and deliberately: a target of two per cent or so is what every central bank
    /// in the world this game is set in is actually aiming at, and a model whose resting
    /// state is zero makes any inflation at all look like a crisis.
    /// </summary>
    public float Base = 0.018f;

    /// <summary>
    /// Per unit of deficit measured against a week's output. A government that spends what
    /// it has not got is, one way or another, paying for it with money that did not exist -
    /// and this is the single largest thing a player does to their own prices.
    /// </summary>
    public float Deficit = 0.55f;

    /// <summary>
    /// And the other way. A surplus is money taken out of the economy and not returned, so
    /// it pulls prices down - by less than a deficit pushes them up, because a government
    /// can always spend faster than it can save.
    /// </summary>
    public float Surplus = 0.30f;

    /// <summary>
    /// Per unit of unmet demand across the market. Too much money is one way to get
    /// inflation and too few goods is the other, and a blockaded country discovers the
    /// second one whatever its budget looks like.
    /// </summary>
    public float Scarcity = 0.22f;

    /// <summary>What fighting does on its own, before anything it costs.</summary>
    public float War = 0.020f;

    /// <summary>
    /// Per unit of debt against annual output. Not the interest - that is a budget line -
    /// but what a large debt does to the confidence of everybody pricing anything.
    /// </summary>
    public float Debt = 0.040f;

    /// <summary>
    /// How much a whole point of policy rate above neutral takes off the target.
    ///
    /// This is the lever, and the reason it is worth having one: raising it is not free.
    /// The rate is a budget line, so raising it makes the country's own debt dearer, and the
    /// line's `growth` weight is what it costs the economy.
    /// </summary>
    public float Policy = 0.055f;

    /// <summary>How much of the gap to its target a country closes each economic week.</summary>
    public float Approach = 0.10f;

    /// <summary>
    /// Deflation is possible, and it is its own problem rather than the absence of one.
    /// A country in it is not enjoying cheap bread; it is one where nobody will invest.
    /// </summary>
    public float Floor = -0.04f;

    public float Ceiling = 1.20f;

    /// <summary>
    /// Inflation below this bothers nobody. Every effect below is measured from here, so a
    /// country sitting at its target has a monetary policy rather than a crisis.
    /// </summary>
    public float Comfort = 0.045f;

    /// <summary>
    /// Per point of inflation past comfort, per economic week.
    ///
    /// Calibrated against what else moves these in a week, which is the thing that matters
    /// and the thing I got wrong first: the routine drivers move a region's radicalism by
    /// four ten-thousandths a week at their strongest, and a first attempt at these numbers
    /// moved it by eighteen. The world answered by changing a hundred and thirty-four
    /// governments in fifteen years, which is not politics, it is weather.
    /// </summary>
    public float Living = -0.006f;

    public float Radicalism = 0.004f;

    /// <summary>
    /// The most inflation past comfort that anybody reacts to.
    ///
    /// Unrest saturates and the arithmetic does not: without this, a country at thirty per
    /// cent is six times as radicalising every week as one at nine, for ever, and fifteen
    /// years of that turns a hundred governments over. People living through a currency
    /// crisis are not six times angrier than people living through a bad year - they are
    /// already as angry as that gets, and what the extra twenty points buy is poverty
    /// rather than politics. The standard of living has no such cap, so the damage keeps
    /// arriving; it simply stops arriving as fury.
    /// </summary>
    public float Bite = 0.080f;

    /// <summary>
    /// Zero, and not because inflation does not shake a country.
    ///
    /// It reaches stability already, through the two above: national stability is worked out
    /// from what every region lives on and how angry it is, so a term here would be counting
    /// the same pressure twice - and it would be counting it in the wrong place, because the
    /// weekly tick settles stability towards its target after this runs and would overwrite
    /// most of it anyway. Left declared so a mod that wants the direct version can have it.
    /// </summary>
    public float Stability;

    /// <summary>
    /// And what deflation costs, per point below zero. Separate from the three above because
    /// falling prices hurt through a different door: not the shopping, the wages.
    /// </summary>
    public float DeflationGrowth = -0.45f;
}

/// <summary>
/// Prices, and what a government can do about them.
///
/// The economy could be run into the ground and the only sign of it was a number in the
/// treasury going down. A deficit had no consequence a player could feel until the money
/// ran out, war cost money and nothing else, and a blockade that emptied the shelves left
/// the cost of living exactly where it was. There was no mechanism connecting what a
/// government spends to what its people pay.
///
/// Inflation is that mechanism, and it is deliberately built out of the pieces the game
/// already has rather than beside them:
///
///   - it is CAUSED by the budget the player sets, the market the player trades in, and the
///     wars the player fights;
///   - it is FELT through the budget - `inflation` is a factor a budget line can be weighted
///     against, so wages and welfare cost more as prices rise, in data - and through the
///     standard of living and the radicalism of every region;
///   - it is FOUGHT with the policy rate, which is an ordinary player-set budget line, so
///     raising it makes the country's own debt dearer and slows its growth.
///
/// And it explains itself. The drivers go into a <see cref="Breakdown"/> like the budget and
/// the stability do, so "why is inflation nine per cent" is a hover rather than a wiki page.
/// </summary>
public static class InflationCore
{
    public static InflationWeights Weights { get; private set; } = new();

    static InflationCore() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Weights = new InflationWeights();
        foreach (var document in GameDataLoader.DataDocuments("common", "inflation"))
            using (document)
                Merge(document.RootElement, errors);
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }

    /// <summary>Later files win, field by field, so a mod names only what it disagrees with.</summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Number) continue;
            var value = entry.Value.GetSingle();
            switch (entry.Name.ToLowerInvariant())
            {
                case "base": Weights.Base = value; break;
                case "deficit": Weights.Deficit = value; break;
                case "surplus": Weights.Surplus = value; break;
                case "scarcity": Weights.Scarcity = value; break;
                case "war": Weights.War = value; break;
                case "debt": Weights.Debt = value; break;
                case "policy": Weights.Policy = value; break;
                case "approach": Weights.Approach = value; break;
                case "floor": Weights.Floor = value; break;
                case "ceiling": Weights.Ceiling = value; break;
                case "comfort": Weights.Comfort = value; break;
                case "living": Weights.Living = value; break;
                case "radicalism": Weights.Radicalism = value; break;
                case "bite": Weights.Bite = value; break;
                case "stability": Weights.Stability = value; break;
                case "deflation_growth": Weights.DeflationGrowth = value; break;
                default: errors?.Add($"common/inflation: '{entry.Name}' is not a weight this game has."); break;
            }
        }
    }

    /// <summary>
    /// The rate this country's prices are heading towards, and why.
    ///
    /// Pure: it reads the country and writes the breakdown, and moves nothing. The weekly
    /// tick below is what actually moves the rate, and the screen can ask this whenever it
    /// likes without a week happening.
    /// </summary>
    public static float Target(Campaign campaign, Nation nation, float weeklyOutput, bool atWar,
        float scarcity, Breakdown into = null)
    {
        if (nation is null) return 0f;
        var weights = Weights;
        into?.Begin(Loc.Get("Inflation, where it comes from"), "share");

        var target = weights.Base;
        into?.Add(Loc.Get("A settled economy"), weights.Base);

        // What the government is doing to its own money supply. Measured against a week of
        // output so that a small country running a small deficit and a large one running a
        // large one arrive at the same answer.
        if (weeklyOutput > 0.0001f)
        {
            var balance = nation.WeeklyBalance / weeklyOutput;
            var fromBudget = balance < 0f
                ? -balance * weights.Deficit
                : -balance * weights.Surplus;
            target += fromBudget;
            into?.Add(balance < 0f ? Loc.Get("Spending more than it takes") : Loc.Get("Taking more than it spends"),
                fromBudget);
        }

        // Too few goods, which is the other way to get the same thing. `scarcity` is the
        // share of demand the market did not meet.
        var fromScarcity = M.Clamp01(scarcity) * weights.Scarcity;
        if (fromScarcity != 0f)
        {
            target += fromScarcity;
            into?.Add(Loc.Get("Goods the market could not supply"), fromScarcity);
        }

        if (atWar && weights.War != 0f)
        {
            target += weights.War;
            into?.Add(Loc.Get("A war economy"), weights.War);
        }

        // What is owed, against what is earned in a year. Not the interest on it - that is a
        // budget line - but what the size of it does to confidence.
        var debt = MathF.Max(0f, -nation.Treasury);
        if (debt > 0f && nation.Gdp > 0.0001f)
        {
            var fromDebt = M.Clamp(debt / nation.Gdp, 0f, 4f) * weights.Debt;
            target += fromDebt;
            into?.Add(Loc.Get("The size of the debt"), fromDebt);
        }

        // And the lever. Measured from the line's own neutral point, so a government that
        // has not touched it is neither tightening nor loosening.
        var policy = PolicyRate(nation, out var neutral);
        var fromPolicy = -(policy - neutral) * weights.Policy;
        if (fromPolicy != 0f)
        {
            target += fromPolicy;
            into?.Add(Loc.Get("The policy rate"), fromPolicy);
        }

        // Settle takes the figure the screen will show, not the difference: it records the
        // gap between the parts and the answer as its own line and then adopts the answer.
        var clamped = M.Clamp(target, weights.Floor, weights.Ceiling);
        into?.Settle(clamped, Loc.Get("held at its limit"));
        return clamped;
    }

    /// <summary>
    /// One economic week of it: approach the target, then charge the country for where it is.
    ///
    /// Called from the economic tick with the figures that tick has already worked out, so
    /// nothing here walks the map or the market a second time.
    /// </summary>
    public static void Tick(Campaign campaign, Nation nation, float weeklyOutput, bool atWar, float scarcity)
    {
        if (campaign is null || nation is null) return;
        var weights = Weights;

        // Only a country somebody is looking at gets its reasons written down, for the same
        // reason the budget does: composing seven labels for two hundred and sixty cabinets
        // nobody is reading is garbage made for an empty room.
        var target = Target(campaign, nation, weeklyOutput, atWar, scarcity,
            nation.IsHuman ? nation.InflationDrivers : null);

        nation.Inflation = M.Clamp(
            M.Lerp(nation.Inflation, target, M.Clamp01(weights.Approach)),
            weights.Floor, weights.Ceiling);

        // What it costs. Measured from the comfortable rate, so a country at its target is
        // not being punished for having an economy.
        var excess = nation.Inflation - weights.Comfort;
        if (excess > 0f)
        {
            // What it costs people rises without limit; what it costs the government does
            // not. See `Bite`.
            var felt = weights.Bite > 0f ? MathF.Min(excess, weights.Bite) : excess;
            nation.StandardOfLiving = M.Clamp01(nation.StandardOfLiving + excess * weights.Living);
            if (weights.Stability != 0f)
                nation.Stability = M.Clamp01(nation.Stability + felt * weights.Stability);
            foreach (var state in campaign.StatesOf(nation))
                state.Radicalism = M.Clamp01(state.Radicalism + felt * weights.Radicalism);
        }
        else if (nation.Inflation < 0f)
        {
            // Falling prices, which hurt through the other door: not what things cost, but
            // what anybody is willing to build.
            nation.Productivity = MathF.Max(0.05f,
                nation.Productivity + nation.Inflation * -weights.DeflationGrowth * 0.01f);
        }
    }

    /// <summary>
    /// What this country has its policy rate set to, and what counts as neutral.
    ///
    /// The rate is an ordinary player-set budget line, so it is stored, defaulted, bounded
    /// and shown by machinery that already existed - and a mod that wants the central bank
    /// to work differently edits a row rather than this file.
    /// </summary>
    public static float PolicyRate(Nation nation, out float neutral)
    {
        neutral = 1f;
        var line = BudgetCore.Find(PolicyLineId);
        if (line is null || nation is null) return neutral;
        neutral = line.Neutral;
        return BudgetCore.RateOf(nation, line);
    }

    /// <summary>The budget line the policy rate lives on. Named once.</summary>
    public const string PolicyLineId = "debt_interest";
}
