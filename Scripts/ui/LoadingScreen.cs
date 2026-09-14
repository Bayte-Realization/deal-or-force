using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>A lightweight cover that can render before catalogs or the world exist.</summary>
public partial class LoadingScreen : Control
{
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
    public bool CanDismiss => _dismiss is { Visible: true };

    public override void _Ready()
    {
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        MouseFilter = MouseFilterEnum.Stop;
        FocusMode = FocusModeEnum.All;
        _paper = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://game/gfx/interface/panels/panel_dossier.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCovered,
            Modulate = new Color(1, 1, 1, 0.12f), MouseFilter = MouseFilterEnum.Ignore
        };
        AddChild(_paper);
        _paper.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _body = new VBoxContainer { Name = "Dossier", MouseFilter = MouseFilterEnum.Ignore };
        _body.AddThemeConstantOverride("separation", 20);
        AddChild(_body);
        _caption = Text("STRATEGIC DOSSIER", 13, "b7a36d");
        _caption.HorizontalAlignment = HorizontalAlignment.Center;
        _body.AddChild(_caption);
        _logoPlate = new Panel { MouseFilter = MouseFilterEnum.Ignore };
        _logoPlate.AddThemeStyleboxOverride("panel", new StyleBoxFlat { BgColor = new Color("e9e4d9") });
        _body.AddChild(_logoPlate);
        _logo = new TextureRect
        {
            Texture = GD.Load<Texture2D>("res://game/gfx/interface/brand_logo_banner.png"),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _logoPlate.AddChild(_logo);
        _logo.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        _heading = Text("Preparing the world", 28, "eee8d9");
        _body.AddChild(_heading);
        _stage = Text("Opening the archive", 17, "ceba80");
        _body.AddChild(_stage);
        _progress = new ProgressBar
        {
            Name = "StageProgress", ShowPercentage = false, CustomMinimumSize = new Vector2(0, 4),
            MouseFilter = MouseFilterEnum.Ignore
        };
        _progress.AddThemeStyleboxOverride("background", new StyleBoxFlat { BgColor = new Color("293642") });
        _progress.AddThemeStyleboxOverride("fill", new StyleBoxFlat
        {
            BgColor = new Color("c2a868"), ContentMarginTop = 2, ContentMarginBottom = 2
        });
        _body.AddChild(_progress);
        _detail = Text("", 14, "9caab5");
        _body.AddChild(_detail);
        _dismiss = new Button { Name = "LoadingDismiss", Visible = false, CustomMinimumSize = new Vector2(0, 44) };
        _body.AddChild(_dismiss);
        _footer = Text("Every decision leaves its mark on the map.", 14, "7c8b97");
        _footer.HorizontalAlignment = HorizontalAlignment.Center;
        AddChild(_footer);
        Resized += Layout;
        _body.MinimumSizeChanged += Layout;
        Layout();
    }

    private static Label Text(string text, int size, string color)
    {
        var label = new Label { Text = text, AutowrapMode = TextServer.AutowrapMode.WordSmart,
            MouseFilter = MouseFilterEnum.Ignore };
        label.AddThemeFontSizeOverride("font_size", size);
        label.AddThemeColorOverride("font_color", new Color(color));
        return label;
    }

    private void Layout()
    {
        var width = Mathf.Min(640, Size.X - 80);
        _body.Size = new Vector2(width, 0);
        _logoPlate.CustomMinimumSize = new Vector2(0, Mathf.Clamp(Size.Y * 0.245f, 125, 264));
        _body.Position = new Vector2((Size.X - width) / 2,
            Mathf.Max(48, Mathf.Min(Size.Y * 0.20f, Size.Y - 110 - _body.GetCombinedMinimumSize().Y)));
        _footer.Position = new Vector2(40, Size.Y - 70);
        _footer.Size = new Vector2(Size.X - 80, 40);
        QueueRedraw();
    }

    public void SetStage(string heading, string stage, double completed, string detail)
    {
        _caption.Text = Loc.Get("STRATEGIC DOSSIER");
        _footer.Text = Loc.Get("Every decision leaves its mark on the map.");
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
        DrawRect(new Rect2(Vector2.Zero, Size), new Color("0c1720"));
        var grid = new Color(0.30f, 0.39f, 0.44f, 0.12f);
        for (var x = 32f; x < Size.X; x += 64) DrawLine(new Vector2(x, 32), new Vector2(x, Size.Y - 32), grid);
        for (var y = 32f; y < Size.Y; y += 64) DrawLine(new Vector2(32, y), new Vector2(Size.X - 32, y), grid);
        var frame = new Color("695e44");
        DrawRect(new Rect2(new Vector2(26, 26), Size - new Vector2(52, 52)), frame, false, 1);
        foreach (var corner in new[] { new Vector2(26, 26), new Vector2(Size.X - 26, 26),
                     new Vector2(26, Size.Y - 26), Size - new Vector2(26, 26) })
        {
            var direction = new Vector2(corner.X < Size.X / 2 ? 1 : -1, corner.Y < Size.Y / 2 ? 1 : -1);
            DrawLine(corner, corner + new Vector2(36 * direction.X, 0), new Color("b8a169"), 2);
            DrawLine(corner, corner + new Vector2(0, 36 * direction.Y), new Color("b8a169"), 2);
        }
        // A quiet cartographic seal, not a shader or another rendered 3D scene.
        var center = new Vector2(Size.X * 0.87f, Size.Y * 0.47f);
        var radius = Size.Y * 0.35f;
        var ink = new Color(0.42f, 0.52f, 0.56f, 0.09f);
        DrawArc(center, radius, 0, Mathf.Tau, 96, ink, 1, true);
        DrawLine(center - new Vector2(radius, 0), center + new Vector2(radius, 0), ink);
        DrawLine(center - new Vector2(0, radius), center + new Vector2(0, radius), ink);
        for (var meridian = 1; meridian <= 3; meridian++)
        {
            var points = new Vector2[97];
            for (var i = 0; i < points.Length; i++)
            {
                var a = Mathf.Tau * i / 96;
                points[i] = center + new Vector2(Mathf.Cos(a) * radius * meridian / 4, Mathf.Sin(a) * radius);
            }
            DrawPolyline(points, ink, 1, true);
        }
    }
}
