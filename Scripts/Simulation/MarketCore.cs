using System;
using System.Collections.Frozen;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text.Json;
using System.Threading;
using DealOrForce.Simulation.Kernels;

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

public enum TradePolicy { OpenMarkets, BalancedTrade, StrategicAutarky, Protectionism }

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
    public float IncomeDemandElasticity { get; set; } = 0.6f;
    public float IncomeFactorMinimum { get; set; } = 0.5f;
    public float IncomeFactorMaximum { get; set; } = 3f;

    /// <summary>
    /// How many times its first jobs per worker a country may build to before more buildings
    /// add less, and the power its plant yields at past that (MarketSystem.CapitalReturns):
    /// diminishing returns to capital. 0 turns them off.
    /// </summary>
    public float CapitalDeepeningHeadroom { get; set; } = 1.0f;
    public float CapitalDeepeningShare { get; set; } = 0.3f;

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

    internal sealed class SnapshotData
    {
        public readonly Commodity[] AllGoods;
        public readonly CommodityDefinition[] ByIndex;
        public readonly string[] IdByIndex;
        public readonly int[] SlotByCommodity;
        public readonly FrozenDictionary<string, Commodity> NamedFrozen;

        public SnapshotData(Commodity[] allGoods, CommodityDefinition[] byIndex, string[] idByIndex, int[] slotByCommodity,
            FrozenDictionary<string, Commodity> namedFrozen)
        {
            AllGoods = allGoods;
            ByIndex = byIndex;
            IdByIndex = idByIndex;
            SlotByCommodity = slotByCommodity;
            NamedFrozen = namedFrozen;
        }
    }

    private static volatile SnapshotData _snapshot;

    internal static SnapshotData Snapshot => _snapshot ?? BuildSnapshot();

    private static SnapshotData BuildSnapshot()
    {
        lock (Definitions)
        {
            if (_snapshot is { } existing) return existing;
            var count = Definitions.Count;
            var allGoods = new Commodity[count];
            var maxIdx = -1;
            var pos = 0;
            foreach (var key in Definitions.Keys)
            {
                allGoods[pos++] = key;
                if ((int)key > maxIdx) maxIdx = (int)key;
            }
            var len = maxIdx + 1;
            var byIndex = len > 0 ? new CommodityDefinition[len] : Array.Empty<CommodityDefinition>();
            var idByIndex = len > 0 ? new string[len] : Array.Empty<string>();
            var slotByCommodity = len > 0 ? new int[len] : Array.Empty<int>();
            if (len > 0) Array.Fill(slotByCommodity, -1);
            for (var i = 0; i < count; i++)
            {
                var c = allGoods[i];
                var idx = (int)c;
                if ((uint)idx < (uint)len)
                {
                    byIndex[idx] = Definitions[c];
                    slotByCommodity[idx] = i;
                    if (Ids.TryGetValue(c, out var id)) idByIndex[idx] = id;
                }
            }
            var built = new SnapshotData(allGoods, byIndex, idByIndex, slotByCommodity,
                Named.ToFrozenDictionary(StringComparer.OrdinalIgnoreCase));
            _snapshot = built;
            return built;
        }
    }

    /// <summary>A good the catalog has forgotten - a mod's, after the mod is gone.</summary>
    private static readonly CommodityDefinition Unknown =
        new("Unknown good", "?", 0.02f, 0f, "Declared by content that is no longer loaded.");

    public static IEnumerable<Commodity> All => Snapshot.AllGoods;

    public static CommodityDefinition Get(Commodity commodity)
    {
        var byIdx = Snapshot.ByIndex;
        var idx = (int)commodity;
        return (uint)idx < (uint)byIdx.Length && byIdx[idx] is { } definition ? definition : Unknown;
    }

    /// <summary>The name this good has in a file, which is what a save should be keyed by.</summary>
    public static string IdOf(Commodity commodity)
    {
        var ids = Snapshot.IdByIndex;
        var idx = (int)commodity;
        return (uint)idx < (uint)ids.Length && ids[idx] is { } id ? id : idx.ToString();
    }

    /// <summary>Stable raster icon key, including data-defined goods outside the enum.</summary>
    public static string IconNameOf(Commodity commodity) => "good_" + IdOf(commodity).ToLowerInvariant();

    static CommodityCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        lock (Definitions)
        {
            _snapshot = null;
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
            _snapshot = null;
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
        : id.AsSpan().IndexOfAny('_', '-') < 0
            ? id
            : id.Replace("_", "", StringComparison.Ordinal).Replace("-", "", StringComparison.Ordinal);

    private static void Register(Commodity commodity, string id)
    {
        Named[Squash(id)] = commodity;
        Ids[commodity] = id;
        if ((int)commodity >= _nextId) _nextId = (int)commodity + 1;
        _snapshot = null;
    }

    /// <summary>Which good a file means, if the catalog has one by that name.</summary>
    public static bool TryParse(string id, out Commodity commodity)
    {
        var squashed = Squash(id);
        if (_snapshot is { } snap) return snap.NamedFrozen.TryGetValue(squashed, out commodity);
        return Named.TryGetValue(squashed, out commodity);
    }

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
    /// <summary>
    /// The part of <see cref="Imports"/> that came in under a trade agreement's tariff cut, weighted
    /// by the cut: free of the import markup and of duty in that proportion. Reset with the imports.
    /// </summary>
    public float Relieved;
    public float Net => Production + Imports - Demand - Exports;
    public bool IsShortage => Satisfaction < 0.985f;
}

