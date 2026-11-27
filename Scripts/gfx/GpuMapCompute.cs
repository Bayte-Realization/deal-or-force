using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using DealOrForce.Simulation;
using Godot;

namespace DealOrForce;

/// <summary>
/// Zero-copy Forward+ (Vulkan / D3D12) GLSL 450 Compute Shader backend for <see cref="PoliticalPlate"/>
/// (1920x960 RGBA8 + mipmaps) and <see cref="FrontierField"/> (5760x2880 R8 via 5-pass Jump Flooding
/// Algorithm + separable Gaussian + mipmaps).
///
/// Outputs directly into GPU-resident <see cref="Texture2Drd"/> textures with zero RAM-to-VRAM image
/// transfers (only a 128 KB province override LUT + 256 KB palette buffer are updated per repaint).
/// Automatically unavailable on Compatibility/GLES3 (Android) and headless runs, where
/// <see cref="GlobePresentation"/> transparently falls back to incremental dirty-rect Rust SIMD.
/// </summary>
internal sealed class GpuMapCompute
{
    private RenderingDevice _rd;
    private int _initState; // 0 = idle, 1 = pending, 2 = ready, -1 = failed
    private bool _disposed;
    private string _failureReason = "";

    private int _plateW, _plateH, _plateMips;
    private int _frontierW, _frontierH, _frontierMips;
    private bool _hasFrontier;

    // Shared dynamic buffers
    private Rid _provLutBuf;
    private Rid _paletteBuf;
    private readonly byte[] _provLutBytes = new byte[65536 * 2];
    private readonly byte[] _paletteBytes = new byte[65536 * 4];

    // Static map buffers
    private Rid _plateCellsBuf;
    private Rid _plateGeoBuf;
    private Rid _frontierCellsBuf;

    // Output textures & per-mip views
    private Rid _plateTex;
    private Rid[] _plateMipViews = Array.Empty<Rid>();
    private Rid _frontierTex;
    private Rid[] _frontierMipViews = Array.Empty<Rid>();
    private Rid _jfaTexA;
    private Rid _jfaTexB;
    private Rid _blurHTex;

    // Shaders & pipelines
    private Rid _plateShader, _platePipe, _plateUset;
    private Rid _mipRgbaShader, _mipRgbaPipe;
    private Rid[] _plateMipUsets = Array.Empty<Rid>();

    private Rid _jfaInitShader, _jfaInitPipe, _jfaInitUset;
    private Rid _jfaStepShader, _jfaStepPipe, _jfaStepUsetAB, _jfaStepUsetBA;
    private Rid _blurHShader, _blurHPipe, _blurHUset;
    private Rid _blurVShader, _blurVPipe, _blurVUset;
    private Rid _mipR8Shader, _mipR8Pipe;
    private Rid[] _frontierMipUsets = Array.Empty<Rid>();

    private readonly List<Rid> _ownedRids = new();

    public Texture2Drd PlateTexture { get; private set; }
    public Texture2Drd FrontierTexture { get; private set; }
    public bool IsReady => System.Threading.Volatile.Read(ref _initState) == 2 && !_disposed;
    public bool IsPending => System.Threading.Volatile.Read(ref _initState) == 1 && !_disposed;
    public bool IsFailed => System.Threading.Volatile.Read(ref _initState) < 0;
    public bool HasFrontier => IsReady && _hasFrontier;
    public string FailureReason => _failureReason;

    public static bool IsEnvironmentAllowed()
    {
        var env = System.Environment.GetEnvironmentVariable("DOF_GPU_MAP_COMPUTE");
        if (string.Equals(env, "0", StringComparison.Ordinal) ||
            string.Equals(env, "false", StringComparison.OrdinalIgnoreCase))
            return false;
        if (Platform.PlatformProfile.Current?.IsTouchPrimary == true ||
            Platform.PlatformProfile.Current?.IsMobile == true)
            return false;
        if (DisplayServer.GetName().Equals("headless", StringComparison.OrdinalIgnoreCase))
            return false;
        return RenderingServer.GetRenderingDevice() is not null;
    }

