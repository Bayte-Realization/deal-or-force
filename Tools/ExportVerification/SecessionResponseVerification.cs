using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifySecessionResponse(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var parent = c.NationByTag("POL");
        c.ConfigureSinglePlayer(parent.Id); c.IsPaused = true; c.ObserverMode = false;
        c.Defines.ReintegrationRecoveryDays = 0; // Only this isolated probe repeats the same split four times.
        c.PendingEvents.Clear(); c.Wars.Clear();
        game.VerificationEnableParticipationOrders();
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            var home = c.StatesOf(parent).Where(s => s.MapIndex != parent.CapitalStateMapIndex && s.Population > 0)
                .OrderByDescending(s => s.Population).First();
            var report = SecessionEvents.Force(c, home, "polish");
            Check(report?.Seceded is not null, "a real Polish region breaks away in the running campaign");
            if (report?.Seceded is not { } rebel) return;
            var item = c.PendingEvents.Single(e => e.NationId == parent.Id);
            // A treaty from before this unilateral split may not turn the response into a dead button.
            CampaignDiplomacy.Get(c,parent.Id,rebel.Id).TruceUntil = c.ElapsedDays + 1825;
            game.Call("Refresh"); await Wait(.3);
            var fp = CampaignFingerprint.Of(c); var rng = c.Random.State;
            game.Call("Refresh"); await Wait(.15);
            Check(CampaignFingerprint.Of(c)==fp && c.Random.State==rng,
                "viewing the secession event changes no authoritative state or random stream");
            var buttons=Enumerable.Range(0,3).Select(i=>game.FindChild("EventChoice"+i,true,false) as Button).ToArray();
            Check(buttons.All(b=>b is { Disabled:false } && b.IsVisibleInTree()), "all three actual event choices are visible and enabled");
            Check(buttons[2]?.Text==Loc.Get("Send the army"), "the response is translated: "+language);
            foreach(var button in buttons)
                Check(button is not null && GetTree().Root.GetVisibleRect().Grow(1).Encloses(button.GetGlobalRect()),
                    "the response fits the viewport: "+language+" "+size.X);
            await Capture("secession-response-"+language+"-"+size.X);
            buttons[2]?.EmitSignal(BaseButton.SignalName.Pressed); await Wait(.15);
            var war=c.Wars.LastOrDefault(w=>!w.Ended && w.Opposes(parent.Id,rebel.Id));
            Check(war is { CivilConflict:true } && Reintegration.Reclaiming(c,war,parent.Id),
                "the actual event button starts a civil war to reintegrate the breakaway despite the old truce");
            Check(!c.PendingEvents.Contains(item) && c.IsPaused, "the response works while paused and consumes the event once");
            if(war is not null) PeaceNegotiation.Resolve(c,war,rebel.Id,PeaceTerms.Capitulate);
            Check(!c.HasOwnedLand(rebel), "the defeated breakaway can really be annexed whole");
            game.Call("Refresh"); await Wait(.1);
        }
    }
}
