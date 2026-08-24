# Distributed simulation is future work

The active product uses one campaign service and one local Sea Power tactical
process. The service runs Operational simulation in-process. This is enough to
prove the player loop and support the 4,096-entity design target.

Private multiplayer remains active work. It does not require distributed
Operational workers or several Sea Power processes. Multiple players can share
one campaign service while one tactical process owns a battle.

## Deferred features

The following features are not on the first-playable or private-multiplayer
critical path:

- Operational simulation split across worker processes or machines;
- hot Unity replicas that shadow a live Sea Power process;
- automatic load-driven movement of interaction islands;
- live tactical migration between Sea Power processes; and
- simultaneous tactical battles hosted by several Sea Power processes.

Existing experiments and code may remain. New product work must not depend on
them.

## Invariants to preserve

Any future design must retain these rules:

- One authority advances an entity or interaction island at a time.
- Entity identity survives fidelity, process, and machine changes.
- Every assignment carries a campaign identity, epoch, and revision fence.
- A destination cannot publish until the campaign service commits its grant.
- Checkpoints are validated before activation and are never a second timeline.
- A stale source remains fenced after disconnect or delayed delivery.
- Player and coalition projections do not expose unauthorized truth.
- Recovery favors a cold checkpoint or Operational fallback over two live
  publishers.

## When to revisit the design

Revisit distributed simulation only after measurements show that one service
cannot meet the 4,096-entity campaign budget, or after the product needs one of
these capabilities:

- concurrent tactical battles;
- a dedicated campaign host with remote Sea Power clients;
- recovery objectives that cold restart cannot meet; or
- theater sizes that exceed one service process after profiling and ordinary
  optimization.

At that point, write a new proposal from measured bottlenecks. It must specify
failure handling, capacity reserve, checkpoint portability, security, and a
player-visible benefit before implementation begins.
