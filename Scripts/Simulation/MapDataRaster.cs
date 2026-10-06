using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Text;
using System.Text.Json;

namespace DealOrForce.Simulation;

public sealed record RgbDataMap(int Width, int Height, byte[] Pixels);

/// <summary>
/// Small engine-independent PNG reader/writer for authoritative map data. It deliberately
/// supports only non-interlaced 8-bit grayscale/RGB/RGBA files: map authors get ordinary
/// lossless PNGs, while accidental indexed palettes or lossy formats fail with a useful error.
/// </summary>
public static class DataMapPng
{
    private static readonly byte[] Signature = { 137, 80, 78, 71, 13, 10, 26, 10 };

    public static RgbDataMap LoadRgb(string path)
    {
        using var stream = File.OpenRead(path);
        Span<byte> signature = stackalloc byte[8];
        stream.ReadExactly(signature);
        if (!signature.SequenceEqual(Signature)) throw new InvalidDataException($"{Path.GetFileName(path)} is not a PNG file.");

        var width = 0;
        var height = 0;
        var colorType = -1;
        using var compressed = new MemoryStream();
        var chunkHeader = new byte[8];
        var chunkCrc = new byte[4];
        while (stream.Position < stream.Length)
        {
            stream.ReadExactly(chunkHeader);
            var length = checked((int)BinaryPrimitives.ReadUInt32BigEndian(chunkHeader.AsSpan(0, 4)));
            var type = Encoding.ASCII.GetString(chunkHeader, 4, 4);
            var data = new byte[length];
            stream.ReadExactly(data);
            stream.ReadExactly(chunkCrc);
            if (type == "IHDR")
            {
                if (length != 13) throw new InvalidDataException("Invalid PNG header.");
                width = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(0, 4)));
                height = checked((int)BinaryPrimitives.ReadUInt32BigEndian(data.AsSpan(4, 4)));
                if (data[8] != 8 || data[10] != 0 || data[11] != 0 || data[12] != 0)
                    throw new InvalidDataException("Data maps must use non-interlaced 8-bit PNG pixels.");
                colorType = data[9];
                if (colorType is not (0 or 2 or 6))
                    throw new InvalidDataException("Data maps must be grayscale, RGB or RGBA PNG; indexed palettes are not accepted.");
            }
            else if (type == "IDAT") compressed.Write(data);
            else if (type == "IEND") break;
        }
        if (width <= 0 || height <= 0 || colorType < 0) throw new InvalidDataException("PNG has no valid IHDR chunk.");

        var channels = colorType == 0 ? 1 : colorType == 2 ? 3 : 4;
        var stride = checked(width * channels);
        var rgb = new byte[checked(width * height * 3)];
        var previous = new byte[stride];
        var row = new byte[stride];
        var encoded = new byte[stride];
        compressed.Position = 0;
        using var zlib = new ZLibStream(compressed, CompressionMode.Decompress, true);
        for (var y = 0; y < height; y++)
        {
            var filter = zlib.ReadByte();
            if (filter < 0) throw new EndOfStreamException("PNG pixel stream ended before the final row.");
            zlib.ReadExactly(encoded);
            for (var x = 0; x < stride; x++)
            {
                var left = x >= channels ? row[x - channels] : 0;
                var up = previous[x];
                var upperLeft = x >= channels ? previous[x - channels] : 0;
                row[x] = filter switch
                {
                    0 => encoded[x],
                    1 => unchecked((byte)(encoded[x] + left)),
                    2 => unchecked((byte)(encoded[x] + up)),
                    3 => unchecked((byte)(encoded[x] + ((left + up) >> 1))),
                    4 => unchecked((byte)(encoded[x] + Paeth(left, up, upperLeft))),
                    _ => throw new InvalidDataException($"Unsupported PNG filter {filter}.")
                };
            }
            for (var x = 0; x < width; x++)
            {
                var write = (y * width + x) * 3;
                if (channels == 1)
                {
                    rgb[write] = row[x];
                    rgb[write + 1] = row[x];
                    rgb[write + 2] = row[x];
                }
                else
                {
                    rgb[write] = row[x * channels];
                    rgb[write + 1] = row[x * channels + 1];
                    rgb[write + 2] = row[x * channels + 2];
                }
            }
            (previous, row) = (row, previous);
        }
        return new RgbDataMap(width, height, rgb);
    }

    public static void SaveRgb(string path, int width, int height, ReadOnlySpan<byte> rgb)
    {
        if (width <= 0 || height <= 0 || rgb.Length != width * height * 3)
            throw new ArgumentException("RGB data does not match the declared dimensions.");
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        using var stream = File.Create(path);
        stream.Write(Signature);
        Span<byte> ihdr = stackalloc byte[13];
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[..4], (uint)width);
        BinaryPrimitives.WriteUInt32BigEndian(ihdr[4..8], (uint)height);
        ihdr[8] = 8;
        ihdr[9] = 2;
        WriteChunk(stream, "IHDR", ihdr);
        using var payload = new MemoryStream();
        using (var zlib = new ZLibStream(payload, CompressionLevel.SmallestSize, true))
        {
            for (var y = 0; y < height; y++)
            {
                zlib.WriteByte(0);
                zlib.Write(rgb.Slice(y * width * 3, width * 3));
            }
        }
        WriteChunk(stream, "IDAT", payload.ToArray());
        WriteChunk(stream, "IEND", ReadOnlySpan<byte>.Empty);
    }

    private static int Paeth(int left, int up, int upperLeft)
    {
        var value = left + up - upperLeft;
        var leftDistance = Math.Abs(value - left);
        var upDistance = Math.Abs(value - up);
        var diagonalDistance = Math.Abs(value - upperLeft);
        return leftDistance <= upDistance && leftDistance <= diagonalDistance ? left : upDistance <= diagonalDistance ? up : upperLeft;
    }

    private static void WriteChunk(Stream stream, string type, ReadOnlySpan<byte> data)
    {
        Span<byte> length = stackalloc byte[4];
        BinaryPrimitives.WriteUInt32BigEndian(length, (uint)data.Length);
        stream.Write(length);
        var typeBytes = Encoding.ASCII.GetBytes(type);
        stream.Write(typeBytes);
        stream.Write(data);
        var crcInput = new byte[typeBytes.Length + data.Length];
        typeBytes.CopyTo(crcInput, 0);
        data.CopyTo(crcInput.AsSpan(typeBytes.Length));
        BinaryPrimitives.WriteUInt32BigEndian(length, Crc32(crcInput));
        stream.Write(length);
    }

    private static uint Crc32(ReadOnlySpan<byte> data)
    {
        var crc = 0xFFFFFFFFu;
        foreach (var value in data)
        {
            crc ^= value;
            for (var bit = 0; bit < 8; bit++) crc = (crc >> 1) ^ (0xEDB88320u & (uint)-(int)(crc & 1));
        }
        return ~crc;
    }
}

