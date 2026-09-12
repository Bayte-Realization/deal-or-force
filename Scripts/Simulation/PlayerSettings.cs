using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What the player has chosen about how the game looks, kept between sessions.
///
/// The game had nowhere to put a preference at all: the language and the projection are
/// toggles on the main menu that reset every launch, which is tolerable for a thing you flip
/// while looking at it and not for a graphics option. Somebody who turns the map smoothing
/// off because their machine is happier without it should not have to turn it off again
/// tomorrow.
///
/// Deliberately small. One flat JSON file beside the saves, written when something changes,
/// read once at boot, and unreadable-means-defaults rather than unreadable-means-crash.
///
/// It holds numbers and text as well as flags now, because the options screen is built out
/// of it and a resolution is not a yes-or-no. What it still does not do is know anything
/// about what any of the keys mean - that is `common/settings`, and this is only the drawer
/// the answers are kept in.
/// </summary>
public static class PlayerSettings
{
    private const string FileName = "settings.json";

    private static readonly Dictionary<string, JsonElement> Values = new(StringComparer.Ordinal);
    private static string _directory;
    private static bool _loaded;

    /// <summary>
    /// Where the file lives. Godot hands this in, because `user://` is the engine's idea and
    /// the simulation layer does not import Godot - the same rule that lets this whole layer
    /// be compiled and run headless by the bench.
    /// </summary>
    public static void UseDirectory(string directory)
    {
        _directory = directory;
        _loaded = false;
        Load();
    }

    private static string Path => string.IsNullOrEmpty(_directory)
        ? null
        : System.IO.Path.Combine(_directory, FileName);

    private static void Load()
    {
        if (_loaded) return;
        _loaded = true;
        Values.Clear();
        var path = Path;
        if (path is null || !File.Exists(path)) return;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(path), GameDataLoader.ForgivingJson);
            if (document.RootElement.ValueKind != JsonValueKind.Object) return;
            foreach (var entry in document.RootElement.EnumerateObject())
                Values[entry.Name] = entry.Value.Clone();
        }
        catch (Exception exception)
        {
            // A settings file that will not parse is a settings file the player loses, not a
            // game they cannot start.
            DiagnosticLogs.Write("errors", $"SETTINGS_UNREADABLE file={path} error={exception.Message}");
            Values.Clear();
        }
    }

    public static bool Bool(string key, bool fallback)
    {
        Load();
        if (!Values.TryGetValue(key, out var value)) return fallback;
        return value.ValueKind switch
        {
            JsonValueKind.True => true,
            JsonValueKind.False => false,
            _ => fallback
        };
    }

    public static int Int(string key, int fallback)
    {
        Load();
        return Values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetInt32(out var number)
            ? number
            : fallback;
    }

    public static float Float(string key, float fallback)
    {
        Load();
        return Values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.Number &&
               value.TryGetSingle(out var number)
            ? number
            : fallback;
    }

    public static string Text(string key, string fallback)
    {
        Load();
        return Values.TryGetValue(key, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback
            : fallback;
    }

    /// <summary>Whether the player has ever said anything about this key.</summary>
    public static bool Has(string key)
    {
        Load();
        return Values.ContainsKey(key);
    }

    public static void Set(string key, bool value) => Write(key, value ? "true" : "false");

    public static void Set(string key, int value) =>
        Write(key, value.ToString(System.Globalization.CultureInfo.InvariantCulture));

    public static void Set(string key, float value) =>
        Write(key, value.ToString("R", System.Globalization.CultureInfo.InvariantCulture));

    public static void Set(string key, string value) => Write(key, JsonSerializer.Serialize(value ?? ""));

    /// <summary>Forget one answer, so the option goes back to whatever the game defaults to.</summary>
    public static void Clear(string key)
    {
        Load();
        if (Values.Remove(key)) Save();
    }

    private static void Write(string key, string json)
    {
        Load();
        try
        {
            Values[key] = JsonDocument.Parse(json).RootElement.Clone();
        }
        catch (Exception exception)
        {
            DiagnosticLogs.Write("errors", $"SETTINGS_UNWRITABLE_VALUE key={key} error={exception.Message}");
            return;
        }
        Save();
    }

    private static void Save()
    {
        var path = Path;
        if (path is null) return;
        try
        {
            Directory.CreateDirectory(_directory);
            using var stream = File.Create(path);
            using var writer = new Utf8JsonWriter(stream, new JsonWriterOptions { Indented = true });
            writer.WriteStartObject();
            foreach (var entry in Values)
            {
                writer.WritePropertyName(entry.Key);
                entry.Value.WriteTo(writer);
            }
            writer.WriteEndObject();
        }
        catch (Exception exception)
        {
            DiagnosticLogs.Write("errors", $"SETTINGS_UNWRITABLE file={path} error={exception.Message}");
        }
    }
}
