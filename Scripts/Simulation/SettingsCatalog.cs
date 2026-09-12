using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>What kind of thing an option is, which is what decides how it is drawn.</summary>
public enum SettingKind
{
    /// <summary>On or off.</summary>
    Toggle,

    /// <summary>One of a written list - a language, a window mode, a resolution.</summary>
    Choice,

    /// <summary>A number between two numbers, stepped.</summary>
    Slider
}

public sealed class SettingDefinition
{
    public string Id = "";
    public string Category = "game";
    public string Name = "";
    public string Description = "";
    public int Order;
    public SettingKind Kind = SettingKind.Toggle;

    /// <summary>Toggles.</summary>
    public bool Default;

    /// <summary>Choices: the values, and the words shown for them.</summary>
    public readonly List<string> Values = new();
    public readonly List<string> Labels = new();
    public string DefaultValue = "";

    /// <summary>Sliders.</summary>
    public float Minimum;
    public float Maximum = 1f;
    public float Step = 0.1f;
    public float DefaultNumber;

    /// <summary>How a slider's number is written: a percentage, a plain number, or seconds.</summary>
    public string Unit = "percent";

    /// <summary>
    /// Whether changing this takes effect now or at the next start. Godot can do most things
    /// live; the ones it cannot should say so rather than looking broken.
    /// </summary>
    public bool NeedsRestart;
}

/// <summary>
/// The options screen, as data.
///
/// A settings screen is the one part of a game that is pure list, and writing it as a list
/// in C# means every new option is a code change and a mod can neither add one nor hide
/// one. Here the screen is built from `common/settings`: the categories, the order, the
/// words, the ranges and the defaults all come out of the file, and the code's only job is
/// to know what to *do* about an id it recognises.
///
/// An option a mod invents still works as far as this goes - it is drawn, it is saved, and
/// the mod's own script can read it back out of <see cref="PlayerSettings"/>. What a mod
/// cannot do is make the base game understand a new id, which is the honest boundary.
/// </summary>
public static class SettingsCatalog
{
    private static readonly List<SettingDefinition> Definitions = new();

    public static IReadOnlyList<SettingDefinition> All => Definitions;

    static SettingsCatalog() => ResetCatalog();

    public static SettingDefinition Of(string id)
    {
        foreach (var definition in Definitions)
            if (string.Equals(definition.Id, id, StringComparison.OrdinalIgnoreCase)) return definition;
        return null;
    }

    /// <summary>Every category that has something in it, in the order the files gave them.</summary>
    public static IReadOnlyList<string> Categories()
    {
        var seen = new List<string>();
        foreach (var definition in Definitions)
            if (!seen.Contains(definition.Category, StringComparer.OrdinalIgnoreCase))
                seen.Add(definition.Category);
        return seen;
    }

