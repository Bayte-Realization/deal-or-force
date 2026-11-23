using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed partial class Nation
{
    /// <summary>
    /// Active non-default production method IDs keyed by canonical building ID
    /// (e.g. "CivilianIndustry" -> "pm_civilian_automated"). Empty when every building
    /// class runs its baseline method, keeping compact saves zero-byte overhead and
    /// older saves 100% backward compatible.
    /// </summary>
    public Dictionary<string, string> ActiveProductionMethods = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last campaign day the AI adjusted any production method for this nation.</summary>
    public int LastProductionMethodAiDay = -1;
}

/// <summary>
/// Data-driven switchable production method for a building class, loaded from
/// <c>game/common/production/*.json</c> entries marked with <c>"kind": "production_method"</c>.
/// </summary>
public sealed class ProductionMethodDefinition
{
    public string Id { get; init; } = "";
    public BuildingKind Building { get; init; }
    public string BuildingId { get; init; } = "";
    public string Name { get; init; } = "";
    public string Description { get; init; } = "";
    public bool IsDefault { get; init; }
    public float OutputMultiplier { get; init; } = 1f;
    public float JobsMultiplier { get; init; } = 1f;
    public float EnergyMultiplier { get; init; } = 1f;
    public float MinLiteracy { get; init; }
    public float MinProductivity { get; init; }
    public (Commodity Good, float PerBuilding)[] ExtraInputs { get; init; } = Array.Empty<(Commodity, float)>();
    public (Commodity Good, float PerBuilding)[] BonusOutputs { get; init; } = Array.Empty<(Commodity, float)>();
    public (Commodity Good, float Scale)[] DemandScales { get; init; } = Array.Empty<(Commodity, float)>();
    public (Commodity Good, float Floor)[] Needs { get; init; } = Array.Empty<(Commodity, float)>();
}

/// <summary>
/// Switchable production methods extending <see cref="ProductionChains"/> without creating
/// a parallel economy. Every method alters real output, labor efficiency, energy load,
/// input demand, and/or byproduct outputs deterministically, controlled by players and AI
/// through <see cref="SetProductionMethodCommand"/>.
/// </summary>
public static class ProductionMethods
{
    private static readonly Dictionary<string, ProductionMethodDefinition> ById =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<BuildingKind, List<ProductionMethodDefinition>> ByBuilding = new();
    private static readonly Dictionary<BuildingKind, ProductionMethodDefinition> Defaults = new();
    private static bool _loaded;

    private static void EnsureLoaded()
    {
        if (_loaded) return;
        _ = ProductionChains.RecipeCount;
    }

    public static int MethodCount { get { EnsureLoaded(); return ById.Count; } }

    public static IEnumerable<ProductionMethodDefinition> All { get { EnsureLoaded(); return ById.Values; } }

    public static ProductionMethodDefinition Find(string methodId)
    {
        EnsureLoaded();
        return !string.IsNullOrWhiteSpace(methodId) && ById.TryGetValue(methodId.Trim(), out var def) ? def : null;
    }

    public static IReadOnlyList<ProductionMethodDefinition> ForBuilding(BuildingKind kind)
    {
        EnsureLoaded();
        return ByBuilding.TryGetValue(kind, out var list) ? list : Array.Empty<ProductionMethodDefinition>();
    }

    public static ProductionMethodDefinition DefaultFor(BuildingKind kind)
    {
        EnsureLoaded();
        if (Defaults.TryGetValue(kind, out var def)) return def;
        return ByBuilding.TryGetValue(kind, out var list) && list.Count > 0 ? list[0] : null;
    }

    public static ProductionMethodDefinition ActiveFor(Nation nation, BuildingKind kind)
    {
        EnsureLoaded();
        if (nation?.ActiveProductionMethods is { Count: > 0 } active)
        {
            var buildingId = BuildingCatalog.IdOf(kind);
            if (active.TryGetValue(buildingId, out var methodId) &&
                ById.TryGetValue(methodId, out var found) &&
                found.Building == kind)
                return found;
        }
        return DefaultFor(kind);
    }

