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

    /// <summary>The game's settings file: `settings/settings.json` inside the one game folder.</summary>
    private static string SettingsFile(string profile) => Path.Combine(profile, "settings", "settings.json");

    /// <summary>Where the file lived before the folders were consolidated.</summary>
    private static string OldSettingsFile(string profile) => Path.Combine(profile, "settings.json");

    /// <summary>Follow the language the game was last set to, if it has been run.</summary>
    public static void Choose(string profile)
    {
        if (TryRead(SettingsFile(profile))) return;
        TryRead(OldSettingsFile(profile));
    }

    private static bool TryRead(string path)
    {
        try
        {
            if (!File.Exists(path)) return false;
            using var document = JsonDocument.Parse(File.ReadAllText(path),
                new JsonDocumentOptions { AllowTrailingCommas = true, CommentHandling = JsonCommentHandling.Skip });
            if (!document.RootElement.TryGetProperty("language", out var value) ||
                value.ValueKind != JsonValueKind.String) return false;
            var chosen = value.GetString()?.Trim().ToLowerInvariant() ?? "";
            // The game can carry more languages than these two - a mod can add one. The
            // launcher knows two, and English is what it falls back to for the rest.
            if (chosen.Length > 0) Language = chosen == "russian" ? "russian" : "english";
            return chosen.Length > 0;
        }
        catch (IOException) { return false; }
        catch (JsonException) { return false; }
        catch (UnauthorizedAccessException) { return false; }
    }

    /// <summary>The two the launcher speaks, in the order its button steps through them.</summary>
    public static readonly string[] Languages = { "english", "russian" };

    /// <summary>What the language button says: each language named in itself, not translated.</summary>
    public static string LanguageLabel => IsRussian ? "Язык: Русский" : "Language: English";

    /// <summary>Speak this language from now on. Anything but Russian is English.</summary>
    public static void Use(string language) =>
        Language = string.Equals(language, "russian", StringComparison.OrdinalIgnoreCase) ? "russian" : "english";

    /// <summary>
    /// Tell the game too, by writing the choice into its settings.json - the file Choose reads
    /// it back from, and the one the game's own options screen keeps. Every other setting in
    /// it is kept as it was. A file that exists and will not parse is left alone rather than
    /// replaced: the player's settings are worth more than the launcher's convenience.
    /// </summary>
    public static bool Remember(string profile, string language) => GameSettings.Write(profile, "language", language);

    public static string T(string english) =>
        IsRussian && Russian.TryGetValue(english, out var russian) ? russian : english;

    public static string F(string english, params object[] args) =>
        string.Format(CultureInfo.CurrentCulture, T(english), args);

    // Keyed by the English exactly as it is written in the window and the code - the same
    // rule as the game's tables. A string missing here simply stays English.
    private static readonly Dictionary<string, string> Russian = new(StringComparer.Ordinal)
    {
        ["Windows could not locate the Documents folder."] = "Windows не удалось определить папку «Документы».",
        ["Snapshot size must be at least 980x660."] = "Размер снимка должен быть не меньше 980x660.",
        // The window
        ["Deal or Force — Launcher"] = "Deal or Force — Лаунчер",
        ["PROJECT  /  GODOT .NET"] = "ПРОЕКТ  /  GODOT .NET",
        ["WINDOWS  /  LOCAL INSTALLATION"] = "WINDOWS  /  ЛОКАЛЬНАЯ УСТАНОВКА",
        ["code"] = "код",
        ["Refresh"] = "Обновить",
        ["Look for mods and DLC again. Changes are saved first."] = "Повторно найти моды и DLC. Изменения сначала сохраняются.",
        ["Mods"] = "Моды",
        ["Expansions"] = "Дополнения",
        ["Search by name or ID"] = "Поиск по названию или ID",
        ["Search mods and expansions"] = "Поиск модов и дополнений",
        ["Search by name or ID…"] = "Поиск по названию или ID…",
        ["Turn on or off for the next launch"] = "Включить / выключить при следующем запуске",
        ["Open folder"] = "Открыть папку",
        ["↑ Earlier"] = "↑ Раньше",
        ["↓ Later"] = "↓ Позже",
        ["Mod load order. Required dependencies always come first."] = "Порядок загрузки модов. Обязательные зависимости всегда первыми.",
        ["Settings take effect when the game starts."] = "Настройки применяются при запуске игры.",
        ["Save"] = "Сохранить",

        // The 0.6.6 window: tabs, start options, help
        ["What's new"] = "Что нового",
        ["Start options"] = "Запуск",
        ["Help"] = "Помощь",
        ["Play"] = "Играть",
        ["Game running"] = "Игра запущена",
        ["Running from the project"] = "Запуск из проекта",
        ["No build information beside the game"] = "Рядом с игрой нет сведений о сборке",
        ["Build {0} · {1}"] = "Сборка {0} · {1}",
        ["The list of changes was not found beside the game. It is published with every release on the game's page."] =
            "Список изменений рядом с игрой не найден. Он публикуется с каждым выпуском на странице игры.",
        ["Enabled mods change the same files ({0}):"] = "Включённые моды меняют одни и те же файлы ({0}):",
        ["{0}: {1} — {2} wins where they disagree."] = "{0}: {1} — где они расходятся, действует {2}.",
        ["…and {0} more. The full list is in the launcher's log."] = "…и ещё {0}. Полный список — в журнале лаунчера.",
        ["safe map"] = "безопасная карта",
        ["mods: {0}"] = "модов: {0}",
        ["What draws the game"] = "Чем рисуется игра",
        ["Change it only if the picture is black, flickers or the game does not reach the menu. A choice here is used as it is: the game no longer steps down by itself."] =
            "Меняйте, только если картинка чёрная, мерцает или игра не доходит до меню. Выбранное здесь используется как есть: сама игра на другой способ уже не переключится.",
        ["Automatic"] = "Автоматически",
        ["As chosen in the game's settings; the best the machine has."] = "Как выбрано в настройках игры; лучшее, что есть на компьютере.",
        ["The full picture on a modern graphics card."] = "Полная картинка на современной видеокарте.",
        ["The same picture, when Direct3D 12 misbehaves."] = "Та же картинка, если Direct3D 12 сбоит.",
        ["Compatibility · OpenGL"] = "Совместимость · OpenGL",
        ["Simpler lighting; draws on nearly anything."] = "Освещение проще; работает почти везде.",
        ["Compatibility · ANGLE (OpenGL over Direct3D 11)"] = "Совместимость · ANGLE (OpenGL через Direct3D 11)",
        ["For old or unusual graphics drivers."] = "Для старых или необычных драйверов видеокарты.",
        ["Window"] = "Окно",
        ["Borderless"] = "Без рамки",
        ["Fullscreen"] = "Полный экран",
        ["Windowed"] = "В окне",
        ["Written into the game's own settings, so the game's options show the same."] =
            "Записывается в настройки самой игры, поэтому в игре будет то же самое.",
        ["Map"] = "Карта",
        ["Safe map"] = "Безопасная карта",
        ["A plainer planet without relief and terrain detail, for a graphics card that crashes or draws the map wrong. Borders, owners and map modes stay as they are. Turn it off once the game runs."] =
            "Упрощённая планета без рельефа и деталей местности — для видеокарты, на которой игра вылетает или карта рисуется неправильно. Границы, владельцы и режимы карты остаются прежними. Когда игра заработает, выключите.",
        ["Renderer chosen for the next start."] = "Способ отрисовки выбран для следующего запуска.",
        ["The next start uses the safe map."] = "Следующий запуск — с безопасной картой.",
        ["The next start uses the full map."] = "Следующий запуск — с полной картой.",
        ["Window mode saved in the game's settings."] = "Режим окна записан в настройки игры.",
        ["The game's settings file could not be written; the game keeps its own window mode."] =
            "Не удалось записать файл настроек игры; игра оставит свой режим окна.",
        ["Check the game's files"] = "Проверка файлов игры",
        ["Compares the game, its data and the launcher with the checksums this build was shipped with. A file that differs was damaged in the download or changed since."] =
            "Сравнивает игру, её данные и лаунчер с контрольными суммами, с которыми вышла эта сборка. Если файл отличается, он повредился при скачивании или был изменён позже.",
        ["Check files"] = "Проверить файлы",
        ["{0} files listed in {1}."] = "В {1} записано файлов: {0}.",
        ["This is the project, not a delivered build: there is nothing to compare with."] =
            "Это проект, а не готовая сборка: сравнивать не с чем.",
        ["{0} is missing, so there is nothing to compare with."] = "Нет файла {0}, поэтому сравнивать не с чем.",
        ["Checking {0}…"] = "Проверяется {0}…",
        ["intact"] = "цел",
        ["missing"] = "нет файла",
        ["differs"] = "отличается",
        ["All {0} files are as shipped."] = "Все файлы ({0}) такие же, как в сборке.",
        ["{0} of {1} files differ from the build. Download the game again, or replace those files."] =
            "Отличаются от сборки: {0} из {1}. Скачайте игру заново или замените эти файлы.",
        ["Report a problem"] = "Сообщить о проблеме",
        ["Puts the build, this computer's system and graphics card, the last logs, the settings and the newest save into one zip in the reports folder, and shows it. Nothing is sent anywhere: attach the file where you report the problem."] =
            "Собирает в один zip-файл в папке reports сведения о сборке, системе и видеокарте, последние логи, настройки и самое свежее сохранение — и показывает его. Ничего никуда не отправляется: приложите файл туда, где сообщаете о проблеме.",
        ["Write a report"] = "Собрать отчёт",
        ["Open reports folder"] = "Открыть папку отчётов",
        ["Writing the report…"] = "Собираю отчёт…",
        ["The report could not be written: {0}"] = "Не удалось записать отчёт: {0}",
        ["Report written: {0}"] = "Отчёт готов: {0}",
        ["Saves, settings and logs"] = "Сохранения, настройки и логи",
        ["Everything the game writes lives in one folder: {0}"] = "Всё, что записывает игра, лежит в одной папке: {0}",
        ["Open game folder"] = "Открыть папку игры",
        ["Choose Godot .NET"] = "Выбрать Godot .NET",
        ["The game exited with code {0}. If it keeps happening, write a report on the Help tab."] =
            "Игра завершилась с кодом {0}. Если это повторяется, соберите отчёт на вкладке «Помощь».",

        // What the window says as it works
        ["Save the changes to the game's content?"] = "Сохранить изменения состава игры?",
        ["Nothing found. Try another name or ID."] = "Ничего не найдено. Попробуйте другое название или ID.",
        ["No expansions found.\n\nAdd an expansion folder with a dlc.json to dlc, then press \"Refresh\"."] =
            "Дополнения не найдены.\n\nДобавьте папку дополнения с dlc.json в dlc и нажмите «Обновить».",
        ["No mods installed yet.\n\nAdd a mod folder with a manifest.json to mods, then press \"Refresh\".\n\n_templates holds examples for authors; they are not active mods."] =
            "Моды пока не установлены.\n\nДобавьте папку мода с manifest.json в mods и нажмите «Обновить».\n\n_templates — примеры для авторов, не активные моды.",
        ["Ready to launch."] = "Всё готово к запуску.",
        ["{0} enabled items cannot load: see the list."] = "Включённых элементов, которые не загрузятся: {0}. См. список.",
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

        // The language button
        ["Switch the launcher and the game between English and Russian."] =
            "Переключить лаунчер и игру между английским и русским.",
        ["Language changed. The game will start in it too."] = "Язык изменён. Игра тоже запустится на нём.",
        ["The launcher changed language, but the game's settings file could not be written, so the game keeps its own."] =
            "Лаунчер сменил язык, но файл настроек игры записать не удалось, поэтому игра останется на своём.",
    };
}
