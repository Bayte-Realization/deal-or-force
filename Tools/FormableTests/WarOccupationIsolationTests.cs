using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// War ids, the occupation census and the side maps of a war are kept per campaign.
/// A second world in the same process must not be handed the first world's rows, and
/// rebuilding the second must not clear the containers the first still holds.
/// </summary>
static class WarOccupationIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("war occupation isolation: " + note); Console.WriteLine("PASS war occupation isolation: " + note); }

    public static void Run()
    {
        var first = World(61121, homeMap: 10, occupierMap: 11, warId: 4);
        var second = World(61122, homeMap: 20, occupierMap: 21, warId: 8);
        Check(first.NationById(0) == first.Nations[0] && first.NationById(1) == first.Nations[1] &&
              second.NationById(0) == second.Nations[0],
            "nation id is the list index");
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);

        Warfare.RebuildArmyIndex(first);
        Warfare.MeasureOccupation(first);
        Warfare.IndexSideMaps(first, first.Wars[0]);
        var wars = Warfare.IndexedWarById(first);
        var homes = Warfare.IndexedHomeProvinces(first);
        var pairs = Warfare.IndexedOccupiedPairs(first);
        var attackers = Warfare.IndexedAttackerMaps(first);
        var defenders = Warfare.IndexedDefenderMaps(first);
        var belligerents = Warfare.IndexedBelligerentMaps(first);
        var occupied = ((long)10 << 32) | 11;
        var homeCount = homes is not null && homes.TryGetValue(10, out var countedHome) ? countedHome : 0;
        var occupiedCount = pairs is not null && pairs.TryGetValue(occupied, out var countedOccupied) ? countedOccupied : 0;
        Check(wars is not null && wars.Count == 1 && wars.TryGetValue(4, out var war) && ReferenceEquals(war, first.Wars[0]) &&
              !wars.ContainsKey(8),
            "the first campaign's war table is its own war");
        Check(homeCount > 0 && !homes.ContainsKey(20),
            "the first campaign counts its own home provinces");
        Check(occupiedCount > 0 && !pairs.ContainsKey(((long)20 << 32) | 21),
            "the first campaign counts its own occupied pairs");
        Check(attackers is not null && attackers.SetEquals(new ushort[] { 10 }) &&
              defenders is not null && defenders.SetEquals(new ushort[] { 11 }) &&
              belligerents is not null && belligerents.SetEquals(new ushort[] { 10, 11 }),
            "the first campaign's side maps are its own two flags");
        Check(Warfare.IndexedWarById(second) is null && Warfare.IndexedHomeProvinces(second) is null &&
              Warfare.IndexedOccupiedPairs(second) is null && Warfare.IndexedAttackerMaps(second) is null &&
              Warfare.IndexedDefenderMaps(second) is null && Warfare.IndexedBelligerentMaps(second) is null,
            "a second campaign does not observe the first campaign's wars or occupation rows");

        Warfare.RebuildArmyIndex(second);
        Warfare.MeasureOccupation(second);
        Warfare.IndexSideMaps(second, second.Wars[0]);
        Warfare.RebuildArmyIndex(second);
        Warfare.MeasureOccupation(second);
        Warfare.IndexSideMaps(second, second.Wars[0]);
        Check(ReferenceEquals(wars, Warfare.IndexedWarById(first)) &&
              ReferenceEquals(homes, Warfare.IndexedHomeProvinces(first)) &&
              ReferenceEquals(pairs, Warfare.IndexedOccupiedPairs(first)) &&
              ReferenceEquals(attackers, Warfare.IndexedAttackerMaps(first)) &&
              ReferenceEquals(defenders, Warfare.IndexedDefenderMaps(first)) &&
              ReferenceEquals(belligerents, Warfare.IndexedBelligerentMaps(first)) &&
              wars.Count == 1 && wars.ContainsKey(4) && !wars.ContainsKey(8) &&
              homes[10] == homeCount && !homes.ContainsKey(20) &&
              pairs[occupied] == occupiedCount &&
              attackers.SetEquals(new ushort[] { 10 }) && defenders.SetEquals(new ushort[] { 11 }) &&
              belligerents.SetEquals(new ushort[] { 10, 11 }),
            "rebuilding the second campaign leaves the first campaign's wars and occupation rows in place");
        Check(!ReferenceEquals(wars, Warfare.IndexedWarById(second)) &&
              !ReferenceEquals(homes, Warfare.IndexedHomeProvinces(second)) &&
              !ReferenceEquals(pairs, Warfare.IndexedOccupiedPairs(second)) &&
              !ReferenceEquals(attackers, Warfare.IndexedAttackerMaps(second)) &&
              !ReferenceEquals(defenders, Warfare.IndexedDefenderMaps(second)) &&
              !ReferenceEquals(belligerents, Warfare.IndexedBelligerentMaps(second)) &&
              Warfare.IndexedWarById(second).ContainsKey(8) && !Warfare.IndexedWarById(second).ContainsKey(4) &&
              Warfare.IndexedHomeProvinces(second).ContainsKey(20) && !Warfare.IndexedHomeProvinces(second).ContainsKey(10) &&
              Warfare.IndexedOccupiedPairs(second).ContainsKey(((long)20 << 32) | 21) &&
              Warfare.IndexedAttackerMaps(second).SetEquals(new ushort[] { 20 }) &&
              Warfare.IndexedDefenderMaps(second).SetEquals(new ushort[] { 21 }) &&
              Warfare.IndexedBelligerentMaps(second).SetEquals(new ushort[] { 20, 21 }),
            "the war table, occupation rows and side maps are separate objects");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the census does not change either fingerprint");
    }

    static Campaign World(int seed, ushort homeMap, ushort occupierMap, int warId)
    {
        const int width = 4, height = 1;
        var states = new ushort[width];
        var owners = new ushort[width];
        var rgb = new byte[width * 3];
        for (var x = 0; x < width; x++)
        {
            var province = x + 1;
            states[x] = (ushort)(1000 + province);
            owners[x] = homeMap;
            rgb[x * 3 + 2] = (byte)province;
        }
        var map = ProvinceTopology.FromColorRaster(states, owners, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, width).ToDictionary(p => p, p => p));
        var controllers = (ushort[])map.ProvinceToNation.Clone();
        controllers[1] = occupierMap;
        var campaign = new Campaign(seed);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = homeMap, Tag = "HOM", Name = "Home", Population = 2_000_000 });
        campaign.Nations.Add(new Nation { Id = 1, MapIndex = occupierMap, Tag = "OCC", Name = "Occupier", Population = 2_000_000 });
        var flags = BindingFlags.Instance | BindingFlags.Public;
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap), flags)!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController), flags)!.SetValue(campaign, controllers);
        campaign.Wars.Add(new StrategicWar { Id = warId, AttackerId = 0, DefenderId = 1 });
        return campaign;
    }
}
