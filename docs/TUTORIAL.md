# Your first campaign: two hours, hand held

*Русская версия: [TUTORIAL_RU.md](TUTORIAL_RU.md)*

This is not a reference. The reference is [PLAYER_GUIDE.md](PLAYER_GUIDE.md), and it is
worth going there once you understand what is happening and need to check a detail. This is
a walk through it: we sit down with one particular country and work out why each button does
what it does.

Read it in order. Every chapter ends with something you can see on screen — if you cannot
see it, you missed a step earlier, and that is fine, go back.

---

## Chapter 0. Who to play first

Take **Poland**.

Not because it is strong — because it is convenient for learning. It has everything you need
to touch and nothing that will kill you while you are reading tooltips:

- a mid-sized economy — there is money, but not endless money, and decisions have a price;
- no war at the start — you learn in quiet, not under fire;
- somebody else's war is running next door — there is something to watch and learn from at a
  safe distance;
- land borders with every neighbour — no need to work out fleets in your first hour.

What **not** to take first: the USA and China (too much at once), Russia and Ukraine (you are
in a war in the first minute), microstates (interesting, but there is almost nothing to run).

> **How to pick.** Main menu → **Singleplayer** → find Poland with the search or click it on
> the map → read its card → confirm.

The game starts **paused**. That is deliberate: you have as long as you like to look around,
and nothing happens until you press space.

---

## Chapter 1. Look around without pressing anything

The pause holds. Let us read the screen from the top down.

**The top bar** — four numbers that describe a country in one line: treasury, GDP,
population, date. The treasury is the money that exists *now*. GDP is how much the country
produces in a year. These are different things and confusing them is expensive: a country
with a large GDP and an empty treasury cannot buy anything today.

**The red alert strip** — what the game thinks needs attention. If it is empty that does not
mean "all is well", it means "nothing is on fire". The difference matters.

**The outliner** — lists of your things: armies, fleets, construction, wars. Clicking a row
shows the item on the map.

Now spin the globe: **hold RMB and drag**. The wheel zooms. `WASD` pans.

Zoom in on Poland until the internal regional borders are visible. That is the game's working
scale: a country is not a blob on a map, it is a dozen and a half regions, each with its own
population, its own industry and its own buildings.

> **Try it:** press **RMB on Germany**. Its dossier opens and Germany's borders light up on
> the map. That is your main way of "seeing what the neighbour has".

---

## Chapter 2. The first month: just watch

Press **space**. Time is running.

The speed is switched with **1–5**. Start at **2**. At speed five the world flies, and on a
first campaign you simply will not keep up with what happened.

What is happening underneath: the game computes the world **an hour at a time**. Not a day —
an hour. An army marching for four days honestly walks ninety-six steps rather than making
four jumps. That is why a front looks like a front and not like a chessboard.

Let **one game month** pass without touching anything. Watch what changes:

- the treasury creeps up or down — that is your budget at work;
- the population moves;
- somewhere on the map other people's wars are running;
- things appear and disappear in the alert strip.

This is boring exactly once. In exchange you now have a feel for "normal" — and without that
you cannot notice the abnormal.

---

## Chapter 3. Money: where it comes from and where it goes

Open the **economy**. The first thing to find is the **balance**: how much comes in and how
much goes out per period.

Three cases, and each wants something different:

| What you see | What it means | What to do |
|---|---|---|
| A positive balance, the treasury growing | You are under-investing | Build. Money in the treasury is not working. |
| A balance near zero | Steady | You can afford one project |
| A negative balance, the treasury draining | You are living on borrowed time | Either cut spending or raise revenue — neither is quick |

### The key rate: the lever everybody forgets

There is a **key rate** line in the budget. It is not just a number — it is the strongest
lever in the game, and it has a price.

How it works. Each of the 261 countries has a **central bank**, and by default it is
**independent**: it watches inflation itself and moves the rate by a rule — a neutral rate of
4 %, an inflation target of 2 %, and for every point of deviation from the target it reacts
with a strength of 1.6. It moves smoothly rather than in jerks.

You can **take the rate away from it**. The game will let you. And here is what happens:

1. You set the rate below neutral. Money gets cheaper, the economy accelerates.
2. Inflation starts to rise. Slowly at first.
3. Your **credibility** in monetary policy falls — that is a separate quantity.
4. When you want to bring inflation back down, the same rate will work **worse**, because
   there is less credibility.

