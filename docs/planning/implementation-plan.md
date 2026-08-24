# Implementation plan

## Goal

Build one complete local campaign loop before expanding infrastructure:

```text
create or load campaign
  -> inspect the war and issue orders
  -> enter Sea Power with a campaign-owned task force
  -> fight while the rest of the war advances
  -> commit the result once
  -> return, save, quit, and recover
```

The proving dataset contains 4,096 campaign entities. The first tactical proof
uses a bounded interaction island. It does not place the full dataset in Unity.

Private multiplayer follows the local loop. Distributed Operational workers,
hot replicas, and live tactical migration are [future work](../future/distributed-simulation.md).

## Definition of done

A milestone is complete only when:

- its player-visible flow works from a fresh start;
- campaign identity, authority, and fog-of-war rules hold;
- expected failures produce clear recovery or rejection states;
- broad automated tests cover the durable service path;
- Sea Power behavior is checked live where Unity is involved; and
- current status and supported claims are updated.

An endpoint, DTO, process launch, screenshot, or isolated map symbol is not a
completed feature.

## Milestone 1: make the campaign application usable

**Status:** The core acceptance gate passed locally on 24 August 2026. The
browser shell created and named the 4,096-entity campaign paused, exposed the
coalition-filtered map and friendly hierarchy, started and paused time, saved,
restarted the service, renewed its player session, and restored the same head.
Packaging and richer degraded or incompatible-version presentation remain
cross-cutting release work.

Deliver the local product shell around the existing service and monitor.

### Player flow

- Create, name, load, start, pause, resume, save, and restore a campaign.
- Start new and restored campaigns paused.
- Show campaign time, status, coalition, ruleset, scenario version, and save
  compatibility.
- Present a coalition-filtered world map and friendly force hierarchy.
- Distinguish known contacts from enemy true state.
- Show connection, degraded, recovering, and incompatible-version states.

### Service work

- Separate player, operator, and companion capabilities.
- Close the HTTP content-type authorization bypass.
- Give hosted browser writes an authenticated path.
- Persist player coalition, preferences, and last campaign cursor.
- Keep operator true-state diagnostics separate from the player projection.

### Acceptance gate

A clean local install can create the 4,096-entity scenario, inspect it without
time advancing, explicitly start it, save it, restart the application, and
restore the same campaign head.

The broad service integration story covers this gate. A browser run separately
confirmed the rendered map, hierarchy, selection, controls, and restart state.
The same change was followed by a full Sea Power smoke on the pinned game build;
that smoke qualifies Runtime Bridge compatibility, not campaign tactical entry.

## Milestone 2: make the war respond to the player

**Status:** The first command slice passed locally on 24 August 2026. A player
can select a movable friendly group, issue a durable Transit order to a visible
region, observe movement and objective assignment, replace the order with Hold,
and retain that exact order through save and service restart. Coalition scope,
expected revisions, idempotent retry, altered-key conflict, foreign-group
rejection, and strategic-AI non-interference are covered by the broad service
story using the full 4,096-entity seed. The rendered browser flow was also
checked. The milestone remains open for the broader command set, task-force and
policy controls, objective progress, and campaign end state.

Add canonical coalition-scoped commands and one understandable campaign
ruleset.

### Commands

- Implemented: move, hold, cancel-to-hold, accepted or duplicate response,
  authorization and revision conflicts, durable idempotency keys, and expected
  revisions.
- Remaining: patrol, escort or support, engage, withdraw, explicit superseded
  response, task-force membership, readiness, EMCON, and ROE.

### Campaign rules

- Add durable objectives, progress, victory, failure, and campaign-ended state.
- Connect losses, control, logistics, readiness, and time to visible results.
- Explain AI objectives and current operational packages at a useful level.
- Keep both coalitions acting from their own intelligence.
- Prevent strategic AI from moving fixed defenses or taking player-owned units
  outside the selected control policy.

### Acceptance gate

The player can redirect one campaign-owned force, observe the accepted order and
its effects over time, cancel or replace it, and reach a durable objective state
without operator endpoints or scripts.

The redirect and cancel/replace portions pass. Destination-based objective
assignment is visible, but durable objective progress and an end state do not
yet pass.

