# E1a two-process divergence sampler — 22 August 2026

## Scope

Read-only sampler for the adversarial-review experiment **E1a** (two Sea Power
processes, same scenario, compare clocks and canonical hashes over wall time):

- `scripts/sea-power-divergence-sample.ps1`
- pnpm wrapper: `pnpm worker:divergence-sample`

World snapshots now include engine `timeScale`, `fixedDeltaTime`,
`unscaledDeltaTime`, and a measured FPS (`WorldSnapshot` schema 12). Those
clocks are **not** mixed into `CanonicalSha256`, so frame-rate noise cannot
look like a simulation hash mismatch.

Schema 12 also carries the normalized scenario ID/path. The sampler requires
distinct process/session identities, matching ready runtime/content
fingerprints, the same tactical scenario and clock mode, and non-empty hashes.
Sequential request/capture skew is recorded.
Hash agreement or disagreement is classified only when the samples are within
the configured logical-time delta; otherwise the run records clock drift and
exits `2` without pretending the hashes describe the same simulation moment.

## Preconditions

Two named workers already passing `pnpm worker:live-proof`, each with a loaded
tactical scene. The interesting comparison is the **same authored scenario**
(for example Red Sea Breakout) at the same pause/compression setting. Sampling
two unrelated missions only proves the hashes differ.

## Command

```powershell
pnpm worker:divergence-sample -- `
  -SourceWorkerId worker-a `
  -DestinationWorkerId worker-b `
  -SampleCount 8 `
  -SampleIntervalSeconds 5 `
  -MaximumComparableSimulationTimeDeltaSeconds 0.05 `
  -EvidencePath .run/evidence/e1a-divergence.json
```

Suggested later runs, still on this host: 1×, 10×, 100×, and one worker
CPU-throttled below 30 fps. Cross-machine samples are only useful if
same-machine divergence is small.

## Claim status

**Hot-shadow failover remains unsupported.** A logically comparable mismatch is
enough to keep that non-claim. A raw mismatch at different simulation times is
not a divergence result. A match is not D0–D5 adapter validation and is not a
recovery-time objective.
