using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _hudTopPanel, _hudNavigationPanel;
    private VBoxContainer _hudTopStack, _hudIdentityBox;
    private HBoxContainer _hudTopRow, _hudStatRow;
    private VBoxContainer _hudClockBox;
    private Button _mediaButton = null!;
    private Button _hudFiguresButton;
    private float _hudHeaderExtra;
    private bool _hudAskLong;
    private bool _hudAskBrief;
    private bool _hudBriefActive;
    private bool _hudSecondRow;

    private void LayoutHudReadability()
    {
        ResolveHudFigures();
        PlaceHudChrome();
    }

    /// <summary>
    /// Pick the short or the long clock row from the widths just measured. Returns whether
    /// the row changed shape, so a refresh can leave the docks where they are.
    /// </summary>
    private bool ResolveHudFigures()
    {
        if (_hudTopPanel is null || _hudStatRow is null || _hudTopRow is null) return false;
        // Canvas stretching can keep Size.X at 1920 in a 1280px window. The plan uses the
        // real window as well, or the second row never appears and the long sentences clip.
        var windowWidth = GetWindow()?.Size.X ?? Size.X;
        ApplyHudFigureForm(true);
        var longWidth = MeasuredClockWidth();
        ApplyHudFigureForm(false);
        var briefWidth = MeasuredClockWidth();
        var available = _hudTopPanel.Size.X > 1 ? _hudTopPanel.Size.X : Size.X - 16;
        var plan = Simulation.ResponsiveHudLayout.Clock(
            System.Math.Min(available, windowWidth), windowWidth, longWidth, briefWidth,
            _hudAskLong, _hudAskBrief,
            Ui.Metric("hud.two_row_below", 1600),
            Ui.Metric("hud.second_row_height", 52),
            Ui.Metric("hud.brief_row_height", 32));
        // Figures that have a row of their own have the bar's whole width: they keep the long form,
        // the week's change and the half-year thread, unless the player asked for the brief one. At
        // 1280 pixels this is the difference between a plate that shows which way things are going
        // and one that only shows where they are. That holds as well when the brief form would just
        // fit one row (a 1366 or 1440 window, five cells): the plan would then shed the week and the
        // thread to save a row, and a figure promoted onto the plate later would flip the bar into
        // two rows mid-game. So whenever the long form does not fit one row, the plate takes its
        // second row up front.
        if (plan.Brief && !_hudAskBrief)
        {
            ApplyHudFigureForm(true);
            _hudStatRow.UpdateMinimumSize();
            if (_hudStatRow.GetCombinedMinimumSize().X + 24 <= available)
                plan = new Simulation.ResponsiveHudLayout.ClockPlan(false, true, Ui.Metric("hud.second_row_height", 52));
        }
        var changed = _hudBriefActive != plan.Brief || _hudSecondRow != plan.SecondRow ||
                      System.Math.Abs(_hudHeaderExtra - plan.HeaderExtra) > 0.5f;
        _hudBriefActive = plan.Brief;
        _hudSecondRow = plan.SecondRow;
        _hudHeaderExtra = plan.HeaderExtra;
        ApplyHudFigureForm(!plan.Brief);
        PaintFigureButton();
        return changed;
    }

    private void PlaceHudChrome()
    {
        if (_hudTopPanel is null || _hudStatRow is null || _hudTopRow is null) return;
        PlaceTopPlate();
        _hudTopPanel.OffsetBottom += _hudHeaderExtra;
        if (_hudSecondRow && _hudStatRow.GetParent() != _hudTopStack)
            _hudStatRow.Reparent(_hudTopStack);
        else if (!_hudSecondRow && _hudStatRow.GetParent() != _hudTopRow)
        {
            _hudStatRow.Reparent(_hudTopRow);
            var idx = _hudIdentityBox is not null ? _hudIdentityBox.GetIndex() + 1 : 2;
            _hudTopRow.MoveChild(_hudStatRow, idx);
        }
        if (_hudClockBox is not null)
            _hudClockBox.SizeFlagsHorizontal = _hudSecondRow ? SizeFlags.ExpandFill : SizeFlags.Fill;
        if (_hudIdentityBox is not null)
            _hudIdentityBox.SizeFlagsHorizontal = _hudSecondRow ? SizeFlags.ExpandFill : SizeFlags.Fill;
        foreach (var panel in new Control[] { _hudNavigationPanel, _sidePanel, _movementPanel, _armyPanel, _supportLayer is PanelContainer ? _supportLayer : null,
                     _fleetPanel, _resourceAtlasPanel, _deskPanel })
            ShiftHudDock(panel);
        LayoutCampaignDossier();
        if (_situationLayer is not null) LayoutSituationRoom();
    }

    private float MeasuredClockWidth()
    {
        _hudStatRow.UpdateMinimumSize();
        _hudTopRow.UpdateMinimumSize();
        var required = _hudStatRow.GetCombinedMinimumSize().X;
        var slots = 1;
        foreach (var child in _hudTopRow.GetChildren())
            if (child is Control control && control != _hudStatRow && control.Visible)
            { required += control.GetCombinedMinimumSize().X; slots++; }
        return required + _hudTopRow.GetThemeConstant("separation") * (slots - 1) + 24;
    }

    private void NoteFigureTips()
    {
        // The refresh just wrote the long explanation. Keep that text, not the short
        // form's tooltip from the previous pass.
        foreach (var stat in _hudStats)
        {
            if (stat?.Box is null || !GodotObject.IsInstanceValid(stat.Box)) continue;
            if (stat.Box.HasMeta("hud_tip_is_brief")) continue;
            stat.Box.SetMeta("hud_saved_tip", stat.Box.TooltipText ?? "");
        }
    }

    private void ToggleHudFigures()
    {
        // The other form. Session only: reading either one does not write the campaign.
        if (_hudBriefActive) { _hudAskLong = true; _hudAskBrief = false; }
        else { _hudAskBrief = true; _hudAskLong = false; }
        _strategicHudFingerprint = int.MinValue;
        LayoutHudReadability();
        RefreshStrategicHud();
    }

    private void PaintFigureButton()
    {
        if (_hudFiguresButton is null || !GodotObject.IsInstanceValid(_hudFiguresButton)) return;
        var key = _hudBriefActive ? "hud.figures.full" : "hud.figures.brief";
        var tip = _hudBriefActive ? "hud.figures.full.tip" : "hud.figures.brief.tip";
        _hudFiguresButton.Text = Tr(key);
        _hudFiguresButton.TooltipText = Ui.Tip(Tr(tip));
        _hudFiguresButton.SetMeta(LocSourceMeta, key);
        _hudFiguresButton.SetMeta(LocAppliedMeta, _hudFiguresButton.Text);
        _hudFiguresButton.SetMeta(LocTipMeta, tip);
        _hudFiguresButton.SetMeta(LocTipAppliedMeta, _hudFiguresButton.TooltipText);
    }

    /// <summary>
    /// Long form: caption, number, and the week's sentence. Brief form: a fixed short
    /// caption and the number. The full sentence moves to the tooltip either way.
    /// </summary>
    private void ApplyHudFigureForm(bool longForm)
    {
        foreach (var stat in _hudStats)
        {
            if (stat?.Box is null || !GodotObject.IsInstanceValid(stat.Box)) continue;
            var movement = stat.Delta?.GetParent() as Control;
            if (longForm)
            {
                if (movement is not null) movement.Visible = true;
                stat.Box.CustomMinimumSize = new Vector2(ResourceCellWidth(false), 0);
                if (stat.Value is not null && stat.Exact.Length > 0)
                    stat.Value.TooltipText = Ui.Tip(stat.Exact);
                if (stat.Delta is not null)
                    stat.Delta.TooltipText = Ui.Tip(string.IsNullOrEmpty(stat.DeltaHover) ? stat.FullDelta : stat.DeltaHover);
                if (stat.Box.HasMeta("hud_saved_tip"))
                {
                    stat.Box.TooltipText = stat.Box.GetMeta("hud_saved_tip").AsString();
                    stat.Box.RemoveMeta("hud_saved_tip");
                }
                if (stat.Box.HasMeta("hud_tip_is_brief")) stat.Box.RemoveMeta("hud_tip_is_brief");
                FitHudStat(stat);
                continue;
            }
            if (movement is not null) movement.Visible = false;
            stat.Box.CustomMinimumSize = new Vector2(ResourceCellWidth(true), 0);
            if (!stat.Box.HasMeta("hud_saved_tip"))
                stat.Box.SetMeta("hud_saved_tip", stat.Box.TooltipText ?? "");
            stat.Box.SetMeta("hud_tip_is_brief", true);
            var fullCaption = Tr(stat.CaptionKey);
            var briefKey = "hud.stat.brief." + stat.CaptionKey;
            var shortKey = "hud.stat.short." + stat.CaptionKey;
            var shown = Simulation.Loc.Has(briefKey) ? Tr(briefKey)
                : Simulation.Loc.Has(shortKey) ? Tr(shortKey) : fullCaption;
            stat.Caption.Text = shown;
            stat.Caption.AutowrapMode = TextServer.AutowrapMode.Off;
            stat.Caption.ClipText = true;
            stat.Caption.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
            stat.Caption.TooltipText = Ui.Tip(fullCaption);
            stat.Caption.MouseFilter = MouseFilterEnum.Stop;
            stat.Caption.AddThemeFontSizeOverride("font_size", 10);
            if (stat.Value is not null)
            {
                stat.Value.AutowrapMode = TextServer.AutowrapMode.Off;
                stat.Value.ClipText = true;
                stat.Value.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
                stat.Value.AddThemeFontSizeOverride("font_size", 13);
            }
            var sentence = fullCaption;
            if (stat.Exact.Length > 0) sentence += "\n" + stat.Exact;
            if (!string.IsNullOrEmpty(stat.FullDelta)) sentence += "\n" + stat.FullDelta;
            var tip = Ui.Tip(sentence);
            if (stat.Value is not null) stat.Value.TooltipText = tip;
            stat.Box.TooltipText = tip;
        }
        _hudStatRow?.UpdateMinimumSize();
        _hudTopRow?.UpdateMinimumSize();
    }

    private void ShiftHudDock(Control panel)
    {
        if (panel is null || !panel.HasMeta("skin_layout")) return;
        var key = panel.GetMeta("skin_layout").AsString();
        if (!key.StartsWith("hud.")) return;
        Ui.Place(panel, key);
        if (panel.AnchorTop == 0) panel.OffsetTop += _hudHeaderExtra;
        if (panel.AnchorBottom == 0) panel.OffsetBottom += _hudHeaderExtra;
        ClearOfRail(panel);
    }

    /// <summary>
    /// A panel docked at the left edge starts where the navigation rail really ends, not where
    /// the skin says it ends. The rail grows to fit its captions ("ГОСУДАРСТВЕННЫЕ ДЕЛА" is wider
    /// than the 90 pixels the layout gives it), and every panel authored at x = 96..106 then
    /// opened underneath it: the agenda and the division panel lost their first letters to the
    /// rail. Only the dossier and the country card used to measure the rail; now every left dock
    /// does, and moves whole, so a panel at its content minimum is not widened on the way.
    /// </summary>
    private void ClearOfRail(Control panel)
    {
        if (panel is null || _hudNavigationPanel is null || panel == _hudNavigationPanel) return;
        if (!_hudNavigationPanel.Visible || panel.AnchorLeft != 0 || panel.AnchorRight != 0) return;
        if (panel.GetParent() is not CanvasItem parent) return;
        var local = parent.GetGlobalTransformWithCanvas().AffineInverse();
        var railTransform = _hudNavigationPanel.GetGlobalTransformWithCanvas();
        var railStart = local * railTransform.Origin;
        var railEnd = local * (railTransform * _hudNavigationPanel.Size);
        // A panel that sits left of the rail on purpose (a mod moved the rail) is not ours to move.
        if (panel.Position.X < railStart.X) return;
        var gap = Mathf.Clamp(Ui.Metric("hud.dock_gap", 8), 4, 24);
        var shift = Mathf.Max(0, railEnd.X + gap - panel.Position.X);
        if (shift > 0) panel.Position += new Vector2(shift, 0);
    }

    /// <summary>Every left dock, placed again after the rail changed width.</summary>
    private void DockBesideRail()
    {
        var support = _supportLayer?.GetNodeOrNull<Control>("SupportForcesPanel");
        foreach (var panel in new Control[] { _movementPanel, _armyPanel, support, _fleetPanel, _resourceAtlasPanel,
                     _hotseatHud, _networkHud })
            ShiftHudDock(panel);
        // The desk also fits its height to its rows; its own fitter re-docks it first.
        if (_deskPanel is { Visible: true }) FitSituationDesk();
        else ShiftHudDock(_deskPanel);
        if (_deskTab is not null && _deskTab.GetParent() is not Container) ClearOfRail(_deskTab);
    }
}