    internal static void ResetCatalog()
    {
        _loaded = true;
        ById.Clear();
        ByBuilding.Clear();
        Defaults.Clear();
    }

    internal static bool IsMethodEntry(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return false;
        if (value.TryGetProperty("kind", out var kindProp) && kindProp.ValueKind == JsonValueKind.String &&
            string.Equals(kindProp.GetString(), "production_method", StringComparison.OrdinalIgnoreCase))
            return true;
        return value.TryGetProperty("building", out _) && !value.TryGetProperty("good", out _);
    }

    internal static void MergeEntry(string id, JsonElement value, List<string> errors)
    {
        if (GameDataLoader.IsRemoved(value, $"common/production:{id}"))
        {
            Remove(id);
            return;
        }
        var parsed = Parse(id, value, errors);
        if (parsed is null) return;
        Remove(parsed.Id);
        ById[parsed.Id] = parsed;
        if (!ByBuilding.TryGetValue(parsed.Building, out var list))
            ByBuilding[parsed.Building] = list = new List<ProductionMethodDefinition>(4);
        list.Add(parsed);
        if (parsed.IsDefault || !Defaults.ContainsKey(parsed.Building))
            Defaults[parsed.Building] = parsed;
    }

    internal static void ValidateEntry(string id, JsonElement value, List<string> problems)
    {
        if (GameDataLoader.IsRemoved(value, $"common/production:{id}")) return;
        _ = Parse(id, value, problems);
    }

    private static void Remove(string id)
    {
        if (!ById.Remove(id, out var existing)) return;
        if (ByBuilding.TryGetValue(existing.Building, out var list))
        {
            list.RemoveAll(m => string.Equals(m.Id, existing.Id, StringComparison.OrdinalIgnoreCase));
            if (Defaults.TryGetValue(existing.Building, out var currentDef) &&
                string.Equals(currentDef.Id, existing.Id, StringComparison.OrdinalIgnoreCase))
            {
                var replacement = list.FirstOrDefault(m => m.IsDefault) ?? list.FirstOrDefault();
                if (replacement is not null) Defaults[existing.Building] = replacement;
                else Defaults.Remove(existing.Building);
            }
        }
    }

    private static ProductionMethodDefinition Parse(string id, JsonElement value, List<string> errors)
    {
        var problems = new List<string>();
        if (!value.TryGetProperty("building", out var buildingProp) || buildingProp.ValueKind != JsonValueKind.String ||
            !BuildingCatalog.TryParse(buildingProp.GetString(), out var building))
        {
            problems.Add($"Production method '{id}' does not name a valid building.");
            errors?.AddRange(problems);
            return null;
        }

        var name = value.TryGetProperty("name", out var nProp) && nProp.ValueKind == JsonValueKind.String
            ? nProp.GetString() ?? id
            : id;
        var description = value.TryGetProperty("description", out var dProp) && dProp.ValueKind == JsonValueKind.String
            ? dProp.GetString() ?? ""
            : "";
        var isDefault = value.TryGetProperty("default", out var defProp) &&
            (defProp.ValueKind == JsonValueKind.True || defProp.ValueKind == JsonValueKind.False) &&
            defProp.GetBoolean();

        var outputMul = ReadFloat(id, value, "output_multiplier", 1f, 0.25f, 3.0f, problems);
        var jobsMul = ReadFloat(id, value, "jobs_multiplier", 1f, 0.20f, 2.5f, problems);
        var energyMul = ReadFloat(id, value, "energy_multiplier", 1f, 0.25f, 3.0f, problems);
        var minLiteracy = ReadFloat(id, value, "min_literacy", 0f, 0f, 1f, problems);
        var minProductivity = ReadFloat(id, value, "min_productivity", 0f, 0f, 3f, problems);

        var extraInputs = ReadGoodAmounts(id, value, "extra_inputs", 0f, 50f, problems);
        var bonusOutputs = ReadGoodAmounts(id, value, "bonus_outputs", 0f, 50f, problems);
        var demandScales = ReadGoodAmounts(id, value, "demand_scales", 0f, 2.5f, problems);
        var needs = ReadGoodAmounts(id, value, "needs", 0f, 1f, problems);

        errors?.AddRange(problems);
        if (problems.Count > 0) return null;

        return new ProductionMethodDefinition
        {
            Id = id,
            Building = building,
            BuildingId = BuildingCatalog.IdOf(building),
            Name = name,
            Description = description,
            IsDefault = isDefault,
            OutputMultiplier = outputMul,
            JobsMultiplier = jobsMul,
            EnergyMultiplier = energyMul,
            MinLiteracy = minLiteracy,
            MinProductivity = minProductivity,
            ExtraInputs = extraInputs,
            BonusOutputs = bonusOutputs,
            DemandScales = demandScales,
            Needs = needs
        };
    }

