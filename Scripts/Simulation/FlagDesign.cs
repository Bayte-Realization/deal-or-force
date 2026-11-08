using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// A flag written down as data rather than painted as a picture: a background and a few
/// emblems laid over it, each with an exact colour.
///
/// Every flag in the game used to be a PNG, which is the right answer for a real country's
/// flag - nobody wants Mexico's eagle rebuilt out of triangles - and the wrong one for the
/// flags a modder or a player invents. A new country in a mod needed somebody to open an image
/// editor, get the proportions right, export at the size the pipeline expects and name the
/// file correctly; a player who wanted a different banner for their own country had no way at
/// all. A description is three lines a person can type, a format a program can write (the
/// in-game flag editor writes exactly this), and something the game can draw at any size.
///
/// A design is a sprite. Its id IS the sprite name, so `flag_xyz` is the flag of XYZ and
/// `flag_xyz_communism` is the flag XYZ flies while it is communist, by the same naming rule
/// common/flags already applies to pictures - a design takes part in the variant search
/// exactly as a file called that would, and needs no list to join. Where a picture and a
/// design share a name, the design wins: it is the thing somebody wrote on purpose.
///
/// The file shape, in common/flag_designs/*.json or *.yaml (the envelope is optional):
///
///     { "flag_designs": { "flag_xyz": {
///         "ratio": 1.5,
///         "background": { "pattern": "horizontal", "colours": ["#ffffff", "#0038a8", "#ce1126"] },
///         "emblems": [ { "shape": "star", "colour": "#fcd116", "x": 0.5, "y": 0.5, "size": 0.4 } ]
///     } } }
///
/// Pure C# with its own pixel buffer, so the headless tests draw the same pixels the game
/// shows; the game only wraps the buffer in a texture.
/// </summary>
public sealed class FlagDesign
{
    /// <summary>The background patterns, in the order the editor offers them.</summary>
    public static readonly string[] Patterns =
    {
        "solid", "horizontal", "vertical", "nordic_cross", "cross", "canton", "diagonal", "saltire", "hoist_triangle"
    };

    /// <summary>The emblems drawn from geometry rather than from a picture.</summary>
    public static readonly string[] Shapes =
    {
        "star", "star_ring", "crescent", "crescent_star", "disc", "ring", "sun", "diamond", "triangle", "cross", "eight_point_star"
    };

    /// <summary>The flat colours the base game's own flags are drawn in, offered first in the editor.</summary>
    public static readonly string[] Palette =
    {
        "#ffffff", "#000000", "#ce1126", "#8d1b3d", "#ff8200", "#fcd116", "#009a44", "#006233",
        "#75aadb", "#0038a8", "#002868", "#00a3dd", "#5b2c83", "#c8102e", "#d4af37", "#7b3f00"
    };

    public string Id = "";

    /// <summary>Width over height. 1.5 is 2:3, the commonest; Switzerland is 1, Qatar 2.545.</summary>
    public float Ratio = 1.5f;

    public string Pattern = "horizontal";

    /// <summary>Background colours, in the order the pattern reads them (see <see cref="Patterns"/>).</summary>
    public List<string> Colours = new() { "#ffffff", "#0038a8", "#ce1126" };

    /// <summary>
    /// Relative band thickness for striped patterns. Empty means equal bands; Spain is 1, 2, 1.
    /// </summary>
    public List<float> Weights = new();

    /// <summary>The colour of the cross, canton or triangle where a pattern has one.</summary>
    public string Accent = "";

    public List<FlagEmblem> Emblems = new();

    public FlagDesign Clone()
    {
        var copy = (FlagDesign)MemberwiseClone();
        copy.Colours = new List<string>(Colours);
        copy.Weights = new List<float>(Weights);
        copy.Emblems = Emblems.Select(e => e.Clone()).ToList();
        return copy;
    }

    /// <summary>
    /// How many background colours a pattern reads, as (fewest, most). The editor shows that
    /// many colour slots; the parser refuses fewer, so a file never draws with a missing band.
    /// </summary>
    public static (int Min, int Max) ColourRange(string pattern) => pattern switch
    {
        "solid" => (1, 1),
        "horizontal" or "vertical" => (2, 5),
        "nordic_cross" or "cross" or "saltire" => (2, 3),
        "canton" => (1, 5),
        "diagonal" => (2, 3),
        "hoist_triangle" => (1, 5),
        _ => (1, 5)
    };

