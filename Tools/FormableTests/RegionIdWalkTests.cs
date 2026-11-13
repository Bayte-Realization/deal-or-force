using System;
using DealOrForce.Simulation;

/// <summary>
/// The region-id walk is the list. An id the list does not have is walked once and then
/// remembered. The same id does not walk the list again. A rename does, and the region is found.
/// A second campaign does not read the first campaign's memory.
/// </summary>
static class RegionIdWalkTests
{
    static int _passed;

    static void Check(bool ok, string note)
    {
        if (!ok) throw new Exception("region id walk: " + note);
        _passed++;
        Console.WriteLine("PASS region id walk: " + note);
    }

    public static void Run()
    {
        var first = World();
        var second = World();
        Check(first.NationById(0) == first.Nations[0] && first.NationById(1) == first.Nations[1],
            "nation id is the list index");

        var fingerprint = CampaignFingerprint.Of(first);
        var walked = Movements.RegionIdWalks;
        var home = Movements.StateById(first, "home");
        Check(home == first.States[0] && Movements.RegionIdWalks == walked + 1,
            "the first lookup walks the list once");
        Check(Movements.StateById(first, "HOME") == first.States[0] && Movements.RegionIdWalks == walked + 1,
            "the same id does not walk the list again");

        Check(Movements.StateById(first, "missing") == null && Movements.RegionIdWalks == walked + 2,
            "an unknown id walks the list once");
        Check(Movements.StateById(first, "missing") == null && Movements.RegionIdWalks == walked + 2,
            "a remembered miss does not walk the list again");
        Check(CampaignFingerprint.Of(first) == fingerprint,
            "reading the index does not change the fingerprint");

        first.States[0].Id = "missing";
        Movements.NoteRegionRenamed(first);
        Check(Movements.StateById(first, "missing") == first.States[0] && Movements.RegionIdWalks == walked + 3,
            "a renamed region is walked again and found");
        Check(Movements.StateById(first, "missing") == first.States[0] && Movements.RegionIdWalks == walked + 3,
            "the renamed id is not walked twice");

        second.States[0].Id = "missing";
        var separate = Movements.RegionIdWalks;
        Check(Movements.StateById(second, "missing") == second.States[0] && Movements.RegionIdWalks == separate + 1,
            "a second campaign does not use the first campaign's memory");

        Console.WriteLine($"PASS region id walk: {_passed} checks");
    }

    static Campaign World()
    {
        var campaign = new Campaign(12013);
        campaign.AddNation(new Nation { Tag = "HST", Name = "Host", MapIndex = 10, Population = 1_000 });
        campaign.AddNation(new Nation { Tag = "OTH", Name = "Other", MapIndex = 11, Population = 1_000 });
        campaign.States.Add(new ScenarioState { Id = "home", Name = "Home", MapIndex = 100, NationMapIndex = 10, Population = 1_000 });
        campaign.States.Add(new ScenarioState { Id = "other", Name = "Other", MapIndex = 101, NationMapIndex = 11, Population = 500 });
        return campaign;
    }
}
