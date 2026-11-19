# Проход 19 ноября 2026: релиз EntertAlpha 0.8.3.2 — доводка интерфейса, портреты правителей, визуальное досье региона, оптимизации и публикация

Проход выполнен в ветке `EntertAlpha` (верхний релизный коммит поверх `e9d59291`) и подготовлен к выкладке версии **`EntertAlpha 0.8.3.2`** (`0.8.3.2` в `GameVersion.props`, `GameBuildInfo.cs`, `project.godot`, `export_presets.cfg`, `Tools/FormableTests/VersionLabelTests.cs`, `README.md`, `README.ru.md`, `docs/BUILDING*.md` и `docs/CHANGELOG_0.8.3.2_{EN,RU}.md`).

## Что сделано

### Версия 0.8.3.2, GitHub README и актуальные скриншоты

- Во всех файлах проекта и сборочных описаниях снят суффикс `-dev` и выставлена единая версия **`0.8.3.2`** / **`EntertAlpha 0.8.3.2`**.
- Добавлены описания релиза [CHANGELOG_0.8.3.2_EN.md](CHANGELOG_0.8.3.2_EN.md) и [CHANGELOG_0.8.3.2_RU.md](CHANGELOG_0.8.3.2_RU.md).
- Старые скриншоты `docs/screenshots/0.6.6/*.jpg` заменены свежей серией 1600×900 в [docs/screenshots/0.8.3.2/](screenshots/0.8.3.2/): `menu-planet.jpg`, `map-globe.jpg`, `country-picker.jpg`, `map-flat.jpg`, `campaign.jpg`, `region-dossier.jpg`, `region-population.jpg`; разделы изменений и галереи в [README.md](../README.md) и [README.ru.md](../README.ru.md) обновлены под `0.8.3.2`.

### Интерактивные улучшения HUD, портреты правителей и визуальное досье региона

- **Спокойная лента сводок и строгий стек `Esc`.** Реализован детерминированный планировщик `Scripts/Simulation/Shell/ShellNewsPacer.cs` (минимальная выдержка `2.8` с, приоритетная очередь `6`, заморозка при наведении/закреплении и пауза между звуками `3.5` с) + отдельная кнопка `×` на карточке газеты (`ShellAlertColumnView.cs`). `Esc` больше никогда не закрывает новости и не открывает меню паузы поверх архива сохранений/настроек (`HandleNewShellEscape` в `NewShellUI.cs` и `IsSystemModalBlockingCampaignInput` в `DealOrForceGame.cs`), а при открытых модалках скрываются правый столбец, миникарта и нижняя полоса планирования.
- **Скульптурная отделка, белая рамка аутлайнера и типографика городов.** Добавлены и зарегистрированы (`90_shell_sprites.json`, `game/interface/README.md`) шесть текстур Creative Claw (`corner_ornament_brass.png`, `divider_engraved_medallion.png`, `button_bevel_sheen.png`, `dossier_paper_header.png`, `meter_track_engraved.png`, `icon_atlas_terrain.png`); правый аутлайнер, плашка даты, нижняя панель карты и миникарта получили единую чёткую светлую рамку (`shell.outliner_border`); подписи городов переведены на чистый гротеск (`map.cities` в `00_fonts.json`, `GlobeCities.cs`), а `port_elizabeth_zaf` обновлён на `Gqeberha` / `Гкеберха`.
- **«Решения» в левом аутлайнере и общий мировой ранг.** В `ShellNavRailView.cs` сразу под внутренними делами добавлена кнопка **«Решения»** (`ShellRail_Decisions`) с живым счётчиком доступных решений; верхний левый бейдж (`ShellCornerView.cs`) и досье страны (`ShellWorkPanelView.cs`) переведены на общий мировой ранг державы (`CountryRankings.Read`, `"shell.overall_rank"` вместо `by GDP`), а при клике по кнопкам левого аутлайнера/флага всплывающая бирка (`HideHoverTag()`) скрывается сразу.
- **Портрет правителя в досье любой страны и визуальное досье региона без скачков ширины.**
  - Делегат `ShellContext.BuildLeaderPortrait` (`NewShellUI.cs` → `BuildLeaderPortraitBadge`) и `ShellRegionVisualsView.PopulateCountryRulerCard` отображают рамку портрета (фото мода или `portrait_cameo_anonymous.png`), титул, возраст, имя, идеологию, черту и главу правительства у **каждой** осматриваемой державы (своей и иностранной).
  - В `ShellWorkPanelView.cs` контейнер `WorkPanelFrame` (`ClipContents = true`, `CustomMinimumSize = Zero`), `_lastTabSig` и немедленный `RemoveChild` перед `QueueFree` полностью убрали горизонтальные рывки панели на Speed 5; подсчёт провинций региона кэшируется за O(1).
  - Новый модуль `Scripts/ui/Shell/ShellRegionVisualsView.cs` заполняет все 4 вкладки региона (своего и иностранного) карточкой местности с модификаторами боя/снабжения, гравированными шкалами стабильности/уровня жизни/урбанизации/грамотности/радикализма, полосами возраста, народов и вер, очередью стройки, сеткой зданий, месторождениями и видимым гарнизоном.

