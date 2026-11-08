using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json.Serialization;

namespace DealOrForce.Simulation;

/// <summary>
/// How a batch of divisions or warship hulls is raised:
///   - Standard (0): full training curriculum, standard procurement, 100% readiness on arrival.
///   - RapidCallUp (1): accelerated 40% training schedule, +35% treasury & material draw, slightly lower initial organisation/experience.
///   - TotalMobilisation (2): emergency 15% schedule (minimum 5 days for divisions, 21 days for ships),
///     +75% treasury & material draw, immediate stability & regional workforce/radicalism strain,
///     and dynamically unlocks massive single-order batch ceilings.
/// </summary>
public enum MobilisationMode
{
    Standard = 0,
    RapidCallUp = 1,
    TotalMobilisation = 2
}

public sealed partial class Defines
{
    public bool StrategicStockpilesEnabled { get; set; } = true;
    public float StockpileInitialWeeks { get; set; } = 12f;
    public float StockpileMaxWeeks { get; set; } = 36f;
    public float StockpileSurplusCaptureShare { get; set; } = 0.65f;
    public float StockpileMinimumTrickle { get; set; } = 0.25f;
    /// <summary>
    /// The defence industry an economy has without a named arms plant, in arms plants per
    /// square root of its GDP in billions - the same measure the depots' size is drawn from.
    /// The history files name the plants of the countries at war in 2024; without this every
    /// other country, the United States included, made no ammunition at all and its depots
    /// refilled at the trickle. 0.06 is about ten plants for the United States, four for
    /// Germany, under one for Ukraine.
    /// </summary>
    public float StockpileIndustryPerRootGdp { get; set; } = 0.06f;
    /// <summary>
    /// Whether the army's fuel reaches the depot from the national market. The market already
    /// carries and prices it (army_fuel_upkeep in common/production); without this the depot
    /// burnt it a second time and saw only the surplus after civilian demand, so a country that
    /// imports its fuel - Ukraine - ran dry in a peaceful market.
    /// </summary>
    public bool StockpileFuelFromMarket { get; set; } = true;

    public float StockpileDivisionDailyArms { get; set; } = 0.012f;
    public float StockpileDivisionCombatDailyAmmunition { get; set; } = 0.085f;
    public float StockpileDivisionCombatDailyArms { get; set; } = 0.035f;
    public float StockpileDivisionMarchDailyFuel { get; set; } = 0.030f;
    public float StockpileDivisionDailyRations { get; set; } = 0.015f;

    /// <summary>
    /// What a day short of a good costs the field, at a need wholly unmet; a partly met need
    /// costs that share of it. Arms wear the divisions' equipment down, ammunition costs the
    /// organisation of divisions in battle, fuel the supply of columns on the move, food the
    /// organisation and cohesion of everybody.
    /// </summary>
    public float StockpileShortArmsEquipmentLoss { get; set; } = 0.006f;
    /// <summary>
    /// Zero by default: a force short of shells already loses organisation faster in battle,
    /// because the munitions factor weakens its fire and a battle's organisation loss follows
    /// the fire each side takes. 0.035 on top of that, with the factor, broke Ukraine's line
    /// in its fourth month on every seed (2026-10-22).
    /// </summary>
    public float StockpileShortAmmunitionOrganisationLoss { get; set; } = 0f;
    public float StockpileShortOilSupplyLoss { get; set; } = 0.025f;
    public float StockpileShortFoodOrganisationLoss { get; set; } = 0.015f;
    public float StockpileShortFoodCohesionLoss { get; set; } = 0.010f;
    /// <summary>The share of its weight a force fights with when no ammunition, or no arms, reached it.</summary>
    public float StockpileDryAmmunitionCombat { get; set; } = 0.60f;
    public float StockpileDryArmsCombat { get; set; } = 0.75f;
    /// <summary>
    /// How much the arsenal - tanks, artillery, drones, aircraft, missiles - weighs in battle:
    /// at 1 a force none of it reached fights at 0.77 of its weight. 0 leaves it out of combat.
    /// </summary>
    public float StockpileArsenalCombatWeight { get; set; } = 1f;

