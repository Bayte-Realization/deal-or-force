using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Faith in two levels: the FAMILY every mechanic compares, and the DENOMINATION the map and
/// the population screen show (common/religions/13_denominations.json, history/faiths).
///
/// Asked of the shipped data and of the real 2024 world: every denomination has a family that
/// exists and a colour of its own; a Lutheran and a Catholic are not one faith and a Lutheran
/// and a Baptist are; ids that existed before denominations (a save's or a mod's "protestant")
/// still resolve to themselves; Utah has Latter-day Saints; every written mix adds to one; no
/// people loses anybody when a region's faith is reweighted.
/// </summary>
static class ReligionDenominationTests
{
    static int _passed;

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("religion: " + label);
        _passed++;
        Console.WriteLine("PASS religion: " + label);
    }

    public static void Run()
    {
        _passed = 0;
        DemographyCatalog.ResetCatalog();
        Catalogue();
        Families();
        Written();
        var c = Campaign.CreateBootstrap(20261115);
        MapRasterCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError),
            "the world map loads: " + mapError);
        c.AttachProvinceTopology(map);
        OldNamesStillRead(c, map);
        World(c);
        Console.WriteLine($"Religion denomination tests passed: {_passed}");
    }

    static void Catalogue()
    {
        var denominations = DemographyCatalog.Religions.Values.Where(religion => religion.IsDenomination).ToList();
        Check(denominations.Count >= 25, $"denominations are declared ({denominations.Count})");
        foreach (var id in new[] { "lutheran", "anglican", "reformed", "baptist", "methodist", "pentecostal", "evangelical",
                     "adventist", "twelver", "zaydi", "ismaili", "alawite", "haredi", "reform_judaism", "theravada" })
            Check(DemographyCatalog.IsDenomination(id), $"'{id}' is a denomination");
        foreach (var id in new[] { "latter_day_saints", "jehovahs_witnesses", "ahmadiyya" })
            Check(DemographyCatalog.Religion(id) is { IsDenomination: false } && DemographyCatalog.Religion(id).Group.Length > 0,
                $"'{id}' is a family of its own inside a wider tradition");
        foreach (var denomination in denominations)
        {
            var family = DemographyCatalog.Religion(denomination.Family);
            Check(family is { IsDenomination: false }, $"{denomination.Id}'s family '{denomination.Family}' exists and is a family");
            Check(System.Text.RegularExpressions.Regex.IsMatch(denomination.Colour ?? "", "^#[0-9a-fA-F]{6}$"),
                $"{denomination.Id} has a colour");
            Check(!string.Equals(denomination.Colour, family.Colour, StringComparison.OrdinalIgnoreCase),
                $"{denomination.Id} is told apart from its family on the map");
            Check(denomination.Group == family.Group, $"{denomination.Id} is in its family's tradition ({family.Group})");
        }
        foreach (var family in DemographyCatalog.Religions.Values.Where(religion => !religion.IsDenomination))
        {
            var colours = DemographyCatalog.DenominationIdsIn(family.Id)
                .Select(id => DemographyCatalog.Religion(id).Colour.ToLowerInvariant()).ToList();
            Check(colours.Distinct().Count() == colours.Count, $"the denominations of '{family.Id}' have distinct colours");
        }
    }

    static void Families()
    {
        Check(DemographyCatalog.FamilyOf("lutheran") == "protestant" && DemographyCatalog.FamilyOf("zaydi") == "shia",
            "a denomination resolves to its family");
        Check(StateIdentity.SameFaith("lutheran", "baptist") && StateIdentity.SameFaith("lutheran", "protestant"),
            "a Lutheran and a Baptist - and a Lutheran and a Protestant - are one faith to every mechanic");
        Check(!StateIdentity.SameFaith("lutheran", "catholic"), "a Lutheran and a Catholic are not");
        Check(!StateIdentity.SameFaith("latter_day_saints", "protestant"), "Latter-day Saints are not Protestants");
        Check(StateIdentity.SameFaith("zaydi", "twelver") && !StateIdentity.SameFaith("zaydi", "sunni"),
            "Zaydis and Twelvers are Shia, and not Sunni");
        Check(StateIdentity.SameFaith("state_atheism", "atheism") && StateIdentity.CanonicalFaith("lutheran") == "protestant",
            "atheism keeps its alias, and declaring a denomination declares its family");
        Check(FormableNations.HasFaith(new[] { "lutheran" }, "protestant") && FormableNations.HasFaith(new[] { "protestant" }, "protestant") &&
              !FormableNations.HasFaith(new[] { "lutheran" }, "catholic"),
            "a formable naming a denomination is met by its family's people");
        var arakan = new MovementDefinition();
        arakan.Faiths.Add("theravada");
        Check(arakan.HasFaith("buddhist") && !arakan.HasFaith("sunni"),
            "a movement naming Theravada finds the Buddhists it names");
    }

    static void Written()
    {
        foreach (var (where, setting) in DemographyCatalog.CountryFaithSettings.Select(entry => ("country " + entry.Key, entry.Value))
                     .Concat(DemographyCatalog.RegionFaithSettings.Select(entry => ("region " + entry.Key, entry.Value))))
        {
            foreach (var (label, place) in new[] { (where, setting) }
                         .Concat(setting.Peoples.Select(people => ($"{where}/{people.Key}", people.Value))))
            {
                if (place.Faiths is { Length: > 0 } faiths)
                {
                    Check(Math.Abs(faiths.Sum(entry => entry.Share) - 1f) < 0.001f, $"{label}: the faith mix adds to one");
                    Check(faiths.All(entry => DemographyCatalog.Religion(entry.Religion) is { IsDenomination: false }),
                        $"{label}: every faith in the mix is a known family");
                }
                foreach (var (family, shares) in place.Denominations)
                {
                    Check(DemographyCatalog.Religion(family) is { IsDenomination: false }, $"{label}: '{family}' is a known family");
                    Check(shares.Sum(entry => entry.Share) <= 1.0001f, $"{label}: {family}'s denominations add to no more than one");
                    Check(shares.All(entry => DemographyCatalog.FamilyOf(entry.Denomination) == family &&
                                              DemographyCatalog.IsDenomination(entry.Denomination)),
                        $"{label}: {family}'s denominations are its own");
                }
            }
        }
        foreach (var (region, tag, culture, family) in new[]
                 {
                     ("us_ut", "USA", "american", "protestant"), ("us_al", "USA", "african_american", "protestant"),
                     ("us_mn", "USA", "american", "protestant"), ("gb_lan", "GBR", "english", "protestant"),
                     ("x", "ISR", "jewish", "jewish"), ("x", "YEM", "arab_yemeni", "shia"), ("x", "DEU", "german", "protestant")
                 })
        {
            var split = DemographyCatalog.DenominationsOf(family, region, tag, culture);
            Check(Math.Abs(split.Sum(entry => entry.Share) - 1f) < 0.001f && split.All(entry => entry.Share > 0f),
                $"{family} in {region}/{tag} for {culture} divides into shares adding to one ({string.Join(", ", split.Select(e => $"{e.Denomination} {e.Share:0.000}"))})");
        }
        var alabama = DemographyCatalog.DenominationsOf("protestant", "us_al", "USA", "american");
        Check(alabama[0].Denomination == "baptist" && Math.Abs(alabama[0].Share - 0.545f) < 0.001f,
            "Alabama's Protestants are first of all Baptist, as Pew writes it");
        var minnesota = DemographyCatalog.DenominationsOf("protestant", "us_mn", "USA", "american");
        Check(minnesota[0].Denomination == "lutheran", "Minnesota's Protestants are first of all Lutheran");
        Check(DemographyCatalog.DenominationsOf("protestant", "x", "DEU", "german").Single().Denomination == "protestant",
            "where no source divides a family it stays whole: Germany's Protestants");
        Check(DemographyCatalog.DenominationsOf("shia", "x", "YEM", "arab_yemeni").Single().Denomination == "zaydi",
            "Yemen's Shia are Zaydi");
        var english = DemographyCatalog.FaithOf("english", "gb_lan", "GBR");
        Check(english.Any(entry => entry.Religion == "protestant") && english.All(entry => !DemographyCatalog.IsDenomination(entry.Religion)),
            "a mix written with a denomination seeds its family: the English are Protestant groups, shown as Anglican");
    }

    /// <summary>A save's or a mod's faith ids from before denominations read exactly as they did.</summary>
    static void OldNamesStillRead(Campaign c, ProvinceTopology map)
    {
        foreach (var id in new[] { "protestant", "catholic", "orthodox", "sunni", "shia", "jewish", "buddhist", "hindu", "irreligion",
                     "maronite", "coptic_orthodox", "old_believers" })
        {
            Check(DemographyCatalog.Religion(id) is { IsDenomination: false } && DemographyCatalog.FamilyOf(id) == id,
                $"'{id}' is still a family of its own");
        }
        Check(!StateIdentity.SameFaith("maronite", "catholic") && !StateIdentity.SameFaith("coptic_orthodox", "orthodox"),
            "the churches that were their own families stay so: nobody's relations change");
        Check(DemographyCatalog.FaithOf("swedish").Single().Religion == "protestant", "a people written as Protestant is Protestant");

        // A mod naming a denomination for a people: the group is its family.
        var errors = new List<string>();
        DemographyCatalog.LoadCultures("{ \"mod_people\": { \"religion\": { \"lutheran\": 0.6, \"baptist\": 0.3, \"catholic\": 0.1 } } }", errors);
        var faith = DemographyCatalog.FaithOf("mod_people");
        Check(errors.Count == 0 && faith.Count == 2 && faith[0].Religion == "protestant" && Math.Abs(faith[0].Share - 0.9f) < 0.001f,
            "a mod's people written in denominations lives in its families' groups");

        // A save written before denominations: groups say "protestant", and they load as it.
        var folder = Path.Combine(Path.GetTempPath(), "dof-religion-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            var before = c.States.SelectMany(state => state.PopulationGroups).Count(group => group.Religion == "protestant");
            SaveSystem.Save(folder, "old_faiths", c, "tests");
            var loaded = SaveSystem.Load(folder, "old_faiths", "tests");
            Check(loaded.Success && loaded.Campaign is not null, "a save naming 'protestant' loads: " + loaded.Warning);
            loaded.Campaign.AttachProvinceTopology(map.CloneForOverrides());
            var after = loaded.Campaign.States.SelectMany(state => state.PopulationGroups).Count(group => group.Religion == "protestant");
            Check(before > 0 && after == before, $"and its Protestant groups are Protestant still ({after:N0})");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch { }
        }
        DemographyCatalog.ResetCatalog();
    }

    static void World(Campaign c)
    {
        var utah = c.States.Find(state => state.Id == "us_ut");
        Check(utah is not null && utah.Population > 0, "Utah is in the world");
        var lds = utah.PopulationGroups.Where(group => group.Religion == "latter_day_saints").Sum(group => group.Size);
        var share = lds / (double)utah.Population;
        Check(share > 0.40 && share < 0.60, $"Utah has Latter-day Saints: {share:P1} of it (Pew 2023-24: 50% of adults, margin 7.3 points; a people keeps the three largest faiths, and seeding gives its rounding remainder to the largest group)");
        Check(DemographyCatalog.DenominationsIn(utah, utah.NationId)[0].Denomination == "latter_day_saints",
            "and the map and the population screen name them as its largest faith");
        var elsewhere = c.States.Where(state => state.NationId == "USA" && state.Id != "us_ut")
            .SelectMany(state => state.PopulationGroups).Count(group => group.Religion == "latter_day_saints");
        Check(elsewhere == 0, "no other state was written, so no other state has them");

        // No people loses its people: where faith is reweighted, the groups still add to the
        // region's population (to the handful of people seeding's rounding already moves
        // everywhere - Rakhine, which nobody reweights, is two people off on its own).
        var reweighted = 0;
        foreach (var state in c.States.Where(state =>
                     DemographyCatalog.RegionFaithSettings.TryGetValue(state.Id, out var region) && region.Faiths is not null ||
                     DemographyCatalog.CountryFaithSettings.TryGetValue(state.NationId ?? "", out var country) &&
                     (country.Faiths is not null || country.Peoples.Values.Any(people => people.Faiths is not null))))
        {
            reweighted++;
            var sum = state.PopulationGroups.Sum(group => group.Size);
            if (Math.Abs(sum - state.Population) > Math.Max(10, state.Population / 100000))
                Check(false, $"{state.Id}: groups add to the population ({sum:N0} of {state.Population:N0})");
        }
        Check(reweighted > 10, $"every region whose faith is reweighted keeps all of its people ({reweighted} regions)");
        var americans = utah.PopulationGroups.Where(group => group.Culture == "american").Sum(group => group.Size);
        Check(americans > utah.Population / 2, $"Utah's Americans are still there ({americans:N0})");
        var faithless = c.States.SelectMany(state => state.PopulationGroups)
            .Where(group => group.Size > 0 && DemographyCatalog.IsDenomination(group.Religion)).Take(1).ToList();
        Check(faithless.Count == 0, "no population group carries a denomination: groups hold families, and their count does not grow");

        var lebanon = c.States.Where(state => state.NationId == "LBN").SelectMany(state => state.PopulationGroups)
            .Where(group => group.Culture == "arab_levantine").GroupBy(group => group.Religion)
            .ToDictionary(group => group.Key, group => group.Sum(entry => entry.Size));
        Check(lebanon.GetValueOrDefault("shia") > 0 && lebanon.GetValueOrDefault("sunni") > 0,
            "Lebanon's Arabs are Sunni and Shia, as the CIA World Factbook counts them");
        var syria = c.States.Where(state => state.NationId == "SYR")
            .SelectMany(state => state.PopulationGroups).Where(group => group.Religion == "shia").Sum(group => group.Size);
        Check(syria > 0, "Syria has its Alawite, Ismaili and Shia minority");
    }
}
