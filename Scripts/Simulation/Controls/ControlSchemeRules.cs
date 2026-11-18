using System;

namespace DealOrForce.Simulation.Controls;

public enum MouseControlScheme
{
    DirectPan,
    BoxSelect
}

public enum MapPointerButton
{
    Left,
    Middle,
    Right
}

public enum LmbReleaseOutcome
{
    None,
    PanEnded,
    BoxArmySelect,
    BoxRegionSelect,
    ClickSelect
}

public enum MmbReleaseOutcome
{
    None,
    PanEnded,
    BoxArmySelect,
    ClickArmySelect
}

public enum RmbReleaseOutcome
{
    None,
    PanEnded,
    IgnoredDrift,
    CommandOrContextMenu,
    AltSplitMove
}

/// <summary>
/// Engine-neutral control scheme configuration, gesture disambiguation (8 px / 120 ms),
/// button drift safety, edge-pan math, and exact 3D globe cursor-anchored zoom solver.
/// </summary>
public static class ControlSchemeRules
{
    public const float DefaultDragThresholdPx = 8f;
    public const double DefaultDragThresholdMs = 120.0;
    public const float EverywhereMaxDriftPx = 8f;
    public const float EdgePanMarginPx = 14f;
    public const float EdgePanSpeedPxPerSec = 720f;

    public static MouseControlScheme ParseMouseScheme(string value) =>
        (value ?? "").Trim().ToLowerInvariant() switch
        {
            "box_select" or "paradox" => MouseControlScheme.BoxSelect,
            _ => MouseControlScheme.DirectPan
        };

    public static MouseControlScheme ActiveMouseScheme =>
        ParseMouseScheme(SettingsCatalog.Chosen("mouse_scheme"));

    public static bool RmbEverywhereEnabled =>
        SettingsCatalog.Of("rmb_everywhere") is null || SettingsCatalog.Flag("rmb_everywhere");

    public static bool EdgePanEnabled =>
        SettingsCatalog.Of("edge_pan") is not null && SettingsCatalog.Flag("edge_pan");

    public static float DragThresholdPx
    {
        get
        {
            if (SettingsCatalog.Of("drag_threshold_px") is null) return DefaultDragThresholdPx;
            var val = SettingsCatalog.Number("drag_threshold_px");
            return float.IsFinite(val) ? Math.Clamp(val, 4f, 24f) : DefaultDragThresholdPx;
        }
    }

    public static double DragThresholdMs
    {
        get
        {
            if (SettingsCatalog.Of("drag_threshold_ms") is null) return DefaultDragThresholdMs;
            var val = SettingsCatalog.Number("drag_threshold_ms");
            return float.IsFinite(val) ? Math.Clamp(val, 60f, 300f) : DefaultDragThresholdMs;
        }
    }

    public static float Distance(float x1, float y1, float x2, float y2)
    {
        var dx = x2 - x1;
        var dy = y2 - y1;
        return MathF.Sqrt(dx * dx + dy * dy);
    }

    /// <summary>True only if the pointer stayed within maxDriftPx between press and release.</summary>
    public static bool IsButtonClickWithinDrift(float pressX, float pressY, float releaseX, float releaseY,
        float maxDriftPx = EverywhereMaxDriftPx) =>
        Distance(pressX, pressY, releaseX, releaseY) <= maxDriftPx;

    /// <summary>
    /// Guards "Do Everywhere" (Shift+LMB always, or RMB when rmb_everywhere is enabled)
    /// against pointer drift across button release.
    /// </summary>
    public static bool ShouldTriggerEverywhere(bool isShiftLmb, bool isRmb, bool rmbEverywhereEnabled,
        float driftPx, float maxDriftPx = EverywhereMaxDriftPx)
    {
        if (driftPx > maxDriftPx) return false;
        return isShiftLmb || (isRmb && rmbEverywhereEnabled);
    }

    public static float ComputeZoomStep(float baseStep, bool shiftPressed, float fineMultiplier = 0.35f) =>
        shiftPressed ? baseStep * fineMultiplier : baseStep;

