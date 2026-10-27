using System;
using System.Linq;
using DealOrForce.Simulation;

static class AiWarTests
{
    static int failures;
    static void Check(bool ok, string label)
    {
        Console.WriteLine((ok ? "PASS " : "FAIL ") + label);
        if (!ok) failures++;
    }

    public static void Run(ProvinceTopology map)
    {
        var c = Campaign.CreateBootstrap(20260927);
        c.AttachProvinceTopology(map);
        c.Wars.Clear(); c.Battles.Clear();
        var north = c.NationByTag("PRK"); var south = c.NationByTag("KOR");
        north.IsHuman = false; south.IsHuman = true;
        var unit = c.Armies.First(a => a.NationId == north.Id && a.TemplateId != MilitiaCore.TemplateId);
        c.Armies.RemoveAll(a => a != unit);
        var war = new StrategicWar { Id = 700, AttackerId = north.Id, DefenderId = south.Id };
        c.Wars.Add(war);
        Warfare.RebuildFronts(c);
        FrontPlanner.ReassignAi(c, war);
        Check(unit.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead,
            "one-division attacker retains an offensive role even with defensive doctrine");
        unit.OrderKind = ArmyOrder.AdvanceFront; unit.AssignedWarId = -1; unit.Route.Clear();
        AiStrategyCatalog.Load("""{"profiles":{"test":{"advance_share_attacker":1,"spearhead_share":0,"defend_share":0}},"country_profiles":{"PRK":"test"}}""", null);
        FrontPlanner.ReassignAi(c, war);
        Check(unit.AssignedWarId == war.Id && unit.OrderKind == ArmyOrder.AdvanceFront,
            "same role attaches to war and zero spearhead share is respected");

        // A static front must not turn every replacement into a permanent defender.
        unit.OrderKind = ArmyOrder.Manual; unit.AssignedWarId = -1; unit.DirectOrder = false;
        unit.Route.Clear(); unit.PlannedRoute.Clear(); unit.FrontLineId = -1;
        FrontManager.Tick(c);
        Check(unit.AssignedWarId == war.Id && unit.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead,
            "idle replacement is assigned a missing offensive role on a static front");
        AiStrategyCatalog.ResetCatalog();

        // Summoning a landing force must not be blocked by an unrelated army on a march.
        var sea = Campaign.CreateBootstrap(20260927);
        sea.AttachProvinceTopology(map);
        sea.Wars.Clear(); sea.Battles.Clear();
        var britain = sea.NationByTag("GBR"); var france = sea.NationByTag("FRA");
        britain.IsHuman = false;
        var fleet = sea.Fleets.First(f => f.NationId == britain.Id);
        sea.Fleets.RemoveAll(f => f != fleet);
        fleet.Strength = fleet.Supply = fleet.Organisation = 1;
        sea.Defines.AiFleetDecisionInterval = 1;
        sea.Wars.Add(new StrategicWar { Id = 701, AttackerId = britain.Id, DefenderId = france.Id });
        var pair = sea.Armies.Where(a => a.NationId == britain.Id && a.TemplateId != MilitiaCore.TemplateId).Take(2).ToArray();
        sea.Armies.RemoveAll(a => !pair.Contains(a));
        var inland = Enumerable.Range(1, map.ProvinceCount).First(p => p != fleet.HomeProvinceId &&
            sea.ProvinceController[p] == britain.MapIndex && SeaLanes.HarbourOf(map, p) < 0 &&
            map.FindRoute(p, fleet.HomeProvinceId, (ushort)britain.MapIndex, sea.ProvinceController).Length > 0);
        foreach (var a in pair)
        {
            a.ProvinceId = inland; (a.Latitude, a.Longitude) = map.CenterOf(inland);
            a.OrderKind = ArmyOrder.ReserveFront; a.Route.Clear(); a.PlannedRoute.Clear();
            a.DirectOrder = false; a.BattleId = -1;
        }
        pair[0].Route.Add(inland); pair[0].OrderProvinceId = inland;
        AiAdmiralty.Tick(sea);
        Check(pair[0].OrderProvinceId == inland && pair[1].OrderProvinceId == fleet.HomeProvinceId && pair[1].Route.Count > 0,
            "unrelated march does not prevent another reserve being called to port");
        pair[1].OrderKind = ArmyOrder.AdvanceFront; pair[1].AssignedWarId = 701;
        pair[1].Route.Clear(); pair[1].PlannedRoute.Clear(); pair[1].DirectOrder = false;
        pair[1].OrderProvinceId = -1;
        AiAdmiralty.Tick(sea);
        Check(pair[1].OrderProvinceId == fleet.HomeProvinceId && pair[1].Route.Count > 0,
            "nominal advance without a land front can join an overseas landing");
        sea.Wars[0].AttackerFront.Add(inland);
        pair[1].OrderKind = ArmyOrder.DefendFront; pair[1].AssignedWarId = 701;
        pair[1].Route.Clear(); pair[1].PlannedRoute.Clear(); pair[1].DirectOrder = false;
        pair[1].OrderProvinceId = inland;
        AiAdmiralty.Tick(sea);
        Check(pair[1].Route.Count == 0 && pair[1].OrderProvinceId == inland,
            "admiralty does not strip a division from an actual land front");

        Check(AiStatecraft.CanStageWar(c, north, south), "controlled land border and a field army provide a staging route");
        Check(!AiStatecraft.CanStageWar(sea, britain, sea.NationByTag("CHE")),
            "navy cannot turn a landlocked overseas country into an invasion target");
        Check(AiStatecraft.CanStageWar(sea, britain, france), "real transport route to a reachable coast remains available");
        sea.Armies.Clear();
        Check(!AiStatecraft.CanStageWar(sea, britain, france), "GDP and ships without landing troops cannot start a voluntary war");
        Check(!AiStatecraft.PreparationComplete(c, north, south), "new voluntary war requires preparation");
        c.Defines.AiWarPreparationDays = 0;
        Check(AiStatecraft.PreparationComplete(c, north, south), "mod can explicitly disable preparation delay");
        c.Defines.AiWarPreparationDays = 45;
        c.IsPaused = false; c.Defines.AiMayDeclareWar = false;
        for (var day = 0; day < 45; day++) c.TickDay();
        // The live war clears voluntary plans. Establish a saved plan dated at day zero.
        north.WarPlanTargetId = south.Id; north.WarPlanSinceDay = 0;
        Check(AiStatecraft.PreparationComplete(c, north, south), "same target is ready after configured preparation time");
        Check(!AiStatecraft.PreparationComplete(c, north, c.NationByTag("JPN")), "changing target starts a fresh preparation period");

        Secession(map, human: false, accept: false);
        Secession(map, human: false, accept: true);
        Secession(map, human: true, accept: false);

        if (failures > 0) throw new Exception($"{failures} AI war regressions failed");
    }

