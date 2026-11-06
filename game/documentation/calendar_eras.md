# Calendar eras for scenario authors / Эры календаря для авторов сценариев

The calendar supports **10000 BCE through 9999 CE**, including scenario editing,
scenario import, ordinary saves, dated events and land-agreement captions. It is
the **proleptic Gregorian calendar**, not a reconstruction of every historical
local calendar. An ancient calendar does not itself replace modern units, countries,
technologies, economic data or event packs: those are authored by the mod.

## In-game workflow

Create/select a mod in the workshop, copy the current world to a scenario, open
**Scenario settings → Calendar era**, select **Before Common Era (BCE)**, enter a
positive civil year such as **3500**, then Apply and Save to mod. The draft is
detached from the live campaign; undo/redo includes date changes. Existing timers
remain measured in elapsed campaign days.

## Stored years: keep the old astronomical contract

The existing `Date.Year`, `Defines.StartYear`, birth years and numeric event `year`
use astronomical numbering. This preserves old scenarios and arithmetic:

| Civil date | Stored year |
| --- | ---: |
| 3500 BCE | -3499 |
| 300 BCE | -299 |
| 44 BCE | -43 |
| 2 BCE | -1 |
| 1 BCE | 0 |
| 1 CE | 1 |

Display has **no year zero**: 31 December 1 BCE is followed by 1 January 1 CE.
Do not subtract an extra year when computing a person's age: stored years already
form one continuous axis. Negative birth years are valid, not missing data; the
legacy person-field sentinel `0` still means unspecified birth year in ruler data.

In exported `world.json`, for example:

```json
"Date": { "Year": -3499, "Month": 2, "Day": 28, "Hour": 23 }
```

Prefer the editor to editing just this fragment: a complete scenario also needs
the current political map, country/state identifiers and the rest of the snapshot.

## Dated events

`date` accepts a civil suffix, e.g. `"3500-02-28 BCE"` or `"0044-03-15 BC"`.
`"2024-01-01"` remains unchanged. Legacy signed ISO-like dates remain astronomical:
`"-0300-03-15"` means **301 BCE**, not 300 BCE. Do not combine a negative sign with
a BCE suffix. Civil suffixes require a positive year; `"0000-01-01 BCE"` is invalid.
JSON and YAML content go through the same date parser. An event's trigger, effects,
choices, window and repetition rules remain the ordinary event system's.

Land arrangements may use `"until_date": "0300-01-01 BCE"` for a dated expiry.
The older integer `until` keeps its legacy convention (positive CE year; non-positive
means indefinite), avoiding a silent reinterpretation of existing mods.

For a calendar stress test, developers can run:

```text
dotnet run -c Release --project Tools/SimBench/SimBench.csproj -- 365 777 --soak --calendar "3500-01-01 BCE"
```

This deliberately changes only the benchmark clock, **not** the modern world's history.

## Коротко по-русски

В настройках сценария выбираются эра и обычный положительный год: «3500 до н. э.».
В JSON сохраняется астрономический год `-3499`; `0` — это 1 до н. э. Подписи
календаря переводятся, на экране нулевого года нет. Событию удобно писать
`"date": "3500-02-28 BCE"`, не высчитывая внутренний год. Старые отрицательные
строки не переинтерпретируются. Проверки покрывают переход между эрами,
високосность, импорт сценария, сохранение/загрузку и редактор.

Это фундамент календаря для античных модов, не готовый набор Бронзового века:
страны, войска, исследования, хозяйство и исторические события автор задаёт отдельно.
