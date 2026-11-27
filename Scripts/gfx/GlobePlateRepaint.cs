using System;
using System.Threading.Tasks;
using Godot;

namespace DealOrForce;

/// <summary>
/// The far-away political map follows the borders as they are now.
///
/// From orbit the globe is coloured from a plate painted when the map was built, because a
/// mipmapped picture is what an eye wants at that distance and the country-id map would
/// shimmer. Close up the colour comes from the live owner of every province. So every border a
/// war or a secession had moved showed twice: the new country in its colour close in, and the
/// old country's colour from far away - a conquered half of Siberia stood out of Kazakhstan as
/// a pale patch of another country. When owners move, the plate is painted again from the live
/// owners on a worker thread, at most once every couple of seconds, and swapped in when it is
/// ready.
///
/// The opening plate and every later one come from the same painter
/// (Simulation.PoliticalPlate), which is the point: a border is shaded where the holder changes
/// between neighbouring cells, an old country border that is now inside one country keeps only
/// the softer shade of a region line, and neither answer depends on when the border was drawn.
/// The plate is asked for whenever the map changes, not only when something differs from the
/// authored world - the question is whether the picture on screen matches the owners of today,
/// and a province handed back at a peace, or a second campaign begun in the same session, moves
/// the owners TOWARDS the authored world and still leaves the picture wrong.
/// </summary>
public sealed partial class GlobePresentation
{
    private const double PlateRepaintSeconds = 0.75;
    private const double FrontierRepaintSeconds = 2.5;
    private const double FastSimPlateRepaintSeconds = 2.5;
    private const double FastSimFrontierRepaintSeconds = 10.0;
    private const double MobilePlateRepaintSeconds = 6.5;

    private ImageTexture _plateTexture;
    private Simulation.PoliticalPlateSource _plateSource;

    /// <summary>The owners the plate on screen was painted from, and those of the one being painted.</summary>
    private ushort[] _plateShownOwners;
    private ushort[] _plateWorkOwners;
    private byte[] _platePixelScratch;
    private byte[] _frontierPixelScratch;
    private Image _pendingPlateUpload;
    private ushort[] _pendingPlateOwners;

    /// <summary>A country changed colour since the plate on screen was painted.</summary>
    private bool _plateColoursStale;
    private bool _plateWorkStartedStale;
    private bool _plateFailureReported;
    private Task<(Image Plate, Image Frontier)> _plateWork;
    private ImageTexture _frontierTexture;
    /// <summary>The authored holder of every raster cell, kept for the frontier field's repaints.</summary>
    private ushort[] _frontierAuthored;
    private ulong _plateRequestedMsec;
    private ulong _frontierRequestedMsec;
    private bool _platePending;

    /// <summary>The opening plate has been uploaded: remember what it was painted from.</summary>
    private void PlateApplied(Simulation.PoliticalPlateSource source, ushort[] owners)
    {
        _plateSource = source;
        _plateShownOwners = owners;
        _plateWorkOwners = null;
        _pendingPlateUpload = null;
        _pendingPlateOwners = null;
        // Painted from the palette this campaign was bound with, a moment ago.
        _plateColoursStale = false;
        _plateWorkStartedStale = false;
    }

    private void RequestPlateRepaint(Simulation.ProvinceTopology topology)
    {
        if (_plateSource is null || topology is null) return;
        _platePending = true;
        TryStartPlateRepaint(topology);
    }

