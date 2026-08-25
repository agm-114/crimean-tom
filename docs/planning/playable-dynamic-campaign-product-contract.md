# Playable dynamic campaign product contract

## Status and authority

This document is the product-level source of truth for how Crimean Tom should
work for a player. It defines the minimum coherent dynamic-campaign experience;
the architecture documents define how the system preserves that experience.

When this contract conflicts with an implementation shortcut or an older phase
description, this contract controls the intended player behavior. The
[system architecture](../architecture/system-architecture.md) remains
authoritative for ownership, persistence, identity, security, and failure
semantics. The [current status ledger](current-status.md) records which parts of
this contract are actually implemented.

The terms **must**, **should**, and **may** distinguish required behavior from
preferred behavior and optional implementation choices.

## Product promise

Crimean Tom is one continuously advancing war, not a sequence of disconnected
missions. Most of the theater is simulated outside Unity. Sea Power presents
the player's local high-fidelity battle while remaining connected to the same
campaign timeline.

A player must be able to:

1. create or load a campaign without it advancing unexpectedly;
2. understand the friendly order of battle and the known enemy picture;
3. issue strategic and operational intent;
4. enter a local Sea Power tactical session with the correct task force and
   nearby interaction island;
5. inspect and select campaign units without needing to know whether they are
   local native objects, visual proxies, or externally simulated records;
6. fight while the rest of the war continues;
7. return tactical outcomes to the durable campaign; and
8. save, quit, reconnect, and recover without creating a second timeline.

## Core experience principles

1. **One world.** Strategic, Operational, and Tactical are fidelity levels of
   the same entities. They are not separate copies of the campaign.
2. **Map-first continuity.** The map is the stable view of the war. Physical
   objects stream around player interest and tactical need; map presence does
   not depend on local Unity materialization.
3. **Representation is an implementation detail.** A non-local friendly map
   unit must use the same Sea Power symbology, selection affordances, labels,
   and interaction behavior as a comparable local unit.
4. **No hidden-truth leakage.** Friendly state is coalition-authorized truth.
   Enemy state is a contact estimate derived from coalition detections, with
   uncertainty, classification, age, and confidence.
5. **No double simulation.** Exactly one current authority advances each entity
   or interaction island. Proxies never simulate or publish outcomes.
6. **The war continues during battle.** A tactical session renews its ownership
   lease through heartbeats while unrelated campaign entities continue in
   parallel at the declared campaign tempo.
7. **Failure is visible and recoverable.** Streaming, terrain, authority, save,
   and connection problems produce actionable player state, not silent missing
   units or duplicate objects.

## Campaign lifecycle

### Create and load

- Creating a campaign must produce a named, versioned, durable save with an
  explicit ruleset, scenario package, player coalition, and opening time.
- Loading or inspecting a campaign must not silently advance campaign time.
  A newly created or restored campaign starts paused until the player starts or
  resumes it.
- The UI must show whether the campaign is paused, running, degraded, recovering,
  or blocked by an incompatible game/catalog version.
- Starting a tactical session does not pause the campaign. Only an explicit
  player/operator pause or a declared global safety policy may do so.

### Save and recovery

- Campaign state, orders, objectives, intelligence, task-force membership,
  authority assignments, and the last accepted tactical checkpoints must be
  durably recoverable.
- Autosave must occur at bounded campaign intervals and at tactical handoff,
  accepted checkpoint, normal tactical exit, and major objective transitions.
- A crash may lose only the explicitly declared uncommitted tactical interval.
  Recovery must identify that interval and the resumed authority source.
- An incompatible save must remain intact and offer a documented migration,
  compatible-engine, or export path. It must not be rewritten opportunistically.

## Player surfaces

Crimean Tom has two complementary views:

1. **Campaign view:** a coalition-filtered strategic/operational map, force
   hierarchy, orders, objectives, logistics, intelligence, time controls, and
   campaign health.
2. **Sea Power tactical view:** the detailed local interaction island plus a
   campaign-backed map overlay that preserves friendly and detected-hostile
   continuity beyond the locally simulated set.

