using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Which of the world's drawn layers a game - or a mod - has at all: game/common/world_layers.
///
/// The player's own switches (clouds, fog, events, trade, cities) say what to show this
/// minute. These say what exists. A mod set in 1453 has no lorries on its roads, no container
/// ships on its sea lanes, and no supply lines drawn in the colours of 2024; it says so in common/world_layers,
/// key by key, and the layer is never drawn. Every layer is on unless a file says otherwise,
/// and a key nobody knows is reported rather than ignored.
/// </summary>
public static class WorldLayers
{
    /// <summary>Every layer a file may name, and what it is.</summary>
    public static readonly IReadOnlyDictionary<string, string> Known = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
    {
        ["trade_traffic"] = "ships and lorries on the trade lanes (models in gfx/models/trade)",
        ["trade_lanes"] = "the dashed lines the trade runs along",
        ["weather"] = "cloud deck, rain shade, mist and air glow",
        ["sky_clouds"] = "the photographed cloud layer of the sky",
        ["ice"] = "the ice shell and sheets of a frozen world",
        ["event_pulses"] = "the rings that mark news on the map",
        ["city_labels"] = "city and town names",
        ["capitals"] = "capital stars",
        ["country_labels"] = "country names across their territory",
        ["military_counters"] = "army, fleet and battle counters",
        ["supply_lines"] = "the selected army's supply line"
    };

    private static readonly Dictionary<string, bool> Switches = new(StringComparer.OrdinalIgnoreCase);
    private static bool _loaded;

    public static void ResetCatalog(List<string> errors = null)
    {
        Switches.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "world_layers"))
            using (document)
                Merge(document.RootElement, errors);
        _loaded = true;
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (!Known.ContainsKey(entry.Name))
            {
                errors?.Add($"common/world_layers: '{entry.Name}' is not a layer this game draws.");
                continue;
            }
            if (entry.Value.ValueKind is JsonValueKind.True or JsonValueKind.False) Switches[entry.Name] = entry.Value.GetBoolean();
            else errors?.Add($"common/world_layers: '{entry.Name}' must be true or false.");
        }
    }

    /// <summary>Whether this layer exists in this game. Unnamed layers are on.</summary>
    public static bool On(string layer)
    {
        if (!_loaded) ResetCatalog();
        return !Switches.TryGetValue(layer, out var on) || on;
    }
}
