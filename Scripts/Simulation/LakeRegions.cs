using System;
using System.Buffers;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text.Json;

namespace DealOrForce.Simulation;

/// <summary>
/// Lakes as places on the map rather than paint on top of one.
///
/// A lake used to be a shape drawn over the land it covered. The cells underneath still
/// belonged to whichever land region held the shore, so a province could span half a lake
/// and an army could march straight across Lake Michigan without anything in the game
/// noticing - which is not a difficulty, it is simply wrong.
///
/// A lake big enough to matter is a region of its own now, and no army can set foot in it,
/// because the ground it would be standing on is water.
///
/// And it belongs to nobody (item 39 on the list). It used to go to whichever country held
/// the longest stretch of its shore and was painted in that country's colour - so Lake
/// Victoria was Ugandan ground, the Caspian was a Kazakh province and Baikal read as a
/// patch of Russia. Nobody lives on a lake, nothing is built on one and no army stands on
/// one, so owning it gave nothing and cost the map its water. A lake is the first region in
/// the game with no owner: it is drawn as water, it has no border of its own, and a country
/// whose shore it is keeps the shore.
///
/// Which lakes count is data. `map_data/lakes/` says how large a lake has to be before it
/// becomes a region and what to call the ones worth naming, and the shapes themselves come
/// from the same vector source the map is drawn from - so a mod that adds a lake to the
/// hydrology gets a lake region for free, and a mod that thinks the threshold is wrong
/// changes one number.
/// </summary>
public static class LakeRegions
{
    /// <summary>How much water it takes to be worth a region of its own, in map cells.</summary>
    public static int MinimumCells { get; private set; } = 40;

    /// <summary>
    /// How wide the water has to be before marching round it stops being an afternoon.
    ///
    /// Area alone is the wrong test. A reservoir on a river is long and thin - the Kakhovka
    /// and Kremenchuk reservoirs cover as many cells as a middling lake, but they are a few
    /// kilometres across, and every army that has ever fought on the Dnipro crossed them.
    /// Made impassable they do not read as water, they read as a wall down the middle of
    /// Ukraine, and the cells behind them are stranded by the resolution of the map rather
    /// than by anything on the ground.
    ///
    /// Depth is measured from the shore inwards, so this is the half-width: three cells is
    /// water that stays water for forty kilometres or so, which is Ladoga and Baikal and
    /// Superior, and is not the Dnipro.
    /// </summary>
    public static int MinimumWidthCells { get; private set; } = 3;

    /// <summary>Where lake regions start numbering, clear of the shipped table and its corrections.</summary>
    public static int FirstMapIndex { get; private set; } = 2000;

    private static readonly HashSet<int> LakeStateIndices = new();

    /// <summary>How many lake regions the last pass created, for the scenario import's arithmetic.</summary>
    public static int CreatedCount { get; private set; }

    /// <summary>Whether a region is water. The province layer asks this to mark itself.</summary>
    public static bool IsLakeRegion(int stateMapIndex) => LakeStateIndices.Contains(stateMapIndex);

    /// <summary>
    /// Whether a region is water, asked of the region itself.
    ///
    /// The index list is rebuilt by the pass that draws the lakes, and a caller that runs
    /// before that pass - or after a save is loaded, when nothing has drawn anything - would
    /// be told a lake is dry land. The kind is written on the region and survives both, so
    /// it is the answer that holds; the index list only has to agree with it.
    /// </summary>
    public static bool IsWaterState(ScenarioState state) => state is not null &&
        (string.Equals(state.Kind, "Lake", StringComparison.OrdinalIgnoreCase) || IsLakeRegion(state.MapIndex));

    internal static void RestoreBootLakes(ReadOnlySpan<CachedBootLake> lakes)
    {
        LakeStateIndices.Clear();
        for (var i = 0; i < lakes.Length; i++)
            if (lakes[i].MapIndex > 0) LakeStateIndices.Add(lakes[i].MapIndex);
        CreatedCount = LakeStateIndices.Count;
    }

    public static void ResetCatalog()
    {
        MinimumCells = 40;
        MinimumWidthCells = 3;
        FirstMapIndex = 2000;
        foreach (var document in GameDataLoader.DataDocuments("map_data", "lakes"))
            using (document)
                Merge(document.RootElement);
    }

