using System;
using System.IO;
using System.Linq;
using System.Collections.Generic;
using System.Text.Json;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private CanvasLayer _authorCanvas;
    private CodeEdit _authorCode;
    private Tree _authorTree;
    private Label _authorStatus;
    private string _authorSaved = "", _authorRelative = "";
    private bool _authorDirty;
    private string AuthorRoot => ProjectSettings.GlobalizePath("user://mods/editor_workspace");
    private IEnumerable<string> ScenarioRoots => _modLoader.Packages.Select(p => p.PhysicalRoot);

    private VBoxContainer AuthorWindow(string heading)
    {
        CloseAuthorWindow();
        _campaign.IsPaused = true;
        _globe.LoadingInputBlocked = true;
        _authorCanvas = new CanvasLayer { Name = "ContentWorkshop", Layer = 60 };
        AddChild(_authorCanvas);
        var cover = FullLayer("WorkshopSurface");
        _authorCanvas.AddChild(cover);
        var panel = TexturedPanel(cover);
        Ui.Place(panel, "workshop.panel");
        var rows = PanelRows(panel, 20, 16, 10);
        var header = new HBoxContainer();
        rows.AddChild(header);
        var title = Title(heading, 25, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(ActionButton("Close", () => ConfirmAuthorExit(CloseAuthorWindow), 90, 36));
        return rows;
    }

    private void CloseAuthorWindow()
    {
        if (IsInstanceValid(_authorCanvas)) { RemoveChild(_authorCanvas); _authorCanvas.QueueFree(); _authorCanvas = null; }
        if (_globe is not null) _globe.LoadingInputBlocked = false;
        _authorDirty = false;
    }

    private void ConfirmAuthorExit(Action action)
    {
        if (!_authorDirty) { action(); return; }
        if (_authorCanvas.GetChildren().OfType<ConfirmationDialog>().Any(d => d.Visible)) return;
        var confirm = new ConfirmationDialog { Title = Tr("Unsaved changes"), DialogText = Tr("Discard unsaved changes?"), Exclusive = true };
        _authorCanvas.AddChild(confirm);
        confirm.Confirmed += () => { confirm.QueueFree(); _authorDirty = false; action(); };
        confirm.Canceled += () => confirm.QueueFree();
        confirm.PopupCentered();
    }

    private void OpenScenarioLibrary()
    {
        if (_networkConnected || _isLoading) return;
        var rows = AuthorWindow("Scenarios & workshop");
        rows.AddChild(Title("Each scenario owns its date, countries, armies, wars and province ownership.", 14, Ui.Colour("text.muted")));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        rows.AddChild(scroll);
        var cards = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("separation", 12);
        scroll.AddChild(cards);
        foreach (var entry in ScenarioLibrary.Discover(ScenarioRoots, Path.Combine(AuthorRoot, "scenarios")))
        {
            var card = new VBoxContainer();
            cards.AddChild(card);
            card.AddChild(Title(entry.Name, 21, Ui.Colour("text.bright")));
            var description = Title(entry.Description, 14, Ui.Colour("text.muted"));
            description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            card.AddChild(description);
            card.AddChild(Title(entry.Id, 12, Ui.Colour("text.caption")));
            card.AddChild(ActionButton("Choose country on map", () => LoadAuthoredScenario(entry), 260, 40, true));
        }
        var actions = new HBoxContainer();
        rows.AddChild(actions);
        actions.AddChild(ActionButton("Content editor", OpenContentEditor, 220, 42));
        var snapshot = ActionButton("Create scenario from this world", CreateScenarioCopy, 320, 42);
        snapshot.Disabled = _campaign.ProvinceMap is null;
        actions.AddChild(snapshot);
        rows.AddChild(Title("A copy keeps the current world's figures; changing the date alone does not create a historical scenario.", 13, Ui.Colour("text.caption")));
    }

    private async void LoadAuthoredScenario(ScenarioEntry entry)
    {
        if (_isLoading || _networkConnected) return;
        var success = await RunLoadingOperation("Loading scenario", async () =>
        {
            await LoadingPhase("Reading scenario", 15);
            var next = ScenarioLibrary.Create(entry, 20260830, _campaign.ProvinceMap?.ProvinceCount ?? 0);
            // Only the legacy contemporary bootstrap receives contemporary mod history.
            // A full world snapshot is already complete: never seed it with 2024 defaults.
            if (entry.Id == "2024" && entry.WorldFile.Length == 0) _modLoader.ApplyCampaignData(next);
            await LoadingPhase("Preparing map", 60);
            _campaign.Changed -= Refresh;
            _campaign = next;
            _campaign.Changed += Refresh;
            _campaignInProgress = false;
            _lastAutosaveDay = -1;
            _selectedNation = _pickerNation = next.Player;
            _selectedState = next.States.FirstOrDefault(s => s.NationMapIndex == next.Player.MapIndex);
            _modApi.Bind(next);
            BindFlags(next);
            _globe.BindCampaign(next);
            ResetTrends();
            ResetStrategicAudioState();
            ApplyAllSettings();
            next.IsPaused = true;
            Refresh();
            await LoadingPhase("Ready", 100);
        }, true);
        if (!success) return;
        CloseAuthorWindow();
        OpenCountryPicker();
    }

    private void CreateScenarioCopy()
    {
        if (_campaign.ProvinceMap is null) return;
        var rows = AuthorWindow("Create scenario");
        rows.AddChild(Title("An independent copy of the current world. Edit its contents in the workshop before publishing.", 14, Ui.Colour("text.muted")));
        var name = new LineEdit { Text = Tr("My scenario"), CustomMinimumSize = new Vector2(400, 40) };
        rows.AddChild(name);
        var result = Title("", 14, Ui.Colour("text.caption"));
        rows.AddChild(result);
        rows.AddChild(ActionButton("Create editable copy", () =>
        {
            try
            {
                var id = "custom_" + Guid.NewGuid().ToString("N")[..10];
                ContentWorkbench.EnsureMod(AuthorRoot);
                ContentWorkbench.Write(AuthorRoot, $"scenarios/{id}/world.json", SaveSystem.ExportScenarioJson(_campaign, id));
                ContentWorkbench.Write(AuthorRoot, $"scenarios/{id}/scenario.json", JsonSerializer.Serialize(new
                { id, name = name.Text, description = $"{_campaign.Date} · {_campaign.Nations.Count} · {_campaign.ScenarioId}", world = "world.json" }, new JsonSerializerOptions { WriteIndented = true }));
                OpenContentEditor();
                OpenAuthorFile($"scenarios/{id}/world.json", Path.Combine(AuthorRoot, "scenarios", id, "world.json"));
            }
            catch (Exception e) { result.Text = e.Message; }
        }, 270, 44, true));
    }

    private void OpenContentEditor()
    {
        var rows = AuthorWindow("Content editor");
        rows.AddChild(Title("Game data → your mod. The base game is never overwritten. Ctrl+Z / Ctrl+Y: undo / redo.", 14, Ui.Colour("text.muted")));
        var columns = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SplitOffsets = new[] { 280 } };
        rows.AddChild(columns);
        var browser = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        columns.AddChild(browser);
        var search = new LineEdit { PlaceholderText = Tr("Find a file…"), ClearButtonEnabled = true };
        browser.AddChild(search);
        var files = new ItemList { SizeFlagsVertical = SizeFlags.ExpandFill };
        browser.AddChild(files);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseRoot = GameDataLoader.DataFolder("");
        foreach (var root in new[] { baseRoot }.Concat(ScenarioRoots).Append(AuthorRoot))
        {
            if (root is null || !Directory.Exists(root)) continue;
            foreach (var category in ContentWorkbench.Roots)
            {
                var dir = Path.Combine(root, category);
                if (!Directory.Exists(dir)) continue;
                foreach (var f in Directory.EnumerateFiles(dir, "*.json", SearchOption.AllDirectories))
                    paths[Path.GetRelativePath(root, f).Replace('\\', '/')] = f;
            }
        }
        void Filter()
        {
            files.Clear();
            foreach (var path in paths.Keys.OrderBy(k => k).Where(k => k.Contains(search.Text, StringComparison.OrdinalIgnoreCase)))
            {
                var index = files.AddItem(path);
                files.SetItemTooltip(index, path + "\n" + paths[path]);
            }
        }
        search.TextChanged += _ => Filter();
        files.ItemSelected += index => ConfirmAuthorExit(() => OpenAuthorFile(files.GetItemText((int)index), paths[files.GetItemText((int)index)]));
        Filter();
        var editing = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddChild(editing);
        var tabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        editing.AddChild(tabs);
        var inspector = new VBoxContainer { Name = "Properties" };
        tabs.AddChild(inspector);
        tabs.SetTabTitle(0, Tr("Properties"));
        _authorTree = new Tree { Columns = 2, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        _authorTree.SetColumnTitle(0, Tr("Property")); _authorTree.SetColumnTitle(1, Tr("Value"));
        _authorTree.ColumnTitlesVisible = true;
        inspector.AddChild(_authorTree);
        var value = new LineEdit { PlaceholderText = Tr("JSON value: number, true, or a quoted string"), CustomMinimumSize = new Vector2(0, 36) };
        inspector.AddChild(value);
        _authorTree.ItemSelected += () =>
        {
            var item = _authorTree.GetSelected();
            if (item is not null) value.Text = item.GetMetadata(1).AsString();
        };
        inspector.AddChild(ActionButton("Apply property", () =>
        {
            try
            {
                var selected = _authorTree.GetSelected();
                if (selected is null) return;
                var updated = ContentWorkbench.SetValue(_authorCode.Text, selected.GetMetadata(0).AsString(), value.Text);
                _authorCode.BeginComplexOperation();
                _authorCode.SelectAll(); _authorCode.InsertTextAtCaret(updated);
                _authorCode.EndComplexOperation();
                RebuildAuthorTree();
            }
            catch (Exception e) { _authorStatus.Text = e.Message; }
        }, 200, 36));
        _authorCode = new CodeEdit { Name = "JSON", GuttersDrawLineNumbers = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        tabs.AddChild(_authorCode);
        _authorCode.TextChanged += () => { _authorDirty = _authorCode.Text != _authorSaved; };
        tabs.TabChanged += index => { if (index == 0) RebuildAuthorTree(); };
        var toolbar = new HBoxContainer();
        rows.AddChild(toolbar);
        toolbar.AddChild(ActionButton("Validate", () => AuthorOperation(false), 130, 38));
        toolbar.AddChild(ActionButton("Save to my mod", () => AuthorOperation(true), 190, 38, true));
        toolbar.AddChild(ActionButton("Scenarios", () => ConfirmAuthorExit(OpenScenarioLibrary), 140, 38));
        toolbar.AddChild(ActionButton("New document", NewAuthorDocument, 160, 38));
        toolbar.AddChild(ActionButton("Open mod folder", () => { ContentWorkbench.EnsureMod(AuthorRoot); OS.ShellOpen(AuthorRoot); }, 180, 38));
        _authorStatus = Title("Select a file. Changes to rules and UI take effect after restarting with Editor workspace enabled.", 13, Ui.Colour("text.caption"));
        _authorStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_authorStatus);
        _authorSaved = _authorRelative = "";
    }

    private void NewAuthorDocument()
    {
        ConfirmAuthorExit(() =>
        {
            var dialog = new ConfirmationDialog { Title = Tr("New document"), DialogText = Tr("Relative path inside the mod, ending in .json"), MinSize = new Vector2I(500, 150) };
            var path = new LineEdit { Text = "localization/russian/99_my_names.json", OffsetTop = 55, OffsetRight = 470, OffsetBottom = 90 };
            dialog.AddChild(path);
            _authorCanvas.AddChild(dialog);
            dialog.Confirmed += () =>
            {
                try
                {
                    var relative = path.Text.Replace('\\', '/');
                    ScenarioLibrary.SafePath(AuthorRoot, relative);
                    if (!ContentWorkbench.Roots.Contains(relative.Split('/')[0]) || !relative.EndsWith(".json", StringComparison.OrdinalIgnoreCase))
                        throw new InvalidDataException(Tr("Use a content folder and the .json extension."));
                    if (File.Exists(ScenarioLibrary.SafePath(AuthorRoot, relative)))
                        throw new InvalidDataException(Tr("This document already exists. Open it in the file list."));
                    _authorRelative = relative; _authorSaved = ""; _authorCode.Text = "{\n}\n"; _authorDirty = true;
                    _authorStatus.Text = relative; RebuildAuthorTree();
                }
                catch (Exception e) { _authorStatus.Text = e.Message; }
                dialog.QueueFree();
            };
            dialog.Canceled += () => dialog.QueueFree();
            dialog.PopupCentered();
        });
    }

    private void OpenAuthorFile(string relative, string path)
    {
        try
        {
            _authorRelative = relative;
            _authorSaved = File.ReadAllText(path);
            _authorCode.Text = _authorSaved;
            _authorCode.ClearUndoHistory();
            _authorDirty = false;
            _authorStatus.Text = relative;
            RebuildAuthorTree();
        }
        catch (Exception e) { _authorStatus.Text = e.Message; }
    }

    private void RebuildAuthorTree()
    {
        _authorTree.Clear();
        if (_authorCode.Text.Length == 0) return;
        try
        {
            using var doc = JsonDocument.Parse(_authorCode.Text, GameDataLoader.ForgivingJson);
            var root = _authorTree.CreateItem();
            var count = 0;
            void Add(JsonElement element, TreeItem parent, string key, string pointer, int depth)
            {
                if (++count > 4000) return;
                var item = _authorTree.CreateItem(parent);
                item.SetText(0, key);
                item.SetMetadata(0, pointer);
                var raw = element.GetRawText();
                var container = element.ValueKind is JsonValueKind.Object or JsonValueKind.Array;
                item.SetMetadata(1, container ? "" : raw);
                item.SetText(1, container ? element.ValueKind == JsonValueKind.Array ? $"[{element.GetArrayLength()}]" : "{…}" : raw);
                item.Collapsed = depth > 0;
                if (depth >= 6) return;
                if (element.ValueKind == JsonValueKind.Object)
                    foreach (var p in element.EnumerateObject()) Add(p.Value, item, p.Name, pointer + "/" + p.Name.Replace("~", "~0").Replace("/", "~1"), depth + 1);
                else if (element.ValueKind == JsonValueKind.Array)
                {
                    var index = 0;
                    foreach (var e in element.EnumerateArray().Take(100)) { Add(e, item, index.ToString(), pointer + "/" + index, depth + 1); index++; }
                }
            }
            if (doc.RootElement.ValueKind == JsonValueKind.Object)
                foreach (var p in doc.RootElement.EnumerateObject()) Add(p.Value, root, p.Name, "/" + p.Name.Replace("~", "~0").Replace("/", "~1"), 0);
            _authorTree.TooltipText = Tr("Property preview: first 100 array entries, 6 levels. The JSON tab exposes the complete document.");
        }
        catch (Exception e) { _authorStatus.Text = e.Message; }
    }

    private void AuthorOperation(bool save)
    {
        if (string.IsNullOrEmpty(_authorRelative)) return;
        try
        {
            _authorStatus.Text = ContentWorkbench.Validate(_authorRelative, _authorCode.Text);
            if (!save) return;
            ContentWorkbench.EnsureMod(AuthorRoot);
            var path = ContentWorkbench.Write(AuthorRoot, _authorRelative, _authorCode.Text);
            _authorSaved = _authorCode.Text; _authorDirty = false;
            _authorStatus.Text = Tr("Saved. Previous version: .bak. Enable Editor workspace and restart for rules/UI changes.") + " " + path;
        }
        catch (Exception e) { _authorStatus.Text = e.Message; }
    }
}
