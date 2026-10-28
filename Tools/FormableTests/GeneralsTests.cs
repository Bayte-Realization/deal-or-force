using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>Commanders (Generals.cs): appointed from the seed, felt in battle, made and broken by it.</summary>
static class GeneralsTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("generals: " + note); Console.WriteLine("PASS generals: " + note); }

    public static void Run()
    {
        Generals.ResetCatalog();
        Check(Generals.All.Count >= 6 && Generals.All.Any(t => t.Attack > 1.1f) && Generals.All.Any(t => t.Defence > 1.1f),
            "the temperaments come from common/generals");

        var c = Campaign.CreateBootstrap(20261028);
        var groups = c.ArmyGroups.Where(g => c.NationById(g.NationId)?.Tag == "UKR").ToList();
        Check(groups.Count > 0 && groups.All(g => g.Commander != Generals.Unassigned && g.CommanderTrait.Length > 0 &&
                                               g.StaffSkill is >= 0.2f and <= 0.8f),
            "every army group opens the campaign with a named commander, a skill and a temperament");
        var twin = Campaign.CreateBootstrap(20261028);
        Check(c.ArmyGroups.Select(g => g.Commander + g.CommanderTrait + g.StaffSkill)
                .SequenceEqual(twin.ArmyGroups.Select(g => g.Commander + g.CommanderTrait + g.StaffSkill)),
            "the same seed appoints the same commanders");
        var other = Campaign.CreateBootstrap(20261029);
        Check(!c.ArmyGroups.Select(g => g.CommanderTrait + g.StaffSkill).SequenceEqual(other.ArmyGroups.Select(g => g.CommanderTrait + g.StaffSkill)),
            "another seed appoints others");

        var group = groups[0];
        var army = c.Armies.First(a => a.ArmyGroupId == group.Id);
        group.StaffSkill = 0.9f; group.CommanderTrait = "steady";
        var good = Generals.Factors(c, army);
        group.StaffSkill = 0.2f;
        var poor = Generals.Factors(c, army);
        Check(good.Attack > 1.15f && poor.Attack < 0.9f, "a skilled commander's formations hit harder than a poor one's");

        var stray = c.Armies.First(a => a.NationId == army.NationId && a.Id != army.Id);
        stray.ArmyGroupId = -1;
        Check(Generals.GroupOf(c, stray) is { } joined && joined.NationId == stray.NationId && stray.ArmyGroupId == joined.Id,
            "a formation without a headquarters joins one of its country's");

        var enemy = c.Armies.First(a => a.NationId != army.NationId);
        var battle = new LandBattle { Id = 77, AttackerId = army.NationId, DefenderId = enemy.NationId,
            ProvinceId = army.ProvinceId, AttackerLosses = 12000, DefenderLosses = 9000 };
        battle.Attackers.Add(army.Id); battle.Defenders.Add(enemy.Id);
        var loserGroup = Generals.GroupOf(c, enemy);
        Generals.EnsureCommander(c, loserGroup);
        var before = (group.Victories, loserGroup.Defeats, group.StaffSkill);
        c.Defines.GeneralFallChance = 1f;
        var fallen = loserGroup.Commander;
        Generals.AfterBattle(c, battle, battle.AttackerId, broken: true);
        Check(group.Victories == before.Victories + 1 && group.StaffSkill > before.StaffSkill,
            "a victory is on the winner's record and teaches them");
        Check(loserGroup.CommandersLost == 1 && loserGroup.Commander != fallen &&
              c.Chronicle.Entries.Any(e => e.Kind == "general_fallen" && e.Subject == fallen),
            "a commander whose side broke can fall, and somebody new takes the group");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "great_battle" && e.ActorId == battle.AttackerId),
            "a battle that cost twenty thousand men is remembered by name");
    }
}

