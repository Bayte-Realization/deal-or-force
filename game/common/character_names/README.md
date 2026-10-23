# Character name lists / Имена персонажей

These files feed the existing `Rulers` and cabinet simulation; they do not add
another population, election or character system. The shipped catalogue has 75
structured lists and 173 explicit country-tag bindings. The lists include male
and female given names, optional gendered family forms, optional middle components
and an authored display order. Related lists can inherit rather than duplicate data.

## Mod contract

Place JSON files in your mod's `common/character_names/` overlay. Normal game/mod
load order applies. A later row replaces only supplied fields; omitted fields
inherit the previous row. `inherits` selects an already-loaded parent list.
It does not copy the parent's country/culture bindings. Bind your new list explicitly.

```json
{
  "example_diaspora": {
    "inherits": "russian",
    "countries": ["X01"],
    "cultures": ["example_culture"],
    "pattern": "{given} {middle} {family}",
    "female_pattern": "{given} {middle} {family}",
    "male": ["Example"],
    "female": ["Examplea"]
  }
}
```

The example is intentionally tiny. Real content should supply many names.

- Required arrays after inheritance: `male`, `female`, `family`.
- Optional `female_family` is index-aligned with `family` and must have the same
  number of entries; an empty array uses the shared forms instead.
- Optional `male_middle` and `female_middle` supply a patronymic or another component.
- `pattern` defaults to `{given} {family}`. `female_pattern` defaults to `pattern`.
  Only `{given}`, `{middle}`, `{family}` placeholders are accepted; `{given}` is required.
  A list can author `{family} {given}` or `{given} bin {family}` without code changes.
  `family` is a component slot, not a claim that every culture uses hereditary surnames.
- `countries`, `cultures`, and optional culture `groups` bind identifiers, not translated names.
  Later bindings can redirect an alias; merely omitting an alias does not delete an old binding.
- Each array is bounded at 4096 entries; each token at 120 characters; patterns at 160.
  Invalid rows are rejected without replacing the previous valid list.

Selection priority: explicitly declared/founded culture, explicit country binding,
primary culture, then an explicitly authored culture-group binding. This matters
for generated tags such as `R01`: they need their local culture, not the tag's spelling.
Uncovered cultures retain the existing `common/rulers` fallback. Coverage is not
claimed complete, and the generic fallback is not suitable historical content for every culture.

## Identity, time and authority

Names are deterministic in campaign seed, country, generation and stream. Reading
or translating a UI does not consume simulation RNG or rename a serving person.
The visible name is canonical in both EN/RU; changing interface language does not
transliterate, reorder or invent a second identity. Generated characters are fictional,
not researched biographies. Authored scenario officeholders remain authored content.

An appointed minister stores name, birth year, trait and appointment day in
`Nation.CabinetPeople`; the payload participates in save and campaign fingerprint.
Old saves without it keep their existing displayed names. The simulation adopts
those names at a personnel review, assigning an initial age because the old format
never recorded one. This migration is not a claim about their historical ages.

Candidates keep their birth year while their cohort ages. New cohorts appear every
`Defines.PoliticianCandidateCohortYears` (default 5; clamped 1–50). Appointed people
remain independent of that preview pool. Ministers use the existing ruler mortality
settings and leave genuine vacancies; AI replacements use the priced appointment command.
Maximum age is `RulerMaxAge`; `RulerMaximumAge` remains an alias, not a second limit.

All recruitment/promotion writes go through authoritative commands and shared
eligibility/price gates. A candidate command can include `ExpectedName` to reject a
stale UI choice rather than silently appoint somebody else after a cohort refresh.

## Проверка и ограничения

Имена действительно используются генератором руководства и кандидатов, а не
только лежат в JSON. Проверки: `--person-names-only`, `--political-identity-only`,
нативный `--verify-political-appointments-only`, наблюдение мира через
`SimBench [days] [seed] --personnel-lifecycle`.

Это расширяемая основа, а не полная историческая просопография. Не обещаются
все культуры, все орфографические традиции или уникальность каждого сочетания
на бесконечном промежутке времени. Родственные варианты могут сознательно
делить компоненты. Полное дерево родственников и автономная партийная карьера
этим каталогом не реализуются. Существующие родовые имена монархов и исторические
назначения не заменяются случайным списком.
