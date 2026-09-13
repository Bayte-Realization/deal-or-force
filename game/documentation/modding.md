# Modding Deal or Force

The layout follows Paradox titles, because that is the layout people who mod this kind of
game already know. One rule underlies all of it:

> **A category is a folder. Every file in it is read, in file-name order. Later files win.**

That is why the base game's own files are numbered — `00_combat.json`, `00_goods.json`.
A mod drops `50_my_balance.json` in beside them, writes only what it changes, and inherits
everything else. It never copies a base-game file, so it never goes stale when the next
patch edits one.

The base game is not a special case. It is simply the first entry in the load order, and it
is arranged exactly the way it asks a mod to arrange itself. SimBench enforces this: it
writes a file into a real category folder, checks it wins, and takes it away again.

A file that will not parse is skipped and reported. One typo costs that file, not the
category, and never the executable.

## Where things live

    game/
      common/            what may exist - the rules and the catalogues
        ai_strategy/     how the AI weighs war, courtship and its budget
        buildings/       what can be built, what it costs, what it gives
        countries/       what a country IS: name, map colour, capital
        cultures/        the peoples of the world, and what each of them believes
        professions/     what people do for a living, and which class each trade belongs to
        religions/       the faiths
        decisions/       the buttons a government may press
        defines/         every tuning number in the simulation
        goods/           the commodities the market trades
        modifiers/       timed effects that can be granted to a country or region
        opinion_modifiers/   why one country thinks what it does of another
        scripted_effects/    named consequences, referred to by "use"
        scripted_modifiers/  named scores: how much an AI wants something
        scripted_triggers/   named conditions, referred to by "use"
        units/           division templates: attack, armour, width, supply use
      events/            scripted events and their outcomes
      gfx/               the game's pictures, and what each one is called
        flags/           one flag per country: flag_fra.png IS the flag of France
        interface/icons/ the icons on buttons
        interface/panels/ the frames panels are drawn on
        globe/           what is drawn on the globe itself
      gui/               where the panels sit - anchors and offsets
      interface/         what the game is made of - the colour palette and its metrics
      history/           what was true when the bookmark opens
        countries/2024/  money, people, armies, politics - and how each country lives:
                         literacy, urbanisation, the share of it working the land
        pops/            who lived where in 2024, by people and by faith
        provinces/       the political map: one line per province
        states/          regional starting values
        units/2024/      standing army sizes, and the navies of the bookmark
      fonts/             the typeface the interface is set in - the engine's by default
      music/             the soundtrack - empty in the base game, yours to fill
      sound/             what each moment sounds like, and how the channels behave
      localization/      one folder per language, as many files as you like
        english/
        russian/
      map_data/          the map itself
        state_regions/   corrections to the region layer, applied over the source map
        hydrography/     rivers and lakes
        lakes/           which water is big enough to be a region nobody can march across
        compiled.json    written by the compiler: which region layer provinces.png was cut from
      documentation/     this file

    dlc/                 content packs that ship with the game - see dlc/README.md

`common/` says what may exist; `history/` says what was true in 2024. A total conversion
usually wants one without the other, which is why they are apart.

`interface/`, `gui/` and `gfx/` are apart for the same reason: colour, position and
artwork are edited by different people. A retint never opens `gui/`; a layout mod never
opens `gfx/`.

Where a `gfx/` file sits does not matter - the whole tree is read, subfolders and all, in
path order. The subfolders are there to be read by people, not by the game.

## The pieces you are most likely to want

**Rename a country, or repaint the map.** `common/countries/50_my_names.json`:

    {
      "FRA": { "name": "The French Republic", "color": "#2a4f8f" },
      "DEU": { "capital_name": "Frankfurt" }
    }

**Change the balance.** `common/defines/50_my_balance.json`. Any key from the shipped
files; anything you leave out keeps its value.

    { "DefenderAdvantage": 2.8, "AttackerCasualtyMultiplier": 2.0 }

**Move a panel.** `gui/50_my_layout.json`. Anchors are fractions of the screen, offsets are
pixels from them, in the order left, top, right, bottom. Write one and inherit the other.

    { "layout": { "hud.army_panel": { "offsets": [14, 140, 560, -190] } } }

**Give a country a navy.** `history/units/2024/50_my_fleets.json`. A fleet is hulls at a
place; its home port is worked out from that place, so you write coordinates you can read
off any map rather than a province id that moves when the map is recompiled. Naming a
country replaces its fleets and leaves everyone else's alone.

    { "fleets": { "ARG": [ { "name": "Fleet of the South Atlantic",
                             "hulls": 24, "latitude": -38.1, "longitude": -57.4 } ] } }

