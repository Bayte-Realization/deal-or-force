using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Presentation;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>Once per process, not once per visit to the menu. Both cards remain skippable.</summary>
public partial class StartupIntro : CanvasLayer
{
    private static bool _played;
    // Keep warmed resources alive until the application uses them. No world is instantiated.
    private static readonly List<Resource> WarmResources = new();
    private readonly List<string> _pending = new();
    private Control _cover;
    private TextureRect _studio, _game;
    private Label _hint, _loading;
    private bool _warmupStarted;
    private double _warmupSeconds;
    private StartupTimeline.Card _lastCard;
    internal StartupTimeline Timeline { get; } = new();
    internal bool ResourcesReady => _warmupStarted && _pending.Count == 0;
    internal bool VerificationBlockWarmup;
    internal bool WarmupStarted => _warmupStarted;
    internal bool Finished { get; private set; }

    public static void AttachIfNeeded(MainMenuScene menu)
    {
        if (_played || OS.GetCmdlineUserArgs().Contains("--verify-build") || DisplayServer.GetName() == "headless") return;
        _played = true;
        menu.AddChild(new StartupIntro { Name = "StartupIntro" });
    }

    public override void _Ready()
    {
        Layer = 90;
        ProcessMode = ProcessModeEnum.Always;
        _cover = new Control { Name = "StartupCover", MouseFilter = Control.MouseFilterEnum.Stop };
        AddChild(_cover); _cover.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        var back = new ColorRect { Name = "StartupBackground", Color = Ui.Colour("brand.intro_background"), MouseFilter = Control.MouseFilterEnum.Ignore };
        _cover.AddChild(back); back.SetAnchorsAndOffsetsPreset(Control.LayoutPreset.FullRect);
        _studio = Picture("StudioLogo", BrandAssets.Studio());
        _game = Picture("GameLogo", BrandAssets.GamePlaque());
        _hint = Text("StartupSkipHint"); _loading = Text("StartupPreparation");
        _cover.Resized += Layout;
        Layout(); UpdateVisuals();
    }

    private TextureRect Picture(string name, Texture2D texture)
    {
        var picture = new TextureRect { Name = name, Texture = texture, ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered, MouseFilter = Control.MouseFilterEnum.Ignore };
        _cover.AddChild(picture); return picture;
    }

    private Label Text(string name)
    {
        var label = new Label { Name = name, HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center, MouseFilter = Control.MouseFilterEnum.Ignore,
            AutowrapMode = TextServer.AutowrapMode.WordSmart };
        label.AddThemeColorOverride("font_color", Ui.Colour("text.muted"));
        label.AddThemeFontSizeOverride("font_size", 15);
        _cover.AddChild(label); return label;
    }

    private void Layout()
    {
        var size = _cover.Size;
        _studio.Position = new Vector2(size.X * .15f, size.Y * .08f);
        _studio.Size = new Vector2(size.X * .70f, size.Y * .76f);
        _game.Position = new Vector2(size.X * .12f, size.Y * .08f);
        _game.Size = new Vector2(size.X * .76f, size.Y * .76f);
        _hint.Position = new Vector2(24, size.Y - 58); _hint.Size = new Vector2(Math.Max(0, size.X - 48), 36);
        _loading.Position = new Vector2(24, size.Y - 100); _loading.Size = new Vector2(Math.Max(0, size.X - 48), 36);
    }

    public override void _Process(double delta)
    {
        if (Finished) return;
        if (Timeline.Current == StartupTimeline.Card.Game)
        {
            if (!_warmupStarted) BeginWarmup();
            PollWarmup(delta);
        }
        Timeline.Tick(delta, ResourcesReady && !VerificationBlockWarmup);
        UpdateVisuals();
        if (Timeline.Current == StartupTimeline.Card.Complete)
        {
            Finished = true; _cover.Hide(); SetProcessInput(false); QueueFree();
        }
    }

    private void UpdateVisuals()
    {
        var studio = Timeline.Current == StartupTimeline.Card.Studio;
        _studio.Visible = studio; _game.Visible = !studio;
        var image = studio ? _studio : _game;
        image.Modulate = Colors.White with { A = (float)Timeline.Opacity };
        _hint.Text = Loc.Get("Press Esc, Space, Enter or click to skip");
        _loading.Visible = !studio && (!ResourcesReady || VerificationBlockWarmup);
        _loading.Text = Loc.Get(Timeline.SkipRequested ? "Skip requested. Finishing resource preparation…" : "Preparing resources…");
        if (_lastCard != Timeline.Current) { _lastCard = Timeline.Current; Layout(); }
    }

    public override void _Input(InputEvent input)
    {
        var skip = input is InputEventKey { Pressed: true, Echo: false } key &&
            (key.Keycode is Key.Escape or Key.Space or Key.Enter or Key.KpEnter || key.PhysicalKeycode is Key.Escape or Key.Space or Key.Enter or Key.KpEnter)
            || input is InputEventMouseButton { Pressed: true, ButtonIndex: MouseButton.Left }
            || input is InputEventScreenTouch { Pressed: true }
            || input is InputEventJoypadButton { Pressed: true } joy && joy.ButtonIndex is JoyButton.A or JoyButton.B or JoyButton.Start;
        if (!skip || Finished) return;
        Timeline.Skip();
        GetViewport().SetInputAsHandled();
    }

    private void BeginWarmup()
    {
        _warmupStarted = true;
        var paths = new List<string> { "res://Scenes/Main.tscn" };
        if (!SettingsCatalog.Flag("loading_without_ai_art"))
            paths.AddRange(new[] { "loading_war_room", "loading_negotiation_hall", "loading_port_dusk", "loading_winter_front" }
                .Select(name => SpriteCatalog.TextureOf(name, "res://game/gfx/loading/" + name + ".jpg")));
        foreach (var path in paths.Distinct(StringComparer.Ordinal))
        {
            if (!ResourceLoader.Exists(path)) continue;
            var result = ResourceLoader.LoadThreadedRequest(path);
            if (result == Error.Ok || result == Error.Busy) _pending.Add(path);
            else GD.PushWarning("Optional startup warmup was not started: " + path + " (" + result + ")");
        }
    }

    private void PollWarmup(double delta)
    {
        _warmupSeconds += Math.Max(0, delta);
        for (var i = _pending.Count - 1; i >= 0; i--)
        {
            var path = _pending[i];
            var state = ResourceLoader.LoadThreadedGetStatus(path);
            if (state == ResourceLoader.ThreadLoadStatus.InProgress && _warmupSeconds < 30) continue;
            if (state == ResourceLoader.ThreadLoadStatus.Loaded)
            {
                var resource = ResourceLoader.LoadThreadedGet(path);
                if (resource is not null && !WarmResources.Contains(resource)) WarmResources.Add(resource);
            }
            else GD.PushWarning("Optional startup warmup deferred to normal campaign loading: " + path);
            // A failed or slow OPTIONAL preload must not strand the player at a logo.
            // The real campaign loader still reports any actual missing-resource error.
            _pending.RemoveAt(i);
        }
    }
}
