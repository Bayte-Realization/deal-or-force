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

    static void PassageAndHostileControllers()
    {
        var c = Fixture();
        c.AddNation(new Nation { Tag = "CCC", MapIndex = 502, Population = 1000 });
        c.AddNation(new Nation { Tag = "DDD", MapIndex = 503, RetiredDay = 0 });
        c.Fallen.Add(3);
        CampaignDiplomacy.Get(c, 0, 1).MilitaryAccessByB = true;
        var aliasRelation = CampaignDiplomacy.Get(c, 0, 2);
        var retiredRelation = CampaignDiplomacy.Get(c, 0, 3);
        retiredRelation.Stance = DiplomaticStance.AtWar;

        var ground = LongRangeMovement.GroundOf(c, 0);
        Check(ground.IsPassable(501) && ground.IsPassable(502) && !ground.IsHostile(503),
            "home and directional access are passable while an archived country is excluded from relation-only enemies");

        aliasRelation.Stance = DiplomaticStance.AtWar;
        ground = LongRangeMovement.GroundOf(c, 0);
        Check(ground.IsHostile(502) && !ground.IsPassable(502) && MilitaryAccess.Controllers(c, 0).Contains(502),
            "a hostile alias blocks support movement while ordinary access still uses the indexed owner");

        aliasRelation.Stance = DiplomaticStance.Neutral;
        var war = new StrategicWar { Id = 2, AttackerId = 0, DefenderId = 3 };
        war.AttackerAllies.Add(1);
        c.Wars.Add(war);
        CampaignDiplomacy.Get(c, 0, 1).MilitaryAccessByB = false;
        ground = LongRangeMovement.GroundOf(c, 0);
        Check(ground.IsPassable(502) && ground.IsHostile(503),
            "a cobelligerent admits support formations and a live enemy is hostile even when retired");

        CampaignDiplomacy.Get(c, 0, 1).Stance = DiplomaticStance.AtWar;
        ground = LongRangeMovement.GroundOf(c, 0);
        Check(ground.IsHostile(502) && !ground.IsPassable(502),
            "a direct at-war relation overrides shared coalition access");
    }

    public static void Run()
    {
        PassageAndHostileControllers();
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
        // Lost ground is a withdrawal, not a jump (LongRangeMovement.Withdraw): the formation stays
        // where it stood, pays the overrun once if the enemy took it, and leaves by road.
        foreach (var atWar in new[] { false, true })
        {
            var c = Fixture(atWar); var f = Add(c, 1); var stood = f.BaseProvinceId; Lose(c, 1001);
            var rng = c.Random.State;
            var readStamp = CampaignFingerprint.Of(c);
            SupportCore.IndexReach(c);
            Check(CampaignFingerprint.Of(c) == readStamp && SupportCore.Displaced(c, f), "rebuilding a read index never moves anything on a paused/UI read");
            Check(!SupportCore.InRange(c, f, SupportTypeCatalog.Get(f.TypeId), f.BaseProvinceId), "captured base projects no power, war=" + atWar);
            SupportCore.PrepareStep(c); // Actual per-step hook, not a monthly AI call.
            Check(f.ProvinceId == stood && f.StateMapIndex == 1001 && f.Route.Count > 0 && f.BaseProvinceId == -1 &&
                  c.ProvinceMap.Neighbors[stood].Contains(f.Route[0]),
                "the formation does not jump: it starts withdrawing by road from where it stood, war=" + atWar);
            var expected = atWar ? .43f * (1f - c.Defines.LongRangeOverrunLoss) : .43f;
            Check(MathF.Abs(f.Strength - expected) < 1e-6f && f.Experience == .62f && c.Random.State == rng,
                "an enemy overrun costs the overrun loss once; a lost access costs nothing; no RNG, war=" + atWar);
            var hash = CampaignFingerprint.Of(c);
            Check(SupportRepatriation.Reconcile(c) == 0 && CampaignFingerprint.Of(c) == hash, "repeated reconciliation is inert");
            var at = f.ProvinceId;
            for (var day = 0; day < 40 && (f.Route.Count > 0 || f.DeployDaysLeft > 0); day++)
            {
                SupportRepatriation.Reconcile(c);
                LongRangeMovement.Advance(c);
                Check(f.ProvinceId == at || c.ProvinceMap.Neighbors[at].Contains(f.ProvinceId), "never more than one neighbouring province a day, war=" + atWar);
                at = f.ProvinceId;
            }
            Check(c.ProvinceMap.ProvinceToState[f.ProvinceId] == 1002 && !SupportCore.Displaced(c, f) && MathF.Abs(f.Strength - expected) < 1e-6f,
                "the withdrawal ends deployed on its own ground, and the loss was charged only once, war=" + atWar);
        }
        {
            var c = Fixture(true); var f = Add(c, 1); var stood = f.BaseProvinceId; Lose(c, 1001); Lose(c, 1002);
            SupportCore.PrepareStep(c);
            Check(f.CutOff && f.ProvinceId == stood && f.Route.Count == 0 && SupportCore.Displaced(c, f) && c.SupportFormations.Count == 1,
                "with no way out the formation is cut off where it stands - on the map, never off it");
            var strength = f.Strength;
            for (var day = 0; day < 3; day++) LongRangeMovement.Advance(c);
            Check(f.CutOff && f.Strength == strength, "a cut-off formation looks for a way out daily without paying again");
            // The neighbouring region retaken: the way out opens and it leaves.
            c.States.First(s => s.MapIndex == 1002).NationMapIndex = 501;
            for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
                if (c.ProvinceMap.ProvinceToState[p] == 1002) { c.ProvinceMap.ProvinceToNation[p] = 501; c.SetProvinceController(p, 501); }
            LongRangeMovement.Advance(c);
            Check(!f.CutOff && (f.Route.Count > 0 || f.ProvinceId != stood), "a way out reopened is taken the next day");
        }
        {
            var c = Fixture(); var f = Add(c, 1, 1002); f.StateMapIndex = 9999;
            SupportCore.PrepareStep(c);
            Check(f.StateMapIndex == 1002 && !SupportCore.Displaced(c, f), "the region always follows the province the formation stands in");
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
        LongRangeMovement.Settle(c);
        var lost = c.SupportFormations.Where(f => SupportCore.Enabled(c, f.TypeId) && SupportCore.Displaced(c, f) && f.Route.Count == 0 && f.DeployDaysLeft == 0).ToArray();
        Console.WriteLine($"SUPPORT_SAVE_BEFORE day={c.ElapsedDays} human_rus={rus?.IsHuman} formations={c.SupportFormations.Count} displaced={lost.Length} rus_displaced={lost.Count(f => f.NationId == rus?.Id)}");
        var rng = c.Random.State; var locations = lost.Select(f => (f.Id, f.Strength, f.Experience, f.ProvinceId)).ToArray();
        SupportCore.PrepareStep(c);
        Check(lost.All(f => f.Route.Count > 0 || f.CutOff || !SupportCore.Displaced(c, f)), "every originally stranded formation is withdrawing, cut off in place, or working again");
        Console.WriteLine($"SUPPORT_SAVE_RUSSIA withdrawing={lost.Count(f => f.NationId == rus?.Id && f.Route.Count > 0)} cut_off={lost.Count(f => f.NationId == rus?.Id && f.CutOff)}");
        Check(locations.All(old => c.SupportFormations.Any(f => f.Id == old.Id && f.ProvinceId == old.ProvinceId && f.Experience == old.Experience &&
                f.Strength >= old.Strength * (1f - c.Defines.LongRangeOverrunLoss) - 1e-5f)) && rng == c.Random.State,
            "player save withdrawal keeps every formation where it stood, charges at most the overrun once, without RNG");
        c.IsPaused = false; var start = c.ElapsedDays;
        for (var day = 0; day < 2; day++) { c.TickDay(); Check(c.SimulationFault.Length == 0, "player campaign continues after withdrawal, day=" + c.ElapsedDays); }
        Console.WriteLine($"SUPPORT_SAVE_AFTER days={c.ElapsedDays - start} moving={c.SupportFormations.Count(f => f.Route.Count > 0)} cut_off={c.SupportFormations.Count(f => f.CutOff)} unplaced={c.SupportFormations.Count(f => f.ProvinceId <= 0)}");
        Check(c.SupportFormations.All(f => !SupportCore.Enabled(c, f.TypeId) || f.ProvinceId > 0), "no enabled formation is off the map after continuation");
        Check(source.AsSpan().SequenceEqual(Hash()), "original player save SHA256 remains unchanged");
    }
}
