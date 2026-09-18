# The campaign interface — stage one

*Русская версия: [UI_REDESIGN_RU.md](UI_REDESIGN_RU.md)*

The continuation, and the current structure, is
[MODDABLE_CAMPAIGN_UI_RU.md](MODDABLE_CAMPAIGN_UI_RU.md). The repeating illustrated banner
from stage one is no longer used in the dossier. Formable nations, flag icons and previews,
and a new network lobby have been added since.

12 September 2026. Native Godot/C#, with no browser shell. The user's references were used
to organise the information, without copying other games' graphics.

## What changed

- Top-bar figures, vertical navigation on the left, a pinned dossier with a fixed heading
  and tabs and scrolling content, and a collapsible situation report on the right. The map
  stays reachable.
- Country: overview, government, society. Region: overview, population, province. Shared
  numeric rows are aligned, long values wrap, and the detail stays in tooltips.
- The economy is split into budget, market and construction. The market shows real
  production, demand, balance, price and satisfaction, with a search box and a shortages
  toggle. Construction has an explicit choice of your own region and a search across the
  building catalogue.
- A star on a good adds it to the watchlist in the situation report. That is a player
  preference, `watch.good.<Commodity>` in PlayerSettings — not a change to the campaign save
  format. Pressing it again removes the good. Clicking it in the report opens that good in
  the market.
- Decisions: separate cards with a description, a cost and a cooldown. Unavailable actions
  are disabled and state the real reason; execution uses the existing command queue.
- Both the whole report and its individual groups collapse. Jumps from market messages and
  from idle construction open the right tabs.
- Text fields hold WASD and Space back from the camera and the pause. Escape first removes
  focus from the field.
- The new captions have English and Russian localisation. Full localisation of the older
  content, of region names and of the old diplomatic and military messages is not part of
  this stage.

## Extension and styling

`game/gui/10_campaign_shell.json` declares the interface rectangles a mod can override.
`Scripts/CampaignShellUI.cs` holds the style, the navigation and the tabs;
`Scripts/EconomyWorkbenchUI.cs` holds the economic tools. The accent is a calm dark blue
ground, light readable text and a restrained brass for active actions.

The original illustration generated through the built-in imagegen is
`game/gfx/interface/panels/campaign_dossier_atlas.png`. The imagegen skill was used for a
single background painting, not for drawing text and buttons: the controls stayed real Godot
Controls.

The prompt:

> Use case: stylized-concept. Asset type: original in-game dossier header illustration for a contemporary geopolitical grand strategy game Deal or Force. Wide landscape 3:1 composition intended to be cropped into a shallow 440 by 100 pixel header. Subject: a modern statesman's desk with a large softly engraved world atlas, thin brass cartographic instruments and a muted silhouette of a modern parliament building and industrial harbor blending into a misty far background. Refined hand-painted editorial strategy-game illustration, restrained navy blue, desaturated slate teal, parchment ivory and subtle antique brass accents. Soft natural side light, quiet authoritative atmosphere. Large recognizable forms, low contrast, no busy micro details. Not science fiction, not medieval. Full bleed, no border, no UI, absolutely no lettering, numbers, logos, national flags, identifiable politicians, watermarks or commercial game assets.

## Verification

- Fennara project diagnostics: 400 files, 0 errors and 0 warnings. A separate Debug build
  and a run of Main.tscn.
- Visual checks at 1920×1080 and 1280×720: country, government, army, economy, region,
  population.
- `.fennara/scripts/runtime/campaign_workbench_check.gd`: real input for the filter, the
  star, collapsing and construction; a check that typing moves neither the camera nor the
  pause. A temporary South Korea session was used without saving the campaign, and the watch
  setting was restored afterwards.
- A temporary acceptance of an economic stimulus was verified: the action really executed
  and was then unavailable until its cooldown expired. Closing the dossier with the
  navigation button was checked at 720p.
- One intermediate run hung while loading the map topology, before the UI probe ran; after a
  restart the map loaded and the main pass completed. The cause of that single hang was not
  established, and it is not declared fixed.
- Release export, network play, the full mod set and all the military windows were not
  checked.

## Next stages

This is the first delivered part of a substantial rework, not a claim that the whole
interface is finished. What is still needed is separate passes over army and plan control,
full diplomacy, and the large policy and statistics screens. The existing simulation rules
and the balance have not been rewritten; the new tools work on top of the real data.
