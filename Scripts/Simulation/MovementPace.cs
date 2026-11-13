using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    /// <summary>
    /// How fast undefended ground is taken when nobody is near enough to cover it - a rear
    /// area after a breakthrough, a province the enemy never garrisoned. The same pace as a
    /// march by default: walking into an empty district is walking.
    /// </summary>
    public float OpenCountryCapturePerDay { get; set; } = 0.6f;

    /// <summary>Base pace for explicit player orders into covered, unoccupied ground.</summary>
    public float OrderedCapturePerDay { get; set; } = 0.5f;

    /// <summary>Supply floor for explicit advances; resistance still slows covered gaps.</summary>
    public float OrderedCaptureSupplyFloor { get; set; } = 0.7f;

    /// <summary>
    /// The least supply counts for when advancing into open country. The trace from the depots
    /// fails exactly there - the ground was the enemy's this morning - but an army moving into
    /// an undefended district lives off the roads and the stores it overruns, not off nothing.
    /// </summary>
    public float OpenCountrySupplyFloor { get; set; } = 0.7f;

    /// <summary>How much faster a march runs for every level of infrastructure in the region it enters.</summary>
    public float RoadSpeedPerInfrastructure { get; set; } = 0.1f;

    /// <summary>The most roads can add to a march, however much infrastructure a region has.</summary>
    public float RoadSpeedCap { get; set; } = 0.6f;
}

/// <summary>
/// What sets the pace of an army on the move, beyond the march itself (item 45).
///
/// Two things were missing. Roads: a division crossing the Ruhr and one crossing the Gobi
/// marched at the same pace, although infrastructure was already counted everywhere else -
/// supply, trade, production. And open country: ground nobody was holding or covering was
/// taken at the same crawl as a gap in a defended line, so an army that had broken through
/// spent three weeks on every empty district behind it - "terribly slow, especially on enemy
/// land". A gap in a front stays slow, because the front's own divisions cover it; what is
/// behind the front, with nobody within reach, is taken at a march.
/// </summary>
public static class MovementPace
{
    private static readonly bool TraceRoads = Environment.GetEnvironmentVariable("DOF_ROADS_TRACE") == "1";
    private static readonly HashSet<int> TraceProvinces = ParseTraceProvinces();

    private static HashSet<int> ParseTraceProvinces()
    {
        var set = new HashSet<int>();
        var raw = Environment.GetEnvironmentVariable("DOF_ROADS_PROV");
        if (string.IsNullOrEmpty(raw)) return set;
        foreach (var part in raw.Split(','))
            if (int.TryParse(part.Trim(), out var p)) set.Add(p);
        return set;
    }

    /// <summary>The multiplier roads give a march into this province.</summary>
    public static float Roads(Campaign campaign, int province)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return 1f;
        // The table lives on the campaign and turns over once per campaign day. It used to be
        // one static array stamped with the last campaign that asked: every switch rebuilt it
        // from whatever the buildings happened to be at that hour, so an interleaved second
        // world re-read mid-day construction and marched the same army at a different pace.
        if (campaign.PaceRoads is null || campaign.PaceRoadsDay != campaign.ElapsedDays)
            Refresh(campaign);
        var region = map.ProvinceToState[province];
        var roads = campaign.PaceRoads;
        var road = region < roads.Length ? roads[region] : 1f;
        if (TraceRoads && TraceProvinces.Contains(province))
            Console.WriteLine($"ROADS_CALL id={campaign.GetHashCode()} seed={campaign.Seed} day={campaign.ElapsedDays} " +
                              $"hour={campaign.ElapsedHours} prov={province} region={region} road={road}");
        return road;
    }

    /// <summary>Once a day per campaign: roads are built over months, and a march asks every hour.</summary>
    private static void Refresh(Campaign campaign)
    {
        var top = 0;
        foreach (var state in campaign.States) top = Math.Max(top, state.MapIndex);
        var roads = campaign.PaceRoads;
        if (roads is null || roads.Length != top + 1) roads = campaign.PaceRoads = new float[top + 1];
        Array.Fill(roads, 1f);
        var defines = campaign.Defines;
        foreach (var state in campaign.States)
        {
            if (state.MapIndex <= 0) continue;
            var level = state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
            roads[state.MapIndex] = 1f + M.Clamp(level * defines.RoadSpeedPerInfrastructure, 0f,
                MathF.Max(0f, defines.RoadSpeedCap));
        }
        campaign.PaceRoadsDay = campaign.ElapsedDays;
        if (TraceRoads)
            Console.WriteLine($"ROADS_REFRESH id={campaign.GetHashCode()} seed={campaign.Seed} day={campaign.ElapsedDays} " +
                              $"hour={campaign.ElapsedHours} states={campaign.States.Count} roads_len={roads.Length}");
    }
}