**Train troops for a landing.** `common/units/50_my_templates.json`. `amphibious` is the
share of a landing's cost a template is trained out of — 0 is a line division doing
something it was never meant to, 1 is troops for whom a defended beach is the ordinary way
to arrive. A mod may set one field and inherit the rest.

    { "mountain_rangers": { "name": "Mountain rangers", "amphibious": 0.4 } }

**Translate the game.** `localization/<language>/50_my_strings.json`. The base game is
written in English, and any caption the interface passes through is looked up by its own
English text, so an untranslated string renders as itself.

**Redraw a border.** `history/provinces/50_my_borders.csv`, one line per province you
change — `province;owner;controller`, with the controller left off when the owner holds it.
Provinces you do not mention keep what the map gave them.

**Swap the artwork.** Put a picture in your own `gfx/` tree with the same name as the one
you are replacing. That is the whole procedure - there is no file to edit:

    my_mod/gfx/interface/icons/icon_diplomacy.png

A picture's file name *is* its sprite name. The interface asks for `icon_diplomacy`, the
game finds whichever file is called that, and the last one loaded wins - so yours does.
Where in the tree you put it makes no difference; the subfolders are for people.

`game/gfx/` is the full set of names, because it is the full set of pictures.

For what a file name cannot say - how many frames a strip holds, a tint to multiply a
picture by, or a sprite whose name should differ from its file - write a declaration
beside it. Paths in one are relative to your `gfx/` folder:

    {
      "sprites": {
        "globe_plan_arrows": { "frames": 4 },
        "panel_dossier": { "texture": "interface/panels/mine.png", "tint": "#c8b48a" }
      }
    }

**Change what the game sounds like.** The code names the moment - a war was declared, a
battle was lost - and `sound/` says what that is. `sound/50_my_audio.json`:

    {
      "cues": {
        "war_declared": "audio/mine.wav",
        "battle_won": { "cooldown_ms": 12000 }
      }
    }

A bare path replaces the file and keeps the timing; a declaration changes only the fields
it names. The six moments are in `game/sound/01_cues.json`, the three channels and how
they interrupt each other in `00_channels.json`.

**Add a soundtrack.** The base game ships none, so `music/` is usually a mod's list alone:

    {
      "tracks": {
        "cold_front": { "file": "audio/front.ogg", "when": "campaign", "weight": 2 }
      }
    }

`when` is `menu`, `campaign` or `any`; `weight` is how often a track comes round against
the others.

**Give a country a different army and navy.** `history/units/2024/50_my_forces.json`:

    {
      "GBR": 400000,
      "fleets": {
        "GBR": [
          { "name": "Grand Fleet", "hulls": 60, "latitude": 50.2, "longitude": -4.1 }
        ]
      }
    }

The number is soldiers under arms; listing a country under `fleets` replaces that
country's navy and leaves everybody else's alone. A fleet is hulls at a place - its home
port is worked out from the coordinates, so you write something you can read off a map
rather than a province id that moves when the map is recompiled.

**Ship a font.** The engine's own face covers English and Russian. A language it has no
glyphs for needs one of yours, or every string you translated renders as empty boxes.
`fonts/50_my_font.json`:

    {
      "fonts": {
        "ui.default": "fonts/NotoSansSC-Regular.ttf"
      }
    }

**Write a decision.** A decision used to need a `kind` - a consequence compiled into the
game - so a mod could rename the five that shipped and write no sixth. A decision with an
`effects` block needs no kind at all. `common/decisions/50_my_decisions.json`:

    {
      "land_reform": {
        "name": "Land reform",
        "description": "Break up the estates. The countryside is quieter and poorer for a while.",
        "cooldown_days": 1800,
        "gdp_cost_share": 0.0030,
        "unavailable": "Requires an unhappy countryside",
        "trigger": { "use": [ "restive" ], "max_standard_of_living": 0.55 },
        "effects": { "use": [ "civil_calm" ], "state.radicalism": -0.03, "gdp_percent": -0.002 },
        "ai_will_do": "ai_land_reform"
      }
    }

**Write the rules it leans on.** `trigger` and `effects` both accept `use`, which pulls in
named blocks from `common/scripted_triggers` and `common/scripted_effects`. That is what
those folders are for: write "this country is at war and can afford it" once, refer to it
everywhere, and change it in one place.

    // common/scripted_triggers/50_mine.json
    { "restive": { "max_stability": 0.45 } }

    // common/scripted_effects/50_mine.json
    { "civil_calm": { "stability": 0.020, "state.radicalism": -0.010 } }

