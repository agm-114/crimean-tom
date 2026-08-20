# Phase 3D replay and invariant gate evidence

Date: 21 August 2026

## Disposition

Phase 3D and exit gate P3 are complete. Phase 3 now has one broad synthetic
theater story rather than a collection of narrow unit/property tests.

## Long-horizon replay fixture

The three-region, two-coalition theater advances from campaign time 0 through
600. After the initial ordered transit, eighteen further simulation commands
advance in thirty-quantum intervals. Every command is independently decided
from identical state with one worker and eight workers. The complete committed
event bytes and canonical state bytes match at every interval.

The final version-2 canonical golden hash is:

```text
214b50d37e85e413d2fa665a71bb0bb2e56537e9732e1d8d03a3593e947bb395
```

That complete command/event history is appended through the fenced SQLite
journal. Recovery starts at snapshot commit 10, verifies and applies the journal
tail, and produces the pinned final hash. The same final hash is verified after
atomic database-plus-blob backup and restore.

## Invariant coverage in the broad story

At every long-horizon advance the story runs whole-state validation and proves:

- campaign head and campaign revision remain identical;
- all fixed-point resources are non-negative;
- total fuel, stores, spares, personnel, and munitions never increase across
  movement, consumption, base transfer, and repair;
- readiness remains within 0–1000 and damage within 0–1,000,000;
- lifecycle values and terminal-state handling remain valid;
- each entity has at most one group owner;
- every active order destination is reachable in the canonical region graph;
- every scheduled activity is uniquely keyed and strictly in the future;
- every durable secondary revision agrees with replayed event effects;
- exact command retries remain idempotent and changed reuse is rejected; and
- coalition projections never contain either synthetic classified-truth sentinel.

The final theater includes resource exhaustion, deterministic damage, lifecycle
degradation, home-port replenishment/repair, repeated AI orders, completed
orders, refreshed intelligence, and future queued work. This exercises state
transitions rather than merely checking an idle clock.

## P3 exit-gate audit

| Exit condition | Evidence |
| --- | --- |
| Multi-region theater advances without Sea Power | The external kernel advances two groups across the Odessa–Black Sea–Bosporus graph through time 600 with Sea Power stopped. |
| Snapshot plus journal replay has identical hash | Snapshot 10 plus the complete journal tail recovers the pinned `214b50…bb395` hash before and after backup/restore. |
| Kill at every persistence boundary | Fourteen fresh child writers are hard-killed across append, snapshot, blob, backup publication, and lost-ack boundaries; recovery selects one head and retries have exactly-once effects. |
| Two coordinators cannot both write | An independent lease-holder process excludes the test coordinator; after death/expiry a higher epoch fences the stale handle. |
| Scheduling-independent result | One-worker and eight-worker event/state bytes match at every long-horizon advance. |
| Fog projections exclude true state | Initial, simulated, and every long-horizon blue projection exclude both classified sentinel strings and the classified field name. |

## Verification

```powershell
pnpm test
pnpm build
```

Phase 4 is now the active phase.
