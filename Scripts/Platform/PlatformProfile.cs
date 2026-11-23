using System;

namespace DealOrForce.Platform;

/// <summary>Device form factor classification used to tune HUD scale, touch targets, and camera defaults.</summary>
public enum PlatformFormFactor
{
    Desktop = 0,
    AndroidPhone = 1,
    AndroidTablet = 2
}

/// <summary>Coarse graphics tier bridging platform capabilities with the map quality ladder.</summary>
public enum PlatformGraphicsTier
{
    DesktopHigh = 0,
    MobileMedium = 1,
    MobileLow = 2
}

/// <summary>
/// Display safe-area insets (notch, camera cutout, rounded corners, gesture navigation bar)
/// clamped to sensible fractions so malformed OS metrics can never collapse the HUD.
/// </summary>
public readonly record struct SafeAreaInsets(float Left, float Top, float Right, float Bottom)
{
    public static SafeAreaInsets Zero => default;

    public bool HasAny => Left > 0f || Top > 0f || Right > 0f || Bottom > 0f;

    /// <summary>
    /// Derive clamped insets from full display dimensions and OS-reported safe rectangle.
    /// Any invalid, NaN, or degenerate rectangle yields <see cref="Zero"/>.
    /// </summary>
    public static SafeAreaInsets FromRects(
        float screenWidth,
        float screenHeight,
        float safeX,
        float safeY,
        float safeWidth,
        float safeHeight,
        float maxInsetFraction = 0.25f)
    {
        if (!IsFinitePositive(screenWidth) || !IsFinitePositive(screenHeight) ||
            !IsFinitePositive(safeWidth) || !IsFinitePositive(safeHeight) ||
            !float.IsFinite(safeX) || !float.IsFinite(safeY))
        {
            return Zero;
        }

        var fraction = float.IsFinite(maxInsetFraction) ? Math.Clamp(maxInsetFraction, 0.01f, 0.45f) : 0.25f;
        var maxHorizontal = screenWidth * fraction;
        var maxVertical = screenHeight * fraction;

        var rawLeft = MathF.Max(0f, safeX);
        var rawTop = MathF.Max(0f, safeY);
        var rawRight = MathF.Max(0f, screenWidth - (safeX + safeWidth));
        var rawBottom = MathF.Max(0f, screenHeight - (safeY + safeHeight));

        var left = Math.Clamp(rawLeft, 0f, maxHorizontal);
        var top = Math.Clamp(rawTop, 0f, maxVertical);
        var right = Math.Clamp(rawRight, 0f, maxHorizontal);
        var bottom = Math.Clamp(rawBottom, 0f, maxVertical);

        // Guard combined opposite edges so usable viewport never collapses below 50%.
        if (left + right > screenWidth * 0.5f)
        {
            var scale = (screenWidth * 0.5f) / (left + right);
            left *= scale;
            right *= scale;
        }
        if (top + bottom > screenHeight * 0.5f)
        {
            var scale = (screenHeight * 0.5f) / (top + bottom);
            top *= scale;
            bottom *= scale;
        }

        return new SafeAreaInsets(left, top, right, bottom);
    }

    /// <summary>Scale insets into logical UI coordinates (never negative or non-finite).</summary>
    public SafeAreaInsets Scale(float factor)
    {
        if (!IsFinitePositive(factor)) return Zero;
        return new SafeAreaInsets(
            MathF.Max(0f, Left * factor),
            MathF.Max(0f, Top * factor),
            MathF.Max(0f, Right * factor),
            MathF.Max(0f, Bottom * factor));
    }

    /// <summary>Compute the unobstructed HUD rectangle inside a viewport of the given size.</summary>
    public (float X, float Y, float Width, float Height) ApplyToViewport(float viewportWidth, float viewportHeight)
    {
        if (!IsFinitePositive(viewportWidth) || !IsFinitePositive(viewportHeight))
            return (0f, 0f, 0f, 0f);

        var left = Math.Clamp(float.IsFinite(Left) ? Left : 0f, 0f, viewportWidth * 0.45f);
        var top = Math.Clamp(float.IsFinite(Top) ? Top : 0f, 0f, viewportHeight * 0.45f);
        var right = Math.Clamp(float.IsFinite(Right) ? Right : 0f, 0f, viewportWidth * 0.45f);
        var bottom = Math.Clamp(float.IsFinite(Bottom) ? Bottom : 0f, 0f, viewportHeight * 0.45f);

        // Guarantee at least 50% of viewport width and height remain usable for HUD chrome.
        if (left + right > viewportWidth * 0.5f)
        {
            var scale = (viewportWidth * 0.5f) / (left + right);
            left *= scale;
            right *= scale;
        }
        if (top + bottom > viewportHeight * 0.5f)
        {
            var scale = (viewportHeight * 0.5f) / (top + bottom);
            top *= scale;
            bottom *= scale;
        }

        var usableW = MathF.Max(0f, viewportWidth - left - right);
        var usableH = MathF.Max(0f, viewportHeight - top - bottom);
        return (left, top, usableW, usableH);
    }

    private static bool IsFinitePositive(float value) => float.IsFinite(value) && value > 0f;
}

