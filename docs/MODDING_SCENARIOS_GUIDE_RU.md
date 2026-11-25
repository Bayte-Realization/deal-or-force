# Гайд по созданию сценариев и тотальных конверсий в Deal or Force (за 10 минут)

Этот гайд показывает, как собрать **исторический** (1444 / 1914 / 1936), **футуристический / Sci-Fi** (2240), **альтернативно-исторический** или **фэнтези-сценарий** без правки C#-кода движка, без сотен файлов-заглушек и без поломки базовой кампании 2024 года.

---

## Быстрый старт за 10 минут

1. Выберите один из двух готовых шаблонов в [`mods/_templates/`](../mods/_templates/README.md):
   - **На базе карты Земли (история / альтернатива / будущее)**: скопируйте `mods/_templates/historical_1914_era/` в `mods/my_era_mod/`.
   - **Полная конверсия (фэнтези / далёкое будущее / своя карта)**: скопируйте `mods/_templates/chronicles_of_aethelgard/` в `mods/my_world_mod/`.
2. Откройте `manifest.json`: задайте уникальный `"id"` (строчные латинские буквы, цифры, `_`) и поставьте `"enabled": true`.
3. Настройте дату, календарь, эпохальные фичи, войны/союзы и избранные державы в `scenarios/<id>/scenario.json`.
4. Прогоните встроенный префлайт-валидатор `ModValidator.ValidateMod(modDir)` (или тесты `--scenario-modding-only`) — он покажет точный файл, путь к полю и готовую подсказку исправления.

---

## 1. Структура `manifest.json` для конверсий и эпох

```json
{
  "id": "my_1914_mod",
  "name": "Июльский кризис 1914",
  "version": "1.0.0",
  "supported_game_version": "*",
  "enabled": true,
  "load_order": 200,
  "dependencies": [],

  "total_conversion": false,
  "era_name": "Great War Era",
  "replace_paths": [
    "events",
    "common/technologies",
    "common/decisions"
  ],
  "exclude_paths": [
    "events/20_ukraine.json"
  ],
  "disabled_features": ["nukes", "drones", "missiles"],
  "era_features": {
    "aviation": "restricted",
    "air_defence": "restricted"
  },
  "theme": "bronze_atlas",
  "music_playlist": "great_war_orchestra"
}
```

### Что делают `replace_paths`, `exclude_paths` и `total_conversion`

- **`replace_paths`** — чистая замена базовых категорий 2024 года. Если указать `"common/technologies"` или `"events"`, движок **вообще не загрузит** файлы 2024 года из этой папки (а для `common/decisions` пропустит даже встроенные дефолты), и каталог наполнится только файлами вашего мода. Больше не нужно писать сотни `"remove": true`!
  Разрешённые виртуальные корни (`ModOverlayPolicy.AllowedReplaceRoots`):
  `common/technologies`, `common/decisions`, `common/ideologies`, `common/governments`, `common/goods`, `common/units`, `common/buildings`, `common/production`, `common/budget`, `common/laws`, `common/agendas`, `common/national_courses`, `common/goals`, `common/grand_projects`, `common/promises`, `common/modifiers`, `common/opinion_modifiers`, `common/ai_strategy`, `common/formable_nations`, `common/organizations`, `common/religions`, `common/cultures`, `common/rulers`, `common/deterrence`, `common/support`, `events`, `history/countries`, `history/states`, `history/diplomacy`, `history/conflicts`, `history/factions`, `history/units`, `history/provinces`, `localization`, `music`, `interface`, `gui`, `skins`.
- **`exclude_paths`** — точечное отключение отдельных файлов базовой игры (например, `"events/20_ukraine.json"`), когда вы хотите сохранить остальную категорию 2024 года.
- **`total_conversion: true`** — включает режим тотальной конверсии. Если `replace_paths` не задан явно, автоматически подавляются базовые нарративные корни 2024 года (`events`, `common/technologies`, `common/decisions`, `history/diplomacy`, `history/conflicts`, `history/factions`), сохраняя структурную экономику, пока вы сами не замените её.
- При возврате к обычной кампании 2024 года (без активных конверсий) `ModOverlayPolicy` полностью сбрасывается — базовая игра и её `CampaignFingerprint` остаются бит-в-бит неизменными.

---

## 2. Декларативный `scenarios/<id>/scenario.json`

В одном файле (поддерживаются **JSON** и **YAML/YML**) описывается вся стартовая конфигурация сценария:

```json
{
  "id": "great_war_1914",
  "name": "July Crisis: 1914",
  "description": "28 July 1914. Europe mobilises as the Great War begins.",

  "base_world": true,
  "clear_base_wars": true,
  "clear_base_diplomacy": true,
  "player_country": "DEU",

  "start_year": 1914,
  "start_month": 7,
  "start_day": 28,
  "calendar": {
    "id": "gregorian_1914",
    "era_name": "Great War Era",
    "era_short": "AD",
    "year_offset": 0,
    "format": "{day} {month} {year} {era_short}",
    "show_era_in_date": true
  },

  "skin": "bronze_atlas",
  "music_playlist": "great_war_orchestra",

  "disabled_features": ["nukes", "drones", "missiles"],
  "era_features": {
    "aviation": "restricted",
    "air_defence": "restricted"
  },

  "featured_countries": [
    { "tag": "DEU", "hook": "Two-front industrial powerhouse.", "difficulty": "medium" },
    { "tag": "FRA", "hook": "Defend the Republic.", "difficulty": "medium" }
  ],
  "starting_alliances": [
    { "name": "Triple Entente", "charter": "collective_defence", "members": ["FRA", "RUS", "GBR"] }
  ],
  "starting_wars": [
    { "name": "The Great War", "attacker": "AUT", "defender": "SRB", "justification": "july_crisis" }
  ],
  "starting_truces": [
    { "between": ["GBR", "USA"], "days": 365 }
  ]
}
```

### Ключевые удобства для авторов

1. **Без 20 МБ `world.dfsave` для сценариев на Земле**: укажите `"base_world": true`, `"clear_base_wars": true`, `"clear_base_diplomacy": true` — сценарий стартует на карте Земли, очистит войны/блоки 2024 года и мгновенно применит вашу дату, календарь, союзы, перемирия и стартовые войны.
2. **Своя карта / вымышленный мир**: укажите `"world": "world.dfsave"` (как в `chronicles_of_aethelgard`) — подойдёт любой компактный JSON или экспортированный из редактора `world.dfsave`. Прежние моды с `world.bdfsave` продолжают открываться автоматически.
3. **Строгая проверка дат**: принимаются `start_year` / `start_month` / `start_day` или `"start_date": "1914-07-28"` / `"0300-03-15 BCE"`. Невалидные даты (например `1914-02-30` или `0000-01-01 BCE`) отклоняются с понятной ошибкой вместо тихого смещения.

---

## 3. Пользовательский календарь и эпохи (`CalendarProfile`)

Внутренние часы симуляции (`Campaign.ElapsedHours` и `GameDate`) всегда остаются детерминированными, а отображаемый календарь настраивается отдельно:

| Поле | Назначение | Пример |
| --- | --- | --- |
| `era_name` | Полное название эпохи (ключ локализации) | `"Third Sun"`, `"Age of Iron"`, `"Galactic Common Era"` |
| `era_short` | Короткий суффикс эры | `"3S"`, `"AD"`, `"G.C."` |
| `year_offset` | Смещение отображаемого года относительно внутреннего | `0` (или `1000` для сдвига шкалы) |
| `format` | Шаблон полной даты (`{day}`, `{month}`, `{year}`, `{era}`, `{era_short}`) | `"{day} {month}, Year {year} of the {era}"` |
| `year_format` | Шаблон отображения одного года | `"Year {year} of the {era}"` |
| `month_names` | Массив ровно из **12** названий месяцев (локализуются через `Loc.Get`) | `["Frostfall", "Deepwinter", ...]` |
| `show_era_in_date` | Показывать ли эру в стандартном формате | `true` |

Настроенный календарь сохраняется в сейвах (`SaveSystem`) и учитывается в `CampaignFingerprint`, не затрагивая ни одного байта в стандартной кампании 2024 года.

---

## 4. Управление механиками эпохи (`disabled_features` и `era_features`)

Механики поддерживают **три состояния** (`EraFeatureState`):
- **`available`** — включена со старта.
- **`restricted`** — выключена на старте, но остаётся в `Campaign.RestrictedFeatures` и может открыться позже через `FeatureUnlocks` (технология, год, событие).
- **`unavailable`** (или попадание в `disabled_features`) — полностью отключена в этой эпохе: скрываются экраны UI, блокируются приказы `ICampaignCommand`, останавливается ИИ и удаляются авторазблокировки.

### Эргономичные алиасы (`EraFeatureRegistry`)

Вместо запоминания 55 внутренних ID можно писать короткие групповые алиасы (или любые точные ID из `MechanicFeatures`):

| Алиас | Раскрывается в |
| --- | --- |
| `nukes` / `nuclear` / `nuclear_weapons` | `defcon`, `nuclear_posture`, `nuclear_strikes` |
| `missiles` / `ballistic_missiles` | `long_range_forces` |
| `modern_air_force` / `air_warfare` | `aviation`, `drones`, `air_defence` |
| `uavs` | `drones` |
| `modern_intelligence` / `cyber` | `intelligence` |
| `global_organizations` | `organizations` |
| `proxy_warfare` | `proxy_wars` |
| `modern_finance` | `public_debt` |