    /// <summary>
    /// Starts render-thread initialization of GPU compute pipelines and static map SSBOs.
    /// Safe in both Single-Threaded and Multi-Threaded Godot 4 render modes: callers poll
    /// <see cref="IsReady"/> on subsequent frames before binding <see cref="PlateTexture"/>.
    /// </summary>
    public bool BeginInitialize(
        PoliticalPlateSource plateSource,
        ushort[] frontierAuthored,
        ushort[] frontierProvinces,
        int frontierW,
        int frontierH,
        ushort[] initialOwners,
        ushort[] authoredOwners,
        IReadOnlyDictionary<int, uint> colors,
        MapPlateShades shades)
    {
        if (_disposed || System.Threading.Volatile.Read(ref _initState) != 0 || !IsEnvironmentAllowed()) return false;
        if (plateSource is null || plateSource.Width <= 0 || plateSource.Height <= 0) return false;

        _rd = RenderingServer.GetRenderingDevice();
        if (_rd is null) return false;

        _plateW = plateSource.Width;
        _plateH = plateSource.Height;
        _plateMips = MipCount(_plateW, _plateH);

        _hasFrontier = frontierAuthored is not null && frontierProvinces is not null &&
                       frontierW > 0 && frontierH > 0 &&
                       frontierAuthored.Length >= frontierW * frontierH &&
                       frontierProvinces.Length >= frontierW * frontierH;
        _frontierW = _hasFrontier ? frontierW : 0;
        _frontierH = _hasFrontier ? frontierH : 0;
        _frontierMips = _hasFrontier ? MipCount(_frontierW, _frontierH) : 0;

        var plateCells = PackHolderProvinceBytes(plateSource.Holders, plateSource.Provinces, _plateW * _plateH);
        var plateGeo = PadBytesTo4(plateSource.Geography, _plateW * _plateH);
        var frontierCells = _hasFrontier
            ? PackHolderProvinceBytes(frontierAuthored, frontierProvinces, _frontierW * _frontierH)
            : null;
        var hasOverride = FillProvLut(initialOwners, authoredOwners, _provLutBytes);
        FillPalette(colors, _paletteBytes);

        var plateLutCopy = (byte[])_provLutBytes.Clone();
        var paletteCopy = (byte[])_paletteBytes.Clone();

        PlateTexture = new Texture2Drd();
        if (_hasFrontier) FrontierTexture = new Texture2Drd();
        System.Threading.Volatile.Write(ref _initState, 1);

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_disposed)
            {
                System.Threading.Volatile.Write(ref _initState, -1);
                return;
            }
            var ok = InitializeOnRenderThread(
                plateCells, plateGeo, frontierCells,
                plateLutCopy, paletteCopy, hasOverride, shades);
            System.Threading.Volatile.Write(ref _initState, ok ? 2 : -1);
        }));

        return true;
    }

    public bool DispatchRepaint(
        ushort[] owners,
        ushort[] authoredOwners,
        IReadOnlyDictionary<int, uint> colors,
        MapPlateShades shades,
        bool updateColors,
        bool updateFrontier)
    {
        if (!IsReady) return false;

        var hasOverride = FillProvLut(owners, authoredOwners, _provLutBytes);
        var lutCopy = (byte[])_provLutBytes.Clone();
        byte[] palCopy = null;
        if (updateColors && colors is not null)
        {
            FillPalette(colors, _paletteBytes);
            palCopy = (byte[])_paletteBytes.Clone();
        }
        var doFrontier = updateFrontier && _hasFrontier;

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            if (_disposed || System.Threading.Volatile.Read(ref _initState) != 2 || _rd is null) return;
            try
            {
                _rd.BufferUpdate(_provLutBuf, 0, (uint)lutCopy.Length, lutCopy);
                if (palCopy is not null)
                    _rd.BufferUpdate(_paletteBuf, 0, (uint)palCopy.Length, palCopy);

                DispatchPlateOnRenderThread(hasOverride, shades);
                if (doFrontier)
                    DispatchFrontierOnRenderThread(hasOverride);
            }
            catch (Exception ex)
            {
                System.Threading.Volatile.Write(ref _initState, -1);
                _failureReason = ex.Message;
                GD.PushWarning($"[dof] GPU map compute dispatch failed ({ex.Message}); falling back to CPU incremental raster.");
            }
        }));
        return !IsFailed;
    }

    public void Dispose()
    {
        if (_disposed) return;
        _disposed = true;
        System.Threading.Volatile.Write(ref _initState, -1);

        // Clear Texture2Drd references BEFORE freeing underlying RD texture RIDs (Godot 4.8 requirement).
        if (PlateTexture is not null) PlateTexture.TextureRdRid = new Rid();
        if (FrontierTexture is not null) FrontierTexture.TextureRdRid = new Rid();
        PlateTexture = null;
        FrontierTexture = null;

        var rd = _rd;
        _rd = null;
        if (rd is null) return;

        RenderingServer.CallOnRenderThread(Callable.From(() =>
        {
            for (var i = _ownedRids.Count - 1; i >= 0; i--)
            {
                if (_ownedRids[i].IsValid) rd.FreeRid(_ownedRids[i]);
            }
            _ownedRids.Clear();
        }));
    }

    private bool InitializeOnRenderThread(
        byte[] plateCells,
        byte[] plateGeo,
        byte[] frontierCells,
        byte[] initLut,
        byte[] initPalette,
        bool hasOverride,
        MapPlateShades shades)
    {
        try
        {
            _provLutBuf = Track(_rd.StorageBufferCreate((uint)initLut.Length, initLut));
            _paletteBuf = Track(_rd.StorageBufferCreate((uint)initPalette.Length, initPalette));
            _plateCellsBuf = Track(_rd.StorageBufferCreate((uint)plateCells.Length, plateCells));
            _plateGeoBuf = Track(_rd.StorageBufferCreate((uint)plateGeo.Length, plateGeo));

            // Output PoliticalPlate Texture2Drd (RGBA8 + full mip chain)
            var plateFmt = new RDTextureFormat
            {
                Format = RenderingDevice.DataFormat.R8G8B8A8Unorm,
                Width = (uint)_plateW,
                Height = (uint)_plateH,
                Depth = 1,
                ArrayLayers = 1,
                Mipmaps = (uint)_plateMips,
                TextureType = RenderingDevice.TextureType.Type2D,
                Samples = RenderingDevice.TextureSamples.Samples1,
                UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                            RenderingDevice.TextureUsageBits.StorageBit |
                            RenderingDevice.TextureUsageBits.CanUpdateBit
            };
            _plateTex = Track(_rd.TextureCreate(plateFmt, new RDTextureView()));
            _plateMipViews = CreateMipViews(_plateTex, _plateMips);

            _plateShader = CompileCompute("dof_political_plate", PlateCompGlsl);
            _platePipe = Track(_rd.ComputePipelineCreate(_plateShader));
            _plateUset = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
            {
                BufferUniform(0, _plateCellsBuf),
                BufferUniform(1, _plateGeoBuf),
                BufferUniform(2, _provLutBuf),
                BufferUniform(3, _paletteBuf),
                ImageUniform(4, _plateMipViews[0])
            }, _plateShader, 0));

            _mipRgbaShader = CompileCompute("dof_mipmap_rgba8", MipmapRgba8Glsl);
            _mipRgbaPipe = Track(_rd.ComputePipelineCreate(_mipRgbaShader));
            _plateMipUsets = new Rid[_plateMips - 1];
            for (var m = 0; m < _plateMips - 1; m++)
            {
                _plateMipUsets[m] = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    ImageUniform(0, _plateMipViews[m]),
                    ImageUniform(1, _plateMipViews[m + 1])
                }, _mipRgbaShader, 0));
            }

            if (_hasFrontier && frontierCells is not null)
            {
                _frontierCellsBuf = Track(_rd.StorageBufferCreate((uint)frontierCells.Length, frontierCells));

                var frontierFmt = new RDTextureFormat
                {
                    Format = RenderingDevice.DataFormat.R8Unorm,
                    Width = (uint)_frontierW,
                    Height = (uint)_frontierH,
                    Depth = 1,
                    ArrayLayers = 1,
                    Mipmaps = (uint)_frontierMips,
                    TextureType = RenderingDevice.TextureType.Type2D,
                    Samples = RenderingDevice.TextureSamples.Samples1,
                    UsageBits = RenderingDevice.TextureUsageBits.SamplingBit |
                                RenderingDevice.TextureUsageBits.StorageBit |
                                RenderingDevice.TextureUsageBits.CanUpdateBit
                };
                _frontierTex = Track(_rd.TextureCreate(frontierFmt, new RDTextureView()));
                _frontierMipViews = CreateMipViews(_frontierTex, _frontierMips);

                var jfaFmt = new RDTextureFormat
                {
                    Format = RenderingDevice.DataFormat.R32Uint,
                    Width = (uint)_frontierW,
                    Height = (uint)_frontierH,
                    Depth = 1,
                    ArrayLayers = 1,
                    Mipmaps = 1,
                    TextureType = RenderingDevice.TextureType.Type2D,
                    Samples = RenderingDevice.TextureSamples.Samples1,
                    UsageBits = RenderingDevice.TextureUsageBits.StorageBit
                };
                _jfaTexA = Track(_rd.TextureCreate(jfaFmt, new RDTextureView()));
                _jfaTexB = Track(_rd.TextureCreate(jfaFmt, new RDTextureView()));

                var blurFmt = new RDTextureFormat
                {
                    Format = RenderingDevice.DataFormat.R16Sfloat,
                    Width = (uint)_frontierW,
                    Height = (uint)_frontierH,
                    Depth = 1,
                    ArrayLayers = 1,
                    Mipmaps = 1,
                    TextureType = RenderingDevice.TextureType.Type2D,
                    Samples = RenderingDevice.TextureSamples.Samples1,
                    UsageBits = RenderingDevice.TextureUsageBits.StorageBit
                };
                _blurHTex = Track(_rd.TextureCreate(blurFmt, new RDTextureView()));

                _jfaInitShader = CompileCompute("dof_jfa_init", JfaInitGlsl);
                _jfaInitPipe = Track(_rd.ComputePipelineCreate(_jfaInitShader));
                _jfaInitUset = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    BufferUniform(0, _frontierCellsBuf),
                    BufferUniform(1, _provLutBuf),
                    ImageUniform(2, _jfaTexA)
                }, _jfaInitShader, 0));

                _jfaStepShader = CompileCompute("dof_jfa_step", JfaStepGlsl);
                _jfaStepPipe = Track(_rd.ComputePipelineCreate(_jfaStepShader));
                _jfaStepUsetAB = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    ImageUniform(0, _jfaTexA),
                    ImageUniform(1, _jfaTexB)
                }, _jfaStepShader, 0));
                _jfaStepUsetBA = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    ImageUniform(0, _jfaTexB),
                    ImageUniform(1, _jfaTexA)
                }, _jfaStepShader, 0));

                _blurHShader = CompileCompute("dof_jfa_blur_h", JfaBlurHGlsl);
                _blurHPipe = Track(_rd.ComputePipelineCreate(_blurHShader));
                // 6-pass 1+JFA (1, 16, 8, 4, 2, 1): A->B->A->B->A->B->A => final seeds live in _jfaTexA!
                _blurHUset = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    ImageUniform(0, _jfaTexA),
                    ImageUniform(1, _blurHTex)
                }, _blurHShader, 0));

                _blurVShader = CompileCompute("dof_jfa_blur_v", JfaBlurVPackGlsl);
                _blurVPipe = Track(_rd.ComputePipelineCreate(_blurVShader));
                _blurVUset = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                {
                    ImageUniform(0, _blurHTex),
                    ImageUniform(1, _frontierMipViews[0])
                }, _blurVShader, 0));

                _mipR8Shader = CompileCompute("dof_mipmap_r8", MipmapR8Glsl);
                _mipR8Pipe = Track(_rd.ComputePipelineCreate(_mipR8Shader));
                _frontierMipUsets = new Rid[_frontierMips - 1];
                for (var m = 0; m < _frontierMips - 1; m++)
                {
                    _frontierMipUsets[m] = Track(_rd.UniformSetCreate(new Godot.Collections.Array<RDUniform>
                    {
                        ImageUniform(0, _frontierMipViews[m]),
                        ImageUniform(1, _frontierMipViews[m + 1])
                    }, _mipR8Shader, 0));
                }
            }

            // Initial dispatch so textures are valid on frame 0!
            DispatchPlateOnRenderThread(hasOverride, shades);
            if (_hasFrontier)
                DispatchFrontierOnRenderThread(hasOverride);

            PlateTexture.TextureRdRid = _plateTex;
            if (_hasFrontier) FrontierTexture.TextureRdRid = _frontierTex;
            return true;
        }
        catch (Exception ex)
        {
            _failureReason = ex.Message;
            GD.PushWarning($"[dof] GPU map compute init unavailable ({ex.Message}); falling back to incremental Rust SIMD.");
            return false;
        }
    }

    private void DispatchPlateOnRenderThread(bool hasOverride, MapPlateShades shades)
    {
        Span<byte> pc = stackalloc byte[32];
        WriteUInt(pc, 0, (uint)_plateW);
        WriteUInt(pc, 4, (uint)_plateH);
        WriteUInt(pc, 8, hasOverride ? 1u : 0u);
        WriteUInt(pc, 12, 0u);
        WriteFloat(pc, 16, shades.Country);
        WriteFloat(pc, 20, shades.Region);
        WriteFloat(pc, 24, shades.Inland);
        WriteFloat(pc, 28, 0f);

        var cl = _rd.ComputeListBegin();
        _rd.ComputeListBindComputePipeline(cl, _platePipe);
        _rd.ComputeListBindUniformSet(cl, _plateUset, 0);
        _rd.ComputeListSetPushConstant(cl, pc, 32);
        _rd.ComputeListDispatch(cl, Groups(_plateW, 16), Groups(_plateH, 16), 1);

        var pw = _plateW;
        var ph = _plateH;
        Span<byte> mipPc = stackalloc byte[16];
        _rd.ComputeListBindComputePipeline(cl, _mipRgbaPipe);
        for (var m = 0; m < _plateMipUsets.Length; m++)
        {
            _rd.ComputeListAddBarrier(cl);
            var nw = Math.Max(1, pw >> 1);
            var nh = Math.Max(1, ph >> 1);
            WriteUInt(mipPc, 0, (uint)pw);
            WriteUInt(mipPc, 4, (uint)ph);
            WriteUInt(mipPc, 8, (uint)nw);
            WriteUInt(mipPc, 12, (uint)nh);
            _rd.ComputeListBindUniformSet(cl, _plateMipUsets[m], 0);
            _rd.ComputeListSetPushConstant(cl, mipPc, 16);
            _rd.ComputeListDispatch(cl, Groups(nw, 16), Groups(nh, 16), 1);
            pw = nw;
            ph = nh;
        }
        _rd.ComputeListEnd();
    }

    private void DispatchFrontierOnRenderThread(bool hasOverride)
    {
        var gx = Groups(_frontierW, 16);
        var gy = Groups(_frontierH, 16);
        Span<byte> pc16 = stackalloc byte[16];

        var cl = _rd.ComputeListBegin();

        // 1. Seed frontier cells into _jfaTexA
        WriteUInt(pc16, 0, (uint)_frontierW);
        WriteUInt(pc16, 4, (uint)_frontierH);
        WriteUInt(pc16, 8, hasOverride ? 1u : 0u);
        WriteUInt(pc16, 12, 0u);
        _rd.ComputeListBindComputePipeline(cl, _jfaInitPipe);
        _rd.ComputeListBindUniformSet(cl, _jfaInitUset, 0);
        _rd.ComputeListSetPushConstant(cl, pc16, 16);
        _rd.ComputeListDispatch(cl, gx, gy, 1);

        // 2. 6-pass 1+JFA (steps 1, 16, 8, 4, 2, 1 => range 31 >= Cap, eliminates 1-pixel Voronoi errors; ends in _jfaTexA)
        _rd.ComputeListBindComputePipeline(cl, _jfaStepPipe);
        ReadOnlySpan<int> steps = stackalloc int[] { 1, 16, 8, 4, 2, 1 };
        for (var i = 0; i < steps.Length; i++)
        {
            _rd.ComputeListAddBarrier(cl);
            WriteUInt(pc16, 0, (uint)_frontierW);
            WriteUInt(pc16, 4, (uint)_frontierH);
            WriteInt(pc16, 8, steps[i]);
            WriteUInt(pc16, 12, 0u);
            _rd.ComputeListBindUniformSet(cl, (i & 1) == 0 ? _jfaStepUsetAB : _jfaStepUsetBA, 0);
            _rd.ComputeListSetPushConstant(cl, pc16, 16);
            _rd.ComputeListDispatch(cl, gx, gy, 1);
        }

        // 3. Fused distance eval + horizontal 7-tap Gaussian (sigma=1.0) into _blurHTex
        _rd.ComputeListAddBarrier(cl);
        WriteUInt(pc16, 0, (uint)_frontierW);
        WriteUInt(pc16, 4, (uint)_frontierH);
        WriteFloat(pc16, 8, FrontierField.Cap);
        WriteUInt(pc16, 12, 0u);
        _rd.ComputeListBindComputePipeline(cl, _blurHPipe);
        _rd.ComputeListBindUniformSet(cl, _blurHUset, 0);
        _rd.ComputeListSetPushConstant(cl, pc16, 16);
        _rd.ComputeListDispatch(cl, gx, gy, 1);

        // 4. Vertical 7-tap Gaussian + R8 pack into _frontierMipViews[0]
        _rd.ComputeListAddBarrier(cl);
        _rd.ComputeListBindComputePipeline(cl, _blurVPipe);
        _rd.ComputeListBindUniformSet(cl, _blurVUset, 0);
        _rd.ComputeListSetPushConstant(cl, pc16, 16);
        _rd.ComputeListDispatch(cl, gx, gy, 1);

        // 5. Compute mipmap pyramid levels 1..N directly in VRAM
        var pw = _frontierW;
        var ph = _frontierH;
        _rd.ComputeListBindComputePipeline(cl, _mipR8Pipe);
        for (var m = 0; m < _frontierMipUsets.Length; m++)
        {
            _rd.ComputeListAddBarrier(cl);
            var nw = Math.Max(1, pw >> 1);
            var nh = Math.Max(1, ph >> 1);
            WriteUInt(pc16, 0, (uint)pw);
            WriteUInt(pc16, 4, (uint)ph);
            WriteUInt(pc16, 8, (uint)nw);
            WriteUInt(pc16, 12, (uint)nh);
            _rd.ComputeListBindUniformSet(cl, _frontierMipUsets[m], 0);
            _rd.ComputeListSetPushConstant(cl, pc16, 16);
            _rd.ComputeListDispatch(cl, Groups(nw, 16), Groups(nh, 16), 1);
            pw = nw;
            ph = nh;
        }
        _rd.ComputeListEnd();
    }

    private Rid CompileCompute(string name, string glsl)
    {
        var src = new RDShaderSource
        {
            Language = RenderingDevice.ShaderLanguage.Glsl,
            SourceCompute = glsl
        };
        var spirv = _rd.ShaderCompileSpirVFromSource(src, allowCache: true);
        if (spirv is null || !string.IsNullOrEmpty(spirv.CompileErrorCompute) || spirv.BytecodeCompute is not { Length: > 0 })
            throw new InvalidOperationException($"{name} GLSL compile error: {spirv?.CompileErrorCompute ?? "empty spirv"}");
        var shader = Track(_rd.ShaderCreateFromSpirV(spirv, name));
        if (!shader.IsValid)
            throw new InvalidOperationException($"{name} ShaderCreateFromSpirV returned invalid RID");
        return shader;
    }

    private Rid[] CreateMipViews(Rid baseTex, int mips)
    {
        var views = new Rid[mips];
        for (var m = 0; m < mips; m++)
        {
            views[m] = Track(_rd.TextureCreateSharedFromSlice(
                new RDTextureView(), baseTex, layer: 0, mipmap: (uint)m, mipmaps: 1,
                RenderingDevice.TextureSliceType.Slice2D));
        }
        return views;
    }

    private Rid Track(Rid rid)
    {
        if (rid.IsValid) _ownedRids.Add(rid);
        return rid;
    }

    private static RDUniform BufferUniform(int binding, Rid buffer)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.StorageBuffer, Binding = binding };
        u.AddId(buffer);
        return u;
    }

    private static RDUniform ImageUniform(int binding, Rid image)
    {
        var u = new RDUniform { UniformType = RenderingDevice.UniformType.Image, Binding = binding };
        u.AddId(image);
        return u;
    }

    private static uint Groups(int dim, int size) => (uint)((dim + size - 1) / size);

    private static int MipCount(int w, int h)
    {
        var m = 1;
        while (w > 1 || h > 1) { w = Math.Max(1, w >> 1); h = Math.Max(1, h >> 1); m++; }
        return m;
    }

    private static void WriteUInt(Span<byte> dst, int offset, uint v) => MemoryMarshal.Write(dst.Slice(offset, 4), in v);
    private static void WriteInt(Span<byte> dst, int offset, int v) => MemoryMarshal.Write(dst.Slice(offset, 4), in v);
    private static void WriteFloat(Span<byte> dst, int offset, float v) => MemoryMarshal.Write(dst.Slice(offset, 4), in v);

    private static byte[] PackHolderProvinceBytes(ushort[] holders, ushort[] provs, int cells)
    {
        var bytes = new byte[cells * 4];
        var dst = MemoryMarshal.Cast<byte, uint>(bytes.AsSpan());
        for (var i = 0; i < cells; i++)
            dst[i] = holders[i] | ((uint)provs[i] << 16);
        return bytes;
    }

    private static byte[] PadBytesTo4(byte[] src, int count)
    {
        var padded = (count + 3) & ~3;
        var dst = new byte[padded];
        Buffer.BlockCopy(src, 0, dst, 0, Math.Min(count, src.Length));
        return dst;
    }

    private static bool FillProvLut(ushort[] owners, ushort[] authoredOwners, byte[] dstBytes)
    {
        Array.Clear(dstBytes);
        if (owners is null || owners.Length == 0) return false;
        var lut = MemoryMarshal.Cast<byte, ushort>(dstBytes.AsSpan());
        var limit = Math.Min(owners.Length, 65536);
        var hasOverride = false;
        for (var p = 1; p < limit; p++)
        {
            var owner = owners[p];
            var same = authoredOwners is not null && p < authoredOwners.Length && authoredOwners[p] == owner;
            if (!same)
            {
                lut[p] = owner == 0 ? (ushort)65535 : owner;
                hasOverride = true;
            }
        }
        return hasOverride;
    }

    private static void FillPalette(IReadOnlyDictionary<int, uint> colors, byte[] dstBytes)
    {
        var pal = MemoryMarshal.Cast<byte, uint>(dstBytes.AsSpan());
        pal.Fill(MapBorders.UnownedColour);
        if (colors is not null)
        {
            foreach (var (mapIndex, packed) in colors)
                if (mapIndex > 0 && mapIndex < MapBorders.Unowned && packed != 0)
                    pal[mapIndex] = packed;
        }
    }

    // -------------------------------------------------------------------------
    // Embedded GLSL 450 Compute Shaders
    // -------------------------------------------------------------------------

    private const string PlateCompGlsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) readonly buffer StaticCells { uint packed_hp[]; };
