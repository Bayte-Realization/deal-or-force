using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The supply trace keeps province lists per campaign. A second world in the same
/// process must not be handed the first world's holders, and tracing the second
/// must not rewrite the lists the first still holds.
/// </summary>
static class LogisticsIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("logistics isolation: " + note); Console.WriteLine("PASS logistics isolation: " + note); }

    public static void Run()
    {
        var first = World(new ushort[] { 0, 10, 10, 11 });
        var second = World(new ushort[] { 0, 11, 12, 10 });
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);

        LogisticsSystem.Tick(first);
        var held = Copy(10, first);
        Check(held.SequenceEqual(new[] { 1, 2 }), "the first campaign records provinces 1 and 2 for flag 10");
        Check(Copy(11, first).SequenceEqual(new[] { 3 }), "the first campaign records province 3 for flag 11");
        Check(LogisticsSystem.ControlledProvinces(second, 10).Count == 0 &&
              LogisticsSystem.ControlledProvinces(second, 11).Count == 0,
            "a second campaign does not observe the first campaign's province lists");

        LogisticsSystem.Tick(second);
        var other = Copy(10, second);
        Check(other.SequenceEqual(new[] { 3 }), "the second campaign records only its own province for flag 10");
        Check(Copy(11, second).SequenceEqual(new[] { 1 }), "the second campaign records province 1 for flag 11");
        Check(Copy(12, second).SequenceEqual(new[] { 2 }), "the second campaign records province 2 for flag 12");
        Check(!held.Intersect(other).Any(), "the two campaigns do not share a province under the same flag");
        Check(Copy(10, first).SequenceEqual(held) && Copy(11, first).SequenceEqual(new[] { 3 }),
            "tracing the second campaign leaves the first campaign's province lists in place");
        Check(!ReferenceEquals(LogisticsSystem.ControlledProvinces(first, 10),
                LogisticsSystem.ControlledProvinces(second, 10)),
            "the lists are separate objects");
        Check(CampaignFingerprint.Of(first) == firstPrint && CampaignFingerprint.Of(second) == secondPrint,
            "reading and tracing these empty orders does not change either fingerprint");
    }

    static int[] Copy(int flag, Campaign campaign) =>
        LogisticsSystem.ControlledProvinces(campaign, flag).ToArray();

    static Campaign World(ushort[] owners)
    {
        var states = new ushort[owners.Length];
        for (var province = 1; province < states.Length; province++) states[province] = 1;
        var campaign = new Campaign(61108);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "LOG", Population = 1000 });
        var map = Map(states, (ushort[])owners.Clone());
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, owners);
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
