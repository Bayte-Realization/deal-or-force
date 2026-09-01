using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace ByDealOrForce.Simulation;

/// <summary>
/// The scripted layer every piece of content writes its consequences in: events,
/// decisions and mod scripts all resolve to the same small effect vocabulary instead of
/// a closed C# enum a modder cannot extend.
///
/// Paradox content works this way for a reason - a modder who can only pick from six
/// pre-written outcomes cannot express a new idea, only re-skin an existing one. Here a
/// JSON object of key/value pairs is the whole language:
///
///   "effects": { "treasury_percent": -0.02, "stability": 0.018, "state.radicalism": -0.01 }
///
/// A bare number adds. Suffix `_percent` scales by (1 + value), `_mult` multiplies
/// outright, `_set` assigns. A `state.` prefix applies the entry to every state the
/// nation owns rather than to the nation itself. Unknown keys never fail silently: they
/// are reported alongside the list of keys that would have worked.
/// </summary>
public static class ScriptedEffects
{
    private enum Operation { Add, Percent, Mult, Set }

    private sealed class Field
    {
        public string Name = "";
        public Func<Nation, float> GetNation;
        public Action<Nation, float> SetNation;
        public Func<ScenarioState, float> GetState;
        public Action<ScenarioState, float> SetState;
        public float Min = float.NegativeInfinity;
        public float Max = float.PositiveInfinity;
        public string Help = "";
    }

    private static readonly Dictionary<string, Field> Fields = new(StringComparer.OrdinalIgnoreCase);

    private static readonly (string Suffix, Operation Operation)[] Suffixes =
    {
        ("_percent", Operation.Percent),
        ("_mult", Operation.Mult),
        ("_set", Operation.Set)
    };

    static ScriptedEffects()
    {
        NationField("treasury", n => n.Treasury, (n, v) => n.Treasury = v, 0f, float.PositiveInfinity,
            "National treasury in the campaign's currency unit.");
        NationField("gdp", n => n.Gdp, (n, v) => n.Gdp = v, 0.01f, float.PositiveInfinity,
            "Annual gross domestic product.");
        NationField("productivity", n => n.Productivity, (n, v) => n.Productivity = v, 0.05f, 8f,
            "Output multiplier; the long-run driver of GDP growth.");
        NationField("stability", n => n.Stability, (n, v) => n.Stability = v, 0.10f, 0.98f,
            "Domestic political stability, 0-1.");
        NationField("war_support", n => n.WarSupport, (n, v) => n.WarSupport = v, 0f, 1f,
            "Public willingness to sustain a war, 0-1.");
        NationField("standard_of_living", n => n.StandardOfLiving, (n, v) => n.StandardOfLiving = v, 0f, 1f,
            "National average standard of living, 0-1.");
        NationField("literacy", n => n.Literacy, (n, v) => n.Literacy = v, 0.05f, 0.999f,
            "Share of the population that is literate, 0-1.");
        NationField("construction_capacity", n => n.ConstructionCapacity, (n, v) => n.ConstructionCapacity = v,
            0f, float.PositiveInfinity, "Parallel construction throughput.");
        NationField("manpower", n => n.ManpowerReserve, (n, v) => n.ManpowerReserve = v, 0f, float.PositiveInfinity,
            "Trained manpower available for reinforcement.");
        NationField("tax_rate", n => n.TaxRate, (n, v) => n.TaxRate = v, 0f, 0.75f,
            "Headline tax rate, 0-1.");
        NationField("military_spending", n => n.MilitarySpending, (n, v) => n.MilitarySpending = v, 0f, 0.5f,
            "Share of output spent on the armed forces.");
        NationField("welfare_spending", n => n.WelfareSpending, (n, v) => n.WelfareSpending = v, 0f, 0.5f,
            "Share of output spent on social welfare.");
        NationField("education_spending", n => n.EducationSpending, (n, v) => n.EducationSpending = v, 0f, 0.5f,
            "Share of output spent on education and science.");
        NationField("market_access", n => n.MarketAccess, (n, v) => n.MarketAccess = v, 0f, 1f,
            "Access to the world market, 0-1.");
        NationField("population", n => n.Population, (n, v) => n.Population = (long)MathF.Max(0f, v),
            0f, float.PositiveInfinity, "National population headcount.");

        StateField("state.standard_of_living", s => s.StandardOfLiving, (s, v) => s.StandardOfLiving = v, 0f, 1f,
            "Standard of living in every state the nation owns.");
        StateField("state.radicalism", s => s.Radicalism, (s, v) => s.Radicalism = v, 0f, 1f,
            "Radicalism in every state the nation owns.");
        StateField("state.literacy", s => s.Literacy, (s, v) => s.Literacy = v, 0f, 1f,
            "Literacy in every state the nation owns.");
        StateField("state.urbanization", s => s.Urbanization, (s, v) => s.Urbanization = v, 0f, 1f,
            "Urbanisation in every state the nation owns.");
        StateField("state.population", s => s.Population, (s, v) => s.Population = (long)MathF.Max(0f, v),
            0f, float.PositiveInfinity, "Population of every state the nation owns.");
    }

