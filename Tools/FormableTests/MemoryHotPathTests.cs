using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using System.Text.Json;
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
        GlobePresentationMemoryGuards();
        PopulationGroupLiteralInterning();
        CampaignHistoryTightCapacity();
        LongCampaignRelationAndImageTrim();
    }

    static string FindRepoRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate DealOrForce.csproj.");
    }

    static void GlobePresentationMemoryGuards()
    {
        var path = Path.Combine(FindRepoRoot(), "Scripts", "gfx", "GlobePresentation.cs");
        var src = File.ReadAllText(path);
        Check(src.Contains("_frontierAuthored = FrontierFieldWanted() && Platform.PlatformProfile.Current?.IsTouchPrimary != true", StringComparison.Ordinal) &&
              src.Contains("result.FrontierAuthored = null;", StringComparison.Ordinal),
            "GlobePresentation gates _frontierAuthored on FrontierFieldWanted() and non-touch and drops worker reference");
        Check(src.Contains("GC.Collect(2, GCCollectionMode.Forced, blocking: true)", StringComparison.Ordinal) &&
              !src.Contains("GC.Collect(1, GCCollectionMode.Optimized", StringComparison.Ordinal),
            "GlobePresentation mobile boot finish runs blocking Gen-2 collection instead of Gen-1 Optimized");
        Check(src.Contains("SyncCameraLockedMapOverlays", StringComparison.Ordinal) &&
              src.Contains("IsTurboSimThrottling", StringComparison.Ordinal),
            "GlobePresentation preserves SyncCameraLockedMapOverlays and IsTurboSimThrottling contracts");
    }

    static void PopulationGroupLiteralInterning()
    {
        var options = new JsonSerializerOptions
        {
            IncludeFields = true,
            Converters = { new PopulationGroupConverter() }
        };
        const string rowJson = """
            [
              [0,0,"mem_test_culture_alpha","mem_test_faith_alpha","mem_test_job_alpha",1200,0.85,0.5,0.6,0.1],
              [1,1,"mem_test_culture_alpha","mem_test_faith_alpha","mem_test_job_alpha",3400,0.90,0.4,0.7,0.2],
              [0,2,"","","",100,0.5,0.1,0.2,0.0]
            ]
            """;
        var rows = JsonSerializer.Deserialize<List<PopulationGroup>>(rowJson, options)!;
        Check(rows.Count == 3 &&
              rows[0].Culture == "mem_test_culture_alpha" &&
              ReferenceEquals(rows[0].Culture, rows[1].Culture) &&
              ReferenceEquals(rows[0].Religion, rows[1].Religion) &&
              ReferenceEquals(rows[0].Profession, rows[1].Profession) &&
              rows[2].Culture.Length == 0,
            "PopulationGroupConverter interns duplicate literal row IDs to shared string instances");

        const string objJson = """
            [
              {"class":0,"sector":0,"culture":"mem_test_obj_culture","religion":"mem_test_obj_faith","profession":"mem_test_obj_job","size":500,"employment":0.7,"wealth":0.3,"standardofliving":0.5,"radicalism":0.05},
              {"class":1,"sector":1,"culture":"mem_test_obj_culture","religion":"mem_test_obj_faith","profession":"mem_test_obj_job","size":900,"employment":0.8,"wealth":0.4,"standardofliving":0.6,"radicalism":0.02}
            ]
            """;
        var objs = JsonSerializer.Deserialize<List<PopulationGroup>>(objJson, options)!;
        Check(objs.Count == 2 &&
              ReferenceEquals(objs[0].Culture, objs[1].Culture) &&
              ReferenceEquals(objs[0].Religion, objs[1].Religion) &&
              ReferenceEquals(objs[0].Profession, objs[1].Profession),
            "PopulationGroupConverter interns duplicate legacy object literal IDs");

        var longId = new string('x', 70);
        var longJson = $$"""[[0,0,"{{longId}}","{{longId}}","{{longId}}",10,0.5,0.5,0.5,0.0]]""";
        var longRows = JsonSerializer.Deserialize<List<PopulationGroup>>(longJson, options)!;
        Check(longRows.Count == 1 && longRows[0].Culture == longId,
            "PopulationGroupConverter preserves literals exceeding 64 characters without caching error");
    }

    static void CampaignHistoryTightCapacity()
    {
        var sample = new NationHistorySample(7, 100f, 10f, 5f, 1f, 1f, 0f, 1f, .5f, .8f, .7f, 1f, 0f, 0f, 1f, 1f, .5f, 100f, 100f, 100f, 100f, 100f);
        var src = new Dictionary<int, List<NationHistorySample>> { [1] = new List<NationHistorySample> { sample } };
        var restoredHistory = new CampaignHistory();
        restoredHistory.Restore(src);
        var restored = (List<NationHistorySample>)restoredHistory.ForNation(1);
        Check(restored.Count == 1 && restored[0] == sample && restored.Capacity == 1,
            "CampaignHistory.Restore allocates exact sample list capacity without +16 slack");

        var c = new Campaign(61047);
        var landed = new Nation { Id = 0, MapIndex = 10, Tag = "LND", Population = 1_000_000, Gdp = 10f };
        var landless = new Nation
        {
            Id = 1, MapIndex = 900, Tag = "LNDLESS", Population = 0,
            Movement = new MovementState { Landless = true, Cadres = 1000f }
        };
        var state = new ScenarioState { Id = "lnd_1", MapIndex = 101, NationMapIndex = 10, Population = 1_000_000 };
        c.Nations.Add(landed);
        c.Nations.Add(landless);
        c.States.Add(state);
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 101, 101 }, new ushort[] { 10, 10 }, 2, 1, _ => false));
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, 7L * 24);
        c.History.Capture(c);

        var landedSeries = (List<NationHistorySample>)c.History.ForNation(landed.Id);
        var landlessSeries = (List<NationHistorySample>)c.History.ForNation(landless.Id);
        Check(landedSeries.Count == 1 && landedSeries.Capacity == 32,
            "CampaignHistory.CaptureStages sizes landed series initial capacity to 32");
        Check(landlessSeries.Count == 1 && landlessSeries.Capacity == 8,
            "CampaignHistory.CaptureStages sizes landless series initial capacity to 8");
    }

    static void LongCampaignRelationAndImageTrim()
    {
        var c = new Campaign(61048);
        c.Defines.RetireDeadAfterDays = 9999;
        var host = new Nation { Id = 0, MapIndex = 10, Tag = "HST", Population = 1_000_000 };
        c.Nations.Add(host);
        c.States.Add(new ScenarioState { Id = "hst_1", MapIndex = 101, NationMapIndex = 10, Population = 1_000_000 });
        for (var i = 0; i < 150; i++)
        {
            var a = 1000 + i;
            var b = 2000 + i;
            c.DormantRelations[((long)Math.Min(a, b) << 32) | (uint)Math.Max(a, b)] =
                new DiplomaticRelation { A = a, B = b, Dormant = true };
        }
        for (var i = 0; i < 20; i++)
            host.PublicImages[$"nation:dead_{i}"] = new PublicImage { Status = 0, Esteem = 0f, Incident = 0f, LastIncidentDay = -1, LastReviewDay = -1 };

        var report = LongCampaign.Run(c);
        Check(report.RelationsDropped == 150 && c.DormantRelations.Count == 0,
            "LongCampaign drops all orphaned dormant relations cleanly");
        Check(report.ImagesDropped == 20 && host.PublicImages.Count == 0,
            "LongCampaign prunes inert public images cleanly");
    }
}
