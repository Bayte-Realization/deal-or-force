using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A probe, not an assertion: watch one subject for a thousand days and say out loud the day
/// it stops being the size it was founded at.
///
/// This exists because a player in Egypt looked at the map and saw China's south-west painted
/// as Macao. `MayGrant` refuses a micro-city enclave and `dependent_territory` carries
/// `receives_land_grants: false`, so the grant route is closed on paper - which means whatever
/// moved Sichuan did not go through it. This prints the route.
/// </summary>
static class MacaoProbe
{
    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError))
        {
            Console.WriteLine("MACAO the world map does not load: " + mapError);
            return;
        }
        campaign.AttachProvinceTopology(map);

        var mac = campaign.NationByTag("MAC");
        var chn = campaign.NationByTag("CHN");
        if (mac is null || chn is null) { Console.WriteLine("MACAO MAC or CHN is missing from the world"); return; }

        Console.WriteLine($"MACAO day 0: regions={campaign.StatesOf(mac).Length} " +
            $"people={campaign.StatesOf(mac).Sum(s => s.Population)} type={mac.SubjectType} " +
            $"overlord={(mac.OverlordId == chn.Id ? "CHN" : mac.OverlordId.ToString())}");
        Console.WriteLine($"MACAO day 0: CHN regions={campaign.StatesOf(chn).Length} " +
            $"people={campaign.StatesOf(chn).Sum(s => s.Population)}");

        var seen = new HashSet<string>();
        for (var day = 0; day < 1500; day++)
        {
            campaign.TickDay();
            if (day % 15 != 14) continue;
            var regions = campaign.StatesOf(mac);
            var people = regions.Sum(s => s.Population);
            var line = $"day={campaign.ElapsedDays} regions={regions.Length} people={people} " +
                       $"type={mac.SubjectType} lib={mac.LibertyDesire:0.00} " +
                       $"states=[{string.Join(",", regions.OrderBy(r => r.MapIndex).Select(r => r.Id))}]";
            if (!seen.Add(line)) continue;
            Console.WriteLine("MACAO " + line);
            if (regions.Length > 1) break;
        }

        Console.WriteLine($"MACAO end: regions={campaign.StatesOf(mac).Length} " +
            $"people={campaign.StatesOf(mac).Sum(s => s.Population)} type={mac.SubjectType}");
        foreach (var state in campaign.StatesOf(mac).OrderBy(s => s.MapIndex))
            Console.WriteLine($"MACAO holds {state.Id} ({state.Name}) people={state.Population}");
    }
}