    /// <summary>Whether a pattern uses <see cref="Accent"/>.</summary>
    public static bool UsesAccent(string pattern) => pattern is "canton" or "hoist_triangle";

    /// <summary>
    /// Read one design. Problems are reported with the design's id and do not throw, so one
    /// bad colour in a mod costs that design rather than the file; a design that cannot be
    /// drawn at all comes back null.
    /// </summary>
    public static FlagDesign Parse(string id, JsonElement value, List<string> errors)
    {
        void Fail(string message) => errors?.Add($"common/flag_designs: '{id}' {message}");
        if (value.ValueKind != JsonValueKind.Object) { Fail("is not an object."); return null; }
        var design = new FlagDesign { Id = id, Colours = new List<string>() };
        if (value.TryGetProperty("ratio", out var ratio))
        {
            if (ratio.ValueKind == JsonValueKind.Number && ratio.TryGetSingle(out var r) && float.IsFinite(r) && r >= 0.5f && r <= 3f)
                design.Ratio = r;
            else Fail("ratio must be a number between 0.5 and 3 (width over height).");
        }
        if (!value.TryGetProperty("background", out var background) || background.ValueKind != JsonValueKind.Object)
        {
            Fail("has no background.");
            return null;
        }
        design.Pattern = background.TryGetProperty("pattern", out var pattern) && pattern.ValueKind == JsonValueKind.String
            ? pattern.GetString()?.Trim().ToLowerInvariant() ?? "" : "solid";
        if (Array.IndexOf(Patterns, design.Pattern) < 0)
        {
            Fail($"has an unknown pattern '{design.Pattern}'. Known: {string.Join(", ", Patterns)}.");
            return null;
        }
        // Both spellings, because half the people writing these are British and the other half
        // are not, and a modder should not lose an evening to a vowel.
        var colours = background.TryGetProperty("colours", out var c) ? c
            : background.TryGetProperty("colors", out var c2) ? c2 : default;
        if (colours.ValueKind == JsonValueKind.Array)
            foreach (var item in colours.EnumerateArray())
            {
                var text = item.ValueKind == JsonValueKind.String ? item.GetString() : null;
                if (Normalise(text) is { } hex) design.Colours.Add(hex);
                else Fail($"has a colour '{text}' that is not #rrggbb.");
            }
        var (min, max) = ColourRange(design.Pattern);
        if (design.Colours.Count < min)
        {
            Fail($"pattern '{design.Pattern}' needs at least {min} colour(s), has {design.Colours.Count}.");
            return null;
        }
        if (design.Colours.Count > max)
        {
            Fail($"pattern '{design.Pattern}' draws at most {max} colours; the rest are ignored.");
            design.Colours.RemoveRange(max, design.Colours.Count - max);
        }
        if (background.TryGetProperty("weights", out var weights) && weights.ValueKind == JsonValueKind.Array)
            foreach (var item in weights.EnumerateArray())
                if (item.ValueKind == JsonValueKind.Number && item.TryGetSingle(out var w) && float.IsFinite(w) && w > 0)
                    design.Weights.Add(w);
                else Fail("has a stripe weight that is not a positive number.");
        if (design.Weights.Count > 0 && design.Weights.Count != design.Colours.Count)
        {
            Fail("has a different number of weights and colours; the stripes are drawn equal.");
            design.Weights.Clear();
        }
        var accent = background.TryGetProperty("accent", out var a) && a.ValueKind == JsonValueKind.String ? a.GetString() : "";
        if (!string.IsNullOrEmpty(accent))
        {
            if (Normalise(accent) is { } hex) design.Accent = hex;
            else Fail($"has an accent '{accent}' that is not #rrggbb.");
        }
        if (UsesAccent(design.Pattern) && design.Accent.Length == 0) design.Accent = "#0038a8";

        if (value.TryGetProperty("emblems", out var emblems))
        {
            if (emblems.ValueKind != JsonValueKind.Array) Fail("emblems must be a list.");
            else
            {
                var index = 0;
                foreach (var item in emblems.EnumerateArray())
                {
                    var emblem = FlagEmblem.Parse(item, message => Fail($"emblem {index}: {message}"));
                    if (emblem is not null) design.Emblems.Add(emblem);
                    index++;
                }
            }
        }
        return design;
    }

