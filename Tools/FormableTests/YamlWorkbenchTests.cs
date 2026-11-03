using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class YamlWorkbenchTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception(message); Console.WriteLine("PASS YAML workbench: " + message); }

    static void Reject(Action action, string message)
    {
        var rejected = false;
        try { action(); } catch (InvalidDataException) { rejected = true; }
        Check(rejected, message);
    }

    public static void Run()
    {
        const string json = """
            {"id":"001","country":"NO","flag":"#e7bd69","date":"2024-01-01",
             "text":"Строка\n\"quoted\"\\path","large":18446744073709551615,"negative":-9223372036854775808,
             "precise":0.123456789123456789,"tiny":1e-50,"zero":-0.0,"nothing":null,"active":true,
             "objects":[{},[],{"a/b":[false,"on","true"]}],"<<":"ordinary quoted key"}
            """;
        var yaml = StructuredData.JsonToYaml(json);
        Check(JsonNode.DeepEquals(JsonNode.Parse(json), JsonNode.Parse(StructuredData.YamlToJson(yaml))),
            "writer roundtrips nested data, Unicode, ambiguous strings and exact numeric tokens");
        using (var document = StructuredData.ParseDocument("roundtrip.YML", yaml))
            Check(document.RootElement.GetProperty("large").GetUInt64() == ulong.MaxValue &&
                document.RootElement.GetProperty("negative").GetInt64() == long.MinValue &&
                document.RootElement.GetProperty("precise").GetRawText() == "0.123456789123456789",
                "integers and precise decimals never pass through floating point");
        foreach (var value in new[] { "", "{}", "#comment", "---", "\t\r\n\0\u0085\u2028\u2029", "😀", "line: value", "a'b\"c", "  trailing  " })
        {
            var original = JsonSerializer.Serialize(new Dictionary<string, string> { [value] = value });
            Check(JsonNode.DeepEquals(JsonNode.Parse(original), JsonNode.Parse(StructuredData.YamlToJson(StructuredData.JsonToYaml(original)))),
                "string and key escaping survives YAML normalisation: " + JsonSerializer.Serialize(value));
        }
        Check(StructuredData.YamlToJson(StructuredData.JsonToYaml("{}")) == "{}" &&
            StructuredData.YamlToJson(StructuredData.JsonToYaml("[]")) == "[]", "empty root containers remain containers");
        var templateRoot = Path.GetFullPath(Path.Combine(GameDataLoader.DataFolder(), "..", "mods", "_templates", "yaml-data"));
        var eventText = StructuredData.ReadJson(Path.Combine(templateRoot, "events", "70_workshop.yml"));
        var eventErrors = new List<string>();
        try
        {
            EventCatalog.Load(eventText, eventErrors);
            var example = EventCatalog.All.Single(e => e.Id == "yaml_workshop_bottleneck");
            var campaign = new Campaign(531) { ScenarioId = "yaml_effect_fixture" };
            var country = new Nation { Id = 0, Tag = "YML", Treasury = 100, ConstructionCapacity = 1 };
            campaign.AddNation(country);
            example.Options[0].Effect.Apply(campaign, country);
            Check(eventErrors.Count == 0 && Math.Abs(country.Treasury - 99) < .0001 && country.ConstructionCapacity > 1,
                "authored YAML event loads and its choice really spends treasury and adds capacity");
            foreach (var language in new[] { "english", "russian" })
            {
                using var strings = StructuredData.ReadDocument(Path.Combine(templateRoot, "localization", language, "70_workshop.yml"));
                Check(strings.RootElement.TryGetProperty(example.Title, out _) && strings.RootElement.TryGetProperty(example.Description, out _) &&
                    example.Options.All(option => strings.RootElement.TryGetProperty(option.Title, out _) && strings.RootElement.TryGetProperty(option.Consequence, out _)),
                    "example event ships every visible caption in " + language);
            }
        }
        finally { EventCatalog.ResetCatalog(); }
        Check(ContentWorkbench.Roots.Contains("events") && ContentWorkbench.IsEditable("events/50_example.YAML") &&
            ContentWorkbench.IsEditable("gui/test.YML"), "events and all supported extensions are discoverable editor content");
        foreach (var path in new[] { "manifest.yaml", "scenarios/test/scenario.yaml", "scenarios/test/world.yml", "common/../escape.yaml", "gui/test.yaml.bak", "gui/test.txt" })
            Check(!ContentWorkbench.IsEditable(path), "editor does not promise an unsupported or unsafe path: " + path);

        // YAML authoring and the province table editor must coexist after integration:
        // a table is editable raw source, not a JSON/YAML document to normalise.
        const string ownership = "province;owner;controller\n1;RUS;UKR\n2;UKR\n";
        Check(ContentWorkbench.IsEditable("history/provinces/2024.csv"), "province ownership tables remain discoverable alongside YAML");
        ContentWorkbench.Validate("history/provinces/2024.csv", ownership);
        Reject(() => ContentWorkbench.Validate("history/provinces/2024.csv", "not-a-province;RUS;UKR"),
            "ownership tables still reject malformed rows");
        Check(!ContentWorkbench.IsEditable("gui/test.csv") && !ContentWorkbench.IsEditable("history/../escape.csv"),
            "CSV support does not widen the editor beyond safe ownership tables");

        const string draft = "# keep this comment\n\"a/b\": [1, 2]\nother: \"001\"\n";
        var changed = ContentWorkbench.SetValue(draft, "/a~1b/0", "42", "common/test.YML");
        changed = ContentWorkbench.AddValue(changed, "/a~1b", "", "true", "common/test.YML");
        changed = ContentWorkbench.RemoveValue(changed, "/a~1b/1", "common/test.YML");
        using (var result = StructuredData.ParseDocument("common/test.YML", changed))
        {
            var array = result.RootElement.GetProperty("a/b");
            Check(array.GetArrayLength() == 2 && array[0].GetInt32() == 42 && array[1].GetBoolean() &&
                result.RootElement.GetProperty("other").GetString() == "001", "property edits support YAML, escaped pointers and arrays");
        }
        Check(!changed.Contains("# keep") && changed.Contains("\"a/b\":"), "structured edits explicitly normalise YAML rather than disguising JSON as YAML");
        Reject(() => ContentWorkbench.Validate("gui/broken.yaml", "metrics: ["), "syntax validation rejects broken YAML");
        try { ContentWorkbench.Validate("gui/broken.yaml", "metrics: ["); }
        catch (InvalidDataException error) { Check(error.Message.Contains("gui/broken.yaml"), "draft diagnostic names the actual source file"); }
        Reject(() => ContentWorkbench.Validate("common/programme/broken.yaml", "units_per_factory: -2"), "YAML reaches the same domain validator as JSON");
        Reject(() => ContentWorkbench.Validate("localization/english/test.yml", "42"), "scalar-only content is not saved as a catalogue");

        var directory = Path.Combine(Path.GetTempPath(), "dof-yaml-workbench-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory);
        try
        {
            var ownershipPath = ContentWorkbench.Write(directory, "history/provinces/2024.csv", ownership, null);
            Check(File.ReadAllText(ownershipPath) == ownership, "transactional writer preserves province CSV verbatim");
            var relative = "gui/50_editor.yaml";
            var path = ContentWorkbench.Write(directory, relative, draft, null);
            Check(File.ReadAllText(path) == draft, "raw source save preserves comments and formatting");
            ContentWorkbench.Write(directory, relative, changed, draft);
            Check(File.ReadAllText(path + ".bak") == draft && File.ReadAllText(path) == changed,
                "YAML replacement keeps one complete recovery version");
            Reject(() => ContentWorkbench.Write(directory, relative, "{}", draft), "stale editor draft cannot overwrite an external edit");
            Reject(() => ContentWorkbench.Write(directory, relative, "{}", null), "new-document collision is not silently overwritten");
            Check(File.ReadAllText(path) == changed && File.ReadAllText(path + ".bak") == draft, "refused writes preserve file and backup");
            var text = "yaml.workbench: Из редактора\n";
            ContentWorkbench.WriteBatch(directory,
                new Dictionary<string, string> { [relative] = draft, ["localization/russian/50_editor.yml"] = text, ["gui/60_extra.json"] = "{}" },
                new Dictionary<string, string> { [relative] = changed, ["localization/russian/50_editor.yml"] = null, ["gui/60_extra.json"] = null });
            using (var extra = StructuredData.ReadDocument(Path.Combine(directory, "gui", "60_extra.json")))
                Check(File.ReadAllText(path) == draft && extra.RootElement.ValueKind == JsonValueKind.Object,
                    "one atomic authoring batch accepts mixed JSON, YAML and YML");
            Reject(() => ContentWorkbench.WriteBatch(directory,
                new Dictionary<string, string> { [relative] = changed, ["gui/70_bad.yaml"] = "a: [" },
                new Dictionary<string, string> { [relative] = draft, ["gui/70_bad.yaml"] = null }), "bad sibling prevents the complete mixed batch from committing");
            Check(File.ReadAllText(path) == draft && !File.Exists(Path.Combine(directory, "gui", "70_bad.yaml")) &&
                !Directory.EnumerateFiles(directory, "*.tmp", SearchOption.AllDirectories).Any(), "failed batch leaves no changed file or staging debris");
            Reject(() => ContentWorkbench.Write(directory, "gui/../../escape.yaml", "{}"), "YAML writes cannot leave the mod root");
            File.SetAttributes(path + ".bak", FileAttributes.ReadOnly);
            Reject(() => ContentWorkbench.Write(directory, relative, changed, draft), "single-file writer protects read-only backups too");
            File.SetAttributes(path + ".bak", FileAttributes.Normal);
        }
        finally
        {
            // This invocation owns precisely this random temporary directory.
            foreach (var file in Directory.EnumerateFiles(directory, "*", SearchOption.AllDirectories)) File.SetAttributes(file, FileAttributes.Normal);
            Directory.Delete(directory, true);
        }
    }
}
