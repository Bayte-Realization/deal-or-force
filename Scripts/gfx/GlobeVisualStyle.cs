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
    }
}
