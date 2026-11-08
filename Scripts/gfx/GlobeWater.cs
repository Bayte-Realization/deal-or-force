using System;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private void ApplyWaterPresentation(TerrainImages prepared = null)
    {
        var water = TerrainCatalog.Water;
        using var lake = prepared?.Lake ?? WaterTileImage(water.LakeTexture, water.TileResolution);
        using var river = prepared?.River ?? WaterTileImage(water.RiverTexture, water.TileResolution);
        if (_packedLookups is not null)
        {
            var layers = new Godot.Collections.Array<Image> { lake, river };
            var texture = new Texture2DArray();
            var error = texture.CreateFromImages(layers);
            if (error != Error.Ok) { texture.Dispose(); throw new InvalidDataException("Packed water tiles: " + error); }
            var previous = _packedWater;
            _packedWater = texture;
            _globeMaterial.SetShaderParameter("packed_water_tiles", texture);
            previous?.Dispose();
        }
        else
        {
            _globeMaterial.SetShaderParameter("lake_surface", ImageTexture.CreateFromImage(lake));
            _globeMaterial.SetShaderParameter("river_surface", ImageTexture.CreateFromImage(river));
        }
        _globeMaterial.SetShaderParameter("river_inset", water.RiverInset);
        _globeMaterial.SetShaderParameter("river_curve_filter",
            Math.Clamp(InterfaceSkin.Metric("map.river_curve_filter", 1f), 0f, 1f));
        _globeMaterial.SetShaderParameter("river_near_opacity", water.RiverNearOpacity);
        _globeMaterial.SetShaderParameter("river_far_opacity", water.RiverFarOpacity);
        _globeMaterial.SetShaderParameter("water_shore_width", water.ShoreWidth);
        _globeMaterial.SetShaderParameter("water_depth_reach", water.DepthReach);
        _globeMaterial.SetShaderParameter("water_texture_strength", water.TextureStrength);
        _globeMaterial.SetShaderParameter("water_repeat", water.TileRepeat);
        _globeMaterial.SetShaderParameter("water_ripple_strength", water.RippleStrength);
        _globeMaterial.SetShaderParameter("water_animation_speed", water.AnimationSpeed);
        GD.Print($"[dof] inland water materials ready: inset {water.RiverInset:0.##} texels, {water.TileResolution}px tiles");
    }

    private static ImageTexture LoadWaterTile(string path, int resolution) => ImageTexture.CreateFromImage(WaterTileImage(path, resolution));

    private static Image WaterTileImage(string path, int resolution)
    {
        var image = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (image is null || image.IsEmpty())
        {
            GD.PushWarning($"[dof] water tile unavailable: {path}; using a quiet neutral material");
            image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            image.Fill(new Color(.08f, .24f, .29f));
        }
        image.Convert(Image.Format.Rgba8);
        image.Resize(resolution, resolution, Image.Interpolation.Lanczos);
        image.GenerateMipmaps();
        return image;
    }
}
