using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// Strait crossings on the production map: every shipped entry lands on two shores, an army
/// marches Spain to Morocco and Crimea to Krasnodar over them at the crossing's price, an
/// enemy fleet stops a ferry and not a bridge, and taking the data away takes the edges away.
/// </summary>
static class StraitCrossingTests
{
    static int _passed;

    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("crossings: " + label);
        _passed++;
        Console.WriteLine("PASS crossings: " + label);
    }

    static ProvinceTopology RealMap()
    {
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
                WorldMapSource.Width, WorldMapSource.Height, out var map, out var why)) throw new Exception(why);
        return map;
    }

    /// <summary>
    /// Provinces between two points over drawn borders alone, ignoring who owns what; -1 past
    /// the limit. How "the other shore" is told from "round the corner of the same bay".
    /// </summary>
    static int LandHops(ProvinceTopology map, int from, int to, int limit)
    {
        var depth = new Dictionary<int, int> { [from] = 0 };
        var queue = new Queue<int>();
        queue.Enqueue(from);
        while (queue.Count > 0)
        {
            var at = queue.Dequeue();
            if (at == to) return depth[at];
            if (depth[at] >= limit) continue;
            foreach (var next in map.LandNeighbors[at])
            {
                if (map.IsWater(next) || depth.ContainsKey(next)) continue;
                depth[next] = depth[at] + 1;
                queue.Enqueue(next);
            }
        }
        return -1;
    }

    static bool UsesEdge(int start, IReadOnlyList<int> route, int a, int b)
    {
        var previous = start;
        foreach (var step in route)
        {
            if ((previous == a && step == b) || (previous == b && step == a)) return true;
            previous = step;
        }
        return false;
    }

    static Defines ShippedDefines()
    {
        var defines = new Defines();
        GameDataLoader.ApplyDefines(defines);
        return defines;
    }

    static Army Division(int id, Nation nation, ProvinceTopology map, int province) => new()
    {
        Id = id, NationId = nation.Id, ProvinceId = province, StateMapIndex = map.ProvinceToState[province],
        TemplateId = "line_infantry", Divisions = 1, Personnel = 10_000, MaxPersonnel = 10_000,
        Strength = 1, SupplyLevel = 1, Organisation = .8f
    };

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, day * 24L);

    public static void Run()
    {
        _passed = 0;
        try { RunChecks(); }
        finally
        {
            // The catalogue is the process's: a later suite must find the shipped crossings.
            LandCrossingCatalog.ResetCatalog();
        }
        Console.WriteLine($"CROSSINGS_OK checks={_passed}");
    }

    static void RunChecks()
    {
        var c = Campaign.CreateBootstrap(20241113);
        var map = RealMap();
        c.AttachProvinceTopology(map);
        var defines = ShippedDefines();
        var controllers = c.ProvinceController;

        // --- the shipped data ---------------------------------------------------------
        var authored = LandCrossingCatalog.All.Where(crossing => crossing.Open).ToList();
        Check(authored.Count >= 30 && LandCrossings.LastErrors.Count == 0 && map.Crossings.Count == authored.Count,
            $"every shipped crossing is placed: {map.Crossings.Count} of {authored.Count}, " +
            $"{LandCrossings.LastErrors.Count} refused {string.Join(" | ", LandCrossings.LastErrors)}");
        foreach (var crossing in map.Crossings)
        {
            var by = LandHops(map, crossing.From, crossing.To, 6);
            Check(crossing.From != crossing.To && !map.IsWater(crossing.From) && !map.IsWater(crossing.To) &&
                  Array.IndexOf(map.LandNeighbors[crossing.From], crossing.To) < 0 && by < 0 &&
                  crossing.SpanKm <= defines.CrossingMaxSpanKm &&
                  Array.IndexOf(map.Neighbors[crossing.From], crossing.To) >= 0 &&
                  Array.IndexOf(map.Neighbors[crossing.To], crossing.From) >= 0 &&
                  ReferenceEquals(map.CrossingBetween(crossing.To, crossing.From), crossing),
                $"{crossing.Id} joins two land provinces on different shores ({crossing.From} and {crossing.To}, " +
                $"{crossing.SpanKm:0} km, by land {(by < 0 ? "more than 6 provinces" : by + " provinces")})");
        }
        var sorted = true;
        var edges = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            var row = map.Neighbors[province];
            for (var index = 1; index < row.Length; index++) sorted &= row[index - 1] < row[index];
            edges += map.LandNeighbors[province].Length;
            if (!map.IsCrossingEnd(province)) sorted &= ReferenceEquals(row, map.LandNeighbors[province]);
        }
        Check(sorted && edges / 2 == map.EdgeCount,
            "neighbour rows stay in id order, untouched away from a crossing, and the drawn borders are the map's own");

        var gibraltar = map.Crossings.FirstOrDefault(crossing => crossing.Id == "gibraltar_ferry");
        var kerch = map.Crossings.FirstOrDefault(crossing => crossing.Id == "crimean_bridge");
        var esp = c.NationByTag("ESP");
        var mar = c.NationByTag("MAR");
        var rus = c.NationByTag("RUS");
        Check(gibraltar is not null && gibraltar.Kind == LandCrossingKind.Ferry &&
              map.ProvinceToNation[gibraltar.From] == esp.MapIndex && map.ProvinceToNation[gibraltar.To] == mar.MapIndex,
            "the Gibraltar ferry joins Spain to Morocco");
        string Region(int province) => c.StateByMapIndex(map.ProvinceToState[province])?.Name ?? "";
        Check(kerch is not null && kerch.Kind == LandCrossingKind.Bridge &&
              Region(kerch.From).Contains("Crimea", StringComparison.OrdinalIgnoreCase) &&
              Region(kerch.To).Contains("Krasnodar", StringComparison.OrdinalIgnoreCase),
            $"the Crimean Bridge joins Crimea to Krasnodar ({Region(kerch?.From ?? 0)} / {Region(kerch?.To ?? 0)})");

        // --- determinism --------------------------------------------------------------
        var again = LandCrossings.Resolve(map, LandCrossingCatalog.All, defines);
        var second = RealMap();
        Check(again.Count == map.Crossings.Count &&
              again.Zip(map.Crossings).All(pair => pair.First.Id == pair.Second.Id && pair.First.From == pair.Second.From &&
                                                   pair.First.To == pair.Second.To) &&
              second.Crossings.Zip(map.Crossings).All(pair => pair.First.Id == pair.Second.Id &&
                  pair.First.From == pair.Second.From && pair.First.To == pair.Second.To) &&
              second.Crossings.Count == map.Crossings.Count,
            "the same data places the same crossings on the same provinces, twice and on a second map");
        var clone = map.CloneForOverrides();
        clone.AttachCrossings(Array.Empty<ResolvedCrossing>());
        Check(clone.Crossings.Count == 0 && map.Crossings.Count == authored.Count &&
              Array.IndexOf(clone.Neighbors[gibraltar.From], gibraltar.To) < 0 &&
              Array.IndexOf(map.Neighbors[gibraltar.From], gibraltar.To) >= 0 &&
              map.CloneForOverrides().Crossings.Count == authored.Count,
            "a clone carries the crossings and losing its own does not take the original's");

        // --- routes -------------------------------------------------------------------
        // Spain to Morocco: the corridor an offensive order would be drawn along, capital to
        // capital, over the two belligerents' ground alone.
        var madrid = esp.CapitalProvinceId;
        var rabat = mar.CapitalProvinceId;
        var toMorocco = map.FindOperationalRoute(madrid, rabat, (ushort)esp.MapIndex, (ushort)mar.MapIndex, controllers);
        Check(toMorocco.Length > 0 && UsesEdge(madrid, toMorocco, gibraltar.From, gibraltar.To),
            $"a land route runs from Madrid to Rabat over the Gibraltar ferry ({toMorocco.Length} provinces)");

        // Crimea to Krasnodar: from the far western end of the peninsula to the far eastern
        // end of the krai, on Russia's own ground.
        int Furthest(int region, bool west)
        {
            var best = -1;
            foreach (var province in map.ProvincesOfState(region))
            {
                if (map.IsWater(province) || controllers[province] != rus.MapIndex) continue;
                if (best < 0 || (west ? map.CenterOf(province).Longitude < map.CenterOf(best).Longitude
                                      : map.CenterOf(province).Longitude > map.CenterOf(best).Longitude)) best = province;
            }
            return best;
        }
        var crimeaWest = Furthest(map.ProvinceToState[kerch.From], west: true);
        var krasnodarEast = Furthest(map.ProvinceToState[kerch.To], west: false);
        var toKrasnodar = map.FindRoute(crimeaWest, krasnodarEast, (ushort)rus.MapIndex, controllers);
        Check(crimeaWest > 0 && krasnodarEast > 0 && toKrasnodar.Length > 0 &&
              UsesEdge(crimeaWest, toKrasnodar, kerch.From, kerch.To),
            $"a land route runs from western Crimea to eastern Krasnodar over the Crimean Bridge ({toKrasnodar.Length} provinces)");

        // --- the march ----------------------------------------------------------------
        c.Armies.Clear();
        c.Fleets.Clear();
        c.Defines.HoursPerTick = 24;
        var bridgePace = MarchAcross(c, map, rus, kerch, out var bridgeOpenPace, out var bridgeDays);
        Check(bridgePace > 0f && MathF.Abs(bridgePace / bridgeOpenPace - defines.CrossingFixedLinkMarch) < 0.001f,
            $"a division crosses the Crimean Bridge at {bridgePace / bridgeOpenPace:P0} of open march pace " +
            $"(CrossingFixedLinkMarch {defines.CrossingFixedLinkMarch:P0}) and arrives in {bridgeDays} days");

        // Morocco's shore under Spanish control, so the step is a march and not an invasion.
        Check(c.SetProvinceController(gibraltar.To, (ushort)esp.MapIndex), "the far shore can be held for the test");
        var ferryPace = MarchAcross(c, map, esp, gibraltar, out var ferryOpenPace, out var ferryDays);
        Check(ferryPace > 0f && MathF.Abs(ferryPace / ferryOpenPace - defines.CrossingFerryMarch) < 0.001f &&
              ferryDays >= (int)MathF.Ceiling(1f / ferryOpenPace) * 2,
            $"a division takes the Gibraltar ferry at {ferryPace / ferryOpenPace:P0} of open march pace " +
            $"(CrossingFerryMarch {defines.CrossingFerryMarch:P0}) and arrives in {ferryDays} days");
        Check(LandCrossings.MarchMultiplier(c, gibraltar.From, gibraltar.To) == defines.CrossingFerryMarch &&
              LandCrossings.MarchMultiplier(c, gibraltar.To, gibraltar.From) == defines.CrossingFerryMarch &&
              LandCrossings.MarchMultiplier(c, gibraltar.From, map.LandNeighbors[gibraltar.From][0]) == 1f,
            "the price is charged on the crossing step in either direction and on no other step");

        // --- supply -------------------------------------------------------------------
        var garrison = Division(7002, esp, map, gibraltar.To);
        c.Armies.Clear();
        c.Armies.Add(garrison);
        SetDay(c, 3);
        LogisticsSystem.Tick(c);
        // The land trace walks the crossing like any other edge, so the garrison's depot is a
        // Spanish region a few provinces up the road. (The same garrison without the crossing
        // is still fed, by sea: what the crossing changes is the road, not whether there is one.)
        var depot = c.StateByMapIndex(garrison.SupplySourceStateMapIndex);
        Check(!garrison.CutOff && garrison.SupplyDistance is >= 1 and <= 6 && depot is not null &&
              depot.NationMapIndex == esp.MapIndex,
            $"a garrison across the strait is supplied from Spain ({garrison.SupplyDistance} provinces from the depot in {depot?.Name})");

        // --- the assault --------------------------------------------------------------
        var overTheFerry = Division(7003, esp, map, gibraltar.From);
        var byLand = Division(7004, esp, map, map.LandNeighbors[gibraltar.To].First(p => !map.IsWater(p)));
        var mixed = LandCrossings.AssaultMultiplier(c, gibraltar.To, new[] { overTheFerry, byLand });
        // A weighted mean of one formation is that formation's figure to within rounding.
        static bool Near(float left, float right) => MathF.Abs(left - right) < 0.0001f;
        Check(Near(LandCrossings.AssaultMultiplier(c, gibraltar.To, new[] { overTheFerry }), defines.CrossingFerryAttack) &&
              Near(LandCrossings.AssaultMultiplier(c, gibraltar.To, new[] { byLand }), 1f) &&
              Near(mixed, (defines.CrossingFerryAttack + 1f) * 0.5f) &&
              LandCrossings.AttackMultiplier(c.Defines, kerch) == defines.CrossingFixedLinkAttack &&
              Near(LandCrossings.AssaultAversion(c, gibraltar.From, gibraltar.To), 1f - defines.CrossingFerryAttack) &&
              LandCrossings.AssaultAversion(c, gibraltar.From, map.LandNeighbors[gibraltar.From][0]) == 0f,
            $"an assault over the ferry keeps {defines.CrossingFerryAttack:P0} of its power, one by land keeps all, " +
            $"a mixed one {mixed:P0}; a bridge keeps {defines.CrossingFixedLinkAttack:P0}");

        // --- who commands the strait --------------------------------------------------
        var dza = c.NationByTag("DZA");
        c.Wars.Add(new StrategicWar { Id = 990_001, AttackerId = dza.Id, DefenderId = esp.Id });
        var squadron = new Fleet
        {
            Id = 990_001, NationId = dza.Id, Name = "Test squadron", Hulls = 40, Mission = FleetMission.Patrol,
            Latitude = gibraltar.SeaLatitude, Longitude = gibraltar.SeaLongitude
        };
        c.Fleets.Add(squadron);
        Check(!squadron.InPort && squadron.Presence >= defines.CrossingBlockingPresence &&
              LandCrossings.ClosedTo(c, gibraltar, esp.Id) && !LandCrossings.ClosedTo(c, gibraltar, mar.Id) &&
              LandCrossings.StepClosed(c, esp.Id, gibraltar.From, gibraltar.To),
            "an enemy squadron in the strait closes the ferry to the country it is at war with, and to nobody else");

        var waiting = Division(7005, esp, map, gibraltar.From);
        c.Armies.Clear();
        c.Armies.Add(waiting);
        var order = new MoveArmyCommand { IssuerId = esp.Id, ArmyId = waiting.Id, TargetProvinceId = gibraltar.To };
        order.Apply(c);
        for (var day = 0; day < 5; day++) MilitaryOperations.Tick(c);
        Check(order.Result == ArmyMoveResult.Issued && waiting.ProvinceId == gibraltar.From && waiting.MoveProgress == 0f &&
              waiting.Route.Count == 1 && ReferenceEquals(LandCrossings.WaitingAt(c, waiting), gibraltar) &&
              LandCrossings.WaitingSentence(gibraltar).Contains("Gibraltar"),
            "a division ordered over a closed ferry keeps its order and waits on the quay");

        var links = LandCrossings.Links(c, esp.Id, null);
        var drawn = links.First(link => link.Id == "gibraltar_ferry");
        Check(links.Count == map.Crossings.Count && drawn.Closed && drawn.Contested &&
              drawn.FromProvince == gibraltar.From && drawn.ToProvince == gibraltar.To &&
              drawn.FromLatitude == map.CenterOf(gibraltar.From).Latitude &&
              drawn.ToLongitude == map.CenterOf(gibraltar.To).Longitude &&
              links.Count(link => link.Closed) == 1 &&
              LandCrossings.Links(c, mar.Id, null).All(link => !link.Closed) &&
              LandCrossings.Describe(c, drawn, esp.Id).Contains("Gibraltar ferry") &&
              LandCrossings.Describe(c, drawn, esp.Id).Contains("Closed") &&
              !LandCrossings.Describe(c, drawn, mar.Id).Contains("Closed") &&
              LandCrossings.Legend().Length > 0,
            "the map's list shows the ferry closed to Spain, open to Morocco, between the two shores' centres");

        // A bridge under the same guns is marked and stays open.
        squadron.Latitude = kerch.SeaLatitude;
        squadron.Longitude = kerch.SeaLongitude;
        Check(LandCrossings.Contested(c, kerch, esp.Id) && !LandCrossings.ClosedTo(c, kerch, esp.Id) &&
              !LandCrossings.ClosedTo(c, gibraltar, esp.Id) &&
              LandCrossings.Describe(c, kerch, esp.Id).Contains("still open"),
            "a bridge an enemy fleet commands is marked and stays open; the ferry it left reopens");
        squadron.Latitude = gibraltar.SeaLatitude;
        squadron.Longitude = gibraltar.SeaLongitude;

        // Spain's own fleet of the same weight contests the strait, and the ferry sails again.
        var home = new Fleet
        {
            Id = 990_002, NationId = esp.Id, Name = "Home squadron", Hulls = 40, Mission = FleetMission.Patrol,
            Latitude = gibraltar.SeaLatitude, Longitude = gibraltar.SeaLongitude
        };
        c.Fleets.Add(home);
        Check(!LandCrossings.ClosedTo(c, gibraltar, esp.Id), "a strait the country's own fleet contests is not closed");
        c.Fleets.Remove(home);
        squadron.Hulls = 2;
        Check(!LandCrossings.ClosedTo(c, gibraltar, esp.Id), "a patrol boat is not a blockade");
        squadron.Hulls = 40;
        c.SetFeature("navy", false);
        Check(!LandCrossings.ClosedTo(c, gibraltar, esp.Id), "a game without navies closes nothing");
        c.SetFeature("navy", true);
        Check(LandCrossings.ClosedTo(c, gibraltar, esp.Id), "and with them back the squadron closes it again");

        // The squadron sails away, and the division that was waiting crosses without a new order.
        squadron.Longitude += 4f;
        var crossedIn = 0;
        while (waiting.ProvinceId != gibraltar.To && crossedIn < 40) { MilitaryOperations.Tick(c); crossedIn++; }
        Check(waiting.ProvinceId == gibraltar.To && crossedIn == ferryDays,
            $"once the strait is clear the waiting division crosses on its old order, in {crossedIn} days");
        c.Fleets.Remove(squadron);

        // --- mods: remove by key, add by key, and a bad entry named -------------------------
        var errors = new List<string>();
        using (var mod = JsonDocument.Parse("""
            {
              "gibraltar_ferry": { "open": false },
              "crimean_bridge": { "march": 0.5 },
              "mod_dover_ferry": { "name": "Dover ferry", "kind": "ferry", "from": [51.13, 1.25], "to": [50.93, 1.90] },
              "mod_nowhere": { "kind": "bridge", "from": [30.0, -45.0], "to": [30.2, -45.0] },
              "mod_same_shore": { "kind": "bridge", "from": [40.40, -3.70], "to": [40.41, -3.70] },
              "mod_bosphorus": { "kind": "bridge", "from": [41.05, 28.93], "to": [41.03, 29.12] },
              "mod_too_wide": { "kind": "ferry", "from": [51.13, 1.25], "to": [49.50, 0.10] },
              "mod_no_far_shore": { "kind": "ferry", "from": [51.13, 1.25] },
              "mod_bad_kind": { "kind": "zeppelin", "from": [51.13, 1.30], "to": [50.93, 1.95] },
              "mod_dover_twice": { "kind": "ferry", "from": [51.13, 1.30], "to": [50.93, 1.95] }
            }
            """, GameDataLoader.ForgivingJson))
            LandCrossingCatalog.Merge(mod.RootElement, errors);
        var modded = LandCrossings.Resolve(map, LandCrossingCatalog.All, defines, errors);
        map.AttachCrossings(modded);
        var revision = map.AdjacencyRevision;
        Check(map.Crossings.All(crossing => crossing.Id != "gibraltar_ferry") &&
              Array.IndexOf(map.Neighbors[gibraltar.From], gibraltar.To) < 0 &&
              map.CrossingBetween(gibraltar.From, gibraltar.To) is null &&
              map.FindOperationalRoute(madrid, rabat, (ushort)esp.MapIndex, (ushort)mar.MapIndex, controllers).Length == 0,
            "a mod's \"open\": false takes one crossing and its edge away, and Madrid no longer reaches Rabat by land");
        var dover = map.Crossings.FirstOrDefault(crossing => crossing.Id == "mod_dover_ferry");
        Check(dover is not null && Array.IndexOf(map.Neighbors[dover.From], dover.To) >= 0 &&
              map.Crossings.First(crossing => crossing.Id == "crimean_bridge").March == 0.5f &&
              LandCrossings.MarchMultiplier(c, kerch.From, kerch.To) == 0.5f,
            "a mod adds a crossing with a new key and re-prices one by naming it again");
        Check(errors.Any(e => e.Contains("mod_nowhere") && e.Contains("no land within")) &&
              errors.Any(e => e.Contains("mod_same_shore") && e.Contains("both shores are province")) &&
              errors.Any(e => e.Contains("mod_bosphorus")) &&
              errors.Any(e => e.Contains("mod_too_wide") && e.Contains("CrossingMaxSpanKm")) &&
              errors.Any(e => e.Contains("mod_no_far_shore") && e.Contains("\"from\" and \"to\"")) &&
              errors.Any(e => e.Contains("mod_bad_kind") && e.Contains("kind")) &&
              errors.Any(e => e.Contains("mod_dover_twice") && e.Contains("already joined")) && errors.Count == 7 &&
              map.Crossings.Count(crossing => crossing.Id.StartsWith("mod_", StringComparison.Ordinal)) == 1,
            "every entry that cannot be placed is refused by name and the rest still load: " + string.Join(" | ", errors));

        // --- no data, no edges --------------------------------------------------------
        LandCrossingCatalog.Clear();
        map.AttachCrossings(LandCrossings.Resolve(map, LandCrossingCatalog.All, defines));
        var bare = true;
        for (var province = 1; province <= map.ProvinceCount; province++)
            bare &= ReferenceEquals(map.Neighbors[province], map.LandNeighbors[province]) && !map.IsCrossingEnd(province);
        var roundTheSea = map.FindRoute(crimeaWest, krasnodarEast, (ushort)rus.MapIndex, controllers);
        Check(bare && map.Crossings.Count == 0 && map.AdjacencyRevision > revision &&
              LandCrossings.MarchMultiplier(c, kerch.From, kerch.To) == 1f &&
              !UsesEdge(crimeaWest, roundTheSea, kerch.From, kerch.To) &&
              (roundTheSea.Length == 0 || roundTheSea.Length > toKrasnodar.Length),
            $"with no crossings in the data the graph is the drawn borders again " +
            $"(Crimea to Krasnodar: {(roundTheSea.Length == 0 ? "no route" : roundTheSea.Length + " provinces round the Sea of Azov")} " +
            $"against {toKrasnodar.Length} over the bridge)");
        // --- and the file puts them back ----------------------------------------------------
        var restored = LandCrossings.Attach(map);
        Check(restored == authored.Count && LandCrossings.LastErrors.Count == 0 &&
              Array.IndexOf(map.Neighbors[gibraltar.From], gibraltar.To) >= 0 &&
              map.Crossings.Zip(again).All(pair => pair.First.Id == pair.Second.Id && pair.First.From == pair.Second.From &&
                                                   pair.First.To == pair.Second.To),
            "reading the folder again restores exactly the shipped crossings");
    }

    /// <summary>
    /// March one division over a crossing by a real order, a day a tick. Returns the progress
    /// its first day made; <paramref name="openPace"/> is the same day's with the crossing
    /// priced at nothing, and <paramref name="days"/> how long the whole crossing took.
    /// </summary>
    static float MarchAcross(Campaign c, ProvinceTopology map, Nation nation, ResolvedCrossing crossing,
        out float openPace, out int days)
    {
        float FirstDay()
        {
            c.Armies.Clear();
            var army = Division(7001, nation, map, crossing.From);
            c.Armies.Add(army);
            var order = new MoveArmyCommand { IssuerId = nation.Id, ArmyId = army.Id, TargetProvinceId = crossing.To };
            order.Apply(c);
            if (order.Result != ArmyMoveResult.Issued || army.Route.Count != 1 || army.Route[0] != crossing.To)
                throw new Exception($"crossings: the order over {crossing.Id} was {order.Result} with a route of {army.Route.Count}");
            MilitaryOperations.Tick(c);
            return army.MoveProgress;
        }

        var ferry = c.Defines.CrossingFerryMarch;
        var link = c.Defines.CrossingFixedLinkMarch;
        var causeway = c.Defines.CrossingCausewayMarch;
        c.Defines.CrossingFerryMarch = c.Defines.CrossingFixedLinkMarch = c.Defines.CrossingCausewayMarch = 1f;
        openPace = FirstDay();
        c.Defines.CrossingFerryMarch = ferry;
        c.Defines.CrossingFixedLinkMarch = link;
        c.Defines.CrossingCausewayMarch = causeway;

        var pace = FirstDay();
        var marching = c.Armies[0];
        days = 1;
        while (marching.ProvinceId != crossing.To && days < 60) { MilitaryOperations.Tick(c); days++; }
        if (marching.ProvinceId != crossing.To) throw new Exception($"crossings: the division never crossed {crossing.Id}");
        return pace;
    }

    /// <summary>
    /// The table a map author reads after editing map_data/crossings: where each entry
    /// landed, whose ground it joins, and how far round the two shores are by land.
    /// </summary>
    public static void Survey()
    {
        var campaign = Campaign.CreateBootstrap(20241113);
        var map = RealMap();
        campaign.AttachProvinceTopology(map);
        string Owner(int province) => campaign.NationByMapIndex(map.ProvinceToNation[province])?.Tag ?? "---";
        string Region(int province) => campaign.StateByMapIndex(map.ProvinceToState[province])?.Name ?? "?";
        Console.WriteLine($"CROSSING_SURVEY authored={LandCrossingCatalog.All.Count} attached={map.Crossings.Count} " +
                          $"rejected={LandCrossings.LastErrors.Count}");
        foreach (var crossing in map.Crossings)
        {
            var hops = LandHops(map, crossing.From, crossing.To, 12);
            var a = map.CenterOf(crossing.From);
            var b = map.CenterOf(crossing.To);
            Console.WriteLine($"CROSSING {crossing.Id,-24} {crossing.Kind,-8} {crossing.SpanKm,5:0} km  " +
                              $"{crossing.From,5} {Owner(crossing.From)} {Region(crossing.From)} [{a.Latitude:0.00}, {a.Longitude:0.00}]  <->  " +
                              $"{crossing.To,5} {Owner(crossing.To)} {Region(crossing.To)} [{b.Latitude:0.00}, {b.Longitude:0.00}]  " +
                              $"by land: {(hops < 0 ? "more than 12 provinces" : hops + " provinces")}");
        }
        foreach (var error in LandCrossings.LastErrors) Console.WriteLine("CROSSING_REJECTED " + error);
    }
}
