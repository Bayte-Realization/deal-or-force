// SPDX-License-Identifier: GPL-3.0-only
#nullable enable
using System;
using System.IO;
using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using System.Threading;

namespace DealOrForce.Simulation.Kernels;

/// <summary>
/// Discovers, validates (ABI v1), and binds optional native <c>dof_kernels</c> SIMD/Rust arenas.
/// Every caller silently falls back to bit-identical pure C# when the native library is absent,
/// disabled by environment/settings, or reports an incompatible ABI version.
/// </summary>
public static partial class NativeKernelsLoader
{
    internal const string LibraryName = "dof_kernels";
    internal const uint ExpectedAbiVersion = 1;

    private static readonly object InitGate = new();
    private static int _resolverRegistered;
    private static int _initialized;
    private static IntPtr _libraryHandle;
    private static bool _available;
    private static uint _abiVersion;
    private static uint _simdLevel;
    private static string _loadedPath = string.Empty;
    private static string _diagnostic = "not initialized";

    /// <summary>Allows differential and fallback tests to force the pure-C# path cleanly in-process.</summary>
    public static volatile bool SimulateUnavailableForTests;

    /// <summary>
    /// Policy switches determined by end-to-end benchmarks + fingerprint parity.
    /// Individual kernels only default ON once proven strictly faster end-to-end AND bit-identical.
    /// Env overrides: <c>DOF_NATIVE_RASTER=0|1</c>, <c>DOF_NATIVE_DISTANCE_FIELD=0|1</c>,
    /// <c>DOF_NATIVE_SUPPLY=0|1</c>, <c>DOF_NATIVE_TRADE=0|1</c>.
    /// </summary>
    public static bool EnableRasterDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_RASTER", defaultValue: true);
    public static bool EnableDistanceFieldDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_DISTANCE_FIELD", defaultValue: true);
    public static bool EnableSupplyDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_SUPPLY", defaultValue: false);
    public static bool EnableTradeDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_TRADE", defaultValue: false);

    public static bool IsAvailable
    {
        get
        {
            if (SimulateUnavailableForTests) return false;
            EnsureInitialized();
            return _available;
        }
    }

    public static uint AbiVersion
    {
        get { EnsureInitialized(); return _abiVersion; }
    }

    public static uint SimdLevel
    {
        get { EnsureInitialized(); return _simdLevel; }
    }

    public static string LoadedPath
    {
        get { EnsureInitialized(); return _loadedPath; }
    }

    public static string Diagnostic
    {
        get { EnsureInitialized(); return _diagnostic; }
    }

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial uint dof_kernels_abi_version();

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial uint dof_kernels_simd_level();

    internal static void EnsureInitialized()
    {
        if (Volatile.Read(ref _initialized) == 1) return;

        lock (InitGate)
        {
            if (Volatile.Read(ref _initialized) == 1) return;

            RegisterResolverOnce();

            if (IsDisabledByEnv())
            {
                _available = false;
                _diagnostic = "disabled by DOF_KERNELS_DISABLE";
                Volatile.Write(ref _initialized, 1);
                return;
            }

            try
            {
                var isMobileRuntime = OperatingSystem.IsAndroid() || OperatingSystem.IsIOS();
                foreach (var candidate in CandidateLibraryPaths())
                {
                    if (string.IsNullOrWhiteSpace(candidate) || !File.Exists(candidate)) continue;
                    if (!NativeLibrary.TryLoad(candidate, out var handle)) continue;

                    if (TryActivateLibraryHandle(handle, candidate))
                        return;
                    return;
                }

                if (isMobileRuntime && !ReadEnvFlag("DOF_KERNELS_ALLOW_MOBILE", defaultValue: false))
                {
                    _available = false;
                    _diagnostic = "disabled by default on mobile (using pure C# fallback)";
                    Volatile.Write(ref _initialized, 1);
                    return;
                }

                // Final attempt via default OS library search paths.
                var sysLibName = PlatformLibraryFileName();
                if (NativeLibrary.TryLoad(sysLibName, typeof(NativeKernelsLoader).Assembly, null, out var sysHandle))
                {
                    if (TryActivateLibraryHandle(sysHandle, sysLibName, viaOsSearch: true))
                        return;
                }

                _available = false;
                _diagnostic = "not found (using pure C# fallback)";
            }
            catch (Exception ex)
            {
                _available = false;
                _libraryHandle = IntPtr.Zero;
                _diagnostic = $"load error ({ex.GetType().Name}: {ex.Message}); using pure C# fallback";
            }

            Volatile.Write(ref _initialized, 1);
        }
    }