A trigger block is an "and": every clause has to hold. Clauses are `min_<key>`,
`max_<key>`, `at_war` and `tags`. Effects are `<key>` to add, `<key>_percent` to scale by
(1 + value), `<key>_mult` to multiply, `<key>_set` to assign, and a `state.` prefix to
apply the entry to every region the country owns. `game/common/scripted_triggers` and
`scripted_effects` are the shipped libraries and the full list of keys.

**Say how much the AI wants it.** `ai_will_do` names a score in
`common/scripted_modifiers`. A decision with no score is never taken by an AI - so a mod's
decisions are the player's alone until you weigh them.

    {
      "ai_land_reform": {
        "base": 0.0,
        "modifiers": [
          { "trigger": { "use": [ "restive" ] }, "add": 1.4 },
          { "trigger": { "at_war": true }, "factor": 0.3 }
        ]
      }
    }

Each government weighs its decisions once a month and takes the best one that beats
`ai_decision_threshold` in `common/defines`.

**Split a region, or add one.** `map_data/state_regions/50_my_regions.json` lists regions
to carve out of the map. A region is a bounding box, a name and a population; the ground
comes from whatever the box covers, and the people are taken off whoever held it:

    {
      "regions": [
        {
          "map_index": 1740,
          "nation": "CHN",
          "name": "Baoding",
          "iso": "CN-HE-BDG",
          "kind": "Prefecture",
          "donor_iso": "CN-HE",
          "carve_from_iso": "CN-HE",
          "population": 10880000,
          "min_longitude": 113.5, "max_longitude": 117.2,
          "min_latitude": 38.55, "max_latitude": 39.5
        }
      ]
    }

`donor_iso` says who loses the people. `carve_from_iso` says whose ground may be taken -
without it a box takes any ground the country holds inside it, which matters whenever the
region you are splitting wraps around another: a box over Hebei contains Beijing and
Tianjin, and would quietly swallow two municipalities.

**Split a region into several.** A box cuts along a parallel, and a border ruled along a
parallel looks exactly like what it is. A partition grows each share out of a point instead,
so the border lands where two shares meet and follows the ground:

    {
      "partitions": [
        {
          "nation": "CHN",
          "carve_from_iso": "CN-HE",
          "pieces": [
            { "map_index": 1099, "name": "Shijiazhuang", "iso": "CN-HE-SJZ",
              "kind": "Prefecture", "population": 23660000, "seed": [114.51, 38.04] },
            { "map_index": 1721, "name": "Zhangjiakou", "iso": "CN-HE-ZJK",
              "kind": "Prefecture", "population": 3050000, "seed": [114.89, 40.82] }
          ]
        }
      ]
    }

`seed` is longitude and latitude. Every cell goes to whichever seed reaches it first by
land, so a piece cut off by a bay belongs to whoever is on its side of the water rather
than to whoever is nearer in a straight line.

Ground does not cost the same everywhere: smooth noise fixed to the map makes the front
advance faster through some country than others, so the border wanders instead of settling
on the straight line exactly halfway between two seeds. Put two seeds on the same meridian
and the wander is all you get - the border between them still runs east to west, because
that is where halfway is. Offset them.

Any share that ends up holding ground it cannot walk to hands that scrap to whichever
region it touches most, so a split never leaves a speck of the wrong colour sitting on its
own.

A piece may reuse the map index of the region being split - that share keeps being the same
region under a truer name, instead of leaving an empty region behind. Give every piece a
population or it inherits the national average, which for China is forty million a
prefecture.

`polygon` traces a border by hand when a box is too blunt, and `seed_points` reaches
islands a box would miss. Every rule is reported when the map is compiled, with the ground
its boundary left out, so a border you drew badly says so.

Rebuild the map after changing this: `dotnet run --project Tools/MapDataCompiler`.

**Decide which water is a place.** A lake past a certain size stops being scenery and
becomes a region of its own: it belongs to a country, it is drawn in that country's colour
like anywhere else, and nothing on foot may enter it. Armies march round it, fronts stop at
its shore, and supply passes it without paying for the detour. `map_data/lakes/00_lakes.json`
is the whole of it:

    {
      "minimum_cells": 40,
      "minimum_width_cells": 3,
      "first_map_index": 2000
    }

`minimum_cells` is how much water is worth a region at all. `minimum_width_cells` is how far
it is from the shore to the middle, and it is the number that matters: area alone would make
a region of every reservoir on every river, and a reservoir a few kilometres across is
something armies have always crossed - made impassable it reads as a wall down the middle of
a country rather than as water. Three cells is roughly forty kilometres of open water, which
is Ladoga, Baikal and Superior, and is not the Dnipro. Raise it and only the inland seas
count; lower it and every pond becomes a border.

