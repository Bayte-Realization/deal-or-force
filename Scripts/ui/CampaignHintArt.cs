using System;
using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>One atlas upload, cropped views, no image readbacks during HUD refresh/draw.
/// A mod can override hint_NAME as an ordinary sprite without replacing the atlas.</summary>
public static class CampaignHintArt
{
    private static readonly string[] Cells = {
        "must", "should", "could", "important", "treasury", "good_food", "materials", "good_energy",
        "good_ammunition", "good_copper", "good_lithium", "good_sulfur", "research", "elections", "decision", "personnel",
        "alert_construction", "alert_supply", "diplomacy", "organisations", "wars", "alert_battle", "unrest", "alert_event"
    };
    private static readonly Dictionary<string, Texture2D> Cache = new(StringComparer.Ordinal);
    private static Texture2D _atlas;
    static CampaignHintArt() => SpriteCatalog.Changed += () => { Cache.Clear(); _atlas = null; };

    public static Texture2D Of(string name)
    {
        if (Cache.TryGetValue(name, out var known)) return known;
        Texture2D result;
        if (SpriteCatalog.Has("hint_" + name))
        {
            var own = GD.Load<Texture2D>(SpriteCatalog.TextureOf("hint_" + name));
            using var image = own.GetImage();
            result = Crop(own, image, new(0, 0, image.GetWidth(), image.GetHeight()));
        }
        else if (SpriteCatalog.Has("icon_" + HudRole(name)))
        {
            // Navigation, telemetry and alerts share one authored atlas-gold icon
            // family. Severity belongs to the border/badge, not unrelated colourful
            // illustrations. An explicit hint_NAME mod override above still wins.
            var stock = GD.Load<Texture2D>(SpriteCatalog.TextureOf("icon_" + HudRole(name)));
            using var image = stock.GetImage();
            result = Crop(stock, image, new(0, 0, image.GetWidth(), image.GetHeight()));
        }
        else if (Array.IndexOf(Cells, name) is var cell && cell >= 0)
        {
            _atlas ??= GD.Load<Texture2D>(SpriteCatalog.TextureOf("hints_atlas", "res://game/gfx/interface/campaign_hints/hints_atlas.png"));
            using var pixels = _atlas.GetImage();
            var w = pixels.GetWidth() / 4; var h = pixels.GetHeight() / 6;
            // Scan the source once and release the CPU image immediately. The cached
            // AtlasTexture views all share one GPU resource, not 24 copied PNGs.
            for (var i = 0; i < Cells.Length; i++)
                if (!Cache.ContainsKey(Cells[i]) && !SpriteCatalog.Has("hint_"+Cells[i]))
                    Cache[Cells[i]] = Crop(_atlas, pixels, new(i % 4 * w, i / 4 * h, w, h));
            result = Cache[name];
        }
        else
        {
            var stock = GD.Load<Texture2D>(SpriteCatalog.TextureOf("icon_" + name, "res://game/gfx/interface/icons/icon_ledger.png"));
            using var image = stock.GetImage();
            result = Crop(stock, image, new(0, 0, image.GetWidth(), image.GetHeight()));
        }
        return Cache[name] = result;
    }

    internal static string HudRole(string name) => name switch
    {
        "must" => "unrest", "should" => "decision", "could" => "diplomacy", "important" => "ledger",
        "materials" => "industry", "organisations" => "alliance", "elections" => "government",
        "personnel" => "manpower", _ => name
    };

    private static Texture2D Crop(Texture2D texture, Image image, Rect2I cell)
    {
        var left = cell.End.X; var top = cell.End.Y; var right = cell.Position.X - 1; var bottom = cell.Position.Y - 1;
        for (var y = cell.Position.Y; y < cell.End.Y; y++)
        for (var x = cell.Position.X; x < cell.End.X; x++)
        {
            if (image.GetPixel(x, y).A < .06f) continue;
            left = Math.Min(left, x); right = Math.Max(right, x); top = Math.Min(top, y); bottom = Math.Max(bottom, y);
        }
        var region = right < left ? (Rect2)cell : new Rect2(left, top, right - left + 1, bottom - top + 1);
        return new AtlasTexture { Atlas = texture, Region = region, FilterClip = true };
    }

    public static Rect2 CenteredRect(Vector2 centre, Vector2 sourceSize, float edge)
    {
        var scale = edge / MathF.Max(1, MathF.Max(sourceSize.X, sourceSize.Y));
        var size = sourceSize * scale;
        return new Rect2(centre - size * .5f, size);
    }
}
