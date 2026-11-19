# Полиглотные ядра и нативные библиотеки (`work/polyglot-kernels`, 2026-11-18)

> Отчёт по реализации и честному end-to-end профилированию двух zero-bottleneck полиглотных архитектур на ветке `work/polyglot-kernels` (база `fe59a338`, рабочее дерево `D:\Godot Projects\dof-polyglot`): **Persistent Native Arena (Rust `cdylib` + C ABI v1 + `.NET [LibraryImport]`)** и **расширение чистых F# 10.1 `[<Struct>]`-правил с нулевой аллокацией**.

---

## 1. Почему предыдущие эксперименты проиграли управляемому C#

Предыдущие стенды (`Tools/KernelBench`, `Tools/OwnershipKernelBench`, `Scripts/Simulation/RasterOverlayKernel.cs`) строили нативное ядро по схеме «вызов -> упаковать объектный граф -> `GCHandle.Alloc` / 7–8 пинов -> перестроить/очистить массивы -> сбросить буфер -> отпинить». На горячих проходах маршалинг, обнуление 35k-элементных массивов и копирование `int[][]`/`List<int>` съедали весь выигрыш нативного кода.

---

## 2. Архитектура 1: Persistent Native Arena (`native/dof_kernels`)

Реализована независимая библиотека на Rust (`edition 2021`, ноль внешних крейтов, `opt-level=3`, `lto="fat"`, `codegen-units=1`, `panic="abort"`, `-C target-cpu=x86-64-v3`) в `native/dof_kernels/` со стабильным контрактом [`native/dof_kernels/include/dof_kernels.h`](../native/dof_kernels/include/dof_kernels.h) (ABI v1) и размером релизной сборки `203 776` байт (`tmp_logs/rust_target/release/dof_kernels.dll`).

### Ключевые инварианты

1. **Единоразовая аллокация при загрузке карты / кампании**:
   - `DofProvinceGraph` (`graph.rs`) создаётся один раз на `ProvinceTopology` через `ConditionalWeakTable<ProvinceTopology, NativeProvinceGraph>`: хранит плоский CSR (`csr_offsets[N+1]`, `csr_edges[E]`, предвычисленные `edge_km[E]` по точной формуле `SupportCore.DistanceKm`, `land_offsets/edges`, `water`, `coastal_provinces`, `harbour_basin`, `prov_lat/lon`).
   - `DofGraphScratch` выделяется лениво в `ThreadLocal` на поток-воркер без блокировок и переиспользуется через `u32` счетчик поколений (`seen_gen` / `reach_gen` / `stamp_gen`) без обнуления 34k-элементных массивов на каждом вызове.
   - `DofRasterBake` (`raster.rs` + `raster_blur.rs`) кэшируется по неизменяемому `authored_holders` (`5760×2880` = `16 588 800` ячеек) и держит постоянные буферы `distance`, `across`, `seam_snap` и L1/L2 `65 536`-LUT `prov_lut`, исключая ~166 MB LOH-аллокаций (`holders`, `distance`, `across`) на перекраске карты мира (`FrontierField.Build`). Параллельный 2-проходный Chamfer сохраняет пограничные `halo`-строки (`ceil(cap) + 1`), гарантируя **побайтовую идентичность** с однопоточным C#.
   - `DofTradeArena` (`trade.rs`) кэшируется на `Campaign` для `GreatCircle` и плотной матрицы `PartnerAffinity`.
2. **Безопасность и бесшумный откат на C#**:
   - [`Scripts/Simulation/Kernels/NativeKernelsLoader.cs`](../Scripts/Simulation/Kernels/NativeKernelsLoader.cs) ищет `dof_kernels.dll` через `NativeLibrary.TryLoad` (`DOF_KERNELS_PATH`, `DOF_KERNELS_DIR`, `AppContext.BaseDirectory`, папку сборки), сверяет `dof_kernels_abi_version() == 1` и поддерживает отключение (`DOF_KERNELS_DISABLE=1` и флаг тестов `SimulateUnavailableForTests`).
   - При отсутствии DLL, несовпадении ABI или ошибке в параметрах адаптеры [`NativeRasterBake.cs`](../Scripts/Simulation/Kernels/NativeRasterBake.cs), [`NativeProvinceGraph.cs`](../Scripts/Simulation/Kernels/NativeProvinceGraph.cs) и [`NativeTradeArena.cs`](../Scripts/Simulation/Kernels/NativeTradeArena.cs) возвращают `false` без исключений, и [`FrontierField.cs`](../Scripts/Simulation/FrontierField.cs) / [`LongRangeMovement.cs`](../Scripts/Simulation/LongRangeMovement.cs) уходят на побитово-идентичный управляемый C#.
   - Папка `native/` изолирована от сканера Godot через `native/.gdignore` и исключена в `DealOrForce.csproj` (`<DefaultItemExcludes>...;native\**;...</DefaultItemExcludes>`). Скрипт сборки/деплоя: [`Tools/build-kernels.ps1`](../Tools/build-kernels.ps1) (`-Deploy`, `-RemoveDeployed`).

