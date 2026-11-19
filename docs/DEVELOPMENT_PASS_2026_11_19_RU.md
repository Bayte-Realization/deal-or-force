# Проход 19 ноября 2026: финальная отделка интерфейса, чистый глобус, мод-ресурсы и релизная сборка 0.7.2.3

Проход выполнен в ветке `EntertAlpha` (без отправки в удалённый репозиторий) перед показовой сборкой `0.7.2.3`. Задачи были разделены на три части: моддируемые ассеты (графика и звук Creative Claw), выравнивание и унификация непрозрачного графитово-латунного интерфейса с исправлением выбора страны и наложений HUD, а также снятие дымки с глобуса меню и орбитальной карты кампании с доводкой экспортной пробы под обе оболочки.

## Что сделано

### Модифицируемые текстуры и звук (Creative Claw)

- **Графика интерфейса.** Три новые текстуры сохранены в `game/gfx/interface/shell/` и зарегистрированы в каталоге `game/gfx/interface/90_shell_sprites.json`, поэтому любой мод подменяет их по имени спрайта:
  - `shell_plate_weave` → `plate_slate_weave.png` (256×256, нейтральная графитовая микротекстура переплётной ткани и сланца);
  - `shell_header_frieze` → `header_frieze_band.png` (512×96, узкий архитектурный фриз для шапок рабочих экранов);
  - `leader_anonymous` → `portrait_cameo_anonymous.png` (128×160, барельеф-камея анонимного государственного деятеля).
- **Загрузка свободных PNG.** Для путей из каталога спрайтов и портретов добавлен резервный путь загрузки через `ProjectSettings.GlobalizePath` и `Image.LoadFromFile`, когда `.import`-ресурса нет в кэше (`Scripts/ui/ChromePlateArt.cs`, `Scripts/ui/LeaderPortraitUI.cs`).
- **Аудиопакет (`8aedb722`).** Через каталоги `game/sound/09_shell_tactile.json` и `game/music/17_architects_of_detente.json` (с локализацией EN/RU в `zz_445_creative_audio.json`) добавлены четыре сухих тактильных звука интерфейса (`shell_plate_tap`, `shell_drawer_slide`, `shell_map_lens`, `shell_dispatch_seal` из `game/sound/audio/interface/*.wav`) и камерная композиция кампании `architects_of_detente` («Architects of détente», `game/music/audio/architects_of_detente.ogg`).

### Единый непрозрачный интерфейс, выбор страны и раскладка HUD

- **Выбор страны (`SELECT A COUNTRY`).** В `MaterialStyles.Undress` (`Scripts/ui/MaterialStyles.cs`) добавлена защита элементов с метаданным `"menu_chrome"`, чтобы `Ui.Place` больше не сбрасывал оформление архитектурных плит. В `CountrySelectionUI.BuildCountryPicker` (`Scripts/ui/CountrySelectionUI.cs`) вызовы `Ui.Place` для `picker.heading`, `picker.country` и `picker.actions` поставлены до `MenuChromeTheme.StyleArchitecturalPlate`, а у `_pickerHeading` и `_pickerSubheading` отключён перенос (`AutowrapMode.Off`, `ClipText = false`, `SizeFlagsHorizontal = ExpandFill`, для подзаголовка — `TrimEllipsis`) — убрано схлопывание высоты заголовка и пустая рамка в верхнем левом углу.
- **100 % непрозрачные графитово-латунные плиты.** В `game/interface/00_skin.json` (`surface.overlay` → `#171615`, `surface.line_faint` → `#2E2C29`), `game/gui/20_campaign_structure.json` (`shell.background` → `#191817`) и в C#-резервах `Scripts/Simulation/InterfaceSkin.cs` убраны полупрозрачные подложки и старый холодный синий `#1d4477`; шесть опорных токенов `game/interface/90_shell.json` (`#222120`, `#171615`, `#0C0C0B`, `#9B958C`, `#F2F0EC`, `#302E2C`) сохранены без изменений.
- **Текстура плит и фриз шапок.** В `MenuChromeTheme.StyleArchitecturalPlate` и `DealOrForceGame.PanelStyle` / `TexturedPanel` (`Scripts/ui/MenuChromeTheme.cs`, вынесенный `Scripts/ui/ChromePlateArt.cs`, `Scripts/DealOrForceGame.cs`) все модальные окна и рабочие досье переведены на непрозрачный градиент `ModalPlateBox` (`#222120` → `#171615`) с тонким наложением `shell_plate_weave` (без `partial class DealOrForceGame`, при `MenuChromeTheme.cs` в `761` строку). В `ScreenChromeUI.ScreenHeader` (`Scripts/ui/ScreenChromeUI.cs`) подключён фоновый фриз `shell_header_frieze`, заголовок с засечками `SerifDisplay` (`26` px, `Paper`) и гравированная линейка `CreateEngravedRule("ScreenHeaderRule", 2f)`.
- **Флаги без чёрных полос и портрет-камея.** В `MenuChromeTheme.CreateFlagPocket` / `UpdateFlagPocket` и `NationFlagUI.FlagBadge` рамка подгоняется строго под вычисленный `ShellLayout.FitFlag` (для вымпела `NPL` — пустой стиль по силуэту, для остальных флагов — 1 px рамка `BrassHi` на подложке `WellTop`). Анонимный портрет (`game/interface/65_leader_portraits.json`, `Scripts/ui/LeaderPortraitUI.cs`) использует `portrait_cameo_anonymous.png` с цветами `#181716` / `#7D766B` и рамкой `WellTop` / `Brass`.
- **Развязка панелей HUD и защита миникарты.** В `game/gui/10_campaign_shell.json` обновлены отступы `hud.side_panel` (`[106,128,548,-168]`), `hud.army_panel` (`[106,128,612,-168]`), `hud.support_panel` (`[106,128,712,-168]`) и `hud.country_card` (`[106,128,548,-168]`), чтобы панели не наезжали на верхний щиток страны и нижнюю полосу планирования/армий. В `Scripts/ui/Shell/NewShellUI.cs` (`396` строк) левый край панелей привязан к `profile.LeftPanel.X`, а на `Compact` (1280×720) правая граница ограничивается перед карточкой газеты и колонкой тревог. В `Scripts/ui/Shell/ShellMapBarView.cs` (`EnsureMinimapTextureCurrent`) чтение цвета страны заменено на безопасный `Color.FromString(..., new Color(0.43f, 0.42f, 0.40f))`.

