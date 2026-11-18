using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

internal static class BrandAssets
{
    public const string GamePlaquePath = "res://game/gfx/interface/brand_logo_banner.png";
    public const string StudioPath = "res://game/gfx/interface/branding/studio_logo_dark.png";
    public static Texture2D GamePlaque() => LoadingScreen.Picture(SpriteCatalog.TextureOf("brand_logo_banner", GamePlaquePath));
    public static Texture2D Studio() => LoadingScreen.Picture(SpriteCatalog.TextureOf("studio_logo_dark", StudioPath));
}
