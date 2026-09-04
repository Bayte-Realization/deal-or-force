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
        decisions/       the buttons a government may press
        defines/         every tuning number in the simulation
        goods/           the commodities the market trades
        modifiers/       timed effects that can be granted to a country or region
        scripted_effects/    named consequences, referred to by "use"
        scripted_modifiers/  named scores: how much an AI wants something
        scripted_triggers/   named conditions, referred to by "use"
        units/           division templates: attack, armour, width, supply use
      events/            scripted events and their outcomes
      gfx/               the game's pictures, and what each one is called
        interface/icons/ the icons on buttons
        interface/panels/ the frames panels are drawn on
        globe/           what is drawn on the globe itself
      gui/               where the panels sit - anchors and offsets
      interface/         what the game is made of - the colour palette and its metrics
      history/           what was true when the bookmark opens
        countries/2024/  money, people, armies, stability, war support
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
      documentation/     this file

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
