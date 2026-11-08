using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>
/// The deep-strike war (LongRangeStrikes): a salvo needs munitions, chooses a target behind the
/// line, is thinned by the air defence that reaches it, saturates a thin screen, lands harder
/// under reconnaissance, and does to each kind of target what that kind is for. The AI flies its
/// formations by role and buys air defence and a strike arm while at war. Run with
/// --long-range-only.
/// </summary>
static class LongRangeTests
{
    static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAIL long-range: " + name);
        Console.WriteLine("PASS long-range: " + name);
    }

    public static void Run(Campaign c)
    {
        SupportTypeCatalog.ResetCatalog();
        var d = c.Defines;

        // ---- the catalogue
        Check(SupportTypeCatalog.TryGet("strike_missile_brigade", out var missiles) && missiles.Strike > 0f &&
              missiles.Munition == "Missiles" && missiles.Missions.Contains(SupportCore.MissionStrike),
            "the strike missile brigade fires missiles on the strike mission");
        Check(SupportTypeCatalog.TryGet("long_range_drone_group", out var shaheds) && shaheds.Munition == "Drones" &&
              shaheds.Evasion < missiles.Evasion, "one-way drones are easier to shoot down than missiles");
        Check(SupportTypeCatalog.Get("sam_brigade").Strike == 0f && !SupportTypeCatalog.Get("sam_brigade").Missions.Contains(SupportCore.MissionStrike),
            "air defence cannot strike");
        using (var mod = JsonDocument.Parse("{ \"strike_missile_brigade\": { \"strike\": 12 } }"))
            SupportTypeCatalog.Merge(mod.RootElement);
        var modded = SupportTypeCatalog.Get("strike_missile_brigade");
        Check(modded.Strike == 12f && modded.Munition == "Missiles" && modded.Evasion == missiles.Evasion,
            "a mod that names only the strike keeps the munition and the evasion");
        SupportTypeCatalog.ResetCatalog();
        missiles = SupportTypeCatalog.Get("strike_missile_brigade");

        // ---- old saves
        var options = new JsonSerializerOptions { IncludeFields = true };
        var legacy = JsonSerializer.Deserialize<SupportFormation>(
            "{\"Id\":5,\"NationId\":1,\"TypeId\":\"mlrs_battalion\",\"Mission\":\"interdiction\"}", options);
        Check(legacy.StrikeTarget == "" && legacy.TargetStateMapIndex == -1,
            "a formation saved before strikes loads with the automatic target");

        // ---- the fixture: Russia strikes Ukraine from Belgorod.
        var ukraine = c.NationByTag("UKR");
        var russia = c.NationByTag("RUS");
        var belgorod = c.States.First(state => state.Id == "ru_bel");
        var kharkiv = c.States.First(state => state.Id == "ua_63");
        var russianBase = SupportCore.BaseProvinceOf(c, russia, belgorod.MapIndex);
        var ukrainianBase = SupportCore.BaseProvinceOf(c, ukraine, kharkiv.MapIndex);
        Check(russianBase > 0 && ukrainianBase > 0, "Belgorod and Kharkiv have bases");
        var addedWar = !c.Wars.Any(w => !w.Ended && w.Involves(russia.Id) && w.Involves(ukraine.Id));
        var war = addedWar ? new StrategicWar { Id = 99071, AttackerId = russia.Id, DefenderId = ukraine.Id } : null;
        if (addedWar) c.Wars.Add(war);

        var saved = c.SupportFormations.ToList();
        var savedMissions = saved.ToDictionary(f => f.Id, f => f.Mission);
        var savedStocks = new[] { russia, ukraine }.ToDictionary(n => n.Id, n => new Dictionary<string, float>(n.Stockpiles));
        var savedStability = ukraine.Stability;
        var savedDamage = c.States.ToDictionary(s => s.MapIndex, s => (s.WarDamage, s.StandardOfLiving));
        var savedArmies = c.Armies.ToDictionary(a => a.Id, a => (a.Organisation, a.Equipment));
        var savedOrders = c.SupportOrders.ToList();
        var savedEvery = d.AiLongRangeProcurementDays;
        var savedTreasury = c.Nations.ToDictionary(n => n.Id, n => (n.Treasury, n.ManpowerReserve, n.IsHuman));
        try
        {
            // Only this test's formations exist.
            c.SupportFormations.Clear();
            var brigade = Formation(c, 1, russia, belgorod, russianBase, "strike_missile_brigade");
            c.SupportFormations.Add(brigade);
            SupportCore.IndexReach(c);
            StrategicStockpiles.EnsureSeeded(c, russia);
            StrategicStockpiles.EnsureSeeded(c, ukraine);
            russia.Stockpiles["Missiles"] = 50f;
            ukraine.Stockpiles["Missiles"] = 50f;
            ukraine.Stockpiles["Ammunition"] = 80f;
            ukraine.Stockpiles["Oil"] = 80f;
            ukraine.Stockpiles["Arms"] = 80f;

            // A preview is a promise, not a shot.
            var plan = LongRangeStrikes.Preview(c, brigade);
            Check(plan.Refusal == "" && plan.Target == LongRangeStrikes.TargetLogistics && plan.TargetNationId == ukraine.Id &&
                  plan.DistanceKm <= missiles.RangeKm, $"missiles look for the depots that feed the enemy line ({plan.Target} {plan.TargetNationId} {plan.Refusal} {plan.DistanceKm:0} km)");
            Check(russia.Stockpiles["Missiles"] == 50f, "a preview fires nothing");
            Check(plan.InterceptShare == 0f && plan.AirControl < 0f, "an undefended sky stops nothing");

            // An undefended depot.
            var hub = c.States.First(s => s.MapIndex == plan.StateMapIndex);
            var damageBefore = hub.WarDamage;
            Salvo(c, brigade);
            var report = c.SupportStrikeReports[brigade.Id];
            Check(report.Launched > 0f && report.Intercepted == 0f && report.Landed > 0f,
                $"the salvo lands whole ({report.Landed:0.0} of {report.Launched:0.0})");
            Check(russia.Stockpiles["Missiles"] < 50f && MathF.Abs(50f - russia.Stockpiles["Missiles"] - report.MunitionsSpent) < 0.01f,
                $"and costs a week of missiles ({report.MunitionsSpent:0.00})");
            Check(ukraine.Stockpiles["Ammunition"] < 80f && ukraine.Stockpiles["Oil"] < 80f, "Ukraine's depots burn");
            Check(hub.WarDamage > damageBefore, $"and the hub's region is damaged ({damageBefore:0.000} -> {hub.WarDamage:0.000})");

            // Air defence over the target.
            var sam = Formation(c, 2, ukraine, kharkiv, ukrainianBase, "sam_brigade");
            sam.Mission = SupportCore.MissionStandby;
            c.SupportFormations.Add(sam);
            SupportCore.IndexReach(c);
            brigade.TargetStateMapIndex = kharkiv.MapIndex;
            plan = LongRangeStrikes.Preview(c, brigade);
            Check(plan.StateMapIndex == kharkiv.MapIndex && plan.InterceptShare > 0.05f,
                $"a missile brigade's screen over Kharkiv stops part of a salvo ({plan.InterceptShare:P0})");
            var interceptorsBefore = ukraine.Stockpiles["Missiles"];
            Salvo(c, brigade);
            report = c.SupportStrikeReports[brigade.Id];
            Check(report.Intercepted > 0f && report.Landed < report.Launched * plan.Precision,
                $"the salvo is thinned ({report.Intercepted:0.0} of {report.Launched:0.0} shot down)");
            Check(ukraine.Stockpiles["Missiles"] < interceptorsBefore, "and the defender spends interceptors doing it");

            // Saturation, evasion, empty launchers.
            var reach = plan.Enemy;
            var small = LongRangeStrikes.InterceptShare(c, missiles, 2f, reach, ukraine);
            var big = LongRangeStrikes.InterceptShare(c, missiles, 40f, reach, ukraine);
            Check(big < small, $"a mass salvo saturates the screen ({big:P0} against {small:P0})");
            Check(LongRangeStrikes.InterceptShare(c, shaheds, 2f, reach, ukraine) > small,
                "slow drones are shot down more than missiles");
            ukraine.Stockpiles["Missiles"] = 0f;
            Check(LongRangeStrikes.InterceptShare(c, missiles, 2f, reach, ukraine) < small,
                "a screen out of interceptor missiles stops less");
            ukraine.Stockpiles["Missiles"] = 50f;

            // Reconnaissance makes it land.
            var blind = plan.Precision;
            var scout = Formation(c, 3, russia, belgorod, russianBase, "recon_drone_group");
            scout.Mission = AirReconnaissance.Mission;
            c.SupportFormations.Add(scout);
            SupportCore.IndexReach(c);
            plan = LongRangeStrikes.Preview(c, brigade);
            Check(plan.Spotted && plan.Precision > blind, $"a reconnaissance flight over the target sharpens it ({blind:P0} -> {plan.Precision:P0})");

            // An empty depot fires nothing.
            russia.Stockpiles["Missiles"] = 0f;
            var ammunition = ukraine.Stockpiles["Ammunition"];
            Salvo(c, brigade);
            report = c.SupportStrikeReports[brigade.Id];
            Check(report.Launched == 0f && report.Note.Length > 0 && ukraine.Stockpiles["Ammunition"] == ammunition,
                "an empty depot fires nothing, and says so");
            russia.Stockpiles["Missiles"] = 50f;

            // Front: the stack in the line loses organisation and equipment.
            var stack = c.Armies.Where(a => a.NationId == ukraine.Id && a.ProvinceId > 0 && a.Divisions > 0f && a.EmbarkedFleetId < 0)
                .FirstOrDefault(a => SupportCore.DistanceKm(c, russianBase, a.ProvinceId) <= missiles.RangeKm);
            Check(stack is not null, "a Ukrainian formation stands in reach");
            new SetSupportStrikeTargetCommand { IssuerId = russia.Id, FormationId = brigade.Id,
                Target = LongRangeStrikes.TargetFront, StateMapIndex = c.ProvinceMap.ProvinceToState[stack.ProvinceId] }.Apply(c);
            stack.Organisation = 1f;
            var equipment = stack.Equipment;
            Salvo(c, brigade);
            report = c.SupportStrikeReports[brigade.Id];
            Check(report.Target == LongRangeStrikes.TargetFront && report.Landed > 0f, "the named front region is struck");
            var hit = c.Armies.Where(a => a.ProvinceId == report.Province && a.NationId == ukraine.Id).ToList();
            Check(hit.Count > 0 && hit.All(a => a.Organisation < 1f || a.Equipment < equipment),
                $"and the stack there loses organisation ({hit.Min(a => a.Organisation):0.000})");

            // Energy: the grid, the cities, the country's stability.
            new SetSupportStrikeTargetCommand { IssuerId = russia.Id, FormationId = brigade.Id, Target = LongRangeStrikes.TargetEnergy }.Apply(c);
            var stability = ukraine.Stability;
            Salvo(c, brigade);
            report = c.SupportStrikeReports[brigade.Id];
            Check(report.Target == LongRangeStrikes.TargetEnergy && ukraine.Stability < stability,
                $"a strike on the grid costs Ukraine stability ({stability:0.0000} -> {ukraine.Stability:0.0000})");

            // Orders: refused whole when wrong, and a fallback the report explains.
            var refused = new SetSupportStrikeTargetCommand { IssuerId = ukraine.Id, FormationId = brigade.Id, Target = LongRangeStrikes.TargetFront };
            refused.Apply(c);
            Check(refused.Refusal.Length > 0 && brigade.StrikeTarget == LongRangeStrikes.TargetEnergy, "another country cannot aim our brigade");
            var nonsense = new SetSupportStrikeTargetCommand { IssuerId = russia.Id, FormationId = brigade.Id, Target = "moon" };
            nonsense.Apply(c);
            Check(nonsense.Refusal.Length > 0 && brigade.StrikeTarget == LongRangeStrikes.TargetEnergy, "an unknown target is refused");
            var samOrder = new SetSupportStrikeTargetCommand { IssuerId = ukraine.Id, FormationId = sam.Id, Target = LongRangeStrikes.TargetFront };
            samOrder.Apply(c);
            Check(samOrder.Refusal.Length > 0, "air defence cannot be given a strike target");
            var far = c.States.Where(s => s.NationMapIndex == ukraine.MapIndex)
                .FirstOrDefault(s => LongRangeStrikes.AnchorOf(c.ProvinceMap, s.MapIndex) is var a && a > 0 &&
                                     SupportCore.DistanceKm(c, russianBase, a) > missiles.RangeKm);
            if (far is not null)
            {
                new SetSupportStrikeTargetCommand { IssuerId = russia.Id, FormationId = brigade.Id,
                    Target = LongRangeStrikes.TargetIndustry, StateMapIndex = far.MapIndex }.Apply(c);
                plan = LongRangeStrikes.Preview(c, brigade);
                Check(plan.StateMapIndex != far.MapIndex && plan.Refusal.Length > 0 && plan.Province > 0,
                    $"a named region out of reach ({far.DisplayName}) falls back to the staff's choice, with the reason");
            }
            var wire = new SetSupportStrikeTargetCommand { IssuerId = russia.Id, FormationId = brigade.Id, Target = LongRangeStrikes.TargetLogistics, StateMapIndex = kharkiv.MapIndex };
            Check(NetworkCommandCodec.TryEncode(wire, out var wireType, out var wireJson) &&
                  NetworkCommandCodec.TryDecode(wireType, wireJson, out var decoded) && decoded is SetSupportStrikeTargetCommand back &&
                  back.Target == wire.Target && back.StateMapIndex == wire.StateMapIndex,
                "a strike order survives the network/replay codec");

            // A formation on deep strike is not over the battles.
            c.SupportExposure.Clear();
            brigade.Mission = SupportCore.MissionStrike;
            var mlrs = Formation(c, 4, russia, belgorod, russianBase, "mlrs_battalion");
            mlrs.Mission = SupportCore.MissionStrike;
            c.SupportFormations.Add(mlrs);
            SupportCore.IndexReach(c);
            var (fire, _, _, _, shown, _) = SupportCore.Engagement(c, ukrainianBase, new HashSet<int> { russia.Id }, new HashSet<int> { ukraine.Id });
            Check(fire == 1f && shown == 0f && !c.SupportExposure.ContainsKey(mlrs.Id),
                "a battery on deep strike adds nothing to a battle and is not charged its sky");

            // Fingerprint: nothing at defaults, something once a target is named.
            brigade.StrikeTarget = ""; brigade.TargetStateMapIndex = -1;
            var plain = CampaignFingerprint.Of(c);
            brigade.StrikeTarget = LongRangeStrikes.TargetIndustry;
            var aimed = CampaignFingerprint.Of(c);
            brigade.StrikeTarget = "";
            Check(aimed != plain && CampaignFingerprint.Of(c) == plain, "a named target is visible to peers; the default mixes nothing");
            d.StrikeInterceptorCost *= 2f;
            var ruled = CampaignFingerprint.Of(c);
            d.StrikeInterceptorCost /= 2f;
            Check(ruled != plain && CampaignFingerprint.Of(c) == plain, "a changed strike rule is visible to peers");

            // ---- the AI
            russia.IsHuman = false;
            Check(SupportCore.RoleOf(c, russia, missiles) == SupportCore.MissionStrike, "the AI fires its missiles");
            russia.Stockpiles["Missiles"] = 0f;
            Check(SupportCore.RoleOf(c, russia, missiles) == SupportCore.MissionStandby, "and holds them when the depot is empty");
            russia.Stockpiles["Missiles"] = 50f;
            Check(SupportCore.RoleOf(c, russia, SupportTypeCatalog.Get("recon_drone_group")) == AirReconnaissance.Mission,
                "the AI's reconnaissance drones look instead of shooting");
            var ukrainianDrones = Formation(c, 5, ukraine, kharkiv, ukrainianBase, "strike_drone_group");
            c.SupportFormations.Add(ukrainianDrones);
            SupportCore.IndexReach(c);
            Check(SupportCore.RoleOf(c, russia, SupportTypeCatalog.Get("fighter_wing")) == SupportCore.MissionAirSuperiority,
                "the AI's fighters sweep a sky the enemy flies in");

            ukraine.IsHuman = false;
            ukraine.Treasury = 1000f;
            ukraine.ManpowerReserve = Math.Max(ukraine.ManpowerReserve, 1_000_000);
            var orders = c.SupportOrders.Count(o => o.NationId == ukraine.Id);
            // A review every day, so today is the country's day (every AI at war reviews with it;
            // the finally block puts every treasury back).
            d.AiLongRangeProcurementDays = 1;
            c.SupportFormations.Remove(sam);
            SupportCore.IndexReach(c);
            LongRangeStrikes.Procure(c);
            var raised = c.SupportOrders.Where(o => o.NationId == ukraine.Id).Skip(orders).ToList();
            Check(raised.Count == 1 && raised[0].TypeId == "sam_brigade",
                $"an AI under missile attack with an open sky buys air defence first ({string.Join(",", raised.Select(o => o.TypeId))})");
            LongRangeStrikes.Procure(c);
            raised = c.SupportOrders.Where(o => o.NationId == ukraine.Id).Skip(orders).ToList();
            Check(raised.Count == 2 && raised[1].TypeId == "long_range_drone_group",
                $"and then a strike arm of its own, drones for an economy its size ({string.Join(",", raised.Select(o => o.TypeId))})");
            var treasury = ukraine.Treasury;
            ukraine.Treasury = SupportCore.CostOf(ukraine, SupportTypeCatalog.Get("long_range_drone_group")) * 2f;
            LongRangeStrikes.Procure(c);
            Check(c.SupportOrders.Count(o => o.NationId == ukraine.Id) == orders + 2,
                "but never out of a treasury that cannot keep the reserve");
            ukraine.Treasury = treasury;
        }
        finally
        {
            c.SupportFormations.Clear();
            c.SupportFormations.AddRange(saved);
            foreach (var f in saved) f.Mission = savedMissions[f.Id];
            c.SupportOrders.Clear();
            c.SupportOrders.AddRange(savedOrders);
            foreach (var (id, stocks) in savedStocks)
            {
                var n = c.NationById(id);
                n.Stockpiles.Clear();
                foreach (var (k, v) in stocks) n.Stockpiles[k] = v;
            }
            foreach (var n in c.Nations) (n.Treasury, n.ManpowerReserve, n.IsHuman) = savedTreasury[n.Id];
            d.AiLongRangeProcurementDays = savedEvery;
            ukraine.Stability = savedStability;
            foreach (var s in c.States) (s.WarDamage, s.StandardOfLiving) = savedDamage[s.MapIndex];
            foreach (var a in c.Armies) if (savedArmies.TryGetValue(a.Id, out var was)) (a.Organisation, a.Equipment) = was;
            if (addedWar) c.Wars.Remove(war);
            c.SupportStrikeReports.Clear();
            c.SupportExposure.Clear();
            SupportCore.IndexReach(c);
        }
    }

    /// <summary>Fire one salvo of this formation (its id puts today on its day of the week), alone.</summary>
    private static void Salvo(Campaign c, SupportFormation formation)
    {
        var mission = new Dictionary<int, string>();
        foreach (var other in c.SupportFormations)
            if (other.Id != formation.Id && other.Mission == SupportCore.MissionStrike) { mission[other.Id] = other.Mission; other.Mission = SupportCore.MissionStandby; }
        formation.Mission = SupportCore.MissionStrike;
        try { LongRangeStrikes.Fire(c); }
        finally
        {
            foreach (var (id, m) in mission) c.SupportFormations.First(f => f.Id == id).Mission = m;
        }
    }

    private static SupportFormation Formation(Campaign c, int id, Nation nation, ScenarioState state, int province, string type) =>
        new()
        {
            // Every test formation fires on today's day of the week.
            Id = IdFor(c, id), NationId = nation.Id, TypeId = type, StateMapIndex = state.MapIndex,
            BaseProvinceId = province, Strength = 1f, Experience = 0.3f, Mission = SupportCore.MissionStandby
        };

    private static int IdFor(Campaign c, int id)
    {
        var interval = Math.Max(1, c.Defines.StrikeIntervalDays);
        var raw = 990000 + id * interval;
        return raw + ((-(c.ElapsedDays + raw)) % interval + interval) % interval;
    }
}
