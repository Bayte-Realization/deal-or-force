using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Shared material palette, typography, and architectural trim for outer-game and meta-UI
/// screens (Main Menu, Country Selection, Save/Load, Pause, Settings, Mod Menu, Loading).
///
/// Reads semantic `shell.*` tokens via <see cref="Ui"/> with warm umber-brass-paper
/// fallbacks so every screen renders coherently standalone today and unifies automatically
/// once merged with updated `00_skin.json` palettes or mod era overlays.
/// </summary>
internal static class MenuChromeTheme
{
    private static readonly Dictionary<string, StyleBox> BoxCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Texture2D> TextureCache = new(StringComparer.Ordinal);
    private static readonly Dictionary<string, Font> FontCache = new(StringComparer.Ordinal);
    private static StyleBoxFlat _focusBox;
    private static bool _wiredSkinInvalidation;

    static MenuChromeTheme() => EnsureInvalidationHook();

    private static void EnsureInvalidationHook()
    {
        if (_wiredSkinInvalidation) return;
        _wiredSkinInvalidation = true;
        InterfaceSkin.Changed += ClearCaches;
        FontCatalog.Changed += ClearFontCaches;
    }

    internal static void ClearCaches()
    {
        BoxCache.Clear();
        TextureCache.Clear();
        _focusBox = null;
    }

    private static void ClearFontCaches() => FontCache.Clear();

    /// <summary>
    /// Resolves a semantic skin token when declared, otherwise uses the Revision 3 warm fallback.
    /// </summary>
    internal static Color Token(string token, string warmFallbackHex, float alpha = 1f)
    {
        var color = InterfaceSkin.Has(token) ? Ui.Colour(token) : Color.FromHtml(warmFallbackHex);
        color.A *= alpha;
        return color;
    }

    internal static Color PlateTop => Token("shell.plate_top", "#332c25");
    internal static Color PlateBottom => Token("shell.plate_bottom", "#221d19");
    internal static Color PlateHover => Token("shell.plate_hover", "#3d342c");
    internal static Color BodyTop => Token("shell.body_top", "#2a241e");
    internal static Color BodyBottom => Token("shell.body_bottom", "#1f1a16");
    internal static Color WellTop => Token("shell.well_top", "#0e0b08");
    internal static Color WellBottom => Token("shell.well_bottom", "#16120e");
    internal static Color BrassHi => Token("shell.brass_hi", "#a48d68");
    internal static Color Brass => Token("shell.brass", "#7a6649");
    internal static Color BrassShadow => Token("shell.brass_shadow", "#43382b");
    internal static Color BorderDark => Token("shell.border", "#0e0c0a");
    internal static Color RuleDark => Token("shell.rule_dark", "#0d0b09");
    internal static Color RuleLight => Token("shell.engrave", "#4d4336");
    internal static Color JournalRule => Token("shell.journal_rule", "#5a4d3f");
    internal static Color Paper => Token("shell.paper", "#efe4c8");
    internal static Color PaperDim => Token("shell.paper_muted", "#cfc5ab");
    internal static Color PaperFaint => Token("shell.paper_faint", "#a39983");
    internal static Color PaperGhost => Token("shell.paper_ghost", "#6f6757");
    internal static Color Highlight => Token("shell.highlight", "#f3e7ca");
    internal static Color PauseAmber => Token("shell.pause_amber", "#d89a3c");
    internal static Color Positive => Token("shell.state.positive", "#4fb477");
    internal static Color Negative => Token("shell.state.negative", "#c94f55");
    internal static Color Warning => Token("shell.state.warning", "#e7b058");
    internal static Color Info => Token("shell.state.info", "#77c9e8");
    internal static Color VeilColor => WellTop with { A = 0.80f };
    internal static Color BackdropColor => new(WellTop.R * 0.86f, WellTop.G * 0.86f, WellTop.B * 0.86f, 1f);

