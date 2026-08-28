# Supported claims

Authenticated `GET /supported-claims` mirrors this document at schema version 21.
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
- One campaign quantum is one simulated second. The default local target tempo
  is 10x, and the player API declares the tick contract and multiplier.
- The local player application can create and name the authored campaign
  paused, start it, pause it, save it, restart the campaign
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
- The full campaign service story can add one coalition-known hostile surface
  contact to the North Sea interaction island. The player sees a stable contact
  track and estimated classification rather than the hostile's canonical ID,
  name, or catalog profile. The trusted companion assignment carries the exact
  identity and resolves an authored task force on the opposing Sea Power side.
- Two coalitions can create strategic objectives, allocate capable groups, keep
  fixed installations in local defense, form bounded multi-role operational
  packages, and replan from coalition intelligence.
- Operational simulation covers movement, sustainment, intelligence, sensors,
  weapons, countermeasures, damage, weather effects, and withdrawal.

These are deterministic campaign systems, not full-fidelity Sea Power
simulation. They do not yet reproduce native event timing, navigation,
collision, sensor propagation and modes, track management, electronic warfare,
weapon guidance, component damage, aviation cycles, acoustics, communications,
or tactical AI. Current server memory and throughput results measure this
incomplete mechanic set and must not be presented as the cost of full parity.
The target and gap inventory are in
[full-fidelity server simulation](../architecture/full-fidelity-server-simulation.md).

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
- The worker projection retains the latest released assignment per entity as a
  positive reclamation receipt. Omission from a projection can suspend stale
  local authority but cannot destroy a checkpointed native object.
- The owned ship/submarine proof of concept can publish changed readbacks for pose,
  telegraph, stores fractions, damage, and destruction during unpaused play.
  Current Sea Power vessel profiles expose no native fuel capacity, so a vessel
  checkpoint leaves campaign-owned fuel unchanged instead of inventing a native
  fuel reading.
- The broad companion integration path publishes an aircraft's native fuel
  fraction directly on state sync and preserves the campaign baseline when Sea
  Power has no fuel reading. This prevents partial aircraft fuel from being
  multiplied by its handoff value a second time. A partial-fuel live Sea Power
  run remains open.
- Owned surface, submarine, and airborne materialization now restores the
  campaign's tracked aggregate damage fraction natively instead of always
  entering Sea Power undamaged. Sea Power recomputes its own aggregate damage
  every tick from each system's remaining integrity rather than exposing a
  settable total, so the bridge distributes the requested fraction across
  every native system and keeps the derived total consistent for an immediate
  readback. A live run materialized a Knox at a requested 40% damage fraction;
  Sea Power's own recomputed damage fraction read back 0.4 both immediately
  and after further unpaused native ticks, and the object remained under
  native control rather than destroyed or disabled. Hit location, warhead
  effects, flooding, fire, and component-specific failure remain unsupported;
  this closes only the aggregate-damage handoff gap.
- The campaign tracks ammunition as rounds in named magazines rather than one
  fungible munitions pool. Capacities come from Sea Power's own loadout rule,
  reimplemented from `SimpleObjectParameters.ReadAmmoFromWeaponSystem` and
  `ReadAmmoFromHardpointSystem`: a magazine contributes its declared
  `Ammunition{n}_Count`, a magazine shared by several mounts is counted once, a
  launcher without a magazine contributes `NumberOfContainers` multiplied by its
  system's `NumberOfAttachments` from `systems/weapons.ini`, a hardpoint station
  contributes one round or one per named attachment position, and a gun or CIWS
  mount without a magazine contributes nothing. Read against the installed
  build, a Knox reports 18 ASROC, 22 MK46, 8 RIM-7, and 600 127 mm rounds, a
  Ticonderoga reports 68 RIM-66C and 8 Harpoon, and an SH-2F ASW loadout reports
  2 torpedoes with 11 passive and 4 active sonobuoys. Weapon selection, point
  defense, and the Winchester withdrawal rule now consume and test those named
  magazines, so an exhausted anti-ship magazine removes that weapon from
  selection while a gun with rounds remaining stays usable. This is a verified
  catalog-derived capacity and consumption rule, not a matched-run parity
  result. Guns and CIWS mounts that the game's rule leaves uncounted stay
  unlimited on the server rather than receiving an invented capacity.
