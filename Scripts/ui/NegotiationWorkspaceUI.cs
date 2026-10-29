using System;
using System.Collections.Generic;
using System.Linq;
using Godot;

namespace DealOrForce;

/// <summary>Presentation only: draft pages, a persistent settlement total and action discovery.</summary>
public partial class DealOrForceGame
{
    private PanelContainer _peaceWorkspacePanel;
    private VBoxContainer _peaceTerritoryPage, _peaceTreatyPage;
    private Button _peaceTerritoryTab, _peaceTreatyTab;
    private Label _peaceLeverageValue, _peaceDemandValue;
    private LineEdit _diplomacyActionSearch;
    private Label _diplomacyActionMatches, _diplomacyActionEmpty, _diplomacyCountryEmpty;
    private readonly List<(VBoxContainer Wrapper, Control Content)> _diplomacyActionBlocks = new();
    private readonly Dictionary<Button, Control> _diplomacyActionSlots = new();
    private Control _diplomacyCountryColumn;

    private void BuildPeaceWorkspaceTabs(VBoxContainer shell)
    {
        var tabs = new HBoxContainer { Name = "PeaceWorkspaceTabs" };
        tabs.AddThemeConstantOverride("separation", 6); shell.AddChild(tabs);
        _peaceTerritoryTab = ActionButton("Territory", () => SelectPeaceWorkspacePage(false), 0, 34);
        _peaceTerritoryTab.Name = "PeaceTerritoryTab";
        _peaceTerritoryTab.SetMeta("atlas_tab_button", true);
        _peaceTreatyTab = ActionButton("Treaty terms", () => SelectPeaceWorkspacePage(true), 0, 34);
        _peaceTreatyTab.Name = "PeaceTreatyTab";
        _peaceTreatyTab.SetMeta("atlas_tab_button", true);
        foreach (var button in new[] { _peaceTerritoryTab, _peaceTreatyTab })
        { button.SizeFlagsHorizontal = SizeFlags.ExpandFill; tabs.AddChild(button); }
        StyleButton(_peaceTerritoryTab, true); StyleButton(_peaceTreatyTab, false);
    }

    private void SelectPeaceWorkspacePage(bool treaty)
    {
        if (_peaceTerritoryPage is null || _peaceTreatyPage is null) return;
        _peaceTerritoryPage.Visible = !treaty; _peaceTreatyPage.Visible = treaty;
        StyleButton(_peaceTerritoryTab, !treaty); StyleButton(_peaceTreatyTab, treaty);
        if (_peaceLayer.FindChild("PeaceBodyScroll", true, false) is ScrollContainer scroll)
            scroll.ScrollVertical = 0;
    }

