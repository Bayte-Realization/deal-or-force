# Языки расширений: фактическое состояние

Игра использует одну C#-симуляцию и C#/GDScript-интерфейс. Моды не создают отдельный мир: изменения идут через контекст или команды этой кампании.

## Поддерживаемый комплект разработки — 16 октября 2026

По решению автора в проекте сохраняется набор **Luau, F#, Nim, Odin, Rust,
Kotlin**, помимо штатных C#/GDScript. Исходники адаптеров, исправления, примеры,
лицензии и фиксированные upstream-ревизии находятся в
[Tools/GodotLanguages](../Tools/GodotLanguages/README.md).
Все шесть совместно проверены в отдельной лаборатории настоящего
Godot `4.8.beta1.mono.official.4d10419ca`, Windows x64/.NET 10.
Локальная установка автора: `C:/GodotEngineNet 4.8-Beta1/LanguageKit`.

Это поддержка комплекта разработки, **не** обещание шести готовых языков Attach
Script в самой игре или проверенных экспортов для всех платформ:

| Язык | Проверенный уровень комплекта | Роль и оставшиеся ограничения |
| --- | --- | --- |
| Luau | ScriptLanguageExtension, `.luau`, модули/Callable/экземпляры. **Beta2, 14 ноября 2026:** привязки впервые собраны из API самой Beta2 (`--dump-extension-api` в каталоге исходника, SHA-256 `2C4306B1…D5B7C`), а не из дампа 4.4; для этого пять патчей (`LanguageKit/adapters/luau/patches`: пропуск сырых указателей в генераторе, editor-only синглтон `GDScriptLanguageProtocol`, typed dictionary/`meta: required`/`INT*_MAX` в godot-cpp 4.4). Собираются `editor` и `template_release`, дескриптор получил настоящую `windows.release`. Игровой вариант (`-Suffixes lua-and-luau`: `.lua` и `.luau`, `require` сначала `.luau`) прошёл `probe.ps1 -Release -EngineModDirectory mods/_templates/luau_engine/corecode` в отдельном проекте: в редакторском бинаре и в **экспортированной** release-сборке `probe.luau`/`probe_dual.lua` (add=42, независимые экземпляры, read-only Callable), последовательность `EngineScriptMod.Load/Invoke` над настоящим `main.lua` (`on_load`/`on_campaign_created`/`on_day_advanced` → `true`, `set_feature("aviation")` ровно на 365-й день), `require` модуля `war_damage.mod.lua`; exit 0, ни одной ERROR/утечки. Старая игровая DLL (`8FC1FE10…`) на той же пробе падает: `loader does not recognise .luau`. Вариант комплекта (`-Suffixes luau`): `C:/GodotEngineNet 4.8-Beta2/LanguageKit/workbench/verify-beta2.ps1 -Languages luau` → debug и экспортированный release `LANGUAGE_KIT_PASS`, exit 0; та же DLL в копии `projects/AllSix` → `LANGUAGE_WORKBENCH_PASS languages=luau failures=0`; `verification.luau` в `sources.lock.json`, Doctor больше не пишет `HISTORICAL-ONLY`, а `RUNTIME-SMOKE-VERIFIED`. Worker: `FormableTests --worker-only` (сборка в отдельный `--artifacts-path`) — 25 PASS, exit 0 | Модовые формулы и события. **Игре нужна переустановка:** `game/extensions/luau` всё ещё держит DLL от 5 октября (API 4.4, только `.lua`, editor-DLL на месте release); заменить можно только при закрытом редакторе: `Tools/LanguageAdapters/install-luau.ps1 -Activate`. До этого `EngineScriptMod` на `.luau`-входе сразу говорит, что адаптер старый, вместо `No loader found`. Upstream архивирован; две сборки Fumohouse вместе не загружать; замыкания GDScript в Luau не сертифицированы; экспорт самой игры с Luau (`Tools/build.ps1 -IncludeExperimentalNativeLuau`) не проверялся |
| F# | Игровая net8-библиотека в net10 Godot-хосте + C#-мост | Готовность к атаке реально перенесена в Scripts/Rules; 624502 сравнений, нулевая аллокация вызова. Не Attach Script; упаковка требует совпадающих экспортных шаблонов. **Beta2, 14 ноября 2026:** `LanguageKit/adapters/fsharp/build.ps1` + headless-проба → `LANGUAGE_KIT_PASS language=fsharp`, exit 0; копия `projects/AllSix` в `tmp_logs/languages/allsix-fsharp-beta2`, `--headless --script res://combined_probe.gd -- fsharp` → `LANGUAGE_WORKBENCH_PASS languages=fsharp failures=0`, exit 0, GodotSharp `4.8.0-beta.2` (`cafc17a19`). `LanguageKitFSharp.csproj` закреплён на `Godot.NET.Sdk/4.8.0-beta.2`, в `sources.lock.json` записано `verification.fsharp`, Doctor печатает `RUNTIME-SMOKE-VERIFIED`. Игровые F#-правила: `FormableTests --rules-backends-only` 21 PASS, `--assault-rules-only` 9, `--peace-rules-only` 17, `--policy-goal-rules-only` 35, `--war-rhythm-only` 11, все exit 0. `--rules-latency-only`, три прогона: импульс F#-делегатом 4,52–4,57 нс против 2,47–2,51 нс C#-эталона (≈1,8×), штурм напрямую F# 30,6–32,8 нс, через hook 32,8–34,2 нс. Порог для переноса любого попровинциального ежедневного цикла в F# — медиана не хуже 1,2× от C#; импульс его не проходит, поэтому такие циклы остаются в C# |
| Nim | GDExtension-класс и Windows runtime bootstrap | Обработка данных и инструменты авторов; generated API старый, полный API 4.8/экспорты не приняты |
| Odin | Прямой C ABI GDExtension, Variant/ptrcall/владение. **Beta2, 14 ноября 2026:** собранная на Beta1 `dof_odin.dll` (SHA-256 `A241BCEE…`) в отдельном scratch-проекте `tmp_logs/languages/odin-beta2/probe`, headless, `--script res://probe_beta2.gd` → exit 0, `ODIN_PROBE_BETA2 PASS` (20, 0, −20, int64 4294967297 и 6·2^40, прямой вызов и `call`/`callv`, 10 000 экземпляров, чистое закрытие без ошибок и утечек). Все 13 функций интерфейса, которые вызывает адаптер, есть в `--dump-gdextension-interface` Beta2; четыре помечены deprecated (`classdb_construct_object`, `mem_alloc`, `mem_free`, `classdb_register_extension_class2`) | Плотные буферы, raster/overlay, preprocessing; полный Temple/Godin генератор пока не готов. **Пересобрать на Beta2 нельзя:** компилятора Odin на машине нет, `toolchains.windows.lock.json` его не называет (только go/zig/rust), исходника odin-godot `9339590` в кэше нет. Поэтому нет DLL ядер `raster_overlay` и `ownership`, нет паритета и замеров; `FormableTests --raster-kernel-only <dir>` и `DOF_EXPERIMENTAL_KERNEL=odin` готовы и ждут DLL. Нужно решение автора внести Odin в lock-файл. Подробности: `LanguageKit/adapters/odin/README.md` |
| Rust | gdext-класс с bindings из установленного 4.8 | Только редкие сложные задачи с доказанной пользой и изолированным интерфейсом; не язык по умолчанию для игры |
| Kotlin | Godot-JVM RC4, `.kt` ресурс и вызов метода | JVM-инструменты/библиотеки, отдельные сценарии; Java/Gradle и deployment требуют сопровождения |