    /// <summary>A colour as the format stores it - "#rrggbb", lower case - or null when it is not one.</summary>
    public static string Normalise(string text)
    {
        text = text?.Trim();
        if (!StructuredData.IsHexColour(text)) return null;
        if (text.Length == 4)
            text = "#" + new string(new[] { text[1], text[1], text[2], text[2], text[3], text[3] });
        return text.ToLowerInvariant();
    }

    /// <summary>
    /// The design as a file a mod can ship: one design in the `flag_designs` envelope, with a
    /// header saying what the file is and how to change it, since the next person to open it
    /// may never have seen the editor that wrote it.
    /// </summary>
    public string ToDocument()
    {
        var builder = new StringBuilder();
        builder.AppendLine($"// Flag design '{Id}', written by the in-game flag editor. Edit it by hand freely.");
        builder.AppendLine("//");
        builder.AppendLine("// The id is the sprite name: flag_<tag> is a country's flag, flag_<tag>_<ideology> or");
        builder.AppendLine("// flag_<tag>_<government> the flag it flies under that politics (common/flags). Patterns:");
        builder.AppendLine("// " + string.Join(", ", Patterns) + ".");
        builder.AppendLine("// Emblem shapes: " + string.Join(", ", Shapes) + ", or \"sprite\": any icon name.");
        builder.AppendLine("// x and y are 0..1 across and down the flag, size is a share of its height.");
        builder.Append(ToJson());
        builder.AppendLine();
        return builder.ToString();
    }

