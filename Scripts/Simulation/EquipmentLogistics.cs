using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

public sealed partial class Defines
{
    public bool EquipmentReplenishmentEnabled { get; set; } = true;
    public float EquipmentReplenishmentPerDay { get; set; } = .02f;
    public float EquipmentCombatDeliveryShare { get; set; } = .4f;
    public float EquipmentMinimumSupply { get; set; } = .22f;
}

public sealed partial class Nation
{
    // Presentation of the last depot pass, like DailyStockpileBurn. Not replay state:
    // only Equipment and the physically debited Stockpiles affect future simulation.
    [JsonIgnore] public readonly Dictionary<string, float> DailyEquipmentReplacement = new(StringComparer.OrdinalIgnoreCase);
}

/// <summary>Durable kit from existing strategic depots, independently of personnel replacements.</summary>
public static class EquipmentLogistics
{
    public static readonly string[] Goods = { "Arms", "MotorVehicles", "Steel", "Electronics" };

    private sealed class Request
    {
        public Army Army;
        public float[] Kit;
        public double Left, Given;
    }

    /// <summary>Read-only daily ceiling. No seeding, random draw, spending or personnel change.</summary>
    public static float DailyLimit(Campaign c, Army army)
    {
        if (Blocker(c, army).Length > 0) return 0f;
        var n = c.NationById(army.NationId);
        var funding = Math.Clamp(n.MilitarySpending / MathF.Max(.01f, c.Defines.BaselineMilitarySpending), 0f, 2.5f);
        var limit = Math.Max(0, c.Defines.EquipmentReplenishmentPerDay) * funding *
            Math.Clamp(army.SupplyLevel, 0f, 1f) * ReinforcementDoctrine.Weight(army.ReinforcementPriority) *
            (army.InBattle ? Math.Clamp(c.Defines.EquipmentCombatDeliveryShare, 0f, 1f) : 1f);
        return float.IsFinite(limit) ? Math.Min(1f - army.Equipment, limit) : 0f;
    }

    private static string Blocker(Campaign c, Army army)
    {
        if (c is null || !c.Defines.StrategicStockpilesEnabled || !c.Defines.EquipmentReplenishmentEnabled)
            return "Equipment replacements are disabled.";
        if (army is null || !float.IsFinite(army.Equipment) || !float.IsFinite(army.Divisions) ||
            army.Divisions <= 0 || army.Equipment >= 1f) return "Equipment establishment is complete.";
        var n = c.NationById(army.NationId);
        if (n is null || n.Population <= 0 || n.Movement is not null || n.MilitarySpending <= 0)
            return "No funded national equipment delivery.";
        if (army.EmbarkedFleetId >= 0) return "Equipment deliveries resume after landing.";
        if (army.CutOff || !float.IsFinite(army.SupplyLevel) || army.SupplyLevel < c.Defines.EquipmentMinimumSupply ||
            c.ProvinceMap is null || army.ProvinceId <= 0 || army.ProvinceId > c.ProvinceMap.ProvinceCount ||
            c.ProvinceMap.IsWater(army.ProvinceId) || MilitaryAccess.MustReturn(c, army))
            return "No supply route for equipment replacements.";
        return "";
    }

    private static float[] KitOf(Army army)
    {
        // Same template-dependent durable bill as recruitment; ammunition, food and
        // fuel are consumables, not the percentage of rifles/vehicles in a formation.
        var bill = StrategicStockpiles.DivisionMaterialBill(UnitTypeCatalog.Get(army.TemplateId), 1, MobilisationMode.Standard);
        return Goods.Select(g => Math.Max(0f, bill.GetValueOrDefault(g))).ToArray();
    }

