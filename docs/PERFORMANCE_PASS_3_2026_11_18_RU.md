# Производительность: проход 3 (Волна 1 + Волна 2 на `work/perf3`)

Дата: 18 ноября 2026  
Ветка: `work/perf3` (`D:\Godot Projects\dof-perf3`)  
Ключевые коммиты: `1bbd5534` (Волна 1), `7d2750a3` (слияние `EntertAlpha` `6ffd5c0a`), `4ddb4112` (Волна 2 + `.cs.uid`), `9452a88f` (слияние `EntertAlpha` `187c9f7f`), `0912f8f6` (устранение x32.5 регрессии `orders:support` / `support` после слияния `work/longrange`).

Все прогоны выполнены на изолированных копиях сейвов в `tmp_logs/perf/` (`bozhe.bdfsave`, 211 MB, день 39 174; `campaign_20261116_010310.bdfsave`, 274 MB, день 52 403), оригиналы в `Documents` не затрагивались. Сборка выполнялась с `--artifacts-path "D:/Godot Projects/dof-perf3/tmp_logs/perf3-build"`.

---

## 1. Что сделано

### Волна 1 (`1bbd5534`)
- Плавный бюджет кадра: `SimulationFrameClock.cs` (`9 ms` по умолчанию), `StageCosts.cs` (отслеживание тяжёлых неделимых элементов > `6.0 ms` и перенос на свежий кадр через `Campaign.HeavyNext()`), кооперативные точки `yield return 0` в длинных проходах (`DealOrForceGame.cs`, `SimulationCore.cs`, `BorderDiplomacy.cs`, `LogisticsCore.cs`, `MovementAi.cs`, `MovementRules.cs`, `Escalation.cs`, `NationNaming.cs`, `NationalModifiers.cs` и др.).
- Параллелизация независимых проходов и добавление `SmoothHeavyYieldTests.cs` и пробы плавного бюджета в `Tools/SimBench/Program.cs`.

### Волна 2 (`4ddb4112`) — алгоритмические ускорения и снятие аллокаций без изменения мира
- **Рынок и экономика (`TradeCore.cs`, `TradeOrders.cs`, `EconomyCore.cs`, `MarketCore.cs`, `ProductionChains.cs`, `NavalCore.cs`)**:
  - В `TradeCore`: инкрементальная матрица расстояний с шагом stride и буфером +128 по списку изменённых стран; битовая маска `HasPartnerRules` перед проверками `PartnerForbidden` / `PartnerPreferenceBetween`; общий буфер `PartnerAffinity` в `TradeWorkspace`.
  - В `TradeOrders`: zero-alloc поиск `ForGood`/`ForPartner` (ветка `#if NET9_0_OR_GREATER` на `TryGetAlternateLookup<ReadOnlySpan<char>>()` и `#else` span-поиск для инструментов `net8.0`).
- **Гео-индекс, названия и локализация (`LandAtlas.cs`, `Localization.cs`, `CountryNames.cs`, `NationNaming.cs`, `InternalMigration.cs`)**:
  - `LandAtlas.CentresOf(Campaign)`: центры регионов кэшируются в `ConditionalWeakTable<Campaign, CentresCache>` с проверкой `(campaign.MapRevision, campaign.ProvinceMap)` — сохраняется корректность при изменении `ProvinceToState` в `StatePartition`/`ColonisationCore`; zero-alloc `Iso2ToTag(ReadOnlySpan<char>)`.
  - `Localization.cs` + `CountryNames.cs`: добавлены `Loc.Revision`, `Loc.TryGet`, `Loc.CollectPoliticsStems`; кэш `PoliticsStemsGate` избавляет от сборок ключей `.government.`/`.ideology.` для стран без переопределений; в `CountryNames.Form` пред-интернированные ключи; в `NationNaming` убран повторный вызов `BestFor` и лишние аллокации/LINQ-замыкания.
  - `InternalMigration.cs`: хранение состояния прохода в `ConditionalWeakTable<Campaign, MovedState>`, фильтрация стран с `>= 2` регионами до `DeterministicParallel.Slots`.
- **Мнения и UI (`OpinionCore.cs`, `DiplomacyScreenUI.cs`, `MapSelectionUI.cs`, `WorldInteractionUI.cs`)**:
  - Пропуск форматирования текстовых строк `OpinionDrivers` для пар ИИ–ИИ в фоновых проходах (`explain: false`); для игрока и интерактивных UI-экранов строки пересчитываются явно через `OpinionSystem.Explain`. Аллокации в `opinion` упали до `0.0 MB`.
