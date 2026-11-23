# Проход 21 ноября 2026: ясность UI и Observer Mode, ускорение симуляции ×1,88, AAA-графика и Mobile/GLES3, межэпохальный моддинг, мегапроекты и фундамент Android

> **Основа и границы прохода.** Ветка `EntertAlpha`, базовый коммит `e11274cb` (базовый замер `SimBench 365 42` зафиксирован до правок в `#190`). Рабочее дерево объединяет 7 согласованных направлений с единым сериализованным гейтом сборки и тестов (`verifier-2`, `#249`). Открытый редактор Godot владельца (`PID 1924`) не затрагивался; на момент записи этого документа изменения находятся в локальном рабочем дереве (без `git commit`/`git push`), а полный прогон всего стенда `FormableTests` без фильтров и живой `ExportVerification` на экспортированном EXE запущены следующим шагом верификации (`#250`).

---

## 1. Сводка результатов по 7 направлениям

| # | Направление | Главный итог | Ключевое доказательство (`#249`) |
|---|---|---|---|
| 1 | **Ясность UI и режим наблюдателя** | Полоса военного опыта (`Army`/`Air`/`Navy XP`) под верхней панелью, 4-слойная кликабельная вкладка дипломатии страны без «пустоты», починка кнопки Observer Mode в меню паузы, мгновенное переключение страны в Observer Mode на том же кадре даже на паузе | `DealOrForce.csproj` 0 ошибок; 5/5 UI-наборов (`--ui-clarity-only`, `--new-shell-rest-only`, `--shell-coherence-only`, `--presentation-diplomacy-only`, `--code-health-only`); `audit-localization.ps1 -Fail` PASS (`27 883` RU, 0 ошибок) |
| 2 | **Производительность (Фазы 1 + 2) и рефакторинг** | Ускорение годового прогона **`131,57 → 69,87 мс/день` (−46,9%, ×1,88)**; худший шаг **`713,8 → 280,2 мс` (−60,7%)**; пауза GC **`175,0 → 32,4 мс` (−81,5%)**; плавный прогон `--smooth 60 6.0` **`>33 мс: 2 → 0`**; модульное разбиение `SimulationCore.cs` (`2 959 → 728` строк) и `LongRangeMovement.cs` | Release `SimBench 365 42`; 1-поток (`BDOF_NO_PARALLEL=1`) == N-потоков побитово на d30/d60/d90 (d30 совпадает с `#190`: `6296889915955535350`); 365d `--soak` PASS (`59,9 мс/день` к d365); `--perf-determinism-only` PASS |
| 3 | **AAA-графика карты и Mobile/GLES3** | Убрано пересвеченное пятно блика океана, откалибрована батиметрия и чернильный берег, добавлены рельеф `ridge_convexity` + микро-топография, виньетка границ уровня Victoria 3, единый `quality_tier` (`0/1/2`) при сохранении жёсткого лимита **12 сэмплеров GLES3** (0 новых сэмплеров) | 9/9 графических и контрактных наборов (`--gpu-atlas-contract-only`, `--map-look-only`, `--border-rank-only`, `--frontier-field-only`, `--map-labels-only`, `--weather-only`, `--flags-only`, `--icons-only`, `--code-health-only`) |
| 4 | **Межэпохальный моддинг и полные конверсии** | Декларативный `scenario.json`, отделение отображаемого календаря от внутреннего `GameDate`, 3-состоянные `era_features`, VFS `replace_paths`/`exclude_paths`, чисто-C# `ModValidator`, 2 готовых шаблона (`historical_1914_era`, `chronicles_of_aethelgard`) и русскоязычный гайд | `--scenario-modding-only` 100% PASS (инвариантность 2024, 32d/35d soak, save round-trip, дуальный детерминизм, восстановление 2024, негативный тест `ModValidator`); `--mod-frontend-only`, `--yaml-only` PASS |
| 5 | **Геймплей: стратегические мегапроекты и консорциумы** | 3-этапные региональные мегапроекты (10 шт. в `game/common/grand_projects/95_grand_projects.json`), реальная экономика 63 товаров, софинансирование союзников/IO, наследование при захвате, диверсии через разведку, 6 `ICampaignCommand`, полный UI и EN+RU локализация | `--gameplay-content-only` 100% PASS, `--integrity-only`, `--campaign-toggles-only`, `audit-localization.ps1 -Fail` PASS, 365d `--soak` PASS (`0` нарушений инвариантов) |
| 6 | **Фундамент порта на Android** | Чисто-C# `PlatformProfile` + детерминированный `TouchGestureMath` + `TouchCameraAdapter` (подключён в `GlobePresentation.cs` с нулевым влиянием на мышь/клавиатуру), `[preset.1]` `Android (Dev / Debug)` в `export_presets.cfg` без изменения Windows-пресета, `android-doctor.ps1`, `build-android.ps1` и план порта | `--android-platform-only` 100% PASS, `--code-health-only` PASS; оба `.ps1` прошли синтаксис и `-DryRun` в `pwsh` и `powershell.exe 5.1` |
| 7 | **Исследование и матрица решений (Decision-1)** | 7-столповой обзор (Perplexity Sonar Pro Search + GPT 6.2 Astra + `#192` + `deal-or-force-knowledge`) и калиброванная оценка 29 предложений моделью `google/gemini-decision-1-20261111`; ранние ориентиры выданы всем 6 командам (`#193`–`#198`) | `docs/RESEARCH_2026_11_21_RU.md` (42,3 КБ), `docs/IDEAS_DECISION_MATRIX_2026_11_21_RU.md` (18,0 КБ) |

---

## 2. Ясность UI и починка Observer Mode (`#236`)

### Что сделано
1. **Верхняя полоса военного опыта и бейдж наблюдателя**:
   - Новый [`Scripts/ui/Shell/ShellXpStripView.cs`](../Scripts/ui/Shell/ShellXpStripView.cs) (498 строк) и разметка в [`game/gui/90_shell.json`](../game/gui/90_shell.json) (`shell.xp_strip`, `shell.compact.xp_strip`): встроенная полоса под `ShellTopPlate` (строго выше `LeftPanel` на 1920×1080 и 1280×720). Показывает **Army XP**, **Air XP** и **Navy XP** с иконками, табличными цифрами `PresentationFonts.Tabular()`, дневным приростом (`+X/d`), полосой прогресса до следующей статьи доктрины, 3-уровневым тултипом и переходом по клику к Доктринам/Армии/Поддержке.
   - Рядом добавлен 3-состоянный бейдж `ShellObserverBadge` (`OBSERVER · WORLD` / `OBSERVER · <Country>`) с ЛКМ в меню паузы и ПКМ к обзору мира (`F4`).
   - В [`Scripts/ui/Shell/ShellCornerView.cs`](../Scripts/ui/Shell/ShellCornerView.cs) (699 строк) кэш `_resourceReadout` привязан к `(player.Id, observerMode)`, добавлен тултип флага и расширены подсказки ресурсов (`StabilityDrivers`, `InflationDrivers`, `PoliticalPointsPerWeek`, `ReinforcementCapacity`).
