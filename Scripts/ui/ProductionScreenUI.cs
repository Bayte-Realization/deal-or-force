using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    private Control _productionLayer;
    private Label _productionSummary, _productionFocusReadout, _productionCapacityLabel, _productionEmpty;
    private readonly Dictionary<ProductionPool, ProductionPoolCard> _productionPoolCards = new();
    private readonly Dictionary<string, StyleBoxFlat> _productionStyles = new();
    private readonly Dictionary<string, Texture2D> _productionFactoryIcons = new();
    private VBoxContainer _productionLineRows, _productionShortageRows;
    private OptionButton _productionLinePicker;
    private Button _productionAddLineButton;
    private readonly Dictionary<int, Button> _productionConversionButtons = new();
    private readonly List<ProductionLineRow> _productionLineControls = new();
    private ProductionAllocation _productionAllocation;
    private int ProductionCells => (int)Math.Clamp(InterfaceSkin.Metric("production.cells_per_page", 20), 10, 30);
    private int ProductionColumns => (int)Math.Clamp(InterfaceSkin.Metric("production.grid_columns", 10), 5, 10);

    private sealed class ProductionPoolCard
    {
        public Label Count, Free, Factories;
        public ProgressBar Usage;
    }

    private sealed class ProductionLineRow
    {
        public PanelContainer Root;
        public Label Name, Reading, Output, Range, Pool;
        public TextureRect Icon;
        public Button MinusTen, PlusTen;
        public SpinBox Amount, PagePicker;
        public OptionButton Priority;
        public ProgressBar Supply;
        public bool Updating;
        public readonly List<Button> Cells = new();
        public Commodity Good;
        public bool Bound;
        public int Page;
    }

    private void BuildProductionScreen()
    {
        _productionLayer = FullLayer("Production");
        _productionLayer.Visible = false;
        _hudLayer.AddChild(_productionLayer);
        var shade = new ColorRect { Color = Ui.Colour("surface.overlay", InterfaceSkin.Metric("production.map_shade", 0.30f)), MouseFilter = MouseFilterEnum.Stop };
        shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _productionLayer.AddChild(shade);
        var panel = TexturedPanel(_productionLayer);
        Ui.Place(panel, "modal.production");
        panel.Name = "ProductionPanel";
        var rows = PanelRows(panel, 16, 12, 10);
        var header = new HBoxContainer();
        rows.AddChild(header);
        var title = Title("PRODUCTION", 23, Ui.Colour("text.accent"));
        title.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        header.AddChild(title);
        header.AddChild(HandbookButton("production"));
        header.AddChild(ActionButton("×", CloseProductionScreen, 38, 32));
        _productionSummary = ProductionLabel("", 12, "text.caption");
        _productionSummary.Name = "ProductionPools";
        _productionSummary.ClipText = true;
        rows.AddChild(_productionSummary);

        var pools = new HBoxContainer();
        pools.AddThemeConstantOverride("separation", 10);
        rows.AddChild(pools);
        BuildProductionPool(pools, ProductionPool.Civilian);
        BuildProductionPool(pools, ProductionPool.Military);

        var conversion = new HBoxContainer();
        rows.AddChild(conversion);
        _productionFocusReadout = ProductionLabel("", 12, "text.body_soft");
        _productionFocusReadout.Name = "ProductionConversion";
        _productionFocusReadout.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _productionFocusReadout.ClipText = true;
        conversion.AddChild(_productionFocusReadout);
        foreach (var step in new[] { -10, -1, 1, 10 })
        {
            var button = ActionButton(step > 0 ? "+" + step : step.ToString(), () => ChangeProductionConversion(step), 42, 28);
            button.Name = "Convert" + (step < 0 ? "Minus" : "Plus") + Math.Abs(step);
            button.TooltipText = Tr("Convert civilian units into military capacity. Conversion rounds down; existing line assignments may be reduced.");
            conversion.AddChild(button);
            _productionConversionButtons[step] = button;
        }
        _productionCapacityLabel = ProductionLabel("", 12, "text.caption");
        _productionCapacityLabel.ClipText = true;

        var add = new HBoxContainer();
        rows.AddChild(add);
        _productionLinePicker = new OptionButton { Name = "ProductionGoodPicker", SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 34) };
        add.AddChild(_productionLinePicker);
        _productionAddLineButton = ActionButton("Open a line", OnProductionAddLine, 140, 34, true);
        _productionAddLineButton.Name = "AddProductionLine";
        add.AddChild(_productionAddLineButton);
        var legend = new HBoxContainer();
        legend.AddThemeConstantOverride("separation", 16);
        rows.AddChild(legend);
        foreach (var (word, token) in new[] { ("Assigned", "production.civilian"), ("Available", "production.empty_border"), ("Reserved", "production.blocked") })
        {
            var key = new HBoxContainer();
            key.AddChild(new ColorRect { Color = Ui.Colour(token), CustomMinimumSize = new Vector2(10, 10), SizeFlagsVertical = SizeFlags.ShrinkCenter, MouseFilter = MouseFilterEnum.Ignore });
            key.AddChild(ProductionLabel(word, 11, "text.caption"));
            legend.AddChild(key);
        }
        _productionCapacityLabel.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        _productionCapacityLabel.HorizontalAlignment = HorizontalAlignment.Right;
        legend.AddChild(_productionCapacityLabel);

        var scroll = new ScrollContainer
        {
            Name = "ProductionLinesScroll", HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled,
            SizeFlagsVertical = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(0, 160)
        };
        rows.AddChild(scroll);
        _productionLineRows = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        _productionLineRows.AddThemeConstantOverride("separation", (int)Math.Clamp(InterfaceSkin.Metric("production.line_gap", 8), 2, 20));
        scroll.AddChild(_productionLineRows);
        _productionEmpty = Title("Open a production line and assign capacity by clicking its factory cells. Unassigned units keep serving the ordinary economy.", 14, Ui.Colour("text.caption"));
        _productionEmpty.AutowrapMode = TextServer.AutowrapMode.WordSmart;
        _productionLineRows.AddChild(_productionEmpty);
        var marketNote = ProductionLabel("National output from the last economic tick; changes apply on the next tick.", 11, "text.caption");
        marketNote.ClipText = true;
        marketNote.TooltipText = Ui.Tip("Output below includes the whole economy, not just assigned factories. Changes appear after the next economic tick.");
        rows.AddChild(marketNote);
        _productionShortageRows = new VBoxContainer();
        rows.AddChild(_productionShortageRows);
    }

    private Label ProductionLabel(string text, int size, string colour)
    {
        var label = Title(text, size, Ui.Colour(colour));
        label.AutowrapMode = TextServer.AutowrapMode.Off;
        return label;
    }

    private StyleBoxFlat ProductionStyle(string fill, string edge, int padding = 5)
    {
        var key = fill + edge + padding;
        if (_productionStyles.TryGetValue(key, out var cached)) return cached;
        var style = new StyleBoxFlat { BgColor = Ui.Colour(fill), BorderColor = Ui.Colour(edge),
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 1, BorderWidthBottom = 2,
            CornerRadiusTopLeft = 2, CornerRadiusTopRight = 2, CornerRadiusBottomLeft = 2, CornerRadiusBottomRight = 2 };
        style.SetContentMarginAll(padding);
        _productionStyles[key] = style;
        return style;
    }

    private ProgressBar ProductionBar(string colour, int height)
    {
        var bar = new ProgressBar { ShowPercentage = false, CustomMinimumSize = new Vector2(0, height), MouseFilter = MouseFilterEnum.Ignore };
        bar.AddThemeStyleboxOverride("background", ProductionStyle("production.inset", "production.inset", 0));
        bar.AddThemeStyleboxOverride("fill", ProductionStyle(colour, colour, 0));
        return bar;
    }

    private Texture2D ProductionFactoryIcon(ProductionPool pool)
    {
        var sprite = pool == ProductionPool.Civilian ? "production_civilian" : "production_military";
        if (SpriteCatalog.Has(sprite)) return Art(sprite, "");
        var atlasPath = SpriteCatalog.TextureOf("production_capacity_atlas", "res://game/gfx/interface/icons/production_capacity_atlas.png");
        var key = sprite + atlasPath;
        if (_productionFactoryIcons.TryGetValue(key, out var cached)) return cached;
        if (ResourceLoader.Exists(atlasPath))
        {
            var atlas = GD.Load<Texture2D>(atlasPath);
            var image = atlas.GetImage();
            var half = new Rect2I(pool == ProductionPool.Civilian ? 0 : image.GetWidth() / 2, 0, image.GetWidth() / 2, image.GetHeight());
            // Each half is one icon. Trim transparent padding in the texture region only;
            // the generated source is preserved intact and mods can override either sprite.
            var used = image.GetRegion(half).GetUsedRect();
            var texture = new AtlasTexture { Atlas = atlas,
                Region = new Rect2(half.Position.X + used.Position.X, used.Position.Y, used.Size.X, used.Size.Y) };
            _productionFactoryIcons[key] = texture;
            return texture;
        }
        var fallback = pool == ProductionPool.Civilian ? "icon_building_civilianindustry" : "icon_building_armsindustry";
        return Art(sprite, $"res://game/gfx/interface/icons/{fallback}.png");
    }

    private void BuildProductionPool(HBoxContainer parent, ProductionPool pool)
    {
        var colour = pool == ProductionPool.Civilian ? "production.civilian" : "production.military";
        var plate = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        plate.AddThemeStyleboxOverride("panel", ProductionStyle("production.card", "production.border", 10));
        AtlasSurfaceSkin.Attach(plate, AtlasSurfaceSkin.Level.Raised, "production.card");
        parent.AddChild(plate);
        var rows = new VBoxContainer();
        plate.AddChild(rows);
        var heading = new HBoxContainer();
        rows.AddChild(heading);
        heading.AddChild(new TextureRect { Texture = ProductionFactoryIcon(pool), Modulate = Ui.Colour(colour),
            CustomMinimumSize = new Vector2(28, 28), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = MouseFilterEnum.Ignore });
        heading.AddChild(ProductionLabel(pool == ProductionPool.Civilian ? "CIVILIAN INDUSTRY" : "MILITARY INDUSTRY", 13, colour));
        var card = new ProductionPoolCard();
        var counts = new HBoxContainer();
        rows.AddChild(counts);
        card.Count = ProductionLabel("", 25, "text.bright");
        card.Count.Name = pool == ProductionPool.Civilian ? "CivilianCapacity" : "MilitaryCapacity";
        card.Count.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        counts.AddChild(card.Count);
        card.Free = ProductionLabel("", 13, colour);
        card.Free.SizeFlagsVertical = SizeFlags.ShrinkCenter;
        counts.AddChild(card.Free);
        card.Usage = ProductionBar(colour, 4);
        rows.AddChild(card.Usage);
        card.Factories = ProductionLabel("", 11, "text.caption");
        rows.AddChild(card.Factories);
        _productionPoolCards[pool] = card;
    }

    private void OpenProductionScreen()
    {
        _openScreen = "Production";
        _sidePanel.Visible = false;
        RaiseWorkspaceLayer(_productionLayer);
        RefreshProductionScreen();
        UpdateScreenButtons();
    }

    private void CloseProductionScreen()
    {
        _productionLayer.Visible = false;
        if (_openScreen == "Production") _openScreen = "";
        UpdateScreenButtons();
    }


    private void RefreshProductionScreen()
    {
        if (_productionLayer is null || !_productionLayer.Visible || _campaign?.Player is null) return;
        var player = _campaign.Player;
        _productionAllocation = ProductionProgramme.Allocate(_campaign, player);
        var a = _productionAllocation;
        foreach (var (step, button) in _productionConversionButtons)
            button.Disabled = step < 0 ? a.Converted == 0 : a.Converted >= a.RawCivilian;
        _productionSummary.Text = Tr("Allocate factories to production lines. Earlier lines receive capacity first.");
        foreach (var (pool, card) in _productionPoolCards)
        {
            var used = pool == ProductionPool.Civilian ? a.CivilianUsed : a.MilitaryUsed;
            card.Count.Text = $"{used} / {a.Capacity(pool)}";
            card.Free.Text = Simulation.Loc.Format("{0} free", a.Free(pool));
            card.Usage.Value = 100.0 * used / Math.Max(1, a.Capacity(pool));
            card.Factories.Text = Simulation.Loc.Format("{0} factories · {1} units each",
                pool == ProductionPool.Civilian ? a.CivilianFactories : a.MilitaryFactories, ProductionProgramme.UnitsPerFactory);
        }
        _productionFocusReadout.Text = Simulation.Loc.Format("Conversion: {0} civilian → {1} military units · efficiency {2:P0}",
            a.Converted, a.ConvertedMilitary, ProductionProgramme.ConversionEfficiency);
        _productionCapacityLabel.Text = Tr("1 cell = 1 unit");
        RefreshProductionLines();
        RefreshProductionLinePicker();
        RefreshProductionShortages();
    }

    private List<ProductionLine> CopyProductionLines()
    {
        var a = ProductionProgramme.Allocate(_campaign, _campaign.Player);
        return _campaign.Player.ProductionLines.Where(l => l is not null && ProductionProgramme.IsLineable(l.Good))
            .Select(l => new ProductionLine { Good = l.Good, Units = l.Units < 0 ? a.Assigned(l.Good) : l.Units }).ToList();
    }
    private void ChangeProductionConversion(int delta)
    {
        var a = ProductionProgramme.Allocate(_campaign, _campaign.Player);
        CommitProductionProgramme(Math.Clamp(a.Converted + delta, 0, a.RawCivilian), CopyProductionLines());
    }
    private void SetProductionUnits(ProductionLineRow row, int units)
    {
        var a = ProductionProgramme.Allocate(_campaign, _campaign.Player);
        var lines = CopyProductionLines();
        var line = lines.Find(l => l.Good == row.Good);
        if (line is null) return;
        line.Units = Math.Clamp(units, 0, a.Assigned(row.Good) + a.Free(ProductionProgramme.PoolOf(row.Good)));
        CommitProductionProgramme(a.Converted, lines);
    }
    private void StepProductionUnits(ProductionLineRow row, int delta) =>
        SetProductionUnits(row, ProductionProgramme.Allocate(_campaign, _campaign.Player).Assigned(row.Good) + delta);

    private ProductionLineRow BuildProductionLine()
    {
        var line = new ProductionLineRow();
        line.Root = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        line.Root.AddThemeStyleboxOverride("panel", ProductionStyle("production.card", "production.border", 10));
        AtlasSurfaceSkin.Attach(line.Root, AtlasSurfaceSkin.Level.Inset, "production.card");
        _productionLineRows.AddChild(line.Root);
        var rows = new VBoxContainer();
        rows.AddThemeConstantOverride("separation", 8);
        line.Root.AddChild(rows);
        var top = new HBoxContainer();
        rows.AddChild(top);
        line.Priority = new OptionButton { Name = "Priority", CustomMinimumSize = new Vector2(58, 26),
            TooltipText = Ui.Tip("Line priority. Earlier lines receive capacity first when factories are lost.") };
        line.Priority.ItemSelected += position => { if (!line.Updating) MoveProductionLineTo(line, (int)position); };
        top.AddChild(line.Priority);
        line.Name = ProductionLabel("", 15, "text.bright");
        line.Name.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        line.Name.ClipText = true;
        top.AddChild(line.Name);
        line.Pool = ProductionLabel("", 11, "text.caption");
        top.AddChild(line.Pool);
        var clear = ActionButton("Release", () => SetProductionUnits(line, 0), 68, 26);
        clear.Name = "ReleaseUnits";
        clear.TooltipText = Tr("Release all units; keep the line.");
        top.AddChild(clear);
        var close = ActionButton("×", () => CloseProductionLine(line), 30, 26);
        close.Name = "CloseLine";
        close.TooltipText = Tr("Close this production line.");
        top.AddChild(close);

        var body = new HBoxContainer();
        body.AddThemeConstantOverride("separation", 12);
        rows.AddChild(body);
        var portrait = new PanelContainer { CustomMinimumSize = new Vector2(76, 76), SizeFlagsVertical = SizeFlags.ShrinkCenter };
        portrait.AddThemeStyleboxOverride("panel", ProductionStyle("production.inset", "production.border", 12));
        line.Icon = new TextureRect { Name = "CommodityIcon", CustomMinimumSize = new Vector2(48, 48),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore };
        portrait.AddChild(line.Icon);
        body.AddChild(portrait);
        var allocation = new VBoxContainer();
        allocation.AddThemeConstantOverride("separation", 5);
        body.AddChild(allocation);
        var cells = new GridContainer { Name = "FactoryCells", Columns = ProductionColumns };
        cells.AddThemeConstantOverride("h_separation", 3);
        cells.AddThemeConstantOverride("v_separation", 3);
        allocation.AddChild(cells);
        var cellSize = (int)Math.Clamp(InterfaceSkin.Metric("production.cell_size", 28), 24, 36);
        for (var i = 0; i < ProductionCells; i++)
        {
            var index = i;
            var cell = ActionButton("", () =>
            {
                var target = line.Page * ProductionCells + index + 1;
                var assigned = ProductionProgramme.Allocate(_campaign, _campaign.Player).Assigned(line.Good);
                SetProductionUnits(line, target == assigned ? target - 1 : target);
            }, cellSize, cellSize);
            cell.ExpandIcon = true;
            cell.IconAlignment = HorizontalAlignment.Center;
            cell.AddThemeConstantOverride("icon_max_width", cellSize - 8);
            cell.AddThemeStyleboxOverride("hover", ProductionStyle("production.hover", "production.focus", 3));
            cell.AddThemeStyleboxOverride("pressed", ProductionStyle("production.hover", "production.focus", 3));
            var focus = new StyleBoxFlat { DrawCenter = false, BorderColor = Ui.Colour("production.focus") };
            focus.SetBorderWidthAll(1);
            cell.AddThemeStyleboxOverride("focus", focus);
            cells.AddChild(cell); line.Cells.Add(cell);
        }
        var page = new HBoxContainer();
        allocation.AddChild(page);
        line.Range = ProductionLabel("", 10, "text.caption");
        line.Range.SizeFlagsHorizontal = SizeFlags.ExpandFill;
        page.AddChild(line.Range);
        page.AddChild(ProductionLabel("Page", 10, "text.caption"));
        line.PagePicker = new SpinBox { Name = "CapacityPage", MinValue = 1, MaxValue = 1, Step = 1, CustomMinimumSize = new Vector2(62, 24) };
        line.PagePicker.AddThemeFontSizeOverride("font_size", (int)Ui.Metric("ui.font.button_small", 11));
        line.PagePicker.ValueChanged += value => { if (!line.Updating) { line.Page = (int)value - 1; RefreshProductionLines(); } };
        page.AddChild(line.PagePicker);
        var controlRows = new VBoxContainer { SizeFlagsVertical = SizeFlags.ShrinkCenter };
        body.AddChild(controlRows);
        controlRows.AddChild(ProductionLabel("Assigned units", 11, "text.caption"));
        line.Amount = new SpinBox { Name = "AssignedUnits", MinValue = 0, MaxValue = 1, Step = 1, CustomMinimumSize = new Vector2(102, 32) };
        line.Amount.ValueChanged += value => { if (!line.Updating) SetProductionUnits(line, (int)value); };
        controlRows.AddChild(line.Amount);
        var steps = new HBoxContainer();
        controlRows.AddChild(steps);
        line.MinusTen = ActionButton("−10", () => StepProductionUnits(line, -10), 48, 24);
        line.PlusTen = ActionButton("+10", () => StepProductionUnits(line, 10), 48, 24);
        line.MinusTen.Name = "MinusTen"; line.PlusTen.Name = "PlusTen";
        steps.AddChild(line.MinusTen); steps.AddChild(line.PlusTen);
        var report = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ShrinkCenter };
        body.AddChild(report);
        line.Reading = ProductionLabel("", 12, "text.caption");
        line.Reading.ClipText = true;
        report.AddChild(line.Reading);
        line.Output = ProductionLabel("", 12, "text.bright");
        line.Output.ClipText = true;
        report.AddChild(line.Output);
        line.Supply = ProductionBar("production.civilian", 4);
        report.AddChild(line.Supply);
        return line;
    }
    private void RefreshProductionLines()
    {
        var player = _campaign.Player;
        var a = _productionAllocation = ProductionProgramme.Allocate(_campaign, player);
        var lines = player.ProductionLines;
        _productionEmpty.Visible = lines.Count == 0;
        while (_productionLineControls.Count > lines.Count)
        {
            var last = _productionLineControls[^1]; _productionLineControls.RemoveAt(_productionLineControls.Count - 1);
            _productionLineRows.RemoveChild(last.Root); last.Root.QueueFree();
        }
        while (_productionLineControls.Count < lines.Count) _productionLineControls.Add(BuildProductionLine());
        for (var i = 0; i < lines.Count; i++)
        {
            var entry = lines[i]; var row = _productionLineControls[i];
            row.Updating = true;
            var assigned = a.Assigned(entry.Good);
            var pool = ProductionProgramme.PoolOf(entry.Good);
            var capacity = a.Capacity(pool);
            if (!row.Bound || row.Good != entry.Good)
                row.Page = Math.Max(0, assigned - 1) / ProductionCells;
            row.Bound = true; row.Good = entry.Good;
            // Stable positional name avoids Godot's automatic renaming on priority swaps.
            row.Root.Name = "ProductionLine" + i;
            row.Root.SetMeta("production_good", entry.Good.ToString());
            row.Page = Math.Clamp(row.Page, 0, Math.Max(0, capacity - 1) / ProductionCells);
            row.Name.Text = Tr(CommodityCatalog.Get(entry.Good).Name);
            var colour = pool == ProductionPool.Civilian ? "production.civilian" : "production.military";
            row.Pool.Text = Tr(pool == ProductionPool.Civilian ? "CIVILIAN" : "MILITARY");
            row.Pool.AddThemeColorOverride("font_color", Ui.Colour(colour));
            row.Reading.Text = Tr("National output / tick");
            if (entry.Units > assigned) row.Reading.Text = Simulation.Loc.Format("{0} units suspended", entry.Units - assigned);
            row.Icon.Texture = Art("icon_" + GoodIcon(entry.Good), $"res://game/gfx/interface/icons/icon_{GoodIcon(entry.Good)}.png");
            row.Icon.Modulate = ArtColour("icon_" + GoodIcon(entry.Good), "icon.commodity");
            row.Root.TooltipText = Ui.Tip(CommodityCatalog.Get(entry.Good).Description);
            if (row.Priority.ItemCount != lines.Count)
            {
                row.Priority.Clear();
                for (var priority = 1; priority <= lines.Count; priority++) row.Priority.AddItem($"#{priority}");
            }
            row.Priority.Select(i);
            row.MinusTen.Disabled = assigned == 0;
            row.PlusTen.Disabled = a.Free(pool) == 0;
            row.Amount.MaxValue = assigned + a.Free(pool);
            if (!row.Amount.GetLineEdit().HasFocus()) row.Amount.SetValueNoSignal(assigned);
            row.Amount.Editable = capacity > 0;
            row.PagePicker.MaxValue = Math.Max(1, (capacity + ProductionCells - 1) / ProductionCells);
            if (!row.PagePicker.GetLineEdit().HasFocus()) row.PagePicker.SetValueNoSignal(row.Page + 1);
            row.PagePicker.Editable = capacity > ProductionCells;
            var factoryIcon = ProductionFactoryIcon(pool);
            for (var j = 0; j < row.Cells.Count; j++)
            {
                var unit = row.Page * ProductionCells + j + 1;
                var cell = row.Cells[j];
                cell.Name = "Cell" + j;
                cell.SetMeta("production_unit", unit);
                cell.Icon = factoryIcon;
                var filled = unit <= assigned;
                cell.Disabled = unit > assigned + a.Free(pool) || unit > capacity;
                var fill = filled ? (pool == ProductionPool.Civilian ? "production.civilian_fill" : "production.military_fill") : "production.inset";
                cell.AddThemeStyleboxOverride("normal", ProductionStyle(fill, filled ? colour : "production.empty_border", 3));
                cell.AddThemeStyleboxOverride("disabled", ProductionStyle("production.inset", "production.border", 3));
                cell.AddThemeColorOverride("icon_normal_color", Ui.Colour(filled ? colour : "production.empty_icon"));
                cell.AddThemeColorOverride("icon_hover_color", Ui.Colour("production.focus"));
                cell.AddThemeColorOverride("icon_pressed_color", Ui.Colour("production.focus"));
                cell.AddThemeColorOverride("icon_disabled_color", unit > capacity ? Colors.Transparent : Ui.Colour("production.blocked"));
                cell.TooltipText = Ui.Tip(unit > capacity ? Tr("No factories in this pool") : cell.Disabled
                    ? Tr("These units are reserved by other lines. Release them there first.")
                    : unit == assigned ? Simulation.Loc.Format("Release unit {0}; keep {1} assigned.", unit, unit - 1)
                    : Simulation.Loc.Format("Assign {0} units to this line.", unit));
            }
            row.Range.Text = capacity == 0 ? Tr("No factories in this pool") :
                Simulation.Loc.Format("Units {0}–{1} / {2}", row.Page * ProductionCells + 1,
                    Math.Min(capacity, (row.Page + 1) * ProductionCells), capacity);
            var settled = _campaign.ElapsedDays >= _campaign.Defines.EconomyTickDays;
            if (settled && player.Market.TryGetValue(entry.Good, out var quote))
            {
                row.Output.Text = Simulation.Loc.Format("{0:0.0} made / {1:0.0} wanted", quote.Production, quote.Demand);
                row.Supply.Visible = true;
                row.Supply.Value = quote.Satisfaction * 100;
                row.Output.TooltipText = Simulation.Loc.Format("Supplied: {0:P0}. Includes all national producers and imports.", quote.Satisfaction);
            }
            else
            {
                row.Output.Text = Tr("Awaiting calculation");
                row.Supply.Visible = false;
            }
            row.Updating = false;
        }
    }
    private void MoveProductionLineTo(ProductionLineRow row, int next)
    {
        var lines = CopyProductionLines(); var index = lines.FindIndex(l => l.Good == row.Good);
        if (index < 0 || next < 0 || next >= lines.Count) return;
        var moved = lines[index]; lines.RemoveAt(index); lines.Insert(next, moved);
        CommitProductionProgramme(_productionAllocation.Converted, lines);
    }
    private void CloseProductionLine(ProductionLineRow row) =>
        CommitProductionProgramme(_productionAllocation.Converted, CopyProductionLines().Where(l => l.Good != row.Good).ToList());

    private void OnProductionAddLine()
    {
        if (_productionLinePicker.Selected < 0) return;
        var good = (Commodity)_productionLinePicker.GetItemMetadata(_productionLinePicker.Selected).AsInt32();
        var lines = CopyProductionLines();
        if (lines.Any(l => l.Good == good)) return;
        var a = ProductionProgramme.Allocate(_campaign, _campaign.Player);
        lines.Add(new ProductionLine { Good = good, Units = Math.Min(1, a.Free(ProductionProgramme.PoolOf(good))) });
        CommitProductionProgramme(a.Converted, lines);
    }
    private void RefreshProductionLinePicker()
    {
        var selected = _productionLinePicker.Selected >= 0
            ? _productionLinePicker.GetItemMetadata(_productionLinePicker.Selected).AsInt32() : -1;
        _productionLinePicker.Clear();
        foreach (var good in ProductionProgramme.Lineable)
        {
            if (_campaign.Player.ProductionLines.Any(l => l.Good == good)) continue;
            var index = _productionLinePicker.ItemCount;
            _productionLinePicker.AddItem(Tr(CommodityCatalog.Get(good).Name) + " · " +
                Tr(ProductionProgramme.PoolOf(good) == ProductionPool.Civilian ? "Civilian units" : "Military units"));
            _productionLinePicker.SetItemMetadata(index, (int)good);
            if ((int)good == selected) _productionLinePicker.Select(index);
        }
        _productionAddLineButton.Disabled = _productionLinePicker.ItemCount == 0;
        _productionLinePicker.Disabled = _productionLinePicker.ItemCount == 0;
        if (_productionLinePicker.ItemCount == 0) _productionLinePicker.Text = Tr("All production lines are open");
    }

    private void RefreshProductionShortages()
    {
        if (_campaign?.Player is null) return;
        var player = _campaign.Player;
        foreach (var child in _productionShortageRows.GetChildren())
        {
            _productionShortageRows.RemoveChild(child);
            child.QueueFree();
        }

        var settled = _campaign.ElapsedDays >= _campaign.Defines.EconomyTickDays;
        if (!settled) return;

        var shortages = player.Market.Values
            .Where(quote => quote.Demand > 0.001f && quote.Satisfaction < 0.985f)
            .OrderByDescending(quote => (1f - quote.Satisfaction) * CommodityCatalog.Get(quote.Commodity).EssentialWeight)
            .Take(3)
            .ToArray();
        if (shortages.Length == 0)
        {
            var calm = Title("Nothing the economy is short of this week.", 13, Ui.Colour("state.positive"));
            _productionShortageRows.AddChild(calm);
            return;
        }
        foreach (var quote in shortages)
        {
            var definition = CommodityCatalog.Get(quote.Commodity);
            var row = Title(
                Simulation.Loc.Format("{0} · supplied {1:P0} · made {2:0.0}, wanted {3:0.0}, bought in {4:0.0}",
                    Simulation.Loc.Get(definition.Name), quote.Satisfaction, quote.Production, quote.Demand, quote.Imports),
                13, quote.Satisfaction < 0.7f ? Ui.Colour("state.negative") : Ui.Colour("state.warning"));
            row.AutowrapMode = TextServer.AutowrapMode.Off;
            row.ClipText = true;
            row.AutowrapMode = TextServer.AutowrapMode.Off;
            row.TooltipText = Ui.Tip(definition.Description);
            _productionShortageRows.AddChild(row);
        }
    }


    private void CommitProductionProgramme(int converted, List<ProductionLine> lines)
    {
        if (_campaign?.Player is null) return;
        _campaign.Commands.Enqueue(new SetProductionProgrammeCommand
        {
            IssuerId = _campaign.Player.Id, ConvertedCivilianUnits = converted, Lines = lines
        });
        _campaign.Commands.ApplyAll(_campaign);
        RefreshProductionScreen();
    }
}