Повторное отключение через перекрывающиеся алиасы (например, `modern_air_force` + `drones`) корректно объединяется, а **противоречие** (`modern_air_force: unavailable` и одновременно `drones: available`) сразу ловится валидатором с кодом `ERR_FEATURE_CONFLICT`.

---

## 5. Префлайт-валидатор `ModValidator`

`ModValidator.ValidateMod(modRoot)` и `ModValidator.ValidateModSet(modRoots)` работают на чистом C# без запуска движка Godot и проверяют:

- `ERR_MANIFEST_*` — наличие и схему `manifest.json`, формат `id`, зависимости, циклы зависимостей (`ERR_DEPENDENCY_CYCLE`), дубликаты ID (`ERR_DUPLICATE_MOD_ID`).
- `ERR_DATA_SYNTAX` — синтаксис всех `.json`, `.yaml`, `.yml` файлов во всех папках мода.
- `ERR_INVALID_REPLACE_PATH` / `ERR_INVALID_EXCLUDE_PATH` / `WARN_REPLACE_PATH_CONFLICT` — защита от `..`, проверка белого списка корней и конфликты `replace_paths` между независимыми модами.
- `ERR_INVALID_DATE` / `ERR_INVALID_CALENDAR` — проверка реальности дат (високосность, 30 февраля, 0 год до н. э.) и 12 месяцев.
- `ERR_UNKNOWN_COUNTRY_TAG` / `ERR_DIPLOMACY_CONFLICT` — несуществующие теги стран, война с самим собой, одновременная война и союз/перемирие.
- `ERR_TECH_MISSING_PREREQ` / `ERR_TECH_CYCLE` — битые ссылки и циклы в дереве технологий (с учётом того, заменены ли базовые технологии через `replace_paths`).
- `ERR_UNKNOWN_EFFECT` / `WARN_DISABLED_FEATURE_REF` — неизвестные ключи эффектов в событиях/решениях и обращения к механикам, выключенным эпохой.
- `WARN_LOC_MISSING_KEY` / `WARN_LOC_EMPTY_VALUE` / `ERR_LOC_PLACEHOLDER_MISMATCH` — паритет английской и русской локализаций и совпадение плейсхолдеров `{0}`, `{1}`.

Каждая диагностика возвращает: `Severity`, `Code`, `File`, `Path`, `Message` и **`SuggestedFix`** (понятный совет, как исправить проблему).

---

## 6. Хирургические патчи (`patches/*.json`) и директива `"$op"`

Чтобы изменить цену одной технологии, добавить модификатор или подружить два чужих мода, **не нужно копировать целый файл из `game/`**. В игре есть два взаимно дополняющих механизма:

### 6.1. Встроенный `"$op"` в записях `common/`, `events/`, `history/`

В любой записи JSON/YAML внутри вашего мода можно указать поле `"$op"`:

| Значение `"$op"` | Поведение |
| :--- | :--- |
| `"inject"` | Глубоко объединяет вложенные объекты (`effects`, `modifiers`, `trigger`) и добавляет уникальные элементы в массивы (`requires`, `options`, `tags`), не затирая остальные поля базовой записи. |
| `"replace"` | Полностью заменяет существующую запись по ID (удаляя прежние поля). |
| `"create"` | Добавляет запись **только если такого ID ещё нет**; при конфликте запись пропускается с предупреждением `WARN_OP_CREATE_EXISTS` (чужой мод не затирается). |

Пример `common/technologies/50_extra_req.json`:

```json
{
  "precision_munitions": {
    "$op": "inject",
    "requires": ["advanced_optics"],
    "effects": { "artillery_attack": 0.08 }
  },
  "my_unique_doctrine_tech": {
    "$op": "create",
    "name": "My Unique Doctrine",
    "cost": 140
  }
}
```

### 6.2. Складываемые декларативные патчи `patches/*.json` (3 стадии)

Файлы в папке `patches/` (JSON или YAML) выполняются по **3 стадиям запуска** поверх всех включённых модов:

1. `"stage": "base"` — базовые правки до основной настройки баланса;
2. `"stage": "updates"` — обычные обновления и баланс (значение по умолчанию);
3. `"stage": "final_fixes"` — финальные исправления и патчи совместимости (всегда применяются последними).

Поддерживаемые операции: `set`, `replace`, `add`, `multiply`, `append` (с флагами `unique`/`flatten`), `remove` (удалить поле, элемент массива по `value` или всю запись целиком) и `conditional` (`if_exists`, `then`, `else`). Условие `"if_mod_loaded"` позволяет безопасно включать патч совместимости только при наличии нужного мода (без ошибок, если мод не установлен).

