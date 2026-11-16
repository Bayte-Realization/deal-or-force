using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;
using DealOrForce.Simulation;

/// <summary>
/// Cities are equal, and a capital is one of them (CityCatalog, CapitalCore).
///
/// A city in the data is a name and a place. Which city is a capital is the country's business:
/// it names one, is governed from the province under it, shows that city's name wherever its
/// capital is shown, and when the government moves it moves into another city.
/// </summary>
static class CityTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("FAIL cities: " + note); Console.WriteLine("PASS cities: " + note); }

    public static void Run()
    {
        CityCatalog.Reset();
        TheData();
        OlderFiles();
        TheWorld();
        CityCatalog.Reset();
    }

    /// <summary>What ships: three keys a city, in the order the game is to take them.</summary>
    static void TheData()
    {
        var allowed = new HashSet<string>(StringComparer.Ordinal) { "name", "latitude", "longitude" };
        var written = new List<string>();
        var strangers = new List<string>();
        var incomplete = 0;
        foreach (var document in GameDataLoader.DataDocuments("map_data", "cities"))
            using (document)
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    written.Add(entry.Name);
                    var keys = entry.Value.EnumerateObject().Select(field => field.Name).ToList();
                    foreach (var key in keys.Where(key => !allowed.Contains(key))) strangers.Add($"{entry.Name}.{key}");
                    if (keys.Count != 3) incomplete++;
                }
        Check(written.Count > 7000, $"the game ships {written.Count} cities");
        Check(strangers.Count == 0, "a city is a name, a latitude and a longitude and nothing else" +
            (strangers.Count > 0 ? ": " + string.Join(", ", strangers.Take(8)) : " - no population, no tier, no capital"));
        Check(incomplete == 0, "and every city has all three");
        Check(written.Distinct(StringComparer.OrdinalIgnoreCase).Count() == written.Count, "no key is written twice");
        var all = CityCatalog.All;
        Check(all.Select(city => city.Id).SequenceEqual(written), "the catalogue keeps the cities in the order the files write them");
        Check(CityCatalog.IndexOf(all[^1].Id) == all.Count - 1 && CityCatalog.Find("no_such_city") is null && CityCatalog.IndexOf(null) == -1,
            "a city is found by its key, and a key nobody has finds nothing");
        Check(CityCatalog.LeadingCount == (all.Count + 2) / 3, "the leading cities - where a base or an office goes - are the first third of the list");
    }

    /// <summary>A mod written for the older shape still loads; what it said about size and rank is not read.</summary>
    static void OlderFiles()
    {
        JsonDocument Doc(string json) => JsonDocument.Parse(json);
        var parsed = CityCatalog.Parse(new[]
        {
            Doc("""
                {
                  "hamlet": {"name": "Hamlet", "latitude": 10.5, "longitude": 20.5, "population": 900, "tier": 3},
                  "metropolis": {"name": "Metropolis", "latitude": 11, "longitude": 21, "population": 9000000, "tier": 0, "capital": "national"},
                  "county_seat": {"name": "County Seat", "latitude": 12, "longitude": 22, "population": 40000, "tier": 2, "capital": "regional"},
                  "old_removal": {"name": "Gone The Old Way", "latitude": 1, "longitude": 1},
                  "new_removal": {"name": "Gone The New Way", "latitude": 2, "longitude": 2},
                  "comes_back": {"name": "Comes Back", "latitude": 3, "longitude": 3}
                }
                """),
            Doc("""
                {
                  "old_removal": {"tier": -1},
                  "new_removal": {"removed": true},
                  "comes_back": {"removed": true},
                  "metropolis": {"name": "Metropolis Renamed"},
                  "added_by_mod": {"name": "Added", "latitude": 4, "longitude": 4, "population": 99000000, "tier": 0, "capital": "national"}
                }
                """),
            Doc("""{ "comes_back": {"removed": false} }""")
        });
        Check(parsed.Select(city => city.Id).SequenceEqual(new[] { "hamlet", "metropolis", "county_seat", "comes_back", "added_by_mod" }),
            "an entry carrying population, tier and capital loads, and none of the three reorders anything: " +
            string.Join(", ", parsed.Select(city => city.Id)));
        Check(parsed[0].Name == "Hamlet" && MathF.Abs(parsed[0].Latitude - 10.5f) < 0.001f && MathF.Abs(parsed[0].Longitude - 20.5f) < 0.001f,
            "its name and its place are read as they always were");
        Check(parsed.All(city => city.Id != "old_removal"), "the older way to remove a city - \"tier\": -1 - still removes it");
        Check(parsed.All(city => city.Id != "new_removal"), "\"removed\": true removes one");
        Check(parsed.Any(city => city.Id == "comes_back"), "and a later file can put a removed city back");
        Check(parsed[1].Name == "Metropolis Renamed" && parsed[1].Latitude == 11f,
            "a later file that names a key again changes that city where it stands in the order");
        Check(parsed[^1].Id == "added_by_mod", "and a new key goes after the cities already written, whatever it claims to be");
        Check(typeof(CityDefinition).GetFields().Select(field => field.Name).OrderBy(name => name, StringComparer.Ordinal)
                .SequenceEqual(new[] { "Id", "Latitude", "Longitude", "Name" }),
            "a city in the game has an id, a name and a place - there is nowhere to keep a head count or a rank");
    }

    static ProvinceTopology Map()
    {
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), "the map builds" + (message?.Length > 0 ? ": " + message : ""));
        return map;
    }

    static Campaign World(int seed, Action<Campaign> before = null)
    {
        var campaign = Campaign.CreateBootstrap(seed);
        before?.Invoke(campaign);
        campaign.AttachProvinceTopology(Map());
        return campaign;
    }

    static bool OwnsLand(Campaign c, Nation nation)
    {
        var map = c.ProvinceMap;
        for (var province = 1; province <= map.ProvinceCount; province++)
            if (map.ProvinceToNation[province] == nation.MapIndex && !map.IsWater(province)) return true;
        return false;
    }

    static double Kilometres(double latA, double lonA, double latB, double lonB)
    {
        double Rad(double degrees) => degrees * Math.PI / 180.0;
        var h = Math.Pow(Math.Sin(Rad(latB - latA) / 2), 2) +
                Math.Cos(Rad(latA)) * Math.Cos(Rad(latB)) * Math.Pow(Math.Sin(Rad(lonB - lonA) / 2), 2);
        return 6371.0 * 2 * Math.Asin(Math.Sqrt(h));
    }

    static void TheWorld()
    {
        const int seed = 20261116;
        var modErrors = new List<string>();
        (IReadOnlyList<CityDefinition> List, int Count, string Gdansk) modHeld = default;
        var c = World(seed);
        var map = c.ProvinceMap;
        var cities = CityCatalog.All;
        var under = CityCatalog.ProvincesOf(map);

        // ---- every country of 2024 is governed from a city
        var landed = c.Nations.Where(nation => OwnsLand(c, nation)).ToList();
        var without = new List<string>();
        var atTheEdge = new List<string>();
        var wrong = new List<string>();
        foreach (var nation in landed)
        {
            var index = CityCatalog.IndexOf(nation.CapitalCityId);
            if (index < 0)
            {
                without.Add($"{nation.Tag} ({nation.CapitalName}, id '{nation.CapitalCityId ?? "null"}')");
                continue;
            }
            var city = cities[index];
            if (map.ProvinceToNation[nation.CapitalProvinceId] != nation.MapIndex)
                wrong.Add($"{nation.Tag}: governed from province {nation.CapitalProvinceId}, which is not its own");
            if (nation.CapitalStateMapIndex != map.ProvinceToState[nation.CapitalProvinceId])
                wrong.Add($"{nation.Tag}: the capital's region is not the region of its province");
            if (under[index] != nation.CapitalProvinceId)
            {
                // The city's very point is somebody else's - Rome's is the Vatican's - and the
                // government sits on its own ground at the city's edge.
                var apart = Kilometres(city.Latitude, city.Longitude, nation.CapitalLatitude, nation.CapitalLongitude);
                var holder = under[index] > 0 ? c.NationByMapIndex(map.ProvinceToNation[under[index]])?.Tag ?? "nobody" : "the sea";
                atTheEdge.Add($"{nation.Tag} {city.Name} ({holder}'s at the dot, {apart:0} km)");
                if (apart > 40) wrong.Add($"{nation.Tag}: governed {apart:0} km from {city.Name}");
            }
            if (nation.CapitalName != city.Name) wrong.Add($"{nation.Tag}: the capital's name is '{nation.CapitalName}', its city's '{city.Name}'");
            if (CountryNames.Capital(nation) != city.DisplayName)
                wrong.Add($"{nation.Tag}: the capital reads '{CountryNames.Capital(nation)}', its city '{city.DisplayName}'");
            if (CapitalCore.HasSeat(c, nation) && nation.GovernmentSeatProvinceId == 0 && CountryNames.Capital(nation, c) != city.DisplayName)
                wrong.Add($"{nation.Tag}: in the campaign the capital reads '{CountryNames.Capital(nation, c)}', its city '{city.DisplayName}'");
        }
        foreach (var line in without) Console.WriteLine("    no city on the map: " + line);
        foreach (var line in atTheEdge) Console.WriteLine("    at the city's edge: " + line);
        foreach (var line in wrong.Take(12)) Console.WriteLine("    WRONG: " + line);
        Check(landed.Count > 200, $"{landed.Count} countries hold ground on the first day");
        // Pitcairn is the one: the map has no province where Adamstown is, so there is no ground
        // under the town for anybody to hold.
        Check(without.Count <= 1, $"every one of them but {without.Count} names a capital city that is on the map" +
            (without.Count > 0 ? " (" + string.Join("; ", without) + ")" : ""));
        Check(atTheEdge.Count <= 12, $"{landed.Count - without.Count - atTheEdge.Count} are governed from the province under their city, " +
            $"{atTheEdge.Count} from their own ground at its edge");
        Check(wrong.Count == 0, "the capital's province, region and name are the city's, and the country card says the city's name");

        // ---- and it is the city the country's own data names, wherever the country holds it
        // A country that names a city somebody else holds on the first day - the recognised
        // government of Yemen names Sana'a, which Ansar Allah holds - is governed from a city of
        // its own instead. And one whose history of 2024 seats its government away from the
        // capital it names is governed from where the history says: Sudan still owns Khartoum on
        // paper, and its government has sat in Port Sudan since the war drove it out
        // (history/factions, "parent_capital"). Those are the only exceptions there may be, and
        // each is printed.
        var seatedByHistory = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            ["SDN"] = "Port Sudan",
        };
        var named = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var calledInData = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        foreach (var document in GameDataLoader.DataDocuments("common", "countries"))
            using (document)
                foreach (var entry in document.RootElement.EnumerateObject())
                {
                    if (entry.Value.ValueKind != JsonValueKind.Object) continue;
                    if (entry.Value.TryGetProperty("capital_city", out var key) && key.ValueKind == JsonValueKind.String)
                        named[entry.Name] = key.GetString();
                    if (entry.Value.TryGetProperty("capital_name", out var called) && called.ValueKind == JsonValueKind.String)
                        calledInData[entry.Name] = called.GetString();
                }
        var elsewhere = new List<string>();
        var unexplained = new List<string>();
        var unknown = new List<string>();
        foreach (var (tag, key) in named)
            if (CityCatalog.IndexOf(key) < 0) unknown.Add($"{tag}: {key}");
        foreach (var nation in landed)
        {
            if (!named.TryGetValue(nation.Tag, out var key) || CityCatalog.IndexOf(key) < 0) continue;
            if (string.Equals(nation.CapitalCityId, key, StringComparison.OrdinalIgnoreCase)) continue;
            var at = under[CityCatalog.IndexOf(key)];
            var holder = at > 0 ? c.NationByMapIndex(map.ProvinceToNation[at]) : null;
            var sits = CapitalCore.City(nation)?.Name ?? nation.CapitalName;
            var own = holder is not null && holder.MapIndex == nation.MapIndex;
            elsewhere.Add(own
                ? $"{nation.Tag} names {CityCatalog.Find(key).Name}, and its history of 2024 seats the government in {sits}"
                : $"{nation.Tag} names {CityCatalog.Find(key).Name} ({holder?.Tag ?? "nobody"}'s on the first day) and is governed from {sits}");
            // Its own city, no history that says otherwise, and it sits somewhere else: that
            // would be the rule failing.
            if (own && !(seatedByHistory.TryGetValue(nation.Tag, out var written) && written == sits)) unexplained.Add(nation.Tag);
        }
        foreach (var line in elsewhere) Console.WriteLine("    not governed from the city it names: " + line);
        Check(named.Count > 230 && unknown.Count == 0, $"{named.Count} countries name their capital city in common/countries, each by a key the map has" +
            (unknown.Count > 0 ? ": " + string.Join(", ", unknown.Take(8)) : ""));
        Check(unexplained.Count == 0 && elsewhere.Count <= 8,
            $"every country that holds the city it names is governed from it, but for {elsewhere.Count} that are explained above" +
            (unexplained.Count > 0 ? " - but not: " + string.Join(", ", unexplained) : ""));

        var ukr = c.NationByTag("UKR");
        var language = Loc.Language;
        try
        {
            Loc.Language = "russian";
            var kyiv = CountryNames.Capital(ukr, c);
            Check(kyiv == Loc.Get("city.kyiv_ukr.name") && kyiv.Any(letter => letter >= 'А' && letter <= 'я'),
                $"in Russian the capital is the city's Russian name, the one the map letters ({kyiv})");
        }
        finally { Loc.Language = language; }
        Check(ukr.CapitalCityId == "kyiv_ukr" && CapitalCore.City(ukr)?.Name == "Kyiv", "Ukraine's capital is the city of Kyiv, by its key");

        // ---- a name made of capitals is made of the cities' keys, and reads in the reader's language
        // An organisation founded in play is named after its founders' capitals and the name is
        // kept as written. Written with the city's name as its file spells it, it was a row in
        // no language: "Washington,  D.C." and "København" stood in the Russian name.
        var usa = c.NationByTag("USA");
        var dnk = c.NationByTag("DNK");
        Check(OrganizationDynamics.CapitalOf(usa) == "city.washington_d_c_usa.name" &&
              OrganizationDynamics.CapitalOf(dnk) == "city.kbenhavn_dnk.name",
            $"an organisation's name keeps its founders' capitals by their cities' keys ({OrganizationDynamics.CapitalOf(usa)}, {OrganizationDynamics.CapitalOf(dnk)})");
        var pact = new InternationalOrganization
        {
            Name = $"org.name.pact|{OrganizationDynamics.CapitalOf(usa)}|{OrganizationDynamics.CapitalOf(dnk)}"
        };
        var olderPact = new InternationalOrganization { Name = "org.name.pact|Washington D.C.|Copenhagen" };
        try
        {
            Loc.Language = "russian";
            var read = InternationalOrganizations.DisplayName(c, pact);
            Check(read == Loc.Format("org.name.pact", CountryNames.Capital(usa, c), CountryNames.Capital(dnk, c)) &&
                  read.Contains("Вашингтон") && read.Contains("Копенгаген") && !read.Contains("benhavn") && !read.Contains("city."),
                $"and in Russian it reads with the names the country cards have ({read})");
            Check(InternationalOrganizations.DisplayName(c, olderPact) == read,
                "a name kept by an older save, made of the capitals' names, still reads the same");
            Loc.Language = "english";
            read = InternationalOrganizations.DisplayName(c, pact);
            Check(read.Contains("Washington, D.C.") && read.Contains("Copenhagen") && !read.Contains("  "),
                $"and in English ({read})");
        }
        finally { Loc.Language = language; }

        // ---- the town weights are the map's, not the cities'
        var townspeople = StatePartition.Townspeople(map);
        Check(townspeople.Max() > 1_000_000 && townspeople.Count(weight => weight > 0) > 4000,
            "how a region's townspeople are spread over its provinces comes from the map's own table (map_data/townspeople)");
        // A mod's table is laid over it (ModLoader hands map_data/townspeople to MergeTownspeople).
        var town = Array.FindIndex(townspeople, weight => weight > 0);
        var tableErrors = new List<string>();
        StatePartition.MergeTownspeople($$"""{ "provinces": {{map.ProvinceCount}}, "townspeople": { "{{town}}": 7 } }""", tableErrors);
        StatePartition.MergeTownspeople("""{ "provinces": 3, "townspeople": { "1": 5, "2": 5 } }""", tableErrors);
        var laidOver = StatePartition.Townspeople(map);
        Check(tableErrors.Count == 0 && laidOver[town] == 7 && laidOver[1] == townspeople[1] &&
              laidOver.Count(weight => weight > 0) == townspeople.Count(weight => weight > 0),
            "a mod's townspeople table is laid over the map's province by province, and one measured on another map is passed over");
        StatePartition.MergeTownspeople("""{ "towns": { "1": 5 } }""", tableErrors);
        Check(tableErrors.Count == 1, "a file that is not a townspeople table is said to be one: " + string.Join(" ", tableErrors));
        StatePartition.ResetTownspeople();
        Check(StatePartition.Townspeople(map)[town] == townspeople[town], "and with the mod gone the map's own table is back");

        // ---- a government that moves, moves into a city
        var rus = c.NationByTag("RUS");
        c.ConfigureSinglePlayer(rus.Id);
        var urals = c.States.First(state => state.Id == "ru_sve");
        var uralCities = CityCatalog.CitiesOf(c, urals);
        Check(uralCities.Count > 1, $"the Sverdlovsk region has {uralCities.Count} cities to choose from");
        rus.Treasury = 1_000f;
        var revision = c.CapitalRevision;
        var order = new MoveCapitalCommand { IssuerId = rus.Id, StateMapIndex = urals.MapIndex };
        order.Apply(c);
        var seat = CapitalCore.City(rus);
        Check(order.Moved && seat is not null && seat.Id == uralCities[0].Id,
            $"moved to the Sverdlovsk region, the capital is the first city written there ({seat?.Name ?? "no city"})");
        Check(rus.CapitalProvinceId == under[CityCatalog.IndexOf(seat.Id)] && rus.CapitalStateMapIndex == urals.MapIndex,
            "it is governed from the province under that city");
        Check(rus.CapitalName == seat.Name && CountryNames.Capital(rus, c) == seat.DisplayName && CountryNames.Capital(rus, c) != urals.DisplayName,
            $"and the capital reads {CountryNames.Capital(rus, c)}, not {urals.DisplayName}");
        Check(c.CapitalRevision > revision && CapitalCore.SeatCity(c, rus)?.Id == seat.Id,
            "the map is told, and the city the capital's mark belongs to is the new one");
        Check(OrganizationDynamics.CapitalOf(rus) == $"city.{seat.Id}.name",
            "and an organisation it founds from now on is named after the new city");
        Check(CapitalCore.SeatCity(c, c.NationByTag("FRA"))?.Id == c.NationByTag("FRA").CapitalCityId,
            "a country that has not moved keeps its mark on its own city");

        // ---- by necessity too: the region is ceded and the government goes to a city of another
        void Cede(ScenarioState state, Nation to)
        {
            state.NationMapIndex = to.MapIndex;
            for (var province = 1; province <= map.ProvinceCount; province++)
                if (map.ProvinceToState[province] == state.MapIndex) map.ProvinceToNation[province] = (ushort)to.MapIndex;
            c.InvalidateStateIndex();
        }
        Cede(urals, c.NationByTag("KAZ"));
        CapitalCore.Tick(c);
        var fled = CapitalCore.City(rus);
        Check(fled is not null && fled.Id != seat.Id && rus.CapitalStateMapIndex != urals.MapIndex &&
              map.ProvinceToState[under[CityCatalog.IndexOf(fled.Id)]] == rus.CapitalStateMapIndex &&
              CountryNames.Capital(rus, c) == fled.DisplayName,
            $"with the region ceded the government sits in {fled?.Name ?? "no city"}, a city of the region it went to");
        Cede(urals, rus);

        // ---- a region with no city at all: the one case a capital goes by its region's name
        Nation villager = null;
        ScenarioState empty = null;
        var counted = new Dictionary<int, int>();
        foreach (var province in under)
            if (province > 0) counted[map.ProvinceToState[province]] = counted.GetValueOrDefault(map.ProvinceToState[province]) + 1;
        foreach (var nation in landed.OrderBy(nation => nation.Tag, StringComparer.Ordinal))
        {
            foreach (var state in c.StatesOf(nation))
            {
                if (counted.GetValueOrDefault(state.MapIndex) > 0 || state.MapIndex == nation.CapitalStateMapIndex) continue;
                nation.Treasury = 1_000_000f;
                nation.CapitalMovedDay = -100_000;
                if (CapitalCore.Refusal(c, nation, state).Length > 0) continue;
                villager = nation; empty = state;
                break;
            }
            if (villager is not null) break;
        }
        Check(villager is not null, $"there is a region with no city on the map to try it on ({empty?.Name} of {villager?.Tag})");
        Check(CapitalCore.Move(c, villager, empty) && villager.CapitalCityId == "" && CapitalCore.City(villager) is null &&
              villager.CapitalStateMapIndex == empty.MapIndex && CountryNames.Capital(villager, c) == empty.DisplayName,
            $"moved there, the capital has no city and goes by the region's name ({CountryNames.Capital(villager, c)})");

        // ---- a government driven out sits, for the time being, in another city
        var pol = c.NationByTag("POL");
        var warsaw = CapitalCore.City(pol);
        var warsawProvince = pol.CapitalProvinceId;
        var warsawHolder = c.ProvinceController[warsawProvince];
        c.ProvinceController[warsawProvince] = (ushort)c.NationByTag("BLR").MapIndex;
        CapitalCore.Tick(c);
        var refuge = CapitalCore.SeatCity(c, pol);
        Check(pol.GovernmentSeatProvinceId > 0 && pol.CapitalCityId == warsaw.Id && refuge is not null && refuge.Id != warsaw.Id &&
              CityCatalog.CityIn(map, pol.GovernmentSeatProvinceId)?.Id == refuge.Id && CountryNames.Capital(pol, c) == refuge.DisplayName,
            $"Warsaw occupied: Poland's capital is still Warsaw and its government sits in {refuge?.Name ?? "no city"}");
        c.ProvinceController[warsawProvince] = warsawHolder;
        CapitalCore.Tick(c);
        Check(pol.GovernmentSeatProvinceId == 0 && CapitalCore.SeatCity(c, pol)?.Id == warsaw.Id && CountryNames.Capital(pol, c) == warsaw.DisplayName,
            "Warsaw freed: it sits in Warsaw again");

        // ---- a save keeps the city, and a save from before there was one finds it
        var folder = Path.Combine(Path.GetTempPath(), "dof-cities-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            // In both formats a save is written in: the packed one first, then the editable text.
            c.Defines.SaveTextFormat = false;
            SaveSystem.Save(folder, "packed", c, "test-mod");
            var packed = SaveSystem.Load(folder, "packed", "test-mod");
            Check(packed.Success && c.Nations.All(nation => packed.Campaign.NationByTag(nation.Tag)?.CapitalCityId == nation.CapitalCityId),
                "a packed save keeps every country's capital city as it was written");
            c.Defines.SaveTextFormat = true;
            SaveSystem.Save(folder, "cities", c, "test-mod");
            var loaded = SaveSystem.Load(folder, "cities", "test-mod");
            Check(loaded.Success, "the campaign saves and loads");
            loaded.Campaign.AttachProvinceTopology(map.CloneForOverrides());
            var differing = c.Nations.Where(nation => (loaded.Campaign.NationByTag(nation.Tag)?.CapitalCityId ?? "<null>") != (nation.CapitalCityId ?? "<null>") ||
                                                      loaded.Campaign.NationByTag(nation.Tag).CapitalProvinceId != nation.CapitalProvinceId).ToList();
            Check(differing.Count == 0 && loaded.Campaign.NationByTag("RUS").CapitalCityId == fled.Id,
                "a save and its load name the same capital city for every country" +
                (differing.Count > 0 ? ": " + string.Join(", ", differing.Take(6).Select(nation => nation.Tag)) : ""));

            // The same save as an older build would have written it: no such field at all.
            var path = Path.Combine(folder, "cities.bdfsave");
            var text = JsonNode.Parse(File.ReadAllText(path));
            var removed = Forget(text, "CapitalCityId");
            File.WriteAllText(path, text.ToJsonString());
            var older = SaveSystem.Load(folder, "cities", "test-mod");
            Check(removed > 200 && older.Success && older.Campaign.Nations.All(nation => nation.CapitalCityId is null),
                $"with the field taken out of {removed} countries the save still loads, as an older build's would");
            older.Campaign.AttachProvinceTopology(map.CloneForOverrides());
            var lost = new List<string>();
            foreach (var nation in c.Nations)
            {
                var then = older.Campaign.NationByTag(nation.Tag);
                // The one capital that went by a region's name is found the region's first city
                // if it has one now, and it has none: it stays as it was.
                if (!OwnsLand(c, nation)) continue;
                if ((then.CapitalCityId ?? "<null>") != (nation.CapitalCityId ?? "<null>"))
                    lost.Add($"{nation.Tag} {nation.CapitalCityId} -> {then.CapitalCityId ?? "null"}");
            }
            foreach (var line in lost.Take(12)) Console.WriteLine("    differs after the older save: " + line);
            Check(lost.Count == 0, "and when it meets the map every capital is bound to the city it stood in");
            Check(CountryNames.Capital(older.Campaign.NationByTag("UKR"), older.Campaign) == CountryNames.Capital(ukr, c),
                "so the older save's Ukraine is governed from Kyiv by name as well");
        }
        finally { try { Directory.Delete(folder, true); } catch (IOException) { } }

        // ---- a country born in play is governed from a city, and is called after it
        // The capital was put into a city of the seat region and the next line named it after
        // the region again: the card said the city, and everything that reads the name itself -
        // an organisation the new country founds, its exported definition - said the region.
        var esp = c.NationByTag("ESP");
        var leaving = c.StatesOf(esp)
            .Where(state => state.MapIndex != esp.CapitalStateMapIndex && CityCatalog.CitiesOf(c, state).Count > 0 &&
                            !CityCatalog.CitiesOf(c, state).Any(city => string.Equals(city.Name, state.Name, StringComparison.OrdinalIgnoreCase)))
            .OrderByDescending(state => state.Population).First();
        var born = SecessionEvents.Force(c, leaving)?.Seceded;
        var bornIn = born is null ? null : CapitalCore.City(born);
        Check(born is not null && bornIn is not null && CityCatalog.CitiesOf(c, leaving).Any(city => city.Id == bornIn.Id),
            $"{leaving.Name} leaves Spain as {born?.Tag}, governed from {bornIn?.Name ?? "no city"}, a city of that region");
        Check(born.CapitalName == bornIn.Name && CapitalCore.OwnName(born) is null &&
              CountryNames.Capital(born, c) == bornIn.DisplayName && OrganizationDynamics.CapitalOf(born) == $"city.{bornIn.Id}.name",
            $"and its capital is called {born.CapitalName} wherever it is read, not {leaving.Name}");

        // ---- what an older build's capital looked like: a name and a point of its own
        var legacy = World(seed, campaign =>
        {
            // Rome as the old catalogue had it, with no city named: the point is in the Vatican's
            // province and the name is the city's.
            var italy = campaign.NationByTag("ITA");
            italy.CapitalCityId = null; italy.CapitalName = "Rome"; italy.CapitalLatitude = 41.8955f; italy.CapitalLongitude = 12.4823f;
            // A mod that moves a capital the older way, by a name and coordinates.
            using var moved = JsonDocument.Parse("""{ "UKR": { "capital_name": "Kharkiv", "capital_latitude": 49.99, "capital_longitude": 36.23 } }""");
            CountryDefinitions.Apply(campaign, moved.RootElement);
            // And one that does it the new way, by the city's key alone.
            using var named = JsonDocument.Parse("""{ "POL": { "capital_city": "krakow_pol" } }""");
            CountryDefinitions.Apply(campaign, named.RootElement);
            // The older way again, to a city next door to the one the base game names: The Hague
            // is 52 km from Amsterdam and Tel Aviv 54 from Jerusalem. And to a place written as no
            // city is ("Tel Aviv-Yafo"): the city standing at the point.
            using var nextDoor = JsonDocument.Parse("""
                {
                  "NLD": { "capital_name": "The Hague", "capital_latitude": 52.08, "capital_longitude": 4.27 },
                  "ISR": { "capital_name": "Tel Aviv-Yafo", "capital_latitude": 32.08, "capital_longitude": 34.78 },
                  "MYS": { "capital_name": "Putrajaya", "capital_latitude": 2.914, "capital_longitude": 101.7019 }
                }
                """);
            CountryDefinitions.Apply(campaign, nextDoor.RootElement);
            // A mod that only renames a capital (mods/_templates/rename_the_world), and an entry
            // that gives a name and a city together, as every row of the base game's file does.
            using var renamed = JsonDocument.Parse("""
                {
                  "GBR": { "capital_name": "Londinium" },
                  "FRA": { "capital_name": "Lutetia", "capital_city": "paris_fra" }
                }
                """);
            CountryDefinitions.Apply(campaign, renamed.RootElement);
            // A mod's own cities file, laid over the game's (ModLoader hands map_data/cities to
            // Merge), and a country that names one of its cities as its capital.
            var held = CityCatalog.All;
            CityCatalog.Merge("""
                {
                  // comments and the older keys are read past
                  "modtown_ltu": {"name": "Mod Town", "latitude": 54.9, "longitude": 23.9, "population": 90000, "tier": 2, "country": "LTU"},
                  "kaunas_ltu": {"tier": 0},
                  "gdansk_pol": {"name": "Danzig"},
                  "utrecht_nld": {"tier": -1},
                  "nowhere_at_all": {"tier": 0},
                  "gone_already": {"removed": true},
                }
                """, modErrors);
            modHeld = (held, held.Count, held[CityCatalog.IndexOf("gdansk_pol")].Name);
            using var modCapital = JsonDocument.Parse("""{ "LTU": { "capital_city": "modtown_ltu" } }""");
            CountryDefinitions.Apply(campaign, modCapital.RootElement);
        });
        Check(legacy.NationByTag("ITA").CapitalCityId == "rome_ita" && CountryNames.Capital(legacy.NationByTag("ITA"), legacy) == "Rome",
            "a capital given as a name and a point is bound to the city standing there - Rome, though its dot is the Vatican's");
        Check(legacy.NationByTag("UKR").CapitalCityId == "kharkiv_ukr" && CountryNames.Capital(legacy.NationByTag("UKR")) == "Kharkiv",
            "a mod that moves a capital by name and coordinates still moves it, into the city at that point");
        Check(legacy.NationByTag("POL").CapitalCityId == "krakow_pol" &&
              legacy.ProvinceMap.ProvinceToNation[legacy.NationByTag("POL").CapitalProvinceId] == legacy.NationByTag("POL").MapIndex &&
              legacy.NationByTag("POL").CapitalProvinceId == CityCatalog.ProvincesOf(legacy.ProvinceMap)[CityCatalog.IndexOf("krakow_pol")],
            $"and one that names a city by its key moves the capital there ({CountryNames.Capital(legacy.NationByTag("POL"))})");

        // ---- the older way, next door, and a capital that is only renamed
        var nld = legacy.NationByTag("NLD");
        var isr = legacy.NationByTag("ISR");
        var mys = legacy.NationByTag("MYS");
        Check(nld.CapitalCityId == "the_hague_nld" && nld.CapitalName == "The Hague" && CountryNames.Capital(nld, legacy) == CityCatalog.Find("the_hague_nld").DisplayName,
            $"a mod that moves the Dutch capital to The Hague by name and coordinates moves it, 52 km from the city the game names ({nld.CapitalCityId})");
        Check(mys.CapitalCityId == "putrajaya_mys", $"and Malaysia's to Putrajaya, 25 km from Kuala Lumpur ({mys.CapitalCityId})");
        Check(isr.CapitalCityId == "tel_aviv_isr" && isr.CapitalName == "Tel Aviv" && CapitalCore.OwnName(isr) is null,
            $"one moved to a point under a name no city bears is the city standing at the point, by that city's name ({isr.CapitalCityId}, {isr.CapitalName})");
        var gbr = legacy.NationByTag("GBR");
        Check(gbr.CapitalCityId == "london_gbr" && gbr.CapitalProvinceId == CityCatalog.ProvincesOf(legacy.ProvinceMap)[CityCatalog.IndexOf("london_gbr")],
            "a mod that only renames the capital leaves it the city it was, governed from the same province");
        Check(gbr.CapitalName == "Londinium" && CapitalCore.OwnName(gbr) == "Londinium" && CountryNames.Capital(gbr) == "Londinium" &&
              CountryNames.Capital(gbr, legacy) == "Londinium" && OrganizationDynamics.CapitalOf(gbr) == "Londinium",
            $"and the capital reads by the name the mod gave it ({CountryNames.Capital(gbr, legacy)}), on the card and in a name made of capitals");
        var fra = legacy.NationByTag("FRA");
        Check(fra.CapitalCityId == "paris_fra" && fra.CapitalName == "Paris" && CountryNames.Capital(fra, legacy) == CityCatalog.Find("paris_fra").DisplayName,
            "an entry that gives a name and a city together is the city, under the city's name - the name is for a map with no such city");

        // ---- a mod's own cities
        var ltu = legacy.NationByTag("LTU");
        var modTown = CityCatalog.Find("modtown_ltu");
        Check(modTown is not null && CityCatalog.IndexOf("modtown_ltu") == CityCatalog.All.Count - 1 && modTown.Name == "Mod Town",
            "a mod's cities file adds its city to the map, after the cities already written");
        Check(CityCatalog.Find("utrecht_nld") is null && CityCatalog.Find("gdansk_pol").Name == "Danzig" && CityCatalog.Find("kaunas_ltu") is not null,
            "takes one off, renames another, and what it says of a city's rank changes nothing");
        Check(CityCatalog.Find("nowhere_at_all") is null && CityCatalog.Find("gone_already") is null &&
              modErrors.Count == 1 && modErrors[0].Contains("nowhere_at_all"),
            "a key that is no city and is given no place adds none, and the mod is told: " + string.Join(" ", modErrors));
        Check(!ReferenceEquals(modHeld.List, CityCatalog.All) && modHeld.List.Count == modHeld.Count && modHeld.Gdansk != "Danzig" &&
              CityCatalog.All.Count == modHeld.Count,
            "the catalogue is a new list - whoever held the old one holds it as it was, and knows by that to look again");
        Check(ltu.CapitalCityId == "modtown_ltu" && CountryNames.Capital(ltu, legacy) == "Mod Town" &&
              legacy.ProvinceMap.ProvinceToNation[ltu.CapitalProvinceId] == ltu.MapIndex &&
              ltu.CapitalProvinceId == CityCatalog.ProvincesOf(legacy.ProvinceMap)[CityCatalog.IndexOf("modtown_ltu")] &&
              OrganizationDynamics.CapitalOf(ltu) == "Mod Town",
            "and a country that names the mod's city as its capital is governed from it");
        CityCatalog.Reset();
        Check(CityCatalog.Find("modtown_ltu") is null && CityCatalog.Find("utrecht_nld") is not null && CityCatalog.Find("gdansk_pol").Name != "Danzig",
            "with the mod gone the cities are the game's own again");

        // ---- the same seed, the same world
        var twin = World(seed);
        var first = World(seed);
        Check(CampaignFingerprint.Of(twin) == CampaignFingerprint.Of(first),
            $"two worlds built from one seed have one fingerprint ({CampaignFingerprint.Of(first)})");
        Check(first.Nations.Select(nation => nation.Tag + ":" + nation.CapitalCityId + ":" + nation.CapitalProvinceId)
                .SequenceEqual(twin.Nations.Select(nation => nation.Tag + ":" + nation.CapitalCityId + ":" + nation.CapitalProvinceId)),
            "and the same capital cities in the same provinces");

        // ---- every capital as an older build knew it: a name and a point, and no city
        // That is what a save written before capitals were cities holds, and what a map made
        // the older way gives: "Washington D.C." at the point the capitals' table had, which is
        // seldom the city's own dot and never its spelling in the cities file. Each must be found
        // the city the game names today - not a suburb that happens to stand nearer the old point.
        var olderOpening = World(seed, campaign =>
        {
            foreach (var nation in campaign.Nations)
            {
                // Only a capital nothing has touched since the definitions were read: a history
                // that seats a government elsewhere (Sudan's in Port Sudan) is not the table's.
                if (!named.TryGetValue(nation.Tag, out var key) || nation.CapitalCityId != key) continue;
                if (!CapitalCatalog.TryGet(nation.Tag, out var was)) continue;
                nation.CapitalCityId = null;
                nation.CapitalName = calledInData.GetValueOrDefault(nation.Tag) ?? was.Name;
                nation.CapitalLatitude = was.Latitude;
                nation.CapitalLongitude = was.Longitude;
            }
        });
        var strayed = new List<string>();
        var tried = 0;
        foreach (var nation in first.Nations)
        {
            if (!OwnsLand(first, nation) || !named.ContainsKey(nation.Tag) || !CapitalCatalog.TryGet(nation.Tag, out var was)) continue;
            tried++;
            var then = olderOpening.NationByTag(nation.Tag);
            if (then.CapitalCityId != nation.CapitalCityId)
                strayed.Add($"{nation.Tag} '{calledInData.GetValueOrDefault(nation.Tag) ?? was.Name}' -> {then.CapitalCityId ?? "null"}, not {nation.CapitalCityId}");
        }
        foreach (var line in strayed) Console.WriteLine("    found another city the older way: " + line);
        Check(tried > 200 && strayed.Count == 0,
            $"{tried} capitals given the older way - the name and point an older build had - are each found the city the game names");
    }

    /// <summary>Take a field out of every object of a document, as if it had never been written.</summary>
    static int Forget(JsonNode node, string field)
    {
        var removed = 0;
        switch (node)
        {
            case JsonObject item:
                if (item.Remove(field)) removed++;
                foreach (var child in item.Select(pair => pair.Value).ToList()) removed += Forget(child, field);
                break;
            case JsonArray list:
                foreach (var child in list) removed += Forget(child, field);
                break;
        }
        return removed;
    }
}
