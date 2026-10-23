using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using DealOrForce.Simulation;

namespace DealOrForce.Tools;

internal static class StaffWatchProbe
{
    public static int Run(Campaign c,int days)
    {
        var n=c.NationByTag("POL"); c.ConfigureSinglePlayer(n.Id);
        foreach(var p in Delegation.Portfolios) new SetDelegationCommand { IssuerId=n.Id,Portfolio=p,Delegated=true }.Apply(c);
        var clock=Stopwatch.StartNew(); long last=0;
        var events=new Dictionary<string,int>(); var formations=new HashSet<int>(); var tech=n.Technologies.Count;
        var startedProjects=new HashSet<int>(); var warnings=new Dictionary<string,int>(); var failures=0;
        c.IsPaused=false;
        for(var day=0;day<Math.Clamp(days,1,3650);day++)
        {
            c.TickDay(); c.FinishStep();
            foreach(var fact in c.Chronicle.Entries.Where(e=>e.Sequence>last))
            { events[fact.Kind]=events.GetValueOrDefault(fact.Kind)+1; last=Math.Max(last,fact.Sequence); }
            foreach(var p in c.Construction.Where(p=>p.NationId==n.Id)) startedProjects.Add(p.Id);
            foreach(var a in c.Armies.Where(a=>a.NationId==n.Id)) formations.Add(a.Id);
            if(day%30==0)
            {
                var fp=CampaignFingerprint.Of(c); var rng=c.Random.State;
                foreach(var a in CampaignAlerts.Read(c,n)) warnings[a.Id]=warnings.GetValueOrDefault(a.Id)+1;
                if(fp!=CampaignFingerprint.Of(c)||rng!=c.Random.State) failures++;
                if(!float.IsFinite(n.Treasury)||!float.IsFinite(n.Gdp)||n.Population<0) failures++;
            }
            if(day%365==364) Console.WriteLine($"STAFF_YEAR seed={c.Seed} day={c.ElapsedDays} POL pop={n.Population} gdp={n.Gdp:0.0} treasury={n.Treasury:0.0} tech={n.Technologies.Count} cabinet={n.Cabinet.Count} projects={startedProjects.Count} wars={c.Wars.Count(w=>!w.Ended)} nations={c.Nations.Count}");
        }
        Console.WriteLine("WORLD_FACTS "+string.Join("; ",events.Where(k=>k.Key.Contains("war")||k.Key.Contains("election")||k.Key.Contains("subject")||k.Key.Contains("union")||k.Key.Contains("secess")).OrderByDescending(k=>k.Value).Take(20).Select(k=>k.Key+"="+k.Value)));
        Console.WriteLine("RECURRING_HINTS "+string.Join("; ",warnings.OrderByDescending(k=>k.Value).Take(8).Select(k=>k.Key+"="+k.Value)));
        Console.WriteLine($"STAFF_WATCH seed={c.Seed} days={days} tech_gained={n.Technologies.Count-tech} construction_ids={startedProjects.Count} army_ids={formations.Count} cabinet={n.Cabinet.Count} fingerprint={CampaignFingerprint.Of(c):X16} failures={failures} seconds={clock.Elapsed.TotalSeconds:0.0}");
        return failures==0?0:1;
    }
}
