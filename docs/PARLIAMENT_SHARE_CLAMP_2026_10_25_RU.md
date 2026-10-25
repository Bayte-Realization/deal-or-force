# Парламентские доли и падение кампании 18 июля 2024 — 25 октября 2026

## Что видел игрок

Кампания на 199-й день (18 июля 2024) останавливалась сама собой, поверх карты
появлялось «Проверка кампании» с одной строкой:

```
'0,1' cannot be greater than 0,09133728.
```

Рядом игра писала путь диагностического снимка
(`save games\diagnostics\campaign_199_20261024_211353_284e83a7a36a4fd7919ea9aa8a5649fd.diagnostic.json`).
В самом снимке лежит только текст ошибки, поэтому причина сначала выглядела
загадкой: ни в одной строке игры такой фразы нет.

## Где нашёлся стек

`%APPDATA%\Godot\app_userdata\Deal or Force\logs\errors.log`:

```
SIMULATION_STOPPED System.ArgumentException: '0,1' cannot be greater than 0,09133728.
   at System.Math.ThrowMinMaxException[T](T min, T max)
   at DealOrForce.Simulation.ParliamentaryPolitics.Review(...) ParliamentaryPolitics.cs:line 91
   at DealOrForce.Simulation.ParliamentaryPolitics.Tick(...)  ParliamentaryPolitics.cs:line 156
   at DealOrForce.Simulation.NationalModifiers.Tick(...)
   at DealOrForce.Simulation.ModifierSystem.Tick(...)
   at DealOrForce.Simulation.Campaign.TickStages()
```

Текст — это `SR.Argument_MinMaxValue` из `Math.Clamp`: он бросает
`ArgumentException`, когда **нижняя граница оказалась больше верхней**. То есть
дело не в делении на ноль и не в испорченном сейве, а в перепутанных концах
диапазона, посчитанных в одном месте.

## Арифметика

Военный режим обзора парламента считал доли так:

```csharp
var rem = 1.0f - p.DovishShare;
p.RulingBlocShare = Math.Clamp(rem * approval, 0.10f, rem - 0.05f);
```

`DovishShare` (антивоенный кавкус) ограничен сверху `0.88`, и на 199-й день он
дошёл до `0.8586627` — мир устал от войны. Тогда:

- `rem = 0.1413373`,
- верхняя граница `rem - 0.05 = 0.09133728`,
- нижняя граница `0.10`.

`0.10 > 0.09133728` — `Math.Clamp` отказывается работать, исключение выходит
из `Review`, общий предохранитель симуляции ловит его и останавливает мир.
Ошибка накапливающаяся: каждая ревизия тянет `DovishShare` на 65 % к цели, и
на четвёртой ревизии подряд верхняя граница впервые пересекает нижнюю. Поэтому
кампания падала не сразу после начала войны, а через месяцы.

## Что изменено

`Scripts/Simulation/ParliamentaryPolitics.cs` — потолок считается один раз, а
пол отдаёт ему место, вместо того чтобы спорить с ним:

```csharp
var rulingCeiling = rem - 0.05f;                       // пять процентов оппозиции
var rulingFloor = Math.Min(0.10f, rulingCeiling);      // обычные десять, если есть где
p.RulingBlocShare = Math.Clamp(rem * approval, rulingFloor, rulingCeiling);
```

Порядок предпочтений выбран осознанно: антивоенный кавкус и пять процентов
оппозиции сохраняются, а «обычные десять процентов правящего блока» уступают,
когда мест физически меньше. При `DovishShare = 0.88` выходит 7 % / 5 % / 88 % —
сумма по-прежнему ровно единица, и это видно игроку в интерфейсе.

## Проверка

`Tools/FormableTests/ParliamentaryPoliticsTests.cs` →
`ExhaustedParliamentKeepsValidShares` (запуск `--parliament-only`) перебирает
семь стартовых значений `DovishShare` (включая `1f`) и по 24 ревизии на каждое,
с войной, `WarSupport = 0.20`, `ShortageIndex = 0.30` и `DebtCondition.Stressed`.
На каждой ревизии проверяется, что доли конечны, оппозиция не ниже 5 %,
антивоенная доля не ниже 85 %, а сумма трёх долей равна единице; отдельно — что
мандат на мир записывается в хронику один раз и что распределение мест не
тратит броски ГПСЧ.

Откат одной строки до старого `Math.Clamp(..., 0.10f, rem - 0.05f)` валит этот
же тест:

```
Unhandled exception. System.ArgumentException: '0,1' cannot be greater than 0,08170487.
   at System.Math.ThrowMinMaxException[T](T min, T max)
```

то есть тест ловит именно ту ошибку, а не соседнюю. С исправлением:
`--parliament-only` — 222 проверки, полный стенд — `PASS 6614`, `EXIT=0`.

## Что делать игроку

Последнее исправное сохранение не тронуто (снимок с ошибкой — диагностический,
не игровой сейв). Нужна сборка, содержащая это исправление: загрузить последний
удачный сейв и продолжить. Уже сохранённый сейв менять не нужно — в нём нет
испорченных значений, исправление живёт в коде, а не в данных.

## Заметка на будущее

`Math.Clamp(value, floor, ceiling)` с посчитанным потолком — единственная
конструкция в этом коде, которая падает от собственных данных. Правило: если
потолок вычитается из остатка, пол обязан считаться от него же
(`Math.Min(floor, ceiling)`), иначе рано или поздно найдётся состояние, где
`floor > ceiling`. Стеки таких падений всегда лежат в `errors.log`, а не в
диагностическом снимке — начинать разбор надо оттуда.
