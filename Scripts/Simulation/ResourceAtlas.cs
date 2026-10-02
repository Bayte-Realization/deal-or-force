using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

public sealed record ResourceRegion(int MapIndex, string Id, string Name, int OwnerId,
    string OwnerName, float Potential, float Intensity);
public sealed record ResourceSurvey(Commodity Good, float Total, float Maximum, ResourceRegion[] Regions);

/// <summary>Public geological potential, never current output or secret factory counts.
/// Detached read-only snapshots; map and list use the same global logarithmic scale.</summary>
public static class ResourceAtlas
{
    public static float Potential(ScenarioState state, Commodity good)
    {
        var value = state?.ResourceDeposits.GetValueOrDefault(good) ?? 0;
        return float.IsFinite(value) && value > 0 ? value : 0;
    }

    public static float Intensity(float potential, float maximum) =>
        float.IsFinite(potential) && float.IsFinite(maximum) && potential > 0 && maximum > 0
            ? (float)Math.Clamp(Math.Log(1d + potential) / Math.Log(1d + maximum), 0, 1) : 0;

    public static ResourceSurvey Read(Campaign campaign, Commodity good, float minimum = .05f)
    {
        if (campaign is null) return new(good, 0, 0, Array.Empty<ResourceRegion>());
        if (!float.IsFinite(minimum) || minimum < 0) minimum = .05f;
        var owners = campaign.Nations.ToDictionary(n => n.MapIndex);
        var states = campaign.States.Where(s => Potential(s, good) > minimum).ToArray();
        var maximum = states.Select(s => Potential(s, good)).DefaultIfEmpty(0).Max();
        var total = states.Sum(s => (double)Potential(s, good));
        var rows = states.Select(s =>
        {
            owners.TryGetValue(s.NationMapIndex, out var owner);
            var amount = Potential(s, good);
            return new ResourceRegion(s.MapIndex, s.Id, s.DisplayName, owner?.Id ?? 0,
                owner?.DisplayName ?? "Unclaimed", amount, Intensity(amount, maximum));
        }).OrderByDescending(r => r.Potential).ThenBy(r => r.MapIndex).ToArray();
        return new(good, (float)Math.Min(float.MaxValue, total), maximum, rows);
    }

    public static ResourceRegion[] Filter(ResourceSurvey survey, string search, int? ownerId = null)
    {
        search = search?.Trim() ?? "";
        return survey.Regions.Where(r => (!ownerId.HasValue || r.OwnerId == ownerId.Value) &&
            (search.Length == 0 || r.Name.Contains(search, StringComparison.OrdinalIgnoreCase) ||
             r.OwnerName.Contains(search, StringComparison.OrdinalIgnoreCase) || r.Id.Contains(search, StringComparison.OrdinalIgnoreCase))).ToArray();
    }

    /// <summary>Find real land in the current region, including splits and edited borders.</summary>
    public static int FocusProvince(Campaign campaign, int region)
    {
        var map = campaign?.ProvinceMap;
        if (map is null) return 0;
        var provinces = new List<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (map.ProvinceToState[p] == region && !map.WaterByProvince[p]) provinces.Add(p);
        return provinces.Count > 0 ? provinces[provinces.Count / 2] : 0;
    }
}
