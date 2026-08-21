# Phase 5 live scheduler evidence — 21 August 2026

## Disposition

The first representative Fulda Gap scheduler-to-Sea-Power lifecycle is working.
The service selected a complete representable interaction island, queued the
handoff to the connected companion, materialized a real Knox in Sea Power,
activated Tactical authority only after the initial checkpoint, drained it
after durable interest-loss hysteresis, and later re-promoted the retained
checkpointed object under a higher authority epoch.

This is a vertical lifecycle gate, not the complete Phase 5 capacity or fidelity
gate. Weapon/guidance/host coupling, multi-profile islands, tactical load limits,
and resolver calibration remain.

## Runtime identity

- Sea Power `0.8.2-23444+s358`, process 38168
- runtime fingerprint `92f00eeb425ad14914e646705b5068f0225898e6b929ccb2715b2e6115794df5`
- bridge assembly `679f4b476c15d5ce7eeaa4cd082d51cf9196a5994dd7d88aab48541159856371`
- catalog `01d4650c0f0ddeb6a106b3e2ded8c79d27ebbcad51319e56470f45d7fa3183ea`
- player task force `taskforce:Taskforce1`
- load cache: 1,532 hits, one miss after the changed bridge deployment

The game was launched through Steam and accepted a fresh AnchorChain health
marker. Typed UI automation loaded
`scenario:missions/intro/01 red sea breakout 1988` and continued the briefing;
no menu coordinate clicks were used.

## Representative campaign

Campaign `37772898357347cd9c9c6fd724037d11` is a fresh deterministic Fulda Gap
bootstrap with 12 regions, 15 entities, and 9 task forces. USS Pharris is a
singleton GIUK surface picket using the adapter's exact
`usn_ff_knox_84 / Variant43 / Default` profile. USS Honolulu belongs to the
existing North Sea force, preserving both entity and task-force counts while
preventing an unsupported submarine from making the demo island impossible.

The hosted monitor is:

`http://127.0.0.1:5081/monitor/?campaign=37772898357347cd9c9c6fd724037d11`

The host issues its own HttpOnly browser session, so opening that URL does not
require copying the service token into the browser.

## Lifecycle observations

The first live promotion reached campaign commit 63. USS Pharris changed from
Operational to Tactical only after assignment epoch 1 materialized in Sea Power
and published its initial content-addressed checkpoint. The runtime world placed
the campaign-stable identity inside the exact player task force.

Two identical healthy requests with no observation then produced:

1. `scheduler-hysteresis-hold`, leaving the active island Tactical;
2. `authority-quiescing`, followed by a final checkpoint and release.

At commit 68 the campaign entity was Operational and the epoch-1 assignment was
Released. Runtime resource diagnostics reported one `suspended-retained` object,
one checkpoint, zero active objects, and zero recovery-required objects.

The first attempt to promote that retained identity exposed a live-only defect:
the bridge treated any retained record as a duplicate materialization. The
runtime adapter now recognizes only a checkpointed, healthy, suspended
predecessor with an exact entity/profile/task-force match and a strictly higher
authority epoch. It rebinds and enables that object for the new assignment; a
failure restores suspension instead of leaving ambiguous authority.

After deploying the corrected bridge, a fresh promotion/drain/re-promotion
sequence ended at campaign commit 85. Assignment epoch 5 is Active, USS Pharris
is Tactical, and the bridge reports one active retained constructed object,
checkpoint present, and no detached or recovery-required state. The live game
record is game unique ID 398.

## Four-island tactical-capacity probe

A separate 4x Fulda campaign,
`160bc4f63fdd4d6f84fb2bd136c255ac`, contains 60 operational entities and 36
task forces. Its four independent USS Pharris pickets were observed together
with tactical capacity set to four. On the same game/catalog/bridge fingerprint:

| Operation | HTTP scheduler return | All four terminal state | Result |
| --- | ---: | ---: | --- |
| Initial materialize + checkpoint | 2,555.7 ms | 4,020.2 ms | Four Active/Tactical at commit 177 |
| Two-sample drain + final checkpoint | 336.3 ms | 2,011.0 ms | Four Released/Operational at commit 191 |
| Retained re-promotion + checkpoint | 337.9 ms | 1,318.4 ms | Four epoch-2 Active/Tactical at commit 200 |

