using System;
using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly Dictionary<ulong, Texture2D> _compactCounterTextures = new();
    private readonly HashSet<ulong> _liveArmyCounterTextureIds = new();
    private readonly List<(int Nation, string Template)> _obsoleteArmyCounterTextureKeys = new();

    /// <summary>
    /// Reuse the authored unit symbol, authentic national flag chip, and nation color banner
    /// in the strategic/compact view. Preserves quick visual country recognition and branch role.
    /// </summary>
    private Texture2D CompactCounterTexture(Texture2D full, Rect2I symbolBounds)
    {
        if (_compactCounterTextures.TryGetValue(full.GetInstanceId(), out var texture)) return texture;
        using var source = full.GetImage();
        if (source.IsCompressed()) source.Decompress();

        const int cardW = 72;
        const int cardH = 46;
        using var card = Image.CreateEmpty(cardW, cardH, false, Image.Format.Rgba8);
        card.Fill(new Color(0.045f, 0.065f, 0.088f, 0.98f));

        // Sample the full plate's rim and header national color
        var rim = source.GetPixel(Math.Min(3, source.GetWidth() - 1), source.GetHeight() / 2);
        var headerCol = source.GetPixel(Math.Min(20, source.GetWidth() - 1), Math.Min(12, source.GetHeight() - 1));
        var brass = new Color(0.85f, 0.75f, 0.45f, 0.95f);

        // 1. Draw outer rim in national color with subtle inner glow
        for (var y = 0; y < cardH; y++)
        for (var x = 0; x < cardW; x++)
        {
            if (x < 2 || x >= cardW - 2 || y < 2 || y >= cardH - 2)
                card.SetPixel(x, y, rim);
            else if (x == 2 || x == cardW - 3 || y == 2 || y == cardH - 3)
                card.SetPixel(x, y, rim.Lightened(0.20f));
        }

        // 2. Draw national color header banner (y = 2..15)
        for (var y = 2; y <= 15; y++)
        for (var x = 2; x < cardW - 2; x++)
            card.SetPixel(x, y, headerCol.Lerp(rim, 0.30f));

        // Divider line below header
        for (var x = 2; x < cardW - 2; x++)
            card.SetPixel(x, 15, brass);

        // 3. Extract and place miniature flag chip in the top-left canton (x = 3..24, y = 2..14)
        if (source.GetWidth() >= 100 && source.GetHeight() >= 70)
        {
            using var flagSrc = source.GetRegion(new Rect2I(10, 7, 47, 29));
            flagSrc.Resize(22, 12, Image.Interpolation.Lanczos);
            // Draw 1px golden border around flag chip
            for (var fy = 0; fy <= 13; fy++)
            for (var fx = 0; fx <= 23; fx++)
                if (fx == 0 || fx == 23 || fy == 0 || fy == 13)
                    card.SetPixel(2 + fx, 2 + fy, brass);
            card.BlendRect(flagSrc, new Rect2I(0, 0, 22, 12), new Vector2I(3, 3));
        }

        // 4. Center recessed bay for DivisionCount label (x = 25..47, y = 16..43)
        var bayCol = new Color(0.035f, 0.048f, 0.062f, 0.95f);
        var bayBorder = new Color(0.20f, 0.25f, 0.30f, 0.90f);
        for (var y = 16; y <= 43; y++)
        for (var x = 25; x <= 47; x++)
        {
            if (x == 25 || x == 47 || y == 16 || y == 43)
                card.SetPixel(x, y, bayBorder);
            else
                card.SetPixel(x, y, bayCol);
        }

        // 5. Extract and place tactical branch symbol badge in the lower-left plate (x = 4..24, y = 17..42)
        using var symbol = source.GetRegion(symbolBounds);
        var scale = Mathf.Min(18f / Math.Max(1, symbol.GetWidth()), 18f / Math.Max(1, symbol.GetHeight()));
        var targetW = Math.Max(1, Mathf.RoundToInt(symbol.GetWidth() * scale));
        var targetH = Math.Max(1, Mathf.RoundToInt(symbol.GetHeight() * scale));
        symbol.Resize(targetW, targetH, Image.Interpolation.Lanczos);
        var branchPlateCol = new Color(0.12f, 0.16f, 0.20f, 0.95f);
        for (var y = 17; y <= 42; y++)
        for (var x = 4; x <= 23; x++)
            card.SetPixel(x, y, branchPlateCol);
        card.BlendRect(symbol, new Rect2I(0, 0, targetW, targetH),
            new Vector2I(4 + (20 - targetW) / 2, 17 + (26 - targetH) / 2));

        // 6. Right silhouette medallion plate (x = 49..68, y = 17..42)
        if (source.GetWidth() >= 160)
        {
            using var silh = source.GetRegion(new Rect2I(124, 25, 42, 48));
            silh.Resize(18, 20, Image.Interpolation.Lanczos);
            card.BlendRect(silh, new Rect2I(0, 0, 18, 20), new Vector2I(49, 20));
        }

        return _compactCounterTextures[full.GetInstanceId()] = ImageTexture.CreateFromImage(card);
    }

    /// <summary>Keep only cards that this reconciliation left on an army marker.</summary>
    private void PruneArmyCounterTextureCaches()
    {
        _liveArmyCounterTextureIds.Clear();
        foreach (var marker in _armyStacks.Values)
            if (marker.FullPlateTexture is not null)
                _liveArmyCounterTextureIds.Add(marker.FullPlateTexture.GetInstanceId());

        _obsoleteArmyCounterTextureKeys.Clear();
        foreach (var (key, texture) in _armyCounterTextures)
            if (!_liveArmyCounterTextureIds.Contains(texture.GetInstanceId()))
                _obsoleteArmyCounterTextureKeys.Add(key);
        foreach (var key in _obsoleteArmyCounterTextureKeys)
        {
            var id = _armyCounterTextures[key].GetInstanceId();
            _armyCounterTextures.Remove(key);
            _compactCounterTextures.Remove(id);
        }

        // A removed marker can still await QueueFree at the end of this frame. Drop our
        // cache references only; its sprites/materials keep their textures until then.
        // Army entries in both caches are bounded by the distinct current marker cards.
        // The compact cache is shared with fleet/support cards: leave those entries alone.
    }
}
