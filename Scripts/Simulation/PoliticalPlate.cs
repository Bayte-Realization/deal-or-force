using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation;

/// <summary>
/// What the far plate is painted from, kept once when the map is built.
///
/// The plate is the political map at a third of the id rasters' size, mipmapped, and it is what
/// the eye is given from orbit. Each of its cells stands for a block of raster cells, and this
/// is what was kept of the block: the province that speaks for it, the country the authored
/// raster gives that province's cell, and two facts of geography that no treaty changes.
/// Nothing in here says where a border IS - that is worked out from the owners every time.
/// </summary>
public sealed class PoliticalPlateSource
{
    public int Width;
    public int Height;

    /// <summary>The province that speaks for each cell; 0 where the block has land but no province.</summary>
    public ushort[] Provinces;

    /// <summary>The country the authored raster gives each cell, <see cref="MapBorders.Sea"/> for water.</summary>
    public ushort[] Holders;

    /// <summary><see cref="PoliticalPlate.Coast"/> and <see cref="PoliticalPlate.RegionLine"/>, per cell.</summary>
    public byte[] Geography;

    private ProvinceRasterBounds _bounds;

    /// <summary>Lazy cached per-province bounding boxes for incremental dirty-rect repaints.</summary>
    public ProvinceRasterBounds Bounds => _bounds ??= ProvinceRasterBounds.Build(Provinces, Width, Height);
}

/// <summary>
/// The far political map, painted from who owns what now - the first time and every time after.
///
/// There used to be two painters. The plate a campaign opened with was shaded from the authored
/// country raster at full size and shrunk; the plate after any border had moved was shaded again
/// by a second routine, at the plate's own size, by a different rule. So the same frontier was
/// one cell wide before the first treaty of a campaign and two after it, every coast on the
/// planet lost its dark rim the day one province changed hands, and nothing repainted at all
/// when the owners came back to where the raster had them - a province returned at a peace, or a
/// second campaign started in the same session, kept the last picture.
///
/// One painter now, and the opening plate is simply its first run. A cell's holder is read by
/// <see cref="MapBorders.CurrentHolder"/>, the rule the close-up shader uses; its rank is the
/// highest line it touches (<see cref="MapBorders.Between"/>); its paint is its holder's colour
/// at that rank's shade. Run twice on the same owners it gives the same bytes, whatever was
/// painted in between, which is the whole of what "every border looks the same" means out here.
///
/// Pure arrays in and out, so it runs on a worker thread and under the test stand.
/// </summary>
public static class PoliticalPlate
{
    /// <summary>The block holds land that touches water: a shore, drawn as a country border.</summary>
    public const byte Coast = 1;

    /// <summary>The block holds land that touches another region: at least a region line.</summary>
    public const byte RegionLine = 2;

    /// <summary>The holder of every plate cell now. `into` is reused when it is the right size.</summary>
    public static ushort[] Holders(PoliticalPlateSource source, ushort[] owners, ushort[] authoredOwners,
        ushort[] into = null)
    {
        var cells = source.Width * source.Height;
        var holders = into is not null && into.Length == cells ? into : new ushort[cells];
        for (var cell = 0; cell < cells; cell++)
        {
            var authored = source.Holders[cell];
            holders[cell] = authored == MapBorders.Sea
                ? MapBorders.Sea
                : MapBorders.CurrentHolder(authored, source.Provinces[cell], owners, authoredOwners);
        }
        return holders;
    }

    /// <summary>
    /// The rank a land cell is painted at: the highest line it touches.
    ///
    /// A country border where a neighbouring land cell is held by somebody else, or where the
    /// block touches water. East and west wrap, because the world does; north and south stop.
    /// Failing that a region line, where geography says the block lies on one - which is also
    /// what an old country border becomes once one country holds both sides of it.
    /// </summary>
    public static MapBorderRank RankAt(PoliticalPlateSource source, ushort[] holders, int x, int y)
    {
        var width = source.Width;
        var cell = y * width + x;
        var holder = holders[cell];
        if (holder == MapBorders.Sea) return MapBorderRank.None;
        var geography = source.Geography[cell];
        if ((geography & Coast) != 0) return MapBorderRank.Country;
        var row = y * width;
        if (Foreign(holders, holder, row + (x + 1 == width ? 0 : x + 1)) ||
            Foreign(holders, holder, row + (x == 0 ? width - 1 : x - 1)) ||
            (y > 0 && Foreign(holders, holder, cell - width)) ||
            (y + 1 < source.Height && Foreign(holders, holder, cell + width)))
            return MapBorderRank.Country;
        return (geography & RegionLine) != 0 ? MapBorderRank.Region : MapBorderRank.None;
    }

