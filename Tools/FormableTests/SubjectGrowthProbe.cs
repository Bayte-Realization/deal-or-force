using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Hunt the route, not the symptom.
///
/// A player saw China's south-west painted as Macao. Every land-grant route in the subject
/// system refuses a micro-city enclave, and `dependent_territory` carries
/// `receives_land_grants: false`, so whatever moved Sichuan went around all of them. This
/// runs many seeds and reports any subject that ends up holding a share of its overlord no
/// subject should hold, together with the chronicle entries that name the day it happened.
/// </summary>
static class SubjectGrowthProbe
{
    public static void Run()
    {
        var seeds = new[] { 20261019, 1, 7, 42, 101, 777, 2024, 31337, 8080, 555, 90210, 12345 };
        foreach (var seed in seeds)
        {
            try { RunSeed(seed); }
            catch (Exception ex) { Console.WriteLine($"GROWTH seed {seed} threw {ex.GetType().Name}: {ex.Message}"); }
        }
    }

    private static void RunSeed(int seed)
    {
        var campaign = Campaign.CreateBootstrap(seed);
        MapRasterCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError))
        { Console.WriteLine("GROWTH map failed: " + mapError); return; }
        campaign.AttachProvinceTopology(map);

        // The authored world is not the bug. Greenland is five regions and fifty-six thousand
        // people against Denmark's four and six million, and that is simply true. What is not
        // true is a subject that grows after the first morning, so every subject is measured
        // against its own day zero rather than against a fixed share.
        var baseline = new Dictionary<int, long>();
        foreach (var nation in campaign.Nations)
        {
            if (!nation.IsSubject) continue;
            baseline[nation.Id] = campaign.StatesOf(nation).Sum(s => s.Population);
        }

        for (var day = 1; day < 1100; day++)
        {
            campaign.TickDay();
            if (day % 20 != 19) continue;
            foreach (var subject in campaign.Nations.Where(n => n.IsSubject).ToArray())
            {
                if (!baseline.TryGetValue(subject.Id, out var born) || born <= 0) continue;
                var people = campaign.StatesOf(subject).Sum(s => s.Population);
                // A tenth more people than it was founded with, and no fewer than a hundred
                // thousand of them: enough to be visible on the map and too much to be drift.
                if (people < born * 1.10d || people < 100_000) continue;
                var owner = campaign.NationById(subject.OverlordId);
                Console.WriteLine($"GROWTH seed={seed} day={campaign.ElapsedDays} " +
                    $"{subject.Tag} grew from {born} to {people} people " +
                    $"({people / (double)born:0.0}x) under {owner?.Tag} type={subject.SubjectType} " +
                    $"regions={campaign.StatesOf(subject).Length}");
                ReportRoute(campaign, subject, owner);
                return;
            }
        }
        Console.WriteLine($"GROWTH seed={seed} clean");
    }

    /// <summary>What the chronicle says happened to this subject, so the route is named rather than guessed at.</summary>
    private static void ReportRoute(Campaign campaign, Nation subject, Nation owner)
    {
        var entries = campaign.Chronicle.Entries
            .Where(e => e.ActorId == subject.Id || e.ActorId == owner?.Id ||
                        e.TargetId == subject.Id || e.TargetId == owner?.Id)
            .OrderBy(e => e.Day).TakeLast(16);
        foreach (var entry in entries)
            Console.WriteLine($"GROWTH   chronicle day={entry.Day} kind={entry.Kind} " +
                $"actor={campaign.NationById(entry.ActorId)?.Tag} target={campaign.NationById(entry.TargetId)?.Tag} " +
                $"subject={entry.Subject}");
    }
}
