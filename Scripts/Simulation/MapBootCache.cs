using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Reflection;
using System.Runtime.InteropServices;
using K4os.Compression.LZ4;

namespace DealOrForce.Simulation;

/// <summary>Pure-C# serializable territory/region label anchor for <see cref="MapBootCache"/>.</summary>
public readonly record struct CachedLabelAnchor(
    int Key,
    float CenterX,
    float CenterY,
    float AxisX,
    float AxisY,
    float HalfSpanPixels,
    float ThicknessPixels,
    int Cells);

/// <summary>Pure-C# serializable major river label anchor for <see cref="MapBootCache"/>.</summary>
public readonly record struct CachedRiverAnchor(
    string Key,
    string DefaultName,
    int Rank,
    float Latitude,
    float Longitude,
    float AxisX,
    float AxisY,
    float SpanDegrees);

/// <summary>Complete pre-baked province topology and label anchor bundle restored from <c>map_boot_v1.bin</c>.</summary>
public sealed class MapBootCacheData
{
    public ProvinceTopology Provinces { get; init; }
    public string ProvinceSource { get; init; } = "";
    public string HydrologySource { get; init; } = "";
    public CachedLabelAnchor[] CountryAnchors { get; init; } = Array.Empty<CachedLabelAnchor>();
    public CachedLabelAnchor[] RegionAnchors { get; init; } = Array.Empty<CachedLabelAnchor>();
    public CachedRiverAnchor[] RiverAnchors { get; init; } = Array.Empty<CachedRiverAnchor>();
    public bool HasTerrainSurfaceSlots { get; init; }
}

/// <summary>Cached pre-rasterized lake SDF (R8) and river centreline (RGBA8) layers restored from <c>hydrology_v2.bin</c>.</summary>
public sealed class HydrologyBootCacheData
{
    public byte[] LakeSdfR8 { get; init; } = Array.Empty<byte>();
    public byte[] RiverCentreRgba8 { get; init; } = Array.Empty<byte>();
    public string HydrologySource { get; init; } = "";
}

/// <summary>Cached LZ4-compressed packed political &amp; plate raster buffers restored from <c>political_rasters_v1.bin</c>.</summary>
public sealed class PoliticalRasterBootCacheData
{
    public ushort[] PlateProvinces { get; init; } = Array.Empty<ushort>();
    public ushort[] PlateHolders { get; init; } = Array.Empty<ushort>();
    public byte[] PlateGeography { get; init; } = Array.Empty<byte>();
    public byte[] PlateClass { get; init; } = Array.Empty<byte>();
    public byte[] RegionIds { get; init; } = Array.Empty<byte>();
    public byte[] ProvinceIds { get; init; } = Array.Empty<byte>();
    public byte[] NationIds { get; init; } = Array.Empty<byte>();
}

/// <summary>
/// Checksummed binary map boot cache (<c>map_boot_v1.bin</c>) storing pre-reconciled
/// <see cref="ProvinceTopology"/> essentials (RLE-packed <c>CellToProvince</c>, political arrays,
/// land neighbor graph, centroids, river crossings, terrain assignments, optional terrain surface slots)
/// plus precomputed territory, region, and river label anchors.
///
/// Keyed by <c>RegionFingerprint</c> + <c>Width</c> + <c>Height</c> + format magic/version, verified with a
/// 64-bit FNV-1a payload checksum, and automatically bypassed whenever <see cref="ModOverlayPolicy.IsActive"/>
/// is true. Pure C# with zero engine references; caller supplies the writable cache directory and optional
/// read-only shipped fallback path (for pre-baked Android APK first-launch speed).
/// </summary>
public static class MapBootCache
{
    public const string FileName = "map_boot_v2.bin";
    public const uint Magic = 0x43424D44u; // "DMBC"
    public const int FormatVersion = 2;
    private const int HeaderSizeBytes = 40;

    public const string HydrologyFileName = "hydrology_v2.bin";
    public const uint HydrologyMagic = 0x43484D44u; // "DMHC"
    public const int HydrologyFormatVersion = 2;
    private const int HydrologyHeaderSizeBytes = 36;

    public const string PoliticalRasterFileName = "political_rasters_v1.bin";
    public const uint PoliticalRasterMagic = 0x43504D44u; // "DMPC"
    public const int PoliticalRasterFormatVersion = 1;
    private const int PoliticalRasterHeaderSizeBytes = 40;

    private static readonly ConstructorInfo TopologyCtor = typeof(ProvinceTopology).GetConstructor(
        BindingFlags.Instance | BindingFlags.NonPublic,
        binder: null,
        types: new[]
        {
            typeof(ushort[]), typeof(ushort[]), typeof(ushort[]),
            typeof(int[]), typeof(int[][]), typeof(int), typeof(int)
        },
        modifiers: null);

