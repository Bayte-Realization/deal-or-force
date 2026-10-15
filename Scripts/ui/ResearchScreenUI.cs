using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _researchLayer;
    private Label _researchSummary, _researchResults, _researchPageLabel;
    private Button _researchFocus, _researchCatalogView, _researchGraphView, _researchPrevious, _researchNext;
    private ProgressBar _researchProgress;
    private LineEdit _researchSearch;
    private OptionButton _researchCategoryPicker, _researchStatusPicker;
    private ScrollContainer _researchScroll, _researchListScroll, _researchDetailScroll;
    private TechnologyTreeCanvas _researchTree;
    private VBoxContainer _researchList, _researchDetails;
    private Label _researchDetailStatus, _researchDetailCost, _researchDetailTime, _researchDetailSpread, _researchDetailReason;
    private Button _researchStart;
    private string _researchCategory = "", _researchSelected = "", _researchStructure = "", _researchDetailKey = "", _researchPickerKey = "";
    private bool _researchGraph, _researchPathOnly;
    private int _researchPage;
    private ResearchWorkspace.Filter _researchFilter;
    private readonly List<string> _researchCategoryIds = new();
    private readonly Dictionary<string, Vector2> _researchPositions = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _researchCards = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ProgressBar> _researchBars = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, Button> _researchRequirements = new(StringComparer.OrdinalIgnoreCase);
    private readonly Dictionary<string, ResearchSystem.DiffusionBreakdown> _researchCosts = new(StringComparer.OrdinalIgnoreCase);

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
        var rows = PanelRows(panel, 18, 14, 8);
        var header = new HBoxContainer();
        rows.AddChild(header);
        var title = Title("Research", 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        _researchFocus = ActionButton("Show current research", () => SelectResearchTechnology(_campaign.Player.CurrentResearch, true), 0, 32);
        _researchFocus.Name = "FocusCurrentResearch";
        header.AddChild(_researchFocus);
        header.AddChild(HandbookButton("research"));
        header.AddChild(ActionButton("×", CloseResearchScreen, 38, 32));
        _researchSummary = Title("", 14, Ui.Colour("text.body"));
        _researchSummary.Name = "ResearchSummary";
        rows.AddChild(_researchSummary);
        _researchProgress = ResearchProgressBar("ActiveResearchProgress", 10);
        rows.AddChild(_researchProgress);

        var toolbar = new HBoxContainer();
        rows.AddChild(toolbar);
        _researchSearch = new LineEdit { Name = "ResearchSearch", ClearButtonEnabled = true, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _researchSearch.TextChanged += _ => { ResearchFilterChanged(); };
        toolbar.AddChild(_researchSearch);
        _researchCatalogView = ActionButton("Catalogue", () => SetResearchView(false), 112, 32, true);
        _researchCatalogView.Name = "ResearchCatalogueView";
        toolbar.AddChild(_researchCatalogView);
        _researchGraphView = ActionButton("Dependencies", () => SetResearchView(true), 112, 32);
        _researchGraphView.Name = "ResearchDependencyView";
        toolbar.AddChild(_researchGraphView);

        var filters = new HBoxContainer();
        rows.AddChild(filters);
        _researchCategoryPicker = new OptionButton { Name = "ResearchCategoryFilter", CustomMinimumSize = new(165, 32), FitToLongestItem = false };
        filters.AddChild(_researchCategoryPicker);
        _researchCategoryPicker.ItemSelected += index =>
        {
            _researchCategory = _researchCategoryIds[(int)index]; ResearchFilterChanged();
        };
        _researchStatusPicker = new OptionButton { Name = "ResearchStatusFilter", CustomMinimumSize = new(165, 32), FitToLongestItem = false };
        filters.AddChild(_researchStatusPicker);
        _researchStatusPicker.ItemSelected += index =>
        {
            _researchFilter = (ResearchWorkspace.Filter)index; ResearchFilterChanged();
        };
        _researchResults = Title("", 12, Ui.Colour("text.muted"));
        _researchResults.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        filters.AddChild(_researchResults);
        filters.AddChild(ActionButton("Reset filters", ResetResearchFilters, 0, 32));

        var body = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        body.AddThemeConstantOverride("separation", 16);
        rows.AddChild(body);
        var browser = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        body.AddChild(browser);
        _researchListScroll = new ScrollContainer { Name = "ResearchCatalogueScroll", SizeFlagsVertical = SizeFlags.ExpandFill, HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        browser.AddChild(_researchListScroll);
        _researchList = new VBoxContainer { Name = "ResearchCatalogue", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _researchList.AddThemeConstantOverride("separation", 6);
        _researchListScroll.AddChild(_researchList);
        _researchScroll = new ScrollContainer { Name = "ResearchTreeScroll", SizeFlagsVertical = SizeFlags.ExpandFill, Visible = false };
        browser.AddChild(_researchScroll);
        _researchTree = new TechnologyTreeCanvas { Name = "TechnologyGraph", MouseFilter = MouseFilterEnum.Pass };
        _researchScroll.AddChild(_researchTree);
        var paging = new HBoxContainer();
        browser.AddChild(paging);
        _researchPrevious = ActionButton("Previous", () => { _researchPage--; _researchListScroll.ScrollVertical = 0; RefreshResearchScreen(); }, 0, 28);
        _researchPrevious.Name = "ResearchPrevious";
        paging.AddChild(_researchPrevious);
        _researchPageLabel = Title("", 12, Ui.Colour("text.muted"));
        _researchPageLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _researchPageLabel.HorizontalAlignment = HorizontalAlignment.Center;
        paging.AddChild(_researchPageLabel);
        _researchNext = ActionButton("Next", () => { _researchPage++; _researchListScroll.ScrollVertical = 0; RefreshResearchScreen(); }, 0, 28);
        _researchNext.Name = "ResearchNext";
        paging.AddChild(_researchNext);
        _researchDetailScroll = new ScrollContainer
        {
            Name = "ResearchDetailsScroll", CustomMinimumSize = new(Math.Clamp(InterfaceSkin.Metric("research.details_width", 350), 280, 420), 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        body.AddChild(_researchDetailScroll);
        body.Resized += () =>
        {
            var minimum = Math.Clamp(InterfaceSkin.Metric("research.details_width", 350), 280, 420);
            var maximum = Math.Clamp(InterfaceSkin.Metric("research.details_max_width", 560), minimum, 700);
            var fraction = Math.Clamp(InterfaceSkin.Metric("research.details_fraction", .38f), .25f, .5f);
            _researchDetailScroll.CustomMinimumSize = new(Math.Clamp(body.Size.X * fraction, minimum, maximum), 0);
        };
        _researchDetails = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _researchDetails.AddThemeConstantOverride("separation", 8);
        _researchDetailScroll.AddChild(_researchDetails);
        var foot = Title("Switching projects keeps invested points. Inspecting a technology does not start research.", 12, Ui.Colour("text.muted"));
        foot.TooltipText = Ui.Tip(Tr("Future tiers represent possible development paths, not dated predictions."));
        rows.AddChild(foot);
    }

    private ProgressBar ResearchProgressBar(string name, int height)
    {
        var bar = new ProgressBar { Name = name, CustomMinimumSize = new(0, height), ShowPercentage = false, MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = Ui.Colour("surface.overlay") });
        bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour("text.accent") });
        return bar;
    }

    private void SetResearchView(bool graph)
    {
        _researchGraph = graph; _researchPathOnly = false; _researchStructure = "";
        RefreshResearchScreen();
    }

    private void ResearchFilterChanged()
    {
        _researchPage = 0; _researchPathOnly = false;
        if (_researchListScroll is not null) _researchListScroll.ScrollVertical = 0;
        if (_researchScroll is not null) { _researchScroll.ScrollVertical = 0; _researchScroll.ScrollHorizontal = 0; }
        RefreshResearchScreen();
    }

    private void ResetResearchFilters()
    {
        _researchCategory = ""; _researchFilter = ResearchWorkspace.Filter.All; _researchPathOnly = false; _researchPage = 0;
        _researchSearch.Text = "";
        RefreshResearchScreen();
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
            _researchSelected = (TechnologyCatalog.Of(_campaign.Player.CurrentResearch) ?? TechnologyCatalog.All.FirstOrDefault())?.Id ?? "";
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
        var technology = TechnologyCatalog.Of(id);
        if (technology is null) return;
        _researchSelected = technology.Id;
        if (reveal) ResetResearchFilters();
        RefreshResearchScreen();
        if (!reveal) return;
        if (_researchGraph && _researchPositions.TryGetValue(id, out var position))
            Callable.From(() => { _researchScroll.ScrollHorizontal = (int)Math.Max(0, position.X - 20); _researchScroll.ScrollVertical = (int)Math.Max(0, position.Y - 20); }).CallDeferred();
        else if (!_researchGraph)
        {
            var index = Array.FindIndex(TechnologyCatalog.All.ToArray(), entry => string.Equals(entry.Id, id, StringComparison.OrdinalIgnoreCase));
            _researchPage = Math.Max(0, index) / ResearchPageSize;
            RefreshResearchScreen();
            if (_researchCards.TryGetValue(id, out var row))
                Callable.From(() => _researchListScroll.EnsureControlVisible(row)).CallDeferred();
        }
    }

    private int ResearchPageSize => (int)Math.Clamp(InterfaceSkin.Metric("research.page_size", 18), 6, 60);

    private void RefreshResearchPickers()
    {
        var key = TechnologyCatalog.Revision + "|" + Loc.Language;
        if (_researchPickerKey != key && !InterfaceInteractionGuard.Busy(_researchLayer))
        {
            _researchPickerKey = key;
            _researchCategoryIds.Clear(); _researchCategoryIds.Add("");
            _researchCategoryIds.AddRange(TechnologyCatalog.All.Select(technology => technology.Category).Distinct(StringComparer.OrdinalIgnoreCase));
            _researchCategoryPicker.Clear();
            foreach (var category in _researchCategoryIds)
            {
                var caption = category.Length == 0 ? Tr("All technologies") : Tr("research.category." + category);
                _researchCategoryPicker.AddItem(caption == "research.category." + category ? category : caption);
            }
            _researchStatusPicker.Clear();
            foreach (var caption in new[] { "Any status", "Ready or researching", "Prerequisites missing", "Researched", "Invested projects" })
                _researchStatusPicker.AddItem(Tr(caption));
        }
        if (_researchCategoryIds.Count > 0)
            _researchCategoryPicker.Select(Math.Max(0, _researchCategoryIds.FindIndex(id => string.Equals(id, _researchCategory, StringComparison.OrdinalIgnoreCase))));
        _researchStatusPicker.Select((int)_researchFilter);
    }

    private void RefreshResearchScreen()
    {
        if (_researchLayer is null || !_researchLayer.Visible || _campaign?.Player is null) return;
        var nation = _campaign.Player;
        var current = TechnologyCatalog.Of(nation.CurrentResearch);
        var pace = ResearchSystem.WeeklyPoints(_campaign, nation);
        // Per-refresh projection, not simulation state. Each visible cost is calculated at most once.
        _researchCosts.Clear();
        ResearchSystem.DiffusionBreakdown Cost(TechnologyDefinition technology)
        {
            if (!_researchCosts.TryGetValue(technology.Id, out var result))
                _researchCosts[technology.Id] = result = ResearchSystem.DiffusionFor(_campaign, nation, technology);
            return result;
        }
        _researchFocus.Disabled = current is null;
        _researchSearch.PlaceholderText = Tr("Search technologies…");
        RefreshResearchPickers();
        var points = current is null ? 0 : nation.ResearchProgress.GetValueOrDefault(current.Id);
        var cost = current is null ? 0 : Cost(current).EffectiveCost;
        _researchProgress.Value = cost > 0 ? Math.Clamp(points / cost * 100, 0, 100) : 0;
        _researchProgress.Visible = current is not null;
        _researchSummary.Text = (current is null ? Tr("Choose a research project") :
            Loc.Format("Current project: {0} · {1} / {2} points", Tr(current.Name), points.ToString("0.0"), cost.ToString("0.0")))
            + "\n" + Loc.Format("{0} points/week · {1}/{2} technologies", pace.ToString("0.0"), nation.Technologies.Count(id => TechnologyCatalog.Of(id) is not null), TechnologyCatalog.All.Count);
        var found = ResearchWorkspace.Browse(TechnologyCatalog.All, nation, _researchCategory, _researchFilter, _researchSearch.Text, text => Tr(text));
        if (_researchPathOnly)
        {
            var path = ResearchWorkspace.DependencyPath(TechnologyCatalog.All, _researchSelected);
            found = TechnologyCatalog.All.Where(technology => path.Contains(technology.Id)).ToArray();
        }
        var pages = Math.Max(1, (found.Length + ResearchPageSize - 1) / ResearchPageSize);
        _researchPage = Math.Clamp(_researchPage, 0, pages - 1);
        var shown = _researchGraph ? found : found.Skip(_researchPage * ResearchPageSize).Take(ResearchPageSize).ToArray();
        _researchResults.Text = _researchPathOnly ? Tr("Selected project and all prerequisites") : Loc.Format("{0} matching technologies", found.Length);
        _researchPageLabel.Text = _researchGraph ? Tr("Lines connect prerequisites to discoveries") : Loc.Format("Page {0} / {1}", _researchPage + 1, pages);
        _researchPrevious.Visible = _researchNext.Visible = !_researchGraph;
        _researchPrevious.Disabled = _researchPage == 0;
        _researchNext.Disabled = _researchPage + 1 == pages;
        _researchListScroll.Visible = !_researchGraph;
        _researchScroll.Visible = _researchGraph;
        StyleButton(_researchCatalogView, !_researchGraph);
        StyleButton(_researchGraphView, _researchGraph);
        // Day, points and inspected selection are absent: ticks update controls in place.
        var structure = $"{TechnologyCatalog.Revision}|{_researchGraph}|{string.Join(',', shown.Select(technology => technology.Id))}";
        if (structure != _researchStructure)
        {
            _researchStructure = structure;
            RebuildResearchCards(shown);
        }
        foreach (var technology in shown)
        {
            var state = ResearchWorkspace.Status(nation, technology);
            var known = state == ResearchWorkspace.State.Known;
            var invested = nation.ResearchProgress.GetValueOrDefault(technology.Id);
            var total = known ? 0 : Cost(technology).EffectiveCost;
            var card = _researchCards[technology.Id];
            card.Text = Tr(technology.Name) + "\n" + Tr(ResearchWorkspace.Caption(state)) + (known ? "" : $" · {invested:0.#} / {total:0.#}");
            card.TooltipText = Ui.Tip(Tr(technology.Name) + "\n" + Tr(technology.Description));
            StyleButton(card, string.Equals(technology.Id, _researchSelected, StringComparison.OrdinalIgnoreCase));
            _researchBars[technology.Id].Visible = !known && invested > 0;
            _researchBars[technology.Id].Value = known ? 100 : Math.Clamp(invested / Math.Max(1, total) * 100, 0, 100);
        }
        if (_researchGraph) RefreshResearchEdges(shown);
        var detailKey = $"{TechnologyCatalog.Revision}|{_researchSelected}|{Loc.Language}";
        if (detailKey != _researchDetailKey)
        {
            _researchDetailKey = detailKey;
            RebuildResearchDetails();
            _researchDetailScroll.ScrollVertical = 0;
        }
        if (TechnologyCatalog.Of(_researchSelected) is { } selected)
            RefreshResearchDetails(selected, Cost(selected), pace);
    }

    private void RebuildResearchCards(TechnologyDefinition[] shown)
    {
        // Keep matching instances even when a completion changes a filtered result set.
        var parent = _researchGraph ? (Control)_researchTree : _researchList;
        foreach (var pair in _researchCards.ToArray())
        {
            if (pair.Value.GetParent() == parent && shown.Any(technology => technology.Id == pair.Key)) continue;
            pair.Value.GetParent()?.RemoveChild(pair.Value); pair.Value.QueueFree();
            _researchCards.Remove(pair.Key); _researchBars.Remove(pair.Key);
        }
        foreach (var empty in parent.GetChildren().OfType<Label>().ToArray()) { parent.RemoveChild(empty); empty.QueueFree(); }
        _researchPositions.Clear();
        var width = Math.Clamp(InterfaceSkin.Metric("research.card_width", 240), 180, 400);
        var height = Math.Clamp(InterfaceSkin.Metric("research.card_height", 128), 100, 220);
        var gapX = Math.Clamp(InterfaceSkin.Metric("research.column_gap", 64), 30, 180);
        var gapY = Math.Clamp(InterfaceSkin.Metric("research.row_gap", 28), 16, 100);
        var layout = TechnologyTreeLayout.Build(_researchPathOnly ? shown : TechnologyCatalog.All).ToDictionary(node => node.Id);
        var columns = shown.Select(technology => layout[technology.Id].Column).Distinct().Order().ToArray();
        var extent = new Vector2(320, 160);
        for (var index = 0; index < shown.Length; index++)
        {
            var technology = shown[index];
            if (!_researchCards.TryGetValue(technology.Id, out var card))
            {
                card = ActionButton("", () => SelectResearchTechnology(technology.Id), 0, _researchGraph ? 0 : 62);
                card.Name = "Technology_" + technology.Id;
                card.ClipText = true; card.AutowrapMode = TextServer.AutowrapMode.WordSmart;
                card.Alignment = HorizontalAlignment.Left;
                card.AddThemeFontSizeOverride("font_size", (int)Math.Clamp(InterfaceSkin.Metric("research.card_font_size", 14), 11, 20));
                card.Icon = Art(technology.Icon, "res://game/gfx/interface/icons/icon_research.png");
                card.ExpandIcon = true; card.AddThemeConstantOverride("icon_max_width", 24);
                var bar = ResearchProgressBar("ResearchInvestment", 3);
                card.AddChild(bar);
                bar.SetAnchorsAndOffsetsPreset(LayoutPreset.BottomWide);
                bar.OffsetLeft = 8; bar.OffsetRight = -8; bar.OffsetTop = -5; bar.OffsetBottom = -2;
                parent.AddChild(card);
                _researchCards[technology.Id] = card; _researchBars[technology.Id] = bar;
            }
            card.Icon = Art(technology.Icon, "res://game/gfx/interface/icons/icon_research.png");
            if (!_researchGraph) { _researchList.MoveChild(card, index); continue; }
            var node = layout[technology.Id];
            // Compact empty filtered columns; retain authored rows and dependency order.
            card.Position = new(16 + Array.IndexOf(columns, node.Column) * (width + gapX), 16 + node.Row * (height + gapY));
            card.Size = new(width, height);
            card.SetDeferred(Control.PropertyName.Size, new Vector2(width, height));
            _researchPositions[technology.Id] = card.Position;
            extent.X = Math.Max(extent.X, card.Position.X + width + 16);
            extent.Y = Math.Max(extent.Y, card.Position.Y + height + 16);
        }
        _researchTree.CustomMinimumSize = extent;
        if (shown.Length == 0)
        {
            var empty = Title("No matching technologies", 16, Ui.Colour("text.muted"));
            if (_researchGraph) { empty.Position = new(16, 16); empty.Size = new(288, 64); }
            parent.AddChild(empty);
        }
    }

    private void RefreshResearchEdges(TechnologyDefinition[] shown)
    {
        _researchTree.Edges.Clear();
        var width = Math.Clamp(InterfaceSkin.Metric("research.card_width", 240), 180, 400);
        var height = Math.Clamp(InterfaceSkin.Metric("research.card_height", 128), 100, 220);
        foreach (var technology in shown)
            foreach (var requirement in technology.Requires)
                if (_researchPositions.TryGetValue(requirement, out var from))
                    _researchTree.Edges.Add((from + new Vector2(width, height / 2), _researchPositions[technology.Id] + new Vector2(0, height / 2), Ui.Colour(_campaign.Player.HasTechnology(requirement) ? "state.positive" : "surface.line")));
        _researchTree.QueueRedraw();
    }

    private void RebuildResearchDetails()
    {
        ClearRuntimeChildren(_researchDetails);
        _researchRequirements.Clear();
        var technology = TechnologyCatalog.Of(_researchSelected);
        if (technology is null) { _researchDetails.AddChild(Title("Select a technology", 18, Ui.Colour("text.muted"))); return; }
        _researchDetails.AddChild(Title("SELECTED PROJECT", 12, Ui.Colour("text.caption")));
        _researchDetails.AddChild(Title(technology.Name, 21, Ui.Colour("text.accent")));
        _researchDetailStatus = Title("", 14, Ui.Colour("text.body"));
        _researchDetails.AddChild(_researchDetailStatus);
        _researchDetailCost = Title("", 14, Ui.Colour("text.body"));
        _researchDetails.AddChild(_researchDetailCost);
        _researchDetailTime = Title("", 13, Ui.Colour("text.muted"));
        _researchDetails.AddChild(_researchDetailTime);
        _researchStart = ActionButton("Start research", () => ChooseResearch(technology.Id), 0, 38, true);
        _researchStart.Name = "StartResearch";
        _researchDetails.AddChild(_researchStart);
        _researchDetailReason = Title("", 13, Ui.Colour("state.warning"));
        _researchDetails.AddChild(_researchDetailReason);
        _researchDetails.AddChild(Title(technology.Description, 14, Ui.Colour("text.body")));
        _researchDetails.AddChild(Title("Effects", 12, Ui.Colour("text.caption")));
        foreach (var effect in NationalModifiers.Lines(technology.Effects))
            _researchDetails.AddChild(Title(effect, 14, Ui.Colour("text.body")));
        _researchDetails.AddChild(Title("Prerequisites", 12, Ui.Colour("text.caption")));
        if (technology.Requires.Count == 0) _researchDetails.AddChild(Title("None", 13, Ui.Colour("text.muted")));
        foreach (var id in technology.Requires)
        {
            var requirement = TechnologyCatalog.Of(id);
            var button = ActionButton("", () => SelectResearchTechnology(id, true), 0, 32);
            button.Name = "Prerequisite_" + id;
            button.Disabled = requirement is null;
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _researchDetails.AddChild(button); _researchRequirements[id] = button;
        }
        _researchDetails.AddChild(ActionButton("Show dependency path", () =>
        {
            ResetResearchFilters(); _researchGraph = true; _researchPathOnly = true; RefreshResearchScreen();
        }, 0, 32));
        _researchDetails.AddChild(Title("Leads to", 12, Ui.Colour("text.caption")));
        var children = TechnologyCatalog.All.Where(child => child.Requires.Contains(technology.Id, StringComparer.OrdinalIgnoreCase)).ToArray();
        if (children.Length == 0) _researchDetails.AddChild(Title("None", 13, Ui.Colour("text.muted")));
        foreach (var child in children)
        {
            var button = ActionButton(Tr(child.Name), () => SelectResearchTechnology(child.Id, true), 0, 32);
            button.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            _researchDetails.AddChild(button);
        }
        _researchDetailSpread = Title("", 12, Ui.Colour("text.muted"));
        _researchDetails.AddChild(_researchDetailSpread);
    }

    private void RefreshResearchDetails(TechnologyDefinition technology, ResearchSystem.DiffusionBreakdown diffusion, float pace)
    {
        var nation = _campaign.Player;
        var state = ResearchWorkspace.Status(nation, technology);
        var known = state == ResearchWorkspace.State.Known;
        var enabled = _campaign.FeatureEnabled("research") &&
            (!_campaign.Defines.FeatureCommands.TryGetValue(nameof(SetResearchCommand), out var feature) ||
                string.IsNullOrWhiteSpace(feature) || _campaign.FeatureEnabled(feature));
        _researchDetailStatus.Text = Tr(ResearchWorkspace.Caption(state));
        _researchDetailStatus.AddThemeColorOverride("font_color", Ui.Colour(known ? "state.positive" : state == ResearchWorkspace.State.Blocked ? "state.warning" : "text.body"));
        var points = nation.ResearchProgress.GetValueOrDefault(technology.Id);
        _researchDetailCost.Text = Loc.Format("Invested: {0} / {1} points", points.ToString("0.0"), diffusion.EffectiveCost.ToString("0.0"));
        _researchDetailCost.Visible = _researchDetailTime.Visible = !known;
        var weeks = ResearchWorkspace.Weeks(diffusion.EffectiveCost - points, pace);
        _researchDetailTime.Text = weeks.HasValue ? Loc.Format(state == ResearchWorkspace.State.Blocked
            ? "Once unlocked: {0} week(s) at this pace." : "About {0} week(s) at this pace.", weeks.Value) : Tr("No research progress at the current pace.");
        _researchDetailTime.TooltipText = Ui.Tip(Tr("Research pace depends on the economy, literacy, education, universities, laboratories, stability and previous discoveries."));
        _researchStart.Text = Tr(known ? "Researched" : state == ResearchWorkspace.State.Active ? "Researching" : "Start research");
        _researchStart.Disabled = !ResearchWorkspace.CanStart(_campaign, nation, technology);
        StyleButton(_researchStart, !_researchStart.Disabled);
        _researchDetailReason.Text = !enabled ? Tr("Research is disabled in this campaign.") : state == ResearchWorkspace.State.Blocked ? Tr("Complete every prerequisite first.") : "";
        _researchDetailReason.Visible = _researchDetailReason.Text.Length > 0;
        foreach (var (id, button) in _researchRequirements)
            button.Text = (nation.HasTechnology(id) ? "✓ " : "→ ") + Tr(TechnologyCatalog.Of(id)?.Name ?? id);
        _researchDetailSpread.Text = known ? "" : Loc.Format("Knowledge spread: world {0}, neighbours {1}, partners {2}; total discount {3}.",
            Fmt.Percent(diffusion.World), Fmt.Percent(diffusion.Neighbours), Fmt.Percent(diffusion.Partners), Fmt.Percent(diffusion.Total));
    }
}