/// <summary>
/// A weekly, deterministic world clearing pass. Countries first satisfy demand from
/// local production, then trade the remaining surpluses. A shortage therefore has a
/// real cause and propagates into prices, GDP, living standards and the treasury.
///
/// ═══════════════════════════════════════════════════════════════════════════════
/// FOR MODDERS — how the market works and where to change it:
/// ═══════════════════════════════════════════════════════════════════════════════
///
/// The clearing runs once a week (EconomyTickDays, default 7) and is deterministic:
/// the same world state always clears to the same prices, in the same order, so a
/// seed replays exactly.
///
/// 1. ADD A COMMODITY → `game/common/trade/*.json`. Each key is an id; the fields
///    are `name`, `icon` (a glyph), `basePrice`, `essentialWeight` (0 to 1: how much
///    a shortage of it hurts), and `description`. The game registers it on load.
///
/// 2. MAKE A COUNTRY PRODUCE OR NEED IT → `game/common/production/*.json` (what
///    each building adds to output) and `game/common/terrain/*.json` (what each
///    terrain type naturally produces or consumes).
///
/// 3. WHAT HAPPENS IN A SHORTAGE → the satisfaction share (how much of demand was
///    met) feeds into `nation.ShortageIndex` (average across essentials), which the
///    HUD shows as an alert, the stability system penalises, and the AI tries to fix
///    by building, trading or conquering.
///
/// 4. PRICES → each commodity's price eases toward its clearing level (what the
///    world's supply and demand say it is worth) at a rate set by the defines. A
///    mod that wants instant prices sets that rate to 1.0.
///
/// 5. TRADE ROUTES ON THE MAP → GlobeTradeTraffic.cs draws them from the data the
///    clearing produced (who is selling what to whom); a mod that adds a commodity
///    gets its routes for free.
/// ═══════════════════════════════════════════════════════════════════════════════
/// </summary>
public static class MarketSystem
{
    public static void Seed(Campaign campaign)
    {
        var allGoods = CommodityCatalog.Snapshot.AllGoods;
        for (var ni = 0; ni < campaign.Nations.Count; ni++)
        {
            var nation = campaign.Nations[ni];
            nation.Market.Clear();
            for (var gi = 0; gi < allGoods.Length; gi++)
            {
                var commodity = allGoods[gi];
                nation.Market[commodity] = new MarketQuote
                {
                    Commodity = commodity,
                    Price = CommodityCatalog.Get(commodity).BasePrice
                };
            }
        }
        Recalculate(campaign, false);
    }

    public static void Tick(Campaign campaign)
    {
        foreach (var _ in TickStages(campaign)) { }
    }

    /// <summary>Day-of-cycle slot on which weekly global market clearing runs (day 0).</summary>
    internal static int ScheduleSlot(int cycle) => 0;

    /// <summary>The week's market in stages, for the smoothed step (EconomySystem.TickStages).</summary>
    public static IEnumerable<int> TickStages(Campaign campaign)
    {
        if (!MechanicFeatures.SystemEnabled(campaign, nameof(MarketSystem))) yield break;
        var cycle = Math.Max(1, campaign.Defines.EconomyTickDays);
        if (campaign.ElapsedDays <= 0 || campaign.ElapsedDays % cycle != ScheduleSlot(cycle)) yield break;
        foreach (var _ in RecalculateStages(campaign, true)) yield return 0;
    }

    /// <summary>
    /// One market pass at day zero, with prices set straight to their clearing level
    /// rather than eased toward it, so the calibration in EconomySystem.CalibrateOutput
    /// measures a settled market instead of one still on its way there.
    /// </summary>
    public static void Prime(Campaign campaign) => Recalculate(campaign, false);

    /// <summary>
    /// The markup on a good bought abroad. What came in under a tariff cut carries none of it, in
    /// proportion to the cut: a trade agreement's cut is a cut in what the importer pays, not only
    /// in what its customs house collects. 1 + (markup - 1) x (1 - relief x relieved / imports).
    /// </summary>
    public static float ImportMarkupOf(Defines defines, MarketQuote quote)
    {
        EconomyRulesHook.EvaluateTradeCost(
            quote.Imports, quote.Relieved, 0f,
            defines.ImportMarkup, defines.TradeAgreementMarkupRelief, 0f,
            out var importMarkup, out _);
        return importMarkup;
    }

    /// <summary>The week's duty on one good, before the treasury's share: forgone on the relieved part.</summary>
    public static float DutyOf(Nation nation, MarketQuote quote)
    {
        EconomyRulesHook.EvaluateTradeCost(
            quote.Imports, quote.Relieved, quote.Price,
            1f, 0f, TradePolicyRules.TariffRate(nation.TradePolicy),
            out _, out var duty);
        return duty;
    }

    private static void Recalculate(Campaign campaign, bool adjustPrice)
    {
        foreach (var _ in RecalculateStages(campaign, adjustPrice)) { }
    }

    /// <summary>How many countries' balances one stage of the market works out in parallel.</summary>
    private const int BalanceStageCountries = 32;

    /// <summary>How many goods one stage of the pricing settles before checking frame budget.</summary>
    private const int PricingStageCommodities = 6;

    [ThreadStatic] private static List<ScenarioState> _stateScratch;
    [ThreadStatic] private static List<Nation> _marketNationsScratch;
    [ThreadStatic] private static NationTally[] _tallyDenseScratch;
    private static readonly ConditionalWeakTable<Campaign, KernelBatchBuffers> KernelBuffers = new();

    private sealed class KernelBatchBuffers
    {
        public float[] BasePrices = Array.Empty<float>();
        public byte[] TradedWorld = Array.Empty<byte>();
        public float[] EssentialWeights = Array.Empty<float>();
        public float[] TariffRates = Array.Empty<float>();
        public float[] Demand = Array.Empty<float>();
        public float[] Production = Array.Empty<float>();
        public float[] Imports = Array.Empty<float>();
        public float[] Exports = Array.Empty<float>();
        public float[] Relieved = Array.Empty<float>();
        public float[] Satisfaction = Array.Empty<float>();
        public float[] Prices = Array.Empty<float>();
        public MarketQuote[] QuoteRefs = Array.Empty<MarketQuote>();

        public Nation[] ClearNations = Array.Empty<Nation>();
        public readonly List<Nation> FallbackNations = new();
        public float[] GrossOutput = Array.Empty<float>();
        public float[] RealOutput = Array.Empty<float>();
        public float[] ShortageIndex = Array.Empty<float>();
        public float[] MarketAccess = Array.Empty<float>();
        public float[] WeeklyTradeBalance = Array.Empty<float>();
        public float[] WeeklyTariffIncome = Array.Empty<float>();

