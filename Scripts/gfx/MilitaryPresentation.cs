using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private Node3D _battleRoot = null!;
    private readonly Dictionary<int, (Node3D Node, Vector3 Normal)> _battleMarkers = new();
    private ProvinceTopology _battleAnchorMap;
    private ProvinceBoundaryAnchor _battleAnchors;

    private void CreateMilitaryOverlays(Node3D world)
    {
        _battleRoot = new Node3D { Name = "BattleMarkers" };
        world.AddChild(_battleRoot);
    }

    /// <summary>
    /// The plate a stacked formation wears: a framed card in the nation's colours with the
    /// unit type drawn NATO-style in the symbol box.
    ///
    /// Everything about the old version was tuned at texture scale and then displayed at a
    /// sixth of it, so the near-black fill became a black smear and the two-pixel symbol
    /// strokes vanished into nothing - which is why the counters on a live map read as
    /// unreadable dark plates rather than as unit cards. The colours here are what they
    /// become on screen: the fill is slate that a border can stand out from, and the symbol
    /// strokes are thick enough to survive the shrink.
    ///
    /// The five catalogue symbols each get their own shape now. Artillery used to fall
    /// through to the infantry X, so the formations players most need to spot - the ones
    /// shooting from behind the front - were invisible at a glance.
    /// </summary>
    private Texture2D ArmyCounterTexture(Nation nation, string templateId)
    {
        var key = (nation.MapIndex, templateId ?? "line_infantry");
        if (_armyCounterTextures.TryGetValue(key, out var cached)) return cached;
        const int width = 176;
        const int height = 104;
        // Drawn three times larger and brought down with a Lanczos filter. The card used to be
        // painted pixel by pixel at its own size, so every diagonal of the symbol and every
        // corner of the frame was a staircase once the card stood on the map.
        const int s = 3;
        var w = width * s;
        var h = height * s;
        var pixels = new byte[w * h * 4];
        var national = new Color(nation.Color);
        var rim = national.Lightened(0.30f);
        var ink = new Color(0.035f, 0.045f, 0.055f, 0.98f);
        var top = new Color(0.205f, 0.245f, 0.280f, 0.97f);
        var bottom = new Color(0.105f, 0.130f, 0.155f, 0.97f);
        const float radius = 7f * s;
        // The rim is the counter's nationality: it has to read as a coloured frame at the size a
        // card stands on the map, so it is wide, and the dark line that parts it from the
        // ground is a hair.
        const float inkLine = 0.9f * s;
        const float rimLine = 7.5f * s;
        // A card stands on the map at about a third of its own size, so its nationality has to
        // be broad strokes to survive: the rim, a band across the top and the stripe down the
        // left are what read as "whose" from across a front.
        const float bandBottom = 21f * s;

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inward = -RoundedBox(x + 0.5f, y + 0.5f, w, h, radius);
            if (inward < 0f)
            {
                // Transparent, but in the ink's colour, so the filter does not drag a pale
                // fringe in from outside the card.
                WritePixel(pixels, w, x, y, new Color(ink.R, ink.G, ink.B, 0f));
                continue;
            }
            Color color;
            if (inward < inkLine) color = ink;
            else if (inward < rimLine) color = rim;
            else
            {
                color = top.Lerp(bottom, y / (float)h);
                if (y < bandBottom) color = national.Darkened(0.12f);
                // A hairline of light under the band, the way a printed counter catches it, and
                // the national stripe down the left.
                else if (y < bandBottom + 1.4f * s) color = color.Lightened(0.16f);
                if (x >= 9 * s && x < 18 * s && y >= bandBottom && y < h - 9 * s) color = national.Lightened(0.06f);
            }
            WritePixel(pixels, w, x, y, color);
        }

        // The unit symbol, NATO's, in the square left of the strength number - kept wholly
        // clear of the number's field in the middle of the card, where the marker is anchored.
        // Without the symbology's own frame around it: at the size a card stands on the map a
        // framed cross reads as an envelope, and the card is the frame.
        var symbol = new Color(0.92f, 0.95f, 0.97f, 0.96f);
        const float cx = 37f * s, cy = 54f * s, halfW = 17f * s, halfH = 15f * s, stroke = 6.5f * s;
        var counterSymbol = UnitTypeCatalog.Get(templateId).CounterSymbol ?? "";
        if (!PaintSymbolArt(pixels, w, counterSymbol, cx, cy, halfW + stroke / 2f, halfH + stroke / 2f, symbol))
            switch (counterSymbol)
            {
                case "armor":
                    StrokeEllipse(pixels, w, cx, cy, halfW, halfH * 0.66f, stroke * 0.8f, symbol);
                    break;
                case "artillery":
                    // The dot is the whole art of it: heavy, filled, unmistakable at a glance - the
                    // one symbol on the map that is solid, because guns do not manoeuvre.
                    FillDisc(pixels, w, cx, cy, halfH * 0.78f, symbol);
                    break;
                case "mechanized":
                    StrokeEllipse(pixels, w, cx, cy, halfW, halfH * 0.66f, stroke * 0.7f, symbol);
                    StrokeLine(pixels, w, cx - halfW * 0.62f, cy - halfH * 0.62f, cx + halfW * 0.62f, cy + halfH * 0.62f, stroke * 0.7f, symbol);
                    StrokeLine(pixels, w, cx - halfW * 0.62f, cy + halfH * 0.62f, cx + halfW * 0.62f, cy - halfH * 0.62f, stroke * 0.7f, symbol);
                    break;
                case "motorized":
                    // The cross on wheels: two of them under it, apart from it, so the mark still
                    // reads as infantry first.
                    StrokeLine(pixels, w, cx - halfW, cy - halfH, cx + halfW, cy + halfH * 0.45f, stroke, symbol);
                    StrokeLine(pixels, w, cx - halfW, cy + halfH * 0.45f, cx + halfW, cy - halfH, stroke, symbol);
                    FillDisc(pixels, w, cx - halfW * 0.55f, cy + halfH * 1.05f, stroke * 0.62f, symbol);
                    FillDisc(pixels, w, cx + halfW * 0.55f, cy + halfH * 1.05f, stroke * 0.62f, symbol);
                    break;
                default: // infantry: the cross from corner to corner of the frame
                    StrokeLine(pixels, w, cx - halfW, cy - halfH, cx + halfW, cy + halfH, stroke, symbol);
                    StrokeLine(pixels, w, cx - halfW, cy + halfH, cx + halfW, cy - halfH, stroke, symbol);
                    break;
            }

        var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, pixels);
        image.Resize(width, height, Image.Interpolation.Lanczos);
        var texture = ImageTexture.CreateFromImage(image);
        _armyCounterTextures[key] = texture;
        return texture;
    }

    /// <summary>
    /// A unit symbol a mod - or the game - drew instead of the built-in shapes: the sprite
    /// unit_[symbol] from any gfx/ folder, white on transparent, fitted inside the frame and
    /// painted in the symbol colour. A new kind of formation declares its counter_symbol in
    /// common/unit_types and ships a picture; nothing in code has to know it exists.
    /// </summary>
    private static bool PaintSymbolArt(byte[] pixels, int width, string symbol, float cx, float cy,
        float halfWidth, float halfHeight, Color color)
    {
        if (string.IsNullOrWhiteSpace(symbol)) return false;
        var name = "unit_" + symbol.ToLowerInvariant();
        if (!SpriteCatalog.Has(name)) return false;
        var path = SpriteCatalog.TextureOf(name);
        Image art = null;
        if (string.IsNullOrWhiteSpace(path)) return false;
        // Imported if Godot has seen it; read straight off the disk if it has not - a mod's
        // folder, or a picture dropped into the game's own gfx/ since the last import.
        var loose = path.StartsWith("res://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
        if (ResourceLoader.Exists(path)) art = GD.Load<Texture2D>(path)?.GetImage();
        else if (System.IO.File.Exists(loose)) art = Image.LoadFromFile(loose);
        if (art is null || art.IsEmpty()) return false;
        if (art.IsCompressed()) art.Decompress();
        art.Convert(Image.Format.Rgba8);
        var scale = MathF.Min(halfWidth * 2f / art.GetWidth(), halfHeight * 2f / art.GetHeight());
        var fitWidth = Math.Max(1, (int)(art.GetWidth() * scale));
        var fitHeight = Math.Max(1, (int)(art.GetHeight() * scale));
        art.Resize(fitWidth, fitHeight, Image.Interpolation.Lanczos);
        var left = (int)(cx - fitWidth / 2f);
        var top = (int)(cy - fitHeight / 2f);
        for (var y = 0; y < fitHeight; y++)
        for (var x = 0; x < fitWidth; x++)
        {
            var source = art.GetPixel(x, y);
            var cover = source.A * MathF.Max(source.R, MathF.Max(source.G, source.B));
            if (cover <= 0.004f) continue;
            BlendPixel(pixels, width, left + x, top + y, color, cover);
        }
        return true;
    }

    /// <summary>Signed distance from a point to a rounded rectangle filling the image: negative inside.</summary>
    private static float RoundedBox(float x, float y, int width, int height, float radius)
    {
        var qx = MathF.Abs(x - width / 2f) - (width / 2f - radius);
        var qy = MathF.Abs(y - height / 2f) - (height / 2f - radius);
        var outside = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
        return outside + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
    }

    private static void BlendPixel(byte[] pixels, int width, int x, int y, Color color, float cover)
    {
        if (x < 0 || y < 0 || x >= width) return;
        var index = (y * width + x) * 4;
        if (index < 0 || index + 3 >= pixels.Length) return;
        var a = Mathf.Clamp(cover * color.A, 0f, 1f);
        pixels[index] = (byte)Mathf.RoundToInt(pixels[index] * (1f - a) + color.R * 255f * a);
        pixels[index + 1] = (byte)Mathf.RoundToInt(pixels[index + 1] * (1f - a) + color.G * 255f * a);
        pixels[index + 2] = (byte)Mathf.RoundToInt(pixels[index + 2] * (1f - a) + color.B * 255f * a);
        pixels[index + 3] = (byte)Mathf.Clamp(Mathf.RoundToInt(pixels[index + 3] + (255 - pixels[index + 3]) * a), 0, 255);
    }

    /// <summary>A line of a given thickness, covered by distance, so its edges are smooth and its ends round.</summary>
    private static void StrokeLine(byte[] pixels, int width, float x0, float y0, float x1, float y1, float thickness, Color color)
    {
        var half = thickness / 2f;
        var minX = (int)MathF.Floor(MathF.Min(x0, x1) - half - 1);
        var maxX = (int)MathF.Ceiling(MathF.Max(x0, x1) + half + 1);
        var minY = (int)MathF.Floor(MathF.Min(y0, y1) - half - 1);
        var maxY = (int)MathF.Ceiling(MathF.Max(y0, y1) + half + 1);
        var dx = x1 - x0;
        var dy = y1 - y0;
        var length = MathF.Max(0.0001f, dx * dx + dy * dy);
        for (var y = minY; y <= maxY; y++)
        for (var x = minX; x <= maxX; x++)
        {
            var px = x + 0.5f;
            var py = y + 0.5f;
            var along = Mathf.Clamp(((px - x0) * dx + (py - y0) * dy) / length, 0f, 1f);
            var ex = px - (x0 + dx * along);
            var ey = py - (y0 + dy * along);
            var distance = MathF.Sqrt(ex * ex + ey * ey);
            var cover = Mathf.Clamp(half + 0.5f - distance, 0f, 1f);
            if (cover > 0f) BlendPixel(pixels, width, x, y, color, cover);
        }
    }

    private static void StrokeBox(byte[] pixels, int width, float left, float top, float right, float bottom, float thickness, Color color)
    {
        StrokeLine(pixels, width, left, top, right, top, thickness, color);
        StrokeLine(pixels, width, right, top, right, bottom, thickness, color);
        StrokeLine(pixels, width, right, bottom, left, bottom, thickness, color);
        StrokeLine(pixels, width, left, bottom, left, top, thickness, color);
    }

    private static void StrokeEllipse(byte[] pixels, int width, float cx, float cy, float rx, float ry, float thickness, Color color)
    {
        var half = thickness / 2f;
        for (var y = (int)(cy - ry - half - 2); y <= (int)(cy + ry + half + 2); y++)
        for (var x = (int)(cx - rx - half - 2); x <= (int)(cx + rx + half + 2); x++)
        {
            var nx = (x + 0.5f - cx) / rx;
            var ny = (y + 0.5f - cy) / ry;
            var radial = MathF.Sqrt(nx * nx + ny * ny);
            // Distance to the curve, near enough for a stroke this thin on radii this round.
            var distance = MathF.Abs(radial - 1f) * MathF.Min(rx, ry);
            var cover = Mathf.Clamp(half + 0.5f - distance, 0f, 1f);
            if (cover > 0f) BlendPixel(pixels, width, x, y, color, cover);
        }
    }

    private static void FillDisc(byte[] pixels, int width, float cx, float cy, float radius, Color color)
    {
        for (var y = (int)(cy - radius - 2); y <= (int)(cy + radius + 2); y++)
        for (var x = (int)(cx - radius - 2); x <= (int)(cx + radius + 2); x++)
        {
            var distance = MathF.Sqrt((x + 0.5f - cx) * (x + 0.5f - cx) + (y + 0.5f - cy) * (y + 0.5f - cy));
            var cover = Mathf.Clamp(radius + 0.5f - distance, 0f, 1f);
            if (cover > 0f) BlendPixel(pixels, width, x, y, color, cover);
        }
    }

    private static void DrawCross(byte[] pixels, int width, int cx, int cy, int reach, Color color, int thickness)
    {
        for (var step = -reach; step <= reach; step++)
        for (var t = -thickness; t <= thickness; t++)
        {
            WritePixel(pixels, width, cx + step, cy + step + t, color);
            WritePixel(pixels, width, cx + step, cy - step + t, color);
        }
    }

    private static void DrawEllipse(byte[] pixels, int width, int cx, int cy, int rx, int ry, Color color, int thickness)
    {
        for (var angle = 0; angle < 360; angle++)
        {
            var radians = Mathf.DegToRad(angle);
            var x = cx + Mathf.RoundToInt(Mathf.Cos(radians) * rx);
            var y = cy + Mathf.RoundToInt(Mathf.Sin(radians) * ry);
            for (var t = -thickness; t <= thickness; t++)
                WritePixel(pixels, width, x + t, y + t, color);
        }
    }

    private static void DrawArc(byte[] pixels, int width, int cx, int cy, int radius, int degrees, int from, Color color, int thickness)
    {
        for (var angle = from; angle < from + degrees; angle++)
        {
            var radians = Mathf.DegToRad(angle);
            var x = cx + Mathf.RoundToInt(Mathf.Cos(radians) * radius);
            var y = cy + Mathf.RoundToInt(Mathf.Sin(radians) * radius);
            for (var t = -thickness; t <= thickness; t++)
                WritePixel(pixels, width, x + t, y, color);
        }
    }

    private static void FillEllipse(byte[] pixels, int width, int cx, int cy, int rx, int ry, Color color)
    {
        for (var y = cy - ry; y <= cy + ry; y++)
        {
            var dy = (float)(y - cy) / ry;
            var half = Mathf.RoundToInt(rx * MathF.Sqrt(MathF.Max(0f, 1f - dy * dy)));
            for (var x = cx - half; x <= cx + half; x++)
                WritePixel(pixels, width, x, y, color);
        }
    }

    private readonly Dictionary<(int, int, int), Texture2D> _selectionGlows = new();

    /// <summary>
    /// The glow a selected counter wears: a thin bright line along its edge and a soft light
    /// falling away outside it, with nothing over the counter itself.
    ///
    /// Selection used to be the whole plate turning amber, which is loud and says the wrong
    /// thing - a colour on a counter already means something here (in battle, on the march),
    /// and "selected" competed with those. An outline is what HOI4 does and what this game did
    /// on s&box: the counter keeps its own face, and the light is around it rather than on it.
    ///
    /// Made to measure for each plate texture, at the plate's own pixel scale, and hung under
    /// the plate as a child - so it follows the plate's size, its visibility and the flat map's
    /// pin shrinking without being told about any of them. The middle is empty, so which of the
    /// two draws first cannot matter.
    /// </summary>
    private Texture2D SelectionGlowTexture(int width, int height, int inset)
    {
        var key = (width, height, inset);
        if (_selectionGlows.TryGetValue(key, out var cached)) return cached;
        // In the counter's own pixels. A plate is drawn at about a sixth of its texture size,
        // so these read on screen as a glow of five or six pixels and a line of one - thin,
        // which is the point, and still there.
        const int reach = 34;
        const float rim = 7f;
        const float corner = 6f;
        var glowWidth = width + reach * 2;
        var glowHeight = height + reach * 2;
        var pixels = new byte[glowWidth * glowHeight * 4];
        // The plate's opaque rectangle, in the glow's coordinates.
        var left = reach + inset + corner;
        var right = reach + width - inset - corner;
        var top = reach + inset + corner;
        var bottom = reach + height - inset - corner;
        for (var y = 0; y < glowHeight; y++)
        for (var x = 0; x < glowWidth; x++)
        {
            // Signed distance to a rounded rectangle: negative inside.
            var dx = MathF.Max(MathF.Max(left - (x + 0.5f), (x + 0.5f) - right), 0f);
            var dy = MathF.Max(MathF.Max(top - (y + 0.5f), (y + 0.5f) - bottom), 0f);
            var inside = MathF.Min(MathF.Max(MathF.Max(left - (x + 0.5f), (x + 0.5f) - right),
                MathF.Max(top - (y + 0.5f), (y + 0.5f) - bottom)), 0f);
            var distance = MathF.Sqrt(dx * dx + dy * dy) + inside - corner;

            float alpha;
            if (distance < -rim) alpha = 0f;
            else if (distance <= 0f) alpha = 0.95f * Mathf.Clamp((distance + rim) / 2.5f, 0f, 1f);
            else
            {
                var fall = Mathf.Clamp(1f - distance / reach, 0f, 1f);
                alpha = 0.85f * fall * fall;
            }
            WritePixel(pixels, glowWidth, x, y, new Color(1f, 1f, 1f, alpha));
        }
        var texture = ImageTexture.CreateFromImage(Image.CreateFromData(glowWidth, glowHeight, false, Image.Format.Rgba8, pixels));
        _selectionGlows[key] = texture;
        return texture;
    }

    /// <summary>A selection glow for one plate, hung under it and hidden until it is wanted.</summary>
    private Sprite3D AttachSelectionGlow(Sprite3D plate, int width, int height, int inset)
    {
        var glow = new Sprite3D
        {
            Name = "SelectionGlow",
            Texture = SelectionGlowTexture(width, height, inset),
            PixelSize = plate.PixelSize,
            FixedSize = plate.FixedSize,
            Billboard = plate.Billboard,
            NoDepthTest = true,
            RenderPriority = -1,
            CastShadow = GeometryInstance3D.ShadowCastingSetting.Off,
            Modulate = Ui.Colour("globe.selection"),
            Visible = false
        };
        plate.AddChild(glow);
        return glow;
    }

    private static void WritePixel(byte[] pixels, int width, int x, int y, Color color)
    {
        if (x < 0 || y < 0 || x >= width || y * width * 4 >= pixels.Length) return;
        var write = (y * width + x) * 4;
        pixels[write] = (byte)Mathf.RoundToInt(color.R * 255f);
        pixels[write + 1] = (byte)Mathf.RoundToInt(color.G * 255f);
        pixels[write + 2] = (byte)Mathf.RoundToInt(color.B * 255f);
        pixels[write + 3] = (byte)Mathf.RoundToInt(color.A * 255f);
    }

    private int _militaryPresentationFingerprint = int.MinValue;

    /// <summary>
    /// What the counters look like this pass, not what they are.
    ///
    /// The expensive half of the pass writes Sprite3D textures, Label3D colours and node
    /// metadata for every counter. Those only change when a formation changes side of a
    /// threshold, joins a battle, starts marching, or the selection/intel changes. Hashing
    /// the same facts first lets an idle map skip the writes entirely; the battle markers
    /// still move with the camera, because their placement is a separate question.
    /// </summary>
    private int MilitaryPresentationFingerprint()
    {
        var hash = new HashCode();
        hash.Add(_flatMap);
        hash.Add(_flatStrategicCounters);
        hash.Add(_militaryMarkersEnabled);
        hash.Add(_campaign.MapRevision);
        foreach (var id in _selectedArmyIds) hash.Add(id);
        foreach (var cachedMarker in _armyStacks.Values)
        {
            var stack = cachedMarker.DisplayStack.Count > 0 ? cachedMarker.DisplayStack : cachedMarker.Stack;
            hash.Add(cachedMarker.NationId);
            hash.Add(cachedMarker.ConditionKey);
            hash.Add(stack.Count);
            var anySelected = false;
            var anyBattle = false;
            var anyMoving = false;
            var cutOff = false;
            var organisationSum = 0f;
            var planningSum = 0f;
            var supplySum = 0f;
            foreach (var division in stack)
            {
                if (_selectedArmyIds.Contains(division.Id)) anySelected = true;
                if (division.InBattle) anyBattle = true;
                if (division.IsMoving) anyMoving = true;
                if (division.CutOff) cutOff = true;
                organisationSum += division.Organisation;
                planningSum += division.Planning;
                supplySum += division.SupplyLevel;
            }
            hash.Add(anySelected);
            hash.Add(anyBattle);
            hash.Add(anyMoving);
            hash.Add(cutOff);
            if (stack.Count > 0)
            {
                hash.Add((int)MathF.Round(organisationSum / stack.Count * 20f));
                hash.Add((int)MathF.Round(planningSum / stack.Count * 20f));
                hash.Add((int)MathF.Round(supplySum / stack.Count * 20f));
            }
        }
        return hash.ToHashCode();
    }

    private void UpdateMilitaryPresentation()
    {
        if (_campaign is null) return;
        var fingerprint = MilitaryPresentationFingerprint();
        if (fingerprint != _militaryPresentationFingerprint)
        {
            _militaryPresentationFingerprint = fingerprint;
            UpdateMilitaryMarkerPresentation();
        }
        UpdateBattleMarkers();
    }

    private void UpdateMilitaryMarkerPresentation()
    {
        foreach (var cachedMarker in _armyStacks.Values)
        {
            var marker = cachedMarker.Node;
            var stack = cachedMarker.DisplayStack.Count > 0 ? cachedMarker.DisplayStack : cachedMarker.Stack;
            if (!IsInstanceValid(marker)) continue;
            if (stack.Count == 0) continue;
            UpdateArmyConditionBars(cachedMarker);
            // One pass instead of seven. This runs for every counter on the map every
            // frame, and each of those Any and Average calls was a delegate and an
            // enumerator - some seven thousand of them a frame, for arithmetic over a
            // handful of divisions.
            var anySelected = false;
            var anyBattle = false;
            var anyMoving = false;
            var cutOff = false;
            var organisationSum = 0f;
            var planningSum = 0f;
            var supplySum = 0f;
            foreach (var division in stack)
            {
                if (_selectedArmyIds.Contains(division.Id)) anySelected = true;
                if (division.InBattle) anyBattle = true;
                if (division.IsMoving) anyMoving = true;
                if (division.CutOff) cutOff = true;
                organisationSum += division.Organisation;
                planningSum += division.Planning;
                supplySum += division.SupplyLevel;
            }
            var organisation = organisationSum / stack.Count;
            var planning = planningSum / stack.Count;
            var supply = supplySum / stack.Count;
            var plate = cachedMarker.Plate;
            var count = cachedMarker.Count;
            if (cachedMarker.Glow is not null && cachedMarker.Glow.Visible != anySelected)
                cachedMarker.Glow.Visible = anySelected;
            if (plate is not null)
            {
                // Selection is the glow around the counter now; the plate keeps only a warm
                // breath of it, so the colours that mean battle and march still read.
                var tint = anySelected
                    ? new Color(1f, 0.96f, 0.86f, 1f)
                    : anyBattle
                        ? new Color(1f, 0.58f, 0.48f, 1f)
                        : anyMoving ? new Color(0.76f, 0.92f, 1f, 1f) : Colors.White;
                if (plate.Modulate != tint) plate.Modulate = tint;
            }
            if (count is not null)
            {
                // The text belongs to the reconcile pass, which knows the real division
                // and personnel totals; this pass owns only the colour. Both used to write
                // the text, with different numbers, and whichever ran last won.
                //
                // The condition now colours the whole counter rather than the small lower
                // line, so a formation out of supply reads at a glance instead of on
                // inspection. Order, stance and planning remain in the selection panel.
                var tint = supply < 0.45f || cutOff
                    ? Ui.Colour("military.routed")
                    : organisation < 0.35f ? Ui.Colour("military.shaken")
                    : planning > 0.70f ? Ui.Colour("military.planning") : Ui.Colour("military.ready");
                if (count.Modulate != tint) count.Modulate = tint;
            }
        }
    }

    private long _battleMarkerDay = -1;
    private int _battleMarkerCount = -1;
    private int _battleViewer = -1;
    private readonly HashSet<int> _activeBattleIds = new();
    private readonly List<int> _finishedBattleIds = new();
    private readonly Dictionary<int, Simulation.Army> _armyById = new();

    /// <summary>
    /// Which battles are on the map, and how they are going.
    ///
    /// All of this used to run every frame, and the worst of it resolved each participant
    /// by searching the whole army list: three hundred battles, a handful of divisions
    /// each, fourteen hundred divisions to search - millions of comparisons sixty times a
    /// second, to redraw numbers that only change when a day passes. Battles start, end and
    /// grind only inside a tick, so the work happens once a day; only the placement, which
    /// follows the camera, still happens every frame.
    /// </summary>
    private void UpdateBattleMarkers()
    {
        if (!IsInstanceValid(_battleRoot) || _campaign?.ProvinceMap is null) return;
        var viewer = FogOfWarActive && _campaign.Player is { IsHuman: true } ? _campaign.Player.Id : -1;
        var active = 0;
        foreach (var battle in _campaign.Battles) if (!battle.Ended) active++;
        if (_battleMarkerDay != _campaign.ElapsedHours || _battleViewer != viewer || _battleMarkerCount != active)
        {
            _battleViewer = viewer;
            _battleMarkerDay = _campaign.ElapsedHours;
            _battleMarkerCount = active;
            RebuildBattleMarkers();
        }
        foreach (var marker in _battleMarkers.Values)
            if (IsInstanceValid(marker.Node)) PositionBattleMarker(marker.Node, marker.Normal);
        FinishBattleMarkers();
    }

    private void RebuildBattleMarkers()
    {
        _activeBattleIds.Clear();
        foreach (var battle in _campaign.Battles)
            if (!battle.Ended && CanShowBattle(battle)) _activeBattleIds.Add(battle.Id);

        _finishedBattleIds.Clear();
        foreach (var id in _battleMarkers.Keys)
            if (!_activeBattleIds.Contains(id)) _finishedBattleIds.Add(id);
        foreach (var old in _finishedBattleIds)
        {
            if (IsInstanceValid(_battleMarkers[old].Node))
            {
                _battleRoot.RemoveChild(_battleMarkers[old].Node);
                _battleMarkers[old].Node.QueueFree();
            }
            _battleMarkers.Remove(old);
            _battleReadouts.Remove(old);
        }

        _armyById.Clear();
        foreach (var army in _campaign.Armies) _armyById[army.Id] = army;
        if (!ReferenceEquals(_battleAnchorMap, _campaign.ProvinceMap))
        {
            _battleAnchorMap = _campaign.ProvinceMap;
            _battleAnchors = new ProvinceBoundaryAnchor(_battleAnchorMap);
        }

        foreach (var battle in _campaign.Battles)
        {
            if (!_activeBattleIds.Contains(battle.Id)) continue;
            if (!_battleMarkers.TryGetValue(battle.Id, out var marker))
            {
                var node = new Node3D { Name = $"Battle_{battle.Id}" };
                _battleRoot.AddChild(node);
                marker = (node, Vector3.Zero);
                _battleMarkers[battle.Id] = marker;
            }

            var location = _campaign.ProvinceMap.CenterOf(battle.ProvinceId);
            var origin = 0;
            // Participants are stable identities, not visual counter positions. Prefer a
            // deterministic adjacent attacking province; internal battles keep the centre.
            foreach (var armyId in battle.Attackers.OrderBy(id => id))
                if (_armyById.TryGetValue(armyId, out var army) &&
                    _battleAnchors.TryFind(battle.ProvinceId, army.ProvinceId, out var border))
                { location = border; origin = army.ProvinceId; break; }
            marker.Normal = SurfacePoint(location.Latitude, location.Longitude, 1f).Normalized();
            _battleMarkers[battle.Id] = marker;
            marker.Node.SetMeta("province_id", battle.ProvinceId);
            marker.Node.SetMeta("attacking_province_id", origin);
            marker.Node.SetMeta("map_latlon", new Vector2(location.Latitude, location.Longitude));

            var own = _campaign.Player is { IsHuman: true } player &&
                (battle.AttackerId == player.Id || battle.DefenderId == player.Id);
            var detailed = !FogOfWarActive || own;
            var assessment = BattleReadout.Assess(battle, _campaign.Defines.BattleBreakOrganisation);
            var attacks = !own || battle.AttackerId == _campaign.Player.Id;
            var share = assessment.ShareFor(attacks);
            var tone = !detailed || !assessment.Known ? "text.muted" : !own ? "state.info" :
                share > .55f ? "state.positive" : share < .45f ? "state.negative" : "state.warning";
            _battleReadouts[battle.Id] = (detailed && assessment.Known ? $"{share * 100:0}" : "?", share, tone);
            marker.Node.SetMeta("endurance_known", detailed && assessment.Known);
            marker.Node.SetMeta("endurance_share", detailed ? share : -1f);
        }
    }

    private float SideOrganisation(List<int> ids)
    {
        var sum = 0f;
        var count = 0;
        foreach (var id in ids)
        {
            if (!_armyById.TryGetValue(id, out var army)) continue;
            sum += army.Organisation;
            count++;
        }
        return count == 0 ? 0f : sum / count;
    }

    /// <summary>Which markers the camera can see. Cheap, and camera-dependent, so per frame.</summary>
    private void FinishBattleMarkers()
    {
        _battleRoot.Visible = MilitaryMarkersEnabled && ViewDistance <= 14f;
        if (!_flatMap && _battleRoot.Visible)
        {
            var view = _camera.GlobalPosition.Normalized();
            var basis = _battleRoot.GlobalBasis;
            var limb = GlobeRadius / Math.Max(GlobeRadius, _camera.GlobalPosition.Length());
            foreach (var marker in _battleMarkers.Values)
                if (IsInstanceValid(marker.Node)) marker.Node.Visible = (basis * marker.Normal).Dot(view) > limb;
        }
        else
            foreach (var marker in _battleMarkers.Values)
                if (IsInstanceValid(marker.Node)) marker.Node.Visible = true;
    }

    private void PositionBattleMarker(Node3D node, Vector3 normal)
    {
        if (_flatMap)
        {
            var geo = GeographicFromNormal(normal);
            node.Position = FlatPoint(geo.Latitude, geo.Longitude, 0f);
        }
        else node.Position = normal * GlobeRadius;
    }

    private void ReprojectBattleMarkers()
    {
        foreach (var marker in _battleMarkers.Values)
            if (IsInstanceValid(marker.Node)) PositionBattleMarker(marker.Node, marker.Normal);
    }

    private bool TryPickBattle(Vector2 pixel, out LandBattle battle)
    {
        battle = null;
        if (!IsInstanceValid(_camera) || !IsInstanceValid(_battleRoot) || !_battleRoot.IsVisibleInTree() || ViewDistance > 14f) return false;
        var radius = BattleMarkerRadius();
        var best = (radius + 3f) * (radius + 3f);
        foreach (var (battleId, marker) in _battleMarkers)
        {
            if (!IsInstanceValid(marker.Node) || !marker.Node.Visible || _camera.IsPositionBehind(marker.Node.GlobalPosition)) continue;
            var distance = BattleMarkerScreen(marker.Node).DistanceSquaredTo(pixel);
            if (distance >= best) continue;
            var candidate = _campaign?.Battles.FirstOrDefault(item => item.Id == battleId && !item.Ended);
            if (candidate is null) continue;
            best = distance;
            battle = candidate;
        }
        return battle is not null;
    }
}
