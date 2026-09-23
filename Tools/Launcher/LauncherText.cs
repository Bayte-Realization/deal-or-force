using System.Globalization;
using System.IO;
using System.Text.Json;

namespace DealOrForce.Launcher;

/// <summary>
/// The launcher's two languages.
///
/// It was written in Russian only, every string typed straight into the window - in front of a
/// game whose default language is English and whose audience is mostly not Russian. It works
/// the way the game's own localisation does now: the English text is the key, so the window
/// reads correctly with no table at all, and Russian is a lookup on top.
///
/// Which language is decided once, before the window is built: whatever the player last chose
/// inside the game, read from the game's own settings.json - the launcher's profile folder IS
/// the game's user:// folder, so the file is already beside it - and failing that, the
/// language Windows is running in. So the launcher and the game speak the same language as
/// soon as the game has been started once, and a first run on a Russian machine is Russian.
///
/// Nothing in this half touches WPF, so LauncherTests can compile the catalogue, which asks
/// for its words here, without a window toolkit. The half that walks a window is in
/// LauncherTextWindow.cs.
/// </summary>
public static partial class L
{
    public static string Language { get; private set; } =
        CultureInfo.CurrentUICulture.TwoLetterISOLanguageName == "ru" ? "russian" : "english";

    public static bool IsRussian => Language == "russian";