The shapes themselves are not listed here - they come from `map_data/hydrography/`, so a mod
that adds a lake to the hydrology gets a lake region for free, and one that thinks the
threshold is wrong changes one number. Which country a lake belongs to is not listed either:
it goes to whoever holds the most of its shore, so moving the border moves the lake.

**When the map disappears.** Provinces are grown inside regions and never cross one, so
`provinces.png` is an *output* of the region layer rather than something that stands on its
own. Change the regions - through `state_regions/`, `lakes/`, hydrography, or the code that
draws them - and the raster on disk no longer fits. The game refuses a map it does not fit
rather than drawing a wrong one, so what you see is no map at all.

`compiled.json` is there so the refusal can say which half is stale. It holds a fingerprint
of the region layer the raster was cut from; if it disagrees with what the running build
draws, the message says whether to recompile the map or to rebuild the game first. It is
written by the compiler and is not worth editing - change it and you have only taught the
check to lie.

**Correct a country's statistics.** `history/countries/2024/10_statistics.json` holds three
numbers per country that decide how its regions are built:

    "DEU": { "literacy": 0.99, "urbanisation": 0.78, "agriculture": 0.012 },
    "ETH": { "literacy": 0.52, "urbanisation": 0.23, "agriculture": 0.630 }

`urbanisation` sets what trades the country's regions hold; `agriculture` sets the level the
curve in `common/professions/` could only guess at. Both are consumed before the population
is dealt out, which is why they live in `history/countries` rather than being corrected
afterwards - a figure applied after the deal is a figure the regions never saw.

A country not listed has all three worked out from what it earns per head. That guess is a
reasonable one and it is still a guess: income and urbanisation part company in both
directions, the Gulf being far more urban than its oil income implies and Rwanda far less
than its poverty does. So the honest way to make this game's statistics better is to add
countries to that file rather than to tune the curve, and `Tools/SimBench` checks that every
country which states a figure ends up with it - within a few points, because regions vary
around their national average on purpose.

**Change who lives somewhere.** Four folders, and they are worth taking together.

`common/religions/` and `common/cultures/` say what peoples and faiths exist. A culture is
a name, a family, a colour, and what most of it believes:

    "tatar":  { "name": "Tatar",  "group": "turkic",       "colour": "#3f9c6a", "religion": "sunni" },
    "yoruba": { "name": "Yoruba", "group": "niger_congo",  "colour": "#5f9f5f",
                "religion": { "other_christian": 0.55, "sunni": 0.44, "animist": 0.01 } }

The faith may be one entry or two. A people genuinely split down the middle is a fact about
the Yoruba and the Albanians and the Lebanese, and flattening it to a majority throws away
the most interesting thing about them. Only the two largest are kept: a region that carried
forty groups instead of a dozen would be one nobody can read and nobody can afford.

`common/professions/` is what people do for a living, and it is the bridge to the economy:

    "subsistence_farmers": { "name": "Subsistence farmers",
                             "class": "Lower", "sector": "Agriculture",
                             "rural_share": 0.720, "urban_share": 0.000,
                             "rural_falloff": 2.60, "urban_rise": 1.00,
                             "wealth": 0.16, "employment": 0.84, "colour": "#6f8a45" }

Every trade answers two questions. `class` is how well off these people are - **Lower**,
**Middle** or **Upper**. `sector` is what they live off - **Agriculture**, **Industry**,
**Services** or **Ownership**.

Be precise about what each currently decides, because it is less than the names suggest. The
sector decides who works the land, and through that how much a country grows and how urban
its regions read back. The class decides who turns up for work - the upper class supplies a
seventh of itself to the labour force, because owning is not a shift - and otherwise it is
what the population and ledger screens count by. Neither drives politics yet: radicalism
belongs to a region and its people inherit it. That is the gap this split exists to make
fillable, and a mod that wants class politics now has somewhere to hang it.

Keep them apart when you add a trade. They are separate because a landowner and a peasant
share a sector and share nothing else, while a machinist and a clerk share no sector and
stand at nearly the same height. One field answering both questions is how an earlier version
of this file came to treat landowners as townsfolk for every purpose farmland mattered for.

The two shares are the same trade in two different places: a farming region is mostly people
working their own land, a city is mostly wage labour, counters and desks, and every region
blends the two by how urban it actually is.

The two exponents say how fast each half arrives, and they exist because a straight line is
wrong about the one thing it matters most for. Work on the land does not decline evenly as
towns grow - it collapses. A country three quarters urban has almost nobody left farming,
while one a quarter urban still has half its people in the fields; `rural_falloff` above one
is that collapse, and `urban_rise` above one is a trade that waits for a real city, which is
what clerks and capital do.

