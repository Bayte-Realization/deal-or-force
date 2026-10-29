using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private HBoxContainer _dossierTabs;
    private ScrollContainer _dossierScroll;
    private string _dossierScreen = "";
    private string _dossierTab = "Overview";
    private string _detailGroup = "";
    private readonly Dictionary<string, Button> _dossierTabButtons = new();

    // Shared, restrained surfaces. Unlike the old ornamental nine-slices their visible
    // boundary and their layout boundary are the same rectangle, even on a small display.
    private static StyleBoxFlat ShellPanelStyle()
    {
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.background"), BorderColor = Ui.Colour("shell.border"),
            ShadowColor = new Color(0, 0, 0, 0.24f),
            ShadowSize = 4, ShadowOffset = new Vector2(0, 2)
        };
        style.SetBorderWidthAll(1);
        style.SetContentMarginAll(1);
        style.SetCornerRadiusAll(4);
        return style;
    }

    /// <summary>
    /// The edge alone: the same line the shell draws, with nothing behind it, for a window whose
    /// surface is already painted by artwork. Screens outside the campaign use the old panel
    /// textures, which are a fine surface and a very faint frame - on their own the country picker,
    /// the workshop and the settings read as flat dark rectangles with no edge at all. Drawing this
    /// over the artwork gives them the line the game's own panels have, without replacing the art
    /// the screens were painted with (DealOrForceGame.TexturedPanel).
    /// </summary>
    private static StyleBoxFlat ShellEdgeStyle()
    {
        var style = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = Ui.Colour("shell.border"),
            ShadowColor = new Color(0, 0, 0, 0.18f), ShadowSize = 3, ShadowOffset = new Vector2(0, 2)
        };
        style.SetBorderWidthAll(1);
        style.SetCornerRadiusAll(4);
        return style;
    }

    private static StyleBoxFlat ShellRowStyle(bool alternate, bool readOnly = false)
    {
        var accented = alternate && !readOnly;
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour(alternate ? "shell.row_alternate" : "shell.row"),
            BorderColor = Ui.Colour(accented ? "button.primary_border" : "surface.line_faint", accented ? 0.72f : 0.45f),
            ContentMarginLeft = InterfaceSkin.Metric("shell.row_padding", 9),
            ContentMarginRight = InterfaceSkin.Metric("shell.row_padding", 9),
            ContentMarginTop = 4, ContentMarginBottom = 4
        };
        style.BorderWidthBottom = 1;
        if (accented) style.BorderWidthLeft = 1;
        style.SetCornerRadiusAll(3);
        return style;
    }

    /// <summary>
    /// Which campaign screen is up, and how to put it away - answered in one place.
    ///
    /// This used to be three parallel lists that had to agree by hand: a switch answering
    /// "is it open", a chain of "else if" answering "how do I close it", and a third naming the
    /// workspaces Escape may close. A screen added to one and not the others is exactly how a
    /// window stopped answering the key that closes it, so the three are now one table.
    /// </summary>
    private (bool Open, System.Action Close) CampaignScreen(string screen) => screen switch
    {
        "Situation"     => (_situationLayer?.Visible == true, CloseSituationRoom),
        "State Affairs" => (_stateAffairsLayer?.Visible == true, CloseStateAffairs),
        "Ledger"        => (_ledgerLayer?.Visible == true, () => _ledgerLayer.Visible = false),
        "Wars"          => (_warLedgerLayer?.Visible == true, () => _warLedgerLayer.Visible = false),
        "Diplomacy"     => (_diplomacyLayer?.Visible == true, CloseDiplomacyScreen),
        "Laws"          => (_policyLayer?.Visible == true, ClosePolicyWorkspace),
        "Doctrines"     => (_doctrineLayer?.Visible == true, CloseDoctrineScreen),
        "Trade"         => (_tradeScreenLayer?.Visible == true, CloseTradeScreen),
        "Production"    => (_productionLayer?.Visible == true, CloseProductionScreen),
        "Support"       => (_supportLayer?.Visible == true, CloseSupportScreen),
        "Research"      => (_researchLayer?.Visible == true, CloseResearchScreen),
        "Conflicts"     => (_conflictsLayer?.Visible == true, CloseConflictsScreen),
        // An Economy that is only the sovereign-finance window; when that is closed, Economy is
        // an ordinary side panel and falls through to the last case with it.
        "Economy" when _sovereignFinanceLayer?.Visible == true => (true, CloseSovereignFinance),
        // Everything without a layer of its own - the country card, the region dossier - shares
        // the one side panel.
        _ => (_sidePanel?.Visible == true && _openScreen == screen, CloseSidePanel),
    };

    private void ToggleCampaignScreen(string screen)
    {
        var (open, close) = CampaignScreen(screen);
        if (!open)
        {
            if (screen == "Nation") _selectedNation = _campaign.Player;
            OpenScreen(screen);
            return;
        }
        close();
        UpdateScreenButtons();
    }

    /// <summary>
    /// The workspaces that cover the navigation which opened them, in the order they are drawn
    /// over each other. A full-screen layer swallows the clicks meant for that navigation, so
    /// without a way out a player is shut inside the window they came to look at - and every
    /// other panel in the game (the province card, the diplomacy menu, the situation room, the
    /// media library) already answers Escape. These did not. The situation room and the province
    /// card answer it themselves and are deliberately not listed here.
    /// </summary>
    private static readonly string[] WorkspaceScreens =
    {
        "State Affairs", "Ledger", "Wars", "Diplomacy", "Laws", "Doctrines",
        "Trade", "Production", "Support", "Research", "Conflicts",
    };

    /// <summary>Close whatever workspace is up. True if there was one to close.</summary>
    internal bool CloseVisibleCampaignScreen()
    {
        foreach (var screen in WorkspaceScreens)
            if (CampaignScreen(screen).Open)
            {
                ToggleCampaignScreen(screen);
                return true;
            }
        return false;
    }

    /// <summary>
    /// Raises one workspace layer above the map and side docks while closing the other full
    /// workspaces and keeping the left navigation rail above its backdrop shade, so the player
    /// can switch directly from one workspace to another with a single click on the rail.
    /// </summary>
    private void RaiseWorkspaceLayer(Control layer)
    {
        foreach (var other in new[]
        {
            _ledgerLayer, _warLedgerLayer, _stateAffairsLayer, _diplomacyLayer, _policyLayer,
            _doctrineLayer, _tradeScreenLayer, _productionLayer, _researchLayer, _conflictsLayer
        })
        {
            if (other is not null && other != layer) other.Visible = false;
        }
        if (layer != _supportLayer && _supportLayer is { Visible: true }) CloseSupportScreen();
        if (layer is null) return;
        layer.Visible = true;
        layer.MoveToFront();
        _hudNavigationPanel?.MoveToFront();
        if (_handbookLayer is { Visible: true }) _handbookLayer.MoveToFront();
    }

    private void LayoutCampaignShell()
    {
        if (_hudLayer is null) return;
        LayoutHudReadability();
        LayoutCampaignAlerts();
        LayoutTeletype();
        LayoutCampaignNavigation();
        LayoutMapControls();
        LayoutMapSelectionCards();
        LayoutPickerActions();
        // Baseline rectangles remain moddable in game/gui. Only the short outliner is
        // clamped to available height; it must not cover the map toolbar at 720p.
        if (_outliner is not null)
        {
            Ui.Place(_outliner, "hud.outliner");
            _outliner.OffsetTop += _hudHeaderExtra;
            _outliner.AnchorBottom = 0;
            FitOutliner();
        }
    }

    /// <summary>
    /// The situation report as tall as what it has to report, and no taller than the room
    /// there is. At a fixed height it hung a tall empty frame down the side of the map on every
    /// quiet day - three lines of news and four hundred pixels of nothing over the Gulf.
    /// </summary>
    private void FitOutliner()
    {
        FitSituationDesk();
        if (_outliner is null) return;
        var collapsed = Math.Max(InterfaceSkin.Metric("shell.outliner_collapsed", 48), _outliner.GetCombinedMinimumSize().Y);
        var room = Mathf.Clamp(Size.Y - _outliner.OffsetTop - 68, 180, InterfaceSkin.Metric("shell.outliner_height", 600));
        if (_regionCard is { Visible: true } && _outliner.Position.X + _outliner.Size.X > _regionCard.Position.X)
            room = Mathf.Min(room, Mathf.Max(collapsed, _regionCard.Position.Y - _outliner.OffsetTop - Ui.Metric("hud.dock_gap", 8)));
        var wanted = room;
        // The frame's own minimum is the header, the margins and the border - the scroll counts
        // for nothing there - and what scrolls is measured on its own.
        if (_outlinerScroll is { } scroll && scroll.GetChildCount() > 0 && scroll.GetChild(0) is Control body)
            wanted = _outliner.GetCombinedMinimumSize().Y + body.GetCombinedMinimumSize().Y + 6f;
        _outliner.OffsetBottom = _outliner.OffsetTop + (_outlinerCollapsed ? collapsed : Mathf.Clamp(wanted, collapsed, room));
    }

    private void PrepareDossierTabs(string screen)
    {
        // The way back to an army's orders belongs to the region it was asked about.
        if (screen != "Region") ForgetDossierReturn();
        if (_dossierTabs is null || _dossierScreen == screen) return;
        _dossierScreen = screen;
        var tabs = CampaignUiCatalog.For(screen).Tabs;
        _dossierTab = tabs.FirstOrDefault()?.Id ?? (screen == "Economy" ? "Budget" : "Overview");
        _dossierScroll.ScrollVertical = 0;
        foreach (var child in _dossierTabs.GetChildren())
        {
            _dossierTabs.RemoveChild(child);
            child.QueueFree();
        }
        _dossierTabButtons.Clear();
        _dossierTabs.Visible = tabs.Length > 0;
        foreach (var tab in tabs)
        {
            var captured = tab.Id;
            var button = ActionButton(tab.Label, () =>
            {
                if (screen == "Region")
                {
                    _dossierTab = captured;
                    _dossierScroll.ScrollVertical = 0;
                    if (captured == "Population") ShowRegionPopulation();
                    else if (captured == "Province") ShowProvinceOverview();
                    else ShowRegionOverview();
                    ApplyDossierTab();
                }
                else SelectDossierTab(captured);
            }, 0, InterfaceSkin.Metric("shell.tab_height", 34));
            button.SetMeta("atlas_tab_button", true);
            button.ClipText = true;
            // Five policy tabs share a narrow dossier; keep Russian labels readable.
            if (screen == "Laws") button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            StyleButton(button, false);
            Tip(button, tab.Label);
            _dossierTabButtons.Add(tab.Id, button);
            _dossierTabs.AddChild(button);
        }
    }

    private void SelectDossierTab(string tab)
    {
        _dossierTab = tab;
        _dossierScroll.ScrollVertical = 0;
        if (_dossierScreen == "Economy") ShowEconomyScreen();
        if (_dossierScreen == "Decisions") ShowDecisionsScreen();
        if (_dossierScreen == "Laws") SelectPolicyTab(tab);
        ApplyDossierTab();
    }

    private void OpenEconomyTab(string tab)
    {
        OpenScreen("Economy");
        SelectDossierTab(tab);
    }

    private void ApplyDossierTab()
    {
        RefreshSideLinks(_dossierScreen);
        foreach (var (tab, button) in _dossierTabButtons) StyleButton(button, tab == _dossierTab);
        if (_sideDetail is null) return;
        _sideDetail.Visible = _dossierScreen != "Economy" || _dossierTab is "Budget" or "Finance";
        if (_economyControls is not null)
            _economyControls.Visible = _dossierScreen == "Economy" && _dossierTab is not ("Budget" or "Finance");
        if (_economyDesk is not null)
            _economyDesk.Visible = _dossierScreen == "Economy";
        if (_economyBuildRows is not null) _economyBuildRows.Visible = _dossierTab == "Construction";
        if (_economyMarketRows is not null) _economyMarketRows.Visible = _dossierTab == "Market";
        foreach (var child in _sideDetail.GetChildren())
        {
            if (child is not Control control) continue;
            var group = child.HasMeta("dossier_group") ? child.GetMeta("dossier_group").AsString() : "";
            var groups = CampaignUiCatalog.For(_dossierScreen).Tabs.FirstOrDefault(t => t.Id == _dossierTab)?.Groups;
            control.Visible = _dossierScreen != "Nation" || groups is null || groups.Length == 0 || groups.Contains(group);
            if (child.HasMeta("dossier_field") && CampaignUiCatalog.DetailHidden(_dossierScreen, child.GetMeta("dossier_field").AsString()))
                control.Visible = false;
        }
        var order = CampaignUiCatalog.DetailOrder(_dossierScreen);
        var position = 0;
        foreach (var field in order)
            foreach (var node in _sideDetail.GetChildren().Where(n => n.HasMeta("dossier_field") && n.GetMeta("dossier_field").AsString() == field))
                _sideDetail.MoveChild(node, position++);
    }
}