    private static float ReadFloat(string id, JsonElement obj, string prop, float fallback, float min, float max,
        List<string> problems)
    {
        if (!obj.TryGetProperty(prop, out var el)) return fallback;
        if (el.ValueKind != JsonValueKind.Number || !el.TryGetSingle(out var val) ||
            !float.IsFinite(val) || val < min || val > max)
        {
            problems.Add($"Production method '{id}' {prop} must be a finite number in [{min}, {max}].");
            return fallback;
        }
        return val;
    }

    private static (Commodity Good, float Amount)[] ReadGoodAmounts(string id, JsonElement obj, string prop,
        float min, float max, List<string> problems)
    {
        if (!obj.TryGetProperty(prop, out var table)) return Array.Empty<(Commodity, float)>();
        if (table.ValueKind != JsonValueKind.Object)
        {
            problems.Add($"Production method '{id}' {prop} must be an object keyed by good ID.");
            return Array.Empty<(Commodity, float)>();
        }
        var items = new List<(Commodity, float)>();
        foreach (var entry in table.EnumerateObject())
        {
            if (!CommodityCatalog.TryParse(entry.Name, out var good))
            {
                problems.Add($"Production method '{id}' {prop} names unknown good '{entry.Name}'.");
                continue;
            }
            if (entry.Value.ValueKind != JsonValueKind.Number || !entry.Value.TryGetSingle(out var amount) ||
                !float.IsFinite(amount) || amount < min || amount > max)
            {
                problems.Add($"Production method '{id}' {prop}.{entry.Name} must be in [{min}, {max}].");
                continue;
            }
            items.Add((good, amount));
        }
        return items.ToArray();
    }

    public static bool CanUse(Campaign campaign, Nation nation, ProductionMethodDefinition method, out string refusal)
    {
        refusal = "";
        if (campaign is null || nation is null || method is null)
        {
            refusal = Loc.Get("Production method is unavailable.");
            return false;
        }
        if (method.MinLiteracy > 0f && nation.Literacy + 1e-4f < method.MinLiteracy)
        {
            refusal = Loc.Format("Requires literacy of at least {0}.", $"{method.MinLiteracy:P0}");
            return false;
        }
        if (method.MinProductivity > 0f && nation.Productivity + 1e-4f < method.MinProductivity)
        {
            refusal = Loc.Format("Requires national productivity of at least {0}.", $"{method.MinProductivity:0.00}");
            return false;
        }
        return true;
    }

