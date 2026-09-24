using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>
/// Compact weekly telemetry used by the ledger, tooltips, AI diagnostics and saves.
/// It deliberately stores values instead of presentation-ready strings so mods and
/// alternative frontends can reuse the same deterministic history.
///
/// Saved as a row of numbers rather than an object: see <see cref="NationHistorySampleConverter"/>.
/// </summary>
[System.Text.Json.Serialization.JsonConverter(typeof(NationHistorySampleConverter))]
public readonly record struct NationHistorySample(
    int Day,
    float Gdp,
    float Treasury,
    float PopulationMillions,
    float WeeklyIncome,
    float WeeklyExpenses,
    float WeeklyBalance,
    float Productivity,
    float StandardOfLiving,
    float Literacy,
    float Stability,
    float MarketAccess,
    float ShortageIndex,
    float TradeBalance,
    float MilitaryPower,
    float ManpowerMillions,
    float WarSupport,
    float FoodPriceIndex,
    float ConsumerPriceIndex,
    float IndustrialPriceIndex,
    float EnergyPriceIndex,
    float ArmsPriceIndex)
{
    public float PriceIndex(Commodity commodity) => commodity switch
    {
        Commodity.Food => FoodPriceIndex,
        Commodity.ConsumerGoods => ConsumerPriceIndex,
        Commodity.IndustrialMaterials => IndustrialPriceIndex,
        Commodity.Energy => EnergyPriceIndex,
        Commodity.Arms => ArmsPriceIndex,
        _ => 100f
    };
}

public sealed class CampaignHistory
{
    // Fifteen years of weekly points for every country is enough for useful trend
    // analysis while keeping century-long campaigns at a stable memory footprint.
    public const int MaximumSamplesPerNation = 52 * 15;
    private readonly Dictionary<int, List<NationHistorySample>> _samples = new();
    private int _lastCapturedDay = -1;

    public IReadOnlyList<NationHistorySample> ForNation(int nationId) =>
        _samples.TryGetValue(nationId, out var values) ? values : Array.Empty<NationHistorySample>();

    public void Reset()
    {
        _samples.Clear();
        _lastCapturedDay = -1;
    }

    internal Dictionary<int, List<NationHistorySample>> Export() =>
        _samples.ToDictionary(entry => entry.Key, entry => new List<NationHistorySample>(entry.Value));

    internal void Restore(Dictionary<int, List<NationHistorySample>> samples)
    {
        _samples.Clear();
        if (samples is not null)
            foreach (var entry in samples)
                _samples[entry.Key] = entry.Value?.TakeLast(MaximumSamplesPerNation).ToList() ?? new List<NationHistorySample>();
        _lastCapturedDay = _samples.Values.SelectMany(values => values).Select(sample => sample.Day).DefaultIfEmpty(-1).Max();
    }

    public void Capture(Campaign campaign, bool force = false)
    {
        if (campaign is null || (!force && campaign.ElapsedDays % 7 != 0) || _lastCapturedDay == campaign.ElapsedDays)
            return;

        var militaryPower = new float[campaign.Nations.Count];
        foreach (var army in campaign.Armies)
            if (army.NationId >= 0 && army.NationId < militaryPower.Length)
                militaryPower[army.NationId] += army.Power;
        foreach (var fleet in campaign.Fleets)
            if (fleet.NationId >= 0 && fleet.NationId < militaryPower.Length)
                militaryPower[fleet.NationId] += fleet.Hulls * 0.32f;

        var warSupport = campaign.Nations.Select(nation => nation.WarSupport).ToArray();
        foreach (var war in campaign.Wars.Where(war => !war.Ended))
        {
            if (war.AttackerId >= 0 && war.AttackerId < warSupport.Length)
                warSupport[war.AttackerId] = MathF.Min(warSupport[war.AttackerId], war.AttackerWarSupport);
            if (war.DefenderId >= 0 && war.DefenderId < warSupport.Length)
                warSupport[war.DefenderId] = MathF.Min(warSupport[war.DefenderId], war.DefenderWarSupport);
        }

        foreach (var nation in campaign.Nations)
        {
            if (!_samples.TryGetValue(nation.Id, out var values))
            {
                values = new List<NationHistorySample>(MaximumSamplesPerNation);
                _samples[nation.Id] = values;
            }

            values.Add(new NationHistorySample(
                campaign.ElapsedDays,
                nation.Gdp,
                nation.Treasury,
                nation.Population / 1_000_000f,
                nation.WeeklyTaxIncome + nation.WeeklyTariffIncome,
                nation.WeeklyExpenses,
                nation.WeeklyBalance,
                nation.Productivity,
                nation.StandardOfLiving,
                nation.Literacy,
                nation.Stability,
                nation.MarketAccess,
                nation.ShortageIndex,
                nation.WeeklyTradeBalance,
                militaryPower[nation.Id],
                nation.ManpowerReserve / 1_000_000f,
                warSupport[nation.Id],
                PriceIndex(nation, Commodity.Food),
                PriceIndex(nation, Commodity.ConsumerGoods),
                PriceIndex(nation, Commodity.IndustrialMaterials),
                PriceIndex(nation, Commodity.Energy),
                PriceIndex(nation, Commodity.Arms)));

            if (values.Count > MaximumSamplesPerNation)
                values.RemoveRange(0, values.Count - MaximumSamplesPerNation);
        }

        _lastCapturedDay = campaign.ElapsedDays;
    }

    private static float PriceIndex(Nation nation, Commodity commodity)
    {
        var basePrice = MathF.Max(0.0001f, CommodityCatalog.Get(commodity).BasePrice);
        return nation.Market.TryGetValue(commodity, out var quote) ? quote.Price / basePrice * 100f : 100f;
    }
}
