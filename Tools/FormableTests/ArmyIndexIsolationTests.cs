using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The army index is kept per campaign. A second world in the same process must not be
/// handed the first world's war buckets, province slots, or id table, and rebuilding the
/// second must not clear the containers the first still holds.
/// </summary>
static class ArmyIndexIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("army index isolation: " + note); Console.WriteLine("PASS army index isolation: " + note); }

    public static void Run()
    {
        var first = World(61109, provinceA: 1, provinceB: 2, attacker: 1, defender: 2, direct: 9);
        var second = World(61110, provinceA: 3, provinceB: 4, attacker: 3, defender: 4, direct: 8);
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var direct = first.Armies.Single(army => army.DirectOrder);

        Warfare.RebuildArmyIndex(first);
        Check(Ids(Warfare.IndexedWarArmies(first, 1)).SequenceEqual(new[] { 1, 2 }) &&
              Ids(Warfare.IndexedProvinceArmies(first, 1)).SequenceEqual(new[] { 1, 9 }) &&
              Ids(Warfare.IndexedProvinceArmies(first, 2)).SequenceEqual(new[] { 2 }),
            "the first campaign places its own armies in its war bucket and province slots");
        Check(Warfare.IndexedById(first).TryGetValue(1, out var one) && one.Id == 1 &&
              Warfare.IndexedById(first).ContainsKey(2) && Warfare.IndexedById(first).ContainsKey(9) &&
              !Warfare.IndexedById(first).ContainsKey(3),
            "the first campaign's id table is its own armies");
        Check(direct.DirectOrder && direct.OrderProvinceId == 1 && direct.ProvinceId == 1 &&
              direct.AssignedWarId < 0 && direct.OrderKind == ArmyOrder.AdvanceFront,
            "a direct order is not rewritten into a war");
        Check(Warfare.IndexedWars(second) is null && Warfare.IndexedWarArmies(second, 1) is null &&
              Warfare.IndexedProvinceSlots(second) is null && Warfare.IndexedProvinceArmies(second, 1) is null &&
              Warfare.IndexedProvinceArmies(second, 3) is null && Warfare.IndexedById(second) is null,
            "a second campaign does not observe the first campaign's armies, war buckets or province slots");

        var wars = Warfare.IndexedWars(first);
        var warBucket = Warfare.IndexedWarArmies(first, 1);
        var slots = Warfare.IndexedProvinceSlots(first);
        var provinceOne = Warfare.IndexedProvinceArmies(first, 1);
        var provinceTwo = Warfare.IndexedProvinceArmies(first, 2);
        var byId = Warfare.IndexedById(first);

        Warfare.RebuildArmyIndex(second);
        Check(Ids(Warfare.IndexedWarArmies(second, 1)).SequenceEqual(new[] { 3, 4 }) &&
              Ids(Warfare.IndexedProvinceArmies(second, 3)).SequenceEqual(new[] { 3, 8 }) &&
              Ids(Warfare.IndexedProvinceArmies(second, 4)).SequenceEqual(new[] { 4 }) &&
              Warfare.IndexedById(second).ContainsKey(3) && Warfare.IndexedById(second).ContainsKey(4) &&
              Warfare.IndexedById(second).ContainsKey(8) && !Warfare.IndexedById(second).ContainsKey(1),
            "the second campaign records only its own armies, war bucket and province slots");
        Check(ReferenceEquals(wars, Warfare.IndexedWars(first)) &&
              ReferenceEquals(warBucket, Warfare.IndexedWarArmies(first, 1)) &&
              ReferenceEquals(slots, Warfare.IndexedProvinceSlots(first)) &&
              ReferenceEquals(provinceOne, Warfare.IndexedProvinceArmies(first, 1)) &&
              ReferenceEquals(provinceTwo, Warfare.IndexedProvinceArmies(first, 2)) &&
              ReferenceEquals(byId, Warfare.IndexedById(first)) &&
              Ids(warBucket).SequenceEqual(new[] { 1, 2 }) &&
              Ids(provinceOne).SequenceEqual(new[] { 1, 9 }) &&
              Ids(provinceTwo).SequenceEqual(new[] { 2 }) &&
              byId[9].DirectOrder && byId[9].AssignedWarId < 0,
            "rebuilding the second campaign leaves the first campaign's index in place");
        Check(!ReferenceEquals(wars, Warfare.IndexedWars(second)) &&
              !ReferenceEquals(warBucket, Warfare.IndexedWarArmies(second, 1)) &&
              !ReferenceEquals(slots, Warfare.IndexedProvinceSlots(second)) &&
              !ReferenceEquals(provinceOne, Warfare.IndexedProvinceArmies(second, 3)) &&
              !ReferenceEquals(provinceTwo, Warfare.IndexedProvinceArmies(second, 4)) &&
              !ReferenceEquals(byId, Warfare.IndexedById(second)),
            "the war buckets, province slots and id table are separate objects");
        Warfare.RebuildArmyIndex(first);
        Check(ReferenceEquals(wars, Warfare.IndexedWars(first)) &&
              ReferenceEquals(warBucket, Warfare.IndexedWarArmies(first, 1)) &&
              ReferenceEquals(slots, Warfare.IndexedProvinceSlots(first)) &&
              ReferenceEquals(provinceOne, Warfare.IndexedProvinceArmies(first, 1)) &&
              ReferenceEquals(byId, Warfare.IndexedById(first)) &&
              Ids(warBucket).SequenceEqual(new[] { 1, 2 }) &&
              Ids(provinceOne).SequenceEqual(new[] { 1, 9 }),
            "the same campaign reuses its index");
        Check(direct.DirectOrder && direct.OrderProvinceId == 1 && direct.ProvinceId == 1 &&
              direct.AssignedWarId < 0 &&
              CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "the rebuilds do not change either fingerprint");
    }

    static int[] Ids(System.Collections.Generic.IReadOnlyList<Army> armies) =>
        armies.Select(army => army.Id).ToArray();

    static Campaign World(int seed, int provinceA, int provinceB, int attacker, int defender, int direct)
    {
        var campaign = new Campaign(seed);
        campaign.Armies.Add(Formation(attacker, provinceA, war: 1, directOrder: false));
        campaign.Armies.Add(Formation(defender, provinceB, war: 1, directOrder: false));
        campaign.Armies.Add(Formation(direct, provinceA, war: -1, directOrder: true));
        return campaign;
    }

    static Army Formation(int id, int province, int war, bool directOrder) => new()
    {
        Id = id,
        NationId = 0,
        ProvinceId = province,
        OrderProvinceId = province,
        AssignedWarId = war,
        OrderKind = ArmyOrder.AdvanceFront,
        DirectOrder = directOrder,
        Divisions = 1f,
        Personnel = 1000
    };
}
