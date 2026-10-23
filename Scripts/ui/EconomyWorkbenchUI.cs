using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private VBoxContainer _economyBuildRows;
    private VBoxContainer _economyMarketRows;
    private VBoxContainer _buildingCatalogRows;
    private VBoxContainer _constructionQueueRows;
    private VBoxContainer _marketTableRows;
    private OptionButton _constructionRegion;
    private HFlowContainer _economyDesk;
    private Button _deskWeek;
    private Button _deskTreasury;
    private Button _deskGood;
    private Button _deskSites;
    private Commodity _deskGoodId;
    private bool _deskHasGood;
    private LineEdit _buildingSearch;
    private LineEdit _marketSearch;
    private CheckButton _shortagesOnly;
    private bool _updatingConstructionRegion;

    private void BuildEconomyWorkbench(VBoxContainer parent)
    {
        _economyControls = new VBoxContainer { Visible = false };
        parent.AddChild(_economyControls);
        _economyBuildRows = new VBoxContainer();
        _economyBuildRows.AddThemeConstantOverride("separation", 10);
        _economyControls.AddChild(_economyBuildRows);
        _economyBuildRows.AddChild(Title("Build in region", 12, Ui.Colour("text.accent_dim")));
        _constructionRegion = new OptionButton { Name = "ConstructionRegion", FitToLongestItem = false, CustomMinimumSize = new Vector2(0, 36) };
        _constructionRegion.ItemSelected += index =>
        {
            if (_updatingConstructionRegion) return;
            _selectedState = _campaign.States.FirstOrDefault(state => state.MapIndex == _constructionRegion.GetItemId((int)index));
            RefreshBuildingCatalog();
        };
        _economyBuildRows.AddChild(_constructionRegion);
        _constructionStatus = Title("", 13, Ui.Colour("text.body_soft"));
        _economyBuildRows.AddChild(_constructionStatus);
        var queueScroll = new ScrollContainer
        {
            Name = "ConstructionQueueScroll", SizeFlagsHorizontal = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, Math.Clamp(InterfaceSkin.Metric("construction.queue_height", 190), 100, 360)),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled
        };
        _economyBuildRows.AddChild(queueScroll);
        _constructionQueueRows = new VBoxContainer { Name = "ConstructionQueue", SizeFlagsHorizontal = SizeFlags.ExpandFill };
        queueScroll.AddChild(_constructionQueueRows);
        _buildingSearch = ShellSearch("Search buildings…", "BuildingSearch");
        _buildingSearch.TextChanged += _ => RefreshBuildingCatalog();
        _economyBuildRows.AddChild(_buildingSearch);
        _buildingCatalogRows = new VBoxContainer { Name = "BuildingCatalog" };
        _buildingCatalogRows.AddThemeConstantOverride("separation", 6);
        _economyBuildRows.AddChild(_buildingCatalogRows);

        _economyMarketRows = new VBoxContainer();
        _economyMarketRows.AddThemeConstantOverride("separation", 10);
        _economyControls.AddChild(_economyMarketRows);
        _marketStatus = Title("", 13, Ui.Colour("text.body_soft"));
        _economyMarketRows.AddChild(_marketStatus);
        _marketSearch = ShellSearch("Search goods…", "MarketSearch");
        _marketSearch.TextChanged += _ => RefreshMarketTable();
        _economyMarketRows.AddChild(_marketSearch);
        _shortagesOnly = new CheckButton { Name = "ShortagesOnly", Text = Tr("Shortages only") };
        _shortagesOnly.SetMeta(LocSourceMeta, "Shortages only");
        _shortagesOnly.SetMeta(LocAppliedMeta, _shortagesOnly.Text);
        _shortagesOnly.Toggled += _ => RefreshMarketTable();
        _economyMarketRows.AddChild(_shortagesOnly);
        _marketTableRows = new VBoxContainer { Name = "MarketTable" };
        _marketTableRows.AddThemeConstantOverride("separation", 6);
        _economyMarketRows.AddChild(_marketTableRows);

        _economyDesk = new HFlowContainer
        {
            Name = "EconomyDesk", Visible = false, SizeFlagsHorizontal = SizeFlags.ExpandFill
        };
        _economyDesk.AddThemeConstantOverride("h_separation", 6);
        _economyDesk.AddThemeConstantOverride("v_separation", 6);
        _deskWeek = DeskButton("Week", () => OpenEconomyTab("Budget"), "Open the week's taxes and spending.");
        _deskTreasury = DeskButton("Treasury", () => OpenEconomyTab("Finance"), "Open the treasury and the debt.");
        _deskGood = DeskButton("Market", OpenDeskGood, "Open this good on the market.");
        _deskSites = DeskButton("Sites", () => OpenEconomyTab("Construction"), "Open the construction queue.");
        parent.AddChild(_economyDesk);
        parent.MoveChild(_economyDesk, _economyControls.GetIndex());
    }

    private Button DeskButton(string label, System.Action open, string tip)
    {
        var button = ActionButton(label, () =>
        {
            open();
            if (label != "Market") PlayStrategicCue("ui_select");
        }, 0, 34);
        button.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        button.ClipText = true;
        button.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button", 12));
        button.AddThemeFontOverride("font", PresentationFonts.Tabular());
        Tip(button, tip);
        _economyDesk.AddChild(button);
        return button;
    }

    private void OpenDeskGood()
    {
        OpenEconomyTab("Market");
        if (_deskHasGood && _marketSearch is not null)
        {
            var name = Tr(CommodityCatalog.Get(_deskGoodId).Name);
            _marketSearch.Text = name;
            _marketSearch.CaretColumn = name.Length;
            _shortagesOnly.ButtonPressed = false;
            RefreshMarketTable();
        }
        var shortOf = _deskHasGood && _campaign?.Player?.Market.GetValueOrDefault(_deskGoodId) is { IsShortage: true };
        PlayStrategicCue(shortOf ? "event_alert" : "ui_select");
    }

    private void RefreshEconomyDesk()
    {
        if (_economyDesk is null || _campaign?.Player is null) return;
        var player = _campaign.Player;
        var settled = _campaign.ElapsedDays >= _campaign.Defines.EconomyTickDays || player.Budget.HasEntries;
        SetDeskCaption(_deskWeek, Simulation.Loc.Format("Week {0}", settled ? SignedMoney(player.WeeklyBalance) : "—"));
        SetDeskCaption(_deskTreasury, Simulation.Loc.Format("Treasury {0}", Fmt.Money(player.Treasury)));
        var worst = player.Market.Values.OrderBy(quote => quote.Satisfaction).ThenBy(quote => quote.Commodity).FirstOrDefault();
        _deskHasGood = worst is not null;
        if (worst is not null)
        {
            _deskGoodId = worst.Commodity;
            SetDeskCaption(_deskGood, Simulation.Loc.Format("{0} {1:P0}", Tr(CommodityCatalog.Get(worst.Commodity).Name), worst.Satisfaction));
        }
        else SetDeskCaption(_deskGood, Tr("Market"));
        var sites = _campaign.Construction.Count(project => project.NationId == player.Id);
        SetDeskCaption(_deskSites, Simulation.Loc.Format("Sites {0}/{1}", sites, _campaign.Defines.MaxConstructionProjects));
    }

    /// <summary>
    /// A desk button's caption is a NUMBER - a treasury, a weekly balance, a market share - and
    /// a clipped number is not a shortened number, it is a different one: "$184.8B" cut to fit a
    /// narrow button reads "$18". So the button is measured against the text it actually holds
    /// and given that width, rather than clipped to whatever the row happened to leave it. A row
    /// that then does not fit wraps, because the desk is a flow container - which is the honest
    /// way to run out of room, instead of quietly changing what the figure says.
    /// </summary>
    private static void SetDeskCaption(Button button, string caption)
    {
        if (button is null) return;
        button.Text = caption;
        button.SetMeta(LocAppliedMeta, caption);
        var font = button.GetThemeFont("font");
        if (font is null) return;
        var fontSize = button.GetThemeFontSize("font_size");
        var width = font.GetStringSize(caption, HorizontalAlignment.Left, -1, fontSize).X;
        button.ClipText = false;
        button.CustomMinimumSize = new Vector2(MathF.Ceiling(width) + 22f, button.CustomMinimumSize.Y);
    }

    private LineEdit ShellSearch(string hint, string name)
    {
        var field = new LineEdit { Name = name, PlaceholderText = Tr(hint), CustomMinimumSize = new Vector2(0, 34) };
        field.SetMeta(LocHintMeta, hint);
        return field;
    }

    private void RefreshEconomyWorkbench()
    {
        var player = _campaign.Player;
        RefreshEconomyDesk();
        _marketStatus.Text = Simulation.Loc.Format("Market access {0:P0} · trade {1}", player.MarketAccess, SignedMoney(player.WeeklyTradeBalance));
        _updatingConstructionRegion = true;
        _constructionRegion.Clear();
        foreach (var region in _campaign.StatesOf(player).Where(region => !LakeRegions.IsWaterState(region)).OrderBy(region => region.Name))
            _constructionRegion.AddItem(region.Name, region.MapIndex);
        var index = _selectedState is null ? -1 : _constructionRegion.GetItemIndex(_selectedState.MapIndex);
        _constructionRegion.Select(index);
        _updatingConstructionRegion = false;
        if (_dossierTab == "Construction") RefreshBuildingCatalog();
        if (_dossierTab == "Market") RefreshMarketTable();
    }

    private void RefreshBuildingCatalog()
    {
        if (_campaign?.Player is null || _buildingCatalogRows is null) return;
        RefreshConstructionQueue();
        ClearRuntimeChildren(_buildingCatalogRows);
        var player = _campaign.Player;
        var count = _campaign.Construction.Count(project => project.NationId == player.Id);
        var hasRegion = _selectedState is not null && _selectedState.NationMapIndex == player.MapIndex
            && !LakeRegions.IsWaterState(_selectedState);
        foreach (var kind in BuildingCatalog.All)
        {
            var definition = BuildingCatalog.Get(kind);
            if (!Tr(definition.Name).Contains(_buildingSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase)) continue;
            var rows = WorkbenchCard(_buildingCatalogRows);
            var title = new HBoxContainer();
            title.AddThemeConstantOverride("separation", 7);
            rows.AddChild(title);
            if (Symbol(BuildingIcon(kind), 22, "text.accent_dim") is { } mark)
                title.AddChild(mark);
            var name = Title(definition.Name, 15, Ui.Colour("text.bright"));
            name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            // Nothing in a crowded row may wrap. A label with word wrap left on, in a box
            // where something else has already claimed the width, gets no width of its own
            // and wraps to one character per line - which turned a card two rows tall into
            // one the height of the panel, with the buttons stretched down its side.
            name.AutowrapMode = TextServer.AutowrapMode.Off;
            name.ClipText = true;
            title.AddChild(name);

            // How many are standing here, beside the name, where "what this region has"
            // reads as part of what the building IS rather than as part of its price.
            var standing = hasRegion ? _selectedState.Buildings.GetValueOrDefault(kind) : 0;
            if (standing > 0)
            {
                var have = Title(Simulation.Loc.Format("{0} standing", standing), 12, Ui.Colour("text.accent"));
                have.AutowrapMode = TextServer.AutowrapMode.Off;
                have.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
                title.AddChild(have);
            }

            var line = new HBoxContainer();
            rows.AddChild(line);
            var cost = Title(Simulation.Loc.Format("{0} · {1} days", Fmt.Money(definition.Cost), definition.Days), 12, Ui.Colour("text.caption"));
            cost.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            cost.AutowrapMode = TextServer.AutowrapMode.Off;
            cost.ClipText = true;
            line.AddChild(cost);
            var captured = kind;

            var remove = ActionButton("−", () => DemolishBuilding(captured), 36, 32);
            remove.Name = "Demolish" + kind;
            remove.Disabled = standing <= 0;
            Tip(remove, standing <= 0
                ? Tr("Nothing of this kind is standing in the selected region.")
                : Simulation.Loc.Format(
                    "Pull one down in {0}. Its contribution goes with it and {1} comes back as scrap.",
                    _selectedState.DisplayName,
                    Fmt.Money(definition.Cost * _campaign.Defines.DemolitionRefund)));
            line.AddChild(remove);

            var dry = hasRegion
                ? Waterfront.Refusal(_campaign, _selectedState.MapIndex, kind)
                : "";
            var add = ActionButton("+", () => QueueBuilding(captured), 36, 32);
            add.Name = "Build" + kind;
            // Mirror admission, not the full project price: the economy pays it over time.
            var slots = _campaign.Defines.MaxConstructionProjects;
            add.Disabled = !hasRegion || dry.Length > 0 || count >= slots ||
                           player.Treasury < Math.Min(0.05f, definition.Cost);
            Tip(add, !hasRegion ? Tr("Select an owned region.")
                : dry.Length > 0 ? dry
                : count >= slots ? Simulation.Loc.Format("Every one of the {0} construction sites is taken.", slots)
                : add.Disabled ? Tr("Insufficient treasury to start construction.")
                : Tr(definition.Description) + "\n" +
                  Simulation.Loc.Format("Queue in {0}", _selectedState.DisplayName));
            line.AddChild(add);
        }
        if (_buildingCatalogRows.GetChildCount() == 0)
            _buildingCatalogRows.AddChild(Title("No matches", 13, Ui.Colour("text.muted")));
    }

    private void RefreshConstructionQueue()
    {
        if (_constructionQueueRows is null) return;
        ClearRuntimeChildren(_constructionQueueRows);
        var queue = _campaign.Construction.Where(p => p.NationId == _campaign.Player.Id).OrderBy(p => p.Id).ToArray();
        _constructionQueueRows.GetParent<Control>().Visible = queue.Length > 0;
        foreach (var project in queue)
        {
            var region = _campaign.States.FirstOrDefault(s => s.MapIndex == project.StateMapIndex);
            var card = WorkbenchCard(_constructionQueueRows);
            var title = Title(Simulation.Loc.Format("{0} — {1}", Tr(BuildingCatalog.Get(project.Building).Name), region?.DisplayName ?? "—"), 13, Ui.Colour("text.bright"));
            title.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(title);
            var row = new HBoxContainer();
            card.AddChild(row);
            var progress = new ProgressBar
            {
                Value = project.Progress * 100, SizeFlagsHorizontal = SizeFlags.ExpandFill,
                CustomMinimumSize = new Vector2(90, 28)
            };
            row.AddChild(progress);
            var id = project.Id;
            var cancel = ActionButton("Cancel", () => CancelConstruction(id), 92, 28);
            cancel.Name = "CancelConstruction" + id;
            cancel.TooltipText = Ui.Tip(Simulation.Loc.Format(
                "Cancel only this project. Spent: {0} (not refunded). Remaining: {1} (will not be charged).",
                Fmt.Money(project.Paid), Fmt.Money(Math.Max(0, project.Cost - project.Paid))));
            row.AddChild(cancel);
        }
    }

    private VBoxContainer WorkbenchCard(Container parent)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", AtlasSurfaceSkin.Section("surface.inset"));
        AtlasSurfaceSkin.Attach(card, AtlasSurfaceSkin.Level.Inset, "surface.inset");
        parent.AddChild(card);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 6);
        card.AddChild(rows);
        return rows;
    }

    private void RefreshMarketTable()
    {
        if (_campaign?.Player is null || _marketTableRows is null) return;
        ClearRuntimeChildren(_marketTableRows);
        var quotes = _campaign.Player.Market.Values
            .Where(quote => !_shortagesOnly.ButtonPressed || quote.IsShortage)
            .Where(quote => Tr(CommodityCatalog.Get(quote.Commodity).Name).Contains(_marketSearch.Text.Trim(), StringComparison.OrdinalIgnoreCase))
            .OrderBy(quote => quote.Satisfaction).ThenBy(quote => quote.Commodity).ToArray();
        foreach (var quote in quotes)
        {
            var definition = CommodityCatalog.Get(quote.Commodity);
            var rows = WorkbenchCard(_marketTableRows);
            var heading = new HBoxContainer();
            heading.AddThemeConstantOverride("separation", 7);
            rows.AddChild(heading);
            // Twenty goods in one scrolling list, told apart by reading twenty names. A mark
            // per good is what makes the market scannable rather than searchable.
            if (Symbol(GoodIcon(quote.Commodity), 22, "icon.commodity") is { } mark)
                heading.AddChild(mark);
            var label = Title(definition.Name, 15, Ui.Colour("text.bright"));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            label.AutowrapMode = TextServer.AutowrapMode.Off;
            label.ClipText = true;
            heading.AddChild(label);
            var commodity = quote.Commodity;
            var watching = PlayerSettings.Bool("watch.good." + commodity, false);
            var watch = ActionButton(watching ? "★" : "☆", () => ToggleMarketWatch(commodity), 34, 30, watching);
            watch.Name = "Watch" + commodity;
            Tip(watch, watching ? "Stop tracking this good" : "Track this good in the outliner");
            heading.AddChild(watch);
            var numbers = new HBoxContainer();
            rows.AddChild(numbers);
            var supply = Title(Simulation.Loc.Format("Supply {0:P0}", quote.Satisfaction), 13,
                Ui.Colour(quote.IsShortage ? "state.warning" : "state.positive"));
            supply.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            supply.AddThemeFontOverride("font", PresentationFonts.Tabular());
            numbers.AddChild(supply);
            var price = Title(Fmt.Money(quote.Price), 14, Ui.Colour("text.bright"));
            price.AutowrapMode = TextServer.AutowrapMode.Off;
            price.HorizontalAlignment = HorizontalAlignment.Right;
            price.AddThemeFontOverride("font", PresentationFonts.Tabular());
            numbers.AddChild(price);
            var bar = new ProgressBar { MinValue = 0, MaxValue = 100, Value = quote.Satisfaction * 100,
                ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4), MouseFilter = MouseFilterEnum.Ignore };
            bar.AddThemeStyleboxOverride("fill", new StyleBoxFlat { BgColor = Ui.Colour(quote.IsShortage ? "state.warning" : "state.positive"), ContentMarginTop = 2, ContentMarginBottom = 2 });
            bar.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("0c1720") });
            rows.AddChild(bar);
            var flow = Title(Simulation.Loc.Format("Output {0:0.#} · demand {1:0.#} · net {2:+0.#;-0.#;0}", quote.Production, quote.Demand, quote.Net),
                12, Ui.Colour("text.caption"));
            Tip(flow, Simulation.Loc.Format("Imports {0:0.#} · exports {1:0.#}", quote.Imports, quote.Exports));
            flow.MouseFilter = MouseFilterEnum.Stop;
            rows.AddChild(flow);
        }
        if (quotes.Length == 0) _marketTableRows.AddChild(Title("No matches", 13, Ui.Colour("text.muted")));
    }

    private void ToggleMarketWatch(Commodity commodity)
    {
        var key = "watch.good." + commodity;
        PlayerSettings.Set(key, !PlayerSettings.Bool(key, false));
        _strategicHudFingerprint = int.MinValue;
        RefreshMarketTable();
        RefreshStrategicHud();
    }
}
