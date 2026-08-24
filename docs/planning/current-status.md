# Current code and product status

## Snapshot

Reviewed through algorithm-v18 strategic-AI, physical-movement, full-scale
recovery, automatic tactical restart reconciliation, and live land-streaming
qualification on 24 August 2026 and against
the installed Sea Power build
`0.8.2-23444+s358`. This is the living status ledger. Dated files in
`docs/evidence/` remain the proof record; the phase plan remains the target and
gate definition. The
[playable dynamic campaign product contract](playable-dynamic-campaign-product-contract.md)
is the source of truth for the intended player experience and minimum playable
acceptance story.

The repository is an advanced campaign-runtime prototype with a local
tactical-sync, land-target, and friendly-render-proxy POC, not yet a complete playable
dynamic-campaign product. Its
strongest areas are deterministic reduction and
replay, authority fencing, the Sea Power control boundary, broad integration
stories, and an increasingly capable Operational analogue. The POC can keep a
real owned surface assignment alive by heartbeat while the rest of the campaign
advances, and can commit changed local tactical state during unpaused play. The
guided player loop, complete result contract, save evolution, and
principal-separated browser writes remain open before an alpha.

## What works now

| Area | Current capability | Qualification |
| --- | --- | --- |
| Sea Power adapter | Authenticated companion/pipe session, extraction, screenshot/UI automation, controlled surface and land/installation mutation, terrain preparation/probing, checkpoints, collider capture | Pinned game build; narrow allow-list of one surface and six land/installation profiles |
| Campaign core | Immutable canonical state, typed commands/events, fixed logical time, deterministic replay/hash; sourced 55-region/81-base/4,096-entity WW3++ proving seed; two-sided strategic AI persists coalition strategies, objectives, bounded group commitments, operational packages, and orders; allocation uses coalition intelligence, weapon capability, target-response saturation, and missing package roles; fixed defense complexes cannot move | Algorithm v18 kept capability matching at 95.5% while producing 63 durable packages over 418 unique groups: 61 phased their starts, 53 carried at least three roles, and every explicit strike package included air cover; zero fixed groups moved |
| Persistence | SQLite WAL, writer fencing, bounded atomic seed import with a final snapshot, durable-head-checked recovered-state cache, compressed stored journal payloads, completed-order retirement, prepared coordinator appends, bounded periodic snapshots, CAS blobs, integrity checks, online backup and side-directory restore; companion catch-up now retains only tactical-migration proofs instead of every full event | Algorithm-v18 4,096-entity journal recovery at time 400 restored all 63 packages and matched its hash in 3.41 seconds (4.48 seconds through the HTTP view); the live companion spool stopped growing across 11 further commits after an obsolete 1.716 GB full-event cache exposed the defect, but campaign-event deltas and a 10,000-commit retention/compaction envelope remain unqualified |
| Operational simulation | Unattended physical-distance movement toward graph-leg anchors, domain-scaled fuel/stores, sustainment, intelligence, doctrine, sensors, ordnance, countermeasures, damage, withdrawal, and operational sub-AI across maritime, fixed-wing, rotary-wing, ground, installation, logistics, and abstract-land domains | Deterministic analogue, not full Sea Power parity; movement no longer teleports one region or creates damage merely by travelling; detailed ground-ground combat remains deliberately absent |
| Fidelity/authority | Interaction-island scheduling, capacity/reserve accounting, resident strategic-system policy, Primary/Shadow fencing, cold reconstruction, migration protocol | Strategic SAM/radar/ballistic islands stay Operational without a compatible worker and request constant Tactical presence with one; live two-container completion remains unproven |
| Local tactical sync | Heartbeat-renewed assignment lease; two-second changed-state readback for owned surface state; terrain-gated ground/installation materialization, retained-object rebind, and lifecycle; game-session-aware detection of missing owned objects with automatic stale-epoch release, checkpoint-backed same-worker rescheduling, and safe superseded-migration replay; coalition-map projection of every non-destroyed friendly plus coalition-filtered hostile contact tracks; native-symbol map records with lazy presentation-only model loading; explicit authenticated player-coalition binding for fresh sessions; automatic focus, terrain-window relocation, pause/restore, and stale-authority containment | The live 4,096-entity campaign first reconciled 1,761 records (1,235 friendlies and 526 contacts). A later continuously advancing sample reconciled 869 records (311 surviving friendlies and 558 contacts); selecting an Atlantic Los Angeles-class record through the typed bridge path produced exactly one non-simulating model after terrain relocation, held 60 FPS at a 16.667 ms fixed step and dilation 1, and added about 0.24 GiB working set. The authored GIUK mission still supplies an unbound static Pharris/Honolulu player task force, Sea Power's local tactical-display viewport is not yet the intended player strategic map, and direct mouse-selection parity for the newer authored-symbol renderer remains under live requalification. Automatic process-restart recovery passes the real-host/companion protocol story but still needs a live kill/relaunch latency and duplicate-object qualification. |
| WW3++ scenario content | 4,096 deterministic opening entities: full usable installed catalog coverage (241 vessels, 111 aircraft, 187 land units/sites), two-ocean US and Soviet global fleets, China/Japan and regional participation, Soviet-aligned Cuba, 38 defended airbase complexes, 149 helicopters, 984 physical ground targets, theater IADS/coastal batteries, ports, industry, depots, sealift, and civilian traffic | Research provenance and authored what-if premises are tracked; opening sorties exist and six ground/installation profiles can materialize, but recurring air tasking, neutral diplomacy/multi-coalition relations, rotary-wing materialization, and general catalog coverage remain open |
| Operational workers | Spawn, identity, health, drain/stop, assignment placement | Work still executes in ServiceHost |
| Player data boundary | Coalition-filtered HTTP projection, reconnectable in-memory session, bounded ordered envelopes | Trusted private model; sessions do not survive restart and envelopes do not issue canonical orders |
| Operator experience | Hosted world monitor, force hierarchy, scheduler/worker/performance/outcome diagnostics, Fulda seed UI | HTTP polling; hosted reads use a locally issued cookie, while mutations currently work only through the authenticated dev proxy |
| Performance comparison | Qualified vanilla/candidate capture and JSON comparison for FPS, snapshot latency, clocks, object/proxy counts, process CPU/memory, and campaign throughput; current full-seed pair measured 60.000 average FPS candidate versus 60.001 vanilla; typed Unity asset diagnostics now separate native mission allocations from campaign/proxy cost | The prior 12-Knox result is historical, not representative of the map-first path. In the current live sample, 869 map records used 4.95 GiB working set / 11.39 GiB private memory; one terrain-relocated physical proxy raised those to 5.19 / 11.60 GiB. The pre-fix off-window focus reached 12.2 / 19.5 GiB and is retained as failure evidence. These remain short local samples, not a soak or final 4K marker budget. |
| Runtime geometry | Durable versioned collider cache and optional meter-space Bepu terminal silhouette | Optional analogue path; no armor/penetration model |

