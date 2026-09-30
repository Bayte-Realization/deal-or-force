# Player's guide

*Русская версия: [PLAYER_GUIDE_RU.md](PLAYER_GUIDE_RU.md)*

## What this game is

**Deal or Force** is a grand strategy game about the modern world. You run a state: its
budget, its construction, its diplomacy and its armed forces. The map comes as a rotatable
globe and as the classic flat plane that wraps around for ever.

The guiding principle of the interface: the map stays the workspace, and the panels explain
the consequences of an action before you press the button. Almost every number in the
simulation moves — the treasury takes taxes and pays for things, population grows or
shrinks, buildings make goods, armies lose men and get replacements, provinces change hands.

## Starting a campaign

1. Choose **Singleplayer** in the main menu.
2. Find a country by search or click it on the map.
3. Read its short dossier and confirm.
4. The campaign starts paused. That is the safe moment to set the budget and the army.

**Multiplayer · LAN / IP** is real networked play with an authoritative host. One player
presses `HOST`, the rest type their IPv4 address and press `JOIN`. For Radmin VPN or
Hamachi, use the host's virtual IPv4; the default port is UDP `7270`. The host starts the
campaign once at least two distinct country tags are registered in the lobby. Windows
Firewall has to allow the game's UDP traffic; playing directly over the internet without a
virtual LAN normally needs a forwarded port.

## Map controls

| Action | Control |
|---|---|
| rotate the globe / drag the flat map | hold RMB |
| zoom | mouse wheel |
| pan | WASD |
| select a country, region, army or battle | LMB |
| open a country's dossier and light up its borders | RMB on foreign land |
| add/remove a division from the selection | `Shift` + LMB |
| box-select armies | drag LMB across the map; `Shift` adds to the current group |
| march the selected divisions | RMB on a province you control |
| add a waypoint | `Shift` + RMB |
| globe / flat map | `F2` or the mode button |
| map smoothing on/off | `F3` or the button in the main menu |
| pause / resume time | `Space` (this key does not confirm buttons and does nothing else) |
| close the top window / open the pause menu | `Esc` |
| quick save / load | `F5` / `F9` |
| smooth horizontal map pan for showcases | `F6` or `PAN` on the map toolbar; press again to stop |

Close in, the internal provincial borders are visible; far out, the map keeps only the
strategically meaningful labels and markers.

### Map smoothing

The map is stored as a raster: every texel is tagged with the number of a country, a region,
a province and a terrain type. A number here is a label, not a quantity — the average of two
labels is not a label — so the raster cannot simply be filtered, and up close the grid it
was written on shows through. With smoothing on, close in every border is reconstructed from
the sixteen texels around the point: each label there makes a smoothed claim, the strongest
takes the pixel, and the line runs where the two strongest are equal. A coast digitised as a
staircase comes out as the coast it was digitised from, a round bay as a round bay, and a
one-texel island or city state keeps its place. Two terrains meet along the same kind of
curve, and the ground photographs are made seamless as they load, so the grid does not come
back through the fields either.

You can switch it off with **"Map smoothing"** in the main menu or with **`F3`** on the map
itself. Off is not a degraded version of on — it is a different picture: the raster exactly
as it was authored, pixels and all. That is a legitimate preference (a map whose pixels you
can count is a map whose data is visible to whoever mods it), and it is cheaper besides: the
noise that pulls borders off the grid is not computed at all, and terrain costs one texture
sample instead of two. The choice is remembered between runs — it lives in `settings.json`
beside your saves.

## Saving and loading

`Esc` opens the pause menu, and **Save / load campaign** opens the campaign archive. It has
separate autosave and quicksave slots plus five manual ones. An occupied slot's card shows
the player's country, the game date, the time it was written and the compressed file size.

- `F5` atomically updates the quicksave; `F9` loads it from anywhere in the campaign.
- Autosave fires on an interval of game days set in defines and never overwrites a manual
  slot.
- The game writes a temporary file first and only then replaces the old slot. An
  interrupted write therefore does not destroy a good save.
- The header holds the format version, the map topology version, the province count and a
  fingerprint of the mod set. If the mods differ, loading warns you; if the file is damaged
  or the map incompatible, it refuses to replace a live campaign.
