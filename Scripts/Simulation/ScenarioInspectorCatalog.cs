using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Presentation of numeric scenario fields. Mods can reorder, hide and relabel these controls.</summary>
public static class ScenarioInspectorCatalog
{
    public sealed record Field(string Key, string Label, double Min, double Max, double Step, double Scale, int Order);
    private static readonly Dictionary<string, Field> Country = new(), Region = new();
    public static IEnumerable<Field> Fields(bool country) => (country ? Country : Region).Values.OrderBy(f => f.Order).ThenBy(f => f.Key);
    static ScenarioInspectorCatalog() => ResetCatalog();
    public static void ResetCatalog()
    {
        Country.Clear(); Region.Clear();
        foreach (var doc in GameDataLoader.DataDocuments("common", "editor")) using (doc) Merge(doc.RootElement);
    }
    public static void Merge(JsonElement root, List<string> errors = null) => MergeInto(root, Country, Region, errors);
    public static void Validate(JsonElement root, List<string> errors) => MergeInto(root, new(Country), new(Region), errors);
    private static void MergeInto(JsonElement root, Dictionary<string, Field> countries, Dictionary<string, Field> regions, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object) { errors?.Add("editor: expected an object."); return; }
        foreach (var (category, table, type) in new[] { ("country", countries, typeof(Nation)), ("region", regions, typeof(ScenarioState)) })
        {
            if (!root.TryGetProperty(category, out var fields)) continue;
            if (fields.ValueKind != JsonValueKind.Object) { errors?.Add("editor/" + category + ": expected an object."); continue; }
            foreach (var entry in fields.EnumerateObject())
            {
                try
                {
                    var property = type.GetField(entry.Name, BindingFlags.Public | BindingFlags.Instance);
                    if (property is null || property.FieldType != typeof(float) && property.FieldType != typeof(long) ||
                        entry.Value.ValueKind != JsonValueKind.Object)
                        throw new InvalidDataException("Use an existing float or population field.");
                    var row = entry.Value;
                    if (row.TryGetProperty("hidden", out var hidden))
                    {
                        if (hidden.ValueKind is not (JsonValueKind.True or JsonValueKind.False)) throw new InvalidDataException("hidden must be true or false.");
                        if (hidden.ValueKind == JsonValueKind.True) { table.Remove(entry.Name); continue; }
                    }
                    var old = table.GetValueOrDefault(entry.Name);
                    double Number(string key, double fallback) => row.TryGetProperty(key, out var v) ? v.GetDouble() : fallback;
                    var field = new Field(entry.Name, row.TryGetProperty("label", out var label) ? label.GetString() : old?.Label ?? entry.Name,
                        Number("min", old?.Min ?? 0), Number("max", old?.Max ?? 1e15), Number("step", old?.Step ?? 1),
                        Number("scale", old?.Scale ?? 1), row.TryGetProperty("order", out var order) ? order.GetInt32() : old?.Order ?? table.Count);
                    if (string.IsNullOrWhiteSpace(field.Label) || !double.IsFinite(field.Min) || !double.IsFinite(field.Max) ||
                        !double.IsFinite(field.Step) || !double.IsFinite(field.Scale) || field.Min > field.Max || field.Step <= 0 || field.Scale <= 0)
                        throw new InvalidDataException("Invalid numeric control limits, scale or label.");
                    table[entry.Name] = field;
                }
                catch (Exception e) { errors?.Add($"editor/{category}/{entry.Name}: {e.Message}"); }
            }
        }
    }
}

/// <summary>Shared inspector validation, independent of Godot. Parsing never modifies the input buffer.</summary>
public static class ScenarioEntityDocument
{
    public static JsonObject Parse(string json, bool country)
    {
        var entity = JsonNode.Parse(json, documentOptions: GameDataLoader.ForgivingJson) as JsonObject
            ?? throw new InvalidDataException("Entity data must be a JSON object.");
        if (string.IsNullOrWhiteSpace(entity["Name"]?.GetValue<string>())) throw new InvalidDataException("Name cannot be empty.");
        if (country)
        {
            var colour = entity["Color"]?.GetValue<string>() ?? "";
            if (colour.Length != 7 || colour[0] != '#' || !uint.TryParse(colour.AsSpan(1), System.Globalization.NumberStyles.HexNumber, null, out _))
                throw new InvalidDataException("Country colour must be #RRGGBB.");
            if (!GovernmentCatalog.All.Any(g => g.Id == entity["Government"]?.GetValue<string>()) ||
                !IdeologyCatalog.All.Any(g => g.Id == entity["Ideology"]?.GetValue<string>()))
                throw new InvalidDataException("Choose a known government and ideology.");
        }
        // These limits are model rules, not presentation limits a UI mod can remove.
        foreach (var field in (country ? typeof(Nation) : typeof(ScenarioState)).GetFields(BindingFlags.Public | BindingFlags.Instance))
            if (field.FieldType == typeof(float)) CheckNumber(field.Name, -float.MaxValue, float.MaxValue);
        foreach (var key in new[] { "Population", "Gdp", "MilitarySpending", "ConstructionCapacity", "ManpowerReserve" }) CheckNumber(key, 0, double.MaxValue);
        foreach (var key in new[] { "TaxRate", "WelfareSpending", "EducationSpending", "Literacy", "StandardOfLiving", "Stability", "WarSupport", "Radicalism", "SecessionPressure", "Urbanization", "Urbanisation", "AgricultureShare" }) CheckNumber(key, 0, 1);
        CheckNumber("CapitalLatitude", -90, 90); CheckNumber("CapitalLongitude", -180, 180);
        if (entity["Population"] is not null && !long.TryParse(entity["Population"].ToJsonString(), out _))
            throw new InvalidDataException("Population must be a whole number.");
        return entity;
        void CheckNumber(string key, double min, double max)
        {
            if (!entity.ContainsKey(key)) return;
            var node = entity[key];
            if (node is null || !double.TryParse(node.ToJsonString(), System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var n) || !double.IsFinite(n) || n < min || n > max)
                throw new InvalidDataException($"{key}: expected a number from {min} to {max}.");
        }
    }
    public static void ProtectStoredFields(JsonObject before, JsonObject after, bool country)
    {
        foreach (var field in before)
            if (!after.ContainsKey(field.Key)) throw new InvalidDataException("Stored field cannot be removed in the inspector: " + field.Key);
        foreach (var key in country ? new[] { "Id", "MapIndex", "Tag", "Iso" } : new[] { "MapIndex", "NationMapIndex", "NationId", "Id" })
            if (!JsonNode.DeepEquals(before[key], after[key])) throw new InvalidDataException("Identity field is read-only: " + key);
    }
}
