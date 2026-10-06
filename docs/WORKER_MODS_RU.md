# Go, Rust, Luau и управляемые моды: контракт v1

Основная игра остаётся C#/GDScript. Подключение другого языка не создаёт вторую симуляцию: мод получает снимок и возвращает команды той же кампании.

## Что реализовано

| Язык | Рабочий путь | Ограничение |
|---|---|---|
| GDScript | существующий `corecode_script`, хуки через ModApi | доверенный код Godot, не песочница |
| F# | `IGlobalityMod`, `corecode_assembly`, отдельный AssemblyLoadContext на мод | зависимости DLL/deps.json должны лежать рядом; доверенный .NET-код |
| Go | скомпилированный worker_executable, JSON stdin/stdout | только одиночная игра; не язык скриптов редактора |
| Rust | тот же worker-протокол; шаблон serde в `_templates/rust_worker` | шаблон требует Cargo; сборка проверяется отдельно от работоспособности хоста |
| Luau | официальный Luau Compiler + VM в `Tools/LuauWorker`, типизированный `.luau` | отдельный процесс, ограниченная VM, не интеграция классов Godot |

Шаблоны выключены по умолчанию. Скопируйте один из `mods/_templates/*_worker` непосредственно в `mods/<ваш_мод>`, соберите его бинарник и включите `enabled`. Редактор не требует глобальной установки этих языков для запуска уже собранного мода. Для каждого ОС/архитектуры нужен свой бинарник. Игра **не компилирует автоматически** скачанный исходный код.

```json
{
  "id": "my_worker", "name": "My worker", "enabled": true,
  "worker_executable": "corecode/my-worker.exe",
  "worker_interval_days": 7, "worker_timeout_ms": 500
}
```

Для Luau дополнительно задайте `worker_script: "corecode/main.luau"` и скопируйте собранный `deal-or-force-luau.exe` в `corecode`. Пути относительно мода; абсолютные пути и выход через `..` отвергаются. Это проверка размещения, не защита от вредоносного нативного кода или ссылок файловой системы.

## Сборка примеров

Go (из папки `go_worker/corecode`): `go test ./...`, затем `go build -o example-go.exe .`. Шаблон использует Go 1.24.0; Go с поддержкой автоматического выбора toolchain может скачать эту версию при первом запуске.

Rust (из `rust_worker/corecode`): `cargo build --release --locked`; результат `target/release/example-rust.exe` скопируйте в `corecode/example-rust.exe`. В этой итерации пример собран Rust 1.99.0; Cargo.lock закрепляет зависимости. Для локальной проверки компилятор установлен в `build/rustup` и `build/rust-cargo` без изменения системного PATH.

Luau (из корня игры): `cmake -S Tools/LuauWorker -B build/luau-worker`, затем `cmake --build build/luau-worker --config Release`. Первый configure получает официальный исходный код Luau из GitHub, закреплённый по commit в CMakeLists. На Windows нужен C++ build toolchain. Результат находится в `build/luau-worker/Release/` для многоконфигурационного генератора.

F#: используйте существующий `_templates/fsharp_mod`, собирайте проект с зависимостями. Загрузчик теперь разрешает зависимости через AssemblyDependencyResolver, затем соседние DLL; контракт игры/Godot делится с хостом, а не загружается повторно. Unload — кооперативный: сохранённые модом потоки/ссылки могут помешать освобождению контекста.

## Протокол

Хуки `load`, `campaign_created`, `day_advanced` запускают **новый** процесс. Состояние между вызовами не сохраняется. День кампании и seed передаются хостом; stdout предназначен только для одного JSON-ответа, диагностические ошибки — stderr.

```json
{"protocol":1,"event":"day_advanced","day":365,"seed":802,
 "nations":[{"tag":"GBR","name":"United Kingdom","population":67000000,"stability":0.7}],
 "features":{"aviation":false}}
```

```json
{"protocol":1,"logs":["Year one: aviation unlocked"],"commands":[
  {"type":"set_feature","feature":"aviation","enabled":true}
]}
```

