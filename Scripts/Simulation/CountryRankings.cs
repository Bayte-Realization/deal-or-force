using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A detached, read-only comparison of the countries visible to the caller.</summary>
public static class CountryRankings
{
    public enum Metric { Overall, Economy, Population, FieldPower, Technology, LivingStandards }

    public readonly record struct Weights(double Economy = 40, double Population = 15,
        double FieldPower = 30, double Technology = 15)
    {
        public Weights() : this(40, 15, 30, 15) { }
        public static Weights Default => new(40, 15, 30, 15);

        /// <summary>Finite, nonnegative percentages; an empty mix uses the default mix.</summary>
        public Weights Normalize()
        {
            var economy = Clean(Economy);
            var population = Clean(Population);
            var fieldPower = Clean(FieldPower);
            var technology = Clean(Technology);
            var largest = Math.Max(Math.Max(economy, population), Math.Max(fieldPower, technology));
            if (largest == 0) return Default;
            // Rescale before summing: even four double.MaxValue inputs cannot overflow.
            economy /= largest; population /= largest; fieldPower /= largest; technology /= largest;
            var total = economy + population + fieldPower + technology;
            return new(economy / total * 100, population / total * 100,
                fieldPower / total * 100, technology / total * 100);
        }
    }

    /// <summary>Raw national values, except Overall (0..100); rank belongs to the whole snapshot.</summary>
    public sealed record Row(int NationId, string Tag, string Name, int OverallRank,
        double Overall, double Economy, double Population, double FieldPower,
        double Technology, double LivingStandards)
    {
        public double Value(Metric metric) => metric switch
        {
            Metric.Economy => Economy,
            Metric.Population => Population,
            Metric.FieldPower => FieldPower,
            Metric.Technology => Technology,
            Metric.LivingStandards => LivingStandards,
            _ => Overall
        };
    }

    /// <summary>
    /// Overall is the weighted sum of four max-normalized national values. Inspection is
    /// checked before finding maxima, so hidden countries cannot disclose their statistics
    /// through visible countries' scores. FieldPower sums Army.Power for ground formations
    /// only; fleets and aircraft have no invented conversion into this measure.
    /// No simulation indices, assessment caches, commands or random stream are touched.
    /// </summary>
    public static IReadOnlyList<Row> Read(Campaign c, Weights weights, Func<Nation, bool> canInspect = null)
    {
        if (c is null) return Array.Empty<Row>();
        var groundOwners = new HashSet<int>();
        // The persisted kind belongs to this campaign. LakeRegions' index catalogue is
        // shared between worlds and can contain another campaign's land-region indices.
        foreach (var state in c.States)
            if (state.NationMapIndex > 0 && !state.Unclaimed &&
                !string.Equals(state.Kind, "Lake", StringComparison.OrdinalIgnoreCase))
                groundOwners.Add(state.NationMapIndex);

        var eligible = new List<Nation>();
        var power = new Dictionary<int, double>();
        foreach (var nation in c.Nations)
            if (nation.Population > 0 && !nation.Landless && groundOwners.Contains(nation.MapIndex) &&
                (canInspect is null || canInspect(nation)))
            {
                eligible.Add(nation);
                power[nation.Id] = 0;
            }
        foreach (var army in c.Armies)
            if (power.TryGetValue(army.NationId, out var strength))
                power[army.NationId] = strength + Clean(army.Power);

        var rows = new List<Row>(eligible.Count);
        double maxEconomy = 0, maxPopulation = 0, maxPower = 0, maxTechnology = 0;
        foreach (var nation in eligible)
        {
            var row = new Row(nation.Id, nation.Tag ?? "", nation.DisplayName ?? "", 0, 0,
                Clean(nation.Gdp), nation.Population, power[nation.Id], nation.Technologies.Count,
                Clean(nation.StandardOfLiving));
            rows.Add(row);
            maxEconomy = Math.Max(maxEconomy, row.Economy);
            maxPopulation = Math.Max(maxPopulation, row.Population);
            maxPower = Math.Max(maxPower, row.FieldPower);
            maxTechnology = Math.Max(maxTechnology, row.Technology);
        }
        var effective = weights.Normalize();
        for (var i = 0; i < rows.Count; i++)
        {
            var row = rows[i];
            var overall = Part(row.Economy, maxEconomy, effective.Economy) +
                Part(row.Population, maxPopulation, effective.Population) +
                Part(row.FieldPower, maxPower, effective.FieldPower) +
                Part(row.Technology, maxTechnology, effective.Technology);
            rows[i] = row with { Overall = Math.Clamp(overall, 0, 100) };
        }
        rows.Sort((a, b) => Compare(a, b, Metric.Overall, true));
        for (var i = 0; i < rows.Count; i++) rows[i] = rows[i] with { OverallRank = i + 1 };
        return rows.AsReadOnly();
    }

    /// <summary>Filters and orders a snapshot without changing its global ranks or values.</summary>
    public static IReadOnlyList<Row> Query(IReadOnlyList<Row> snapshot, Metric metric,
        bool descending = true, string search = null)
    {
        if (snapshot is null || snapshot.Count == 0) return Array.Empty<Row>();
        var term = search?.Trim() ?? "";
        var rows = snapshot.Where(row => term.Length == 0 ||
            row.Name.Contains(term, StringComparison.OrdinalIgnoreCase) ||
            row.Tag.Contains(term, StringComparison.OrdinalIgnoreCase)).ToList();
        rows.Sort((a, b) => Compare(a, b, metric, descending));
        return rows.AsReadOnly();
    }

    public static IReadOnlyList<Row> Sort(IReadOnlyList<Row> snapshot, Metric metric,
        bool descending = true, string search = null) => Query(snapshot, metric, descending, search);

    private static double Clean(double value) => double.IsFinite(value) && value >= 0 ? value : 0;
    private static double Part(double value, double maximum, double weight) => maximum > 0 ? value / maximum * weight : 0;
    private static int Compare(Row a, Row b, Metric metric, bool descending)
    {
        var compared = descending ? b.Value(metric).CompareTo(a.Value(metric)) : a.Value(metric).CompareTo(b.Value(metric));
        if (compared != 0) return compared;
        compared = StringComparer.Ordinal.Compare(a.Tag, b.Tag);
        return compared != 0 ? compared : a.NationId.CompareTo(b.NationId);
    }
}
