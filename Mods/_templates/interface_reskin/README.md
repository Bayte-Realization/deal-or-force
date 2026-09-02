# Interface reskin template

Every colour the interface draws with is a named token, so a reskin is a JSON file, not a
fork. Copy this directory out of `_templates`, give the folder and the manifest a unique
id, set `"enabled": true`, and start the game.

1. `interface/skin.json` overrides tokens by name. List only what you want to change.
2. Tokens are named by role, not by appearance — retinting `text.caption` moves every
   section heading in the game at once.
3. The full token list, with the values the base game uses, is in
   `game/interface/skin.json`. Copy from there.
4. Values are `#rgb`, `#rrggbb` or `#rrggbbaa`. A malformed value costs that one token and
   is reported in the mod's error list; the rest of the skin still loads.
5. `//` comments are allowed.

In game, the console command `skin` reports which tokens a mod has moved off their shipped
values, and `skin text` lists one family with a colour swatch beside each entry.

`localization/` is included here because a reskin usually wants to reword a few things too;
delete it if you do not. It works the same way — the key is the English source string.

## Layout

Panels are named rectangles too. `interface/skin.json` takes a `layout` section: four
anchors as fractions of the screen, then four offsets in pixels — Godot's own model, left,
top, right, bottom. Anchors and offsets are independent, so a panel that only needs to be
wider lists the offsets and keeps the anchors.

The template moves two panels as an example: a wider ledger, and the army roster on the
right instead of the left.

`skin hud.` in the console lists every panel with its current rectangle.

## What is still not moddable

What goes *inside* a panel. A layout file can move, resize or hide the screens the base
game builds; it cannot invent a new one or add a control to an existing one. That needs
either a C# mod or a further step this project has not taken.
