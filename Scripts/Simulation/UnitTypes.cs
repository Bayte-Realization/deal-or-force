using System;
using System.Collections.Generic;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// One formation template, in the shape Hearts of Iron states its divisions.
///
/// The positional members are the original eight and stay first, so code and mods written
/// against the old catalogue keep working. Everything after them is the combat model this
/// project grew into: a division has separate soft and hard attack, its own breakthrough
/// for when it is the one attacking, armour and piercing, and a hardness that decides which
/// of the two attack values an enemy gets to use against it. `attack` survives as a general
/// offence multiplier for a template that has not been rewritten - it scales both.
/// </summary>
public sealed record UnitTypeDefinition(
    string Id,
    string Name,
    string CounterSymbol,
    float Attack,
    float Defense,
    float Speed,
    float CombatWidth,
    float SupplyUse)
{
    /// <summary>Damage against soft targets - infantry, artillery, unarmoured everything.</summary>
    public float SoftAttack { get; init; } = 1f;

    /// <summary>Damage against armour. Nearly useless against infantry, decisive against tanks.</summary>
    public float HardAttack { get; init; } = 0.35f;

    /// <summary>Protection while attacking. Defense protects; breakthrough is its offensive twin.</summary>
    public float Breakthrough { get; init; } = 0.8f;

    /// <summary>Armour rating. Above the enemy's piercing, incoming damage is halved.</summary>
    public float Armour { get; init; }

    /// <summary>Ability to hurt armour at all. Below the enemy's armour, damage is halved.</summary>
    public float Piercing { get; init; } = 0.4f;

    /// <summary>Share of the formation that counts as armoured when it is being shot at.</summary>
    public float Hardness { get; init; }

    /// <summary>Organisation ceiling. Organisation is the will to keep fighting, not the men.</summary>
    public float Organisation { get; init; } = 1f;

    /// <summary>Organisation regained per day out of combat, as a multiplier of the base rate.</summary>
    public float Recovery { get; init; } = 1f;
}

/// <summary>Paradox-style common/units catalogue consumed by simulation and rendering.</summary>
public static class UnitTypeCatalog
{
    private static readonly Dictionary<string, UnitTypeDefinition> Definitions = Load();

    public static UnitTypeDefinition Get(string id) =>
        id is not null && Definitions.TryGetValue(id, out var definition)
            ? definition
            : Definitions["line_infantry"];

    public static IEnumerable<UnitTypeDefinition> All => Definitions.Values;

    /// <summary>
    /// Layer one JSON object over a template. Every field is optional, so a mod that only
    /// wants harder tanks writes a single stat and inherits everything else.
    /// </summary>
    public static bool Override(string id, JsonElement value)
    {
        if (string.IsNullOrWhiteSpace(id) || value.ValueKind != JsonValueKind.Object) return false;
        var current = Definitions.TryGetValue(id, out var existing) ? existing : Fallback(id);
        var next = Read(id, value, current);
        if (next is null) return false;
        Definitions[id] = next;
        return true;
    }

