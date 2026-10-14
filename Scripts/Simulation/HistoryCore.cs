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
    // Kept by age rather than by count, the way a ledger is: every week of the last three
    // years, every fourth week back to ten, a point a quarter beyond that, and nothing older
    // than forty. Fifteen years of every week was 780 points a country and most of every
    // save - 47 MB of an 80 MB save in a long campaign - for charts that draw a decade in a
    // few hundred pixels. This keeps forty years in under half the points.
    public const int WeeklyDays = 3 * 364;
    public const int MonthlyDays = 10 * 364;
    public const int KeptDays = 40 * 364;
    /// <summary>An upper bound on what one country keeps, for whatever reads a count.</summary>
    public const int MaximumSamplesPerNation = WeeklyDays / 7 + (MonthlyDays - WeeklyDays) / 28 + (KeptDays - MonthlyDays) / 91 + 4;
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
                _samples[entry.Key] = entry.Value?.ToList() ?? new List<NationHistorySample>();
        _lastCapturedDay = _samples.Values.SelectMany(values => values).Select(sample => sample.Day).DefaultIfEmpty(-1).Max();
        // A save from before the thinning keeps all fifteen years of every week; thinned on
        // load, so the file it is saved back into is the smaller one. Against the same yearly
        // mark the campaign last thinned at, so a save made since loads exactly as it was.
        if (_lastCapturedDay >= 0)
            foreach (var values in _samples.Values) Thin(values, _lastCapturedDay / 364 * 364);
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
            // A country that is no more has no week to record. Its history ends where it
            // did, which is what its chart should show - and sixty dead countries writing a
            // row of zeros every week was a fifth of the table.
            if (nation.Movement is null && campaign.ProvinceMap is not null && !campaign.HasOwnedLand(nation) &&
                _samples.ContainsKey(nation.Id))
                continue;
            if (!_samples.TryGetValue(nation.Id, out var values))
            {
                values = new List<NationHistorySample>(MaximumSamplesPerNation);
                _samples[nation.Id] = values;
            }

            // At the precision a chart can show: four significant figures. Nine digits of a
            // GDP read by nothing but a line two hundred pixels tall were half of every row.
            values.Add(new NationHistorySample(
                campaign.ElapsedDays,
                Chart(nation.Gdp),
                Chart(nation.Treasury),
                Chart(nation.Population / 1_000_000f),
                Chart(nation.WeeklyTaxIncome + nation.WeeklyTariffIncome),
                Chart(nation.WeeklyExpenses),
                Chart(nation.WeeklyBalance),
                Chart(nation.Productivity),
                Chart(nation.StandardOfLiving),
                Chart(nation.Literacy),
                Chart(nation.Stability),
                Chart(nation.MarketAccess),
                Chart(nation.ShortageIndex),
                Chart(nation.WeeklyTradeBalance),
                Chart(militaryPower[nation.Id]),
                Chart(nation.ManpowerReserve / 1_000_000f),
                Chart(warSupport[nation.Id]),
                Chart(PriceIndex(nation, Commodity.Food)),
                Chart(PriceIndex(nation, Commodity.ConsumerGoods)),
                Chart(PriceIndex(nation, Commodity.IndustrialMaterials)),
                Chart(PriceIndex(nation, Commodity.Energy)),
                Chart(PriceIndex(nation, Commodity.Arms))));

            // Thinned once a year rather than every week: the cost is a pass over each list.
            if (campaign.ElapsedDays % 364 == 0) Thin(values, campaign.ElapsedDays);
        }

        _lastCapturedDay = campaign.ElapsedDays;
    }

    /// <summary>Four significant figures - what a chart, a tooltip or a ledger line can show.</summary>
    internal static float Chart(float value)
    {
        if (!float.IsFinite(value) || value == 0f) return value;
        var magnitude = (int)MathF.Floor(MathF.Log10(MathF.Abs(value)));
        var digits = Math.Clamp(3 - magnitude, 0, 7);
        return (float)Math.Round(value, digits, MidpointRounding.AwayFromZero);
    }

    /// <summary>
    /// The ledger's resolution by age: every sample of the last three years, the first of
    /// each four-week span back to ten, the first of each quarter back to forty, and none
    /// older. Spans are counted from day zero, so the same sample survives every pass and a
    /// thinned list thinned again is the same list.
    /// </summary>
    internal static void Thin(List<NationHistorySample> values, int today)
    {
        if (values.Count == 0) return;
        var kept = new List<NationHistorySample>(values.Count);
        var lastBucket = int.MinValue;
        foreach (var sample in values)
        {
            var age = today - sample.Day;
            if (age > KeptDays) continue;
            if (age <= WeeklyDays) { kept.Add(sample); lastBucket = int.MinValue; continue; }
            var span = age <= MonthlyDays ? 28 : 91;
            // Quarters are numbered below zero so they never meet a four-week span's number.
            var bucket = span == 28 ? sample.Day / 28 : -1 - sample.Day / 91;
            if (bucket == lastBucket) continue;
            lastBucket = bucket;
            kept.Add(sample);
        }
        if (kept.Count == values.Count) return;
        values.Clear();
        values.AddRange(kept);
    }

    private static float PriceIndex(Nation nation, Commodity commodity)
    {
        var basePrice = MathF.Max(0.0001f, CommodityCatalog.Get(commodity).BasePrice);
        return nation.Market.TryGetValue(commodity, out var quote) ? quote.Price / basePrice * 100f : 100f;
    }
}
