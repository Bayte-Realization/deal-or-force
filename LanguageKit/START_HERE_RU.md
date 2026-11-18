# Языки в Godot 4.8 Beta3

Это установленный LanguageKit. Основная папка:
`C:\GodotEngineNet 4.8-Beta3\LanguageKit`.

Результат 16 ноября ([отчёт](reports/beta3-2026-11-16.md)): на Beta3 пересобраны и прошли
свою пробу Rust, Luau (и вариант для модов игры с `.lua`), Zig, Go, Lua, Odin, Sandbox и F#.
Старые DLL Nim и Kotlin (сборка Beta1) на Beta3 загружаются и проходят пробу, но не
пересобраны. Python (py4godot) по-прежнему портит число 4294967297 до 1 и оставляет
ошибки очистки при выходе; для `godot4-meson` проверен только парсер API.

Интерфейс GDExtension у Beta2 и Beta3 побайтно одинаковый, в API классов изменились
только `Line3D` → `LineBase3D` и аргументы `Node.replace_by`.

| Что нужно | Где это лежит |
| --- | --- |
| Рецепты, код примеров и патчи | `adapters` |
| Переносимые компиляторы Rust 1.99.1, Zig 0.17.0, Go 1.27.3, Odin dev-2026-10, MinGW (w64devkit 2.10.1) | `.cache/toolchains` |
| Закреплённые исходники сторонних связок | `.cache/sources-beta3` |
| Готовые пробные проекты | `.cache/verified-beta3/<язык>/project` |
| Логи и матрица результатов | `.cache/verified-beta3/verification-*`, `.cache/logs` |
| Проект со всеми шестью языками сразу | `projects/AllSix` |
| Пробы свежих веток gdext и godot-sandbox (не закреплены) | `.cache/experiments-beta3` |
| Старые источники и квитанции обновления | `../LanguageKit-backups` |

`Open Verified Probes.cmd` откроет Rust. Для Zig или Go в PowerShell 7:

```powershell
./workbench/open-verified.ps1 -Language zig
./workbench/open-verified.ps1 -Language go
```

Проверка состояния, пересборка и пробы:

```powershell
./language-kit.ps1 -Action Doctor
./workbench/build-local.ps1 -Languages rust,zig,go
./workbench/verify-beta3.ps1 -Languages rust,zig,go,luau
./tests/kit-tests.ps1
./tests/probe-contracts.ps1
```

На новой машине сначала `./workbench/install-portable-toolchains.ps1`
(для Odin и Sandbox: `-Languages odin,w64devkit`, нужен установленный 7-Zip);
для Rust и Odin дополнительно нужны уже установленные MSVC C++ tools и Windows SDK.
Постоянный PATH, системные компиляторы, движок и игровые addons не заменяются.

Первый «холодный» `--headless --import` любого проекта падает при выходе с кодом
`-1073741819` — и на Beta3, и на Beta2, даже в пустом проекте. Языки тут ни при чём;
скрипты пакета сначала коротко запускают редактор без окна, а потом импорт, и так ошибки нет.

Это не доказательство ускорения игры и не разрешение запускать неизвестные
нативные моды: связки сами по себе не являются защитной песочницей.
