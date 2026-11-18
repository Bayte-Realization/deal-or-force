using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static partial class BenchChecks
{

    /// <summary>
    /// The whole point of calibrating emergent output: day zero must still read as the
    /// 2024 the scenario was authored with, or the economy is accurate to its own model
    /// and wrong about the world.
    /// </summary>
    internal static string CalibrationHolds(Campaign campaign)
    {
        foreach (var nation in campaign.Nations.Where(nation => nation.Gdp > 1f)
                     .OrderByDescending(nation => nation.Gdp).Take(40))
        {
            var emergent = nation.GrossOutput * 52f * nation.OutputScale;
            var drift = MathF.Abs(emergent - nation.Gdp) / nation.Gdp;
            if (drift > 0.001f)
                return $"{nation.Tag} drifts {drift:P2} (authored {nation.Gdp:N1}B, emergent {emergent:N1}B)";
        }
        return "";
    }

    internal static string NationsCalibrated(Campaign campaign)
    {
        var uncalibrated = campaign.Nations.Count(nation => nation.Gdp > 0f && nation.OutputScale <= 0f);
        return uncalibrated == 0 ? "" : $"{uncalibrated} nation(s) have no output scale";
    }

    internal static string EconomyMoved(Campaign campaign)
    {
        var moved = campaign.Nations.Count(nation => nation.GrossOutput > 0f);
        return moved > 0 ? "" : "no nation produced anything";
    }

    /// <summary>
    /// Research after the main run: the catalogue is sound, the world has learned something, the
    /// largest economies more than the smallest, and nobody has run out of things to learn in
    /// two years - which is what a tech tree tuned for decades has to look like early on.
    /// </summary>
    internal static string TheWorldResearches(Campaign campaign, int days)
    {
        var problems = TechnologyCatalog.Problems();
        if (problems.Count > 0) return string.Join("; ", problems.Take(6));
        if (TechnologyCatalog.All.Count == 0) return "there are no technologies to research";
        var researching = campaign.Nations.Where(nation => nation.Population > 0).ToList();
        var total = researching.Sum(nation => nation.Technologies.Count);
        if (total == 0) return $"after {days} day(s) no country in the world has finished a technology";
        var largest = researching.OrderByDescending(nation => nation.Gdp).Take(10).Average(nation => nation.Technologies.Count);
        var smallest = researching.OrderBy(nation => nation.Gdp).Take(50).Average(nation => nation.Technologies.Count);
        if (largest <= smallest)
            return $"the ten largest economies average {largest:0.0} technologies and the fifty smallest {smallest:0.0}";
        var finished = researching.Where(nation => nation.Technologies.Count >= TechnologyCatalog.All.Count).Select(n => n.Tag).ToList();
        if (finished.Count > 0)
            return $"{string.Join(", ", finished.Take(5))} learned everything there is in {days} day(s)";
        string Of(string tag) => campaign.NationByTag(tag) is { } nation
            ? $"{tag} {nation.Technologies.Count} ({nation.ResearchPerWeek:0.0}/wk)"
            : "";
        Console.WriteLine($"  research: after {days} day(s) {total:N0} technologies across the world; " +
                          $"the ten largest economies average {largest:0.0}, the fifty smallest {smallest:0.0}; " +
                          string.Join(", ", new[] { "USA", "CHN", "DEU", "RUS", "UKR", "MNG" }.Select(Of).Where(t => t.Length > 0)));
        return "";
    }

    internal static string FiguresReadWell()
    {
        var cases = new (float Billions, string Expected)[]
        {
            (21433.2f, "$21.43T"),
            (845.25f, "$845.3B"),
            (176.76f, "$176.8B"),
            (21.433f, "$21.43B"),
            (1f, "$1B"),
            (0.5f, "$500M"),
            (0.0004f, "$400k"),
            (0f, "$0"),
            (-1200f, "−$1.2T"),
            (-1.29f, "−$1.29B"),
            (-0.0000001f, "$0"),
        };
        foreach (var (billions, expected) in cases)
        {
            var actual = Fmt.Money(billions);
            if (actual != expected) return $"Money({billions}) is \"{actual}\", expected \"{expected}\"";
        }

        if (Fmt.People(1_412_000_000d) != "1.41B") return $"People(1.412bn) is \"{Fmt.People(1_412_000_000d)}\"";
        if (Fmt.People(51_780_579d) != "51.8M") return $"People(51.8m) is \"{Fmt.People(51_780_579d)}\"";
        if (Fmt.MoneyDelta(0f) != "—") return "a zero change should read as an em dash, not +$0";
        if (!Fmt.MoneyDelta(2.5f).StartsWith("+")) return "a rise is missing its sign";
        if (Fmt.Direction(0.001d, 0.005d) != 0) return "a change inside the deadband counted as movement";
        return "";
    }

    /// <summary>
    /// Whether the world's industry adds up.
    ///
    /// A good that nobody makes enough of is not a scarcity, it is a bug: every country in
    /// the world sits in permanent shortage, living standards fall everywhere at once, and
    /// the market stops being a place where a decision can be made. A good that everybody
    /// makes four times over is the opposite bug - it can never run short, so nothing that
    /// happens to it matters and it may as well not be simulated.
    ///
    /// This prints the whole balance sheet and holds every good between the two.
    /// </summary>
    /// <summary>
    /// Whether trade is between countries rather than with nowhere.
    ///
    /// The market was one anonymous pool: a shortfall was met by "the world" at a constant
    /// rate. Nobody could be cut off, so an embargo was unthinkable, a blockade took money
    /// instead of goods, and a country could invade its largest customer and lose nothing
    /// but soldiers. This checks the three things that had to become true: that every import
    /// has a named seller behind it, that a country knows how much of its economy runs
    /// through each partner, and that refusing to trade is actually felt.
    /// </summary>
    /// <summary>
    /// Whether the promises countries make to each other are worth anything.
    ///
    /// Alliances existed as a word. The stance could be Allied and nothing anywhere read it,
    /// so a country could watch its ally dismembered without sending a note; there was no
    /// such thing as a strong small country, and no reason to court anybody. Conquest had no
    /// price either: every province taken made the next one easier, and nothing on the map
    /// grew in proportion to somebody else's success.
    ///
    /// Run on a campaign of its own, because it starts wars.
    /// </summary>
    /// <summary>
    /// Whether a war is a decision rather than an appetite.
    ///
    /// The old judgement had five terms and every one argued for war: strength, dislike,
    /// rivalry, opportunity, and a multiplier for morale. Nothing anywhere argued against,
    /// so the only brake on the map was a low die roll, and a country would happily invade
    /// its largest customer while a coalition formed behind it.
    ///
    /// The claim being checked is precise: the same pair of countries, in the same world, on
    /// the same day, is a war worth having when nothing connects them and not worth having
    /// when half an economy runs through it. If that is not true, the price is decoration.
    /// </summary>
    internal static string ACountryCountsTheCost()
    {
        var campaign = Campaign.CreateBootstrap(20240202);
        if (!AttachMap(campaign)) return "no province map, so nobody borders anybody";

        // A strong country next to a much weaker one - the case the old rule always took.
        Nation strong = null;
        Nation weak = null;
        var bestGap = 3f;
        foreach (var candidate in campaign.Nations)
        {
            if (candidate.Population < 8_000_000 || candidate.IsHuman) continue;
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, candidate.MapIndex))
            {
                var neighbour = NationByMap(campaign, mapIndex);
                if (neighbour is null || neighbour.Population < 500_000) continue;
                if (!CampaignDiplomacy.CanDeclareWar(campaign, candidate.Id, neighbour.Id, out _)) continue;
                var gap = AiStatecraft.StrengthOf(campaign, candidate) /
                          MathF.Max(0.01f, AiStatecraft.StrengthOf(campaign, neighbour));
                if (gap <= bestGap) continue;
                bestGap = gap;
                strong = candidate;
                weak = neighbour;
            }
        }
        if (strong is null || weak is null)
            return "no country in the world is three times its neighbour's strength";

        // Weakness alone is not a reason. China looking at Bhutan and seeing an army a
        // twenty-thousandth of its own is not a war in anybody's 2024; a China that remembers
        // Bhutan taking a valley of its own is the case the AI has to take seriously.
        var unprovoked = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        OpinionSystem.Remember(campaign, CampaignDiplomacy.Get(campaign, strong.Id, weak.Id), "took_our_land");
        var wanted = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        if (wanted <= 0f)
            return $"{strong.Tag} is {bestGap:0.0} times {weak.Tag}'s strength, has a reason, and still " +
                   $"would not consider a war ({wanted:0.00}); the AI has stopped being dangerous rather " +
                   "than started being careful";
        if (unprovoked >= wanted)
            return $"{strong.Tag} wants a war on {weak.Tag} as much without a reason as with one " +
                   $"({unprovoked:0.00} against {wanted:0.00}); a casus belli is decoration";

        // Now give them a real economic relationship and ask again.
        strong.TradePartners[weak.Id] = 100f;
        strong.ImportValue = 100f;
        strong.ExportValue = 100f;
        var withTrade = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        strong.TradePartners.Remove(weak.Id);
        strong.ImportValue = 0f;
        strong.ExportValue = 0f;

        if (withTrade >= wanted)
            return $"{strong.Tag} runs half its economy through {weak.Tag} and wants the war just as " +
                   $"much ({wanted:0.00} against {withTrade:0.00}); trade is not a cost";

        // And friends have to matter too.
        var shelter = campaign.Nations.Find(other =>
            other.Id != strong.Id && other.Id != weak.Id && other.Population > 5_000_000);
        if (shelter is null) return "nobody left to guarantee the weak country";
        var bond = CampaignDiplomacy.Get(campaign, shelter.Id, weak.Id);
        if (shelter.Id == bond.A) bond.GuaranteeByA = true; else bond.GuaranteeByB = true;
        var withFriends = AiStatecraft.JudgeWarFor(campaign, strong, weak);
        if (shelter.Id == bond.A) bond.GuaranteeByA = false; else bond.GuaranteeByB = false;

        if (withFriends >= wanted)
            return $"{shelter.Tag} guaranteed {weak.Tag} and {strong.Tag} did not care " +
                   $"({wanted:0.00} against {withFriends:0.00}); a guarantee deters nobody";

        Console.WriteLine($"  restraint: {strong.Tag} is {bestGap:0.0}x stronger than {weak.Tag} and rates " +
                          $"the war at {unprovoked:0.00} for no reason and {wanted:0.00} with one. Half its trade running through {weak.Tag} takes that " +
                          $"to {withTrade:0.00}; a guarantee from {shelter.Tag} takes it to {withFriends:0.00}.");
        return "";
    }

    internal static string TradeHasNamesOnIt(Campaign campaign)
    {
        if (campaign.TradeRoutes.Count == 0)
            return "not one country sold anything to another; the market is still anonymous";

        // Somebody has to depend on somebody. If no pair of countries carries a meaningful
        // share of each other's trade, nothing downstream of this can weigh a partner.
        // The largest trading relationship in the world by value - not by share, or the
        // answer is always some island whose entire foreign trade is one shipment.
        Nation buyer = null;
        Nation seller = null;
        var largest = 0f;
        foreach (var nation in campaign.Nations)
        foreach (var (partnerId, value) in nation.TradePartners)
        {
            if (value <= largest) continue;
            largest = value;
            buyer = nation;
            seller = campaign.NationById(partnerId);
        }
        if (buyer is null || seller is null)
            return "no two countries in the world trade with each other at all";
        var strongest = buyer.TradeDependenceOn(seller.Id);
        if (strongest < 0.03f)
            return $"the largest trading relationship in the world is worth only {strongest:P1} of " +
                   $"{buyer.Tag}'s trade, which is not a relationship anybody would defend";

        // And refusing has to cost something. Cut the strongest pair and see.
        var before = buyer.ImportValue + buyer.ExportValue;
        var beforeAccess = buyer.MarketAccess;
        var relation = CampaignDiplomacy.Get(campaign, buyer.Id, seller.Id);
        var wasEmbargoed = relation.EmbargoBy(seller.Id);
        new SetEmbargoCommand { IssuerId = seller.Id, TargetId = buyer.Id, Embargo = true }.Apply(campaign);
        MarketSystem.Prime(campaign);
        var after = buyer.ImportValue + buyer.ExportValue;
        var afterAccess = buyer.MarketAccess;

        new SetEmbargoCommand { IssuerId = seller.Id, TargetId = buyer.Id, Embargo = wasEmbargoed }.Apply(campaign);
        MarketSystem.Prime(campaign);

        if (campaign.TradeRoutes.Count == 0) return "the market emptied when one embargo was lifted";
        var stillTrading = 0f;
        foreach (var route in campaign.TradeRoutes)
            if (route.BuyerId == buyer.Id && route.SellerId == seller.Id) stillTrading += route.Value;
        if (stillTrading <= 0.0001f && !wasEmbargoed)
            return $"{seller.Tag} stopped selling to {buyer.Tag} and never started again";

        if (after >= before)
            return $"{seller.Tag} refused to trade with {buyer.Tag} and {buyer.Tag}'s trade did not fall " +
                   $"({before:0.00} before, {after:0.00} after) - an embargo costs nothing";

        Console.WriteLine($"  trade: {campaign.TradeRoutes.Count:N0} route(s) between named countries; " +
                          $"the tightest pair is {buyer.Tag}, which runs {strongest:P0} of its trade through {seller.Tag}.");
        Console.WriteLine($"    an embargo by {seller.Tag} cut {buyer.Tag}'s trade from {before:0.0} to {after:0.0} " +
                          $"and its market access from {beforeAccess:P0} to {afterAccess:P0}.");
        return "";
    }

    /// <summary>
    /// What a country says about its own trade is what its traders do.
    ///
    /// The market was one national switch - a trade policy, an embargo, and that was the
    /// whole of a player's say in an economy the game otherwise models in detail. These are
    /// the controls underneath it: per good, how badly this country wants it, how much of a
    /// shortfall it will cover by buying, how much of a surplus it keeps at home, and what
    /// it will pay; per country, a preference and a refusal that can name a single good.
    ///
    /// Measured on the largest trade route in the world, because a rule that cannot stop the
    /// biggest deal on the map is not a rule. Three of the instructions are tried in turn on
    /// the same route, each taken back before the next, and the world has to come back to
    /// where it started afterwards - an instruction that could not be withdrawn would be a
    /// worse trap than not having one.
    /// </summary>
    internal static string TradeAnswersToItsGovernment(Campaign campaign)
    {
        if (campaign.TradeRoutes.Count == 0) return "nothing is being traded to instruct";

        // The largest single route: one seller, one buyer, one good.
        var biggest = 0f;
        var sellerId = 0;
        var buyerId = 0;
        var commodity = default(Commodity);
        foreach (var route in campaign.TradeRoutes)
        {
            if (route.Value <= biggest) continue;
            biggest = route.Value;
            sellerId = route.SellerId;
            buyerId = route.BuyerId;
            commodity = route.Commodity;
        }
        var seller = campaign.NationById(sellerId);
        var buyer = campaign.NationById(buyerId);
        if (seller is null || buyer is null) return "the largest route has no countries on it";
        var good = CommodityCatalog.IdOf(commodity);
        var routesBefore = campaign.TradeRoutes.Count;

        float Volume()
        {
            var total = 0f;
            foreach (var route in campaign.TradeRoutes)
                if (route.SellerId == sellerId && route.BuyerId == buyerId && route.Commodity == commodity)
                    total += route.Volume;
            return total;
        }

        float Imports()
        {
            return buyer.Market.TryGetValue(commodity, out var quote) ? quote.Imports : 0f;
        }

        void Instruct(Nation nation, string partner, string setting, float value) =>
            new SetTradeInstructionCommand
            {
                IssuerId = nation.Id, GoodId = good, PartnerTag = partner,
                Setting = setting, Value = value
            }.Apply(campaign);

        var opening = Volume();
        if (opening <= 0.0001f) return "the largest route in the world carries nothing";

        // 1. Keep it all at home.
        Instruct(seller, "", TradeDesk.ExportReserve, 1f);
        MarketSystem.Prime(campaign);
        var reserved = Volume();
        Instruct(seller, "", TradeDesk.ExportReserve, 0f);
        MarketSystem.Prime(campaign);
        if (reserved > opening * 0.05f)
            return $"{seller.Tag} reserved all its {good} and still sold {reserved:0.0} of {opening:0.0} to {buyer.Tag}";

        // 2. Refuse that one country, for that one good.
        Instruct(buyer, seller.Tag, TradeDesk.Forbid, 1f);
        MarketSystem.Prime(campaign);
        var forbidden = Volume();
        var elsewhere = 0f;
        foreach (var route in campaign.TradeRoutes)
            if (route.SellerId == sellerId && route.BuyerId == buyerId && route.Commodity != commodity)
                elsewhere += route.Volume;
        Instruct(buyer, seller.Tag, TradeDesk.Forbid, 0f);
        MarketSystem.Prime(campaign);
        if (forbidden > 0.0001f)
            return $"{buyer.Tag} forbade {good} from {seller.Tag} and bought {forbidden:0.0} of it anyway";

        // 3. Go short on purpose.
        Instruct(buyer, "", TradeDesk.ImportCeiling, 0f);
        MarketSystem.Prime(campaign);
        var capped = Imports();
        Instruct(buyer, "", TradeDesk.ImportCeiling, 1f);
        MarketSystem.Prime(campaign);
        if (capped > 0.0001f)
            return $"{buyer.Tag} capped its {good} imports at nothing and imported {capped:0.0}";

        // And the world is where it was.
        if (buyer.TradeSettings.Count > 0 || seller.TradeSettings.Count > 0)
            return "an instruction set back to its default was still being stored, so a default " +
                   "and a decision cannot be told apart";
        var closing = Volume();
        if (closing <= opening * 0.5f)
            return $"the instructions were withdrawn and the route came back at {closing:0.0} of {opening:0.0}";

        Console.WriteLine($"  trade orders: {TradeDesk.All.Count:N0} instruction(s) declared in common/trade. " +
                          $"On the world's largest route - {seller.Tag} selling {good} to {buyer.Tag}, {opening:0.0} a week - " +
                          $"a full export reserve cut it to {reserved:0.0}, a refusal naming that one good cut it to " +
                          $"{forbidden:0.0} while {elsewhere:0.0} of their other trade carried on, and an import ceiling " +
                          $"of nothing took {buyer.Tag}'s {good} imports to {capped:0.0}. Withdrawn, the route is {closing:0.0} again.");
        return "";
    }

    /// <summary>
    /// One line on the goods the building audit of 1 November 2026 touched: for each, what the
    /// world makes against what it asks for, how many countries are short of it, and its price
    /// against the base price, weighted by how much each country asks for. Printed by --growth
    /// every report and by --pulse at the end, so a before/after pair can be read side by side.
    /// </summary>
    internal static string WorldMarketLine(Campaign campaign)
    {
        var parts = new List<string>();
        foreach (var id in new[] { "Oil", "NaturalGas", "RareEarths", "Energy", "Coal", "Steel", "Aluminium", "Electronics", "Chemicals" })
        {
            if (!CommodityCatalog.TryParse(id, out var good)) continue;
            double made = 0, wanted = 0, weightedPrice = 0;
            var shortOf = 0;
            var basePrice = Math.Max(0.0001f, CommodityCatalog.Get(good).BasePrice);
            foreach (var nation in campaign.Nations)
            {
                if (nation.Population <= 0 || !nation.Market.TryGetValue(good, out var quote)) continue;
                made += quote.Production;
                wanted += quote.Demand;
                weightedPrice += quote.Demand * (quote.Price / basePrice);
                if (quote.Demand > 0.001f && quote.IsShortage) shortOf++;
            }
            var price = wanted > 0 ? weightedPrice / wanted : 1;
            parts.Add($"{id} {made:0.0}/{wanted:0.0} ({(wanted > 0 ? made / wanted : 0):0.00}x, {shortOf} short, price x{price:0.00})");
        }
        return "MARKET " + string.Join("; ", parts);
    }

    internal static string TheWorldMakesWhatItNeeds(Campaign campaign)
    {
        var worst = "";
        var worstRatio = float.MaxValue;
        var lines = new List<string>();
        var complaints = new List<string>();
        foreach (var commodity in CommodityCatalog.All)
        {
            var production = 0f;
            var demand = 0f;
            var shortOf = 0;
            foreach (var nation in campaign.Nations)
            {
                if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
                production += quote.Production;
                demand += quote.Demand;
                if (quote.IsShortage) shortOf++;
            }
            if (demand <= 0.001f) continue;
            var ratio = production / demand;
            lines.Add($"{CommodityCatalog.Get(commodity).Name} {ratio:0.00}x ({shortOf:N0} short)");
            if (ratio < worstRatio) { worstRatio = ratio; worst = CommodityCatalog.Get(commodity).Name; }
            if (ratio < 0.75f)
                complaints.Add($"the world makes only {ratio:0.00} of the {CommodityCatalog.Get(commodity).Name} " +
                               $"it wants ({shortOf:N0} countries short)");
            else if (ratio > 4.0f)
                complaints.Add($"the world makes {ratio:0.0}x the {CommodityCatalog.Get(commodity).Name} it wants, " +
                               "so nothing that happens to it can matter");
        }

        Console.WriteLine("  world balance: " + string.Join(", ", lines));
        Console.WriteLine($"    tightest good: {worst} at {worstRatio:0.00} of demand.");
        if (complaints.Count > 0) return string.Join("; ", complaints);
        return "";
    }

    /// <summary>
    /// A tooltip that explains a number must add up to that number.
    ///
    /// This is the whole value of the thing. A player who hovers a falling budget and reads
    /// a list that does not come to what the top bar says has been told something false,
    /// and would have been better served by no explanation at all. Clamps and floors are
    /// where it goes wrong: the parts are computed, then the result is limited, and the
    /// listed parts quietly stop describing it. Every breakdown therefore has to name that
    /// difference rather than swallow it.
    /// </summary>
    internal static string BreakdownsAddUp(Campaign campaign)
    {
        var checkedNations = 0;
        var checkedEntries = 0;
        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0) continue;
            foreach (var (breakdown, shown, what) in new[]
                     {
                         (nation.Budget, nation.WeeklyBalance, "the weekly balance"),
                         (nation.StabilityDrivers, float.NaN, "stability"),
                         (nation.WarSupportDrivers, float.NaN, "war support")
                     })
            {
                if (!breakdown.HasEntries) continue;
                checkedNations++;
                var sum = 0f;
                foreach (var entry in breakdown.Entries)
                {
                    checkedEntries++;
                    if (string.IsNullOrWhiteSpace(entry.Source))
                        return $"{nation.Tag} has an unnamed part in {what}";
                    sum += entry.Value;
                }
                var scale = MathF.Max(1f, MathF.Abs(breakdown.Total));
                if (MathF.Abs(sum - breakdown.Total) / scale > 0.001f)
                    return $"{nation.Tag}: the parts of {what} come to {sum:0.####} but the total says " +
                           $"{breakdown.Total:0.####}";
                if (!float.IsNaN(shown) && MathF.Abs(breakdown.Total - shown) / MathF.Max(1f, MathF.Abs(shown)) > 0.001f)
                    return $"{nation.Tag}: {what} is explained as {breakdown.Total:0.##} but the screen shows " +
                           $"{shown:0.##}";
            }
        }
        if (checkedNations == 0) return "no country explained a single number; the ledgers were never filled";

        Console.WriteLine($"  explained numbers: {checkedEntries:N0} named part(s) across {checkedNations:N0} " +
                          "ledger(s), each adding up to the figure it explains.");
        return "";
    }

    internal static string TwoAxesOverOnePopulation(Campaign campaign)
    {
        // Every trade on a rung its wealth belongs to. The ladder has wide gaps - 0.42 to
        // 0.55 and 0.70 to 0.88 - so a bound in the middle of each nags at nobody and still
        // catches a trade filed on the wrong rung, which is the way these two axes would
        // quietly become one again.
        var misfiled = new List<string>();
        foreach (var trade in DemographyCatalog.Professions)
        {
            var expected = trade.Wealth >= 0.79f ? SocialClass.Upper
                : trade.Wealth >= 0.49f ? SocialClass.Middle
                : SocialClass.Lower;
            if (trade.Class != expected)
                misfiled.Add($"{trade.Id} is worth {trade.Wealth:0.00} and is filed as {trade.Class}");
        }
        if (misfiled.Count > 0) return string.Join("; ", misfiled);

        // And the sums. A region's rungs and its sectors are the same people twice.
        var checkedStates = 0;
        foreach (var state in campaign.States)
        {
            long people = 0, byClass = 0, bySector = 0;
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                people += group.Size;
                if (Enum.IsDefined(group.Class)) byClass += group.Size;
                if (Enum.IsDefined(group.Sector)) bySector += group.Size;
            }
            if (people <= 0) continue;
            checkedStates++;
            if (byClass != people)
                return $"{state.Id}: {people:N0} people but {byClass:N0} on a rung of the ladder";
            if (bySector != people)
                return $"{state.Id}: {people:N0} people but {bySector:N0} in a sector";
        }
        if (checkedStates < 100) return $"only {checkedStates} regions had anybody in them";

        // What the split was for: a landowner works the land, so a country with good fields
        // grows the people who own them. Under the old single axis they counted as townsfolk.
        var world = campaign.States.SelectMany(state => state.PopulationGroups).ToList();
        long land = world.Where(group => group.Sector == Sector.Agriculture).Sum(group => group.Size);
        long upperOnTheLand = world
            .Where(group => group.Sector == Sector.Agriculture && group.Class == SocialClass.Upper)
            .Sum(group => group.Size);
        if (upperOnTheLand <= 0) return "nobody owns any farmland anywhere in the world";

        Console.WriteLine($"  strata: {checkedStates:N0} regions add up along both axes; " +
                          $"{land / 1_000_000.0:N0}M live off the land, of whom " +
                          $"{upperOnTheLand / 1_000_000.0:N1}M own it.");
        return "";
    }

    internal static string TheStatedStatisticsCameTrue(Campaign campaign)
    {
        var stated = campaign.Nations.Where(nation => nation.AgricultureShare > 0f).ToList();
        if (stated.Count < 50) return $"only {stated.Count} countries state what they do for a living";

        var faults = new List<string>();
        var worst = 0.0;
        var worstTag = "";
        var worstPair = (Stated: 0.0, Actual: 0.0);
        foreach (var nation in stated)
        {
            long total = 0, farming = 0;
            foreach (var state in campaign.StatesOf(nation))
            foreach (var group in state.PopulationGroups)
            {
                if (group.Size <= 0) continue;
                total += group.Size;
                if (group.Sector == Sector.Agriculture) farming += group.Size;
            }
            if (total < 100_000) continue;
            var actual = farming / (double)total;
            var slip = Math.Abs(actual - nation.AgricultureShare);
            if (slip > worst)
            {
                worst = slip;
                worstTag = nation.Tag;
                worstPair = (nation.AgricultureShare, actual);
            }
            // Regions vary around the average on purpose, so a few points of slack is the
            // spread and not a fault. Ten would be the bug this caught: aiming at the
            // midpoint of a bent curve instead of averaging across the real regions.
            if (slip > 0.05)
                faults.Add($"{nation.Tag} says {nation.AgricultureShare:P0} of it farms and {actual:P0} does");
        }
        if (faults.Count > 0) return string.Join("; ", faults.Take(5));

        var sample = new[] { "DEU", "USA", "IND", "ETH", "CHN" }
            .Select(tag => campaign.NationByTag(tag))
            .Where(nation => nation is not null && nation.AgricultureShare > 0f)
            .Select(nation =>
            {
                long total = 0, farming = 0;
                foreach (var state in campaign.StatesOf(nation))
                foreach (var group in state.PopulationGroups)
                {
                    if (group.Size <= 0) continue;
                    total += group.Size;
                    if (group.Sector == Sector.Agriculture) farming += group.Size;
                }
                return $"{nation.Tag} {farming / (double)Math.Max(1, total):P0} (said {nation.AgricultureShare:P0})";
            });
        Console.WriteLine($"  statistics: {stated.Count:N0} countries state how urban they are and how much of " +
                          $"them farms, and get it back within {worst:P1} at worst " +
                          $"({worstTag} {worstPair.Stated:P0} -> {worstPair.Actual:P0}).");
        Console.WriteLine($"    on the land: {string.Join(", ", sample)}.");
        return "";
    }

    /// <summary>
    /// The literacy history/countries writes is the literacy a country starts with, and what
    /// productivity reads is the human capital beside it. The deal used to work literacy out of
    /// GDP after history had set it, so Russia's 0.99 started the game as 0.74 - and the
    /// balance of the world was set against the 0.74, which is why the economy keeps it as
    /// the country's human capital rather than reading the literacy rate.
    /// </summary>
    internal static string StatedLiteracyStands(Campaign campaign)
    {
        var stated = campaign.Nations.Count(nation => nation.StatedFigures.Contains("literacy"));
        if (stated < 150) return $"only {stated} countries state their literacy";
        var russia = campaign.NationByTag("RUS");
        if (russia is null) return "there is no Russia to read";
        if (russia.Literacy < 0.95f) return $"Russia starts at {russia.Literacy:P0} literate, and history says 99 %";
        if (russia.HumanCapital > 0.85f)
            return $"Russia's human capital is {russia.HumanCapital:0.00}: the economy is reading the literacy rate";
        var literate = campaign.Nations.Count(nation => nation.Literacy >= 0.95f);
        foreach (var nation in campaign.Nations)
        {
            // A movement has no economy of its own for its people's schooling to make productive.
            if (nation.Movement is not null) continue;
            var expected = 0.72f + nation.HumanCapital * 0.50f;
            if (MathF.Abs(nation.Productivity - expected) > 0.001f)
                return $"{nation.Tag} starts at productivity {nation.Productivity:0.000}, and its human capital puts it at {expected:0.000}";
        }
        Console.WriteLine($"  literacy: {stated:N0} countries state theirs and {literate:N0} are at 95 % or more; " +
                          $"Russia {russia.Literacy:P0} literate on human capital {russia.HumanCapital:0.00}.");
        return "";
    }

    /// <summary>
    /// What the enemy stands on makes nothing for its owner. Half of a Ukrainian region handed
    /// to Russian control makes the region deliver half its output to Ukraine, and handing it
    /// back restores it. On its own world, so the war around it moves nothing.
    /// </summary>
    internal static string OccupationCostsOutput()
    {
        var campaign = Campaign.CreateBootstrap(20240902);
        if (!AttachMap(campaign)) return "no province map to occupy";
        var ukraine = campaign.NationByTag("UKR");
        var russia = campaign.NationByTag("RUS");
        if (ukraine is null || russia is null) return "there is no Ukraine or no Russia";
        var topology = campaign.ProvinceMap;
        ScenarioState region = null;
        List<int> provinces = null;
        foreach (var state in campaign.StatesOf(ukraine).OrderBy(state => state.Id, StringComparer.Ordinal))
        {
            var own = Enumerable.Range(1, topology.ProvinceCount)
                .Where(province => topology.ProvinceToState[province] == state.MapIndex &&
                                   campaign.ProvinceController[province] == ukraine.MapIndex).ToList();
            if (own.Count < 4) continue;
            region = state;
            provinces = own;
            break;
        }
        if (region is null) return "no Ukrainian region of four provinces it holds";
        var before = WarDamage.OutputFactor(campaign, ukraine, region);
        if (MathF.Abs(before - 1f) > 0.001f) return $"{region.Id}, whole and unoccupied, makes {before:P0} of its output";

        var taken = provinces.Take(provinces.Count / 2).ToList();
        foreach (var province in taken) campaign.SetProvinceController(province, (ushort)russia.MapIndex);
        var occupied = StabilityCore.OccupiedShare(campaign, region, ukraine);
        var during = WarDamage.OutputFactor(campaign, ukraine, region);
        var expected = 1f - occupied * campaign.Defines.OccupationOutputLoss;
        if (occupied < 0.2f) return $"Russia holds {taken.Count} of {region.Id}'s {provinces.Count} provinces and it counts {occupied:P0} occupied";
        if (MathF.Abs(during - expected) > 0.001f)
            return $"{region.Id}, {occupied:P0} occupied, makes {during:P0} of its output for Ukraine, not {expected:P0}";

        foreach (var province in taken) campaign.SetProvinceController(province, (ushort)ukraine.MapIndex);
        var after = WarDamage.OutputFactor(campaign, ukraine, region);
        if (MathF.Abs(after - 1f) > 0.001f) return $"{region.Id}, given back, still makes only {after:P0} of its output";
        Console.WriteLine($"  occupation: Russia holding {taken.Count} of {region.Id}'s {provinces.Count} provinces " +
                          $"({occupied:P0}) leaves Ukraine {during:P0} of its output, and giving them back restores it.");
        return "";
    }

    /// <summary>
    /// A country short of a good builds what makes it (AiBuilder). Short of electronics, Germany
    /// builds an electronics plant - the specialist, not the civilian industry that makes a little
    /// of it; short of oil, Russia opens a mine where the oil lies richest for the mines already
    /// there; short of nothing, nobody builds for a shortage.
    /// </summary>
    internal static string ShortagesAreBuiltFor()
    {
        var campaign = Campaign.CreateBootstrap(20240903);
        if (!AttachMap(campaign)) return "no province map to build on";
        static void Short(Nation nation, Commodity good)
        {
            foreach (var quote in nation.Market.Values) quote.Satisfaction = 1f;
            var wanting = nation.Market[good];
            wanting.Satisfaction = 0.3f;
            wanting.Demand = MathF.Max(wanting.Demand, 5f);
        }

        var germany = campaign.NationByTag("DEU");
        var russia = campaign.NationByTag("RUS");
        if (germany is null || russia is null) return "there is no Germany or no Russia";

        foreach (var quote in germany.Market.Values) quote.Satisfaction = 1f;
        if (AiBuilder.Choose(campaign, germany, campaign.StatesOf(germany), out var idle, out _))
            return $"Germany, short of nothing, chose to build {idle} for a shortage";

        Short(germany, Commodity.Electronics);
        if (!AiBuilder.Choose(campaign, germany, campaign.StatesOf(germany), out var electronics, out var plantAt))
            return "Germany, short of electronics, found nothing to build for it";
        if (electronics != BuildingKind.ElectronicsPlant)
            return $"Germany, short of electronics, chose {electronics} over an electronics plant";

        Short(russia, Commodity.Oil);
        var states = campaign.StatesOf(russia);
        if (!AiBuilder.Choose(campaign, russia, states, out var oil, out var mineAt))
            return "Russia, short of oil, found nothing to build for it";
        if (oil != BuildingKind.ExtractionIndustry) return $"Russia, short of oil, chose {oil} over a mine";
        static float Score(ScenarioState state) =>
            state.ResourceDeposits.GetValueOrDefault(Commodity.Oil) / (1f + state.Buildings.GetValueOrDefault(BuildingKind.ExtractionIndustry));
        var richest = states.OrderByDescending(Score).First();
        if (mineAt != richest)
            return $"Russia opened its oil mine in {mineAt.Id} ({Score(mineAt):0.0}) and not {richest.Id} ({Score(richest):0.0})";

        Console.WriteLine($"  building for shortages: Germany short of electronics builds {electronics} in {plantAt.Id}; " +
                          $"Russia short of oil opens a mine in {mineAt.Id} ({mineAt.ResourceDeposits.GetValueOrDefault(Commodity.Oil):0} of oil).");
        return "";
    }

    /// <summary>
    /// A drawn line is held where it is pressed, and stops being re-thought once it is.
    ///
    /// Two failures, opposite and both quiet. A planner that spreads formations evenly
    /// leaves the dangerous end of a line as thin as the safe end, and nothing on screen
    /// says so until it breaks. A planner that re-optimises every tick keeps every division
    /// walking sideways for the rest of the war, which also defends nothing and looks like
    /// activity. So: the most pressed post must be held, and asking twice must give the
    /// same answer.
    /// </summary>
    /// <summary>
    /// An attack waiting to go stands along its own front, not in one province.
    ///
    /// A drawn offensive shares its goals out between the divisions on it, which was right,
    /// and then sent every division sharing a goal to the departure province nearest THAT
    /// GOAL - the same answer for all of them. Seventy-three divisions with four goals a few
    /// provinces apart therefore walked into one tile, which is a traffic jam rather than a
    /// jumping-off line and reads, correctly, as a commander who has lost the plot.
    ///
    /// Built by hand rather than by drawing on the map: the question is what Route does with
    /// a line, and a line assembled here can have exactly the shape that provokes it - one
    /// goal, many divisions, a departure front wide enough to spread over.
    /// </summary>
    /// <summary>
    /// Every word an event puts on the screen is in both tables.
    ///
    /// The localisation check next door counts what the SOURCE asks for by name - every Tr,
    /// every Title, every DetailRow - and read 3,872 of 3,872 while the event window was in
    /// English, because an event's title is not written in the source. It arrives out of
    /// game/events as a value, and a string that arrives as a value cannot be found by
    /// reading the source.
    ///
    /// So this reads the content instead of the code: every title, description, option and
    /// consequence the event catalogue holds, against both tables. It is the shape of check
    /// that any data-driven prose needs, and events are where the game keeps most of it.
    /// </summary>
    /// <summary>
    /// Ports and shipyards need water, and the map has to know where the water is.
    ///
    /// Nothing in the game recorded a coastline before this: the sea is not a province, it
    /// is where the provinces stop, so "is this region coastal" had never been asked and
    /// a shipyard could be ordered in Ulaanbaatar. The answer is now derived from the cell
    /// raster, and a derived answer needs a check against geography nobody disputes.
    ///
    /// Three claims. Landlocked countries have no region on the sea. Maritime countries
    /// have one. And the compromise works: a country with rivers and no coast can build,
    /// for less - which is the whole reason this is a yield and not a refusal.
    /// </summary>
    /// <summary>
    /// An alliance answering a declaration produces one war with sides, not a war per member.
    ///
    /// This is the thing a tester reported and it is worth a check rather than a look,
    /// because the failure is silent: everything works, every ally fights, and the ledger
    /// fills with thirty entries that each have their own war score and each have to be
    /// settled separately. What makes it one war is that the allies are inside it.
    /// </summary>
    /// <summary>
    /// Demolition is the exact inverse of completion, and cancelling is the inverse of
    /// ordering.
    ///
    /// The economy could only ever add: a region industrialised by mistake stayed
    /// industrialised, and twelve slots filled with the wrong thing stayed filled. Both
    /// orders are new, so what they are worth is worth measuring rather than assuming -
    /// a demolition that forgets to take the GDP back is a money printer, and one that
    /// takes back more than it gave is a way to delete a country.
    /// </summary>
    /// <summary>
    /// An economy may be ruined; it may not vanish.
    ///
    /// A country can be conquered, blockaded, bankrupted and pulled apart, and all of those
    /// are gradual. A figure that is a billion on Tuesday and nothing on Wednesday is not a
    /// country in trouble, it is a number that was overwritten - and the report this exists
    /// to answer ("the economy sometimes zeroes out, is it the events?") cannot be
    /// investigated without knowing whether it happens and to whom.
    ///
    /// Secession and annexation are the two legitimate cliffs: a country that has just lost
    /// half its regions is smaller by every measure on purpose, so a day the map changed
    /// under a country is not counted against it.
    /// </summary>
    /// <summary>
    /// A budget screen that reads zero, on the first morning and after every load.
    ///
    /// This is what "the economy sometimes zeroes out" was. The weekly figures and every
    /// breakdown behind them are written by the economic week, the economic week does not
    /// run on day zero, and a breakdown keeps its parts in a private list that no
    /// serialiser writes. So a new campaign showed nothing for seven days and a loaded one
    /// showed nothing until the next Sunday - "sometimes", with no pattern to see.
    ///
    /// Three moments: the first morning, mid-week, and straight out of a save.
    /// </summary>
    /// <summary>
    /// Undefended ground is taken by armies nobody is steering.
    ///
    /// The gap rule was written about the player - a formation refusing an order into an
    /// empty province was the complaint that produced it - and the fix went into the shared
    /// path, so the AI inherited it without anybody checking. An AI that will not walk into
    /// an open province cannot finish a war it is winning: every front freezes the moment
    /// the other side runs out of divisions to put on it.
    ///
    /// So this counts provinces that changed hands with nobody standing in them, for
    /// countries with no human in them, over a year of the world as it is.
    /// </summary>
    /// <summary>
    /// Border gore, counted.
    ///
    /// "Make the AI avoid border gore" is a request about how the map looks, and how the map
    /// looks is downstream of how a treaty is written. A settlement written province by
    /// province draws the front line onto the map, and because a border that cuts a region
    /// in two creates a second region, the damage is permanent and compounds: every later
    /// war starts from the fragments the last one left.
    ///
    /// So the measurement is the number of regions in the world, which starts at 1,826 and
    /// only ever goes up. Two worlds from the same seed, stepped the same number of days,
    /// with the rounding rule on in one and off in the other - which is the only honest way
    /// to say the rule did anything, since the number alone means nothing without the
    /// number it replaced.
    /// </summary>
    /// <summary>
    /// Forty-four formable nations, forty-four drawn flags, and none of them on screen.
    ///
    /// The flags were shipped and credited and named exactly the way common/flags documents
    /// - `flag_formable_soviet_union.png` - and the resolver never asked for that name: the
    /// family list it comes from did not include `formable`, and even with it there the
    /// family sat below the country's own flag, so Russia proclaiming the Soviet Union kept
    /// flying the tricolour.
    ///
    /// Two claims. The flag a formable would fly is the formable's own where one exists, for
    /// every country entitled to proclaim it - a flag that depends on who proclaimed it is a
    /// flag nobody drew. And the ordinary rules still hold underneath: a country that has
    /// proclaimed nothing is unaffected.
    /// </summary>
    /// <summary>
    /// Every folder of data is read by something, and everything read is shipped.
    ///
    /// What is moddable was a list in a guide, which is a claim somebody has to remember to
    /// keep true. The loader knows: every catalogue asks it for a category by name, so it
    /// writes down what it was asked for and this holds that against the disk.
    ///
    /// A folder nothing reads is the worse of the two failures - a modder edits it, nothing
    /// happens, and no error is raised anywhere - and it is the one a list in a document
    /// cannot catch, because the document is where the wrong claim lives.
    /// </summary>
    /// <summary>
    /// How much English the interface still assembles behind the localisation table's back.
    ///
    /// A C# interpolated string is a format the compiler assembles at runtime. There is no
    /// literal in the source for a table to index and nothing for the caption scan to find,
    /// so "the English table indexes every string the game shows" passes while the game
    /// shows English: the whole war screen was composed this way, every line of it, and
    /// every check was green.
    ///
    /// Prose inside an interpolation is the failure. A format with no words in it -
    /// "{0:N0} / {1:N0}", "{0}%" - is punctuation and reads the same in every language, so
    /// it does not count.
    ///
    /// The ceiling only ever goes down. Adding one of these means lowering a number in this
    /// file on purpose, which is the point.
    /// </summary>
    /// <summary>
    /// A portfolio handed to the civil service is actually governed by it.
    ///
    /// Three claims, and the first is the one that rots silently: a delegation that is
    /// switched on and never runs looks identical to one that runs and had nothing to do.
    ///
    ///   - a country that has delegated nothing is left entirely alone. A player who never
    ///     asked must never find their tax rate moved;
    ///   - a delegated budget is governed by the same reasoning every other country gets -
    ///     an emptying treasury is taxed;
    ///   - a delegated trade desk answers a shortage, and withdraws the instruction again
    ///     when the shortage is over. A desk that only ever adds rules ends the decade with
    ///     forty of them.
    /// </summary>
    internal static string ADelegatedPortfolioIsGoverned()
    {
        var campaign = Campaign.CreateBootstrap(20250704);
        var nation = campaign.Nations.FirstOrDefault(country => country.Gdp > 100f);
        if (nation is null) return "no country large enough to have an economy";
        nation.IsHuman = true;

        // ---- nothing delegated, nothing touched ----------------------------------------
        nation.Delegated.Clear();
        nation.Treasury = -nation.Gdp * 0.5f;      // as broke as a country gets
        var untouched = nation.TaxRate;
        for (var day = 0; day < 120; day++) Delegation.Tick(campaign, nation);
        if (MathF.Abs(nation.TaxRate - untouched) > 0.0001f)
            return $"a country that delegated nothing had its tax moved from {untouched:P1} " +
                   $"to {nation.TaxRate:P1}";

        // ---- the budget, delegated -------------------------------------------------------
        // The levy is not one number: an empty treasury is taxed first through the consumption
        // tax every household pays, then the income tax, then firms - the same order the AI's
        // own budget reasoning uses. What has to rise is the whole take, not TaxRate alone.
        float Burden() => nation.TaxRate +
            (BudgetCore.Find("value_added_tax") is { } vat ? BudgetCore.RateOf(nation, vat) : 0f) +
            (BudgetCore.Find("corporate_tax") is { } corporate ? BudgetCore.RateOf(nation, corporate) : 0f);
        Delegation.Set(nation, Delegation.Budget, true);
        var before = Burden();
        for (var month = 0; month < 6; month++) Delegation.Tick(campaign, nation);
        if (Burden() <= before + 0.0001f)
            return $"a delegated treasury sat on {before:P1} of tax through half a year " +
                   $"of insolvency, never raising the take";

        // ---- the trade desk, delegated ----------------------------------------------------
        Delegation.Set(nation, Delegation.Budget, false);
        Delegation.Set(nation, Delegation.Trade, true);
        nation.TradeSettings.Clear();

        var commodity = nation.Market.Keys.FirstOrDefault();
        if (nation.Market.Count == 0) return "the country has no market to run a desk over";
        var quote = nation.Market[commodity];

        // A real shortage: they want far more than they make.
        quote.Production = 10f;
        quote.Demand = 100f;
        quote.Satisfaction = 0.10f;
        Delegation.Tick(campaign, nation);
        var priority = TradeDesk.GoodKey(commodity, TradeDesk.ImportPriority);
        if (!nation.TradeSettings.ContainsKey(priority))
            return $"a delegated desk watched a {1f - quote.Satisfaction:P0} shortage of " +
                   $"{CommodityCatalog.IdOf(commodity)} and gave no instruction about it";

        // And over: they make more than they need, comfortably.
        quote.Production = 200f;
        quote.Demand = 100f;
        quote.Satisfaction = 1f;
        Delegation.Tick(campaign, nation);
        if (nation.TradeSettings.ContainsKey(priority))
            return $"the shortage of {CommodityCatalog.IdOf(commodity)} ended and the desk left " +
                   "its import priority standing";

        Console.WriteLine($"  delegation: {nation.Tag} left alone kept its tax; with the budget " +
                          $"delegated it taxed an empty treasury up to {nation.TaxRate:P1}; with the " +
                          "desk delegated it answered a shortage and withdrew the instruction after.");
        return "";
    }

    /// <summary>
    /// The printing press: real money now, and a bill that outlives the press.
    ///
    /// Four claims, and each is a thing a player does.
    ///
    ///   - a bank nobody has taken will not print. The press is downstream of the seizure,
    ///     and a government that has not made the first decision cannot make the second;
    ///   - once taken, the money is REAL and arrives at once. That is the whole appeal and
    ///     the reason the mechanic is dangerous rather than decorative: a country a week from
    ///     insolvency is solvent on Monday;
    ///   - it shows up in prices, by more than the debt it just paid off takes out of them.
    ///     Printing does not make the problem go away, it changes which problem it is;
    ///   - and stopping does not undo it. What a country is remembered for printing decays
    ///     slowly on its own, so a government that printed through a war goes on paying for
    ///     it through the peace.
    ///
    /// Run on one country in a sandbox with the weeks stepped by hand, like its sibling
    /// above: what is being tested is the arithmetic, not what the world happens to do.
    /// </summary>
    internal static string ThePressPrintsRealMoneyAndRealPrices()
    {
        if (CentralBank.EmissionRange() <= 0f) return "";   // a mod switched the press off

        static void Weeks(Nation nation, Campaign campaign, int weeks)
        {
            var weeklyOutput = nation.Gdp / 52f;
            for (var week = 0; week < weeks; week++)
                InflationCore.Tick(campaign, nation, weeklyOutput, false, 0f);
        }

        var campaign = Campaign.CreateBootstrap(20250211);
        var nation = campaign.Nations.FirstOrDefault(country => country.Gdp > 100f);
        if (nation is null) return "no country large enough to have an economy";

        // ---- a bank nobody has taken will not print ------------------------------------
        nation.GovernmentSetsKeyRate = false;
        CentralBank.SetEmission(nation, CentralBank.EmissionRange());
        if (nation.MoneyPrinting > 0f)
            return "an independent central bank agreed to print money for the government";

        var treasuryBefore = nation.Treasury;
        Weeks(nation, campaign, 8);
        if (nation.Treasury > treasuryBefore + 0.0001f)
            return "the treasury grew while nothing was supposed to be printing";

        // ---- taken, it prints, and the money is real -----------------------------------
        nation.GovernmentSetsKeyRate = true;
        nation.WeeklyBalance = 0f;
        var settled = nation.Inflation;
        var printing = MathF.Min(0.08f, CentralBank.EmissionRange());
        CentralBank.SetEmission(nation, printing);
        if (MathF.Abs(nation.MoneyPrinting - printing) > 0.0001f)
            return $"asked for {printing:P1} of output and got {nation.MoneyPrinting:P1}";

        var before = nation.Treasury;
        Weeks(nation, campaign, 52);
        var gained = nation.Treasury - before;
        var expected = nation.Gdp * printing;
        if (gained < expected * 0.8f)
            return $"a year of printing {printing:P1} of output put {gained:0.0} in the treasury " +
                   $"against the {expected:0.0} it should have";

        // ---- and it is in the prices ---------------------------------------------------
        var printed = nation.Inflation;
        if (printed <= settled + 0.01f)
            return $"a year of printing {printing:P1} of output moved prices from {settled:P1} " +
                   $"to {printed:P1}, which a player would never notice";

        // ---- stopping is not the same as never having started --------------------------
        CentralBank.SetEmission(nation, 0f);
        if (nation.EmissionMemory <= 0f)
            return "the press stopped and the country was immediately forgiven for it";
        Weeks(nation, campaign, 4);
        var justAfter = nation.Inflation;
        if (justAfter <= settled + 0.004f)
            return $"a month after printing {printing:P1} of output for a year, prices were back " +
                   $"to {justAfter:P1} against a resting {settled:P1} - the press cost nothing";

        // ---- but it does fade ----------------------------------------------------------
        Weeks(nation, campaign, 78);
        var muchLater = nation.Inflation;
        if (muchLater >= justAfter)
            return $"eighteen months after the press stopped prices were still {muchLater:P1} " +
                   $"against {justAfter:P1} a month after - nothing is ever forgiven";

        Console.WriteLine($"  emission: printing {printing:P1} of output for a year put " +
                          $"{gained:0.0} in the treasury and took prices from {settled:P1} to " +
                          $"{printed:P1}; a month after stopping they were {justAfter:P1} and " +
                          $"eighteen months after, {muchLater:P1}.");
        return "";
    }

    /// <summary>
    /// Inflation is caused by what a government does and answers to what it decides.
    ///
    /// The economy had no mechanism connecting what a state spends to what its people pay:
    /// a deficit had no consequence until the money ran out, a war cost money and nothing
    /// else, and a blockade that emptied the shelves left the cost of living exactly where
    /// it was. Three claims, and each is a thing a player would do:
    ///
    ///   - a settled country with a balanced budget sits near the resting rate;
    ///   - a country spending far beyond its income sees prices climb;
    ///   - raising the policy rate brings them back down.
    ///
    /// Run on one country in a sandbox of its own, with the weeks stepped by hand, because
    /// what is being tested is the arithmetic rather than what the world happens to do.
    /// </summary>
    internal static string PricesAnswerToTheBudgetAndTheRate()
    {
        static float Settle(Nation nation, Campaign campaign, float balancePerWeek, int weeks,
            float scarcity = 0f, bool atWar = false)
        {
            var weeklyOutput = nation.Gdp / 52f;
            for (var week = 0; week < weeks; week++)
            {
                nation.WeeklyBalance = balancePerWeek * weeklyOutput;
                InflationCore.Tick(campaign, nation, weeklyOutput, atWar, scarcity);
            }
            return nation.Inflation;
        }

        var weights = InflationCore.Weights;
        if (weights.Deficit <= 0f && weights.Policy <= 0f)
            return "";      // a mod has switched the model off; nothing to prove

        // ---- a country doing nothing in particular ------------------------------------
        var calm = Campaign.CreateBootstrap(20250104);
        var nation = calm.Nations.FirstOrDefault(country => country.Gdp > 100f);
        if (nation is null) return "no country large enough to have an economy";
        var settled = Settle(nation, calm, 0f, 120);
        if (MathF.Abs(settled - weights.Base) > 0.004f)
            return $"a balanced budget settled at {settled:P1} rather than the resting rate " +
                   $"of {weights.Base:P1}";

        // ---- and one spending a fifth of its output it has not got ---------------------
        var spent = Settle(nation, calm, -0.20f, 120);
        if (spent <= settled + 0.01f)
            return $"a country spending a fifth of its output beyond its income moved prices " +
                   $"from {settled:P1} to {spent:P1}, which a player would never notice";

        // ---- until somebody puts the rate up -------------------------------------------
        var line = BudgetCore.Find(InflationCore.PolicyLineId);
        if (line is null) return $"there is no '{InflationCore.PolicyLineId}' budget line to set a rate on";
        if (!line.PlayerSet)
            return $"the policy rate is not a government's to set, so there is no lever at all";

        // The rate is the central bank's until a government takes it, and a bank left alone
        // undoes anything else that moves it - so the test has to take it first, which is
        // also what a player has to do.
        nation.GovernmentSetsKeyRate = true;
        BudgetCore.SetRate(nation, line, MathF.Min(line.Maximum, line.Neutral + 1.5f));
        var tightened = Settle(nation, calm, -0.20f, 120);
        if (tightened >= spent - 0.005f)
            return $"the same deficit at a policy rate {MathF.Min(line.Maximum, line.Neutral + 1.5f):0.##} " +
                   $"against a neutral {line.Neutral:0.##} still sits at {tightened:P1} against " +
                   $"{spent:P1}, so the rate does nothing";

        // ---- and cheap money by decree is not free ---------------------------------------
        //
        // The other half of the same lever. A government holding the rate under what the bank
        // wanted is buying growth with prices, and if that costs nothing the decision is not
        // one - cheap money would simply be correct.
        BudgetCore.SetRate(nation, line, line.Minimum);
        var cheap = Settle(nation, calm, -0.20f, 120);
        if (CentralBank.Rule.Credibility > 0f && cheap <= tightened + 0.005f)
            return $"holding the rate on the floor against a bank that wanted " +
                   $"{CentralBank.Wanted(nation, false):P1} cost nothing: {cheap:P1} against " +
                   $"{tightened:P1} with the rate up";

        // ---- and a bank nobody has taken moves the rate itself ---------------------------
        //
        // The part that did not exist before: two hundred and sixty-one countries have a
        // monetary policy now, and the check has to prove it is a policy rather than a
        // number sitting where the scenario left it.
        nation.GovernmentSetsKeyRate = false;
        nation.Inflation = 0.14f;
        var before = CentralBank.KeyRate(nation);
        for (var week = 0; week < 12; week++) CentralBank.Tick(nation, false);
        var after = CentralBank.KeyRate(nation);
        if (CentralBank.Rule.Smoothing > 0f && after <= before + 0.0005f)
            return $"an independent bank watched inflation at {nation.Inflation:P0} and left the " +
                   $"rate at {after:P1}, so nobody in the world is running monetary policy";
        Console.WriteLine($"  central bank: neutral {CentralBank.Rule.NeutralRate:P1}, target " +
                          $"{CentralBank.Rule.Target:P1}; at {nation.Inflation:P0} inflation an " +
                          $"independent bank went from {before:P1} to {after:P1} in twelve weeks.");
        nation.Inflation = settled;
        BudgetCore.SetRate(nation, line, line.Default);

        // ---- and it explains itself ------------------------------------------------------
        nation.IsHuman = true;
        InflationCore.Target(calm, nation, nation.Gdp / 52f, false, 0f, nation.InflationDrivers);
        if (!nation.InflationDrivers.HasEntries)
            return "inflation cannot say where it came from";
        if (MathF.Abs(nation.InflationDrivers.Total -
                      InflationCore.Target(calm, nation, nation.Gdp / 52f, false, 0f)) > 0.0005f)
            return $"the reasons add up to {nation.InflationDrivers.Total:P2} and the rate is " +
                   $"{InflationCore.Target(calm, nation, nation.Gdp / 52f, false, 0f):P2}";

        // ---- and a budget line can be weighted against it --------------------------------
        var weighted = BudgetCore.All.Count(candidate => candidate.Factors.Length > 0);
        Console.WriteLine($"  inflation: balanced {settled:P1}, a fifth-of-output deficit {spent:P1}, " +
                          $"the same deficit with the rate up {tightened:P1}; " +
                          $"{nation.InflationDrivers.Entries.Count} named driver(s), " +
                          $"{weighted:N0} budget line(s) carry factors.");

        // ---- nothing in the world runs away ----------------------------------------------
        var world = Campaign.CreateBootstrap(20250211);
        if (!AttachMap(world)) return "";
        world.IsPaused = false;
        for (var day = 0; day < 730; day++) world.TickDay();
        // The distribution, not the extremes. Two outliers say nothing about whether the
        // model is calibrated; what matters is where the ordinary country sits, and a world
        // where the median country is in double digits is a world in permanent crisis.
        var rates = world.Nations.Select(country => country.Inflation).OrderBy(rate => rate).ToArray();
        var median = rates[rates.Length / 2];
        var hot = rates.Count(rate => rate > InflationCore.Weights.Comfort);
        var worst = world.Nations.OrderByDescending(country => country.Inflation).First();
        var lowest = world.Nations.OrderBy(country => country.Inflation).First();
        Console.WriteLine($"  inflation: after two years the median country is at {median:P1}; " +
                          $"{hot:N0} of {rates.Length:N0} are above the comfortable rate; " +
                          $"the world runs from {lowest.Inflation:P1} ({lowest.Tag}) to " +
                          $"{worst.Inflation:P1} ({worst.Tag}).");
        foreach (var country in world.Nations)
        {
            if (float.IsNaN(country.Inflation) || float.IsInfinity(country.Inflation))
                return $"{country.Tag} has an inflation of {country.Inflation}";
            if (country.Inflation < weights.Floor - 0.0001f || country.Inflation > weights.Ceiling + 0.0001f)
                return $"{country.Tag} is at {country.Inflation:P1}, outside the declared " +
                       $"{weights.Floor:P1}\u2013{weights.Ceiling:P1}";
        }
        return "";
    }

    internal static string TheBooksAreNeverBlank()
    {
        var campaign = Campaign.CreateBootstrap(20241121);
        if (!AttachMap(campaign)) return "";
        var player = campaign.Player;
        if (player is null) return "the bootstrap seated nobody";

        string Blank(string when, Nation nation)
        {
            if (!nation.Budget.HasEntries)
                return $"{when}: {nation.Tag} has no budget to show at all";
            if (MathF.Abs(nation.WeeklyTaxIncome) < 0.000001f &&
                MathF.Abs(nation.WeeklyExpenses) < 0.000001f)
                return $"{when}: {nation.Tag} takes nothing and spends nothing";
            if (MathF.Abs(nation.Budget.Total - nation.WeeklyBalance) > 0.01f)
                return $"{when}: {nation.Tag}'s budget explains {nation.Budget.Total:0.###} " +
                       $"against a balance of {nation.WeeklyBalance:0.###}";
            if (!nation.StabilityDrivers.HasEntries)
                return $"{when}: {nation.Tag} cannot say why it is as stable as it is";
            return "";
        }

        var day0 = Blank("on the first morning", player);
        if (day0.Length > 0) return day0;

        // Mid-week, where the weekly tick has not yet run again.
        campaign.IsPaused = false;
        for (var day = 0; day < 3; day++) campaign.TickDay();
        var midweek = Blank("three days in", campaign.Player);
        if (midweek.Length > 0) return midweek;

        // And out of a save, which is where the parts were being lost.
        var bytes = SaveSystem.ExportNetworkSnapshot(campaign, "bench");
        var restored = SaveSystem.ImportNetworkSnapshot(bytes, "bench").Campaign;
        if (restored is null) return "the campaign would not round-trip through a save";
        var loaded = restored.Player;
        if (loaded is null) return "the restored campaign seated nobody";
        var afterLoad = Blank("straight out of a save", loaded);
        if (afterLoad.Length > 0) return afterLoad;

        Console.WriteLine($"  economy: {player.Tag} explains {player.Budget.Entries.Count} budget " +
                          $"line(s) on day one and {loaded.Budget.Entries.Count} straight out of a save.");
        return "";
    }

    internal static string NoEconomyFallsOffACliff()
    {
        var campaign = Campaign.CreateBootstrap(20241102);
        if (!AttachMap(campaign)) return "";
        campaign.IsPaused = false;

        var gdp = new Dictionary<int, float>();
        var regions = new Dictionary<int, int>();
        foreach (var nation in campaign.Nations)
        {
            gdp[nation.Id] = nation.Gdp;
            regions[nation.Id] = campaign.StatesOf(nation).Length;
        }

        var worst = "";
        var worstDrop = 0f;
        var cliffs = 0;
        for (var day = 0; day < 730; day++)
        {
            campaign.TickDay();
            foreach (var nation in campaign.Nations)
            {
                var held = campaign.StatesOf(nation).Length;
                var was = gdp.GetValueOrDefault(nation.Id, nation.Gdp);
                var hadRegions = regions.GetValueOrDefault(nation.Id, held);
                gdp[nation.Id] = nation.Gdp;
                regions[nation.Id] = held;

                // Ground changed hands, so of course the figures moved.
                if (held != hadRegions) continue;
                // A fallen country's books closing is the fall flow working, not a cliff.
                if (campaign.Fallen.Contains(nation.Id)) continue;
                if (was < 1f) continue;

                var drop = 1f - nation.Gdp / was;
                if (drop < 0.60f) continue;
                cliffs++;
                if (drop <= worstDrop) continue;
                worstDrop = drop;
                worst = $"{nation.Tag} went from {was:0.##}B to {nation.Gdp:0.##}B on " +
                        $"{campaign.Date} with the same {held} region(s)";
            }
        }

        Console.WriteLine($"  economy: two years, {cliffs} single-day fall(s) of more than three fifths " +
                          $"of a country's output with its borders unchanged.");
        if (cliffs > 0)
            return $"an economy vanished overnight: {worst}";
        return "";
    }

    internal static string GdpSane(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            if (float.IsNaN(nation.Gdp) || float.IsInfinity(nation.Gdp))
                return $"{nation.Tag} GDP is {nation.Gdp}";
            if (nation.Gdp < 0f) return $"{nation.Tag} GDP went negative: {nation.Gdp}";
        }
        return "";
    }

}