    private void BuildPeaceSettlementFooter(VBoxContainer shell)
    {
        var footer = new PanelContainer { Name = "ConferenceFooter" };
        footer.AddThemeStyleboxOverride("panel", WarInsetStyle(10, 10));
        AtlasSurfaceSkin.Attach(footer, AtlasSurfaceSkin.Level.Inset, "war.card");
        shell.AddChild(footer);
        var rows = new VBoxContainer(); rows.AddThemeConstantOverride("separation", 8); footer.AddChild(rows);
        var figures = new HBoxContainer(); figures.AddThemeConstantOverride("separation", 16); rows.AddChild(figures);
        var leverage = NumberCell("War leverage", "—"); leverage.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        var demand = NumberCell("Settlement cost", "—"); demand.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        figures.AddChild(leverage); figures.AddChild(demand);
        _peaceLeverageValue = leverage.GetNode<Label>("FigureValue");
        _peaceDemandValue = demand.GetNode<Label>("FigureValue");
        _peaceBudget = Title("", 12, Ui.Colour("text.muted"));
        _peaceBudget.Name = "PeaceBudgetSummary";
        rows.AddChild(_peaceBudget);
        _peaceVerdict = Title("", 13, Ui.Colour("state.warning"));
        _peaceVerdict.Name = "PeaceSigningStatus";
        _peaceVerdict.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_peaceVerdict);
        var actions = new HBoxContainer(); actions.AddThemeConstantOverride("separation", 6); rows.AddChild(actions);
        _conferenceWhitePeaceButton = ActionButton("White peace", ProposeConferenceWhitePeace, 0, 38);
        _conferenceWhitePeaceButton.Name = "ConferenceWhitePeace";
        _conferenceProposeButton = ActionButton("Propose", ProposeWrittenPeace, 0, 38, true);
        _conferenceProposeButton.Name = "ConferencePropose";
        foreach (var button in new[] { _conferenceWhitePeaceButton, _conferenceProposeButton })
        { button.Icon = null; button.SizeFlagsHorizontal = SizeFlags.ExpandFill; actions.AddChild(button); }
    }

    private void BuildDiplomacyActionDiscovery(VBoxContainer column)
    {
        SectionRule(column, "Diplomatic actions");
        _diplomacyActionSearch = Placeholder(new LineEdit { Name = "DiplomacyActionSearch",
            ClearButtonEnabled = true, CustomMinimumSize = new Vector2(0, 32) }, "Find an action…");
        _diplomacyActionSearch.TooltipText = Ui.Tip(Tr("Search action names. Unavailable actions keep their explanations."));
        column.AddChild(_diplomacyActionSearch);
        _diplomacyActionMatches = Title("", 11, Ui.Colour("text.muted"));
        column.AddChild(_diplomacyActionMatches);
        _diplomacyActionSearch.TextChanged += _ =>
        {
            FilterDiplomacyActions();
            if (_diplomacyLayer.FindChild("WideDiplomaticActionScroll", true, false) is ScrollContainer scroll)
                scroll.ScrollVertical = 0;
        };
    }

    private void WrapDiplomacyActionBlocks()
    {
        // Wrappers, not the rule-controlled buttons, own search visibility. Refreshing a
        // request/war/subject can still change its real availability while a search is open.
        foreach (var content in _diplomacyActionRows.GetChildren().OfType<Control>().ToArray())
        {
            _diplomacyActionRows.RemoveChild(content);
            var wrapper = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
            _diplomacyActionRows.AddChild(wrapper); wrapper.AddChild(content);
            _diplomacyActionBlocks.Add((wrapper, content));
            if (content is Button single) _diplomacyActionSlots[single] = wrapper;
            else foreach (var nested in content.FindChildren("*", "Button", true, false).OfType<Button>())
            {
                var parent = nested.GetParent(); var index = nested.GetIndex();
                parent.RemoveChild(nested);
                var slot = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
                slot.SetMeta("diplomacy_search_slot", true);
                parent.AddChild(slot); parent.MoveChild(slot, index); slot.AddChild(nested);
                _diplomacyActionSlots[nested] = slot;
            }
        }
        _diplomacyActionEmpty = Title("No diplomatic actions match. Clear the search to see all actions.", 13, Ui.Colour("text.muted"));
        _diplomacyActionEmpty.Name = "DiplomacyActionEmpty";
        _diplomacyActionEmpty.Visible = false;
        _diplomacyActionRows.AddChild(_diplomacyActionEmpty);
    }

    private void FilterDiplomacyActions()
    {
        if (_diplomacyActionSearch is null) return;
        var query = _diplomacyActionSearch.Text.Trim();
        var shown = 0; var available = 0;
        VBoxContainer heading = null;
        var sectionHasMatch = false;
        foreach (var (wrapper, content) in _diplomacyActionBlocks)
        {
            if (content is Label)
            {
                if (heading is not null) heading.Visible = query.Length == 0 || sectionHasMatch;
                heading = wrapper; sectionHasMatch = false; continue;
            }
            var buttons = content is Button button ? new[] { button }
                : content.FindChildren("*", "Button", true, false).OfType<Button>().ToArray();
            bool Allowed(Button candidate)
            {
                for (Node node = candidate; node is Control control; node = node.GetParent())
                {
                    if (!control.HasMeta("diplomacy_search_slot") && !control.Visible) return false;
                    if (node == content) break;
                }
                return true;
            }
            var matches = buttons.Where(Allowed).Where(b => query.Length == 0 ||
                b.Text.Contains(query, StringComparison.CurrentCultureIgnoreCase) ||
                b.HasMeta("diplomacy_search_name") && Tr(b.GetMeta("diplomacy_search_name").AsString())
                    .Contains(query, StringComparison.CurrentCultureIgnoreCase)).ToArray();
            foreach (var candidate in buttons)
                if (_diplomacyActionSlots.TryGetValue(candidate, out var slot))
                    slot.Visible = query.Length == 0 || matches.Contains(candidate);
            wrapper.Visible = query.Length == 0 || matches.Length > 0;
            sectionHasMatch |= matches.Length > 0;
            shown += matches.Length; available += matches.Count(b => !b.Disabled);
        }
        if (heading is not null) heading.Visible = query.Length == 0 || sectionHasMatch;
        _diplomacyActionEmpty.Visible = shown == 0;
        _diplomacyActionMatches.Text = Simulation.Loc.Format("{0} actions · {1} available", shown, available);
    }

    // A 1080-unit canvas must not reduce small document text to eight physical pixels.
    // Re-run only after rebuild/resize, never traverse every UI tree each frame.
    private void FitNegotiationTypography(Control root)
    {
        if (root is null) return;
        var compact = GetWindow().Size.Y < 900 || GetWindow().Size.X < 1600;
        if (root == _diplomacyLayer && _diplomacyCountryColumn is not null)
        {
            _diplomacyCountryColumn.CustomMinimumSize = new Vector2(compact ? 400 : 360, 0);
            _diplomacyActionColumn.CustomMinimumSize = new Vector2(compact ? 350 : 300, 0);
        }
        foreach (var control in root.FindChildren("*", "Control", true, false).OfType<Control>())
        {
            if (control is not (Label or Button or LineEdit)) continue;
            if (!control.HasMeta("negotiation_base_font"))
                control.SetMeta("negotiation_base_font", control.GetThemeFontSize("font_size"));
            var font = control.GetMeta("negotiation_base_font").AsInt32();
            control.AddThemeFontSizeOverride("font_size", compact && font <= 15 ? Ui.TypeStep((int)MathF.Ceiling(font * 1.4f)) : font);
            if (control is Button or LineEdit)
            {
                if (!control.HasMeta("negotiation_base_height"))
                    control.SetMeta("negotiation_base_height", control.CustomMinimumSize.Y);
                var height = control.GetMeta("negotiation_base_height").AsSingle();
                control.CustomMinimumSize = new Vector2(control.CustomMinimumSize.X, compact ? MathF.Max(height, 44) : height);
            }
        }
        if (root == _diplomacyLayer)
            foreach (var row in _diplomacyRows) FitDiplomacyName(row);
    }
}