layout(set = 0, binding = 1, std430) readonly buffer StaticGeo { uint packed_geo[]; };
layout(set = 0, binding = 2, std430) readonly buffer ProvLut { uint prov_lut[]; };
layout(set = 0, binding = 3, std430) readonly buffer Palette { uint palette_rgba[]; };
layout(set = 0, binding = 4, rgba8) writeonly uniform image2D out_plate;

layout(push_constant) uniform Params {
    uint width;
    uint height;
    uint has_override;
    uint pad0;
    float country_shade;
    float region_shade;
    float inland_shade;
    float pad1;
} params;

uint effective_holder(uint idx) {
    uint hp = packed_hp[idx];
    uint ah = hp & 0xFFFFu;
    if (ah == 0u || params.has_override == 0u) return ah;
    uint prov = hp >> 16;
    uint ov = (prov_lut[prov >> 1] >> ((prov & 1u) * 16u)) & 0xFFFFu;
    return ov == 0u ? ah : ov;
}

void main() {
    uint x = gl_GlobalInvocationID.x;
    uint y = gl_GlobalInvocationID.y;
    if (x >= params.width || y >= params.height) return;

    uint row = y * params.width;
    uint cell = row + x;
    uint holder = effective_holder(cell);
    if (holder == 0u) {
        imageStore(out_plate, ivec2(int(x), int(y)), vec4(0.0));
        return;
    }

    uint geo = (packed_geo[cell >> 2] >> ((cell & 3u) * 8u)) & 0xFFu;
    float shade = params.inland_shade;
    if ((geo & 1u) != 0u) {
        shade = params.country_shade;
    } else {
        uint east_x = (x + 1u == params.width) ? 0u : (x + 1u);
        uint west_x = (x == 0u) ? (params.width - 1u) : (x - 1u);
        uint east = effective_holder(row + east_x);
        uint west = effective_holder(row + west_x);
        bool foreign = (east != 0u && east != holder) || (west != 0u && west != holder);
        if (!foreign && y > 0u) {
            uint north = effective_holder(cell - params.width);
            foreign = (north != 0u && north != holder);
        }
        if (!foreign && y + 1u < params.height) {
            uint south = effective_holder(cell + params.width);
            foreign = (south != 0u && south != holder);
        }
        if (foreign) shade = params.country_shade;
        else if ((geo & 2u) != 0u) shade = params.region_shade;
    }

    uint packed = palette_rgba[holder];
    float r = floor(float((packed >> 24) & 0xFFu) * shade) / 255.0;
    float g = floor(float((packed >> 16) & 0xFFu) * shade) / 255.0;
    float b = floor(float((packed >> 8) & 0xFFu) * shade) / 255.0;
    imageStore(out_plate, ivec2(int(x), int(y)), vec4(r, g, b, 1.0));
}
""";

    private const string MipmapRgba8Glsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, rgba8) readonly uniform image2D src_mip;
layout(set = 0, binding = 1, rgba8) writeonly uniform image2D dst_mip;

layout(push_constant) uniform Params {
    uint src_w;
    uint src_h;
    uint dst_w;
    uint dst_h;
} params;

void main() {
    uint dx = gl_GlobalInvocationID.x;
    uint dy = gl_GlobalInvocationID.y;
    if (dx >= params.dst_w || dy >= params.dst_h) return;

    int x0 = min(int(dx * 2u), int(params.src_w) - 1);
    int x1 = min(x0 + 1, int(params.src_w) - 1);
    int y0 = min(int(dy * 2u), int(params.src_h) - 1);
    int y1 = min(y0 + 1, int(params.src_h) - 1);
    vec4 avg = 0.25 * (
        imageLoad(src_mip, ivec2(x0, y0)) +
        imageLoad(src_mip, ivec2(x1, y0)) +
        imageLoad(src_mip, ivec2(x0, y1)) +
        imageLoad(src_mip, ivec2(x1, y1)));
    imageStore(dst_mip, ivec2(int(dx), int(dy)), avg);
}
""";

    private const string JfaInitGlsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, std430) readonly buffer FrontierCells { uint packed_hp[]; };
