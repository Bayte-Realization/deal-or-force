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

        Relief();
    }

    /// <summary>Relieving a commander: the officers put forward, the price, the wait, and the computer's governments.</summary>
    static void Relief()
    {
        var c = Campaign.CreateBootstrap(20261029);
        var ukraine = c.NationByTag("UKR");
        var group = c.ArmyGroups.First(g => g.NationId == ukraine.Id);
        Generals.EnsureCommander(c, group);
        var candidates = Generals.Candidates(c, group);
        Check(candidates.Length == c.Defines.CommanderCandidates && candidates.Select(o => o.Name).Distinct().Count() == candidates.Length &&
              candidates.All(o => o.Name != group.Commander),
            "three different officers are put forward, none of them the commander in post");
        Check(Generals.Candidates(c, group).Select(o => o.Name + o.Trait + o.Skill).SequenceEqual(candidates.Select(o => o.Name + o.Trait + o.Skill)),
            "the same officers are put forward until somebody is relieved");

        ukraine.PoliticalPoints = 50f;
        group.CommanderSinceDay = 0;
        Check(Generals.MayRelieve(c, ukraine, group, out _),
            "the commanders the campaign opened with can be changed at once");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 200L * 24);
        group.CommanderSinceDay = c.ElapsedDays - 10;
        Check(!Generals.MayRelieve(c, ukraine, group, out var wait) && wait.Length > 0,
            "a commander the government has just put in cannot be relieved at once");
        group.CommanderSinceDay = c.ElapsedDays - c.Defines.CommanderReliefCooldownDays;
        group.Victories = 5; group.Defeats = 1;
        var support = ukraine.WarSupport;
        var expected = Generals.ReliefWarSupportCost(c, group);
        Check(expected > 0f, "Ukraine is at war, so removing a victorious commander costs war support");
        c.Commands.Enqueue(new RelieveCommanderCommand { IssuerId = ukraine.Id, GroupId = group.Id, Candidate = 1 });
        c.Commands.ApplyAll(c);
        Check(group.Commander == candidates[1].Name && group.CommanderTrait == candidates[1].Trait &&
              group.Victories == 0 && group.CommandersRelieved == 1,
            "the chosen officer takes the group with a clean record");
        Check(Math.Abs(ukraine.PoliticalPoints - (50f - c.Defines.CommanderReliefPoliticalCost)) < 0.001f &&
              ukraine.WarSupport < support - expected + 0.0001f,
            "the relief is paid in political credit, and the victor's removal in war support");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "general_relieved" && e.ActorId == ukraine.Id),
            "the world hears of it");
        Check(!Generals.Candidates(c, group).Select(o => o.Name).SequenceEqual(candidates.Select(o => o.Name)),
            "after a relief, other officers are put forward");

        // The computer's governments, by the same rule.
        var russia = c.NationByTag("RUS");
        var losing = c.ArmyGroups.First(g => g.NationId == russia.Id);
        Generals.EnsureCommander(c, losing);
        losing.StaffSkill = 0.2f; losing.Victories = 0; losing.Defeats = 4;
        losing.CommanderSinceDay = -1000;
        russia.PoliticalPoints = 50f; russia.IsHuman = false;
        var loser = losing.Commander;
        c.Defines.AiRelievesFailedCommanders = true;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (28L * 8 + 19) * 24);
        Generals.AiMonth(c);
        Check(losing.Commander != loser && losing.CommandersRelieved == 1 && losing.StaffSkill > 0.2f,
            "a government the computer runs relieves the commander who keeps losing and picks an abler one");
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

