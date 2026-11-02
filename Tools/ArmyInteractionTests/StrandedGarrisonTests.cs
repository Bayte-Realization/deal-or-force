using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The garrison a rising strands, and the pocket it is in afterwards (StrandedGarrisons).
///
/// --stranded-garrison-only: a French region rises with a garrison of mixed formations on it and
/// France fights. Some go over, some go home, some dig in as a pocket that holds its ground; and
/// for the first weeks nobody on that ground simply stands where it was and starves. An island
/// region (Corsica) is the case with no road home: those who leave go by sea.
///
/// --pocket-stores-only: a Russian formation holding a province deep inside Ukraine. It lives off
/// the region's stores before it starves; more roads and longer on the ground mean more stores;
/// an air bridge stretches them; with the stores gone and nobody coming it surrenders and the
/// ground goes back; a pocket the computer runs breaks out toward friendly ground when its
/// stores run low, and holds while relief is fighting its way toward it.
/// </summary>
static class StrandedGarrisonTests
{
    static int failures;

    static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + "stranded garrisons: " + label);
        if (!ok) failures++;
    }

    static void SetDay(Campaign c, int day) =>
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)day * 24);

    static void Place(Army army, int province, ProvinceTopology map)
    {
        army.ProvinceId = province;
        army.StateMapIndex = map.ProvinceToState[province];
        (army.Latitude, army.Longitude) = map.CenterOf(province);
        army.Route.Clear(); army.PlannedRoute.Clear(); army.MoveProgress = 0; army.BattleId = -1;
        army.EmbarkedFleetId = -1;
    }

    // ------------------------------------------------------------------ A: a rising with a garrison

    public static void RunRising(ProvinceTopology map)
    {
        failures = 0;
        Rising(map, island: false);
        Rising(map, island: true);
        if (failures > 0) throw new Exception($"{failures} stranded-garrison checks failed");
    }

    static void Rising(ProvinceTopology map, bool island)
    {
        var c = Campaign.CreateBootstrap(20261102);
        c.Defines.HoursPerTick = 24;
        c.AttachProvinceTopology(map);
        c.Wars.Clear(); c.Battles.Clear(); c.MarkWarsChanged();
        // An island region of a country with a mainland: France, else the first that has one.
        var parent = island
            ? new[] { "FRA", "ITA", "GRC", "ESP", "GBR", "JPN", "IDN" }.Select(c.NationByTag)
                .FirstOrDefault(n => n is not null && c.StatesOf(n).Any(s => IsIsland(map, s.MapIndex)))
            : c.NationByTag("FRA");
        if (parent is null) { Console.WriteLine("SKIP stranded garrisons: no island region found"); return; }
        foreach (var nation in c.Nations) { nation.IsHuman = false; nation.IsPlayer = false; }
        c.ObserverMode = true;
        parent.Stability = .9f; parent.WarSupport = 1f; parent.Treasury = MathF.Max(parent.Treasury, 200f);
        var acceptance = SecessionCore.Weights.Acceptance;
        SecessionCore.Weights.Acceptance = -10; // France fights: the war is what strands the garrison.
        try
        {
            var region = island
                ? c.StatesOf(parent).Where(s => IsIsland(map, s.MapIndex) && s.MapIndex != parent.CapitalStateMapIndex)
                    .OrderByDescending(s => s.Population).FirstOrDefault()
                : c.StatesOf(parent).Where(s => s.Population > 300000 && s.MapIndex != parent.CapitalStateMapIndex)
                    .Where(s => ProvincesOf(map, s.MapIndex).Count(p => !map.IsWater(p)) >= 3)
                    // On the mainland: its land touches another French region's.
                    .Where(s => ProvincesOf(map, s.MapIndex).Any(p => !map.IsWater(p) && map.Neighbors[p].Any(n =>
                        n > 0 && !map.IsWater(n) && map.ProvinceToState[n] != s.MapIndex &&
                        map.ProvinceToNation[n] == parent.MapIndex)))
                    .OrderByDescending(s => ProvincesOf(map, s.MapIndex).Count(p => !map.IsWater(p)))
                    .ThenBy(s => s.MapIndex).FirstOrDefault();
            if (region is null)
            {
                Console.WriteLine($"SKIP stranded garrisons: no {(island ? "island" : "mainland")} region found");
                return;
            }
            var ground = ProvincesOf(map, region.MapIndex).Where(p => !map.IsWater(p)).ToList();
            // The garrison: sixteen formations of the country's own, half of them cohesive veterans and
            // half of them shaken, spread over the region's provinces.
            var pool = c.Armies.Where(a => a.NationId == parent.Id && a.EmbarkedFleetId < 0 && !a.InBattle)
                .OrderBy(a => a.Id).Take(16).ToList();
            Check(pool.Count >= 6, $"France has formations to garrison {region.Name} with ({pool.Count})");
            for (var i = 0; i < pool.Count; i++)
            {
                var army = pool[i];
                Place(army, ground[i % ground.Count], map);
                army.Divisions = 1f; army.Strength = 1f; army.Organisation = 1f; army.SupplyLevel = 1f;
                army.CutOff = false; army.DaysCutOff = 0; army.ReadyDay = 0;
                army.Cohesion = i % 2 == 0 ? 0.95f : 0.45f;
                army.Experience = i % 2 == 0 ? 0.8f : 0.1f;
                army.Entrenchment = 0.4f;
                army.OrderKind = ArmyOrder.Manual; army.DirectOrder = false; army.AssignedWarId = -1; army.FrontLineId = -1;
            }
            var ids = pool.Select(a => a.Id).ToHashSet();
            var tally = new Dictionary<string, int>(StrandedGarrisons.Tally);

            var report = SecessionEvents.Force(c, region);
            Check(report?.Seceded is not null && report.Outcome == SecessionOutcome.Contested,
                $"{region.Name} rises and France fights ({report?.Outcome})");
            if (report?.Seceded is null) return;
            var born = report.Seceded;
            Console.WriteLine($"  makeup: kin {StrandedGarrisons.Kin(c, parent, born, ground[0]):0.00}, army disloyalty " +
                              $"{StateFailure.ArmyDisloyalty(c, parent):0.00}, region stability {region.Stability:0.00} radicalism {region.Radicalism:0.00}, " +
                              $"rising peoples {string.Join("/", born.FoundedCultures)}");
            var defected = pool.Count(a => a.NationId == born.Id);
            var left = pool.Where(a => a.NationId == parent.Id).ToList();
            var marched = left.Count(a => a.BreakingOut);
            var shipped = left.Count(a => !ground.Contains(a.ProvinceId));
            var held = left.Where(a => a.HoldoutSinceDay >= 0).ToList();
            Console.WriteLine($"  {region.Name}: {pool.Count} formations -> defected {defected}, marched {marched}, " +
                              $"shipped {shipped}, holding {held.Count}; tally " +
                              string.Join(" ", StrandedGarrisons.Tally.Select(p => $"{p.Key}={p.Value - tally.GetValueOrDefault(p.Key)}")));
            Check(defected > 0, "part of the garrison goes over to the rising");
            Check(marched + shipped > 0, "part of it leaves for home");
            if (island) Check(marched == 0 && shipped > 0, "from an island those who leave go by sea, not on foot");
            Check(held.Count > 0, "part of it digs in as a pocket");
            Check(held.All(a => a.PocketStores > 0f && a.CutOff), "a holdout starts with the region's stores counted");
            Check(held.Any(a => c.ProvinceController[a.ProvinceId] == parent.MapIndex),
                "a holdout holds its province for France again");
            Check(left.All(a => a.BreakingOut || a.HoldoutSinceDay >= 0 || !ground.Contains(a.ProvinceId)),
                "no loyal formation is simply left standing on the rising's ground");
            var shippedArmy = left.FirstOrDefault(a => !ground.Contains(a.ProvinceId));
            if (shippedArmy is not null)
                Check(shippedArmy.Equipment < 0.9f && shippedArmy.ReadyDay > c.ElapsedDays,
                    "a formation taken out by sea or air loses equipment and needs time to come back");

            // Three weeks of the world. Nobody of that garrison simply starves where it stands.
            var worst = 0;
            c.IsPaused = false;
            var startDay = c.ElapsedDays;
            for (var day = 0; day < 21; day++)
            {
                c.TickDay();
                foreach (var army in c.Armies)
                    if (ids.Contains(army.Id) && army.NationId == parent.Id && army.CutOff)
                        worst = Math.Max(worst, army.DaysCutOff);
            }
            Check(c.ElapsedDays >= startDay + 20, $"the world really ran three weeks (day {startDay} to {c.ElapsedDays})");
            var alive = c.Armies.Where(a => ids.Contains(a.Id) && a.NationId == parent.Id).ToList();
            var stillHolding = alive.Where(a => a.HoldoutSinceDay >= 0).ToList();
            Console.WriteLine($"  after 21 days: {alive.Count} of France's {left.Count} alive, {stillHolding.Count} holding " +
                              $"(stores {string.Join("/", stillHolding.Select(a => a.PocketStores.ToString("0.0")))}), " +
                              $"relief {string.Join("/", stillHolding.Select(a => a.ReliefDistance))}, worst hunger {worst} days");
            foreach (var army in stillHolding.Take(2))
                Console.WriteLine("    card: " + ArmyVocabulary.Situation(c, army));
            foreach (var army in stillHolding)
                Console.WriteLine($"    holdout {army.Id} at #{army.ProvinceId}: held by {(c.ProvinceController[army.ProvinceId] == parent.MapIndex ? parent.Tag : "the rising")}, " +
                                  $"cut off {army.CutOff}, way home {PocketBreakout.WayHome(c, parent, army.ProvinceId)?.Count - 1 ?? -1}, " +
                                  $"strength {army.Strength:0.00}, in battle {army.InBattle}");
            Check(worst <= 7, "in the first three weeks no formation of that garrison has starved for more than a week");
            Check(alive.All(a => a.Strength > 0.3f || a.InBattle),
                "and none of the survivors has wasted away standing still");
        }
        finally
        {
            SecessionCore.Weights.Acceptance = acceptance;
        }
    }

    /// <summary>A region whose land touches no other region's land: no road leads out of it.</summary>
    static bool IsIsland(ProvinceTopology map, int stateMapIndex)
    {
        var land = ProvincesOf(map, stateMapIndex).Where(p => !map.IsWater(p)).ToList();
        return land.Count >= 2 && land.All(p => map.Neighbors[p].All(n =>
            n <= 0 || map.IsWater(n) || map.ProvinceToState[n] == stateMapIndex));
    }

    static List<int> ProvincesOf(ProvinceTopology map, int stateMapIndex) =>
        Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToState[p] == stateMapIndex).ToList();

    // ------------------------------------------------------------------ B: stores, air, surrender, breakout

    public static void RunPocket(ProvinceTopology map)
    {
        failures = 0;
        var c = Campaign.CreateBootstrap(20260906);
        c.Defines.HoursPerTick = 24;
        c.AttachProvinceTopology(map);
        var rus = c.NationByTag("RUS");
        var ukr = c.NationByTag("UKR");
        foreach (var nation in c.Nations) { nation.IsHuman = false; nation.IsPlayer = false; }
        c.ObserverMode = true;
        var war = c.Wars.First(w => !w.Ended && w.Involves(rus.Id) && w.Involves(ukr.Id));
        c.Wars.RemoveAll(w => w != war);
        c.MarkWarsChanged();
        c.Battles.Clear();
        // Russia's own air wings would feed the pocket from the first day; the air bridge is
        // measured on its own below.
        c.SupportFormations.RemoveAll(f => f.NationId == rus.Id);
        SupportCore.IndexReach(c);
        var ctrl = c.ProvinceController;

        // The Ukrainian province furthest from anything Russia holds, inland and not water.
        var depth = new Dictionary<int, int>();
        var frontier = new Queue<int>();
        for (var p = 1; p <= map.ProvinceCount; p++)
            if (ctrl[p] == rus.MapIndex && !map.IsWater(p)) { depth[p] = 0; frontier.Enqueue(p); }
        while (frontier.Count > 0)
        {
            var here = frontier.Dequeue();
            foreach (var next in map.Neighbors[here])
                if (next > 0 && !map.IsWater(next) && !depth.ContainsKey(next) && ctrl[next] == ukr.MapIndex)
                { depth[next] = depth[here] + 1; frontier.Enqueue(next); }
        }
        bool Inland(int p) => !SeaLanes.TryHarbourPoint(map, p, out _, out _);
        var deep = depth.Where(p => p.Value >= 4 && Inland(p.Key) && map.Neighbors[p.Key].Length >= 3)
            .OrderByDescending(p => p.Value).ThenBy(p => p.Key).Select(p => p.Key).FirstOrDefault();
        var near = depth.Where(p => p.Value is >= 2 and <= 4 && Inland(p.Key) && map.Neighbors[p.Key].Length >= 3 &&
                                    map.Neighbors[p.Key].All(n => ctrl[n] == ukr.MapIndex))
            .OrderBy(p => p.Key).Select(p => p.Key).FirstOrDefault();
        Check(deep > 0 && near > 0, $"a deep Ukrainian province #{deep} (depth {depth.GetValueOrDefault(deep)}) and a near one #{near}");
        if (deep <= 0 || near <= 0) throw new Exception("no pocket provinces");

        var template = c.Armies.First(a => a.NationId == rus.Id);
        var nextId = c.Armies.Max(a => a.Id) + 1;
        Army Make(int province)
        {
            var army = new Army
            {
                Id = nextId++, NationId = rus.Id, Name = "Pocket " + nextId, TemplateId = template.TemplateId,
                Divisions = 1f, Strength = 1f, Organisation = 1f, SupplyLevel = 1f, Personnel = 15000,
                MaxPersonnel = 15000, Cohesion = 0.7f, Equipment = 0.9f, ArmyGroupId = -1
            };
            Place(army, province, map);
            c.Armies.Add(army);
            return army;
        }

        // --- stores: counted on the day the ring closes, from the region and the time on the ground
        c.SetProvinceController(deep, (ushort)rus.MapIndex);
        var held = Make(deep);
        held.Entrenchment = 1f;
        var lost = Make(map.Neighbors[deep].First(n => ctrl[n] == ukr.MapIndex && !map.IsWater(n)));
        SetDay(c, 0);
        LogisticsSystem.Tick(c);
        Check(held.CutOff && lost.CutOff, "both formations deep in Ukraine are cut off");
        Console.WriteLine($"  stores: holding its ground {held.PocketStores:0.0} days, on enemy ground {lost.PocketStores:0.0} days");
        Check(held.PocketStores > lost.PocketStores && lost.PocketStores <= c.Defines.PocketStoresCarriedDays + 0.01f,
            "a formation on ground it holds has the region's stores; one on enemy ground only what it carries");
        var region = c.States.First(s => s.MapIndex == map.ProvinceToState[deep]);
        held.Entrenchment = 0f;
        var fresh = StrandedGarrisons.AssessStores(c, held);
        held.Entrenchment = 1f;
        var dug = StrandedGarrisons.AssessStores(c, held);
        region.Buildings[BuildingKind.Infrastructure] = region.Buildings.GetValueOrDefault(BuildingKind.Infrastructure) + 6;
        var roads = StrandedGarrisons.AssessStores(c, held);
        region.Buildings[BuildingKind.Infrastructure] -= 6;
        Console.WriteLine($"  stores by time on the ground: fresh {fresh:0.0}, dug in {dug:0.0}; with six more roads {roads:0.0}");
        Check(dug > fresh, "longer on the ground means more stockpiled");
        Check(roads > dug, "more infrastructure in the region means more stockpiled");
        c.Armies.Remove(lost);

        // --- living off the stores: no hunger and no wasting while they last
        var stores = held.PocketStores;
        var days = 0;
        var strengthBefore = held.Strength;
        var hungryWhileStored = false;
        while (held.PocketStores > 0f && days < 400)
        {
            SetDay(c, ++days);
            LogisticsSystem.Tick(c);
            Attrition.Tick(c);
            if (held.PocketStores > 0f && (held.DaysCutOff > 0 || held.Strength < strengthBefore - 0.0001f)) hungryWhileStored = true;
        }
        Console.WriteLine($"  {stores:0.0} days of stores lasted {days} days; supply on rations {held.SupplyLevel:0.00}");
        Check(!hungryWhileStored, "while the stores last the formation neither goes hungry nor wastes away");
        Check(days >= (int)stores - 1 && held.PocketStores == 0f, "the stores last about as long as they were counted");
        for (var extra = 0; extra < 4; extra++) { SetDay(c, ++days); LogisticsSystem.Tick(c); Attrition.Tick(c); }
        Check(held.DaysCutOff > 0 && held.Strength < strengthBefore, "once they are gone it starts to starve");

        // --- an air bridge stretches them
        held.PocketStores = 10f; held.DaysCutOff = 0;
        var without = StrandedGarrisons.DailyDraw(c, held);
        var (wing, wingType) = AirWing(c, rus, deep);
        if (wing is not null)
        {
            c.SupportFormations.Add(wing);
            SupportCore.IndexReach(c);
            var with = StrandedGarrisons.DailyDraw(c, held);
            Console.WriteLine($"  daily draw without an air bridge {without:0.00}, with a {wingType} in range {with:0.00}; " +
                              $"tooltip: {LogisticsSystem.DescribeSupply(c, held)}");
            Check(with < without, "an air wing in range carries part of the pocket's needs");
            c.SupportFormations.Remove(wing);
            SupportCore.IndexReach(c);
        }
        else Check(false, "an air formation type with the reach to fly to the pocket exists");

        // --- with the stores gone and nobody coming, it surrenders and the ground goes back
        held.PocketStores = 0f; held.DaysCutOff = c.Defines.PocketSurrenderHungryDays; held.ReliefDistance = -1;
        held.Strength = 0.8f;
        var surrendered = WarLosses.SurrenderedFormations;
        var waited = 0;
        while (c.Armies.Contains(held) && waited < 60)
        {
            SetDay(c, ++days); waited++;
            held.DaysCutOff++;
            StrandedGarrisons.Tick(c);
        }
        Console.WriteLine($"  out of stores: surrendered after {waited} more days");
        Check(!c.Armies.Contains(held) && WarLosses.SurrenderedFormations == surrendered + 1,
            "a pocket out of stores with nobody coming surrenders");
        Check(ctrl[deep] == ukr.MapIndex, "and the province it held goes back to Ukraine");

        // --- breakout toward friends, and holding while relief is on its way. Russia's own
        // formations at the front are taken off so that "relief is coming" is the case's to set.
        c.Armies.RemoveAll(a => a.NationId == rus.Id);
        c.SetProvinceController(near, (ushort)rus.MapIndex);
        var pocket = Make(near);
        SetDay(c, 300);
        LogisticsSystem.Tick(c);
        Check(pocket.CutOff, "a formation two or three provinces inside Ukraine is cut off");
        pocket.PocketStores = 20f;
        PocketBreakout.Tick(c);
        Console.WriteLine($"  near pocket #{near}: relief {pocket.ReliefDistance} provinces; card: {ArmyVocabulary.Situation(c, pocket)}");
        Check(pocket.ReliefDistance > 0, "the breakout staff measures the way to friendly ground");
        Check(pocket.BreakingOut, "with stores but nobody coming toward it, a computer-run pocket breaks out at once");

        pocket.BreakingOut = false; Place(pocket, near, map); pocket.DirectOrder = false; pocket.OrderKind = ArmyOrder.Manual;
        var way = PocketBreakout.WayHome(c, rus, near);
        var relief = Make(way[^1]);
        relief.Route.Add(way[^2]); // marching into the ring
        PocketBreakout.Tick(c);
        Check(!pocket.BreakingOut, "with stores and relief marching toward it, the pocket holds");
        pocket.PocketStores = c.Defines.PocketBreakoutStoresDays - 1f;
        PocketBreakout.Tick(c);
        Check(pocket.BreakingOut && pocket.PlannedRoute.Count + pocket.Route.Count > 0,
            "when the stores run low it breaks out toward friendly ground anyway");
        c.Armies.Remove(relief);

        pocket.BreakingOut = false; Place(pocket, near, map); pocket.PocketStores = 20f;
        pocket.HoldoutSinceDay = c.ElapsedDays; pocket.DirectOrder = true;
        PocketBreakout.Tick(c);
        Check(!pocket.BreakingOut, "a garrison holding out behind the lines stays while it has stores");
        c.Armies.Remove(pocket);

        // --- a long road is not a pocket: past the trace range on its own connected ground a
        // formation is thinly supplied, never cut off (the far-north fronts that sat worn out)
        var range = c.Defines.SupplyTraceRange;
        c.Defines.SupplyTraceRange = 0;
        // A province of the capital's own region other than the one its depot stands on: on the
        // road network for certain, and with the range at nothing, past it.
        var far = Enumerable.Range(1, map.ProvinceCount).Last(p => !map.IsWater(p) && ctrl[p] == rus.MapIndex &&
            map.ProvinceToState[p] == rus.CapitalStateMapIndex);
        var remote = Make(far);
        SetDay(c, 303);
        LogisticsSystem.Tick(c);
        Console.WriteLine($"  past the trace range at #{far}: cut off {remote.CutOff}, {remote.SupplyDistance} provinces from its hub, supply {remote.SupplyLevel:0.00}");
        Check(!remote.CutOff && remote.SupplyDistance > 0, "a formation past the trace range on its own connected ground is not cut off");
        c.Defines.SupplyTraceRange = range;
        c.Armies.Remove(remote);

        // --- a forward depot in an enemy region held with the road open
        var front = c.States.Where(s => s.NationMapIndex == ukr.MapIndex).Select(s => s.MapIndex)
            .FirstOrDefault(state => Enumerable.Range(1, map.ProvinceCount).Any(p => map.ProvinceToState[p] == state && !map.IsWater(p) &&
                map.Neighbors[p].Any(n => n > 0 && ctrl[n] == rus.MapIndex && map.ProvinceToNation[n] == rus.MapIndex)));
        Check(front > 0, "a Ukrainian region on the Russian border");
        var taken = Enumerable.Range(1, map.ProvinceCount).Where(p => map.ProvinceToState[p] == front && !map.IsWater(p)).ToList();
        foreach (var p in taken) c.SetProvinceController(p, (ushort)rus.MapIndex);
        var occupier = Make(taken[^1]);
        SetDay(c, 306);
        LogisticsSystem.Tick(c);
        Console.WriteLine($"  held Ukrainian region {front} ({taken.Count} provinces): fed from region {occupier.SupplySourceStateMapIndex}, " +
                          $"{occupier.SupplyDistance} provinces along the road");
        Check(!occupier.CutOff && occupier.SupplySourceStateMapIndex == front, "a region held with the road open keeps a forward depot");
        var share = c.Defines.ForwardDepotHeldShare;
        c.Defines.ForwardDepotHeldShare = 2f;
        SetDay(c, 309);
        LogisticsSystem.Tick(c);
        Check(occupier.SupplySourceStateMapIndex != front, "with forward depots turned off it is fed from home");
        c.Defines.ForwardDepotHeldShare = share;

        if (failures > 0) throw new Exception($"{failures} pocket checks failed");
    }

    /// <summary>An air formation of the country, based in the province it holds nearest the pocket, that reaches it.</summary>
    static (SupportFormation, string) AirWing(Campaign c, Nation nation, int pocket)
    {
        var map = c.ProvinceMap;
        var bases = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => c.ProvinceController[p] == nation.MapIndex && map.ProvinceToNation[p] == nation.MapIndex && !map.IsWater(p))
            .OrderBy(p => SupportCore.DistanceKm(c, p, pocket)).ToList();
        if (bases.Count == 0) return (null, "");
        var km = SupportCore.DistanceKm(c, bases[0], pocket);
        var type = SupportTypeCatalog.All.Where(t => t.Kind == SupportKind.Air && t.RangeKm >= km && SupportCore.Enabled(c, t))
            .OrderBy(t => t.RangeKm).FirstOrDefault();
        if (type is null) return (null, "");
        return (new SupportFormation
        {
            Id = (c.SupportFormations.Count == 0 ? 0 : c.SupportFormations.Max(f => f.Id)) + 1,
            NationId = nation.Id, TypeId = type.Id, StateMapIndex = map.ProvinceToState[bases[0]],
            BaseProvinceId = bases[0], Strength = 1f
        }, type.Id);
    }
}