    // Water next door is the Coast flag's business: it was read from the full raster, where a
    // shore is a cell wide, and a plate cell beside an all-water one always carries it.
    private static bool Foreign(ushort[] holders, ushort holder, int other)
    {
        var there = holders[other];
        return there != MapBorders.Sea &&
               MapBorders.Between(holder, there, 0, 0, 0, 0) == MapBorderRank.Country;
    }

    [ThreadStatic]
    private static uint[] _paletteScratch;
    [ThreadStatic]
    private static List<RasterDirtyRect> _dirtyRectScratch;

    /// <summary>
    /// Incrementally repaints ONLY the dirty rectangles around provinces whose owners changed since
    /// <paramref name="prevOwners"/> into a persistent Godot-packed RGBA8 mipmap chain, then updates
    /// only the dirty mipmap sub-rects. Bit-exact with full <see cref="Paint"/> on level 0.
    /// </summary>
    public static byte[] PaintDirtyMipmappedInto(
        PoliticalPlateSource source,
        ushort[] prevOwners,
        ushort[] owners,
        ushort[] authoredOwners,
        IReadOnlyDictionary<int, uint> colours,
        MapPlateShades shades,
        bool forceFull,
        ref byte[] mipBuffer,
        out bool usedIncremental)
    {
        var width = source.Width;
        var height = source.Height;
        var mipBytes = Kernels.NativeRasterBake.ComputeMipmapChainBytes(width, height, 4);
        var needFull = forceFull || mipBuffer is null || mipBuffer.Length != mipBytes || prevOwners is null;
        if (mipBuffer is null || mipBuffer.Length != mipBytes)
            mipBuffer = new byte[mipBytes];

        var rects = _dirtyRectScratch ??= new List<RasterDirtyRect>(16);
        rects.Clear();
        if (!needFull)
        {
            var any = source.Bounds.ComputeDirtyRects(prevOwners, owners, padCells: 1, rects, out needFull);
            if (!any && !needFull)
            {
                usedIncremental = true;
                return mipBuffer;
            }
        }

        var palette = FillPalette(colours, _paletteScratch ??= new uint[65536]);

        if (!needFull && rects.Count > 0)
        {
            for (var i = 0; i < rects.Count; i++)
            {
                var r = rects[i];
                if (!Kernels.NativeRasterBake.TryPaintPoliticalPlateRect(
                        width, height, source.Holders, source.Provinces, source.Geography,
                        owners, authoredOwners, palette,
                        shades.Country, shades.Region, shades.Inland,
                        r.X0, r.Y0, r.X1, r.Y1, mipBuffer))
                {
                    PaintManagedRectInto(source, owners, authoredOwners, palette, shades, r.X0, r.Y0, r.X1, r.Y1, mipBuffer);
                }
                if (!Kernels.NativeRasterBake.TryMipmapRgba8Rect(width, height, r.X0, r.Y0, r.X1, r.Y1, mipBuffer))
                    ProvinceRasterBounds.MipmapRgba8RectManaged(width, height, r.X0, r.Y0, r.X1, r.Y1, mipBuffer);
            }
            usedIncremental = true;
            return mipBuffer;
        }

        // Full repaint + full mipmap rebuild
        if (!Kernels.NativeRasterBake.TryPaintPoliticalPlate(
                width, height, source.Holders, source.Provinces, source.Geography,
                owners, authoredOwners, palette,
                shades.Country, shades.Region, shades.Inland, mipBuffer))
        {
            Array.Clear(mipBuffer, 0, width * height * 4);
            PaintManagedInto(source, owners, authoredOwners, palette, shades, mipBuffer);
        }
        if (!Kernels.NativeRasterBake.TryMipmapRgba8Rect(width, height, 0, 0, width, height, mipBuffer))
            ProvinceRasterBounds.MipmapRgba8RectManaged(width, height, 0, 0, width, height, mipBuffer);
        usedIncremental = false;
        return mipBuffer;
    }

