# Production chains template

The economy is twenty goods and seventeen buildings, and most of them are links rather than
items: bauxite is worthless without a smelter and cheap power, electronics need rare earths
that eight countries have, and a modern harvest is made of natural gas. This template moves
the numbers that decide who needs whom.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game.

## What each file does

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

Add a **new** good or a **new** building. Both are enumerations in code, so a mod can retune
every one of the twenty goods and seventeen buildings, rename them, re-price them and change
what a shortage of them does — but it cannot invent a twenty-first. Terrain types can be
added freely (`common/terrain`), and that is the model the goods will eventually follow.

It also cannot rewrite the recipes: which building consumes what and produces what is in
`Scripts/Simulation/MarketCore.cs`, in one readable method. Changing that needs a C# mod -
see `Mods/_templates/csharp_mod`.

## Checking that it worked

Open the Economy screen and read the market table: production, demand, imports, exports and
satisfaction per good. Then the Ledger, which shows the same thing across every country.

The headless bench prints the whole world's balance sheet, which is the fastest way to see
whether a change has made a good meaningless or made it a famine:

```
dotnet run --project Tools/SimBench/SimBench.csproj -- 730
```

Look for the line beginning `world balance:`. A good below 0.9 of world demand is scarce
everywhere; above about 3.0 it can never run short and nothing that happens to it matters.