2. **Полная вкладка дипломатии в досье страны**:
   - Новые [`Scripts/ui/Shell/ShellCountryDiplomacyView.cs`](../Scripts/ui/Shell/ShellCountryDiplomacyView.cs) (731 строка) и [`Scripts/ui/Shell/ShellCountryDiplomacyActions.cs`](../Scripts/ui/Shell/ShellCountryDiplomacyActions.cs) (271 строка) заменили пустой блок четырьмя читаемыми слоями (без мутации симуляции):
     - *Двусторонняя сводка*: стойка, знаковое мнение + цветная шкала `-100..+100`, бейджи статусов (субъект/сюзерен, дни перемирия, торговля/ПН, гарантии, проход, эмбарго, посланник, ядерное сдерживание, демографический спад), разбивка модификаторов мнения, сравнение ВВП/армии/населения.
     - *Активные войны и противостояния*: карточки войн с флагами, шкалой счёта `%` и кликом в экран войны, плюс чипы противостояний.
     - *Кликабельное положение в мире* (пустые категории скрыты): организации/блоки, сюзерен, субъекты, союзники, соперники, перемирия с остатком дней, торговые/ПН-пакты, гарантии (выданные/полученные), проход войск, эмбарго, покровители/прокси и число претензий.
     - *Прямые действия* в 2 колонки через существующие `ICampaignCommand` с ценой, согласием и причиной отказа в тултипе.
3. **Починка кнопки Observer Mode в меню паузы**:
   - В [`Scripts/ui/ObserverModeUI.cs`](../Scripts/ui/ObserverModeUI.cs) синхронизированы `_observerMode` и `_campaign.ObserverMode`, сохраняется `_observerReturnNationId` для чистого возврата к своей стране, при входе/выходе блокируется `SyncObserverCommandGate()` и вызывается `RefreshNewShell(force: true)`.
   - В [`Scripts/ui/PauseMenuUI.cs`](../Scripts/ui/PauseMenuUI.cs) подключена именованная кнопка `PauseToggleObserver` с динамическими подписями (`Observer mode [F7]` / `Leave observer mode [F7]` / `Take control of {0} [F7]`) и `SyncNewShellModalState()` при закрытии.
4. **Мгновенное переключение страны в Observer Mode на паузе**:
   - В [`Scripts/ui/Shell/ShellOrchestrator.cs`](../Scripts/ui/Shell/ShellOrchestrator.cs), `ShellDatePlateView.cs`, `ShellAlertColumnView.cs`, `ShellInboxBarView.cs`, `ShellStripView.cs`, `ShellWorkPanelView.cs` и `ShellCountryDossierView.cs` (568 строк) кэши и перерисовка привязаны к `(world.Player?.Id, observerMode, observedId, world.WarRevision)` — клик по стране в Observer Mode обновляет верхний бар, флаг, ранг, XP, дату, тревоги, нижнюю ленту и досье **на том же кадре даже на паузе**.
5. **Локализация, интеграция и проверки**:
   - Новые [`game/localization/english/95_ui_clarity.json`](../game/localization/english/95_ui_clarity.json) и [`game/localization/russian/95_ui_clarity.json`](../game/localization/russian/95_ui_clarity.json) (`77` ключей EN/RU, 0 конфликтов).
   - Подключён экран `GrandProjectsView` в `ShellContext.cs`, `ShellOrchestrator.cs`, `NewShellUI.cs`, `ShellEconomyWorkspaceView.cs`.
   - Все файлы в `Scripts/ui/Shell/` строго `< 800` строк; 0 сырых `Color`-литералов. Добавлен [`Tools/FormableTests/UiClarityTests.cs`](../Tools/FormableTests/UiClarityTests.cs) (`--ui-clarity-only`) и проверка `VerifyUiClarityAndObserverMode` в [`Tools/ExportVerification/NewShellExportVerification.cs`](../Tools/ExportVerification/NewShellExportVerification.cs).

---

## 3. Производительность (Фазы 1 + 2), многопоточность и рефакторинг (`#190` → `#249`)

### Сравнительная таблица замеров (`Release SimBench 365 42`, сид `42`)

| Метрика | База (`#190`, `e11274cb`) | Фаза 1 (`#224`) | **Фаза 2 (`#249`)** | **Итоговый эффект** |
|---|---:|---:|---:|---:|
| Время стены (`365` дней) | `48 024 мс` (`131,57 мс/д`) | `38 715 мс` (`106,07 мс/д`) | **`25 502 мс` (`69,87 мс/д`)** | **−46,9% (×1,88 быстрее)** |
| CPU потока симуляции | `121,4 мс/д` | `98,5 мс/д` | **`68,0 мс/д`** | **−44,0% (×1,79 быстрее)** |
| Шаг: медиана / p95 / p99 | `1,39` / `10,69` / `89,78 мс` | `0,97` / `8,23` / `80,29 мс` | **`0,58` / `5,35` / `59,23 мс`** | **−58% / −50% / −34%** |
| **Худший шаг / 12-шаговый кадр** | `713,8` / `742,7 мс` | `718,2` / `747,2 мс` | **`280,2` / `288,9 мс`** | **−60,7% / −61,1% (×2,55 ниже)** |
| `--smooth 60 6.0` мед / p99 / worst | `1,93` / `12,15` / `40,3 мс` | `0,63` / `10,45` / `240,1 мс` | **`0,64` / `9,62` / `20,9 мс`** | **worst −48,1%; `>33 мс`: `2 → 0`** |
| `--smooth 60 6.0` кадров `>16` / `>33 мс` | `14` / `2` | `11` / `5` | **`2` / `0`** | **0 просадок ниже 30 FPS** |
| Аллокации / пауза GC | `4 882 кБ/д` / `175,0 мс` | `3 885 кБ/д` / `39,2 мс` | **`3 884 кБ/д` / `32,4 мс`** | **−20,4% аллок. / −81,5% пауза GC** |
| Резидентная RAM (RSS) | `1 100,3 МБ` | `932,4 МБ` | **`933,6 МБ`** | **−15,2%** |

### Ускорение крупнейших горячих систем (`#190` → `#249`)
- `step:orders`: `2 694 мс (156,8 МБ)` → **`502 мс (0,4 МБ)`** (**×5,37 быстрее, −99,7% аллокаций**)
- `war emergency`: `781 мс (108,1 МБ)` → **`279 мс (5,6 МБ)`** (**−64,3% CPU, −94,8% аллокаций**)
- `intel.visibility`: `2 674 мс` → **`1 000 мс`** (**−62,6%**)
- `support`: `1 685 мс (118,4 МБ)` → **`679 мс (50,7 МБ)`** (**−59,7% CPU, −57,2% аллокаций**)
- `military`: `3 169 мс` → **`1 295 мс`** (**−59,1%**)
- `war:advances`: `4 873 мс` → **`2 126 мс`** (**−56,4%**)
- `statecraft`: `4 957 мс` → **`2 849 мс`** (**−42,5%**)
- `movements`: `2 007 → 1 223 мс`, `economy`: `1 283 → 747 мс`, `demography`: `754 → 363 мс`.

