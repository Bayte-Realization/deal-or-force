# План рефакторинга кода — 18 ноября 2026

*Фронт 9 программы (`docs/OVERHAUL_PROGRAMME_2026_11_14_RU.md`). Владелец, 18 ноября: «не забудь про
рефакторинг — код, кажется, сейчас довольно грязный». Цель фронта: «правка в одном месте не требует
чтения пяти тысяч строк». Этот проход только мерил и планировал: код игры не менялся, ничего не
собиралось в проект и не коммитилось.*

## 0. Коротко

Код грязный не везде одинаково, и это хорошая новость.

**Что в порядке.** Имена единообразны (2 725 закрытых полей `_camel` против 25 исключений), копипасты
мало (около 1 % строк лежит в повторах длиннее ста лексем), симуляция ни разу не обращается к Godot —
это держит сама сборка тестов, которая собирает `Scripts/Simulation` без GodotSharp.

**Где грязь на самом деле** — в семи местах, и почти все они видны по одной цифре:

| Место | Цифра | Почему мешает |
|---|---|---|
| `DealOrForceGame` — весь интерфейс одним классом | 52 681 строка в 229 файлах, 1 473 поля, 3 429 членов; `_campaign` трогают 174 файла | любое поле видно из любого экрана; чтобы понять экран, надо знать общий котёл |
| `GlobePresentation` — вся карта одним классом | 15 856 строк в 64 файлах; `GlobePresentation.cs` один — 5 711 строк, из них ~1 220 — шейдер карты внутри строки (`CreateGlobe`, стр. 2240–3517) | шейдер нельзя править в редакторе шейдеров и нельзя читать без C# |
| `CampaignFingerprint.Of` — отпечаток мира | один метод на 1 238 строк, цикломатическая сложность 542 | каждая новая механика дописывает сюда, три ветки делают это прямо сейчас |
| `Campaign.TickStages` — день мира | один метод на 1 243 строки, сложность 209 | порядок дня — договор всей игры, а записан как простыня |
| Точки регистрации тестов и замеров | `Tools/SimBench/Program.cs` — 15 212 строк, его правят **6 из 8** идущих веток; `FormableTests/Program.cs` — 372 флага цепочкой `if` | главный источник конфликтов слияния |
| Состояние в `static` | 953 изменяемых статических члена в симуляции, из них 25 — «кэш для такой-то кампании» (`_xxxCampaign`) и 318 общих буферов | утечки между кампаниями, мешает многопоточности |
| Мёртвый код | 144 кандидата строгой проверки; **59 подтверждены и лежат в файлах, которых не касается ни одна ветка** | шум при чтении; пара находок — недоделанные функции, а не мусор |

Порядок работы (раздел 3): сначала то, что можно сделать сейчас, не задевая ни одной ветки
(мёртвый код, проверка «код не толстеет», вынос консоли, разрезка нетронутых больших файлов); затем,
сразу после волны слияний, — регистрация тестов и замеров, отпечаток по частям, день мира таблицей,
кэши кампании; интерфейс — вместе с новым экраном кампании, а не отдельно от него.

## 1. Как мерили

Инструмент — `tmp_logs/refactor-audit/RefactorAudit` (Roslyn из SDK, без пакетов). Он разбирает тот же
набор файлов, что компилирует игра (`Scripts/**` и `Tools/ExportVerification/**`, 832 файла,
243 361 строка), строит семантическую модель на сборках последней сборки редактора и пишет отчёты в
`tmp_logs/refactor-audit/out/`. **Ошибок компиляции в модели — 0**, то есть каждая ссылка разрешена,
а не угадана по имени. Полный прогон — 3 минуты 20 секунд.

```
dotnet build tmp_logs/refactor-audit/RefactorAudit/RefactorAudit.csproj -c Release --artifacts-path tmp_logs/refactor-audit/artifacts
tmp_logs/refactor-audit/artifacts/bin/RefactorAudit/release/RefactorAudit.exe              # всё, ~3 мин
tmp_logs/refactor-audit/artifacts/bin/RefactorAudit/release/RefactorAudit.exe --verify     # перепроверка мёртвого кода
tmp_logs/refactor-audit/artifacts/bin/RefactorAudit/release/RefactorAudit.exe --partials   # связность больших partial-классов
```

Файлы отчёта: `summary.txt`, `files.csv`, `methods.csv` + `methods_summary.txt`, `types.csv`,
`partials.txt`, `duplicates.txt`, `dead_candidates.csv`, `dead_verify.csv`, `safe_now.txt`,
`statics.csv`, `godot_in_simulation.csv`, `magic_numbers.csv`, `naming.txt`, `reflection_uses.txt`,
`branch_files_by_worktree.txt`. Папка `tmp_logs` игнорируется git и Godot (`.gdignore`).

Сложность — классическая цикломатическая: 1 + каждое `if`, `case`, цикл, `catch`, `?:`, `&&`, `||`, `??`,
ветвь `switch`-выражения. Вложенность — глубина `if`/циклов/`switch`/`try`/`using`/`lock`.

## 2. Измеренное состояние

### 2.1. Размеры по папкам

| Папка | Файлов | Строк |
|---|---:|---:|
| `Scripts/Simulation` | 435 | 149 813 |
| `Scripts/ui` | 229 | 58 044 |
| `Scripts/gfx` | 55 | 16 726 |
| `Scripts` (корень: `DealOrForceGame.cs`, `MainMenuScene.cs`…) | 9 | 4 286 |
| `Scripts/Modding` | 12 | 2 690 |
| `Scripts/Presentation` | 11 | 920 |
| `Scripts/Boot` | 5 | 260 |
| `Tools/ExportVerification` (компилируется в игру) | 76 | 10 622 |
| **Итого в сборке игры** | **832** | **243 361** |

Вне игры, но той же природы: `Tools/SimBench/Program.cs` — **15 212 строк одним файлом** (больше
любого файла игры), все `Tools/FormableTests` — 55 491 строка, `FormableTests/Program.cs` — 859 строк
из одних `if (args.Contains("--…-only"))`.

### 2.2. Сорок самых больших файлов

«Длиннейший метод» и «макс. сложность» — по этому файлу. «Ветки» — какие из идущих сейчас веток
меняют этот файл (раздел 4).