The first release hosts the campaign view in a local Crimean Tom application.
Sea Power remains the battle and tactical-map surface. A diagnostic true-state
operator monitor is not a player campaign view and must not be reused as the
fog-of-war boundary.

## Map and symbology contract

### Friendly picture

- Every non-destroyed friendly campaign entity authorized to the player
  coalition must be represented on the campaign map.
- The Sea Power tactical display must represent the player's task force, the
  current interaction island, and all other friendly campaign entities supplied
  by the campaign projection. Locality must not determine whether a friendly
  unit has a map symbol.
- At wide zoom, clustering, formation symbols, or aggregation may reduce visual
  clutter, but may not make entities undiscoverable. Drilling into a cluster or
  zooming in must expose its members.
- Native objects and campaign projections must use the same Sea Power posture,
  domain, heading, color, label, hover, selection, and focus conventions. Debug
  crosses, placeholder X markers, and mod-specific fallback icons are not a
  shippable representation.

### Hostile and unknown picture

- Hostile symbols exist only for tracks known to the player's coalition.
- A track must expose its estimated position, uncertainty, classification,
  confidence, source/age where authorized, and lost/stale state.
- Selecting a hostile track must never reveal its true campaign position,
  identity, loadout, sensors, orders, or damage unless coalition intelligence
  has legitimately resolved those fields.
- Track symbols must remain clickable even when uncertainty is large, symbols
  overlap, or the physical object is not present locally. Overlap must resolve
  through deterministic hit selection or a contact-choice list.

### Selection parity

Selecting any visible entity or contact must:

1. mark it selected using native Sea Power visual feedback;
2. identify it in the appropriate unit/contact panel;
3. center or focus the relevant map without an unexplained camera jump;
4. request the best permitted local representation; and
5. show a bounded loading, unavailable, or failed state until that request
   resolves.

For a friendly campaign entity, this request is entity-scoped. The selected
entity may be promoted even when the rest of its Operational formation remains
external. Only dependencies that Sea Power must own with it, such as its native
host or a live weapon/sensor coupling, join the tactical lease. A visual model
is acceptable as immediate feedback, but it is not a successful promotion and
must be replaced by a native simulated object before the player can inspect
native stores or issue tactical orders.

Selection must not require a local simulated `ObjectBase`. A map-only record is
a first-class selectable object.

## Player task force and local tactical set

### Campaign-owned membership

- Player task-force membership belongs to the campaign save, not to a static
  authored Sea Power mission.
- On tactical entry, every available member assigned to the player task force
  must be loaded as a real locally simulated Sea Power object unless a visible
  compatibility or capacity rule excludes it.
- Hosted aircraft, helicopters, boats, and other children required for that
  task force must remain associated with their parent and be included according
  to the supported portable-state contract.
- A task-force member may not be represented only by a decorative map symbol
  while the campaign claims it is under the player's local tactical control.

### Interaction island

- The local tactical set includes the player task force plus entities joined by
  live sensor, weapon, formation, support, targeting, or collision relationships.
- High-end resident systems such as strategic air-defense complexes, critical
  radar nodes, and ballistic-missile launchers request persistent Tactical
  residency when compatible local capacity exists. Their campaign map presence
  remains unconditional even when they fall back to Operational fidelity.
- Ground formations may stay abstract for ground-versus-ground resolution while
  exposing representative tanks, low-end GBAD, artillery, logistics, and other
  physical targets when local surface or air attack requires them.
- Terrain-backed units and installations must not become visible until placement
  is validated against loaded terrain. Models must be clamped above the terrain
  surface; water/land mismatches must fail visibly rather than spawn underground.

## Streaming and proxy behavior

Representation follows a monotonic player-facing path:

```text
campaign record
 -> native-looking selectable map symbol
 -> visual-only local proxy when selected or observed closely
 -> simulated tactical object when authority and interaction require it
 -> checkpointed return to Operational/Strategic fidelity
```

- A visual proxy may use a real installed model, a classification-appropriate
  generic model, or an uncertainty volume. It must have no AI, sensors, weapons,
  collision, damage authority, or campaign publication rights.
- Promotion is complete only when the matching native object is initialized,
  controllable when friendly, and carrying its required loadout and movement
  state. The visual proxy then disappears without changing map identity.
