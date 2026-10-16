using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

static class PeaceTidyTests
{
    public static void Run(Campaign campaign)
    {
        var map = campaign.ProvinceMap;
        var groups = Enumerable.Range(1, map.ProvinceCount)
            .GroupBy(p => (int)map.ProvinceToState[p])
            .Where(g => g.Key > 0 && g.Count() >= 4)
            .Take(48)
            .Select(g => g.ToArray())
            .ToArray();
        if (groups.Length < 2) throw new Exception("Peace rounding fixture needs two multi-province regions");

        var brush = groups.SelectMany(g => g.Take((g.Length + 1) / 2)).ToArray();
        for (var i = 0; i < 8; i++) PeaceConference.Tidy(campaign, null, brush, 1);
        const int repetitions = 100;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < repetitions; i++) PeaceConference.Tidy(campaign, null, brush, 1);
        clock.Stop();
        Console.WriteLine($"Peace rounding: {map.ProvinceCount} provinces, {groups.Length} regions, " +
                          $"{repetitions} runs in {clock.Elapsed.TotalMilliseconds:0.0} ms");

        var whole = groups[0];
        var fragment = groups[1];
        var drawn = whole.Concat(new[] { fragment[0] }).ToArray();
        var noRoundingUp = PeaceConference.Tidy(campaign, null, drawn, 1, roundUp: false);
        Check(noRoundingUp.ToHashSet().SetEquals(whole),
            "disabled round-up returns the sliver while retaining the complete region");

        var half = fragment.Take((fragment.Length + 1) / 2).ToArray();
        var rounded = PeaceConference.Tidy(campaign, null, half, 1);
        Check(rounded.ToHashSet().SetEquals(fragment), "majority claim expands to one complete region");

        var fallback = PeaceConference.Tidy(campaign, null, new[] { fragment[0] }, 1, roundUp: false);
        Check(fallback.ToHashSet().SetEquals(fragment), "a sole sliver falls back to one complete region");

        var expected = groups.SelectMany(g => g).ToHashSet();
        Check(PeaceConference.Tidy(campaign, null, brush, 1).ToHashSet().SetEquals(expected),
            "large brush resolves every touched majority region");

        var wholeRegions = campaign.Defines.PeaceWholeRegions;
        var provinceCost = campaign.Defines.PeaceCostPerProvince;
        campaign.Defines.PeaceWholeRegions = false;
        campaign.Defines.PeaceCostPerProvince = 0.01f;
        try
        {
            var named = PeaceConference.Tidy(campaign, null, half, 1);
            Check(named.ToHashSet().SetEquals(half),
                "province peace signs the named provinces and does not fill the region");

            var sizeOf = new Dictionary<int, int>();
            for (var province = 1; province <= map.ProvinceCount; province++)
                sizeOf[map.ProvinceToState[province]] = sizeOf.GetValueOrDefault(map.ProvinceToState[province]) + 1;
            ushort winnerNation = 0;
            var occupied = 0;
            var region = 0;
            for (var province = 1; province <= map.ProvinceCount && occupied == 0; province++)
            {
                var state = map.ProvinceToState[province];
                if (sizeOf.GetValueOrDefault(state) < 4) continue;
                foreach (var neighbour in map.Neighbors[province])
                {
                    var other = map.ProvinceToNation[neighbour];
                    if (other == 0 || other == map.ProvinceToNation[province]) continue;
                    occupied = province;
                    winnerNation = other;
                    region = state;
                    break;
                }
            }
            Check(occupied > 0 && winnerNation != 0, "the map has a province an army can hold against a neighbour");
            var winner = campaign.Nations.Find(nation => nation.MapIndex == winnerNation);
            var loser = campaign.Nations.Find(nation => nation.MapIndex == map.ProvinceToNation[occupied]);
            var war = new StrategicWar { Id = 991, AttackerId = winner.Id, DefenderId = loser.Id };
            var previous = campaign.ProvinceController[occupied];
            campaign.SetProvinceController(occupied, winnerNation);
            try
            {
                var settled = PeaceShape.SettleProvinces(campaign, war, new[] { occupied }, winner);
                Check(settled.Count == 1 && settled[0] == occupied,
                    "an enforced peace keeps the one province the army holds");
                var demand = new PeaceDemand { WarId = war.Id, ProposerId = winner.Id };
                PeaceShape.Write(campaign, winner, war, new PeaceLedger(campaign, war, winner.Id), demand, 500f);
                Check(demand.Provinces.Contains(occupied), "the other government asks for the province it holds");
                var siblings = Enumerable.Range(1, map.ProvinceCount).Where(province =>
                    province != occupied && map.ProvinceToState[province] == region).ToArray();
                Check(siblings.Length > 0 && siblings.Any(province => !demand.Provinces.Contains(province)),
                    "the other government does not take the rest of the region");
            }
            finally
            {
                campaign.SetProvinceController(occupied, previous);
            }
        }
        finally
        {
            campaign.Defines.PeaceWholeRegions = wholeRegions;
            campaign.Defines.PeaceCostPerProvince = provinceCost;
        }
    }

    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }
}