The initial game unique IDs were `365`, `455`, `545`, and `635`; the same four
IDs remained after drain and re-promotion. Runtime diagnostics ended with four
active retained objects, four checkpoints, and zero detached or
recovery-required objects. This supports a local tactical capacity of four for
independent Knox islands on this host. It does not yet establish tick-rate or
combat-load headroom, and it must not be generalized to mixed unit profiles.

The live capacity monitor is:

`http://127.0.0.1:5081/monitor/?campaign=160bc4f63fdd4d6f84fb2bd136c255ac`

## Scene-aware worker refresh

The companion now publishes its current game session, compatibility
fingerprints, player task force, and materialization profiles in each campaign
heartbeat. Service session `8a6e6fd197bc4fce9586b9f0145c1f61` remained
unchanged while typed automation exited tactical play to debrief and loaded Red
Sea Breakout again. The service projection cleared the player task force at
debrief, then reacquired `taskforce:Taskforce1` and the
`usn_ff_knox_84 / Variant43 / Default` profile after mission load without a
companion restart.

After that transition, fresh 4x campaign
`739f13a58fde41ff9563bae754213672` promoted four independent USS Pharris
islands. The scheduler request returned in 2,623.8 ms and all four epoch-1
assignments were Active/Tactical at commit 177. Sea Power reported four active
owned objects (unique IDs `405`, `495`, `585`, and `675`) and zero recovery
debt. The hosted monitor is:

`http://127.0.0.1:5081/monitor/?campaign=739f13a58fde41ff9563bae754213672`

## Active weapon/guidance fence

Fresh 4x campaign `d42468bb4c984e50bacecfd6f8f4cb57` proves the first
hard runtime coupling edge end to end. Four independent USS Pharris/Knox
islands reached Active/Tactical at commit 177. Typed experimental automation
paused the mission and launched an untargeted `usn_rgm-84c` from campaign unit
`18017b8e1dfd95defd2024f1ce9d6d73`; the bridge captured weapon `541` with
`ActiveRadarHoming` guidance. The compact coupling capture took 110 microseconds
with two active weapons in the mission.

The next heartbeat projected one unsafe canonical self-edge for the launching
platform. On two healthy interest-loss samples, the coupled island reported
`active-coupling-fence` both times while the other three moved from
`scheduler-hysteresis-hold` to `authority-quiescing`. At commit 188 the coupled
Knox remained epoch-1 Active/Tactical and the other three were
Released/Operational.

Browser verification of the hosted monitor found no Vite error overlay and
showed service healthy, scheduler stable at two samples, Tactical/Authority
counts of one, and the selected track fields `ENGAGED // FENCED` plus
`active-weapon:ActiveRadarHoming`:

`http://127.0.0.1:5081/monitor/?campaign=d42468bb4c984e50bacecfd6f8f4cb57`

The heartbeat cache deliberately retains a hard edge across one missing sample;
the broad protocol story proves the edge is removed after the second empty
sample and normal drain hysteresis resumes.

## Plotting and physical coupling graph

Schema-v2 coupling is deployed in Sea Power `0.8.2-23444+s358`. In Red Sea
Breakout the bridge observed stable formation plus Visual, SearchRadar, ESM,
and PassiveSonar plotting edges. Collapsing repeated native track components by
source, target, sensor type, and detection kind reduced the initial graph to 90
relationships with zero duplicate graph keys. Capture cost was 1,339
microseconds in that sample.

Fresh 4x Fulda campaign `454e01816c934979a5c6c7b930d43c34`
materialized four real `usn_ff_knox_84 / Variant43 / Default` hulls and reached
four Active/Tactical assignments at commit 178. Two independently grouped
campaign hulls were relocated head-on with the armed owned-object seam and run
for two seconds. Sea Power's native `ObjectCollisionAvoidance.UnitToAvoid`
reported reciprocal campaign-stable edges. The combined mission graph contained
132 stable relationships and captured in 3,059 microseconds.

The next heartbeat carried exactly two canonical `physical-dependency` edges
with `collision-avoidance`, `unsafeToSplit=true`, and `missingSamples=0`. An
interest-loss schedule at commit 179 joined the two independent groups into one
two-entity Tactical island with coupling score 1,000, reason
`active-coupling-fence`, and causal evidence
`physical-dependency:collision-avoidance`. The live monitor is:

`http://127.0.0.1:5081/monitor/?campaign=454e01816c934979a5c6c7b930d43c34`

The same broad protocol story now exercises active-weapon, plotting, and
physical relationships in one scheduler lifecycle. It remains the only
protocol integration test; no narrow coupling unit-test family was added.

## Catalog-correct global-war representative campaign

