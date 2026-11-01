using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using DealOrForce.Simulation;

/// <summary>
/// Proof that a YAML file and its JSON twin are the same data to the game.
///
/// StructuredDataTests checks the converter against hand-written snippets: what a boolean is,
/// which tags are refused. That says YAML is read correctly; it does not say a modder can take
/// any file the game ships, write it out as YAML and get the same game. These checks do.
///
/// Three things, in order of how much a modder would mind if they were false:
///
/// 1. The worked example in mods/_templates/yaml_example and a JSON file saying the same thing
///    give the opinion catalogue the same reason - through OpinionCatalog.Merge, which is the
///    exact call ModLoader makes for a mod's common/opinion_modifiers, after the same
///    StructuredData.ReadJson. Not "the documents look alike": the definition the simulation
///    keeps is equal, field by field, and the base game is untouched afterwards.
///
/// 2. Every JSON document under game/ survives being rewritten as block-style YAML. The writer
///    below is deliberately the way a person writes YAML - indented mappings, `- ` lists, bare
///    words where they are safe and quotes only where they are needed - so the round trip goes
///    through the plain-scalar rules a modder actually leans on, over every schema the game
///    has, rather than through the JSON-in-YAML shortcut that would prove nothing.
///
/// 3. Load order is the file name, not the format, including the tie a modder could create by
///    shipping `x.json` and `x.yaml` side by side; and a broken YAML file is reported with its
///    file name and the line of the mistake, because "parse error" with no place is a support
///    ticket.
/// </summary>
static class YamlTwinTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS YAML twin: " + label); }

    public static void Run()
    {
        var root = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), ".."));
        TemplateTwin(root);
        WholeTreeTwin(Path.Combine(root, "game"));
        OrderAndErrors();
    }

    /// <summary>The worked example, against a JSON file that says the same thing.</summary>
    static void TemplateTwin(string root)
    {
        var template = Path.Combine(root, "mods", "_templates", "yaml_example");
        var yamlPath = Path.Combine(template, "common", "opinion_modifiers", "50_longer_grudges.yaml");
        Check(File.Exists(yamlPath), "the yaml_example template ships its YAML file");

        using (var manifest = JsonDocument.Parse(File.ReadAllText(Path.Combine(template, "manifest.json"))))
            Check(manifest.RootElement.GetProperty("enabled").ValueKind == JsonValueKind.False,
                "the template is disabled, so it cannot change a normal campaign");

        var directory = Path.Combine(Path.GetTempPath(), "dof-yaml-twin-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            // Written the way somebody would write the JSON by hand, comment and all, so the
            // JSON side goes through the same forgiving reader the shipped data does.
            var jsonPath = Path.Combine(directory, "50_longer_grudges.json");
            File.WriteAllText(jsonPath, """
                // The JSON twin of mods/_templates/yaml_example.
                {
                  "denounced": { "opinion": -18, "days": 1825, },
                }
                """);

            using var fromYaml = StructuredData.ReadDocument(yamlPath);
            using var fromJson = StructuredData.ReadDocument(jsonPath);
            // Re-serialised rather than GetRawText, which would hand back the JSON twin's
            // trailing commas to a strict parser.
            Check(JsonNode.DeepEquals(JsonNode.Parse(JsonSerializer.Serialize(fromYaml.RootElement)),
                                      JsonNode.Parse(JsonSerializer.Serialize(fromJson.RootElement))),
                "the YAML template and its JSON twin are the same document");

            OpinionCatalog.ResetCatalog();
            var shipped = OpinionCatalog.Find("denounced");
            Check(shipped is not null && shipped.Value.Days == 1100,
                "base game: a denunciation is remembered for 1100 days");

            var errors = new List<string>();
            OpinionCatalog.Merge(fromYaml.RootElement, errors);
            var viaYaml = OpinionCatalog.Find("denounced").Value;
            OpinionCatalog.ResetCatalog();
            OpinionCatalog.Merge(fromJson.RootElement, errors);
            var viaJson = OpinionCatalog.Find("denounced").Value;
            Check(errors.Count == 0, "neither twin is refused by the opinion catalogue: " + string.Join(" | ", errors));
            Check(viaYaml == viaJson, $"both twins leave the same reason in the catalogue: {viaYaml}");
            Check(viaYaml.Days == 1825 && viaYaml.Opinion == -18f, "the override changes the lifetime and keeps the size");
            Check(viaYaml.Name == shipped.Value.Name && viaYaml.Decays == shipped.Value.Decays &&
                  viaYaml.MaxStacks == shipped.Value.MaxStacks,
                "fields the override leaves out are inherited from the base game");
        }
        finally
        {
            Directory.Delete(directory, true);
            OpinionCatalog.ResetCatalog();
        }
        Check(OpinionCatalog.Find("denounced")?.Days == 1100, "reloading the base game restores 1100 days");
    }

    /// <summary>
    /// Every JSON document the game ships, rewritten as YAML and read back.
    ///
    /// Files are compared as parsed trees, so formatting and comments do not count and every
    /// key, value, type and order does. A file the forgiving JSON reader itself refuses is not
    /// this test's business and is skipped; a duplicate key is counted separately, because YAML
    /// refuses it on purpose and a modder converting that file would meet the refusal.
    /// </summary>
    static void WholeTreeTwin(string game)
    {
        var watch = Stopwatch.StartNew();
        // The YAML reader on its own, apart from writing the twins and comparing trees, because
        // that is the part a player waits for when a mod ships YAML.
        var converting = new Stopwatch();
        var files = Directory.EnumerateFiles(game, "*.json", SearchOption.AllDirectories)
            .OrderBy(path => path, StringComparer.Ordinal).ToArray();
        long bytes = 0, yamlBytes = 0;
        var compared = 0;
        var unreadable = 0;
        var mismatches = new List<string>();
        var duplicated = new List<string>();
        foreach (var file in files)
        {
            JsonNode original;
            try
            {
                var text = File.ReadAllText(file);
                using (var document = JsonDocument.Parse(text, GameDataLoader.ForgivingJson))
                    if (DuplicateKey(document.RootElement, "$") is { } where)
                    {
                        duplicated.Add(Path.GetRelativePath(game, file) + " at " + where);
                        continue;
                    }
                original = JsonNode.Parse(text, documentOptions: GameDataLoader.ForgivingJson);
            }
            catch (JsonException) { unreadable++; continue; }
            var yaml = ToYaml(original);
            bytes += new FileInfo(file).Length;
            yamlBytes += Encoding.UTF8.GetByteCount(yaml);
            try
            {
                converting.Start();
                var converted = StructuredData.YamlToJson(yaml);
                converting.Stop();
                var back = JsonNode.Parse(converted);
                if (!JsonNode.DeepEquals(original, back))
                    mismatches.Add(Path.GetRelativePath(game, file) + ": the YAML twin reads back differently");
            }
            catch (Exception exception)
            {
                mismatches.Add(Path.GetRelativePath(game, file) + ": " + exception.Message);
            }
            compared++;
        }
        foreach (var mismatch in mismatches.Take(10)) Console.WriteLine("  twin mismatch: " + mismatch);
        // Not a failure of the YAML path - YAML refuses a repeated key on purpose - but a file a
        // modder cannot convert as it stands, and a JSON file whose earlier value is dead.
        foreach (var duplicate in duplicated) Console.WriteLine("  note: repeated JSON key, no YAML twin possible: " + duplicate);
        Check(compared > 1000 && mismatches.Count == 0,
            $"all {compared} JSON documents under game/ ({bytes / 1024} KB) read back identically from " +
            $"block-style YAML twins ({yamlBytes / 1024} KB); YAML reading {converting.ElapsedMilliseconds} ms " +
            $"of {watch.ElapsedMilliseconds} ms in all " +
            $"({duplicated.Count} with a repeated key and {unreadable} unreadable as JSON skipped)");
    }

    /// <summary>The first object path at which a key repeats, or null.</summary>
    static string DuplicateKey(JsonElement element, string path)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var property in element.EnumerateObject())
            {
                if (!seen.Add(property.Name)) return path + "." + property.Name;
                if (DuplicateKey(property.Value, path + "." + property.Name) is { } inner) return inner;
            }
        }
        else if (element.ValueKind == JsonValueKind.Array)
        {
            var index = 0;
            foreach (var item in element.EnumerateArray())
                if (DuplicateKey(item, $"{path}[{index++}]") is { } inner) return inner;
        }
        return null;
    }

    /// <summary>Lexical order across formats, the same-name tie, and where a mistake is.</summary>
    static void OrderAndErrors()
    {
        var directory = Path.Combine(Path.GetTempPath(), "dof-yaml-order-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            foreach (var name in new[] { "50_x.yml", "50_x.yaml", "50_x.json", "10_a.yaml", "90_z.json" })
                File.WriteAllText(Path.Combine(directory, name), name.EndsWith(".json") ? "{}" : "a: 1");
            var order = StructuredData.Files(directory).Select(Path.GetFileName).ToArray();
            Check(order.SequenceEqual(new[] { "10_a.yaml", "50_x.json", "50_x.yaml", "50_x.yml", "90_z.json" }),
                "load order is the file name; a same-named .json loads before .yaml before .yml: " + string.Join(", ", order));

            var broken = Path.Combine(directory, "60_broken.yaml");
            File.WriteAllText(broken, "denounced:\n  opinion: -18\n  days: [1825\n");
            var message = "";
            try { StructuredData.ReadDocument(broken); }
            catch (InvalidDataException exception) { message = exception.Message; }
            Check(message.Contains("60_broken.yaml") && Regex.IsMatch(message, @"Line: [34]\b"),
                "a syntax error names the file and the line: " + message);

            File.WriteAllText(broken, "denounced:\n  opinion: -18\n  opinion: -20\n");
            message = "";
            try { StructuredData.ReadDocument(broken); }
            catch (InvalidDataException exception) { message = exception.Message; }
            Check(message.Contains("60_broken.yaml") && message.Contains("'opinion'") && message.Contains("Line: 3"),
                "a duplicate key names the file, the key and the line: " + message);
        }
        finally { Directory.Delete(directory, true); }
    }

    // ---- A deliberately human-style JSON-to-YAML writer -------------------------------------

    /// <summary>
    /// A word that needs no quotes in YAML and that the game's reader will keep as text: it
    /// starts with a letter or underscore, holds no punctuation YAML gives meaning to, and is
    /// not one of the words the reader turns into a boolean or null.
    /// </summary>
    static readonly Regex PlainWord = new(@"^[A-Za-z_](?:[A-Za-z0-9_.\- ]*[A-Za-z0-9_.])?$", RegexOptions.CultureInvariant);

    static bool IsPlain(string text) =>
        PlainWord.IsMatch(text) && !text.Contains("  ") &&
        !(text.Equals("true", StringComparison.OrdinalIgnoreCase) ||
          text.Equals("false", StringComparison.OrdinalIgnoreCase) ||
          text.Equals("null", StringComparison.OrdinalIgnoreCase));

    static string ToYaml(JsonNode node)
    {
        var builder = new StringBuilder();
        if (node is JsonObject { Count: > 0 } or JsonArray { Count: > 0 }) Block(builder, node, 0);
        else builder.Append(Inline(node)).Append('\n');
        return builder.ToString();
    }

    static void Block(StringBuilder builder, JsonNode node, int indent)
    {
        var pad = new string(' ', indent);
        if (node is JsonObject map)
            foreach (var (key, value) in map)
            {
                builder.Append(pad).Append(IsPlain(key) ? key : Quote(key)).Append(':');
                Value(builder, value, indent);
            }
        else
            foreach (var item in (JsonArray)node)
            {
                builder.Append(pad).Append('-');
                Value(builder, item, indent);
            }
    }

    static void Value(StringBuilder builder, JsonNode value, int indent)
    {
        if (value is JsonObject { Count: > 0 } or JsonArray { Count: > 0 })
        {
            builder.Append('\n');
            Block(builder, value, indent + 2);
        }
        else builder.Append(' ').Append(Inline(value)).Append('\n');
    }

    static string Inline(JsonNode node) => node switch
    {
        null => "null",
        JsonObject => "{}",
        JsonArray => "[]",
        _ => node.GetValueKind() switch
        {
            JsonValueKind.String => node.GetValue<string>() is var text && IsPlain(text) ? text : Quote(text),
            JsonValueKind.True => "true",
            JsonValueKind.False => "false",
            // The JSON number text exactly as written; the reader passes it through unchanged.
            _ => node.ToJsonString()
        }
    };

    /// <summary>
    /// A YAML double-quoted string. Only the backslash, the quote and characters YAML may not
    /// carry raw are escaped; everything else - Cyrillic, emoji - is written as itself, as a
    /// person would.
    /// </summary>
    static string Quote(string text)
    {
        var builder = new StringBuilder(text.Length + 2).Append('"');
        foreach (var c in text)
            builder.Append(c switch
            {
                '"' => "\\\"",
                '\\' => "\\\\",
                '\n' => "\\n",
                '\t' => "\\t",
                '\r' => "\\r",
                _ when char.IsControl(c) || (int)c is 0x2028 or 0x2029 or 0xFEFF or 0xFFFE or 0xFFFF
                    => "\\u" + ((int)c).ToString("X4", CultureInfo.InvariantCulture),
                _ => c.ToString()
            });
        return builder.Append('"').ToString();
    }
}