## Documentation drift found in this review

| Drift | Correction |
| --- | --- |
| Root documentation called the companion surface read-only | The bridge now has typed UI automation, experimental probes, and fenced owned-surface mutation |
| Root documentation called the campaign application planned | ServiceHost, engine, SQLite journal, monitor, gRPC transport, and Operational worker all exist |
| Architecture project list stopped after the first three projects | [Implemented architecture map](../architecture/implementation-map.md) now maps every production/tool project |
| Architecture diagrams implied SignalR was live | SignalR is a target only; the current monitor and player projection use HTTP |
| Phase status labels said phases 5-8 were implemented | Individual infrastructure slices are implemented, but their playable/realtime/scale exit gates remain open |
| Recent collider cache and Bepu terminal work was absent | It is now documented as an optional Operational analogue path |
| The handoff diary mixed live URLs, historical proofs, and next steps | It is replaced by this stable status/gap ledger; evidence stays under `docs/evidence/` |

## Blocking foundation gaps

These are prerequisites, not release-polish tasks:

1. **Bounded long-journal recovery.** Seed import now validates and commits
   all 5,892 records atomically with one final snapshot. A clean 4,096-entity
   bootstrap measured 8.009 seconds, 162.8 MiB peak working set, and a 32.3 MiB
   database under the new local workstation-GC default. Coordinator reads and
   appends now reuse immutable recovered state after checking the durable SQLite
   head, and the full protocol story passes in 1 minute 3 seconds. A clean
   30-quanta advance measured 2.065 seconds; cold recovery plus a full diagnostic
   view measured 1.189 seconds at commit 5,893. The default unattended loop now
   reads that cache instead of retaining a fresh replay across every timer wait;
   at live head 9,300 the service held 77.2 MiB of managed objects rather than
   the observed pre-fix 1.0-1.2 GiB sawtooth. A
   10,000-commit restart/recovery/storage envelope is still unqualified. The
   companion-side copy of every replayed event was a separate unbounded spool:
   it reached 1.716 GB in the live full-seed run and now stores only compact
   tactical-migration proofs. Full-seed simulation events themselves still need
   bounded deltas.