    internal static Font SerifDisplay(bool bold = false)
    {
        var role = bold ? "shell.serif_bold" : "shell.serif_semibold";
        if (FontCache.TryGetValue(role, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var font = PresentationFonts.For(role);
        if (font == ThemeDB.FallbackFont) font = PresentationFonts.For("ui.display");
        return FontCache[role] = font;
    }

    internal static Font SerifBody()
    {
        const string role = "shell.serif";
        if (FontCache.TryGetValue(role, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var font = PresentationFonts.For(role);
        if (font == ThemeDB.FallbackFont) font = PresentationFonts.For("ui.display");
        return FontCache[role] = font;
    }

    internal static Font SansBody()
    {
        const string role = "shell.sans";
        if (FontCache.TryGetValue(role, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var font = PresentationFonts.For(role);
        if (font == ThemeDB.FallbackFont) font = PresentationFonts.For("ui.default");
        return FontCache[role] = font;
    }

    internal static Font SansTabular()
    {
        const string key = "shell.sans:tnum";
        if (FontCache.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var font = PresentationFonts.Tabular("shell.sans");
        return FontCache[key] = font;
    }

    internal static Font TrackedCaps()
    {
        const string key = "shell.sans:tracked";
        if (FontCache.TryGetValue(key, out var cached) && GodotObject.IsInstanceValid(cached)) return cached;
        var variation = new FontVariation
        {
            BaseFont = SansBody(),
            SpacingGlyph = 1
        };
        return FontCache[key] = variation;
    }

    internal static StyleBoxFlat FocusBox()
    {
        if (_focusBox is not null) return _focusBox;
        var style = new StyleBoxFlat
        {
            DrawCenter = false,
            BorderColor = new Color(BrassHi, 0.88f)
        };
        style.SetBorderWidthAll(2);
        style.SetCornerRadiusAll(3);
        style.SetContentMarginAll(0);
        return _focusBox = style;
    }

    /// <summary>
    /// Smooth vertical gradient 9-slice without artificial grain on dark plates (Rev 3 pass 2).
    /// </summary>
    internal static StyleBox GradientPlateBox(
        Color top, Color bottom, Color outerBorder,
        Color? innerTopLine = null, Color? innerBottomLine = null,
        Color? brassEdge = null, int borderThickness = 1,
        int padLeft = 14, int padTop = 8, int padRight = 14, int padBottom = 8)
    {
        var key = $"gp|{top.ToHtml()}|{bottom.ToHtml()}|{outerBorder.ToHtml()}|" +
                  $"{innerTopLine?.ToHtml() ?? "-"}|{innerBottomLine?.ToHtml() ?? "-"}|" +
                  $"{brassEdge?.ToHtml() ?? "-"}|{borderThickness}|{padLeft},{padTop},{padRight},{padBottom}";
        if (BoxCache.TryGetValue(key, out var cached)) return cached;

        var texKey = $"gt|{top.ToHtml()}|{bottom.ToHtml()}|{outerBorder.ToHtml()}|" +
                     $"{innerTopLine?.ToHtml() ?? "-"}|{innerBottomLine?.ToHtml() ?? "-"}|" +
                     $"{brassEdge?.ToHtml() ?? "-"}|{borderThickness}";
        if (!TextureCache.TryGetValue(texKey, out var texture))
        {
            const int w = 24;
            const int h = 32;
            using var img = Image.CreateEmpty(w, h, false, Image.Format.Rgba8);
            var brassLo = brassEdge.HasValue ? brassEdge.Value.Lerp(BrassShadow, 0.55f) : outerBorder;
            for (var y = 0; y < h; y++)
            {
                var v = y / (float)(h - 1);
                var fill = top.Lerp(bottom, v);
                for (var x = 0; x < w; x++)
                {
                    var dx = Math.Min(x, w - 1 - x);
                    var dy = Math.Min(y, h - 1 - y);
                    var d = Math.Min(dx, dy);
                    Color px;
                    if (d == 0)
                    {
                        px = outerBorder;
                    }
                    else if (brassEdge.HasValue && d <= borderThickness)
                    {
                        px = brassEdge.Value.Lerp(brassLo, v * 0.72f);
                    }
                    else
                    {
                        px = fill;
                        var innerStart = 1 + (brassEdge.HasValue ? borderThickness : 0);
                        if (y == innerStart && innerTopLine.HasValue)
                            px = fill.Lerp(innerTopLine.Value, innerTopLine.Value.A);
                        else if (y == h - 1 - innerStart && innerBottomLine.HasValue)
                            px = fill.Lerp(innerBottomLine.Value, innerBottomLine.Value.A);
                    }
                    img.SetPixel(x, y, px);
                }
            }
            texture = ImageTexture.CreateFromImage(img);
            TextureCache[texKey] = texture;
        }

        const int slice = 6;
        var box = new StyleBoxTexture
        {
            Texture = texture,
            TextureMarginLeft = slice,
            TextureMarginTop = slice,
            TextureMarginRight = slice,
            TextureMarginBottom = slice,
            ContentMarginLeft = padLeft,
            ContentMarginTop = padTop,
            ContentMarginRight = padRight,
            ContentMarginBottom = padBottom
        };
        return BoxCache[key] = box;
    }

    internal static StyleBox WellBox(int padX = 10, int padY = 8, bool highlighted = false) =>
        GradientPlateBox(
            WellTop,
            WellBottom,
            BorderDark,
            innerTopLine: new Color(0f, 0f, 0f, 0.55f),
            innerBottomLine: new Color(Paper, highlighted ? 0.14f : 0.06f),
            brassEdge: highlighted ? Brass : RuleLight,
            borderThickness: 1,
            padLeft: padX, padTop: padY, padRight: padX, padBottom: padY);

    internal static StyleBox RecessedBodyBox(int padX = 12, int padY = 10) =>
        GradientPlateBox(
            BodyTop,
            BodyBottom,
            BorderDark,
            innerTopLine: new Color(Paper, 0.07f),
            innerBottomLine: new Color(0f, 0f, 0f, 0.45f),
            brassEdge: RuleLight,
            borderThickness: 1,
            padLeft: padX, padTop: padY, padRight: padX, padBottom: padY);

    internal static StyleBox ModalPlateBox(int padX = 0, int padY = 0) =>
        GradientPlateBox(
            PlateTop,
            PlateBottom,
            BorderDark,
            innerTopLine: new Color(Paper, 0.12f),
            innerBottomLine: new Color(0f, 0f, 0f, 0.55f),
            brassEdge: null,
            borderThickness: 1,
            padLeft: padX, padTop: padY, padRight: padX, padBottom: padY);

    /// <summary>
    /// Dresses a panel as an architectural warm-umber plate with restrained chamfered
    /// antique-brass corner trim and prevents cold AtlasPanelFinish overlays from double-drawing.
    /// </summary>
    internal static void StyleArchitecturalPlate(PanelContainer panel, bool brassCorners = true, int padX = 0, int padY = 0)
    {
        if (panel is null) return;
        panel.SetMeta(MaterialStyles.MaterialMeta, "menu_chrome");
        if (panel.GetNodeOrNull<Node>("AtlasMaterial") is { } oldAtlas)
        {
            oldAtlas.QueueFree();
        }
        if (panel.GetNodeOrNull<Node>("ShellEdge") is { } oldEdge)
        {
            oldEdge.QueueFree();
        }
        panel.AddThemeStyleboxOverride("panel", ModalPlateBox(padX, padY));

        if (!brassCorners) return;
        if (panel.GetNodeOrNull<MenuChromeBrassFrame>("MenuChromeBrassFrame") is null)
        {
            var frame = new MenuChromeBrassFrame { Name = "MenuChromeBrassFrame", ZIndex = 4 };
            panel.AddChild(frame);
        }
    }

    internal static Control CreateEngravedRule(string name = "EngravedRule", float verticalMargin = 2f)
    {
        var rule = new MenuChromeEngravedRule
        {
            Name = name,
            CustomMinimumSize = new Vector2(0f, 2f + verticalMargin * 2f),
            SizeFlagsHorizontal = Control.SizeFlags.ExpandFill,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        rule.SetMeta("rule_pad", verticalMargin);
        return rule;
    }

    internal static void StylePrimaryCta(Button button, int fontSize = 16, bool serif = true)
    {
        if (button is null) return;
        var normal = GradientPlateBox(
            PlateTop.Darkened(0.10f), PlateBottom.Darkened(0.18f), BorderDark,
            innerTopLine: new Color(Paper, 0.14f), innerBottomLine: new Color(0f, 0f, 0f, 0.55f),
            brassEdge: BrassHi, borderThickness: 2, padLeft: 16, padTop: 6, padRight: 16, padBottom: 6);
        var hover = GradientPlateBox(
            PlateHover, PlateBottom, BorderDark,
            innerTopLine: new Color(Highlight, 0.20f), innerBottomLine: new Color(0f, 0f, 0f, 0.45f),
            brassEdge: BrassHi.Lightened(0.14f), borderThickness: 2, padLeft: 16, padTop: 6, padRight: 16, padBottom: 6);
        var pressed = GradientPlateBox(
            WellTop.Lightened(0.06f), WellBottom, BorderDark,
            innerTopLine: new Color(0f, 0f, 0f, 0.65f), innerBottomLine: new Color(Brass, 0.25f),
            brassEdge: Brass, borderThickness: 2, padLeft: 16, padTop: 7, padRight: 16, padBottom: 5);
        var disabled = GradientPlateBox(
            PlateBottom.Darkened(0.18f), WellBottom, BorderDark,
            innerTopLine: null, innerBottomLine: null,
            brassEdge: BrassShadow, borderThickness: 1, padLeft: 16, padTop: 6, padRight: 16, padBottom: 6);

        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", disabled);
        button.AddThemeStyleboxOverride("focus", FocusBox());
        button.AddThemeFontOverride("font", serif ? SerifDisplay() : SansBody());
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", Paper);
        button.AddThemeColorOverride("font_hover_color", Highlight);
        button.AddThemeColorOverride("font_pressed_color", Paper);
        button.AddThemeColorOverride("font_focus_color", Highlight);
        button.AddThemeColorOverride("font_disabled_color", PaperGhost);
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    internal static void StyleWellButton(Button button, bool selected = false, int fontSize = 14)
    {
        if (button is null) return;
        var normal = selected
            ? GradientPlateBox(WellTop, WellBottom.Lightened(0.04f), BorderDark,
                innerTopLine: new Color(0f, 0f, 0f, 0.70f), innerBottomLine: new Color(Paper, 0.90f),
                brassEdge: Brass, borderThickness: 1, padLeft: 14, padTop: 5, padRight: 14, padBottom: 5)
            : GradientPlateBox(WellTop, WellBottom, BorderDark,
                innerTopLine: new Color(0f, 0f, 0f, 0.60f), innerBottomLine: new Color(Paper, 0.08f),
                brassEdge: RuleLight, borderThickness: 1, padLeft: 14, padTop: 5, padRight: 14, padBottom: 5);
        var hover = GradientPlateBox(
            WellBottom.Lightened(0.08f), PlateBottom, BorderDark,
            innerTopLine: new Color(Paper, 0.06f), innerBottomLine: new Color(Paper, 0.12f),
            brassEdge: Brass, borderThickness: 1, padLeft: 14, padTop: 5, padRight: 14, padBottom: 5);
        var pressed = GradientPlateBox(
            WellTop, WellTop.Lightened(0.04f), BorderDark,
            innerTopLine: new Color(0f, 0f, 0f, 0.75f), innerBottomLine: new Color(Paper, 0.85f),
            brassEdge: BrassHi, borderThickness: 1, padLeft: 14, padTop: 6, padRight: 14, padBottom: 4);
        var disabled = GradientPlateBox(
            WellTop, WellTop, BorderDark,
            brassEdge: BrassShadow, borderThickness: 1, padLeft: 14, padTop: 5, padRight: 14, padBottom: 5);

        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", disabled);
        button.AddThemeStyleboxOverride("focus", FocusBox());
        button.AddThemeFontOverride("font", SansBody());
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", selected ? Paper : PaperDim);
        button.AddThemeColorOverride("font_hover_color", Paper);
        button.AddThemeColorOverride("font_pressed_color", Highlight);
        button.AddThemeColorOverride("font_focus_color", Paper);
        button.AddThemeColorOverride("font_disabled_color", PaperGhost);
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    internal static void StylePlateButton(Button button, bool active = false, int fontSize = 13)
    {
        if (button is null) return;
        var normal = active
            ? GradientPlateBox(WellTop.Lightened(0.05f), WellBottom, BorderDark,
                innerTopLine: new Color(0f, 0f, 0f, 0.65f), innerBottomLine: new Color(Paper, 0.90f),
                brassEdge: BrassHi, borderThickness: 1, padLeft: 12, padTop: 4, padRight: 12, padBottom: 4)
            : GradientPlateBox(PlateTop, PlateBottom, BorderDark,
                innerTopLine: new Color(Paper, 0.12f), innerBottomLine: new Color(0f, 0f, 0f, 0.45f),
                brassEdge: RuleLight, borderThickness: 1, padLeft: 12, padTop: 4, padRight: 12, padBottom: 4);
        var hover = GradientPlateBox(
            PlateHover, PlateBottom.Lightened(0.05f), BorderDark,
            innerTopLine: new Color(Paper, 0.16f), innerBottomLine: new Color(0f, 0f, 0f, 0.40f),
            brassEdge: Brass, borderThickness: 1, padLeft: 12, padTop: 4, padRight: 12, padBottom: 4);
        var pressed = GradientPlateBox(
            WellTop, WellBottom, BorderDark,
            innerTopLine: new Color(0f, 0f, 0f, 0.70f), innerBottomLine: new Color(Paper, 0.80f),
            brassEdge: BrassHi, borderThickness: 1, padLeft: 12, padTop: 5, padRight: 12, padBottom: 3);

        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", hover);
        button.AddThemeStyleboxOverride("pressed", pressed);
        button.AddThemeStyleboxOverride("hover_pressed", pressed);
        button.AddThemeStyleboxOverride("disabled", normal);
        button.AddThemeStyleboxOverride("focus", FocusBox());
        button.AddThemeFontOverride("font", SansBody());
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", active ? Paper : PaperDim);
        button.AddThemeColorOverride("font_hover_color", Highlight);
        button.AddThemeColorOverride("font_pressed_color", Paper);
        button.AddThemeColorOverride("font_focus_color", Paper);
        button.AddThemeColorOverride("font_disabled_color", PaperGhost);
        button.MouseDefaultCursorShape = Control.CursorShape.PointingHand;
    }

    internal static void StyleTabButton(Button button, bool active, int fontSize = 13)
    {
        if (button is null) return;
        var normal = active
            ? GradientPlateBox(PlateHover, BodyTop, BorderDark,
                innerTopLine: new Color(BrassHi, 0.90f), innerBottomLine: null,
                brassEdge: Brass, borderThickness: 1, padLeft: 12, padTop: 5, padRight: 12, padBottom: 5)
            : GradientPlateBox(PlateBottom, WellBottom, BorderDark,
                innerTopLine: new Color(Paper, 0.06f), innerBottomLine: null,
                brassEdge: RuleLight, borderThickness: 1, padLeft: 12, padTop: 5, padRight: 12, padBottom: 5);
        var hover = GradientPlateBox(
            PlateTop, BodyTop, BorderDark,
            innerTopLine: new Color(Paper, 0.14f), innerBottomLine: null,
            brassEdge: Brass, borderThickness: 1, padLeft: 12, padTop: 5, padRight: 12, padBottom: 5);

        button.AddThemeStyleboxOverride("normal", normal);
        button.AddThemeStyleboxOverride("hover", active ? normal : hover);
        button.AddThemeStyleboxOverride("pressed", normal);
        button.AddThemeStyleboxOverride("hover_pressed", normal);
        button.AddThemeStyleboxOverride("focus", FocusBox());
        button.AddThemeFontOverride("font", active ? SerifDisplay() : SansBody());
        button.AddThemeFontSizeOverride("font_size", fontSize);
        button.AddThemeColorOverride("font_color", active ? Paper : PaperFaint);
        button.AddThemeColorOverride("font_hover_color", Paper);
        button.AddThemeColorOverride("font_pressed_color", Paper);
    }

    internal static void StyleLineEdit(LineEdit field)
    {
        if (field is null) return;
        var normal = new StyleBoxFlat
        {
            BgColor = WellTop,
            BorderColor = RuleLight,
            ContentMarginLeft = 12,
            ContentMarginRight = 12,
            ContentMarginTop = 5,
            ContentMarginBottom = 5
        };
        normal.SetBorderWidthAll(1);
        normal.SetCornerRadiusAll(3);
        var focus = (StyleBoxFlat)normal.Duplicate();
        focus.BorderColor = BrassHi;
        focus.BorderWidthBottom = 2;

        field.AddThemeStyleboxOverride("normal", normal);
        field.AddThemeStyleboxOverride("focus", focus);
        field.AddThemeStyleboxOverride("read_only", normal);
        field.AddThemeFontOverride("font", SansBody());
        field.AddThemeColorOverride("font_color", Paper);
        field.AddThemeColorOverride("font_placeholder_color", PaperFaint);
        field.AddThemeColorOverride("caret_color", BrassHi);
        field.AddThemeColorOverride("selection_color", new Color(Brass, 0.45f));
    }

    internal static void StyleOptionButton(OptionButton option)
    {
        if (option is null) return;
        StyleWellButton(option, selected: false, fontSize: 13);
        if (option.GetPopup() is { } popup) StylePopupMenu(popup);
    }

    internal static void StylePopupMenu(PopupMenu popup)
    {
        if (popup is null) return;
        var panel = new StyleBoxFlat
        {
            BgColor = PlateBottom,
            BorderColor = Brass,
            BorderWidthLeft = 1,
            BorderWidthTop = 2,
            BorderWidthRight = 1,
            BorderWidthBottom = 1,
            ShadowColor = new Color(0f, 0f, 0f, 0.60f),
            ShadowSize = 8,
            ShadowOffset = new Vector2(0f, 3f),
            ContentMarginLeft = 8,
            ContentMarginRight = 8,
            ContentMarginTop = 6,
            ContentMarginBottom = 6
        };
        panel.SetCornerRadiusAll(3);
        var hover = new StyleBoxFlat
        {
            BgColor = PlateHover,
            BorderColor = BrassHi,
            ContentMarginLeft = 6,
            ContentMarginRight = 6,
            ContentMarginTop = 4,
            ContentMarginBottom = 4
        };
        hover.SetBorderWidthAll(1);
        hover.SetCornerRadiusAll(2);
        popup.AddThemeStyleboxOverride("panel", panel);
        popup.AddThemeStyleboxOverride("hover", hover);
        popup.AddThemeFontOverride("font", SansBody());
        popup.AddThemeColorOverride("font_color", Paper);
        popup.AddThemeColorOverride("font_hover_color", Highlight);
        popup.AddThemeColorOverride("font_disabled_color", PaperGhost);
    }

    internal static void StyleSlider(HSlider slider)
    {
        if (slider is null) return;
        var track = new StyleBoxFlat
        {
            BgColor = WellTop,
            BorderColor = RuleLight,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        track.SetBorderWidthAll(1);
        track.SetCornerRadiusAll(3);

        var fill = new StyleBoxFlat
        {
            BgColor = Brass,
            BorderColor = BrassHi,
            ContentMarginTop = 3,
            ContentMarginBottom = 3
        };
        fill.SetBorderWidthAll(1);
        fill.SetCornerRadiusAll(3);

        var fillHot = (StyleBoxFlat)fill.Duplicate();
        fillHot.BgColor = BrassHi;

        slider.AddThemeStyleboxOverride("slider", track);
        slider.AddThemeStyleboxOverride("grabber_area", fill);
        slider.AddThemeStyleboxOverride("grabber_area_highlight", fillHot);
    }

    internal static void StyleExistingLabel(Label label, int size, Color color, bool serif = false, bool bold = false, bool tabular = false)
    {
        if (label is null) return;
        label.AddThemeFontOverride("font", serif ? SerifDisplay(bold) : (tabular ? SansTabular() : SansBody()));
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
    }

    internal static void StyleScreenHeader(PanelContainer header)
    {
        if (header is null) return;
        if (header.FindChild("ScreenHeaderTitle", true, false) is Label title)
            StyleExistingLabel(title, 26, Paper, serif: true, bold: false);
        if (header.FindChild("ScreenHeaderSubtitle", true, false) is Label subtitle)
            StyleExistingLabel(subtitle, 13, PaperDim, serif: false);
        if (header.GetParent()?.FindChild("ScreenHeaderRule", false, false) is HSeparator rule)
        {
            rule.AddThemeStyleboxOverride("separator", new StyleBoxLine { Color = RuleLight, Thickness = 1 });
        }
    }

    /// <summary>
    /// Creates a recessed natural-aspect flag frame (no shield, no cloth gloss, 1px dark edge,
    /// Nepal cutout preserved via ShellLayout.FitFlag).
    /// </summary>
    internal static PanelContainer CreateFlagPocket(string tag, Texture2D texture, float maxWidth, float maxHeight, string tooltip = "")
    {
        var pocket = new PanelContainer
        {
            Name = "NaturalFlagPocket",
            CustomMinimumSize = new Vector2(maxWidth + 4f, maxHeight + 4f),
            SizeFlagsHorizontal = Control.SizeFlags.ShrinkBegin,
            SizeFlagsVertical = Control.SizeFlags.ShrinkCenter,
            MouseFilter = Control.MouseFilterEnum.Pass,
            TooltipText = Ui.Tip(tooltip)
        };
        var pocketStyle = new StyleBoxFlat
        {
            BgColor = WellTop,
            BorderColor = BorderDark,
            ContentMarginLeft = 2,
            ContentMarginRight = 2,
            ContentMarginTop = 2,
            ContentMarginBottom = 2
        };
        pocketStyle.SetBorderWidthAll(1);
        pocketStyle.SetCornerRadiusAll(2);
        pocket.AddThemeStyleboxOverride("panel", pocketStyle);

        var center = new CenterContainer
        {
            Name = "FlagCenter",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        pocket.AddChild(center);

        var edge = new PanelContainer
        {
            Name = "FlagEdge",
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        center.AddChild(edge);

        var rect = new TextureRect
        {
            Name = "FlagTexture",
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            TextureFilter = CanvasItem.TextureFilterEnum.LinearWithMipmaps,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
        edge.AddChild(rect);

        UpdateFlagPocket(pocket, tag, texture, maxWidth, maxHeight);
        return pocket;
    }

    internal static void UpdateFlagPocket(Control pocket, string tag, Texture2D texture, float maxWidth, float maxHeight)
    {
        if (pocket is null) return;
        pocket.CustomMinimumSize = new Vector2(maxWidth + 4f, maxHeight + 4f);
        var edge = pocket.FindChild("FlagEdge", true, false) as PanelContainer;
        var rect = pocket.FindChild("FlagTexture", true, false) as TextureRect;
        if (edge is null || rect is null) return;

        var texW = texture is not null && texture.GetWidth() > 0 ? texture.GetWidth() : 3f;
        var texH = texture is not null && texture.GetHeight() > 0 ? texture.GetHeight() : 2f;
        var fit = ShellLayout.FitFlag(texW, texH, maxWidth, maxHeight, tag ?? "");
        rect.Texture = texture;
        rect.Material = null; // Flat archival print without synthetic cloth gloss.
        rect.CustomMinimumSize = new Vector2(fit.Width, fit.Height);

        if (fit.CutoutSilhouette)
        {
            var empty = new StyleBoxEmpty();
            empty.SetContentMarginAll(0);
            edge.AddThemeStyleboxOverride("panel", empty);
            edge.CustomMinimumSize = new Vector2(fit.Width, fit.Height);
        }
        else
        {
            var frame = new StyleBoxFlat
            {
                DrawCenter = false,
                BorderColor = new Color(0.047f, 0.035f, 0.024f, 0.88f),
                ContentMarginLeft = 1,
                ContentMarginRight = 1,
                ContentMarginTop = 1,
                ContentMarginBottom = 1
            };
            frame.SetBorderWidthAll(1);
            edge.AddThemeStyleboxOverride("panel", frame);
            edge.CustomMinimumSize = new Vector2(fit.Width + 2f, fit.Height + 2f);
        }
    }
}

/// <summary>
/// Non-intrusive architectural frame overlay drawn across full parent bounds:
/// subtle top light hairline and chamfered antique-brass corner trim.
/// </summary>
public partial class MenuChromeBrassFrame : Node2D
{
    private Control _host;

    public override void _EnterTree()
    {
        _host = GetParent() as Control;
        InterfaceSkin.Changed += OnSkinChanged;
        if (_host is not null) _host.Resized += QueueRedraw;
    }

    public override void _ExitTree()
    {
        InterfaceSkin.Changed -= OnSkinChanged;
        if (GodotObject.IsInstanceValid(_host)) _host.Resized -= QueueRedraw;
    }

    private void OnSkinChanged() => QueueRedraw();

    public override void _Draw()
    {
        if (_host is null || !_host.IsVisibleInTree()) return;
        var size = _host.Size;
        if (size.X < 24f || size.Y < 24f) return;

        var w = size.X;
        var h = size.Y;
        var len = MathF.Min(18f, MathF.Min(w, h) * 0.18f);
        var thick = 2f;
        var hi = MenuChromeTheme.BrassHi;
        var mid = MenuChromeTheme.Brass;
        var shadow = MenuChromeTheme.BrassShadow;
        var dark = MenuChromeTheme.BorderDark;

        // Subtle warm light catch along top inside edge.
        DrawLine(new Vector2(len + 2f, 1.5f), new Vector2(w - len - 2f, 1.5f), new Color(MenuChromeTheme.Paper, 0.11f), 1f);

        // Chamfered 2px antique-brass corner trim (top-left, top-right, bottom-left, bottom-right).
        // Top-left (brightest toward upper-left key light)
        DrawRect(new Rect2(1f, 1f, len, thick), hi);
        DrawRect(new Rect2(1f, 1f, thick, len), hi);
        DrawRect(new Rect2(0f, 0f, 3f, 3f), dark);

        // Top-right
        DrawRect(new Rect2(w - 1f - len, 1f, len, thick), hi);
        DrawRect(new Rect2(w - 1f - thick, 1f, thick, len), mid);
        DrawRect(new Rect2(w - 3f, 0f, 3f, 3f), dark);

        // Bottom-left
        DrawRect(new Rect2(1f, h - 1f - thick, len, thick), mid);
        DrawRect(new Rect2(1f, h - 1f - len, thick, len), mid);
        DrawRect(new Rect2(0f, h - 3f, 3f, 3f), dark);

        // Bottom-right (shadowed brass)
        DrawRect(new Rect2(w - 1f - len, h - 1f - thick, len, thick), shadow.Lerp(mid, 0.45f));
        DrawRect(new Rect2(w - 1f - thick, h - 1f - len, thick, len), shadow.Lerp(mid, 0.45f));
        DrawRect(new Rect2(w - 3f, h - 3f, 3f, 3f), dark);
    }
}

/// <summary>
/// Engraved 2px horizontal divider (1px dark cut over 1px warm `#4d4336` bevel lip).
/// </summary>
public partial class MenuChromeEngravedRule : Control
{
    public override void _Ready() => InterfaceSkin.Changed += QueueRedraw;
    public override void _ExitTree() => InterfaceSkin.Changed -= QueueRedraw;

    public override void _Draw()
    {
        if (Size.X < 4f) return;
        var y = MathF.Floor(Size.Y * 0.5f) - 1f;
        DrawLine(new Vector2(0f, y + 0.5f), new Vector2(Size.X, y + 0.5f), MenuChromeTheme.RuleDark, 1f);
        DrawLine(new Vector2(0f, y + 1.5f), new Vector2(Size.X, y + 1.5f), MenuChromeTheme.RuleLight, 1f);
    }
}