/// <summary>
/// Pure-data runtime facts gathered once from Godot (or fabricated in headless unit tests)
/// so <see cref="PlatformProfile.Evaluate"/> stays 100% deterministic and engine-independent.
/// </summary>
public readonly record struct PlatformSnapshot(
    string OsName,
    bool HasMobileFeature,
    bool HasTouchscreen,
    int ScreenWidthPx,
    int ScreenHeightPx,
    float ScreenDpi,
    SafeAreaInsets SafeAreaRawPx,
    int SystemMemoryMb = 0,
    bool IsLowEndGpu = false,
    float ViewportHeightLogical = 1080f);

/// <summary>
/// Central platform capability and mobile ergonomics profile for Deal or Force.
/// Pure C# core with zero Godot dependencies; populated on startup by <c>GodotPlatformBridge</c>.
/// </summary>
public sealed class PlatformProfile
{
    public const float TabletDiagonalThresholdInches = 7.0f;
    public const float BaselineDpi = 160f;
    public const float PhoneMinTouchTargetDp = 48f;
    public const float TabletMinTouchTargetDp = 44f;
    public const float DesktopMinTargetPx = 32f;

    public static PlatformProfile DesktopDefault { get; } = Evaluate(new PlatformSnapshot(
        OsName: "Windows",
        HasMobileFeature: false,
        HasTouchscreen: false,
        ScreenWidthPx: 1920,
        ScreenHeightPx: 1080,
        ScreenDpi: 96f,
        SafeAreaRawPx: SafeAreaInsets.Zero,
        SystemMemoryMb: 16384,
        IsLowEndGpu: false,
        ViewportHeightLogical: 1080f));

    private static volatile PlatformProfile _current = DesktopDefault;

    /// <summary>Active runtime profile; defaults cleanly to <see cref="DesktopDefault"/> in headless runs and can never be null.</summary>
    public static PlatformProfile Current => _current ?? DesktopDefault;

    /// <summary>Set by <c>GodotPlatformBridge.DetectCurrent</c> or tests.</summary>
    public static void SetCurrent(PlatformProfile profile) => _current = profile ?? DesktopDefault;

    public PlatformFormFactor FormFactor { get; }
    public bool IsAndroid { get; }
    public bool IsMobile { get; }
    public bool IsTouchPrimary { get; }
    public SafeAreaInsets SafeAreaRawPx { get; }
    public SafeAreaInsets SafeArea { get; }
    public float ScreenDiagonalInches { get; }
    public float DensityScale { get; }
    public float UiScaleFactor { get; }
    public float MinimumTouchTargetDp { get; }
    public float MinimumTouchTargetPx { get; }
    public PlatformGraphicsTier GraphicsQualityTier { get; }
    public bool IsSteamSupported { get; }
    public bool IsDesktopFileDialogSupported { get; }
    public bool IsExternalWorkerModSupported { get; }
    public bool ShouldUseCompactTextureBudget { get; }
    public int RecommendedIdleFpsCap { get; }
    public int RecommendedActiveFpsCap { get; }

