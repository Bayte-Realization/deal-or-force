using System;
using System.Linq;
using System.Reflection;
using DealOrForce.Simulation;

static class CampaignAlertTests
{
    private static void Check(bool ok, string label)
    { if (!ok) throw new Exception("campaign alerts: " + label); Console.WriteLine("PASS campaign alerts: " + label); }
    public static void Run()
    {
        var c = Campaign.CreateBootstrap(20261020); var n = c.NationByTag("POL");
        c.ConfigureSinglePlayer(n.Id); c.PendingEvents.Clear(); c.Wars.Clear(); c.Battles.Clear();
        n.Cabinet.Clear(); n.CurrentResearch = "";
        var items = new[]
        {
            new SituationItem("a", SituationPriority.Critical, SituationArea.Economy, "a", "x", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Finance, "x"),
            new SituationItem("b", SituationPriority.Warning, SituationArea.Economy, "b", "x", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Finance, "x"),
            new SituationItem("c", SituationPriority.Opportunity, SituationArea.Economy, "c", "x", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Finance, "x"),
            new SituationItem("d", SituationPriority.Awaiting, SituationArea.Foreign, "d", "x", Array.Empty<string>(), Array.Empty<string>(), Array.Empty<SituationFact>(), SituationRoute.Inbox, "x", DeadlineDay: 20)
        };
        var fp = CampaignFingerprint.Of(c); var random = c.Random.State;
        var read = CampaignAlerts.Read(c,n,items);
        Check(read.Single(a=>a.Id=="a").Group==CampaignAlertGroup.Must, "critical fact belongs to must-do");
        Check(read.Single(a=>a.Id=="b").Group==CampaignAlertGroup.Should, "warning belongs to should-do");
        Check(read.Single(a=>a.Id=="c").Group==CampaignAlertGroup.Could, "opportunity is not mandatory");
        Check(read.Single(a=>a.Id=="d").Group==CampaignAlertGroup.Must, "pending expiring answer is not an optional opportunity");
        Check(read.Select(a=>a.Id).Distinct().Count()==read.Count && read.Count<=64, "bounded stable unique identities");
        Check(CampaignFingerprint.Of(c)==fp && c.Random.State==random, "reading never changes campaign or random stream");
        Check(CampaignAlerts.Read(c,n,items,true).Count==0, "observer is not told to run a country");
        c.Defines.CampaignAlertsEnabled=false;
        Check(CampaignAlerts.Read(c,n,items).Count==0, "moddable master switch"); c.Defines.CampaignAlertsEnabled=true;
        c.PendingEvents.Add(new PendingCampaignEvent { Id=901,NationId=n.Id,Title="Local event",ExpiresDay=30 });
        c.PendingEvents.Add(new PendingCampaignEvent { Id=902,NationId=c.NationByTag("FRA").Id,Title="Foreign event",ExpiresDay=30 });
        read=CampaignAlerts.Read(c,n,items);
        Check(read.Any(a=>a.Id=="event:901") && read.All(a=>a.Id!="event:902"), "no foreign event spam");
        var memory=new CampaignAlertSnoozes(); var warning=read.Single(a=>a.Id=="b");
        memory.Hide(warning,10,7);
        Check(memory.Hidden(warning with { Title="Перевод",Summary="Changed number" },11), "translation and changed values do not defeat snooze");
        Check(!memory.Hidden(warning with { Group=CampaignAlertGroup.Must },11), "escalation immediately restores hidden warning");
        Check(!memory.Hidden(warning,17), "temporary hide expires in campaign days");
        memory.Hide(read.Single(a=>a.Id=="a"),10,30);
        Check(!memory.Hidden(read.Single(a=>a.Id=="a"),11), "urgent obligation returns tomorrow");
        memory.Reconcile(Array.Empty<CampaignAlert>(),10);
        Check(memory.Count==0, "resolved issues leave no stale hidden IDs");
        ElectoralPolitics.Tick(c); n.Elections.TransferOnDay=20; n.Elections.Winner="Named winner";
        Check(CampaignAlerts.Read(c,n,items).Single(a=>a.Id=="election_handover").Route==CampaignAlertRoute.Elections, "electoral deadline has its own destination");
        Check(fp!=CampaignFingerprint.Of(c), "fixture mutations really affect fingerprint");
    }
}

