# Current implementation handoff

## Repository state

This handoff records the state through 21 August 2026. Phases 0 through 4 are
complete and Phase 5 is active. Durable campaign authority, unattended simulation,
and the first canonical tactical handoff loop now run
outside Unity. The
game-independent kernel can now validate typed commands, produce and apply
ordered events, and hash immutable canonical state. Its SQLite WAL journal now
requires a live compare-and-swap writer epoch, commits command/event/revision/head
changes transactionally, enforces idempotency, and replays from origin. The first
allow-listed
tactical mutation family now materializes one Crimean-Tom-owned Knox surface
unit after explicit, session-scoped arming. That owned unit can now receive a
primary campaign authority epoch, be checkpointed, revision-fenced, updated,
suspended outside the active world, released, and returned to Operational
authority with stable identity.

Use `git log` for the current revision. Work is committed after each validated
slice; preserve unrelated user changes if the worktree is not clean.

Check `pnpm status` for the current process state. The deployed bridge should
remain build/hash-matched. Agents may launch Sea
Power when runtime verification or interactive UI work requires it, minimize
it when not used interactively, and close agent-launched applications afterward.

## Historical Phase 0 foundation

The hardened read-only bridge currently provides:

- strict length-prefixed frames, bounded queues, priority lanes, deadlines,
  cancellation frames, and session fencing;
- a current-user-only named pipe and authenticated loopback HTTP API;
- explicit `starting`, `waiting-for-game`, `handshaking`, `connected`, and
  `stopping` companion states, plus a monotonically increasing session
  generation and transition timestamp;
- removal of queued Unity commands and queued responses belonging to an ended
  session, preventing stale authorized work from crossing a reconnect;
- graceful broker shutdown that disconnects the game and completes pending
  callers;
- direct and real-host control-token authentication tests;
- main-thread ping, capability, scene inspection, and a live world snapshot,
  plus runtime file identity and fail-closed authority blockers;
- a typed campaign-management read and typed `open-task-force-builder` UI
  action, with explicit preconditions and a verified loaded-panel
  postcondition.

`get-world-snapshot` and `GET /game/world` now expose scenario-scoped task-force
identities and Sea Power's own per-unit `UniqueID` values. Unit records include
task-force ownership, name, class, runtime type, nation, latitude/longitude,
altitude, heading, speed, and destruction state. The command is read-only and
runs through the existing Unity-main-thread dispatcher. `pnpm game:world`
provides the operator-facing command.

The companion now enforces one absolute command lifetime after a pending slot
is acquired. The same deadline token governs write-lock acquisition, the
complete pipe-frame write, and correlated response wait. The shorter transport
write timeout remains a separate linked bound; interrupted writes dispose the
session so a partial frame cannot poison subsequent parsing. Caller
cancellation, command expiry, transport failure, and game disconnection retain
distinct results. `GamePipeBrokerOptions` provides short timeout and one-slot
test seams while retaining the five-second production default.

The real-pipe deadline test negotiates a session, verifies the advertised short
deadline, withholds the response, observes a correlated Control-priority
`cancel-command` with reason `deadline-elapsed`, requires
`game-command-timeout`, then completes a follow-up command through the same
one-slot broker to prove the pending slot was released.

The runtime bridge also runs a one-shot initialization self-test on a managed
worker. It invokes the actual `GameCommandDispatcher` with a valid future-dated
ping envelope and requires a structured `wrong-execution-thread` response. The
top-level capability result exposes only `pending`, `passed`, or sanitized
`failed:*` values and remains outside the stable compatibility fingerprint.
Pending or failed results block authority. The control smoke now requires
`passed`.

Control-token files are now created with a current-user-only Windows ACL in the
same atomic file-creation operation. Existing token files must already be owned
by the current user before the companion will use them. The real-host
integration test starts the actual companion on a temporary loopback port and
requires missing and incorrect tokens to return structured `401` responses,
while the generated token reaches `/health` successfully.

The companion lifecycle script now verifies that a PID belongs to the expected
companion command line before treating it as live or stopping it. Startup clears
stale PID, token, stdout, and stderr artifacts; failed startup stops its child
and removes transient PID/token state. The integration test seeds its own PID as
a recycled unrelated PID plus stale token/log markers, then proves safe startup,
artifact replacement, authenticated health, and clean stop in an isolated run
directory.

