# Облик карты, волна 2: единый `.gdshader`, широкая полоса `FrontierField`, приоритетная MSDF-типографика и настольный свет

18 ноября 2026, ветка `work/mapart` (основной коммит волны 2 — `df623347`, чистое слияние с `EntertAlpha` `6ffd5c0a` на Godot 4.8-Beta3 / `0.7.2.3-dev` — `d146b0a7`).

Волна 2 закрывает пункты «Чего нет в этом срезе» из [`MAP_LOOK_WAVE_1_2026_11_18_RU.md`](MAP_LOOK_WAVE_1_2026_11_18_RU.md) и развивает [`docs/design/graphics-2026-11-18/map-look.md`](design/graphics-2026-11-18/map-look.md) с полной обратимостью в 1 клик (`map_look = "classic"`), строгим сохранением 12 сэмплеров в Compatibility и без единого перебора провинций/клеток/городов/рек на главном потоке.

---

## Что сделано

### 2A. Единый канонический шейдер `game/shaders/globe_map.gdshader`

- Полный spatial-шейдер карты (~1 470 строк, раньше лежавший строковым литералом в `Scripts/gfx/GlobePresentation.cs` со склейкой `OccupationShaderFunctions` и `SeamSamplingFunctions()`) вынесен в **один** канонический файл [`game/shaders/globe_map.gdshader`](../game/shaders/globe_map.gdshader) (`1632` строки, `105 575` байт, валидный standalone `.gdshader` без двойных кавычек C#-экранирования).
- Создан чистый загрузчик [`Scripts/Presentation/MapShaderSource.cs`](../Scripts/Presentation/MapShaderSource.cs) (`ResourcePath = "res://game/shaders/globe_map.gdshader"`, `LocateFile()`, `LoadFull()` через `GameDataLoader.FindDataFile("shaders", "globe_map.gdshader")`).
- [`DealOrForce.csproj`](../DealOrForce.csproj) теперь копирует `game\**\*.gdshader` как `<Content CopyToOutputDirectory="PreserveNewest" />`; [`Tools/FormableTests/FormableTests.csproj`](../Tools/FormableTests/FormableTests.csproj) подключает `MapShaderSource.cs`.
- В [`Scripts/gfx/GlobePresentation.cs`](../Scripts/gfx/GlobePresentation.cs) `CreateGlobe()` загружает шейдер через `LoadGlobeShaderSource()` (`res://` через `Godot.FileAccess` → `GameDataLoader.FindDataFile` → `GD.Load<Shader>`), затем применяет прежние постпроцессоры `MapColorSpaceShader.Build` и `PackedMapShader.Build`.
- Удалены устаревшие C#-строки и их осиротевшие `.uid`: `Scripts/gfx/GlobeOccupation.cs(.uid)` и `Scripts/gfx/SeamSamplingShader.cs(.uid)`.
- Обновлены контрактные тесты в `GpuAtlasContractTests.cs`, `MapLookTests.cs`, `BorderRankTests.cs`, `FrontierFieldTests.cs`: все читают один и тот же `MapShaderSource.LoadFull()`.

### 2B. Широкая внутренняя полоса границ (Imperator) и потолок `FrontierField` = 20

- [`Scripts/Simulation/FrontierField.cs`](../Scripts/Simulation/FrontierField.cs): потолок расстояния `Cap` поднят с `8f` до **`20f`** (по оценке `google/gemini-decision-1`: `cap_20`, 63% vs `cap_16` 24% и `cap_24` 13%). Размер и формат буфера **не изменились** (R8 `5760×2880` на рабочем потоке, 0 лишней VRAM; шаг квантования `20 / 255 ≈ 0.0784` клетки/LSB).
- В `game/shaders/globe_map.gdshader` default-значение `frontier_cap` обновлено до `20.0`, а в [`Scripts/gfx/GlobePlateRepaint.cs`](../Scripts/gfx/GlobePlateRepaint.cs) (`ApplyFrontier`) `frontier_cap` теперь передаётся в материал при каждом обновлении поля.
- Настроены параметры дальней полосы в [`game/gui/78_map_look.json`](../game/gui/78_map_look.json) и [`Scripts/Simulation/MapLook.cs`](../Scripts/Simulation/MapLook.cs):
  - `map.look_far_band`: `0.30 → 0.36`
  - `map.look_far_band_pixels`: `3.0 → 4.5` (верхний кламп в `MapLook.Current()` расширен до `16f`)
  - В шейдере (`if (atlas_look > 0.5)`) сохранены `band_end = min(frontier_cap * 0.85, frontier_offset + look_far_band_pixels * id_aa)` (защита микрогосударств `В8` на среднем зуме) и плавная кривая затухания `pow(max(raw_far_band, 0.0), 1.35)`, благодаря которой полоса держит бархатный кант до `17` клеток от границы на мировом зуме (`В1`, `В2`), не заливая середину мелких стран и побережья (`1.0 - 0.5 * coast_far`). Выборка `texture(frontier_field, UV)` осталась в единственном буквальном виде.
- **Проливы (`game/map_data/crossings/00_crossings.json`)**: проверено автотестами `--crossings-only` (`CROSSINGS_OK checks=62`, `34 of 34 placed, 0 refused`) и `CartographyTests` (`6793/6793`): `oresund_bridge` и `messina_ferry` уже входят в базовые данные, а Магелланов пролив задокументирован в `00_crossings.json` как не имеющий водного разрыва на растре 5760×2880 (не добавлялся искусственно, что подтверждено `google/gemini-decision-1`: `p=0.909`).

### 2C. Типографика атласа, MSDF, приоритеты, реки/регионы и свет рабочего стола

- **Тёплая бумага на чернилах (`#efe4c8` на `#11161c`, только `atlas`)**: в `78_map_look.json`, `MapLook.DefaultColours` и `InterfaceSkin.cs` добавлены токены `map.look_label_fill` (`#efe4c8`), `map.look_label_outline` (`#11161c`), `map.look_capital_fill` (`#f7eed6` вместо резкого жёлтого золота), `map.look_region_fill` (`#e6d9bb`), `map.look_river_fill` (`#a8cedd`) и `map.look_river_outline` (`#0f1e29`). В `classic` сохраняются исходные токены `globe.*` из `00_skin.json`.
- **MSDF только для 3D-подписей карты**: в [`Scripts/ui/PresentationFonts.cs`](../Scripts/ui/PresentationFonts.cs) `MultichannelSignedDistanceField = true` с `MsdfPixelRange = 40` (`>= 2 ×` максимальной обводки `19 px` при `fontSize = 128`) и `MsdfSize = 64/48` включается только на ролях `map.countries` и `map.cities` (`IsMapLabelRole`), не трогая хинтинг мелких UI-шрифтов `ui.default`/`ui.display`.
- **Чистые правила приоритетов и плавного затухания ([`Scripts/Presentation/MapLabelPriority.cs`](../Scripts/Presentation/MapLabelPriority.cs))**:
  - Порядок (`OrderCityCandidates`): 1) столица игрока и выбранной страны → 2) остальные столицы → 3) города игрока/выбранной страны → 4) остальные города по значимости → 5) крупные реки (`PlaceRivers`, никогда не вытесняют города/столицы).
  - Жёсткие диапазоны зума с мягкими рампами прозрачности (`CountryTargetAlpha` — средний+дальний; `RegionTargetAlpha` — только средний зум `6.0..11.0` / `0.55..3.2`; `RiverTargetAlpha` — ближний+средний до `12.2` / `4.0`) и плавный пошаговый фейд (`StepOpacity`) без 1-кадровых скачков; выход за горизонт по-прежнему гасится мгновенно.