    public static void Merge(JsonElement root, List<string> errors = null)
    {
        if (root.ValueKind != JsonValueKind.Object) return;
        if (root.TryGetProperty("minimum_cells", out var minimum) &&
            minimum.TryGetInt32(out var cells) && cells > 0) MinimumCells = cells;
        if (root.TryGetProperty("minimum_width_cells", out var thickness) &&
            thickness.TryGetInt32(out var across) && across > 0) MinimumWidthCells = across;
        if (root.TryGetProperty("first_map_index", out var first) &&
            first.TryGetInt32(out var index) && index > 0) FirstMapIndex = index;
    }

    public const string BootCacheFileName = "lakes_boot_v1.bin";
    public const uint BootCacheMagic = 0x434C4D44u; // "DMLC"
    public const int BootCacheFormatVersion = 1;
    private const int BootCacheHeaderSizeBytes = 44;
    private const int MaxBootCachePayloadBytes = 64 * 1024 * 1024;
    private const int MinAutoPersistWidth = 1024;
    private const int MinAutoPersistHeight = 512;

    private sealed record CachedLakeBody(int[] Cells, int[] BoundaryNeighbors, string Name);

    private static readonly object BootCacheGate = new();
    private static bool[] _water;
    private static ulong _waterLakes;
    private static int _waterWidth, _waterHeight;
    private static CachedLakeBody[] _cachedBodies;
    private static (ulong Fingerprint, int Width, int Height, int MinCells, int MinWidth) _cachedBodiesKey;

    public static void ReleaseWaterCache()
    {
        lock (BootCacheGate)
        {
            _water = null;
            _cachedBodies = null;
        }
    }

    /// <summary>
    /// Which cells are lake water, drawn from the hydrology once and kept until the lakes
    /// change: a campaign is built more than once a session, and drawing every lake of
    /// the world onto a sixteen-million-cell raster each time was most of what this pass cost.
    /// Null when there is no hydrology to draw.
    /// </summary>
    private static bool[] LakeWater(int width, int height)
    {
        var lakes = HydrologyCatalog.LakeFingerprint;
        if (_water is not null && _waterLakes == lakes && _waterWidth == width && _waterHeight == height)
            return _water;
        HydrologyRaster hydrology;
        try { hydrology = HydrologyCatalog.BuildVectorRaster(width, height); }
        catch (Exception) { return null; }
        if (hydrology.Pixels.Length != width * height * 2) return null;
        var water = new bool[width * height];
        var pixels = hydrology.Pixels;
        for (var cell = 0; cell < water.Length; cell++) water[cell] = pixels[cell * 2] > 0;
        (_water, _waterLakes, _waterWidth, _waterHeight) = (water, lakes, width, height);
        _cachedBodies = null;
        return water;
    }

    private static ulong ResolveBootCacheFingerprint()
    {
        var lakesPath = GameDataLoader.FindDataFile("map_data", "hydrography", "lakes.json");
        if (!string.IsNullOrWhiteSpace(lakesPath) && File.Exists(lakesPath))
        {
            try
            {
                const ulong fnvOffset = 14695981039346656037UL;
                const ulong fnvPrime = 1099511628211UL;
                var hash = fnvOffset;
                using var fs = new FileStream(lakesPath, FileMode.Open, FileAccess.Read, FileShare.Read, 16384);
                hash ^= (ulong)fs.Length;
                hash *= fnvPrime;
                Span<byte> buf = stackalloc byte[4096];
                while (true)
                {
                    var read = fs.Read(buf);
                    if (read <= 0) break;
                    for (var i = 0; i < read; i++)
                    {
                        hash ^= buf[i];
                        hash *= fnvPrime;
                    }
                }
                return hash;
            }
            catch (Exception)
            {
                // Fall back to parsed hydrology fingerprint if direct file read fails.
            }
        }
        return HydrologyCatalog.LakeFingerprint;
    }