The actual broker now has a Windows kernel ACL test that impersonates the
anonymous security token, requires access denial before protocol negotiation,
then connects and handshakes as the current user through the same listener. A
stronger proof using a second named local account remains outstanding because
this medium-integrity session has neither that account's credentials nor token
management privileges; no account or system-policy changes were made.

The earlier collection of narrow unit tests was removed. Repository policy now
prefers working features and a small number of broad integration scenarios.
The remaining passing integration test starts the real companion through its
lifecycle script, verifies stale-artifact cleanup and token enforcement,
connects a protocol-speaking game peer over a unique current-user pipe, then
traverses health, ping, capabilities, scene, world, campaign management, and
typed task-force-builder automation end to end. The builder result also proves
that ship, submarine, and air roster groups are populated and returns purchased
draft units with stable instance ID, unit/category/variant, nation, selected
loadout, quantity, cost, and hosted-air assignments. Its cleanup waits for
redirected log handles to close instead of racing companion shutdown.

The freshly deployed bridge loaded on Sea Power `0.8.2-23444+s358`, returned a
ready runtime fingerprint and `executionThreadGuardSelfTest: passed`, and passed
the complete control smoke. Ping and scene both ran on Unity thread 1. The
wrapper's hash implementation now uses .NET SHA-256 directly, avoiding a
Windows PowerShell module-resolution failure inherited through `pnpm`. The
build completed with zero warnings and errors, and the built/deployed DLL
hashes matched.

The same companion survived two consecutive full Sea Power process restarts.
Game PIDs changed `8980 -> 30452 -> 33264`, pipe sessions changed each time,
session generation advanced `1 -> 2 -> 3`, and every restart passed the full
control smoke with the same compatibility fingerprint. Rapid restart exposed a
Steam cooldown: one launch URI was ignored. The launch wrapper now retries the
same sanctioned Steam app URI every ten seconds within a bounded window; the
next immediate restart exercised and passed on attempt 2.

Fault injection then force-terminated validated companion PID `32676`. Startup
consumed the stale lifecycle state, replacement PID `44520` started, and the
still-running game reconnected and passed the full smoke. Finally validated game
PID `33264` was force-terminated. The companion moved to `waiting-for-game`
with no session, Steam relaunched game PID `508`, and the fresh plugin/session
again passed the full smoke and preserved the fingerprint.

Live feature verification then loaded Sea Power `0.8.2-23444+s358` scenario
`01 Red Sea Breakout - NATO (1988)`. `/game/scene` reported 41 total objects and
3 task forces; `/game/world` returned all 3 task forces and 18 units. Real
records included Yorktown, coastal installations, P6/Osa boats, Petya, and
Krivak. Two consecutive captures retained the same 18 unit IDs while Yorktown
(`unit:8`, `taskforce:Taskforce1`) moved at about 12.79 knots on heading 144.77,
proving both scenario-scoped identity stability and live state updates.

## Working port-operations shell

`content/campaigns/crimean-tom-port-operations` is now a disposable Task Force
Mode campaign projection. The build/deploy wrapper copies every file into Sea
Power's user campaign directory and verifies its SHA-256 hash. It deliberately
uses a repository-owned roster rather than a cross-campaign path: source
inspection proved that `RosterFile` is resolved relative to the authored
campaign, which explained the initially empty catalog.

Live verification on Sea Power `0.8.2-23444+s358` showed the development
campaign, opened its one-node `PORT OPERATIONS / MAINTENANCE` map, and used the
typed bridge action to open the native Task Force Builder. The builder displayed
500/500 points and a populated catalog containing ships such as Knox, Perry,
Spruance, Kidd, Virginia, and their configured loadouts, plus submarine and air
groups. No purchase or confirmation was made. No campaign save was created and
no existing user save was read or changed.

This shell is a maintenance draft editor, not a second campaign engine. The
external Crimean Tom service remains authoritative for theater time, force
location, readiness, stores, repair, reinforcement, and outcomes. The intended
workflow is:

1. pause only the player's administrative transaction at a durable campaign
   revision;
