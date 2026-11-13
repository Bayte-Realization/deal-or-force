# Почему поздний сейв идёт медленно — 11 ноября 2026

Замер на реальном сейве игрока `50s of slow times.bdfsave` (107 МБ, 28 августа 2050,
день 9736): 495 стран, 1937 регионов, **2062 формирования**, 300 войн (22 открытых),
**64 034 двусторонних отношения**. Это мир, проживший 27 лет; ощущение игрока —
«ползёт» и «дёргается».

Сейв открывался только на чтение (`Tools/FormableTests --replay-save`), плюс разобраны
логи того же запуска игры (`performance.log` за 18:36 11 ноября 2026).

## Что измерено

### Реальная игра (Smooth time включён, скорость 5)

| Метрика | Значение |
|---|---:|
| fps | 37–45 |
| p95 кадра | 48–69 мс |
| худший кадр | 99–383 мс |
| **дней в секунду** | **1.20–1.60** |
| `pending_seconds` | 0.792 (очередь всё время на максимуме) |
| управляемая куча | 0.69–1.22 ГБ |
| процесс | 8.3 ГБ |
| GC gen0/gen1/gen2 за сессию | 2603 / 817 / 28 |
| `save.prepare` (худший) | 1464 мс |
| `save:serialize` (худший) | 953 мс |
| `save:serialize_again` (худший) | 452 мс |

Скорость 5 просит `SecondsPerDay = 5 × 0.0066` → **30 игровых дней в секунду**.
Машина на этом сейве отдаёт **1.2–1.6**. Скорость 3 (1.33 дня/с) — предел, скорости
4 (4 дня/с) и 5 (30 дней/с) на таком мире недостижимы: ползунок обещает в 20 раз
больше, чем мир успевает.

### Headless, полный шаг (то же, что Smooth time выключен)

24 часовых шага на день, все пассы добегают до конца в одном вызове:

| Метрика | Значение |
|---|---:|
| установившийся день | **0.71–1.06 с CPU** |
| сумма пассов дня (верхний уровень) | 270–730 мс |
| вне пассов (GC, итераторы, JIT) | **140–330 мс, 20–30 % дня** |
| аллокации | **19–42 МБ на день** |
| ящиков gen0/gen1/gen2 | 1–3 / 0–1 / изредка 1 |

Где уходит день (на день, после правки ниже):

| Пасс | мс/день | мусор за 10 дней |
|---|---:|---:|
| `statecraft` (остаток) | 93 | 16.7 МБ |
| `ai:diplomacy` (внутри statecraft) | 83 | 14.1 МБ |
| `war:advances` | 64 | 10.7 МБ |
| `movements` | 61 | 22.9 МБ |
| `ai:foundations` | 51 | **48.1 МБ** |
| `war:staff` | 48 | 3.8 МБ |
| `pocket breakout` | 43 | 2.1 МБ |
| `logistics` | 22 | — |
| `military` (марш 2062 формирований × 24 шага) | 26 | 0.3 МБ |

Недельный день — тот же набор плюс экономика и движения: в замере такой день стоил
**1043–1679 мс** против 500–700 мс обычного.


## Почему Smooth time не спасает

1. **Он не уменьшает работу, а только режет её по границам стадий.** Стоимость дня
   та же, поэтому потолок скорости тот же: при бюджете 9 мс на кадр и 45 fps машина
   физически не может отдать больше `45 × 9 мс = 405 мс` симуляции в секунду, а день
   стоит ~1000 мс. Реальные 1.2 дня/с берутся из перерасхода бюджета.
2. **Он не может прервать стадию.** `RunStages` проверяет бюджет *после* `MoveNext`,
   и хотя бы одна стадия стартует всегда. Стадия, которая не отдаёт кадр внутри,
   проходит целиком. До правки это были, по логам игры: `ai:foundations` 58–111 мс,
   `diplomatic_gestures` до **350 мс**, `state failure` 176 мс, `mov:fall` 106 мс,
   `civil war outbreak` 104 мс, `secession` 96 мс, `union` 91 мс, `war:staff` 92 мс,
   `war:disposition` 23–27 мс, `mov:dealings` 23 мс.
3. **Без него** тот же день кладётся в один кадр: шаг, пересекающий полночь, несёт все
   дневные пассы — по замеру это **~0.6–0.7 с в одном кадре** обычного дня и
   **1.4–1.8 с** в недельный. Отсюда «замирает раз в день» при выключенном сглаживании
   и «ползёт, но ровно» при включённом.