- **Регионы и крупные реки с рабочего потока**:
  - В `BuildPoliticalMap` в фоне считаются даунсэмплированные (`×4`) якоря регионов (`BuildRegionAnchorsDownsampled`, пул <= 48 `Label3D` в пространственных тайлах 12×6 в [`Scripts/gfx/GlobeRegionLabels.cs`](../Scripts/gfx/GlobeRegionLabels.cs), пропуск одноимённых микрогосударствам или уже показанному на экране городу названий и проверка экранного AABB против `_cityTaken`, `_starMarks` и `_regionTaken` после `UpdateCityLabels`) и якоря крупных рек [`Scripts/Presentation/RiverLabelLayout.cs`](../Scripts/Presentation/RiverLabelLayout.cs) (`BuildAnchors` по `HydrologyCatalog.RiverFeatures`, `scalerank <= 4`, дедупликация сегментов, 104 уникальные реки с лево-направо касательной).
  - Добавлена полная EN+RU локализация всех 104 рек: [`game/localization/english/325_map_rivers.json`](../game/localization/english/325_map_rivers.json) и [`game/localization/russian/325_map_rivers.json`](../game/localization/russian/325_map_rivers.json).
- **PC-only тёплый обод СЗ-лампы и виньетка (только `Forward+` + `atlas`)**:
  - В [`Scripts/gfx/GlobeAtmosphere.cs`](../Scripts/gfx/GlobeAtmosphere.cs) (`AtmosphereShader` + `ApplyAtmosphereLook`) и [`Scripts/ui/MapVignetteUI.cs`](../Scripts/ui/MapVignetteUI.cs) (`UpdateMapVignetteLook`, вызывается и при живом переключении в [`Scripts/ui/SettingsApply.cs`](../Scripts/ui/SettingsApply.cs)) добавлено тёплое подсвечивание СЗ-края и мягкая виньетка, управляемые `MapLabelPriority.AtmospherePolish(...)`. При `map_look = "classic"`, в `gl_compatibility` и `mobile` все новые термы обнуляются (`WarmLampRim = false`, `tabletop_warmth = 0.0`, `warmth = 0.0`).

