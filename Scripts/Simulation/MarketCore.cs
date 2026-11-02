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
    Machinery,

    // A second tier, and the reason for it: with ten goods every country either made a
    // thing or did not, and trade was one anonymous world pool that nobody could be cut
    // off from. These ten exist to be links in chains - bauxite is worthless without a
    // smelter and cheap power, electronics need rare earths a country may not have, and
    // fertiliser turns gas into food. A chain is what makes a trade partner matter, and a
    // partner who matters is what makes an embargo a weapon and a war a decision.
    NaturalGas,
    Timber,
    Bauxite,
    Uranium,
    Aluminium,
    Chemicals,
    Fertiliser,
    Electronics,
    Textiles,
    Pharmaceuticals
}

public enum TradePolicy { OpenMarkets, BalancedTrade, StrategicAutarky }

/// <summary>
/// How open a country is to the world market, asked of `common/trade_policies` rather than
/// answered by four switch statements that had to be kept in step by hand.
/// </summary>
public static class TradePolicyRules
{
    public static float ImportAccess(TradePolicy policy) =>
        TradePolicyCatalog.Of(policy).ImportAccess;

    public static float ExportAccess(TradePolicy policy) =>
        TradePolicyCatalog.Of(policy).ExportAccess;

    public static float TariffRate(TradePolicy policy) =>
        TradePolicyCatalog.Of(policy).Tariff;

    public static string Name(TradePolicy policy) =>
        Loc.Get(TradePolicyCatalog.Of(policy).Name);

    public static string Description(TradePolicy policy) =>
        Loc.Get(TradePolicyCatalog.Of(policy).Description);
}

public sealed class CommodityDefinition
{
    public string Name { get; set; }
    public string Icon { get; set; }
    public float BasePrice { get; set; }
    public float EssentialWeight { get; set; }
    public string Description { get; set; }

    /// <summary>
    /// How much being cut off from this good is worth to whoever sells it (CommodityLeverage):
    /// none, a strategic good - grain, fuel, potash, tungsten, the things a country can be
    /// squeezed with - or a high-technology one, which counts twice, because what is made in
    /// three places on Earth cannot be bought from a fourth. Written in the good's own file
    /// (`"leverage": "strategic"` or `"high_tech"`) rather than in a compiled list, so the
    /// semiconductor that a mod adds is a lever the moment it is declared.
    /// </summary>
    public CommodityLeverageTier Leverage { get; set; }

    /// <summary>
    /// Whether the world price follows the trade in this good rather than the whole harvest
    /// (`"world_price": "traded"` in its file). Most of the world's grain is eaten where it
    /// grows; what the price answers to is the part that crosses a border - how much the
    /// importers want against how much the exporters can spare. Priced on the whole harvest,
    /// losing the Black Sea's exports moved world output by a few per cent and the price by as
    /// much, while the countries that live on those exports went without. Off for every good
    /// that does not ask for it, so nothing else is priced differently.
    /// </summary>
    public bool TradedWorldPrice { get; set; }

    public CommodityDefinition(string name, string icon, float basePrice, float essentialWeight, string description) =>
        (Name, Icon, BasePrice, EssentialWeight, Description) = (name, icon, basePrice, essentialWeight, description);
}

/// <summary>What a good is to the country that cannot make it (CommodityDefinition.Leverage).</summary>
public enum CommodityLeverageTier { None = 0, Strategic = 1, HighTech = 2 }

public sealed partial class Defines
{
    /// <summary>
    /// How households' appetite follows their income: demand terms written with "income" grow as
    /// the country's real output per head rises above where it started, to this power, within
    /// these bounds. Below one, as consumption is: a country twice as rich does not eat twice as
    /// much, but it buys a good deal more of almost everything else.
    /// </summary>
    public float IncomeDemandElasticity { get; set; } = 0.85f;
    public float IncomeFactorMinimum { get; set; } = 0.5f;
    public float IncomeFactorMaximum { get; set; } = 3f;

