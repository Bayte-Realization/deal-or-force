using System;
using System.Collections.Generic;

namespace ByDealOrForce.Simulation;

public sealed class Fleet
{
    public int Id;
    public int NationId;
    public string Name = "";
    public int Hulls;
    public float Readiness = 0.85f;
    public float Strength = 1f;
    public float Latitude;
    public float Longitude;
}

public static class NavalOperations
{
    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays % 7 != 0) return;
        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null) continue;
            var target = M.Clamp(nation.MilitarySpending / Math.Max(0.01f, campaign.Defines.BaselineMilitarySpending), 0.35f, 1f);
            fleet.Readiness = M.Lerp(fleet.Readiness, target, 0.08f);
        }
    }
}

public static class NavalBootstrap
{
    private readonly record struct Port(string Tag, string Name, int Hulls, float Latitude, float Longitude);
    private static readonly Port[] Ports =
    {
        new("USA", "Atlantic Fleet", 243, 36.9f, -75.4f), new("CHN", "Northern Theatre Fleet", 230, 35.6f, 121.5f),
        new("RUS", "Pacific Fleet", 115, 42.7f, 133.6f), new("JPN", "Maritime Defence Fleet", 102, 34.9f, 140.1f),
        new("KOR", "Republic of Korea Fleet", 82, 34.6f, 129.7f), new("PRK", "Korean East Sea Fleet", 54, 39.0f, 129.6f),
        new("GBR", "Home Fleet", 75, 50.2f, -3.1f), new("FRA", "Atlantic Naval Force", 72, 47.8f, -5.2f),
        new("IND", "Western Naval Command", 85, 18.5f, 71.8f), new("TUR", "Turkish Naval Forces", 48, 40.9f, 28.6f),
        new("ITA", "Marina Militare", 45, 39.8f, 17.3f), new("ESP", "Spanish Fleet", 32, 36.2f, -7.3f),
        new("AUS", "Royal Australian Fleet", 42, -33.8f, 152.1f), new("BRA", "Brazilian Fleet", 38, -23.2f, -42.7f),
        new("IDN", "Eastern Fleet Command", 55, -7.0f, 113.3f)
    };

    public static void Seed(Campaign campaign)
    {
        campaign.Fleets.Clear();
        var id = 0;
        foreach (var port in Ports)
        {
            var nation = campaign.NationByTag(port.Tag);
            if (nation is null) continue;
            campaign.Fleets.Add(new Fleet
            {
                Id = id++, NationId = nation.Id, Name = port.Name, Hulls = port.Hulls,
                Latitude = port.Latitude, Longitude = port.Longitude
            });
        }
    }
}