## Что изменено этим проходом

Восемь правок, ни одна из которых не меняет решений мира. Проверка одна и та же на всех:
`--replay-save` того же сейва на 10 дней плюс **отпечаток мира**. Он одинаков до и после —
`456C8804FB80AFDB`.

| Правка | Что было | Что стало |
|---|---|---|
| `ai:foundations` | одна неделимая стадия | постранично: 9 → 114 стадий |
| `FormableProclamationAi` | каталог фильтровался и сортировался на каждую страну | один раз на проход |
| `FormableNations.Evaluate` | `Participates` у всех 495 стран на каждое определение | только у владельцев земли этого определения |
| `PeacefulFormationAi` | `PromisedElsewhere` обходил все страны на каждого кандидата | читает ключи партнёра один раз |
| `PeacefulFormationAi` | отказ (локализованная строка) строился до проверки готовности | готовность первой, отказ — только для готовых |
| `PeacefulFormation.CulturalAffinity` | `HashSet` и три LINQ на каждую пару | два обхода без аллокаций |
| `PeacefulFormation.RegionProvinces` | обход всех провинций мира на каждое определение | кэш на раскладку регионов |
| `DiplomaticGestures.AiTickStages` | скан 64 000 отношений **на каждую думающую страну** + 8 сортировок | одна сборка на проход, 8 линейных обходов |

Замер на том же сейве, 10 дней:

| | до | после |
|---|---:|---:|
| `ai:foundations` за 10 дней | 549 мс | **236 мс** (−57 %) |
| `ai:foundations.proclaim` | 539 мс | **225 мс** (−58 %) |
| средняя стадия `ai:foundations` | 4.82 мс | **2.07 мс** |
| мусор `ai:foundations` | 48.3 МБ | **39.6 МБ** (−18 %) |
| `diplomatic_gestures` | 260 мс (9 запусков) | вышло из топ-30 |

Проверки: `--formable-proclamation-ai-only`, `--living-world-fingerprint-only`,
`--opinion-witnesses-only`, `--generals-only`, `--alliances-only` — **214 PASS,
0 исключений**. Сборка 0 ошибок.

Чего это стоит: стадия proclamation-AI теперь 2.7 мс в среднем, то есть кадр больше
не замирает на 60–110 мс из-за оснований государств. Остаток 39.6 МБ — это списки
требований (`FormationRequirement` + подписи через `Loc.Format`), которые строятся
и для игрока, и для ИИ одним и тем же кодом; чтобы убрать их из ИИ, нужен отдельный
дешёвый предикат готовности, а это уже дублирование правил — решение владельца.

`SaveReplay` теперь умеет то, без чего эти числа не получить: `--replay-detail`
печатает вклад пассов **за конкретный день** (`REPLAY_DAY_SCOPE`), аллокации и
GC-счётчики дня (`REPLAY_DAY_GC`), отпечаток мира (`REPLAY_FINGERPRINT`) и число
отношений в мире. Заодно исправлена колонка `alloc_mb`: формат `,8:1` — пользовательский,
поэтому она **всегда печатала «1»**, и 48-мегабайтный пасс читался как нулевой.

## Что осталось и в каком порядке

1. **Автосейв**: 1.46 + 0.95 + 0.45 с — это те самые «фризы» в 1.5–3 с. Нужен фоновый
   снимок и запись вне кадра.
2. **Двойная сериализация сейва.** `save:serialize_again` — это не проверка, а повторная
   запись всего 100-мегабайтного payload: `SaveSystem` ждёт, что `History` окажется
   последним полем, не находит ожидаемого хвоста и сериализует мир заново. 0.45 с на
   каждое сохранение, и это лечится порядком полей, а не оптимизацией.
3. **Остальные монолитные стадии** (`war:staff`, `mov:fall`, `state failure`,
   `civil war outbreak`, `secession`, `union`) — тем же приёмом, что и foundations:
   постранично. По логам игры это оставшиеся фризы в 90–176 мс.
4. **Списки требований** в `FormableNations.Evaluate` (остаток 39.6 МБ / 10 дней):
   они строятся и для игрока, и для ИИ одним кодом. Отдельный дешёвый предикат для ИИ
   уберёт их из симуляции, но продублирует правила готовности — это решение владельца.
5. **Частота AI**: ежедневный `ai:diplomacy` (83 мс/день) держит недельный цикл для всех
   500 стран. Второстепенным — раз в 11–15 дней, великим державам и соседям игрока —
   по-прежнему раз в неделю.