```json
{
  "stage": "final_fixes",
  "category": "common/technologies",
  "if_mod_loaded": ["my_expansion_mod"],
  "operations": [
    { "op": "multiply", "target": "precision_munitions", "path": "cost", "value": 1.2 },
    { "op": "add", "target": "precision_munitions", "path": "effects.artillery_attack", "value": 0.05 },
    { "op": "append", "target": "precision_munitions", "path": "requires", "value": "advanced_optics", "unique": true },
    {
      "op": "conditional",
      "target": "precision_munitions",
      "if_exists": "effects.output",
      "then": [{ "op": "multiply", "path": "effects.output", "value": 1.1 }],
      "else": [{ "op": "set", "path": "effects.output", "value": 0.05 }]
    }
  ]
}
```

При отключении мода `ModJsonPatcher` полностью сбрасывается и возвращает базовую игру 2024 года к исходному `CampaignFingerprint`.

---

## 7. Расширенные триггеры и эффекты (`MOD-02`)

Скриптовый слой `trigger` / `effects` поддерживает прямое взаимодействие с ключевыми механиками кампании:

- **Эпохи и механики**: эффекты `"unlock_feature"`, `"restrict_feature"`, `"disable_feature"` (понимают ID и псевдонимы вроде `"nukes"`, `"missiles"`, `"modern_air_force"`), триггеры `"has_feature"` и `"missing_feature"`.
- **Мегапроекты (`GrandProjects`)**: эффект `"grand_project_progress"` (ускорить/замедлить стройку на N дней), триггеры `"has_grand_project"`, `"completed_grand_project"`, числовые пороги `min_active_grand_projects`, `min_completed_grand_projects`, `min_grand_project.<id>`.
- **Цепочки поставок (`StrategicChokepoints`)**: триггер `"squeezed_chokepoint": "Oil"`, числовые пороги `min_squeezed_chokepoints`, `min_chokepoint_exposures`, `min_healthy_supply_anchors`.
- **Сдерживание и ядерная готовность**: чтение `min_deterrence_tier` / `min_nuclear_tier`, `max_nuclear_posture`, `max_defcon` и эффект `"nuclear_posture"` (для держав с ядерным сдерживанием).
- **Движения, дипломатия и экономика**: `"is_movement": true`, `min_active_movements`, `min_movement.legitimacy`, `min_movement.cadres`, `min_ally_count`, `min_active_wars`, `min_organization_count`, `min_subject_count`, `min_notoriety`, `min_weekly_balance`, а также чтение и изменение стратегических складов через `"stockpile.<good>"` (например, `"stockpile.ammunition": 25`, `"stockpile.oil_mult": 1.15`).
- **Диагностика опечаток**: неизвестный ключ никогда не отбрасывается молча — загрузчик и `ModValidator` сразу подсказывают ближайший правильный ключ (`Did you mean 'stability'?`).

---

## 8. Автодополнение в IDE (JSON Schema) и CLI-валидатор

В репозитории лежат готовые схемы Draft-07 с описаниями полей и сниппетами:
- `docs/modding/schemas/manifest.schema.json`
- `docs/modding/schemas/scenario.schema.json`
- `docs/modding/schemas/patch.schema.json`
- `docs/modding/schemas/event.schema.json`
- `docs/modding/schemas/decision.schema.json`

Скопируйте блок `json.schemas` из `docs/modding/vscode-settings.example.json` в `.vscode/settings.json`, чтобы получить автодополнение и мгновенную подсветку ошибок прямо в редакторе.

Для проверки любого мода одной командой запустите:

```powershell
powershell -ExecutionPolicy Bypass -File Tools\validate-mod.ps1 -ModPath mods\my_mod
```

Для проверки сразу набора модов (с проверкой циклов зависимостей и конфликтов `replace_paths`) и строгим режимом (ошибка даже на предупреждениях):

```powershell
powershell -ExecutionPolicy Bypass -File Tools\validate-mod.ps1 -ModPath mods\mod_a,mods\mod_b -Strict
```

---

## 9. Чек-лист перед публикацией своего сценария

1. Проверить локализацию на обоих языках (`localization/english/` и `localization/russian/`).
2. Проверить мод командой `Tools\validate-mod.ps1 -ModPath mods\<id>` — должно быть `0` ошибок.
3. Убедиться, что кампания стартует и проходит минимум 30–365 дней без падений и `NaN`, а сохранение/загрузка восстанавливает дату, календарь и `CampaignFingerprint`.
4. Проверить переключение «свой сценарий / патчи → базовая 2024» — каталоги должны вернуться к чистым базовым значениям.