/// <summary>Diplomatic gestures (DiplomaticGestures.cs): visits, shows of force, mediation and loans.</summary>
static class DiplomaticGestureTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("diplomatic gestures: " + note); Console.WriteLine("PASS diplomatic gestures: " + note); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261028);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        var france = c.NationByTag("FRA"); var germany = c.NationByTag("DEU");
        france.PoliticalPoints = 100f; france.Treasury = 100f;

        var before = CampaignDiplomacy.Get(c, france.Id, germany.Id).Opinion;
        var visit = new DiplomaticGestureCommand { IssuerId = france.Id, TargetId = germany.Id, Gesture = "state_visit" };
        visit.Apply(c);
        Check(visit.Done && CampaignDiplomacy.Get(c, france.Id, germany.Id).Opinion > before && france.PoliticalPoints < 100f,
            "a state visit costs political credit and warms the relation");
        Check(DiplomaticGestures.Refusal(c, france.Id, germany.Id, "state_visit").Length > 0, "and is not repeated the next day");
        Check(c.Chronicle.Entries.Any(e => e.Kind == "state_visit" && e.ActorId == france.Id), "the visit is news");

        Check(DiplomaticGestures.Refusal(c, france.Id, c.NationByTag("JPN").Id, "show_of_force").Length > 0,
            "troops can only be massed on a border we share");

        var poor = c.NationByTag("MDA") ?? c.NationByTag("ALB");
        CampaignDiplomacy.Get(c, germany.Id, poor.Id).Opinion = 40f;
        germany.Treasury = 500f;
        var amount = DiplomaticGestures.LoanSize(c, germany, poor);
        var poorCash = poor.Treasury;
        Check(DiplomaticGestures.Perform(c, germany.Id, poor.Id, "loan") && poor.Treasury > poorCash && amount > 0f,
            "a loan moves money from the lender's treasury to the borrower's");
        var relation = CampaignDiplomacy.Get(c, germany.Id, poor.Id);
        Check(relation.LoanLender == germany.Id && relation.LoanOutstanding > amount, "and is owed back with interest");
        poor.Treasury = 100f;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 6L * 24);
        var owed = relation.LoanOutstanding;
        DiplomaticGestures.Tick(c);
        Check(relation.LoanOutstanding < owed, "an instalment is paid each week");
        poor.Treasury = -1f;
        for (var week = 1; week <= c.Defines.LoanDefaultWeeks; week++)
        {
            typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, (6L + 7L * week) * 24);
            DiplomaticGestures.Tick(c);
        }
        Check(relation.LoanLender < 0 && c.Chronicle.Entries.Any(e => e.Kind == "loan_default" && e.ActorId == poor.Id),
            "an empty treasury for weeks on end is a default the lender remembers");

        var russia = c.NationByTag("RUS"); var ukraine = c.NationByTag("UKR"); var turkey = c.NationByTag("TUR");
        turkey.PoliticalPoints = 100f;
        CampaignDiplomacy.Get(c, turkey.Id, russia.Id).Opinion = 20f;
        CampaignDiplomacy.Get(c, turkey.Id, ukraine.Id).Opinion = 20f;
        Check(DiplomaticGestures.MediableWar(c, turkey, ukraine) is not null &&
              DiplomaticGestures.Refusal(c, turkey.Id, ukraine.Id, "mediate").Length == 0,
            "a country both sides think well of may offer to mediate their war");
        var mediation = new DiplomaticGestureCommand { IssuerId = turkey.Id, TargetId = ukraine.Id, Gesture = "mediate" };
        mediation.Apply(c);
        Check(turkey.PoliticalPoints < 100f && (mediation.Done || mediation.Refusal.Length > 0),
            "the offer is made and paid for, whether or not the sides are ready");

        // Expelling diplomats: free, loud, remembered.
        var poland = c.NationByTag("POL");
        var tie = CampaignDiplomacy.Get(c, poland.Id, russia.Id);
        var tension = tie.Tension;
        var support = poland.WarSupport = 0.5f;
        Check(DiplomaticGestures.Perform(c, poland.Id, russia.Id, "expel_diplomats") && tie.Tension > tension &&
              poland.WarSupport > support && tie.Reasons.Any(r => r.Id == "expelled_diplomats"),
            "expelling their diplomats raises the tension, cheers the hawks and is remembered");

        // Joint exercises: a message to the rival both partners share.
        var lithuania = c.NationByTag("LTU");
        CampaignDiplomacy.Get(c, poland.Id, lithuania.Id).Stance = DiplomaticStance.Allied;
        CampaignDiplomacy.Get(c, poland.Id, lithuania.Id).Opinion = 60f;
        CampaignDiplomacy.Get(c, lithuania.Id, russia.Id).Stance = DiplomaticStance.Rival;
        CampaignDiplomacy.Get(c, poland.Id, russia.Id).Stance = DiplomaticStance.Rival;
        poland.PoliticalPoints = 100f; poland.Treasury = 100f;
        var rivalTension = CampaignDiplomacy.Get(c, lithuania.Id, russia.Id).Tension;
        Check(DiplomaticGestures.CommonRivals(c, poland, lithuania).Contains(russia), "the partners' common rival is known");
        Check(DiplomaticGestures.Perform(c, poland.Id, lithuania.Id, "joint_exercises") &&
              CampaignDiplomacy.Get(c, lithuania.Id, russia.Id).Tension > rivalTension,
            "joint exercises warm the partners and raise the tension with their common rival");

        // Producers answer the price: a glut idles plant, a shortage runs it hard.
        ProductionChains.Defines = c.Defines;
        var glut = new MarketQuote { Commodity = Commodity.Machinery, Price = CommodityCatalog.Get(Commodity.Machinery).BasePrice * 0.4f };
        var dear = new MarketQuote { Commodity = Commodity.Machinery, Price = CommodityCatalog.Get(Commodity.Machinery).BasePrice * 2f };
        Check(ProductionChains.SupplyResponse(france, glut) < 0.7f && ProductionChains.SupplyResponse(france, dear) > 1.05f &&
              ProductionChains.SupplyResponse(france, dear) <= c.Defines.SupplyResponseMaximum + 1e-5f,
            "a glutted good is made less of and a dear one more, within the bounds");
    }
}