---

## 3. Архитектура 2 и аудит F# 10.1 (`Scripts/Rules/`)

Проведён аудит всех 12 модулей `.fs` в `Scripts/Rules/` (`FSharp.Core 10.1.402`, `TreatWarningsAsErrors=true`):

| Модуль | Статус до правки | Изменения / аудит |
|---|---|---|
| `AssaultRules.fs` | `[<Struct>] Assessment` + `AssaultRulesHook` + `AssaultRulesFSharpBoot` | Без изменений (0 байт/вызов, проверен) |
| `WarScoreRules.fs` | `[<Struct>] WarScoreOutcome` + `WarScoreRulesHook` + Boot | Без изменений |
| `WarRhythmRules.fs` | 5 чистых функций + `WarRhythm` + Boot | Без изменений |
| `PeaceRules.fs` | `PeaceRulesHook` + Boot (`FindParts` использует `HashSet`/`Queue`) | Сохранён без дрейфа отпечатков |
| `CatalogueWindow.fs` | `[<Struct>] CatalogueSlice` для UI-окна | Без аллокаций |
| `DiplomaticActRules.fs`, `PoliticalPolicyRules.fs` | Чистые предикаты/enum | Без изменений |
| `EconomyRules.fs` | Был без Boot-хука и C#-фоллбека | Добавлены `[<Struct>] TradeCostOutcome`, `EvaluateTradeCost`, `TaxCompliance`, `EvaluateBudgetLineAmount` + [`EconomyRulesHook.cs`](../Scripts/Simulation/EconomyRulesHook.cs) + [`EconomyRulesFSharpBoot.cs`](../Scripts/Boot/EconomyRulesFSharpBoot.cs) |
| `DemographyRules.fs` | `[<Struct>] AgeCohortStep` без Boot-хука/фоллбека | Добавлены [`DemographyRulesHook.cs`](../Scripts/Simulation/DemographyRulesHook.cs) + [`DemographyRulesFSharpBoot.cs`](../Scripts/Boot/DemographyRulesFSharpBoot.cs) |
| `DiplomacyRules.fs` | Был без `[<Struct>]` и Boot-хука | Добавлены `[<Struct>] DeterrenceOutcome`, `EvaluateDeterrence` + [`DiplomacyRulesHook.cs`](../Scripts/Simulation/DiplomacyRulesHook.cs) + [`DiplomacyRulesFSharpBoot.cs`](../Scripts/Boot/DiplomacyRulesFSharpBoot.cs) |
| `CombatTempoRules.fs` | Был без `[<Struct>]` и Boot-хука | Добавлены `[<Struct>] CombatTempoBundle`, `[<Struct>] SupplyDeliveryOutcome`, `EvaluateTempoBundle`, `EvaluateSupplyDelivery` + [`CombatTempoRulesHook.cs`](../Scripts/Simulation/CombatTempoRulesHook.cs) + [`CombatTempoRulesFSharpBoot.cs`](../Scripts/Boot/CombatTempoRulesFSharpBoot.cs) |
| `PeaceLedger.fs` | Мёртвый прототип (`LedgerEntry`, `Demand list`, `decimal`, 0 вызовов в C#) | Зафиксирован в аудите, сохранён для бинарной совместимости, добавлен zero-alloc `[<Struct>] PeaceQuotaOutcome` и `PeaceQuotaRules.EvaluateQuota` |

Все новые хуки подключены в `SovereignFinance.cs`, `MarketCore.cs`, `BudgetCore.cs`, `DemographicCycle.cs`, `Consolidation.cs`, `AiStatecraft.cs`, `Deterrence.cs`, `CombatTempo.cs`, `LogisticsCore.cs`, прогреваются в `JitWarmup.cs` и зарегистрированы в `Tools/FormableTests/FormableTests.csproj`. При падении делегата хук однократно снимается с логом `RULES_BACKEND fallback=managed` без срыва кампании.

---

## 4. Честные end-to-end бенчмарки на реальных данных (34 138 провинций / 5760×2880)

Замеры выполнены в `Release` (`AVX2` `simd=2`, `FormableTests --polyglot-kernels-only`, финальная сборка с выделением `raster_blur.rs`, с учётом пиннинга, FFI-вызова и материализации результатов в C#):

| Ядро / сценарий | Объём данных | Управляемый C# | Persistent Rust `dof_kernels.dll` | Ускорение / статус | Паритет | Включено по умолчанию? |
|---|---|---|---|---|---|---|
| `FrontierField.Build` (`5760×2880`) | 16 588 800 px (иниц. + аннексии) | мед. **298.38 мс** (p95 313.27) | мед. **86.23 мс** (p95 104.77, warm 73.6 мс) | **3.46×** + экономия ~166 MB LOH | Побайтово `= 100%` (`D265E120FD138417`, `BF3126D9268A86FA`) | **Да** (`EnableRasterDefault=true`) |
| `LongRangeMovement.DistanceField` (`1600 km`) | 34 138 пров., 12 570 достижимо | мед. **13.460 мс** (p95 14.637) | мед. **1.407 мс** (p95 1.739; граф 13.0 мс раз на карту) | **9.57×** (включая `Dictionary<int,float>` + `List<int>`) | Побитово `= 100%` по всем 12 570 `float` | **Да** (`EnableDistanceFieldDefault=true`) |
| `OwnershipComponents` (`34k`) | 34 138 пров., 2 371 комп. | мед. **0.899 мс** (p95 0.963) | мед. **0.840 мс** (p95 0.892) | **1.07×** (на уровне погрешности, маршалинг съедает выигрыш) | Побитово `= 100%` | **Нет** (`C# по умолчанию`) |
| `LogisticsCore` (`dof_graph_solve_supply_v1`) | `CHN`, 136 армий | вся земля (паралл. C#): **5.525 мс** | одна страна `CHN`: **0.132 мс** | Параллельный C# с `[ThreadStatic]` уже быстр | Совпадает на всех 136/136 дивизиях | **Нет** (`EnableSupplyDefault=false`) |
| `TradeAffinity` (`200×200`) | 40 000 пар (плотный span) | мед. **0.211 мс** | мед. **0.153 мс** (без стоимости упаковки словарей C#) | **1.38×** сырых span (упаковка словарей дороже) | Побитово `= 100%` | **Нет** (`EnableTradeDefault=false`) |
| `TradeGreatCircle` (`360×360`) | 129 600 пар | мед. **2.181 мс** | мед. **1.716 мс** | **1.27×** (вызывается редко) | **129 600 / 129 600** exact bit match, diff `0` | **Нет** (`EnableTradeDefault=false`) |

### Латентность и аллокации F# `[<Struct>]` правил

- `--polyglot-kernels-only` (12 000 псевдослучайных и граничных кейсов по `EconomyRules`, `DemographyRules`, `DiplomacyRules`, `CombatTempoRules`, `PeaceQuotaRules`, а также проверка установки хуков `WarScoreRulesHook`, `AssaultRulesHook`, `WarRhythm`): **0 байт** аллокаций на потоке, **100% совпадение** битов IEEE-754 `SingleToInt32Bits` с управляемым C#.
- `--rules-latency-only` (`Release`, 4096 вызовов × 31 замер): `assault-hook-delegate` мед. `33.01 нс`, `assault-direct-fsharp` `30.96 нс`, `pulse-fsharp-delegate` `4.57 нс`, `pulse-csharp-reference` `2.44 нс`, аллокации `0.0` байт/вызов во всех ветках (профиль `WarScoreRules` и `WarRhythmRules` дополнительно подтверждён `--war-score-isolation-only` и `--war-rhythm-only`).

---

## 5. Проверка детерминизма и прогона кампаний

- `SimBench 365 1 --fingerprint 365` (**с `dof_kernels.dll`**): `day 365: 17612415472924603674` (`without the retirement clock 6407327689794137374`) — **0 дрейфа** относительно `fe59a338`.
- `SimBench 365 1 --fingerprint 365` (**без `dof_kernels.dll`**, чистый C# fallback): `day 365: 17612415472924603674` (`without the retirement clock 6407327689794137374`) — **0 дрейфа**.
- `SimBench --profile-save tmp_logs/perf/bozhe.bdfsave 14 --diet`: итоговый отпечаток `7BB21264843608C8` (идентично базе `fe59a338`), round-trip `FB2D61830F29C983`. Замечание по `bozhe.bdfsave`: `support` на дне 39179 составил `501.6 мс` (на 14 днях `783 мс` против `733 мс` в базе), так как в коротком 14-дневном прогоне `DistanceField` вызывается редко и на день 39179 пришлась однократная сборка графа `NativeProvinceGraph` (`~45–67 мс`), тогда как остальное время шага `support` лежит вне `DistanceField` (`NearestByRoad`, `Targets`, `SupportCore`).
- Модульные наборы `FormableTests` (`Release`, 13 прогонов): `--polyglot-kernels-only`, `--rules-backends-only`, `--rules-latency-only`, `--frontier-field-only`, `--ownership-kernel-only`, `--war-score-isolation-only`, `--combat-tempo-only`, `--war-rhythm-only`, `--assault-rules-only`, `--peace-rules-only`, `--supply-network-only`, `--corridor-report-only`, `--deals-only` — все `EXIT 0`.
- `cargo test --release` в `native/dof_kernels`: 3/3 тестов `ok` (`abi_and_null_guards`, `distance_field_and_owner_components_roundtrip`, `parallel_chamfer_matches_sequential_exact`).
- Проверка сайдкаров `.cs.uid` в `Scripts/`: `SCRIPTS_MISSING_CS_UID=0`. `Tools/audit-localization.ps1 -Fail` -> `exit 0` (`27524 entries in 418`, `0` конфликтов/расхождений). `Tools/Test-ReleaseVersion.ps1` -> `exit 0`.