2. **Campaign contract evolution.** Campaign recovery hard-rejects any stored
   schema or algorithm version other than the compiled constants. There is no
   upcaster, compatible old-engine path, or export-only recovery mode. Any
   objectives/ruleset work is likely to change these contracts, so save upgrade
   policy must precede private-alpha iteration.
3. **Tactical clock and lease qualification.** A live companion heartbeat now
   renews tactical authority while campaign time and unrelated entities advance;
   the 120-quanta deadline applies after the owner becomes stale. The local POC
   crossed multiple old deadlines during unpaused play. Service-restart grace,
   long-session soak, clock-rate UX, and explicit capture-time versus commit-time
   semantics still need qualification. The 23 August local proof also showed
   both clocks advancing together while an assignment remained active and
   lease-live; this is not yet a long soak or service-restart grace proof.
4. **Principals and browser writes.** Companion, workers, and operator tooling
   share the master service token. The hosted monitor is issued a read cookie
   without an operator identity and cannot authenticate writes. The general
   `application/grpc` content-type bypass also reaches non-gRPC control routes
   and must be removed before treating the loopback control plane as hardened.
5. **Shipping-default verification.** The broad protocol story disables
   unattended simulation and changes migration timing, leaving the default
   clock/expiry interaction untested. Keep broad scenario tests, but add a
   shipping-default story and a long-journal performance/recovery gate.

## Missing product features

### P0: complete a player-visible campaign loop

1. **Explicit campaign lifecycle.** New and restored campaigns need a
   player-visible paused/running state, explicit start/resume behavior, save
   selection, and time controls. The current unattended host advances every
   non-paused campaign and a long-lived development campaign can therefore open
   days into an attritional war with few surviving friendlies; that is valid
   simulation state but not an acceptable implicit new-game experience.
2. **Canonical player orders and UI.** Convert coalition-scoped authenticated
   player intent into
   authorized, journaled `IssueOrderCommand`/policy commands with deterministic
   conflict handling and clear rejection feedback. Do not turn the privileged
   true-state operator monitor into the player trust boundary: either provide a
   distinct player surface or an explicit coalition-filtered mode. Operator
   tooling still needs order creation/cancellation, ROE/doctrine,
   destination/tasking, pause/resume, and ruleset configuration.
3. **Objectives and campaign progression.** Add durable objectives, victory and
   failure predicates, scenario/campaign end state, political/narrative events,
   reinforcement/replacement policy, and an authorable ruleset. The current
   Fulda campaign is hard-coded proving content.
4. **Playable tactical round trip.** Replace the now-proven script-guided local
   POC with an operator/player flow that moves a selected interaction island
   into Sea Power, exposes assignment/preparation and lease progress, starts
   play, and returns cleanly to the campaign. Prove the fate of a
   campaign-owned hostile, not only owned-vessel kinematics/resources/damage.
5. **Broaden portable state and entity families.** Qualify or add aircraft,
   rotary-wing, subsurface, general base/land, formation, and hosted-aircraft materialization. Apply
   or explicitly model damage, fuel, magazines, sensors, formations, and hosted
   children instead of leaving them fenced as unsupported fields. The local
   land POC covers M60, M113, Vulcan, MIM-23 launcher, EWR, and small-airbase
   profiles. The map-first replica path now accepts every friendly domain and
   filtered hostile contact tracks, and it can lazily load installed vessel,
   aircraft, and land presentation models. This is not simulation-family parity:
   native owned materialization remains narrow, model/loadout variants need
   catalog qualification, and the player still needs a strategic map, explicit
   campaign task-force membership, zoom/framing, uncertainty symbology, labels,
   and lifecycle/error feedback.
6. **Campaign management UI.** Expose backup/restore, pause/drain, worker
   lifecycle, diagnostics, integrity status, and failure recovery through a
   safe operator workflow rather than HTTP/scripts alone.
7. **Airbase operations and integrated defense.** Turn seeded opening sorties
   into durable aircraft inventories and air-tasking cycles: mission creation,
   launch/recovery, turnaround, fuel and magazine draw, runway damage/repair,
   dispersal, radar-network degradation, GBAD command relationships, and SEAD.
   The current airbase complexes provide the objects and opening orders, not
   this lifecycle.
