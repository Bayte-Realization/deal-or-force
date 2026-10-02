using System;
using System.Collections.Generic;
using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>Edits the workshop's shared JSON buffer; CodeEdit owns undo, dirty state and saving.</summary>
public partial class VisualLayoutEditor : VBoxContainer
{
    public Func<string> ReadDocument;
    public Func<bool> IsLayoutDocument;
    public Action<string> ReplaceDocument;
    public Action UndoDocument, RedoDocument;
    private readonly (string Key, InterfaceSkin.PanelRect Rect)[] _inherited = InterfaceSkin.Panels.ToArray();
    private LayoutPreview _preview;
    private ItemList _list;
    private LineEdit _search;
    private Label _empty;
    private Label _status, _identity;
    private Button _apply, _remove, _fit;
    private readonly SpinBox[] _anchors = new SpinBox[4], _offsets = new SpinBox[4];
    private Dictionary<string, InterfaceSkin.PanelRect> _panels = new();
    private bool _valid;

    public bool CancelGesture()
    {
        if (!IsVisibleInTree() || _preview?.IsDragging != true) return false;
        _preview.CancelDrag(); return true;
    }

    public override void _UnhandledKeyInput(InputEvent input)
    {
        if (!IsVisibleInTree() || input is not InputEventKey { Pressed: true, Echo: false } key || !(key.CtrlPressed || key.MetaPressed)) return;
        if (DealOrForceGame.Letter(key) == Key.Z && !key.ShiftPressed) UndoDocument?.Invoke();
        else if (DealOrForceGame.Letter(key) == Key.Y || DealOrForceGame.Letter(key) == Key.Z && key.ShiftPressed) RedoDocument?.Invoke();
        else return;
        RefreshDocument(); GetViewport().SetInputAsHandled();
    }

    private static Label Caption(string text) => new() { Text = Loc.Get(text), AutowrapMode = TextServer.AutowrapMode.WordSmart };
    private static Button Button(string label, Action action)
    {
        var button = new Button { Text = Loc.Get(label), CustomMinimumSize = new Vector2(0, 32) };
        DealOrForceGame.StyleButton(button, false);
        button.Pressed += action;
        return button;
    }