/// <summary>Authoritative Paradox-familiar raster contract and layered mod paths.</summary>
public static class MapRasterCatalog
{
    public const string DescriptorFile = "default.map";
    public const string ProvinceFile = "provinces.png";
    public const string DefinitionFile = "definition.csv";
    public const string RiverFile = "rivers.png";
    public const string LakeFile = "lakes.png";
    public const string StampFile = "compiled.json";

    public static string ProvincePath { get; private set; }
    public static string DefinitionPath { get; private set; }
    public static string RiverPath { get; private set; }
    public static string LakePath { get; private set; }
    public static string StampPath { get; private set; }
    public static string SourceControlPath { get; private set; }
    public static string SourceStatesPath { get; private set; }
    public static string SourceDisputesPath { get; private set; }
    private static string _sourceRevision = "";
    private static bool _resettingSources;

    static MapRasterCatalog() => ResetCatalog();

    public static void ResetCatalog()
    {
        _resettingSources = true;
        try
        {
        WorldMapTables.ResetCatalog();
        ProvincePath = null;
        DefinitionPath = null;
        RiverPath = null;
        LakePath = null;
        StampPath = null;
        SourceControlPath = SourceStatesPath = SourceDisputesPath = null;
        var descriptor = GameDataLoader.FindDataFile("map_data", DescriptorFile);
        if (descriptor is not null) ApplyDirectory(Path.GetDirectoryName(descriptor)!, null);
        else
        {
            ProvincePath = GameDataLoader.FindDataFile("map_data", ProvinceFile);
            DefinitionPath = GameDataLoader.FindDataFile("map_data", DefinitionFile);
            RiverPath = GameDataLoader.FindDataFile("map_data", RiverFile);
            LakePath = GameDataLoader.FindDataFile("map_data", LakeFile);
            StampPath = GameDataLoader.FindDataFile("map_data", StampFile);
            SourceControlPath = GameDataLoader.FindDataFile("map_data", "source_control.png");
            SourceStatesPath = GameDataLoader.FindDataFile("map_data", "source_states.png");
            SourceDisputesPath = GameDataLoader.FindDataFile("map_data", "source_disputes.png");
        }
        }
        finally
        {
            _resettingSources = false;
            RefreshSourceRevision();
        }
    }