| Файл | Строк | Длиннейший метод | Макс. сложность | Ветки |
|---|---:|---:|---:|---|
| Scripts/gfx/GlobePresentation.cs | 5 711 | 1 277 (`CreateGlobe`, это шейдер) | 77 | longrange, mapart* |
| Scripts/Simulation/SimulationCore.cs | 2 865 | 1 243 (`TickStages`) | 209 | perf3, pockets |
| Scripts/DealOrForceGame.cs | 2 455 | 301 (`_Input`) | 122 | longrange, perf3 |
| Scripts/Simulation/MovementRules.cs | 2 232 | 204 | 62 | — |
| Scripts/Simulation/AiStatecraft.cs | 2 119 | 231 | 60 | perf3, pockets |
| Scripts/Simulation/TerritorialTopology.cs | 1 896 | 280 (`GrowPolar`) | 103 | — |
| Scripts/Simulation/CombatCore.cs | 1 852 | 557 (`ResolveBattles`) | 120 | perf3, pockets |
| Scripts/Simulation/EconomyCore.cs | 1 831 | 232 | 52 | conquest |
| Scripts/ui/DeveloperConsoleCommands.cs | 1 820 | 329 (`EnsureConsoleCommands`) | 17 | — |
| Scripts/Simulation/MilitaryCore.cs | 1 754 | 199 | 67 | pockets |
| Scripts/Simulation/SaveSystem.cs | 1 715 | 135 | 49 | fw-saves |
| Scripts/ui/DiplomacyScreenUI.cs | 1 597 | 290 | 37 | — |
| Scripts/Simulation/NationNaming.cs | 1 509 | 109 | 50 | perf3 |
| Scripts/Simulation/SeaLanes.cs | 1 500 | 244 | 68 | — |
| Scripts/Simulation/Defines.cs | 1 480 | 6 | 2 | pockets |
| Scripts/Simulation/SubjectInteractions.cs | 1 478 | 261 | 63 | conquest, perf3 |
| Scripts/Simulation/Escalation.cs | 1 380 | 74 | 48 | perf3 |
| Scripts/Simulation/WarCore.cs | 1 367 | 160 | 52 | — |
| Scripts/Simulation/SecessionEvents.cs | 1 341 | 224 | 48 | perf3 |
| Scripts/Simulation/CampaignFingerprint.cs | 1 306 | 1 238 (`Of`) | 542 | conquest, longrange, pockets |
| Scripts/ui/SupportScreenUI.cs | 1 300 | 162 | 20 | longrange |
| Scripts/Simulation/TacticalDoctrines.cs | 1 250 | 161 | 70 | perf3 |
| Scripts/Simulation/ProvinceTopology.cs | 1 230 | 130 | 36 | — |
| Tools/ExportVerification/ExportVerification.cs | 1 220 | 623 (`_Ready`) | 201 | — |
| Scripts/Modding/ModLoader.cs | 1 220 | 103 | 28 | — |
| Scripts/Simulation/ScriptedEffects.cs | 1 211 | 136 | 48 | — |
| Scripts/ui/WarLedgerUI.cs | 1 196 | 376 | 35 | — |
| Scripts/Simulation/Rulers.cs | 1 165 | 91 | 233 (`PoolFor`) | perf3 |
| Scripts/Simulation/LandArrangements.cs | 1 163 | 97 | 41 | perf3 |
| Scripts/ui/SubjectsAndReleaseUI.cs | 1 119 | 293 | 43 | — |
| Scripts/Simulation/PeaceShape.cs | 1 079 | 229 | 81 | — |
| Scripts/Simulation/Factions.cs | 1 067 | 134 | 47 | perf3, pockets |
| Scripts/gfx/MilitaryPresentation.cs | 1 057 | 179 | 39 | — |
| Scripts/MainMenuScene.cs | 1 046 | 148 | 18 | — |
| Scripts/Simulation/SupportCore.cs | 1 026 | 72 | 34 | longrange |
| Scripts/Simulation/PeaceConference.cs | 1 020 | 154 | 49 | deals, pockets |
| Scripts/Simulation/NavalCore.cs | 974 | 82 | 18 | perf3 |
| Scripts/Simulation/FrontManager.cs | 969 | 212 | 65 | — |
| Scripts/ui/ArmyOperationsUI.cs | 958 | 132 | 37 | longrange |
| Scripts/ui/MovementUI.cs | 955 | 224 | 60 | — |

\* `work/mapart` ещё не начата (ветка стоит на базе), но её задача может забрать шейдер из
`GlobePresentation.cs`.

### 2.3. Методы

9 042 метода с телом. Длиннее 50 строк — 794, длиннее 100 — 226, длиннее 200 — 42, длиннее 400 — 7.
Сложность выше 20 — 647, выше 40 — 147, выше 80 — 18. Вложенность больше 4 — 122, больше 6 — 7.
Больше семи параметров — 65.

Худшие по сложности (полный список — `methods_summary.txt`):

| Метод | Сложность | Строк | Вложенность |
|---|---:|---:|---:|
| `CampaignFingerprint.Of` (CampaignFingerprint.cs:33) | 542 | 1 238 | 4 |
| `Rulers.PoolFor` (Rulers.cs:905) — огромный `switch` в 82 строки | 233 | 82 | 2 |
| `Campaign.TickStages` (SimulationCore.cs:931) | 209 | 1 243 | 5 |
| `ExportVerification._Ready` (ExportVerification.cs:34) | 201 | 623 | 4 |
| `MovementActions.CheckRefusal` (MovementActions.cs:160) | 136 | 145 | 3 |
| `DealOrForceGame.DrawNamedLandBanner` (NationFlagUI.cs:332), 13 параметров | 135 | 300 | 4 |
| `SituationReport.Read` (SituationReport.cs:29) | 135 | 428 | 4 |
| `DealOrForceGame._Input` (DealOrForceGame.cs:343) | 122 | 301 | 3 |
| `Warfare.ResolveBattles` (CombatCore.cs:1065) | 120 | 557 | **7** |
| `MovementAi.ThinkStages` (MovementAi.cs:122) | 117 | 247 | 5 |
| `Ultimatum.ValidateCore` (UltimatumDemands.cs:197) | 108 | 101 | 2 |
| `TerritorialTopology.GrowPolar` (TerritorialTopology.cs:799) | 103 | 280 | 5 |

Худшие по длине, кроме названных: `BuildFlagEditor` 469, `RefreshWarLedgerDetail` 376,
`BuildProvinceCard` 356, `EnsureConsoleCommands` 329, `RefreshStrategicHud` 306 — все в интерфейсе.

### 2.4. Большие partial-классы и их связность

| Класс | Файлов | Строк | Членов | Используются вне своего файла |
|---|---:|---:|---:|---:|
| `DealOrForceGame` | 229 | 52 681 | 3 429 (1 473 поля) | 1 420 |
| `GlobePresentation` | 64 | 15 856 | 1 068 (528 полей) | 472 |
| `ExportVerification` | 76 | 8 032 | 150 | — |
| `Defines` | 186 | 6 099 | 5 956 | (так задумано: настройки лежат рядом с механикой) |
| `AiStatecraft` | 9 | 4 373 | 155 | 40 |
| `Warfare` | 10 | 3 677 | 200 | 53 |
| `Campaign` | 33 | 2 983 | 227 | 168 |
| `Nation` | 64 | 1 155 | 306 (293 поля) | 277 |

`DealOrForceGame`: самые общие поля — `_campaign` (174 файла), `_hudLayer` (72), `_globe` (69),
`_sidePanel` (36), `_observerMode` (32), `_selectedNation` (31). Только у 24 файлов из 229 все члены
не выходят за файл. Самые спутанные: сам `DealOrForceGame.cs` (берёт 340 чужих членов),
`DiplomacyScreenUI.cs` (97), `TopBarHudUI.cs` (86), `MovementUI.cs` (72), `MapSelectionUI.cs` (69).
Важная деталь: 76 файлов `Tools/ExportVerification` — тоже `partial DealOrForceGame` и
`partial GlobePresentation`, то есть проверка сборки читает закрытые поля интерфейса напрямую.

`AiStatecraft` и `Warfare` разрезаны хорошо: у файлов мало общих членов (медиана 2–3) — это образец,
как надо. `Campaign` и `Nation` — честные «мешки данных», их разрезка по файлам не мешает.

### 2.5. Повторы

Окно в 100 одинаковых лексем (числа и строки приравнены): 124 пары, 2 250 строк — около 1 %.
Это немного. Повторы стоят исправления там, где это одна и та же «форма», а не совпадение:

