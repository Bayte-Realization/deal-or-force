using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    // Explicitly development-only, in-memory fixtures. No player save is loaded or written.
    private async Task VerifyDiplomaticParticipation(DealOrForceGame game)
    {
        var c = game.VerificationWorld;
        var applicant = c.NationByTag("POL"); var memberA = c.NationByTag("DEU");
        var memberB = c.NationByTag("FRA"); var power = c.NationByTag("USA");
        Check(applicant is not null && memberA is not null && memberB is not null && power is not null,
            "participation fixtures use real scenario countries");
        if (applicant is null || memberA is null || memberB is null || power is null) return;
        c.Wars.Clear(); c.Organizations.Clear(); c.Defines.AiMayFoundOrganizations = false;
        var council = InternationalOrganizations.Seed(c, "European Cooperation Council", "cooperation", new[] { memberA.Id, memberB.Id });
        var relation = CampaignDiplomacy.Get(c, memberA.Id, memberB.Id);
        c.ConfigureSinglePlayer(applicant.Id); c.IsPaused = true;
        game.VerificationEnableParticipationOrders();

        Button Button(string name) => game.FindChild(name, true, false) as Button;
        void Press(string name)
        {
            var button = Button(name);
            Check(button is { Disabled: false } && button.IsVisibleInTree(), "real diplomatic button is usable: " + name);
            if (button is { Disabled: false }) button.EmitSignal(BaseButton.SignalName.Pressed);
        }
        async Task Reveal(Control control, string caption)
        {
            Check(control is not null, caption + " exists");
            if (control is null) return;
            control.GrabFocus(); await Wait(.2);
            Node ancestor = control.GetParent();
            while (ancestor is not null && ancestor is not ScrollContainer) ancestor = ancestor.GetParent();
            var scroll = ancestor as ScrollContainer;
            var inViewport = control.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, control.Size);
            var inScroll = scroll is null ? default : scroll.GetGlobalTransform().AffineInverse()
                * control.GetGlobalTransform() * new Rect2(Vector2.Zero, control.Size);
            Check(control.IsVisibleInTree() && scroll is not null &&
                new Rect2(Vector2.Zero, scroll.Size).Grow(1).Encloses(inScroll) &&
                game.GetViewport().GetVisibleRect().Grow(1).Encloses(inViewport), caption + " fits after normal focus scrolling");
        }
        foreach (var language in new[] { "english", "russian" })
        foreach (var size in new[] { new Vector2I(1920,1080), new Vector2I(1280,720) })
        {
            Loc.Language = language; GetTree().Root.Size = size;
            c.ConfigureSinglePlayer(applicant.Id); c.IsPaused = true;
            applicant.PoliticalPoints = 100; applicant.Treasury = 20;
            council.Motion = null; council.Members.Remove(applicant.Id); council.Invitations.Clear();
            applicant.DecisionAvailableDay.Remove("organization_application/" + council.Id);
            game.Call("RefreshLocalisedText"); game.Call("OpenConflictsScreen"); await Wait(.25);
            Press("ConflictTab4"); Press("Organization_" + council.Id); await Wait(.2);
            var fp = CampaignFingerprint.Of(c); var rng = c.Random.State; var clock = c.ElapsedHours;
            game.Call("RefreshConflictsScreen", true); await Wait(.15);
            Check(CampaignFingerprint.Of(c) == fp && c.Random.State == rng, "organization inspection changes no world or randomness");
            var request = Button("RequestOrganizationMembership");
            Check(request?.Text == Loc.Get("Apply for membership"), "membership application is translated: " + language);
            await Reveal(request, "application action " + language + " " + size.X);
            await Capture("organization-apply-" + language + "-" + size.X);
            Press("RequestOrganizationMembership");
            Check(council.Motion is null, "first application click asks for confirmation");
            Press("RequestOrganizationMembership");
            Check(council.Motion is { Kind: "admission" } && council.Motion.TargetId == applicant.Id &&
                council.Motion.Votes.Count == 0 && !council.Members.Contains(applicant.Id), "actual second click starts only an admission vote");
            Check(c.IsPaused && c.ElapsedHours == clock && c.Random.State == rng, "application responds while paused without consuming random state");
            await Reveal(Button("WithdrawOrganizationApplication"), "withdrawal action");
            await Capture("organization-pending-" + language + "-" + size.X);
            Press("WithdrawOrganizationApplication"); Press("WithdrawOrganizationApplication");
            Check(council.Motion is null && Button("RequestOrganizationMembership") is { Disabled: true },
                "withdrawal immediately shows the application cooldown");

            var draft = game.FindChild("OrganizationName", true, false) as LineEdit;
            draft.Text = "New Council " + language + " " + size.X;
            // Assigning LineEdit.Text does not emit the signal generated by actual typing.
            draft.EmitSignal(LineEdit.SignalName.TextChanged, draft.Text);
            await Reveal(Button("CreateOrganization"), "founding action " + language + " " + size.X);
            await Capture("organization-founding-" + language + "-" + size.X);
            var money = applicant.Treasury; var capital = applicant.PoliticalPoints;
            Press("CreateOrganization"); Press("CreateOrganization");
            Check(applicant.Treasury == money - c.Defines.OrganizationFoundingDeposit &&
                applicant.PoliticalPoints == capital - InternationalOrganizations.FoundingPoliticalCost(c),
                "actual founding form pays both published costs exactly once");

            // Reach the third-party page through the normal hotspot row, not a substitute view.
            c.ConfigureSinglePlayer(power.Id); c.IsPaused = true; power.Treasury = 100;
            relation.Tension = c.Defines.TensionCrisisAt + 15; relation.TensionStage = (int)TensionStage.Crisis;
            relation.TalksUntilDay = 0; relation.LastMediationDay = -10000;
            relation.InterveningPowerId = -1; relation.InterventionUntilDay = 0;
            game.Call("RefreshLocalisedText"); Press("ConflictTab0");
            var search = game.FindChild("ConflictSearch", true, false) as LineEdit;
            search.Text = c.NationById(relation.A).Tag + " " + c.NationById(relation.B).Tag;
            search.EmitSignal(LineEdit.SignalName.TextChanged, search.Text); await Wait(.25);
            var pairKey = ((long)relation.A << 32) | (uint)relation.B;
            Press("Pair_" + pairKey); await Wait(.2);
            var mediate = Button("MediateForeignCrisis");
            await Reveal(mediate, "mediation action " + language + " " + size.X);
            await Capture("crisis-actions-" + language + "-" + size.X);
            fp = CampaignFingerprint.Of(c); game.Call("RefreshConflictsScreen", true);
            Check(CampaignFingerprint.Of(c) == fp, "foreign crisis inspection is read-only");
            money = power.Treasury; clock = c.ElapsedHours;
            Press("MediateForeignCrisis");
            Check(relation.MediatorId == power.Id && power.Treasury == money - c.Defines.CrisisMediationCost &&
                c.IsPaused && c.ElapsedHours == clock, "actual mediation button charges the acting player immediately on pause");
            Check(Button("MediateForeignCrisis") is { Disabled: true }, "running talks disable repeat mediation");
            // Mediation has de-escalated this fixture below the crisis threshold.
            // Test intervention in a separate renewed crisis, not by bypassing that rule.
            relation.Tension = c.Defines.TensionCrisisAt + 15;
            game.Call("RefreshConflictsScreen", true);
            Press("InterveneFor_" + memberA.Id);
            Check(relation.InterveningPowerId == power.Id && relation.InterventionTargetId == memberB.Id && c.Wars.Count == 0,
                "actual intervention button protects the chosen side without declaring war");
            Check(Button("InterveneFor_" + memberA.Id) is { Disabled: true } && Button("InterveneFor_" + memberB.Id) is { Disabled: true },
                "an active intervention disables both duplicate sides");
            await Reveal(Button("InterveneFor_" + memberB.Id), "intervention status");
            await Capture("crisis-recorded-" + language + "-" + size.X);
        }
    }
}

public partial class DealOrForceGame
{
    internal void VerificationEnableParticipationOrders()
    { _observerMode = false; _campaign.ObserverMode = false; _campaign.IsPaused = true; }
}
