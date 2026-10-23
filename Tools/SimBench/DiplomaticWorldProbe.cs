using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static class DiplomaticWorldProbe
{
    public static int Run(Campaign c,int days)
    {
        var initialOrganizations=c.Organizations.Select(o=>o.Id).ToHashSet();
        var facts=new Dictionary<string,int>(); var births=new HashSet<int>();
        var resources=new DecisionResourceTrace(c); long sequence=c.Chronicle.Entries.LastOrDefault()?.Sequence??0;
        var initialTop=c.Nations.Where(n=>n.Movement is null).OrderByDescending(n=>n.Gdp).Take(10).Select(n=>n.Tag).ToHashSet();
        var clock=Stopwatch.StartNew(); var failures=0; var missionWeeks=0;
        var paid=new Dictionary<(long Pair,int Issuer,int Start),int>();
        c.IsPaused=false;
        for(var day=0;day<days;day++)
        {
            c.TickDay(); c.FinishStep(); resources.Observe(c);
            foreach(var fact in c.Chronicle.Entries)
            {
                if(fact.Sequence<=sequence) continue;
                sequence=fact.Sequence; facts[fact.Kind]=facts.GetValueOrDefault(fact.Kind)+1;
            }
            foreach(var pair in c.Relations)
            {
                void Observe(int issuer,DiplomaticMission m)
                {
                    if(m is null) return;
                    var key=(pair.Key,issuer,m.StartedDay); var old=paid.GetValueOrDefault(key);
                    missionWeeks+=Math.Max(0,m.PaidWeeks-old); paid[key]=m.PaidWeeks;
                }
                Observe(pair.Value.A,pair.Value.MissionByA); Observe(pair.Value.B,pair.Value.MissionByB);
            }
            foreach(var n in c.Nations) if(!float.IsFinite(n.Gdp)||!float.IsFinite(n.PoliticalPoints)||n.PoliticalPoints<0||n.Population<0) failures++;
            if(day%365==364 || day==days-1)
            {
                var newBlocs=c.Organizations.Where(o=>!initialOrganizations.Contains(o.Id)).ToArray();
                foreach(var o in newBlocs) births.Add(o.Id);
                var nato=c.Organizations.FirstOrDefault(o=>o.Name=="NATO"); var usa=c.NationByTag("USA");
                var top=c.Nations.Where(n=>n.Movement is null).OrderByDescending(n=>n.Gdp).Take(10).ToArray();
                Console.WriteLine($"DIPLOMACY_YEAR seed={c.Seed} day={c.ElapsedDays} newBlocs={newBlocs.Length} largestNew={newBlocs.Select(o=>o.Members.Count).DefaultIfEmpty(0).Max()} crossRegionNew={newBlocs.Count(o=>o.Members.Select(c.NationById).Where(n=>n is not null).Select(n=>n.Region).Distinct().Count()>1)} NATO_members={nato?.Members.Count} NATO_dissolved={nato?.Dissolved} USA_stability={usa?.Stability:0.000} USA_GDP={usa?.Gdp:0.0}");
                Console.WriteLine("POWER_TOP "+string.Join("; ",top.Select(n=>$"{n.Tag}:{n.Gdp:0}"))+" entrants="+string.Join(",",top.Where(n=>!initialTop.Contains(n.Tag)).Select(n=>n.Tag)));
            }
        }
        var resourceFailures=resources.Print(c);
        Console.WriteLine("DIPLOMACY_FACTS "+string.Join("; ",facts.Where(x=>x.Key.Contains("organization")||x.Key.Contains("mission")||x.Key.Contains("civil_war")||x.Key.Contains("secess")||x.Key.Contains("crisis")).OrderByDescending(x=>x.Value).Select(x=>x.Key+"="+x.Value)));
        Console.WriteLine($"DIPLOMACY_PROBE seed={c.Seed} days={days} missionPaidWeeks={missionWeeks} bornInstitutions={births.Count} failures={failures+resourceFailures} fingerprint={CampaignFingerprint.Of(c):X16} seconds={clock.Elapsed.TotalSeconds:0.0}");
        return failures+resourceFailures==0?0:1;
    }
}
