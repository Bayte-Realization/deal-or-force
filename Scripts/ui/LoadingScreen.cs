using System.Linq;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// A lightweight cover that can render before catalogs or the world exist.
///
/// Behind the dossier hangs one of the illustrations in gfx/loading, a different one each
/// time, drifting very slowly. The dossier sits on a dark card so the words read over any
/// picture, and with no pictures at all the screen is the plain chart it always was.
/// </summary>
public partial class LoadingScreen : Control
{
    private const string SceneFolder = "res://game/gfx/loading/";
    private const string Motto = "Life is an eternal struggle. History is an eternal war.";
    private static int _sceneTurn = -1;
    private TextureRect _scene;
    private TextureRect _shade;
    private Panel _card;
    private Control _frame;
    private VBoxContainer _body;
    private Label _heading;
    private Label _stage;
    private Label _detail;
    private Label _caption;
    private Label _footer;
    private ProgressBar _progress;
    private Button _dismiss;
    private TextureRect _logo;
    private Panel _logoPlate;
    private TextureRect _paper;
    public bool CanDismiss => IsInstanceValid(_dismiss) && _dismiss.Visible;

    /// <summary>
    /// The setting that takes the generated pictures away (`loading_without_ai_art`): the screen
    /// is then the plain chart it was at first - grid, frame, seal and dossier. Read straight from
    /// the player's file, because the first loading screen stands before the catalogues exist.
    /// The main menu has its own setting (`menu_without_ai_art`, MenuIllustrations).
    /// </summary>
    internal static bool Plain(string setting = "loading_without_ai_art")
    {
        try
        {
            // The campaign scene started on its own (from the editor) shows its first loading
            // screen before it has told the settings where the player's file is.
            if (!PlayerSettings.Located)
            {
                GamePaths.Prepare();
                PlayerSettings.UseDirectory(GamePaths.Settings);
            }
            return SettingsCatalog.Flag(setting);
        }
        catch (System.Exception) { return false; }
    }

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        var plain = Plain();
        _scene = new TextureRect
        {
            // Plain does not mean empty. Without the illustrations the screen was a logo on a
            // blank chart for the half-minute a world takes to build; it now shows the same
            // picture of the game's own globe the menu stands on, so the menu, the wait and the
            // map are one place. The illustrations, when they are switched on, still come first.
            Name = "LoadingScene", Texture = plain ? WorldPicture() : NextScene(), ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered, MouseFilter = MouseFilterEnum.Ignore,
            SelfModulate = plain ? new Color(0.66f, 0.66f, 0.67f) : new Color(0.84f, 0.84f, 0.84f)
        };
        var pictured = _scene.Texture is not null;
        AddChild(_scene);
        _scene.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // Darker towards the edges and the foot, where the footer stands.
        var ink = MenuChromeTheme.BackdropColor;
        _shade = new TextureRect
        {
            Name = "LoadingShade", MouseFilter = MouseFilterEnum.Ignore, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            Texture = new GradientTexture2D
            {
                Fill = GradientTexture2D.FillEnum.Radial, FillFrom = new Vector2(.5f, .42f), FillTo = new Vector2(1.08f, 1.12f),
                Width = 256, Height = 256,
                Gradient = new Gradient
                {
                    Offsets = new[] { 0f, .55f, 1f },
                    Colors = new[] { ink with { A = .24f }, ink with { A = .62f }, ink with { A = .94f } }
                }
            }
        };
        AddChild(_shade);
        _shade.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _shade.Visible = pictured;
        if (_scene.Texture is not null)
        {
            // A slow drift, not a slideshow. No fade-in: a loading stage can hold the frame for
            // seconds, and a picture fading in from nothing would be absent for all of them.
            var drift = CreateTween().SetLoops();
            drift.TweenProperty(_scene, "scale", new Vector2(1.045f, 1.045f), 24).SetTrans(Tween.TransitionType.Sine);
            drift.TweenProperty(_scene, "scale", Vector2.One, 24).SetTrans(Tween.TransitionType.Sine);
        }
        _paper = new TextureRect
        {
            Texture = Picture(SpriteCatalog.TextureOf("panel_dossier", "res://game/gfx/interface/panels/panel_dossier.png")),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            Modulate = MenuChromeTheme.PaperDim with { A = 0.05f }, MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_paper);
        _paper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _frame = new Control { Name = "LoadingFrame", MouseFilter = MouseFilterEnum.Ignore };
        _frame.Draw += DrawFrame;
        AddChild(_frame);
        _frame.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _card = new Panel { Name = "DossierCard", MouseFilter = MouseFilterEnum.Ignore };
        _card.AddThemeStyleboxOverride("panel", MenuChromeTheme.GradientPlateBox(
            MenuChromeTheme.PlateTop with { A = 0.92f },
            MenuChromeTheme.PlateBottom with { A = 0.95f },
            MenuChromeTheme.BorderDark,
            innerTopLine: new Color(MenuChromeTheme.Highlight, 0.06f),
            innerBottomLine: MenuChromeTheme.RuleDark,
            brassEdge: MenuChromeTheme.RuleLight,
            borderThickness: 1));
        _card.AddChild(new MenuChromeBrassFrame { Name = "DossierBrassFrame" });
        AddChild(_card);
        // The dark umber plate is there so the words read cleanly over a picture; the plain chart needs none.
        _card.Visible = pictured;
        _body = new VBoxContainer { Name = "Dossier", MouseFilter = MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("separation", 14);
        AddChild(_body);
        _caption = Text("STRATEGIC DOSSIER", 12, MenuChromeTheme.BrassHi);
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _body.AddChild(_caption);
        _logoPlate = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _logoPlate.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = Colors.Transparent });
        _body.AddChild(_logoPlate);
        _logo = new TextureRect
        {
            Name = "LoadingGameLogo", Texture = BrandAssets.GamePlaque(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _logoPlate.AddChild(_logo);
        _logo.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _heading = Text("Preparing the world", 26, MenuChromeTheme.Paper, serif: true, display: true);
        _heading.HorizontalAlignment = HorizontalAlignment.Center;
        _body.AddChild(_heading);
        _stage = Text("Opening the archive", 16, MenuChromeTheme.PaperDim, serif: true);
        _stage.HorizontalAlignment = HorizontalAlignment.Center;
        _body.AddChild(_stage);
        _progress = new ProgressBar
        {
            Name = "StageProgress", ShowPercentage = false, CustomMinimumSize = new Vector2(0, 6),
            MouseFilter = MouseFilterEnum.Ignore
        };
        var track = new StyleBoxFlat
        {
            BgColor = MenuChromeTheme.WellTop,
            BorderColor = MenuChromeTheme.RuleLight,
            CornerRadiusTopLeft = 1, CornerRadiusTopRight = 1,
            CornerRadiusBottomLeft = 1, CornerRadiusBottomRight = 1
        };
        track.SetBorderWidthAll(1);
        var fill = new StyleBoxFlat
        {
            BgColor = MenuChromeTheme.BrassHi,
            ContentMarginTop = 2, ContentMarginBottom = 2
        };
        _progress.AddThemeStyleboxOverride("background", track);
        _progress.AddThemeStyleboxOverride("fill", fill);
        _body.AddChild(_progress);
        _detail = Text("", 13, MenuChromeTheme.PaperFaint);
        _detail.HorizontalAlignment = HorizontalAlignment.Center;
        _body.AddChild(_detail);
        _dismiss = new Button { Name = "LoadingDismiss", Visible = false, CustomMinimumSize = new Vector2(0, 44) };
        MenuChromeTheme.StylePrimaryCta(_dismiss, 15, serif: true);
        _body.AddChild(_dismiss);
        _footer = Text(Loc.Get(Motto), 14, MenuChromeTheme.PaperDim, serif: true);
        _footer.Name = "LoadingMotto";
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_footer);
        Resized += Layout;
        _body.MinimumSizeChanged += Layout;
        Layout();
    }

    /// <summary>The menu's picture of the world, or nothing if a mod removed it.</summary>
    private static Texture2D WorldPicture()
    {
        var path = SpriteCatalog.TextureOf("menu_world", MenuWorld.ShippedPath);
        return string.IsNullOrWhiteSpace(path) ? null : Picture(path);
    }

    private static Label Text(string text, int size, Color color, bool serif = false, bool display = false)
    {
        var label = new Label { Text = Loc.Get(text), AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontOverride("font", display ? MenuChromeTheme.SerifDisplay() : serif ? MenuChromeTheme.SerifBody() : MenuChromeTheme.SansBody());
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", color);
        return label;
    }

    private void Layout()
    {
        var width = Mathf.Min(640, Size.X - 80);
        _body.Size = new Vector2(width, 0);
        _logoPlate.CustomMinimumSize = new Vector2(0, Mathf.Clamp(Size.Y * 0.22f, 120, 236));
        _body.Position = new Vector2((Size.X - width) / 2,
            Mathf.Max(48, Mathf.Min(Size.Y * 0.18f, Size.Y - 110 - _body.GetCombinedMinimumSize().Y)));
        _footer.Position = new Vector2(40, Size.Y - 70);
        _footer.Size = new Vector2(Size.X - 80, 40);
        _card.Position = _body.Position - new Vector2(32, 28);
        _card.Size = new Vector2(width + 64, _body.GetCombinedMinimumSize().Y + 56);
        _scene.PivotOffset = Size / 2;
        QueueRedraw();
        _frame.QueueRedraw();
    }

    /// <summary>
    /// The next illustration in turn, the first one by the clock; null when there are none.
    /// Every sprite named loading_* is one - the base game's gfx/loading and a mod's gfx alike, a
    /// mod replacing one by giving its file the same name - and before the sprite catalogue
    /// exists, at the very first loading, the base folder is read directly.
    /// </summary>
    private static Texture2D NextScene()
    {
        var paths = IllustrationPaths();
        if (paths.Length == 0) return null;
        _sceneTurn = _sceneTurn < 0 ? (int)(Time.GetUnixTimeFromSystem() / 60 % paths.Length) : (_sceneTurn + 1) % paths.Length;
        return Picture(paths[_sceneTurn]);
    }

    /// <summary>Every loading_* illustration, base game and mods alike, in name order; none when the player turned them off with this setting.</summary>
    internal static string[] IllustrationPaths(string setting = "loading_without_ai_art")
    {
        if (Plain(setting)) return System.Array.Empty<string>();
        string[] paths;
        try
        {
            paths = SpriteCatalog.Count > 0
                ? SpriteCatalog.Names.Where(name => name.StartsWith("loading_", System.StringComparison.Ordinal))
                    .OrderBy(name => name, System.StringComparer.Ordinal)
                    .Select(name => SpriteCatalog.TextureOf(name)).Where(path => path.Length > 0).ToArray()
                : ResourceLoader.ListDirectory(SceneFolder)
                    .Where(f => f.EndsWith(".jpg") || f.EndsWith(".png") || f.EndsWith(".webp"))
                    .OrderBy(f => f, System.StringComparer.Ordinal).Select(f => SceneFolder + f).ToArray();
        }
        catch (System.Exception) { return System.Array.Empty<string>(); }
        return paths;
    }

    /// <summary>An imported picture, or a mod's loose file read as it is.</summary>
    internal static Texture2D Picture(string path)
    {
        if (string.IsNullOrWhiteSpace(path)) return null;
        if (ResourceLoader.Exists(path)) return GD.Load<Texture2D>(path);
        var loose = path.StartsWith("res://", System.StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
        if (!System.IO.File.Exists(loose)) return null;
        var image = Image.LoadFromFile(loose);
        return image is null || image.IsEmpty() ? null : ImageTexture.CreateFromImage(image);
    }

    public void SetStage(string heading, string stage, double completed, string detail)
    {
        _caption.Text = Loc.Get("STRATEGIC DOSSIER");
        _footer.Text = Loc.Get(Motto);
        _heading.Text = Loc.Get(heading);
        _stage.Text = Loc.Get(stage);
        _detail.Text = Loc.Get(detail);
        _progress.Value = completed;
    }

    public void ShowFailure(string message, bool canReturn, System.Action dismiss)
    {
        _heading.Text = Loc.Get("Loading could not be completed");
        _stage.MaxLinesVisible = 3;
        _stage.TextOverrunBehavior = TextServer.OverrunBehavior.TrimEllipsis;
        _stage.Text = Loc.Get(message);
        _detail.Text = Loc.Get("Details have been written to the error log.");
        _dismiss.Text = Loc.Get(canReturn ? "Return to game" : "Quit to desktop");
        _dismiss.Pressed += dismiss;
        _dismiss.Show();
        _dismiss.GrabFocus();
        Layout();
    }

    public override void _Draw()
    {
        DrawRect(new Rect2(Vector2.Zero, Size), MenuChromeTheme.BackdropColor);
        var grid = MenuChromeTheme.RuleLight with { A = 0.24f };
        for (var x = 32f; x < Size.X; x += 64) DrawLine(new Vector2(x, 32), new Vector2(x, Size.Y - 32), grid);
        for (var y = 32f; y < Size.Y; y += 64) DrawLine(new Vector2(32, y), new Vector2(Size.X - 32, y), grid);
    }

    /// <summary>The gold frame and the cartographic seal, drawn over the picture.</summary>
    private void DrawFrame()
    {
        var frame = MenuChromeTheme.RuleLight with { A = 0.72f };
        var brass = MenuChromeTheme.BrassHi;
        _frame.DrawRect(new Rect2(new Vector2(26, 26), Size - new Vector2(52, 52)), frame, false, 1);
        foreach (var corner in new[] { new Vector2(26, 26), new Vector2(Size.X - 26, 26),
                     new Vector2(26, Size.Y - 26), Size - new Vector2(26, 26) })
        {
            var direction = new Vector2(corner.X < Size.X / 2 ? 1 : -1, corner.Y < Size.Y / 2 ? 1 : -1);
            _frame.DrawLine(corner, corner + new Vector2(36 * direction.X, 0), brass, 2);
            _frame.DrawLine(corner, corner + new Vector2(0, 36 * direction.Y), brass, 2);
        }
        // A quiet cartographic seal, not a shader or another rendered 3D scene.
        var center = new Vector2(Size.X * 0.87f, Size.Y * 0.47f);
        var radius = Size.Y * 0.35f;
        var ink = MenuChromeTheme.Brass with { A = 0.16f };
        _frame.DrawArc(center, radius, 0, Mathf.Tau, 96, ink, 1, true);
        _frame.DrawLine(center - new Vector2(radius, 0), center + new Vector2(radius, 0), ink);
        _frame.DrawLine(center - new Vector2(0, radius), center + new Vector2(0, radius), ink);
        for (var meridian = 1; meridian <= 3; meridian++)
        {
            var points = new Vector2[97];
            for (var i = 0; i < points.Length; i++)
            {
                var a = Mathf.Tau * i / 96;
                points[i] = center + new Vector2(Mathf.Cos(a) * radius * meridian / 4, Mathf.Sin(a) * radius);
            }
            _frame.DrawPolyline(points, ink, 1, true);
        }
    }
}