- It is not only the date and the treasury that are saved: countries, regions, population
  groups, markets, construction, armies and their routes, fleets, wars, battles, provincial
  control and occupation progress, diplomacy, events, the Ledger's history and the state of
  the deterministic generator all come back together.

Files live in `user://save_games`. Each new slot is one `.bdfsave`: readable JSON by
default, or optional gzip. The game detects which it is from the file contents. Old
slots with a separate `.meta.json` still load.

## Time, economy and construction

The pause button and speeds 1–5 drive the calendar. The top bar shows your country, the
treasury, GDP, population and the date. Red strategic alerts mean something needs attention:
a battle, a cut-off army, a budget deficit or an available decision.

- **The treasury** takes tax revenue and pays for the state, construction, the army and the
  navy.
- **GDP** is the sum of the value actually produced, not a decorative constant.
- **Military spending** raises the army and navy you are allowed and the pace of
  replacements, and worsens the budget.
- **Construction** consumes construction capacity and completes gradually.
- **Population groups** have a profession, an income, a standard of living and a radicalism;
  the state of the economy drives their growth and mobility.

Hover a value to see it broken down. Before building, check the upkeep and the workforce you
have.

### Where GDP comes from

A country's GDP is the value of what it actually produced in a week, not a separate number
that grows by itself. The chain is simple: buildings in regions make goods, nobody makes
them without workers, the world market matches demand against supply and sets prices, and
"produced × price" is the output.

Practical consequences at the table:

- building industry where the people are beats building it where they are not: an
  understaffed factory does not run at full;
- taxes, stability, war and shortage do not "penalise GDP" directly — they change
  productivity, and productivity changes output. The effect is not instant, but it is real;
- a shortage of raw material or energy shows up in the market at once and in GDP only after.

On 1 January 2024 the economy is calibrated to match the real figures of that year — and
everything after that you do yourself.

### Chains: what is made of what

Twenty goods, and most of them are links rather than objects.

- **Bauxite** is useless on its own: it wants a smelter and cheap electricity, and then it
  becomes **aluminium**.
- **Electronics** want **rare earths**, which eight countries have and everybody needs. That
  is the tightest bottleneck on the map.
- A modern harvest is made of **natural gas**: gas → chemicals → **fertiliser** → food. A
  country cut off from fertiliser loses about a quarter of its harvest — the difference
  between exporting bread and queueing for it.

A broken supply does not stop industry, it slows it, and it does so with a week's delay: the
chain looks at last week's market clearing. Which is why an embargo does not bite at once —
and that is correct.

Two of the newer buildings are not industry at all. A **port** and a **rail network** are
reach: how much of its own shortfall a country is physically able to bring in. A landlocked
country with no rails sits in shortage however many friends it has — and can fix that by
building.

### Trade has names on it

The market is not an anonymous pot you can draw any amount from. Every week your shortfall
is matched against the surpluses of **particular countries**, and the result is a list of
routes. On day one there are about 2,200 of them; China runs half its trade through Russia.

Who sells to you is decided by diplomacy: war and embargo zero a pair out, a trade agreement
nearly doubles the pull, an alliance adds, a rivalry subtracts, distance damps. How much you
can actually bring in is decided by your docks and your rails.

The practical conclusion: **a large trading partner acquires real power over you**, and you
over them. An embargo is the first thing between a note of protest and a declaration of war,
and it costs both sides money.

### National conditions

Events and decisions can hang a named condition on a country for a while — "war economy",
"investment boom", "austerity programme". They act every economic week until they expire and
are listed on the **In effect** line in the National Ledger, beside GDP and the treasury.
Receiving the same condition again extends it rather than stacking a second one.

### The key rate and the central bank

The "Central bank" card sits first in state affairs, above taxes, because the rate is what
everything below it is borrowed at.

The rate is shown as an **annual rate**, not a multiplier: 4 % is neutral, 12 % is harsh,
0.8 % is as soft as it goes. Beside it are the two figures without which a rate means
nothing: inflation, and the bank's own target.

By default **the bank is independent** and moves the rate itself — against its own target,
all over the world, not only for you. The line under the card always says what it is doing
right now: raising, cutting or holding.

