using System;
using Godot;
using DealOrForce.Simulation;

namespace DealOrForce;

/// <summary>
/// The world behind the main menu: a small live planet.
///
/// The menu stood on a photograph of the game's map - flag badges, capital stars and all -
/// which read as a screenshot with buttons on it. The campaign's own globe cannot be put
/// here instead: it is bound to a campaign, builds two hundred megabytes of rasters and
/// takes seconds, and the whole reason the menu is a scene of its own is to be on screen
/// before any of that exists.
///
/// So this is a planet of its own, and a cheap one: one ball, one picture of the world baked
/// from the game's own map (Tools/bake-menu-globe.py - first-day frontiers, country colours,
/// relief, no icons, no words), one picture of its lights at night, and three short shaders
/// with no engine lights and no shadows. It turns slowly, the dusk line crosses it, and it
/// answers the hand: the pointer leans the view a little, and a drag spins the ball with the
/// same glide the campaign's globe has.
///
/// The camera never turns; the ball does. That keeps the sun where it is, so the terminator
/// moves over the continents instead of being painted onto them.
/// </summary>
public partial class MenuGlobe : Control
{
    public const string ShippedWorld = "res://game/gfx/interface/brand/menu_globe.jpg";
    public const string ShippedLights = "res://game/gfx/interface/brand/menu_globe_lights.png";

    /// <summary>
    /// Holds the planet still, for anything that photographs the menu and compares the
    /// picture with yesterday's: a screenshot of a turning ball is never the same twice.
    /// </summary>
    public static bool Frozen;

    private const float Radius = 5f;
    private const float Distance = 17.4f;
    private const float FieldOfView = 34f;
    /// <summary>Where the planet's centre stands across the screen: right of the middle, clear of the menu column.</summary>
    private const float CentreAcross = 0.645f;
    /// <summary>The north leans towards the viewer by this much, so Europe and Asia are not on the rim.</summary>
    private const float RestTilt = 27f;
    /// <summary>
    /// Radians a second: one turn in a little under nine minutes. At twice this the planet had
    /// turned its back - the Atlantic - by the time the opening cards were over.
    /// </summary>
    private const float Spin = 0.012f;
    /// <summary>The longitude facing the viewer when the menu opens.</summary>
    private const float OpeningLongitude = 42f;

    private SubViewport _viewport;
    private TextureRect _picture;
    private Camera3D _camera;
    private Node3D _planet;
    private float _yaw = Mathf.DegToRad(-OpeningLongitude);
    private float _tilt = RestTilt;
    private float _glide;
    private bool _dragging;
    private Vector2 _lean;

    /// <summary>True when both pictures and the shader are there to make a planet from.</summary>
    public static bool Available()
    {
        var world = SpriteCatalog.TextureOf("menu_globe", ShippedWorld);
        var shader = SpriteCatalog.ShaderOf("menu_globe", "res://game/gfx/interface/shaders/menu_globe.gdshader");
        return !string.IsNullOrWhiteSpace(world) && ResourceLoader.Exists(world) && ResourceLoader.Exists(shader);
    }

