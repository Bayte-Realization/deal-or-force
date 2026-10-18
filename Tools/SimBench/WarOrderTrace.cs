using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>Opt-in bounded, daily observation. No command or private-save write.</summary>
sealed class WarOrderTrace
{
    readonly record struct Note(int Position, int Goal, int War, ArmyOrder Kind, int Issued, float Progress)
    {
        public static Note Of(Army a) => new(a.ProvinceId, a.OrderProvinceId, a.AssignedWarId,
            a.OrderKind, a.OrderIssuedDay, a.MoveProgress);
    }
    sealed class Counts
    {
        public int Retargets, Arrivals, SameGoalResets;
        public string Tag = "";
    }
    readonly HashSet<int> _countries;
    readonly Dictionary<int, Note> _last = new();
    readonly Dictionary<int, Counts> _counts = new();
    int _printed;

    public WarOrderTrace(Campaign c, HashSet<int> countries)
    {
        _countries = new(countries);
        foreach (var a in c.Armies.Where(a => _countries.Contains(a.NationId))) _last[a.Id] = Note.Of(a);
    }

    public void Observe(Campaign c)
    {
        foreach (var a in c.Armies)
        {
            if (!_countries.Contains(a.NationId)) continue;
            var now = Note.Of(a);
            if (!_last.TryGetValue(a.Id, out var was)) { _last[a.Id] = now; continue; }
            _last[a.Id] = now;
            if (!_counts.TryGetValue(a.Id, out var count))
                _counts[a.Id] = count = new() { Tag = c.NationById(a.NationId)?.Tag ?? "gone" };
            var changes = ArmyOrderSample.Compare(new(was.Position, was.Goal, was.Issued, was.Progress),
                new(now.Position, now.Goal, now.Issued, now.Progress));
            if ((changes & ArmyOrderChange.Arrived) != 0) count.Arrivals++;
            var retarget = (changes & ArmyOrderChange.Retargeted) != 0;
            var restart = (changes & ArmyOrderChange.ProgressReset) != 0;
            if (retarget) count.Retargets++;
            if (restart) count.SameGoalResets++;
            if (!(retarget || restart) || _printed++ >= 120) continue;
            Console.WriteLine($"ORDER_TRACE day={c.ElapsedDays} {count.Tag}/A{a.Id} at={was.Position}->{now.Position} " +
                $"goal={was.Goal}->{now.Goal} role={was.Kind}->{now.Kind} war={was.War}->{now.War} " +
                $"age={c.ElapsedDays-was.Issued} progress={was.Progress:0.000}->{now.Progress:0.000} " +
                $"route={a.Route.Count}/{a.PlannedRoute.Count} planning={a.Planning:0.000} " +
                $"org={a.Organisation:0.00} supply={a.SupplyLevel:0.00} retarget={retarget} restart={restart}");
        }
    }

    public void Print()
    {
        Console.WriteLine($"ORDER_TRACE totals retargets={_counts.Values.Sum(x=>x.Retargets)} " +
            $"arrivals={_counts.Values.Sum(x=>x.Arrivals)} same-goal-resets={_counts.Values.Sum(x=>x.SameGoalResets)}");
        foreach (var p in _counts.OrderByDescending(p=>p.Value.Retargets+p.Value.SameGoalResets).Take(12))
            Console.WriteLine($"ORDER_TRACE army={p.Value.Tag}/A{p.Key} retargets={p.Value.Retargets} " +
                $"arrivals={p.Value.Arrivals} same-goal-resets={p.Value.SameGoalResets}");
    }
}