A government can **take the rate away from the bank** — the switch under the slider. Taking
it is free. What is paid for is holding the rate **below** what the bank wanted: money a
government has ordered to be cheap is money nobody believes will stay cheap, and prices move
before spending does. The card names the price in points of inflation a week **before** you
touch the slider, and a separate line, "The bank is not independent", appears in the
inflation breakdown.

It is a real choice, not a free button: cheap money is growth now against prices later.

### Emission: the printing press

When the government holds the rate, a second slider appears on the central bank card —
**printing money**, as a share of a year's output per year. While the bank is independent the
slider is dead: a bank does not print for a government that does not own it.

The money is real and reaches the treasury at once. It is paid for with two things:

- **the money itself** — unearned, chasing what was not produced;
- **belief** — and that is the larger part. Cutting the rate says "money will be cheap";
  starting the press says "there is no limit".

The second part does not vanish when the press stops: the country remembers that it printed,
and that memory decays by a few per cent a week. A government that paid for a war with the
press pays for it through the whole peace that follows.

In the inflation breakdown (`inflation` in the console, or the Ledger) printed money is a
**separate line** from the rate — they are different decisions with different prices, and
adding them into one figure would hide which of them ruined you.

Everything that governs emission lives in `common/inflation` → `central_bank`:
`emission_ceiling`, `emission_inflation`, `emission_credibility`, `emission_memory_decay`.
Set the ceiling to zero and there is no printing press in your game.

## The Ledger, charts and state policy

The **Ledger** button opens a full-screen statistical office. It is not a decorative
encyclopedia: the values come from the same simulation that moves the map, the budget and
the population. History is recorded once a game week and keeps the last fifteen years, so
neither the save file nor memory grows without bound.

- **Overview** — GDP, treasury, population, stability, standard of living and the weekly
  budget.
- **Economy** — GDP, revenue and spending over time, the construction base and the tax rate.
- **Market** — market access, shortages, price indices, demand satisfaction and net flows of
  goods.
- **Population** — growth, literacy, classes and the largest regions.
- **Military** — real combat power, manpower reserve, armies and the current war score.
- **Rankings** — the world's largest economies and populations.

Hover a line or a bar for the exact day, value and explanation. **Map selection** switches
the charts to the last country you picked on the map; its policy is read-only.

On the Economy tab the tax slider really does move revenue, growth and the standard of
living. On the Market tab you choose a trade stance:

- **Open markets** raises import and export access at almost no tariff;
- **Balanced trade** is the moderate default;
- **Strategic autarky** limits imports and exports and raises tariffs, so a foreign shock
  hurts trade less but a domestic shortage is harder to close.

Market policy takes effect at the next weekly clearing. That lets you pause, change course,
run a few weeks and see cause and effect on the charts.

## The army and its orders

Left-clicking a region opens the regional inspector. The `Overview`, `Population` and
`Province` tabs show buildings and the construction queue, social classes, nationalities,
who owns the province and who actually holds it, occupation, neighbours and the armies
standing there. In your own region, `Develop`, `Industry` and `Raise army` are available
directly.

A new division costs $0.18B, 15,000 men of reserve and a province you control; it arrives
with incomplete organisation. Every division is its own entity with its own men,
experience, equipment, cohesion, reconnaissance, engineers and artillery support. A "stack"
on the map is only a compact drawing of several divisions in one province, not a merged
super-division.

RMB on the map gives a movement order and no longer replaces the region you were reading.
`Shift` + RMB appends a waypoint to an existing route. The order is validated at once and
the panel names the exact reason for a refusal: somebody else's division, somebody else's
control of the target, no connected land path, or a battle still to finish. Only a textured
ribbon of the route stays on the map: gold for a march, hatched amber for a prepared
offensive and red for an attack being executed.

Select one army with a click or several with a box. The operations panel shows numbers,
organisation, supply, where the supply comes from, the distance to it, the flow available
and the replacements arriving.

- **Halt** — cancel the route or plan and hold the current province.
- **Defend front** — take a threatened border province and hold it.
- **Advance front** — push evenly along the available front.
- **Reserve** — sit behind the line; reserves recover organisation faster and join
  neighbouring battles as reinforcements.
