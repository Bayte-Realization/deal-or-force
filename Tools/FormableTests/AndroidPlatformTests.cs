using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Text.Json;
using System.Threading;
using System.Threading.Tasks;
using DealOrForce.Platform;
using DealOrForce.Simulation;
using DealOrForce.Simulation.Shell;

static class AndroidPlatformTests
{
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception("FAIL android platform: " + label);
        Console.WriteLine("PASS android platform: " + label);
    }

    private static bool Approx(float a, float b, float eps = 1e-3f) => MathF.Abs(a - b) <= eps;

    [TestSuite("--android-platform-only", Order = 2600, IncludeInDefaultRun = true,
        Description = "PlatformProfile scaling/safe-area, TouchGestureMachine, 4-mode TouchTapMode, and MobileTextAdapter.")]
    public static void Run()
    {
        RunPlatformProfileTests();
        RunSafeAreaTests();
        RunTapAndLongPressTests();
        RunPanAndInertiaTests();
        RunPinchRotateTiltTests();
        RunTouchZoomAntiDriftAndTapConfigTests();
        RunMultiTouchEdgeCaseTests();
        RunTapModeTests();
        RunMobileTextAdapterTests();
        RunTouchModeBarAndLazyBootContractTests();
        RunWaveM5AssetCatalogAndWarmupTests();
        RunWaveM6UiAndTouchContractTests();
        RunBackgroundSimReadToWriteUpgradeTests();
        RunBackgroundSimStagedDeterminismTests();
        RunBackgroundSimMobilePacingTests();
        RunCompletedDateTests();
        RunQueuedSaveTests();
        RunRenderSnapshotReuseTests();
        RunRenderSnapshotNameTests();
        RunConcurrentCatalogReloadSafetyTests();
        RunCombatAndMovementArrowTests();
        RunMobileFirstBootDefaultsTests();
        RunSupportMarkerNoFlickerAndReadabilityTests();
        RunS22HotfixRegressionTests();
    }

    private static void RunPlatformProfileTests()
    {
        // Regression guard: PlatformProfile.Current must be non-null before any SetCurrent() call
        // (protects against static field initializer ordering bugs).
        Check(PlatformProfile.Current is not null, "PlatformProfile.Current is non-null prior to SetCurrent");
        Check(ReferenceEquals(PlatformProfile.Current, PlatformProfile.DesktopDefault),
            "PlatformProfile.Current defaults to DesktopDefault before initialization");

        // Desktop profile
        var desktop = PlatformProfile.DesktopDefault;
        Check(desktop.FormFactor == PlatformFormFactor.Desktop, "DesktopDefault form factor is Desktop");
        Check(!desktop.IsAndroid && !desktop.IsMobile && !desktop.IsTouchPrimary, "DesktopDefault is non-mobile non-touch");
        Check(Approx(desktop.UiScaleFactor, 1.0f), "DesktopDefault UI scale is 1.0");
        Check(desktop.GraphicsQualityTier == PlatformGraphicsTier.DesktopHigh, "DesktopDefault graphics tier is DesktopHigh");
        Check(desktop.IsSteamSupported && desktop.IsDesktopFileDialogSupported && desktop.IsExternalWorkerModSupported,
            "DesktopDefault enables Steam, desktop file dialogs, and worker mods");

        // Mid/high-end Android Phone (6.4" 2400x1080 @ 411 dpi, 8 GB RAM, Adreno 740, punch-hole notch left=88px)
        var phone = PlatformProfile.Evaluate(new PlatformSnapshot(
            OsName: "Android",
            HasMobileFeature: true,
            HasTouchscreen: true,
            ScreenWidthPx: 2400,
            ScreenHeightPx: 1080,
            ScreenDpi: 411f,
            SafeAreaRawPx: new SafeAreaInsets(Left: 88f, Top: 0f, Right: 24f, Bottom: 48f),
            SystemMemoryMb: 8192,
            IsLowEndGpu: false,
            ViewportHeightLogical: 1080f));

        Check(phone.FormFactor == PlatformFormFactor.AndroidPhone, "6.4in Android classifies as AndroidPhone");
        Check(phone.IsAndroid && phone.IsMobile && phone.IsTouchPrimary, "AndroidPhone flags IsAndroid/IsMobile/IsTouchPrimary");
        Check(phone.ScreenDiagonalInches > 6.0f && phone.ScreenDiagonalInches < 6.8f, "AndroidPhone diagonal inches accurate");
        Check(phone.UiScaleFactor >= 1.15f && phone.UiScaleFactor <= 1.45f, "AndroidPhone boosts UiScaleFactor cleanly");
        Check(phone.MinimumTouchTargetDp == PlatformProfile.PhoneMinTouchTargetDp && phone.MinimumTouchTargetPx >= 48f,
            "AndroidPhone enforces >= 48dp touch target");
        Check(phone.GraphicsQualityTier == PlatformGraphicsTier.MobileMedium, "8GB non-low-end phone gets MobileMedium");
        Check(!phone.IsSteamSupported && !phone.IsDesktopFileDialogSupported && !phone.IsExternalWorkerModSupported,
            "AndroidPhone disables Steam, desktop ShellOpen, and external worker mods");
        Check(Approx(phone.SafeArea.Left, 88f) && Approx(phone.SafeArea.Bottom, 48f),
            "AndroidPhone propagates safe-area insets into logical coordinates");
        Check(PlatformProfile.ClampTouchTarget(28f, phone) >= 48f, "ClampTouchTarget lifts small button heights on phone");

        // Android Tablet (11" 2560x1600 @ 274 dpi, 6 GB RAM)
        var tablet = PlatformProfile.Evaluate(new PlatformSnapshot(
            OsName: "Android",
            HasMobileFeature: true,
            HasTouchscreen: true,
            ScreenWidthPx: 2560,
            ScreenHeightPx: 1600,
            ScreenDpi: 274f,
            SafeAreaRawPx: new SafeAreaInsets(0f, 32f, 0f, 32f),
            SystemMemoryMb: 6144,
            IsLowEndGpu: false,
            ViewportHeightLogical: 1080f));

        Check(tablet.FormFactor == PlatformFormFactor.AndroidTablet, "11in Android classifies as AndroidTablet");
        Check(tablet.MinimumTouchTargetDp == PlatformProfile.TabletMinTouchTargetDp && tablet.MinimumTouchTargetPx >= 44f,
            "AndroidTablet enforces >= 44dp touch target");
        Check(tablet.UiScaleFactor >= 1.05f && tablet.UiScaleFactor < phone.UiScaleFactor,
            "AndroidTablet uses subtler UI scale than compact phone");

        // Budget 4 GB / Low-end GPU Android phone -> MobileLow + compact texture budget
        var budget = PlatformProfile.Evaluate(new PlatformSnapshot(
            OsName: "Android",
            HasMobileFeature: true,
            HasTouchscreen: true,
            ScreenWidthPx: 1600,
            ScreenHeightPx: 720,
            ScreenDpi: 270f,
            SafeAreaRawPx: SafeAreaInsets.Zero,
            SystemMemoryMb: 4096,
            IsLowEndGpu: PlatformProfile.IsLowEndMobileAdapter("Mali-G52 MC2", "gl_compatibility"),
            ViewportHeightLogical: 720f));

        Check(budget.GraphicsQualityTier == PlatformGraphicsTier.MobileLow, "4GB/Mali-G52/GLES3 gets MobileLow");
        Check(budget.ShouldUseCompactTextureBudget, "MobileLow enables compact texture budget");
        Check(budget.RecommendedIdleFpsCap == 30 && budget.RecommendedActiveFpsCap == 60,
            "Mobile recommends 30 FPS idle / 60 FPS active caps");
        Check(PlatformProfile.IsLowEndMobileAdapter("Adreno (TM) 506", "mobile"), "Adreno 5xx flagged low-end");
        Check(!PlatformProfile.IsLowEndMobileAdapter("Adreno (TM) 740", "mobile"), "Adreno 740 on mobile Vulkan is not low-end");
        Check(phone.ShouldUseCompactTextureBudget, "8GB AndroidPhone enables compact texture budget");
        Check(!PlatformProfile.IsLowEndMobileAdapter("Mali-G78", "mobile"), "Mali-G78 mobile adapter is not flagged low-end");
        Check(!PlatformProfile.IsLowEndMobileAdapter("ARM Mali-G78 MP14", "gl_compatibility"), "Exynos Mali-G78 on GLES3 runs at MobileMedium");
        Check(!PlatformProfile.IsLowEndMobileAdapter("ANGLE (Samsung Xclipse 920) on Vulkan 1.3.279", "gl_compatibility"),
            "Samsung Xclipse 920 via ANGLE-on-Vulkan is not flagged low-end");
        var s21 = PlatformProfile.Evaluate(new PlatformSnapshot(
            OsName: "Android",
            HasMobileFeature: true,
            HasTouchscreen: true,
            ScreenWidthPx: 2400,
            ScreenHeightPx: 1080,
            ScreenDpi: 421f,
            SafeAreaRawPx: SafeAreaInsets.Zero,
            SystemMemoryMb: 8192,
            IsLowEndGpu: PlatformProfile.IsLowEndMobileAdapter("Mali-G78", "gl_compatibility"),
            ViewportHeightLogical: 1080f));
        Check(s21.GraphicsQualityTier == PlatformGraphicsTier.MobileMedium && s21.ShouldUseCompactTextureBudget,
            "8GB Galaxy S21 Mali-G78 evaluates to MobileMedium + ShouldUseCompactTextureBudget");
        var s22 = PlatformProfile.Evaluate(new PlatformSnapshot(
            OsName: "Android",
            HasMobileFeature: true,
            HasTouchscreen: true,
            ScreenWidthPx: 2340,
            ScreenHeightPx: 1080,
            ScreenDpi: 425f,
            SafeAreaRawPx: SafeAreaInsets.Zero,
            SystemMemoryMb: 8192,
            IsLowEndGpu: PlatformProfile.IsLowEndMobileAdapter("ANGLE (Samsung Xclipse 920) on Vulkan 1.3.279", "gl_compatibility"),
            ViewportHeightLogical: 1080f));
        Check(s22.GraphicsQualityTier == PlatformGraphicsTier.MobileMedium && s22.ShouldUseCompactTextureBudget,
            "8GB Galaxy S22 Xclipse 920 on Vulkan 1.3.279 evaluates to MobileMedium + ShouldUseCompactTextureBudget");

        // RecommendedCloseButtonPx scaling across Desktop, Phone, and Tablet
        Check(Approx(desktop.RecommendedCloseButtonPx, 32f), "Desktop RecommendedCloseButtonPx is 32f");
        Check(phone.RecommendedCloseButtonPx >= 42f && phone.RecommendedCloseButtonPx <= 52f &&
              Approx(phone.RecommendedCloseButtonPx, Math.Clamp(phone.MinimumTouchTargetPx * 0.92f, 42f, 52f)),
            "AndroidPhone RecommendedCloseButtonPx scales cleanly within [42, 52]");
        Check(tablet.RecommendedCloseButtonPx >= 42f && tablet.RecommendedCloseButtonPx <= 52f &&
              Approx(tablet.RecommendedCloseButtonPx, Math.Clamp(tablet.MinimumTouchTargetPx * 0.92f, 42f, 52f)),
            "AndroidTablet RecommendedCloseButtonPx scales cleanly within [42, 52]");

        // Emulator (BlueStacks / Android SDK / VM) adapter detection -> MobileLow + ShouldUseCompactTextureBudget
        foreach (var emuAdapter in new[]
                 {
                     "BlueStacks OpenGL Renderer",
                     "BST OpenGL",
                     "Android Emulator OpenGL ES Translator",
                     "Android SDK built for x86_64",
                     "Mesa Virtio-GPU",
                     "Goldfish GPU",
                     "Ranchu Emulator",
                     "ANGLE (NVIDIA, Direct3D11)",
                     "VBoxSVGA",
                     "VMware SVGA 3D"
                 })
        {
            var flagged = PlatformProfile.IsLowEndMobileAdapter(emuAdapter, "mobile");
            Check(flagged, $"Emulator/VM adapter '{emuAdapter}' flagged low-end");
            var emuProfile = PlatformProfile.Evaluate(new PlatformSnapshot(
                OsName: "Android",
                HasMobileFeature: true,
                HasTouchscreen: true,
                ScreenWidthPx: 1920,
                ScreenHeightPx: 1080,
                ScreenDpi: 320f,
                SafeAreaRawPx: SafeAreaInsets.Zero,
                SystemMemoryMb: 8192,
                IsLowEndGpu: flagged,
                ViewportHeightLogical: 1080f));
            Check(emuProfile.GraphicsQualityTier == PlatformGraphicsTier.MobileLow &&
                  emuProfile.ShouldUseCompactTextureBudget,
                $"Emulator adapter '{emuAdapter}' evaluates to MobileLow + ShouldUseCompactTextureBudget");
        }
    }

    private static void RunSafeAreaTests()
    {
        // Normal landscape cutout
        var normal = SafeAreaInsets.FromRects(2400f, 1080f, 96f, 20f, 2280f, 1020f);
        Check(Approx(normal.Left, 96f) && Approx(normal.Top, 20f) && Approx(normal.Right, 24f) && Approx(normal.Bottom, 40f),
            "SafeAreaInsets.FromRects extracts exact 4-edge notch/gesture insets");

        // Pathological / malformed OS metrics (giant 80% cutout) must be clamped so HUD never collapses
        var clamped = SafeAreaInsets.FromRects(2000f, 1000f, 900f, 600f, 200f, 100f);
        Check(clamped.Left <= 500f && clamped.Right <= 500f && clamped.Top <= 250f && clamped.Bottom <= 250f,
            "SafeAreaInsets clamps extreme insets to <= 25% per edge");
        var rect = clamped.ApplyToViewport(1920f, 1080f);
        Check(rect.Width >= 960f && rect.Height >= 540f, "ApplyToViewport guarantees usable HUD bounds");

        // NaN / zero / negative safety
        var bad = SafeAreaInsets.FromRects(float.NaN, 1080f, -10f, 0f, 0f, 1080f);
        Check(!bad.HasAny, "SafeAreaInsets returns Zero on NaN or degenerate rects");
    }

    private static void RunTapAndLongPressTests()
    {
        var machine = new TouchGestureMachine(TouchGestureConfig.Default);

        // Quick tap inside deadzone
        var d0 = machine.OnTouchDown(0, 400f, 300f, 1.00);
        Check(!d0.HasAnyAction && machine.State == TouchGestureState.PendingTapOrPan, "TouchDown enters PendingTapOrPan");
        var dMove = machine.OnTouchMove(0, 404f, 303f, 1.05);
        Check(!dMove.HasAnyAction && machine.State == TouchGestureState.PendingTapOrPan, "Jitter inside deadzone does not pan");
        var dUp = machine.OnTouchUp(0, 404f, 303f, 1.12);
        Check(dUp.TapTriggered && Approx(dUp.TapPositionPx.X, 404f) && Approx(dUp.TapPositionPx.Y, 303f),
            "Quick release inside deadzone emits TapTriggered");
        Check(!dUp.LongPressTriggered && machine.State == TouchGestureState.Idle, "Tap returns cleanly to Idle");

        // Long press (right-click context / army order equivalent)
        machine.OnTouchDown(1, 500f, 450f, 2.00);
        var tickEarly = machine.OnTick(2.25, 0.016f);
        Check(!tickEarly.LongPressTriggered && machine.State == TouchGestureState.PendingTapOrPan,
            "Long-press does not fire before duration threshold");
        var tickFire = machine.OnTick(2.45, 0.016f);
        Check(tickFire.LongPressTriggered && Approx(tickFire.LongPressPositionPx.X, 500f) && machine.State == TouchGestureState.LongPressFired,
            "Holding >= 0.42s inside tolerance fires LongPressTriggered");
        var tickRepeat = machine.OnTick(2.60, 0.016f);
        Check(!tickRepeat.LongPressTriggered, "Long-press fires strictly ONCE per hold");
        var upAfterLong = machine.OnTouchUp(1, 500f, 450f, 2.70);
        Check(!upAfterLong.TapTriggered && !upAfterLong.LongPressTriggered && machine.State == TouchGestureState.Idle,
            "Releasing after long-press emits no spurious tap");
    }

    private static void RunPanAndInertiaTests()
    {
        var machine = new TouchGestureMachine(TouchGestureConfig.Default);

        machine.OnTouchDown(0, 100f, 100f, 0.00);
        var p1 = machine.OnTouchMove(0, 130f, 110f, 0.02); // 31.6px > 12px deadzone
        Check(machine.State == TouchGestureState.Panning, "Drag past deadzone transitions to Panning");
        Check(Approx(p1.PanDeltaPx.X, 30f) && Approx(p1.PanDeltaPx.Y, 10f), "Initial pan emits full offset from start");

        // Long-press must NOT fire while panning
        var tickDuringPan = machine.OnTick(0.60, 0.016f);
        Check(!tickDuringPan.LongPressTriggered, "Long-press suppressed once Panning started");

        // Active flick followed by release
        var p2 = machine.OnTouchMove(0, 170f, 110f, 0.62);
        Check(Approx(p2.PanDeltaPx.X, 40f), "Subsequent pan emits incremental delta");
        machine.OnTouchUp(0, 170f, 110f, 0.63);
        Check(machine.State == TouchGestureState.Idle && machine.InertiaVelocityPxPerSec.X > 100f,
            "Release during active flick seeds inertial velocity");

        var coast1 = machine.OnTick(0.646, 0.016f);
        var coast2 = machine.OnTick(0.662, 0.016f);
        Check(coast1.PanDeltaPx.X > 0f && coast2.PanDeltaPx.X > 0f && coast2.PanDeltaPx.X < coast1.PanDeltaPx.X,
            "Inertial coast decays exponentially frame over frame");

        // Eventually settles to exact zero
        for (var i = 0; i < 60; i++) machine.OnTick(0.67 + i * 0.05, 0.05f);
        Check(machine.InertiaVelocityPxPerSec.LengthSquared == 0f, "Inertia decays cleanly to zero");

        // Rested drag (>90ms pause before lift) must produce zero inertia
        machine.OnTouchDown(0, 200f, 200f, 5.00);
        machine.OnTouchMove(0, 250f, 200f, 5.02);
        machine.OnTouchUp(0, 250f, 200f, 5.20); // rested 180ms
        Check(machine.InertiaVelocityPxPerSec.LengthSquared == 0f, "Rested drag release yields zero inertia");
    }

    private static void RunPinchRotateTiltTests()
    {
        var machine = new TouchGestureMachine(TouchGestureConfig.Default);

        // Two fingers placed horizontally 200px apart around midpoint (500, 400)
        machine.OnTouchDown(0, 400f, 400f, 0.00);
        machine.OnTouchDown(1, 600f, 400f, 0.01);
        Check(machine.State == TouchGestureState.Pinching && machine.ActiveFingerCount == 2,
            "Second finger enters Pinching state");

        // Pinch out: expand right finger from 600 -> 700 (span 200 -> 300, scale = 1.5, midpoint = 550, 400)
        var zoomOut = machine.OnTouchMove(1, 700f, 400f, 0.03);
        Check(Approx(zoomOut.ZoomScaleFactor, 1.5f, 1e-2f), "Pinch-out emits accurate ZoomScaleFactor = 1.5");
        Check(Approx(zoomOut.ZoomAnchorPx.X, 550f) && Approx(zoomOut.ZoomAnchorPx.Y, 400f),
            "Pinch zoom anchor tracks exact 2-finger midpoint");
        Check(zoomOut.PanDeltaPx.LengthSquared == 0f && zoomOut.RotateDeltaRadians == 0f && zoomOut.TiltDeltaPx == 0f,
            "Active pinch zoom emits strictly zero pan, zero rotate drift, and zero tilt drift");

        // Pinch in: contract right finger from 700 -> 550 (span 300 -> 150, scale = 0.5)
        var zoomIn = machine.OnTouchMove(1, 550f, 400f, 0.05);
        Check(Approx(zoomIn.ZoomScaleFactor, 0.5f, 1e-2f), "Pinch-in emits accurate ZoomScaleFactor = 0.5");
        Check(zoomIn.PanDeltaPx.LengthSquared == 0f && zoomIn.RotateDeltaRadians == 0f,
            "Pinch-in emits strictly zero pan and zero rotate drift");

        // Small accidental wobble (<0.22 rad ~ 12.5 deg) at constant span must NOT emit rotation drift
        var wobble = machine.OnTouchMove(1, 549.4f, 413f, 0.06); // ~0.086 rad, span ~150
        Check(wobble.RotateDeltaRadians == 0f && wobble.PanDeltaPx.LengthSquared == 0f,
            "Sub-deadzone 2-finger wobble (<0.22 rad) emits zero rotation drift");

        // Intentional 2-finger twist rotation at stable span (move finger 1 to (400, 550) -> +pi/2 rad)
        var twist = machine.OnTouchMove(1, 400f, 550f, 0.07);
        Check(Approx(twist.RotateDeltaRadians, MathF.PI * 0.5f, 0.03f) && twist.PanDeltaPx.LengthSquared == 0f,
            "Intentional 2-finger twist at stable span emits signed RotateDeltaRadians and zero pan");

        // Angle wrap across -pi / +pi boundary
        Check(Approx(TouchGestureMachine.WrapAngleRadians(MathF.PI * 1.9f), -MathF.PI * 0.1f, 1e-3f),
            "WrapAngleRadians normalizes across pi boundary cleanly");

        // Reset for parallel vertical 2-finger tilt (anti-drift deadzone = 18px)
        machine.CancelAll();
        machine.OnTouchDown(0, 400f, 400f, 1.00);
        machine.OnTouchDown(1, 600f, 400f, 1.01);
        var smallTilt = machine.OnTouchMove(0, 400f, 408f, 1.02); // midpoint +4px < 18px
        Check(smallTilt.TiltDeltaPx == 0f && smallTilt.PanDeltaPx.LengthSquared == 0f,
            "Small 2-finger vertical jitter (<18px) emits zero tilt drift");
        var t1 = machine.OnTouchMove(0, 400f, 424f, 1.03); // midpoint +12px total
        var t2 = machine.OnTouchMove(1, 600f, 424f, 1.04); // midpoint +24px total -> crosses 18px deadzone
        Check((t1.TiltDeltaPx > 0f || t2.TiltDeltaPx > 0f) && t2.PanDeltaPx.LengthSquared == 0f,
            "Intentional parallel 2-finger vertical slide (>=18px) emits TiltDeltaPx and zero pan");
        Check(Approx(t2.ZoomScaleFactor, 1.0f, 0.05f), "Parallel vertical tilt keeps zoom near 1.0");
    }

    private static void RunTouchZoomAntiDriftAndTapConfigTests()
    {
        // (a) 30-step 120 Hz smooth incremental pinch (~1% span growth per frame) with realistic
        // thumb arc and slight vertical drift must emit ZERO pan, rotate, or tilt across all 30 frames.
        var machine = new TouchGestureMachine(TouchGestureConfig.Default);
        const float cx = 960f;
        const float cy = 540f;
        var radius = 120f;
        var angle = 0f;
        var midY = cy;
        machine.OnTouchDown(0, cx - radius, midY, 0.000);
        machine.OnTouchDown(1, cx + radius, midY, 0.001);
        var hadDrift = false;
        var allZoomedIn = true;
        for (var step = 1; step <= 30; step++)
        {
            radius *= 1.01f;
            angle += 0.009f; // ~0.27 rad (~15.5 deg) cumulative arc over 30 frames
            midY += 0.5f;    // 15px cumulative vertical drift
            var cos = MathF.Cos(angle);
            var sin = MathF.Sin(angle);
            var t = 0.008 * step;
            var d0 = machine.OnTouchMove(0, cx - radius * cos, midY - radius * sin, t);
            var d1 = machine.OnTouchMove(1, cx + radius * cos, midY + radius * sin, t + 0.001);
            if (d0.PanDeltaPx.LengthSquared > 0f || d0.RotateDeltaRadians != 0f || d0.TiltDeltaPx != 0f ||
                d1.PanDeltaPx.LengthSquared > 0f || d1.RotateDeltaRadians != 0f || d1.TiltDeltaPx != 0f)
            {
                hadDrift = true;
            }
            if (d0.ZoomScaleFactor <= 1f || d1.ZoomScaleFactor <= 1f)
                allZoomedIn = false;
        }
        Check(!hadDrift && allZoomedIn,
            "30-step 120Hz smooth pinch with thumb arc emits zero pan, zero rotate, and zero tilt drift");

        // (b) TouchGestureConfig.ForProfile eliminates the 0.32s-0.42s silent tap drop gap and sets 15px*scale deadzone
        var phoneProfile = PlatformProfile.Evaluate(new PlatformSnapshot(
            OsName: "Android",
            HasMobileFeature: true,
            HasTouchscreen: true,
            ScreenWidthPx: 2340,
            ScreenHeightPx: 1080,
            ScreenDpi: 425f,
            SafeAreaRawPx: SafeAreaInsets.Zero,
            SystemMemoryMb: 8192,
            IsLowEndGpu: false,
            ViewportHeightLogical: 1080f));
        var phoneCfg = TouchGestureConfig.ForProfile(phoneProfile);
        var expectedScale = Math.Clamp(phoneProfile.UiScaleFactor, 1.0f, 1.6f);
        Check(phoneCfg.MaxTapDurationSec >= phoneCfg.LongPressDurationSec - 0.01f &&
              Approx(phoneCfg.MaxTapDurationSec, 0.42f) &&
              Approx(phoneCfg.PanDeadzonePx, 15f * expectedScale) &&
              Approx(TouchGestureConfig.Default.MaxTapDurationSec, 0.32f) &&
              Approx(TouchGestureConfig.Default.PanDeadzonePx, 12f),
            "TouchGestureConfig.ForProfile removes 0.32s-0.42s tap dead-zone and uses 15px*scale pan deadzone while preserving Default");

        var phoneTapMachine = new TouchGestureMachine(phoneCfg);
        phoneTapMachine.OnTouchDown(0, 600f, 400f, 1.00);
        var slowTap = phoneTapMachine.OnTouchUp(0, 602f, 401f, 1.37); // 370 ms tap
        Check(slowTap.TapTriggered && !slowTap.LongPressTriggered,
            "TouchGestureConfig.ForProfile fires TapTriggered cleanly for a 370ms mobile tap");

        // (c) Multi-step direct pinch anchor accuracy using ControlSchemeRules.SolveGlobeZoomAnchor
        const float viewW = 1920f;
        const float viewH = 1080f;
        const float fov = 38f;
        const float globeRadius = 5f;
        const float maxPitch = 1.50f;
        const float pixelX = 1180f;
        const float pixelY = 440f;
        var pitch = 0.30f;
        var yaw = -0.35f;
        var dist = 12.5f;

        var hitOk = DealOrForce.Simulation.Controls.ControlSchemeRules.TryGlobeRayHitLocal(
            pixelX, pixelY, viewW, viewH, fov, globeRadius, dist, out var initialLocal);
        Check(hitOk && initialLocal.Z >= 0.25, "Initial multi-step pinch test ray hits inside globe limb");
        var initialWorld = DealOrForce.Simulation.Controls.ControlSchemeRules.OrbitLocalToWorld(initialLocal, pitch, yaw);

        var maxAnchorDotErr = 0.0;
        var allSolved = true;
        for (var step = 0; step < 10; step++)
        {
            if (!DealOrForce.Simulation.Controls.ControlSchemeRules.TryGlobeRayHitLocal(
                    pixelX, pixelY, viewW, viewH, fov, globeRadius, dist, out var hitLocal) ||
                hitLocal.Z < 0.25)
            {
                allSolved = false;
                break;
            }
            var anchorWorld = DealOrForce.Simulation.Controls.ControlSchemeRules.OrbitLocalToWorld(hitLocal, pitch, yaw);
            var oldAlt = Math.Max(0.05f, dist - globeRadius);
            var newDist = Math.Clamp(globeRadius + oldAlt / 1.06f, 5.4f, 22f);
            if (!DealOrForce.Simulation.Controls.ControlSchemeRules.SolveGlobeZoomAnchor(
                    pixelX, pixelY, viewW, viewH, fov, globeRadius, maxPitch,
                    anchorWorld.X, anchorWorld.Y, anchorWorld.Z,
                    pitch, yaw, newDist, out var nextPitch, out var nextYaw))
            {
                allSolved = false;
                break;
            }
            pitch = nextPitch;
            yaw = nextYaw;
            dist = newDist;
            if (!DealOrForce.Simulation.Controls.ControlSchemeRules.TryGlobeRayHitLocal(
                    pixelX, pixelY, viewW, viewH, fov, globeRadius, dist, out var verifyLocal))
            {
                allSolved = false;
                break;
            }
            var verifyWorld = DealOrForce.Simulation.Controls.ControlSchemeRules.OrbitLocalToWorld(verifyLocal, pitch, yaw);
            var dot = initialWorld.X * verifyWorld.X + initialWorld.Y * verifyWorld.Y + initialWorld.Z * verifyWorld.Z;
            maxAnchorDotErr = Math.Max(maxAnchorDotErr, Math.Abs(1.0 - dot));
        }
        Check(allSolved && maxAnchorDotErr < 1e-6,
            $"10-step direct globe pinch zoom keeps anchored surface point locked (dot error={maxAnchorDotErr:E2})");
    }

    private static void RunMultiTouchEdgeCaseTests()
    {
        var machine = new TouchGestureMachine(TouchGestureConfig.Default);

        // 2-finger pinch -> lift one finger -> PostPinchLockout stays locked until ALL fingers lift
        machine.OnTouchDown(0, 300f, 300f, 0.00);
        machine.OnTouchDown(1, 500f, 300f, 0.01);
        // 3rd finger noise must be ignored
        machine.OnTouchDown(2, 700f, 700f, 0.02);
        Check(machine.ActiveFingerCount == 2, "3rd finger ignored cleanly without corrupting primary pair");
        machine.OnTouchMove(2, 750f, 750f, 0.03);
        machine.OnTouchUp(2, 750f, 750f, 0.04);
        Check(machine.State == TouchGestureState.Pinching && machine.ActiveFingerCount == 2,
            "3rd finger lift leaves active pinch intact");

        // Lift finger 0 -> enters PostPinchLockout with finger 1 promoted to primary
        machine.OnTouchUp(0, 300f, 300f, 0.05);
        Check(machine.State == TouchGestureState.PostPinchLockout && machine.ActiveFingerCount == 1,
            "Lifting 1 of 2 pinch fingers enters PostPinchLockout");

        // Movement during PostPinchLockout must NEVER leak into pan even long after 80ms or >100px drag
        var duringLockout1 = machine.OnTouchMove(1, 520f, 300f, 0.07);
        var duringLockout2 = machine.OnTouchMove(1, 650f, 340f, 0.60);
        Check(!duringLockout1.HasAnyAction && !duringLockout2.HasAnyAction && machine.State == TouchGestureState.PostPinchLockout,
            "PostPinchLockout blocks all 1-finger pan jumps until ALL fingers lift");

        // Lifting remaining finger during PostPinchLockout must return to Idle with NO tap or inertia
        var liftRemaining = machine.OnTouchUp(1, 650f, 340f, 0.65);
        Check(!liftRemaining.HasAnyAction && machine.State == TouchGestureState.Idle && machine.ActiveFingerCount == 0,
            "Lifting remaining finger after pinch returns cleanly to Idle with zero accidental tap/pan");

        // OnTouchCancel on 1-finger drag/box-select must cancel cleanly with zero tap/box-complete/inertia
        machine.SetTapMode(TouchTapMode.BoxSelect);
        machine.OnTouchDown(0, 200f, 200f, 0.70);
        machine.OnTouchMove(0, 280f, 260f, 0.72);
        Check(machine.State == TouchGestureState.BoxSelecting, "BoxSelect enters BoxSelecting before OS cancel");
        var cancelBox = machine.OnTouchCancel(0, 0.74);
        Check(!cancelBox.HasAnyAction && machine.State == TouchGestureState.Idle && machine.ActiveFingerCount == 0,
            "OnTouchCancel during BoxSelecting cancels cleanly without firing BoxSelectCompleted or tap");
        machine.SetTapMode(TouchTapMode.Select);

        // OnTouchCancel on 1 of 2 pinch fingers enters PostPinchLockout cleanly
        machine.OnTouchDown(0, 300f, 300f, 0.80);
        machine.OnTouchDown(1, 500f, 300f, 0.81);
        var cancelPinch = machine.OnTouchCancel(0, 0.82);
        Check(!cancelPinch.HasAnyAction && machine.State == TouchGestureState.PostPinchLockout && machine.ActiveFingerCount == 1,
            "OnTouchCancel on 1 pinch finger transitions cleanly to PostPinchLockout");
        machine.OnTouchCancel(1, 0.83);
        Check(machine.State == TouchGestureState.Idle && machine.ActiveFingerCount == 0,
            "OnTouchCancel on remaining finger returns cleanly to Idle");

        // CancelAll resets everything mid-gesture
        machine.OnTouchDown(0, 100f, 100f, 1.00);
        machine.OnTouchMove(0, 150f, 100f, 1.02);
        machine.CancelAll();
        Check(machine.State == TouchGestureState.Idle && machine.ActiveFingerCount == 0 && machine.InertiaVelocityPxPerSec.LengthSquared == 0f,
            "CancelAll resets active fingers, state, and inertia");
    }

    private static void RunTapModeTests()
    {
        var machine = new TouchGestureMachine(TouchGestureConfig.Default);
        Check(machine.ActiveMode == TouchTapMode.Select && machine.OneShotOrderMode,
            "TouchGestureMachine defaults to Select with OneShotOrderMode=true");

        // 1. Order mode (one-shot default): single tap fires immediate RMB order & returns to Select
        machine.SetTapMode(TouchTapMode.Order, oneShotOrder: true);
        machine.OnTouchDown(0, 320f, 240f, 1.00);
        var orderTap = machine.OnTouchUp(0, 322f, 241f, 1.10);
        Check(orderTap.LongPressTriggered && !orderTap.TapTriggered && Approx(orderTap.LongPressPositionPx.X, 322f),
            "Order mode single tap fires immediate RMB/context order without 420ms hold");
        Check(machine.ActiveMode == TouchTapMode.Select,
            "OneShotOrderMode automatically returns ActiveMode to Select after order");

        // 2. Order mode (sticky via PressOrderModeButton twice): stays in Order across multiple taps
        machine.PressOrderModeButton(); // Select -> Order (one-shot)
        machine.PressOrderModeButton(); // Order one-shot -> Order sticky
        Check(machine.ActiveMode == TouchTapMode.Order && machine.IsStickyOrder,
            "Second press on Order button enables Sticky Order chaining");
        machine.OnTouchDown(0, 100f, 100f, 2.00);
        var sticky1 = machine.OnTouchUp(0, 100f, 100f, 2.08);
        machine.OnTouchDown(0, 200f, 150f, 2.20);
        var sticky2 = machine.OnTouchUp(0, 200f, 150f, 2.28);
        Check(sticky1.LongPressTriggered && sticky2.LongPressTriggered && machine.ActiveMode == TouchTapMode.Order,
            "Sticky Order mode dispatches repeated single-tap orders without resetting");

        // 3. BoxSelect mode: 1-finger drag draws normalized marquee rectangle; 2-finger still zooms & pans!
        machine.SetTapMode(TouchTapMode.BoxSelect);
        machine.OnTouchDown(0, 500f, 400f, 3.00);
        // Drag up-left to verify normalized positive rect bounds
        var boxUpd = machine.OnTouchMove(0, 420f, 310f, 3.04);
        Check(machine.State == TouchGestureState.BoxSelecting && boxUpd.BoxSelectUpdated && !boxUpd.BoxSelectCompleted,
            "BoxSelect 1-finger drag enters BoxSelecting and emits BoxSelectUpdated");
        Check(Approx(boxUpd.BoxSelectRect.X, 420f) && Approx(boxUpd.BoxSelectRect.Y, 310f) &&
              Approx(boxUpd.BoxSelectRect.Width, 80f) && Approx(boxUpd.BoxSelectRect.Height, 90f),
            "BoxSelect normalizes up-left drag to positive (X,Y,W,H) rect");
        Check(boxUpd.PanDeltaPx.LengthSquared == 0f, "BoxSelect 1-finger drag does not pan camera");

        var boxDone = machine.OnTouchUp(0, 400f, 300f, 3.10);
        Check(boxDone.BoxSelectCompleted && !boxDone.TapTriggered && Approx(boxDone.BoxSelectRect.Width, 100f) && Approx(boxDone.BoxSelectRect.Height, 100f),
            "BoxSelect release emits BoxSelectCompleted with final bounds and no tap");
        Check(machine.InertiaVelocityPxPerSec.LengthSquared == 0f, "BoxSelect release emits zero pan inertia");

        // 2-finger pinch inside BoxSelect mode zooms cleanly and NEVER pans the camera
        machine.OnTouchDown(0, 300f, 300f, 4.00);
        machine.OnTouchDown(1, 500f, 300f, 4.01);
        var boxTwoFinger = machine.OnTouchMove(1, 600f, 320f, 4.03);
        Check(machine.State == TouchGestureState.Pinching && machine.ActiveMode == TouchTapMode.BoxSelect,
            "2nd finger in BoxSelect transitions to Pinching while keeping BoxSelect active");
        Check(boxTwoFinger.ZoomScaleFactor > 1.2f && boxTwoFinger.PanDeltaPx.LengthSquared == 0f,
            "2-finger pinch in BoxSelect emits zoom and strictly zero camera pan");
        machine.CancelAll();

        // Mid-marquee 2nd finger cancels BoxSelect into Pinching with zero BoxSelectCompleted and zero pan
        machine.OnTouchDown(0, 200f, 200f, 4.50);
        var midBox = machine.OnTouchMove(0, 260f, 260f, 4.52);
        Check(machine.State == TouchGestureState.BoxSelecting && midBox.BoxSelectUpdated && midBox.PanDeltaPx.LengthSquared == 0f,
            "Mid-marquee BoxSelect emits BoxSelectUpdated with zero pan");
        var secondFingerCancel = machine.OnTouchDown(1, 460f, 260f, 4.53);
        Check(machine.State == TouchGestureState.Pinching && !secondFingerCancel.BoxSelectCompleted && secondFingerCancel.PanDeltaPx.LengthSquared == 0f,
            "2nd finger during BoxSelect cancels marquee cleanly into Pinching without BoxSelectCompleted");
        machine.CancelAll();

        // 4. Pan mode: suppresses both taps and long-presses while allowing normal 1-finger pan
        machine.SetTapMode(TouchTapMode.Pan);
        machine.OnTouchDown(0, 250f, 250f, 5.00);
        var panTap = machine.OnTouchUp(0, 251f, 251f, 5.10);
        Check(!panTap.TapTriggered && !panTap.LongPressTriggered, "Pan mode suppresses accidental taps");

        machine.OnTouchDown(0, 250f, 250f, 6.00);
        var panHold = machine.OnTick(6.60, 0.016f);
        Check(!panHold.LongPressTriggered, "Pan mode suppresses long-press orders");
        var panDrag = machine.OnTouchMove(0, 300f, 250f, 6.62);
        Check(machine.State == TouchGestureState.Panning && Approx(panDrag.PanDeltaPx.X, 50f),
            "Pan mode allows free 1-finger panning");

        // Localization key mapping sanity
        Check(TouchTapModeController.LocalizationKey(TouchTapMode.Select) == "mobile.mode.select" &&
              TouchTapModeController.LocalizationKey(TouchTapMode.Order) == "mobile.mode.order" &&
              TouchTapModeController.LocalizationKey(TouchTapMode.BoxSelect) == "mobile.mode.box" &&
              TouchTapModeController.LocalizationKey(TouchTapMode.Pan) == "mobile.mode.pan",
            "TouchTapModeController maps all 4 modes to mobile.mode.* keys");
    }

    private static void RunMobileTextAdapterTests()
    {
        // Desktop passthrough must be 100% byte-for-byte unchanged
        const string enQuit = "Quit to desktop";
        const string ruQuit = "Выход на рабочий стол";
        const string enObs = "Observer mode  [F7]";
        const string ruCmd = "ПКМ — марш или атака";
        Check(ReferenceEquals(MobileTextAdapter.Adapt(enQuit, isTouchPrimary: false), enQuit),
            "MobileTextAdapter is zero-allocation passthrough on Desktop (EN)");
        Check(ReferenceEquals(MobileTextAdapter.Adapt(ruQuit, isTouchPrimary: false), ruQuit),
            "MobileTextAdapter is zero-allocation passthrough on Desktop (RU)");
        Check(MobileTextAdapter.Adapt(enObs, isTouchPrimary: false) == enObs &&
              MobileTextAdapter.Adapt(ruCmd, isTouchPrimary: false) == ruCmd,
            "MobileTextAdapter preserves hotkeys and mouse labels on Desktop");

        // Mobile EN adaptation
        Check(MobileTextAdapter.Adapt(enQuit, isTouchPrimary: true) == "Exit game",
            "Mobile EN rewrites 'Quit to desktop' -> 'Exit game'");
        Check(MobileTextAdapter.Adapt(enObs, isTouchPrimary: true) == "Observer mode",
            "Mobile EN strips '[F7]' hotkey badge cleanly");
        Check(MobileTextAdapter.Adapt("Take control of {0}  [F7]", isTouchPrimary: true) == "Take control of {0}",
            "Mobile EN preserves {0} format placeholder when stripping hotkeys");
        Check(MobileTextAdapter.Adapt("Nothing is waiting for you. Let time run  [Space]", isTouchPrimary: true) ==
              "Nothing is waiting for you. Let time run",
            "Mobile EN strips '[Space]' hotkey cleanly");
        var enOrder = MobileTextAdapter.Adapt("Right-click to march · Left-click to select · Mouse wheel to zoom", isTouchPrimary: true);
        Check(enOrder.Contains("Order tap / Hold") && enOrder.Contains("Tap to select") && enOrder.Contains("Pinch to zoom"),
            "Mobile EN rewrites Right-click, Left-click, and Mouse wheel");

        // Mobile RU adaptation
        Check(MobileTextAdapter.Adapt(ruQuit, isTouchPrimary: true) == "Выйти из игры",
            "Mobile RU rewrites 'Выход на рабочий стол' -> 'Выйти из игры'");
        Check(MobileTextAdapter.Adapt("Режим наблюдателя  [F7]", isTouchPrimary: true) == "Режим наблюдателя",
            "Mobile RU strips '[F7]' cleanly");
        Check(MobileTextAdapter.Adapt("Взять под контроль {0}  [F7]", isTouchPrimary: true) == "Взять под контроль {0}",
            "Mobile RU preserves {0} format placeholder");
        var ruOrder = MobileTextAdapter.Adapt("ПКМ — марш · ЛКМ — выбор · Колёсико — масштаб", isTouchPrimary: true);
        Check(ruOrder.Contains("Режим приказа / Удержание") && ruOrder.Contains("Тап") && ruOrder.Contains("Щипок"),
            "Mobile RU rewrites ПКМ, ЛКМ, and Колёсико");

        // Explicit key override takes priority
        var overridden = MobileTextAdapter.Adapt(
            "Quit to desktop",
            isTouchPrimary: true,
            localizationKey: "quit_to_desktop",
            overrideLookup: k => k == "mobile.override.quit_to_desktop" ? "Close App" : null);
        Check(overridden == "Close App", "MobileTextAdapter honors explicit mobile.override.* lookup first");

        // Verify EN + RU 98_mobile_ux.json parity and placeholders
        var root = FindRepoRoot();
        var enPath = Path.Combine(root, "game", "localization", "english", "98_mobile_ux.json");
        var ruPath = Path.Combine(root, "game", "localization", "russian", "98_mobile_ux.json");
        Check(File.Exists(enPath) && File.Exists(ruPath), "98_mobile_ux.json ships in both english and russian");

        var enMap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(enPath));
        var ruMap = JsonSerializer.Deserialize<Dictionary<string, string>>(File.ReadAllText(ruPath));
        Check(enMap is not null && ruMap is not null && enMap.Count >= 18, "98_mobile_ux.json parses with all mobile UX keys");
        Check(enMap.Keys.OrderBy(k => k, StringComparer.Ordinal).SequenceEqual(ruMap.Keys.OrderBy(k => k, StringComparer.Ordinal)),
            "98_mobile_ux.json EN and RU have identical key sets");
        Check(enMap.Values.All(v => !string.IsNullOrWhiteSpace(v)) && ruMap.Values.All(v => !string.IsNullOrWhiteSpace(v)),
            "98_mobile_ux.json has zero empty values");
        Check(enMap["mobile.override.observer_take_fmt"].Contains("{0}") && ruMap["mobile.override.observer_take_fmt"].Contains("{0}"),
            "98_mobile_ux.json preserves {0} placeholder in EN and RU");
    }

    private static void RunTouchModeBarAndLazyBootContractTests()
    {
        // 1. Desktop keeps TouchModeBar empty; mobile docks 2x2 TouchModeBar beside DatePlate, off Minimap.
        var desktop = ShellLayout.ForViewport(1920f, 1080f, atWar: false, touchMode: false);
        Check(desktop.TouchModeBar.W == 0f && desktop.TouchModeBar.H == 0f,
            "Desktop ShellLayout leaves TouchModeBar empty");

        foreach (var (w, h) in new[] { (1280f, 720f), (1600f, 720f), (1920f, 1080f), (2400f, 1080f) })
        {
            var p = ShellLayout.ForViewport(w, h, atWar: true, touchMode: true);
            var mini = ShellLayout.MinimapRect(p);
            Check(p.TouchModeBar.Inside(w, h) && p.TouchModeBar.Right <= p.DatePlate.X &&
                  !p.TouchModeBar.Intersects(p.DatePlate) && !p.TouchModeBar.Intersects(p.TopPlate) &&
                  !p.TouchModeBar.Intersects(p.NewsCard) && !p.TouchModeBar.Intersects(mini) &&
                  !mini.Intersects(p.AlertColumn) && !mini.Intersects(p.MapBar),
                $"TouchModeBar beside DatePlate & Minimap unobstructed at {w:0}x{h:0}");
        }

        // 2. Source contracts: dedicated ShellTouchModeBarView, removed from ShellMapBarView,
        //    and true lazy modal construction + Speed 4/5 HUD refresh throttle in DealOrForceGame.
        var root = FindRepoRoot();
        var touchViewPath = Path.Combine(root, "Scripts", "ui", "Shell", "ShellTouchModeBarView.cs");
        var touchUidPath = touchViewPath + ".uid";
        var mapBarPath = Path.Combine(root, "Scripts", "ui", "Shell", "ShellMapBarView.cs");
        var gamePath = Path.Combine(root, "Scripts", "DealOrForceGame.cs");

        Check(File.Exists(touchViewPath) && File.Exists(touchUidPath),
            "ShellTouchModeBarView.cs and its .cs.uid exist");
        var touchViewSrc = File.ReadAllText(touchViewPath);
        Check(!touchViewSrc.Contains("new Color(", StringComparison.Ordinal) &&
              touchViewSrc.Split('\n').Length < 400,
            "ShellTouchModeBarView uses Ui.Colour tokens only and stays compact (<400 lines)");
        Check(!touchViewSrc.Contains("mobile.tap.", StringComparison.Ordinal) &&
              touchViewSrc.Contains("mobile.mode.select.short", StringComparison.Ordinal) &&
              touchViewSrc.Contains("mobile.mode.order.oneshot", StringComparison.Ordinal) &&
              touchViewSrc.Contains("mobile.mode.order.sticky", StringComparison.Ordinal) &&
              touchViewSrc.Contains("mobile.mode.box.short", StringComparison.Ordinal) &&
              touchViewSrc.Contains("mobile.mode.pan.short", StringComparison.Ordinal) &&
              touchViewSrc.Contains("ShellTouchModeToast", StringComparison.Ordinal) &&
              touchViewSrc.Contains("MouseFilterEnum.Ignore", StringComparison.Ordinal) &&
              touchViewSrc.Contains("public void Tick(double delta)", StringComparison.Ordinal),
            "ShellTouchModeBarView binds mobile.mode.* keys and non-blocking auto-fading toast pill");

        var orchSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "Shell", "ShellOrchestrator.cs"));
        Check(orchSrc.Contains("TouchModeBar.Tick(delta);", StringComparison.Ordinal),
            "ShellOrchestrator ticks ShellTouchModeBarView toast fade");

        // 2b. Verify ShellTouchModeBarView CaptionFor / TooltipFor / ToastFor keys resolve via Loc in english and russian
        var prevLang = Loc.Language;
        var prevAdapter = Loc.TextAdapter;
        Loc.ResetCatalog();
        try
        {
            Loc.TextAdapter = null;
            var keyMatches = System.Text.RegularExpressions.Regex.Matches(
                touchViewSrc, @"L\(""(mobile\.mode\.[^""]+)""");
            var viewKeys = keyMatches.Select(m => m.Groups[1].Value).Distinct(StringComparer.Ordinal).ToList();
            Check(viewKeys.Count >= 14,
                $"ShellTouchModeBarView extracts all mobile.mode.* localization keys ({viewKeys.Count})");

            Loc.Language = "english";
            Check(Loc.Get("mobile.mode.select.short") == "Select" &&
                  Loc.Get("mobile.mode.order.oneshot") == "Order 1×" &&
                  Loc.Get("mobile.mode.order.sticky") == "Order Lock" &&
                  Loc.Get("mobile.mode.box.short") == "Box" &&
                  Loc.Get("mobile.mode.pan.short") == "Map",
                "ShellTouchModeBarView resolves English captions (Select / Order 1× / Order Lock / Box / Map)");
            var enStrings = new Dictionary<string, string>(StringComparer.Ordinal);
            foreach (var k in viewKeys)
            {
                var resolved = Loc.Get(k);
                Check(resolved != k && !string.IsNullOrWhiteSpace(resolved),
                    $"ShellTouchModeBarView key '{k}' resolves in english");
                enStrings[k] = resolved;
            }

            Loc.Language = "russian";
            var ruOneShot = Loc.Get("mobile.mode.order.oneshot");
            var ruSticky = Loc.Get("mobile.mode.order.sticky");
            Check(Loc.Get("mobile.mode.select.short") == "Выбор" &&
                  ruOneShot == "Приказ 1×" && ruOneShot != "Order 1×" &&
                  ruSticky == "Приказ ●" && ruSticky != "Order Lock" &&
                  Loc.Get("mobile.mode.box.short") == "Рамка" &&
                  Loc.Get("mobile.mode.pan.short") == "Карта",
                "ShellTouchModeBarView resolves Russian captions without English fallback");
            foreach (var k in viewKeys)
            {
                var resolved = Loc.Get(k);
                Check(resolved != k && !string.IsNullOrWhiteSpace(resolved) && resolved != enStrings[k],
                    $"ShellTouchModeBarView key '{k}' translates distinctly in russian");
            }
        }
        finally
        {
            Loc.TextAdapter = prevAdapter;
            Loc.Language = prevLang;
        }

        var mapBarSrc = File.ReadAllText(mapBarPath);
        Check(!mapBarSrc.Contains("ShellTouchModeBar", StringComparison.Ordinal) &&
              !mapBarSrc.Contains("BuildTouchModeBar", StringComparison.Ordinal),
            "ShellMapBarView no longer hosts touch-mode buttons over the minimap");

        var gameSrc = File.ReadAllText(gamePath);
        Check(!gameSrc.Contains("EnsureDeferredMenuModals", StringComparison.Ordinal) &&
              !gameSrc.Contains("Built(\"pause_menu\"", StringComparison.Ordinal) &&
              !gameSrc.Contains("Built(\"free_camera\"", StringComparison.Ordinal),
            "DealOrForceGame removes eager/deferred modal boot construction (true lazy on-first-open)");
        Check(gameSrc.Contains("_lastFastHudRefreshMsec", StringComparison.Ordinal),
            "DealOrForceGame throttles secondary HUD refreshes at Speed 4/5");

        var lazyChecks = new (string RelPath, string Key)[]
        {
            (Path.Combine("Scripts", "ui", "SettingsUI.cs"), "EnsureScreen(\"settings\""),
            (Path.Combine("Scripts", "ui", "SaveLoadUI.cs"), "EnsureScreen(\"save_load\""),
            (Path.Combine("Scripts", "ui", "NetworkMultiplayerUI.cs"), "EnsureScreen(\"network\""),
            (Path.Combine("Scripts", "ui", "PauseMenuUI.cs"), "EnsureScreen(\"pause_menu\""),
            (Path.Combine("Scripts", "ui", "FreeCameraUI.cs"), "EnsureScreen(\"free_camera\"")
        };
        foreach (var (rel, needle) in lazyChecks)
        {
            var src = File.ReadAllText(Path.Combine(root, rel));
            Check(src.Contains(needle, StringComparison.Ordinal), $"{rel} lazily guards via {needle}");
        }
    }

    private static void RunWaveM5AssetCatalogAndWarmupTests()
    {
        var root = FindRepoRoot();
        var gameRoot = Path.Combine(root, "game");

        // 1. SpriteCatalog idempotency + overlay dirty reset
        SpriteCatalog.Load(force: true);
        var baseCount = SpriteCatalog.Count;
        Check(baseCount >= 500, $"SpriteCatalog.Load loads full base sprite table ({baseCount})");

        var spriteChanges = 0;
        Action onSpriteChanged = () => spriteChanges++;
        SpriteCatalog.Changed += onSpriteChanged;
        try
        {
            SpriteCatalog.Load(); // must be a 0-ms idempotent no-op
            Check(spriteChanges == 0 && SpriteCatalog.Count == baseCount,
                "SpriteCatalog.Load() is idempotent across repeated calls (0 Changed events)");

            SpriteCatalog.Merge("{\"__wave_m5_probe_sprite__\":\"res://game/gfx/interface/icons/icon_diplomacy.png\"}",
                "res://game/gfx", "wave_m5_test", null);
            Check(SpriteCatalog.Has("__wave_m5_probe_sprite__"),
                "SpriteCatalog.Merge layers mod sprite over base");

            SpriteCatalog.Load(); // dirty flag must trigger clean reload dropping probe sprite
            Check(spriteChanges == 1 && !SpriteCatalog.Has("__wave_m5_probe_sprite__") && SpriteCatalog.Count == baseCount,
                "SpriteCatalog.Load() reloads cleanly after mod/overlay Merge");
        }
        finally
        {
            SpriteCatalog.Changed -= onSpriteChanged;
        }

        // 2. Pre-baked sprite_index_v1.json round-trip matches full filesystem crawl
        var tempIndex = Path.Combine(Path.GetTempPath(), $"dof_sprite_index_test_{Guid.NewGuid():N}.json");
        try
        {
            var written = SpriteCatalog.WritePrebakedIndex(gameRoot, tempIndex);
            Check(written == baseCount && File.Exists(tempIndex),
                $"SpriteCatalog.WritePrebakedIndex writes all {written} sprites to {SpriteCatalog.SpriteIndexFileName}");
            var readOk = SpriteCatalog.TryReadPrebakedIndex(tempIndex, out var readSprites, out var readShaders);
            Check(readOk && readSprites == baseCount,
                $"SpriteCatalog.TryReadPrebakedIndex round-trips cleanly ({readSprites} sprites, {readShaders} shaders)");
        }
        finally
        {
            if (File.Exists(tempIndex)) File.Delete(tempIndex);
        }

        // 3. SoundCatalog, MusicCatalog, FontCatalog idempotency
        SoundCatalog.Load(force: true);
        MusicCatalog.Load(force: true);
        FontCatalog.Load(force: true);
        var soundChanges = 0;
        var musicChanges = 0;
        var fontChanges = 0;
        Action onSound = () => soundChanges++;
        Action onMusic = () => musicChanges++;
        Action onFont = () => fontChanges++;
        SoundCatalog.Changed += onSound;
        MusicCatalog.Changed += onMusic;
        FontCatalog.Changed += onFont;
        try
        {
            SoundCatalog.Load();
            MusicCatalog.Load();
            FontCatalog.Load();
            Check(soundChanges == 0 && musicChanges == 0 && fontChanges == 0,
                "SoundCatalog, MusicCatalog, and FontCatalog Load() are idempotent (0 redundant reloads)");
        }
        finally
        {
            SoundCatalog.Changed -= onSound;
            MusicCatalog.Changed -= onMusic;
            FontCatalog.Changed -= onFont;
        }

        // 4. TextureWarmup mobile concurrency + bounded RAM retention + StartDeferred
        var warmupSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "TextureWarmup.cs"));
        Check(warmupSrc.Contains("StartDeferred(", StringComparison.Ordinal) &&
              warmupSrc.Contains("MobileInFlight = 2", StringComparison.Ordinal) &&
              warmupSrc.Contains("MobileKeptLimit", StringComparison.Ordinal) &&
              warmupSrc.Contains("ShouldUseCompactTextureBudget", StringComparison.Ordinal),
            "TextureWarmup exposes StartDeferred, caps mobile concurrency at 2, and bounds mobile RAM retention");
    }

    private static void RunWaveM6UiAndTouchContractTests()
    {
        // 1. Desktop XP strip parity (bit-for-bit unchanged at SafeArea=0)
        var desktop1080 = ShellLayout.ForViewport(1920f, 1080f, atWar: false, touchMode: false);
        var xp1080 = ShellLayout.XpStripRect(desktop1080);
        Check(Approx(xp1080.X, 118f) && Approx(xp1080.Y, 74f) && Approx(xp1080.W, 348f) && Approx(xp1080.H, 40f),
            "Desktop 1920x1080 XpStripRect preserves exact (118, 74, 348, 40) placement");

        var desktop720 = ShellLayout.ForViewport(1280f, 720f, atWar: false, touchMode: false);
        var xp720 = ShellLayout.XpStripRect(desktop720);
        Check(Approx(xp720.X, 80f) && Approx(xp720.Y, 50f) && Approx(xp720.W, 276f) && Approx(xp720.H, 32f),
            "Desktop compact 1280x720 XpStripRect preserves exact (80, 50, 276, 32) placement");

        // 2. Android notched/safe-area viewports: ZERO overlap between XpStrip, CornerNode, TopPlate, LeftRail, NewsCard
        var mobileCases = new (float W, float H, float SL, float ST, float SR, float SB, bool War)[]
        {
            (1280f, 720f, 48f, 0f, 24f, 0f, false),
            (1280f, 720f, 56f, 12f, 24f, 8f, true),
            (1440f, 720f, 72f, 16f, 32f, 12f, true),
            (2400f, 1080f, 88f, 24f, 40f, 16f, true),
        };
        foreach (var c in mobileCases)
        {
            var prof = ShellLayout.ForViewport(c.W, c.H, c.War, touchMode: true, c.SL, c.ST, c.SR, c.SB);
            var xp = ShellLayout.XpStripRect(prof);
            Check(xp.X >= prof.CornerNode.Right && xp.X >= prof.LeftRail.Right,
                $"XpStrip clears CornerNode & LeftRail at {c.W}x{c.H} safeL={c.SL} war={c.War} (xp.X={xp.X}, cornerR={prof.CornerNode.Right}, railR={prof.LeftRail.Right})");
            Check(xp.Y >= prof.TopPlate.Bottom,
                $"XpStrip clears TopPlate at {c.W}x{c.H} safeT={c.ST} war={c.War} (xp.Y={xp.Y}, topB={prof.TopPlate.Bottom})");
            Check(xp.Right <= prof.NewsCard.X,
                $"XpStrip clears NewsCard at {c.W}x{c.H} safeL={c.SL} war={c.War} (xp.Right={xp.Right}, news.X={prof.NewsCard.X})");
        }

        // 3. Source contracts: legacy CampaignFlagMount hidden, fallback SafeArea preserved, DoctrineScreenUI × in header
        var root = FindRepoRoot();
        var newShellSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "Shell", "NewShellUI.cs"));
        Check(newShellSrc.Contains("\"CampaignFlagMount\"", StringComparison.Ordinal) &&
              !newShellSrc.Contains("\"HudFlagMount\"", StringComparison.Ordinal) &&
              newShellSrc.Contains("AddClassicNode(_hudFlagMount);", StringComparison.Ordinal),
            "NewShellUI hides CampaignFlagMount (fixes ghost legacy plate behind SafeArea HUD)");

        foreach (var shellFile in new[] { "ShellCornerView.cs", "ShellAlertColumnView.cs", "ShellMessageModalView.cs" })
        {
            var src = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "Shell", shellFile));
            Check(!src.Contains("ShellLayout.ForViewport(", StringComparison.Ordinal) &&
                  src.Contains("_ctx.ResolveViewportProfile(", StringComparison.Ordinal),
                $"{shellFile} uses _ctx.ResolveViewportProfile (never drops SafeArea on war/resize)");
        }

        var doctrineSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "DoctrineScreenUI.cs"));
        Check(doctrineSrc.Contains("false, toMilitary, close);", StringComparison.Ordinal) &&
              !doctrineSrc.Contains("_doctrineGeneralsView, toMilitary, close", StringComparison.Ordinal),
            "DoctrineScreenUI places CloseDoctrineScreen × in ScreenHeader instead of DoctrineViewTabs");

        // 4. StartupIntro + MainMenuScene platform boot + MobileTextAdapter + EN/RU mobile UX keys
        var introSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "StartupIntro.cs"));
        var menuSrc = File.ReadAllText(Path.Combine(root, "Scripts", "MainMenuScene.cs"));
        Check(menuSrc.Contains("GodotPlatformBridge.EnsureInitialized(", StringComparison.Ordinal) &&
              introSrc.Contains("GodotPlatformBridge.EnsureInitialized(", StringComparison.Ordinal) &&
              introSrc.Contains("mobile.intro.tap_to_skip", StringComparison.Ordinal),
            "MainMenuScene & StartupIntro initialize PlatformProfile early and use touch skip prompt");

        Check(MobileTextAdapter.Adapt("Press Esc, Space, Enter or click to skip", isTouchPrimary: true) == "Tap to skip",
            "MobileTextAdapter rewrites EN intro skip prompt on touch");
        Check(MobileTextAdapter.Adapt("Press Esc, Space, Enter or click to skip", isTouchPrimary: false) == "Press Esc, Space, Enter or click to skip",
            "MobileTextAdapter preserves EN intro skip prompt byte-identically on desktop");
        Check(MobileTextAdapter.Adapt("Нажмите Esc, пробел, Enter или кликните, чтобы пропустить", isTouchPrimary: true) == "Коснитесь экрана, чтобы пропустить",
            "MobileTextAdapter rewrites RU intro skip prompt on touch");
        Check(MobileTextAdapter.Adapt("Find  [Ctrl+K]", isTouchPrimary: true) == "Find",
            "MobileTextAdapter strips modifier bracketed hotkey [Ctrl+K] on touch");
        Check(MobileTextAdapter.Adapt("Undo (Ctrl+Z)", isTouchPrimary: true) == "Undo",
            "MobileTextAdapter strips modifier paren hotkey (Ctrl+Z) on touch");

        var requiredKeys = new[]
        {
            "mobile.intro.tap_to_skip", "mobile.override.peace_controls", "mobile.override.archive_subtitle",
            "mobile.override.handbook_subtitle", "mobile.override.country_picker_hint", "mobile.override.army_in_battle"
        };
        foreach (var lang in new[] { "english", "russian" })
        {
            var path = Path.Combine(root, "game", "localization", lang, "98_mobile_ux.json");
            using var doc = JsonDocument.Parse(File.ReadAllText(path));
            foreach (var key in requiredKeys)
            {
                Check(doc.RootElement.TryGetProperty(key, out var val) && !string.IsNullOrWhiteSpace(val.GetString()),
                    $"98_mobile_ux.json ({lang}) defines non-empty '{key}'");
            }
        }
        // 5. Ready-screen deadlock guard: DealOrForceGame.Refresh checks IsHeldByCurrentThread
        var gameSrc = File.ReadAllText(Path.Combine(root, "Scripts", "DealOrForceGame.cs"));
        Check(gameSrc.Contains("!_campaign.AccessGate.IsHeldByCurrentThread", StringComparison.Ordinal),
            "DealOrForceGame.Refresh checks AccessGate.IsHeldByCurrentThread before calling RunWrite");
    }

    private static void RunBackgroundSimReadToWriteUpgradeTests()
    {
        var campaign = new Campaign(61045) { IsPaused = true };
        using var runner = new BackgroundSimRunner();
        runner.Start(campaign);
        Check(runner.IsActive, "BackgroundSimRunner starts cleanly for read->write upgrade test");

        // Regression for Ready-screen freeze: Main Thread acquires TryEnterRead(0) in DealOrForceGame._Process,
        // then FlushInterfaceChanges invokes Changed -> Refresh -> RunWrite (or nested RunWrite / TryEnterRead).
        var changedHits = 0;
        campaign.Changed += () =>
        {
            runner.RunWrite(c =>
            {
                changedHits++;
                Check(c.AccessGate.IsWriterHeldByCurrentThread, "Upgraded RunWrite holds writer lock on current thread");
                Check(c.TryEnterRead(0, true), "Reentrant TryEnterRead inside upgraded RunWrite succeeds immediately");
                c.ExitRead();
                runner.RunWrite(_ => changedHits++, refreshSnapshot: false);
                Check(c.AccessGate.IsWriterHeldByCurrentThread, "Nested RunWrite exit keeps outer writer lock active");
            }, refreshSnapshot: true);
        };

        var upgradeDone = Task.Run(() =>
        {
            var sw = Stopwatch.StartNew();
            Check(campaign.TryEnterRead(200, true), "Outer TryEnterRead acquires read lock on caller thread");
            try
            {
                campaign.MarkStrategicChange();
                campaign.FlushInterfaceChanges();
            }
            finally
            {
                campaign.ExitRead();
            }
            sw.Stop();
            Check(changedHits == 2, $"Upgraded + nested RunWrite executed cleanly (hits={changedHits})");
            Check(sw.ElapsedMilliseconds < 1000, $"Read->Write upgrade finished without stall ({sw.ElapsedMilliseconds} ms)");
            Check(!campaign.AccessGate.IsHeldByCurrentThread && !campaign.AccessGate.IsWriterActive,
                "All read/write locks released cleanly after ExitRead");
        });
        Check(upgradeDone.Wait(2000), "Read->Write upgrade during active BackgroundSimRunner does not deadlock (Ready freeze regression)");

        // Stress active Speed-5 stepping concurrently with alternating Read and Read->Write upgrades.
        runner.RunWrite(c => c.IsPaused = false);
        runner.SyncClockState(false, 5);
        var stressDone = Task.Run(() =>
        {
            for (var i = 0; i < 8; i++)
            {
                if (campaign.TryEnterRead(50, true))
                {
                    try
                    {
                        runner.RunWrite(c => c.MarkStrategicChange(), refreshSnapshot: false);
                    }
                    finally
                    {
                        campaign.ExitRead();
                    }
                }
                Thread.Sleep(5);
            }
        });
        Check(stressDone.Wait(3000), "Concurrent Speed-5 Read->Write upgrade stress loop completes without deadlock");

        runner.Stop();
        Check(!runner.IsActive && !campaign.AccessGate.IsWriterActive, "BackgroundSimRunner stops cleanly with lock free");
    }

    private static void RunRenderSnapshotReuseTests()
    {
        var campaign = new Campaign(39);
        var first = new Nation { Tag = "AAA", MapIndex = 7, CustomName = "First", Treasury = 10 };
        var second = new Nation { Tag = "BBB", MapIndex = 9, CustomName = "Second", Treasury = 20 };
        campaign.AddNation(first);
        campaign.AddNation(second);
        var weights = CountryRankings.Weights.Default;
        var before = CampaignRenderSnapshot.Capture(campaign, CampaignRenderSnapshot.Empty, weights, 1);
        var nextHour = CampaignRenderSnapshot.Capture(campaign, before, weights, 2);
        Check(ReferenceEquals(before.Nations, nextHour.Nations), "unchanged country rows share the previous snapshot array");

        second.CustomName = "Renamed";
        second.Treasury = 25;
        var edited = CampaignRenderSnapshot.Capture(campaign, nextHour, weights, 3);
        Check(!ReferenceEquals(nextHour.Nations, edited.Nations) && edited.Nations[0].Equals(before.Nations[0]) &&
              edited.TryGetNation(second.Id, out var renamed) && renamed.DisplayName == "Renamed" && renamed.MapName == "Renamed" && renamed.Treasury == 25 &&
              before.TryGetNation(second.Id, out var old) && old.DisplayName == "Second" && old.Treasury == 20,
              "changing a late country row keeps earlier rows and leaves the last published snapshot untouched");

        second.MapIndex = 13;
        var reindexed = CampaignRenderSnapshot.Capture(campaign, edited, weights, 4);
        Check(reindexed.TryGetNationByMapIndex(13, out var moved) && moved.Id == second.Id &&
              !reindexed.TryGetNationByMapIndex(9, out _) && edited.TryGetNationByMapIndex(9, out _),
              "a map code change refreshes only the new snapshot's lookup even when the country count is unchanged");

        var founded = new Nation { Tag = "CCC", MapIndex = 21, CustomName = "Third" };
        campaign.AddNation(founded);
        var expanded = CampaignRenderSnapshot.Capture(campaign, reindexed, weights, 5);
        Check(expanded.Nations.Length == 3 && expanded.TryGetNation(founded.Id, out var added) && added.MapIndex == 21 &&
              expanded.TryGetNationByMapIndex(21, out _) && !reindexed.TryGetNation(founded.Id, out _),
              "new countries are indexed by id and map code as soon as the new snapshot is published");

        campaign.States.Add(new ScenarioState { Id = "snapshot_region", MapIndex = 3001, NationMapIndex = first.MapIndex, NationId = first.Tag });
        var regions = Enumerable.Repeat((ushort)3001, 128).ToArray();
        var owners = Enumerable.Repeat((ushort)first.MapIndex, regions.Length).ToArray();
        campaign.AttachProvinceTopology(ProvinceTopology.Build(regions, owners, 16, 8, _ => false));
        var map = CampaignRenderSnapshot.Capture(campaign, expanded, weights, 6);

        campaign.MarkMapChanged();
        campaign.InvalidateStateIndex();
        var revised = CampaignRenderSnapshot.Capture(campaign, map, weights, 7);
        Check(ReferenceEquals(map.ProvinceController, revised.ProvinceController) && ReferenceEquals(map.ProvinceOwner, revised.ProvinceOwner) &&
              revised.ControllerMapRevision == campaign.MapRevision && revised.OwnerRevision == campaign.OwnershipRevision,
              "an invalidated but unchanged map shares both rows and carries the new revisions");

        Check(campaign.SetProvinceController(1, (ushort)second.MapIndex), "the map fixture changes controller");
        var occupied = CampaignRenderSnapshot.Capture(campaign, revised, weights, 8);
        Check(!ReferenceEquals(revised.ProvinceController, occupied.ProvinceController) && ReferenceEquals(revised.ProvinceOwner, occupied.ProvinceOwner) &&
              revised.ProvinceController[1] == first.MapIndex && occupied.ProvinceController[1] == second.MapIndex,
              "a provincial occupation replaces the controller row and leaves the previous publication intact");
    }

    private static void RunRenderSnapshotNameTests()
    {
        var locale = Loc.Language;
        var adapter = Loc.TextAdapter;
        var c = new Campaign(40);
        var japan = new Nation { Tag = "JPN", CustomName = "country.JPN.name", MapIndex = 71 };
        var founded = new Nation { Tag = "LOCAL", MapIndex = 73, FoundNameKind = "land",
            FoundNameId = "snapshot_test_coast", FoundNameBase = "Old Coast", FoundNameForm = "republic" };
        c.AddNation(japan);
        c.AddNation(founded);
        var snapshot = CampaignRenderSnapshot.Empty;
        CampaignRenderSnapshot Capture() => snapshot = CampaignRenderSnapshot.Capture(
            c, snapshot, CountryRankings.Weights.Default, snapshot.Version + 1);

        try
        {
            Loc.TextAdapter = null;
            Loc.Language = "english";
            var english = Capture();
            Loc.Language = "russian";
            var russian = Capture();
            Check(russian.Nations[0].DisplayName == japan.DisplayName && english.Nations[0].DisplayName != japan.DisplayName,
                "cached country names switch language on the next snapshot");

            Loc.TextAdapter = (key, text) => key == japan.CustomName ? text + " · mobile" : text;
            var adapted = Capture();
            Check(adapted.Nations[0].DisplayName == russian.Nations[0].DisplayName + " · mobile",
                "changing the mobile text adapter invalidates cached country names");

            Loc.TextAdapter = null;
            japan.CustomName = "";
            japan.Government = "presidential_republic";
            var republic = Capture();
            japan.Government = "absolute_monarchy";
            var monarchy = Capture();
            Check(monarchy.Nations[0].DisplayName == japan.DisplayName &&
                  monarchy.Nations[0].DisplayName != republic.Nations[0].DisplayName,
                "a change of government updates a country's cached political name");

            Loc.Language = "english";
            var beforeMod = Capture();
            using var naming = JsonDocument.Parse("""{"lands":{"snapshot_test_coast":{"name":"New Coast"}}}""");
            LandAtlas.Merge(naming.RootElement);
            var afterMod = Capture();
            Check(afterMod.Nations[1].DisplayName == founded.DisplayName &&
                  afterMod.Nations[1].DisplayName.Contains("New Coast", StringComparison.Ordinal) &&
                  beforeMod.Nations[1].DisplayName.Contains("Old Coast", StringComparison.Ordinal),
                "a naming catalogue merge changes the snapshot without changing the country's recipe");
        }
        finally
        {
            using var remove = JsonDocument.Parse("""{"lands":{"snapshot_test_coast":{"remove":true}}}""");
            LandAtlas.Merge(remove.RootElement);
            Loc.TextAdapter = adapter;
            Loc.Language = locale;
        }
    }

    private static void RunBackgroundSimStagedDeterminismTests()
    {
        var cBg = Campaign.CreateBootstrap(91122);
        cBg.IsPaused = false;
        cBg.Speed = 8;
        using var runner = new BackgroundSimRunner();
        runner.Start(cBg);
        runner.SyncClockState(paused: false, speed: 8);

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 5000)
        {
            long hrs = 0;
            bool mid = false;
            if (cBg.TryEnterRead(5, true))
            {
                try
                {
                    hrs = cBg.ElapsedHours;
                    mid = cBg.MidStep;
                }
                finally { cBg.ExitRead(); }
            }
            if (hrs >= 48 && !mid) break;
            Thread.Sleep(2);
        }

        runner.RunWrite(c =>
        {
            c.IsPaused = true;
            if (c.MidStep) c.FinishStep();
        });
        runner.Stop();

        Check(cBg.ElapsedHours >= 48 && !cBg.MidStep,
            $"BackgroundSimRunner staged StepFor advanced cleanly to {cBg.ElapsedHours}h");

        var cSync = Campaign.CreateBootstrap(91122);
        cSync.IsPaused = false;
        cSync.Speed = 8;
        cSync.RuntimeStepHoursOverride = BackgroundSimRunner.AdaptiveStepHours(8);
        while (cSync.ElapsedHours < cBg.ElapsedHours)
            cSync.Tick();
        cSync.RuntimeStepHoursOverride = 0;

        var bgFp = CampaignFingerprint.Of(cBg);
        var syncFp = CampaignFingerprint.Of(cSync);
        Check(cSync.ElapsedHours == cBg.ElapsedHours && bgFp == syncFp,
            $"BackgroundSimRunner staged StepFor matches synchronous Campaign.Tick fingerprint at {cBg.ElapsedHours}h " +
            $"(syncH={cSync.ElapsedHours}, bgD={cBg.ElapsedDays}, syncD={cSync.ElapsedDays}, bgRng={cBg.Random.State}, syncRng={cSync.Random.State}, bgFp=0x{bgFp:X16}, syncFp=0x{syncFp:X16})");
    }

    private static void RunBackgroundSimMobilePacingTests()
    {
        // 1. Deterministic HasReaderRequested responsiveness & read-window handoff on CampaignAccessGate.
        var gateWorld = Campaign.CreateBootstrap(91124);
        gateWorld.AccessGate.EnterWrite();
        try
        {
            Check(!gateWorld.AccessGate.HasReaderRequested, "HasReaderRequested starts false before missed read");
            var missed = Task.Run(() => gateWorld.TryEnterRead(0, requestWindowOnMiss: true)).GetAwaiter().GetResult();
            Check(!missed && gateWorld.AccessGate.HasReaderRequested,
                "Missed TryEnterRead sets CampaignAccessGate.HasReaderRequested immediately");
        }
        finally
        {
            gateWorld.AccessGate.ExitWrite(yieldToRequestedReader: false);
        }
        Check(gateWorld.TryEnterRead(0, requestWindowOnMiss: true), "Subsequent TryEnterRead succeeds cleanly once writer exits");
        gateWorld.ExitRead();
        Check(!gateWorld.AccessGate.HasReaderRequested, "Entering read clears HasReaderRequested");

        // 2. Wartime CountryRankings throttle inside CampaignRenderSnapshot.Capture.
        gateWorld.IsPaused = false;
        var snap1 = CampaignRenderSnapshot.Capture(gateWorld, CampaignRenderSnapshot.Empty, CountryRankings.Weights.Default, 1);
        gateWorld.MarkMapChanged();
        gateWorld.MarkWarsChanged();
        var snap2 = CampaignRenderSnapshot.Capture(gateWorld, snap1, CountryRankings.Weights.Default, 2);
        Check(ReferenceEquals(snap1.NationRanksById, snap2.NationRanksById),
            "Wartime unpaused Capture reuses CountryRankings within the same week across Map/War revisions");
        gateWorld.IsPaused = true;
        var snap3 = CampaignRenderSnapshot.Capture(gateWorld, snap2, CountryRankings.Weights.Default, 3);
        Check(!ReferenceEquals(snap2.NationRanksById, snap3.NationRanksById) && snap3.NationRanksById.Count > 0,
            "Paused Capture refreshes CountryRankings when Map/War revisions differ");
        gateWorld.IsPaused = false;
        gateWorld.Defines.HoursPerTick = 24;
        for (var d = 0; d < 7; d++) gateWorld.Tick();
        gateWorld.IsPaused = false;
        var snap4 = CampaignRenderSnapshot.Capture(gateWorld, snap3, CountryRankings.Weights.Default, 4);
        Check(!ReferenceEquals(snap3.NationRanksById, snap4.NationRanksById) && snap4.RankWeek == gateWorld.ElapsedDays / 7,
            "Weekly boundary unpaused Capture recomputes CountryRankings cleanly");

        // 3. Batched Speed-8 mobile pacing (22 ms/day target) with 30 FPS reader non-starvation.
        // Pre-warm weekly passes and BackgroundSimRunner pacing path before timing so cold JIT
        // compilation never causes a false maxConsecutiveMisses spike.
        JitWarmup.PrepareWeeklyPasses();
        var warmupWorld = Campaign.CreateBootstrap(91120);
        warmupWorld.IsPaused = false;
        warmupWorld.Speed = 8;
        using (var warmupRunner = new BackgroundSimRunner { MobilePacing = true, MinSpeed5DayIntervalMs = 22.0 })
        {
            warmupRunner.Start(warmupWorld);
            warmupRunner.SyncClockState(paused: false, speed: 8);
            var warmSw = Stopwatch.StartNew();
            while (warmSw.ElapsedMilliseconds < 2500)
            {
                if (warmupWorld.TryEnterRead(5, true))
                {
                    var warmed = warmupWorld.ElapsedDays >= 1 && !warmupWorld.MidStep;
                    warmupWorld.ExitRead();
                    if (warmed) break;
                }
                Thread.Sleep(5);
            }
            warmupRunner.RunWrite(c =>
            {
                c.IsPaused = true;
                if (c.MidStep) c.FinishStep();
            });
            warmupRunner.Stop();
        }

        var cBg = Campaign.CreateBootstrap(91123);
        cBg.IsPaused = false;
        cBg.Speed = 8;
        using var runner = new BackgroundSimRunner
        {
            MobilePacing = true,
            MinSpeed5DayIntervalMs = 22.0
        };
        runner.Start(cBg);
        runner.SyncClockState(paused: false, speed: 8);

        var sw = Stopwatch.StartNew();
        var readSuccesses = 0;
        var consecutiveMisses = 0;
        var maxConsecutiveMisses = 0;
        var days = 0;
        long hrs = 0;
        var mid = false;
        while (sw.ElapsedMilliseconds < 8000)
        {
            if (cBg.TryEnterRead(0, requestWindowOnMiss: true))
            {
                try
                {
                    readSuccesses++;
                    consecutiveMisses = 0;
                    days = cBg.ElapsedDays;
                    hrs = cBg.ElapsedHours;
                    mid = cBg.MidStep;
                }
                finally { cBg.ExitRead(); }
            }
            else
            {
                consecutiveMisses++;
                if (consecutiveMisses > maxConsecutiveMisses) maxConsecutiveMisses = consecutiveMisses;
            }
            if (days >= 4 && hrs >= 96 && !mid) break;
            Thread.Sleep(33);
        }
        var pacingElapsedMs = sw.ElapsedMilliseconds;

        runner.RunWrite(c =>
        {
            c.IsPaused = true;
            if (c.MidStep) c.FinishStep();
        });
        runner.Stop();

        Check(cBg.ElapsedDays >= 4 && cBg.ElapsedHours >= 96 && !cBg.MidStep,
            $"Batched MobilePacing BackgroundSimRunner completed >= 4 days cleanly ({cBg.ElapsedDays}d / {cBg.ElapsedHours}h in {pacingElapsedMs} ms)");
        Check(cBg.RenderSnapshot.ElapsedDays >= 4 && cBg.RenderSnapshot.NationRanksById.Count > 0,
            "CampaignRenderSnapshot tracks batched Speed-8 progress and cached rankings");
        Check(readSuccesses >= 2 && maxConsecutiveMisses <= 2,
            $"Mobile 30 FPS reader acquires read windows without starvation (reads={readSuccesses}, maxConsecutiveMisses={maxConsecutiveMisses})");
        Check(pacingElapsedMs >= 50,
            $"MobilePacing enforces steady ~22ms/day EMA Speed-8 pacing ({pacingElapsedMs} ms >= 50 ms for {cBg.ElapsedDays} days)");

        var cSync = Campaign.CreateBootstrap(91123);
        cSync.IsPaused = false;
        cSync.Speed = 8;
        cSync.RuntimeStepHoursOverride = BackgroundSimRunner.MobileAdaptiveStepHours(8);
        while (cSync.ElapsedHours < cBg.ElapsedHours)
            cSync.Tick();
        cSync.RuntimeStepHoursOverride = 0;

        Check(cSync.ElapsedHours == cBg.ElapsedHours &&
              CampaignFingerprint.Of(cBg) == CampaignFingerprint.Of(cSync),
            $"Batched MobilePacing BackgroundSimRunner matches synchronous Campaign.Tick fingerprint at {cBg.ElapsedHours}h");

        // 4. Uncapped desktop batched Speed-8 determinism (MinSpeed5DayIntervalMs = 0).
        var cDesk = Campaign.CreateBootstrap(91127);
        cDesk.IsPaused = false;
        cDesk.Speed = 8;
        using var deskRunner = new BackgroundSimRunner
        {
            MobilePacing = false,
            MinSpeed5DayIntervalMs = 0.0
        };
        deskRunner.Start(cDesk);
        deskRunner.SyncClockState(paused: false, speed: 8);
        var deskSw = Stopwatch.StartNew();
        while (deskSw.ElapsedMilliseconds < 5000)
        {
            if (cDesk.TryEnterRead(5, true))
            {
                try
                {
                    if (cDesk.ElapsedDays >= 3 && cDesk.ElapsedHours >= 72 && !cDesk.MidStep) break;
                }
                finally { cDesk.ExitRead(); }
            }
            Thread.Sleep(2);
        }
        deskRunner.RunWrite(c =>
        {
            c.IsPaused = true;
            if (c.MidStep) c.FinishStep();
        });
        deskRunner.Stop();
        var cDeskSync = Campaign.CreateBootstrap(91127);
        cDeskSync.IsPaused = false;
        cDeskSync.Speed = 8;
        cDeskSync.RuntimeStepHoursOverride = BackgroundSimRunner.AdaptiveStepHours(8);
        while (cDeskSync.ElapsedHours < cDesk.ElapsedHours)
            cDeskSync.Tick();
        cDeskSync.RuntimeStepHoursOverride = 0;
        Check(cDesk.ElapsedHours >= 72 && cDeskSync.ElapsedHours == cDesk.ElapsedHours &&
              CampaignFingerprint.Of(cDesk) == CampaignFingerprint.Of(cDeskSync),
            $"Uncapped desktop batched Speed-8 matches synchronous Campaign.Tick fingerprint at {cDesk.ElapsedHours}h");

        var root = FindRepoRoot();
        var datePlate = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "Shell", "ShellDatePlateView.cs"));
        var game = File.ReadAllText(Path.Combine(root, "Scripts", "DealOrForceGame.cs"));
        Check(datePlate.Contains("_ctx.GetPresentationDate?.Invoke()", StringComparison.Ordinal),
            "ShellDatePlateView uses the same calendar source during ordinary and clock-only refreshes");
        Check(game.Contains("MobilePacing = _isMobileRuntime", StringComparison.Ordinal) &&
              game.Contains("MinSpeed5DayIntervalMs = 0.0", StringComparison.Ordinal) &&
              game.Contains("TryEnterRead(0, requestWindowOnMiss: urgentLive)", StringComparison.Ordinal) &&
              game.Contains("DatePlate.RefreshClock()", StringComparison.Ordinal),
            "DealOrForceGame configures uncapped mobile Speed-5 pacing, urgent live frame reads, and refreshes clock outside read gate");
    }

    private static void RunCompletedDateTests()
    {
        var campaign = Campaign.CreateBootstrap(91125);
        campaign.Date = new GameDate(2024, 1, 31) { Hour = 23 };
        campaign.Defines.HoursPerTick = 1;
        campaign.IsPaused = true;
        using var runner = new BackgroundSimRunner();
        runner.Start(campaign);

        // Hold at the exact point that used to make the displayed date oscillate.
        // The worker is paused, so bounded slices can put the world there deterministically.
        campaign.AccessGate.EnterWrite();
        try
        {
            campaign.IsPaused = false;
            for (var slice = 0; slice < 64 && campaign.Date.Day == 31; slice++)
                campaign.StepFor(0.0);
            Check(campaign.MidStep && campaign.Date.Month == 2 && campaign.Date.Day == 1,
                "the live staged world reaches midnight before completing the step");
            var shown = runner.ReadPresentationDate(campaign);
            Check(shown.Month == 1 && shown.Day == 31 && shown.Hour == 23,
                "a staged midnight cannot leak its unfinished date to the display");
        }
        finally { campaign.AccessGate.ExitWrite(); }

        var readable = campaign.TryEnterRead(0, requestWindowOnMiss: false);
        Check(readable, "the UI can read between slices while midnight is in progress");
        try
        {
            Check(runner.ReadPresentationDate(campaign).Day == 31,
                "acquiring a read between slices gives the same displayed date as the lock-free path");
        }
        finally { if (readable) campaign.ExitRead(); }

        var previousSnapshot = campaign.RenderSnapshot;
        runner.RunWrite(c => c.IsPaused = true, refreshSnapshot: false);
        var committed = runner.ReadPresentationDate(campaign);
        Check(!campaign.MidStep && committed.Month == 2 && committed.Day == 1 && committed.Hour == 0 &&
              ReferenceEquals(previousSnapshot, campaign.RenderSnapshot),
            "finishing the step publishes midnight without rebuilding the snapshot");

        var year = runner.RunWrite(c =>
        {
            c.Date = new GameDate(-299, 12, 31) { Hour = 22 };
            return c.Date.Year;
        }, refreshSnapshot: false);
        var ancient = runner.ReadPresentationDate(campaign);
        Check(year == -299 && ancient.Year == year && ancient.Month == 12 && ancient.Day == 31 && ancient.Hour == 22,
            "the atomic clock preserves a signed year and the complete calendar after a generic write");

        var another = Campaign.CreateBootstrap(91126);
        another.Date = new GameDate(1337, 4, 1);
        another.IsPaused = true;
        runner.Start(another);
        Check(runner.ReadPresentationDate(another).Year == 1337,
            "a new campaign never inherits the previous campaign's displayed date");
    }

    private static void RunQueuedSaveTests()
    {
        var campaign = Campaign.CreateBootstrap(91128);
        var region = campaign.States.First(s => s.NationMapIndex == campaign.Player.MapIndex);
        var states = Enumerable.Repeat((ushort)region.MapIndex, 128).ToArray();
        var owners = Enumerable.Repeat((ushort)campaign.Player.MapIndex, states.Length).ToArray();
        campaign.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 8, _ => false));
        campaign.Date = new GameDate(2024, 1, 31) { Hour = 23 };
        campaign.Defines.HoursPerTick = 1;
        campaign.IsPaused = true;

        var folder = Path.Combine(Path.GetTempPath(), "dof-queued-save-" + Guid.NewGuid().ToString("N"));
        using var runner = new BackgroundSimRunner { MobilePacing = true };
        runner.Start(campaign);
        Task<SaveSystem.PreparedSave> pending;
        campaign.AccessGate.EnterWrite();
        try
        {
            campaign.IsPaused = false;
            for (var slice = 0; slice < 64 && campaign.Date.Day == 31; slice++) campaign.StepFor(0.0);
            campaign.IsPaused = true;
            Check(campaign.MidStep && runner.ReadPresentationDate(campaign).Day == 31,
                "the save fixture suspends a midnight step before publishing its date");
            pending = runner.QueueSavePreparation(campaign, folder, SaveSystem.AutoSlot, "", "", autosave: true);
            Check(!pending.IsCompleted, "queuing a save returns without waiting on the campaign gate");
            var duplicate = runner.QueueSavePreparation(campaign, folder, SaveSystem.AutoSlot, "", "", autosave: true);
            Check(duplicate.Exception?.InnerException is InvalidOperationException,
                "a second request cannot overtake preparation for the first save");
        }
        finally { campaign.AccessGate.ExitWrite(); }

        var prepared = pending.WaitAsync(TimeSpan.FromSeconds(15)).GetAwaiter().GetResult();
        var capturedDate = runner.ReadPresentationDate(campaign);
        Check(!campaign.MidStep && campaign.IsPaused && capturedDate.Month == 2 && capturedDate.Day == 1 &&
              capturedDate.Hour == 0 && prepared.Metadata.IsAutosave &&
              prepared.Metadata.GameDate == capturedDate.ToString() && prepared.Metadata.DisplayName.EndsWith(capturedDate.Display()),
            "background preparation finishes a paused midnight and uses the committed date for its save");
        runner.RunWrite(c => c.Date = new GameDate(2024, 2, 2), refreshSnapshot: false);
        prepared.Commit();
        var saved = SaveSystem.Load(folder, SaveSystem.AutoSlot, "");
        Check(saved.Success && saved.Campaign.Date.Day == 1 && runner.ReadPresentationDate(campaign).Day == 2,
            "disk writing uses the detached snapshot even after the live campaign advances");

        Check(campaign.TryEnterRead(0, requestWindowOnMiss: false), "the save fixture holds a read window during shutdown");
        try
        {
            var abandoned = runner.QueueSavePreparation(campaign, folder, "abandoned", "", "", autosave: true);
            runner.Stop();
            Check(abandoned.IsCanceled && !runner.IsActive && SaveSystem.List(folder).Count == 1,
                "stopping a campaign cancels a snapshot waiting for the gate without creating a new save");
        }
        finally { campaign.ExitRead(); }

        campaign.Commands.Enqueue(new FailingSaveStepCommand());
        campaign.Speed = 5;
        campaign.IsPaused = false;
        runner.Start(campaign);
        Exception stepFault = null;
        Check(SpinWait.SpinUntil(() =>
        {
            runner.DrainMainThreadUpdates(out _, out _, out _, out stepFault);
            return stepFault is not null;
        }, TimeSpan.FromSeconds(15)), "the test drains a failed step before the UI blocks the campaign");
        var afterFault = runner.QueueSavePreparation(campaign, folder, "after_fault", "", "", autosave: true);
        Check(SpinWait.SpinUntil(() => afterFault.IsCompleted, TimeSpan.FromSeconds(15)) &&
              ReferenceEquals(afterFault.Exception?.InnerException?.InnerException, stepFault) && SaveSystem.List(folder).Count == 1,
            "draining a fault for display cannot let a save capture the failed campaign");
    }

    private sealed class FailingSaveStepCommand : ICampaignCommand
    {
        public int IssuerId => -1;
        public void Apply(Campaign campaign) => throw new InvalidOperationException("Failed step before a queued save");
    }

    private static void RunConcurrentCatalogReloadSafetyTests()
    {
        SoundCatalog.Load(force: false, notify: false);
        MusicCatalog.Load(force: false, notify: false);
        SpriteCatalog.Load(force: false, notify: false);
        FontCatalog.Load(force: false, notify: false);

        var unexpectedNotifications = 0;
        Action onChanged = () => Interlocked.Increment(ref unexpectedNotifications);
        SoundCatalog.Changed += onChanged;
        MusicCatalog.Changed += onChanged;
        SpriteCatalog.Changed += onChanged;
        FontCatalog.Changed += onChanged;

        try
        {
            var failures = new ConcurrentQueue<Exception>();
            using var startGate = new ManualResetEventSlim(false);
            var writersRemaining = 2;

            var readers = Enumerable.Range(0, 4).Select(readerIndex => Task.Run(() =>
            {
                startGate.Wait();
                while (Volatile.Read(ref writersRemaining) > 0)
                {
                    try
                    {
                        var ch = SoundCatalog.GetChannel("music");
                        _ = ch.VolumeDb;
                        _ = SoundCatalog.Count;
                        _ = SoundCatalog.Has("click");
                        _ = SoundCatalog.Get("click");
                        foreach (var bed in SoundCatalog.AmbienceBeds) break;

                        var menu = MusicCatalog.For(MusicCatalog.Moment.Menu);
                        var campaign = MusicCatalog.For(MusicCatalog.Moment.Campaign, MusicMood.Calm);
                        _ = menu.Count + campaign.Count;
                        _ = MusicCatalog.Director.FadeSeconds;
                        _ = MusicCatalog.Revision;

                        _ = SpriteCatalog.Count;
                        _ = SpriteCatalog.Has("panel_dossier");
                        _ = SpriteCatalog.TextureOf("panel_dossier");
                        _ = SpriteCatalog.ShaderOf("globe");
                        foreach (var spriteName in SpriteCatalog.Names) break;

                        _ = FontCatalog.Count;
                        _ = FontCatalog.FileOf("ui.default");
                        _ = FontCatalog.FamiliesOf("ui.default");
                        foreach (var font in FontCatalog.All) break;
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                        break;
                    }
                }
            })).ToArray();

            var writers = Enumerable.Range(0, 2).Select(writerIndex => Task.Run(() =>
            {
                startGate.Wait();
                try
                {
                    for (var i = 0; i < 25; i++)
                    {
                        SoundCatalog.Load(force: true, notify: false);
                        MusicCatalog.Load(force: true, notify: false);
                        SpriteCatalog.Load(force: true, notify: false);
                        FontCatalog.Load(force: true, notify: false);
                    }
                }
                catch (Exception ex)
                {
                    failures.Enqueue(ex);
                }
                finally
                {
                    Interlocked.Decrement(ref writersRemaining);
                }
            })).ToArray();

            startGate.Set();
            Task.WaitAll(writers.Concat(readers).ToArray());

            Check(failures.IsEmpty,
                $"Concurrent 60Hz catalog reads survive 50 forced reloads with 0 exceptions (errors={failures.Count})");
            Check(unexpectedNotifications == 0,
                $"notify:false suppresses Changed events across 50 forced reloads (events={unexpectedNotifications})");
            Check(SoundCatalog.Count > 0 && MusicCatalog.Count > 0 && SpriteCatalog.Count >= 500 && FontCatalog.Count >= 0,
                $"All 4 catalogs remain coherent after concurrent reload stress (sound={SoundCatalog.Count}, music={MusicCatalog.Count}, sprite={SpriteCatalog.Count}, font={FontCatalog.Count})");
        }
        finally
        {
            SoundCatalog.Changed -= onChanged;
            MusicCatalog.Changed -= onChanged;
            SpriteCatalog.Changed -= onChanged;
            FontCatalog.Changed -= onChanged;
        }
    }

    private static void RunCombatAndMovementArrowTests()
    {
        var root = FindRepoRoot();

        var badgeSource = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "SurfaceBattleBadge.cs"));
        Check(!badgeSource.Contains("_plateMaterial?.Dispose()", StringComparison.Ordinal) &&
              !badgeSource.Contains("_ownedTexture?.Dispose()", StringComparison.Ordinal) &&
              !badgeSource.Contains("new SubViewport", StringComparison.Ordinal),
            "BattleBadgeGpu eliminates per-battle SubViewports and avoids manual RefCounted Dispose()");
        Check(badgeSource.Contains("public static class BattleBadgeGpu", StringComparison.Ordinal) &&
              badgeSource.Contains("CreateSharedMaterial", StringComparison.Ordinal),
            "BattleBadgeGpu renders battle medallions through a single shared GPU-instanced material");

        var bordersSource = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.BordersAndOverlays.cs"));
        Check(bordersSource.Contains("FindActiveBattle", StringComparison.Ordinal) &&
              bordersSource.Contains("_drawnRouteCorridors", StringComparison.Ordinal) &&
              bordersSource.Contains("PostBattleWaypoints", StringComparison.Ordinal),
            "GlobePresentation.BordersAndOverlays restores stylish selected attacker routes while deduplicating corridors");

        var vfxSource = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeBattleVfx.cs"));
        Check(vfxSource.Contains("_battleRibbonBatch", StringComparison.Ordinal) &&
              vfxSource.Contains("_routeMaterials", StringComparison.Ordinal) &&
              !vfxSource.Contains("BattleAssaultArrowShader", StringComparison.Ordinal) &&
              !vfxSource.Contains("_battleClash", StringComparison.Ordinal) &&
              vfxSource.Contains("CurrentBattleArrowZoomBucket()", StringComparison.Ordinal),
            "GlobeBattleVfx uses _battleRibbonBatch and _routeMaterials with no BattleAssaultArrowShader or _battleClash");

        var militarySource = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "MilitaryPresentation.cs"));
        Check(militarySource.Contains("_battleOriginScratch", StringComparison.Ordinal) &&
              militarySource.Contains("_selectedBattleOriginScratch", StringComparison.Ordinal) &&
              militarySource.Contains("_selectedArmyIds", StringComparison.Ordinal) &&
              militarySource.Contains("_lastBattleSelectedHash", StringComparison.Ordinal) &&
              militarySource.Contains("RecordBattleAssaultArrow(", StringComparison.Ordinal),
            "MilitaryPresentation skips selected player attacker provinces when feeding battle arrows");

        RunCampaignIntelConcurrencyTests();
        RunMobileAdaptiveStepHoursAndMidnightAlignmentTests();
        RunPriorityReaderPreemptionTests();
        RunDesktopAdaptiveStepPacingTests();
        RunMobileBigLittleParallelDeterminismTests();
    }

    private static void RunCampaignIntelConcurrencyTests()
    {
        var campaign = Campaign.CreateBootstrap(91130);
        var nationA = campaign.Player ?? campaign.Nations[0];
        var nationB = campaign.Nations.First(n => n.Id != nationA.Id && n.RetiredDay < 0);
        var states = new ushort[128];
        var owners = new ushort[128];
        var regionA = campaign.States.First(s => s.NationMapIndex == nationA.MapIndex);
        var regionB = campaign.States.FirstOrDefault(s => s.NationMapIndex == nationB.MapIndex) ?? regionA;
        for (var i = 0; i < states.Length; i++)
        {
            var isA = i < 64;
            states[i] = (ushort)(isA ? regionA.MapIndex : regionB.MapIndex);
            owners[i] = (ushort)(isA ? nationA.MapIndex : nationB.MapIndex);
        }
        campaign.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 8, _ => false));
        campaign.ObserverMode = true;
        campaign.IsPaused = false;
        campaign.Speed = 5;

        var battle = new LandBattle
        {
            Id = ++campaign.NextBattleId,
            WarId = 0,
            ProvinceId = 10,
            AttackerId = nationA.Id,
            DefenderId = nationB.Id
        };
        campaign.Battles.Add(battle);

        var viewerIds = campaign.Nations.Take(6).Select(n => n.Id).ToArray();
        var mapIndices = campaign.Nations.Take(6).Select(n => n.MapIndex).ToArray();

        // Direct multi-threaded burst on SyncGate (Vision, ProvincesHeldBy, Invalidate, InvalidateActors).
        var burstFailures = new ConcurrentQueue<Exception>();
        Parallel.For(0, 64, i =>
        {
            try
            {
                var controllers = campaign.ProvinceController ?? campaign.ProvinceMap.ProvinceToNation;
                for (var iter = 0; iter < 8; iter++)
                {
                    var viewer = viewerIds[(i + iter) % viewerIds.Length];
                    var mapIdx = mapIndices[(i + iter) % mapIndices.Length];
                    _ = CampaignIntel.Vision(campaign, viewer);
                    _ = CampaignIntel.ProvincesHeldBy(campaign, controllers, mapIdx).Length;
                    if ((iter & 1) == 0) CampaignIntel.InvalidateActors(campaign);
                    else CampaignIntel.Invalidate(campaign);
                }
            }
            catch (Exception ex)
            {
                burstFailures.Enqueue(ex);
            }
        });
        Check(burstFailures.IsEmpty,
            $"Direct multi-threaded CampaignIntel burst completes with 0 exceptions (errors={burstFailures.Count})");

        // Concurrent reader threads hammering CampaignIntel while BackgroundSimRunner steps 15+ days.
        using var runner = new BackgroundSimRunner
        {
            MobilePacing = true,
            MinSpeed5DayIntervalMs = 0.0
        };
        runner.Start(campaign);
        runner.SyncClockState(paused: false, speed: 5);

        var failures = new ConcurrentQueue<Exception>();
        var totalReads = 0;
        var stopReaders = 0;
        var readers = Enumerable.Range(0, 4).Select(workerIdx => Task.Run(() =>
        {
            var step = 0;
            while (Volatile.Read(ref stopReaders) == 0)
            {
                if (campaign.TryEnterRead(0, requestWindowOnMiss: (step & 3) == 0))
                {
                    try
                    {
                        var viewer = viewerIds[(workerIdx + step) % viewerIds.Length];
                        var mapIdx = mapIndices[(workerIdx + step) % mapIndices.Length];
                        var view = CampaignIntel.Vision(campaign, viewer);
                        var controllers = campaign.ProvinceController ?? campaign.ProvinceMap?.ProvinceToNation;
                        if (controllers is not null)
                            _ = CampaignIntel.ProvincesHeldBy(campaign, controllers, mapIdx).Length;
                        if (view is not null && (step & 3) == 0)
                            CampaignIntel.InvalidateActors(campaign);
                        Interlocked.Increment(ref totalReads);
                    }
                    catch (Exception ex)
                    {
                        failures.Enqueue(ex);
                        break;
                    }
                    finally
                    {
                        campaign.ExitRead();
                    }
                }
                step++;
                Thread.Sleep(5);
            }
        })).ToArray();

        var sw = Stopwatch.StartNew();
        while (sw.ElapsedMilliseconds < 15000)
        {
            if (campaign.TryEnterRead(5, requestWindowOnMiss: false))
            {
                var reached = campaign.ElapsedDays >= 15 && !campaign.MidStep;
                campaign.ExitRead();
                if (reached) break;
            }
            Thread.Sleep(15);
        }

        Interlocked.Exchange(ref stopReaders, 1);
        Task.WaitAll(readers);

        runner.RunWrite(c =>
        {
            c.IsPaused = true;
            if (c.MidStep) c.FinishStep();
        });
        runner.Stop();

        Check(campaign.ElapsedDays >= 15,
            $"Live BackgroundSimRunner stepped >= 15 days during concurrent CampaignIntel stress ({campaign.ElapsedDays}d)");
        Check(totalReads > 0 && failures.IsEmpty,
            $"Concurrent CampaignIntel.Vision + ProvincesHeldBy stress completed with 0 exceptions (reads={totalReads}, errors={failures.Count})");
    }

    private static void RunMobileAdaptiveStepHoursAndMidnightAlignmentTests()
    {
        Check(BackgroundSimRunner.MobileAdaptiveStepHours(1) == 1 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(2) == 1 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(3) == 1 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(4) == 2 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(5) == 4 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(6) == 6 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(7) == 12 &&
              BackgroundSimRunner.MobileAdaptiveStepHours(8) == 24,
            "MobileAdaptiveStepHours maps Speed 1..8 to 1h/1h/1h/2h/4h/6h/12h/24h");

        var defines = new Defines { SecondsPerDay = 5f, HoursPerTick = 1 };
        Check(Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 1) - 9.00) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 2) - 4.50) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 3) - 2.25) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 4) - 1.00) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 5) - 0.475) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 6) - 0.21) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 7) - 0.08) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.MobileStepSecondsPerDay(defines, 8) - 0.0) < 1e-9,
            "MobileStepSecondsPerDay returns smooth 8-speed intervals (9.00, 4.50, 2.25, 1.00, 0.475, 0.21, 0.08, 0.0)");

        var campaign = Campaign.CreateBootstrap(91131);
        var region = campaign.States.First(s => s.NationMapIndex == campaign.Player.MapIndex);
        var states = Enumerable.Repeat((ushort)region.MapIndex, 128).ToArray();
        var owners = Enumerable.Repeat((ushort)campaign.Player.MapIndex, states.Length).ToArray();
        campaign.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 8, _ => false));
        campaign.ObserverMode = true;
        campaign.IsPaused = false;
        campaign.Defines.HoursPerTick = 1;
        for (var h = 0; h < 14; h++) campaign.Tick();
        Check(campaign.ElapsedHours == 14 && campaign.Date.Hour == 14 && campaign.ElapsedDays == 0,
            $"Campaign advances to 14:00 cleanly ({campaign.ElapsedHours}h, hour={campaign.Date.Hour})");

        var fpBeforeOverride = CampaignFingerprint.Of(campaign);
        campaign.RuntimeStepHoursOverride = BackgroundSimRunner.MobileAdaptiveStepHours(8);
        var overrideField = typeof(Campaign).GetField("RuntimeStepHoursOverride",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var hasJsonIgnore = overrideField?.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: false).Length == 1;
        Check(hasJsonIgnore && CampaignFingerprint.Of(campaign) == fpBeforeOverride &&
              !SaveSystem.ExportScenarioJson(campaign, "runtime-override-check").Contains("RuntimeStepHoursOverride", StringComparison.Ordinal),
            "RuntimeStepHoursOverride is transient ([JsonIgnore], excluded from CampaignFingerprint and JSON serialization)");
        Check(campaign.TickHours == 10 && campaign.Defines.HoursPerTick == 1,
            $"At 14:00 with Speed-8 override, TickHours aligns to 10h without mutating Defines.HoursPerTick (TickHours={campaign.TickHours})");

        campaign.Tick();
        Check(campaign.ElapsedHours == 24 && campaign.ElapsedDays == 1 && campaign.Date.Hour == 0,
            $"First Speed-8 step from 14:00 advances 10h to 00:00 next day without skipping a day ({campaign.ElapsedHours}h, day={campaign.ElapsedDays}, hour={campaign.Date.Hour})");
        Check(campaign.TickHours == 24,
            $"At 00:00 with Speed-8 override, TickHours expands to 24h (TickHours={campaign.TickHours})");

        campaign.Tick();
        Check(campaign.ElapsedHours == 48 && campaign.ElapsedDays == 2 && campaign.Date.Hour == 0,
            $"Subsequent Speed-8 step advances 24h cleanly ({campaign.ElapsedHours}h, day={campaign.ElapsedDays}, hour={campaign.Date.Hour})");

        campaign.RuntimeStepHoursOverride = BackgroundSimRunner.MobileAdaptiveStepHours(1);
        Check(campaign.TickHours == 1 && campaign.Defines.HoursPerTick == 1,
            $"Switching back to Speed 1 restores 1h steps and keeps Defines.HoursPerTick=1 (TickHours={campaign.TickHours})");

        campaign.Tick();
        Check(campaign.ElapsedHours == 49 && campaign.Date.Hour == 1,
            $"Speed 1 step after switching back advances 1h cleanly ({campaign.ElapsedHours}h, hour={campaign.Date.Hour})");

        // Verify BackgroundSimRunner sets RuntimeStepHoursOverride on MobilePacing and resets it in Stop().
        var bgCampaign = Campaign.CreateBootstrap(91132);
        bgCampaign.ObserverMode = true;
        bgCampaign.IsPaused = false;
        bgCampaign.Defines.HoursPerTick = 1;
        for (var h = 0; h < 14; h++) bgCampaign.Tick();

        using var runner = new BackgroundSimRunner
        {
            MobilePacing = true,
            MinSpeed5DayIntervalMs = 0.0
        };
        runner.Start(bgCampaign);
        runner.SyncClockState(paused: false, speed: 8);
        var waitSw = Stopwatch.StartNew();
        while (waitSw.ElapsedMilliseconds < 5000)
        {
            if (bgCampaign.TryEnterRead(5, true))
            {
                var ok = bgCampaign.ElapsedHours >= 48 && !bgCampaign.MidStep;
                bgCampaign.ExitRead();
                if (ok) break;
            }
            Thread.Sleep(2);
        }
        runner.RunWrite(c =>
        {
            c.IsPaused = true;
            if (c.MidStep) c.FinishStep();
        });
        runner.Stop();

        Check(bgCampaign.ElapsedHours >= 48 && bgCampaign.ElapsedHours % 24 == 0 &&
              bgCampaign.RuntimeStepHoursOverride == 0 && bgCampaign.Defines.HoursPerTick == 1,
            $"MobilePacing BackgroundSimRunner aligns from 14:00 onto midnight steps and Stop() resets RuntimeStepHoursOverride ({bgCampaign.ElapsedHours}h)");
    }

    private static void RunPriorityReaderPreemptionTests()
    {
        var campaign = Campaign.CreateBootstrap(91140);
        var nationA = campaign.Player ?? campaign.Nations[0];
        var nationB = campaign.Nations.First(n => n.Id != nationA.Id && n.RetiredDay < 0);
        var regionA = campaign.States.First(s => s.NationMapIndex == nationA.MapIndex);
        var regionB = campaign.States.FirstOrDefault(s => s.NationMapIndex == nationB.MapIndex) ?? regionA;
        var states = new ushort[128];
        var owners = new ushort[128];
        for (var i = 0; i < states.Length; i++)
        {
            var isA = i < 64;
            states[i] = (ushort)(isA ? regionA.MapIndex : regionB.MapIndex);
            owners[i] = (ushort)(isA ? nationA.MapIndex : nationB.MapIndex);
        }
        campaign.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 8, _ => false));
        campaign.ObserverMode = true;
        campaign.IsPaused = false;
        campaign.Speed = 8;

        using var runner = new BackgroundSimRunner
        {
            AdaptiveStepPacing = true,
            MinSpeed5DayIntervalMs = 0.0
        };
        runner.Start(campaign);
        runner.SyncClockState(paused: false, speed: 8);

        // Warm up at least one live day before racing the lock-stealer.
        var warmSw = Stopwatch.StartNew();
        while (warmSw.ElapsedMilliseconds < 3000)
        {
            if (campaign.TryEnterRead(5, requestWindowOnMiss: true))
            {
                var ready = campaign.ElapsedDays >= 1;
                campaign.ExitRead();
                if (ready) break;
            }
            Thread.Sleep(2);
        }

        // Continuously hammer normal TryEnterRead(0, requestWindowOnMiss: true) on a helper thread,
        // reproducing DealOrForceGame._Process stealing routine read windows before a tap retries.
        var stopStealer = 0;
        var stolenReads = 0;
        var stealer = Task.Run(() =>
        {
            while (Volatile.Read(ref stopStealer) == 0)
            {
                if (campaign.TryEnterRead(0, requestWindowOnMiss: true))
                {
                    try { Interlocked.Increment(ref stolenReads); }
                    finally { campaign.ExitRead(); }
                }
                Thread.Yield();
            }
        });

        try
        {
            for (var probe = 0; probe < 5; probe++)
            {
                Thread.Sleep(5);
                var sw = Stopwatch.StartNew();
                var acquired = false;
                campaign.AccessGate.RequestPriorityRead();
                try
                {
                    acquired = campaign.AccessGate.TryEnterPriorityRead(100);
                    if (acquired)
                    {
                        try { _ = campaign.ElapsedHours; }
                        finally { campaign.ExitRead(); }
                    }
                }
                finally
                {
                    campaign.AccessGate.ClearPriorityRead();
                }
                sw.Stop();
                Check(acquired && sw.ElapsedMilliseconds < 100,
                    $"Priority tap probe {probe + 1}/5 preempts Speed-8 + lock-stealing reader within 100 ms ({sw.ElapsedMilliseconds} ms, acquired={acquired})");
            }
        }
        finally
        {
            Interlocked.Exchange(ref stopStealer, 1);
            stealer.Wait(3000);
        }

        runner.RunWrite(c =>
        {
            c.IsPaused = true;
            if (c.MidStep) c.FinishStep();
        });
        runner.Stop();

        Check(Volatile.Read(ref stolenReads) > 0 && !campaign.AccessGate.HasPriorityReaderRequested &&
              campaign.ElapsedDays >= 1 && !campaign.MidStep && string.IsNullOrEmpty(campaign.SimulationFault),
            $"Priority reader lane stayed clear after preemption stress (stolenReads={stolenReads}, days={campaign.ElapsedDays})");
    }

    private static void RunDesktopAdaptiveStepPacingTests()
    {
        var defaults = new BackgroundSimRunner();
        Check(!defaults.MobilePacing && defaults.AdaptiveStepPacing,
            "BackgroundSimRunner enables AdaptiveStepPacing by default on desktop");

        var expectedHours = new[] { 0, 1, 1, 1, 2, 4, 6, 12, 24 };
        for (var speed = 1; speed <= 8; speed++)
        {
            Check(BackgroundSimRunner.AdaptiveStepHours(speed) == expectedHours[speed],
                $"AdaptiveStepHours({speed}) == {expectedHours[speed]}h");
        }
        var testDefines = new Defines { SecondsPerDay = 5f, HoursPerTick = 1 };
        Check(Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 1, mobilePacing: false) - 12.00) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 2, mobilePacing: false) - 6.00) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 3, mobilePacing: false) - 3.00) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 4, mobilePacing: false) - 1.40) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 5, mobilePacing: false) - 0.65) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 6, mobilePacing: false) - 0.275) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 7, mobilePacing: false) - 0.10) < 1e-4 &&
              Math.Abs(BackgroundSimRunner.AdaptiveStepSecondsPerDay(testDefines, 8, mobilePacing: false) - 0.0) < 1e-9,
            "AdaptiveStepSecondsPerDay desktop pacing table matches 12.00/6.00/3.00/1.40/0.65/0.275/0.10/0.0");

        // Verify RuntimeStepHoursOverride stays [JsonIgnore] and is excluded from saves & fingerprints.
        var overrideField = typeof(Campaign).GetField("RuntimeStepHoursOverride",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic);
        var hasJsonIgnore = overrideField?.GetCustomAttributes(
            typeof(System.Text.Json.Serialization.JsonIgnoreAttribute), inherit: false).Length == 1;
        Check(hasJsonIgnore, "Campaign.RuntimeStepHoursOverride carries [JsonIgnore]");

        // Verify desktop AdaptiveStepPacing steps 1h..24h across Speed 1..8 when Defines.HoursPerTick == 1.
        for (var speed = 1; speed <= 8; speed++)
        {
            var cSpeed = Campaign.CreateBootstrap(91141);
            cSpeed.ObserverMode = true;
            cSpeed.IsPaused = false;
            cSpeed.Defines.HoursPerTick = 1;
            cSpeed.Defines.SecondsPerDay = 0.05f;
            cSpeed.Speed = speed;
            var expectedStep = expectedHours[speed];
            using var speedRunner = new BackgroundSimRunner
            {
                MobilePacing = false,
                AdaptiveStepPacing = true,
                MinSpeed5DayIntervalMs = 0.0
            };
            speedRunner.Start(cSpeed);
            speedRunner.SyncClockState(paused: false, speed: speed);
            var wait = Stopwatch.StartNew();
            while (wait.ElapsedMilliseconds < 4000)
            {
                if (cSpeed.TryEnterRead(5, requestWindowOnMiss: false))
                {
                    var done = cSpeed.ElapsedHours >= expectedStep && !cSpeed.MidStep;
                    cSpeed.ExitRead();
                    if (done) break;
                }
                Thread.Sleep(2);
            }
            speedRunner.RunWrite(c =>
            {
                c.IsPaused = true;
                if (c.MidStep) c.FinishStep();
            });
            var activeOverride = cSpeed.RuntimeStepHoursOverride;
            speedRunner.Stop();
            Check(cSpeed.ElapsedHours >= expectedStep && cSpeed.ElapsedHours % expectedStep == 0 &&
                  activeOverride == expectedStep && cSpeed.RuntimeStepHoursOverride == 0 &&
                  cSpeed.Defines.HoursPerTick == 1,
                $"Desktop AdaptiveStepPacing Speed {speed} steps in {expectedStep}h multiples (hours={cSpeed.ElapsedHours}, override={activeOverride})");
        }

        // Fingerprint parity: desktop AdaptiveStepPacing at Speed 8 matches explicit Defines.HoursPerTick = 24 stepping.
        var cAdaptive = Campaign.CreateBootstrap(91141);
        cAdaptive.ObserverMode = true;
        cAdaptive.IsPaused = false;
        cAdaptive.Defines.HoursPerTick = 1;
        cAdaptive.Speed = 8;
        var fpBefore = CampaignFingerprint.Of(cAdaptive);
        using (var runner = new BackgroundSimRunner
        {
            MobilePacing = false,
            AdaptiveStepPacing = true,
            MinSpeed5DayIntervalMs = 0.0
        })
        {
            runner.Start(cAdaptive);
            runner.SyncClockState(paused: false, speed: 8);
            var sw = Stopwatch.StartNew();
            while (sw.ElapsedMilliseconds < 5000)
            {
                if (cAdaptive.TryEnterRead(5, requestWindowOnMiss: false))
                {
                    var done = cAdaptive.ElapsedHours >= 72 && !cAdaptive.MidStep;
                    cAdaptive.ExitRead();
                    if (done) break;
                }
                Thread.Sleep(2);
            }
            runner.RunWrite(c =>
            {
                c.IsPaused = true;
                if (c.MidStep) c.FinishStep();
            });
            runner.Stop();
        }

        var cExplicit = Campaign.CreateBootstrap(91141);
        cExplicit.ObserverMode = true;
        cExplicit.IsPaused = false;
        cExplicit.Speed = 8;
        Check(CampaignFingerprint.Of(cExplicit) == fpBefore,
            "Bootstrap fingerprint before desktop adaptive stepping is identical");
        cExplicit.Defines.HoursPerTick = 24;
        while (cExplicit.ElapsedHours < cAdaptive.ElapsedHours)
            cExplicit.Tick();
        cExplicit.Defines.HoursPerTick = 1;

        Check(cAdaptive.ElapsedHours >= 72 && cExplicit.ElapsedHours == cAdaptive.ElapsedHours &&
              CampaignFingerprint.Of(cAdaptive) == CampaignFingerprint.Of(cExplicit),
            $"Desktop AdaptiveStepPacing Speed 8 fingerprint matches explicit HoursPerTick=24 stepping at {cAdaptive.ElapsedHours}h");
    }

    private static void RunMobileBigLittleParallelDeterminismTests()
    {
        var originalCap = DeterministicParallel.MobileBigLittleCap;
        try
        {
            Check(DeterministicParallel.ResolveWorkerCount(200, 16, mobileCap: true, processorCount: 8) == 4 &&
                  DeterministicParallel.ResolveWorkerCount(20, 16, mobileCap: true, processorCount: 8) == 1 &&
                  DeterministicParallel.ResolveWorkerCount(200, 16, mobileCap: false, processorCount: 8) == 7,
                "ResolveWorkerCount caps big.LITTLE mobile workers to 4 and raises minItems to 24");

            // 1. Multi-worker DeterministicParallel.ForRange parity with MobileBigLittleCap false vs true.
            const int itemCount = 512;
            var baseline = new double[itemCount];
            var capped = new double[itemCount];
            DeterministicParallel.MobileBigLittleCap = false;
            var ranUnrestricted = DeterministicParallel.ForRange(0, itemCount, (from, to) =>
            {
                for (var i = from; i < to; i++)
                    baseline[i] = Math.Sin(i * 0.17) * (i + 1) + Math.Sqrt(i + 0.5);
            }, minItems: 16);

            DeterministicParallel.MobileBigLittleCap = true;
            var expectedWorkers = Math.Clamp(Environment.ProcessorCount / 2, 2, 4);
            Check(DeterministicParallel.Workers == expectedWorkers &&
                  DeterministicParallel.EffectiveMinItems(16) == 24 &&
                  DeterministicParallel.EffectiveMinItems(8) == 16 &&
                  DeterministicParallel.EffectiveMinItems(48) == 48,
                $"MobileBigLittleCap=true enforces Workers={expectedWorkers} and EffectiveMinItems(16)=24");

            var ranCapped = DeterministicParallel.ForRange(0, itemCount, (from, to) =>
            {
                for (var i = from; i < to; i++)
                    capped[i] = Math.Sin(i * 0.17) * (i + 1) + Math.Sqrt(i + 0.5);
            }, minItems: 16);

            Check(baseline.SequenceEqual(capped),
                $"DeterministicParallel.ForRange produces bit-identical results under MobileBigLittleCap false vs true (unrestricted={ranUnrestricted}, capped={ranCapped})");

            // 2. Live multi-day campaign tick parity under MobileBigLittleCap false vs true.
            static Campaign CreateParallelCampaign()
            {
                var c = Campaign.CreateBootstrap(91142);
                var nationA = c.Player ?? c.Nations[0];
                var nationB = c.Nations.First(n => n.Id != nationA.Id && n.RetiredDay < 0);
                var regionA = c.States.First(s => s.NationMapIndex == nationA.MapIndex);
                var regionB = c.States.FirstOrDefault(s => s.NationMapIndex == nationB.MapIndex) ?? regionA;
                var states = new ushort[256];
                var owners = new ushort[256];
                for (var i = 0; i < states.Length; i++)
                {
                    var isA = i < 128;
                    states[i] = (ushort)(isA ? regionA.MapIndex : regionB.MapIndex);
                    owners[i] = (ushort)(isA ? nationA.MapIndex : nationB.MapIndex);
                }
                c.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 16, _ => false));
                c.ObserverMode = true;
                c.IsPaused = false;
                c.Defines.HoursPerTick = 24;
                c.Defines.ParallelRegions = true;
                return c;
            }

            DeterministicParallel.MobileBigLittleCap = false;
            var cUnrestricted = CreateParallelCampaign();
            for (var day = 0; day < 5; day++) cUnrestricted.Tick();

            DeterministicParallel.MobileBigLittleCap = true;
            var cMobileCapped = CreateParallelCampaign();
            for (var day = 0; day < 5; day++) cMobileCapped.Tick();

            Check(CampaignFingerprint.Of(cUnrestricted) == CampaignFingerprint.Of(cMobileCapped) &&
                  cUnrestricted.Random.State == cMobileCapped.Random.State,
                "Campaign ticks stay 100% bit-identical with MobileBigLittleCap false vs true");
        }
        finally
        {
            DeterministicParallel.MobileBigLittleCap = originalCap;
        }
        Check(DeterministicParallel.MobileBigLittleCap == OperatingSystem.IsAndroid(),
            "MobileBigLittleCap restored to platform default cleanly");
    }

    private static void RunMobileFirstBootDefaultsTests()
    {
        var tempRoot = Path.Combine(Path.GetTempPath(), "dof_mobile_firstboot_" + Guid.NewGuid().ToString("N"));
        var settingsDir = Path.Combine(tempRoot, "settings");
        var saveDir = Path.Combine(tempRoot, "saves");
        Directory.CreateDirectory(settingsDir);
        Directory.CreateDirectory(saveDir);
        try
        {
            PlayerSettings.UseDirectory(settingsDir);
            var compactAvailable = MapCatalog.IsAvailable(MapCatalog.CompactId);
            Check(compactAvailable, "MapCatalog discovers world_compact before MobileFirstBootDefaults test");

            Check(!MobileFirstBootDefaults.ApplyOnce(isMobilePlatform: false),
                "MobileFirstBootDefaults.ApplyOnce(false) leaves desktop settings untouched");
            Check(!PlayerSettings.Has(MobileFirstBootDefaults.MigrationKey) && !PlayerSettings.Has("map_pack") &&
                  !PlayerSettings.Has("sim_tempo_preset"),
                "Desktop boot writes no mobile default keys");

            Check(MobileFirstBootDefaults.ApplyOnce(isMobilePlatform: true),
                "MobileFirstBootDefaults.ApplyOnce(true) applies once on clean mobile install");
            Check(PlayerSettings.Bool(MobileFirstBootDefaults.MigrationKey, false) &&
                  PlayerSettings.Text("map_pack", "") == MapCatalog.CompactId &&
                  PlayerSettings.Text("sim_tempo_preset", "") == "fast_mobile" &&
                  PlayerSettings.Text("hours_per_tick", "") == "12" &&
                  PlayerSettings.Text("economy_tick_days", "") == "14" &&
                  PlayerSettings.Text("ai_calm_cadence", "") == "30",
                "Mobile first boot selects world_compact and fast_mobile (12h / 14d eco / 30d AI)");

            // Switching manually back to Standard World + Detailed must stick permanently across reboots.
            PlayerSettings.Set("map_pack", MapCatalog.StandardId);
            PlayerSettings.Set("sim_tempo_preset", "detailed");
            Check(!MobileFirstBootDefaults.ApplyOnce(isMobilePlatform: true),
                "Second mobile boot does not reapply migration");
            Check(PlayerSettings.Text("map_pack", "") == MapCatalog.StandardId &&
                  PlayerSettings.Text("sim_tempo_preset", "") == "detailed",
                "Explicit player map and tempo overrides survive subsequent mobile boots");

            // Existing mobile user who already chose custom map/tempo keeps those values on upgrade.
            var upgradeDir = Path.Combine(tempRoot, "upgrade_settings");
            Directory.CreateDirectory(upgradeDir);
            PlayerSettings.UseDirectory(upgradeDir);
            PlayerSettings.Set("map_pack", MapCatalog.TacticalId);
            PlayerSettings.Set("sim_tempo_preset", "grand_rts");
            Check(!MobileFirstBootDefaults.ApplyOnce(isMobilePlatform: true),
                "Mobile upgrade migration marks applied without overwriting existing map_pack or tempo");
            Check(PlayerSettings.Text("map_pack", "") == MapCatalog.TacticalId &&
                  PlayerSettings.Text("sim_tempo_preset", "") == "grand_rts",
                "Pre-existing mobile player choices remain world_tactical + grand_rts");

            // Boot map resolution switches to save's MapId (e.g., legacy world_standard WW4) when Continue/Load is used.
            var legacyCampaign = Campaign.CreateBootstrap(777);
            var nA = legacyCampaign.Player ?? legacyCampaign.Nations[0];
            var rA = legacyCampaign.States.First(s => s.NationMapIndex == nA.MapIndex);
            var states = new ushort[256];
            var owners = new ushort[256];
            Array.Fill(states, (ushort)rA.MapIndex);
            Array.Fill(owners, (ushort)nA.MapIndex);
            legacyCampaign.AttachProvinceTopology(ProvinceTopology.Build(states, owners, 16, 16, _ => false));
            legacyCampaign.MapId = MapCatalog.StandardId;
            var meta = SaveSystem.Save(saveDir, "ww4_legacy", legacyCampaign, "", "WW4 Legacy");
            Check(meta is not null && SaveSystem.List(saveDir).Any(s => s.Slot == "ww4_legacy"),
                "Created temporary legacy world_standard save fixture");
            Check(MobileFirstBootDefaults.ResolveBootMapId(MapCatalog.CompactId, loadLatest: false, loadSlot: null, saveDir) == MapCatalog.CompactId,
                "New Campaign keeps configured world_compact on mobile");
            Check(MobileFirstBootDefaults.ResolveBootMapId(MapCatalog.CompactId, loadLatest: true, loadSlot: null, saveDir) == MapCatalog.StandardId,
                "Continue / LoadLatest resolves legacy save's world_standard prior to globe preload");
            Check(MobileFirstBootDefaults.ResolveBootMapId(MapCatalog.CompactId, loadLatest: false, loadSlot: "ww4_legacy", saveDir) == MapCatalog.StandardId,
                "Archive LoadSlot resolves target save's world_standard prior to globe preload");
        }
        finally
        {
            PlayerSettings.UseDirectory(null);
            try { if (Directory.Exists(tempRoot)) Directory.Delete(tempRoot, recursive: true); }
            catch { }
        }
    }

    private static void RunSupportMarkerNoFlickerAndReadabilityTests()
    {
        var root = FindRepoRoot();
        var supportLayout = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeSupportLayout.cs"));
        Check(supportLayout.Contains("AlphaCut=Discard", StringComparison.Ordinal) &&
              !supportLayout.Contains("AlphaCutMode.Disabled", StringComparison.Ordinal) &&
              supportLayout.Contains(".72f,.75f,.78f,1f", StringComparison.Ordinal) &&
              supportLayout.Contains("LayoutPosition", StringComparison.Ordinal) &&
              supportLayout.Contains("HasLayoutPosition", StringComparison.Ordinal) &&
              supportLayout.Contains("KeepOrFindSupportOffset", StringComparison.Ordinal),
            "GlobeSupportLayout eliminates AlphaCut switching flicker, keeps opaque faded tint, and applies layout hysteresis");

        var supportPres = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeSupportPresentation.cs"));
        Check(supportPres.Contains("AlphaCutMode.Discard", StringComparison.Ordinal) &&
              !supportPres.Contains("AlphaCutMode.Disabled", StringComparison.Ordinal) &&
              supportPres.Contains("RenderPriority = 6", StringComparison.Ordinal) &&
              supportPres.Contains("NoDepthTest = true", StringComparison.Ordinal) &&
              supportPres.Contains("FontSize = 40", StringComparison.Ordinal) &&
              supportPres.Contains("ToNum(marker.Node.GlobalPosition)", StringComparison.Ordinal),
            "GlobeSupportPresentation uses sorted depth bias, crisp readiness label, and GlobalPosition horizon visibility");

        var battleMap = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "BattleMapPresentation.cs"));
        Check(battleMap.Contains("dy = -4; dy <= 4", StringComparison.Ordinal) &&
              !battleMap.Contains("dy = -7; dy <= 7", StringComparison.Ordinal),
            "BattleMapPresentation packs compact status shield/pips into top header so DivisionCount is unobstructed");

        var unitsMarkers = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.UnitsAndMarkers.cs"));
        var lockFreeSync = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeLockFreeSync.cs"));
        Check(unitsMarkers.Contains("FontSize = 44", StringComparison.Ordinal) &&
              unitsMarkers.Contains("strategic ? 58 : 44", StringComparison.Ordinal) &&
              lockFreeSync.Contains("FontSize = 44", StringComparison.Ordinal) &&
              lockFreeSync.Contains("TextureFilter = BaseMaterial3D.TextureFilterEnum.Linear", StringComparison.Ordinal),
            "Globe army stack counters use 2x supersampled crisp typography (44/58px) and smooth condition bars");

        var pickerWorld = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "CountryPickerWorldSetupUI.cs"));
        var pickerUi = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "CountrySelectionUI.cs"));
        var panelsJson = File.ReadAllText(Path.Combine(root, "game", "gui", "00_panels.json"));
        var pickerActionsLayout = InterfaceSkin.Layout("picker.actions");
        var pickerActionsWidth = pickerActionsLayout.OffsetRight - pickerActionsLayout.OffsetLeft;
        Check(pickerWorld.Contains("PickerWorldFormatCard", StringComparison.Ordinal) &&
              pickerWorld.Contains("PickerWorldFormatDrawer", StringComparison.Ordinal) &&
              pickerWorld.Contains("PickerWorldFormatToggle", StringComparison.Ordinal) &&
              pickerWorld.Contains("Visible = false", StringComparison.Ordinal) &&
              pickerWorld.Contains("QueuePickerActionsLayout()", StringComparison.Ordinal) &&
              !pickerWorld.Contains("new Color(", StringComparison.Ordinal) &&
              pickerWorld.Contains("PersistTempoSelection(", StringComparison.Ordinal) &&
              pickerWorld.Contains("keptSettings", StringComparison.Ordinal) &&
              pickerWorld.Contains("PlayerSettings.Set(\"sim_tempo_preset\"", StringComparison.Ordinal) &&
              pickerWorld.Contains("PlayerSettings.Set(\"hours_per_tick\"", StringComparison.Ordinal) &&
              !pickerUi.Contains("new Color(", StringComparison.Ordinal) &&
              pickerUi.Contains("new HBoxContainer { Name = \"PickerActionsFlow\"", StringComparison.Ordinal) &&
              pickerUi.Contains("CollapsePickerWorldFormat()", StringComparison.Ordinal) &&
              pickerActionsWidth > 0f && pickerActionsWidth <= 620f &&
              panelsJson.Contains("\"picker.actions\"", StringComparison.Ordinal),
            $"CountryPickerWorldSetupUI defaults to compact 1-line collapsed brass World Format bar with drawer toggle, palette tokens, Tempo persistence, single-row action bar, and compact width ({pickerActionsWidth:0}px <= 620px)");
    }

    private static void RunS22HotfixRegressionTests()
    {
        var root = FindRepoRoot();
        var previousMap = MapCatalog.ActiveMapId;
        var tempDir = Path.Combine(Path.GetTempPath(), "dof_s22_hotfix_" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(tempDir);
        try
        {
            // 1. Legacy pre-MapId saves (omitting MapId in JSON) resolve by persisted ProvinceCount across List / ResolveBootMapId / Load.
            static void WritePreMapIdSave(string dir, string slot, int provinceCount, DateTime savedUtc)
            {
                var payload = new Dictionary<string, object>
                {
                    ["EmbeddedMetadata"] = new Dictionary<string, object>
                    {
                        ["FormatVersion"] = SaveSystem.FormatVersion,
                        ["Slot"] = slot,
                        ["DisplayName"] = slot,
                        ["NationTag"] = "SAU",
                        ["NationName"] = "Saudi Arabia",
                        ["GameDate"] = "21 March 2025",
                        ["ElapsedDays"] = 445,
                        ["Seed"] = 777,
                        ["SavedUtc"] = savedUtc,
                        ["GameVersion"] = SaveSystem.GameVersion,
                        ["MapDataVersion"] = SaveSystem.MapDataVersion,
                        ["ProvinceCount"] = provinceCount,
                        ["ModFingerprint"] = "",
                        ["Checksum"] = "",
                        ["CompressedBytes"] = 0,
                        ["IsAutosave"] = slot == SaveSystem.AutoSlot
                    },
                    ["FormatVersion"] = SaveSystem.FormatVersion,
                    ["GameVersion"] = SaveSystem.GameVersion,
                    ["MapDataVersion"] = SaveSystem.MapDataVersion,
                    ["ModFingerprint"] = "",
                    ["ProvinceCount"] = provinceCount,
                    ["Seed"] = 777,
                    ["ScenarioId"] = "2024",
                    ["Date"] = new GameDate(2025, 3, 21),
                    ["ElapsedDays"] = 445,
                    ["PlayerId"] = 0,
                    ["Nations"] = new[] { new Nation { Id = 0, MapIndex = 1, Tag = "SAU", Name = "Saudi Arabia", Population = 1_000_000L } },
                    ["States"] = new[] { new ScenarioState { MapIndex = 10, NationMapIndex = 1, NationId = "SAU", Id = "st1", Name = "Riyadh", Population = 1_000_000L } },
                    ["ProvinceController"] = new ushort[provinceCount + 1],
                    ["ProvinceOccupation"] = new float[provinceCount + 1],
                    ["ProvinceOwner"] = new ushort[provinceCount + 1],
                    ["ProvinceState"] = new ushort[provinceCount + 1],
                    ["ScenarioRules"] = new Defines()
                };
                var path = Path.Combine(dir, slot + SaveSystem.Extension);
                var json = JsonSerializer.SerializeToUtf8Bytes(payload, new JsonSerializerOptions { IncludeFields = true });
                using var fs = File.Create(path);
                using var gz = new System.IO.Compression.GZipStream(fs, System.IO.Compression.CompressionLevel.Fastest);
                gz.Write(json);
            }

            var saveDir = Path.Combine(tempDir, "saves");
            Directory.CreateDirectory(saveDir);
            var now = DateTime.UtcNow;
            WritePreMapIdSave(saveDir, "legacy_unknown", 777, now.AddMinutes(-3));
            WritePreMapIdSave(saveDir, "legacy_tactical", 18_493, now.AddMinutes(-2));
            WritePreMapIdSave(saveDir, SaveSystem.AutoSlot, 10_370, now.AddMinutes(-1));

            var listed = SaveSystem.List(saveDir);
            var autoMeta = listed.FirstOrDefault(s => s.Slot == SaveSystem.AutoSlot);
            var tacMeta = listed.FirstOrDefault(s => s.Slot == "legacy_tactical");
            var unkMeta = listed.FirstOrDefault(s => s.Slot == "legacy_unknown");
            Check(autoMeta is not null && autoMeta.MapId == MapCatalog.CompactId &&
                  tacMeta is not null && tacMeta.MapId == MapCatalog.TacticalId &&
                  unkMeta is not null && unkMeta.MapId == MapCatalog.StandardId,
                "SaveSystem.List infers world_compact (10,370) and world_tactical (18,493) for pre-MapId saves and falls back to world_standard otherwise");

            Check(MobileFirstBootDefaults.ResolveBootMapId(MapCatalog.StandardId, loadLatest: true, loadSlot: null, saveDir) == MapCatalog.CompactId &&
                  MobileFirstBootDefaults.ResolveBootMapId(MapCatalog.StandardId, loadLatest: false, loadSlot: "legacy_tactical", saveDir) == MapCatalog.TacticalId,
                "ResolveBootMapId selects world_compact / world_tactical for legacy pre-MapId saves before globe preload");

            MapCatalog.SetActiveMap(MapCatalog.StandardId);
            var compactLoaded = SaveSystem.Load(saveDir, SaveSystem.AutoSlot, "", activeProvinceCount: 34_138);
            Check(compactLoaded.Success && MapCatalog.ActiveMapId == MapCatalog.CompactId &&
                  compactLoaded.Campaign.MapId == MapCatalog.CompactId && compactLoaded.Metadata?.MapId == MapCatalog.CompactId,
                $"SaveSystem.Load switches to world_compact and restores legacy pre-MapId 10,370-province save (warning='{compactLoaded.Warning}')");

            MapCatalog.SetActiveMap(MapCatalog.StandardId);
            var tacticalLoaded = SaveSystem.Load(saveDir, "legacy_tactical", "", activeProvinceCount: 34_138);
            Check(tacticalLoaded.Success && MapCatalog.ActiveMapId == MapCatalog.TacticalId &&
                  tacticalLoaded.Campaign.MapId == MapCatalog.TacticalId && tacticalLoaded.Metadata?.MapId == MapCatalog.TacticalId,
                $"SaveSystem.Load switches to world_tactical and restores legacy pre-MapId 18,493-province save (warning='{tacticalLoaded.Warning}')");

            // 2. SaveLoadUI null-safe _pickerLayer guard & failure-dismiss return to MainMenu (no bare-globe softlock).
            var saveLoadSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "SaveLoadUI.cs"));
            var loadingUiSrc = File.ReadAllText(Path.Combine(root, "Scripts", "ui", "LoadingScreenUI.cs"));
            Check(!saveLoadSrc.Contains("_pickerLayer is { Visible: false }", StringComparison.Ordinal) &&
                  saveLoadSrc.Contains("(_pickerLayer is null || !_pickerLayer.Visible)", StringComparison.Ordinal) &&
                  saveLoadSrc.Contains("(_hudLayer is null || !_hudLayer.Visible)", StringComparison.Ordinal) &&
                  saveLoadSrc.Contains("!_campaignInProgress", StringComparison.Ordinal) &&
                  saveLoadSrc.Contains("onFailureDismissed: ReturnToMainMenuIfNoSurface", StringComparison.Ordinal) &&
                  loadingUiSrc.Contains("onFailureDismissed?.Invoke()", StringComparison.Ordinal),
                "SaveLoadUI + LoadingScreenUI guard null _pickerLayer and return to MainMenu after dismissing a failed MainMenu load");

            // 3. Coarser-map crossing folds route to system.log so errors.log stays strictly 0 B on Compact boot.
            var wasLogging = DiagnosticLogs.Enabled;
            if (!wasLogging)
            {
                var compactLogDir = Path.Combine(tempDir, "logs_compact");
                DiagnosticLogs.Start(compactLogDir);
                try
                {
                    MapCatalog.SetActiveMap(MapCatalog.CompactId);
                    var builtCompact = MapRasterCatalog.TryBuildProvinceTopology(
                        WorldMapSource.States, WorldMapSource.Control, WorldMapSource.Width, WorldMapSource.Height,
                        out var compactMap, out var compactWhy);
                    Check(builtCompact && compactMap is not null, $"Built real world_compact topology ({compactWhy})");
                }
                finally { DiagnosticLogs.Stop(); }
                var compactErrBytes = new FileInfo(Path.Combine(compactLogDir, "errors.log")).Length;
                var compactSysLog = File.ReadAllText(Path.Combine(compactLogDir, "system.log"));
                Check(compactErrBytes == 0 && compactSysLog.Contains("CROSSING_REJECTED", StringComparison.Ordinal),
                    $"world_compact crossing folds leave errors.log strictly 0 B ({compactErrBytes} B) and log to system.log");

                var stdLogDir = Path.Combine(tempDir, "logs_standard");
                DiagnosticLogs.Start(stdLogDir);
                try
                {
                    MapCatalog.SetActiveMap(MapCatalog.StandardId);
                    var tinyStates = new ushort[16];
                    var tinyOwners = new ushort[16];
                    Array.Fill(tinyStates, (ushort)1);
                    Array.Fill(tinyOwners, (ushort)1);
                    var tinyMap = ProvinceTopology.Build(tinyStates, tinyOwners, 4, 4, _ => false);
                    LandCrossings.Attach(tinyMap);
                }
                finally { DiagnosticLogs.Stop(); }
                var stdErrLog = File.ReadAllText(Path.Combine(stdLogDir, "errors.log"));
                Check(stdErrLog.Contains("CROSSING_REJECTED", StringComparison.Ordinal),
                    "world_standard continues logging CROSSING_REJECTED to errors.log");
            }

            // 4. Support/reserve readiness label hides when faded behind an army counter and centers cleanly inside its bay.
            var supportPresSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeSupportPresentation.cs"));
            var supportLayoutSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobeSupportLayout.cs"));
            var unitsMarkersSrc = File.ReadAllText(Path.Combine(root, "Scripts", "gfx", "GlobePresentation.UnitsAndMarkers.cs"));
            Check(supportPresSrc.Contains("SupportReadinessVisible(SupportMapMarker marker)", StringComparison.Ordinal) &&
                  supportPresSrc.Contains("!marker.DisplayFaded", StringComparison.Ordinal) &&
                  supportPresSrc.Contains("Offset = new Vector2(48f, -17f)", StringComparison.Ordinal) &&
                  supportPresSrc.Contains("platePixel * .34f", StringComparison.Ordinal) &&
                  supportLayoutSrc.Contains("SupportReadinessVisible(m)", StringComparison.Ordinal) &&
                  supportLayoutSrc.Contains("!CounterRoomOccupied(room.Grow(2))", StringComparison.Ordinal) &&
                  unitsMarkersSrc.Contains("SupportReadinessVisible(marker)", StringComparison.Ordinal),
                "Support readiness label hides when overlapped/faded behind divisions and fits inside the support card bay");

            // 5. Built-in map.json manifests ship clean UTF-8 Cyrillic (no BOM, no \\uXXXX escapes) and load in MapCatalog.
            foreach (var (mapId, expectedRu) in new[]
                     {
                         (MapCatalog.CompactId, "Компактный мир"),
                         (MapCatalog.TacticalId, "Тактический мир")
                     })
            {
                var manifestPath = Path.Combine(root, "game", "maps", mapId, "map.json");
                var rawBytes = File.ReadAllBytes(manifestPath);
                var hasBom = rawBytes.Length >= 3 && rawBytes[0] == 0xEF && rawBytes[1] == 0xBB && rawBytes[2] == 0xBF;
                var rawText = System.Text.Encoding.UTF8.GetString(rawBytes);
                Check(!hasBom && !rawText.Contains("\\u", StringComparison.Ordinal) && rawText.Contains(expectedRu, StringComparison.Ordinal),
                    $"game/maps/{mapId}/map.json is clean UTF-8 without BOM or \\uXXXX escapes");
                Check(MapCatalog.TryGet(mapId, out var pack) && pack.LocalizedName("russian") == expectedRu,
                    $"MapCatalog loads readable Russian name '{expectedRu}' for {mapId}");
            }
        }
        finally
        {
            MapCatalog.SetActiveMap(previousMap);
            try { if (Directory.Exists(tempDir)) Directory.Delete(tempDir, recursive: true); }
            catch { }
        }
    }

    private static string FindRepoRoot()
    {
        foreach (var seed in new[] { Directory.GetCurrentDirectory(), AppContext.BaseDirectory })
        {
            var dir = new DirectoryInfo(seed);
            while (dir is not null)
            {
                if (File.Exists(Path.Combine(dir.FullName, "DealOrForce.csproj")))
                    return dir.FullName;
                dir = dir.Parent;
            }
        }
        throw new DirectoryNotFoundException("Could not locate DealOrForce.csproj.");
    }
}
