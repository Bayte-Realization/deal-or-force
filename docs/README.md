# Документация By Deal or Force

Документация соответствует Godot.NET-версии проекта на 1 сентября 2026 года.

| Документ | Для кого | Что внутри |
|---|---|---|
| [PLAYER_GUIDE.md](PLAYER_GUIDE.md) | игрок | управление, экономика, армия, война, мир, сетевой мультиплеер |
| [NETWORK_MULTIPLAYER.md](NETWORK_MULTIPLAYER.md) | игрок/разработчик | LAN, direct IP, Radmin, UDP 7270 и модель синхронизации |
| [MODDING_GUIDE.md](MODDING_GUIDE.md) | моддер | Paradox-подобная структура модов, JSON, GDScript, C#, порядок загрузки |
| [ARCHITECTURE.md](ARCHITECTURE.md) | разработчик | устройство симуляции, карта кода, тики, команды, производительность |
| [CSHARP_GUIDE.md](CSHARP_GUIDE.md) | новичок в C# | язык на примерах игры и путь от правки числа до новой механики |
| [MIGRATION.md](MIGRATION.md) | команда | состояние переноса с s&box и технические решения миграции |

Инструменты: `Tools/MapDataCompiler` собирает растровую карту офлайн, `Tools/SimBench` прогоняет кампанию без Godot и проверяет инварианты симуляции (`dotnet run --project Tools/SimBench/SimBench.csproj -- 730`).

Шаблоны модов находятся в `Mods/_templates`. Папки, начинающиеся с `_`, игра намеренно не загружает.