- **Spearhead** — a concentrated breakthrough towards a chosen province or war goal.
- **Auto deploy** — distribute exactly the selected group across separate sectors of the
  front between defence, attack, reserve and the main blow. With nothing selected, the whole
  national army is used.
- **Prepare plan** — hold the assigned offensive formations at the front, keeping their
  sectors and objective, while they entrench and accumulate planning.
- **Execute plan** — launch the chosen Advance and Spearhead formations. Defence and reserve
  carry on with their roles.

Auto deploy creates a plan in preparation; it does not start an unprepared attack by itself.
A front order is a standing plan. When a capture moves the line of contact, formations no
longer stay behind guarding the old border: their objectives are recomputed against the new
front graph.

### The army panel

Selected divisions open a list panel on the left — laid out the way Hearts of Iron does it.

**A division row** is a branch counter (✕ infantry, ⊙ motorised, ◈ mechanised, ▬ armour,
● artillery), a name, an organisation bar, a strength bar and a status mark. The bars matter
more than the numbers: forty rows of numbers are a table, forty rows of bars are a situation.
The mark on the right says what is wrong with this particular division: ⊘ cut off from
supply, ⚔ in battle, △ short of supply, ↓ organisation broken, → on the march, ▶ plan
executing, ◷ plan preparing.

**Selection** works as you expect: click to command one division, Ctrl+click to add or
remove, double-click to take **every** division of the same type (the fastest way to gather
all the armour). Hovering a row shows what HOI4 shows: organisation out of maximum, combat
strength, men out of establishment, equipment, experience, supply, entrenchment and plan
readiness with the attack bonus it carries.

**Battle plans** are the row of marks at the bottom: ✋ hold still, ▬ hold the front,
➤ advance along the front, ➤➤ spearhead, ⌂ reserve. Beside them "Prepare" and "Execute ▶" —
a plan accumulates while a division stands on its sector and gives up to +30 % attack when it
is finally let loose. Everything else — combat posture, reinforcement priority, headquarters
— sits alongside in compact rows.

The panel's heading shows what the selection consists of: how many divisions of which type,
with a tooltip for their characteristics — soft and hard attack, defence, breakthrough,
armour, piercing and the front width they take up.

### Formation types, posture and planning

- **Leg infantry** advances more slowly but defends better and spends less supply.
- **Motorized corps** moves faster and exploits a breakthrough better, but wants more supply.
- **Armored corps** has the strongest attack and a high width, but is especially hard on
  logistics.

The symbol inside the counter shows the formation type. The counter's bottom line carries the
order, organisation, posture and accumulated planning.

- **Cautious** improves recovery, defence and entrenchment and cuts losses, at the cost of
  speed and offensive power.
- **Balanced** applies no special modifiers.
- **Aggressive** speeds movement and strengthens the attack, but worsens recovery and raises
  losses.

An army with an offensive order standing at the front gradually accumulates planning. It is
worth up to 30 % offensive power, is spent by moving and burns off quickly in battle. Which
is why an immediate continuous advance is faster, and a prepared operation is more effective.

The front line is built from genuinely adjacent provinces along the border between enemies
and is pressed onto the surface of the map. The base scenario's map holds **37,909** tactical
provinces — about the forty thousand aimed for. After repartitioning, the area, elongation
and asymmetry of neighbouring cells differ noticeably, so close in they do not form an even
"scale pattern"; at strategic zoom the layer is hidden. The starting deployment is not a
random scatter: belligerents concentrate most of their formations on the active front,
peaceful countries cover foreign land borders, and some forces stay near the capital or
inland.

Several divisions in one province get one compact counter: the number of independent
divisions above, their combined manpower below. A click selects the whole visual stack,
after which the side operations panel lets you drop an individual division from the
selection, create a headquarters army, attach the selected divisions to an existing army or
detach them. An army here is an organisation and the bearer of a plan; divisions go on
moving, fighting and losing men individually.

## Supply, replacements and encirclement

Supply runs from a country's interior along a chain of friendly-controlled provinces.
Infrastructure and barracks raise throughput, and too many divisions cause congestion.

