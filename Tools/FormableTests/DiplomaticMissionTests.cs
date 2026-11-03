using System;
using System.Linq;
using System.Text.Json;
using DealOrForce.Simulation;

static class DiplomaticMissionTests
{
    static void Check(bool ok,string text)
    { if(!ok) throw new Exception("diplomatic missions: "+text); Console.WriteLine("PASS diplomatic missions: "+text); }
    static (Campaign c,Nation n,Nation other) World()
    {
        var c=new Campaign(20261020);
        var n=c.AddNation(new Nation { Tag="AAA",Name="A",MapIndex=501,Population=1_000_000,Gdp=100,Treasury=100,PoliticalPoints=100,Ideology="liberalism",OutputScale=1 });
        var other=c.AddNation(new Nation { Tag="BBB",Name="B",MapIndex=502,Population=1_000_000,Gdp=100,Treasury=100,PoliticalPoints=100,Ideology="liberalism",OutputScale=1 });
        c.States.Add(new ScenarioState { Id="mission_a",MapIndex=1001,NationId=n.Tag,NationMapIndex=501,Population=n.Population });
        c.States.Add(new ScenarioState { Id="mission_b",MapIndex=1002,NationId=other.Tag,NationMapIndex=502,Population=other.Population });
        c.InvalidateStateIndex(); c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[]{1001,1002,1001,1002},new ushort[]{501,502,501,502},2,2,_=>false));
        c.SetPlayer(n.Id); SovereignFinance.NormalizeAccounts(c); EconomySystem.Explain(c);
        return(c,n,other);
    }
    static void Days(Campaign c,int start,int end)
    { for(var day=start;day<=end;day++){ DomesticCrisesTests.Day(c,day); OpinionSystem.Tick(c); } }
    static ImproveRelationsCommand Start(Campaign c,Nation n,Nation other)
    { var cmd=new ImproveRelationsCommand { IssuerId=n.Id,TargetId=other.Id }; cmd.Apply(c); return cmd; }
    public static void Run()
    {
        var(c,n,other)=World();
        var fp=CampaignFingerprint.Of(c); var rng=c.Random.State;
        Check(DiplomaticOutreach.Refusal(c,n.Id,other.Id).Length==0 && DiplomaticOutreach.Description(c,n.Id,other.Id).Contains("180"),"preview exposes duration, price and availability");
        Check(CampaignFingerprint.Of(c)==fp && c.Random.State==rng && c.Relations.Count==0,"previews create no relation, state or random draw");
        n.PoliticalPoints=7; fp=CampaignFingerprint.Of(c);
        var poor=Start(c,n,other);
        Check(!poor.Changed && poor.Refusal.Length>0 && CampaignFingerprint.Of(c)==fp,"political shortage is an atomic refusal before creating a relation");
        n.PoliticalPoints=100;
        var r=CampaignDiplomacy.Get(c,n.Id,other.Id); OpinionSystem.Settle(c,r); var opinion=r.Opinion; var cash=n.Treasury;
        Check(Start(c,n,other).Changed && n.PoliticalPoints==92 && Math.Abs(n.Treasury-(cash-.1f))<.0001f,"dispatch pays the displayed up-front political and cash prices");
        Check(r.Opinion==opinion && !OpinionSystem.Has(r,"diplomatic_outreach"),"dispatch never adds an instant opinion boost");
        fp=CampaignFingerprint.Of(c); Check(!Start(c,n,other).Changed && CampaignFingerprint.Of(c)==fp,"repeated button press cannot stack or charge an existing mission");
        Days(c,1,6); Check(n.PoliticalPoints==92,"less than a week earns no shortcut and incurs no weekly bill");
        Days(c,7,14); var mission=DiplomaticOutreach.Of(r,n.Id);
        Check(mission.PaidWeeks==2 && n.PoliticalPoints==89 && r.Opinion>opinion && r.Opinion<opinion+3,"weekly payment buys gradual goodwill rather than +12 per click");
        fp=CampaignFingerprint.Of(c); OpinionSystem.Tick(c); Check(CampaignFingerprint.Of(c)==fp,"repeating a daily review cannot charge the week twice");
        var saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"missions"),"missions");
        Check(saved.Success,"missions survive the actual saved host snapshot: "+saved.Warning);
        saved.Campaign.AttachProvinceTopology(c.ProvinceMap.CloneForOverrides());
        if (CampaignFingerprint.Of(saved.Campaign)!=fp)
        {
            foreach(var original in c.Nations)
            foreach(var field in typeof(Nation).GetFields().Where(f=>f.FieldType.IsPrimitive || f.FieldType.IsEnum || f.FieldType==typeof(string)))
                if(!Equals(field.GetValue(original),field.GetValue(saved.Campaign.NationById(original.Id))))
                    Console.WriteLine("MISSION_DIFF "+original.Tag+"."+field.Name+" "+field.GetValue(original)+" -> "+field.GetValue(saved.Campaign.NationById(original.Id)));
            var rr=CampaignDiplomacy.Peek(saved.Campaign,n.Id,other.Id);
            Console.WriteLine("MISSION_DIFF relation "+JsonSerializer.Serialize(r,new JsonSerializerOptions{IncludeFields=true}));
            Console.WriteLine("MISSION_DIFF restored "+JsonSerializer.Serialize(rr,new JsonSerializerOptions{IncludeFields=true}));
        }
        Check(CampaignFingerprint.Of(saved.Campaign)==fp,"restored prices, progress and goodwill have the same fingerprint");
        var restored=CampaignDiplomacy.Peek(saved.Campaign,n.Id,other.Id); restored.MissionByA.WeeklyPoliticalCost+=1;
        Check(CampaignFingerprint.Of(saved.Campaign)!=fp,"changing a saved commitment is detected before the next payment");
        var recall=new ImproveRelationsCommand { IssuerId=n.Id,TargetId=other.Id,Active=false };
        Check(NetworkCommandCodec.TryEncode(recall,out var type,out var json) && NetworkCommandCodec.TryDecode(type,json,out var decoded) && decoded is ImproveRelationsCommand { Active:false },"recall uses the existing authoritative network command codec");
        recall.Apply(c); var left=n.PoliticalPoints; var weight=OpinionSystem.Weight(c,r.Reasons.Single(x=>x.Id=="diplomatic_outreach"));
        Days(c,15,40);
        Check(recall.Changed && DiplomaticOutreach.Of(r,n.Id) is null && n.PoliticalPoints==left,"recall stops the real weekly payments without refund exploits");
        Check(OpinionSystem.Weight(c,r.Reasons.Single(x=>x.Id=="diplomatic_outreach"))<weight,"earned goodwill fades after recall");
        Check(Start(c,n,other).Changed,"a later paid mission can resume the same diplomatic relationship");
        n.PoliticalPoints=0; Days(c,41,55);
        Check(DiplomaticOutreach.Of(r,n.Id) is null && n.PoliticalPoints==0,"insufficient weekly funds end the mission without overdrawing credit");
        n.PoliticalPoints=100; Days(c,56,70); Start(c,n,other);
        c.Wars.Add(new StrategicWar { Id=1,AttackerId=n.Id,DefenderId=other.Id }); Days(c,71,80);
        fp=CampaignFingerprint.Of(c);
        Check(DiplomaticOutreach.Of(r,n.Id) is null && !Start(c,n,other).Changed && CampaignFingerprint.Of(c)==fp,"war closes and blocks a goodwill mission atomically");
        var(c2,n2,o2)=World(); var relation=CampaignDiplomacy.Get(c2,n2.Id,o2.Id);
        if(relation.A==n2.Id) relation.EmbargoByA=true; else relation.EmbargoByB=true;
        Check(!Start(c2,n2,o2).Changed,"our own embargo cannot be bypassed with political credit");
        relation.EmbargoByA=relation.EmbargoByB=false;
        // Theirs no longer closes the road: a mission is how a government talks its way out of
        // a sanction (OpinionWitnesses, the player's complaint that nothing mended a relation).
        if(relation.A==o2.Id) relation.EmbargoByA=true; else relation.EmbargoByB=true;
        Check(DiplomaticOutreach.Refusal(c2,n2.Id,o2.Id).Length==0,"the recipient's embargo does not close the patient road back");
        relation.EmbargoByA=relation.EmbargoByB=false;
        Start(c2,n2,o2); Days(c2,1,175);
        var grown=OpinionSystem.Weight(c2,relation.Reasons.Single(x=>x.Id=="diplomatic_outreach"));
        Check(grown>10 && grown<=c2.Defines.DiplomaticMissionOpinionCap,"long engagement remains inside one bounded opinion modifier");
        Days(c2,176,190); Check(DiplomaticOutreach.Of(relation,n2.Id) is null,"mission automatically expires instead of becoming an invisible perpetual bill");
        var(c3,n3,o3)=World(); o3.Ideology="fascism";
        var friction=CampaignDiplomacy.Get(c3,n3.Id,o3.Id); friction.Tension=90; Start(c3,n3,o3); Days(c3,1,175);
        Check(OpinionSystem.Weight(c3,friction.Reasons.Single(x=>x.Id=="diplomatic_outreach"))<grown*.6f,"political distance and crisis tension materially slow the same funded mission");
        c3.Defines.DiplomaticMissionLimit=1;
        var fourth=c3.AddNation(new Nation { Tag="CCC",Population=1_000_000 });
        Check(!Start(c3,n3,fourth).Changed,"concurrent embassy workload is bounded even for a rich government");
        var legacy=JsonSerializer.Deserialize<DiplomaticRelation>("{\"A\":0,\"B\":1}",new JsonSerializerOptions { IncludeFields=true });
        Check(legacy.MissionByA is null && legacy.MissionByB is null,"old relations do not acquire an unauthorised paid commitment");
        foreach(var emptyIssuer in new[]{false,true})
        {
            var(emptyWorld,sponsor,recipient)=World(); Start(emptyWorld,sponsor,recipient);
            var balance=sponsor.PoliticalPoints; var cashLeft=sponsor.Treasury;
            (emptyIssuer?sponsor:recipient).Population=0;
            Days(emptyWorld,1,8);
            Check(DiplomaticOutreach.Of(CampaignDiplomacy.Peek(emptyWorld,sponsor.Id,recipient.Id),sponsor.Id) is null &&
                sponsor.PoliticalPoints==balance && sponsor.Treasury==cashLeft,
                "empty "+(emptyIssuer?"sponsor":"recipient")+" ends the mission before any payment even without a fallen marker");
        }
        var oldLanguage=Loc.Language; Loc.Language="russian";
        Check(DiplomaticOutreach.Description(c2,n2.Id,o2.Id).StartsWith("Миссия"),"prices and the no-instant-bonus explanation are localized"); Loc.Language=oldLanguage;
    }
}