/// <summary>Desertion (Desertion.cs): a beaten, hungry army bleeds men, some of them to the enemy.</summary>
static class DesertionTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("desertion: " + note); Console.WriteLine("PASS desertion: " + note); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261028);
        var russia = c.NationByTag("RUS"); var ukraine = c.NationByTag("UKR");
        var war = c.Wars.First(w => w.Involves(russia.Id) && w.Involves(ukraine.Id));
        var army = c.Armies.First(a => a.NationId == russia.Id && a.Personnel > 1000);
        army.AssignedWarId = war.Id;
        army.Cohesion = 0.2f; army.SupplyLevel = 0.1f; army.Organisation = 0.1f; russia.WarSupport = 0.1f;
        var men = army.Personnel; var enemyReserve = ukraine.ManpowerReserve;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 3L * 24);
        Desertion.Tick(c);
        Check(army.Personnel < men, "a starving, beaten formation in an unpopular war loses men");
        Check(ukraine.ManpowerReserve > enemyReserve, "and the enemy takes some of them into its ranks");
        var civil = new StrategicWar { CivilConflict = true, AttackerId = russia.Id, DefenderId = ukraine.Id };
        Check(Desertion.ShareToEnemy(c, civil, russia, ukraine) > Desertion.ShareToEnemy(c, war, russia, ukraine),
            "in a civil war deserters cross to the other side far more readily");
    }
}

/// <summary>A coup can split the army into a civil war, whatever the stability (CivilWarOutbreak.FromCoup).</summary>
static class CoupCivilWarTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("coup civil war: " + note); Console.WriteLine("PASS coup civil war: " + note); }

    static Campaign Fixture()
    {
        var c = new Campaign(20261028) { ScenarioId = "coup_civil_war_fixture" };
        var parent = new Nation { Id = 0, MapIndex = 500, Tag = "TST", Name = "Test country", Population = 20_000_000,
            Treasury = 20, Gdp = 100, OutputScale = 1f, Stability = .6f, CapitalStateMapIndex = 3001 };
        c.Nations.Add(parent);
        for (var i = 0; i < 4; i++)
            c.States.Add(new ScenarioState { MapIndex = 3001 + i, Id = "coup_" + i, Name = "Region " + i,
                NationMapIndex = parent.MapIndex, NationId = parent.Tag, Population = 5_000_000,
                StandardOfLiving = .4f, Literacy = .8f, Urbanization = .5f, Radicalism = .4f });
        var cells = Enumerable.Range(0, 8192).Select(i => (ushort)(3001 + i % 128 / 32)).ToArray();
        var flags = Enumerable.Repeat((ushort)500, 8192).ToArray();
        c.AttachProvinceTopology(ProvinceTopology.Build(cells, flags, 128, 64, _ => false));
        SovereignFinance.NormalizeAccounts(c);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (long)(c.Defines.CivilWarEarliestDay + 10) * 24);
        return c;
    }

    public static void Run()
    {
        var c = Fixture();
        var nation = c.Nations[0];
        Check(CivilWarOutbreak.EligibilityReason(c, nation).Length > 0, "the country is above the stability line");
        Check(CivilWarOutbreak.FromCoup(c, nation, 0f, 1) is null, "a coup that takes the army whole starts no war");
        var report = CivilWarOutbreak.FromCoup(c, nation, 1f, 1);
        Check(report?.Seceded is not null && c.Wars.Any(w => !w.Ended && w.CivilConflict && w.Involves(nation.Id)),
            "a split army is a civil war from that day, above the stability line too");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "coup_civil_war" && e.ActorId == nation.Id), "and the world hears the army has split");
        Check(CivilWarOutbreak.FromCoup(c, nation, 1f, 1) is null, "one civil war at a time");
    }
}

/// <summary>World layers (common/world_layers): every layer on unless a file says otherwise, unknown keys reported.</summary>
static class WorldLayersTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("world layers: " + note); Console.WriteLine("PASS world layers: " + note); }

    public static void Run()
    {
        var errors = new List<string>();
        WorldLayers.ResetCatalog(errors);
        Check(errors.Count == 0 && WorldLayers.Known.Keys.All(WorldLayers.On), "the shipped file names only real layers, all on");
        using (var document = System.Text.Json.JsonDocument.Parse("{\"trade_traffic\": false, \"steam_trains\": true, \"weather\": 3}"))
            WorldLayers.Merge(document.RootElement, errors);
        Check(!WorldLayers.On("trade_traffic") && WorldLayers.On("weather"), "a mod switches a layer off by naming it");
        Check(errors.Count == 2, "a layer nobody draws and a value that is not true or false are reported");
        WorldLayers.ResetCatalog();
        Check(WorldLayers.On("trade_traffic"), "and a reload restores the shipped layers");
    }
}
