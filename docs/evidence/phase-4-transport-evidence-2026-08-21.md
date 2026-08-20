# Phase 4 typed-transport evidence

Date: 21 August 2026

## Current disposition

Phase 4 is complete. The service/companion transport, durable spools,
authenticated checkpoint transfer, canonical assignment lifecycle, ordered
movement commit, service-restart reconnect, and checkpointed quiesce/release
are working. Portable checkpoints now carry normalized runtime fuel, magazine,
and damage fractions and combine them with the durable handoff baseline before
canonical release. Catalog mismatch now fails closed before materialization,
and a destroyed tactical unit returns as canonical terminal damage before its
assignment releases. Game-process crashes before the first checkpoint and after
a local mutation but before its proposal are covered. A real Sea Power run now
closes the materialize, ordered-move, checkpoint, quiesce, normal-exit, and
player-visible timing boundaries. Tactical
assignments have a fixed 120-quanta maximum interval: if a
worker does not return, the coordinator resolves at that exact campaign time,
releases authority, restores Operational fidelity from the latest committed
checkpoint, and reports the checkpoint cursor/hash and maximum-loss bound.

## Player-visible durability contract

Authenticated `GET /durability-policy` exposes the Phase 4 contract as data,
and the broad story asserts it:

- display is `display-first-provisional`;
- the only canonical durability boundary is `campaign-journal-commit`;
- tactical outcomes commit through a typed event or final checkpoint;
- a crash discards local-only state and restores the last committed checkpoint;
- the maximum rollback is 120 campaign quanta; and
- launch, impact, damage, destruction, UI, audio, and visual effects are all
  classified as provisional before commit.

This policy follows the implemented order. Sea Power mutates native state on
its main thread before returning a command result. Only after that result can
the companion construct and write-through stage a proposal, and only after the
service journals the resulting command/event can the outcome be canonical. It
would therefore be false to claim commit-before-display. Movement is proposed
continuously. Fuel, stores, damage, and destruction are captured by typed final
checkpoint unless a more specific typed event exists.

Sea Power exposes native world state and player-event snapshots but no exact
timestamp for every UI widget, sound, particle, or model transition. The policy
reports that coverage as
`native-world-and-player-event-observation; exact-ui-audio-visual-emission-unavailable`.
Those presentation effects are treated as provisional from their earliest
possible native display, rather than assigning them a fabricated delay.

## Live Sea Power round trip

The live gate ran against Sea Power `0.8.2-23444+s358`. The freshly loaded bridge
reported runtime fingerprint
`c4e85cfdbeb626903ffdd95cdc38c02b63927d8fe4092df2ab7b6b5ac49c8964`
and catalog fingerprint
`01d4650c0f0ddeb6a106b3e2ded8c79d27ebbcad51319e56470f45d7fa3183ea`.
Typed UI automation loaded `missions/Intro/01 Red Sea Breakout 1988.ini` and
advanced its briefing; no menu-button navigation was used.

The first real Knox handoff materialized game entity
`unit:crimean-tom:phase4-live-knox-2` as Lockwood FF-1064, game unique ID 511,
with seven ammunition families. Its campaign assignment activated in 658.8 ms.
Campaign advance `0 -> 30` kept the assigned entity's revision frozen at 2 while
an unrelated entity advanced from revision 1 to 7. An ordered movement then
changed the game object to latitude 44.210, longitude 28.720, heading 123
degrees, telegraph 1 and reached canonical commit in 120.4 ms.

That run uncovered three live-only defects which were corrected before the gate
was closed: assignment preparation had cached startup capabilities before the
catalog was ready; a service living longer than its five-minute writer lease
tried to renew an expired lease; and a no-state-delta final checkpoint returned
before releasing a quiescing assignment. Assignment handling now refreshes
capabilities, an expired lease is acquired under a new epoch, and the release
path runs even when the checkpoint matches canonical state.

A clean post-fix handoff materialized
`unit:crimean-tom:phase4-live-knox-4` and reached Active in 287.7 ms. With no
intervening state change, typed quiesce captured the checkpoint, suspended the
real object at game revision 2, released the assignment, and restored canonical
Operational fidelity in 212.7 ms. The retained game record reported
`suspended-retained` and `hasCheckpoint: true`.

For the player-visible boundary, a typed native RIM-66G launch from Yorktown to
P6 `unit:89` returned `serializable-active-weapon-launched` for `weapon:691` in
190.2 ms. The immediate world read observed the same weapon at `Launch` stage
with the intended target while the campaign remained at commit 32. Thus the
native/visible effect was measured before any campaign proposal or durable
event, exactly matching the provisional policy. The existing live targeted
missile evidence observes the later weapon disappearance and target destruction
at simulation second 23.267; the Phase 4 broad story carries corresponding
damage and destruction through a final checkpoint.

