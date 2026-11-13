using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// The map source's glaciers, reefs, buffer zones and base areas are not countries.
///
/// One table says which rows of the country table they are (common/country_merges) and it is
/// applied once, where the world is built. This is the whole of what follows from that, asked of
/// the real 2024 world on its real map: none of them is in the nation list by tag or by name in
/// either language, their regions and provinces belong to whoever administers them from the
/// first day, nothing is left on the map in a colour no country answers to, and everything that
/// IS in the nation list has a name in English and in Russian, a flag to fly and ground or a
/// movement to be. Then the two ways the answer could quietly stop being true: a month of the
/// living world bringing one back, and a save from before the table loading as something else.
/// </summary>
static class NonCountriesTests
{
    static int _passed;

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("non-countries: " + label);
        _passed++;
        Console.WriteLine("PASS non-countries: " + label);
    }

    /// <summary>
    /// Every entry, the name the map source gives it, and whose its ground is on 1 January 2024.
    /// Written out here rather than read back from the table: a line dropped from the data file
    /// should fail this test, not shorten it.
    /// </summary>
    static readonly (string Tag, string Name, string Holder)[] Entries =
    {
        ("KAS", "Siachen Glacier", "IND"),
        ("SCR", "Scarborough Reef", "CHN"),
        ("CNM", "Cyprus No Mans Area", "CYP"),
        ("KAB", "Baykonur Cosmodrome", "KAZ"),
        ("USG", "US Naval Base Guantanamo Bay", "CUB"),
        ("ESB", "Dhekelia Sovereign Base Area", "GBR"),
        ("WSB", "Akrotiri Sovereign Base Area", "GBR"),
        ("IOT", "British Indian Ocean Territory", "GBR"),
        ("SGS", "South Georgia and the Islands", "GBR"),
        ("SPI", "Southern Patagonian Ice Field", "ARG"),
        ("BJN", "Bajo Nuevo Bank (Petrel Is.)", "COL"),
        ("SER", "Serranilla Bank", "COL"),
        ("BRI", "Brazilian Island", "BRA"),
        ("CLP", "Clipperton Island", "FRA"),
        ("ATF", "French Southern and Antarctic Lands", "FRA"),
        ("CSI", "Coral Sea Islands", "AUS"),
        ("ATC", "Ashmore and Cartier Islands", "AUS"),
        ("HMD", "Heard Island and McDonald Islands", "AUS"),
        ("IOA", "Indian Ocean Territories", "AUS"),
        ("UMI", "United States Minor Outlying Islands", "USA"),
        ("PGA", "Spratly Islands", CountryMerges.Nobody),
        ("BRT", "Bir Tawil", CountryMerges.Nobody),
        ("ATA", "Antarctica", CountryMerges.Nobody)
    };

    /// <summary>The region each entry had, by id, and the country it is a region of now.</summary>
    static readonly (string Region, string Holder)[] Regions =
    {
        ("99_x06", "IND"), ("99_x04", "CYP"), ("99_x05", "KAZ"), ("99_x13", "CUB"),
        ("99_x16", "GBR"), ("99_x17", "GBR"), ("io_x01", "GBR"), ("gs_x01", "GBR"),
        ("tf_x01", "FRA"), ("hm_x01", "AUS"), ("99_x14", "AUS"), ("um_76", "USA")
    };

    public static void Run()
    {
        _passed = 0;
        const int seed = 20261113;
        var language = Loc.Language;
        try
        {
            CountryMerges.ResetCatalog();
            Table();
            var c = Campaign.CreateBootstrap(seed);
            NotInTheNationList(c, "as the world is built");
            RegionsBelongToTheirHolders(c);

            MapRasterCatalog.ResetCatalog();
            Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                    WorldMapSource.Width, WorldMapSource.Height, out var map, out var mapError),
                "the world map loads: " + mapError);
            c.AttachProvinceTopology(map);
            Check(c.ProvinceMap == map && map.ProvinceCount > 1000 && c.ProvinceController.Length == map.ProvinceCount + 1,
                $"the province topology attaches ({map.ProvinceCount:N0} provinces)");
            NotInTheNationList(c, "once the map is attached");
            ProvincesBelongToTheirHolders(c, map);
            EveryNationIsSomebody(c);
            NobodysLandIsNotACountry(c);

            c.IsPaused = false;
            for (var day = 0; day < 35; day++) c.TickDay();
            NotInTheNationList(c, "five weeks on");
            Check(c.Nations.Select((nation, index) => nation.Id == index).All(same => same),
                "ids are still positions in the nation list");

            AModCanTakeAnEntryOffTheList(seed);
            ASaveFromBeforeTheTableStillLoads(seed, map);
        }
        finally
        {
            Loc.Language = language;
            CountryMerges.ResetCatalog();
        }
        Console.WriteLine($"non-countries: all {_passed} checks passed");
    }

    static void Table()
    {
        var ground = CountryMerges.GroundByMapIndex();
        foreach (var (tag, name, holder) in Entries)
        {
            Check(CountryMerges.IsMerged(tag), $"{tag} ({name}) is in common/country_merges");
            Check(string.Equals(CountryMerges.OwnerOf(tag), holder, StringComparison.OrdinalIgnoreCase),
                $"{tag}'s ground is {(holder == CountryMerges.Nobody ? "nobody" : holder)}'s");
            var row = CountryMerges.MapIndexOf(tag);
            var holderRow = holder == CountryMerges.Nobody ? 0 : CountryMerges.MapIndexOf(holder);
            Check(row > 0 && ground.TryGetValue(row, out var held) && held == holderRow && (holderRow > 0 || holder == CountryMerges.Nobody),
                $"{tag}'s row of the country table ({row}) maps to its holder's ({holderRow})");
        }
        Check(CountryMerges.All.Keys.All(tag => Entries.Any(entry => string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase))),
            "the table has no line this test has not been told about: " +
            string.Join(", ", CountryMerges.All.Keys.Where(tag => !Entries.Any(entry => string.Equals(entry.Tag, tag, StringComparison.OrdinalIgnoreCase)))));
        // Nothing else in the data is written for one as if it were a country: a definition, a
        // line of history, a place in a formable's founding list.
        var defined = new List<string>();
        foreach (var document in GameDataLoader.DataDocuments("common", "countries"))
            using (document)
                if (document.RootElement.ValueKind == JsonValueKind.Object)
                    foreach (var entry in document.RootElement.EnumerateObject())
                        if (CountryMerges.IsMerged(entry.Name)) defined.Add(entry.Name);
        Check(defined.Count == 0, "common/countries defines none of them" + (defined.Count > 0 ? ": " + string.Join(", ", defined) : ""));
        var history = new List<string>();
        foreach (var folder in new[] { "countries", "diplomacy" })
            foreach (var file in GameDataLoader.DataFiles("history", folder, "2024"))
            {
                var text = File.ReadAllText(file);
                foreach (var (tag, _, _) in Entries)
                    if (text.Contains("\"" + tag + "\"", StringComparison.Ordinal)) history.Add($"{tag} in {Path.GetFileName(file)}");
            }
        Check(history.Count == 0, "history/countries and history/diplomacy write nothing for them" +
            (history.Count > 0 ? ": " + string.Join(", ", history.Take(8)) : ""));
        var founding = FormableNations.All.Where(formable => formable.EligibleTags.Concat(formable.RequiredTags).Any(CountryMerges.IsMerged) ||
                                                             CountryMerges.IsMerged(formable.Restores))
            .Select(formable => formable.Id).ToList();
        Check(founding.Count == 0, "no formable is founded by or made of one" + (founding.Count > 0 ? ": " + string.Join(", ", founding) : ""));
        var named = GameDataLoader.DataFiles("history", "states", "2024").Select(Path.GetFileNameWithoutExtension)
            .Where(CountryMerges.IsMerged).ToList();
        Check(named.Count == 0, "no file of history/states is named for one" + (named.Count > 0 ? ": " + string.Join(", ", named) : ""));
        Check(CountryMerges.MapIndexOf("IND") > 0 && !ground.ContainsKey(CountryMerges.MapIndexOf("IND")) &&
              CountryMerges.MapIndexOf("no such tag") == 0, "a real country's row maps to nothing, and an unknown tag has no row");
    }

    static void NotInTheNationList(Campaign c, string when)
    {
        var rows = WorldMapTables.CountryRows.Length;
        var landed = c.Nations.Count(nation => nation.Movement is null && nation.FactionId.Length == 0 && nation.SecededFromId < 0);
        if (c.ElapsedDays == 0 && c.ProvinceMap is null)
            Check(c.Nations.Count == rows - Entries.Length,
                $"the world has the country table's {rows} rows less the {Entries.Length} that are not countries: {c.Nations.Count} ({landed} of them plain countries)");

        var leaked = new List<string>();
        foreach (var lang in new[] { "english", "russian" })
        {
            Loc.Language = lang;
            // What the entry would be called in this language if it were shown anywhere.
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var (tag, name, _) in Entries)
            {
                names.Add(name);
                names.Add(Loc.Get(name));
                names.Add(CountryNames.Of(tag));
            }
            foreach (var nation in c.Nations)
            {
                if (Entries.Any(entry => string.Equals(entry.Tag, nation.Tag, StringComparison.OrdinalIgnoreCase) ||
                                         string.Equals(entry.Tag, nation.Iso, StringComparison.OrdinalIgnoreCase)))
                    leaked.Add($"{nation.Tag} by tag");
                else if (names.Contains(nation.Name) || names.Contains(nation.DisplayName) ||
                         names.Contains(CountryNames.Resolve(nation, true)))
                    leaked.Add($"{nation.Tag} by name '{nation.DisplayName}' ({lang})");
            }
        }
        Loc.Language = "english";
        Check(leaked.Count == 0, $"none of the {Entries.Length} entries is a nation {when}" +
            (leaked.Count > 0 ? ": " + string.Join(", ", leaked.Distinct().Take(8)) : ""));

        var indices = Entries.Select(entry => CountryMerges.MapIndexOf(entry.Tag)).ToHashSet();
        Check(c.Nations.All(nation => !indices.Contains(nation.MapIndex)), $"no nation stands on an entry's row of the country table {when}");
        Check(c.Player is not null && !Entries.Any(entry => entry.Tag == c.Player.Tag), $"the seat is a real country {when}");
        Check(c.Relations.Values.All(relation => relation.A >= 0 && relation.A < c.Nations.Count && relation.B >= 0 && relation.B < c.Nations.Count),
            $"every relation is between two nations of the list {when}");
        Check(c.Armies.All(army => army.NationId >= 0 && army.NationId < c.Nations.Count) &&
              c.Fleets.All(fleet => fleet.NationId >= 0 && fleet.NationId < c.Nations.Count),
            $"every army and fleet answers to a nation of the list {when}");
        Check(c.Nations.Where(nation => nation.IsSubject).All(nation => nation.OverlordId >= 0 && nation.OverlordId < c.Nations.Count),
            $"every subject's overlord is a nation of the list {when}");
    }

    static void RegionsBelongToTheirHolders(Campaign c)
    {
        foreach (var (id, holderTag) in Regions)
        {
            var region = c.States.Find(state => state.Id == id);
            var holder = c.NationByTag(holderTag);
            Check(region is not null && holder is not null && region.NationMapIndex == holder.MapIndex &&
                  string.Equals(region.NationId, holder.Tag, StringComparison.OrdinalIgnoreCase) && !region.Unclaimed,
                $"{id} ({region?.Name}) is a region of {holderTag} before the map is laid");
            Check(c.StatesOf(holder).Contains(region), $"{holderTag} counts {id} among its regions");
        }
        Check(c.States.Find(state => state.Id == "99_x06")?.Name == "Siachen Glacier" &&
              c.States.Find(state => state.Id == "99_x04")?.Name == "UN Buffer Zone" &&
              c.States.Find(state => state.Id == "99_x13")?.Name == "Guantanamo Bay",
            "the three regions the map source misnamed are called what they are");
        // What a player reads is the row keyed by the region's id, not the roster's name.
        string Shown(string id) => c.States.Find(state => state.Id == id).DisplayName;
        Loc.Language = "english";
        Check(Shown("99_x06") == "Siachen Glacier" && Shown("99_x04") == "UN Buffer Zone" && Shown("99_x13") == "Guantanamo Bay",
            $"and read so in English: {Shown("99_x06")}, {Shown("99_x04")}, {Shown("99_x13")}");
        Loc.Language = "russian";
        Check(Shown("99_x06") == "Ледник Сиачен" && Shown("99_x04") == "Буферная зона ООН" && Shown("99_x13") == "Гуантанамо",
            $"and in Russian: {Shown("99_x06")}, {Shown("99_x04")}, {Shown("99_x13")}");
        Loc.Language = "english";

        // The dispute is on the region, where every other one is.
        Check(c.States.Find(state => state.Id == "99_x06") is { } siachen && siachen.Claims.Contains("PAK") && siachen.CoreTags.Contains("IND"),
            "Siachen is India's homeland ground and Pakistan's claim");
        Check(c.States.Find(state => state.Id == "io_x01").Claims.Contains("MUS") &&
              c.States.Find(state => state.Id == "gs_x01").Claims.Contains("ARG") &&
              c.States.Find(state => state.Id == "um_76").Claims.Contains("HTI"),
            "Chagos, South Georgia and Navassa carry their claimants");
        foreach (var claimant in new[] { "PAK", "MUS", "ARG", "HTI" })
            Check(c.NationByTag(claimant) is not null, $"the claimant {claimant} is a country");

        var lands = c.States.Where(state => state.Id.StartsWith("aq_", StringComparison.Ordinal)).ToList();
        Check(lands.Count == 17 && lands.All(land => land.NationMapIndex == 0 && land.NationId.Length == 0 && ColonisationCore.IsUnclaimed(land)),
            $"Antarctica is {lands.Count} lands that belong to nobody, without a country to have held them first");
        Check(c.States.All(state => state.NationMapIndex == 0 || c.NationByMapIndex(state.NationMapIndex) is not null),
            "every region is nobody's or a nation's");
        Check(c.States.Where(state => state.NationMapIndex == 0).All(state => state.Unclaimed || string.Equals(state.Kind, "Lake", StringComparison.OrdinalIgnoreCase)),
            "a region with no owner is unclaimed land or water, never an orphan");
    }

    static void ProvincesBelongToTheirHolders(Campaign c, ProvinceTopology map)
    {
        var known = c.Nations.Select(nation => nation.MapIndex).ToHashSet();
        int orphans = 0, held = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var owner = map.ProvinceToNation[province];
            var controller = c.ProvinceController[province];
            if (owner != 0 && !known.Contains(owner)) orphans++;
            if (controller != 0 && !known.Contains(controller)) held++;
        }
        Check(orphans == 0 && held == 0, $"no province is owned ({orphans}) or held ({held}) by a number no nation answers to");

        var points = ProvinceOwnership.PointCorrections(map);
        var moved = 0;
        foreach (var (tag, name, holderTag) in Entries)
        {
            var row = CountryMerges.MapIndexOf(tag);
            var expected = holderTag == CountryMerges.Nobody ? 0 : c.NationByTag(holderTag).MapIndex;
            // The political map's own lines for the entry, less the ones a point on the map
            // deliberately sends elsewhere (the Patagonian ice, Bir Tawil).
            var written = ProvinceOwnership.ProvincesOf(tag)
                .Where(p => p > 0 && p <= map.ProvinceCount && !map.IsWater(p) && !points.ContainsKey(p)).ToArray();
            Check(written.All(p => map.ProvinceToNation[p] == expected && c.ProvinceController[p] == expected),
                $"{tag} ({name}): {written.Length} province(s) the political map writes for it are {(expected == 0 ? "nobody" : holderTag)}'s");
            // And the ground itself: every province whose centre the raster paints in the
            // entry's colour, whether or not the political map has a line for it.
            var painted = new List<int>();
            for (var province = 1; province <= map.ProvinceCount; province++)
            {
                var cell = map.ProvinceCenterCell[province];
                if (cell >= 0 && cell < WorldMapSource.Control.Length && WorldMapSource.Control[cell] == row && !map.IsWater(province))
                    painted.Add(province);
            }
            var elsewhere = painted.Where(p => map.ProvinceToNation[p] != expected && !points.ContainsKey(p)).ToArray();
            // Graham Land and the Polar Plateau may since have been colonised by nobody on day 0,
            // so "nobody's" really is zero here.
            Check(elsewhere.Length == 0,
                $"{tag}: all {painted.Count} province(s) standing on its raster colour belong to {(expected == 0 ? "nobody" : holderTag)}" +
                (elsewhere.Length > 0 ? $" - not {string.Join(", ", elsewhere.Take(5))}" : ""));
            moved += painted.Count;
        }
        Check(moved > 0, $"{moved:N0} provinces stand on ground the map source drew as one of these 'countries'");

        foreach (var (id, holderTag) in Regions)
        {
            var region = c.States.Find(state => state.Id == id);
            var holder = c.NationByTag(holderTag);
            var provinces = PeaceConference.ProvincesOfState(map, region.MapIndex);
            Check(region.NationMapIndex == holder.MapIndex && provinces.Count > 0 && provinces.All(p => map.ProvinceToNation[p] == holder.MapIndex),
                $"{id} ({region.Name}) and its {provinces.Count} province(s) are {holderTag}'s on the map");
        }
        // Held by arrangement, not owned: the lease is the existing mechanism and it survives.
        var baikonur = c.States.Find(state => state.Id == "99_x05");
        var rus = c.NationByTag("RUS");
        Check(PeaceConference.ProvincesOfState(map, baikonur.MapIndex).All(p => c.ProvinceController[p] == rus.MapIndex),
            "Baikonur is Kazakhstan's ground and held by Russia under its lease");
        var guantanamo = c.States.Find(state => state.Id == "99_x13");
        var usa = c.NationByTag("USA");
        Check(PeaceConference.ProvincesOfState(map, guantanamo.MapIndex).All(p => c.ProvinceController[p] == usa.MapIndex),
            "Guantanamo Bay is Cuba's ground and held by the United States under its lease");
    }

    static bool HasCyrillic(string text) => !string.IsNullOrEmpty(text) && text.Any(letter => letter >= 'Ѐ' && letter <= 'ӿ');
    static bool LooksLikeAKey(string text) =>
        text.StartsWith("country.", StringComparison.Ordinal) || text.StartsWith("formable.", StringComparison.Ordinal) ||
        text.StartsWith("movement.", StringComparison.Ordinal) || text.StartsWith("rising:", StringComparison.Ordinal) ||
        text.StartsWith("faction.", StringComparison.Ordinal) || text.Contains(".name", StringComparison.Ordinal);

    static void EveryNationIsSomebody(Campaign c)
    {
        // The same catalogues the game asks: a flag is a file in gfx/flags or a design in
        // common/flag_designs, and a headless run has to be told to read them.
        SpriteCatalog.Load();
        FlagCatalog.ResetCatalog();
        FlagDesignCatalog.ResetCatalog();
        var unnamed = new List<string>();
        var untranslated = new List<string>();
        var flagless = new List<string>();
        var nobodyHome = new List<string>();
        foreach (var nation in c.Nations)
        {
            Loc.Language = "english";
            var english = nation.DisplayName ?? "";
            var englishMap = CountryNames.Resolve(nation, true) ?? "";
            if (english.Trim().Length == 0 || englishMap.Trim().Length == 0 || LooksLikeAKey(english) || LooksLikeAKey(englishMap) ||
                string.Equals(english, nation.Tag, StringComparison.Ordinal))
                unnamed.Add($"{nation.Tag}='{english}'");
            Loc.Language = "russian";
            var russian = nation.DisplayName ?? "";
            var russianMap = CountryNames.Resolve(nation, true) ?? "";
            if (!HasCyrillic(russian) || !HasCyrillic(russianMap) || LooksLikeAKey(russian))
                untranslated.Add($"{nation.Tag}='{russian}'/'{russianMap}'");
            Loc.Language = "english";
            var flag = FlagCatalog.Resolve(c, nation);
            if (flag.Length == 0 || !SpriteCatalog.Has(flag) || SpriteCatalog.TextureOf(flag).Length == 0) flagless.Add(nation.Tag);
            // A country is its regions and its people; a movement is its following. A row with
            // neither on the first day is exactly what this pass removed.
            if (nation.Movement is null && (c.StatesOf(nation).Length == 0 || nation.Population <= 0 || !c.HasOwnedLand(nation)))
                nobodyHome.Add($"{nation.Tag}(regions {c.StatesOf(nation).Length}, people {nation.Population:N0})");
        }
        var movements = c.Nations.Count(nation => nation.Movement is not null);
        Check(unnamed.Count == 0, $"all {c.Nations.Count} nations ({movements} of them movements) have an English name" +
            (unnamed.Count > 0 ? $" - not {unnamed.Count}: " + string.Join(", ", unnamed.Take(12)) : ""));
        Check(untranslated.Count == 0, $"all {c.Nations.Count} nations have a Russian name, in full and on the map" +
            (untranslated.Count > 0 ? $" - not {untranslated.Count}: " + string.Join(", ", untranslated.Take(12)) : ""));
        Check(flagless.Count == 0, $"all {c.Nations.Count} nations have a flag to fly" +
            (flagless.Count > 0 ? $" - not {flagless.Count}: " + string.Join(", ", flagless.Take(12)) : ""));
        Check(nobodyHome.Count == 0, "every country that is not a movement opens with regions, ground and people" +
            (nobodyHome.Count > 0 ? $" - not {nobodyHome.Count}: " + string.Join(", ", nobodyHome.Take(12)) : ""));
        // Spellings that were the map source's rather than anybody's name for the place.
        var sourceSpellings = new[] { "S.A.R", "Aland", "eSwatini", "Barthelemy", "No Mans", "Baykonur", "(Petrel" };
        foreach (var lang in new[] { "english", "russian" })
        {
            Loc.Language = lang;
            var odd = c.Nations.Where(nation => sourceSpellings.Any(spelling =>
                    nation.Name.Contains(spelling, StringComparison.Ordinal) || nation.DisplayName.Contains(spelling, StringComparison.Ordinal) ||
                    CountryNames.Resolve(nation, true).Contains(spelling, StringComparison.Ordinal)))
                .Select(nation => $"{nation.Tag}='{nation.DisplayName}'").ToList();
            Check(odd.Count == 0, $"no country carries a map-source spelling ({lang})" + (odd.Count > 0 ? ": " + string.Join(", ", odd) : ""));
        }
        Loc.Language = "english";
        Check(CountryNames.Resolve(c.NationByTag("SRB"), true) == "SERBIA" && CountryNames.Resolve(c.NationByTag("TZA"), true) == "TANZANIA" &&
              CountryNames.Resolve(c.NationByTag("HKG"), true) == "Hong Kong" && CountryNames.Resolve(c.NationByTag("MAC"), true) == "Macao",
            "Serbia, Tanzania, Hong Kong and Macao are labelled by the names they are known by");
        Loc.Language = "russian";
        Check(CountryNames.Resolve(c.NationByTag("SRB"), true) == "Сербия" && CountryNames.Resolve(c.NationByTag("TZA"), true) == "Танзания",
            "and Serbia and Tanzania so in Russian");
        Loc.Language = "english";
        Check(c.Nations.Select(nation => nation.Tag).Distinct(StringComparer.OrdinalIgnoreCase).Count() == c.Nations.Count &&
              c.Nations.Select(nation => nation.MapIndex).Distinct().Count() == c.Nations.Count,
            "no two nations share a tag or a place on the map");
        foreach (var lang in new[] { "english", "russian" })
        {
            Loc.Language = lang;
            var twice = c.Nations.Where(nation => nation.Movement is null).GroupBy(nation => nation.DisplayName, StringComparer.OrdinalIgnoreCase)
                .Where(group => group.Count() > 1).Select(group => $"'{group.Key}' ({string.Join("/", group.Select(nation => nation.Tag))})").ToList();
            Check(twice.Count == 0, $"no two countries read the same in {lang}" + (twice.Count > 0 ? ": " + string.Join(", ", twice.Take(8)) : ""));
        }
        Loc.Language = "english";
    }

    static void NobodysLandIsNotACountry(Campaign c)
    {
        var labels = new[] { "Unclaimed", "Unowned", "Nobody", "None", "No owner", "Antarctica", "Bir Tawil" };
        foreach (var lang in new[] { "english", "russian" })
        {
            Loc.Language = lang;
            var named = c.Nations.Where(nation => labels.Any(label =>
                string.Equals(nation.Name, label, StringComparison.OrdinalIgnoreCase) ||
                string.Equals(nation.DisplayName, Loc.Get(label), StringComparison.OrdinalIgnoreCase))).Select(nation => nation.Tag).ToList();
            Check(named.Count == 0, $"no nation is called after land nobody owns ({lang})" + (named.Count > 0 ? ": " + string.Join(", ", named) : ""));

            // The one list that prints an owner beside every region: land with no owner is said
            // to be nobody's, in the reader's language, not given a name where a country's goes.
            var survey = ResourceAtlas.Read(c, Commodity.Coal, 0.01f);
            var unowned = survey.Regions.Where(region => region.Id.StartsWith("aq_", StringComparison.Ordinal)).ToList();
            Check(unowned.Count > 0 && unowned.All(region => region.OwnerId == 0 && region.OwnerName == Loc.Get("Land that belongs to nobody") &&
                      region.OwnerName != "Unclaimed" && (lang == "english" || HasCyrillic(region.OwnerName))),
                $"the resource atlas says Antarctic coal lies in land that belongs to nobody ({lang}: '{unowned.FirstOrDefault()?.OwnerName}')");
        }
        Loc.Language = "english";
    }

    /// <summary>
    /// The table is data: a mod that wants Scarborough Shoal to be a country after all writes one
    /// line, and it is then an ordinary row of the country table again - and only it.
    /// </summary>
    static void AModCanTakeAnEntryOffTheList(int seed)
    {
        try
        {
            using (var mod = JsonDocument.Parse("{ \"SCR\": { \"into\": \"\" }, \"KAS\": { \"into\": \"PAK\" } }"))
                CountryMerges.Merge(mod.RootElement);
            var modded = Campaign.CreateBootstrap(seed);
            Check(modded.NationByTag("SCR") is { } reef && reef.MapIndex == CountryMerges.MapIndexOf("SCR") &&
                  modded.Nations.Count == WorldMapTables.CountryRows.Length - Entries.Length + 1,
                "a mod's one line makes an entry a country again, and only that one");
            Check(modded.NationByTag("KAS") is null && modded.States.Find(state => state.Id == "99_x06").NationMapIndex == modded.NationByTag("PAK").MapIndex,
                "a mod's one line gives the Siachen Glacier to Pakistan instead");
        }
        finally { CountryMerges.ResetCatalog(); }
        Check(CountryMerges.IsMerged("SCR") && CountryMerges.OwnerOf("KAS") == "IND", "the base table reloads");
    }

    /// <summary>
    /// A campaign saved before the table was applied at the world's building has these entries in
    /// its nation list, and ids are positions in that list. It has to load as the campaign that
    /// was saved - same nations, same ids - with the leftover entry kept out of sight and its
    /// ground where the table says.
    /// </summary>
    static void ASaveFromBeforeTheTableStillLoads(int seed, ProvinceTopology map)
    {
        var old = Campaign.CreateBootstrap(seed);
        // What such a save carries: the entry as a country of its list, with the country table's
        // own number and figures, and no ground (the political map always gave that to its holder).
        var row = CountryMerges.MapIndexOf("KAS");
        var legacy = old.AddNation(new Nation
        {
            Tag = "KAS", Iso = "KAS", Name = "Siachen Glacier", Continent = "Asia", Region = "Southern Asia",
            Population = 6000, Gdp = 0.01f, Color = "#949494", MapIndex = row,
            Government = GovernmentCatalog.Default, Ideology = IdeologyCatalog.Default
        });
        old.AttachProvinceTopology(map.CloneForOverrides());
        var india = old.NationByTag("IND");
        var count = old.Nations.Count;
        var folder = Path.Combine(Path.GetTempPath(), "dof-non-countries-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder, "before_the_table", old, "tests");
            var loaded = SaveSystem.Load(folder, "before_the_table", "tests");
            Check(loaded.Success && loaded.Campaign is not null, "a save that still carries an entry as a nation loads: " + loaded.Warning);
            var c = loaded.Campaign;
            c.AttachProvinceTopology(map.CloneForOverrides());
            var kept = c.NationByTag("KAS");
            Check(c.Nations.Count == count && kept is not null && kept.Id == legacy.Id && c.NationByTag("IND").Id == india.Id,
                "it loads with the nation list it was saved with: nothing is taken out of the middle of it");
            Check(CountryMerges.IsLegacyEntry(c, kept) && !CountryMerges.IsLegacyEntry(c, c.NationByTag("IND")),
                "the leftover entry is known for what it is, and India is not");
            var ground = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).Count(p => c.ProvinceMap.ProvinceToNation[p] == row);
            var siachen = c.States.Find(state => state.Id == "99_x06");
            Check(ground == 0 && siachen.NationMapIndex == c.NationByTag("IND").MapIndex,
                "its ground is still India's in the loaded campaign");
            c.IsPaused = false;
            for (var day = 0; day < 14; day++) c.TickDay();
            Check(kept.Movement is null && !c.HasOwnedLand(kept) && kept.Population == 0 && c.Fallen.Contains(kept.Id),
                "two weeks on it has simply ended: no exile, no ground, no people");
        }
        finally
        {
            try { Directory.Delete(folder, true); } catch (IOException) { } catch (UnauthorizedAccessException) { }
        }
    }
}