layout(set = 0, binding = 1, std430) readonly buffer ProvLut { uint prov_lut[]; };
layout(set = 0, binding = 2, r32ui) writeonly uniform uimage2D out_seed;

layout(push_constant) uniform Params {
    uint width;
    uint height;
    uint has_override;
    uint pad0;
} params;

uint effective_holder(uint idx) {
    uint hp = packed_hp[idx];
    uint ah = hp & 0xFFFFu;
    if (ah == 0u || params.has_override == 0u) return ah;
    uint prov = hp >> 16;
    uint ov = (prov_lut[prov >> 1] >> ((prov & 1u) * 16u)) & 0xFFFFu;
    return ov == 0u ? ah : ov;
}

void main() {
    uint x = gl_GlobalInvocationID.x;
    uint y = gl_GlobalInvocationID.y;
    if (x >= params.width || y >= params.height) return;

    uint row = y * params.width;
    uint cell = row + x;
    uint here = effective_holder(cell);
    uint east_x = (x + 1u == params.width) ? 0u : (x + 1u);
    uint west_x = (x == 0u) ? (params.width - 1u) : (x - 1u);
    bool frontier =
        effective_holder(row + east_x) != here ||
        effective_holder(row + west_x) != here ||
        (y > 0u && effective_holder(cell - params.width) != here) ||
        (y + 1u < params.height && effective_holder(cell + params.width) != here);
    uint seed = frontier ? (x | (y << 16)) : 0xFFFFFFFFu;
    imageStore(out_seed, ivec2(int(x), int(y)), uvec4(seed, 0u, 0u, 0u));
}
""";

    private const string JfaStepGlsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, r32ui) readonly uniform uimage2D in_seed;
layout(set = 0, binding = 1, r32ui) writeonly uniform uimage2D out_seed;

layout(push_constant) uniform Params {
    uint width;
    uint height;
    int step_size;
    uint pad0;
} params;

void main() {
    int x = int(gl_GlobalInvocationID.x);
    int y = int(gl_GlobalInvocationID.y);
    int w = int(params.width);
    int h = int(params.height);
    if (x >= w || y >= h) return;

    uint best_seed = 0xFFFFFFFFu;
    float best_d2 = 1e30;
    int st = params.step_size;

    for (int oy = -1; oy <= 1; oy++) {
        int ny = y + oy * st;
        if (ny < 0 || ny >= h) continue;
        for (int ox = -1; ox <= 1; ox++) {
            int nx = x + ox * st;
            if (nx < 0) nx += w;
            else if (nx >= w) nx -= w;
            uint s = imageLoad(in_seed, ivec2(nx, ny)).r;
            if (s == 0xFFFFFFFFu) continue;
            int sx = int(s & 0xFFFFu);
            int sy = int(s >> 16);
            int dx = abs(sx - x);
            dx = min(dx, w - dx);
            int dy = sy - y;
            float d2 = float(dx * dx + dy * dy);
            if (d2 < best_d2) {
                best_d2 = d2;
                best_seed = s;
            }
        }
    }
    imageStore(out_seed, ivec2(x, y), uvec4(best_seed, 0u, 0u, 0u));
}
""";

    private const string JfaBlurHGlsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, r32ui) readonly uniform uimage2D in_seed;
