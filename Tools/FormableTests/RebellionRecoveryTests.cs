using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;

static class RebellionRecoveryTests
{
    static void Check(bool ok, string note)
    { if (!ok) throw new Exception("revolt recovery: " + note); Console.WriteLine("PASS revolt recovery: " + note); }

    static Campaign Fixture()
    {
        var c = new Campaign(20261025) { ScenarioId = "revolt_recovery_fixture" };
        c.AddNation(new Nation { Tag = "RRP", Name = "Parent", MapIndex = 501, Population = 3_000_000,
            Gdp = 300, Treasury = 50, Stability = .3f, Government = "military_junta", Ideology = "nationalism" });
        c.AddNation(new Nation { Tag = "RRE", Name = "Breakaway", MapIndex = 502, Treasury = 50,
            SecededFromId = 0, Movement = new MovementState { Id = "", Exile = true, Landless = true,
                Cadres = 100000, Legitimacy = 1, HeadquartersStateMapIndex = 3004 } });
        c.AddNation(new Nation { Tag = "RRN", Name = "Neighbour", MapIndex = 503, Population = 1_000_000, Gdp = 100, Treasury = 20 });
        c.ConfigureSinglePlayer(0);
        for (var i = 0; i < 4; i++)
        {
            var region = new ScenarioState { Id = "recovery_" + i, Name = "Recovery " + i, MapIndex = 3001+i,
                NationMapIndex = i < 3 ? 501 : 503, NationId = i < 3 ? "RRP" : "RRN", Population = 1_000_000,
                Stability = .05f, Radicalism = .9f, StandardOfLiving = .3f, Literacy = .6f };
            region.PopulationGroups.Add(new PopulationGroup { Culture = "english", Size = region.Population });
            c.States.Add(region);
        }
        c.Nations[0].CapitalStateMapIndex = 3001;
        c.Nations[1].Movement.Peoples.Add("english");
        c.Nations[1].Movement.Homeland.AddRange(new[] { "recovery_1", "recovery_2" });
        c.Nations[1].Movement.Support[3002] = 1; c.Nations[1].Movement.Support[3003] = 1;
        c.Homelands[1] = new() { 3002, 3003 };
        const int w = 128, h = 16;
        var states = new ushort[w*h]; var owners = new ushort[states.Length]; var rgb = new byte[states.Length*3];
        for (var y=0; y<h; y++) for(var x=0; x<w; x++)
        {
            var p=y*w+x; states[p]=(ushort)(3001+x/32); owners[p]=(ushort)(x<96 ? 501 : 503);
            rgb[p*3+2]=(byte)(x/16+1);
        }
        c.AttachProvinceTopology(ProvinceTopology.FromColorRaster(states,owners,w,h,
            new RgbDataMap(w,h,rgb),Enumerable.Range(1,8).ToDictionary(p=>p,p=>p)));
        return c;
    }

    static void Day(Campaign c, int day) => c.RestoreRuntimeState(c.Date, day, true, c.Random.State,
        c.Player.Id,c.HumanSeats.ToArray(),0,c.NextBattleId,c.MapRevision,true,null,null,null,null);

