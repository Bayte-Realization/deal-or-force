# Deal or Force diagnostic logs

*Русская версия: [DIAGNOSTICS_RU.md](DIAGNOSTICS_RU.md)*

## Where to look

Open the console with `~` and run `logs`. The command reports whether logging is on, the
actual folder, how many lines have been written and the last filesystem error.

The usual Windows folder is `%APPDATA%/Godot/app_userdata/Deal or Force/logs/`
(`user://logs`). A second copy of the game running at the same time uses an `instance-PID`
subfolder, so LAN clients do not overwrite each other's logs.

| File | What is in it |
| --- | --- |
| `system.log` | Session start and end, the game and map versions, engine, GPU/CPU, content-loading messages, saves and loads, and ordinary engine output |
| `performance.log` | Frame measurement windows every 5 seconds, FPS, p95 and maximum frame time, stalls of 100 ms and over, game-time lag, memory and garbage collections, and the duration, call count and allocation of individual calculations |
| `orders.log` | The start and end of commands that go through the queue: type, side, division, target, template, execution time, treasury change. For movement, the concrete result; for recruitment, either the queueing or the reason for refusal |
| `simulation.log` | The start and end of a game day, entity counts, campaign state, battle rounds and outcomes, retreats and the completion of training |
| `errors.log` | Engine, shader and script errors and warnings with a stack, exceptions from commands, the console and saving, unread data, log queue overflow, .NET exceptions |

The existing `godot.log` keeps its role and is especially useful for crashes before game
code runs. The new system neither deletes nor rotates it.

## How to read an entry

Every line carries a UTC time, `seq` (the entry id), `day` (the campaign day, −1 before a
campaign starts) and `thread`. Multi-line messages and stacks are folded with a literal
`\n`, so one entry stays one physical line.

Numeric diagnostic fields use a dot regardless of the Windows language. Durations are
milliseconds, memory is bytes, treasury and GDP are billions of the game's currency unit. A
command's line may contain the original localised value of its parameter.

A `BEGIN` with no matching `END`, or a `DAY_BEGIN` with no `DAY_END`, helps locate an
interrupted stretch. On generic commands, `Returned (command may be a no-op)` means only
that the handler returned — **it is not confirmation that an order was accepted**. Orders
the AI applies directly, bypassing the queue, have no universal argument logging yet; their
consequences are visible in the simulation and in the timings.

## When it runs slowly

1. Find the time in question in `FRAME_STALL` and `FRAME_WINDOW`.
2. Compare `p95_ms` with `max_ms`: an average FPS can hide rare but noticeable hitches.
3. Cross-reference `SLOW_SCOPE` and `CALC_WINDOW`: the stage name, `total_ms`, `mean_ms`,
   `calls`, `allocated_bytes`.
4. A rising `pending_seconds` while time is running shows the simulation failing to spend
   the time it has accumulated. Check the game speed and the `campaign.day` timing.
5. A jump in `gc_counts` next to a large allocation helps find pressure on the garbage
   collector. The log itself **does not force a collection**.

These are CPU timings and frame intervals, not a full GPU profiler. Frame time also includes
the influence of the OS, of loading, of taking screenshots and of other applications.
`CALC_WINDOW` shows the difference between counters over an interval, not an average across
the whole session. At most 4096 frame intervals are taken inside one window.

## When a division will not move, or retreats oddly

Search `orders.log` for `MoveArmyCommand` by `ArmyId` and `TargetProvinceId`. Then search
the same day and province in `BATTLE_ROUND`, `BATTLE_END` and `RETREAT`.

A battle round records the effective attack and defence, both sides' organisation, the
losses, the strength of a river crossing and the planning. These are diagnostic values of
the calculation, not a complete step-by-step trace of every multiplier.

`RETREAT_NO_EXIT ... action=left_in_place known_defect=true` is a known encirclement defect:
the losing formation stays on the territory it lost. The log makes it observable; it **does
not fix it**. The same check also found that a `ReadyDay` after a retreat does not forbid an
ordinary march across your own territory.

## After a crash

- Copy the log folder before the next few runs. Attach a save from before the problem, the
  steps to reproduce it and the approximate time.
- `FATAL` holds a .NET exception when a handler was available; `ENGINE_*` holds engine
  details and whatever stack there was.
- `SESSION_END clean_shutdown=true` means shutdown reached the normal closing of the game
  node. The absence of that line points to an abnormal or interrupted exit, but does not by
  itself prove any particular cause.
- This is **not a crash dump**. A native abort, a driver hang, a power cut or a killed
  process may leave no managed stack and no last buffered messages. In that case `godot.log`
  and the system's own crash information are needed too.

The diagnostic files contain the internal state of the world, including battle values hidden
by the fog of war. This is a developer's tool, not a network anti-cheat mechanism. Before
sending them anywhere public, check the file names, the paths and the message text of mods.

## Write limits and settings

`game/common/diagnostics/00_logging.json` is read at startup:

- `enabled` — turn logging on;
- `max_file_mb` — the maximum size of one file (1–32 MB, default 4);
- `queue_limit` — the maximum number of pending messages (128–16384, default 2048);
- `slow_scope_ms` — the threshold for recording a slow measured scope (default 40 ms).

Changes need a restart. A background thread flushes the buffer roughly once a second and on
close. Under overload, messages are dropped with an explicit `LOGGER_OVERFLOW` entry, and
the game thread does not wait for the queue to drain. A write error turns logging off rather
than crashing the game; the reason is available through `logs`.

Each of the five files keeps a current version plus `.1` and `.2`. Rotation happens at the
next start and on reaching the size limit; the oldest `.2` is deleted with no way to recover
it from inside the game. That is about 60 MB for a normal set at the default settings (not
counting Godot's own logs and second LAN instances). The size may exceed the limit slightly,
by one entry or buffer.

## How the implementation is verified

`Tools/ArmyInteractionTests` covers multi-threaded writing, a command refusal, the
synchronous write of a fatal message without an actual crash, rotation and the normal flush.
A runtime probe additionally writes a deliberate `DIAGNOSTIC_PROBE_EXPECTED_WARNING`; that
is a test of error logging, not a fault in the game.
