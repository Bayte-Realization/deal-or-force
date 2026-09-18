# Central bank template

Money in this game is a model with about twenty numbers in it, and all twenty are rows in
`game/common/inflation`. Nothing about it is compiled in, including what a central bank is
for. This template gives the world a harder decade: prices that resist, a bank that moves in
big steps, and a real bill for a government that tries to hold the rate down.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game. The central bank card at the top of
the state affairs screen reads out of your file from the first morning.

## Where the rate actually lives

The key rate is not stored twice. It is the `debt_interest` line in `common/budget` — an
ordinary player-set rate between a fifth of neutral and three times it — and everything here
decides what that multiplier **means** and who moves it.

    key rate = central_bank.neutral_rate × (the line's rate ÷ the line's neutral)

So `"neutral_rate": 0.07` with the shipped budget line gives a scale running from 1.4 % to
21 %. Widen the scale by editing the line's `min` and `max` in `common/budget`; move the
whole scale by editing `neutral_rate` here.

That also means the rate is charged the way the budget charges things: on what the treasury
owes, and on nothing else. A solvent country pays nothing for a high rate. Everybody pays for
one in growth, through the line's own `effects.growth`.

## The bank

| row | what it is |
| --- | --- |
| `neutral_rate` | what the line's neutral point means, per annum |
| `target` | what the bank is holding prices at |
| `response` | points of rate per point of overshoot — above one, or the bank is loosening while appearing to tighten |
| `smoothing` | the share of the distance it covers each economic week |
| `war_tolerance` | how much overshoot it forgives while the country is at war |
| `credibility` | what a government pays, per point it holds the rate **below** what the bank wanted |

The rule the bank follows is one line:

    wanted = neutral_rate + response × (inflation − target − (war ? war_tolerance : 0))

clamped to what the budget line allows, and approached by `smoothing` of the distance a week.
It runs for **every country in the world**, not only the player's — so a mod that changes
these numbers changes two hundred and sixty-one monetary policies, and the AI will be living
under them too.

## The decision this exists for

A government can take the rate off its own bank. Taking it is free; what costs is holding the
rate under what the bank wanted, because money a government has ordered to be cheap is money
nobody believes will stay cheap. That is `credibility`, measured per point below, added to
the inflation target every week it lasts, and named in the breakdown as its own line so the
player can see exactly what they are paying.

Set `credibility` to zero and the lever becomes free, which makes cheap money strictly
correct and the decision disappears. Set it high and taking the rate becomes a wartime
measure. Both are legitimate worlds; the shipped 0.35 is the one where it is a real choice.

## Things that will bite

- **Rates are annual and are shares.** `0.04` is four per cent a year, not four.
- **`base` and `central_bank.target` should be near each other.** A bank aiming at two per
  cent in a world that rests at six spends the entire game tightening and the player never
  sees it do anything else.
- **`response` below 1.0 is a broken bank** and will read as one: prices rise, the bank
  raises the rate by less, and real rates fall. Do it on purpose or not at all.
- **Everything is measured from `comfort`, not from zero.** A country sitting at its target
  is not being punished; raising `base` without raising `comfort` makes the whole world
  slightly unhappy, which may or may not be what you meant.
- **Switching money off.** Set every cause to zero and inflation sits at `base` for ever.
  Set `central_bank.smoothing` to zero and the bank never moves, which leaves the rate
  wherever the player put it — a world with a budget and no monetary policy at all.

## Печатный станок

Тот же файл описывает и эмиссию — второй денежный рычаг, который открывается первым:
печатать может только правительство, уже забравшее банк себе.

| Ключ | Что делает | В базовой игре | Здесь |
|---|---|---|---|
| `emission_ceiling` | сколько годового выпуска можно напечатать за год | 0.15 | 0.25 |
| `emission_inflation` | очков инфляции за единицу напечатанного | 0.90 | 1.10 |
| `emission_credibility` | и сколько стоит утраченная вера — это и есть механика | 1.60 | 2.40 |
| `emission_memory_decay` | как быстро это забывают после остановки | 0.12 | 0.05 |

Поставьте `emission_ceiling` в `0`, и станка в вашем моде не будет вовсе — ползунок
останется, но двигать его будет некуда. Это законный дизайн: мир, где печатать нельзя,
играется иначе.
