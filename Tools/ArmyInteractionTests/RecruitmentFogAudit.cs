using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class RecruitmentFogAudit
{
    static void Check(bool value, string name)
    { if (!value) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    static void NextTrainingDay(Campaign c)
    {
        // Test clock only; no production shortcut or artificial instant recruitment.
        typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours)).SetValue(c, c.ElapsedHours + 24L);
        RecruitmentSystem.Tick(c);
    }
    public static void Run(Campaign c, Army own, Army enemy, int start, int target, StrategicWar war)
    {
        var rus = c.NationById(own.NationId); var ukr = c.NationById(enemy.NationId);
        Check(CampaignIntel.CanInspect(rus.Id,rus.Id) && !CampaignIntel.CanInspect(rus.Id,ukr.Id) &&
            !CampaignIntel.CanInspect(ukr.Id,rus.Id), "same fog policy for both countries / AI viewers");
        var estimate = CampaignIntel.EstimatedStrength(c,rus.Id,ukr);
        enemy.Divisions *= 1000; enemy.Organisation = .99f; ukr.Gdp *= 100;
        Check(estimate == CampaignIntel.EstimatedStrength(c,rus.Id,ukr), "foreign strength estimate independent of hidden armies and GDP");
        Check(!CampaignIntel.PlayerCanInspect(c,ukr.Id), "player cannot inspect foreign nation");
        var region = c.States.First(s => s.NationMapIndex == rus.MapIndex && RecruitmentSystem.DeploymentProvince(c,rus.Id,s.MapIndex)>0);
        rus.Treasury = 100; rus.ManpowerReserve = 1000000;
        var oldCount = c.Armies.Count;
        foreach (var t in UnitTypeCatalog.All)
        {
            var budget = rus.Treasury; var manpower = rus.ManpowerReserve;
            var command = new RaiseArmyCommand { IssuerId=rus.Id,StateMapIndex=region.MapIndex,TemplateId=t.Id,Divisions=2 };
            command.Apply(c);
            Check(command.FailureReason == "" && c.Armies.Count == oldCount && c.Recruitment.Count==1, t.Id+" queues two independent divisions, no instant spawn");
            Check(Math.Abs(rus.Treasury-(budget-t.RecruitmentCost*2))<.001 && rus.ManpowerReserve==manpower-t.RecruitmentPersonnel*2, t.Id+" charges full price and manpower");
            var order=c.Recruitment.Single();
            new CancelRecruitmentCommand { IssuerId=ukr.Id,OrderId=order.Id }.Apply(c);
            Check(c.Recruitment.Count==1,"foreign cancellation rejected");
            new CancelRecruitmentCommand { IssuerId=rus.Id,OrderId=order.Id }.Apply(c);
            Check(c.Recruitment.Count==0 && rus.ManpowerReserve==manpower && Math.Abs(rus.Treasury-(budget-t.RecruitmentCost*.5f))<.001,"cancellation returns recruits and 75 percent funding");
        }
        Check(UnitTypeCatalog.Get("armored_corps").RecruitmentCost > UnitTypeCatalog.Get("line_infantry").RecruitmentCost,
            "authored recruitment prices loaded; armour is more expensive");
        foreach(var invalid in new[]{0f,7f,1.5f,float.NaN})
        {
            new RaiseArmyCommand { IssuerId=rus.Id,StateMapIndex=region.MapIndex,Divisions=invalid }.Apply(c);
            Check(c.Recruitment.Count==0,"invalid batch rejected: "+invalid);
        }
        rus.Treasury=0;
        new RaiseArmyCommand { IssuerId=rus.Id,StateMapIndex=region.MapIndex }.Apply(c);
        Check(c.Recruitment.Count==0,"unaffordable recruitment rejected");
        rus.Treasury=100;
        var recruit=new RaiseArmyCommand { IssuerId=rus.Id,StateMapIndex=region.MapIndex,TemplateId="armored_corps",Divisions=2 };
        recruit.Apply(c);
        var queued=c.Recruitment.Single();
        RecruitmentSystem.Tick(c);
        Check(queued.TrainedDays==0,"no same-day free training progress");
        NextTrainingDay(c);
        RecruitmentSystem.Tick(c);
        Check(queued.TrainedDays==1,"training advances exactly once per day");
        var stateOwner=region.NationMapIndex; region.NationMapIndex=ukr.MapIndex;
        NextTrainingDay(c);
        Check(queued.TrainedDays==1,"lost region pauses training");
        region.NationMapIndex=stateOwner;
        var saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"recruitment-test"),"recruitment-test");
        Check(saved.Success && saved.Campaign.Recruitment.Single().TrainedDays==1 &&
            saved.Campaign.Recruitment.Single().TemplateId=="armored_corps", "training queue and progress survive save/network snapshot");
        var cancel = new CancelRecruitmentCommand { IssuerId=rus.Id,OrderId=queued.Id };
        Check(NetworkCommandCodec.TryEncode(cancel,out var type,out var json) && NetworkCommandCodec.TryDecode(type,json,out _),"queue cancellation uses network command codec");
        for(int day=queued.TrainedDays; day<queued.DurationDays;day++) NextTrainingDay(c);
        Check(c.Recruitment.Count==0 && c.Armies.Count==oldCount+2,"completed training deploys exact batch count");
        Check(c.Armies.TakeLast(2).All(a=>a.TemplateId=="armored_corps" && a.Personnel==12000 && a.StateMapIndex==region.MapIndex),"deployed template, manpower and region match order");

        // Retreat audit: exercise the actual post-victory routine in an isolated arrangement.
        var capture=typeof(Warfare).GetMethod("Capture",BindingFlags.NonPublic|BindingFlags.Static);
        var map=c.ProvinceMap;
        c.Battles.Clear(); c.Armies.Clear(); c.Armies.Add(own);c.Armies.Add(enemy);
        own.ProvinceId=start;own.SupportOnly=false;own.BattleId=-1;own.ReadyDay=0;
        enemy.ProvinceId=target;enemy.BattleId=-1;enemy.Divisions=1;
        c.SetProvinceController(target,(ushort)ukr.MapIndex);
        var ringGuards = new System.Collections.Generic.List<Army>();
        foreach(var p in map.Neighbors[target])
        {
            c.SetProvinceController(p,(ushort)rus.MapIndex);
            if (map.IsWater(p)) continue;
            // Enemy colour alone is not a sealed ring: a deeper legal, empty corridor
            // permits a staged breakout. This post-victory test requires an occupied ring.
            var guard = new Army { Id = 99100 + p, NationId = rus.Id, ProvinceId = p, Divisions = 1, Strength = 1 };
            ringGuards.Add(guard); c.Armies.Add(guard);
        }
        capture.Invoke(null,new object[]{c,war,target,rus.Id,new[]{own},new[]{enemy},"retreat-audit"});
        Check(!c.Armies.Contains(enemy) && c.ProvinceController[target]==rus.MapIndex,
            "trapped defeated division is removed instead of remaining on captured ground");
        foreach (var guard in ringGuards) c.Armies.Remove(guard);
        var fallback=map.Neighbors[target].First(p=>!map.IsWater(p) && map.Neighbors[p].Any(n=>n!=target && !map.IsWater(n)));
        var next=map.Neighbors[fallback].First(n=>n!=target && !map.IsWater(n));
        c.SetProvinceController(target,(ushort)ukr.MapIndex);
        c.SetProvinceController(fallback,(ushort)ukr.MapIndex);
        c.SetProvinceController(next,(ushort)ukr.MapIndex);
        own.ProvinceId=start;
        // A fresh defender: the preceding one was destroyed, not a reusable survivor.
        enemy=new Army{Id=99002,NationId=ukr.Id,ProvinceId=target,Divisions=1,
            Strength=1,Organisation=1,SupplyLevel=1,BattleId=-1,ReadyDay=0};
        c.Armies.Add(enemy);
        capture.Invoke(null,new object[]{c,war,target,rus.Id,new[]{own},new[]{enemy},"retreat-audit"});
        Check(enemy.ProvinceId==fallback && enemy.ReadyDay>c.ElapsedDays && enemy.Route.Count==0,"normal defeat relocates to friendly neighbor and sets recovery deadline");
        enemy.OrderKind=ArmyOrder.Manual;enemy.AssignedWarId=-1;enemy.BattleId=-1;enemy.SupplyLevel=1;
        new MoveArmyCommand{IssuerId=ukr.Id,ArmyId=enemy.Id,TargetProvinceId=next}.Apply(c);
        MilitaryOperations.Tick(c);
        Check(enemy.MoveProgress>0 || enemy.ProvinceId==next,
            "recovering division may continue marching through friendly territory");
        new HaltArmyCommand{IssuerId=ukr.Id,ArmyId=enemy.Id}.Apply(c);
        enemy.ProvinceId=fallback;
        var counterattack=new MoveArmyCommand{IssuerId=ukr.Id,ArmyId=enemy.Id,TargetProvinceId=target};
        counterattack.Apply(c);
        Check(counterattack.Result==ArmyMoveResult.AttackIssued && enemy.ReadyDay>c.ElapsedDays,
            "counterattack can be queued without cancelling retreat recovery");
        Warfare.Tick(c);
        Check(enemy.ProvinceId==fallback && enemy.MoveProgress==0 && !enemy.InBattle &&
            !c.Battles.Any(b=>!b.Ended), "retreat recovery prevents an immediate counterattack");
    }
}
