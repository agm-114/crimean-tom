# Remote transport and local-first backend stack

## Decision

This table is the target boundary decision. The named pipe and gRPC machine
paths are implemented. The current operator monitor and coalition projection
use HTTP, and there is no SignalR hub/client or MessagePack player DTO surface
yet. See [Implemented architecture map](implementation-map.md).

Use different transports at boundaries with different requirements:

| Boundary | Default transport | Contract format | Reason |
| --- | --- | --- | --- |
| Sea Power bridge -> local companion | Current-user Windows named pipe | Bounded versioned JSON frames initially | Same-machine isolation and Unity-thread handoff |
| Companion/worker -> campaign service | ASP.NET Core gRPC | Protobuf-generated clients and services | Fully typed bidirectional machine protocol and explicit schema evolution |
| Campaign service -> player UI | ASP.NET Core SignalR | MessagePack for realtime DTOs | Browser support, groups, notifications, and managed connection lifecycle |
| Administration and bulk content | HTTPS API | JSON metadata; content-addressed binary bodies | Simple tooling and resumable checkpoint/catalog transfer |

All endpoints are hosted by the same local ASP.NET Core application in the
default deployment. This split does not require another server, broker,
container, database, or cloud service.

The local pipe negotiates pre-execution cancellation and three weighted priority
lanes: Bulk (`-100`), Normal (`0`), and Control (`100`). The companion transmits
a correlated cancel frame when its caller disconnects or its deadline elapses;
the bridge removes the command if it is still queued. Commands already executing
on Unity's main thread are not interrupted unsafely. Command and response queues
are bounded, and their weighted schedule prevents both control starvation and
permanent bulk starvation.

## Why gRPC is the canonical machine protocol

The companions and operational workers are .NET applications. They need typed
commands, events, assignments, health reports, checkpoints, resumable cursors,
and bidirectional streams. gRPC defines these in versioned `.proto` files and
generates both the C# client and server surfaces. Renaming or changing a method
or payload incompatibly therefore produces build-time failures at the adapter
boundary rather than scattered runtime method-name errors.

