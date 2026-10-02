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
    public float OpenCountryCapturePerDay { get; set; } = 0.35f;

    /// <summary>Base pace for explicit player orders into covered, unoccupied ground.</summary>
    public float OrderedCapturePerDay { get; set; } = 0.30f;

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
    private static Campaign _campaign;
    private static int _day = -1;
    private static float[] _roads = Array.Empty<float>();

    /// <summary>The multiplier roads give a march into this province.</summary>
    public static float Roads(Campaign campaign, int province)
    {
        var map = campaign?.ProvinceMap;
        if (map is null || province <= 0 || province > map.ProvinceCount) return 1f;
        if (!ReferenceEquals(campaign, _campaign) || _day != campaign.ElapsedDays) Refresh(campaign);
        var region = map.ProvinceToState[province];
        return region < _roads.Length ? _roads[region] : 1f;
    }

    /// <summary>Once a day: roads are built over months, and a march asks every hour.</summary>
    private static void Refresh(Campaign campaign)
    {
        _campaign = campaign;
        _day = campaign.ElapsedDays;
        var top = 0;
        foreach (var state in campaign.States) top = Math.Max(top, state.MapIndex);
        if (_roads.Length != top + 1) _roads = new float[top + 1];
        Array.Fill(_roads, 1f);
        var defines = campaign.Defines;
        foreach (var state in campaign.States)
        {
            if (state.MapIndex <= 0) continue;
            var level = state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
            _roads[state.MapIndex] = 1f + M.Clamp(level * defines.RoadSpeedPerInfrastructure, 0f,
                MathF.Max(0f, defines.RoadSpeedCap));
        }
    }
}
