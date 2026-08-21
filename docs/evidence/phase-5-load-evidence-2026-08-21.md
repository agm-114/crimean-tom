# Phase 5 operational-load evidence — 21 August 2026

## Scope

This evidence covers the game-independent Phase 5 operational kernel and its
service/journal path. It does not claim Unity tactical capacity. The probe uses
the representative Fulda Gap seed replicated into independent task-force
echelons across the same 12-region graph, then advances the canonical campaign
from T+0 to T+30 through the authenticated service endpoint.

The reusable command is:

```powershell
pnpm campaign:stress -- --ForceMultiplier 20 --TargetQuanta 30
```

The script creates a fresh campaign, calls the scaled Fulda bootstrap endpoint,
advances it, reads service-observed telemetry, and prints the canonical hash. It
does not use Sea Power or feed wall time into simulation decisions.

## Test host

- Windows 11 Education
- AMD Ryzen 9 9900X, 12 physical / 24 logical cores
- 31.1 GiB visible memory
- .NET SDK 10.0.303, target framework net8.0
- SQLite WAL journal on the local workspace volume
- Debug configuration, campaign service and Vite watcher running

## Results

| Force multiplier | Entities | Groups | Outcomes retained | 30-quanta service time | Measured rate | Recovery | Working set | Result |
| ---: | ---: | ---: | ---: | ---: | ---: | ---: | ---: | --- |
| 20x | 300 | 180 | 1,866 | 836.289 ms | 35.9 quanta/s | 142.616 ms | 654.4 MiB | Passes 30/s healthy target |
| 40x | 600 | 360 | 3,891 | 1,621.916 ms | 18.5 quanta/s | 275.517 ms | 690.3 MiB | Below healthy target; overloaded envelope |

The 20x run ended at canonical hash
`efb606a641702db376e6d7848abbe0c82518bb78c3632b19a76329d4914a60fc`.
The 40x run ended at
`44b3be4cf7d049257d6e59c186020662e9bf21cbe05103f17f852bb03a4805d1`.
Replay/hash determinism remains covered by the broad campaign integration story;
hashes differ between these campaigns because campaign identity is an explicit
deterministic seed input.

## Interpretation

The current supported operational baseline on this host is 300 entities at the
healthy 30-tick wall-time target. The 600-entity shape completes correctly with
no event loss or fixed-step change, but its 18.5/s throughput is an overload
signal: the scheduler must shed suitable islands or reduce the global target
tempo rather than claim real-time health.

Memory growth from 300 to 600 entities was modest relative to the throughput
drop, pointing to CPU/event-generation and journal/recovery work as the current
capacity limiter. The retained operational-outcome window also grows with force
density and is part of that cost.

Bootstrap took 37.660 seconds at 300 entities and 132.389 seconds at 600 because
each seed object is currently a separate durable transaction. This is an
iteration/startup bottleneck, not the measured simulation-rate gate. A future
bulk genesis transaction or snapshot import should address it without weakening
normal command durability.

## Remaining load gate

- Calibrate the exact boundary between 300 and 600 entities on more than one
  hardware class.
- Add service-driven health classification from measured headroom; current
  scheduler health remains an authenticated input displayed beside observed
  telemetry.
- Measure live Sea Power tactical island capacity, checkpoint bursts, and
  materialization/quiesce cost separately.
- Benchmark sensor/weapon/host-expanded interaction graphs before raising the
  300-entity operational baseline.
