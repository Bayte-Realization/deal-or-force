using System;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

static class TerritoryDiagnosisReport
{
    public static int Run(Campaign c, string firstTag, string secondTag)
    {
        var first=c.NationByTag(firstTag); var second=c.NationByTag(secondTag);
        if(first is null || second is null) return 2;
        Console.WriteLine($"TERRITORY_DIAG date={c.Date} day={c.ElapsedDays}; read-only; no save writes");
        foreach(var n in new[]{first,second})
        {
            Console.WriteLine($"COUNTRY {n.Tag} id={n.Id} map={n.MapIndex} capital={n.CapitalStateMapIndex} " +
                $"cash={n.Treasury:R} stability={n.Stability:R} strength={AiStatecraft.StrengthOf(c,n):R} " +
                $"secededFrom={c.NationById(n.SecededFromId)?.Tag} movement={n.Movement?.Id} statehood={n.Movement?.StatehoodDay}");
            foreach(var s in c.States.Where(s=>s.NationMapIndex==n.MapIndex ||
                (TerritorialClaims.TitleOf(c,n,s)==TerritorialTitle.Homeland && s.NationMapIndex!=n.MapIndex)))
                Console.WriteLine($"LAND {n.Tag} {s.Id}/{s.MapIndex} owner={c.Nations.FirstOrDefault(x=>x.MapIndex==s.NationMapIndex)?.Tag} " +
                    $"title={TerritorialClaims.TitleOf(c,n,s)} people={s.Population} " +
                    $"cultures={string.Join(",",s.PopulationGroups.GroupBy(g=>g.Culture).OrderByDescending(g=>g.Sum(x=>x.Size)).Take(4).Select(g=>$"{g.Key}:{g.Sum(x=>x.Size)}"))}");
        }
        var relation=CampaignDiplomacy.Peek(c,first.Id,second.Id);
        var previewWar = new StrategicWar { AttackerId = first.Id, DefenderId = second.Id };
        var recovery = Reintegration.CapitulationGround(c, previewWar, second, first);
        Console.WriteLine($"CAPITULATION_PREVIEW {second.Tag}->{first.Tag} recoverable_provinces={recovery.Count}; " +
            "read-only hypothetical surrender, not a replay of the historical treaty");
        Console.WriteLine($"RELATION stance={relation?.Stance} opinion={relation?.Opinion} truce={relation?.TruceUntil} nap={relation?.NonAggressionPact}");
        foreach(var w in c.Wars.Where(w=>w.Involves(first.Id) && w.Involves(second.Id)).OrderByDescending(w=>w.StartedDay))
            Console.WriteLine($"WAR id={w.Id} start={w.StartedDay} ended={w.Ended} civil={w.CivilConflict} winner={c.NationById(w.WinnerId)?.Tag} " +
                $"proposer={c.NationById(w.PeaceProposerId)?.Tag} score={w.WarScore:R} annexed={w.AnnexedProvinces} " +
                $"support={w.AttackerWarSupport:R}/{w.DefenderWarSupport:R} losses={w.AttackerLosses:R}/{w.DefenderLosses:R} cause={w.Justification} negotiation={w.LastNegotiation}");
        foreach(var e in c.Chronicle.Entries.Where(e=>(e.ActorId==first.Id && e.TargetId==second.Id) ||
            (e.ActorId==second.Id && e.TargetId==first.Id)).OrderByDescending(e=>e.Sequence).Take(30))
            Console.WriteLine($"HISTORY day={e.Day} {e.Kind} {e.ActorName}->{e.TargetName} subject={e.Subject} place={e.Place}");
        return 0;
    }
}
