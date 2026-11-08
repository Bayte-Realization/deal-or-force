using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// A country's population figure is the people it governs.
///
/// The scenario writes each country's figure and shares it over the regions the country names;
/// then the map says who actually holds what, and regions under somebody else's troops in 2024 -
/// Donetsk, Zaporizhzhia, Kherson - go to their holder with the people they were given. Ukraine
/// was written at 25 million, the figure for the ground its government holds, and opened with
/// 20: the occupied regions took a fifth of it with them. A country that loses regions this way
/// keeps its written figure over the regions it still holds, every group of people scaled alike;
/// the regions that went keep theirs and count for their holder.
///
/// Only regions moved by the map's political layer on a new world, never a save, and never a
/// country cut up afterwards by the de facto powers of 2024 (Factions) - Yemen's figure is the
/// whole country's, Houthis included.
/// </summary>
public sealed partial class Defines
{
    /// <summary>A country that loses regions to their 2024 holder keeps its written population over the rest (OpeningOccupation).</summary>
    public bool OpeningOccupationKeepsFigure { get; set; } = true;
    /// <summary>How far its remaining regions may be scaled for that; a larger gap is not occupation but data.</summary>
    public float OpeningOccupationMaximumScale { get; set; } = 1.6f;
}

internal sealed class OpeningOccupation
{
    /// <summary>The countries the last new world scaled, with the factor (for the bench and the data checks).</summary>
    public static readonly List<(string Tag, double Scale, string Lost)> LastScaled = new();

    private readonly Dictionary<int, int> _ownerOfRegion = new();
    private readonly Dictionary<int, long> _figure = new();

    public OpeningOccupation(Campaign c)
    {
        foreach (var region in c.States) _ownerOfRegion[region.MapIndex] = region.NationMapIndex;
        foreach (var nation in c.Nations)
            if (nation.Movement is null && nation.MapIndex > 0) _figure[nation.MapIndex] = nation.Population;
    }

    public void KeepControlledFigures(Campaign c)
    {
        LastScaled.Clear();
        if (!c.Defines.OpeningOccupationKeepsFigure) return;
        var lostTo = new HashSet<int>();
        var lostRegions = new Dictionary<int, List<string>>();
        foreach (var region in c.States)
        {
            var origin = region.ParentMapIndex > 0 ? region.ParentMapIndex : region.MapIndex;
            if (!_ownerOfRegion.TryGetValue(origin, out var was) || was <= 0 || was == region.NationMapIndex) continue;
            if (region.NationMapIndex <= 0 || region.Unclaimed || region.Population <= 0) continue;
            lostTo.Add(was);
            if (!lostRegions.TryGetValue(was, out var names)) lostRegions[was] = names = new List<string>();
            names.Add($"{region.Name} ({region.Population:N0}) -> {c.Nations.FirstOrDefault(n => n.MapIndex == region.NationMapIndex)?.Tag}");
        }
        foreach (var mapIndex in lostTo.OrderBy(index => index))
        {
            if (!_figure.TryGetValue(mapIndex, out var figure) || figure <= 0) continue;
            var nation = c.Nations.FirstOrDefault(n => n.MapIndex == mapIndex && n.Movement is null);
            if (nation is null) continue;
            var kept = c.States.Where(region => region.NationMapIndex == mapIndex).ToList();
            var people = kept.Sum(region => region.Population);
            if (people <= 0 || people >= figure) continue;
            var scale = (double)figure / people;
            if (scale > Math.Max(1f, c.Defines.OpeningOccupationMaximumScale)) continue;
            LastScaled.Add((nation.Tag, scale, string.Join("; ", lostRegions.GetValueOrDefault(mapIndex) ?? new List<string>())));
            foreach (var region in kept)
            {
                if (region.PopulationGroups.Count == 0)
                {
                    region.Population = (long)Math.Round(region.Population * scale);
                    continue;
                }
                foreach (var group in region.PopulationGroups) group.Size = (long)Math.Round(group.Size * scale);
                region.Population = region.PopulationGroups.Sum(group => group.Size);
            }
            nation.Population = kept.Sum(region => region.Population);
        }
    }
}
