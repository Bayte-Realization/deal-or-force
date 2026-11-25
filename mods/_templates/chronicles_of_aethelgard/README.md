# Fantasy Total Conversion Starter: Chronicles of Aethelgard / Шаблон тотальной конверсии: Хроники Этельгарда

Complete, zero-bloat fantasy total conversion proving that Deal or Force cleanly supports
non-modern and non-Earth settings (fantasy, medieval, or sci-fi) using pure data files.

## What it demonstrates

- **`total_conversion: true` + `replace_paths`**: cleanly replaces base-2024 `events`,
  `common/technologies`, `common/decisions`, `common/ideologies`, and `common/governments`
  with zero dummy files.
- **Custom fantasy calendar (`CalendarProfile`)**: `"Year 402 of the Third Sun"` with 12
  custom localized month names (`Frostfall`..`Starquiet` / `Листопадень`..`Звездотих`)
  while keeping internal simulation ticks 100% deterministic.
- **Custom world snapshot (`world.dfsave`)**: 3 rival fantasy realms (`VAL`, `IRN`, `SYL`),
  starting alliance (`Pact of the Silver Leaf`) and starting war (`Siege of the Iron Marches`).
- **Era feature gating**: disables `nukes`, `modern_air_force`, `missiles`, `drones`, and
  `modern_intelligence` via declarative `disabled_features`.
- **Custom goods, ideologies, governments, technologies, decisions, events, and parchment era skin**,
  with 100% EN+RU localization parity.

## How to clone in 3 steps

1. Copy `mods/_templates/chronicles_of_aethelgard` to `mods/my_fantasy_world`.
2. In `manifest.json`, set a unique `"id"` and `"enabled": true`.
3. Replace or extend the nations, months, technologies, events, and skin tokens!

---

Готовый шаблон фэнтези-конверсии (подходит также для средневековья и sci-fi): собственный календарь
с 12 месяцами и названием эры, `total_conversion: true`, `replace_paths`, отключение современного
вооружения, свои идеологии, формы правления, товары, технологии, решения, события и облик интерфейса.
