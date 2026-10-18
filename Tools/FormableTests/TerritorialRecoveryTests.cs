using System;
using System.Linq;
using DealOrForce.Simulation;

static class TerritorialRecoveryTests
{
    static void Check(bool ok, string message)
    { if (!ok) throw new Exception("territorial recovery: " + message); Console.WriteLine("PASS territorial recovery: " + message); }

    internal static (Campaign c, Nation a, Nation b) World(int imperialCapacity = 4)
    {
        var c = new Campaign(20261018) { ScenarioId = "recovery_fixture" };
        c.Defines.BaseImperialCapacity = imperialCapacity;
        var a = c.AddNation(new Nation { Tag = "RCA", MapIndex = 501, Name = "A", Population = 2000000, Treasury = 10000, Gdp = 1 });
        var b = c.AddNation(new Nation { Tag = "RCB", MapIndex = 502, Name = "B", Population = 2000000, Treasury = 10, Gdp = 1 });
        var states = new ushort[32]; var owners = new ushort[32];
        for (var i = 0; i < 4; i++)
        {
            var n = i < 2 ? a : b;
            var s = new ScenarioState { Id = "recovery_" + i, Name = "Region " + i,
                MapIndex = 3001 + i, NationMapIndex = n.MapIndex, NationId = n.Tag, Population = 1000000 };
            s.PopulationGroups.Add(new PopulationGroup { Culture = "tajik", Size = 1000000 });
            c.States.Add(s);
        }
        for (var y = 0; y < 4; y++) for (var x = 0; x < 8; x++)
        { states[y * 8 + x] = (ushort)(3001 + x / 2); owners[y * 8 + x] = (ushort)(x < 4 ? 501 : 502); }
        c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 8, 4, _ => false));
        a.CapitalStateMapIndex = 3001; b.CapitalStateMapIndex = 3004;
        return (c, a, b);
    }

    static ScenarioState Lose(Campaign c, Nation to)
    {
        var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => c.ProvinceMap.ProvinceToState[p] == 3002).ToArray();
        StatePartition.Annex(c, provinces, to, false);
        return c.States.Single(s => s.MapIndex == 3002);
    }

    public static void Run()
    {
        var calibrated = World(imperialCapacity: 0);
        Check(calibrated.c.Nations.All(n => Math.Abs(StabilityCore.Target(calibrated.c, n) - n.Stability) < .0001f),
            "day-zero homeland precedes stability calibration even for a multiethnic country above imperial capacity");
        var (c, a, b) = World();
        var home = c.States.Single(s => s.MapIndex == 3002);
        Check(c.ElapsedDays == 0 && c.Homelands[a.Id].Contains(home.MapIndex), "initial map remembers home before the first AI action or weekly tick");
        Check(Ultimatum.ProtectsHomelandSale(c, a, home), "an initial minority-culture region has cash-sale protection on day zero");
        c.Homelands.Remove(b.Id);
        c.AttachProvinceTopology(c.ProvinceMap);
        Check(!c.Homelands.ContainsKey(b.Id), "reattaching an already deployed world does not invent missing legacy titles");
        home = Lose(c, b);
        Check(LostLands.Of(c, a).Any(x => x.Region.MapIndex == home.MapIndex), "losing ground before the first week does not erase its homeland title");
        var before = CampaignFingerprint.Of(c);
        var calm = LostLands.Weight(c, a, b);
        Check(calm > home.Population / 12000000f && before == CampaignFingerprint.Of(c), "relative loss matters to a small country and its evaluation is read-only");
        b.Stability = .1f;
        Check(LostLands.Weight(c, a, b) > calm, "a holder's visible domestic crisis creates a stronger opening");
        b.Stability = 1f;
        var third = c.AddNation(new Nation { Tag = "RCC", MapIndex = 503, Population = 1 });
        c.Wars.Add(new StrategicWar { AttackerId = b.Id, DefenderId = third.Id });
        Check(LostLands.Weight(c, a, b) > calm, "a holder distracted by another war creates a stronger opening");
        c.Wars.Clear();
        c.Defines.AiRevancheRelativeWeight = c.Defines.AiRevancheOpportunityWeight = 0;
        Check(Math.Abs(LostLands.Weight(c, a, b) - home.Population / 12000000f) < .0001f, "mods can restore the population-only motive");
        Check(AiStatecraft.TryBuyKindredLand(c, a) && home.NationMapIndex == a.MapIndex,
            "an AI can repurchase its lost civic homeland without sharing its majority culture");

        (c, a, b) = World(); home = Lose(c, b);
        c.Defines.AiPrioritisesLostHomeland = false;
        Check(!AiStatecraft.TryBuyKindredLand(c, a), "a mod can disable culture-independent homeland purchase priority");
        c.Defines.AiPrioritisesLostHomeland = true;
        TerritorialClaims.Renounce(c, a, home);
        Check(LostLands.Weight(c, a, b) == 0 && !AiStatecraft.TryBuyKindredLand(c, a), "an explicitly renounced title supplies neither revenge motive nor recovery purchase");

        (c, a, b) = World(); home = Lose(c, b); c.ConfigureSinglePlayer(b.Id);
        Check(Statecraft.CasusBelli(c, a, b) == "reconquest" && AiStatecraft.TryUltimatumFirst(c, a, b),
            "an existing reconquest aim does not skip the diplomatic ultimatum path");
        Check(home.NationMapIndex == b.MapIndex && Ultimatum.PendingBetween(c, a.Id, b.Id), "the human holder receives a demand, not an automatic territorial transfer");
        Check(AiStatecraft.TryUltimatumFirst(c, a, b), "a pending demand is reused rather than duplicated");
        var restored = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c, "recovery"), "recovery");
        Check(restored.Success && restored.Campaign.Homelands[a.Id].Contains(home.MapIndex), "initial territorial memory survives a real network snapshot: " + restored.Warning);

        MovementOpenings();
        SuccessorCapitulation();
    }

    static void SuccessorCapitulation()
    {
        var (c, a, b) = World(); var home = Lose(c, b);
        var third = c.AddNation(new Nation { Tag = "RCD", MapIndex = 503, Population = 1 });
        b.SecededFromId = third.Id;
        var war = new StrategicWar { Id = 1, AttackerId = a.Id, DefenderId = b.Id, Justification = "reconquest", WarScore = 100 };
        c.Wars.Add(war);
        var ground = Reintegration.CapitulationGround(c, war, b, a);
        Check(ground.Count > 0 && ground.All(p => c.ProvinceMap.ProvinceToState[p] == home.MapIndex),
            "a successor's unconditional surrender returns real homeland, not its unrelated acquisitions");
        var protectedProvince = ground[0];
        c.SetProvinceController(protectedProvince, (ushort)third.MapIndex);
        Check(!Reintegration.CapitulationGround(c, war, b, a).Contains(protectedProvince), "a separate third-party occupation cannot be signed away in someone else's capitulation");
        c.SetProvinceController(protectedProvince, (ushort)b.MapIndex);
        c.Defines.PeaceCapitulationRecoversHomeland = false;
        Check(Reintegration.CapitulationGround(c, war, b, a).Count == 0, "a mod can disable successor homeland recovery on surrender");
        c.Defines.PeaceCapitulationRecoversHomeland = true;
        TerritorialClaims.Renounce(c, a, home);
        Check(Reintegration.CapitulationGround(c, war, b, a).Count == 0, "capitulation respects an explicitly renounced title");
        // A fresh fixture exercises the actual settlement, not only its selection helper.
        (c, a, b) = World(); home = Lose(c, b);
        war = new StrategicWar { Id = 2, AttackerId = a.Id, DefenderId = b.Id, Justification = "reconquest", WarScore = 100 };
        c.Wars.Add(war);
        PeaceNegotiation.Resolve(c, war, b.Id, PeaceTerms.Capitulate);
        Check(war.Ended && war.WinnerId == a.Id && home.NationMapIndex == a.MapIndex && war.AnnexedProvinces > 0,
            "the signed capitulation recovers the entire homeland region even without weeks of secure occupation");
        Check(c.States.Any(s => s.NationMapIndex == b.MapIndex), "recovery is not automatic total annexation of the defeated country");
    }

    static void MovementOpenings()
    {
        var (c, _, host) = World();
        var definition = new MovementDefinition { Id = "recovery_movement", Tag = "RCM" };
        definition.Traits.Add("armed_wing"); definition.Homeland.Add("recovery_2");
        var movement = c.AddNation(new Nation { Tag = definition.Tag,
            Movement = new MovementState { Id = definition.Id, Militancy = .9f } });
        var home = c.States.Single(s => s.Id == "recovery_2");
        var abroad = c.States.Single(s => s.Id == "recovery_3");
        host.Stability = .8f;
        var basic = c.Defines.MovementAiUprisingAppetite;
        Check(MovementAi.UprisingAppetite(c, movement, definition, home, .95f) == basic, "a calm homeland is not a fabricated crisis");
        host.Stability = .1f;
        var before = CampaignFingerprint.Of(c);
        Check(MovementAi.UprisingAppetite(c, movement, definition, home, .95f) > basic && CampaignFingerprint.Of(c) == before,
            "an armed movement notices a credible homeland opening without mutating support or world state");
        Check(MovementAi.UprisingAppetite(c, movement, definition, abroad, .95f) == basic, "a crisis abroad is not a homeland opening");
        Check(MovementAi.UprisingAppetite(c, movement, definition, home, .6f) == basic &&
            MovementAi.UprisingAppetite(c, movement, definition, home, float.NaN) == basic, "a poor or invalid chance does not inflate appetite");
        c.Defines.MovementAiUseHomelandOpenings = false;
        Check(MovementAi.UprisingAppetite(c, movement, definition, home, .95f) == basic, "a mod can disable opening-sensitive uprising appetite");
        c.Defines.MovementAiUseHomelandOpenings = true;
        movement.Movement.Militancy = .1f;
        Check(MovementAi.UprisingAppetite(c, movement, definition, home, .95f) == 0, "peaceful methods do not become armed simply because the host is weak");
    }
}
