# Phase 4 typed-transport evidence

Date: 21 August 2026

## Current disposition

Phase 4 is in progress. The service/companion transport, durable spools,
authenticated checkpoint transfer, canonical assignment lifecycle, ordered
movement commit, service-restart reconnect, and checkpointed quiesce/release
are working. Portable checkpoints now carry normalized runtime fuel, magazine,
and damage fractions and combine them with the durable handoff baseline before
canonical release. Catalog mismatch now fails closed before materialization,
and a destroyed tactical unit returns as canonical terminal damage before its
assignment releases. Game-process crashes before the first checkpoint and after
a local mutation but before its proposal are covered. Live Sea Power scenarios,
outcome-timeline policy, and the final P4 gate are not yet complete. Tactical
assignments have a fixed 120-quanta maximum interval: if a
worker does not return, the coordinator resolves at that exact campaign time,
releases authority, restores Operational fidelity from the latest committed
checkpoint, and reports the checkpoint cursor/hash and maximum-loss bound.

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

## Next slice

Run the measured live Sea Power round trip and establish the player-visible
durability timeline policy.
