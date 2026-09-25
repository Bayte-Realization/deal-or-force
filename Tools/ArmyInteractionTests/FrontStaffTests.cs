using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// The front staff (item 44): a front piled into one province is spread until every post is
/// held, the posts facing the enemy first; a front that is already spread is left alone; an
/// advance stays where it was massed; and a line a player draws is held the same way.
/// </summary>
static class FrontStaffTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260930);
        c.AttachProvinceTopology(map);
        Warfare.RebuildFronts(c);
        var ukr = c.NationByTag("UKR");
        var war = c.Wars.First(candidate => !candidate.Ended && candidate.Involves(ukr.Id) && candidate.Involves(c.NationByTag("RUS").Id));

        var posts = war.FriendlyFront(ukr.Id).Where(province => c.ProvinceController[province] == (ushort)ukr.MapIndex).ToList();
        posts.Sort();
        Check(posts.Count >= 10, $"Ukraine has a front of {posts.Count} posts to hold");

        // --- A: a whole front piled into one province ------------------------------------------
        var defenders = c.Armies.Where(army => army.NationId == ukr.Id && army.TemplateId != MilitiaCore.TemplateId)
            .Take(posts.Count + 6).ToList();
        var pile = posts[0];
        foreach (var army in defenders)
        {
            new SetArmyOrderCommand { IssuerId = ukr.Id, ArmyId = army.Id, Order = ArmyOrder.DefendFront }.Apply(c);
            army.AssignedWarId = war.Id;
            army.FrontLineId = -1;
            army.DirectOrder = false;
            army.ProvinceId = pile;
            army.OrderProvinceId = pile;
            army.Route.Clear();
            army.PlannedRoute.Clear();
            army.BattleId = -1;
        }
        foreach (var army in c.Armies.Where(army => army.NationId == ukr.Id && !defenders.Contains(army)))
            if (army.OrderKind == ArmyOrder.DefendFront) army.OrderKind = ArmyOrder.ReserveFront;

        // A post no road of Ukraine's reaches from the pile - a salient cut off by the line -
        // cannot be walked to by anybody standing in it, and is not counted.
        var reachable = posts.Where(post => post == posts[0] ||
            map.FindRoute(posts[0], post, (ushort)ukr.MapIndex, c.ProvinceController).Length > 0).ToList();
        int Holes() => reachable.Count(post => !defenders.Any(army => army.OrderProvinceId == post));
        int Largest() => posts.Max(post => defenders.Count(army => army.OrderProvinceId == post));
        Check(Holes() == reachable.Count - 1, $"piled: {defenders.Count} divisions on one post, {Holes()} posts held by nobody");

        var looks = 0;
        for (; looks < 30 && Holes() > 0; looks++) FrontManager.Balance(c, war);
        Check(Holes() == 0, $"after {looks} look(s) by the staff every one of the {reachable.Count} posts a road reaches is held");
        Check(Largest() <= Math.Max(3, defenders.Count / 4),
            $"and the largest post holds {Largest()} of {defenders.Count}, not the pile it was");

        // --- B: a front that is held is left alone -----------------------------------------------
        for (var extra = 0; extra < 20; extra++) FrontManager.Balance(c, war);
        var before = FrontManager.Reposts;
        for (var extra = 0; extra < 10; extra++) FrontManager.Balance(c, war);
        Check(FrontManager.Reposts - before <= 2,
            $"once it is held, ten more looks move {FrontManager.Reposts - before} division(s)");

        // --- C: the posts facing the enemy are filled first ----------------------------------------
        var fewer = defenders.Take(posts.Count / 2).ToList();
        foreach (var army in defenders.Skip(fewer.Count)) army.OrderKind = ArmyOrder.ReserveFront;
        var threatened = posts[posts.Count - 1];
        var rus = c.NationByTag("RUS");
        var enemies = c.Armies.Where(army => army.NationId == rus.Id).Take(4).ToList();
        var across = map.Neighbors[threatened].FirstOrDefault(province => c.ProvinceController[province] == (ushort)rus.MapIndex);
        foreach (var army in enemies) army.ProvinceId = across;
        foreach (var army in fewer) { army.ProvinceId = pile; army.OrderProvinceId = pile; army.OrderIssuedDay = -100; }
        for (var look = 0; look < 30; look++) FrontManager.Balance(c, war);
        Check(across <= 0 || fewer.Any(army => army.OrderProvinceId == threatened),
            "with fewer divisions than posts, the post an enemy corps is standing next to is held");

        // --- D: an advance stays where it was massed -----------------------------------------------
        var advancing = fewer[0];
        advancing.OrderKind = ArmyOrder.AdvanceFront;
        advancing.OrderProvinceId = pile;
        foreach (var army in fewer.Skip(1).Take(3)) { army.OrderProvinceId = pile; army.ProvinceId = pile; }
        for (var look = 0; look < 10; look++) FrontManager.Balance(c, war);
        Check(advancing.OrderProvinceId == pile, "a division ordered to advance is not spread along the line");

        // --- E: a drawn line ---------------------------------------------------------------------------
        var p = Campaign.CreateBootstrap(20260930);
        p.AttachProvinceTopology(map);
        Warfare.RebuildFronts(p);
        var player = p.NationByTag("UKR");
        p.ConfigureSinglePlayer(player.Id);
        var playerWar = p.Wars.First(candidate => !candidate.Ended && candidate.Involves(player.Id));
        var line = new DrawnFrontLine { Id = 7001, NationId = player.Id, Kind = FrontLineKind.Defence };
        foreach (var province in playerWar.FriendlyFront(player.Id)
                     .Where(province => p.ProvinceController[province] == (ushort)player.MapIndex).OrderBy(value => value).Take(8))
            line.Provinces.Add(province);
        var linePosts = line.Provinces.ToList();
        var drawnOn = p.Armies.Where(army => army.NationId == player.Id && army.TemplateId != MilitiaCore.TemplateId).Take(10).ToList();
        foreach (var army in drawnOn)
        {
            line.ArmyIds.Add(army.Id);
            army.FrontLineId = line.Id;
            army.OrderKind = ArmyOrder.DefendFront;
            army.AssignedWarId = playerWar.Id;
            army.DirectOrder = false;
            army.ProvinceId = linePosts[0];
            army.OrderProvinceId = linePosts[0];
            army.Route.Clear();
            army.BattleId = -1;
        }
        p.FrontLines.Add(line);
        for (var look = 0; look < 20; look++) FrontManager.Tick(p);
        var unheld = linePosts.Count(post => !drawnOn.Any(army => army.OrderProvinceId == post));
        Check(unheld == 0, $"a line the player drew over {linePosts.Count} provinces is held along all of it ({unheld} unheld)");

        // --- F: a country fighting two wars fights both --------------------------------------------
        var q = Campaign.CreateBootstrap(20260930);
        q.AttachProvinceTopology(map);
        Warfare.RebuildFronts(q);
        var russia = q.NationByTag("RUS");
        var georgia = q.NationByTag("GEO");
        var ukraineWar = q.Wars.First(candidate => !candidate.Ended && candidate.Involves(russia.Id) &&
                                                   candidate.Involves(q.NationByTag("UKR").Id));
        new DeclareWarCampaignCommand { IssuerId = russia.Id, TargetId = georgia.Id }.Apply(q);
        Warfare.RebuildFronts(q);
        var georgianWar = q.Wars.FirstOrDefault(candidate => !candidate.Ended && candidate.Involves(russia.Id) &&
                                                             candidate.Involves(georgia.Id));
        Check(georgianWar is not null, "Russia opens a second war, on Georgia");
        var georgianPosts = georgianWar.FriendlyFront(russia.Id)
            .Where(province => q.ProvinceController[province] == (ushort)russia.MapIndex).ToList();
        Check(georgianPosts.Count > 0, $"and has a front of its own there, {georgianPosts.Count} posts");

        FrontPlanner.AssignAi(q);
        int Serving(StrategicWar served) => q.Armies.Count(army => army.NationId == russia.Id && army.AssignedWarId == served.Id);
        Check(Serving(georgianWar) > 0 && Serving(ukraineWar) > Serving(georgianWar),
            $"the AI splits its army between them: {Serving(ukraineWar)} on Ukraine, {Serving(georgianWar)} on Georgia");

        var served = q.Armies.Where(army => army.NationId == russia.Id && army.AssignedWarId >= 0)
            .ToDictionary(army => army.Id, army => army.AssignedWarId);
        for (var day = 0; day < 3; day++) q.TickDay();
        FrontPlanner.AssignAi(q);
        var switched = q.Armies.Count(army => army.NationId == russia.Id && army.AssignedWarId >= 0 &&
            served.TryGetValue(army.Id, out var before) && before != army.AssignedWarId);
        Check(switched <= Math.Max(2, served.Count / 20),
            $"and the split holds: three days on, {switched} of {served.Count} divisions changed wars");

        // A division released to manual orders in the middle of a war is called up again.
        var idle = q.Armies.First(army => army.NationId == russia.Id && army.AssignedWarId == ukraineWar.Id && !army.InBattle);
        idle.OrderKind = ArmyOrder.Manual;
        idle.AssignedWarId = -1;
        idle.DirectOrder = false;
        idle.Route.Clear();
        FrontManager.Tick(q);
        Check(idle.AssignedWarId >= 0 && idle.OrderKind != ArmyOrder.Manual,
            $"a division left on manual orders in wartime is called up again (now {idle.OrderKind})");

        // An order given without naming the war goes to the front the division stands by.
        var nearGeorgia = q.Armies.First(army => army.NationId == russia.Id && army.AssignedWarId == ukraineWar.Id && !army.InBattle);
        nearGeorgia.ProvinceId = georgianPosts[0];
        nearGeorgia.Route.Clear();
        new SetArmyOrderCommand { IssuerId = russia.Id, ArmyId = nearGeorgia.Id, Order = ArmyOrder.DefendFront }.Apply(q);
        Check(nearGeorgia.AssignedWarId == georgianWar.Id,
            "a division told to hold the line by the Georgian border holds the Georgian front, not the Ukrainian");
    }
}