    private static CachedLakeBody[] GetOrBuildBodies(int width, int height)
    {
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue) return null;
        var modActive = ModOverlayPolicy.IsActive;
        var fingerprint = modActive ? HydrologyCatalog.LakeFingerprint : ResolveBootCacheFingerprint();
        var minCells = MinimumCells;
        var minWidth = MinimumWidthCells;
        var key = (fingerprint, width, height, minCells, minWidth);

        lock (BootCacheGate)
        {
            if (_cachedBodies is not null && _cachedBodiesKey == key) return _cachedBodies;

            var writableDir = modActive ? null : DefaultWritableCacheDirectory();
            if (!modActive)
            {
                if (!string.IsNullOrWhiteSpace(writableDir))
                {
                    var writablePath = Path.Combine(writableDir, BootCacheFileName);
                    if (TryReadBootCacheFile(writablePath, fingerprint, width, height, minCells, minWidth, out var fromWritable))
                    {
                        _cachedBodies = fromWritable;
                        _cachedBodiesKey = key;
                        return _cachedBodies;
                    }
                }

                var shippedPath = GameDataLoader.FindDataFile("cache", BootCacheFileName);
                if (!string.IsNullOrWhiteSpace(shippedPath) &&
                    TryReadBootCacheFile(shippedPath, fingerprint, width, height, minCells, minWidth, out var fromShipped))
                {
                    _cachedBodies = fromShipped;
                    _cachedBodiesKey = key;
                    return _cachedBodies;
                }
            }

            var built = BuildBodiesUncached(width, height);
            if (built is null) return null;

            _cachedBodies = built;
            _cachedBodiesKey = key;

            if (!modActive &&
                width >= MinAutoPersistWidth && height >= MinAutoPersistHeight &&
                !string.IsNullOrWhiteSpace(writableDir))
            {
                TryWriteBootCacheToDirectory(writableDir, fingerprint, width, height, minCells, minWidth, built, out _);
            }

            return _cachedBodies;
        }
    }

    internal static bool TryReadPrebakedBootLakeNames(int width, int height, out string[] names)
    {
        names = null;
        if (ModOverlayPolicy.IsActive || width <= 0 || height <= 0 || (long)width * height > int.MaxValue)
            return false;

        var key = (ResolveBootCacheFingerprint(), width, height, MinimumCells, MinimumWidthCells);
        lock (BootCacheGate)
        {
            var bodies = TryResolveBootBodiesWithoutBuildLocked(key);
            if (bodies is null || bodies.Length == 0) return false;
            names = new string[bodies.Length];
            for (var i = 0; i < bodies.Length; i++) names[i] = bodies[i].Name ?? "Lake";
            return true;
        }
    }

    private static CachedLakeBody[] TryResolveBootBodiesWithoutBuildLocked(
        (ulong Fingerprint, int Width, int Height, int MinCells, int MinWidth) key)
    {
        if (_cachedBodies is not null && _cachedBodiesKey == key) return _cachedBodies;
        var writableDir = DefaultWritableCacheDirectory();
        if (!string.IsNullOrWhiteSpace(writableDir) &&
            TryReadBootCacheFile(Path.Combine(writableDir, BootCacheFileName), key.Fingerprint, key.Width, key.Height,
                key.MinCells, key.MinWidth, out var fromWritable))
        {
            _cachedBodies = fromWritable;
            _cachedBodiesKey = key;
            return fromWritable;
        }
        var shippedPath = GameDataLoader.FindDataFile("cache", BootCacheFileName);
        if (!string.IsNullOrWhiteSpace(shippedPath) &&
            TryReadBootCacheFile(shippedPath, key.Fingerprint, key.Width, key.Height,
                key.MinCells, key.MinWidth, out var fromShipped))
        {
            _cachedBodies = fromShipped;
            _cachedBodiesKey = key;
            return fromShipped;
        }
        return null;
    }

    /// <summary>
    /// Pre-bake or persist <c>lakes_boot_v1.bin</c> under <paramref name="cacheDirectory"/>.
    /// Bypassed cleanly whenever <see cref="ModOverlayPolicy.IsActive"/> is true.
    /// </summary>
    public static bool TryWriteBootCache(string cacheDirectory, int width, int height, out string error)
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
        if (width <= 0 || height <= 0 || (long)width * height > int.MaxValue)
        {
            error = "invalid map dimensions";
            return false;
        }

        lock (BootCacheGate)
        {
            var fingerprint = ResolveBootCacheFingerprint();
            var minCells = MinimumCells;
            var minWidth = MinimumWidthCells;
            var key = (fingerprint, width, height, minCells, minWidth);
            var bodies = _cachedBodies is not null && _cachedBodiesKey == key
                ? _cachedBodies
                : BuildBodiesUncached(width, height);
            if (bodies is null)
            {
                error = "hydrology raster unavailable";
                return false;
            }
            return TryWriteBootCacheToDirectory(cacheDirectory, fingerprint, width, height, minCells, minWidth, bodies, out error);
        }
    }

    private static string DefaultWritableCacheDirectory()
    {
        try
        {
            var args = Environment.GetCommandLineArgs();
            var isVerifyBuild = false;
            for (var i = 0; i < args.Length; i++)
            {
                if (string.Equals(args[i], "--verify-build", StringComparison.Ordinal))
                {
                    isVerifyBuild = true;
                    break;
                }
            }
            if (isVerifyBuild)
            {
                var verifyDocs = Environment.GetEnvironmentVariable("DOF_VERIFICATION_DOCUMENTS");
                if (!string.IsNullOrWhiteSpace(verifyDocs) && Path.IsPathRooted(verifyDocs))
                {
                    var verifyFolders = GameFolders.For(verifyDocs);
                    if (verifyFolders is not null) return Path.Combine(verifyFolders.Root, "cache");
                }
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

    private static bool TryReadBootCacheFile(
        string filePath,
        ulong expectedFingerprint,
        int expectedWidth,
        int expectedHeight,
        int expectedMinCells,
        int expectedMinWidth,
        out CachedLakeBody[] bodies)
    {
        bodies = null;
        if (ModOverlayPolicy.IsActive || string.IsNullOrWhiteSpace(filePath) || !File.Exists(filePath))
            return false;

        try
        {
            using var stream = new FileStream(filePath, FileMode.Open, FileAccess.Read, FileShare.Read, 65536);
            if (stream.Length < BootCacheHeaderSizeBytes) return false;
            using var reader = new BinaryReader(stream);
            if (reader.ReadUInt32() != BootCacheMagic) return false;
            if (reader.ReadInt32() != BootCacheFormatVersion) return false;
            if (reader.ReadUInt64() != expectedFingerprint) return false;
            var width = reader.ReadInt32();
            var height = reader.ReadInt32();
            if (width != expectedWidth || height != expectedHeight) return false;
            if (reader.ReadInt32() != expectedMinCells) return false;
            if (reader.ReadInt32() != expectedMinWidth) return false;
            var payloadLength = reader.ReadInt32();
            var expectedChecksum = reader.ReadUInt64();
            if (payloadLength <= 0 || payloadLength > MaxBootCachePayloadBytes ||
                stream.Length - BootCacheHeaderSizeBytes != payloadLength)
                return false;

            var payload = reader.ReadBytes(payloadLength);
            if (payload.Length != payloadLength) return false;
            if (ComputeFnv1a64(payload) != expectedChecksum) return false;

            bodies = DecodeBootCachePayload(payload, width * height);
            return bodies is not null;
        }
        catch (Exception)
        {
            bodies = null;
            return false;
        }
    }

    private static bool TryWriteBootCacheToDirectory(
        string cacheDirectory,
        ulong fingerprint,
        int width,
        int height,
        int minCells,
        int minWidth,
        CachedLakeBody[] bodies,
        out string error)
    {
        error = "";
        if (ModOverlayPolicy.IsActive)
        {
            error = "bypassed: mod overlay active";
            return false;
        }
        if (string.IsNullOrWhiteSpace(cacheDirectory) || bodies is null)
        {
            error = "invalid lake boot cache write arguments";
            return false;
        }

        var targetPath = Path.Combine(cacheDirectory, BootCacheFileName);
        string tempPath = null;
        try
        {
            Directory.CreateDirectory(cacheDirectory);
            var payload = EncodeBootCachePayload(bodies);
            var checksum = ComputeFnv1a64(payload);

            tempPath = $"{targetPath}.tmp.{Guid.NewGuid():N}";
            using (var stream = new FileStream(tempPath, FileMode.Create, FileAccess.Write, FileShare.None, 65536))
            using (var writer = new BinaryWriter(stream))
            {
                writer.Write(BootCacheMagic);
                writer.Write(BootCacheFormatVersion);
                writer.Write(fingerprint);
                writer.Write(width);
                writer.Write(height);
                writer.Write(minCells);
                writer.Write(minWidth);
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

    private static byte[] EncodeBootCachePayload(CachedLakeBody[] bodies)
    {
        using var compressed = new MemoryStream();
        using (var brotli = new BrotliStream(compressed, CompressionLevel.Fastest, leaveOpen: true))
        using (var writer = new BinaryWriter(brotli))
        {
            writer.Write(bodies.Length);
            for (var i = 0; i < bodies.Length; i++)
            {
                var body = bodies[i];
                writer.Write(body.Name ?? "Lake");
                WriteIntArray(writer, body.Cells);
                WriteIntArray(writer, body.BoundaryNeighbors);
            }
            writer.Flush();
        }
        return compressed.ToArray();
    }

    private static CachedLakeBody[] DecodeBootCachePayload(byte[] payload, int cellCount)
    {
        using var input = new MemoryStream(payload, writable: false);
        using var brotli = new BrotliStream(input, CompressionMode.Decompress);
        using var reader = new BinaryReader(brotli);
        var count = reader.ReadInt32();
        if (count < 0 || count > cellCount) return null;
        var bodies = new CachedLakeBody[count];
        for (var i = 0; i < count; i++)
        {
            var name = reader.ReadString();
            if (!TryReadIntArray(reader, cellCount, out var cells)) return null;
            if (!TryReadIntArray(reader, cellCount, out var boundary)) return null;
            bodies[i] = new CachedLakeBody(cells, boundary, string.IsNullOrWhiteSpace(name) ? "Lake" : name);
        }
        return bodies;
    }

    private static void WriteIntArray(BinaryWriter writer, int[] values)
    {
        var len = values?.Length ?? 0;
        writer.Write(len);
        if (len == 0) return;
        if (BitConverter.IsLittleEndian)
        {
            writer.Write(MemoryMarshal.AsBytes(values.AsSpan()));
            return;
        }
        for (var i = 0; i < len; i++) writer.Write(values[i]);
    }

    private static bool TryReadIntArray(BinaryReader reader, int cellCount, out int[] values)
    {
        values = Array.Empty<int>();
        var len = reader.ReadInt32();
        if (len < 0 || len > cellCount) return false;
        if (len == 0) return true;
        var result = new int[len];
        if (BitConverter.IsLittleEndian)
        {
            var bytes = MemoryMarshal.AsBytes(result.AsSpan());
            var read = 0;
            while (read < bytes.Length)
            {
                var n = reader.Read(bytes.Slice(read));
                if (n <= 0) return false;
                read += n;
            }
        }
        else
        {
            for (var i = 0; i < len; i++) result[i] = reader.ReadInt32();
        }
        for (var i = 0; i < len; i++)
        {
            if ((uint)result[i] >= (uint)cellCount) return false;
        }
        values = result;
        return true;
    }

    private static ulong ComputeFnv1a64(ReadOnlySpan<byte> bytes)
    {
        var hash = 14695981039346656037UL;
        for (var i = 0; i < bytes.Length; i++)
        {
            hash ^= bytes[i];
            hash *= 1099511628211UL;
        }
        return hash;
    }

    private static CachedLakeBody[] BuildBodiesUncached(int width, int height)
    {
        var water = LakeWater(width, height);
        if (water is null) return null;

        var cellCount = width * height;
        var visited = ArrayPool<bool>.Shared.Rent(cellCount);
        Array.Clear(visited, 0, cellCount);
        var component = new List<int>();
        var queue = new Queue<int>();
        var boundary = new List<int>();
        var boundarySeen = new HashSet<int>();
        var widthInside = new HashSet<int>();
        var widthDepth = new Dictionary<int, int>();
        var widthWave = new Queue<int>();
        var bodies = new List<CachedLakeBody>();
        try
        {
            var words = MemoryMarshal.Cast<bool, ulong>(water.AsSpan());
            var wordCount = words.Length;
            for (var wi = 0; wi < wordCount; wi++)
            {
                if (words[wi] == 0) continue;
                var baseCell = wi << 3;
                for (var bit = 0; bit < 8; bit++)
                {
                    var seed = baseCell + bit;
                    if (visited[seed] || !water[seed]) continue;
                    ProcessWaterSeed(seed, water, visited, width, height, component, queue, boundary, boundarySeen,
                        widthInside, widthDepth, widthWave, bodies);
                }
            }
            for (var seed = wordCount << 3; seed < cellCount; seed++)
            {
                if (visited[seed] || !water[seed]) continue;
                ProcessWaterSeed(seed, water, visited, width, height, component, queue, boundary, boundarySeen,
                    widthInside, widthDepth, widthWave, bodies);
            }
        }
        finally
        {
            ArrayPool<bool>.Shared.Return(visited);
        }

        return bodies.ToArray();
    }

    private static void ProcessWaterSeed(int seed, bool[] water, bool[] visited, int width, int height,
        List<int> component, Queue<int> queue, List<int> boundary, HashSet<int> boundarySeen,
        HashSet<int> widthInside, Dictionary<int, int> widthDepth, Queue<int> widthWave,
        List<CachedLakeBody> bodies)
    {
        component.Clear();
        queue.Clear();
        boundary.Clear();
        boundarySeen.Clear();
        visited[seed] = true;
        queue.Enqueue(seed);
        while (queue.Count > 0)
        {
            var cell = queue.Dequeue();
            component.Add(cell);
            var y = cell / width;
            var x = cell % width;
            for (var side = 0; side < 4; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (ny < 0 || ny >= height) continue;
                nx = (nx + width) % width;
                var neighbour = ny * width + nx;
                if (water[neighbour])
                {
                    if (!visited[neighbour])
                    {
                        visited[neighbour] = true;
                        queue.Enqueue(neighbour);
                    }
                    continue;
                }
                if (boundarySeen.Add(neighbour)) boundary.Add(neighbour);
            }
        }
        if (component.Count < MinimumCells || boundary.Count == 0) return;
        if (WidthOf(component, width, height, widthInside, widthDepth, widthWave) < MinimumWidthCells) return;
        bodies.Add(new CachedLakeBody(component.ToArray(), boundary.ToArray(), NameFor(component, width, height)));
    }

    /// <summary>
    /// Turn every body of water above the threshold into its own region.
    ///
    /// The lake shapes come from the vector hydrology rather than from the raster the
    /// compiler writes, because the compiler writes that raster in the same run: reading
    /// it back would be the map eating its own output, and the game and the compiler would
    /// end up drawing different lakes.
    ///
    /// Idempotent by construction. Which cells are water is decided by the hydrology, not
    /// by what the state map currently says, so a second pass finds the same bodies, gives
    /// them the same names in the same order and reaches the same answer.
    /// </summary>
    public static int Apply(Campaign campaign, ushort[] control, ushort[] states, int width, int height)
    {
        CreatedCount = 0;
        LakeStateIndices.Clear();
        if (campaign is null || control is null || states is null) return 0;

        var bodies = GetOrBuildBodies(width, height);
        if (bodies is null || bodies.Length == 0) return 0;

        var statesByMap = new Dictionary<int, ScenarioState>(campaign.States.Count);
        for (var i = 0; i < campaign.States.Count; i++)
        {
            var existing = campaign.States[i];
            statesByMap.TryAdd(existing.MapIndex, existing);
        }

        var next = FirstMapIndex;
        for (var bi = 0; bi < bodies.Length; bi++)
        {
            var body = bodies[bi];
            var boundary = body.BoundaryNeighbors;
            var hasShore = false;
            for (var i = 0; i < boundary.Length; i++)
            {
                var neighbour = boundary[i];
                if (control[neighbour] != 0 && !IsLakeRegion(states[neighbour]))
                {
                    hasShore = true;
                    break;
                }
            }
            if (!hasShore) continue;

            var mapIndex = next++;
            var name = body.Name;
            if (!statesByMap.TryGetValue(mapIndex, out var state))
            {
                state = new ScenarioState
                {
                    MapIndex = mapIndex,
                    NationMapIndex = 0,
                    NationId = "",
                    Id = $"lake_{mapIndex}",
                    Name = name,
                    Iso = $"LAKE-{mapIndex}",
                    Kind = "Lake"
                };
                campaign.States.Add(state);
                statesByMap[mapIndex] = state;
                CreatedCount++;
            }
            else
            {
                state.Name = name;
                state.NationMapIndex = 0;
                state.NationId = "";
            }

            LakeStateIndices.Add(mapIndex);
            var cells = body.Cells;
            for (var i = 0; i < cells.Length; i++)
            {
                var cell = cells[i];
                states[cell] = (ushort)mapIndex;
                control[cell] = 0;
            }
        }

        return LakeStateIndices.Count;
    }

    /// <summary>
    /// How far it is from the shore to the middle of this water, in cells.
    ///
    /// A wave of cells started at every edge of the body and run inwards; the last cell it
    /// reaches is the furthest point from any shore, and that distance is the half-width.
    /// It is the honest measure for a shape that bends, which a bounding box is not: a
    /// reservoir that winds for three hundred kilometres has a wide box and no width at all.
    /// </summary>
    private static int WidthOf(List<int> component, int width, int height,
        HashSet<int> inside, Dictionary<int, int> depth, Queue<int> wave)
    {
        inside.Clear();
        depth.Clear();
        wave.Clear();
        for (var i = 0; i < component.Count; i++) inside.Add(component[i]);
        for (var i = 0; i < component.Count; i++)
        {
            var cell = component[i];
            var y = cell / width;
            var x = cell % width;
            var onTheEdge = false;
            for (var side = 0; side < 4 && !onTheEdge; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                // The top and bottom of the map are as good as a shore: nothing is beyond them.
                if (ny < 0 || ny >= height) { onTheEdge = true; break; }
                nx = (nx + width) % width;
                if (!inside.Contains(ny * width + nx)) onTheEdge = true;
            }
            if (!onTheEdge) continue;
            depth[cell] = 1;
            wave.Enqueue(cell);
        }

        var deepest = 0;
        while (wave.Count > 0)
        {
            var cell = wave.Dequeue();
            var here = depth[cell];
            if (here > deepest) deepest = here;
            var y = cell / width;
            var x = cell % width;
            for (var side = 0; side < 4; side++)
            {
                var nx = x + (side == 0 ? -1 : side == 1 ? 1 : 0);
                var ny = y + (side == 2 ? -1 : side == 3 ? 1 : 0);
                if (ny < 0 || ny >= height) continue;
                nx = (nx + width) % width;
                var next = ny * width + nx;
                if (!inside.Contains(next) || depth.ContainsKey(next)) continue;
                depth[next] = here + 1;
                wave.Enqueue(next);
            }
        }
        return deepest;
    }

    /// <summary>The nearest named lake in the hydrology, so the region is called something.</summary>
    private static string NameFor(List<int> component, int width, int height)
    {
        double sumX = 0, sumY = 0;
        foreach (var cell in component)
        {
            sumX += cell % width;
            sumY += cell / width;
        }
        var longitude = sumX / component.Count / width * 360.0 - 180.0;
        var latitude = 90.0 - sumY / component.Count / height * 180.0;

        var best = "";
        var bestDistance = double.MaxValue;
        foreach (var lake in HydrologyCatalog.LakeFeatures)
        {
            if (lake.Rings.Length == 0 || lake.Rings[0].Length == 0) continue;
            double ringX = 0, ringY = 0;
            foreach (var point in lake.Rings[0])
            {
                ringX += point.Longitude;
                ringY += point.Latitude;
            }
            ringX /= lake.Rings[0].Length;
            ringY /= lake.Rings[0].Length;
            var distance = (ringX - longitude) * (ringX - longitude) + (ringY - latitude) * (ringY - latitude);
            if (distance >= bestDistance) continue;
            bestDistance = distance;
            best = lake.Name;
        }
        // Two degrees is close enough to be the same water; further away it is some other
        // lake that happens to be nearest, and an unnamed lake is better than a wrong name.
        return bestDistance <= 4.0 && !string.IsNullOrWhiteSpace(best) ? best : "Lake";
    }
}
