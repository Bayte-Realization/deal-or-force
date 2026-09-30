using System;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// Where a number came from.
///
/// This is the thing a Paradox interface is actually made of. The screen shows one figure -
/// a budget, a stability, a war support - and hovering it shows the list of named pieces
/// that add up to it. That single habit is why those games can be dense without being
/// frightening: everything is on screen, and the explanation of anything is one hover away
/// rather than a wiki page.
///
/// This game computed all of those numbers as one long expression and threw the parts
/// away, so the interface could only ever say what a number *is*, never why. A breakdown
/// keeps the parts.
///
/// It is deliberately a mutable object that is refilled rather than rebuilt: these are
/// recomputed for every country every economic week, and the simulation is not allowed to
/// start making garbage again.
/// </summary>
[JsonConverter(typeof(BreakdownConverter))]
public sealed class Breakdown
{
    /// <summary>One named contribution. Negative values are costs; the renderer colours them.</summary>
    public readonly record struct Entry(string Source, float Value);

    private readonly List<Entry> _entries = new();

    /// <summary>What the number is called, for the heading of the tooltip.</summary>
    public string Title { get; private set; } = "";

    /// <summary>The unit the parts are in, so the renderer can format them: "money", "share", "people".</summary>
    public string Unit { get; private set; } = "";

    public IReadOnlyList<Entry> Entries => _entries;

    /// <summary>The sum of every part. What the screen shows must equal this.</summary>
    public float Total { get; private set; }

    /// <summary>Whether anything has been recorded yet, so the UI can stay quiet until it has.</summary>
    public bool HasEntries => _entries.Count > 0;

    internal void Restore(string title, string unit, float total, IEnumerable<Entry> entries)
    {
        Title = title ?? "";
        Unit = unit ?? "";
        Total = total;
        _entries.Clear();
        if (entries is not null) _entries.AddRange(entries);
    }

    public void Begin(string title, string unit)
    {
        Title = title;
        Unit = unit;
        _entries.Clear();
        Total = 0f;
    }

    /// <summary>
    /// Record one part. A part worth nothing is left out: a tooltip listing fifteen zeroes
    /// is exactly the wall of numbers this is meant to avoid.
    /// </summary>
    public void Add(string source, float value)
    {
        Total += value;
        if (MathF.Abs(value) < 0.0000005f) return;
        _entries.Add(new Entry(source, value));
    }

    /// <summary>
    /// Record a part that is already counted in the total but is worth naming - the base a
    /// clamp started from, say. Use sparingly; it is the one way a breakdown can lie.
    /// </summary>
    public void Note(string source, float value) => _entries.Add(new Entry(source, value));

    /// <summary>
    /// Fix the total to what actually happened. Clamps and floors mean the parts do not
    /// always survive contact with the result, and a tooltip that adds up to something the
    /// screen does not show is worse than no tooltip - so when they differ, the difference
    /// is named rather than hidden.
    /// </summary>
    public void Settle(float actual, string limitedBy = "held at its limit")
    {
        var difference = actual - Total;
        if (MathF.Abs(difference) > 0.0000005f) _entries.Add(new Entry(limitedBy, difference));
        Total = actual;
    }
}

/// <summary>Preserves the explanation attached to a figure across saves. Earlier JSON
/// already wrote these properties, but their private setters meant it read them as blanks.</summary>
public sealed class BreakdownConverter : JsonConverter<Breakdown>
{
    public override Breakdown Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.StartObject) throw new JsonException("Expected a breakdown object.");
        var title = "";
        var unit = "";
        var total = 0f;
        var entries = new List<Breakdown.Entry>();
        while (reader.Read() && reader.TokenType != JsonTokenType.EndObject)
        {
            if (reader.TokenType != JsonTokenType.PropertyName) throw new JsonException("Invalid breakdown property.");
            var name = reader.GetString();
            if (!reader.Read()) throw new JsonException("Missing breakdown value.");
            switch (name)
            {
                case "Title": title = reader.GetString() ?? ""; break;
                case "Unit": unit = reader.GetString() ?? ""; break;
                case "Total": total = reader.GetSingle(); break;
                case "Entries": entries = JsonSerializer.Deserialize<List<Breakdown.Entry>>(ref reader, options) ?? new(); break;
                default: reader.Skip(); break;
            }
        }
        var value = new Breakdown();
        value.Restore(title, unit, total, entries);
        return value;
    }

    public override void Write(Utf8JsonWriter writer, Breakdown value, JsonSerializerOptions options)
    {
        writer.WriteStartObject();
        writer.WriteString("Title", value.Title);
        writer.WriteString("Unit", value.Unit);
        writer.WritePropertyName("Entries");
        JsonSerializer.Serialize(writer, value.Entries, options);
        writer.WriteNumber("Total", value.Total);
        writer.WriteEndObject();
    }
}
