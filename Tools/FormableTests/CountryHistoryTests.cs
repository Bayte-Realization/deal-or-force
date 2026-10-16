using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// What a country's HISTORY file may say about it, and what belongs somewhere else.
///
/// The base world is assembled from game/history, so the answer to "what can a mod change about
/// the 2024 world without forking code" is the list of keys this loader accepts. Two of the keys
/// this file used to pin down - a country's colour and its capital - have moved out, because they
/// were never history: what a country *is* (its name, the colour it is painted, the city its
/// government sits in) lives in common/countries, and what it *had* in the year the bookmark opens
/// lives here. That separation is written down in CountryDefinitions and in the modding guide, and
/// it is checked below, because the two folders look interchangeable and are not.
/// </summary>
static class CountryHistoryTests
{
    static void Check(bool ok, string label)
    { if (!ok) throw new Exception("country history: " + label); Console.WriteLine("PASS country history: " + label); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261016);
        var afg = c.NationByTag("AFG");
        var ind = c.NationByTag("IND");
        Check(afg is not null && ind is not null, "the world has the countries the catalogue knows");
        if (afg is null || ind is null) return;

        var neighbourName = ind.CapitalName;
        void Apply(string json, List<string> errors = null) => HistoryDataLoader.ApplyCountries(c, json, errors);

        // The figures the opening economy is built from, which is what makes a country poor or
        // rich at the bookmark rather than a decade in.
        Apply("{ \"AFG\": { \"gdp\": 123.5, \"treasury\": 7.25, \"population\": 40000000 } }");
        Check(Math.Abs(afg.Gdp - 123.5f) < 0.001f, "a country's income can be stated outright");
        Check(Math.Abs(afg.Treasury - 7.25f) < 0.001f, "and so can its opening balance");
        Check(afg.Population == 40000000L, "and its population");

        // Debts, and how far the markets trust it - which is what the loan ledger is built from.
        Apply("{ \"AFG\": { \"public_debt\": 42.5, \"credit_confidence\": 0.4 } }");
        Check(Math.Abs(afg.PublicDebt - 42.5f) < 0.001f, "a country's debt can be stated");
        Check(Math.Abs(afg.CreditConfidence - 0.4f) < 0.001f, "and how far it is trusted");

        // And the surface a country's politics opens on.
        Apply("{ \"AFG\": { \"tax_rate\": 0.33, \"military_spending\": 0.11, \"stability\": 0.44, " +
              "\"war_support\": 0.55, \"literacy\": 0.62, \"standard_of_living\": 0.31, " +
              "\"manpower\": 900000, \"urbanisation\": 0.28, \"agriculture\": 0.41, " +
              "\"trade_policy\": \"StrategicAutarky\" } }");
        Check(Math.Abs(afg.TaxRate - 0.33f) < 0.001f && Math.Abs(afg.MilitarySpending - 0.11f) < 0.001f,
            "a country's taxes and its army's share of them can be stated");
        Check(afg.Stability > 0.43f && afg.Stability < 0.45f && afg.WarSupport > 0.54f && afg.WarSupport < 0.56f,
            "and its settledness and its appetite for a war");
        Check(Math.Abs(afg.StandardOfLiving - 0.31f) < 0.001f && Math.Abs(afg.Urbanisation - 0.28f) < 0.001f,
            "and how its people live and where");
        Check(afg.TradePolicy == TradePolicy.StrategicAutarky, "and which way it trades");

        // A rate past the end of its range is brought back to it, which is the whole of the check
        // the loader does: a mod saying 90 % tax has a reason, and the reason is not a crash.
        Apply("{ \"AFG\": { \"tax_rate\": 9.0 } }");
        Check(afg.TaxRate <= 0.55f, $"a tax rate past the ceiling is brought back to it ({afg.TaxRate:0.##})");

        // A form of government that does not exist is a complaint, not a silently unchanged
        // country: a scenario naming one has a mistake in it and should be told.
        var errors = new List<string>();
        Apply("{ \"AFG\": { \"government\": \"no_such_thing\" } }", errors);
        Check(errors.Count == 1 && errors[0].Contains("no form of government"), "a government nobody has says so");

        // ---- and what is NOT here
        //
        // These four keys belong to common/countries. Writing them here does nothing, and the
        // point of checking that is that it is not obvious: the two folders are both called
        // "countries", both keyed by tag, and both JSON. A mod that put a colour in the wrong one
        // would see nothing happen and no error either.
        var name = afg.Name;
        var colour = afg.Color;
        var capital = afg.CapitalName;
        var latitude = afg.CapitalLatitude;
        Apply("{ \"AFG\": { \"name\": \"Renamed\", \"color\": \"#ff00ff\", \"colour\": \"#ff00ff\", " +
              "\"capital_name\": \"Nowhere\", \"capital\": \"Nowhere\", \"capital_latitude\": 1.0 } }");
        Check(afg.Name == name, "a name written into history is ignored - names are common/countries");
        Check(afg.Color == colour, "and so is a colour, under either spelling");
        Check(afg.CapitalName == capital && afg.CapitalLatitude == latitude,
            "and so is a capital, which is what CountryDefinitions is for");

        // None of it leaked into the country next door, which said nothing at all.
        Check(ind.CapitalName == neighbourName, $"a country that said nothing kept its capital ({ind.CapitalName})");

        // ---- the shipped files, read back against the list above
        //
        // A key in a country's file is only worth writing if something reads it, and a key nothing
        // reads is invisible: no error, no effect, no clue. That has happened twice in this folder's
        // history - a name and a colour written here because they looked like they belonged - and
        // both times the modder saw nothing happen and nothing complain.
        //
        // The list below is written out by hand on purpose. It is the contract rather than a copy of
        // the code: a field added to ApplyCountries without this line being updated should stop the
        // suite and make somebody say what the field is for.
        var accepted = new HashSet<string>(StringComparer.Ordinal)
        {
            "treasury", "public_debt", "credit_confidence", "gdp", "population", "tax_rate",
            "military_spending", "literacy", "urbanisation", "agriculture", "standard_of_living",
            "stability", "war_support", "manpower", "trade_policy", "government", "ideology",
            "primary_cultures"
        };

        var folder = Path.Combine(GameDataLoader.DataFolder("history") ?? "", "countries", "2024");
        Check(Directory.Exists(folder), "the year's country files are in one folder");
        var strangers = new List<string>();
        var entries = 0;
        if (Directory.Exists(folder))
            foreach (var file in Directory.EnumerateFiles(folder, "*.json"))
            {
                using var shipped = JsonDocument.Parse(File.ReadAllText(file), GameDataLoader.ForgivingJson);
                if (shipped.RootElement.ValueKind != JsonValueKind.Object) continue;
                foreach (var country in shipped.RootElement.EnumerateObject())
                {
                    if (country.Value.ValueKind != JsonValueKind.Object) continue;
                    entries++;
                    foreach (var key in country.Value.EnumerateObject())
                        if (!accepted.Contains(key.Name))
                            strangers.Add($"{Path.GetFileName(file)}: {country.Name}.{key.Name}");
                }
            }
        Check(entries > 0, $"the year's country files were read back ({entries} entries)");
        Check(strangers.Count == 0, "and every key in them is one this loader actually reads" +
            (strangers.Count > 0 ? $" - unread keys: {string.Join(", ", strangers.Take(6))}" : ""));
    }
}