    public static IEnumerable<SettingDefinition> In(string category)
    {
        foreach (var definition in Definitions)
            if (string.Equals(definition.Category, category, StringComparison.OrdinalIgnoreCase))
                yield return definition;
    }

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "settings"))
            using (document)
                Merge(document.RootElement, errors);
        Sort();
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Sort();
    }

    /// <summary>
    /// Add or replace options from one file. Later wins by id, so a mod that only wants the
    /// autosave interval to go further names `autosave_interval` and inherits the rest - and
    /// a mod that wants an option gone can set its category to something the screen does not
    /// show rather than having to fork the file.
    /// </summary>
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = Of(entry.Name);
            if (definition is null)
            {
                definition = new SettingDefinition { Id = entry.Name };
                Definitions.Add(definition);
            }
            Read(definition, entry.Value, errors);
        }
    }

    private static void Read(SettingDefinition definition, JsonElement element, List<string> errors)
    {
        foreach (var field in element.EnumerateObject())
            switch (field.Name.ToLowerInvariant())
            {
                case "category": definition.Category = Text(field.Value, definition.Category); break;
                case "name": definition.Name = Text(field.Value, definition.Name); break;
                case "description": definition.Description = Text(field.Value, definition.Description); break;
                case "order": definition.Order = (int)Number(field.Value, definition.Order); break;
                case "unit": definition.Unit = Text(field.Value, definition.Unit); break;
                case "needs_restart": definition.NeedsRestart = Flag(field.Value, definition.NeedsRestart); break;
                case "kind":
                    definition.Kind = Text(field.Value, "toggle").ToLowerInvariant() switch
                    {
                        "choice" => SettingKind.Choice,
                        "slider" => SettingKind.Slider,
                        _ => SettingKind.Toggle
                    };
                    break;
                case "default":
                    // One field, three meanings, decided by what is written there: `true`
                    // for a toggle, a number for a slider, a string for a choice. A file
                    // that says `"default": 0.7` has said everything it needs to.
                    switch (field.Value.ValueKind)
                    {
                        case JsonValueKind.True:
                        case JsonValueKind.False:
                            definition.Default = field.Value.ValueKind == JsonValueKind.True;
                            break;
                        case JsonValueKind.Number:
                            definition.DefaultNumber = Number(field.Value, definition.DefaultNumber);
                            break;
                        case JsonValueKind.String:
                            definition.DefaultValue = field.Value.GetString() ?? "";
                            break;
                    }
                    break;
                case "minimum": definition.Minimum = Number(field.Value, definition.Minimum); break;
                case "maximum": definition.Maximum = Number(field.Value, definition.Maximum); break;
                case "step": definition.Step = MathF.Max(0.0001f, Number(field.Value, definition.Step)); break;
                case "values" when field.Value.ValueKind == JsonValueKind.Array:
                    definition.Values.Clear();
                    foreach (var item in field.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            definition.Values.Add(item.GetString() ?? "");
                    break;
                case "labels" when field.Value.ValueKind == JsonValueKind.Array:
                    definition.Labels.Clear();
                    foreach (var item in field.Value.EnumerateArray())
                        if (item.ValueKind == JsonValueKind.String)
                            definition.Labels.Add(item.GetString() ?? "");
                    break;
                default:
                    errors?.Add($"common/settings: '{definition.Id}.{field.Name}' is not something this game reads.");
                    break;
            }
    }

    private static void Sort() =>
        Definitions.Sort((left, right) =>
        {
            var order = left.Order.CompareTo(right.Order);
            return order != 0 ? order : string.CompareOrdinal(left.Id, right.Id);
        });

    private static string Text(JsonElement element, string fallback) =>
        element.ValueKind == JsonValueKind.String ? element.GetString() ?? fallback : fallback;

    private static float Number(JsonElement element, float fallback) =>
        element.ValueKind == JsonValueKind.Number && element.TryGetSingle(out var value) ? value : fallback;

    private static bool Flag(JsonElement element, bool fallback) => element.ValueKind switch
    {
        JsonValueKind.True => true,
        JsonValueKind.False => false,
        _ => fallback
    };

    // ---- what the player has actually chosen -------------------------------------------

    public static bool Flag(string id)
    {
        var definition = Of(id);
        return definition is null
            ? PlayerSettings.Bool(id, false)
            : PlayerSettings.Bool(id, definition.Default);
    }

    public static string Chosen(string id)
    {
        var definition = Of(id);
        var fallback = definition?.DefaultValue ?? "";
        var value = PlayerSettings.Text(id, fallback);
        // A value the option no longer offers - a resolution a new monitor does not have, a
        // language a mod stopped shipping - is not a value.
        if (definition is not null && definition.Values.Count > 0 &&
            !definition.Values.Contains(value, StringComparer.Ordinal))
            return definition.Values.Contains(fallback, StringComparer.Ordinal) ? fallback : definition.Values[0];
        return value;
    }

    public static float Number(string id)
    {
        var definition = Of(id);
        if (definition is null) return PlayerSettings.Float(id, 0f);
        var value = PlayerSettings.Float(id, definition.DefaultNumber);
        return Math.Clamp(value, definition.Minimum, definition.Maximum);
    }
}
