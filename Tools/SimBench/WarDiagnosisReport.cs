using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Bounded inspection/replay of a disposable loaded campaign; never writes a save.</summary>
static class WarDiagnosisReport
{
    public static int Run(Campaign c, string tag, int days)
    {
        var focus = c.NationByTag(tag);
        if (focus is null || c.ProvinceMap is null) { Console.WriteLine("No mapped country: " + tag); return 2; }
        var wars = c.Wars.Where(w => !w.Ended && w.Involves(focus.Id)).ToArray();
        Console.WriteLine($"WAR_DIAG authority observer={c.ObserverMode} seats={string.Join(",",c.HumanSeats)} focusHuman={focus.IsHuman}");
        foreach (var ended in c.Wars.Where(w => w.Involves(focus.Id)).OrderByDescending(w => w.StartedDay).Take(8))
        {
            var enemy = c.NationById(ended.EnemyLeaderOf(focus.Id));
            var relation = CampaignDiplomacy.Peek(c,focus.Id,enemy?.Id ?? -1);
            Console.WriteLine($"WAR_HISTORY {ended.Id} {focus.Tag}-{enemy?.Tag} ended={ended.Ended} start={ended.StartedDay} day={ended.EndedDay} winner={c.NationById(ended.WinnerId)?.Tag} " +
                $"score={ended.WarScore:0.0} goal={ended.ClaimedStateMapIndex} reason={ended.Justification} " +
                $"annexed={ended.AnnexedProvinces} summary={ended.PeaceSummary}");
            Console.WriteLine($"WAR_RELATION stance={relation?.Stance} nap={relation?.NonAggressionPact} truceUntil={relation?.TruceUntil}");
            Console.WriteLine("WAR_SIDES " + string.Join(",",ended.Side(true).Select(id=>c.NationById(id)?.Tag)) +
                " vs " + string.Join(",",ended.Side(false).Select(id=>c.NationById(id)?.Tag)));
        }
        var ids = new HashSet<int> { focus.Id };
        foreach (var w in wars) { ids.UnionWith(w.Side(true)); ids.UnionWith(w.Side(false)); }
        var positions = c.Armies.Where(a => ids.Contains(a.NationId)).ToDictionary(a => a.Id, a => a.ProvinceId);
        var initialDay = c.ElapsedDays;
        var controls = (ushort[])c.ProvinceController.Clone();
        var controlChanges = 0;
        Console.WriteLine($"WAR_DIAG saved day={c.ElapsedDays} date={c.Date} focus={tag} wars={wars.Length}; replay={days} days; no save writes");
        Warfare.RebuildFronts(c);
        Print(c, wars, ids, "BEFORE");
        var orderTrace = Environment.GetEnvironmentVariable("DOF_TRACE_ORDERS") == "1"
            ? new WarOrderTrace(c, ids) : null;
        var owners = (ushort[])c.ProvinceMap.ProvinceToNation.Clone();
        var changed = 0;
        c.IsPaused = false;
        for (var day = 0; day < Math.Clamp(days, 0, 365); day++)
        {
            c.TickDay();
            orderTrace?.Observe(c);
            for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
            {
                if (controls[p] != c.ProvinceController[p])
                {
                    if (ids.Any(id => c.NationById(id)?.MapIndex == controls[p] ||
                        c.NationById(id)?.MapIndex == c.ProvinceController[p])) controlChanges++;
                    controls[p] = c.ProvinceController[p];
                }
                var owner = c.ProvinceMap.ProvinceToNation[p];
                if (owner == owners[p]) continue;
                var from = c.Nations.FirstOrDefault(n => n.MapIndex == owners[p]);
                var to = c.Nations.FirstOrDefault(n => n.MapIndex == owner);
                if (from is not null && to is not null && (ids.Contains(from.Id) || ids.Contains(to.Id)))
                {
                    if (changed++ < 25)
                        Console.WriteLine($"OWNER_CHANGE day={c.ElapsedDays} province={p} {from.Tag}->{to.Tag} still-at-war={CampaignDiplomacy.AtWar(c, from.Id, to.Id)}");
                }
                owners[p] = owner;
            }
        }
        if (days > 0) Print(c, wars, ids, "AFTER");
        var moved = positions.Count(kv => c.Armies.FirstOrDefault(a => a.Id == kv.Key) is { } a && a.ProvinceId != kv.Value);
        var battles = c.Battles.Count(b => b.StartedDay >= initialDay && wars.Any(w => w.Id == b.WarId));
        Console.WriteLine($"WAR_DIAG end day={c.ElapsedDays}; moved={moved}/{positions.Count}; control changes={controlChanges}; new battles={battles}; relevant ownership changes={changed}; fault={c.SimulationFault}");
        orderTrace?.Print();
        return c.SimulationFault.Length == 0 ? 0 : 1;
    }

    static void Print(Campaign c, StrategicWar[] wars, HashSet<int> ids, string label)
    {
        foreach (var w in wars)
            Console.WriteLine($"{label} WAR {w.Id} {w.Name} ended={w.Ended} score={w.WarScore:0.0} fronts={w.AttackerFront.Count}/{w.DefenderFront.Count} " +
                $"sides={string.Join(",", w.Side(true).Select(id => c.NationById(id)?.Tag))} vs {string.Join(",", w.Side(false).Select(id => c.NationById(id)?.Tag))}");
        foreach (var id in ids.OrderBy(i => i))
        {
            var n = c.NationById(id);
            var units = c.Armies.Where(a => a.NationId == id).OrderBy(a => a.Id).ToArray();
            var land = c.StatesOf(n);
            Console.WriteLine($"{label} COUNTRY {n?.Tag} {n?.Name} faction={n?.FactionId} human={n?.IsHuman} landless={n?.Landless} regions={land.Length} " +
                $"units={units.Length} militia={units.Count(a => a.TemplateId == MilitiaCore.TemplateId)}");
            foreach (var a in units.Take(45))
                Console.WriteLine($"  A{a.Id} {a.TemplateId} at={a.ProvinceId} order={a.OrderKind} war={a.AssignedWarId} target={a.OrderProvinceId} " +
                    $"active={a.PlanActive} direct={a.DirectOrder} battle={a.InBattle} route={a.Route.Count}/{a.PlannedRoute.Count} progress={a.MoveProgress:0.000} " +
                    $"plan={a.Planning:0.00} org={a.Organisation:0.00} supply={a.SupplyLevel:0.00} strength={a.Strength:0.00} ready={a.ReadyDay}");
        }
    }
}