        private MarketQuote[][] _nationQuoteRows = Array.Empty<MarketQuote[]>();
        private int[] _nationQuoteCounts = Array.Empty<int>();
        private object _quoteCacheCatalogSnap;
        private int _quoteMaxGoodIndex = -1;
        public MarketQuote[][] ActivePriceQuoteRows = Array.Empty<MarketQuote[]>();

        public void PrepareQuoteTable(IReadOnlyList<Nation> nations, CommodityCatalog.SnapshotData catalogSnap)
        {
            var maxId = -1;
            for (var i = 0; i < nations.Count; i++)
                if (nations[i].Id > maxId) maxId = nations[i].Id;

            if (maxId >= _nationQuoteRows.Length)
            {
                var next = Math.Max(maxId + 64, 128);
                Array.Resize(ref _nationQuoteRows, next);
                Array.Resize(ref _nationQuoteCounts, next);
            }

            var maxGood = Math.Max(0, catalogSnap.ByIndex.Length - 1);
            if (!ReferenceEquals(_quoteCacheCatalogSnap, catalogSnap) || _quoteMaxGoodIndex != maxGood)
            {
                Array.Fill(_nationQuoteCounts, -1);
                _quoteCacheCatalogSnap = catalogSnap;
                _quoteMaxGoodIndex = maxGood;
            }
        }

        public MarketQuote[] GetNationQuotes(Nation nation, CommodityCatalog.SnapshotData catalogSnap)
        {
            var id = nation.Id;
            if ((uint)id >= (uint)_nationQuoteRows.Length) return null;
            var marketCount = nation.Market.Count;
            var row = _nationQuoteRows[id];
            if (row is not null && _nationQuoteCounts[id] == marketCount &&
                ReferenceEquals(_quoteCacheCatalogSnap, catalogSnap))
            {
                return row;
            }
            var needed = Math.Max(_quoteMaxGoodIndex + 1, catalogSnap.ByIndex.Length);
            if (row is null || row.Length < needed)
                _nationQuoteRows[id] = row = new MarketQuote[needed];
            else
                Array.Clear(row);

            foreach (var quote in nation.Market.Values)
            {
                var idx = (int)quote.Commodity;
                if ((uint)idx < (uint)row.Length)
                    row[idx] = quote;
            }
            _nationQuoteCounts[id] = marketCount;
            return row;
        }

        public void InvalidateNationQuotes(int nationId)
        {
            if ((uint)nationId < (uint)_nationQuoteCounts.Length)
                _nationQuoteCounts[nationId] = -1;
        }

        public void EnsurePriceCapacity(int goodCount, int nationCount)
        {
            if (BasePrices.Length < goodCount) BasePrices = new float[goodCount];
            if (TradedWorld.Length < goodCount) TradedWorld = new byte[goodCount];
            if (ActivePriceQuoteRows.Length < nationCount) ActivePriceQuoteRows = new MarketQuote[nationCount][];
            var total = goodCount * nationCount;
            if (Demand.Length < total) Demand = new float[total];
            if (Production.Length < total) Production = new float[total];
            if (Imports.Length < total) Imports = new float[total];
            if (Relieved.Length < total) Relieved = new float[total];
            if (Satisfaction.Length < total) Satisfaction = new float[total];
            if (Prices.Length < total) Prices = new float[total];
            if (QuoteRefs.Length < total) QuoteRefs = new MarketQuote[total];
        }

        public void EnsureClearCapacity(int nationCount, int goodCount)
        {
            if (BasePrices.Length < goodCount) BasePrices = new float[goodCount];
            if (EssentialWeights.Length < goodCount) EssentialWeights = new float[goodCount];
            if (TariffRates.Length < nationCount) TariffRates = new float[nationCount];
            if (ClearNations.Length < nationCount) ClearNations = new Nation[nationCount];
            if (GrossOutput.Length < nationCount) GrossOutput = new float[nationCount];
            if (RealOutput.Length < nationCount) RealOutput = new float[nationCount];
            if (ShortageIndex.Length < nationCount) ShortageIndex = new float[nationCount];
            if (MarketAccess.Length < nationCount) MarketAccess = new float[nationCount];
            if (WeeklyTradeBalance.Length < nationCount) WeeklyTradeBalance = new float[nationCount];
            if (WeeklyTariffIncome.Length < nationCount) WeeklyTariffIncome = new float[nationCount];
            var total = nationCount * goodCount;
            if (Production.Length < total) Production = new float[total];
            if (Demand.Length < total) Demand = new float[total];
            if (Imports.Length < total) Imports = new float[total];
            if (Exports.Length < total) Exports = new float[total];
            if (Relieved.Length < total) Relieved = new float[total];
            if (Satisfaction.Length < total) Satisfaction = new float[total];
            if (Prices.Length < total) Prices = new float[total];
        }
    }

    private static volatile int _jobCacheVersion = -1;
    private static float[] _jobOrderJobs = Array.Empty<float>();
    private static bool _hasExtraJobBuildings;

    private static void EnsureJobOrderCache()
    {
        var ver = BuildingCatalog.Version;
        if (_jobCacheVersion == ver && _jobOrderJobs.Length == JobOrder.Length) return;
        var arr = new float[JobOrder.Length];
        for (var i = 0; i < JobOrder.Length; i++)
            arr[i] = BuildingCatalog.Get(JobOrder[i]).Jobs;
        _jobOrderJobs = arr;
        _hasExtraJobBuildings = BuildingCatalog.HasExtraJobBuildings;
        _jobCacheVersion = ver;
    }

