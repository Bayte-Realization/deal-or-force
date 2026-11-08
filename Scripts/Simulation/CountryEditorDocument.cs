using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One country as a whole, the way a scenario author thinks of it - name, colour, capital,
/// politics, leaders, peoples, money, land - written into the same files the base game reads.
///
/// The game splits a country across five folders for good reasons (common/countries is what a
/// country is, history/countries what it had on the bookmark's morning, localization what it
/// is called in each language, history/provinces where its borders run), and every one of
/// those reasons is invisible to somebody who just wants to make a country. This is the other
/// side of that split: one object to edit, and a writer that puts each value in the file the
/// loaders already read for it. Nothing new is invented for the game to learn - a mod written
/// by the country editor is indistinguishable from one written by hand, and loads through the
/// same doors.
///
/// Every file it writes is its own, named for the country (`90_editor_&lt;tag&gt;`), so saving
/// one country never rewrites another's, and they sort after the base game so they win.
/// It never writes into the base game: the root it is given is a mod's folder.
/// </summary>
public sealed class CountryEditorDocument
{
    public string Tag = "";

    /// <summary>Name and adjective by language id (english, russian, ...). Adjective may be empty.</summary>
    public Dictionary<string, (string Name, string Adjective)> Names = new(StringComparer.OrdinalIgnoreCase);

    public string Colour = "";
    public string CapitalName = "";
    public float? CapitalLatitude;
    public float? CapitalLongitude;
    public string Government = "";
    public string Ideology = "";
    public string HeadOfState = "";
    public string HeadOfGovernment = "";
    public string HeadOfStatePortrait = "";
    public string HeadOfGovernmentPortrait = "";
    public bool HasPortraitSettings;
    public List<string> PrimaryCultures = new();

    /// <summary>Starting treasury, in billions like every money figure in the game; null leaves the base value.</summary>
    public float? Treasury;

    /// <summary>
    /// Provinces whose owner this document sets on the bookmark: the country's added regions
    /// (owner = Tag) and any it gives up (owner = whoever receives them). Empty leaves the map alone.
    /// </summary>
    public List<(int Province, string Owner)> Ownership = new();

    /// <summary>The stem of every file this document writes: `90_editor_kor`.</summary>
    public string FileStem => "90_editor_" + Tag.ToLowerInvariant();

    /// <summary>The country as the running campaign has it, in every language the game has loaded.</summary>
    public static CountryEditorDocument FromNation(Nation nation, IEnumerable<string> languages)
    {
        var document = new CountryEditorDocument
        {
            Tag = nation.Tag,
            Colour = FlagDesign.Normalise(nation.Color) ?? "#888888",
            CapitalName = nation.CapitalName ?? "",
            Government = nation.Government ?? "",
            Ideology = nation.Ideology ?? "",
            HeadOfState = nation.HeadOfStateName ?? "",
            HeadOfGovernment = nation.HeadOfGovernmentName ?? "",
            PrimaryCultures = DemographyCatalog.PrimaryCultures(nation.Tag).ToList(),
            Treasury = nation.Treasury
        };
        foreach (var language in languages)
        {
            var name = Loc.TextIn(language, $"country.{nation.Tag}.name");
            var adjective = Loc.TextIn(language, $"country.{nation.Tag}.adjective");
            // A language with no row of its own shows the English name to start from, so the
            // author sees what that language currently falls back to rather than a blank.
            document.Names[language] = (string.IsNullOrEmpty(name) ? nation.Name : name, adjective ?? "");
        }
        return document;
    }

    /// <summary>What is wrong with the document, in words a scenario author can act on. Empty when it can be saved.</summary>
    public List<string> Problems()
    {
        var problems = new List<string>();
        if (Tag.Length is < 2 or > 8 || !Tag.All(c => c is >= 'A' and <= 'Z' or >= '0' and <= '9'))
            problems.Add("The tag must be 2-8 capital letters or digits.");
        if (FlagDesign.Normalise(Colour) is null) problems.Add($"The colour '{Colour}' is not #rrggbb.");
        if (!Names.Values.Any(n => n.Name.Trim().Length > 0)) problems.Add("Give the country a name in at least one language.");
        if (Government.Length > 0 && !GovernmentCatalog.Knows(Government)) problems.Add($"There is no form of government called '{Government}'.");
        if (Ideology.Length > 0 && !IdeologyCatalog.Knows(Ideology)) problems.Add($"There is no ideology called '{Ideology}'.");
        foreach (var culture in PrimaryCultures)
            if (!DemographyCatalog.Cultures.ContainsKey(culture)) problems.Add($"There is no culture called '{culture}'.");
        if (Treasury is { } money && !float.IsFinite(money)) problems.Add("The treasury is not a number.");
        if (CapitalLatitude is { } lat && (!float.IsFinite(lat) || MathF.Abs(lat) > 90)) problems.Add("Capital latitude must be -90 to 90.");
        if (CapitalLongitude is { } lon && (!float.IsFinite(lon) || MathF.Abs(lon) > 180)) problems.Add("Capital longitude must be -180 to 180.");
        if (Ownership.Any(o => o.Province <= 0 || string.IsNullOrWhiteSpace(o.Owner))) problems.Add("A region change names no province or no owner.");
        foreach (var portrait in new[] { HeadOfStatePortrait, HeadOfGovernmentPortrait })
            if (portrait.Length > 200 || portrait.Any(char.IsControl) || portrait.IndexOfAny(new[] { '/', '\\', ':' }) >= 0)
                problems.Add("Portrait must be a sprite id, not a file path or URL.");
        return problems;
    }

