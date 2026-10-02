using System;
using System.Collections.Generic;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum ProductionPool { Civilian, Military }
public sealed class ProductionLine
{
    public Commodity Good;
    public int Units = -1; // -1 only in legacy percentage saves/commands.
    public float Share; // Read compatibility; new commands always write Units.
}
public sealed partial class Nation
{
    public float ArmsFocus; // Legacy saves and AI policy input.
    public int ConvertedCivilianUnits = -1;
    public List<ProductionLine> ProductionLines = new();
}

/// <summary>Effective allocation. Factory losses suspend lower-priority units;
/// saved requests survive, so rebuilding restores rather than multiplies the programme.</summary>
public sealed class ProductionAllocation
{
    public int CivilianFactories { get; internal set; }
    public int MilitaryFactories { get; internal set; }
    public int RawCivilian { get; internal set; }
    public int Converted { get; internal set; }
    public int ConvertedMilitary { get; internal set; }
    public int Civilian => RawCivilian - Converted;
    public int Military { get; internal set; }
    public int CivilianUsed { get; internal set; }
    public int MilitaryUsed { get; internal set; }
    private readonly Dictionary<Commodity, int> _assigned = new();
    public int Capacity(ProductionPool pool) => pool == ProductionPool.Civilian ? Civilian : Military;
    public int Free(ProductionPool pool) => Capacity(pool) - (pool == ProductionPool.Civilian ? CivilianUsed : MilitaryUsed);
    public int Assigned(Commodity good) => _assigned.GetValueOrDefault(good);
    internal void Assign(Commodity good, int units)
    {
        _assigned[good] = units;
        if (ProductionProgramme.PoolOf(good) == ProductionPool.Civilian) CivilianUsed += units;
        else MilitaryUsed += units;
    }
    public float Fraction(Commodity good, ProductionPool pool)
    {
        var capacity = Capacity(pool);
        if (capacity <= 0) return 0f;
        return (ProductionProgramme.PoolOf(good) == pool && _assigned.TryGetValue(good, out var units)
            ? units : Free(pool)) / (float)capacity;
    }
}

