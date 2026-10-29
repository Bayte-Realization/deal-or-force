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
        var last=map.Neighbors[goal].First(n=>distances.GetValueOrDefault(n)>3 && n!=next && !map.IsWater(n));
        var phaseGoals=new List<int>{last};
        for(var cursor=0;phaseGoals.Count<OffensivePlanning.MaxPhases-1;cursor++)
            foreach(var p in map.Neighbors[phaseGoals[cursor]])
                if(!map.IsWater(p)&&c.ProvinceController[p]==ukr.MapIndex&&!phaseGoals.Contains(p))phaseGoals.Add(p);
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
        var append=new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=new[]{last},ArmyIds=ids,EditLineId=before,AppendOffensivePhase=true};
        var onePhaseFingerprint=CampaignFingerprint.Of(c);
        append.Apply(c);
        Check(OffensivePlanning.PhaseCount(line)==2&&line.ActiveOffensivePhase==0&&line.ObjectiveSequence.SequenceEqual(goals.Reverse())&&
            OffensivePlanning.PhaseGoals(line,1).SequenceEqual(new[]{last})&&armies.All(a=>!a.PlanActive),
            $"append preserves the active objective and adds a future phase: phases={OffensivePlanning.PhaseCount(line)} active={line.ActiveOffensivePhase} goals=[{string.Join(',',line.ObjectiveSequence)}] next=[{string.Join(',',OffensivePlanning.PhaseGoals(line,1))}] expected_next={last} preparing={armies.Count(a=>!a.PlanActive)}/{armies.Length}");
        Check(CampaignFingerprint.Of(c)!=onePhaseFingerprint&&NetworkCommandCodec.TryEncode(append,out type,out json)&&NetworkCommandCodec.TryDecode(type,json,out _),"phases affect fingerprint and use network codec");
        new DrawFrontLineCommand{IssuerId=ukr.Id,Kind=FrontLineKind.Offensive,Provinces=new[]{last},ArmyIds=ids,EditLineId=before,AppendOffensivePhase=true}.Apply(c);
        new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=new[]{origin},ArmyIds=ids,EditLineId=before,AppendOffensivePhase=true}.Apply(c);
        Check(OffensivePlanning.PhaseCount(line)==2,"foreign and invalid phase commands preserve the plan atomically");
        var beforeLimitFingerprint=CampaignFingerprint.Of(c);
        for(var phase=2;phase<OffensivePlanning.MaxPhases;phase++)
        {
            new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=new[]{phaseGoals[phase-1]},ArmyIds=ids,
                EditLineId=before,AppendOffensivePhase=true}.Apply(c);
            Check(OffensivePlanning.PhaseCount(line)==phase+1,$"append distinct phase {phase+1}/{OffensivePlanning.MaxPhases} from {phaseGoals[phase-2]} to {phaseGoals[phase-1]}");
        }
        Check(OffensivePlanning.PhaseCount(line)==OffensivePlanning.MaxPhases&&line.ActiveOffensivePhase==0,"a plan accepts twenty-four sequential phases");
        var fullPlanFingerprint=CampaignFingerprint.Of(c);
        new DrawFrontLineCommand{IssuerId=rus.Id,Kind=FrontLineKind.Offensive,Provinces=new[]{phaseGoals[^1]},ArmyIds=ids,
            EditLineId=before,AppendOffensivePhase=true}.Apply(c);
        Check(OffensivePlanning.PhaseCount(line)==OffensivePlanning.MaxPhases&&line.ActiveOffensivePhase==0&&CampaignFingerprint.Of(c)==fullPlanFingerprint,
            "the twenty-fifth phase is rejected without changing the plan");
        line.OffensivePhases.RemoveRange(2,line.OffensivePhases.Count-2);
        Check(CampaignFingerprint.Of(c)==beforeLimitFingerprint,"removing the limit fixture restores the original two-phase campaign");
        saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"offensive-tests"),"offensive-tests");
        restored=saved.Campaign.FrontLines.Single();
        Check(restored.OffensivePhases.Count==2&&restored.ActiveOffensivePhase==0&&restored.OffensivePhases[1].ObjectiveSequence.SequenceEqual(new[]{last}),"all phases and active index survive network snapshot");
        restored.OffensivePhases.Clear();
        Check(OffensivePlanning.PhaseGoals(restored,0).SequenceEqual(restored.ObjectiveSequence),"legacy one-objective save remains readable");
        foreach(var a in armies)new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=a.Id,Active=true}.Apply(c);
        Check(armies.All(a=>a.PlanActive&&a.PlannedRoute.Count>=3&&line.Provinces.Contains(a.OrderProvinceId)),"execute assigns real multi-province corridors to objectives");
        new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=armies[0].Id,Active=false}.Apply(c);
        Check(!armies[0].PlanActive&&armies[0].Route.Count==0&&armies[0].PlannedRoute.Count==0,"Prepare pauses locally without marching back to old front");
        new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=armies[0].Id,Active=true}.Apply(c);
        var war=c.Wars.First(w=>!w.Ended&&w.Involves(rus.Id)&&w.Involves(ukr.Id));
        war.FrontDirty=false; war.LastFrontDay=c.ElapsedDays;
        for(var day=0;day<120 && armies.Any(a=>a.ProvinceId!=line.Objectives[a.Id]);day++)
        {
            foreach(var a in armies){a.ReadyDay=0;a.SupplyLevel=1;a.Organisation=1;a.Planning=0;}
            MilitaryOperations.Tick(c); Warfare.Tick(c);
        }
        Check(line.ActiveOffensivePhase==0&&armies.All(a=>a.ProvinceId==line.Objectives[a.Id]&&a.PlanActive),"troops reach the first phase and wait for the shared transition");
        var currentObjectives=new Dictionary<int,int>(line.Objectives);
        line.Objectives.Clear();
        MilitaryOperations.Tick(c);
        Check(line.ActiveOffensivePhase==0&&line.OffensiveRegroupDay<0,"a phase without assigned participants cannot regroup or advance");
        foreach(var pair in currentObjectives)line.Objectives[pair.Key]=pair.Value;
        foreach(var a in armies){a.ReadyDay=0;a.SupplyLevel=1;a.Organisation=1;a.Planning=0;}
        MilitaryOperations.Tick(c);
        Check(line.ActiveOffensivePhase==0&&line.OffensiveRegroupDay<0,"positions and average planning do not substitute for one fully prepared participant");
        armies[0].Planning=1;
        MilitaryOperations.Tick(c);
        Check(line.ActiveOffensivePhase==0&&line.OffensiveRegroupDay==c.ElapsedDays+6&&OffensivePlanning.RegroupDays(c,line)==6,
            "one fully prepared participant starts a six-day regroup at half prepared strength");
        armies[1].Planning=1;
        Check(OffensivePlanning.RegroupDays(c,line)==3,"full participant readiness keeps the three-day regroup minimum");
        armies[1].Planning=0;
        var regroupFingerprint=CampaignFingerprint.Of(c);
        saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"offensive-tests"),"offensive-tests");
        Check(saved.Campaign.FrontLines.Single().OffensiveRegroupDay==line.OffensiveRegroupDay&&CampaignFingerprint.Of(saved.Campaign)==regroupFingerprint,
            "regrouping deadline survives save and fingerprint");
        line.Objectives.Remove(armies[1].Id);
        var regroupDay=line.OffensiveRegroupDay;
        MilitaryOperations.Tick(c);
        Check(line.OffensiveRegroupDay==regroupDay,"an unassigned roster member does not block or restart the current objective regroup");
        line.Objectives[armies[1].Id]=currentObjectives[armies[1].Id];
        c.ProvinceController[next]=(ushort)ukr.MapIndex;
        MilitaryOperations.Tick(c);
        Check(line.ActiveOffensivePhase==0&&line.OffensiveRegroupDay<0,"losing a required objective suspends regrouping");
        c.ProvinceController[next]=(ushort)rus.MapIndex;
        MilitaryOperations.Tick(c);
        Check(line.ActiveOffensivePhase==0&&line.OffensiveRegroupDay==c.ElapsedDays+6,"recovering the objective requires a fresh readiness-scaled regroup");
        foreach(var a in armies)new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=a.Id,Active=false}.Apply(c);
        MilitaryOperations.Tick(c);
        Check(line.ActiveOffensivePhase==0&&line.OffensiveRegroupDay<0,"pausing every participant cancels a started regrouping countdown");
        foreach(var a in armies)new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=a.Id,Active=true}.Apply(c);
        armies[1].Planning=1;
        new SetArmyPlanExecutionCommand{IssuerId=rus.Id,ArmyId=armies[0].Id,Active=false}.Apply(c);
        c.IsPaused=false;
        for(var day=0;day<7&&line.ActiveOffensivePhase==0;day++){foreach(var a in armies){a.ReadyDay=0;a.SupplyLevel=1;a.Organisation=1;}armies[1].Planning=1;c.TickDay();}
        c.IsPaused=true;
        Check(line.ActiveOffensivePhase==1,"another fully prepared participant advances while a different division is paused");
        Check(line.ObjectiveSequence.SequenceEqual(new[]{last})&&line.SourceProvinces.SetEquals(goals)&&
            armies.All(a=>a.PlanActive&&a.OrderProvinceId==last),$"regrouping activates the second phase from the captured first line: phase={line.ActiveOffensivePhase} regroup={line.OffensiveRegroupDay} day={c.ElapsedDays} sources=[{string.Join(',',line.SourceProvinces)}] expected_sources=[{string.Join(',',goals)}] armies={string.Join(';',armies.Select(a=>$"{a.Id}@{a.ProvinceId}->{a.OrderProvinceId} active={a.PlanActive} battle={a.InBattle} ready={a.ReadyDay} org={a.Organisation:F2}"))}");
        saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"offensive-tests"),"offensive-tests");
        Check(saved.Campaign.FrontLines.Single().ActiveOffensivePhase==1,"active transition survives save and network snapshot");
        for(var day=0;day<120 && armies.Any(a=>a.ProvinceId!=last);day++)
        {
            foreach(var a in armies){a.ReadyDay=0;a.SupplyLevel=1;a.Organisation=1;}
            MilitaryOperations.Tick(c); Warfare.Tick(c);
        }
        Check(armies.All(a=>a.ProvinceId==last),"troops actually reach the final destination through empty hostile provinces");
        var final=armies.Select(a=>a.ProvinceId).ToArray();
        for(var day=0;day<10;day++){ foreach(var a in armies)a.ReadyDay=0; MilitaryOperations.Tick(c); Warfare.Tick(c); }
        Check(armies.Select(a=>a.ProvinceId).SequenceEqual(final)&&armies.All(a=>!a.PlanActive),"completed offensive stops instead of attacking beyond drawn goal");
        // A progressed war must not resurrect an erased plan through any daily planner.
        line.ArmyIds.Clear(); line.ArmyIds.Add(999999);
        new DeleteBattlePlanCommand { IssuerId = rus.Id, FrontLineId = line.Id }.Apply(c);
        for (var day = 0; day < 30; day++)
        {
            FrontManager.Tick(c); MilitaryOperations.Tick(c); Warfare.Tick(c);
        }
        Check(c.FrontLines.Count == 0 && armies.All(a => a.FrontLineId < 0 &&
            a.OrderKind == ArmyOrder.Manual && a.Route.Count == 0 && a.PlannedRoute.Count == 0) &&
            armies.Select(a => a.ProvinceId).SequenceEqual(final),
            "erased stale-roster plan stays erased for 30 daily planner passes without renewed movement");
    }
}