The earlier four-Knox scenes above are tactical-capacity probes, not a plausible
September 1989 GIUK order of battle. The representative tactical-window mission
now loads a catalog-valid local slice: USS Pharris FF-1094
(`usn_ff_knox_84 / Variant43`), USS Honolulu SSN-718
(`usn_ssn_los_angeles / Variant31`), Soviet Victor III K-244
(`wp_ssn_victor3 / Variant8`), and an Oscar I
(`wp_ssgn_oscar1 / Variant1`). Typed scenario automation loaded it without menu
coordinate clicks; the bridge read back four combat units, three task forces,
and the expected names, unit IDs and variants before pausing tactical time at
1.7385 seconds.

The matching final global campaign bootstrap is campaign
`6af7fef19f574feab7fe586b2141d637`, initially committed with 30 regions, 28
bases, 54 entities and 29 groups. Browser verification of its immediate
predecessor selected the same 54-entity content shape,
showed service healthy, rendered the world plot, and exposed all 54 entities in
the semantic coalition/formation/unit hierarchy. The order of battle includes
China, Japan, India, Pakistan, Australia, Iran, Israel and the wider European
catalog nations. Argentina is modelled as the requested Soviet-support logistics
partner using clearly labelled cargo-hull substitutions because Sea Power 0.8.2
contains no Argentine nation prefix.

The monitor's local Natural Earth country topology now draws borders only for
countries detected in that OOB: friendly outlines are green, hostile outlines
are red, and neutral-country borders remain absent. A live browser pass showed
the aligned outlines, 30 regions, 29 groups, 54 units, T+30, 170 events and no
console warnings or errors. The modern geometry is explicitly adapted into a
merged USSR outline; unified Germany remains the FRG outline because the dataset
cannot distinguish East Germany.

The final campaign was advanced through 30 deterministic operational quanta.
It produced 170 recent operational outcomes across the expanded theater with no
destroyed entities; maximum damage was 427,078 milliunits and mean damage was
86,049 milliunits. This makes the monitor a live operational picture rather than
an inert order-of-battle catalog while keeping every force below destruction.

The live monitor is:

`http://127.0.0.1:5081/monitor/?campaign=6af7fef19f574feab7fe586b2141d637`

## Catalog-derived engagement envelopes and worker container

Campaign `e7ae56ae91be4138abf4af5c03fee6cd` is the schema-v5
representative pass. Bootstrap committed 32 regions, 31 bases, 60 entities and
34 groups. The installed Sea Power catalog resolver found 338 operational
target/range capabilities across 55 entities. Representative snapshots included
USS Pharris with RGM-84C at 120,380 m, the Victor III with SS-N-16 and torpedoes
up to 100,008 m, RoCAF F-5E CAS with AGM-65B at 31,484 m, ROKAF F-5E with
AIM-9P at 14,816 m, and KPAF J-7C with its date-selected 1989 PL-5B at 15,927 m.

Advancing through T+30 produced 97 recent outcomes and nine weapon expenditures.
The new Korea and Taiwan fronts fired PL-5B, AIM-9P, AGM-65B and 100 mm naval
gun rounds. Every recorded detail included the rounded great-circle distance and
the selected catalog maximum. Automated inspection found zero violations; the
largest ratio was 0.929. Unknown catalog data remains unable to fire rather than
falling back to a theater-wide guessed range.

Typed UI automation then loaded
`scenario:missions/user_missions/crimean tom - tactical worker container`.
The live mission paused at 2.013 seconds with three task-force records, one
stationary player-controlled Adjutant-class survey anchor, no enemy units, one
session-surrogate identity, zero campaign identities and no active weapons. The
mission defines no objectives or victory conditions. Its sole trigger displays
the external-authority notice and cannot create a campaign outcome. This proves
the first loadable non-resolving shell for future fenced island materialization.

The same schema-v5 campaign then completed the shell's first full scheduler
lifecycle. USS Pharris reached assignment epoch 1 Active/Tactical at commit 242;
the bridge reported one materialized and one active campaign object alongside
the process-local anchor. After two identical healthy interest-loss samples,
the first returned `scheduler-hysteresis-hold` and the second returned
`active-island-outside-player-interest`. Commit 248 left Pharris
Released/Operational with zero active campaign objects, one retained suspended
object and zero recovery-required objects. The container therefore provides
real promotion and demotion without resolving a local scenario or fabricating a
campaign result.

## Exact native-weapon tactical load sample

