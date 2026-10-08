using System;
using System.Collections.Generic;
using System.Linq;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>A read-only crop of the actual political raster, not a hypothetical map command.</summary>
public partial class DealTerritoryPreview : TextureRect
{
    private const int RasterWidth = 400, RasterHeight = 180;
    private ImageTexture _imageTexture;
    private string _lastKey = "";
    private Campaign _lastCampaign;

    public DealTerritoryPreview()
    {
        Name = "DealTerritoryPreview";
        ExpandMode = ExpandModeEnum.IgnoreSize;
        StretchMode = StretchModeEnum.KeepAspectCentered;
        CustomMinimumSize = new Vector2(200, 130);
        SizeFlagsHorizontal = SizeFlags.ExpandFill;
        MouseFilter = MouseFilterEnum.Ignore;
    }

    public void Refresh(Campaign campaign, IEnumerable<int> give, IEnumerable<int> ask,
        bool giveRelease, bool askRelease)
    {
        var map = campaign?.ProvinceMap;
        var giveIds = give.ToHashSet();
        var askIds = ask.ToHashSet();
        Visible = map is not null && giveIds.Count + askIds.Count > 0;
        if (!Visible) { _lastKey = ""; return; }
        var key = campaign.MapRevision + ":" + string.Join(',', giveIds.Order()) + ":" +
            string.Join(',', askIds.Order()) + ":" + giveRelease + ":" + askRelease;
        if (ReferenceEquals(_lastCampaign, campaign) && key == _lastKey) return;
        _lastCampaign = campaign; _lastKey = key;

        var selected = giveIds.Concat(askIds).ToHashSet();
        var centres = Enumerable.Range(1, map.ProvinceCount)
            .Where(p => selected.Contains(map.ProvinceToState[p])).Select(p => map.ProvinceCenterCell[p]).ToArray();
        if (centres.Length == 0) { Visible = false; return; }
        // Cut the cylindrical world at the largest empty longitude interval. A Pacific
        // package must be shown together, not stretched across both edges of a world map.
        var xs = centres.Select(cell => cell % map.Width).Distinct().Order().ToArray();
        var origin = xs[0]; var largestGap = -1;
        for (var i = 0; i < xs.Length; i++)
        {
            var next = xs[(i + 1) % xs.Length];
            var gap = i == xs.Length - 1 ? next + map.Width - xs[i] : next - xs[i];
            if (gap > largestGap) { largestGap = gap; origin = next; }
        }
        int Unwrap(int x) => (x - origin + map.Width) % map.Width;
        var padding = Math.Max(6, map.Width / 180);
        double left = -padding, right = centres.Max(cell => Unwrap(cell % map.Width)) + padding;
        double top = Math.Max(0, centres.Min(cell => cell / map.Width) - padding);
        double bottom = Math.Min(map.Height - 1, centres.Max(cell => cell / map.Width) + padding);
        var width = Math.Max(1, right - left); var height = Math.Max(1, bottom - top);
        var ratio = RasterWidth / (double)RasterHeight;
        if (width / height < ratio) { var add = (height * ratio - width) / 2; left -= add; right += add; }
        else { var add = (width / ratio - height) / 2; top -= add; bottom += add; }

        var colours = campaign.Nations.ToDictionary(n => n.MapIndex, n => new Color(n.Color).Darkened(.35f));
        var water = Ui.Colour("surface.overlay");
        var neutral = Ui.Colour("surface.panel");
        var ours = Ui.Colour("text.accent");
        var theirs = Ui.Colour("state.positive");
        var pixels = new int[RasterWidth * RasterHeight];
        using var image = Image.CreateEmpty(RasterWidth, RasterHeight, false, Image.Format.Rgba8);
        for (var y = 0; y < RasterHeight; y++)
        for (var x = 0; x < RasterWidth; x++)
        {
            var rx = ((int)(origin + left + (right - left) * (x + .5) / RasterWidth) % map.Width + map.Width) % map.Width;
            var ry = (int)(top + (bottom - top) * (y + .5) / RasterHeight);
            var province = ry < 0 || ry >= map.Height ? 0 : map.CellToProvince[ry * map.Width + rx];
            var region = province == 0 ? 0 : map.ProvinceToState[province];
            pixels[y * RasterWidth + x] = region;
            var colour = province == 0 || map.WaterByProvince[province] ? water :
                colours.GetValueOrDefault(map.ProvinceToNation[province], neutral);
            var offered = giveIds.Contains(region); var requested = askIds.Contains(region);
            if (offered || requested)
            {
                colour = offered ? ours : theirs;
                if ((offered ? giveRelease : askRelease) && (x + y) % 10 < 3) colour = colour.Darkened(.45f);
            }
            // Region outlines make the package legible without inventing province shapes.
            if (region != 0 && (x > 0 && pixels[y * RasterWidth + x - 1] != region ||
                              y > 0 && pixels[(y - 1) * RasterWidth + x] != region)) colour = colour.Lightened(.3f);
            image.SetPixel(x, y, colour);
        }
        if (_imageTexture is null) { _imageTexture = ImageTexture.CreateFromImage(image); Texture = _imageTexture; }
        else _imageTexture.Update(image);
    }

    public override void _ExitTree() { Texture = null; _imageTexture?.Dispose(); _imageTexture = null; }
}
