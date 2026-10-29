using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Read/replay a disposable save. Never writes to the save or changes human authority.</summary>
internal static class ConflictIntegrityReport
{
    public static int ProbeRepublic(Campaign c)
    {
        var a = c.LandArrangements.First(x => x.Origin == "armistice");
        var provinces = Enumerable.Range(1, c.ProvinceMap.ProvinceCount)
            .Where(p => a.Regions.Contains(c.ProvinceMap.ProvinceToState[p])).ToArray();
        void Print(string phase) => Console.WriteLine($"PROBE {phase} " +
            $"arrangement={c.LandArrangements.Contains(a)} region-owner={c.States.First(s => s.MapIndex == a.Regions[0]).NationMapIndex} " +
            $"owners={string.Join(",", provinces.GroupBy(p => c.ProvinceMap.ProvinceToNation[p]).Select(g => g.Key + ":" + g.Count()))} " +
            $"controllers={string.Join(",", provinces.GroupBy(p => c.ProvinceController[p]).Select(g => g.Key + ":" + g.Count()))}");
        Print("before");
        var count = c.Nations.Count;
        Console.WriteLine("PROCLAIM " + LandArrangementSystem.ProclaimRepublic(c, c.NationById(a.HolderId), a, out var result) + " " + result);
        Print("proclaimed");
        if (c.Nations.Count > count)
        {
            var born = c.Nations[^1];
            Console.WriteLine($"BORN {born.Id}/{born.Tag}/{born.MapIndex} {born.Name} states={c.StatesOf(born).Length}");
        }
        LandArrangementSystem.Enforce(c);
        Print("enforced");
        Occupations.Tick(c);
        Print("occupations");
        Factions.SettleDefenselessRebelRemnants(c);
        Print("remnant-review");
        return 0;
    }

    public static int Run(Campaign c, string firstTag, string secondTag, int days)
    {
        var first = c.NationByTag(firstTag);
        var second = c.NationByTag(secondTag);
        if (first is null || second is null || c.ProvinceMap is null) return 2;
        var focus = new HashSet<int> { first.Id, second.Id };
        string Tag(int id) => c.NationById(id)?.Tag ?? id.ToString();
        Console.WriteLine($"CONFLICT_AUDIT seed={c.Seed} day={c.ElapsedDays} date={c.Date} replay={days}; NO SAVE WRITES");
        foreach (var war in c.Wars.Where(w => w.Opposes(first.Id, second.Id)))
            Console.WriteLine($"WAR id={war.Id} start={war.StartedDay} end={war.EndedDay} ended={war.Ended} score={war.WarScore:R} " +
                $"limited={war.Limited}/{war.LimitedUntilDay} unlock={war.AiNegotiationUnlockDay} losses={war.AttackerLosses:R}/{war.DefenderLosses:R} " +
                $"proposer={Tag(war.PeaceProposerId)} annexed={war.AnnexedProvinces} summary={war.PeaceSummary}");
        void Arrangements()
        {
            foreach (var a in c.LandArrangements.Where(a => focus.Contains(a.HolderId) || focus.Contains(a.OwnerId)))
                Console.WriteLine($"ARRANGEMENT id={a.Id} {a.Kind}/{a.Origin} {Tag(a.OwnerId)}->{Tag(a.HolderId)} " +
                    $"since={a.StartDay} end={a.EndDay} leaving={a.LeavingOnDay} regions={string.Join(",", a.Regions)}");
        }
        Arrangements();
        foreach (var n in c.Nations.Where(n => n.Movement is not null &&
                     (focus.Contains(n.SecededFromId) || n.Movement.Homeland.Any(id =>
                         c.States.Any(s => s.Id == id && (s.NationMapIndex == first.MapIndex || s.NationMapIndex == second.MapIndex))))))
        {
            var m = n.Movement;
            Console.WriteLine($"MOVEMENT {n.Id}/{n.Tag} {n.Name} from={Tag(n.SecededFromId)} id={m.Id} " +
                $"exile={m.Exile} landless={m.Landless} dissolved={m.Dissolved} HQ={m.HeadquartersStateMapIndex} " +
                $"people={string.Join(",", m.Peoples)} homeland={string.Join(",", m.Homeland)} flag={FlagCatalog.Resolve(c, n)}");
        }
        long sequence = -1;
        void Events(bool initial)
        {
            var events = c.Chronicle.Entries.Where(e => e.Sequence > sequence &&
                (e.Kind.Contains("peace") || e.Kind.Contains("armistice") || e.Kind.Contains("war") ||
                 e.Kind.Contains("subject") || e.Kind.Contains("republic") || e.Kind.Contains("reintegr") ||
                 e.Kind.Contains("secess") || e.Kind.Contains("underground") || e.Kind.Contains("presence")));
            foreach (var e in initial ? events.TakeLast(45) : events.Take(80))
                Console.WriteLine($"EVENT day={e.Day} {e.Kind} {e.ActorName}->{e.TargetName} subject={e.Subject} place={e.Place}");
            if (c.Chronicle.Entries.Count > 0) sequence = c.Chronicle.Entries.Max(e => e.Sequence);
        }
        Events(true);
        var controls = (ushort[])c.ProvinceController.Clone();
        var owners = (ushort[])c.ProvinceMap.ProvinceToNation.Clone();
        c.IsPaused = false;
        for (var day = 0; day < Math.Clamp(days, 0, 730); day++)
        {
            c.TickDay();
            var changes = new Dictionary<string, int>();
            for (var p = 1; p <= c.ProvinceMap.ProvinceCount; p++)
            {
                var owner = c.ProvinceMap.ProvinceToNation[p];
                var controller = c.ProvinceController[p];
                if ((owner == first.MapIndex || owner == second.MapIndex || owners[p] == first.MapIndex || owners[p] == second.MapIndex) &&
                    (owner != owners[p] || controller != controls[p]))
                {
                    var key = $"owner {owners[p]}->{owner} control {controls[p]}->{controller}";
                    changes[key] = changes.GetValueOrDefault(key) + 1;
                }
                controls[p] = controller;
                owners[p] = owner;
            }
            foreach (var (change, count) in changes)
                Console.WriteLine($"GROUND day={c.ElapsedDays} {change} provinces={count}");
            Events(false);
            if (c.SimulationFault.Length > 0) { Console.WriteLine("FAULT " + c.SimulationFault); return 1; }
        }
        if (days > 0) Arrangements();
        foreach (var w in c.Wars.Where(w => w.Side(true).Concat(w.Side(false)).Any(id => focus.Contains(id))))
            Console.WriteLine($"FINAL_WAR id={w.Id} {Tag(w.AttackerId)}->{Tag(w.DefenderId)} start={w.StartedDay} " +
                $"ended={w.Ended}/{w.EndedDay} cause={w.Justification} summary={w.PeaceSummary}");
        Console.WriteLine($"CONFLICT_AUDIT END day={c.ElapsedDays} fault={c.SimulationFault}");
        return 0;
    }
}
