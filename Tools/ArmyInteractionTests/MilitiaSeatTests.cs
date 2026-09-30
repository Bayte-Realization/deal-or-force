using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Militia forms up where its people live (item 94): in the largest town of its region the
/// country still holds, in another when that one falls, and nowhere once the whole region has.
/// It carries the town's name, and the country's chronicle records it.
/// </summary>
static class MilitiaSeatTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL militia: " + name);
        Console.WriteLine("PASS militia: " + name);
    }

    public static void Run(Campaign campaign)
    {
        CityCatalog.Reset();
        var ukraine = campaign.NationByTag("UKR");
        var russia = campaign.NationByTag("RUS");
        var kharkiv = campaign.States.First(state => state.Id == "ua_63");
        var topology = campaign.ProvinceMap;
        var flag = (ushort)ukraine.MapIndex;
        var region = (ushort)kharkiv.MapIndex;

        // A copy of the political map with the whole region in Ukrainian hands, so the test does
        // not depend on where the front stands on the first day.
        var control = (ushort[])campaign.ProvinceController.Clone();
        var provinces = Enumerable.Range(1, topology.ProvinceCount)
            .Where(province => topology.ProvinceToState[province] == region && !topology.IsWater(province)).ToList();
        foreach (var province in provinces) control[province] = flag;

        var (seat, town) = MilitiaCore.Seat(topology, control, flag, region);
        Check(town?.Name == "Kharkiv", $"the Kharkiv region's militia forms up in Kharkiv ({town?.Name ?? "no town"})");
        Check(seat > 0 && topology.ProvinceToState[seat] == region, "in a province of its own region");

        control[seat] = (ushort)russia.MapIndex;
        var (fallback, next) = MilitiaCore.Seat(topology, control, flag, region);
        Check(fallback > 0 && fallback != seat && control[fallback] == flag,
            "with Kharkiv itself taken it forms up on ground still held");
        Check(next?.Name != "Kharkiv", $"and not in the fallen city ({next?.Name ?? "the middle of the region"})");

        foreach (var province in provinces) control[province] = (ushort)russia.MapIndex;
        var (none, _) = MilitiaCore.Seat(topology, control, flag, region);
        Check(none <= 0, "a region wholly occupied has nowhere to form up");

        // Standing one up for real: named for its town, and in the country's chronicle.
        var before = campaign.Armies.Count;
        var (liveSeat, liveTown) = MilitiaCore.Seat(topology, campaign.ProvinceController, flag, region);
        if (liveSeat > 0 && MilitiaCore.Found(campaign, ukraine, kharkiv))
        {
            var raised = campaign.Armies[^1];
            var place = liveTown?.Name ?? kharkiv.Name;
            Check(raised.TemplateId == MilitiaCore.TemplateId && raised.ProvinceId == liveSeat,
                "the formation stands where the seat is");
            Check(raised.Name.EndsWith($" {place} Militia", StringComparison.Ordinal),
                $"and is named for it ({raised.Name})");
            var entry = campaign.Chronicle.Entries.LastOrDefault();
            Check(entry is { Kind: "militia_raised", Public: false } && entry.ActorId == ukraine.Id,
                "the country's own chronicle records the town taking up arms");
            campaign.Armies.Remove(raised);
        }
        else Check(false, "Kharkiv region can stand a militia up on the first day");
        Check(campaign.Armies.Count == before, "and the test leaves the world as it found it");
    }
}
