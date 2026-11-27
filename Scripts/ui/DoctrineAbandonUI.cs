using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Two-click inline confirmation and cancellation controls for tactical doctrines,
/// in-progress article studies, and strategic courses across Book of War and Army Doctrines.
/// </summary>
public partial class DealOrForceGame
{
    private string _doctrineAbandonConfirmKey = "";
    private Button _doctrineToolbarAbandonButton;

    private void ResetDoctrineAbandonConfirm() => _doctrineAbandonConfirmKey = "";

    private static string DoctrineBooksPresentationKey(Nation n)
    {
        TacticalDoctrines.EnsureShelves(n);
        return string.Join(';', DoctrineSlots.All.Select(slot =>
        {
            var track = n.TacticalDoctrine.Tracks.GetValueOrDefault(slot);
            if (track is null) return slot + "=";
            var primary = track.Branch + "/" + string.Join(',', track.Known) + "/" +
                track.LearningBranch + "/" + track.LearningNode + "/" + track.LearningDoneDay;
            var comp = track.Companion;
            if (comp is null) return slot + "=" + primary;
            return slot + "=" + primary + "|" + comp.Branch + "/" + string.Join(',', comp.Known) + "/" +
                comp.LearningBranch + "/" + comp.LearningNode + "/" + comp.LearningDoneDay;
        }));
    }

    private static string StrategyAdoptButtonLabel(Nation n, StrategicDoctrine course, bool inForce)
    {
        if (inForce) return Loc.Get("in force");
        if (StrategicDoctrines.IsFirstChoiceFree(n)) return Loc.Get("take this course");
        if (n.StrategicDoctrine.Id.Length == 0) return Loc.Format("doctrine.ui.adopt_paid_fmt", Fmt.Number(course.PoliticalCost));
        return Loc.Format("change course ({0})", Fmt.Number(course.PoliticalCost));
    }

    private static void StyleDoctrineDangerButton(Button button, bool armed)
    {
        var token = armed ? "state.negative" : "state.warning";
        var normalAlpha = armed ? 0.26f : 0.14f;
        var hoverAlpha = armed ? 0.38f : 0.24f;
        var pressedAlpha = armed ? 0.50f : 0.34f;
        var borderAlpha = armed ? 0.95f : 0.70f;
        button.AddThemeStyleboxOverride("normal",
            Ui.ChromeBox(token, normalAlpha, token, borderAlpha, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4));
        button.AddThemeStyleboxOverride("hover",
            Ui.ChromeBox(token, hoverAlpha, token, 0.95f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4));
        button.AddThemeStyleboxOverride("pressed",
            Ui.ChromeBox(token, pressedAlpha, token, 1f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4));
        button.AddThemeStyleboxOverride("hover_pressed",
            Ui.ChromeBox(token, pressedAlpha, token, 1f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4));
        button.AddThemeStyleboxOverride("disabled",
            Ui.ChromeBox("button.fill_disabled", 0.85f, "surface.line_faint", 0.45f, cornerRadius: 3, borderWidth: 1, padX: 8, padY: 4));
        var textColor = Ui.Colour(armed ? "text.bright" : "state.warning");
        button.AddThemeColorOverride("font_color", textColor);
        button.AddThemeColorOverride("font_hover_color", Ui.Colour(armed ? "text.bright" : "state.warning"));
        button.AddThemeColorOverride("font_pressed_color", Ui.Colour("text.bright"));
        button.AddThemeColorOverride("font_disabled_color", Ui.Colour("button.text_disabled"));
    }

    private void EnsureDoctrineToolbarAbandonButton(HBoxContainer toolbar)
    {
        if (toolbar is null) return;
        _doctrineToolbarAbandonButton = ActionButton("", OnToolbarAbandonDoctrineClicked, 0, 32, false);
        _doctrineToolbarAbandonButton.Name = "AbandonDoctrineBook";
        _doctrineToolbarAbandonButton.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _doctrineToolbarAbandonButton.Visible = false;
        toolbar.AddChild(_doctrineToolbarAbandonButton);
    }

    private void RefreshDoctrineToolbarAbandon(Nation n)
    {
        if (_doctrineToolbarAbandonButton is null || !IsInstanceValid(_doctrineToolbarAbandonButton)) return;
        var branch = TacticalDoctrineCatalog.Find(_doctrineBook);
        if (n is null || branch is null || !TacticalDoctrines.IsHeld(n, branch.Id, out var established))
        {
            _doctrineToolbarAbandonButton.Visible = false;
            return;
        }
        _doctrineToolbarAbandonButton.Visible = true;
        ConfigureAbandonDoctrineButton(_doctrineToolbarAbandonButton, n, branch, established, "toolbar:" + branch.Id, compact: false);
    }

    private void OnToolbarAbandonDoctrineClicked()
    {
        var branch = TacticalDoctrineCatalog.Find(_doctrineBook);
        if (branch is null) return;
        HandleAbandonDoctrineClick(branch.Id, "toolbar:" + branch.Id);
    }