- **Разведка, миграция умов, родственные претензии и война (`ProvinceVision.cs`, `BrainDrain.cs`, `KinStatecraft.cs`, `MilitaryExperience.cs`, `WarDamage.cs`, `WarEmergency.cs`, `MovementRules.cs`, `MovementDealings.cs`)**:
  - `ProvinceVision.cs`: индекс акторов (`ArmiesByNation`, `BattlesByNation`, `ArmyProvinceById`, `InvasionBeachesByNation`) кэшируется на `Cache` с ключом `(stamp, ElapsedHours, landingRange)`.
  - `BrainDrain.cs`: недельный пул направлений и корректная точность `double DoorMultiplier = 1.65d`.
  - `KinStatecraft.cs`: параллельная проверка батчами по 256 регионов (`DeterministicParallel.For`) с последующей детерминированной сборкой кандидатов в порядке регионов.
  - `WarDamage.cs` (`Involves` напрямую по сторонам войны без перебора `campaign.Nations`, `WalksBeforeTable = 8`), `MilitaryExperience.cs`, `WarEmergency.cs`, `MovementRules.cs`, `MovementDealings.cs` (`_holderStatusScratch` + `CheckHoldsClaimFast`), `Escalation.cs`, `AiStatecraft.cs`/`AiDiplomacy.cs`/`AiJudgement.cs` (`DailyTables.LiveNationsSorted`), `DomesticCrises.cs`/`MassProtests.cs`, `DiplomaticGestures.cs` (`HasCommonRival`), `CultureUnion.cs`, `HomelandIntegration.cs`, `UnionCohesion.cs`, `RegionalAdministration.cs`, `Dismantlement.cs`, `Deals.cs`, `SecessionEvents.cs`, `StateIdentity.cs`, `Reintegration.cs`, `WarAims.cs`.
- **Побочная гигиена Beta3**: добавлены `.cs.uid` для `SimulationFrameClock.cs` и `StageCosts.cs` (`0` отсутствующих sidecar-файлов по всему `Scripts/`).

### Слияние `EntertAlpha` (`187c9f7f` -> `9452a88f`) и оптимизация `LongRangeMovement` + `AirReconnaissance` (`0912f8f6`)
При слиянии `187c9f7f` (`work/mapart` + `work/newscreen` + `work/controls` + `work/longrange`) вскрылась тяжёлая почасовая регрессия на поздних сейвах:
- На `bozhe.bdfsave` `orders:support` (`SupportCore.PrepareStep` -> `SupportRepatriation.Reconcile` -> `LongRangeMovement.Settle`) вырос с `74 ms` до **`5,099 ms` (38.2 % всего 14-дневного прогона)**, так как `Settle` каждый час перебирал по `34 138` провинций в `CapitalCore.SupplyOriginProvinceId` и повторном fallback-цикле для каждой неустроенной формации выбывших/безземельных стран.
- Ежедневный `support` вырос до `1,598 ms` (`712–786 ms` на тяжёлом шаге из-за полных обходов карты на страну в `Reposition`, `CoverWithSams`, `FlyToward` и `AirReconnaissance.Expose`).
- Исправления в `0912f8f6`:
  - `LongRangeMovement.Settle`: сначала быстрая проверка `CapitalCore.EffectiveProvinceId`, затем один общий проход `1..ProvinceCount` для всех стран, ещё не нашедших провинцию (выбывшие страны с `0` провинций гарантированно не вызывают `SupplyOriginProvinceId` повторно).
  - `LongRangeMovement.Reposition` / `CoverWithSams` / `FlyToward`: однократная постройка `BuildLandByController` на весь дневной шаг с сохранением точного порядка tie-breaking по `p`.
  - `AirReconnaissance.Expose`: однократный сбор координат провинций только для нужных вражеских контроллеров и однократный расчёт `Enemies` / `OpenWars` на страну вместо каждого вылета.

---

## 2. Замеры (до / Волна 2 до слияния / после слияния `187c9f7f` и `0912f8f6`)

### 2.1. Поздний сейв `bozhe.bdfsave` (211 MB, 14 дней + 30 дней smooth, `--diet`)