    /// <summary>Called once by the daily depot pass, after ordinary military/naval upkeep.</summary>
    internal static void ReplenishDay(Campaign c)
    {
        if (!c.Defines.EquipmentReplenishmentEnabled) return;
        var kits = new Dictionary<string, float[]>(StringComparer.OrdinalIgnoreCase);
        var byNation = new Dictionary<int, List<Request>>();
        foreach (var army in c.Armies)
        {
            var limit = DailyLimit(c, army);
            if (limit <= 0f) continue;
            var key = army.TemplateId ?? "";
            if (!kits.TryGetValue(key, out var kit)) kits[key] = kit = KitOf(army);
            if (!kit.Any(v => v > 0f)) continue; // A malformed free bill cannot mint kit.
            if (!byNation.TryGetValue(army.NationId, out var requests)) byNation[army.NationId] = requests = new();
            requests.Add(new Request { Army = army, Kit = kit, Left = limit });
        }
        foreach (var (id, requests) in byNation)
        {
            var n = c.NationById(id);
            requests.Sort((a,b) => a.Army.Id.CompareTo(b.Army.Id));
            var stock = Goods.Select(g => (double)Math.Max(0f, n.Stockpiles.GetValueOrDefault(g))).ToArray();
            var demand = new double[Goods.Length];
            var spent = new double[Goods.Length];
            // Proportional allocation, not list order. Each round exhausts a limiting
            // material or completes demand. Four materials bound the pass. An absent
            // truck does not stop rifle deliveries to infantry that uses no trucks.
            for (var round = 0; round < Goods.Length; round++)
            {
                Array.Clear(demand); Array.Clear(spent);
                foreach (var r in requests)
                {
                    for (var g = 0; g < Goods.Length; g++)
                        if (r.Kit[g] > 0f && stock[g] <= 1e-8) { r.Left = 0; break; }
                    for (var g = 0; g < Goods.Length; g++) demand[g] += r.Left * r.Army.Divisions * r.Kit[g];
                }
                var progress = 0d;
                foreach (var r in requests)
                {
                    var share = 1d;
                    for (var g = 0; g < Goods.Length; g++)
                        if (r.Kit[g] > 0f && demand[g] > 0) share = Math.Min(share, stock[g] / demand[g]);
                    var given = r.Left * Math.Clamp(share, 0d, 1d);
                    r.Given += given; r.Left -= given; progress += given;
                    for (var g = 0; g < Goods.Length; g++) spent[g] += given * r.Army.Divisions * r.Kit[g];
                }
                for (var g = 0; g < Goods.Length; g++) stock[g] = Math.Max(0, stock[g] - spent[g]);
                if (progress <= 1e-10) break;
            }
            Array.Clear(spent);
            foreach (var r in requests)
            {
                var before = r.Army.Equipment;
                var after = Math.Min(1f, before + (float)r.Given);
                // Round delivered kit down, never above the allocated material bill.
                if ((double)after - before > r.Given) after = MathF.BitDecrement(after);
                var delta = Math.Max(0f, after - before);
                if (delta <= 0f) continue;
                r.Army.Equipment = after;
                for (var g = 0; g < Goods.Length; g++) spent[g] += delta * (double)r.Army.Divisions * r.Kit[g];
            }
            for (var g = 0; g < Goods.Length; g++)
            {
                if (spent[g] <= 0) continue;
                var good = Goods[g];
                n.Stockpiles[good] = (float)Math.Max(0d, n.Stockpiles.GetValueOrDefault(good) - spent[g]);
                n.DailyEquipmentReplacement[good] = (float)spent[g];
                n.DailyStockpileBurn[good] = n.DailyStockpileBurn.GetValueOrDefault(good) + (float)spent[g];
            }
        }
    }

    public static string Description(Campaign c, Army army)
    {
        var blocker = Blocker(c, army);
        if (blocker.Length > 0) return Loc.Get(blocker);
        var limit = DailyLimit(c, army);
        var n = c.NationById(army.NationId);
        var kit = KitOf(army);
        var missing = new List<string>();
        var cost = new List<string>();
        for (var g = 0; g < Goods.Length; g++)
        {
            if (kit[g] <= 0) continue;
            var good = Goods[g];
            if (n.Stockpiles.GetValueOrDefault(good) <= 0f) missing.Add(Loc.Get(good));
            cost.Add(Loc.Format("{0}: {1:0.###}", Loc.Get(good), kit[g] * army.Divisions * limit));
        }
        return Loc.Get("Equipment and personnel are replaced separately. Ammunition is a consumable, not equipment completeness.") + "\n" +
            (missing.Count > 0 ? Loc.Format("Equipment deliveries await depot materials: {0}.", string.Join(", ", missing)) :
                Loc.Format("Equipment delivery ceiling: +{0:0.0} percentage points/day; actual deliveries share depot stocks by reinforcement priority.", limit * 100f)) + "\n" +
            Loc.Format("Durable materials at that ceiling: {0}.", string.Join(" · ", cost)) +
            (army.InBattle ? "\n" + Loc.Get("Equipment deliveries continue in combat at a reduced rate.") : "");
    }
}
