using System;
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
        RunMultiTouchEdgeCaseTests();
        RunTapModeTests();
        RunMobileTextAdapterTests();
        RunTouchModeBarAndLazyBootContractTests();
        RunWaveM5AssetCatalogAndWarmupTests();
        RunWaveM6UiAndTouchContractTests();
        RunBackgroundSimReadToWriteUpgradeTests();
        RunBackgroundSimStagedDeterminismTests();
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

    private static void RunBackgroundSimStagedDeterminismTests()
    {
        var cBg = Campaign.CreateBootstrap(91122);
        cBg.IsPaused = false;
        cBg.Speed = 5;
        using var runner = new BackgroundSimRunner();
        runner.Start(cBg);
        runner.SyncClockState(paused: false, speed: 5);

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
        cSync.Speed = 5;
        while (cSync.ElapsedHours < cBg.ElapsedHours)
            cSync.Tick();

        Check(cSync.ElapsedHours == cBg.ElapsedHours &&
              CampaignFingerprint.Of(cBg) == CampaignFingerprint.Of(cSync),
            $"BackgroundSimRunner staged StepFor matches synchronous Campaign.Tick fingerprint at {cBg.ElapsedHours}h");
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