---

## Бюджет текстурных выборок и арифметики (доказательство Compatibility 12)

Измерения на текущем файле `game/shaders/globe_map.gdshader` и постпроцессоре `PackedMapShader.Build(...)`:

| Показатель | Волна 1 | Волна 2 | Дельта |
|---|---|---|---|
| Сэмплеры Forward+ (`globe_map.gdshader`) | 27 (`26` `sampler2D` + `1` `sampler2DArray`) | **27** (`26` `sampler2D` + `1` `sampler2DArray`) | **0** |
| Сэмплеры Compatibility (`PackedMapShader`) | **12** (`9` прямых + `packed_ids` + `packed_lookups` + `packed_water_tiles`) | **12** (та же раскладка; `frontier_field` вырезан полностью) | **0** |
| Вхождений `frontier_field` после `PackedMapShader.Build` | 0 | **0** (замена буквального `texture(frontier_field, UV)` на `vec4(1.0)`) | **0** |
| Новые текстуры / VRAM | — | **0 байт** (`FrontierField` остаётся R8 `5760×2880`) | **0** |
| Скаляр/векторные униформы в `.gdshader` | 90 | **90** | **0** |
| Доп. фрагментная арифметика | — | `globe_map.gdshader`: `+1 pow` в ветке `if (atlas_look > 0.5)`; `AtmosphereRim` / `MapVignette` (только `Forward+` + `atlas`): `~10 ALU`, 0 доп. проходов/RT (< 0.1 мс GPU, укладывается с запасом в бюджет 4 мс) | минимальная, гейтирована |

---

## Проверка после слияния (`d146b0a7`, Godot 4.8-Beta3 SDK)

Все проверки пройдены из-под корня `D:\Godot Projects\dof-mapart` с `--artifacts-path "D:/Godot Projects/dof-mapart/tmp_logs/mapart-build"` (длинный полный прогон запущен скрытым WMI `ShowWindow=0`, лог `tmp_logs/mapart-wave2-full.log`):

