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
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthBottom = 1,
            BorderWidthTop = 2, ShadowColor = new Color(0, 0, 0, 0.28f),
            ShadowSize = 5, ShadowOffset = new Vector2(0, 3)
        };
        style.SetContentMarginAll(1);
        return style;
    }

    private static StyleBoxFlat ShellRowStyle(bool alternate)
    {
        var style = new StyleBoxFlat
        {
            BgColor = Ui.Colour(alternate ? "shell.row_alternate" : "shell.row"),
            ContentMarginLeft = InterfaceSkin.Metric("shell.row_padding", 9), ContentMarginRight = InterfaceSkin.Metric("shell.row_padding", 9),
            ContentMarginTop = 3, ContentMarginBottom = 3
        };
        return style;
    }

    private void ToggleCampaignScreen(string screen)
    {
        var open = screen switch
        {
            "Situation" => _situationLayer?.Visible == true,
            "State Affairs" => _stateAffairsLayer?.Visible == true,
            "Ledger" => _ledgerLayer?.Visible == true,
            "Wars" => _warLedgerLayer?.Visible == true,
            "Diplomacy" => _diplomacyLayer?.Visible == true,
            "Laws" => _policyLayer?.Visible == true,
            "Trade" => _tradeScreenLayer?.Visible == true,
            "Production" => _productionLayer?.Visible == true,
            "Support" => _supportLayer?.Visible == true,
            "Research" => _researchLayer?.Visible == true,
            "Conflicts" => _conflictsLayer?.Visible == true,
            "Economy" when _sovereignFinanceLayer?.Visible == true => true,
            _ => _sidePanel?.Visible == true && _openScreen == screen
        };
        if (!open)
        {
            if (screen == "Nation") _selectedNation = _campaign.Player;
            OpenScreen(screen);
            return;
        }
        if (screen == "Situation") CloseSituationRoom();
        else if (screen == "State Affairs") CloseStateAffairs();
        else if (screen == "Ledger") _ledgerLayer.Visible = false;
        else if (screen == "Wars") _warLedgerLayer.Visible = false;
        else if (screen == "Diplomacy") CloseDiplomacyScreen();
        else if (screen == "Laws") ClosePolicyWorkspace();
        else if (screen == "Trade") CloseTradeScreen();
        else if (screen == "Production") CloseProductionScreen();
        else if (screen == "Support") CloseSupportScreen();
        else if (screen == "Research") CloseResearchScreen();
        else if (screen == "Conflicts") CloseConflictsScreen();
        else if (screen == "Economy" && _sovereignFinanceLayer?.Visible == true) CloseSovereignFinance();
        else CloseSidePanel();
        UpdateScreenButtons();
    }

    private void LayoutCampaignShell()
    {
        if (_hudLayer is null) return;
        LayoutHudReadability();
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
            button.ClipText = true;
            // Five policy tabs share a narrow dossier; keep Russian labels readable.
            if (screen == "Laws") button.AddThemeFontSizeOverride("font_size", 12);
            button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
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
