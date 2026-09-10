using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What a cabinet priority is called and what it does to the country, as data.
///
/// The five the game ships with were a switch statement in five different places: one for
/// the name, one for the short name, one for the sentence under the button, one for annual
/// growth, and four more scattered through the weekly economic tick for radicalism, living
/// standards, literacy, stability and war support. A person retuning the cabinet had to find
/// all nine. They are one row each now.
/// </summary>
public sealed class GovernmentFocusDefinition
{
    public string Id = "";
    public string Name = "";
    public string ShortName = "";
    public string Description = "";

    /// <summary>Added to the annual growth the economy converges on.</summary>
    public float Growth;

    // The rest are weekly, which is the cadence each of these quantities already moves at.
    public float Living;
    public float Literacy;
    public float Radicalism;
    public float Stability;
    public float WarSupport;

    /// <summary>Added to the productivity a country's literacy is pulling it towards.</summary>
    public float ProductivityTarget;
}

public static class GovernmentFocusCatalog
{
    /// <summary>
    /// Enum values kept clear for cabinets the game has never heard of.
    ///
    /// The same arrangement goods and buildings use. `GovernmentFocus` is an enum because a
    /// country carries one in every save ever written and the AI names them directly; a mod
    /// that wants a sixth priority takes the next free value, and everything that switches on
    /// a focus asks this catalogue rather than the enum, so it works the day it is declared.
    /// </summary>
    public const int ModdedHeadroom = 16;

    private static readonly Dictionary<GovernmentFocus, GovernmentFocusDefinition> Definitions = new();
    private static readonly Dictionary<string, GovernmentFocus> Named =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly GovernmentFocusDefinition Unknown = new()
    {
        Id = "unknown",
        Name = "Unnamed cabinet",
        ShortName = "Unnamed",
        Description = "A priority nothing in the data describes."
    };

    /// <summary>Every focus a government may adopt, in the order the file declares them.</summary>
    public static IReadOnlyList<GovernmentFocus> All { get; private set; } = Array.Empty<GovernmentFocus>();

    static GovernmentFocusCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        Named.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "government_focus"))
            using (document)
                Merge(document.RootElement, errors);
        Reorder();
        if (Definitions.Count == 0)
            errors?.Add("common/government_focus describes no cabinet, so no government has a direction.");
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Reorder();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = Read(entry.Name, entry.Value, errors);
            if (definition is null) continue;
            var focus = Resolve(entry.Name, errors);
            if (focus is null) continue;
            Definitions[focus.Value] = definition;
            Named[Squash(entry.Name)] = focus.Value;
        }
    }

    public static void Validate(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Object)
                Read(entry.Name, entry.Value, problems);
    }

    /// <summary>
    /// Which enum value a name means: the shipped five by their own names, and anything else
    /// the next free value in the headroom.
    /// </summary>
    private static GovernmentFocus? Resolve(string id, List<string> errors)
    {
        if (Named.TryGetValue(Squash(id), out var existing)) return existing;
        if (Enum.TryParse<GovernmentFocus>(Squash(id), true, out var shipped) &&
            Enum.IsDefined(shipped)) return shipped;

        var shippedCount = Enum.GetValues<GovernmentFocus>().Length;
        for (var value = shippedCount; value < shippedCount + ModdedHeadroom; value++)
            if (!Definitions.ContainsKey((GovernmentFocus)value)) return (GovernmentFocus)value;

        errors?.Add($"government focus `{id}`: no room left. The headroom is " +
                    $"{ModdedHeadroom} cabinets beyond the ones the game ships with.");
        return null;
    }

    private static GovernmentFocusDefinition Read(string id, JsonElement element, List<string> errors)
    {
        var definition = new GovernmentFocusDefinition { Id = id.Trim(), Name = id.Trim(), ShortName = id.Trim() };
        foreach (var field in element.EnumerateObject())
        {
            if (field.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            switch (field.Name.ToLowerInvariant())
            {
                case "name" when field.Value.ValueKind == JsonValueKind.String:
                    definition.Name = field.Value.GetString() ?? definition.Name;
                    break;
                case "short_name" when field.Value.ValueKind == JsonValueKind.String:
                    definition.ShortName = field.Value.GetString() ?? definition.ShortName;
                    break;
                case "description" when field.Value.ValueKind == JsonValueKind.String:
                    definition.Description = field.Value.GetString() ?? "";
                    break;
                case "effects" when field.Value.ValueKind == JsonValueKind.Object:
                    ReadEffects(id, field.Value, definition, errors);
                    break;
                default:
                    errors?.Add($"government focus `{id}`: `{field.Name}` is not a field a cabinet has.");
                    break;
            }
        }
        return definition;
    }

    private static void ReadEffects(string id, JsonElement element,
        GovernmentFocusDefinition definition, List<string> errors)
    {
        foreach (var entry in element.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            var value = entry.Value.ValueKind == JsonValueKind.Number &&
                        entry.Value.TryGetSingle(out var number) ? number : 0f;
            switch (entry.Name.ToLowerInvariant())
            {
                case "growth": definition.Growth = value; break;
                case "living": definition.Living = value; break;
                case "literacy": definition.Literacy = value; break;
                case "radicalism": definition.Radicalism = value; break;
                case "stability": definition.Stability = value; break;
                case "war_support": definition.WarSupport = value; break;
                case "productivity_target": definition.ProductivityTarget = value; break;
                default:
                    errors?.Add($"government focus `{id}`: `{entry.Name}` is not an effect. Use " +
                                "growth, living, literacy, radicalism, stability, war_support " +
                                "or productivity_target.");
                    break;
            }
        }
    }

    private static void Reorder()
    {
        var order = new List<GovernmentFocus>(Definitions.Keys);
        order.Sort((left, right) => ((int)left).CompareTo((int)right));
        All = order;
    }

    private static string Squash(string id)
    {
        var text = new System.Text.StringBuilder(id.Length);
        foreach (var character in id)
            if (char.IsLetterOrDigit(character)) text.Append(char.ToLowerInvariant(character));
        return text.ToString();
    }

    public static GovernmentFocusDefinition Of(GovernmentFocus focus) =>
        Definitions.TryGetValue(focus, out var definition) ? definition : Unknown;

    public static bool Knows(GovernmentFocus focus) => Definitions.ContainsKey(focus);
}

