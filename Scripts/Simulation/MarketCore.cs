using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

public enum Commodity
{
    Food,
    ConsumerGoods,
    IndustrialMaterials,
    Energy,
    Arms,
    Coal,
    Oil,
    IronOre,
    RareEarths,
    Machinery
}

public enum TradePolicy { OpenMarkets, BalancedTrade, StrategicAutarky }

public static class TradePolicyRules
{
    public static float ImportAccess(TradePolicy policy) => policy switch
    {
        TradePolicy.OpenMarkets => 1.18f,
        TradePolicy.StrategicAutarky => 0.58f,
        _ => 1f
    };

    public static float ExportAccess(TradePolicy policy) => policy switch
    {
        TradePolicy.OpenMarkets => 1.08f,
        TradePolicy.StrategicAutarky => 0.52f,
        _ => 0.92f
    };

    public static float TariffRate(TradePolicy policy) => policy switch
    {
        TradePolicy.OpenMarkets => 0.01f,
        TradePolicy.StrategicAutarky => 0.14f,
        _ => 0.05f
    };

    public static string Name(TradePolicy policy) => policy switch
    {
        TradePolicy.OpenMarkets => "Open markets",
        TradePolicy.StrategicAutarky => "Strategic autarky",
        _ => "Balanced trade"
    };
}

public sealed class CommodityDefinition
{
    public string Name { get; set; }
    public string Icon { get; set; }
    public float BasePrice { get; set; }
    public float EssentialWeight { get; set; }
    public string Description { get; set; }

    public CommodityDefinition(string name, string icon, float basePrice, float essentialWeight, string description) =>
        (Name, Icon, BasePrice, EssentialWeight, Description) = (name, icon, basePrice, essentialWeight, description);
}

public static class CommodityCatalog
{
    private static readonly Dictionary<Commodity, CommodityDefinition> Defaults = new()
    {
        [Commodity.Food] = new("Food", "●", 0.012f, 1.00f, "Household food supply and agricultural output."),
        [Commodity.ConsumerGoods] = new("Consumer goods", "◆", 0.024f, 0.75f, "Everyday manufactured goods that support living standards."),
        [Commodity.IndustrialMaterials] = new("Industrial materials", "■", 0.038f, 0.62f, "Steel, cement and components used by industry and construction."),
        [Commodity.Energy] = new("Energy", "▲", 0.031f, 0.90f, "Electricity and fuels required by households and factories."),
        [Commodity.Arms] = new("Arms", "✦", 0.085f, 0.30f, "Weapons and military equipment consumed by field formations.")
        ,[Commodity.Coal] = new("Coal", "◆", 0.019f, 0.45f, "Fuel for power generation and heavy industry.")
        ,[Commodity.Oil] = new("Oil", "●", 0.052f, 0.68f, "Fuel and petrochemical feedstock for transport, armies and industry.")
        ,[Commodity.IronOre] = new("Iron ore", "⬟", 0.026f, 0.38f, "Ore refined into construction and industrial materials.")
        ,[Commodity.RareEarths] = new("Rare earths", "✧", 0.094f, 0.32f, "Strategic minerals for advanced electronics and precision equipment.")
        ,[Commodity.Machinery] = new("Machinery", "⚙", 0.072f, 0.54f, "Machine tools, engines and industrial equipment.")
    };
    private static readonly Dictionary<Commodity, CommodityDefinition> Definitions = new();

    public static IEnumerable<Commodity> All => Definitions.Keys;
    public static CommodityDefinition Get(Commodity commodity) => Definitions[commodity];

    static CommodityCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        foreach (var entry in Defaults)
            Definitions[entry.Key] = new CommodityDefinition(entry.Value.Name, entry.Value.Icon,
                entry.Value.BasePrice, entry.Value.EssentialWeight, entry.Value.Description);
        foreach (var document in GameDataLoader.DataDocuments("common", "goods"))
            using (document)
            foreach (var entry in document.RootElement.EnumerateObject())
            {
                if (!entry.Value.TryGetProperty("name", out var name)) name = default;
                if (!entry.Value.TryGetProperty("icon", out var icon)) icon = default;
                if (!entry.Value.TryGetProperty("description", out var description)) description = default;
                Override(entry.Name,
                    name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                    icon.ValueKind == JsonValueKind.String ? icon.GetString() : null,
                    entry.Value.TryGetProperty("base_price", out var price) && price.TryGetSingle(out var basePrice) ? basePrice : null,
                    entry.Value.TryGetProperty("essential_weight", out var weight) && weight.TryGetSingle(out var essential) ? essential : null,
                    description.ValueKind == JsonValueKind.String ? description.GetString() : null);
            }
    }

    public static bool TryParse(string id, out Commodity commodity) =>
        Enum.TryParse(id?.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal), true, out commodity);

    public static bool Override(string id, string name, string icon, float? basePrice, float? essentialWeight, string description)
    {
        if (!TryParse(id, out var commodity)) return false;
        var definition = Definitions[commodity];
        if (!string.IsNullOrWhiteSpace(name)) definition.Name = name;
        if (!string.IsNullOrWhiteSpace(icon)) definition.Icon = icon;
        if (basePrice is > 0f) definition.BasePrice = basePrice.Value;
        if (essentialWeight is >= 0f) definition.EssentialWeight = essentialWeight.Value;
        if (!string.IsNullOrWhiteSpace(description)) definition.Description = description;
        return true;
    }
}

