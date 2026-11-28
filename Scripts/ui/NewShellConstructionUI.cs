using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// DealOrForceGame integration partial for the standalone Sovereign Brass Construction Workspace
/// and touch-safe map-click Construction Placement overlay.
/// </summary>
public partial class DealOrForceGame
{
    internal bool IsNewShellConstructionOpen => _shell?.ConstructionWorkspace.IsVisible ?? false;

    internal bool IsAnyNewShellWorkspaceOpen() =>
        IsNewShellEconomyOpen || IsNewShellConstructionOpen || IsNewShellGrandProjectsOpen;

    internal bool CloseTopNewShellWorkspace()
    {
        if (IsNewShellGrandProjectsOpen) { CloseNewShellGrandProjects(); return true; }
        if (IsNewShellConstructionOpen) { CloseNewShellConstructionWorkspace(); return true; }
        if (IsNewShellEconomyOpen) { CloseNewShellEconomyWorkspace(); return true; }
        return false;
    }

    internal void CloseAllNewShellWorkspaces()
    {
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        if (IsNewShellConstructionOpen) CloseNewShellConstructionWorkspace();
        if (IsNewShellGrandProjectsOpen) CloseNewShellGrandProjects();
    }

    private void BindConstructionShellContextActions(ShellContext ctx)
    {
        ctx.OpenConstruction = OpenNewShellConstructionWorkspace;
        ctx.BeginConstructionPlacement = BeginConstructionPlacement;
        ctx.CancelConstruction = CancelConstruction;
        ctx.QueueConstructionInState = QueueConstructionInState;
        ctx.DemolishBuildingInState = DemolishBuildingInState;
        ctx.PreviewBuildingEffects = (st, kind, factor) => { var o = BuildingEffects(st, kind, factor); return (o.Text, o.Summary); };
        ctx.FocusState = FocusStateOnMap;
        BindRegionShellContextActions(ctx);
    }

    private void BindRegionShellContextActions(ShellContext ctx)
    {
        ctx.CommitImmediateCommand = (cmd, wakeGlobe) =>
            CommitImmediateCampaignCommand(cmd, refreshHud: true, wakeGlobe: wakeGlobe);
        ctx.OpenCapitalChooserForState = OpenCapitalChooserForShell;
        ctx.OpenRegionalLawsForState = OpenRegionalLawsForShell;
    }

    private void OpenCapitalChooserForShell(ScenarioState state)
    {
        if (state is null || _campaign?.Player is null) return;
        _selectedState = state;
        ChooseCapitalCity();
        if (_capitalCityChooser is { Visible: true } && IsInstanceValid(_capitalCityChooser))
            RaiseOverlayAboveNewShell(_capitalCityChooser);
        RefreshNewShell(force: true);
    }

    private void OpenRegionalLawsForShell(ScenarioState state)
    {
        if (state is null) return;
        _selectedState = state;
        OpenScreen("Laws");
        SelectPolicyTab("Regional");
    }

    internal void OpenNewShellConstructionWorkspace()
    {
        if (!_newShellBuilt) BuildNewShell();
        CloseVisibleCampaignScreen();
        if (IsNewShellEconomyOpen) CloseNewShellEconomyWorkspace();
        if (IsNewShellGrandProjectsOpen) CloseNewShellGrandProjects();
        HideLegacyLeftAndBottomCards();
        _shell?.WorkPanel.Close();
        _openScreen = "Construction";
        _newShellRoot?.MoveToFront();
        _shell?.ConstructionWorkspace.Open();
        SyncNewShellModalState();
        SyncNewShellLeftAndBottomDock();
        RefreshNewShell(force: true);
    }

    internal void CloseNewShellConstructionWorkspace()
    {
        _shell?.ConstructionWorkspace.Close();
        if (_openScreen == "Construction") _openScreen = "";
        SyncNewShellModalState();
        SyncNewShellLeftAndBottomDock();
    }

    internal void OpenConstructionWorkspaceOrLegacy()
    {
        if (IsNewShellActive) { OpenNewShellConstructionWorkspace(); UpdateScreenButtons(); return; }
        OpenEconomyTab("Construction");
    }

    internal bool TryOpenNewShellEconomyTab(string tab)
    {
        if (tab != "Construction" || !IsNewShellActive) return false;
        OpenNewShellConstructionWorkspace();
        UpdateScreenButtons();
        return true;
    }

