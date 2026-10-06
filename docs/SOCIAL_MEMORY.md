# Historical norms and audience-dependent reputation

Experimental gameplay slice in the Godot 4.8 copy; the 4.7 project is unchanged.
[Русская версия](SOCIAL_MEMORY_RU.md).

## Gameplay

Institutions acquire familiarity over peaceful years, contributing up to 3.5 percentage
points of stability with default tuning. A change in government, ideology or subject status
resets that habit. Instability and shortages interrupt growth. Prosperity becomes an
expectation: expectations rise faster than they fall, so a subsequent living-standard loss
can outweigh institutional comfort.

Regions remember their owner. Undisturbed ownership gradually reduces secession pressure
by at most 25% with default tuning. Occupation, radicalism and hardship erode it; ownership
changes reset it. Civic independence remains possible.

Every society has its own image of countries/movements, resident cultures and active
coalitions. Public successful chronicle facts leave bounded, decaying impulses: war, peace,
peaceful transfers, alliance rupture, financial aid, denunciation and movement dealings.
Loading the chronicle does not replay them. Idealisation/rejection need sustained esteem;
entrenchment slows reversals, while hysteresis prevents flickering labels.

Reputation affects diplomatic opinion, directed alliance willingness and local movement
support. Habits and expectations affect stability; border memory affects secession pressure.
Opinion bonuses are excluded from their own reputation target, preventing positive feedback.

Diplomacy shows both directions of opinion. Your country page exposes domestic standing,
institutional age, living expectations, net stability effects and expandable audience/culture/
coalition views. Unknown images are shown as unknown. Expanded rows are created on demand,
six views per page; paging does not rebuild the entire diplomacy screen.
Translations live in `localization/*/114_social_memory.json`.

## Modding

Features `historical_norms` and `public_reputation` can be disabled independently through
the existing feature defines, scripted effects and unlock triggers. Disabled state is frozen,
its effects/UI disappear, and re-enabling resets review clocks without inventing dormant years.
`FeatureSystems` gates assigned to `SocialMemory` also disable its downstream effects.

`common/social_memory` accepts JSON/YAML/YML, with partial overlays preserving omitted fields:

```yaml
war_started:
  actor: -0.25
  target: 0.06
  witness: 0
  name: A new war
```

Weights are finite numbers bounded to -1..1. `actor` changes observers' view of the actor;
`target` changes their view of the target; `witness` adds an actor-directed observer impulse.
Direct participants see more; allies discount the actor's reputational cost. Domestic impulses
use a quarter of the weight. Invalid input produces mod diagnostics.

Tuning defines: `SocialReviewDays`, `InstitutionHabitYears`, `BorderHabitYears`,
`HabitStabilityBonus`, `HabitExpectationPenalty`, `BorderHabitRelief`, `SocialResponseDays`,
`SocialEntrenchmentDays`, `SocialIncidentDecayDays`, `SocialOpinionWeight`,
`SocialAllianceWeight`, `SocialMovementWeight`, `SocialLabelMinimumDays`.

Existing events/decisions can author audience-scoped narratives:

```json
{
  "effects": {
    "social.expected_living_set": 0.6,
    "social.domestic_incident": 0.08,
    "reputation.nation.CAN": 0.15,
    "reputation.culture.abkhaz": 0.12
  }
}
```

The **scoped nation is the audience**, not the subject. `reputation.coalition.TARGET:DAY`
addresses an active coalition by target id and formation day. Values add; suffixes `_set`,
`_mult`, `_percent` follow standard scripted operations. They change the pending impulse,
not settled esteem/status instantly. Subjects must exist. Country tags are canonicalised;
cultural ids preserve authored spelling. Disabled reputation rejects scripted impulses.

## Public outreach

Foreign diplomacy offers public dialogue (90 days) and cultural exchange (120 days).
A PNG-backed picker chooses the programme. Its separate card visibly quotes one-time
funding, duration, recovery and the reason an action is blocked, without requiring hover.
The panel displays progress and remaining recovery. Starting restores keyboard focus to
the picker. These fund public contact, not a
government treaty or a guaranteed opinion/status. Contact contributes a small, gradual,
decaying incident; politics and public events still determine the resulting view.

Exchange also improves directed views of each other's primary cultures. Its cultural
budget is divided across those cultures, not multiplied by their number. Funding is paid
once; changing the action cannot evade the shared per-audience cooldown. Default capacity
is three simultaneous programmes (`PublicOutreachMaxActive`, minimum one). War, embargo or
disappearance interrupts work without a refund. Feature/system disabling freezes both
progress and cooldown, and reactivation cannot count dormant years.

Moddable `common/public_outreach` accepts JSON/YAML with action-id objects and fields
`name`, `duration_days` (7–3650), `cooldown_days` (1–3650), `gdp_cost_share` (.000001–.01),
`nation_impulse` and `culture_impulse` (0–.4 for the entire programme). Missing fields
preserve previous values; invalid numbers produce diagnostics. New actions automatically
appear in the UI. Paid programmes snapshot their promised budget across catalogue reloads.
`StartPublicOutreachCommand` is feature-bindable and uses the normal campaign/network queue.

Action/metric PNG roles live in `gui/31_action_icons.json`: `action.Start programme`
maps to `plan_execute` (`icon_plan_execute`), and `social.funding` maps to `treasury`.
Action keys use source English captions; already-localised Russian captions resolve to
the same semantic role. Explicit button icons win. Unknown/numeric captions are not
guessed. This is an extensible mapping, not a claim that every game control and value
already has an icon.

## Persistence and limits

Additive fields participate in campaign saves, network snapshots and fingerprints. Old saves
start with unknown history, not fabricated past years. Weekly reviews are staggered, use no
randomness, and tolerate multi-day steps. Dissolved coalition images are removed.

This is not a complete global cultural/media simulation. Cultural views currently develop
through residential contact, funded exchange and authored narratives. Government actions do not automatically
damage the image of a whole ethnic group. Reputation-specific sanctions, discrimination and
AI outreach planning are not implemented in this slice. Domestic standing does not duplicate the
institutional stability bonus.

Tests: `dotnet run --project Tools/FormableTests/FormableTests.csproj -- --social-only`.
They cover opposite audiences, reversals, disable/re-enable paths, scripted operations,
invalid subjects, full campaign snapshots and deterministic social continuation after loading.