    public static void Run()
    {
        Loc.ResetCatalog(); MovementCatalog.ResetCatalog(); DemographyCatalog.ResetCatalog();
        var c=Fixture(); var parent=c.Nations[0]; var rebel=c.Nations[1]; var home=c.States[1];
        Check(MovementRules.UprisingChance(c,rebel,home)>0,"a genuine unprotected homeland can rise");
        RevoltRecovery.Protect(c,parent,home,0);
        var fp=CampaignFingerprint.Of(c); var rng=c.Random.State;
        Check(RevoltRecovery.DaysLeft(c,parent,home)==730 && MovementRules.UprisingChance(c,rebel,home)==0,
            "reconquest protection stops even a popular armed exile");
        Check(!MovementRules.TakeRegion(c,rebel,home,parent) && !MovementRules.Rise(c,rebel,home),
            "direct transfer and uprising execution cannot bypass protection");
        Check(SecessionEvents.Force(c,home,"english") is null && CampaignFingerprint.Of(c)==fp && c.Random.State==rng,
            "a forced separatist split also refuses without spending state or randomness");
        Check(MovementActions.Refusal(c,rebel,"rise",home.MapIndex,-1).Length>0,"the action explains the recovery refusal");
        var split=new ScenarioState { MapIndex=4010, ParentMapIndex=home.MapIndex, NationMapIndex=parent.MapIndex };
        Check(RevoltRecovery.DaysLeft(c,parent,split)==730,"a partitioned piece inherits its original region's protection");
        split.NationMapIndex=503;
        Check(RevoltRecovery.DaysLeft(c,c.Nations[2],split)==0,"protection does not follow a region to an unrelated holder");
        c.Defines.ReintegrationRecoveryDays=0;
        Check(MovementRules.UprisingChance(c,rebel,home)>0,"mods can disable recovery with the shared define");
        c.Defines.ReintegrationRecoveryDays=730; Day(c,730);
        Check(RevoltRecovery.DaysLeft(c,parent,home)==0 && MovementRules.UprisingChance(c,rebel,home)>0,
            "the protection expires, rather than making the region permanently loyal");

        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        StatePartition.Annex(c,new[]{3,4},rebel,asConquest:false);
        StatePartition.Annex(c,new[]{3,4},parent,asConquest:false);
        Check(RevoltRecovery.DaysLeft(c,parent,home)==730 && home.SecessionPressure<0,
            "the ordinary annexation path protects an actual reintegration");
        var firstFp=CampaignFingerprint.Of(c);
        parent.DecisionAvailableDay[RevoltRecovery.Prefix+home.MapIndex]++;
        Check(CampaignFingerprint.Of(c)!=firstFp,"the authoritative recovery clock participates in fingerprints");

        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        var relation=CampaignDiplomacy.Get(c,parent.Id,rebel.Id);
        relation.Stance=DiplomaticStance.Truce; relation.TruceUntil=2000; relation.NonAggressionPact=true;
        Check(MovementRules.TakeZone(c,rebel,home,parent)==2,"an eligible uprising takes its coherent homeland");
        var item=c.PendingEvents.Single(e=>e.NationId==parent.Id);
        Check(item.Options.Length==3 && item.Options.All(o=>o.SecessionTargetId==rebel.Id) &&
            !CampaignDiplomacy.AtWar(c,parent.Id,rebel.Id),"the affected player always gets three real responses before war");
        Check(CampaignDiplomacy.CanDeclareWar(c,parent.Id,rebel.Id,out _),"a new armed break is not shielded by the earlier peace or pact");
        fp=CampaignFingerprint.Of(c);
        var snapshot=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(c,"recovery"),"recovery").Campaign;
        Check(snapshot is not null && snapshot.PendingEvents.Single().Options[2].SecessionTargetId==rebel.Id &&
            snapshot.PendingEvents.Single().Options[2].SecessionChoice=="fight" &&
            snapshot.Player.DecisionAvailableDay[SecessionResponse.Prefix+rebel.Id]==60,
            "the question, target, choice and response deadline survive a network save");
        var changed=item.Options[2];
        item.Options[2]=new CampaignEventOption{SecessionTargetId=rebel.Id,SecessionChoice="dispute"};
        Check(CampaignFingerprint.Of(c)!=fp,"the pending choice itself participates in fingerprints"); item.Options[2]=changed;
        new ResolveCampaignEventCommand{IssuerId=c.Nations[2].Id,EventId=item.Id,OptionIndex=2}.Apply(c);
        Check(c.PendingEvents.Contains(item) && !CampaignDiplomacy.AtWar(c,parent.Id,rebel.Id),"another seat cannot answer the secession");
        new ResolveCampaignEventCommand{IssuerId=parent.Id,EventId=item.Id,OptionIndex=2}.Apply(c);
        var war=c.Wars.Single(w=>!w.Ended && w.Opposes(parent.Id,rebel.Id));
        Check(war.CivilConflict && Reintegration.Reclaiming(c,war,parent.Id) && relation.TruceUntil==0 && !relation.NonAggressionPact,
            "send the army starts an annexable civil reintegration war and clears only the broken bilateral treaty");
        Check(!c.PendingEvents.Contains(item),"a successful response consumes the card once");
        PeaceNegotiation.Resolve(c,war,rebel.Id,PeaceTerms.Capitulate);
        Check(!c.HasOwnedLand(rebel) && RevoltRecovery.DaysLeft(c,parent,home)>0,
            "defeating the breakaway really returns it and renews recovery");
        Check(!SecessionResponse.CanContest(c,parent,rebel),"a completed response grants no reusable truce bypass");

