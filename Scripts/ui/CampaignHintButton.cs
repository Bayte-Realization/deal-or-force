using Godot;

namespace DealOrForce;

/// <summary>Explanatory hover card, not a one-line truncated caption.</summary>
public partial class CampaignHintButton : Button
{
    public Texture2D Glyph { get; private set; }
    public string Badge { get; private set; } = "";
    public bool GlyphOnLeft { get; set; }
    /// <summary>The square a glyph on the left is fitted into, and how far that square starts from the left edge.
    /// The full list's rows keep 28 from 12; the always-visible list under the date plate is a
    /// row of 28 pixels and draws a smaller glyph closer to the edge.</summary>
    public float GlyphBox { get; set; } = 28;
    public float GlyphInset { get; set; } = 12;
    public Color Accent { get; private set; }
    /// <summary>The severity dot at the right end of a list row; transparent draws none.</summary>
    public Color Dot { get; private set; } = Colors.Transparent;
    public Rect2 GlyphRect => Glyph is null ? default : CampaignHintArt.CenteredRect(
        GlyphOnLeft ? new Vector2(GlyphInset + GlyphBox * .5f, Size.Y * .5f) : Size * .5f, Glyph.GetSize(), GlyphOnLeft ? GlyphBox : 29);
    /// <summary>Where the dot is drawn: a small circle, centred on the row's height, clear of the right edge.</summary>
    public Vector2 DotCentre => new(Size.X - 11f, Size.Y * .5f);

    public void Present(Texture2D glyph, Color accent, string badge = "")
    {
        if (Glyph == glyph && Accent == accent && Badge == badge) return;
        Glyph = glyph; Accent = accent; Badge = badge; QueueRedraw();
    }

    public void Mark(Color dot)
    {
        if (Dot == dot) return;
        Dot = dot; QueueRedraw();
    }

    public override void _Draw()
    {
        if (Glyph is not null) DrawTextureRect(Glyph, GlyphRect, false);
        if (Dot.A > 0f)
        {
            // A dark ring under the colour, so a green or amber dot reads on any plate.
            DrawCircle(DotCentre, 5.5f, Ui.Colour("shell.background"));
            DrawCircle(DotCentre, 4f, Dot);
        }
        if (Badge.Length == 0) return;
        var font = GetThemeFont("font");
        var textSize = font.GetStringSize(Badge, fontSize: 10);
        var size = new Vector2(Mathf.Max(13, textSize.X + 5), 13);
        var p = new Vector2(Size.X - size.X - 1, 1);
        DrawRect(new(p, size), Ui.Colour("shell.background"));
        DrawString(font, p + new Vector2((size.X - textSize.X) * .5f, 10), Badge, fontSize: 10, modulate: Accent);
    }

    public override GodotObject _MakeCustomTooltip(string forText)
    {
        var panel = new PanelContainer();
        var frame = new StyleBoxFlat
        {
            BgColor = Ui.Colour("shell.background"), BorderColor = Accent,
            BorderWidthLeft = 1, BorderWidthRight = 1, BorderWidthTop = 3, BorderWidthBottom = 1
        };
        frame.SetCornerRadiusAll(4); frame.SetContentMarginAll(14); panel.AddThemeStyleboxOverride("panel", frame);
        var stack = new VBoxContainer(); stack.AddThemeConstantOverride("separation", 10); panel.AddChild(stack);
        var lines = forText.Split('\n', 3);
        var head = new HBoxContainer(); head.AddThemeConstantOverride("separation", 12); stack.AddChild(head);
        if (Glyph is not null) head.AddChild(new TextureRect { Texture = Glyph, CustomMinimumSize = new(36,36),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize, StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore });
        var category = new Label { Text = lines[0], CustomMinimumSize = new(290,0), AutowrapMode = TextServer.AutowrapMode.WordSmart };
        category.AddThemeColorOverride("font_color", Accent); category.AddThemeFontSizeOverride("font_size", 14); head.AddChild(category);
        stack.AddChild(new HSeparator());
        var label = new Label
        {
            Text = lines.Length > 1 ? string.Join("\n", lines[1..]).Trim() : forText,
            CustomMinimumSize = new(340, 0), AutowrapMode = TextServer.AutowrapMode.WordSmart
        };
        label.AddThemeFontSizeOverride("font_size", 14);
        label.AddThemeColorOverride("font_color", Ui.Colour("text.bright"));
        stack.AddChild(label); return panel;
    }
}
