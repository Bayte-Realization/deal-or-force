using System;
using System.Linq;
using DealOrForce.Simulation;

static class AdvanceLayoutTests
{
    private static void Check(bool ok, string label)
    {
        if (!ok) throw new Exception("FAIL: " + label);
        Console.WriteLine("PASS: " + label);
    }

    public static void Run()
    {
        var c = BattleWithdrawalTests.CreateFixture();
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR"); var map = c.ProvinceMap;
        c.Armies.Clear(); c.Battles.Clear(); c.FrontLines.Clear();
        var border = BorderFronts.Candidates(c, rus.Id, ukr.Id).OrderBy(p => p).ToArray();
        Check(border.Length >= 2, "real fixture has a multi-province departure front");
        var goal = map.Neighbors[border[0]].First(p => c.ProvinceController[p] == ukr.MapIndex);
        var line = new DrawnFrontLine { Id = 400, NationId = rus.Id, BorderNationId = ukr.Id,
            Kind = FrontLineKind.Offensive };
        (line.StartLatitude, line.StartLongitude) = map.CenterOf(border[0]);
        (line.EndLatitude, line.EndLongitude) = map.CenterOf(border[^1]);
        line.SourceProvinces.UnionWith(border); line.ObjectiveSequence.Add(goal); line.Provinces.Add(goal);
        c.FrontLines.Add(line);
        var goals = Enumerable.Range(1, map.ProvinceCount).Where(p => c.ProvinceController[p] == ukr.MapIndex).ToArray();
        for (var i = 0; i < border.Length * 3 + 1; i++)
        {
            var a = new Army { Id = 8000 + i, NationId = rus.Id, ProvinceId = border[0],
                Divisions = 1, Organisation = 1, Strength = 1, FrontLineId = line.Id };
            line.ArmyIds.Add(a.Id); c.Armies.Add(a);
            // Every goal is different: the old sharing-goal rank restarted at zero.
            line.Objectives[a.Id] = goals[i % goals.Length];
        }
        foreach (var a in c.Armies) OffensivePlanning.DeparturePostFor(c, a, line);
        var occupancy = border.Select(p => line.Assignment.Values.Count(v => v == p)).ToArray();
        Check(occupancy.Max() - occupancy.Min() <= 1 && occupancy.Min() > 0,
            "different goal groups distribute evenly across the entire source front");
        var before = line.Assignment.OrderBy(p => p.Key).ToArray();
        c.Armies.Reverse();
        foreach (var a in c.Armies) OffensivePlanning.DeparturePostFor(c, a, line);
        Check(before.SequenceEqual(line.Assignment.OrderBy(p => p.Key)), "allocation is stable across query/list order");
        foreach (var a in c.Armies) { a.PlanActive = false; OffensivePlanning.Route(c, a, line); }
        Check(c.Armies.All(a => a.OrderProvinceId == line.Assignment[a.Id] && !a.PlanActive),
            "Prepare routes to balanced friendly posts without activating an attack");
        var removed = border[0]; c.ProvinceController[removed] = (ushort)ukr.MapIndex;
        foreach (var a in c.Armies) a.ProvinceId = border[^1];
        foreach (var a in c.Armies) OffensivePlanning.DeparturePostFor(c, a, line);
        Check(!line.Assignment.Values.Contains(removed), "lost departure tile is never assigned");
        c.ProvinceController[removed] = (ushort)rus.MapIndex;
        foreach (var a in c.Armies.Skip(2).ToArray()) { line.ArmyIds.Remove(a.Id); c.Armies.Remove(a); }
        foreach (var a in c.Armies) OffensivePlanning.DeparturePostFor(c, a, line);
        Check(line.Assignment.Count == 2 && line.Assignment.Values.Distinct().Count() == 2,
            "sparse front uses spaced posts and drops removed formations");

        c.Armies.Clear(); c.Battles.Clear(); c.FrontLines.Clear();
        var war = c.Wars.Single(); war.FrontDirty = false; war.LastFrontDay = c.ElapsedDays;
        var start = border[0];
        var target = map.Neighbors[start].First(p => c.ProvinceController[p] == ukr.MapIndex);
        var flank = map.Neighbors[target].First(p => p != start && c.ProvinceController[p] == ukr.MapIndex);
        var mover = new Army { Id = 9000, NationId = rus.Id, ProvinceId = start, Divisions = 1,
            Organisation = 1, Strength = 1, SupplyLevel = .06f, PlanActive = true, AssignedWarId = war.Id,
            OrderKind = ArmyOrder.Spearhead, OrderProvinceId = target, DirectOrder = true };
        c.Armies.Add(mover);
        c.Armies.Add(new Army { Id = 9001, NationId = ukr.Id, ProvinceId = flank, Divisions = 200,
            Organisation = 1, Strength = 1, OrderKind = ArmyOrder.Manual });
        float Measure(int hours, bool ordered)
        {
            mover.MoveProgress = 0; mover.DirectOrder = ordered; c.Defines.HoursPerTick = hours;
            Warfare.Tick(c); return mover.MoveProgress;
        }
        var daily = Measure(24, true);
        Check(daily > .06f && daily < 1 && mover.ProvinceId == start,
            "direct covered advance progresses in days, not months, without teleporting");
        var hourly = Measure(1, true);
        Check(Math.Abs(daily - hourly * 24) < .0001f, "advance pace is invariant to hourly/daily tick length");
        Check(Measure(24, false) == 0, "autonomous weak gap exploit still refuses overwhelming cover");
        c.Defines.OrderedCapturePerDay *= .5f;
        var retuned = Measure(24, true);
        Check(Math.Abs(retuned * 2 - daily) < .0001f, "moddable ordered pace changes real covered advance progress");
        c.Defines.OrderedCapturePerDay *= 2;
        c.Armies.RemoveAt(1);
        var open = Measure(24, true);
        Check(open > daily, "open country remains faster than covered enemy ground");
        var state = c.States.First(s => s.MapIndex == map.ProvinceToState[target]);
        state.Buildings[BuildingKind.Infrastructure] = 0;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 24L);
        war.FrontDirty = false; war.LastFrontDay = c.ElapsedDays;
        var withoutRoads = Measure(24, true);
        state.Buildings[BuildingKind.Infrastructure] = 6;
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, 48L);
        war.FrontDirty = false; war.LastFrontDay = c.ElapsedDays;
        var withRoads = Measure(24, true);
        Check(withRoads > withoutRoads && Math.Abs(withRoads / withoutRoads - 1.6f) < .001f,
            "enemy-ground advance uses the entered region's infrastructure and road cap");
        mover.SupportOnly = true;
        Check(Measure(24, true) == 0 && mover.ProvinceId == start, "support fire cannot occupy empty ground");
        mover.SupportOnly = false;
        c.Armies.Add(new Army { Id = 9002, NationId = ukr.Id, ProvinceId = target, Divisions = 1,
            Organisation = 1, Strength = 1, OrderKind = ArmyOrder.Manual });
        Measure(24, true);
        Check(mover.InBattle && c.ProvinceController[target] == ukr.MapIndex,
            "a real garrison starts combat instead of accelerated occupation");
    }
}
