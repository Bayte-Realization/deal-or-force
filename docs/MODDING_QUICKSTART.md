# Your first mod in fifteen minutes

*Русская версия: [MODDING_QUICKSTART_RU.md](MODDING_QUICKSTART_RU.md)*

The full modder's reference is [MODDING_GUIDE.md](MODDING_GUIDE.md). It is thorough and you
will need it. But not today.

Today we will do four things, each one end to end, and after every one of them you will see
the result in the game:

1. change one number and confirm the game obeyed it;
2. add a **new commodity** to the world economy;
3. add a **building** that produces it;
4. translate all of it into another language.

No compiler, no IDE, no engine sources. A text editor and the game.

---

## What a mod is in this game

A folder. In it, a `manifest.json` and some subfolders by subject. The game reads them over
its own data — your file does not replace the base one whole, it **adds to it and overrides
the keys that match**.

The game looks for mods in two places:

- `res://mods` — beside the project, for development;
- `user://mods` — installed, and they survive a game update.

Folders whose name begins with `_` are deliberately **not loaded** — those are templates.

---

## Step 1. Make a mod and get it to say hello

Copy a template out and rename it:

```
mods/_templates/balance_and_script   →   mods/my_first_mod
```

Open `mods/my_first_mod/manifest.json` and make it look like this:

```json
{
  "id": "my_first_mod",
  "name": "My first mod",
  "version": "1.0.0",
  "supported_game_version": "0.1.*",
  "enabled": true,
  "load_order": 100
}
```

What matters here and why:

- **`id`** — the mod's permanent name. Dependencies find it by this, and a save uses it to
  know which mods it was written with. It cannot be changed after publication.
- **`enabled`** — if `false`, the mod sits there and does not load. That is your switch.
- **`load_order`** — who goes first. A lower number loads earlier. Whoever goes later
  overrides the keys that match. If two mods argue over one number, the one with the higher
  `load_order` wins.
- `gdscript_entry` and `csharp_assembly` are optional, not needed today; remove them.

Start the game. The main menu will show the number of loaded mods. The detail is in Godot's
Output with the `[mods]` prefix.

> **If the mod did not load:** it is almost always either `enabled: false`, or a folder name
> starting with `_`, or JSON that will not parse. The third one the game writes to the log
> with a line number.

---

## Step 2. Change one number

The fastest check that a mod works at all.

Create `mods/my_first_mod/common/buildings/cheap_farms.json`:

```json
{
  "Agriculture": {
    "cost": 0.20,
    "days": 60
  }
}
```

That is all. You did not rewrite the building — you overrode **two keys** of it. The name,
the description, the GDP gain and everything else stayed as the base game has them.

The base values were `cost 0.45 · days 120`. They are now `0.20` and `60`: half the price and
half the time.

Start the game, pick any country, open the agricultural complex in construction — the cost
and the time have changed. **That is modding.** Everything after this is a matter of scale.

> **A rule that will save you hours:** one file, one subject. Do not put army changes into
> `cheap_farms.json` as well. In a month you will not remember what is where, and the game
> merges everything into one picture and will not remind you.

---

## Step 3. A new commodity in the world economy

Commodities live in `common/goods`. Let us look at how a base one is built before making our
own:

```json
"Food": {
  "name": "Food",
  "icon": "●",
  "base_price": 0.012,
  "essential_weight": 1.0,
  "description": "Household food supply and agricultural output."
}
```

- **`name`** — the English string. It is also the **localisation key**: the game looks for a
  translation by it.
- **`base_price`** — the starting price. The market moves it from there on its own.
- **`essential_weight`** — how much of a necessity this is. Food is `1.0`: a shortage of it
  hurts people most. A luxury would be near zero.

### First check it does not already exist

Before "adding" something, look at the list of what is already there. If you write a key the
game already has, you have not added a commodity — you have silently **overridden** a base
one, and you will then spend a long time working out why the prices went strange.

Open `game/common/goods/00_goods.json` — or, faster, press `~` in the game and type
`market`: it lists everything the world trades.

The base game has twenty commodities:

```
Food · ConsumerGoods · IndustrialMaterials · Energy · Arms · Coal · Oil · IronOre
RareEarths · Machinery · NaturalGas · Timber · Bauxite · Uranium · Aluminium
Chemicals · Fertiliser · Electronics · Textiles · Pharmaceuticals
```

Rare earths are already there. Lithium is not. We will take that.

### Your own commodity

Create `mods/my_first_mod/common/goods/lithium.json`:

```json
{
  "Lithium": {
    "name": "Lithium",
    "icon": "◆",
    "base_price": 0.062,
    "essential_weight": 0.12,
    "description": "Lithium carbonate for batteries and energy storage."
  }
}
```

The key `"Lithium"` is the identifier for code and other files. `"name"` is what a person
sees.

Start the game, open the market or type `market` in the console — the commodity is there. It
has a price, something moves that price, and it can be bought. You added a line to the world
economy with one file.