    public static (float Dx, float Dy) ComputeEdgePanDelta(float mouseX, float mouseY, float viewWidth, float viewHeight,
        float marginPx = EdgePanMarginPx, float speedPxPerSec = EdgePanSpeedPxPerSec, float deltaSec = 0.016f)
    {
        if (viewWidth <= 1f || viewHeight <= 1f || deltaSec <= 0f) return (0f, 0f);
        float dx = 0f, dy = 0f;
        if (mouseX >= 0f && mouseX < marginPx)
            dx = (1f - mouseX / marginPx) * speedPxPerSec * deltaSec;
        else if (mouseX <= viewWidth && mouseX > viewWidth - marginPx)
            dx = -(1f - (viewWidth - mouseX) / marginPx) * speedPxPerSec * deltaSec;

        if (mouseY >= 0f && mouseY < marginPx)
            dy = (1f - mouseY / marginPx) * speedPxPerSec * deltaSec;
        else if (mouseY <= viewHeight && mouseY > viewHeight - marginPx)
            dy = -(1f - (viewHeight - mouseY) / marginPx) * speedPxPerSec * deltaSec;
        return (dx, dy);
    }

    /// <summary>
    /// Computes the camera ray direction in camera local space (camera at (0,0,D) looking toward -Z)
    /// and intersects it with a sphere of radius R centered at the origin.
    /// </summary>
    public static bool TryGlobeRayHitLocal(float pixelX, float pixelY, float viewWidth, float viewHeight,
        float fovDeg, float globeRadius, float distance,
        out (double X, double Y, double Z) normalLocal)
    {
        normalLocal = default;
        if (viewWidth < 1f || viewHeight < 1f || distance <= globeRadius) return false;
        double ndcX = (pixelX / (double)viewWidth - 0.5) * 2.0;
        double ndcY = (0.5 - pixelY / (double)viewHeight) * 2.0;
        double tanHalf = Math.Tan(fovDeg * Math.PI / 360.0);
        double aspect = viewWidth / (double)viewHeight;

        double rx = ndcX * aspect * tanHalf;
        double ry = ndcY * tanHalf;
        double rz = -1.0;
        double rLen = Math.Sqrt(rx * rx + ry * ry + rz * rz);
        rx /= rLen; ry /= rLen; rz /= rLen;

        // Ray origin in camera-local frame is (0, 0, distance).
        double b = distance * rz;
        double c = distance * distance - (double)globeRadius * globeRadius;
        double disc = b * b - c;
        if (disc < 0.0) return false;
        double t = -b - Math.Sqrt(disc);
        if (t < 0.0) return false;

        double hx = rx * t;
        double hy = ry * t;
        double hz = distance + rz * t;
        double hLen = Math.Sqrt(hx * hx + hy * hy + hz * hz);
        if (hLen < 1e-9) return false;
        normalLocal = (hx / hLen, hy / hLen, hz / hLen);
        return true;
    }

    public static (double X, double Y, double Z) OrbitLocalToWorld((double X, double Y, double Z) v,
        double pitch, double yaw)
    {
        // Godot Basis.FromEuler(new Vector3(pitch, yaw, 0)) uses YXZ order: RotateY(yaw) * RotateX(pitch).
        double cp = Math.Cos(pitch), sp = Math.Sin(pitch);
        double x1 = v.X;
        double y1 = v.Y * cp - v.Z * sp;
        double z1 = v.Y * sp + v.Z * cp;

        double cy = Math.Cos(yaw), sy = Math.Sin(yaw);
        double x2 = x1 * cy + z1 * sy;
        double y2 = y1;
        double z2 = -x1 * sy + z1 * cy;
        return (x2, y2, z2);
    }

