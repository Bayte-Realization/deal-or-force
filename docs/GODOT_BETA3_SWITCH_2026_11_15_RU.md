# Переход на Godot 4.8 Beta3 .NET — подготовка, 14–15 ноября 2026

Ветка `claude/godot-4-8-beta3-3c5af5` (от `EntertAlpha` ea42b634). Ничего не
запушено и не слито. Редактор Beta2 владельца и основная папка проекта не
трогались: всё проверено в отдельной рабочей копии
`.claude/worktrees/godot-4-8-beta3-3c5af5`.

Beta3 вышла 9 ноября 2026, коммит движка `c51ff06e8`, в основном исправления
регрессий. Требует .NET 10 (стоит 10.0.403).

## Что установлено

| Что | Куда | Размер | SHA-512 (сверен с официальным `SHA512-SUMS.txt`) |
|---|---|---|---|
| `Godot_v4.8-beta3_mono_win64.zip` | распакован в `C:\GodotEngineNet 4.8-Beta3\` | 95 852 755 Б | `d48ed5b9d88019c8ae75bfa9b3a38f5585ccbcad9e81397a98e24c9887e110ce1b412b796dbb0c0477f5ec6430d5b08c7aa6b1859d81c6bc2cfbd9b140dc2974` |
| `Godot_v4.8-beta3_mono_export_templates.tpz` | распакован в `%APPDATA%\Godot\export_templates\4.8.beta3.mono\` (27 файлов, 1,9 ГБ, `version.txt` = `4.8.beta3.mono`) | 1 188 457 518 Б | `e208ca4b8914cc9a23e071d4baace4ff77673d3b147921424e95efbf461257aebc9ec9e3a2ffb87fae5b4752c2ded99ce1dde1386d1eae435ef0c14b148a432f` |

- Источник: ссылки со страницы https://godotengine.org/download/archive/4.8-beta3/
  ведут на релиз `godotengine/godot-builds` `4.8-beta3` на GitHub. Оттуда же взят
  `SHA512-SUMS.txt` (SHA-256 самого файла сумм:
  `c62cad4611e648a177b7165f7a318ba70449cc7919f44698d5b14d6290c1858d`).
- SHA-256 обоих архивов совпал и с дайджестами, которые GitHub публикует для этих
  файлов (`9e04f6b9…5a2d` и `0811bf3a…b934`).
- `Godot_v4.8-beta3_mono_win64.exe --version` → `4.8.beta3.mono.official.c51ff06e8`.
- Архивы оставлены в `C:\Users\User\Downloads\godot-4.8-beta3-dl\`, их можно удалить.
- `C:\GodotEngineNet 4.8-Beta2\` и `export_templates\4.8.beta2.mono\` не тронуты.
  В папке шаблонов Beta2 лежат только файлы Windows x86-64, в Beta3 — полный
  комплект. Лишние платформы (Android, iOS, macOS, Linux; около 1,2 ГБ) можно удалить
  так же, если место нужно.

## Что изменено в ветке

1. `DealOrForce.csproj`: `Godot.NET.Sdk/4.8.0-beta.2` → `4.8.0-beta.3`. Пакеты
   `GodotSharp`, `GodotSharpEditor`, `Godot.SourceGenerators` 4.8.0-beta.3 есть на
   NuGet и подтягиваются при сборке. Других проектов с этим SDK, кроме языкового
   комплекта, нет.
2. Путь к движку по умолчанию — `C:\GodotEngineNet 4.8-Beta3\Godot_v4.8-beta3_mono_win64_console.exe`:
   `Tools/dev.ps1` (оба места), `Tools/editor-doctor.ps1`, `Tools/game-capture.ps1`;
   комментарий в `Tools/close-godot.ps1`; `docs/BUILDING.md`, `docs/BUILDING_RU.md`.
3. **Точная сверка версии движка.** `build.ps1` и `dev.ps1` сравнивали только `4.8.`,
   поэтому редактор Beta2 молча экспортировал бы проект, собранный против GodotSharp
   Beta3. Новый `Tools/EngineVersion.ps1` выводит точный префикс из строки SDK:
   `4.8.0-beta.3` → `4.8.beta3.`, `4.8.0-rc.1` → `4.8.rc1.`, `4.8.0` → `4.8.stable.`.
   Это же имя папки шаблонов. `build.ps1` отказывает при несовпадении, `dev.ps1`
   переходит на путь по умолчанию.
4. Исторические отчёты в `docs/` с упоминанием Beta2 не правились: они описывают
   прошлые прогоны.

## Проверки на Beta3 (в рабочей копии)

| Проверка | Результат |
|---|---|
| `dotnet build DealOrForce.csproj` | 0 ошибок, 1 предупреждение (давнее CS0649 `MilitaryOperations.MarchProbe`), пакеты 4.8.0-beta.3 |
| Импорт без окна: `Godot_v4.8-beta3_mono_win64.exe --headless --path . --import` | код 0, 3 597 с (холодный кэш, машина под нагрузкой). 15 ошибок — все из-за отсутствующих `addons/` (их нет в git): 5 автозагрузок аддонов не создаются; 12 включённых плагинов редактор пропускает. `project.godot` при этом не изменился |
| `Tools/FormableTests` (Release, без флага) | **16 254 PASS, 0 FAIL**, код 0, 594 с |
| `SimBench --profile-save tmp/bench_late.bdfsave 14` | код 0, 174 с, отпечаток мира **`D203FF95BBDA7E2C`**. SimBench не ссылается на Godot, и ни один его исходник в ветке не менялся (`git diff EntertAlpha -- Scripts Scenes game Tools/SimBench Tools/FormableTests Tools/Shared` пуст), так что на Beta2 число то же самое; отдельный прогон на Beta2 не делался |
| `Tools/build.ps1 -Godot "C:\GodotEngineNet 4.8-Beta3\Godot_v4.8-beta3_mono_win64.exe" -VerifyRenderers` | **192 PASS, 0 FAIL** в экспортированной игре (`EXPORT_VERIFY failures=0`, чистое завершение, нет ошибок шейдеров/скриптов), лог игры: `Godot Engine v4.8.beta3.mono.official.c51ff06e8`. GPU: **gl_compatibility PASS, gl_angle PASS, forward_plus PASS**. Архив `build\Deal or Force 2026-11-15_11-49-01.zip`, 639,9 МБ, версия 0.6.6.0 |
| Главное меню экспортированной игры (`verification-renderers/*/export-menu.png`) | глобус, логотип, кнопки и подпись версии отрисованы нормально |

## Аддоны (`addons/`, вне git)

Все четыре нативных расширения объявляют `compatibility_minimum` ниже 4.8 и
загружаются Beta3 как есть: ABI GDExtension совместим вперёд. В экспорт аддоны не
входят — `build.ps1` экспортирует копию без них.

| Аддон | Версия | `compatibility_minimum` | Сборка под Beta3 у автора |
|---|---|---|---|
| Fennara | 0.4.3 (stable) | 4.5 | нет, не требуется; инфраструктура не трогалась |
| GodotSteam (GDExtension) | 4.23 | 4.4 | нет. Вышла 4.24 (8 ноября, Steamworks 1.65), но не под 4.8; обновление необязательно |
| LimboAI | v1.8.1 | 4.2 | нет, 1.8.1 — последний релиз |
| Ziva Agent | v3.3.2 | 4.2 | нет |

Окончательно проверяется первым открытием в редакторе Beta3: в выводе не должно
быть `Can't open GDExtension dynamic library`.

### Проверка компиляции плагинов, 15 ноября

Отдельная временная копия: только `addons/` (без `at-icons` и папки обновления
`.godot_ai_update`) плюс маленький проверочный плагин. Он ждёт сканирования
файлов, загружает каждый `.gd` из аддонов и выходит. Ни один настоящий плагин не
включался. Нативные библиотеки Fennara и Ziva были отключены, чтобы второй
редактор не мешал маршрутизации MCP у открытого редактора. Один и тот же прогон
сделан на Beta3 и на Beta2 (`--headless --editor`).

| | Beta3 | Beta2 |
|---|---|---|
| Скрипты аддонов (17 аддонов, 850 файлов) | все загружены | все загружены |
| Нативные GodotSteam и LimboAI | загрузились | загрузились |
| Ошибки и предупреждения в логе | 56 / 3 | 56 / 3 — **те же строки** |
| Код выхода | `-1073741819` (падение при закрытии, уже после проверки) | то же |

Beta3 не добавила ни одной новой ошибки. Старые, общие для обеих версий:
- В `godot_mcp_toolkit` 10 скриптов не компилируются: `modules.gd` и
  `versioning/mcp_version_utils.gd` не содержат членов, на которые ссылаются
  `update_check.gd`, `setup_step_settings.gd`, `consent.gd` и другие. Похоже на
  наполовину применённое обновление.
- Дубликат UID для `input_map_commands.gd` в том же аддоне.
- Устаревшие UID в `asset_placer` и `kanban_tasks`.
- `Parse Error: Busy` в сценах `dialogue_manager` и `script-ide` появляется от
  массовой загрузки в самой проверке.
- Падение при выходе одинаково на обеих версиях. Нативная загрузка Fennara и Ziva
  этим прогоном не проверена.

## Языковой комплект — что нужно для пересборки на Beta3 (не сделано)

`LanguageKit/`, `Tools/GodotLanguages/` и `C:\GodotEngineNet 4.8-Beta2\LanguageKit`
не менялись: другой агент проверяет их на Beta2.

1. Новые дампы из Beta3: `Godot_v4.8-beta3_mono_win64_console.exe --headless --dump-extension-api`
   и `--dump-gdextension-interface`. От них зависят все нативные связки.
2. Rust (gdext `api-custom`), Zig (gdzig), Go: перегенерировать связки из дампа
   Beta3, пересобрать, прогнать smoke-пробы (`workbench/verify-beta2.ps1` →
   версия для beta3).
3. Python (`python-meson`): тест `test_exact_beta2_dump_all_layouts` и патч
   `0001-beta2-api-metadata.patch` — прогнать на дампе Beta3.
4. F#: `adapters/fsharp/template/LanguageKitFSharp.csproj` → SDK `4.8.0-beta.3`;
   в `adapters/fsharp/build.ps1` регулярка `^4\.8\.beta2\.mono\.`.
5. `sources.lock.json` (version, directory, executable, SHA-256 исполняемого
   файла, verified-записи), значения по умолчанию `EngineDirectory` в
   `language-kit.ps1`, `create-workbench.ps1`, `open-workbench.ps1` и `workbench/*.ps1`.
6. Lua, Luau, JVM/Sandbox: подтверждены только на Beta1, их тоже надо прогнать на Beta3.
7. Установленную копию положить в `C:\GodotEngineNet 4.8-Beta3\LanguageKit`
   (`workbench/update-installed-kit.ps1 -EngineDirectory 'C:/GodotEngineNet 4.8-Beta3'`).

## Как переключиться (владелец)

1. Сохранить сцены и закрыть редактор Beta2.
2. Слить ветку `claude/godot-4-8-beta3-3c5af5` в `EntertAlpha`.
3. В `.mcp.json` основной папки (файл вне git) заменить одно значение — строку 24,
   `GODOT_PATH` → `C:\\GodotEngineNet 4.8-Beta3\\Godot_v4.8-beta3_mono_win64_console.exe`.
   Затем перезапустить окно клиента: MCP-серверы читают файл при старте.
4. Один раз открыть проект в `C:\GodotEngineNet 4.8-Beta3\Godot_v4.8-beta3_mono_win64.exe`.
   Чего ждать:
   - редактор пересоберёт C# против пакетов 4.8.0-beta.3 (`.godot/mono`);
   - заново скопирует DLL аддонов в `~lib*.dll`;
   - один раз переиндексирует файловую систему.
   Существующий `.godot/imported` должен остаться, если импортёры между Beta2 и
   Beta3 не менялись. Если редактор всё-таки решит переимпортировать всё, это
   полный проход: здесь, с нуля и под нагрузкой, он занял около часа. Шаблоны
   экспорта уже на месте.
5. Сборка как обычно: `pwsh -File Tools\build.ps1 -Godot "C:\GodotEngineNet 4.8-Beta3\Godot_v4.8-beta3_mono_win64_console.exe" -VerifyRenderers`.
   Редактор Beta2 после слияния больше не сможет экспортировать проект: сверка
   версии его остановит. Чтобы вернуться, нужно откатить строку SDK.
6. Следующий шаг (RC или стабильная) — это строка SDK в `DealOrForce.csproj` плюс
   путь по умолчанию в трёх скриптах (`dev.ps1`, `editor-doctor.ps1`,
   `game-capture.ps1`) и в `.mcp.json`.
