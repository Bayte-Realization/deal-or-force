# Проход 22 ноября 2026 (Волны M4 + M5): релизная сборка ПК `0.8.3.5`, облегчённый Dual-ABI Android Release APK v8 (`0.8.3.8`), 12 правок UI, GPU/загрузка и ускорение симуляции

> **Основа и верификация.** Все показатели ниже подтверждены независимым прогоном верификации (`#611`), итоговыми отчётами исполнителей (`#565`, `#567`, `#582`, `#584`, `#592`, `#593`, `#602`, `#603`, `#606`, `#607`, `#608`) и финальной двойной сборкой (`#612`). Редактор Godot пользователя (`PID 1924`) и эталонный сейв 2053 года (`campaign_20261122_005807.bdfsave`, `49 445 932` Б) не затрагивались.

---

## 1. Двойная релизная поставка (`#612`)

### 1.1. Android Release APK v8 (`0.8.3.8`, Dual-ABI `arm64-v8a` + `x86_64`)
- **Путь**: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-22.apk` (+ `.sha256`), коммит тонкой оболочки `2a8a838` (`0` remotes, без push).
- **Размер**: **`445 123 599` Б (`424,50 МБ`)** — уменьшен на **`−100,23 МБ`** относительно v7 (`524,73 МБ`) благодаря экспорту `--export-release` / `-c Release` (`PDBS=0`, `APPLICATION_DEBUGGABLE_COUNT=0`) и дедупликации `game/gfx/` из внешнего архива.
- **SHA-256**: `08C85E5B7C186DDDD4FF36A6058F8A97377A577BE93785A4CA10E84DBCD6AC9A` (`SHA256_MATCH=True`).
- **`assets/android_game_fs.zip`**: **`69 425 132` Б (`66,21 МБ`, `1 692` файла, `skipped_pck_gfx=4440`)** вместо `195,0 МБ` / `6 136` файлов ранее (**`−66,0%`**); честный `STORED` (метод `0`, `raw == comp`) через новый `.NET 10` инструмент `Tools/ApkZipTool/` (устранён баг псевдо-deflate PS 5.1).
- **Все 5 предзапечённых кэшей (`fp=3954006852556129685`, `duplicated_outer_cache=0`)**:
  - `map_boot_v2.bin` (`4 926 493` Б, **`terrainSlots=true`** — Android не декодирует `terrain.png` на старте);
  - `hydrology_v2.bin` (`2 672 675` Б);
  - `political_rasters_v1.bin` (`4 344 697` Б);
  - `lakes_boot_v1.bin` (`108 052` Б);
  - `sprite_index_v1.json` (`365 790` Б, `sprites=4502`, `shaders=5`).
- **Целостность подписи и упаковки**: `APKSIGNER_EXIT=0`, `aapt2`: `com.bayterealization.dealorforce`, `versionCode='8'`, `versionName='0.8.3.8'`, `minSdk='24'`, `targetSdk='36'`, `native-code: 'arm64-v8a' 'x86_64'`, `arm64_so=11`, `x64_so=11`, `VERIFY_STORED apk_fs_zip_method=0 audio_stored=114/114 ctex_stored=4460/4460 sparsepck_stored=1/1 inner_media_stored=24/24 inner_bin_stored=2/2 failures=0`, `UNGUARDED_GCSETTINGS=0`.

### 1.2. Релизная сборка ПК (`EntertAlpha 0.8.3.5 2026-11-22`)
- **`Tools/build.ps1`**: компиляция исходников **`0` ошибок / `0` предупреждений**, **`EXPORT_VERIFY failures=0`**, чистый выход `code=0`, `0` ошибок шейдеров и скриптов.
- **Распакованное зеркало**: `C:\Билды DoF (DiskC)\EntertAlpha 0.8.3.5 2026-11-22\Deal or Force` (`Deal or Force.exe` `100,32 МБ`, `Deal or Force Launcher.exe` `73,12 МБ`, `Deal or Force.pck` `238,69 МБ`, `DealOrForce.dll` `11,51 МБ`, `game\` `6 236` файлов / `287,16 МБ`, `mods\` `486` файлов / `6,12 МБ`).
- **Архив**: `C:\Билды DoF (DiskC)\Deal or Force EntertAlpha 0.8.3.5 2026-11-22.zip` (+ `.sha256`), **`681 677 804` Б (`650,10 МБ`)**, SHA-256 `3D98C009E73E8248C0665AF54DE4590D50667459641BBC55DF727EB984461F89` (`SHA256_MATCH=True`).

---

## 2. Все 12 исправлений UI, тач-гигиены и ленивой загрузки (`#565`, `#582`, `#593`, `#603`)

