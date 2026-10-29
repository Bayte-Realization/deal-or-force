# Запуск редактора: границы автоматического сканирования

Локальное исправление зависания редактора, разрешённое автором проекта
28 октября 2026 года. Fennara не отключён. Патч не меняет игру, MCP-конфигурацию,
автозагрузки, веб-интерфейс или политику доступа к аддонам.

`fennara-0.4.3-scan-boundary.patch` относится только к двум C++-сканерам:

- поиск C#-проекта пропускает скрытые служебные каталоги;
- оба обхода не заходят в подкаталоги с `.gdignore`, отдельные вложенные
  Godot-проекты и каталоги-ссылки/junction;
- индексатор исходников также пропускает `bin`, `obj`, `node_modules`;
- обычные вложенные C#-библиотеки остаются видимыми, выбор главного проекта
  и разрешение `ProjectReference` самим MSBuild не заменены.

Оригинал: [Fennara v0.4.3](https://github.com/fennaraOfficial/fennara-godot-ai/tree/v0.4.3),
commit `0abbac3b3a1cb46bbce4f90e8cebc6a3e5d61ec6`;
godot-cpp `e83fd0904c13356ed1d4c3d09f8bb9132bdc6b77`.
Исходный upstream распространяется по MIT: [текст](FENNARA_LICENSE.md).
Это локальный hotfix, не официальный релиз Fennara.

## Воспроизводимая сборка

Нужны уже установленные Git, Python/SCons и x64 MSVC. Не устанавливает их
автоматически. Собирать вне `res://`, в новом отдельном каталоге. Команды для
x64 Developer PowerShell/Command Prompt; пути заменить своими:

```powershell
git clone --depth 1 --branch v0.4.3 --recurse-submodules --shallow-submodules https://github.com/fennaraOfficial/fennara-godot-ai.git upstream
git -C upstream rev-parse HEAD
git -C upstream/fennara-cpp/godot-cpp rev-parse HEAD
git -C upstream apply --check "D:/Godot Projects/deal-or-force-godot-4-8/Tools/EditorStartup/fennara-0.4.3-scan-boundary.patch"
git -C upstream apply "D:/Godot Projects/deal-or-force-godot-4-8/Tools/EditorStartup/fennara-0.4.3-scan-boundary.patch"
Set-Location upstream/fennara-cpp
scons platform=windows target=editor arch=x86_64 -j4
```

Перед применением обязательно сверить оба commit выше. Результат находится в
`upstream/godot_demo/addons/fennara/bin/libfennara.windows.editor.x86_64.dll`.
Не копировать всю демонстрационную папку поверх рабочего аддона.

Для установки закрыть редактор именно этого проекта, сохранить резервную
копию установленного `addons/fennara/bin/libfennara.windows.editor.x86_64.dll`
и заменить только его. Проверить SHA256 установленного и нового файлов.
`~libfennara...dll` — управляемая Godot рабочая копия; не заменять её на лету.
Официальное обновление аддона может перезаписать hotfix: сначала проверить,
вошло ли исправление в новую версию, а не применять старый patch вслепую.

## Нативная регрессия сканеров

`tests/scan_probe.cpp` использует настоящие изменённые `project.cpp` и
`file_utils.cpp`, Godot `DirAccess/FileAccess/ProjectSettings` и godot-cpp.
Только политика доступа к аддонам подменена запретом по умолчанию.
Это проверка сканеров, не тест всей диагностической системы или скорости игры.

После основной сборки можно собрать probe из x64 Developer Command Prompt
(в примере текущий каталог содержит `upstream`):

```bat
cl /nologo /LD /EHsc /std:c++17 /MT /O2 /DDEBUG_ENABLED /DTYPED_METHOD_BIND /Iupstream\fennara-cpp\include /Iupstream\fennara-cpp\godot-cpp\include /Iupstream\fennara-cpp\godot-cpp\gen\include /Iupstream\fennara-cpp\godot-cpp\gdextension "D:\Godot Projects\deal-or-force-godot-4-8\Tools\EditorStartup\tests\scan_probe.cpp" upstream\fennara-cpp\src\csharp\project.cpp upstream\fennara-cpp\src\file_utils.cpp /link /OUT:scan_probe.dll upstream\fennara-cpp\godot-cpp\bin\libgodot-cpp.windows.editor.x86_64.lib
```

Флаги probe должны соответствовать библиотеке godot-cpp (`/MT`, editor/debug
bindings). Подмена `/MT` на `/MD` даёт конфликт CRT, а не дефект сканера.
Запускать `tests/verify_scan.gd` только на отдельном пустом fixture-каталоге:
он специально создаёт ложные `.csproj`, `.gdignore`, исходники и вложенный
`project.godot`. В корне fixture заранее должны находиться `scan_probe.dll`,
маркер `.dof-fennara-fixture` и junction `loop`, ведущий обратно в fixture.
Без маркера скрипт отказывается писать. Descriptor и вложенный проект
сохраняются через Godot `ConfigFile`, не редактируются вручную.

```powershell
& $Godot --headless --path $Fixture --script "D:/Godot Projects/deal-or-force-godot-4-8/Tools/EditorStartup/tests/verify_scan.gd"
```

Ожидается `SCAN_VERIFY failures=0`. Junction намеренно циклический:
**не удалять fixture рекурсивной командой**, не проверив и не убрав саму ссылку.
Не запускать fixture-скрипт в настоящем игровом проекте.

## Dialogue Manager 4.0.3 и пример Luau

После ремонта Fennara обнаружен второй синхронный обход в
`DMCache.prepare()`. `dialogue-manager-4.0.3-scan-boundary.patch` ограничивает
поиск `.dialogue` теми же `.gdignore` / скрытыми папками / вложенными проектами
и directory links. Плагин не отключён; null при открытии папки обработан,
`list_dir_end()` вызывается явно. Лицензия —
[Dialogue Manager MIT](DIALOGUE_MANAGER_LICENSE.md).
Патч применяется из корня игры; он сохранён отдельно, потому что установленный
аддон локально игнорируется Git. После обновления сначала проверять совместимость.

`tests/verify_dialogue_scan.gd -- <production dialogue_cache.gd>` запускается
на том же отмеченном fixture, что и нативный probe. Он извлекает и компилирует
**дословно настоящую функцию**, без остальных зависимостей DMCache;
это 9 проверок границ и поиска, не приёмка всех интерфейсов диалогов.

У свободного примера `mods/luau_events/war_damage.mod.lua` возвращаемая таблица
находится после функций; `.mod.lua` отличает модуль от Godot-класса в
установленном адаптере. `tests/verify_luau_parse.gd -- <DLL> <module>` проверяет
настоящий адаптер и компиляцию модуля (2 PASS), но не игровые эффекты событий.
Для этого probe скопировать DLL в fixture и передавать **копию**: Godot создаёт
соседнюю `~`-копию даже при `reloadable=false`, не надо конкурировать за неё
с открытым редактором. Первая проверка с общей DLL выявила такой lock;
это не объявлено ошибкой игрового Luau-модуля.

## Локальные диагностические помощники

`Inspect-EditorWaits.ps1 -EditorProcessId <PID>` читает Windows wait chains.
`Inspect-EditorNativeStack.ps1 -EditorProcessId <PID> [-ThreadId <TID>]` кратко
останавливает один проверенный поток для `GetThreadContext` и закрепления
диагностической копии directory handle, возобновляет его в `finally` до
получения имени пути и разрешения символов. По умолчанию — главный поток;
явный TID должен принадлежать этому редактору.
Нет инъекции, дампа, загрузки символов из сети или
завершения чужого процесса. Запускать только для проверенного Godot `--editor`.
`stack_extension_candidate` — адрес-кандидат из стека, **не доказанный backtrace**;
аргументы файлов печатаются только для соответствующего системного вызова.
Отсутствие цикла WCT само по себе не исключает зависание.

Результаты установки, резервная копия и ограничения — в
[отчёте](../../docs/EDITOR_STARTUP_FENNARA_FIX_2026_10_28_RU.md).