    private static IEnumerable<int> RecalculateStages(Campaign campaign, bool adjustPrice)
    {
        // Warm read-only lookup tables before parallel market passes read them concurrently.
        ControlTally.World(campaign, 0, 0);
        HeldRegions.Refresh(campaign);
        var administrations = HeldRegions.CommittedAdministrations(campaign);
        campaign.NationsByMapIndex();
        campaign.StatesOf(0);
        campaign.StateByMapIndex(1);
        Rulers.Found(campaign);
        EnsureJobOrderCache();
        var active = campaign.ActiveNations;
        var activeCount = active.Count;
        var tallies = CollectTallies(campaign);
        var catalogSnap = CommodityCatalog.Snapshot;
        var batchBuffers = KernelBuffers.GetOrCreateValue(campaign);
        batchBuffers.PrepareQuoteTable(campaign.Nations, catalogSnap);
        if (activeCount > 0 && Campaign.StageOverdue()) yield return 0;

        // A whole-step caller has no frame to hand back: the countries go to the workers as one
        // loop, one wake-up of the pool and an even spread, instead of eight loops of thirty-two.
        // Each country's balance is its own (it reads the week's tables and writes its own market),
        // so the chunking is the wall time and nothing else.
        var balanceChunk = Campaign.IsUnboundedStep ? Math.Max(1, activeCount) : BalanceStageCountries;
        for (var start = 0; start < activeCount; start += balanceChunk)
        {
            var end = Math.Min(activeCount, start + balanceChunk);
            using (SimProfiler.Measure("    mkt:balance"))
            {
                DeterministicParallel.For(campaign, start, end, index =>
                    CalculateNationalBalance(campaign, active[index], tallies.Get(active[index].Id), administrations, batchBuffers), minItems: 8);
            }
            if (end < activeCount && Campaign.StageOverdue()) yield return 0;
        }

        // Who actually sells to whom. This used to be an anonymous pool: a shortfall was
        // met by "the world" at a constant rate, so nobody could be cut off and a trade
        // partner was worth one opinion modifier. Every import and export below is now a
        // named route to a named country - see TradeNetwork.
        foreach (var _ in TradeNetwork.RouteStages(campaign)) yield return 0;

        // Prices, a few goods a stage. Each good's world totals read every country and its
        // price writes to every country, but no good reads another, so a stage can end
        // between any two of them. This used to be one block at the end of a market week -
        // 7,8 ms that a frame had to carry whole, on the week a market week runs.
        var catalogue = catalogSnap.AllGoods;
        var marketNations = _marketNationsScratch ??= new List<Nation>(activeCount);
        marketNations.Clear();
        for (var i = 0; i < active.Count; i++)
        {
            var n = active[i];
            if (n.Market.Count > 0) marketNations.Add(n);
        }
        var pricingChunk = Campaign.IsUnboundedStep ? catalogue.Length : PricingStageCommodities;
        for (var firstGood = 0; firstGood < catalogue.Length; firstGood += pricingChunk)
        {
            var lastGood = Math.Min(firstGood + pricingChunk, catalogue.Length);
            using (SimProfiler.Measure("    mkt:prices"))
                SettlePrices(campaign, adjustPrice, catalogSnap, catalogue, firstGood, lastGood, marketNations);
            if (lastGood < catalogue.Length && Campaign.StageOverdue()) yield return 0;
        }

        // And what each country made of its own week - shortages, output, income, tariffs -
        // a chunk of countries at a time, in the same order as before: country by country.
        var commerce = NavalOperations.BeginCommerceBatch(campaign);
        var commerceDay = campaign.ElapsedDays;
        var commerceRev = campaign.MapRevision;
        var commerceFleets = campaign.Fleets.Count;
        var settleChunk = Campaign.IsUnboundedStep ? Math.Max(1, activeCount) : BalanceStageCountries;
        for (var firstNation = 0; firstNation < activeCount; firstNation += settleChunk)
        {
            if (commerceDay != campaign.ElapsedDays || commerceRev != campaign.MapRevision ||
                commerceFleets != campaign.Fleets.Count)
            {
                commerce = NavalOperations.BeginCommerceBatch(campaign);
                commerceDay = campaign.ElapsedDays;
                commerceRev = campaign.MapRevision;
                commerceFleets = campaign.Fleets.Count;
            }
            var lastNation = Math.Min(activeCount, firstNation + settleChunk);
            using (SimProfiler.Measure("    mkt:nations"))
                SettleNations(campaign, active, firstNation, lastNation, commerce, catalogSnap);
            if (lastNation < activeCount && Campaign.StageOverdue()) yield return 0;
        }
    }

    /// <summary>Goods <paramref name="from"/> to <paramref name="to"/>: the world's price, and each country's share of it.</summary>
    private static void SettlePrices(Campaign campaign, bool adjustPrice, CommodityCatalog.SnapshotData catalogSnap,
        Commodity[] catalogue, int from, int to, List<Nation> nations)
    {
        var goodCount = to - from;
        var nationCount = nations.Count;
        if (goodCount <= 0 || nationCount <= 0) return;

        var byIndex = catalogSnap.ByIndex;
        var buf = KernelBuffers.GetOrCreateValue(campaign);
        buf.PrepareQuoteTable(campaign.Nations, catalogSnap);
        buf.EnsurePriceCapacity(goodCount, nationCount);

        var nationQuoteRows = buf.ActivePriceQuoteRows;
        for (var i = 0; i < nationCount; i++)
            nationQuoteRows[i] = buf.GetNationQuotes(nations[i], catalogSnap);

        for (var g = 0; g < goodCount; g++)
        {
            var commodity = catalogue[from + g];
            var cidx = (int)commodity;
            var definition = (uint)cidx < (uint)byIndex.Length && byIndex[cidx] is { } found
                ? found
                : CommodityCatalog.Get(commodity);
            buf.BasePrices[g] = definition.BasePrice;
            buf.TradedWorld[g] = definition.TradedWorldPrice ? (byte)1 : (byte)0;

            var row = g * nationCount;
            for (var i = 0; i < nationCount; i++)
            {
                var idx = row + i;
                var qRow = nationQuoteRows[i];
                var quote = qRow is not null && (uint)cidx < (uint)qRow.Length
                    ? qRow[cidx]
                    : nations[i].Market.GetValueOrDefault(commodity);
                if (quote is null)
                {
                    buf.QuoteRefs[idx] = null;
                    buf.Demand[idx] = 0f;
                    buf.Production[idx] = 0f;
                    buf.Imports[idx] = 0f;
                    buf.Relieved[idx] = 0f;
                    buf.Prices[idx] = 0f;
                    continue;
                }
                buf.QuoteRefs[idx] = quote;
                buf.Demand[idx] = quote.Demand;
                buf.Production[idx] = quote.Production;
                buf.Imports[idx] = quote.Imports;
                buf.Relieved[idx] = quote.Relieved;
                buf.Prices[idx] = quote.Price;
            }
        }

        var total = goodCount * nationCount;
        NativeMarketKernels.SettlePrices(
            goodCount,
            nationCount,
            nationCount,
            buf.BasePrices.AsSpan(0, goodCount),
            buf.TradedWorld.AsSpan(0, goodCount),
            buf.Demand.AsSpan(0, total),
            buf.Production.AsSpan(0, total),
            buf.Imports.AsSpan(0, total),
            buf.Relieved.AsSpan(0, total),
            adjustPrice,
            campaign.Defines.ImportMarkup,
            campaign.Defines.TradeAgreementMarkupRelief,
            campaign.Defines.PriceAdjustSpeed,
            buf.Satisfaction.AsSpan(0, total),
            buf.Prices.AsSpan(0, total));

        for (var idx = 0; idx < total; idx++)
        {
            var quote = buf.QuoteRefs[idx];
            if (quote is null) continue;
            buf.QuoteRefs[idx] = null;
            quote.Satisfaction = buf.Satisfaction[idx];
            quote.Price = buf.Prices[idx];
        }
    }

