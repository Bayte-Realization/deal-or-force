# Conflicts, backers and de facto powers

Three systems that make the world of 2024 a world at war with itself between its wars, and
the data each of them reads. All three are features (`common/defines/46_features.json`):
`escalation`, `proxy_wars` and `de_facto_factions`, each of which a mod can switch off.

## Escalation (`Scripts/Simulation/Escalation.cs`)

Every pair of countries with something between them has a **tension** from 0 to 100, reviewed
once a week. It rises toward what the pair's **drivers** add up to and falls when they do not:
hostility, grievance (land lost in the last war, decaying over decades), rivalry, a border,
an ideological gap, backing the other's enemy. Alliances, pacts and trade pull it down. A
**structural** part - an armistice line, a disputed border - never decays.

Tension moves through stages: Calm, Friction (25), Crisis (50), Clashes (75), and war. At
Clashes the border shoots: both sides take losses every week, war support rises, and the
`border_clashes` modifier applies. Incidents (`common/escalation`) fire at random under tension
and move it. The computer weighs tension when it decides whether to go to war; a country can
**hold talks** (Conflicts screen) to cool a crisis for a price.

Wars grow too: after `EscalationWarMajorDays` and a loss share they become a **major war**,
later a **total war** (call-ups, rationing, `major_war` / `total_war` modifiers), and the sunk
cost makes the side that has lost most less willing to stop.

## Proxy wars (`Scripts/Simulation/ProxyWars.cs`)

A country can back one side of a war it is not fighting, at three levels - advisers and money,
weapons, weapons at scale with volunteers - for a weekly share of its economy. The client gets
part of it as money, the rest as equipment for its most worn divisions, volunteers at level 3,
and a `foreign_backing_N` modifier. The side armed against remembers it.

The computer backs a side when the war is its business (a neighbour, an ally's defensive war,
its dependency, a rival on the other side - or it is a great power) and it likes that side
better. It stops when the case fades or the money runs out - except a backing **history
wrote**, which it keeps for `ProxyHistoricCommitmentDays` (730) whatever its opinions say.

It never arms a faction it lists as a terrorist or sanctioned organisation (`listed_by` below).

## De facto powers (`Scripts/Simulation/Factions.cs`)

Real organisations that held and governed ground of their own on 1 January 2024 without being
recognised states: the Rapid Support Forces, Ansar Allah, the Syrian Interim and Salvation
governments, North and East Syria, the Libyan National Army, Myanmar's National Unity
Government, Arakan Army and Kachin Independence Organisation, al-Shabaab, the Alliance Fleuve
Congo. They are countries on the map from the first day, cut out of the state whose ground
they hold - whole regions, and where the front ran through a region, by province.

A faction is a breakaway to the rules (`Reintegration`): the state it holds ground of can write
that ground back whole when it wins, and does not trade it for a white peace while winning.
An insurgency (the default) does not surrender for losing most of its ground while it has
`FactionFightOnFormations` formations and `FactionFightOnSupport` war support left. It opens
with the `seasoned_insurgency` modifier for `FactionSeasonedDays`.

A civil war is fought by its two sides. Allies and coalitions do not join it - their friends
back it instead - and only a state that one side answers to, or that answers to it, fights in
it (`CampaignDiplomacy.MayJoinCivilWar`). A faction's **patrons** are counted by anybody
thinking of attacking it (`FactionPatronDeterrence` of the patron's strength): the American
soldiers in Hasakah are why Turkey's threatened offensive never came.

### `history/factions/2024/*.json`

```json
{
  "factions": [{
    "id": "rapid_support_forces", "tag": "RSF", "name": "Rapid Support Forces",
    "description": "...",
    "parent": "SDN", "colour": "#8d6e63",
    "capital": "Nyala", "capital_at": [24.88, 12.05],
    "parent_capital": "Port Sudan", "parent_capital_at": [37.22, 19.62],
    "government": "military_junta", "ideology": "nationalism", "peoples": ["arab_sudanese"],
    "regions": ["sd_ds"],
    "areas": [[[lon, lat], [lon, lat], [lon, lat]]],
    "except": [],
    "holds_regions": ["sd_kh"], "holds": [],
    "relation": "war", "war_name": "Sudanese Civil War", "truce_days": 365,
    "fighters": 90000, "army_share": 0.0, "militia": 4,
    "stability": 0.35, "war_support": 0.8,
    "overlord": "", "subject_type": "",
    "patrons": ["ARE"], "listed_by": ["USA"],
    "parent_modifier": "", "insurgency": true
  }],
  "tensions": [ ... ], "sponsorships": [ ... ]
}
```

| field | meaning |
|---|---|
| `regions` | whole regions of the parent it owns |
| `areas` | rings of `[longitude, latitude]`; every parent province whose middle is inside is its own |
| `except` | rings inside its regions it does not own (a port the government still holds) |
| `holds_regions`, `holds` | parent ground it holds at war without owning it - occupied |
| `relation` | `war`, `truce` (for `truce_days`) or `peace` with the parent |
| `fighters` | its own men under arms, stood up as formations of 15 000 |
| `army_share` | share of the parent's formations that are the faction's instead |
| `militia` | militia formations stood up on its ground besides |
| `overlord`, `subject_type` | a state it answers to (the Syrian Interim Government and Turkey) |
| `patrons` | states that keep it going; opinion "armed us", and deterrence |
| `listed_by` | states that list it as terrorist or sanctioned; they never arm it |
| `parent_capital` | where the parent's government actually sat that morning |
| `parent_modifier` | a modifier on the parent for the same years (`junta_overstretched`) |
| `insurgency` | `false` for a faction that surrenders like a state |

Order matters: a province goes to the first faction that asks for it. `tensions` and
`sponsorships` have the format of `history/conflicts` and are applied once the factions exist,
so they can name them. A mod adds factions with the same document in its own `common/factions`
folder; an entry with the same `id` replaces the base one.

The province map is coarse where these wars are fought - Syria is thirty provinces. Use
`SimBench --provinces TAG[,TAG]` to list a country's provinces with their middles before
drawing rings, and `SimBench <days> <seed> --factions` to see what each faction got and what
became of it.

## Tuning

| define | default | what it does |
|---|---|---|
| `FactionFightOnFormations` | 2 | formations an insurgency needs to fight on with most of its ground lost |
| `FactionFightOnSupport` | 0.2 | war support below which it gives up anyway |
| `FactionSeasonedDays` | 1460 | how long `seasoned_insurgency` and a `parent_modifier` last |
| `FactionPatronDeterrence` | 0.25 | share of a patron's strength an attacker counts |
| `ProxyHistoricCommitmentDays` | 730 | how long a backing history wrote is kept |

## Reports

- `SimBench <days> <seed> --factions` - every faction at the start, each year and at the end; their wars.
- `SimBench <days> <seed> --war-ledger [TAG]` - every war of the run, its backers, the tension table.
- `SimBench --provinces TAG[,TAG]` - a country's provinces with their middles and regions.
