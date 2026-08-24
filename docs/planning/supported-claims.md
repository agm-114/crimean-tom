# Supported claims

Authenticated `GET /supported-claims` mirrors this document at schema version 10.
Where either source is narrower, use the narrower statement and fix the drift.

The supported game build is Sea Power `0.8.2-23444+s358`. Runtime and catalog
fingerprints can fail tactical preparation and activation closed. A later game
update requires renewed qualification.

## Campaign and persistence

- The deterministic campaign kernel uses typed commands and events, canonical
  hashing, fixed logical time, and SQLite WAL persistence.
- Ordinary local play does not require Docker, an external database, a broker,
  or cloud storage.
- Online backup and side-directory restore verify journal and blob integrity.
- Operator pause rejects campaign time advances until resume.
- The local player application can create and name the fixed 4,096-entity
  proving scenario paused, start it, pause it, save it, restart the campaign
  service, and restore the same campaign head and time.
- The player can give a movable friendly group a coalition-scoped Transit order
  to a visible region, replace it with Hold, and see whether the write was
  accepted, duplicated, conflicted, or rejected. The commands use expected
  revisions and durable idempotency keys. They survive save and service restart,
  and strategic replanning leaves them under player control.
- The campaign view shows coalition objectives and counts groups whose current
  destination matches each objective. This is assignment visibility, not yet a
  complete objective-progress or victory system.
- Two coalitions can create strategic objectives, allocate capable groups, keep
  fixed installations in local defense, form bounded multi-role operational
  packages, and replan from coalition intelligence.
- Operational simulation covers movement, sustainment, intelligence, sensors,
  weapons, countermeasures, damage, weather effects, and withdrawal.

These are deterministic game systems, not a claim of human-equivalent command
or complete Sea Power parity.

## Authority and local synchronization

- A healthy companion heartbeat renews one tactical assignment lease while the
  campaign service advances unrelated entities.
- Stale assignment epochs are rejected. Expired authority can return to the
  accepted checkpoint or Operational fallback.
- The owned-surface proof of concept can publish changed readbacks for pose,
  telegraph, fuel or stores fractions, damage, and destruction during unpaused
  play.
- Ordered lifecycle mutations temporarily pause Sea Power and restore its prior
  play state.
- A `RenderProxy` cannot receive simulation authority or publish campaign
  outcomes.

This is a narrow surface contract. It does not cover complete magazines,
sensors, formations, hosted aircraft, helicopters, submarines, or every ground
and installation family.

## Map and presentation

- The local player application shows the authorized friendly map, force
  hierarchy, bases, and coalition-known contacts without serializing canonical
  enemy entities or operator diagnostics.
- A fresh authenticated Sea Power session binds an explicit player coalition.
- Its projection can include every non-destroyed friendly campaign entity and
  coalition-filtered hostile tracks before a tactical assignment exists.
- Projected records use relationship-colored, authored Sea Power domain and
  posture symbols instead of generic drawing-marker X symbols.
- Typed friendly selection can relocate the tactical terrain window and load a
  presentation-only model from installed content.
- Selecting a hostile track creates a generic estimate proxy and does not load
  the hostile catalog model or reveal exact true state.
- Presentation proxies have no game unique ID, physics, sensors, weapons,
  velocity, campaign authority, or checkpoint rights.
- Terrain-gated ground and installation materialization waits for ready terrain
  and rejects invalid land or water placement.

Generic visual loading has only narrow live qualification. Direct mouse
selection parity, overlapping symbols, uncertainty glyphs, full catalog
coverage, and campaign-owned default task-force entry are not supported claims.

## Security and projections

- Loopback companion and operator writes require bearer authentication.
- The process-lifetime browser cookie is scoped to `/player` and cannot
  authorize operator reads or writes. A fresh local monitor session can renew
  it after campaign-service restart.
- An `application/grpc` content type does not bypass authentication on the
  ordinary HTTP listener; only the dedicated HTTP/2 gRPC listener enters the
  gRPC pipeline.
- Coalition-filtered HTTP projections omit opposing true-state entities.
- Assignment preparation and activation reject a runtime or catalog fingerprint
  mismatch against the assignment requirement.

The current design is trusted local or private cooperative play. It does not
claim a safe public hostile-client boundary.

## Explicitly unsupported

- A complete player campaign-to-tactical loop.
- Player patrol, escort, engage, withdraw, task-force editing, readiness, EMCON,
  ROE, complete objective progress, and a campaign end state.
- A tactical loss against a campaign-owned hostile committed back to the
  campaign with complete expenditure and portable state.
- Campaign-owned mixed task-force entry across surface, submarine, fixed-wing,
  helicopter, ground, and installation families.
- Live game restart recovery with a measured latency and duplicate-object proof.
- Schema or algorithm upgrade of an existing save.
- A bounded long-running campaign storage and performance envelope.
- Durable private multiplayer identity, authorization, and realtime delivery.
- Public adversarial multiplayer or independently trusted adjudication.
- Out-of-process Operational tick execution.
- Hot replica failover or live migration between Sea Power processes.
- Human-equivalent strategic AI, route and capacity deconfliction, deception, or
  learned opponent response.

## Qualification rule

Broad integration tests support deterministic service claims. Player-visible
Sea Power claims also require a live run on the pinned game build. Store raw
benchmark output and screenshots with the run artifacts or release record. Do
not create a parallel dated evidence documentation tree.
