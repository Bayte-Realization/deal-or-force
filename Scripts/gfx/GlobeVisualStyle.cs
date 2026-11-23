using System;
using DealOrForce.Simulation;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private float _cloudOpacityScale = 1f;

    /// <summary>
    /// Style is independent of hardware and quality. The default preserves the authored
    /// Forward+ balance; vivid paint and stronger relief are optional on EVERY renderer.
    /// Only material constants change: no extra render pass or texture copy.
    /// Reapply after terrain upload so loading never overwrites the player's choice.
    /// </summary>
    public void ApplyMapVisualStyle()
    {
        ApplyAtlasLighting();
        var (saturation, relief) = SettingsCatalog.Chosen("map_visual_style") switch
        {
            "vivid" => (1.04f, 1.10f),
            "relief" => (.86f, 1.36f),
            _ => (.86f, 1f)
        };
        _globeMaterial?.SetShaderParameter("political_saturation", saturation);
        _globeMaterial?.SetShaderParameter("relief_shading", Math.Clamp(TerrainCatalog.ReliefShading * relief, 0f, 1.35f));
        // Missing in an older content pack means the original sky, not a zero fallback.
        // Only decorative clouds change; the intelligence mask remains untouched.
        _cloudOpacityScale = SettingsCatalog.Of("map_cloud_opacity") is null ? 1f
            : Math.Clamp(SettingsCatalog.Number("map_cloud_opacity"), 0f, 1f);
        UpdateWeather(0);
        // The atlas look rides on the same call: every place that reapplies the style (the
        // settings page, the terrain upload) must reapply the look too, or loading would put
        // the classic uniforms back under a player who chose the atlas.
        ApplyMapLook();
        ApplyShaderQualityTier();
        ApplyTerrainRelief3D();
    }

    private void ApplyTerrainRelief3D()
    {
        var tier = CurrentShaderQualityTier();
        var isTouchPrimary = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var toggleOn = SettingsCatalog.Of("terrain_3d_relief") is null || SettingsCatalog.Flag("terrain_3d_relief");
        var enabled = toggleOn && !ReducedMap && !_mapLookPaper && tier < 1.5f;
        var styleBoost = SettingsCatalog.Chosen("map_visual_style") == "relief" ? 1.18f : 1.0f;
        // Sculptural physical relief on PC (mobile skips 5760px heightmap decode so keeps 0 displacement).
        var baseDisplacement = isTouchPrimary ? 0f : (tier < 0.5f ? 0.055f : 0.042f);
        var displacement = enabled ? baseDisplacement * styleBoost : 0f;
        var oceanDepth = enabled && !isTouchPrimary ? (tier < 0.5f ? 0.018f : 0.014f) : 0f;
        _globeMaterial?.SetShaderParameter("relief_displacement", displacement);
        _globeMaterial?.SetShaderParameter("relief_ocean_depth", oceanDepth);
        ApplyReliefMeshSubdivision(enabled && !isTouchPrimary, tier);
        ApplyReliefState(displacement, oceanDepth);
    }
}