### Архитектурные изменения и рефакторинг
- Новая инфраструктура [`Scripts/Simulation/Perf/`](../Scripts/Simulation/Perf/): `DenseLookupTables.cs` (`DenseIntTable<T>`, `GenerationalStampSet` с `O(1)` сбросом, `ScratchFloatMap`), `ParallelExecution.cs` + `DeterministicParallel.cs` (`MaxThreadsOverride`, батчи `ForRange`/`SlotsRange` с каноническим последовательным коммитом), `ProtectorIndex.cs` (`volatile`-снимок союзов и гарантий с ключом `(AllianceRevision, Relations.Count)`, убравший перебор 25k отношений в `Statecraft`/`AiJudgement`/`AiStatecraft`/`WorldWar`), `SupportStepCache.cs`, `SimProfiler.cs`.
- Разделение god-файлов: `SimulationCore.cs` (`2 959 → 728` строк) разбит на `SimulationCore.cs`, `Campaign.Indexes.cs`, `Campaign.TickStages.cs`, `Campaign.Seats.cs` и `Perf/SimProfiler.cs`; `LongRangeMovement.cs` (`2 255 → 1 611` строк) разделён с `LongRangeMovement.Reposition.cs`.
- Снятие фриза на дне 321: пошаговый `WorldWar.DeclareStages` с `yield return 0` между вступлениями блоков.
- Быстрые пути: пограничный BFS в `ProvinceVision.cs`, обход `ProvincesHeldBy` в `AirReconnaissance.cs`, `DeterministicParallel.ForRange` + `ArrayPool<float>` в `DomesticCrises.cs`, мирные fast-path в `MilitaryCore.cs`, быстрый сферический фильтр широты (`111 км/град`) в `SupportCore.cs`, общий `CasusBelli` в `AiJudgement.cs`, zero-alloc `FrameRateGovernor`.
- **Детерминизм (`#249`, сид `42`, `BDOF_NO_PARALLEL=1` против многопоточного прогона)**:
  - День 30: `6296889915955535350 == 6296889915955535350` (побитово равно базе `#190`);
  - День 60: `14423045206935645985 == 14423045206935645985`;
  - День 90: `6850272479998729078 == 6850272479998729078`.
- **365-дневный `--soak`**: `Soak PASS: 365/365 days, 1 alert [предсуществующий CDC->ADF], 0 invariant failures`, ВВП `88 452B → 92 010B`, радикализм `1,0%`, темп к дню 365 — **`59,9 мс/день`**. Добавлен [`Tools/FormableTests/PerfDeterminismTests.cs`](../Tools/FormableTests/PerfDeterminismTests.cs).

---

## 4. AAA-графика карты и совместимость с Mobile/GLES3 (`#231`)

- **[`game/shaders/globe_map.gdshader`](../game/shaders/globe_map.gdshader)** (0 новых сэмплеров, сохранён лимит **12 упакованных текстур GLES3**):
  - *Океаны и анти-засветка*: убран многократно сложенный белый блик и неоновый бирюзовый шельф (проблема на Корейском проливе и Арафурском море), расширены градиенты батиметрии, перекалибровано поглощение Бэра–Ламберта, физический `F0 = 0.02` (`SPECULAR = 0.25`), добавлена чёткая узкая «чернильная» береговая кромка (`coast_drop`).
  - *Скульптурный рельеф*: добавлена макро-выпуклость хребтов `ridge_convexity` через `textureLod(relief_height, ..., 2.5)` без доп. сэмплеров, микро-топография по яркости (`dFdx`/`dFdy`), прохладное заполнение теней долин и тёплая подсветка горных гряд.
  - *Границы и фронты*: чистая хроматическая внутренняя виньетка границ (без грязно-коричневого затемнения), резкая золотая обводка выделения и тёмная подложка под анимированной линией фронта.
  - *Уровни качества (`uniform float quality_tier = 0.0`)*: `0 = DesktopHigh`, `1 = MobileMedium`, `2 = MobileLow` (отключает тяжёлые 4× микро-выборки и мультиоктавные волны на мобильных GPU). Без `textureQueryLod` и `dFdxFine`/`Coarse`.
- **[`Scripts/gfx/GlobeAtmosphere.cs`](../Scripts/gfx/GlobeAtmosphere.cs), [`Scripts/gfx/GlobeWeather.cs`](../Scripts/gfx/GlobeWeather.cs), [`Scripts/gfx/CityMiniatureRenderer.cs`](../Scripts/gfx/CityMiniatureRenderer.cs), [`game/gfx/interface/shaders/menu_globe.gdshader`](../game/gfx/interface/shaders/menu_globe.gdshader) & `menu_globe_air.gdshader`**: резкий горизонт Рэлея + тёплый сумеречный терминатор, масштабирование октав облаков (`5 → 3 → 2`) и тумана (`4 → 2`) по `quality_tier`, убраны аллокации при выключенной погоде, доработано сумеречное свечение городов.
- **[`Scripts/gfx/GlobePlatformHooks.cs`](../Scripts/gfx/GlobePlatformHooks.cs) и [`Scripts/gfx/GlobePresentation.cs`](../Scripts/gfx/GlobePresentation.cs)**: проброс `PlatformProfile.Current.GraphicsQualityTier` в шейдеры карты/погоды/атмосферы с 0 аллокаций на кадр и 2-строчное подключение `TouchCameraAdapter` с полным сохранением десктопного управления.

---

## 5. Межэпохальный моддинг, `ModValidator` и эталонные шаблоны (`#245`)

- **Новое чисто-C# ядро [`Scripts/Simulation/Modding/`](../Scripts/Simulation/Modding/)**:
  - `CalendarProfile.cs` — отвязывает отображаемый календарь (`era_name`, `era_short`, `year_offset`, 12 локализуемых названий месяцев, шаблоны `format`/`year_format`) от внутреннего детерминированного `GameDate`/`ElapsedHours`.
  - `ScenarioManifest.cs` — декларативный `scenario.json` (JSON/YAML): старт с жёсткой проверкой дат (отклоняет 30 февраля и год 0), `base_world`, `clear_base_wars`/`diplomacy`, `total_conversion`, `replace_paths`/`exclude_paths`, стартовые войны/союзы/перемирия, 3-состоянные `era_features` (`available`/`restricted`/`unavailable`) с алиасами (`nukes`, `missiles`, `drones`, `modern_air_force`, `cyber`), `skin`/`theme`/`music`.
  - `ModOverlayPolicy.cs` — VFS-слои с белым списком корней `AllowedReplaceRoots`, защитой от path-traversal, отчётом владения (`ModProvenanceEntry`) и 1-булевым fast-path (0 оверхеда в базе).
  - `ScenarioRulesApplier.cs` и `ModValidator.cs` — предполётная проверка схемы, синтаксиса, дат, циклов зависимостей и пререквизитов технологий, конфликтов `replace_paths`, тегов стран, ссылок на отключённые `era_features` и паритета EN/RU-локализации с точными подсказками `SuggestedFix`.