    private static void PaintManagedRectInto(
        PoliticalPlateSource source, ushort[] owners, ushort[] authoredOwners,
        uint[] palette, MapPlateShades shades, int x0, int y0, int x1, int y1, byte[] pixels)
    {
        var width = source.Width;
        var height = source.Height;
        var srcHolders = source.Holders;
        var srcProvs = source.Provinces;
        var srcGeo = source.Geography;
        for (var y = y0; y < y1; y++)
        {
            var row = y * width;
            var hasUp = y > 0;
            var hasDown = y + 1 < height;
            for (var x = x0; x < x1; x++)
            {
                var cell = row + x;
                var write = cell * 4;
                var authored = srcHolders[cell];
                var holder = authored == MapBorders.Sea
                    ? MapBorders.Sea
                    : MapBorders.CurrentHolder(authored, srcProvs[cell], owners, authoredOwners);
                if (holder == MapBorders.Sea)
                {
                    pixels[write] = 0; pixels[write + 1] = 0; pixels[write + 2] = 0; pixels[write + 3] = 0;
                    continue;
                }
                var geo = srcGeo[cell];
                float shade;
                if ((geo & Coast) != 0)
                {
                    shade = shades.Country;
                }
                else
                {
                    var eastCell = row + (x + 1 == width ? 0 : x + 1);
                    var westCell = row + (x == 0 ? width - 1 : x - 1);
                    var east = CellHolder(srcHolders, srcProvs, eastCell, owners, authoredOwners);
                    var west = CellHolder(srcHolders, srcProvs, westCell, owners, authoredOwners);
                    var foreign = (east != MapBorders.Sea && east != holder) ||
                                  (west != MapBorders.Sea && west != holder) ||
                                  (hasUp && CellHolder(srcHolders, srcProvs, cell - width, owners, authoredOwners) is var n && n != MapBorders.Sea && n != holder) ||
                                  (hasDown && CellHolder(srcHolders, srcProvs, cell + width, owners, authoredOwners) is var s && s != MapBorders.Sea && s != holder);
                    shade = foreign ? shades.Country : (geo & RegionLine) != 0 ? shades.Region : shades.Inland;
                }
                var packed = palette[holder];
                pixels[write] = (byte)(((packed >> 24) & 0xFF) * shade);
                pixels[write + 1] = (byte)(((packed >> 16) & 0xFF) * shade);
                pixels[write + 2] = (byte)(((packed >> 8) & 0xFF) * shade);
                pixels[write + 3] = 255;
            }
        }
    }

    private static ushort CellHolder(ushort[] srcHolders, ushort[] srcProvs, int cell, ushort[] owners, ushort[] authoredOwners)
    {
        var a = srcHolders[cell];
        return a == MapBorders.Sea ? MapBorders.Sea : MapBorders.CurrentHolder(a, srcProvs[cell], owners, authoredOwners);
    }

    /// <summary>
    /// Paint the whole plate: RGBA, one cell after another, water left transparent for the
    /// shader's own sea. Uses the native <c>dof_kernels</c> raster painter when enabled and available.
    /// </summary>
    public static byte[] Paint(PoliticalPlateSource source, ushort[] owners, ushort[] authoredOwners,
        IReadOnlyDictionary<int, uint> colours, MapPlateShades shades)
    {
        byte[] pixels = null;
        return PaintInto(source, owners, authoredOwners, colours, shades, ref pixels);
    }

    /// <summary>
    /// Paint the whole plate into a caller-supplied reusable RGBA buffer (allocated/resized if needed)
    /// to avoid repeated large-object-heap allocations during wartime border repaints.
    /// </summary>
    public static byte[] PaintInto(PoliticalPlateSource source, ushort[] owners, ushort[] authoredOwners,
        IReadOnlyDictionary<int, uint> colours, MapPlateShades shades, ref byte[] pixels)
    {
        var width = source.Width;
        var height = source.Height;
        var needed = width * height * 4;
        if (pixels is null || pixels.Length != needed)
            pixels = new byte[needed];
        else
            Array.Clear(pixels);
        var palette = FillPalette(colours, _paletteScratch ??= new uint[65536]);

        if (Kernels.NativeRasterBake.TryPaintPoliticalPlate(
                width, height, source.Holders, source.Provinces, source.Geography,
                owners, authoredOwners, palette,
                shades.Country, shades.Region, shades.Inland, pixels))
        {
            return pixels;
        }

        PaintManagedInto(source, owners, authoredOwners, palette, shades, pixels);
        return pixels;
    }

