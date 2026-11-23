using System;

namespace DealOrForce.Platform;

/// <summary>
/// Four-mode mobile grand-strategy tap toolbar inspired by touch-first map clients:
/// allows players to issue single-tap orders, draw marquee selection boxes, or lock the map
/// for safe panning without desktop mouse buttons or keyboard modifiers.
/// </summary>
public enum TouchTapMode
{
    /// <summary>Primary selection: tap selects, 1-finger drag pans, long-press (~420ms) issues secondary order.</summary>
    Select = 0,

    /// <summary>Command/Order mode (RMB equivalent): single tap immediately fires secondary order without hold delay.</summary>
    Order = 1,

    /// <summary>Marquee box selection: 1-finger drag draws a selection rectangle; 2-finger pinch/drag still navigates the map.</summary>
    BoxSelect = 2,

    /// <summary>Safe map navigation lock: taps and long-presses are ignored so panning never misclicks armies or provinces.</summary>
    Pan = 3
}

/// <summary>Engine-free normalized screen-space rectangle (Width >= 0, Height >= 0).</summary>
public readonly record struct TouchScreenRect(float X, float Y, float Width, float Height)
{
    public static TouchScreenRect Empty => default;

    public bool IsEmpty => Width <= 0f && Height <= 0f;
    public float Right => X + Width;
    public float Bottom => Y + Height;

    public static TouchScreenRect FromCorners(TouchPoint2D a, TouchPoint2D b)
    {
        var minX = MathF.Min(a.X, b.X);
        var minY = MathF.Min(a.Y, b.Y);
        var maxX = MathF.Max(a.X, b.X);
        var maxY = MathF.Max(a.Y, b.Y);
        return new TouchScreenRect(minX, minY, maxX - minX, maxY - minY);
    }
}

/// <summary>
/// Pure-C# state helper for the 4-mode mobile Tap-Mode toolbar, supporting Decision-1's
/// default one-shot Order mode (auto-returning to Select after an order) with an optional Sticky toggle.
/// </summary>
public sealed class TouchTapModeController
{
    public TouchTapMode ActiveMode { get; private set; } = TouchTapMode.Select;

    /// <summary>
    /// When <c>true</c> (default), issuing an order in <see cref="TouchTapMode.Order"/> automatically
    /// returns to <see cref="TouchTapMode.Select"/> to prevent accidental subsequent orders.
    /// </summary>
    public bool OneShotOrderMode { get; set; } = true;

    public bool IsStickyOrder => ActiveMode == TouchTapMode.Order && !OneShotOrderMode;

    public void SetMode(TouchTapMode mode) => ActiveMode = mode;

    /// <summary>
    /// Tap handler for the Order mode pill: entering Order uses current <see cref="OneShotOrderMode"/>;
    /// tapping Order again while already active toggles between one-shot and sticky order chaining.
    /// </summary>
    public void PressOrderButton()
    {
        if (ActiveMode == TouchTapMode.Order)
        {
            OneShotOrderMode = !OneShotOrderMode;
            return;
        }
        ActiveMode = TouchTapMode.Order;
    }

    /// <summary>Notify the controller that an order tap was dispatched; returns <c>true</c> if mode auto-reset to Select.</summary>
    public bool NotifyOrderDispatched()
    {
        if (ActiveMode == TouchTapMode.Order && OneShotOrderMode)
        {
            ActiveMode = TouchTapMode.Select;
            return true;
        }
        return false;
    }

    public static string LocalizationKey(TouchTapMode mode) => mode switch
    {
        TouchTapMode.Select => "mobile.mode.select",
        TouchTapMode.Order => "mobile.mode.order",
        TouchTapMode.BoxSelect => "mobile.mode.box",
        TouchTapMode.Pan => "mobile.mode.pan",
        _ => "mobile.mode.select"
    };

    public static string ShortLocalizationKey(TouchTapMode mode) => mode switch
    {
        TouchTapMode.Select => "mobile.mode.select.short",
        TouchTapMode.Order => "mobile.mode.order.short",
        TouchTapMode.BoxSelect => "mobile.mode.box.short",
        TouchTapMode.Pan => "mobile.mode.pan.short",
        _ => "mobile.mode.select.short"
    };

    public static string TooltipLocalizationKey(TouchTapMode mode) => mode switch
    {
        TouchTapMode.Select => "mobile.mode.select.tip",
        TouchTapMode.Order => "mobile.mode.order.tip",
        TouchTapMode.BoxSelect => "mobile.mode.box.tip",
        TouchTapMode.Pan => "mobile.mode.pan.tip",
        _ => "mobile.mode.select.tip"
    };
}