- **Загрузчик каталога**: «очистить; для каждого документа `common/<папка>` — `Merge`; `Merge(string json)`;
  `Merge(JsonElement)` с `switch` по ключам». Одинаковые блоки в `InflationCore`, `MilitiaCore`,
  `SecessionCore`, `StabilityCore` (по 24 строки), в `FontCatalog`/`MusicCatalog`/`SoundCatalog`
  (по 44 строки), в `PoliticsCore` (дважды в одном файле, 49 строк), `PolicyCatalogs`,
  `NationalCourseCatalog`/`StrategicDoctrines`, `CountryDefinitions`/`StateDefinitions`. Ниже порога
  в 100 лексем таких загрузчиков ещё десятки.
- `Ultimatum.cs:61-92` ≡ `UltimatumDemands.cs:75-121` (проверка требований), `UltimatumInbox.cs:98-110`
  ≡ `UltimatumComposerUI.cs:363-376`.
- `MechanicFeatures.cs:58-78` ≡ `ScriptedEffects.cs:863-901` (таблица «механика → система»).
- `AddressToNation.cs:67-88` ≡ `CovertOperations.cs:171-192`; `SaveSnapshotCloner.cs` (30 строк дважды);
  `PeacefulFormation.cs` (21 строка дважды); `CombatAttrition.cs` (23 строки дважды).

### 2.6. Изменяемое статическое состояние

1 046 членов во всей игре, 953 — в симуляции:

| Вид | В симуляции |
|---|---:|
| каталоги из данных (`ById`, `Definitions`, `Settings`… в классах `*Catalog`/`*Library`) | ~207 |
| `[ThreadStatic]` буферы | 119 |
| общие буферы-контейнеры без `[ThreadStatic]` (`Seen`, `Frontier`, `Scratch`, `Pool`…) | 318 |
| нечитаемые-только скаляры: счётчики, флаги, «день последнего пересчёта» | 255 |
| **ссылка на кампанию, для которой посчитан кэш** | **25** |

Последняя строка — главный риск. Это поля вида `static Campaign _postureCampaign`: кэш помнит, для
какой кампании он посчитан, и держит её в памяти до следующей. Список: `AiStrategyCatalog._postureCampaign`,
`AiTemperament._characterCampaign`, `ArmyDisposition._armiesFor`, `Corridors._poreCampaign`,
`Coups._mapCampaign`, `FormableNations._peopleCampaign`, `FormableProclamationAi._foundationCampaign`,
`FormableRestorations._cachedFor`, `Generals._indexed`, `HeldRegions._for`,
`InternalMigration._centresOf`/`_movedCampaign`, `MarketSystem._talliedCampaign`,
`MovementPower._stateIndexCampaign`, `NavalOperations._indexed`/`_reachesCampaign`,
`PeacefulFormation._territoryCampaign`/`_regionCampaign`, `PeaceShape._nearCacheCampaign`,
`SecessionEvents.RecentWorld`/`_adjCampaign`, `SecularCycle._frontierWorld`,
`Shipbuilding._slipwaysCampaign`, `StrategicExclaves._campaign`, `Waterfront._regionCampaign`.
Поздняя кампания держит 600–800 МБ кучи, поэтому «держит старую кампанию до следующей» — не мелочь.
Общие буферы без `[ThreadStatic]` — это то, обо что споткнётся многопоточность (ветка perf3).

Интерфейс и карта добавляют ещё 93 статических члена — для одного окна это терпимо.

### 2.7. Симуляция и Godot

В `Scripts/Simulation` нет ни одного обращения к типам Godot (0 файлов, 0 обращений). Это держится не
дисциплиной, а сборкой: `FormableTests`, `ArmyInteractionTests` и `SimBench` компилируют
`Scripts/Simulation/**` без GodotSharp. Это правило надо сохранить, оно уже работает как проверка.

### 2.8. «Магические числа»

7 162 числовых литерала в симуляции вне `Defines`, вне `const` и `static readonly`, без 0, 1, 2 и без
констант больше 100 000 (хэши, форматы). Больше всего в `SubjectInteractions.cs` (158), `EconomyCore.cs`
(141), `MilitaryCore.cs` (137), `HouseholdEconomy.cs` (124), `Rulers.cs` (124), `StrategicStockpiles.cs`
(107), `FlagDesign.cs` (106), `BrainDrain.cs` (105). Частые значения: 0.5 (465 раз), 3 (302), 4 (274),
0.01 (202), 0.05 (182), 100 (173), 7 (163), 365 (99), 30 (112).

Переводить всё в `Defines` не нужно: 7 и 30 — это неделя и месяц, 0.5 — середина, 100 — проценты.
Нужно правило для **нового** кода (раздел 6) и точечный перенос там, где число — настройка баланса
(веса решений ИИ в `AiJudgement.cs`, `Escalation.cs`, `OrganizationDynamics.cs`).

### 2.9. Имена

- Закрытые поля: `_camel` — 2 725, `PascalCase` — 25, других стилей нет. Хорошо.
- Методы с маленькой буквы — 17, все в `Scripts/Modding/ModApi.cs` (`get_day`, `set_define`…): это
  нарочно, API для GDScript-модов. Хорошо.
- Пространства имён — четыре: `DealOrForce.Simulation` (434 файла), `DealOrForce` (383),
  `DealOrForce.Modding` (13), `DealOrForce.Presentation` (2). Папка `Scripts/Presentation` (11 файлов)
  почти вся живёт в `DealOrForce`, а не в `DealOrForce.Presentation` — мелочь, но путает.
- **Имя файла не совпадает с главным классом** в 96 файлах без `partial`. Чаще всего так: файл `*Core.cs`,
  а класс `*System` или вовсе другой: `EconomyCore.cs` → `EconomySystem`, `LogisticsCore.cs` →
  `LogisticsSystem`, `MarketCore.cs` → `MarketSystem`, `NavalCore.cs` → `NavalOperations`,
  `CombatCore.cs` → `Warfare`, `PeaceCore.cs` → `PeaceNegotiation`, `SimulationCore.cs` → `Campaign`,
  `ControlTally.cs` → `RegionControl`, `Format.cs` → `Fmt`. Суффиксы файлов: `UI` 170, `Core` 35,
  `Catalog` 19, `Presentation` 9, остальные по одному-два. Переименовывать ради красоты не стоит —
  это конфликты со всеми ветками; правило «новый файл называется по главному классу» — стоит.

### 2.10. Хрупкие места, которые найдены по дороге

- `Scripts/Simulation/JitWarmup.cs` прогревает 21 метод **по строковому имени**
  (`Prepare(typeof(X), "Name")`). Переименование такого метода при разрезке файла молча выключит его
  прогрев — сборка не скажет ни слова. Перед разрезкой симуляции это надо закрыть (раздел 5).
- `Scripts/Simulation/NetworkCommandCodec.cs:22` собирает все `ICampaignCommand` отражением. Поэтому
  команда, которую никто не отдаёт, всё равно «жива» для сети — см. раздел 5, группа Б.
- `Scripts/Simulation/LongCampaign.cs` и `ScenarioInspectorCatalog.cs` обращаются к полям `Nation` по
  имени из данных. Переименование поля `Nation` — изменение формата данных.
- `DlcLoader.RulesFingerprint` («что паки дают вместе, чтобы сетевая игра сравнила») нигде не
  используется: сравнения паков правил перед сетевой игрой, похоже, нет. Это не рефакторинг, а
  возможный рассинхрон — отдельная задача.

## 3. Шаги рефакторинга по порядку выгоды

