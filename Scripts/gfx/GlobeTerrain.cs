using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
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

    /// <summary>
    /// Everything the terrain needs that is decoding and arithmetic rather than an upload:
    /// the ground tiles loaded, resized, made seamless and mipmapped, the relief, colour and
    /// sea-floor pictures, and the lake and river surfaces. `Key` is what they were made from.
    /// </summary>
    private sealed class TerrainImages
    {
        public string Key = "";
        public Image[] Tiles = Array.Empty<Image>();
        public Image Height, Colour, Floor, Lake, River;
    }

    private Task<TerrainImages> _terrainImages;

    /// <summary>
    /// Start the terrain's pictures on worker threads, beside the political map.
    ///
    /// They used to be made on the main thread once the map had been built: twelve ground
    /// photographs decoded, resized, levelled and mipmapped one after another, then three
    /// world-sized pictures, while the loading screen stood still for two to three seconds
    /// with every other core idle. None of it touches the engine's servers - only the upload
    /// does, and that stays where it was.
    /// </summary>
    private void StartTerrainImages()
    {
        // The reduced atlas has no terrain/relief/water samplers. Do not decode,
        // mipmap and upload those pictures just to bind nonexistent uniforms.
        if (ReducedMap) return;
        if (_terrainImages is not null) return;
        var definitions = TerrainCatalog.All.ToArray();
        _terrainImages = Task.Run(() => PrepareTerrainImages(definitions));
    }

    /// <summary>What the terrain pictures are made from, so pictures made from something else are not used.</summary>
    private static string TerrainImagesKey(IReadOnlyList<TerrainDefinition> definitions)
    {
        var water = TerrainCatalog.Water;
        return string.Join("|", definitions.Select(d => d.Id + "=" + d.Texture + "#" + d.Color)) +
               $"|{TerrainCatalog.TileResolution}|{TerrainCatalog.SeamlessTiles}|{TerrainCatalog.HeightPath}|{TerrainCatalog.ReliefColorPath}|{TerrainCatalog.ReliefColorOpacity > 0f}|" +
               $"{TerrainCatalog.BathymetryPath}|{water.LakeTexture}|{water.RiverTexture}|{water.TileResolution}";
    }

    private static TerrainImages PrepareTerrainImages(IReadOnlyList<TerrainDefinition> definitions)
    {
        var prepared = new TerrainImages { Key = TerrainImagesKey(definitions), Tiles = new Image[definitions.Count] };
        // A tile to a core, leaving room for the map worker and the main thread.
        var options = new ParallelOptions { MaxDegreeOfParallelism = Math.Clamp(System.Environment.ProcessorCount - 2, 1, 6) };
        Parallel.For(0, definitions.Count, options, index => prepared.Tiles[index] = PrepareTerrainTile(definitions[index]));
        prepared.Height = ReliefImage(TerrainCatalog.HeightPath, new Color(.5f, .5f, .5f));
        // The relief's own colours are drawn at relief_color_opacity, and the shipped map draws
        // them at nought: the 5760-wide picture was decoded, given mipmaps and uploaded - 63 MB of
        // video memory and a second of loading - to be multiplied by zero. A map that shows them
        // (a mod raising the opacity) loads them; one that does not gets a texel of the fallback.
        prepared.Colour = TerrainCatalog.ReliefColorOpacity > 0f
            ? ReliefImage(TerrainCatalog.ReliefColorPath, new Color(.57f, .58f, .47f))
            : ReliefImage("", new Color(.57f, .58f, .47f));
        prepared.Floor = ReliefImage(TerrainCatalog.BathymetryPath, new Color(0f, 0f, 0f));
        var water = TerrainCatalog.Water;
        prepared.Lake = WaterTileImage(water.LakeTexture, water.TileResolution);
        prepared.River = WaterTileImage(water.RiverTexture, water.TileResolution);
        return prepared;
    }

    /// <summary>The pictures started beside the map when they are still the right ones, else made here and now.</summary>
    private TerrainImages TakeTerrainImages(IReadOnlyList<TerrainDefinition> definitions)
    {
        var task = _terrainImages;
        _terrainImages = null;
        if (task is not null)
        {
            try
            {
                var ready = task.Result;
                if (ready.Key == TerrainImagesKey(definitions) && ready.Tiles.Length == definitions.Count) return ready;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[dof] terrain pictures made in the background failed, making them here: {exception.GetBaseException().Message}");
            }
        }
        return PrepareTerrainImages(definitions);
    }

    private static Image PrepareTerrainTile(TerrainDefinition definition)
    {
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
        return tile;
    }

    // All allocations happen once when the map loads, not once per province per frame.
    // Texture arrays isolate each tile's mip chain: neighbouring atlas slots cannot bleed.
    private void ApplyTerrainPresentation(ProvinceTopology topology)
    {
        _crossoverGroundPixels = null;
        if (ReducedMap)
        {
            // Province terrain data stays intact for combat/marching. This byte
            // raster is only the optional visual tile surface, not province identity.
            topology.TerrainSurfaceSlots = Array.Empty<byte>();
            GD.Print("[dof] compatibility atlas: unused terrain/relief/water images were not decoded or uploaded");
            return;
        }
        if (TerrainCatalog.LoadError.Length > 0) GD.PushWarning("[dof] terrain catalog: " + TerrainCatalog.LoadError);
        var definitions = TerrainCatalog.All;
        var prepared = TakeTerrainImages(definitions);
        var images = new Godot.Collections.Array<Image>();
        var slots = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definitions.Count; index++)
        {
            slots[definitions[index].Id] = (byte)index;
            images.Add(prepared.Tiles[index]);
        }
        var tiles = new Texture2DArray();
        var error = tiles.CreateFromImages(images);
        if (error != Error.Ok) throw new InvalidDataException($"Terrain texture array: {error}");
        _globeMaterial.SetShaderParameter("terrain_tiles", tiles);
        images.Clear();
        for (var index = 0; index < prepared.Tiles.Length; index++) prepared.Tiles[index]?.Dispose();
        using (var surfaceImg = Image.CreateFromData(topology.Width, topology.Height, false, Image.Format.R8, topology.TerrainSurfaceSlots))
            _globeMaterial.SetShaderParameter("terrain_surface", ImageTexture.CreateFromImage(surfaceImg));
        topology.TerrainSurfaceSlots = Array.Empty<byte>();
        var lookup = new byte[256 * 256];
        for (var province = 1; province <= topology.ProvinceCount; province++)
            lookup[province] = slots[topology.TerrainAt(province).Id];
        using (var lookupImg = Image.CreateFromData(256, 256, false, Image.Format.R8, lookup))
            UploadMapImage("province_terrain", lookupImg);
        _globeMaterial.SetShaderParameter("relief_height", ImageTexture.CreateFromImage(prepared.Height));
        prepared.Height?.Dispose();
        _globeMaterial.SetShaderParameter("relief_color", ImageTexture.CreateFromImage(prepared.Colour));
        prepared.Colour?.Dispose();
        // The sea floor: a map without it is a flat navy sheet, one without shelves or coasts.
        var hasFloor = File.Exists(TerrainCatalog.BathymetryPath);
        _globeMaterial.SetShaderParameter("ocean_depth", ImageTexture.CreateFromImage(prepared.Floor));
        prepared.Floor?.Dispose();
        _globeMaterial.SetShaderParameter("ocean_depth_ready", hasFloor ? 1f : 0f);
        _globeMaterial.SetShaderParameter("terrain_detail", TerrainCatalog.DetailStrength);
        _globeMaterial.SetShaderParameter("relief_shading", TerrainCatalog.ReliefShading);
        _globeMaterial.SetShaderParameter("terrain_blend_texels", TerrainCatalog.TerrainBlendTexels);
        _globeMaterial.SetShaderParameter("political_opacity", TerrainCatalog.PoliticalOpacity);
        _globeMaterial.SetShaderParameter("terrain_repeat", TerrainCatalog.TileRepeat);
        _globeMaterial.SetShaderParameter("relief_strength", TerrainCatalog.ReliefStrength);
        _globeMaterial.SetShaderParameter("relief_color_opacity", TerrainCatalog.ReliefColorOpacity);
        _globeMaterial.SetShaderParameter("terrain_ready", 1f);
        ApplyMapVisualStyle();
        ApplyWaterPresentation(prepared);
        GD.Print($"[dof] terrain ready: {definitions.Count} types, {topology.ProvinceCount} provinces; {TerrainCatalog.TerrainPath}");
    }

    private static ImageTexture LoadRelief(string path, Color fallback) => ImageTexture.CreateFromImage(ReliefImage(path, fallback));

    private static Image ReliefImage(string path, Color fallback)
    {
        var image = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (image is null || image.IsEmpty())
        {
            image = Image.CreateEmpty(4, 2, false, Image.Format.Rgb8);
            image.Fill(fallback);
        }
        // Colour/height are presentation data, never province identity. Filtering is safe.
        image.GenerateMipmaps();
        return image;
    }
}
