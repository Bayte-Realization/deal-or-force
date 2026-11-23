using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Blueprint for one Strategic Megaproject from <c>game/common/grand_projects/*.json</c>.
/// Every modifier key is validated against <see cref="NationalModifiers.Keys"/> at load time so
/// typos in base data or mods fail loudly instead of silently doing nothing.
/// </summary>
public sealed record GrandProjectDefinition(
    string Id,
    string NameKey,
    string DescriptionKey,
    string Category,
    int Order,
    int[] StageDays,
    float BaseCostBillions,
    float GdpCostShare,
    float PoliticalCost,
    string ExperienceCurrency,
    float ExperienceCost,
    long MinPopulation,
    float MinLiteracy,
    bool RequiresDistressedRegion,
    bool UniqueGlobal,
    string[] KeyGoods,
    Dictionary<string, float> HostModifiers,
    Dictionary<string, float> PartnerModifiers)
{
    public string DisplayName => Loc.Get(NameKey);
    public string DisplayDescription => Loc.Get(DescriptionKey);
    public string CategoryName => Loc.Get("gp.cat." + Category);

    public int DurationForStage(int stage)
    {
        if (StageDays is null || StageDays.Length == 0) return 90;
        var index = M.Clamp(stage - 1, 0, StageDays.Length - 1);
        return Math.Max(7, StageDays[index]);
    }
}

/// <summary>
/// Moddable catalog of Strategic Megaproject blueprints (<c>common/grand_projects</c>).
/// Later files override earlier ids; <c>"remove": true</c> removes an entry cleanly.
/// </summary>
public static class GrandProjectCatalog
{
    private static readonly List<GrandProjectDefinition> Definitions = new();
    private static readonly Dictionary<string, GrandProjectDefinition> ById = new(StringComparer.OrdinalIgnoreCase);

    public static int Revision { get; private set; }
    public static IReadOnlyList<GrandProjectDefinition> All => Definitions;

    static GrandProjectCatalog() => ResetCatalog();

    public static GrandProjectDefinition Find(string id) =>
        !string.IsNullOrWhiteSpace(id) && ById.TryGetValue(id, out var found) ? found : null;

    public static void ResetCatalog()
    {
        Definitions.Clear();
        ById.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "grand_projects"))
            using (document) Merge(document.RootElement, null);
        RebuildIndex();
    }

    public static void Merge(JsonElement root, List<string> errors)
    {
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add("grand_projects: expected a JSON object.");
            return;
        }
        foreach (var entry in root.EnumerateObject())
        {
            if (entry.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            Apply(entry.Name, entry.Value, errors);
        }
        RebuildIndex();
    }

    public static bool Apply(string id, JsonElement value, List<string> errors)
    {
        if (!MechanicFeatures.ValidId(id) || value.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"grand_projects/{id}: expected a valid id and JSON object.");
            return false;
        }
        if (GameDataLoader.IsRemoved(value, $"common/grand_projects:{id}"))
        {
            Definitions.RemoveAll(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
            ById.Remove(id);
            Revision++;
            return true;
        }

        var existing = Find(id);
        string Text(string key, string fallback) =>
            value.TryGetProperty(key, out var raw) && raw.ValueKind == JsonValueKind.String
                ? raw.GetString() ?? fallback
                : fallback;

        float Number(string key, float fallback, float min, float max)
        {
            if (!value.TryGetProperty(key, out var raw)) return fallback;
            if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetSingle(out var parsed) ||
                !float.IsFinite(parsed) || parsed < min || parsed > max)
            {
                errors?.Add($"grand_projects/{id}: `{key}` must be a finite number in [{min}, {max}].");
                return fallback;
            }
            return parsed;
        }

        long LongVal(string key, long fallback, long min, long max)
        {
            if (!value.TryGetProperty(key, out var raw)) return fallback;
            if (raw.ValueKind != JsonValueKind.Number || !raw.TryGetInt64(out var parsed) ||
                parsed < min || parsed > max)
            {
                errors?.Add($"grand_projects/{id}: `{key}` must be an integer in [{min}, {max}].");
                return fallback;
            }
            return parsed;
        }

        bool Flag(string key, bool fallback) =>
            value.TryGetProperty(key, out var raw) && raw.ValueKind is JsonValueKind.True or JsonValueKind.False
                ? raw.GetBoolean()
                : fallback;

        int[] stageDays = existing?.StageDays is { Length: 3 } prevDays
            ? (int[])prevDays.Clone()
            : new[] { 90, 150, 90 };
        if (value.TryGetProperty("stage_days", out var rawDays) && rawDays.ValueKind == JsonValueKind.Array)
        {
            var parsed = new List<int>(3);
            foreach (var item in rawDays.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetInt32(out var d))
                    parsed.Add(M.Clamp(d, 7, 1825));
            if (parsed.Count == 3)
                stageDays = parsed.ToArray();
            else
                errors?.Add($"grand_projects/{id}: `stage_days` must contain exactly 3 stage durations.");
        }

        var expCurrency = Text("experience_currency", existing?.ExperienceCurrency ?? "").Trim().ToLowerInvariant();
        if (expCurrency.Length > 0 && expCurrency is not ("army_experience" or "air_experience" or "naval_experience"))
        {
            errors?.Add($"grand_projects/{id}: unsupported `experience_currency` '{expCurrency}'.");
            expCurrency = "";
        }

        string[] keyGoods = existing?.KeyGoods ?? Array.Empty<string>();
        if (value.TryGetProperty("key_goods", out var rawGoods) && rawGoods.ValueKind == JsonValueKind.Array)
        {
            var goods = new List<string>();
            foreach (var item in rawGoods.EnumerateArray())
            {
                if (item.ValueKind != JsonValueKind.String) continue;
                var goodName = (item.GetString() ?? "").Trim();
                if (goodName.Length == 0) continue;
                if (!CommodityCatalog.TryParse(goodName, out _))
                {
                    errors?.Add($"grand_projects/{id}: unknown commodity '{goodName}' in `key_goods`.");
                    continue;
                }
                goods.Add(goodName);
            }
            keyGoods = goods.ToArray();
        }

        Dictionary<string, float> hostMods = existing is not null
            ? new Dictionary<string, float>(existing.HostModifiers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (value.TryGetProperty("host_modifiers", out var rawHost))
            hostMods = NationalModifiers.Read(rawHost, $"grand_projects/{id}.host_modifiers", errors);

        Dictionary<string, float> partnerMods = existing is not null
            ? new Dictionary<string, float>(existing.PartnerModifiers, StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (value.TryGetProperty("partner_modifiers", out var rawPartner))
            partnerMods = NationalModifiers.Read(rawPartner, $"grand_projects/{id}.partner_modifiers", errors);

        var def = new GrandProjectDefinition(
            Id: id,
            NameKey: Text("name", existing?.NameKey ?? id),
            DescriptionKey: Text("description", existing?.DescriptionKey ?? ""),
            Category: Text("category", existing?.Category ?? "infrastructure").Trim().ToLowerInvariant(),
            Order: (int)Number("order", existing?.Order ?? 100, 0, 10000),
            StageDays: stageDays,
            BaseCostBillions: Number("base_cost_billions", existing?.BaseCostBillions ?? 1.0f, 0.05f, 500f),
            GdpCostShare: Number("gdp_cost_share", existing?.GdpCostShare ?? 0.002f, 0f, 0.25f),
            PoliticalCost: Number("political_cost", existing?.PoliticalCost ?? 10f, 0f, 500f),
            ExperienceCurrency: expCurrency,
            ExperienceCost: Number("experience_cost", existing?.ExperienceCost ?? 0f, 0f, 500f),
            MinPopulation: LongVal("min_population", existing?.MinPopulation ?? 0, 0, 2_000_000_000L),
            MinLiteracy: Number("min_literacy", existing?.MinLiteracy ?? 0f, 0f, 1f),
            RequiresDistressedRegion: Flag("requires_distressed_region", existing?.RequiresDistressedRegion ?? false),
            UniqueGlobal: Flag("unique_global", existing?.UniqueGlobal ?? false),
            KeyGoods: keyGoods,
            HostModifiers: hostMods,
            PartnerModifiers: partnerMods);

        Definitions.RemoveAll(d => d.Id.Equals(id, StringComparison.OrdinalIgnoreCase));
        Definitions.Add(def);
        ById[id] = def;
        Revision++;
        return true;
    }

    private static void RebuildIndex()
    {
        DataCatalogLoader.RebuildSortedById(Definitions, ById, d => d.Order, d => d.Id);
        Revision++;
    }
}
