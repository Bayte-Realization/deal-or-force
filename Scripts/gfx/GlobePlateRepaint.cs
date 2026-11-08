using System;
using System.Threading.Tasks;
using Godot;

namespace DealOrForce;

/// <summary>
/// The far-away political map follows the borders as they are now.
///
/// From orbit the globe is coloured from a plate painted once, when the map was built, because
/// a mipmapped picture is what an eye wants at that distance and the country-id map would
/// shimmer. Close up the colour comes from the live owner of every province. So every border a
/// war or a secession had moved showed twice: the new country in its colour close in, and the
/// old country's colour from far away - a conquered half of Siberia stood out of Kazakhstan as
/// a pale patch of another country. Now, when owners move, the plate is painted again from the
/// live owners on a worker thread, at most once every couple of seconds, and swapped in when it
/// is ready. A border is shaded where the owner changes between neighbouring cells, an old
/// country border that is now inside one country keeps only the softer shade of a region line.
/// </summary>
public sealed partial class GlobePresentation
{
    private const double PlateRepaintSeconds = 2.0;

    private ImageTexture _plateTexture;
    private byte[] _platePixels;
    private ushort[] _plateProvinces;
    private byte[] _plateShade;
    private ushort[] _platePainted;
    private Task<Image> _plateWork;
    private ulong _plateRequestedMsec;
    private bool _platePending;

    private void RequestPlateRepaint(Simulation.ProvinceTopology topology)
    {
        if (_plateProvinces is null || _platePixels is null || topology is null) return;
        _platePending = true;
        TryStartPlateRepaint(topology);
    }

    private void TryStartPlateRepaint(Simulation.ProvinceTopology topology)
    {
        if (!_platePending || _plateWork is not null) return;
        var now = Time.GetTicksMsec();
        if (_plateRequestedMsec > 0 && now - _plateRequestedMsec < PlateRepaintSeconds * 1000) return;
        _plateRequestedMsec = now;
        _platePending = false;
        var owners = (ushort[])topology.ProvinceToNation.Clone();
        // Nothing has changed hands since the plate was last painted: nothing to do.
        if (_platePainted is not null && owners.AsSpan().SequenceEqual(_platePainted)) return;
        _platePainted = owners;
        var colors = new System.Collections.Generic.Dictionary<int, uint>(_colorByMapIndex);
        var provinces = _plateProvinces;
        var shade = _plateShade;
        var source = _platePixels;
        // The picture and its mipmaps are made on the worker too: the main thread only uploads.
        _plateWork = Task.Run(() =>
        {
            var image = Image.CreateFromData(MapWidth / PoliticalScale, MapHeight / PoliticalScale,
                false, Image.Format.Rgba8, PaintPlate(owners, colors, provinces, shade, source));
            image.GenerateMipmaps();
            return image;
        });
    }

    /// <summary>Called every frame: swaps a finished plate in, and starts a waiting one.</summary>
    private void PollPlateRepaint()
    {
        if (_plateWork is { IsCompleted: true } done)
        {
            _plateWork = null;
            if (done.IsCompletedSuccessfully && done.Result is { } image)
            {
                if (_plateTexture is not null) _plateTexture.Update(image);
                image.Dispose();
            }
        }
        if (_platePending && _campaign?.ProvinceMap is { } topology) TryStartPlateRepaint(topology);
    }

    private static byte[] PaintPlate(ushort[] owners, System.Collections.Generic.Dictionary<int, uint> colors,
        ushort[] provinces, byte[] shade, byte[] source)
    {
        var width = MapWidth / PoliticalScale;
        var height = MapHeight / PoliticalScale;
        var pixels = (byte[])source.Clone();
        ushort OwnerAt(int cell)
        {
            var province = provinces[cell];
            return province > 0 && province < owners.Length ? owners[province] : (ushort)0;
        }
        for (var y = 0; y < height; y++)
        {
            for (var x = 0; x < width; x++)
            {
                var cell = y * width + x;
                if (source[cell * 4 + 3] == 0) continue;           // sea stays sea
                var owner = OwnerAt(cell);
                if (owner == 0) continue;                          // nothing known: keep the authored paint
                var east = y * width + (x + 1) % width;
                var west = y * width + (x == 0 ? width - 1 : x - 1);
                var north = (y == 0 ? y : y - 1) * width + x;
                var south = (y == height - 1 ? y : y + 1) * width + x;
                bool Differs(int other) => source[other * 4 + 3] != 0 && OwnerAt(other) is var o && o != 0 && o != owner;
                var border = Differs(east) || Differs(west) || Differs(north) || Differs(south);
                // The authored shade: 0.34 a country border, 0.70 a region line, 0.92 inland.
                var authored = shade[cell] / 255f;
                var factor = border ? 0.34f : authored < 0.8f ? 0.70f : 0.92f;
                var packed = colors.TryGetValue(owner, out var color) ? color : 0x6E7B8BFFu;
                var write = cell * 4;
                pixels[write] = (byte)(((packed >> 24) & 0xFF) * factor);
                pixels[write + 1] = (byte)(((packed >> 16) & 0xFF) * factor);
                pixels[write + 2] = (byte)(((packed >> 8) & 0xFF) * factor);
            }
        }
        return pixels;
    }
}