If the path is cut, supply and organisation fall, isolation losses begin, and replacement of
men nearly stops. Replacements are not free: they come out of the national manpower reserve,
and the daily volume is bounded by mobilisation capacity, the budget and local supply.

## Rivers and lakes

Rivers and lakes are part of one geography, not a decorative layer. A river is crossed with
an ordinary land order: no separate embarkation or "swimming" is needed. But a crossing slows
the march and gives the defender a combat bonus; a major river is punished harder. The
operational AI accounts for this and prefers less defended dry sectors, while `Spearhead` is
willing to take more risk for its objective. The shore of a large lake slightly improves
local supply.

Hovering, and the province tab, report river crossings and lake frontage. The blue lines and
surfaces are pinned to the map identically in 3D and 2D.

## Battles, occupation and peace

Entering an enemy province starts a battle. The outcome is driven by numbers, organisation,
supply and the defender's advantage. Neighbouring armies with a suitable order can join as
reinforcements; their count is visible on the battle card.

After a victory the province changes hands and colour at once. Occupation then consolidates
gradually. Supply, garrison and local unrest drive the speed; unconsolidated captures can be
handed back at the peace.

The **Wars** tab holds declarations, war goals, the current score, losses and readiness for
peace. The score is made of control of the goal, occupations, battles, attrition and war
support.

- **Ceasefire** ends the fighting for a year and restores the pre-war map of control;
- **White peace** restores the pre-war map of control and creates a five-year truce;
- **Reparations** also restores the borders, but hands the winner an economically computed
  payment;
- **Trade concessions** adds a limited payment and a compulsory bilateral trade agreement;
- **Demilitarize** restores the borders, but the loser surrenders a significant part of its
  divisions' equipment and cohesion;
- **War goal** leaves only the consolidated provinces inside the declared war goal;
- **Hold occupied** keeps every sufficiently consolidated occupation and evacuates exposed
  salients;
- **Capitulate** immediately concedes defeat for your own side.

Each demand has its own readiness on the opponent's side and a minimum war duration. After a
peace there is a five-year truce, and the outcome stays in the treaty log.

## The peace conference

The ready-made terms (white peace, reparations, hold occupied) are still there — they are
convenient labels for the peaces that have a name. But a war over two provinces is not
described by such a button, and a war that took half a country has no way of keeping the half
that mattered and handing back the half that did not.

So a peace is now **drawn**. You drag with the left button across the map — the same gesture
you draw an offensive with — and the provinces under the cursor go into the demand; the right
button gives them back. The map repaints in the claimant's colours as you go: you are looking
at the border you are proposing, not at a list that describes it. Clicking a region in the
schedule takes or gives back the whole region at once, because painting twenty provinces one
at a time is not a feature.

**War score is a budget, and every part of a demand costs.** Land you occupied and held costs
the base price; occupied but not consolidated costs twice; land you never touched costs four
and a half times. A declared war goal comes at a discount — that is what declaring a goal is
for. A region with a capital in it costs 2.6 times more, and the price rises with population
density. The other terms — reparations, trade concessions, disarmament, humiliation, a
ten-year truce — cost too.

When the bill exceeds what the war was worth, the other side refuses — **and says why, and by
how much**: "they will not sign: the demand is worth 47 and the war is worth 8". That is a
negotiation, not a refusal.

Signing runs the provinces through the same annexation path as any other border change, so
regions cut by a new border split exactly the same way. A drawn peace is not a special case
of the map.

**Who holds what is visible on the map.** An occupied province keeps the **owner's** colour
and wears the **occupier's** diagonal hatching over it, the way Paradox games do it. Your
solid colour is what the treaty hands you; hatching is what you are merely standing on.
Previously, taken land was painted in the taker's colour, and at the conference you could not
tell an annexation from a hand-back.

The schedule's rows say the same thing in words: `Donetsk 12/18 · 9 held` — twelve provinces
out of eighteen in the demand, nine of which you actually occupy. The other three are the
expensive ones.

**The camera moves.** Both mouse buttons are taken by the brush, so the map is dragged with
the **middle button**, and WASD always works. A war can be on the other side of the world
from wherever the camera is looking.

Every price lives in `common/defines` (`peace_cost_*`) — that is, in data.