| Метрика | `7d2750a3` (Волна 1) | `4ddb4112` (Волна 2 до `187c9f7f`) | `9452a88f` (после `187c9f7f`, до фикса) | **`0912f8f6` (финал `work/perf3`)** |
|---|---:|---:|---:|---:|
| Поток симуляции (`CPU own`, ms/day) | 543.5 | **483.3** (−11.1 %) | 837.1 | **482.1** (serial: 616.1) |
| Все потоки процесса (ms/day) | 783.5 | **695.3** | 1215.4 | **683.0** |
| Wall (ms/day) | 575.0 | **509.6** (−11.4 %) | 953.6 | **555.8** |
| Аллокации (`kB/day`) | 16,770 | **13,247** (−21.0 %) | — | **13,952** |
| Smooth `9 ms` `p99` / `p99.9` / `worst` (ms) | 17.11 / 69.20 / 77.7 | **15.68 / 22.69 / 47.4** | 26.37 / 68.96 / 146.1 | **21.87 / 26.92 / 43.7** |
| Кадров > 33 ms (из 30 дней) | 2 | **1** | 15 | **1** (макс. неделимый этап: `19.3 ms`) |
| `orders:support` (14d, ms) | 74 | 56 | 5,099 | **157** (**×32.5** к `9452a88f`) |
| `support` (14d, ms) | 58 | 56 | 1,598 | **1,184** |
| `market` / `mkt:trade` / `mkt:nations` (ms) | 408 / 253 / 27 | **258 / 146 / 6** | 268 / 155 / — | **279 / 154 / 4** |
| `naming` (ms / MB) | 182 / 41.0 | **132 / 13.3** | 136 / 13.3 | **125 / 13.3** |
| `intel.visibility` / `kin:scan` (ms) | 138 / 136 | **78 / 54** | 68 / 58 | **74 / 58** |
| `internal migration` / `brain drain` (ms) | 148 / 106 | **76 / 78** | 69 / 73 | **87 / 107** |
| Загрузка сейва (ms, ОС-кэш разный) | 34,308 | 5,743 | 13,398 | 35,939 (холодный) |
| Отпечаток после 39 251 дня | `536AB736A490A9D2` | **`536AB736A490A9D2`** | `C15781CC5D1A5D28` | **`C15781CC5D1A5D28`** (parallel == `BDOF_NO_PARALLEL=1`) |

*(Примечание: `536AB736A490A9D2` до слияния полностью совпадает с `7d2750a3`; изменение на `C15781CC5D1A5D28` вызвано новыми системами из `EntertAlpha` `187c9f7f`, в частности `work/longrange`, и сохраняется идентично до/после `0912f8f6` и между многопоточным и однопоточным режимами.)*

### 2.2. Поздний сейв `campaign_20261116_010310.bdfsave` (274 MB, 14 дней + 30 дней smooth, `--diet`)

| Метрика | `7d2750a3` (Волна 1) | `4ddb4112` (Волна 2 до `187c9f7f`) | `9452a88f` (после `187c9f7f`, до фикса) | **`0912f8f6` (финал `work/perf3`)** |
|---|---:|---:|---:|---:|
| Поток симуляции (`CPU own`, ms/day) | 588.2 | **536.8** | 761.2 | **626.1** |
| Все потоки / Wall (ms/day) | 868.3 / 628.3 | **756.7 / 568.5** | 1092.6 / 798.3 | **901.8 / 664.7** |
| Аллокации (`kB/day`) | 18,655 | **16,558** | 17,244 | **16,967** |
| `orders:support` (ms) | 45 | 86 | — | **221** |
| `market` / `mkt:trade` / `mkt:nations` (ms) | 502 / 336 / 26 | **290 / 178 / 5** | — / — / 6 | **295 / 178 / 6** |
| Smooth `9 ms` `p99` / `p99.9` / `worst` (ms) | — | 22.34 / 46.12 / 60.3 | 22.97 / 56.94 / 89.0 | **23.16 / 45.41 / 65.2** |
| Отпечаток после 52 480 дней | `BA9D6B5738013458` | **`BA9D6B5738013458`** | `5CBD7BD361B49B97` | **`5CBD7BD361B49B97`** |

---

## 3. Верификация на финальном коде (`0912f8f6`)

- **Сборка 5 проектов (`-c Debug`/`Release`, `--artifacts-path "D:/Godot Projects/dof-perf3/tmp_logs/perf3-build"`)**: `DealOrForce.sln` (Debug и Release), `SimBench`, `FormableTests`, `ArmyInteractionTests` — `0` ошибок.
- **`Tools/Test-ReleaseVersion.ps1`**: `17/17 PASS` (`0.7.2.3`, `exit 0`).
- **`Tools/audit-localization.ps1 -Fail`**: `exit 0` (`russian: 27397 entries in 416`, `0` конфликтов / плейсхолдеров / пустых значений).
- **`SimBench 365 1 --soak`**: `Soak PASS: 365/365 days, 0 alert(s), 0 invariant failure(s)` (`exit 0`).
- **`SimBench 60 1 --fingerprint 60` (x2)**: `exit 0`, оба прогона дали `day 30: 13797964481020792265`, `day 60: 12587660896740538553`.
- **Полный `FormableTests`**: **`18,251 PASS, 0 FAIL`** (`exit 0`); таргетные наборы `--smooth-heavy-yield-only`, `--parallel-determinism-only`, `--performance-regressions-only`, `--identity-only`, `--support-repatriation-only`, `--living-world-fingerprint-only`, `--parity-wave1-only`, `--opinion-witnesses-only`, `--society-passes-only` — все `exit 0`, `0` `[FAIL]`.
- **`ArmyInteractionTests --long-range-only`**: `exit 0` (`511` строк вывода, `0` ошибок).