### Снятие дымки с глобуса меню и кампании

- **Глобус главного меню (`Scripts/MenuGlobe.cs`, `game/gfx/interface/shaders/menu_globe.gdshader`, `menu_globe_air.gdshader`).** Воздушная оболочка сужена с `Radius * 1.075f` до `Radius * 1.032f` (`thickness = 0.11`, `strength = 0.14`), тон зари `air_colour` выровнен к `(0.32, 0.50, 0.76)`, кривая терминатора заменена на `smoothstep(-0.10, 0.78, lit)`, затухание лимба обострено (`pow(..., 5.2)` и `pow(..., 3.8)`), ночные огни городов выведены на `0.58`.
- **Орбитальный глобус кампании (`Scripts/gfx/GlobeAtmosphere.cs`, `Scripts/gfx/GlobeWeather.cs`).** Задана контрастная цветокоррекция (`GradeBrightness = 0.99f`, `GradeContrast = 1.06f`, `GradeSaturation = 1.05f`), сила атмосферного ореола снижена до `0.14f–0.18f` (`falloff 4.8f–5.2f`), прозрачность облачных материалов `_cloudMaterial` и `_cloudShadeMaterial` умножена на `0.38f`, а интенсивность `AirShader` уменьшена примерно на 60 % без изменения тумана войны `_mistMaterial` и `Defines`.

### Две оболочки в экспортной пробе (`ExportVerification`)

- **Причина сбоя при первой сборке.** Настройка по умолчанию в `46_ui_shell.json` перешла на новую оболочку `"new"`, из-за чего в экспортированной сборке скрывалась классическая верхняя панель (`CampaignTopBar`, `HudStat_DEBT`) и проба суверенных финансов падала (`EXPORT_VERIFY failures=10`).
- **Исправление (`Tools/ExportVerification/ExportVerification.cs`, `Tools/ExportVerification/NewShellExportVerification.cs` + `.cs.uid`).** Вспомогательные методы `EnterClassicUiShellAndBeginSession` / `UiShellScope.Dispose` переключают `ui_shell` в `"classic"` на период проверки классического HUD и возвращают прежнее значение при выходе; затем `VerifyLedgerAndNewShell` после `VerifyLedgerCharts` включает `"new"`, проверяет видимость `NewCampaignShell` и `ShellMinimap` и снимает кадр `new-shell-rest`.

## Чем проверено