- **Нулевой дрейф базы 2024**: точечные хуки в `ModLoader.cs`, `GameDataLoader.cs`, `DecisionCore.cs`, `ScenarioLibrary.cs`, `SimulationCore.cs`, `SaveSystem.cs` (`WhenWritingNull`), `CampaignFingerprint.World.cs` (0 байт в базе 2024).
- **Два проверенных шаблона и гайд**:
  - [`mods/_templates/historical_1914_era/`](../mods/_templates/historical_1914_era/) (Июльский кризис `1914-07-28`, `base_world: true`, без ядерного оружия/дронов/ракет, Антанта и Центральные державы, 6 технологий, решения, события, EN+RU);
  - [`mods/_templates/chronicles_of_aethelgard/`](../mods/_templates/chronicles_of_aethelgard/) (фэнтези-конверсия «Год 402 Третьего Солнца», 12 авторских месяцев EN/RU, `total_conversion: true`, `replace_paths`, 3 государства `VAL`/`IRN`/`SYL`, свои товары/идеологии/технологии/события);
  - Обновлён [`mods/_templates/README.md`](../mods/_templates/README.md), написан [`docs/MODDING_SCENARIOS_GUIDE_RU.md`](MODDING_SCENARIOS_GUIDE_RU.md), добавлен [`Tools/FormableTests/ScenarioModdingTests.cs`](../Tools/FormableTests/ScenarioModdingTests.cs).

---

## 6. Геймплей: стратегические мегапроекты и международные консорциумы (`#242`)

