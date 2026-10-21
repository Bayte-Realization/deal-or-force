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
        // The flag is part of the key. A country that changes its government changes its flag,
        // and a cached card must not go on flying the old one.
        var flagSprite = FlagSpriteFor(nation);
        var resolvedTemplate = string.IsNullOrWhiteSpace(templateId) ? "line_infantry" : templateId;
        var key = (nation.MapIndex, resolvedTemplate + "#" + flagSprite);
        if (_armyCounterTextures.TryGetValue(key, out var cached)) return cached;
        const int width = 176;
        const int height = 104;
        // Drawn supersampled and brought down with a Lanczos filter.
        const int s = 2;
        var w = width * s;
        var h = height * s;
        var pixels = new byte[w * h * 4];
        var national = new Color(nation.Color);
        var brass = new Color(0.84f, 0.72f, 0.44f);
        var rimLight = national.Lightened(0.36f).Lerp(brass, 0.22f);
        var rimBase = national.Lightened(0.18f);
        var rimDark = national.Darkened(0.24f);
        var ink = new Color(0.025f, 0.035f, 0.045f, 0.99f);
        var top = new Color(0.155f, 0.190f, 0.225f, 0.98f);
        var bottom = new Color(0.075f, 0.095f, 0.118f, 0.98f);
        const float radius = 7.5f * s;
        const float inkLine = 1.1f * s;
        const float rimLine = 6.2f * s;
        const float bevelLine = 7.5f * s;
        const float bandBottom = 22.5f * s;

        var counterSymbol = UnitTypeCatalog.Get(resolvedTemplate).CounterSymbol ?? "";
        var branchRole = resolvedTemplate switch
        {
            "marine_brigade" => "marine",
            "militia" => "militia",
            _ => counterSymbol
        };
        var branch = BranchColour(branchRole);

        for (var y = 0; y < h; y++)
        for (var x = 0; x < w; x++)
        {
            var inward = -RoundedBox(x + 0.5f, y + 0.5f, w, h, radius);
            if (inward < 0f)
            {
                WritePixel(pixels, w, x, y, new Color(ink.R, ink.G, ink.B, 0f));
                continue;
            }
            Color color;
            if (inward < inkLine) color = ink;
            else if (inward < rimLine)
            {
                var vertical = y / (float)h;
                color = vertical < 0.35f
                    ? rimLight.Lerp(rimBase, vertical / 0.35f)
                    : rimBase.Lerp(rimDark, (vertical - 0.35f) / 0.65f);
            }
            else if (inward < bevelLine)
            {
                color = (y < h * 0.5f ? brass.Lightened(0.18f) : brass.Darkened(0.28f)).Lerp(national.Lightened(0.40f), 0.35f);
            }
            else
            {
                color = top.Lerp(bottom, y / (float)h);
                if (y < bandBottom)
                {
                    var headerT = y / bandBottom;
                    color = national.Lightened(0.12f).Lerp(national.Darkened(0.26f), headerT);
                    var stripe = ((x + y * 2) / (6 * s)) % 2 == 0 ? 0.04f : -0.02f;
                    color = stripe > 0f ? color.Lightened(stripe) : color.Darkened(-stripe);
                }
                else if (y < bandBottom + 1.5f * s)
                {
                    color = brass.Lerp(national.Lightened(0.35f), 0.35f);
                }
                else if (y >= 74.5f * s)
                {
                    color = color.Darkened(0.26f);
                }
            }
            WritePixel(pixels, w, x, y, color);
        }

        // Center recessed numerical readout bay behind DivisionCount (x = 60..121, y = 25.5..73.5).
        const float bayLeft = 60f * s, bayTop = 25.5f * s, bayRight = 121f * s, bayBottom = 73.5f * s;
        for (var y = (int)bayTop; y <= (int)bayBottom; y++)
        for (var x = (int)bayLeft; x <= (int)bayRight; x++)
        {
            var inside = -RoundedBoxAt(x + 0.5f, y + 0.5f, bayLeft, bayTop, bayRight, bayBottom, 3.5f * s);
            if (inside < 0f) continue;
            var bayCol = inside < 1.1f * s
                ? new Color(0.26f, 0.31f, 0.36f, 0.90f)
                : new Color(0.045f, 0.060f, 0.078f, 0.92f);
            BlendPixel(pixels, w, x, y, bayCol, 0.92f);
        }

        // Prominent National Flag canton on the top-left (x = 10..57, y = 7..36 -> 47x29 px!).
        PaintFlagChip(pixels, w, flagSprite, 10f * s, 7f * s, 47f * s, 29f * s, nation);

        // NATO Echelon "XX" (Division) gold marks in the top-right national header bar.
        var echelonCol = new Color(0.97f, 0.92f, 0.76f, 0.95f);
        StrokeLine(pixels, w, 134f * s, 10.5f * s, 141f * s, 18.5f * s, 1.8f * s, echelonCol);
        StrokeLine(pixels, w, 134f * s, 18.5f * s, 141f * s, 10.5f * s, 1.8f * s, echelonCol);
        StrokeLine(pixels, w, 145f * s, 10.5f * s, 152f * s, 18.5f * s, 1.8f * s, echelonCol);
        StrokeLine(pixels, w, 145f * s, 18.5f * s, 152f * s, 10.5f * s, 1.8f * s, echelonCol);
        FillDisc(pixels, w, 160f * s, 14.5f * s, 3.2f * s, national.Lightened(0.38f));
        StrokeEllipse(pixels, w, 160f * s, 14.5f * s, 3.2f * s, 3.2f * s, 0.9f * s, echelonCol);

        // Heavy Artillery / Long-Range Missile firepower indicator in the header
        var hasArtillery = branchRole == "artillery" || resolvedTemplate.Contains("artillery");
        var hasMissile = resolvedTemplate.Contains("missile") || resolvedTemplate.Contains("rocket");
        if (hasArtillery || hasMissile)
        {
            var iconAccent = new Color(1.0f, 0.88f, 0.45f, 0.95f);
            var iconBase = new Color(0.92f, 0.32f, 0.22f, 0.95f);
            if (hasMissile)
            {
                const float rx = 96f * s, ry = 14.5f * s;
                StrokeLine(pixels, w, rx - 9f * s, ry, rx + 7f * s, ry, 2.4f * s, iconAccent);
                StrokeLine(pixels, w, rx + 7f * s, ry, rx + 11f * s, ry, 3.4f * s, iconBase);
                StrokeLine(pixels, w, rx - 4f * s, ry - 3.5f * s, rx + 4f * s, ry, 1.8f * s, iconAccent);
                StrokeLine(pixels, w, rx - 4f * s, ry + 3.5f * s, rx + 4f * s, ry, 1.8f * s, iconAccent);
            }
            else
            {
                const float ax = 96f * s, ay = 14.5f * s;
                StrokeLine(pixels, w, ax - 7f * s, ay + 4f * s, ax + 7f * s, ay - 4f * s, 2.4f * s, iconAccent);
                StrokeLine(pixels, w, ax - 7f * s, ay - 4f * s, ax + 7f * s, ay + 4f * s, 2.4f * s, iconAccent);
                FillDisc(pixels, w, ax, ay, 2.2f * s, iconBase);
            }
        }

        // Left NATO Tactical Branch Plate (x = 10..57, y = 39..73.5 -> sits cleanly above condition bars!).
        const float plateLeft = 10f * s, plateTop = 39f * s, plateRight = 57f * s, plateBottom = 73.5f * s;
        for (var y = (int)plateTop; y <= (int)plateBottom; y++)
        for (var x = (int)plateLeft; x <= (int)plateRight; x++)
        {
            var inside = -RoundedBoxAt(x + 0.5f, y + 0.5f, plateLeft, plateTop, plateRight, plateBottom, 3.5f * s);
            if (inside < 0f) continue;
            var colour = inside < 1.3f * s
                ? branch.Lightened(0.36f)
                : branch.Darkened(0.42f).Lerp(branch.Darkened(0.62f), (y - plateTop) / (plateBottom - plateTop));
            BlendPixel(pixels, w, x, y, colour, 0.96f);
        }

        var symbol = new Color(0.96f, 0.98f, 1.00f, 0.98f);
        const float cx = 33.5f * s, cy = 56.2f * s, halfW = 16.5f * s, halfH = 11.5f * s, stroke = 4.6f * s;
        if (!PaintSymbolArt(pixels, w, counterSymbol, cx, cy, halfW + stroke / 2f, halfH + stroke / 2f, symbol))
            DrawNatoBranchSymbol(pixels, w, branchRole, cx, cy, halfW, halfH, stroke, symbol);

        // Right Combat Branch Silhouette Medallion (x = 124..166, y = 25.5..73.5).
        const float rightLeft = 124f * s, rightTop = 25.5f * s, rightRight = 166f * s, rightBottom = 73.5f * s;
        for (var y = (int)rightTop; y <= (int)rightBottom; y++)
        for (var x = (int)rightLeft; x <= (int)rightRight; x++)
        {
            var inside = -RoundedBoxAt(x + 0.5f, y + 0.5f, rightLeft, rightTop, rightRight, rightBottom, 3.5f * s);
            if (inside < 0f) continue;
            var colour = inside < 1.2f * s
                ? branch.Lightened(0.28f).Lerp(national.Lightened(0.30f), 0.35f)
                : branch.Darkened(0.55f).Lerp(national.Darkened(0.65f), 0.35f);
            BlendPixel(pixels, w, x, y, colour, 0.94f);
        }
        DrawBranchSilhouette(pixels, w, branchRole, 145f * s, 49.5f * s, s, symbol, branch.Lightened(0.45f));

        try
        {
            using var image = Image.CreateFromData(w, h, false, Image.Format.Rgba8, pixels);
            image.Resize(width, height, Image.Interpolation.Lanczos);
            var texture = ImageTexture.CreateFromImage(image);
            _armyCounterTextures[key] = texture;
            return texture;
        }
        catch (Exception e)
        {
            GD.PushWarning($"[dof] ArmyCounterTexture fallback for {nation.Tag}: {e.Message}");
            if (_armyCounterTextures.Values.FirstOrDefault(t => t is not null) is { } fallback)
                return fallback;
            return new PlaceholderTexture2D { Size = new Vector2(width, height) };
        }
    }

    private static void DrawNatoBranchSymbol(byte[] pixels, int w, string branchRole,
        float cx, float cy, float halfW, float halfH, float stroke, Color symbol)
    {
        switch (branchRole)
        {
            case "armor":
                StrokeEllipse(pixels, w, cx, cy - halfH * 0.12f, halfW * 0.92f, halfH * 0.58f, stroke * 0.82f, symbol);
                for (var tick = -1; tick <= 1; tick++)
                    FillDisc(pixels, w, cx + tick * halfW * 0.50f, cy + halfH * 0.65f, stroke * 0.36f, symbol);
                break;
            case "artillery":
                FillDisc(pixels, w, cx, cy, stroke * 1.15f, symbol);
                StrokeEllipse(pixels, w, cx, cy, stroke * 1.65f, stroke * 1.65f, stroke * 0.42f, symbol);
                break;
            case "mechanized":
                StrokeLine(pixels, w, cx - halfW * 0.85f, cy - halfH * 0.78f, cx + halfW * 0.85f, cy + halfH * 0.78f, stroke * 0.72f, symbol);
                StrokeLine(pixels, w, cx - halfW * 0.85f, cy + halfH * 0.78f, cx + halfW * 0.85f, cy - halfH * 0.78f, stroke * 0.72f, symbol);
                StrokeEllipse(pixels, w, cx, cy, halfW * 0.78f, halfH * 0.55f, stroke * 0.68f, symbol);
                break;
            case "motorized":
                StrokeLine(pixels, w, cx - halfW * 0.88f, cy - halfH * 0.72f, cx + halfW * 0.88f, cy + halfH * 0.48f, stroke * 0.80f, symbol);
                StrokeLine(pixels, w, cx - halfW * 0.88f, cy + halfH * 0.48f, cx + halfW * 0.88f, cy - halfH * 0.72f, stroke * 0.80f, symbol);
                FillDisc(pixels, w, cx - halfW * 0.52f, cy + halfH * 0.82f, stroke * 0.52f, symbol);
                FillDisc(pixels, w, cx + halfW * 0.52f, cy + halfH * 0.82f, stroke * 0.52f, symbol);
                break;
            case "marine":
                StrokeLine(pixels, w, cx - halfW * 0.85f, cy - halfH * 0.75f, cx + halfW * 0.85f, cy + halfH * 0.75f, stroke * 0.75f, symbol);
                StrokeLine(pixels, w, cx - halfW * 0.85f, cy + halfH * 0.75f, cx + halfW * 0.85f, cy - halfH * 0.75f, stroke * 0.75f, symbol);
                StrokeLine(pixels, w, cx, cy - halfH * 0.78f, cx, cy + halfH * 0.72f, stroke * 0.62f, symbol);
                StrokeLine(pixels, w, cx - halfW * 0.45f, cy + halfH * 0.45f, cx + halfW * 0.45f, cy + halfH * 0.45f, stroke * 0.58f, symbol);
                break;
            case "militia":
                StrokeLine(pixels, w, cx - halfW * 0.82f, cy - halfH * 0.72f, cx + halfW * 0.82f, cy + halfH * 0.72f, stroke * 0.75f, symbol);
                StrokeLine(pixels, w, cx - halfW * 0.82f, cy + halfH * 0.72f, cx + halfW * 0.82f, cy - halfH * 0.72f, stroke * 0.75f, symbol);
                StrokeLine(pixels, w, cx, cy - halfH * 0.85f, cx, cy + halfH * 0.85f, stroke * 0.55f, symbol);
                break;
            default:
                StrokeLine(pixels, w, cx - halfW * 0.90f, cy - halfH * 0.82f, cx + halfW * 0.90f, cy + halfH * 0.82f, stroke * 0.88f, symbol);
                StrokeLine(pixels, w, cx - halfW * 0.90f, cy + halfH * 0.82f, cx + halfW * 0.90f, cy - halfH * 0.82f, stroke * 0.88f, symbol);
                break;
        }
    }

    private static void DrawBranchSilhouette(byte[] pixels, int w, string branchRole,
        float cx, float cy, int s, Color primary, Color accent)
    {
        switch (branchRole)
        {
            case "armor":
                // Main Battle Tank silhouette: track assembly, sloped hull, low turret, and long cannon barrel.
                StrokeEllipse(pixels, w, cx - 1f * s, cy + 7.5f * s, 14f * s, 4.2f * s, 2.6f * s, primary);
                for (var wheel = -2; wheel <= 2; wheel++)
                    FillDisc(pixels, w, cx - 1f * s + wheel * 4.5f * s, cy + 7.5f * s, 1.8f * s, accent);
                StrokeLine(pixels, w, cx - 15f * s, cy + 3.2f * s, cx + 13f * s, cy + 3.2f * s, 3.8f * s, primary);
                StrokeLine(pixels, w, cx - 9f * s, cy - 2.2f * s, cx + 4f * s, cy - 2.2f * s, 4.8f * s, primary);
                StrokeLine(pixels, w, cx + 2f * s, cy - 3.2f * s, cx + 17.5f * s, cy - 5.2f * s, 2.2f * s, accent);
                break;
            case "mechanized":
                // IFV / Armored Carrier silhouette: tracked hull, troop compartment, turret & autocannon.
                StrokeEllipse(pixels, w, cx, cy + 7.5f * s, 13.5f * s, 3.8f * s, 2.4f * s, primary);
                StrokeLine(pixels, w, cx - 14f * s, cy + 2.8f * s, cx + 13f * s, cy + 2.8f * s, 4.4f * s, primary);
                StrokeLine(pixels, w, cx - 11f * s, cy - 1.5f * s, cx + 5f * s, cy - 1.5f * s, 3.6f * s, primary);
                StrokeLine(pixels, w, cx + 1f * s, cy - 4.2f * s, cx + 15.5f * s, cy - 5.8f * s, 2.0f * s, accent);
                break;
            case "motorized":
                // Tactical 6x6 Truck silhouette: cab, troop canopy bed, chassis, and 3 wheels.
                StrokeLine(pixels, w, cx - 14f * s, cy + 2.5f * s, cx + 14f * s, cy + 2.5f * s, 3.6f * s, primary);
                StrokeLine(pixels, w, cx - 13.5f * s, cy - 3.5f * s, cx + 3.5f * s, cy - 3.5f * s, 5.5f * s, primary);
                StrokeLine(pixels, w, cx + 5.5f * s, cy - 1.8f * s, cx + 12.5f * s, cy - 1.8f * s, 4.2f * s, accent);
                FillDisc(pixels, w, cx - 10f * s, cy + 7.5f * s, 3.2f * s, primary);
                FillDisc(pixels, w, cx - 1.5f * s, cy + 7.5f * s, 3.2f * s, primary);
                FillDisc(pixels, w, cx + 9.5f * s, cy + 7.5f * s, 3.2f * s, primary);
                break;
            case "artillery":
                // Field Howitzer: gun wheel, recoil trail, elevated cannon barrel, and ballistic arc.
                StrokeLine(pixels, w, cx - 14f * s, cy + 8.5f * s, cx - 3f * s, cy + 4.5f * s, 2.6f * s, primary);
                FillDisc(pixels, w, cx - 3f * s, cy + 5.5f * s, 4.2f * s, primary);
                FillDisc(pixels, w, cx - 3f * s, cy + 5.5f * s, 1.8f * s, accent);
                StrokeLine(pixels, w, cx - 4f * s, cy + 3.5f * s, cx + 13.5f * s, cy - 8.5f * s, 3.2f * s, primary);
                FillDisc(pixels, w, cx + 15f * s, cy - 9.5f * s, 2.2f * s, accent);
                for (var seg = 0; seg < 5; seg++)
                {
                    var a0 = Mathf.DegToRad(200f + seg * 22f);
                    var a1 = Mathf.DegToRad(200f + (seg + 1) * 22f);
                    StrokeLine(pixels, w,
                        cx + MathF.Cos(a0) * 14f * s, cy - 1f * s + MathF.Sin(a0) * 10f * s,
                        cx + MathF.Cos(a1) * 14f * s, cy - 1f * s + MathF.Sin(a1) * 10f * s,
                        1.4f * s, accent);
                }
                break;
            case "marine":
                // Naval Anchor & Waves crest.
                StrokeEllipse(pixels, w, cx, cy - 9.5f * s, 3.0f * s, 3.0f * s, 1.8f * s, accent);
                StrokeLine(pixels, w, cx, cy - 6.5f * s, cx, cy + 7.5f * s, 2.6f * s, primary);
                StrokeLine(pixels, w, cx - 7.5f * s, cy - 2.5f * s, cx + 7.5f * s, cy - 2.5f * s, 2.2f * s, primary);
                StrokeLine(pixels, w, cx - 10.5f * s, cy + 2.5f * s, cx, cy + 8.5f * s, 2.4f * s, primary);
                StrokeLine(pixels, w, cx + 10.5f * s, cy + 2.5f * s, cx, cy + 8.5f * s, 2.4f * s, primary);
                StrokeLine(pixels, w, cx - 13f * s, cy + 12f * s, cx + 13f * s, cy + 12f * s, 1.8f * s, accent);
                break;
            case "militia":
                // Irregular Partisan Chevron Shield & Rifle.
                StrokeLine(pixels, w, cx - 10f * s, cy - 9f * s, cx + 10f * s, cy - 9f * s, 2.2f * s, accent);
                StrokeLine(pixels, w, cx - 10f * s, cy - 9f * s, cx - 8f * s, cy + 3f * s, 2.2f * s, primary);
                StrokeLine(pixels, w, cx + 10f * s, cy - 9f * s, cx + 8f * s, cy + 3f * s, 2.2f * s, primary);
                StrokeLine(pixels, w, cx - 8f * s, cy + 3f * s, cx, cy + 11f * s, 2.2f * s, primary);
                StrokeLine(pixels, w, cx + 8f * s, cy + 3f * s, cx, cy + 11f * s, 2.2f * s, primary);
                StrokeLine(pixels, w, cx - 6f * s, cy + 4f * s, cx + 6f * s, cy - 5f * s, 2.4f * s, accent);
                break;
            default:
                // Infantry: Crossed Rifles with Bayonets & Tactical Chevron above.
                StrokeLine(pixels, w, cx - 12f * s, cy + 9.5f * s, cx + 12f * s, cy - 7.5f * s, 2.6f * s, primary);
                StrokeLine(pixels, w, cx - 12f * s, cy - 7.5f * s, cx + 12f * s, cy + 9.5f * s, 2.6f * s, primary);
                StrokeLine(pixels, w, cx - 7f * s, cy - 7.5f * s, cx, cy - 12.5f * s, 2.2f * s, accent);
                StrokeLine(pixels, w, cx + 7f * s, cy - 7.5f * s, cx, cy - 12.5f * s, 2.2f * s, accent);
                break;
        }
    }

    /// <summary>
    /// Which flag this country would fly today, as a sprite name. Asked of the flag catalogue
    /// rather than built from the tag, so a country that has changed its government shows the
    /// flag of the government it has.
    /// </summary>
    private string FlagSpriteFor(Nation nation)
    {
        var sprite = _campaign is not null ? FlagCatalog.Resolve(_campaign, nation) : "";
        if (string.IsNullOrWhiteSpace(sprite))
            sprite = "flag_" + (nation.Tag ?? "").ToLowerInvariant();
        return sprite;
    }

    /// <summary>Flags already decoded for the cards: one image per sprite, not one per card.</summary>
    private static readonly Dictionary<string, Image> FlagChipImages = new(StringComparer.Ordinal);
    private static readonly Queue<string> FlagChipOrder = new();
    private const int FlagChipImageLimit = 64;
    private static readonly Dictionary<string, Image> SymbolArtImages = new(StringComparer.OrdinalIgnoreCase);

    // These are owned CPU copies from Texture.GetImage, not textures borrowed by UI.
    // A changing world must not retain every decoded full-resolution historic flag.
    private static void RememberFlagChipImage(string key, Image image)
    {
        if (FlagChipImages.Remove(key, out var replaced))
        { if (!ReferenceEquals(replaced, image)) replaced?.Dispose(); }
        else FlagChipOrder.Enqueue(key);
        FlagChipImages[key] = image;
        while (FlagChipImages.Count > FlagChipImageLimit && FlagChipOrder.Count > 0)
            if (FlagChipImages.Remove(FlagChipOrder.Dequeue(), out var retired)) retired?.Dispose();
    }

    private static void ClearFlagChipImages()
    {
        foreach (var image in FlagChipImages.Values) image?.Dispose();
        FlagChipImages.Clear(); FlagChipOrder.Clear();
    }

    private static void PaintFlagChip(byte[] pixels, int width, string sprite,
        float left, float top, float chipWidth, float chipHeight, Nation nation = null)
    {
        var cacheKey = !string.IsNullOrWhiteSpace(sprite) ? sprite : (nation?.Tag ?? "fallback");
        if (!FlagChipImages.TryGetValue(cacheKey, out var image) || image is null || image.IsEmpty())
        {
            image = DealOrForceGame.FlagSpriteTexture(sprite)?.GetImage();
            if (image is not null && !image.IsEmpty())
            {
                if (image.IsCompressed()) image.Decompress();
                image.Convert(Image.Format.Rgba8);
                RememberFlagChipImage(cacheKey, image);
            }
            else { image?.Dispose(); image = null; }
        }
        var x0 = (int)MathF.Round(left);
        var y0 = (int)MathF.Round(top);
        var cw = Math.Max(2, (int)MathF.Round(chipWidth));
        var ch = Math.Max(2, (int)MathF.Round(chipHeight));
        var outerShadow = new Color(0.015f, 0.022f, 0.030f, 0.98f);
        var brassRim = new Color(0.88f, 0.76f, 0.46f, 0.96f);
        for (var y = -2; y <= ch + 1; y++)
        for (var x = -2; x <= cw + 1; x++)
        {
            if (x >= 0 && y >= 0 && x < cw && y < ch) continue;
            var isInnerRim = x >= -1 && y >= -1 && x <= cw && y <= ch;
            BlendPixel(pixels, width, x0 + x, y0 + y, isInnerRim ? brassRim : outerShadow, 0.96f);
        }
        if (image is not null && !image.IsEmpty())
        {
            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var u = (x + 0.5f) / cw;
                var v = (y + 0.5f) / ch;
                var source = SampleBilinear(image, u, v);
                var sheen = 1f + MathF.Sin(u * MathF.PI * 2f) * 0.04f - v * 0.06f;
                var col = new Color(
                    Mathf.Clamp(source.R * sheen, 0f, 1f),
                    Mathf.Clamp(source.G * sheen, 0f, 1f),
                    Mathf.Clamp(source.B * sheen, 0f, 1f), 1f);
                BlendPixel(pixels, width, x0 + x, y0 + y, col, 1f);
            }
        }
        else if (nation is not null)
        {
            // Fallback tricolour banner for dynamically founded breakaway states without a static sprite.
            var mainCol = new Color(nation.Color);
            var topStripe = mainCol.Lightened(0.24f);
            var midStripe = new Color(0.94f, 0.95f, 0.96f);
            var botStripe = mainCol.Darkened(0.22f);
            for (var y = 0; y < ch; y++)
            for (var x = 0; x < cw; x++)
            {
                var v = y / (float)ch;
                var col = v < 0.33f ? topStripe : v < 0.67f ? midStripe : botStripe;
                BlendPixel(pixels, width, x0 + x, y0 + y, col, 1f);
            }
        }
        for (var x = 0; x < cw; x++)
            BlendPixel(pixels, width, x0 + x, y0, new Color(1f, 1f, 1f, 0.28f), 0.55f);
    }

    /// <summary>Bilinear read of an image by normalised coordinates, for shrinking a flag.</summary>
    private static Color SampleBilinear(Image image, float u, float v)
    {
        var w = image.GetWidth();
        var h = image.GetHeight();
        if (w <= 0 || h <= 0) return Colors.Transparent;
        var fx = Mathf.Clamp(u * w - 0.5f, 0f, w - 1);
        var fy = Mathf.Clamp(v * h - 0.5f, 0f, h - 1);
        var x0 = (int)fx;
        var y0 = (int)fy;
        var x1 = Math.Min(x0 + 1, w - 1);
        var y1 = Math.Min(y0 + 1, h - 1);
        var tx = fx - x0;
        var ty = fy - y0;
        return image.GetPixel(x0, y0).Lerp(image.GetPixel(x1, y0), tx)
            .Lerp(image.GetPixel(x0, y1).Lerp(image.GetPixel(x1, y1), tx), ty);
    }

    /// <summary>The colour a branch of service is drawn in, one per counter symbol.</summary>
    private static Color BranchColour(string symbol) => symbol switch
    {
        "armor" => new Color(0.78f, 0.22f, 0.22f),
        "mechanized" => new Color(0.22f, 0.58f, 0.52f),
        "motorized" => new Color(0.38f, 0.56f, 0.26f),
        "artillery" => new Color(0.86f, 0.48f, 0.12f),
        "rocket" => new Color(0.88f, 0.32f, 0.16f),
        "marine" => new Color(0.18f, 0.44f, 0.74f),
        "air_defence" => new Color(0.52f, 0.36f, 0.72f),
        "militia" => new Color(0.55f, 0.45f, 0.32f),
        _ => new Color(0.28f, 0.48f, 0.34f)
    };

    /// <summary>Signed distance from a point to a rounded rectangle anywhere in the image.</summary>
    private static float RoundedBoxAt(float x, float y, float left, float top, float right, float bottom, float radius)
    {
        var cx = (left + right) * 0.5f;
        var cy = (top + bottom) * 0.5f;
        var qx = MathF.Abs(x - cx) - ((right - left) * 0.5f - radius);
        var qy = MathF.Abs(y - cy) - ((bottom - top) * 0.5f - radius);
        var outside = MathF.Sqrt(MathF.Max(qx, 0f) * MathF.Max(qx, 0f) + MathF.Max(qy, 0f) * MathF.Max(qy, 0f));
        return outside + MathF.Min(MathF.Max(qx, qy), 0f) - radius;
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
        if (string.IsNullOrWhiteSpace(path)) return false;
        if (!SymbolArtImages.TryGetValue(path, out var baseArt) || baseArt is null || baseArt.IsEmpty())
        {
            var loose = path.StartsWith("res://", StringComparison.Ordinal) ? ProjectSettings.GlobalizePath(path) : path;
            if (ResourceLoader.Exists(path)) baseArt = GD.Load<Texture2D>(path)?.GetImage();
            else if (System.IO.File.Exists(loose)) baseArt = Image.LoadFromFile(loose);
            if (baseArt is null || baseArt.IsEmpty()) return false;
            if (baseArt.IsCompressed()) baseArt.Decompress();
            baseArt.Convert(Image.Format.Rgba8);
            SymbolArtImages[path] = baseArt;
        }
        using var art = baseArt.Duplicate() as Image;
        if (art is null) return false;
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
        using var image = Image.CreateFromData(glowWidth, glowHeight, false, Image.Format.Rgba8, pixels);
        var texture = ImageTexture.CreateFromImage(image);
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
