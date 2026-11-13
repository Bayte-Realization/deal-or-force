using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// The map's lines, handed to the material.
///
/// What a country border, a region line, a province line and an occupation contour look like is
/// data (Simulation.MapBorderStyle, game/gui/77_map_borders.json), and this is the one place it
/// crosses into the shader. The full material, its packed Compatibility build and the safe
/// material declare the same uniform names, so all three are set by the same calls and cannot
/// drift apart; a material that does not declare one of them simply ignores it.
///
/// Called when the projection changes, because the flat map and the globe have their own
/// widths - never their own colours.
/// </summary>
public sealed partial class GlobePresentation
{
    private void ApplyMapBorderStyle()
    {
        if (_globeMaterial is null) return;
        var style = MapBorderStyles.For(_flatMap);
        _globeMaterial.SetShaderParameter("border_pixels", style.Country.Width);
        _globeMaterial.SetShaderParameter("country_border_color", Ink(style.Country.Colour));
        _globeMaterial.SetShaderParameter("country_border_opacity", style.Country.Opacity);
        _globeMaterial.SetShaderParameter("country_border_far_weight", style.CountryFarWeight);
        _globeMaterial.SetShaderParameter("region_border_pixels", style.Region.Width);
        _globeMaterial.SetShaderParameter("region_border_color", Ink(style.Region.Colour));
        _globeMaterial.SetShaderParameter("region_border_opacity", style.Region.Opacity);
        _globeMaterial.SetShaderParameter("province_border_pixels", style.Province.Width);
        _globeMaterial.SetShaderParameter("province_border_color", Ink(style.Province.Colour));
        _globeMaterial.SetShaderParameter("province_border_opacity", style.Province.Opacity);
        _globeMaterial.SetShaderParameter("province_halo_color", Ink(style.ProvinceHalo));
        _globeMaterial.SetShaderParameter("province_border_halo", style.ProvinceHaloOpacity);
        _globeMaterial.SetShaderParameter("occupation_line_color", Ink(style.Occupation));
        _globeMaterial.SetShaderParameter("occupation_key_color", Ink(style.OccupationKey));
    }

    // A plain vector, not a Color: the uniforms carry no source_color hint, so neither backend
    // runs a transfer curve over numbers that are already linear.
    private static Vector3 Ink(LinearColour colour) => new(colour.R, colour.G, colour.B);
}