    /// <summary>Countries <paramref name="from"/> to <paramref name="to"/>: what the week left each of them with.</summary>
    private static void SettleNations(Campaign campaign, IReadOnlyList<Nation> active, int from, int to,
        NavalOperations.CommerceBatch commerce, CommodityCatalog.SnapshotData catalogSnap)
    {
        var batchCount = to - from;
        if (batchCount <= 0) return;

        var catalogue = catalogSnap.AllGoods;
        var goodCount = catalogue.Length;
        if (goodCount == 0)
        {
            for (var index = from; index < to; index++)
                SettleSingleNation(campaign, active[index], commerce, catalogSnap);
            return;
        }

        var byIndex = catalogSnap.ByIndex;
        var buf = KernelBuffers.GetOrCreateValue(campaign);
        buf.PrepareQuoteTable(campaign.Nations, catalogSnap);
        buf.EnsureClearCapacity(batchCount, goodCount);

        for (var g = 0; g < goodCount; g++)
        {
            var commodity = catalogue[g];
            var cidx = (int)commodity;
            var definition = (uint)cidx < (uint)byIndex.Length && byIndex[cidx] is { } found
                ? found
                : CommodityCatalog.Get(commodity);
            buf.BasePrices[g] = definition.BasePrice;
            buf.EssentialWeights[g] = definition.EssentialWeight;
        }

        var packedCount = 0;
        buf.FallbackNations.Clear();
        for (var index = from; index < to; index++)
        {
            var nation = active[index];
            if (nation.Market.Count < goodCount)
            {
                buf.FallbackNations.Add(nation);
                continue;
            }

            var qRow = buf.GetNationQuotes(nation, catalogSnap);
            var row = packedCount * goodCount;
            var complete = true;
            for (var g = 0; g < goodCount; g++)
            {
                var cidx = (int)catalogue[g];
                var quote = qRow is not null && (uint)cidx < (uint)qRow.Length
                    ? qRow[cidx]
                    : nation.Market.GetValueOrDefault(catalogue[g]);
                if (quote is null)
                {
                    complete = false;
                    break;
                }
                var idx = row + g;
                buf.Production[idx] = quote.Production;
                buf.Demand[idx] = quote.Demand;
                buf.Imports[idx] = quote.Imports;
                buf.Exports[idx] = quote.Exports;
                buf.Relieved[idx] = quote.Relieved;
                buf.Satisfaction[idx] = quote.Satisfaction;
                buf.Prices[idx] = quote.Price;
            }

            if (!complete)
            {
                buf.FallbackNations.Add(nation);
                continue;
            }

            buf.ClearNations[packedCount] = nation;
            buf.TariffRates[packedCount] = TradePolicyRules.TariffRate(nation.TradePolicy);
            packedCount++;
        }

        if (packedCount > 0)
        {
            var total = packedCount * goodCount;
            NativeMarketKernels.ClearNations(
                packedCount,
                goodCount,
                goodCount,
                buf.BasePrices.AsSpan(0, goodCount),
                buf.EssentialWeights.AsSpan(0, goodCount),
                buf.TariffRates.AsSpan(0, packedCount),
                buf.Production.AsSpan(0, total),
                buf.Demand.AsSpan(0, total),
                buf.Imports.AsSpan(0, total),
                buf.Exports.AsSpan(0, total),
                buf.Relieved.AsSpan(0, total),
                buf.Satisfaction.AsSpan(0, total),
                buf.Prices.AsSpan(0, total),
                campaign.Defines.MarketTreasuryTradeShare,
                campaign.Defines.GdpPriceShare,
                campaign.Defines.GdpPriceBand,
                buf.GrossOutput.AsSpan(0, packedCount),
                buf.RealOutput.AsSpan(0, packedCount),
                buf.ShortageIndex.AsSpan(0, packedCount),
                buf.MarketAccess.AsSpan(0, packedCount),
                buf.WeeklyTradeBalance.AsSpan(0, packedCount),
                buf.WeeklyTariffIncome.AsSpan(0, packedCount));

            for (var ni = 0; ni < packedCount; ni++)
            {
                var nation = buf.ClearNations[ni];
                buf.ClearNations[ni] = null;
                nation.GrossOutput = buf.GrossOutput[ni];
                MeasureIncome(campaign, nation, buf.RealOutput[ni]);
                nation.ShortageIndex = buf.ShortageIndex[ni];
                nation.MarketAccess = buf.MarketAccess[ni];
                nation.WeeklyTradeBalance = buf.WeeklyTradeBalance[ni];
                NavalOperations.ApplyCommerceEffects(campaign, nation, null, commerce);
                nation.WeeklyTariffIncome = buf.WeeklyTariffIncome[ni];
            }
        }

        if (buf.FallbackNations.Count > 0)
        {
            for (var fi = 0; fi < buf.FallbackNations.Count; fi++)
                SettleSingleNation(campaign, buf.FallbackNations[fi], commerce, catalogSnap);
            buf.FallbackNations.Clear();
        }
    }