That is not a punishment, it is a mechanic. Cheap money really does work — for a year,
sometimes two. Then the bill arrives.

> **Try it right now (paused, in a sandbox):** drop the rate two points below neutral,
> unpause and run a year at speed 4. Watch inflation. Then put the rate back and run another
> year. It comes back more slowly than it left. That is the price.

### The printing press: the button with no way back

Once you have taken the bank, a **second slider** appears on its card — emission. It was not
there while the bank was independent, and that is not an accident: only a government that has
already taken the bank can print. One decision unlocks the other.

What it does, literally: the bank prints money and puts it **in the treasury**. Immediately.
Real money. A country a week from bankruptcy on Monday is solvent.

What it costs is where it gets interesting. Not one thing but two:

1. **The money itself.** Unearned, chasing goods that were not produced. Direct inflation.
2. **Belief.** And that is dearer. Cutting the rate says "money will be cheap". Starting the
   press says "there is no limit any more", which is a far more expensive statement.

And the key point: **stopping the press is not the same as never starting it.** The game
remembers that you printed, and that memory fades slowly.

Here are measured numbers to check yourself against:

| What was done | What happened |
|---|---|
| Printed 8 % of GDP for a year | +89.5 into the treasury |
| — | prices from **1.9 %** to **24.8 %** |
| Stopped, a month passed | still **21.1 %** |
| Eighteen months passed | **2.7 %** — forgiven |

Read that table again. A year of printing paid for a war and cost **two years of prices**.
That is not a prohibition, it is a price, and it has been computed. Governments in that
situation really do print, and a model is obliged to show what happens next, or the mechanic
is pointless.

> **From the console:** `keyrate seize`, then `emission 8`. And `emission off` when it gets
> frightening. `inflation` gives the line-by-line breakdown — printed money is a separate
> line from the rate, because they are different decisions with different prices.

---

## Chapter 4. Construction: your first real decision

Construction is how money turns into an economy.

Take a concrete example. The **agricultural complex** in the base data costs this:

```
cost 0.45 · days 120 · gdp_gain 0.22 · capacity_gain 0.05
```

Read that literally: it costs 0.45, takes 120 days to build and afterwards adds 0.22 to the
region's GDP and 0.05 to its production capacity.

From which the arithmetic is simple: it **pays for itself in about two years** and yields
after that. It is a good investment if you have 120 days and are not expecting a war.

How to build:

1. Click your region.
2. The region card has a list of buildings and a build button.
3. Pick a building. The game shows the cost and the time **before** you press.
4. Confirm. The project appears in the outliner.

**The rule for a first campaign:** do not queue five projects at once. Queue one, wait, and
look at what changed in the numbers. Otherwise you will not know which one did it.

> **If construction capacity is idle**, the game says so in the alert strip. Idle
> construction is money not working, and it is the only "on fire" you can calmly put off.

---

## Chapter 5. Trade: the controls have names

Trade here is not a "more/less" slider. It is **seven named controls**, and each answers its
own question:

| Control | The question it answers |
|---|---|
| `import_priority` | What to buy first when there is not enough money for everything |
| `import_ceiling` | The most to bring in |
| `export_reserve` | How much to keep before selling |
| `price_ceiling` | The price above which not to buy |
| `price_floor` | The price below which not to sell |
| `preference` | Who to trade with more willingly |
| `forbid` | Who not to trade with at all |

The trade screen is built from two cards: **about one good** and **about one country**. That
is deliberate — nearly every trade question sounds either like "what is my position on
grain" or "what is my position with Germany", and mixing them into one table means answering
neither.

Under each control the game writes **a sentence in plain words**: exactly what will change if
you touch it. Read that sentence. It is more precise than any intuition about a slider.

> **The first thing to do about trade:** nothing. Watch for a month which goods are short
> (that is visible in the economy), and only then touch the controls. Trade policy set before
> you know your own shortages is guessing.

---

## Chapter 6. The army: orders, not micromanagement

A division here is an independent unit on the map. But running them one at a time is not the
way the game is meant to be played.

**Selection:**

- LMB — pick one;
- `Shift` + LMB — add to or remove from the group;
- drag LMB across the map — a box, selecting everyone inside.

