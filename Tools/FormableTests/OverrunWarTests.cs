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
