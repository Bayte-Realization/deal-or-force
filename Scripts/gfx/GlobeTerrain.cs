using System;
using System.Collections.Generic;
using System.IO;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

public sealed partial class GlobePresentation
{
    private static string TerrainDescription(TerrainDefinition t) =>
        "\n" + Loc.Format("{0} · march ×{1:0.##} · attack ×{2:0.##}",
            Loc.Get(t.Name), t.MovementSpeed, t.Attack) +
        "\n" + Loc.Format("Defense ×{0:0.##} · frontage ×{1:0.##} · supply ×{2:0.##}",
            t.Defense, t.CombatWidth, t.Supply);

    /// <summary>
    /// A tile that repeats without a seam.
    ///
    /// The ground photographs were cut out of larger pictures, so where one copy of a tile meets
    /// the next the ground jumps: a field lit from one side ends in a hard line against the same
    /// field lit from the other. Once per tile width that is a faint grid over the whole map;
    /// close in, where the detail layer repeats a tile every eight map texels, it is a grid of
    /// hard squares over every forest and farmland - as much a part of the pixelated look as
    /// any border was.
    ///
    /// Two steps, both on the tile as it loads, so the art on disk is never altered and a mod's
    /// own tiles are treated the same. The broad light and shade of the photograph is levelled
    /// first (a wide blur taken away and the tile's mean put back), because a seam between a
    /// bright edge and a dark one survives any blending and only moves. Then the tile is
    /// cross-faded with itself shifted by half: the shifted copy is continuous across the
    /// tile's edges, the original is kept whole in the middle, and the mix between them keeps
    /// the texture's contrast rather than averaging it into mush.
    /// </summary>
    private static Image SeamlessTile(Image tile)
    {
        var side = tile.GetWidth();
        if (side < 8 || tile.GetHeight() != side) return tile;
        var source = tile.GetData();
        var count = side * side;
        var level = new float[count * 3];
        var mean = new double[3];
        for (var i = 0; i < count; i++)
            for (var c = 0; c < 3; c++)
            {
                level[i * 3 + c] = source[i * 4 + c];
                mean[c] += source[i * 4 + c];
            }
        for (var c = 0; c < 3; c++) mean[c] /= count;

        // The broad light: a box blur a quarter of the tile wide, wrapping round the edges
        // as the tile itself will, applied twice to round it off.
        var broad = (float[])level.Clone();
        var radius = Math.Max(2, side / 8);
        for (var pass = 0; pass < 2; pass++)
        {
            broad = BoxBlurWrapped(broad, side, radius, horizontal: true);
            broad = BoxBlurWrapped(broad, side, radius, horizontal: false);
        }
        for (var i = 0; i < count; i++)
            for (var c = 0; c < 3; c++)
                level[i * 3 + c] += (float)mean[c] - broad[i * 3 + c];

        // Cross-fade with the half-shifted copy, weight one in the middle and none at the edges.
        var weight = new float[side];
        for (var x = 0; x < side; x++) weight[x] = MathF.Sin(MathF.PI * (x + 0.5f) / side);
        var half = side / 2;
        var result = new byte[count * 4];
        for (var y = 0; y < side; y++)
        for (var x = 0; x < side; x++)
        {
            var own = weight[x] * weight[y];
            var other = 1f - own;
            var norm = 1f / MathF.Sqrt(own * own + other * other);
            var i = y * side + x;
            var j = ((y + half) % side) * side + (x + half) % side;
            for (var c = 0; c < 3; c++)
            {
                var m = (float)mean[c];
                var value = m + ((level[i * 3 + c] - m) * own + (level[j * 3 + c] - m) * other) * norm;
                result[i * 4 + c] = (byte)Math.Clamp((int)MathF.Round(value), 0, 255);
            }
            result[i * 4 + 3] = source[i * 4 + 3];
        }
        return Image.CreateFromData(side, side, false, Image.Format.Rgba8, result);
    }

    /// <summary>A running-sum box blur along one axis of a square RGB field that wraps round.</summary>
    private static float[] BoxBlurWrapped(float[] field, int side, int radius, bool horizontal)
    {
        var output = new float[field.Length];
        var span = radius * 2 + 1;
        for (var line = 0; line < side; line++)
            for (var c = 0; c < 3; c++)
            {
                float sum = 0;
                for (var k = -radius; k <= radius; k++)
                    sum += field[Index(line, (k + side) % side) * 3 + c];
                for (var along = 0; along < side; along++)
                {
                    output[Index(line, along) * 3 + c] = sum / span;
                    var leaving = (along - radius + side) % side;
                    var entering = (along + radius + 1) % side;
                    sum += field[Index(line, entering) * 3 + c] - field[Index(line, leaving) * 3 + c];
                }
            }
        return output;

        int Index(int line, int along) => horizontal ? line * side + along : along * side + line;
    }