    private PlatformProfile(
        PlatformFormFactor formFactor,
        bool isAndroid,
        bool isMobile,
        bool isTouchPrimary,
        SafeAreaInsets safeAreaRawPx,
        SafeAreaInsets safeArea,
        float screenDiagonalInches,
        float densityScale,
        float uiScaleFactor,
        float minimumTouchTargetDp,
        float minimumTouchTargetPx,
        PlatformGraphicsTier graphicsQualityTier,
        bool isSteamSupported,
        bool isDesktopFileDialogSupported,
        bool isExternalWorkerModSupported,
        bool shouldUseCompactTextureBudget,
        int recommendedIdleFpsCap,
        int recommendedActiveFpsCap)
    {
        FormFactor = formFactor;
        IsAndroid = isAndroid;
        IsMobile = isMobile;
        IsTouchPrimary = isTouchPrimary;
        SafeAreaRawPx = safeAreaRawPx;
        SafeArea = safeArea;
        ScreenDiagonalInches = screenDiagonalInches;
        DensityScale = densityScale;
        UiScaleFactor = uiScaleFactor;
        MinimumTouchTargetDp = minimumTouchTargetDp;
        MinimumTouchTargetPx = minimumTouchTargetPx;
        GraphicsQualityTier = graphicsQualityTier;
        IsSteamSupported = isSteamSupported;
        IsDesktopFileDialogSupported = isDesktopFileDialogSupported;
        IsExternalWorkerModSupported = isExternalWorkerModSupported;
        ShouldUseCompactTextureBudget = shouldUseCompactTextureBudget;
        RecommendedIdleFpsCap = recommendedIdleFpsCap;
        RecommendedActiveFpsCap = recommendedActiveFpsCap;
    }

    public static PlatformProfile Evaluate(PlatformSnapshot snapshot)
    {
        var os = snapshot.OsName ?? string.Empty;
        var isAndroid = os.Equals("Android", StringComparison.OrdinalIgnoreCase);
        var isIos = os.Equals("iOS", StringComparison.OrdinalIgnoreCase);
        var isMobile = isAndroid || isIos || snapshot.HasMobileFeature;

        var widthPx = Math.Max(1, snapshot.ScreenWidthPx);
        var heightPx = Math.Max(1, snapshot.ScreenHeightPx);
        var safeDpi = float.IsFinite(snapshot.ScreenDpi) && snapshot.ScreenDpi >= 72f
            ? Math.Clamp(snapshot.ScreenDpi, 72f, 800f)
            : (isMobile ? 320f : 96f);

        var diagonal = ComputeDiagonalInches(widthPx, heightPx, safeDpi);
        var densityScale = Math.Clamp(safeDpi / BaselineDpi, 0.75f, 4.5f);

        PlatformFormFactor formFactor;
        if (!isMobile)
        {
            formFactor = PlatformFormFactor.Desktop;
        }
        else
        {
            formFactor = diagonal >= TabletDiagonalThresholdInches
                ? PlatformFormFactor.AndroidTablet
                : PlatformFormFactor.AndroidPhone;
        }

        var isTouchPrimary = isMobile || (snapshot.HasTouchscreen && formFactor != PlatformFormFactor.Desktop);

        // Convert raw pixel safe-area insets into logical canvas units using viewport height ratio.
        var logicalH = float.IsFinite(snapshot.ViewportHeightLogical) && snapshot.ViewportHeightLogical > 0f
            ? snapshot.ViewportHeightLogical
            : 1080f;
        var shortPx = Math.Min(widthPx, heightPx);
        var pxToLogical = shortPx > 0 ? logicalH / shortPx : 1f;
        var clampedRawSafe = SafeAreaInsets.FromRects(
            widthPx, heightPx,
            snapshot.SafeAreaRawPx.Left,
            snapshot.SafeAreaRawPx.Top,
            MathF.Max(1f, widthPx - snapshot.SafeAreaRawPx.Left - snapshot.SafeAreaRawPx.Right),
            MathF.Max(1f, heightPx - snapshot.SafeAreaRawPx.Top - snapshot.SafeAreaRawPx.Bottom));
        var safeAreaLogical = clampedRawSafe.Scale(pxToLogical);

        float uiScale;
        float minTargetDp;
        float minTargetPx;
        if (formFactor == PlatformFormFactor.Desktop)
        {
            uiScale = 1.0f;
            minTargetDp = DesktopMinTargetPx;
            minTargetPx = DesktopMinTargetPx;
        }
        else if (formFactor == PlatformFormFactor.AndroidPhone)
        {
            // Phones have small physical screens; boost readable chrome and enforce >= 48dp hit targets.
            var sizeBoost = diagonal < 6.0f ? 1.35f : (diagonal < 6.6f ? 1.25f : 1.18f);
            uiScale = Math.Clamp(sizeBoost, 1.15f, 1.45f);
            minTargetDp = PhoneMinTouchTargetDp;
            var physicalTargetPx = minTargetDp * densityScale;
            minTargetPx = Math.Clamp(physicalTargetPx * pxToLogical, 48f, 96f);
        }
        else
        {
            // Tablets have ample canvas area; subtle touch-friendly scale and >= 44dp hit targets.
            uiScale = diagonal < 9.5f ? 1.15f : 1.08f;
            minTargetDp = TabletMinTouchTargetDp;
            var physicalTargetPx = minTargetDp * densityScale;
            minTargetPx = Math.Clamp(physicalTargetPx * pxToLogical, 44f, 84f);
        }

        PlatformGraphicsTier graphicsTier;
        if (!isMobile)
        {
            graphicsTier = PlatformGraphicsTier.DesktopHigh;
        }
        else
        {
            var lowRam = snapshot.SystemMemoryMb > 0 && snapshot.SystemMemoryMb <= 4096;
            graphicsTier = (snapshot.IsLowEndGpu || lowRam)
                ? PlatformGraphicsTier.MobileLow
                : PlatformGraphicsTier.MobileMedium;
        }

        var isDesktop = formFactor == PlatformFormFactor.Desktop;
        var compactTextures = graphicsTier == PlatformGraphicsTier.MobileLow ||
                              (formFactor == PlatformFormFactor.AndroidPhone && snapshot.SystemMemoryMb > 0 && snapshot.SystemMemoryMb <= 6144);

        return new PlatformProfile(
            formFactor: formFactor,
            isAndroid: isAndroid,
            isMobile: isMobile,
            isTouchPrimary: isTouchPrimary,
            safeAreaRawPx: clampedRawSafe,
            safeArea: safeAreaLogical,
            screenDiagonalInches: diagonal,
            densityScale: densityScale,
            uiScaleFactor: uiScale,
            minimumTouchTargetDp: minTargetDp,
            minimumTouchTargetPx: minTargetPx,
            graphicsQualityTier: graphicsTier,
            isSteamSupported: isDesktop,
            isDesktopFileDialogSupported: isDesktop,
            isExternalWorkerModSupported: isDesktop,
            shouldUseCompactTextureBudget: compactTextures,
            recommendedIdleFpsCap: isMobile ? 30 : 60,
            recommendedActiveFpsCap: isMobile ? 60 : 0);
    }

