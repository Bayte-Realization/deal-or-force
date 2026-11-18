using System;
using DealOrForce.Simulation.Shell;
using Godot;

namespace DealOrForce;

/// <summary>
/// Standalone physical keycode / Russian JCUKEN hotkey resolver for the Wave 2 campaign shell.
/// Operates through ShellContext without extending DealOrForceGame.
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

        var raw = resolvedPhysicalLetter switch
        {
            Key.Space => "Space",
            Key.Escape => "Escape",
            Key.Home => "Home",
            Key.Slash => "Slash",
            Key.Equal or Key.Plus or Key.KpAdd => "Equal",
            Key.Minus or Key.KpSubtract => "Minus",
            >= Key.Key0 and <= Key.Key9 => ((char)('0' + (resolvedPhysicalLetter - Key.Key0))).ToString(),
            >= Key.Kp0 and <= Key.Kp9 => ((char)('0' + (resolvedPhysicalLetter - Key.Kp0))).ToString(),
            >= Key.A and <= Key.Z => ((char)('A' + (resolvedPhysicalLetter - Key.A))).ToString(),
            _ => ""
        };

        var modalOpen = ctx.IsAnyModalOpen?.Invoke() ?? false;
        var freeCam = ctx.IsFreeCameraMode?.Invoke() ?? false;

        if (!ShellHotkeys.TryResolve(
                raw,
                key.CtrlPressed || key.MetaPressed,
                key.AltPressed,
                textFocused,
                freeCam,
                modalOpen,
                out var action))
            return false;

        switch (action)
        {
            case ShellHotkeyAction.Country: ctx.ToggleScreen?.Invoke("Nation"); return true;
            case ShellHotkeyAction.StateAffairs: ctx.ToggleScreen?.Invoke("State Affairs"); return true;
            case ShellHotkeyAction.Economy: ctx.ToggleScreen?.Invoke("Economy"); return true;
            case ShellHotkeyAction.Trade: ctx.ToggleScreen?.Invoke("Trade"); return true;
            case ShellHotkeyAction.Production: ctx.ToggleScreen?.Invoke("Production"); return true;
            case ShellHotkeyAction.Research: ctx.ToggleScreen?.Invoke("Research"); return true;
            case ShellHotkeyAction.Military: ctx.ToggleScreen?.Invoke("Military"); return true;
            case ShellHotkeyAction.Diplomacy: ctx.ToggleScreen?.Invoke("Diplomacy"); return true;
            case ShellHotkeyAction.Wars: ctx.ToggleScreen?.Invoke("Wars"); return true;
            case ShellHotkeyAction.Ledger: ctx.ToggleScreen?.Invoke("Ledger"); return true;
            case ShellHotkeyAction.Search: ctx.ToggleNavigator?.Invoke(); return true;

            case ShellHotkeyAction.MapPolitical: ctx.SetMapMode?.Invoke(StrategicMapMode.Political); return true;
            case ShellHotkeyAction.MapDiplomatic: ctx.SetMapMode?.Invoke(StrategicMapMode.Diplomatic); return true;
            case ShellHotkeyAction.MapEconomy: ctx.SetMapMode?.Invoke(StrategicMapMode.Economy); return true;
            case ShellHotkeyAction.MapResources: ctx.SetMapMode?.Invoke(StrategicMapMode.Resources); return true;
            case ShellHotkeyAction.MapWar: ctx.SetMapMode?.Invoke(StrategicMapMode.Wars); return true;

            case ShellHotkeyAction.MapMore:
            case ShellHotkeyAction.ToggleOutliner:
                // Handled by caller which owns view references
                return false;

            case ShellHotkeyAction.TogglePause: ctx.TogglePause?.Invoke(); return true;
            case ShellHotkeyAction.Speed1: ctx.SetSpeed?.Invoke(1); return true;
            case ShellHotkeyAction.Speed2: ctx.SetSpeed?.Invoke(2); return true;
            case ShellHotkeyAction.Speed3: ctx.SetSpeed?.Invoke(3); return true;
            case ShellHotkeyAction.Speed4: ctx.SetSpeed?.Invoke(4); return true;
            case ShellHotkeyAction.Speed5: ctx.SetSpeed?.Invoke(5); return true;
            case ShellHotkeyAction.SpeedFaster: ctx.SetSpeed?.Invoke((ctx.GetSpeed?.Invoke() ?? 1) + 1); return true;
            case ShellHotkeyAction.SpeedSlower: ctx.SetSpeed?.Invoke((ctx.GetSpeed?.Invoke() ?? 1) - 1); return true;
            case ShellHotkeyAction.FocusCapital: ctx.FocusOwnCapital?.Invoke(); return true;

            case ShellHotkeyAction.MenuOrClose:
                return closeTopmostShellOverlay?.Invoke() ?? false;

            default:
                return false;
        }
    }
}
