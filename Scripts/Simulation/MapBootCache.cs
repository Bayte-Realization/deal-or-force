using System;
using System.Buffers;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.IO.Hashing;
using System.Reflection;
using System.Runtime.InteropServices;
using System.Text;
using BitFaster.Caching.Lru;
using CommunityToolkit.HighPerformance;
using CommunityToolkit.HighPerformance.Buffers;
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

/// <summary>Serialized lake state descriptor stored in <c>world_raster_boot_v1.bin</c>.</summary>
public readonly record struct CachedBootLake(ushort MapIndex, string Name);

/// <summary>Serialized donor region index resolved for a rule state stored in <c>world_raster_boot_v1.bin</c>.</summary>
internal readonly record struct CachedBootRuleDonor(ushort RuleMapIndex, ushort DonorMapIndex);

/// <summary>Complete pre-baked province topology and label anchor bundle restored from <c>map_boot_v2.bin</c>.</summary>
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
/// Checksummed binary map boot caches (<c>map_boot_v2.bin</c>, <c>hydrology_v2.bin</c>,
/// <c>political_rasters_v1.bin</c>, and <c>world_raster_boot_v1.bin</c>) built on pooled
/// <see cref="ArrayPoolBufferWriter{T}"/> writers, SIMD run-length scans, and zero-LOH LZ4 framing.
/// Automatically bypassed whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
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

    public const string BootRasterFileName = "world_raster_boot_v1.bin";
    public const uint BootRasterMagic = 0x43524D44u; // "DMRC"
    public const int BootRasterFormatVersion = 2;
    private const int BootRasterHeaderSizeBytes = 88;

    private const ulong FnvOffsetBasis = 14695981039346656037UL;
    private const ulong FnvPrime = 1099511628211UL;
    private const int BootRasterInitialBufferBytes = 256 * 1024;
    private const int MinLakeWriterBytes = 1024;
    private const int UShortRleEntryBytes = sizeof(ushort) * 2;
    private const int MaxRiverCrossings = 2_000_000;
    private const int MaxLabelAnchors = 200_000;
    private const int MaxRiverAnchors = 50_000;
    private const int MaxBootLakes = 16_384;
    private const int MaxBootRuleDonors = 16_384;
    private const int MaxLakeSectionBytes = 4 * 1024 * 1024;
    private const int MaxCompressedBlockBytes = 256 * 1024 * 1024;
    private const int IoBufferBytes = 65536;
    private const int MaxTerrainPaletteEntries = 254;
    private const byte UnassignedTerrainSlot = 0xFF;
    private const int FileHashCacheCapacity = 64;

    private readonly record struct CachedFileXxHash(long Length, long LastWriteTicksUtc, long Seed, ulong Hash);

    // Bounded lock-free/striped LRU memoizing disk file XxHash3 digests keyed by normalized path.
    private static readonly ConcurrentLru<string, CachedFileXxHash> FileHashCache = new(FileHashCacheCapacity);

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

    /// <summary>Resolve the writable user cache directory matching <see cref="LakeRegions"/>.</summary>
    public static string DefaultWritableCacheDirectory()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            for (var i = 0; i < args.Length; i++)
            {
                if (!string.Equals(args[i], "--verify-build", StringComparison.Ordinal)) continue;
                var verifyDocs = Environment.GetEnvironmentVariable("DOF_VERIFICATION_DOCUMENTS");
                if (!string.IsNullOrWhiteSpace(verifyDocs) && Path.IsPathRooted(verifyDocs))
                {
                    var verifyFolders = GameFolders.For(verifyDocs);
                    if (verifyFolders is not null) return Path.Combine(verifyFolders.Root, "cache");
                }
                break;
            }

            if (OperatingSystem.IsAndroid() || OperatingSystem.IsIOS())
            {
                var baseDir = AppContext.BaseDirectory;
                if (!string.IsNullOrWhiteSpace(baseDir)) return Path.Combine(baseDir, "cache");
            }

            var docs = Environment.GetFolderPath(Environment.SpecialFolder.MyDocuments);
            var docFolders = GameFolders.For(docs);
            if (docFolders is not null) return Path.Combine(docFolders.Root, "cache");

            var localApp = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            var localFolders = GameFolders.For(localApp);
            if (localFolders is not null) return Path.Combine(localFolders.Root, "cache");
        }
        catch (Exception)
        {
            // Fall through when special folders are unavailable in restricted hosts.
        }
        return null;
    }

    /// <summary>Resolve the best readable cache file path across writable user cache and shipped fallback.</summary>
    public static string ResolveReadableCachePath(string writableCacheDirectory, string shippedFallbackPath = null) =>
        ResolveReadablePath(FileName, writableCacheDirectory, shippedFallbackPath);

    /// <summary>Resolve the writable cache file path, or <c>null</c> if mod overlays are active.</summary>
    public static string ResolveWritableCachePath(string writableCacheDirectory) =>
        ModOverlayPolicy.IsActive || string.IsNullOrWhiteSpace(writableCacheDirectory)
            ? null
            : Path.Combine(writableCacheDirectory, FileName);

    /// <summary>Atomically write <paramref name="data"/> to <c>map_boot_v2.bin</c> under <paramref name="cacheDirectory"/>.</summary>
    public static bool TryWrite(
        string cacheDirectory,
        ulong regionFingerprint,
        MapBootCacheData data,
        out string error,
        bool includeTerrainSurfaceSlots = true)
    {
        if (!ValidateCacheWriteTarget(cacheDirectory, FileName, data?.Provinces is null ? "missing province topology" : null, out var targetPath, out error))
            return false;
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
        if (ModOverlayPolicy.IsActive) { error = "bypassed: mod overlay active"; return false; }
        if (string.IsNullOrWhiteSpace(targetPath) || data?.Provinces is null) { error = "invalid cache write arguments"; return false; }

        try
        {
            var provinces = data.Provinces;
            using var payloadWriter = new ArrayPoolBufferWriter<byte>(Math.Max(IoBufferBytes, provinces.CellToProvince.Length / 4));
            EncodePayload(data, payloadWriter, includeTerrainSurfaceSlots);
            var payload = payloadWriter.WrittenSpan;
            var checksum = ComputeFnv1a64(payload);
            Span<byte> header = stackalloc byte[HeaderSizeBytes];
            var hw = new SpanWriter(header);
            hw.WriteUInt32(Magic);
            hw.WriteInt32(FormatVersion);
            hw.WriteUInt64(regionFingerprint);
            hw.WriteInt32(provinces.Width);
            hw.WriteInt32(provinces.Height);
            hw.WriteInt32(provinces.ProvinceCount);
            hw.WriteInt32(payload.Length);
            hw.WriteUInt64(checksum);
            return AtomicWriteSegments(targetPath, out error, header, payload);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Try to load and validate <c>map_boot_v2.bin</c> from writable or shipped fallback storage.</summary>
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
        if (ModOverlayPolicy.IsActive) { reason = "bypassed: mod overlay active"; return false; }
        if (TryResolveReadableWritable(writableCacheDirectory, FileName, out var writable) &&
            TryReadFile(writable, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason, attachCrossings))
            return true;
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadFile(shippedFallbackPath, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason, attachCrossings);

        reason = "cache file not found";
        return false;
    }

    /// <summary>Try to load and validate an explicit <c>map_boot_v2.bin</c> file path.</summary>
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
        if (!HasTopologyReflection()) { reason = "topology reflection contract unavailable"; return false; }
        if (!CanReadCachePath(filePath, "cache file not found", out reason)) return false;

        byte[] rentedPayload = null;
        try
        {
            using var stream = OpenReadStream(filePath);
            if (!TryReadTopologyHeader(stream, expectedFingerprint, expectedWidth, expectedHeight,
                    out var provinceCount, out var payloadLength, out var expectedChecksum, out reason))
                return false;

            rentedPayload = ArrayPool<byte>.Shared.Rent(payloadLength);
            stream.ReadExactly(rentedPayload, 0, payloadLength);
            var payload = rentedPayload.AsSpan(0, payloadLength);
            if (ComputeFnv1a64(payload) != expectedChecksum)
            {
                reason = "checksum mismatch";
                return false;
            }

            data = DecodePayload(payload, expectedWidth, expectedHeight, provinceCount, attachCrossings, out reason);
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
        finally
        {
            if (rentedPayload is not null) ArrayPool<byte>.Shared.Return(rentedPayload);
        }
    }

    internal static bool TryReadTopologyProvinceStates(
        string writableCacheDirectory,
        int expectedWidth,
        int expectedHeight,
        out ushort[] provinceToState,
        out ulong regionFingerprint,
        string shippedFallbackPath = null)
    {
        provinceToState = null;
        regionFingerprint = 0;
        if (ModOverlayPolicy.IsActive) return false;
        if (TryResolveReadableWritable(writableCacheDirectory, FileName, out var writable) &&
            TryReadTopologyProvinceStatesFile(writable, expectedWidth, expectedHeight, out provinceToState, out regionFingerprint))
            return true;
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadTopologyProvinceStatesFile(shippedFallbackPath, expectedWidth, expectedHeight, out provinceToState, out regionFingerprint);
        return false;
    }

    internal static bool TryReadTopologyProvinceStatesFile(
        string filePath,
        int expectedWidth,
        int expectedHeight,
        out ushort[] provinceToState,
        out ulong regionFingerprint)
    {
        provinceToState = null;
        regionFingerprint = 0;
        if (!CanReadCachePath(filePath, "", out _)) return false;

        try
        {
            using var stream = OpenReadStream(filePath);
            if (!TryReadTopologyHeaderCore(stream, verifyFingerprint: false, 0UL, expectedWidth, expectedHeight,
                    out regionFingerprint, out var provinceCount, out var payloadLength, out _, out _))
                return false;

            Span<byte> runCountBuf = stackalloc byte[sizeof(int)];
            stream.ReadExactly(runCountBuf);
            var runCount = BinaryPrimitives.ReadInt32LittleEndian(runCountBuf);
            var cellCount = (long)expectedWidth * expectedHeight;
            if (runCount <= 0 || runCount > cellCount) return false;

            var rleBytes = (long)runCount * UShortRleEntryBytes;
            var slots = provinceCount + 1;
            var stateBytes = slots * sizeof(ushort);
            if (sizeof(int) + rleBytes + stateBytes > payloadLength) return false;

            stream.Seek(rleBytes, SeekOrigin.Current);
            var rawBytes = ArrayPool<byte>.Shared.Rent(stateBytes);
            try
            {
                stream.ReadExactly(rawBytes, 0, stateBytes);
                provinceToState = new ushort[slots];
                for (var i = 0; i < slots; i++)
                    provinceToState[i] = BinaryPrimitives.ReadUInt16LittleEndian(rawBytes.AsSpan(i * sizeof(ushort), sizeof(ushort)));
                return true;
            }
            finally
            {
                ArrayPool<byte>.Shared.Return(rawBytes);
            }
        }
        catch
        {
            provinceToState = null;
            regionFingerprint = 0;
            return false;
        }
    }

    private static bool TryReadTopologyHeader(
        FileStream stream,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out int provinceCount,
        out int payloadLength,
        out ulong expectedChecksum,
        out string reason) =>
        TryReadTopologyHeaderCore(stream, verifyFingerprint: true, expectedFingerprint, expectedWidth, expectedHeight,
            out _, out provinceCount, out payloadLength, out expectedChecksum, out reason);

    private static bool TryReadTopologyHeaderCore(
        FileStream stream,
        bool verifyFingerprint,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out ulong fileFingerprint,
        out int provinceCount,
        out int payloadLength,
        out ulong expectedChecksum,
        out string reason)
    {
        fileFingerprint = 0;
        provinceCount = 0;
        payloadLength = 0;
        expectedChecksum = 0;
        if (stream.Length < HeaderSizeBytes) { reason = "truncated cache header"; return false; }
        Span<byte> header = stackalloc byte[HeaderSizeBytes];
        stream.ReadExactly(header);
        var r = new SpanReader(header);
        var magic = r.ReadUInt32();
        if (magic != Magic) { reason = $"bad magic 0x{magic:X8}"; return false; }
        var version = r.ReadInt32();
        if (version != FormatVersion) { reason = $"version mismatch {version} != {FormatVersion}"; return false; }
        fileFingerprint = r.ReadUInt64();
        if (verifyFingerprint && fileFingerprint != expectedFingerprint)
        {
            reason = "region fingerprint mismatch";
            return false;
        }
        var width = r.ReadInt32();
        var height = r.ReadInt32();
        if (width != expectedWidth || height != expectedHeight)
        {
            reason = $"dimensions mismatch {width}x{height}";
            return false;
        }
        provinceCount = r.ReadInt32();
        if (provinceCount <= 0 || provinceCount > ushort.MaxValue)
        {
            reason = $"invalid province count {provinceCount}";
            return false;
        }
        payloadLength = r.ReadInt32();
        expectedChecksum = r.ReadUInt64();
        if (payloadLength <= 0 || stream.Length - HeaderSizeBytes != payloadLength)
        {
            reason = "payload length mismatch";
            return false;
        }
        reason = "";
        return true;
    }

    private static void EncodePayload(MapBootCacheData data, ArrayPoolBufferWriter<byte> w, bool includeTerrainSurfaceSlots)
    {
        var p = data.Provinces;
        var slots = p.ProvinceCount + 1;

        WriteUShortRle(w, p.CellToProvince);
        WriteUShortSpan(w, p.ProvinceToState.AsSpan(0, slots));
        WriteUShortSpan(w, p.ProvinceToNation.AsSpan(0, slots));
        WriteIntSpan(w, p.ProvinceCenterCell.AsSpan(0, slots));
        WriteNeighborGraph(w, p.LandNeighbors, slots);
        WriteHydrologySection(w, p, slots);
        WriteTerrainPaletteSection(w, p.TerrainByProvince, slots);

        var hasSlots = includeTerrainSurfaceSlots &&
                       p.TerrainSurfaceSlots is { Length: > 0 } surf &&
                       surf.Length == p.Width * p.Height;
        WriteByte(w, hasSlots ? (byte)1 : (byte)0);
        if (hasSlots) WriteByteRle(w, p.TerrainSurfaceSlots);

        WriteString(w, data.ProvinceSource ?? "");
        WriteString(w, data.HydrologySource ?? "");
        WriteLabelAnchors(w, data.CountryAnchors);
        WriteLabelAnchors(w, data.RegionAnchors);
        WriteRiverAnchors(w, data.RiverAnchors);
    }

    private static void WriteNeighborGraph(ArrayPoolBufferWriter<byte> w, int[][] landNeighbors, int slots)
    {
        for (var i = 0; i < slots; i++)
        {
            var row = i < landNeighbors.Length ? landNeighbors[i] : Array.Empty<int>();
            WriteUInt16(w, (ushort)row.Length);
            WriteIntSpan(w, row);
        }
    }

    private static void WriteHydrologySection(ArrayPoolBufferWriter<byte> w, ProvinceTopology p, int slots)
    {
        var rivers = p.RiverIntensityByProvince;
        var riverDst = w.GetSpan(slots);
        var copyCount = Math.Min(slots, rivers.Length);
        if (copyCount > 0) rivers.AsSpan(0, copyCount).CopyTo(riverDst);
        if (copyCount < slots) riverDst.Slice(copyCount, slots - copyCount).Clear();
        w.Advance(slots);

        var lakes = p.LakeAccessByProvince;
        var lakeByteCount = (slots + 7) >> 3;
        var lakeDst = w.GetSpan(lakeByteCount);
        lakeDst.Slice(0, lakeByteCount).Clear();
        for (var i = 0; i < slots && i < lakes.Length; i++)
            if (lakes[i]) lakeDst[i >> 3] |= (byte)(1 << (i & 7));
        w.Advance(lakeByteCount);

        var crossings = RiverCrossingsField?.GetValue(p) as Dictionary<long, byte>;
        WriteInt32(w, crossings?.Count ?? 0);
        if (crossings is null) return;
        foreach (var kv in crossings)
        {
            WriteInt64(w, kv.Key);
            WriteByte(w, kv.Value);
        }
    }

    private static void WriteTerrainPaletteSection(ArrayPoolBufferWriter<byte> w, TerrainDefinition[] terrainArr, int slots)
    {
        terrainArr ??= Array.Empty<TerrainDefinition>();
        var palette = new List<string>(16);
        var paletteIdx = new Dictionary<string, byte>(StringComparer.OrdinalIgnoreCase);
        var terrainBytes = ArrayPool<byte>.Shared.Rent(slots);
        try
        {
            terrainBytes.AsSpan(0, slots).Fill(UnassignedTerrainSlot);
            for (var i = 0; i < slots && i < terrainArr.Length; i++)
            {
                var def = terrainArr[i];
                if (def is null || string.IsNullOrEmpty(def.Id)) continue;
                if (!paletteIdx.TryGetValue(def.Id, out var slot))
                {
                    if (palette.Count >= MaxTerrainPaletteEntries) continue;
                    slot = (byte)palette.Count;
                    palette.Add(def.Id);
                    paletteIdx[def.Id] = slot;
                }
                terrainBytes[i] = slot;
            }
            WriteUInt16(w, (ushort)palette.Count);
            for (var i = 0; i < palette.Count; i++) WriteString(w, palette[i]);
            WriteBytes(w, terrainBytes.AsSpan(0, slots));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(terrainBytes);
        }
    }

    private static MapBootCacheData DecodePayload(
        ReadOnlySpan<byte> payload,
        int width,
        int height,
        int provinceCount,
        bool attachCrossings,
        out string reason)
    {
        reason = "";
        var cellCount = width * height;
        var slots = provinceCount + 1;
        var r = new SpanReader(payload);

        var cellToProvince = ReadUShortRle(ref r, cellCount, provinceCount);
        if (cellToProvince is null) { reason = "corrupt CellToProvince RLE"; return null; }

        var provinceToState = ReadUShortArray(ref r, slots);
        var provinceToNation = ReadUShortArray(ref r, slots);
        var provinceCenterCell = ReadIntArray(ref r, slots);
        if (!ValidateCenters(provinceCenterCell, slots, cellCount, out reason)) return null;

        var neighbors = ReadNeighborGraph(ref r, slots, provinceCount, out reason);
        if (neighbors is null) return null;

        if (!ReadHydrologySection(ref r, slots, out var riverIntensity, out var lakeAccess, out var crossings, out reason))
            return null;

        var terrainByProvince = ReadTerrainPaletteSection(ref r, slots, out reason);
        if (terrainByProvince is null) return null;

        var hasSlots = r.ReadBoolean();
        var surfaceSlots = hasSlots ? ReadByteRle(ref r, cellCount) : Array.Empty<byte>();
        if (hasSlots && surfaceSlots is null) { reason = "corrupt TerrainSurfaceSlots RLE"; return null; }

        var provinceSource = r.ReadString(intern: true);
        var hydrologySource = r.ReadString(intern: true);
        var countryAnchors = ReadLabelAnchors(ref r);
        var regionAnchors = ReadLabelAnchors(ref r);
        var riverAnchors = ReadRiverAnchors(ref r);

        var topology = AssembleTopology(
            cellToProvince, provinceToState, provinceToNation, provinceCenterCell,
            neighbors, width, height, riverIntensity, lakeAccess, terrainByProvince,
            surfaceSlots, crossings, attachCrossings);

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

    private static bool ValidateCenters(int[] centers, int slots, int cellCount, out string reason)
    {
        for (var i = 1; i < slots; i++)
        {
            if ((uint)centers[i] < (uint)cellCount) continue;
            reason = $"invalid center cell for province {i}";
            return false;
        }
        reason = "";
        return true;
    }

    private static int[][] ReadNeighborGraph(ref SpanReader r, int slots, int provinceCount, out string reason)
    {
        var neighbors = new int[slots][];
        for (var i = 0; i < slots; i++)
        {
            var deg = r.ReadUInt16();
            if (deg == 0) { neighbors[i] = Array.Empty<int>(); continue; }
            var row = ReadIntArray(ref r, deg);
            for (var j = 0; j < row.Length; j++)
            {
                if (row[j] > 0 && row[j] <= provinceCount) continue;
                reason = $"invalid neighbor {row[j]}";
                return null;
            }
            neighbors[i] = row;
        }
        reason = "";
        return neighbors;
    }

    private static bool ReadHydrologySection(
        ref SpanReader r,
        int slots,
        out byte[] riverIntensity,
        out bool[] lakeAccess,
        out List<(long Key, byte Strength)> crossings,
        out string reason)
    {
        riverIntensity = null;
        lakeAccess = null;
        crossings = null;

        var riverSlice = r.ReadBytes(slots);
        riverIntensity = riverSlice.ToArray();

        var lakeByteCount = (slots + 7) >> 3;
        var lakeBits = r.ReadBytes(lakeByteCount);
        lakeAccess = new bool[slots];
        for (var i = 0; i < slots; i++)
            lakeAccess[i] = (lakeBits[i >> 3] & (1 << (i & 7))) != 0;

        var crossingCount = r.ReadInt32();
        if (crossingCount < 0 || crossingCount > MaxRiverCrossings)
        {
            reason = $"invalid river crossing count {crossingCount}";
            return false;
        }
        crossings = new List<(long Key, byte Strength)>(crossingCount);
        for (var i = 0; i < crossingCount; i++)
            crossings.Add((r.ReadInt64(), r.ReadByte()));

        reason = "";
        return true;
    }

    private static TerrainDefinition[] ReadTerrainPaletteSection(ref SpanReader r, int slots, out string reason)
    {
        var paletteCount = r.ReadUInt16();
        var paletteDefs = new TerrainDefinition[paletteCount];
        for (var i = 0; i < paletteCount; i++)
            paletteDefs[i] = TerrainCatalog.Get(r.ReadString(intern: true));

        var terrainIdx = r.ReadBytes(slots);
        var terrainByProvince = new TerrainDefinition[slots];
        for (var i = 0; i < slots; i++)
        {
            var idx = terrainIdx[i];
            if (idx < paletteDefs.Length) terrainByProvince[i] = paletteDefs[idx];
        }
        reason = "";
        return terrainByProvince;
    }

    private static ProvinceTopology AssembleTopology(
        ushort[] cellToProvince,
        ushort[] provinceToState,
        ushort[] provinceToNation,
        int[] provinceCenterCell,
        int[][] neighbors,
        int width,
        int height,
        byte[] riverIntensity,
        bool[] lakeAccess,
        TerrainDefinition[] terrainByProvince,
        byte[] surfaceSlots,
        List<(long Key, byte Strength)> crossings,
        bool attachCrossings)
    {
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
        if (attachCrossings) LandCrossings.Attach(topology);
        TerritorialTopology.ApplyProvinceSliverHealing(topology);
        return topology;
    }

    private static void WriteUShortRle(ArrayPoolBufferWriter<byte> w, ushort[] data)
    {
        const int RleEntryBytes = sizeof(ushort) * 2;
        var runsOffset = w.WrittenCount;
        WriteInt32(w, 0);
        var runCount = 0;
        ReadOnlySpan<ushort> span = data;
        var i = 0;
        while (i < span.Length)
        {
            var val = span[i];
            var slice = span.Slice(i, Math.Min(ushort.MaxValue, span.Length - i));
            var diff = slice.IndexOfAnyExcept(val);
            var run = diff < 0 ? slice.Length : diff;
            var hw = new SpanWriter(w.GetSpan(RleEntryBytes));
            hw.WriteUInt16(val);
            hw.WriteUInt16((ushort)run);
            w.Advance(RleEntryBytes);
            runCount++;
            i += run;
        }
        PatchInt32(w, runsOffset, runCount);
    }

    private static ushort[] ReadUShortRle(ref SpanReader r, int expectedLength, int maxProvinceId)
    {
        var runCount = r.ReadInt32();
        if (runCount < 0 || runCount > expectedLength) return null;
        var result = new ushort[expectedLength];
        var span = result.AsSpan();
        var cursor = 0;
        for (var i = 0; i < runCount; i++)
        {
            var val = r.ReadUInt16();
            var run = r.ReadUInt16();
            if (run == 0 || val > maxProvinceId || cursor + run > expectedLength) return null;
            if (val != 0) span.Slice(cursor, run).Fill(val);
            cursor += run;
        }
        return cursor == expectedLength ? result : null;
    }

    private static void WriteByteRle(ArrayPoolBufferWriter<byte> w, byte[] data)
    {
        var runsOffset = w.WrittenCount;
        WriteInt32(w, 0);
        var runCount = 0;
        ReadOnlySpan<byte> span = data;
        var i = 0;
        while (i < span.Length)
        {
            var val = span[i];
            var slice = span.Slice(i, Math.Min(ushort.MaxValue, span.Length - i));
            var diff = slice.IndexOfAnyExcept(val);
            var run = diff < 0 ? slice.Length : diff;
            var dst = w.GetSpan(3);
            dst[0] = val;
            BinaryPrimitives.WriteUInt16LittleEndian(dst.Slice(1), (ushort)run);
            w.Advance(3);
            runCount++;
            i += run;
        }
        PatchInt32(w, runsOffset, runCount);
    }

    private static byte[] ReadByteRle(ref SpanReader r, int expectedLength)
    {
        var runCount = r.ReadInt32();
        if (runCount < 0 || runCount > expectedLength) return null;
        var result = new byte[expectedLength];
        var span = result.AsSpan();
        var cursor = 0;
        for (var i = 0; i < runCount; i++)
        {
            var val = r.ReadByte();
            var run = r.ReadUInt16();
            if (run == 0 || cursor + run > expectedLength) return null;
            if (val != 0) span.Slice(cursor, run).Fill(val);
            cursor += run;
        }
        return cursor == expectedLength ? result : null;
    }

    private static void WriteUShortSpan(ArrayPoolBufferWriter<byte> w, ReadOnlySpan<ushort> data)
    {
        if (BitConverter.IsLittleEndian) { WriteBytes(w, MemoryMarshal.AsBytes(data)); return; }
        for (var i = 0; i < data.Length; i++) WriteUInt16(w, data[i]);
    }

    private static ushort[] ReadUShortArray(ref SpanReader r, int count)
    {
        var arr = new ushort[count];
        var raw = r.ReadBytes(count * 2);
        if (BitConverter.IsLittleEndian) raw.CopyTo(MemoryMarshal.AsBytes(arr.AsSpan()));
        else
            for (var i = 0; i < count; i++) arr[i] = BinaryPrimitives.ReadUInt16LittleEndian(raw.Slice(i * 2, 2));
        return arr;
    }

    private static void WriteIntSpan(ArrayPoolBufferWriter<byte> w, ReadOnlySpan<int> data)
    {
        if (BitConverter.IsLittleEndian) { WriteBytes(w, MemoryMarshal.AsBytes(data)); return; }
        for (var i = 0; i < data.Length; i++) WriteInt32(w, data[i]);
    }

    private static int[] ReadIntArray(ref SpanReader r, int count)
    {
        var arr = new int[count];
        var raw = r.ReadBytes(count * 4);
        if (BitConverter.IsLittleEndian) raw.CopyTo(MemoryMarshal.AsBytes(arr.AsSpan()));
        else
            for (var i = 0; i < count; i++) arr[i] = BinaryPrimitives.ReadInt32LittleEndian(raw.Slice(i * 4, 4));
        return arr;
    }

    private static void WriteLabelAnchors(ArrayPoolBufferWriter<byte> w, CachedLabelAnchor[] anchors)
    {
        const int AnchorStrideBytes = 32;
        var count = anchors?.Length ?? 0;
        WriteInt32(w, count);
        for (var i = 0; i < count; i++)
        {
            var a = anchors[i];
            var hw = new SpanWriter(w.GetSpan(AnchorStrideBytes));
            hw.WriteInt32(a.Key);
            hw.WriteSingle(a.CenterX);
            hw.WriteSingle(a.CenterY);
            hw.WriteSingle(a.AxisX);
            hw.WriteSingle(a.AxisY);
            hw.WriteSingle(a.HalfSpanPixels);
            hw.WriteSingle(a.ThicknessPixels);
            hw.WriteInt32(a.Cells);
            w.Advance(AnchorStrideBytes);
        }
    }

    private static CachedLabelAnchor[] ReadLabelAnchors(ref SpanReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxLabelAnchors) throw new InvalidDataException($"Invalid label anchor count {count}.");
        var arr = count == 0 ? Array.Empty<CachedLabelAnchor>() : new CachedLabelAnchor[count];
        for (var i = 0; i < count; i++)
            arr[i] = new CachedLabelAnchor(
                r.ReadInt32(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadInt32());
        return arr;
    }

    private static void WriteRiverAnchors(ArrayPoolBufferWriter<byte> w, CachedRiverAnchor[] anchors)
    {
        var count = anchors?.Length ?? 0;
        WriteInt32(w, count);
        for (var i = 0; i < count; i++)
        {
            var a = anchors[i];
            WriteString(w, a.Key ?? "");
            WriteString(w, a.DefaultName ?? "");
            WriteInt32(w, a.Rank);
            WriteSingle(w, a.Latitude);
            WriteSingle(w, a.Longitude);
            WriteSingle(w, a.AxisX);
            WriteSingle(w, a.AxisY);
            WriteSingle(w, a.SpanDegrees);
        }
    }

    private static CachedRiverAnchor[] ReadRiverAnchors(ref SpanReader r)
    {
        var count = r.ReadInt32();
        if (count < 0 || count > MaxRiverAnchors) throw new InvalidDataException($"Invalid river anchor count {count}.");
        var arr = count == 0 ? Array.Empty<CachedRiverAnchor>() : new CachedRiverAnchor[count];
        for (var i = 0; i < count; i++)
            arr[i] = new CachedRiverAnchor(
                r.ReadString(intern: true), r.ReadString(intern: true), r.ReadInt32(),
                r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle(), r.ReadSingle());
        return arr;
    }

    /// <summary>64-bit hardware-accelerated XxHash3 digest for fast in-memory and boot-cache integrity checks.</summary>
    public static ulong ComputeXxHash3(ReadOnlySpan<byte> bytes, long seed = 0) =>
        XxHash3.HashToUInt64(bytes, seed);

    /// <summary>
    /// Compute or retrieve a cached 64-bit XxHash3 digest for a file on disk,
    /// validating file length and last-write timestamp against the bounded LRU entry.
    /// </summary>
    internal static ulong ComputeFileXxHash3(string filePath, long seed = 0)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return 0UL;
        try
        {
            var fullPath = Path.GetFullPath(filePath);
            var info = new FileInfo(fullPath);
            if (!info.Exists) return 0UL;
            var length = info.Length;
            var ticks = info.LastWriteTimeUtc.Ticks;
            if (FileHashCache.TryGet(fullPath, out var cached) &&
                cached.Length == length &&
                cached.LastWriteTicksUtc == ticks &&
                cached.Seed == seed)
                return cached.Hash;

            using var stream = OpenReadStream(fullPath);
            var hash = RecyclableStreams.ComputeXxHash3(stream, seed);
            FileHashCache.AddOrUpdate(fullPath, new CachedFileXxHash(length, ticks, seed, hash));
            return hash;
        }
        catch
        {
            return 0UL;
        }
    }

    internal static void ClearFileHashCache() => FileHashCache.Clear();

    /// <summary>64-bit FNV-1a checksum for fast corruption detection on cache loads.</summary>
    public static ulong ComputeFnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = FnvOffsetBasis;
        for (var i = 0; i < bytes.Length; i++)
            hash = (hash ^ bytes[i]) * FnvPrime;
        return hash;
    }

    /// <summary>Hash a file on disk sequentially using a pooled scratch buffer.</summary>
    internal static ulong ComputeFileFnv1a64(string filePath)
    {
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) return 0UL;
        var hash = FnvOffsetBasis;
        var scratch = ArrayPool<byte>.Shared.Rent(IoBufferBytes);
        try
        {
            using var stream = OpenReadStream(filePath);
            while (true)
            {
                var read = stream.Read(scratch, 0, scratch.Length);
                if (read <= 0) break;
                for (var i = 0; i < read; i++)
                    hash = (hash ^ scratch[i]) * FnvPrime;
            }
            return hash == 0UL ? 1UL : hash;
        }
        catch
        {
            return 0UL;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(scratch);
        }
    }

    /// <summary>Resolve the best readable <c>hydrology_v2.bin</c> path.</summary>
    public static string ResolveReadableHydrologyCachePath(string writableCacheDirectory, string shippedFallbackPath = null) =>
        ResolveReadablePath(HydrologyFileName, writableCacheDirectory, shippedFallbackPath);

    /// <summary>Atomically write compressed pre-rasterized lake SDF and river centreline layers.</summary>
    public static bool TryWriteHydrology(
        string cacheDirectory,
        ulong regionFingerprint,
        int width,
        int height,
        HydrologyBootCacheData data,
        out string error)
    {
        if (!ValidateCacheWriteTarget(cacheDirectory, HydrologyFileName, data is null ? "invalid hydrology cache arguments" : null, out var targetPath, out error))
            return false;
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
        if (ModOverlayPolicy.IsActive) { error = "bypassed: mod overlay active"; return false; }
        if (string.IsNullOrWhiteSpace(targetPath) || data is null || width <= 0 || height <= 0)
        {
            error = "invalid hydrology cache arguments";
            return false;
        }
        var lake = data.LakeSdfR8 ?? Array.Empty<byte>();
        var river = data.RiverCentreRgba8 ?? Array.Empty<byte>();
        if (lake.Length != width * height || (river.Length != 0 && river.Length != width * height * 4))
        {
            error = "hydrology buffer length mismatch";
            return false;
        }

        try
        {
            using var compressedWriter = new ArrayPoolBufferWriter<byte>(Math.Max(IoBufferBytes, lake.Length / 8));
            using (var rawStream = compressedWriter.AsStream())
            using (var deflate = new DeflateStream(rawStream, CompressionLevel.Fastest, leaveOpen: true))
            using (var bw = new BinaryWriter(deflate, Encoding.UTF8, leaveOpen: true))
            {
                bw.Write(data.HydrologySource ?? "");
                bw.Write(lake.Length);
                bw.Write(lake);
                bw.Write(river.Length);
                if (river.Length > 0) bw.Write(river);
                bw.Flush();
            }

            var compressed = compressedWriter.WrittenSpan;
            var checksum = ComputeFnv1a64(compressed);
            Span<byte> header = stackalloc byte[HydrologyHeaderSizeBytes];
            var hw = new SpanWriter(header);
            hw.WriteUInt32(HydrologyMagic);
            hw.WriteInt32(HydrologyFormatVersion);
            hw.WriteUInt64(regionFingerprint);
            hw.WriteInt32(width);
            hw.WriteInt32(height);
            hw.WriteInt32(compressed.Length);
            hw.WriteUInt64(checksum);
            return AtomicWriteSegments(targetPath, out error, header, compressed);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Try to load and validate <c>hydrology_v2.bin</c> from writable or shipped fallback storage.</summary>
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
        if (ModOverlayPolicy.IsActive) { reason = "bypassed: mod overlay active"; return false; }
        if (TryResolveReadableWritable(writableCacheDirectory, HydrologyFileName, out var writable) &&
            TryReadHydrologyFile(writable, expectedFingerprint, expectedWidth, expectedHeight, out data, out reason))
            return true;
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
        if (!CanReadCachePath(filePath, "hydrology cache file not found", out reason)) return false;

        byte[] rentedCompressed = null;
        try
        {
            using var stream = OpenReadStream(filePath);
            if (!TryReadHydrologyHeader(stream, expectedFingerprint, expectedWidth, expectedHeight,
                    out var payloadLength, out var expectedChecksum, out reason))
                return false;

            rentedCompressed = ArrayPool<byte>.Shared.Rent(payloadLength);
            stream.ReadExactly(rentedCompressed, 0, payloadLength);
            if (ComputeFnv1a64(rentedCompressed.AsSpan(0, payloadLength)) != expectedChecksum)
            {
                reason = "hydrology checksum mismatch";
                return false;
            }

            data = DecodeHydrologyPayload(rentedCompressed, payloadLength, expectedWidth, expectedHeight, out reason);
            if (data is null) return false;
            reason = $"hit ({Path.GetFileName(filePath)}, {payloadLength / 1024:N0} KB)";
            return true;
        }
        catch (Exception ex)
        {
            data = null;
            reason = $"hydrology read error: {ex.Message}";
            return false;
        }
        finally
        {
            if (rentedCompressed is not null) ArrayPool<byte>.Shared.Return(rentedCompressed);
        }
    }

    private static bool TryReadHydrologyHeader(
        FileStream stream,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        out int payloadLength,
        out ulong expectedChecksum,
        out string reason)
    {
        payloadLength = 0;
        expectedChecksum = 0;
        if (stream.Length < HydrologyHeaderSizeBytes) { reason = "truncated hydrology header"; return false; }
        Span<byte> header = stackalloc byte[HydrologyHeaderSizeBytes];
        stream.ReadExactly(header);
        var r = new SpanReader(header);
        var magic = r.ReadUInt32();
        if (magic != HydrologyMagic) { reason = $"bad hydrology magic 0x{magic:X8}"; return false; }
        var version = r.ReadInt32();
        if (version != HydrologyFormatVersion) { reason = $"hydrology version mismatch {version} != {HydrologyFormatVersion}"; return false; }
        if (r.ReadUInt64() != expectedFingerprint)
        {
            reason = "hydrology region fingerprint mismatch";
            return false;
        }
        var width = r.ReadInt32();
        var height = r.ReadInt32();
        if (width != expectedWidth || height != expectedHeight) { reason = $"hydrology dimensions mismatch {width}x{height}"; return false; }
        payloadLength = r.ReadInt32();
        expectedChecksum = r.ReadUInt64();
        if (payloadLength <= 0 || stream.Length - HydrologyHeaderSizeBytes != payloadLength)
        {
            reason = "hydrology payload length mismatch";
            return false;
        }
        reason = "";
        return true;
    }

    private static HydrologyBootCacheData DecodeHydrologyPayload(
        byte[] compressed,
        int payloadLength,
        int expectedWidth,
        int expectedHeight,
        out string reason)
    {
        var expectedLakeLen = expectedWidth * expectedHeight;
        var expectedRiverLen = expectedWidth * expectedHeight * 4;
        using var ms = RecyclableStreams.FromBytes("MapBootCache.DecodeHydrology", compressed.AsSpan(0, payloadLength));
        using var deflate = new DeflateStream(ms, CompressionMode.Decompress);
        using var br = new BinaryReader(deflate, Encoding.UTF8);

        var source = br.ReadString();
        var lakeLen = br.ReadInt32();
        if (lakeLen != expectedLakeLen) { reason = $"invalid lake buffer length {lakeLen}"; return null; }
        var lake = new byte[lakeLen];
        br.BaseStream.ReadExactly(lake, 0, lakeLen);
        var riverLen = br.ReadInt32();
        if (riverLen != 0 && riverLen != expectedRiverLen) { reason = $"invalid river buffer length {riverLen}"; return null; }
        var river = riverLen > 0 ? new byte[riverLen] : Array.Empty<byte>();
        if (riverLen > 0) br.BaseStream.ReadExactly(river, 0, riverLen);

        reason = "";
        return new HydrologyBootCacheData
        {
            LakeSdfR8 = lake,
            RiverCentreRgba8 = river,
            HydrologySource = source
        };
    }

    /// <summary>Resolve the best readable <c>political_rasters_v1.bin</c> path.</summary>
    public static string ResolveReadablePoliticalRasterCachePath(string writableCacheDirectory, string shippedFallbackPath = null) =>
        ResolveReadablePath(PoliticalRasterFileName, writableCacheDirectory, shippedFallbackPath);

    /// <summary>Atomically write LZ4-compressed packed political &amp; plate raster buffers.</summary>
    public static bool TryWritePoliticalRasters(
        string cacheDirectory,
        ulong regionFingerprint,
        int width,
        int height,
        int politicalScale,
        PoliticalRasterBootCacheData data,
        out string error)
    {
        if (!ValidateCacheWriteTarget(cacheDirectory, PoliticalRasterFileName, data is null ? "invalid political raster cache arguments" : null, out var targetPath, out error))
            return false;
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
        if (ModOverlayPolicy.IsActive) { error = "bypassed: mod overlay active"; return false; }
        if (string.IsNullOrWhiteSpace(targetPath) || data is null ||
            !ValidatePoliticalRasterDimensions(width, height, politicalScale, out var plateCount, out var fullRgBytes))
        {
            error = "invalid political raster cache arguments";
            return false;
        }
        if (!ValidatePoliticalBuffers(data, plateCount, fullRgBytes))
        {
            error = "political raster buffer length mismatch";
            return false;
        }

        try
        {
            using var payloadWriter = new ArrayPoolBufferWriter<byte>(Math.Max(IoBufferBytes, Math.Min(4 * 1024 * 1024, fullRgBytes / 16)));
            WriteLz4UShortBlock(payloadWriter, data.PlateProvinces);
            WriteLz4UShortBlock(payloadWriter, data.PlateHolders);
            WriteLz4Block(payloadWriter, data.PlateGeography);
            WriteLz4Block(payloadWriter, data.PlateClass);
            WriteLz4Block(payloadWriter, data.RegionIds);
            WriteLz4Block(payloadWriter, data.ProvinceIds);
            WriteLz4Block(payloadWriter, data.NationIds);

            var payloadSpan = payloadWriter.WrittenSpan;
            var checksum = ComputeFnv1a64(payloadSpan);
            Span<byte> header = stackalloc byte[PoliticalRasterHeaderSizeBytes];
            var hw = new SpanWriter(header);
            hw.WriteUInt32(PoliticalRasterMagic);
            hw.WriteInt32(PoliticalRasterFormatVersion);
            hw.WriteUInt64(regionFingerprint);
            hw.WriteInt32(width);
            hw.WriteInt32(height);
            hw.WriteInt32(politicalScale);
            hw.WriteInt32(payloadSpan.Length);
            hw.WriteUInt64(checksum);
            return AtomicWriteSegments(targetPath, out error, header, payloadSpan);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    /// <summary>Try to load and validate <c>political_rasters_v1.bin</c> from writable or shipped fallback storage.</summary>
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
        if (ModOverlayPolicy.IsActive) { reason = "bypassed: mod overlay active"; return false; }
        if (TryResolveReadableWritable(writableCacheDirectory, PoliticalRasterFileName, out var writable) &&
            TryReadPoliticalRastersFile(writable, expectedFingerprint, expectedWidth, expectedHeight, expectedPoliticalScale, out data, out reason))
            return true;
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
        if (!CanReadCachePath(filePath, "political raster cache file not found", out reason)) return false;
        if (!ValidatePoliticalRasterDimensions(expectedWidth, expectedHeight, expectedPoliticalScale, out var plateCount, out var fullRgBytes))
        {
            reason = "invalid expected political raster dimensions";
            return false;
        }

        byte[] rentedPayload = null;
        try
        {
            using var stream = OpenReadStream(filePath);
            if (!TryReadPoliticalHeader(stream, expectedFingerprint, expectedWidth, expectedHeight, expectedPoliticalScale,
                    out var payloadLength, out var expectedChecksum, out reason))
                return false;

            rentedPayload = ArrayPool<byte>.Shared.Rent(payloadLength);
            stream.ReadExactly(rentedPayload, 0, payloadLength);
            var payloadSpan = rentedPayload.AsSpan(0, payloadLength);
            if (ComputeFnv1a64(payloadSpan) != expectedChecksum)
            {
                reason = "political raster checksum mismatch";
                return false;
            }

            data = DecodePoliticalRasters(payloadSpan, plateCount, fullRgBytes);
            if (data is null) { reason = "corrupt political raster LZ4 payload"; return false; }
            reason = $"hit ({Path.GetFileName(filePath)}, {payloadLength / 1024:N0} KB)";
            return true;
        }
        catch (Exception ex)
        {
            data = null;
            reason = $"political raster read error: {ex.Message}";
            return false;
        }
        finally
        {
            if (rentedPayload is not null) ArrayPool<byte>.Shared.Return(rentedPayload);
        }
    }

    private static bool ValidatePoliticalBuffers(PoliticalRasterBootCacheData data, int plateCount, int fullRgBytes) =>
        data.PlateProvinces is not null && data.PlateProvinces.Length == plateCount &&
        data.PlateHolders is not null && data.PlateHolders.Length == plateCount &&
        data.PlateGeography is not null && data.PlateGeography.Length == plateCount &&
        data.PlateClass is not null && data.PlateClass.Length == plateCount &&
        data.RegionIds is not null && data.RegionIds.Length == fullRgBytes &&
        data.ProvinceIds is not null && data.ProvinceIds.Length == fullRgBytes &&
        data.NationIds is not null && data.NationIds.Length == fullRgBytes;

    private static bool TryReadPoliticalHeader(
        FileStream stream,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        int expectedPoliticalScale,
        out int payloadLength,
        out ulong expectedChecksum,
        out string reason)
    {
        payloadLength = 0;
        expectedChecksum = 0;
        if (stream.Length < PoliticalRasterHeaderSizeBytes) { reason = "truncated political raster header"; return false; }
        Span<byte> header = stackalloc byte[PoliticalRasterHeaderSizeBytes];
        stream.ReadExactly(header);
        var r = new SpanReader(header);
        var magic = r.ReadUInt32();
        if (magic != PoliticalRasterMagic) { reason = $"bad political raster magic 0x{magic:X8}"; return false; }
        var version = r.ReadInt32();
        if (version != PoliticalRasterFormatVersion) { reason = $"political raster version mismatch {version} != {PoliticalRasterFormatVersion}"; return false; }
        if (r.ReadUInt64() != expectedFingerprint)
        {
            reason = "political raster region fingerprint mismatch";
            return false;
        }
        var width = r.ReadInt32();
        var height = r.ReadInt32();
        var scale = r.ReadInt32();
        if (width != expectedWidth || height != expectedHeight || scale != expectedPoliticalScale)
        {
            reason = $"political raster dimensions mismatch {width}x{height}/{scale}";
            return false;
        }
        payloadLength = r.ReadInt32();
        expectedChecksum = r.ReadUInt64();
        if (payloadLength <= 0 || stream.Length - PoliticalRasterHeaderSizeBytes != payloadLength)
        {
            reason = "political raster payload length mismatch";
            return false;
        }
        reason = "";
        return true;
    }

    private static PoliticalRasterBootCacheData DecodePoliticalRasters(ReadOnlySpan<byte> payloadSpan, int plateCount, int fullRgBytes)
    {
        var plateProvinces = new ushort[plateCount];
        var plateHolders = new ushort[plateCount];
        var plateGeography = new byte[plateCount];
        var plateClass = new byte[plateCount];
        var regionIds = new byte[fullRgBytes];
        var provinceIds = new byte[fullRgBytes];
        var nationIds = new byte[fullRgBytes];

        var offset = 0;
        if (!TryReadLz4UShortBlock(payloadSpan, ref offset, plateProvinces) ||
            !TryReadLz4UShortBlock(payloadSpan, ref offset, plateHolders) ||
            !TryReadLz4ByteBlock(payloadSpan, ref offset, plateGeography) ||
            !TryReadLz4ByteBlock(payloadSpan, ref offset, plateClass) ||
            !TryReadLz4ByteBlock(payloadSpan, ref offset, regionIds) ||
            !TryReadLz4ByteBlock(payloadSpan, ref offset, provinceIds) ||
            !TryReadLz4ByteBlock(payloadSpan, ref offset, nationIds) ||
            offset != payloadSpan.Length)
            return null;

        return new PoliticalRasterBootCacheData
        {
            PlateProvinces = plateProvinces,
            PlateHolders = plateHolders,
            PlateGeography = plateGeography,
            PlateClass = plateClass,
            RegionIds = regionIds,
            ProvinceIds = provinceIds,
            NationIds = nationIds
        };
    }

    /// <summary>Atomically write pre-repaired <c>world_raster_boot_v1.bin</c> (lake list, donors, LZ4 States + Control).</summary>
    internal static bool TryWriteBootRasters(
        string cacheDirectory,
        ulong contentKey,
        ulong sourcePngKey,
        ulong regionFingerprint,
        ulong appliedPartitionMask,
        int width,
        int height,
        ReadOnlySpan<CachedBootLake> lakes,
        ushort[] control,
        ushort[] states,
        out string error,
        ReadOnlySpan<CachedBootRuleDonor> ruleDonors = default)
    {
        if (!ValidateCacheWriteTarget(cacheDirectory, BootRasterFileName, null, out var targetPath, out error))
            return false;
        return TryWriteBootRastersToFile(targetPath, contentKey, sourcePngKey, regionFingerprint, appliedPartitionMask,
            width, height, lakes, control, states, out error, ruleDonors);
    }

    /// <summary>Atomically write pre-repaired <c>world_raster_boot_v1.bin</c> to an explicit file path.</summary>
    internal static bool TryWriteBootRastersToFile(
        string targetPath,
        ulong contentKey,
        ulong sourcePngKey,
        ulong regionFingerprint,
        ulong appliedPartitionMask,
        int width,
        int height,
        ReadOnlySpan<CachedBootLake> lakes,
        ushort[] control,
        ushort[] states,
        out string error,
        ReadOnlySpan<CachedBootRuleDonor> ruleDonors = default)
    {
        error = "";
        if (ModOverlayPolicy.IsActive) { error = "bypassed: mod overlay active"; return false; }
        var cellCount = (long)width * height;
        if (string.IsNullOrWhiteSpace(targetPath) || width <= 0 || height <= 0 || cellCount > int.MaxValue ||
            control is null || states is null || control.Length != cellCount || states.Length != cellCount)
        {
            error = "invalid boot raster arguments";
            return false;
        }

        try
        {
            using var lakeWriter = new ArrayPoolBufferWriter<byte>(Math.Max(MinLakeWriterBytes, lakes.Length * sizeof(ushort) * 16));
            EncodeBootMetadataSection(lakeWriter, lakes, ruleDonors);

            using var statesWriter = new ArrayPoolBufferWriter<byte>(BootRasterInitialBufferBytes);
            WriteLz4UShortBlock(statesWriter, states);
            using var controlWriter = new ArrayPoolBufferWriter<byte>(BootRasterInitialBufferBytes);
            WriteLz4UShortBlock(controlWriter, control);

            var lakeSpan = lakeWriter.WrittenSpan;
            var statesSpan = statesWriter.WrittenSpan;
            var controlSpan = controlWriter.WrittenSpan;

            Span<byte> header = stackalloc byte[BootRasterHeaderSizeBytes];
            WriteBootRasterHeader(
                header, contentKey, sourcePngKey, regionFingerprint, appliedPartitionMask,
                width, height, lakes.Length, lakeSpan, statesSpan, controlSpan);
            return AtomicWriteSegments(targetPath, out error, header, lakeSpan, statesSpan, controlSpan);
        }
        catch (Exception ex)
        {
            error = ex.Message;
            return false;
        }
    }

    private static void EncodeBootMetadataSection(
        ArrayPoolBufferWriter<byte> writer,
        ReadOnlySpan<CachedBootLake> lakes,
        ReadOnlySpan<CachedBootRuleDonor> ruleDonors)
    {
        for (var i = 0; i < lakes.Length; i++)
        {
            WriteUInt16(writer, lakes[i].MapIndex);
            WriteString(writer, lakes[i].Name ?? "");
        }
        WriteInt32(writer, ruleDonors.Length);
        for (var i = 0; i < ruleDonors.Length; i++)
        {
            WriteUInt16(writer, ruleDonors[i].RuleMapIndex);
            WriteUInt16(writer, ruleDonors[i].DonorMapIndex);
        }
    }

    private static void WriteBootRasterHeader(
        Span<byte> header,
        ulong contentKey,
        ulong sourcePngKey,
        ulong regionFingerprint,
        ulong appliedPartitionMask,
        int width,
        int height,
        int lakeCount,
        ReadOnlySpan<byte> lakeSpan,
        ReadOnlySpan<byte> statesSpan,
        ReadOnlySpan<byte> controlSpan)
    {
        var hw = new SpanWriter(header);
        hw.WriteUInt32(BootRasterMagic);
        hw.WriteInt32(BootRasterFormatVersion);
        hw.WriteUInt64(contentKey);
        hw.WriteUInt64(sourcePngKey);
        hw.WriteUInt64(regionFingerprint);
        hw.WriteInt32(width);
        hw.WriteInt32(height);
        hw.WriteInt32(lakeCount);
        hw.WriteInt32(lakeSpan.Length);
        hw.WriteInt32(statesSpan.Length);
        hw.WriteInt32(controlSpan.Length);
        hw.WriteUInt64(ComputeFnv1a64(lakeSpan));
        hw.WriteUInt64(ComputeFnv1a64(statesSpan));
        hw.WriteUInt64(ComputeFnv1a64(controlSpan));
        hw.WriteUInt64(appliedPartitionMask);
    }

    /// <summary>Read only the prebaked lake list and metadata from <c>world_raster_boot_v1.bin</c> without decompressing pixels.</summary>
    internal static bool TryReadBootLakes(
        string writableCacheDirectory,
        ulong expectedContentKey,
        ulong expectedSourcePngKey,
        int expectedWidth,
        int expectedHeight,
        out CachedBootLake[] lakes,
        out CachedBootRuleDonor[] ruleDonors,
        out ulong regionFingerprint,
        out ulong appliedPartitionMask,
        string shippedFallbackPath = null)
    {
        lakes = null;
        ruleDonors = null;
        regionFingerprint = 0;
        appliedPartitionMask = 0;
        if (ModOverlayPolicy.IsActive) return false;
        if (TryResolveReadableWritable(writableCacheDirectory, BootRasterFileName, out var writable) &&
            TryReadBootLakesFile(writable, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight,
                out lakes, out ruleDonors, out regionFingerprint, out appliedPartitionMask))
            return true;
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadBootLakesFile(shippedFallbackPath, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight,
                out lakes, out ruleDonors, out regionFingerprint, out appliedPartitionMask);
        return false;
    }

    internal static bool TryReadBootLakesFile(
        string filePath,
        ulong expectedContentKey,
        ulong expectedSourcePngKey,
        int expectedWidth,
        int expectedHeight,
        out CachedBootLake[] lakes,
        out ulong regionFingerprint,
        out ulong appliedPartitionMask) =>
        TryReadBootLakesFile(filePath, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight,
            out lakes, out _, out regionFingerprint, out appliedPartitionMask);

    internal static bool TryReadBootLakesFile(
        string filePath,
        ulong expectedContentKey,
        ulong expectedSourcePngKey,
        int expectedWidth,
        int expectedHeight,
        out CachedBootLake[] lakes,
        out CachedBootRuleDonor[] ruleDonors,
        out ulong regionFingerprint,
        out ulong appliedPartitionMask)
    {
        lakes = null;
        ruleDonors = null;
        regionFingerprint = 0;
        appliedPartitionMask = 0;
        if (!CanReadCachePath(filePath, "", out _)) return false;

        try
        {
            using var stream = OpenReadStream(filePath);
            if (!TryReadBootRasterHeader(stream, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight, out var h))
                return false;
            regionFingerprint = h.RegionFingerprint;
            appliedPartitionMask = h.AppliedPartitionMask;
            return TryReadBootMetadataSection(stream, h.LakeCount, h.LakeSectionLen, h.LakeChecksum, out lakes, out ruleDonors);
        }
        catch
        {
            lakes = null;
            ruleDonors = null;
            return false;
        }
    }

    /// <summary>Load only the requested pre-repaired grid (<c>States</c> or <c>Control</c>) from <c>world_raster_boot_v1.bin</c>.</summary>
    internal static bool TryReadBootRasterLayer(
        string writableCacheDirectory,
        ulong expectedContentKey,
        ulong expectedSourcePngKey,
        int expectedWidth,
        int expectedHeight,
        bool loadControl,
        out ushort[] raster,
        out CachedBootLake[] lakes,
        string shippedFallbackPath = null)
    {
        raster = null;
        lakes = null;
        if (ModOverlayPolicy.IsActive) return false;
        if (TryResolveReadableWritable(writableCacheDirectory, BootRasterFileName, out var writable) &&
            TryReadBootRasterLayerFile(writable, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight,
                loadControl, out raster, out lakes))
            return true;
        if (!string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath))
            return TryReadBootRasterLayerFile(shippedFallbackPath, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight,
                loadControl, out raster, out lakes);
        return false;
    }

    internal static bool TryReadBootRasterLayerFile(
        string filePath,
        ulong expectedContentKey,
        ulong expectedSourcePngKey,
        int expectedWidth,
        int expectedHeight,
        bool loadControl,
        out ushort[] raster,
        out CachedBootLake[] lakes)
    {
        raster = null;
        lakes = null;
        if (!CanReadCachePath(filePath, "", out _)) return false;

        byte[] rentedBlock = null;
        try
        {
            using var stream = OpenReadStream(filePath);
            if (!TryReadBootRasterHeader(stream, expectedContentKey, expectedSourcePngKey, expectedWidth, expectedHeight, out var h))
                return false;

            if (!TryReadBootMetadataSection(stream, h.LakeCount, h.LakeSectionLen, h.LakeChecksum, out lakes, out _))
                return false;

            if (loadControl) stream.Seek(h.StatesLen, SeekOrigin.Current);
            var blockLen = loadControl ? h.ControlLen : h.StatesLen;
            var blockChecksum = loadControl ? h.ControlChecksum : h.StatesChecksum;
            rentedBlock = ArrayPool<byte>.Shared.Rent(blockLen);
            stream.ReadExactly(rentedBlock, 0, blockLen);
            var blockSpan = rentedBlock.AsSpan(0, blockLen);
            if (ComputeFnv1a64(blockSpan) != blockChecksum) return false;

            var destination = new ushort[expectedWidth * expectedHeight];
            var offset = 0;
            if (!TryReadLz4UShortBlock(blockSpan, ref offset, destination) || offset != blockLen) return false;
            raster = destination;
            return true;
        }
        catch
        {
            raster = null;
            lakes = null;
            return false;
        }
        finally
        {
            if (rentedBlock is not null) ArrayPool<byte>.Shared.Return(rentedBlock);
        }
    }

    private readonly record struct BootRasterHeader(
        ulong RegionFingerprint,
        int LakeCount,
        int LakeSectionLen,
        int StatesLen,
        int ControlLen,
        ulong LakeChecksum,
        ulong StatesChecksum,
        ulong ControlChecksum,
        ulong AppliedPartitionMask);

    private static bool TryReadBootRasterHeader(
        FileStream stream,
        ulong expectedContentKey,
        ulong expectedSourcePngKey,
        int expectedWidth,
        int expectedHeight,
        out BootRasterHeader h)
    {
        h = default;
        if (stream.Length < BootRasterHeaderSizeBytes) return false;
        Span<byte> buf = stackalloc byte[BootRasterHeaderSizeBytes];
        stream.ReadExactly(buf);
        var r = new SpanReader(buf);
        if (r.ReadUInt32() != BootRasterMagic) return false;
        if (r.ReadInt32() != BootRasterFormatVersion) return false;
        if (r.ReadUInt64() != expectedContentKey) return false;
        var pngKey = r.ReadUInt64();
        if (expectedSourcePngKey != 0 && pngKey != expectedSourcePngKey) return false;
        var regionFp = r.ReadUInt64();
        if (r.ReadInt32() != expectedWidth || r.ReadInt32() != expectedHeight) return false;
        var lakeCount = r.ReadInt32();
        var lakeLen = r.ReadInt32();
        var statesLen = r.ReadInt32();
        var controlLen = r.ReadInt32();
        if (lakeCount < 0 || lakeCount > MaxBootLakes || lakeLen < 0 || lakeLen > MaxLakeSectionBytes ||
            statesLen <= 0 || statesLen > MaxCompressedBlockBytes ||
            controlLen <= 0 || controlLen > MaxCompressedBlockBytes)
            return false;
        var totalPayload = (long)lakeLen + statesLen + controlLen;
        if (stream.Length - BootRasterHeaderSizeBytes != totalPayload) return false;

        var lakeChecksum = r.ReadUInt64();
        var statesChecksum = r.ReadUInt64();
        var controlChecksum = r.ReadUInt64();
        var appliedPartitionMask = r.ReadUInt64();
        h = new BootRasterHeader(
            regionFp, lakeCount, lakeLen, statesLen, controlLen,
            lakeChecksum, statesChecksum, controlChecksum, appliedPartitionMask);
        return true;
    }

    private static bool TryReadBootMetadataSection(
        FileStream stream,
        int lakeCount,
        int sectionLen,
        ulong expectedChecksum,
        out CachedBootLake[] lakes,
        out CachedBootRuleDonor[] ruleDonors)
    {
        lakes = null;
        ruleDonors = null;
        if (sectionLen <= 0) return false;
        var rented = ArrayPool<byte>.Shared.Rent(sectionLen);
        try
        {
            stream.ReadExactly(rented, 0, sectionLen);
            var span = rented.AsSpan(0, sectionLen);
            if (ComputeFnv1a64(span) != expectedChecksum) return false;
            var r = new SpanReader(span);
            var readLakes = lakeCount == 0 ? Array.Empty<CachedBootLake>() : new CachedBootLake[lakeCount];
            for (var i = 0; i < lakeCount; i++)
                readLakes[i] = new CachedBootLake(r.ReadUInt16(), r.ReadString(intern: true));

            var donorCount = r.ReadInt32();
            if (donorCount < 0 || donorCount > MaxBootRuleDonors) return false;
            var readDonors = donorCount == 0 ? Array.Empty<CachedBootRuleDonor>() : new CachedBootRuleDonor[donorCount];
            for (var i = 0; i < donorCount; i++)
                readDonors[i] = new CachedBootRuleDonor(r.ReadUInt16(), r.ReadUInt16());

            if (r.Position != sectionLen) return false;
            lakes = readLakes;
            ruleDonors = readDonors;
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(rented);
        }
    }

    private static bool ValidatePoliticalRasterDimensions(int width, int height, int politicalScale, out int plateCount, out int fullRgBytes)
    {
        plateCount = 0;
        fullRgBytes = 0;
        if (width <= 0 || height <= 0 || politicalScale <= 0) return false;
        if (width % politicalScale != 0 || height % politicalScale != 0) return false;
        var fullPixels = (long)width * height;
        var platePixels = (long)(width / politicalScale) * (height / politicalScale);
        if (fullPixels * 2L > int.MaxValue || platePixels * 2L > int.MaxValue) return false;
        plateCount = (int)platePixels;
        fullRgBytes = (int)(fullPixels * 2L);
        return true;
    }

    private static void WriteLz4UShortBlock(ArrayPoolBufferWriter<byte> w, ushort[] source)
    {
        if (BitConverter.IsLittleEndian) { WriteLz4Block(w, MemoryMarshal.AsBytes(source.AsSpan())); return; }
        var bytes = ArrayPool<byte>.Shared.Rent(source.Length * 2);
        try
        {
            for (var i = 0; i < source.Length; i++)
                BinaryPrimitives.WriteUInt16LittleEndian(bytes.AsSpan(i * 2, 2), source[i]);
            WriteLz4Block(w, bytes.AsSpan(0, source.Length * 2));
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(bytes);
        }
    }

    private static void WriteLz4Block(ArrayPoolBufferWriter<byte> w, ReadOnlySpan<byte> raw)
    {
        var lenOffset = w.WrittenCount;
        w.GetSpan(4);
        w.Advance(4);
        var before = w.WrittenCount;
        LZ4Pickler.Pickle(raw, w, LZ4Level.L00_FAST);
        PatchInt32(w, lenOffset, w.WrittenCount - before);
    }

    private static bool TryReadLz4UShortBlock(ReadOnlySpan<byte> payload, ref int offset, ushort[] destination)
    {
        if (BitConverter.IsLittleEndian)
            return TryReadLz4ByteBlock(payload, ref offset, MemoryMarshal.AsBytes(destination.AsSpan()));

        var temp = ArrayPool<byte>.Shared.Rent(destination.Length * 2);
        try
        {
            if (!TryReadLz4ByteBlock(payload, ref offset, temp.AsSpan(0, destination.Length * 2))) return false;
            for (var i = 0; i < destination.Length; i++)
                destination[i] = BinaryPrimitives.ReadUInt16LittleEndian(temp.AsSpan(i * 2, 2));
            return true;
        }
        finally
        {
            ArrayPool<byte>.Shared.Return(temp);
        }
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

    private static string ResolveReadablePath(string fileName, string writableCacheDirectory, string shippedFallbackPath)
    {
        if (ModOverlayPolicy.IsActive) return null;
        if (TryResolveReadableWritable(writableCacheDirectory, fileName, out var writable)) return writable;
        return !string.IsNullOrWhiteSpace(shippedFallbackPath) && File.Exists(shippedFallbackPath) ? shippedFallbackPath : null;
    }

    private static bool TryResolveReadableWritable(string writableCacheDirectory, string fileName, out string writable)
    {
        writable = null;
        if (string.IsNullOrWhiteSpace(writableCacheDirectory)) return false;
        var candidate = Path.Combine(writableCacheDirectory, fileName);
        if (!File.Exists(candidate)) return false;
        writable = candidate;
        return true;
    }

    private static bool ValidateCacheWriteTarget(
        string cacheDirectory,
        string fileName,
        string extraError,
        out string targetPath,
        out string error)
    {
        targetPath = null;
        if (ModOverlayPolicy.IsActive) { error = "bypassed: mod overlay active"; return false; }
        if (string.IsNullOrWhiteSpace(cacheDirectory)) { error = "no cache directory"; return false; }
        if (!string.IsNullOrEmpty(extraError)) { error = extraError; return false; }
        targetPath = Path.Combine(cacheDirectory, fileName);
        error = "";
        return true;
    }

    private static bool CanReadCachePath(string filePath, string missingReason, out string reason)
    {
        if (ModOverlayPolicy.IsActive) { reason = "bypassed: mod overlay active"; return false; }
        if (string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath)) { reason = missingReason; return false; }
        reason = "";
        return true;
    }

    private static bool HasTopologyReflection() =>
        TopologyCtor is not null && RiverIntensityProp is not null && LakeAccessProp is not null &&
        TerrainByProvinceProp is not null && RiverCrossingsField is not null;

    private static FileStream OpenReadStream(string filePath) =>
        new(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, IoBufferBytes, FileOptions.SequentialScan);

    private static bool AtomicWriteSegments(
        string targetPath,
        out string error,
        ReadOnlySpan<byte> s0,
        ReadOnlySpan<byte> s1,
        ReadOnlySpan<byte> s2 = default,
        ReadOnlySpan<byte> s3 = default)
    {
        error = "";
        string tempPath = null;
        try
        {
            var dir = Path.GetDirectoryName(targetPath);
            if (!string.IsNullOrEmpty(dir)) Directory.CreateDirectory(dir);
            tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, IoBufferBytes))
            {
                if (!s0.IsEmpty) stream.Write(s0);
                if (!s1.IsEmpty) stream.Write(s1);
                if (!s2.IsEmpty) stream.Write(s2);
                if (!s3.IsEmpty) stream.Write(s3);
                stream.Flush();
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

    private static void WriteByte(ArrayPoolBufferWriter<byte> w, byte value)
    {
        w.GetSpan(1)[0] = value;
        w.Advance(1);
    }

    private static void WriteUInt16(ArrayPoolBufferWriter<byte> w, ushort value)
    {
        BinaryPrimitives.WriteUInt16LittleEndian(w.GetSpan(2), value);
        w.Advance(2);
    }

    private static void WriteInt32(ArrayPoolBufferWriter<byte> w, int value)
    {
        BinaryPrimitives.WriteInt32LittleEndian(w.GetSpan(4), value);
        w.Advance(4);
    }

    private static void WriteInt64(ArrayPoolBufferWriter<byte> w, long value)
    {
        BinaryPrimitives.WriteInt64LittleEndian(w.GetSpan(8), value);
        w.Advance(8);
    }

    private static void WriteSingle(ArrayPoolBufferWriter<byte> w, float value)
    {
        BinaryPrimitives.WriteSingleLittleEndian(w.GetSpan(4), value);
        w.Advance(4);
    }

    private static void WriteBytes(ArrayPoolBufferWriter<byte> w, ReadOnlySpan<byte> bytes)
    {
        if (bytes.IsEmpty) return;
        bytes.CopyTo(w.GetSpan(bytes.Length));
        w.Advance(bytes.Length);
    }

    private static void WriteString(ArrayPoolBufferWriter<byte> w, string value)
    {
        value ??= "";
        var byteCount = Encoding.UTF8.GetByteCount(value);
        Write7BitEncodedInt(w, byteCount);
        if (byteCount == 0) return;
        Encoding.UTF8.GetBytes(value, w.GetSpan(byteCount));
        w.Advance(byteCount);
    }

    private static void Write7BitEncodedInt(ArrayPoolBufferWriter<byte> w, int value)
    {
        var v = (uint)value;
        while (v >= 0x80u)
        {
            WriteByte(w, (byte)(v | 0x80u));
            v >>= 7;
        }
        WriteByte(w, (byte)v);
    }

    private static void PatchInt32(ArrayPoolBufferWriter<byte> w, int offset, int value)
    {
        var seg = w.DangerousGetArray();
        BinaryPrimitives.WriteInt32LittleEndian(seg.AsSpan(offset, 4), value);
    }

    private ref struct SpanWriter
    {
        private readonly Span<byte> _data;
        private int _pos;

        public SpanWriter(Span<byte> data)
        {
            _data = data;
            _pos = 0;
        }

        public void WriteUInt16(ushort value)
        {
            BinaryPrimitives.WriteUInt16LittleEndian(_data.Slice(_pos, sizeof(ushort)), value);
            _pos += sizeof(ushort);
        }

        public void WriteUInt32(uint value)
        {
            BinaryPrimitives.WriteUInt32LittleEndian(_data.Slice(_pos, sizeof(uint)), value);
            _pos += sizeof(uint);
        }

        public void WriteInt32(int value)
        {
            BinaryPrimitives.WriteInt32LittleEndian(_data.Slice(_pos, sizeof(int)), value);
            _pos += sizeof(int);
        }

        public void WriteUInt64(ulong value)
        {
            BinaryPrimitives.WriteUInt64LittleEndian(_data.Slice(_pos, sizeof(ulong)), value);
            _pos += sizeof(ulong);
        }

        public void WriteSingle(float value)
        {
            BinaryPrimitives.WriteSingleLittleEndian(_data.Slice(_pos, sizeof(float)), value);
            _pos += sizeof(float);
        }
    }

    private ref struct SpanReader
    {
        private readonly ReadOnlySpan<byte> _data;
        public int Position { get; private set; }

        public SpanReader(ReadOnlySpan<byte> data)
        {
            _data = data;
            Position = 0;
        }

        public byte ReadByte() => _data[Position++];
        public bool ReadBoolean() => _data[Position++] != 0;

        public ushort ReadUInt16()
        {
            var v = BinaryPrimitives.ReadUInt16LittleEndian(_data.Slice(Position, sizeof(ushort)));
            Position += sizeof(ushort);
            return v;
        }

        public uint ReadUInt32()
        {
            var v = BinaryPrimitives.ReadUInt32LittleEndian(_data.Slice(Position, sizeof(uint)));
            Position += sizeof(uint);
            return v;
        }

        public int ReadInt32()
        {
            var v = BinaryPrimitives.ReadInt32LittleEndian(_data.Slice(Position, sizeof(int)));
            Position += sizeof(int);
            return v;
        }

        public long ReadInt64()
        {
            var v = BinaryPrimitives.ReadInt64LittleEndian(_data.Slice(Position, sizeof(long)));
            Position += sizeof(long);
            return v;
        }

        public ulong ReadUInt64()
        {
            var v = BinaryPrimitives.ReadUInt64LittleEndian(_data.Slice(Position, sizeof(ulong)));
            Position += sizeof(ulong);
            return v;
        }

        public float ReadSingle()
        {
            var v = BinaryPrimitives.ReadSingleLittleEndian(_data.Slice(Position, sizeof(float)));
            Position += sizeof(float);
            return v;
        }

        public ReadOnlySpan<byte> ReadBytes(int count)
        {
            var slice = _data.Slice(Position, count);
            Position += count;
            return slice;
        }

        public string ReadString(bool intern = false)
        {
            var byteCount = Read7BitEncodedInt();
            if (byteCount == 0) return "";
            var slice = ReadBytes(byteCount);
            return intern ? StringPool.Shared.GetOrAdd(slice, Encoding.UTF8) : Encoding.UTF8.GetString(slice);
        }

        private int Read7BitEncodedInt()
        {
            uint result = 0;
            byte shift = 0;
            while (true)
            {
                if (shift >= 35) throw new FormatException("Malformed 7-bit integer.");
                var b = ReadByte();
                result |= (uint)(b & 0x7F) << shift;
                if ((b & 0x80) == 0) return (int)result;
                shift += 7;
            }
        }
    }
}
