using Godot;

namespace DealOrForce;

/// <summary>
/// The few controls the stock theme draws for a light window on a dark map.
///
/// The game builds every screen by hand and leaves Godot's own theme in place, which is
/// right for buttons and panels it styles itself - but a check box it never styled. The
/// stock one is a dark square on a dark panel: in the deal table the boxes beside "Non-
/// aggression pact" and every region were all but invisible, and a ticked one could be
/// told from an empty one only by looking hard. These are drawn once, in the skin's own
/// colours: an outlined square, filled with the accent and ticked when chosen, faded when
/// the choice is not available. Round ones for radio groups.
/// </summary>
internal static class ControlSkin
{
    private const int Size = 20;
    private static bool _applied;

    /// <summary>
    /// Written into the engine's default theme rather than set on a root control: half the
    /// interface lives on canvas layers, and a theme does not cross into one.
    /// </summary>
    public static void Apply()
    {
        if (_applied) return;
        _applied = true;
        Build(ThemeDB.GetDefaultTheme());
    }

    private static void Build(Theme theme)
    {
        if (theme is null) return;
        var edge = Ui.Colour("text.muted");
        var accent = Ui.Colour("text.accent");
        var selected = Ui.Colour("text.accent_dim");
        var ground = Ui.Colour("surface.inset");
        var mark = Ui.Colour("surface.background");
        void Set(string name, Texture2D texture) => theme.SetIcon(name, "CheckBox", texture);
        Set("unchecked", Box(edge, ground, false, false, mark, 1f));
        Set("checked", Box(selected, selected, true, false, mark, 1f));
        Set("unchecked_hover", Box(accent, Ui.Colour("button.fill_hover"), false, false, mark, 1f));
        Set("checked_hover", Box(accent, selected.Lightened(0.08f), true, false, mark, 1f));
        Set("unchecked_hover_pressed", Box(accent, Ui.Colour("button.fill_pressed"), false, false, mark, 1f));
        Set("checked_hover_pressed", Box(accent, selected.Darkened(0.12f), true, false, mark, 1f));
        Set("unchecked_disabled", Box(edge, Ui.Colour("button.fill_disabled"), false, false, mark, 0.45f));
        Set("checked_disabled", Box(selected, selected, true, false, mark, 0.45f));
        Set("radio_unchecked", Box(edge, ground, false, true, mark, 1f));
        Set("radio_checked", Box(selected, ground, true, true, selected, 1f));
        Set("radio_unchecked_hover", Box(accent, Ui.Colour("button.fill_hover"), false, true, mark, 1f));
        Set("radio_checked_hover", Box(accent, Ui.Colour("button.fill_hover"), true, true, accent, 1f));
        Set("radio_unchecked_hover_pressed", Box(accent, Ui.Colour("button.fill_pressed"), false, true, mark, 1f));
        Set("radio_checked_hover_pressed", Box(accent, Ui.Colour("button.fill_pressed"), true, true, selected, 1f));
        Set("radio_unchecked_disabled", Box(edge, Ui.Colour("button.fill_disabled"), false, true, mark, 0.45f));
        Set("radio_checked_disabled", Box(selected, Ui.Colour("button.fill_disabled"), true, true, selected, 0.45f));
        theme.SetConstant("h_separation", "CheckBox", 8);
        theme.SetColor("font_color", "CheckBox", Ui.Colour("text.body"));
        theme.SetColor("font_hover_color", "CheckBox", Ui.Colour("text.bright"));
        theme.SetColor("font_hover_pressed_color", "CheckBox", Ui.Colour("text.bright"));
        theme.SetColor("font_pressed_color", "CheckBox", Ui.Colour("text.accent_dim"));
        theme.SetColor("font_focus_color", "CheckBox", Ui.Colour("text.bright"));
        theme.SetColor("font_disabled_color", "CheckBox", Ui.Colour("button.text_disabled"));
        var checkFocus = Surface(new Color(0, 0, 0, 0), accent, 0, 0);
        checkFocus.DrawCenter = false;
        theme.SetStylebox("focus", "CheckBox", checkFocus);
        Separators(theme);
        Gauges(theme, accent);
        Inputs(theme, accent);
        Tooltips(theme);
    }

