# Current code and product status

## Snapshot

Reviewed against `main` at `5c4302c` on 23 August 2026 and the installed Sea
Power build `0.8.2-23444+s358`. This is the living status ledger. Dated files in
`docs/evidence/` remain the proof record; the phase plan remains the target and
gate definition.

The repository is an advanced campaign-runtime prototype, not yet a playable
dynamic-campaign product. Its strongest areas are deterministic durable state,
authority fencing, the Sea Power control boundary, broad integration stories,
and an increasingly capable Operational analogue. Its largest gap is the
player loop: a player cannot yet author meaningful campaign orders in the UI
and play a complete campaign through tactical promotion and return.

## What works now

| Area | Current capability | Qualification |
| --- | --- | --- |
| Sea Power adapter | Authenticated companion/pipe session, extraction, screenshot/UI automation, controlled surface mutation, checkpoints, collider capture | Pinned game build; narrow allow-list |
| Campaign core | Immutable canonical state, typed commands/events, fixed logical time, deterministic replay/hash | Broad integration story and golden hashes |
| Persistence | SQLite WAL, writer fencing, snapshots, CAS blobs, integrity checks, online backup and side-directory restore | Local profile design; no installer/soak claim |
| Operational simulation | Unattended movement, sustainment, intelligence, doctrine, sensors, ordnance, countermeasures, damage, withdrawal, basic AI | Deterministic analogue, not Sea Power parity; calibration remains limited |
| Fidelity/authority | Interaction-island scheduling, capacity/reserve accounting, Primary/Shadow fencing, cold reconstruction, migration protocol | Live two-container completion and hot convergence unproven |
| Operational workers | Spawn, identity, health, drain/stop, assignment placement | Work still executes in ServiceHost |
| Player data boundary | Coalition-filtered HTTP projection, reconnectable session, bounded ordered envelopes | Trusted private model; envelopes do not issue canonical orders |
| Operator experience | Hosted world monitor, force hierarchy, scheduler/worker/performance/outcome diagnostics, Fulda seed | HTTP polling; mostly read-only |
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

## Missing product features

### P0 — complete a player-visible campaign loop

1. **Canonical player orders and UI.** Convert authenticated player intent into
   authorized, journaled `IssueOrderCommand`/policy commands with deterministic
   conflict handling and clear rejection feedback. The monitor needs order
   creation, cancellation, ROE/doctrine, destination/tasking, pause/resume, and
   general campaign/ruleset configuration. Today its only mutation is the Fulda
   demo bootstrap/advance/schedule convenience; campaign selection already works.
2. **Objectives and campaign progression.** Add durable objectives, victory and
   failure predicates, scenario/campaign end state, political/narrative events,
   reinforcement/replacement policy, and an authorable ruleset. The current
   Fulda campaign is hard-coded proving content.
3. **Playable tactical round trip.** Provide an operator/player flow that moves
   a selected interaction island into Sea Power, exposes assignment/preparation
   progress, lets play occur, captures the result, and returns cleanly to the
   campaign without script-driven orchestration.
4. **Broaden portable state and entity families.** Qualify or add aircraft,
   subsurface, base/land, formation, and hosted-aircraft materialization. Apply
   or explicitly model damage, fuel, magazines, sensors, formations, and hosted
   children instead of leaving them fenced as unsupported fields.
5. **Campaign management UI.** Expose backup/restore, pause/drain, worker
   lifecycle, diagnostics, integrity status, and failure recovery through a
   safe operator workflow rather than HTTP/scripts alone.

### P1 — move prototype execution across its intended boundaries

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

### P2 — qualify deployment, trust, and scale

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

The next work should prioritize real campaign play over more infrastructure:

1. journaled player order submission plus monitor order controls;
2. durable objective/victory state and one small end-to-end campaign ruleset;
3. a guided tactical promotion/play/return workflow for owned surface islands;
4. portable state closure for the fields that block that workflow;
5. out-of-process Operational leases/results;
6. realtime SignalR player delivery after the command/projection model is real;
7. packaging, soak, and broader multiplayer qualification.

Each slice should update this file, the machine-readable supported claims when
appropriate, and a dated evidence record. A feature is not complete merely
because its control-plane endpoint or DTO exists.