Note that there are two farming trades, not one. **Subsistence farmers** are people feeding
themselves off their own land, largely outside the market - which is a true description of
much of the Sahel and the Himalayas and no description at all of Denmark. **Farmers** are
commercial agriculture: fewer people, machinery, selling a crop. Both are the Agriculture
sector and both are the Lower class, so the economy sees no difference; the population screen
does, and so does anyone reading it. A game that called the Danish countryside "peasants" would be using a nineteenth-century
word for a twenty-first-century country.

Shares are weights and are normalised, so you may add an eleventh trade without rebalancing
the other ten.

`history/pops/` is who was actually there in 2024. Two tables:

    "countries": {
      "CHN": { "primary_cultures": [ "han" ], "cultures": {
        "han": 0.9150,
        "uyghur":  { "share": 0.0080, "where": [ "CN-XJ" ] },
        "tibetan": { "share": 0.0050, "where": [ "CN-XZ", "CN-QH" ] } } }
    },
    "regions": {
      "CN-XJ": { "cultures": { "uyghur": 0.450, "han": 0.420, "kazakh": 0.062, "hui": 0.045 } }
    }

The `where` line is the one that matters. Without it a minority is spread evenly over the
whole country, which is how a game ends up with Tibetans in Shanghai and Chechens in
Kaliningrad. With it, the people's whole national head-count is placed in the regions named,
in proportion to how big they are, and everyone else fills what is left. It is one line, and
it is the difference between a map worth looking at and a map that is the same colour
everywhere. `regions` is the blunter instrument for the places a national average simply
lies about: a region listed there is taken at its word and takes no further part.

`primary_cultures` says whose state it is; everyone else in it is a minority, which is what
assimilation works on and what radicalises a region governed by somebody it is not. Belgium
has two, Switzerland four, and a game that assumed one would make foreigners of half of
both. Left unsaid, the largest people is taken to be the state's own.

Shares are normalised, so they need not add to exactly one.

**Change a flag, or any picture.** `gfx/flags/flag_fra.png` **is** the flag of France - the
file is the declaration, the same rule as every other picture in the game. Drop your own
`flag_fra.png` into your mod's `gfx/flags/` and nothing else has to be written down
anywhere; a country the base game has never heard of works the same way, and so does an
icon: `gfx/interface/icons/icon_diplomacy.png`.

Everything ships as PNG on purpose. Anybody can open a picture in whatever they already
have and paint over it; a vector file asks them to find and learn an editor first, and a
modding format that half the people who want to use it cannot open is not a modding format.
The resolution is the compensation - flags are 512 pixels tall and icons 256 square, far
larger than any panel draws them, so they are downsampled rather than stretched. Keep to
roughly those sizes and your art will sit beside the base game's without anyone noticing
which is which.

Flags all share a height and keep their own width, so a Nepalese pennant and a Swiss square
keep their shape. SVG still loads if you insist - it is last in the list of picture formats,
so a `.png` beside it wins - but nothing in the base game uses it.

**Change what countries remember.** An opinion is the sum of named reasons, each with a
size, a lifetime and whether it fades. `common/opinion_modifiers/50_mine.json`:

    {
      "declared_war_on_us": { "name": "Declared war on us", "opinion": -60,
                              "days": 3650, "decays": true, "max_stacks": 2 },
      "trade_agreement":    { "name": "Trade agreement", "opinion": 28,
                              "days": 0, "decays": false }
    }

`days: 0` means the reason is held until something takes it away - which is how a standing
condition like a pact or a shared border is written. A decaying reason is worth its full
size the day it happens and nothing the day it runs out. `max_stacks` is how many times the
same thing counts: a second denunciation stings again, a fifth does not.

Make the numbers bigger and your world holds longer grudges. There is nothing else to
change - the screens read the same sum, and so does the AI.

## What is still code

Region membership is geometric: a province is grown inside one region and never crosses its
border, so which region it belongs to is a fact about its shape rather than about its
politics. `map_data/state_regions/` corrects the region layer itself; it cannot hand a
province to a region it does not touch.

What goes *inside* a panel is code. A layout file can move, resize and re-anchor every
panel the game builds, but it cannot invent a new screen.

## Checking your work

`Tools/SimBench` runs the whole simulation headless and prints what it finds. It will tell
you if the shipped data has drifted from the compiled defaults, if a category stopped
loading, or if the world stopped governing itself:

    dotnet run --project Tools/SimBench/SimBench.csproj -c Release -- 730

A second argument reseeds the world, which is how to tell a real change from a lucky run.
