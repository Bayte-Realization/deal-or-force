using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

static class ProvinceVisionTests
{
    static void Check(bool ok, string name)
    { if (!ok) throw new Exception("FAIL: " + name); Console.WriteLine("PASS: " + name); }
    public static void Run(Campaign c)
    {
        var rus = c.NationByTag("RUS"); var ukr = c.NationByTag("UKR"); var map = c.ProvinceMap;
        c.ConfigureSinglePlayer(rus.Id);
        c.Armies.Clear(); c.Battles.Clear(); c.Fleets.Clear();
        Array.Fill(c.ProvinceController, (ushort)ukr.MapIndex);
        var origin = Enumerable.Range(1, map.ProvinceCount).First(p => !map.IsWater(p) && map.Neighbors[p].Length > 3);
        c.ProvinceController[origin] = (ushort)rus.MapIndex;
        c.Defines.BorderVisionDepth = 3; c.Defines.ArmyVisionDepth = 1;
        CampaignIntel.Invalidate(c);
        var distances = new Dictionary<int,int>{{origin,0}}; var queue = new Queue<int>(); queue.Enqueue(origin);
        while(queue.Count > 0)
        {
            var p = queue.Dequeue(); if(distances[p] >= 4) continue;
            foreach(var n in map.Neighbors[p]) if(distances.TryAdd(n,distances[p]+1)) queue.Enqueue(n);
        }
        var third = distances.First(kv => kv.Value == 3).Key;
        var fourth = distances.First(kv => kv.Value == 4).Key;
        Check(CampaignIntel.CanSeeProvince(c,rus.Id,third), "border vision includes third foreign province");
        Check(!CampaignIntel.CanSeeProvince(c,rus.Id,fourth), "fourth foreign province stays in fog");
        var view = CampaignIntel.Vision(c,rus.Id); var revision=view.Revision;
        for(var i=0;i<1000;i++) CampaignIntel.CanSeeProvince(c,rus.Id,third);
        Check(view.Revision==revision,"visibility cached across repeated queries");
        var enemy=new Army{Id=901,NationId=ukr.Id,ProvinceId=third}; c.Armies.Add(enemy);
        Check(CampaignIntel.CanSeeArmy(c,rus.Id,enemy),"foreign border division visible");
        Check(!ArmyInteraction.CanSelect(c,enemy),"visible enemy still cannot be selected or commanded");
        var remote=Enumerable.Range(1,map.ProvinceCount).First(p=>!map.IsWater(p) && !distances.ContainsKey(p) &&
            map.Neighbors[p].Length>0 && map.Neighbors[p].All(n=>!distances.ContainsKey(n)));
        enemy.ProvinceId=remote; CampaignIntel.Invalidate(c);
        Check(!CampaignIntel.CanSeeArmy(c,rus.Id,enemy),"distant enemy stays hidden");
        var own=new Army{Id=902,NationId=rus.Id,ProvinceId=map.Neighbors[remote][0]};c.Armies.Add(own);
        Check(CampaignIntel.CanSeeArmy(c,rus.Id,enemy),"own division reveals adjacent enemy behind the border belt");
        Check(CampaignIntel.CanSeeArmy(c,ukr.Id,own),"AI viewer uses the same contact rules");
        own.ProvinceId=origin; CampaignIntel.Invalidate(c);
        Check(!CampaignIntel.CanSeeArmy(c,rus.Id,enemy),"leaving contact restores fog without stale enemy tracking");
        var battle=new LandBattle{Id=901,AttackerId=rus.Id,DefenderId=ukr.Id,ProvinceId=remote};
        battle.Attackers.Add(own.Id);battle.Defenders.Add(enemy.Id);c.Battles.Add(battle);
        Check(CampaignIntel.CanSeeArmy(c,rus.Id,enemy) && CampaignIntel.CanSeeProvince(c,rus.Id,remote),
            "remote battle reveals defender and beach even without a land connection");
        battle.Ended=true;CampaignIntel.Invalidate(c);
        Check(!CampaignIntel.CanSeeArmy(c,rus.Id,enemy),"ended remote battle does not grant permanent vision");
        var beach=map.CenterOf(remote);
        var fleet=new Fleet{Id=901,NationId=rus.Id,Hulls=20,Mission=FleetMission.Invasion,TargetProvinceId=remote,
            Latitude=beach.Latitude+10,Longitude=beach.Longitude+20};c.Fleets.Add(fleet);
        Check(!CampaignIntel.CanSeeProvince(c,rus.Id,remote),"issuing a distant invasion order does not reveal its target");
        fleet.Latitude=beach.Latitude;fleet.Longitude=beach.Longitude;CampaignIntel.Invalidate(c);
        Check(CampaignIntel.CanSeeArmy(c,rus.Id,enemy),"approaching invasion fleet reveals beach defenders");
        c.Fleets.Clear();CampaignIntel.Invalidate(c);
        Check(!CampaignIntel.CanInspect(rus.Id,ukr.Id),"border reconnaissance does not reveal foreign national economy");
    }
}