static class StaffPortfolioTests
{
    private static void Check(bool ok,string label)
    { if (!ok) throw new Exception("staff: " + label); Console.WriteLine("PASS staff: " + label); }
    private static void Day(Campaign c,int day) => typeof(Campaign).GetProperty(nameof(Campaign.ElapsedHours))!
        .GetSetMethod(true)!.Invoke(c,new object[]{(long)day*24});
    public static void Run()
    {
        var c=Campaign.CreateBootstrap(20261020); var n=c.NationByTag("POL"); c.ConfigureSinglePlayer(n.Id);
        n.Delegated.Clear();
        Check(Delegation.Holds(n,Delegation.Research), "old-save research remains automatic by default");
        Delegation.Set(n,Delegation.Budget,true);
        Check(Delegation.Holds(n,Delegation.Construction), "old delegated treasury retains construction");
        Delegation.Set(n,Delegation.Construction,false);
        Check(Delegation.Holds(n,Delegation.Budget) && !Delegation.Holds(n,Delegation.Construction), "building can be taken back without taking the budget");
        Delegation.Set(n,Delegation.Budget,false); Delegation.Set(n,Delegation.Construction,true);
        Check(Delegation.Holds(n,Delegation.Construction) && !Delegation.Holds(n,Delegation.Budget), "works ministry can run without delegated taxes");
        c.Construction.Clear(); n.Treasury=10000; n.DebtArrears=n.PaymentArrears=0;
        var tax=n.TaxRate; var cash=n.Treasury;
        Delegation.Tick(c,n);
        Day(c,1); ConstructionSystem.Tick(c);
        Check(c.Construction.Any(p=>p.NationId==n.Id) && n.Treasury<cash, "delegated construction starts and pays for a real project");
        Check(n.TaxRate==tax, "works ministry does not set taxes");
        var projects=c.Construction.Count; Delegation.Set(n,Delegation.Construction,false); Delegation.Tick(c,n);
        Check(c.Construction.Count==projects, "revoking construction preserves existing projects");
        n.CurrentResearch=""; Delegation.Set(n,Delegation.Research,false); Day(c,7); ResearchSystem.Tick(c);
        Check(n.CurrentResearch.Length==0, "manual research does not silently choose a project");
        Delegation.Set(n,Delegation.Research,true); Day(c,14); ResearchSystem.Tick(c);
        Check(n.CurrentResearch.Length>0 && n.ResearchProgress.Count>0, "delegated research chooses and advances a real technology");
        var research=n.CurrentResearch; var progress=n.ResearchProgress[research];
        Delegation.Set(n,Delegation.Research,false); Day(c,21); ResearchSystem.Tick(c);
        Check(n.CurrentResearch==research && n.ResearchProgress[research]>progress, "revoking selection does not halt current research");
        n.Cabinet.Clear(); n.LastCabinetChangeDay=-9999; Day(c,47);
        Rulers.Tick(c); Check(n.Cabinet.Count==0, "manual personnel office does not appoint ministers");
        Delegation.Set(n,Delegation.Personnel,true); cash=n.Treasury; var points=n.PoliticalPoints=1000;
        Rulers.Tick(c);
        Check(n.Cabinet.Count==1 && n.Treasury<cash, "delegated personnel appoints a named paid minister through normal command");
        Delegation.Set(n,Delegation.Research,false);
        var state=c.StatesOf(n)[0];
        c.AttachProvinceTopology(ProvinceTopology.Build(new ushort[]{(ushort)state.MapIndex,(ushort)state.MapIndex},
            new ushort[]{(ushort)n.MapIndex,(ushort)n.MapIndex},2,1,_=>false));
        var fp=CampaignFingerprint.Of(c); var saved=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,""),"");
        saved.Campaign?.AttachProvinceTopology(c.ProvinceMap?.CloneForOverrides());
        Check(saved.Success && CampaignFingerprint.Of(saved.Campaign)==fp, "delegation markers survive complete network snapshot and fingerprint");
        Check(!Delegation.Holds(saved.Campaign.Player,Delegation.Research) && Delegation.Holds(saved.Campaign.Player,Delegation.Personnel), "restored portfolios reflect actual saved settings");
        Delegation.Set(n,Delegation.Personnel,false);
        Check(CampaignFingerprint.Of(c)!=fp, "staff authority is included in the network fingerprint");
        var before=n.Delegated.Count; Delegation.Set(n,"not-a-portfolio",true);
        Check(n.Delegated.Count==before, "unknown portfolio rejected");
    }
}
