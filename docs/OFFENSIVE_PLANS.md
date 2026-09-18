# The offensive line — 8 September 2026

*Русская версия: [OFFENSIVE_PLANS_RU.md](OFFENSIVE_PLANS_RU.md)*

## Controls

1. Select your divisions and give them a **Front**: LMB on the border for the whole
   connected sector, or RMB-drag for part of it.
2. Select the divisions of **one** such front, press **Attack** and drag **RMB** across the
   land of its target country. What you draw is the final objective line, not one division's
   path.
3. Release RMB: the plan is stored in preparation. The troops may take up their start
   positions on their own territory, but they do not attack by themselves.
4. **Execute** starts the advance. **Prepare** stops the plan's movement where it stands (a
   battle already joined is not cancelled).
5. **Attack → Alt+RMB** redraws the existing objective in place, without making a duplicate.
   Esc cancels an unfinished drawing. The middle button moves the map, the wheel zooms.

The objective is a thin line; the direction of the operation is a single arrow from the
start sector. The individual routes of the selected divisions are drawn separately: yellow
for redeployment, red for an active advance. The panel shows the plan's phase and how many
divisions have reached the objective.

## What was fixed

- The objective no longer starts out containing provinces of the old front. Switching tools
  no longer mixes the start front with the destination line.
- Start positions, the order of provinces in the objective stroke and the division
  assignments are all stored separately. The start front updates when the border changes;
  the objective does not move with it.
- A fast brush stroke fills the provinces it skipped with a connected land path. Dragging
  back along the stroke shortens it.
- Refused: somebody else's divisions, mixed start fronts, an objective in the wrong country,
  a broken line, and objectives unreachable across sea or neutral ground. A refusal does not
  erase the current orders.
- Divisions get stable, distinct sectors of the objective, build real routes through
  adjacent provinces, and stop on arrival. A blocked route no longer turns into an attack on
  an arbitrary neighbouring province.
- Removed the forced hiding of plan geometry every frame. Switching between 2D and the globe
  rebuilds the plan in the matching projection.

## Verification and limits

Regression-tested: the commands, saving and the network snapshot, preparation, execution,
movement to two different parts of the objective, completion without further unbidden
attacks, and editing. In a real Godot window: mouse input, the preview, saving on RMB
release, the Alt redraw, the routes and both projections.

This is the basic "front → objective" model, not a full copy of HOI4. Chains of sequential
phases, free editing of the axis curve, and automatically taking every province of the whole
area between the lines are not implemented yet. Alt+RMB replaces the objective stroke
entirely rather than dragging an individual vertex. Old plans still load, but a stroke that
was previously corrupted should be redrawn: the player's intent cannot be reliably
reconstructed from a mixed set of provinces.

Brush safety limits: up to 128 provinces in a line, up to 16 intermediate provinces per
cursor jump. The operation's assignment is written to `orders.log` as `OFFENSIVE_PLAN`.

For the meaning of the front and the final objective, the reference point is the
[official HOI4 dev diary on operational plans](https://store.steampowered.com/news/posts/?appids=394360&enddate=1457093764),
the entry of 19 February 2016.
