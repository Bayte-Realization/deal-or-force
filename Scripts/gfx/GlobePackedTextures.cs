using System;
using System.IO;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    // Safe mode is an explicit escape hatch, not an automatic visual downgrade on
    // every integrated GPU. The normal Compatibility and Mobile paths retain the full material.
    private static bool ReducedMap => Array.IndexOf(OS.GetCmdlineUserArgs(), "--safe-map") >= 0;
    internal static bool UsePackedMapShader =>
        !ReducedMap && RenderingServer.GetCurrentRenderingMethod() is "gl_compatibility" or "mobile";
    private Texture2DArray _packedLookups, _packedIds, _packedWater;
    private Image _packedLookupRgbaScratch;
    private byte[] _packedLookupRgbaBytes;

    private void InitializePackedTextures()
    {
        if (!UsePackedMapShader) return;
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
            else if (image.GetFormat() == Image.Format.R8)
            {
                var src = image.GetData();
                var dst = _packedLookupRgbaBytes ??= new byte[256 * 256 * 4];
                for (int s = 0, d = 0; s < src.Length && d + 3 < dst.Length; s++, d += 4)
                {
                    var v = src[s];
                    // Match Godot's Image.Convert(R8 -> Rgba8) layout (R=v, G=0, B=0, A=255).
                    dst[d] = v; dst[d + 1] = 0; dst[d + 2] = 0; dst[d + 3] = 255;
                }
                if (_packedLookupRgbaScratch is null)
                    _packedLookupRgbaScratch = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, dst);
                else
                    _packedLookupRgbaScratch.SetData(256, 256, false, Image.Format.Rgba8, dst);
                _packedLookups.UpdateLayer(_packedLookupRgbaScratch, layer);
            }
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
            _packedIds = CreateArray(blank, PackedMapShader.Ids.Count);
            _globeMaterial.SetShaderParameter("packed_ids", _packedIds);
        }
        _packedIds.UpdateLayer(image, layer);
        return true;
    }

    /// <summary>Create the 3-layer geographic ID array directly from its real images with zero blank upload.</summary>
    private bool UploadPackedIds(Image regionIds, Image provinceIds, Image nationIds)
    {
        if (_packedLookups is null) return false;
        if (regionIds.GetFormat() != Image.Format.Rg8 || regionIds.HasMipmaps() ||
            provinceIds.GetFormat() != Image.Format.Rg8 || provinceIds.HasMipmaps() ||
            nationIds.GetFormat() != Image.Format.Rg8 || nationIds.HasMipmaps())
            throw new InvalidDataException("Map IDs must remain lossless RG8.");
        if (regionIds.GetWidth() != provinceIds.GetWidth() || regionIds.GetHeight() != provinceIds.GetHeight() ||
            regionIds.GetWidth() != nationIds.GetWidth() || regionIds.GetHeight() != nationIds.GetHeight())
            throw new InvalidDataException("Geographic ID layers must have identical dimensions.");

        var layers = new Godot.Collections.Array<Image> { regionIds, provinceIds, nationIds };
        var created = new Texture2DArray();
        var result = created.CreateFromImages(layers);
        if (result != Error.Ok) throw new InvalidDataException("Map ID texture array: " + result);
        _packedIds = created;
        _globeMaterial.SetShaderParameter("packed_ids", created);
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
        if (texture is not null && IsInstanceValid(texture) &&
            texture.GetWidth() == image.GetWidth() &&
            texture.GetHeight() == image.GetHeight() &&
            texture.GetFormat() == image.GetFormat())
        {
            texture.Update(image);
        }
        else
        {
            texture = ImageTexture.CreateFromImage(image);
            _globeMaterial.SetShaderParameter(uniform, texture);
        }
    }

    private static Texture2DArray CreateArray(Image image, int count)
    {
        var layers = new Godot.Collections.Array<Image>();
        // The server copies each layer synchronously; a single blank CPU image is
        // enough to initialize an array, rather than N equally large CPU buffers.
        for (var i = 0; i < count; i++) layers.Add(image);
        var texture = new Texture2DArray();
        var result = texture.CreateFromImages(layers);
        if (result != Error.Ok) throw new InvalidDataException("Map texture array: " + result);
        return texture;
    }
}
