using System;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Standalone authored editor scene, mounted in the workshop with a detached draft.</summary>
public partial class ScenarioMapEditor : Control
{
    public ScenarioMapDraft Draft;
    public ProvinceTopology Topology;
    public string ModRoot;
    public Action ExitRequested;
    private ScenarioMapCanvas _map;
    private ItemList _countries;
    private OptionButton _regions;
    private Label _status, _selection;
    private LineEdit _name;
    private Button _undo, _redo, _select, _paint;
    private ScenarioMapDraft.Country[] _filtered;
    private int _owner, _inspectedProvince;
    private bool _savedOnce;

    private static Label Caption(string text) => new() { Text = Loc.Get(text), AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private static Button ActionButton(string id, string text, Action action)
    {
        var button = new Button { Name = id, Text = Loc.Get(text), CustomMinimumSize = new Vector2(0, 34) };
        DealOrForceGame.StyleButton(button, false);
        button.Pressed += action; return button;
    }
    public override void _Ready()
    {
        if (Draft is null || Topology is null)
        {
            AddChild(Caption("Open this editor from Scenarios & workshop after the map loads.")); return;
        }
        var rows = new VBoxContainer { Name = "EditorRows" }; AddChild(rows); rows.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        rows.AddThemeConstantOverride("separation", 8);
        rows.AddChild(Caption("Political map editor — independent draft. The current campaign is unchanged."));
        var toolbar = new HFlowContainer { Name = "MapTools" }; rows.AddChild(toolbar);
        _select = ActionButton("MapSelect", "Select province", () => { _map.FinishStroke(); _map.PaintMode = false; RefreshStatus(); }); toolbar.AddChild(_select);
        _paint = ActionButton("MapPaint", "Paint ownership", () => { _map.PaintMode = true; RefreshStatus(); }); toolbar.AddChild(_paint);
        _undo = ActionButton("MapUndo", "Undo", () => { _map.FinishStroke(); Draft.Undo(); RefreshMap(); }); toolbar.AddChild(_undo);
        _redo = ActionButton("MapRedo", "Redo", () => { _map.FinishStroke(); Draft.Redo(); RefreshMap(); }); toolbar.AddChild(_redo);
        toolbar.AddChild(ActionButton("MapFit", "Fit world", () => _map.Fit()));
        toolbar.AddChild(ActionButton("MapFocus", "Focus target country", () => _map.FocusCountry(_owner)));
        var columns = new HSplitContainer { Name = "MapColumns", SizeFlagsVertical = SizeFlags.ExpandFill,
            SplitOffsets = new[] { (int)Math.Clamp(InterfaceSkin.Metric("map_editor.sidebar_width", 260), 200, 420) } };
        rows.AddChild(columns);
        var sidebar = new VBoxContainer { Name = "MapInspector", CustomMinimumSize = new Vector2(200, 0) }; columns.AddChild(sidebar);
        sidebar.AddChild(Caption("Target country"));
        var search = new LineEdit { Name = "MapCountrySearch", PlaceholderText = Loc.Get("Find country…"), ClearButtonEnabled = true }; sidebar.AddChild(search);
        _countries = new ItemList { Name = "MapCountries", SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(200, 110) }; sidebar.AddChild(_countries);
        search.TextChanged += FilterCountries;
        _countries.ItemSelected += i => ChooseOwner(_filtered[(int)i].MapIndex);
        sidebar.AddChild(Caption("Target region"));
        _regions = new OptionButton { Name = "MapRegions", FitToLongestItem = false }; sidebar.AddChild(_regions);
        _regions.ItemSelected += i => { _map.FinishStroke(); _map.TargetRegion = _regions.GetItemId((int)i); RefreshStatus(); };
        _selection = Caption("Select a province on the map to inspect it."); _selection.Name = "MapSelection"; sidebar.AddChild(_selection);
        _map = new ScenarioMapCanvas { Name = "PoliticalMapCanvas", Draft = Draft, Topology = Topology,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(240, 200) };
        _map.Picked = Inspect; _map.Changed = RefreshStatus; columns.AddChild(_map);
        rows.AddChild(Caption("Left drag: paint whole provinces. Wheel: zoom. Middle drag: pan. Borders are visible when zoomed in."));
        rows.AddChild(Caption("This brush does not split provinces or recalculate population, armies or the economy. Review those data in the content editor."));
        var saveRow = new HBoxContainer { Name = "MapSaveRow" }; rows.AddChild(saveRow);
        _name = new LineEdit { Name = "MapScenarioName", Text = Loc.Get("My scenario"), SizeFlagsHorizontal = SizeFlags.ExpandFill }; saveRow.AddChild(_name);
        _name.TextChanged += _ => { _savedOnce = false; RefreshStatus(); };
        saveRow.AddChild(ActionButton("MapSave", "Save scenario to mod", Save));
        _status = Caption(""); _status.Name = "MapEditorStatus"; rows.AddChild(_status);
        FilterCountries(""); ChooseOwner(Draft.Countries.First().MapIndex); RefreshStatus();
    }
    private void FilterCountries(string text)
    {
        _countries.Clear(); _filtered = Draft.Countries.Where(c => (Loc.Get(c.Name) + " " + c.Tag).Contains(text, StringComparison.OrdinalIgnoreCase))
            .OrderBy(c => Loc.Get(c.Name)).ToArray();
        foreach (var c in _filtered) _countries.AddItem($"{Loc.Get(c.Name)} [{c.Tag}]");
        var selected = Array.FindIndex(_filtered, c => c.MapIndex == _owner); if (selected >= 0) _countries.Select(selected);
    }
    private void ChooseOwner(int owner)
    {
        _map.FinishStroke(); _owner = owner; _map.TargetOwner = owner; _regions.Clear();
        foreach (var r in Draft.Regions.Where(r => r.Owner == owner).OrderBy(r => r.Name)) _regions.AddItem(Loc.Get(r.Name), r.MapIndex);
        _regions.Disabled = _regions.ItemCount == 0;
        _map.TargetRegion = _regions.ItemCount > 0 ? _regions.GetItemId(0) : 0;
        var selected = Array.FindIndex(_filtered, c => c.MapIndex == owner);
        if (selected >= 0) { _countries.Select(selected); _countries.EnsureCurrentIsVisible(); }
        RefreshStatus();
    }
    private void Inspect(int p)
    {
        if (p <= 0) return;
        _inspectedProvince = p;
        var owner = Draft.Countries.FirstOrDefault(c => c.MapIndex == Draft.OwnerAt(p));
        var region = Draft.Regions.FirstOrDefault(r => r.MapIndex == Draft.StateAt(p));
        _selection.Text = $"#{p}\n{Loc.Get(owner?.Name ?? "Unclaimed")}\n{Loc.Get(region?.Name ?? "Unknown")}";
    }
    private void RefreshMap() { _map.InvalidateMap(); RefreshStatus(); }
    private void RefreshStatus()
    {
        if (_status is null) return;
        // Keep the inspector consistent after painting, undo and redo without another click.
        if (_inspectedProvince > 0) Inspect(_inspectedProvince);
        DealOrForceGame.StyleButton(_select, !_map.PaintMode);
        DealOrForceGame.StyleButton(_paint, _map.PaintMode);
        _undo.Disabled = !Draft.CanUndo; _redo.Disabled = !Draft.CanRedo;
        var c = Draft.Countries.FirstOrDefault(c => c.MapIndex == _owner);
        _status.Text = Loc.Get(_map.PaintMode ? "Paint ownership" : "Select province") + " · " + Loc.Get(c?.Name ?? "") + " · " +
            Loc.Get(Draft.Dirty || !_savedOnce ? "Unsaved draft" : "Saved") +
            (_map.TargetRegion == 0 ? " · " + Loc.Get("No region available for this country.") : "");
    }
    private void Save()
    {
        _map.FinishStroke();
        try { var path = Draft.Save(ModRoot, _name.Text); _savedOnce = true; RefreshStatus(); _status.Text += "\n" + path; }
        catch (Exception e) { _status.Text = Loc.Get("Could not save scenario") + ": " + e.Message; }
    }
    public void RequestClose()
    {
        _map?.FinishStroke();
        if (Draft is null || !Draft.Dirty && _savedOnce) { ExitRequested?.Invoke(); return; }
        if (GetChildren().OfType<ConfirmationDialog>().Any(d => d.Visible)) return;
        var dialog = new ConfirmationDialog { Name = "DiscardMapDraft", Title = Loc.Get("Unsaved changes"), DialogText = Loc.Get("Discard unsaved changes?"), Exclusive = true };
        AddChild(dialog); dialog.Confirmed += () => { dialog.QueueFree(); ExitRequested?.Invoke(); }; dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }
}
