using System;
using System.IO;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    // Safe mode is an explicit escape hatch, not an automatic visual downgrade on
    // every integrated GPU. The normal Compatibility path retains the full material.
    private static bool ReducedMap => Array.IndexOf(OS.GetCmdlineUserArgs(), "--safe-map") >= 0;
    private Texture2DArray _packedLookups, _packedIds, _packedWater;

    private void InitializePackedTextures()
    {
        if (ReducedMap || RenderingServer.GetCurrentRenderingMethod() != "gl_compatibility") return;
        using var lookup = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
        _packedLookups = CreateArray(lookup, PackedMapShader.Lookups.Count);
        _globeMaterial.SetShaderParameter("packed_lookups", _packedLookups);
        using var ids = Image.CreateEmpty(2, 2, false, Image.Format.Rg8);
        _packedIds = CreateArray(ids, PackedMapShader.Ids.Count);
        _globeMaterial.SetShaderParameter("packed_ids", _packedIds);
    }

    /// <summary>Upload a layer directly, without allocating an unused 2D GPU copy.</summary>
    private bool UploadPacked(string uniform, Image image)
    {
        if (_packedLookups is null) return false;
        var layer = PackedMapShader.Layer(PackedMapShader.Lookups, uniform);
        if (layer >= 0)
        {
            if (image.GetWidth() != 256 || image.GetHeight() != 256 || image.HasMipmaps())
                throw new InvalidDataException("Map lookup must be 256x256 without mipmaps: " + uniform);
            if (image.GetFormat() == Image.Format.Rgba8) _packedLookups.UpdateLayer(image, layer);
            else
            {
                using var rgba = (Image)image.Duplicate();
                rgba.Convert(Image.Format.Rgba8);
                _packedLookups.UpdateLayer(rgba, layer);
            }
            return true;
        }
        layer = PackedMapShader.Layer(PackedMapShader.Ids, uniform);
        if (layer < 0) return false;
        if (image.GetFormat() != Image.Format.Rg8 || image.HasMipmaps())
            throw new InvalidDataException("Map IDs must remain lossless RG8: " + uniform);
        if (_packedIds.GetWidth() != image.GetWidth() || _packedIds.GetHeight() != image.GetHeight())
        {
            if (_packedIds.GetWidth() != 2)
                throw new InvalidDataException("Geographic ID layers must have identical dimensions.");
            using var blank = Image.CreateEmpty(image.GetWidth(), image.GetHeight(), false, Image.Format.Rg8);
            var previous = _packedIds;
            _packedIds = CreateArray(blank, PackedMapShader.Ids.Count);
            _globeMaterial.SetShaderParameter("packed_ids", _packedIds);
            previous.Dispose();
        }
        _packedIds.UpdateLayer(image, layer);
        return true;
    }

    private void UploadMapImage(string uniform, Image image)
    {
        if (!UploadPacked(uniform, image))
            _globeMaterial.SetShaderParameter(uniform, ImageTexture.CreateFromImage(image));
    }

    // Existing revision gates still decide WHEN a table changes. Both backends use
    // the same image and update on that revision, so peace/occupation/fog never freeze.
    private void UpdateMapImage(string uniform, Image image, ref ImageTexture texture)
    {
        if (UploadPacked(uniform, image)) return;
        if (texture is null)
        {
            texture = ImageTexture.CreateFromImage(image);
            _globeMaterial.SetShaderParameter(uniform, texture);
        }
        else texture.Update(image);
    }

    private static Texture2DArray CreateArray(Image image, int count)
    {
        var layers = new Godot.Collections.Array<Image>();
        // The server copies each layer synchronously; a single blank CPU image is
        // enough to initialize an array, rather than N equally large CPU buffers.
        for (var i = 0; i < count; i++) layers.Add(image);
        var texture = new Texture2DArray();
        var result = texture.CreateFromImages(layers);
        if (result != Error.Ok) { texture.Dispose(); throw new InvalidDataException("Map texture array: " + result); }
        return texture;
    }
}
