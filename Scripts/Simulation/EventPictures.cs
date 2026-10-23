using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// The picture above an event's title. Presentation only: nothing in the simulation reads it.
///
/// An event names its own with a "picture" field; otherwise common/event_pictures says which
/// of the shared pictures fits it. A name ("finance") is gfx/events/event_finance.jpg; a path
/// ("leaders/x.png") is under gfx/; a res:// path is taken as it is, which is how a mod points
/// at a picture of its own.
/// </summary>
public static class EventPictures
{
    private static Dictionary<string, string> _byEvent;

    /// <summary>The engine path of the picture for an event, or "" for none.</summary>
    public static string PathFor(CampaignEventDefinition definition)
    {
        if (definition is null) return "";
        _byEvent ??= Load();
        var picture = definition.Picture.Length > 0 ? definition.Picture : _byEvent.GetValueOrDefault(definition.Id, "");
        return Resolve(picture);
    }

    /// <summary>The engine path a picture name stands for.</summary>
    public static string Resolve(string picture)
    {
        if (string.IsNullOrWhiteSpace(picture)) return "";
        picture = picture.Trim();
        if (picture.StartsWith("res://", StringComparison.Ordinal)) return picture;
        if (picture.Contains('/')) return "res://game/gfx/" + picture.TrimStart('/');
        return "res://game/gfx/events/event_" + picture + ".jpg";
    }

    /// <summary>Every event the table names, and its picture.</summary>
    public static IReadOnlyDictionary<string, string> Table => _byEvent ??= Load();

    /// <summary>Read the base game's table again.</summary>
    public static void ResetCatalog() => _byEvent = Load();

    private static Dictionary<string, string> Load()
    {
        var table = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "event_pictures"))
            using (document)
                Merge(document.RootElement, table);
        return table;
    }

    private static void Merge(JsonElement root, Dictionary<string, string> table)
    {
        if (root.ValueKind != JsonValueKind.Object || !root.TryGetProperty("events", out var events) ||
            events.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in events.EnumerateObject().Where(e => e.Value.ValueKind == JsonValueKind.String))
            table[entry.Name] = entry.Value.GetString().Trim();
    }
}