1. **`ShellCornerView`**: исправлен баг нулевой ширины `ClipText` на плашках ресурсов.
2. **Outliner**: исправлен `RemoveChild` при обновлении узлов списка.
3. **Армейский чип**: устранена ошибка отображения/обрезки чипа соединений.
4. **`TooltipKeeper`**: добавлена защита от залипания тултипов на тач-экранах.
5. **DEFCON / Tension**: устранено 52-пиксельное переполнение индикаторов напряжения.
6. **Ещё 5 багов `ClipText`**: вылечена обрезка текста в экранах Экономики, Сообщений и `ScenarioMapEditor`.
7. **Левая панель навигации**: закрыта «мёртвая дыра» в раскладке левой рейки.
8. **`SafeArea`**: выровнена посадка HUD внутри безопасных отступов вырезов экрана.
9. **11 ленивых модальных экранов**: модальные экраны создаются по требованию вместо тяжёлой инициализации на старте кампании.
10. **Идемпотентность каталогов и `sprite_index_v1.json` (`#603`)**: `SpriteCatalog`, `SoundCatalog`, `MusicCatalog`, `FontCatalog` не пересканируют ~4 500 файлов между `MainMenuScene` и `DealOrForceGame`; добавлен быстрый путь предзапечённого индекса `cache/sprite_index_v1.json` и отложенный прогрев `TextureWarmup`.

---

## 3. Рендер, GPU и ускорение загрузки кампании (`#584`, `#606`)

- **Шейдер `globe_map.gdshader` и мобильный MSAA/масштаб**: отсечение вычислений океана на чистой суше (`political.a < 0.999`) и рельефа/гидрологии в чистом океане (`political.a > 0.001`), подстройка `MobileEffectiveRenderScale` (`0.65..1.0` с шагом `FrameRateGovernor`), отключение лишнего MSAA в `gl_compatibility` / эмуляторах при сохранении десктопного `relief_displacement` и чёткости Android.
- **Нулевая работа на простое в плоском режиме (`UpdateFlatWorldPositions`)**: dirty-гейты для 260 столиц, ~2 000 глифов стран, флотов, дальнобойных маркеров и стеков армий.
- **Разнесение 32 МБ GPU-загрузок по кадрам**: `ApplyFrontier` (кадр N) и `_plateTexture.Update` + `UploadPlateCatchUp` (кадр N+1) разведены на разные кадры; на таче `MobilePlateRepaintSeconds = 6.5s` и сняты runtime-перерисовки `FrontierField` во время войны; `4.15M`-даунсэмплинг подписей вынесен в `Task.Run`; кэширован `_regionCellCounts` (убран обход 16,6 млн ячеек на каждый `MapRevision`).
- **Устранение лишних декодов старта (`#606`)**:
  - Убран **100 МБ** пустой перезаписывающий проход `_packedIds` на `gl_compatibility` (`UploadPackedIds` через `CreateFromImages`);
  - Пропуск дублирующего вызова `TerritorialTopology.Apply` в `BindCampaign`, если `ScenarioCatalog` уже применил топологию;
  - Убран синхронный разбор **1,63 МБ** GeoJSON (`rivers.json`/`lakes.json`) из лог-строки на главном потоке;
  - Освобождение ~50 МБ prefetch-растра `provinces.png` при попадании в `MapBootCache` (`ReleasePrefetch`);
  - LZ4-кэш мобильных тайлов `terrain_mobile_v1.bin` (`128px` Bilinear) и снятие GPU→CPU `GetImage()` readback в `MenuGlobe.Picture`.

---

## 4. Симуляция, ИИ и скорость ходов (`#567`, `#592`, `#607`, `#608`)