    private static void RefreshSourceRevision()
    {
        if (_resettingSources) return;
        string Revision(string path)
        {
            if (path is null || !File.Exists(path)) return "missing:" + path;
            var file = new FileInfo(path);
            return $"{file.FullName}|{file.Length}|{file.LastWriteTimeUtc.Ticks}";
        }
        var revision = string.Join(";", Revision(SourceControlPath), Revision(SourceStatesPath), Revision(SourceDisputesPath));
        // Catalog refreshes must not discard the campaign's repaired region grid when
        // its source files are unchanged. Changed files/paths still invalidate it.
        if (revision == _sourceRevision) return;
        _sourceRevision = revision;
        WorldMapSource.ReleaseDecodedGrids();
    }

    public static void ApplyDirectory(string directory, List<string> errors)
    {
        if (!Directory.Exists(directory)) return;
        var descriptor = ReadDescriptor(Path.Combine(directory, DescriptorFile), errors);
        string FileFor(string key, string fallback)
        {
            var name = descriptor.TryGetValue(key, out var configured) ? configured : fallback;
            if (Path.IsPathRooted(name) || name.Split('/', '\\').Contains(".."))
            {
                errors?.Add($"Unsafe map-data path for '{key}': {name}");
                return Path.Combine(directory, fallback);
            }
            return Path.Combine(directory, name.Replace('/', Path.DirectorySeparatorChar));
        }
        var province = FileFor("provinces", ProvinceFile);
        var definition = FileFor("definition", DefinitionFile);
        if (descriptor.ContainsKey("provinces") && !File.Exists(province))
            errors?.Add($"default.map references missing province image: {Path.GetFileName(province)}");
        if (descriptor.ContainsKey("definition") && !File.Exists(definition))
            errors?.Add($"default.map references missing province definition: {Path.GetFileName(definition)}");
        if (File.Exists(province) != File.Exists(definition))
            errors?.Add("map_data/provinces.png and definition.csv must be overridden together.");
        else if (File.Exists(province))
        {
            ProvincePath = province;
            DefinitionPath = definition;
        }
        var rivers = FileFor("rivers", RiverFile);
        var lakes = FileFor("lakes", LakeFile);
        if (descriptor.ContainsKey("rivers") && !File.Exists(rivers))
            errors?.Add($"default.map references missing river image: {Path.GetFileName(rivers)}");
        if (descriptor.ContainsKey("lakes") && !File.Exists(lakes))
            errors?.Add($"default.map references missing lake image: {Path.GetFileName(lakes)}");
        if (File.Exists(rivers)) RiverPath = rivers;
        if (File.Exists(lakes)) LakePath = lakes;
        var stamp = Path.Combine(directory, StampFile);
        if (File.Exists(stamp)) StampPath = stamp;
        SourceControlPath = SourceFileFor("source_control", "source_control.png", SourceControlPath);
        SourceStatesPath = SourceFileFor("source_states", "source_states.png", SourceStatesPath);
        SourceDisputesPath = SourceFileFor("source_disputes", "source_disputes.png", SourceDisputesPath);
        WorldMapTables.ApplyDirectory(directory, errors);
        RefreshSourceRevision();

        string SourceFileFor(string key, string fallback, string previous)
        {
            var path = FileFor(key, fallback);
            if (!File.Exists(path))
            {
                if (descriptor.ContainsKey(key))
                {
                    var message = $"default.map references missing source image: {Path.GetFileName(path)}";
                    if (errors is null) throw new InvalidDataException(message);
                    errors.Add(message);
                }
                return previous;
            }
            try
            {
                // Validate before committing a mod override. An invalid mod must not
                // replace a working base layer or leak old decoded ids into a new map.
                if (errors is not null) SourceMapGrid.Load(path, WorldMapSource.Width, WorldMapSource.Height);
                return path;
            }
            catch (Exception exception) when (errors is not null)
            {
                errors.Add($"source map: {exception.Message}");
                return previous;
            }
        }
    }

