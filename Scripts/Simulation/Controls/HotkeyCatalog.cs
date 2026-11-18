using System;
using System.Collections.Generic;
using System.Linq;
using System.Text.Json;

namespace DealOrForce.Simulation.Controls;

public sealed class HotkeyEntry
{
    public string Id = "";
    public string Scope = "campaign";
    public string PhysicalKey = "";
    public bool Ctrl;
    public bool Shift;
    public bool Alt;
    public string Label = "";
    public string Description = "";
    public string Category = "general";
    public string HudTarget = "";
    public string Badge = "";
    public int Order;
    public bool HasVisibleButton = true;

    public string Signature =>
        $"{Scope.ToLowerInvariant()}|{(Ctrl ? "C" : "")}{(Shift ? "S" : "")}{(Alt ? "A" : "")}|{PhysicalKey.ToUpperInvariant()}";

    public string ComboSignature =>
        $"{(Ctrl ? "C" : "")}{(Shift ? "S" : "")}{(Alt ? "A" : "")}|{PhysicalKey.ToUpperInvariant()}";

    public string DisplayCombo =>
        Badge.Length > 0 ? Badge : FormatCombo(PhysicalKey, Ctrl, Shift, Alt);

    public static string FormatCombo(string physicalKey, bool ctrl, bool shift, bool alt)
    {
        var parts = new List<string>(4);
        if (ctrl) parts.Add("Ctrl");
        if (alt) parts.Add("Alt");
        if (shift) parts.Add("Shift");
        parts.Add(PrettyKey(physicalKey));
        return string.Join("+", parts);
    }

    public static string PrettyKey(string key) => (key ?? "") switch
    {
        "Period" => ".",
        "Comma" => ",",
        "Slash" => "/",
        "Space" => "Space",
        "Escape" => "Esc",
        "Delete" => "Del",
        "Enter" => "Enter",
        "Equal" => "+",
        "Minus" => "−",
        "Left" => "←",
        "Right" => "→",
        "Up" => "↑",
        "Down" => "↓",
        "Key0" => "0",
        "Key1" => "1",
        "Key2" => "2",
        "Key3" => "3",
        "Key4" => "4",
        "Key5" => "5",
        "Key6" => "6",
        "Key7" => "7",
        "Key8" => "8",
        "Key9" => "9",
        _ => key ?? ""
    };
}

public readonly record struct HotkeyCollision(string FirstId, string SecondId, string ScopePair, string Combo);

/// <summary>
/// Canonical physical-layout hotkey catalog loaded from `game/interface/91_controls_hotkeys.json`
/// (and any mod overlays via InterfaceSkin). Maps physical keys identically across QWERTY and Russian JCUKEN.
/// </summary>
public static class HotkeyCatalog
{
    private static readonly Dictionary<string, HotkeyEntry> Entries = new(StringComparer.OrdinalIgnoreCase);