Выбрано единогласно (`Decision-1`: 93% локального выбора + 61% `#197`, соответствие существующим циклам `p=0.980`):
- **Симуляция (чистый C#, 0 Godot)**: [`Scripts/Simulation/GrandProjectsCatalog.cs`](../Scripts/Simulation/GrandProjectsCatalog.cs), [`Scripts/Simulation/GrandProjects.cs`](../Scripts/Simulation/GrandProjects.cs), [`Scripts/Simulation/GrandProjectsCommands.cs`](../Scripts/Simulation/GrandProjectsCommands.cs) (`6` сетевых `ICampaignCommand`: `Start`, `AdvanceStage`, `TogglePause`, `Cancel`, `JoinConsortium`, `Sabotage`).
  - 3 этапа (`Изыскания → Строительство → Ввод в строй`), масштабируемая от ВВП стоимость в казне, политкапитале и военном опыте, реальная обеспеченность товарами из 63-товарного рынка (`MarketQuote.Satisfaction`), совместное софинансирование союзниками/IO (`-30%` цены хозяину, `+25%` скорости, мнения в `OpinionSystem`, постоянные дивиденды партнёру с мгновенной приостановкой при войне/эмбарго), остановка при военном уроне, наследование при захвате региона, диверсии через `IntelNetworks`, распределённый ИИ (`nation.Id % 30`), сохранение в `BDF3`, переключатель `grand_projects` в `CampaignToggles` и подмешивание в `CampaignFingerprint`.
- **Данные и локализация**: [`game/common/grand_projects/95_grand_projects.json`](../game/common/grand_projects/95_grand_projects.json) (10 мегапроектов: Трансконтинентальная ВСМ, Термоядерная сеть HVDC, Глубоководный мегапорт, Суверенная фабрика полупроводников, Космодром, Континентальный щит ПРО, Научная цитадель, Суверенный резервный фонд, Тоннель через пролив, Послевоенное обновление мегаполисов), сопутствующие `defines`/`settings`/`opinion_modifiers`/`news` (`95_grand_projects.json`) и [`game/localization/{english,russian}/96_gameplay_content.json`](../game/localization/russian/96_gameplay_content.json).
- **Интерфейс и тесты**: [`Scripts/ui/GrandProjectsView.cs`](../Scripts/ui/GrandProjectsView.cs) (3 вкладки, read-only в Observer Mode, явные причины отказа), [`Tools/FormableTests/GameplayContentTests.cs`](../Tools/FormableTests/GameplayContentTests.cs).

---

## 7. Фундамент порта на Android (`#241`)

- **Чисто-C# платформенное ядро и жесты**:
  - [`Scripts/Platform/PlatformProfile.cs`](../Scripts/Platform/PlatformProfile.cs): классификация `Desktop`/`AndroidPhone`/`AndroidTablet`, безопасные отступы вырезов `SafeAreaInsets`, `UiScaleFactor`, цели нажатия `48 dp` / `44 dp` / `32 px`, уровни графики `PlatformGraphicsTier` и флаги `IsSteamSupported` / `IsDesktopFileDialogSupported` / `IsExternalWorkerModSupported`.
  - [`Scripts/Platform/GodotPlatformBridge.cs`](../Scripts/Platform/GodotPlatformBridge.cs) и [`Scripts/Platform/TouchGestureMath.cs`](../Scripts/Platform/TouchGestureMath.cs) (1-пальцевый пан с deadzone и инерцией, 2-пальцевый pinch-to-zoom вокруг средней точки, поворот и наклон 3D-глобуса, `long-press` ~420 мс как ПКМ, фильтрация 3-го пальца, явный конструктор по умолчанию `TouchGestureConfig()`).
  - [`Scripts/Input/TouchCameraAdapter.cs`](../Scripts/Input/TouchCameraAdapter.cs) (пространство имён `DealOrForce.Platform`, чтобы не перекрывать `Godot.Input`; возвращает `false` для всех событий мыши/клавиатуры).
- **Экспорт и диагностика**:
  - [`export_presets.cfg`](../export_presets.cfg): добавлен `[preset.1]` `Android (Dev / Debug)` (`arm64-v8a`, Landscape, Immersive, `ETC2/ASTC`, `minSdk=24`, `targetSdk=35`, исключены десктопные/редакторские аддоны и DLL); `[preset.0]` (`Windows Desktop`) сохранён побайтово без изменений.
  - [`Tools/android-doctor.ps1`](../Tools/android-doctor.ps1) и [`Tools/build-android.ps1`](../Tools/build-android.ps1): проверены на машине — OpenJDK 17, шаблоны Godot `4.8.beta3.mono` и `.NET 10.0.404` уже готовы (`PASS`); для сборки живого APK на этой машине осталось доустановить только Android SDK (`platform-tools`, `build-tools;35.0.1`, `platforms;android-35`) и `dotnet workload install android`.
  - План порта зафиксирован в [`docs/ANDROID_PORT_PLAN_RU.md`](ANDROID_PORT_PLAN_RU.md), юнит-тесты — в [`Tools/FormableTests/AndroidPlatformTests.cs`](../Tools/FormableTests/AndroidPlatformTests.cs).

---

## 8. Исследование и матрица приоритетов (`#202`)

- Написаны [`docs/RESEARCH_2026_11_21_RU.md`](RESEARCH_2026_11_21_RU.md) и [`docs/IDEAS_DECISION_MATRIX_2026_11_21_RU.md`](IDEAS_DECISION_MATRIX_2026_11_21_RU.md).
- По калиброванной оценке `google/gemini-decision-1-20261111` (29 предложений, 3 пакета, `$0.00122`):
  - Главный рычаг удержания и конверсии — **Столп A (Ясность UI — 38%)**;
  - Главный вектор производительности — **`zero_alloc_plus_spikes` (96%)** (подтверждено фактическим ускорением ×1,88);
  - Отсечены 4 анти-паттерна: Rust FFI на мелких игровых циклах (`p=0.043`), Forward+-only тяжёлый постпроцесс (`p=0.012`), агрессивный `PublishTrimmed`/AOT до первого рабочего APK (`p=0.029`) и ранний Steam Next Fest без вертикального среза (`p=0.010`);
  - На следующую волну геймплея зафиксированы **Карта уязвимостей цепочек поставок** (приоритет независимого разбора `GPT 6.2 Astra`), **Обязывающие пакетные соглашения → Глобальные саммиты** и **Ветераны как политическая сила (`POL-01`)**.

---

## 9. Проверенные гейты и честные границы охвата

### Проверено и прошло (`#249`)
- **Сборка**: `DealOrForce.csproj`, `Tools/FormableTests/FormableTests.csproj`, Release `Tools/SimBench/SimBench.csproj` — **0 ошибок**.
- **Статические инварианты**: 0 ссылок на Godot в `Scripts/Simulation/`, 0 отсутствующих/дублированных `.cs.uid`, 0 статических полей `Campaign`, 0 сырых `Color`-литералов в новом UI.
- **Локализация**: `Tools/audit-localization.ps1 -Fail` — **PASS** (`27 883` RU-записей в `424` файлах, `0` конфликтов, `0` расхождений плейсхолдеров, `0` пустых значений).
- **Интегрированные наборы `FormableTests` (18/18 PASS в финальном прогоне + 9/9 на неизменённых срезах)**: `--code-health-only`, `--perf-determinism-only`, `--parallel-determinism-only`, `--smooth-heavy-yield-only`, `--performance-regressions-only`, `--integrity-only`, `--diplomatic-symmetry-only`, `--alliances-only`, `--deals-only`, `--android-platform-only`, `--ui-clarity-only`, `--scenario-modding-only`, `--gameplay-content-only`, `--gpu-atlas-contract-only`, `--map-look-only`, `--border-rank-only`, `--frontier-field-only`, `--weather-only`.
- **Бенчмарк, детерминизм и 365d soak**: `SimBench 365 42` (`131,57 → 69,87 мс/день`, worst `713,8 → 280,2 мс`, `--smooth 60 6.0` `>33 мс: 2 → 0`, GC `-81,5%`), побитовый паритет 1-vs-N потоков на d30/d60/d90 и `Soak PASS` на 365 днях.

### Что ещё в процессе / не закрыто этим документом
- Живой замер позднего сейва `bozhe.bdfsave` (день 39 174) и 1000-летней кампании, а также сборка реального Android APK на устройстве (после доустановки Android SDK и workload `android`) остаются в следующих этапах приёмки.
- Изменения ещё не зафиксированы коммитом и не опубликованы в ветку `EntertAlpha`.

---

## 10. Дополнение по Волне 2, живому экспорту и аудиту гигиены/легаси (`#295`, `#303`, `#307`, `#308`)

### 10.1. Верификация Волны 2 и живого экспорта (`#303`, `#305`, `#308`)
- **Экспорт `build/DealOrForce_20261121_mega` (`Tools/build.ps1`, Godot `4.8.beta3.mono`)**: **PASS** (`807,1 МБ`, **`EXPORT_VERIFY failures=0`**, 0 ошибок шейдеров/скриптов).
- **Оконный GPU-прогон на русском (`--verify-build --verify-language russian`)**: **PASS** (`exit=0`, `EXPORT_VERIFY failures=0`, **56/56 PNG** в `tmp_playtest/export_20261121_wave2_ru/`), подтверждены верхняя полоса XP, вкладка дипломатии страны (64 компактных флага без горизонтального переполнения), `PauseToggleObserver` и мгновенное переключение в Observer Mode.
- **Визуальная находка и оперативное исправление (`#305` → `#308`)**: при осмотре живого PNG обнаружено обрезание флагов `38×26` в `ShellCountryDiplomacyStanding.cs` (инициализация `Texture`/`Size` до `ExpandMode = IgnoreSize`); исправлено с добавлением runtime-проверки размеров `FlagImg` в `NewShellExportVerification.cs` и `UiClarityTests.cs`.
- **Ускорение Фазы 3 + геймплей Волны 2 (`#303`)**: `SimBench 365 42` достиг **`58,2 мс/день`** к дню 365 (**×2,26 быстрее базы `#190`**), `step:orders` **×20,1 быстрее**, `--smooth 60 6.0` медиана `0,51 мс` (**0 кадров > 33 мс**), 1-поток == N-потоков побитово (`1771571261352512683`), `audit-localization.ps1 -Fail` **PASS** (`28 107` RU в `426` файлах), `--no-competitor-names-only` **PASS**.
- **Переработка рабочих панелей (`#295`)**: обновлены `ShellWorkPanelView.cs`, досье региона (`ShellRegionVisualsView.cs`, `ShellRegionKpiCardsView.cs`, `ShellRegionBuildTabView.cs`), экономика (`ShellEconomyWorkspaceView.cs`, `ShellEconomyCharts.cs`) и журнал войн (`WarLedgerUI.cs`, `WarLedgerDetailUI.cs`), `97_ui_workspaces.json` (54 ключа), `UiWorkspacesTests.cs`.

### 10.2. Гигиена рабочего дерева и аудит вывода легаси-UI / выноса данных в JSON (`#307`, `perf-refactor`)
- **Безопасная очистка неотслеживаемых артефактов F#**: удалены **138** неотслеживаемых файлов (`1,55 МБ` — `Scripts/Rules/tmp`, `Scripts/Rules/tmp_logs`, пустой `Scripts/LegacySourceMap`); добавлены `.gdignore` в `Scripts/Rules/bin` и `Scripts/Rules/obj` (исключены **73** файла / `1,09 МБ` из сканирования `res://` редактором Godot).
- **Карта вывода устаревшего Classic UI (`Scripts/ui/`, только чтение, ничего не удалялось)**:
  - *Очередь 1 (чистые кандидаты после снятия режима Classic, `~2 190` строк, 0 прямых вызовов из `Scripts/ui/Shell/`)*: `CampaignAlertsUI.cs` (647), `EconomyWorkbenchUI.cs` (444), `RegionBuildListUI.cs` (414), `DatePlateUI.cs` (370), `ResourcePlateUI.cs` (321), `ArmyBarUI.cs` (347, после переноса 1 общего хелпера); после выноса ~5 хелперов — `TopBarHudUI.cs` (801), `StrategicHudUI.cs` (827), `WorldStripUI.cs` (109), итого **`~4 280` строк**.
  - *Очередь 2 (активные модальные экраны, используемые новым HUD — **НЕ удалять**)*: `DiplomacyScreenUI.cs` (1 600), `StateAffairsUI.cs` / `PoliticsUI.cs`, `RegionInteractionUI.cs`, `TradeScreenUI.cs`, `ProductionScreenUI.cs`, `DoctrineTreeUI.cs` / `DoctrineUI.cs`, `ResearchScreenUI.cs`, `WarLedgerUI.cs` / `WarLedgerDetailUI.cs`, `DossierDetailUI.cs`, `SaveLoadUI.cs`, `CountrySelectionUI.cs`.
- **Топ-5 зашитых C#-таблиц для выноса в `game/common/*.json` (усиление моддинга)**:
  1. `TerritorialTopology.cs` (`DefaultRegionRules`, `TransnistriaBoundary`) → `game/common/geography/region_rules.json`
  2. `NationNaming.cs` (`FormPhrases`, `StateFormKeys`) → `game/common/naming/state_forms.json`
  3. `CombatTactics.cs` (`Cumulative`, 75 строк) → `game/common/combat_tactics/*.json`
  4. `DiplomaticActs.cs` (`All`, 20 строк) → `game/common/diplomatic_acts/*.json`
  5. `BudgetCore.cs` (`Slots`, 34 строки) и `MilitaryCore.cs` (`TroopRows2024` fallback).
- **Следующие монолиты симуляции под модульное разбиение**: `AiStatecraft.cs` (`2 334`), `MovementRules.cs` (`2 273`), `EconomyCore.cs` (`1 954`), `CombatCore.cs` (`1 887`), `SaveSystem.cs` (`1 832`), `MilitaryCore.cs` (`1 788`), `LongRangeMovement.cs` (`1 722`), `NationNaming.cs` (`1 614`).

---

## 11. Волна 3 (война/бои/VFX), Android M1–M2 (играбельный APK), маркетинг и глубокое исследование Perplexity (`#326`–`#341`)

### 11.1. Волна 3: переработка боёв, телеметрия, AAA-жетоны и экран боя (`#326`, `#327`, `#331`, `#335`, `#336`)
- **Симуляция (`#331`, `gameplay-content`)**: новый чисто-C# [`Scripts/Simulation/BattleTelemetry.cs`](../Scripts/Simulation/BattleTelemetry.cs) (`BattleTelemetry.Build` — read-only, 0 мутаций, 9 фаз боя, прогресс, импульс, ETA, цветные чипы модификаторов, разделение `Frontline`/`Reserve`/`Retreating`) + 4 боевые механики в `CombatCore.cs` / [`game/common/defines/97_warfare_overhaul.json`](../game/common/defines/97_warfare_overhaul.json) (обход окопов с флангов `18%`/ось до `54%`, общевойсковой бонус пехота+броня/поддержка `+4..10%`, ускоренный развал котлов `1,45×` при истощении, кулдаун реорганизации отступления `>= 3д`) + `99_warfare_overhaul.json` (EN+RU) + `WarfareOverhaulTests.cs` (`--warfare-overhaul-only` 100% PASS, детерминизм `7915499830807823772`).
- **Графика карты (`#326`, `graphics-aaa`)**: обновлены жетоны армий (`BattleMapPresentation.cs`: двойные микро-бары орг./силы, 4-цветная окантовка стойки, тактические пипы котла/снабжения/окопов/движения), 128×128 пузыри боёв с дугой прогресса и дыханием (`SurfaceBattleBadge.cs`), новый [`Scripts/gfx/GlobeBattleVfx.cs`](../Scripts/gfx/GlobeBattleVfx.cs) (батченные красные стрелки штурма и аналитические кольца вспышек без доп. сэмплеров, управляемые `quality_tier`), раздельные чернила приказов в `GlobeOrderRibbons.cs`.
- **Панели боя и армий (`#327`, `ui-workspaces`)**: переработаны `BattleDossierUI.cs` / `BattleFactorsUI.cs` (сплит-шапка сторон, бейдж фазы боя, чипы модификаторов, двойная шкала ширины фронта, карточки фронта/резерва/отступления), `ArmyPanelUI.cs` разбит с новым `ArmyRosterUI.cs` (4-метрическая готовность стека, быстрое слияние HQ+, кликабельный прыжок в бой `⚔`), улучшен `ShellStripView.cs` (выделение группы армий + `+N` бейдж), исправлен кроп флагов `38×26` в `ShellCountryDiplomacyStanding.cs` (`#336`).
- **Верификация Волны 3 (`#335`, `verifier-2`)**: `DealOrForce.csproj` / `FormableTests` / `SimBench` **0 ошибок, 0 новых предупреждений**; `audit-localization.ps1 -Fail` **PASS** (`28 169` RU в `427` файлах); **16/16** наборов `FormableTests` + `ArmyInteractionTests` (`--coalition-combat-only` 38/38, `--assault-readiness-only` 45/45) **PASS**; Release `SimBench 365 42 --soak` **PASS** (`0` нарушений инвариантов); финальный экспорт `build/DealOrForce_20261121_mega/Deal or Force.exe` и 56/56 русских GPU-скринов (`tmp_playtest/export_20261121_wave3_ru`) — **`EXPORT_VERIFY failures=0`**, визуально подтверждены некропнутые флаги дипломатии, верхний XP-бар и боевые маркеры.

### 11.2. Android-порт: вехи M1 (живой запуск на телефоне) и M2 (полный играбельный APK, `#328`, `#339`)
- **M1 (`507d0a0`)**: установлен Android SDK + `.NET android` workload, собран первый диагностический APK (`49,11 МБ`), запущен владельцем на реальном 5,4" смартфоне, найден и устранён баг пустого `AppContext.BaseDirectory` на Android Mono.
- **M2 (`22a3aed` в `D:\Godot Projects\deal-or-force-android\`, 0 правок в основном репо)**: собран, подписан (`apksigner verify` EXIT `0`) и проверен **первый полный играбельный APK кампании 2024**:
  - Артефакт: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk` (**`388,98 МБ`**, `arm64-v8a`, `SHA-256: 7CEE61EF3A52A72E9F7FDD2AB9CB2D9B2FBF0B8943AF93896ED8B50E6E2ECF50`);
  - Внутри: `DealOrForceAndroid.dll` (`11,1 МБ`), `DealOrForce.Rules.dll`, `android_game_fs.zip` (`193,8 МБ` / `6 078` файлов с потоковой распаковкой по 1 МБ на первом запуске в `user://game/`), полный переход `AndroidBoot → MainMenu → 2024 Campaign` на 3D-глобусе с 4-кнопочной панелью тач-режимов (`Выбор` / `Приказ 1×|фикс.` / `Рамка` / `Карта`).

### 11.3. Маркетинг, упаковка витрин и глубокое исследование Perplexity (`#332`, `#334`, `#340`, `#341`)
- [`docs/MARKETING_AND_LAUNCH_PLAYBOOK_RU.md`](MARKETING_AND_LAUNCH_PLAYBOOK_RU.md) (`44+ КБ`) + [`docs/marketing/CAPSULE_PROMPTS_2026_11_21_RU.md`](marketing/CAPSULE_PROMPTS_2026_11_21_RU.md) (защищено `docs/marketing/.gdignore`): оценка 14 маркетинговых инициатив через Decision-1, готовые EN+RU тексты Steam/itch.io, 20 тегов, порядок 10 скриншотов + GIF, 55-сек. сценарий трейлера, 5 вирусных Shorts, шаблоны писем креаторам, живые эталоны 5 инди-стратегий (`#332`), 5 флагманов Paradox (`#341`) и бенчмарки `howtomarketagame.com`.
- [`docs/PERPLEXITY_DEEP_RESEARCH_2026_11_21_RU.md`](PERPLEXITY_DEEP_RESEARCH_2026_11_21_RU.md): 8-сферный синтез (`perplexity/sonar-pro-search` ×8 + `perplexity/sonar-reasoning-pro` ×1 + Decision-1) с готовым планом следующей волны: #1 Военный ИИ (гистерезис, устойчивые фронты, History Logger — `30%`), #2 Лестница вовлечения экономики (`22%`), #3 Двухступенчатые `Dev Corner → Dev Diary` (`20%`), #4 Пакетная дипломатия + `Trust` (`18%`), #5 Android M3 (4 ГБ RAM, Play PAD, RuStore — `9%`).

---

## 12. Закрытие Прохода 2 (Волны M1 + M2 — 22 ноября 2026, `#482`, `#488`, `#497`, `#504`, `#506`, `#507`)

### 12.1. Что закрыто и проверено в Волнах M1 + M2 (`6G` + `6H` + `6I` + `6J` + Android `0.8.3.6` + Store Pack)
- **Сборка и локализация (`#504`)**: Debug `DealOrForce.csproj`, Debug + Release `FormableTests` и `SimBench` — **`0` ошибок**; `Tools/audit-localization.ps1 -Language russian -Fail` — **PASS** (`29 309` записей в `443` файлах, `0` конфликтов, `0` расхождений плейсхолдеров, `0` пустых значений).
- **Все 24/24 наборов `FormableTests` (Release, шаг 3, `0` падений, `#504`)**:
  - `--geopolitics-only` (`780`), `--ui-workspaces-only` (`676`), `--wave5-mechanics-only` (`629`), `--flags-only` (`562`), `--ui-clarity-only` (`552`), `--diseases-disasters-only` (`532`), `--world-situations-only` (`338`), `--mod-studio-only` (`329`), `--rebel-flags-only` (`170`), `--long-campaign-only` (`156`), `--diplomacy-overhaul-only` (`141`), `--bilateral-peace-only` (`117`), `--android-platform-only` (`102`), `--ai-temperament-only` (`97`), `--map-boot-cache-only` (`87`), `--mainland-reclamation-only` (`81`), `--economy-overhaul-only` (`66`), `--perf-determinism-only` (`54`), `--code-health-only` (`50`), `--border-slivers-only` (`29`), `--national-ambitions-only` (`26`), `--late-game-forensics-only` (`22`), `--secession-colours-only` (`13`), `--no-competitor-names-only` (`11`), `--shell-coherence-only` (`9`).
- **Волна 6H (`DiseasesAndDisasters`) и Волна 6I (`ModContentStudio` + `ScenarioMapEditor`, `#489`)**:
  - `DiseasesAndDisasters` интегрированы в `Campaign.TickStages.cs`, `SaveSystem.cs` (`NormalizeAfterLoad`), `CampaignFingerprint.World.cs` и `OpportunityAtlasUI.cs` (карточки региональных кризисов здоровья и стихийных бедствий с командами `HealthResponseCommand` / `DisasterReliefCommand`).
  - Новый [`Scripts/Simulation/ModStudioDocument.cs`](../Scripts/Simulation/ModStudioDocument.cs) (7 шаблонов + валидация `ContentWorkbench.Validate` + автогенерация EN/RU заготовок + `AuditDraft`) и [`Scripts/ui/ModContentStudioUI.cs`](../Scripts/ui/ModContentStudioUI.cs), плюс явный баннер режима, живой ховер-просмотр и карточка аудита в `ScenarioMapEditor.cs` (`--mod-studio-only` `329/329 PASS`).
- **Детерминизм, профилирование и реплей (`#482`, `#497`, `#504`)**:
  - `SimBench 90 42 --fingerprint 90` с активными волнами `6G+6H+6I+6J`: **`MT == ST` побитово идентичны** (`d90 = 12285651321360155053`, `people 7 669 698 768`, `folded 11 758`).
  - `SimBench 120 42 --profile`: плавный бюджет `8 мс` — медиана **`1,17 мс`**, `p99 11,24 мс`, **`0` кадров `>33 мс`**, **`0` пауз GC**; `MainlandReclamation = 0,0 МБ` (было `175,9 МБ/30д`), `RelationDormancy = 41 мс / 0,1 МБ` (было `207 мс / 31,8 МБ`).
  - Read-only 30-дневный реплей позднего сейва 2053 года (`campaign_20261122_005807.bdfsave`): **`REPLAY_OK`**, установившийся темп **`347,3 мс/день`** (`11 204 мс` всего).
  - **Два 20-летних многосидных соука (`7300` дней, сиды `42` и `2026`) — 2/2 PASS** (`7300/7300` дней, `0` тревог, `0` нарушений инвариантов): сид `42` — ВВП `141 168,2B`, средн. `100,3 мс/д`; сид `2026` — ВВП `133 371,5B`, радикализм `5,88%`, средн. `124,5 мс/д`.
- **Dual-ABI Android / эмулятор APK (`versionCode=6`, `0.8.3.6`, `#506`)**:
  - Артефакт: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk`, **`547 376 852` байт (`522,02 МБ`)**, ABI **`arm64-v8a` + `x86_64`** (нативная работа на ПК-эмуляторах Android без ARM-трансляции), `APKSIGNER_EXIT=0`, `UNGUARDED_GCSETTINGS=0`.
  - **SHA-256**: `0C4CE8B977E89BE616966CCC449B5F5DA3A7288C64FBB1890CF647B4939B41B3` (`SHA256_MATCH=True`).
  - Внутри предзапечены **`cache/map_boot_v2.bin`** (`3 187 782` Б) и **`cache/hydrology_v2.bin`** (`2 672 675` Б), 4-поточная инкрементальная распаковка (`.android_pack_manifest`), встроенная в `ShellMapBarView` панель тач-режимов без плавающего оверлея и MIT NuGet-библиотеки (`CommunityToolkit.HighPerformance 8.4.2`, `System.Numerics.Tensors 11.0.0`, `K4os.Compression.LZ4 1.3.8`) для обеих ABI.
- **Пакет материалов для витрин (`docs/marketing/store_ready/`, `#488`)**: 10/10 визуально проверенных PNG-капсул и иконок для Steam, Google Play, RuStore и itch.io + 3 EN/RU документа (тексты витрин с точными счётчиками символов, пресс-кит/аутрич и Дневник разработки №1, `0` упоминаний сторонних игр).

### 12.2. Честные неблокирующие замечания
- В `mods/luau_events` отсутствует `manifest.json` (зафиксировано `Tools/validate-mod.ps1`).
- Живой прогон кадров и тач-ввода `versionCode=6` APK на физическом телефоне и в эмуляторе ожидает быстрой установки пользователем (настольные автотесты покрыли сборку, шейдеры, автомат жестов, вёрстку и Dual-ABI упаковку).

---

## 13. Волна M3 (22 ноября 2026, `#514`–`#548`): кратная оптимизация симуляции и загрузки, чёткий Android-рендер, верхне-правый тач-HUD и Dual-ABI APK `0.8.3.7` (`versionCode=7`)

### 13.1. Сборка, локализация, тесты и побитовый детерминизм (`#540`)
- **Сборка и локализация**: Debug `DealOrForce.csproj`, Release `FormableTests` и Release `SimBench` — **`0` ошибок**; `Tools/audit-localization.ps1 -Language russian -Fail` — **PASS** (`29 309` записей в `443` файлах, `0` конфликтов, `0` расхождений плейсхолдеров, `0` пустых значений).
- **`25/25` Release-наборов `FormableTests` PASS (`0` FAIL)**, включая обновлённые `ShellCoherenceTests` (`34/34`), `AndroidPlatformTests` (`117/117`), `MapBootCacheTests` (`113 PASS` с LZ4-кэшем `political_rasters_v1.bin`) и `BilateralPeaceTests` (адаптирован под `ShellTouchModeBarView.cs`).
- **Побитовый детерминизм `MT == ST` — PASS**:
  - `SimBench 90 42 --fingerprint 90`: `2624531782213017021 == 2624531782213017021`;
  - `SimBench 30 1 --fingerprint 30`: `12027599730665850675 == 12027599730665850675`;
  - 30-дневный read-only реплей сейва 2053 года (`campaign_20261122_005807.bdfsave`): **`REPLAY_OK`** (`A745BF03F5159AA7`; `sup:advance` `−61%` мс и `−90,4%` аллокаций).

### 13.2. Измеренные выигрыши загрузки, симуляции и плавности кадра (`#540`)
- **Старт и загрузка кампании**:
  - Холодное создание: **`2 165 → 1 710 мс`** (новый Brotli-кэш `lakes_boot_v1.bin` `108 052` Б снизил `topology applied` с `1 246,3` до **`928,2 мс`**; тёплое создание **`485 → 343–372 мс`**; ленивый per-language `Loc` — **`7,4 мс`**).
  - Добавлен LZ4-кэш **`political_rasters_v1.bin`** (`4 344 697` Б), устраняющий runtime-упаковку 16,6 млн пикселей; удалён `EnsureDeferredMenuModals()` для подлинно ленивой инициализации модальных окон.
- **Сейв 2053 года (`campaign_20261122_005807.bdfsave`, read-only)**:
  - Доля `support` упала с **`20,6%` до `5,1%`** (`577 → 206 мс`, аллокации `9,7 → 0,1 МБ`, **`−99%`** за счёт локального кэша `CutOffFailed` в `sup:advance` и раннего выхода при `departures == 0`);
  - `defcon system`: `121 → 31 мс` (**`−74%`**); `integrity`: `30 → 11 мс` (**`−63%`**); снят холодный спайк `diseases & disasters` (`6 мс`); хвост `war:negotiate` снижен с `24,6` до **`15,8 мс`**;
  - Дневные аллокации снижены с `6 827` до **`4 970 кБ/день`** (**`−27%`**).
- **Плавность кадра на сейве 2053 года**:
  - Плавный бюджет `8 мс` (30 дней): `p99` **`47,18 → 16,09 мс`**, `worst` **`66,0 → 30,1 мс`** (**`0` кадров `>33 мс`** вместо `29`!);
  - Speed 5 (`--frames 5` на 2053): медиана **`15,12 мс`**, `p99 23,37 мс`, `worst 23,6 мс`, **`0` кадров `>33 мс`**, темп **`7,70 дней/с`**.
  - *Честная оговорка по фону хоста*: на свободном хосте R1 время дня упало `225,81 → 200,03 мс/д` (реплей `347,3 → 198,0 мс/д`), тогда как R2/R3 шли под ~1,7× более высокой фоновой нагрузкой рабочего стола (контроль `military` `67 → 110–120 мс`), дав сырые `291,66 мс/д`; реальный прогресс подтверждается долями подсистем, аллокациями и перцентилями кадра.

### 13.3. Верхне-правый тач-HUD, чёткость Android и Dual-ABI APK `versionCode=7` (`#548`)
- Панель `ShellTouchModeBar` перенесена в отдельный [`Scripts/ui/Shell/ShellTouchModeBarView.cs`](../Scripts/ui/Shell/ShellTouchModeBarView.cs) рядом с `DatePlate` (`168×102`, `0` перекрытий с миникартой).
- Чёткость рендера на Android: длинная сторона `<= 2048`, `RenderScale = 1.0`, `Msaa2X`, `Anisotropy4X`, `TextureMipmapBias = -0.35`, `ScreenSpaceAA = Disabled`, `default_texture_filter = 1`, на таче по умолчанию `CloudsEnabled = false` (десктопный `relief_displacement` сохранён без изменений).
- Верифицированный Dual-ABI APK `versionCode=7` (`0.8.3.7`): `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk` (**`550 219 570` байт**, `arm64-v8a` + `x86_64`, **4 предзапечённых бинарных кэша**, `APKSIGNER_EXIT=0`, `UNGUARDED_GCSETTINGS=0`, SHA-256 `D48229BB43A7AC8FD2374368B6A051208DB58F21AB847B4607F85E939D467CB5`). Подробности — в разделе 12 [`docs/ANDROID_PORT_PLAN_RU.md`](ANDROID_PORT_PLAN_RU.md).