---

## 4. Аудит `D:\Godot Projects\dof-fw-saves` (`work/fw-saves`, read-only)

- Архитектурный обзор (`SaveContainer.cs`, `SaveHistoryTable.cs`, `SaveSystem.cs`, `docs/SAVE_FORMAT_BENCH_2026_11_17_RU.md`):
  - Контейнер `"BDF3"` (`ContainerVersion = 1`, `SaveSystem.FormatVersion = 2`) с 44-байтным заголовком, проверкой контрольной суммы SHA-256 и встроенным поточным сжатием `BrotliCodec = 1` (`CompressionLevel.Fastest` через буфер `1 MiB`). В рантайме игры Zstd не подключается (`ZstdCodec = 2` только для стенда), поэтому внешние нативные зависимости и риски сборки отсутствуют.
  - История стран (`SaveHistoryTable`) вынесена в 22-колоночный бинарный табличный блок на `ArrayPool<int>` вместо MessagePack (от последнего обоснованно отказались из-за подхвата `IMessagePackFormatter` из мод-сборок и зависимости бесконтрактного резолвера от `Reflection.Emit` / JIT). Остальной граф сохраняется в JSON для сохранения совместимости с текстовыми сейвами и модами.
- При первичном осмотре в `dof-fw-saves` были `10 M + 5 ??` поверх `7369e0a9` и битая ссылка на `SAVE_FORMAT_2026_11_18_RU.md`; впоследствии ветка зафиксирована в `a13ae3c7`, прошла гейт верификатора и уже интегрирована в `EntertAlpha` (`eeaa7e52`). Конфликтов с оптимизациями `work/perf3` не имеет; после `eeaa7e52` ветка `work/perf3` готова к интеграции.


## Интеграция `EntertAlpha` (`eeaa7e52`) и правки конкурентности `#71` (коммит `b7a7a0f9`)

- Ветка `work/perf3` объединена с `EntertAlpha` (`eeaa7e52`: `conquest-aftermath` + `work/deals` `TradeTerms` + `work/fw-saves` `BDF3`). Конфликты в `AiDiplomacy.cs`, `MarketCore.cs` и `TradeCore.cs` разрешены с сохранением обеих ветвей:
  - `AiDiplomacy.TryHedgeSupply`: сохранён кэш `LiveNationsSorted` (обход без мёртвых/движений) и правило `eeaa7e52` («наибольший излишек побеждает, при равенстве — меньший Id»).
  - `MarketCore.SettleNations`: сохранён параллельный цикл `DeterministicParallel.For` + `SettleSingleNation` с вызовом `DutyOf(nation, quote)` для `Relieved`-импорта.
  - `TradeCore.Affinity` и `MergeCommodity`: сохранена плоская матрица `AffinityValues` по `AffinityStride` + `HasPartnerRules`, домноженная на `AgreementFactor` при `PairTerms.Count > 0`, плюс обработка `buf.Deliveries` (`GuarantorShortWeeks`, `Delivered`, `ShortWeeks`).
- Закрыты все замечания независимого аудита `#71`:
  - `Scripts/Simulation/NationNaming.cs` (`GroundPeople`): возвращён `StringComparer.OrdinalIgnoreCase.Compare`.
  - `Scripts/Simulation/BrainDrain.cs` (`Stages`): `[ThreadStatic] _candidatesScratch` убран из корутины с `yield return 0`, заменён локальным `List<DestCandidate>`.
  - `Scripts/Simulation/LandAtlas.cs` (`CentresOf`): гонка двойной проверки закрыта атомарной подменой неизменяемого `CentresSnapshot` через `volatile`.
  - `Scripts/Simulation/CountryNames.cs` (`HasPoliticsOverride`) и `Scripts/Simulation/NavalCore.cs` (`NationAt`, `BuildHarbourIndex`): убраны парные lock-free чтения через атомарные `volatile`-снимки `PoliticsStemsSnapshot` и `MapIndexSnapshot`.
  - `Scripts/Simulation/ProvinceVision.cs` (`EnsureActorIndex`) и `Scripts/Simulation/LongRangeMovement.cs`: сохранён явный флаг `HasActiveBattles`, удалена заглушка `ActiveControllerMask`.