**Общая проверка каждого шага («тот же мир»).** До и после, в одном рабочем дереве, на одной машине:

```
dotnet build DealOrForce.csproj
dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 1095 1 --fingerprint 1095 > fp_s1_<before|after>.txt
dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 1095 2 --fingerprint 1095 > fp_s2_<before|after>.txt
# все строки "FINGERPRINT day N: …" до и после совпадают побайтно
powershell -ExecutionPolicy Bypass -File Tools\dev.ps1 -Test          # полный FormableTests
dotnet run -c Release --project Tools/ArmyInteractionTests             # полный ArmyInteractionTests
```

Долгие прогоны — скрытым отдельным процессом через WMI с `ShowWindow=0`, как сказано в `AGENTS.md`.
Для шагов про интерфейс и карту к этому добавляется `game_verify` (`EXPORT_VERIFY failures=0`) и снимки
до/после. Для шагов про статическое состояние — ещё `SimBench --isolation-only`,
`--determinism-only` и `FormableTests --parallel-determinism-only`. Шаг, после которого отпечаток
изменился, откатывается целиком, а не «чинится сверху».

Каждый шаг — одна сессия и один коммит (или короткая серия), в своей ветке `work/refactor-<n>`.

| № | Шаг | Когда |
|---|---|---|
| 1 | Регистрация тестов и замеров без правки `Program.cs` | сразу после слияния pockets, conquest, longrange, deals, fw-saves, perf3 |
| 2 | Отпечаток мира по частям | после conquest, longrange, pockets |
| 3 | Мёртвый код, волна 1 (59 членов) | **сейчас** |
| 4 | Проверка «код не толстеет» с базовой линией | **сейчас** |
| 5 | Шейдер карты — в файлы шейдера | первым коммитом mapart или до её начала; после longrange |
| 6 | День мира — таблицей проходов | после pockets и perf3 (согласовать с perf3) |
| 7 | Кэши кампании — из `static` в кампанию | после perf3 |
| 8 | Интерфейс: консоль разработчика из `DealOrForceGame` + образец «экран — свой класс» | консоль — **сейчас**; образец — вместе с новым экраном кампании |
| 9 | Разрезка нетронутых больших файлов симуляции | **сейчас**, по одному файлу за сессию |
| 10 | Общий загрузчик каталогов | **сейчас** для нетронутых каталогов, остальное — после веток |
| 11 | Разрезка `GlobePresentation.cs` | после longrange и mapart |
| 12 | Длинные методы и огромные `switch` | по мере освобождения файлов |
| 13 | Мёртвый код, волна 2 и решения по брошенным функциям | после слияния веток; решения — владельца |

### Шаг 1. Тесты и замеры регистрируются сами

**Что.** `Tools/FormableTests/Program.cs` (859 строк, 372 флага), `Tools/ArmyInteractionTests/Program.cs`
(383 строки) и начало `Tools/SimBench/Program.cs` (разбор флагов на ~300 строк, дальше отчёты) превращаются
в цикл по реестру. Каждый набор тестов объявляет себя рядом с собой: имя флага, входит ли в полный прогон и
его место в порядке (`[Suite("finance", Order = 120)]` или запись в `[ModuleInitializer]`). Каждый отчёт
SimBench (`--conquest`, `--pockets`, `--wars`…) — свой файл `*Report.cs` с той же регистрацией. Тела
отчётов из 15-тысячного `Program.cs` выезжают в свои файлы по одному.

**Зачем.** Сейчас каждая новая механика правит эти три файла, и **шесть из восьми идущих веток правят
`SimBench/Program.cs` одновременно**. После шага новая механика добавляет только свои файлы — конфликтов
слияния в точках регистрации больше нет. Это самая дорогая грязь при параллельной работе агентов.

**Риск.** Низкий для мира (код инструментов), средний для тестов: порядок полного прогона сейчас
записан цепочкой `if` вперемешку с `if (args.Length == 0)`, и если какой-то тест зависит от того, что
перед ним уже прогнали другой, это всплывёт. Проверка: число `PASS` в полном прогоне то же, список
флагов тот же (`--help` печатает реестр, сравнить с нынешним grep по `"--…-only"`), порядок сохранён.

**Когда.** Это единственный шаг, который нельзя делать, пока ветки открыты: его смысл — переписать ровно
те строки, которые они правят. Сделать сразу после волны слияний, в тихое окно, одной сессией.

### Шаг 2. Отпечаток мира по частям

**Что.** `CampaignFingerprint.Of` (1 238 строк, сложность 542) режется на части по системам в **том же
порядке** подмешивания: `FingerprintArmies`, `FingerprintEconomy`, `FingerprintMovements`… Каждая часть —
статический метод рядом с механикой или в `CampaignFingerprint.<Система>.cs`; `Of` становится списком
вызовов.

**Зачем.** Каждая новая механика дописывает сюда своё состояние; сейчас это делают conquest, longrange и
pockets одновременно. После шага новая механика добавляет одну строку в список и свой метод.

**Риск.** Отпечаток — это и предмет правки, и инструмент проверки. Поэтому проверка особая: старая и новая
сборка SimBench печатают **одинаковые** значения на семенах 1 и 2 за 1 095 дней, плюс отпечаток
загруженного старого сохранения тот же (`SimBench --load`, сравнение с выводом старой сборки), плюс
нарочная порча одного поля в каждой части меняет отпечаток (часть не потерялась).

**Когда.** После слияния conquest, longrange и pockets.

### Шаг 3. Мёртвый код, волна 1

**Что.** Удалить 59 членов из раздела 5, группа А: все они в файлах, которых не касается ни одна идущая
ветка, ни один не упомянут в инструментах, модах, данных, сценах, и ни одна ветка не начала ими
пользоваться. Один файл уходит целиком: `Scripts/ui/NavigationCaptionFit.cs`.

**Зачем.** Меньше ложных следов при чтении («где это вызывается?» — нигде). Небольшой, но мгновенный
выигрыш, и он снимает часть шума перед разрезкой файлов.

**Риск.** Низкий: проверено семантической моделью без ошибок и поиском по всем внешним местам
(раздел 5). Остаточный риск — сторонние управляемые моды, собранные против `DealOrForce.dll` и
вызывающие публичный член симуляции; публичного договора на эти члены нет, но удалённые
публичные члены стоит перечислить в журнале изменений. Определения из `Defines`
(`AllianceReaffirmBoost`): проверить, что загрузка старого сохранения и мода с этим ключом не падает
(`FormableTests --reload-only`, `--replay-save` на свежем сохранении).

**Когда.** Сейчас. Перед удалением — повторить `RefactorAudit --verify`: ветки двигаются.

### Шаг 4. Проверка «код не толстеет»

**Что.** Перенести инструмент из `tmp_logs/refactor-audit` в `Tools/CodeAudit` (новая папка с `.gdignore`)
и добавить режим `--ratchet`: базовая линия `Tools/CodeAudit/baseline.json` хранит по каждому файлу
строки, длиннейший метод, сложность, число изменяемых статических членов, число литералов, и общее
число мёртвых кандидатов. Проверка падает, если что-то из этого **выросло** по сравнению с базой (уменьшаться
можно всегда, база обновляется ключом `--accept`). Включить в `Tools/dev.ps1 -Doctor` и в описание
слияния ветки. Быструю текстовую часть (размер файла, новые `partial class DealOrForceGame`, `static` без
`readonly` в `Scripts/Simulation`) — после шага 1 добавить набором `--code-health-only` в FormableTests.