    /// <summary>The design as JSON alone, in the `flag_designs` envelope.</summary>
    public string ToJson()
    {
        using var buffer = new MemoryStream();
        using (var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true }))
        {
            writer.WriteStartObject();
            writer.WritePropertyName("flag_designs");
            writer.WriteStartObject();
            writer.WritePropertyName(Id);
            WriteBody(writer);
            writer.WriteEndObject();
            writer.WriteEndObject();
        }
        return Encoding.UTF8.GetString(buffer.ToArray());
    }

    private void WriteBody(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        writer.WriteNumber("ratio", Math.Round(Ratio, 4));
        writer.WritePropertyName("background");
        writer.WriteStartObject();
        writer.WriteString("pattern", Pattern);
        writer.WritePropertyName("colours");
        writer.WriteStartArray();
        foreach (var colour in Colours) writer.WriteStringValue(colour);
        writer.WriteEndArray();
        if (Weights.Count == Colours.Count && Weights.Count > 0)
        {
            writer.WritePropertyName("weights");
            writer.WriteStartArray();
            foreach (var weight in Weights) writer.WriteNumberValue(Math.Round(weight, 4));
            writer.WriteEndArray();
        }
        if (Accent.Length > 0) writer.WriteString("accent", Accent);
        writer.WriteEndObject();
        if (Emblems.Count > 0)
        {
            writer.WritePropertyName("emblems");
            writer.WriteStartArray();
            foreach (var emblem in Emblems) emblem.Write(writer);
            writer.WriteEndArray();
        }
        writer.WriteEndObject();
    }

    /// <summary>
    /// Draw the design at a given height. The width follows the ratio. Every pixel is sampled
    /// four times, so a star's points and a saltire's edges are smooth at the 24-pixel size a
    /// list draws them at as much as at full size.
    /// </summary>
    /// <param name="sprites">Where a sprite emblem's pixels come from; null leaves those out.</param>
    public FlagRaster Render(int height, Func<string, FlagRaster> sprites = null)
    {
        height = Math.Clamp(height, 4, 2048);
        var width = Math.Clamp((int)MathF.Round(height * Ratio), 4, 4096);
        var raster = new FlagRaster(width, height);
        var colours = Colours.Select(Rgb).ToArray();
        if (colours.Length == 0) colours = new[] { (1f, 1f, 1f) };
        var accent = Accent.Length > 0 ? Rgb(Accent) : colours[^1];
        var emblemSprites = Emblems.Select(e => e.Sprite.Length > 0 ? sprites?.Invoke(e.Sprite) : null).ToArray();
        var emblemColours = Emblems.Select(e => Rgb(e.Colour)).ToArray();
        var offsets = new[] { (0.25f, 0.25f), (0.75f, 0.25f), (0.25f, 0.75f), (0.75f, 0.75f) };
        for (var y = 0; y < height; y++)
        for (var x = 0; x < width; x++)
        {
            float r = 0, g = 0, b = 0;
            foreach (var (ox, oy) in offsets)
            {
                // Normalised flag space: u across 0..ratio, v down 0..1, so shapes keep their
                // proportions whatever the flag's.
                var v = (y + oy) / height;
                var u = (x + ox) / height;
                var colour = Background(u, v, colours, accent);
                for (var e = 0; e < Emblems.Count; e++)
                    colour = Emblems[e].Over(colour, u, v, Ratio, emblemColours[e], emblemSprites[e]);
                r += colour.R; g += colour.G; b += colour.B;
            }
            raster.Set(x, y, r / 4f, g / 4f, b / 4f, 1f);
        }
        return raster;
    }

    private (float R, float G, float B) Background(float u, float v, (float, float, float)[] colours, (float, float, float) accent)
    {
        var across = u / Ratio;
        switch (Pattern)
        {
            case "solid":
                return colours[0];
            case "horizontal":
                return colours[Band(v, colours.Length)];
            case "vertical":
                return colours[Band(across, colours.Length)];
            case "nordic_cross":
            case "cross":
            {
                // The Nordic cross's upright stands at a third of the length, as Denmark's does;
                // the plain cross stands in the middle, as Switzerland's and England's do.
                var centreU = Pattern == "nordic_cross" ? 0.375f : Ratio / 2f;
                var half = 0.1f;
                var border = colours.Length > 2 ? 0.05f : 0f;
                var du = MathF.Abs(u - centreU);
                var dv = MathF.Abs(v - 0.5f);
                if (du < half || dv < half) return colours[colours.Length > 2 ? 2 : 1];
                if (du < half + border || dv < half + border) return colours[1];
                return colours[0];
            }
            case "saltire":
            {
                var a = MathF.Abs(v - across) * MathF.Sqrt(1 + 1f / (Ratio * Ratio));
                var b = MathF.Abs(v - (1f - across)) * MathF.Sqrt(1 + 1f / (Ratio * Ratio));
                var d = MathF.Min(a, b);
                if (d < 0.09f) return colours[colours.Length > 2 ? 2 : 1];
                if (colours.Length > 2 && d < 0.14f) return colours[1];
                return colours[0];
            }
            case "diagonal":
            {
                // Lower hoist to upper fly, as Tanzania's and the Republic of the Congo's run.
                var d = (1f - v) - across;
                if (colours.Length > 2 && MathF.Abs(d) < 0.12f) return colours[2];
                return d > 0 ? colours[0] : colours[1];
            }
            case "canton":
            {
                if (u < Ratio * 0.4f && v < 0.5f) return accent;
                return colours[Band(v, colours.Length)];
            }
            case "hoist_triangle":
            {
                var depth = Ratio * 0.45f * (1f - MathF.Abs(v - 0.5f) * 2f);
                if (u < depth) return accent;
                return colours[Band(v, colours.Length)];
            }
            default:
                return colours[0];
        }
    }

    private int Band(float t, int count)
    {
        if (count <= 1) return 0;
        t = Math.Clamp(t, 0f, 0.99999f);
        if (Weights.Count != count) return Math.Min(count - 1, (int)(t * count));
        var total = Weights.Sum();
        var at = 0f;
        for (var i = 0; i < count; i++)
        {
            at += Weights[i] / total;
            if (t < at) return i;
        }
        return count - 1;
    }

    internal static (float R, float G, float B) Rgb(string hex)
    {
        hex = Normalise(hex) ?? "#ffffff";
        float Channel(int at) => int.Parse(hex.Substring(at, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture) / 255f;
        return (Channel(1), Channel(3), Channel(5));
    }
}

/// <summary>One thing laid over a flag's background: a shape, or a picture from the sprite catalogue.</summary>
public sealed class FlagEmblem
{
    /// <summary>One of <see cref="FlagDesign.Shapes"/>, or empty when <see cref="Sprite"/> names a picture.</summary>
    public string Shape = "star";
    public string Sprite = "";
    public string Colour = "#ffffff";
    /// <summary>Centre, 0..1 across and down the whole flag.</summary>
    public float X = 0.5f, Y = 0.5f;
    /// <summary>Outer size as a share of the flag's height.</summary>
    public float Size = 0.4f;
    /// <summary>Clockwise, in degrees.</summary>
    public float Rotation;
    /// <summary>How many stars a ring holds.</summary>
    public int Count = 12;

    public FlagEmblem Clone() => (FlagEmblem)MemberwiseClone();

