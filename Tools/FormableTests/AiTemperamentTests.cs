using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A government's character, as opposed to its doctrine.
///
/// The doctrine (<see cref="AiStrategyCatalog"/>) is picked from a handful of profiles by size,
/// so before this every major power in the world played the same way. These are the properties
/// the character has to have for the world to read as a set of governments rather than one
/// algorithm wearing a hundred flags: it is stable, it is the same on every machine playing the
/// seed, it differs between countries, it averages out to no change in how violent the world is,
/// a mod can turn it off, and it answers to the state of the country that has it.
/// </summary>
static class AiTemperamentTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("ai temperament: " + label); Console.WriteLine("PASS ai temperament: " + label); }

    public static void Run()
    {
        const int seed = 20261016;
        var c = Campaign.CreateBootstrap(seed);
        // Who neighbours whom comes off the province graph, so without the map attached every
        // country in the world is an island and there is no pair to ask anything about.
        MapRasterCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError),
            "the world map loads: " + mapError);
        c.AttachProvinceTopology(map);
        var all = c.Nations.Where(n => n.Population > 0 && !n.Landless).ToArray();
        // Sixty is enough to measure a spread over and keeps the statistics cheap; the two
        // behaviour checks below want the whole world, because they are looking for one pair.
        var nations = all.Take(60).ToArray();
        Check(nations.Length >= 8, "the bootstrap world has enough countries to measure a spread over");

        // Stable. A character that drifted from week to week would be weather, not a government,
        // and the player could not form any expectation of anybody.
        var first = nations.ToDictionary(n => n.Id, n => AiTemperament.Of(c, n));
        foreach (var nation in nations)
            Check(AiTemperament.Of(c, nation) == first[nation.Id], $"{nation.Tag}'s character is stable within a day");

        // Deterministic across machines. The same seed has to give the same governments the same
        // characters, or a run could not be reproduced and no measurement of it would mean anything.
        var again = Campaign.CreateBootstrap(seed);
        foreach (var nation in nations.Take(12))
        {
            var twin = again.NationByTag(nation.Tag);
            Check(twin is not null && AiTemperament.Of(again, twin) == first[nation.Id],
                $"{nation.Tag} is the same government in a second world on the same seed");
        }

        // Not alike. This is the whole point: two countries of the same size used to make the
        // same decision on the same day, every time.
        var opportunisms = nations.Select(n => AiTemperament.Of(c, n).Opportunism).Distinct().Count();
        var volatilities = nations.Select(n => AiTemperament.Of(c, n).Volatility).Distinct().Count();
        Check(opportunisms > nations.Length / 2 && volatilities > nations.Length / 2,
            $"countries are told apart by character ({opportunisms} distinct opportunisms, {volatilities} volatilities of {nations.Length})");

        // Centred. Adding character must not quietly make the world more warlike, which is what
        // a multiplier that averaged above one would do.
        var spread = c.Defines.AiTemperamentSpread;
        var mean = nations.Average(n => AiTemperament.OpportunismOf(c, n));
        Check(MathF.Abs(mean - 1f) < 0.12f, $"the opportunism multiplier averages to no change (mean {mean:0.000})");

        // Bounded, and never able to invert an appetite.
        Check(nations.All(n =>
                AiTemperament.OpportunismOf(c, n) >= 0.05f &&
                AiTemperament.OpportunismOf(c, n) <= 1f + spread + 0.001f),
            "every multiplier stays inside its bounds and away from zero");

        // And a mod that does not want a mercurial world can say so, and be obeyed exactly.
        c.Defines.AiTemperamentSpread = 0f;
        Check(nations.All(n => AiTemperament.OpportunismOf(c, n) == 1f &&
                               AiTemperament.VolatilityOf(c, n) == 1f &&
                               AiTemperament.CunningOf(c, n) == 1f &&
                               AiTemperament.ForesightOf(c, n) == 1f),
            "a spread of zero gives every government the ordinary character, exactly");
        c.Defines.AiTemperamentSpread = spread;

        Earned(seed, nations);
        Openings(c, all);
        TargetDistress(c, all);
        Betrayal(c, all);
        Balance(c, all);
    }

    /// <summary>
    /// Target financial and institutional distress: sovereign debt default, coup pressure, and runaway
    /// inflation invite opportunistic aggression, while an attacker's own insolvency acts as a brake.
    /// </summary>
    private static void TargetDistress(Campaign c, Nation[] sample)
    {
        var (watcher, target, _, plain) = Pair(c, sample);
        var oldDebt = target.DebtCondition;
        var oldWatcherDebt = watcher.DebtCondition;
        try
        {
            target.DebtCondition = DebtCondition.Default;
            var (valueWithDefault, _, valueParts, _) = AiStatecraft.JudgeWarParts(c, watcher, target);
            var opportunistBonus = valueParts.FirstOrDefault(p => p.Part == "target distress").Amount;
            Check(opportunistBonus > 0f,
                $"a defaulting neighbour is recognized as in distress (bonus {opportunistBonus:0.00})");

            // Attacker insolvency check
            watcher.DebtCondition = DebtCondition.Default;
            var (_, priceWithInsolvency, _, priceParts) = AiStatecraft.JudgeWarParts(c, watcher, target);
            var insolvencyCaution = priceParts.FirstOrDefault(p => p.Part == "insolvency").Amount;
            Check(insolvencyCaution >= c.Defines.AiInsolvencyCaution - 0.01f,
                $"an insolvent attacker faces cautious restraint (caution {insolvencyCaution:0.00})");
        }
        finally
        {
            target.DebtCondition = oldDebt;
            watcher.DebtCondition = oldWatcherDebt;
        }
    }

    /// <summary>
    /// The balance sheet the countries decide with and the balance sheet the bench reads are one
    /// thing. If they could drift, every measurement of why a war did or did not happen would be a
    /// measurement of something else - and the arithmetic would still look right, which is the
    /// worst kind of wrong.
    /// </summary>
    private static void Balance(Campaign c, Nation[] sample)
    {
        var (watcher, target, _, _) = Pair(c, sample);
        var (value, price, valueParts, priceParts) = AiStatecraft.JudgeWarParts(c, watcher, target);
        Check(value > 0f && valueParts.Count > 0, "a war worth weighing comes with its items listed");
        var offered = valueParts.Sum(part => part.Amount);
        Check(MathF.Abs(offered - value) < 0.01f,
            $"the items of what is on offer add up to what is on offer ({offered:0.00} against {value:0.00})");
        var asked = priceParts.Sum(part => part.Amount);
        Check(MathF.Abs(asked - price) < 0.01f,
            $"the items of the price add up to the price ({asked:0.00} against {price:0.00})");
        // The fixture pair is not allied, so no nerve stands between the sum and the decision.
        Check(MathF.Abs(AiStatecraft.JudgeWarFor(c, watcher, target) - (value - price)) < 0.01f,
            $"the judgement the country reaches is the sum the bench is shown ({value - price:0.00})");
    }

    /// <summary>
    /// Born, then earned. Two worlds on one seed hold the same country with the same birth; only
    /// the state of it differs, so whatever differs between them was earned. A coup or a collapse
    /// has to change how a country behaves, or the character is decoration.
    /// </summary>
    private static void Earned(int seed, Nation[] sample)
    {
        var steady = Campaign.CreateBootstrap(seed);
        var crisis = Campaign.CreateBootstrap(seed);
        var tag = sample.First(n => steady.NationByTag(n.Tag) is not null).Tag;
        var calm = steady.NationByTag(tag);
        var failing = crisis.NationByTag(tag);
        calm.Stability = 0.98f;
        failing.Stability = 0.02f;
        // Read in this order on purpose: the character table is stamped with the campaign, so
        // each of these is drawn fresh from the world it belongs to.
        var calmCharacter = AiTemperament.Of(steady, calm);
        var failingCharacter = AiTemperament.Of(crisis, failing);
        Check(failingCharacter.Volatility > calmCharacter.Volatility,
            $"{tag} is more mercurial when it is falling apart than when it is settled");
        Check(calmCharacter.Foresight > failingCharacter.Foresight,
            $"{tag} holds a plan longer when it is settled than when it may not survive the year");
    }

    /// <summary>
    /// An opening is measured, not assumed. A neighbour fighting somebody else is worth more than
    /// one at peace, and one losing badly is worth more than one holding its own - two things the
    /// old flat bonus could not tell apart, which is why every busy enemy looked the same.
    /// </summary>
    private static void Openings(Campaign c, Nation[] sample)
    {
        var (watcher, target, _, _) = Pair(c, sample);
        Check(AiStatecraft.Stretch(c, target) == 0f, "a neighbour at peace is not stretched at all");

        // Somebody for the target to be busy with. Not the watcher, or the opening would be our
        // own war, and not a civil conflict - a neighbour of a civil war arms a side, it does not
        // invade, and that case is deliberately priced the other way.
        var third = sample.FirstOrDefault(n => n.Id != watcher.Id && n.Id != target.Id &&
                                              !CampaignDiplomacy.AtWar(c, n.Id, target.Id));
        Check(third is not null, "the world has a third country to make the target busy with");
        var busy = new StrategicWar { Id = 9411, AttackerId = third.Id, DefenderId = target.Id, WarScore = 10f };
        c.Wars.Add(busy);
        try
        {
            var holding = AiStatecraft.Stretch(c, target);
            Check(holding > 0f, "a neighbour already fighting somebody else is stretched");

            // The same single front, going badly. Nothing else about the world has changed.
            busy.WarScore = 95f;
            var losing = AiStatecraft.Stretch(c, target);
            Check(losing > holding,
                $"an enemy losing badly is more stretched than one holding its own ({holding:0.00} then {losing:0.00})");

            // And a second front on top of the first, which is the other half of being thin.
            busy.WarScore = 10f;
            var fourth = sample.FirstOrDefault(n => n.Id != watcher.Id && n.Id != target.Id && n.Id != third.Id);
            Check(fourth is not null, "the world has a fourth country to open a second front");
            var second = new StrategicWar { Id = 9412, AttackerId = fourth.Id, DefenderId = target.Id, WarScore = 10f };
            c.Wars.Add(second);
            try
            {
                Check(AiStatecraft.Stretch(c, target) > holding,
                    "an enemy fighting on two fronts is more stretched than one fighting on a single one");
            }
            finally
            {
                c.Wars.Remove(second);
            }

            // The appetite has to actually follow the stretch, or measuring it is decoration.
            // Switching the appetite off is the only thing that changes between these two calls,
            // so the difference between them is the opening and nothing else.
            var appetite = c.Defines.AiOpportunismAppetite;
            var wanted = AiStatecraft.JudgeWarFor(c, watcher, target);
            c.Defines.AiOpportunismAppetite = 0f;
            var indifferent = AiStatecraft.JudgeWarFor(c, watcher, target);
            c.Defines.AiOpportunismAppetite = appetite;
            Check(wanted > indifferent,
                $"a stretched neighbour is worth more to a watcher than the same neighbour is without an appetite ({indifferent:0.00} then {wanted:0.00})");
        }
        finally
        {
            c.Wars.Remove(busy);
        }
    }
    /// <summary>
    /// An alliance used to be a wall: the sum was never worked out at all, so no government in the
    /// world would ever turn on a friend for profit and every alliance was a permanent fact. It is
    /// a price now - which means it still has to make the war less attractive, and the price of
    /// breaking it has to be the price the data declares and not somebody's arithmetic.
    /// </summary>
    private static void Betrayal(Campaign c, Nation[] sample)
    {
        var nerve = c.Defines.AiBetrayalNerve;
        var notoriety = c.Defines.AiBetrayalNotoriety;
        var (watcher, target, bond, plain) = Pair(c, sample);

        var previous = bond.Stance;
        try
        {
            bond.Stance = DiplomaticStance.Allied;
            c.Defines.AiBetrayalNerve = 1e6f;
            Check(AiStatecraft.JudgeWarFor(c, watcher, target) == -10f,
                "an alliance still forbids a war that does not clear the nerve");

            c.Defines.AiBetrayalNerve = -1e6f;
            c.Defines.AiBetrayalNotoriety = 0f;
            var free = AiStatecraft.JudgeWarFor(c, watcher, target);
            Check(free > -10f, "an alliance is weighed now, rather than refused without being read");
            Check(free < plain, "and it still makes a war less worth having than the same war without one");

            // Only the declared price changes between these two calls, so the difference has to be
            // exactly it. This is the assertion that would catch the term being applied twice,
            // scaled wrongly, or applied to a war that has nothing to do with an alliance.
            c.Defines.AiBetrayalNotoriety = notoriety;
            var priced = AiStatecraft.JudgeWarFor(c, watcher, target);
            Check(MathF.Abs(priced - (free - notoriety * 0.1f)) < 0.001f,
                $"turning on an ally costs exactly the standing the data declares ({free:0.00} then {priced:0.00})");
        }
        finally
        {
            bond.Stance = previous;
            c.Defines.AiBetrayalNerve = nerve;
            c.Defines.AiBetrayalNotoriety = notoriety;
        }
    }

    /// <summary>
    /// Two neighbours, both at peace with everybody and with each other, not allied, and with a war
    /// between them worth weighing at all.
    ///
    /// That last condition is the one that matters. <see cref="AiStatecraft"/> stops the balance
    /// sheet at the door when the watcher is nowhere near strong enough, and returns nothing - so a
    /// pair picked on geography alone gives every assertion below an empty sheet to pass against,
    /// and a test that passes on nothing is worse than no test. Neighbours are asked of the border
    /// table rather than by testing every pair, which on a world this size is forty thousand
    /// questions to find one.
    /// </summary>
    private static (Nation Watcher, Nation Target, DiplomaticRelation Bond, float Plain) Pair(Campaign c, Nation[] sample)
    {
        var byMap = new System.Collections.Generic.Dictionary<int, Nation>();
        foreach (var nation in sample) byMap[nation.MapIndex] = nation;
        foreach (var watcher in sample)
        {
            if (AtWar(c, watcher)) continue;
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(c, watcher.MapIndex))
            {
                if (!byMap.TryGetValue(mapIndex, out var target) || target.Id == watcher.Id) continue;
                if (AtWar(c, target) || CampaignDiplomacy.AtWar(c, watcher.Id, target.Id)) continue;
                var bond = CampaignDiplomacy.Get(c, watcher.Id, target.Id);
                if (bond.Stance == DiplomaticStance.Allied) continue;
                var plain = AiStatecraft.JudgeWarFor(c, watcher, target);
                if (plain <= -9f) continue;
                return (watcher, target, bond, plain);
            }
        }
        throw new Exception("ai temperament: the world has no two peaceful neighbours with a war worth weighing");
    }

    private static bool AtWar(Campaign c, Nation nation)
    {
        foreach (var war in c.Wars)
            if (!war.Ended && war.Involves(nation.Id)) return true;
        return false;
    }
}


