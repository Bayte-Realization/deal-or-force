using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _researchLayer;
    private Label _researchSummary;
    private Button _researchFocus;
    private ProgressBar _researchProgress;
    private HFlowContainer _researchCategories;
    private LineEdit _researchSearch;
    private ScrollContainer _researchScroll;
    private TechnologyTreeCanvas _researchTree;
    private VBoxContainer _researchDetails;
    private string _researchCategory = "industry", _researchSelected = "", _researchSignature = "";
    private readonly Dictionary<string, Vector2> _researchPositions = new(StringComparer.OrdinalIgnoreCase);

    private void BuildResearchScreen()
    {
        _researchLayer = FullLayer("Research");
        _researchLayer.Visible = false;
        _hudLayer.AddChild(_researchLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay"), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _researchLayer.AddChild(shade);
        var panel = TexturedPanel(_researchLayer);
        Ui.Place(panel, "modal.research");
        var rows = PanelRows(panel, 18, 14, 10);
        var header = new HBoxContainer();
        rows.AddChild(header);
        var title = Title("Technology tree", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        _researchFocus = ActionButton("Show current research", () => SelectResearchTechnology(_campaign.Player.CurrentResearch, true), 0, 32);
        _researchFocus.Name = "FocusCurrentResearch";
        header.AddChild(_researchFocus);
        header.AddChild(ActionButton("×", CloseResearchScreen, 38, 32));
        _researchSummary = Title("", 14, Ui.Colour("text.body"));
        _researchSummary.Name = "ResearchSummary";
        rows.AddChild(_researchSummary);
        _researchProgress = new ProgressBar { Name = "ActiveResearchProgress", CustomMinimumSize = new(0, 18), ShowPercentage = true };
        // A track and a fill that read as a bar: the engine's own grey fill on no track at all
        // was a short grey smudge with "12%" floating in the middle of the screen.
        _researchProgress.AddThemeStyleboxOverride("background", new StyleBoxFlat
        {
            BgColor = new Color("0c1720"), BorderColor = Ui.Colour("text.muted") with { A = 0.35f },
            BorderWidthLeft = 1, BorderWidthTop = 1, BorderWidthRight = 1, BorderWidthBottom = 1
        });
        _researchProgress.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour("text.accent") with { A = 0.85f } });
        _researchProgress.AddThemeColorOverride("font_color", Ui.Colour("text.body"));
        _researchProgress.AddThemeColorOverride("font_outline_color", new Color("0c1720"));
        _researchProgress.AddThemeConstantOverride("outline_size", 4);
        rows.AddChild(_researchProgress);
        _researchCategories = new HFlowContainer();
        rows.AddChild(_researchCategories);
        _researchSearch = new LineEdit { Name = "ResearchSearch", PlaceholderText = Tr("Search technologies…"), ClearButtonEnabled = true };
        _researchSearch.TextChanged += _ => { _researchSignature = ""; RefreshResearchScreen(); };
        rows.AddChild(_researchSearch);
        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 14);
        rows.AddChild(body);
        _researchScroll = new ScrollContainer { Name = "ResearchTreeScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddChild(_researchScroll);
        _researchTree = new TechnologyTreeCanvas { Name = "TechnologyGraph", MouseFilter = MouseFilterEnum.Pass };
        _researchScroll.AddChild(_researchTree);
        var detailScroll = new ScrollContainer { Name = "ResearchDetailsScroll", CustomMinimumSize = new(Math.Clamp(InterfaceSkin.Metric("research.details_width", 320), 240, 480), 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        body.AddChild(detailScroll);
        _researchDetails = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _researchDetails.AddThemeConstantOverride("separation", 10);
        detailScroll.AddChild(_researchDetails);
        rows.AddChild(Title("Select a card to inspect it. Switching research preserves invested points.", 12, Ui.Colour("text.muted")));
        rows.AddChild(Title("Future tiers represent possible development paths, not dated predictions.", 12, Ui.Colour("text.muted")));
    }

    private void OpenResearchScreen()
    {
        _openScreen = "Research";
        _sidePanel.Visible = false;
        HideMapSelection(true);
        SetArmyPanelVisible(false);
        foreach (var other in new[] { _ledgerLayer, _warLedgerLayer, _stateAffairsLayer, _diplomacyLayer, _tradeScreenLayer, _productionLayer })
            if (other is not null) other.Visible = false;
        _researchLayer.Visible = true;
        _researchLayer.MoveToFront();
        if (TechnologyCatalog.Of(_researchSelected) is null)
        {
            var initial = TechnologyCatalog.Of(_campaign.Player.CurrentResearch) ?? TechnologyCatalog.All.FirstOrDefault();
            _researchSelected = initial?.Id ?? "";
            _researchCategory = initial?.Category ?? "";
        }
        _researchSignature = "";
        RefreshResearchScreen();
        UpdateScreenButtons();
    }

    private void CloseResearchScreen()
    {
        _researchLayer.Visible = false;
        if (_openScreen == "Research") _openScreen = "";
        UpdateScreenButtons();
    }

    private void SelectResearchTechnology(string id, bool reveal = false)
    {
        var tech = TechnologyCatalog.Of(id);
        if (tech is null) return;
        _researchSelected = tech.Id;
        if (reveal)
        {
            _researchCategory = tech.Category;
            _researchSearch.Text = "";
        }
        _researchSignature = "";
        RefreshResearchScreen();
        if (reveal && _researchPositions.TryGetValue(id, out var position))
            Callable.From(() => { _researchScroll.ScrollHorizontal = (int)Math.Max(0, position.X - 20); _researchScroll.ScrollVertical = (int)Math.Max(0, position.Y - 20); }).CallDeferred();
    }

    private void RefreshResearchScreen()
    {
        if (_researchLayer is null || !_researchLayer.Visible || _campaign?.Player is null) return;
        var nation = _campaign.Player;
        var current = TechnologyCatalog.Of(nation.CurrentResearch);
        _researchFocus.Disabled = current is null;
        _researchSearch.PlaceholderText = Tr("Search technologies…");
        var pace = ResearchSystem.WeeklyPoints(_campaign, nation);
        var cost = current is null ? 0 : ResearchSystem.CostFor(_campaign, nation, current);
        var points = current is null ? 0 : nation.ResearchProgress.GetValueOrDefault(current.Id);
        _researchProgress.Value = cost > 0 ? Math.Clamp(points / cost * 100, 0, 100) : 0;
        // Nothing chosen, nothing to be a share of: an empty bar reading "0%" under "Choose a
        // research project" looked like a project stuck at the start.
        _researchProgress.Visible = current is not null;
        _researchSummary.Text = current is null
            ? Tr("Choose a research project")
            : $"{Tr(current.Name)} · {points:0.0} / {cost:0.0} · " + Simulation.Loc.Format("About {0} week(s) at this pace.", ResearchSystem.WeeksLeft(_campaign, nation, current));
        _researchSummary.Text += "  |  " + Simulation.Loc.Format("{0} points/week · {1}/{2} technologies", pace.ToString("0.0"), nation.Technologies.Count(id => TechnologyCatalog.Of(id) is not null), TechnologyCatalog.All.Count);
        // Selection does not reset scrolling; rebuilding is limited to meaningful changes.
        var signature = $"{nation.Id}|{TechnologyCatalog.Revision}|{_campaign.ElapsedDays}|{nation.CurrentResearch}|{nation.Technologies.Count}|{points}|{pace}|{_researchCategory}|{_researchSelected}|{_researchSearch.Text}|{Tr("Technology tree")}";
        if (signature == _researchSignature) return;
        _researchSignature = signature;
        ClearRuntimeChildren(_researchCategories);
        foreach (var category in new[] { "" }.Concat(TechnologyCatalog.All.Select(t => t.Category).Distinct(StringComparer.OrdinalIgnoreCase)))
        {
            var captured = category;
            var button = ActionButton(category.Length == 0 ? "All technologies" : "research.category." + category,
                () => { _researchCategory = captured; _researchSignature = ""; _researchScroll.ScrollHorizontal = 0; _researchScroll.ScrollVertical = 0; RefreshResearchScreen(); }, 0, 32, category == _researchCategory);
            // Custom categories without a translation remain readable rather than showing a key.
            if (category.Length > 0 && Tr("research.category." + category) == "research.category." + category) button.Text = category;
            button.Name = "ResearchCategory_" + (category.Length == 0 ? "all" : category);
            _researchCategories.AddChild(button);
        }
        ClearRuntimeChildren(_researchTree);
        _researchTree.Edges.Clear();
        _researchPositions.Clear();
        var width = Math.Clamp(InterfaceSkin.Metric("research.card_width", 240), 180, 400);
        var height = Math.Clamp(InterfaceSkin.Metric("research.card_height", 128), 100, 220);
        var gapX = Math.Clamp(InterfaceSkin.Metric("research.column_gap", 64), 30, 180);
        var gapY = Math.Clamp(InterfaceSkin.Metric("research.row_gap", 28), 16, 100);
        var query = _researchSearch.Text.Trim();
        var nodes = TechnologyTreeLayout.Build(TechnologyCatalog.All, _researchCategory)
            .Where(n => query.Length == 0 || Tr(TechnologyCatalog.Of(n.Id).Name).Contains(query, StringComparison.OrdinalIgnoreCase) || n.Id.Contains(query, StringComparison.OrdinalIgnoreCase)).ToArray();
        var extent = new Vector2(320, 160);
        foreach (var node in nodes)
        {
            var tech = TechnologyCatalog.Of(node.Id);
            var known = nation.HasTechnology(tech.Id);
            var active = tech.Id == nation.CurrentResearch;
            var available = ResearchSystem.Available(nation, tech);
            var colour = Ui.Colour(known ? "state.positive" : active ? "state.info" : available ? "text.accent" : "text.muted");
            var state = Tr(known ? "Researched" : active ? "Researching" : available ? "Available" : "Prerequisites missing");
            var invested = nation.ResearchProgress.GetValueOrDefault(tech.Id);
            var total = ResearchSystem.CostFor(_campaign, nation, tech);
            var caption = Tr(tech.Name) + "\n" + state + (known ? "" : $"\n{invested:0.#} / {total:0.#}");
            var button = ActionButton(caption, () => SelectResearchTechnology(tech.Id), 0, 0, tech.Id == _researchSelected);
            button.Name = "Technology_" + tech.Id;
            button.Position = new(16 + node.Column * (width + gapX), 16 + node.Row * (height + gapY));
            button.ClipText = true;
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            button.AddThemeColorOverride("font_color", colour);
            button.AddThemeFontSizeOverride("font_size", (int)Math.Clamp(InterfaceSkin.Metric("research.card_font_size", 14), 11, 20));
            button.Icon = Art(tech.Icon, "res://game/gfx/interface/icons/icon_research.png");
            button.ExpandIcon = true;
            button.AddThemeConstantOverride("icon_max_width", 24);
            button.TooltipText = Ui.Tip(TechnologyTooltip(tech, known ? -1 : ResearchSystem.WeeksLeft(_campaign, nation, tech)));
            // Assign after wrapping, icon and font: otherwise the original unwrapped
            // caption's minimum width can permanently enlarge a manually positioned card.
            button.Size = new(width, height);
            _researchTree.AddChild(button);
            // Godot recomputes the shaped, wrapped text minimum after entering the tree.
            // Reset that first provisional size once those deferred notifications finish.
            button.SetDeferred(Control.PropertyName.Size, new Vector2(width, height));
            var bar = new ProgressBar { MouseFilter = MouseFilterEnum.Ignore, ShowPercentage = false, Position = new(8, height - 9), Size = new(width - 16, 4), Value = known ? 100 : Math.Clamp(invested / Math.Max(1, total) * 100, 0, 100) };
            button.AddChild(bar);
            _researchPositions[tech.Id] = button.Position;
            extent.X = Math.Max(extent.X, button.Position.X + width + 16);
            extent.Y = Math.Max(extent.Y, button.Position.Y + height + 16);
        }
        foreach (var node in nodes)
            foreach (var requirement in TechnologyCatalog.Of(node.Id).Requires)
                if (_researchPositions.TryGetValue(requirement, out var from))
                    _researchTree.Edges.Add((from + new Vector2(width, height / 2), _researchPositions[node.Id] + new Vector2(0, height / 2), Ui.Colour(nation.HasTechnology(requirement) ? "state.positive" : "surface.line")));
        _researchTree.CustomMinimumSize = extent;
        _researchTree.QueueRedraw();
        if (nodes.Length == 0) _researchTree.AddChild(Title("No matching technologies", 16, Ui.Colour("text.muted")));
        RebuildResearchDetails();
    }

    private void RebuildResearchDetails()
    {
        ClearRuntimeChildren(_researchDetails);
        var tech = TechnologyCatalog.Of(_researchSelected);
        if (tech is null) { _researchDetails.AddChild(Title("Select a technology", 18, Ui.Colour("text.muted"))); return; }
        var nation = _campaign.Player;
        _researchDetails.AddChild(Title(tech.Name, 21, Ui.Colour("text.accent")));
        _researchDetails.AddChild(Title(tech.Description, 14, Ui.Colour("text.body")));
        _researchDetails.AddChild(Title("Effects", 13, Ui.Colour("text.caption")));
        foreach (var effect in NationalModifiers.Lines(tech.Effects))
            _researchDetails.AddChild(Title(effect, 14, Ui.Colour("text.body")));
        var cost = ResearchSystem.CostFor(_campaign, nation, tech);
        if (!nation.HasTechnology(tech.Id))
            _researchDetails.AddChild(Title(ResearchDiffusionText(tech), 13, Ui.Colour("text.muted")));
        if (!nation.HasTechnology(tech.Id)) _researchDetails.AddChild(Title(Simulation.Loc.Format("Invested: {0} / {1} points", nation.ResearchProgress.GetValueOrDefault(tech.Id).ToString("0.0"), cost.ToString("0.0")), 14, Ui.Colour("text.body")));
        if (!nation.HasTechnology(tech.Id)) _researchDetails.AddChild(Title(Simulation.Loc.Format("About {0} week(s) at this pace.", ResearchSystem.WeeksLeft(_campaign, nation, tech)), 13, Ui.Colour("text.muted")));
        var start = ActionButton(nation.HasTechnology(tech.Id) ? "Researched" : tech.Id == nation.CurrentResearch ? "Researching" : "Start research", () => ChooseResearch(tech.Id), 0, 40, true);
        start.Name = "StartResearch";
        start.Disabled = !ResearchSystem.Available(nation, tech) || tech.Id == nation.CurrentResearch;
        _researchDetails.AddChild(start);
        if (!nation.HasTechnology(tech.Id) && !ResearchSystem.Available(nation, tech)) _researchDetails.AddChild(Title("Complete every prerequisite first.", 13, Ui.Colour("state.warning")));
        _researchDetails.AddChild(Title("Prerequisites", 13, Ui.Colour("text.caption")));
        if (tech.Requires.Count == 0) _researchDetails.AddChild(Title("None", 13, Ui.Colour("text.muted")));
        foreach (var id in tech.Requires)
        {
            var parent = TechnologyCatalog.Of(id);
            var button = ActionButton((nation.HasTechnology(id) ? "✓ " : "→ ") + Tr(parent?.Name ?? id), () => SelectResearchTechnology(id, true), 0, 34);
            button.Name = "Prerequisite_" + id;
            button.Disabled = parent is null;
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _researchDetails.AddChild(button);
        }
        _researchDetails.AddChild(Title("Leads to", 13, Ui.Colour("text.caption")));
        foreach (var child in TechnologyCatalog.All.Where(t => t.Requires.Contains(tech.Id, StringComparer.OrdinalIgnoreCase)))
        {
            var button = ActionButton(Tr(child.Name), () => SelectResearchTechnology(child.Id, true), 0, 34);
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _researchDetails.AddChild(button);
        }
    }
}
