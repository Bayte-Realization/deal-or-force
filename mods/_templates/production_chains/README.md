# Production chains template

The economy is twenty goods and seventeen buildings, and most of them are links rather than
items: bauxite is worthless without a smelter and cheap power, electronics need rare earths
that eight countries have, and a modern harvest is made of natural gas. This template moves
the numbers that decide who needs whom.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game.

## What each file does

**`common/production/chains.json`** — **what a good is made out of, and who eats it.** This
is the interesting one, and until recently it was not possible: the recipes were a hundred
and thirty lines of arithmetic inside `MarketCore.cs`. A recipe names the good it makes, a
sum of weighted terms that make it, a sum of weighted terms that consume it, and the goods
whose supply last week scales it:

```json
"aluminium_smelting": {
  "good":   "Aluminium",
  "output": { "Smelter": 1.90, "deposit:Bauxite*extraction_efficiency": 0.30 },
  "needs":  { "Energy": 0.70 },
  "demand": { "ElectronicsPlant": 0.55, "ArmsIndustry": 0.40 }
}
```

A term is a product of named factors written with `*`, so `population*standard_of_living`
means what it looks like. The full list of factors, and what each one is, is documented at
the top of `game/common/production/00_chains.json` — read that file; it is the reference,
and it is the whole of the base game's economy in one place.

Merging is by recipe name, so this template's file is four recipes long rather than forty.
Name a recipe and yours replaces it; leave one out and you inherit it, including next
patch's retune of it.

**`common/goods/chains.json`** — what a good is worth and how badly it is needed.
`base_price` is where the price sits when supply matches demand. `essential_weight` is how
much a shortage of it hurts: it is the weight in the market-access figure that drives GDP,
living standards and unrest. Raising the weight of rare earths makes the countries that do
not have them visibly poorer; lowering it makes the whole chain decorative.

Fields you leave out keep the value the base game gave them, so a file can be three lines.

**`common/buildings/works.json`** — what a building costs, how long it takes, and what it
does for output and construction capacity. The two most interesting here are not industry:
a **port** and a **rail network** decide how much of its shortfall a country can physically
bring in, whoever its friends are. Make them cheap and the world trades freely; make them
dear and geography starts deciding foreign policy.

**`common/defines/market.json`** — the market itself.

| Define | What it does |
|---|---|
| `world_market_access` | The ceiling on imports, before a country's own reach is applied. Lower it and every country lives closer to its own production. |
| `import_markup` | What imported goods cost over home-made ones. |
| `price_adjust_speed` | How fast prices chase their clearing level. 0 freezes them, 1 snaps. |
| `economy_tick_days` | How often the market clears. Seven is a week. |
| `market_treasury_trade_share` | How much of the trade balance reaches the treasury. |

## What this cannot do

Add a **new** good or a **new** building. Both are still enumerations in code, so a mod can
retune every one of the twenty goods and seventeen buildings — rename them, re-price them,
rewrite what they are made of and what a shortage of them does — but it cannot invent a
twenty-first. Terrain types can be added freely (`common/terrain`), and that is the model the
goods will follow.

In practice this bites less than it sounds. A good's name, price, icon, description, what
makes it and what wants it are all data, so repurposing one you do not care about into one
you do is a file, not a fork.

A factor a recipe asks for has to be a quantity the simulation tracks, and that list is
fixed - letting a mod invent one would mean letting it ask a question the simulation cannot
answer. What is open is how they are combined, which is where all the design is. Something
genuinely new - a good whose output depends on the weather - needs a C# mod; see
`mods/_templates/csharp_mod`.

## Checking that it worked

Open the Economy screen and read the market table: production, demand, imports, exports and
satisfaction per good. Then the Ledger, which shows the same thing across every country.

The headless bench prints the whole world's balance sheet, which is the fastest way to see
whether a change has made a good meaningless or made it a famine:

```
dotnet run --project Tools/SimBench/SimBench.csproj -- 730
```

Look also for the line beginning `production:`, which says how many recipes loaded and
whether every good still has somebody who makes it and somebody who wants it. A factor you
misspelled shows up there as a failed check naming the recipe and the word, rather than as a
famine several hours into a game.

Look for the line beginning `world balance:`. A good below 0.9 of world demand is scarce
everywhere; above about 3.0 it can never run short and nothing that happens to it matters.
