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
                entry.Value.BasePrice, entry.Value.EssentialWeight, entry.Value.Description);
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
                }
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
            foreach (var nation in campaign.Nations)
            {
                if (!nation.Market.TryGetValue(commodity, out var quote)) continue;
                totalDemand += quote.Demand;
                totalProduction += quote.Production;
            }
            totalDemand = MathF.Max(0.01f, totalDemand);
            totalProduction = MathF.Max(0.01f, totalProduction);

            var definition = CommodityCatalog.Get(commodity);
            var worldScarcity = M.Clamp(totalDemand / totalProduction, 0.55f, 2.75f);
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
            foreach (var quote in nation.Market.Values) grossOutput += quote.Production * quote.Price;
            nation.GrossOutput = grossOutput;

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
    /// </summary>
    internal static float JobsDemanded(ScenarioState state) =>
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
        // And what its technologies, spirits and government add (NationalModifiers): a share
        // on top, not a new input.
        var outputFactor = nation.EmployedShare * M.Clamp(nation.Productivity, 0.35f, 3.0f) *
                           NationalModifiers.Factor(nation, "output");

        var allocation = ProductionProgramme.Allocate(states, nation);
        ProductionProgramme.MigrateLegacy(nation, allocation);
        // A region the war has wrecked makes less of everything (WarDamage).
        foreach (var state in states)
            ProductionChains.RunRegion(nation, state, outputFactor * WarDamage.OutputFactor(campaign, nation, state), allocation);

        // Four LINQ scans of four global lists, once per country, with a capturing closure
        // each. That is a thousand closures and half a million list steps every market week,
        // to answer four questions that one pass over each list answers for everybody.
        var tallies = Tallies(campaign, nation.Id);
        ProductionChains.RunNation(nation, tallies.Projects, tallies.Divisions, tallies.Hulls,
            tallies.AtWar);
    }
}
