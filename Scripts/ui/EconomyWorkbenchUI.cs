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
    private VBoxContainer _marketTableRows;
    private OptionButton _constructionRegion;
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
            rows.AddChild(Title(definition.Name, 15, Ui.Colour("text.bright")));
            var line = new HBoxContainer();
            rows.AddChild(line);
            var cost = Title(Simulation.Loc.Format("{0} · {1} days", Fmt.Money(definition.Cost), definition.Days), 12, Ui.Colour("text.caption"));
            cost.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            line.AddChild(cost);
            var captured = kind;

            // How many are standing here, so the row says what the region has rather than
            // only what it could have. The minus is the whole of the tester's request and
            // it needs this number to be worth showing at all.
            var standing = hasRegion ? _selectedState.Buildings.GetValueOrDefault(kind) : 0;
            if (standing > 0)
            {
                var have = Title(Simulation.Loc.Format("{0} standing", standing), 12, Ui.Colour("text.accent"));
                line.AddChild(have);
            }

            var remove = ActionButton("−", () => DemolishBuilding(captured), 36, 32);
            remove.Name = "Demolish" + kind;
            remove.Disabled = standing <= 0;
            Tip(remove, standing <= 0
                ? Tr("Nothing of this kind is standing in the selected region.")
                : Simulation.Loc.Format(
                    "Pull one down in {0}. Its contribution goes with it and {1} comes back as scrap.",
                    _selectedState.Name,
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
                : Tr(definition.Description) + "\n" + Simulation.Loc.Format("Queue in {0}", _selectedState.Name));
            line.AddChild(add);
        }
        if (_buildingCatalogRows.GetChildCount() == 0)
            _buildingCatalogRows.AddChild(Title("No matches", 13, Ui.Colour("text.muted")));
    }

    private VBoxContainer WorkbenchCard(Container parent)
    {
        var card = new PanelContainer();
        card.AddThemeStyleboxOverride("panel", ShellRowStyle(true));
        parent.AddChild(card);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 5);
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
            rows.AddChild(heading);
            var label = Title(definition.Name, 15, Ui.Colour("text.bright"));
            label.SizeFlagsHorizontal = SizeFlags.ExpandFill;
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
            numbers.AddChild(supply);
            var price = Title(Fmt.Money(quote.Price), 14, Ui.Colour("text.bright"));
            price.AutowrapMode = TextServer.AutowrapMode.Off;
            price.HorizontalAlignment = HorizontalAlignment.Right;
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
