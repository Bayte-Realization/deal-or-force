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
    private const double PlateRepaintSeconds = 0.25;
    private const double FrontierRepaintSeconds = 0.50;
    private const double FastSimPlateRepaintSeconds = 0.35;
    private const double FastSimFrontierRepaintSeconds = 0.85;
    private const double GpuPlateRepaintSeconds = 0.16;
    private const double GpuFrontierRepaintSeconds = 0.30;
    private const double MobilePlateRepaintSeconds = 1.0;

    private ImageTexture _plateTexture;
    private Simulation.PoliticalPlateSource _plateSource;

    /// <summary>The owners the plate on screen was painted from, and those of the one being painted.</summary>
    private ushort[] _plateShownOwners;
    private ushort[] _plateWorkOwners;
    private ushort[] _frontierShownOwners;
    private byte[] _plateMipScratch;
    private byte[] _frontierMipScratch;
    private Image _pendingPlateUpload;
    private ushort[] _pendingPlateOwners;

    /// <summary>A country changed colour since the plate on screen was painted.</summary>
    private bool _plateColoursStale;
    private bool _plateWorkStartedStale;
    private bool _plateFailureReported;
    private Task<(Image Plate, Image Frontier, ushort[] FrontierOwners)> _plateWork;
    private ImageTexture _frontierTexture;
    /// <summary>The authored holder of every raster cell, kept for the frontier field's repaints.</summary>
    private ushort[] _frontierAuthored;
    private ulong _plateRequestedMsec;
    private ulong _frontierRequestedMsec;
    private bool _platePending;

    // Optional Forward+ (Vulkan / D3D12) GLSL compute backend; null on Compatibility/GLES3 & headless.
    private GpuMapCompute _gpuMapCompute;
    private bool _gpuMapBound;

    /// <summary>The opening plate has been uploaded: remember what it was painted from.</summary>
    private void PlateApplied(Simulation.PoliticalPlateSource source, ushort[] owners)
    {
        _plateSource = source;
        _plateShownOwners = owners;
        _frontierShownOwners = null;
        _plateWorkOwners = null;
        _plateMipScratch = null;
        _frontierMipScratch = null;
        _pendingPlateUpload?.Dispose();
        _pendingPlateUpload = null;
        _pendingPlateOwners = null;
        // Painted from the palette this campaign was bound with, a moment ago.
        _plateColoursStale = false;
        _plateWorkStartedStale = false;
    }

    private void FrontierApplied(ushort[] owners)
    {
        _frontierShownOwners = owners;
    }

    private void TryStartGpuMapCompute(Simulation.ProvinceTopology topology, ushort[] initialOwners)
    {
        DisposeGpuMapCompute();
        if (_plateSource is null || !GpuMapCompute.IsEnvironmentAllowed()) return;
        var gpu = new GpuMapCompute();
        var wantFrontier = FrontierFieldWanted() && _frontierAuthored is not null && topology?.CellToProvince is not null;
        if (gpu.BeginInitialize(
                _plateSource,
                wantFrontier ? _frontierAuthored : null,
                wantFrontier ? topology.CellToProvince : null,
                wantFrontier ? topology.Width : 0,
                wantFrontier ? topology.Height : 0,
                initialOwners,
                _paintedOwner,
                _colorByMapIndex,
                Simulation.MapBorderStyles.PlateShades()))
        {
            _gpuMapCompute = gpu;
            _gpuMapBound = false;
        }
        else
        {
            gpu.Dispose();
        }
    }

    private void DisposeGpuMapCompute(bool markStaleForCpuFallback = false)
    {
        if (_gpuMapBound && _globeMaterial is not null)
        {
            if (_plateTexture is not null) _globeMaterial.SetShaderParameter("political_map", _plateTexture);
            if (_frontierTexture is not null) _globeMaterial.SetShaderParameter("frontier_field", _frontierTexture);
        }
        if (markStaleForCpuFallback && _gpuMapBound)
        {
            // CPU textures have not been updated while GPU compute owned the material parameters;
            // reset tracking so the next repaint rebuilds CPU plate/frontier cleanly.
            _plateShownOwners = null;
            _frontierShownOwners = null;
            _plateMipScratch = null;
            _frontierMipScratch = null;
            _platePending = true;
        }
        _gpuMapBound = false;
        _gpuMapCompute?.Dispose();
        _gpuMapCompute = null;
    }

    private void PollGpuMapComputeBinding()
    {
        if (_gpuMapCompute is null) return;
        if (_gpuMapCompute.IsFailed)
        {
            DisposeGpuMapCompute(markStaleForCpuFallback: true);
            return;
        }
        if (!_gpuMapBound && _gpuMapCompute.IsReady && _globeMaterial is not null)
        {
            if (!_gpuMapCompute.BindTexturesOnMainThread())
            {
                DisposeGpuMapCompute(markStaleForCpuFallback: true);
                return;
            }
            _globeMaterial.SetShaderParameter("political_map", _gpuMapCompute.PlateTexture);
            if (_gpuMapCompute.HasFrontier && FrontierFieldWanted())
            {
                _globeMaterial.SetShaderParameter("frontier_field", _gpuMapCompute.FrontierTexture);
                _globeMaterial.SetShaderParameter("frontier_cap", Simulation.FrontierField.Cap);
                _globeMaterial.SetShaderParameter("frontier_ready", 1f);
            }
            _gpuMapBound = true;
            var frontierSummary = _gpuMapCompute.HasFrontier
                ? $"{_gpuMapCompute.FrontierWidth}x{_gpuMapCompute.FrontierHeight} (6-pass 1+JFA)"
                : "off";
            GD.Print($"[dof] gpu_map_compute active: plate={_gpuMapCompute.PlateWidth}x{_gpuMapCompute.PlateHeight} frontier={frontierSummary} driver={RenderingServer.GetCurrentRenderingDriverName()}");
        }
    }

    private void RequestPlateRepaint(Simulation.ProvinceTopology topology, ushort[] ownersOverride = null, bool? fastForwardOverride = null)
    {
        if (_plateSource is null || topology is null) return;
        _platePending = true;
        TryStartPlateRepaint(topology, ownersOverride, fastForwardOverride);
    }

    private static void ResolvePlateCooldowns(bool gpuReady, bool fastForward, out double plateMs, out double frontierMs)
    {
        var isTouch = Platform.PlatformProfile.Current?.IsTouchPrimary == true;
        var plateSec = isTouch
            ? MobilePlateRepaintSeconds
            : gpuReady ? GpuPlateRepaintSeconds : (fastForward ? FastSimPlateRepaintSeconds : PlateRepaintSeconds);
        var frontierSec = gpuReady
            ? GpuFrontierRepaintSeconds
            : (fastForward ? FastSimFrontierRepaintSeconds : FrontierRepaintSeconds);
        plateMs = plateSec * 1000.0;
        frontierMs = frontierSec * 1000.0;
    }

    private bool TryDispatchGpuPlateRepaint(
        ushort[] owners, ushort[] authoredOwners, Simulation.MapPlateShades shades,
        ulong now, bool needPlate, bool needFrontier, bool wantFrontierNow)
    {
        if (!(_gpuMapBound && _gpuMapCompute is { IsReady: true })) return false;
        if (wantFrontierNow && !_gpuMapCompute.HasFrontier) return false;
        if (wantFrontierNow) _frontierRequestedMsec = now;
        var updateColors = _plateColoursStale;
        if (needPlate) _plateColoursStale = false;
        if (_gpuMapCompute.DispatchRepaint(owners, authoredOwners, _colorByMapIndex, shades,
                updatePlate: needPlate, updateColors: updateColors, updateFrontier: wantFrontierNow))
        {
            if (needPlate) _plateShownOwners = owners;
            if (wantFrontierNow) _frontierShownOwners = owners;
            UploadPlateCatchUp(owners);
            return true;
        }
        _plateColoursStale |= updateColors;
        DisposeGpuMapCompute(markStaleForCpuFallback: true);
        _platePending = needFrontier && !wantFrontierNow;
        return false;
    }

    private void TryStartPlateRepaint(Simulation.ProvinceTopology topology, ushort[] ownersOverride = null, bool? fastForwardOverride = null)
    {
        PollGpuMapComputeBinding();
        if (!_platePending || _plateWork is not null || _pendingPlateUpload is not null) return;
        var currentOwners = ownersOverride is { Length: > 0 } ? ownersOverride : topology.ProvinceToNation;
        var plateMatches = !_plateColoursStale && _plateShownOwners is not null &&
                           currentOwners.AsSpan().SequenceEqual(_plateShownOwners);
        var frontierWanted = FrontierFieldWanted() && Platform.PlatformProfile.Current?.IsTouchPrimary != true &&
                             _frontierAuthored is not null;
        var frontierMatches = !frontierWanted ||
                              (_frontierShownOwners is not null && currentOwners.AsSpan().SequenceEqual(_frontierShownOwners));
        if (plateMatches && frontierMatches) { _platePending = false; return; }
        if (_gpuMapCompute is { IsPending: true }) return;

        var now = Time.GetTicksMsec();
        var fastForward = fastForwardOverride ?? (_campaign is { IsPaused: false, Speed: >= 4 });
        var gpuReady = _gpuMapBound && _gpuMapCompute is { IsReady: true };
        ResolvePlateCooldowns(gpuReady, fastForward, out var plateCooldownMs, out var frontierCooldownMs);

        var needPlate = !plateMatches;
        var needFrontier = !frontierMatches;
        var frontierReadyByTime = _frontierRequestedMsec == 0 || now - _frontierRequestedMsec >= frontierCooldownMs;
        if (needPlate)
        {
            if (_plateRequestedMsec > 0 && now - _plateRequestedMsec < plateCooldownMs) return;
            _plateRequestedMsec = now;
        }
        else if (!frontierReadyByTime)
        {
            return;
        }

        var wantFrontierNow = needFrontier && frontierReadyByTime;
        _platePending = needFrontier && !wantFrontierNow;
        var owners = ownersOverride is { Length: > 0 } ? ownersOverride : (ushort[])topology.ProvinceToNation.Clone();
        var authoredOwners = _paintedOwner;
        var shades = Simulation.MapBorderStyles.PlateShades();

        // Fast Path: Forward+ GLSL Compute Shaders (Vulkan / D3D12) — zero RAM->VRAM upload, < 1 ms in VRAM!
        if (TryDispatchGpuPlateRepaint(owners, authoredOwners, shades, now, needPlate, needFrontier, wantFrontierNow))
            return;

        _plateWorkStartedStale = _plateColoursStale;
        _plateColoursStale = false;
        _plateWorkOwners = owners;
        var prevPlateOwners = _plateWorkStartedStale ? null : _plateShownOwners;
        var prevFrontierOwners = _frontierShownOwners;
        var colors = new System.Collections.Generic.Dictionary<int, uint>(_colorByMapIndex);
        var source = _plateSource;
        if (wantFrontierNow) _frontierRequestedMsec = now;
        var frontierAuthored = wantFrontierNow ? _frontierAuthored : null;
        var forceFullPlate = _plateWorkStartedStale;

        // Tier A CPU Path (Android ARM64 NEON, Compatibility/GLES3, headless):
        // Incremental dirty-rect PoliticalPlate + FrontierField + incremental mipmap pyramid.
        _plateWork = Task.Run(() =>
        {
            var plateMipBytes = Simulation.PoliticalPlate.PaintDirtyMipmappedInto(
                source, prevPlateOwners, owners, authoredOwners, colors, shades,
                forceFullPlate, ref _plateMipScratch, out _);
            var image = Image.CreateFromData(source.Width, source.Height, true, Image.Format.Rgba8, plateMipBytes);
            Image frontier = null;
            ushort[] frontierDoneOwners = null;
            if (frontierAuthored is not null)
            {
                frontier = FrontierImageDirtyInto(
                    frontierAuthored, topology, prevFrontierOwners, owners, authoredOwners, ref _frontierMipScratch);
                if (frontier is not null) frontierDoneOwners = owners;
            }
            return (image, frontier, frontierDoneOwners);
        });
    }

    private static Image FrontierImageDirtyInto(
        ushort[] authored, Simulation.ProvinceTopology topology,
        ushort[] prevOwners, ushort[] owners, ushort[] authoredOwners, ref byte[] mipScratch)
    {
        if (authored is null || topology?.CellToProvince is null) return null;
        var width = topology.Width;
        var height = topology.Height;
        if (authored.Length < width * height || topology.CellToProvince.Length < width * height) return null;
        var mipBytes = Simulation.FrontierField.BuildDirtyMipmappedInto(
            authored, topology.CellToProvince, width, height, prevOwners, owners, authoredOwners,
            forceFull: false, ref mipScratch, out _);
        return Image.CreateFromData(width, height, true, Image.Format.R8, mipBytes);
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
    private bool PollPlateRepaint(ushort[] ownersOverride = null, bool? fastForwardOverride = null)
    {
        PollGpuMapComputeBinding();
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
                UploadPlateCatchUp(ownersOverride);
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
                var frontierDoneOwners = done.Result.FrontierOwners;
                _plateWorkStartedStale = false;
                if (frontier is not null)
                {
                    // Stagger FrontierField GPU upload (Frame N) and Plate GPU upload (Frame N+1) across
                    // separate frames so a single frame never uploads 32 MB of mipmapped textures at once.
                    _pendingPlateUpload = image;
                    _pendingPlateOwners = _plateWorkOwners;
                    try
                    {
                        ApplyFrontier(frontier);
                        if (frontierDoneOwners is not null) _frontierShownOwners = frontierDoneOwners;
                    }
                    finally { frontier.Dispose(); }
                    uploaded = true;
                }
                else
                {
                    try
                    {
                        if (_plateTexture is not null) _plateTexture.Update(image);
                        _plateShownOwners = _plateWorkOwners;
                        UploadPlateCatchUp(ownersOverride);
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
        if (_platePending && _pendingPlateUpload is null && (_provinceTopology ?? _campaign?.ProvinceMap) is { } topology)
            TryStartPlateRepaint(topology, ownersOverride, fastForwardOverride);
        return false;
    }

    /// <summary>
    /// The provinces the plate on screen has not caught up with (PoliticalPlate.PackCatchUp),
    /// uploaded as the table both materials lay over the plate sample. Empty almost always.
    /// </summary>
    private void UploadPlateCatchUp(ushort[] ownersOverride = null)
    {
        var topology = _provinceTopology ?? _campaign?.ProvinceMap;
        if (topology is null || _globeMaterial is null) return;
        var currentOwners = ownersOverride is { Length: > 0 } ? ownersOverride : topology.ProvinceToNation;
        var pixels = _ownerPixels ??= new byte[256 * 256 * 4];
        Simulation.PoliticalPlate.PackCatchUp(topology.ProvinceCount, currentOwners,
            _plateShownOwners, _colorByMapIndex, Simulation.MapBorderStyles.PlateShades().Inland, pixels);
        if (_ownerImage is null) _ownerImage = Image.CreateFromData(256, 256, false, Image.Format.Rgba8, pixels);
        else _ownerImage.SetData(256, 256, false, Image.Format.Rgba8, pixels);
        UpdateMapImage("province_owner_colors", _ownerImage, ref _ownerTexture);
    }
}
