using System;
using System.Text.RegularExpressions;

namespace DealOrForce.Platform;

/// <summary>
/// Pure-C# mobile copy adapter ("тексты под мобилы") that rewrites desktop-centric input copy
/// (Quit to desktop, [F7]/[Space]/[Esc] hotkey badges, Right-click/ПКМ, Left-click/ЛКМ, mouse wheel)
/// into touch-native wording when running on primary-touch devices.
///
/// Desktop guarantee: when <c>isTouchPrimary == false</c>, <see cref="Adapt"/> returns the input
/// string 100% unchanged with zero allocations.
/// </summary>
public static class MobileTextAdapter
{
    public const string OverrideKeyPrefix = "mobile.override.";

    private static readonly Regex BracketedHotkeyRx = new(
        @"\s*\[(?:(?:Ctrl|Shift|Alt)\+)*(?:Space|Esc|Escape|Tab|Enter|Shift|Ctrl|Alt|Del|Delete|Backspace|Пробел|F(?:1[0-2]|[1-9])|[A-Z])\]",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex DotHotkeySuffixRx = new(
        @"\s*[·•]\s*(?:Space|Esc|Escape|WASD|F(?:1[0-2]|[1-9])|Пробел)\b",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex ParenHotkeyRx = new(
        @"\s*\((?:(?:Ctrl|Shift|Alt)\+)*(?:Space|Esc|Escape|Del|Delete|F(?:1[0-2]|[1-9])|Пробел|[A-Z])\)",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly (Regex Pattern, string Replacement)[] PhraseReplacements =
    {
        // Intro skip prompt (EN + RU)
        (Compile(@"Press Esc, Space, Enter or click to skip"), "Tap to skip"),
        (Compile(@"Нажмите Esc, пробел, Enter или кликните, чтобы пропустить"), "Коснитесь экрана, чтобы пропустить"),

        // Quit to desktop (EN + RU)
        (Compile(@"\bQuit to desktop\b"), "Exit game"),
        (Compile(@"\bquit to desktop\b"), "exit game"),
        (Compile(@"\bExit to desktop\b"), "Exit game"),
        (Compile(@"\bexit to desktop\b"), "exit game"),
        (Compile(@"Выход на рабочий стол"), "Выйти из игры"),
        (Compile(@"Выйти на рабочий стол"), "Выйти из игры"),
        (Compile(@"выход на рабочий стол"), "выйти из игры"),
        (Compile(@"выйти на рабочий стол"), "выйти из игры"),

        // Mouse button phrases (EN)
        (Compile(@"\bDouble-click\b"), "Double-tap"),
        (Compile(@"\bdouble-click\b"), "double-tap"),
        (Compile(@"\bRight-click\b"), "Order tap / Hold"),
        (Compile(@"\bright-click\b"), "order tap / hold"),
        (Compile(@"\bRight click\b"), "Order tap / Hold"),
        (Compile(@"\bright click\b"), "order tap / hold"),
        (Compile(@"\bLeft-click\b"), "Tap"),
        (Compile(@"\bleft-click\b"), "tap"),
        (Compile(@"\bLeft click\b"), "Tap"),
        (Compile(@"\bleft click\b"), "tap"),
        (Compile(@"\bClick to\b"), "Tap to"),
        (Compile(@"\bclick to\b"), "tap to"),
        (Compile(@"\bClick a\b"), "Tap a"),
        (Compile(@"\bclick a\b"), "tap a"),
        (Compile(@"\bRight-drag\b"), "Drag"),
        (Compile(@"\bright-drag\b"), "drag"),
        (Compile(@"\bRMB-drag\b"), "Order drag"),
        (Compile(@"\bRMB\b"), "Order tap"),
        (Compile(@"\bLMB\b"), "Tap"),
        (Compile(@"\bHover over\b"), "Tap"),
        (Compile(@"\bhover over\b"), "tap"),
        (Compile(@"\bHover a\b"), "Tap a"),
        (Compile(@"\bhover a\b"), "tap a"),
        (Compile(@"\bMouse wheel\b"), "Pinch"),
        (Compile(@"\bmouse wheel\b"), "pinch"),
        (Compile(@"\bScroll wheel\b"), "Pinch"),
        (Compile(@"\bscroll wheel\b"), "pinch"),
        (Compile(@"\bWheel to zoom\b"), "Pinch to zoom"),
        (Compile(@"\bwheel to zoom\b"), "pinch to zoom"),
        (Compile(@"\bScroll to zoom\b"), "Pinch to zoom"),
        (Compile(@"\bscroll to zoom\b"), "pinch to zoom"),

        // Mouse button phrases (RU)
        (Compile(@"(?<![А-Яа-яЁёA-Za-z0-9_])ПКМ(?![А-Яа-яЁёA-Za-z0-9_])"), "Режим приказа / Удержание"),
        (Compile(@"(?<![А-Яа-яЁёA-Za-z0-9_])ЛКМ(?![А-Яа-яЁёA-Za-z0-9_])"), "Тап"),
        (Compile(@"Двойной клик"), "Двойное касание"),
        (Compile(@"двойной клик"), "двойное касание"),
        (Compile(@"Кликните"), "Коснитесь"),
        (Compile(@"кликните"), "коснитесь"),
        (Compile(@"правой кнопкой мыши"), "в режиме приказа или удержанием"),
        (Compile(@"правой кнопкой"), "в режиме приказа или удержанием"),
        (Compile(@"левой кнопкой мыши"), "касанием"),
        (Compile(@"левой кнопкой"), "касанием"),
        (Compile(@"Наведите курсор"), "Нажмите"),
        (Compile(@"наведите курсор"), "нажмите"),
        (Compile(@"Колёсико"), "Щипок"),
        (Compile(@"колёсико"), "щипок"),
        (Compile(@"Колесико"), "Щипок"),
        (Compile(@"колесико"), "щипок"),
    };

    private static readonly Regex MultiSpaceRx = new(
        @"[ \t]{2,}",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>
    /// Adapt a player-visible string for touchscreens.
    /// Returns <paramref name="text"/> byte-for-byte unchanged when <paramref name="isTouchPrimary"/> is <c>false</c>.
    /// </summary>
    public static string Adapt(
        string text,
        bool isTouchPrimary,
        string localizationKey = null,
        Func<string, string> overrideLookup = null)
    {
        if (!isTouchPrimary || string.IsNullOrEmpty(text))
            return text;

        if (!string.IsNullOrWhiteSpace(localizationKey) && overrideLookup is not null)
        {
            var overrideKey = localizationKey.StartsWith(OverrideKeyPrefix, StringComparison.Ordinal)
                ? localizationKey
                : OverrideKeyPrefix + localizationKey;
            var explicitOverride = overrideLookup(overrideKey);
            if (!string.IsNullOrWhiteSpace(explicitOverride))
                return explicitOverride;
        }

        var result = BracketedHotkeyRx.Replace(text, string.Empty);
        result = DotHotkeySuffixRx.Replace(result, string.Empty);
        result = ParenHotkeyRx.Replace(result, string.Empty);

        for (var i = 0; i < PhraseReplacements.Length; i++)
        {
            result = PhraseReplacements[i].Pattern.Replace(result, PhraseReplacements[i].Replacement);
        }

        result = MultiSpaceRx.Replace(result, " ").Trim();
        return result;
    }

    /// <summary>Adapt using the active <see cref="PlatformProfile.Current"/>.</summary>
    public static string AdaptForCurrent(
        string text,
        string localizationKey = null,
        Func<string, string> overrideLookup = null)
        => Adapt(text, PlatformProfile.Current.IsTouchPrimary, localizationKey, overrideLookup);

    private static Regex Compile(string pattern) =>
        new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