        // Old saves have no new clock; a dated victory and negative crushed pressure are the evidence.
        parent.DecisionAvailableDay.Remove(RevoltRecovery.Prefix+home.MapIndex);
        RevoltRecovery.RestoreLegacy(c);
        Check(RevoltRecovery.DaysLeft(c,parent,home)==730,"legacy reconquest is recovered from the dated peace and crushed homeland");
        fp=CampaignFingerprint.Of(c); RevoltRecovery.RestoreLegacy(c);
        Check(CampaignFingerprint.Of(c)==fp,"legacy repair is idempotent and does not extend the deadline on every load");

        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        MovementRules.TakeZone(c,rebel,home,parent); item=c.PendingEvents.Single();
        fp=CampaignFingerprint.Of(c);
        StatePartition.Annex(c,Reintegration.Everything(c,rebel),parent);
        var previousSupport=parent.WarSupport;
        new ResolveCampaignEventCommand{IssuerId=parent.Id,EventId=item.Id,OptionIndex=2}.Apply(c);
        Check(c.PendingEvents.Count==0 && !CampaignDiplomacy.AtWar(c,parent.Id,rebel.Id) && parent.WarSupport==previousSupport,
            "a stale response to an already reintegrated state cannot start a phantom war or apply rewards");

        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        MovementRules.TakeZone(c,rebel,home,parent); item=c.PendingEvents.Single();
        relation=CampaignDiplomacy.Get(c,parent.Id,rebel.Id); relation.TruceUntil=2000;
        new ResolveCampaignEventCommand{IssuerId=parent.Id,EventId=item.Id,OptionIndex=0}.Apply(c);
        // Recognition decides whether the claim is still pressed in the cabinet; it does not
        // decide whether the state may ever stand on its own ground again. The treaty binds
        // the side that broke away, not the side that was broken.
        Check(!SecessionResponse.CanContest(c,parent,rebel) && SecessionResponse.Reclaiming(parent,rebel) &&
            CampaignDiplomacy.CanDeclareWar(c,parent.Id,rebel.Id,out _) &&
            !CampaignDiplomacy.CanDeclareWar(c,rebel.Id,parent.Id,out _),
            "recognition closes the question, not the claim: the state may take its ground back, the breakaway may not attack over the same peace");

        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        MovementRules.TakeZone(c,rebel,home,parent); item=c.PendingEvents.Single();
        relation=CampaignDiplomacy.Get(c,parent.Id,rebel.Id); relation.TruceUntil=2000;
        Day(c,61);
        Check(!SecessionResponse.CanContest(c,parent,rebel) && CampaignDiplomacy.CanDeclareWar(c,parent.Id,rebel.Id,out _),
            "the deadline ends the question, not the right: the breakaway is still reachable after it");
        new ResolveCampaignEventCommand{IssuerId=parent.Id,EventId=item.Id,OptionIndex=2}.Apply(c);
        Check(CampaignDiplomacy.AtWar(c,parent.Id,rebel.Id) && relation.TruceUntil==0 && !c.PendingEvents.Contains(item),
            "a late send-the-army answer still fights, and voids the treaty that was shielding the breakaway");