    private static void SettleSingleNation(Campaign campaign, Nation nation, NavalOperations.CommerceBatch commerce,
        CommodityCatalog.SnapshotData catalogSnap)
    {
        var weight = 0f;
        var shortages = 0f;
        var trade = 0f;
        var tariffs = 0f;
        // The value of what this country actually produced this week. EconomySystem
        // derives GDP from it, so output is a consequence of buildings, workers and
        // prices rather than a number that grows on its own beside them.
        var grossOutput = 0f;
        var real = 0f;
        var marketTradeShare = campaign.Defines.MarketTreasuryTradeShare;
        // At base price plus a share of the difference (Defines.GdpPriceShare): real output with
        // the terms of trade in it, not every shortage in the world counted as growth.
        var priceShare = M.Clamp01(campaign.Defines.GdpPriceShare);
        // And within GdpPriceBand of the base: a world short of everything put prices at
        // their cap of nearly three times, and the country making those goods doubled its
        // GDP in two years - the United States reached two fifths of the world economy.
        var band = MathF.Max(0f, campaign.Defines.GdpPriceBand);
        var bandLow = MathF.Max(0f, 1f - band);
        var bandHigh = 1f + band;
        var tariffRate = TradePolicyRules.TariffRate(nation.TradePolicy);
        var byIndex = catalogSnap.ByIndex;

        foreach (var quote in nation.Market.Values)
        {
            var cidx = (int)quote.Commodity;
            var definition = (uint)cidx < (uint)byIndex.Length && byIndex[cidx] is { } found
                ? found
                : CommodityCatalog.Get(quote.Commodity);
            weight += definition.EssentialWeight;
            shortages += (1f - quote.Satisfaction) * definition.EssentialWeight;
            trade += (quote.Exports - quote.Imports) * quote.Price;
            EconomyRulesHook.EvaluateTradeCost(quote.Imports, quote.Relieved, quote.Price,
                1f, 0f, tariffRate, out _, out var duty);
            tariffs += duty;

            var basePrice = definition.BasePrice;
            var price = M.Clamp(quote.Price, basePrice * bandLow, basePrice * bandHigh);
            // What was sold, not what was made: the country's own use of its own goods and what
            // it sent abroad. A mine working a deposit nobody wants piles up ore, not income -
            // the United States mined rare earths it had no buyer for, at the world's floor
            // price, until they were half its economy.
            var sold = MathF.Min(quote.Production, MathF.Max(0f, quote.Demand - quote.Imports) + quote.Exports);
            grossOutput += sold * (basePrice + priceShare * (price - basePrice));
            real += quote.Production * basePrice;
        }
        nation.GrossOutput = grossOutput;
        MeasureIncome(campaign, nation, real);

        nation.ShortageIndex = weight <= 0f ? 0f : M.Clamp01(shortages / weight);
        nation.MarketAccess = 1f - nation.ShortageIndex;
        nation.WeeklyTradeBalance = trade * marketTradeShare;

        // What the enemy's navy takes off the sea lanes, and what one's own escorts
        // keep. This is where a fleet stops being scenery and becomes money.
        NavalOperations.ApplyCommerceEffects(campaign, nation, null, commerce);
        nation.WeeklyTariffIncome = tariffs * marketTradeShare;
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

    private static readonly int[] JobSlotByKind = BuildJobSlotByKind();

    private static int[] BuildJobSlotByKind()
    {
        var max = 0;
        for (var i = 0; i < JobOrder.Length; i++)
            if ((int)JobOrder[i] > max) max = (int)JobOrder[i];
        var map = new int[max + 1];
        Array.Fill(map, -1);
        for (var i = 0; i < JobOrder.Length; i++)
            map[(int)JobOrder[i]] = i;
        return map;
    }

    internal static float JobsDemanded(ScenarioState state)
    {
        if (state.Buildings.Count == 0) return 0f;
        EnsureJobOrderCache();
        var weights = _jobOrderJobs;
        Span<int> counts = stackalloc int[JobOrder.Length];
        var slotMap = JobSlotByKind;
        var hasExtra = false;
        foreach (var (kind, count) in state.Buildings)
        {
            var idx = (int)kind;
            if ((uint)idx < (uint)slotMap.Length && slotMap[idx] >= 0)
                counts[slotMap[idx]] = count;
            else if (_hasExtraJobBuildings && idx > (int)BuildingKind.ResearchLab && count != 0)
                hasExtra = true;
        }
        var jobs = 0f;
        for (var k = 0; k < JobOrder.Length; k++)
            if (counts[k] != 0)
                jobs += counts[k] * weights[k];
        if (hasExtra)
        {
            foreach (var (kind, count) in state.Buildings)
            {
                if ((int)kind <= (int)BuildingKind.ResearchLab || count == 0) continue;
                jobs += count * BuildingCatalog.Get(kind).Jobs;
            }
        }
        return jobs;
    }

    /// <summary>
    /// Workers a state can actually put in those jobs, in millions. Not everyone alive is
    /// available: children, the retired and the unemployable are already priced out by the
    /// working-age share, and the pop groups carry their own employment level on top.
    /// </summary>
    internal static float WorkersAvailable(ScenarioState state)
    {
        const float WorkingAgeShare = 0.62f;
        var groups = System.Runtime.InteropServices.CollectionsMarshal.AsSpan(state.PopulationGroups);
        if (groups.Length == 0) return state.Population / 1_000_000f * WorkingAgeShare;

        var workers = 0f;
        for (var i = 0; i < groups.Length; i++)
        {
            var group = groups[i];
            // The upper class runs capital rather than filling a shift; everyone else works.
            var participation = group.Class == SocialClass.Upper ? 0.15f : 1f;
            workers += group.Size / 1_000_000f * WorkingAgeShare * group.Employment * participation;
        }
        return workers;
    }

    /// <summary>What a country has under construction, under arms, at sea and at war.</summary>
    private struct NationTally
    {
        public int Projects;
        public float Divisions;
        public int Hulls;
        public bool AtWar;
    }

    private const int DenseNationTallyLimit = 4096;

    private readonly struct TallyTable
    {
        public readonly NationTally[] Dense;
        public readonly Dictionary<int, NationTally> Overflow;

        public TallyTable(NationTally[] dense, Dictionary<int, NationTally> overflow)
        {
            Dense = dense;
            Overflow = overflow;
        }

        public NationTally Get(int nationId) =>
            (uint)nationId < (uint)Dense.Length ? Dense[nationId] :
            Overflow is not null && Overflow.TryGetValue(nationId, out var tally) ? tally : default;
    }

    private static void AddProjectTally(NationTally[] dense, ref Dictionary<int, NationTally> overflow, int nationId)
    {
        if ((uint)nationId < (uint)dense.Length)
        {
            dense[nationId].Projects++;
            return;
        }
        overflow ??= new Dictionary<int, NationTally>();
        var t = overflow.GetValueOrDefault(nationId);
        t.Projects++;
        overflow[nationId] = t;
    }

    private static void AddDivisionsTally(NationTally[] dense, ref Dictionary<int, NationTally> overflow, int nationId, float divisions)
    {
        if ((uint)nationId < (uint)dense.Length)
        {
            dense[nationId].Divisions += divisions;
            return;
        }
        overflow ??= new Dictionary<int, NationTally>();
        var t = overflow.GetValueOrDefault(nationId);
        t.Divisions += divisions;
        overflow[nationId] = t;
    }

    private static void AddHullsTally(NationTally[] dense, ref Dictionary<int, NationTally> overflow, int nationId, int hulls)
    {
        if ((uint)nationId < (uint)dense.Length)
        {
            dense[nationId].Hulls += hulls;
            return;
        }
        overflow ??= new Dictionary<int, NationTally>();
        var t = overflow.GetValueOrDefault(nationId);
        t.Hulls += hulls;
        overflow[nationId] = t;
    }

    private static void SetAtWarTally(NationTally[] dense, ref Dictionary<int, NationTally> overflow, int nationId)
    {
        if ((uint)nationId < (uint)dense.Length)
        {
            dense[nationId].AtWar = true;
            return;
        }
        overflow ??= new Dictionary<int, NationTally>();
        var t = overflow.GetValueOrDefault(nationId);
        t.AtWar = true;
        overflow[nationId] = t;
    }

    /// <summary>The four global lists, walked once each per market week into a local table.</summary>
    private static TallyTable CollectTallies(Campaign campaign)
    {
        var maxId = -1;
        for (var i = 0; i < campaign.Nations.Count; i++)
        {
            var id = campaign.Nations[i].Id;
            if (id >= 0 && id < DenseNationTallyLimit && id > maxId) maxId = id;
        }
        var needed = maxId + 1;
        var dense = _tallyDenseScratch;
        if (dense is null || dense.Length < needed)
            _tallyDenseScratch = dense = needed > 0 ? new NationTally[Math.Max(needed, 64)] : Array.Empty<NationTally>();
        if (needed > 0) Array.Clear(dense, 0, needed);
        Dictionary<int, NationTally> overflow = null;

        foreach (var project in campaign.Construction)
        {
            // Materials are bought for the sites worked, not for every site open: twelve sites in
            // Albania, one worked at a time, bought ten times the industrial materials it used.
            if (!ConstructionQueue.WorkedToday(project)) continue;
            AddProjectTally(dense, ref overflow, project.NationId);
        }
        foreach (var army in campaign.Armies)
            AddDivisionsTally(dense, ref overflow, army.NationId, army.Divisions);
        foreach (var fleet in campaign.Fleets)
            AddHullsTally(dense, ref overflow, fleet.NationId, fleet.Hulls);
        var wars = Warfare.LiveWars(campaign);
        for (var index = 0; index < wars.Count; index++)
        {
            var war = wars[index];
            SetAtWarTally(dense, ref overflow, war.AttackerId);
            SetAtWarTally(dense, ref overflow, war.DefenderId);
        }
        return new TallyTable(dense, overflow);
    }

    private static void CalculateNationalBalance(Campaign campaign, Nation nation, NationTally tallies,
        IReadOnlyDictionary<int, List<ScenarioState>> administrations, KernelBatchBuffers batchBuffers = null)
    {
        // Every good the catalog knows, not only when the market is empty.
        //
        // This used to seed only a market with nothing in it, which was right while the
        // twenty goods were fixed and is wrong now that a mod can declare a twenty-first.
        // Enabling such a mod and loading an existing campaign left every country without a
        // quote for the new good, and the trade pass reads that quote by index - so the
        // first market week after enabling the mod threw. Topping up costs one dictionary
        // probe per good per country per week and cannot be got wrong.
        // Walked as the snapshot's array (the order CommodityCatalog.All gives) rather than through
        // the interface, which boxed an enumerator for every country every market week.
        var catalogSnap = CommodityCatalog.Snapshot;
        var allGoods = catalogSnap.AllGoods;
        var quoteRow = batchBuffers?.GetNationQuotes(nation, catalogSnap);
        if (quoteRow is not null && nation.Market.Count >= allGoods.Length)
        {
            var allPresent = true;
            for (var gi = 0; gi < allGoods.Length; gi++)
            {
                var cidx = (int)allGoods[gi];
                if ((uint)cidx >= (uint)quoteRow.Length || quoteRow[cidx] is null)
                {
                    allPresent = false;
                    break;
                }
            }
            if (allPresent)
            {
                for (var qi = 0; qi < quoteRow.Length; qi++)
                {
                    var quote = quoteRow[qi];
                    if (quote is null) continue;
                    quote.Production = 0f;
                    quote.Demand = 0f;
                    quote.Imports = 0f;
                    quote.Exports = 0f;
                }
            }
            else
            {
                for (var gi = 0; gi < allGoods.Length; gi++)
                {
                    var commodity = allGoods[gi];
                    if (!nation.Market.ContainsKey(commodity))
                        nation.Market[commodity] = new MarketQuote { Commodity = commodity, Price = CommodityCatalog.Get(commodity).BasePrice };
                }
                batchBuffers!.InvalidateNationQuotes(nation.Id);
                foreach (var quote in nation.Market.Values)
                {
                    quote.Production = 0f;
                    quote.Demand = 0f;
                    quote.Imports = 0f;
                    quote.Exports = 0f;
                }
            }
        }
        else
        {
            var added = false;
            for (var gi = 0; gi < allGoods.Length; gi++)
            {
                var commodity = allGoods[gi];
                if (!nation.Market.ContainsKey(commodity))
                {
                    nation.Market[commodity] = new MarketQuote { Commodity = commodity, Price = CommodityCatalog.Get(commodity).BasePrice };
                    added = true;
                }
            }
            if (added) batchBuffers?.InvalidateNationQuotes(nation.Id);

            foreach (var quote in nation.Market.Values)
            {
                quote.Production = 0f;
                quote.Demand = 0f;
                quote.Imports = 0f;
                quote.Exports = 0f;
            }
        }

        // The regions whose economy answers to it: its own, less those another country holds,
        // and those it holds of others (HeldRegions) - held ground is ground.
        var states = HeldRegions.FillEconomyOf(campaign, nation, _stateScratch ??= new List<ScenarioState>(),
            refresh: false, administrations: administrations);
        var defines = campaign.Defines;

        // How much of the labour its buildings ask for the nation can actually staff.
        // A country whose industry has outgrown its workforce runs its plant below
        // capacity instead of conjuring output from nowhere, which is what makes
        // population a real economic input rather than a display statistic.
        var demandedJobs = 0f;
        var availableWorkers = 0f;
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            demandedJobs += JobsDemanded(state);
            availableWorkers += WorkersAvailable(state);
        }
        nation.EmployedShare = StaffingShare(demandedJobs, availableWorkers);
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
        // What the country's quotes say to every one of its regions is asked once, here
        // (ProductionChains.RegionFactors); nothing between here and the last region writes
        // a price or a satisfaction.
        var factors = ProductionChains.PrepareRegions(nation, defines);
        for (var i = 0; i < states.Count; i++)
        {
            var state = states[i];
            ProductionChains.RunRegion(nation, state, outputFactor * WarDamage.OutputFactor(campaign, nation, state) *
                HeldRegions.WorkedAt(campaign, nation, state), allocation, factors, defines);
        }

        ProductionChains.RunNation(nation, tallies.Projects, tallies.Divisions, tallies.Hulls,
            tallies.AtWar, defines);
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
    /// asks for 0.95 workers per worker it has and Greenland for 130 - not a census. Counted on
    /// the plant the country can staff: a building with nobody to work it is EmployedShare's
    /// business, and adds nothing rather than taking output away.
    /// </summary>
    /// <summary>
    /// How much of its rated output a country's plant gives this week: the share of the jobs it
    /// can staff, times how productive the staff are, its modifiers, the diminishing return on
    /// capital past its headroom, and any recession.
    /// </summary>
    private static float NationalOutputFactor(Campaign campaign, Nation nation, float demandedJobs, float availableWorkers) =>
        nation.EmployedShare * M.Clamp(nation.Productivity, 0.35f, 3.0f) *
        NationalModifiers.FactorSlot(nation, NationalModifiers.SlotOutput) *
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
        foreach (var region in HeldRegions.EconomyOf(campaign, nation, refresh: false))
        {
            demandedJobs += JobsDemanded(region);
            availableWorkers += WorkersAvailable(region);
        }
        return NationalOutputFactor(campaign, nation, demandedJobs, availableWorkers) *
               WarDamage.OutputFactor(campaign, nation, state) * HeldRegions.WorkedAt(campaign, nation, state);
    }

