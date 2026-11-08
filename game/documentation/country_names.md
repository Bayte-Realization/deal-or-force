# Country names / Названия стран

One state has one name everywhere: tabs, tooltips, the news and the label on the map.
The code is `Scripts/Simulation/CountryNames.cs` (`Nation.DisplayName`, `Nation.MapName`).

## Which row is read / Какая строка читается

For a country with tag `TAG`, government `G` and ideology `I`, the first row that exists wins:

| Order | Interface (`.name`) | Map label (`.map`) |
|---|---|---|
| 1 | `CustomName` set in play or by the scenario | same |
| 2 | a name the campaign gave a state it founded (secession, rising) | same, short form |
| 3 | `country.TAG.government.G.ideology.I.name` | `...ideology.I.map`, else the `.name` row |
| 4 | `country.TAG.government.G.name` | `...government.G.map`, else the `.name` row |
| 5 | `country.TAG.ideology.I.name` | `...ideology.I.map`, else the `.name` row |
| 6 | `country.TAG.name` (with the state form for the government) | `country.TAG.map`, else `country.TAG.name` |

Every row may carry a spelling variant suffix (`.name.<variant>`), chosen by the player's settings.

**The map label of a form of state is that form's name** unless a shorter `.map` row is written for
it. Until 2026-11 a government variant without a `.map` row fell through to the country's plain
short name (China under the Beiyang Government: "Beiyang Government" in the tabs, "CHINA" on
the map). To shorten a long variant on the map, write its `.map` row; do not leave it out.

## По-русски

Одно государство — одно имя везде. Порядок такой же, как в таблице. Подпись на карте для формы
правления — это имя этой формы, если для неё не написана короткая строка `.map`. Чтобы на карте
было короче, добавьте строку `.map` для того же варианта.