6. **Честная скорость**: при `days_per_second < 1` показывать игроку, что мир не успевает,
   вместо того чтобы держать очередь на 0.792 с и рвать кадры.

## Чего этот проход не доказывает

- Общее время дня на этой машине колеблется 0.88–1.06 с от прогона к прогону (машина
  занята другой работой), поэтому ускорением дня это не объявляется. Доказаны величины
  внутри пассов: `ai:foundations` 549 → 236 мс за 10 дней, стадия 4.82 → 2.07 мс.
- Неизменность мира доказана отпечатком на одном сейве и 214 проверками, а не полным
  стендом: прогнаны пять наборов, относящихся к правке.
- Игра с этими изменениями в реальном окне не перезапускалась.

## Итоговый замер после пакета оптимизации и ревью (11–12 ноября 2026)

После закрытия узких мест и финальных правок ревью (`FlagCatalog.Slug` с быстрым выходом для канонических строк без потери `ReferenceEquals`; ограниченный `StepFor(FastSpeedSimulationBudgetMs)` в `DealOrForceGame._Process` при `Smooth time` OFF; постраничные `war:staff`, `FrontManager`, `ThreatPosture`, `KinStatecraft.ScanStages`, `PocketBreakout`, `ArmyHomecoming`/`ArmyDisposition`, `MovementLifecycle.Pressure`/`TickStages`, `MovementAi.ThinkStages`, `AiStatecraft.WarmakingStages` с обёрткой `Warmaking` и вынесением прогрева `EnsurePeopleIndexedStages` перед `MovementPower.TickStages` в `SimulationCore`; хоистинг `InternationalOrganizations.DefencePartners` в `AiRescue.SeekRescue`; `ResearchSystem.BuyerSurvey` в `SecessionResearch.Inherit` и оптимизация `DiplomacyCore.Borders`; кэш `AllianceCore.OfferScan` с `DormantRelations` и инвалидацией в `AllianceSystem.Break`/`SubjectInteractions`; кастомный клон `DoctrineTrack` в `SaveSnapshotCloner`; однопроходный хвост `History` и валидация `AllFinite` в `SaveSystem.Prepare` + `WaitForPendingSave` в `SaveLoadUI.DeleteSave`) проведены замеры на финальном `.cs` дереве (сейв `50s of slow times.bdfsave`, день 9736, 28 августа 2050, строго только чтение, SHA256 `1B1CC81292B309E8644A44576068D0A7C91B89B5FFC9A56C70768C83C7C32471`):

| Метрика | Значение |
|---|---:|
| `--replay-save ... 14` (Release) | `11886 ms` всего, `849,0 ms/day`, **`steady 716,1 ms/day`** (дни 2..14) |
| Отпечаток мира через 14 дней (`--replay-save`) | **`904156DF870A6DD2`** (бит-в-бит с базой; `REPLAY_ROUTE asks=19580 keeps=533 reuses=18831 walks=216`) |
| `SimBench --profile-save ... 14` (Release, копия в `tmp/`) | `9 413 ms` за 14 дн. (**`672.42 ms/day`**), `18 162 kB/day`; smooth `9 ms` (`30 d`): `1 696` кадров, median `9.03 ms`, p99 `19.97`, worst `60.1`, `>33 ms`: **`3`** |
| `SimBench --smooth 14 9 --define HoursPerTick=48` (копия сейва `tmp/bench_late`) | `701` кадр / `6` шагов, median `9.68 ms`, p95 `19.85`, p99 `28.09`, worst `57.1` (`sub:country_release 57.0`), `>33 ms`: **`1`**, отпечаток `D2FD5B5595F929D2` |
| `SimBench --smooth 14 42 --define HoursPerTick=48` (копия сейва `tmp/bench_late`) | `157` кадров / `6` шагов, median `42.87 ms`, p95 `51.70`, p99 `63.90`, worst `84.9` (`sub:country_release 79.8`), `>33 ms`: `151`, отпечаток `D2FD5B5595F929D2` |
| `SimBench --save-cost 0` (копия сейва `tmp/bench_late`) | на кадре `467 ms` (прогретые `364, 342, 438 ms`; история `213 ms` за `11.7 MB`), на воркере `2 402 ms` (`2 869 ms` всего, `11.3 MB` brotli / `63.1 MB` JSON), загрузка `1 489 ms` (`88 451` сэмплов) |
| `SimBench --integrity-save ... 14` (Release) | `PASS` (все числа стран, движений, регионов, групп и войн конечны) |