**Зачем.** Без этого через месяц будет новый отчёт с новыми цифрами. Правило «не расти» дешевле
правила «уменьшить» и не требует ни от кого чистить чужое.

**Риск.** Никакого для игры: только новые файлы. **Когда.** Сейчас.

### Шаг 5. Шейдер карты — в файлы шейдера

**Что.** Строка из `CreateGlobe` (`GlobePresentation.cs`, ~1 220 строк GLSL с двумя вклейками
`OccupationShaderFunctions` и `SeamSamplingFunctions()`) уходит в `Scripts/gfx/shaders/globe_map.gdshader`,
вклейки — в `globe_occupation.gdshaderinc` и `globe_seam.gdshaderinc` (`#include`). C# загружает шейдер
ресурсом и, как сейчас, прогоняет код через `MapColorSpaceShader.Build` и, для gl_compatibility,
`PackedMapShader.Build`. `CompatibilityMapShader.Source` — по той же схеме, вторым коммитом.

**Зачем.** Это прямое условие фронта 9. Шейдер начинают править в редакторе шейдеров с подсветкой и
ошибками по строкам; `GlobePresentation.cs` худеет на пятую часть.

**Риск.** Средний: преобразования `PackedMapShader`/`MapColorSpaceShader` работают поиском по тексту
шейдера, и их тесты в FormableTests собирают эти файлы — проверить, что тесты читают тот же текст.
`#include` в экспорте должен попасть в пак (проверка `EXPORT_VERIFY`, `MapRenderHealthVerification`). `Shader.Code` отдаёт текст с невскрытыми `#include`: если
преобразование ищет строку, которая уехала во вставку, оно молча промахнётся — поэтому вставки
выносить только те, которых преобразования не касаются, или вскрывать их в C# перед `Build`.
Проверка: снимки карты до/после на двух рендерерах (Forward+ и Compatibility) совпадают попиксельно,
`game_verify` чистый, отпечаток мира тот же (он не может измениться, но правило одно для всех).

**Когда.** Ветка `work/mapart` ещё не начала работу, а её задача — та же карта. Лучше всего — первым
коммитом mapart или отдельным шагом до её начала. В любом случае после слияния longrange (она правит
`GlobePresentation.cs`, хотя и в другом месте — около строки 3 756).

### Шаг 6. День мира — таблицей

**Что.** `Campaign.TickStages` (1 243 строки, сложность 209) становится коротким исполнителем
упорядоченного списка проходов: «на каждый шаг» (марш, снабжение, бои…) и «на каждый целый день» (в
порядке из скилла `deal-or-force-architecture`). Каждый проход — именованный метод в файле своей
системы; швы `yield return 0`, по которым день делится между кадрами, сохраняются там же, где сейчас.
Комментарии о том, почему порядок такой (`SecessionEvents` после истории, `CapitalCore` последним),
остаются у соответствующих строк таблицы.

**Зачем.** Порядок дня — главный договор игры. Сейчас, чтобы вставить новую систему, надо читать
простыню и понимать, где кончается «на шаг» и начинается «на день». После шага — одна строка в таблице
с соседями по смыслу. И профилировщик получает имена проходов бесплатно.

**Риск.** Высокий, потому что любой сдвиг порядка меняет мир. Проверка — отпечаток на двух семенах за
три года, плюс `--determinism-only`, плюс `--parallel-determinism-only`, плюс полный FormableTests.
`JitWarmup` прогревает часть проходов по имени — сначала быстрая победа 2 из раздела 4.

**Когда.** После слияния pockets и perf3. perf3 (многопоточность) почти наверняка трогает именно этот
метод — лучше, чтобы этот шаг сделала сама perf3 или чтобы он шёл сразу за ней.

### Шаг 7. Кэши кампании — из `static` в кампанию

**Что.** 25 статических кэшей «для такой-то кампании» (список в 2.6) переезжают в объект кэшей,
который принадлежит `Campaign` (`campaign.Caches`), и умирают вместе с ней. Общие буферы без
`[ThreadStatic]` (318) — по мере того, как perf3 решит, какие проходы идут параллельно: либо
`[ThreadStatic]`, либо буфер прохода.

**Зачем.** Две кампании в одном процессе (загрузка, сетевой клиент, SimBench на двух мирах) не делят
состояние; старая кампания не держит сотни мегабайт после выхода в меню; параллельные проходы не
пишут в общий список.

**Риск.** Средний. Проверка: отпечаток, `SimBench --isolation-only`, `--determinism-only`,
`FormableTests --parallel-determinism-only`, `--reload-only`, и замер памяти после второй загрузки
(`MemoryReport`).

**Когда.** После perf3 — это её территория.

### Шаг 8. Интерфейс: консоль отдельно и образец «экран — свой класс»

**Что, часть 1 (сейчас).** `DeveloperConsoleCommands.cs` (1 820 строк, `EnsureConsoleCommands` на 329)
и `DeveloperConsoleUI.cs` выезжают из `partial DealOrForceGame` в собственные классы: таблица команд по
темам (`ConsoleWorldCommands`, `ConsoleDiplomacyCommands`, `ConsoleDebugCommands`…), каждая получает
узкий контекст — кампанию, карту, «перерисовать», «сыграть звук». Ни одна ветка эти файлы не трогает,
новый экран кампании консоль не заменяет.

**Что, часть 2 (вместе с новым экраном).** Ветка newscreen по своей спецификации строит новый экран
кампании **рядом со старым** и удаляет старый после одобрения. Это и есть главный рефакторинг
интерфейса: новый экран пишется не как ещё 30 файлов `partial DealOrForceGame`, а как отдельные классы
с тем же узким контекстом, что получила консоль (часть 1 — его обкатка). Удаление старой оболочки после
одобрения будет самой большой разовой уборкой мёртвого кода в проекте.

**Зачем.** 1 473 поля одного класса — это «каждый экран видит всё». Чинить старые экраны, которые скоро
заменят, — пустая работа; правильно — не дать новому экрану повторить старую форму.

**Риск.** Часть 1 — средний: консоль меняет мир командами, тестов на неё мало. Проверка: сборка,
`game_verify`, ручной прогон десятка команд в консоли, отпечаток (команды не вызываются — должен совпасть).

### Шаг 9. Разрезка нетронутых больших файлов симуляции

**Что.** По одному файлу за сессию, по смыслу, без изменения поведения (перенос методов, а не правка):
- `MovementRules.cs` (2 232): кэш людей движений (`MovementPeopleCache` и `EnsurePeopleIndexedStages`)
  → `MovementPeopleIndex.cs`; недельный проход (`WeekStages`, 204 строки) → `MovementWeek.cs`.
- `TerritorialTopology.cs` (1 896): загрузка переопределений (`LoadOverrides`) → `TerritorialOverrides.cs`;
  рост областей (`GrowPolar` и соседи) → `TerritorialGrowth.cs`; геометрия барьеров
  (`ClearSharedBarrier`, `BridgeRegionComponents`, `Connect`, `ThroughDistance`) → `TerritorialBarriers.cs`.
- `SeaLanes.cs` (1 500): построение сетки (`Build`, 244 строки, вложенность 7) отдельно от запросов
  (`Route`, `FindHarbours`).
- Затем `WarCore.cs`, `ProvinceTopology.cs`, `PeaceShape.cs`, `FrontManager.cs`, `ScriptedEffects.cs`.

**Зачем.** «Правка в одном месте не требует чтения пяти тысяч строк» — буквально это.

