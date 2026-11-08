using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _warDeclaration;
    private readonly Dictionary<int, CheckBox> _warInvitations = new();
    private int _warDeclarationTarget = -1, _warDeclarationGoal = -1;
    private bool _warDeclarationSent;
    private Label _warDeclarationFeedback;
    private Button _warDeclarationConfirm;

    private void CloseWarDeclaration()
    {
        if (IsInstanceValid(_warDeclaration))
        {
            _warDeclaration.GetParent()?.RemoveChild(_warDeclaration); _warDeclaration.QueueFree();
            if (_globe is not null) _globe.LoadingInputBlocked = false;
        }
        _warDeclaration = null;
        _warInvitations.Clear();
        _warDeclarationTarget = -1;
        RestoreDecisionFocus(ref _warDeclarationReturnFocus);
    }

    /// <summary>One shared review for the dossier and full diplomacy screen.
    /// Enter never declares war; only the explicitly labelled final button does.</summary>
    private void OpenWarDeclaration(Nation target, int goal = -1)
    {
        if (_campaign?.Player is null || target is null || target == _campaign.Player) return;
        CloseWarDeclaration(); CloseDealComposer(); CloseUltimatumComposer();
        _warDeclarationReturnFocus = GetViewport().GuiGetFocusOwner();
        _warDeclarationTarget = target.Id; _warDeclarationGoal = goal; _warDeclarationSent = false;
        var preview = WarDeclarationPlanning.Preview(_campaign, _campaign.Player.Id, target.Id);
        _warDeclaration = FullLayer("WarDeclarationReview");
        _hudLayer.AddChild(_warDeclaration);
        _globe.LoadingInputBlocked = true;
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect); _warDeclaration.AddChild(shade);
        var panel = TexturedPanel(_warDeclaration);
        panel.Name = "WarDeclarationPanel";
        Ui.Place(panel, "modal.ultimatum");
        var rows = PanelRows(panel, 20, 16, 10);
        var header = ScreenHeader(rows, Loc.Format("Declare war on {0}?", target.DisplayName),
            "Review both sides before committing. Invitations are requests, not guarantees.",
            false, ActionButton("×", CloseWarDeclaration, 38, 32));
        PrependToScreenHeader(header, FlagBadge(target.Tag, 30, target.DisplayName));
        rows.AddChild(WrappedUltimatumText(Loc.Format("Reason: {0}",
            string.IsNullOrEmpty(preview.Cause) ? Tr("No recognized justification") : Tr(CasusBelliNames.Of(preview.Cause))), 13));
        rows.AddChild(WrappedUltimatumText("Forecast uses the current diplomatic rules, not random percentages. Treaties, finances and other wars can change the answer.", 12));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 18); rows.AddChild(body);
        AddWarSide(body, preview, false, _campaign.Player);
        AddWarSide(body, preview, true, target);
        _warDeclarationFeedback = WrappedUltimatumText(preview.Allowed
            ? "Nothing is sent until you confirm. Unchecked partners will not receive an offensive invitation."
            : preview.Refusal, 12);
        _warDeclarationFeedback.Name = "WarDeclarationFeedback"; rows.AddChild(_warDeclarationFeedback);
        var actions = new HFlowContainer { Name = "WarDeclarationActions", Alignment = FlowContainer.AlignmentMode.End };
        actions.AddThemeConstantOverride("h_separation", 10); actions.AddThemeConstantOverride("v_separation", 8); rows.AddChild(actions);
        var cancel = ActionButton("Cancel", CloseWarDeclaration, 130, 38);
        cancel.Name = "CancelWarDeclaration"; actions.AddChild(cancel);
        var refresh = ActionButton("Refresh forecast", () =>
        {
            var selected = _warInvitations.Where(p => p.Value.ButtonPressed).Select(p => p.Key).ToHashSet();
            OpenWarDeclaration(target, goal);
            foreach (var (id, box) in _warInvitations) box.ButtonPressed = selected.Contains(id);
        }, 160, 38);
        refresh.Name = "RefreshWarForecast";
        actions.AddChild(refresh);
        _warDeclarationConfirm = ActionButton("Declare War", SubmitWarDeclaration, 0, 38, true);
        _warDeclarationConfirm.Name = "ConfirmWarDeclaration";
        _warDeclarationConfirm.Disabled = !preview.Allowed;
        // Prevent an accidental key repeat from activating a freshly opened destructive action.
        _warDeclarationConfirm.FocusMode = FocusModeEnum.All;
        _warDeclarationConfirm.TooltipText = preview.Allowed ? Tr("Confirm with a click or Space. Enter never declares war.") : preview.Refusal;
        actions.AddChild(_warDeclarationConfirm);
        ReadableDecision(panel, "modal.ultimatum");
        cancel.GrabFocus();
    }

    private void AddWarSide(HBoxContainer body, WarDeclarationPreview preview, bool defending, Nation leader)
    {
        var column = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsStretchRatio = 1 };
        column.AddThemeConstantOverride("separation", 8); body.AddChild(column);
        SectionRule(column, defending ? "POSSIBLE OPPOSITION" : "YOUR POSSIBLE PARTNERS");
        var lead = new HBoxContainer(); lead.AddThemeConstantOverride("separation", 8); column.AddChild(lead);
        lead.AddChild(FlagBadge(leader.Tag, 26, leader.DisplayName));
        lead.AddChild(Title(leader.DisplayName, 17, Ui.Colour("text.bright")));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, VerticalScrollMode = ScrollContainer.ScrollMode.Auto, FollowFocus = true };
        column.AddChild(scroll);
        var list = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        list.AddThemeConstantOverride("separation", 8); scroll.AddChild(list);
        var candidates = preview.Support.Where(p => p.Defending == defending)
            .OrderByDescending(p => p.Automatic).ThenByDescending(p => p.ExpectedJoin).ThenBy(p => p.NationId).ToArray();
        if (candidates.Length == 0) list.AddChild(WrappedUltimatumText("No eligible partners identified. Later intervention is still possible.", 13));
        foreach (var candidate in candidates)
        {
            var nation = _campaign.NationById(candidate.NationId); if (nation is null) continue;
            var card = new PanelContainer { Name = "WarPartner_" + (defending ? "Defender_" : "Attacker_") + nation.Id,
                SizeFlagsHorizontal = SizeFlags.ExpandFill };
            card.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 8)); list.AddChild(card);
            var content = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            content.AddThemeConstantOverride("separation", 4); card.AddChild(content);
            var name = new HBoxContainer(); name.AddThemeConstantOverride("separation", 8); content.AddChild(name);
            name.AddChild(FlagBadge(nation.Tag, 24, nation.DisplayName));
            var title = WrappedUltimatumText(nation.DisplayName, 14); title.SizeFlagsHorizontal = SizeFlags.ExpandFill; name.AddChild(title);
            var likelihood = candidate.HumanDecision ? "Player decision" : candidate.ExpectedJoin ? "Likely to join" : "Unlikely to join";
            var status = WrappedUltimatumText(Loc.Format("{0} · {1}", Tr(candidate.Role), Tr(likelihood)), 12);
            status.LabelSettings.FontColor = Ui.Colour(candidate.ExpectedJoin ? "state.positive" : "text.muted"); content.AddChild(status);
            content.AddChild(WrappedUltimatumText(Tr(candidate.Reason), 12));
            if (!defending && !candidate.Automatic)
            {
                var box = new CheckBox { Name = "InviteWarPartner_" + nation.Id, Text = Tr("Invite on declaration"),
                    Disabled = !candidate.CanInvite, ButtonPressed = candidate.CanInvite && candidate.ExpectedJoin,
                    TooltipText = Tr(candidate.Reason),
                    SizeFlagsHorizontal = SizeFlags.ExpandFill };
                content.AddChild(box); _warInvitations[nation.Id] = box;
            }
            else if (candidate.Automatic)
                content.AddChild(WrappedUltimatumText(defending ? "Defensive commitments are evaluated automatically."
                    : candidate.HumanDecision ? "A player must explicitly accept." : "Subject treaty: no invitation required.", 11));
            else
                content.AddChild(WrappedUltimatumText("Possible later intervention; not automatically called.", 11));
        }
    }

    private void SubmitWarDeclaration()
    {
        if (_warDeclarationSent || _campaign?.Player is null) return;
        var target = _campaign.NationById(_warDeclarationTarget);
        var refusal = "";
        if (target is null || !CampaignDiplomacy.CanDeclareWar(_campaign, _campaign.Player.Id, target.Id, out refusal))
        {
            _warDeclarationFeedback.Text = target is null ? Tr("Invalid target.") : refusal;
            _warDeclarationConfirm.Disabled = true; return;
        }
        _warDeclarationSent = true; _warDeclarationConfirm.Disabled = true;
        _campaign.Commands.Enqueue(new DeclareWarCampaignCommand { IssuerId = _campaign.Player.Id, TargetId = target.Id,
            ClaimedStateMapIndex = _warDeclarationGoal,
            InvitedNationIds = _warInvitations.Where(p => p.Value.ButtonPressed && !p.Value.Disabled).Select(p => p.Key).OrderBy(id => id).ToArray() });
        _campaign.Commands.ApplyAll(_campaign);
        CloseWarDeclaration();
        PlayStrategicCue("war_declared");
        _pendingWarTarget = -1; _diplomacyPendingWarTarget = -1;
        if (IsInstanceValid(_countryActionFeedback)) _countryActionFeedback.Text = Loc.Format("War declaration submitted against {0}.", target.DisplayName);
        if (IsInstanceValid(_diplomacyFeedback)) _diplomacyFeedback.Text = Loc.Format("War declaration submitted against {0}.", target.DisplayName);
        ForceDiplomacyRefresh(); RefreshCountryViews("War declaration");
    }
}
