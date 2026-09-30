using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Text.Json.Nodes;

namespace DealOrForce.Simulation;

/// <summary>Pure layout authoring math. Drafts never mutate the running skin.</summary>
public static class LayoutWorkbench
{
    public readonly record struct Bounds(float X, float Y, float Width, float Height);

    public static Dictionary<string, InterfaceSkin.PanelRect> Read(string json,
        IEnumerable<(string Key, InterfaceSkin.PanelRect Rect)> inherited)
    {
        var result = inherited.ToDictionary(p => p.Key, p => p.Rect, StringComparer.OrdinalIgnoreCase);
        var root = Parse(json);
        if (root["layout"] is null) return result;
        if (root["layout"] is not JsonObject layout) throw new InvalidDataException("layout: expected an object.");
        foreach (var entry in layout)
        {
            if (entry.Key.StartsWith("//") || entry.Key.StartsWith('#')) continue;
            if (entry.Value is not JsonObject panel) throw new InvalidDataException(entry.Key + ": expected an object.");
            var previous = result.GetValueOrDefault(entry.Key, new(0, 0, 1, 1, 40, 40, -40, -40));
            var anchors = ReadFour(panel, "anchors", Anchors(previous), true);
            var offsets = ReadFour(panel, "offsets", Offsets(previous), false);
            result[entry.Key] = Make(anchors, offsets);
        }
        return result;
    }

    public static string SetRect(string json, string key, InterfaceSkin.PanelRect rect)
    {
        if (string.IsNullOrWhiteSpace(key)) throw new InvalidDataException("Select a panel.");
        var root = Parse(json);
        if (root["layout"] is null) root["layout"] = new JsonObject();
        if (root["layout"] is not JsonObject layout) throw new InvalidDataException("layout: expected an object.");
        if (layout[key] is null) layout[key] = new JsonObject();
        if (layout[key] is not JsonObject panel) throw new InvalidDataException(key + ": expected an object.");
        panel["anchors"] = JsonSerializer.SerializeToNode(Anchors(rect));
        panel["offsets"] = JsonSerializer.SerializeToNode(Offsets(rect));
        var updated = root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        Read(updated, Array.Empty<(string, InterfaceSkin.PanelRect)>()); // Reject non-finite/out-of-range input before adoption.
        return updated;
    }

    public static string RemoveOverride(string json, string key)
    {
        var root = Parse(json);
        if (root["layout"] is JsonObject layout) layout.Remove(key);
        return root.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
    }

    public static bool HasOverride(string json, string key) => Parse(json)["layout"] is JsonObject layout && layout.ContainsKey(key);
    public static float[] Anchors(InterfaceSkin.PanelRect r) => new[] { r.AnchorLeft, r.AnchorTop, r.AnchorRight, r.AnchorBottom };
    public static float[] Offsets(InterfaceSkin.PanelRect r) => new[] { r.OffsetLeft, r.OffsetTop, r.OffsetRight, r.OffsetBottom };
    public static InterfaceSkin.PanelRect Make(float[] a, float[] o) => new(a[0], a[1], a[2], a[3], o[0], o[1], o[2], o[3]);
    public static Bounds Measure(InterfaceSkin.PanelRect r, float width, float height)
    {
        var x = r.AnchorLeft * width + r.OffsetLeft;
        var y = r.AnchorTop * height + r.OffsetTop;
        return new(x, y, r.AnchorRight * width + r.OffsetRight - x, r.AnchorBottom * height + r.OffsetBottom - y);
    }

    public static InterfaceSkin.PanelRect Move(InterfaceSkin.PanelRect r, float dx, float dy) => r with
    { OffsetLeft = r.OffsetLeft + dx, OffsetRight = r.OffsetRight + dx, OffsetTop = r.OffsetTop + dy, OffsetBottom = r.OffsetBottom + dy };

    public static InterfaceSkin.PanelRect Resize(InterfaceSkin.PanelRect r, float dx, float dy, float width, float height)
    {
        var bounds = Measure(r, width, height);
        return r with { OffsetRight = r.OffsetRight + Math.Max(dx, 32 - bounds.Width),
            OffsetBottom = r.OffsetBottom + Math.Max(dy, 24 - bounds.Height) };
    }

    /// <summary>Recover an off-screen draft at this resolution, retaining its responsive anchors.</summary>
    public static InterfaceSkin.PanelRect FitInside(InterfaceSkin.PanelRect r, float width, float height)
    {
        if (!float.IsFinite(width) || !float.IsFinite(height) || width <= 0 || height <= 0)
            throw new ArgumentOutOfRangeException(nameof(width), "Reference dimensions must be finite and positive.");
        var b = Measure(r, width, height);
        if (b.Width > 0 && b.Height > 0 && b.X >= 0 && b.Y >= 0 && b.X + b.Width <= width && b.Y + b.Height <= height)
            return r;
        var w = Math.Clamp(b.Width, Math.Min(32, width), width);
        var h = Math.Clamp(b.Height, Math.Min(24, height), height);
        var x = Math.Clamp(b.X, 0, width - w);
        var y = Math.Clamp(b.Y, 0, height - h);
        return r with { OffsetLeft = x - r.AnchorLeft * width, OffsetTop = y - r.AnchorTop * height,
            OffsetRight = x + w - r.AnchorRight * width, OffsetBottom = y + h - r.AnchorBottom * height };
    }

    // Rebinding keeps the rectangle in place at the reference resolution.
    public static InterfaceSkin.PanelRect Reanchor(InterfaceSkin.PanelRect r, float[] anchors, float width, float height)
    {
        var b = Measure(r, width, height);
        return Make(anchors, new[] { b.X - anchors[0] * width, b.Y - anchors[1] * height,
            b.X + b.Width - anchors[2] * width, b.Y + b.Height - anchors[3] * height });
    }

    private static JsonObject Parse(string json) => JsonNode.Parse(json, documentOptions: GameDataLoader.ForgivingJson) as JsonObject
        ?? throw new InvalidDataException("A layout document must be an object.");
    private static float[] ReadFour(JsonObject panel, string key, float[] fallback, bool anchors)
    {
        if (!panel.ContainsKey(key)) return fallback;
        if (panel[key] is not JsonArray array || array.Count != 4) throw new InvalidDataException(key + ": expected four numbers.");
        var values = new float[4];
        for (var i = 0; i < 4; i++)
        {
            if (array[i] is not JsonValue value || !value.TryGetValue<float>(out values[i]) || !float.IsFinite(values[i]) ||
                (anchors ? values[i] < 0 || values[i] > 1 : Math.Abs(values[i]) > 32768))
                throw new InvalidDataException(key + ": invalid number.");
        }
        return values;
    }
}