    private static readonly PropertyInfo RiverIntensityProp = typeof(ProvinceTopology).GetProperty(
        nameof(ProvinceTopology.RiverIntensityByProvince),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly PropertyInfo LakeAccessProp = typeof(ProvinceTopology).GetProperty(
        nameof(ProvinceTopology.LakeAccessByProvince),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly PropertyInfo TerrainByProvinceProp = typeof(ProvinceTopology).GetProperty(
        nameof(ProvinceTopology.TerrainByProvince),
        BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic);

    private static readonly FieldInfo RiverCrossingsField = typeof(ProvinceTopology).GetField(
        "_riverCrossings",
        BindingFlags.Instance | BindingFlags.NonPublic);

    /// <summary>
    /// Resolve the best readable <c>map_boot_v1.bin</c>: writable cache directory first, then an
    /// optional read-only shipped fallback (e.g. <c>GameDataLoader.FindDataFile("cache", FileName)</c>).
    /// Returns <c>null</c> whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static string ResolveReadableCachePath(string writableCacheDirectory, string shippedFallbackPath = null)
    {
        if (ModOverlayPolicy.IsActive) return null;
        if (!string.IsNullOrWhiteSpace(writableCacheDirectory))
        {
            var writable = Path.Combine(writableCacheDirectory, FileName);
            if (File.Exists(writable)) return writable;
        }
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return shippedFallbackPath;
        return null;
    }

    /// <summary>Resolve the writable cache file path, or <c>null</c> if mod overlays are active.</summary>
    public static string ResolveWritableCachePath(string writableCacheDirectory)
    {
        if (ModOverlayPolicy.IsActive || string.IsNullOrWhiteSpace(writableCacheDirectory))
            return null;
        return Path.Combine(writableCacheDirectory, FileName);
    }

    /// <summary>
    /// Atomically write <paramref name="data"/> to <c>map_boot_v1.bin</c> under <paramref name="cacheDirectory"/>.
    /// Bypassed cleanly whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static bool TryWrite(
        string cacheDirectory,
        ulong regionFingerprint,
        MapBootCacheData data,
        out string error,
        bool includeTerrainSurfaceSlots = true)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            error = "no cache directory";
            return false;
        }
        if (data?.Provinces is null)
        {
            error = "missing province topology";
            return false;
        }

