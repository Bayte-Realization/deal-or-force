using System;
using System.Collections.Generic;

namespace DealOrForce.Simulation.Shell;

public enum ShellHotkeyScope
{
    CampaignMap,
    ModalClosed
}

public enum ShellHotkeyAction
{
    Country,
    StateAffairs,
    Economy,
    Trade,
    Production,
    Research,
    Military,
    Diplomacy,
    Wars,
    Ledger,
    Search,
    MapPolitical,
    MapDiplomatic,
    MapEconomy,
    MapResources,
    MapWar,
    MapMore,
    ToggleOutliner,
    TogglePause,
    Speed1,
    Speed2,
    Speed3,
    Speed4,
    Speed5,
    SpeedFaster,
    SpeedSlower,
    MenuOrClose,
    FocusCapital
}

public sealed record ShellHotkeyBinding(
    ShellHotkeyAction Action,
    string PhysicalKey,
    bool RequireCtrl,
    string DisplayCap,
    ShellHotkeyScope Scope);

/// <summary>
/// Canonical layout-independent hotkey table for the new campaign shell.
/// Maps physical key positions identically across QWERTY and Russian ЙЦУКЕН keyboards
/// without colliding with Controls ergonomics (G merge, . idle cycle, 6..0 army groups, Ctrl+A/Z).
/// </summary>
public static class ShellHotkeys
{
    private static readonly IReadOnlyList<ShellHotkeyBinding> Table = new ShellHotkeyBinding[]
    {
        new(ShellHotkeyAction.Country, "Q", false, "Q", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.StateAffairs, "E", false, "E", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Economy, "R", false, "R", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Trade, "T", false, "T", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Production, "Y", false, "Y", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Research, "U", false, "U", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Military, "I", false, "I", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Diplomacy, "O", false, "O", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Wars, "P", false, "P", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Ledger, "L", false, "L", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Search, "K", true, "Ctrl+K", ShellHotkeyScope.CampaignMap),

        new(ShellHotkeyAction.MapPolitical, "Z", false, "Z", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.MapDiplomatic, "X", false, "X", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.MapEconomy, "C", false, "C", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.MapResources, "V", false, "V", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.MapWar, "N", false, "N", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.MapMore, "M", false, "M", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.ToggleOutliner, "J", false, "J", ShellHotkeyScope.CampaignMap),

        new(ShellHotkeyAction.TogglePause, "Space", false, "Space", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Speed1, "1", false, "1", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Speed2, "2", false, "2", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Speed3, "3", false, "3", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Speed4, "4", false, "4", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.Speed5, "5", false, "5", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.SpeedFaster, "Equal", false, "+", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.SpeedSlower, "Minus", false, "-", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.FocusCapital, "Home", false, "Home", ShellHotkeyScope.CampaignMap),
        new(ShellHotkeyAction.MenuOrClose, "Escape", false, "Esc", ShellHotkeyScope.ModalClosed),
    };

    private static readonly Dictionary<char, string> RussianToPhysical = new()
    {
        ['Й'] = "Q", ['й'] = "Q",
        ['У'] = "E", ['у'] = "E",
        ['К'] = "R", ['к'] = "R",
        ['Е'] = "T", ['е'] = "T",
        ['Н'] = "Y", ['н'] = "Y",
        ['Г'] = "U", ['г'] = "U",
        ['Ш'] = "I", ['ш'] = "I",
        ['Щ'] = "O", ['щ'] = "O",
        ['З'] = "P", ['з'] = "P",
        ['Д'] = "L", ['д'] = "L",
        ['Л'] = "K", ['л'] = "K",
        ['О'] = "J", ['о'] = "J",
        ['Я'] = "Z", ['я'] = "Z",
        ['Ч'] = "X", ['ч'] = "X",
        ['С'] = "C", ['с'] = "C",
        ['М'] = "V", ['м'] = "V",
        ['Т'] = "N", ['т'] = "N",
        ['Ь'] = "M", ['ь'] = "M",
        ['П'] = "G", ['п'] = "G"
    };

    private static readonly HashSet<string> ControlsReservedKeys = new(StringComparer.Ordinal)
    {
        "Ctrl+A", "Ctrl+Shift+A", "Ctrl+Alt+H", "Ctrl+Z", "G", "Period", "6", "7", "8", "9", "0"
    };

    public static IReadOnlyList<ShellHotkeyBinding> All => Table;

    public static string NormalizeKeyName(string rawKey)
    {
        if (string.IsNullOrWhiteSpace(rawKey)) return "";
        var trimmed = rawKey.Trim();
        if (trimmed.Length == 1)
        {
            var ch = trimmed[0];
            if (RussianToPhysical.TryGetValue(ch, out var mapped)) return mapped;
            if (ch is >= 'a' and <= 'z') return char.ToUpperInvariant(ch).ToString();
            if (ch is >= 'A' and <= 'Z' or >= '0' and <= '9') return ch.ToString();
            if (ch is '+' or '=') return "Equal";
            if (ch == '-') return "Minus";
            if (ch == '/') return "Slash";
            if (ch == ' ') return "Space";
        }
        return trimmed switch
        {
            "Add" or "KpAdd" or "Plus" => "Equal",
            "Subtract" or "KpSubtract" => "Minus",
            "Kp1" => "1",
            "Kp2" => "2",
            "Kp3" => "3",
            "Kp4" => "4",
            "Kp5" => "5",
            "Esc" => "Escape",
            _ => trimmed
        };
    }

    public static bool TryResolve(
        string rawKey,
        bool ctrl,
        bool alt,
        bool textInputFocused,
        bool freeCameraActive,
        bool modalBlocking,
        out ShellHotkeyAction action)
    {
        action = default;
        if (textInputFocused || alt) return false;

        var key = NormalizeKeyName(rawKey);
        if (key == "Escape")
        {
            action = ShellHotkeyAction.MenuOrClose;
            return true;
        }
        if (modalBlocking || freeCameraActive) return false;

        if (!ctrl && key == "Slash")
        {
            action = ShellHotkeyAction.Search;
            return true;
        }

        foreach (var entry in Table)
        {
            if (entry.Scope == ShellHotkeyScope.ModalClosed) continue;
            if (entry.RequireCtrl == ctrl && string.Equals(entry.PhysicalKey, key, StringComparison.OrdinalIgnoreCase))
            {
                action = entry.Action;
                return true;
            }
        }
        return false;
    }

    public static IReadOnlyList<string> FindCollisions()
    {
        var issues = new List<string>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var entry in Table)
        {
            var chord = (entry.RequireCtrl ? "Ctrl+" : "") + entry.PhysicalKey;
            if (!seen.Add(chord))
                issues.Add("Duplicate shell hotkey: " + chord);
            if (ControlsReservedKeys.Contains(chord))
                issues.Add("Collides with Controls ergonomics binding: " + chord);
        }
        return issues;
    }
}
