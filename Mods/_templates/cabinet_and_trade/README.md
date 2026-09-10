# Cabinet and trade template

Two of the smallest, most visible levers in the game — what the government is *for* this
year, and how open the border is — are files now, and this template changes both.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game. The new cabinet and the new trade
stance appear as buttons on the state affairs screen, named and explained out of your files.

## What is in here

**`common/government_focus/cabinets.json`**

- **`industrial_mobilisation`** — an id the base game does not use, so it is a **sixth
  cabinet**. It takes one of sixteen reserved slots. The interesting part is that it is not
  only the player's: the AI governments of the world choose from the same list, so a mod that
  adds a priority adds it to two hundred and sixty countries.
- **`education_and_science`** — the same id the base game uses, so this **replaces** that
  cabinet: twice the literacy, a far higher productivity target, and growth given up for it.

**`common/trade_policies/stances.json`**

- **`imperial_preference`** — a fourth stance, in the eight reserved slots.
- **`strategic_autarky`** — the shipped one, made harsher.

## What a cabinet may do

`effects` are weekly, except `growth`, which is annual because that is how growth is quoted:

| channel | what it moves |
| --- | --- |
| `growth` | the annual growth the economy converges on |
| `living` | the standard of living |
| `literacy` | literacy |
| `radicalism` | radicalism in every region |
| `stability` | stability |
| `war_support` | war support |
| `productivity_target` | the productivity literacy is pulling the country towards |

A cabinet with an empty `effects` is legal and is exactly what "Balanced" is: a government
that has decided not to lean on anything.

## What a trade stance may do

`import_access` and `export_access` multiply what the country can buy and sell abroad;
`tariff` is the share of the value of its trade the customs house keeps. There is no free
side: the stance that buys most easily collects almost no duty, and vice versa. Two players
may not sit down together in a multiplayer campaign with different rule content, so a stance
that changes these numbers is a rule change and both sides need the mod.

## Things that will bite

- **Sixteen cabinets and eight stances.** Both are enums underneath, because a country
  carries one of each in every save ever written. Beyond the headroom, the loader says so
  and refuses the entry rather than silently colliding with another.
- **Ids are matched loosely.** `education_and_science`, `EducationAndScience` and
  `educationandscience` are the same cabinet. Choose a distinctive id for a new one.
- **A save remembers the slot, not the name.** A country that adopted your sixth cabinet and
  is then loaded without your mod comes back with a priority nothing describes. That is
  survivable — it reads as an unnamed cabinet with no effects — but it is why removing a mod
  mid-campaign is not free.
- **`name` and `description` are localisation keys.** Put them in your mod's
  `localization/<language>.json` and they translate like everything else; leave them out and
  they render as the English you wrote, which is also fine.
