using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// What a panel is made of, as data: its colours, its bevel, shadow and grain, an optional
/// nine-slice picture, and its content margins. A panel names a ROLE - `plate`, `well`,
/// `button.primary` - never a colour, so a skin or an era overlay that changes what a plate is
/// changes every plate at once.
///
/// Read from the `lighting` and `materials` sections of any `interface/` or `gui/` file, merged
/// field by field in load order: a later file that writes one field of a material keeps the
/// rest. Engine-free on purpose, so the bench can load a mod's materials and check them without
/// a renderer; <c>MaterialStyles</c> is the only place that turns a material into a StyleBox, and
/// it does so by asking <see cref="Bake"/> here for the pixels.
///
/// Nothing a mod writes can produce an invalid surface: numbers are clamped to safe ranges,
/// non-finite values fall back to the default, unknown fields are reported and ignored, and an
/// unknown role resolves to the base `plate` rather than to the loud magenta an unknown colour
/// token gets - a missing material should be plain, not alarming.
/// </summary>
public static class InterfaceMaterials
{
    public enum SurfaceState { Normal, Hover, Pressed, Disabled }

    public sealed record LightingModel(float DirectionX, float DirectionY, float Highlight, float Shade,
        float Grain, float InnerShadowAlpha, float DropShadowAlpha);

    public sealed record Material(string Role, string Top, string Bottom, string Border, string Engrave,
        int EngraveInset, int Bevel, int CornerRadius, int BorderWidth, int DropShadow, int InnerShadow,
        float Grain, int Notches, bool Inset, string Shape, string Texture,
        IReadOnlyList<int> TextureMargin, IReadOnlyList<int> ContentMargin);

    public const string BaseRole = "plate";

    /// <summary>The side of the square every code-drawn material is baked into, before its shadow.</summary>
    public const int BakeSize = 48;

    /// <summary>The budget the factory keeps to: baked surfaces are few and shared, never per panel.</summary>
    public const int MaxBakedTextures = 64;
    public const long MaxBakedBytes = 2L * 1024 * 1024;

    private static readonly string[] Shapes = { "rect", "circle", "shield" };