    private static bool TryActivateLibraryHandle(IntPtr handle, string sourceLabel, bool viaOsSearch = false)
    {
        if (handle == IntPtr.Zero) return false;
        _libraryHandle = handle;
        try
        {
            var abi = dof_kernels_abi_version();
            if (abi != ExpectedAbiVersion)
            {
                NativeLibrary.Free(handle);
                _libraryHandle = IntPtr.Zero;
                _abiVersion = abi;
                _available = false;
                _diagnostic = $"abi mismatch (got {abi}, expected {ExpectedAbiVersion}) at {sourceLabel}";
                Volatile.Write(ref _initialized, 1);
                return false;
            }

            _abiVersion = abi;
            _simdLevel = dof_kernels_simd_level();
            _loadedPath = sourceLabel;
            _available = true;
            _diagnostic = viaOsSearch
                ? $"loaded abi={abi} simd={_simdLevel} via OS search"
                : $"loaded abi={abi} simd={_simdLevel} path={sourceLabel}";
            Volatile.Write(ref _initialized, 1);
            return true;
        }
        catch
        {
            _libraryHandle = IntPtr.Zero;
            try { NativeLibrary.Free(handle); } catch { }
            throw;
        }
    }

    private static void RegisterResolverOnce()
    {
        if (Interlocked.Exchange(ref _resolverRegistered, 1) != 0) return;
        try
        {
            NativeLibrary.SetDllImportResolver(typeof(NativeKernelsLoader).Assembly, ResolveDllImport);
        }
        catch (Exception)
        {
            // Another resolver is already registered or mobile AOT runtime forbids custom resolvers.
        }
    }

    private static IntPtr ResolveDllImport(string libraryName, Assembly assembly, DllImportSearchPath? searchPath)
    {
        if (!string.Equals(libraryName, LibraryName, StringComparison.Ordinal)) return IntPtr.Zero;
        return _libraryHandle;
    }

    private static string PlatformLibraryFileName()
    {
        if (OperatingSystem.IsWindows()) return "dof_kernels.dll";
        if (OperatingSystem.IsMacOS()) return "libdof_kernels.dylib";
        return "libdof_kernels.so";
    }

    private static string[] CandidateLibraryPaths()
    {
        var file = PlatformLibraryFileName();
        var explicitPath = Environment.GetEnvironmentVariable("DOF_KERNELS_PATH");
        var explicitDir = Environment.GetEnvironmentVariable("DOF_KERNELS_DIR");
        string? asmDir = null;
        try
        {
            var asmLocation = typeof(NativeKernelsLoader).Assembly.Location;
            asmDir = string.IsNullOrEmpty(asmLocation) ? null : Path.GetDirectoryName(asmLocation);
        }
        catch (Exception)
        {
            asmDir = null;
        }
        var baseDir = AppContext.BaseDirectory;

        return new[]
        {
            explicitPath ?? string.Empty,
            string.IsNullOrWhiteSpace(explicitDir) ? string.Empty : Path.Combine(explicitDir, file),
            string.IsNullOrWhiteSpace(baseDir) ? string.Empty : Path.Combine(baseDir, file),
            string.IsNullOrWhiteSpace(asmDir) ? string.Empty : Path.Combine(asmDir, file),
        };
    }

    private static bool IsDisabledByEnv()
    {
        var val = Environment.GetEnvironmentVariable("DOF_KERNELS_DISABLE");
        return string.Equals(val, "1", StringComparison.Ordinal) ||
               string.Equals(val, "true", StringComparison.OrdinalIgnoreCase);
    }

    private static bool ReadEnvFlag(string name, bool defaultValue)
    {
        var val = Environment.GetEnvironmentVariable(name);
        if (string.Equals(val, "1", StringComparison.Ordinal) ||
            string.Equals(val, "true", StringComparison.OrdinalIgnoreCase)) return true;
        if (string.Equals(val, "0", StringComparison.Ordinal) ||
            string.Equals(val, "false", StringComparison.OrdinalIgnoreCase)) return false;
        return defaultValue;
    }
}