2. materialize that port's eligible docked force, reinforcement pool, budgets,
   and rules into the shell roster/save projection;
3. perform a controlled Sea Power restart when the projection changes, then let
   the player use the native builder/refit UI;
4. read the resulting draft through typed bridge state, validate it against the
   original revision, and commit one external campaign transaction;
5. rematerialize or restart as needed while the rest of the theater continues
   under the external simulation.

The referenced built-in mission is temporary UI/map context only. Starting or
finishing it must never resolve a Crimean Tom battle, advance the authoritative
campaign, or inject tactical results. A future empty/non-resolving maintenance
container is preferable once the required native UI context is understood.

## Next implementation slice

Portable checkpoint capture, fenced update, duplicate-update replay, stale
revision/epoch rejection, and native suspension/enable have live evidence in
`docs/evidence/phase-2-live-evidence-2026-08-20.md`. Suspension removed the
owned Knox from the active world and enable restored the same game unique ID,
state, and canonical world hash exactly once.

The quiet-removal investigation is now complete: native destruction and the
partial pooling callback cannot safely provide quiet memory reclamation, so the
supported P2 lifecycle is explicitly retained suspension. Checkpoint restore is exact on a
retained object and reconstructs after Unity disposes the object during scene
reload. A companion reconnect requires and successfully enforces new-session
arming before lifecycle work resumes. Field tolerances and live timings are in
the Phase 2 evidence.

Transactional fault injection now covers all eleven declared materialize,
update, suspension, and restore boundaries. A resource snapshot exposes active,
retained-suspended, detached, recovery-required, and retained-constructed
counts. Detached recovery release is registry-only, fenced, idempotent, and
cannot act while a Unity object or checkpoint remains. The Phase 2 gate matrix
closes the narrow Knox gate.

Canonical snapshots are strict-decoded, invariant-checked against durable
journal replay before storage, content-hashed, and used as a recovery base for
the remaining journal tail.

Portable checkpoint and catalog payloads now use an adjacent immutable SHA-256
blob store. A live writer epoch binds each logical reference to a campaign commit
position, and full integrity verification crosses SQLite, snapshots, subject
revisions, event hashes, and every referenced blob.

Backup now copies a consistent SQLite image, derives the exact referenced blob
set from that copied database, verifies the staged bundle, and publishes it with
one directory rename. Restore verifies the manifest/database/blob hashes,
reconstructs every campaign, fences all pre-backup writer epochs, and atomically
publishes a new storage directory.

All fourteen append, snapshot, blob, backup-publication, and lost-acknowledgement
boundaries now have both controlled interruption and abrupt child-process death
coverage. Recovery always selects one valid head and retries have exactly-once
effects. A separate process also holds the live writer lease while the test
process proves a second coordinator is rejected; after killing the holder and
advancing past expiry, a newer epoch fences its stale handle.

The authoritative kernel now advances a durable scheduled queue through graph
movement, resource consumption and port replenishment, damage/readiness,
spare-parts repair, attrition, deterministic AI patrol intent, and coalition
intelligence refresh. One-worker and four-worker decisions produce identical
event and snapshot bytes. The journal recomputes decisions from its recovered
head before append and revision-fences every secondary simulation mutation.

The Phase 3 gate is closed by the one broad theater story. Through campaign time
600, one-worker and eight-worker decisions remain byte-identical; resource,
lifecycle, ownership, reachability, schedule, revision, and fog invariants pass;
snapshot-plus-journal and restored replay both reach the pinned golden hash
`bafa04793761b86b23aee4817bcabfba11ae21262bb4fdea3aa34724d9d4d036`.

The Phase 4 transport and canonical handoff slice is now implemented. Generated Protobuf types
cross authenticated bidirectional gRPC between a new campaign-service host and
the companion. Both sides durably spool authority-affecting frames before
acknowledgement; committed catch-up advances an atomic reconnect cursor; and
checkpoint bytes use a separate authenticated content-addressed HTTP endpoint.
The broad protocol story bootstraps a canonical theater, materializes and
checkpoints a Knox, activates its primary authority lease, advances unrelated
strategic simulation while freezing that entity, commits ordered movement, hard
restarts the service, idempotently replays unacknowledged frames, captures a
final checkpoint, and returns the entity to Operational fidelity.