The typed experimental launch request now accepts an exact installed
ammunition ID as well as Sea Power's native ammunition type. This closed a probe
defect where requesting only broad type `Missile` could select RGM-84C for a
submarine target. The bridge does not add its own target or range table: exact
selection still invokes the native weapon system, and it verifies the launched
round's ammunition ID before returning success.

The catalog-valid GIUK window was adjusted to place Victor III K-244 five
nautical miles from USS Pharris. On a fresh deployed runtime, typed automation
launched exactly `usn_rur-5` / `ASROC` from `unit:1` to `unit:94`. Sea Power
registered `weapon:5` with launch platform Pharris, intended target K-244,
primary target type `ASW`, and flight stage `ASROCFlight`. ASROC is a live native
world object but is not present in Sea Power's missile/torpedo/bomb save lists,
so the response explicitly reported `nativeSaveRegistered=false` rather than
claiming save/load coverage.

With that exact ASROC active, a controlled 900-tick batch completed 30.0
simulation seconds in 15.114 wall seconds: 59.5 ticks per wall second, 1.98x
real time, 900 game updates and 450 physics updates. Compact coupling capture
took 203 microseconds and retained the exact Pharris-to-Victor active-weapon
edge. A following 1,800-tick/60-second batch held 59.8 ticks per wall second and
1.99x real time. These are valid single-ASROC tactical-load observations, not a
calibration claim for operational hit probability or damage.

The live monitor for the schema-v5 campaign is:

`http://127.0.0.1:5081/monitor/?campaign=e7ae56ae91be4138abf4af5c03fee6cd`

## Schema-v6 multi-container ownership foundation

Schema v6 persists the stable worker client instance ID on each new assignment.
The service now keeps multiple campaign companion sessions concurrently, routes
assignments and authority fences to the recorded owner, rejects inbound
assignment traffic from a non-owner, and fans committed events out to every
campaign worker. A backpressured recipient is disconnected for journal catch-up
instead of stalling an already-durable canonical commit.

The single broad protocol story connected a second authenticated gRPC worker to
the same campaign while the real game companion remained selected and live. The
health projection reported two sessions; closing the probe returned it to one
without replacing the primary session. The complete 42-second story then passed
promotion, checkpoint, batch activation, coupling, quiesce, restart catch-up,
fallback and typed UI automation as before.

Fresh representative campaign `ec4fb4931d2a46589092530202af3ad8`
bootstrapped 32 regions, 31 bases, 60 entities and 34 groups under schema and
algorithm version 6. Its 30-quanta advance reached commit 186 with 107 recent
outcomes. The existing Sea Power process reconnected through one campaign
worker. The hosted monitor reports the worker count and aggregate materialized,
active and recovery-required resources, and rendered without browser warnings
or errors at:

`http://127.0.0.1:5081/monitor/?campaign=ec4fb4931d2a46589092530202af3ad8`

This is session coexistence, placement affinity, event fan-out and publisher
fencing. It is not yet evidence for destination-headroom admission, live
checkpoint migration, rebalance cooldown or migration-storm suppression.

## Portable checkpoint transfer plumbing

A subsequent implementation slice now attaches the latest durable tactical
checkpoint to a higher-epoch assignment. The destination companion downloads
the content-addressed blob through the authenticated service endpoint, verifies
the blob SHA-256, and supplies the decoded checkpoint to materialization. The
runtime bridge separately rejects stale/same-assignment epochs, mismatched
entity/profile/task-force identity, inactive surface state, and a mismatched
internal checkpoint hash before it creates a new owned object.

The full solution built with zero warnings, and the two existing broad stories
passed: the deterministic campaign story in 5 seconds and the real-host protocol
story in 42 seconds. Those checks cover the transfer path's contracts and
service/companion integration; they are not a substitute for a live source and
destination Sea Power process proof. Only position, heading, speed, and
telegraph are currently applied during reconstruction. Fuel, stores, damage,
sensors, formations, and hosted aircraft remain explicit unsupported fields.

A live cold-reconstruction proof then used fresh global-war campaign
`08eed6175b1a4e1abf50004d4e5808c8`. Its 32-region, 60-entity seed advanced to
T+30 before USS Pharris entered epoch 1 with 146,715 canonical damage. The
assignment materialized and drained normally; a bug found in that first live
attempt had allowed the pristine tactical hull to overwrite campaign damage.
The assignment contract now carries the canonical damage baseline and adds only
observed tactical damage. The broad protocol story's existing 250,000-damage
assertion passed after the fix, and the live drain preserved 146,715 exactly.

