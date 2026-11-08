using System;
using System.Linq;
using System.Threading.Tasks;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class ExportVerification
{
    private async Task ClickAllianceControl(Button button, string caption, bool repeat = false)
    {
        Check(button is { Disabled: false } && button.IsVisibleInTree(), "alliance click target is usable: " + caption);
        if (button is not { Disabled: false } || !button.IsVisibleInTree()) return;
        Control reveal = button;
        for (Node parent = button.GetParent(); parent is not null; parent = parent.GetParent())
            if (parent is ScrollContainer scroll) { scroll.EnsureControlVisible(reveal); reveal = scroll; }
        button.GrabFocus(); await Wait(.15);
        var point = button.GetGlobalTransformWithCanvas() * (button.Size * .5f);
        GetViewport().PushInput(new InputEventMouseMotion { Position = point }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left,
            ButtonMask = MouseButtonMask.Left, Pressed = true }, true);
        GetViewport().PushInput(new InputEventMouseButton { Position = point, ButtonIndex = MouseButton.Left, Pressed = false }, true);
        // Exercise a queued activation while the just-closed button still exists for
        // this frame. The submission guard must refuse it before the deferred free.
        if (repeat && IsInstanceValid(button)) button.EmitSignal(BaseButton.SignalName.Pressed);
        await Wait(.3);
    }

    private async Task AllianceReviewKey(Key key, bool shift = false)
    {
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = true, ShiftPressed = shift }, true);
        GetViewport().PushInput(new InputEventKey { Keycode = key, PhysicalKeycode = key, Pressed = false, ShiftPressed = shift }, true);
        await Wait(.1);
    }

    private void CheckAllianceReviewReadability(DealOrForceGame game, string caption)
    {
        var panel = game.FindChild("AllianceProposalPanel", true, false) as Control;
        Check(panel is not null && panel.IsVisibleInTree(), "alliance review is visible: " + caption);
        if (panel is null) return;
        Check(game.GetViewport().GetVisibleRect().Grow(1).Encloses(
            panel.GetGlobalTransformWithCanvas() * new Rect2(Vector2.Zero, panel.Size)), "alliance review fits viewport: " + caption);
        foreach (var control in panel.FindChildren("*", "Control", true, false).OfType<Control>()
                     .Where(control => control is Label or Button))
        {
            var transform = game.GetViewport().GetStretchTransform() * control.GetGlobalTransformWithCanvas();
            var scale = MathF.Min(MathF.Abs(transform.Scale.X), MathF.Abs(transform.Scale.Y));
            var font = control is Label { LabelSettings: not null } label
                ? label.LabelSettings.FontSize : control.GetThemeFontSize("font_size");
            Check(font * scale >= Ui.Metric("decision.minimum_font_pixels", 14) - .1f,
                "alliance text has physical font floor: " + caption + " " + control.Name);
            if (control is Button) Check(control.Size.Y * scale >= Ui.Metric("decision.minimum_target_pixels", 32) - .1f,
                "alliance action has physical hit target: " + caption + " " + control.Name);
        }
    }

    private async Task VerifyAllianceProposalReview(DealOrForceGame game)
    {
        var world = game.VerificationWorld;
        var proposer = world.NationByTag("POL"); var target = world.NationByTag("DEU");
        Check(proposer is not null && target is not null, "alliance review uses real campaign countries");
        if (proposer is null || target is null) return;
        // An explicit probe-only in-memory fixture. No player save is loaded or written.
        world.Wars.Clear(); world.ConfigureSinglePlayer(proposer.Id); world.IsPaused = true;
        proposer.Notoriety = 70; target.Stability = .35f; target.IsHuman = false;
        var relation = CampaignDiplomacy.Get(world, proposer.Id, target.Id);
        relation.Stance = DiplomaticStance.Neutral; relation.Opinion = 80;
        relation.AllianceOfferedDay = relation.AllianceRefusedDay = -9999;
        var originalLanguage = Loc.Language; var originalSize = GetTree().Root.Size;
        var originalScale = SettingsCatalog.Number("interface_scale");
        var originalThreshold = world.Defines.AllianceWillingness;
        var originalInterceptor = world.Commands.SubmitInterceptor;
        var submitted = 0;
        world.Commands.SubmitInterceptor = command =>
        {
            if (command is ProposeAllianceCommand) submitted++;
            return originalInterceptor?.Invoke(command) ?? false;
        };
        try
        {
            foreach (var language in new[] { "english", "russian" })
            foreach (var size in new[] { new Vector2I(1920, 1080), new Vector2I(1280, 720) })
            {
                Loc.Language = language; GetTree().Root.Size = size;
                SettingsCatalog.Store("interface_scale", 1f); game.Call("ApplySetting", "interface_scale");
                game.Call("RefreshLocalisedText"); game.VerificationOpenDiplomacy(target.Tag); await Wait(.4);
                var action = game.VerificationAllianceProposalAction;
                var oracle = new Breakdown();
                var score = AllianceSystem.Willingness(world, target, proposer, oracle, refreshAssessment: false);
                Check(oracle.Entries.Count > 3 && oracle.Entries.Any(e => e.Value > 0) && oracle.Entries.Any(e => e.Value < 0),
                    "alliance fixture has more than three mixed score factors");
                var tooltip = action.TooltipText.Replace("\n", " ");
                Check(oracle.Entries.All(e => tooltip.Contains(Loc.Get(e.Source), StringComparison.Ordinal)),
                    "actual alliance button explains every score factor: " + language + size.X);
                var fingerprint = CampaignFingerprint.Of(world); var random = world.Random.State; var hours = world.ElapsedHours;
                var before = submitted;
                await ClickAllianceControl(action, "open review " + language + size.X);
                var dialog = game.FindChild("AllianceProposalReview", true, false) as Control;
                Check(dialog is not null && dialog.IsVisibleInTree(), "actual diplomacy button opens alliance review");
                if (dialog is null) return;
                CheckAllianceReviewReadability(game, language + size.X);
                var factors = dialog.FindChildren("AllianceProposalFactor_*", "HBoxContainer", true, false)
                    .Select(node => new Breakdown.Entry(node.GetMeta("source").AsString(), node.GetMeta("value").AsSingle())).ToArray();
                Check(factors.SequenceEqual(oracle.Entries.Where(e => e.Value >= 0).Concat(oracle.Entries.Where(e => e.Value < 0))),
                    "review displays every authoritative signed score factor without truncation");
                Check((game.FindChild("AllianceProposalScore", true, false) as Label)?.Text ==
                    Loc.Format("Acceptance score: {0:+0.000;-0.000;0.000}", score), "review total equals authoritative score");
                Check((game.FindChild("AllianceProposalRule", true, false) as Label)?.Text == Loc.Format(
                    "The AI accepts only when the score is greater than {0:0.000}. An equal score is a refusal.", world.Defines.AllianceWillingness),
                    "review explains the strict greater-than threshold");
                await ClickAllianceControl(game.FindChild("CancelAllianceProposal", true, false) as Button, "pointer cancellation");
                Check(game.FindChild("AllianceProposalReview", true, false) is null && GetViewport().GuiGetFocusOwner() == action &&
                    action.GetThemeStylebox("focus") is StyleBoxEmpty && !action.ButtonPressed,
                    "mouse cancellation preserves return position without a latched alliance focus ring: " + language + size.X);
                GetViewport().PushInput(new InputEventMouseMotion { Position = GetViewport().GetVisibleRect().Size * new Vector2(.66f, .7f) }, true);
                await Wait(.15); await Capture("alliance-pointer-closed-" + language + "-" + size.X);
                await ClickAllianceControl(action, "reopen after pointer cancellation");
                dialog = game.FindChild("AllianceProposalReview", true, false) as Control;
                var confirm = game.FindChild("ConfirmAllianceProposal", true, false) as Button;
                var scroll = game.FindChild("AllianceProposalScroll", true, false) as ScrollContainer;
                var footerRect = confirm.GetGlobalRect(); scroll.ScrollVertical = int.MaxValue; await Wait(.2);
                Check(confirm.GetGlobalRect() == footerRect, "alliance confirmation stays pinned while reasons scroll");
                for (var n = 0; n < 7; n++) await AllianceReviewKey(Key.Tab);
                Check(dialog.IsAncestorOf(GetViewport().GuiGetFocusOwner()), "alliance Tab remains in the review");
                for (var n = 0; n < 7; n++) await AllianceReviewKey(Key.Tab, shift: true);
                Check(dialog.IsAncestorOf(GetViewport().GuiGetFocusOwner()), "alliance Shift+Tab remains in the review");
                confirm.GrabFocus(); await AllianceReviewKey(Key.Enter);
                Check(submitted == before && CampaignFingerprint.Of(world) == fingerprint, "Enter does not submit an alliance offer");
                await ClickAllianceControl(game.FindChild("RefreshAllianceProposal", true, false) as Button, "refresh assessment");
                Check(submitted == before && CampaignFingerprint.Of(world) == fingerprint && world.Random.State == random && world.ElapsedHours == hours,
                    "opening and refreshing alliance review preserves fingerprint, RNG, time and command count");
                var cancelForReading = game.FindChild("CancelAllianceProposal", true, false) as Button;
                GetViewport().PushInput(new InputEventMouseMotion { Position = cancelForReading.GetGlobalTransformWithCanvas() * (cancelForReading.Size * .5f) }, true);
                await Wait(.35);
                await Capture("alliance-review-" + language + "-" + size.X);
                await AllianceReviewKey(Key.Escape);
                Check(game.FindChild("AllianceProposalReview", true, false) is null && submitted == before &&
                    CampaignFingerprint.Of(world) == fingerprint, "Escape cancels alliance review without any command");
                Check(GetViewport().GuiGetFocusOwner() == action, "alliance cancellation restores its opening button focus");
                Check(action.GetThemeStylebox("focus") is not StyleBoxEmpty && !action.ButtonPressed,
                    "keyboard cancellation retains a visible focus cue without pressing the alliance action: " + language + size.X);
                await VerifyMediaShortcutFocus(game, language, size.X);
            }

            game.VerificationOpenAllianceReview(target.Id); await Wait(.3);
            var beforeChanged = submitted; relation.Opinion -= 10;
            await ClickAllianceControl(game.FindChild("ConfirmAllianceProposal", true, false) as Button, "stale assessment");
            var revisedScore = AllianceSystem.Willingness(world, target, proposer, refreshAssessment: false);
            Check(submitted == beforeChanged && game.FindChild("AllianceProposalReview", true, false) is not null &&
                (game.FindChild("AllianceProposalScore", true, false) as Label)?.Text ==
                Loc.Format("Acceptance score: {0:+0.000;-0.000;0.000}", revisedScore) &&
                (game.FindChild("AllianceProposalFeedback", true, false) as Label)?.Text ==
                Loc.Get("The assessment changed. Review the updated reasons before confirming again."),
                "changed score refreshes the assessment before an offer can be confirmed");
            // AtWar reads active StrategicWar sides, not the relation's descriptive
            // stance. A stance-only fixture would remain eligible and send a real offer.
            var intervention = new StrategicWar { Id = 920001, AttackerId = proposer.Id,
                DefenderId = target.Id, StartedDay = world.ElapsedDays };
            world.Wars.Add(intervention); world.MarkWarsChanged();
            Check(CampaignDiplomacy.AtWar(world, proposer.Id, target.Id) &&
                !AllianceSystem.CanPropose(world, proposer, target, out _, preview: true),
                "alliance invalidation fixture creates actual opposing war sides");
            await ClickAllianceControl(game.FindChild("ConfirmAllianceProposal", true, false) as Button, "newly illegal offer");
            Check(submitted == beforeChanged && (game.FindChild("ConfirmAllianceProposal", true, false) as Button)?.Disabled == true,
                "eligibility is checked again at confirmation and an illegal offer is disabled");
            await AllianceReviewKey(Key.Escape);
            world.Wars.Remove(intervention); world.MarkWarsChanged();

            // At equality, the AI refuses; the player may still deliberately send one offer.
            // Keep this fixture independent of a failed invalidation assertion above.
            relation.Stance = DiplomaticStance.Neutral;
            relation.AllianceRefusedDay = relation.AllianceOfferedDay = -9999;
            Check(AllianceSystem.CanPropose(world, proposer, target, out var equalityRefusal, preview: true),
                "exact-threshold fixture permits a new offer: " + equalityRefusal);
            world.Defines.AllianceWillingness = AllianceSystem.Willingness(world, target, proposer, refreshAssessment: false);
            game.VerificationOpenAllianceReview(target.Id); await Wait(.3);
            Check((game.FindChild("AllianceProposalOutcome", true, false) as Label)?.Text == Loc.Get("They would refuse this alliance."),
                "the exact threshold is labelled as refusal rather than acceptance");
            var exactButton = game.FindChild("ConfirmAllianceProposal", true, false) as Button;
            var beforeExact = submitted;
            await ClickAllianceControl(exactButton, "confirm exact-threshold offer", repeat: true);
            Check(submitted == beforeExact + 1 && relation.Stance != DiplomaticStance.Allied &&
                relation.AllianceRefusedDay == world.ElapsedDays, "confirmed AI refusal submits exactly one normal campaign command");
            world.Defines.AllianceWillingness = originalThreshold;

            // A human's score cannot answer their letter. Preserve that free choice even below the AI threshold.
            relation.AllianceRefusedDay = relation.AllianceOfferedDay = -9999;
            world.AddHumanSeat(target.Id); world.TakeSeat(0); relation.Opinion = -100;
            game.VerificationOpenAllianceReview(target.Id); await Wait(.3);
            Check((game.FindChild("AllianceProposalOutcome", true, false) as Label)?.Text ==
                Loc.Get("Player decision: they must answer the offer themselves."), "human target is explicitly a manual decision");
            var humanConfirm = game.FindChild("ConfirmAllianceProposal", true, false) as Button;
            Check(humanConfirm is { Disabled: false }, "eligible human offer remains available regardless of score");
            var beforeHuman = submitted;
            await ClickAllianceControl(humanConfirm, "offer to a human");
            Check(submitted == beforeHuman + 1 && relation.Stance != DiplomaticStance.Allied &&
                AllianceSystem.OfferedTo(world, target)?.Id == proposer.Id,
                "one confirmed human proposal becomes an unanswered offer without signing");
        }
        finally
        {
            game.Call("CloseAllianceProposalReview");
            world.Commands.SubmitInterceptor = originalInterceptor;
            world.Defines.AllianceWillingness = originalThreshold;
            SettingsCatalog.Store("interface_scale", originalScale); game.Call("ApplySetting", "interface_scale");
            Loc.Language = originalLanguage; GetTree().Root.Size = originalSize;
        }
    }
}

public partial class DealOrForceGame
{
    internal Button VerificationAllianceProposalAction => _diplomacyAllianceButton;
    internal void VerificationOpenAllianceReview(int target) => OpenAllianceProposalReview(_campaign.NationById(target));
}