    /// <summary>
    /// Solves the exact (targetPitch, targetYaw) on the 3D orbit rig so that the geographic surface point
    /// originally under (pixelX, pixelY) at oldDistance remains under (pixelX, pixelY) at newDistance.
    /// </summary>
    public static bool SolveGlobeZoomAnchor(float pixelX, float pixelY, float viewWidth, float viewHeight,
        float fovDeg, float globeRadius, float maxPitch,
        double anchorWorldX, double anchorWorldY, double anchorWorldZ,
        double currentPitch, double currentYaw, float newDistance,
        out float targetPitch, out float targetYaw)
    {
        targetPitch = (float)currentPitch;
        targetYaw = (float)currentYaw;

        if (!TryGlobeRayHitLocal(pixelX, pixelY, viewWidth, viewHeight, fovDeg, globeRadius, newDistance, out var u))
            return false;

        // Solve u.Y * cos(p) - u.Z * sin(p) = anchorWorldY.
        double hypot = Math.Sqrt(u.Y * u.Y + u.Z * u.Z);
        if (hypot < 1e-6) return false;
        double ratio = Math.Clamp(anchorWorldY / hypot, -1.0, 1.0);
        double alpha = Math.Atan2(-u.Z, u.Y);
        double delta = Math.Acos(ratio);

        double p1 = WrapAngle(alpha + delta);
        double p2 = WrapAngle(alpha - delta);
        double chosenP = Math.Abs(WrapAngle(p1 - currentPitch)) <= Math.Abs(WrapAngle(p2 - currentPitch)) ? p1 : p2;
        chosenP = Math.Clamp(chosenP, -maxPitch, maxPitch);

        // v = RotateX(chosenP) * u
        double cp = Math.Cos(chosenP), sp = Math.Sin(chosenP);
        double vx = u.X;
        double vz = u.Y * sp + u.Z * cp;
        double horizSq = vx * vx + vz * vz;
        if (horizSq < 1e-10)
        {
            targetPitch = (float)chosenP;
            return true;
        }

        double chosenYaw = Math.Atan2(anchorWorldX * vz - anchorWorldZ * vx,
            anchorWorldX * vx + anchorWorldZ * vz);

        targetPitch = (float)chosenP;
        targetYaw = (float)chosenYaw;
        return true;
    }

    private static double WrapAngle(double a)
    {
        while (a > Math.PI) a -= 2.0 * Math.PI;
        while (a < -Math.PI) a += 2.0 * Math.PI;
        return a;
    }
}

/// <summary>
/// Stateful click-vs-drag tracker for Direct Pan / Box Select mouse schemes (8 px and 120 ms thresholds).
/// </summary>
public sealed class MapGestureSession
{
    public bool IsPressed { get; private set; }
    public MapPointerButton Button { get; private set; }
    public MouseControlScheme Scheme { get; private set; }
    public float PressX { get; private set; }
    public float PressY { get; private set; }
    public float CurrentX { get; private set; }
    public float CurrentY { get; private set; }
    public double PressTimeMs { get; private set; }
    public float MaxDisplacementPx { get; private set; }
    public bool CtrlAtPress { get; private set; }
    public bool AltAtPress { get; private set; }
    public bool ShiftAtPress { get; private set; }
    public bool PanStarted { get; private set; }
    public bool BoxStarted { get; private set; }
    public bool BoxRegions { get; private set; }

    public void BeginPress(MouseControlScheme scheme, MapPointerButton button, float x, float y, double nowMs,
        bool ctrlPressed, bool altPressed, bool shiftPressed, bool allowRegionBox)
    {
        IsPressed = true;
        Scheme = scheme;
        Button = button;
        PressX = CurrentX = x;
        PressY = CurrentY = y;
        PressTimeMs = nowMs;
        MaxDisplacementPx = 0f;
        CtrlAtPress = ctrlPressed;
        AltAtPress = altPressed;
        ShiftAtPress = shiftPressed;
        PanStarted = false;
        BoxStarted = false;
        BoxRegions = false;

        if (button == MapPointerButton.Left)
        {
            if (ctrlPressed && allowRegionBox)
            {
                BoxRegions = true;
                BoxStarted = true;
            }
            else if (scheme == MouseControlScheme.BoxSelect)
            {
                BoxStarted = true;
            }
        }
        else if (button == MapPointerButton.Middle)
        {
            if (scheme == MouseControlScheme.DirectPan)
                BoxStarted = true;
            else
                PanStarted = true;
        }
    }