Состояние мира, команды, сохранения и сетевая авторитетность остаются в C#.
Ни игрокам, ни авторам обычных JSON/YAML-модов не требуется изучать/устанавливать
все шесть языков. Рецепты и ограничения:
[руководство](../Tools/GodotLanguages/GUIDE_RU.md),
[свидетельства совместной проверки](GODOT_LANGUAGE_KIT_2026_10_16_RU.md).

**18 октября, продолжение:** F# остаётся единственным вторым языком, реально
подключённым к игровому правилу; в этом проходе новых native runtime-языков в
кампанию не добавляли. Luau/Nim/Odin/Kotlin по-прежнему требуют отдельной
измеренной границы, экспортной приёмки и владения бинарниками; следующий
осмысленный кандидат — Nim-валидатор данных карт или Luau-формулы на копии
данных, а не перенос ежедневного тика.

**Текущее решение автора (18 октября): Rust пока не переносим в игровой код.**
Поддержку уже созданного комплекта не удаляем. Постоянное правило для будущего:
применять лишь там, где измеренная польза оправдывает
дополнительную сложность. Автор не должен обслуживать повседневную логику на Rust.
Строгость компилятора сама по себе не делает игру нестабильнее; риски native —
границы ABI, время жизни, упаковка и платформенные сборки. Они требуют проверок
для любого native-языка. Эксперимент связности карты на Rust и Odin не показал
выигрыша по медиане и остался только в Tools, без native-зависимости игры.
Чистые F#-правила готовности к атаке теперь подключены к игре; и сам F#-метод,
и реальный C#-адаптер совпали с замороженным прежним C# assessor в 624 502 случаях.
Игровое состояние, команды и сохранения остались в C#. Измерения, проверенные
границы и следующие задачи собраны в
[отчёте о выборе языковых участков](LANGUAGE_KERNEL_EVALUATION_2026_10_16_RU.md).

