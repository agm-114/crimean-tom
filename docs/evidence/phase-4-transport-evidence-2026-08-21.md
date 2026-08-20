# Phase 4 typed-transport evidence

Date: 21 August 2026

## Current disposition

Phase 4 is in progress. The service/companion transport, durable spools,
authenticated checkpoint transfer, assignment preparation, ordered movement,
and service-restart reconnect are working. Canonical tactical-event commit,
dematerialization/fallback, live Sea Power scenarios, and the final P4 gate are
not yet complete.

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
fences map to owned-object suspension using the same epoch/revision controls.

## Broad integration evidence

The existing single companion integration story now starts a real campaign
service process in addition to the real companion and mock game peer. It:

- creates and bearer-authenticates a schema/algorithm-pinned campaign;
- negotiates an authenticated generated gRPC duplex stream;
- queues a primary Knox assignment;
- observes real pause/arm/materialize/checkpoint game commands;
- verifies checkpoint upload and authenticated byte-identical download;
- verifies prepared/checkpoint frames were durably spooled before acknowledgement;
- sends an ordered movement and receives its typed tactical event;
- hard-kills and restarts the campaign service; and
- proves the companion reconnects as a new service session from its durable cursor.

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

The service must translate prepared assignments and tactical proposals into
fenced canonical journal events, attach checkpoint references at the committed
cursor, advance unrelated operational entities while the assigned entity is
tactical, and complete quiesce/fence/checkpoint/dematerialize fallback.
