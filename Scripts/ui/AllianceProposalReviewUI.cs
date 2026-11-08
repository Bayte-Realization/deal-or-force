using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _allianceProposalReview, _allianceProposalReturnFocus;
    private VBoxContainer _allianceProposalReasons;
    private Label _allianceProposalStatus, _allianceProposalScore, _allianceProposalRule, _allianceProposalFeedback;
    private Button _allianceProposalConfirm;
    private Campaign _allianceProposalCampaign;
    private int _allianceProposalIssuer = -1, _allianceProposalTarget = -1;
    private bool _allianceProposalSent;
    private AllianceProposalReadout _allianceProposalReadout;

    private sealed record AllianceProposalReadout(bool Allowed, string Refusal, bool HumanDecision,
        float Score, float Threshold, Breakdown Reasons);

    // This is a quote, not an assessment refresh. It must not create relations, update
    // intelligence, spend a command or advance the random stream merely by being read.
    private AllianceProposalReadout ReadAllianceProposal(Nation target)
    {
        var allowed = AllianceSystem.CanPropose(_campaign, _campaign?.Player, target, out var refusal, preview: true);
        var reasons = new Breakdown();
        var score = AllianceSystem.Willingness(_campaign, target, _campaign?.Player, reasons, refreshAssessment: false);
        return new(allowed, refusal, target?.IsHuman == true, score, _campaign?.Defines.AllianceWillingness ?? 0, reasons);
    }

    private static string AllianceProposalOutcome(AllianceProposalReadout quote) => Loc.Get(!quote.Allowed
        ? "Alliance offer unavailable"
        : quote.HumanDecision ? "Player decision: they must answer the offer themselves."
        : quote.Score > quote.Threshold ? "They would accept this alliance." : "They would refuse this alliance.");

    private static string AllianceProposalRule(AllianceProposalReadout quote) => quote.HumanDecision
        ? Loc.Get("The score is advisory for a player. It cannot accept or refuse on their behalf.")
        : Loc.Format("The AI accepts only when the score is greater than {0:0.000}. An equal score is a refusal.", quote.Threshold);

    private string AllianceProposalTooltip(Nation target)
    {
        var quote = ReadAllianceProposal(target);
        return Ui.Tip(AllianceProposalOutcome(quote) + "\n" +
            (!quote.Allowed ? Loc.Get(quote.Refusal) + "\n" : "") +
            Loc.Format("Acceptance score: {0:+0.000;-0.000;0.000}", quote.Score) + "\n" +
            AllianceProposalRule(quote) + "\n" +
            Loc.Get("Score factors are from the recipient's point of view.") + "\n" +
            string.Join("\n", quote.Reasons.Entries.Select(entry =>
                Loc.Format("{0}: {1:+0.000;-0.000;0.000}", Loc.Get(entry.Source), entry.Value))) + "\n" +
            Loc.Get("Review every reason before sending the alliance offer."));
    }

    private void CloseAllianceProposalReview()
    {
        if (IsInstanceValid(_allianceProposalReview))
        {
            _allianceProposalReview.GetParent()?.RemoveChild(_allianceProposalReview);
            _allianceProposalReview.QueueFree();
            if (_globe is not null) _globe.LoadingInputBlocked = false;
        }
        _allianceProposalReview = null;
        _allianceProposalCampaign = null;
        _allianceProposalIssuer = _allianceProposalTarget = -1;
        _allianceProposalReadout = null;
        RestoreDecisionFocus(ref _allianceProposalReturnFocus);
    }

    private void OpenAllianceProposalReview(Nation target)
    {
        if (_campaign?.Player is not { } player || target is null || player.Id == target.Id) return;
        CloseAllianceProposalReview(); CloseWarDeclaration(); CloseDealComposer(); CloseUltimatumComposer(); CloseNuclearStrikeReview();
        _allianceProposalReturnFocus = GetViewport().GuiGetFocusOwner();
        _allianceProposalCampaign = _campaign;
        _allianceProposalIssuer = player.Id; _allianceProposalTarget = target.Id; _allianceProposalSent = false;
        _allianceProposalReview = FullLayer("AllianceProposalReview");
        _hudLayer.AddChild(_allianceProposalReview);
        _allianceProposalReview.MoveToFront();
        _globe.LoadingInputBlocked = true;
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _allianceProposalReview.AddChild(shade);
        var panel = TexturedPanel(_allianceProposalReview, PanelSurface.Modal); panel.Name = "AllianceProposalPanel";
        Ui.Place(panel, "modal.ultimatum");
        var rows = PanelRows(panel, 20, 16, 10);
        var header = ScreenHeader(rows, Loc.Format("Propose an alliance to {0}?", target.DisplayName),
            "Review the recipient's reasons. Nothing is sent until you confirm.", false,
            ActionButton("×", CloseAllianceProposalReview, 38, 32));
        PrependToScreenHeader(header, FlagBadge(target.Tag, 30, target.DisplayName));
        var scroll = new ScrollContainer { Name = "AllianceProposalScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            VerticalScrollMode = ScrollContainer.ScrollMode.Auto, FollowFocus = true };
        rows.AddChild(scroll);
        var body = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 10); scroll.AddChild(body);
        _allianceProposalStatus = WrappedUltimatumText("", 17); _allianceProposalStatus.Name = "AllianceProposalOutcome";
        body.AddChild(_allianceProposalStatus);
        _allianceProposalScore = WrappedUltimatumText("", 17); _allianceProposalScore.Name = "AllianceProposalScore";
        body.AddChild(_allianceProposalScore);
        _allianceProposalRule = WrappedUltimatumText("", 14); _allianceProposalRule.Name = "AllianceProposalRule";
        body.AddChild(_allianceProposalRule);
        body.AddChild(WrappedUltimatumText("Score factors are from the recipient's point of view.", 14));
        _allianceProposalReasons = new VBoxContainer { Name = "AllianceProposalReasons", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _allianceProposalReasons.AddThemeConstantOverride("separation", 8); body.AddChild(_allianceProposalReasons);
        body.AddChild(WrappedUltimatumText("An alliance is a promise of military support. The assessment can change before you send it.", 14));
        _allianceProposalFeedback = WrappedUltimatumText("", 14); _allianceProposalFeedback.Name = "AllianceProposalFeedback";
        rows.AddChild(_allianceProposalFeedback);
        var actions = new HFlowContainer { Name = "AllianceProposalActions", Alignment = FlowContainer.AlignmentMode.End };
        actions.AddThemeConstantOverride("h_separation", 10); actions.AddThemeConstantOverride("v_separation", 8); rows.AddChild(actions);
        var cancel = ActionButton("Cancel", CloseAllianceProposalReview, 130, 38); cancel.Name = "CancelAllianceProposal";
        actions.AddChild(cancel);
        var refresh = ActionButton("Refresh assessment", () => RefreshAllianceProposalReview(), 170, 38);
        refresh.Name = "RefreshAllianceProposal"; actions.AddChild(refresh);
        _allianceProposalConfirm = ActionButton("Send alliance offer", SubmitAllianceProposal, 190, 38, true);
        _allianceProposalConfirm.Name = "ConfirmAllianceProposal"; actions.AddChild(_allianceProposalConfirm);
        RefreshAllianceProposalReview(); ReadableDecision(panel, "modal.ultimatum"); cancel.GrabFocus();
    }

    private void RefreshAllianceProposalReview(AllianceProposalReadout current = null, string feedback = null)
    {
        if (!IsInstanceValid(_allianceProposalReview) || _campaign != _allianceProposalCampaign ||
            _campaign?.Player?.Id != _allianceProposalIssuer) { CloseAllianceProposalReview(); return; }
        var target = _campaign.NationById(_allianceProposalTarget);
        _allianceProposalReadout = current ?? ReadAllianceProposal(target);
        var quote = _allianceProposalReadout;
        _allianceProposalStatus.Text = AllianceProposalOutcome(quote);
        _allianceProposalStatus.LabelSettings.FontColor = Ui.Colour(!quote.Allowed ? "state.warning"
            : quote.HumanDecision ? "state.info" : quote.Score > quote.Threshold ? "state.positive" : "state.warning");
        _allianceProposalScore.Text = Loc.Format("Acceptance score: {0:+0.000;-0.000;0.000}", quote.Score);
        _allianceProposalRule.Text = AllianceProposalRule(quote);
        foreach (var child in _allianceProposalReasons.GetChildren()) { _allianceProposalReasons.RemoveChild(child); child.QueueFree(); }
        var index = 0;
        foreach (var positive in new[] { true, false })
        {
            SectionRule(_allianceProposalReasons, positive ? "REASONS TO ACCEPT" : "REASONS TO REFUSE");
            var factors = quote.Reasons.Entries.Where(entry => positive ? entry.Value >= 0 : entry.Value < 0).ToArray();
            if (factors.Length == 0) _allianceProposalReasons.AddChild(WrappedUltimatumText(
                positive ? "No positive score factors." : "No negative score factors.", 14));
            foreach (var entry in factors)
            {
                var row = new HBoxContainer { Name = "AllianceProposalFactor_" + index++, SizeFlagsHorizontal = SizeFlags.ExpandFill };
                row.SetMeta("source", entry.Source); row.SetMeta("value", entry.Value);
                row.AddThemeConstantOverride("separation", 16); _allianceProposalReasons.AddChild(row);
                var label = WrappedUltimatumText(Loc.Get(entry.Source), 14); label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
                row.AddChild(label);
                var value = Title(entry.Value.ToString("+0.000;-0.000;0.000", Loc.Culture), 15,
                    Ui.Colour(entry.Value < 0 ? "state.warning" : "state.positive"));
                value.HorizontalAlignment = HorizontalAlignment.Right; value.CustomMinimumSize = new Vector2(100, 0);
                row.AddChild(value);
            }
        }
        _allianceProposalConfirm.Disabled = !quote.Allowed || _allianceProposalSent;
        _allianceProposalConfirm.TooltipText = Ui.Tip(!quote.Allowed ? quote.Refusal
            : Loc.Get("Send this alliance offer. The recipient's current rules are checked again before submission."));
        _allianceProposalFeedback.Text = !quote.Allowed ? Loc.Get(quote.Refusal)
            : feedback ?? Loc.Get("Confirm sends one offer. Escape or Cancel closes this review without sending.");
        (_allianceProposalReview.FindChild("ReadableDecisionLayout", true, false) as DecisionDialogLayout)?.Refit();
    }

    private void SubmitAllianceProposal()
    {
        if (_allianceProposalSent || !IsInstanceValid(_allianceProposalReview)) return;
        if (_campaign != _allianceProposalCampaign || _campaign?.Player?.Id != _allianceProposalIssuer)
        { CloseAllianceProposalReview(); return; }
        var target = _campaign.NationById(_allianceProposalTarget);
        var current = ReadAllianceProposal(target);
        if (!current.Allowed) { RefreshAllianceProposalReview(current); return; }
        var prior = _allianceProposalReadout;
        if (prior is null || prior.HumanDecision != current.HumanDecision || prior.Score != current.Score ||
            prior.Threshold != current.Threshold || !prior.Reasons.Entries.SequenceEqual(current.Reasons.Entries))
        {
            RefreshAllianceProposalReview(current, Loc.Get("The assessment changed. Review the updated reasons before confirming again."));
            return;
        }
        _allianceProposalSent = true; _allianceProposalConfirm.Disabled = true;
        var issuer = _allianceProposalIssuer;
        _campaign.Commands.Enqueue(new ProposeAllianceCommand { IssuerId = issuer, TargetId = target.Id });
        _campaign.Commands.ApplyAll(_campaign);
        var relation = CampaignDiplomacy.Peek(_campaign, issuer, target.Id);
        var feedback = _networkConnected && !_networkIsHost
            ? Loc.Get("Alliance proposal sent to the host. Awaiting confirmation.")
            : relation?.Stance == DiplomaticStance.Allied ? Loc.Format("{0} accepted.", target.DisplayName)
            : target.IsHuman ? Loc.Format("Alliance proposal sent to {0}. They must answer it themselves.", target.DisplayName)
            : relation?.AllianceRefusedDay == _campaign.ElapsedDays ? Loc.Format("{0} declined.", target.DisplayName)
            : Loc.Format("Alliance proposal submitted to {0}.", target.DisplayName);
        CloseAllianceProposalReview();
        if (IsInstanceValid(_diplomacyFeedback)) _diplomacyFeedback.Text = feedback;
        if (IsInstanceValid(_countryActionFeedback)) _countryActionFeedback.Text = feedback;
        ForceDiplomacyRefresh(); RefreshCountryViews("Diplomacy");
    }
}
