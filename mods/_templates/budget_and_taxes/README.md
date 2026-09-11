# Budget and taxes template

What a government may raise and must spend is seventeen rows in `game/common/budget/00_budget.json`,
and nothing about it is compiled in. This template retunes one of those rows and adds three
the game has never had.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game. The new lines appear as sliders on
the state affairs screen under REVENUE and SPENDING, named and explained out of your file.

## The one expression

Every line, shipped or modded, is the same arithmetic:

    amount = basis · rate · yield · (constant + Σ weight · factor)

- **`basis`** — what it is charged against. `output` is one week of the country's output and
  covers almost everything; `debt` is what the treasury owes, and is how interest works;
  `tariffs` and `trade_balance` are figures the market already worked out.
- **`rate`** — the policy. A slider, if `"player": true`; a fixed 1 if not.
- **`yield`** — what one whole point of rate is worth, as a share of the basis.
- **`scale`** — what the country is like. `constant`, plus any of `stability`, `literacy`,
  `standard_of_living`, `urbanisation`, `employment`, `at_war`, `shortage`.

So `"yield": 0.24` with `"scale": { "constant": 0.72, "stability": 0.22 }` is a tax that
brings in about a quarter of output at a full rate, and rather less of it from a country
that is coming apart.

## What a line does besides move money

`rate.neutral` is where the line stops pushing the country about. Everything in `effects` is
measured **per whole point of (rate − neutral)**, so a line sitting at its neutral rate is
fiscally real and socially invisible.

| channel | what it moves | cadence |
| --- | --- | --- |
| `growth` | the annual growth the economy converges on | annual |
| `living` | the standard of living | weekly |
| `literacy` | literacy | weekly |
| `radicalism` | radicalism in every region | weekly |
| `stability` | stability | weekly |

`resented_above` is for a rate people tolerate up to a point and then do not: nothing below
`threshold`, and `living` / `radicalism` per point above it. A straight line through the
origin cannot say "nobody minded until it passed a third of their income", and that is a
thing a tax does.

## What is in this template

**`common/budget/taxes.json`**

- **`income_tax`** — the same id the base game uses, so this **replaces** that line and
  leaves the other sixteen alone. Raises half again as much and is resented from 26% rather
  than 32%. This is the shape of every balance mod: name what you disagree with, inherit the
  rest, and do not go stale when the base game retunes something else next patch.
- **`wealth_tax`** — an id nothing else uses, so it is a **new** line. It appears as its own
  slider, starts at zero, is worth most in rich urban countries and costs a great deal of
  growth.
- **`civil_service_reform`** — an expense with `"player": false`: no slider, and an amount
  that comes entirely from the country. It is nearly free where literacy is high and ruinous
  where it is not.
- **`war_bonds_service`** — `"scale": { "constant": 0.0, "at_war": 1.0 }`, which is a line
  that costs nothing in peace and appears the week war is declared.

## Things that will bite

- **The four with their own sliders.** `income_tax`, `armed_forces`, `welfare` and
  `education` keep hand-built controls higher up the screen; retuning them works, but they
  will not move to the REVENUE and SPENDING sections. Every other line, yours included, is
  built from the catalogue.
- **A rate is a share, not a percentage.** `0.20` is twenty per cent.
- **Nothing may raise money for being miserable.** Every line except `trade_balance` is
  floored at zero, so a scale that goes negative yields nothing rather than paying out.
- **Default to zero.** A new revenue line with a non-zero default changes every country's
  finances on day one, including the two hundred and sixty run by the AI. Start it at zero
  and let a government decide, unless changing the world's balance is the point of your mod.
- **Debt compounds.** The base game charges interest on a negative treasury. A mod that
  removes `debt_interest` gives every country in the world unlimited free borrowing, which
  they will use.
