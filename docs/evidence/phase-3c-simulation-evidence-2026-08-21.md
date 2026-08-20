# Phase 3C core-simulation evidence

Date: 21 August 2026

## Disposition

Phase 3C is complete. The game-independent campaign kernel now advances an
unattended multi-region theater through the same typed command/event journal
used by all other canonical mutations.

## Authoritative simulation path

`SimulateCampaignCommand` advances to a requested campaign time and resolves to
one `CampaignSimulatedEvent`. The event contains the exact entity, base, group,
order, intelligence, and future-activity state needed for deterministic replay.
The command targets the campaign revision and can only advance monotonically.

The canonical schema and algorithm pins are now version 2. Canonical snapshots
include readiness, damage, order completion, and the future scheduled-activity
queue. Strict decoding and whole-state validation cover the new fields and
relationships.

The SQLite service independently recovers the current durable head and reruns
the kernel decision before accepting any event. It rejects a caller-supplied
simulation result whose bytes differ from the canonical decision. A simulation
append transaction also advances the durable revision fence for every affected
entity, base, group, order, intelligence contact, and scheduled activity.

## Scheduled unattended theater

The engine seeds and retains a durable ten-quantum activity queue for:

- AI intent;
- graph movement;
- sustainment;
- repair;
- attrition; and
- coalition intelligence.

Each due interval is consumed in time order. Work left beyond the requested
campaign time remains in canonical state for the next advance.

Groups follow active orders one undirected region-graph edge per activity.
Groups without active orders generate deterministic patrol intent from their
current neighbors. Reaching a destination records order completion and releases
the group for later intent.

Movement consumes fuel and stores and applies operational wear. Empty fuel or
stores adds deterministic readiness and damage penalties. Terminal damage maps
to disabled/destroyed lifecycle states. Idle units at their coalition home base
draw bounded fuel, stores, and munitions from canonical base stocks; spare parts
repair bounded damage. All quantities remain fixed-point integers and are
clamped before entering canonical state.

Keyed SHA-256 draws use campaign seed, typed subject, campaign time, and purpose.
Movement work may run on one or many workers, but results enter a concurrent
collection and are merged only after sorting by stable typed IDs. AI orders,
attrition, intelligence-coordinate error, scheduled IDs, event bytes, final
snapshot bytes, and canonical hashes are therefore independent of worker
scheduling.

Coalition intelligence is refreshed from simulated contacts into observer-only
estimates. Projections expose friendly readiness/damage and estimated opposing
location without emitting either side's classified truth tag.

## Broad integration evidence

The single campaign integration story now:

1. builds two equivalent three-region theaters from different collection input order;
2. assigns a player transit order and leaves an opposing group for AI intent;
3. resolves the identical simulation command with one and four workers;
4. proves the complete committed event bytes and final canonical state bytes are identical;
5. moves the player group across both graph edges and completes its order;
6. consumes fuel/stores and changes damage/readiness;
7. moves an AI patrol through its home port, replenishes fuel, and consumes repair spares;
8. produces three coalition-scoped contact estimates and fifteen future activities;
9. proves the coalition projection contains no classified sentinel;
10. rejects a fabricated simulation event before the durable head changes;
11. appends the real simulation event to SQLite, verifies all secondary revision fences,
    snapshots/replays it, backs it up, restores it, and recovers the identical hash; and
12. reruns the Phase 3B hard-crash and competing-writer story against the extended schema.

Verification command:

```powershell
dotnet test tests/CrimeanTom.Campaign.IntegrationTests/CrimeanTom.Campaign.IntegrationTests.csproj --configuration Debug
```

Phase 3D replay/invariant gate evidence is next.