    /// <summary>
    /// The files this document is, by path inside the mod: what <see cref="Write"/> puts on disk
    /// and what a test can compare without touching one. A part that says nothing writes no file.
    /// </summary>
    public Dictionary<string, string> Files()
    {
        var files = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var english = Names.TryGetValue("english", out var en) && en.Name.Trim().Length > 0 ? en.Name.Trim()
            : Names.Values.Select(n => n.Name.Trim()).FirstOrDefault(n => n.Length > 0) ?? Tag;

        files[$"common/countries/{FileStem}.json"] = Json(
            $"What {Tag} is - its name, map colour and capital. Written by the country editor; edit freely.",
            writer =>
            {
                writer.WriteString("name", english);
                writer.WriteString("color", FlagDesign.Normalise(Colour) ?? Colour);
                if (CapitalName.Trim().Length > 0) writer.WriteString("capital_name", CapitalName.Trim());
                if (CapitalLatitude is { } lat) writer.WriteNumber("capital_latitude", lat);
                if (CapitalLongitude is { } lon) writer.WriteNumber("capital_longitude", lon);
            });

        files[$"history/countries/{FileStem}.json"] = Json(
            $"What {Tag} had on the bookmark's first morning. Ids come from common/governments, common/ideologies and common/cultures; money is in billions.",
            writer =>
            {
                if (Government.Length > 0) writer.WriteString("government", Government);
                if (Ideology.Length > 0) writer.WriteString("ideology", Ideology);
                if (Treasury is { } money) writer.WriteNumber("treasury", money);
                if (HeadOfState.Trim().Length > 0) writer.WriteString("head_of_state", HeadOfState.Trim());
                if (HeadOfGovernment.Trim().Length > 0) writer.WriteString("head_of_government", HeadOfGovernment.Trim());
                if (PrimaryCultures.Count > 0)
                {
                    writer.WritePropertyName("primary_cultures");
                    writer.WriteStartArray();
                    foreach (var culture in PrimaryCultures) writer.WriteStringValue(culture);
                    writer.WriteEndArray();
                }
            });

        if (HasPortraitSettings || HeadOfStatePortrait.Length > 0 || HeadOfGovernmentPortrait.Length > 0)
            files[$"history/rulers/{FileStem}.json"] = Json("Officeholder pictures. Empty sprite ids restore automatic portraits.", writer =>
            {
                if (HeadOfState.Trim().Length > 0) writer.WriteString("head_of_state", HeadOfState.Trim());
                if (HeadOfGovernment.Trim().Length > 0) writer.WriteString("head_of_government", HeadOfGovernment.Trim());
                writer.WriteString("state_portrait", HeadOfStatePortrait.Trim());
                writer.WriteString("gov_portrait", HeadOfGovernmentPortrait.Trim());
            });

        foreach (var (language, (name, adjective)) in Names.OrderBy(pair => pair.Key, StringComparer.Ordinal))
        {
            if (name.Trim().Length == 0 && adjective.Trim().Length == 0) continue;
            using var buffer = new MemoryStream();
            using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
                   { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
            {
                writer.WriteStartObject();
                if (name.Trim().Length > 0) writer.WriteString($"country.{Tag}.name", name.Trim());
                if (adjective.Trim().Length > 0) writer.WriteString($"country.{Tag}.adjective", adjective.Trim());
                writer.WriteEndObject();
            }
            files[$"localization/{language}/{FileStem}.json"] = Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
        }

        if (Ownership.Count > 0)
        {
            var csv = new StringBuilder();
            csv.AppendLine($"# Regions {Tag} holds or gives up on the bookmark, written by the country editor.");
            csv.AppendLine("# province;owner - layered over the base game's political map in load order.");
            csv.AppendLine("province;owner;controller");
            foreach (var (province, owner) in Ownership.OrderBy(o => o.Province))
                csv.Append(province.ToString(CultureInfo.InvariantCulture)).Append(';').AppendLine(owner.Trim().ToUpperInvariant());
            files[$"history/provinces/{FileStem}.csv"] = csv.ToString();
        }
        return files;
    }

    /// <summary>
    /// Write the country into a mod's folder, all files or none. A file this editor wrote before
    /// is replaced (its previous version kept as .bak); a file that changed on disk since it was
    /// read is not overwritten - the whole save stops and says so.
    /// </summary>
    /// <returns>The paths written.</returns>
    public IReadOnlyList<string> Write(string modRoot)
    {
        var problems = Problems();
        if (problems.Count > 0) throw new InvalidDataException(string.Join("\n", problems));
        var files = Files();
        var expected = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var relative in files.Keys)
        {
            var path = ScenarioLibrary.SafePath(modRoot, relative);
            expected[relative] = File.Exists(path) ? File.ReadAllText(path) : null;
        }
        ContentWorkbench.WriteBatch(modRoot, files, expected);
        // A region change that was taken back must not linger as a stale table from last time.
        var stale = ScenarioLibrary.SafePath(modRoot, $"history/provinces/{FileStem}.csv");
        if (Ownership.Count == 0 && File.Exists(stale)) File.Move(stale, stale + ".bak", true);
        return files.Keys.Select(relative => ScenarioLibrary.SafePath(modRoot, relative)).ToList();
    }

