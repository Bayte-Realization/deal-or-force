---
name: dof-capture-play
description: >-
  Drive a real Deal or Force session from a script and photograph the screens a player reaches,
  with the failure modes that cost hours on 2026-10-16. Use when asked to play the game, look at
  a screen, inventory the HUD, verify a UI change, or when a capture session goes silent.
---

# Playing the game from a script

`Tools/game-capture.ps1 -Script res://path.gd` runs the game out of the editor project with
Fennara's runtime helper on. The script is `extends RefCounted` with `func run(ctx)`; `ctx` is
documented in `addons/fennara/runtime/runtime_script_context.gd`. Images land in
`tmp_playtest/captures/<stamp>/`, and the log beside them carries every `ctx.log` line as
`FENNARA_SCRIPT_LOG` JSON.

## Four rules that cost a session each when broken

**Close the Godot editor first.** With the editor open, Fennara routes the runtime session to
the editor, the game's MCPRuntimeServer takes the next free port (6571 instead of 6570), and
`FENNARA_RUNTIME_SESSION_READY` never arrives — the run dies with *"the game did not report
readiness within 240 s"* and a log that ends right after the MCPRegistry line. That is session
routing, not a game crash. Every successful capture on 2026-10-16 ran with the editor closed;
the first failure appeared the moment it was opened.

**One script per session.** `AGENTS.md` says the game is left running so more scripts can be
sent to it. The file channel is read once at startup: writing a second
`run_runtime_script` JSON into `<session>/commands/` is silently ignored — no `STARTED`, no
status file. Put every stage in one script, and `taskkill` the previous PID before starting
(the project rule is never two Godot instances).

**Log before the first `await`.** A script that dies silently and a script that is waiting look
identical. `ctx.log("probe entered")` on the line above `await ctx.wait(...)` is what
distinguishes them.

**A vanished window is usually a person.** On this machine the game window gets closed by
someone walking past. The symptom is a probe that logs once and then nothing, a status file
frozen at `"running"`, and a `game.err` with no crash trace — only the known
`gdsyncprofiler` unregister error and the usual one leaked ObjectDB. Check the process is gone
and the exit is clean before suspecting your GDScript.

## Reaching a campaign

Singleplayer → *Choose country on map* → wait ~12 s for the globe → press the begin button.
The campaign opens **paused**; `Space` resumes it.

The picker does **not** list every country. It shows four large ones (USA, CHN, JPN, DEU), a
scroll of movements and unrecognised states, and the rest of the world is chosen by clicking
the globe. So `BEGIN AS POL` is not always there — the docs' "Poland is the first country"
does not match the picker. Read the tag off the button instead of hardcoding it:

```gdscript
for node in ctx.get_scene_root().find_children("*", "Button", true, false):
    var button := node as Button
    if button != null and button.is_visible_in_tree() and button.text.begins_with("BEGIN AS"):
        begin = button
```

## Inventorying the HUD

The honest way to ask "what can a player see and press" is to walk the tree, filtered:

```gdscript
if not node.is_visible_in_tree():   # the HUD keeps closed windows in the tree; an
    return                          # unfiltered walk reports hundreds on a screen of nine
```

Collect `text`, `path` and `disabled` per `Button` and log one line each. Two things this
caught that screenshots did not: a nav button that looked pressed but never changed the screen,
and the exact label a stray caption belonged to.

**A screenshot cannot name a node.** When a caption is visible but no `Label`/`Button`/
`LineEdit` carries that text, walk *every* node and *every* `TYPE_STRING` property, hidden and
`Label3D` included, and print the parent chain. If still nothing owns it, the glyphs are drawn
procedurally (`_draw`, shader, baked texture) and the search moves there — say so rather than
guessing at an owner.

## Reading what you found

- Identical photographs after clicking different things is a finding, not a broken script: the
  click never arrived. Same visible-button count across "different" screens confirms it.
- A row clipped by a panel's own edge is usually a scroll container doing its job. Check whether
  the height is recomputed after the rows are rebuilt (`RefreshStrategicHud` ends in
  `Callable.From(FitOutliner).CallDeferred()`) before calling it a defect.
- Withdraw what you cannot support. Two "defects" reported on 2026-10-16 were wrong: the outliner
  height was a search that missed a method-group call, and the picker's country/movement split
  turned out to be deliberate design.

## Verifying a UI change

Compile is not evidence. The order that works: `Tools/dev.ps1 -Build -Test` (the headless stand
guards the simulation, not the interface), then one capture session with the editor closed,
photographing the before and after of the specific interaction. `EXPORT_VERIFY failures=7`
naming only addon autoloads is the editor project, not the game — the release gate is
`Tools/build.ps1`.