- Friendly proxies use authorized friendly state. Hostile proxies use only the
  filtered contact estimate and must not instantiate a secret true-state enemy
  merely to hide it visually.
- Selecting a projected unit should begin streaming immediately and provide
  progress feedback. Repeated selection must be idempotent.
- Promotion must replace or hide the matching proxy atomically enough that the
  player never sees two authoritative-looking copies.
- Demotion must preserve the map record while removing expensive local
  simulation only after a durable checkpoint or declared fallback decision.
- Streaming budgets may delay physical realization, but must not remove map
  visibility, selection, or error feedback.
- Sea Power may hold native objects in only one bounded floating-origin locality
  at a time. Selecting a distant unit first checkpoints and suspends the current
  native island, then rebases and promotes the destination island. Theater-wide
  map records remain visible throughout the handoff.
- Authored scenario objects obey the same locality rule. They cannot remain
  active at the scenario origin after the campaign has rebased thousands of
  kilometers away.

## Time, leases, and concurrent simulation

- Campaign time uses a fixed logical step and a declared target tempo.
- Sea Power may retain its supported native fixed timestep inside the tactical
  process, but the UI must explain the relationship between tactical time,
  campaign time, and time compression.
- While a tactical session is healthy, heartbeats renew the assignment lease.
  The owned interaction island is fenced from external advancement, while all
  unrelated entities continue in the external campaign.
- A missed heartbeat does not instantly permit two authorities. The service
  waits the declared grace period, fences the stale epoch, and resumes from the
  last accepted checkpoint using tactical recovery or Operational fallback.
- Pause is explicit and scoped. Pausing the player's tactical presentation does
  not implicitly freeze the whole theater unless the selected single-player
  ruleset says so and the UI reports that policy.

## Orders, AI, and campaign progression

### Player orders

- Player orders are coalition-scoped authenticated campaign commands, not
  privileged operator mutations or local-only Unity state.
- The first playable set includes move, patrol, hold, escort/support, engage,
  withdraw, task-force membership, readiness/EMCON/ROE policy, and cancellation.
- Commands must return accepted, conflicted, unauthorized, unsupported, or
  superseded feedback and remain idempotent across reconnects.

### Strategic and operational AI

- Each belligerent has a strategic director that selects objectives, allocates
  forces, manages reserves and sustainment, and revises plans from coalition
  intelligence rather than omniscient enemy truth.
- Operational sub-AIs execute bounded packages for naval movement, air defense,
  air tasking, strike/SEAD, logistics, ground maneuver, and local defense.
- AI must respect fixed installations, platform capability, readiness,
  ammunition, geography, timing, and player-controlled forces.
- The AI need not imitate a human commander for the first playable release, but
  it must keep both sides acting, replanning, and producing understandable
  pressure without requiring operator scripts.

### Objectives and end state

- A campaign requires durable objectives, measurable progress, victory/failure
  predicates, and an explicit ended state.
- The first ruleset may be small, but it must connect losses, territorial or
  operational control, logistics, and time to player-visible consequences.
- Tactical missions cannot independently award campaign victory or mutate a
  competing objective timeline. They publish fenced outcomes to the campaign.

## Tactical entry and return

### Entry

1. The player selects a task force or threatened location.
2. The campaign shows the proposed interaction island, local capacity,
   unsupported fields/platforms, and assignment state.
3. The service fences the island at a campaign tick and issues a renewable
   authority epoch.
4. Sea Power loads the task force, interaction entities, filtered tracks, map
   projections, portable state, and terrain.
5. The player receives control only after identity, placement, and initial
   checkpoint validation succeed.

### Return

1. Normal exit, explicit disengagement, or policy demotion quiesces new tactical
   actions.
2. The tactical process captures a final checkpoint and typed outcomes.
3. The service validates identity, epoch, revisions, causality, and supported
   state before committing once.
4. The campaign resumes the island from the accepted state while the Sea Power
   objects dematerialize or become non-authoritative presentation objects.
5. The player sees a result summary, rejected/unsupported fields, losses,
   expenditure, objective effects, and the new campaign time.

## Degraded behavior

