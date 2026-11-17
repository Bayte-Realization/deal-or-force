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
        if (_globeMaterial is null) return;
        var style = _mapLookStyle;
        _globeMaterial.SetShaderParameter("atlas_look", _mapLookAtlas ? 1f : 0f);
        _globeMaterial.SetShaderParameter("look_paper", Ink(style.Paper));
        _globeMaterial.SetShaderParameter("look_paper_grain", style.PaperGrain);
        _globeMaterial.SetShaderParameter("look_shade_mul", Ink(MapLook.ScaledTo(style.Shade, MapLook.ShadeLuminance)));
        _globeMaterial.SetShaderParameter("look_lamp_mul", Ink(MapLook.ScaledTo(style.Lamp, MapLook.LampLuminance)));
        _globeMaterial.SetShaderParameter("look_lamp", Ink(style.Lamp));
        _globeMaterial.SetShaderParameter("look_sea_deep", Ink(style.SeaDeep));
        _globeMaterial.SetShaderParameter("look_sea_mid", Ink(style.SeaMid));
        _globeMaterial.SetShaderParameter("look_sea_shelf", Ink(style.SeaShelf));
        _globeMaterial.SetShaderParameter("look_sea_coast", Ink(style.SeaCoast));
        _globeMaterial.SetShaderParameter("look_sea_chart", Ink(style.SeaChart));
        _globeMaterial.SetShaderParameter("look_foam", Ink(style.Foam));
        _globeMaterial.SetShaderParameter("look_coast_ink", Ink(style.CoastInk));
        _globeMaterial.SetShaderParameter("look_cover_near", style.CoverNear);
        _globeMaterial.SetShaderParameter("look_flat_cover_near", style.FlatCoverNear);
        _globeMaterial.SetShaderParameter("look_cover_far", style.CoverFar);
        _globeMaterial.SetShaderParameter("look_saturation_far", style.SaturationFar);
        _globeMaterial.SetShaderParameter("look_relief_far", style.ReliefFar);
        _globeMaterial.SetShaderParameter("look_border_wobble", style.BorderWobble);
        _globeMaterial.SetShaderParameter("look_far_band", style.FarBand);
        _globeMaterial.SetShaderParameter("look_foam_strength", style.FoamStrength);
        _globeMaterial.SetShaderParameter("look_waterline", style.Waterline);
        _globeMaterial.SetShaderParameter("look_sea_sheen", style.SeaSheen);
        _globeMaterial.SetShaderParameter("look_selection_halo", style.SelectionHalo);
    }

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
