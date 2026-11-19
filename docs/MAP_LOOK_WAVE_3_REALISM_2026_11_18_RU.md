# Облик карты — Волна 3: Физический реализм (AAA), мгновенный переключатель настроений и 3D-миниатюры городов (18 ноября 2026)

Ветка: `work/mapart` (`D:\Godot Projects\dof-mapart`, база `EntertAlpha` `eeaa7e52`).

## 1. Архитектура 4-режимного настроения карты (`map_look`) и опциональной заливки стран (`map_political_fill`)

Добавлены две независимые настройки в разделе **Видео** (`game/common/settings/12_map_style.json`, локализация EN + RU в `game/localization/{english,russian}/324_map_style.json`), применяющиеся мгновенно в реальном времени без перезапуска и без влияния на `CampaignFingerprint`:

1. **`map_look`** (`"default": "realistic"`, однократная миграция `map_look_default_v3` при старте игры переводит прежний сохранённый дефолт `"atlas"` в `"realistic"`, сохраняя ручной выбор `"classic"` и любой последующий выбор игрока):
   - **`realistic`** («Реалистичная (AAA)» / `"Realistic (AAA)"`): физический рельеф с многомасштабной реконструкцией нормалей, снежными шапками по высоте и широте, зеркальными солнечными бликами на реках, батиметрическим океаном Бугера — Ламберта — Бера, 3D PBR-миниатюрами столиц/городов/портов, кучевыми облаками с мягкими тенями и атмосферным рассеянием Рэлея/Ми на лимбе глобуса; плавный переход к гравированной карте только на орбитальном отдалении (`d > 18.5..24.5`).
   - **`paper`** («Бумажная карта (везде)» / `"Paper chart (always)"`): фиксирует `paper_mix = 1.0` на всех масштабах (глобус и плоская карта), отключает облака, 3D-модели городов, свечение лимба и зеркальный блеск воды (`SPECULAR = 0`, `ROUGHNESS = 0.94`), обеспечивая чистую кабинетную карту на нейтральной хлопковой бумаге (`#ECEAE4`) и максимальный FPS на слабых ПК и Android.
   - **`atlas`** («Атлас (бумага издалека)» / `"Atlas (paper from afar)"`): гибридный режим Волны 2 (`paper_mix` на дальнем зуме `17..22`).
   - **`classic`** («Классика» / `"Classic"`): мгновенный откат в 1 клик к исходной карте (`atlas_look = 0`, `realistic_look = 0`, `paper_mix = 0`).

2. **`map_political_fill`** (`["classic_opaque", "balanced", "translucent"]`, `"default": "translucent"`):
   - **`classic_opaque`** («Сплошная» / `"Solid"`): классическая плотная окраска территории (`political_fill_translucency = 0.0`).
   - **`balanced`** («Полупрозрачная» / `"Balanced"`): умеренная полупрозрачность (`0.5`), открывающая горные хребты и долины.
   - **`translucent`** («Прозрачная (рельеф)» / `"Translucent"`): открытые физические интерьеры стран (`~0.36` вблизи / `~0.46` на среднем зуме) с насыщенным суверенным пигментом (`~0.82`) вдоль 20-ячеечного поля границ `FrontierField`. Работает во **всех четырёх** режимах `map_look`.

## 2. Строгое устранение желтизны (De-Yellowing / D65 Neutrality)

По обратной связи владельца и стандарту `ANTI_SLOP_RESEARCH_2026_11_18_RU.md` полностью убран жёлто-сепийный оттенок:
- Подписи стран, регионов и городов переведены на чистый нейтральный слоново-белый **`#F5F6F8`** с глубоким графитовым контуром **`#111318`** (`globe.label`, `map.look_label_fill`, `map.look_capital_fill = #F8F9FB`, `map.look_region_fill = #ECEEF2`).
- Бумажная подложка переведена на нейтральный архивный хлопок **`#ECEAE4`** (`map.look_paper`), незанятая суша — `#DFDDD6`, прибой — `#F2F1EE`.
- Освещение солнца, облаков, бликов рек/океана, атмосферного лимба и 3D-городов выровнено под нейтральный дневной стандарт **`D65`** (`map.look_lamp = #FAF9F6`, `look_relief_tint = 0.28`, нейтральная угольная виньетка без коричневой сепии).
- Из всех пользовательских описаний и локализации полностью исключены упоминания сторонних игр.

## 3. Шейдерный бюджет и контракт 12 сэмплеров Compatibility (`PackedMapShader`)

