using System;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using System.Text.Json.Nodes;
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
    /// <summary>
    /// Opens the flag editor for a country tag over this editor, without leaving it. Set by the
    /// game that mounts the editor; null hides the button.
    /// </summary>
    public Action<string> OpenFlagEditor;
    private ScenarioMapCanvas _map;
    private ItemList _countries;
    private OptionButton _regions;
    private Label _status, _selection, _scenarioSummary;
    private LineEdit _search;
    private Button _undo, _redo, _select, _paint;
    private Button _sample, _editSelectedCountry, _editSelectedRegion;
    private ScenarioMapDraft.Country[] _filtered;
    private int _owner, _inspectedProvince;
    private Button _save;

    private static Label Caption(string text) => new() { Text = Loc.Get(text), AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private static Label FieldCaption(string text) => new() {
        Text = Loc.Get(text), AutowrapMode = TextServer.AutowrapMode.WordSmart,
        CustomMinimumSize = new Vector2(Math.Clamp(InterfaceSkin.Metric("map_editor.field_label_width", 160), 90, 280), 0)
    };
    private static Button ActionButton(string id, string text, Action action)
    {
        var button = new Button { Name = id, Text = Loc.Get(text), CustomMinimumSize = new Vector2(0, 34) };
        DealOrForceGame.StyleButton(button, false);
        button.Pressed += action; return button;
    }
    public override void _Ready()
    {
        GameCursorService.Ensure(this);
        Theme = ScenarioEditorTheme.Create();
        if (Draft is null || Topology is null)
        {
            AddChild(Caption("Open this editor from Scenarios & workshop after the map loads.")); return;
        }
        var rows = new VBoxContainer { Name = "EditorRows" }; AddChild(rows); rows.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        rows.AddThemeConstantOverride("separation", 8);
        _scenarioSummary = Caption(""); _scenarioSummary.Name = "ScenarioSummary"; rows.AddChild(_scenarioSummary);
        _scenarioSummary.AddThemeColorOverride("font_color", Ui.Colour("text.accent"));
        var toolbar = new HFlowContainer { Name = "MapTools" }; rows.AddChild(toolbar);
        _select = ActionButton("MapSelect", "Select province", () => { _map.FinishStroke(); _map.PaintMode = false; RefreshStatus(); }); toolbar.AddChild(_select);
        _paint = ActionButton("MapPaint", "Paint ownership", () => { _map.PaintMode = true; RefreshStatus(); }); toolbar.AddChild(_paint);
        _undo = ActionButton("MapUndo", "Undo", () => { _map.FinishStroke(); Draft.Undo(); RefreshMap(); }); toolbar.AddChild(_undo);
        _redo = ActionButton("MapRedo", "Redo", () => { _map.FinishStroke(); Draft.Redo(); RefreshMap(); }); toolbar.AddChild(_redo);
        toolbar.AddChild(ActionButton("MapFit", "Fit world", () => _map.Fit()));
        toolbar.AddChild(ActionButton("MapFocus", "Focus target country", () => _map.FocusCountry(_owner)));
        toolbar.AddChild(ActionButton("MapSettings", "Scenario settings…", EditSettings));
        toolbar.AddChild(ActionButton("MapHelp", "Controls & scope", ShowHelp));
        var columns = new HSplitContainer { Name = "MapColumns", SizeFlagsVertical = SizeFlags.ExpandFill,
            SplitOffsets = new[] { (int)Math.Clamp(InterfaceSkin.Metric("map_editor.sidebar_width", 260), 200, 420) } };
        rows.AddChild(columns);
        var inspectorScroll = new ScrollContainer { Name = "MapInspectorScroll", CustomMinimumSize = new Vector2(200, 0),
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, SizeFlagsVertical = SizeFlags.ExpandFill }; columns.AddChild(inspectorScroll);
        var sidebar = new VBoxContainer { Name = "MapInspector", SizeFlagsHorizontal = SizeFlags.ExpandFill }; inspectorScroll.AddChild(sidebar);
        sidebar.AddThemeConstantOverride("separation", 6);
        sidebar.AddChild(Caption("PAINT TARGET"));
        sidebar.AddChild(Caption("Choose where painted provinces will belong."));
        var search = _search = new LineEdit { Name = "MapCountrySearch", PlaceholderText = Loc.Get("Find country…"), ClearButtonEnabled = true }; sidebar.AddChild(search);
        _countries = new ItemList { Name = "MapCountries", CustomMinimumSize = new Vector2(200,
            (float)Math.Clamp(InterfaceSkin.Metric("map_editor.country_list_height", 150), 90, 320)) }; sidebar.AddChild(_countries);
        search.TextChanged += FilterCountries;
        _countries.ItemSelected += i => ChooseOwner(_filtered[(int)i].MapIndex);
        sidebar.AddChild(Caption("Target region"));
        _regions = new OptionButton { Name = "MapRegions", FitToLongestItem = false }; sidebar.AddChild(_regions);
        _regions.ItemSelected += i => { _map.FinishStroke(); _map.TargetRegion = _regions.GetItemId((int)i); RefreshStatus(); };
        sidebar.AddChild(ActionButton("EditScenarioCountry", "Edit target country…", () => EditEntity(true)));
        sidebar.AddChild(ActionButton("EditScenarioRegion", "Edit target region…", () => EditEntity(false)));
        sidebar.AddChild(new HSeparator());
        sidebar.AddChild(Caption("SELECTED TERRITORY"));
        _selection = Caption("Select a province on the map to inspect it."); _selection.Name = "MapSelection"; sidebar.AddChild(_selection);
        _sample = ActionButton("SampleProvinceTarget", "Use selection as paint target", UseSelectedTarget); sidebar.AddChild(_sample);
        _editSelectedCountry = ActionButton("EditSelectedCountry", "Edit selected country…", () => EditEntity(true, Draft.OwnerAt(_inspectedProvince))); sidebar.AddChild(_editSelectedCountry);
        _editSelectedRegion = ActionButton("EditSelectedRegion", "Edit selected region…", () => EditEntity(false, Draft.StateAt(_inspectedProvince))); sidebar.AddChild(_editSelectedRegion);
        _map = new ScenarioMapCanvas { Name = "PoliticalMapCanvas", Draft = Draft, Topology = Topology,
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(240, 200) };
        _map.Picked = Inspect; _map.Changed = RefreshStatus; columns.AddChild(_map);
        // Flow layout gives an autowrapped label its near-zero minimum width, making the
        // caption thousands of pixels tall and starving the map of vertical space.
        // Separate rows allocate the caption the full editor width at every resolution.
        var saveRow = new VBoxContainer { Name = "MapSaveRow" }; rows.AddChild(saveRow);
        var save = _save = ActionButton("MapSave", "Save scenario to mod", Save); DealOrForceGame.StyleButton(save, true); saveRow.AddChild(save);
        save.SizeFlagsHorizontal = SizeFlags.ShrinkBegin;
        saveRow.AddChild(Caption("Political map editor — independent draft. The current campaign is unchanged."));
        var destination = Caption(Draft.HasSavedVersion ? "Editing a saved workspace scenario. Saving updates this scenario with backups." :
            "New independent copy. Saving creates a new scenario; the source is not overwritten.");
        destination.Name = "ScenarioSaveDestination"; destination.TooltipText = ModRoot + "/scenarios/" + Draft.Id;
        saveRow.AddChild(destination);
        _status = Caption(""); _status.Name = "MapEditorStatus"; rows.AddChild(_status);
        FilterCountries("");
        if (Draft.Countries.Length > 0) ChooseOwner(Draft.Countries[0].MapIndex);
        RefreshStatus();
    }
    private void EditEntity(bool country, int mapIndex = -1)
    {
        _map.FinishStroke();
        var id = mapIndex < 0 ? country ? _owner : _map.TargetRegion : mapIndex;
        if (id <= 0) { _status.Text = Loc.Get("Choose a target country and region first."); return; }
        var source = JsonNode.Parse(Draft.ReadEntity(country, id)).AsObject();
        var original = source.DeepClone().AsObject();
        var (window, rows) = EditorDialog(country ? "CountryInspector" : "RegionInspector", country ? "Scenario country" : "Scenario region", 760, 680);
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill }; rows.AddChild(tabs);
        var propertiesScroll = new ScrollContainer { Name = "Properties", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled }; tabs.AddChild(propertiesScroll);
        var properties = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill }; propertiesScroll.AddChild(properties);
        properties.AddThemeConstantOverride("separation", 8);
        tabs.SetTabTitle(0, Loc.Get("Properties"));
        properties.AddChild(Caption("Edits apply only to this draft. Identity IDs and country ownership links are protected."));
        properties.AddChild(Caption("Name"));
        var name = new LineEdit { Name = "EntityName", Text = source["Name"].GetValue<string>() }; properties.AddChild(name);
        ColorPickerButton colour = null; OptionButton government = null, ideology = null;
        TextureRect flag = null;
        Nation previewNation = null; Campaign previewWorld = null;
        Action refreshFlag = null;
        if (country)
        {
            properties.AddChild(Caption("Map colour"));
            colour = new ColorPickerButton { Name = "EntityMapColour", Color = Color.FromString(source["Color"].GetValue<string>(), Colors.Gray), EditAlpha = false, CustomMinimumSize = new Vector2(0, 34) }; properties.AddChild(colour);
            properties.AddChild(Caption("Form of government")); government = new OptionButton { Name = "EntityGovernment" }; properties.AddChild(government);
            foreach (var g in GovernmentCatalog.All) { government.AddItem(Loc.Get(g.Name)); government.SetItemMetadata(government.ItemCount - 1, g.Id); if (g.Id == source["Government"]?.GetValue<string>()) government.Select(government.ItemCount - 1); }
            properties.AddChild(Caption("Ideology")); ideology = new OptionButton { Name = "EntityIdeology" }; properties.AddChild(ideology);
            foreach (var g in IdeologyCatalog.All) { ideology.AddItem(Loc.Get(g.Name)); ideology.SetItemMetadata(ideology.ItemCount - 1, g.Id); if (g.Id == source["Ideology"]?.GetValue<string>()) ideology.Select(ideology.ItemCount - 1); }
            var flagRow = new HBoxContainer(); properties.AddChild(flagRow);
            flag = new TextureRect { Name = "ScenarioFlagPreview", CustomMinimumSize = new Vector2(64, 42), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered }; flagRow.AddChild(flag);
            var flagCaption = Caption("Flag in this scenario"); flagCaption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
            flagRow.AddChild(flagCaption);
            if (OpenFlagEditor is not null)
            {
                var tag = Draft.Countries.FirstOrDefault(c => c.MapIndex == id)?.Tag ?? "";
                flagRow.AddChild(ActionButton("EditScenarioFlag", "Edit flag…", () => OpenFlagEditor(tag)));
                // The flag editor saves a design that is flown at once; show it here on return.
                window.FocusEntered += () => refreshFlag?.Invoke();
            }
            previewWorld = SaveSystem.ImportScenarioJson(Draft.Export(), Draft.Id, Draft.ProvinceCount);
            previewNation = previewWorld.Nations.First(n => n.MapIndex == id);
            void RefreshFlag()
            {
                var sprite = FlagCatalog.ResolvePreview(previewWorld, previewNation, government.GetSelectedMetadata().AsString(), ideology.GetSelectedMetadata().AsString());
                flag.Texture = DealOrForceGame.FlagSpriteTexture(sprite);
                flag.TooltipText = Loc.Get(sprite.Length == 0 ? "No flag available" : "Flag in this scenario") + (sprite.Length == 0 ? "" : "\n" + sprite);
            }
            refreshFlag = RefreshFlag;
            government.ItemSelected += _ => RefreshFlag(); ideology.ItemSelected += _ => RefreshFlag(); RefreshFlag();
        }
        var numbers = new Dictionary<string, (ScenarioInspectorCatalog.Field Field, SpinBox Control)>();
        var changedNumbers = new HashSet<string>(); var syncing = false;
        var grid = new GridContainer { Columns = 2 }; properties.AddChild(grid);
        grid.AddThemeConstantOverride("h_separation", 16); grid.AddThemeConstantOverride("v_separation", 6);
        foreach (var field in ScenarioInspectorCatalog.Fields(country).Where(f => source[f.Key] is not null))
        {
            grid.AddChild(FieldCaption(field.Label));
            var numeric = new SpinBox { Name = "Entity_" + field.Key, MinValue = field.Min, MaxValue = field.Max, Step = field.Step,
                AllowGreater = true, AllowLesser = true, Value = source[field.Key].GetValue<double>() * field.Scale,
                SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(150, 34) };
            numeric.TooltipText = field.Key + (field.Scale == 1 ? "" : $" · {Loc.Get("Stored value")} × {field.Scale}");
            grid.AddChild(numeric); numbers[field.Key] = (field, numeric);
            numeric.ValueChanged += _ => { if (!syncing) changedNumbers.Add(field.Key); };
        }
        properties.AddChild(Caption("Population figures do not redistribute population groups or recalculate the economy. Review those records in JSON."));
        properties.AddChild(Caption("Advanced JSON exposes all stored fields, including flags, population, economy and research. Switching tabs keeps the data in sync."));
        var code = new CodeEdit { Name = "JSON", Text = source.ToJsonString(new JsonSerializerOptions { WriteIndented = true }), GuttersDrawLineNumbers = true, SizeFlagsVertical = SizeFlags.ExpandFill }; tabs.AddChild(code);
        var error = Caption(""); rows.AddChild(error);
        void MergeFields()
        {
            var updated = source.DeepClone().AsObject();
            updated["Name"] = name.Text.Trim();
            // Untouched values must retain their original precision, especially population and mod fields.
            foreach (var key in changedNumbers)
            {
                var (field, box) = numbers[key];
                if (key == "Population") updated[key] = checked((long)Math.Round(box.Value / field.Scale));
                else updated[key] = box.Value / field.Scale;
            }
            if (country)
            {
                updated["Color"] = "#" + colour.Color.ToHtml(false);
                updated["Government"] = government.GetSelectedMetadata().AsString();
                updated["Ideology"] = ideology.GetSelectedMetadata().AsString();
            }
            source = updated; changedNumbers.Clear();
        }
        var reversingTransition = false;
        tabs.TabChanged += tab =>
        {
            // A failed JSON -> form transition returns without overwriting the user's invalid buffer.
            if (reversingTransition) { reversingTransition = false; return; }
            try
            {
                if (tab == 1) { MergeFields(); code.Text = source.ToJsonString(new JsonSerializerOptions { WriteIndented = true }); }
                else
                {
                    var parsed = ScenarioEntityDocument.Parse(code.Text, country);
                    ScenarioEntityDocument.ProtectStoredFields(original, parsed, country);
                    source = parsed;
                    name.Text = source["Name"].GetValue<string>();
                    syncing = true;
                    foreach (var (key, numeric) in numbers) numeric.Control.Value = source[key].GetValue<double>() * numeric.Field.Scale;
                    syncing = false; changedNumbers.Clear();
                    if (country)
                    {
                        colour.Color = Color.FromString(source["Color"].GetValue<string>(), Colors.Gray);
                        foreach (var option in new[] { (government, "Government"), (ideology, "Ideology") })
                            for (var i = 0; i < option.Item1.ItemCount; i++) if (option.Item1.GetItemMetadata(i).AsString() == source[option.Item2]?.GetValue<string>()) option.Item1.Select(i);
                        previewNation.FormedNationId = source["FormedNationId"]?.GetValue<string>() ?? "";
                        refreshFlag?.Invoke();
                    }
                }
                error.Text = "";
            }
            catch (Exception e)
            {
                syncing = false;
                error.Text = Loc.Get("Could not switch tabs. Your input is preserved.") + " " + e.Message;
                reversingTransition = true; tabs.CallDeferred("set_current_tab", tab == 0 ? 1 : 0);
            }
        };
        var actions = new HBoxContainer(); rows.AddChild(actions);
        actions.AddChild(ActionButton("ApplyScenarioEntity", "Apply to draft", () =>
        {
            try
            {
                if (tabs.CurrentTab == 0) MergeFields();
                Draft.ReplaceEntity(country, id, tabs.CurrentTab == 1 ? code.Text : source.ToJsonString());
                RefreshMap(); window.QueueFree();
            }
            catch (Exception e) { error.Text = Loc.Get("Could not apply changes") + ": " + e.Message; }
        }));
        actions.AddChild(ActionButton("CancelScenarioEntity", "Cancel", () => window.QueueFree()));
        window.PopupCentered();
    }
    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (Draft is null || input is not InputEventKey { Pressed: true, Echo: false } key || !(key.CtrlPressed || key.MetaPressed) || GetChildren().OfType<Window>().Any(w => w.Visible)) return;
        var letter = DealOrForceGame.Letter(key);
        if (letter == Key.S) Save();
        else if (letter == Key.Z && !key.ShiftPressed) { _map.FinishStroke(); Draft.Undo(); RefreshMap(); }
        else if (letter == Key.Y || letter == Key.Z && key.ShiftPressed) { _map.FinishStroke(); Draft.Redo(); RefreshMap(); }
        else return;
        GetViewport().SetInputAsHandled();
    }
    public bool CancelGesture() => _map?.CancelGesture() == true;
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
        _inspectedProvince = p;
        var owner = Draft.Countries.FirstOrDefault(c => c.MapIndex == Draft.OwnerAt(p));
        var region = Draft.Regions.FirstOrDefault(r => r.MapIndex == Draft.StateAt(p));
        var land = p > 0 && p <= Draft.ProvinceCount && !Topology.WaterByProvince[p];
        _selection.Text = p <= 0 ? Loc.Get("Select a province on the map to inspect it.") :
            $"{Loc.Get("Province")} #{p}\n{Loc.Get(owner?.Name ?? "Unclaimed")}\n{Loc.Get(region?.Name ?? "Unknown")}";
        _sample.Disabled = !land || owner is null || region is null || region.Owner != owner.MapIndex;
        _editSelectedCountry.Disabled = !land || owner is null;
        _editSelectedRegion.Disabled = !land || region is null;
    }
    private void UseSelectedTarget()
    {
        _map.FinishStroke();
        var owner = Draft.OwnerAt(_inspectedProvince); var region = Draft.StateAt(_inspectedProvince);
        if (!Draft.Regions.Any(r => r.MapIndex == region && r.Owner == owner)) return;
        _search.Text = ""; FilterCountries(""); ChooseOwner(owner);
        for (var i = 0; i < _regions.ItemCount; i++) if (_regions.GetItemId(i) == region) { _regions.Select(i); _map.TargetRegion = region; break; }
        // Sampling chooses the target; it never silently turns the paint tool on.
        RefreshStatus();
    }
    private void RefreshMap()
    {
        var region = _map.TargetRegion;
        FilterCountries(_search.Text); ChooseOwner(_owner);
        for (var i = 0; i < _regions.ItemCount; i++) if (_regions.GetItemId(i) == region) { _regions.Select(i); _map.TargetRegion = region; }
        _map.InvalidateMap(); RefreshStatus();
    }
    private void RefreshStatus()
    {
        if (_status is null) return;
        // Keep the inspector consistent after painting, undo and redo without another click.
        Inspect(_inspectedProvince);
        var settings = Draft.Settings;
        _scenarioSummary.Text = $"{Loc.Get(settings.Name)} · {new GameDate(settings.Year, settings.Month, settings.Day) { Hour = settings.Hour }.Display(true)} · {Loc.Get("World seed")} {settings.Seed}";
        _scenarioSummary.TooltipText = Draft.Id + "\n" + settings.Description;
        DealOrForceGame.StyleButton(_select, !_map.PaintMode);
        DealOrForceGame.StyleButton(_paint, _map.PaintMode);
        _undo.Disabled = !Draft.CanUndo; _redo.Disabled = !Draft.CanRedo;
        var c = Draft.Countries.FirstOrDefault(c => c.MapIndex == _owner);
        _status.Text = Loc.Get(_map.PaintMode ? "Paint ownership" : "Select province") + " · " + Loc.Get(c?.Name ?? "") + " · " +
            Loc.Get(Draft.Dirty || !Draft.HasSavedVersion ? "Unsaved draft" : "Saved") +
            (_map.TargetRegion == 0 ? " · " + Loc.Get("No region available for this country.") : "");
        _save.Text = Loc.Get(Draft.HasSavedVersion ? "Save scenario changes" : "Save new scenario copy");
    }
    private void Save()
    {
        _map.FinishStroke();
        try
        {
            var path = Draft.Save(ModRoot); RefreshStatus(); _status.Text += "\n" + path;
            (GetNode("EditorRows/MapSaveRow/ScenarioSaveDestination") as Label).Text = Loc.Get("Editing a saved workspace scenario. Saving updates this scenario with backups.");
        }
        catch (Exception e) { _status.Text = Loc.Get("Could not save scenario") + ": " + e.Message; }
    }
    public void RequestClose()
    {
        _map?.FinishStroke();
        if (Draft is null || !Draft.Dirty && Draft.HasSavedVersion) { ExitRequested?.Invoke(); return; }
        if (GetChildren().OfType<ConfirmationDialog>().Any(d => d.Visible)) return;
        var dialog = new ConfirmationDialog { Name = "DiscardMapDraft", Title = Loc.Get("Unsaved changes"), DialogText = Loc.Get("Discard unsaved changes?"), Exclusive = true };
        AddChild(dialog); dialog.Confirmed += () => { dialog.QueueFree(); ExitRequested?.Invoke(); }; dialog.Canceled += () => dialog.QueueFree(); dialog.PopupCentered();
    }
}