This narrow command slice is sufficient to start Milestone 3. Finish one
campaign-to-battle loop before widening the order surface.

## Milestone 3: enter a campaign-owned battle

**Status:** The first entry slice passed locally on 24 August 2026. The campaign
application checks the selected friendly group against the connected runtime,
shows per-member support and available capacity, rejects foreign, stale, or
incomplete requests, and activates the supported group through the existing
fenced handoff path. The broad 4,096-entity service story now proves atomic
two-member preparation. Live Sea Power qualification loaded USS Yorktown and
USS Spruance together with campaign names, stable tags, and positions intact. A
second path loaded USS Honolulu as a native Los Angeles-class submarine while
the campaign continued advancing. A third path atomically loaded native SH-2F
and SH-3H campaign helicopters at about 500 feet while the campaign advanced.
The bridge now also registers campaign-tagged helicopter inventory on a native
ship flight deck and carries the same identity through Sea Power's real launch
cycle. The full campaign now supplies canonical host-child assignments: the
North Sea group enters as two native ships plus an SH-2F and SH-3H in
Yorktown's native flight-deck inventory. The milestone remains open for combined
surface/submarine entry, hosted-aircraft recovery, removal of authored
placeholder membership, tactical-map interaction parity, and hostile
participation.

Replace the authored placeholder force with campaign membership and make the
map/proxy system usable.

### Tactical preparation

- Implemented for a selected group: derive membership from the campaign save,
  require the whole surviving group to be supported, and prepare it atomically.
- Implemented: qualify a two-member mixed surface-class task force, a separate
  Los Angeles-class submarine patrol, and a separate two-member airborne
  SH-2F/SH-3H group.
- Implemented: canonical ship-hosted SH-2F/SH-3H inventory in player entry,
  world-object capacity accounting, Flight Deck UI inspection, native launch
  scheduling, and campaign identity adoption after launch.
- Remaining: combine the entry paths, replace authored player membership, and
  implement hosted recovery and restart reconciliation.
- Represent every supported available member as a real simulated object or a
  supported native child inventory item.
- Include required hosted aircraft, helicopters, boats, and child assets when
  their portable-state contract is supported.
- Implemented: report unsupported members and capacity before activation.
- Compute and fence the full interaction island under one epoch.
- Validate terrain and initial placement before showing land objects.

### Tactical map

- Show all authorized friendlies and coalition-known hostile tracks.
- Use native Sea Power domain, posture, heading, color, label, and selection
  behavior for local and non-local records.
- Make every visible record clickable without a local `ObjectBase`.
- Resolve overlapping symbols through deterministic selection or a choice list.
- Show uncertainty, confidence, age, and stale state for contacts.
- Stream a terrain-safe friendly visual proxy after selection without granting
  it simulation authority.

### First supported island

Prove one mixed naval island with:

- a player surface task force;
- one submarine;
- fixed-wing and rotary-wing assets;
- a detected campaign-owned enemy; and
- nearby terrain-backed radar, low-end GBAD, or installation targets where the
  scenario supports them.

Broader catalog coverage continues after this vertical slice. The 4,096-entity
campaign remains loaded and advancing around the bounded island.

### Acceptance gate

From the campaign application, the player selects a task force, enters Sea
Power, sees the correct campaign-owned members, selects local units, non-local
friendlies, and hostile contacts with the same interaction model, and observes
no placeholder X symbols or underground objects.

## Milestone 4: return the battle to the campaign

Close the authority and state loop.

### Portable state

- Define extraction and application for identity, pose, motion, damage, fuel,
  stores, ammunition, sensors, formations, hosted assets, launched weapons, and
  destruction.
- Classify every field as portable, reconstructable, presentation-only, or
  unsupported.
- Reject activation when required state cannot be represented safely.
- Keep checkpoints bounded and versioned.

### Result commit

- Quiesce new tactical actions for normal exit.
- Capture a final checkpoint and typed outcomes.
- Validate epoch, expected revisions, causality, and content fingerprints.
- Commit hostile fate, player state, expenditure, and objective effects once.
- Show the player the result and every rejected or unsupported field.

### Recovery

- Qualify clean tactical exit, forced game termination, game relaunch, companion
  restart, and campaign-service restart.