    /// <summary>
    /// Output multiplier applied to one building class's staffed slot during <see cref="ProductionChains"/>
    /// regional evaluation. Combines the method's output multiplier (gated by its <see cref="ProductionMethodDefinition.Needs"/>)
    /// with labor-saving relief when the country is labor-constrained (<see cref="Nation.EmployedShare"/> &lt; 1).
    /// Zero allocations and 1-branch passthrough when <see cref="Nation.ActiveProductionMethods"/> is empty.
    /// </summary>
    internal static float EffectiveOutputFactor(Nation nation, BuildingKind kind)
    {
        if (nation?.ActiveProductionMethods is not { Count: > 0 }) return 1f;
        var method = ActiveFor(nation, kind);
        if (method is null || method.IsDefault) return 1f;

        var supply = SupplyFactor(nation, method.Needs);
        var outputMul = method.OutputMultiplier >= 1f
            ? 1f + (method.OutputMultiplier - 1f) * supply
            : method.OutputMultiplier * supply;

        var laborRelief = 1f;
        if (method.JobsMultiplier < 0.999f)
        {
            var employed = M.Clamp(nation.EmployedShare, 0.25f, 1f);
            if (employed < 0.995f)
            {
                var effJobs = M.Lerp(1f, method.JobsMultiplier, 1f - employed);
                laborRelief = MathF.Min(1f / employed, 1f / MathF.Max(0.5f, effJobs));
            }
        }
        return MathF.Max(0.25f, outputMul * laborRelief);
    }

    /// <summary>
    /// Applies per-method extra input bills, energy scaling, fuel demand reductions (e.g. Clean-Grid
    /// cutting Coal demand), and bonus byproduct outputs for one region.
    /// Returns the extra gross market value produced by bonus outputs.
    /// </summary>
    internal static float ApplyRegionMethodDeltas(Nation nation, ScenarioState state, float outputFactor, Defines defines)
    {
        if (nation?.ActiveProductionMethods is not { Count: > 0 } active || state is null || outputFactor <= 0f)
            return 0f;

        var bonusValue = 0f;
        foreach (var entry in active)
        {
            if (!ById.TryGetValue(entry.Value, out var method) || method.IsDefault) continue;
            var count = state.Buildings.GetValueOrDefault(method.Building);
            if (count <= 0) continue;

            var staffed = count * outputFactor;

            if (method.EnergyMultiplier != 1f && nation.Market.TryGetValue(Commodity.Energy, out var energyQuote))
            {
                var baseEnergy = BaseEnergyLoad(method.Building);
                var deltaEnergy = staffed * baseEnergy * (method.EnergyMultiplier - 1f);
                energyQuote.Demand = MathF.Max(0f, energyQuote.Demand + deltaEnergy);
            }

            var extraInputs = method.ExtraInputs;
            for (var i = 0; i < extraInputs.Length; i++)
            {
                var (good, perBuilding) = extraInputs[i];
                if (nation.Market.TryGetValue(good, out var quote))
                    quote.Demand += staffed * perBuilding;
            }

            var scales = method.DemandScales;
            for (var i = 0; i < scales.Length; i++)
            {
                var (good, scale) = scales[i];
                var baseLoad = BaseFuelLoad(method.Building, good);
                if (baseLoad <= 0f || !nation.Market.TryGetValue(good, out var quote)) continue;
                var delta = staffed * baseLoad * (scale - 1f);
                quote.Demand = MathF.Max(0f, quote.Demand + delta);
            }

            var bonuses = method.BonusOutputs;
            if (bonuses.Length > 0)
            {
                var supply = SupplyFactor(nation, method.Needs);
                for (var i = 0; i < bonuses.Length; i++)
                {
                    var (good, perBuilding) = bonuses[i];
                    if (!nation.Market.TryGetValue(good, out var quote)) continue;
                    var produced = MathF.Max(0f,
                        staffed * perBuilding * supply * ProductionChains.SupplyResponse(nation, quote, defines));
                    quote.Production += produced;
                    bonusValue += produced * MathF.Max(0f, quote.Price);
                }
            }
        }
        return bonusValue;
    }

    /// <summary>Total building job demand in <paramref name="state"/> adjusted by active production methods.</summary>
    public static float EffectiveJobs(Nation nation, ScenarioState state)
    {
        if (state is null) return 0f;
        var jobs = 0f;
        foreach (var (kind, count) in state.Buildings)
        {
            if (count <= 0) continue;
            var baseJobs = BuildingCatalog.Get(kind).Jobs;
            if (baseJobs <= 0f) continue;
            var method = ActiveFor(nation, kind);
            jobs += count * baseJobs * (method?.JobsMultiplier ?? 1f);
        }
        return jobs;
    }