- **Автономная релизная сборка `0.7.2.3`** (`Tools/build.ps1 -Godot "C:\GodotEngineNet 4.8-Beta3\Godot_v4.8-beta3_mono_win64.exe" -OutputDirectory "build\DealOrForce_ShowReady\Deal or Force" -ReplaceExistingOutput`):
  - Сборка и упаковка: каталог `build/DealOrForce_ShowReady/Deal or Force/` (`Deal or Force.exe` 105,2 МБ, `Deal or Force Launcher.exe` 76,7 МБ, `Deal or Force.pck` 248,6 МБ, `data_DealOrForce_windows_x86_64`, данные `804,9` МБ, 0 лишних файлов) и архив `build/Deal or Force 2026-11-19_05-34-34.zip` (`645,9` МБ).
  - Внутриигровая проба `verify-build.ps1`: **`EXPORT_VERIFY failures=0`**, `[dof] application exit ready code=0` (проверены импорт римских флагов, 60-дневный прогон истории с 22 из 22 событий на графиках, классический HUD и новая оболочка с видимым `ShellMinimap`).
- **Компиляция и контроль кода:**
  - `DealOrForce.sln`: Debug — `0` ошибок; Release — `0` ошибок.
  - `Tools/CodeAudit --ratchet`: **PASS, 0 регрессий** (`files=920, maxFileLines=4814/4814, maxMethodLines=1257/1257, maxCyclomatic=233/233, simStaticMutable=949/949, deadCandidates=115/115`).
  - `Scripts/ui/MenuChromeTheme.cs` — `761` строка (`< 800`), `Scripts/ui/Shell/NewShellUI.cs` — `396` строк (`< 800`), `0` новых `partial class DealOrForceGame`.
  - `Tools/audit-localization.ps1 -Fail`: exit `0` (`27 600` записей в `420` файлах, `0` конфликтов, `0` расхождений подстановок, `0` пустых значений).
- **Наборы `Tools/FormableTests`** (все перечисленные с exit `0`, 0 пропущенных проверок):
  - `--new-shell-rest-only`: `233` PASS (все 30 `.import` под `shell/`, нулевые пересечения в обычном/военном/компактном режимах);
  - `--menu-chrome-only`: `55` PASS; `--map-look-only`: `224` PASS; `--map-labels-only`: `51` PASS; `--weather-only`: `28` PASS;
  - `--no-competitor-names-only`: `11` PASS; `--shell-coherence-only`: `71` панель и `16` кнопок навигации PASS; `--atlas-surfaces-only`: `17` PASS; `--code-health-only`: `45` PASS (`0` Godot в симуляции, `0` отсутствующих/дублирующихся `.cs.uid`, `0` статических полей `Campaign`);
  - `--skin-overlay-only`, `--responsive-hud-only`, `--ui-icons-only`: exit `0`.
- **Визуальный осмотр кадров экспортной пробы (`tmp_playtest/live_final/`):** просмотрены `export-menu.png` (глобус без дымки, тёмная плита меню без синих кнопок), `export-new-shell-rest.png` (живая цветная миникарта с рамкой камеры, часовая плашка даты, плотная латунная рамка флага США, газета и лента армий без наложений), `export-diplomacy-layout-english-1920.png`, `export-sovereign-finance-1920.png` (термины казны/госдолга/просрочки), `export-laws.png`, `export-course.png` и `export-ledger-charts.png`.
- **Живая съёмка из редактора (Godot MCP Pro, 1920×1080, `tmp_playtest/live_final/`):**
  - `02_country_picker.png`: непрозрачная графитовая плита заголовка `SELECT A COUNTRY` и строки даты без вертикального схлопывания и обрезки, плотная латунная рамка флага DEU без чёрных полос (внешний бейдж `№ 4 · DEU`), камея портрета `portrait_cameo_anonymous.png`, чистая Европа без белёсой дымки и без синих кнопок `#1d4477`.
  - `04_army_planning.png`: выбранная `1st DEU Division` (`12 div`), левая панель армии, нижняя панель планирования (`Whole front / Attack / Defend / Execute`) и нижняя лента армий (`DEU Army 1`, `DEU Army 2`) разведены без наложений; читаются 3D-здания Берлина/Потсдама и живая миникарта.
  - `05_state_affairs.png` и `07_production.png`: 100 % непрозрачные текстурированные плиты (`#222120` → `#171615`) с уголками фриза шапки и заголовком с засечками; в `State Affairs` соблюдены термины казны, госдолга и баланса, активная кнопка рейки подсвечена без синего `#1d4477`.

## Что не проверено и ограничения

- Полный прогон всех тестов `FormableTests` единым вызовом и многолетний прогон баланса `SimBench --soak` в этом проходе не запускались: правки затрагивали интерфейс, атмосферные шейдеры, аудиокаталог и экспортную пробу, не меняя правила симуляции.
- Работа зафиксирована локальным коммитом на ветке `EntertAlpha`; публикация в удалённый репозиторий и сборка дополнительных платформ не выполнялись.
