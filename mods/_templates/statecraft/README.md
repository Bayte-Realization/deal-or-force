# Statecraft template

Whether the world is a peaceful one is not a difficulty setting. It is a balance sheet the
AI keeps, and this template moves the numbers on both sides of it.

Copy the directory out of `_templates`, give the folder and the `id` in `manifest.json` a
unique name, set `"enabled": true`, and start the game.

## How a government decides on a war

Every eleven days a country looks at everyone it can reach and works out, for each of them,
what a war would be **worth** and what it would **cost**. The two are subtracted. Above
`ai_war_desire_threshold` it may declare; above zero but below it, the country keeps the
target as a plan, spends on its army, and starts courting the target's friends away.

**The value side** — raise these for a hungrier world:

| Define | What it is |
|---|---|
| `ai_conquest_appetite` | How much a large population on the other side of the border is worth taking. |
| `ai_irredenta_appetite` | How much a country wants the regions its own people live in. Answered from the demography, so it is true of exactly the regions where it is true. |
| `ai_resource_appetite` | How much a country wants a good it is going without and the target sells. |
| `ai_hostility_appetite` | Plain dislike. |
| `ai_rivalry_appetite` | Being declared rivals. |
| `ai_opportunism_appetite` | The target is already fighting somebody else. |
| `ai_war_minimum_advantage` | Below this strength ratio there is no appetite at all. |

**The price side** — raise these for a quieter one:

| Define | What it is |
|---|---|
| `ai_trade_loss_weight` | The trade lost on the morning of the declaration. For a country that runs half its economy through its neighbour, this is the whole argument. |
| `ai_ally_deterrence` | Per ally and guarantor who would arrive with the defender. Each is a separate war. |
| `ai_coalition_deterrence` | Per member of a coalition already formed against us. |
| `ai_notoriety_caution` | How much a country's own record restrains it. |
| `ai_no_casus_belli_caution` | The cost of fighting with nothing to say about why. |
| `ai_war_weariness_caution` | How much a tired population restrains its government. |
| `ai_instability_caution` | How much unrest at home does. |
| `ai_poverty_caution` | An empty treasury. |
| `ai_overseas_caution` | Subtracted when the war has to be carried across water. |

And two that shape the patience rather than the sum: `ai_war_plan_patience_days` is how long
a government will prepare before dropping the idea, and `ai_embargo_leverage` is how much
more dependent on us a rival must be before we sanction them instead of invading them.

## What conquest costs with everyone else

| Define | What it is |
|---|---|
| `notoriety_per_million_taken` | Earned per million people annexed by force. Scaled by people rather than provinces, because tundra is not what makes a neighbourhood nervous. |
| `notoriety_per_unjustified_war` | Earned by declaring a war nobody accepts a reason for. |
| `unjustified_war_stability_cost` | What that costs at home. |
| `unjustified_war_support_cost` | And with the people. |
| `broken_promise_stability_cost` | What a government pays for refusing to honour its own alliance. |

Notoriety decays with nothing but time. Past a threshold the neighbours organise into a
coalition, and a coalition answers when the country it formed against attacks anybody.

## Opinions

`common/opinion_modifiers/` is where the reasons live. An opinion is the sum of named
reasons rather than a number anybody sets, so every one of them can be retuned, renamed, or
given a different memory. `days: 0` means "held while it is true"; `decays: true` means it
fades over its lifetime; `max_stacks` is how many times the same thing counts.

The ones this template touches are the newest: refusing to trade, guaranteeing somebody's
independence, standing by a friend, and abandoning one.

## Checking that it worked

The headless bench runs two years of a world nobody is playing and prints what came of it:

```
dotnet run --project Tools/SimBench/SimBench.csproj -- 730
```

Look for the line beginning `the world after 730 day(s)`. It counts wars declared and
settled, provinces annexed, and how many trade agreements, pacts, alliances and rivalries
the world signed. The base game's numbers are roughly 77 wars, 89 trade agreements, 71
pacts and 66 alliances; a mod that halves the price side should visibly raise the first and
lower the rest.

The check named `a country counts the cost before it declares` will fail if the price side
is turned down so far that trade and allies stop deterring anybody, which is a useful thing
to be told.