**Риск.** Низкий, если только переносить. Проверка: отпечаток, полный набор. Перед каждой сессией —
снова список файлов веток (раздел 4): perf3 расширяется на глазах (за время этого прохода — с 3 файлов до 73).

### Шаг 10. Общий загрузчик каталогов

**Что.** Один помощник: «прочитать все документы `common/<папка>` (и пакеты модов) по порядку имён,
каждому — `Merge`, ошибки — в список, после — `Validate`». Каталоги переходят на него по одному.
Сейчас — нетронутые ветками: `FontCatalog`, `MusicCatalog`, `SoundCatalog` (одинаковые 44 строки),
`PoliticsCore` (две копии в одном файле), `InflationCore`, `SecessionCore`. `MilitiaCore`,
`StabilityCore`, `PolicyCatalogs` — после pockets, conquest и deals.

**Зачем.** Правило «последний файл побеждает, мод пишет только то, с чем не согласен» живёт в одном
месте; новый каталог — десять строк вместо пятидесяти; у модов одно поведение везде.

**Риск.** Низкий. Проверка: отпечаток (данные те же — мир тот же), `SimBench --release-content-only`,
сравнение списка ошибок загрузки до/после.

### Шаг 11. Разрезка `GlobePresentation.cs`

После шага 5 в главном файле карты останется ~4 500 строк: камера и ввод (`_GuiInput`, `_Process` —
сложность 77), подписи стран (`BuildCountryLabels`, 207 строк), значки флотов
(`CreateFleetMarkerTexture`, вложенность 6), состояние плоской карты (`_flatMap` трогают 37 файлов).
Разрезать по этим четырём смыслам. **Когда:** после longrange и mapart.

### Шаг 12. Длинные методы и огромные `switch`

По мере освобождения файлов: `Rulers.PoolFor` (сложность 233 в 82 строках — `switch` просится в таблицу
данных), `Warfare.ResolveBattles` (557 строк, вложенность 7), `SituationReport.Read` (428),
`MovementActions.CheckRefusal` (136), `Ultimatum.ValidateCore` (108), `PeaceShape.Write` (вложенность 7),
`ExportVerification._Ready` (623 строки — тоже реестр, как шаг 1). Каждый — со своим тестовым набором
и отпечатком.

### Шаг 13. Мёртвый код, волна 2, и брошенные функции

После слияния веток — группа В из раздела 5, перепроверенная заново. Группа Б требует решения владельца
или хозяина механики: не каждая неиспользуемая вещь — мусор, часть из них — недоделанная функция.

## 4. Идущие ветки и что они трогают

Снимок 17 ноября, около 13:30 (`git diff <merge-base>` + незакоммиченное + новые файлы; полный список —
`tmp_logs/refactor-audit/out/branch_files_by_worktree.txt`).

| Ветка | Рабочее дерево | Что | Файлы в Scripts/Tools/данных |
|---|---|---|---|
| `work/pockets-supply` | `dof-pockets` | котлы, снабжение, партизаны | 40: `Attrition`, `LogisticsCore`, `MilitiaCore`, `Partisans` (новый), `StrandedGarrisons`, `CombatCore`, `CombatTerritory`, `MilitaryCore`, `Factions`, `AiStatecraft`, `CapitalCore`, `PeaceCore`, `PeaceConference`, `WarCoalition`, `WarWeariness`, `MovementFall`, `Desertion`, `EquipmentLogistics`, `MilitaryAccess`, `PocketResistance`, `SimulationCore`, `Defines`, `CampaignFingerprint`, `ui/ArmyPanelUI`, тесты, `SimBench/Program.cs` |
| `work/conquest-aftermath` | `dof-conquest` | что бывает после завоевания | 34: `ConquestAftermath`, `RegionalAutonomy` (новые), `CampaignFingerprint`, `CultureUnion`, `Deals`, `DomesticCrises`, `EconomyCore`, `FormableNations`, `HomelandIntegration`, `MechanicFeatures`, `MoneyLedger(View)`, `PeacefulFormation`, `RadicalismCore`, `StabilityCore`, `StabilityOutlook`, `StatePartition`, `SubjectInteractions`, `UnionCohesion`, ui: `MapSelectionUI`, `ProvinceContextMenuUI`, `RadicalismUI`, `CountryStabilityOutlookUI`, `FormableTests/Program.cs`, `SimBench/Program.cs` |
| `work/longrange` | `dof-longrange` | силы дальнего действия | 28: `LongRangeMovement`, `MapSelection` (новые), `LongRangeStrikes`, `SupportCore`, `SupportRepatriation`, `TerrainDestruction`, `CampaignFingerprint`, `DealOrForceGame.cs`, `gfx/GlobePresentation`, `GlobeSupportLayout`, `GlobeSupportPresentation`, ui: `ArmyOperationsUI`, `FleetPanelUI`, `RegionBuildListUI`, `SupportScreenUI`, `ArmyInteractionTests/Program.cs`, `SimBench/Program.cs` |
| `work/deals` | `dof-deals` | торговые соглашения | 22: `AgreementsCore`, `AiDiplomacy`, `Consolidation`, `DealNegotiations`, `DealTally`, `Deals`, `MarketCore`, `PeaceConference`, `PeaceCore`, `PolicyCatalogs`, `SaveRows`, `TradeCore`, `TradeTerms(Value)`, `UltimatumDemands`, `SimBench/Program.cs` |
| `work/fw-saves` | `dof-fw-saves` | формат сохранений | 15: `SaveSystem`, `SaveContainer`, `SaveHistoryTable` (новые), `Tools/SaveFormatBench/**`, `SimBench/Program.cs` |
| `work/perf3` | `dof-perf3` | производительность, многопоточность | 73 и растёт: `SimulationCore`, `SimulationFrameClock` (новый), `DealOrForceGame.cs`, `AiStatecraft`, `ArmyDisposition`, `CombatCore`, `Corridors`, `Coups`, `Escalation`, `Factions`, `MilitiaCore`, `MovementAi`, `NationNaming`, `NavalCore`, `Rulers`, `SecessionEvents`, `StrategicStockpiles`, `SubjectInteractions`, `TacticalDoctrines`, `WorldWar`… (полный список в файле), ui: `DiagnosticsUI`, `LoadingScreenUI`, `SettingsApply`; `SimBench/Program.cs` |
| `work/mapart` | `dof-mapart` | художественная карта | пока ничего (ветка на базе); по задаче — `GlobePresentation.cs` и шейдер карты |
| `work/newscreen` | `dof-newscreen` | новый экран кампании | пока только `docs/design/interface-2026-11-17/**`; потом — весь интерфейс кампании |

Слиты и чисты: `work/three-bugs`, `work/fw-data`, `work/fw-profiling`. В основном дереве владельца
незакоммичены правки версии 0.7.2.3 и языковых адаптеров (`DealOrForce.csproj`, `project.godot`,
`Scripts/Modding/EngineScriptMod.cs`, `Scripts/Simulation/GameBuildInfo.cs`, `Tools/LanguageAdapters/**`,
`Tools/ManagedModTests/**`) — их тоже не трогать.

