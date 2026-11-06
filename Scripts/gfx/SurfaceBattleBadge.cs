using Godot;

namespace DealOrForce;

/// <summary>An owned, cached canvas on a depth-tested surface quad. The canvas only
/// redraws when the readout changes, not once per battle per frame. Its viewport and
/// texture die with the marker; no global cache accumulates ended battles.</summary>
public partial class SurfaceBattleBadge : Node3D
{
    public Sprite3D Plate { get; private set; }
    private SubViewport _canvas;
    private Node2D _ink;
    private string _text, _tone;
    private float _share;
    private Texture2D _symbol;
    private ViewportTexture _ownedTexture;

    public override void _Ready()
    {
        _canvas = new SubViewport { Name = "BattleBadgeCanvas", Size = new Vector2I(96, 96),
            Disable3D = true, TransparentBg = true, RenderTargetUpdateMode = SubViewport.UpdateMode.Once };
        AddChild(_canvas); _ink = new Node2D(); _canvas.AddChild(_ink); _ink.Draw += Paint;
        _ownedTexture = _canvas.GetTexture();
        Plate = new Sprite3D { Name = "BattleSurfacePlate", Texture = _ownedTexture, PixelSize = 1f / 96,
            Billboard = BaseMaterial3D.BillboardModeEnum.Disabled, NoDepthTest = false,
            AlphaCut = SpriteBase3D.AlphaCutMode.Discard, RenderPriority = 20,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off };
        AddChild(Plate);
    }

    public override void _ExitTree()
    {
        // The generated C# wrapper can outlive a queued node until a later GC.
        // Release OUR viewport texture now; never dispose the borrowed icon/font.
        if (IsInstanceValid(_ink)) _ink.Draw -= Paint;
        if (IsInstanceValid(Plate)) Plate.Texture = null;
        _ownedTexture?.Dispose(); _ownedTexture = null;
        _symbol = null;
    }

    public void SetReadout(string text, float share, string tone, Texture2D symbol)
    {
        if (_text == text && _share == share && _tone == tone && _symbol == symbol) return;
        _text = text; _share = share; _tone = tone; _symbol = symbol;
        if (_canvas is null) return;
        _canvas.RenderTargetUpdateMode = SubViewport.UpdateMode.Once; _ink.QueueRedraw();
    }

    private void Paint()
    {
        var center = new Vector2(48, 48); const float radius = 42;
        var background = Ui.Colour("combat.badge_background");
        var border = Ui.Colour("button.primary_border"); var ink = Ui.Colour(_tone ?? "text.muted");
        _ink.DrawCircle(center, radius + 3, background.Darkened(.3f));
        _ink.DrawCircle(center, radius, background);
        _ink.DrawArc(center, radius + 1, 0, Mathf.Tau, 64, border, 2, true);
        _ink.DrawArc(center, radius - 5, -Mathf.Pi * .5f, Mathf.Pi * 1.5f, 64, Ui.Colour("state.negative", .45f), 5, true);
        if (_text is not (null or "?")) _ink.DrawArc(center, radius - 5, -Mathf.Pi * .5f,
            -Mathf.Pi * .5f + Mathf.Tau * Mathf.Clamp(_share, 0, 1), 64, ink, 5, true);
        if (_symbol is not null) _ink.DrawTextureRect(_symbol, new Rect2(31, 15, 34, 34), false, ink.Lightened(.18f));
        else
        { _ink.DrawLine(new Vector2(37, 22), new Vector2(59, 44), ink, 3, true); _ink.DrawLine(new Vector2(59, 22), new Vector2(37, 44), ink, 3, true); }
        var font = PresentationFonts.For("ui.default"); var text = _text == "?" ? "?" : (_text ?? "?") + "%";
        const int size = 23; var width = font.GetStringSize(text, HorizontalAlignment.Left, -1, size).X;
        var box = new Rect2(48 - width / 2 - 6, 49, width + 12, 29);
        _ink.DrawRect(box, background.Darkened(.35f)); _ink.DrawRect(box, border.Darkened(.2f), false, 1);
        _ink.DrawString(font, new Vector2(48 - width / 2, 72), text, HorizontalAlignment.Left, -1, size, Ui.Colour("text.bright"));
    }
}
