# План и технический аудит Android-порта Deal or Force

Дата: 21 ноября 2026  
Ветка: `EntertAlpha` (базовый коммит `e11274cb`, целевой движок `Godot 4.8.0-beta.3 .NET / net10.0`).

---

## 1. Резюме аудита и вердикты Decision-1 (`google/gemini-decision-1`)

Кодовая база **Deal or Force** находится в сильной отправной точке для мобильного порта: уже существуют 12-текстурный упакованный шейдер карты (`PackedMapShader`, укладывается в лимит 16 texture units GLES 3), чистый C#-фолбэк в `NativeKernelsLoader` при отсутствии нативных библиотек `dof_kernels`, базовое определение `_isMobileRuntime` в `PerformanceGovernor`/`ShellOrchestrator`/`ShellMapBarView`, а на рабочей станции уже установлены шаблоны экспорта `4.8.beta3.mono` (`android_debug.apk`, `android_release.apk`, `android_source.zip`) и Microsoft JDK 17.

Архитектурные решения прошли независимую проверку через `Tools/advisor-mcp/server.mjs` (`google/gemini-4-carbon` review + калиброванное голосование `google/gemini-decision-1`):

| Решение | Выбранный вариант | Уверенность Decision-1 | Обоснование |
| :--- | :--- | :---: | :--- |
| Графический бэкенд | **Mobile (Vulkan) primary + `gl_compatibility` (GLES 3) fallback** | **85%** | Единый проход на современных ARM-GPU + надёжный фолбэк через `PackedMapShader` (12 юнитов) на бюджетных Mali/Adreno; Forward+ на Android не используется. |
| Доставка данных `game/` (~300 МБ) | **Первичная распаковка версионированного архива `game/` в `OS.GetUserDataDir()` при первом запуске** | **93%** | Вся симуляция, карты и моды читают `game/` через `System.IO` (`GameDataLoader`). Распаковка в `user://` с проверкой версии сохраняет 100% совместимость с `GameDataLoader` и моддингом без переписывания сотен вызовов. |
| GC и бюджет памяти | **Workstation GC (`ServerGarbageCollection=false`) + ограничение кучи + компактные текстуры на MobileLow** | **96%** | Десктопный Server GC и лимит 4 GiB на 4–6 ГБ телефонах приводят к немедленному убийству процесса Android `lmkd` (порог ~1.2–1.5 ГБ PSS). |
| Жест контекстного действия (аналог ПКМ) | **Долгое нажатие (~420 мс) + явная панель приказов при выбранной армии** | **98%** | Проверенный стандарт мобильных глобальных стратегий: исключает случайные приказы и не требует сложных жестов двумя пальцами для отдачи команды. |
| Исключение `addons/godotsteam` | **Исключать из Android APK и закрывать через `PlatformProfile.IsSteamSupported`** | **90%** | Несмотря на наличие записей `android.arm64` в `godotsteam.gdextension`, Steamworks не работает на обычных устройствах Android / Google Play. |
| Оценка архитектурной готовности базы | **2.66 / 4 (~ «Сильный фундамент с ясными задачами»)** | 41% | Нет непреодолимых блокеров; ключевые задачи сосредоточены в M1 (доставка `game/`, MSBuild-условия для GC/F# trimming и SDK-инфраструктура). |

---

## 2. Полный технический аудит проекта и хоста

### 2.1. Результаты `Tools/android-doctor.ps1` на рабочей станции (21.11.2026)

| Область | Проверка | Статус | Фактическое состояние |
| :--- | :--- | :---: | :--- |
| Host | **JDK 17 (Godot/Gradle)** | **PASS** | Найден `C:\Program Files\Microsoft\jdk-17.0.13.11-hotspot` (`JAVA_HOME` установлен). |
| Host | **Конфликт `java` в `PATH`** | **WARN** | В `PATH` первым стоит Adoptium JDK 25 (`class file major version 69`), на котором падает Gradle в Godot 4.8. `Tools/build-android.ps1` автоматически ставит JDK 17 первым в `PATH`. |
| Host | **Android SDK** | **FAIL** | `ANDROID_HOME` / `ANDROID_SDK_ROOT` не заданы, `%LOCALAPPDATA%\Android\Sdk` отсутствует. |
| Host | **ADB & Devices** | **FAIL** | `adb` не найден (появится после установки Android SDK `platform-tools`). |
| Godot | **Android Export Templates (`4.8.beta3.mono`)** | **PASS** | Установлены в `%APPDATA%\Godot\export_templates\4.8.beta3.mono` (`android_debug.apk` 116.1 МБ, `android_release.apk` 98.4 МБ, `android_source.zip` 199.1 МБ). |
| DotNet | **64-bit .NET SDK** | **PASS** | `10.0.404` (`C:\Program Files\dotnet\dotnet.exe`). |
| DotNet | **.NET Android Workload** | **FAIL** | Не установлен (`dotnet workload list` пуст). Требуется `"C:\Program Files\dotnet\dotnet.exe" workload install android`. |
| Project | **`export_presets.cfg`** | **PASS** | Сохранены Windows-пресет (`preset.0`, байт-в-байт) и добавлен `Android (Dev / Debug)` (`preset.1`). |
| Project | **Исключения GDExtension** | **PASS** | Все 5 расширений (`fennara`, `ziva_agent`, `godotsteam`, `limboai`, `game/extensions/luau`) исключены в Android-пресете. |
| Project | **Мобильные шейдеры карты** | **PASS** | `PackedMapShader` (12 texture units <= 16 GLES3), `CompatibilityMapShader`, 6 `.gdshader`-файлов в `game/`. |
| Project | **Платформенный фундамент и тач** | **PASS** | Реализованы `PlatformProfile`, `GodotPlatformBridge`, `TouchGestureMath`, `TouchCameraAdapter`. |

### 2.2. Аудит GDExtension и нативных зависимостей

1. `addons/fennara/fennara.gdextension` — **Editor-only** (win/linux/macos editor). Исключён из Android-пресета и удаляется в `Tools/build-android.ps1`.
2. `addons/ziva_agent/ziva_agent.gdextension` — **Desktop-only**. Исключён из Android-пресета.
3. `addons/godotsteam/godotsteam.gdextension` — содержит ссылки на `libsteam_api.so`, но несовместим с Google Play / стандартным Android. Исключён из Android-пресета; все вызовы должны проверяться через `PlatformProfile.Current.IsSteamSupported`.
4. `addons/limboai/bin/limboai.gdextension` — исключён в `addons/*` (не требуется в базовом Android-клиенте).
5. `game/extensions/luau/luau_script.gdextension` — **Windows x86_64 only**. Исключён в `export_presets.cfg` и удаляется при staging в `Tools/build-android.ps1`.
6. `Scripts/Simulation/Kernels/NativeKernelsLoader.cs` (`dof_kernels`) — **уже Android-safe**: при отсутствии `.so` бесшумно и детерминированно откатывается на чистый C# без спама `DllNotFoundException`. При необходимости в M3 можно дособрать `libdof_kernels.so` под `arm64-v8a` через Android NDK r28b (`28.1.13356709`).
7. `Scripts/Rules/DealOrForce.Rules.fsproj` (F#) — управляемый .NET, переносится на Android. Для Release-сборок с ILLink trimming потребуется `<TrimmerRootAssembly Include="FSharp.Core" />` или `<PublishTrimmed>false</PublishTrimmed>`.

### 2.3. Аудит настольных вызовов, путей и памяти

- **`OS.ShellOpen(...)`** найден в 6 файлах (`MainMenuScene.cs`, `CountryEditorUI.cs`, `FlagEditorUI.cs`, `SaveLoadUI.cs`, `ScenarioWorkbenchUI.cs`, `SettingsUI.cs`). На Android прямой вызов `ShellOpen` для локального пути приводит к отказу или `FileUriExposedException`. Обёрнут безопасным методом `GodotPlatformBridge.TryOpenLocalFolderSafely(...)` и флагом `PlatformProfile.IsDesktopFileDialogSupported`.
- **`OS.GetSystemDir(OS.SystemDir.Documents)`** в `Scripts/GamePaths.cs` — на Android 11+ (Scoped Storage) недоступен на запись без спецразрешений. Для Android добавлен `GodotPlatformBridge.ResolveWritableDataRoot()`, возвращающий `ProjectSettings.GlobalizePath("user://")`.
- **`WorkerModBackend.cs` (`process.Start()`)** — на Android SELinux W^X запрещает запуск произвольных внешних бинарников из пользовательского каталога; флаг `PlatformProfile.IsExternalWorkerModSupported = false` на мобильных платформах.
- **Шейдеры и Mobile/GLES3** (согласовано с рекомендациями Godot 4.x и `#192`):
  - `globe_map.gdshader` использует >16 сэмплеров в Forward+, но `PackedMapShader` автоматически пакует его ровно в **12 texture units** (`sampler2DArray` для ID, lookup-таблиц и воды).
  - На GLES 3 (`gl_compatibility`) требуется 1-кадровый прогрев шейдеров при загрузке, а на Forward Mobile запрещены compute-шейдеры и соблюдается правило `source_color` на sRGB-текстурах.

---

## 3. Реализованный фундамент (M0 — готов в коде и тестах)

1. **`Scripts/Platform/PlatformProfile.cs`** *(чистый C#, 0 зависимостей от Godot)*:
   - Классификация форм-фактора: `Desktop`, `AndroidPhone` (< 7.0"), `AndroidTablet` (>= 7.0").
   - `SafeAreaInsets`: безопасная обработка вырезов/челок/панели жестов (`FromRects` с ограничением <= 25% на сторону и защитой от `NaN`/вырожденных прямоугольников, `ApplyToViewport`, масштабирование в логические координаты).
   - Эргономика: `UiScaleFactor` (Desktop `1.0`, Phone `1.18–1.35`, Tablet `1.08–1.15`), `MinimumTouchTargetDp` / `MinimumTouchTargetPx` (48dp на телефоне, 44dp на планшете, 32px на ПК) и хелпер `ClampTouchTarget(...)`.
   - Графика и защита возможностей: `PlatformGraphicsTier` (`DesktopHigh` / `MobileMedium` / `MobileLow`), `IsLowEndMobileAdapter(...)`, `IsSteamSupported`, `IsDesktopFileDialogSupported`, `IsExternalWorkerModSupported`, `ShouldUseCompactTextureBudget`, `RecommendedIdleFpsCap` (30 FPS в простое на мобильных / 60 FPS при активности).
2. **`Scripts/Platform/GodotPlatformBridge.cs`**:
   - Инициализация `PlatformProfile.Current` из `OS`, `DisplayServer`, `RenderingServer` (`EnsureInitialized` / `Refresh`).
   - `TryOpenLocalFolderSafely`, `ResolveWritableDataRoot`, `DescribeRuntimeSummary`.
3. **`Scripts/Platform/TouchGestureMath.cs`** *(чистый детерминированный C#, 100% покрыт unit-тестами)*:
   - Автомат состояний `TouchGestureMachine`: `Idle` -> `PendingTapOrPan` -> `Panning` / `Pinching` / `LongPressFired` / `PostPinchGrace`.
   - 1-пальцевое панорамирование с мёртвой зоной и экспоненциальной инерцией (`Damping = 8.0`, синхронно с `CameraInertia`, обнуление при остановке пальца >90 мс).
   - 2-пальцевый pinch-to-zoom вокруг точной средней точки экрана `ZoomAnchorPx`.
   - 2-пальцевое вращение (`RotateDeltaRadians` с корректным переходом через границу `-pi/+pi`) и параллельный вертикальный наклон 3D-глобуса (`TiltDeltaPx`).
   - Разделение тапа (ЛКМ) и долгого нажатия ~420 мс (ПКМ для приказов армиям / контекстного меню дипломатии), защитное окно `PostPinchGrace` (80 мс) против случайных кликов при отпускании одного из двух пальцев и игнорирование 3-го+ пальца.
4. **`Scripts/Input/TouchCameraAdapter.cs`**:
   - Обрабатывает только `InputEventScreenTouch` и `InputEventScreenDrag` — для мыши и клавиатуры мгновенно возвращает `false` (нулевое влияние на ПК).
   - Простой 2-строчный интерфейс подключения к `GlobePresentation.cs` (`TryHandleInput` в `_GuiInput` + `Tick` в `_Process`) с событиями `PanRequested`, `ZoomRequested` (знак направления совпадает с `WheelUp`/`ZoomGlobeAt`), `TiltRotateRequested`, `TapRequested`, `LongPressContextRequested`.
5. **Инфраструктура экспорта и проверки**:
   - `export_presets.cfg`: добавлен `[preset.1]` `Android (Dev / Debug)` (`arm64-v8a`, Landscape, Immersive, `ETC2/ASTC`, `minSdk=24`, `targetSdk=35`, исключены все editor/desktop аддоны и DLL) без изменения `[preset.0]` (`Windows Desktop`).
   - `Tools/android-doctor.ps1`: быстрая read-only диагностика хоста и проекта (поддержка `-Json`, совместимость с `pwsh` и Windows PowerShell 5.1).
   - `Tools/build-android.ps1`: воспроизводимая сборка APK/AAB в изолированном staging-каталоге `build/.export-android-*` с автозакреплением JDK 17 + 64-bit `.NET` и опциональными `-Install` / `-Launch` через `adb`.
   - `Tools/FormableTests/AndroidPlatformTests.cs`: headless-тест-сьют `--android-platform-only`.

---

## 4. Точки стыковки со смежными подсистемами

- **Для `@graphics-aaa` (`Scripts/gfx/GlobePresentation.cs` и шейдеры)**:
  - В `_GuiInput`: `if (_touchAdapter.TryHandleInput(inputEvent)) { AcceptEvent(); return; }` (с подпиской на `DragCamera`, `ZoomGlobeAt`/`ZoomFlatMapAt`, тап-выбор и `ProvinceCommanded` на долгое нажатие).
  - В `_Process`: `_touchAdapter.Tick(delta);` и `_touchAdapter.Cancel();` при `LoadingInputBlocked`.
  - Проверить сохранение контракта `PackedMapShader` (ровно 12 сэмплеров) при любых правках `globe_map.gdshader`, а также опциональное отключение мировых подписей провинций на `PlatformGraphicsTier.MobileLow` (по выводам `#192`).
- **Для `@ui-clarity` (`Scripts/ui/Shell/ShellOrchestrator.cs` и HUD)**:
  - При `NOTIFICATION_RESIZED` / пересчёте раскладки вызывать `GodotPlatformBridge.Refresh(viewportHeight)` и применять `PlatformProfile.Current.SafeArea.ApplyToViewport(w, h)` к корневым контейнерам HUD.
  - Масштабировать интерактивные кнопки на тачскринах через `PlatformProfile.ClampTouchTarget(height)` и `PlatformProfile.Current.UiScaleFactor`.
  - Скрывать кнопки «Открыть папку» при `!PlatformProfile.Current.IsDesktopFileDialogSupported`.

---

## 5. Дорожная карта до играбельного APK и Google Play AAB

### M0 — Фундамент и диагностика *(ВЫПОЛНЕНО)*
- [x] Аудит проекта, хоста, GDExtensions, шейдеров и Decision-1 верификация.
- [x] `PlatformProfile`, `GodotPlatformBridge`, `TouchGestureMath`, `TouchCameraAdapter`.
- [x] `export_presets.cfg` (`Android (Dev / Debug)`), `Tools/android-doctor.ps1`, `Tools/build-android.ps1`, `AndroidPlatformTests`.

### M1 — Первый запускаемый Dev-APK на устройстве
1. Установить недостающие инструменты хоста:
   - Android SDK (`platform-tools`, `build-tools;35.0.1`, `platforms;android-35`, опционально `ndk;28.1.13356709`, `cmake;3.10.2.4988404`) + задать `ANDROID_HOME`.
   - Выполнить `"C:\Program Files\dotnet\dotnet.exe" workload install android`.
   - В Godot Editor Settings -> Export -> Android -> Java SDK Path указать `C:\Program Files\Microsoft\jdk-17.0.13.11-hotspot`.
2. Добавить в `DealOrForce.csproj` условные свойства для Android (`!$(TargetFramework.Contains('android'))` вокруг `ServerGarbageCollection=true`; для Android — Workstation GC, `<PublishTrimmed>false</PublishTrimmed>` для сохранения F# `FSharp.Core`).
3. Реализовать `AndroidDataExtractor` (при первом старте на Android распаковывает `game/` из APK в `OS.GetUserDataDir()/game` с файлом-штампом версии и прогресс-баром на экране загрузки).
4. Подключить `TouchCameraAdapter` в `GlobePresentation.cs` и `GodotPlatformBridge.Refresh` в `ShellOrchestrator.cs`.
5. Собрать первый APK через `Tools\build-android.ps1 -Godot <path> -Install -Launch` и снять logcat-профиль запуска.

### M2 — Мобильная эргономика и UX 38 000 провинций
- Fat-finger picking fallback (кольцевой сэмпл 16–24 dp в карте `province_ids`, если точный тап попал в границу/воду, или компактное всплывающее меню выбора мелкой островной провинции).
- Подтверждение необратимых приказов и явная кнопка «Приказать / Переместить» при выбранной армии в дополнение к долгому нажатию.
- Мобильный профиль HUD (`PlatformProfile.Current.UiScaleFactor` + `SafeArea` + скрытие избыточных мировых подписей на `MobileLow`).
- Термо-/энергосбережение в `PerformanceGovernor`: `Engine.MaxFps = 30` на паузе/покое, 60 FPS при свайпах и прокрутке времени, пауза рендера по `NOTIFICATION_APPLICATION_PAUSED`.

### M3 — Память, текстуры и производительность
- Профилирование PSS на реальном устройстве 4–6 ГБ RAM (цель: < 900 МБ PSS на поздней кампании); потоковая загрузка BDF3 для снижения пикового потребления RAM и атомарная замена файла сохранения (`tmp` -> `fsync` -> `rename`) на случай внезапного убийства процесса в фоне (`lmkd`).
- Опциональная половинная политическая/рельефная текстура и ASTC-сжатие на `ShouldUseCompactTextureBudget`.
- Опциональная кросс-компиляция `dof_kernels` (Rust) под `aarch64-linux-android` (NDK r28b `28.1.13356709`) с обязательным выравниванием сегментов `.so` под **16 KB page size** (требование Android 15+ / Google Play) и проверкой битовой идентичности `CampaignFingerprint`.
- **Мобильный профиль модов**: разрешены только валидированные data-only моды (JSON/YAML/графика); внешние subprocess-воркеры (Go/Rust/компилируемые во время работы) явно отклоняются через `PlatformProfile.IsExternalWorkerModSupported = false`.

### M4 — Релизный AAB (Google Play)
- Сначала доказать стабильный untrimmed `arm64-v8a` Dev-APK (`PublishTrimmed=false` — решение `#198`, агрессивный тримминг/AOT на первой сборке отклонён Decision-1 с вероятностью `p=0.029`, 97% уверенности), проверив JSON/BDF3/F# round-trip.
- Выделенный релизный пресет `Android (Release AAB)` (`gradle_build/use_gradle_build=true`, `export_format=1`), адаптивная иконка `432x432` + `192x192`, проверка выравнивания `.so` под 16 KB, подпись release keystore (вне репозитория), проверка на 3 устройствах (бюджетный телефон 4 ГБ / флагман / планшет).

---

## 6. Вариант B (одобрено): Тонкая Android-оболочка `deal-or-force-android`

Пользователь утвердил **Вариант B** — отдельный легковесный Godot 4.8 .NET проект и репозиторий **`D:\Godot Projects\deal-or-force-android\`**, который не форкает симуляцию, а разделяет чистое C#-ядро и платформенный слой с основным репозиторием `deal-or-force-godot-4-8` через MSBuild-свойство `$(DoFCoreDir)`.

### 6.1. Локальная структура `D:\Godot Projects\deal-or-force-android\`
- `project.godot` — чистый проект Godot 4.8 (`C#`, `Mobile`), `1280x720` (`canvas_items`/`expand`, Landscape), `emulate_mouse_from_touch=true`, `ETC2/ASTC`, **0 десктопных/редакторских аддонов** (`fennara`, `ziva_agent`, `godotsteam`, `godot_mcp`, `luau` отсутствуют).
- `DealOrForceAndroid.csproj` + `DealOrForceAndroid.sln` — `Godot.NET.Sdk/4.8.0-beta.3`, `net10.0`, `DOF_ANDROID_SHELL`, `<ServerGarbageCollection>false</ServerGarbageCollection>`, `<PublishTrimmed>false</PublishTrimmed>`. Подключает:
  - `$(DoFCoreDir)\Scripts\Simulation\**\*.cs` + `StructuredData.Dependencies.props` (`YamlDotNet`, `DealOrForce.Rules.fsproj`)
  - `$(DoFCoreDir)\Scripts\Platform\{PlatformProfile,TouchGestureMath,GodotPlatformBridge}.cs`
  - `$(DoFCoreDir)\Scripts\Input\TouchCameraAdapter.cs`, `$(DoFCoreDir)\Scripts\GamePaths.cs`.
- `export_presets.cfg` — `[preset.0]` `Android (Debug APK)` (`arm64-v8a`, `minSdk=24`, `targetSdk=35`, `ETC2/ASTC`) и `[preset.1]` `Android (Release AAB)` (Gradle AAB).
- `Scenes/AndroidShell.tscn` + `Scripts/AndroidShellEntry.cs` (+ `.uid`) — загрузочный мобильный стенд: инициализирует `GodotPlatformBridge`, динамически применяет `SafeAreaInsets`, прогоняет проверку чистого C#-ядра (`Campaign(42)` + `GameDate` + `CampaignFingerprint`) и выводит живую телеметрию `TouchCameraAdapter` (пан/щипок/наклон/тап/долгое нажатие).
- `Tools/sync-core.ps1` — проверка связки `$(DoFCoreDir)` и помощник подключения/обновления git-подмодуля `core`.
- `Tools/build-apk.ps1` — сборка Debug APK / Release AAB с автозакреплением 64-bit `.NET` + JDK 17 и опциональной установкой `-Install -Launch` по `adb`.
- `.gitignore`, `icon.png`, `README.md` (RU + EN).

### 6.2. Приоритет разрешения `$(DoFCoreDir)` (без рассинхрона ПК ↔ Android)
1. Явный параметр `-p:DoFCoreDir=<путь>` или переменная окружения `DOF_CORE_DIR`.
2. Git-подмодуль `./core` (для автономного клонирования и CI).
3. Соседний локальный репозиторий `..\deal-or-force-godot-4-8` (автоматически на рабочей станции разработчика).

### 6.3. Шаги публикации на GitHub (после выбора видимости: `--private` или `--public`)
```powershell
cd "D:\Godot Projects\deal-or-force-android"

# 1. При желании закрепить основное ядро как git-подмодуль:
powershell -ExecutionPolicy Bypass -File Tools\sync-core.ps1 -InitSubmoduleUrl https://github.com/<owner>/deal-or-force-godot-4-8.git

# 2. Создать удалённый репозиторий через GitHub CLI и отправить ветку main (выберите --private или --public):
gh repo create deal-or-force-android --private --source=. --remote=origin --push
# или для публичного репозитория:
# gh repo create deal-or-force-android --public --source=. --remote=origin --push
```

---

## 7. Мобильный UX: 4-режимная панель тапа (AoH-стиль) и адаптация текстов под мобилы

### 7.1. Исследование мобильного управления AoH2 / AoH3 и вердикты Decision-1
По результатам веб-исследования (`perplexity/sonar-pro-search`) и калиброванного голосования `google/gemini-decision-1`:
- В мобильных версиях *Age of History II / III* режимы взаимодействия с картой вынесены в верхний угол рядом с датой/скоростью в виде явных кнопок переключения режима, но главная критика игроков к AoH3 mobile — слишком мелкие кнопки и неадаптированные десктопные формулировки.
- **Decision-1 вердикты**:
  1. **Поведение режима «Приказ» (`Order`)**: `one_shot_with_sticky_toggle` (**73%**) — по умолчанию одноразовый (после одного тапа-приказа автоматически возвращает в `Select`, исключая случайные мисклики), а повторное нажатие кнопки «Приказ» включает фиксацию (`Sticky`) для цепочки приказов.
  2. **2-пальцевая навигация в режиме «Рамка» (`BoxSelect`)**: `YES` (**p=0.917, 92%**) — 1 палец рисует прямоугольник выделения, 2 пальца одновременно зумируют и двигают карту без выхода из режима.
  3. **Расположение панели режимов**: `top_corner_safe_area_inset` (**88%**) — верхний угол внутри `SafeAreaInsets` с кнопками `>= 48dp`.
  4. **Адаптация текстов (`тексты под мобилы`)**: `explicit_override_plus_deterministic_adapter` (**98%**) — без дублирования 28 000 ключей локализации.

### 7.2. Реализованный движок режимов тапа и Android-оболочка
- **`Scripts/Platform/TouchTapMode.cs`** *(чистый C#, 0 зависимостей от Godot)* + интеграция в `TouchGestureMath.cs` и `TouchCameraAdapter.cs`:
  - `TouchTapMode.Select` (**Выбор / ЛКМ**) — тап выбирает, свайп двигает карту, удержание ~420 мс отдаёт приказ.
  - `TouchTapMode.Order` (**Приказ / ПКМ**) — одиночный тап мгновенно отдаёт приказ без удержания (`OneShotOrderMode` по умолчанию + `PressOrderModeButton()` для фиксации `Sticky`).
  - `TouchTapMode.BoxSelect` (**Рамка**) — 1-пальцевый драг генерирует `BoxSelectUpdated` / `BoxSelectCompleted` с нормализованным прямоугольником `TouchScreenRect`, а 2 пальца зумируют и двигают карту.
  - `TouchTapMode.Pan` (**Карта**) — безопасная блокировка тапов и удержаний для свободного обзора карты.
- В `D:\Godot Projects\deal-or-force-android\Scripts\AndroidShellEntry.cs` добавлена живая верхняя панель-таблетка из 4 кнопок (`>= 48dp`, активная подсветка, индикатор `1× / фикс.`) и живая отрисовка рамки выделения (`_Draw()`) поверх экрана.

### 7.3. Аудит ПК-текстов локализации и `MobileTextAdapter`
Read-only аудит `game/localization/{english,russian}/` выявил основные классы ПК-фраз:
- **English**: `Right-click / RMB` (35), `Ctrl/Shift/Alt+` (35), `Left-click / LMB` (14), горячие клавиши `[F7]/[Space]` (5), `mouse wheel / hover` (3), `Quit to desktop`.
- **Russian**: `ПКМ / ЛКМ / правой/левой кнопкой` (48), `Ctrl/Shift/Alt+` (46), `колёсико / наведите курсор` (15), горячие клавиши `[F5]/[F7]/[Space]` (8), `Выход на рабочий стол` (1).
- Реализован **`Scripts/Platform/MobileTextAdapter.cs`** (0 аллокаций и 100% неизменность строк на Desktop при `!isTouchPrimary`; на тачскринах сначала проверяет явные ключи `mobile.override.*`, затем детерминированно удаляет теги `[F7]`/`[Space]`/`[Esc]` и заменяет `Quit to desktop` -> `Exit game`, `Выход на рабочий стол` -> `Выйти из игры`, `ПКМ` -> `Режим приказа / Удержание`, `ЛКМ` -> `Тап`, `Колёсико` -> `Щипок` с сохранением `{0}`-плейсхолдеров).
- Добавлены чистые таблицы `game/localization/{english,russian}/98_mobile_ux.json` (19 ключей EN+RU, 0 конфликтов).

---

## 8. Первый живой подписанный `.apk` (подтверждено 21.11.2026)

1. **Установленный Android SDK (user-local, без прав администратора)**:
   - Путь: `%LOCALAPPDATA%\Android\Sdk` (`ANDROID_HOME` и `ANDROID_SDK_ROOT` прописаны в пользовательском окружении).
   - Пакеты: `cmdline-tools;latest`, `platform-tools` (`adb.exe`), `build-tools;35.0.1`, **`build-tools;36.1.0`** (требование Godot `4.8.beta3.mono`), `platforms;android-35`, `platforms;android-36`.
   - Подпись: debug keystore `%APPDATA%\Godot\keystores\debug.keystore` и `%USERPROFILE%\.android\debug.keystore` (сгенерирован через JDK 17 `keytool`).
2. **`.NET` Android Runtime (важное открытие)**:
   - Установка глобального MSI-ворклоада `dotnet workload install android` требует прав администратора (UAC `ERROR_NOT_SUPPORTED` в неинтерактивной сессии).
   - Однако `Godot.NET.Sdk/4.8.0-beta.3` экспортирует `net10.0` Android APK через NuGet-пакет `Microsoft.NETCore.App.Runtime.Mono.android-arm64` полностью в пользовательском пространстве — **без прав администратора**!
3. **Собранный и проверенный артефакт (`EXPORT_EXIT=0`, `apksigner` Signed + Verified)**:
   - Исходный путь: `D:\Godot Projects\deal-or-force-android\build\android\DealOrForce-debug.apk`
   - Копия для установки на телефон: `C:\Билды DoF (DiskC)\DealOrForce-Android-Dev-2026-11-21.apk`
   - Размер: **49.11 МБ** (`51 493 158` байт, `316` записей ZIP)
   - SHA-256: `F5446A347F0CF001A04C48CE505751252646F6979937012A29ACB9EC303825AF`
   - Содержимое внутри APK: `AndroidManifest.xml`, `classes.dex`..`classes13.dex`, `lib/arm64-v8a/libgodot_android.so`, `lib/arm64-v8a/libmonosgen-2.0.so`, `DealOrForceAndroid.dll`, `DealOrForce.Rules.dll`.

---

## 9. Milestone M2 — Первый полноценный играбельный `.apk` (21.11.2026)

После успешного запуска M1-стенда на реальном 5.4" Android-телефоне пользователя (и исправления бага пустого `AppContext.BaseDirectory` на Android Mono в коммите `507d0a0`) в репозитории `D:\Godot Projects\deal-or-force-android\` собран **полноценный играбельный APK** с полным исходным кодом игры (Главное меню, 3D-глобус / плоская карта, кампания 2024, все обновления Wave 1–3 UI/графики/сражений и 4-режимная панель тапа):

1. **Архитектура M2 (без единой правки в основном `Scripts/` и без раздувания Git)**:
   - `Tools/sync-core.ps1`: синхронизирует в `.gitignore`-каталоги `Scripts/`, `Scenes/{MainMenu,Main,ScenarioMapEditor}.tscn`, `game/{gfx,fonts,shaders,sound}` и собирает версионированный архив `res://android_game_fs.zip` (`189.3 МБ`, `6 078` файлов) + `android_game_fs.version`.
   - `Scenes/AndroidBoot.tscn` + `AndroidScripts/AndroidBootEntry.cs`: при первом запуске на телефоне закрепляет `AppContext.BaseDirectory` на `user://`, потоково (чанками по 1 МБ, без пика RAM) распаковывает `android_game_fs.zip` в `user://game/` с прогресс-баром и версионным штампом `.android_pack_version` (повторные запуски мгновенно пропускают распаковку!) и переходит в `res://Scenes/MainMenu.tscn`.
   - `AndroidScripts/GlobePresentation.AndroidBridge.cs` + autoload `AndroidScripts/AndroidCampaignOverlay.cs`: `partial class GlobePresentation` напрямую связывает `TouchTapMode.BoxSelect` с `DispatchBoxUnitSelection(Rect2)` и форсирует мобильный `quality_tier`, а оверлей размещает в правом верхнем углу внутри `SafeAreaInsets` 4-кнопочную панель режимов (`Выбор` / `Приказ 1×|фикс.` / `Рамка` / `Карта`, `>= 48dp`) поверх живой кампании.
   - `AndroidScripts/AndroidDesktopStubs.cs`: минимальная заглушка `ExportVerification` (0 десктопных аддонов/GDExtension).
2. **Собранный и проверенный играбельный APK (`0` ошибок компиляции, `APKSIGNER_VERIFY_EXIT=0`)**:
   - Путь для установки на телефон: **`C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk`** (также сохранён лёгкий диагностический стенд `DealOrForce-Android-Dev-2026-11-21.apk` `49.11 МБ`).
   - Размер: **388.98 МБ** (`407 877 517` байт, `10 208` записей ZIP, `arm64-v8a`, `versionCode=2`).
   - SHA-256: `7CEE61EF3A52A72E9F7FDD2AB9CB2D9B2FBF0B8943AF93896ED8B50E6E2ECF50`.
   - Проверено внутри APK: `AndroidManifest.xml`, `classes.dex`..`classes13.dex`, `libgodot_android.so`, `libmonosgen-2.0.so`, `DealOrForceAndroid.dll` (`11 128 КБ`), `DealOrForce.Rules.dll`, `android_game_fs.zip` (`193 837.5 КБ`), `AndroidBoot.tscn`, `MainMenu.tscn`, `Main.tscn`.

## 10. Milestone M2.1 (`versionCode=4`, 2026-11-21) — Полевой фикс ARM64-карты, быстрая загрузка, звук, иконки и глобальный `Loc.TextAdapter`

### 10.1. Корневая причина ошибки загрузки карты на реальном Android-телефоне и её исправление
- **Симптом на реальном 5.4" телефоне**: 3D-глобус и логотип *Deal or Force* успешно отрендерились, `Campaign.CreateBootstrap` прошёл полностью, но `BuildPoliticalMap` -> `MapRasterCatalog.TryBuildProvinceTopology` выдал:
  `game/map_data/provinces.png + definition.csv could not be loaded (provinces.png was cut from a different region layer than this build draws...)`.
- **Точный forensic-разбор**:
  - На Windows x64 тот же самый `android_game_fs.zip` даёт точное совпадение `RegionFingerprint(states) == 3954006852556129685` (`34 138` провинций, `diffStates=0`, `landControlDiff=0`).
  - В `TerritorialGrowth.GrowPolar` (`Scripts/Simulation/TerritorialGrowth.cs:270-393`) для полярной нарезки Антарктиды (`~1.5M` ячеек) вызываются платформенные трансцендентные функции `MathF.Cos` / `MathF.Sin` / `MathF.Exp` / `MathF.Atan2` (`(int)(100f * MathF.Exp(...))`). Разница в **1 ULP** между `ucrtbase.dll` (Windows x64) и Android Bionic `libm.so` + ARM64 FMA смещала пограничные ячейки Антарктиды, ломая 64-битный `RegionFingerprint(states)` и проверку `ProvinceTopology.FromColorRaster:371`.
- **Решение (0 мс на Windows x64, кросс-архитектурная совместимость для Android ARM64 / Linux / macOS ARM64)**:
  - В `Scripts/Simulation/MapDataRaster.cs` добавлен `MapRasterCatalog.ReconcileCrossArchitectureRegions(states, control, width, height)` (вызывается в конце `TerritorialTopology.Apply` до запуска параллельных задач `BuildPoliticalMap` — ноль гонок данных) + одноразовый prefetch-кэш декодированного `(RgbDataMap, definitions)`, очищаемый сразу после потребления в `TryBuildProvinceTopology` и в `ResetCatalog()`.
  - На Windows x64 проверка остаётся 100% строгой; на ARM64/non-Windows при расхождении хеша растр `states`/`control` выравнивается по эталонной паре `provinces.png + definition.csv` с обязательной верификацией `RegionFingerprint(authoredStates) == stamped` (`3954006852556129685`). Покрыто автотестом в `PerfDeterminismTests` (`54/54 PASS`).

### 10.2. Пакет улучшений, вошедших в пересборку `versionCode=4` (`commit a02d883`)
1. **Фикс карты ARM64** (`ReconcileCrossArchitectureRegions` + `res_map_data_leaked=0` — `map_data/*` никогда не попадает под lossy ETC2/ASTC импорт Godot).
2. **Ускорение холодной загрузки и распаковки (Wave 4 `#346` + `#347`)**:
   - Холодное создание кампании (Release `SimBench`): **`5 783 мс -> 2 257–2 338 мс`** (**`-60%`**, **2.5× быстрее**); `Parallel Loc.ResetCatalog` **`12.0 мс`** (427 файлов), `TryBuildProvinceTopology` **`534.8 мс`** (`34 138` провинций).
   - `AndroidBootEntry.cs`: стриминг архива чанками **4 МБ** (`1 МБ` буфер `FileStream`), переиспользуемый **256 КБ** буфер распаковки (без per-file 81 КБ аллокаций и `utimensat`), кэш уникальных директорий `HashSet<string>` (~140 `mkdir` вместо 6 000+), троттлинг UI-прогресса до `100 мс`, `CompressionLevel.NoCompression` (**STORE**) для всех сжатых медиа в `android_game_fs.zip` (`stamp=0.8.3.5-e11274cb-6011-194501360-m2_1-store-music1`).
3. **Глобальный `Loc.TextAdapter` (`MobileTextAdapter`)**: подключён в `GodotPlatformBridge.Refresh()` и вызывается в начале `DealOrForceGame._Ready()` до `LoadingScreen` — на Android строки вроде `"Quit to desktop"` / `"Выход на рабочий стол"` глобально заменяются на `"Exit game"` / `"Выйти из игры"`, а `[Space]`/`[F7]`/`ПКМ`/`ЛКМ` очищаются автоматически (Desktop: `0 B` аллокаций).
4. **Официальный Brand Kit и полный звук**: адаптивные иконки `192×192` / `432×432` (`25` mipmap-ресурсов), boot splash (`a3b5754`), полная музыка + SFX (`default_bus_layout.tres`, `46` файлов в `game/music/`, `87` в `game/sound/`, `114` импортированных аудиопотоков в `.pck`, `abdbb6b`).

### 10.3. Верификация артефакта (`versionCode=4`)
- Коммит тонкой оболочки: `D:\Godot Projects\deal-or-force-android\` -> `main` **`a02d883`** (0 remotes, не пушилось).
- Подпись: `apksigner verify --print-certs` -> **`EXIT 0`**; `aapt2 dump badging`: `com.bayterealization.dealorforce`, `versionCode='4'`, `versionName='0.8.3.5'`, `targetSdkVersion='36'`, `arm64-v8a`.
- Доставленный файл: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk` (**`463.31 MB` / `485 810 979` байт**, `SHA256_MATCH=True`).
- **SHA-256**: `D3CA7CFCB25949A26DF5495181A5DD05B9773D2C0608025D5EEB7CD8F4253642`.

---

## 11. Milestone M2.2 (`versionCode=6`, `0.8.3.6`, 22.11.2026) — Dual-ABI `arm64-v8a` + `x86_64`, встроенный тач-HUD, предзапечённый кэш карты и 4-поточный запуск

### 11.1. Ключевые архитектурные и UX-улучшения (`#487`, `#506`)
1. **Dual-ABI `arm64-v8a` + нативный `x86_64`**: оба пресета (`Android (Debug APK)` и `Android (Release AAB)`) в `D:\Godot Projects\deal-or-force-android\export_presets.cfg` собираются с `architectures/arm64-v8a=true` и `architectures/x86_64=true` (`versionCode=6`, `versionName="0.8.3.6"`), что убирает ARM-трансляцию на ПК-эмуляторах Android (включая BlueStacks) и сохраняет полную нативную работу на смартфонах.
2. **Встроенная панель тач-режимов, масштаб HUD и подавление клавиатуры**:
   - Удалён громоздкий плавающий оверлей `AndroidCampaignOverlay` — теперь он работает как лёгкий zero-UI мост жизненного цикла, а 4-кнопочная панель режимов касания в стиле Sovereign Brass интегрирована непосредственно в `ShellMapBarView` и отображается только в живой кампании.
   - Применены `Ui.FocusInputIfDesktop` (подавление нежелательной экранной клавиатуры на тач-устройствах), тач-масштабирование HUD и мобильные лимиты FPS/LOD.
3. **Предзапечённый бинарный кэш карты и 4-поточная инкрементальная распаковка**:
   - `Tools/sync-core.ps1 -PrebakeMapCache` предзапекает и сверяет в `android_game_fs.zip` оба бинарных кэша загрузки карты: **`cache/map_boot_v2.bin`** (`3 187 782` Б, `DMBC` v2, `fp=3954006852556129685`) и **`cache/hydrology_v2.bin`** (`2 672 675` Б, `DMHC` v2, STORE).
   - `AndroidBootEntry.cs` использует до **4 параллельных потоков `ZipArchive`** по непересекающимся срезам записей и атомарный **инкрементальный манифест** `.android_pack_manifest` (`FullName -> Length:Crc32`), благодаря чему повторные запуски и обновления APK пропускают неизменившиеся файлы вместо перераспаковки 200+ МБ.
   - В обе ABI упакованы MIT-библиотеки из `StructuredData.Dependencies.props`: `CommunityToolkit.HighPerformance 8.4.2`, `System.Numerics.Tensors 11.0.0`, `K4os.Compression.LZ4 1.3.8`; подтверждено `UNGUARDED_GCSETTINGS=0`.

### 11.2. Верификация артефакта (`versionCode=6`, `0.8.3.6`, `#506`)
- **Доставленный APK**: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk` (**`522,02 МБ` / `547 376 852` байт**, `SHA256_MATCH=True`).
- **SHA-256**: `0C4CE8B977E89BE616966CCC449B5F5DA3A7288C64FBB1890CF647B4939B41B3`.
- **Подпись и манифест**: `apksigner verify` **`EXIT 0`**, `native-code: 'arm64-v8a' 'x86_64'`, `imported_audio=114`, `mipmaps=25`, `res_map_data_leaked=0`.
- **Честная граница проверки**: настольные юнит-тесты (`--android-platform-only` `102/102`, `--map-boot-cache-only` `87/87`) и проверка упаковки Dual-ABI прошли полностью; живой прогон кадров и тач-ввода `versionCode=6` на физическом смартфоне и в эмуляторе ожидает установки пользователем.

---

## 12. Milestone M3 (`versionCode=7`, `0.8.3.7`, 22.11.2026) — Чёткий рендер карты, верхне-правый тач-HUD, 4 бинарных кэша и Dual-ABI APK

### 12.1. Визуальная чёткость, мобильный HUD и ускорение старта (`#540`, `#548`, `#549`)
1. **Чёткость изображения на смартфонах и эмуляторах (`GlobePresentation.Setup.cs`, `GlobeWeather.cs`, `project.godot`)**:
   - Установлены `<= 2048` по длинной стороне, `RenderScale = 1.0`, `Msaa2X`, `Anisotropy4X`, `TextureMipmapBias = -0.35`, `ScreenSpaceAA = Disabled`, `default_texture_filter = 1` и мипмапы подписей/спрайтов; на тач-устройствах по умолчанию `CloudsEnabled = false` (десктопный `relief_displacement` не затронут).
2. **Верхне-правая панель тач-режимов (`Scripts/ui/Shell/ShellTouchModeBarView.cs`)**:
   - Вынесена рядом с `DatePlate` (`168×102` px) с нулевым пересечением с миникартой; проверено `ShellCoherenceTests` (`34/34`), `AndroidPlatformTests` (`117/117`) и `BilateralPeaceTests`.
3. **GC и быстрая загрузка (`AndroidBootEntry.cs`, `DealOrForceAndroid.csproj`)**:
   - Включён `AndroidEnableSGenConcurrent=true`; быстрый пропуск актуального кэша (`< 5 мс`) и буферизованная распаковка `64 КБ`; подтверждено `UNGUARDED_GCSETTINGS=0` (единственное обращение к `LargeObjectHeapCompactionMode` в `GlobePresentation.cs:483` защищено `!OperatingSystem.IsAndroid() && !OperatingSystem.IsIOS()` + `catch (PlatformNotSupportedException)`).
4. **Все 4 предзапечённых бинарных кэша внутри `assets/android_game_fs.zip` (`6 136` файлов, `184 857 873` Б сжато / `195 005 873` Б сырых)**:
   - `cache/map_boot_v2.bin`: `3 187 782` Б сырых (`1 705 190` Б в zip, `magic=0x43424D44` `'DMBC'`, `ver=2`);
   - `cache/hydrology_v2.bin`: `2 672 675` Б сырых (`2 673 085` Б в zip, `magic=0x43484D44` `'DMHC'`, `ver=2`);
   - `cache/political_rasters_v1.bin` (LZ4): `4 344 697` Б сырых (`4 345 362` Б в zip, `magic=0x43504D44` `'DMPC'`, `ver=1`);
   - `cache/lakes_boot_v1.bin` (Brotli): `108 052` Б сырых (`105 116` Б в zip, `magic=0x434C4D44` `'DMLC'`, `ver=1`).

### 12.2. Независимая верификация артефакта (`versionCode=7`, `0.8.3.7`, `#548`)
- **Доставленный APK**: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-21.apk` (побайтово совпадает с `D:\Godot Projects\deal-or-force-android\build\android\DealOrForce-debug.apk`, `SIZE_MATCH=True`, `SHA256_MATCH=True`).
- **Точный размер**: **`550 219 570` байт** (`524,73 МБ`).
- **SHA-256**: `D48229BB43A7AC8FD2374368B6A051208DB58F21AB847B4607F85E939D467CB5`.
- **Манифест, ABI и подпись**: `com.bayterealization.dealorforce`, `versionCode='7'`, `versionName='0.8.3.7'`, `targetSdkVersion='36'`, **`native-code: 'arm64-v8a' 'x86_64'`** (`lib/arm64-v8a=11` и `lib/x86_64=11` `.so`; MIT NuGet DLL в обеих ABI), `apksigner verify --print-certs` **`EXIT 0`**.
- **Честная граница проверки**: живой визуальный осмотр чёткости карты, плавности кадров и тач-ввода `versionCode=7` на физическом смартфоне и в эмуляторе BlueStacks ожидает установки APK пользователем.

---

## 13. Milestone M4+M5 (`versionCode=8`, `0.8.3.8`, 22.11.2026) — Release APK (`−100,23 МБ`), честный `STORED(0)`, 5 кэшей и `−66%` архива первого старта

### 13.1. Ключевые ускорения упаковки, старта и рендера (`#584`, `#602`, `#603`, `#606`, `#612`)
1. **Уменьшение APK на `−100,23 МБ` и `android_game_fs.zip` на `−66%`**:
   - Переход на чистый релизный экспорт `--export-release` / `-c Release` (`PDBS=0`, `APPLICATION_DEBUGGABLE_COUNT=0`).
   - Дедупликация 4 440 картинок `game/gfx/` (уже лежащих в `.pck`) и исключение `cache/*` из внешнего `.pck` (`duplicated_outer_cache=0`): `assets/android_game_fs.zip` сжался со `195,0 МБ` (`6 136` файлов) до **`66,21 МБ` (`69 425 132` Б, `1 692` файла)**.
   - Новый CLI `Tools/ApkZipTool/` (`.NET 10`) исправил баг псевдо-сжатия PowerShell 5.1 и перевёл `assets/android_game_fs.zip`, `114/114` аудиопотоков, `4460/4460` `.ctex`, `1/1` `.sparsepck` и внутренние медиа/бинарники в честный **`STORED` (метод `0`)** перед `zipalign -f -p 4` и `apksigner`.
2. **Все 5 предзапечённых кэшей (`fp=3954006852556129685`)**:
   - `map_boot_v2.bin` (`4 926 493` Б, **`terrainSlots=true`** — убрано декодирование `terrain.png` на старте);
   - `hydrology_v2.bin` (`2 672 675` Б), `political_rasters_v1.bin` (`4 344 697` Б), `lakes_boot_v1.bin` (`108 052` Б);
   - `sprite_index_v1.json` (`365 790` Б, `sprites=4502`, `shaders=5`) — устранено сканирование ~4 500 файлов на старте.
3. **Мобильный рендер, память и UI (`#584`, `#606`)**:
   - Пропуск 100 МБ пустого `_packedIds` в `gl_compatibility`, пропуск дубля `TerritorialTopology.Apply` и 1,63 МБ GeoJSON-лога, освобождение 50 МБ prefetch-растра, LZ4-кэш `terrain_mobile_v1.bin` (`128px`), отказ от `MenuGlobe` GPU→CPU readback, `MobileEffectiveRenderScale` (`0.65..1.0`), 11 ленивых модальных экранов и 12 UI-правок (`SafeArea`, `TooltipKeeper`, `ClipText`).

### 13.2. Верификация релизного APK v8 (`0.8.3.8`, `#612`)
- **Путь**: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-22.apk` (+ `.sha256`), коммит оболочки `2a8a838` (`0` remotes).
- **Точный размер**: **`445 123 599` Б (`424,50 МБ`)** (`SHA256_MATCH=True`).
- **SHA-256**: `08C85E5B7C186DDDD4FF36A6058F8A97377A577BE93785A4CA10E84DBCD6AC9A`.
- **Манифест, ABI и подпись**: `com.bayterealization.dealorforce`, `versionCode='8'`, `versionName='0.8.3.8'`, `minSdk='24'`, `targetSdk='36'`, `native-code: 'arm64-v8a' 'x86_64'` (`arm64_so=11`, `x64_so=11`), `APKSIGNER_EXIT=0`, `VERIFY_STORED failures=0`, `PDBS=0`, `UNGUARDED_GCSETTINGS=0`.

---

## 14. Milestone M6 (`versionCode=9`, `0.8.3.9`, 22.11.2026) — Выделенный поток `DoF-Sim`, полировка тач-UI и финальный Release APK v9

- **Что вошло в синхронизацию (`#635`, `#636`, `#637`)**:
  - `BackgroundSimRunner.cs` (`DoF-Sim`) + `CampaignRenderSnapshot` + `GlobePresentation` `TryEnterRead` (36 gfx-файлов) — симуляция больше не блокирует UI/рендер;
  - Многопоточный `EconomyCore` (`−43%`), битсет `MovementRules` (`−42%`), параллельный `AiStatecraft`, `DefconSystem` (`−54%`), 1000-летняя защита `PopulationFolding`;
  - UI/тач: фикс `CampaignFlagMount`, `SafeArea` в `ShellXpStripView`/`FullWorkspace`, `DoctrineComfortUI` (кнопка `×` в шапке «Книги войны»), `StartupIntro`, обновлённый `98_mobile_ux.json` (`29 315` RU, `0` проблем);
  - Все 5 предзапечённых кэшей (`map_boot_v2` `terrainSlots=true`, `hydrology_v2`, `political_rasters_v1`, `lakes_boot_v1`, `sprite_index_v1`, `fp=3954006852556129685`) и `assets/android_game_fs.zip` (`66,21 МБ`, `1 692` файла, `STORED` метод `0`).
- **Верификация артефакта v9 (`#636`)**:
  - **Путь**: `C:\Билды DoF (DiskC)\DealOrForce-Android-Playable-2026-11-22.apk` (+ `.sha256`), коммит `731bb11` (`0` remotes);
  - **Размер**: **`445 205 613` Б (`424,58 МБ`)** (`SHA256_MATCH=True`);
  - **SHA-256**: `1EFCBB5C8A01F5E20ED56C5C21FDAB645EF4B8CEBA0605F6CEE559EB49C4FF01`;
  - **Манифест и подпись**: `versionCode='9'`, `versionName='0.8.3.9'`, `minSdk='24'`, `targetSdk='36'`, `native-code: 'arm64-v8a' 'x86_64'`, `APKSIGNER_EXIT=0`, `VERIFY_STORED failures=0`, `PDBS=0`, `APPLICATION_DEBUGGABLE_COUNT=0`, `UNGUARDED_GCSETTINGS=0`.