- Weapon employment is gated by native firing cadence as well as stock. Reload
  time, salvo size, and mount count come from the weapon system rather than the
  ammunition, reimplemented from Sea Power's `weapons.ini` lookups with the
  unit's own weapon-system section allowed to override the shared definition.
  Read against the installed build, a Knox's Mk25 Sea Sparrow box reloads in
  1800 s, its two MK46 tubes share a 200 s cycle, a Ticonderoga's two Mk26
  launchers cycle in 7 s, its Harpoon canisters declare no reload at all, and
  its Phalanx fires 50 rounds a second from a 3000 rpm mount. A mount still
  reloading is removed from weapon selection and from point defense, so a box
  launcher fires once and then stays quiet instead of re-engaging every tick.
  One engagement now consumes the native salvo size, which replaces the
  invented four-round ballistic burst.
- Each mount reloads independently, keyed by the platform's own weapon-system
  section. A ship with two tubes fires both in quick succession and then waits a
  full cycle, rather than firing at an averaged rate that never bursts, and a
  mount that feeds several ammunition types is occupied for all of them while it
  reloads. This replaces the earlier approximation that divided one reload time
  by the mount count. It remains a catalog-derived cadence rule, not a
  matched-run parity result.
- Magazine and reload state now survives the tick boundary. The simulated-entity
  event previously carried neither, so rounds spent while resolving a tick were
  discarded when the event was applied and a platform could fire the same last
  round every tick indefinitely; the magazine and cadence rules above were
  computed but never took effect across ticks. Both are now carried as changes
  on the event and verified by a stepped engagement.
- Radar detection range comes from the radars a platform actually carries.
  Each unit's sensor sections resolve into `systems/sensors.ini`, and the
  longest declared `MaxRange` whose `Role` covers the target's class sets the
  horizon; ranges there are kilometres, unlike weapon ranges, which the game
  declares in nautical miles. Read against the installed build, a Knox searches
  for aircraft to 407 km through its SPS-40 but for ships only to 104 km, a
  Ticonderoga reaches 463 km with SPY-1A, and a Krivak reaches 300 km for
  aircraft and 46 km for ships. This replaces a table of constants that gave
  every warship 120 km against aircraft and 80 km against ships regardless of
  what it carried. Only radars are sourced this way: a sonar's detection
  distance comes out of an acoustic model rather than its nominal declared
  `ActiveRange`, and a visual sensor declares a multiplier rather than a range,
  so submerged contacts and platforms whose sensors do not resolve keep the
  approximate horizons. Those remain approximations, not measured tolerances.
  Scan timing, altitude limits, target channels, datalink reach, line of sight,
  and radar-cross-section dependence are still not modelled.
- Simultaneous engagements are bounded by native fire-control channels instead
  of fixed constants. A mount's guidance capacity is summed from the
  `WeaponChannels` of the sensors its weapon system is associated with, through
  the unit's own sensor sections into `systems/sensors.ini`; a Knox Mk25 draws 2
  channels from its MK-115 illuminator. Only weapons needing the shooter to keep
  illuminating or commanding them occupy a channel, so a fire-and-forget round
  is limited by reload rather than by channel count. This replaces a hardcoded
  limit of two guided rounds per shooter and a point-defense channel count that
  was clamped to between one and four distinct weapons. A director shared by
  several weapons is counted once rather than once per ammunition. Reaction
  time, doctrine, and per-target channel occupancy are still not modelled.
