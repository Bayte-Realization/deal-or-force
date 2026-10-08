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
        var ground = Ui.Colour("surface.overlay");
        ground.A = 0.55f;
        var mark = new Color(0.07f, 0.09f, 0.12f);
        void Set(string name, Texture2D texture) => theme.SetIcon(name, "CheckBox", texture);
        Set("unchecked", Box(edge, ground, false, false, mark, 1f));
        Set("checked", Box(accent, accent, true, false, mark, 1f));
        Set("unchecked_disabled", Box(edge, ground, false, false, mark, 0.35f));
        Set("checked_disabled", Box(accent, accent, true, false, mark, 0.4f));
        Set("radio_unchecked", Box(edge, ground, false, true, mark, 1f));
        Set("radio_checked", Box(accent, ground, true, true, accent, 1f));
        Set("radio_unchecked_disabled", Box(edge, ground, false, true, mark, 0.35f));
        Set("radio_checked_disabled", Box(accent, ground, true, true, accent, 0.4f));
        theme.SetConstant("h_separation", "CheckBox", 8);
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