    public float StockpileHullAtSeaDailyFuel { get; set; } = 0.018f;
    public float StockpileHullActionDailyAmmunition { get; set; } = 0.045f;
    public float StockpileHullDailySteel { get; set; } = 0.006f;

    public float RapidCallUpTimeFactor { get; set; } = 0.40f;
    public float RapidCallUpCostFactor { get; set; } = 1.35f;
    public float RapidCallUpMaterialFactor { get; set; } = 1.25f;
    public float RapidCallUpStabilityPerDivision { get; set; } = 0.002f;

    public float TotalMobilisationTimeFactor { get; set; } = 0.15f;
    public float TotalMobilisationCostFactor { get; set; } = 1.75f;
    public float TotalMobilisationMaterialFactor { get; set; } = 1.50f;
    public float TotalMobilisationStabilityPerDivision { get; set; } = 0.0045f;
}

public sealed partial class Nation
{
    /// <summary>Physical strategic depot reserves by commodity ID (e.g. "Arms", "Ammunition", "Oil", "Steel", "Food", "Electronics").</summary>
    public Dictionary<string, float> Stockpiles = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last day's total consumption by commodity ID, for UI readouts and burn-rate warnings.</summary>
    [JsonIgnore]
    public readonly Dictionary<string, float> DailyStockpileBurn = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Last day's depot refill by commodity ID.</summary>
    [JsonIgnore]
    public readonly Dictionary<string, float> DailyStockpileGain = new(StringComparer.OrdinalIgnoreCase);

    public bool StockpilesSeeded;

    /// <summary>
    /// The share of yesterday's need for a good that the depot and the day's deliveries could
    /// meet, for the goods that fell short; a good that is not listed was met in full. Saved,
    /// because the next battle is fought on it.
    /// </summary>
    public Dictionary<string, float> StockpileCoverage = new(StringComparer.OrdinalIgnoreCase);

    [JsonIgnore]
    public bool HasCustomStockpiles => StockpilesSeeded || Stockpiles.Count > 0;
}

public static class StrategicStockpiles
{
    public static readonly string[] TrackedGoods =
    {
        "Arms",
        "Ammunition",
        "Oil",
        "Steel",
        "MotorVehicles",
        "Electronics",
        "Food",
        "Tanks",
        "Artillery",
        "Drones",
        "CombatAircraft",
        "Missiles"
    };

    /// <summary>
    /// The arsenal (common/goods/20_military_industry.json) and how much of a force's weight
    /// each piece of it carries in battle: a force none of it reached fights at 1 minus the sum,
    /// times <see cref="Defines.StockpileArsenalCombatWeight"/>. Shells and rifles stay
    /// the heavy terms; this is what turns a long war into a war of factories as well.
    /// </summary>
    private static readonly (string Good, float Weight)[] Arsenal =
    {
        ("Artillery", 0.06f),
        ("Tanks", 0.05f),
        ("Drones", 0.05f),
        ("CombatAircraft", 0.04f),
        ("Missiles", 0.03f)
    };

    public static void EnsureSeeded(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || nation.StockpilesSeeded) return;
        nation.StockpilesSeeded = true;