Что изменилось и что остаётся на старте/в позднем сейве/на горизонте лет:
- `SimBench --profile 365 --define HoursPerTick=1` (seed 42): `178.49 ms/day` (`10,376 kB/day`), smooth `9 ms` (`30` дн.): `1,104` кадра, median `3.65 ms`, p99 `14.28`, worst `30.0`, **`0 >33 ms`**.
- `SimBench --profile 365 --define HoursPerTick=48` (seed 42): `108.47 ms/day` (`8,747 kB/day`), smooth `9 ms` (`30` дн.): `294` кадра, median `9.19 ms`, p99 `14.94`, worst `20.2`, **`0 >33 ms`**.
- `SimBench 1825 42 --aging --define HoursPerTick=24` (5 лет): год 1 `126.5 ms/day` (`9,554 kB/day`, heap `309 MB`, `329` стран) → год 5 `138.2 ms/day` (`12,057 kB/day`, worst `398 ms`, heap `260 MB`, `353` стран); при этом растут коллекции (`Nation.PublicImages 0->38,471`, `Relation.Reasons 279->24,550`, `Campaign.Relations 253->16,415`, `DormantRelations 0->2,923`), что на 27-летнем сейве (`495` стран, `64,034` связей) даёт `672–716 ms/day`.
- На 27-летнем сейве под `StepFor(9 ms)` пики `>33 ms` сократились с `221.3 ms` (`24` кадра / `10` дн.) до `60.1 ms` (`3` кадра / `30` дн.: `corridors 60`, `land_arrangements 38`) и `57.1 ms` (`sub:country_release` на дне 9743 при `48h`), но единичные неделимые шаги всё ещё превышают `33 ms`.
- Окно Godot и экспортная сборка в этом сеансе не запускались (редактор `Godot_v4.8-beta2_mono_win64.exe` открыт, живые `SaveLoadUI` / GPU FPS не проверены).

### Обновление замера после финального пакета горячих путей войны, маршрутов и оснований (12 ноября 2026)

После дополнительных оптимизаций без смены логики (`CombatAttrition.ApplyDamage`/`CasualtyTotals` с быстрым индексным циклом по `IReadOnlyList<Army>` и `double`-аккумулятором при сохранении сигнатуры `IReadOnlyCollection<Army>`, `CombatCore.ComputeLineProfile`, zero-alloc маршрутизация `FillOperationalRoute` в `ProvinceTopology`/`WarCoalition`/`CombatOperationalRouting`, кэш дружественных контролёров `FillControllers` в `PocketBreakout` + ленивая синхронизация `Scratch.CameFrom`/`Scratch.Depth`, однопроходный `BuildThreatBorders` в `ThreatPosture`, пул `SelectionContext` на `Campaign` в `FrontDisengagement`, индексированный `ArmyIndex.RegionByIndex` и `ActiveBattlesByKey` в `CombatOpportunitySelection`/`CombatCore`, `CoalitionCache` на `Campaign` в `WarCoalition`, кэш `FormableRestorations` по `OwnershipRevision`/`MovementRevision` + `[ThreadStatic]`-скретч в `FormableNations`/`FormableProclamationAi`, циклы без замыканий в `PeacefulFormation`/`Consolidation`/`MovementAi`/`SubjectInteractions`/`KinStatecraft`/`WarScoring`/`LogisticsCore`/`WarDamage`/`Recruitment`):

