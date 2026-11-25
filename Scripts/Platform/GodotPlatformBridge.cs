using System;
using System.Globalization;
using Godot;

namespace DealOrForce.Platform;

/// <summary>
/// Thin Godot runtime adapter that samples <see cref="OS"/>, <see cref="DisplayServer"/>,
/// and <see cref="RenderingServer"/> once to build and cache <see cref="PlatformProfile.Current"/>,
/// plus Android-safe guards for desktop-only OS calls.
/// </summary>
public static class GodotPlatformBridge
{
    private static bool _initialized;

    /// <summary>Ensure <see cref="PlatformProfile.Current"/> reflects the running device.</summary>
    public static PlatformProfile EnsureInitialized(float logicalViewportHeight = 1080f)
    {
        if (_initialized) return PlatformProfile.Current;
        return Refresh(logicalViewportHeight);
    }

    /// <summary>Re-query display/safe-area metrics (call on startup and on window resize / rotation).</summary>
    public static PlatformProfile Refresh(float logicalViewportHeight = 1080f)
    {
        var osName = OS.GetName();
        var hasMobile = OS.HasFeature("mobile") || OS.HasFeature("android") || OS.HasFeature("ios");
        var hasTouch = DisplayServer.IsTouchscreenAvailable();

        var screenSize = DisplayServer.ScreenGetSize();
        if (screenSize.X <= 0 || screenSize.Y <= 0)
            screenSize = DisplayServer.WindowGetSize();

        var dpi = DisplayServer.ScreenGetDpi();
        var safeRect = DisplayServer.GetDisplaySafeArea();
        var safeInsets = SafeAreaInsets.FromRects(
            screenSize.X,
            screenSize.Y,
            safeRect.Position.X,
            safeRect.Position.Y,
            safeRect.Size.X,
            safeRect.Size.Y);

        var adapter = RenderingServer.GetVideoAdapterName();
        var method = RenderingServer.GetCurrentRenderingMethod();
        var lowEndGpu = hasMobile && PlatformProfile.IsLowEndMobileAdapter(adapter, method);
        var memoryMb = TryQueryPhysicalMemoryMb();

        var snapshot = new PlatformSnapshot(
            OsName: osName,
            HasMobileFeature: hasMobile,
            HasTouchscreen: hasTouch,
            ScreenWidthPx: Math.Max(1, screenSize.X),
            ScreenHeightPx: Math.Max(1, screenSize.Y),
            ScreenDpi: dpi > 0 ? dpi : (hasMobile ? 320f : 96f),
            SafeAreaRawPx: safeInsets,
            SystemMemoryMb: memoryMb,
            IsLowEndGpu: lowEndGpu,
            ViewportHeightLogical: logicalViewportHeight > 0f ? logicalViewportHeight : 1080f);

        var profile = PlatformProfile.Evaluate(snapshot);
        PlatformProfile.SetCurrent(profile);
        DealOrForce.Simulation.Loc.TextAdapter = profile.IsTouchPrimary
            ? static (key, text) => MobileTextAdapter.Adapt(
                text,
                isTouchPrimary: true,
                localizationKey: key,
                overrideLookup: static overrideKey => DealOrForce.Simulation.Loc.RawText(overrideKey))
            : null;
        _initialized = true;
        return profile;
    }

    /// <summary>
    /// Android-safe wrapper around <see cref="OS.ShellOpen"/> for local folders.
    /// Returns <c>false</c> cleanly on mobile platforms where desktop file managers are unsupported.
    /// </summary>
    public static bool TryOpenLocalFolderSafely(string directoryPath)
    {
        var profile = EnsureInitialized();
        if (!profile.IsDesktopFileDialogSupported || string.IsNullOrWhiteSpace(directoryPath))
            return false;

        try
        {
            return OS.ShellOpen(directoryPath) == Error.Ok;
        }
        catch (Exception ex)
        {
            GD.PushWarning($"[platform] ShellOpen failed for '{directoryPath}': {ex.Message}");
            return false;
        }
    }

    /// <summary>
    /// Guaranteed writable data root: on Android/iOS always uses <c>user://</c> inside the
    /// app sandbox (safe under Android 11+ Scoped Storage); on Desktop uses <see cref="GamePaths.Root"/>.
    /// </summary>
    public static string ResolveWritableDataRoot()
    {
        var profile = EnsureInitialized();
        if (profile.IsMobile)
            return ProjectSettings.GlobalizePath("user://");
        return GamePaths.Root;
    }

    /// <summary>One-line startup diagnostic summary suitable for logs and bug reports.</summary>
    public static string DescribeRuntimeSummary()
    {
        var p = EnsureInitialized();
        return string.Create(CultureInfo.InvariantCulture,
            $"[platform] form={p.FormFactor} android={p.IsAndroid} touch={p.IsTouchPrimary} " +
            $"diag={p.ScreenDiagonalInches:0.0}in ui_scale={p.UiScaleFactor:0.00} " +
            $"min_target={p.MinimumTouchTargetPx:0.0}px tier={p.GraphicsQualityTier} " +
            $"safe=({p.SafeArea.Left:0},{p.SafeArea.Top:0},{p.SafeArea.Right:0},{p.SafeArea.Bottom:0}) " +
            $"steam={p.IsSteamSupported} file_dialog={p.IsDesktopFileDialogSupported}");
    }

    private static int TryQueryPhysicalMemoryMb()
    {
        try
        {
            var info = OS.GetMemoryInfo();
            if (info is not null && info.ContainsKey("physical"))
            {
                var bytes = (long)info["physical"];
                if (bytes > 0) return (int)Math.Clamp(bytes / (1024L * 1024L), 0L, int.MaxValue);
            }
        }
        catch
        {
            // Older or restricted sandboxes may omit physical RAM reporting.
        }

        // Godot Android's FileAccess procfs reader sees size == 0 on /proc/meminfo and returns -1.
        if (OperatingSystem.IsAndroid() || OperatingSystem.IsLinux())
        {
            try
            {
                if (System.IO.File.Exists("/proc/meminfo"))
                {
                    foreach (var line in System.IO.File.ReadLines("/proc/meminfo"))
                    {
                        if (!line.StartsWith("MemTotal:", StringComparison.Ordinal)) continue;
                        var kb = ParseMemInfoKb(line);
                        if (kb > 0) return (int)Math.Clamp(kb / 1024L, 0L, int.MaxValue);
                        break;
                    }
                }
            }
            catch
            {
                // Procfs may be inaccessible in restricted sandboxes.
            }
        }
        return 0;
    }

    private static long ParseMemInfoKb(string line)
    {
        long value = 0;
        var inDigits = false;
        for (var i = 0; i < line.Length; i++)
        {
            var ch = line[i];
            if (ch is >= '0' and <= '9')
            {
                inDigits = true;
                value = value * 10 + (ch - '0');
            }
            else if (inDigits)
            {
                break;
            }
        }
        return inDigits ? value : 0;
    }
}