- `dotnet build DealOrForce.csproj` (`Godot.NET.Sdk/4.8.0-beta.3`): **0 ошибок** (1 прежнее предупреждение `CS0649` в `MilitaryCore.cs:654`).
- `dotnet build Tools/FormableTests/FormableTests.csproj`: **0 ошибок**.
- `Tools/audit-localization.ps1 -Fail`: **exit 0** — `russian: 27071 entries in 413`, `translated twice, differently 0`, `placeholder mismatches 0`, `empty values 0`.
- `Tools/CartographyTests`: **`CARTOGRAPHY_TESTS passed=6793 failures=0`**.
- `Tools/TerrainTests`: **59 PASS**, exit 0 (`Terrain/front regression suite complete.`).
- `Tools/MapTerritoryTests`: **33 PASS**, exit 0.
- Полный `Tools/FormableTests` (без фильтров): **exit 0, `17 936 PASS`, 0 ошибок**, включая ключевые профильные наборы:
  - `--map-labels-only`: **50 PASS** (новый набор `MapLabelPriorityTests`: порядок приоритетов, обход коллизий без наложений — включая уступку `RegionLabels` по `_cityTaken` / `_placedCityNames` в `GlobeRegionLabels`, зум-диапазоны, сходимость альфа-фейда, 104 дедуплицированные реки в EN/RU, Forward+/atlas-гейтинг атмосферы и виньетки; проверено повторно после финальной правки региональных коллизий вместе с `--map-look-only` 148, `--gpu-atlas-contract-only` 63, `--frontier-field-only` 17 и `--border-rank-only` 104)
  - `--map-look-only`: **148 PASS** (включая MSDF `MapMsdfPixelRange = 40` только для ролей карты, токены `#efe4c8`/`#f7eed6`, новые метрики `look_far_band = 0.36` / `look_far_band_pixels = 4.5`)
  - `--gpu-atlas-contract-only`: **39 PASS GPU atlas contract** + **24 PASS map style settings** (канонический `.gdshader`, отсутствие `textureQueryLod`, ровно **12** сэмплеров и **0** `frontier_field` в Compatibility)
  - `--frontier-field-only`: **17 PASS** (проверка истинной дистанции 12.5 клеток за старым пределом 8 и потолка `Cap = 20`, синхронизация `frontier_cap` в `GlobePlateRepaint`)
  - `--border-rank-only`: **`BORDER_RANK passed=104 failed=0`**
  - `--crossings-only`: **`CROSSINGS_OK checks=62`** (`34 of 34` проливов, `0 refused`)
  - `--river-width-only`: **20 PASS**
  - `--atlas-surfaces-only`: **17 PASS**
  - `--map-usability-only`: **50 PASS**

---

## Что проверить живому верификатору (`@verifier`)

1. Сверить живые скриншоты после слияния `work/mapart` против базового слепка `tmp_playtest/captures/2026-11-18/baseline/` (`6ffd5c0a`):
   - `B1_world_flat` / `B2_globe_far`: широкая бархатная полоса `FrontierField` (до ~17 клеток) по краям крупных стран и без перетемнения центров `B8_balkans`.
   - `B3_europe_globe` / `B3_europe_flat`: тёплые подписи `#efe4c8` на чернильной обводке без ярко-жёлтого золота, появление средних региональных подписей (`regions mid`) и названий крупных рек (`Danube`, `Rhine`, `Dnieper`, `Volga`) в свободном от городов и звёзд пространстве.
   - Приближение к городу/столице: чёткость MSDF-шрифта при зуме и плавный фейд при коллизии.
   - В Forward+: деликатный тёплый СЗ-обод и мягкая виньетка (GPU < бюджет, регрессий по `MeasuredGpuMs` нет); переключение `map_look = "classic"` в Settings возвращает прежнюю картинку в 1 клик.
   - Compatibility (`gl_compatibility`): 12 сэмплеров, 0 шейдерных ошибок в рантайме, без СЗ-обода/виньетки Forward+.