    /// <summary>
    /// One number standing for the whole region layer, so a map can say what it was cut from.
    ///
    /// Provinces are grown inside regions and never cross one, which makes the province
    /// raster an output of the region layer rather than something that stands on its own. If
    /// the two are ever built by different code the province layer refuses the map, and the
    /// refusal reads as "the map is corrupt" when what actually happened is that one half of
    /// it is older than the other - a game that was not rebuilt after the region code
    /// changed, or a map that was not recompiled after the data did. The fingerprint is
    /// written beside the raster so the refusal can say which half.
    /// </summary>
    public static ulong RegionFingerprint(ushort[] states)
    {
        var hash = 14695981039346656037UL;
        foreach (var index in states)
        {
            hash = (hash ^ (byte)index) * 1099511628211UL;
            hash = (hash ^ (byte)(index >> 8)) * 1099511628211UL;
        }
        return hash;
    }

    /// <summary>The region layer the province raster on disk was cut from, if it says.</summary>
    private static ulong? StampedFingerprint()
    {
        if (string.IsNullOrWhiteSpace(StampPath) || !File.Exists(StampPath)) return null;
        try
        {
            using var document = JsonDocument.Parse(File.ReadAllText(StampPath));
            if (document.RootElement.TryGetProperty("regions", out var value) &&
                value.TryGetUInt64(out var fingerprint)) return fingerprint;
        }
        catch { /* A map pack that says nothing is treated as one that was never stamped. */ }
        return null;
    }

    public static bool TryBuildProvinceTopology(ushort[] states, ushort[] control, int width, int height,
        out ProvinceTopology topology, out string message)
    {
        topology = null;
        if (string.IsNullOrWhiteSpace(ProvincePath) || string.IsNullOrWhiteSpace(DefinitionPath))
        {
            message = "no province raster pair found";
            return false;
        }
        var stamped = StampedFingerprint();
        if (stamped is not null && stamped != RegionFingerprint(states))
        {
            message = $"{Path.GetFileName(ProvincePath)} was cut from a different region layer " +
                "than this build draws. Nothing is corrupt - one half of the map is simply older " +
                "than the other. If the data changed, recompile the map with Tools/MapDataCompiler; " +
                "if the code that draws regions changed, rebuild the game first and then recompile";
            return false;
        }
        try
        {
            var raster = DataMapPng.LoadRgb(ProvincePath);
            var definitions = LoadDefinitions(DefinitionPath);
            topology = ProvinceTopology.FromColorRaster(states, control, width, height, raster, definitions);
            topology.AttachTerrain();
            message = $"{Path.GetFileName(ProvincePath)} + {Path.GetFileName(DefinitionPath)}";
            return true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return false;
        }
    }

