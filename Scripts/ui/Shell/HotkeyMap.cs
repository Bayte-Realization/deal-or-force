using System;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Standalone physical keycode / Russian JCUKEN hotkey resolver for the Wave 2 campaign shell.
/// </summary>
internal static class HotkeyMap
{
    public static bool HandleKey(
        ShellContext ctx,
        InputEventKey key,
        Key resolvedPhysicalLetter,
        bool textFocused,
        Func<bool> closeTopmostShellOverlay)
    {
        if (ctx?.World is null) return false;
        var raw = NormalizeKeyName(resolvedPhysicalLetter);
        if (raw.Length == 0) return false;

        if (!ShellHotkeys.TryResolve(
                raw,
                key.CtrlPressed || key.MetaPressed,
                key.AltPressed,
                textFocused,
                ctx.IsFreeCameraMode?.Invoke() ?? false,
                ctx.IsAnyModalOpen?.Invoke() ?? false,
                out var action))
            return false;

        return DispatchScreenOrMap(ctx, action) || DispatchClockOrClose(ctx, action, closeTopmostShellOverlay);
    }

    private static string NormalizeKeyName(Key k) => k switch
    {
        Key.Space => "Space",
        Key.Escape => "Escape",
        Key.Home => "Home",
        Key.Slash => "Slash",
        Key.Equal or Key.Plus or Key.KpAdd => "Equal",
        Key.Minus or Key.KpSubtract => "Minus",
        >= Key.Key0 and <= Key.Key9 => ((char)('0' + (k - Key.Key0))).ToString(),
        >= Key.Kp0 and <= Key.Kp9 => ((char)('0' + (k - Key.Kp0))).ToString(),
        >= Key.A and <= Key.Z => ((char)('A' + (k - Key.A))).ToString(),
        _ => ""
    };

    private static bool DispatchScreenOrMap(ShellContext ctx, ShellHotkeyAction action)
    {
        var screen = action switch
        {
            ShellHotkeyAction.Country => "Nation",
            ShellHotkeyAction.StateAffairs => "State Affairs",
            ShellHotkeyAction.Economy => "Economy",
            ShellHotkeyAction.Trade => "Trade",
            ShellHotkeyAction.Production => "Production",
            ShellHotkeyAction.Research => "Research",
            ShellHotkeyAction.Military => "Military",
            ShellHotkeyAction.Diplomacy => "Diplomacy",
            ShellHotkeyAction.Wars => "Wars",
            ShellHotkeyAction.Ledger => "Ledger",
            _ => ""
        };
        if (screen.Length > 0) { ctx.ToggleScreen?.Invoke(screen); return true; }
        if (action == ShellHotkeyAction.Search) { ctx.ToggleNavigator?.Invoke(); return true; }

        return action switch
        {
            ShellHotkeyAction.MapPolitical => SetMode(ctx, StrategicMapMode.Political),
            ShellHotkeyAction.MapDiplomatic => SetMode(ctx, StrategicMapMode.Diplomatic),
            ShellHotkeyAction.MapEconomy => SetMode(ctx, StrategicMapMode.Economy),
            ShellHotkeyAction.MapResources => SetMode(ctx, StrategicMapMode.Resources),
            ShellHotkeyAction.MapWar => SetMode(ctx, StrategicMapMode.Wars),
            _ => false
        };
    }

    private static bool SetMode(ShellContext ctx, StrategicMapMode mode)
    {
        ctx.SetMapMode?.Invoke(mode);
        return true;
    }

    private static bool DispatchClockOrClose(ShellContext ctx, ShellHotkeyAction action, Func<bool> closeOverlay) => action switch
    {
        ShellHotkeyAction.TogglePause => InvokeTrue(ctx.TogglePause),
        ShellHotkeyAction.Speed1 => SetSpeedTrue(ctx, 1),
        ShellHotkeyAction.Speed2 => SetSpeedTrue(ctx, 2),
        ShellHotkeyAction.Speed3 => SetSpeedTrue(ctx, 3),
        ShellHotkeyAction.Speed4 => SetSpeedTrue(ctx, 4),
        ShellHotkeyAction.Speed5 => SetSpeedTrue(ctx, 5),
        ShellHotkeyAction.SpeedFaster => SetSpeedTrue(ctx, (ctx.GetSpeed?.Invoke() ?? 1) + 1),
        ShellHotkeyAction.SpeedSlower => SetSpeedTrue(ctx, (ctx.GetSpeed?.Invoke() ?? 1) - 1),
        ShellHotkeyAction.FocusCapital => InvokeTrue(ctx.FocusOwnCapital),
        ShellHotkeyAction.MenuOrClose => closeOverlay?.Invoke() ?? false,
        _ => false
    };

    private static bool InvokeTrue(Action fn) { fn?.Invoke(); return true; }
    private static bool SetSpeedTrue(ShellContext ctx, int s) { ctx.SetSpeed?.Invoke(s); return true; }
}