    private void TryStartPlateRepaint(Simulation.ProvinceTopology topology)
    {
        if (!_platePending || _plateWork is not null || _pendingPlateUpload is not null) return;
        // The picture on screen already is today's owners in today's colours: nothing to do.
        if (!_plateColoursStale && _plateShownOwners is not null &&
            topology.ProvinceToNation.AsSpan().SequenceEqual(_plateShownOwners))
        {
            _platePending = false;
            return;
        }
        var now = Time.GetTicksMsec();
        var fastForward = _campaign is { IsPaused: false, Speed: >= 4 };
        var desktopPlateSeconds = fastForward ? FastSimPlateRepaintSeconds : PlateRepaintSeconds;
        var desktopFrontierSeconds = fastForward ? FastSimFrontierRepaintSeconds : FrontierRepaintSeconds;
        var cooldownMs = (Platform.PlatformProfile.Current?.IsTouchPrimary == true ? MobilePlateRepaintSeconds : desktopPlateSeconds) * 1000.0;
        if (_plateRequestedMsec > 0 && now - _plateRequestedMsec < cooldownMs) return;
        _plateRequestedMsec = now;
        _platePending = false;
        _plateWorkStartedStale = _plateColoursStale;
        _plateColoursStale = false;
        var owners = (ushort[])topology.ProvinceToNation.Clone();
        _plateWorkOwners = owners;
        // Never written after the map is applied, so the worker may read it as it is.
        var authoredOwners = _paintedOwner;
        var colors = new System.Collections.Generic.Dictionary<int, uint>(_colorByMapIndex);
        var source = _plateSource;
        var shades = Simulation.MapBorderStyles.PlateShades();
        // The picture and its mipmaps are made on the worker too: the main thread only uploads.
        // Skip heavy 5760x2880 FrontierField repaints during active war on touch/mobile GPUs
        // (close-up borders and UploadPlateCatchUp stay exact without 22 MB mipmap uploads),
        // and throttle desktop FrontierField rebuilds to 2.5s (10s at Speed 4/5) while keeping
        // fast 0.75s (2.5s at Speed 4/5) PoliticalPlate updates with reusable LOH pixel buffers.
        var wantFrontierNow = FrontierFieldWanted() && Platform.PlatformProfile.Current?.IsTouchPrimary != true &&
                              (_frontierRequestedMsec == 0 || now - _frontierRequestedMsec >= desktopFrontierSeconds * 1000.0);
        if (wantFrontierNow) _frontierRequestedMsec = now;
        var frontierAuthored = wantFrontierNow ? _frontierAuthored : null;
        _plateWork = Task.Run(() =>
        {
            var platePixels = Simulation.PoliticalPlate.PaintInto(
                source, owners, authoredOwners, colors, shades, ref _platePixelScratch);
            var image = Image.CreateFromData(source.Width, source.Height, false, Image.Format.Rgba8, platePixels);
            image.GenerateMipmaps();
            var frontier = frontierAuthored is null ? null
                : FrontierImageInto(frontierAuthored, topology, owners, authoredOwners, ref _frontierPixelScratch);
            return (image, frontier);
        });
    }

    /// <summary>
    /// The frontier field as a mipmapped one-byte picture, made where it is called - on a worker.
    /// Null when the grids do not match the topology, which leaves the shader on the per-pixel
    /// rebuild at every zoom rather than drawing a stroke in the wrong place.
    /// </summary>
    private static Image FrontierImage(ushort[] authored, Simulation.ProvinceTopology topology,
        ushort[] owners, ushort[] authoredOwners)
    {
        byte[] scratch = null;
        return FrontierImageInto(authored, topology, owners, authoredOwners, ref scratch);
    }

    private static Image FrontierImageInto(ushort[] authored, Simulation.ProvinceTopology topology,
        ushort[] owners, ushort[] authoredOwners, ref byte[] scratch)
    {
        if (authored is null || topology?.CellToProvince is null) return null;
        var width = topology.Width;
        var height = topology.Height;
        if (authored.Length < width * height || topology.CellToProvince.Length < width * height) return null;
        var bytes = Simulation.FrontierField.BuildInto(authored, topology.CellToProvince, width, height, owners, authoredOwners, ref scratch);
        var image = Image.CreateFromData(width, height, false, Image.Format.R8, bytes);
        image.GenerateMipmaps();
        return image;
    }

    private static Image FrontierImageOrNull(Task<Image> work)
    {
        try { return work.GetAwaiter().GetResult(); }
        catch (Exception exception)
        {
            GD.PushWarning($"[dof] frontier field not built, far borders use the per-pixel rebuild: {exception.Message}");
            return null;
        }
    }

