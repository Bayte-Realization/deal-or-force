# Языки в Godot 4.8 Beta2

Это установленный LanguageKit. Основная папка:
`C:\GodotEngineNet 4.8-Beta2\LanguageKit`.

Свежий результат 29 октября: Rust, Zig и Go пересобраны, вызваны из Beta2
и чисто завершаются в отдельных пробных проектах. Python пока экспериментален:
py4godot портит число 4294967297 до 1 и оставляет ошибки очистки при выходе.
Для альтернативного `godot4-meson` подготовлен исправленный парсер API и
восемь зелёных регрессионных тестов; полная нативная сборка порта не принята.

| Что нужно | Где это лежит |
| --- | --- |
| Рецепты, код примеров и патчи | `adapters` |
| Переносимые компиляторы Rust 1.99.1, Zig 0.17.0, Go 1.27.3 | `.cache/toolchains` |
| Закреплённые исходники сторонних связок | `.cache/sources-beta2` |
| Готовые пробные проекты | `.cache/verified-beta2/rust/project`, `zig/project`, `go/project` |
| Логи и матрица результатов | `.cache/verified-beta2/verification-*` |
| Старые источники и квитанции обновления | `../LanguageKit-backups` |

`Open Verified Probes.cmd` откроет Rust. Для Zig или Go в PowerShell 7:

```powershell
./workbench/open-verified.ps1 -Language zig
./workbench/open-verified.ps1 -Language go
```

Пересборка и проверка:

```powershell
./workbench/build-local.ps1 -Languages rust,zig,go
./workbench/verify-beta2.ps1
./tests/kit-tests.ps1
./tests/probe-contracts.ps1
```

На новой машине сначала `./workbench/install-portable-toolchains.ps1`;
для Rust дополнительно нужны уже установленные MSVC C++ tools и Windows SDK.
Постоянный PATH, системные компиляторы, движок и игровые addons не заменяются.
Старый AllSix сохранён, но не переаттестован на Beta2. Другие языки остаются
в комплекте с прежними результатами Beta1 — они не объявлены сломанными,
но новая проверка им ещё требуется.

Оговорки раннего прохода «recipe-only» для Zig/Go уже заменены
[проверкой настоящего выполнения](reports/beta2-runtime-2026-10-29.md).
Это не доказательство ускорения игры и не разрешение запускать неизвестные
нативные моды: связки сами по себе не являются защитной песочницей.
