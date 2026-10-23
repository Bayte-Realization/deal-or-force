using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

/// <summary>Behavioral oracle for the late-save staff optimization, not a wall-clock test.
/// Keep the pre-optimization selector below deliberately unindexed: the optimized path must
/// choose the same detachment, route and command effects without consuming another random draw.</summary>
static class FrontDisengagementPerformanceTests
{
    static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL front disengagement equivalence: " + label);
        Console.WriteLine("PASS front disengagement equivalence: " + label);
    }

    static string State(Campaign c) => JsonSerializer.Serialize(new
    { c.Armies, c.Battles, c.FrontLines, c.ProvinceController, c.Random.State },
        new JsonSerializerOptions { IncludeFields = true });

    public static void Run()
    {
        foreach (var defending in new[] { false, true })
        {
            Compare("same-position candidates, reversed tie order, defending=" + defending,
                () => Fixture(defending), new[] { 6 }, expectedSuccesses: 1, expectedFirstSource: 1);
            Compare("successive holes rebuild powers and occupancies, defending=" + defending,
                () => Fixture(defending), new[] { 6, 1, 5 }, expectedSuccesses: 3);
            Compare("different battle and lower-power candidate, defending=" + defending,
                () => Fixture(defending, secondBattle: true), new[] { 6, 5 }, expectedSuccesses: 2,
                expectedFirstSource: 4);
            Compare("coalition corridor and allied battle cover, defending=" + defending,
                () => Fixture(defending, coalition: true), new[] { 5 }, expectedSuccesses: 1);
            Compare("neutral corridor cannot become coalition access, defending=" + defending,
                () => { var c = Fixture(defending, coalition: true); c.Wars[0].AttackerAllies.Clear();
                    c.Wars[0].DefenderAllies.Clear(); return c; }, new[] { 5 }, expectedSuccesses: 0);
        }

        Compare("active battle on friendly-controlled destination is unsafe", () =>
        {
            var c = Fixture(true);
            c.Battles.Add(new LandBattle { Id = 993, WarId = 882, ProvinceId = 6 });
            return c;
        }, new[] { 6 }, expectedSuccesses: 0);
        Compare("hostile formation on friendly-controlled destination is unsafe", () =>
        {
            var c = Fixture(false); AddUnit(c, 30, 1, 6, 1, .8f, false); return c;
        }, new[] { 6 }, expectedSuccesses: 0);
        Compare("friendly formation already covers destination", () =>
        {
            var c = Fixture(false); AddUnit(c, 30, 0, 6, 1, .8f, false); return c;
        }, new[] { 6 }, expectedSuccesses: 0);
        Compare("ended report and neutral visitor do not poison safety index", () =>
        {
            var c = Fixture(false);
            c.Battles.Add(new LandBattle { Id = 993, WarId = 882, ProvinceId = 6, Ended = true });
            AddUnit(c, 30, 2, 6, 1, .8f, false); return c;
        }, new[] { 6 }, expectedSuccesses: 1);
        Compare("hostile formation inside an otherwise legal allied corridor is unsafe", () =>
        {
            var c = Fixture(false, coalition: true); AddUnit(c, 30, 1, 6, 1, .8f, false); return c;
        }, new[] { 5 }, expectedSuccesses: 0);
        Compare("unrelated active battle inside an allied corridor is unsafe", () =>
        {
            var c = Fixture(false, coalition: true);
            c.Battles.Add(new LandBattle { Id = 993, WarId = 9876, ProvinceId = 6 }); return c;
        }, new[] { 5 }, expectedSuccesses: 0);
        Compare("small hostile remnant is below the original safety threshold", () =>
        {
            var c = Fixture(false); AddUnit(c, 30, 1, 6, .09f, .8f, false); return c;
        }, new[] { 6 }, expectedSuccesses: 1);
        Compare("invalid destination is a pure refusal", () => Fixture(false), new[] { 0, 900 }, expectedSuccesses: 0);
        FreshContextAfterMutation();
        RoutingWorkIsShared();
        FallbackCrowdingRemainsLive();
    }

    static void FallbackCrowdingRemainsLive()
    {
        for (var variant = 0; variant < 12; variant++)
        {
            var c = Fixture(true);
            for (var p = 1; p <= 8; p++) c.ProvinceController[p] = 500;
            c.Armies.Find(a => a.NationId == 1).ProvinceId = 8;
            c.Battles.Add(new LandBattle { Id = 994, WarId = 882, ProvinceId = 1 });
            var first = AddUnit(c, 31, 0, 4, 1, .8f, false);
            var second = AddUnit(c, 32, 0, variant % 2 == 0 ? 3 : 6, 1, .8f, false);
            first.OrderProvinceId = variant % 3 == 0 ? 3 : 6; first.Route.Add(first.OrderProvinceId);
            second.OrderProvinceId = 6; second.Route.Add(6);
            using var context = new FrontDisengagement.SelectionContext(c, c.Wars[0], 0, 5);
            foreach (var hole in new[] { 5, 1, 5 })
            {
                context.BeginHole(hole);
                var before = CampaignFingerprint.Of(c); var random = c.Random.State;
                foreach (var unit in Candidates(c))
                    Check(context.WithdrawalProvince(unit) == Warfare.WithdrawalProvince(c, c.Wars[0], unit, hole),
                        "crowding fallback matches legacy, variant=" + variant + ", hole=" + hole + ", unit=" + unit.Id);
                Check(CampaignFingerprint.Of(c) == before && c.Random.State == random,
                    "fallback census is read-only, variant=" + variant + ", hole=" + hole);
                // Reposting changes the next hole's crowding without changing positions.
                first.OrderProvinceId = first.OrderProvinceId == 3 ? 6 : 3;
                first.Route.Clear(); first.Route.Add(first.OrderProvinceId);
            }
        }
    }

    static Campaign Fixture(bool defending, bool coalition = false, bool secondBattle = false)
    {
        var c = PartialFrontWithdrawalTests.Fixture(defending);
        // An explicit tiny graph makes shortest-route/tie assertions independent of raster seams.
        int[][] neighbours = { Array.Empty<int>(), new[] { 2, 5 }, new[] { 1, 3, 6 },
            new[] { 2, 4, 7 }, new[] { 3, 8 }, new[] { 1, 6 }, new[] { 2, 5, 7 },
            new[] { 3, 6, 8 }, new[] { 4, 7 } };
        for (var p = 1; p <= 8; p++) c.ProvinceMap.Neighbors[p] = neighbours[p];
        for (var id = 3; id <= 5; id++)
        {
            var a = AddUnit(c, id, 0, 2, 20, .8f, true);
            (defending ? c.Battles[0].Defenders : c.Battles[0].Attackers).Add(a.Id);
        }
        if (secondBattle)
        {
            var b = new LandBattle { Id = 992, WarId = 882, ProvinceId = defending ? 2 : 3,
                AttackerId = c.Wars[0].AttackerId, DefenderId = c.Wars[0].DefenderId, StartedDay = 0 };
            foreach (var id in new[] { 4, 5 })
            {
                (defending ? c.Battles[0].Defenders : c.Battles[0].Attackers).Remove(id);
                (defending ? b.Defenders : b.Attackers).Add(id);
                c.Armies.Find(a => a.Id == id).BattleId = b.Id;
            }
            c.Armies.Find(a => a.Id == 4).Organisation = .55f;
            var enemy = AddUnit(c, 10, 1, 3, 1, .8f, false); enemy.BattleId = b.Id;
            (defending ? b.Attackers : b.Defenders).Add(enemy.Id); c.Battles.Add(b);
        }
        if (coalition)
        {
            c.Wars[0].Join(2, c.Wars[0].IsAttacking(0));
            // The only route to own province 5 crosses ally 6, never the neutral/hostile 1.
            c.ProvinceMap.Neighbors[2] = new[] { 3, 6 };
            c.ProvinceMap.Neighbors[6] = new[] { 2, 5 };
            c.ProvinceMap.Neighbors[5] = new[] { 6 };
            c.ProvinceController[1] = 501; c.ProvinceController[6] = 502;
            var ally = AddUnit(c, 20, 2, 2, 20, .8f, false); ally.BattleId = c.Battles[0].Id;
            (defending ? c.Battles[0].Defenders : c.Battles[0].Attackers).Add(ally.Id);
        }
        return c;
    }

    static Army AddUnit(Campaign c, int id, int nation, int province, float divisions, float organisation, bool pinned)
    {
        var source = c.Armies[0];
        var a = new Army { Id = id, NationId = nation, ProvinceId = province,
            StateMapIndex = 2000 + province, Divisions = divisions,
            Personnel = (int)(divisions * 10_000), MaxPersonnel = (int)(divisions * 15_000),
            OrderKind = source.OrderKind, AssignedWarId = 882, OrderProvinceId = province,
            OrderIssuedDay = -100, Strength = source.Strength, Organisation = organisation,
            Equipment = source.Equipment, Experience = source.Experience, Cohesion = source.Cohesion,
            Recon = source.Recon, Engineers = source.Engineers, Artillery = source.Artillery,
            SupplyLevel = source.SupplyLevel, BattleId = pinned ? c.Battles[0].Id : -1 };
        (a.Latitude, a.Longitude) = c.ProvinceMap.CenterOf(province); c.Armies.Add(a); return a;
    }

    static List<Army> Candidates(Campaign c) => new[] { 5, 3, 4, 1 }
        .Select(id => c.Armies.Find(a => a.Id == id)).Where(a => a is not null).ToList();

    static void Compare(string label, Func<Campaign> create, int[] holes, int expectedSuccesses,
        int expectedFirstSource = -1)
    {
        var legacy = create(); var optimized = create();
        Check(CampaignFingerprint.Of(legacy) == CampaignFingerprint.Of(optimized), label + ": identical fixtures");
        var successes = 0;
        FrontDisengagement.SelectionContext context = null;
        foreach (var hole in holes)
        {
            var beforeLegacy = legacy.Random.State; var beforeOptimized = optimized.Random.State;
            var divisions = legacy.Armies.ToDictionary(a => a.Id, a => a.Divisions);
            var expected = LegacyTryCoverHole(legacy, legacy.Nations[0], legacy.Wars[0], null, hole,
                out var oldMove, Candidates(legacy));
            var actual = FrontDisengagement.TryCoverHole(optimized, optimized.Nations[0], optimized.Wars[0], null,
                hole, out var newMove, ref context, Candidates(optimized));
            Check(actual == expected && (oldMove?.Id ?? -1) == (newMove?.Id ?? -1),
                label + ": selected detachment at hole " + hole);
            Check(State(legacy) == State(optimized) && CampaignFingerprint.Of(legacy) == CampaignFingerprint.Of(optimized),
                label + ": exact commands, troops, routes, losses and fingerprint at hole " + hole);
            Check(legacy.Random.State == beforeLegacy && optimized.Random.State == beforeOptimized,
                label + ": no random consumption at hole " + hole);
            if (!actual) continue;
            if (successes == 0 && expectedFirstSource >= 0)
                Check(legacy.Armies.Find(a => a.Id == expectedFirstSource).Divisions < divisions[expectedFirstSource],
                    label + ": expected candidate wins, not enumeration order");
            successes++;
        }
        context?.Dispose();
        Check(successes == expectedSuccesses, label + ": expected successful/refused holes");
    }

    static void FreshContextAfterMutation()
    {
        var legacy = Fixture(false, coalition: true); var optimized = Fixture(false, coalition: true);
        FrontDisengagement.SelectionContext context = null;
        for (var attempt = 0; attempt < 3; attempt++)
        {
            var before = optimized.Random.State;
            var expected = LegacyTryCoverHole(legacy, legacy.Nations[0], legacy.Wars[0], null, 5,
                out var oldMove, Candidates(legacy));
            var actual = FrontDisengagement.TryCoverHole(optimized, optimized.Nations[0], optimized.Wars[0], null,
                5, out var newMove, ref context, Candidates(optimized));
            Check(expected == actual && actual == (attempt != 1) &&
                CampaignFingerprint.Of(legacy) == CampaignFingerprint.Of(optimized) && State(legacy) == State(optimized),
                "fresh context sees changed coalition and previous detachment, attempt=" + attempt);
            Check(optimized.Random.State == before && legacy.Random.State == before,
                "fresh context leaves random state unchanged, attempt=" + attempt);
            if (actual)
            {
                // Remove the already detached reserve from this tiny test scene, not from a live game.
                legacy.Armies.Remove(oldMove); optimized.Armies.Remove(newMove);
            }
            if (attempt == 0)
            { legacy.Wars[0].AttackerAllies.Clear(); optimized.Wars[0].AttackerAllies.Clear(); }
            if (attempt == 1)
            { legacy.Wars[0].Join(2, true); optimized.Wars[0].Join(2, true); }
        }
    }

    static int BfsCalls() => SimProfiler.All.Where(s => s.Name.Trim() == "disengage:bfs").Sum(s => s.Runs);

    static void RoutingWorkIsShared()
    {
        var c = Fixture(false);
        FrontDisengagement.SelectionContext context = null;
        var before = BfsCalls(); var random = c.Random.State;
        var success = FrontDisengagement.TryCoverHole(c, c.Nations[0], c.Wars[0], null, 6,
            out _, ref context, Candidates(c));
        Check(success && BfsCalls() - before == 1,
            "four same-origin candidates perform one actual BFS, not four");
        Check(context is null && c.Random.State == random,
            "a successful command discards shared selection facts and consumes no random draw");

        c = Fixture(false, coalition: true);
        c.Battles.Add(new LandBattle { Id = 993, WarId = 9876, ProvinceId = 6 });
        var state = State(c); var fp = CampaignFingerprint.Of(c); before = BfsCalls();
        success = FrontDisengagement.TryCoverHole(c, c.Nations[0], c.Wars[0], null, 5,
            out _, ref context, Candidates(c));
        Check(!success && BfsCalls() - before == 1 && context is not null,
            "unsafe corridor is checked once for the shared route and retains read-only context");
        before = BfsCalls();
        success = FrontDisengagement.TryCoverHole(c, c.Nations[0], c.Wars[0], null, 5,
            out _, ref context, Candidates(c));
        Check(!success && BfsCalls() == before && State(c) == state && CampaignFingerprint.Of(c) == fp,
            "repeating an unchanged blocked hole reuses its route without any authoritative mutation");

        var other = Fixture(false, coalition: true); before = BfsCalls();
        success = FrontDisengagement.TryCoverHole(other, other.Nations[0], other.Wars[0], null, 5,
            out _, ref context, Candidates(other));
        Check(success && BfsCalls() - before == 1 && context is null,
            "another campaign cannot inherit a former world's unsafe provinces or cached route");
    }

    // Frozen baseline from FrontDisengagement.TryCoverHole before the October 21 late-save
    // optimization. Intentionally keeps all repeated Find/route/safety calls as the oracle.
    static bool LegacyTryCoverHole(Campaign c, Nation nation, StrategicWar war, DrawnFrontLine line,
        int hole, out Army moving, IReadOnlyList<Army> candidates = null)
    {
        moving = null;
        if (!c.Defines.FrontStaffDisengages || war is null || war.Ended ||
            !WarCoalition.SafeRetreatProvince(c, war, nation.Id, hole)) return false;
        foreach (var unit in c.Armies)
            if (unit.NationId == nation.Id && unit.ProvinceId == hole && unit.Divisions >= .1f) return false;
        Army best = null; int[] bestRoute = null; var bestRetreat = -1; var bestShare = 1f; var bestScore = float.MaxValue;
        foreach (var army in candidates ?? c.Armies)
        {
            if (!FrontDisengagement.Eligible(c, nation, war, line, army)) continue;
            var battle = c.Battles.Find(b => b.Id == army.BattleId && !b.Ended && b.WarId == war.Id);
            if (battle is null || c.ElapsedDays - battle.StartedDay < Math.Max(0, c.Defines.FrontDisengagementMinimumBattleDays)) continue;
            var attacking = battle.Attackers.Contains(army.Id);
            if (!attacking && !battle.Defenders.Contains(army.Id)) continue;
            if (line?.Cohesion == PlanCohesion.Rigid &&
                !c.ProvinceMap.Neighbors[army.ProvinceId].AsSpan().Contains(hole)) continue;
            var take = army.Divisions >= 2f
                ? MathF.Min(army.Divisions - 1f, MathF.Max(1f, MathF.Floor(army.Divisions *
                    Math.Clamp(c.Defines.FrontDisengagementMaximumShare, .05f, .5f)))) : army.Divisions;
            var share = take / army.Divisions;
            var ownDivisions = 0f; var ownPower = 0f; var coveringPower = 0f;
            foreach (var id in attacking ? battle.Attackers : battle.Defenders)
            {
                var unit = c.Armies.Find(a => a.Id == id && a.BattleId == battle.Id);
                if (unit is null) continue;
                coveringPower += unit.Power;
                if (unit.NationId == nation.Id) { ownDivisions += unit.Divisions; ownPower += unit.Power; }
            }
            if (ownDivisions - take < .99f || ownPower - army.Power * share < MathF.Max(.05f, ownPower * .5f)) continue;
            if (!attacking)
            {
                var pressure = 0f;
                foreach (var id in battle.Attackers)
                    if (c.Armies.Find(a => a.Id == id && a.BattleId == battle.Id) is { } enemy) pressure += enemy.Power;
                if (coveringPower - army.Power * share < pressure *
                    Math.Clamp(c.Defines.FrontDisengagementDefenceRatio, 0f, 2f)) continue;
            }
            var from = attacking ? army.ProvinceId : Warfare.WithdrawalProvince(c, war, army, hole);
            if (from <= 0) continue;
            var route = from == hole ? Array.Empty<int>() : WarCoalition.FriendlyRoute(c, war, nation.Id, from, hole);
            if (from != hole && route.Length == 0) continue;
            var safe = true;
            foreach (var step in route)
                if (!WarCoalition.SafeRetreatProvince(c, war, nation.Id, step)) { safe = false; break; }
            if (!safe) continue;
            var score = route.Length + (attacking ? 0 : 1) + army.Power / MathF.Max(.01f, army.Divisions);
            if (score > bestScore || score == bestScore && best is not null && army.Id >= best.Id) continue;
            best = army; bestRoute = route; bestRetreat = from; bestShare = share; bestScore = score;
        }
        if (best is null) return false;
        var objective = line?.Objectives.GetValueOrDefault(best.Id, -1) ?? -1;
        var order = new WithdrawArmyFromBattleCommand
        { IssuerId = nation.Id, ArmyId = best.Id, TargetProvinceId = bestRetreat, Share = bestShare };
        order.Apply(c);
        if (!order.Withdrawn) return false;
        moving = c.Armies.Find(a => a.Id == order.WithdrawnArmyId);
        moving.OrderKind = ArmyOrder.DefendFront; moving.AssignedWarId = war.Id; moving.DirectOrder = false;
        moving.PlanActive = line?.Kind != FrontLineKind.Offensive; moving.FrontLineId = line?.Id ?? -1;
        moving.Route.Clear(); moving.Route.AddRange(bestRoute); moving.PlannedRoute.Clear(); moving.PlannedRoute.AddRange(bestRoute);
        moving.MoveProgress = 0; moving.OrderProvinceId = hole; moving.OrderStateMapIndex = c.ProvinceMap.ProvinceToState[hole];
        moving.ObjectiveStateMapIndex = -1; moving.OrderIssuedDay = c.ElapsedDays; moving.OrderRouteSteps = bestRoute.Length;
        moving.OrderStepsCompleted = 0;
        if (line is not null)
        {
            if (!line.ArmyIds.Contains(moving.Id)) line.ArmyIds.Add(moving.Id);
            line.Posts[moving.Id] = hole; if (objective > 0) line.Objectives[moving.Id] = objective;
        }
        return true;
    }
}