- Measure detection and recovery latency.
- Prove that stale objects are hidden or removed and cannot publish.
- Prove no duplicate object and no second timeline appears.

### Acceptance gate

A campaign-owned hostile is damaged or destroyed in Sea Power. Its fate and the
player force's supported state commit once. Normal exit and a separate forced
restart both return to one durable campaign timeline.

## Milestone 5: qualify parity and the 4K design

Compare the external campaign model with Sea Power and enforce explicit budgets.

### Parity portfolio

Use small matched scenarios to compare:

- movement and fuel use;
- detection, classification, and track aging;
- weapon launch, flight, interception, hit, and damage;
- aircraft, helicopter, submarine, ship, ground-unit, and installation behavior;
- fixed timestep and internal time dilation; and
- player-visible controls and map interactions.

Record systematic differences as versioned calibration rules or explicit
limitations. Aggregate FPS alone is not parity.

### Performance portfolio

- Compare small purpose-built native scenarios with matching dynamic cases.
- Exercise 500, 1,000, 2,000, and 4,096 dynamic campaign sizes.
- Report campaign throughput, frame time, fixed timestep, time dilation, working
  set, tactical-entry latency, proxy latency, checkpoint size, and commit
  latency.
- Report native objects, map records, proxies, Operational entities, tracks,
  weapons, and terrain-backed objects separately.

### Durability portfolio

- Run at least 10,000 commits with snapshots, cold restart, backup, restore, and
  bounded storage growth.
- Prove one schema and one algorithm upgrade without rewriting the source save.
- Test malformed checkpoints, catalog mismatches, interrupted writes, and disk
  pressure.

### Acceptance gate

The 4,096-entity campaign meets declared simulation, memory, UI, entry, and
recovery budgets with a bounded Sea Power object set. Known parity differences
are visible and versioned.

## Milestone 6: broaden campaign content

Expand only through the same entry, play, return, and recovery contract.

- Aircraft and helicopter sortie generation, basing, turnaround, loadouts, and
  recovery.
- Subsurface tasking and contact behavior.
- Airbase defense with radar, high- and low-end GBAD, dispersal, and logistics.
- Strategic air-defense and ballistic-missile systems with persistent-residency
  policy where supported.
- Representative tanks, artillery, logistics, and low-end GBAD for abstract
  ground formations.
- Ports, supply sites, shore defenses, civilian traffic, and regional logistics.
- Versioned platform and scenario catalogs with compatibility diagnostics.

Each family needs map representation, proxy behavior, Tactical portability where
required, Operational behavior, AI use, terrain safety, and return semantics.

## Milestone 7: add private multiplayer

Multiplayer stays in the active roadmap but does not block the local proof.

- Add durable users, invitations, revocation, coalition roles, and control
  rights.
- Serve coalition-filtered projection snapshots and realtime deltas.
- Resume from durable campaign cursors after reconnect.
- Define shared task-force ownership and order-conflict policy.
- Add TLS, rate limits, audit records, and secret rotation for LAN or private
  internet hosting.
- Prove that clients cannot read opposing true state through APIs, errors,
  timing, logs, or presentation proxies.

The first multiplayer milestone can use one campaign service and one Sea Power
tactical process. Public adversarial multiplayer needs a separate threat model
and independently trusted adjudication.

## Work that must not block these milestones

Do not make the local player loop depend on:

- out-of-process Operational execution;
- hot Unity replicas or deterministic shadow convergence;
- automatic island migration;
- more than one live Sea Power process;
- PostgreSQL, Redis, a broker, containers, or cloud hosting; or
- exhaustive support for every installed platform before one complete mixed
  island works.

The removed experiments remain available at Git tag
`archive/distributed-workers-before-local-trim-2026-08-24`.

## Review questions

Use these questions at every milestone gate:

- Can a player complete the promised flow without operator tools?
- Does one component own each mutation at every moment?
- Can retries, reconnects, or stale epochs create a second effect?
- Does any projection or proxy reveal unauthorized enemy truth?
- Does every missing family or field fail visibly before authority transfer?
- Can terrain loading produce a visible underground or water-mismatched object?
- Can the player recover the same campaign after each process fails?
- Are performance results split by native objects, proxies, map records, and
  external entities?
- Does a test exercise the real product boundary, or only its scaffolding?