    /// <summary>The five-number form, kept so code written against the old catalogue still builds.</summary>
    public static bool Override(string id, string name, string counterSymbol, float? attack,
        float? defense, float? speed, float? combatWidth, float? supplyUse)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        var current = Definitions.TryGetValue(id, out var existing) ? existing : Fallback(id);
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
        if (personnel >= 260_000f && nation.Gdp >= 600f && formationIndex % 7 == 3)
            return "mechanized_corps";
        if (personnel >= 80_000f && nation.Gdp >= 100f && formationIndex % 4 == 1)
            return "motorized_corps";
        if (personnel >= 120_000f && formationIndex % 5 == 2)
            return "artillery_corps";
        return "line_infantry";
    }

    private static UnitTypeDefinition Fallback(string id) =>
        new(id, id, "infantry", 1f, 1f, 1f, 2f, 1f);

    /// <summary>
    /// Read a template from JSON on top of a base. A value that is present but nonsensical -
    /// zero or negative where the model divides by it - rejects the whole template rather
    /// than quietly producing a division that cannot lose.
    /// </summary>
    private static UnitTypeDefinition Read(string id, JsonElement value, UnitTypeDefinition current)
    {
        string Text(string key, string fallback) =>
            value.TryGetProperty(key, out var element) && element.ValueKind == JsonValueKind.String
                ? element.GetString() ?? fallback : fallback;
        float Number(string key, float fallback) =>
            value.TryGetProperty(key, out var element) && element.TryGetSingle(out var number)
                ? number : fallback;

        var attack = Number("attack", current.Attack);
        var defense = Number("defense", current.Defense);
        var speed = Number("speed", current.Speed);
        var width = Number("combat_width", current.CombatWidth);
        var supply = Number("supply_use", current.SupplyUse);
        var organisation = Number("organisation", current.Organisation);
        if (attack <= 0f || defense <= 0f || speed <= 0f || width <= 0f || supply <= 0f || organisation <= 0f)
            return null;

        return current with
        {
            Id = id,
            Name = Text("name", current.Name),
            CounterSymbol = Text("counter_symbol", current.CounterSymbol),
            Attack = attack,
            Defense = defense,
            Speed = speed,
            CombatWidth = width,
            SupplyUse = supply,
            SoftAttack = MathF.Max(0f, Number("soft_attack", current.SoftAttack)),
            HardAttack = MathF.Max(0f, Number("hard_attack", current.HardAttack)),
            Breakthrough = MathF.Max(0.05f, Number("breakthrough", current.Breakthrough)),
            Armour = MathF.Max(0f, Number("armour", Number("armor", current.Armour))),
            Piercing = MathF.Max(0f, Number("piercing", current.Piercing)),
            Hardness = Math.Clamp(Number("hardness", current.Hardness), 0f, 1f),
            Organisation = organisation,
            Recovery = MathF.Max(0.05f, Number("recovery", current.Recovery))
        };
    }

    private static Dictionary<string, UnitTypeDefinition> Load()
    {
        var result = new Dictionary<string, UnitTypeDefinition>(StringComparer.OrdinalIgnoreCase);
        var file = GameDataLoader.FindDataFile("common", "units", "unit_types.json");
        if (file is not null)
        {
            try
            {
                using var document = JsonDocument.Parse(File.ReadAllText(file), new JsonDocumentOptions
                {
                    AllowTrailingCommas = true,
                    CommentHandling = JsonCommentHandling.Skip
                });
                foreach (var row in document.RootElement.EnumerateObject())
                {
                    var definition = Read(row.Name, row.Value, Fallback(row.Name));
                    if (definition is not null) result[row.Name] = definition;
                }
            }
            catch
            {
                result.Clear();
            }
        }

        // Compiled fallbacks, so the game is playable with no data files at all. These are
        // the same numbers game/common/units/unit_types.json ships.
        result.TryAdd("line_infantry", new UnitTypeDefinition(
            "line_infantry", "Line infantry", "infantry", 1f, 1.10f, 1f, 20f, 0.95f)
        {
            SoftAttack = 1.00f, HardAttack = 0.25f, Breakthrough = 0.55f, Armour = 0.30f,
            Piercing = 0.55f, Hardness = 0.05f, Organisation = 1.00f, Recovery = 1.00f
        });
        result.TryAdd("motorized_corps", new UnitTypeDefinition(
            "motorized_corps", "Motorized corps", "motorized", 1f, 0.96f, 1.28f, 22f, 1.18f)
        {
            SoftAttack = 1.15f, HardAttack = 0.40f, Breakthrough = 0.95f, Armour = 0.45f,
            Piercing = 0.75f, Hardness = 0.20f, Organisation = 1.05f, Recovery = 1.05f
        });
        result.TryAdd("mechanized_corps", new UnitTypeDefinition(
            "mechanized_corps", "Mechanized corps", "mechanized", 1f, 1.18f, 1.22f, 26f, 1.45f)
        {
            SoftAttack = 1.35f, HardAttack = 0.95f, Breakthrough = 1.45f, Armour = 1.60f,
            Piercing = 1.35f, Hardness = 0.60f, Organisation = 1.00f, Recovery = 0.95f
        });
        result.TryAdd("armored_corps", new UnitTypeDefinition(
            "armored_corps", "Armored corps", "armor", 1f, 1.05f, 1.12f, 30f, 1.70f)
        {
            SoftAttack = 1.55f, HardAttack = 1.60f, Breakthrough = 2.40f, Armour = 2.60f,
            Piercing = 2.10f, Hardness = 0.85f, Organisation = 0.90f, Recovery = 0.90f
        });
        result.TryAdd("artillery_corps", new UnitTypeDefinition(
            "artillery_corps", "Artillery corps", "artillery", 1f, 0.72f, 0.82f, 24f, 1.30f)
        {
            SoftAttack = 1.90f, HardAttack = 0.45f, Breakthrough = 0.40f, Armour = 0.10f,
            Piercing = 0.85f, Hardness = 0.02f, Organisation = 0.72f, Recovery = 0.88f
        });
        return result;
    }
}
