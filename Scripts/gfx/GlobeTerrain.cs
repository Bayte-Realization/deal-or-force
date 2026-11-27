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
        var activeProfile = Platform.PlatformProfile.Current;
        var isTouchPrimary = activeProfile?.IsTouchPrimary == true || ShouldUseHalfResMobileMapTextures(activeProfile);
        var cacheDir = Path.Combine(GamePaths.Root, "cache");
        var shippedCache = GameDataLoader.FindDataFile("cache", MobileTerrainCacheFile);
        _terrainImages = Task.Run(() => PrepareTerrainImages(definitions, isTouchPrimary, cacheDir, shippedCache));
    }

    /// <summary>What the terrain pictures are made from, so pictures made from something else are not used.</summary>
    private static string TerrainImagesKey(IReadOnlyList<TerrainDefinition> definitions, bool isTouchPrimary)
    {
        var water = TerrainCatalog.Water;
        return string.Join("|", definitions.Select(d => d.Id + "=" + d.Texture + "#" + d.Color)) +
               $"|{TerrainCatalog.TileResolution}|{TerrainCatalog.SeamlessTiles}|{isTouchPrimary}|{TerrainCatalog.HeightPath}|{TerrainCatalog.ReliefColorPath}|{TerrainCatalog.ReliefColorOpacity > 0f}|" +
               $"{TerrainCatalog.BathymetryPath}|{water.LakeTexture}|{water.RiverTexture}|{water.TileResolution}";
    }

    private static TerrainImages PrepareTerrainImages(
        IReadOnlyList<TerrainDefinition> definitions,
        bool isTouchPrimary,
        string cacheDirectory = "",
        string shippedCachePath = "")
    {
        var key = TerrainImagesKey(definitions, isTouchPrimary);
        var water = TerrainCatalog.Water;
        if (isTouchPrimary && !ModOverlayPolicy.IsActive &&
            TryReadMobileTerrainCache(cacheDirectory, shippedCachePath, key, definitions.Count, out var cached))
        {
            cached.Height = ReliefImage("", new Color(.5f, .5f, .5f));
            cached.Colour = ReliefImage("", new Color(.57f, .58f, .47f));
            cached.Floor = ReliefImage("", new Color(0f, 0f, 0f));
            return cached;
        }

        var prepared = new TerrainImages { Key = key, Tiles = new Image[definitions.Count] };
        var rawTiles = isTouchPrimary && !ModOverlayPolicy.IsActive ? new byte[definitions.Count + 2][] : null;
        var tileSides = isTouchPrimary && !ModOverlayPolicy.IsActive ? new int[definitions.Count + 2] : null;

        // Bounded parallelism keeps Windows OS/DWM/audio responsive on 16-32 core CPUs and prevents RAM spikes on mobile.
        var maxPar = isTouchPrimary
            ? Math.Clamp(System.Environment.ProcessorCount, 1, 2)
            : Math.Clamp(System.Environment.ProcessorCount / 2, 1, 4);
        var options = new ParallelOptions { MaxDegreeOfParallelism = maxPar };
        Parallel.For(0, definitions.Count, options, index =>
        {
            var img = PrepareTerrainTile(definitions[index], isTouchPrimary, out var raw, out var side);
            prepared.Tiles[index] = img;
            if (rawTiles is not null && raw is not null)
            {
                rawTiles[index] = raw;
                tileSides![index] = side;
            }
        });
        // On mobile/touch hardware (Android & BlueStacks), skip decoding three 5760x2880 relief/bathymetry
        // PNGs (+mipmaps + >120 MiB VRAM) so terrain boot stays under 1.5s. Desktop PC keeps full 5760px relief.
        prepared.Height = isTouchPrimary
            ? ReliefImage("", new Color(.5f, .5f, .5f))
            : ReliefImage(TerrainCatalog.HeightPath, new Color(.5f, .5f, .5f));
        // The relief's own colours are drawn at relief_color_opacity, and the shipped map draws
        // them at nought: the 5760-wide picture was decoded, given mipmaps and uploaded - 63 MB of
        // video memory and a second of loading - to be multiplied by zero. A map that shows them
        // (a mod raising the opacity) loads them; one that does not gets a texel of the fallback.
        prepared.Colour = !isTouchPrimary && TerrainCatalog.ReliefColorOpacity > 0f
            ? ReliefImage(TerrainCatalog.ReliefColorPath, new Color(.57f, .58f, .47f))
            : ReliefImage("", new Color(.57f, .58f, .47f));
        prepared.Floor = isTouchPrimary
            ? ReliefImage("", new Color(0f, 0f, 0f))
            : ReliefImage(TerrainCatalog.BathymetryPath, new Color(0f, 0f, 0f));
        prepared.Lake = PrepareWaterTileFromDisk(water.LakeTexture, water.TileResolution, isTouchPrimary, out var lakeRaw, out var lakeSide);
        prepared.River = PrepareWaterTileFromDisk(water.RiverTexture, water.TileResolution, isTouchPrimary, out var riverRaw, out var riverSide);
        if (rawTiles is not null)
        {
            if (lakeRaw is not null) { rawTiles[definitions.Count] = lakeRaw; tileSides![definitions.Count] = lakeSide; }
            if (riverRaw is not null) { rawTiles[definitions.Count + 1] = riverRaw; tileSides![definitions.Count + 1] = riverSide; }
            TryWriteMobileTerrainCache(cacheDirectory, key, rawTiles, tileSides!);
        }
        return prepared;
    }

    private static TerrainImages PrepareTerrainImages(IReadOnlyList<TerrainDefinition> definitions, bool isTouchPrimary) =>
        PrepareTerrainImages(definitions, isTouchPrimary, "", "");

    /// <summary>The pictures started beside the map when they are still the right ones, else made here and now.</summary>
    private TerrainImages TakeTerrainImages(IReadOnlyList<TerrainDefinition> definitions, bool isTouchPrimary)
    {
        var task = _terrainImages;
        _terrainImages = null;
        TerrainImages result = null;
        if (task is not null)
        {
            try
            {
                var ready = task.Result;
                if (ready.Key == TerrainImagesKey(definitions, isTouchPrimary) && ready.Tiles.Length == definitions.Count)
                    result = ready;
            }
            catch (Exception exception)
            {
                GD.PushWarning($"[dof] terrain pictures made in the background failed, making them here: {exception.GetBaseException().Message}");
            }
        }
        if (result is null)
        {
            var cacheDir = Path.Combine(GamePaths.Root, "cache");
            var shippedCache = GameDataLoader.FindDataFile("cache", MobileTerrainCacheFile);
            result = PrepareTerrainImages(definitions, isTouchPrimary, cacheDir, shippedCache);
        }
        for (var i = 0; i < definitions.Count; i++)
        {
            if (result.Tiles[i] is not null) continue;
            var resImg = TryLoadResourceImageCopy(definitions[i].TextureRelative);
            result.Tiles[i] = FinalizeTerrainTileImage(resImg, definitions[i], isTouchPrimary, out _, out _);
        }
        if (result.Lake is null)
            result.Lake = WaterTileImageOrResource(TerrainCatalog.Water.LakeTexture, TerrainCatalog.Water.LakeRelative, TerrainCatalog.Water.TileResolution, isTouchPrimary);
        if (result.River is null)
            result.River = WaterTileImageOrResource(TerrainCatalog.Water.RiverTexture, TerrainCatalog.Water.RiverRelative, TerrainCatalog.Water.TileResolution, isTouchPrimary);
        return result;
    }

    private static Image PrepareTerrainTile(TerrainDefinition definition, bool isTouchPrimary, out byte[] rawRgba, out int outSide)
    {
        var tile = File.Exists(definition.Texture) ? Image.LoadFromFile(definition.Texture) : null;
        if (tile is null && !File.Exists(definition.Texture))
        {
            // Return null when disk asset is missing so TakeTerrainImages can fill from res:// on main thread.
            rawRgba = null;
            outSide = 0;
            return null;
        }
        return FinalizeTerrainTileImage(tile, definition, isTouchPrimary, out rawRgba, out outSide);
    }

    private static Image FinalizeTerrainTileImage(Image tile, TerrainDefinition definition, bool isTouchPrimary, out byte[] rawRgba, out int outSide)
    {
        if (tile is null || tile.IsEmpty())
        {
            tile?.Dispose();
            GD.PushWarning($"[dof] terrain {definition.Id}: texture unavailable; using its map color (data root: {GameDataLoader.DataFolder()})");
            tile = Image.CreateEmpty(isTouchPrimary ? 128 : 256, isTouchPrimary ? 128 : 256, false, Image.Format.Rgba8);
            tile.Fill(new Color($"#{definition.Color:X6}"));
        }
        if (tile.IsCompressed()) tile.Decompress();
        if (tile.GetFormat() != Image.Format.Rgba8) tile.Convert(Image.Format.Rgba8);
        // On touch/mobile hardware cap tile resolution at 128, use fast Bilinear interpolation,
        // and skip the heavy CPU SeamlessTile blur/crossfade pass across 12 ground photographs.
        var side = Math.Clamp(TerrainCatalog.TileResolution, 64, isTouchPrimary ? 128 : 2048);
        var interp = isTouchPrimary ? Image.Interpolation.Bilinear : Image.Interpolation.Lanczos;
        if (tile.GetWidth() != side || tile.GetHeight() != side)
            tile.Resize(side, side, interp);
        if (TerrainCatalog.SeamlessTiles && !isTouchPrimary)
        {
            var seamless = SeamlessTile(tile);
            tile.Dispose();
            tile = seamless;
        }
        outSide = tile.GetWidth();
        rawRgba = isTouchPrimary && tile.GetFormat() == Image.Format.Rgba8 && !tile.HasMipmaps() ? tile.GetData() : null;
        tile.GenerateMipmaps();
        return tile;
    }

    private static Image PrepareWaterTileFromDisk(string path, int resolution, bool isTouchPrimary, out byte[] rawRgba, out int outSide)
    {
        if (!File.Exists(path))
        {
            rawRgba = null;
            outSide = 0;
            return null;
        }
        var img = WaterTileImage(path, resolution, isTouchPrimary);
        outSide = img.GetWidth();
        rawRgba = isTouchPrimary && img.GetFormat() == Image.Format.Rgba8 ? ExtractLevel0Rgba(img, outSide) : null;
        return img;
    }

    private static byte[] ExtractLevel0Rgba(Image img, int side)
    {
        var need = side * side * 4;
        var data = img.GetData();
        if (data.Length == need) return data;
        if (data.Length > need)
        {
            var level0 = new byte[need];
            Buffer.BlockCopy(data, 0, level0, 0, need);
            return level0;
        }
        return null;
    }

    private static Image TryLoadResourceImageCopy(string relative)
    {
        if (string.IsNullOrWhiteSpace(relative)) return null;
        var resPath = relative.StartsWith("res://", StringComparison.OrdinalIgnoreCase)
            ? relative
            : "res://game/" + relative.TrimStart('/');
        if (!ResourceLoader.Exists(resPath)) return null;
        var texture = GD.Load<Texture2D>(resPath);
        var image = texture?.GetImage();
        return image is null || image.IsEmpty() ? null : image;
    }

    private const string MobileTerrainCacheFile = "terrain_mobile_v1.bin";
    private const uint MobileTerrainCacheMagic = 0x43544D44u; // DMTC

    private static ulong HashTerrainKey(string key)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < key.Length; i++)
        {
            var ch = key[i];
            hash = (hash ^ (byte)ch) * 1099511628211UL;
            hash = (hash ^ (byte)(ch >> 8)) * 1099511628211UL;
        }
        return hash;
    }

    private static ulong HashBytesFnv1a(byte[] data)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < data.Length; i++)
            hash = (hash ^ data[i]) * 1099511628211UL;
        return hash;
    }

    private static bool TryReadMobileTerrainCache(string cacheDir, string shippedPath, string key, int defCount, out TerrainImages prepared)
    {
        prepared = null;
        var total = defCount + 2;
        var keyHash = HashTerrainKey(key);
        foreach (var path in new[]
        {
            string.IsNullOrWhiteSpace(cacheDir) ? "" : Path.Combine(cacheDir, MobileTerrainCacheFile),
            shippedPath ?? ""
        })
        {
            if (string.IsNullOrWhiteSpace(path) || !File.Exists(path)) continue;
            try
            {
                using var fs = new FileStream(path, FileMode.Open, System.IO.FileAccess.Read, FileShare.Read);
                using var br = new BinaryReader(fs);
                if (br.ReadUInt32() != MobileTerrainCacheMagic || br.ReadInt32() != 1) continue;
                if (br.ReadUInt64() != keyHash || br.ReadInt32() != total) continue;
                var expectedCsum = br.ReadUInt64();
                var compLen = br.ReadInt32();
                if (compLen <= 0 || compLen > 64 * 1024 * 1024) continue;
                var compressed = br.ReadBytes(compLen);
                if (compressed.Length != compLen) continue;
                var raw = K4os.Compression.LZ4.LZ4Pickler.Unpickle(compressed);
                if (HashBytesFnv1a(raw) != expectedCsum) continue;
                using var ms = new MemoryStream(raw, writable: false);
                using var pr = new BinaryReader(ms);
                var result = new TerrainImages { Key = key, Tiles = new Image[defCount] };
                try
                {
                    for (var i = 0; i < total; i++)
                    {
                        var side = pr.ReadInt32();
                        var len = pr.ReadInt32();
                        if (side <= 0 || len != side * side * 4) throw new InvalidDataException("corrupt mobile tile");
                        var pixels = pr.ReadBytes(len);
                        var img = Image.CreateFromData(side, side, false, Image.Format.Rgba8, pixels);
                        img.GenerateMipmaps();
                        if (i < defCount) result.Tiles[i] = img;
                        else if (i == defCount) result.Lake = img;
                        else result.River = img;
                    }
                    prepared = result;
                    return true;
                }
                catch
                {
                    foreach (var t in result.Tiles) t?.Dispose();
                    Array.Clear(result.Tiles);
                    result.Lake?.Dispose();
                    result.Lake = null;
                    result.River?.Dispose();
                    result.River = null;
                    result.River = null;
                    throw;
                }
            }
            catch { /* Fall back cleanly to source PNGs if cache invalid */ }
        }
        return false;
    }

    private static void TryWriteMobileTerrainCache(string cacheDir, string key, byte[][] rawTiles, int[] sides)
    {
        if (string.IsNullOrWhiteSpace(cacheDir)) return;
        for (var i = 0; i < rawTiles.Length; i++)
            if (rawTiles[i] is null || sides[i] <= 0) return;
        try
        {
            Directory.CreateDirectory(cacheDir);
            var target = Path.Combine(cacheDir, MobileTerrainCacheFile);
            var tmp = target + ".tmp." + System.Environment.ProcessId;
            byte[] payload;
            using (var ms = new MemoryStream())
            using (var bw = new BinaryWriter(ms))
            {
                for (var i = 0; i < rawTiles.Length; i++)
                {
                    bw.Write(sides[i]);
                    bw.Write(rawTiles[i].Length);
                    bw.Write(rawTiles[i]);
                }
                bw.Flush();
                payload = ms.ToArray();
            }
            var compressed = K4os.Compression.LZ4.LZ4Pickler.Pickle(payload);
            var csum = HashBytesFnv1a(payload);
            using (var fs = new FileStream(tmp, FileMode.Create, System.IO.FileAccess.Write, FileShare.None))
            using (var hw = new BinaryWriter(fs))
            {
                hw.Write(MobileTerrainCacheMagic);
                hw.Write(1);
                hw.Write(HashTerrainKey(key));
                hw.Write(rawTiles.Length);
                hw.Write(csum);
                hw.Write(compressed.Length);
                hw.Write(compressed);
            }
            File.Move(tmp, target, overwrite: true);
        }
        catch { /* Non-fatal cache write error */ }
    }

    private ImageTexture _terrainSurfaceTexture;
    private ImageTexture _reliefColorTexture;
    private ImageTexture _oceanDepthTexture;

    private void UpdateTerrainTexture(ref ImageTexture texture, string uniform, Image image)
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
        var activeProfile = Platform.PlatformProfile.Current;
        var halfResGpuMaps = ShouldUseHalfResMobileMapTextures(activeProfile);
        var isTouchPrimary = activeProfile?.IsTouchPrimary == true || halfResGpuMaps;
        var prepared = TakeTerrainImages(definitions, isTouchPrimary);
        var images = new Godot.Collections.Array<Image>();
        var slots = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < definitions.Count; index++)
        {
            slots[definitions[index].Id] = (byte)index;
            images.Add(prepared.Tiles[index]);
        }
        var tiles = new Texture2DArray();
        try
        {
            var error = tiles.CreateFromImages(images);
            if (error != Error.Ok) throw new InvalidDataException($"Terrain texture array: {error}");
            _globeMaterial.SetShaderParameter("terrain_tiles", tiles);
        }
        finally
        {
            images.Clear();
            Array.Clear(prepared.Tiles);
        }
        if (topology.TerrainSurfaceSlots is null || topology.TerrainSurfaceSlots.Length != topology.Width * topology.Height)
            topology.AttachTerrain();
        if (topology.TerrainSurfaceSlots is { Length: > 0 } slotsData && slotsData.Length == topology.Width * topology.Height)
        {
            var surfW = topology.Width;
            var surfH = topology.Height;
            var surfBytes = halfResGpuMaps
                ? DownsampleMobileTerrainSlotsHalfRes(slotsData, topology.Width, topology.Height, out surfW, out surfH)
                : slotsData;
            using var surfaceImg = Image.CreateFromData(surfW, surfH, false, Image.Format.R8, surfBytes);
            UpdateTerrainTexture(ref _terrainSurfaceTexture, "terrain_surface", surfaceImg);
        }
        else
        {
            using var fallbackSurface = Image.CreateEmpty(2, 2, false, Image.Format.R8);
            UpdateTerrainTexture(ref _terrainSurfaceTexture, "terrain_surface", fallbackSurface);
        }
        topology.TerrainSurfaceSlots = Array.Empty<byte>();
        var lookup = new byte[256 * 256];
        for (var province = 1; province <= topology.ProvinceCount; province++)
            lookup[province] = slots.TryGetValue(topology.TerrainAt(province).Id, out var slot) ? slot : (byte)0;
        using var lookupImg = Image.CreateFromData(256, 256, false, Image.Format.R8, lookup);
        UploadMapImage("province_terrain", lookupImg);
        if (!isTouchPrimary && prepared.Height is not null && !prepared.Height.IsEmpty())
        {
            using var cpuHeight = (Image)prepared.Height.Duplicate();
            cpuHeight.ClearMipmaps();
            if (cpuHeight.GetFormat() != Image.Format.L8 && cpuHeight.GetFormat() != Image.Format.R8)
                cpuHeight.Convert(Image.Format.L8);
            const int gridW = 768;
            const int gridH = 384;
            if (cpuHeight.GetWidth() != gridW || cpuHeight.GetHeight() != gridH)
                cpuHeight.Resize(gridW, gridH, Image.Interpolation.Bilinear);
            _reliefHeightWidth = cpuHeight.GetWidth();
            _reliefHeightHeight = cpuHeight.GetHeight();
            _reliefHeightGrid = cpuHeight.GetData();
        }
        else
        {
            _reliefHeightGrid = null;
            _reliefHeightWidth = 0;
            _reliefHeightHeight = 0;
        }
        var hasReliefHeight = !isTouchPrimary && prepared.Height is not null && prepared.Height.GetWidth() > 8;
        UpdateTerrainTexture(ref _reliefHeightTexture, "relief_height", prepared.Height);
        _globeMaterial.SetShaderParameter("relief_height_ready", hasReliefHeight ? 1f : 0f);
        prepared.Height = null;
        UpdateTerrainTexture(ref _reliefColorTexture, "relief_color", prepared.Colour);
        prepared.Colour = null;
        // The sea floor: a map without it is a flat navy sheet, one without shelves or coasts.
        var hasFloor = !isTouchPrimary && File.Exists(TerrainCatalog.BathymetryPath);
        UpdateTerrainTexture(ref _oceanDepthTexture, "ocean_depth", prepared.Floor);
        prepared.Floor = null;
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

    private static ImageTexture LoadRelief(string path, Color fallback)
    {
        using var img = ReliefImage(path, fallback);
        return ImageTexture.CreateFromImage(img);
    }

    private static Image ReliefImage(string path, Color fallback)
    {
        var image = File.Exists(path) ? Image.LoadFromFile(path) : null;
        if (image is null || image.IsEmpty())
        {
            image?.Dispose();
            image = Image.CreateEmpty(4, 2, false, Image.Format.Rgb8);
            image.Fill(fallback);
        }
        // Colour/height are presentation data, never province identity. Filtering is safe.
        image.GenerateMipmaps();
        return image;
    }

    internal static byte[] DownsampleMobileTerrainSlotsHalfRes(
        byte[] source, int sourceWidth, int sourceHeight, out int halfWidth, out int halfHeight)
    {
        if (source is null || sourceWidth < 2 || sourceHeight < 2 || source.Length != sourceWidth * sourceHeight)
        {
            halfWidth = sourceWidth;
            halfHeight = sourceHeight;
            return source;
        }

        halfWidth = sourceWidth / 2;
        halfHeight = sourceHeight / 2;
        var dst = new byte[halfWidth * halfHeight];
        var dstW = halfWidth;
        var srcW = sourceWidth;

        Parallel.For(0, halfHeight, hy =>
        {
            var sy0 = hy * 2;
            var row0 = sy0 * srcW;
            var row1 = (sy0 + 1) * srcW;
            var dstRow = hy * dstW;
            for (var hx = 0; hx < dstW; hx++)
            {
                var sx0 = hx * 2;
                var a = source[row0 + sx0];
                var b = source[row0 + sx0 + 1];
                var c = source[row1 + sx0];
                var d = source[row1 + sx0 + 1];
                if (a == b && a == c && a == d)
                {
                    dst[dstRow + hx] = a;
                    continue;
                }
                var c0 = 1 + (a == b ? 1 : 0) + (a == c ? 1 : 0) + (a == d ? 1 : 0);
                var c1 = 1 + (b == a ? 1 : 0) + (b == c ? 1 : 0) + (b == d ? 1 : 0);
                var c2 = 1 + (c == a ? 1 : 0) + (c == b ? 1 : 0) + (c == d ? 1 : 0);
                var c3 = 1 + (d == a ? 1 : 0) + (d == b ? 1 : 0) + (d == c ? 1 : 0);
                var best = a;
                var bestCount = c0;
                if (c1 > bestCount) { bestCount = c1; best = b; }
                if (c2 > bestCount) { bestCount = c2; best = c; }
                if (c3 > bestCount) best = d;
                dst[dstRow + hx] = best;
            }
        });
        return dst;
    }
}
