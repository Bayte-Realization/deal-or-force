using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;

/// <summary>
/// Supply starts at a root and travels over ground the country controls. These tests pin the
/// difference between owning a province and being able to feed it: a formation circled on its
/// own soil is cut off, a formation with a corridor behind it is not, an island of one's own is
/// reached by the sea lane, and an evacuated government retains one connected rear.
/// </summary>
public static class SupplyNetworkTests
{
    private static void Check(bool ok, string text)
    {
        if (!ok) throw new Exception("FAIL supply network: " + text);
        Console.WriteLine("PASS supply network: " + text);
    }

    private static Army Formation(int id, int province)
    {
        return new Army { Id=id,NationId=0,ProvinceId=province,StateMapIndex=1000+province,
            TemplateId="line_infantry",Divisions=1,Personnel=10000,MaxPersonnel=10000,
            Strength=1f,SupplyLevel=1f,Organisation=.8f };
    }

    /// <summary>
    /// Five provinces in a column, all the country's own soil. Army one stands on the second,
    /// army two on the fifth, and the third province between them is held by whoever the test
    /// says - the corridor, and nothing else, is what stands between the pocket and its capital.
    ///
    /// The column runs north to south on purpose: the province graph closes round the globe in
    /// longitude, so a row of provinces would be a corridor round the back of the world.
    /// </summary>
    private static Campaign Rack(ushort corridor)
    {
        const int width=32, height=160;
        var states=new ushort[width*height]; var control=new ushort[states.Length]; var rgb=new byte[states.Length*3];
        for(var y=0;y<height;y++) for(var x=0;x<width;x++)
        { var at=y*width+x; var p=y/32+1; states[at]=(ushort)(1000+p); control[at]=100; rgb[at*3+2]=(byte)p; }
        var map=ProvinceTopology.FromColorRaster(states,control,width,height,new RgbDataMap(width,height,rgb),
            Enumerable.Range(1,5).ToDictionary(p=>p,p=>p));
        var c=new Campaign(1201){ ScenarioId="supply_network" };
        c.Nations.Add(new Nation { Id=0,Tag="SN0",Name="Network country",MapIndex=100,Population=4000000,
            Gdp=400,Treasury=50,MilitarySpending=.06f,StockpilesSeeded=true,CapitalProvinceId=1,
            CapitalStateMapIndex=1001 });
        c.Nations.Add(new Nation { Id=1,Tag="SN1",Name="Corridor country",MapIndex=101,Population=2000000,
            Gdp=200,Treasury=50,MilitarySpending=.06f,StockpilesSeeded=true });
        for(var p=1;p<=5;p++) c.States.Add(new ScenarioState { Id="sn_"+p,Name="Network region "+p,
            MapIndex=1000+p,NationMapIndex=100,NationId="SN0",Population=100000 });
        c.AttachProvinceTopology(map);
        c.ProvinceController[3]=corridor;
        c.Armies.Clear();
        c.Armies.Add(Formation(1,2));
        c.Armies.Add(Formation(2,5));
        return c;
    }

    /// <summary>
    /// Two provinces with open sea between them: the far one is the country's own ground that no
    /// road reaches, which is every island and every beachhead on the map. North to south again,
    /// for the same reason the rack runs that way.
    ///
    /// The raster is larger than the navy's 360x180 sea grid on purpose: a harbour is found in a
    /// grid square that holds both land and sea, and a map of a few dozen rows puts one raster row
    /// in each square, so a coast would never be seen at all.
    /// </summary>
    private static Campaign Island()
    {
        const int width=720, height=360;
        var states=new ushort[width*height]; var control=new ushort[states.Length]; var rgb=new byte[states.Length*3];
        for(var y=0;y<height;y++) for(var x=0;x<width;x++)
        {
            var at=y*width+x; var p=y<=120?1:y<=240?0:2;
            if(p==0) continue;
            states[at]=(ushort)(1000+p); control[at]=100; rgb[at*3+2]=(byte)p;
        }
        var map=ProvinceTopology.FromColorRaster(states,control,width,height,new RgbDataMap(width,height,rgb),
            new Dictionary<int,int>{{1,1},{2,2}});
        var c=new Campaign(1202){ ScenarioId="supply_network" };
        c.Nations.Add(new Nation { Id=0,Tag="SN0",Name="Island country",MapIndex=100,Population=1000000,
            Gdp=100,Treasury=20,MilitarySpending=.06f,StockpilesSeeded=true,CapitalProvinceId=1,
            CapitalStateMapIndex=1001 });
        for(var p=1;p<=2;p++) c.States.Add(new ScenarioState { Id="isl_"+p,Name="Island region "+p,
            MapIndex=1000+p,NationMapIndex=100,NationId="SN0",Population=50000 });
        c.AttachProvinceTopology(map);
        c.Armies.Clear();
        c.Armies.Add(Formation(1,2));
        return c;
    }