    public static FlagEmblem Parse(JsonElement value, Action<string> fail)
    {
        if (value.ValueKind != JsonValueKind.Object) { fail("is not an object."); return null; }
        var emblem = new FlagEmblem();
        if (value.TryGetProperty("sprite", out var sprite) && sprite.ValueKind == JsonValueKind.String &&
            sprite.GetString() is { Length: > 0 } name)
        {
            emblem.Sprite = name.Trim();
            emblem.Shape = "";
        }
        else if (value.TryGetProperty("shape", out var shape) && shape.ValueKind == JsonValueKind.String)
        {
            emblem.Shape = shape.GetString()?.Trim().ToLowerInvariant() ?? "";
            if (Array.IndexOf(FlagDesign.Shapes, emblem.Shape) < 0)
            {
                fail($"unknown shape '{emblem.Shape}'. Known: {string.Join(", ", FlagDesign.Shapes)}, or a sprite.");
                return null;
            }
        }
        var colour = value.TryGetProperty("colour", out var c) ? c : value.TryGetProperty("color", out var c2) ? c2 : default;
        if (colour.ValueKind == JsonValueKind.String)
        {
            if (FlagDesign.Normalise(colour.GetString()) is { } hex) emblem.Colour = hex;
            else fail($"colour '{colour.GetString()}' is not #rrggbb.");
        }
        else if (emblem.Sprite.Length > 0) emblem.Colour = "";
        float Number(string key, float fallback, float min, float max)
        {
            if (!value.TryGetProperty(key, out var field)) return fallback;
            if (field.ValueKind == JsonValueKind.Number && field.TryGetSingle(out var number) && float.IsFinite(number))
                return Math.Clamp(number, min, max);
            fail($"{key} is not a number.");
            return fallback;
        }
        emblem.X = Number("x", emblem.X, -0.5f, 1.5f);
        emblem.Y = Number("y", emblem.Y, -0.5f, 1.5f);
        emblem.Size = Number("size", emblem.Size, 0.01f, 2f);
        emblem.Rotation = Number("rotation", 0f, -360f, 360f);
        emblem.Count = (int)Number("count", 12, 3, 50);
        return emblem;
    }

    internal void Write(Utf8JsonWriter writer)
    {
        writer.WriteStartObject();
        if (Sprite.Length > 0) writer.WriteString("sprite", Sprite);
        else writer.WriteString("shape", Shape);
        if (Colour.Length > 0) writer.WriteString("colour", Colour);
        writer.WriteNumber("x", Math.Round(X, 4));
        writer.WriteNumber("y", Math.Round(Y, 4));
        writer.WriteNumber("size", Math.Round(Size, 4));
        if (Rotation != 0) writer.WriteNumber("rotation", Math.Round(Rotation, 2));
        if (Shape == "star_ring") writer.WriteNumber("count", Count);
        writer.WriteEndObject();
    }

    /// <summary>The colour at a point once this emblem is laid over what is beneath it.</summary>
    internal (float R, float G, float B) Over((float R, float G, float B) under, float u, float v, float ratio,
        (float R, float G, float B) colour, FlagRaster sprite)
    {
        // Into the emblem's own space: centred, rotated, scaled so its outer radius is 1.
        var radius = Size / 2f;
        if (radius <= 0) return under;
        var dx = (u - X * ratio) / radius;
        var dy = (v - Y) / radius;
        if (Rotation != 0)
        {
            var angle = -Rotation * MathF.PI / 180f;
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            (dx, dy) = (dx * cos - dy * sin, dx * sin + dy * cos);
        }
        if (MathF.Abs(dx) > 1.05f || MathF.Abs(dy) > 1.05f) return under;
        if (Sprite.Length > 0)
        {
            if (sprite is null) return under;
            // The picture fills the emblem's square, keeping its own proportions.
            var aspect = sprite.Width / (float)Math.Max(1, sprite.Height);
            var sx = aspect >= 1 ? dx : dx / aspect;
            var sy = aspect >= 1 ? dy * aspect : dy;
            if (MathF.Abs(sx) > 1 || MathF.Abs(sy) > 1) return under;
            var px = (sx + 1) / 2f * (sprite.Width - 1);
            var py = (sy + 1) / 2f * (sprite.Height - 1);
            var (r, g, b, a) = sprite.Get((int)MathF.Round(px), (int)MathF.Round(py));
            if (a <= 0) return under;
            // A colour turns the picture into a mask of that colour, which is what an icon
            // drawn white or gold on transparency is for.
            if (Colour.Length > 0) (r, g, b) = colour;
            return (under.R + (r - under.R) * a, under.G + (g - under.G) * a, under.B + (b - under.B) * a);
        }
        return Inside(dx, dy) ? colour : under;
    }