    /// <summary>Follow the language the game was last set to, if it has been run.</summary>
    public static void Choose(string profile)
    {
        try
        {
            var path = Path.Combine(profile, "settings.json");
            if (!File.Exists(path)) return;
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("language", out var value) ||
                value.ValueKind != JsonValueKind.String) return;
            var chosen = value.GetString()?.Trim().ToLowerInvariant() ?? "";
            // The game can carry more languages than these two - a mod can add one. The
            // launcher knows two, and English is what it falls back to for the rest.
            if (chosen.Length > 0) Language = chosen == "russian" ? "russian" : "english";
        }
        catch (IOException) { }
        catch (JsonException) { }
        catch (UnauthorizedAccessException) { }
    }

    public static string T(string english) =>
        IsRussian && Russian.TryGetValue(english, out var russian) ? russian : english;

    public static string F(string english, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    // Keyed by the English exactly as it is written in the window and the code - the same
    // rule as the game's tables. A string missing here simply stays English.
    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        // The window
        ["Deal or Force — Launcher"] = "Deal or Force — Лаунчер",
        ["DEAL OR FORCE  /  LAUNCH CENTRE"] = "DEAL OR FORCE  /  ЦЕНТР ЗАПУСКА",
        ["LOCAL INSTALLATION"] = "ЛОКАЛЬНАЯ УСТАНОВКА",
        ["PROJECT  /  GODOT .NET"] = "ПРОЕКТ  /  GODOT .NET",
        ["WINDOWS  /  LOCAL INSTALLATION"] = "WINDOWS  /  ЛОКАЛЬНАЯ УСТАНОВКА",
        ["THE WORLD WILL NOT WAIT."] = "МИР НЕ ЖДЁТ.",
        ["Strike a deal. Or force one."] = "Договорись. Или заставь.",
        ["Prepare your version of the world: choose the expansions, set up your mods and begin a campaign."] =
            "Подготовьте свою версию мира: выберите дополнения, настройте моды и начните кампанию.",
        ["Base game → DLC → mods\nDependencies load first."] = "Базовая игра → DLC → моды\nЗависимости загружаются первыми.",
        ["Game content"] = "Состав игры",
        ["Refresh"] = "Обновить",
        ["Look for mods and DLC again. Changes are saved first."] = "Повторно найти моды и DLC. Изменения сначала сохраняются.",
        ["Mods"] = "Моды",
        ["Expansions"] = "Дополнения",
        ["Search by name or ID"] = "Поиск по названию или ID",
        ["Search mods and expansions"] = "Поиск модов и дополнений",
        ["Search by name or ID…"] = "Поиск по названию или ID…",
        ["Turn on or off for the next launch"] = "Включить / выключить при следующем запуске",
        ["Choose a mod or expansion to see its description, dependencies and where its files are."] =
            "Выберите мод или дополнение, чтобы посмотреть описание, зависимости и расположение файлов.",
        ["Open folder"] = "Открыть папку",
        ["↑ Earlier"] = "↑ Раньше",
        ["↓ Later"] = "↓ Позже",
        ["Mod load order. Required dependencies always come first."] = "Порядок загрузки модов. Обязательные зависимости всегда первыми.",
        ["Settings take effect when the game starts."] = "Настройки применяются при запуске игры.",
        ["Logs and settings"] = "Логи и настройки",
        ["  /  Choose Godot .NET"] = "  /  Выбрать Godot .NET",
        ["Save"] = "Сохранить",
        ["PLAY  ▶"] = "ИГРАТЬ  ▶",
        ["GAME RUNNING"] = "ИГРА ЗАПУЩЕНА",

        // What the window says as it works
        ["Save the changes to the game's content?"] = "Сохранить изменения состава игры?",
        ["Nothing found. Try another name or ID."] = "Ничего не найдено. Попробуйте другое название или ID.",
        ["No expansions found.\n\nAdd an expansion folder with a dlc.json to dlc, then press \"Refresh\"."] =
            "Дополнения не найдены.\n\nДобавьте папку дополнения с dlc.json в dlc и нажмите «Обновить».",
        ["No mods installed yet.\n\nAdd a mod folder with a manifest.json to mods, then press \"Refresh\".\n\n_templates holds examples for authors; they are not active mods."] =
            "Моды пока не установлены.\n\nДобавьте папку мода с manifest.json в mods и нажмите «Обновить».\n\n_templates — примеры для авторов, не активные моды.",
        ["READY TO LAUNCH\n{0} expansions  ·  {1} mods"] = "К ЗАПУСКУ\n{0} дополнений  ·  {1} модов",
        ["Settings error. Open the profile folder; the backup is the .bak file."] =
            "Ошибка настроек. Откройте папку профиля; резервная копия — .bak.",
        ["Discovery warnings: {0}. Details are in the log."] = "Предупреждения обнаружения: {0}. Подробности — в журнале.",
        ["Choose an item to see its details.\n\nSettings are kept apart from the mod and DLC files."] =
            "Выберите элемент для просмотра сведений.\n\nНастройки хранятся отдельно от файлов модов и DLC.",
        ["Position in its category: {0}. "] = "Порядок в категории: {0}. ",
        ["Dependencies: {0}.\n"] = "Зависимости: {0}.\n",
        ["No dependencies.\n"] = "Без зависимостей.\n",
        ["The expansion is installed, but the entitlement \"{0}\" is not owned. The launcher does not change entitlements.\n"] =
            "Дополнение установлено, но entitlement «{0}» не получен. Лаунчер не изменяет права доступа.\n",
        ["Contains executable code — only enable mods from sources you trust.\n"] =
            "Содержит исполняемый код — включайте только моды из доверенных источников.\n",
        ["Declared game version: {0} (compatibility is not checked automatically).\n"] =
            "Заявленная версия игры: {0} (совместимость не проверена автоматически).\n",
        ["There are unsaved changes. Save them, or press \"Play\"."] = "Есть изменения. Сохраните их или нажмите «Играть».",
        ["Order changed. Dependencies always load before the mod that needs them."] =
            "Порядок изменён. Зависимости всегда загружаются перед зависимым модом.",
        ["Saved. The changes take effect at the next launch."] = "Сохранено. Изменения вступят в силу при следующем запуске.",
        ["List refreshed. Settings take effect at the next launch."] = "Список обновлён. Настройки применяются при следующем запуске.",
        ["Choose Godot .NET (mono), not the standard build of Godot"] = "Выберите Godot .NET (mono), не обычную версию Godot",
        ["Godot chosen: {0}"] = "Godot выбран: {0}",
        ["Some enabled items have missing dependencies or a cycle. Enable the dependencies they need, or turn the problem items off."] =
            "Некоторые включённые элементы имеют недоступные зависимости или цикл. Включите необходимые зависимости либо выключите проблемные элементы.",
        ["Could not start the game process."] = "Не удалось создать процесс игры.",
        ["The game is starting. Wait for the main menu; a second launch is blocked."] =
            "Игра запускается. Дождитесь главного меню; повторный запуск заблокирован.",
        ["The game has closed. You can change the content for the next campaign."] =
            "Игра закрыта. Можно изменить состав следующей кампании.",
        ["The game exited with code {0}. Check the game's logs."] = "Игра завершилась с кодом {0}. Проверьте логи игры.",
        ["Deal or Force — error"] = "Deal or Force — ошибка",

        // A package's status in the list
        ["Not owned"] = "Нет права доступа",
        ["Off"] = "Выключено",
        ["Will load"] = "Будет загружено",

        // Failures a player can meet
        ["Put the launcher beside Deal or Force.exe or project.godot, or give --root <folder>."] =
            "Поместите лаунчер рядом с Deal or Force.exe или project.godot. Можно указать --root <папка>.",
        ["The manifest has no id."] = "В манифесте отсутствует id.",
        ["Fix content_selection.json, or restore the .bak, before saving."] =
            "Исправьте content_selection.json или восстановите .bak перед сохранением.",
        ["Choose Godot .NET to run the project."] = "Выберите Godot .NET для запуска проекта.",
        ["Deal or Force.exe was not found beside the launcher."] = "Не найден Deal or Force.exe рядом с лаунчером.",
        ["The launcher is already open. Switch to its window."] = "Лаунчер уже открыт. Переключитесь на его окно.",
        ["Deal or Force — cannot start"] = "Deal or Force — запуск невозможен",
    };
}
