using System;
using System.Linq;
using DealOrForce.Simulation;

static class DismantlementTests
{
    static void Check(bool ok, string label) { if (!ok) throw new Exception(label); Console.WriteLine("PASS release: " + label); }
    static (Campaign c, Nation a, Nation b) Setup()
    {
        var c = new Campaign(811) { ScenarioId = "release_fixture" };
        var a = new Nation { Id = 0, Tag = "RLA", Name = "Parent", MapIndex = 501, Population = 3000000,
            Treasury = 90, Gdp = 120, ManpowerReserve = 300000, PublicDebt = 60, DebtArrears = 6, PaymentArrears = 3,
            DebtInitialized = true, DebtCondition = DebtCondition.Default, DebtMissedWeeks = 8 };
        var b = new Nation { Id = 1, Tag = "RLB", Name = "Partner", MapIndex = 502, Population = 1000000, Treasury = 90, Gdp = 40 };
        c.Nations.Add(a); c.Nations.Add(b);
        var cells = new ushort[80]; var owners = new ushort[80];
        for (var i = 0; i < 4; i++)
        {
            var s = new ScenarioState { MapIndex = 3001+i, NationMapIndex = i < 3 ? 501 : 502,
                Name = "Settlement " + i, Id = "release_"+i, Population = 1000000 };
            s.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = 1000000 });
            s.CoreTags.Add(i < 3 ? a.Tag : b.Tag); c.States.Add(s);
        }
        for (var y = 0; y < 4; y++) for (var x = 0; x < 20; x++)
        { cells[y*20+x] = (ushort)(3001+x/5); owners[y*20+x] = (ushort)(x<15 ? 501 : 502); }
        c.AttachProvinceTopology(ProvinceTopology.Build(cells,owners,20,4,_ => false)); c.ConfigureSinglePlayer(a.Id);
        return (c,a,b);
    }
    public static void Run()
    {
        var (c,a,b) = Setup(); var ids = new[] {3002};
        var before = CampaignFingerprint.Of(c);
        Check(Dismantlement.Refusal(c,a,ids) == "" && Dismantlement.Culture(c,ids) == "english" && before == CampaignFingerprint.Of(c), "preview is read-only and identity follows real inhabitants");
        Check(Dismantlement.Refusal(c,a,new[] {3001,3002,3003}).Length>0, "a settlement cannot erase its parent country");
        Check(Dismantlement.Refusal(c,a,new[] {3001,3003}).Length>0, "disconnected regions cannot be bundled into one invented country");
        Check(Dismantlement.Refusal(c,a,new[] {3002,3002}).Length>0, "duplicate regions are refused");
        c.SetFeature("secession",false); before = CampaignFingerprint.Of(c);
        Check(Dismantlement.Refusal(c,a,ids).Length>0 && before==CampaignFingerprint.Of(c), "mod switch blocks a release without mutation"); c.SetFeature("secession",true);
        var born = Dismantlement.Apply(c,a,ids,true);
        Check(born is not null && born.Id != a.Id && born.Tag != a.Tag && c.HasOwnedLand(born) && !CampaignDiplomacy.AtWar(c,a.Id,born.Id), "a real independent country is established without a fabricated rebellion or war");
        Check(Math.Abs(a.Treasury+born.Treasury-90)<.001 && Math.Abs(born.Treasury-30)<.001 && Math.Abs(a.PublicDebt+born.PublicDebt-60)<.001 && Math.Abs(born.PublicDebt-20)<.001,
            "cash and public debt are divided, not duplicated");
        Check(Math.Abs(a.DebtArrears+born.DebtArrears-6)<.001 && Math.Abs(a.PaymentArrears+born.PaymentArrears-3)<.001 && born.DebtCondition==DebtCondition.Default && born.DebtMissedWeeks==8,
            "independence does not erase default or arrears");
        Check(a.Population+born.Population==3000000 && c.StatesOf(born).Sum(s=>s.PopulationGroups.Sum(g=>g.Size))==born.Population && Math.Abs(a.Gdp+born.Gdp-120)<.001 && Math.Abs(a.ManpowerReserve+born.ManpowerReserve-300000)<1,
            "population, groups, economic output and reserves remain conserved");
        Check(TerritorialClaims.TitleOf(c,a,c.StatesOf(born).First())==TerritorialTitle.None && CampaignDiplomacy.Peek(c,a.Id,born.Id).TruceUntil>c.ElapsedDays,
            "voluntary release explicitly renounces homeland rights and protects the new country with a truce");
        var saved = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"released"),"released");
        Check(saved.Success && saved.Campaign.NationByTag(born.Tag)?.PublicDebt==born.PublicDebt && saved.Campaign.StatesOf(saved.Campaign.NationByTag(born.Tag)).Any(), "new identity, territory and debt survive the real save codec");

        (c,a,b)=Setup();
        var deal = new Ultimatum.Deal(new Ultimatum.Side { Regions=ids, ReleaseRegions=true }, new Ultimatum.Side());
        var assessment = Ultimatum.AssessDeal(c,a,b,deal);
        Check(assessment.Acceptable && Math.Abs(assessment.FromTreasuryAfter-60)<.001 && assessment.ToTreasuryAfter==90, "deal preview distinguishes independence from acquiring territory and includes the new treasury share");
        var command = new ProposeDealCommand { IssuerId=a.Id,ToId=b.Id,FromRegions=ids,FromReleaseRegions=true };
        ICampaignCommand decoded = null;
        Check(NetworkCommandCodec.TryEncode(command,out var type,out var json) && NetworkCommandCodec.TryDecode(type,json,out decoded) && ((ProposeDealCommand)decoded).FromReleaseRegions,
            "independence intent survives command replay/network encoding");
        decoded.Apply(c);
        Check(c.Nations.Count==3 && a.Treasury==assessment.FromTreasuryAfter && b.Treasury==90 && c.States.Find(s=>s.MapIndex==3002).NationMapIndex!=b.MapIndex,
            "a signed release establishes a third party rather than annexing to the deal partner");
        before=CampaignFingerprint.Of(c); decoded.Apply(c);
        Check(before==CampaignFingerprint.Of(c), "a repeated package cannot release the same region or charge it twice");

        (c,a,b)=Setup();
        var forced = new PeaceDemand {WarId=1,ProposerId=b.Id,ReleaseRegions=true};
        foreach(var p in PeaceConference.ProvincesOfState(c.ProvinceMap,3002)) {forced.Provinces.Add(p);c.SetProvinceController(p,(ushort)b.MapIndex);}
        var war = new StrategicWar {Id=1,AttackerId=b.Id,DefenderId=a.Id,WarScore=100,DefenderWarSupport=0,StartedDay=-600}; c.Wars.Add(war);
        Check(PeaceConference.BlockReason(c,war,forced)=="" && PeaceConference.Round(c,war,forced).ReleaseRegions, "whole-region independence is priced against actual war score and retained in rounding");
        Check(PeaceConference.Resolve(c,war,forced) && war.Ended && war.AnnexedProvinces==0 && c.Nations.Count==3, "written peace can compel independence without crediting annexed territory");
        born=c.Nations.Last();
        Check(TerritorialClaims.TitleOf(c,a,c.StatesOf(born).First())==TerritorialTitle.Homeland && Math.Abs(c.Nations.Sum(n=>n.Treasury)-180)<.001, "compelled independence keeps homeland claims and conserves cash");

        (c,a,b)=Setup();
        a.IsHuman=true; b.IsHuman=false;
        war=new StrategicWar { Id=2,AttackerId=b.Id,DefenderId=a.Id,WarScore=100,DefenderWarSupport=0,StartedDay=-600 };c.Wars.Add(war);
        forced=new PeaceDemand { WarId=2,ProposerId=b.Id,ReleaseRegions=true };
        foreach(var p in PeaceConference.ProvincesOfState(c.ProvinceMap,3002)) forced.Provinces.Add(p);
        var peaceCommand=WrittenPeaceCommand.From(forced); ICampaignCommand peaceCopy=null;
        Check(NetworkCommandCodec.TryEncode(peaceCommand,out type,out json) && NetworkCommandCodec.TryDecode(type,json,out peaceCopy) && ((WrittenPeaceCommand)peaceCopy).ReleaseRegions,
            "written independence settlement survives network command encoding");
        peaceCopy.Apply(c);
        Check(!war.Ended && war.PendingOffer is {ReleaseRegions:true} && c.Nations.Count==2, "a human recipient receives an offer rather than having land released without consent");
        Check(PeaceOffers.Answer(c,war,a.Id,true) && war.Ended && c.Nations.Count==3, "accepting the real pending offer applies its independence condition");
    }
}