    public override void _Ready()
    {
        Name = "MenuGlobe";
        SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        // The bottom layer of the menu: everything with a job sits above it and takes its own
        // clicks first, so what arrives here is a press on the world itself.
        MouseFilter = MouseFilterEnum.Stop;
        MouseDefaultCursorShape = CursorShape.Arrow;

        var isTouchPrimary = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var aaChoice = (SettingsCatalog.Chosen("map_antialiasing") ?? "").Trim().ToLowerInvariant();
        Viewport.Msaa menuMsaa;
        if (aaChoice is "off" or "0" or "none")
            menuMsaa = Viewport.Msaa.Disabled;
        else if (aaChoice is "4x" or "4")
            menuMsaa = Viewport.Msaa.Msaa4X;
        else if (aaChoice is "2x" or "2")
            menuMsaa = Viewport.Msaa.Msaa2X;
        else if (isTouchPrimary)
            menuMsaa = Viewport.Msaa.Disabled;
        else
            menuMsaa = Viewport.Msaa.Msaa2X;

        _viewport = new SubViewport
        {
            Name = "MenuGlobeView",
            OwnWorld3D = true,
            TransparentBg = false,
            PhysicsObjectPicking = false,
            HandleInputLocally = false,
            ScreenSpaceAA = Viewport.ScreenSpaceAAEnum.Disabled,
            AnisotropicFilteringLevel = isTouchPrimary
                ? Viewport.AnisotropicFiltering.Anisotropy4X
                : Viewport.AnisotropicFiltering.Anisotropy8X,
            TextureMipmapBias = isTouchPrimary ? -0.35f : -0.20f,
            Msaa3D = menuMsaa,
            RenderTargetUpdateMode = SubViewport.UpdateMode.Always
        };
        AddChild(_viewport);

        var sun = new Vector3(-0.76f, 0.32f, 0.42f).Normalized();
        var ground = MenuChromeTheme.BackdropColor.Darkened(0.45f);
        var twilight = new Color(0.32f, 0.50f, 0.76f);
        var lampTone = new Color(0.95f, 0.89f, 0.80f);
        var starTone = MenuChromeTheme.Paper;

        _viewport.AddChild(new WorldEnvironment
        {
            Environment = new Godot.Environment
            {
                BackgroundMode = Godot.Environment.BGMode.Color,
                BackgroundColor = ground,
                AmbientLightSource = Godot.Environment.AmbientSource.Disabled
            }
        });

        _camera = new Camera3D { Name = "Eye", Fov = FieldOfView, Near = 0.5f, Far = 160f, Current = true };
        _viewport.AddChild(_camera);

        var sky = Shell("Sky", 90f, 48, 24, "menu_globe_sky");
        if (sky.MaterialOverride is ShaderMaterial skyMaterial)
        {
            skyMaterial.SetShaderParameter("ground_colour", ground);
            skyMaterial.SetShaderParameter("star_colour", starTone);
            skyMaterial.SetShaderParameter("star_strength", 0.78f);
        }
        _viewport.AddChild(sky);

        _planet = new Node3D { Name = "Planet" };
        _viewport.AddChild(_planet);
        var ball = Shell("World", Radius, 128, 64, "menu_globe");
        if (ball.MaterialOverride is ShaderMaterial worldMaterial)
        {
            worldMaterial.SetShaderParameter("world_map", Picture("menu_globe", ShippedWorld));
            worldMaterial.SetShaderParameter("night_lights", Picture("menu_globe_lights", ShippedLights));
            worldMaterial.SetShaderParameter("sun_direction", sun);
            worldMaterial.SetShaderParameter("air_colour", twilight);
            worldMaterial.SetShaderParameter("lamp_colour", lampTone);
            worldMaterial.SetShaderParameter("brightness", InterfaceSkin.Metric("menu.globe_brightness", 1f));
        }
        _planet.AddChild(ball);

        var air = Shell("Air", Radius * 1.032f, 96, 48, "menu_globe_air");
        if (air.MaterialOverride is ShaderMaterial airMaterial)
        {
            airMaterial.SetShaderParameter("sun_direction", sun);
            airMaterial.SetShaderParameter("air_colour", twilight);
            airMaterial.SetShaderParameter("planet_radius", Radius);
            airMaterial.SetShaderParameter("thickness", 0.11f);
            airMaterial.SetShaderParameter("strength", 0.14f);
        }
        _viewport.AddChild(air);

        _picture = new TextureRect
        {
            Name = "MenuGlobePicture",
            Texture = _viewport.GetTexture(),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.Scale,
            MouseFilter = MouseFilterEnum.Ignore
        };
        _picture.SetAnchorsAndOffsetsPreset(LayoutPreset.FullRect);
        AddChild(_picture);

        Resized += Fit;
        if (GetWindow() is { } window) window.SizeChanged += Fit;
        Fit();
        Place();
    }

    public override void _ExitTree()
    {
        if (GetWindow() is { } window) window.SizeChanged -= Fit;
    }

    /// <summary>
    /// The view is drawn at the window's real pixels, not at the interface's design size: on
    /// a 4K screen the interface is scaled up, and a planet drawn at 1920 and stretched with
    /// it would be the one soft thing on a sharp screen.
    /// </summary>
    private void Fit()
    {
        if (_viewport is null || GetWindow() is not { } window) return;
        var pixels = window.Size;
        if (pixels.X < 16 || pixels.Y < 16) return;
        if (Platform.PlatformProfile.Current?.IsTouchPrimary == true)
        {
            var longEdge = Math.Max(pixels.X, pixels.Y);
            if (longEdge > 2048)
            {
                var ratio = 2048f / Math.Max(1, longEdge);
                pixels = new Vector2I(
                    Math.Max(1, (int)MathF.Round(pixels.X * ratio)),
                    Math.Max(1, (int)MathF.Round(pixels.Y * ratio)));
            }
        }
        if (_viewport.Size != pixels) _viewport.Size = pixels;
        Place();
    }

