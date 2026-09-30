# Private military company template

A company that fights for money: a movement (a country without land) with the `for_hire` trait.
The base game ships none - its world is the real one of 2024, and every company here would be
invented - so this is where one lives.

1. Copy this directory out of `_templates`, give the folder and the manifest a unique id and
   set `"enabled": true`.
2. `common/movements/50_private_military_company.json` declares it: a tag, a headquarters region,
   money, cadres, a government and its traits. Every field and trait is described at the top of
   the base game's `common/movements/00_movements.json`.
3. `for_hire` gives it "Take a contract" and half-price recruiting, and takes away territorial
   uprisings: a contractor works for somebody else's war. A company that is meant to carve out a
   country of its own - a warlord, an alternate history - adds `territorial_ambition` as well.
4. Its name, and its description in any language but English, go in
   `localization/<language>/`: `country.<tag>.name`, and the description's English text as a key.
5. Its flag is `gfx/flags/flag_<tag>.png` in lower case, found by its file name: this one is
   `gfx/flags/flag_vsg.png`.

Movements are founded when a new campaign starts; a saved game keeps the world it was saved with.
