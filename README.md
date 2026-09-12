# Deal or Force

> Deal or Force Project by some developer who hasn't yet come up with a name for himself...

Глобальная стратегия в реальном времени на **Godot 4.7 (.NET 8)**: вся Земля — 37 833 провинций,
1 720 регионов, 261 страна, экономика 2024 года, армии, войны, дипломатия и мир.
Симуляция детерминированная и полностью отделена от движка, а контент лежит в открытых
файлах в структуре, знакомой по играм Paradox Interactive.

## Что уже работает

| Система | Состояние |
|---|---|
| Карта | растровая `provinces.png` + `definition.csv`, граф из 98 550 рёбер, реки и озёра |
| Экономика | производство → двусторонняя торговля → цены → ВВП; двадцать товаров, из которых десять — звенья цепочек; день ноль совпадает с реальным 2024-м с точностью 0,00 % |
| Торговля | около 2200 маршрутов между названными странами; эмбарго и блокада забирают товары, а не абстрактные деньги |
| Дипломатия | причина войны, репутация, коалиции, союзы, которые действительно приходят, гарантии и санкции |
| Военное дело | фронты по провинциям, снабжение, флот, наступления и нарисованные на карте мирные переговоры |
| Контент | события, решения, здания, товары, ландшафты, юниты, модификаторы — всё в JSON |
| Локализация | `game/localization/*.json`, ключ = английская строка; русский полностью |
| Мультиплеер | LAN / direct IP, UDP 7270, lockstep: приказ применяется в тот день, в который отдан, плюс контрольная сумма и resync |
| Моддинг | три уровня: JSON, GDScript, скомпилированный C# |

## Структура

```
game/            контент базовой игры — то, что мод может переопределить
  common/        defines, goods, buildings, units, decisions, modifiers, ai_strategy
  events/        события с триггерами и эффектами
  history/       страны, регионы, стартовые армии
  localization/  english.json, russian.json
  map_data/      provinces.png, definition.csv, default.map, гидрография
Scripts/
  Simulation/    ядро симуляции — 32 файла, ни одного `using Godot`
  *UI.cs         интерфейс и презентация
  Modding/       загрузчик модов
mods/            моды; `_templates` — готовые шаблоны трёх уровней
Tools/
  MapDataCompiler  офлайн-сборка растровой карты
  SimBench         прогон кампании без движка + проверка инвариантов
docs/            документация (см. ниже)
```

## Документация

| Документ | Для кого |
|---|---|
| [PLAYER_GUIDE](docs/PLAYER_GUIDE.md) | игроку — управление, экономика, война, мир |
| [MODDING_GUIDE](docs/MODDING_GUIDE.md) | моддеру — структура модов, язык эффектов, локализация |
| [ARCHITECTURE](docs/ARCHITECTURE.md) | разработчику — устройство симуляции, тики, производительность |
| [CSHARP_GUIDE](docs/CSHARP_GUIDE.md) | новичку в C# — от правки числа до новой механики |
| [NETWORK_MULTIPLAYER](docs/NETWORK_MULTIPLAYER.md) | о сетевой игре |
| [MIGRATION](docs/MIGRATION.md) | о переносе проекта с s&box |

## Сборка и запуск

Нужен **Godot 4.7+ с поддержкой .NET** и **.NET SDK 8**.

```bash
dotnet build DealOrForce.csproj
```

Затем откройте папку проекта в Godot и нажмите **Play** (главная сцена — `Scenes/Main.tscn`).

Прогнать кампанию без движка и проверить инварианты симуляции:

```bash
dotnet run --project Tools/SimBench/SimBench.csproj -- 730
```

### Установка

Папка `addons/` не входит в репозиторий: там лежат сторонние редакторные плагины,
которые ставятся на машину отдельно и весят около 1,6 ГБ. Сама игра ими не пользуется —
ни одной ссылки на них в коде нет. Но `project.godot` держит два autoload'а от
инструментов разработки (`fennara`, `godot_ai`); после чистого клона Godot сообщит,
что не нашёл их — autoload'ы можно убрать в *Project → Project Settings → Autoload*,
на игру это не влияет.

## Моддинг

Мод повторяет структуру базовой игры и накладывается поверх неё:

```
mods/my_mod/
  mod.json
  common/defines/economy.json     переопределяет только те ключи, что перечислил
  events/my_events.json
  localization/russian.json
```

Эффекты событий и решений — данные, а не код:

```json
"effects": { "treasury_percent": -0.06, "stability": 0.018, "state.radicalism": -0.01 }
```

Голое число прибавляет, `_percent` масштабирует, `_mult` умножает, `_set` присваивает,
префикс `state.` применяет эффект к каждому региону страны. Подробности —
в [MODDING_GUIDE](docs/MODDING_GUIDE.md).

## Открытые долги

- Растры карты попадают в собранную игру дважды: Godot импортирует все 319 png в `.pck`, и
  они же кладутся рядом обычными файлами, потому что часть кода читает их напрямую. Лишние
  десятки мегабайт в раздаче.
- Интерфейс собирается кодом; аналога paradox'овской папки `gui/` пока нет.
- Миграции хоста в мультиплеере нет: потеря хоста заканчивает сессию.

## Лицензия

Код — **GNU General Public License v3.0 или новее**. Полный текст в [`LICENSE`](LICENSE).

    Copyright (C) 2026 Bayte Realization

    This program is free software: you can redistribute it and/or modify it under the
    terms of the GNU General Public License as published by the Free Software Foundation,
    either version 3 of the License, or (at your option) any later version.

    This program is distributed in the hope that it will be useful, but WITHOUT ANY
    WARRANTY; without even the implied warranty of MERCHANTABILITY or FITNESS FOR A
    PARTICULAR PURPOSE. See the GNU General Public License for more details.

    You should have received a copy of the GNU General Public License along with this
    program. If not, see <https://www.gnu.org/licenses/>.

Практически это значит: брать, изучать, менять и распространять — можно; распространять
изменённое — тоже, но с исходниками и под той же лицензией.

**Что сюда не входит.** `addons/` целиком в `.gitignore` и в репозиторий не попадает: это
чужие редакторные плагины, каждый под своей лицензией (восемь под MIT, `ziva_agent` —
проприетарный). Godot распространяется под MIT, что с GPL совместимо.

**Что ещё не решено.** Флаги в `game/gfx/flags` (261 штука) собраны из внешних источников, и
файла с указанием происхождения у них нет. Сами государственные флаги как символы почти всегда
общественное достояние, но конкретная отрисовка может иметь автора и свою лицензию. Пока это не
выяснено, лицензия выше — про **код**; для художественных файлов стоит завести
`game/gfx/CREDITS.md` и указать источник каждого набора. То же касается растров карты, если они
делались не с нуля.

**Если дело дойдёт до Steam.** GPLv3 и Steamworks SDK сочетаются плохо: SDK проприетарен, а GPL
требует, чтобы всё произведение целиком распространялось под ней. Обычное решение —
дополнительное разрешение по §7 GPLv3 на связывание со Steamworks. Сейчас это ни на что не
влияет: `godotsteam` лежит только локально, и ни одна строка кода игры к Steam не обращается.
