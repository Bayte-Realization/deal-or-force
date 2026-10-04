using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

static class StructuredDataTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception(label); Console.WriteLine("PASS YAML: " + label); }

    static void Reject(string yaml, string label)
    {
        var rejected = false;
        try { StructuredData.YamlToJson(yaml); }
        catch (Exception e) when (e is InvalidDataException or YamlDotNet.Core.YamlException) { rejected = true; }
        Check(rejected, label);
    }

    public static void Run()
    {
        const string yaml = """
            # Names and schemas are unchanged from JSON.
            units_per_factory: 12
            conversion:
              efficiency: 0.5
            lines:
              Steel: {pool: military}
            tags: [NO, on, yes, 001, "true", 'null', 2024-01-01]
            flags: {aviation: false, navy: true}
            nothing: null
            empty:
            text: |-
              Первая строка
              Вторая строка
            folded: >-
              first
              second
            color: "#e7bd69"
            small: -1.25e-3
            "123": a province key
            """;
        using var doc = JsonDocument.Parse(StructuredData.YamlToJson(yaml));
        var root = doc.RootElement;
        Check(root.GetProperty("units_per_factory").GetInt32() == 12 &&
              root.GetProperty("conversion").GetProperty("efficiency").GetSingle() == .5f,
            "block mappings and numeric types match JSON");
        Check(root.GetProperty("tags").EnumerateArray().All(v => v.ValueKind == JsonValueKind.String),
            "quoted scalars, NO/on/yes, leading-zero IDs and dates remain strings");
        Check(!root.GetProperty("flags").GetProperty("aviation").GetBoolean() &&
              root.GetProperty("flags").GetProperty("navy").GetBoolean(), "true/false become booleans");
        Check(root.GetProperty("nothing").ValueKind == JsonValueKind.Null &&
              root.GetProperty("empty").ValueKind == JsonValueKind.Null, "null and empty values are null");
        Check(root.GetProperty("text").GetString() == "Первая строка\nВторая строка" &&
              root.GetProperty("folded").GetString() == "first second", "literal/folded Unicode text preserves YAML semantics");
        Check(root.GetProperty("color").GetString() == "#e7bd69" && root.GetProperty("small").GetDouble() == -.00125 &&
              root.GetProperty("123").GetString() == "a province key", "quoted colors, exponents and numeric-looking keys work");
        Check(JsonNode.DeepEquals(JsonNode.Parse(StructuredData.YamlToJson("a: [1, false, null, \"x\"]")),
              JsonNode.Parse("{\"a\":[1,false,null,\"x\"]}")), "identical YAML and JSON yield identical data");
        using (var tags = JsonDocument.Parse(StructuredData.YamlToJson("a: !!str true\nb: !!bool false\nc: !!int 12\nd: !!float 0.5\ne: !!null null")))
            Check(tags.RootElement.GetProperty("a").GetString() == "true" &&
                  tags.RootElement.GetProperty("c").GetInt32() == 12, "safe explicit scalar tags work without object deserialization");
        Reject("a: 1\na: 2", "duplicate keys rejected instead of silently changing balance");
        Reject("a: [1, 2", "syntax errors rejected");
        Reject("a: !System.Type x", "custom object tags rejected");
        Reject("a: &base [1]\nb: *base", "anchors and aliases rejected without expansion");
        Reject("a: *unknown", "undefined aliases rejected");
        Reject("<<: {a: 1}", "merge keys rejected rather than ignored");
        Reject("? [a, b]\n: 1", "complex mapping keys rejected");
        Reject("a: 1\n---\na: 2", "multiple documents rejected");
        Reject("a: !!float .nan", "explicit non-finite numbers rejected");
        Reject("a: !!bool yes", "legacy boolean spellings cannot silently change flags");
        Reject(new string('[', 65) + "0" + new string(']', 65), "nesting limit enforced");
        Reject(new string(' ', StructuredData.MaxYamlBytes + 1), "input size limit enforced");

        var directory = Path.Combine(Path.GetTempPath(), "dof-yaml-" + Guid.NewGuid().ToString("N"));
        var language = Loc.Language;
        Directory.CreateDirectory(directory);
        try
        {
            var gui = Path.Combine(directory, "gui"); Directory.CreateDirectory(gui);
            File.WriteAllText(Path.Combine(gui, "00_base.json"), "{\"metrics\":{\"yaml.test\":17}}");
            File.WriteAllText(Path.Combine(gui, "20_override.yaml"), "metrics:\n  yaml.test: 29");
            File.WriteAllText(Path.Combine(gui, "30_override.YML"), "metrics: {yaml.test: 31}");
            File.WriteAllText(Path.Combine(gui, "40_broken.yaml"), "metrics: [");
            File.WriteAllText(Path.Combine(gui, "ignored.yaml.bak"), "bad");
            File.WriteAllText(Path.Combine(gui, "ignored.txt"), "bad");
            var found = StructuredData.Files(gui).Select(Path.GetFileName).ToArray();
            Check(found.SequenceEqual(new[] { "00_base.json", "20_override.yaml", "30_override.YML", "40_broken.yaml" }),
                "formats share lexical load order; uppercase YML works; backups are ignored");
            var loc = Path.Combine(directory, "localization", "english"); Directory.CreateDirectory(loc);
            File.WriteAllText(Path.Combine(loc, "10_text.yml"), "yaml.test: YAML translation");
            Loc.Language = "english";
            var errors = PresentationReload.Reload(new[] { directory });
            Check(InterfaceSkin.Metric("yaml.test", 0) == 31 && Loc.Get("yaml.test") == "YAML translation",
                "real presentation reload layers JSON/YAML/YML and localisation");
            Check(errors.Count == 1 && errors[0].Contains("40_broken.yaml"),
                "bad YAML names its file and does not suppress valid siblings");
            File.Delete(Path.Combine(gui, "40_broken.yaml"));
            File.WriteAllText(Path.Combine(gui, "30_override.YML"), "metrics: {yaml.test: 37}");
            errors = PresentationReload.Reload(new[] { directory });
            Check(errors.Count == 0 && InterfaceSkin.Metric("yaml.test", 0) == 37, "hot reload rereads edited YAML");
            File.Delete(Path.Combine(gui, "30_override.YML")); File.Delete(Path.Combine(gui, "20_override.yaml"));
            PresentationReload.Reload(new[] { directory });
            Check(InterfaceSkin.Metric("yaml.test", 0) == 17, "removed YAML falls back to earlier JSON");
            var gfx = Path.Combine(directory, "gfx"); Directory.CreateDirectory(gfx);
            File.WriteAllText(Path.Combine(gfx, "icons.yaml"), "sprites:\n  yaml_test: {texture: icons/test.png, source_colour: '#e7bd69'}");
            var domainErrors = new List<string>(); SpriteCatalog.LoadTree(gfx, "res://test/gfx", domainErrors);
            Check(domainErrors.Count == 0 && SpriteCatalog.Get("yaml_test").SourceColour == "#e7bd69",
                "sprite YAML includes gold PNG source-colour metadata");
            using var programme = JsonDocument.Parse(StructuredData.YamlToJson("units_per_factory: 12\nconversion: {efficiency: 0.5}\nlines: {Steel: {pool: military}}"));
            ProductionProgramme.Merge(programme.RootElement, domainErrors);
            CommodityCatalog.TryParse("Steel", out var steel);
            Check(domainErrors.Count == 0 && ProductionProgramme.UnitsPerFactory == 12 &&
                  ProductionProgramme.ConversionEfficiency == .5f && ProductionProgramme.PoolOf(steel) == ProductionPool.Military,
                "production programme uses unchanged domain parser");
        }
        finally
        {
            Directory.Delete(directory, true);
            InterfaceSkin.ResetCatalog(); Loc.ResetCatalog(); Loc.Language = language;
            SpriteCatalog.Load(); ProductionProgramme.ResetCatalog();
        }
    }
}