    /// <summary>Updates pointer motion and returns true when a pan camera delta should be applied.</summary>
    public bool UpdateMotion(float x, float y, double nowMs, float thresholdPx, double thresholdMs,
        out float panDeltaX, out float panDeltaY)
    {
        panDeltaX = 0f;
        panDeltaY = 0f;
        if (!IsPressed) return false;

        var stepDx = x - CurrentX;
        var stepDy = y - CurrentY;
        CurrentX = x;
        CurrentY = y;

        var disp = ControlSchemeRules.Distance(PressX, PressY, x, y);
        if (disp > MaxDisplacementPx) MaxDisplacementPx = disp;
        var elapsed = Math.Max(0.0, nowMs - PressTimeMs);

        if (Button == MapPointerButton.Right)
        {
            if (!PanStarted && MaxDisplacementPx > thresholdPx && elapsed >= thresholdMs)
            {
                PanStarted = true;
                panDeltaX = stepDx;
                panDeltaY = stepDy;
                return true;
            }
            if (PanStarted)
            {
                panDeltaX = stepDx;
                panDeltaY = stepDy;
                return true;
            }
            return false;
        }

        if (Button == MapPointerButton.Left)
        {
            if (BoxRegions || Scheme == MouseControlScheme.BoxSelect)
            {
                BoxStarted = true;
                return false;
            }
            // Direct Pan scheme: LMB-drag pans/rotates map once drag threshold is crossed.
            if (!PanStarted && MaxDisplacementPx >= thresholdPx)
            {
                PanStarted = true;
                panDeltaX = stepDx;
                panDeltaY = stepDy;
                return true;
            }
            if (PanStarted)
            {
                panDeltaX = stepDx;
                panDeltaY = stepDy;
                return true;
            }
            return false;
        }

        if (Button == MapPointerButton.Middle)
        {
            if (Scheme == MouseControlScheme.DirectPan)
            {
                BoxStarted = true;
                return false;
            }
            if (!PanStarted) PanStarted = true;
            panDeltaX = stepDx;
            panDeltaY = stepDy;
            return true;
        }

        return false;
    }

    public LmbReleaseOutcome ReleaseLeft(float x, float y, float thresholdPx)
    {
        if (!IsPressed || Button != MapPointerButton.Left) return LmbReleaseOutcome.None;
        UpdateDisplacement(x, y);
        var pan = PanStarted;
        var regions = BoxRegions;
        var disp = MaxDisplacementPx;
        var scheme = Scheme;
        Reset();

        if (pan) return LmbReleaseOutcome.PanEnded;
        if (regions) return LmbReleaseOutcome.BoxRegionSelect;
        if (scheme == MouseControlScheme.BoxSelect && disp >= thresholdPx) return LmbReleaseOutcome.BoxArmySelect;
        if (disp < thresholdPx) return LmbReleaseOutcome.ClickSelect;
        return LmbReleaseOutcome.PanEnded;
    }

    public MmbReleaseOutcome ReleaseMiddle(float x, float y, float thresholdPx)
    {
        if (!IsPressed || Button != MapPointerButton.Middle) return MmbReleaseOutcome.None;
        UpdateDisplacement(x, y);
        var pan = PanStarted;
        var disp = MaxDisplacementPx;
        var scheme = Scheme;
        Reset();

        if (pan || scheme == MouseControlScheme.BoxSelect) return MmbReleaseOutcome.PanEnded;
        return disp >= thresholdPx ? MmbReleaseOutcome.BoxArmySelect : MmbReleaseOutcome.ClickArmySelect;
    }

    public RmbReleaseOutcome ReleaseRight(float x, float y, double nowMs, float thresholdPx, double thresholdMs,
        bool altPressedNow)
    {
        if (!IsPressed || Button != MapPointerButton.Right) return RmbReleaseOutcome.None;
        UpdateDisplacement(x, y);
        var elapsed = Math.Max(0.0, nowMs - PressTimeMs);
        var pan = PanStarted || (MaxDisplacementPx > thresholdPx && elapsed >= thresholdMs);
        var disp = MaxDisplacementPx;
        var alt = altPressedNow || AltAtPress;
        Reset();

        if (pan) return RmbReleaseOutcome.PanEnded;
        if (disp > thresholdPx) return RmbReleaseOutcome.IgnoredDrift;
        return alt ? RmbReleaseOutcome.AltSplitMove : RmbReleaseOutcome.CommandOrContextMenu;
    }

    public void Reset()
    {
        IsPressed = false;
        PanStarted = false;
        BoxStarted = false;
        BoxRegions = false;
        MaxDisplacementPx = 0f;
    }

    private void UpdateDisplacement(float x, float y)
    {
        CurrentX = x;
        CurrentY = y;
        var disp = ControlSchemeRules.Distance(PressX, PressY, x, y);
        if (disp > MaxDisplacementPx) MaxDisplacementPx = disp;
    }
}
