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
    private static uint _capabilities;
    private static string _loadedPath = string.Empty;
    private static string _diagnostic = "not initialized";

    /// <summary>Capability bitflags returned by <c>dof_kernels_capabilities()</c>.</summary>
    public const uint CapAbiV1Base = 1u << 0;
    public const uint CapDiplomacy = 1u << 1;
    public const uint CapMarket = 1u << 2;
    public const uint CapDistance = 1u << 3;
    public const uint CapAvx2 = 1u << 4;
    public const uint CapNeon = 1u << 5;
    public const uint CapRenderPrep = 1u << 6;
    public const uint CapEconomyDemo = 1u << 7;
    public const uint CapIncrementalRaster = 1u << 8;
    public const uint CapWartimeGraph = 1u << 9;
    public const uint CapEconCombat = 1u << 10;

    /// <summary>Allows differential and fallback tests to force the pure-C# path cleanly in-process.</summary>
    public static volatile bool SimulateUnavailableForTests;

    /// <summary>
    /// Policy switches determined by end-to-end benchmarks + fingerprint parity.
    /// Individual kernels only default ON once proven strictly faster end-to-end AND bit-identical.
    /// Env overrides: <c>DOF_NATIVE_RASTER=0|1</c>, <c>DOF_NATIVE_DISTANCE_FIELD=0|1</c>,
    /// <c>DOF_NATIVE_SUPPLY=0|1</c>, <c>DOF_NATIVE_TRADE=0|1</c>,
    /// <c>DOF_NATIVE_DIPLOMACY=0|1</c>, <c>DOF_NATIVE_MARKET=0|1</c>, <c>DOF_NATIVE_DISTANCE=0|1</c>,
    /// <c>DOF_NATIVE_ECONOMY_DEMO=0|1</c>, <c>DOF_NATIVE_DEMO_AGGREGATE=0|1</c>,
    /// <c>DOF_NATIVE_INCREMENTAL_RASTER=0|1</c>, <c>DOF_NATIVE_WARTIME_GRAPH=0|1</c>,
    /// <c>DOF_NATIVE_POLITICAL_COHORTS=0|1</c>, <c>DOF_NATIVE_ECON_COMBAT=0|1</c>.
    /// </summary>
    public static bool EnableRasterDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_RASTER", defaultValue: true);
    public static bool EnableDistanceFieldDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_DISTANCE_FIELD", defaultValue: true);
    public static bool EnableSupplyDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_SUPPLY", defaultValue: OperatingSystem.IsAndroid() || OperatingSystem.IsIOS());
    public static bool EnableTradeDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_TRADE", defaultValue: OperatingSystem.IsAndroid() || OperatingSystem.IsIOS());
    public static bool EnableDiplomacyDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_DIPLOMACY", defaultValue: true);
    public static bool EnableMarketDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_MARKET", defaultValue: true);
    public static bool EnableDistanceDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_DISTANCE", defaultValue: true);
    public static bool EnableEconomyDemoDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_ECONOMY_DEMO", defaultValue: true);
    public static bool EnableDemoAggregateDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_DEMO_AGGREGATE", defaultValue: OperatingSystem.IsAndroid() || OperatingSystem.IsIOS());
    public static readonly bool EnableIncrementalRasterDefault = ReadEnvFlag("DOF_NATIVE_INCREMENTAL_RASTER", defaultValue: true);
    public static readonly bool EnableWartimeGraphDefault = ReadEnvFlag("DOF_NATIVE_WARTIME_GRAPH", defaultValue: true);
    public static readonly bool EnablePoliticalCohortsDefault = ReadEnvFlag("DOF_NATIVE_POLITICAL_COHORTS", defaultValue: true);
    public static bool EnableEconCombatDefault { get; set; } = ReadEnvFlag("DOF_NATIVE_ECON_COMBAT", defaultValue: true);

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

    public static uint Capabilities
    {
        get
        {
            if (SimulateUnavailableForTests) return 0;
            EnsureInitialized();
            return _capabilities;
        }
    }

    public static bool HasCapability(uint capMask)
    {
        if (SimulateUnavailableForTests) return false;
        EnsureInitialized();
        return _available && (_capabilities & capMask) == capMask;
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

    [LibraryImport(LibraryName)]
    [UnmanagedCallConv(CallConvs = new[] { typeof(CallConvCdecl) })]
    private static partial uint dof_kernels_capabilities();

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

                if (isMobileRuntime && !ReadEnvFlag("DOF_KERNELS_ALLOW_MOBILE", defaultValue: true))
                {
                    _available = false;
                    _diagnostic = "disabled on mobile via DOF_KERNELS_ALLOW_MOBILE=0 (using pure C# fallback)";
                    Volatile.Write(ref _initialized, 1);
                    return;
                }

                // Final attempt via default OS library search paths (covers APK lib/<abi>/libdof_kernels.so).
                var sysLibName = PlatformLibraryFileName();
                if (NativeLibrary.TryLoad(sysLibName, typeof(NativeKernelsLoader).Assembly, null, out var sysHandle))
                {
                    if (TryActivateLibraryHandle(sysHandle, sysLibName, viaOsSearch: true))
                        return;
                }

                if (!string.Equals(sysLibName, LibraryName, StringComparison.Ordinal) &&
                    NativeLibrary.TryLoad(LibraryName, typeof(NativeKernelsLoader).Assembly, null, out var shortHandle))
                {
                    if (TryActivateLibraryHandle(shortHandle, LibraryName, viaOsSearch: true))
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
            _capabilities = NativeLibrary.TryGetExport(handle, "dof_kernels_capabilities", out _)
                ? dof_kernels_capabilities()
                : CapAbiV1Base;
            _loadedPath = sourceLabel;
            _available = true;
            _diagnostic = viaOsSearch
                ? $"loaded abi={abi} simd={_simdLevel} caps=0x{_capabilities:X} via OS search"
                : $"loaded abi={abi} simd={_simdLevel} caps=0x{_capabilities:X} path={sourceLabel}";
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
