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

Not every detection or map track is automatically an unsafe edge. Presentation
tracks may cross the authority boundary. Consequential sensor, fire-control,
guidance, jamming, collision, formation, host, and support relationships need a
versioned mechanic policy. The policy either joins their complete dependency
set, performs a lossless authority transfer, uses a parity-qualified boundary
adapter, or rejects the interaction. Until an adapter passes, the registry uses
the conservative same-authority rule. See
[full-fidelity server simulation](full-fidelity-server-simulation.md).

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

Suspension is the rollback boundary, not the steady-state unload policy. After
the service durably accepts the final checkpoint and releases the assignment,
its worker projection retains the latest `Released` assignment as a positive
cleanup receipt. Only that receipt permits the companion to issue an idempotent
committed-resource release. Omission from a short or recovering projection is
not release authority. The bridge then removes its tagged object from Sea
Power's disabled-task-force ledger, destroys it as a clean area departure
without scoring, and removes its local ledger record. A failure before service
commit retains the suspended object and checkpoint for recovery. A failure
after commit retries cleanup from the retained release receipt. Hosted-aircraft
records follow the same rule even when no separate native object exists.
If a disconnect leaves an uncheckpointed materialization behind, the same
positive receipt authorizes its transactional rollback. A detached record with
no checkpoint can then release its ledger entry without a Unity mutation.

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
- Tactical leases expire on wall clock. If the campaign is paused, unattended
  maintenance may resolve an expired lease at the current campaign quantum; it
  must not advance campaign time to do so.

## Selection and projection

A direct Sea Power map click sets the campaign symbol's native
`IMapSelectable.Selected` property, which emits a typed selection event to the
companion. The viewport hit-radius hook handles presentation wrappers only. One
promotion orchestrator owns retry, idempotency, native-readiness gating, focus,
and locality-handoff state. A slow reconciliation poll exists only to repair a
lost event or reconnect.

Promotion does not commit when the native constructor returns. It commits after
the object becomes initialized, active, and player-orderable and the companion
captures its first checkpoint. A readiness failure rolls back the uncommitted
transaction. New objects are removed without campaign scoring. Reused retained
objects return to their predecessor checkpoint and suspended state. The same
promotion may then retry without replaying the failed native object.

Projection generations are scoped to a game-pipe session. The first revision
and any resynchronization use a full snapshot. Normal revisions contain only
record upserts and explicit removals. The bridge never replaces projection work
that is already applying; it reports busy until the frame-budgeted apply
finishes. Control traffic retains priority over projection traffic.