Sea Power was then fully restarted. The new process reported zero owned records
before promotion, proving that retained-object reuse was impossible. Epoch 2
assignment `9f06359e64656f01be9a0812a21de255` carried content reference
`df7f9f6a97facc9b08b2d888efd3e41c44320b597fee2326500421aa3983f4a7`.
The companion downloaded it and the bridge constructed game object 126 at
latitude 62.7, longitude -15, heading 0 and speed 0. Campaign commit 242 showed
one Active/Tactical object, no retained object, no recovery debt, and unchanged
canonical damage 146,715.

This is live process-to-process cold checkpoint reconstruction for the supported
surface subset, not simultaneous two-container migration. The reconstructed
Unity hull remains pristine (`damageFraction=0`) because damage application is
unsupported even though canonical damage is preserved externally. It must not
receive gameplay authority where that mismatch matters. The same restriction
continues to cover fuel, stores, sensors, formations and hosted aircraft.

## Destination-local admission envelope

The companion protocol now advertises tactical object capacity and reserved
headroom on both initial hello and recurring heartbeat. The coordinator rejects
invalid declarations by assigning zero capacity. New complete islands are
admitted only when their planned destination remains within the smaller of the
campaign scheduler budget and that worker's declared `capacity - reserve`.
Occupied space is the larger of the worker's observed materialized count and its
non-released assignment count, so a lagging resource heartbeat cannot silently
overbook the process.

The development defaults are capacity four and reserve one. Four is supported
only by the independent-surface live probe above; reserving one leaves an
unclaimed slot for catch-up or later migration work. This is an implemented
admission policy, not evidence that three mixed active-combat entities are safe.
The existing broad protocol story verifies the default envelope through the
real companion/service handshake and campaign diagnostic view. The monitor now
reports aggregate capacity, reserve and free admission headroom.

The real campaign companion then reconnected to
`08eed6175b1a4e1abf50004d4e5808c8` and advertised capacity four with reserve
one while owning one materialized, active Pharris. The active-campaign endpoint
selected that worker-backed journal. In the in-app browser, both `/monitor/it`
and the older `ec4fb4931d2a46589092530202af3ad8` deep link redirected to the
current campaign. The rendered status showed `CAP 4`, `RESERVE 1`, `FREE 2`, 60
units and no browser errors; only Babylon's normal WebGL initialization log was
present.

The follow-on admission projection removed that UI-side `FREE` estimate. With
the campaign-safe budget still at one, the same live worker reported one
occupied object, zero ordinary-promotion headroom and one reserve-protected
migration-overlap slot. The monitor rendered `LOAD 1/4`, `RESERVE 1`, `PROMO 0`
and `MIG 1` directly from the service, and `/monitor/it` again canonicalized to
the active campaign without browser warnings or errors. `MIG 1` proves the
reserved physical envelope is accounted for; it does not claim that the current
one-Primary state machine can spend that slot yet.

## Remaining gate

- Expand the first exact-ASROC tactical throughput sample to mixed weapons,
  sensors and multiple simultaneous islands before setting a capacity ceiling.
- Add safe profiles for more naval/air units before claiming mixed-island
  coverage.
- Exercise campaign-owned sensor, host, and temporary-refueling edges, then
  test split hysteresis live.
- Calibrate operational outcomes against repeatable Sea Power observation
  batches rather than treating this one mission as combat-model validation.
- Add a durable non-publishing destination-preparation state, spend the now
  explicit overlap reserve under bounded concurrency, and fence whole-island
  migration,
  then prove this checkpoint reconstruction path between two real tactical
  containers. Do not enable automatic migration for unsupported state families.

## Migration orchestration admission and cooldown

The broad protocol story now proves bounded migrate-tactical-island orchestration
admission and durable campaign-scoped post-commit cooldown. A second migrate call
while staging is in flight is rejected with `migration-orchestration-busy` before
any authority mutation. After a successful `TacticalIslandMigratedEvent`, a
later non-idempotent migration for another ready island in the same campaign is
rejected with `migration-cooldown-active` before staging or authority mutation;
the rejection survives coordinator restart because cooldown is reconstructed from
journal history, not a process-local clock. Unrelated `FinalCheckpointStaged`
failures for another prepared island do not release an in-flight migration lease.
Already-migrated activation-grant replay remains allowed. Cooldown duration is configurable through
`CRIMEAN_TOM_MIGRATION_COOLDOWN_QUANTA` (default 30 quanta).
