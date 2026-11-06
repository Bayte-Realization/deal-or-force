# Deal or Force — EntertAlpha 0.5.3.0

November 6, 2026

This update focuses on technical stability, graphics compatibility, interface
usability and tools for modders.

## Graphics and compatibility

- Corrected excessive map brightness and saturation in OpenGL/ANGLE by aligning
  color-texture and packed-lookup color-space handling.
- Added three map styles, independent cloud opacity, and consistent surface lighting.
  Choosing a compatible renderer does not reduce geographic detail.

- Fixed an OpenGL/ANGLE map shader error that could leave a white background with
  country names and unit markers instead of the map surface.
- Reorganized map textures: the full OpenGL/ANGLE material now uses 12 texture bindings
  instead of 26. Terrain, rivers, water, weather effects and strategic layers are retained;
  province ID maps are neither downscaled nor lossily compressed.
- Ownership, control and fog-of-war updates no longer require redundant 2D copies
  of the packed lookup textures.
- Automatic quality no longer selects low settings solely because OpenGL is in use.
  The player's manual quality selection still takes priority.
- Clarified the renderer selection description in English and Russian.

## Interface and stability

- Globe dragging now follows altitude above the surface and viewport resolution,
  making close tactical views controllable instead of racing past provinces.
- Fixed label placement/orientation for elongated countries after annexation,
  including Korean unification, and largest-landmass selection after border changes.
- Alert icons share the main HUD's restrained icon family; borders and counts retain urgency colors.
- Removed the category dropdown. All screens stay on one searchable rail with
  visible button borders and group headings; short displays scroll the list.
- The situation report opens by default; its button still opens the Desk.
- Added a short camera coast in both projections and an immediately applied,
  persistent Camera inertia toggle.
- War declarations open a shared confirmation screen with both sides' partners,
  current willingness forecasts, refusal reasons and selectable invitations.
  Forecasts are not random percentages or guarantees. AI attackers also call willing allies immediately.
- Multiplayer scenarios can be chosen before creating a lobby. Clients receive
  the host's world before registering a country; connected lobbies lock the scenario.
- Headlines show readable names, while technical country codes remain in clearly labelled tooltips.
- Removed duplicate music controls from navigation and the outliner. One permanent
  music note opens track selection and transport controls; playback continues when closed.
- Oversized external-mod responses now fail at the output limit without waiting for the full process timeout.

- Standardized Cooperation buttons with the rest of diplomacy. Actions have their
  own names, and search finds the specific action.
- Fixed map tooltips remaining over the in-game editor and the mod package picker.
- Fixed an error when opening the multiplayer menu without first visiting the
  single-player menu.
- Fixed errors when leaving an editor with Escape and accessing loading-screen
  controls that had already been freed.
- The loading screen correctly captures input until the world is ready.
- Corrected the Windows application's system name to Deal or Force.

## Formable countries and flags

- Fixed the 2024 Afghan government using the former republic's flag. Added explicit,
  moddable regime exceptions and 64 original alternate-history banners/names for 32
  countries; ordinary government changes still retain national artwork.
- Refined formation requirements: a large founder's territory can no longer make
  up for an entirely missing smaller founding component.
- Reunified China requires Taiwan; Greater Italy also requires San Marino, Vatican
  City and Malta. Smaller components are no longer lost to rounding.
- Closed opening-map shortcuts for Libyan and Palestinian reunification, plus an
  equivalent Sudanese path previously hidden behind the peace requirement. Libya,
  Sudan, Syria and Yemen require their full declared reunification territory;
  Palestinian reunification now also accounts for Gaza.
- Military occupation is not settled ownership. Genuine reunification remains
  possible after securing the required territory.
- Audited all 84 authored formation projects against the opening map.
- Authored national flags are now retained by default when ideology or government
  changes. Special rules for movements and formable countries remain in place.
- Reunified China's flag depends on its founder: the PRC or Taiwan.

## Supply and military priorities

- Fixed false inland encirclement in Kaliningrad and other home regions split across
  disconnected land fragments. Distribution points share regional capacity; losing
  the sea entrances still cuts a pocket off.
- AI priorities now account for exposed exclaves and their coastal entrances in
  opening deployment, defensive cover and assault selection, without overriding direct orders.

## Editors and modding

- Added BCE era selection, validation and localized civil dates without a displayed
  year zero. Dated events accept BCE/CE suffixes; ancient scenarios save and load normally.
- Land-agreement dates and negative ruler birth years no longer rely on a CE-only calendar.
- Added a bilingual calendar contract for modders. This is calendar infrastructure,
  not a complete Bronze Age ruleset or historical content pack.

- Added a shared create/select mod bar to the content, country, flag, interface and
  scenario editors, showing the current save destination.
- New packages are created with a manifest and localization folders under
  `Documents/Bayte Realization/Deal or Force/mods`. Existing folders are not
  overwritten; the player enables a new package through the launcher.
- Added `ReplaceSimulationPass` for core-code mods: replace or extend the built-in
  diplomatic action, logistics, warfare and construction passes. Conflicting
  replacements are explicitly rejected, and handlers are released on unload.
- Updated the compiled C# mod template for .NET 10, with expanded examples and guidance.
- Moved the `luau_events` example into the shared Luau template directory and removed
  duplicate handler definitions. It is a reference module, not an automatically active mod.
- Updated build documentation and third-party notices. Development-only `docs` and
  `licenses` directories are not added to the player package.

## Verification and known limitations

- The general CPU test suite completed 12,330 checks without failures. Additional
  checks cover the updated wording, reunification conditions and core-mod lifecycle.
- Real OpenGL, ANGLE/Direct3D 11 and Forward+/Direct3D 12 runs were checked on an
  RTX 3060, alongside English/Russian UI checks at 1920×1080 and 1280×720.
- The packaged executable completed loading and 60 simulated days. Its map was
  additionally checked through ANGLE from outside the source project directory.
- A follow-up test on an actual Intel HD Graphics 530 is still pending. Fixing the
  identified incompatibility does not guarantee support for every GPU or a particular FPS.
- Map colors and lighting can differ between renderers.
- Network compatibility of the new simulation-pass replacement API needs separate
  verification. Core-code mods are trusted code; test them on copies of saves.

Thank you to our first testers for reporting issues and helping improve the game!