**Movement:** RMB on your province is a march. `Shift` + RMB adds a waypoint.

**Orders** are the point. A division has a role:

| Order | What the division does |
|---|---|
| `Manual` | Only what you said. Nothing of its own. |
| `DefendFront` | Takes a post on the line and holds it |
| `AdvanceFront` | Moves forward with the line |
| `ReserveFront` | Stands in the second echelon |
| `Spearhead` | Goes for a specific objective |

The idea is that you **draw a line and hand out roles**, and the divisions spread themselves
across the posts and hold their own intervals. You do not place seventy-seven divisions by
hand — you say what should be happening and watch whether it is.

> **Why this is worth knowing:** if you set everybody to `Manual`, the front stops holding
> itself, and any hole stays a hole until you personally plug it. `Manual` is for one
> particular operation, not for an army.

---

## Chapter 7. War and peace: the full cycle

Even if you are not going to fight in your first campaign, watch how others do it. Find
somebody else's war in the outliner and open its dossier.

The cycle is always the same:

1. **The declaration.** A war has a goal — usually a particular region.
2. **The front.** The game draws the line of contact itself.
3. **The battles.** Divisions on the line fight, losing men and organisation.
4. **Occupation.** A province you take does not become yours — it becomes **occupied**.
5. **Peace.** And this is where it gets interesting.

### How a peace treaty is written

The winner does not get "what they are standing on". They get **whole regions**.

That is a rule, and it is stronger than it looks. A region is handed over whole or not at
all. If you have taken half of one, you either take all of it (and pay for all of it) or you
take none. Cutting a region along the line where two armies ran out of breath is not
possible.

Why it is built that way: **a border that cuts a region creates a second region, for ever**.
A region cut once never grows back by itself, and every war after that starts from the debris
the last one left. Ten years of AI wars turn a map into noodles. That is what this genre calls
bordergore, and that is exactly why there is none here: measured, ten years of AI wars
produce **zero** cut regions.

The practical conclusion for you: **count a war in regions, not in provinces**. The question
"can I take enough to claim the whole region" is the right one. The question "how many
provinces can I grab" is meaningless.

---

## Chapter 8. The console as a teaching aid

`~` opens the developer console. That is not cheating — it is a way to **see a mechanic** you
would otherwise have to infer from hours of watching.

The commands that genuinely teach:

```
economy          what the economy is doing right now
market           prices and shortages
inflation        inflation and what it is made of
keyrate          the key rate: whose it is and why it is what it is
fronts           how the divisions are laid out along the line
blame            why a number is what it is
defines          every rule constant the game computes with
```

`help` lists them. `commands` lists them with descriptions.

And yes, there are unserious ones. `touchgrass` reads you a verse about the grass outside and
closes the game eight seconds later, without saving - it takes its own advice. The verse is
`game/common/console/00_touchgrass.json`, one entry per language, and yours to rewrite.

> **The best way to learn:** pause, run `blame` on a number that surprised you, and read the
> breakdown. This game can explain its own numbers — which is not always true in the genre.

---

## If something has gone wrong

| Symptom | What it usually is |
|---|---|
| The treasury is draining and I built nothing | Military spending or debt service. Open the budget. |
| Divisions stand around doing nothing | They are on `Manual`, or they are not assigned to a front |
| Prices have jumped | Check shortages in the economy and the rate in the budget |
| The neighbours have gone cold | Look at reputation: annexations cost dearly in opinion |
| The game runs slowly | Shorten the simulation step in settings — an hour a step is honester but dearer |
| Everything is broken | `F9` — quick load. It is always there. |

---

## What next

Once the first campaign stops surprising you:

- [PLAYER_GUIDE.md](PLAYER_GUIDE.md) — the full reference for every screen;
- [MODDING_QUICKSTART.md](MODDING_QUICKSTART.md) — your first mod in fifteen minutes;
- [MODDING_GUIDE.md](MODDING_GUIDE.md) — the full modder's reference;
- settings → **simulation step length** — try three hours instead of one and feel the
  difference in speed against the difference in precision. The outcome does not change; the
  grain does.

And one parting word. This game does not punish experiments: there is a pause, there is a
quick save, and there is a console that explains. The worst that can happen is that you learn
how something works.