    /// <summary>
    /// How many times its first jobs per worker a country may build to before more buildings
    /// add less, and the power its plant yields at past that (MarketSystem.CapitalReturns):
    /// diminishing returns to capital. 0 turns them off.
    /// </summary>
    public float CapitalDeepeningHeadroom { get; set; } = 1.3f;
    public float CapitalDeepeningShare { get; set; } = 0.4f;

    /// <summary>
    /// How producers answer the price (ProductionChains.SupplyResponse): output scales as last
    /// week's price against the base to this power, between the minimum (a glut idles plant) and
    /// the maximum (a shortage runs it past its rating). 0 runs every plant flat out.
    /// </summary>
    public float SupplyResponseElasticity { get; set; } = 0.5f;
    public float SupplyResponseMinimum { get; set; } = 0.5f;
    public float SupplyResponseMaximum { get; set; } = 1.15f;
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
        ,[Commodity.NaturalGas] = new("Natural gas", "◇", 0.041f, 0.72f, "Heating, power generation and the feedstock every chemical industry starts from.")
        ,[Commodity.Timber] = new("Timber", "❦", 0.014f, 0.34f, "Construction lumber, pulp and the raw material behind cloth and paper.")
        ,[Commodity.Bauxite] = new("Bauxite", "◭", 0.021f, 0.20f, "Aluminium ore. Worthless without a smelter and cheap electricity.")
        ,[Commodity.Uranium] = new("Uranium", "☢", 0.118f, 0.18f, "Fuel for reactors, and the reason some countries are watched more closely than others.")
        ,[Commodity.Aluminium] = new("Aluminium", "▣", 0.058f, 0.36f, "Light metal for airframes, vehicles and transmission lines.")
        ,[Commodity.Chemicals] = new("Chemicals", "⚗", 0.047f, 0.44f, "Industrial chemistry: plastics, explosives, fertiliser and medicine all begin here.")
        ,[Commodity.Fertiliser] = new("Fertiliser", "✿", 0.033f, 0.58f, "Nitrogen and phosphate. What a modern harvest is actually made of.")
        ,[Commodity.Electronics] = new("Electronics", "⌘", 0.106f, 0.52f, "Chips, sensors and control systems for industry, consumers and precision weapons.")
        ,[Commodity.Textiles] = new("Textiles", "▩", 0.017f, 0.48f, "Cloth and clothing - the cheapest industry to start and the first one to be undercut.")
        ,[Commodity.Pharmaceuticals] = new("Pharmaceuticals", "✚", 0.089f, 0.66f, "Medicine. A shortage is felt in living standards long before it is felt in the ledger.")
        ,[Commodity.Machinery] = new("Machinery", "⚙", 0.072f, 0.54f, "Machine tools, engines and industrial equipment.")
    };
    private static readonly Dictionary<Commodity, CommodityDefinition> Definitions = new();

    /// <summary>
    /// What each good is called in a file, and which good that is - including the ones no
    /// enum ever named.
    ///
    /// `Commodity` is an enum, and an enum in C# is an int with names for some of its values.
    /// `(Commodity)20` is a perfectly good value: a dictionary key, a field, a number in a
    /// save. What stopped a mod inventing a twenty-first good was never the enum, it was that
    /// every road into the catalog went through `Enum.TryParse`, which only knows the twenty
    /// that were compiled. So the catalog keeps its own name table, hands out the next free
    /// number to an id it has not seen, and the enum keeps naming the twenty the game ships -
    /// which is what lets `Commodity.Food` go on meaning what it means in a hundred places.
    /// </summary>
    private static readonly Dictionary<string, Commodity> Named =
        new(StringComparer.OrdinalIgnoreCase);

    private static readonly Dictionary<Commodity, string> Ids = new();
    private static int _nextId;

    /// <summary>A good the catalog has forgotten - a mod's, after the mod is gone.</summary>
    private static readonly CommodityDefinition Unknown =
        new("Unknown good", "?", 0.02f, 0f, "Declared by content that is no longer loaded.");

    public static IEnumerable<Commodity> All => Definitions.Keys;

    public static CommodityDefinition Get(Commodity commodity) =>
        Definitions.TryGetValue(commodity, out var definition) ? definition : Unknown;

    /// <summary>The name this good has in a file, which is what a save should be keyed by.</summary>
    public static string IdOf(Commodity commodity) =>
        Ids.TryGetValue(commodity, out var id) ? id : ((int)commodity).ToString();

    /// <summary>Stable raster icon key, including data-defined goods outside the enum.</summary>
    public static string IconNameOf(Commodity commodity) => "good_" + IdOf(commodity).ToLowerInvariant();

    static CommodityCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        Definitions.Clear();
        Named.Clear();
        Ids.Clear();
        _nextId = 0;
        foreach (var entry in Defaults)
        {
            Definitions[entry.Key] = new CommodityDefinition(entry.Value.Name, entry.Value.Icon,
                entry.Value.BasePrice, entry.Value.EssentialWeight, entry.Value.Description)
            {
                Leverage = DefaultLeverage(entry.Key)
            };
            Register(entry.Key, entry.Key.ToString());
        }
        foreach (var document in GameDataLoader.DataDocuments("common", "goods"))
            using (document)
            {
                if (document.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (!entry.Value.TryGetProperty("name", out var name)) name = default;
                    if (!entry.Value.TryGetProperty("icon", out var icon)) icon = default;
                    if (!entry.Value.TryGetProperty("description", out var description)) description = default;
                    Override(entry.Name,
                        name.ValueKind == JsonValueKind.String ? name.GetString() : null,
                        icon.ValueKind == JsonValueKind.String ? icon.GetString() : null,
                        entry.Value.TryGetProperty("base_price", out var price) && price.ValueKind == JsonValueKind.Number && price.TryGetSingle(out var basePrice) ? basePrice : null,
                        entry.Value.TryGetProperty("essential_weight", out var weight) && weight.ValueKind == JsonValueKind.Number && weight.TryGetSingle(out var essential) ? essential : null,
                        description.ValueKind == JsonValueKind.String ? description.GetString() : null);
                    ReadLeverage(entry.Name, entry.Value);
                    ReadWorldPrice(entry.Name, entry.Value);
                }
            }
    }

    /// <summary>
    /// The levers the first twenty goods always were, before the tier was data: rare earths,
    /// electronics, machinery and uranium are high technology; chemicals, oil, gas and arms are
    /// strategic. A file can change any of them.
    /// </summary>
    private static CommodityLeverageTier DefaultLeverage(Commodity commodity) => commodity switch
    {
        Commodity.RareEarths or Commodity.Electronics or Commodity.Machinery or Commodity.Uranium => CommodityLeverageTier.HighTech,
        Commodity.Chemicals or Commodity.Oil or Commodity.NaturalGas or Commodity.Arms => CommodityLeverageTier.Strategic,
        _ => CommodityLeverageTier.None
    };

    /// <summary>
    /// A good's `"world_price"` field, if it has one: "traded" prices it on what crosses borders
    /// (CommodityDefinition.TradedWorldPrice), "harvest" on the whole of world output, as every
    /// good was. Read by the base loader and by a mod's alike. Returns whether it was set.
    /// </summary>
    public static bool ReadWorldPrice(string id, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("world_price", out var pricing) ||
            pricing.ValueKind != JsonValueKind.String || !TryParse(id, out var commodity) ||
            !Definitions.TryGetValue(commodity, out var definition)) return false;
        switch (pricing.GetString()?.Trim().ToLowerInvariant())
        {
            case "traded": definition.TradedWorldPrice = true; return true;
            case "harvest": definition.TradedWorldPrice = false; return true;
            default: return false;
        }
    }

    /// <summary>
    /// A good's `"leverage"` field, if it has one: "none", "strategic" or "high_tech". Read by
    /// the base game's loader and by a mod's (ModLoader.ApplyGoods) alike; anything else is
    /// ignored and the good keeps the tier it had. Returns whether a tier was set.
    /// </summary>
    public static bool ReadLeverage(string id, JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object || !value.TryGetProperty("leverage", out var leverage) ||
            leverage.ValueKind != JsonValueKind.String || !TryParse(id, out var commodity) ||
            !Definitions.TryGetValue(commodity, out var definition)) return false;
        switch (Squash(leverage.GetString()).ToLowerInvariant())
        {
            case "none": definition.Leverage = CommodityLeverageTier.None; return true;
            case "strategic": definition.Leverage = CommodityLeverageTier.Strategic; return true;
            case "hightech": definition.Leverage = CommodityLeverageTier.HighTech; return true;
            default: return false;
        }
    }

    private static string Squash(string id) => id is null
        ? ""
        : id.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static void Register(Commodity commodity, string id)
    {
        Named[Squash(id)] = commodity;
        Ids[commodity] = id;
        if ((int)commodity >= _nextId) _nextId = (int)commodity + 1;
    }

    /// <summary>Which good a file means, if the catalog has one by that name.</summary>
    public static bool TryParse(string id, out Commodity commodity) =>
        Named.TryGetValue(Squash(id), out commodity);

    /// <summary>
    /// Change a good, or declare one that did not exist.
    ///
    /// An unknown id used to be rejected, which is what made the twenty a hard limit. It is
    /// now a new good, on one condition: a name and a base price, because a good with neither
    /// is a typo rather than a declaration and rejecting typos is most of what a catalog is
    /// for. Everything about it after that is the same as everything about the other twenty -
    /// it gets a market quote in every country, it can be produced and consumed by recipes in
    /// `common/production`, it trades, and it appears in the ledger.
    /// </summary>
    public static bool Override(string id, string name, string icon, float? basePrice, float? essentialWeight, string description)
    {
        if (string.IsNullOrWhiteSpace(id)) return false;
        // Reject invalid numeric patches before changing even the name or allocating an ID.
        if ((basePrice.HasValue && (!float.IsFinite(basePrice.Value) || basePrice.Value <= 0f)) ||
            (essentialWeight.HasValue && (!float.IsFinite(essentialWeight.Value) || essentialWeight.Value < 0f))) return false;
        if (!TryParse(id, out var commodity))
        {
            if (string.IsNullOrWhiteSpace(name) || basePrice is not > 0f) return false;
            commodity = (Commodity)_nextId;
            Register(commodity, id);
            Definitions[commodity] = new CommodityDefinition(name, icon ?? "◆", basePrice.Value,
                essentialWeight ?? 0.3f, description ?? "");
            return true;
        }
        var definition = Definitions[commodity];
        if (!string.IsNullOrWhiteSpace(name)) definition.Name = name;
        if (!string.IsNullOrWhiteSpace(icon)) definition.Icon = icon;
        if (basePrice is > 0f) definition.BasePrice = basePrice.Value;
        if (essentialWeight is >= 0f) definition.EssentialWeight = essentialWeight.Value;
        if (!string.IsNullOrWhiteSpace(description)) definition.Description = description;
        return true;
    }
}

