using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace ByDealOrForce.Simulation;

public sealed record UnitTypeDefinition(
    string Id,
    string Name,
    string CounterSymbol,
    float Attack,
    float Defense,
    float Speed,
    float CombatWidth,
    float SupplyUse);

/// <summary>Paradox-style common/units catalogue consumed by simulation and rendering.</summary>
public static class UnitTypeCatalog
{
    private static readonly Dictionary<string, UnitTypeDefinition> Definitions = Load();

    public static UnitTypeDefinition Get(string id) =>
        id is not null && Definitions.TryGetValue(id, out var definition)
            ? definition
            : Definitions["line_infantry"];

    public static bool Override(string id, string name, string counterSymbol, float? attack,
        float? defense, float? speed, float? combatWidth, float? supplyUse)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var current = Definitions.TryGetValue(id, out var existing)
            ? existing
            : new UnitTypeDefinition(id, id, "infantry", 1f, 1f, 1f, 2f, 1f);
        if (attack is <= 0f || defense is <= 0f || speed is <= 0f || combatWidth is <= 0f || supplyUse is <= 0f)
            return false;
        Definitions[id] = current with
        {
            Name = string.IsNullOrWhiteSpace(name) ? current.Name : name,
            CounterSymbol = string.IsNullOrWhiteSpace(counterSymbol) ? current.CounterSymbol : counterSymbol,
            Attack = attack ?? current.Attack,
            Defense = defense ?? current.Defense,
            Speed = speed ?? current.Speed,
            CombatWidth = combatWidth ?? current.CombatWidth,
            SupplyUse = supplyUse ?? current.SupplyUse
        };
        return true;
    }

    public static string InitialTemplate(Nation nation, int formationIndex, float personnel)
    {
        if (personnel >= 150_000f && nation.Gdp >= 300f && formationIndex == 0)
            return "armored_corps";
        if (personnel >= 80_000f && nation.Gdp >= 100f && formationIndex % 4 == 1)
            return "motorized_corps";
        return "line_infantry";
    }

    private static Dictionary<string, UnitTypeDefinition> Load()
    {
        var result = new Dictionary<string, UnitTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        var file = GameDataLoader.FindDataFile("common", "units", "unit_types.json");
        if (file is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file));
                foreach (var row in document.RootElement.EnumerateObject())
                {
                    var value = row.Value;
                    result[row.Name] = new UnitTypeDefinition(
                        row.Name,
                        Text(value, "name", row.Name),
                        Text(value, "counter_symbol", "infantry"),
                        Number(value, "attack", 1f),
                        Number(value, "defense", 1f),
                        Number(value, "speed", 1f),
                        Number(value, "combat_width", 2f),
                        Number(value, "supply_use", 1f));
                }
            }
            catch
            {
                result.Clear();
            }
        }

        result.TryAdd("line_infantry", new UnitTypeDefinition(
            "line_infantry", "Line infantry", "infantry", 0.95f, 1.10f, 1f, 1.8f, 0.95f));
        result.TryAdd("motorized_corps", new UnitTypeDefinition(
            "motorized_corps", "Motorized corps", "motorized", 1.08f, 0.96f, 1.28f, 2f, 1.18f));
        result.TryAdd("armored_corps", new UnitTypeDefinition(
            "armored_corps", "Armored corps", "armor", 1.32f, 1.12f, 1.12f, 2.8f, 1.55f));
        return result;
    }

    private static string Text(JsonElement element, string name, string fallback) =>
        element.TryGetProperty(name, out var value) && value.ValueKind == JsonValueKind.String
            ? value.GetString() ?? fallback : fallback;

    private static float Number(JsonElement element, string name, float fallback) =>
        element.TryGetProperty(name, out var value) && value.TryGetSingle(out var number) ? number : fallback;
}