    private static float SupplyFactor(Nation nation, (Commodity Good, float Floor)[] needs)
    {
        if (needs is null || needs.Length == 0) return 1f;
        var factor = 1f;
        for (var i = 0; i < needs.Length; i++)
        {
            var (good, floor) = needs[i];
            var sat = nation.Market.TryGetValue(good, out var quote) ? M.Clamp01(quote.Satisfaction) : 1f;
            factor *= floor + (1f - floor) * sat;
        }
        return factor;
    }

    private static float BaseEnergyLoad(BuildingKind kind) => kind switch
    {
        BuildingKind.CivilianIndustry => 0.814f,
        BuildingKind.Smelter => 1.60f,
        BuildingKind.ChemicalPlant => 0.80f,
        BuildingKind.ElectronicsPlant => 0.60f,
        BuildingKind.Refinery => 0.45f,
        BuildingKind.ArmsIndustry => 0.50f,
        BuildingKind.ExtractionIndustry => 0.35f,
        BuildingKind.Agriculture => 0.20f,
        _ => 0.30f
    };

    private static float BaseFuelLoad(BuildingKind kind, Commodity good) => (kind, good) switch
    {
        (BuildingKind.PowerPlant, Commodity.Coal) => 2.129f,
        (BuildingKind.PowerPlant, Commodity.NaturalGas) => 0.836f,
        (BuildingKind.Smelter, Commodity.Coal) => 0.601f,
        (BuildingKind.CivilianIndustry, Commodity.Coal) => 1.302f,
        (BuildingKind.Refinery, Commodity.Oil) => 1.00f,
        _ => 0.25f
    };

    /// <summary>
    /// Deterministic AI evaluator run monthly (staggered by <c>nation.Id % 30</c>, with a 180-day
    /// hysteresis cooldown) so AI nations modernize production methods when their energy, machinery,
    /// and tech supply chains can actually sustain them, and step back if cut off by embargoes.
    /// </summary>
    public static void EvaluateAi(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.IsHuman || nation.Landless || nation.Population <= 0)
            return;
        if (campaign.ElapsedDays < 28) return;
        var week = campaign.ElapsedDays / 7;
        if ((week + nation.Id) % 4 != 0) return;
        if (nation.LastProductionMethodAiDay >= 0 && campaign.ElapsedDays - nation.LastProductionMethodAiDay < 180)
            return;
        if (ByBuilding.Count == 0) return;

        var states = campaign.StatesOf(nation);
        if (states.Length == 0) return;

