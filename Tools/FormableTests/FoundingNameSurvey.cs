using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// What the game actually calls the countries it founds.
///
/// A player's complaint was that a breakaway comes out "South Russia" - a bearing, not a
/// name. The composer now has foreign cores, peoples with countries of their own, historic
/// predecessors and a hundred named lands to choose from, so the question is no longer what
/// the cascade can do but what it does do. This walks every sizable country in the world,
/// releases one region from each, and prints the name it got - then says which of those are
/// still only a compass direction, because those are the ones left to fix.
/// </summary>
static class FoundingNameSurvey
{
    public static void Run()
    {
        var campaign = Campaign.CreateBootstrap(20261019);
        MapRasterCatalog.ResetCatalog();
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError))
        { Console.WriteLine("SURVEY map failed: " + mapError); return; }
        campaign.AttachProvinceTopology(map);

        var bearing = new List<string>();
        var total = 0;
        foreach (var parent in campaign.Nations
                     .Where(n => !n.IsSubject && n.Movement is null && campaign.StatesOf(n).Length >= 4)
                     .OrderByDescending(n => n.Population)
                     .Take(40))
        {
            var releasable = CountryRelease.ReleasableStates(campaign, parent)
                .Where(s => s.MapIndex != parent.CapitalStateMapIndex)
                .OrderByDescending(s => s.Population)
                .Take(2)
                .ToList();
            foreach (var state in releasable)
            {
                var born = CountryRelease.Release(campaign, parent, new CountryRelease.ReleaseOptions
                {
                    StateMapIndices = new[] { state.MapIndex }.ToList(),
                    AsSubject = false,
                    Culture = Dominant(campaign, state) ?? DemographyCatalog.PrimaryCulture(parent.Tag),
                    Government = parent.Government,
                    Ideology = parent.Ideology
                }, out _);
                if (born is null) continue;
                total++;
                var name = CountryNames.Resolve(born, false);
                var kind = born.FoundNameKind;
                if (Environment.GetEnvironmentVariable("BDOF_SURVEY_DEBUG") == "1")
                    Console.WriteLine($"SURVEY DBG {parent.Tag} people={DemographyCatalog.PrimaryCulture(parent.Tag)} " +
                        $"ground={Dominant(campaign, state)} same={string.Equals(Dominant(campaign, state), DemographyCatalog.PrimaryCulture(parent.Tag), StringComparison.OrdinalIgnoreCase)} " +
                        $"kind={kind} id={born.FoundNameId} parentName={parent.Name}");
                if (kind == "direction")
                {
                    bearing.Add($"{parent.Tag}/{state.Id} -> {name}   [{born.FoundNameId}]");
                    continue;
                }
                Console.WriteLine($"SURVEY {parent.Tag,-4} {state.Id,-14} {kind,-10} {name}");
            }
        }

        Console.WriteLine($"SURVEY total={total} bearings={bearing.Count}");
        foreach (var line in bearing.Take(40)) Console.WriteLine("SURVEY BEARING " + line);
    }

    private static string Dominant(Campaign campaign, ScenarioState state)
    {
        var top = EconomySystem.Cultures(state).OrderByDescending(p => p.Size).FirstOrDefault();
        return top.Culture ?? "";
    }
}
