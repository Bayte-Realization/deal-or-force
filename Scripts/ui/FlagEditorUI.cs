using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public partial class DealOrForceGame
{
    /// <summary>
    /// The height a described flag is drawn at for the interface. Big enough that the dossier's
    /// largest badge is downsampled rather than stretched, the same rule the PNG flags follow,
    /// and small enough that drawing a whole mod's worth of designs costs a moment, not a pause.
    /// </summary>
    private const int FlagDesignHeight = 256;

    /// <summary>Icons used as emblems, read once as pixels: the renderer is pure C# and cannot load a texture.</summary>
    private static readonly Dictionary<string, FlagRaster> FlagEmblemRasters = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// A sprite's pixels for a flag emblem, or null when there is no such picture. Shrunk to at
    /// most 256 pixels on its long side, which is more than any emblem is ever drawn at and keeps
    /// a 2048-pixel icon from being sampled pixel by pixel into a 24-pixel flag.
    /// </summary>
    internal static FlagRaster FlagEmblemRaster(string sprite)
    {
        if (string.IsNullOrWhiteSpace(sprite)) return null;
        if (FlagEmblemRasters.TryGetValue(sprite, out var cached)) return cached;
        FlagRaster raster = null;
        var path = SpriteCatalog.TextureOf(sprite);
        if (!path.StartsWith(FlagDesignCatalog.TexturePrefix, StringComparison.Ordinal) &&
            path.Length > 0 && ResourceLoader.Exists(path) && GD.Load<Texture2D>(path)?.GetImage() is { } image)
        {
            image = (Image)image.Duplicate();
            if (image.IsCompressed()) image.Decompress();
            image.Convert(Image.Format.Rgba8);
            var longest = Math.Max(image.GetWidth(), image.GetHeight());
            if (longest > 256)
                image.Resize(Math.Max(1, image.GetWidth() * 256 / longest), Math.Max(1, image.GetHeight() * 256 / longest),
                    Image.Interpolation.Lanczos);
            raster = new FlagRaster(image.GetWidth(), image.GetHeight(), image.GetData());
        }
        return FlagEmblemRasters[sprite] = raster;
    }

    /// <summary>A described flag as a texture the interface can show, filtered through mipmaps like every flag.</summary>
    internal static ImageTexture FlagDesignTexture(FlagDesign design, int height)
    {
        var raster = design.Render(height, FlagEmblemRaster);
        var image = Image.CreateFromData(raster.Width, raster.Height, false, Image.Format.Rgba8, raster.Pixels);
        if (height >= 64) image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }

    /// <summary>
    /// The flag editor as a workshop window: from the scenario library, from the country editor
    /// and from the player's own country.
    /// </summary>
    /// <param name="tag">The country whose flag is edited; empty asks for one.</param>
    /// <param name="back">Where its way out leads; the workshop's own exit when null.</param>
    private void OpenFlagEditor(string tag, Action back = null)
    {
        if (_networkConnected || _isLoading) return;
        var rows = AuthorWindow("Flag editor", back, back is null ? "Close" : "Back", () => OpenFlagEditor(tag, back));
        BuildFlagEditor(rows, tag, () => _authorDirty = true);
    }

    /// <summary>
    /// The flag editor in a window of its own, over whatever is already open - the map editor's
    /// country inspector, where replacing the workshop window would throw away an unsaved draft.
    /// </summary>
    private void OpenFlagEditorWindow(string tag)
    {
        var host = IsInstanceValid(_authorCanvas) ? (Node)_authorCanvas : this;
        var viewport = GetViewport().GetVisibleRect().Size;
        var window = new Window
        {
            Title = Tr("Flag editor"),
            Exclusive = true,
            Size = new Vector2I(Math.Min(1280, (int)viewport.X - 40), Math.Min(860, (int)viewport.Y - 72)),
            Theme = ScenarioEditorTheme.Create()
        };
        host.AddChild(window);
        window.CloseRequested += () => window.QueueFree();
        var surface = new PanelContainer();
        window.AddChild(surface);
        surface.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        surface.AddThemeStyleboxOverride("panel", ScenarioEditorTheme.Surface("shell.background", "shell.border", 0));
        var rows = PanelRows(surface, 16, 14, 8);
        BuildFlagEditor(rows, tag, null);
        window.PopupCentered();
    }

    /// <summary>
    /// The editor itself, built into whatever holds it.
    ///
    /// Left: what is being edited and how - the target flag, the background, the emblems. Right:
    /// the flag at the sizes the game actually draws it (a dossier, a list row, a map marker),
    /// because a design that reads at 160 pixels and turns to mud at 20 is the commonest way a
    /// flag goes wrong, and the file it will be saved as, so a modder sees the format by using it.
    /// </summary>
    private void BuildFlagEditor(VBoxContainer rows, string tag, Action dirty)
    {
        tag = (tag ?? "").Trim().ToUpperInvariant();
        rows.AddChild(Title("Design a flag from a background and emblems. It is saved as a flag description in your mod (common/flag_designs) and flown at once.", 14, Ui.Colour("text.muted")));

        var columns = new HBoxContainer { SizeFlagsVertical = SizeFlags.ExpandFill };
        columns.AddThemeConstantOverride("separation", 18);
        rows.AddChild(columns);
        var leftScroll = new ScrollContainer
        {
            SizeFlagsHorizontal = SizeFlags.ExpandFill, SizeFlagsVertical = SizeFlags.ExpandFill,
            HorizontalScrollMode = ScrollContainer.ScrollMode.Disabled, CustomMinimumSize = new Vector2(520, 0)
        };
        columns.AddChild(leftScroll);
        var left = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddThemeConstantOverride("separation", 6);
        leftScroll.AddChild(left);
        var right = new VBoxContainer { SizeFlagsHorizontal = SizeFlags.ExpandFill, CustomMinimumSize = new Vector2(420, 0) };
        right.AddThemeConstantOverride("separation", 8);
        columns.AddChild(right);

        var design = new FlagDesign();
        var building = false;
        Action redraw = null;

        HBoxContainer Row(string caption)
        {
            var row = new HBoxContainer();
            row.AddThemeConstantOverride("separation", 8);
            var label = Title(caption, 13, Ui.Colour("text.muted"));
            label.CustomMinimumSize = new Vector2(150, 0);
            label.AutowrapMode = TextServer.AutowrapMode.Off;
            row.AddChild(label);
            left.AddChild(row);
            return row;
        }

        // ---- What is being edited ----
        left.AddChild(Title("Flag", 13, Ui.Colour("text.accent"), true));
        var targetRow = Row("Country tag");
        var tagEdit = new LineEdit { Text = tag, MaxLength = 8, CustomMinimumSize = new Vector2(110, 32), PlaceholderText = "ABC" };
        targetRow.AddChild(tagEdit);
        var variantRow = Row("Flown while");
        var variant = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        variant.AddItem(Tr("Always (the country's own flag)"));
        variant.SetItemMetadata(0, "");
        foreach (var ideology in IdeologyCatalog.All)
        {
            variant.AddItem(Tr("Ideology") + ": " + Tr(ideology.Name.Length > 0 ? ideology.Name : ideology.Id));
            variant.SetItemMetadata(variant.ItemCount - 1, ideology.Id);
        }
        foreach (var government in GovernmentCatalog.All)
        {
            variant.AddItem(Tr("Government") + ": " + Tr(government.Name.Length > 0 ? government.Name : government.Id));
            variant.SetItemMetadata(variant.ItemCount - 1, government.Id);
        }
        variantRow.AddChild(variant);
        var idLabel = Title("", 12, Ui.Colour("text.caption"));
        left.AddChild(idLabel);
        string CurrentId()
        {
            var stem = new string(tagEdit.Text.Trim().ToLowerInvariant().Where(char.IsLetterOrDigit).ToArray());
            var qualifier = variant.GetSelectedMetadata().AsString();
            return stem.Length == 0 ? "" : "flag_" + stem + (qualifier.Length > 0 ? "_" + FlagCatalog.Slug(qualifier) : "");
        }

        // ---- Background ----
        left.AddChild(Title("Background", 13, Ui.Colour("text.accent"), true));
        var patternRow = Row("Pattern");
        var pattern = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        foreach (var id in FlagDesign.Patterns) pattern.AddItem(Tr("flag_pattern_" + id));
        patternRow.AddChild(pattern);
        var ratioRow = Row("Proportions");
        var ratio = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var ratios = new[] { (1f, "1:1"), (1.25f, "4:5"), (4f / 3f, "3:4"), (1.5f, "2:3"), (1.6f, "5:8"), (5f / 3f, "3:5"), (1.75f, "4:7"), (1.9f, "10:19"), (2f, "1:2") };
        foreach (var (_, name) in ratios) ratio.AddItem(name);
        ratioRow.AddChild(ratio);
        var countRow = Row("Colours");
        var count = new SpinBox { MinValue = 1, MaxValue = 5, Step = 1, CustomMinimumSize = new Vector2(110, 32) };
        countRow.AddChild(count);

        // One row per colour slot: an exact hex field beside a picker whose presets are the
        // palette the base game's own flags use, so "the same red as Canada" is one click and
        // "#c8102e exactly" is a paste.
        (HBoxContainer Row, ColorPickerButton Picker, LineEdit Hex) ColourSlot(string caption, Action<string> set)
        {
            var row = Row(caption);
            var picker = new ColorPickerButton { CustomMinimumSize = new Vector2(64, 32), EditAlpha = false };
            foreach (var preset in FlagDesign.Palette) picker.GetPicker().AddPreset(Color.FromHtml(preset));
            var hex = new LineEdit { CustomMinimumSize = new Vector2(110, 32), MaxLength = 7, PlaceholderText = "#rrggbb" };
            row.AddChild(picker);
            row.AddChild(hex);
            picker.ColorChanged += colour =>
            {
                if (building) return;
                var text = "#" + colour.ToHtml(false).ToLowerInvariant();
                hex.Text = text;
                set(text);
                redraw();
            };
            hex.TextSubmitted += _ => Apply();
            hex.FocusExited += Apply;
            void Apply()
            {
                if (building) return;
                if (FlagDesign.Normalise(hex.Text) is not { } text) { hex.Modulate = new Color(1, .55f, .55f); return; }
                hex.Modulate = Colors.White;
                hex.Text = text;
                picker.Color = Color.FromHtml(text);
                set(text);
                redraw();
            }
            return (row, picker, hex);
        }

        var slots = new List<(HBoxContainer Row, ColorPickerButton Picker, LineEdit Hex)>();
        for (var i = 0; i < 5; i++)
        {
            var index = i;
            slots.Add(ColourSlot(Tr("Colour") + " " + (i + 1), text =>
            {
                while (design.Colours.Count <= index) design.Colours.Add("#ffffff");
                design.Colours[index] = text;
            }));
        }
        var accentSlot = ColourSlot("Canton / triangle", text => design.Accent = text);
        var weightsRow = Row("Stripe widths");
        var weights = new LineEdit
        {
            PlaceholderText = Tr("equal, or e.g. 1, 2, 1"), SizeFlagsHorizontal = SizeFlags.ExpandFill,
            TooltipText = Ui.Tip(Tr("Relative widths of the stripes, one number per colour. Spain is 1, 2, 1."))
        };
        weightsRow.AddChild(weights);

        // ---- Emblems ----
        left.AddChild(Title("Emblems", 13, Ui.Colour("text.accent"), true));
        var emblemList = new ItemList { CustomMinimumSize = new Vector2(0, 96), SizeFlagsHorizontal = SizeFlags.ExpandFill };
        left.AddChild(emblemList);
        var emblemButtons = new HFlowContainer();
        left.AddChild(emblemButtons);
        var kindRow = Row("Emblem");
        var kind = new OptionButton { SizeFlagsHorizontal = SizeFlags.ExpandFill };
        var kinds = new List<(string Shape, string Sprite)>();
        foreach (var shape in FlagDesign.Shapes) { kind.AddItem(Tr("flag_shape_" + shape)); kinds.Add((shape, "")); }
        // The emblem sets the game already has: its ideology marks first, then every interface
        // icon, so a modder's own icons dropped into gfx/ are offered here without a list.
        foreach (var sprite in SpriteCatalog.Names.Where(n => n.StartsWith("icon_ideology_", StringComparison.OrdinalIgnoreCase))
                     .Concat(SpriteCatalog.Names.Where(n => n.StartsWith("icon_", StringComparison.OrdinalIgnoreCase) &&
                                                            !n.StartsWith("icon_ideology_", StringComparison.OrdinalIgnoreCase)))
                     .Distinct(StringComparer.OrdinalIgnoreCase).Take(400))
        {
            kind.AddItem(sprite);
            kinds.Add(("", sprite));
        }
        kindRow.AddChild(kind);
        FlagEmblem Selected() =>
            emblemList.GetSelectedItems() is { Length: > 0 } picked && picked[0] < design.Emblems.Count ? design.Emblems[picked[0]] : null;
        var emblemColour = ColourSlot("Emblem colour", text => { if (Selected() is { } e) e.Colour = text; });
        var ownColours = new CheckBox { Text = Tr("Keep the picture's own colours") };
        emblemColour.Row.AddChild(ownColours);
        SpinBox Number(string caption, double min, double max, double step, Action<FlagEmblem, float> set)
        {
            var row = Row(caption);
            var box = new SpinBox { MinValue = min, MaxValue = max, Step = step, CustomMinimumSize = new Vector2(120, 32) };
            row.AddChild(box);
            box.ValueChanged += value =>
            {
                if (building || Selected() is not { } e) return;
                set(e, (float)value);
                redraw();
            };
            return box;
        }
        var ex = Number("Across (0-1)", -0.5, 1.5, 0.01, (e, v) => e.X = v);
        var ey = Number("Down (0-1)", -0.5, 1.5, 0.01, (e, v) => e.Y = v);
        var esize = Number("Size (share of height)", 0.02, 2, 0.01, (e, v) => e.Size = v);
        var erotation = Number("Rotation (degrees)", -360, 360, 1, (e, v) => e.Rotation = v);
        var ecount = Number("Stars in a ring", 3, 50, 1, (e, v) => e.Count = (int)v);
        var emblemControls = new Control[] { kindRow, emblemColour.Row, ex.GetParent<Control>(), ey.GetParent<Control>(),
            esize.GetParent<Control>(), erotation.GetParent<Control>(), ecount.GetParent<Control>() };

        // ---- Preview and file ----
        right.AddChild(Title("Preview", 13, Ui.Colour("text.accent"), true));
        var previews = new List<(TextureRect Rect, int Height)>();
        foreach (var (height, caption) in new[] { (160, "Dossier"), (48, "Lists"), (20, "Map marker") })
        {
            var line = new HBoxContainer();
            line.AddThemeConstantOverride("separation", 10);
            right.AddChild(line);
            var frame = new PanelContainer { SizeFlagsHorizontal = SizeFlags.ShrinkBegin, SizeFlagsVertical = SizeFlags.ShrinkCenter };
            var border = new StyleBoxFlat
            {
                BgColor = Ui.Colour("surface.inset"), BorderColor = Ui.Colour("surface.line"),
                ContentMarginLeft = 1, ContentMarginRight = 1, ContentMarginTop = 1, ContentMarginBottom = 1
            };
            border.SetBorderWidthAll(1);
            frame.AddThemeStyleboxOverride("panel", border);
            var rect = new TextureRect
            {
                ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
                StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
                TextureFilter = TextureFilterEnum.LinearWithMipmaps
            };
            frame.AddChild(rect);
            line.AddChild(frame);
            line.AddChild(Title(caption, 12, Ui.Colour("text.caption")));
            previews.Add((rect, height));
        }
        var document = new CodeEdit
        {
            Editable = false, GuttersDrawLineNumbers = true, SizeFlagsVertical = SizeFlags.ExpandFill,
            CustomMinimumSize = new Vector2(0, 180)
        };
        right.AddChild(document);
        var status = Title("", 13, Ui.Colour("text.caption"));
        rows.AddChild(status);
        var actions = new HFlowContainer();
        rows.AddChild(actions);

        void RefreshEmblemList()
        {
            var keep = emblemList.GetSelectedItems() is { Length: > 0 } s ? s[0] : -1;
            emblemList.Clear();
            for (var i = 0; i < design.Emblems.Count; i++)
            {
                var e = design.Emblems[i];
                emblemList.AddItem($"{i + 1}. {(e.Sprite.Length > 0 ? e.Sprite : Tr("flag_shape_" + e.Shape))}");
            }
            if (design.Emblems.Count > 0) emblemList.Select(Math.Clamp(keep < 0 ? 0 : keep, 0, design.Emblems.Count - 1));
            ShowEmblem();
        }

        void ShowEmblem()
        {
            var e = Selected();
            foreach (var control in emblemControls) control.Visible = e is not null;
            if (e is null) return;
            building = true;
            var at = kinds.FindIndex(k => e.Sprite.Length > 0 ? string.Equals(k.Sprite, e.Sprite, StringComparison.OrdinalIgnoreCase) : k.Shape == e.Shape);
            if (at < 0 && e.Sprite.Length > 0) { kind.AddItem(e.Sprite); kinds.Add(("", e.Sprite)); at = kinds.Count - 1; }
            kind.Selected = Math.Max(0, at);
            ownColours.Visible = e.Sprite.Length > 0;
            ownColours.ButtonPressed = e.Sprite.Length > 0 && e.Colour.Length == 0;
            emblemColour.Picker.Color = Color.FromHtml(e.Colour.Length > 0 ? e.Colour : "#ffffff");
            emblemColour.Hex.Text = e.Colour;
            ex.Value = e.X; ey.Value = e.Y; esize.Value = e.Size; erotation.Value = e.Rotation; ecount.Value = e.Count;
            ecount.GetParent<Control>().Visible = e.Shape == "star_ring";
            building = false;
        }

        void ShowDesign()
        {
            building = true;
            pattern.Selected = Math.Max(0, Array.IndexOf(FlagDesign.Patterns, design.Pattern));
            var nearest = 0;
            for (var i = 1; i < ratios.Length; i++)
                if (MathF.Abs(ratios[i].Item1 - design.Ratio) < MathF.Abs(ratios[nearest].Item1 - design.Ratio)) nearest = i;
            ratio.Selected = nearest;
            var (min, max) = FlagDesign.ColourRange(design.Pattern);
            count.MinValue = min; count.MaxValue = max;
            count.Value = Math.Clamp(design.Colours.Count, min, max);
            for (var i = 0; i < slots.Count; i++)
            {
                slots[i].Row.Visible = i < count.Value;
                var colour = i < design.Colours.Count ? design.Colours[i] : "#ffffff";
                slots[i].Picker.Color = Color.FromHtml(colour);
                slots[i].Hex.Text = colour;
            }
            accentSlot.Row.Visible = FlagDesign.UsesAccent(design.Pattern);
            accentSlot.Picker.Color = Color.FromHtml(design.Accent.Length > 0 ? design.Accent : "#0038a8");
            accentSlot.Hex.Text = design.Accent;
            weightsRow.Visible = design.Pattern is "horizontal" or "vertical" or "canton" or "hoist_triangle";
            weights.Text = string.Join(", ", design.Weights.Select(w => w.ToString("0.##", CultureInfo.InvariantCulture)));
            building = false;
            RefreshEmblemList();
        }

        redraw = () =>
        {
            if (building) return;
            design.Id = CurrentId();
            var (min, max) = FlagDesign.ColourRange(design.Pattern);
            while (design.Colours.Count < Math.Max(min, (int)count.Value)) design.Colours.Add("#ffffff");
            if (design.Colours.Count > (int)count.Value) design.Colours.RemoveRange((int)count.Value, design.Colours.Count - (int)count.Value);
            if (design.Weights.Count != design.Colours.Count) design.Weights.Clear();
            if (FlagDesign.UsesAccent(design.Pattern) && design.Accent.Length == 0) design.Accent = "#0038a8";
            foreach (var (rect, height) in previews)
            {
                rect.Texture = FlagDesignTexture(design, height <= 24 ? height * 2 : height);
                rect.CustomMinimumSize = new Vector2(Mathf.Round(height * design.Ratio), height);
            }
            idLabel.Text = design.Id.Length == 0 ? Tr("Enter a country tag.") :
                Tr("Saved as") + ": common/flag_designs/" + design.Id + ".json" +
                (FlagDesignCatalog.Has(design.Id) ? "  ·  " + Tr("replaces the design already loaded") : "");
            document.Text = design.Id.Length == 0 ? "" : design.ToDocument();
            dirty?.Invoke();
        };

        void LoadTarget(bool fromCatalogue)
        {
            var id = CurrentId();
            if (fromCatalogue && FlagDesignCatalog.Find(id) is { } saved)
            {
                design = saved.Clone();
                status.Text = Tr("Loaded the saved design") + ": " + id;
            }
            else if (fromCatalogue && design.Emblems.Count == 0 && _campaign?.NationByTag(tagEdit.Text.Trim()) is { } nation)
            {
                // A first draft in the country's own map colour, so the editor opens on
                // something that belongs to the country rather than on somebody else's tricolour.
                var own = FlagDesign.Normalise(nation.Color) ?? "#0038a8";
                design = new FlagDesign { Pattern = "horizontal", Colours = new List<string> { own, "#ffffff", own }, Weights = new List<float> { 1, 2, 1 } };
                design.Emblems.Add(new FlagEmblem { Shape = "star", Colour = own, X = 0.5f, Y = 0.5f, Size = 0.36f });
                status.Text = Tr("No design saved for this flag yet: started from the country's colour.");
            }
            ShowDesign();
            redraw();
        }

        tagEdit.TextSubmitted += _ => LoadTarget(true);
        tagEdit.FocusExited += () => LoadTarget(true);
        variant.ItemSelected += _ => LoadTarget(true);
        pattern.ItemSelected += index =>
        {
            if (building) return;
            design.Pattern = FlagDesign.Patterns[(int)index];
            var (min, max) = FlagDesign.ColourRange(design.Pattern);
            while (design.Colours.Count < min) design.Colours.Add(design.Colours.Count % 2 == 0 ? "#ffffff" : "#ce1126");
            if (design.Colours.Count > max) design.Colours.RemoveRange(max, design.Colours.Count - max);
            ShowDesign();
            redraw();
        };
        ratio.ItemSelected += index => { if (building) return; design.Ratio = ratios[(int)index].Item1; redraw(); };
        count.ValueChanged += value =>
        {
            if (building) return;
            while (design.Colours.Count < (int)value) design.Colours.Add(FlagDesign.Palette[(design.Colours.Count + 2) % FlagDesign.Palette.Length]);
            if (design.Colours.Count > (int)value) design.Colours.RemoveRange((int)value, design.Colours.Count - (int)value);
            ShowDesign();
            redraw();
        };
        weights.TextSubmitted += _ => ApplyWeights();
        weights.FocusExited += ApplyWeights;
        void ApplyWeights()
        {
            if (building) return;
            var parsed = weights.Text.Split(new[] { ',', ' ', ';' }, StringSplitOptions.RemoveEmptyEntries)
                .Select(part => float.TryParse(part, NumberStyles.Float, CultureInfo.InvariantCulture, out var w) && w > 0 ? w : -1f).ToList();
            design.Weights = parsed.Count == design.Colours.Count && parsed.All(w => w > 0) ? parsed : new List<float>();
            weights.Modulate = parsed.Count == 0 || design.Weights.Count > 0 ? Colors.White : new Color(1, .55f, .55f);
            redraw();
        }
        emblemList.ItemSelected += _ => ShowEmblem();
        kind.ItemSelected += index =>
        {
            if (building || Selected() is not { } e) return;
            var (shape, sprite) = kinds[(int)index];
            e.Shape = shape; e.Sprite = sprite;
            if (sprite.Length == 0 && e.Colour.Length == 0) e.Colour = "#ffffff";
            RefreshEmblemList();
            redraw();
        };
        ownColours.Toggled += on =>
        {
            if (building || Selected() is not { } e) return;
            e.Colour = on ? "" : (FlagDesign.Normalise(emblemColour.Hex.Text) ?? "#ffffff");
            ShowEmblem();
            redraw();
        };
        emblemButtons.AddChild(ActionButton("Add emblem", () =>
        {
            design.Emblems.Add(new FlagEmblem { Shape = "star", Colour = "#ffffff", X = 0.5f, Y = 0.5f, Size = 0.4f });
            RefreshEmblemList();
            emblemList.Select(design.Emblems.Count - 1);
            ShowEmblem();
            redraw();
        }, 150, 34));
        emblemButtons.AddChild(ActionButton("Remove emblem", () =>
        {
            if (emblemList.GetSelectedItems() is not { Length: > 0 } picked) return;
            design.Emblems.RemoveAt(picked[0]);
            RefreshEmblemList();
            redraw();
        }, 150, 34));
        emblemButtons.AddChild(ActionButton("Move up", () =>
        {
            if (emblemList.GetSelectedItems() is not { Length: > 0 } picked || picked[0] == 0) return;
            var i = picked[0];
            (design.Emblems[i - 1], design.Emblems[i]) = (design.Emblems[i], design.Emblems[i - 1]);
            RefreshEmblemList();
            emblemList.Select(i - 1);
            ShowEmblem();
            redraw();
        }, 120, 34));

        actions.AddChild(ActionButton("Save flag to my mod", () =>
        {
            design.Id = CurrentId();
            if (!FlagDesignCatalog.IsValidId(design.Id)) { status.Text = Tr("Enter a country tag of letters and digits."); return; }
            try
            {
                ContentWorkbench.EnsureMod(AuthorRoot);
                var path = ContentWorkbench.Write(AuthorRoot, $"common/flag_designs/{design.Id}.json", design.ToDocument());
                // A picture of it as well, outside every folder the game reads: for a modder who
                // wants to finish it by hand, or a store page. The game itself draws the
                // description and never needs this file.
                var exports = Path.Combine(AuthorRoot, "exports", "flags");
                Directory.CreateDirectory(exports);
                var raster = design.Render(512, FlagEmblemRaster);
                Image.CreateFromData(raster.Width, raster.Height, false, Image.Format.Rgba8, raster.Pixels)
                    .SavePng(Path.Combine(exports, design.Id + ".png"));
                FlagDesignCatalog.Put(design);
                if (_campaign is not null) Refresh();
                if (dirty is not null) _authorDirty = false;
                status.Text = Tr("Saved and flown now. Kept in the Editor workspace mod for the next start:") + " " + path;
            }
            catch (Exception e) { status.Text = e.Message; }
        }, 240, 40, true));
        actions.AddChild(ActionButton("Revert to saved", () => LoadTarget(true), 180, 40));
        actions.AddChild(ActionButton("Open mod folder", () => { ContentWorkbench.EnsureMod(AuthorRoot); OS.ShellOpen(AuthorRoot); }, 180, 40));

        LoadTarget(true);
        // Opening is not an edit: only what the player changes from here asks before closing.
        if (dirty is not null) _authorDirty = false;
    }
}