    public static bool TryLoadHydrology(int width, int height, out HydrologyRaster raster, out string message)
    {
        raster = null;
        if (string.IsNullOrWhiteSpace(RiverPath) || string.IsNullOrWhiteSpace(LakePath))
        {
            message = "rivers.png or lakes.png is missing";
            return false;
        }
        try
        {
            var rivers = DataMapPng.LoadRgb(RiverPath);
            var lakes = DataMapPng.LoadRgb(LakePath);
            RequireDimensions(rivers, width, height, RiverFile);
            RequireDimensions(lakes, width, height, LakeFile);
            var pixels = new byte[width * height * 2];
            for (var cell = 0; cell < width * height; cell++)
            {
                var riverColor = Packed(rivers.Pixels, cell * 3);
                pixels[cell * 2 + 1] = riverColor switch
                {
                    0x000000 => 0,
                    0x1E5AA8 => 96,
                    0x1E8CD2 => 160,
                    0x2DD4FF => 225,
                    _ => throw new InvalidDataException($"Unknown river colour #{riverColor:X6} at pixel {cell}; use the documented palette.")
                };
                var lakeColor = Packed(lakes.Pixels, cell * 3);
                pixels[cell * 2] = lakeColor switch
                {
                    0x000000 => 0,
                    0x3366FF => 255,
                    _ => throw new InvalidDataException($"Unknown lake colour #{lakeColor:X6} at pixel {cell}; use #3366FF or black.")
                };
            }
            raster = new HydrologyRaster(width, height, pixels, 0, 0, "raster data maps");
            message = $"{Path.GetFileName(RiverPath)} + {Path.GetFileName(LakePath)}";
            return true;
        }
        catch (Exception exception)
        {
            message = exception.Message;
            return false;
        }
    }

