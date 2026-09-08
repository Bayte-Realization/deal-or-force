using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class OffensivePlanTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception("FAIL: " + label); Console.WriteLine("PASS: " + label); }
    public static void Run(Campaign c)
    {
        var map = c.ProvinceMap; var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR");
        c.Armies.Clear(); c.Battles.Clear(); c.FrontLines.Clear(); c.Fleets.Clear();
        c.ConfigureSinglePlayer(rus.Id);
        Array.Fill(c.ProvinceController, (ushort)ukr.MapIndex);
        var origin = Enumerable.Range(1,map.ProvinceCount).Where(p=>!map.IsWater(p)&&map.Neighbors[p].Length>3)
            .OrderBy(p=>Math.Abs(map.CenterOf(p).Latitude-50)+Math.Abs(map.CenterOf(p).Longitude-35)).First();
        var distances = new Dictionary<int,int>{{origin,0}}; var queue = new Queue<int>(); queue.Enqueue(origin);
        while(queue.Count>0)
        {
            var p=queue.Dequeue(); if(distances[p]>=5)continue;
            foreach(var n in map.Neighbors[p]) if(!map.IsWater(n)&&distances.TryAdd(n,distances[p]+1))queue.Enqueue(n);
        }
        c.ProvinceController[origin]=(ushort)rus.MapIndex;
        var goal=distances.First(x=>x.Value==3).Key;
        var next=map.Neighbors[goal].First(n=>distances.GetValueOrDefault(n)>=3 && !map.IsWater(n));
        var goals=new[]{goal,next};
        var armies=Enumerable.Range(0,2).Select(i=>new Army{Id=9600+i,NationId=rus.Id,ProvinceId=origin,
            StateMapIndex=map.ProvinceToState[origin],Divisions=1,Strength=1,Organisation=1,SupplyLevel=1,ReadyDay=0}).ToArray();
        foreach(var a in armies) { (a.Latitude,a.Longitude)=map.CenterOf(origin); c.Armies.Add(a); }
        var ids=armies.Select(a=>a.Id).ToArray();
        var draw=new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=goals,ArmyIds=ids};
        draw.Apply(c); Check(c.FrontLines.Count==0,"offensive requires an assigned source front");
        new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Defence,Provinces=new[]{origin},ArmyIds=ids}.Apply(c);
        Check(c.FrontLines.Count==1,"source border front created");
        Check(NetworkCommandCodec.TryEncode(draw,out var type,out var json)&&NetworkCommandCodec.TryDecode(type,json,out _),"offensive drawing uses network codec");
        draw.Apply(c);
        var line=BattlePlanning.LineFor(c,armies[0]);
        Check(line is {Kind:FrontLineKind.Offensive} && line.ObjectiveSequence.SequenceEqual(goals),"objective stroke order preserved, not seeded from front");
        Check(line.SourceProvinces.Contains(origin)&&!line.Provinces.Contains(origin),"departure front separate from objective");
        Check(line.Objectives.Values.Distinct().Count()==2,"divisions receive separate goal sectors");
        Check(armies.All(a=>!a.PlanActive&&a.PlannedRoute.Count==0),"preparing does not march into enemy territory");
        var saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"offensive-tests"),"offensive-tests");
        var restored=saved.Campaign.FrontLines.Single();
        Check(restored.ObjectiveSequence.SequenceEqual(goals)&&restored.SourceProvinces.SetEquals(line.SourceProvinces)&&restored.Objectives.Count==2,
            "source, ordered objective and division assignments survive save/network snapshot");
        var before=line.Id;
        new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=new[]{origin},ArmyIds=ids}.Apply(c);
        Check(armies.All(a=>a.FrontLineId==before),"friendly/invalid objective preserves existing plan");
        var oldControl=(ushort[])c.ProvinceController.Clone();
        Array.Fill(c.ProvinceController,(ushort)c.NationByTag("USA").MapIndex);
        c.ProvinceController[origin]=(ushort)rus.MapIndex;
        foreach(var p in goals)c.ProvinceController[p]=(ushort)ukr.MapIndex;
        draw.Apply(c);
        Check(c.FrontLines.Count==1&&armies.All(a=>a.FrontLineId==before),"unreachable objective across neutral territory rejected atomically");
        Array.Copy(oldControl,c.ProvinceController,oldControl.Length);
        new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=goals.Reverse().ToArray(),ArmyIds=ids,EditLineId=before}.Apply(c);
        line=BattlePlanning.LineFor(c,armies[0]);
        Check(line.Id==before&&line.ObjectiveSequence.SequenceEqual(goals.Reverse()),"editing updates objective in place without duplicating plan");
        foreach(var a in armies)new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=a.Id,Active=true}.Apply(c);
        Check(armies.All(a=>a.PlanActive&&a.PlannedRoute.Count>=3&&line.Provinces.Contains(a.OrderProvinceId)),"execute assigns real multi-province corridors to objectives");
        new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=armies[0].Id,Active=false}.Apply(c);
        Check(!armies[0].PlanActive&&armies[0].Route.Count==0&&armies[0].PlannedRoute.Count==0,"Prepare pauses locally without marching back to old front");
        new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=armies[0].Id,Active=true}.Apply(c);
        var war=c.Wars.First(w=>!w.Ended&&w.Involves(rus.Id)&&w.Involves(ukr.Id));
        war.FrontDirty=false; war.LastFrontDay=c.ElapsedDays;
        for(var day=0;day<120 && armies.Any(a=>a.ProvinceId!=line.Objectives[a.Id]);day++)
        {
            foreach(var a in armies){a.ReadyDay=0;a.SupplyLevel=1;a.Organisation=1;}
            MilitaryOperations.Tick(c); Warfare.Tick(c);
        }
        Check(armies.All(a=>a.ProvinceId==line.Objectives[a.Id]),"troops actually reach assigned destination sectors through empty hostile provinces");
        var final=armies.Select(a=>a.ProvinceId).ToArray();
        for(var day=0;day<10;day++){ foreach(var a in armies)a.ReadyDay=0; MilitaryOperations.Tick(c); Warfare.Tick(c); }
        Check(armies.Select(a=>a.ProvinceId).SequenceEqual(final)&&armies.All(a=>!a.PlanActive),"completed offensive stops instead of attacking beyond drawn goal");
    }
}