/// <summary>A customs union (organisation doctrine "common_market"): its members buy from each other first.</summary>
static class CustomsUnionTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("customs union: " + note); Console.WriteLine("PASS customs union: " + note); }

    static float InsideShare(Campaign c, HashSet<int> members)
    {
        MarketSystem.Prime(c);
        var inside = 0f; var all = 0f;
        foreach (var route in c.TradeRoutes)
        {
            if (!members.Contains(route.BuyerId)) continue;
            all += route.Value;
            if (members.Contains(route.SellerId)) inside += route.Value;
        }
        return all <= 0f ? 0f : inside / all;
    }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261029);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        var eu = c.Organizations.FirstOrDefault(o => o.Name == "European Union");
        Check(eu is not null && eu.DoctrineId == c.Defines.CustomsUnionDoctrine, "the EU opens as a customs union");
        var members = eu.Members.ToHashSet();
        var union = InsideShare(c, members);
        eu.DoctrineId = "";
        var plain = InsideShare(c, members);
        Check(union > plain, $"its members buy more of what they import from each other ({union:P0} against {plain:P0})");
    }
}

/// <summary>Membership crises (OrganizationCrises.cs): bargaining before leaving, concessions, the vote.</summary>
static class OrganizationCrisisTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("organization crisis: " + note); Console.WriteLine("PASS organization crisis: " + note); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261029);
        c.Defines.OrganizationCrisisDays = 180;
        c.Defines.AiConcessionChance = 0f;
        var eu = c.Organizations.First(o => o.Name == "European Union");
        var hungary = c.NationByTag("HUN"); var poland = c.NationByTag("POL"); var germany = c.NationByTag("DEU");
        hungary.IsHuman = false; poland.IsHuman = false; germany.IsHuman = false;

        Check(OrganizationCrises.Open(c, eu, hungary) && eu.CrisisMemberId == hungary.Id &&
              c.Chronicle.Entries.Any(e => e.Kind == "organization_crisis" && e.ActorId == hungary.Id),
            "a member that would leave opens a crisis instead and the world hears of it");
        Check(!OrganizationCrises.Open(c, eu, poland) && eu.CrisisMemberId == hungary.Id,
            "one crisis at a time: a second estranged member would simply leave");

        var cost = OrganizationCrises.ConcessionCost(c, hungary);
        var berlin = germany.Treasury; var budapest = hungary.Treasury;
        var before = OrganizationDynamics.Belonging(c, eu, hungary);
        c.Commands.Enqueue(new OrganizationCommand { IssuerId = germany.Id, OrganizationId = eu.Id, Action = "concede" });
        c.Commands.ApplyAll(c);
        Check(eu.CrisisMemberId < 0 && Math.Abs(germany.Treasury - (berlin - cost)) < 0.001f &&
              Math.Abs(hungary.Treasury - (budapest + cost)) < 0.001f,
            "a concession is paid by the giver to the bargaining member and ends the crisis");
        Check(OrganizationDynamics.Belonging(c, eu, hungary) > before - 0.0001f && OrganizationCrises.ConcessionBelonging(c, eu, hungary) > 0.17f,
            "the member bought back belongs more");
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 365L * 6 * 24);
        Check(OrganizationCrises.ConcessionBelonging(c, eu, hungary) < 0.05f, "and the concession fades over the years: a quarter after six years");

        c.Commands.Enqueue(new OrganizationCommand { IssuerId = poland.Id, OrganizationId = eu.Id, Action = "demand_terms" });
        c.Commands.ApplyAll(c);
        Check(eu.CrisisMemberId == poland.Id, "a member can open a crisis of its own by demanding better terms");
        var stay = c.Chronicle.Entries.Count(e => e.Kind == "organization_crisis_passed");
        eu.CrisisSinceDay = c.ElapsedDays - 200;
        OrganizationCrises.Week(c, eu, _ => 0.9f, _ => throw new Exception("must not leave"));
        Check(eu.CrisisMemberId < 0 && eu.Members.Contains(poland.Id) &&
              c.Chronicle.Entries.Count(e => e.Kind == "organization_crisis_passed") == stay + 1,
            "a member that belongs again by the vote stays");
        Check(InternationalOrganizations.Refusal(c, new OrganizationCommand { IssuerId = poland.Id, OrganizationId = eu.Id, Action = "demand_terms" }).Length > 0,
            "better terms cannot be demanded again at once");

        OrganizationCrises.Open(c, eu, hungary);
        eu.CrisisSinceDay = c.ElapsedDays - 200;
        var left = false;
        OrganizationCrises.Week(c, eu, _ => 0.05f, m => { left = m.Id == hungary.Id; eu.Members.Remove(m.Id); });
        Check(left && c.Chronicle.Entries.Any(e => e.Kind == "organization_exit_vote" && e.ActorId == hungary.Id),
            "a member nobody bought back, still estranged at the vote, leaves");

        c.Defines.AiConcessionChance = 1f;
        c.Defines.AiConcessionValueShare = 0f;
        var leader = InternationalOrganizations.Leader(c, eu);
        leader.IsHuman = false; leader.Treasury = 1000f;
        var bargainer = eu.Members.Select(c.NationById).First(n => n.Id != leader.Id);
        bargainer.IsHuman = false;
        OrganizationCrises.Open(c, eu, bargainer);
        OrganizationCrises.Week(c, eu, _ => 0.05f, _ => throw new Exception("must not leave"));
        Check(eu.CrisisMemberId < 0 && bargainer.OrganizationConcessions.ContainsKey(eu.Id),
            "the computer's leader buys back a member worth keeping, by the same rule");
    }
}

/// <summary>A state that threw off its overlord is not the rump of a rising (Factions.SettleDefenselessRebelRemnants).</summary>
static class SovereignRemnantTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("sovereign remnant: " + note); Console.WriteLine("PASS sovereign remnant: " + note); }

    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261029);
        MapRasterCatalog.ResetCatalog(); TerrainCatalog.ResetCatalog();
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var map, out var message), message);
        c.AttachProvinceTopology(map);
        var austria = c.NationByTag("AUT"); var liechtenstein = c.NationByTag("LIE");
        Check(austria is not null && liechtenstein is not null && c.StatesOf(liechtenstein).Length > 0, "Austria and Liechtenstein are on the map");
        liechtenstein.IsHuman = false;
        liechtenstein.SecededFromId = austria.Id;
        c.Armies.RemoveAll(a => a.NationId == liechtenstein.Id);
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 40L * 24);
        Factions.SettleDefenselessRebelRemnants(c);
        Check(c.HasOwnedLand(liechtenstein) && c.StatesOf(liechtenstein).All(s => s.NationId == "LIE"),
            "Liechtenstein, free of Austria and with no army, keeps its own ground");
    }
}