A second handoff now proves bounded worker-loss recovery. After the companion
publishes the initial checkpoint it is stopped; the coordinator advances to the
fixed 120-quanta assignment deadline, keeps unrelated simulation moving, then
releases authority and restores Operational fidelity with an explicit last-
checkpoint cursor/hash and maximum-loss report.

Portable owned-surface checkpoints now include normalized fuel, magazine, and
damage fractions. The companion combines those values with its durable handoff
baseline and submits one fenced final-state event before release. The broad
story proves canonical fuel `1,000,000 -> 800,000`, stores `500,000 -> 300,000`,
and damage `0 -> 250,000` together with the prior kinematics round trip.

The same broad story now rejects an incompatible catalog before issuing any
materialization command, captures a destroyed combat Knox as canonical terminal
damage before release, and resolves the rejected Preparing assignment through
the same 120-quanta fallback. Initial and final checkpoints may legitimately
share a game revision; their durable spool/blob identities now include content
hashes so both captures coexist while exact replay remains idempotent.

The broad story now also replaces the game pipe twice to prove both crash
boundaries. A local movement applied before the game returns its response never
becomes a tactical proposal or canonical event, while the assignment still
falls back from its committed initial checkpoint. A second game crashes during
its first checkpoint capture; that Preparing assignment reaches the same fixed
deadline and reports Operational resolution without a worker checkpoint.

The live P4 run used typed automation to load Red Sea Breakout, materialized real
Knox instances into the player task force, committed an ordered movement in
120.4 ms, and completed a fresh handoff in 287.7 ms. A live no-state-delta final
checkpoint exposed and then verified a release bug: after the fix, quiesce,
checkpoint, game suspension, canonical release, and Operational restoration
completed in 212.7 ms. A typed RIM-66G launch returned a native `weapon:691`
postcondition in 190.2 ms while the campaign head remained unchanged, directly
proving the launch was visible/local but not durable. Typed tactical exit then
scheduled the native debrief flow without a menu click. The final campaign was
at time 270, commit 35, with every assignment released.

The authenticated `/durability-policy` contract names the resulting policy:
Sea Power displays native launch, impact, damage, destruction, UI, audio, and
visual effects immediately and provisionally; movement is proposed
continuously, while resource and combat outcomes become canonical through typed
events or final checkpoints. Only the external campaign journal commit is
durable. A crash discards local-only state and resumes from the last committed
checkpoint with a maximum 120-quanta rollback. Exact UI/audio/visual emission
timestamps remain unavailable from Sea Power and are reported as unavailable,
not inferred as a display delay.

Phase 5 is active on schema v6. The deterministic Fulda Gap Gone Hot proving
campaign has 12 connected regions and 15 opening entities across the Central
Front, North Atlantic, GIUK, Norwegian Sea, North Sea, and Baltic. Land armies
are explicitly `LandAbstract`, capped at Operational fidelity, and rejected by
tactical handoff; Sea Power tactical promotion is reserved for the naval and air
reinforcement war it can credibly simulate.

The service now resolves deterministic operational detection, uncertainty,
expenditure, damage, destruction, and whole-task-force withdrawal. Its first
dynamic scheduler builds task-force-minimum islands, merges recent opposing
combat groups, applies observation/control, domain ceilings, active-authority
and interaction fences, capacity, client health/eligibility, backlog load
shedding, and target tempo. The authenticated Tailwind/Babylon monitor explains
those decisions on a square black-and-white wireframe campaign plot with green,
red, amber, and white semantic state. The broad host story proves promotion,
ceiling enforcement, quiet-island overload demotion, re-promotion, safe
demotion, transport, crash recovery, and operational fallback together.

Schema v6 begins the simulation-container layer. The session registry now keeps
multiple companion workers connected at once; assignments persist their owner
client ID, assignments and fences route only to that owner, non-owner tactical
returns are rejected, and committed journal events broadcast to every worker.
The diagnostic API reports every worker and its live resource/coupling envelope.
The broad protocol story connects a second gRPC worker without replacing the
real game companion, then removes it while the primary session remains intact.
This is placement and fencing infrastructure, not yet a claim of safe live
whole-island migration or automatic rebalance.

