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
    private Label _authorDocumentHeading, _authorFormatBadge;
    private string _authorSaved = "", _authorRelative = "";
    private string _authorSourcePath = "";
    // Only the explicit development probe redirects authoring; never use a player's mod as a fixture.
    private string _authorVerificationRoot;
    private bool _authorDirty;
    private ScenarioEntry _pickerScenarioEntry;
    private string _authorScenarioId = "";
    private Action _authorRefreshFiles;
    private ScenarioMapEditor _authorMap;
    private string _authorSelectedRoot;
    private string AuthorRoot => _authorSelectedRoot ?? _authorVerificationRoot ?? Path.Combine(GamePaths.Mods, "editor_workspace");
    private string AuthorModsRoot => _authorVerificationRoot is null ? GamePaths.Mods : Path.Combine(_authorVerificationRoot, "packages");
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

    /// <summary>Whether the campaign was already paused when the workshop opened.</summary>
    private bool _authorPausedBefore;

    private VBoxContainer AuthorWindow(string heading, Action onClose = null, string closeLabel = "Close", Action reopen = null, bool readable = false, bool scenarioLibrary = false)
    {
        var pausedBefore = _authorCanvas is null
            ? _campaign is not null && _campaign.IsPaused
            : _authorPausedBefore;
        // Not restoring the clock here: this close is only making room for the window about
        // to replace it, and the line below pauses again immediately.
        CloseAuthorWindow(restoreClock: false);
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
        // Navigation is explicit, not inherited from the window being replaced.
        // Otherwise a child of the library inherits "main menu" as its Close action,
        // while the same editor opened from a campaign only hides its canvas.
        _authorExit = onClose ?? OpenScenarioLibrary;
        if (!scenarioLibrary && onClose is not null && closeLabel == "Back")
        {
            var previous = ActionButton("Back to previous editor", () => ConfirmAuthorExit(onClose), 200, 36);
            previous.Name = "WorkshopPreviousEditor";
            header.AddChild(previous);
        }
        Action scenariosExit = !scenarioLibrary && closeLabel != "Back to scenarios" ? OpenScenarioLibrary : _authorExit;
        var navigation = ActionButton(scenarioLibrary ? closeLabel : "Back to scenarios", () => ConfirmAuthorExit(scenariosExit), 220, 36);
        navigation.Name = scenarioLibrary ? "WorkshopMainMenu" : "WorkshopScenarios";
        header.AddChild(navigation);
        AddAuthorPackageBar(rows, reopen ?? OpenScenarioLibrary);
        if (readable)
            panel.CallDeferred(Node.MethodName.AddChild, new DecisionDialogLayout { Name = "WorkshopReadability", Panel = panel, LayoutKey = "workshop.panel" });
        return rows;
    }

    /// <param name="restoreClock">
    /// Whether to put the campaign clock back the way the workshop found it. False where the
    /// caller is about to set the clock itself - opening another window, or arriving on a
    /// campaign it has just loaded and deliberately paused.
    /// </param>
    private void CloseAuthorWindow(bool restoreClock = true)
    {
        var hadWindow = IsInstanceValid(_authorCanvas);
        if (hadWindow) { RemoveChild(_authorCanvas); _authorCanvas.QueueFree(); _authorCanvas = null; }
        if (_globe is not null) _globe.LoadingInputBlocked = false;
        _authorDirty = false;
        _authorExit = null;
        _authorRefreshFiles = null; _authorMap = null;
        _authorCode = null; _authorTree = null; _authorLayout = null; _authorTabs = null;
        _authorStatus = null; _authorDocumentHeading = null; _authorFormatBadge = null;
        // The workshop pauses the campaign to open, and used to leave it that way: the world
        // stood still afterwards while the pause button said it was running, because the
        // button follows a flag that had been set behind its back. Whatever the clock was
        // doing before is what it goes back to.
        if (restoreClock && hadWindow && _campaign is not null && !_authorPausedBefore)
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
        if (IsInstanceValid(_authorMap) && _authorMap.CancelGesture()) return;
        if (IsInstanceValid(_authorLayout) && _authorLayout.CancelGesture()) return;
        ConfirmAuthorExit(_authorExit ?? (() => CloseAuthorWindow()));
    }

    private void ConfirmAuthorExit(Action action)
    {
        // A fresh map copy has not been saved even if no brush stroke was made yet.
        // All header routes share one warning, rather than asking again inside RequestClose.
        var unsavedMap = IsInstanceValid(_authorMap) && _authorMap.Draft is { } draft && (draft.Dirty || !draft.HasSavedVersion);
        if (!_authorDirty && !unsavedMap) { action(); return; }
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
            () => { CloseAuthorWindow(); ShowMainMenu(); }, "Back to the main menu", scenarioLibrary: true);
        rows.AddChild(Title("Each scenario owns its date, countries, armies, wars and province ownership.", 14, Ui.Colour("text.muted")));
        // Two columns: the scenarios, and what the chosen one actually is.
        //
        // The list used to be a scroll container stretched to fill the panel, which with one
        // scenario in the world left about six hundred pixels of empty box under a heading
        // that promised "its date, countries, armies, wars and province ownership". A screen
        // that says what a thing contains and then shows a blank rectangle reads as broken.
        // The right-hand column is where that promise is kept, and it is also where the one
        // question this screen could not answer goes: WHY the map-editing button is greyed
        // out. That used to be a single hint line that said all three possible reasons at
        // once, whichever one was true.
        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 16);
        rows.AddChild(columns);

        var scroll = new ScrollContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SizeFlagsHorizontal = SizeFlags.ExpandFill };
        scroll.SizeFlagsStretchRatio = 1.1f;
        columns.AddChild(scroll);
        var cards = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        cards.AddThemeConstantOverride("separation", 8);
        scroll.AddChild(cards);

        var detailsCard = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        detailsCard.SizeFlagsStretchRatio = 1f;
        detailsCard.AddThemeStyleboxOverride("panel", ButtonStyle(false, 0));
        columns.AddChild(detailsCard);
        var details = PanelRows(detailsCard, 18, 16, 8);
        // The bookmark's picture, as a Paradox bookmark has one: the sprite scenario_<id> when a
        // scenario or a mod ships it, the night side of the Earth when it does not.
        var detailsPicture = new TextureRect
        {
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            CustomMinimumSize = new Vector2(0, 220),
            ClipContents = true,
            MouseFilter = MouseFilterEnum.Ignore
        };
        details.AddChild(detailsPicture);
        var detailsName = Title("", 21, Ui.Colour("text.bright"));
        detailsName.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        details.AddChild(detailsName);
        var detailsId = Title("", 13, Ui.Colour("text.caption"));
        details.AddChild(detailsId);
        var detailsDescription = Title("", 14, Ui.Colour("text.muted"));
        detailsDescription.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        details.AddChild(detailsDescription);
        details.AddChild(new HSeparator());
        var detailsSource = Title("", 13, Ui.Colour("text.muted"));
        detailsSource.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        details.AddChild(detailsSource);
        var detailsEdit = Title("", 13, Ui.Colour("text.muted"));
        detailsEdit.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        details.AddChild(detailsEdit);

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
        var openError = Title("", 14, Ui.Colour("text.caption")); openError.Name = "ScenarioOpenError";
        openError.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        var edit = ActionButton("Edit selected scenario on map", () => { if (chosen is not null) OpenAuthoredScenarioMapEditor(chosen, openError); }, 320, 42);
        edit.Name = "EditSelectedScenario";
        var editHint = Title("", 13, Ui.Colour("text.caption")); editHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;

        void Paint(PanelContainer card, ScenarioEntry entry, bool hovered) =>
            card.AddThemeStyleboxOverride("panel", ButtonStyle(entry == chosen, hovered ? 1 : 0));

        void Choose(ScenarioEntry entry)
        {
            chosen = entry;
            _authorScenarioId = entry?.Id ?? "";
            foreach (var (card, candidate) in pickedCards) Paint(card, candidate, false);
            start.Disabled = chosen is null;
            // The three reasons used to be one `||` with no way to tell which one held.
            var generated = chosen is null || string.IsNullOrEmpty(chosen.WorldFile);
            var noMap = _campaign is null || _campaign.ProvinceMap is null;
            edit.Disabled = chosen is null || noMap || generated;
            editHint.Text = Tr(edit.Disabled ? "Generated scenarios: use Edit current world on map to create a copy." :
                "Workspace scenarios save in place; base and mod scenarios become independent copies.");
            openError.Text = "";
            detailsName.Text = chosen?.Name ?? "";
            detailsPicture.Texture = chosen is null ? null : ScenarioPicture(chosen.Id);
            detailsPicture.Visible = detailsPicture.Texture is not null;
            detailsId.Text = chosen is null ? "" : Tr("Identifier") + ": " + chosen.Id;
            detailsDescription.Text = chosen?.Description ?? "";
            detailsSource.Text = chosen is null ? "" : Tr("Source") + ": " +
                (string.IsNullOrEmpty(chosen.WorldFile) ? Tr("Built into the game") : Tr("An authored world file"));
            detailsEdit.Text = chosen is null ? ""
                : generated ? Tr("Map editing: this scenario has no world file of its own. Use \"Edit current world on map\" to make a copy.")
                : noMap ? Tr("Map editing: waiting for the world to finish loading.")
                : Tr("Map editing: this scenario opens on the map and saves in place.");
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
            card.SetMeta("scenario_id", entry.Id);
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
        Choose(entries.FirstOrDefault(e => e.Id == _authorScenarioId) ?? entries.FirstOrDefault());
        var launch = new HFlowContainer(); rows.AddChild(launch);
        launch.AddChild(start); launch.AddChild(edit);
        rows.AddChild(editHint); rows.AddChild(openError);

        var actions = new HFlowContainer();
        rows.AddChild(actions);
        actions.AddChild(ActionButton("Content editor", OpenContentEditor, 220, 42));
        actions.AddChild(ActionButton("UI layout editor", OpenVisualLayoutEditor, 220, 42));
        // The two whole-thing editors: a country as one screen, and its flag as layers. Both
        // open on the country being looked at and come back here.
        var editedTag = (_selectedNation ?? _campaign.Player)?.Tag ?? "";
        actions.AddChild(ActionButton("Country editor", () => OpenCountryEditor(editedTag), 220, 42));
        actions.AddChild(ActionButton("Flag editor", () => OpenFlagEditor(editedTag), 200, 42));
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
        var rows = AuthorWindow("Create scenario", reopen: CreateScenarioCopy);
        rows.AddChild(Title("An independent copy of the current world. Edit its contents in the workshop before publishing.", 14, Ui.Colour("text.muted")));
        var name = new LineEdit { Text = Tr("My scenario"), CustomMinimumSize = new Vector2(400, 40) };
        name.TextChanged += _ => _authorDirty = true;
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
        var rows = AuthorWindow("Content editor", reopen: OpenContentEditor);
        rows.AddChild(Title("Game data → your mod. The base game is never overwritten. Ctrl+Z / Ctrl+Y: undo / redo.", 14, Ui.Colour("text.muted")));
        var formatHint = Title("Property and layout edits reformat the document and remove comments. Edit Source to preserve them.", 13, Ui.Colour("text.caption"));
        formatHint.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(formatHint);
        var columns = new HSplitContainer { SizeFlagsVertical = SizeFlags.ExpandFill, SplitOffsets = new[] { 280 } };
        rows.AddChild(columns);
        var browser = new VBoxContainer { CustomMinimumSize = new Vector2(240, 0) };
        columns.AddChild(browser);
        var search = new LineEdit { PlaceholderText = Tr("Find a file…"), ClearButtonEnabled = true };
        browser.AddChild(search);
        var files = new ItemList { Name = "ContentFileList", SizeFlagsVertical = SizeFlags.ExpandFill };
        browser.AddChild(files);
        var paths = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        var baseRoot = GameDataLoader.DataFolder("");
        void ScanFiles()
        {
            paths.Clear();
            foreach (var root in new[] { baseRoot }.Concat(ScenarioRoots).Append(AuthorRoot))
            {
                if (root is null || !Directory.Exists(root)) continue;
                foreach (var category in ContentWorkbench.Roots)
                {
                    var dir = Path.Combine(root, category);
                    if (!Directory.Exists(dir)) continue;
                    // Everything the loaders read, not only JSON: a YAML twin or a mod written
                    // in YAML used to be invisible here although the game loads it. Ownership
                    // CSV tables must also reach IsEditable rather than being filtered out early.
                    foreach (var f in Directory.EnumerateFiles(dir, "*", SearchOption.AllDirectories))
                    {
                        var relative = Path.GetRelativePath(root, f).Replace('\\', '/');
                        if (ContentWorkbench.IsEditable(relative)) paths[relative] = f;
                    }
                }
            }
        }
        ScanFiles();
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
        files.ItemSelected += index =>
        {
            var relative = files.GetItemText((int)index); var path = paths[relative];
            ConfirmAuthorExit(() => OpenAuthorFile(relative, path));
        };
        Filter();
        _authorRefreshFiles = () => { ScanFiles(); Filter(); };
        var editing = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        columns.AddChild(editing);
        var documentHeading = new HBoxContainer();
        editing.AddChild(documentHeading);
        _authorDocumentHeading = Title("Select a file.", 16, Ui.Colour("text.accent"));
        _authorDocumentHeading.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _authorDocumentHeading.AutowrapMode = TextServer.AutowrapMode.Off;
        _authorDocumentHeading.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _authorDocumentHeading.ClipText = true;
        _authorDocumentHeading.Name = "ContentDocumentHeading";
        documentHeading.AddChild(_authorDocumentHeading);
        _authorFormatBadge = Title("JSON / YAML", 13, Ui.Colour("text.accent"));
        _authorFormatBadge.AutowrapMode = TextServer.AutowrapMode.Off;
        _authorFormatBadge.SizeFlagsHorizontal = SizeFlags.ShrinkEnd;
        _authorFormatBadge.Name = "ContentFormatBadge";
        documentHeading.AddChild(_authorFormatBadge);
        var tabs = _authorTabs = new TabContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        editing.AddChild(tabs);
        var inspector = new VBoxContainer { Name = "Properties" };
        tabs.AddChild(inspector);
        tabs.SetTabTitle(0, Tr("Properties"));
        _authorTree = new Tree { Columns = 2, HideRoot = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        _authorTree.SetColumnTitle(0, Tr("Property")); _authorTree.SetColumnTitle(1, Tr("Value"));
        _authorTree.ColumnTitlesVisible = true;
        inspector.AddChild(_authorTree);
        var value = new CodeEdit { Name = "PropertyValue", PlaceholderText = Tr("JSON value: number, true, or a quoted string"), GuttersDrawLineNumbers = true, CustomMinimumSize = new Vector2(0, 100) };
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
                if (AuthorTextOnly()) return;
                var selected = _authorTree.GetSelected();
                if (selected is null) { _authorStatus.Text = Tr("Select a property first."); return; }
                var updated = ContentWorkbench.SetValue(_authorCode.Text, selected.GetMetadata(0).AsString(), value.Text, _authorRelative);
                _authorCode.BeginComplexOperation();
                _authorCode.SelectAll(); _authorCode.InsertTextAtCaret(updated);
                _authorCode.EndComplexOperation();
                RebuildAuthorTree();
            }
            catch (Exception e) { _authorStatus.Text = e.Message; }
        }, 200, 36));
        var addRow = new HFlowContainer(); inspector.AddChild(addRow);
        var propertyName = new LineEdit { Name = "NewPropertyName", PlaceholderText = Tr("New property name (objects)"), CustomMinimumSize = new Vector2(210, 36) }; addRow.AddChild(propertyName);
        addRow.AddChild(ActionButton("Add child / array item", () =>
        {
            try
            {
                if (AuthorTextOnly()) return;
                var pointer = _authorTree.GetSelected()?.GetMetadata(0).AsString() ?? "";
                ReplaceAuthorDocument(ContentWorkbench.AddValue(_authorCode.Text, pointer, propertyName.Text, value.Text, _authorRelative)); RebuildAuthorTree();
            }
            catch (Exception e) { _authorStatus.Text = e.Message; }
        }, 200, 36));
        addRow.AddChild(ActionButton("Delete selected property", () =>
        {
            if (AuthorTextOnly()) return;
            var pointer = _authorTree.GetSelected()?.GetMetadata(0).AsString();
            if (string.IsNullOrEmpty(pointer)) return;
            var document = _authorCode.Text;
            var relative = _authorRelative;
            var confirmation = new ConfirmationDialog { Title = Tr("Delete property"), DialogText = pointer + "\n" + Tr("This removes the selected property and its children. Undo is available in the Source tab."), Exclusive = true };
            _authorCanvas.AddChild(confirmation);
            confirmation.Confirmed += () =>
            {
                try
                {
                    if (_authorRelative != relative || _authorCode.Text != document) throw new InvalidDataException(Tr("The document changed. Select the property again before deleting it."));
                    ReplaceAuthorDocument(ContentWorkbench.RemoveValue(document, pointer, relative)); RebuildAuthorTree();
                }
                catch (Exception e) { _authorStatus.Text = e.Message; }
                confirmation.QueueFree();
            };
            confirmation.Canceled += () => confirmation.QueueFree(); confirmation.PopupCentered();
        }, 200, 36));
        _authorCode = new CodeEdit { Name = "JSON", GuttersDrawLineNumbers = true, SizeFlagsVertical = SizeFlags.ExpandFill };
        tabs.AddChild(_authorCode);
        // The raw document, which is JSON or YAML (or a province table) depending on the file.
        tabs.SetTabTitle(1, Tr("Content source"));
        _authorCode.TextChanged += () => { _authorDirty = _authorCode.Text != _authorSaved; };
        _authorLayout = new VisualLayoutEditor
        {
            Name = "Layout", ReadDocument = () => StructuredData.ToJson(_authorRelative, _authorCode.Text),
            IsLayoutDocument = () => _authorRelative.StartsWith("gui/", StringComparison.OrdinalIgnoreCase),
            ReplaceDocument = json => ReplaceAuthorDocument(StructuredData.FormatJson(_authorRelative, json)),
            UndoDocument = () => _authorCode.Undo(), RedoDocument = () => _authorCode.Redo()
        };
        tabs.AddChild(_authorLayout);
        tabs.SetTabTitle(2, Tr("Layout"));
        tabs.TabChanged += index => { if (index == 0) RebuildAuthorTree(); if (index == 2) _authorLayout.RefreshDocument(); };
        var toolbar = new HFlowContainer();
        rows.AddChild(toolbar);
        var validate = ActionButton("Validate", () => AuthorOperation(false), 130, 38);
        validate.Name = "ContentValidate"; toolbar.AddChild(validate);
        var save = ActionButton("Save to my mod", () => AuthorOperation(true), 190, 38, true);
        save.Name = "ContentSave"; toolbar.AddChild(save);
        toolbar.AddChild(ActionButton("Scenarios", () => ConfirmAuthorExit(OpenScenarioLibrary), 140, 38));
        var newDocument = ActionButton("New document", NewAuthorDocument, 160, 38);
        newDocument.Name = "ContentNewDocument"; toolbar.AddChild(newDocument);
        toolbar.AddChild(ActionButton("Refresh file list", () => { ScanFiles(); Filter(); }, 160, 38));
        toolbar.AddChild(ActionButton("Open mod folder", () => { ContentWorkbench.EnsureMod(AuthorRoot); OS.ShellOpen(AuthorRoot); }, 180, 38));
        _authorStatus = Title("Select a file. Changes to rules and UI take effect after restarting with Editor workspace enabled.", 13, Ui.Colour("text.caption"));
        _authorStatus.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        rows.AddChild(_authorStatus);
        _authorSaved = _authorRelative = "";
        _authorSourcePath = "";
    }

    /// <summary>
    /// YAML and province tables are edited as text. The property editor rewrites a document as
    /// JSON, and doing that to a YAML file would throw away its comments and its format, so it
    /// says where to edit instead.
    /// </summary>
    private bool AuthorTextOnly()
    {
        if (!ContentWorkbench.IsYaml(_authorRelative) && !_authorRelative.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return false;
        _authorStatus.Text = Tr("This file is edited as text: use the Text tab. The property list shows what the game reads from it.");
        return true;
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
            _authorRelative = relative; _authorSaved = ""; _authorSourcePath = "";
            _authorCode.Text = "{\n  \"layout\": {}\n}\n";
            _authorCode.ClearUndoHistory(); _authorDirty = true;
            _authorStatus.Text = relative;
            RefreshAuthorDocumentHeading();
        }
        _authorTabs.CurrentTab = 2;
        _authorLayout.RefreshDocument();
    }

    private void NewAuthorDocument()
    {
        ConfirmAuthorExit(() =>
        {
            var dialog = new ConfirmationDialog { Name = "NewContentDocument", Title = Tr("New document"), DialogText = Tr("Relative path inside the mod, ending in .json, .yaml or .yml"), MinSize = new Vector2I(500, 150) };
            var path = new LineEdit { Name = "NewContentPath", Text = "localization/russian/99_my_names.json", OffsetTop = 55, OffsetRight = 470, OffsetBottom = 90 };
            dialog.AddChild(path);
            _authorCanvas.AddChild(dialog);
            dialog.Confirmed += () =>
            {
                try
                {
                    var relative = path.Text.Replace('\\', '/');
                    ScenarioLibrary.SafePath(AuthorRoot, relative);
                    if (!ContentWorkbench.IsEditable(relative))
                        throw new InvalidDataException(Tr("Use JSON or YAML in a declared content folder. Scenario documents remain JSON."));
                    if (File.Exists(ScenarioLibrary.SafePath(AuthorRoot, relative)))
                        throw new InvalidDataException(Tr("This document already exists. Open it in the file list."));
                    _authorRelative = relative; _authorSaved = ""; _authorSourcePath = "";
                    _authorCode.Text = relative.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
                        ? "province;owner;controller\n"
                        : StructuredData.IsYaml(relative) ? "{}\n" : "{\n}\n";
                    _authorCode.ClearUndoHistory(); _authorDirty = true;
                    _authorStatus.Text = relative; RebuildAuthorTree(); _authorLayout.RefreshDocument();
                    RefreshAuthorDocumentHeading();
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
            // Read first: an unavailable file must not bind the previous buffer to a new save path.
            var contents = File.ReadAllText(path);
            _authorRelative = relative;
            _authorSourcePath = Path.GetFullPath(path);
            _authorSaved = contents;
            _authorCode.Text = _authorSaved;
            _authorCode.ClearUndoHistory();
            _authorDirty = false;
            _authorStatus.Text = relative;
            RefreshAuthorDocumentHeading();
            RebuildAuthorTree();
            _authorLayout.RefreshDocument();
        }
        catch (Exception e) { _authorStatus.Text = e.Message; }
    }

    private void RebuildAuthorTree()
    {
        var selection = _authorTree.GetSelected()?.GetMetadata(0).AsString();
        var expanded = new HashSet<string>();
        void Remember(TreeItem item)
        {
            if (item is null) return;
            if (!item.Collapsed) expanded.Add(item.GetMetadata(0).AsString());
            for (var child = item.GetFirstChild(); child is not null; child = child.GetNext()) Remember(child);
        }
        Remember(_authorTree.GetRoot());
        _authorTree.Clear();
        if (_authorCode.Text.Length == 0) return;
        try
        {
            // A YAML document is shown through the same converter the loaders use, so the tree
            // is what the game will read; a province table has no tree at all.
            if (_authorRelative.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)) return;
            using var doc = StructuredData.ParseDocument(_authorRelative, _authorCode.Text);
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
                item.SetMetadata(1, raw);
                item.SetText(1, container ? element.ValueKind == JsonValueKind.Array ? $"[{element.GetArrayLength()}]" : "{…}" : raw);
                item.Collapsed = depth > 0 && !expanded.Contains(pointer);
                if (selection == pointer) item.Select(0);
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
            else if (doc.RootElement.ValueKind == JsonValueKind.Array) Add(doc.RootElement, root, Tr("Document array"), "", 0);
            _authorTree.TooltipText = Tr("Property preview: first 100 array entries, 6 levels. The Source tab exposes the complete document.");
        }
        catch (Exception e) { _authorStatus.Text = e.Message; }
    }

    private void RefreshAuthorDocumentHeading()
    {
        if (_authorDocumentHeading is null || _authorFormatBadge is null) return;
        _authorDocumentHeading.Text = _authorRelative;
        _authorDocumentHeading.TooltipText = _authorSourcePath.Length > 0 ? _authorSourcePath : _authorRelative;
        _authorFormatBadge.Text = _authorRelative.EndsWith(".csv", StringComparison.OrdinalIgnoreCase)
            ? "CSV" : StructuredData.IsYaml(_authorRelative) ? "YAML" : "JSON";
    }

    private void AuthorOperation(bool save)
    {
        if (string.IsNullOrEmpty(_authorRelative)) return;
        try
        {
            _authorStatus.Text = Tr(ContentWorkbench.Validate(_authorRelative, _authorCode.Text));
            if (!save) return;
            ContentWorkbench.EnsureMod(AuthorRoot);
            var target = ScenarioLibrary.SafePath(AuthorRoot, _authorRelative);
            var expected = string.Equals(_authorSourcePath, target, StringComparison.OrdinalIgnoreCase) ? _authorSaved : null;
            var path = ContentWorkbench.Write(AuthorRoot, _authorRelative, _authorCode.Text, expected);
            _authorSourcePath = Path.GetFullPath(path);
            _authorSaved = _authorCode.Text; _authorDirty = false;
            _authorRefreshFiles?.Invoke();
            _authorStatus.Text = Tr("Saved. Previous version: .bak. Enable Editor workspace and restart for rules/UI changes.") + " " + path;
        }
        catch (Exception e) { _authorStatus.Text = Tr(e.Message); }
    }

    /// <summary>A scenario's picture: its own sprite (scenario_&lt;id&gt;), else the night Earth, else none.</summary>
    private static Texture2D ScenarioPicture(string id)
    {
        // The player who turned the generated pictures off sees none here either.
        if (LoadingScreen.IllustrationPaths().Length == 0) return null;
        foreach (var name in new[] { "scenario_" + id.ToLowerInvariant(), "loading_earth_night" })
        {
            var path = SpriteCatalog.TextureOf(name);
            if (path.Length > 0 && LoadingScreen.Picture(path) is { } texture) return texture;
        }
        return null;
    }
}
