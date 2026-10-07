# Invented regional civic movements

Sixteen civic organisations for regions with a people of their own - a Bavarian forum, an Assam
assembly, Tatar, Bashkir, Udmurt, Karelian and other councils. None of them exists: they were
written as examples of the civic-movement lifecycle (`can_emerge`, `emergence_threshold`,
`start_active`) and as a way to make regional discontent organise. The base game's world is the
real one of 2024, so they live here, and the base game ships real movements instead
(`common/movements/40_movements_2024.json`).

To use them, copy this folder out of `_templates`, give it and its manifest a unique id and set
`"enabled": true`. Each one is a row in `common/movements/30_regional_voices.json` with the
fields described at the top of the base game's `common/movements/00_movements.json`; their
flags are in `gfx/flags/` and their words in `localization/`.