## Действующие игровые мосты и прежние проверки

Принятый игровой срез F# и годовой A/B-профиль:
[F#-ядро и оптимизация](FSHARP_CORE_REFACTOR_2026_10_18_RU.md).
Standalone-экспорт пока блокируют отсутствующие .NET export templates Beta1;
успешный запуск из проекта не подменяет проверку экспорта.

| Язык | Подключение | Проверка и границы |
|---|---|---|
| GDScript | существующий `gdscript_entry` / ModApi | язык Godot; доверенный код, не песочница |
| F# | `IGlobalityMod` / `corecode_assembly` | DLL-пример и три хука проверены; теперь отдельный AssemblyLoadContext с разрешением зависимостей каждого мода. **14 ноября 2026, против экспортированной игры Beta2:** `powershell -File Tools/ManagedModTests/run-corecode-load.ps1` собирает шаблон с `-p:GameAssembly=build/2026-11-14_14-48-47/.../DealOrForce.dll` (DealOrForce.csproj не компилируется, Godot не запускается), раскладывает пакет по пути из manifest и запускает `ManagedModTests --corecode-load-only`: путь принят `ModCodePaths`, тип найден в собственном `ManagedModLoadContext`, `OnLoad`/`OnCampaignCreated`/`OnDayAdvanced` исполнены, заём и закон дошли до хоста, после `Unload` контекст действительно собран GC → `CORECODE_LOAD_PASS`, exit 0. Проверка выгрузки опровергаема: с намеренно удержанной ссылкой на экземпляр мода тест падает. Не проверено: загрузка внутри запущенной игры (редактор держит проект) и мод, которому реально нужен FSharp.Core (пример его не загружает) |
| Go | `worker_executable`, протокол JSON v1 | пример скомпилирован и исполняется настоящим игровым хостом в тестах |
| Rust | тот же `worker_executable`, шаблон serde | пример собран Rust 1.99.0 и проверяется настоящим игровым хостом; компилятор размещён изолированно в `build/`, системный PATH не менялся |
| Luau | официальный Compiler + VM, собственный runner | C++ runner собран, типизированный Luau-пример исполнен через игровой хост |
| Luau (нативный) | `engine_script_entry`, ScriptLanguageExtension через GDExtension | экспериментальный Win x64 адаптер; на 4.7.2 ресурсы Script, экземпляры и хуки кампании проверены в игре. 14 ноября 2026 сборка против API Beta2 (`.lua`+`.luau`, editor + template_release) прошла ту же последовательность хуков над `mods/_templates/luau_engine` в отдельном проекте, в том числе в экспортированной release-сборке; в саму игру ещё не установлена (см. строку Luau выше) |

Go/Rust/Luau-worker — **не** встроенный язык редактора Godot и не полная замена C# API. Первый контракт поддерживает включение механик, улучшение отношений и подтверждение союза. Workers доступны только в одиночной игре. Шаблоны выключены по умолчанию.

У .NET-загрузчика есть AssemblyDependencyResolver и поиск соседних DLL. Это устраняет прежнее ограничение общего контекста, но экспортированная игра с произвольными зависимостями F# ещё требует отдельной проверки. Unload кооперативный: сторонний мод может удержать ссылки/потоки.

Схема манифеста, сборка каждого примера, лимиты, безопасность и команды описаны в [WORKER_MODS_RU.md](WORKER_MODS_RU.md). Данные JSON/YAML — отдельный слой: [YAML_MODDING_RU.md](YAML_MODDING_RU.md).

## GDExtension и форк движка

GDExtension подключает нативную библиотеку через C ABI без пересборки Godot. `.gdextension` указывает библиотеку для платформы и entry point; расширение регистрирует классы и методы. Rust может использовать gdext. Для полноценного языка, которому можно прикреплять исходные скрипты к узлам, нужен адаптер ScriptLanguageExtension/ScriptExtension с экземплярами, свойствами, сигналами и отладкой. Наличие биндинга само по себе это не обеспечивает.

Теперь добавлен отдельный экспериментальный Luau-адаптер с установщиком и патчем
возвращаемых значений Callable: [установка, API и ограничения](LUAU_ADAPTER_RU.md).
В игровом мод-хосте Rust/Go остаются workers, F# — сборки через .NET,
GDScript — штатный язык Godot. Отдельная лаборатория выше расширяет возможности
разработки, но не заменяет игровой хост автоматически. Форк движка не создавался.

Эта рабочая копия `deal-or-force-godot-4-8` перешла с Godot 4.8 dev7 на
Godot 4.8 Beta1 / net10.0.
Шаблон F# и ManagedModTests переведены на net10.0, путь DLL в manifest исправлен.
Оригинальный проект 4.7 и его редактор не менялись. Native DLL, рендерер, моды,
сейвы и экспорт требуют собственных проверок: версия движка сама их не гарантирует.
Источник: [официальные заметки dev7](https://godotengine.org/article/dev-snapshot-godot-4-8-dev-7/).

## Дополнения контракта, 6 октября

В общий мост добавлены постановка закона/обещания, операции госдолга и
потрясение национального курса. GDScript/engine API: `get_capabilities`,
`get_politics`, `enact_policy`, `manage_debt`, `add_course_shock`.
F#: `ModContext.EnactPolicy`, `ManageDebt`, `AddCourseShock`, типизированные
`GovernanceHelpers` в шаблоне. Workers: команды `enact_policy`, `manage_debt`,
`course_shock`. Контракт worker остался v1: поля добавлены без переименования старых.
У Godot-callable API и workers разные имена некоторых вызовов; каждый сообщает
свой перечень возможностей, это не обещание полного доступа к симуляции.

Действия идут через обычную очередь команд, повторно проверяют игровые условия
при исполнении и не обходят стоимость/перерывы. `true` у API означает «поставлено
в очередь», не «уже исполнено». Нет автоматического повтора долговых операций.
`course_shock` ускоряет адаптацию, а не мгновенно назначает новую повестку.

Проверено: WorkerBackendTests — 24 PASS, включая реальные Go/Rust/Luau процессы,
неправильные пакеты, timeout и отказ в multiplayer; ManagedModTests — 5 PASS,
включая загрузку F# DLL, три хука и выполнение закона/заимствования через мост.
Экспортированная игра с F#-зависимостями отдельно ещё не проверена.

Историческая игровая проверка нативного Luau относится к 4.7.2, проверка
комплекта — к Beta1-лаборатории с `.luau`. 14 ноября 2026 Luau пересобран против
API Beta2 и проверен на Beta2, включая экспортированный release (строка Luau в
таблице выше; команды — в `LanguageKit/adapters/luau/README.md`). Старая игровая DLL
от 5 октября этой проверке не соответствует и должна быть заменена установщиком.
Luau worker проверен и доступен; весь набор не стал автоматически языками
игрового редактора. Установленные toolchains нужны для сборки исходников,
не для запуска уже собранного Go/Rust worker.

Нативные exe и .NET/GDScript-моды — доверенный код с правами пользователя. Отдельный процесс не является OS-песочницей. Luau-скрипт ограничен VM без файлов/сети, лимитом памяти и deadline; сам runner остаётся доверенным нативным кодом.

Первичные источники: [GDExtension](https://docs.godotengine.org/en/stable/engine_details/engine_api/gdextension/what_is_gdextension.html), [ScriptLanguageExtension](https://docs.godotengine.org/en/stable/classes/class_scriptlanguageextension.html), [godot-rust](https://godot-rust.github.io/book/), [.NET plugins](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support), [Luau sandbox](https://luau.org/sandbox/).
