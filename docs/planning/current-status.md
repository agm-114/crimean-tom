# Current code and product status

## Snapshot

Reviewed from `main` at `5c4302c` through the local tactical-sync POC branch on
23 August 2026 and against the installed Sea Power build
`0.8.2-23444+s358`. This is the living status ledger. Dated files in
`docs/evidence/` remain the proof record; the phase plan remains the target and
gate definition.

The repository is an advanced campaign-runtime prototype with a narrow local
tactical-sync and friendly-render-proxy POC, not yet a complete playable
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
| Sea Power adapter | Authenticated companion/pipe session, extraction, screenshot/UI automation, controlled surface mutation, checkpoints, collider capture | Pinned game build; narrow allow-list |
| Campaign core | Immutable canonical state, typed commands/events, fixed logical time, deterministic replay/hash | Broad integration story and golden hashes |
| Persistence | SQLite WAL, writer fencing, snapshots every 100 commits, CAS blobs, integrity checks, online backup and side-directory restore | No recovered-head cache or qualified long-journal envelope |
| Operational simulation | Unattended movement, sustainment, intelligence, doctrine, sensors, ordnance, countermeasures, damage, withdrawal, basic AI | Deterministic analogue, not Sea Power parity; calibration remains limited |
| Fidelity/authority | Interaction-island scheduling, capacity/reserve accounting, Primary/Shadow fencing, cold reconstruction, migration protocol | Live two-container completion and hot convergence unproven |
| Local tactical sync | Heartbeat-renewed assignment lease; two-second changed-state readback for owned surface kinematics/resources/damage; inert two-second campaign projection of other deployed friendly Knox-class vessels; unpaused read capture and pause/restore mutation safe points | Script-guided trusted-local POC; one authoritative surface family and one exact proxy profile; no hostile/contact proxy or campaign-owned hostile loss proof |
| Operational workers | Spawn, identity, health, drain/stop, assignment placement | Work still executes in ServiceHost |
| Player data boundary | Coalition-filtered HTTP projection, reconnectable in-memory session, bounded ordered envelopes | Trusted private model; sessions do not survive restart and envelopes do not issue canonical orders |
| Operator experience | Hosted world monitor, force hierarchy, scheduler/worker/performance/outcome diagnostics, Fulda seed UI | HTTP polling; hosted reads use a locally issued cookie, while mutations currently work only through the authenticated dev proxy |
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

1. **Bounded journal recovery.** ServiceHost now saves a canonical snapshot
   every 100 commits, so ordinary recovery starts from a recent snapshot rather
   than the origin. The coordinator still has no recovered-state cache, each
   append re-derives the durable head, and no 10,000-commit recovery/storage
   envelope has been qualified.
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
   semantics still need qualification.
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

### P0 — complete a player-visible campaign loop

1. **Canonical player orders and UI.** Convert coalition-scoped authenticated
   player intent into
   authorized, journaled `IssueOrderCommand`/policy commands with deterministic
   conflict handling and clear rejection feedback. Do not turn the privileged
   true-state operator monitor into the player trust boundary: either provide a
   distinct player surface or an explicit coalition-filtered mode. Operator
   tooling still needs order creation/cancellation, ROE/doctrine,
   destination/tasking, pause/resume, and ruleset configuration.
2. **Objectives and campaign progression.** Add durable objectives, victory and
   failure predicates, scenario/campaign end state, political/narrative events,
   reinforcement/replacement policy, and an authorable ruleset. The current
   Fulda campaign is hard-coded proving content.
3. **Playable tactical round trip.** Replace the now-proven script-guided local
   POC with an operator/player flow that moves a selected interaction island
   into Sea Power, exposes assignment/preparation and lease progress, starts
   play, and returns cleanly to the campaign. Prove the fate of a
   campaign-owned hostile, not only owned-vessel kinematics/resources/damage.
4. **Broaden portable state and entity families.** Qualify or add aircraft,
   subsurface, base/land, formation, and hosted-aircraft materialization. Apply
   or explicitly model damage, fuel, magazines, sensors, formations, and hosted
   children instead of leaving them fenced as unsupported fields. The local
   friendly render-proxy slice covers only deployed Knox-class maritime units;
   move it to a coalition-scoped service projection and add catalog-specific
   proxy profiles before treating it as a general theater view.
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

The next work should build only the foundation needed for an honest playable
slice, then traverse that slice vertically:

1. close the HTTP content-type bypass and implement scoped
   companion/worker/operator/player credentials plus an authenticated hosted
   browser write channel;
2. qualify the 100-commit snapshot cadence and/or add a safe recovered-head
   cache, then prove bounded behavior on at least 10,000 commits;
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
