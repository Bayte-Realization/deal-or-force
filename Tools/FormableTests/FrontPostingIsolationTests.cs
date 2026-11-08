using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

/// <summary>
/// The front staff keeps its posting lists per campaign. A second world in the same
/// process must not be handed the first world's posts, and looking at the second
/// must not clear the posts, army buckets or front rows the first still holds.
/// </summary>
static class FrontPostingIsolationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("front posting isolation: " + note); Console.WriteLine("PASS front posting isolation: " + note); }

    public static void Run()
    {
        var first = World(new[] { 1, 2 }, directOrder: true);
        var second = World(new[] { 3 }, directOrder: false);
        var firstPrint = CampaignFingerprint.Of(first);
        var secondPrint = CampaignFingerprint.Of(second);
        var direct = first.Armies.Single(army => army.DirectOrder);
        var firstLine = first.FrontLines[0];
        var secondLine = second.FrontLines[0];

        FrontManager.Tick(first);
        Check(!first.Defines.FrontStaffMovesAdvances, "advances stay where they were massed");
        Check(CopyPosts(first).SequenceEqual(new[] { 1, 2 }), "the first campaign posts provinces 1 and 2");
        Check(Ids(first, 1).SequenceEqual(new[] { 1 }) && Ids(first, 2).SequenceEqual(new[] { 2 }),
            "the first campaign counts its own armies on those posts");
        Check(FrontManager.FrontRows(first).Count == 1 && ReferenceEquals(FrontManager.FrontRows(first)[0], firstLine) &&
              firstLine.Provinces.SetEquals(new[] { 1, 2 }),
            "the first campaign keeps its own front row");
        Check(direct.OrderProvinceId == 1 && direct.ProvinceId == 1 && !Ids(first, 1).Contains(direct.Id),
            "a direct order is not rewritten or counted as a spare posting");
        Check(FrontManager.PostedProvinces(second).Count == 0 && FrontManager.PostedArmies(second, 3).Count == 0 &&
              FrontManager.FrontRows(second).Count == 0,
            "a second campaign does not observe the first campaign's posts, armies or front rows");

        var posts = FrontManager.PostedProvinces(first);
        var armies = FrontManager.PostedArmies(first, 1);
        var rows = FrontManager.FrontRows(first);
        FrontManager.Tick(second);
        Check(CopyPosts(second).SequenceEqual(new[] { 3 }) && Ids(second, 3).SequenceEqual(new[] { 3 }) &&
              FrontManager.FrontRows(second).Count == 1 && ReferenceEquals(FrontManager.FrontRows(second)[0], secondLine),
            "the second campaign records only its own province, army and front row");
        Check(CopyPosts(first).SequenceEqual(new[] { 1, 2 }) && Ids(first, 1).SequenceEqual(new[] { 1 }) &&
              Ids(first, 2).SequenceEqual(new[] { 2 }) && ReferenceEquals(FrontManager.FrontRows(first)[0], firstLine),
            "looking at the second campaign leaves the first campaign's posts, armies and front row in place");
        Check(!ReferenceEquals(posts, FrontManager.PostedProvinces(second)) &&
              !ReferenceEquals(armies, FrontManager.PostedArmies(second, 3)) &&
              !ReferenceEquals(rows, FrontManager.FrontRows(second)),
            "the lists are separate objects");
        Check(direct.OrderProvinceId == 1 && CampaignFingerprint.Of(first) == firstPrint &&
              CampaignFingerprint.Of(second) == secondPrint,
            "the two looks do not change either fingerprint");
    }

    static int[] CopyPosts(Campaign campaign) => FrontManager.PostedProvinces(campaign).ToArray();

    static int[] Ids(Campaign campaign, int province) =>
        FrontManager.PostedArmies(campaign, province).Select(army => army.Id).ToArray();

    static Campaign World(int[] front, bool directOrder)
    {
        var states = new ushort[4];
        var nations = new ushort[4];
        var controllers = new ushort[4];
        for (var province = 1; province < states.Length; province++)
        {
            states[province] = 1;
            controllers[province] = 10;
        }
        var campaign = new Campaign(61109);
        campaign.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "FRT", Population = 1000, IsHuman = true });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(campaign, Map(states, nations));
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(campaign, controllers);
        var war = new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 };
        foreach (var province in front) war.AttackerFront.Add(province);
        campaign.Wars.Add(war);
        foreach (var province in front)
            campaign.Armies.Add(new Army
            {
                Id = province,
                NationId = 0,
                ProvinceId = province,
                OrderProvinceId = province,
                AssignedWarId = war.Id,
                OrderKind = ArmyOrder.DefendFront,
                Divisions = 1f,
                Personnel = 1000
            });
        if (directOrder)
            campaign.Armies.Add(new Army
            {
                Id = 90,
                NationId = 0,
                ProvinceId = front[0],
                OrderProvinceId = front[0],
                AssignedWarId = war.Id,
                OrderKind = ArmyOrder.DefendFront,
                DirectOrder = true,
                Divisions = 1f,
                Personnel = 1000
            });
        var line = new DrawnFrontLine { Id = 4, NationId = 0, Kind = FrontLineKind.Defence };
        foreach (var province in front) line.Provinces.Add(province);
        campaign.FrontLines.Add(line);
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
