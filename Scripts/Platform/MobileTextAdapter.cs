using System;
using System.Text.RegularExpressions;

namespace DealOrForce.Platform;

/// <summary>
/// Pure-C# mobile copy adapter ("тексты под мобилы") that rewrites desktop-centric input copy
/// (Quit to desktop, [F7]/[Space]/[Esc] hotkey badges, Right-click/ПКМ, Left-click/ЛКМ, MMB/СКМ,
/// Shift/Ctrl/Alt combos, Esc/Space/WASD, mouse/мышь/клавиши) into touch-native wording when
/// running on primary-touch devices. Desktop (<c>isTouchPrimary == false</c>) returns unchanged.
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
        // Intro skip prompt & Quit to desktop (EN + RU)
        (Compile(@"Press Esc, Space, Enter or click to skip"), "Tap to skip"),
        (Compile(@"Нажмите Esc, пробел, Enter или кликните, чтобы пропустить"), "Коснитесь экрана, чтобы пропустить"),
        (Compile(@"\b(?:Quit|Exit) to desktop\b"), "Exit game"), (Compile(@"\b(?:quit|exit) to desktop\b"), "exit game"),
        (Compile(@"Выход на рабочий стол|Выйти на рабочий стол"), "Выйти из игры"),
        (Compile(@"выход на рабочий стол|выйти на рабочий стол"), "выйти из игры"),

        // Parenthetical PC analogies (even inside mobile tips)
        (CompileI(@"\s*\((?:like\s+)?(?:right|left)[- ]click\)"), ""),
        (CompileI(@"\s*\((?:как\s+)?(?:ПКМ|ЛКМ|правой кнопкой(?:\s+мыши)?|левой кнопкой(?:\s+мыши)?)\)"), ""),

        // Modifier + mouse/button & keyboard combo phrases (EN + RU)
        (CompileI(@"\b(?:Shift|Ctrl|Alt)\s*\+\s*(?:right[- ]click|left[- ]click|RMB|LMB|click|drag)\b"), "tap"),
        (CompileI(@"\b(?:Shift|Ctrl|Alt)/(?:RMB|LMB)\b"), "tap"), (CompileI(@"\b(?:Shift|Ctrl|Alt)-click\b"), "tap"),
        (CompileI(@"\bCtrl\+Alt\+[A-Z0-9]\b"), ""), (CompileI(@"\b(?:Ctrl|Shift|Alt)\+(?:[A-Z0-9]+(?:\.\.[0-9]+)?)\b"), ""),
        (CompileI(@"(?:Shift|Ctrl|Alt)\s*\+\s*(?:ПКМ|ЛКМ|клик|проводка)"), "касание"),
        (CompileI(@"(?:Shift|Ctrl|Alt)/(?:ПКМ|ЛКМ)"), "касание"),

        // Mouse button phrases (EN)
        (Compile(@"\bDouble-click\b"), "Double-tap"), (Compile(@"\bdouble-click\b"), "double-tap"),
        (Compile(@"\bRight-click\b"), "Order tap / Hold"), (Compile(@"\bright-click\b"), "order tap / hold"),
        (Compile(@"\bRight click\b"), "Order tap / Hold"), (Compile(@"\bright click\b"), "order tap / hold"),
        (Compile(@"\bLeft-click\b"), "Tap"), (Compile(@"\bleft-click\b"), "tap"),
        (Compile(@"\bLeft click\b"), "Tap"), (Compile(@"\bleft click\b"), "tap"),
        (Compile(@"\bClick to\b"), "Tap to"), (Compile(@"\bclick to\b"), "tap to"),
        (Compile(@"\bClick a\b"), "Tap a"), (Compile(@"\bclick a\b"), "tap a"),
        (Compile(@"\bRight-drag\b"), "Drag"), (Compile(@"\bright-drag\b"), "drag"),
        (Compile(@"\bRMB-drag\b"), "Order drag"), (Compile(@"\bRMB\b"), "Order tap"), (Compile(@"\bLMB\b"), "Tap"),
        (CompileI(@"\bMMB\s+pans\.?"), "Two fingers zoom."), (Compile(@"\bMMB\b"), "Two fingers"),
        (CompileI(@"\bmiddle\s+button\b"), "two fingers"),
        (CompileI(@"\b(?:hold\s+the\s+right\s+button\s+and\s+drag|drag\s+with\s+the\s+right\s+button)\b"), "drag on the map"),
        (CompileI(@"\bwith\s+the\s+right\s+button\b"), "with a finger"),
        (CompileI(@"\bright\s+button\b"), "Order tap"), (CompileI(@"\bleft\s+button\b"), "tap"),
        (Compile(@"\bHover over\b"), "Tap"), (Compile(@"\bhover over\b"), "tap"),
        (Compile(@"\bHover a\b"), "Tap a"), (Compile(@"\bhover a\b"), "tap a"),
        (Compile(@"\b(?:Mouse|Scroll) wheel\b"), "Pinch"), (Compile(@"\b(?:mouse|scroll) wheel\b"), "pinch"),
        (Compile(@"\b(?:Wheel|Scroll) to zoom\b"), "Pinch to zoom"), (Compile(@"\b(?:wheel|scroll) to zoom\b"), "pinch to zoom"),

        // Mouse button phrases (RU)
        (Compile(@"(?<![А-Яа-яЁёA-Za-z0-9_])ПКМ(?![А-Яа-яЁёA-Za-z0-9_])"), "Режим приказа / Удержание"),
        (Compile(@"(?<![А-Яа-яЁёA-Za-z0-9_])ЛКМ(?![А-Яа-яЁёA-Za-z0-9_])"), "Тап"),
        (Compile(@"(?<![А-Яа-яЁёA-Za-z0-9_])СКМ(?![А-Яа-яЁёA-Za-z0-9_])"), "Два пальца"),
        (Compile(@"Двойной клик"), "Двойное касание"), (Compile(@"двойной клик"), "двойное касание"),
        (Compile(@"Кликните|Щёлкните"), "Коснитесь"), (Compile(@"кликните|щёлкните"), "коснитесь"),
        (CompileI(@"(?:проведите|тяните)\s+правой\s+кнопкой(?:\s+мыши)?"), "проведите пальцем"),
        (Compile(@"правой кнопкой мыши|правой кнопкой"), "в режиме приказа или удержанием"),
        (Compile(@"левой кнопкой мыши|левой кнопкой"), "касанием"),
        (CompileI(@"средней кнопк(?:ой|и|у|е)(?:\s+мыши)?"), "двумя пальцами"),
        (Compile(@"Наведите курсор"), "Нажмите"), (Compile(@"наведите курсор"), "нажмите"),
        (Compile(@"Колёсико|Колесико"), "Щипок"), (Compile(@"колёсико|колесико"), "щипок"),

        // Escape / Space / WASD / keyboard / mouse leftovers (EN + RU)
        (CompileI(@"\bEsc\s+cancels?\.?"), "Tap Cancel to exit."),
        (CompileI(@"\bEsc\s*[—–-]\s*(?:отмена|закрыть)\.?|\bEsc\s+отменяет\.?"), "«Отмена» — выход."),
        (CompileI(@"(?<=[А-Яа-яЁё][^\n]*)\b(?:Esc|Escape)\b"), "Отмена"),
        (CompileI(@"\bEscape\b"), "Cancel"), (Compile(@"\bEsc\b"), "Cancel"),
        (Compile(@"\bWASD\b"), "drag"), (CompileI(@"\bmouse\s+cursor\b"), "finger"), (CompileI(@"\bmouse\b"), "touch"),
        (CompileI(@"мышк(?:ой|а|у|е|и)"), "пальцем"),
        (CompileI(@"(?<![А-Яа-яЁё])мыш(?:ью|и|ь)(?![А-Яа-яЁё])"), "касанием"),
        (CompileI(@"клавиш(?:ей|ами|а|у|и|е)?"), "кнопкой"),
        (Compile(@"(?<![А-Яа-яЁё])Пробел(?![А-Яа-яЁё])"), "Пауза"),
        (Compile(@"(?<![А-Яа-яЁё])пробел(?![А-Яа-яЁё])"), "пауза"),

        // Punctuation & separator cleanup
        (Compile(@"(?:\s*[·•]\s*){2,}"), " · "), (Compile(@"^\s*[·•]\s*|\s*[·•]\s*$"), ""),
        (Compile(@"\s+([.,:;!?])"), "$1"),
    };

    private static readonly Regex MultiSpaceRx = new(@"[ \t]{2,}", RegexOptions.CultureInvariant | RegexOptions.Compiled);

    /// <summary>Adapt a player-visible string for touchscreens; desktop (<c>false</c>) returns unchanged.</summary>
    public static string Adapt(string text, bool isTouchPrimary, string localizationKey = null, Func<string, string> overrideLookup = null)
    {
        if (!isTouchPrimary || string.IsNullOrEmpty(text)) return text;
        if (!string.IsNullOrWhiteSpace(localizationKey) && overrideLookup is not null)
        {
            var overrideKey = localizationKey.StartsWith(OverrideKeyPrefix, StringComparison.Ordinal) ? localizationKey : OverrideKeyPrefix + localizationKey;
            var explicitOverride = overrideLookup(overrideKey);
            if (!string.IsNullOrWhiteSpace(explicitOverride)) return explicitOverride;
        }
        var result = BracketedHotkeyRx.Replace(text, string.Empty);
        result = DotHotkeySuffixRx.Replace(result, string.Empty);
        result = ParenHotkeyRx.Replace(result, string.Empty);
        for (var i = 0; i < PhraseReplacements.Length; i++)
            result = PhraseReplacements[i].Pattern.Replace(result, PhraseReplacements[i].Replacement);
        return MultiSpaceRx.Replace(result, " ").Trim();
    }

    public static string AdaptForCurrent(string text, string localizationKey = null, Func<string, string> overrideLookup = null) =>
        Adapt(text, PlatformProfile.Current.IsTouchPrimary, localizationKey, overrideLookup);

    private static Regex Compile(string pattern) => new(pattern, RegexOptions.CultureInvariant | RegexOptions.Compiled);
    private static Regex CompileI(string pattern) => new(pattern, RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);
}