The scheduler no longer treats that session list as one selected worker. It
aggregates resource debt and unsafe coupling across every campaign worker, and
places each new complete island on one compatible least-loaded destination.
Planned assignments count immediately, so several islands in one scheduling
call can spread rather than concentrating before the next heartbeat. Existing
batch ownership is sticky and conflicting live owners fail closed. The final
tie-breaker is stable client identity rather than connection time. The current
capacity envelope remains campaign-global; explicit per-worker health and
migration-overlap budgets are the next admission-control slice.

Tactical fidelity is now an achieved worker state rather than a scheduler label.
The scheduler may project Tactical as desired, but leaves canonical fidelity at
the lower level until a companion materializes the unit and publishes its first
checkpoint. Handoff creates an Operational `Preparing` reservation; a ready
response atomically promotes the entity and activates authority in one journal
event, while rejection, lost queue ownership, or pre-checkpoint failure releases
the reservation without phantom Tactical state.

The handshake now reports the loaded player task-force ID plus exact supported
materialization profiles. Scheduler projection gates Tactical selection on a
connected matching campaign worker and full-island representability, reporting
`tactical-worker-offline`, `player-taskforce-unavailable`, or
`worker-materialization-ceiling` instead of requesting impossible handoffs. The
current Knox-only surface adapter therefore cannot misrepresent a mixed naval
island as fully tactical.

Scheduling a compatible island creates deterministic batch-linked assignments
and queues them to the connected companion without separate handoff API calls.
Individual checkpoints move members into Prepared; one journal event activates
the entire island. Strategic sources are promoted to Operational before
Preparing. The broad protocol story now proves a two-unit group cannot become
partially Tactical and exposes batch identity, size, lifecycle, and authority
epoch through diagnostics and the monitor.

Scheduler-driven load shedding closes the whole-island loop: Active members
become Quiescing, companions resolve durably recorded game mutation revisions,
publish final checkpoints, and suspend owned units. Members hold Quiesced until
one event releases every assignment and restores Operational fidelity across
the batch. Direct handoff/quiesce endpoints remain explicit operator/debug
seams.

The latest scheduler request is now a canonical event and snapshot field rather
than a process-local diagnostic cache. Health, eligibility, interest, capacity,
backlog, request hash, and stable-sample count survive a service restart. A
healthy active island receives one durable hysteresis hold after player interest
disappears and drains on the second matching sample; explicit drain and overload
still act immediately. The broad protocol story proves both restart recovery and
the two-sample whole-batch drain.

The live Phase 5 vertical slice now carries these decisions through Sea Power.
Typed scenario and briefing actions loaded Red Sea Breakout without menu clicks;
the Fulda scheduler promoted singleton GIUK picket USS Pharris, the companion
materialized and checkpointed a real Knox, two healthy interest-loss samples
quiesced and released it, and a later higher-epoch assignment re-enabled the
retained checkpointed object. Campaign `37772898357347cd9c9c6fd724037d11`
ended the proof at commit 85 with epoch 5 Active/Tactical and no bridge recovery
debt. See `docs/evidence/phase-5-live-evidence-2026-08-21.md`.

The follow-on live capacity campaign
`160bc4f63fdd4d6f84fb2bd136c255ac` promoted four independent Pharris/Knox
islands into one Sea Power client. All four were Active in 4.02 seconds, drained
in 2.01 seconds after the required hysteresis samples, and reactivated from the
same retained game objects in 1.32 seconds. It ended at commit 200 with four
epoch-2 Active/Tactical assignments and zero recovery debt. The hosted monitor
deep link is
`http://127.0.0.1:5081/monitor/?campaign=160bc4f63fdd4d6f84fb2bd136c255ac`.

The connected worker no longer freezes its launch-scene capability snapshot.
One-second campaign heartbeats refresh the game session, runtime/catalog
fingerprints, player task force, and exact materialization profiles. Live typed
automation moved the same service session `8a6e6fd197bc4fce9586b9f0145c1f61`
from tactical play to debrief (where the task force cleared) and back into Red
Sea Breakout (where `taskforce:Taskforce1` and the Knox profile returned) without
restarting the companion. A fresh post-transition 4x campaign,
`739f13a58fde41ff9563bae754213672`, reached commit 177 with four epoch-1
Active/Tactical Knox assignments and zero recovery debt. Its current monitor is
`http://127.0.0.1:5081/monitor/?campaign=739f13a58fde41ff9563bae754213672`.

