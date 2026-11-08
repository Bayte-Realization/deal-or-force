using System;
using System.Reflection;
using DealOrForce.Simulation;

static class MemoryHotPathTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("Memory hot path: " + label); Console.WriteLine("PASS memory hot path: " + label); }

    public static void Run()
    {
        var c = new Campaign(61046, new Defines { BuildingDepreciationPerYear = float.Epsilon });
        c.Nations.Add(new Nation { Id = 0, MapIndex = 500 });
        for (var i = 0; i < 3000; i++)
        {
            var s = new ScenarioState { MapIndex = i + 1, NationMapIndex = 500 };
            s.Buildings[BuildingKind.CivilianIndustry] = 1; c.States.Add(s);
        }
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, 7L * 24);
        CapitalDepreciation.Week(c); // Warm the catalogue; tiny rate underflows to zero.
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        var allocated = GC.GetAllocatedBytesForCurrentThread();
        CapitalDepreciation.Week(c);
        var bytes = GC.GetAllocatedBytesForCurrentThread() - allocated;
        Check(bytes < 4096, "3000 regional owner lookups avoid per-region closures (" + bytes + " bytes)");
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng,
            "scratch-only lookup changes no world data or random draws");
        c.Nations.Add(new Nation { Id = 1, MapIndex = 500 });
        Check(ReferenceEquals(c.NationByMapIndex(500), c.Nations[0]) && c.NationByMapIndex(-1) is null,
            "first-match duplicate and missing owner semantics remain unchanged");
        DoctrineHotPathTests.Run();
    }
}
