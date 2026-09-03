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
        units/           division templates: attack, armour, width, supply use
      events/            scripted events and their outcomes
      gfx/               which picture the game means by a name
        interface/       icons, panel frames, the logo
        globe/           what is drawn on the globe itself
      gui/               where the panels sit - anchors and offsets
      interface/         what the game is made of - the colour palette and its metrics
      history/           what was true when the bookmark opens
        countries/2024/  money, people, armies, stability, war support
        provinces/       the political map: one line per province
        states/          regional starting values
        units/           standing army sizes, and the navies of the bookmark
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

**Swap the artwork.** The interface never names a file, it names a sprite, and `gfx/`
says which picture that is. `gfx/interface/50_my_icons.json`:

    {
      "sprites": {
        "icon_diplomacy": "art/my_diplomacy.png",
        "panel_dossier": { "texture": "art/my_frame.png", "tint": "#c8b48a" }
      }
    }

Paths are relative to your own mod folder, so the art ships beside the line that names it
and neither has to know where the player installed you. An absolute `res://` path is left
alone, which is how the base game points at its own `Assets/` tree.

List only the sprites you are replacing: the other thirty are inherited. `game/gfx/` is
the full set of names, and it is the only place they are written down.

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