    private static void NationField(string name, Func<Nation, float> get, Action<Nation, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetNation = get, SetNation = set, Min = min, Max = max, Help = help };

    private static void StateField(string name, Func<ScenarioState, float> get, Action<ScenarioState, float> set,
        float min, float max, string help) =>
        Fields[name] = new Field { Name = name, GetState = get, SetState = set, Min = min, Max = max, Help = help };

    /// <summary>Every key content may write to, for documentation and console help.</summary>
    public static IEnumerable<(string Key, string Help)> Vocabulary =>
        Fields.Values.OrderBy(field => field.Name, StringComparer.Ordinal).Select(field => (field.Name, field.Help));

    public static bool IsKnown(string key) => Resolve(key, out _, out _);

    private static bool Resolve(string rawKey, out Field field, out Operation operation)
    {
        field = null;
        operation = Operation.Add;
        if (string.IsNullOrWhiteSpace(rawKey)) return false;

        var key = rawKey.Trim().Replace('-', '_');
        foreach (var (suffix, candidate) in Suffixes)
        {
            if (!key.EndsWith(suffix, StringComparison.OrdinalIgnoreCase)) continue;
            var stem = key[..^suffix.Length];
            if (!Fields.TryGetValue(stem, out field)) continue;
            operation = candidate;
            return true;
        }
        return Fields.TryGetValue(key, out field);
    }

    /// <summary>One parsed entry, kept flat so applying an effect allocates nothing.</summary>
    public readonly struct Entry
    {
        internal Entry(string key, float value)
        {
            Key = key;
            Value = value;
        }

        public string Key { get; }
        public float Value { get; }
    }

    public sealed class Effect
    {
        private readonly List<Entry> _entries = new();

        public IReadOnlyList<Entry> Entries => _entries;
        public bool IsEmpty => _entries.Count == 0;

        internal void Add(string key, float value) => _entries.Add(new Entry(key, value));

        public void Apply(Campaign campaign, Nation nation)
        {
            if (nation is null) return;
            List<ScenarioState> owned = null;

            foreach (var entry in _entries)
            {
                if (!Resolve(entry.Key, out var field, out var operation)) continue;

                if (field.SetNation is not null)
                {
                    field.SetNation(nation, Combine(field.GetNation(nation), entry.Value, operation, field));
                    continue;
                }

                owned ??= campaign.StatesOf(nation).ToList();
                foreach (var state in owned)
                    field.SetState(state, Combine(field.GetState(state), entry.Value, operation, field));
            }
        }

        private static float Combine(float current, float value, Operation operation, Field field)
        {
            var result = operation switch
            {
                Operation.Percent => current * (1f + value),
                Operation.Mult => current * value,
                Operation.Set => value,
                _ => current + value
            };
            return M.Clamp(result, field.Min, field.Max);
        }

        /// <summary>Plain-language summary, so the UI can describe a modded option that shipped no text.</summary>
        public string Describe()
        {
            if (_entries.Count == 0) return "No effect.";
            return string.Join(", ", _entries.Select(entry =>
            {
                var sign = entry.Value >= 0f ? "+" : "";
                return $"{entry.Key} {sign}{entry.Value.ToString("0.###", CultureInfo.InvariantCulture)}";
            }));
        }
    }

    /// <summary>
    /// Parse an "effects" object. An unknown key costs that one line, not the whole file:
    /// one typo in a mod should never take the rest of the mod down with it.
    /// </summary>
    public static Effect Parse(JsonElement element, string context, List<string> errors)
    {
        var effect = new Effect();
        if (element.ValueKind != JsonValueKind.Object) return effect;

        foreach (var property in element.EnumerateObject())
        {
            if (!property.Value.TryGetSingle(out var value))
            {
                errors?.Add($"{context}: effect '{property.Name}' must be a number.");
                continue;
            }
            if (!IsKnown(property.Name))
            {
                errors?.Add($"{context}: unknown effect key '{property.Name}'. Known keys: " +
                            string.Join(", ", Vocabulary.Select(item => item.Key)));
                continue;
            }
            effect.Add(property.Name, value);
        }
        return effect;
    }

    /// <summary>Build an effect in code, for the compiled-in defaults.</summary>
    public static Effect From(params (string Key, float Value)[] entries)
    {
        var effect = new Effect();
        foreach (var (key, value) in entries) effect.Add(key, value);
        return effect;
    }
}

