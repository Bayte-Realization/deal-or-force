using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>One hardware-cursor palette above the scenes, including the main menu.
/// No per-frame drawing or texture allocation. Catalog changes coalesce into one reload.</summary>
public partial class GameCursorService : Node
{
    private static GameCursorService _instance;
    private readonly List<ImageTexture> _textures = new();
    private bool _refreshQueued;
    private const string BaseFolder = "res://game/gfx/interface/cursors/";

    // Godot's 17 CursorShape values; aliases retain distinct mod sprite names.
    private static readonly (string Name, string Bundled, Vector2 Hotspot)[] Shapes = {
        ("arrow", "arrow", new(2, 2)), ("ibeam", "ibeam", new(16, 16)),
        ("hand", "hand", new(10, 3)), ("cross", "cross", new(16, 16)),
        ("wait", "wait", new(16, 16)), ("busy", "busy", new(2, 2)),
        ("drag", "move", new(16, 16)), ("can_drop", "can_drop", new(2, 2)),
        ("forbidden", "forbidden", new(16, 16)), ("vsize", "vsize", new(16, 16)),
        ("hsize", "hsize", new(16, 16)), ("bdiagsize", "bdiagsize", new(16, 16)),
        ("fdiagsize", "fdiagsize", new(16, 16)), ("move", "move", new(16, 16)),
        ("vsplit", "vsize", new(16, 16)), ("hsplit", "hsize", new(16, 16)),
        ("help", "help", new(2, 2))
    };

    public static GameCursorService Ensure(Node scene)
    {
        if (_instance is not null && IsInstanceValid(_instance)) return _instance;
        _instance = new GameCursorService { Name = "GameCursorService", ProcessMode = ProcessModeEnum.Always };
        scene.GetTree().Root.CallDeferred(Node.MethodName.AddChild, _instance);
        return _instance;
    }

    public override void _Ready()
    {
        SpriteCatalog.Changed += QueueRefresh;
        InterfaceSkin.Changed += QueueRefresh;
        Refresh();
    }

    private void QueueRefresh()
    {
        if (_refreshQueued) return;
        _refreshQueued = true;
        Callable.From(() => {
            _refreshQueued = false;
            if (IsInstanceValid(this) && IsInsideTree()) Refresh();
        }).CallDeferred();
    }

    public void Refresh()
    {
        var previous = _textures.ToArray();
        _textures.Clear();
        var installed = 0;
        var enabled = InterfaceSkin.Metric("cursor.enabled", 1) > 0;
        var scale = InterfaceSkin.Metric("cursor.scale", 1);
        scale = float.IsFinite(scale) ? Math.Clamp(scale, 0.75f, 2f) : 1;
        for (var index = 0; index < Shapes.Length; index++)
        {
            var cursor = Shapes[index];
            ImageTexture texture = null;
            var hotspot = Vector2.Zero;
            if (enabled)
            {
                var bundled = BaseFolder + "cursor_" + cursor.Bundled + ".svg";
                var path = SpriteCatalog.TextureOf("cursor_" + cursor.Name,
                    SpriteCatalog.TextureOf("cursor_" + cursor.Bundled, bundled));
                var requested = new Vector2(
                    InterfaceSkin.Metric("cursor." + cursor.Name + ".hotspot_x", cursor.Hotspot.X),
                    InterfaceSkin.Metric("cursor." + cursor.Name + ".hotspot_y", cursor.Hotspot.Y));
                try { (texture, hotspot) = LoadCursor(path, requested, scale); }
                catch (Exception e)
                {
                    GD.PushWarning($"[cursor] {cursor.Name}: {e.Message}; using bundled cursor.");
                    try { (texture, hotspot) = LoadCursor(bundled, cursor.Hotspot, scale); }
                    catch (Exception fallback) { GD.PushWarning("[cursor] " + fallback.Message); }
                }
            }
            Input.SetCustomMouseCursor(texture, (Input.CursorShape)index, hotspot);
            if (texture is not null) { _textures.Add(texture); installed++; }
        }
        foreach (var texture in previous) texture.Dispose();
        SetMeta("installed_shapes", installed);
        SetMeta("expected_shapes", Shapes.Length);
        SetMeta("cursor_scale", scale);
    }

    private static (ImageTexture Texture, Vector2 Hotspot) LoadCursor(string path, Vector2 hotspot, float scale)
    {
        Image image;
        string svgSource = null;
        if ((path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal))
            && !Godot.FileAccess.FileExists(path))
        {
            // PCK exports can omit source files: load the remapped imported texture.
            var imported = ResourceLoader.Load<Texture2D>(path);
            image = imported?.GetImage() ?? throw new InvalidDataException("Missing cursor " + path);
        }
        else
        {
            image = new Image();
            if (path.EndsWith(".svg", StringComparison.OrdinalIgnoreCase))
                svgSource = path.StartsWith("res://", StringComparison.Ordinal) || path.StartsWith("user://", StringComparison.Ordinal)
                    ? Godot.FileAccess.GetFileAsString(path) : File.ReadAllText(path);
            var error = svgSource is not null ? image.LoadSvgFromString(svgSource) : image.Load(path);
            if (error != Error.Ok) { image.Dispose(); throw new InvalidDataException("Cannot load cursor " + path); }
        }
        using (image)
        {
            var width = image.GetWidth(); var height = image.GetHeight();
            if (width <= 0 || height <= 0 || width > 256 || height > 256)
                throw new InvalidDataException("Cursor must be 1–256 pixels on each side.");
            if (!float.IsFinite(hotspot.X) || !float.IsFinite(hotspot.Y) || hotspot.X < 0 || hotspot.Y < 0 || hotspot.X >= width || hotspot.Y >= height)
                throw new InvalidDataException("Cursor hotspot must be finite and inside its image.");
            var targetWidth = Math.Clamp((int)Math.Round(width * scale), 1, 256);
            var targetHeight = Math.Clamp((int)Math.Round(height * scale), 1, 256);
            if (targetWidth != width || targetHeight != height)
            {
                if (svgSource is not null)
                {
                    // Render vectors at the final hardware size, never enlarge a tiny raster.
                    var rasterScale = Math.Min(targetWidth / (float)width, targetHeight / (float)height);
                    if (image.LoadSvgFromString(svgSource, rasterScale) != Error.Ok)
                        throw new InvalidDataException("Cannot rasterize cursor " + path);
                    targetWidth = image.GetWidth(); targetHeight = image.GetHeight();
                }
                else image.Resize(targetWidth, targetHeight, Image.Interpolation.Lanczos);
            }
            hotspot = new Vector2(Math.Clamp(MathF.Round(hotspot.X * targetWidth / width), 0, targetWidth - 1),
                Math.Clamp(MathF.Round(hotspot.Y * targetHeight / height), 0, targetHeight - 1));
            return (ImageTexture.CreateFromImage(image), hotspot);
        }
    }

    public override void _ExitTree()
    {
        SpriteCatalog.Changed -= QueueRefresh;
        InterfaceSkin.Changed -= QueueRefresh;
        for (var index = 0; index < Shapes.Length; index++) Input.SetCustomMouseCursor(null, (Input.CursorShape)index);
        foreach (var texture in _textures) texture.Dispose();
        _textures.Clear();
        if (ReferenceEquals(_instance, this)) _instance = null;
    }
}
