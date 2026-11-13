using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class StatecraftSnapshotTests
{
    private static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("statecraft snapshot: " + note);
        Console.WriteLine("PASS statecraft snapshot: " + note);
    }

    public static void Run()
    {
        var host = World();
        var client = World();
        // A running host has yesterday's tables; an adopted snapshot has none.
        AiStatecraft.RegionsHeldBy(host, 10);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(host, 24L);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(client, 24L);
        var beforeHost = CampaignFingerprint.Of(host);
        var beforeClient = CampaignFingerprint.Of(client);
        Check(AiStatecraft.Threatened(host, host.Nations[0]) &&
              AiStatecraft.Threatened(client, client.Nations[0]),
            "warm host and cold client both recognise their rival next door");
        Check(beforeHost == CampaignFingerprint.Of(host) && beforeClient == CampaignFingerprint.Of(client),
            "looking up a neighbour changes no authoritative state or random draw");

        // Recruitment/economy can change strength after attrition checks neighbours,
        // before statecraft builds today's assessment. A lookup must not pin it early.
        host.Nations[0].Gdp = client.Nations[0].Gdp = 900;
        AiStatecraft.RegionsHeldBy(host, 10);
        AiStatecraft.RegionsHeldBy(client, 10);
        Check(AiStatecraft.CachedStrength(host, host.Nations[0]) ==
              AiStatecraft.CachedStrength(client, client.Nations[0]),
            "an early cold-client lookup does not freeze a different daily strength");
        Check(AiStatecraft.CachedStrength(client, client.Nations[0]) ==
              AiStatecraft.StrengthOf(client, client.Nations[0]),
            "today's first explicit assessment sees the updated economy");

        // A country founded since the last daily assessment is a current neighbour,
        // not an unknown just because the economic table was already populated.
        var third = new Nation { Id = 2, MapIndex = 12, Tag = "CCC", Population = 1000 };
        host.Nations.Add(third);
        host.ProvinceMap.ProvinceToNation[2] = 12;
        host.MarkMapChanged();
        CampaignDiplomacy.Get(host, 0, 2).Stance = DiplomaticStance.Rival;
        Check(AiStatecraft.Threatened(host, host.Nations[0]),
            "a newly founded neighbouring country is visible without refreshing daily economics");
        Check(AiStatecraft.CachedStrength(client, client.Nations[0]) ==
              AiStatecraft.StrengthOf(client, client.Nations[0]),
            "another campaign's lookup leaves the client's assessment alone");
    }

    private static Campaign World()
    {
        var c = new Campaign(61109);
        c.Nations.Add(new Nation { Id = 0, MapIndex = 10, Tag = "AAA", Population = 1000, Gdp = 90 });
        c.Nations.Add(new Nation { Id = 1, MapIndex = 11, Tag = "BBB", Population = 1000, Gdp = 180 });
        c.States.Add(new ScenarioState { MapIndex = 1, NationMapIndex = 10, Id = "a" });
        c.States.Add(new ScenarioState { MapIndex = 2, NationMapIndex = 11, Id = "b" });
        var owners = new ushort[] { 0, 10, 11 };
        var map = (ProvinceTopology)typeof(ProvinceTopology)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke(new object[] { new ushort[1], new ushort[] { 0, 1, 2 },
                (ushort[])owners.Clone(), new int[3],
                new[] { Array.Empty<int>(), new[] { 2 }, new[] { 1 } }, 1, 1 });
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceMap))!.SetValue(c, map);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceController))!.SetValue(c, owners);
        typeof(Campaign).GetProperty(nameof(Campaign.ProvinceOccupation))!.SetValue(c, new float[3]);
        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.Rival;
        return c;
    }
}
