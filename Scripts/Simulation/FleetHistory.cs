using System;
using System.Collections.Generic;
using System.Linq;
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
                ReadFleets(campaign, document.RootElement);

        AssignHomePorts(campaign);
    }

    /// <summary>
    /// Layer one package's own navies over what is already there.
    ///
    /// A country's fleets are replaced whole rather than merged, because a navy is a list
    /// and there is no sensible way to half-override a list: a mod that names Britain's
    /// fleets means those and not those plus the base game's. Every other country is left
    /// alone, so giving one nation a navy of your own costs one entry.
    ///
    /// The whole `history/units` tree is read, so a mod mirrors the base game's bookmark
    /// folder - `history/units/2024/` - and its files are found without it having to know
    /// what the folder is called.
    /// </summary>
    public static void ApplyDirectory(Campaign campaign, string directory, List<string> errors)
    {
        if (string.IsNullOrWhiteSpace(directory) || !System.IO.Directory.Exists(directory)) return;

        string[] files;
        try
        {
            files = StructuredData.Files(directory, System.IO.SearchOption.AllDirectories);
        }
        catch (Exception exception)
        {
            errors?.Add($"history/units: {exception.Message}");
            return;
        }

        var touched = false;
        foreach (var file in files)
        {
            var origin = $"history/units/{System.IO.Path.GetFileName(file)}";
            try
            {
                using var document = JsonDocument.Parse(StructuredData.ReadJson(file),
                    GameDataLoader.ForgivingJson);
                touched |= ReadFleets(campaign, document.RootElement);
            }
            catch (Exception exception) { errors?.Add($"{origin}: {exception.Message}"); }
        }

        if (touched) AssignHomePorts(campaign);
    }

    /// <summary>The `fleets` table of one document, if it has one. True when it did.</summary>
    private static bool ReadFleets(Campaign campaign, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return false;
        if (!root.TryGetProperty("fleets", out var fleets) ||
            fleets.ValueKind != JsonValueKind.Object) return false;

        var touched = false;
        foreach (var entry in fleets.EnumerateObject())
        {
            var nation = campaign.NationByTag(entry.Name);
            if (nation is null || entry.Value.ValueKind != JsonValueKind.Array) continue;
            campaign.Fleets.RemoveAll(fleet => fleet.NationId == nation.Id);
            foreach (var row in entry.Value.EnumerateArray()) Read(campaign, nation, row);
            touched = true;
        }
        return touched;
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
            Establishment = Math.Max(0, hulls),
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
    ///
    /// A home is a port: a province of the owner's with a harbour, on the same water the
    /// fleet is lying on. The nearest province of any kind was the rule until the sea lanes
    /// could say which provinces have a coast, and it gave Britain's fleet a home port in
    /// Devon's interior and sent a beaten squadron to repair itself in a field.
    /// </summary>
    public static void AssignHomePorts(Campaign campaign, bool keepSaved = false)
    {
        var topology = campaign.ProvinceMap;
        if (topology is null) return;
        var coastal = SeaLanes.CoastalProvinces(topology);
        var coastalSet = keepSaved ? new HashSet<int>(coastal) : null;
        foreach (var fleet in campaign.Fleets)
        {
            var nation = campaign.NationById(fleet.NationId);
            if (nation is null) continue;
            // A loaded campaign's fleets already have homes - perhaps moved since the bookmark, when a
            // port was lost - and the coordinates they were first placed by would hand them back.
            if (keepSaved && fleet.HomeProvinceId > 0 && fleet.HomeProvinceId <= topology.ProvinceCount &&
                coastalSet.Contains(fleet.HomeProvinceId) && topology.ProvinceToNation[fleet.HomeProvinceId] == nation.MapIndex)
                continue;
            if (fleet.Establishment <= 0) fleet.Establishment = fleet.Hulls;
            var basin = SeaLanes.BasinAt(topology, fleet.HomeLatitude, fleet.HomeLongitude);

            var best = -1;
            var bestDistance = float.MaxValue;
            foreach (var province in coastal)
            {
                if (topology.ProvinceToNation[province] != nation.MapIndex) continue;
                if (basin >= 0 && SeaLanes.HarbourBasin(topology, province) != basin) continue;
                var centre = topology.CenterOf(province);
                var distance = NavalOperations.Distance(fleet.HomeLatitude, fleet.HomeLongitude,
                    centre.Latitude, centre.Longitude);
                if (distance >= bestDistance) continue;
                bestDistance = distance;
                best = province;
            }

            // A country with no coast of its own on that water - a mod that parks a navy on a
            // lake, a map that has moved - keeps the old answer rather than none.
            if (best < 0)
            {
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
            }
            fleet.HomeProvinceId = best;

            // Where it lies at home: the written anchorage when that is water, the harbour off
            // the home province when it is not.
            if (!SeaLanes.IsWaterAt(topology, fleet.HomeLatitude, fleet.HomeLongitude) &&
                SeaLanes.TryHarbourPoint(topology, best, out var latitude, out var longitude) &&
                NavalOperations.Distance(latitude, longitude, fleet.HomeLatitude, fleet.HomeLongitude) > 1.5f)
            {
                var wasHome = NavalOperations.Distance(fleet.Latitude, fleet.Longitude,
                    fleet.HomeLatitude, fleet.HomeLongitude) < 0.01f;
                fleet.HomeLatitude = latitude;
                fleet.HomeLongitude = longitude;
                if (wasHome && !fleet.HasOrder) { fleet.Latitude = latitude; fleet.Longitude = longitude; }
            }
            // Only on the first day. This runs whenever a map is attached - a loaded save too, before
            // the save's own borders are laid back over the 2024 ones - and berthed at home a
            // squadron the live game had refused a berth (its port lost or hostile), so loading a
            // save changed the fleet it had saved. Later on, the daily pass (NavalOperations)
            // decides berths by who holds the port now.
            if (campaign.ElapsedDays == 0 &&
                fleet.Mission == FleetMission.Anchored && !fleet.HasOrder && fleet.BerthProvinceId <= 0 &&
                NavalOperations.Distance(fleet.Latitude, fleet.Longitude, fleet.HomeLatitude, fleet.HomeLongitude) <=
                campaign.Defines.FleetBerthRangeDegrees + 1.5f)
                fleet.BerthProvinceId = best;
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
