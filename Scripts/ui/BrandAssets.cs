using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

internal static class BrandAssets
{
    public const string GamePlaquePath = "res://game/gfx/interface/brand_logo_banner.png";
    public const string StudioPath = "res://game/gfx/interface/branding/studio_logo_dark.png";
    public static Texture2D GamePlaque() => GD.Load<Texture2D>(SpriteCatalog.TextureOf("brand_logo_banner", GamePlaquePath));
    public static Texture2D Studio() => GD.Load<Texture2D>(SpriteCatalog.TextureOf("studio_logo_dark", StudioPath));
}