Active weapon/guidance coupling now crosses the full live path. The bridge
captures a compact active-weapon graph, the companion converts campaign-stable
platform/target IDs into unsafe heartbeat relationships, and the service keeps
one-missed-sample hysteresis before unioning those edges into scheduler islands.
Fresh 4x Fulda campaign `d42468bb4c984e50bacecfd6f8f4cb57`
reached commit 188 with one campaign Knox held Active/Tactical by an untargeted
RGM-84C self-edge while three quiet Knox islands drained to Operational. The
hosted monitor visibly reports `active-coupling-fence`,
`ENGAGED // FENCED`, and `active-weapon:ActiveRadarHoming` at
`http://127.0.0.1:5081/monitor/?campaign=d42468bb4c984e50bacecfd6f8f4cb57`.

Schema-v2 coupling now also captures exact native sensor targets/guided weapons,
home bases, runtime and temporary formations/refueling, plotting tracks, and
collision-avoidance targets. Native plotting records collapse to stable
source/target/sensor-kind keys. In fresh 4x Fulda campaign
`454e01816c934979a5c6c7b930d43c34`, four Knox hulls reached Active/Tactical at
commit 178. Two campaign-stable hulls were placed head-on through the owned
mutation seam; Sea Power emitted reciprocal physical collision-avoidance edges,
the companion published both with zero missed samples, and commit 179 joined
their independent groups into one unsafe two-unit Tactical island with
`physical-dependency:collision-avoidance` and `active-coupling-fence`. The raw
mixed mission graph captured 132 stable relationships in 3,059 microseconds.

The representative scenario is now catalog-audited rather than the earlier 4x
Knox capacity tableau. Typed scenario automation loads and pauses USS Pharris
FF-1094 (`Variant43`) and USS Honolulu SSN-718 against a Victor III and Oscar I
in the GIUK window. The global operational bootstrap spans 32 regions, 31 bases,
60 entities and 34 groups across the Central Front, Atlantic, South Atlantic,
Middle East, Indian Ocean and Asia-Pacific. Taiwan and Korea extend the eastern
theater with RoCAF/ROCN, ROKAF and KPAF forces. It uses shipped catalog IDs for
21 directly represented national forces; Argentina is an explicitly labelled
alternate-history Soviet logistics partner using cargo-hull substitutions. The
current live campaign is `e7ae56ae91be4138abf4af5c03fee6cd` at
`http://127.0.0.1:5081/monitor/?campaign=e7ae56ae91be4138abf4af5c03fee6cd`.

Schema v5 snapshots each entity's selected catalog loadout and operational
weapon capabilities. Bootstrap resolves installed Sea Power hardpoints,
magazines, date-based 1989 ammunition, target classes, and native ranges;
unresolved data cannot fire. The current live campaign resolved 338 target/range
capabilities across 55 armed entities. Its first 30 quanta produced nine weapon
expenditures, including the new Korea and Taiwan fronts, with zero range
violations and a maximum shot-to-catalog-range ratio of 0.929.

The first tactical worker container is also live. Typed scenario automation
loaded `scenario:missions/user_missions/crimean tom - tactical worker container`
without menu clicks. It paused at 2.013 simulation seconds with a valid player
task force, one stationary process-local Adjutant-class anchor, zero campaign
identities, no hostile units, and no local campaign-resolution path. This is the
intended empty shell for fenced interaction-island materialization.

The shell has now completed a real scheduler round trip in the schema-v5
campaign. USS Pharris became epoch-1 Active/Tactical at commit 242, materialized
beside the anchor, then returned to Released/Operational at commit 248 after the
two-sample interest-loss hysteresis. Runtime resources ended with zero active,
one retained-suspended and zero recovery-required campaign objects; no local
scenario outcome entered the journal.