layout(set = 0, binding = 1, r16f) writeonly uniform image2D out_blur_h;

layout(push_constant) uniform Params {
    uint width;
    uint height;
    float cap;
    uint pad0;
} params;

// Normalized 7-tap Gaussian weights for sigma = 1.0 (radius = 3)
const float W[7] = float[7](
    0.004431848, 0.054005583, 0.242036229, 0.399052680,
    0.242036229, 0.054005583, 0.004431848
);

float seed_distance(int x, int y, int w, float cap) {
    uint s = imageLoad(in_seed, ivec2(x, y)).r;
    if (s == 0xFFFFFFFFu) return cap;
    int sx = int(s & 0xFFFFu);
    int sy = int(s >> 16);
    int dx = abs(sx - x);
    dx = min(dx, w - dx);
    int dy = sy - y;
    return min(cap, sqrt(float(dx * dx + dy * dy)) + 0.5);
}

void main() {
    int x = int(gl_GlobalInvocationID.x);
    int y = int(gl_GlobalInvocationID.y);
    int w = int(params.width);
    int h = int(params.height);
    if (x >= w || y >= h) return;

    float sum = 0.0;
    for (int k = -3; k <= 3; k++) {
        int col = x + k;
        if (col < 0) col += w;
        else if (col >= w) col -= w;
        sum += seed_distance(col, y, w, params.cap) * W[k + 3];
    }
    imageStore(out_blur_h, ivec2(x, y), vec4(sum, 0.0, 0.0, 0.0));
}
""";

    private const string JfaBlurVPackGlsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, r16f) readonly uniform image2D in_blur_h;
layout(set = 0, binding = 1, r8) writeonly uniform image2D out_frontier;

layout(push_constant) uniform Params {
    uint width;
    uint height;
    float cap;
    uint pad0;
} params;

const float W[7] = float[7](
    0.004431848, 0.054005583, 0.242036229, 0.399052680,
    0.242036229, 0.054005583, 0.004431848
);

void main() {
    int x = int(gl_GlobalInvocationID.x);
    int y = int(gl_GlobalInvocationID.y);
    int w = int(params.width);
    int h = int(params.height);
    if (x >= w || y >= h) return;

    float sum = 0.0;
    for (int k = -3; k <= 3; k++) {
        int line = clamp(y + k, 0, h - 1);
        sum += imageLoad(in_blur_h, ivec2(x, line)).r * W[k + 3];
    }
    float norm = clamp(sum / params.cap, 0.0, 1.0);
    imageStore(out_frontier, ivec2(x, y), vec4(norm, 0.0, 0.0, 0.0));
}
""";

    private const string MipmapR8Glsl = """
#version 450
layout(local_size_x = 16, local_size_y = 16, local_size_z = 1) in;

layout(set = 0, binding = 0, r8) readonly uniform image2D src_mip;
layout(set = 0, binding = 1, r8) writeonly uniform image2D dst_mip;

layout(push_constant) uniform Params {
    uint src_w;
    uint src_h;
    uint dst_w;
    uint dst_h;
} params;

void main() {
    uint dx = gl_GlobalInvocationID.x;
    uint dy = gl_GlobalInvocationID.y;
    if (dx >= params.dst_w || dy >= params.dst_h) return;

    int x0 = min(int(dx * 2u), int(params.src_w) - 1);
    int x1 = min(x0 + 1, int(params.src_w) - 1);
    int y0 = min(int(dy * 2u), int(params.src_h) - 1);
    int y1 = min(y0 + 1, int(params.src_h) - 1);
    float avg = 0.25 * (
        imageLoad(src_mip, ivec2(x0, y0)).r +
        imageLoad(src_mip, ivec2(x1, y0)).r +
        imageLoad(src_mip, ivec2(x0, y1)).r +
        imageLoad(src_mip, ivec2(x1, y1)).r);
    imageStore(dst_mip, ivec2(int(dx), int(dy)), vec4(avg, 0.0, 0.0, 0.0));
}
""";
}