    private static StyleBoxFlat Surface(Color fill, Color border, int horizontal, int vertical)
    {
        var box = new StyleBoxFlat
        {
            BgColor = fill,
            BorderColor = border,
            CornerRadiusTopLeft = 4,
            CornerRadiusTopRight = 4,
            CornerRadiusBottomLeft = 4,
            CornerRadiusBottomRight = 4,
            ContentMarginLeft = horizontal,
            ContentMarginRight = horizontal,
            ContentMarginTop = vertical,
            ContentMarginBottom = vertical
        };
        box.SetBorderWidthAll(1);
        return box;
    }

    private static void Inputs(Theme theme, Color accent)
    {
        var normal = Surface(Ui.Colour("surface.inset"), Ui.Colour("surface.line"), 12, 4);
        var focus = (StyleBoxFlat)normal.Duplicate();
        focus.DrawCenter = false;
        focus.BorderColor = accent;
        var readOnly = Surface(Ui.Colour("button.fill_disabled"), Ui.Colour("button.border_disabled"), 12, 4);
        theme.SetStylebox("normal", "LineEdit", normal);
        theme.SetStylebox("focus", "LineEdit", focus);
        theme.SetStylebox("read_only", "LineEdit", readOnly);
        theme.SetColor("font_color", "LineEdit", Ui.Colour("text.bright"));
        theme.SetColor("font_uneditable_color", "LineEdit", Ui.Colour("text.muted"));
        theme.SetColor("font_placeholder_color", "LineEdit", Ui.Colour("text.hint"));
        theme.SetColor("caret_color", "LineEdit", accent);
        theme.SetColor("selection_color", "LineEdit", Ui.Colour("button.fill_hover"));

        var tabSelected = Surface(Ui.Colour("surface.raised"), accent, 12, 8);
        tabSelected.CornerRadiusBottomLeft = 0;
        tabSelected.CornerRadiusBottomRight = 0;
        var tabUnselected = Surface(Ui.Colour("surface.inset"), Ui.Colour("surface.line_faint"), 12, 8);
        tabUnselected.CornerRadiusBottomLeft = 0;
        tabUnselected.CornerRadiusBottomRight = 0;
        var tabHover = (StyleBoxFlat)tabUnselected.Duplicate();
        tabHover.BgColor = Ui.Colour("button.fill_hover");
        tabHover.BorderColor = Ui.Colour("button.border_hover");
        var tabDisabled = (StyleBoxFlat)tabUnselected.Duplicate();
        tabDisabled.BgColor = Ui.Colour("button.fill_disabled");
        tabDisabled.BorderColor = Ui.Colour("button.border_disabled");
        var tabFocus = (StyleBoxFlat)tabSelected.Duplicate();
        tabFocus.DrawCenter = false;
        var tabPanel = Surface(Ui.Colour("surface.base"), Ui.Colour("surface.line"), 12, 12);
        theme.SetStylebox("tab_selected", "TabContainer", tabSelected);
        theme.SetStylebox("tab_unselected", "TabContainer", tabUnselected);
        theme.SetStylebox("tab_hovered", "TabContainer", tabHover);
        theme.SetStylebox("tab_disabled", "TabContainer", tabDisabled);
        theme.SetStylebox("tab_focus", "TabContainer", tabFocus);
        theme.SetStylebox("panel", "TabContainer", tabPanel);
        theme.SetColor("font_selected_color", "TabContainer", Ui.Colour("text.accent"));
        theme.SetColor("font_unselected_color", "TabContainer", Ui.Colour("text.muted"));
        theme.SetColor("font_hovered_color", "TabContainer", Ui.Colour("text.bright"));
        theme.SetColor("font_disabled_color", "TabContainer", Ui.Colour("button.text_disabled"));

        // Sleek dark-slate scrollbars and sliders so scrollable lists and budget sliders match the shell.
        var scrollTrackV = Surface(Ui.Colour("surface.inset", 0.72f), Ui.Colour("surface.line_faint", 0.35f), 2, 2);
        scrollTrackV.SetCornerRadiusAll(3);
        scrollTrackV.ContentMarginLeft = 3;
        scrollTrackV.ContentMarginRight = 3;
        var scrollTrackH = (StyleBoxFlat)scrollTrackV.Duplicate();
        scrollTrackH.ContentMarginTop = 3;
        scrollTrackH.ContentMarginBottom = 3;
        var grabber = Surface(Ui.Colour("surface.line").Lightened(0.08f), Ui.Colour("button.border"), 2, 2);
        grabber.SetCornerRadiusAll(3);
        var grabberHover = Surface(Ui.Colour("text.accent_dim"), Ui.Colour("text.accent"), 2, 2);
        grabberHover.SetCornerRadiusAll(3);
        var grabberPressed = Surface(Ui.Colour("text.accent"), Ui.Colour("text.bright"), 2, 2);
        grabberPressed.SetCornerRadiusAll(3);
        foreach (var bar in new[] { "VScrollBar", "HScrollBar" })
        {
            theme.SetStylebox("scroll", bar, bar == "VScrollBar" ? scrollTrackV : scrollTrackH);
            theme.SetStylebox("scroll_focus", bar, bar == "VScrollBar" ? scrollTrackV : scrollTrackH);
            theme.SetStylebox("grabber", bar, grabber);
            theme.SetStylebox("grabber_highlight", bar, grabberHover);
            theme.SetStylebox("grabber_pressed", bar, grabberPressed);
        }

        var sliderTrack = Surface(Ui.Colour("surface.inset"), Ui.Colour("surface.line"), 4, 4);
        sliderTrack.SetCornerRadiusAll(3);
        var sliderFill = Surface(Ui.Colour("text.accent_dim", 0.78f), Ui.Colour("button.primary_border"), 4, 4);
        sliderFill.SetCornerRadiusAll(3);
        var sliderHighlight = Surface(Ui.Colour("text.accent"), Ui.Colour("text.bright"), 4, 4);
        sliderHighlight.SetCornerRadiusAll(3);
        theme.SetStylebox("slider", "HSlider", sliderTrack);
        theme.SetStylebox("grabber_area", "HSlider", sliderFill);
        theme.SetStylebox("grabber_area_highlight", "HSlider", sliderHighlight);

        var popupPanel = Surface(Ui.Colour("surface.overlay", 0.98f), Ui.Colour("shell.border"), 8, 8);
        popupPanel.BorderWidthTop = 2;
        popupPanel.BorderColor = Ui.Colour("button.primary_border");
        popupPanel.ShadowColor = new Color(0f, 0f, 0f, 0.55f);
        popupPanel.ShadowSize = 10;
        popupPanel.ShadowOffset = new Vector2(0, 4);
        var popupHover = Surface(Ui.Colour("button.fill_hover"), Ui.Colour("button.border_hover"), 6, 4);
        theme.SetStylebox("panel", "PopupMenu", popupPanel);
        theme.SetStylebox("hover", "PopupMenu", popupHover);
        theme.SetColor("font_color", "PopupMenu", Ui.Colour("text.body"));
        theme.SetColor("font_hover_color", "PopupMenu", Ui.Colour("text.bright"));
        theme.SetColor("font_Disabled_color", "PopupMenu", Ui.Colour("button.text_disabled"));

        var optNormal = Surface(Ui.Colour("button.fill"), Ui.Colour("button.border"), 10, 5);
        var optHover = Surface(Ui.Colour("button.fill_hover"), Ui.Colour("button.border_hover"), 10, 5);
        var optPressed = Surface(Ui.Colour("button.fill_pressed"), Ui.Colour("button.border_pressed"), 10, 5);
        theme.SetStylebox("normal", "OptionButton", optNormal);
        theme.SetStylebox("hover", "OptionButton", optHover);
        theme.SetStylebox("pressed", "OptionButton", optPressed);
        theme.SetColor("font_color", "OptionButton", Ui.Colour("button.text"));
        theme.SetColor("font_hover_color", "OptionButton", Ui.Colour("button.text_hover"));
        theme.SetColor("font_pressed_color", "OptionButton", Ui.Colour("button.text_pressed"));
    }

