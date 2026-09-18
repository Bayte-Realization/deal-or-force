# C# through this game's code

*Русская версия: [CSHARP_GUIDE_RU.md](CSHARP_GUIDE_RU.md)*

This is not a language reference. There are hundreds of those, and they all teach C# in
general — console calculators and an `Animal` class. This teaches the exact C# you need to
**read and change this game**, and every example is a real line out of `Scripts/`, not
something invented for a tutorial.

Read it in order. Every section ends somewhere you can open a file, find the thing
described, and see it with your own eyes.

**What you need to know first:** nothing at all. If you can open a folder and a text
editor, that is enough.

**Where you end up:** adding your own field to the country model, your own command, the
row that shows it on screen, and a check that will fail if somebody later breaks your work.

**If you do not need C# at all** — an enormous part of this game is configured in JSON
without a single line of code. That is [MODDING_QUICKSTART_RU.md](MODDING_QUICKSTART_RU.md)
(Russian), fifteen minutes, and it is the better place to start.

---

## Contents

1. [How the game is built, and why that comes first](#1-how-the-game-is-built)
2. [Tools in five minutes](#2-tools-in-five-minutes)
3. [The alphabet: variables, types, strings](#3-the-alphabet)
4. [Class and object: reading `Nation`](#4-class-and-object)
5. [Methods and properties](#5-methods-and-properties)
6. [`null` — the mistake everybody makes](#6-null)
7. [Collections: list, dictionary, set](#7-collections)
8. [LINQ — and when not to](#8-linq)
9. [`enum`, `switch` and pattern matching](#9-enum-and-switch)
10. [Interfaces: why this game uses commands](#10-interfaces-and-commands)
11. [`struct` and `record`: small values](#11-struct-and-record)
12. [Data catalogues — this game's main pattern](#12-data-catalogues)
13. [Determinism: why `new Random()` is a bug here](#13-determinism)
14. [The Godot half: `_Ready`, `_Process`, signals, lambdas](#14-the-godot-half)
15. [Localisation: the key is the English string](#15-localisation)
16. [Exercise 1: your own field, from model to screen](#16-exercise-1)
17. [Exercise 2: your own command](#17-exercise-2)
18. [Exercise 3: your own bench check](#18-exercise-3)
19. [How to read somebody else's code in this project](#19-how-to-read-the-code)
20. [Mistakes you will make, and what they look like](#20-mistakes)
21. [Where to go next](#21-where-next)

---

<a name="1-how-the-game-is-built"></a>

## 1. How the game is built

One picture before the first line of C#. Without it, half the decisions in this code look
arbitrary.

The game is **two halves**, and the border between them is not a convention — it is a rule
the compiler enforces for you.

```
Scripts/Simulation/        ← the simulation. Knows nothing about Godot.
    SimulationCore.cs          country, campaign, calendar, randomness
    EconomyCore.cs, TradeCore.cs   the economy
    MilitaryCore.cs            armies and fronts
    PeaceConference.cs         peace treaties
    CentralBank.cs             the key rate and the printing press
    ...

Scripts/ui/, Scripts/*.cs  ← the interface. Knows Godot, knows no rules.
    DealOrForceGame.cs         the campaign root
    StateAffairsUI.cs          the state affairs screen
    TradeInstructionsUI.cs     trade instructions
    ...
```

**One rule: there is not a single `using Godot;` anywhere in `Scripts/Simulation/`.**

Why so strict:

- **The simulation runs without the game.** `Tools/SimBench` is a separate program that
  takes those same simulation files, lives through decades in minutes, and checks a
  hundred claims about the world. No window, no GPU. That is only possible because the
  simulation does not depend on the engine.
- **A button cannot quietly break the rules.** If the economy can only change through
  commands (section 10), then no button ever moves a tax rate past everything that was
  meant to check it.
- **Multiplayer and saves.** Two machines that ran the same commands must end up with the
  same world. Section 13.

Keep that picture. **"Where does this live?" is almost always answered by "is this a rule
of the game, or is this a screen?"**

---

<a name="2-tools-in-five-minutes"></a>

## 2. Tools in five minutes

You need two things: Godot 4.7 with .NET support (the "mono" build) and the .NET 8 SDK.

### Build the game

```bash
dotnet build DealOrForce.csproj -c Debug
```

`-c Debug` is the **configuration**. There are two and confusing them is expensive:

| | `Debug` | `Release` |
|---|---|---|
| who uses it | the Godot editor when you press play | `Tools/SimBench` and the shipped build |
| internal checks | on | off |
| speed | slower | faster |
| output folder | its own | its own |

**Build the wrong one and you get the wrong result.** The editor runs `Debug`; if you only
rebuilt `Release`, the editor keeps running yesterday's code and you will spend an hour
hunting a bug that is not there.

### Run the checks

```bash
dotnet run --project Tools/SimBench/SimBench.csproj -c Release
```

This takes tens of minutes — it honestly lives through years of game time. At the end it
says how many checks passed and how many did not. **This is your safety net:** if you
changed a formula and the world fell over, you find out here rather than from a player.

### Look at what you did

Run the game from the editor. `~` opens the developer console; `help` lists every command.

---

<a name="3-the-alphabet"></a>

## 3. The alphabet

### A variable is a named box with a value in it

```csharp
int day = 0;
float stability = 0.65f;
string tag = "KOR";
bool atWar = false;
```

Type on the left, then the name, then the value. The type is mandatory and checked at
build time: `int day = "Monday";` does not compile. That is not the compiler being fussy,
that is the compiler catching your mistake today instead of in front of a player.

### The number types you will actually meet

| Type | What it is | Where |
|---|---|---|
| `int` | whole, roughly ±2 billion | `Id`, day number, division counts |
| `long` | whole, very large | world population |
| `float` | fractional, 7 significant digits | shares, rates, stability |
| `double` | fractional, 15 digits | Godot's frame `delta` |

**The `f` suffix is not optional.** `0.65` is a `double`; `0.65f` is a `float`. Writing
`float x = 0.65;` will not compile, and that is correct: the language refuses to lose
precision quietly behind your back.

### `var` — "work the type out yourself"

```csharp
var korea = campaign.NationByTag("KOR");
```

The variable still has a type and it is still checked; you just did not type it out when
the right-hand side already says it. This project uses `var` nearly everywhere the type
reads off the line.

### Strings and interpolation

```csharp
var line = $"{nation.DisplayName} · {nation.Stability:P0}";
```

The `$` before the quote turns on **interpolation**: everything in braces is evaluated and
substituted. After a colon comes the format:

- `:P0` — percent, no decimals: `0.744` → `74%`
- `:N0` — thousands separators: `51700000` → `51,700,000`
- `:0.##` — up to two decimals, no trailing zeros: `2.25` → `2.25`

**Important:** this is how you join numbers. It is *not* how you join translatable text.
Section 15 says why.

---

<a name="4-class-and-object"></a>

## 4. Class and object

Open `Scripts/Simulation/SimulationCore.cs` at line 145:

```csharp
public sealed partial class Nation
{
    public int Id;
    public string Tag = "";
    public string Name = "";
    public string Continent = "";
    public float StabilityBias;
    ...
}
```

Word by word, left to right.

**`class`** — a type description. Not a country: the blueprint of a country, the list of
fields one can have. South Korea specifically is an **object** (an "instance"), and a
campaign holds two hundred-odd of them.

**`public`** — visible everywhere. There is also `private` (this class only) and
`internal` (this assembly only). Write nothing and you get `private`, the most closed.

**`sealed`** — nothing may inherit from this. Almost everything here is sealed:
inheritance adds the question "which kind of country is this really?", and a data model
that has to be written to a file and compared between two machines suffers for it.

**`partial`** — and this one is interesting. `Nation` is not described in one file:

```
Scripts/Simulation/SimulationCore.cs:145   public sealed partial class Nation
Scripts/Simulation/TradeCore.cs:32         public sealed partial class Nation
Scripts/Simulation/Statecraft.cs:29        public sealed partial class Nation
Scripts/Simulation/Attrition.cs:6          public sealed partial class Nation
Scripts/Simulation/AiJudgement.cs:7        public sealed partial class Nation
```

That is **one class**, laid out by subject: what a country knows about trade sits next to
trade rather than in one heap. The compiler glues them back into a single type. The
country does not split in two — there is still one object.

**A field with a default.** `public string Tag = "";` — if nobody sets a tag it is an
empty string, not `null`. The difference is enormous: an empty string has a length, `null`
does not (section 6). It is a small habit that removes a whole family of crashes.

### Making one

```csharp
var nation = new Nation { Tag = "KOR", Name = "South Korea" };
```

`new` creates, the braces fill fields in. Countries are not usually made by hand in the
game — a scenario builds them from JSON — but you will see the syntax constantly.

---

<a name="5-methods-and-properties"></a>

## 5. Methods and properties

### A method is an action with a name

```csharp
public static string Of(string tag)
{
    if (string.IsNullOrEmpty(tag)) return "";
    ...
    return Loc.Has($"country.{tag}.name") ? Loc.Get($"country.{tag}.name") : tag;
}
```

(`Scripts/Simulation/CountryNames.cs`)

`public` — visible to all. `static` — belongs to the **type**, not to an object: it is
called as `CountryNames.Of("KOR")` and no "names object" has to exist. `string` — what it
returns. `Of` — the name. `(string tag)` — the parameter.

`return` ends the method there and then. There are two `return`s here and that is good
practice: leaving early on bad input saves you from nesting the whole rest of the method
inside an `if`.

**The ternary operator** `condition ? this : otherwise` is a short `if` for when both
branches are a single expression.

### A property is a field with a calculation behind it

```csharp
public string DisplayName => CountryNames.Resolve(this, false);
```

From outside it reads like a field — `nation.DisplayName` — but nothing is stored; it is
computed when asked. The `=>` arrow means "this property is that expression".

Why `Name` is a field and `DisplayName` is a property, and why that is not pedantry:
`Name` is what the data calls the country, and it goes into save files, logs and checks,
where a value that changed with the interface language would be a value nothing could
compare. `DisplayName` is the same name put through the player's translation table. One is
stored, the other is derived. **If you ever see a Russian country name inside a save file,
this separation is what broke.**

### `this`

`this` inside a method is "the object the method was called on". In `Resolve(this, false)`
the country hands itself over.

---

<a name="6-null"></a>

## 6. `null`

`null` means "there is no reference here". Not an empty string, not zero — the absence of
an object. Touching a member of `null` crashes with `NullReferenceException`, and it is
**the most common bug** in code you will be fixing.

It usually arrives from a lookup that found nothing:

```csharp
var nation = campaign.NationByTag("XXX");   // no such country → null
var stability = nation.Stability;            // ← crash here
```

### Four tools that cure it

**1. Check and leave.** Nearly everything in this project is written this way:

```csharp
public void Apply(Campaign campaign)
{
    var nation = campaign?.NationById(IssuerId);
    if (nation is null) return;
    CentralBank.SetEmission(nation, ShareOfOutput);
}
```

(`Scripts/Simulation/CentralBank.cs`, `SetEmissionCommand`)

`is null` reads exactly as it sounds. `is not null` also exists.

**2. `?.` — "and if it is null, never mind".**

```csharp
var nation = campaign?.NationById(IssuerId);
```

If `campaign` is `null` the call never happens and the whole expression becomes `null`.
The chain stops quietly instead of crashing.

**3. `??` — a fallback.**

```csharp
public int Day => Campaign?.ElapsedDays ?? 0;
```

"The campaign day, or zero if there is no campaign."

**4. `= null!` — "I know something will be here".**

```csharp
private Label _pickerSubheading = null!;
```

The `!` is a promise to the compiler: "stop warning, I assign this in `_Ready` before
anything reads it". Nothing enforces the promise, which is why it is only used for
interface fields filled in while a screen is being built.

---

<a name="7-collections"></a>

## 7. Collections

### `List<T>` — a list

```csharp
List<Army> armies = campaign.Armies;
armies.Add(newArmy);
var first = armies[0];
var count = armies.Count;
```

`<T>` is the element type. `List<Army>` is a list of armies. Order is kept; items can be
added and removed.

### `Dictionary<K, V>` — lookup by key

```csharp
public readonly Dictionary<string, float> TradeSettings = new(StringComparer.Ordinal);
```

Key → value. Its one great property: finding by key is instant no matter how many entries
there are. Which is why trade instructions are stored like this:
`good/Food/import_priority` → `2.25`.

Read from it **carefully**:

```csharp
// throws if the key is missing:
var value = nation.TradeSettings[key];

// correct:
if (nation.TradeSettings.TryGetValue(key, out var value))
    ...
```

`out var value` means "declare a variable here and put the result in it". The method
returns `true`/`false` — found or not.

### `HashSet<T>` — a set

```csharp
public readonly HashSet<string> Delegated = new(StringComparer.Ordinal);
```

Membership only: an item is in it or not, never twice. `Add` returns `true` if the item
was actually new and `false` if it was already there — and that single fact is the whole
"do not report the same problem twice" mechanism in `Scripts/ui/LayoutAudit.cs`.

### `StringComparer.Ordinal` — why it is on every other collection

It says: compare strings **by character code, bluntly, identically everywhere**. Without
it .NET compares by the rules of the operating system's current language — and a
dictionary built on a Turkish Windows can behave differently from one built on a Russian
Windows, because Turkish upper-cases `i` differently. For a world that must match on two
machines that is unacceptable. So: **any string-keyed collection in the simulation is
`Ordinal`.**

### The trap: never change a collection you are walking

```csharp
foreach (var army in campaign.Armies)
    if (army.Strength <= 0) campaign.Armies.Remove(army);   // ← exception
```

Collect first, remove after:

```csharp
var dead = campaign.Armies.Where(army => army.Strength <= 0).ToList();
foreach (var army in dead) campaign.Armies.Remove(army);
```

`ToList()` is required: without it `dead` is not a list but "instructions for producing
one", and those instructions run *during* the removal — exactly the wrong moment.

---

<a name="8-linq"></a>

## 8. LINQ

LINQ is a set of methods for querying collections. It reads almost like a sentence.

```csharp
var worstGood = player.Market.Values
    .OrderBy(quote => quote.Satisfaction)
    .FirstOrDefault();
```

(`Scripts/ui/StrategicHudUI.cs` — the good that ends up in the situation report)

The `=>` here is a **lambda**, a function with no name: "give me a `quote`, I hand back
`quote.Satisfaction`". `OrderBy` sorts by it, `FirstOrDefault` takes the first — or `null`
if the list is empty. (`...OrDefault` in all of these means exactly that: "and if there is
nothing, hand back nothing, do not crash".)

Worth knowing on sight:

| Method | What it does |
|---|---|
| `Where(x => ...)` | keep the matching ones |
| `Select(x => ...)` | turn each into something else |
| `OrderBy` / `OrderByDescending` | sort |
| `FirstOrDefault` | the first, or `null` |
| `Any(x => ...)` | "is there at least one" |
| `All(x => ...)` | "are they all" |
| `Sum`, `Count`, `Max`, `Min` | as they say |
| `ToList()`, `ToArray()` | freeze the result |

### When not to use it

LINQ allocates objects. One query is nothing. But simulation code runs **for every country
every game hour**, and a query inside a query there becomes millions of temporary objects
a second, the garbage collector starts waking up, and the game stutters.

The rule in this project: **interface code and rare events use LINQ, because it reads
honestly. The hot simulation loop uses a plain `for`/`foreach` and a reused buffer.** When
in doubt, look at what the code next to yours does.

---

<a name="9-enum-and-switch"></a>

## 9. `enum` and `switch`

An `enum` is a type with a finite list of named values:

```csharp
public enum Fault
{
    Squeezed,
    Clipped
}
```

(`Scripts/ui/LayoutAudit.cs`)

Why, when a string or a number would do: the compiler will not let you write
`Fault.Squished` by mistake, and the editor offers the list. The error is caught at build
time instead of by your eyes.

### `switch` as an expression

```csharp
public static string CategorySource(string category) => category.ToLowerInvariant() switch
{
    "game"      => "Game",
    "interface" => "Interface",
    "video"     => "Display",
    "audio"     => "Sound",
    _           => category
};
```

(`Scripts/ui/SettingsRows.cs`)

Read top to bottom: matched, returned. `_` is "everything else". It is an **expression** —
it produces a value — which is why the whole method fits behind one arrow.

### Pattern matching

C# can test a type and a shape right inside a condition, and this codebase does it
constantly:

```csharp
if (node is Control { Visible: false }) return;
```

"If `node` is a `Control` and its `Visible` is `false`."

```csharp
if (node is Label label && !string.IsNullOrWhiteSpace(label.Text))
```

"If it is a `Label` — and call it `label` while you are at it — and its text is not
empty."

```csharp
return label.HorizontalAlignment is HorizontalAlignment.Right or HorizontalAlignment.Center;
```

`or` inside a pattern: "either this or that".

---

<a name="10-interfaces-and-commands"></a>

## 10. Interfaces and commands

An **interface** is a list of what a type must be able to do, with not one line about how.

```csharp
public interface ICampaignCommand
{
    int IssuerId { get; }
    void Apply(Campaign campaign);
}
```

(`Scripts/Simulation/SimulationCore.cs`)

Any type that declares `: ICampaignCommand` must have `IssuerId` and `Apply`. In return it
can be put in the command queue by code that knows nothing else about it.

### A real command, whole

```csharp
public sealed class SetEmissionCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>Share of a year's output, per year. 0.05 is five per cent of GDP a year.</summary>
    public float ShareOfOutput { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;
        CentralBank.SetEmission(nation, ShareOfOutput);
    }
}
```

(`Scripts/Simulation/CentralBank.cs`)

`{ get; init; }` — a property that can be set **only at creation** and never afterwards. A
command is a note of intent; a note that can be rewritten on the way is worthless.

### Why commands at all

The button does not touch the world. It writes a note:

```
Button  →  new SetEmissionCommand { IssuerId = ..., ShareOfOutput = ... }
        →  campaign.Commands.Enqueue(command)
        →  campaign.Commands.ApplyAll(campaign)
        →  Apply(campaign) changes the world
        →  the interface redraws
```

What that buys, point by point:

- **Multiplayer.** The note travels, not the changed world. Two clients applying the same
  notes in the same order get the same world.
- **Authority.** `IssuerId` is who asked. A player cannot sign a treaty for somebody
  else's country, because the note says who wrote it.
- **Testability.** A `SimBench` check builds a command and applies it with no interface at
  all. If the button changed a field directly, testing it without pressing the button
  would be impossible.
- **"No" is an answer too.** Look at `SetKeyRateCommand` next door: it has `Accepted` and
  `Applied`. A command can say "no, the central bank is independent" — and the interface
  learns that honestly instead of showing the player a value the world does not hold.

**The rule:** if your change to the world has to show up in multiplayer, in a save file or
in a check, it is a command. If it is "open a panel", it is not a command, it is
interface.

---

<a name="11-struct-and-record"></a>

## 11. `struct` and `record`

A `class` is a reference: the variable points at an object and two variables can point at
the same one. A `struct` is a value: it is copied whole on assignment.

```csharp
public struct GameDate
{
    public int Year;
    public int Month;
    public int Day;
    public int Hour;
    ...
}
```

(`Scripts/Simulation/SimulationCore.cs`)

A date is a value, like a number. Nobody needs to "refer to that same date"; they need to
copy it and do arithmetic. Hence `struct`.

### `record struct` — a value that knows how to compare itself

```csharp
public readonly record struct Finding(string Path, string Text, float Width, float Height,
    Fault Fault, float Wanted);
```

(`Scripts/ui/LayoutAudit.cs`)

One line, and you get six properties, a constructor, comparison by content and readable
printing. `readonly` — it never changes after it is made.

This is the perfect shape for a "result": an audit finding, a report row, a pair of
values. When you need to return three numbers and a caption from a method, do not write a
class — write a one-line `record struct`.

---

<a name="12-data-catalogues"></a>

## 12. Data catalogues

This is the **main pattern of the game**. It explains why almost everything can be changed
with a JSON file, and if this is the only section you absorb you can already add content.

Every catalogue is built the same way:

```csharp
public static class CommodityCatalog
{
    static CommodityCatalog() => ResetCatalog();

    public static void ResetCatalog(List<string> errors = null)
    {
        // 1. put everything back to the built-in values
        // 2. read game/common/<folder>/*.json in filename order
        // 3. call Merge for each file
    }

    private static void Merge(JsonElement root)
    {
        // lay what was read over what is already there
    }
}
```

Three things to take from it:

**`static class`** — this type has no objects at all. There is one catalogue of commodities
in the world; it is not created, it is asked: `CommodityCatalog.Get(good)`.

**`static CommodityCatalog()`** — a static constructor. It runs **once**, by itself, before
the type is first touched. Nobody calls it; .NET guarantees it finished before anyone
managed to ask the catalogue anything. Hence the rule: a catalogue is always ready when
used, and "the data has not loaded yet" never happens.

**`Merge`, not "read"** — files are laid over each other **in filename order**, and a later
one overrides matching keys from an earlier one. From which:

- files are named `00_base.json`, `10_...`, `90_...` — the number *is* the queue;
- a mod does not replace the base file, it **adds to** it: to change one number you ship a
  file with one number in it;
- `ResetCatalog` starts by resetting, so reloading mods is just calling it again rather
  than restarting the game.

The data arrives from `GameDataLoader.DataDocuments("common", "<folder>")`, which collects
the file list from the game and from every enabled mod in the right order.

**`List<string> errors`** — where a catalogue puts complaints about bad data instead of
crashing. A mod with a typo should produce a readable message, not a black screen.

---

<a name="13-determinism"></a>

## 13. Determinism

The requirement: **two runs of the same commands from the same seed must produce the same
world.** Saves, replays and multiplayer all rest on it. Breaking it is easy and silent.

### Randomness

```csharp
public float NextFloat();
public int Range(int min, int max);
public bool Chance(float chance);
```

(`SimRandom` in `Scripts/Simulation/SimulationCore.cs`; reached as `campaign.Random`)

**Never write `new Random()` inside the rules.** It seeds itself from the system clock, so
two players get different worlds. `campaign.Random` keeps its state inside the campaign, it
goes into the save (`RandomState` in `SaveSystem.cs`) and it reproduces exactly.

```csharp
if (campaign.Random.Chance(0.05f)) { ... }          // right
if (new Random().NextDouble() < 0.05) { ... }        // you just broke multiplayer
```

### Iteration order

A `Dictionary` **promises no order**. If you walk one and anything depends on the order,
two runs can diverge. Which is why the code keeps saying:

```csharp
foreach (var (key, value) in nation.TradeSettings.OrderBy(entry => entry.Key, StringComparer.Ordinal))
```

(`Scripts/ui/TradeInstructionsUI.cs`)

Sort explicitly, by a stated rule, and the order is the same everywhere.

### Floating point

`float` is an approximation. `0.1f + 0.2f` is not exactly `0.3f`. So:

```csharp
if (value == definition.Default) ...                       // unreliable
if (MathF.Abs(value - definition.Default) < 0.0001f) ...    // how this code writes it
```

Comparing floats for equality means asking whether they are close enough.

### Three more rules

- **No system clock** in the rules. `DateTime.Now` is for the interface only (the
  copyright year, for instance).
- **No file or network I/O** in the daily loop: it is both slow and non-deterministic.
- **No Godot at all.** The simulation does not know whether a screen exists.

---

<a name="14-the-godot-half"></a>

## 14. The Godot half

```csharp
public partial class DealOrForceGame : Control
{
    public override void _Ready() { ... }
    public override void _Process(double delta) { ... }
}
```

**`: Control`** — inheritance: our class **is** a Godot node and gets everything a node can
do.

**`partial` is mandatory here** — not for tidiness but technically: Godot generates the
other half of every node class itself.

**`override`** — we are replacing a method Godot calls for us:

- `_Ready()` — once, when the node has entered the scene;
- `_Process(double delta)` — every frame; `delta` is the seconds since the last one.

### Frames are not game days

The most important idea in this section. Frame rate can be 30, or 144, or 7 on a tired
machine. **Tie game time to frames and the game runs faster on a faster computer.** So
`_Process` accumulates `delta` and separately decides whether it is time for a simulation
step:

```csharp
_dayAccumulator += delta;
var secondsPerDay = _campaign.Defines.SecondsForSpeed(_speed) * _campaign.TickScale;
while (_dayAccumulator >= secondsPerDay && ...)
{
    _dayAccumulator -= secondsPerDay;
    _campaign.Tick();
}
```

Note the `&& ...`: there is also a frame-budget condition. The frame gives the simulation a
few milliseconds, and when they are spent the loop stops and the rest happens next frame.
That way a slow machine gets a **slow game instead of an unresponsive one**.

### Signals and lambdas

```csharp
button.Pressed += () => ShowSettingsCategory(category);
```

`+=` subscribes to an event. `() => ...` is a lambda with no parameters.

**And here is the trap everybody hits.** A lambda captures **the variable, not its value**:

```csharp
foreach (var category in SettingsCatalog.Categories())
{
    var captured = category;                                   // ← this is why
    var tab = new Button { Text = ... };
    tab.Pressed += () => ShowSettingsCategory(captured);
}
```

(`Scripts/MainMenuScene.cs`)

Had the lambda said `category`, every button would open the **last** category: the loop
ended, one variable remains, and every lambda is looking at it. `captured` is a fresh copy
per iteration.

### The layout trap that cost five bugs

Worth memorising, because it does not look like a bug:

```csharp
// WRONG — the caption collapses to nothing:
var caption = Fixed(Title("ABOUT ONE GOOD", 12, ...));
var value   = Fixed(Title("", 11, ...));
value.SizeFlagsHorizontal = SizeFlags.ExpandFill;

// RIGHT — the caption expands, the value gets a fixed width:
var caption = Fixed(Title("ABOUT ONE GOOD", 12, ...));
caption.SizeFlagsHorizontal = SizeFlags.ExpandFill;
var value = Fixed(Title("", 11, ...), 120);
value.HorizontalAlignment = HorizontalAlignment.Right;
```

`Fixed(...)` turns on text clipping and sets no minimum width. A sibling with `ExpandFill`
takes all the room, the caption is laid out at zero and **clips itself down to a dot**. On
screen that does not look like broken layout, it looks like a mysterious design decision —
which is why it happened four times running without anybody reporting it.

The game now has a watchman: `Scripts/ui/LayoutAudit.cs`. In a debug build it looks over
whatever is on screen once a second and shouts into the log about a caption that was not
given room; the console command `layout` asks the same question by hand.

---

<a name="15-localisation"></a>

## 15. Localisation

**The translation key is the English string itself.**

```csharp
_governmentSetsRate.Text = Tr("The government controls the central bank");
```

And somewhere in `game/localization/russian/*.json`:

```json
{ "The government controls the central bank": "Центробанк управляется правительством" }
```

The upside: the code reads without a dictionary. The downside, and it is harsh: **change
the string in the code and you break the translation**, because no row has the new text
and the screen falls back to English. Text and tables have to change **in one move**, or a
Russian screen will one day start speaking English.

### What you must never do

```csharp
// NEVER:
var line = Tr("Price") + ": " + Fmt.Money(price);
```

Another language may want a different word order, and the translator receives a fragment
with no context. Do this instead:

```csharp
Simulation.Loc.Format("Price {0}", Fmt.Money(price))
```

The whole sentence is one key; numbers go into `{0}`, `{1}`.

### Three functions

| | What it does |
|---|---|
| `Tr("...")` | translate a string (inside interface classes) |
| `Loc.Format("... {0} ...", x)` | translate and substitute |
| `Loc.Has("key")` | is there such a translation at all |

`Loc.Has` matters where the key is assembled from data: `country.KOR.name.belorussia` may
or may not exist, and "no" is a normal answer rather than an error.

### Named entities are the exception

For countries, regions and commodities the key is an identifier, not an English string:
`country.KOR.name`, `region.1234.name`. Because a country's name is data, not interface
text, and a mod must be able to add a country that appears in no English table at all.

### The files

```
game/localization/english/00_english.json
game/localization/english/23_naming.json
game/localization/russian/00_russian.json
game/localization/russian/23_naming.json
```

Merged in filename order, later wins — exactly like the data catalogues in section 12. When
you add strings, start a **new numbered file** instead of editing the shared one: your
strings stay visible on their own and conflicts get rarer.

---

<a name="16-exercise-1"></a>

## 16. Exercise 1: your own field, from model to screen

The task: a country gains a "press mood" — a number from 0 to 1 that drifts slowly towards
stability. And it shows on the state affairs screen.

### Step 1. The field

`Scripts/Simulation/SimulationCore.cs`, inside `public sealed partial class Nation`:

```csharp
/// <summary>
/// How the government is being written about. Drifts towards stability, but slowly:
/// newspapers change their tone later than circumstances change.
/// </summary>
public float PressMood = 0.5f;
```

**Why with a default:** old saves know nothing about this field. Loading one will not set
it, and it stays `0.5f` — so an old campaign opens and behaves sensibly. A field with no
default would turn every old save into a country with zero press mood.

### Step 2. A rule that moves it

In the file where stability lives (`Scripts/Simulation/StabilityCore.cs`), in the daily
step:

```csharp
// A tenth of the gap a day: a fortnight and the papers have caught up with reality.
nation.PressMood += (nation.Stability - nation.PressMood) * 0.1f;
nation.PressMood = Math.Clamp(nation.PressMood, 0f, 1f);
```

`Math.Clamp(value, min, max)` holds a number inside its bounds. Without it one wrong
formula eventually produces `-3` and everything that reads the number starts lying.

### Step 3. The save

Find how neighbouring country fields are written in `Scripts/Simulation/SaveSystem.cs` and
add yours beside them — to both the write and the read. **A field that is not saved is
zeroed on load**, and that presents itself as "a bug that only happens after loading".

### Step 4. Show it

`Scripts/ui/StateAffairsUI.cs`, next to its neighbours:

```csharp
rows.AddChild(Reading(Tr("Press mood"), $"{nation.PressMood:P0}"));
```

Check the file for the exact helper — there is a ready-made "caption — value" row, and
**use that one** rather than assembling it by hand: it already accounts for the trap in
section 14.

### Step 5. The translation

Create `game/localization/english/31_press.json`:

```json
{ "Press mood": "Press mood" }
```

and `game/localization/russian/31_press.json`:

```json
{ "Press mood": "Настроение прессы" }
```

The English file is required even though key equals value: it is the list of what exists
to be translated.

### Step 6. See it

```bash
dotnet build DealOrForce.csproj -c Debug
```

Run, open state affairs, find the row. Then switch language and find it again.

---

<a name="17-exercise-2"></a>

## 17. Exercise 2: your own command

The task: a government can **lean on the press** — a one-off lift to press mood, paid for
in stability.

### The command

In `Scripts/Simulation/StabilityCore.cs` (or your own file beside it):

```csharp
/// <summary>
/// Lean on the newspapers. Press mood rises at once and stability falls: everybody can see
/// it happen, and "everybody" includes the people who read those newspapers.
/// </summary>
public sealed class LeanOnThePressCommand : ICampaignCommand
{
    public int IssuerId { get; init; }

    /// <summary>How hard, 0..1. Harder is more visible and more expensive.</summary>
    public float Pressure { get; init; }

    public void Apply(Campaign campaign)
    {
        var nation = campaign?.NationById(IssuerId);
        if (nation is null) return;

        var pressure = Math.Clamp(Pressure, 0f, 1f);
        nation.PressMood = Math.Clamp(nation.PressMood + pressure * 0.25f, 0f, 1f);
        nation.Stability = Math.Clamp(nation.Stability - pressure * 0.05f, 0f, 1f);
    }
}
```

Note that the **command computes nothing about the interface and knows nothing about it**.
It was handed an intent and carried it out.

### The button

In `Scripts/ui/StateAffairsUI.cs`:

```csharp
rows.AddChild(ActionButton(Tr("Lean on the press"), () =>
{
    _campaign.Commands.Enqueue(new LeanOnThePressCommand
    {
        IssuerId = _campaign.Player.Id,
        Pressure = 0.5f
    });
    _campaign.Commands.ApplyAll(_campaign);
    RefreshStateAffairs();
}, 160, 30));
```

Three lines in order: put the note in the queue, ask the queue to apply, redraw. **The
button never touches `nation.PressMood`** — which is exactly why this will work in
multiplayer and why it can be tested without a button. Which we are about to do.

---

<a name="18-exercise-3"></a>

## 18. Exercise 3: your own bench check

A check is a method that returns **an empty string when all is well** and the complaint
when it is not. Open `Tools/SimBench/Program.cs` and look at its neighbours; they are all
built the same way.

```csharp
/// <summary>
/// Leaning on the press costs exactly what it promises.
///
/// Three claims, and the third is why the check is worth writing at all: the bounds. A
/// formula that behaves in the middle and escapes past 1 at the edge does not break
/// immediately, it breaks ten years into a campaign, and it gets hunted in the wrong place.
/// </summary>
private static string LeaningOnThePressCostsWhatItSays()
{
    var campaign = Campaign.CreateBootstrap(20250704);
    var nation = campaign.Nations.FirstOrDefault(country => country.Gdp > 100f);
    if (nation is null) return "no country large enough to have an economy";

    nation.PressMood = 0.40f;
    nation.Stability = 0.60f;

    new LeanOnThePressCommand { IssuerId = nation.Id, Pressure = 0.5f }.Apply(campaign);

    if (nation.PressMood <= 0.40f)
        return $"the government leaned and press mood stayed at {nation.PressMood:P0}";
    if (nation.Stability >= 0.60f)
        return $"leaning was free: stability stayed at {nation.Stability:P0}";

    // And the edge: however hard you lean, nothing may leave 0..1.
    nation.PressMood = 0.99f;
    for (var attempt = 0; attempt < 20; attempt++)
        new LeanOnThePressCommand { IssuerId = nation.Id, Pressure = 1f }.Apply(campaign);
    if (nation.PressMood > 1f || nation.Stability < 0f)
        return $"the bounds gave way: press {nation.PressMood}, stability {nation.Stability}";

    Console.WriteLine($"  press: {nation.Tag} leaned and bought {nation.PressMood:P0} of " +
                      $"mood for {nation.Stability:P0} of stability.");
    return "";
}
```

And register it beside the others:

```csharp
failures += Check("leaning on the press costs what it says",
    LeaningOnThePressCostsWhatItSays());
```

Run it:

```bash
dotnet run --project Tools/SimBench/SimBench.csproj -c Release
```

**Why this is worth the time.** The check outlives you: in six months somebody optimises
the stability formula and drops the `Clamp` by accident. Without the check that surfaces
when a player's tenth year contains a country at −340 % stability. With it, forty minutes
after the edit, with a sentence saying what broke.

Note the `Console.WriteLine` too: **a passing check still says something**. Not "ok", but
numbers you can look at to see that the world still resembles a world.

---

<a name="19-how-to-read-the-code"></a>

## 19. How to read somebody else's code here

Three habits that save hours.

**1. Search for the words on screen.** A word you can see is also the translation key and
is also in the code:

```bash
grep -rn "The government controls the central bank" Scripts/
```

That lands you in the right file with no guesswork.

**2. Read the comments — in this project they explain *why*.** A comment saying "this
caption collapsed four times running" is worth ten minutes of staring. The
`/// <summary>` above a class is not paperwork, it is the shortest statement of why the
thing exists.

**3. Follow the name.** Found `CentralBank.SetEmission`? Open `CentralBank.cs` and read the
whole class; it is short. One file is one subject here, and reading the file almost always
beats extracting one function from it.

### Where things live

| I want | Look at |
|---|---|
| economic rules | `Scripts/Simulation/EconomyCore.cs`, `TradeCore.cs`, `InflationCore.cs` |
| key rate and printing press | `Scripts/Simulation/CentralBank.cs` |
| armies and fronts | `Scripts/Simulation/MilitaryCore.cs` |
| war and peace | `Scripts/Simulation/PeaceConference.cs`, `Statecraft.cs` |
| what the AI does | `Scripts/Simulation/AiStatecraft.cs`, `AiJudgement.cs` |
| automation for the player | `Scripts/Simulation/Delegation.cs` |
| country, campaign, calendar | `Scripts/Simulation/SimulationCore.cs` |
| saves | `Scripts/Simulation/SaveSystem.cs` |
| the main menu | `Scripts/MainMenuScene.cs` |
| the campaign root | `Scripts/DealOrForceGame.cs` |
| any screen | `Scripts/ui/*UI.cs` |
| the checks | `Tools/SimBench/Program.cs` |

---

<a name="20-mistakes"></a>

## 20. Mistakes you will make

| What you see | What happened | What to do |
|---|---|---|
| `NullReferenceException` | you touched something that is not there | `if (x is null) return;` first; section 6 |
| `Collection was modified` | you changed a collection inside its own `foreach` | `.ToList()` first, then change |
| a number comes out `0` that should not | integer division: `1 / 2` is `0` | `1f / 2f` |
| a condition never fires on a "round" number | you compared `float`s for equality | `MathF.Abs(a - b) < 0.0001f` |
| an English sentence on a Russian screen | missing `Tr`, or the string changed and the table did not | find the key and fix **every** file at once |
| a caption turned into a dot | the `Fixed(Title(...))` trap | section 14; check with the `layout` command |
| multiplayer diverged | `new Random()`, dictionary order, or the system clock in the rules | section 13 |
| a field is zero after loading | you did not add it to `SaveSystem` | add it to the write and the read |
| your change did nothing | you built the wrong configuration | the editor runs `Debug`, `SimBench` runs `Release` |
| "cannot build, file in use" | the game is still running and holding the library | stop the game and build again |

---

<a name="21-where-next"></a>

## 21. Where to go next

A route that actually works — every step produces something visible:

1. **Change a number in JSON** and find the change in the game. No code at all.
   → [MODDING_QUICKSTART_RU.md](MODDING_QUICKSTART_RU.md)
2. **Add a commodity and a building** in data. Still no code, and you are already changing
   the world.
3. **Read one simulation file end to end** — start with `CentralBank.cs`; it is short and
   it has everything: a catalogue, a rule, a command, bounds.
4. **Do exercise 1** from this document. Field, rule, save, screen, translation.
5. **Do exercises 2 and 3.** A command and a check — after those you can add a whole
   mechanic.
6. **Read [ARCHITECTURE.md](ARCHITECTURE.md)** — it will now read as a description of a
   place you know rather than as theory.

And then as needed:

- [MODDING_GUIDE.md](MODDING_GUIDE.md) — the full modder's reference, every catalogue and
  every field;
- [PLAYER_GUIDE.md](PLAYER_GUIDE.md) — what the mechanics mean from the player's side;
  more useful than it sounds, because it is hard to fix a rule you have never played;
- [BUILDING.md](BUILDING.md) — building, configurations, release.

---

*Found a place in this text that only makes sense if you already knew? That is a bug in the
document, not in the reader. File it as one.*