    private bool Inside(float x, float y)
    {
        var distance = MathF.Sqrt(x * x + y * y);
        switch (Shape)
        {
            case "disc": return distance <= 1f;
            case "ring": return distance <= 1f && distance >= 0.72f;
            case "diamond": return MathF.Abs(x) + MathF.Abs(y) <= 1f;
            // Point up, base across the lower quarter: the apex at the top of the emblem's square.
            case "triangle": return y <= 0.75f && MathF.Abs(x) <= (y + 1f) / 1.75f;
            case "cross": return (MathF.Abs(x) <= 0.25f && MathF.Abs(y) <= 1f) || (MathF.Abs(y) <= 0.25f && MathF.Abs(x) <= 1f);
            case "star": return Star(x, y, 5, 1f, 0.382f);
            case "eight_point_star": return Star(x, y, 8, 1f, 0.55f);
            case "sun":
                return distance <= 0.55f || Star(x, y, 16, 1f, 0.6f);
            case "crescent":
                return distance <= 1f && MathF.Sqrt((x - 0.32f) * (x - 0.32f) + y * y) > 0.8f;
            case "crescent_star":
                return (distance <= 1f && MathF.Sqrt((x - 0.32f) * (x - 0.32f) + y * y) > 0.8f) ||
                       Star((x - 0.55f) / 0.32f, y / 0.32f, 5, 1f, 0.382f);
            case "star_ring":
            {
                // Stars on a circle, upright, as the European flag sets them.
                var small = 0.16f;
                for (var i = 0; i < Count; i++)
                {
                    var angle = i * 2f * MathF.PI / Count;
                    var cx = MathF.Sin(angle) * 0.8f;
                    var cy = -MathF.Cos(angle) * 0.8f;
                    if (MathF.Abs(x - cx) <= small && MathF.Abs(y - cy) <= small &&
                        Star((x - cx) / small, (y - cy) / small, 5, 1f, 0.382f)) return true;
                }
                return false;
            }
            default: return false;
        }
    }

    /// <summary>Whether a point is inside a star with one point straight up.</summary>
    private static bool Star(float x, float y, int points, float outer, float inner)
    {
        var distance = MathF.Sqrt(x * x + y * y);
        if (distance > outer) return false;
        if (distance <= inner * MathF.Cos(MathF.PI / points)) return true;
        // Angle measured clockwise from straight up, folded into one point's wedge.
        var angle = MathF.Atan2(x, -y);
        var wedge = 2f * MathF.PI / points;
        var local = ((angle % wedge) + wedge) % wedge;
        if (local > wedge / 2f) local = wedge - local;
        // The edge from the tip (angle 0, outer) to the notch (angle wedge/2, inner).
        var tip = (0f, outer);
        var notch = (MathF.Sin(wedge / 2f) * inner, MathF.Cos(wedge / 2f) * inner);
        var px = MathF.Sin(local) * distance;
        var py = MathF.Cos(local) * distance;
        // Inside when on the centre's side of that edge.
        var cross = (notch.Item1 - tip.Item1) * (py - tip.Item2) - (notch.Item2 - tip.Item2) * (px - tip.Item1);
        var centre = (notch.Item1 - tip.Item1) * (0 - tip.Item2) - (notch.Item2 - tip.Item2) * (0 - tip.Item1);
        return MathF.Sign(cross) == MathF.Sign(centre) || cross == 0;
    }
}

/// <summary>Pixels, RGBA, 0..1, row by row: what a flag design draws into and the game turns into a texture.</summary>
public sealed class FlagRaster
{
    public FlagRaster(int width, int height)
    {
        Width = width;
        Height = height;
        Pixels = new byte[width * height * 4];
    }

    public FlagRaster(int width, int height, byte[] rgba)
    {
        Width = width;
        Height = height;
        Pixels = rgba ?? throw new ArgumentNullException(nameof(rgba));
        if (rgba.Length != width * height * 4) throw new ArgumentException("RGBA buffer does not match its size.");
    }

    public int Width { get; }
    public int Height { get; }