    private void ConfigureAbandonDoctrineButton(Button button, Nation n, DoctrineBranch branch, bool established, string confirmKey, bool compact)
    {
        var observing = ObserverUiRules.IsObserving(_campaign, _observerMode);
        var refusal = observing ? Tr("obs.ui.action.readonly") : TacticalDoctrines.AbandonRefusal(_campaign, n, branch.Id);
        var armed = !observing && refusal.Length == 0 && string.Equals(_doctrineAbandonConfirmKey, confirmKey, StringComparison.Ordinal);
        var cost = established ? _campaign.Defines.DoctrineRewritePoliticalCost : 0f;
        if (armed)
        {
            button.Text = cost > 0f
                ? Loc.Format("doctrine.ui.confirm_abandon_pp_fmt", Fmt.Number(cost))
                : Loc.Get("doctrine.ui.confirm_abandon_free");
        }
        else if (!established)
        {
            button.Text = Loc.Get(compact ? "doctrine.ui.cancel_study_btn" : "doctrine.ui.cancel_first_study_btn");
        }
        else
        {
            button.Text = Loc.Get(compact ? "doctrine.ui.abandon_short_btn" : "doctrine.ui.abandon_doctrine_btn");
        }
        button.Disabled = observing || refusal.Length > 0;
        button.TooltipText = Ui.Tip(refusal.Length > 0
            ? refusal
            : !established
                ? Loc.Format("doctrine.ui.cancel_first_study_tip_fmt", Tr(branch.Name))
                : cost > 0f
                    ? Loc.Format("doctrine.ui.abandon_doctrine_tip_fmt", Tr(branch.Name), Fmt.Number(cost), _campaign.Defines.DoctrineRetrainingDays)
                    : Loc.Format("doctrine.ui.abandon_doctrine_free_tip_fmt", Tr(branch.Name), _campaign.Defines.DoctrineRetrainingDays));
        StyleDoctrineDangerButton(button, armed);
    }

    private Button CreateAbandonDoctrineButton(Nation n, DoctrineBranch branch, string confirmKey, string nodeName, bool compact)
    {
        TacticalDoctrines.IsHeld(n, branch.Id, out var established);
        var id = branch.Id;
        var button = ActionButton("", () => HandleAbandonDoctrineClick(id, confirmKey), 0, compact ? 28 : 32, false);
        button.Name = nodeName;
        button.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        ConfigureAbandonDoctrineButton(button, n, branch, established, confirmKey, compact);
        return button;
    }

    private void HandleAbandonDoctrineClick(string branchId, string confirmKey)
    {
        if (ObserverUiRules.IsObserving(_campaign, _observerMode) || _campaign?.Player is not { } player) return;
        if (TacticalDoctrines.AbandonRefusal(_campaign, player, branchId).Length > 0)
        {
            ResetDoctrineAbandonConfirm();
            RedrawDoctrinePage();
            return;
        }
        if (!string.Equals(_doctrineAbandonConfirmKey, confirmKey, StringComparison.Ordinal))
        {
            _doctrineAbandonConfirmKey = confirmKey;
            RedrawDoctrinePage();
            return;
        }
        var command = new AbandonDoctrineCommand { IssuerId = player.Id, BranchId = branchId };
        _doctrineStructure = "";
        ApplyDoctrineCommand(command);
        if (command.Refusal.Length > 0)
            DiagnosticLogs.Write("orders", "DOCTRINE_ABANDON_REFUSED branch=" + branchId + " reason=" + command.Refusal);
    }

    private void AddDoctrineCancelButton(HBoxContainer buttons, string branchId, string nodeId)
    {
        var cancel = ActionButton("", () => CancelDoctrineStudy(branchId), 0, 28, false);
        cancel.Name = "CancelStudy_" + nodeId;
        cancel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        cancel.ClipText = true;
        cancel.Visible = false;
        buttons.AddChild(cancel);
    }

    private void RefreshDoctrineCardButtons(Control row, Nation n, DoctrineBranch branch, DoctrineNode node, bool known, bool learning)
    {
        if (row is null) return;
        var study = row.GetNodeOrNull<Button>("StudyArticle_" + node.Id);
        if (study is not null)
            DoctrineButton(study, n, branch, node, false, known, learning, Loc.Format("Study · {0}", Fmt.Number(node.Cost)));
        var rush = row.GetNodeOrNull<Button>("RushArticle_" + node.Id);
        if (rush is not null)
        {
            rush.Visible = !learning;
            if (!learning)
                DoctrineButton(rush, n, branch, node, true, known, false, Loc.Format("Rush · {0}", Fmt.Number(node.RushCost)));
        }
        var cancel = row.GetNodeOrNull<Button>("CancelStudy_" + node.Id);
        if (cancel is not null)
        {
            cancel.Visible = learning;
            if (learning) ConfigureCancelStudyButton(cancel, n, branch.Id);
        }
    }

