using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The Gaza Strip (item 77): its own region, Palestine's, with its own people - and held by
/// Israel on the first day, written in its region history rather than against province ids
/// that move whenever the map is recut.
/// </summary>
static class HeldGroundTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL held ground: " + name);
        Console.WriteLine("PASS held ground: " + name);
    }

    public static void Run(Campaign campaign)
    {
        var israel = campaign.NationByTag("ISR");
        var palestine = campaign.NationByTag("PSE");
        var gaza = campaign.States.FirstOrDefault(state => state.Id == "ps_gza");
        var westBank = campaign.States.FirstOrDefault(state => state.Id == "ps_wbk");
        Check(gaza is not null, "the Gaza Strip is a region of its own");
        Check(gaza.NationMapIndex == palestine.MapIndex, "and it is Palestine's");
        Check(gaza.StartController == "ISR", "its history says Israel holds it");
        Check(Math.Abs(gaza.Population - 2_100_000) < 50_000, $"about two million people live there ({gaza.Population:N0})");
        Check(westBank is not null && westBank.Population < 3_000_000,
            $"and they are no longer counted in the West Bank ({westBank?.Population:N0})");

        var topology = campaign.ProvinceMap;
        var provinces = Enumerable.Range(1, topology.ProvinceCount)
            .Where(province => topology.ProvinceToState[province] == (ushort)gaza.MapIndex).ToList();
        Check(provinces.Count > 0, $"it has ground on the map ({provinces.Count} province(s))");
        Check(provinces.All(province => topology.ProvinceToNation[province] == (ushort)palestine.MapIndex),
            "every province of it is owned by Palestine");
        Check(provinces.All(province => campaign.ProvinceController[province] == (ushort)israel.MapIndex),
            "and held by Israel");
        var westBankHeld = Enumerable.Range(1, topology.ProvinceCount)
            .Where(province => topology.ProvinceToState[province] == (ushort)westBank.MapIndex)
            .Count(province => campaign.ProvinceController[province] == (ushort)israel.MapIndex);
        Check(westBankHeld == 0, "while the West Bank is not written as occupied");
    }
}
