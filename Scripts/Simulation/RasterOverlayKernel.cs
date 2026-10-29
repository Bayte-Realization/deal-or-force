using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace DealOrForce.Simulation;

/// <summary>
/// Dense 256x256 RGBA province overlay packer with an optional Odin C-ABI v1 native kernel
/// and a zero-dictionary flat-array managed implementation.
/// </summary>
public static class RasterOverlayKernel
{
    private const int BufferSize = 256 * 256 * 4;
    private const int MaxPalette = 65536;

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate uint AbiVersionDelegate();

    [UnmanagedFunctionPointer(CallingConvention.Cdecl)]
    private delegate int PackOverlaysDelegate(
        uint provinceCount,
        IntPtr owners,
        IntPtr paintedOwners,
        IntPtr controllers,
        IntPtr paletteRgba,
        uint paletteLen,
        byte occupiedAlpha,
        ushort unownedPaletteIndex,
        uint fallbackRgba,
        IntPtr outControlRgba,
        IntPtr outOwnerRgba,
        IntPtr outOwnerIdsRgba,
        out int outOwnersMoved);

    private static readonly object Gate = new();
    private static IntPtr _library;
    private static PackOverlaysDelegate _nativePack;
    private static bool _probed;
    private static uint[] _flatPalette;

    public static string BackendStatus { get; private set; } = "managed-flat-soa";

    public static bool TryEnableNative(string directoryPath)
    {
        lock (Gate)
        {
            if (_nativePack is not null) return true;
            if (string.IsNullOrWhiteSpace(directoryPath) || !Directory.Exists(directoryPath)) return false;
            var fileName = OperatingSystem.IsWindows() ? "dof_raster_kernel.dll"
                : OperatingSystem.IsLinux() ? "libdof_raster_kernel.so"
                : OperatingSystem.IsMacOS() ? "libdof_raster_kernel.dylib" : null;
            if (fileName is null) return false;
            var fullPath = Path.GetFullPath(Path.Combine(directoryPath, fileName));
            if (!File.Exists(fullPath)) return false;
            try
            {
                var handle = NativeLibrary.Load(fullPath);
                var abi = Marshal.GetDelegateForFunctionPointer<AbiVersionDelegate>(
                    NativeLibrary.GetExport(handle, "dof_raster_abi_version"));
                if (abi() != 1u)
                {
                    NativeLibrary.Free(handle);
                    return false;
                }
                _nativePack = Marshal.GetDelegateForFunctionPointer<PackOverlaysDelegate>(
                    NativeLibrary.GetExport(handle, "dof_pack_province_overlays_v1"));
                _library = handle;
                BackendStatus = "odin-c-abi-v1";
                return true;
            }
            catch
            {
                return false;
            }
        }
    }

    public static bool PackProvinceOverlays(
        int provinceCount,
        ushort[] owners,
        ushort[] paintedOwners,
        ushort[] controllers,
        Dictionary<int, uint> colorByMapIndex,
        byte occupiedAlpha,
        ushort unownedPaletteIndex,
        uint fallbackRgba,
        byte[] controlPixels,
        byte[] ownerPixels,
        byte[] ownerIdPixels)
    {
        if (owners is null || controllers is null || controlPixels is null ||
            ownerPixels is null || ownerIdPixels is null) return false;

        var palette = _flatPalette ??= new uint[MaxPalette];
        Array.Clear(palette, 0, palette.Length);
        if (colorByMapIndex is not null)
        {
            foreach (var (mapIndex, packed) in colorByMapIndex)
                if ((uint)mapIndex < MaxPalette) palette[mapIndex] = packed;
        }

        if (!_probed)
        {
            _probed = true;
            var envDir = Environment.GetEnvironmentVariable("DOF_ODIN_RASTER_KERNEL_DIR");
            if (!string.IsNullOrWhiteSpace(envDir)) TryEnableNative(envDir);
        }

        var count = Math.Min(provinceCount, Math.Min(owners.Length - 1, controllers.Length - 1));
        if (count <= 0) return false;

        if (_nativePack is not null && paintedOwners is not null && paintedOwners.Length > count)
        {
            var pins = new GCHandle[7];
            try
            {
                pins[0] = GCHandle.Alloc(owners, GCHandleType.Pinned);
                pins[1] = GCHandle.Alloc(paintedOwners, GCHandleType.Pinned);
                pins[2] = GCHandle.Alloc(controllers, GCHandleType.Pinned);
                pins[3] = GCHandle.Alloc(palette, GCHandleType.Pinned);
                pins[4] = GCHandle.Alloc(controlPixels, GCHandleType.Pinned);
                pins[5] = GCHandle.Alloc(ownerPixels, GCHandleType.Pinned);
                pins[6] = GCHandle.Alloc(ownerIdPixels, GCHandleType.Pinned);

                var status = _nativePack(
                    (uint)count,
                    pins[0].AddrOfPinnedObject(),
                    pins[1].AddrOfPinnedObject(),
                    pins[2].AddrOfPinnedObject(),
                    pins[3].AddrOfPinnedObject(),
                    MaxPalette,
                    occupiedAlpha,
                    unownedPaletteIndex,
                    fallbackRgba,
                    pins[4].AddrOfPinnedObject(),
                    pins[5].AddrOfPinnedObject(),
                    pins[6].AddrOfPinnedObject(),
                    out var nativeMoved);
                if (status == 0) return nativeMoved != 0;
            }
            catch
            {
                _nativePack = null;
                BackendStatus = "managed-flat-soa (native fallback)";
            }
            finally
            {
                for (var i = 0; i < pins.Length; i++)
                    if (pins[i].IsAllocated) pins[i].Free();
            }
        }

        Array.Clear(controlPixels, 0, Math.Min(BufferSize, controlPixels.Length));
        Array.Clear(ownerPixels, 0, Math.Min(BufferSize, ownerPixels.Length));
        Array.Clear(ownerIdPixels, 0, Math.Min(BufferSize, ownerIdPixels.Length));

        var ownersMoved = false;
        for (var province = 1; province <= count; province++)
        {
            var owner = owners[province];
            var write = province * 4;
            if (paintedOwners is not null && province < paintedOwners.Length && paintedOwners[province] != owner)
            {
                var ownerPacked = palette[owner];
                if (ownerPacked == 0) ownerPacked = fallbackRgba;
                ownerPixels[write] = (byte)((ownerPacked >> 24) & 0xFF);
                ownerPixels[write + 1] = (byte)((ownerPacked >> 16) & 0xFF);
                ownerPixels[write + 2] = (byte)((ownerPacked >> 8) & 0xFF);
                ownerPixels[write + 3] = 255;

                var drawnOwner = owner == 0 ? unownedPaletteIndex : owner;
                ownerIdPixels[write] = (byte)(drawnOwner & 0xFF);
                ownerIdPixels[write + 1] = (byte)((drawnOwner >> 8) & 0xFF);
                ownerIdPixels[write + 2] = 255;
                ownerIdPixels[write + 3] = 255;
                ownersMoved = true;
            }

            var controller = controllers[province];
            if (controller == owner) continue;
            var packed = palette[controller];
            if (packed == 0) packed = fallbackRgba;
            controlPixels[write] = (byte)((packed >> 24) & 0xFF);
            controlPixels[write + 1] = (byte)((packed >> 16) & 0xFF);
            controlPixels[write + 2] = (byte)((packed >> 8) & 0xFF);
            controlPixels[write + 3] = occupiedAlpha;
        }
        return ownersMoved;
    }
}