    public static void Export(string directory, ProvinceTopology topology, HydrologyRaster hydrology,
        ushort[] states = null)
    {
        Directory.CreateDirectory(directory);
        if (states is not null)
        {
            var note = "Written by Tools/MapDataCompiler. Not hand-edited - it records which " +
                "region layer provinces.png was cut from, so a mismatch can say so instead of " +
                "looking like a corrupt map.";
            var stamp = new StringBuilder();
            stamp.AppendLine("{");
            stamp.AppendLine($"  \"//\": \"{note}\",");
            stamp.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  \"regions\": {RegionFingerprint(states)},"));
            stamp.AppendLine(string.Create(CultureInfo.InvariantCulture,
                $"  \"provinces\": {topology.ProvinceCount}"));
            stamp.AppendLine("}");
            File.WriteAllText(Path.Combine(directory, StampFile), stamp.ToString(), new UTF8Encoding(false));
        }

        var provinces = new byte[topology.Width * topology.Height * 3];
        for (var cell = 0; cell < topology.CellToProvince.Length; cell++)
        {
            var id = topology.CellToProvince[cell];
            if (id <= 0) continue;
            var color = ColorForProvince(id);
            provinces[cell * 3] = (byte)(color >> 16);
            provinces[cell * 3 + 1] = (byte)(color >> 8);
            provinces[cell * 3 + 2] = (byte)color;
        }
        DataMapPng.SaveRgb(Path.Combine(directory, ProvinceFile), topology.Width, topology.Height, provinces);
        using (var writer = new StreamWriter(Path.Combine(directory, DefinitionFile), false, new UTF8Encoding(false)))
        {
            writer.WriteLine("province_id;r;g;b;state_id;nation_id");
            for (var id = 1; id <= topology.ProvinceCount; id++)
            {
                var color = ColorForProvince(id);
                writer.WriteLine(string.Create(CultureInfo.InvariantCulture,
                    $"{id};{(color >> 16) & 255};{(color >> 8) & 255};{color & 255};{topology.ProvinceToState[id]};{topology.ProvinceToNation[id]}"));
            }
        }

        var rivers = new byte[topology.Width * topology.Height * 3];
        var lakes = new byte[rivers.Length];
        for (var cell = 0; cell < topology.Width * topology.Height; cell++)
        {
            var lake = hydrology.Pixels[cell * 2];
            var river = hydrology.Pixels[cell * 2 + 1];
            if (lake > 0) WriteColor(lakes, cell * 3, 0x3366FF);
            if (river > 0) WriteColor(rivers, cell * 3, river <= 110 ? 0x1E5AA8 : river <= 190 ? 0x1E8CD2 : 0x2DD4FF);
        }
        DataMapPng.SaveRgb(Path.Combine(directory, RiverFile), topology.Width, topology.Height, rivers);
        DataMapPng.SaveRgb(Path.Combine(directory, LakeFile), topology.Width, topology.Height, lakes);
    }

    private static Dictionary<int, int> LoadDefinitions(string path)
    {
        var result = new Dictionary<int, int>();
        foreach (var raw in File.ReadLines(path).Skip(1))
        {
            if (string.IsNullOrWhiteSpace(raw) || raw.TrimStart().StartsWith('#')) continue;
            var columns = raw.Split(';');
            if (columns.Length < 4 || !int.TryParse(columns[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ||
                !byte.TryParse(columns[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var r) ||
                !byte.TryParse(columns[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out var g) ||
                !byte.TryParse(columns[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out var b) || id <= 0 || id > ushort.MaxValue)
                throw new InvalidDataException($"Invalid province definition: {raw}");
            var color = (r << 16) | (g << 8) | b;
            if (color == 0 || !result.TryAdd(color, id)) throw new InvalidDataException($"Duplicate or black province colour in definition.csv: {raw}");
        }
        if (result.Count == 0) throw new InvalidDataException("definition.csv contains no provinces.");
        return result;
    }

    private static Dictionary<string, string> ReadDescriptor(string path, List<string> errors)
    {
        var result = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (!File.Exists(path)) return result;
        try
        {
            foreach (var raw in File.ReadLines(path))
            {
                var line = raw.Split('#', 2)[0].Trim();
                if (line.Length == 0) continue;
                var equals = line.IndexOf('=');
                if (equals <= 0) throw new InvalidDataException($"Invalid default.map line: {raw}");
                var key = line[..equals].Trim();
                var value = line[(equals + 1)..].Trim().Trim('"');
                if (key is not ("provinces" or "definition" or "rivers" or "lakes" or "wrap_x"))
                    throw new InvalidDataException($"Unknown default.map key '{key}'.");
                result[key] = value;
            }
            if (result.TryGetValue("wrap_x", out var wrap) && !wrap.Equals("yes", StringComparison.OrdinalIgnoreCase))
                throw new InvalidDataException("Only wrap_x = yes is supported by the global map.");
        }
        catch (Exception exception) { errors?.Add($"default.map: {exception.Message}"); }
        return result;
    }

    private static int ColorForProvince(int id) => (int)((uint)id * 0x9E3779B1u & 0xFFFFFFu);
    private static int Packed(byte[] pixels, int index) => (pixels[index] << 16) | (pixels[index + 1] << 8) | pixels[index + 2];
    private static void WriteColor(byte[] pixels, int index, int color)
    {
        pixels[index] = (byte)(color >> 16);
        pixels[index + 1] = (byte)(color >> 8);
        pixels[index + 2] = (byte)color;
    }
    private static void RequireDimensions(RgbDataMap map, int width, int height, string name)
    {
        if (map.Width != width || map.Height != height)
            throw new InvalidDataException($"{name} is {map.Width}x{map.Height}; expected {width}x{height}.");
    }
}