        // A card that cannot be honoured is still never charged - but the reason has to be a
        // real block, not the standing right, so this state loses its own ground first.
        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        MovementRules.TakeZone(c,rebel,home,parent); item=c.PendingEvents.Single();
        relation=CampaignDiplomacy.Get(c,parent.Id,rebel.Id); relation.TruceUntil=2000;
        StatePartition.Annex(c,Reintegration.Everything(c,parent),c.Nations[2]);
        var support=parent.WarSupport;
        Day(c,61);
        new ResolveCampaignEventCommand{IssuerId=parent.Id,EventId=item.Id,OptionIndex=2}.Apply(c);
        Check(c.PendingEvents.Contains(item) && !CampaignDiplomacy.AtWar(c,parent.Id,rebel.Id) && parent.WarSupport==support,
            "a blocked send-army choice is not silently consumed or charged");
        EventSystem.Tick(c);
        Check(c.PendingEvents.Count==0,"an unanswered expired event defaults to nonrecognition, not forced war");

        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1];
        parent.IsHuman=false; c.ObserverMode=true;
        relation=CampaignDiplomacy.Get(c,parent.Id,rebel.Id); relation.TruceUntil=2000;
        MovementRules.TakeZone(c,rebel,c.States[1],parent);
        Check(CampaignDiplomacy.AtWar(c,parent.Id,rebel.Id) && c.PendingEvents.Count==0,
            "the AI contests an armed rising through the same fresh-response permission");

        // The half of the clock a rising cannot check: an exile handed back its homeland by a
        // holder that never really agreed. AutosaveBelarusRebel came back this way - three
        // regions in 1996, no war, no event, and a five-year peace that then forbade an answer.
        c=Fixture(); parent=c.Nations[0]; rebel=c.Nations[1]; home=c.States[1];
        RevoltRecovery.Protect(c,parent,home,0);
        rebel.Movement.ReadyOnDay[Restoration.ConsentKey(parent.Id)]=9999;
        Check(Restoration.Refusal(c,rebel,home).Contains("too recently"),
            "an exile cannot be handed back ground that was retaken last week, consent or no consent");
        Day(c,731);
        Check(Restoration.CanRestore(c,rebel,home),
            "the same return is permitted once the ground has stopped being a battlefield");

        c=Fixture(); parent=c.Nations[0]; var neighbour=c.Nations[2];
        relation=CampaignDiplomacy.Get(c,parent.Id,neighbour.Id); relation.TruceUntil=2000;
        Check(!CampaignDiplomacy.CanDeclareWar(c,parent.Id,neighbour.Id,out var reason) && reason.Contains("Truce"),
            "the breakaway exception never bypasses an unrelated country's truce");
    }

    public static void InspectSave(string[] args)
    {
        var at = Array.IndexOf(args, "--revolt-save");
        var path = Path.GetFullPath(args[at + 1]);
        var days = at + 2 < args.Length ? int.Parse(args[at + 2]) : 30;
        _ = Campaign.CreateBootstrap(947986505);
        var loaded = SaveSystem.Load(Path.GetDirectoryName(path), Path.GetFileNameWithoutExtension(path), "");
        var c = loaded.Campaign ?? throw new Exception(loaded.Warning);
        if (!MapRasterCatalog.TryBuildProvinceTopology(WorldMapSource.States, WorldMapSource.Control,
            WorldMapSource.Width, WorldMapSource.Height, out var topology, out var why)) throw new Exception(why);
        c.AttachProvinceTopology(topology);
        c.IsPaused = false;
        var r = c.NationByTag("RUS"); var b = c.NationByTag("BLR");
        var loadDay = c.ElapsedDays;
        var printed = new HashSet<string>();
        void Report(string label)
        {
            var relation = CampaignDiplomacy.Peek(c, r.Id, b.Id);
            Console.WriteLine($"REVOLT_SAVE {label} day={c.ElapsedDays} BLR owned={Reintegration.Everything(c,b).Count} " +
                $"regions={c.StatesOf(b).Length} pop={b.Population} movement={b.Movement?.Id} exile={b.Movement?.Exile} " +
                $"fallen={c.Fallen.Contains(b.Id)} truce={relation?.TruceUntil} war={CampaignDiplomacy.AtWar(c,r.Id,b.Id)} " +
                $"canDeclare={CampaignDiplomacy.CanDeclareWar(c,r.Id,b.Id,out var refusal)} refusal={refusal}");
            foreach(var item in c.PendingEvents.Where(e => e.NationId == r.Id))
                Console.WriteLine($"  EVENT {item.Title} {string.Join(" | ",item.Options.Select(o=>o.Title))}");
            // Everything that happened to Belarus since the save was loaded, once - a window of
            // one day hides the day the ground actually moved.
            foreach(var entry in c.Chronicle.Entries.Where(e=>e.Day>=loadDay && (e.ActorId==b.Id || e.TargetId==b.Id)))
                if (printed.Add($"{entry.Day}|{entry.Kind}|{entry.ActorId}|{entry.TargetId}|{entry.Place}"))
                    Console.WriteLine($"  FACT {entry.Day} {entry.Kind} {entry.ActorId}->{entry.TargetId} {entry.Place}");
            if (b.Movement is { } m)
                Console.WriteLine($"  MOVEMENT ready={string.Join(",", m.ReadyOnDay.Select(p => p.Key + "=" + p.Value))} " +
                    $"support59={m.Support.GetValueOrDefault(59)} exile={m.Exile} landless={m.Landless}");
            if (b.Movement is { Exile: true })
                Console.WriteLine($"  RESTORE_BLR consent={b.Movement.ReadyOnDay.GetValueOrDefault(Restoration.ConsentKey(r.Id))} " +
                    $"refusal={Restoration.Refusal(c, b, c.States.FirstOrDefault(s => s.MapIndex == 1500))}");
            if (label != "loaded") return;
            foreach (var pair in r.DecisionAvailableDay)
                Console.WriteLine($"  RUS_DECISION {pair.Key}={pair.Value}");
        }
        Report("loaded");
        for(var d=0; d<days; d++)
        {
            c.TickDay(); Report("tick");
            if(c.SimulationFault.Length>0) throw new Exception(c.SimulationFault);
        }
    }
}
