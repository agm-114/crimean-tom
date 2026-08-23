# WW3++ 801-entity headless campaign evidence — 23 August 2026

## Claim

The expanded default WW3++ campaign bootstraps deterministically and advances
30 campaign quanta through the real ServiceHost/SQLite path. This is headless
campaign evidence, not a Sea Power streaming, frame-rate, or vanilla-comparison
claim.

## Setup

- branch: `codex/local-tactical-sync-poc`;
- campaign service: Debug build, isolated fresh run directory;
- HTTP/gRPC: disposable loopback ports `5181`/`5182`;
- unattended wall-clock advancement: disabled for controlled measurement;
- seed route: `POST /control/campaigns/{id}/bootstrap/fulda-gap`;
- advance route: `POST /control/campaigns/{id}/advance/30`;
- campaign ID: `f979ba428a954be5a436dd2b2ae1aaa5`.

## Committed opening state

| Measure | Observed |
| --- | ---: |
| Regions | 39 |
| Bases | 61 |
| Entities | 801 |
| Groups | 280 |
| Groups with opening orders | 213 |
| Maritime entities | 324 |
| Air entities | 112 |
| Logistics entities | 165 |
| Land/fixed entities | 200 |
| Civilian/merchant catalog entities | 90 |
| Fixed-site components | 220 |
| Tactical-eligible entities | 775 |
| Operational-only abstract land formations | 26 |

The committed bootstrap contains 1,394 region, base, entity, group, and order
events. The canonical opening hash was
`cd667b65e70a9cabe9847d19e114fdee9c9c343e7f3c1ad7a6750a89f17467ee`.

## Performance observation

| Measure | Observed |
| --- | ---: |
| Bootstrap wall time | 44,063.9 ms |
| 30-quanta advance wall time | 1,701.9 ms |
| Service advance duration | 1,667,194 µs |
| Measured campaign throughput | 17.994 quanta/s |
| Recent Operational outcomes | 430 |
| Managed memory after advance | 512.8 MiB |
| Process working set after advance | 637.8 MiB |
| Campaign time after advance | 30 quanta |
| Post-advance canonical hash | `237ee139f676404dbd2906221bd1a16315dd1a4417c6eda21ad45dd1ed96893f` |

The advance completed well inside the current 10x tactical-compression ceiling
for a 30-quanta request. Bootstrap is the immediate scale defect: every seed
event is appended as a separate durable transaction and snapshots are written
during import. A versioned, validated, atomic campaign-package import/batch path
is needed before this is an acceptable player-facing new-campaign delay.

## Live-performance status

The paired vanilla-versus-dynamic Sea Power evidence in
`full-dynamic-campaign-sync-performance-2026-08-23.md` predates this expansion
and used the former 60-entity seed. It remains a valid dated proof for that
workload but does not qualify the 801-entity campaign. The next live pass must
use the same GIUK mission and fresh-process pairing while the full 801-entity
campaign advances, then report campaign/entity/proxy counts from the capture.