    /// <summary>The eye stands left of the planet by as much as puts the planet right of the middle.</summary>
    private void Place()
    {
        if (_camera is null || _viewport is null) return;
        var size = _viewport.Size;
        var aspect = size.Y > 0 ? size.X / (float)size.Y : 16f / 9f;
        var halfWidth = Distance * Mathf.Tan(Mathf.DegToRad(FieldOfView) * 0.5f) * aspect;
        var across = (CentreAcross - 0.5f) * 2f * halfWidth;
        _camera.Position = new Vector3(-across + _lean.X * 0.22f, _lean.Y * -0.14f, Distance);
        _planet.Basis = new Basis(Vector3.Right, Mathf.DegToRad(_tilt)) * new Basis(Vector3.Up, _yaw);
    }

    public override void _Process(double delta)
    {
        if (_planet is null) return;
        var step = (float)delta;
        if (!Frozen && !_dragging)
        {
            _yaw += (Spin + _glide) * step;
            // The same glide the campaign's globe has: what the hand gave it dies away in
            // about a second, and the planet's own slow turn is what is left.
            _glide *= Mathf.Exp(-2.4f * step);
            _tilt = Mathf.Lerp(_tilt, RestTilt, 1f - Mathf.Exp(-0.6f * step));
        }

        // The pointer leans the view: a few hundredths of the screen, eased, so the planet
        // has depth without the menu swimming.
        var wanted = Vector2.Zero;
        if (!Frozen && Size.X > 1f && Size.Y > 1f)
        {
            var pointer = GetLocalMousePosition() / Size - new Vector2(0.5f, 0.5f);
            wanted = new Vector2(Mathf.Clamp(pointer.X, -0.5f, 0.5f), Mathf.Clamp(pointer.Y, -0.5f, 0.5f)) * 2f;
        }
        _lean = _lean.Lerp(wanted, 1f - Mathf.Exp(-3.5f * step));
        Place();
    }

    public override void _GuiInput(InputEvent @event)
    {
        if (Frozen) return;
        switch (@event)
        {
            case InputEventMouseButton { ButtonIndex: MouseButton.Left } button:
                _dragging = button.Pressed;
                MouseDefaultCursorShape = _dragging ? CursorShape.Drag : CursorShape.Arrow;
                if (_dragging) _glide = 0f;
                AcceptEvent();
                break;

            case InputEventMouseMotion motion when _dragging:
                // A drag across the planet's width turns it about a third of the way round.
                var perPixel = Size.X > 1f ? 2.2f / Size.X : 0.0015f;
                var turned = motion.Relative.X * perPixel;
                _yaw += turned;
                _tilt = Mathf.Clamp(_tilt + motion.Relative.Y * perPixel * 40f, -20f, 70f);
                var seconds = Math.Max((float)GetProcessDeltaTime(), 1f / 240f);
                _glide = Mathf.Lerp(_glide, Mathf.Clamp(turned / seconds, -4f, 4f), 0.35f);
                AcceptEvent();
                break;
        }
    }

    private static MeshInstance3D Shell(string name, float radius, int around, int rings, string shaderName)
    {
        var instance = new MeshInstance3D
        {
            Name = name,
            Mesh = new SphereMesh { Radius = radius, Height = radius * 2f, RadialSegments = around, Rings = rings },
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off
        };
        var path = SpriteCatalog.ShaderOf(shaderName, $"res://game/gfx/interface/shaders/{shaderName}.gdshader");
        if (ResourceLoader.Exists(path) && GD.Load<Shader>(path) is { } shader)
            instance.MaterialOverride = new ShaderMaterial { Shader = shader };
        return instance;
    }

    /// <summary>
    /// A sprite by name, with the small copies of itself a turning ball needs: without them
    /// a four-thousand-wide picture drawn a thousand pixels across sparkles along every
    /// frontier as it moves. Made here when the picture was imported without them.
    /// </summary>
    private static Texture2D Picture(string sprite, string shipped)
    {
        var path = SpriteCatalog.TextureOf(sprite, shipped);
        if (string.IsNullOrWhiteSpace(path) || !ResourceLoader.Exists(path)) return null;
        var texture = GD.Load<Texture2D>(path);
        if (texture is null) return null;
        // Skip main-thread GPU->CPU GetImage() readbacks + Decompress() + CPU GenerateMipmaps() on
        // touch/mobile devices and on assets already imported with mipmaps (menu_globe.jpg).
        if (Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
            string.Equals(sprite, "menu_globe", StringComparison.Ordinal))
            return texture;
        using var image = texture.GetImage();
        if (image is null || image.IsEmpty() || image.HasMipmaps()) return texture;
        if (image.IsCompressed() && image.Decompress() != Error.Ok) return texture;
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
