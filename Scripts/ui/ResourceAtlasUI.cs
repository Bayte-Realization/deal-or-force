using System;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private PanelContainer _resourceAtlasPanel;
    private OptionButton _resourceChoice;
    private LineEdit _resourceSearch;
    private CheckButton _resourceOwned;
    private Label _resourceSummary;
    private VBoxContainer _resourceRows;
    private ScrollContainer _resourceScroll;
    private Button _resourcePrevious, _resourceNext;
    private int _resourcePage;
    private double _resourceSinceRefresh;
    private int _resourceFingerprint;

    // Bounded survey reads once a second while visible. No row churn or scroll jumps
    // on unrelated daily budget updates; map and list refresh together after a split.
    private void KeepResourceAtlas(double delta)
    {
        if (_resourceAtlasPanel is not { Visible: true } || _campaign is null) return;
        _resourceSinceRefresh += delta;
        if (_resourceSinceRefresh < 1) return;
        _resourceSinceRefresh = 0;
        var survey = ResourceAtlas.Read(_campaign, _globe.ResourceGood, InterfaceSkin.Metric("resources.minimum_potential", .05f));
        var fingerprint = new HashCode();
        fingerprint.Add(_campaign.MapRevision);
        fingerprint.Add(_campaign.ProvinceMap is not null);
        fingerprint.Add(_campaign.Player?.Id);
        foreach (var region in survey.Regions) fingerprint.Add(region);
        var current = fingerprint.ToHashCode();
        if (current == _resourceFingerprint) return;
        _resourceFingerprint = current;
        var scroll = _resourceScroll.ScrollVertical;
        RefreshResourceAtlas();
        _resourceScroll.SetDeferred(ScrollContainer.PropertyName.ScrollVertical, scroll);
        _globe.RefreshMapModePalette();
    }

    private Label ResourceText(string text, int size, string colour)
    {
        var label = Title(text, size, Ui.Colour(colour));
        label.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        label.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
        return label;
    }

    private void ShowResourceAtlas()
    {
        if (_campaign is null || _hudLayer is null) return;
        CloseSidePanel();
        HideMapSelection(true);
        SetArmyPanelVisible(false);
        if (_fleetPanel is not null) _fleetPanel.Visible = false;
        if (_supportLayer is { Visible: true }) CloseSupportScreen();
        if (_resourceAtlasPanel is null)
        {
            _resourceAtlasPanel = TexturedPanel(_hudLayer, PanelSurface.Command);
            _resourceAtlasPanel.Name = "ResourceAtlas";
            Ui.Place(_resourceAtlasPanel, "hud.resource_atlas");
            var rows = PanelRows(_resourceAtlasPanel, 16, 12, 8);
            var header = new HBoxContainer();
            rows.AddChild(header);
            header.AddChild(ResourceText("Resource survey", (int)InterfaceSkin.Metric("resources.title_size", 21), "text.accent"));
            header.AddChild(ActionButton("×", () => _globe.SetMapMode(StrategicMapMode.Political), 32, 30));
            _resourceChoice = new OptionButton { Name = "ResourceChoice", FitToLongestItem = false,
                CustomMinimumSize = new Vector2(0, 36), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            rows.AddChild(_resourceChoice);
            _resourceChoice.ItemSelected += index =>
            {
                _globe.SetResourceGood((Commodity)_resourceChoice.GetItemId((int)index));
                _resourcePage = 0;
                RefreshResourceAtlas();
            };
            rows.AddChild(ResourceText("Geological and plantation potential, not current production or measured reserves.", 12, "text.muted"));
            var ramp = new HBoxContainer();
            ramp.AddThemeConstantOverride("separation", 0);
            rows.AddChild(ramp);
            for (var i = 0; i < 24; i++)
                ramp.AddChild(new ColorRect { Color = GlobePresentation.ResourceColour(i / 23f),
                    CustomMinimumSize = new Vector2(0, 12), SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
                    MouseFilter = Control.MouseFilterEnum.Ignore });
            rows.AddChild(ResourceText("Dark: no deposit · green: lower potential · gold: highest potential", 12, "text.muted"));
            _resourceSummary = ResourceText("", 13, "text.body");
            _resourceSummary.Name = "ResourceSummary";
            rows.AddChild(_resourceSummary);
            _resourceSearch = new LineEdit { Name = "ResourceSearch", PlaceholderText = Tr("Search regions or countries…"),
                CustomMinimumSize = new Vector2(0, 32) };
            rows.AddChild(_resourceSearch);
            _resourceSearch.TextChanged += _ => { _resourcePage = 0; RefreshResourceAtlas(); };
            _resourceOwned = new CheckButton { Name = "ResourceOwnedOnly", Text = Tr("Only our regions") };
            rows.AddChild(_resourceOwned);
            _resourceOwned.Toggled += _ => { _resourcePage = 0; RefreshResourceAtlas(); };
            _resourceScroll = new ScrollContainer { Name = "ResourceScroll", SizeFlagsVertical = Control.SizeFlags.ExpandFill,
                HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(0, 48) };
            rows.AddChild(_resourceScroll);
            _resourceRows = new VBoxContainer { Name = "ResourceRegions", SizeFlagsHorizontal = Control.SizeFlags.ExpandFill };
            _resourceRows.AddThemeConstantOverride("separation", 5);
            _resourceScroll.AddChild(_resourceRows);
            var pages = new HBoxContainer();
            rows.AddChild(pages);
            _resourcePrevious = ActionButton("Previous", () => { _resourcePage--; RefreshResourceAtlas(); }, 0, 30);
            _resourceNext = ActionButton("Next", () => { _resourcePage++; RefreshResourceAtlas(); }, 0, 30);
            _resourcePrevious.SizeFlagsHorizontal = _resourceNext.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            pages.AddChild(_resourcePrevious);
            pages.AddChild(_resourceNext);
        }
        _resourceChoice.Clear();
        var goods = ProductionChains.ResourceGoods.OrderBy(g => Tr(CommodityCatalog.Get(g).Name)).ToArray();
        foreach (var good in goods) _resourceChoice.AddItem(Tr(CommodityCatalog.Get(good).Name), (int)good);
        var selected = Array.IndexOf(goods, _globe.ResourceGood);
        if (selected < 0 && goods.Length > 0) { selected = 0; _globe.SetResourceGood(goods[0]); }
        if (selected >= 0) _resourceChoice.Select(selected);
        _resourceAtlasPanel.Visible = true;
        RefreshResourceAtlas();
    }

    private void RefreshResourceAtlas()
    {
        if (_resourceAtlasPanel is null || _campaign is null) return;
        var survey = ResourceAtlas.Read(_campaign, _globe.ResourceGood, InterfaceSkin.Metric("resources.minimum_potential", .05f));
        var regions = ResourceAtlas.Filter(survey, _resourceSearch.Text, _resourceOwned.ButtonPressed ? _campaign.Player?.Id : null);
        var limit = Math.Clamp((int)InterfaceSkin.Metric("resources.rows_limit", 120), 1, 200);
        var pageCount = Math.Max(1, (int)Math.Ceiling(regions.Length / (double)limit));
        _resourcePage = Math.Clamp(_resourcePage, 0, pageCount - 1);
        _resourcePrevious.Disabled = _resourcePage == 0;
        _resourceNext.Disabled = _resourcePage + 1 >= pageCount;
        _resourceSummary.Text = Loc.Format("World potential {0:0.##} · {1} regions\nMatches {2} · page {3}/{4}",
            survey.Total, survey.Regions.Length, regions.Length, _resourcePage + 1, pageCount);
        ClearRuntimeChildren(_resourceRows);
        _resourceScroll.ScrollVertical = 0;
        if (_campaign.ProvinceMap is null)
            _resourceRows.AddChild(ResourceText("Map is loading. Region locations will be available when it is ready.", 13, "text.muted"));
        if (regions.Length == 0)
            _resourceRows.AddChild(ResourceText("No matching deposits. Choose another resource or clear the filters.", 13, "text.muted"));
        foreach (var region in regions.Skip(_resourcePage * limit).Take(limit))
        {
            var button = ActionButton(Loc.Format("{0}\n{1} · potential {2:0.##}", region.Name, region.OwnerName, region.Potential),
                () => LocateResourceRegion(region.MapIndex), 0, (int)InterfaceSkin.Metric("resources.row_height", 42));
            button.Name = "ResourceRegion_" + region.MapIndex;
            button.Alignment = HorizontalAlignment.Left;
            button.ClipText = true;
            button.SizeFlagsHorizontal = Control.SizeFlags.ExpandFill;
            button.Disabled = _campaign.ProvinceMap is null;
            button.TooltipText = Ui.Tip(Loc.Format("{0} · {1}\nPotential {2:0.##} ({3:P1} of world total). Locate this region; no orders are issued.",
                region.Name, region.OwnerName, region.Potential, survey.Total > 0 ? region.Potential / survey.Total : 0));
            _resourceRows.AddChild(button);
        }
    }

    private void LocateResourceRegion(int region)
    {
        var province = ResourceAtlas.FocusProvince(_campaign, region);
        if (province <= 0) return;
        var centre = _campaign.ProvinceMap.CenterOf(province);
        _globe.FocusOn(centre.Latitude, centre.Longitude, InterfaceSkin.Metric("resources.focus_distance", 6.5f));
    }
}