### Рефакторинг и оптимизация под слабые ПК и Android

- Устранён двойной пересчёт скрытого классического HUD при активной новой оболочке (`ArmyBarUI.cs`, `StrategicHudUI.cs`, `TopBarHudUI.cs`, `SituationDeskUI.cs`, `HeadlinePopupsUI.cs`) и рекурсивный обход 12 `FindChild` на каждом часовом тике (`NewShellUI.cs`); добавлены разнесение почасовых/дневных обновлений в `ShellOrchestrator.cs`, O(1)-пропуск хроники в `TickNews`, переиспользуемые буферы миникарты, кэш подсчёта провинций и адаптивный мобильный профиль качества (`PerformanceGovernor.cs`, `ShellMapBarView.cs`).

---

### Ранний этап дня (0.7.2.3 до релиза 0.8.3.2) — зафиксирован в `e9d59291`
*(Базовый проход этого же дня: перенос `shell_plate_weave` / `shell_header_frieze` / `portrait_cameo_anonymous`, аудиопакет `8aedb722`, 100 % непрозрачные плиты `surface.overlay` / `shell.background`, снятие дымки с глобуса меню и орбитальной карты, двойная проверка оболочек в `ExportVerification` — сохранён в истории ветки коммитом `e9d59291`.)*

## Чем проверено (на финальном дереве 0.8.3.2)

- **Компиляция, версия и локализация:**
  - `dotnet build DealOrForce.sln -c Debug` и `-c Release`: **0 ошибок** (в обоих конфигурациях).
  - `Tools/Test-ReleaseVersion.ps1`: **17/17 PASS** (`0.8.3.2`, `EntertAlpha 0.8.3.2`, Windows product/file version & shared props).
  - `Tools/audit-localization.ps1 -Fail`: **exit 0** (`27 622` записей в `421` русском файле, `0` конфликтов ключей, `0` ошибок подстановок `{0}`, `0` пустых строк).
- **Инварианты `Tools/FormableTests` (все 17 наборов, Release, exit `0`, 1 336 проверок PASS):**
  - `--version-label-only`: **1 PASS** (`PASS version label: EntertAlpha 0.8.3.2`).
  - `--news-pacer-only`: **35 PASS** (выдержка 2.8 с, приоритетная очередь 6, заморозка при наведении/закреплении, пауза звука 3.5 с, чистая симуляция без Godot).
  - `--new-shell-rest-only`: **347 PASS** (проверены все новые текстуры и `.import` под `shell/`, нулевые пересечения в 1920×1080 и 1280×720, пороги `< 800` строк во всех файлах `Shell/` и отсутствие обхода классического HUD).
  - `--menu-chrome-only` (250 PASS), `--map-look-only` (224 PASS), `--map-labels-only` (51 PASS), `--weather-only` (14 PASS), `--no-competitor-names-only` (11 PASS), `--shell-coherence-only` (9 PASS), `--atlas-surfaces-only` (17 PASS), `--code-health-only` (50 PASS), `--interface-type-only` (72 PASS), `--shell-date-plate-only` (8 PASS), `--skin-overlay-only` (16 PASS), `--content-shape-only` (3 PASS), `--ui-icons-only` (219 PASS), `--smooth-heavy-yield-only` (7 PASS).
- **Статический контроль качества (`Tools/CodeAudit --ratchet`):**
  - **PASS, 0 регрессий** (`files=922, lines=274277, compileErrors=16, godotInSim=0, maxFileLines=4814/4814, maxMethodLines=1257/1257, maxCyclomatic=233/233, simStaticMutable=949/949, deadCandidates=115/115`); каждый изменённый/новый файл под `Scripts/ui/Shell/` строго меньше 800 строк (`ShellAlertColumnView.cs` — 752, `ShellWorkPanelView.cs` — 761, `ShellRegionVisualsView.cs` — 784).