    private void QueueConstructionInState(int stateMapIndex, BuildingKind kind, int count)
    {
        if (ObserverUiRules.IsObserving(_campaign, _observerMode) || _campaign?.Player is not { } player || count <= 0) return;
        var state = _campaign.StateByMapIndex(stateMapIndex);
        if (state is null) return;
        _selectedState = state;
        CommitImmediateCampaignWrite(c =>
        {
            for (var i = 0; i < count; i++)
                c.Commands.Enqueue(new QueueConstructionCommand { IssuerId = player.Id, StateMapIndex = stateMapIndex, Building = kind });
            c.Commands.ApplyAll(c);
        }, refreshHud: true, wakeGlobe: true);
        RefreshNewShell(force: true);
    }

    private void DemolishBuildingInState(int stateMapIndex, BuildingKind kind)
    {
        if (ObserverUiRules.IsObserving(_campaign, _observerMode) || _campaign?.Player is not { } player) return;
        var state = _campaign.StateByMapIndex(stateMapIndex);
        if (state is null || !state.Buildings.TryGetValue(kind, out var standing) || standing <= 0) return;
        _selectedState = state;
        CommitImmediateCampaignCommand(new DemolishBuildingCommand
        {
            IssuerId = player.Id, StateMapIndex = stateMapIndex, Building = kind
        }, refreshHud: true, wakeGlobe: true);
        RefreshNewShell(force: true);
    }

    private void FocusStateOnMap(ScenarioState state)
    {
        if (state is null) return;
        _selectedState = state;
        if (_campaign?.ProvinceMap is not { } map) return;
        for (var p = 1; p <= map.ProvinceCount; p++)
        {
            if (map.ProvinceToState[p] != state.MapIndex) continue;
            var center = map.CenterOf(p);
            _globe?.FocusOn(center.Latitude, center.Longitude, 6.2f);
            return;
        }
    }

    internal void RaiseOverlayAboveNewShell(Control overlay)
    {
        if (overlay is null || !IsInstanceValid(overlay) || _hudLayer is null) return;
        if (_newShellRoot is not null && IsInstanceValid(_newShellRoot) && overlay.GetParent() == _hudLayer)
            _hudLayer.MoveChild(overlay, _hudLayer.GetChildCount() - 1);
        else
            overlay.MoveToFront();
    }

    private (Vector2 Pos, Vector2 Size, int BtnH) ConfigureConstructionPlacementOverlay(PanelContainer panel)
    {
        TouchScrollController.EnsureInstalled(this);
        RaiseOverlayAboveNewShell(panel);
        var isTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var vp = _hudLayer?.Size is { X: >= 320f, Y: >= 240f } sz ? sz : GetViewportRect().Size;
        var sa = isTouch ? (Platform.PlatformProfile.Current?.SafeArea ?? default) : default;
        var panelW = isTouch ? MathF.Min(560f, MathF.Max(320f, vp.X - sa.Left - sa.Right - 48f)) : 620f;
        var panelH = isTouch ? MathF.Min(360f, MathF.Max(260f, vp.Y - sa.Top - sa.Bottom - 160f)) : 420f;
        var pos = new Vector2(isTouch ? MathF.Max(16f, sa.Left + 24f) : 120f, isTouch ? MathF.Max(110f, sa.Top + 104f) : 148f);
        var size = new Vector2(panelW, panelH);
        panel.Position = pos;
        panel.Size = size;
        return (pos, size, isTouch ? 42 : 36);
    }

    private string ConstructionPlacementInitialFeedback() =>
        Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? Loc.Get("shell.ws.build.place_feedback_touch")
            : "Hover over a region to inspect it before building. Click to queue one project; Escape or right-click finishes placement.";

    private string ConstructionPlacementIdlePreview() =>
        Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? Loc.Get("shell.ws.build.place_preview_touch")
            : Tr("Move the pointer over the map to compare sites. This is a regional project, not an exact building footprint.");

    private string ConstructionPlacementBuildHereLabel() =>
        Platform.PlatformProfile.Current?.IsTouchPrimary == true
            ? Loc.Get("shell.ws.build.place_tap_to_build")
            : Tr("Click to build here");

    private string ConstructionPlacementQueuedText(string stateName, string cost, string when) =>
        Loc.Format(Platform.PlatformProfile.Current?.IsTouchPrimary == true
                ? "shell.ws.build.place_queued_touch_fmt"
                : "Queued in {0}. Contract: {1}; {2}. Click another region or finish placement.",
            stateName, cost, when);
}
