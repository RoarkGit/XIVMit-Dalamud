# XIVMit (Dalamud plugin)

In-game view of a [xivmit.app](https://xivmit.app) mitigation plan. Shows your upcoming
mitigations against a live fight clock, starts on combat, resyncs itself from boss actions, and
ticks off mitigations as you actually press them.

## Status

Working first version. Builds clean; plan loading and resolution are verified end-to-end against
the live API. The in-game behaviour (hook, cast sync, press detection) has not yet been exercised
in an actual pull.

## How it works

### Data

Everything comes from the public, unauthenticated xivmit.app endpoints:

| Endpoint | Used for |
| --- | --- |
| `GET /api/plans/:code` | roster + assignments |
| `GET /api/fights/:id` | phases, boss actions, `maxLevel` |
| `GET /api/jobs/:job` | ability names, durations, `abilityId` |

`PlanContext` joins these into a flat list of `PlannedMit`. Level-adjusted durations mirror
`effectiveDuration()` from the web client's `gameUtils.ts`; the heavier logic there (charge
pools, lanes, conflicts) is deliberately not reimplemented, because a read-only display does not
need it.

### The clock

Advances off `IFramework.UpdateDelta` rather than wall time, so it stalls with the game during a
loading screen or hitch instead of running ahead. Manual start/pause/stop, a phase-skip dropdown,
and automatic start on `ConditionFlag.InCombat`.

### Sync

Two tiers, tried in order:

1. **Phase advance from boss identity.** `FightTracker.DetectPhaseAdvance` is exact where
   verified, falling back to a heuristic otherwise:
   - `VerifiedPhaseOpeners` holds real game NPC ids (Dalamud's `BaseId`) confirmed, per fight,
     to debut in exactly one phase - each entry was checked against 3 independent FFLogs pulls:
     the actor stays entirely within one phase's time window, and its id is identical across
     all three pulls. A single sighting of that id is trusted immediately, no debounce needed.
     Currently 15 of 22 ultimate phase transitions (FRU 5/5, UMAD 4/4, DSR 4/7, TOP 2/6).
   - Everywhere else, the fallback: the targetable `Combatant` currently carrying the most max
     HP changing to one not seen before this pull, held for `PhaseAdvanceDebounce` (1.5s, so a
     stray add that briefly outHPs the boss can't commit a false jump). Needs no per-fight
     authored data at all, at the cost of being an approximation rather than an exact match.
   - A name alone is not a safe key for the verified table: querying FFLogs turned up multiple
     real, distinct NPC ids sharing one displayed name within a single pull (Kefka alone spans
     seven) - most are cleanly phase-scoped and became table entries, but two are a
     cross-phase, persistent model and were excluded. The split is only visible by checking
     each candidate's time window and cross-pull stability, not by counting distinct ids.
2. **Cast/hit matching** against authored `BossAction.GameActionId`s, via two observation paths
   since neither alone is sufficient: cast bars polled off `IObjectTable` (patch-stable,
   sub-second accurate via the game's own live cast progress, but only fires for actions that
   actually have a cast - 23 of UMAD's 89 boss actions), and action effects via a hook on
   `ActionEffectHandler.Receive` (catches the instants that never show a cast bar, and also
   detects the local player's own presses).

A recognised action within 5s of the clock is treated as drift and nudged; a larger delta is
treated as a phase jump (toggleable via the same setting that gates tier 1, and bounded at 600s
so a stray match cannot fling the clock across the fight).

A third tier - crossing an authored HP percentage, for a same-actor transformation neither the
verified table nor the heuristic can resolve confidently (a boss that keeps its NPC id but
changes moveset, e.g. UMAD's Kefka -> God Kefka) - is not implemented. It would need a per-phase
HP threshold authored per fight, which nothing in this repo currently derives.

Only `BossAction.GameActionId` (authored in the fight data) feeds tier 2 - there is no runtime
learning or name-matching fallback. An id-less boss action simply isn't a sync candidate; tier 1
covers most phase transitions regardless.

## The hook

`ActionWatcher` hooks `ActionEffectHandler.Receive`, the client's entry point for `ActionEffectN`
server packets:

```
Receive(uint casterEntityId, Character* caster, Vector3* targetPos,
        Header* header, TargetEffects* effects, GameObjectId* targetIds)
```

It targets a named FFXIVClientStructs member-function address rather than a raw byte signature,
so a patch that moves code is resolved by a ClientStructs bump instead of a manual re-scan. It is
still the most patch-fragile part of the plugin, so it is built to fail soft: the detour calls
the original first and unconditionally, subscriber exceptions are swallowed rather than
propagated back into the game's packet path, and a failed hook degrades to "no press detection"
with a notice in the UI rather than taking the plugin down.

## Building

Requires the Dalamud reference assemblies. `DALAMUD_HOME` is already set correctly by
XIVLauncher Core on Linux (`~/.xlcore/dalamud/Hooks/dev`).

```sh
dotnet build -c Release
```

Output lands in `bin/Release/`, with a packaged `bin/Release/XIVMit/latest.zip` alongside.

## Icon

`images/icon.png` is a 512x512 rasterisation of `data/logo.svg` from the XIVMit repo, regenerable with:

```sh
magick -background none -density 1200 ../XIVMit/data/logo.svg \
  -resize 512x512 -gravity center -extent 512x512 -depth 8 -strip images/icon.png
```

(ImageMagick needs its `rsvg` delegate for the gradient to render; its built-in SVG renderer does
not handle `linearGradient` correctly.)

## Installing locally

`/xlsettings` -> Experimental -> Dev Plugin Locations -> add `bin/Release`, then enable XIVMit in
the plugin installer.

## Usage

- `/xivmit` opens the window
- `/xivmit UMAD-4C3ME5` loads a plan code directly
- `/xivmit overlay` toggles overlay mode

The plan code is the part after `?plan=` in a xivmit.app URL.

## The two views

**Full** is a scrolling time axis: mitigations enter at the bottom and rise toward a NOW line, so
a mitigation's position on screen *is* its distance in time. That is what makes a whole phase
readable at a glance. The wheel scrolls the viewport ahead of the clock without moving the clock
itself; ctrl+wheel zooms the axis.

**Overlay** drops the title bar, plan bar and settings, and replaces the axis with a fixed stack
of draining bars. Position becomes plain running order and each bar's own length carries the
countdown - the trade cactbot's timeline makes. At overlay size there is no room for a spatial
axis, and a stack stays legible where a compressed axis would not. Transport controls and phase
buttons stay, since those are the ones reached for mid-pull, and the clock line keeps an exit
button.

It runs as a true overlay: window background and border are fully transparent, so the bars sit
directly on the game. Buttons stay legible through their own fill and border, bare text gets a
dark outline rather than a panel, and the bar tracks stay semi-opaque so the drained portion
still contrasts against whatever is underneath. The clock/phase readout is rasterised at a fixed
22px (see `ScaledFont`) rather than scaled up from the default, so it stays sharp; its vertical
centring is a font-metric estimate (Dear ImGui does not expose cap height) checked by eye against
the real in-game AXIS font.

The phase strip collapses in either mode via the chevron at the right of the clock line, handing
its space back to the mitigations. The control only appears for fights that have phases.

Overlay mode is ignored until a plan is loaded, since the plan bar it hides is the only way to
load one.