В `game/shaders/globe_map.gdshader` добавлено **0 текстурных сэмплеров** (только 2 `uniform float`: `realistic_look` и `political_fill_translucency`):
- **Forward+ / Mobile**: **27** сэмплеров.
- **Compatibility (`PackedMapShader`, GLES3 / Android)**: ровно **12** текстурных юнитов (`packed_ids`, `packed_lookups`, `packed_water_tiles` + 9 базовых текстур), **0** остаточных идентификаторов `frontier_field`.
- **Физический рельеф и снега**: макро-уклон из `relief_height` (с поправкой `1 / cos(lat)` у полюсов) + mip/derivative-ограниченный микро-уклон из уже загруженного `terrain_tiles` (без мерцания и без 13-го сэмплера) + снежный покров по высоте, широте и крутизне склонов (`snow_surface_mask` снижает `ROUGHNESS` до `0.66`).
- **Океан и реки**: поглощение Бугера — Ламберта — Бера `exp(-vec3(0.58, 0.145, 0.040) * d)` (бирюзовые отмели → изумрудный шельф → кобальтовая бездна), двухоктавные аналитические нормали волн без шва на `±180°`, отражение неба по Френелю — Шлику (`F0 = 0.02`), солнечная дорожка GGX и снижение шероховатости рек до `0.16` (`SPECULAR = 0.68`) для живых солнечных бликов при вращении глобуса.

## 4. 3D PBR-миниатюры городов из Blender 5.3 и чанкинг `8×4` (`CityMiniatureRenderer`)

- Сгенерированы в headless Blender 5.3 (`--background`) три низкополигональные архитектурные PBR-модели в `game/gfx/models/cities/` (с `.obj.import` sidecar-файлами Godot 4.8-Beta3):
  - `capital_monument.obj` — **162 треугольника** (классическая ротонда с куполом, флигелями и светящимися окнами);
  - `city_cluster.obj` — **114 треугольников** (асимметричный городской квартал с башней ратуши, черепичными и сланцевыми крышами);
  - `port_harbor.obj` — **148 треугольников** (каменный мол, портовый маяк с фонарём и пакгаузы).
- Архитектура строго без `partial class GlobePresentation`:
  - `Scripts/Presentation/CityMiniatureLayout.cs` (`public static class CityMiniatureLayout`, без зависимостей от Godot) — парсер `.obj` с принудительной обмоткой граней по часовой стрелке (CW) под `cull_back` Godot, правосторонняя столбцовая ориентация `GlobeBasisColumns` без лишнего транспонирования, распределение городского населения региона по рангу Ципфа (`1 / ((r + 1) * H_N)`), береговой сдвиг `TryNudgeOntoShore`, проверка суши по центру и 4 углам (`FootprintOnLand`), классификация столиц/портов/городов и справедливый отбор до `96` миниатюр в каждом из **32 пространственных чанков (`8×4` по долготе и широте)** в фоновом потоке (`Task.Run`).
  - `Scripts/gfx/CityMiniatureRenderer.cs` (`public sealed class CityMiniatureRenderer`) — рендерер чанкованных `MultiMeshInstance3D` с GPU-масштабированием в вершинном шейдере и отсечением за горизонтом/фрустумом за `O(32)` на главном потоке (**0 циклов по городам на main thread**).

## 5. Результаты верификации

- Сборка `DealOrForce.csproj` (`Release` и `Debug`), `FormableTests.csproj`, `CartographyTests.csproj` (`--artifacts-path D:/Godot Projects/dof-mapart/tmp_logs/mapart`): **0 ошибок**.
- Гигиена дерева: `MISSING_CS_UID = 0` (включая `Scripts/` и `Tools/FormableTests/CityMiniatureTests.cs.uid`), `MISSING_ASSET_IMPORT = 0` (`capital_monument.obj.import`, `city_cluster.obj.import`, `port_harbor.obj.import`), `SIM_GODOT_USING = 0`, `0` упоминаний сторонних игр в настройках и локализации.
- `Tools/audit-localization.ps1 -Fail`: exit `0` (`27 531` ключей в `418` файлах, `0` конфликтов / плейсхолдеров / пустых значений).
- Headless-наборы:
  - `--map-look-only` (включая `CityMiniatureTests`: 100% CW-обмотка треугольников всех 3 `.obj`, правосторонний столбцовый базис в 4 полушариях и ранговое распределение Ципфа): **`224` PASS** (`EXIT = 0`);
  - `--gpu-atlas-contract-only` (12 сэмплеров Compatibility + `MapStyleSettingTests` + миграция `map_look_default_v3`): **`74` PASS** (`EXIT = 0`);
  - `--map-labels-only`: **`51` PASS** (`EXIT = 0`);
  - `--frontier-field-only`: **`17` PASS** (`EXIT = 0`);
  - `--border-rank-only`: **`104/0` PASS** (`EXIT = 0`);
  - `--crossings-only` (`62`), `--river-width-only` (`20`), `--date-line-only` (`19`), `CartographyTests` (`6793/6793`), `TerrainTests` (`29`), `MapTerritoryTests` (`33`) — все `EXIT = 0`.
- Live Godot 4.8-Beta3 (`Forward+` Vulkan и `gl_compatibility` OpenGL3) + `Gemini Decision 1`: оба рендерера загрузились с `EXIT = 0` и `0` шейдерных ошибок; на среднем зуме Европы читаемость стран подтверждена (`countries_distinguishable_mid_zoom = YES`) на всех значениях `map_political_fill` (`translucent`, `balanced`, `classic_opaque`) и в режиме `paper` (`2.49 / 3`).
