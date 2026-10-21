using System;
using System.Linq;
using System.Reflection;
using System.Collections.Generic;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyInstitutions(DealOrForceGame game)
    {
        var w = game.VerificationWorld; var n = w.Player;
        n.PoliticalPoints = 1000;
        ElectoralPolitics.Tick(w);
        n.Elections.Control = 0; n.Elections.IncumbentTerms = 8; n.NextElectionDay = 0;
        foreach (var s in w.StatesOf(n)) { s.GovernmentApproval = .1f; s.PoliticalDivergence = .3f; }
        ElectoralPolitics.Tick(w);
        Check(n.Elections.TransferOnDay >= 0, "native elected government exposes a pending named successor");
        game.Call("OpenPoliticalAppointments");
        foreach (var (lang, width, height) in new[] { ("russian",1920,1080), ("russian",1280,720), ("english",1920,1080), ("english",1280,720) })
        {
            Loc.Language = lang; GetTree().Root.Size = new(width,height); game.Call("RefreshLocalisedText");
            game.Call("OpenPoliticalAppointments"); await Wait(.2);
            var fp = CampaignFingerprint.Of(w); game.Call("RefreshStateAffairs");
            Check(CampaignFingerprint.Of(w) == fp, "election UI reads without initialising or changing electoral state");
            var accept = game.FindChild("ElectionAcceptResult", true, false) as Button;
            var defy = game.FindChild("ElectionRefuseResult", true, false) as Button;
            Check(accept is { Disabled:false } && defy is { Disabled:false }, "native electoral panel offers both real handover choices");
            await Capture("election-" + lang + "-" + width);
        }
        var money = n.PoliticalPoints = 1000; var leader = Rulers.HeadOfGovernment(w,n).Name;
        (game.FindChild("ElectionRefuseResult",true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(n.Elections.TransferOnDay < 0 && n.PoliticalPoints == money - w.Defines.ElectionRefusePoliticalCost,
            "actual refuse-result button executes and pays the shared price");
        Check(Rulers.HeadOfGovernment(w,n).Name == leader, "actual refusal retains the incumbent");
        var fpSaved = CampaignFingerprint.Of(w); var loaded = SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(w,""),"");
        loaded.Campaign?.AttachProvinceTopology(w.ProvinceMap.CloneForOverrides());
        Check(loaded.Success && CampaignFingerprint.Of(loaded.Campaign) == fpSaved,
            "full network snapshot preserves electoral states and fingerprint");

        game.Call("CloseStateAffairs");
        var fra = w.NationByTag("FRA"); var bel = w.NationByTag("BEL");
        w.ConfigureSinglePlayer(fra.Id); fra.PoliticalPoints = 1000;
        SubjectSystem.Subjugate(w,fra,bel,"satellite",out _); bel.LastSubjectInteractionDay = -9999;
        var regions = CountryRelease.ReleasableStates(w,fra)
            .Where(s => TerritorialCoherence.TouchesOwnedGround(w,bel,s.MapIndex)).Take(2).Select(s=>s.MapIndex).ToArray();
        Check(regions.Length > 0, "native map supplies a connected administrative package");
        game.OpenSubjectsModal(bel); await Wait(.2);
        (game.FindChild("GrantRegionsTo" + bel.Id,true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.2);
        var selection = (HashSet<int>)typeof(DealOrForceGame).GetField("_releaseSelectedStates",BindingFlags.NonPublic|BindingFlags.Instance).GetValue(game);
        foreach (var id in selection.Except(regions).ToArray())
            (game.FindChild("ReleaseRegion" + id,true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        foreach (var id in regions.Where(id => !selection.Contains(id)))
            (game.FindChild("ReleaseRegion" + id,true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        foreach (var (lang,width,height) in new[] { ("russian",1920,1080), ("russian",1280,720), ("english",1920,1080), ("english",1280,720) })
        {
            Loc.Language=lang; GetTree().Root.Size=new(width,height); game.Call("RefreshLocalisedText");
            game.Call("RefreshReleaseCountryModal"); await Wait(.2);
            var button=game.FindChild("ConfirmSubjectLandGrant",true,false) as Button;
            Check(button is { Disabled:false }, "subject land grant uses a real enabled confirmation button");
            Check(selection.SetEquals(regions), "region multi-selection survives language and layout refresh");
            var before=CampaignFingerprint.Of(w); game.Call("RefreshReleaseCountryModal");
            Check(before==CampaignFingerprint.Of(w), "subject package preview is read-only");
            await Capture("subject-grant-" + lang + "-" + width);
        }
        var points=fra.PoliticalPoints;
        (game.FindChild("ConfirmSubjectLandGrant",true,false) as Button)?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(regions.All(id => w.States.Find(s=>s.MapIndex==id)?.NationMapIndex==bel.MapIndex) &&
            fra.PoliticalPoints==points-w.Defines.SubjectLandGrantPoliticalCostPerRegion*regions.Length,
            "actual confirmation enlarges the existing subject and pays exactly once");
        fpSaved=CampaignFingerprint.Of(w); loaded=SaveSystem.ImportNetworkSnapshot(SaveSystem.ExportNetworkSnapshot(w,""),"");
        loaded.Campaign?.AttachProvinceTopology(w.ProvinceMap.CloneForOverrides());
        Check(loaded.Success && CampaignFingerprint.Of(loaded.Campaign)==fpSaved,
            "post-transfer ownership, accounts and fingerprint survive full snapshot");
    }
}
