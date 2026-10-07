# Historical flags template

The base game used to fly a historical flag whenever a country became somebody's subject or
changed its politics: a British colonial ensign for a protectorate, the flag of a wartime
puppet state for a puppet, the banner of a 1930s regime for a fascist government. Palestine
under Russian protection flew the ensign of the British Mandate. None of that says anything
true about a country in 2024 or later, so the base game now flies a country's own flag in
those cases, and the old files live here.

What is in `gfx/flags/`:

- `flag_<tag>_protectorate.png` - colonial-era ensigns and protectorate flags;
- `flag_<tag>_puppet.png` - flags of states installed by an occupier in the 1930s-40s;
- `flag_<tag>_fascism*.png` and `flag_ideology_fascism.png` - flags of historical fascist and
  authoritarian regimes;
- `flag_jpn_at_war.png` - the Rising Sun ensign.
- `flag_omn_absolute_monarchy.png` - the plain red flag of Muscat and Oman before 1970. In the base
  game it outranked the modern flag, because Oman is an absolute monarchy today.
- `flag_sau_traditionalism.png` - the flag of the Arab Revolt and the Kingdom of Hejaz;
- `flag_nic_socialism.png` - the flag of the Sandinista front, a party's rather than the state's;
- `flag_grc_parliamentary_republic.png` - the Greek land flag, the cross alone, flown until 1978.
  Each outranked the country's modern flag in the base game, because each country is what the
  file is named for in 2024.

To use them, copy this folder out of `_templates`, give it and its manifest a unique id and set
`"enabled": true`. The flag rules are in the base game's `common/flags/00_flags.json`: a file
named for a subject type or an ideology is flown while the country is that, and the most
specific file wins. `CREDITS.md` lists the source and licence of every file.
