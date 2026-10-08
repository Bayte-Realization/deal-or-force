using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _policyLayer;
    private PanelContainer _policyPanel;
    private VBoxContainer _policyList, _policyOptions, _policyPreview, _policyContext;
    private ScrollContainer _policyOptionsScroll;
    private LineEdit _policySearch;
    private Label _policySummary, _policyWorkspaceFeedback;
    private readonly Dictionary<string, Button> _policyTabs = new();
    private string _policyTab = "National", _policyCategory = "", _policySelected = "", _lawTarget = "";
    private int _lawRegion, _policyBuiltDay = -1;
    private Button _policyEnact;

    private bool PolicyIsAgenda => _policyTab == "Agendas";
    private string PolicyScope => _policyTab == "Regional" ? "region" : _policyTab == "Foreign" ? "foreign" : "nation";

    private void BuildPolicyWorkspace()
    {
        _policyLayer = FullLayer("PolicyWorkspace");
        _policyLayer.Visible = false;
        _hudLayer.AddChild(_policyLayer);
        var shade = new ColorRect { Color = new Color(.015f, .026f, .041f, .72f), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _policyLayer.AddChild(shade);
        _policyPanel = TexturedPanel(_policyLayer);
        _policyPanel.Name = "PolicyPanel";
        // Use the same authored surface and framing as full diplomacy, not a second skin.
        Ui.Place(_policyPanel, "modal.policies");
        var rows = PanelRows(_policyPanel, 22, 16, 9);
        var header = new HBoxContainer(); rows.AddChild(header);
        var title = Title("LAWS AND NATIONAL AGENDA", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill; header.AddChild(title);
        header.AddChild(ActionButton("My country", () => OpenScreen("Nation"), 0, 32,
            iconPath: "res://game/gfx/interface/icons/icon_nation.png"));
        header.AddChild(HandbookButton("domestic_crises"));
        var close = ActionButton("×", ClosePolicyWorkspace, 38, 32);
        close.Name = "ClosePolicyWorkspace"; close.TooltipText = Ui.Tip(Tr("Close")); header.AddChild(close);
        _policySummary = Title("", 13, Ui.Colour("text.body_soft")); rows.AddChild(_policySummary);
        var tabs = new HBoxContainer(); tabs.AddThemeConstantOverride("separation", 6); rows.AddChild(tabs);
        foreach (var tab in CampaignUiCatalog.For("Laws").Tabs)
        {
            var id = tab.Id;
            var button = ActionButton(tab.Label, () => SelectPolicyTab(id), 0, 34,
                iconPath: "res://game/gfx/interface/icons/icon_" + (id == "Course" || id == "Agendas" ? "agenda" : "laws") + ".png");
            button.Name = "PolicyTab_" + id; button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            button.AddThemeFontSizeOverride("font_size", 12);
            button.ClipText = true; button.TooltipText = Ui.Tip(Tr(tab.Label));
            tabs.AddChild(button); _policyTabs[id] = button;
        }
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14); rows.AddChild(body);
        var list = new VBoxContainer { CustomMinimumSize = new Vector2(220, 0) }; body.AddChild(list);
        _policyContext = new VBoxContainer { Name = "PolicyTarget" }; list.AddChild(_policyContext);
        _policySearch = Placeholder(new LineEdit { Name = "PolicySearch", ClearButtonEnabled = true }, "Find a law or category...");
        _policySearch.TextChanged += _ => RebuildPolicyList(); list.AddChild(_policySearch);
        _policyList = PolicyColumn(list, "PolicyCategories", false);
        var options = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1.1f };
        body.AddChild(options);
        _policyOptions = PolicyColumn(options, "PolicyOptions", true);
        _policyOptionsScroll = (ScrollContainer)_policyOptions.GetParent();
        var preview = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsStretchRatio = 1 };
        body.AddChild(preview);
        _policyPreview = PolicyColumn(preview, "PolicyPreview", true);
        _policyWorkspaceFeedback = Title("", 12, Ui.Colour("text.body_soft"));
        _policyWorkspaceFeedback.Name = "PolicyFeedback"; _policyWorkspaceFeedback.CustomMinimumSize = new Vector2(0, 22); rows.AddChild(_policyWorkspaceFeedback);
    }

    private static VBoxContainer PolicyColumn(Control parent, string name, bool expand)
    {
        var scroll = new ScrollContainer { Name = name + "Scroll", SizeFlagsVertical = SizeFlags.ExpandFill,
            SizeFlagsHorizontal = expand ? SizeFlags.ExpandFill : SizeFlags.Fill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, FollowFocus = true };
        parent.AddChild(scroll);
        var rows = new VBoxContainer { Name = name, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        rows.AddThemeConstantOverride("separation", 8); scroll.AddChild(rows); return rows;
    }

    private static void ClearPolicyRows(Control parent)
    {
        foreach (var child in parent.GetChildren()) { parent.RemoveChild(child); child.QueueFree(); }
    }

    private void ShowPoliticalPolicies()
    {
        _sidePanel.Visible = false;
        SetArmyPanelVisible(false);
        foreach (var other in new[] { _diplomacyLayer, _ledgerLayer, _warLedgerLayer, _productionLayer, _tradeScreenLayer })
            if (other is not null) other.Visible = false;
        _openScreen = "Laws";
        _policyLayer.Visible = true; _policyLayer.MoveToFront();
        _policyWorkspaceFeedback.Text = "";
        SelectPolicyTab(_policyTab);
        _policySearch.GrabFocus(); UpdateScreenButtons();
    }

    private void ClosePolicyWorkspace()
    {
        _policyLayer.Visible = false;
        if (_openScreen == "Laws") _openScreen = "";
        UpdateScreenButtons();
    }

    private void SelectPolicyTab(string tab)
    {
        if (!_policyTabs.ContainsKey(tab)) return;
        _policyTab = tab; _policyCategory = ""; _policySelected = ""; _policySearch.Text = "";
        foreach (var (id, button) in _policyTabs) StyleButton(button, id == tab);
        _policyOptionsScroll.ScrollVertical = 0;
        BuildPolicyTarget(); RebuildPolicyList(); RefreshPolicyWorkspace();
    }

    private void BuildPolicyTarget()
    {
        ClearPolicyRows(_policyContext);
        if (PolicyScope == "region")
        {
            var states = _campaign.StatesOf(_campaign.Player).OrderBy(s => s.DisplayName).ToArray();
            if (!states.Any(s => s.MapIndex == _lawRegion)) _lawRegion = states.FirstOrDefault()?.MapIndex ?? 0;
            _policyContext.AddChild(Title("Governed region", 12, Ui.Colour("text.caption")));
            var select = new OptionButton { Name = "PolicyRegion", SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
            foreach (var state in states) select.AddItem(state.DisplayName, state.MapIndex);
            if (states.Length > 0) select.Select(Array.FindIndex(states, s => s.MapIndex == _lawRegion));
            select.ItemSelected += i => { _lawRegion = select.GetItemId((int)i); ShowPolicyOptions(); };
            _policyContext.AddChild(select);
        }
        else if (PolicyScope == "foreign")
        {
            var targets = _campaign.Nations.Where(t => t.Id != _campaign.Player.Id && !_campaign.Fallen.Contains(t.Id))
                .OrderBy(t => t.DisplayName).ToArray();
            if (!targets.Any(t => t.Tag == _lawTarget)) _lawTarget = targets.FirstOrDefault()?.Tag ?? "";
            _policyContext.AddChild(Title("Country or movement", 12, Ui.Colour("text.caption")));
            var select = new OptionButton { Name = "PolicyForeignTarget", SizeFlagsHorizontal = SizeFlags.ExpandFill, FitToLongestItem = false };
            foreach (var target in targets) select.AddItem(target.DisplayName + " · " + target.Tag);
            if (targets.Length > 0) select.Select(Array.FindIndex(targets, t => t.Tag == _lawTarget));
            select.ItemSelected += i => { _lawTarget = targets[(int)i].Tag; ShowPolicyOptions(); };
            _policyContext.AddChild(select);
        }
    }

    private IEnumerable<PoliticalPolicy> WorkspacePolicies() => PolicyIsAgenda ? PoliticalPolicyCatalog.AllAgendas :
        PoliticalPolicyCatalog.AllLaws.Where(p => p.Scope == PolicyScope);

    private void RebuildPolicyList()
    {
        if (_policyLayer is not { Visible: true } || _campaign?.Player is null) return;
        ClearPolicyRows(_policyList);
        if (_policyTab == "Crises")
        {
            _policySearch.PlaceholderText = Tr("Find a governed region...");
            ShowDomesticCrisisWorkspace(); return;
        }
        _policySearch.PlaceholderText = Tr("Find a law or category...");
        if (_policyTab == "Course")
        {
            _policyList.AddChild(Title("ACCUMULATED EXPERIENCE", 13, Ui.Colour("text.caption")));
            _policyList.AddChild(Title("National convictions evolve over time. They cannot be selected like a law.", 13, Ui.Colour("text.body_soft")));
            ShowPolicyOptions(); return;
        }
        var query = _policySearch.Text.Trim();
        var groups = WorkspacePolicies().GroupBy(p => p.Category).Where(g => query.Length == 0 ||
            Tr(g.Key).Contains(query, StringComparison.OrdinalIgnoreCase) ||
            g.Any(p => Tr(p.Name).Contains(query, StringComparison.OrdinalIgnoreCase) || p.Id.Contains(query, StringComparison.OrdinalIgnoreCase))).ToArray();
        if (!groups.Any(g => g.Key == _policyCategory)) { _policyCategory = groups.FirstOrDefault()?.Key ?? ""; _policySelected = ""; }
        foreach (var group in groups)
        {
            var key = group.Key;
            var button = ActionButton(key, () => { _policyCategory = key; _policySelected = ""; RebuildPolicyList(); }, 0, 38,
                key == _policyCategory, "res://game/gfx/interface/icons/icon_" + group.First().Icon + ".png");
            button.Name = "PolicyCategory_" + group.First().Id; button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true; button.TooltipText = Ui.Tip(Tr(key)); _policyList.AddChild(button);
        }
        if (groups.Length == 0) _policyList.AddChild(Title("No matching policies", 13, Ui.Colour("text.caption")));
        ShowPolicyOptions();
    }

    private void RefreshPolicyWorkspace()
    {
        if (_policyLayer is not { Visible: true } || _campaign?.Player is null) return;
        var n = _campaign.Player;
        _policySummary.Text = n.DisplayName + " · " + Tr("Stability") + " " + Fmt.Percent(n.Stability) +
            " · " + Tr("Treasury") + " " + Fmt.Money(n.Treasury) + " · " + Tr("Enacted laws") + " " + n.Laws.Count;
        // Do not tear down focused controls on every simulation frame.
        if (_policyBuiltDay == _campaign.ElapsedDays) return;
        _policyBuiltDay = _campaign.ElapsedDays;
        if (_policyTab == "Crises") { ShowDomesticCrisisWorkspace(); return; }
        if (_policyTab == "Course")
        {
            var scroll = _policyOptionsScroll.ScrollVertical;
            ClearPolicyRows(_policyOptions);
            ShowNationalCourse(n, _policyOptions);
            _policyOptionsScroll.ScrollVertical = scroll;
        }
        RefreshPolicyPreview();
    }
}
