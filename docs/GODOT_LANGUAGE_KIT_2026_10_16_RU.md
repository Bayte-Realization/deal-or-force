# Godot 4.8 Beta1: языковые адаптеры — 16 октября 2026

Реализован отдельный language kit для Luau, F#, Nim, Odin, Rust и Kotlin.
Исходники: `Tools/GodotLanguages/`; [руководство и план рефакторинга](../Tools/GodotLanguages/GUIDE_RU.md).
Локальная установка: `C:/GodotEngineNet 4.8-Beta1/LanguageKit`, лаборатория
`projects/AllSix`, запуск редактора через `Open Workbench.cmd`.

## Принятый результат

- Настоящий `4.8.beta1.mono.official.4d10419ca`, Windows x86-64, .NET 10.
- Каждый язык выполняет реальный вызов из Godot. Не только найдена DLL или
  получена успешная компиляция.
- Все шесть совместно прошли editor bootstrap, обычный повторный импорт,
  runtime с F#/CLR и Kotlin/JVM в одном процессе, завершение 0 без Godot ERROR.
- Та же проверка повторена из установленной возле движка `projects/AllSix`.
- Дополнительная общая лаборатория восьми: отдельные Lua 5.4.8 и Sandbox,
  arithmetic/restrictions, exit 0, no ERROR. Они остаются опциональными.
- 21 контрактная проверка установщика/путей/pins/защиты существующих файлов;
  синтаксический разбор всех 23 PowerShell-скриптов без ошибок.
- Точные commit pins, семь upstream gitlinks, build/probe-скрипты, шаблоны,
  патчи и оригинальные лицензии. Сборки игры и native-бинарники не в Git.

Это **не** перенос расчёта кампании на шесть языков. F# — managed library через
C#; Rust/Nim/Odin — native GDExtension-классы, не Attach Script. Luau и Kotlin
имеют свои script-ресурсы/загрузчики. Пределы API и рецептов описаны в отчётах:
[native](../Tools/GodotLanguages/reports/native-bindings.md),
[Luau/F#/Lua](../Tools/GodotLanguages/reports/luau-fsharp.md),
[JVM/Sandbox](../Tools/GodotLanguages/reports/jvm-sandbox.md).

## Исправления и сохранённые границы

Nim: ранняя инициализация runtime/allocator, `stdint.h`, `noSignalHandler`.
Luau: `.luau`/`.mod.luau` и тип результата vararg Callable. Lua/Sandbox:
устранены конфликты унаследованных имён методов; у Sandbox ранняя регистрация
языков. Rust: API из проверяемого движка. Odin: прямой ABI с явным владением
StringName/String, ptrcall и teardown вместо неработающего полного генератора.
Kotlin: RC4 native source build, JDK 25, target Java 17; явный выбор Java runtime.
Отключение LTO не выдаётся за доказанное исправление гонки. Maven SNAPSHOT
требует checksum lock перед воспроизводимым релизным pipeline.

Godot exe/GodotSharp, настройки/сцены игры и старый Luau не заменены. GUI SHA-256
до/после: `6A76B684EDBA16C12348663CB9759B6ECB42FC4C0969470C69B1A75727E42CF9`.
Пользовательский редактор не останавливался нашими командами. Tools помечен
`.gdignore`; пробники не становятся игровыми автозагрузками. Старый и новый
Fumohouse Luau имеют одинаковые native class names: одновременно не активировать.
Миграция старых `.lua` модов — отдельная задача при закрытом редакторе.

На холодном немедленном импорте воспроизведён access violation, согласующийся с
[Godot #111048](https://github.com/godotengine/godot/issues/111048) и
[предлагаемым исправлением](https://github.com/godotengine/godot/pull/123658).
Без PDB локальный dump не доказывает конкретную строку core. Kit заранее создаёт
производный extension cache, выполняет ограниченный delayed bootstrap, затем
требует clean import/runtime. Ошибки не игнорируются и не превращаются в PASS.
Это workaround, не исправление исполняемого файла Godot.

## Следующий рефакторинг

Одна авторитетная C#-модель команд/save/fingerprint остаётся владельцем мира.
Вынесение только пакетных чистых kernels с managed reference/fallback. Первые
кандидаты: связность/стоимость путей для Rust/Nim, raster/overlay для Odin,
типизированные правила/оценки для F#, capability-события для Luau,
инструменты/валидаторы для Kotlin. Не делаем все runtime обязательными игрокам.

Каждый кандидат требует A/B на настоящем срезе карты с учётом marshaling:
p50/p95, allocations, память, малый/типичный/стрессовый ввод, численная
эквивалентность; затем replay/save-load/network fingerprint. Арифметический
smoke-test не доказывает ускорение игры. Не вводить JNI/Variant вызов на каждую
провинцию/армию и не разносить сетевую авторитетность ради количества языков.

Release/standalone exports, hot reload, все platform/API, длинные кампании,
adversarial sandbox security и production deployment всех языков ещё не приняты.
Полный игровой SimBench не запускался: авторитетная симуляция этим срезом не менялась.

## Свидетельства

Приватные логи находятся в ignored `.fennara/languages` публикуемой рабочей копии,
не являются зависимостью публичного проекта:

- `combined-six-v3.log`, `combined-eight-final.log` — совместные проверки.
- `engine-kit-install.log` — source-only установка возле движка.
- `engine-allsix-install-probe.log` — установленная лаборатория: bootstrap/import/runtime.
- `kit-{rust,nim,odin,kotlin,sandbox}-probe-final.log` — финальные runners.
- Неудачные cold-import/Java-version контроли сохранены и описаны в отчётах.

Готовность лаборатории и готовность экспортируемой игры — разные критерии.
MOD-03 остаётся частичным, MOD-04 не требует форка ради bindings.

## Следующий срез: выбор языка для реальной задачи

Постоянный набор и новое правило автора «Rust только в исключительных сложных
случаях» закреплены в [LANGUAGE_BACKENDS_RU](LANGUAGE_BACKENDS_RU.md) и MOD-03
общего плана. Общая C#-модель остаётся владельцем кампании.

Пакетная связность владений сравнена на Rust/Odin с существующим C# на настоящей
карте и малом/стрессовом вводе. Совпадение полное, медиана native хуже: рабочий
hook снят, исходники/дифференциальный стенд оставлены только в Tools. F#-правила
готовности к атаке прошли 624 502 сравнения с фактическим C# assessor.
Это отдельные опциональные эксперименты, не новые зависимости игроков.
[Точные измерения, проверки и открытые задачи](LANGUAGE_KERNEL_EVALUATION_2026_10_16_RU.md).
