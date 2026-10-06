using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

/// <summary>
/// `--secession-war REGION [CULTURE]` breaks one region away, makes sure the state it left
/// fights for it, and watches the war a fortnight at a time: who holds whose ground, who has
/// what army, who is attacking, and how it ends. The question it answers is whether a war for
/// independence is fought at all, and whether the side that wins it gets what it fought for.
/// </summary>
internal static class SecessionWarReport
{
    public static int Run(Campaign c, int days, string regionId, string culture)
    {
        c.IsPaused = false;
        var state = c.States.Find(s => string.Equals(s.Id, regionId, StringComparison.OrdinalIgnoreCase));
        if (state is null) { Console.WriteLine($"no region '{regionId}'"); return 2; }
        var report = SecessionEvents.Force(c, state, string.IsNullOrWhiteSpace(culture) ? null : culture);
        if (report is null) { Console.WriteLine("the region did not secede"); return 2; }
        var parent = report.Parent;
        var born = report.Seceded;
        var war = c.Wars.LastOrDefault(w => !w.Ended && w.Involves(parent.Id) && w.Involves(born.Id)) ??
                  CampaignDiplomacy.DeclareWar(c, parent.Id, born.Id, state.MapIndex, "secession", onOwnInitiative: false);
        if (war is null) { Console.WriteLine($"{parent.Tag} could not go to war with {born.Tag}"); return 2; }
        Console.WriteLine($"SECESSION {born.Tag} '{born.Name}' from {parent.Tag}: {report.Regions} region(s), " +
                          $"{report.Population:N0} people, outcome {report.Outcome}, war '{war.Name}' civil={war.CivilConflict}");
        Line(c, war, parent, born);
        for (var day = 1; day <= days; day++)
        {
            c.TickDay();
            if (war.Ended)
            {
                Line(c, war, parent, born);
                Console.WriteLine($"ENDED day {war.EndedDay} after {war.EndedDay - war.StartedDay}d winner=" +
                                  $"{(war.WinnerId >= 0 ? c.NationById(war.WinnerId)?.Tag : "-")} :: {war.PeaceSummary}");
                Console.WriteLine($"AFTER {born.Tag} regions={c.StatesOf(born).Length} population={born.Population:N0} " +
                                  $"{parent.Tag} regions={c.StatesOf(parent).Length}");
                return 0;
            }
            if (day % 14 == 0) Line(c, war, parent, born);
        }
        Console.WriteLine("STILL FIGHTING at the end of the run");
        return 0;
    }

    private static void Line(Campaign c, StrategicWar war, Nation parent, Nation born)
    {
        var map = c.ProvinceMap;
        var owners = map.ProvinceToNation;
        var held = c.ProvinceController;
        int bornHeldByParent = 0, bornOwned = 0, parentHeldByBorn = 0;
        for (var province = 1; province <= map.ProvinceCount; province++)
        {
            if (owners[province] == born.MapIndex)
            {
                bornOwned++;
                if (held[province] == parent.MapIndex) bornHeldByParent++;
            }
            else if (owners[province] == parent.MapIndex && held[province] == born.MapIndex) parentHeldByBorn++;
        }
        string Army(Nation n)
        {
            var own = c.Armies.Where(a => a.NationId == n.Id).ToArray();
            var militia = own.Count(a => a.TemplateId == MilitiaCore.TemplateId);
            var inWar = own.Count(a => a.AssignedWarId == war.Id);
            var attacking = own.Count(a => a.OrderKind is ArmyOrder.AdvanceFront or ArmyOrder.Spearhead);
            var battle = own.Count(a => a.InBattle);
            return $"{own.Length} div ({militia} militia, {inWar} on this war, {attacking} attacking, {battle} in battle)";
        }
        Console.WriteLine($" day {c.ElapsedDays - war.StartedDay,4} score {war.WarScore,7:0.0} " +
                          $"{born.Tag} land {bornOwned} of which {parent.Tag} holds {bornHeldByParent}; " +
                          $"{born.Tag} holds {parentHeldByBorn} of {parent.Tag}'s | {parent.Tag}: {Army(parent)} | " +
                          $"{born.Tag}: {Army(born)} | fronts {war.AttackerFront.Count}/{war.DefenderFront.Count} " +
                          $"support {parent.WarSupport:P0}/{born.WarSupport:P0} last '{war.LastNegotiation}'");
    }
}