> **Why `base_price` is so small.** Prices in this game are fractions, not currency. Read
> them relative to each other: food 0.012, lithium 0.062 — so lithium is five times as
> expensive as food per unit. That is the only thing that matters.

---

## Step 4. A building that produces it

A commodity nobody makes is decoration. Let us give it a source.

The base game has seventeen buildings:

```
Agriculture · CivilianIndustry · Infrastructure · University · Hospital · Barracks
ExtractionIndustry · PowerPlant · ArmsIndustry · Refinery · ChemicalPlant · Smelter
ElectronicsPlant · Shipyard · Port · RailNetwork · ResearchLab
```

There is no lithium mine. Create
`mods/my_first_mod/common/buildings/lithium_mine.json`:

```json
{
  "LithiumMine": {
    "name": "Lithium mine",
    "cost": 0.90,
    "days": 200,
    "capacity_gain": 0.04,
    "description": "Brine extraction and carbonate refining."
  }
}
```

Compare it with the agricultural complex (`0.45 / 120 / 0.22 / 0.05`): twice the price,
longer to build, more GDP but less capacity. That is a meaningful trade rather than random
numbers — and that is how balance should be chosen: **always relative to what already
exists**.

For the mine to actually **produce** lithium it needs a recipe in `common/production`. How
that works is covered in detail in [MODDING_GUIDE.md](MODDING_GUIDE.md), and a complete
working example is in the `mods/_templates/production_chains` template. It loads as it is —
copy it and look.

---

## Step 5. Translate it

Right now your commodity is called "Lithium" on every screen, in every language. Let us fix
that.

In this game **the localisation key is the English string**. Not `GOOD_LITHIUM_NAME` but
literally `"Lithium"`. The reason is simple: something untranslated shows up in English —
which is to say, meaningfully — rather than as a hole reading `GOOD_LITHIUM_NAME`, from which
a player learns nothing at all.

Create `mods/my_first_mod/localization/russian/10_goods.json`:

```json
{
  "Lithium": "Литий",
  "Lithium carbonate for batteries and energy storage.":
    "Карбонат лития для аккумуляторов и накопителей энергии.",
  "Lithium mine": "Литиевый рудник",
  "Brine extraction and carbonate refining.":
    "Добыча из рассолов и переработка в карбонат."
}
```

Files inside a language are merged **in filename order**, and the later one wins. Which is
why the names start with a number: `10_goods.json` loads after `00_*` and overrides it if
need be.

Switch the language in the main menu — the names are translated, **immediately, with no
restart**.

> **The exception to "the key is the English string".** Country and region names are keyed by
> tag: `country.POL.name`, `region.pl_ds.name` (Lower Silesia). The reason is that "Southern"
> is six different regions in six countries, and in a flat table keyed by name they would all
> share one translation. Three letters mean one country; an English phrase means everything
> that happens to be spelled the same way.

---

## How to check you have not broken anything

Three levels, from quick to strict:

**1. The load log.** Godot's Output, `[mods]` prefix. It says what loaded and what failed to
parse.

**2. The in-game console.** `~`, then:

```
validate     check the data's integrity
reload       re-read the mods without restarting
market       see your commodity in the market
defines      see which constants are actually in effect
```

**3. The bench.** If you have the sources:

```bash
dotnet run --project Tools/SimBench -c Release
```

It runs the world and checks several hundred assertions, from "every country has a capital"
to "ten years of war did not shred the map". A mod that broke the economy will fail it
loudly.

---

## Five mistakes everybody makes

| Mistake | How it looks | Cure |
|---|---|---|
| The folder starts with `_` | The mod is not in the loaded list | Rename it |
| `enabled: false` | The same thing | Set it to `true` |
| A comma after the last key | A parse error in the log | Remove it |
| Overriding the whole object instead of a key | The description and the icon vanished | Write only the keys you are changing |
| The translation did not take | A translated screen with English text on it | The key has to be the **exact** English string, character for character |
| "Added" something that already existed | Prices or balance went strange for no reason | Check the list first: `market`, `buildings`, `defines` in the console |

The last one is the most common. If the base says `"Rare earth mine"` and you translated
`"Rare Earth Mine"`, those are two different keys, and the second will never match.

---

## What next

The templates in `mods/_templates` are not documentation, they are **working mods**. Every
key of every template is checked by the bench, so they cannot go quietly stale:

| Template | About |
|---|---|
| `balance_and_script` | What you just did, plus a GDScript entry point |
| `production_chains` | Commodities, buildings and production recipes, whole |
| `central_bank` | The monetary model: inflation, the key rate, the price of cheap money |
| `trade_instructions` | Trade controls and how they explain themselves to a player |
| `statecraft` | Diplomacy and the AI: the price of war, reputation, coalitions |
| `border_redraw` | Who owns what, without rebuilding the raster |
| `map_data_override` | The shape of the world itself |
| `interface_reskin` | Colours, panel layout, localisation |
| `csharp_mod` | A new mechanic in C# |

Copy one out, give it a new `id`, set `enabled: true` — and off you go.
