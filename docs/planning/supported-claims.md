# Supported claims

Authenticated `GET /supported-claims` mirrors this document at schema version 17.
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
- The campaign application can inspect a selected friendly group for tactical
  entry, including connected-session state, usable capacity, and exact runtime
  profile support for each surviving member. Entry requires the reviewed group
  revision and an idempotency key, rejects foreign or incomplete groups, and
  prepares the whole supported group before activation.
- Two coalitions can create strategic objectives, allocate capable groups, keep
  fixed installations in local defense, form bounded multi-role operational
  packages, and replan from coalition intelligence.
- Operational simulation covers movement, sustainment, intelligence, sensors,
  weapons, countermeasures, damage, weather effects, and withdrawal.

These are deterministic game systems, not a claim of human-equivalent command
or complete Sea Power parity.

## Authority and local synchronization

- An eligible companion heartbeat keeps a tactical assignment lease live while
  the campaign service advances unrelated entities. Eight seconds without an
  eligible heartbeat starts a 12-second wall-clock grace period. The assignment
  remains fenced until reconnect or grace expiry.
- Stale assignment epochs are rejected. Expired authority can return to the
  accepted checkpoint or Operational fallback.
- A player can request normal exit for one complete interaction island. The
  service queues final-checkpoint fences together, releases the island only
  after every member responds, and returns the committed member result plus the
  fields that are still unsupported.
- The owned ship/submarine proof of concept can publish changed readbacks for pose,
  telegraph, fuel or stores fractions, damage, and destruction during unpaused
  play.
- Ordered lifecycle mutations temporarily pause Sea Power and restore its prior
  play state.
- A `RenderProxy` cannot receive simulation authority or publish campaign
  outcomes.
- Live qualification on the pinned build atomically loaded the selected
  two-member NATO North Sea surface action group as campaign-owned USS Yorktown
  and USS Spruance objects in the native player task force. Both exact profiles
  prepared before either assignment became tactical. Sea Power preserved their
  campaign names, stable tags, and North Sea positions with no duplicate stable
  IDs. The earlier Knox run also showed unrelated campaign state advancing
  while its assignment lease stayed live.
- A separate live 4,096-entity run loaded campaign-owned USS Honolulu as a
  native Los Angeles-class submarine with its exact `Variant31` profile,
  campaign name, stable tag, player task-force membership, and GIUK coordinate.
  The assignment remained Active while campaign time advanced from T+0 to T+30.
- A live 4,096-entity run atomically loaded a two-member maritime helicopter
  group as native SH-2F and SH-3H objects. Sea Power preserved the exact
  `Squadron4 / ASW` and `Squadron1 / ASW` profiles, campaign names, stable tags,
  player task-force membership, and roughly 500-foot entry altitude. Both
  assignments remained Active while campaign time advanced from T+0 to T+40.
- A separate live bridge-level run registered a campaign-tagged SH-2F in a
  bridge-owned Ticonderoga's native flight-deck inventory, enforced native deck
  capacity, created Sea Power's real launch task, and adopted the launched
  helicopter under the same campaign identity and host relation. The airborne
  object retained the requested `Squadron4 / ASW` profile and full native fuel.
- A live full-campaign run entered the canonical NATO North Sea group as USS
  Yorktown and USS Spruance plus Yorktown's hosted SH-2F and SH-3H. Sea Power
  created two ship objects and registered the two helicopters in Yorktown's
  native flight-deck inventory. All four assignments remained active while the
  campaign advanced past T+3500. The worker reported two world objects and four
  owned campaign resources, with no duplicate stable IDs. The game held about
  60 FPS, a 16.667 ms fixed step, and internal time dilation 1.
- A fresh live full-campaign run launched that canonical onboard SH-2F through
  Sea Power's native flight-deck cycle. The airborne object retained its stable
  campaign identity, name, `Squadron4 / ASW` profile, host relation, ammunition,
  and full 1,214 kg fuel load. Campaign readback reported 1,000,000 fuel
  milliunits, the assignment remained Active with a live lease, and the world
  contained no duplicate stable IDs. The game returned from a bounded x10 deck
  wait to x1 at about 60 FPS, a 16.667 ms fixed step, and internal time dilation
  1.

This is a narrow ship, single-profile submarine, two-profile airborne
helicopter, and two-profile hosted-helicopter entry contract. It does not cover
hosted-aircraft recovery, complete magazines, sensors, formations, fixed-wing
aircraft, submerged-state portability, broader submarine families, or every
ground and installation family.

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
coverage, combined-arms campaign-owned entry, and replacement of authored
mission membership are not supported claims.

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
- Campaign-owned combined task-force entry across surface, submarine, fixed-wing,
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
