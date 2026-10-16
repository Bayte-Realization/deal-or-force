using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What a country *is*, as data: its name, the colour it is painted on the map, and the
/// city its government sits in.
///
/// Paradox separates this from history for a good reason, and so does this game now.
/// `common/countries/` says a country exists and what it looks like; `history/countries/`
/// says what it had in the year the bookmark opens. A mod that renames a country, or
/// repaints half the map, or writes the whole thing in another alphabet, touches only the
/// first - and it does so by dropping a file into the folder rather than by editing the
/// base game's, so it does not go stale when the base game adds a line to its own.
///
/// These were compiled constants until now, which meant the most-modded thing in any
/// grand-strategy game was the one thing a mod could not reach.
/// </summary>
public static class CountryDefinitions
{
    /// <summary>Layer every file in `common/countries/` over the scenario's own values.</summary>
    public static void Apply(Campaign campaign)
    {
        foreach (var document in GameDataLoader.DataDocuments("common", "countries"))
            using (document)
                Apply(campaign, document.RootElement);
    }

    /// <summary>
    /// Layer one document over the scenario's own values.
    ///
    /// Separate from the folder walk because a mod package's files and the base game's arrive
    /// through the same door, and because a single document is the only way to check any of this
    /// without writing a file into the shipped folder to see what happens.
    /// </summary>
    public static void Apply(Campaign campaign, JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var nation = campaign.NationByTag(entry.Name);
            if (nation is null) continue;
            Read(entry.Value, "name", text => nation.Name = text);
            Read(entry.Value, "color", text => nation.Color = text);
            Read(entry.Value, "capital_name", text => nation.CapitalName = text);
            // Where that city is. Renaming a capital and moving one are different jobs - a mod
            // renaming a city has no business restating its coordinates, and one laying out a
            // different map has no name to keep - so both are optional and neither clears the
            // other. The starting seat is the region nearest this point, so moving the point
            // moves the government with it.
            //
            // Clamped to the surface of the planet: a decimal point in the wrong place would
            // otherwise put a capital in orbit, where the nearest region is nobody's and the
            // country quietly loses its seat.
            Number(entry.Value, "capital_latitude", value => nation.CapitalLatitude = M.Clamp(value, -90f, 90f));
            Number(entry.Value, "capital_longitude", value => nation.CapitalLongitude = M.Clamp(value, -180f, 180f));
        }
    }

    private static void Read(JsonElement value, string key, Action<string> set)
    {
        if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String &&
            field.GetString() is { Length: > 0 } text)
            set(text);
    }

    /// <summary>
    /// A number, if the file gave one. NaN and infinities are not coordinates and are left alone
    /// rather than clamped, because clamping them would invent a place rather than refuse one.
    /// </summary>
    private static void Number(JsonElement value, string key, Action<float> set)
    {
        if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number &&
            field.TryGetSingle(out var number) && float.IsFinite(number))
            set(number);
    }

    /// <summary>
    /// Write the roster back out. The compiler ships the base game's own file with this, so
    /// what a modder edits is exactly what the game runs on rather than a hand-made
    /// approximation of it - the same arrangement the political map already uses.
    /// </summary>
    public static string Export(Campaign campaign, out int written)
    {
        written = 0;
        var builder = new StringBuilder();
        builder.AppendLine("// Deal or Force - what each country is: its name, its colour on the map, and its capital.");
        builder.AppendLine("//");
        builder.AppendLine("// This is the definition. What a country *had* in 2024 - money, people, an army - is");
        builder.AppendLine("// history/countries/2024/ instead, because those are edited by different people for");
        builder.AppendLine("// different reasons and a total conversion usually wants one without the other.");
        builder.AppendLine("//");
        builder.AppendLine("// Every file in this folder is read in name order and later files win, so a mod that");
        builder.AppendLine("// renames six countries ships `50_my_names.json` with six entries and inherits the");
        builder.AppendLine("// rest. It never has to copy this file, and so never goes stale against it.");
        builder.AppendLine("//");
        builder.AppendLine("// Generated by Tools/MapDataCompiler. Edit it by hand if you like - nothing");
        builder.AppendLine("// regenerates it unless the compiler is run.");
        builder.AppendLine("{");

        var nations = new List<Nation>(campaign.Nations);
        nations.Sort((left, right) => string.CompareOrdinal(left.Tag, right.Tag));
        for (var index = 0; index < nations.Count; index++)
        {
            var nation = nations[index];
            if (string.IsNullOrWhiteSpace(nation.Tag)) continue;
            written++;
            builder.Append("  \"").Append(nation.Tag).Append("\": { \"name\": ")
                .Append(Quote(nation.Name))
                .Append(", \"color\": ").Append(Quote(nation.Color));
            if (!string.IsNullOrWhiteSpace(nation.CapitalName))
                builder.Append(", \"capital_name\": ").Append(Quote(nation.CapitalName));
            builder.Append(" }");
            if (index < nations.Count - 1) builder.Append(',');
            builder.AppendLine();
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    private static string Quote(string text) => JsonSerializer.Serialize(text ?? "");
}