Protobuf also gives each field a stable number and has explicit rules for adding,
removing, reserving, and ignoring fields. That is a better fit for recorded event
history and mixed companion/service versions than treating ordinary runtime C#
object layout as the wire schema. ASP.NET Core generates client and server assets
from referenced `.proto` files as part of the build; see Microsoft's
[gRPC services with C#](https://learn.microsoft.com/en-us/aspnet/core/grpc/basics).

Bidirectional gRPC streaming is suitable for a long-lived companion session, but
it is not free reliability. A broken stream must be recreated, only one writer
may write a given request stream at a time, and application queues are still
needed. Microsoft's [gRPC performance guidance](https://learn.microsoft.com/en-us/aspnet/core/grpc/performance)
also recommends streaming only when throughput/latency justify its additional
complexity. Crimean Tom has that likely shape, but still benchmarks it before
fixing stream count or batching constants.

## Why SignalR is not the canonical machine protocol

SignalR is strongly typed in a narrower sense. A server can derive from
`Hub<TClient>` and receive compile-time checking when it invokes client methods.
That is valuable, and Crimean Tom should use it for UI notifications. The stock
.NET and JavaScript clients still register or invoke hub methods by name at the
connection edge, so SignalR does not provide the same generated end-to-end RPC
surface as gRPC. Microsoft's [strongly typed hub documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/hubs)
shows both the `Hub<TClient>` guarantee and the client handler boundary.

SignalR remains the better player-UI protocol because it:

- normally uses WebSockets while handling negotiation and fallback;
- supports browser, .NET, and JavaScript clients;
- provides users, groups, server callbacks, and streaming abstractions;
- supports automatic reconnect and, on current .NET versions, bounded stateful
  reconnect; and
- has a built-in MessagePack hub protocol for smaller binary messages.

Those facilities reduce UI/session plumbing. They do not make SignalR a durable
event bus. Microsoft's [SignalR overview](https://learn.microsoft.com/en-us/aspnet/core/signalr/introduction)
and [stateful reconnect documentation](https://learn.microsoft.com/en-us/aspnet/core/signalr/configuration)
describe the transport selection and bounded reconnect behavior.

## Comparison for Crimean Tom

| Concern | gRPC/Protobuf | SignalR/MessagePack | Crimean Tom consequence |
| --- | --- | --- | --- |
| End-to-end .NET typing | Generated client, service base, methods, and messages | Typed server callbacks; client edge still uses hub method registration/invocation | gRPC for canonical companion/worker RPC |
| Schema evolution | Stable numbered fields, reserved identifiers, generated code | Typed DTOs but serializer/member-shape compatibility must be governed separately | Protobuf for journal-facing network contracts |
| Bidirectional streaming | Native HTTP/2 duplex streaming | Bidirectional hub calls and streaming abstractions | Either works; gRPC is clearer for machine sessions |
| Browser support | Native browser gRPC unavailable; gRPC-Web lacks client and bidirectional streaming | First-class browser clients over WebSockets/fallbacks | SignalR for campaign UI |
| Reconnect convenience | Stream restart is application-managed | Automatic and bounded stateful reconnect support | SignalR improves UI experience; both resume from campaign cursors |
| Binary encoding | Protobuf | MessagePack | Both are compact enough; benchmark actual contracts |
| Broadcast/groups | Must be modeled by the service | Built-in users, connections, and groups | SignalR for player/coalition projections |
| Priority and backpressure | Application queues plus chosen stream topology | Application queues; one realtime connection by default | Bulk content stays on HTTP in both cases |
| Local operation | Hosted by local Kestrel; no external infrastructure | Hosted by the same Kestrel process | Neither conflicts with local-first deployment |

Browser constraints are decisive here: gRPC-Web does not support browser client
streaming or bidirectional streaming, according to Microsoft's
[gRPC-Web documentation](https://learn.microsoft.com/en-us/aspnet/core/grpc/grpcweb).
Rather than weaken the internal protocol to accommodate browsers, the service
publishes filtered UI projections through SignalR.

## Contract and stream shape

Start with a small generated protocol rather than a generic arbitrary-method
envelope:

- unary session negotiation and capability exchange;
- one bidirectional companion session stream whose client/server frames use
  Protobuf `oneof` variants for commands, event proposals, acknowledgements,
  leases/fences, cursors, health, destination final-checkpoint staging, staged
  readiness, and post-commit activation grants;
- bounded priority queues before the single stream writer on each side;
- explicit message IDs, campaign commit positions, assignment epochs,
  idempotency keys, deadlines, and schema versions; and
- authenticated HTTP download/upload for content-addressed checkpoints and
  catalogs, with hashes and atomic completion.

Do not open several streams merely to look more parallel. Add a separate control
or telemetry stream only when ordering semantics differ or stress tests show that
the bounded priority queue cannot protect lease/fence deadlines. Do not send
large checkpoint bodies through either the gRPC session or SignalR UI channel.

The SignalR UI surface contains filtered projections and player interaction only:

- campaign clock and map projection changes;
- authorized contact/unit updates;
- order acceptance, rejection, and progress notifications;
- player/coalition presence and health; and
- resync-required notifications followed by an HTTP snapshot fetch.

A SignalR client cannot directly publish a canonical tactical outcome. Player
orders enter the same validation, idempotency, ordering, and durable commit path
as every other command.

## Delivery and reconnect semantics

Neither transport changes the campaign's delivery model:

- delivery may be duplicated across reconnect;
- success is acknowledged only after durable commit where durability matters;
- the journal commit position and per-entity revision establish canonical order;
- a reconnect presents its last applied cursor and current assignment epoch;
- gaps are replayed or rebased from a verified snapshot/checkpoint; and
- expired epochs and sessions are fenced even if a transport reconnect succeeds.

SignalR's stateful reconnect buffer covers short UI interruptions. It is not
allowed to become the only copy of an event. gRPC stream restart similarly does
not imply that a previous write committed.

## Local-first deployment

The default package contains:

```text
Crimean Tom application (.NET 8)
|- ASP.NET Core/Kestrel
|  |- gRPC endpoint for companions/workers
|  |- SignalR endpoint for realtime UI
|  `- HTTPS API for administration and bulk content
|- deterministic campaign engine
|- SQLite WAL event/snapshot store
`- content-addressed local checkpoint/catalog directory
```

One executable may host all of these roles. A player-hosted private campaign and
a basic dedicated campaign use the same package. PostgreSQL, Redis, a SignalR
cloud service, an external object store, and Docker are not required. A packaged
.NET runtime and SQLite native dependency allow a clean machine to run it without
a developer SDK.

## Alternatives and promotion gates

- **SignalR everywhere:** rejected as the baseline because its typing is weaker
  at the client edge and its UI-oriented hub model adds no benefit to generated
  .NET worker RPCs.
- **gRPC everywhere:** rejected because browser gRPC cannot provide the required
  bidirectional streaming experience without a proxy or reduced gRPC-Web
  feature set.
- **Raw WebSockets:** deferred until SignalR or gRPC framing/dispatch is measured
  as a material bottleneck. It would otherwise recreate RPC dispatch, schema,
  reconnect, and error handling.
- **UDP/QUIC datagrams:** deferred for optional lossy presentation samples only.
  Commands, leases, commits, and checkpoints remain reliable and cursor-based.

Before the transport stack is considered proven, tests must cover generated
contract compatibility, MessagePack UI compatibility, bounded queue pressure,
stream interruption at every message phase, duplicate delivery, cursor catch-up,
expired-session/epoch fencing, large HTTP checkpoint transfer, and control
latency while bulk and projection traffic are saturated.
