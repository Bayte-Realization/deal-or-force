using System;
using System.Linq;
using System.Collections.Generic;
using DealOrForce.Simulation;

static class PocketResistanceTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("FAIL pocket resistance: " + message); Console.WriteLine("PASS pocket resistance: " + message); }

    public static void Run()
    {
        HoldsUntilTheDefenceActuallyFails();
        OpenRetreatAndToggle();
        DeepRetreatIsOneLegalStep();
        BreakoutRespectsAccessAndCoalitions();
        ReliefIsAnApproachNotProximity();
        HungerAndAlliedGarrisons();
        HourlyHungerAndSnapshot();
        RuleOverridesAreAuthoritative();
    }

    static void HoldsUntilTheDefenceActuallyFails()
    {
        var c = Fixture(); var a = Unit(c, 0, 0, 2); var d = Unit(c, 1, 1, 3);
        Unit(c, 2, 0, 4); d.Organisation = .001f; d.PocketStores = 12;
        var battle = Battle(c, a, d);
        var fingerprint = CampaignFingerprint.Of(c); var random = c.Random.State;
        Check(PocketResistance.CanContinue(c, c.Wars[0], 3, new[] { d }) &&
            CampaignFingerprint.Of(c) == fingerprint && c.Random.State == random,
            "resistance eligibility is a pure read and does not rely on a stale cutoff trace");
        Warfare.Tick(c);
        Check(!battle.Ended && c.Armies.Contains(d) && c.ProvinceController[3] == 501 &&
            a.ProvinceId == 2 && d.ProvinceId == 3 && d.InBattle,
            "organisation collapse alone neither captures the province nor deletes its trapped defenders");
        Check(d.Strength < 1 && battle.DefenderLosses > 0 && c.Wars[0].DefenderCaptured == 0 &&
            battle.Rounds.Last().Note == PocketResistance.BattleNote,
            "resistance takes normal combat losses and explains itself without invented prisoners");
        d.Strength = .24f;
        Warfare.Tick(c);
        Check(battle.Ended && !c.Armies.Contains(d) && c.ProvinceController[3] == 500 &&
            c.Wars[0].DefenderCaptured > 0,
            "a genuinely depleted closed defence falls and accounts for the survivors");
    }

    static void OpenRetreatAndToggle()
    {
        foreach (var mode in new[] { "open", "disabled", "starved" })
        {
            var c = Fixture(); var a = Unit(c, 0, 0, 2); var d = Unit(c, 1, 1, 3);
            Unit(c, 2, 0, 4); d.Organisation = .001f; d.PocketStores = 12;
            if (mode == "open") { c.Armies.RemoveAt(2); c.SetProvinceController(4, 502); }
            if (mode == "disabled") c.Defines.PocketResistanceEnabled = false;
            if (mode == "starved") { d.PocketStores = 0; d.SupplyLevel = 0; }
            var battle = Battle(c, a, d); Warfare.Tick(c);
            Check(battle.Ended && c.ProvinceController[3] == 500,
                mode + ": normal defeat is not turned into an immortal pocket");
            if (mode == "open") Check(c.Armies.Contains(d) && d.ProvinceId == 4 &&
                c.Wars[0].DefenderCaptured == 0, "an allied exit still permits an ordinary retreat");
        }
    }

    static void DeepRetreatIsOneLegalStep()
    {
        var c = Fixture(); c.Defines.RetreatSearchDepth = 4;
        var a = Unit(c, 0, 0, 2); var d = Unit(c, 1, 1, 3);
        var battle = Battle(c, a, d); d.Organisation = .001f; d.PocketStores = 12;
        d.PlanActive = true; d.DirectOrder = true; d.PlannedRoute.Add(2);
        Check(WarCoalition.SpreadRetreatProvince(c, c.Wars[0], d, 3, true) == 4,
            "a deep retreat chooses the neighbouring first step, not the distant home province");
        Warfare.Tick(c);
        Check(battle.Ended && c.Armies.Contains(d) && d.ProvinceId == 4 &&
            c.ProvinceMap.Neighbors[3].Contains(d.ProvinceId) && d.PlannedRoute.Count == 0 &&
            !d.DirectOrder && !d.PlanActive && d.CutOff,
            "actual battle retreat is one adjacent step and cannot resume the stale offensive");
        c = Fixture(); c.Defines.RetreatSearchDepth = 4;
        d = Unit(c, 1, 1, 3); Unit(c, 0, 0, 2); c.SetProvinceController(4, 503);
        Check(WarCoalition.SpreadRetreatProvince(c, c.Wars[0], d, 3, true) == 0,
            "deep retreat cannot cross a neutral country without access");
    }

    static void BreakoutRespectsAccessAndCoalitions()
    {
        var c = Fixture(); var d = Unit(c, 1, 1, 3); d.CutOff = true;
        c.SetProvinceController(4, 503);
        Check(PocketBreakout.WayHome(c, c.Nations[1], 3) is null,
            "breakout does not invent a road through neutral territory");
        c.SetProvinceController(4, 500);
        var way = PocketBreakout.WayHome(c, c.Nations[1], 3);
        Check(way is not null && way.SequenceEqual(new[] { 3, 4, 5, 6 }),
            "a legal hostile corridor is returned as all adjacent steps");
        c.SetProvinceController(6, 502);
        way = PocketBreakout.WayHome(c, c.Nations[1], 3);
        Check(way is not null && way.Last() == 6, "allied ground is a legitimate breakout refuge");
        c.Wars[0].DefenderAllies.Clear(); c.MarkWarsChanged();
        Check(PocketBreakout.WayHome(c, c.Nations[1], 3) is null,
            "same-day coalition withdrawal cannot leave a cached friendly destination");
        c = Fixture(); d = Unit(c, 1, 1, 3); d.CutOff = true; d.ReliefDistance = -2;
        c.ConfigureSinglePlayer(1); c.Defines.AiBreaksOutOfPockets = false;
        PocketBreakout.Tick(c);
        Check(d.ReliefDistance == 3 && !d.BreakingOut && d.Route.Count == 0 && d.PlannedRoute.Count == 0,
            "disabling AI breakout still refreshes human relief facts without issuing orders");
    }

    static void ReliefIsAnApproachNotProximity()
    {
        var c = Fixture(); var d = Unit(c, 1, 1, 3); d.CutOff = true; d.PocketStores = 20;
        var way = PocketBreakout.WayHome(c, c.Nations[1], 3);
        var relief = Unit(c, 2, 1, 6);
        relief.Route.Add(5);
        Check(StrandedGarrisons.ShouldHold(c, d, way), "a real adjacent movement toward the pocket is relief");
        relief.Route.Clear(); relief.Route.Add(7);
        Check(!StrandedGarrisons.ShouldHold(c, d, way), "movement away from the pocket is not relief");
        relief.Route.Clear(); relief.PlanActive = true; relief.PlannedRoute.Add(5);
        Check(StrandedGarrisons.ShouldHold(c, d, way), "an active hostile attack leg counts without a friendly marching prefix");
        relief.PlannedRoute.Clear(); relief.PlanActive = false;
        var enemy = Unit(c, 3, 0, 5); var battle = Battle(c, relief, enemy, 5);
        Check(StrandedGarrisons.ShouldHold(c, d, way), "a real attacking battle into the ring counts as relief");
        battle.Attackers.Clear(); battle.Defenders.Add(relief.Id);
        Check(!StrandedGarrisons.ShouldHold(c, d, way), "merely defending a nearby battle is not a relief attack");
        battle.Ended = true; relief.BattleId = -1; relief.Route.Add(5); c.SetProvinceController(5, 503);
        Check(!StrandedGarrisons.ShouldHold(c, d, way), "a neutral barrier invalidates claimed relief");
    }

    static void HungerAndAlliedGarrisons()
    {
        var c = Fixture(); var d = Unit(c, 1, 1, 3);
        d.CutOff = true; d.PocketStores = 0; d.DaysCutOff = 30; d.ReliefDistance = 2;
        c.Defines.PocketSurrenderDailyChance = 100;
        // Province 3 belongs to the enemy in the authored topology, but its guest garrison holds it now.
        c.ProvinceMap.ProvinceToNation[3] = 500;
        var ally = Unit(c, 2, 2, 3);
        StrandedGarrisons.Tick(c);
        Check(!c.Armies.Contains(d) && c.Wars[0].DefenderCaptured > 0,
            "a nearby friendly province without an approaching force does not prevent hungry surrender");
        Check(c.Armies.Contains(ally) && c.ProvinceController[3] == 501,
            "one garrison surrendering does not hand the ground out from under its coalition ally");

        c = Fixture(); d = Unit(c, 1, 1, 3); d.CutOff = true; d.PocketStores = 0;
        d.DaysCutOff = 30; d.ReliefDistance = 3; c.Defines.PocketSurrenderDailyChance = 100;
        var relief = Unit(c, 2, 2, 6); relief.Route.Add(5);
        StrandedGarrisons.Tick(c);
        Check(c.Armies.Contains(d), "actual coalition relief, not just own-national relief, delays hungry surrender");
        relief.Route.Clear(); relief.Route.Add(7); StrandedGarrisons.Tick(c);
        Check(!c.Armies.Contains(d), "relief turning away removes the exemption without resetting hunger");

        c = Fixture(); d = Unit(c, 1, 1, 3); ally = Unit(c, 2, 2, 3);
        c.ProvinceMap.ProvinceToNation[3] = 500; c.Defines.PocketSurrenderDailyChance = 100;
        foreach (var unit in new[] { d, ally })
        { unit.CutOff = true; unit.PocketStores = 0; unit.DaysCutOff = 30; unit.ReliefDistance = -1; }
        StrandedGarrisons.Tick(c);
        Check(c.Armies.Count == 0 && c.ProvinceController[3] == 500,
            "a whole coalition surrendering in one batch cannot leave ghost occupation");
    }

    static void HourlyHungerAndSnapshot()
    {
        var daily = Starvation(24); var hourly = Starvation(1);
        Check(MathF.Abs(daily.Strength - hourly.Strength) < .0001f &&
            MathF.Abs(daily.Losses - hourly.Losses) < 2f && daily.Hungry == hourly.Hungry,
            "stores-to-hunger lifecycle takes calendar days, not 24 daily losses at hourly cadence");
        var c = Fixture();
        c.ConfigureSinglePlayer(1);
        // Give the synthetic world the same load-time economy/debt/doctrine preparation
        // as a real opened campaign before asking for exact whole-world round-trip parity.
        var prepared = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "pocket-test"), "pocket-test");
        if (!prepared.Success) throw new Exception(prepared.Warning);
        c = prepared.Campaign; c.AttachProvinceTopology(Fixture().ProvinceMap);
        var a = Unit(c, 0, 0, 2); var d = Unit(c, 1, 1, 3);
        Unit(c, 2, 0, 4); d.Organisation = .001f; d.PocketStores = 12;
        Battle(c, a, d); Warfare.Tick(c); c.ConfigureSinglePlayer(1);
        var fp = CampaignFingerprint.Of(c);
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "pocket-test"), "pocket-test");
        Check(saved.Success &&
            saved.Campaign.Battles.Single().Rounds.Last().Note == PocketResistance.BattleNote,
            "an ongoing pocket battle and its ordinary fields round-trip through the real snapshot");
        // Attach a separate topology before continuing: the loaded province arrays belong to its own world.
        var restored = saved.Campaign; restored.AttachProvinceTopology(Fixture().ProvinceMap);
        Check(CampaignFingerprint.Of(restored) == fp, "the prepared authoritative world keeps its full fingerprint on reload");
        Warfare.Tick(c); Warfare.Tick(restored);
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(restored) && c.Random.State == restored.Random.State,
            "saved continuation and interleaved worlds keep the same battle outcome and RNG");
    }

    static (float Strength, float Losses, int Hungry) Starvation(int hours)
    {
        var c = Fixture(); c.Defines.HoursPerTick = hours;
        var d = Unit(c, 1, 1, 3); d.CutOff = true; d.PocketStores = 2; d.DirectOrder = true;
        // Only the lifecycle passes under test; the calendar is advanced exactly as their host tick does.
        for (var elapsed = 0; elapsed < 5 * 24; elapsed += hours)
        {
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!.SetValue(c, (long)elapsed);
            typeof(Campaign).GetProperty(nameof(Campaign.StepOpensNewDay))!.SetValue(c, elapsed % 24 == 0);
            // Keep this closed-ring fixture independent of the full three-day road rebuild.
            if (c.StepOpensNewDay)
                typeof(LogisticsSystem).GetMethod("AgeCutOff", System.Reflection.BindingFlags.NonPublic |
                    System.Reflection.BindingFlags.Static)!.Invoke(null, new object[] { c });
            Attrition.Tick(c);
        }
        return (d.Strength, c.Wars[0].DefenderLosses, d.DaysCutOff);
    }

    static void RuleOverridesAreAuthoritative()
    {
        var c = Fixture(); c.ConfigureSinglePlayer(1);
        var baseline = CampaignFingerprint.Of(c);
        c.Defines.PocketResistanceEnabled = false;
        Check(CampaignFingerprint.Of(c) != baseline, "the mechanic toggle changes the authority fingerprint");
        c.Defines.PocketResistanceEnabled = true; c.Defines.PocketResistanceMinStrength = .5f;
        Check(CampaignFingerprint.Of(c) != baseline, "a mod's strength threshold changes the authority fingerprint");
        c.Defines.PocketResistanceMinStrength = .25f; c.Defines.PocketResistanceMinSupply = .3f;
        Check(CampaignFingerprint.Of(c) != baseline, "a mod's supply threshold changes the authority fingerprint");
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "pocket-rules"), "pocket-rules");
        Check(saved.Success && saved.Campaign.Defines.PocketResistanceMinSupply == .3f,
            "scenario rule overrides survive the existing save schema");
    }

    static Army Unit(Campaign c, int id, int nation, int province)
    {
        var centre = c.ProvinceMap.CenterOf(province);
        var a = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = c.ProvinceMap.ProvinceToState[province], AssignedWarId = 991,
            OrderProvinceId = -1, OrderKind = ArmyOrder.Manual, DirectOrder = true,
            Divisions = 1, Strength = 1, Organisation = 1, SupplyLevel = 1, Cohesion = .8f,
            Personnel = 15_000, Latitude = centre.Latitude, Longitude = centre.Longitude };
        c.Armies.Add(a); return a;
    }

    static LandBattle Battle(Campaign c, Army a, Army d, int province = 3)
    {
        var b = new LandBattle { Id = 992, WarId = 991, ProvinceId = province,
            AttackerId = a.NationId, DefenderId = d.NationId };
        b.Attackers.Add(a.Id); b.Defenders.Add(d.Id); a.BattleId = d.BattleId = b.Id;
        c.Battles.Add(b); return b;
    }

    static Campaign Fixture()
    {
        const int width = 256, height = 64;
        var states = new ushort[width * height]; var controllers = new ushort[states.Length];
        var rgb = new byte[states.Length * 3];
        var owners = new ushort[] { 0, 500, 500, 501, 500, 500, 501, 502, 503 };
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            var p = x / 32 + 1; var i = y * width + x;
            states[i] = (ushort)(3000 + p); controllers[i] = owners[p]; rgb[i * 3 + 2] = (byte)p;
        }
        var map = ProvinceTopology.FromColorRaster(states, controllers, width, height,
            new RgbDataMap(width, height, rgb), Enumerable.Range(1, 8).ToDictionary(p => p, p => p));
        // Close the international-date-line seam: a linear land corridor, not a circular island.
        for (var p = 1; p <= 8; p++) map.Neighbors[p] = new[] { p - 1, p + 1 }.Where(n => n > 0 && n <= 8).ToArray();
        var c = new Campaign(777) { ScenarioId = "pocket_fixture" };
        c.Defines.HoursPerTick = 24; c.Defines.RetreatSearchDepth = 1;
        c.Defines.FrontStaffCallsUpIdle = false;
        for (var n = 0; n < 4; n++) c.Nations.Add(new Nation { Id = n, MapIndex = 500 + n,
            Tag = "PK" + n, Name = "Pocket country " + n, Population = 10_000_000, Gdp = 100,
            Treasury = 20, ManpowerReserve = 0 });
        for (var p = 1; p <= 8; p++) c.States.Add(new ScenarioState { Id = "pocket_" + p,
            MapIndex = 3000 + p, NationMapIndex = owners[p], NationId = "PK" + (owners[p] - 500),
            Name = "Pocket region " + p, Population = 1_000_000 });
        c.AttachProvinceTopology(map); c.Armies.Clear();
        var war = new StrategicWar { Id = 991, AttackerId = 0, DefenderId = 1, LastFrontDay = 0 };
        war.DefenderAllies.Add(2); c.Wars.Add(war); return c;
    }
}