/// <summary>
/// The other half of the scripted layer: whether a piece of content may fire at all.
/// Same vocabulary as the effects, so anything a mod can change it can also test, written
/// as "trigger": { "min_stability": 0.4, "max_treasury": 500, "at_war": true }.
/// </summary>
public sealed class ScriptedTrigger
{
    private readonly List<(string Key, float Value, bool IsMinimum)> _bounds = new();
    private readonly List<string> _tags = new();
    private bool? _atWar;

    public bool IsEmpty => _bounds.Count == 0 && _atWar is null && _tags.Count == 0;

    public static ScriptedTrigger Parse(JsonElement element, string context, List<string> errors)
    {
        var trigger = new ScriptedTrigger();
        if (element.ValueKind != JsonValueKind.Object) return trigger;

        foreach (var property in element.EnumerateObject())
        {
            var name = property.Name.Trim().Replace('-', '_');

            if (name.Equals("at_war", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind is JsonValueKind.True or JsonValueKind.False)
                    trigger._atWar = property.Value.GetBoolean();
                else errors?.Add($"{context}: 'at_war' must be true or false.");
                continue;
            }

            if (name.Equals("tags", StringComparison.OrdinalIgnoreCase))
            {
                if (property.Value.ValueKind == JsonValueKind.Array)
                {
                    foreach (var tag in property.Value.EnumerateArray())
                        if (tag.ValueKind == JsonValueKind.String)
                            trigger._tags.Add(tag.GetString() ?? "");
                }
                else errors?.Add($"{context}: 'tags' must be an array of country tags.");
                continue;
            }

            var isMinimum = name.StartsWith("min_", StringComparison.OrdinalIgnoreCase);
            var isMaximum = name.StartsWith("max_", StringComparison.OrdinalIgnoreCase);
            if (!isMinimum && !isMaximum)
            {
                errors?.Add($"{context}: trigger '{property.Name}' must be min_<key>, max_<key>, at_war or tags.");
                continue;
            }
            if (!property.Value.TryGetSingle(out var value))
            {
                errors?.Add($"{context}: trigger '{property.Name}' must be a number.");
                continue;
            }

            var key = name[4..];
            if (!ScriptedEffects.IsKnown(key))
            {
                errors?.Add($"{context}: unknown trigger key '{key}'.");
                continue;
            }
            trigger._bounds.Add((key, value, isMinimum));
        }
        return trigger;
    }

    public bool Matches(Campaign campaign, Nation nation)
    {
        if (nation is null) return false;
        if (_tags.Count > 0 && !_tags.Any(tag => string.Equals(tag, nation.Tag, StringComparison.OrdinalIgnoreCase)))
            return false;
        if (_atWar.HasValue && campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id)) != _atWar.Value)
            return false;

        foreach (var (key, value, isMinimum) in _bounds)
        {
            if (!ScriptedValues.TryRead(campaign, nation, key, out var current)) continue;
            if (isMinimum && current < value) return false;
            if (!isMinimum && current > value) return false;
        }
        return true;
    }
}

/// <summary>Reads the same keys the effects write, for triggers and for the console.</summary>
public static class ScriptedValues
{
    public static bool TryRead(Campaign campaign, Nation nation, string key, out float value)
    {
        value = 0f;
        if (nation is null) return false;

        switch (key.Trim().Replace('-', '_').ToLowerInvariant())
        {
            case "treasury": value = nation.Treasury; return true;
            case "gdp": value = nation.Gdp; return true;
            case "productivity": value = nation.Productivity; return true;
            case "stability": value = nation.Stability; return true;
            case "war_support": value = nation.WarSupport; return true;
            case "standard_of_living": value = nation.StandardOfLiving; return true;
            case "literacy": value = nation.Literacy; return true;
            case "construction_capacity": value = nation.ConstructionCapacity; return true;
            case "manpower": value = nation.ManpowerReserve; return true;
            case "tax_rate": value = nation.TaxRate; return true;
            case "military_spending": value = nation.MilitarySpending; return true;
            case "welfare_spending": value = nation.WelfareSpending; return true;
            case "education_spending": value = nation.EducationSpending; return true;
            case "market_access": value = nation.MarketAccess; return true;
            case "population": value = nation.Population; return true;
            case "state.standard_of_living": value = Average(campaign, nation, state => state.StandardOfLiving); return true;
            case "state.radicalism": value = Average(campaign, nation, state => state.Radicalism); return true;
            case "state.literacy": value = Average(campaign, nation, state => state.Literacy); return true;
            case "state.urbanization": value = Average(campaign, nation, state => state.Urbanization); return true;
            case "state.population":
                value = campaign.StatesOf(nation)
                    .Sum(state => (float)state.Population);
                return true;
            default: return false;
        }
    }

    private static float Average(Campaign campaign, Nation nation, Func<ScenarioState, float> read)
    {
        var owned = campaign.StatesOf(nation);
        return owned.Length == 0 ? 0f : owned.Average(read);
    }
}