    static void Secession(ProvinceTopology map, bool human, bool accept)
    {
        var c = Campaign.CreateBootstrap(20260927);
        c.AttachProvinceTopology(map); c.Wars.Clear(); c.Battles.Clear();
        var parent = c.NationByTag("FRA");
        parent.IsHuman = human; parent.Stability = .9f; parent.WarSupport = 1;
        // Force the political branch, not the war result. Geography/orders remain real.
        SecessionCore.Weights.Acceptance = accept ? 10 : -10;
        try
        {
            var region = c.StatesOf(parent).Where(s => s.Population > 300000)
                .OrderByDescending(s => s.Population).First();
            var report = SecessionEvents.Force(c, region);
            Check(report != null, "region forms a real breakaway country");
            var war = c.Wars.FirstOrDefault(w => !w.Ended && w.Opposes(parent.Id, report.Seceded.Id));
            if (human)
            {
                Check(war == null, "human parent is asked instead of being forced into war");
                var prompt = c.PendingEvents.Last(e => e.NationId == parent.Id);
                new ResolveCampaignEventCommand { IssuerId = parent.Id, EventId = prompt.Id, OptionIndex = 2 }.Apply(c);
                Check(c.Wars.Any(w => !w.Ended && w.Opposes(parent.Id, report.Seceded.Id)), "Send the army actually declares war on the breakaway");
                var relation = CampaignDiplomacy.Get(c, parent.Id, report.Seceded.Id);
                foreach (var w in c.Wars) w.Ended = true;
                relation.Stance = DiplomaticStance.Neutral; relation.TruceUntil = c.ElapsedDays + 100;
                // The truce binds the breakaway that signed it, not the state it left
                // (SecessionResponse.Reclaiming, 2026-10-25): the state may always take its own
                // ground back, and the reclaiming war clears the truce it no longer honours.
                Check(!CampaignDiplomacy.CanDeclareWar(c, report.Seceded.Id, parent.Id, out _),
                    "a later truce still binds the breakaway");
                Check(CampaignDiplomacy.DeclareWar(c, parent.Id, report.Seceded.Id, -1, "secession") != null &&
                      relation.TruceUntil == 0,
                    "the state it left may reclaim it through the truce, and the truce is void");
            }
            else if (accept) Check(war == null && report.Outcome == SecessionOutcome.Accepted, "AI may accept secession peacefully");
            else
            {
                Check(war != null && report.Outcome == SecessionOutcome.Contested, "AI refusal creates war even while voluntary preparation is enabled");
                Check(report.Seceded.MapIndex >= 1000 && war.AttackerFront.Count > 0 && war.DefenderFront.Count > 0,
                    "dynamic country map indices participate in real front construction");
                Console.WriteLine($"SECESSION front={war.AttackerFront.Count}/{war.DefenderFront.Count} parent={parent.Tag} born={report.Seceded.Tag} region={region.Name}");
                foreach (var a in c.Armies.Where(a => a.NationId == parent.Id).Take(6))
                    Console.WriteLine($" unit={a.Id} at={a.ProvinceId} control={c.ProvinceController[a.ProvinceId]} role={a.OrderKind} war={a.AssignedWarId} target={a.OrderProvinceId} route={a.Route.Count} direct={a.DirectOrder}");
                Check(c.Armies.Any(a => a.NationId == parent.Id && a.AssignedWarId == war.Id &&
                    (a.Route.Count > 0 || war.FriendlyFront(parent.Id).Contains(a.ProvinceId))), "parent troops receive actual routes/posts against the breakaway");
                c.IsPaused = false; c.Defines.AiMayDeclareWar = false;
                for (var day = 0; day < 60 && !war.Ended; day++) c.TickDay();
                Check(war.AttackerLosses + war.DefenderLosses > 0 || MathF.Abs(war.OccupationScore) > 0 || war.Ended,
                    "secession war produces combat or territorial progress, not just assigned labels");
            }
        }
        finally { SecessionCore.ResetCatalog(); }
    }
}