    public override void _Ready()
    {
        AddThemeConstantOverride("separation", 8);
        AddChild(Caption("Layout schematic — drag a panel; drag its lower-right handle to resize. Shift: ignore grid. Esc: cancel drag."));
        var tools = new HFlowContainer(); AddChild(tools);
        var context = new OptionButton { Name = "PreviewContext" };
        foreach (var label in new[] { "Automatic context", "Gameplay panels", "Country / region / province", "Country selection" }) context.AddItem(Loc.Get(label));
        tools.AddChild(context);
        context.ItemSelected += i => { _preview.CancelDrag(); _preview.Context = (int)i; _preview.QueueRedraw(); };
        var resolution = new OptionButton { Name = "PreviewResolution" };
        var sizes = new[] { new Vector2(1920, 1080), new Vector2(1280, 720), new Vector2(2560, 1440), new Vector2(3440, 1440) };
        foreach (var size in sizes) resolution.AddItem($"{size.X:0} × {size.Y:0}");
        tools.AddChild(resolution);
        resolution.ItemSelected += i => { _preview.CancelDrag(); _preview.ReferenceSize = sizes[(int)i]; Inspect(); _preview.QueueRedraw(); };
        var snap = new CheckButton { Text = Loc.Get("8 px grid"), ButtonPressed = true };
        tools.AddChild(snap); snap.Toggled += value => _preview.Snap = value;
        tools.AddChild(Button("Undo", () => { UndoDocument?.Invoke(); RefreshDocument(); }));
        tools.AddChild(Button("Redo", () => { RedoDocument?.Invoke(); RefreshDocument(); }));
        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 12); AddChild(columns);
        var panelBrowser = new VBoxContainer { CustomMinimumSize = new Vector2(190, 0) };
        columns.AddChild(panelBrowser);
        _search = new LineEdit { Name = "LayoutPanelSearch", PlaceholderText = Loc.Get("Find panel…"),
            TooltipText = Loc.Get("Search by panel name or stable ID"), ClearButtonEnabled = true };
        panelBrowser.AddChild(_search);
        _search.TextChanged += _ => RebuildPanelList();
        _empty = Caption("No matching panels"); panelBrowser.AddChild(_empty);
        _list = new ItemList { Name = "LayoutPanels", SizeFlagsVertical = SizeFlags.ExpandFill };
        panelBrowser.AddChild(_list);
        _list.ItemSelected += index => { _preview.CancelDrag(); _preview.Selection = _list.GetItemMetadata((int)index).AsString(); Inspect(); _preview.QueueRedraw(); };
        _preview = new LayoutPreview { Name = "LayoutCanvas", SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill };
        _preview.TooltipText = Loc.Get("Focus the canvas: arrow keys move 1 px; Shift + arrows move 8 px.");
        columns.AddChild(_preview);
        _preview.Selected += id => { SelectList(id); Inspect(); };
        _preview.Committed += Commit;
        var scroll = new ScrollContainer { CustomMinimumSize = new Vector2(280, 0), HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled };
        columns.AddChild(scroll);
        var inspector = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        inspector.AddThemeConstantOverride("separation", 8); scroll.AddChild(inspector);
        _identity = Caption(""); inspector.AddChild(_identity);
        var grid = new GridContainer { Columns = 3 }; inspector.AddChild(grid);
        // Grid labels need their text minimum width: wrapped labels advertise a 1px
        // minimum and otherwise collapse into a vertical column of individual letters.
        grid.AddChild(new Label { Text = Loc.Get("Edge") });
        grid.AddChild(new Label { Text = Loc.Get("Anchor") });
        grid.AddChild(new Label { Text = Loc.Get("Offset") });
        var edges = new[] { "Left", "Top", "Right", "Bottom" };
        for (var i = 0; i < 4; i++)
        {
            grid.AddChild(new Label { Text = Loc.Get(edges[i]) });
            _anchors[i] = new SpinBox { MinValue = 0, MaxValue = 1, Step = .001, CustomMinimumSize = new Vector2(86, 32) };
            _offsets[i] = new SpinBox { MinValue = -32768, MaxValue = 32768, Step = 1, CustomMinimumSize = new Vector2(76, 32) };
            grid.AddChild(_anchors[i]); grid.AddChild(_offsets[i]);
        }
        _apply = Button("Apply geometry", () => Commit(LayoutWorkbench.Make(_anchors.Select(s => (float)s.Value).ToArray(), _offsets.Select(s => (float)s.Value).ToArray())));
        inspector.AddChild(_apply);
        _fit = Button("Fit inside screen", () =>
        {
            if (_valid && _panels.TryGetValue(_preview.Selection, out var rect))
                Commit(LayoutWorkbench.FitInside(rect, _preview.ReferenceSize.X, _preview.ReferenceSize.Y));
        });
        _fit.Name = "FitPanelInsideScreen";
        _fit.TooltipText = Loc.Get("Move the panel inside the reference screen, shrinking only if needed. Keeps anchors; can be undone.");
        inspector.AddChild(_fit);
        inspector.AddChild(Caption("Rebind without moving"));
        var presets = new OptionButton { Name = "AnchorPreset" };
        foreach (var label in new[] { "Choose anchors…", "Top left", "Top right", "Bottom left", "Bottom right", "Centre", "Stretch" }) presets.AddItem(Loc.Get(label));
        inspector.AddChild(presets);
        presets.ItemSelected += i =>
        {
            if (!_valid || i == 0 || !_panels.TryGetValue(_preview.Selection, out var rect)) return;
            var choices = new[] { new float[] { 0, 0, 0, 0 }, new float[] { 1, 0, 1, 0 }, new float[] { 0, 1, 0, 1 },
                new float[] { 1, 1, 1, 1 }, new float[] { .5f, .5f, .5f, .5f }, new float[] { 0, 0, 1, 1 } };
            Commit(LayoutWorkbench.Reanchor(rect, choices[(int)i - 1], _preview.ReferenceSize.X, _preview.ReferenceSize.Y));
            presets.Select(0);
        };
        _remove = Button("Remove file override", () =>
        {
            if (!_valid) return;
            TryEdit(() => LayoutWorkbench.RemoveOverride(ReadDocument(), _preview.Selection));
        });
        inspector.AddChild(_remove);
        inspector.AddChild(Caption("Blue dots: anchors. Values are in reference-screen pixels. Only edited panels are saved; other fields stay intact."));
        _status = Caption(""); AddChild(_status);
        RefreshDocument();
    }

    public void RefreshDocument()
    {
        if (_preview is null) return;
        _preview.CancelDrag();
        try
        {
            if (IsLayoutDocument?.Invoke() != true) throw new InvalidOperationException(Loc.Get("Open a gui document, or use UI layout editor to create an override."));
            _panels = LayoutWorkbench.Read(ReadDocument(), _inherited);
            _valid = true;
            _preview.Panels = _panels;
            if (!_panels.ContainsKey(_preview.Selection)) _preview.Selection = _panels.Keys.FirstOrDefault() ?? "";
            RebuildPanelList();
            Inspect();
        }
        catch (Exception e)
        {
            _valid = false; _panels.Clear(); _preview.Panels = new(); _list.Clear(); _empty.Visible = false;
            _status.Text = e.Message; _apply.Disabled = _remove.Disabled = _fit.Disabled = true;
        }
        _preview.QueueRedraw();
    }

    private void SelectList(string id)
    {
        _list.DeselectAll();
        for (var i = 0; i < _list.ItemCount; i++) if (_list.GetItemMetadata(i).AsString() == id) { _list.Select(i); _list.EnsureCurrentIsVisible(); break; }
    }

    private void RebuildPanelList()
    {
        _list.Clear();
        if (!_valid) { _empty.Visible = false; return; }
        var query = _search.Text.Trim();
        foreach (var key in _panels.Keys.OrderBy(k => k, StringComparer.Ordinal))
        {
            var name = LayoutPreview.PanelName(key);
            if (!name.Contains(query, StringComparison.OrdinalIgnoreCase) &&
                !key.Contains(query, StringComparison.OrdinalIgnoreCase)) continue;
            var i = _list.AddItem(name);
            _list.SetItemMetadata(i, key);
            var authored = LayoutWorkbench.HasOverride(ReadDocument(), key);
            _list.SetItemCustomFgColor(i, Ui.Colour(authored ? "text.accent" : "text.muted"));
            _list.SetItemTooltip(i, name + "\n" + key + "\n" + Loc.Get(authored ? "Defined in this document" : "Inherited from the loaded interface"));
        }
        _empty.Visible = _list.ItemCount == 0;
        SelectList(_preview.Selection);
    }

    private void Inspect()
    {
        if (!_valid || !_panels.TryGetValue(_preview.Selection, out var rect)) return;
        var anchors = LayoutWorkbench.Anchors(rect); var offsets = LayoutWorkbench.Offsets(rect);
        for (var i = 0; i < 4; i++) { _anchors[i].Value = anchors[i]; _offsets[i].Value = offsets[i]; }
        _identity.Text = LayoutPreview.PanelName(_preview.Selection) + "\n" + _preview.Selection;
        _apply.Disabled = false;
        _remove.Disabled = !LayoutWorkbench.HasOverride(ReadDocument(), _preview.Selection);
        var b = LayoutWorkbench.Measure(rect, _preview.ReferenceSize.X, _preview.ReferenceSize.Y);
        _fit.Disabled = LayoutWorkbench.FitInside(rect, _preview.ReferenceSize.X, _preview.ReferenceSize.Y) == rect;
        _status.Text = $"{b.X:0}, {b.Y:0} · {b.Width:0} × {b.Height:0} px — " + Loc.Get("Schematic only: content minimum sizes and adaptive windows may affect the in-game result.");
        if (b.Width <= 0 || b.Height <= 0 || b.X < 0 || b.Y < 0 || b.X + b.Width > _preview.ReferenceSize.X || b.Y + b.Height > _preview.ReferenceSize.Y)
            _status.Text += " " + Loc.Get("Warning: panel is inverted or outside the reference screen.");
    }

    private void Commit(InterfaceSkin.PanelRect rect)
    {
        if (_valid && _panels.TryGetValue(_preview.Selection, out var previous) && previous != rect)
            TryEdit(() => LayoutWorkbench.SetRect(ReadDocument(), _preview.Selection, rect));
    }

    private void TryEdit(Func<string> change)
    {
        try { ReplaceDocument(change()); RefreshDocument(); }
        catch (Exception e) { _status.Text = e.Message; _preview.QueueRedraw(); }
    }
}