    /// <summary>
    /// Read back what <see cref="Write"/> wrote for a tag, or null when the mod holds nothing for
    /// it. Reads only this editor's own files: the country's values elsewhere in the mod are the
    /// mod's business and are layered by the game as usual.
    /// </summary>
    public static CountryEditorDocument Read(string modRoot, string tag)
    {
        var document = new CountryEditorDocument { Tag = tag.ToUpperInvariant() };
        var stem = document.FileStem;
        var found = false;
        JsonElement? Entry(string relative, out JsonDocument owner)
        {
            owner = null;
            var path = Path.Combine(modRoot, relative);
            if (!File.Exists(path)) return null;
            owner = JsonDocument.Parse(StructuredData.ReadJson(path), GameDataLoader.ForgivingJson);
            return owner.RootElement.TryGetProperty(document.Tag, out var value) && value.ValueKind == JsonValueKind.Object ? value : null;
        }
        string Text(JsonElement value, string key) =>
            value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.String ? field.GetString() ?? "" : "";
        float? Number(JsonElement value, string key) =>
            value.TryGetProperty(key, out var field) && field.ValueKind == JsonValueKind.Number && field.TryGetSingle(out var number) ? number : null;

        if (Entry(Path.Combine("common", "countries", stem + ".json"), out var common) is { } definition)
        {
            found = true;
            document.Colour = Text(definition, "color");
            document.CapitalName = Text(definition, "capital_name");
            document.CapitalLatitude = Number(definition, "capital_latitude");
            document.CapitalLongitude = Number(definition, "capital_longitude");
        }
        common?.Dispose();
        if (Entry(Path.Combine("history", "countries", stem + ".json"), out var history) is { } had)
        {
            found = true;
            document.Government = Text(had, "government");
            document.Ideology = Text(had, "ideology");
            document.Treasury = Number(had, "treasury");
            document.HeadOfState = Text(had, "head_of_state");
            document.HeadOfGovernment = Text(had, "head_of_government");
            if (had.TryGetProperty("primary_cultures", out var cultures) && cultures.ValueKind == JsonValueKind.Array)
                document.PrimaryCultures = cultures.EnumerateArray().Where(c => c.ValueKind == JsonValueKind.String)
                    .Select(c => c.GetString()).ToList();
        }
        history?.Dispose();
        if (Entry(Path.Combine("history", "rulers", stem + ".json"), out var rulers) is { } people)
        {
            found = true; document.HasPortraitSettings = true;
            document.HeadOfStatePortrait = Text(people, "state_portrait");
            document.HeadOfGovernmentPortrait = Text(people, "gov_portrait");
            if (Text(people, "head_of_state").Length > 0) document.HeadOfState = Text(people, "head_of_state");
            if (Text(people, "head_of_government").Length > 0) document.HeadOfGovernment = Text(people, "head_of_government");
        }
        rulers?.Dispose();
        var localization = Path.Combine(modRoot, "localization");
        if (Directory.Exists(localization))
            foreach (var folder in Directory.EnumerateDirectories(localization))
            {
                var file = Path.Combine(folder, stem + ".json");
                if (!File.Exists(file)) continue;
                using var table = JsonDocument.Parse(StructuredData.ReadJson(file), GameDataLoader.ForgivingJson);
                document.Names[Path.GetFileName(folder)] = (Text(table.RootElement, $"country.{document.Tag}.name"),
                    Text(table.RootElement, $"country.{document.Tag}.adjective"));
                found = true;
            }
        var provinces = Path.Combine(modRoot, "history", "provinces", stem + ".csv");
        if (File.Exists(provinces))
        {
            found = true;
            foreach (var raw in File.ReadAllLines(provinces))
            {
                var row = raw.Trim();
                if (row.Length == 0 || row[0] == '#') continue;
                var parts = row.Split(';');
                if (parts.Length >= 2 && int.TryParse(parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var province))
                    document.Ownership.Add((province, parts[1].Trim()));
            }
        }
        return found ? document : null;
    }

    private string Json(string comment, Action<Utf8JsonWriter> body)
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions
               { Indented = true, Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName(Tag);
            writer.WriteStartObject();
            body(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return "// " + comment + "\n" + Encoding.UTF8.GetString(buffer.ToArray()) + "\n";
    }
}
