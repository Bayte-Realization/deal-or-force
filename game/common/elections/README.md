# Elections / Выборы

This is a fictional simulation calibration, not a rating or a forecast of any real
country. `initial_control` seeds institutional incumbency advantage in 2024. Other
countries derive it from government centralisation and ideology authority. It is NOT
immunity: approval, shortages, term fatigue, seeded contest uncertainty, political
reform and a slow drift toward current institutions can all change the result.

Mods overlay individual country values in `common/elections/*.json` (0..1).
They apply only when a country's electoral state is first established, not on every
load. The election period/office are existing `common/rulers` office rules. Parliamentary
systems elect the head of government and preserve the monarch/lifetime head of state.
Single-executive systems elect the head of state. This first slice abstracts parliament
as a governing contest, not district-level seats, coalition negotiations or a separate
presidential ballot in a dual parliamentary system. First dates start from the existing
tenure/calendar, not a historical election-date database.

Prices, handover/postponement duration and vote weights: `common/defines/95_elections.json`.
`ElectionsEnabled=false` pauses this system; `rulers=false` gates both ticks and commands.
An old save has no ElectionState: it gets a schedule at its next simulation day.
Snapshot import retains saved results; opening UI never initializes state.
New countries start fresh; fallen countries stop voting; annexation uses the existing
country lifecycle rather than copying another state's pending presidential winner.

Начальные коэффициенты — игровые настройки, не исторические измерения и не прогноз.
Они не дают иммунитет от поражения. Результат зависит от одобрения, дефицитов,
усталости от сроков и воспроизводимой случайности. При смене государственного
строя переоцениваются институты. В парламентской системе меняется правительство,
а не наследственный монарх. Полноценная модель партий, депутатских мандатов и
коалиций в этот срез не входит. Сроки задаются существующими правилами должностей;
это пока не база реальных дат выборов каждой страны.