    /// <summary>RGBA8, the layout Godot's Image.CreateFromData takes for Format.Rgba8.</summary>
    public byte[] Pixels { get; }

    public void Set(int x, int y, float r, float g, float b, float a)
    {
        var at = (y * Width + x) * 4;
        Pixels[at] = Byte(r);
        Pixels[at + 1] = Byte(g);
        Pixels[at + 2] = Byte(b);
        Pixels[at + 3] = Byte(a);
    }

    public (float R, float G, float B, float A) Get(int x, int y)
    {
        x = Math.Clamp(x, 0, Width - 1);
        y = Math.Clamp(y, 0, Height - 1);
        var at = (y * Width + x) * 4;
        return (Pixels[at] / 255f, Pixels[at + 1] / 255f, Pixels[at + 2] / 255f, Pixels[at + 3] / 255f);
    }

    /// <summary>The pixel as "#rrggbb", for tests and tooltips.</summary>
    public string Hex(int x, int y)
    {
        var at = (Math.Clamp(y, 0, Height - 1) * Width + Math.Clamp(x, 0, Width - 1)) * 4;
        return $"#{Pixels[at]:x2}{Pixels[at + 1]:x2}{Pixels[at + 2]:x2}";
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
}

/// <summary>
/// Every flag design the base game and the loaded mods declare, by sprite name.
///
/// Layered like every catalogue in common/: the base game's folder first, each mod's after it
/// in load order, a later design of the same id replacing the earlier one whole - a design is
/// one picture, and half of one mixed with half of another is nobody's flag.
/// </summary>
public static class FlagDesignCatalog
{
    /// <summary>What a design's sprite path starts with, so the interface knows to draw it rather than load it.</summary>
    public const string TexturePrefix = "flagdesign://";

    private static readonly Dictionary<string, FlagDesign> Designs = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Raised when a design is added or the catalogue is rebuilt, so drawn textures can be dropped.</summary>
    public static event Action Changed;

    static FlagDesignCatalog() => ResetCatalog();

    public static IReadOnlyCollection<FlagDesign> All => Designs.Values;

    public static bool Has(string id) => !string.IsNullOrEmpty(id) && Designs.ContainsKey(id);

    public static FlagDesign Find(string id) =>
        !string.IsNullOrEmpty(id) && Designs.TryGetValue(id, out var design) ? design : null;

    public static void ResetCatalog(List<string> errors = null)
    {
        Designs.Clear();
        foreach (var document in GameDataLoader.DataDocuments("common", "flag_designs"))
            using (document)
                Merge(document.RootElement, errors);
        Changed?.Invoke();
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        foreach (var design in Read(root, errors)) Designs[design.Id] = design;
        Changed?.Invoke();
    }

    /// <summary>Put one design in place now, as the editor does after saving it.</summary>
    public static void Put(FlagDesign design)
    {
        if (design is null || string.IsNullOrWhiteSpace(design.Id)) return;
        Designs[design.Id] = design.Clone();
        Changed?.Invoke();
    }

    /// <summary>The designs in one document, without touching the catalogue - for validation and tests.</summary>
    public static List<FlagDesign> Read(JsonElement root, List<string> errors = null)
    {
        var found = new List<FlagDesign>();
        if (root.ValueKind != JsonValueKind.Object)
        {
            errors?.Add("common/flag_designs: expected an object of designs.");
            return found;
        }
        var body = root.TryGetProperty("flag_designs", out var inner) && inner.ValueKind == JsonValueKind.Object ? inner : root;
        foreach (var entry in body.EnumerateObject())
        {
            if (!IsValidId(entry.Name))
            {
                errors?.Add($"common/flag_designs: '{entry.Name}' is not a valid id - use lower-case letters, digits and _ , starting with flag_.");
                continue;
            }
            if (FlagDesign.Parse(entry.Name, entry.Value, errors) is { } design) found.Add(design);
        }
        return found;
    }

    /// <summary>
    /// A design id is a sprite name, and every flag sprite starts with `flag_`; held to the same
    /// characters as a file name the game would accept, since a design may be exported as one.
    /// </summary>
    public static bool IsValidId(string id) =>
        !string.IsNullOrEmpty(id) && id.Length <= 96 && id.StartsWith("flag_", StringComparison.Ordinal) &&
        id.All(character => character is >= 'a' and <= 'z' or >= '0' and <= '9' or '_');
}