    internal static byte[] PaintManaged(PoliticalPlateSource source, ushort[] owners, ushort[] authoredOwners,
        IReadOnlyDictionary<int, uint> colours, MapPlateShades shades)
    {
        var width = source.Width;
        var height = source.Height;
        var pixels = new byte[width * height * 4];
        var palette = FillPalette(colours, _paletteScratch ??= new uint[65536]);
        PaintManagedInto(source, owners, authoredOwners, palette, shades, pixels);
        return pixels;
    }

    private static void PaintManagedInto(PoliticalPlateSource source, ushort[] owners, ushort[] authoredOwners,
        uint[] palette, MapPlateShades shades, byte[] pixels)
    {
        var width = source.Width;
        var height = source.Height;
        var holders = Holders(source, owners, authoredOwners);
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                var holder = holders[cell];
                if (holder == MapBorders.Sea) continue;
                var shade = shades.Of(RankAt(source, holders, x, y));
                var packed = palette[holder];
                var write = cell * 4;
                pixels[write] = (byte)(((packed >> 24) & 0xFF) * shade);
                pixels[write + 1] = (byte)(((packed >> 16) & 0xFF) * shade);
                pixels[write + 2] = (byte)(((packed >> 8) & 0xFF) * shade);
                pixels[write + 3] = 255;
            }
        }
    }

    /// <summary>
    /// The provinces the plate on screen has not caught up with, in the colour it will give them.
    ///
    /// A plate is repainted on a worker and no more often than every couple of seconds, so for a
    /// moment after a treaty the far map still shows the old owner. This is the stopgap for that
    /// moment and for nothing else: a 256x256 table, one texel a province, opaque where the owner
    /// today is not the owner the plate was painted with (`shownOwners`), holding the new owner's
    /// colour at the plate's inland shade - byte for byte what the repainted plate will show
    /// there. The shader lays it over the plate sample alone. When the plate is swapped in the
    /// table is packed again and comes out empty, and from then on the land a peace moved is
    /// drawn by exactly the code that draws land nobody ever fought over.
    ///
    /// Returns whether any province is waiting.
    /// </summary>
    public static bool PackCatchUp(int provinceCount, ushort[] owners, ushort[] shownOwners,
        IReadOnlyDictionary<int, uint> colours, float inlandShade, byte[] pixels)
    {
        Array.Clear(pixels, 0, pixels.Length);
        if (owners is null || shownOwners is null) return false;
        var count = Math.Min(provinceCount, Math.Min(owners.Length, shownOwners.Length) - 1);
        count = Math.Min(count, pixels.Length / 4 - 1);
        var waiting = false;
        for (var province = 1; province <= count; province++)
        {
            var owner = owners[province];
            if (owner == shownOwners[province]) continue;
            var packed = owner != 0 && colours is not null && colours.TryGetValue(owner, out var colour) && colour != 0
                ? colour
                : MapBorders.UnownedColour;
            var write = province * 4;
            pixels[write] = (byte)(((packed >> 24) & 0xFF) * inlandShade);
            pixels[write + 1] = (byte)(((packed >> 16) & 0xFF) * inlandShade);
            pixels[write + 2] = (byte)(((packed >> 8) & 0xFF) * inlandShade);
            pixels[write + 3] = 255;
            waiting = true;
        }
        return waiting;
    }

    // One flat table rather than a dictionary probe per cell: the plate is 1.8 million cells.
    // A country the palette has no colour for is painted the grey of unowned land, as it always was.
    private static uint[] Palette(IReadOnlyDictionary<int, uint> colours) =>
        FillPalette(colours, new uint[65536]);

    private static uint[] FillPalette(IReadOnlyDictionary<int, uint> colours, uint[] palette)
    {
        Array.Fill(palette, MapBorders.UnownedColour);
        if (colours is not null)
            foreach (var (mapIndex, packed) in colours)
                if (mapIndex > 0 && mapIndex < MapBorders.Unowned && packed != 0) palette[mapIndex] = packed;
        return palette;
    }
}