**Горячие точки — файлы, которые правят две ветки и больше:** `Tools/SimBench/Program.cs` (6 веток),
`Tools/FormableTests/Program.cs` (3), `Scripts/Simulation/CampaignFingerprint.cs` (3),
`Tools/ArmyInteractionTests/Program.cs` (2), `DealOrForceGame.cs`, `GlobePresentation.cs`,
`SimulationCore.cs`, `AiStatecraft.cs`, `CombatCore.cs`, `Factions.cs`, `MilitiaCore.cs`,
`CapitalCore.cs`, `PeaceCore.cs`, `PeaceConference.cs`, `Deals.cs`, `DealNegotiations.cs`,
`Consolidation.cs`, `CultureUnion.cs`, `DomesticCrises.cs`, `HomelandIntegration.cs`,
`SubjectInteractions.cs`, `UnionCohesion.cs`, `WarWeariness.cs`, `ui/SettingsApply.cs` (по 2).
Первые три строки этого списка — ровно шаги 1 и 2: их задача — чтобы следующие ветки в эти файлы не
писали вовсе.

## 5. Мёртвый код: что найдено и как проверено

**Как.** (1) Семантическая модель Roslyn по всей сборке игры, 0 ошибок компиляции. Ссылкой считается
любое имя, создание объекта, вызов конструктора базы, индексатор, перечислитель `foreach`, оператор,
обе половины `partial`-метода, расширение. Ссылки изнутри самого объявления (рекурсия) не считаются.
Не считаются кандидатами: `override`, реализации интерфейсов, виртуальные методы Godot (`_Ready`,
`_Process`…), `Main`, операторы, члены перечислений. (2) Имя каждого кандидата целым словом ищется:
в строковых литералах кода игры (вызовы по имени: `Call`, `Connect`, `HasMethod`, `JitWarmup`), во всех
`Tools/**` (`.cs`, `.gd`, `.ps1`, `.mjs`, `.py`), в `mods/**`, `addons/**`, `LanguageKit/**`, в данных
`game/**` (ещё и с переводом `snake_case` → `PascalCase`), в сценах `.tscn`/`.tres`/`.gd`, в `docs/**`, и
**в изменённых и новых файлах восьми веток**. (3) Сомнительные прочитаны глазами.

**Итог.** 631 неиспользуемых по модели: 323 упомянуты в данных или строках (почти все — настройки
`Defines`, которые задаются JSON), 164 используются только тестами и инструментами (это опоры тестов,
не мусор), 144 — нигде. Из 144:

**Ложные находки, которые проверка поймала** (поэтому нужен ручной шаг):
- `AssaultRulesFSharpBoot`, `PeaceRulesFSharpBoot`, `WarScoreRulesFSharpBoot` — классы с
  `[ModuleInitializer]`, их вызывает среда .NET при загрузке сборки. Живые.
- `DealOrForceGame.MemoryReport`, `MainMenuScene.MemoryReport`, `GlobePresentation.PreviewNuclearBlast` —
  крючки для инструментов, которые управляют игрой через `runtime exec` (Godot MCP Pro): их вызывают
  строкой GDScript, которой нет в репозитории. Живые; стоит пометить атрибутом `[ToolHook]`, чтобы
  проверка их пропускала.
- `ProvinceOwnership.ProvinceSetOf`, `ProvinceTopology.FindRouteToAny` — мертвы в `EntertAlpha`, но **perf3
  и pockets только что начали ими пользоваться** (`dof-perf3/Scripts/Simulation/Corridors.cs:487`,
  `dof-pockets/Scripts/Simulation/MilitaryCore.cs:1375`). Удалить сейчас — сломать две ветки. Поэтому
  поиск по веткам обязателен перед каждой волной.

**Группа А — удалить сейчас (59 членов, файлы не трогает ни одна ветка):**

- `Scripts/gfx/BattleMapPresentation.cs`: `BattleMarkerScreen`; `FlatCounterPresentation.cs`:
  `FindFreeCounterOffset`; `MilitaryPresentation.cs`: `StrokeBox`, `DrawCross`, `DrawEllipse`, `DrawArc`,
  `FillEllipse`, `SideOrganisation` (вызовы `DrawArc` в коде — это `CanvasItem.DrawArc` Godot, а совпадения
  в `Tools/IconCompiler/*.ps1` — свои функции PowerShell).
- `Scripts/Modding/ModLoader.cs`: `TopologicalOrder` (закрытый).
- `Scripts/Simulation/AllianceMaintenance.cs`: `CanReaffirm`, настройка `Defines.AllianceReaffirmBoost`
  (ни в данных, ни в коде; см. риск шага 3);
  `AllyRivalShift.cs`: `ShiftBoard.Protects`; `ArmyHomecoming.cs`: `OrderReturns`; `BrainDrain.cs`:
  `ReceptionState`; `CampaignIntel.cs`: `EstimatedNavy`; `ProvinceVision.cs`: `CampaignIntel.PlayerCanSeeProvince`;
  `CombatAttrition.cs`: `Warfare.Weighted`; `CombatOperationalRouting.cs`: `HeldControllersChanged`,
  `ReuseHeldRoute`; `ControlTally.cs`: `RegionControl.BorderedShare`; `CrossoverWorks.cs`:
  `Campaign.CrossoverNearbyBlocks`; `DefconSystem.cs`: `LevelName`; `Delegation.cs`: `DeskReason`;
  `DemographyCatalog.cs`: `ReligionDefinition.FamilyId`, `CountryCount`, `RegionOverrideCount`,
  `DenominationCount`, `LoadFaithSettings`, `DenominationSize`; `DiplomaticGestures.cs`: `AiTick`;
  `EconomicExposure.cs`: `PickHedgeCandidate`; `EventCore.cs`: `CampaignEventOption.ConsequenceText`;
  `FlagCatalog.cs`: `IdentitySources`; `MovementRules.cs`: `CultureTotalsOf` и семь никогда не
  заполняемых полей `MovementPeopleCache` (`ByReligion`, `RegionCultures`, `RegionRelCults`, `NewCultStamp`,
  `PendingStateCultIds`, `PendingStateRelIds`, `PendingStateSigs`); `NumericIntegrity.cs`: `ReadersFor`;
  `ProvinceTopology.cs`: `TargetProvinceCount`; `ScenarioMapDraft.cs`: `StrokeCount`; `SeaLanes.cs`:
  `CellAt`, `WaterPointNear`; `SourceMapGrid.cs`: `LoadOptional`; `WeeklyDiplomacyScan.cs`:
  `AiStatecraft.UnchangedWeeklyPair`.
- `Scripts/ui`: `BattleDossierUI.cs`: тип `BattleLabelStyle`; `EconomyUI.cs`: `GroupSize`;
  `MaterialStyles.cs`: `IconTone`; `NavigationCaptionFit.cs`: `FitNavigationCaption` — **весь файл**;
  `ObserverModeUI.cs`: `HideForObserver`; `ResearchUI.cs`: `EffectCaption`; `ScenarioWorkbenchUI.cs`:
  `AuthorWindowOpen`; `ScreenChromeUI.cs`: `PaintRow`; `StrategicHudUI.cs`: `AddOutlinerHint`;
  `WarPeaceUI.cs`: перегрузка `ProposePeace(PeaceTerms)` (живая — `ProposePeace(StrategicWar, PeaceTerms)`)
  и `PeaceHistory`.

Все удаляемые свойства — вычисляемые (`=>`), ни одно не пишется в сохранение.

**Группа Б — решить, а не удалить** (неиспользуемое, но похоже на недоделанную функцию):
- Команды, которые никто не отдаёт: `SetWelfareSpendingCommand`, `SetEducationSpendingCommand` (консоль
  меняет то же самое напрямую, мимо очереди команд), `RequestWarInterventionCommand`,
  `RespondWarInterventionCommand` (игрок не может попросить военного вмешательства — так задумано или
  нет кнопки?); в ветках: `CultureUnionCommand`, `NameConventionCommand`, `RebaseSupportCommand`.
