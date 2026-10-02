using System.Collections.Generic;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private readonly Dictionary<ulong, Texture2D> _compactCounterTextures = new();

    /// <summary>Reuse the authored unit symbol and nationality, without shrinking tiny
    /// personnel/mission text into the strategic view. Mods' symbol art carries through.</summary>
    private Texture2D CompactCounterTexture(Texture2D full, Rect2I symbolBounds)
    {
        if (_compactCounterTextures.TryGetValue(full.GetInstanceId(), out var texture)) return texture;
        var source = full.GetImage();
        if (source.IsCompressed()) source.Decompress();
        var symbol = source.GetRegion(symbolBounds);
        var scale = Mathf.Min(28f / symbol.GetWidth(), 28f / symbol.GetHeight());
        symbol.Resize(Mathf.RoundToInt(symbol.GetWidth() * scale), Mathf.RoundToInt(symbol.GetHeight() * scale), Image.Interpolation.Lanczos);
        var card = Image.CreateEmpty(48, 40, false, Image.Format.Rgba8);
        card.Fill(new Color(.055f, .085f, .115f, .97f));
        // Sample the full plate's rim: army nationality, support arm or selection.
        var rim = source.GetPixel(2, source.GetHeight() / 2);
        for (var y = 0; y < 40; y++)
        for (var x = 0; x < 48; x++)
            if (x < 2 || x >= 46 || y < 2 || y >= 38) card.SetPixel(x, y, rim);
        card.BlendRect(symbol, new Rect2I(0, 0, symbol.GetWidth(), symbol.GetHeight()),
            new Vector2I(24 - symbol.GetWidth() / 2, 20 - symbol.GetHeight() / 2));
        return _compactCounterTextures[full.GetInstanceId()] = ImageTexture.CreateFromImage(card);
    }
}