The experimental weapon seam now takes an exact ammunition ID and supports Sea
Power's native `ASROC` type. It verifies that exact round after launch and
distinguishes active-world registration from the narrower native save lists.
In the representative GIUK window, typed automation launched `usn_rur-5` from
Pharris to a Victor III five nautical miles away. With that exact ASW weapon
active, 900 controlled ticks advanced 30.0 simulation seconds in 15.114 wall
seconds (59.5 ticks/s, 1.98x real time), and a following 1,800-tick batch held
59.8 ticks/s. Coupling capture retained the native Pharris-to-Victor edge in
203 microseconds. This is the first valid active-combat load sample; mixed-island
capacity and operational outcome calibration remain open.

The first schema-v6 representative campaign is
`ec4fb4931d2a46589092530202af3ad8`. Bootstrap and a real 30-quanta advance
reached commit 186 with 32 regions, 31 bases, 60 entities, 34 groups and 107
recent outcomes. The unchanged live Sea Power session reconnected through one
worker, and diagnostics reported the exact worker/game session without exposing
the service token. The monitor now shows worker count plus aggregate
materialized, active and recovery debt, and selected-unit inspection reports
the owning worker. A browser pass at the schema-v6 deep link rendered the world
map and hierarchy with no warnings or errors:
`http://127.0.0.1:5081/monitor/?campaign=ec4fb4931d2a46589092530202af3ad8`.

The companion launcher now derives `companion-state-<campaignId>` beneath a
shared campaign run directory. Switching campaigns no longer reuses another
campaign's durable cursor and silently prevents the gRPC session from opening.

Reserve-aware load shedding is now implemented around actual runtime resource
counts: active, suspended-retained, recovery-required and total materialized
objects all contribute to the scheduler projection. Capacity pressure selects
whole quiet islands and preserves fenced/engaged islands; the scheduling
response retains the original decision reasons even after assignments enter
Quiescing. Next, expand live tactical combat tempo to mixed active systems,
exercise campaign-owned sensor/host/refueling edges, and
calibrate the operational resolver against tactical observation batches. Keep
tactical projection and campaign commit separate: Sea Power remains a fenced
client of the external authoritative kernel.

Campaign diagnostics and the monitor now expose process-local service uptime,
managed/working-set memory, recovery latency, advance latency and quanta rate,
and scheduler latency/island rate. The two broad stories remain the only tests;
the protocol story requires a real 30-quanta advance to populate and validate
that telemetry. These measurements are operational evidence, not canonical
inputs.

The hosted operator monitor now plots the campaign over a Natural Earth 110m
world wire map, discovers journal campaigns through `GET /campaigns`, provides
switching/copyable deep links, and presents units as a collapsible NATO/Warsaw
Pact -> formation -> unit order of battle. Fixed SVG affiliation cells replace
the cramped text glyph layout. The combined watcher uses polling plus backend
process restarts (not Roslyn hot reload), preventing hosted monitor asset builds
from crashing the development session.

The scaled Fulda bootstrap and `pnpm campaign:stress` now exercise 1x–100x force
packages through the actual HTTP, kernel, SQLite journal, recovery, and telemetry
path. On the Ryzen 9 9900X host, 20x (300 entities/180 groups) produced 1,866
outcomes and sustained 35.9 quanta/s, passing the 30/s healthy target. 40x (600
entities/360 groups) produced 3,891 outcomes at 18.5/s and is explicitly an
overloaded envelope. Full measurements and caveats are in
`docs/evidence/phase-5-load-evidence-2026-08-21.md`.

## Deferred hardening

- Complete the stronger named-local-account pipe rejection proof when a
  disposable second-user credential or pre-authorized helper is available.
- Expand fault injection to commands interrupted during write/execution when a
  deterministic suspension hook exists; process-level crash/restart coverage
  is complete.
- Pin the bridge compiler/SDK, trial C# 13 through source-level metadata shims,
  and verify compile, IL/API compatibility, clean load, and command smoke.
- Keep catalog/mod fingerprints and deterministic-hook method fingerprints
  fail-closed until their Phase 1 implementations exist.

## Verification commands

Use the repository automation and the repository-local runtime skill:

```powershell
pnpm test
pnpm build
pnpm dev
pnpm game:control-test
pnpm build
```

The final `pnpm build` intentionally stops the companion and Sea Power after
the live check while leaving the verified DLL deployed. Commit each validated
implementation slice as required by the active project goal.