Finally, the deterministic deadline resolver released the deliberately failed
preparation at its 120-quanta bound. Typed `exit-tactical` scheduled Sea Power's
native debrief flow without a menu click. The campaign ended at time 270 and
commit 35 with every assignment Released, and the companion, campaign service,
and Sea Power were closed cleanly.

## Generated transport contract

`CrimeanTom.Campaign.Transport` owns the versioned
`campaign_companion.proto`. `Grpc.Tools` generates both client and service C#
types. The bidirectional `Connect` stream has explicitly numbered messages for:

- hello/welcome and reconnect commit cursors;
- service and companion acknowledgements;
- heartbeats;
- tactical assignments and preparation reports;
- ordered tactical commands and typed tactical event proposals;
- checkpoint publication;
- committed campaign catch-up events; and
- authority fences and terminal errors.

The Protobuf frame limit is 256 KiB. Checkpoint bytes never enter the simulation
stream.

## Authentication and endpoint separation

`CrimeanTom.Campaign.ServiceHost` exposes a dedicated HTTP/2 gRPC loopback port
and an HTTP/1 loopback port for health, administrative control, and
content-addressed blobs. The service refuses non-loopback cleartext endpoints.
Non-loopback deployment must use HTTPS.

A random 256-bit bearer token authenticates both gRPC metadata and every HTTP
route. On Windows the token file is owned by, and ACL-restricted to, the current
user. Token comparisons are fixed-time. The companion's existing local control
token and game named-pipe session remain separate fences.

## Durability and flow control

The service accepts only a sequence-1 hello, validates protocol/campaign/cursor,
and rejects a cursor ahead of the SQLite durable head. It streams strict ordered
journal catch-up after the requested cursor.

Both directions use bounded application queues. Before acknowledging an
authority-affecting companion frame, the service writes its exact Protobuf bytes
to a 4,096-entry/64-MiB write-through spool with atomic publication. The
companion likewise write-through stages assignment, checkpoint, tactical-event,
and fence responses before sending. A service acknowledgement removes the
companion spool entry. Reused durable keys or sequences with different bytes
fail instead of overwriting history.

Committed catch-up events are SHA-256 verified, atomically stored, and only then
advance the companion reconnect cursor. A stream restart is therefore transport
recovery, not an alternative source of truth.

## Companion/game bridge

When configured, the existing companion background service waits for a valid
game named-pipe session, reads runtime/catalog capabilities, and connects to the
campaign service. A primary assignment must be globally paused and match the
required runtime and catalog fingerprints. The companion then uses the existing
typed game commands to:

1. pause the tactical session;
2. arm the session-scoped owned-mutation capability;
3. materialize the assigned Knox with assignment/epoch/revision/idempotency fences;
4. capture a portable checkpoint;
5. upload its exact bytes to the authenticated content-addressed HTTP endpoint;
6. publish the checkpoint metadata through the durable gRPC spool; and
7. report preparation readiness and the realized revision/hash.

An ordered service movement command crosses gRPC, companion, named pipe, and
the game peer, then returns as a deterministic-ID typed tactical event. Authority
fences first capture and upload a final checkpoint, then map to owned-object
suspension using the same epoch/revision controls.

## Canonical authority lifecycle

The campaign coordinator journals `Preparing`, `Active`, `Quiescing`, and
`Released` assignment transitions under an authority epoch. A preparing entity
remains operational until the companion publishes a checkpoint and readiness;
an active or quiescing primary is frozen in strategic simulation while all
unrelated entities continue. Fenced tactical state carries stable identity,
revision, position, heading, telegraph, resources, damage, and lifecycle into
canonical state. Quiesce captures one last content-addressed checkpoint,
suspends the game object, releases the assignment, and restores Operational
fidelity.

Reconnect replay is idempotent. Stream sequence numbers are assigned only by
the single response writer, historical catch-up is bounded to the head captured
at connection, and later live commits remain queued behind it. Replayed
prepared/checkpoint/movement frames cannot repeat canonical effects. Logical
checkpoint retries retain their originally fenced commit position.

The companion durably stores each complete assignment baseline. The final
checkpoint proposal uses that baseline to convert Sea Power fuel and magazine
fractions into absolute canonical resources, and carries kinematics, telegraph,
damage, and destruction state in the same fenced event. The coordinator commits
that state before `Released` and Operational fidelity. The broad story proves a
round trip from 1,000,000 fuel / 500,000 stores / zero damage to 800,000 fuel /
300,000 stores / 250,000 damage without disturbing identity or revision fences.