| Проверка | Было (база прохода) | После финальной правки |
|---|---:|---:|
| `SimBench 365 42 --define HoursPerTick=1` | `143.675 ms/day`, `5042.1 kB/day` (~210 kB/step) | **`130.896 ms/day` (`47 776 ms`), `4253.1 kB/day` (`177 kB/step`, `gen0/1/2 = 90/15/0`)** |
| — `war:advances` / `adv:resolve` / `adv:execution` (365 дн.) | `7082.7` / `4326.8` / `2755.9 MB` | **`1574.2` (−77.8 %) / `749.6` (−82.7 %) / `824.5 MB` (−70.1 %)** |
| — `disengage:index` / `ai:foundations` (365 дн.) | `764.3` / `2323.2 MB` | **`5.8` (−99.2 %) / `803.1 MB` (−65.4 %)** |
| `SimBench --profile-save tmp\bench_late.bdfsave 14` (Release) | `9 413 ms` (`672.42 ms/day`), `18 162 kB/day`, `3` кадра `>33 ms` | **`8 465 ms` (`604.70 ms/day`), `8 810 kB/day` (−51.5 % аллокаций)**; smooth `9 ms` (`30 d`): `1 712` кадров, median `9.03 ms`, p99 `20.80`, worst `88.8` (`naming 91`), `5 >33 ms`, отпечаток `8636001A217DD47F` |
| `SimBench --load tmp bench_late --smooth 14 9` (1h/шаг) | `57.1 ms` / `1` кадр `>33 ms` (при `48h`) | **`1 000` кадров / `335` шагов, median `9.09 ms`, p95 `14.70`, p99 `23.25`, worst `32.2`, `0` кадров `>33 ms`**, отпечаток `32EF60D84F280BEE` |
| `SimBench --load tmp bench_late --smooth 14 42` (1h/шаг) | `63.90 p99`, `84.9 worst` (при `48h`) | `495` кадров / `335` шагов, median `3.95 ms`, p95 `44.74`, p99 `55.13`, worst `60.3`, `164 >33 ms`, тот же отпечаток `32EF60D84F280BEE` |
| `SimBench --smooth 14 9` / `--smooth 14 42` (старт 2024) | — | `9 ms`: `576` кадров, median `2.34 ms`, p99 `20.08`, worst `39.6`, `1 >33 ms`; `42 ms`: `388` кадров, median `1.82 ms`, p99 `47.88`, worst `61.3`, `56 >33 ms`; общий отпечаток `AC332FAB026BC8C6` |
| `FormableTests --replay-save "...\50s of slow times.bdfsave" 14 --replay-detail 20` | `11 886 ms`, steady `716.1 ms/day` | **`9 326 ms` (`666.1 ms/day`), steady `546.4 ms/day`**; `adv:route` `30 369` вызова `141.7 ms` / `0.0 MB`; `REPLAY_ROUTE asks=19580 keeps=533 reuses=18831 walks=216`, отпечаток `7172FBE9E95E6D5D` |
| `SimBench 1825 42 --aging --define HoursPerTick=24` (5 лет) | г.1 `126.5` → г.5 `138.2 ms/day`, `9.6→12.1 MB/day` | **г.1 `113.4 ms/day` (`4 187 kB/day`) → г.5 `113.9 ms/day` (`4 862 kB/day`, worst `326 ms`, `gen2=0`, heap `365 MB`)** |
| `CombatAttritionRefactorTests` (`ATTRITION_BENCH`) | `legacy_ms=202.48`, `legacy_bytes=6 768 040` | **`current_ms=28.01`, `current_bytes=0`** |

Ограничения этого обновления: все замеры выполнены headless (`SimBench` Release, `FormableTests`/`ArmyInteractionTests` Debug); окно Godot и экспортный прогон не запускались (на машине открыт редактор пользователя). Оригинальный сейв `"50s of slow times.bdfsave"` остался нетронутым (`SHA256 = 1B1CC81292B309E8644A44576068D0A7C91B89B5FFC9A56C70768C83C7C32471`).

#### Пост-ревью финализация горячих путей, изоляции и тестов (12 ноября 2026)

После ревью и доработки оставшихся регрессий и горячих путей (`TacticalDoctrines.All` — возврат ленивого `yield return` для `DoctrineHotPathTests.CatalogReloads`; `FormableRestorations.MatchesRestoredTag` — восстановлена проверка авторских `Restores` без аллокаций для `RestorationCycleTests`; `CombatOpportunitySelection`/`WarCore` — `[ThreadStatic]`-скретчи и предрасчёт `enemyMaps` раз на войну в `UpdateAiExecution`; `CombatOperationalRouting.RememberAdvanceRoute` — переиспользование `Path`/`Control` без аллокаций; `MovementReach.Centres`/`Movements.StatesById`/`Consolidation.OwnersCache` — изоляция на `Campaign` через `ConditionalWeakTable`; `MovementRules`/`MovementAi`/`Restoration`/`NationNaming`/`RegionalAdministration`/`SubjectInteractions` — удаление LINQ-замыканий, поиск через `StateByMapIndex`/`NationByMapIndex`, переиспользование пакета `MatchingSubject`) прогнан полный пост-финальный стенд без дальнейших правок `.cs`:

| Проверка | Результат после финальной правки |
|---|---|
| `dotnet build DealOrForce.csproj --nologo` | **0 ошибок** |
| `Tools/FormableTests` (полный прогон без аргументов + 7 целевых флагов) | **PASS, exit 0** (включая `DoctrineHotPathTests`, `RestorationCycleTests`, `--movement-staging-only`, `--flags-only`, `--integrity-only`, `--war-occupation-isolation-only`, `--advance-route-cache-only`, `--pocket-scratch-only`, `--smooth-heavy-yield-only`) |
| `Tools/ArmyInteractionTests` | **PASS, exit 0** |
| `FormableTests --replay-save "...\50s of slow times.bdfsave" 14 --replay-detail 20` | `REPLAY_OK` (день `9750`), `10 532 ms` (`752,3 ms/day`, **`steady 607,9 ms/day`**), `REPLAY_ROUTE asks=19580 keeps=533 reuses=18831 walks=216`, отпечаток `7172FBE9E95E6D5D` |
| `SimBench --determinism-only 60` | **PASS, exit 0** (`1,601 formations`, `327 countries`) |
| `SimBench 365 42 --define HoursPerTick=1` | `157.423 ms/day` (`57 459 ms`, шум нагрузки машины), **`4 323.6 kB/day` (`180 kB/step` против бюджета `300 kB/step`, `gen0/1/2 = 93/17/1`)**, `1,092 shipped defines [ ok ]`; `war:advances 1574.2 MB`, `adv:resolve 749.6 MB`, `adv:execution 824.6 MB`, `disengage:index 5.8 MB`, `ai:foundations 803.1 MB`, `soc:borders 0 MB`; те же `15` предсуществующих провалов баланса/скина/локализации/ИИ-войн |
| `SimBench --profile-save tmp\bench_late.bdfsave 14` | `8 645 ms` (**`617.50 ms/day`**), **`8 236 kB/day`**, `adv:execution 125 ms (2.0 MB)`, `adv:route 212 ms (0.0 MB)`, `disengage:index 54 ms (0.0 MB)`; smooth `9 ms` (`30 d`): `1 704` кадров, median `9.03 ms`, p99 `17.13`, p99.9 `33.86`, worst `74.9`, **`2 >33 ms`**, отпечаток `8636001A217DD47F` |
| `SimBench --load tmp bench_late --smooth 14 9` / `16` / `42` (`1h`) | `9 ms`: `1 018` кадров / `335` шагов, median `9.10 ms`, p95 `13.59`, p99 `20.78`, worst `29.9`, **`0 >33 ms`**; `16 ms`: `742` кадров, median `16.03 ms`, worst `37.5`, `4 >33 ms`; `42 ms`: `490` кадров, median `4.06 ms`, p95 `44.44`, p99 `54.58`, worst `58.4`, `160 >33 ms`; отпечаток `32EF60D84F280BEE` |
| `SimBench --smooth 14 9` / `42` (старт 2024) | `9 ms`: `596` кадров / `335` шагов, median `3.12 ms`, p99 `19.69`, worst `41.8`, `1 >33 ms`; `42 ms`: `386` кадров, median `1.69 ms`, p99 `52.49`, worst `60.0`, `56 >33 ms`; отпечаток `AC332FAB026BC8C6` |
| `SimBench --save-cost 0` | на кадре `248 ms` (прогретый `21, 33, 27 ms`; `history` `5 ms` за `0.0 MB`), на воркере `934 ms` (`1 182 ms` всего, `1.5 MB` / `8.4 MB` JSON) |
| `SimBench --integrity-save tmp\bench_late.bdfsave 14` | **PASS, exit 0** (`tmp\bench_late.bdfsave` удалён после замера) |
| `SimBench 1825 42 --aging --define HoursPerTick=24` | **exit 0**: г.1 `86.7 ms/day` (`4 173 kB/day`, heap `283 MB`, `329` стран) → г.5 `125.1 ms/day` (`4 659 kB/day`, worst `341 ms`, `GC 100/21/2`, heap `262 MB`, `352` страны) |
| `audit-localization.ps1 -Fail` & `Test-ReleaseVersion.ps1` | **PASS, exit 0** (`25 559` ключей, `0/0/0`; `EntertAlpha 0.6.3.1`, 17/17 проверок) |

По-прежнему не проверялось (ограничения): живое окно Godot, GPU FPS, интерактивный `SaveLoadUI` и экспортный `ExportVerification` (открыт редактор пользователя). Оригинальный сейв `"50s of slow times.bdfsave"` не изменялся (`SHA256 = 1B1CC81292B309E8644A44576068D0A7C91B89B5FFC9A56C70768C83C7C32471`).