8. **Diplomacy and noncombatants.** Add relationship-aware hostility, neutral
   traffic, protected/noncombatant status, identification, ROE, diversion, and
   unintended-effects consequences. Current civilian traffic is assigned to a
   protection coalition because the engine treats every other coalition as
   hostile.

### P1: move prototype execution across its intended boundaries

1. **Out-of-process Operational work.** Implement immutable island work leases,
   deterministic worker execution, result hashes/events, validation, timeout,
   idempotent replay, quarantine, and atomic coordinator commit. Until then,
   Operational workers do not provide CPU isolation or horizontal capacity.
2. **Realtime player transport.** Add SignalR/MessagePack only after the player
   projection/command DTOs are stable. Reconnect must resume from campaign
   cursors; realtime delivery must never become a canonical commit path.
3. **Qualified tactical migration/recovery.** Complete a real isolated
   two-process Sea Power migration, active-weapon/hosted-aircraft round trips,
   destination-failure recovery, and measured recovery objectives.
4. **Content/catalog pipeline.** Replace hard-coded catalog assumptions with a
   versioned normalized catalog and campaign package format, diagnostics,
   migration rules, mod/update invalidation, and authoring validation.
5. **Calibration.** Compare Operational sensors, movement, expenditure, damage,
   and engagement outcomes against batches of Sea Power observations and expose
   a versioned ruleset/calibration profile.

### P2: qualify deployment, trust, and scale

1. **Packaged application lifecycle.** Installer/portable bundle, configuration,
   clean-machine startup, update/rollback, campaign import/export, retention and
   compaction, privacy-safe diagnostics, and a full recovery runbook.
2. **Long-duration evidence.** Soak, restart storms, resource exhaustion,
   malformed catalog/checkpoint fuzzing, large-theater benchmarks, and explicit
   latency/storage/recovery envelopes.
3. **Network deployment.** TLS, durable identity/authorization, invitation and
   revocation, LAN/private-host configuration, rate limiting, and audit UX.
4. **Hostile-client model.** Neutral adjudication or independently validated
   outcomes, proxy/fog-of-war qualification, abuse resistance, and side-channel
   review before any public multiplayer claim.
5. **Hot replicas, only if justified.** Finish the deterministic runtime-adapter
   D0-D5 program before implementing promotable live Unity Shadows. Cold
   checkpoint/Operational fallback remains the supported design otherwise.

## Recommended implementation order

The next work should build only the foundation needed for an honest playable
slice, then traverse that slice vertically:

1. close the HTTP content-type bypass and implement scoped
   companion/worker/operator/player credentials plus an authenticated hosted
   browser write channel;
2. extend the recovered-head cache and 100-commit snapshot evidence through at
   least 10,000 commits, including cold restart, storage, and full-event payload
   bounds;
3. choose and implement campaign save-contract evolution, proving that a saved
   campaign can cross one schema and one algorithm change;
4. soak the heartbeat-renewed tactical lease across service/game restarts and
   define capture-time, commit-time, and clock-rate UX for human sessions;
5. expose canonical coalition-scoped player orders headlessly, with durable
   idempotency, control-right checks, expected-revision conflicts, and typed
   rejection feedback;
6. add one small durable objective/victory ruleset and player/operator UI around
   that command path;
7. prove a guided tactical round trip that commits the fate of a
   campaign-owned hostile, then close only the portable-state fields that the
   round trip demonstrates are missing;
8. defer out-of-process Operational leases, SignalR, broader entity families,
   packaging, soak, and multiplayer qualification until the vertical slice is
   real.

### Delivery outlook

For one strong developer/agent with local-only Sea Power verification, these are
planning ranges rather than commitments:

| Outcome | Current planning range | Dominant uncertainty |
| --- | --- | --- |
| Survivable foundation (steps 1-4) | 3-5 weeks | migration design, long-journal behavior, live tactical clock policy |
| Honest narrow playable slice (steps 1-7) | 9-13 weeks | campaign-owned hostile round trip and portable native state |
| Private single-player alpha | 20-28 weeks | content/ruleset depth, management UI, save compatibility, repeated live verification |
| Single-machine documented release | 7-9 months | packaging, update/rollback, soak, recovery and compatibility qualification |
| Broader networked release | 9-15 months | identity, hostile-client boundaries, distributed execution and multiplayer evidence |

SignalR, hot replicas, PostgreSQL/brokers, broad entity-family expansion, and a
second live Sea Power container should not consume the critical path yet.

Each slice should update this file, the machine-readable supported claims when
appropriate, and a dated evidence record. A feature is not complete merely
because its control-plane endpoint or DTO exists.