- **Экономика, флот, снабжение и бюджет Speed-5 (`#592`, `#607`)**:
  - Оптимизация `StaggerWeeklyEconomy`, разблокировка бюджета Speed-5 и снижение `sup:reposition` в **`3,73×`**;
  - Худший шаг сокращён с **`1 137,6` до `302,5 мс`**; аллокации обычного дня снижены (`2 812 → 2 134 кБ/шаг`), тяжёлого дня (`13 865 → 11 770 кБ/шаг`); холодное создание кампании `1 707 мс`, тёплое `401 мс` (`rasters read 0,0 мс`).
- **Statecraft, дипломатия, кризисы, организации и `--civic-only` (`#608`)**:
  - `StrandedGarrisons.cs`: исправлен `DefectionChance` при `share >= 1f` (`--civic-only` PASS) + zero-alloc `[ThreadStatic]` в `Kin`.
  - `AiStatecraft.cs`: устранена ошибка `lcm(7, 30) = 210д` голодания `ai:economy` при `AiCalmCadenceDays` (спокойная экономика отвязана на `day % 30 == 0`: `ai:economy` `×315 → ×1 029` за 120д, постройки на 90-й день `158 → 394`), введены ярусы `CalmCadences` (Major `7/14д`, Minor `14/21д`, Micro `14/28д`), сократившие `war:judge` (`×8 612 → ×4 410`, `20,4 → 13,0 МБ`) и `ai:warmaking` (`×9 241 → ×4 509`, `30,5 → 24,7 МБ`).
  - `DiplomaticGestures.cs`, `DomesticCrises.cs`, `InternationalOrganizations.cs`: фильтрация неактивных/выбывших акторов, `Warfare.LiveWars`, однопроходные `[ThreadStatic]` веса армий (`organizations` и `organization life` — **`0,0 МБ`** на сейве 2053).

---

## 5. Независимый гейт верификации (`#611`, `#608`, `#612`)

- **Сборки (`0` ошибок)**: `DealOrForce.csproj` Debug, `FormableTests.csproj` Release, `SimBench.csproj` Release; `Tools/build.ps1` `0` ошибок / `0` предупреждений.
- **Локализация**: `Tools/audit-localization.ps1 -Language russian -Fail` — **PASS** (`29 309` записей в `443` файлах, `0` конфликтов, `0` расхождений плейсхолдеров, `0` пустых значений).
- **`FormableTests` Release**: **`24/24` PASS (`0` FAIL)** в независимом гейте `#611` (`--ui-workspaces-only`, `--ui-clarity-only`, `--shell-coherence-only`, `--android-platform-only`, `--bilateral-peace-only`, `--mod-studio-only`, `--map-boot-cache-only 113`, `--gpu-atlas-contract-only`, `--border-rank-only 104`, `--frontier-field-only 17`, `--map-labels-only 51`, `--civic-only`, `--long-campaign-only`, `--stage-budget-only`, `--staged-boundary-only`, `--smooth-heavy-yield-only`, `--parallel-determinism-only`, `--campaign-isolation-only`, `--perf-determinism-only`, `--performance-regressions-only`, `--living-world-fingerprint-only`, `--late-game-forensics-only`, `--code-health-only`, `--no-competitor-names-only`).
- **Реплей 2053 года (`30` дней)**: `REPLAY_OK`, `REPLAY_FINGERPRINT 4633C342D5EF3E26`, `steady 306,5 мс/день`, `0` нарушений инвариантов (`0 over 33 ms` на плавном 14-дневном прогоне).
- **Детерминизм `MT == ST` (`BDOF_NO_PARALLEL=1`)**: побитовое совпадение на сиде `42` (d30 `15104860604994440336`, d60 `7898756452184986271`, d90 `4906584741490367284`) и сиде `1` (d30 `5859470584432058967`).
- **5-летний соук (`SimBench 1825 42 --soak`)**: **`Soak PASS: 1825/1825 days, 0 alert(s), 0 invariant failure(s)`** (`GDP=141 746B`, `wars=20`, `170,6 мс/день`).
- **Безопасность**: `UNGUARDED_GCSETTINGS=0`, `0` API-ссылок Godot в `Scripts/Simulation/`.

