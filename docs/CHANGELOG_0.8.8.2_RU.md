# Deal or Force — PortAlpha 0.8.8.2

26 ноября 2026 · Windows x64 и Android · Godot 4.8 Beta 3 (.NET 10)

Выпуск **PortAlpha 0.8.8.2** (Android `versionCode` 14) добавляет нативные SIMD-ядра на Rust (`dof_kernels` для дипломатии, рынка, дистанций, рендер-подготовки и экономики/демографии), 4-режимную настройку кампании `ai_diplomacy_lod`, разнесение тяжёлых недельных этапов симуляции, 6-компонентное архитектурное устранение фризов/дёрганий карты и утечек памяти в реальном времени наряду с синхронизацией Скорости 5 на ПК, оптимизацию шейдера GLES3 и видеопамяти/ОЗУ, а также исправления масштаба кораблей, имён `rising:...`, панели движений наблюдателя, гардов `!is_inside_tree()` и сборки Android APK. Предыдущие выпуски описаны в [CHANGELOG 0.8.7.3](CHANGELOG_0.8.7.3_RU.md) и [CHANGELOG 0.8.5.3](CHANGELOG_0.8.5.3_RU.md).

---

## Нативные SIMD-ядра на Rust (`dof_kernels`)

- **Кроссплатформенная библиотека `native/dof_kernels/`.** Собирается в Release для Win64 (`dof_kernels.dll` с AVX2/SSE2, `Caps=0xDF`), Android `arm64-v8a` (`libdof_kernels.so` с NEON и выравниванием ELF по 16 КБ) и Android `x86_64`. При старте кампании `DealOrForceGame` пишет в лог строку `[dof-kernels]` со статусом, ABI, уровнем SIMD и флагами возможностей.
- **Дипломатия, рынок, дистанции и рендер-подготовка (`CapDiplomacy` / `CapMarket` / `CapDistance` / `CapRenderPrep`).** По замерам `KernelBench` (`11/11 BIT-EXACT` против чистого C#): `dip_pair_score` `2.63×`, `dip_reason_decay` `1.62×`, `market_settle` `3.10×`, `market_clear` `2.45×`, `distance_within_range` `1.80×`, `raster_pack_overlays` `1.38×`, `political_plate (1920×960)` `6.57×` (`18.997 → 2.892 мс`). В `PolyglotKernels` перерисовка границ `raster_frontier_5760x2880` ускорена в `9.64×` (`685.59 → 71.09 мс`, побитово точно по всем 16.59 млн клеток).
- **Экономика и демография (`CapEconomyDemo`).** Ускорения микроядер: `demo_step_cohorts` `5.05×`, `econ_household_groups` `5.75×`, `econ_vital_rates` `8.92×`, `demo_aggregate_nations` `4.00×` (агрегация оставлена по флагу `DOF_NATIVE_DEMO_AGGREGATE=1`, т.к. A/B в `SimBench` показал оверхед упаковки `StatesOf`). По `SimBench 120d` нативные ядра ускоряют `economy` на `9.9%`, `eco:states` на `13.1%` и `demography` на `9.7%` при идентичном `CampaignFingerprint` с `DOF_KERNELS_DISABLE=1`.

## Настраиваемый LOD ИИ-дипломатии, разнесение этапов и экономия ОЗУ

- **4-режимная настройка кампании `ai_diplomacy_lod` (`00_settings.json`, `zz_447_ai_diplomacy_lod.json`).** Выбор прямо в настройках: `full` (вся дипломатия мира), `world` (независимая от игрока оценка значимых стран, великих держав, войн, союзов и очагов), `balanced` (баланс по умолчанию) и `player_focus` (приоритет окружения игрока с автоматическим откатом в `world` в режиме наблюдателя). Сохраняется в `Defines` и учитывается в `CampaignFingerprint` при отличии от `balanced`.
- **Разнесение недельных проходов по `ElapsedDays % 7` и кэш фронтов.** `MarketCore`, `BrainDrain`, `TechnologyCore`, `TickStages`, `MovementRules`, `Statecraft` и когортная `InternalMigration` разведены по дням недели; `QuietFronts` и 8-слотовый LRU `FrontDisengagement` убрали фризы (`SimBench 120d --profile`: `0` кадров `>33 мс`, худший `25.7 мс`, медиана `1.37 мс`).
- **Оптимизация ОЗУ и GC (`HistoryCore`, `LongCampaign`, `SaveRows`, мобильный старт).** На мобильных устройствах освобождается буфер `_frontierAuthored` (`33.2 МБ`), после загрузки карты выполняется одноразовая сборка Gen-2 (`gc_ms=35`, управляемая куча `153 МБ` на Galaxy S22 в мировой войне), уплотнены ёмкости коллекций и интернирование строк (на `bozhe.bdfsave` 2131 г. куча снижена `534 → 500 МБ` на загрузке, `450 → 408 МБ` после «Диеты», `537 → 508 МБ` после 153 дней).

## Синхронизация рендера ПК, устранение фризов и GPU GLES3 на Android

- **5-компонентный фикс синхронизации карты, фронтов и армий на Скорости 5 на ПК:**
  1. `DealOrForceGame._Process` делит своё окно чтения с глобусом (`GlobePresentation.SyncWorldVisualsUnderReadLock`), чтобы обновление HUD не отбирало доступ у карты.
  2. `Campaign.TickStages.RunStages` уступает замок между этапами при `AccessGate.IsReaderStarving`, а `CampaignAccessGate.ExitWrite` держит окно восстановления `50 мс`.
  3. Снижен рабочий срез десктопного `BackgroundSimRunner` на 5× скорости (`sliceMs=24 мс`, уступка через `8 мс`, `readerWorkAheadMs = 0`) с полным сохранением мобильных таймингов.
  4. `SyncFencedWorldVisuals` перекрашивает провинции и живые фронты (`WarRevision` + `MapRevision`, однопроходный `PaintLiveWarFronts`) *строго до* армий, боёв и маршрутов на едином десктоп-интервале `33 мс`.
  5. `GlobePlateRepaint` сохраняет быструю Rust-перекраску `PoliticalPlate` (`0.75 с` / `2.5 с` на Скорости 4–5) и ограничивает пересборку `5760×2880` `FrontierField` (`2.5 с` / `10.0 с` на Скорости 4–5).
- **6-компонентное архитектурное устранение дёрганий, Gen-2 GC-фризов и ошибок дерева сцены в реальном времени:**
  1. Повторное использование LOH-буферов через `PoliticalPlate.PaintInto` и `FrontierField.BuildInto`, разнос GPU-аплоадов пластины и поля границ по разным кадрам и обязательный `.Dispose()` временных `Image`.
  2. Пересборка `CityMiniatureRenderer` только при структурных изменениях (`DestroyedProvinces.Count`, `CapitalRevision`, год), дифференциальный кэш меток стран (`_countryLabelCache`) и дебаунс переразметки на 4–5× скорости.
  3. Расширенный гистерезис `SurfaceBattleBadge` (`0.01f` доли / `0.15f` импульса), расширение пула `SubViewport` значков боя (`24 → 192`), скретч-буфер и `Bilinear`-фильтр для текстур фишек (`_counterTextureScratch`).
  4. Перевод `PaintLiveWarFronts` на одиночный проход `O(P)` по провинциям и кэширование координат/статуса маркер-стеков армий (`HasPendingArrivalGlide`) с пропуском лишних вызовов фингерпринта.
  5. Выполнение шага камеры (`AdvanceCameraForFrame`) до синка с симуляцией, монотонные проверки `>` без лишних фризов на залочках, адаптивное к темпу шага демпфирование фишек (`_simStepIntervalEma`) и отложенный `InvalidateFlatCounterDensity()`.
  6. Гарды `IsInsideTree()` / `IsInstanceValid()` для `_camera` и корней оверлеев в `GlobeCities` и `GlobeOverlaySync` (устранение ошибок `!is_inside_tree()`).
- **5 GPU-оптимизаций шейдера (`globe_map.gdshader`).** Точно сохранён бюджет 12 сэмплеров GLES3 `PackedMapShader` при сокращении лишних выборок и ветвлений; на Android включены `MobileRenderScaleFloor = 0.50f`, `MobileMediumDefaultHighDpiScale = 0.85f`, текстуры карты `2880×1440` и выборочное ETC2/ASTC сжатие (`sync-core.ps1`).
- **Подтверждённые замеры на Samsung Galaxy S22 (`SM-S901B`).** Размер APK уменьшен `408.9 → 383.9 МБ` (ARM64, 16 КБ ELF выравнивание, `VERIFY_STORED failures=0`); по `dumpsys meminfo` в мировой войне: `GL mtrack` снижен **`654.6 → 537.6 МБ`** (`-17.9%`), `SWAP PSS` **`393.6 → 64.2 МБ`** (`-83.7%`), `TOTAL PSS` `1.78 → 1.66 ГБ`, старт меню `4.13 с`, `0` вылетов.

## Исправления визуала, имён, наблюдателя и упаковки

- **Защита размера кораблей и бесконечных стрелок (`GlobeTradeTraffic`, `DateLineSeam.GlobeLineSanity`).** Отсечены гигантские/битые скейлы торговых судов, невалидные `centerCells[provinceId] <= 0` и несмежные источники стрелок атаки.
- **Починка имён `rising:...`, панели наблюдателя и `zipalign`.** Исправлены вложенные имена мятежников (`rising:...`), пустой список безземельных движений в режиме наблюдателя, `player == null`-гарды HUD и ошибка парсинга `stderr` у `zipalign` в Android `build-apk.ps1`.