Допустимы `set_feature`, `improve_relations`, `reaffirm_alliance`, `enact_policy`,
`manage_debt`, `course_shock`. Отношения и союз требуют `issuer`/`target` — реальные
разные теги стран; игровые правила стоимости, условий и перерывов остаются в командах.
Это ограниченный API, не доступ ко всем полям/эффектам игры. Весь пакет структурно
валидируется перед постановкой команд в очередь: повреждённый ответ не ставится
частично. Исполнение отдельных допустимых команд не является транзакцией:
если игровые условия изменились, команда может отказать независимо от соседних.

```json
{"protocol":1,"commands":[
  {"type":"enact_policy","issuer":"KOR","policy":"fiscal_discipline"},
  {"type":"manage_debt","issuer":"KOR","action":"borrow","amount":0.1},
  {"type":"course_shock","issuer":"KOR","strength":0.4,"reason":"mod event"}
]}
```

`enact_policy`: `promise:true` выбирает каталог обещаний, иначе закон; для внешнего
закона нужен `target`, для регионального `region` — числовой MapIndex региона,
не ID тактической провинции. Права на территорию проверяются при исполнении.
`manage_debt`: `borrow`, `repay` с положительным конечным `amount` либо `restructure`
без суммы. Денежные единицы — миллиарды, как в симуляции: 0.1 = 100 млн.
`course_shock`: конечная сила (0,1], причина до 256 символов. Не заменяет курс
немедленно. `reaffirm_alliance` принимает необязательные `program` и `cancel`;
программа по умолчанию — `coordination`, расходы оплачивает issuer.

В запрос добавлены `capabilities` и поля страны: `treasury`, `public_debt`,
`debt_arrears`, `credit_confidence`, `debt_condition`, `course`,
`course_initialized`, `inertia`, `shock`, `convictions`, `laws`,
`promise`. Старые поля и `protocol:1` сохранены. Неизвестные поля следует
игнорировать. Пакет принят в очередь — ещё не результат симуляции. Нельзя
повторять `borrow`/`repay` автоматически: операции не идемпотентны.

Вход ограничен 2 Mi символов, ответ 256 Ki, stderr 16 Ki; не более 32 команд и 32 сообщений по 1024 символа. Timeout ограничен диапазоном 50–5000 ms; интервал 1–365 дней. После сбоя worker выключается до новой кампании, процесс и его дерево завершаются. Синхронный хук может задержать игровой тик до timeout: тяжёлые вычисления следует делать инструментами вне игры, не увеличивать этот бюджет.

## Безопасность и сеть

Внешний exe и .NET/GDScript-мод имеют права пользователя. Это **не OS-песочница**. Запускайте только доверенные моды. Luau-runner не предоставляет скрипту файлов, сети или загрузку библиотек: использует штатный sandbox Luau, 64 MiB для памяти VM, лимиты входного исходника и внешний deadline. Компилятор и сам нативный процесс не имеют общего OS-лимита памяти.

Worker v1 отключается при нескольких человеческих местах кампании; сетевой lockstep потребует отдельной проверки идентичности модов, воспроизводимости и репликации команд. Не обещаем поддержку multiplayer для этих workers.

## Как сюда относится GDExtension

GDExtension загружает библиотеку через C ABI Godot (`.gdextension` описывает платформы и entry point). В Rust можно использовать gdext: собрать библиотеку, описать её в `.gdextension`, зарегистрировать классы/методы. Нужны совместимые версии Godot, биндинга и экспортные библиотеки для каждой платформы.

Это нативное расширение, **не автоматическая поддержка произвольного языка скриптов**. Для полноценного языка в инспекторе/редакторе нужен адаптер ScriptLanguageExtension/ScriptExtension: загрузка исходников, создание экземпляров, свойства, методы, сигналы и отладка. В текущей работе такой адаптер Go/Rust/Luau не добавлен; для лёгкого моддинга используется более простой worker-контракт. Форк Godot пока не нужен.

Первичные источники: [GDExtension](https://docs.godotengine.org/en/stable/engine_details/engine_api/gdextension/what_is_gdextension.html), [ScriptLanguageExtension](https://docs.godotengine.org/en/stable/classes/class_scriptlanguageextension.html), [godot-rust](https://godot-rust.github.io/book/), [.NET plugin loading](https://learn.microsoft.com/en-us/dotnet/core/tutorials/creating-app-with-plugin-support), [Luau sandbox](https://luau.org/sandbox/).