| Condition | Required player-visible behavior |
| --- | --- |
| Campaign service unavailable | Tactical session reports disconnected and cannot publish new campaign truth; reconnect or safe local exit is offered |
| Sea Power/plugin unavailable | Campaign continues Operationally where safe; tactical entry is disabled with a compatibility reason |
| Terrain not ready | Symbol remains selectable; physical realization reports loading and retries without underground placement |
| Unsupported platform/state | Map visibility remains; tactical control/materialization is blocked or degraded with the exact unsupported contract |
| Tactical process lost | Lease enters grace/recovery, stale epoch is fenced, and last accepted checkpoint/fallback is reported |
| Capacity exhausted | Map and orders remain available; lower-priority entities stay Operational and the UI explains the queue/degradation |
| Catalog/game mismatch | No mutation occurs; campaign data remains intact and the required compatible version is shown |

## Minimum playable proof of concept

The POC is playable only when one clean run demonstrates all of the following
while the full dynamic campaign dataset is loaded and advancing around a bounded
tactical island:

1. A fresh 4,096-entity campaign can be created, loaded paused, explicitly
   started, saved, and restored.
2. The player campaign map exposes the full authorized friendly order of battle
   and only coalition-detected hostile tracks, with usable clustering at world
   scale.
3. Sea Power loads the campaign-owned player task force as native simulated
   objects rather than the static authored placeholder force.
4. The tactical display shows proper native symbology for local units,
   non-local friendlies, and hostile tracks. All are selectable with equivalent
   feedback; there are no placeholder X icons.
5. Selecting a non-local friendly produces a terrain-safe visual proxy within a
   declared latency bound. Selecting a hostile produces only a filtered contact
   representation. Neither proxy simulates.
6. The first supported island contains a surface task force, a submarine,
   fixed-wing and rotary-wing support, one campaign-owned enemy, and at least
   one terrain-backed target where the scenario permits it. A family that is
   not yet portable remains visible on the map and is reported before tactical
   activation.
7. A player-controlled tactical session remains authoritative through a
   15-minute unpaused engagement while unrelated campaign entities and both
   strategic AIs continue advancing.
8. A campaign-owned hostile is damaged or destroyed in Sea Power and that fate,
   expenditure, and relevant player-unit state commit exactly once to the
   campaign.
9. Normal exit returns to the campaign cleanly. A separate forced game restart
   recovers without duplicate objects, split authority, or silent state loss.
10. The same bounded scenario is compared with fully native Sea Power behavior
    for fixed timestep, internal time dilation, contacts, movement, sensors,
    weapons, damage, and player-visible interaction. Differences are recorded,
    not hidden behind aggregate FPS.
11. Performance captures distinguish native simulated objects, map records,
    visual proxies, and external campaign entities. The dynamic 4K design stays
    within its declared local object and memory budgets.

Passing only service tests, rendering map symbols, or loading the plugin is not
a playable POC.

## First playable release boundary

The first playable release is local single-player. The local campaign
application owns strategy, orders, saves, time controls, and tactical entry.
Sea Power owns the battle and tactical map. The release includes one authorable
ruleset, a coalition-filtered campaign view, persistent player task forces,
strategic and operational AI, objectives, orders, tactical entry and return,
recovery, and explicit unsupported-state feedback.

It does not require:

- private or public multiplayer;
- promotable hot Unity replicas;
- several simultaneous Sea Power processes;
- detailed ground-versus-ground tactical simulation across the whole theater;
- every installed or modded platform family;
- a cloud service, container stack, external database, or message broker; or
- fully automated balance or human-equivalent strategic AI.

Private multiplayer remains on the active roadmap immediately after the local
loop. Public hostile-client play, distributed simulation, hot replicas, and
live tactical migration require later qualification. Those features may follow
only when they preserve this contract.

## Change control

Changes to this contract require updating:

1. the affected normative architecture contract;
2. [current-status.md](current-status.md), including implementation gaps;
3. [implementation-plan.md](implementation-plan.md), including acceptance gates;
4. [supported-claims.md](supported-claims.md) when a behavior becomes proven;
   and
5. the repeatable test procedure and retained run artifacts for any new live
   claim.