    // All allocations happen once when the map loads, not once per province per frame.
    // Texture arrays isolate each tile's mip chain: neighbouring atlas slots cannot bleed.
    private void ApplyTerrainPresentation(ProvinceTopology topology)
    {
        if (TerrainCatalog.LoadError.Length > 0) GD.PushWarning("[dof] terrain catalog: " + TerrainCatalog.LoadError);
        var definitions = TerrainCatalog.All;
        var images = new Godot.Collections.Array<Image>();
        var slots = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definitions.Count; index++)
        {
            var definition = definitions[index];
            slots[definition.Id] = (byte)index;
            var tile = File.Exists(definition.Texture) ? Image.LoadFromFile(definition.Texture) : null;
            if (tile is null || tile.IsEmpty())
            {
                GD.PushWarning($"[dof] terrain {definition.Id}: texture unavailable; using its map color (data root: {GameDataLoader.DataFolder()})");
                tile = Image.CreateEmpty(256, 256, false, Image.Format.Rgba8);
                tile.Fill(new Color($"#{definition.Color:X6}"));
            }
            tile.Convert(Image.Format.Rgba8);
            var side = Math.Clamp(TerrainCatalog.TileResolution, 64, 2048);
            tile.Resize(side, side, Image.Interpolation.Lanczos);
            if (TerrainCatalog.SeamlessTiles) tile = SeamlessTile(tile);
            tile.GenerateMipmaps();
            images.Add(tile);
        }
        var tiles = new Texture2DArray();
        var error = tiles.CreateFromImages(images);
        if (error != Error.Ok) throw new InvalidDataException($"Terrain texture array: {error}");
        _globeMaterial.SetShaderParameter("terrain_tiles", tiles);
        _globeMaterial.SetShaderParameter("terrain_surface", ImageTexture.CreateFromImage(
            Image.CreateFromData(topology.Width, topology.Height, false, Image.Format.R8, topology.TerrainSurfaceSlots)));
        var lookup = new byte[256 * 256];
        for (var province = 1; province <= topology.ProvinceCount; province++)
            lookup[province] = slots[topology.TerrainAt(province).Id];
        _globeMaterial.SetShaderParameter("province_terrain", ImageTexture.CreateFromImage(
            Image.CreateFromData(256, 256, false, Image.Format.R8, lookup)));
        _globeMaterial.SetShaderParameter("relief_height", LoadRelief(TerrainCatalog.HeightPath, new Color(.5f, .5f, .5f)));
        _globeMaterial.SetShaderParameter("relief_color", LoadRelief(TerrainCatalog.ReliefColorPath, new Color(.57f, .58f, .47f)));
        // The sea floor: a map without it is a flat navy sheet, one without shelves or coasts.
        var hasFloor = File.Exists(TerrainCatalog.BathymetryPath);
        _globeMaterial.SetShaderParameter("ocean_depth", LoadRelief(TerrainCatalog.BathymetryPath, new Color(0f, 0f, 0f)));
        _globeMaterial.SetShaderParameter("ocean_depth_ready", hasFloor ? 1f : 0f);
        _globeMaterial.SetShaderParameter("terrain_detail", TerrainCatalog.DetailStrength);
        _globeMaterial.SetShaderParameter("relief_shading", TerrainCatalog.ReliefShading);
        _globeMaterial.SetShaderParameter("terrain_blend_texels", TerrainCatalog.TerrainBlendTexels);
        _globeMaterial.SetShaderParameter("political_opacity", TerrainCatalog.PoliticalOpacity);
        _globeMaterial.SetShaderParameter("terrain_repeat", TerrainCatalog.TileRepeat);
        _globeMaterial.SetShaderParameter("relief_strength", TerrainCatalog.ReliefStrength);
        _globeMaterial.SetShaderParameter("relief_color_opacity", TerrainCatalog.ReliefColorOpacity);
        _globeMaterial.SetShaderParameter("terrain_ready", 1f);
        ApplyWaterPresentation();
        GD.Print($"[dof] terrain ready: {definitions.Count} types, {topology.ProvinceCount} provinces; {TerrainCatalog.TerrainPath}");
    }

    private static ImageTexture LoadRelief(string path, Color fallback)
    {
        var image = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (image is null || image.IsEmpty())
        {
            image = Image.CreateEmpty(4, 2, false, Image.Format.Rgb8);
            image.Fill(fallback);
        }
        // Colour/height are presentation data, never province identity. Filtering is safe.
        image.GenerateMipmaps();
        return ImageTexture.CreateFromImage(image);
    }
}
