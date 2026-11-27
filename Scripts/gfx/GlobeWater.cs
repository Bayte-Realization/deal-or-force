using System;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private ImageTexture _lakeSurfaceTexture;
    private ImageTexture _riverSurfaceTexture;

    private void ApplyWaterPresentation(TerrainImages prepared = null)
    {
        var water = TerrainCatalog.Water;
        var isTouchPrimary = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        using var lake = prepared?.Lake ?? WaterTileImageOrResource(water.LakeTexture, water.LakeRelative, water.TileResolution, isTouchPrimary);
        using var river = prepared?.River ?? WaterTileImageOrResource(water.RiverTexture, water.RiverRelative, water.TileResolution, isTouchPrimary);
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
            UpdateWaterTileTexture(ref _lakeSurfaceTexture, "lake_surface", lake);
            UpdateWaterTileTexture(ref _riverSurfaceTexture, "river_surface", river);
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
        GD.Print($"[dof] inland water materials ready: inset {water.RiverInset:0.##} texels, {(isTouchPrimary ? Math.Clamp(water.TileResolution, 64, 128) : water.TileResolution)}px tiles");
    }

    private static ImageTexture LoadWaterTile(string path, int resolution)
    {
        using var img = WaterTileImage(path, resolution);
        return ImageTexture.CreateFromImage(img);
    }

    private static Image WaterTileImageOrResource(string path, string relative, int resolution, bool isTouchPrimary)
    {
        var image = File.Exists(path) ? Image.LoadFromFile(path) : TryLoadResourceImageCopy(relative);
        return FinalizeWaterTileImage(image, path, resolution, isTouchPrimary);
    }

    private static Image WaterTileImage(string path, int resolution, bool isTouchPrimary = false)
    {
        var image = File.Exists(path) ? Image.LoadFromFile(path) : null;
        return FinalizeWaterTileImage(image, path, resolution, isTouchPrimary);
    }

    private static Image FinalizeWaterTileImage(Image image, string path, int resolution, bool isTouchPrimary)
    {
        if (image is null || image.IsEmpty())
        {
            image?.Dispose();
            GD.PushWarning($"[dof] water tile unavailable: {path}; using a quiet neutral material");
            image = Image.CreateEmpty(4, 4, false, Image.Format.Rgba8);
            image.Fill(new Color(.08f, .24f, .29f));
        }
        if (image.IsCompressed()) image.Decompress();
        if (image.GetFormat() != Image.Format.Rgba8) image.Convert(Image.Format.Rgba8);
        var side = isTouchPrimary ? Math.Clamp(resolution, 64, 128) : resolution;
        var interp = isTouchPrimary ? Image.Interpolation.Bilinear : Image.Interpolation.Lanczos;
        if (image.GetWidth() != side || image.GetHeight() != side)
            image.Resize(side, side, interp);
        image.GenerateMipmaps();
        return image;
    }

    private void UpdateWaterTileTexture(ref ImageTexture texture, string uniform, Image image)
    {
        if (texture is not null && IsInstanceValid(texture) &&
            texture.GetWidth() == image.GetWidth() &&
            texture.GetHeight() == image.GetHeight() &&
            texture.GetFormat() == image.GetFormat() &&
            texture.HasMipmaps() == image.HasMipmaps())
        {
            texture.Update(image);
        }
        else
        {
            texture = ImageTexture.CreateFromImage(image);
            _globeMaterial.SetShaderParameter(uniform, texture);
        }
    }
}
