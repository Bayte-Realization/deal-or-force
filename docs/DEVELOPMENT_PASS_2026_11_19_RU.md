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