[System.Text.Json.Serialization.JsonConverter(typeof(MarketQuoteConverter))]
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
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>The week's market in stages, for the smoothed step (EconomySystem.TickStages).</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MarketSystem))) yield break;
        if (campaign.ElapsedDays == 0 || campaign.ElapsedDays % campaign.Defines.EconomyTickDays != 0) yield break;
        foreach (var _ in RecalculateStages(campaign, true)) yield return 0;
    }

    /// <summary>
    /// One market pass at day zero, with prices set straight to their clearing level
    /// rather than eased toward it, so the calibration in EconomySystem.CalibrateOutput
    /// measures a settled market instead of one still on its way there.
    /// </summary>
    public static void Prime(Campaign campaign) => Recalculate(campaign, false);

    private static void Recalculate(Campaign campaign, bool adjustPrice)
    {
        foreach (var _ in RecalculateStages(campaign, adjustPrice)) { }
    }

    /// <summary>How many countries' balances one stage of the market works out.</summary>
    private const int BalanceStageCountries = 48;

    private static IEnumerable<int> RecalculateStages(Campaign campaign, bool adjustPrice)
    {
        for (var start = 0; start < campaign.Nations.Count; start += BalanceStageCountries)
        {
            using (SimProfiler.Measure("    mkt:balance"))
                for (var index = start; index < Math.Min(campaign.Nations.Count, start + BalanceStageCountries); index++)
                    CalculateNationalBalance(campaign, campaign.Nations[index]);
            yield return 0;
        }

        // Who actually sells to whom. This used to be an anonymous pool: a shortfall was
        // met by "the world" at a constant rate, so nobody could be cut off and a trade
        // partner was worth one opinion modifier. Every import and export below is now a
        // named route to a named country - see TradeNetwork.
        foreach (var _ in TradeNetwork.RouteStages(campaign)) yield return 0;

        using var pricing = SimProfiler.Measure("    mkt:prices");
        foreach (var commodity in CommodityCatalog.All)
        {
            var totalDemand = 0f;
            var totalProduction = 0f;
            var wantedImports = 0f;
            var spareExports = 0f;
            foreach (var nation in campaign.Nations)
            {
                if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
                totalDemand += quote.Demand;
                totalProduction += quote.Production;
                if (quote.Demand > quote.Production) wantedImports += quote.Demand - quote.Production;
                else spareExports += quote.Production - quote.Demand;
            }
            totalDemand = MathF.Max(0.01f, totalDemand);
            totalProduction = MathF.Max(0.01f, totalProduction);

            var definition = CommodityCatalog.Get(commodity);
            // A good priced on its trade: what the importers want against what the exporters can
            // spare (CommodityDefinition.TradedWorldPrice). Everything else: the whole harvest.
            var worldScarcity = definition.TradedWorldPrice
                ? M.Clamp(MathF.Max(0.01f, wantedImports) / MathF.Max(0.01f, spareExports), 0.55f, 2.75f)
                : M.Clamp(totalDemand / totalProduction, 0.55f, 2.75f);
            foreach (var nation in campaign.Nations)
            {
                if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
                quote.Satisfaction = quote.Demand <= 0.001f
                    ? 1f
                    : M.Clamp((quote.Production + quote.Imports) / quote.Demand, 0f, 1f);

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
            // At base price plus a share of the difference (Defines.GdpPriceShare): real output with
            // the terms of trade in it, not every shortage in the world counted as growth.
            var priceShare = M.Clamp01(campaign.Defines.GdpPriceShare);
            foreach (var quote in nation.Market.Values)
            {
                var basePrice = CommodityCatalog.Get(quote.Commodity).BasePrice;
                // And within GdpPriceBand of the base: a world short of everything put prices at
                // their cap of nearly three times, and the country making those goods doubled its
                // GDP in two years - the United States reached two fifths of the world economy.
                var band = MathF.Max(0f, campaign.Defines.GdpPriceBand);
                var price = M.Clamp(quote.Price, basePrice * MathF.Max(0f, 1f - band), basePrice * (1f + band));
                // What was sold, not what was made: the country's own use of its own goods and what
                // it sent abroad. A mine working a deposit nobody wants piles up ore, not income -
                // the United States mined rare earths it had no buyer for, at the world's floor
                // price, until they were half its economy.
                var sold = MathF.Min(quote.Production, MathF.Max(0f, quote.Demand - quote.Imports) + quote.Exports);
                grossOutput += sold * (basePrice + priceShare * (price - basePrice));
            }
            nation.GrossOutput = grossOutput;
            MeasureIncome(campaign, nation);

            nation.ShortageIndex = weight <= 0f ? 0f : M.Clamp01(shortages / weight);
            nation.MarketAccess = 1f - nation.ShortageIndex;
            nation.WeeklyTradeBalance = trade * campaign.Defines.MarketTreasuryTradeShare;

            // What the enemy's navy takes off the sea lanes, and what one's own escorts
            // keep. This is where a fleet stops being scenery and becomes money.
            NavalOperations.ApplyCommerceEffects(campaign, nation, null);
            nation.WeeklyTariffIncome = tariffs * campaign.Defines.MarketTreasuryTradeShare;
        }
    }

    /// <summary>
    /// Jobs a state's buildings ask for, in millions of workers. Deliberately coarse: the
    /// point is that industry has to be staffed by someone, not that every profession is
    /// tracked separately.
    ///
    /// How many each building asks for is its `jobs` in common/buildings (BuildingDefinition.Jobs),
    /// read here and by the build list, so the tooltip's "jobs" is the number that staffs the
    /// plant. The second tier needs hands too: refineries, smelters, chemical and electronics
    /// plants, yards, ports and railways once employed nobody, so a rich country that built
    /// them grew without ever running short of people - the United States doubled its real
    /// output in two years at 86 % employment and reached two fifths of the world economy.
    /// Universities, hospitals and barracks still ask for none.
    ///
    /// Summed in the order the hand-written sum used, then anything a mod declared, so the
    /// shipped buildings add up to the same float they always did.
    /// </summary>
    internal static float JobsDemanded(ScenarioState state)
    {
        var jobs = 0f;
        foreach (var kind in JobOrder)
            jobs += state.Buildings.GetValueOrDefault(kind) * BuildingCatalog.Get(kind).Jobs;
        foreach (var (kind, count) in state.Buildings)
        {
            if ((int)kind <= (int)BuildingKind.ResearchLab || count == 0) continue;
            jobs += count * BuildingCatalog.Get(kind).Jobs;
        }
        return jobs;
    }

    private static readonly BuildingKind[] JobOrder =
    {
        BuildingKind.Agriculture, BuildingKind.CivilianIndustry, BuildingKind.ExtractionIndustry,
        BuildingKind.ArmsIndustry, BuildingKind.PowerPlant, BuildingKind.Infrastructure,
        BuildingKind.Refinery, BuildingKind.ChemicalPlant, BuildingKind.Smelter,
        BuildingKind.ElectronicsPlant, BuildingKind.Shipyard, BuildingKind.Port,
        BuildingKind.RailNetwork, BuildingKind.ResearchLab,
        // Never staffed in the shipped data; listed so a mod that gives them jobs is counted once.
        BuildingKind.University, BuildingKind.Hospital, BuildingKind.Barracks
    };

    /// <summary>
    /// Workers a state can actually put in those jobs, in millions. Not everyone alive is
    /// available: children, the retired and the unemployable are already priced out by the
    /// working-age share, and the pop groups carry their own employment level on top.
    /// </summary>
    internal static float WorkersAvailable(ScenarioState state)
    {
        const float WorkingAgeShare = 0.62f;
        if (state.PopulationGroups.Count == 0) return state.Population / 1_000_000f * WorkingAgeShare;

        var workers = 0f;
        foreach (var group in state.PopulationGroups)
        {
            // The upper class runs capital rather than filling a shift; everyone else works.
            var participation = group.Class == SocialClass.Upper ? 0.15f : 1f;
            workers += group.Size / 1_000_000f * WorkingAgeShare * group.Employment * participation;
        }
        return workers;
    }

    /// <summary>What a country has under construction, under arms, at sea and at war.</summary>
    private readonly record struct NationTally(int Projects, float Divisions, int Hulls, bool AtWar);

    private static readonly Dictionary<int, NationTally> TallyByNation = new();
    private static int _talliedDay = int.MinValue;
    private static Campaign _talliedCampaign;

    /// <summary>
    /// The four global lists, walked once each, instead of once per country.
    ///
    /// Cached for the day and stamped with the campaign it describes, because two live
    /// campaigns sharing a static cache is a bug that has already been paid for once in this
    /// codebase - see the border table in CampaignDiplomacy.
    /// </summary>
    private static NationTally Tallies(Campaign campaign, int nationId)
    {
        if (!ReferenceEquals(_talliedCampaign, campaign) || _talliedDay != campaign.ElapsedDays)
        {
            _talliedCampaign = campaign;
            _talliedDay = campaign.ElapsedDays;
            TallyByNation.Clear();

            foreach (var project in campaign.Construction)
            {
                var current = TallyByNation.GetValueOrDefault(project.NationId);
                TallyByNation[project.NationId] = current with { Projects = current.Projects + 1 };
            }
            foreach (var army in campaign.Armies)
            {
                var current = TallyByNation.GetValueOrDefault(army.NationId);
                TallyByNation[army.NationId] = current with { Divisions = current.Divisions + army.Divisions };
            }
            foreach (var fleet in campaign.Fleets)
            {
                var current = TallyByNation.GetValueOrDefault(fleet.NationId);
                TallyByNation[fleet.NationId] = current with { Hulls = current.Hulls + fleet.Hulls };
            }
            foreach (var war in campaign.Wars)
            {
                if (war.Ended) continue;
                foreach (var side in new[] { war.AttackerId, war.DefenderId })
                {
                    var current = TallyByNation.GetValueOrDefault(side);
                    TallyByNation[side] = current with { AtWar = true };
                }
            }
        }
        return TallyByNation.GetValueOrDefault(nationId);
    }

    private static void CalculateNationalBalance(Campaign campaign, Nation nation)
    {
        // Every good the catalog knows, not only when the market is empty.
        //
        // This used to seed only a market with nothing in it, which was right while the
        // twenty goods were fixed and is wrong now that a mod can declare a twenty-first.
        // Enabling such a mod and loading an existing campaign left every country without a
        // quote for the new good, and the trade pass reads that quote by index - so the
        // first market week after enabling the mod threw. Topping up costs one dictionary
        // probe per good per country per week and cannot be got wrong.
        foreach (var commodity in CommodityCatalog.All)
            if (!nation.Market.ContainsKey(commodity))
                nation.Market[commodity] = new MarketQuote { Commodity = commodity, Price = CommodityCatalog.Get(commodity).BasePrice };

        foreach (var quote in nation.Market.Values)
        {
            quote.Production = 0f;
            quote.Demand = 0f;
            quote.Imports = 0f;
            quote.Exports = 0f;
        }

        // The regions whose economy answers to it: its own, less those another country holds,
        // and those it holds of others (HeldRegions) - held ground is ground.
        var states = HeldRegions.EconomyOf(campaign, nation).ToArray();
        ProductionChains.Defines = campaign.Defines;

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
        // The jobs per worker it is measured against from now on: its first market's
        // (CapitalReturns), and a country born later starts from its own.
        if (nation.CapitalIntensityReference <= 0f && demandedJobs > 0.001f && availableWorkers > 0.001f)
            nation.CapitalIntensityReference = demandedJobs / availableWorkers;

        // Productivity is the other half: literacy, education policy and the government's
        // focus all reach production through this one multiplier rather than being applied
        // to GDP after the fact.
        // And what its technologies, spirits and government add (NationalModifiers): a share
        // on top, not a new input.
        var outputFactor = NationalOutputFactor(campaign, nation, demandedJobs, availableWorkers);

        var allocation = ProductionProgramme.Allocate(states, nation);
        ProductionProgramme.MigrateLegacy(nation, allocation);
        // A region the war has wrecked makes less of everything (WarDamage).
        foreach (var state in states)
            ProductionChains.RunRegion(nation, state, outputFactor * WarDamage.OutputFactor(campaign, nation, state) *
                HeldRegions.WorkedAt(campaign, nation, state), allocation);

        // Four LINQ scans of four global lists, once per country, with a capturing closure
        // each. That is a thousand closures and half a million list steps every market week,
        // to answer four questions that one pass over each list answers for everybody.
        var tallies = Tallies(campaign, nation.Id);
        ProductionChains.RunNation(nation, tallies.Projects, tallies.Divisions, tallies.Hulls,
            tallies.AtWar);
    }

    /// <summary>
    /// A country's households' appetite against its income. The market made more and more of
    /// everything while what a head of the people wanted stayed where it was in 2024, so a
    /// decade later consumer goods, energy and materials sat at the floor price everywhere and
    /// no factory anywhere was worth building - India's industry shrank while its mines grew.
    /// Real output per head at base prices, against the first week's, to Defines.IncomeDemandElasticity.
    /// </summary>
    /// <summary>
    /// What one more building adds once a country has more of them for each worker than it
    /// began with. Up to CapitalDeepeningHeadroom times its first measured jobs per worker
    /// (Nation.CapitalIntensityReference) every building adds its full output; past it the
    /// country's plant as a whole yields as its size to the power CapitalDeepeningShare - a
    /// second steelworks for the same workers is worth less than the first. Nothing else
    /// stopped a rich country compounding: labour never bound, and the United States
    /// multiplied its real output by half in eight years. Against the country's own start,
    /// because the jobs a region's buildings ask for are a coarse count - the median country
    /// asks for 0.95 workers per worker it has and Greenland for 130 - not a census.
    /// </summary>
    /// <summary>
    /// How much of its rated output a country's plant gives this week: the share of the jobs it
    /// can staff, times how productive the staff are, its modifiers, the diminishing return on
    /// capital past its headroom, and any recession.
    /// </summary>
    private static float NationalOutputFactor(Campaign campaign, Nation nation, float demandedJobs, float availableWorkers) =>
        nation.EmployedShare * M.Clamp(nation.Productivity, 0.35f, 3.0f) *
        NationalModifiers.Factor(nation, "output") *
        CapitalReturns(campaign, nation, demandedJobs, availableWorkers) *
        // A recession from a financial crisis or a pandemic (TreasuryDynamics).
        TreasuryDynamics.OutputFactor(campaign, nation);

    /// <summary>
    /// The output factor the market pass hands ProductionChains.RunRegion for this region, worked
    /// out again from the country as it stands - for the build list, which wants to say what one
    /// more plant here would make. Uses the staffing share the last market week measured rather
    /// than measuring it again, so it reads the same number the economy ran on.
    /// </summary>
    public static float RegionOutputFactor(Campaign campaign, Nation nation, ScenarioState state)
    {
        if (campaign is null || nation is null || state is null) return 1f;
        var demandedJobs = 0f;
        var availableWorkers = 0f;
        foreach (var region in HeldRegions.EconomyOf(campaign, nation))
        {
            demandedJobs += JobsDemanded(region);
            availableWorkers += WorkersAvailable(region);
        }
        return NationalOutputFactor(campaign, nation, demandedJobs, availableWorkers) *
               WarDamage.OutputFactor(campaign, nation, state) * HeldRegions.WorkedAt(campaign, nation, state);
    }

    internal static float CapitalReturns(Campaign campaign, Nation nation, float demandedJobs, float availableWorkers)
    {
        var headroom = campaign.Defines.CapitalDeepeningHeadroom;
        var reference = nation.CapitalIntensityReference;
        if (headroom <= 0f || reference <= 0f || availableWorkers <= 0.001f || demandedJobs <= 0.001f) return 1f;
        var deepening = demandedJobs / availableWorkers / (reference * headroom);
        if (!float.IsFinite(deepening) || deepening <= 1f) return 1f;
        var share = M.Clamp01(campaign.Defines.CapitalDeepeningShare);
        return M.Clamp(MathF.Pow(deepening, share - 1f), 0.1f, 1f);
    }

    private static void MeasureIncome(Campaign campaign, Nation nation)
    {
        var people = nation.Population / 1_000_000f;
        if (people <= 0.001f) return;
        var real = 0f;
        foreach (var quote in nation.Market.Values)
            real += quote.Production * CommodityCatalog.Get(quote.Commodity).BasePrice;
        var perHead = real / people;
        if (!float.IsFinite(perHead) || perHead <= 0f) return;
        if (nation.IncomeBaseline <= 0f) { nation.IncomeBaseline = perHead; nation.IncomeFactor = 1f; return; }
        var d = campaign.Defines;
        var factor = MathF.Pow(perHead / nation.IncomeBaseline, MathF.Max(0f, d.IncomeDemandElasticity));
        if (float.IsFinite(factor))
            nation.IncomeFactor = M.Clamp(factor, MathF.Max(0.01f, d.IncomeFactorMinimum), MathF.Max(1f, d.IncomeFactorMaximum));
    }
}