public static class ProductionProgramme
{
    public static int UnitsPerFactory { get; private set; } = 10;
    public static float ConversionEfficiency { get; private set; } = .35f;
    private static readonly Dictionary<Commodity, ProductionPool> Pools = new();
    private static readonly List<Commodity> Order = new();
    public static IReadOnlyList<Commodity> Lineable => Order;
    public static bool IsLineable(Commodity good) => Pools.ContainsKey(good);
    public static ProductionPool PoolOf(Commodity good) => Pools.GetValueOrDefault(good);
    static ProductionProgramme() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        UnitsPerFactory = 10; ConversionEfficiency = .35f;
        Pools.Clear(); Order.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "programme"))
            using (document) Merge(document.RootElement, errors);
    }
    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object)
        { errors?.Add("Production programme must be an object."); return; }
        if (root.TryGetProperty("units_per_factory", out var units))
        {
            if (units.ValueKind == JsonValueKind.Number && units.TryGetInt32(out var count) && count is >= 1 and <= 1000)
                UnitsPerFactory = count;
            else errors?.Add("units_per_factory must be an integer from 1 to 1000.");
        }
        if (root.TryGetProperty("conversion", out var conversion))
        {
            if (conversion.ValueKind != JsonValueKind.Object) errors?.Add("Production conversion must be an object.");
            else if (conversion.TryGetProperty("efficiency", out var efficiency))
            {
                if (efficiency.ValueKind == JsonValueKind.Number && efficiency.TryGetSingle(out var value) &&
                    float.IsFinite(value) && value is >= .05f and <= 1f) ConversionEfficiency = value;
                else errors?.Add("Conversion efficiency must be a finite number from 0.05 to 1.");
            }
        }
        if (!root.TryGetProperty("lines", out var lines)) return;
        if (lines.ValueKind != JsonValueKind.Object)
        { errors?.Add("Production lines must be an object keyed by good ID."); return; }
        foreach (var entry in lines.EnumerateObject())
        {
            if (!CommodityCatalog.TryParse(entry.Name, out var good))
            { errors?.Add($"Programme line '{entry.Name}' names an unknown good."); continue; }
            var pool = Pools.GetValueOrDefault(good, good == Commodity.Arms ? ProductionPool.Military : ProductionPool.Civilian);
            if (entry.Value.ValueKind != JsonValueKind.Object)
            { errors?.Add($"Programme line '{entry.Name}' must be an object."); continue; }
            if (entry.Value.TryGetProperty("pool", out var declared))
            {
                var name = declared.ValueKind == JsonValueKind.String ? declared.GetString() : "";
                if (name == "civilian") pool = ProductionPool.Civilian;
                else if (name == "military") pool = ProductionPool.Military;
                else { errors?.Add($"Programme line '{entry.Name}': pool must be civilian or military."); continue; }
            }
            if (!Pools.ContainsKey(good)) Order.Add(good);
            Pools[good] = pool;
        }
    }
    public static void Merge(string json, List<string> errors = null)
    {
        using var document = JsonDocument.Parse(json, GameDataLoader.ForgivingJson);
        Merge(document.RootElement, errors);
    }
    public static void Validate(JsonElement root, List<string> problems)
    {
        var units = UnitsPerFactory; var efficiency = ConversionEfficiency;
        var pools = new Dictionary<Commodity, ProductionPool>(Pools); var order = Order.ToArray();
        try { Merge(root, problems); }
        finally
        {
            UnitsPerFactory = units; ConversionEfficiency = efficiency;
            Pools.Clear(); foreach (var pair in pools) Pools.Add(pair.Key, pair.Value);
            Order.Clear(); Order.AddRange(order);
        }
    }
    private static int FromShare(float share, int capacity) => float.IsFinite(share)
        ? (int)Math.Round(Math.Clamp((double)share, 0, 1) * capacity, MidpointRounding.AwayFromZero) : 0;
    private static int Capacity(long factories) => (int)Math.Clamp(factories * UnitsPerFactory, 0, int.MaxValue / 2);
    public static ProductionAllocation Allocate(Campaign campaign, Nation nation) => Allocate(campaign.StatesOf(nation), nation);
    public static ProductionAllocation Allocate(IEnumerable<ScenarioState> states, Nation nation)
    {
        long civilian = 0, military = 0;
        foreach (var state in states)
        {
            civilian += Math.Max(0, state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry));
            military += Math.Max(0, state.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry));
        }
        var result = new ProductionAllocation
        {
            CivilianFactories = (int)Math.Min(int.MaxValue, civilian), MilitaryFactories = (int)Math.Min(int.MaxValue, military),
            RawCivilian = Capacity(civilian)
        };
        result.Converted = nation.ConvertedCivilianUnits < 0 ? FromShare(nation.ArmsFocus, result.RawCivilian)
            : Math.Clamp(nation.ConvertedCivilianUnits, 0, result.RawCivilian);
        // Decimal avoids 1000 * 0.35f becoming 349.999994 and losing a whole unit.
        result.ConvertedMilitary = (int)decimal.Floor(result.Converted * (decimal)ConversionEfficiency);
        result.Military = Capacity(military) + result.ConvertedMilitary;
        var seen = new HashSet<Commodity>();
        foreach (var line in nation.ProductionLines)
        {
            if (line is null || !IsLineable(line.Good) || !seen.Add(line.Good)) continue;
            var pool = PoolOf(line.Good);
            var requested = line.Units == -1 ? FromShare(line.Share, result.Capacity(pool)) : Math.Max(0, line.Units);
            result.Assign(line.Good, Math.Min(requested, result.Free(pool)));
        }
        return result;
    }
    /// <summary>Migrate once, not every time another factory is constructed. Zero lines are intentional.</summary>
    public static void MigrateLegacy(Nation nation, ProductionAllocation allocation)
    {
        if (nation.ConvertedCivilianUnits < 0) nation.ConvertedCivilianUnits = allocation.Converted;
        foreach (var line in nation.ProductionLines)
            if (line is not null && line.Units == -1) { line.Units = allocation.Assigned(line.Good); line.Share = 0; }
    }
}
public sealed class SetProductionProgrammeCommand : ICampaignCommand
{
    public int IssuerId { get; init; }
    public float ArmsFocus { get; init; }
    public int ConvertedCivilianUnits { get; init; } = -1;
    public List<ProductionLine> Lines { get; init; }
    public void Apply(Campaign campaign)
    {
        if (!MechanicFeatures.Allows(campaign, this)) return;
        var nation = campaign.NationById(IssuerId);
        if (nation is null) return;
        // Never retain a network caller's mutable list.
        var order = new Nation { ArmsFocus = ArmsFocus, ConvertedCivilianUnits = ConvertedCivilianUnits,
            ProductionLines = Lines ?? new() };
        var allocation = ProductionProgramme.Allocate(campaign.StatesOf(nation), order);
        nation.ConvertedCivilianUnits = allocation.Converted;
        nation.ArmsFocus = allocation.RawCivilian == 0 ? 0 : allocation.Converted / (float)allocation.RawCivilian;
        var normalized = new List<ProductionLine>(); var seen = new HashSet<Commodity>();
        foreach (var line in order.ProductionLines)
            if (line is not null && ProductionProgramme.IsLineable(line.Good) && seen.Add(line.Good))
                normalized.Add(new ProductionLine { Good = line.Good, Units = allocation.Assigned(line.Good) });
        nation.ProductionLines = normalized;
    }
}
