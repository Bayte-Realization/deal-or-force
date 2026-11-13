using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// War-score tables are kept per campaign. A second world in the same process must not be
/// handed the first world's province index or score dictionaries, and scoring the second
/// must not clear the lists the first still holds.
/// </summary>
static class WarScoreIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("war score isolation: " + note); Console.WriteLine("PASS war score isolation: " + note); }

    public static void Run()
    {
        var first = World(61109, 1, 2, 10, 20,
            new ushort[] { 0, 10, 10, 20, 20, 10, 20 },
            new ushort[] { 0, 1, 1, 2, 2, 9, 9 },
            occupiedProvince: 3, claimedState: 20);
        var second = World(61110, 3, 4, 30, 40,
            new ushort[] { 0, 30, 30, 40 },
            new ushort[] { 0, 3, 3, 4 },
            occupiedProvince: 0, claimedState: -1);
        var untouched = new Campaign(61111);
        var untouchedPrint = CampaignFingerprint.Of(untouched);
        var firstBefore = CampaignFingerprint.Of(first);
        var firstWar = first.Wars[0];
        var secondWar = second.Wars[0];
        var built = WarScoring.StateIndexBuilds;

        WarScoring.Update(first, firstWar);
        Check(Near(firstWar.OccupationScore, 25f) && Near(firstWar.CapitalScore, 4f) && Near(firstWar.ObjectiveScore, 0f)
              && Near(firstWar.WarScore, 29f) && firstWar.Collapsed == 0
              && WarScoring.IndexedProvinces(first).SequenceEqual(new[] { 1, 2, 5, 3, 4, 6 })
              && WarScoring.IndexedOwners(first)[1].SequenceEqual(new[] { 1, 2 })
              && WarScoring.StateIndexBuilds == built + 1,
            "one occupied third of the capital stays 25, 4 and 0, on the first campaign's own index");
        Check(WarScoring.IndexedProvinces(second).Length == 0 && WarScoring.IndexedOwners(second) is null &&
              WarScoring.ScoreSides(second) is null && WarScoring.ScoreWorth(second) is null &&
              WarScoring.ScoreOccupied(second) is null && WarScoring.ScoreValuePerProvince(second) is null &&
              WarScoring.ScoreProvinceCounts(second) is null &&
              CampaignFingerprint.Of(untouched) == untouchedPrint && CampaignFingerprint.Of(first) != firstBefore,
            "a second campaign does not observe the first campaign's lists, and an untouched world is unchanged");

        var provinces = WarScoring.IndexedProvinces(first);
        var starts = WarScoring.IndexedStateStarts(first);
        var counts = WarScoring.IndexedStateCounts(first);
        var owners = WarScoring.IndexedOwners(first);
        var sides = WarScoring.ScoreSides(first);
        var worth = WarScoring.ScoreWorth(first);
        var occupied = WarScoring.ScoreOccupied(first);
        var values = WarScoring.ScoreValuePerProvince(first);
        var provinceCounts = WarScoring.ScoreProvinceCounts(first);
        var firstPrint = CampaignFingerprint.Of(first);

        WarScoring.Update(second, secondWar);
        Check(WarScoring.StateIndexBuilds == built + 2 &&
              WarScoring.IndexedProvinces(second).SequenceEqual(new[] { 1, 2, 3 }) &&
              WarScoring.IndexedStateCounts(second)[30] == 2 &&
              WarScoring.ScoreSides(second).ContainsKey(3) && WarScoring.ScoreSides(second).ContainsKey(4) &&
              !WarScoring.ScoreSides(second).ContainsKey(1) &&
              WarScoring.ScoreValuePerProvince(second).ContainsKey(30) &&
              !WarScoring.ScoreValuePerProvince(second).ContainsKey(10),
            "the second campaign records only its own provinces, region counts and sides");
        Check(ReferenceEquals(provinces, WarScoring.IndexedProvinces(first)) &&
              provinces.SequenceEqual(new[] { 1, 2, 5, 3, 4, 6 }) &&
              counts[10] == 3 && owners[1].SequenceEqual(new[] { 1, 2 }) &&
              sides.ContainsKey(1) && sides.ContainsKey(2) && !sides.ContainsKey(3) &&
              worth.ContainsKey(1) && occupied.ContainsKey(2) &&
              values.ContainsKey(10) && provinceCounts.ContainsKey(20),
            "scoring the second campaign leaves the first campaign's lists in place");
        Check(!ReferenceEquals(provinces, WarScoring.IndexedProvinces(second)) &&
              !ReferenceEquals(starts, WarScoring.IndexedStateStarts(second)) &&
              !ReferenceEquals(counts, WarScoring.IndexedStateCounts(second)) &&
              !ReferenceEquals(owners, WarScoring.IndexedOwners(second)) &&
              !ReferenceEquals(sides, WarScoring.ScoreSides(second)) &&
              !ReferenceEquals(worth, WarScoring.ScoreWorth(second)) &&
              !ReferenceEquals(occupied, WarScoring.ScoreOccupied(second)) &&
              !ReferenceEquals(values, WarScoring.ScoreValuePerProvince(second)) &&
              !ReferenceEquals(provinceCounts, WarScoring.ScoreProvinceCounts(second)),
            "the index and the score dictionaries are separate objects");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(untouched) == untouchedPrint,
            "the second campaign's score does not move the first fingerprint or an untouched world");

        WarScoring.Update(first, firstWar);
        Check(WarScoring.StateIndexBuilds == built + 2 && Near(firstWar.WarScore, 29f) &&
              ReferenceEquals(provinces, WarScoring.IndexedProvinces(first)) &&
              ReferenceEquals(starts, WarScoring.IndexedStateStarts(first)) &&
              ReferenceEquals(counts, WarScoring.IndexedStateCounts(first)) &&
              ReferenceEquals(owners, WarScoring.IndexedOwners(first)) &&
              ReferenceEquals(sides, WarScoring.ScoreSides(first)) &&
              ReferenceEquals(worth, WarScoring.ScoreWorth(first)) &&
              ReferenceEquals(occupied, WarScoring.ScoreOccupied(first)) &&
              ReferenceEquals(values, WarScoring.ScoreValuePerProvince(first)) &&
              ReferenceEquals(provinceCounts, WarScoring.ScoreProvinceCounts(first)),
            "the same campaign reuses its index and dictionaries");

        Check(first.SetProvinceController(4, 1), "another occupation bumps the map revision");
        WarScoring.Update(first, firstWar);
        Check(WarScoring.StateIndexBuilds == built + 2 && firstWar.Collapsed == 1 &&
              firstWar.WarScore == first.Defines.WarScoreMax &&
              ReferenceEquals(provinces, WarScoring.IndexedProvinces(first)) &&
              ReferenceEquals(sides, WarScoring.ScoreSides(first)),
            "a map revision from occupation reuses the ownership index and still collapses the score");

        var secondProvinces = WarScoring.IndexedProvinces(second);
        var secondSides = WarScoring.ScoreSides(second);
        first.InvalidateStateIndex();
        WarScoring.Update(first, firstWar);
        Check(WarScoring.StateIndexBuilds == built + 3 &&
              !ReferenceEquals(provinces, WarScoring.IndexedProvinces(first)) &&
              WarScoring.IndexedProvinces(first).SequenceEqual(new[] { 1, 2, 5, 3, 4, 6 }) &&
              ReferenceEquals(sides, WarScoring.ScoreSides(first)) && sides.ContainsKey(1) && sides.ContainsKey(2) &&
              ReferenceEquals(secondProvinces, WarScoring.IndexedProvinces(second)) &&
              secondProvinces.SequenceEqual(new[] { 1, 2, 3 }) &&
              ReferenceEquals(secondSides, WarScoring.ScoreSides(second)) &&
              secondSides.ContainsKey(3) && secondSides.ContainsKey(4) &&
              CampaignFingerprint.Of(untouched) == untouchedPrint,
            "rebuilding the first campaign's index does not empty the second campaign's lists");
    }

    static bool Near(float value, float expected) => MathF.Abs(value - expected) < 0.05f;

    static Campaign World(int seed, ushort attackerMap, ushort defenderMap, int attackerCapital, int defenderCapital,
        ushort[] state, ushort[] owner, int occupiedProvince, int claimedState)
    {
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation
        {
            Id = 0, MapIndex = attackerMap, Tag = "ATK", Name = "Attacker", Gdp = 10f,
            CapitalStateMapIndex = attackerCapital, IsHuman = true
        });
        campaign.Nations.Add(new Nation
        {
            Id = 1, MapIndex = defenderMap, Tag = "DEF", Name = "Defender", Gdp = 10f,
            CapitalStateMapIndex = defenderCapital
        });
        campaign.States.Add(new ScenarioState
        {
            MapIndex = attackerCapital, NationMapIndex = attackerMap, Population = 1000, Name = "Attacker home"
        });
        campaign.States.Add(new ScenarioState
        {
            MapIndex = defenderCapital, NationMapIndex = defenderMap, Population = 1000, Name = "Defender home"
        });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, Map(state, owner));
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, (ushort[])owner.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(campaign, new float[state.Length]);
        if (occupiedProvince > 0 && !campaign.SetProvinceController(occupiedProvince, attackerMap))
            throw new Exception("war score isolation: the fixture occupation did not apply");
        campaign.Wars.Add(new StrategicWar
        {
            Id = 1, AttackerId = 0, DefenderId = 1, ClaimedStateMapIndex = claimedState,
            AttackerWarSupport = 1f, DefenderWarSupport = 1f
        });
        return campaign;
    }

    static ProvinceTopology Map(ushort[] state, ushort[] owner)
    {
        var neighbors = new int[state.Length][];
        for (var i = 0; i < neighbors.Length; i++) neighbors[i] = Array.Empty<int>();
        var constructor = typeof(ProvinceTopology).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single();
        return (ProvinceTopology)constructor.Invoke(new object[]
        {
            new ushort[1], state, owner, new int[state.Length], neighbors, 1, 1
        });
    }
}
