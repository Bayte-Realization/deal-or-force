using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The navies a bookmark opens with, as data.
///
/// Everything else the scenario starts from is a file - the countries, the political map,
/// the standing armies - and there is no reason a navy should be the exception. A mod that
/// wants a 1914 world, or simply wants Argentina to have a fleet, adds a file to
/// history/units and never opens the executable.
///
/// A fleet's home port is a coastal province, and it is worked out from where the fleet is
/// rather than written down: the nearest coastal province the owner holds. That way a mod
/// writes a latitude and a longitude - which it can read off any map - instead of a
/// province id that changes whenever the map is recompiled.
/// </summary>
public static class FleetHistory
{
    public static void Apply(Campaign campaign)
    {
        foreach (var document in GameDataLoader.DataDocuments("history", "units", "2024"))
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                if (!document.RootElement.TryGetProperty("fleets", out var fleets) ||
                    fleets.ValueKind != JsonValueKind.Object) continue;

                foreach (var entry in fleets.EnumerateObject())
                {
                    var nation = campaign.NationByTag(entry.Name);
                    if (nation is null || entry.Value.ValueKind != JsonValueKind.Array) continue;
                    campaign.Fleets.RemoveAll(fleet => fleet.NationId == nation.Id);
                    foreach (var row in entry.Value.EnumerateArray()) Read(campaign, nation, row);
                }
            }

        AssignHomePorts(campaign);
    }

    private static void Read(Campaign campaign, Nation nation, JsonElement row)
    {
        if (row.ValueKind != JsonValueKind.Object) return;
        var name = row.TryGetProperty("name", out var named) && named.ValueKind == JsonValueKind.String
            ? named.GetString() : $"{nation.Tag} Fleet";
        if (!row.TryGetProperty("hulls", out var hullField) || !hullField.TryGetInt32(out var hulls)) return;
        if (!row.TryGetProperty("latitude", out var latField) || !latField.TryGetSingle(out var latitude)) return;
        if (!row.TryGetProperty("longitude", out var lonField) || !lonField.TryGetSingle(out var longitude)) return;

        var id = 0;
        foreach (var fleet in campaign.Fleets) id = Math.Max(id, fleet.Id + 1);
        campaign.Fleets.Add(new Fleet
        {
            Id = id,
            NationId = nation.Id,
            Name = name,
            Hulls = Math.Max(0, hulls),
            Latitude = M.Clamp(latitude, -85f, 85f),
            Longitude = longitude,
            HomeLatitude = M.Clamp(latitude, -85f, 85f),
            HomeLongitude = longitude,
            Mission = FleetMission.Anchored
        });
    }

    /// <summary>
    /// Find each fleet the province it belongs to. A fleet with no home has nowhere to be
    /// repaired and nowhere to be driven back to, so it is worth getting right even though
    /// nothing in the data says it.
    /// </summary>
    public static void AssignHomePorts(Campaign campaign)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null) continue;
            var best = -1;
            var bestDistance = float.MaxValue;
            for (var province = 1; province <= topology.ProvinceCount; province++)
            {
                if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
                var centre = topology.CenterOf(province);
                var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                    centre.Latitude, centre.Longitude);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = province;
            }
            fleet.HomeProvinceId = best;
        }
    }

    /// <summary>
    /// Write the navies back out, so what a modder edits is what the game runs on. The map
    /// compiler ships the base game's file with this, exactly as it does the political map
    /// and the country definitions.
    /// </summary>
    public static string Export(Campaign campaign, out int written)
    {
        written = 0;
        var byNation = new Dictionary<string, List<Fleet>>(StringComparer.Ordinal);
        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null || string.IsNullOrWhiteSpace(nation.Tag)) continue;
            if (!byNation.TryGetValue(nation.Tag, out var list)) byNation[nation.Tag] = list = new List<Fleet>();
            list.Add(fleet);
        }

        var tags = new List<string>(byNation.Keys);
        tags.Sort(StringComparer.Ordinal);

        var builder = new StringBuilder();
        builder.AppendLine("// Deal or Force - the navies of 1 January 2024.");
        builder.AppendLine("//");
        builder.AppendLine("// A fleet is hulls at a place. Its home port is worked out from that place - the");
        builder.AppendLine("// nearest coastal province its owner holds - so a mod writes coordinates it can read");
        builder.AppendLine("// off any map instead of a province id that moves when the map is recompiled.");
        builder.AppendLine("//");
        builder.AppendLine("// Listing a country here replaces whatever fleets it had, so a mod that gives one");
        builder.AppendLine("// country a navy leaves every other country's alone.");
        builder.AppendLine("//");
        builder.AppendLine("// Generated by Tools/MapDataCompiler; edit it by hand if you like.");
        builder.AppendLine("{");
        builder.AppendLine("  \"fleets\": {");
        for (var index = 0; index < tags.Count; index++)
        {
            var tag = tags[index];
            builder.Append("    \"").Append(tag).AppendLine("\": [");
            var list = byNation[tag];
            for (var entry = 0; entry < list.Count; entry++)
            {
                var fleet = list[entry];
                written++;
                builder.Append("      { \"name\": ").Append(JsonSerializer.Serialize(fleet.Name))
                    .Append(", \"hulls\": ").Append(fleet.Hulls)
                    .Append(", \"latitude\": ").Append(fleet.Latitude.ToString("0.##",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(", \"longitude\": ").Append(fleet.Longitude.ToString("0.##",
                        System.Globalization.CultureInfo.InvariantCulture))
                    .Append(" }");
                if (entry < list.Count - 1) builder.Append(',');
                builder.AppendLine();
            }
            builder.Append("    ]");
            if (index < tags.Count - 1) builder.Append(',');
            builder.AppendLine();
        }
        builder.AppendLine("  }");
        builder.AppendLine("}");
        return builder.ToString();
    }
}
