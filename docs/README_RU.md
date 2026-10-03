# Документация Deal or Force

*English version: [README.md](README.md)*

Документация соответствует Godot.NET-версии проекта на 18 сентября 2026 года.

Если вы здесь впервые — начинайте с учебников. Они написаны как проход за руку, а не как
справочник: за ними садятся и делают, а не читают.

Свежий проверенный срез: [сверка списка работ 03.10.2026](BACKLOG_AUDIT_2026_10_03_RU.md),
[ластик приказов](ORDER_ERASER_RU.md), [частота событий](EVENT_CADENCE_RU.md),
[языки расширений и первый пример F#](LANGUAGE_BACKENDS_RU.md).
Результаты общего прогона и оставшиеся дефекты: [отчёт отладки 04.10.2026](DEBUG_REPORT_2026_10_04_RU.md).
Новые PNG и исправления читаемости: [армейские команды и товары 04.10.2026](ICONS_2026_10_04_RU.md).

| Учебник | Для кого | Сколько это займёт |
|---|---|---|
| [TUTORIAL_RU.md](TUTORIAL_RU.md) — **первая кампания, за руку** | игрок | пара часов, с игрой |
| [MODDING_QUICKSTART_RU.md](MODDING_QUICKSTART_RU.md) — **первый мод** | моддер | пятнадцать минут |
| [CSHARP_GUIDE_RU.md](CSHARP_GUIDE_RU.md) — **C# через код этой игры** | программист с нуля | вечер на чтение, выходные на упражнения |

Дальше — справочники, в которые ходят за деталью:

| Документ | Для кого | Что внутри |
|---|---|---|
| [PLAYER_GUIDE_RU.md](PLAYER_GUIDE_RU.md) | игрок | управление, экономика, армия, война, мир, эмиссия, сетевой мультиплеер |
| [NETWORK_MULTIPLAYER_RU.md](NETWORK_MULTIPLAYER_RU.md) | игрок/разработчик | LAN, direct IP, Radmin, UDP 7270 и модель синхронизации |
| [MODDING_GUIDE_RU.md](MODDING_GUIDE_RU.md) | моддер | Paradox-подобная структура модов, JSON, GDScript, C#, порядок загрузки |
| [ARCHITECTURE_RU.md](ARCHITECTURE_RU.md) | разработчик | устройство симуляции, карта кода, тики, команды, производительность |
| [BUILDING_RU.md](BUILDING_RU.md) | разработчик | сборка, конфигурации Debug/Release, выпуск |
| [SCENARIOS_AND_WORKSHOP_RU.md](SCENARIOS_AND_WORKSHOP_RU.md) | автор сценариев | формат мира, редактор данных, подписи карты |
| [MODDABLE_CAMPAIGN_UI_RU.md](MODDABLE_CAMPAIGN_UI_RU.md) | моддер интерфейса | раскладка панелей, вкладки, формируемые государства, лобби |
| [LAUNCHER_RU.md](LAUNCHER_RU.md) | игрок | выбор модов и DLC, порядок загрузки, журналы |
| [DIAGNOSTICS_RU.md](DIAGNOSTICS_RU.md) | тестировщик | пять журналов, как их читать и что прислать при вылете |
| [FOG_OF_WAR_RU.md](FOG_OF_WAR_RU.md), [OFFENSIVE_PLANS_RU.md](OFFENSIVE_PLANS_RU.md) | игрок/моддер | видимость и рисование наступлений |
| [LOADING_SCREEN_RU.md](LOADING_SCREEN_RU.md), [INTERFACE_AUDIO_RU.md](INTERFACE_AUDIO_RU.md), [UI_REDESIGN_RU.md](UI_REDESIGN_RU.md) | разработчик | заставка, звуки, первый этап переработки интерфейса |

## На каких языках что написано

Проверено по тексту файлов, а не по их именам — имя врёт: `PLAYER_GUIDE.md`, `BUILDING.md`
и `ARCHITECTURE.md` долго были русскими под английскими названиями.

Соглашение: **голое имя — английский, `_RU` — русский.**

Вся документация теперь есть на обоих языках, кроме двух исключений:

| Документ | 🇷🇺 | 🇬🇧 |
|---|:--:|:--:|
| PLAYER_GUIDE, TUTORIAL, MODDING_GUIDE, MODDING_QUICKSTART,<br>CSHARP_GUIDE, ARCHITECTURE, BUILDING, GETTING_STARTED,<br>NETWORK_MULTIPLAYER, SCENARIOS_AND_WORKSHOP,<br>MODDABLE_CAMPAIGN_UI, LAUNCHER, DIAGNOSTICS, FOG_OF_WAR,<br>OFFENSIVE_PLANS, LOADING_SCREEN, INTERFACE_AUDIO, UI_REDESIGN | ✅ | ✅ |
| MIGRATION — журнал переноса с s&box | — | ✅ |
| `*_2026-09-*.md` — журнал работ, а не документация | ✅ | — |

Проверить, на каком языке файл на самом деле, можно так — считаем буквы, а не верим имени:

```bash
cd docs && for f in *.md; do
  ru=$(head -c 4000 "$f" | grep -o '[а-яё]' | wc -l)
  en=$(head -c 4000 "$f" | grep -o '[a-z]' | wc -l)
  [ "$ru" -gt "$en" ] && echo "RU  $f" || echo "EN  $f"
done | sort
```

## Инструменты

- `Tools/MapDataCompiler` — собирает растровую карту офлайн.
- `Tools/TerrainCompiler` — собирает растр ландшафта (Python).
- `Tools/SimBench` — прогоняет кампанию без Godot и проверяет инварианты симуляции:
  `dotnet run --project Tools/SimBench/SimBench.csproj -c Release`.
- `Tools/TerrainTests` — регрессии ландшафта и фронтов.

Бенч — основной способ узнать, что изменилось: он печатает мировой баланс товаров, что
стало с миром за два года без игрока, и проверяет детерминизм, изоляцию кампаний друг от
друга и то, что примеры модов ещё называют существующие вещи.

В самой игре есть ещё два инструмента: консоль разработчика (`~`, команда `help`) и
проверка вёрстки — команда `layout`, которая называет каждую подпись на экране, которой не
хватило места под собственный текст. В отладочной сборке та же проверка идёт сама раз в
секунду и пишет находки в лог.

## Шаблоны модов

Находятся в `mods/_templates`; папки, начинающиеся с `_`, игра намеренно не загружает.

| Шаблон | Что показывает |
|---|---|
| `balance_and_script` | Первый мод: defines, страна, товар, GDScript. |
| `production_chains` | Экономика: товары, здания, цепочки, потолок торговли. |
| `statecraft` | Дипломатия и ИИ: цена войны, репутация, коалиции, мнения. |
| `central_bank` | Денежная модель: инфляция, ставка, цена дешёвых денег. |
| `trade_instructions` | Торговые регуляторы и как они объясняют себя игроку. |
| `border_redraw` | Кто чем владеет. |
| `map_data_override` | Форма мира и пересборка карты. |
| `interface_reskin` | Цвета, раскладка, локализация. |
| `csharp_mod` | Новая механика на C#. |