To open: the **Wars** screen → "Open the peace conference", or the console command `peace`.

## Diplomacy: reputation, alliances and coalitions

**A reason for war.** When a country declares war it either has a reason or it does not. The
reason is derived from the state of affairs rather than chosen from a list: they took our
land and we have not forgotten; our people live on the other side of that border; somebody is
hitting a friend we vouched for. A war with no reason costs stability, popular support and
reputation with everybody not in it.

**Reputation.** Land taken by force adds notoriety of the bad kind — measured in people, not
provinces: a hundred thousand square kilometres of tundra frightens no neighbours. Reputation
fades only with time and with nothing else. While it is high, the whole world's opinion of you
falls.

**Coalitions.** Past a threshold, the neighbours form a bloc against you. A coalition is the
only force on the map that grows in proportion to your successes, and it answers when you
attack somebody. That is the brake that used to be missing.

**Alliances that work.** Attacking a country with friends now brings them in — each with a
separate declaration of war. A country that refuses to come loses the alliance publicly, and
both sides remember it for a long time. A guarantee is the cheap half of an alliance: it
commits to nothing until the guaranteed country is attacked.

**Embargoes and guarantees** are available as separate actions. Sanctions against somebody
who depends on you more than you depend on them are a way of applying pressure without going
to war.

### What the AI expects of you

A computer government no longer simply wants your land. It counts what a war is worth to it
(people, its own countrymen across your border, a good it is short of) and what it will cost
(the trade with you it loses, your allies and guarantors, the coalition already assembled
against it, the reputation it will have to spend).

Three things follow that are useful at the table:

1. **Trade with whoever frightens you.** That is not a metaphor: the share of your trade in
   their economy is subtracted directly from their appetite for attacking.
2. **Make friends and accept guarantees.** Every ally and guarantor is a separate line in
   their costs.
3. **Do not celebrate when they "change their mind".** A country that cannot afford a war
   today keeps you as a target, arms itself and starts prising your friends away. If your
   allies have suddenly gone cool on you, you may be being prepared.

The AI now also **offers peace to you**. A war involving the player used to be able to end
only when the player ended it: a losing country fought to its last division without once
asking to stop. Now an offer arrives — listing what is being demanded of you, with the border
drawn on the map behind the panel. Refusing costs nothing but time; an unanswered offer
expires after 60 days.

## Government and the social budget

`Ledger → Government` sets the cabinet's direction: balanced development, growth, social
cohesion, education and science, or national security. The real shares of welfare and
education spending are here too. They enter the weekly budget and gradually move the standard
of living, radicalism, literacy, productivity, stability and the growth rate. For a foreign
country the tab stays analytical and cannot be edited.

Population is held in administrative regions. A country's figure in the top bar is the
recomputed sum of its current regions, so transferring territory, occupation and changes in
regional demography never disagree with the national total.

## Regions, provinces and nationalities

Every row in the region panel explains itself: hover it and you get what the number is and
what moves it. That is the intent — as much detail as you want, without a newcomer having to
read it in order to play.

**A region** shows people (population, literacy, standard of living, urbanisation,
radicalism), economy (buildings, surveyed deposits, construction) and land (how many
provinces it is made of, how many divisions stand there). A separate tab is the demographic
register: estates with their employment, wealth and standard of living, and **nationalities**.

**A province** shows who owns it by right and who holds it now, occupation progress, the
number of neighbours, river crossings and lake frontage, and every formation standing in it.

### Nationalities and minorities

At the start of a scenario every region is populated by its own people. The moment a border
moves, people do not change nationality along with the treaty. A region taken by force is
full of those who did not choose it: it is **more radical than its neighbours** and stays
that way until a generation has passed.

Assimilation is slow and depends on conditions: faster where people are educated and live
well, slower where they are angry. Which is why conquest costs for years — and yet one day
stops costing. That is the difference between a border that has moved and a border that is
still moving.

## How the numbers read

The top bar shows the treasury, GDP and population in the largest sensible unit: `$21.43T`,
`$184.8B`, `51.7M`. Hover and the tooltip gives the exact figure down to the last digit.

Under each value is a second line — the movement over the campaign's last week. Green up, red
down, a long dash means nothing noticeable happened this week. That is the fastest way to
notice that the budget has been draining for two months without opening the Ledger.

