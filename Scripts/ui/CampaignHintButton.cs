using Godot;

namespace DealOrForce;

/// <summary>Explanatory hover card, not a one-line truncated caption.</summary>
public partial class CampaignHintButton : Button
{
    public Texture2D Glyph { get; private set; }
    public string Badge { get; private set; } = "";
    public bool GlyphOnLeft { get; set; }
    public Color Accent { get; private set; }
    public Rect2 GlyphRect => Glyph is null ? default : CampaignHintArt.CenteredRect(
        GlyphOnLeft ? new Vector2(26, Size.Y * .5f) : Size * .5f, Glyph.GetSize(), GlyphOnLeft ? 28 : 29);

    public void Present(Texture2D glyph, Color accent, string badge = "")
    {
        if (Glyph == glyph && Accent == accent && Badge == badge) return;
        Glyph = glyph; Accent = accent; Badge = badge; QueueRedraw();
    }

    public override void _Draw()
    {
        if (Glyph is not null) DrawTextureRect(Glyph, GlyphRect, false);
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