        var baseScale = SeedScale(campaign, nation);
        SetIfMissing(nation, "Arms", baseScale * OpeningFactors["Arms"]);
        SetIfMissing(nation, "Ammunition", baseScale * OpeningFactors["Ammunition"]);
        SetIfMissing(nation, "Oil", baseScale * OpeningFactors["Oil"]);
        SetIfMissing(nation, "Steel", baseScale * OpeningFactors["Steel"]);
        SetIfMissing(nation, "MotorVehicles", baseScale * OpeningFactors["MotorVehicles"]);
        SetIfMissing(nation, "Electronics", baseScale * OpeningFactors["Electronics"]);
        SetIfMissing(nation, "Food", baseScale * OpeningFactors["Food"]);
        SeedArsenal(nation, baseScale);
    }

    /// <summary>
    /// The arsenal's opening depots - also for a campaign saved before the arsenal existed,
    /// whose depots were seeded without it and would otherwise go to its first battle empty.
    /// </summary>
    private static void EnsureArsenal(Campaign campaign, Nation nation)
    {
        if (!nation.Stockpiles.ContainsKey(Arsenal[0].Good) || !nation.Stockpiles.ContainsKey(Arsenal[^1].Good))
            SeedArsenal(nation, SeedScale(campaign, nation));
    }

    private static void SeedArsenal(Nation nation, float baseScale)
    {
        SetIfMissing(nation, "Tanks", baseScale * OpeningFactors["Tanks"]);
        SetIfMissing(nation, "Artillery", baseScale * OpeningFactors["Artillery"]);
        SetIfMissing(nation, "Drones", baseScale * OpeningFactors["Drones"]);
        SetIfMissing(nation, "CombatAircraft", baseScale * OpeningFactors["CombatAircraft"]);
        SetIfMissing(nation, "Missiles", baseScale * OpeningFactors["Missiles"]);
    }

    private static readonly Dictionary<string, float> OpeningFactors = new(StringComparer.OrdinalIgnoreCase)
    {
        ["Arms"] = 4.5f, ["Ammunition"] = 5f, ["Oil"] = 4.2f, ["Steel"] = 3.8f,
        ["MotorVehicles"] = 2.4f, ["Electronics"] = 1.8f, ["Food"] = 6f,
        ["Tanks"] = 2f, ["Artillery"] = 2.6f, ["Drones"] = 1.6f, ["CombatAircraft"] = 1.2f, ["Missiles"] = 1.4f
    };

    /// <summary>Same value Get would return, without seeding depots as a side effect
    /// of a tooltip, panel or strike forecast. Only the simulation initializes stores.</summary>
    public static float Peek(Campaign campaign, Nation nation, string goodId)
    {
        if (nation is null || string.IsNullOrEmpty(goodId)) return 0f;
        if (nation.Stockpiles.TryGetValue(goodId, out var stored)) return MathF.Max(0, stored);
        if (nation.StockpilesSeeded || campaign is null || !OpeningFactors.TryGetValue(goodId, out var factor)) return 0f;
        return MathF.Max(0, MathF.Round(SeedScale(campaign, nation) * factor, 2));
    }

    private static float SeedScale(Campaign campaign, Nation nation)
    {
        var divisions = 0f;
        foreach (var a in campaign.Armies)
            if (a.NationId == nation.Id) divisions += a.Divisions;
        var hulls = 0;
        foreach (var f in campaign.Fleets)
            if (f.NationId == nation.Id) hulls += f.Hulls;
        return MathF.Max(1.5f, divisions * 1.4f + hulls * 0.6f + MathF.Sqrt(MathF.Max(1f, nation.Gdp)) * 0.8f);
    }

    private static void SetIfMissing(Nation nation, string key, float amount)
    {
        if (!nation.Stockpiles.ContainsKey(key))
            nation.Stockpiles[key] = MathF.Round(amount, 2);
    }

    public static float Get(Campaign campaign, Nation nation, string goodId)
    {
        if (nation is null || string.IsNullOrEmpty(goodId)) return 0f;
        if (!nation.StockpilesSeeded && campaign is not null) EnsureSeeded(campaign, nation);
        return MathF.Max(0f, nation.Stockpiles.GetValueOrDefault(goodId));
    }

    public static float Capacity(Campaign campaign, Nation nation, string goodId)
    {
        if (nation is null) return 10f;
        var barracks = 0;
        var ports = 0;
        var infra = 0;
        var armsPlants = 0;
        if (campaign is not null)
            foreach (var st in campaign.StatesOf(nation))
            {
                barracks += st.Buildings.GetValueOrDefault(BuildingKind.Barracks);
                ports += st.Buildings.GetValueOrDefault(BuildingKind.Port) + st.Buildings.GetValueOrDefault(BuildingKind.Shipyard);
                infra += st.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
                armsPlants += st.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry);
            }
        var baseCap = 12f + barracks * 6f + armsPlants * 10f + ports * 4f + infra * 2.5f + MathF.Sqrt(MathF.Max(1f, nation.Gdp)) * 3.5f;
        return goodId switch
        {
            "Ammunition" or "Food" => baseCap * 1.35f,
            "Arms" or "Oil" or "Steel" => baseCap * 1.15f,
            _ => baseCap * 0.85f
        };
    }

    public static float FillRatio(Campaign campaign, Nation nation, string goodId)
    {
        var cap = MathF.Max(1f, Capacity(campaign, nation, goodId));
        return M.Clamp01(Get(campaign, nation, goodId) / cap);
    }

    /// <summary>
    /// Material cost per division of a template, by commodity ID.
    /// Raising divisions immediately draws down physical stockpiles; if depots are lower than the
    /// requirement, the remainder must be procured at an emergency import premium.
    /// </summary>
    public static Dictionary<string, float> DivisionMaterialBill(UnitTypeDefinition template, int count, MobilisationMode mode)
    {
        var bill = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (template is null || count <= 0) return bill;
        var mult = mode switch
        {
            MobilisationMode.RapidCallUp => 1.25f,
            MobilisationMode.TotalMobilisation => 1.50f,
            _ => 1f
        };
        var id = template.Id.ToLowerInvariant();
        var arms = 1.2f;
        var ammo = 0.8f;
        var steel = 0.4f;
        var vehicles = 0f;
        var oil = 0.2f;
        var electronics = 0f;
        var food = 0.8f;

        if (id.Contains("armored") || template.Hardness >= 0.65f)
        {
            arms = 2.2f; ammo = 1.6f; steel = 2.8f; vehicles = 2.4f; oil = 1.8f; electronics = 1.2f;
        }
        else if (id.Contains("mechanized") || template.Hardness >= 0.35f)
        {
            arms = 1.9f; ammo = 1.4f; steel = 1.8f; vehicles = 1.9f; oil = 1.3f; electronics = 0.8f;
        }
        else if (id.Contains("motorized") || template.Speed >= 1.15f)
        {
            arms = 1.4f; ammo = 1.0f; steel = 0.9f; vehicles = 1.5f; oil = 1.0f; electronics = 0.3f;
        }
        else if (id.Contains("artillery") || template.SoftAttack >= 1.6f)
        {
            arms = 2.0f; ammo = 2.4f; steel = 1.5f; vehicles = 0.6f; oil = 0.5f; electronics = 0.4f;
        }
        else if (id.Contains("marine") || template.Amphibious >= 0.5f)
        {
            arms = 1.6f; ammo = 1.2f; steel = 0.7f; electronics = 0.5f;
        }

        void Add(string key, float perDiv)
        {
            var total = MathF.Round(perDiv * count * mult, 2);
            if (total > 0f) bill[key] = total;
        }
        Add("Arms", arms);
        Add("Ammunition", ammo);
        Add("Steel", steel);
        Add("MotorVehicles", vehicles);
        Add("Oil", oil);
        Add("Electronics", electronics);
        Add("Food", food);
        return bill;
    }

    public static Dictionary<string, float> HullMaterialBill(int hulls, MobilisationMode mode)
    {
        var bill = new Dictionary<string, float>(StringComparer.OrdinalIgnoreCase);
        if (hulls <= 0) return bill;
        var mult = mode switch
        {
            MobilisationMode.RapidCallUp => 1.25f,
            MobilisationMode.TotalMobilisation => 1.50f,
            _ => 1f
        };
        bill["Steel"] = MathF.Round(2.2f * hulls * mult, 2);
        bill["Arms"] = MathF.Round(1.1f * hulls * mult, 2);
        bill["Electronics"] = MathF.Round(0.95f * hulls * mult, 2);
        bill["Oil"] = MathF.Round(0.85f * hulls * mult, 2);
        bill["Ammunition"] = MathF.Round(0.75f * hulls * mult, 2);
        return bill;
    }

    /// <summary>
    /// Read-only emergency import surcharge if physical stockpiles do not cover the full bill.
    /// Allows the player to still mobilise by buying missing equipment on the emergency market at a steep price,
    /// while draining whatever is in the depot to zero.
    /// </summary>
    public static float SurchargeForMissingStockpile(Campaign campaign, Nation nation, Dictionary<string, float> bill, out List<string> shortages)
    {
        shortages = new List<string>();
        if (campaign is null || nation is null || bill is null) return 0f;
        var surcharge = 0f;
        foreach (var (goodId, needed) in bill)
        {
            var have = Peek(campaign, nation, goodId);
            if (have >= needed) continue;
            var missing = needed - have;
            var price = CommodityCatalog.TryParse(goodId, out var comm) && nation.Market.TryGetValue(comm, out var quote) && quote.Price > 0f
                ? quote.Price
                : 0.06f;
            // Emergency off-market procurement is expensive
            surcharge += missing * price * 2.2f;
            shortages.Add($"{Loc.Get(goodId)}: {have:0.#}/{needed:0.#}");
        }
        return MathF.Round(surcharge, 3);
    }

    public static void ConsumeBill(Campaign campaign, Nation nation, Dictionary<string, float> bill)
    {
        if (campaign is null || nation is null || bill is null) return;
        if (!nation.StockpilesSeeded) EnsureSeeded(campaign, nation);
        foreach (var (goodId, needed) in bill)
        {
            var have = Get(campaign, nation, goodId);
            nation.Stockpiles[goodId] = MathF.Max(0f, MathF.Round(have - needed, 3));
        }
    }

    /// <summary>
    /// Daily stockpile refill from national production & imports, and daily draw by field armies,
    /// active battles, marching motorized/armored columns, and fleets at sea.
    /// When a depot runs dry, the nation's forces suffer real supply, organisation, speed and combat penalties!
    /// </summary>
    public static void TickDay(Campaign campaign)
    {
        if (campaign is null || !campaign.Defines.StrategicStockpilesEnabled) return;
        var defines = campaign.Defines;

        // Pre-tally per nation: total divisions, marching divisions, fighting divisions, hulls at sea, hulls in action
        var totalDivs = new Dictionary<int, float>();
        var marchingMobile = new Dictionary<int, float>();
        var fightingDivs = new Dictionary<int, float>();
        var seaHulls = new Dictionary<int, int>();
        var actionHulls = new Dictionary<int, int>();

        foreach (var army in campaign.Armies)
        {
            if (army.Divisions <= 0f) continue;
            totalDivs[army.NationId] = totalDivs.GetValueOrDefault(army.NationId) + army.Divisions;
            if (army.InBattle)
                fightingDivs[army.NationId] = fightingDivs.GetValueOrDefault(army.NationId) + army.Divisions;
            if (army.IsMoving)
            {
                var tmpl = UnitTypeCatalog.Get(army.TemplateId);
                var fuelWeight = tmpl.Hardness > 0.1f || tmpl.Speed > 1.05f ? 1.8f : 0.45f;
                marchingMobile[army.NationId] = marchingMobile.GetValueOrDefault(army.NationId) + army.Divisions * fuelWeight;
            }
        }

        foreach (var fleet in campaign.Fleets)
        {
            if (fleet.Hulls <= 0) continue;
            if (!fleet.InPort)
                seaHulls[fleet.NationId] = seaHulls.GetValueOrDefault(fleet.NationId) + fleet.Hulls;
            if (fleet.InAction)
                actionHulls[fleet.NationId] = actionHulls.GetValueOrDefault(fleet.NationId) + fleet.Hulls;
        }

        foreach (var nation in campaign.Nations)
        {
            if (nation.Population <= 0 || nation.Movement is not null) continue;
            if (!nation.StockpilesSeeded) EnsureSeeded(campaign, nation);
            else EnsureArsenal(campaign, nation);
            nation.DailyStockpileBurn.Clear();
            nation.DailyStockpileGain.Clear();
            nation.DailyEquipmentReplacement.Clear();

            var divs = totalDivs.GetValueOrDefault(nation.Id);
            var fighting = fightingDivs.GetValueOrDefault(nation.Id);
            var marching = marchingMobile.GetValueOrDefault(nation.Id);
            var afloat = seaHulls.GetValueOrDefault(nation.Id);
            var navalCombat = actionHulls.GetValueOrDefault(nation.Id);

            // 1. Refill from national production and imports (1/7th of weekly output + war industry bonus)
            var plants = PlantsOf(campaign, nation);

            foreach (var goodId in TrackedGoods)
            {
                var cap = Capacity(campaign, nation, goodId);
                var current = Get(campaign, nation, goodId);
                var gain = Intake(campaign, nation, goodId, plants);

                // Burn calculation
                var burn = goodId switch
                {
                    "Arms" => divs * defines.StockpileDivisionDailyArms + fighting * defines.StockpileDivisionCombatDailyArms,
                    "Ammunition" => divs * 0.004f + fighting * defines.StockpileDivisionCombatDailyAmmunition + navalCombat * defines.StockpileHullActionDailyAmmunition,
                    "Oil" => marching * defines.StockpileDivisionMarchDailyFuel + fighting * 0.025f + afloat * defines.StockpileHullAtSeaDailyFuel,
                    "Steel" => fighting * 0.018f + afloat * defines.StockpileHullDailySteel + navalCombat * 0.025f,
                    "MotorVehicles" => marching * 0.008f + fighting * 0.014f,
                    "Electronics" => fighting * 0.010f + navalCombat * 0.015f,
                    "Food" => divs * defines.StockpileDivisionDailyRations + afloat * 0.005f,
                    // The arsenal is spent in battle: armour and barrels wear out, drones and
                    // aircraft are shot down, and missiles go to the ships and the air defence.
                    "Tanks" => fighting * 0.010f,
                    "Artillery" => fighting * 0.014f,
                    "Drones" => fighting * 0.022f,
                    "CombatAircraft" => fighting * 0.004f + navalCombat * 0.004f,
                    "Missiles" => fighting * 0.006f + navalCombat * 0.012f,
                    _ => 0f
                };

                // The army's fuel is bought on the national market like everybody's: the share
                // of it the market met arrives, and the depot is what covers a market that
                // fails - a blockade, an embargo, a refinery on fire.
                if (defines.StockpileFuelFromMarket && goodId == "Oil")
                    gain += burn * MarketMet(nation, goodId);

                nation.DailyStockpileGain[goodId] = gain;
                nation.DailyStockpileBurn[goodId] = burn;

                // An empty depot is a ration, not a silence: the guns fire what arrived that
                // morning, and only the part of the need nothing arrived for goes unmet.
                var available = current + gain;
                if (burn > 0f && available < burn) nation.StockpileCoverage[goodId] = MathF.Round(available / burn, 3);
                else nation.StockpileCoverage.Remove(goodId);

                var next = Math.Clamp(current + gain - burn, 0f, cap);
                nation.Stockpiles[goodId] = MathF.Round(next, 3);
            }

            // 2. The field pays for the part of the need that went unmet, in proportion to it.
            var armsShort = 1f - Coverage(nation, "Arms");
            var ammoShort = 1f - Coverage(nation, "Ammunition");
            var oilShort = 1f - Coverage(nation, "Oil");
            var foodShort = 1f - Coverage(nation, "Food");
            var electronicsShort = 1f - Coverage(nation, "Electronics");

            if (divs > 0f && (armsShort > 0f || ammoShort > 0f || oilShort > 0f || foodShort > 0f || electronicsShort > 0f))
            {
                foreach (var army in campaign.Armies)
                {
                    if (army.NationId != nation.Id || MilitaryAccess.MustReturn(campaign, army)) continue;
                    if (armsShort > 0f)
                        army.Equipment = MathF.Min(army.Equipment,
                            M.Clamp(army.Equipment - defines.StockpileShortArmsEquipmentLoss * armsShort, 0.20f, 1f));
                    if (ammoShort > 0f && army.InBattle)
                        army.Organisation = M.Clamp01(army.Organisation - defines.StockpileShortAmmunitionOrganisationLoss * ammoShort);
                    if (oilShort > 0f && army.IsMoving)
                        army.SupplyLevel = M.Clamp01(army.SupplyLevel - defines.StockpileShortOilSupplyLoss * oilShort);
                    if (foodShort > 0f)
                    {
                        army.Organisation = M.Clamp01(army.Organisation - defines.StockpileShortFoodOrganisationLoss * foodShort);
                        army.Cohesion = M.Clamp01(army.Cohesion - defines.StockpileShortFoodCohesionLoss * foodShort);
                    }
                    if (electronicsShort > 0f)
                        army.Recon = M.Clamp01(army.Recon - 0.01f * electronicsShort);
                }
            }

            if (afloat > 0 && (oilShort > 0f || ammoShort > 0f || electronicsShort > 0f))
            {
                foreach (var fleet in campaign.Fleets)
                {
                    if (fleet.NationId != nation.Id || fleet.InPort) continue;
                    if (oilShort > 0f)
                        fleet.Supply = M.Clamp01(fleet.Supply - 0.03f * oilShort);
                    if (ammoShort > 0f && fleet.InAction)
                        fleet.Organisation = M.Clamp01(fleet.Organisation - 0.04f * ammoShort);
                    if (electronicsShort > 0f && fleet.InAction)
                        fleet.Strength = M.Clamp01(fleet.Strength - 0.01f * electronicsShort);
                }
            }
        }
        EquipmentLogistics.ReplenishDay(campaign);
    }

    /// <summary>The share of yesterday's need for a good that was met, 0 to 1.</summary>
    public static float Coverage(Nation nation, string goodId) =>
        nation is not null && nation.StockpileCoverage.TryGetValue(goodId, out var met) ? M.Clamp01(met) : 1f;

    /// <summary>
    /// Whole days the depot still covers at yesterday's draw: stock divided by
    /// <see cref="Nation.DailyStockpileBurn"/>. Null when nothing is being drawn, so a quiet
    /// depot is not reported as an endless stock. Coverage is the share of a need already
    /// met; it is not a second clock. This only reads the two dictionaries.
    /// </summary>
    public static int? DaysOnHand(Nation nation, string goodId)
    {
        if (nation is null || string.IsNullOrEmpty(goodId)) return null;
        if (!nation.DailyStockpileBurn.TryGetValue(goodId, out var burn) ||
            !float.IsFinite(burn) || burn <= 0.00001f)
            return null;
        var stock = nation.Stockpiles.GetValueOrDefault(goodId);
        if (!float.IsFinite(stock) || stock <= 0f) return 0;
        var days = stock / burn;
        if (!float.IsFinite(days) || days <= 0f) return 0;
        if (days >= int.MaxValue) return int.MaxValue;
        return (int)MathF.Floor(days);
    }

    /// <summary>
    /// Combat effectiveness from the munitions that reached the guns: full while the depots
    /// meet the need, down to 0.60 of it for ammunition and 0.75 for arms when nothing came.
    /// A side rationed to half its shells fights at eight tenths, not at six.
    /// </summary>
    public static float CombatMunitionsFactor(Campaign campaign, Nation nation)
    {
        if (campaign is null || nation is null || !campaign.Defines.StrategicStockpilesEnabled) return 1f;
        if (!nation.StockpilesSeeded) return 1f;
        var dryAmmo = M.Clamp01(campaign.Defines.StockpileDryAmmunitionCombat);
        var dryArms = M.Clamp01(campaign.Defines.StockpileDryArmsCombat);
        var ammoFactor = dryAmmo + (1f - dryAmmo) * Coverage(nation, "Ammunition");
        var armsFactor = dryArms + (1f - dryArms) * Coverage(nation, "Arms");
        return ammoFactor * armsFactor * ArsenalFactor(campaign, nation);
    }

    /// <summary>The share of its weight a force fights with for the arsenal it was short of yesterday.</summary>
    public static float ArsenalFactor(Campaign campaign, Nation nation)
    {
        var weight = MathF.Max(0f, campaign.Defines.StockpileArsenalCombatWeight);
        if (weight <= 0f) return 1f;
        var lost = 0f;
        foreach (var (good, share) in Arsenal)
            lost += share * (1f - Coverage(nation, good));
        return MathF.Max(0.3f, 1f - weight * lost);
    }

    private static readonly string[] Shipped = { "Ammunition", "Arms", "Artillery", "Drones" };

    /// <summary>
    /// A backer's shipment from its own depots: this share of what its industry and market
    /// add to them a day, for each of the days since the last one, of ammunition, arms, guns and drones.
    /// Physical, not priced - the shells that reached Ukraine were drawn from the stocks of
    /// the countries that sent them. A backer short of a good itself sends none of it, and
    /// nothing goes past what the client's depots hold. Returns what was shipped.
    /// </summary>
    public static float Ship(Campaign campaign, Nation from, Nation to, float share, int days)
    {
        if (campaign is null || from is null || to is null || share <= 0f || days <= 0 ||
            !campaign.Defines.StrategicStockpilesEnabled) return 0f;
        if (!from.StockpilesSeeded) EnsureSeeded(campaign, from);
        if (!to.StockpilesSeeded) EnsureSeeded(campaign, to);
        var plants = PlantsOf(campaign, from);
        var shipped = 0f;
        foreach (var goodId in Shipped)
        {
            if (Coverage(from, goodId) < 1f) continue;
            var have = Get(campaign, from, goodId);
            var room = Capacity(campaign, to, goodId) - Get(campaign, to, goodId);
            var amount = MathF.Min(MathF.Min(have, room), Intake(campaign, from, goodId, plants) * M.Clamp01(share) * days);
            if (amount <= 0f) continue;
            from.Stockpiles[goodId] = MathF.Round(have - amount, 3);
            to.Stockpiles[goodId] = MathF.Round(Get(campaign, to, goodId) + amount, 3);
            shipped += amount;
        }
        return shipped;
    }

    /// <summary>The share of a good's demand the national market met this week, 0 to 1.</summary>
    private static float MarketMet(Nation nation, string goodId) =>
        CommodityCatalog.TryParse(goodId, out var comm) && nation.Market.TryGetValue(comm, out var quote)
            ? M.Clamp01(quote.Satisfaction)
            : 0f;

    private readonly record struct Plants(int Arms, int Refineries, int Smelters, int Farms, int Electronics);

    private static Plants PlantsOf(Campaign campaign, Nation nation)
    {
        int arms = 0, refineries = 0, smelters = 0, farms = 0, electronics = 0;
        foreach (var st in campaign.StatesOf(nation))
        {
            arms += st.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry);
            refineries += st.Buildings.GetValueOrDefault(BuildingKind.Refinery) + st.Buildings.GetValueOrDefault(BuildingKind.ExtractionIndustry);
            smelters += st.Buildings.GetValueOrDefault(BuildingKind.Smelter);
            farms += st.Buildings.GetValueOrDefault(BuildingKind.Agriculture);
            electronics += st.Buildings.GetValueOrDefault(BuildingKind.ElectronicsPlant);
        }
        return new Plants(arms, refineries, smelters, farms, electronics);
    }

    /// <summary>What a day of national production and imports adds to a depot.</summary>
    private static float Intake(Campaign campaign, Nation nation, string goodId, Plants plants)
    {
        var defines = campaign.Defines;
        var spendScale = M.Clamp(nation.MilitarySpending / MathF.Max(0.01f, defines.BaselineMilitarySpending), 0.35f, 2.5f);
        var gain = defines.StockpileMinimumTrickle * spendScale;
        var arms = plants.Arms + MathF.Sqrt(MathF.Max(0f, nation.Gdp)) * MathF.Max(0f, defines.StockpileIndustryPerRootGdp);
        if (CommodityCatalog.TryParse(goodId, out var comm) && nation.Market.TryGetValue(comm, out var quote))
        {
            var surplus = MathF.Max(0f, quote.Production + quote.Imports - quote.Demand * 0.72f);
            gain += (surplus / 7f) * defines.StockpileSurplusCaptureShare * quote.Satisfaction;
        }
        gain += goodId switch
        {
            "Arms" => arms * 0.14f * spendScale,
            "Ammunition" => arms * 0.18f * spendScale,
            "Oil" => plants.Refineries * 0.08f * spendScale,
            "Steel" => plants.Smelters * 0.12f * spendScale,
            "Electronics" => plants.Electronics * 0.10f * spendScale,
            "Food" => plants.Farms * 0.09f,
            _ => arms * 0.05f * spendScale
        };
        return gain;
    }
}