    /// <summary>A quiet reading surface with one structural edge and just enough depth to lift it.</summary>
    private static void Tooltips(Theme theme)
    {
        var frame = Surface(Ui.Colour("surface.overlay", 0.98f), Ui.Colour("shell.border"), 12, 8);
        frame.BorderWidthTop = 2;
        frame.BorderColor = Ui.Colour("button.primary_border", 0.85f);
        frame.ShadowColor = new Color(0f, 0f, 0f, 0.48f);
        frame.ShadowSize = 10;
        frame.ShadowOffset = new Vector2(0, 3);
        theme.SetStylebox("panel", "TooltipPanel", frame);
        theme.SetColor("font_color", "TooltipLabel", Ui.Colour("text.body"));
        theme.SetColor("font_shadow_color", "TooltipLabel", new Color(0f, 0f, 0f, 0f));
    }

    /// <summary>One-pixel neutral rules; their spacing comes from the same 8 px rhythm.</summary>
    private static void Separators(Theme theme)
    {
        var horizontal = new StyleBoxLine { Color = Ui.Colour("surface.line"), Thickness = 1 };
        var vertical = new StyleBoxLine { Color = Ui.Colour("surface.line"), Thickness = 1, Vertical = true };
        theme.SetStylebox("separator", "HSeparator", horizontal);
        theme.SetStylebox("separator", "VSeparator", vertical);
        theme.SetConstant("separation", "HSeparator", 8);
        theme.SetConstant("separation", "VSeparator", 8);
    }