---

## 6. Дополнение — Волна M6 (22 ноября 2026, `#619`–`#637`): фоновый поток симуляции `DoF-Sim`, 1000-летнее масштабирование `O(1)`, скульптурный 3D-рельеф ПК, «Книга войны» и Dual Delivery v9 (`0.8.3.9`)

### 6.1. Двойная релизная поставка M6 (`#636`)
- **Android Release APK v9 (`0.8.3.9`, Dual-ABI `arm64-v8a` + `x86_64`)**:
  - `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-22.apk` (+ `.sha256`), коммит оболочки `731bb11` (`0` remotes);
  - Размер: **`445 205 613` Б (`424,58 МБ`)**, SHA-256 `1EFCBB5C8A01F5E20ED56C5C21FDAB645EF4B8CEBA0605F6CEE559EB49C4FF01` (`SHA256_MATCH=True`);
  - `assets/android_game_fs.zip`: `69 425 918` Б (`66,21 МБ`, `1 692` файла, `STORED` метод `0`), 5 предзапечённых кэшей (`terrainSlots=true`, `sprite_index_v1.json`), `APKSIGNER_EXIT=0`, `VERIFY_STORED failures=0`, `PDBS=0`, `APPLICATION_DEBUGGABLE_COUNT=0`, `UNGUARDED_GCSETTINGS=0`.
- **ПК-релиз M6 (`EntertAlpha 0.8.3.5 2026-11-22`)**:
  - `Tools/build.ps1` (`Godot 4.8-beta3`): **`0` предупреждений / `0` ошибок**, **`EXPORT_VERIFY failures=0`**, чистый выход `code=0`;
  - Архив: `C:\Билды DoF (DiskC)\Deal or Force EntertAlpha 0.8.3.5 2026-11-22.zip` (+ `.sha256`), **`681 742 693` Б (`650,16 МБ`)**, SHA-256 `93F6A763A82801C3345130C06B2AE020069BCB4228F3C3415D212829583177A9` (`SHA256_MATCH=True`).

### 6.2. Фоновый поток симуляции, многопоточность и 1000-летняя устойчивость (`#635`, `#637`)
- **Выделенный поток `DoF-Sim`**: `BackgroundSimRunner` + `CampaignRenderSnapshot` + неблокирующее чтение `GlobePresentation` `TryEnterRead` в 36 файлах графики.
- **Ускорение горячих систем**: многопоточный `EconomyCore` (`−43%`), грязный битсет `MovementRules` (`−42%` на сейве 2053), параллельный батч `AiStatecraft`, `DefconSystem` (`−54%`).
- **Измеренные прогоны**:
  - 30-дневный реплей сейва 2053 года (`campaign_20261122_005807.bdfsave`): **`REPLAY_OK`** (`CEC346DFE40B5C36`), установившийся темп ускорен с **`306,5` до `181,1 мс/день` (`×1,7`)** (`total 6 079 мс`);
  - 20-летнее масштабирование: **`Год 1: 116,3 → Год 20: 80,1 мс/день`**; 1000-летний потолок `PopulationFolding`, укрепление `PublicImages`, `RelationDormancy`, `Organizations` и `Recruitment`;
  - **`28/28` Release-наборов `FormableTests` PASS (`0` FAIL)**, `audit-localization.ps1` **`29 315` RU (`0` проблем)**, `MT == ST` побитово, 5-летний соук `1825/1825` PASS (`0` тревог/нарушений).

### 6.3. Полировка UI и скульптурный 3D-рельеф ПК (`#637`)
- Исправлена «призрачная плашка» `CampaignFlagMount`, очищены отступы `SafeArea` в `ShellXpStripView` и `FullWorkspace`, в шапку «Книги войны» (`DoctrineScreenUI` / `DoctrineComfortUI`) добавлена кнопка закрытия `×`, обновлены `StartupIntro` и мобильная локализация `98_mobile_ux.json`.
- На десктопе усилен скульптурный 3D-рельеф суши (`0.105f` + сетка `768×384` с клиренсом городов и рек).
