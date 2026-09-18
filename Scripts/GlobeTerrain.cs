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
            tile.Resize(256, 256, Image.Interpolation.Lanczos);
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
        _globeMaterial.SetShaderParameter("political_opacity", TerrainCatalog.PoliticalOpacity);
        _globeMaterial.SetShaderParameter("terrain_repeat", TerrainCatalog.TileRepeat);
        _globeMaterial.SetShaderParameter("relief_strength", TerrainCatalog.ReliefStrength);
        _globeMaterial.SetShaderParameter("relief_color_opacity", TerrainCatalog.ReliefColorOpacity);
        _globeMaterial.SetShaderParameter("terrain_ready", 1f);
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
