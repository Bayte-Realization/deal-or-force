using System;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// A war ends the week one side has nothing left to fight from (MovementFall.EndLostWars):
/// the state that has taken back every region of its breakaway is not left at war with a
/// country of no ground - and a side whose ally still holds ground fights on.
/// </summary>
static class OverrunWarTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("overrun war: " + note); Console.WriteLine("PASS overrun war: " + note); }

    static Campaign Fixture(bool withAlly)
    {
        var c = new Campaign(20261028) { ScenarioId = "overrun_war_fixture" };
        c.AddNation(new Nation { Tag = "OVP", Name = "Parent", MapIndex = 601, Population = 2000000, Gdp = 200, Treasury = 40 });
        c.AddNation(new Nation { Tag = "OVR", Name = "Rising", MapIndex = 602, Population = 0, Gdp = 10, Treasury = 2 });
        c.AddNation(new Nation { Tag = "OVA", Name = "Friend", MapIndex = 603, Population = 1000000, Gdp = 50, Treasury = 5 });
        c.ConfigureSinglePlayer(0);
        foreach (var (index, owner, tag) in new[] { (4001, 601, "OVP"), (4002, 601, "OVP"), (4003, 603, "OVA") })
            c.States.Add(new ScenarioState { Id = "overrun_" + index, Name = "Region " + index,
                MapIndex = index, NationMapIndex = owner, NationId = tag, Population = 1000000 });
        c.InvalidateStateIndex();
        var war = new StrategicWar { Id = 11, AttackerId = 1, DefenderId = 0, CivilConflict = true, StartedDay = 0 };
        if (withAlly) war.AttackerAllies.Add(2);
        c.Wars.Add(war);
        c.Armies.Add(new Army { Id = 200, NationId = 1, ProvinceId = 0, StateMapIndex = 4002,
            AssignedWarId = 11, Divisions = 1, Strength = 1, Organisation = 1 });
        c.Fallen.Add(1);
        return c;
    }

    public static void Run()
    {
        var c = Fixture(withAlly: false);
        var war = c.Wars.Single();
        Check(c.StatesOf(c.Nations[1]).Length == 0 && !war.Ended, "the rising holds no region and its war is still open");
        MovementFall.EndLostWars(c);
        Check(war.Ended && war.WinnerId == 0, "the war ends that week, won by the state that took the ground back");
        Check(!c.Armies.Any(a => a.NationId == 1), "what was left of the rising's army laid down its arms");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "peace_signed" && e.ActorId == 0 && e.TargetId == 1),
            "the end is in the chronicle");

        var coalition = Fixture(withAlly: true);
        MovementFall.EndLostWars(coalition);
        Check(!coalition.Wars.Single().Ended, "a side whose ally still holds ground fights on");

        var twin = Fixture(withAlly: false);
        MovementFall.EndLostWars(twin);
        Check(CampaignFingerprint.Of(c) == CampaignFingerprint.Of(twin), "the surrender is deterministic");

        Underground();
        ForcedPeaceOnTotalOccupationAndFullScore();
    }

    /// <summary>
    /// When ForcePeaceOnTotalOccupationOrFullScore is true, 100% occupation or 100% score ends wars decisive-day.
    /// </summary>
    static void ForcedPeaceOnTotalOccupationAndFullScore()
    {
        var cells = new ushort[] { 1001, 1002, 1003 };
        var owners = new ushort[] { 501, 502, 503 };

        // 1. 100%-occupied AI leader immediately settles peace (even with surviving army and before AiMinimumWarDays)
        {
            var c = new Campaign(42);
            var a = new Nation { Tag = "ATK", MapIndex = 501, Name = "Attacker", Population = 1000000 };
            var d = new Nation { Tag = "DEF", MapIndex = 502, Name = "Defender", Population = 1000000 };
            c.AddNation(a);
            c.AddNation(d);
            var sa = new ScenarioState { MapIndex = 1001, NationMapIndex = 501, NationId = "ATK", Population = 1000000 };
            var sd = new ScenarioState { MapIndex = 1002, NationMapIndex = 502, NationId = "DEF", Population = 1000000 };
            c.States.Add(sa);
            c.States.Add(sd);
            c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002 }, new ushort[] { 501, 502 }, 2, 1, _ => false));
            // Occupy defender province (index 2) by attacker (501)
            c.ProvinceController[2] = 501;

            var war = new StrategicWar { Id = 501, AttackerId = a.Id, DefenderId = d.Id, StartedDay = c.ElapsedDays };
            c.Wars.Add(war);
            // DEF still has an active army in the field
            c.Armies.Add(new Army { Id = 901, NationId = d.Id, ProvinceId = 0, StateMapIndex = 1002, Divisions = 5, Strength = 10f, Organisation = 1f });

            Check(PeaceNegotiation.IsTotallyOccupiedInWar(c, war, d), "defender is totally occupied");
            AiStatecraft.Negotiate(c, a, war);
            Check(war.Ended && war.WinnerId == a.Id, "100%-occupied AI leader immediately settles decisive peace");
        }

        // 2. 100% war score between AI leaders immediately settles peace
        {
            var c = new Campaign(43);
            var a = new Nation { Tag = "AT2", MapIndex = 501, Name = "Attacker2", Population = 1000000 };
            var d = new Nation { Tag = "DE2", MapIndex = 502, Name = "Defender2", Population = 1000000 };
            c.AddNation(a);
            c.AddNation(d);
            var sa = new ScenarioState { MapIndex = 1001, NationMapIndex = 501, NationId = "AT2", Population = 1000000 };
            var sd = new ScenarioState { MapIndex = 1002, NationMapIndex = 502, NationId = "DE2", Population = 1000000 };
            c.States.Add(sa);
            c.States.Add(sd);
            c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002 }, new ushort[] { 501, 502 }, 2, 1, _ => false));

            var war = new StrategicWar { Id = 502, AttackerId = a.Id, DefenderId = d.Id, StartedDay = c.ElapsedDays, WarScore = 100f };
            c.Wars.Add(war);

            AiStatecraft.Negotiate(c, a, war);
            Check(war.Ended && war.WinnerId == a.Id, "100% war score between AI leaders immediately settles peace");
        }

        // 3. 100%-occupied junior coalition partner immediately signs separate peace
        {
            var c = new Campaign(44);
            var a = new Nation { Tag = "AT3", MapIndex = 501, Name = "Attacker3", Population = 1000000 };
            var d = new Nation { Tag = "DE3", MapIndex = 502, Name = "Defender3", Population = 1000000 };
            var j = new Nation { Tag = "JUN", MapIndex = 503, Name = "JuniorAlly", Population = 500000 };
            c.AddNation(a);
            c.AddNation(d);
            c.AddNation(j);
            var sa = new ScenarioState { MapIndex = 1001, NationMapIndex = 501, NationId = "AT3", Population = 1000000 };
            var sd = new ScenarioState { MapIndex = 1002, NationMapIndex = 502, NationId = "DE3", Population = 1000000 };
            var sj = new ScenarioState { MapIndex = 1003, NationMapIndex = 503, NationId = "JUN", Population = 500000 };
            c.States.Add(sa);
            c.States.Add(sd);
            c.States.Add(sj);
            c.AttachProvinceTopology(ProvinceTopology.Build(cells, owners, 3, 1, _ => false));
            // Occupy junior ally's province (whichever province is owned by j.MapIndex)
            for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
            {
                if (c.ProvinceMap.ProvinceToNation[p] == j.MapIndex)
                    c.ProvinceController[p] = 501;
            }

            var war = new StrategicWar { Id = 503, AttackerId = a.Id, DefenderId = d.Id, StartedDay = c.ElapsedDays };
            war.DefenderAllies.Add(j.Id);
            c.Wars.Add(war);
            c.StepOpensNewDay = true;

            Check(PeaceNegotiation.IsTotallyOccupiedInWar(c, war, j), "junior ally is totally occupied");
            SeparatePeace.Tick(c);
            Check(!war.DefenderAllies.Contains(j.Id), "100%-occupied junior partner immediately concludes separate peace");
        }

        // 4. Human victor immediately receives enemy capitulation offer without auto-closing silently
        {
            var c = new Campaign(45);
            var human = new Nation { Tag = "HUM", MapIndex = 501, Name = "HumanVictor", Population = 1000000, IsHuman = true };
            var enemy = new Nation { Tag = "ENM", MapIndex = 502, Name = "EnemyBeaten", Population = 1000000 };
            c.AddNation(human);
            c.AddNation(enemy);
            c.ConfigureSinglePlayer(human.Id);
            var sh = new ScenarioState { MapIndex = 1001, NationMapIndex = 501, NationId = "HUM", Population = 1000000 };
            var se = new ScenarioState { MapIndex = 1002, NationMapIndex = 502, NationId = "ENM", Population = 1000000 };
            c.States.Add(sh);
            c.States.Add(se);
            c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[] { 1001, 1002 }, new ushort[] { 501, 502 }, 2, 1, _ => false));
            // Occupy enemy province (index 2)
            c.ProvinceController[2] = 501;

            var war = new StrategicWar { Id = 504, AttackerId = human.Id, DefenderId = enemy.Id, StartedDay = c.ElapsedDays };
            c.Wars.Add(war);

            AiStatecraft.Negotiate(c, enemy, war);
            Check(!war.Ended, "war with human victor does not silently auto-close without player receipt");
            Check(war.PendingOfferFromId == enemy.Id, "human victor immediately receives enemy capitulation offer");
            Check(war.PendingOfferTerms == PeaceTerms.Capitulate, "offer received is capitulation");
        }
    }

    /// <summary>A beaten rising goes to ground in its furious homeland and can rise again from it.</summary>
    static void Underground()
    {
        var c = Fixture(withAlly: false);
        var rising = c.Nations[1];
        rising.SecededFromId = 0;
        c.Homelands[rising.Id] = new() { 4002 };
        var home = c.States.Single(s => s.MapIndex == 4002);
        home.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = 1000000 });
        home.Radicalism = 0.1f;
        var calm = MovementFall.UndergroundChance(c, rising, null);
        home.Radicalism = 0.6f;
        var furious = MovementFall.UndergroundChance(c, rising, null);
        Check(furious > calm && furious <= 0.95f, "a furious homeland hides its fighters more readily than a calm one");

        MovementFall.GoUnderground(c, rising);
        var state = rising.Movement;
        Check(state is { Landless: true } && state.Id == "underground_ovr" && state.HeadquartersStateMapIndex == 4002,
            "it becomes a landless movement with its headquarters in its own homeland");
        Check(state.Peoples.Contains("english") && state.Support[4002] > 0.5f && state.Cadres >= 1500f,
            "it keeps its people, a following as deep as the region is radical, and its fighters as cadres");
        Check(!c.Armies.Any(a => a.NationId == rising.Id) && rising.Population == 0,
            "its army went home with its rifles rather than into captivity");
        Check(Movements.DefinitionOf(rising) is { } d && d.Has("armed_wing") && MovementMethods.WillRise(c, rising, d),
            "it is armed and will rise again");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "went_underground" && e.ActorId == rising.Id), "the world hears of it");
        MovementFall.EndLostWars(c);
        Check(c.Wars.Single().Ended, "the war with the state it lost to is over all the same");
    }
}