- `DealOrForceGame.ActionCard` (`ActionCardUI.cs`) — «одна карточка, на которой стоит любое действие
  игры», нигде не используется. Новому экрану кампании она может пригодиться.
- `DlcLoader.RulesFingerprint` (сравнение паков перед сетевой игрой не подключено — см. 2.10),
  `DlcEntitlements.UnlockEverything`, `DlcLoader.OwnedCount`.
- `SaveTextLayout` (175 строк, весь файл) — раскладка текстового сохранения; решает ветка fw-saves.
- `ScriptLibrary.HasTrigger/HasEffect/HasScore/TriggerNames/ScoreNames` — похоже на API для модов.
- `LuauFormulaEvaluator.ClearCache` — кэш формул никто не чистит при перезагрузке модов; ключ — текст
  формулы, так что вреда нет, но решить: вызывать или удалить.
- `GlobePresentation.StraitCrossingLegend`/`StraitCrossingTooltip`, `LoadRelief`, `LoadWaterTile` — к ветке mapart.
- `Defines.RestorationConsentOpinion` — оставлен нарочно («для совместимости модов и сохранений»), не трогать.

**Группа В — после слияния веток (файл сейчас правит ветка):** `Attrition` (`StarveThePockets`,
`BuryTheBroken`), `Consolidation` (`ShareBorder`, `ShardConsolidationReviewDays`), `Deals.DealValue`,
`Defines.OceanTexture` и шесть `Defines.Advisor*` (локальный советник-LLM, кода которого больше нет),
`EconomySystem.WeeklyMilitaryAt`, `DivisionRules.PersonnelText`, `MilitaryBootstrap.MaxPerCountry`,
`MilitiaCore.Threat`/`CollectEnemies`, `NationNaming.MajorityCountry`/`IsAdjectiveBase`,
`ProxyWars.IsConfrontation`, `Rulers.PrimaryLeader` и настройка `RulerElectionChangeFloorStability`,
`SaveSystem.ReadCompressed`/`ReadBrotli`, `SecessionEvents.BuildAdjacency`, `StabilityCore.RegionStability`,
`StrategicDoctrines.InForce`, `StrategicStockpiles.FillRatio` и восемь настроек запасов/мобилизации,
`SubjectInteractions.TickAiForeignAgitation`, `SupportCore.HomeStateOf`, `WarWeariness.PeaceForcedEndLead`,
`WorldWar.Fear`, `GlobePresentation.FreeCameraSpeed`, `GlobeSupportPresentation.SelectedSupportId(s)`/`FocusSupportFormation`,
`PocketBreakout.SearchPocketed`/`SearchArmies`/`Scratch.LocalHostile` (территория pockets),
`DeterministicParallel.SerialRuns` (территория perf3). Перед удалением — заново `--verify`.

## 6. Быстрые победы — можно сейчас, ни одна ветка не задета

1. **Мёртвый код, группа А** (шаг 3): 59 членов в 38 файлах, один файл целиком. Одна сессия.
2. **`JitWarmup` без строк.** 21 вызов `Prepare(typeof(X), "Name")` в `Scripts/Simulation/JitWarmup.cs`
   (файл не трогает ни одна ветка) — на `nameof(X.Name)` там, где метод доступен; где закрыт —
   проверка в `Tools/CodeAudit`, что каждое имя находится. Тогда переименование при разрезке файлов
   ломает сборку, а не прогрев. Делать **до** шагов 6 и 9.
3. **Инструмент в репозиторий** (шаг 4): `Tools/CodeAudit` с базовой линией и `--ratchet`. Только новые файлы.
4. **Консоль разработчика из `DealOrForceGame`** (шаг 8, часть 1): `DeveloperConsoleCommands.cs` +
   `DeveloperConsoleUI.cs`, ни одна ветка их не трогает.
5. **Загрузчик каталогов** (шаг 10) для `FontCatalog`, `MusicCatalog`, `SoundCatalog`, `PoliticsCore`,
   `InflationCore`, `SecessionCore`.
6. **Разрезка `MovementRules.cs`, `TerritorialTopology.cs`, `SeaLanes.cs`** (шаг 9) — по одному за сессию,
   после пункта 2.
7. **Пустая папка `Scripts/LegacySourceMap`** — в git её нет, локальный остаток; удалить.
8. **Атрибут `[ToolHook]`** для `MemoryReport` (×2), `PreviewNuclearBlast` и подобных методов,
   которые зовут инструменты через `runtime exec`, — чтобы они не всплывали в следующем отчёте о
   мёртвом коде и чтобы было видно, что их нельзя переименовать.

Перед каждой из них — заново снять список файлов веток (раздел 4): за время этого прохода perf3 выросла с 3 до 73 файлов.

## 7. Правила, чтобы не загрязнить снова

1. **Бюджет файла.** Новый файл — до 800 строк; 1 000 — предупреждение. Файл, который уже больше,
   не может расти (базовая линия `Tools/CodeAudit`). Новый метод — до 150 строк и сложности 40;
   существующие — не расти.
2. **Новый экран — свой класс.** Никаких новых файлов `partial class DealOrForceGame` и
   `partial class GlobePresentation`. Экран получает узкий контекст (кампания, карта, «перерисовать»,
   «звук»), а не всё. Новый слой карты — отдельный узел со своим классом.
3. **Состояние кампании живёт в кампании.** В `Scripts/Simulation` новый изменяемый `static` — только
   каталог из данных или буфер с `[ThreadStatic]` и комментарием «почему». Кэш, который знает, для какой
   он кампании, — в `campaign.Caches`, не в статическом поле.
4. **Число баланса — в `Defines`.** Вес, порог, срок, который захочется подкрутить, — в `partial Defines`
   рядом с механикой и в `game/common/defines/*.json`. Число литералов в файле симуляции не растёт.
5. **Симуляция не знает Godot** — уже держится сборкой тестов без GodotSharp; так и оставить.
6. **Регистрация без общих файлов** (после шага 1–2). Новый тестовый набор, новый отчёт SimBench, новая
   часть отпечатка, новый проход дня — новый файл с регистрацией, а не правка `Program.cs`,
   `CampaignFingerprint.Of` или `TickStages`.
7. **Ничего по строковому имени**, если можно `nameof`: прогрев, `Call("…")`, команды консоли, поля
   `Nation` из данных — либо `nameof`, либо проверка, что имя существует.
8. **Новый файл называется по главному классу.** Не `XxxCore.cs` с классом `XxxSystem` внутри.
9. **Мёртвого кода не больше, чем было.** `Tools/CodeAudit --ratchet` считает кандидатов «нигде»;
   число не растёт. Неиспользуемая команда или кнопка — это вопрос «не забыли ли подключить», его
   задают в описании слияния.
10. **Где проверять.** `Tools/CodeAudit --ratchet` — перед слиянием каждой ветки (полная модель, ~3 мин,
    вне тестов, потому что ей нужна сборка редактора). Быстрая текстовая часть — набором
    `FormableTests --code-health-only` после шага 1, чтобы падать в обычном полном прогоне.
11. **Каждый шаг рефакторинга — тот же мир**: отпечаток на семенах 1 и 2 за 1 095 дней совпадает,
    полные FormableTests и ArmyInteractionTests зелёные. Шаг, который это нарушил, откатывается.
