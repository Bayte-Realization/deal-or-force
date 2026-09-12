using System;
using System.Collections.Generic;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What a region *is*, as data: what it is called and what kind of administrative unit it
/// is. The counterpart of <see cref="CountryDefinitions"/>, one level down.
///
/// The regions were the last of the world still compiled into the game. A country could be
/// renamed, repainted and given a different capital by a mod that dropped one file into
/// `common/countries/`; the 1,826 provinces, oblasts, departments and prefectures under
/// them could not be touched at all, because their names lived in a C# array. That is an
/// odd place for a grand-strategy game to draw the line: a total conversion that moves the
/// game to another century or another planet renames every region it has, and half of the
/// mods that do not still want their own spelling of a disputed one.
///
/// `common/states/` says a region exists and what it is called; `history/states/2024/` says
/// what it had when the bookmark opens.
///
/// Both are keyed by the region's id: a slug of its published subdivision code, or of the
/// country and the name where no code was published. Not by the code itself, tempting as
/// that is to read - nine codes are shared by more than one region, and MG-F alone covers
/// five Madagascan provinces, so a file keyed that way would let a mod rename one region
/// and quietly hit another. A code is still accepted where it names exactly one region,
/// because that is what the history files were written with.
/// </summary>
public static class StateDefinitions
{
    /// <summary>Layer every file in `common/states/` over the scenario's own values.</summary>
    public static void Apply(Campaign campaign)
    {
        if (campaign is null) return;

        // Built once. The old country overlay walks the whole list per entry, which is fine
        // for 261 of them and would be 1,826 × 1,826 string comparisons here.
        var byKey = new Dictionary<string, ScenarioState>(StringComparer.OrdinalIgnoreCase);
        var ambiguous = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in campaign.States)
        {
            if (!string.IsNullOrWhiteSpace(state.Id)) byKey[state.Id] = state;
            if (string.IsNullOrWhiteSpace(state.Iso)) continue;
            // A code that turns out to name a second region is withdrawn rather than
            // resolved: quietly renaming one of five provinces is worse than not
            // renaming any of them, and the id always works.
            if (!byKey.TryAdd(state.Iso, state) && !ambiguous.Add(state.Iso)) continue;
        }
        foreach (var code in ambiguous) byKey.Remove(code);

        foreach (var document in GameDataLoader.DataDocuments("common", "states"))
            using (document)
                Merge(document.RootElement, byKey);
    }

    /// <summary>One file, over regions already indexed. The bench feeds this directly.</summary>
    public static int Apply(Campaign campaign, string json)
    {
        if (campaign is null || string.IsNullOrWhiteSpace(json)) return 0;
        var byKey = new Dictionary<string, ScenarioState>(StringComparer.OrdinalIgnoreCase);
        foreach (var state in campaign.States)
            if (!string.IsNullOrWhiteSpace(state.Id)) byKey[state.Id] = state;
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        return Merge(document.RootElement, byKey);
    }

    /// <summary>How many regions this file actually spoke for.</summary>
    private static int Merge(JsonElement root, Dictionary<string, ScenarioState> byKey)
    {
        if (root.ValueKind != JsonValueKind.Object) return 0;
        var applied = 0;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            if (!byKey.TryGetValue(entry.Name, out var state)) continue;
            Read(entry.Value, "name", text => state.Name = text);
            Read(entry.Value, "kind", text => state.Kind = text);
            applied++;
        }
        return applied;
    }

    private static void Read(JsonElement value, string key, Action<string> set)
    {
        if (value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String &&
            field.GetString() is { Length: > 0 } text)
            set(text);
    }

    /// <summary>
    /// Write the roster back out, so what a modder edits is the file the game actually runs
    /// on rather than a hand-made approximation of it - the same arrangement the countries
    /// and the political map already use.
    /// </summary>
    public static string Export(Campaign campaign, out int written)
    {
        written = 0;
        var builder = new StringBuilder();
        builder.AppendLine("// Deal or Force - what each region is: its name and what kind of unit it is.");
        builder.AppendLine("//");
        builder.AppendLine("// This is the definition. What a region *had* in 2024 - people, industry, what is");
        builder.AppendLine("// under the ground - is history/states/2024/ instead, for the same reason the");
        builder.AppendLine("// countries are split that way: they are edited by different people for different");
        builder.AppendLine("// reasons, and a total conversion usually wants one without the other.");
        builder.AppendLine("//");
        builder.AppendLine("// The key is the region id: a slug of its published subdivision code, or of the");
        builder.AppendLine("// country and the name where no code was published. It is the same key the");
        builder.AppendLine("// history files use. Codes themselves are not unique - nine of them name more");
        builder.AppendLine("// than one region - which is why they are not the key here.");
        builder.AppendLine("//");
        builder.AppendLine("// Every file in this folder is read in name order and later files win, so a mod that");
        builder.AppendLine("// renames nine regions ships `50_my_regions.json` with nine entries and inherits the");
        builder.AppendLine("// rest. It never has to copy this file, and so never goes stale against it.");
        builder.AppendLine("//");
        builder.AppendLine("// Generated by Tools/MapDataCompiler. Edit it by hand if you like - nothing");
        builder.AppendLine("// regenerates it unless the compiler is run.");
        builder.AppendLine("{");

        var states = new List<ScenarioState>(campaign.States);
        states.Sort((left, right) => left.MapIndex.CompareTo(right.MapIndex));
        for (var index = 0; index < states.Count; index++)
        {
            var state = states[index];
            var key = state.Id;
            if (string.IsNullOrWhiteSpace(key)) continue;
            written++;
            builder.Append("  ").Append(Quote(key)).Append(": { \"name\": ").Append(Quote(state.Name));
            if (!string.IsNullOrWhiteSpace(state.Kind))
                builder.Append(", \"kind\": ").Append(Quote(state.Kind));
            builder.Append(" }");
            if (index < states.Count - 1) builder.Append(',');
            builder.AppendLine();
        }
        builder.AppendLine("}");
        return builder.ToString();
    }

    /// <summary>
    /// Written as the letters themselves rather than as escapes. Region names are full of
    /// accents and scripts - Entre Ríos, Åland, Ōsaka - and a file that spells those out as
    /// six-character escapes is a file no modder can read, let alone edit. The output is
    /// UTF-8 JSON either way; only what JSON actually reserves is escaped.
    /// </summary>
    private static readonly JsonSerializerOptions Readable = new()
    {
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    private static string Quote(string text) => JsonSerializer.Serialize(text ?? "", Readable);
}
