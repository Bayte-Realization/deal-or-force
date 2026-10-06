using System;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private void ApplyWaterPresentation()
    {
        var water = TerrainCatalog.Water;
        _globeMaterial.SetShaderParameter("lake_surface", LoadWaterTile(water.LakeTexture, water.TileResolution));
        _globeMaterial.SetShaderParameter("river_surface", LoadWaterTile(water.RiverTexture, water.TileResolution));
        _globeMaterial.SetShaderParameter("river_inset", water.RiverInset);
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

    private static ImageTexture LoadWaterTile(string path, int resolution)
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
        return ImageTexture.CreateFromImage(image);
    }
}
