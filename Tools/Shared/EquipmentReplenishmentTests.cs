using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

public static class EquipmentReplenishmentTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL equipment replenishment: " + text);
        Console.WriteLine("PASS equipment replenishment: " + text);
    }
    private static bool Near(float a, float b) => Math.Abs(a-b) < .00001f;

    public static Campaign Fixture()
    {
        const int width=96, height=32;
        var states=new ushort[width*height]; var owners=new ushort[states.Length]; var rgb=new byte[states.Length*3];
        for(var y=0;y<height;y++) for(var x=0;x<width;x++)
        { var at=y*width+x; var p=x/32+1; states[at]=(ushort)(1000+p); owners[at]=(ushort)(p==3?101:100); rgb[at*3+2]=(byte)p; }
        var map=ProvinceTopology.FromColorRaster(states,owners,width,height,new RgbDataMap(width,height,rgb),
            Enumerable.Range(1,3).ToDictionary(p=>p,p=>p));
        var c=new Campaign(981){ ScenarioId="equipment_replenishment" };
        c.Defines.HoursPerTick=24;
        c.Defines.StockpileMinimumTrickle=c.Defines.StockpileIndustryPerRootGdp=c.Defines.StockpileSurplusCaptureShare=0;
        for(var id=0;id<2;id++) c.Nations.Add(new Nation { Id=id,Tag="EQ"+id,Name="Equipment country "+id,
            MapIndex=100+id,Population=1000000,Gdp=100,Treasury=20,MilitarySpending=.06f,StockpilesSeeded=true });
        for(var p=1;p<=3;p++) c.States.Add(new ScenarioState { Id="eq_"+p,Name="Equipment region "+p,
            MapIndex=1000+p,NationMapIndex=p==3?101:100,NationId=p==3?"EQ1":"EQ0",Population=100000 });
        c.AttachProvinceTopology(map); c.Armies.Clear();
        c.Wars.Add(new StrategicWar{Id=12,AttackerId=0,DefenderId=1});
        foreach(var n in c.Nations) foreach(var g in StrategicStockpiles.TrackedGoods) n.Stockpiles[g]=100f;
        c.Armies.Add(new Army { Id=1,NationId=0,ProvinceId=2,StateMapIndex=1002,TemplateId="line_infantry",
            Equipment=.2f,Divisions=1,Personnel=15000,MaxPersonnel=15000,Strength=1f,SupplyLevel=1f,Organisation=.8f });
        return c;
    }

    public static void Run()
    {
        var c=Fixture(); var a=c.Armies[0]; var n=c.Nations[0]; var rng=c.Random.State;
        var bill=StrategicStockpiles.DivisionMaterialBill(UnitTypeCatalog.Get(a.TemplateId),1,MobilisationMode.Standard);
        var fp=CampaignFingerprint.Of(c); var depot=n.Stockpiles.ToDictionary(p=>p.Key,p=>p.Value);
        EquipmentLogistics.ReplenishDay(c);
        Check(Near(a.Equipment,.22f),"a supplied 20% formation receives gradual daily replacements");
        foreach(var good in EquipmentLogistics.Goods)
            Check(Near(depot[good]-n.Stockpiles[good], (a.Equipment-.2f)*bill.GetValueOrDefault(good)),"material conservation: "+good);
        Check(a.Personnel==15000 && a.Strength==1f && a.Organisation==.8f && n.ManpowerReserve==0,
            "kit delivery neither creates people nor requires unused manpower");
        Check(c.Random.State==rng && c.ElapsedDays==0,"delivery uses no random draw and skips no time");
        Check(CampaignFingerprint.Of(c)!=fp,"replaced kit and spent depots change the authoritative fingerprint");
        var actual=CampaignFingerprint.Of(c); a.Equipment+=.01f;
        Check(CampaignFingerprint.Of(c)!=actual,"equipment alone is authoritative even outside a withdrawal");
        actual=CampaignFingerprint.Of(c); n.DailyEquipmentReplacement["Arms"]=500;
        Check(CampaignFingerprint.Of(c)==actual,"last-day presentation totals do not alter authoritative state");
        actual=CampaignFingerprint.Of(c); EquipmentLogistics.Description(c,a);
        Check(CampaignFingerprint.Of(c)==actual,"reading price/refusal neither seeds nor spends materials");
        actual=CampaignFingerprint.Of(c); c.Defines.EquipmentReplenishmentEnabled=false;
        Check(CampaignFingerprint.Of(c)!=actual,"the delivery switch is visible to peer divergence checks");

        c=Fixture(); a=c.Armies[0]; n=c.Nations[0];
        n.Stockpiles["Ammunition"]=0; n.Stockpiles["Food"]=0; n.Stockpiles["Oil"]=0;
        EquipmentLogistics.ReplenishDay(c);
        Check(a.Equipment>.2f && n.Stockpiles["Ammunition"]==0 && n.Stockpiles["Food"]==0 && n.Stockpiles["Oil"]==0,
            "shells, rations and fuel are not magically filled into the equipment percentage");
        foreach(var reason in new[]{"empty", "cut off", "low supply", "at sea", "unfunded", "disabled", "full"})
        {
            c=Fixture(); a=c.Armies[0]; n=c.Nations[0];
            switch(reason)
            {
                case "empty": foreach(var g in EquipmentLogistics.Goods) n.Stockpiles[g]=0; break;
                case "cut off": a.CutOff=true; break;
                case "low supply": a.SupplyLevel=.1f; break;
                case "at sea": a.EmbarkedFleetId=2; break;
                case "unfunded": n.MilitarySpending=0; break;
                case "disabled": c.Defines.EquipmentReplenishmentEnabled=false; break;
                case "full": a.Equipment=1f; break;
            }
            var before=a.Equipment; var arms=n.Stockpiles["Arms"];
            EquipmentLogistics.ReplenishDay(c);
            Check(a.Equipment==before && n.Stockpiles["Arms"]==arms,"no free kit when "+reason);
        }
        c=Fixture(); a=c.Armies[0]; a.BattleId=50;
        var combatLimit=EquipmentLogistics.DailyLimit(c,a); EquipmentLogistics.ReplenishDay(c);
        Check(Near(a.Equipment,.2f+combatLimit) && combatLimit>0 && combatLimit<.02f,"delivery in battle is possible but slower");

        c=Fixture(); a=c.Armies[0]; a.Equipment=.995f;
        EquipmentLogistics.ReplenishDay(c);
        Check(a.Equipment<=1f && a.Equipment>.9999f,"the last delivery is capped by missing equipment, never above 100%");
        c=Fixture(); a=c.Armies[0]; a.CutOff=true; a.Equipment=.6f;
        var people=a.Personnel; Attrition.Tick(c);
        Check(a.Personnel<people && a.Equipment==.6f,"starvation losses in men do not overwrite the separate equipment percentage");

        foreach(var value in new[]{.1f,.2f,.7f})
        {
            c=Fixture(); a=c.Armies[0]; a.Equipment=value; n=c.Nations[0];
            n.Stockpiles["Arms"]=0; StrategicStockpiles.TickDay(c);
            Check(a.Equipment<=value && a.Equipment>=Math.Min(.2f,value),"dry-depot wear neither heals nor collapses below its floor: "+value);
        }
        c=Fixture(); a=c.Armies[0]; n=c.Nations[0]; n.Stockpiles["Arms"]=c.Defines.StockpileDivisionDailyArms;
        StrategicStockpiles.TickDay(c);
        Check(Near(a.Equipment,.2f) && Near(n.Stockpiles["Arms"],0),"today's maintenance has priority over replacing old kit");
        Check(StrategicStockpiles.Coverage(n,"Arms")==1f,"optional replacements do not falsify operational coverage");
        c=Fixture(); a=c.Armies[0]; n=c.Nations[0];
        var beforeBurn=n.Stockpiles["Arms"]; StrategicStockpiles.TickDay(c);
        Check(a.Equipment>.2f && n.DailyEquipmentReplacement.GetValueOrDefault("Arms")>0 &&
            n.DailyStockpileBurn["Arms"]>c.Defines.StockpileDivisionDailyArms && n.Stockpiles["Arms"]<beforeBurn,
            "the real daily depot loop records operations and physical replacement draw separately");

        SharedMaterials(); Persistence();
    }

    private static void SharedMaterials()
    {
        var c=Fixture(); var n=c.Nations[0]; var infantry=c.Armies[0];
        var armor=new Army {Id=2,NationId=0,ProvinceId=2,StateMapIndex=1002,Divisions=1,Equipment=.2f,SupplyLevel=1,
            TemplateId=UnitTypeCatalog.All.First(t=>t.Hardness>=.65f).Id};
        c.Armies.Add(armor); n.Stockpiles["MotorVehicles"]=0;
        EquipmentLogistics.ReplenishDay(c);
        Check(infantry.Equipment>.2f && armor.Equipment==.2f,"a missing truck blocks armor, not unrelated rifle deliveries");

        float[] Result(bool reverse)
        {
            var w=Fixture(); w.Armies[0].ReinforcementPriority=ReinforcementPriority.High;
            w.Armies.Add(new Army{Id=2,NationId=0,ProvinceId=2,StateMapIndex=1002,Divisions=1,Equipment=.2f,SupplyLevel=1,
                TemplateId="line_infantry",ReinforcementPriority=ReinforcementPriority.Low});
            w.Nations[0].Stockpiles["Arms"]=.02f;
            if(reverse) w.Armies.Reverse();
            EquipmentLogistics.ReplenishDay(w);
            Check(w.Nations[0].Stockpiles.Values.All(v=>float.IsFinite(v)&&v>=0),"scarce shared depots stay nonnegative");
            var high=w.Armies.Single(a=>a.Id==1).Equipment; var low=w.Armies.Single(a=>a.Id==2).Equipment;
            Check(high>low && low>.2f,"reinforcement priority weights deliveries without starving the low-priority formation");
            Check(Near((high-.2f+low-.2f)*1.2f,.02f),"two formations cannot spend the same scarce rifles twice");
            return new[]{high,low};
        }
        Check(Result(false).SequenceEqual(Result(true)),"reordering a save's army list does not change delivery shares");
    }

    private static void Persistence()
    {
        var c=Fixture(); c.ConfigureSinglePlayer(0);
        // A played campaign has opened its accounts and calibrated its economy. A raw
        // test fixture otherwise intentionally takes the legacy migrations on load.
        SovereignFinance.NormalizeAccounts(c); EconomySystem.CalibrateOutput(c); EconomySystem.Explain(c);
        EquipmentLogistics.ReplenishDay(c);
        var folder=Path.Combine(Path.GetTempPath(),"dof-equipment-"+Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        try
        {
            SaveSystem.Save(folder,"kit",c,"equipment-test"); var loaded=SaveSystem.Load(folder,"kit","equipment-test");
            // Like the game, restore the saved political layer onto the map before
            // comparing the whole-world fingerprint (which includes controllers).
            if(loaded.Success) loaded.Campaign.AttachProvinceTopology(c.ProvinceMap);
            Check(loaded.Success && loaded.Campaign.Armies[0].Equipment==c.Armies[0].Equipment &&
                loaded.Campaign.Nations[0].Stockpiles["Arms"]==c.Nations[0].Stockpiles["Arms"],"real save/load retains kit and spent materials");
            Check(CampaignFingerprint.Of(loaded.Campaign)==CampaignFingerprint.Of(c),"save/load retains the equipment fingerprint");
        }
        finally { Directory.Delete(folder,true); }
        var legacy=JsonSerializer.Deserialize<Army>("{\"Id\":5}",new JsonSerializerOptions{IncludeFields=true});
        Check(legacy.Equipment==.88f,"old Army records keep their equipment default without being repaired for free");
    }
}