    public static IReadOnlyList<HotkeyEntry> All =>
        Entries.Values.OrderBy(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();

    public static HotkeyEntry Find(string id) =>
        id is not null && Entries.TryGetValue(id, out var entry) ? entry : null;

    public static void Reset() => Entries.Clear();

    public static void Merge(JsonElement element, string context, List<string> errors)
    {
        if (element.ValueKind != JsonValueKind.Object)
        {
            errors?.Add($"{context}: hotkeys must be an object keyed by action id.");
            return;
        }
        foreach (var pair in element.EnumerateObject())
        {
            if (pair.Name.StartsWith("//", StringComparison.Ordinal)) continue;
            if (pair.Value.ValueKind != JsonValueKind.Object) continue;
            if (pair.Value.TryGetProperty("remove", out var rem) && rem.ValueKind == JsonValueKind.True)
            {
                Entries.Remove(pair.Name);
                continue;
            }
            if (!Entries.TryGetValue(pair.Name, out var entry))
                Entries[pair.Name] = entry = new HotkeyEntry { Id = pair.Name };

            foreach (var field in pair.Value.EnumerateObject())
            {
                switch (field.Name.ToLowerInvariant())
                {
                    case "scope": entry.Scope = field.Value.GetString() ?? entry.Scope; break;
                    case "key": entry.PhysicalKey = field.Value.GetString() ?? entry.PhysicalKey; break;
                    case "ctrl": entry.Ctrl = field.Value.ValueKind == JsonValueKind.True; break;
                    case "shift": entry.Shift = field.Value.ValueKind == JsonValueKind.True; break;
                    case "alt": entry.Alt = field.Value.ValueKind == JsonValueKind.True; break;
                    case "label": entry.Label = field.Value.GetString() ?? entry.Label; break;
                    case "description": entry.Description = field.Value.GetString() ?? entry.Description; break;
                    case "category": entry.Category = field.Value.GetString() ?? entry.Category; break;
                    case "hud_target": entry.HudTarget = field.Value.GetString() ?? entry.HudTarget; break;
                    case "badge": entry.Badge = field.Value.GetString() ?? entry.Badge; break;
                    case "order" when field.Value.TryGetInt32(out var ord): entry.Order = ord; break;
                    case "visible_button":
                        entry.HasVisibleButton = field.Value.ValueKind == JsonValueKind.True;
                        break;
                    default:
                        errors?.Add($"{context}: hotkey '{pair.Name}.{field.Name}' is not recognized.");
                        break;
                }
            }
        }
    }

    public static HotkeyEntry ResolveInScope(string scope, string physicalKey, bool ctrl, bool shift, bool alt)
    {
        if (string.IsNullOrEmpty(physicalKey)) return null;
        var norm = NormalizePhysicalKeyName(physicalKey);
        foreach (var entry in Entries.Values)
        {
            if (!string.Equals(entry.Scope, scope, StringComparison.OrdinalIgnoreCase)) continue;
            if (entry.Ctrl != ctrl || entry.Shift != shift || entry.Alt != alt) continue;
            if (string.Equals(entry.PhysicalKey, norm, StringComparison.OrdinalIgnoreCase))
                return entry;
        }
        return null;
    }

    public static HotkeyEntry ResolveActive(string physicalKey, bool ctrl, bool shift, bool alt,
        bool hasArmiesSelected, bool hasFleetSelected, bool freeCameraActive, bool quickAmountFocused)
    {
        if (quickAmountFocused)
        {
            var q = ResolveInScope("quick_amount", physicalKey, ctrl, shift, alt);
            if (q is not null) return q;
        }
        if (freeCameraActive)
        {
            var fc = ResolveInScope("free_camera", physicalKey, ctrl, shift, alt);
            if (fc is not null) return fc;
        }
        var global = ResolveInScope("global", physicalKey, ctrl, shift, alt);
        if (global is not null) return global;
        if (hasArmiesSelected)
        {
            var sel = ResolveInScope("selection", physicalKey, ctrl, shift, alt);
            if (sel is not null) return sel;
        }
        if (hasFleetSelected && !hasArmiesSelected)
        {
            var fleet = ResolveInScope("fleet", physicalKey, ctrl, shift, alt);
            if (fleet is not null) return fleet;
        }
        return ResolveInScope("campaign", physicalKey, ctrl, shift, alt);
    }

    /// <summary>
    /// Enforces 1 key = 1 meaning per scope (and prevents campaign/global vs selection/fleet ambiguity).
    /// </summary>
    public static IReadOnlyList<HotkeyCollision> FindCollisions()
    {
        var list = Entries.Values.OrderBy(e => e.Order).ThenBy(e => e.Id, StringComparer.Ordinal).ToArray();
        var collisions = new List<HotkeyCollision>();

        static bool ScopesOverlap(string a, string b)
        {
            if (string.Equals(a, b, StringComparison.OrdinalIgnoreCase)) return true;
            // Global & Campaign coexist with Selection & Fleet (when not in FreeCamera or QuickAmount).
            bool IsBase(string s) => string.Equals(s, "global", StringComparison.OrdinalIgnoreCase) ||
                                     string.Equals(s, "campaign", StringComparison.OrdinalIgnoreCase);
            bool IsAction(string s) => string.Equals(s, "selection", StringComparison.OrdinalIgnoreCase) ||
                                       string.Equals(s, "fleet", StringComparison.OrdinalIgnoreCase);
            if (IsBase(a) && IsBase(b)) return true;
            if ((IsBase(a) && IsAction(b)) || (IsAction(a) && IsBase(b))) return true;
            return false;
        }

        for (var i = 0; i < list.Length; i++)
        {
            for (var j = i + 1; j < list.Length; j++)
            {
                var first = list[i];
                var second = list[j];
                if (!ScopesOverlap(first.Scope, second.Scope)) continue;
                if (!string.Equals(first.ComboSignature, second.ComboSignature, StringComparison.OrdinalIgnoreCase)) continue;
                collisions.Add(new HotkeyCollision(first.Id, second.Id,
                    $"{first.Scope}+{second.Scope}", first.DisplayCombo));
            }
        }
        return collisions;
    }

    public static string TipWithShortcut(string hotkeyId, string baseTooltip)
    {
        var entry = Find(hotkeyId);
        var tip = Loc.Get(baseTooltip ?? "");
        if (entry is null) return tip;
        var shortcut = Loc.Format("Shortcut: {0}", entry.DisplayCombo);
        return string.IsNullOrEmpty(tip) ? shortcut : tip + "\n\n" + shortcut;
    }

    /// <summary>
    /// Resolves a character or key name from either English QWERTY or Russian JCUKEN
    /// to its canonical physical key name ("Q", "Period", "Slash", etc.).
    /// </summary>
    public static string NormalizePhysicalKeyName(string raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return "";
        var trimmed = raw.Trim();
        if (trimmed.Length == 1)
        {
            var mapped = MapCharToPhysicalKey(trimmed[0]);
            if (mapped.Length > 0) return mapped;
        }
        return trimmed switch
        {
            "." => "Period",
            "," => "Comma",
            "/" => "Slash",
            "+" => "Equal",
            "-" => "Minus",
            "0" => "Key0",
            "1" => "Key1",
            "2" => "Key2",
            "3" => "Key3",
            "4" => "Key4",
            "5" => "Key5",
            "6" => "Key6",
            "7" => "Key7",
            "8" => "Key8",
            "9" => "Key9",
            "Kp0" => "Key0",
            "Kp1" => "Key1",
            "Kp2" => "Key2",
            "Kp3" => "Key3",
            "Kp4" => "Key4",
            "Kp5" => "Key5",
            "Kp6" => "Key6",
            "Kp7" => "Key7",
            "Kp8" => "Key8",
            "Kp9" => "Key9",
            "KpEnter" => "Enter",
            "KpAdd" => "Equal",
            "KpSubtract" => "Minus",
            "Plus" => "Equal",
            _ => trimmed
        };
    }

    /// <summary>
    /// Maps any Russian JCUKEN character (or Latin/punctuation character) to its physical US-ANSI key name.
    /// </summary>
    public static string MapCharToPhysicalKey(char ch) => char.ToLowerInvariant(ch) switch
    {
        'q' or 'й' => "Q",
        'w' or 'ц' => "W",
        'e' or 'у' => "E",
        'r' or 'к' => "R",
        't' or 'е' or 'ё' => "T",
        'y' or 'н' => "Y",
        'u' or 'г' => "U",
        'i' or 'ш' => "I",
        'o' or 'щ' => "O",
        'p' or 'з' => "P",
        'a' or 'ф' => "A",
        's' or 'ы' => "S",
        'd' or 'в' => "D",
        'f' or 'а' => "F",
        'g' or 'п' => "G",
        'h' or 'р' => "H",
        'j' or 'о' => "J",
        'k' or 'л' => "K",
        'l' or 'д' => "L",
        'z' or 'я' => "Z",
        'x' or 'ч' => "X",
        'c' or 'с' => "C",
        'v' or 'м' => "V",
        'b' or 'и' => "B",
        'n' or 'т' => "N",
        'm' or 'ь' => "M",
        'б' or ',' => "Comma",
        'ю' => "Period",
        '.' => "Period", // Physical US '.' (Russian 'ю' lives at Period; physical '/' types '.' in RU, handled via PhysicalKeycode in Godot).
        '/' or '?' => "Slash",
        ' ' => "Space",
        '0' => "Key0",
        '1' => "Key1",
        '2' => "Key2",
        '3' => "Key3",
        '4' => "Key4",
        '5' => "Key5",
        '6' => "Key6",
        '7' => "Key7",
        '8' => "Key8",
        '9' => "Key9",
        _ => ""
    };

    /// <summary>
    /// Formats grouped controls rows from the canonical catalog for the Handbook ("F1") Controls page.
    /// </summary>
    public static IReadOnlyList<(string CategoryTitle, IReadOnlyList<HotkeyEntry> Items)> GroupedForReference()
    {
        var order = new[] { "time", "camera", "screens", "map", "army", "quick_amount", "system" };
        var byCat = Entries.Values
            .GroupBy(e => (e.Category ?? "general").ToLowerInvariant())
            .ToDictionary(g => g.Key, g => (IReadOnlyList<HotkeyEntry>)g.OrderBy(x => x.Order).ToArray(),
                StringComparer.OrdinalIgnoreCase);

        var result = new List<(string, IReadOnlyList<HotkeyEntry>)>();
        foreach (var cat in order)
            if (byCat.TryGetValue(cat, out var items))
                result.Add((CategoryTitle(cat), items));
        foreach (var (cat, items) in byCat.OrderBy(p => p.Key, StringComparer.Ordinal))
            if (!order.Contains(cat, StringComparer.OrdinalIgnoreCase))
                result.Add((CategoryTitle(cat), items));
        return result;
    }

    private static string CategoryTitle(string category) => category switch
    {
        "time" => Loc.Get("Time & speed"),
        "camera" => Loc.Get("Camera & navigation"),
        "screens" => Loc.Get("Campaign screens"),
        "map" => Loc.Get("Map modes & layers"),
        "army" => Loc.Get("Armies, groups & combat"),
        "quick_amount" => Loc.Get("Quick amount & batch undo"),
        "system" => Loc.Get("System & camera modes"),
        _ => Loc.Get(category)
    };
}