- **Живой прогон в редакторе (Godot MCP Pro, `capture_showcase.mjs`, 1920×1080):**
  - Проверены автоматические утверждения: `ShellRail_Decisions` найден на `(32, 213)`, `ShellNewspaperCard` + `ShellNewsCloseButton` активны на Speed 5 (`true|true`), `RegionDossierWidth` при открытой вкладке региона и прогоне Speed 5 равен `472.0 -> 472.0` (**дельта 0.0 px**), `SaveLoadEsc` (`open=true|false`, `afterEsc=false|true`) ни разу не показал меню паузы поверх архива сохранений.
  - Визуально проверены все кадры (`01_main_menu.png` с `EntertAlpha 0.8.3.2`, `02_country_picker.png`, `03_map_globe.png` с `№ 12 overall` и белой рамкой правого аутлайнера, `05_map_flat.png`, `06_country_dossier_ruler.png` и `06b_foreign_country_ruler.png` с портретами Штайнмайера и Макрона и общим рангом, `07_region_dossier_visual.png` и `07b_region_pop_visual.png` со шкалами/карточкой местности/полосами демографии, `08_decisions_rail.png`, `09_saveload_clean.png`).
- **Автономная релизная сборка и публикация:**
  - `Tools/build.ps1` → `build/DealOrForce_0832/Deal or Force/`: **`EXPORT_VERIFY failures=0`**, `[dof] application exit ready code=0` (`Deal or Force.exe` версии `0.8.3.2`, `Deal or Force Launcher.exe`, `Deal or Force.pck`, `data_DealOrForce_windows_x86_64`, данные 805 МБ).
  - Развёрнуто в **`C:\Билды DoF (DiskC)\EntertAlpha 0.8.3.2 2026-11-19\Deal or Force\`**, архив **`C:\Билды DoF (DiskC)\Deal or Force EntertAlpha 0.8.3.2 2026-11-19.zip`** (`677 366 088` байт), контрольная сумма в `Deal or Force EntertAlpha 0.8.3.2 2026-11-19.zip.sha256`: `A819BF1FF71FA7B57CB421CBBD509E38EFDD0ADB36971F5426552929F4B1EC0A`.

## Что не проверено и ограничения

- Полноценный нативный экспорт и запуск APK на физическом Android-устройстве и 1825-дневный прогон баланса `SimBench --soak` в рамках этого финального прохода не запускались; мобильные и производительностные правки проверены в ПК-сборке и на наборе `--smooth-heavy-yield-only` / инспекторе сборки.
- Исходная история веток `work/*`, предшествовавших Wave 2, проверена (`git branch --no-merged EntertAlpha --list "work/*"` вернул 0 невмерженных веток).


---

## Финальный проход вечера 19 ноября 2026 (текстуры, переработка меню/досье, фиксы багов со скриншотов, принудительный мир и глубокая оптимизация A+C)

Выполнено поверх `d653bec0` (ветка `EntertAlpha`) с полной перепроверкой на финальном дереве:

1. **5 равномерных бесшовных фактур и 30+ мод-спрайтов (`game/gfx/interface/panels/`, `game/gfx/interface/icons/`, `90_shell_sprites.json`, `90_shell.json`):**
   - `surface_command_leather.png` (`shell_surface_leather`), `surface_slate_twill.png` (`shell_surface_slate`), `dossier_body_grain.png` (`shell_dossier_grain`), `surface_laid_paper.png` (`shell_surface_laid_paper`), `surface_blueprint_grid.png` (`shell_surface_blueprint`) + равномерные `button_bevel_uniform_9s.png`, `card_wrapper_9s.png`, `tab_strip_backing.png`, `header_emblem_filigree.png`, `modal_cartography_backdrop.png`, `portrait_bezel_ornate.png`, 12 алебастровых иконок местности (`icon_terrain_*`) и 11 отмасштабированных чётких иконок меню (`icon_menu_*`).
   - Новый `Scripts/ui/ChromeSurfaceOverlay.cs` (`Node2D`, с `.uid`) накладывает бесшовный тайлинг поверх всей площади `PanelContainer` (без отступа `ContentMargin`).
2. **Структурная переработка меню, досье и исправление 5 живых багов HUD:**
   - `MainMenuScene.cs`: карточка «Продолжить» с флагом без дублирующейся даты, командная кожа, иконки действий, центрированная филигрань.
   - `PauseMenuUI.cs` (живая сводка кампании + камея + геодезический фон), `SaveLoadUI.cs` (поиск сейвов + карточки с флагом), `ShellCountryDossierView.cs` (вынесенный 344-строчный модуль: сетка 2×2 KPI, новая вкладка `Общество` со шкалами и карточками правительства/веры).
   - Исправлены: пустое тело `ВОЙНЫ МИРА` (`WarLedgerUI.FitWarLedger` + растяжение полного экрана в `ShellOrchestrator`), пустая левая панель `"Война"` (`ShellAlerts.cs` `"War"` -> `"Wars"`, `DealOrForceGame.cs` сброс `_sideBody.Text`), обрезка `'y'` в `"15 div Army"` (`ShellWorkPanelView.AddFooterButton`), подпись войн и отступы в верхней плашке (`ShellCornerView`), локализованная дата и расширенный перенос заголовков в газете (`ShellAlertColumnView`), и 82 ошибки `p_name.is_empty()`.
3. **Мир при 100 % оккупации или 100 % счёта (`02_peace.json`, `ForcePeaceOnTotalOccupationOrFullScore = true`):**
   - Полностью оккупированные лидеры и младшие партнёры коалиций (`SeparatePeace.cs`, `AiStatecraft.Negotiate`, `PeaceCore.cs`, `PeaceConference.cs`, `PeaceOffers.cs`) и межгосударственные войны на `±99,5 %` счёта завершаются сразу; для движущейся гражданской войны сохранена защита от реконкисты; игроку-победителю приходит немедленная капитуляция.
4. **Глубокая оптимизация (Этапы A + C) — замеры `SimBench` ДО и ПОСЛЕ:**
   - Этап A: пулинг узлов `ArmyStackMarker` в `GlobePresentation.cs`, тёплый кэш `CompactCounterTextures.cs`, троттлинг `RebuildBattleMarkers` (`MilitaryPresentation.cs`) и GPU-загрузок контроллеров на Speed 5, нулевые аллокации в `ShellStripView.cs`, кооперативные `Campaign.StageOverdue()` в `AiStatecraft.cs` и тяжёлых циклах.
   - Этап C (`95_long_campaign.json`): `FoldSliverGroups = true`, `StaggerWeeklyEconomy = true`, `AiCalmCadenceDays = 7`.
   - **2024 старт Speed 5 (`SimBench 30 42 --frames 5,3 --frame-days 14`, срез 16 мс):** худший кадр `661,9 мс -> 27,3 мс`, `p99`: `343,89 -> 25,39 мс`, кадров `> 33 мс`: `14 -> 0`, `> 50 мс`: `8 -> 0`, скорость `2,61 -> 6,53 дн/с` (Speed 3: медиана `1,02 -> 0,67 мс`, `0 > 33 мс`).
   - **Soak и детерминизм:** `SimBench 365 1 --soak` -> `Soak PASS: 365/365 days, 0 alert(s), 0 invariant failure(s)`; `SimBench 60 42 --fingerprint 60` (`day 30: 6296889915955535350`, `day 60: 11847644473826386008`) идентичен при `BDOF_NO_PARALLEL=1`.
   - **Поздний сейв 2131 г. (`bozhe.bdfsave`, `--diet`, срез 9 мс):** свёрнуто `69 228` групп (`268 313 -> 201 890`), плавные кадры `p99 21,53 мс`, `worst 32,5 мс`, `0` кадров `> 33 мс` (`CPU own 540,2 мс/дн`).
5. **Дальнобойные войска в левой панели, скриншоты и финальный билд:**
   - В `Scripts/ui/Shell/ShellNavRailView.cs` добавлена 12-я кнопка левого рельса **«Дальнобойные войска»** (`Support`, иконка гаубицы `icon_artillery`, бейдж свободных соединений во время войны, точная подгонка высоты под 1920×1080 и 1280×720, `Scripts/Simulation/Shell/ShellLayout.cs` `baseHudButtons = 27`, локализация EN/RU `shell.rail_support` / `shell.sub_support`), а `Scripts/ui/SupportScreenUI.cs` получил чертёжную сетку `PanelSurface.Command` и синхронную подсветку кнопки при открытии/закрытии.
   - Обновлены все скриншоты в `docs/screenshots/0.8.3.2/*.jpg` (добавлены `war-ledger.jpg`, `pause-desk.jpg` и `long-range-forces.jpg`), обновлены `README.md`, `README.ru.md`, `CHANGELOG_0.8.3.2_EN.md`, `CHANGELOG_0.8.3.2_RU.md`, пройдены тесты `FormableTests` (включая `--new-shell-rest-only`, `--shell-coherence-only`, `--menu-chrome-only`, `--code-health-only`, `--no-competitor-names-only`, `--ui-icons-only`, `--atlas-surfaces-only`, `--version-label-only`, 4 новых теста на принудительный мир и возвращённую защиту `--civil-stalemate-only`), `Test-ReleaseVersion.ps1` (17/17 PASS), `CodeAudit` (`dead=115`, `godotInSim=0`) и `audit-localization.ps1 -Fail` (`0` конфликтов по `27 641` записям в `422` файлах).
   - Финальный экспорт `Tools/build.ps1` → `build/DealOrForce_0832_r3/Deal or Force/`: `EXPORT_VERIFY failures=0`, `[dof] application exit ready code=0`. Развёрнуто на диск `C:\Билды DoF (DiskC)\EntertAlpha 0.8.3.2 2026-11-19\Deal or Force\`, архив `Deal or Force EntertAlpha 0.8.3.2 2026-11-19.zip` (`678 546 387` байт), SHA-256: `84BB4195F476E31BE80C308EF72723F8AF3031021A4BF24D3DDADF9F0B5A6308`.
