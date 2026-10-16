using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// Why the world is calm.
///
/// The bench could already say how many wars a run produced, and why each war that did happen
/// started. It could not say why the other ninety-nine per cent never did: whether nobody is
/// strong enough, whether nobody can be reached, or whether the price of a war is simply higher
/// than what is on offer. That question is what tells a person which number to reach for, and
/// without it every attempt at a livelier world is a guess followed by a re-run.
///
/// Every figure here is read off the same balance sheet the countries themselves decide with -
/// <see cref="AiStatecraft.JudgeWarParts"/> is the one implementation both use - so making the
/// world livelier means moving the number this prints, and nothing else.
/// </summary>
internal static class AiAppetiteReport
{
    public static int Run(Campaign campaign, int days)
    {
        // What the world is actually disputing, before a day is simulated. A term that reads zero
        // because there is nothing behind it reads exactly like a term that is broken, and the
        // difference is not visible in the numbers below.
        ReportDisputedGround(campaign);
        campaign.IsPaused = false;
        for (var day = 1; day <= days; day++)
        {
            campaign.TickDay();
            if (day % 365 != 0 && day != days) continue;
            Sample(campaign);
        }
        return 0;
    }

    private static void Sample(Campaign campaign)
    {
        var byMap = new Dictionary<int, Nation>();
        foreach (var nation in campaign.Nations) byMap[nation.MapIndex] = nation;

        var pairs = 0;
        var gated = 0;
        var valued = 0;
        var wanted = 0;
        var worthSum = 0f;
        var priceSum = 0f;
        var priceTerms = new Dictionary<string, float>(StringComparer.Ordinal);
        var valueTerms = new Dictionary<string, float>(StringComparer.Ordinal);

        foreach (var nation in campaign.Nations)
        {
            if (nation.IsHuman || nation.Movement is not null || nation.Population <= 0 || nation.Landless) continue;
            foreach (var mapIndex in CampaignDiplomacy.Neighbours(campaign, nation.MapIndex))
            {
                if (!byMap.TryGetValue(mapIndex, out var other) || other.Id == nation.Id) continue;
                if (other.Population <= 0) continue;
                pairs++;
                var (value, price, valueParts, priceParts) = AiStatecraft.JudgeWarParts(campaign, nation, other);
                // A value of nothing means the sum never got past the door: not strong enough by
                // the margin the AI insists on, or no ground to take. Those pairs are counted and
                // dropped, because averaging them in would drown every real term in zeroes.
                if (value <= 0f) { gated++; continue; }
                valued++;
                worthSum += value;
                priceSum += price;
                foreach (var (part, amount) in priceParts)
                    if (amount != 0f) priceTerms[part] = priceTerms.GetValueOrDefault(part) + amount;
                foreach (var (part, amount) in valueParts)
                    if (amount != 0f) valueTerms[part] = valueTerms.GetValueOrDefault(part) + amount;
                if (value - price > 0f) wanted++;
            }
        }

        var share = pairs == 0 ? 0f : gated * 100f / pairs;
        Console.WriteLine($"  DAY {campaign.ElapsedDays}: {pairs:N0} neighbours weighed | " +
                          $"{gated:N0} never got past the advantage gate ({share:0}%) | " +
                          $"{valued:N0} worth something | {wanted:N0} worth having");
        if (valued == 0) return;
        Console.WriteLine($"    on offer, per war worth weighing ({worthSum / valued:0.00} average): {Terms(valueTerms, valued)}");
        Console.WriteLine($"    the price, per war worth weighing ({priceSum / valued:0.00} average): {Terms(priceTerms, valued)}");
    }

    /// <summary>The biggest terms first, as a mean per war worth weighing. Long names are the notes
    /// the balance sheet leaves itself and are worth reading, so they are kept as they are.</summary>
    private static string Terms(Dictionary<string, float> terms, int count) =>
        string.Join(", ", terms.OrderByDescending(entry => entry.Value).Take(8)
            .Select(entry => $"{entry.Key} {entry.Value / count:0.00}"));

    /// <summary>
    /// The ground the world actually disputes: regions some country counts as its homeland and
    /// somebody else holds.
    ///
    /// This exists because of `revanche`. It reads 0.00 in every sample of a three-year run, and
    /// the reason is not a broken term - the shipped scenario declares homeland titles for two
    /// dozen regions and every one of them is held by the country that claims it, by a dependency,
    /// or by somebody it is already at war with, so there is nothing in this world for revanchism
    /// to be about. A reader of the numbers cannot tell that from a term that never fires, and the
    /// only way to find out is to read LostLands, TerritorialClaims and the scenario file. One
    /// line here says it instead.
    ///
    /// The same question is worth asking of any term that sits at zero for a whole run: whether
    /// the world contains its inputs at all.
    /// </summary>
    private static void ReportDisputedGround(Campaign campaign)
    {
        var pairs = new List<(Nation Claimant, Nation Holder, ScenarioState Region)>();
        foreach (var nation in campaign.Nations)
        {
            if (nation.IsHuman || nation.Population <= 0 || nation.Landless) continue;
            foreach (var (region, holder) in LostLands.Of(campaign, nation))
                pairs.Add((nation, holder, region));
        }
        var reachable = pairs.Count(pair => !CampaignDiplomacy.AtWar(campaign, pair.Claimant.Id, pair.Holder.Id));
        Console.WriteLine(pairs.Count == 0
            ? "  disputed ground: none - no country's homeland is held by anybody else, so revanchism has nothing to be about"
            : $"  disputed ground: {pairs.Count} homeland regions held by somebody else, {reachable} of them not already at war");
        foreach (var pair in pairs.OrderByDescending(item => item.Region.Population).Take(3))
            Console.WriteLine($"    {pair.Region.Name}: {pair.Claimant.Tag} claims it, {pair.Holder.Tag} holds it, " +
                              $"{pair.Region.Population:N0} people");
    }
}
