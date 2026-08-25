# Interaction islands and locality handoff

An interaction island is the smallest set of campaign entities that must share
one simulation authority at a given moment. It is not a task force, formation,
handoff batch, or map cluster.

## Membership

The service reconstructs each active island from:

- durable non-Operational assignments on the same Sea Power worker;
- host-child relationships, such as a ship and its embarked aircraft;
- complete handoff batches; and
- live runtime relationships marked unsafe to split, including launched
  weapons and other coupling observations.

`CampaignInteractionIslandRegistry` computes connected components over those
edges. Durable assignments remain the source of authority truth. Runtime
coupling observations can join existing assignments into a larger island, but
cannot create authority by themselves.

A handoff batch is only an atomic preparation or fence transaction. Several
complete batches may belong to one island after independent map promotions.
Code must not infer island completeness from one shared `BatchId`.

## One native locality

Sea Power may host one active native locality inside its floating-origin
envelope. Theater-wide map records are not native locality members and remain
visible across a rebase.

Camera focus is allowed to move the floating origin. When the requested entity
is outside the current locality, the sequence is:

1. identify the complete active island;
2. pause only the short tactical mutation safe point;
3. quiesce new native actions and capture final checkpoints;
4. commit the checkpoints and fence the old authority epochs;
5. unload or suspend every old native object, including authored mission
   objects that cannot remain valid at the new origin;
6. relocate the origin and wait for terrain readiness;
7. prepare and activate the new island; and
8. replace its selected presentation proxy with the native object.

Unloading is expected streaming behavior. No authored or campaign unit is
permanently resident merely because it was loaded first. A failed handoff keeps
the old authority decision and the selected map record; it must not leave two
active native localities.

## Authority and pause invariants

- A campaign entity has at most one active primary tactical publisher.
- Every mutation carries campaign identity, entity identity, authority epoch,
  expected revision, and an effect idempotency key.
- Pipe session identity is transport state and is excluded from effect replay
  comparison. Reconnect does not make the same mutation a new effect.
- The service continues advancing entities outside the leased islands.
- Tactical pause is serialized by one companion gate and is restored after the
  safe point. Mutation arming never leaves the battle paused.
- Eligible heartbeats renew the tactical lease. Campaign pause and tactical
  pause remain separate controls.

## Selection and projection

A direct Sea Power map click sets the campaign symbol's native
`IMapSelectable.Selected` property, which emits a typed selection event to the
companion. The viewport hit-radius hook handles presentation wrappers only. One
promotion orchestrator owns retry, idempotency, native-readiness gating, focus,
and locality-handoff state. A slow reconciliation poll exists only to repair a
lost event or reconnect.

Projection generations are scoped to a game-pipe session. The first revision
and any resynchronization use a full snapshot. Normal revisions contain only
record upserts and explicit removals. The bridge never replaces projection work
that is already applying; it reports busy until the frame-budgeted apply
finishes. Control traffic retains priority over projection traffic.
