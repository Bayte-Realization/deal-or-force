using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The atlas look, handed to the material (Simulation.MapLook, game/gui/78_map_look.json).
///
/// Every new term in the map shader stands behind the uniform atlas_look, which defaults to
/// nought: a material nobody has configured, and the classic setting, both draw the map exactly
/// as it was. The one per-frame number, paper_mix, is worked out in _Process from the camera
/// alone - O(1), nothing that grows with provinces or cities - and sent only when it moves.
/// </summary>
public sealed partial class GlobePresentation
{
    private MapLookStyle _mapLookStyle;
    private bool _mapLookAtlas;
    private float _cachedPaperMix = -1f;

    /// <summary>Reads the setting and the skin, and sets every uniform of the look. Cheap; not per frame.</summary>
    private void ApplyMapLook()
    {
        _mapLookStyle = MapLook.Current();
        _mapLookAtlas = MapLook.Atlas();
        // Resend paper_mix on the next frame: switching to classic sends nought, back sends the zoom's.
        _cachedPaperMix = -1f;
        ApplyAtmosphereLook();
        InvalidateCities();
        InvalidateCountryAndRegionLabelAppearance();
        if (_globeMaterial is null) return;
        var style = _mapLookStyle;
        _globeMaterial.SetShaderParameter("atlas_look", _mapLookAtlas ? 1f : 0f);
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
        // Compatibility has no texture unit left for the frontier field, so its read there is a
        // constant meaning "inland" (PackedMapShader). With frontier_ready at one every country
        // line faded out from the middle zoom (GlobePlateRepaint.ApplyFrontier). The atlas keeps
        // it at nought there, so the line is rebuilt per pixel, as the packed shader intends,
        // out to the zoom where a pixel holds about two cells; beyond that the rebuilt line would
        // only alias, so it fades, and the far frontiers are the ones painted into the plate.
        // Classic keeps what it always did.
        if (_packedLookups is not null)
        {
            _globeMaterial.SetShaderParameter("frontier_ready",
                !_mapLookAtlas && _frontierTexture is not null ? 1f : 0f);
            // Under the atlas the field is never built there (FrontierFieldWanted), so a switch to
            // classic in the middle of a game would leave classic without it - drawing differently
            // from a classic cold start - until some border happened to move. Build it now: the
            // plate repaint makes the field on its worker, from the same owners, once.
            if (!_mapLookAtlas && _frontierTexture is null && _frontierAuthored is not null &&
                _campaign?.ProvinceMap is { } topology)
            {
                _plateColoursStale = true;
                RequestPlateRepaint(topology);
            }
        }
    }

    /// <summary>
    /// Whether the frontier field is worth building: everywhere but Compatibility under the atlas
    /// look, where the shader cannot read it and the 22-megabyte picture (with mipmaps) would be built and
    /// uploaded for nothing.
    /// </summary>
    private bool FrontierFieldWanted() => _packedLookups is null || !MapLook.Atlas();

    /// <summary>Per frame from _Process: the paper share, sent only when it moves by more than 0.002.</summary>
    private void UpdatePaperMix()
    {
        if (_mapLookStyle is null) return;
        var paperMix = _mapLookAtlas
            ? MapLook.PaperMix(_flatMap, _flatMap ? _flatZoom : _distance, _mapLookStyle)
            : 0f;
        // The two ends are always sent exactly: the shader skips the terrain only above 0.999,
        // and a camera that crept there in steps under the threshold would never get it.
        if (paperMix == _cachedPaperMix ||
            (Mathf.Abs(paperMix - _cachedPaperMix) <= 0.002f && paperMix > 0f && paperMix < 1f)) return;
        _cachedPaperMix = paperMix;
        _globeMaterial?.SetShaderParameter("paper_mix", paperMix);
    }
}
