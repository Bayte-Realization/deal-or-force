using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;
using Metric = DealOrForce.Simulation.CountryRankings.Metric;
using Weights = DealOrForce.Simulation.CountryRankings.Weights;

static class CountryRankingTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("country rankings: " + label); Console.WriteLine("PASS country rankings: " + label); }

    static bool Near(double a, double b) => Math.Abs(a - b) < .00001;
    static string Ids(IReadOnlyList<CountryRankings.Row> rows) => string.Join(",", rows.Select(r => r.NationId));
    static CountryRankings.Row Of(IReadOnlyList<CountryRankings.Row> rows, int id) => rows.Single(r => r.NationId == id);

    static Nation Add(Campaign c, int id, string tag, long population = 1000, float gdp = 0, bool ground = true)
    {
        var nation = new Nation { Id = id, MapIndex = 101 + id, Tag = tag, Name = "Ranking " + tag,
            Population = population, Gdp = gdp };
        c.Nations.Add(nation);
        if (ground) c.States.Add(new ScenarioState { MapIndex = 101 + id, NationMapIndex = nation.MapIndex,
            Population = population, Name = nation.Name });
        return nation;
    }

    static Campaign World(int seed = 8231)
    {
        var c = new Campaign(seed, new Defines());
        var a = Add(c, 0, "RANK_A", 1000, 300); a.StandardOfLiving = .3f;
        var b = Add(c, 1, "RANK_B", 4000, 150); b.StandardOfLiving = .9f;
        var d = Add(c, 2, "RANK_C", 2000, 0); d.StandardOfLiving = .6f;
        a.Technologies.AddRange(new[] { "rank_one", "rank_two" });
        b.Technologies.Add("rank_one");
        d.Technologies.AddRange(new[] { "rank_one", "rank_two", "rank_three", "rank_four" });
        c.Armies.Add(new Army { Id = 0, NationId = 0, Divisions = 1 });
        c.Armies.Add(new Army { Id = 1, NationId = 1, Divisions = 2 });
        c.Armies.Add(new Army { Id = 2, NationId = 2, Divisions = 4 });
        return c;
    }

    public static void Run()
    {
        ColumnsAndQueries();
        WeightSafety();
        MembershipAndFog();
        LakeCatalogueIsolation();
        StableTies();
        InvalidValues();
        ReadOnlyAndFresh();
    }

    static void ColumnsAndQueries()
    {
        var c = World();
        var rows = CountryRankings.Read(c, Weights.Default);
        Check(Ids(rows) == "0,1,2" && rows.Select(r => r.OverallRank).SequenceEqual(new[] { 1, 2, 3 }),
            "overall order and global ordinal positions follow the default composite");
        Check(Near(Of(rows, 0).Overall, 58.75) && Near(Of(rows, 1).Overall, 53.75) && Near(Of(rows, 2).Overall, 52.5),
            "default composite exposes the 40/15/30/15 max-normalized calculation");
        var first = Of(rows, 0);
        Check(first.Tag == c.Nations[0].Tag && first.Name == c.Nations[0].DisplayName &&
            first.Economy == 300 && first.Population == 1000 && first.FieldPower == c.Armies[0].Power &&
            first.Technology == 2 && Near(first.LivingStandards, .3), "row values are existing national facts and actual ground army power");

        var orders = new Dictionary<Metric, string> {
            [Metric.Overall] = "0,1,2", [Metric.Economy] = "0,1,2", [Metric.Population] = "1,2,0",
            [Metric.FieldPower] = "2,1,0", [Metric.Technology] = "2,0,1", [Metric.LivingStandards] = "1,2,0" };
        foreach (var entry in orders)
        {
            Check(Ids(CountryRankings.Query(rows, entry.Key, true)) == entry.Value, entry.Key + " sorts descending");
            var reversed = string.Join(",", entry.Value.Split(',').Reverse());
            Check(Ids(CountryRankings.Sort(rows, entry.Key, false)) == reversed, entry.Key + " sorts ascending");
            Check(rows.All(r => double.IsFinite(r.Value(entry.Key))), entry.Key + " values are finite");
        }
        var selected = CountryRankings.Query(rows, Metric.Population, true, "  rAnK_c  ");
        Check(selected.Count == 1 && selected[0].NationId == 2 && selected[0].OverallRank == 3,
            "case-insensitive tag search keeps the global rank instead of renumbering");
        Check(CountryRankings.Query(rows, Metric.Economy, false, "rAnKiNg").Count == 3,
            "search includes display names");
        Check(CountryRankings.Query(rows, Metric.Overall, true, "not-a-country").Count == 0,
            "unmatched search returns an empty view");
        Check(Ids(rows) == "0,1,2" && first == Of(rows, 0), "queries leave the original snapshot untouched");
        Check(rows is IList<CountryRankings.Row> list && list.IsReadOnly, "the returned collection rejects in-place mutation");
    }

    static void WeightSafety()
    {
        var c = World();
        var defaults = CountryRankings.Read(c, Weights.Default);
        Check(new Weights() == Weights.Default && default(Weights).Normalize() == Weights.Default,
            "both constructor and zero-valued struct safely resolve the default mix");
        Check(defaults.SequenceEqual(CountryRankings.Read(c, new Weights(0, 0, 0, 0))), "zero mix falls back to defaults");
        Check(defaults.SequenceEqual(CountryRankings.Read(c, new Weights(double.NaN, double.PositiveInfinity, -1, double.NegativeInfinity))),
            "an entirely invalid mix falls back to defaults");
        var cases = new[] {
            (new Weights(8, 0, 0, 0), 0, 1, 50d), (new Weights(0, 8, 0, 0), 1, 2, 50d),
            (new Weights(0, 0, 8, 0), 2, 1, 50d), (new Weights(0, 0, 0, 8), 2, 0, 50d) };
        foreach (var (weights, winner, runner, value) in cases)
        {
            var rows = CountryRankings.Read(c, weights);
            Check(rows[0].NationId == winner && Near(rows[0].Overall, 100) && Near(Of(rows, runner).Overall, value),
                "a single weighted component determines the result: " + weights);
        }
        Check(new Weights(double.NaN, 8, -1, double.PositiveInfinity).Normalize() == new Weights(0, 100, 0, 0),
            "invalid individual weights become zero while valid weights remain effective");
        var huge = new Weights(double.MaxValue, double.MaxValue, double.MaxValue, double.MaxValue).Normalize();
        Check(huge == new Weights(25, 25, 25, 25) && CountryRankings.Read(c, huge).All(r => double.IsFinite(r.Overall)),
            "malicious huge weights cannot overflow the total");
        Check(new Weights(double.Epsilon, 0, 0, 0).Normalize() == new Weights(100, 0, 0, 0),
            "tiny positive weights normalize without underflowing to an empty mix");
        c.Nations[0].StandardOfLiving = 100;
        Check(defaults.Select(r => r.Overall).SequenceEqual(CountryRankings.Read(c, Weights.Default).Select(r => r.Overall)),
            "living standards is an independent displayed column, outside the composite");
    }

    static void MembershipAndFog()
    {
        var c = World();
        Add(c, 3, "RANK_EMPTY", 0, float.MaxValue);
        Add(c, 4, "RANK_NEGATIVE", -100, float.MaxValue);
        Add(c, 5, "RANK_LANDLESS", 1000, float.MaxValue).Movement = new MovementState { Landless = true };
        Add(c, 6, "RANK_WATER", 1000, float.MaxValue); c.States[^1].Kind = "Lake";
        Add(c, 7, "RANK_MISSING", 1000, float.MaxValue, ground: false);
        Add(c, 8, "RANK_UNCLAIMED", 1000, float.MaxValue); c.States[^1].Unclaimed = true;
        Check(Ids(CountryRankings.Read(c, Weights.Default)) == "0,1,2",
            "zero/negative population, landless movements, water-only, missing and unclaimed ground are excluded");

        var baseline = CountryRankings.Read(c, Weights.Default);
        var hidden = Add(c, 9, "RANK_HIDDEN", long.MaxValue, float.MaxValue);
        for (var i = 0; i < 100; i++) hidden.Technologies.Add("rank_hidden_" + i);
        c.Armies.Add(new Army { Id = 9, NationId = 9, Divisions = float.MaxValue });
        Check(baseline.SequenceEqual(CountryRankings.Read(c, Weights.Default, n => n.Id != 9)),
            "an invisible maximum cannot change visible rows, scores or ranks");
        Check(CountryRankings.Read(c, Weights.Default, _ => false).Count == 0, "inspection can hide every row");
        Check(CountryRankings.Read(new Campaign(17, new Defines()), Weights.Default).Count == 0 &&
            CountryRankings.Read(null, Weights.Default).Count == 0, "empty and absent campaigns have empty snapshots");
        var one = new Campaign(18, new Defines()); Add(one, 0, "RANK_ONLY", gdp: 1);
        var only = CountryRankings.Read(one, Weights.Default).Single();
        Check(only.OverallRank == 1 && Near(only.Overall, 55), "one country receives only components that have a positive maximum");
        one.States[0].Population = 0;
        Check(CountryRankings.Read(one, Weights.Default).Count == 1, "owned non-water ground need not itself have residents");
    }

    static void LakeCatalogueIsolation()
    {
        var lakeIndices = (HashSet<int>)typeof(LakeRegions)
            .GetField("LakeStateIndices", BindingFlags.Static | BindingFlags.NonPublic).GetValue(null);
        var savedIndices = lakeIndices.ToArray();
        try
        {
            lakeIndices.Clear();
            var c = World(); c.States[0].MapIndex = 2000;
            var baseline = CountryRankings.Read(c, Weights.Default);
            var other = World(99); other.States[0].MapIndex = 2000; other.States[0].Kind = "lAkE";
            // Reproduce the shared catalogue left by generating the other world's lake,
            // without requiring a hydrology raster in this projection test.
            lakeIndices.Add(other.States[0].MapIndex);
            var fingerprint = CampaignFingerprint.Of(c); var random = c.Random.State;
            var otherFingerprint = CampaignFingerprint.Of(other); var otherRandom = other.Random.State;

            Check(LakeRegions.IsWaterState(c.States[0]), "fixture reproduces a foreign lake index colliding with owned land");
            Check(baseline.SequenceEqual(CountryRankings.Read(c, Weights.Default)),
                "another campaign's lake catalogue cannot remove owned land or alter rankings");
            Check(CountryRankings.Read(other, Weights.Default).All(r => r.NationId != 0),
                "persisted lake kind excludes water regardless of casing");
            lakeIndices.Clear();
            Check(CountryRankings.Read(other, Weights.Default).All(r => r.NationId != 0) &&
                baseline.SequenceEqual(CountryRankings.Read(c, Weights.Default)),
                "persisted lake and land kinds remain authoritative with an empty shared catalogue");
            Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random &&
                CampaignFingerprint.Of(other) == otherFingerprint && other.Random.State == otherRandom && lakeIndices.Count == 0,
                "cross-world lake eligibility reads preserve campaign state, random streams and the catalogue");
        }
        finally
        {
            lakeIndices.Clear();
            lakeIndices.UnionWith(savedIndices);
        }
        Check(lakeIndices.SetEquals(savedIndices), "lake isolation fixture restores the original shared catalogue");
    }

    static void StableTies()
    {
        var c = World();
        foreach (var n in c.Nations) { n.Gdp = 0; n.Population = 1000; n.Technologies.Clear(); n.StandardOfLiving = .5f; }
        c.Armies.Clear();
        c.Nations[0].Tag = "ZZZ"; c.Nations[0].CustomName = "Alpha";
        c.Nations[1].Tag = "AAA"; c.Nations[1].CustomName = "Zulu";
        c.Nations[2].Tag = "AAA"; c.Nations[2].CustomName = "Bravo";
        var before = CountryRankings.Read(c, Weights.Default);
        Check(Ids(before) == "1,2,0", "ties use ordinal tags then nation ids, never localized names");
        c.Nations.Reverse(); c.States.Reverse(); c.Armies.Reverse();
        var after = CountryRankings.Read(c, Weights.Default);
        Check(before.SequenceEqual(after), "reversed insertion produces identical global ranks");
        foreach (var metric in Enum.GetValues<Metric>())
            Check(Ids(CountryRankings.Query(after, metric, true)) == "1,2,0" &&
                Ids(CountryRankings.Query(after, metric, false)) == "1,2,0", metric + " ties remain stable in both directions");
        c.Nations[0].Tag = "aaa";
        Check(Ids(CountryRankings.Read(c, Weights.Default)) == "1,0,2", "tag tie ordering is ordinal, including case");
    }

    static void InvalidValues()
    {
        var c = new Campaign(28, new Defines());
        var invalid = new[] { float.NaN, -1f, float.PositiveInfinity, float.NegativeInfinity };
        for (var i = 0; i < invalid.Length; i++)
        {
            var n = Add(c, i, "RANK_BAD_" + i, gdp: invalid[i]);
            n.StandardOfLiving = invalid[i];
            c.Armies.Add(new Army { Id = i, NationId = i, Divisions = invalid[i] });
        }
        c.Armies.Add(new Army { Id = 10, NationId = 0, Divisions = 2 });
        c.Armies.Add(new Army { Id = 11, NationId = 9999, Divisions = float.MaxValue });
        var rows = CountryRankings.Read(c, Weights.Default);
        Check(rows.All(r => r.Economy == 0 && r.LivingStandards == 0 && double.IsFinite(r.Overall) && r.Overall is >= 0 and <= 100),
            "negative, NaN and infinite economic/display values sanitize to zero");
        Check(Near(Of(rows, 0).FieldPower, c.Armies[4].Power) && rows.Where(r => r.NationId != 0).All(r => r.FieldPower == 0),
            "invalid army powers are zero individually without erasing a valid army or counting an absent owner");
        Check(float.IsNaN(c.Nations[0].Gdp) && float.IsPositiveInfinity(c.Nations[2].Gdp),
            "sanitization does not repair or write the authoritative values");
    }

    static void ReadOnlyAndFresh()
    {
        var c = World();
        foreach (var n in c.Nations) { n.AssessedDay = -50; n.AssessedStrength = 123; }
        var fingerprint = CampaignFingerprint.Of(c); var random = c.Random.State;
        var first = CountryRankings.Read(c, Weights.Default);
        foreach (var metric in Enum.GetValues<Metric>())
            _ = CountryRankings.Query(CountryRankings.Read(c, Weights.Default), metric, false, "rank");
        var other = World(99); other.Nations[0].Gdp = 9000; other.Armies[0].Divisions = 300;
        var otherFingerprint = CampaignFingerprint.Of(other); var otherRandom = other.Random.State;
        _ = CountryRankings.Read(other, new Weights(0, 0, 1, 0));
        Check(first.SequenceEqual(CountryRankings.Read(c, Weights.Default)), "cross-campaign reads cannot replace an earlier snapshot or cache");
        Check(CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random &&
            CampaignFingerprint.Of(other) == otherFingerprint && other.Random.State == otherRandom,
            "repeated reads and queries preserve both campaigns' fingerprints and random streams");
        Check(c.Nations.All(n => n.AssessedDay == -50 && n.AssessedStrength == 123), "rankings never refresh mutable intelligence assessments");
        c.Nations[2].Gdp = 1000; c.Armies[0].Divisions = 12;
        c.Nations[1].Technologies.AddRange(new[] { "rank_two", "rank_three", "rank_four", "rank_five" });
        c.Nations[0].CustomName = "Changed ranking name";
        var next = CountryRankings.Read(c, Weights.Default);
        Check(Of(next, 2).Economy == 1000 && Of(next, 0).FieldPower == c.Armies[0].Power && Of(next, 1).Technology == 5 &&
            Of(next, 0).Name == c.Nations[0].DisplayName && !Near(Of(next, 0).Overall, Of(first, 0).Overall),
            "a new snapshot immediately reflects economy, army, technology and display-name changes");
        Check(Of(first, 2).Economy == 0 && Of(first, 1).Technology == 1 && Of(first, 0).Name != Of(next, 0).Name,
            "previous snapshots retain their detached values after live state changes");
        c.Fleets.Add(new Fleet { NationId = 2, Hulls = int.MaxValue });
        Check(next.SequenceEqual(CountryRankings.Read(c, Weights.Default)), "naval hulls have no invented ground-power conversion");
    }
}