    /// <summary>A recessed track and a solid, subdued brass fill without a glossy gradient.</summary>
    private static void Gauges(Theme theme, Color accent)
    {
        var track = Surface(Ui.Colour("surface.inset"), Ui.Colour("surface.line_faint"), 4, 2);
        track.SetCornerRadiusAll(3);
        var fillColour = Ui.Colour("text.accent_dim");
        var fill = Surface(fillColour, fillColour.Darkened(0.25f), 4, 2);
        fill.SetCornerRadiusAll(3);
        theme.SetStylebox("background", "ProgressBar", track);
        theme.SetStylebox("fill", "ProgressBar", fill);
        theme.SetColor("font_color", "ProgressBar", Ui.Colour("text.bright"));
        theme.SetColor("font_outline_color", "ProgressBar", Ui.Colour("surface.background"));
        theme.SetConstant("outline_size", "ProgressBar", 1);
        theme.SetColor("font_shadow_color", "ProgressBar", new Color(accent, 0f));
    }

    /// <summary>One icon, drawn by distance so its edges are smooth at any scale.</summary>
    private static ImageTexture Box(Color edge, Color fill, bool on, bool round, Color mark, float alpha)
    {
        var image = Image.CreateEmpty(Size, Size, false, Image.Format.Rgba8);
        var half = (Size - 4) / 2f;
        var centre = new Vector2(Size / 2f, Size / 2f);
        for (var y = 0; y < Size; y++)
        for (var x = 0; x < Size; x++)
        {
            var p = new Vector2(x + 0.5f, y + 0.5f) - centre;
            // Signed distance to the outline: a circle, or a square with rounded corners.
            float d;
            if (round) d = p.Length() - half;
            else
            {
                const float corner = 3f;
                var q = new Vector2(Mathf.Abs(p.X), Mathf.Abs(p.Y)) - new Vector2(half - corner, half - corner);
                d = new Vector2(Mathf.Max(q.X, 0f), Mathf.Max(q.Y, 0f)).Length() + Mathf.Min(Mathf.Max(q.X, q.Y), 0f) - corner;
            }
            var inside = Mathf.Clamp(0.5f - d, 0f, 1f);
            var border = Mathf.Clamp(1f - Mathf.Abs(d + 0.9f) + 0.5f, 0f, 1f);
            var colour = fill;
            colour.A *= inside;
            colour = Blend(colour, edge, border * inside);
            if (on)
            {
                var ink = round ? Dot(p, half * 0.45f) : Tick(p);
                colour = Blend(colour, mark, ink * inside);
            }
            colour.A *= alpha;
            image.SetPixel(x, y, colour);
        }
        return ImageTexture.CreateFromImage(image);
    }

    private static float Dot(Vector2 p, float radius) => Mathf.Clamp(radius + 0.5f - p.Length(), 0f, 1f);

    private static float Tick(Vector2 p)
    {
        var a = new Vector2(-4.2f, 0.2f);
        var b = new Vector2(-1.2f, 3.4f);
        var c = new Vector2(4.4f, -3.6f);
        var d = Mathf.Min(Segment(p, a, b), Segment(p, b, c));
        return Mathf.Clamp(1.9f - d, 0f, 1f);
    }

    private static float Segment(Vector2 p, Vector2 a, Vector2 b)
    {
        var ab = b - a;
        var t = Mathf.Clamp((p - a).Dot(ab) / ab.LengthSquared(), 0f, 1f);
        return (p - (a + ab * t)).Length();
    }

    private static Color Blend(Color under, Color over, float amount)
    {
        if (amount <= 0f) return under;
        var a = amount + under.A * (1f - amount);
        if (a <= 0f) return new Color(0, 0, 0, 0);
        var r = (over.R * amount + under.R * under.A * (1f - amount)) / a;
        var g = (over.G * amount + under.G * under.A * (1f - amount)) / a;
        var bl = (over.B * amount + under.B * under.A * (1f - amount)) / a;
        return new Color(r, g, bl, a);
    }
}