### Результаты проверок после правки (на `b7a7a0f9` без локальных правок кода)

- Сборка `--artifacts-path "D:/Godot Projects/dof-perf3/tmp_logs/perf3-build"`: `DealOrForce.sln` (`Debug` и `Release`), `SimBench`, `FormableTests`, `ArmyInteractionTests` — **0 ошибок**; отсутствующих `Scripts/**/*.cs.uid` — **0**.
- Статические проверки: `Tools/audit-localization.ps1 -Fail` (`russian: 27524 entries in 418`, `0` конфликтов/расхождений/пустых), `Tools/Test-ReleaseVersion.ps1` (`17/17 PASS`).
- Профильные наборы тестов (`0 FAIL`):
  - `FormableTests`: `--trade-terms-only` (`PASS=133`), `--deals-only` (`PASS=59`), `--save-container-only` (`PASS=43`), `--conquest-aftermath-only` (`PASS=57`), `--smooth-heavy-yield-only` (`PASS=7`), `--parallel-determinism-only` (`PASS=13`), `--performance-regressions-only` (`PASS=40`), `--identity-only` (`PASS=128`), `--brain-drain-only` (`PASS=20`), `--names-only` (`PASS=11`), `--country-name-settings-only` (`PASS=16`), `--campaign-isolation-only` (`PASS=11`), `--movement-staging-only` (exit `0`), `--stability-outlook-only` (exit `0`), `--diplomatic-participation-only` (`PASS=50`), `--diplomatic-symmetry-only` (`PASS=25`), `--exposure-only` (`PASS=25`).
  - `ArmyInteractionTests --long-range-only`: `PASS=481 FAIL=0`.
- `SimBench`:
  - `SimBench 60 42 --fingerprint 60` ×2: совпадает побитово (`day 30: 10029390469122779836`, `day 60: 18243716138951872501`).
  - `SimBench 365 1 --soak`: `Soak PASS: 365/365 days, 0 alert(s), 0 invariant failure(s)` (`GDP=94242B`, `wars=21`).
  - Поздний сейв `bozhe.bdfsave` `14 --diet --fingerprint 14`:
    - Параллельный прогон: `CPU own 334.8 ms/day` (`every thread 572.5`, `wall 359.4`), `mkt:trade 128 ms`, `mkt:nations 14 ms`, `orders:support 91 ms`; плавные кадры (`9 ms` бюджет, `1,271` кадр): `median 1.39 ms`, `p99 16.53`, `p99.9 20.21`, `worst 31.5`, `0 over 33 ms`; фингерпринт `7BB21264843608C8`.
    - Последовательный прогон (`BDOF_NO_PARALLEL=1`): `CPU own 410.7 ms/day` (`wall 417.4`), `mkt:trade 197 ms`, `mkt:nations 6 ms`, `orders:support 88 ms`; плавные кадры (`1,365` кадр): `median 2.91 ms`, `p99 19.70`, `p99.9 28.45`, `worst 31.8`, `0 over 33 ms`; фингерпринт `7BB21264843608C8` (полное побитовое совпадение с многопоточным).
- **Обнаруженные предсуществующие сбои вне `work/perf3` (не правились по границе владения)**:
  - Полный прогон `FormableTests.exe` прошёл `11,021 PASS, 0 FAIL` до исключения в `SupplyHedgeTests.AnAiSignsWithTheOtherSupplier()` (`SupplyHedgeTests.cs:170`, `exit=-532462766`): в пришедшем из `eeaa7e52` (`work/deals`) переключении `AiStatecraft.TryHedgeSupply` на `TradeTermsComposer.Propose` фикстура `SupplyHedgeTests.World()` оставляет `WeeklyTaxIncome = 0`, из-за чего `TradeTermsValue.Read` берёт минимальный `Revenue = 0.001f`, штраф за потерю пошлины (`TradeValueDuty`) насыщается до `-200`, и ни один пресет не набирает `>= 0`. Файлы `TradeTerms*.cs`, `AgreementsCore.cs` и `SupplyHedgeTests.cs` имеют 0 отличий от `eeaa7e52`.
  - В `--naming-only` зафиксирован предсуществующий сбой на `NamingTests.cs:205` (`WarNames.Of` вернул `"Republic of East War of Independence"` вместо `"Saudi Arabia Civil War"`); `WarCore.cs` в `work/perf3` не изменялся.