The active game speed and the open screen are lit in brass: you can see whether the game is
running at 3 or at 5, and which panel is in front of you.

## Interface styling

Every colour of the interface and the map is data, not code. A mod can repaint the whole game
by shipping an `interface/skin.json` with the tokens it wants; there is a worked example in
`mods/_templates/interface_reskin`. The console command `skin` shows which colours mods have
moved.

## Interface language

The **Language** button in the main menu cycles every loaded language, including ones added
by mods. The base game ships English and Russian. A translation is a `localization/` folder
with one JSON file per language, so a translation mod needs no code and no rebuild; details
are in the modding guide.

## Free camera

For a level globe rotation or flat-map pan during a showcase, press **F6** or **PAN**. One horizontal turn takes about 90 seconds; press again to stop. For a HUD-free fly camera, use **F10**, then **T** for its own automatic orbit.

**F10** takes the camera off its orbital mount and it starts to fly. The mouse looks, **WASD**
moves where you are looking, **Space** and **Ctrl** rise and fall, **Shift** speeds up 4.5×,
**Alt** slows to a quarter, the **wheel** sets the base speed. Nothing constrains it: you can
sit on a province, fly along an entire front, or pull back until the planet is a marble.

Speed scales with height above the surface — right down on the ground the camera crawls, in
space it crosses an ocean. The same keypress stays comfortable at both scales.

The interface is hidden while you fly: this is a tool for showing the game, not for playing
it. **F10** again or **Escape** lands the camera back — exactly where it flew to, not where
it took off from. The console command `freecam [on/off]` does the same.

## Developer console

`~` opens the console; `~` or `Esc` closes it. The main commands:

- `tag KOR`, `observe`, `pause`, `speed 1-5` — country and time control;
- `mapmode pol|dip|war|pop|gdp`, `focus LAT LON`, `teleport PROVINCE` — map, camera and
  debugging of the selected divisions;
- `money`, `manpower`, `stability` — test changes to state;
- `freecam on|off` — the free flying camera with no HUD (the same as F10);
- `armies`, `fronts`, `province`, `fps`, `sim`, `date` — simulation diagnostics and
  profiling;
- `economy` — output calibration and the current gap between GDP and the value produced;
- `effects` — the full list of keys that events, decisions and modifiers are written with;
- `loc [language]` — switch language and see how many strings are translated and which are
  not;
- `skin [part of a name]` — which interface colour tokens mods have moved, or one family with
  colour swatches;
- `layout` — every caption on screen that was not given room for its own text;
- `peace` — open the peace conference for the player's current war.

Money and cadence:

- `inflation [country]` — what prices are doing and why, line by line, with the rate beside
  it;
- `keyrate [PERCENT|seize|release]` — read the rate, take it from the bank, give it back, or
  set it;
- `step [HOURS]` — how much game time one simulation step covers, and change it on the fly;
- `advance [DAYS]` — advance whole days without running the clock;
- `cadence` — how many times each pass ran per step (the quick way to spot a system that
  thinks in days but runs on every step);
- `plural [key]` — what forms a counting phrase takes in the loaded language.

And a few unserious ones — each of which still counts something real about your own world:

- `blame` — who exactly dislikes you and what they say about it;
- `vibe` — how it is going, in one sentence, in the register the figures have earned;
- `doomsday` — when stability reaches zero along the line it is actually falling on;
- `ratio` — you against whoever you are fighting, figure by figure, with a verdict;
- `idle` — every division of yours that nobody has told anything;
- `flip`, `coffee`, `whoami`, `touchgrass`, `sudo`, `xyzzy`.

Type `help` to see the current short reference in the game itself.

## A first game as South Korea

1. Pick South Korea and leave it paused.
2. Box-select the divisions and use Auto deploy.
3. Leave some formations in Reserve and give the main body Defend front.
4. Select North Korea and read the Wars tab before declaring.
5. Once the war starts, give a strong group Spearhead onto a key province and the rest
   Advance front. Let the plan prepare, then press Execute plan.
6. Watch supply and the manpower reserve, not only the front.
7. Make peace once the key occupations have consolidated.
