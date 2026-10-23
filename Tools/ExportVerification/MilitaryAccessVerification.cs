using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task VerifyMilitaryAccess(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var guest = c.NationByTag("POL"); var host = c.NationByTag("DEU");
        Check(guest is not null && host is not null, "military access uses actual country diplomacy");
        if (guest is null || host is null) return;
        // Explicit development-only in-memory fixture. Never loads or writes a player save.
        c.Wars.Clear(); c.ConfigureSinglePlayer(guest.Id); c.IsPaused = true;
        var r = CampaignDiplomacy.Get(c, guest.Id, host.Id);
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            game.Call("RefreshLocalisedText");
            r.Stance = DiplomaticStance.Neutral; r.Opinion = 70f;
            r.MilitaryAccessByA = r.MilitaryAccessByB = false; r.MilitaryAccessRequestedBy = -1;
            guest.DecisionAvailableDay.Remove("military_access/" + host.Id);
            host.IsHuman = false;
            game.VerificationAccessDiplomacy(host.Tag);
            await Wait(.25);
            var ask = game.FindChildren("WideRequestMilitaryAccess", "Button", true, false).OfType<Button>().Single();
            ask.GrabFocus(); await Wait(.15);
            Check(ask.IsVisibleInTree() && !ask.Disabled && ask.Text == Loc.Get("Request military access"), "request button is localised and usable: " + language + " " + size.X);
            var clock = c.ElapsedHours; var rng = c.Random.State;
            ask.EmitSignal(BaseButton.SignalName.Pressed);
            Check(MilitaryAccess.Granted(c, guest.Id, host.Id) && !MilitaryAccess.Granted(c, host.Id, guest.Id), "real request immediately grants only the requested direction");
            Check(c.IsPaused && c.ElapsedHours == clock && c.Random.State == rng, "access button keeps pause, time and RNG");
            Check(ask.Text == Loc.Get("Renounce military access"), "request button immediately displays the active permission");
            var grant = game.FindChildren("WideGrantMilitaryAccess", "Button", true, false).OfType<Button>().Single();
            grant.EmitSignal(BaseButton.SignalName.Pressed);
            Check(MilitaryAccess.Granted(c, host.Id, guest.Id) && grant.Text == Loc.Get("Revoke military access"), "real offer button grants the opposite direction and changes caption");
            grant.GrabFocus(); await Wait(.15);
            // Controls live in a scaled canvas. Comparing their global canvas coordinates
            // with physical window pixels falsely rejects the 720p layout. Check both the
            // actual clipping ancestor and viewport in their respective coordinate spaces.
            Node ancestor = grant.GetParent();
            while (ancestor is not null && ancestor is not ScrollContainer) ancestor = ancestor.GetParent();
            var scroll = ancestor as ScrollContainer;
            var inScroll = scroll is null ? default : scroll.GetGlobalTransform().AffineInverse()
                * grant.GetGlobalTransform() * new Rect2(Vector2.Zero, grant.Size);
            var inViewport = grant.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, grant.Size);
            GD.Print($"ACCESS_LAYOUT window={size} viewport={game.GetViewport().GetVisibleRect()} button={inViewport} clip={inScroll} scroll={scroll?.Size}");
            Check(scroll is not null && new Rect2(Vector2.Zero, scroll.Size).Grow(1).Encloses(inScroll)
                && game.GetViewport().GetVisibleRect().Grow(1).Encloses(inViewport),
                "military access actions fit after ordinary focus scrolling");
            var status = grant.GetParent().GetChildren().OfType<Label>().Single();
            Check(status.Text == Loc.Format("Your access: {0} · Their access: {1}",
                Loc.Get("Passage allowed"), Loc.Get("Passage allowed")), "access status values use the active language");
            var fp = CampaignFingerprint.Of(c);
            game.Call("ForceDiplomacyRefresh");
            Check(CampaignFingerprint.Of(c) == fp, "refreshing the permission status is read-only");
            await Capture("military-access-" + language + "-" + size.X);
            grant.EmitSignal(BaseButton.SignalName.Pressed);
            Check(!MilitaryAccess.Granted(c, host.Id, guest.Id) && MilitaryAccess.Granted(c, guest.Id, host.Id), "real revoke button preserves the opposite grant");
            ask.EmitSignal(BaseButton.SignalName.Pressed);
            Check(!MilitaryAccess.Granted(c, guest.Id, host.Id), "real renounce button removes the guest's permission");
        }
        // A human host must explicitly answer its letter, not be auto-signed by a high score.
        c.AddHumanSeat(host.Id); c.TakeSeat(0);
        guest.DecisionAvailableDay.Remove("military_access/" + host.Id);
        new MilitaryAccessCommand { IssuerId = guest.Id, TargetId = host.Id, Action = MilitaryAccessAction.Request }.Apply(c);
        Check(MilitaryAccess.Incoming(c, host.Id) == 1 && !MilitaryAccess.Granted(c, guest.Id, host.Id), "human host receives a letter without signature");
        c.TakeSeat(1); game.OpenDealInbox(); await Wait(.2);
        var letter = game.FindChild("MilitaryAccessLetter_" + guest.Id, true, false);
        var accept = letter?.GetChildren().OfType<Button>().FirstOrDefault();
        Check(accept is not null && accept.IsVisibleInTree(), "human host can reach the incoming access letter");
        await Capture("military-access-inbox-pending-russian-1280");
        accept?.EmitSignal(BaseButton.SignalName.Pressed);
        Check(MilitaryAccess.Granted(c, guest.Id, host.Id) && MilitaryAccess.Incoming(c, host.Id) == 0, "actual inbox consent grants access and closes the letter");
        await Capture("military-access-inbox-russian-1280");
    }
}

public partial class DealOrForceGame
{
    internal void VerificationAccessDiplomacy(string tag)
    {
        _observerMode = false; _campaign.ObserverMode = false; _campaign.IsPaused = true;
        VerificationOpenDiplomacy(tag);
        RefreshMilitaryAccessControls();
    }
}
