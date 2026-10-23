using System;
using System.Linq;
using DealOrForce.Simulation;

static class OrganicBlocTests
{
    static void Check(bool ok,string text)
    { if(!ok) throw new Exception("organic blocs: "+text); Console.WriteLine("PASS organic blocs: "+text); }
    static Campaign World(int count)
    {
        var c=new Campaign(20261020);
        for(var i=0;i<count;i++)
        {
            var n=c.AddNation(new Nation { Tag="B"+i,Name="Country "+i,MapIndex=501+i,Population=1_000_000,
                Gdp=i==0?5000:100,Treasury=100,PoliticalPoints=100,Stability=.8f,Region="region_"+i,OutputScale=1 });
            c.States.Add(new ScenarioState { Id="bloc_"+i,MapIndex=1001+i,NationId=n.Tag,NationMapIndex=n.MapIndex,Population=n.Population });
        }
        c.InvalidateStateIndex();
        c.AttachProvinceTopology(ProvinceTopology.Build(Enumerable.Range(0,count*4).Select(i=>(ushort)(1001+i/4)).ToArray(),
            Enumerable.Range(0,count*4).Select(i=>(ushort)(501+i/4)).ToArray(),count*4,1,_=>false));
        for(var a=0;a<count;a++) for(var b=a+1;b<count;b++) CampaignDiplomacy.Get(c,a,b).Opinion=60;
        return c;
    }
    public static void Run()
    {
        var c=World(16); var founder=c.Nations[0]; founder.NationalCourse.Dominant="global_integration";
        c.Defines.AiMayFoundOrganizations=false;
        var org=InternationalOrganizations.Seed(c,"Growing bloc","development",Enumerable.Range(0,13));
        org.DoctrineId="common_market"; org.Cohesion=.8f;
        DomesticCrisesTests.Day(c,600);
        var fp=CampaignFingerprint.Of(c); var rng=c.Random.State;
        Check(OrganizationDynamics.ExpansionRelevant(c,org,founder,c.Nations[13]) && CampaignFingerprint.Of(c)==fp && c.Random.State==rng,
            "a globally engaged leading economy can identify a cross-region partner without mutating state");
        c.Defines.OrganicOrganizationReachEnabled=false; InternationalOrganizations.Tick(c);
        Check(org.Motion is null,"legacy reach toggle reproduces the 13-member admission barrier");
        c.Defines.OrganicOrganizationReachEnabled=true; InternationalOrganizations.Tick(c);
        Check(org.Motion is { Kind:"admission",TargetId:13 },"the real AI proposes expansion beyond thirteen members into another region");
        DomesticCrisesTests.Day(c,org.Motion.EndsDay); InternationalOrganizations.Tick(c);
        DomesticCrisesTests.Day(c,c.ElapsedDays+1); InternationalOrganizations.Tick(c);
        Check(org.Members.Count==14 && org.Members.Contains(13),"assembly approval and the recipient's actual AI consent grow the bloc, not a forced seed");
        var rel=CampaignDiplomacy.Get(c,0,1); rel.Opinion=-80; rel.Stance=DiplomaticStance.Rival;
        var penalty=OrganizationDynamics.LeadershipFriction(c,org,c.Nations[1],founder);
        Check(penalty>.2f,"a small member's dispute with the leading power is not averaged away by a large membership");
        rel.Opinion=60; rel.Stance=DiplomaticStance.Neutral;
        Check(OrganizationDynamics.LeadershipFriction(c,org,c.Nations[1],founder)==0,"being large or leading is not itself a secession penalty");
        var f=World(3); var f0=f.Nations[0]; f0.NationalCourse.Dominant="global_integration";
        InternationalOrganizations.Seed(f,"Prior membership","development",new[]{0,1});
        DomesticCrisesTests.Day(f,365); var credit=f0.PoliticalPoints;
        InternationalOrganizations.Tick(f);
        var born=f.Organizations.SingleOrDefault(o=>o.FoundedDay==365 && o.FounderId==0);
        Check(born is not null && born.Motion is { Kind:"admission" },"a country already belonging to an institution can found a new, distinct cooperation bloc");
        Check(f0.PoliticalPoints==credit-f.Defines.AiOrganizationFoundingPoliticalCost,"AI institution building spends political capital as well as the ordinary cash deposit");
        var invited=born.Motion.TargetId; DomesticCrisesTests.Day(f,born.Motion.EndsDay); InternationalOrganizations.Tick(f);
        DomesticCrisesTests.Day(f,f.ElapsedDays+1); InternationalOrganizations.Tick(f);
        Check(born.Members.Contains(invited) && f.NationById(invited).Region!=f0.Region,"a new cross-region bloc reaches real consenting membership");
        var sober=World(3); sober.Nations[0].NationalCourse.Dominant="global_integration";
        sober.Nations[0].PoliticalPoints=0; InternationalOrganizations.Seed(sober,"Prior","development",new[]{0,1});
        DomesticCrisesTests.Day(sober,365); InternationalOrganizations.Tick(sober);
        Check(sober.Organizations.Count==1,"institutional founding does not borrow political points or spam free councils");

        // A hostile-members fixture, not a claim that a starting alliance should collapse.
        var world=Campaign.CreateBootstrap(20261020);
        var nato=world.Organizations.First(o=>o.Name=="NATO");
        world.Organizations.Clear(); world.Organizations.Add(nato);
        foreach(var id in nato.Members) world.NationById(id).IsHuman=false;
        for(var a=0;a<nato.Members.Count;a++) for(var b=a+1;b<nato.Members.Count;b++)
        { var r=CampaignDiplomacy.Get(world,nato.Members[a],nato.Members[b]); r.Opinion=-100; r.Stance=DiplomaticStance.Rival; }
        world.Defines.OrganizationCohesionPull=1; world.Defines.OrganizationDissolveChance=1;
        for(var day=1000;day<1007;day++) { DomesticCrisesTests.Day(world,day); OrganizationDynamics.Tick(world); }
        Check(nato.Dissolved,"the actual NATO entity fully dissolves under modeled universal estrangement; its name grants no immunity");
        var usa=world.NationByTag("USA"); usa.IsHuman=false; usa.Stability=.15f;
        Check(CivilWarOutbreak.EligibilityReason(world,usa).Length==0 && CivilWarOutbreak.Chance(world,usa)>0,
            "the actual US is eligible for civil war after institutional collapse, with no great-power exemption");
        usa.Stability=.9f;
        Check(CivilWarOutbreak.EligibilityReason(world,usa).Length>0,"a prosperous stable great power is not forced to disintegrate for spectacle");
        Check(MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States,WorldMapSource.Control,
            WorldMapSource.Width,WorldMapSource.Height,out var map,out var mapMessage),"actual US outbreak fixture loads the real province graph: "+mapMessage);
        world.AttachProvinceTopology(map); usa.Stability=.15f;
        var outbreak=CivilWarOutbreak.Break(world,usa);
        Check(outbreak?.Seceded is not null && world.Wars.Any(w=>!w.Ended && w.CivilConflict && w.Opposes(usa.Id,outbreak.Seceded.Id)),
            "a collapsed actual US creates a territorial armed opposition and an active civil war on the real map");
    }
}