public sealed class MarketQuote
{
    public Commodity Commodity;
    public float Production;
    public float Demand;
    public float Imports;
    public float Exports;
    public float Price;
    public float Satisfaction = 1f;
    public float Net => Production + Imports - Demand - Exports;
    public bool IsShortage => Satisfaction < 0.985f;
}

/// <summary>
/// A weekly, deterministic world clearing pass. Countries first satisfy demand from
/// local production, then trade the remaining surpluses. A shortage therefore has a
/// real cause and propagates into prices, GDP, living standards and the treasury.
/// </summary>
public static class MarketSystem
{
    public static void Seed(Campaign campaign)
    {
        foreach (var nation in campaign.Nations)
        {
            nation.Market.Clear();
            foreach (var commodity in CommodityCatalog.All)
                nation.Market[commodity] = new MarketQuote
                {
                    Commodity = commodity,
                    Price = CommodityCatalog.Get(commodity).BasePrice
                };
        }
        Recalculate(campaign, false);
    }

    public static void Tick(Campaign campaign)
    {
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) return;
        Recalculate(campaign, true);
    }

    /// <summary>
    /// One market pass at day zero, with prices set straight to their clearing level
    /// rather than eased toward it, so the calibration in EconomySystem.CalibrateOutput
    /// measures a settled market instead of one still on its way there.
    /// </summary>
    public static void Prime(Campaign campaign) => Recalculate(campaign, false);

    private static void Recalculate(Campaign campaign, bool adjustPrice)
    {
        foreach (var nation in campaign.Nations)
            CalculateNationalBalance(campaign, nation);

        foreach (var commodity in CommodityCatalog.All)
        {
            var market = campaign.Nations.Select(nation => (Nation: nation, Quote: nation.Market[commodity])).ToArray();
            var totalDemand = MathF.Max(0.01f, market.Sum(item => item.Quote.Demand));
            var totalProduction = MathF.Max(0.01f, market.Sum(item => item.Quote.Production));
            var totalExportable = market.Sum(item => MathF.Max(0f, item.Quote.Production - item.Quote.Demand) *
                                                         TradePolicyRules.ExportAccess(item.Nation.TradePolicy));
            var totalImportDemand = market.Sum(item => MathF.Max(0f, item.Quote.Demand - item.Quote.Production) *
                                                           TradePolicyRules.ImportAccess(item.Nation.TradePolicy));
            var clearing = totalImportDemand <= 0.001f
                ? 0f
                : MathF.Min(1f, totalExportable / totalImportDemand);
            var totalImports = 0f;

            foreach (var item in market)
            {
                item.Quote.Imports = MathF.Max(0f, item.Quote.Demand - item.Quote.Production) *
                                     campaign.Defines.WorldMarketAccess *
                                     TradePolicyRules.ImportAccess(item.Nation.TradePolicy) * clearing;
                totalImports += item.Quote.Imports;
            }

            foreach (var item in market)
            {
                var quote = item.Quote;
                var rawSurplus = MathF.Max(0f, quote.Production - quote.Demand);
                var exportable = rawSurplus * TradePolicyRules.ExportAccess(item.Nation.TradePolicy);
                quote.Exports = totalExportable <= 0.001f ? 0f : exportable * totalImports / totalExportable;
                quote.Satisfaction = quote.Demand <= 0.001f
                    ? 1f
                    : M.Clamp((quote.Production + quote.Imports) / quote.Demand, 0f, 1f);

                var definition = CommodityCatalog.Get(commodity);
                var worldScarcity = M.Clamp(totalDemand / totalProduction, 0.55f, 2.75f);
                var localScarcity = 1f + (1f - quote.Satisfaction) * 1.40f;
                var importMarkup = quote.Imports > 0.001f ? campaign.Defines.ImportMarkup : 1f;
                var targetPrice = definition.BasePrice * worldScarcity * localScarcity * importMarkup;
                quote.Price = adjustPrice
                    ? M.Lerp(quote.Price <= 0f ? definition.BasePrice : quote.Price, targetPrice, campaign.Defines.PriceAdjustSpeed)
                    : targetPrice;
            }
        }

        foreach (var nation in campaign.Nations)
        {
            var weight = 0f;
            var shortages = 0f;
            var trade = 0f;
            var tariffs = 0f;
            foreach (var quote in nation.Market.Values)
            {
                var definition = CommodityCatalog.Get(quote.Commodity);
                weight += definition.EssentialWeight;
                shortages += (1f - quote.Satisfaction) * definition.EssentialWeight;
                trade += (quote.Exports - quote.Imports) * quote.Price;
                tariffs += quote.Imports * quote.Price * TradePolicyRules.TariffRate(nation.TradePolicy);
            }
            // The value of what this country actually produced this week. EconomySystem
            // derives GDP from it, so output is a consequence of buildings, workers and
            // prices rather than a number that grows on its own beside them.
            var grossOutput = 0f;
            foreach (var quote in nation.Market.Values) grossOutput += quote.Production * quote.Price;
            nation.GrossOutput = grossOutput;

            nation.ShortageIndex = weight <= 0f ? 0f : M.Clamp01(shortages / weight);
            nation.MarketAccess = 1f - nation.ShortageIndex;
            nation.WeeklyTradeBalance = trade * campaign.Defines.MarketTreasuryTradeShare;
            nation.WeeklyTariffIncome = tariffs * campaign.Defines.MarketTreasuryTradeShare;
        }
    }

    /// <summary>
    /// Jobs a state's buildings ask for, in millions of workers. Deliberately coarse: the
    /// point is that industry has to be staffed by someone, not that every profession is
    /// tracked separately.
    /// </summary>
    private static float JobsDemanded(ScenarioState state) =>
        state.Buildings.GetValueOrDefault(BuildingKind.Agriculture) * 0.42f +
        state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) * 0.85f +
        state.Buildings.GetValueOrDefault(BuildingKind.ExtractionIndustry) * 0.55f +
        state.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry) * 0.60f +
        state.Buildings.GetValueOrDefault(BuildingKind.PowerPlant) * 0.22f +
        state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) * 0.30f;

    /// <summary>
    /// Workers a state can actually put in those jobs, in millions. Not everyone alive is
    /// available: children, the retired and the unemployable are already priced out by the
    /// working-age share, and the pop groups carry their own employment level on top.
    /// </summary>
    private static float WorkersAvailable(ScenarioState state)
    {
        const float WorkingAgeShare = 0.62f;
        if (state.PopulationGroups.Count == 0) return state.Population / 1_000_000f * WorkingAgeShare;

        var workers = 0f;
        foreach (var group in state.PopulationGroups)
        {
            // Owners run capital rather than filling a shift; everyone else can work.
            var participation = group.Class == SocialClass.Owners ? 0.15f : 1f;
            workers += group.Size / 1_000_000f * WorkingAgeShare * group.Employment * participation;
        }
        return workers;
    }

    private static void CalculateNationalBalance(Campaign campaign, Nation nation)
    {
        if (nation.Market.Count == 0)
            foreach (var commodity in CommodityCatalog.All)
                nation.Market[commodity] = new MarketQuote { Commodity = commodity, Price = CommodityCatalog.Get(commodity).BasePrice };

        foreach (var quote in nation.Market.Values)
        {
            quote.Production = 0f;
            quote.Demand = 0f;
            quote.Imports = 0f;
            quote.Exports = 0f;
        }

        var states = campaign.StatesOf(nation);

        // How much of the labour its buildings ask for the nation can actually staff.
        // A country whose industry has outgrown its workforce runs its plant below
        // capacity instead of conjuring output from nowhere, which is what makes
        // population a real economic input rather than a display statistic.
        var demandedJobs = 0f;
        var availableWorkers = 0f;
        foreach (var state in states)
        {
            demandedJobs += JobsDemanded(state);
            availableWorkers += WorkersAvailable(state);
        }
        nation.EmployedShare = demandedJobs <= 0.001f
            ? 1f
            : M.Clamp(availableWorkers / demandedJobs, 0.25f, 1f);

        // Productivity is the other half: literacy, education policy and the government's
        // focus all reach production through this one multiplier rather than being applied
        // to GDP after the fact.
        var outputFactor = nation.EmployedShare * M.Clamp(nation.Productivity, 0.35f, 3.0f);

        foreach (var state in states)
        {
            var population = state.Population / 1_000_000f;
            // Staffed capacity, not raw building count: outputFactor folds in how much of
            // the demanded labour the nation can fill and how productive that labour is.
            var infrastructure = (float)state.Buildings.GetValueOrDefault(BuildingKind.Infrastructure);
            var extraction = (float)state.Buildings.GetValueOrDefault(BuildingKind.ExtractionIndustry);
            var agriculture = state.Buildings.GetValueOrDefault(BuildingKind.Agriculture) * outputFactor;
            var industry = state.Buildings.GetValueOrDefault(BuildingKind.CivilianIndustry) * outputFactor;
            var barracks = state.Buildings.GetValueOrDefault(BuildingKind.Barracks) * outputFactor;
            var powerPlants = state.Buildings.GetValueOrDefault(BuildingKind.PowerPlant) * outputFactor;
            var armsIndustry = state.Buildings.GetValueOrDefault(BuildingKind.ArmsIndustry) * outputFactor;
            var extractionEfficiency = (0.32f + extraction * 0.72f + infrastructure * 0.11f) * outputFactor;

            Add(nation, Commodity.Food, population * 0.35f + agriculture * 2.70f, population);
            Add(nation, Commodity.ConsumerGoods,
                population * (0.28f + state.Urbanization * 1.50f) + industry * 6.0f,
                population * (0.42f + state.StandardOfLiving * 0.46f));
            Add(nation, Commodity.IndustrialMaterials, industry * 5.0f + infrastructure * 1.5f,
                population * 0.18f);
            Add(nation, Commodity.Energy, population * 0.53f + infrastructure * 2.0f,
                population * (0.54f + state.Urbanization * 0.34f) + industry * 0.45f);
            Add(nation, Commodity.Arms, barracks * 0.75f + industry * 0.48f, 0f);
            var coalOutput = state.ResourceDeposits.GetValueOrDefault(Commodity.Coal) * extractionEfficiency;
            var oilOutput = state.ResourceDeposits.GetValueOrDefault(Commodity.Oil) * extractionEfficiency;
            var ironOutput = state.ResourceDeposits.GetValueOrDefault(Commodity.IronOre) * extractionEfficiency;
            var rareEarthOutput = state.ResourceDeposits.GetValueOrDefault(Commodity.RareEarths) * extractionEfficiency;
            Add(nation, Commodity.Coal, coalOutput, industry * 0.52f + powerPlants * 0.85f);
            Add(nation, Commodity.Oil, oilOutput, population * 0.16f + industry * 0.18f + barracks * 0.22f);
            Add(nation, Commodity.IronOre, ironOutput, industry * 0.72f + extraction * 0.16f);
            Add(nation, Commodity.RareEarths, rareEarthOutput, industry * 0.08f + armsIndustry * 0.18f);
            Add(nation, Commodity.Machinery,
                industry * 0.95f + state.Urbanization * 0.25f,
                extraction * 0.45f + agriculture * 0.12f);
            nation.Market[Commodity.Energy].Production += powerPlants * 5.5f + coalOutput * 0.12f + oilOutput * 0.08f;
            nation.Market[Commodity.Arms].Production += armsIndustry * 4.5f;
            nation.Market[Commodity.IndustrialMaterials].Production += ironOutput * 0.38f;
        }

        var projects = campaign.Construction.Count(project => project.NationId == nation.Id);
        nation.Market[Commodity.IndustrialMaterials].Demand += projects * 2.2f;
        nation.Market[Commodity.Machinery].Demand += projects * 0.85f;
        var divisions = campaign.Armies.Where(army => army.NationId == nation.Id).Sum(army => army.Divisions);
        var hulls = campaign.Fleets.Where(fleet => fleet.NationId == nation.Id).Sum(fleet => fleet.Hulls);
        var atWar = campaign.Wars.Any(war => !war.Ended && war.Involves(nation.Id));
        nation.Market[Commodity.Arms].Production += nation.Gdp / 250f * M.Clamp(nation.Productivity, 0.55f, 1.55f);
        nation.Market[Commodity.Arms].Demand += divisions * (atWar ? 0.52f : 0.18f) + hulls * 0.04f;
        nation.Market[Commodity.Oil].Demand += divisions * (atWar ? 0.18f : 0.06f) + hulls * 0.025f;
    }

    private static void Add(Nation nation, Commodity commodity, float production, float demand)
    {
        var quote = nation.Market[commodity];
        quote.Production += MathF.Max(0f, production);
        quote.Demand += MathF.Max(0f, demand);
    }
}
