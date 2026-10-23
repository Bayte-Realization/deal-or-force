using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class PersonNameCatalogTests
{
    private static void Check(bool condition, string text)
    { if (!condition) throw new Exception("person names: " + text); Console.WriteLine("PASS person names: " + text); }

    public static void Run()
    {
        PersonNameCatalog.ResetCatalog();
        var pools = PersonNameCatalog.All;
        Check(pools.Count >= 75, "at least 75 structured cultural name lists are shipped");
        Check(pools.All(p => p.MaleNames >= 24 && p.FemaleNames >= 24 && p.Families >= 24),
            "every list has at least 24 male, 24 female and 24 second-component entries");
        foreach (var (tag, culture, expected) in new[]
        {
            ("RUS", "", "russian"), ("UKR", "", "ukrainian"), ("BLR", "", "belarusian"),
            ("POL", "", "polish"), ("CZE", "", "czech"), ("SVK", "", "slovak"),
            ("JPN", "", "japanese"), ("KOR", "", "korean"), ("PRK", "", "korean"), ("CHN", "", "chinese"),
            ("IRN", "", "persian"), ("SAU", "", "arabic"), ("TUR", "", "turkish"), ("AZE", "", "azerbaijani"),
            ("KGZ", "", "kyrgyz"), ("KAZ", "", "kazakh"), ("MKD", "", "macedonian"),
            ("SWE", "", "swedish"), ("NOR", "", "norwegian"), ("DNK", "", "danish"),
            ("ESP", "basque", "basque"), ("ESP", "catalan", "catalan"),
            ("R01", "russian", "russian"), ("R02", "yoruba", "yoruba"), ("R03", "igbo", "igbo"),
            ("AFG", "tajik", "persian"), ("IDN", "", "indonesian"), ("BRA", "", "portuguese"),
            ("GBR", "", "english"), ("IRL", "", "irish"), ("NLD", "", "dutch"),
            ("ETH", "", "amhara"), ("ZAF", "afrikaner", "afrikaner"), ("ZAF", "", "nguni")
        })
            Check(PersonNameCatalog.PoolFor(tag, culture) == expected, tag + "/" + culture + " selects " + expected);

        var names = new HashSet<string>(); var sampleCount = 0;
        foreach (var pool in pools)
        {
            names.Clear();
            for (var serial = 0; serial < 128; serial++)
            {
                CheckDraw(PersonNameCatalog.TryGenerate("TEST", pool.Id, serial, 17, 31, out var a), pool.Id);
                PersonNameCatalog.TryGenerate("TEST", pool.Id, serial, 17, 31, out var b);
                if (a != b || string.IsNullOrWhiteSpace(a.FullName) || a.FullName.Contains('{'))
                    throw new Exception("unstable or malformed name in " + pool.Id);
                names.Add(a.FullName); sampleCount++;
            }
            Check(names.Count >= 105, pool.Id + " has diverse seeded characters (" + names.Count + "/128)");
        }
        Check(sampleCount >= 9600, "at least 9600 seeded name draws were checked twice");
        PersonNameCatalog.TryGenerate("RUS", "russian", 5, 7, 17, out var woman, true);
        PersonNameCatalog.TryGenerate("RUS", "russian", 5, 7, 17, out var man, false);
        Check(woman.Female && woman.Family.EndsWith("a") && woman.Middle.EndsWith("vna") &&
            man.Middle.EndsWith("vich") && !man.Female, "Russian family and patronymic forms agree with the generated person");
        foreach (var pool in new[] { "japanese", "korean", "chinese", "hungarian", "vietnamese" })
        {
            PersonNameCatalog.TryGenerate("TEST", pool, 2, 3, 17, out var person);
            Check(person.FullName == person.Family + " " + person.Given, pool + " uses its authored family-first order");
        }
        PersonNameCatalog.TryGenerate("MYS", "malay", 5, 7, 17, out woman, true);
        PersonNameCatalog.TryGenerate("MYS", "malay", 5, 7, 17, out man, false);
        Check(woman.FullName.Contains(" binti ") && man.FullName.Contains(" bin "), "gender-specific patronymic patterns work without surname assumptions");
        Check(!PersonNameCatalog.TryGenerate("XXX", "__missing__", 0, 0, 0, out _), "missing pool reports no generated person");
        PersonNameCatalog.TryGenerate("XXX", "russian", int.MinValue, int.MinValue, int.MinValue, out var negative);
        Check(negative.FullName.Length > 0, "negative seed and serial cannot overflow an index");

        var c = Campaign.CreateBootstrap(20261019); var before = CampaignFingerprint.Of(c); var rng = c.Random.State;
        var language = Loc.Language;
        try
        {
            PersonNameCatalog.TryGenerate("RUS", "russian", 2, c.Seed, 17, out var english);
            Loc.Language = "russian";
            PersonNameCatalog.TryGenerate("RUS", "russian", 2, c.Seed, 17, out var russian);
            Check(english == russian, "canonical character identity does not depend on the viewer's language");
            _ = PersonNameCatalog.PoolFor(c.Player);
            Check(before == CampaignFingerprint.Of(c) && rng == c.Random.State, "catalogue inspection changes neither campaign state nor global RNG");
        }
        finally { Loc.Language = language; }

        try
        {
            using var overlay = JsonDocument.Parse("{\"test_pool\":{\"inherits\":\"korean\",\"cultures\":[\"mod_people\"],\"male\":[\"First\"],\"female\":[\"Second\"],\"family\":[\"House\"]}}");
            var errors = new List<string>(); PersonNameCatalog.Merge(overlay.RootElement, errors);
            Check(errors.Count == 0 && PersonNameCatalog.PoolFor("MOD", "mod_people") == "test_pool", "a mod can inherit a pattern and bind a new culture");
            PersonNameCatalog.TryGenerate("MOD", "test_pool", 0, 0, 0, out var mod);
            Check(mod.FullName is "House First" or "House Second", "mod names run through the same formatter");
            using var broken = JsonDocument.Parse("{\"test_pool\":{\"male\":[],\"female_family\":[\"A\",\"B\"]},\"bad_parent\":{\"inherits\":\"no_parent\"}}");
            PersonNameCatalog.Merge(broken.RootElement, errors);
            PersonNameCatalog.TryGenerate("MOD", "test_pool", 0, 0, 0, out var preserved);
            Check(errors.Count == 2 && preserved == mod, "bad overlays are reported and cannot destroy an existing valid list");
        }
        finally { PersonNameCatalog.ResetCatalog(); }
    }

    private static void CheckDraw(bool exists, string pool)
    { if (!exists) throw new Exception("missing name list " + pool); }
}
