# Правила имён и форм данных (`DATA_RULES_RU`)

Единый стандарт для каталогов `game/` и `mods/_templates/` (а также для новых модов).
Автоматический замок против новых отступлений — тест `ContentShapeTests` (`Tools/FormableTests --content-shape-only`).

## 1. Имена файлов

- Стандартное имя файла данных: **`NN_тема.json`** (или `.yml` / `.yaml`), где `NN` — две цифры порядка загрузки (`00`..`99`), а `тема` — латиница в `snake_case` (`^[0-9]{2}_[a-z0-9_]+\.(json|yml|yaml)$`).
- Загрузка на один уровень в пределах каталога вида:
  - `common/<вид>/*.json|yml|yaml`, `events/*.json|yml|yaml`, `interface/*.json|yml|yaml`, `gui/*.json|yml|yaml`, `fonts/*.json|yml|yaml`, `sound/*.json|yml|yaml`, `music/*.json|yml|yaml`, `presentation/*.json|yml|yaml`;
  - явно допускается один тематический подуровень там, где он задан структурой:
    - `history/{countries,states,diplomacy,conflicts,factions,units}/<сценарий>/` (например `2024/`) и `history/provinces/terrain/`;
    - `localization/<язык>/`;
    - `map_data/{cities,crossings,lakes,townspeople,state_regions,hydrography}/`;
    - `scenarios/<id>/scenario.json`, `skins/<id>/skin.json` и ресурсы под `gfx/**`.
- Вложенные глубже подпапки не подхватываются втихую: `GameDataLoader.TopLevelDataFiles` пишет явное предупреждение в `package.Errors` и пропускает файл.

## 2. Идентификаторы (`id`)

- Стандартный идентификатор верхнего уровня — **`snake_case`** (латиница/цифры/`_`; для токенов интерфейса, шрифтов и локализации допускаются разделённые точкой сегменты вроде `text.accent` или `ui.display`).
- Регулярное выражение для ключей: `^[a-z0-9_]+(\.[a-z0-9_]+)*$`.

## 3. Форма корня файла

- По умолчанию каждый файл данных — это плоский JSON/YAML-объект формата **«`id` → объект»**:
  ```json
  {
    "my_entry": {
      "name": "Example"
    }
  }
  ```
- **Новые внешние обёртки запрещены.** Нельзя заворачивать записи нового каталога в `{ "items": { ... } }`, `{ "entries": { ... } }` или `{ "<имя_папки>": { ... } }`.

## 4. Удаление записей

- Единственный канонический способ удалить запись из базового каталога или раннего мода:
  ```json
  {
    "existing_id": {
      "remove": true
    }
  }
  ```
- Через общую проверку `GameDataLoader.TryReadRemoveFlag` / `GameDataLoader.IsRemoved` ключ `"remove": true` работает единообразно во всех словарных каталогах, принимающих моды.
- Устаревшие псевдонимы `"removed": true` и `"disabled": true` поддерживаются только как совместимость для старых сторонних пакетов (с обязательной записью предупреждения в `ModLoader.Log`) и **строго запрещены** в `game/` и `mods/_templates/`.

## 5. Принятые исторические исключения

Исторические отступления оставлены в явном белом списке в `ContentShapeTests.cs`, чтобы не ломать существующие кампании и сохранения. Новые файлы за пределами этого закрытого перечня тест отклоняет.

1. **Имена файлов**:
   - `history/states/<сценарий>/<ISO3>.json` (по коду страны) и четыре исторических сводки в `history/states/2024/` (`lakes.json`, `unclaimed.json`, `zz_strategic_deposits.json`, `zz_world_resources.json`);
   - трёхзначные/буквенные исторические файлы базы: `common/defines/100_*..108_*`, `common/modifiers/49b_*`, `49c_*`, `common/opinion_modifiers/100_*..101_*`;
   - служебные/геометрические файлы `map_data/*.json`, `map_data/hydrography/*.json`, `gfx/interface/icons/*_palette.json`, `gfx/leaders/anonymous_portrait.source.json`, `gfx/models/trade/*.json`;
   - старые примеры в `mods/_templates/` до рефакторинга (явный закрытый список из 18 файлов).
2. **Регистр ключей (`PascalCase` / `UPPER_TAG`)**:
   - `PascalCase` / legacy: `common/defines`, `common/goods`, `common/buildings`, `common/peace_terms` (переход товаров и зданий на `snake_case` с псевдонимами — отдельный этап после E10);
   - ISO-теги `UPPER_TAG`: `common/countries`, `common/country_merges`, `history/countries/*`, `history/units/*`;
   - ключи секций инспектора `common/editor` и метаданные `anonymous_portrait.source.json`, `_comment`.
3. **Необъектные таблицы и исторические обёртки**:
   - плоские таблицы чисел/флагов/настроек: `common/defines`, `common/inflation`, `common/militia`, `common/secession`, `common/stability`, `common/programme`, `common/world_layers`, `common/diagnostics`, `common/performance`, `common/deterrence`, `common/coups`, `common/news`, `common/flags`, `common/governance`, `history/units/*`, `map_data/*`;
   - многоколлекционные/исторические форматы и слои облика: `common/ai_strategy` (`profiles`, `country_profiles`), `common/budget` (`taxation`, `revenues`, `expenses`), `common/deals` (`answer_kinds`, `trade_presets`), `common/deal_terms` (`trade_agreement`), `common/dynasties` (`dynasties`), `common/editor` (`country`, `region`), `common/event_pictures` (`events`, `keywords`), `common/flag_designs` (`flag_designs`), `common/flags` (`qualifiers`, `flag_context`, `fallbacks`, `identities`), `common/governance` (`transitions`, `base`), `common/handbook` (`topics`, `articles`), `common/naming` (`lands`), `common/organizations` (`charters`, `doctrines`), `common/peace_terms` (`terms`), `common/programme` (`lines`), `common/rulers` (`offices`, `traits`, `portfolios`, `names`), `history/conflicts`, `history/diplomacy`, `history/factions`, `history/pops`, `history/faiths`, `interface`, `gui`, `fonts`, `sound`, `music`, `presentation`, `gfx`, `skins/<id>/skin.json`.

## 6. Проверка и запуск

- Перед правкой или добавлением данных в `game/` или `mods/_templates/` запускайте:
  ```powershell
  FormableTests.exe --content-shape-only
  FormableTests.exe --mod-reach-only
  FormableTests.exe --modder-examples-only
  powershell -ExecutionPolicy Bypass -File Tools\audit-localization.ps1 -Fail
  ```
- В документации и шаблонах `mods/_templates/` не допускаются ссылки на сторонние коммерческие игры/движки и устаревшие пути вроде `net8.0` или `game/interface/skin.json` (актуально: `net10.0`, `game/interface/00_skin.json`).