    public static float ComputeDiagonalInches(int widthPx, int heightPx, float dpi)
    {
        if (widthPx <= 0 || heightPx <= 0 || !float.IsFinite(dpi) || dpi <= 0f) return 0f;
        var wIn = widthPx / dpi;
        var hIn = heightPx / dpi;
        return MathF.Sqrt(wIn * wIn + hIn * hIn);
    }

    /// <summary>Ensure an interactive control height/width meets the active platform's minimum touch target.</summary>
    public static float ClampTouchTarget(float requestedPx, PlatformProfile profile = null)
    {
        var active = profile ?? Current;
        var value = float.IsFinite(requestedPx) ? MathF.Max(0f, requestedPx) : 0f;
        return MathF.Max(value, active.MinimumTouchTargetPx);
    }

    /// <summary>Heuristic detector for older mobile GPUs or GLES3 fallback where <see cref="PlatformGraphicsTier.MobileLow"/> is safer.</summary>
    public static bool IsLowEndMobileAdapter(string adapterName, string renderingMethod)
    {
        if (!string.IsNullOrWhiteSpace(renderingMethod) &&
            renderingMethod.Contains("gl_compatibility", StringComparison.OrdinalIgnoreCase))
        {
            return true;
        }
        if (string.IsNullOrWhiteSpace(adapterName)) return false;

        var name = adapterName.ToLowerInvariant();
        return name.Contains("swiftshader") ||
               name.Contains("llvmpipe") ||
               name.Contains("powervr ge8") ||
               name.Contains("mali-g52") ||
               name.Contains("mali-g57") ||
               name.Contains("mali-t8") ||
               name.Contains("mali-400") ||
               name.Contains("adreno (tm) 5") ||
               name.Contains("adreno 5") ||
               name.Contains("adreno (tm) 610") ||
               name.Contains("adreno 610");
    }
}
