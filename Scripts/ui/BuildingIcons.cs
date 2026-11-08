using System.Collections.Generic;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// A building's icon wherever a building is named: the region's build list, the region card,
/// the map's region badges (GlobeRegionIcons). One gold icon per building kind, from the sprite
/// catalogue, so a mod that ships "icon_building_&lt;id&gt;" for its own building is drawn as well.
/// </summary>
public partial class DealOrForceGame
{
    private static readonly Dictionary<BuildingKind, Texture2D> BuildingIconCache = new();

    private static Texture2D BuildingIconTexture(BuildingKind kind)
    {
        if (BuildingIconCache.TryGetValue(kind, out var cached)) return cached;
        var name = BuildingIconName(kind);
        Texture2D texture = null;
        if (SpriteCatalog.Has(name)) texture = GD.Load<Texture2D>(SpriteCatalog.TextureOf(name));
        else if (ResourceLoader.Exists($"res://game/gfx/interface/icons/{name}.png"))
            texture = GD.Load<Texture2D>($"res://game/gfx/interface/icons/{name}.png");
        BuildingIconCache[kind] = texture;
        return texture;
    }

    /// <summary>The sprite name of a building's icon: "icon_building_" and its id without separators.</summary>
    internal static string BuildingIconName(BuildingKind kind)
    {
        var id = System.Enum.IsDefined(kind) ? kind.ToString() : BuildingCatalog.IdOf(kind);
        return "icon_building_" + id.Replace("_", "").Replace("-", "").ToLowerInvariant();
    }

    /// <summary>A small icon for a building kind, or an empty spacer of the same size when it has none.</summary>
    private static Control BuildingIconControl(BuildingKind kind, int size = 18)
    {
        var texture = BuildingIconTexture(kind);
        if (texture is null) return new Control { CustomMinimumSize = new Vector2(size, size), MouseFilter = Control.MouseFilterEnum.Ignore };
        return new TextureRect
        {
            Texture = texture,
            CustomMinimumSize = new Vector2(size, size),
            ExpandMode = TextureRect.ExpandModeEnum.IgnoreSize,
            StretchMode = TextureRect.StretchModeEnum.KeepAspectCentered,
            MouseFilter = Control.MouseFilterEnum.Ignore
        };
    }
}
