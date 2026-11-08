using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using DealOrForce.Simulation;

static class DoctrineHotPathTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("Doctrine hot path: " + label); Console.WriteLine("PASS doctrine hot path: " + label); }

    public static void Run()
    {
        Allocations();
        CoursesMatchSnapshotOracle();
        CatalogReloads();
    }

    static void Allocations()
    {
        var c = new Campaign(61108);
        for (var i = 0; i < 326; i++)
        {
            var n = new Nation { Id = i, MapIndex = i + 1, IsHuman = true };
            foreach (var slot in DoctrineSlots.All) n.TacticalDoctrine.Track(slot);
            c.Nations.Add(n);
        }
        for (var i = 0; i < 4; i++) TacticalDoctrines.Tick(c);
        var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        var start = GC.GetAllocatedBytesForCurrentThread();
        for (var hour = 0; hour < 24; hour++) TacticalDoctrines.Tick(c);
        var tickBytes = GC.GetAllocatedBytesForCurrentThread() - start;

        long CountBooks()
        {
            long count = 0;
            for (var i = 0; i < 2000; i++)
                foreach (var branch in TacticalDoctrineCatalog.All) count += branch.Nodes.Count;
            return count;
        }
        var expected = CountBooks();
        start = GC.GetAllocatedBytesForCurrentThread();
        var actual = CountBooks();
        var catalogBytes = GC.GetAllocatedBytesForCurrentThread() - start;
        Console.WriteLine($"DOCTRINE_MEMORY nations=326 hours=24 tick_bytes={tickBytes} enumerations=2000 catalog_bytes={catalogBytes}");
        Check(tickBytes < 4096, "idle hourly shelves do not allocate an array for every country");
        Check(catalogBytes < 256000 && actual == expected, "catalogue enumeration reuses its sorted index");
        Check(CampaignFingerprint.Of(c) == before && c.Random.State == rng,
            "idle checks and catalogue reads preserve world fingerprint and random state");
    }

    static Campaign Courses()
    {
        var c = new Campaign(61109);
        for (var i = 0; i < 3; i++) c.Nations.Add(new Nation { Id = i, MapIndex = i + 1, IsHuman = true });
        var branches = TacticalDoctrineCatalog.All.ToArray();
        // Deliberately not shelf order: legacy iteration follows dictionary insertion order.
        foreach (var slot in DoctrineSlots.All.Reverse())
        {
            var branch = branches.First(b => b.Slot == slot);
            var other = branches.First(b => b.Slot == slot && b.Id != branch.Id);
            var track = c.Nations[0].TacticalDoctrine.Track(slot);
            track.LearningBranch = branch.Id; track.LearningNode = branch.Nodes[0].Id;
            track.LearningDoneDay = 1;
            track.Companion = new DoctrineTrack { LearningBranch = other.Id,
                LearningNode = other.Nodes[0].Id, LearningDoneDay = 2 };
        }
        var legacy = c.Nations[1].TacticalDoctrine;
        legacy.Branch = branches[0].Id; legacy.LearningBranch = branches[0].Id;
        legacy.LearningNode = branches[0].Nodes[0].Id; legacy.LearningDoneDay = 1;
        c.Nations[2].TacticalDoctrine.Track("mod_legacy_shelf").LearningNode = "missing_article";
        return c;
    }

    static void CoursesMatchSnapshotOracle()
    {
        var actual = Courses(); var oracle = Courses();
        var finish = typeof(TacticalDoctrines).GetMethod("FinishCourse", BindingFlags.Static | BindingFlags.NonPublic)!;
        var hours = typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!;
        for (var hour = 1; hour <= 72; hour++)
        {
            hours.SetValue(actual, (long)hour); hours.SetValue(oracle, (long)hour);
            var day = oracle.ElapsedDays;
            if (oracle.FeatureEnabled("tactical_doctrines") && oracle.LastTacticalDoctrineDay != day)
                oracle.LastTacticalDoctrineDay = day;
            foreach (var nation in oracle.Nations)
            {
                TacticalDoctrines.EnsureShelves(nation);
                foreach (var track in nation.TacticalDoctrine.Tracks.Values.ToArray())
                {
                    finish.Invoke(null, new object[] { oracle, nation, track, day });
                    if (track.Companion is not null)
                        finish.Invoke(null, new object[] { oracle, nation, track.Companion, day });
                }
            }
            TacticalDoctrines.Tick(actual);
            Check(CampaignFingerprint.Of(actual) == CampaignFingerprint.Of(oracle) && actual.Random.State == oracle.Random.State,
                "snapshot oracle preserves courses, insertion order, chronicle and RNG at hour " + hour);
        }
        Check(actual.LastTacticalDoctrineDay == oracle.LastTacticalDoctrineDay &&
            actual.Chronicle.Entries.SequenceEqual(oracle.Chronicle.Entries),
            "daily AI gate and complete chronicle entries match the snapshot oracle");
        Check(actual.Chronicle.Entries.Count(e => e.Kind == "doctrine_article") == 9,
            "four primary, four companion and one legacy course finish exactly once");
        Check(actual.Nations[2].TacticalDoctrine.Tracks["mod_legacy_shelf"].LearningNode.Length == 0,
            "unknown mod article is still cleared without dropping the shelf");
    }

    static void CatalogReloads()
    {
        var baseline = TacticalDoctrineCatalog.All.Select(b => b.Id).ToArray();
        var pending = TacticalDoctrineCatalog.All;
        try
        {
            var revision = TacticalDoctrineCatalog.Revision;
            using var data = JsonDocument.Parse("""
                { "zz_memory_test": { "name": "Memory test", "experience": "army",
                  "nodes": [{ "id": "zz_memory_article", "days": 1, "cost": 1 }] } }
                """);
            var errors = new List<string>();
            TacticalDoctrineCatalog.Merge(data.RootElement, errors);
            var ordered = TacticalDoctrineCatalog.All.ToArray();
            Check(errors.Count == 0 && TacticalDoctrineCatalog.Revision > revision && ordered.Length == baseline.Length + 1,
                "a data-only mod invalidates the sorted catalogue immediately");
            Check(ordered.Select(b => b.Id).SequenceEqual(ordered.Select(b => b.Id).OrderBy(id => id, StringComparer.Ordinal)),
                "catalogue order stays ordinal after mod loading");
            Check(pending.Any(b => b.Id == "zz_memory_test") && TacticalDoctrineCatalog.OnShelf(DoctrineSlots.Ground).Any(b => b.Id == "zz_memory_test"),
                "deferred enumeration and shelf filtering see a newly loaded mod");
            using var replacement = JsonDocument.Parse("""
                { "zz_memory_test": { "name": "Replaced memory test", "experience": "army",
                  "nodes": [{ "id": "zz_memory_replacement", "days": 2, "cost": 3 }] } }
                """);
            TacticalDoctrineCatalog.Merge(replacement.RootElement, errors);
            Check(errors.Count == 0 && TacticalDoctrineCatalog.All.Single(b => b.Id == "zz_memory_test").Name == "Replaced memory test" &&
                TacticalDoctrineCatalog.All.Count() == ordered.Length,
                "an accepted replacement also invalidates the index without duplicating a book");
            using var invalid = JsonDocument.Parse("""{ "bad_memory_test": { "experience": "invalid" } }""");
            TacticalDoctrineCatalog.Merge(invalid.RootElement, errors);
            Check(errors.Count == 1 && TacticalDoctrineCatalog.All.Count() == ordered.Length,
                "a refused mod entry does not poison the sorted catalogue");
        }
        finally { TacticalDoctrineCatalog.ResetCatalog(); }
        Check(TacticalDoctrineCatalog.All.Select(b => b.Id).SequenceEqual(baseline) &&
            TacticalDoctrineCatalog.BranchOf("zz_memory_article") is null,
            "catalogue reset restores shipped books and drops the test overlay");
    }
}
