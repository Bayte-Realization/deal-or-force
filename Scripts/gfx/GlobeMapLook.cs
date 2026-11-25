using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The 4-way map mood ("realistic", "paper", "atlas", "classic") and optional interior
/// country-fill translucency ("classic_opaque", "balanced", "translucent"), handed to the
/// globe material (Simulation.MapLook, game/gui/78_map_look.json).
///
/// Every new term in the map shader stands behind uniforms that default to nought, so an
/// unconfigured material and the "classic" setting both draw the map exactly as it was.
/// </summary>
public sealed partial class GlobePresentation
{
    private MapLookStyle _mapLookStyle;
    private string _mapLookMode = MapLook.RealisticValue;
    private bool _mapLookAtlas;
    private bool _mapLookRealistic;
    private bool _mapLookPaper;
    private float _cachedPaperMix = -1f;

    /// <summary>Reads the mood and fill settings plus the skin, and updates every uniform. Cheap; not per frame.</summary>
    private void ApplyMapLook()
    {
        _mapLookStyle = MapLook.Current();
        _mapLookMode = MapLook.Mode();
        _mapLookAtlas = MapLook.Atlas();
        _mapLookRealistic = _mapLookMode == MapLook.RealisticValue;
        _mapLookPaper = _mapLookMode == MapLook.PaperValue;
        var fillTranslucency = MapLook.PoliticalFillTranslucency(MapLook.EffectivePoliticalFill(_mapLookMode));
        // Resend paper_mix immediately: switching to paper sends 1, classic sends 0, realistic/atlas send zoom's.
        _cachedPaperMix = -1f;
        if (IsInstanceValid(_daySun))
            _daySun.LightColor = _mapLookAtlas ? new Color(0.99f, 0.994f, 1.0f) : Colors.White;
        if (IsInstanceValid(_skyEnvironment) && _skyEnvironment.Environment is not null)
            _skyEnvironment.Environment.AmbientLightColor = _mapLookAtlas ? new Color(0.86f, 0.89f, 0.94f) : Ui.Colour("globe.ambient");
        SyncAtlasSun();
        ApplyAtmosphereLook();
        UpdateWeather(0);
        _cityMiniatures.InvalidateVisibility();
        InvalidateCities();
        InvalidateCountryAndRegionLabelAppearance();
        if (_globeMaterial is null) return;
        var style = _mapLookStyle;
        _globeMaterial.SetShaderParameter("atlas_look", _mapLookAtlas ? 1f : 0f);
        _globeMaterial.SetShaderParameter("realistic_look", _mapLookRealistic ? 1f : 0f);
        _globeMaterial.SetShaderParameter("political_fill_translucency", fillTranslucency);
        _globeMaterial.SetShaderParameter("look_paper", Ink(style.Paper));
        _globeMaterial.SetShaderParameter("look_shade", Ink(style.Shade));
        _globeMaterial.SetShaderParameter("look_lamp", Ink(style.Lamp));
        _globeMaterial.SetShaderParameter("look_relief_tint", style.ReliefTint);
        _globeMaterial.SetShaderParameter("look_sea_deep", Ink(style.SeaDeep));
        _globeMaterial.SetShaderParameter("look_sea_mid", Ink(style.SeaMid));
        _globeMaterial.SetShaderParameter("look_sea_shelf", Ink(style.SeaShelf));
        _globeMaterial.SetShaderParameter("look_sea_coast", Ink(style.SeaCoast));
        _globeMaterial.SetShaderParameter("look_sea_chart", Ink(style.SeaChart));
        _globeMaterial.SetShaderParameter("look_foam", Ink(style.Foam));
        _globeMaterial.SetShaderParameter("look_coast_ink", Ink(style.CoastInk));
        _globeMaterial.SetShaderParameter("look_unowned", Ink(style.Unowned));
        _globeMaterial.SetShaderParameter("look_ground_show_near", style.GroundShowNear);
        _globeMaterial.SetShaderParameter("look_cover_far", style.CoverFar);
        _globeMaterial.SetShaderParameter("look_saturation_far", style.SaturationFar);
        _globeMaterial.SetShaderParameter("look_relief_far", style.ReliefFar);
        _globeMaterial.SetShaderParameter("look_far_band", style.FarBand);
        _globeMaterial.SetShaderParameter("look_far_band_pixels", style.FarBandPixels);
        _globeMaterial.SetShaderParameter("look_foam_strength", style.FoamStrength);
        _globeMaterial.SetShaderParameter("look_waterline", style.Waterline);
        _globeMaterial.SetShaderParameter("look_sea_sheen", style.SeaSheen);
        _globeMaterial.SetShaderParameter("look_selection_width", style.SelectionWidth);
        if (_packedLookups is not null)
        {
            // PackedMapShader strips frontier_field on Compatibility/Mobile (12-sampler budget)
            // and replaces texture(frontier_field, UV) with vec4(1.0); keep frontier_ready at 0.
            _globeMaterial.SetShaderParameter("frontier_ready", 0f);
        }
        UpdatePaperMix();
    }

    /// <summary>
    /// Whether the frontier field is worth building: only Forward+ where the un-packed shader binds
    /// a dedicated frontier_field sampler; Compatibility/Mobile strips it in PackedMapShader.
    /// </summary>
    private bool FrontierFieldWanted() => _packedLookups is null;

    /// <summary>Per frame from _Process: the paper share, sent only when it moves by more than 0.002.</summary>
    private void UpdatePaperMix()
    {
        if (_mapLookStyle is null) return;
        var paperMix = MapLook.PaperMixForMode(
            _mapLookMode, _flatMap, _flatMap ? _flatZoom : _distance, _mapLookStyle);
        // The two ends are always sent exactly: the shader skips terrain sampling above 0.999.
        if (paperMix == _cachedPaperMix ||
            (Mathf.Abs(paperMix - _cachedPaperMix) <= 0.002f && paperMix > 0f && paperMix < 1f)) return;
        _cachedPaperMix = paperMix;
        _globeMaterial?.SetShaderParameter("paper_mix", paperMix);
    }
}