    // name -> (minimum, maximum, default). The ranges are the safe envelope from the build plan.
    private static readonly Dictionary<string, (float Min, float Max, float Default)> NumberFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["engrave_inset"] = (0, 12, 4),
            ["bevel"] = (0, 4, 1),
            ["corner_radius"] = (0, 12, 3),
            ["border_width"] = (0, 4, 1),
            ["drop_shadow"] = (0, 24, 6),
            ["inner_shadow"] = (0, 24, 0),
            ["grain"] = (0, 2, 1),
            ["notches"] = (0, 12, 0),
        };

    private static readonly Dictionary<string, string> ColourFields = new(StringComparer.OrdinalIgnoreCase)
    {
        ["top"] = "#2a2520", ["bottom"] = "#1c1916", ["border"] = "#0e0c0a", ["engrave"] = "#3a332b",
    };

    private static readonly Dictionary<string, (float Min, float Max, float Default)> LightingFields =
        new(StringComparer.OrdinalIgnoreCase)
        {
            ["highlight"] = (0, .5f, .10f),
            ["shade"] = (0, .5f, .18f),
            ["grain"] = (0, .05f, .018f),
            ["inner_shadow_alpha"] = (0, 1, .35f),
            ["drop_shadow_alpha"] = (0, 1, .45f),
        };

    private const float DefaultDirectionX = -.7f, DefaultDirectionY = -.7f;

    private static readonly Dictionary<string, Dictionary<string, object>> Raw = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, object> RawLighting = new(StringComparer.OrdinalIgnoreCase);
    private static readonly Dictionary<string, Material> Resolved = new(StringComparer.OrdinalIgnoreCase);

    /// <summary>Forget every material and the lighting. <see cref="InterfaceSkin.ResetCatalog"/> calls this.</summary>
    public static void Reset()
    {
        Raw.Clear();
        RawLighting.Clear();
        Resolved.Clear();
    }

    /// <summary>
    /// Drop the finished materials but keep what the files said. A resolved material has the
    /// button and nine-slice metrics baked into it, so a metrics change - from a skin file, an
    /// overlay or a mod - has to call this or buttons keep the old corners until something else
    /// happens to clear the cache.
    /// </summary>
    internal static void ForgetResolved() => Resolved.Clear();

    /// <summary>Every role a file has declared, in name order.</summary>
    public static IEnumerable<string> Roles => Raw.Keys.OrderBy(key => key, StringComparer.Ordinal);

    public static bool HasRole(string role) => role is not null && Raw.ContainsKey(role);

    // ------------------------------------------------------------------------- reading --

    public static void MergeLighting(JsonElement element, string context, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{context}: 'lighting' must be an object.");
            return;
        }
        foreach (var entry in element.EnumerateObject())
        {
            if (IsComment(entry.Name)) continue;
            if (entry.NameEquals("direction"))
            {
                var pair = Floats(entry.Value, 2);
                if (pair is null) errors?.Add($"{context}: lighting.direction must be two numbers, such as [-0.7, -0.7].");
                else RawLighting["direction"] = pair;
                continue;
            }
            if (!LightingFields.ContainsKey(entry.Name))
            {
                errors?.Add($"{context}: lighting has no field '{entry.Name}'; ignored.");
                continue;
            }
            if (entry.Value.ValueKind != JsonValueKind.Number)
            {
                errors?.Add($"{context}: lighting.{entry.Name} must be a number.");
                continue;
            }
            RawLighting[entry.Name] = entry.Value.GetDouble();
        }
        Resolved.Clear();
    }

    public static void MergeMaterials(JsonElement element, string context, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{context}: 'materials' must be an object of roles.");
            return;
        }
        foreach (var role in element.EnumerateObject())
        {
            if (IsComment(role.Name)) continue;
            if (role.Value.ValueKind != JsonValueKind.Object)
            {
                errors?.Add($"{context}: material '{role.Name}' must be an object.");
                continue;
            }
            if (!Raw.TryGetValue(role.Name, out var fields))
                Raw[role.Name] = fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);

            foreach (var field in role.Value.EnumerateObject())
            {
                if (IsComment(field.Name)) continue;
                var name = field.Name;
                var value = field.Value;
                if (NumberFields.ContainsKey(name))
                {
                    if (value.ValueKind == JsonValueKind.Number) fields[name] = value.GetDouble();
                    else errors?.Add($"{context}: material '{role.Name}'.{name} must be a number.");
                }
                else if (ColourFields.ContainsKey(name) || name is "inherits" or "texture" or "shape")
                {
                    if (value.ValueKind != JsonValueKind.String)
                    {
                        errors?.Add($"{context}: material '{role.Name}'.{name} must be a string.");
                        continue;
                    }
                    var text = value.GetString()?.Trim() ?? "";
                    if (name == "shape" && !Shapes.Contains(text, StringComparer.OrdinalIgnoreCase))
                    {
                        errors?.Add($"{context}: material '{role.Name}' shape '{text}' is not rect, circle or shield; rect is used.");
                        text = "rect";
                    }
                    fields[name] = text;
                }
                else if (name == "inset")
                {
                    if (value.ValueKind is JsonValueKind.True or JsonValueKind.False) fields[name] = value.GetBoolean();
                    else errors?.Add($"{context}: material '{role.Name}'.inset must be true or false.");
                }
                else if (name is "texture_margin" or "content_margin")
                {
                    var four = Floats(value, 4);
                    if (four is null) errors?.Add($"{context}: material '{role.Name}'.{name} must be four numbers: left, top, right, bottom.");
                    else fields[name] = four;
                }
                else errors?.Add($"{context}: material '{role.Name}' has no field '{name}'; ignored.");
            }
        }
        Resolved.Clear();
    }

    /// <summary>
    /// Set one number directly, the way a file would. For the bench and the console: JSON cannot
    /// spell NaN, but a script or a binary mod can hand one over.
    /// </summary>
    public static void Override(string role, string field, double value)
    {
        if (string.IsNullOrWhiteSpace(role) || !NumberFields.ContainsKey(field ?? "")) return;
        if (!Raw.TryGetValue(role, out var fields))
            Raw[role] = fields = new Dictionary<string, object>(StringComparer.OrdinalIgnoreCase);
        fields[field] = value;
        Resolved.Clear();
    }

    // ----------------------------------------------------------------------- resolving --

    /// <summary>The light every material and the relief shader share, clamped and normalised.</summary>
    public static LightingModel Lighting
    {
        get
        {
            float Read(string name)
            {
                var (min, max, fallback) = LightingFields[name];
                return RawLighting.TryGetValue(name, out var value) ? Clamp((double)value, min, max, fallback) : fallback;
            }
            var (x, y) = LightDirection(RawLighting.TryGetValue("direction", out var d) ? (float[])d : null);
            return new LightingModel(x, y, Read("highlight"), Read("shade"), Read("grain"),
                Read("inner_shadow_alpha"), Read("drop_shadow_alpha"));
        }
    }

    /// <summary>
    /// Where the light comes FROM, in screen terms (x right, y down), as a unit vector. A zero,
    /// non-finite or missing direction is the shipped upper-left light.
    /// </summary>
    public static (float X, float Y) LightDirection(float[] direction)
    {
        if (direction is not { Length: 2 } || !float.IsFinite(direction[0]) || !float.IsFinite(direction[1]))
            return Normalise(DefaultDirectionX, DefaultDirectionY);
        var length = MathF.Sqrt(direction[0] * direction[0] + direction[1] * direction[1]);
        return length < 1e-4f ? Normalise(DefaultDirectionX, DefaultDirectionY) : Normalise(direction[0], direction[1]);
    }

    private static (float, float) Normalise(float x, float y)
    {
        var length = MathF.Sqrt(x * x + y * y);
        return (x / length, y / length);
    }

    /// <summary>
    /// A role's material with every inherited field filled in. An unknown role is the base
    /// <c>plate</c>. Two metrics that were declared and never read become defaults here:
    /// <c>button.corner_radius</c> and <c>button.border_width</c> for the button family, and
    /// <c>panel.texture_margin</c> for every nine-slice.
    /// </summary>
    public static Material Resolve(string role)
    {
        if (string.IsNullOrWhiteSpace(role) || !Raw.ContainsKey(role)) role = BaseRole;
        if (Resolved.TryGetValue(role, out var cached)) return cached;

        var chain = new List<string>();
        var visited = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var current = role; current is not null && Raw.TryGetValue(current, out var fields) && visited.Add(current) && chain.Count < 16;)
        {
            chain.Insert(0, current);
            current = fields.TryGetValue("inherits", out var parent) ? (string)parent : null;
        }

        var numbers = NumberFields.ToDictionary(pair => pair.Key, pair => (double)pair.Value.Default, StringComparer.OrdinalIgnoreCase);
        var strings = new Dictionary<string, string>(ColourFields, StringComparer.OrdinalIgnoreCase)
            { ["texture"] = "", ["shape"] = "rect" };
        var inset = false;
        var textureMargin = Enumerable.Repeat(InterfaceSkin.Metric("panel.texture_margin", 16f), 4).ToArray();
        var contentMargin = new[] { 10f, 8f, 10f, 8f };

        foreach (var link in chain)
        {
            if (string.Equals(link, "button", StringComparison.OrdinalIgnoreCase))
            {
                numbers["corner_radius"] = InterfaceSkin.Metric("button.corner_radius", 3f);
                numbers["border_width"] = InterfaceSkin.Metric("button.border_width", 1f);
            }
            foreach (var (name, value) in Raw[link])
                switch (value)
                {
                    case double number when NumberFields.ContainsKey(name): numbers[name] = number; break;
                    case string text when name != "inherits": strings[name] = text; break;
                    case bool flag: inset = flag; break;
                    case float[] four when name == "texture_margin": textureMargin = four; break;
                    case float[] four when name == "content_margin": contentMargin = four; break;
                }
        }

        int Whole(string name)
        {
            var (min, max, fallback) = NumberFields[name];
            return (int)MathF.Round(Clamp(numbers[name], min, max, fallback));
        }
        static IReadOnlyList<int> Margins(float[] values, int max, int fallback) =>
            values.Select(v => (int)MathF.Round(Clamp(v, 0, max, fallback))).ToArray();

        var material = new Material(role,
            strings["top"], strings["bottom"], strings["border"], strings["engrave"],
            Whole("engrave_inset"), Whole("bevel"), Whole("corner_radius"), Whole("border_width"),
            Whole("drop_shadow"), Whole("inner_shadow"),
            Clamp(numbers["grain"], 0, 2, 1), Whole("notches"), inset,
            strings["shape"].ToLowerInvariant(), strings["texture"],
            Margins(textureMargin, 128, 16), Margins(contentMargin, 64, 8));
        Resolved[role] = material;
        return material;
    }

    // NaN means nothing and gets the default; an infinity means "as far as allowed" and gets the bound.
    private static float Clamp(double value, float min, float max, float fallback) =>
        double.IsNaN(value) ? fallback : (float)Math.Clamp(value, min, max);

    // ------------------------------------------------------------------------- colours --

    /// <summary>A material colour field as #rrggbb[aa]: a literal, a skin token, or the plate default.</summary>
    public static string ColourOf(string tokenOrHex, string fallbackHex)
    {
        if (TryParseHex(tokenOrHex, out _)) return tokenOrHex;
        return InterfaceSkin.Has(tokenOrHex) ? InterfaceSkin.Colour(tokenOrHex) : fallbackHex;
    }

    public static bool TryParseHex(string value, out (float R, float G, float B, float A) colour)
    {
        colour = default;
        if (string.IsNullOrEmpty(value) || value[0] != '#') return false;
        var digits = value[1..];
        if (digits.Length is 3 or 4) digits = string.Concat(digits.Select(c => new string(c, 2)));
        if (digits.Length == 6) digits += "ff";
        if (digits.Length != 8 || !digits.All(Uri.IsHexDigit)) return false;
        float Channel(int index) => int.Parse(digits.Substring(index * 2, 2), NumberStyles.HexNumber) / 255f;
        colour = (Channel(0), Channel(1), Channel(2), Channel(3));
        return true;
    }

    private static (float R, float G, float B, float A) Rgba(string tokenOrHex, string fallbackHex)
    {
        TryParseHex(ColourOf(tokenOrHex, fallbackHex), out var colour);
        return colour;
    }

    // -------------------------------------------------------------------------- baking --

    /// <summary>How many pixels of shadow surround the face, and the side of the baked square.</summary>
    public static (int Pad, int Size) BakedExtent(Material material)
    {
        var pad = material.DropShadow;
        return (pad, BakeSize + 2 * pad);
    }

    public static long BakedBytes(Material material)
    {
        var (_, size) = BakedExtent(material);
        return (long)size * size * 4;
    }

    /// <summary>
    /// The nine-slice margin of a baked material, shadow included: everything that must not
    /// stretch - the corner, the notch, the engraved line, the bevel, the inner shadow.
    /// </summary>
    public static int BakedMargin(Material material)
    {
        var (pad, _) = BakedExtent(material);
        if (material.Shape != "rect") return pad + BakeSize / 2;
        var detail = new[] { material.CornerRadius, material.Notches, material.EngraveInset + 2,
            material.BorderWidth + material.Bevel, material.InnerShadow }.Max();
        return pad + Math.Min(BakeSize / 2 - 1, detail + 2);
    }

    /// <summary>
    /// The lit amount at a point of a face, from the shared light: positive toward the light,
    /// negative away from it, zero in the middle. Used by the CPU bake; the relief shader gets the
    /// same direction as its <c>light_dir</c> uniform.
    /// </summary>
    public static float Directional(float u, float v, float directionX, float directionY)
    {
        u = float.IsFinite(u) ? Math.Clamp(u, 0, 1) : .5f;
        v = float.IsFinite(v) ? Math.Clamp(v, 0, 1) : .5f;
        return (u - .5f) * directionX + (v - .5f) * directionY;
    }

    /// <summary>
    /// The finished pixels of a material, RGBA8, row by row: gradient, light, grain, inner
    /// shadow, bevel, engraved line, border, shape and drop shadow, all in one square. Computed
    /// once per role and state and cached by the factory; nothing here runs per frame, reads a
    /// clock or draws from a random stream.
    /// </summary>
    public static byte[] Bake(Material material, SurfaceState state, out int size, out int pad)
    {
        (pad, size) = BakedExtent(material);
        var light = Lighting;
        var top = Rgba(material.Top, ColourFields["top"]);
        var bottom = Rgba(material.Bottom, ColourFields["bottom"]);
        var border = Rgba(material.Border, ColourFields["border"]);
        var engrave = Rgba(material.Engrave, ColourFields["engrave"]);
        var sunk = material.Inset ^ (state == SurfaceState.Pressed);
        var relief = state == SurfaceState.Disabled ? .25f : 1f;
        var lift = state == SurfaceState.Hover ? .05f : 0f;
        var pixels = new byte[size * size * 4];

        for (var y = 0; y < size; y++)
        for (var x = 0; x < size; x++)
        {
            var px = x + .5f - pad;
            var py = y + .5f - pad;
            var sd = Distance(material, px, py);
            var coverage = Math.Clamp(.5f - sd, 0, 1);

            // The drop shadow: the same shape a third of its depth lower, fading out.
            var shadow = 0f;
            if (pad > 0)
            {
                var below = Distance(material, px, py - pad * .35f);
                var fade = Math.Clamp(1 - Math.Max(below, 0) / pad, 0, 1);
                shadow = light.DropShadowAlpha * fade * fade;
            }

            (float R, float G, float B) colour = (0, 0, 0);
            if (coverage > 0)
            {
                var u = Math.Clamp(px / BakeSize, 0, 1);
                var v = Math.Clamp(py / BakeSize, 0, 1);
                colour = (Lerp(top.R, bottom.R, v), Lerp(top.G, bottom.G, v), Lerp(top.B, bottom.B, v));
                if (state == SurfaceState.Disabled)
                {
                    var grey = (colour.R + colour.G + colour.B) / 3f;
                    colour = (Lerp(colour.R, grey, .5f), Lerp(colour.G, grey, .5f), Lerp(colour.B, grey, .5f));
                }
                var lit = Directional(u, v, light.DirectionX, light.DirectionY) * (sunk ? -1 : 1) * relief;
                var illumination = (lit >= 0 ? lit * light.Highlight : lit * light.Shade) + lift;
                illumination += AtlasSurfaceLighting.Grain(x, y) * light.Grain * material.Grain * 1.5f;
                colour = Shade(colour, illumination);

                var depth = -sd;
                var inner = sunk && material.InnerShadow == 0 ? 3 : material.InnerShadow;
                if (inner > 0 && depth < inner)
                {
                    var t = 1 - depth / inner;
                    colour = Shade(colour, -light.InnerShadowAlpha * t * t);
                }

                var bevelStart = material.BorderWidth;
                if (material.Bevel > 0 && depth >= bevelStart && depth < bevelStart + material.Bevel)
                {
                    var (nx, ny) = Normal(material, px, py);
                    var facing = (nx * light.DirectionX + ny * light.DirectionY) * (sunk ? -1 : 1) * relief;
                    colour = Shade(colour, facing >= 0 ? facing * light.Highlight * 2.5f : facing * light.Shade * 2.5f);
                }

                if (material.EngraveInset > 0)
                {
                    var line = material.BorderWidth + material.EngraveInset;
                    if (MathF.Abs(depth - (line + .5f)) < .5f)
                        colour = Mix(colour, engrave, .85f);
                    else if (MathF.Abs(depth - (line + 1.5f)) < .5f)
                        colour = Shade(colour, light.Highlight * 1.2f * relief);
                }

                if (depth < material.BorderWidth) colour = (border.R, border.G, border.B);
            }

            // Face over shadow; the shadow is black at its own alpha.
            var alpha = coverage + shadow * (1 - coverage);
            var face = alpha > 0 ? coverage / alpha : 0;
            var index = (y * size + x) * 4;
            pixels[index] = Byte(colour.R * face);
            pixels[index + 1] = Byte(colour.G * face);
            pixels[index + 2] = Byte(colour.B * face);
            pixels[index + 3] = Byte(alpha);
        }
        return pixels;
    }

    private static byte Byte(float value) => (byte)Math.Clamp((int)MathF.Round(value * 255f), 0, 255);
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

    private static (float R, float G, float B) Shade((float R, float G, float B) c, float amount) => amount >= 0
        ? (Lerp(c.R, 1, amount), Lerp(c.G, 1, amount), Lerp(c.B, 1, amount))
        : (Lerp(c.R, 0, -amount), Lerp(c.G, 0, -amount), Lerp(c.B, 0, -amount));

    private static (float R, float G, float B) Mix((float R, float G, float B) c, (float R, float G, float B, float A) ink, float t) =>
        (Lerp(c.R, ink.R, t * ink.A), Lerp(c.G, ink.G, t * ink.A), Lerp(c.B, ink.B, t * ink.A));

    /// <summary>Signed distance to the material's outline in the 48-pixel face: negative inside.</summary>
    public static float Distance(Material material, float px, float py)
    {
        const float half = BakeSize / 2f;
        var dx = px - half;
        var dy = py - half;
        if (material.Shape == "circle") return MathF.Sqrt(dx * dx + dy * dy) - half;

        var radius = Math.Min(material.CornerRadius, half - 1);
        var qx = MathF.Abs(dx) - (half - radius);
        var qy = MathF.Abs(dy) - (half - radius);
        var outside = MathF.Sqrt(MathF.Max(qx, 0) * MathF.Max(qx, 0) + MathF.Max(qy, 0) * MathF.Max(qy, 0));
        var distance = outside + MathF.Min(MathF.Max(qx, qy), 0) - radius;

        if (material.Notches > 0)
            distance = MathF.Max(distance, (MathF.Abs(dx) + MathF.Abs(dy) - (2 * half - material.Notches)) * .70710678f);

        if (material.Shape == "shield")
        {
            // Flat top, straight sides to 55 %, then two edges meeting in a point at the bottom.
            var (nx, ny) = Normalise(-.45f * BakeSize, half);
            var left = px * nx + (py - .55f * BakeSize) * ny;
            var right = (BakeSize - px) * nx + (py - .55f * BakeSize) * ny;
            distance = MathF.Max(distance, MathF.Max(left, right));
        }
        return distance;
    }

    private static (float X, float Y) Normal(Material material, float px, float py)
    {
        var gx = Distance(material, px + .5f, py) - Distance(material, px - .5f, py);
        var gy = Distance(material, px, py + .5f) - Distance(material, px, py - .5f);
        var length = MathF.Sqrt(gx * gx + gy * gy);
        return length < 1e-5f ? (0, 0) : (gx / length, gy / length);
    }

    // ------------------------------------------------------------------------- helpers --

    private static float[] Floats(JsonElement element, int count)
    {
        if (element.ValueKind != JsonValueKind.Array) return null;
        var values = new List<float>();
        foreach (var item in element.EnumerateArray())
        {
            if (item.ValueKind != JsonValueKind.Number) return null;
            values.Add((float)item.GetDouble());
        }
        return values.Count == count ? values.ToArray() : null;
    }

    private static bool IsComment(string name) =>
        string.IsNullOrEmpty(name) || name.StartsWith("//", StringComparison.Ordinal) || name[0] == '#';
}
