using System;
using System.IO;
using System.Linq;
using System.Security.Cryptography;
using DealOrForce.Simulation;

static class SupportRepatriationTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("Support repatriation: " + note); Console.WriteLine("PASS support repatriation: " + note); }

    static Campaign Fixture(bool war = false)
    {
        var c = new Campaign(71107);
        c.AddNation(new Nation { Tag = "AAA", MapIndex = 501, Population = 1000000 });
        c.AddNation(new Nation { Tag = "BBB", MapIndex = 502, Population = 1000000 });
        c.SetPlayer(0);
        foreach (var id in new[] { 1001, 1002, 1003 }) c.States.Add(new ScenarioState {
            MapIndex = id, Id = "base" + id, NationMapIndex = id == 1003 ? 502 : 501, Population = 1000 });
        ushort[] regions = { 1001, 1001, 1002, 1002, 1003, 1003, 1001, 1001, 1002, 1002, 1003, 1003 };
        c.AttachProvinceTopology(ProvinceTopology.Build(regions, regions.Select(s => (ushort)(s == 1003 ? 502 : 501)).ToArray(), 6, 2, _ => false));
        c.SupportFormations.Clear(); c.SupportOrders.Clear();
        if (war) c.Wars.Add(new StrategicWar { Id = 1, AttackerId = 0, DefenderId = 1 });
        return c;
    }
    static SupportFormation Add(Campaign c, int id, int state = 1001)
    {
        var province = Enumerable.Range(1, c.ProvinceMap.ProvinceCount).First(p => c.ProvinceMap.ProvinceToState[p] == state);
        var f = new SupportFormation { Id = id, NationId = 0, TypeId = "artillery_brigade", StateMapIndex = state,
            BaseProvinceId = province, Strength = .43f, Experience = .62f, Mission = SupportCore.MissionInterdiction, TargetStateMapIndex = 1003 };
        c.SupportFormations.Add(f); return f;
    }
    static void Lose(Campaign c, int state)
    {
        c.States.First(s => s.MapIndex == state).NationMapIndex = 502;
        for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
            if (c.ProvinceMap.ProvinceToState[p] == state)
            { c.ProvinceMap.ProvinceToNation[p] = 502; c.SetProvinceController(p, 502); }
    }
    public static void Run()
    {
        var stockWorld = Fixture(); var stockOwner = stockWorld.Nations[0];
        stockOwner.Stockpiles.Clear(); stockOwner.StockpilesSeeded = false;
        var stamp = CampaignFingerprint.Of(stockWorld); var stockRng = stockWorld.Random.State;
        var quoted = StrategicStockpiles.TrackedGoods.Select(g => StrategicStockpiles.Peek(stockWorld, stockOwner, g)).ToArray();
        Check(CampaignFingerprint.Of(stockWorld) == stamp && stockWorld.Random.State == stockRng && !stockOwner.StockpilesSeeded,
            "viewing opening depot values never seeds or mutates the campaign");
        Check(StrategicStockpiles.Peek(stockWorld, stockOwner, "missiles") == StrategicStockpiles.Peek(stockWorld, stockOwner, "Missiles") &&
            StrategicStockpiles.Peek(stockWorld, stockOwner, "unknown_mod_good") == 0, "read-only depot lookup preserves case and missing-good semantics");
        StrategicStockpiles.EnsureSeeded(stockWorld, stockOwner);
        Check(quoted.SequenceEqual(StrategicStockpiles.TrackedGoods.Select(g => StrategicStockpiles.Get(stockWorld, stockOwner, g))),
            "every previewed opening amount matches actual initialized stock exactly");
        foreach (var atWar in new[] { false, true })
        {
            var c = Fixture(atWar); var f = Add(c, 1); Lose(c, 1001);
            var rng = c.Random.State;
            var readStamp = CampaignFingerprint.Of(c);
            SupportCore.IndexReach(c);
            Check(CampaignFingerprint.Of(c) == readStamp && SupportCore.Displaced(c, f), "rebuilding a read index never evacuates on a paused/UI read");
            Check(!SupportCore.InRange(c, f, SupportTypeCatalog.Get(f.TypeId), f.BaseProvinceId), "captured base projects no power, war=" + atWar);
            SupportCore.PrepareStep(c); // Actual per-step hook, not a monthly AI call.
            Check(c.Nations[0].IsHuman && !SupportCore.Displaced(c, f) && f.StateMapIndex == 1002,
                "human formation evacuates immediately to controlled homeland, war=" + atWar);
            Check(f.Strength == .43f && f.Experience == .62f && c.Random.State == rng && f.Mission == SupportCore.MissionStandby && f.TargetStateMapIndex == -1,
                "evacuation clears firing orders but grants no healing/experience/RNG, war=" + atWar);
            var hash = CampaignFingerprint.Of(c);
            Check(SupportRepatriation.Reconcile(c) == 0 && CampaignFingerprint.Of(c) == hash, "repeated reconciliation is inert");
        }
        {
            var c = Fixture(); var first = Add(c, 10); var second = Add(c, 20); Add(c, 30, 1002);
            c.SupportOrders.Add(new SupportOrder { Id = 1, NationId = 0, StateMapIndex = 1002, TypeId = first.TypeId });
            Lose(c, 1001); SupportCore.PrepareStep(c);
            Check(first.BaseProvinceId == -1 && first.StateMapIndex == -1 && second.BaseProvinceId == -1 && c.SupportFormations.Count == 3,
                "full bases plus training reservations put displaced units in off-map reserve, never delete/overfill them");
            Check(!SupportCore.InRange(c, first, SupportTypeCatalog.Get(first.TypeId), 1), "unbased reserve has no firing range");
            c.SupportOrders.Clear(); c.States.First(s => s.MapIndex == 1002).Buildings[BuildingKind.Barracks] = 1;
            typeof(Campaign).GetProperty(nameof(Campaign.StepOpensNewDay))!.SetValue(c, true);
            Check(SupportRepatriation.Reconcile(c) == 2 && !SupportCore.Displaced(c, first) && !SupportCore.Displaced(c, second),
                "reserve automatically returns when posts become available");
            Check(SupportCore.PostsUsed(c, 1002) <= SupportCore.CapacityOf(c, c.States.First(s => s.MapIndex == 1002)), "reserved posts never exceed capacity");
        }
        {
            var c = Fixture(); var f = Add(c, 1); Lose(c, 1001); Lose(c, 1002);
            SupportCore.PrepareStep(c);
            Check(f.BaseProvinceId == -1 && f.StateMapIndex == -1 && f.Strength == .43f,
                "landless nation retains a non-operational reserve instead of occupying a foreign base forever");
        }
        {
            var c = Fixture(); var f = Add(c, 1, 1002); f.StateMapIndex = 9999;
            Check(SupportCore.Displaced(c, f), "stale region binding is recognized");
            SupportCore.PrepareStep(c);
            Check(f.StateMapIndex == 1002 && !SupportCore.Displaced(c, f), "region split/reunion repairs binding using actual controlled map");
        }
        {
            var c = Fixture(); var f = Add(c, 1); Lose(c, 1001);
            c.Defines.FeatureSystems[nameof(SupportCore)] = "aviation"; c.SetFeature("aviation", false);
            var hash = CampaignFingerprint.Of(c);
            Check(SupportRepatriation.Reconcile(c) == 0 && CampaignFingerprint.Of(c) == hash && f.StateMapIndex == 1001,
                "custom disabled support-system binding preserves dormant formations");
        }
    }

    public static void Replay(string[] args)
    {
        var at = Array.IndexOf(args, "--support-repatriation-save"); var path = Path.GetFullPath(args[at + 1]);
        byte[] Hash() { using var stream = File.OpenRead(path); return SHA256.HashData(stream); }
        var source = Hash(); _ = Campaign.CreateBootstrap(71107);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), "");
        Check(loaded.Success && loaded.Campaign is not null, "player save loads: " + loaded.Warning);
        var c = loaded.Campaign;
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control, WorldMapSource.Width, WorldMapSource.Height, out var map, out var why)) throw new Exception(why);
        if (MapRasterCatalog.TryLoadHydrology(WorldMapSource.Width, WorldMapSource.Height, out var water, out _)) map.AttachHydrology(water);
        c.AttachProvinceTopology(map);
        var rus = c.NationByTag("RUS");
        var lost = c.SupportFormations.Where(f => SupportCore.Enabled(c, f.TypeId) && SupportCore.Displaced(c, f) && f.BaseProvinceId > 0).ToArray();
        Console.WriteLine($"SUPPORT_SAVE_BEFORE day={c.ElapsedDays} human_rus={rus?.IsHuman} formations={c.SupportFormations.Count} displaced={lost.Length} rus_displaced={lost.Count(f => f.NationId == rus?.Id)}");
        var rng = c.Random.State; var locations = lost.Select(f => (f.Id, f.Strength, f.Experience)).ToArray();
        SupportCore.PrepareStep(c);
        Check(lost.All(f => f.BaseProvinceId < 0 && f.StateMapIndex < 0 || !SupportCore.Displaced(c, f)), "every originally stranded formation is safely rebased or explicitly reserved");
        Console.WriteLine($"SUPPORT_SAVE_RUSSIA rebased={lost.Count(f => f.NationId == rus?.Id && f.BaseProvinceId > 0)} reserve={lost.Count(f => f.NationId == rus?.Id && f.BaseProvinceId < 0)}");
        Check(locations.All(old => c.SupportFormations.Any(f => f.Id == old.Id && f.Strength == old.Strength && f.Experience == old.Experience)) && rng == c.Random.State,
            "player save evacuation retains all formations and their condition without RNG");
        c.IsPaused = false; var start = c.ElapsedDays;
        for (var day = 0; day < 2; day++) { c.TickDay(); Check(c.SimulationFault.Length == 0, "player campaign continues after evacuation, day=" + c.ElapsedDays); }
        Console.WriteLine($"SUPPORT_SAVE_AFTER days={c.ElapsedDays - start} foreign_base={c.SupportFormations.Count(f => SupportCore.Enabled(c, f.TypeId) && f.BaseProvinceId > 0 && SupportCore.Displaced(c, f))} reserve={c.SupportFormations.Count(f => f.BaseProvinceId < 0 && f.StateMapIndex < 0)}");
        Check(c.SupportFormations.All(f => !SupportCore.Enabled(c, f.TypeId) || f.BaseProvinceId < 0 || !SupportCore.Displaced(c, f)), "no enabled formation remains on a foreign base after continuation");
        Check(source.AsSpan().SequenceEqual(Hash()), "original player save SHA256 remains unchanged");
    }
}