/// <summary>What a trade stance is called and what it does to the market. Same idea.</summary>
public sealed class TradePolicyDefinition
{
    public string Id = "";
    public string Name = "";
    public string Description = "";
    public float ImportAccess = 1f;
    public float ExportAccess = 1f;
    public float Tariff;
}

public static class TradePolicyCatalog
{
    public const int ModdedHeadroom = 8;

    private static readonly Dictionary<TradePolicy, TradePolicyDefinition> Definitions = new();
    private static readonly Dictionary<string, TradePolicy> Named =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly TradePolicyDefinition Unknown = new()
    {
        Id = "unknown",
        Name = "Unnamed trade policy",
        Description = "A stance nothing in the data describes."
    };

    public static IReadOnlyList<TradePolicy> All { get; private set; } = Array.Empty<TradePolicy>();

    static TradePolicyCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        Definitions.Clear();
        Named.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "trade_policies"))
            using (document)
                Merge(document.RootElement, errors);
        Reorder();
        if (Definitions.Count == 0)
            errors?.Add("common/trade_policies describes no stance, so no country can trade on terms.");
    }

    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
        Reorder();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.Object) continue;
            var definition = Read(entry.Name, entry.Value, errors);
            if (definition is null) continue;
            var policy = Resolve(entry.Name, errors);
            if (policy is null) continue;
            Definitions[policy.Value] = definition;
            Named[Squash(entry.Name)] = policy.Value;
        }
    }

    public static void Validate(JsonElement root, List<string> problems)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        foreach (var entry in root.EnumerateObject())
            if (entry.Value.ValueKind == JsonValueKind.Object)
                Read(entry.Name, entry.Value, problems);
    }

    private static TradePolicy? Resolve(string id, List<string> errors)
    {
        if (Named.TryGetValue(Squash(id), out var existing)) return existing;
        if (Enum.TryParse<TradePolicy>(Squash(id), true, out var shipped) &&
            Enum.IsDefined(shipped)) return shipped;

        var shippedCount = Enum.GetValues<TradePolicy>().Length;
        for (var value = shippedCount; value < shippedCount + ModdedHeadroom; value++)
            if (!Definitions.ContainsKey((TradePolicy)value)) return (TradePolicy)value;

        errors?.Add($"trade policy `{id}`: no room left. The headroom is {ModdedHeadroom} " +
                    "stances beyond the ones the game ships with.");
        return null;
    }

    private static TradePolicyDefinition Read(string id, JsonElement element, List<string> errors)
    {
        var definition = new TradePolicyDefinition { Id = id.Trim(), Name = id.Trim() };
        foreach (var field in element.EnumerateObject())
        {
            if (field.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            var number = field.Value.ValueKind == JsonValueKind.Number &&
                         field.Value.TryGetSingle(out var value) ? value : 0f;
            switch (field.Name.ToLowerInvariant())
            {
                case "name" when field.Value.ValueKind == JsonValueKind.String:
                    definition.Name = field.Value.GetString() ?? definition.Name;
                    break;
                case "description" when field.Value.ValueKind == JsonValueKind.String:
                    definition.Description = field.Value.GetString() ?? "";
                    break;
                case "import_access": definition.ImportAccess = number; break;
                case "export_access": definition.ExportAccess = number; break;
                case "tariff": definition.Tariff = number; break;
                default:
                    errors?.Add($"trade policy `{id}`: `{field.Name}` is not a field a stance has.");
                    break;
            }
        }
        return definition;
    }

    private static void Reorder()
    {
        var order = new List<TradePolicy>(Definitions.Keys);
        order.Sort((left, right) => ((int)left).CompareTo((int)right));
        All = order;
    }

    private static string Squash(string id)
    {
        var text = new System.Text.StringBuilder(id.Length);
        foreach (var character in id)
            if (char.IsLetterOrDigit(character)) text.Append(char.ToLowerInvariant(character));
        return text.ToString();
    }

    public static TradePolicyDefinition Of(TradePolicy policy) =>
        Definitions.TryGetValue(policy, out var definition) ? definition : Unknown;

    public static bool Knows(TradePolicy policy) => Definitions.ContainsKey(policy);
}
