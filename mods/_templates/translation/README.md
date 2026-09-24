# Translation template

A new language is a folder of JSON, not a build. Copy this directory out of `_templates`, give
the folder and the manifest a unique id, set `"enabled": true`, and start the game: the language
is on the selector in the main menu.

1. `localization/<language>/` holds the translation, in as many files as you like, read in
   file-name order. The key of every line is the English source text; the value is yours.
2. The three `language.*` lines at the top say what the language is:
   - `language.name` — its name in itself, as the selector shows it;
   - `language.plural` — how it counts: `one-other` (English and most), `slavic` (Russian,
     Polish, Czech) or `single` (Japanese, Chinese, Korean, Turkish). A line with a count in it
     then has `|few` and `|many` variants where the language needs them;
   - `language.culture` — how it writes numbers, as a culture name (`de-DE`, `pl-PL`, `ja-JP`).
3. Every line the game has: run `loc german template` in the console (`` ` ``). It writes
   `user://mods/translation_german/` with all of them — what is translated already kept, the
   rest in English to be written over.
4. A line not translated yet shows in English, so the translation works from its first line.
   The console command `loc german` lists what is left.
5. Letters the game's face does not have — Chinese, Japanese, Arabic, Hindi — need a face that
   does: declare it as `ui.default` in this mod's own `fonts/` folder and ship the file beside
   it. The format is in `game/fonts/00_fonts.json`.

The bench (`SimBench --loc`) checks every loaded language: that it names itself, that its plural
rule and culture exist, and that no line asks for an argument its English line does not pass or
has a brace that does not close. Run it before you publish; each of those would otherwise show a
player `{2}` or a raw pattern, and nothing else would tell you.