    public static void Run()
    {
        // The defect this slice exists to kill: an encircled division standing on its own soil
        // was a supply root of its own, so closing a pocket cost the enemy nothing.
        var c=Rack(101);
        LogisticsSystem.Tick(c);
        var home=c.Armies[0]; var pocket=c.Armies[1];
        Check(!home.CutOff && home.SupplyLevel>.95f,"the formation behind the front is supplied from the capital");
        Check(pocket.CutOff && pocket.SupplyDistance==-1 && pocket.SupplyCongestion==0f,
            "a formation inside a closed pocket on its own soil is cut off, not a supply root " +
            $"(cutOff={pocket.CutOff} distance={pocket.SupplyDistance} source={pocket.SupplySourceStateMapIndex} " +
            $"congestion={pocket.SupplyCongestion} level={pocket.SupplyLevel})");
        Check(pocket.SupplyLevel<.95f,"cut off ground bleeds supply instead of refilling it");

        // The same map with the corridor held: nothing about the pocket changed but the road.
        c=Rack(100);
        LogisticsSystem.Tick(c);
        home=c.Armies[0]; pocket=c.Armies[1];
        Check(!home.CutOff && !pocket.CutOff,"a corridor the country holds supplies the ground behind it");
        Check(pocket.SupplySourceStateMapIndex==1005 && pocket.SupplyDistance==0,
            "the pocket is fed by its own region once the road is open");

        // A capital taken: the first equally populous safe region is the provisional rear.
        // Its barracks is useful, but a disconnected pocket is not another national root.
        c=Rack(101);
        c.ProvinceController[1]=101;
        c.States[1].Buildings[BuildingKind.Barracks]=1;
        LogisticsSystem.Tick(c);
        Check(!c.Armies[0].CutOff,"the chosen replacement rear and its barracks supply the connected formation");
        Check(c.Armies[1].CutOff,"a pocket without the capital and without a depot is still cut off");

        // No barracks is required for a replacement rear. Here the surviving land is connected.
        c=Rack(100);
        c.ProvinceController[1]=101;
        LogisticsSystem.Tick(c);
        Check(!c.Armies[0].CutOff && !c.Armies[1].CutOff,
            "a country without barracks still supplies its connected rear after capital loss");

        // An island is ground no road reaches, but the sea lane is a road.
        c=Island();
        LogisticsSystem.Tick(c);
        var garrison=c.Armies[0];
        Check(!garrison.CutOff,"an island of one's own is reached across the sea lane, not cut off from day one");
        Check(garrison.SupplySourceStateMapIndex==1002,"the island is fed by its own region across the water");

        // Nothing here draws a random number: the same campaign gives the same supply every time,
        // and a replay that steps the same days must not diverge on this.
        c=Rack(101); var state=c.Random.State;
        LogisticsSystem.Tick(c);
        var first=c.Armies[1].SupplyLevel;
        LogisticsSystem.Tick(c);
        Check(c.Random.State==state,"supply traces use no random draw");
        Check(c.Armies[1].SupplyLevel<=first,"a repeated closed pocket does not recover between ticks");
    }
}
