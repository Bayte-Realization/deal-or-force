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
    private VisualLayoutEditor _authorLayout;
    private TabContainer _authorTabs;
    private Label _authorStatus;
    private string _authorSaved = "", _authorRelative = "";
    private bool _authorDirty;
    private ScenarioEntry _pickerScenarioEntry;
    private string AuthorRoot => ProjectSettings.GlobalizePath("user://mods/editor_workspace");
    private IEnumerable<string> ScenarioRoots => _modLoader.Packages.Select(p => p.PhysicalRoot);

    /// <summary>
    /// Where this window's way out leads. Set when the window is opened.
    ///
    /// The workshop is reached from two places and they are not the same place to go back
    /// to. Opened from a running campaign it closes onto that campaign; opened to CHOOSE a
    /// country there is no campaign behind it, and closing onto one was the bug - the window
    /// went away and left a globe with no interface on it, because the interface belongs to
    /// a campaign that had not been started.
    /// </summary>
    private Action _authorExit;

    /// <summary>
    /// The way out of the workshop as a whole, kept while one of its windows opens another.
    ///
    /// Routing the library's exit to the main menu fixed the library and nothing else: the
    /// content editor and the scenario copier are reached THROUGH the library and were opened
    /// without a way out of their own, so they fell back on closing the canvas and leaving
    /// whatever was behind it. Reached from the country picker, that is the same globe with no
    /// interface on it as before, one level deeper. A window opened from inside another now
    /// inherits where that one was going to go.
    /// </summary>
    private Action _authorHomeExit;

    /// <summary>Whether the campaign was already paused when the workshop opened.</summary>
    private bool _authorPausedBefore;

    private VBoxContainer AuthorWindow(string heading, Action onClose = null, string closeLabel = "Close")
    {
        var inheritedExit = onClose ?? _authorHomeExit;
        var pausedBefore = _authorCanvas is null
            ? _campaign is not null && _campaign.IsPaused
            : _authorPausedBefore;
        // Not restoring the clock here: this close is only making room for the window about
        // to replace it, and the line below pauses again immediately.
        CloseAuthorWindow(restoreClock: false);
        _authorHomeExit = inheritedExit;
        _authorPausedBefore = pausedBefore;
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
        _authorExit = inheritedExit ?? (() => CloseAuthorWindow());
        header.AddChild(ActionButton(closeLabel, () => ConfirmAuthorExit(_authorExit), 200, 36));
        return rows;
    }

    /// <param name="restoreClock">
    /// Whether to put the campaign clock back the way the workshop found it. False where the
    /// caller is about to set the clock itself - opening another window, or arriving on a
    /// campaign it has just loaded and deliberately paused.
    /// </param>
    private void CloseAuthorWindow(bool restoreClock = true)
    {
        if (IsInstanceValid(_authorCanvas)) { RemoveChild(_authorCanvas); _authorCanvas.QueueFree(); _authorCanvas = null; }
        if (_globe is not null) _globe.LoadingInputBlocked = false;
        _authorDirty = false;
        _authorExit = null;
        _authorHomeExit = null;
        // The workshop pauses the campaign to open, and used to leave it that way: the world
        // stood still afterwards while the pause button said it was running, because the
        // button follows a flag that had been set behind its back. Whatever the clock was
        // doing before is what it goes back to.
        if (restoreClock && _campaign is not null && !_authorPausedBefore)
        {
            _campaign.IsPaused = false;
            if (GodotObject.IsInstanceValid(_pause))
            {
                _pause.Text = "II";
                StyleButton(_pause, false);
            }
            UpdateSpeedButtons();
        }
        _authorPausedBefore = false;
    }

    /// <summary>Whether the workshop is up, so escape knows it is the thing to answer to.</summary>
    private bool AuthorWindowOpen => IsInstanceValid(_authorCanvas);

    /// <summary>Escape takes the window's own way out, whichever one that is.</summary>
    private void EscapeAuthorWindow()
    {
        if (IsInstanceValid(_authorLayout) && _authorLayout.CancelGesture()) return;
        ConfirmAuthorExit(_authorExit ?? (() => CloseAuthorWindow()));
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
        // Opened with no campaign behind it, so its way out is the front door rather than a
        // world that does not exist yet.
        var rows = AuthorWindow("Scenarios & workshop",
            () => { CloseAuthorWindow(); ShowMainMenu(); }, "Back to the main menu");
        rows.AddChild(Title("Each scenario owns its date, countries, armies, wars and province ownership.", 14, Ui.Colour("text.muted")));
        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        rows.AddChild(scroll);
        var cards = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(cards);

        // Each scenario is a card that is chosen by clicking it, lit while it is the one
        // chosen, and started by the one button underneath - or by a double click, the way a
        // list of saves or servers works anywhere else. They were three lines of text with a
        // start button apiece, which with one scenario looked like a heading and with several
        // would have been a column of identical buttons with the thing that tells them apart
        // written above each one.
        var entries = ScenarioLibrary.Discover(ScenarioRoots, Path.Combine(AuthorRoot, "scenarios")).ToList();
        var pickedCards = new List<(PanelContainer Card, ScenarioEntry Entry)>();
        ScenarioEntry chosen = null;
        var start = ActionButton("Choose country on map", () => { if (chosen is not null) LoadAuthoredScenario(chosen); }, 300, 42, true);

        void Paint(PanelContainer card, ScenarioEntry entry, bool hovered) =>
            card.AddThemeStyleboxOverride("panel", ButtonStyle(entry == chosen, hovered ? 1 : 0));

        void Choose(ScenarioEntry entry)
        {
            chosen = entry;
            foreach (var (card, candidate) in pickedCards) Paint(card, candidate, false);
            start.Disabled = chosen is null;
        }

        foreach (var entry in entries)
        {
            var card = new PanelContainer
            {
                SizeFlagsHorizontal = SizeFlags.ExpandFill,
                MouseFilter = MouseFilterEnum.Stop,
                MouseDefaultCursorShape = CursorShape.PointingHand,
                TooltipText = Ui.Tip(Tr("Click to choose this scenario, double-click to start it."))
            };
            cards.AddChild(card);
            var margin = new MarginContainer { MouseFilter = MouseFilterEnum.Ignore };
            foreach (var side in new[] { "left", "right" }) margin.AddThemeConstantOverride("margin_" + side, 16);
            foreach (var side in new[] { "top", "bottom" }) margin.AddThemeConstantOverride("margin_" + side, 10);
            card.AddChild(margin);
            var lines = new VBoxContainer { MouseFilter = MouseFilterEnum.Ignore };
            lines.AddThemeConstantOverride("separation", 3);
            margin.AddChild(lines);
            var name = Title(entry.Name, 21, Ui.Colour("text.bright"));
            var description = Title(entry.Description, 14, Ui.Colour("text.muted"));
            description.AutowrapMode = TextServer.AutowrapMode.WordSmart;
            var id = Title(entry.Id, 12, Ui.Colour("text.caption"));
            foreach (var label in new[] { name, description, id })
            {
                label.MouseFilter = MouseFilterEnum.Ignore;
                lines.AddChild(label);
            }

            var captured = entry;
            card.MouseEntered += () => Paint(card, captured, true);
            card.MouseExited += () => Paint(card, captured, false);
            card.GuiInput += input =>
            {
                if (input is not InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left } click) return;
                Choose(captured);
                if (click.DoubleClick) LoadAuthoredScenario(captured);
                card.AcceptEvent();
            };
            pickedCards.Add((card, entry));
        }
        Choose(entries.FirstOrDefault());
        rows.AddChild(start);

        var actions = new HFlowContainer();
        rows.AddChild(actions);
        actions.AddChild(ActionButton("Content editor", OpenContentEditor, 220, 42));
        actions.AddChild(ActionButton("UI layout editor", OpenVisualLayoutEditor, 220, 42));
        var snapshot = ActionButton("Create scenario from this world", CreateScenarioCopy, 320, 42);
        snapshot.Disabled = _campaign.ProvinceMap is null;
        actions.AddChild(snapshot);
        var mapEditor = ActionButton("Edit current world on map", OpenScenarioMapEditor, 270, 42);
        mapEditor.Disabled = _campaign.ProvinceMap is null;
        actions.AddChild(mapEditor);
        rows.AddChild(Title("A copy keeps the current world's figures; changing the date alone does not create a historical scenario.", 13, Ui.Colour("text.caption")));
    }

    private async void LoadAuthoredScenario(ScenarioEntry entry)
    {
        if (_isLoading || _networkConnected) return;
        if (!await RebuildAuthoredScenarioWithLoading(entry)) return;
        // The campaign above was loaded and paused on purpose; the picker is what unpauses it.
        CloseAuthorWindow(restoreClock: false);
        OpenCountryPicker();
    }

    private System.Threading.Tasks.Task<bool> RebuildAuthoredScenarioWithLoading(ScenarioEntry entry) =>
        RunLoadingOperation("Loading scenario", async () =>
        {
            await LoadingPhase("Reading scenario", 15);
            var next = ScenarioLibrary.Create(entry, _campaignSeed, _campaign.ProvinceMap?.ProvinceCount ?? 0);
            // Only the legacy contemporary bootstrap receives contemporary mod history.
            // A full world snapshot is already complete: never seed it with 2024 defaults.
            if (entry.Id == "2024" && entry.WorldFile.Length == 0) _modLoader.ApplyCampaignData(next);
            await LoadingPhase("Preparing map", 60);
            _campaign.Changed -= Refresh;
            _campaign = next;
            _pickerScenarioEntry = entry.Id == "2024" && entry.WorldFile.Length == 0 ? null : entry;
            _campaignSeed = next.Seed;
            if (_pickerSeed is not null) _pickerSeed.Text = _campaignSeed.ToString();
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
        var tabs = _authorTabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
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
        _authorLayout = new VisualLayoutEditor
        {
            Name = "Layout", ReadDocument = () => _authorCode.Text,
            IsLayoutDocument = () => _authorRelative.StartsWith("gui/", StringComparison.OrdinalIgnoreCase),
            ReplaceDocument = ReplaceAuthorDocument, UndoDocument = () => _authorCode.Undo(), RedoDocument = () => _authorCode.Redo()
        };
        tabs.AddChild(_authorLayout);
        tabs.SetTabTitle(2, Tr("Layout"));
        tabs.TabChanged += index => { if (index == 0) RebuildAuthorTree(); if (index == 2) _authorLayout.RefreshDocument(); };
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

    private void ReplaceAuthorDocument(string json)
    {
        if (json == _authorCode.Text) return;
        _authorCode.BeginComplexOperation();
        _authorCode.SelectAll(); _authorCode.InsertTextAtCaret(json);
        _authorCode.EndComplexOperation();
        _authorDirty = _authorCode.Text != _authorSaved;
    }

    private void OpenVisualLayoutEditor()
    {
        OpenContentEditor();
        const string relative = "gui/99_editor_layout.json";
        var path = Path.Combine(AuthorRoot, relative);
        if (File.Exists(path)) OpenAuthorFile(relative, path);
        else
        {
            _authorRelative = relative; _authorSaved = "";
            _authorCode.Text = "{\n  \"layout\": {}\n}\n";
            _authorCode.ClearUndoHistory(); _authorDirty = true;
            _authorStatus.Text = relative;
        }
        _authorTabs.CurrentTab = 2;
        _authorLayout.RefreshDocument();
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
                    _authorStatus.Text = relative; RebuildAuthorTree(); _authorLayout.RefreshDocument();
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
            _authorLayout.RefreshDocument();
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