- Owned materialization can restore per-ammunition magazines natively through
  Sea Power's `setAmmunitionAmount`, and checkpoints capture the native
  `AmmunitionAmountDictionary`, so a partly expended loadout crosses the handoff
  boundary as exact rounds instead of an aggregate stores fraction. Requests
  naming ammunition the spawned native unit does not carry are rejected rather
  than dropped, and realized magazines are verified exactly with no tolerance.
  A live Sea Power run of this path has not been performed yet.
- Ordered lifecycle mutations temporarily pause Sea Power and restore its prior
  play state.
- A checkpointed bridge record that becomes detached after scene loss can fence
  at its unchanged revision without trying to pause a nonexistent tactical
  mission. A live USS Honolulu run left the Sea Power scene first, reused the
  current checkpoint, committed release, and reclaimed the detached ledger entry
  on companion reconnect without loading another tactical scene.
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
- A later full-campaign map selection promoted USS Honolulu from its non-local
  representation into a native `usn_ssn_los_angeles / Variant31 / Default`
  object. Readback reported native initialization, player orders and control,
  initialized stores, 52 rounds, no recovery flag, and one active owned record.
  Sea Power's map, Formation Manager, and selected-unit HUD preserved the
  campaign identity.
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
- A fresh live full-campaign run prepared the same four friendly North Sea
  members plus one coalition-confirmed Krivak contact. Sea Power placed Yorktown
  and Spruance in the player task force, both helicopters in Yorktown's native
  deck inventory, and the Krivak in the authored Enemy task force. The worker
  ledger held five owned resources and three world objects, with no duplicate
  stable IDs or remaining generic hostile proxy. Normal exit checkpointed and
  released all five assignments together. Sea Power held about 60 FPS, a
  16.667 ms fixed step, and internal time dilation 1.
- A later run applied all 4,096 authorized projection records in about 521 ms
  of frame-budgeted work with a 4.631 ms worst slice. Typed focus on USS
  Spruance centered Sea Power's full-screen tactical map on that native object.
  This qualifies initial replay and native focus, not direct mouse selection or
  reconnect repair.
- After viewport prefiltering, clustering, attachment preparation, detach, and
  attach became separate frame-budgeted phases, a fresh full-world pass examined
  3,711 candidates, attached 97 native-style markers, completed in 3.292 ms of
  aggregate work, and used a 0.404 ms worst slice. The process-wide cold maximum
  was 3.851 ms. The associated 4,096-record projection applied in 330.271 ms with
  a 1.584 ms worst slice. This is one machine and one map state, not a frame-time
  guarantee.
- The current 4,161-entity campaign entered its five-member North Sea tactical
  island and applied 4,157 remaining projection records in 16.296 ms. A pinned
  native fullscreen request produced Sea Power's clamped global zoom of 3.006.
  The viewport processed all 4,157 records, found 4,061 on-screen candidates,
  attached 183 clustered markers, and completed in 4.056 ms with a 4.223 ms
  worst slice. Sea Power reported about 60 FPS, a 16.667 ms fixed step, and
  internal time dilation 1. This qualifies the global clustering path on one
  machine. It does not qualify direct mouse selection or reconnect repair.
- The same Sea Power process entered and exited the five-member North Sea
  island, then entered and exited the one-member USS Honolulu island. The
  companion reclaimed five committed resources after the first exit and one
  after the second. The bridge reported zero owned, active, suspended,
  detached, or recovery-required records after each cleanup.
- A fresh 4,096-entity run left Sea Power before campaign return with USS
  Honolulu materialized. The bridge exposed the destroyed Unity wrapper as a
  detached, checkpointed record with native readiness and control false. Return
  completed at revision 1, and companion reconnect reclaimed the post-commit
  ledger entry without a tactical scene. The final owned-resource count was zero.

This is a narrow ship, single-profile submarine, two-profile airborne
helicopter, and two-profile hosted-helicopter entry contract. It does not cover
hosted-aircraft recovery, complete magazines, sensors, formations, fixed-wing
aircraft, submerged-state portability, broader submarine families, or every
ground and installation family. The opposing Krivak entry and clean supported-
field return are live-qualified; combat damage and complete expenditure return
are not.

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
