# System architecture

## Scope

This document defines the active architecture for a local-first dynamic
campaign. The first product target is one campaign application, one campaign
service, and one Sea Power tactical process on one machine. Private multiplayer
extends the same campaign service after the local player loop works.

[Distributed Operational workers, hot replicas, and live tactical migration](../future/distributed-simulation.md)
are not active architecture.

## Product boundary

The campaign application is the product shell. It owns:

- campaign creation, loading, saving, and time controls;
- the strategic map and coalition order of battle;
- orders, objectives, intelligence, logistics, and campaign results;
- tactical-entry requests and return summaries; and
- local hosting and later private multiplayer sessions.

Sea Power is the tactical engine. It owns:

- the local battle simulation;
- control of the player's campaign-owned task force;
- local terrain, sensors, weapons, damage, and tactical AI; and
- the tactical map, augmented with campaign-backed friendly units and hostile
  tracks.

Sea Power does not own the campaign save, strategic AI, or global campaign UI.

## Active process topology

```text
Campaign application
  | HTTP now; SignalR later for realtime multiplayer updates
  v
Campaign service
  |- command validation and durable journal
  |- campaign clock and in-process Operational simulation
  |- strategic and operational AI
  |- coalition projections and player sessions
  `- SQLite WAL plus content-addressed local files
  |
  | authenticated typed companion protocol
  v
Local companion
  |
  | current-user named pipe
  v
Sea Power Runtime Bridge
  `- Sea Power tactical process
```

The companion isolates the campaign service from Unity lifecycle and thread
constraints. The Runtime Bridge performs typed game inspection and mutation on
the correct Unity thread.

## State ownership

The campaign service owns canonical campaign truth. Sea Power receives a fenced
lease for one interaction island. During that lease:

- Sea Power advances the leased entities;
- the campaign service does not advance those entities;
- the campaign service continues advancing unrelated entities; and
- presentation proxies remain non-authoritative.

Every durable mutation includes the applicable campaign, entity or island,
revision, authority-epoch, and idempotency fields. The service accepts a tactical
result once or rejects it. Transport delivery may repeat without repeating the
campaign effect.

The player API derives coalition authority from the durable local player
profile. A client does not select its own coalition or command scope in an order
request. Each player order names a friendly group, expected group revision, and
idempotency key. Reusing a key with a different payload conflicts; repeating the
same payload is a duplicate success.

The journal, not any process memory or UI cache, determines the accepted
timeline.

## Identity and representation

Each campaign entity has one stable identity across Strategic, Operational, and
Tactical fidelity. A Sea Power object, map record, and visual proxy are
representations of that identity, not new campaign entities.

The supported representation states are:

```text
campaign record
  -> map record
  -> optional visual proxy
  -> leased Sea Power object
  -> accepted checkpoint and campaign record
```

A visual proxy has no AI, sensors, weapons, collision, damage authority, or
publication rights. Promotion hides the matching proxy before the tactical
object becomes player-visible. Demotion preserves the map record.

## Campaign time and tactical leases

Campaign time advances in fixed logical quanta at a declared tempo. Sea Power
may use its supported native fixed timestep inside the tactical process.

A tactical session sends heartbeats. Healthy heartbeats renew the island lease.
The rest of the campaign continues in parallel. The service marks a session
stale after eight seconds without an eligible heartbeat, then keeps its island
fenced for a 12-second wall-clock grace period. Reconnecting with the same
campaign and client identity during grace restores the live lease. After grace,
the service fences the stale epoch and uses the last accepted checkpoint or an
explicit Operational fallback.

Loading and inspecting a campaign never advances it. A new or restored campaign
starts paused. Tactical pause and campaign pause are separate states unless a
ruleset explicitly couples them and tells the player.

## Fidelity and interaction islands

Strategic state represents theater intent and aggregated formations.
Operational state advances individual campaign entities outside Sea Power.
Tactical state uses real Sea Power objects for the local battle.

The interaction island begins with the campaign-owned player task force and
includes entities coupled by live sensor, weapon, targeting, formation,
support, or collision relationships. Sea Power receives the whole island under
one epoch. The service must not split a live weapon or sensor relationship
between authorities.

Strategic radars, high-end air defenses, and ballistic missile systems may
request persistent Tactical residency after those families are portable. Until
then, they remain present in campaign truth and on authorized maps while the
Operational model advances them.

## Map, intelligence, and proxies

The campaign application receives a coalition-filtered projection:

- all authorized friendly campaign entities; and
- only hostile or unknown tracks known to that coalition.

The Sea Power tactical map receives the same friendly continuity and filtered
contact picture. Locality does not control map visibility. Native and non-local
records must use equivalent Sea Power symbology, hover, selection, labels, and
focus behavior.