        var targetPath = Path.Combine(cacheDirectory, FileName);
        return TryWriteToFile(targetPath, regionFingerprint, data, out error, includeTerrainSurfaceSlots);
    }

    /// <summary>Atomically write <paramref name="data"/> to an explicit file path.</summary>
    public static bool TryWriteToFile(
        string targetPath,
        ulong regionFingerprint,
        MapBootCacheData data,
        out string error,
        bool includeTerrainSurfaceSlots = true)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(targetPath) || data?.Provinces is null)
        {
            error = "invalid cache write arguments";
            return false;
        }

        string tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            var payload = EncodePayload(data, includeTerrainSurfaceSlots);
            var checksum = ComputeFnv1a64(payload);
            var provinces = data.Provinces;

            tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(Magic);
                writer.Write(FormatVersion);
                writer.Write(regionFingerprint);
                writer.Write(provinces.Width);
                writer.Write(provinces.Height);
                writer.Write(provinces.ProvinceCount);
                writer.Write(payload.Length);
                writer.Write(checksum);
                writer.Write(payload);
                writer.Flush();
            }

            File.Move(tempPath, targetPath, overwrite: true);
            tempPath = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Try to load and validate <c>map_boot_v1.bin</c> from <paramref name="writableCacheDirectory"/> or
    /// <paramref name="shippedFallbackPath"/>. Never throws on missing, stale, or corrupt files.
    /// </summary>
    public static bool TryRead(
        string writableCacheDirectory,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out MapBootCacheData data,
        out string reason,
        string shippedFallbackPath = null,
        bool attachCrossings = true)
    {
        data = null;
        if (ModOverlayPolicy.IsActive)
        {
            reason = "bypassed: mod overlay active";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(writableCacheDirectory))
        {
            var writable = Path.Combine(writableCacheDirectory, FileName);
            if (File.Exists(writable))
            {
                if (TryReadFile(writable, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason, attachCrossings))
                    return true;
                // Fall through to shipped fallback if writable cache was stale or corrupt.
            }
        }

        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadFile(shippedFallbackPath, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason, attachCrossings);

        reason = "cache file not found";
        return false;
    }

    /// <summary>Try to load and validate an explicit cache file path.</summary>
    public static bool TryReadFile(
        string filePath,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out MapBootCacheData data,
        out string reason,
        bool attachCrossings = true)
    {
        data = null;
        reason = "";
        if (ModOverlayPolicy.IsActive)
        {
            reason = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            reason = "cache file not found";
            return false;
        }
        if (TopologyCtor is null || RiverIntensityProp is null || LakeAccessProp is null ||
            TerrainByProvinceProp is null || RiverCrossingsField is null)
        {
            reason = "topology reflection contract unavailable";
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (stream.Length < HeaderSizeBytes)
            {
                reason = "truncated cache header";
                return false;
            }
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadUInt32();
            if (magic != Magic) { reason = $"bad magic 0x{magic:X8}"; return false; }
            var version = reader.ReadInt32();
            if (version != FormatVersion) { reason = $"version mismatch {version} != {FormatVersion}"; return false; }
            var fingerprint = reader.ReadUInt64();
            if (fingerprint != expectedFingerprint) { reason = "region fingerprint mismatch"; return false; }
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            if (width != expectedWidth || height != expectedHeight) { reason = $"dimensions mismatch {width}x{height}"; return false; }
            var provinceCount = reader.ReadInt32();
            if (provinceCount <= 0 || provinceCount > ushort.MaxValue) { reason = $"invalid province count {provinceCount}"; return false; }
            var payloadLength = reader.ReadInt32();
            var expectedChecksum = reader.ReadUInt64();
            if (payloadLength <= 0 || stream.Length - HeaderSizeBytes != payloadLength)
            {
                reason = "payload length mismatch";
                return false;
            }

            var payload = reader.ReadBytes(payloadLength);
            if (payload.Length != payloadLength)
            {
                reason = "truncated payload";
                return false;
            }
            var actualChecksum = ComputeFnv1a64(payload);
            if (actualChecksum != expectedChecksum)
            {
                reason = "checksum mismatch";
                return false;
            }

            data = DecodePayload(payload, width, height, provinceCount, attachCrossings, out reason);
            if (data is null) return false;
            reason = $"hit ({Path.GetFileName(filePath)}, {payloadLength / 1024:N0} KB)";
            return true;
        }
        catch (Exception ex)
        {
            data = null;
            reason = $"read error: {ex.Message}";
            return false;
        }
    }

    private static byte[] EncodePayload(MapBootCacheData data, bool includeTerrainSurfaceSlots)
    {
        var p = data.Provinces;
        var count = p.ProvinceCount;
        var slots = count + 1;
        using var ms = new MemoryStream(Math.Max(64 * 1024, p.CellToProvince.Length / 4));
        using var w = new BinaryWriter(ms);

        // 1. CellToProvince (RLE ushort)
        WriteUShortRle(w, p.CellToProvince);

        // 2. Per-province political and center arrays
        WriteUShortArray(w, p.ProvinceToState, slots);
        WriteUShortArray(w, p.ProvinceToNation, slots);
        WriteIntArray(w, p.ProvinceCenterCell, slots);

        // 3. Pure land neighbor graph (LandNeighbors, before strait crossings)
        var landNeighbors = p.LandNeighbors;
        for (var i = 0; i < slots; i++)
        {
            var row = i < landNeighbors.Length ? landNeighbors[i] : Array.Empty<int>();
            w.Write((ushort)row.Length);
            for (var j = 0; j < row.Length; j++) w.Write(row[j]);
        }

        // 4. Hydrology per-province intensity, lake access bitset, and river crossings
        var rivers = p.RiverIntensityByProvince;
        for (var i = 0; i < slots; i++) w.Write(i < rivers.Length ? rivers[i] : (byte)0);
        var lakes = p.LakeAccessByProvince;
        var lakeByteCount = (slots + 7) >> 3;
        var lakeBits = new byte[lakeByteCount];
        for (var i = 0; i < slots && i < lakes.Length; i++)
            if (lakes[i]) lakeBits[i >> 3] |= (byte)(1 << (i & 7));
        w.Write(lakeBits);

        var crossings = RiverCrossingsField?.GetValue(p) as Dictionary<long, byte>;
        w.Write(crossings?.Count ?? 0);
        if (crossings is not null)
        {
            foreach (var kv in crossings)
            {
                w.Write(kv.Key);
                w.Write(kv.Value);
            }
        }

        // 5. TerrainByProvince via interned terrain ID palette
        var terrainArr = p.TerrainByProvince ?? Array.Empty<TerrainDefinition>();
        var palette = new List<string>(16);
        var paletteIdx = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var terrainBytes = new byte[slots];
        Array.Fill(terrainBytes, (byte)0xFF);
        for (var i = 0; i < slots && i < terrainArr.Length; i++)
        {
            var def = terrainArr[i];
            if (def is null || string.IsNullOrEmpty(def.Id)) continue;
            if (!paletteIdx.TryGetValue(def.Id, out var slot))
            {
                if (palette.Count >= 254) continue;
                slot = (byte)palette.Count;
                palette.Add(def.Id);
                paletteIdx[def.Id] = slot;
            }
            terrainBytes[i] = slot;
        }
        w.Write((ushort)palette.Count);
        for (var i = 0; i < palette.Count; i++) w.Write(palette[i]);
        w.Write(terrainBytes);

        // 6. Optional TerrainSurfaceSlots (RLE byte)
        var hasSlots = includeTerrainSurfaceSlots &&
                       p.TerrainSurfaceSlots is { Length: > 0 } surf &&
                       surf.Length == p.Width * p.Height;
        w.Write(hasSlots);
        if (hasSlots) WriteByteRle(w, p.TerrainSurfaceSlots);

        // 7. Source labels and precomputed map anchors
        w.Write(data.ProvinceSource ?? "");
        w.Write(data.HydrologySource ?? "");
        WriteLabelAnchors(w, data.CountryAnchors);
        WriteLabelAnchors(w, data.RegionAnchors);
        WriteRiverAnchors(w, data.RiverAnchors);

        w.Flush();
        return ms.ToArray();
    }

    private static MapBootCacheData DecodePayload(
        byte[] payload,
        int width,
        int height,
        int provinceCount,
        bool attachCrossings,
        out string reason)
    {
        reason = "";
        var cellCount = width * height;
        var slots = provinceCount + 1;
        using var ms = new MemoryStream(payload, writable: false);
        using var r = new BinaryReader(ms);

        // 1. CellToProvince
        var cellToProvince = ReadUShortRle(r, cellCount, provinceCount);
        if (cellToProvince is null) { reason = "corrupt CellToProvince RLE"; return null; }

        // 2. Political and center arrays
        var provinceToState = ReadUShortArray(r, slots);
        var provinceToNation = ReadUShortArray(r, slots);
        var provinceCenterCell = ReadIntArray(r, slots);
        for (var i = 1; i < slots; i++)
            if ((uint)provinceCenterCell[i] >= (uint)cellCount)
            {
                reason = $"invalid center cell for province {i}";
                return null;
            }

        // 3. Land neighbor graph
        var neighbors = new int[slots][];
        for (var i = 0; i < slots; i++)
        {
            var deg = r.ReadUInt16();
            if (deg == 0) { neighbors[i] = Array.Empty<int>(); continue; }
            var row = new int[deg];
            for (var j = 0; j < deg; j++)
            {
                var n = r.ReadInt32();
                if (n <= 0 || n > provinceCount) { reason = $"invalid neighbor {n}"; return null; }
                row[j] = n;
            }
            neighbors[i] = row;
        }

        // 4. Hydrology per-province intensity, lake access bitset, and river crossings
        var riverIntensity = r.ReadBytes(slots);
        if (riverIntensity.Length != slots) { reason = "truncated river intensity"; return null; }
        var lakeByteCount = (slots + 7) >> 3;
        var lakeBits = r.ReadBytes(lakeByteCount);
        if (lakeBits.Length != lakeByteCount) { reason = "truncated lake access"; return null; }
        var lakeAccess = new bool[slots];
        for (var i = 0; i < slots; i++)
            lakeAccess[i] = (lakeBits[i >> 3] & (1 << (i & 7))) != 0;

        var riverCrossingCount = r.ReadInt32();
        if (riverCrossingCount < 0 || riverCrossingCount > 2_000_000)
        {
            reason = $"invalid river crossing count {riverCrossingCount}";
            return null;
        }
        var crossings = new List<(long Key, byte Strength)>(riverCrossingCount);
        for (var i = 0; i < riverCrossingCount; i++)
            crossings.Add((r.ReadInt64(), r.ReadByte()));

        // 5. TerrainByProvince
        var paletteCount = r.ReadUInt16();
        var paletteDefs = new TerrainDefinition[paletteCount];
        for (var i = 0; i < paletteCount; i++)
            paletteDefs[i] = TerrainCatalog.Get(r.ReadString());
        var terrainIdx = r.ReadBytes(slots);
        if (terrainIdx.Length != slots) { reason = "truncated terrain palette indices"; return null; }
        var terrainByProvince = new TerrainDefinition[slots];
        for (var i = 0; i < slots; i++)
        {
            var idx = terrainIdx[i];
            if (idx < paletteDefs.Length) terrainByProvince[i] = paletteDefs[idx];
        }

        // 6. Optional TerrainSurfaceSlots
        var hasSlots = r.ReadBoolean();
        var surfaceSlots = hasSlots ? ReadByteRle(r, cellCount) : Array.Empty<byte>();
        if (hasSlots && surfaceSlots is null) { reason = "corrupt TerrainSurfaceSlots RLE"; return null; }

        // 7. Source strings and anchors
        var provinceSource = r.ReadString();
        var hydrologySource = r.ReadString();
        var countryAnchors = ReadLabelAnchors(r);
        var regionAnchors = ReadLabelAnchors(r);
        var riverAnchors = ReadRiverAnchors(r);

        // Construct ProvinceTopology via cached private constructor and populate auxiliary tables.
        var topology = (ProvinceTopology)TopologyCtor.Invoke(new object[]
        {
            cellToProvince, provinceToState, provinceToNation,
            provinceCenterCell, neighbors, width, height
        });
        RiverIntensityProp.SetValue(topology, riverIntensity);
        LakeAccessProp.SetValue(topology, lakeAccess);
        TerrainByProvinceProp.SetValue(topology, terrainByProvince);
        topology.TerrainSurfaceSlots = surfaceSlots;

        if (RiverCrossingsField.GetValue(topology) is Dictionary<long, byte> targetCrossings)
        {
            targetCrossings.Clear();
            targetCrossings.EnsureCapacity(crossings.Count);
            for (var i = 0; i < crossings.Count; i++)
                targetCrossings[crossings[i].Key] = crossings[i].Strength;
        }

        if (attachCrossings)
            LandCrossings.Attach(topology);
        TerritorialTopology.ApplyProvinceSliverHealing(topology);

        return new MapBootCacheData
        {
            Provinces = topology,
            ProvinceSource = provinceSource,
            HydrologySource = hydrologySource,
            CountryAnchors = countryAnchors,
            RegionAnchors = regionAnchors,
            RiverAnchors = riverAnchors,
            HasTerrainSurfaceSlots = hasSlots
        };
    }

    private static void WriteUShortRle(BinaryWriter w, ushort[] data)
    {
        var runsPos = w.BaseStream.Position;
        w.Write(0); // placeholder for runCount
        var runCount = 0;
        var i = 0;
        while (i < data.Length)
        {
            var val = data[i];
            var run = 1;
            while (i + run < data.Length && data[i + run] == val && run < ushort.MaxValue) run++;
            w.Write(val);
            w.Write((ushort)run);
            runCount++;
            i += run;
        }
        var endPos = w.BaseStream.Position;
        w.BaseStream.Position = runsPos;
        w.Write(runCount);
        w.BaseStream.Position = endPos;
    }

    private static ushort[] ReadUShortRle(BinaryReader r, int expectedLength, int maxProvinceId)
    {
        var runCount = r.ReadInt32();
        if (runCount < 0 || runCount > expectedLength) return null;
        var result = new ushort[expectedLength];
        var cursor = 0;
        for (var i = 0; i < runCount; i++)
        {
            var val = r.ReadUInt16();
            var run = r.ReadUInt16();
            if (run == 0 || val > maxProvinceId || cursor + run > expectedLength) return null;
            if (val != 0) result.AsSpan(cursor, run).Fill(val);
            cursor += run;
        }
        return cursor == expectedLength ? result : null;
    }

    private static void WriteByteRle(BinaryWriter w, byte[] data)
    {
        var runsPos = w.BaseStream.Position;
        w.Write(0);
        var runCount = 0;
        var i = 0;
        while (i < data.Length)
        {
            var val = data[i];
            var run = 1;
            while (i + run < data.Length && data[i + run] == val && run < ushort.MaxValue) run++;
            w.Write(val);
            w.Write((ushort)run);
            runCount++;
            i += run;
        }
        var endPos = w.BaseStream.Position;
        w.BaseStream.Position = runsPos;
        w.Write(runCount);
        w.BaseStream.Position = endPos;
    }

    private static byte[] ReadByteRle(BinaryReader r, int expectedLength)
    {
        var runCount = r.ReadInt32();
        if (runCount < 0 || runCount > expectedLength) return null;
        var result = new byte[expectedLength];
        var cursor = 0;
        for (var i = 0; i < runCount; i++)
        {
            var val = r.ReadByte();
            var run = r.ReadUInt16();
            if (run == 0 || cursor + run > expectedLength) return null;
            if (val != 0) result.AsSpan(cursor, run).Fill(val);
            cursor += run;
        }
        return cursor == expectedLength ? result : null;
    }

    private static void WriteUShortArray(BinaryWriter w, ushort[] data, int count)
    {
        if (BitConverter.IsLittleEndian)
            w.Write(MemoryMarshal.AsBytes(data.AsSpan(0, count)));
        else
            for (var i = 0; i < count; i++) w.Write(data[i]);
    }

    private static ushort[] ReadUShortArray(BinaryReader r, int count)
    {
        var arr = new ushort[count];
        if (BitConverter.IsLittleEndian)
        {
            var span = MemoryMarshal.AsBytes(arr.AsSpan());
            if (r.Read(span) != span.Length) throw new EndOfStreamException();
        }
        else
        {
            for (var i = 0; i < count; i++) arr[i] = r.ReadUInt16();
        }
        return arr;
    }

    private static void WriteIntArray(BinaryWriter w, int[] data, int count)
    {
        if (BitConverter.IsLittleEndian)
            w.Write(MemoryMarshal.AsBytes(data.AsSpan(0, count)));
        else
            for (var i = 0; i < count; i++) w.Write(data[i]);
    }

    private static int[] ReadIntArray(BinaryReader r, int count)
    {
        var arr = new int[count];
        if (BitConverter.IsLittleEndian)
        {
            var span = MemoryMarshal.AsBytes(arr.AsSpan());
            if (r.Read(span) != span.Length) throw new EndOfStreamException();
        }
        else
        {
            for (var i = 0; i < count; i++) arr[i] = r.ReadInt32();
        }
        return arr;
    }

    private static void WriteLabelAnchors(BinaryWriter w, CachedLabelAnchor[] anchors)
    {
        var count = anchors?.Length ?? 0;
        w.Write(count);
        for (var i = 0; i < count; i++)
        {
            var a = anchors[i];
            w.Write(a.Key);
            w.Write(a.CenterX);
            w.Write(a.CenterY);
            w.Write(a.AxisX);
            w.Write(a.AxisY);
            w.Write(a.HalfSpanPixels);
            w.Write(a.ThicknessPixels);
            w.Write(a.Cells);
        }
    }

    private static CachedLabelAnchor[] ReadLabelAnchors(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > 200_000) throw new InvalidDataException($"Invalid label anchor count {count}.");
        var arr = count == 0 ? Array.Empty<CachedLabelAnchor>() : new CachedLabelAnchor[count];
        for (var i = 0; i < count; i++)
            arr[i] = new CachedLabelAnchor(
                r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32());
        return arr;
    }

    private static void WriteRiverAnchors(BinaryWriter w, CachedRiverAnchor[] anchors)
    {
        var count = anchors?.Length ?? 0;
        w.Write(count);
        for (var i = 0; i < count; i++)
        {
            var a = anchors[i];
            w.Write(a.Key ?? "");
            w.Write(a.DefaultName ?? "");
            w.Write(a.Rank);
            w.Write(a.Latitude);
            w.Write(a.Longitude);
            w.Write(a.AxisX);
            w.Write(a.AxisY);
            w.Write(a.SpanDegrees);
        }
    }

    private static CachedRiverAnchor[] ReadRiverAnchors(BinaryReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > 50_000) throw new InvalidDataException($"Invalid river anchor count {count}.");
        var arr = count == 0 ? Array.Empty<CachedRiverAnchor>() : new CachedRiverAnchor[count];
        for (var i = 0; i < count; i++)
            arr[i] = new CachedRiverAnchor(
                r.ReadString(), r.ReadString(), r.ReadInt32(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return arr;
    }

    /// <summary>64-bit FNV-1a checksum for fast corruption detection on cache loads.</summary>
    public static ulong ComputeFnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * 1099511628211UL;
        return hash;
    }

    /// <summary>
    /// Resolve the best readable <c>hydrology_v2.bin</c>: writable cache directory first, then an
    /// optional read-only shipped fallback. Returns <c>null</c> whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static string ResolveReadableHydrologyCachePath(string writableCacheDirectory, string shippedFallbackPath = null)
    {
        if (ModOverlayPolicy.IsActive) return null;
        if (!string.IsNullOrWhiteSpace(writableCacheDirectory))
        {
            var writable = Path.Combine(writableCacheDirectory, HydrologyFileName);
            if (File.Exists(writable)) return writable;
        }
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return shippedFallbackPath;
        return null;
    }

    /// <summary>
    /// Atomically write compressed pre-rasterized lake SDF (R8) and river centreline (RGBA8) layers
    /// to <c>hydrology_v2.bin</c>. Bypassed cleanly whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static bool TryWriteHydrology(
        string cacheDirectory,
        ulong regionFingerprint,
        int width,
        int height,
        HydrologyBootCacheData data,
        out string error)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            error = "no cache directory";
            return false;
        }
        var targetPath = Path.Combine(cacheDirectory, HydrologyFileName);
        return TryWriteHydrologyToFile(targetPath, regionFingerprint, width, height, data, out error);
    }

    /// <summary>Atomically write compressed hydrology surface layers to an explicit file path.</summary>
    public static bool TryWriteHydrologyToFile(
        string targetPath,
        ulong regionFingerprint,
        int width,
        int height,
        HydrologyBootCacheData data,
        out string error)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(targetPath) || data is null || width <= 0 || height <= 0)
        {
            error = "invalid hydrology cache arguments";
            return false;
        }
        var expectedLakeLen = width * height;
        var expectedRiverLen = width * height * 4;
        var lake = data.LakeSdfR8 ?? Array.Empty<byte>();
        var river = data.RiverCentreRgba8 ?? Array.Empty<byte>();
        if (lake.Length != expectedLakeLen || (river.Length != 0 && river.Length != expectedRiverLen))
        {
            error = "hydrology buffer length mismatch";
            return false;
        }

        string tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            byte[] compressed;
            using (var rawOut = new MemoryStream(Math.Max(64 * 1024, expectedLakeLen / 8)))
            {
                using (var deflate = new DeflateStream(rawOut, CompressionLevel.Fastest, leaveOpen: true))
                using (var bw = new BinaryWriter(deflate, System.Text.Encoding.UTF8, leaveOpen: true))
                {
                    bw.Write(data.HydrologySource ?? "");
                    bw.Write(lake.Length);
                    bw.Write(lake);
                    bw.Write(river.Length);
                    if (river.Length > 0) bw.Write(river);
                    bw.Flush();
                }
                compressed = rawOut.ToArray();
            }

            var checksum = ComputeFnv1a64(compressed);
            tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(HydrologyMagic);
                writer.Write(HydrologyFormatVersion);
                writer.Write(regionFingerprint);
                writer.Write(width);
                writer.Write(height);
                writer.Write(compressed.Length);
                writer.Write(checksum);
                writer.Write(compressed);
                writer.Flush();
            }

            File.Move(tempPath, targetPath, overwrite: true);
            tempPath = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Try to load and validate <c>hydrology_v2.bin</c> from <paramref name="writableCacheDirectory"/> or
    /// <paramref name="shippedFallbackPath"/>. Never throws on missing, stale, or corrupt files.
    /// </summary>
    public static bool TryReadHydrology(
        string writableCacheDirectory,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out HydrologyBootCacheData data,
        out string reason,
        string shippedFallbackPath = null)
    {
        data = null;
        if (ModOverlayPolicy.IsActive)
        {
            reason = "bypassed: mod overlay active";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(writableCacheDirectory))
        {
            var writable = Path.Combine(writableCacheDirectory, HydrologyFileName);
            if (File.Exists(writable))
            {
                if (TryReadHydrologyFile(writable, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason))
                    return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadHydrologyFile(shippedFallbackPath, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason);

        reason = "hydrology cache file not found";
        return false;
    }

    /// <summary>Try to load and validate an explicit <c>hydrology_v2.bin</c> file path.</summary>
    public static bool TryReadHydrologyFile(
        string filePath,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out HydrologyBootCacheData data,
        out string reason)
    {
        data = null;
        reason = "";
        if (ModOverlayPolicy.IsActive)
        {
            reason = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            reason = "hydrology cache file not found";
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (stream.Length < HydrologyHeaderSizeBytes)
            {
                reason = "truncated hydrology header";
                return false;
            }
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadUInt32();
            if (magic != HydrologyMagic) { reason = $"bad hydrology magic 0x{magic:X8}"; return false; }
            var version = reader.ReadInt32();
            if (version != HydrologyFormatVersion) { reason = $"hydrology version mismatch {version} != {HydrologyFormatVersion}"; return false; }
            var fingerprint = reader.ReadUInt64();
            if (fingerprint != expectedFingerprint) { reason = "hydrology region fingerprint mismatch"; return false; }
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            if (width != expectedWidth || height != expectedHeight) { reason = $"hydrology dimensions mismatch {width}x{height}"; return false; }
            var payloadLength = reader.ReadInt32();
            var expectedChecksum = reader.ReadUInt64();
            if (payloadLength <= 0 || stream.Length - HydrologyHeaderSizeBytes != payloadLength)
            {
                reason = "hydrology payload length mismatch";
                return false;
            }

            var compressed = reader.ReadBytes(payloadLength);
            if (compressed.Length != payloadLength)
            {
                reason = "truncated hydrology payload";
                return false;
            }
            var actualChecksum = ComputeFnv1a64(compressed);
            if (actualChecksum != expectedChecksum)
            {
                reason = "hydrology checksum mismatch";
                return false;
            }

            var expectedLakeLen = expectedWidth * expectedHeight;
            var expectedRiverLen = expectedWidth * expectedHeight * 4;
            using var ms = new MemoryStream(compressed, writable: false);
            using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
            using var br = new BinaryReader(deflate, System.Text.Encoding.UTF8);

            var source = br.ReadString();
            var lakeLen = br.ReadInt32();
            if (lakeLen != expectedLakeLen) { reason = $"invalid lake buffer length {lakeLen}"; return false; }
            var lake = ReadExactBytes(br, lakeLen);
            var riverLen = br.ReadInt32();
            if (riverLen != 0 && riverLen != expectedRiverLen) { reason = $"invalid river buffer length {riverLen}"; return false; }
            var river = riverLen > 0 ? ReadExactBytes(br, riverLen) : Array.Empty<byte>();

            data = new HydrologyBootCacheData
            {
                LakeSdfR8 = lake,
                RiverCentreRgba8 = river,
                HydrologySource = source
            };
            reason = $"hit ({Path.GetFileName(filePath)}, {payloadLength / 1024:N0} KB)";
            return true;
        }
        catch (Exception ex)
        {
            data = null;
            reason = $"hydrology read error: {ex.Message}";
            return false;
        }
    }

    private static byte[] ReadExactBytes(BinaryReader reader, int length)
    {
        var buffer = new byte[length];
        var offset = 0;
        while (offset < length)
        {
            var read = reader.Read(buffer, offset, length - offset);
            if (read <= 0) throw new EndOfStreamException("Unexpected end of compressed hydrology payload.");
            offset += read;
        }
        return buffer;
    }

    /// <summary>
    /// Resolve the best readable <c>political_rasters_v1.bin</c>: writable cache directory first, then an
    /// optional read-only shipped fallback. Returns <c>null</c> whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static string ResolveReadablePoliticalRasterCachePath(string writableCacheDirectory, string shippedFallbackPath = null)
    {
        if (ModOverlayPolicy.IsActive) return null;
        if (!string.IsNullOrWhiteSpace(writableCacheDirectory))
        {
            var writable = Path.Combine(writableCacheDirectory, PoliticalRasterFileName);
            if (File.Exists(writable)) return writable;
        }
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return shippedFallbackPath;
        return null;
    }

    /// <summary>
    /// Atomically write LZ4-compressed packed political &amp; plate raster buffers to
    /// <c>political_rasters_v1.bin</c>. Bypassed cleanly whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static bool TryWritePoliticalRasters(
        string cacheDirectory,
        ulong regionFingerprint,
        int width,
        int height,
        int politicalScale,
        PoliticalRasterBootCacheData data,
        out string error)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cacheDirectory))
        {
            error = "no cache directory";
            return false;
        }
        var targetPath = Path.Combine(cacheDirectory, PoliticalRasterFileName);
        return TryWritePoliticalRastersToFile(targetPath, regionFingerprint, width, height, politicalScale, data, out error);
    }

    /// <summary>Atomically write LZ4-compressed packed political &amp; plate raster buffers to an explicit file path.</summary>
    public static bool TryWritePoliticalRastersToFile(
        string targetPath,
        ulong regionFingerprint,
        int width,
        int height,
        int politicalScale,
        PoliticalRasterBootCacheData data,
        out string error)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(targetPath) || data is null ||
            !ValidatePoliticalRasterDimensions(width, height, politicalScale, out var plateCount, out var fullRgBytes))
        {
            error = "invalid political raster cache arguments";
            return false;
        }

        if (data.PlateProvinces is null || data.PlateProvinces.Length != plateCount ||
            data.PlateHolders is null || data.PlateHolders.Length != plateCount ||
            data.PlateGeography is null || data.PlateGeography.Length != plateCount ||
            data.PlateClass is null || data.PlateClass.Length != plateCount ||
            data.RegionIds is null || data.RegionIds.Length != fullRgBytes ||
            data.ProvinceIds is null || data.ProvinceIds.Length != fullRgBytes ||
            data.NationIds is null || data.NationIds.Length != fullRgBytes)
        {
            error = "political raster buffer length mismatch";
            return false;
        }

        string tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);

            using var payloadMs = new MemoryStream(Math.Max(64 * 1024, Math.Min(4 * 1024 * 1024, fullRgBytes / 16)));
            using (var bw = new BinaryWriter(payloadMs, System.Text.Encoding.UTF8, leaveOpen: true))
            {
                WriteLz4UShortBlock(bw, data.PlateProvinces);
                WriteLz4UShortBlock(bw, data.PlateHolders);
                WriteLz4Block(bw, data.PlateGeography);
                WriteLz4Block(bw, data.PlateClass);
                WriteLz4Block(bw, data.RegionIds);
                WriteLz4Block(bw, data.ProvinceIds);
                WriteLz4Block(bw, data.NationIds);
                bw.Flush();
            }

            if (!payloadMs.TryGetBuffer(out var payloadSegment))
                payloadSegment = new ArraySegment<byte>(payloadMs.ToArray());
            var payloadSpan = payloadSegment.AsSpan();
            var checksum = ComputeFnv1a64(payloadSpan);

            tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(PoliticalRasterMagic);
                writer.Write(PoliticalRasterFormatVersion);
                writer.Write(regionFingerprint);
                writer.Write(width);
                writer.Write(height);
                writer.Write(politicalScale);
                writer.Write(payloadSpan.Length);
                writer.Write(checksum);
                writer.Write(payloadSpan);
                writer.Flush();
            }

            File.Move(tempPath, targetPath, overwrite: true);
            tempPath = null;
            return true;
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
        finally
        {
            if (tempPath is not null)
            {
                try { if (File.Exists(tempPath)) File.Delete(tempPath); } catch { }
            }
        }
    }

    /// <summary>
    /// Try to load and validate <c>political_rasters_v1.bin</c> from <paramref name="writableCacheDirectory"/> or
    /// <paramref name="shippedFallbackPath"/>. Never throws on missing, stale, or corrupt files.
    /// </summary>
    public static bool TryReadPoliticalRasters(
        string writableCacheDirectory,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        int expectedPoliticalScale,
        out PoliticalRasterBootCacheData data,
        out string reason,
        string shippedFallbackPath = null)
    {
        data = null;
        if (ModOverlayPolicy.IsActive)
        {
            reason = "bypassed: mod overlay active";
            return false;
        }

        if (!string.IsNullOrWhiteSpace(writableCacheDirectory))
        {
            var writable = Path.Combine(writableCacheDirectory, PoliticalRasterFileName);
            if (File.Exists(writable))
            {
                if (TryReadPoliticalRastersFile(writable, expectedFingerprint, expectedWidth, expectedHeight, expectedPoliticalScale, out data, out reason))
                    return true;
            }
        }

        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadPoliticalRastersFile(shippedFallbackPath, expectedFingerprint, expectedWidth, expectedHeight, expectedPoliticalScale, out data, out reason);

        reason = "political raster cache file not found";
        return false;
    }

    /// <summary>Try to load and validate an explicit <c>political_rasters_v1.bin</c> file path.</summary>
    public static bool TryReadPoliticalRastersFile(
        string filePath,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        int expectedPoliticalScale,
        out PoliticalRasterBootCacheData data,
        out string reason)
    {
        data = null;
        reason = "";
        if (ModOverlayPolicy.IsActive)
        {
            reason = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
        {
            reason = "political raster cache file not found";
            return false;
        }
        if (!ValidatePoliticalRasterDimensions(expectedWidth, expectedHeight, expectedPoliticalScale, out var plateCount, out var fullRgBytes))
        {
            reason = "invalid expected political raster dimensions";
            return false;
        }

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (stream.Length < PoliticalRasterHeaderSizeBytes)
            {
                reason = "truncated political raster header";
                return false;
            }
            using var reader = new BinaryReader(stream);
            var magic = reader.ReadUInt32();
            if (magic != PoliticalRasterMagic) { reason = $"bad political raster magic 0x{magic:X8}"; return false; }
            var version = reader.ReadInt32();
            if (version != PoliticalRasterFormatVersion) { reason = $"political raster version mismatch {version} != {PoliticalRasterFormatVersion}"; return false; }
            var fingerprint = reader.ReadUInt64();
            if (fingerprint != expectedFingerprint) { reason = "political raster region fingerprint mismatch"; return false; }
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            var politicalScale = reader.ReadInt32();
            if (width != expectedWidth || height != expectedHeight || politicalScale != expectedPoliticalScale)
            {
                reason = $"political raster dimensions mismatch {width}x{height}/{politicalScale}";
                return false;
            }
            var payloadLength = reader.ReadInt32();
            var expectedChecksum = reader.ReadUInt64();
            if (payloadLength <= 0 || stream.Length - PoliticalRasterHeaderSizeBytes != payloadLength)
            {
                reason = "political raster payload length mismatch";
                return false;
            }

            var payload = ReadExactBytes(reader, payloadLength);
            if (ComputeFnv1a64(payload) != expectedChecksum)
            {
                reason = "political raster checksum mismatch";
                return false;
            }

            var plateProvinces = new ushort[plateCount];
            var plateHolders = new ushort[plateCount];
            var plateGeography = new byte[plateCount];
            var plateClass = new byte[plateCount];
            var regionIds = new byte[fullRgBytes];
            var provinceIds = new byte[fullRgBytes];
            var nationIds = new byte[fullRgBytes];

            ReadOnlySpan<byte> payloadSpan = payload;
            var offset = 0;
            if (!TryReadLz4UShortBlock(payloadSpan, ref offset, plateProvinces) ||
                !TryReadLz4UShortBlock(payloadSpan, ref offset, plateHolders) ||
                !TryReadLz4ByteBlock(payloadSpan, ref offset, plateGeography) ||
                !TryReadLz4ByteBlock(payloadSpan, ref offset, plateClass) ||
                !TryReadLz4ByteBlock(payloadSpan, ref offset, regionIds) ||
                !TryReadLz4ByteBlock(payloadSpan, ref offset, provinceIds) ||
                !TryReadLz4ByteBlock(payloadSpan, ref offset, nationIds) ||
                offset != payloadSpan.Length)
            {
                reason = "corrupt political raster LZ4 payload";
                return false;
            }

            data = new PoliticalRasterBootCacheData
            {
                PlateProvinces = plateProvinces,
                PlateHolders = plateHolders,
                PlateGeography = plateGeography,
                PlateClass = plateClass,
                RegionIds = regionIds,
                ProvinceIds = provinceIds,
                NationIds = nationIds
            };
            reason = $"hit ({Path.GetFileName(filePath)}, {payloadLength / 1024:N0} KB)";
            return true;
        }
        catch (Exception ex)
        {
            data = null;
            reason = $"political raster read error: {ex.Message}";
            return false;
        }
    }

    private static bool ValidatePoliticalRasterDimensions(int width, int height, int politicalScale, out int plateCount, out int fullRgBytes)
    {
        plateCount = 0;
        fullRgBytes = 0;
        if (width <= 0 || height <= 0 || politicalScale <= 0) return false;
        if (width % politicalScale != 0 || height % politicalScale != 0) return false;
        var politicalWidth = width / politicalScale;
        var politicalHeight = height / politicalScale;
        var fullPixels = (long)width * height;
        var platePixels = (long)politicalWidth * politicalHeight;
        if (fullPixels * 2L > int.MaxValue || platePixels * 2L > int.MaxValue) return false;
        plateCount = (int)platePixels;
        fullRgBytes = (int)(fullPixels * 2L);
        return true;
    }

    private static void WriteLz4UShortBlock(BinaryWriter bw, ushort[] source)
    {
        if (BitConverter.IsLittleEndian)
        {
            WriteLz4Block(bw, MemoryMarshal.AsBytes(source.AsSpan()));
            return;
        }
        var bytes = new byte[source.Length * 2];
        for (var i = 0; i < source.Length; i++)
            BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), source[i]);
        WriteLz4Block(bw, bytes);
    }

    private static void WriteLz4Block(BinaryWriter bw, ReadOnlySpan<byte> raw)
    {
        var pickled = LZ4Pickler.Pickle(raw, LZ4Level.L00_FAST);
        bw.Write(pickled.Length);
        bw.Write(pickled);
    }

    private static bool TryReadLz4UShortBlock(ReadOnlySpan<byte> payload, ref int offset, ushort[] destination)
    {
        if (BitConverter.IsLittleEndian)
            return TryReadLz4ByteBlock(payload, ref offset, MemoryMarshal.AsBytes(destination.AsSpan()));

        var temp = new byte[destination.Length * 2];
        if (!TryReadLz4ByteBlock(payload, ref offset, temp)) return false;
        for (var i = 0; i < destination.Length; i++)
            destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(temp.AsSpan(i * 2, 2));
        return true;
    }

    private static bool TryReadLz4ByteBlock(ReadOnlySpan<byte> payload, ref int offset, Span<byte> destination)
    {
        if (offset + 4 > payload.Length) return false;
        var compressedLen = BinaryPrimitives.ReadInt32LittleEndian(payload.Slice(offset, 4));
        offset += 4;
        if (compressedLen <= 0 || offset + compressedLen > payload.Length) return false;
        var block = payload.Slice(offset, compressedLen);
        offset += compressedLen;
        if (LZ4Pickler.UnpickledSize(block) != destination.Length) return false;
        LZ4Pickler.Unpickle(block, destination);
        return true;
    }
}