    /// <summary>Upload a frontier field and switch the far stroke onto it. Null leaves things as they are.</summary>
    private void ApplyFrontier(Image frontier)
    {
        if (frontier is null || _globeMaterial is null) return;
        // Compatibility/Mobile packed shader has no frontier_field sampler; keep frontier_ready at 0.
        if (!FrontierFieldWanted())
        {
            _globeMaterial.SetShaderParameter("frontier_ready", 0f);
            return;
        }
        if (_frontierTexture is not null && _frontierTexture.GetWidth() == frontier.GetWidth() &&
            _frontierTexture.GetHeight() == frontier.GetHeight())
            _frontierTexture.Update(frontier);
        else
        {
            _frontierTexture = ImageTexture.CreateFromImage(frontier);
            _globeMaterial.SetShaderParameter("frontier_field", _frontierTexture);
        }
        _globeMaterial.SetShaderParameter("frontier_cap", Simulation.FrontierField.Cap);
        _globeMaterial.SetShaderParameter("frontier_ready", 1f);
    }

    /// <summary>Called every frame: swaps a finished plate in, and starts a waiting one. Returns true when a large map texture uploaded this frame.</summary>
    private bool PollPlateRepaint()
    {
        if (_pendingPlateUpload is { } pendingPlate)
        {
            _pendingPlateUpload = null;
            try
            {
                if (_plateTexture is not null) _plateTexture.Update(pendingPlate);
                _plateShownOwners = _pendingPlateOwners;
                _pendingPlateOwners = null;
                // The plate has caught up: the stopgap table has nothing left to say about
                // the provinces this repaint covered.
                UploadPlateCatchUp();
            }
            finally
            {
                pendingPlate.Dispose();
            }
            return true;
        }
        if (_plateWork is { IsCompleted: true } done)
        {
            _plateWork = null;
            var uploaded = false;
            if (done.IsCompletedSuccessfully && done.Result.Plate is { } image)
            {
                var frontier = done.Result.Frontier;
                _plateWorkStartedStale = false;
                if (frontier is not null)
                {
                    // Stagger FrontierField GPU upload (Frame N) and Plate GPU upload (Frame N+1) across
                    // separate frames so a single frame never uploads 32 MB of mipmapped textures at once.
                    _pendingPlateUpload = image;
                    _pendingPlateOwners = _plateWorkOwners;
                    try { ApplyFrontier(frontier); }
                    finally { frontier.Dispose(); }
                    uploaded = true;
                }
                else
                {
                    try
                    {
                        if (_plateTexture is not null) _plateTexture.Update(image);
                        _plateShownOwners = _plateWorkOwners;
                        UploadPlateCatchUp();
                    }
                    finally
                    {
                        image.Dispose();
                    }
                    uploaded = true;
                }
            }
            else
            {
                // Dispose any orphaned Images if the task completed with partial output or canceled.
                if (done.IsCompletedSuccessfully)
                {
                    done.Result.Plate?.Dispose();
                    done.Result.Frontier?.Dispose();
                }
                // Preserve colour staleness if the background worker threw before committing.
                _plateColoursStale |= _plateWorkStartedStale;
                _plateWorkStartedStale = false;
                if (!_plateFailureReported)
                {
                    // The stopgap table keeps the colours right until a later repaint succeeds; the
                    // next change of the map asks again, because the picture still does not match.
                    _plateFailureReported = true;
                    GD.PushError($"[dof] far political plate repaint failed: {done.Exception?.GetBaseException().Message}");
                }
            }
            _plateWorkOwners = null;
            return uploaded;
        }
        if (_platePending && _pendingPlateUpload is null && _campaign?.ProvinceMap is { } topology)
            TryStartPlateRepaint(topology);
        return false;
    }

    /// <summary>
    /// The provinces the plate on screen has not caught up with (PoliticalPlate.PackCatchUp),
    /// uploaded as the table both materials lay over the plate sample. Empty almost always.
    /// </summary>
    private void UploadPlateCatchUp()
    {
        if (_campaign?.ProvinceMap is not { } topology || _globeMaterial is null) return;
        var pixels = _ownerPixels ??= new byte[256 * 256 * 4];
        Simulation.PoliticalPlate.PackCatchUp(topology.ProvinceCount, topology.ProvinceToNation,
            _plateShownOwners, _colorByMapIndex, Simulation.MapBorderStyles.PlateShades().Inland, pixels);
        if (_ownerImage is null) _ownerImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _ownerImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("province_owner_colors", _ownerImage, ref _ownerTexture);
    }
}