    private void ConfigureCancelStudyButton(Button button, Nation n, string branchId)
    {
        var observing = ObserverUiRules.IsObserving(_campaign, _observerMode);
        var refusal = observing ? Tr("obs.ui.action.readonly") : TacticalDoctrines.CancelStudyRefusal(_campaign, n, branchId);
        button.Text = Loc.Get("doctrine.ui.cancel_study_btn");
        button.Disabled = observing || refusal.Length > 0;
        button.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Loc.Get("doctrine.ui.cancel_study_tip"));
        StyleDoctrineDangerButton(button, armed: false);
    }

    private void CancelDoctrineStudy(string branchId)
    {
        if (ObserverUiRules.IsObserving(_campaign, _observerMode) || _campaign?.Player is not { } player) return;
        var command = new CancelDoctrineStudyCommand { IssuerId = player.Id, BranchId = branchId };
        ApplyDoctrineCommand(command);
        if (command.Refusal.Length > 0)
            DiagnosticLogs.Write("orders", "DOCTRINE_CANCEL_STUDY_REFUSED branch=" + branchId + " reason=" + command.Refusal);
    }

    private Button CreateAbandonStrategyButton(Nation n, string confirmKey, string nodeName, bool compact)
    {
        var observing = ObserverUiRules.IsObserving(_campaign, _observerMode);
        var refusal = observing ? Tr("obs.ui.action.readonly") : StrategicDoctrines.AbandonRefusal(_campaign, n);
        var armed = !observing && refusal.Length == 0 && string.Equals(_doctrineAbandonConfirmKey, confirmKey, StringComparison.Ordinal);
        var button = ActionButton(
            armed ? Loc.Get("doctrine.ui.confirm_abandon_free") : Loc.Get("doctrine.ui.abandon_course_btn"),
            () => HandleAbandonStrategyClick(confirmKey), 0, compact ? 28 : 32, false);
        button.Name = nodeName;
        button.Disabled = observing || refusal.Length > 0;
        button.TooltipText = Ui.Tip(refusal.Length > 0 ? refusal : Loc.Get("doctrine.ui.abandon_course_tip"));
        StyleDoctrineDangerButton(button, armed);
        return button;
    }

    private void HandleAbandonStrategyClick(string confirmKey)
    {
        if (ObserverUiRules.IsObserving(_campaign, _observerMode) || _campaign?.Player is not { } player) return;
        if (StrategicDoctrines.AbandonRefusal(_campaign, player).Length > 0)
        {
            ResetDoctrineAbandonConfirm();
            RedrawDoctrinePage();
            return;
        }
        if (!string.Equals(_doctrineAbandonConfirmKey, confirmKey, StringComparison.Ordinal))
        {
            _doctrineAbandonConfirmKey = confirmKey;
            RedrawDoctrinePage();
            return;
        }
        var command = new AbandonStrategicDoctrineCommand { IssuerId = player.Id };
        ApplyDoctrineCommand(command);
        if (command.Refusal.Length > 0)
            DiagnosticLogs.Write("orders", "STRATEGY_ABANDON_REFUSED reason=" + command.Refusal);
    }

    private void AddStrategyCardActionRow(Nation n, StrategicDoctrine course, bool inForce, string refusal, VBoxContainer body)
    {
        var button = CreateAdoptStrategyButton(n, course, inForce, refusal, compact: false);
        if (!inForce)
        {
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            body.AddChild(button);
            return;
        }
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(button);
        var abandon = CreateAbandonStrategyButton(n, "page_strategy:" + course.Id, "AbandonStrategy_" + course.Id, compact: false);
        abandon.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(abandon);
        body.AddChild(row);
    }

    private void AddGrandStrategyAbandonRow(Nation n, VBoxContainer col, string confirmKey, string nodeName)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var abandon = CreateAbandonStrategyButton(n, confirmKey, nodeName, compact: true);
        abandon.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        row.AddChild(abandon);
        col.AddChild(row);
    }

    private void AddPreviewBookHeader(VBoxContainer rows, Nation n, DoctrineBranch book, DoctrineTrack track)
    {
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var lbl = Title(Loc.Format("{0} · {1}/{2}", Tr(book.Name), track.Known.Count, book.Nodes.Count),
            13, Ui.Colour("text.accent"));
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(lbl);
        row.AddChild(CreateAbandonDoctrineButton(n, book, "preview:" + book.Id, "PreviewAbandon_" + book.Id, compact: true));
        rows.AddChild(row);
    }

    private void AddPreviewStudyRow(VBoxContainer rows, Nation n, string branchId, string nodeId, int doneDay, bool accent)
    {
        var studying = TacticalDoctrineCatalog.Find(branchId)?.Find(nodeId);
        var text = Loc.Format("{0}: {1} days left.", Tr(studying?.Name ?? nodeId), Math.Max(0, doneDay - _campaign.ElapsedDays));
        var row = new HBoxContainer();
        row.AddThemeConstantOverride("separation", 6);
        var lbl = Title(text, 12, Ui.Colour(accent ? "text.accent" : "text.body"));
        lbl.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        lbl.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        row.AddChild(lbl);
        var cancel = ActionButton("", () => CancelDoctrineStudy(branchId), 0, 26, false);
        cancel.Name = "PreviewCancelStudy_" + branchId;
        cancel.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        ConfigureCancelStudyButton(cancel, n, branchId);
        row.AddChild(cancel);
        rows.AddChild(row);
    }
}