A second combat handoff deliberately performs no intervening tactical movement,
then publishes a final checkpoint at the same game revision as its initial
checkpoint. Durable spool keys and blob-reference identities incorporate the
checkpoint content hash, so both captures coexist and exact reconnect replay is
still idempotent. The final proposal carries terminal destruction and 1,000,000
damage into canonical state before the assignment becomes `Released`; terminal
entities may return only to Operational fidelity, not to another tactical state.

A deliberately incompatible catalog fingerprint produces a durable negative
preparation report, issues no materialization commands, and leaves the
assignment in `Preparing` until the bounded operational fallback releases it.

The crash-boundary continuation uses two replacement game-pipe peers. In the
first boundary, the fallback Knox applies an update locally and its simulated
game process disconnects before returning the command response. The companion
therefore cannot construct or spool a tactical proposal; after it reconnects,
the inbound durable spool contains no event for that idempotency key and the
canonical entity retains its prior coordinate. Its already committed initial
checkpoint remains available to the deadline resolver. In the second boundary,
a new Knox materializes and the game disconnects while capturing its first
checkpoint. The assignment remains Preparing, has no checkpoint reference, and
is released at the same fixed deadline with the explicit
`operational-resolution-without-worker-checkpoint` report.

The broad story also hands off a second Knox, waits for its initial checkpoint,
stops the companion, and advances directly to its 120-quanta deadline. The
assigned entity stays frozen until the deadline, unrelated simulation continues,
and the coordinator then releases it at the bound using the durable checkpoint;
no tactical assignment can remain indefinitely suspended or active.

## Broad integration evidence

The existing single companion integration story now starts a real campaign
service process in addition to the real companion and mock game peer. It:

- creates and bearer-authenticates a schema/algorithm-pinned campaign;
- negotiates an authenticated generated gRPC duplex stream;
- bootstraps a two-region canonical theater and queues a primary Knox handoff;
- observes real pause/arm/materialize/checkpoint game commands;
- verifies checkpoint upload and authenticated byte-identical download;
- verifies prepared/checkpoint frames were durably spooled before acknowledgement;
- proves the assigned entity freezes while an unrelated entity and theater time
  advance;
- sends an ordered movement, receives its typed tactical event, and verifies
  canonical position, heading, and telegraph;
- hard-kills and restarts the campaign service; and
- proves the companion reconnects as a new service session from its durable
  cursor, safely replays unacknowledged outbound frames, captures a final
  checkpoint, and releases the entity back to Operational fidelity;
- rejects a catalog-mismatched assignment without touching game state;
- captures and commits a destroyed combat entity before releasing authority;
  and
- proves initial/final checkpoints at the same game revision remain distinct,
  durable, and replay-safe;
- crashes a game peer after local mutation but before proposal creation and
  proves the local-only state never reaches the canonical journal; and
- crashes a replacement peer before its first checkpoint and proves bounded
  no-checkpoint Operational resolution.

The story then continues across the pre-existing authenticated HTTP, named-pipe,
catalog, UI-automation, scenario, controlled-tick, save/load, mutation lifecycle,
and port-projection surfaces. There remains one broad protocol test rather than
separate transport unit tests.

Verification:

```powershell
pnpm test
pnpm build
```

## Exit gate disposition

| P4 condition | Evidence | Result |
| --- | --- | --- |
| Theater time and unrelated entities continue | Live advance `0 -> 30` froze the tactical entity while an unrelated revision advanced `1 -> 7`; broad story covers the same boundary. | pass |
| Identity, revisions, time, damage, fuel, and stores round-trip | Real Knox IDs/revisions/kinematics round-trip live; broad checkpoint story carries `800,000` fuel, `300,000` stores, `250,000` damage and terminal destruction. | pass |
| Replay is externally identical | Service hard restart and outbound replay retain one canonical result and checkpoint identity. | pass |
| Unacknowledged local outcome is not durable | Game-peer crash after local movement produces no tactical proposal and no canonical coordinate change. | pass |
| Player-visible timeline has an explicit contract | Native weapon visibility was observed in 190.2 ms with unchanged campaign head; authenticated policy exposes display-first provisional ordering and rollback. | pass |
| Operational fallback is bounded and reported | Deadline resolution reports the last checkpoint/hash/cursor and a 120-quanta maximum loss; no-checkpoint recovery is explicit. | pass |
| Suspended islands cannot remain forever | Active, Quiescing, and failed Preparing assignments all release normally or at the 120-quanta bound; the live run ended with all assignments Released. | pass |

Phase 5 operational simulation and dynamic fidelity is the next slice.