Selecting a non-local friendly can request a terrain-safe visual proxy. Selecting
a hostile track may create only a classification-appropriate or uncertainty
proxy at the estimated track position. It must not load secret true-state data.

Map density is controlled through level-of-detail updates, clustering, and
bounded refresh rates. Removing a physical object never removes its authorized
map record.

## Tactical entry

Tactical entry is a transaction:

1. The campaign application requests entry for a task force or location.
2. The service computes the interaction island and reports unsupported state.
3. The service fences the island and issues an epoch-bound lease.
4. The companion prepares terrain, campaign-owned objects, map records, and
   portable state.
5. The bridge validates identity and placement and captures an initial
   checkpoint.
6. The service activates the lease, then Sea Power gives control to the player.

Failure before activation leaves the service authoritative. Terrain-backed
objects remain hidden until loaded terrain has been sampled and placement is
valid.

## Tactical return and recovery

Normal return quiesces new actions, captures a final checkpoint and typed
outcomes, and commits them once. The result includes entity fate, damage,
position, fuel, stores, ammunition, hosted assets, and other fields supported
by the portable-state contract. Unsupported fields are reported.

If Sea Power exits unexpectedly, the service keeps the last accepted checkpoint
and waits through the lease grace period. A reconnect with the same session may
resume only under a current epoch. Otherwise the service fences the old epoch
and recovers the island from the accepted checkpoint or Operational fallback.
No recovery path may leave two publishers.

Clean application exit, game restart, and service restart are active recovery
requirements. Hot failover to another running Sea Power process is not.

## Persistence

The default deployment uses SQLite WAL and content-addressed local files. The
store contains commands, events, campaign heads, snapshots, checkpoint
references, ruleset identity, and content fingerprints.

Snapshots accelerate recovery but do not replace ordered history. A successful
write requires compare-and-swap validation against the current head and writer
epoch. Backup writes to a new verified location. Restore never mutates a live
journal in place.

Save formats and campaign algorithms are versioned. An incompatible save stays
intact and receives a clear migration, compatible-version, or export path.

## Strategic and operational AI

Each coalition has a strategic director that chooses objectives, allocates
forces, manages reserves and sustainment, and replans from coalition
intelligence. Domain controllers turn that intent into bounded naval, air,
ground, air-defense, strike, escort, and logistics packages.

AI commands enter the same validation and durable commit path as player
commands. Fixed installations cannot receive movement orders. Player-controlled
forces remain subject to the selected control policy.

The active design keeps all AI and Operational ticks in the campaign service.
Process distribution is an optimization, not a product requirement.

## Local application and private multiplayer

The local application initially hosts one player session and the campaign
service. The same API later supports invited private players.

Multiplayer adds:

- durable users, invitations, revocation, and coalition roles;
- reconnectable player sessions and control rights;
- realtime filtered projections and command results;
- conflict handling for shared units and task forces; and
- TLS and rate limits when traffic leaves loopback.

Realtime delivery is not canonical history. Every client resumes from a
campaign cursor or fetches a verified projection snapshot. Player processes do
not receive enemy true state. Public hostile-client adjudication remains outside
the private multiplayer milestone.

## Security boundary

The local named pipe is restricted to the current user. Companion, operator,
and player capabilities use distinct credentials. Browser cookies that permit
read access cannot authorize writes. Every projection is built for an explicit
coalition and role.

Runtime, catalog, ruleset, and save fingerprints fail closed at tactical
activation. Logs and screenshots remain local and must not contain reusable
credentials.

See the [threat model](../planning/threat-model.md) for current gaps.

## Performance budgets

The design target is a 4,096-entity campaign, not 4,096 Unity objects. Most
entities remain in the Operational model. The tactical process receives a
bounded interaction island, map records, and selected visual proxies.

Qualification records these populations separately:

- campaign entities;
- entities advanced in the Operational model;
- Sea Power simulated objects;
- map records;
- visual proxies; and
- active tracks and weapons.

Benchmarks report campaign throughput, Sea Power fixed timestep, internal time
dilation, frame time, memory, tactical-entry latency, proxy latency, and result
commit latency. The comparison baseline is a smaller all-native Sea Power
scenario with matching force composition where possible.

## Active exclusions

The active architecture does not require:

- distributed Operational workers;
- hot Unity replicas;
- live tactical migration;
- PostgreSQL, Redis, brokers, containers, or cloud services;
- whole-theater ground combat in Sea Power; or
- public adversarial multiplayer.

The archived experiments remain available at Git tag
`archive/distributed-workers-before-local-trim-2026-08-24`. They do not block
the local player loop.