        foreach (var pair in ByBuilding.OrderBy(p => (int)p.Key))
        {
            var kind = pair.Key;
            var methods = pair.Value;
            if (methods.Count <= 1) continue;

            var buildingCount = 0;
            for (var s = 0; s < states.Length; s++)
                buildingCount += states[s].Buildings.GetValueOrDefault(kind);
            if (buildingCount <= 0) continue;

            var current = ActiveFor(nation, kind);
            var currentScore = ScoreForAi(nation, current);
            ProductionMethodDefinition best = current;
            var bestScore = currentScore;

            for (var i = 0; i < methods.Count; i++)
            {
                var candidate = methods[i];
                if (ReferenceEquals(candidate, current)) continue;
                if (!CanUse(campaign, nation, candidate, out _)) continue;
                var score = ScoreForAi(nation, candidate);
                if (score > bestScore + 0.10f)
                {
                    bestScore = score;
                    best = candidate;
                }
            }

            if (best is not null && !ReferenceEquals(best, current))
            {
                new SetProductionMethodCommand
                {
                    IssuerId = nation.Id,
                    BuildingId = BuildingCatalog.IdOf(kind),
                    MethodId = best.Id
                }.Apply(campaign);
                nation.LastProductionMethodAiDay = campaign.ElapsedDays;
                return; // At most one industrial transition per cooldown window for smooth macro dynamics.
            }
        }
    }

    private static float ScoreForAi(Nation nation, ProductionMethodDefinition method)
    {
        if (method is null) return 0f;
        var supply = SupplyFactor(nation, method.Needs);
        var score = (method.OutputMultiplier - 1f) * 1.40f * supply;

        // Value labor savings when the workforce is tight.
        if (method.JobsMultiplier < 1f)
        {
            var tightness = MathF.Max(0f, 0.94f - nation.EmployedShare);
            score += (1f - method.JobsMultiplier) * (0.25f + tightness * 2.2f);
        }

        // Penalize extra energy load if the grid is strained.
        if (method.EnergyMultiplier > 1f && nation.Market.TryGetValue(Commodity.Energy, out var energy))
        {
            var deficit = MathF.Max(0f, 0.95f - M.Clamp01(energy.Satisfaction));
            score -= (method.EnergyMultiplier - 1f) * (0.18f + deficit * 1.8f);
        }

        // Penalize extra inputs whose market is tight or expensive.
        for (var i = 0; i < method.ExtraInputs.Length; i++)
        {
            var (good, amount) = method.ExtraInputs[i];
            if (!nation.Market.TryGetValue(good, out var quote)) continue;
            var basePrice = MathF.Max(0.001f, CommodityCatalog.Get(good).BasePrice);
            var priceRatio = quote.Price / basePrice;
            var shortage = MathF.Max(0f, 0.92f - M.Clamp01(quote.Satisfaction));
            score -= MathF.Min(0.35f, amount * (shortage * 1.6f + MathF.Max(0f, priceRatio - 1.25f) * 0.25f));
        }

        // Reward cutting scarce/expensive fuels (e.g. Clean-Grid or Electric Arc Furnace when coal is short).
        for (var i = 0; i < method.DemandScales.Length; i++)
        {
            var (good, scale) = method.DemandScales[i];
            if (scale >= 1f || !nation.Market.TryGetValue(good, out var quote)) continue;
            var basePrice = MathF.Max(0.001f, CommodityCatalog.Get(good).BasePrice);
            var shortage = MathF.Max(0f, 0.95f - M.Clamp01(quote.Satisfaction));
            var pricePremium = MathF.Max(0f, quote.Price / basePrice - 1.10f);
            score += (1f - scale) * (0.06f + shortage * 0.85f + pricePremium * 0.20f);
        }

        // Value bonus outputs at current prices.
        for (var i = 0; i < method.BonusOutputs.Length; i++)
        {
            var (good, amount) = method.BonusOutputs[i];
            if (!nation.Market.TryGetValue(good, out var quote)) continue;
            var shortageBoost = 1f + MathF.Max(0f, 1f - M.Clamp01(quote.Satisfaction));
            score += amount * MathF.Min(0.30f, quote.Price * 2.0f) * shortageBoost * supply;
        }

        return score;
    }
}

/// <summary>
/// Authoritative command to switch a nation's production method for a building class.
/// Used identically by players and AI so multiplayer/replays stay 100% deterministic.
/// </summary>
public sealed class SetProductionMethodCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public string BuildingId { get; init; } = "";
    public string MethodId { get; init; } = "";

    public void Apply(Campaign campaign)
    {
        if (campaign is null || !MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null || nation.Landless || nation.Population <= 0) return;
        if (!BuildingCatalog.TryParse(BuildingId, out var building)) return;
        var method = ProductionMethods.Find(MethodId);
        if (method is null || method.Building != building) return;
        if (!ProductionMethods.CanUse(campaign, nation, method, out _)) return;

        var canonicalBuilding = BuildingCatalog.IdOf(building);
        nation.ActiveProductionMethods ??= new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var current = ProductionMethods.ActiveFor(nation, building);
        if (current is not null && string.Equals(current.Id, method.Id, StringComparison.OrdinalIgnoreCase))
            return;

        if (method.IsDefault)
            nation.ActiveProductionMethods.Remove(canonicalBuilding);
        else
            nation.ActiveProductionMethods[canonicalBuilding] = method.Id;

        campaign.Chronicle.Record(campaign, "production_method_switched", nation.Id,
            subject: BuildingCatalog.Get(building).Name, place: method.Name, isPublic: false);
    }
}
