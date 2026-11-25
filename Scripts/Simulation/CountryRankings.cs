using System;
using System.Collections.Generic;
using System.Linq;

namespace DealOrForce.Simulation;

/// <summary>A detached, read-only comparison of the countries visible to the caller.</summary>
public static class CountryRankings
{
    public enum Metric { Overall, Economy, Population, FieldPower, Technology, Prestige, LivingStandards }

    public readonly record struct Weights(double Economy = 42, double Population = 14,
        double FieldPower = 12, double Technology = 17, double Prestige = 15)
    {
        public Weights() : this(42, 14, 12, 17, 15) { }
        public Weights(double economy, double population, double fieldPower, double technology)
            : this(economy, population, fieldPower, technology, 0) { }
        public static Weights Default => new(42, 14, 12, 17, 15);

        /// <summary>Finite, nonnegative percentages; an empty mix uses the default mix.</summary>
        public Weights Normalize()
        {
            var economy = Clean(Economy);
            var population = Clean(Population);
            var fieldPower = Clean(FieldPower);
            var technology = Clean(Technology);
            var prestige = Clean(Prestige);
            var largest = Math.Max(Math.Max(Math.Max(economy, population), Math.Max(fieldPower, technology)), prestige);
            if (largest == 0) return Default;
            // Rescale before summing: even five double.MaxValue inputs cannot overflow.
            economy /= largest; population /= largest; fieldPower /= largest; technology /= largest; prestige /= largest;
            var total = economy + population + fieldPower + technology + prestige;
            return new(economy / total * 100, population / total * 100,
                fieldPower / total * 100, technology / total * 100, prestige / total * 100);
        }
    }

    /// <summary>Raw national values, except Overall (0..100); rank belongs to the whole snapshot.</summary>
    public sealed record Row(int NationId, string Tag, string Name, int OverallRank,
        double Overall, double Economy, double Population, double FieldPower,
        double Technology, double LivingStandards, double Prestige = 0)
    {
        public double Value(Metric metric) => metric switch
        {
            Metric.Economy => Economy,
            Metric.Population => Population,
            Metric.FieldPower => FieldPower,
            Metric.Technology => Technology,
            Metric.Prestige => Prestige,
            Metric.LivingStandards => LivingStandards,
            _ => Overall
        };
    }

    /// <summary>
    /// Overall is the weighted sum of five max-normalized national values. Inspection is
    /// checked before finding maxima, so hidden countries cannot disclose their statistics
    /// through visible countries' scores. FieldPower sums Army.Power for ground formations
    /// only; fleets and aircraft have no invented conversion into this measure.
    /// No simulation indices, assessment caches, commands or random stream are touched.
    /// </summary>
    private readonly record struct ScratchRow(
        int NationId, string Tag, string Name, double Economy, double Population,
        double FieldPower, double Technology, double LivingStandards, double Prestige, double Overall = 0);

    [ThreadStatic] private static HashSet<int> t_groundOwners;
    [ThreadStatic] private static Dictionary<int, double> t_power;
    [ThreadStatic] private static List<ScratchRow> t_scratch;
    [ThreadStatic] private static bool t_inUse;

    public static IReadOnlyList<Row> Read(Campaign c, Weights weights, Func<Nation, bool> canInspect = null)
    {
        if (c is null) return Array.Empty<Row>();
        var reentrant = t_inUse;
        HashSet<int> groundOwners;
        Dictionary<int, double> power;
        List<ScratchRow> scratch;
        if (!reentrant)
        {
            t_inUse = true;
            groundOwners = t_groundOwners ??= new HashSet<int>(256);
            power = t_power ??= new Dictionary<int, double>(256);
            scratch = t_scratch ??= new List<ScratchRow>(256);
            groundOwners.Clear();
            power.Clear();
            scratch.Clear();
        }
        else
        {
            groundOwners = new HashSet<int>(256);
            power = new Dictionary<int, double>(256);
            scratch = new List<ScratchRow>(256);
        }

        try
        {
            // The persisted kind belongs to this campaign. LakeRegions' index catalogue is
            // shared between worlds and can contain another campaign's land-region indices.
            var states = c.States;
            for (var i = 0; i < states.Count; i++)
            {
                var state = states[i];
                if (state.NationMapIndex > 0 && !state.Unclaimed &&
                    !string.Equals(state.Kind, "Lake", StringComparison.OrdinalIgnoreCase))
                    groundOwners.Add(state.NationMapIndex);
            }

            var nations = c.Nations;
            for (var i = 0; i < nations.Count; i++)
            {
                var nation = nations[i];
                if (nation.Population > 0 && !nation.Landless && groundOwners.Contains(nation.MapIndex) &&
                    (canInspect is null || canInspect(nation)))
                {
                    power[nation.Id] = 0;
                    scratch.Add(new ScratchRow(
                        nation.Id,
                        nation.Tag ?? "",
                        nation.DisplayName ?? "",
                        Clean(nation.Gdp),
                        nation.Population,
                        0,
                        nation.Technologies.Count,
                        Clean(nation.StandardOfLiving),
                        Clean(nation.Prestige)));
                }
            }

            var armies = c.Armies;
            for (var i = 0; i < armies.Count; i++)
            {
                var army = armies[i];
                if (power.TryGetValue(army.NationId, out var strength))
                    power[army.NationId] = strength + Clean(army.Power);
            }

            double maxEconomy = 0, maxPopulation = 0, maxPower = 0, maxTechnology = 0, maxPrestige = 0;
            for (var i = 0; i < scratch.Count; i++)
            {
                var item = scratch[i];
                var fp = power[item.NationId];
                item = item with { FieldPower = fp };
                scratch[i] = item;
                if (item.Economy > maxEconomy) maxEconomy = item.Economy;
                if (item.Population > maxPopulation) maxPopulation = item.Population;
                if (fp > maxPower) maxPower = fp;
                if (item.Technology > maxTechnology) maxTechnology = item.Technology;
                if (item.Prestige > maxPrestige) maxPrestige = item.Prestige;
            }

            var effective = weights.Normalize();
            for (var i = 0; i < scratch.Count; i++)
            {
                var item = scratch[i];
                var overall = Part(item.Economy, maxEconomy, effective.Economy) +
                    Part(item.Population, maxPopulation, effective.Population) +
                    Part(item.FieldPower, maxPower, effective.FieldPower) +
                    Part(item.Technology, maxTechnology, effective.Technology) +
                    Part(item.Prestige, maxPrestige, effective.Prestige);
                scratch[i] = item with { Overall = Math.Clamp(overall, 0, 100) };
            }

            scratch.Sort(CompareScratchOverallDesc);
            var result = new Row[scratch.Count];
            for (var i = 0; i < scratch.Count; i++)
            {
                var item = scratch[i];
                result[i] = new Row(item.NationId, item.Tag, item.Name, i + 1, item.Overall,
                    item.Economy, item.Population, item.FieldPower, item.Technology, item.LivingStandards, item.Prestige);
            }
            return Array.AsReadOnly(result);
        }
        finally
        {
            if (!reentrant) t_inUse = false;
        }
    }

    private static int CompareScratchOverallDesc(ScratchRow a, ScratchRow b)
    {
        var compared = b.Overall.CompareTo(a.Overall);
        if (compared != 0) return compared;
        compared = StringComparer.Ordinal.Compare(a.Tag, b.Tag);
        return compared != 0 ? compared : a.NationId.CompareTo(b.NationId);
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