    /// <summary>The share of the jobs its buildings ask for that a country can staff (Nation.EmployedShare).</summary>
    internal static float StaffingShare(float demandedJobs, float availableWorkers) =>
        demandedJobs <= 0.001f ? 1f : M.Clamp(availableWorkers / demandedJobs, 0.25f, 1f);

    internal static float CapitalReturns(Campaign campaign, Nation nation, float demandedJobs, float availableWorkers)
    {
        var headroom = campaign.Defines.CapitalDeepeningHeadroom;
        var reference = nation.CapitalIntensityReference;
        if (headroom <= 0f || reference <= 0f || availableWorkers <= 0.001f || demandedJobs <= 0.001f) return 1f;
        // Measured on the plant the country can staff. Past full employment EmployedShare already
        // spreads the workers thinner over every building, so a building nobody can work adds
        // nothing; counting the unstaffed plant here as well made it take output away - output
        // went as workers x (jobs/workers)^(share-1), falling with every building, and a peaceful
        // Switzerland that built 29 plants for the workers of 11 halved its GDP in ten years.
        var staffed = MathF.Min(demandedJobs, availableWorkers);
        var deepening = staffed / availableWorkers / (reference * headroom);
        if (!float.IsFinite(deepening) || deepening <= 1f) return 1f;
        var share = M.Clamp01(campaign.Defines.CapitalDeepeningShare);
        return M.Clamp(MathF.Pow(deepening, share - 1f), 0.1f, 1f);
    }

    private static void MeasureIncome(Campaign campaign, Nation nation, float real)
    {
        var people = nation.Population / 1_000_000f;
        if (people <= 0.001f) return;
        var perHead = real / people;
        if (!float.IsFinite(perHead) || perHead <= 0f) return;
        if (nation.IncomeBaseline <= 0f) { nation.IncomeBaseline = perHead; nation.IncomeFactor = 1f; return; }
        var d = campaign.Defines;
        var factor = MathF.Pow(perHead / nation.IncomeBaseline, MathF.Max(0f, d.IncomeDemandElasticity));
        if (float.IsFinite(factor))
            nation.IncomeFactor = M.Clamp(factor, MathF.Max(0.01f, d.IncomeFactorMinimum), MathF.Max(1f, d.IncomeFactorMaximum));
    }
}
