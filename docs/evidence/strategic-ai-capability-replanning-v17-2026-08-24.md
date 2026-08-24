# Strategic AI capability allocation and replanning — algorithm v17

## Scope

Algorithm v17 improves the two-sided campaign director in two related ways:

1. a group prefers detected threats its snapshotted Operational weapons can
   actually engage; and
2. an 80-quanta commitment now expires as designed instead of being renewed
   forever by the 40-quanta director cadence.

The response allocator derives air, surface, subsurface, logistics-as-surface,
and land response classes only from coalition-visible probable or confirmed
intelligence. It cannot inspect hostile true state. Per-objective response-role
counts favor a missing or least-covered capability, allowing complementary air,
ASW, surface, and land responders instead of treating the first group as a
complete package.

The affinity is a preference, not an authority rule. Fixed complexes still
defend locally, and an imperfect group can retain a fallback objective when no
capable responder exists. This is coordinated force allocation, not yet an
explicit strike-package object or mission planner.

## Immortal-commitment defect

Before v17, a commitment created at director time `t` expired at `t + 80`, but
the next director pass at `t + 40` rewrote it to `t + 120`. Repeating that rule
made every compatible opening assignment permanent. This explained why an
initial full-scale capability-aware run produced byte-for-byte equivalent
allocation statistics: classifications became useful after the director had
already locked the theater.

The director now preserves the original expiry while a commitment is sticky.
At the deadline it scores a new objective and starts a new bounded commitment.
The broad integration story proves three behaviors together:

- a surface-capable group chooses a farther known surface contact over a closer
  air contact;
- changing the same group to an air-only capability chooses the air contact;
- an existing surface commitment remains unchanged at the intermediate director
  pass, then changes objective at its original deadline rather than being
  silently extended.

## Matched 4K comparison

Both sides used campaign ID `20260824000000000000000000000002`, seed
`19890911`, the same 55-region/81-base/4,096-entity/919-group opening state, and
forty separate ten-quanta advances. The v16 executable came from commit
`681f99f`; v17 used the current feature branch. The metric evaluates every
non-fixed `Defend` or `Interdict` commitment with at least one probable/confirmed
contact in its response area and asks whether any group weapon targets one of
the visible contact classes.

| At campaign time 400 | Algorithm v16 | Algorithm v17 |
| --- | ---: | ---: |
| Evaluated threat-response commitments | 101 | 229 |
| Capability-matched | 75 | 219 |
| Capability-mismatched | 26 | 10 |
| Armed but wrong capability | 12 | 4 |
| Match rate | 74.3% | 95.6% |
| Active referenced orders | 770 | 762 |
| Fixed groups with active movement orders | 0 | 0 |
| Strategic objectives | 125 | 125 |
| Recent Operational outcomes | 527 | 527 |

The v17 matched set includes 57 air, 40 maritime, 14 rotary-wing, 8 mixed
ground/abstract-land, and 3 ground Interdict responders: 122 matched Interdict
responses plus 97 matched defense responses. It does not count fixed
installations, whose local-defense commitment is mandatory rather than
capability-selected. The evaluated populations differ because the allocator
changes which groups occupy threat-bearing objectives; the absolute mismatch
count therefore matters alongside the percentage and falls from 26 to 10.

The final v17 clean bootstrap took 8,424.7 ms. Forty ten-quanta advances averaged
598.6 ms, with a 1,343.8 ms maximum and 502.3 ms final cycle. After service stop,
the journal recovery portion of a cold view at commit 5,932/time 400 took
3,056.4 ms (4,030.6 ms end-to-end) and returned 4,096
entities, 919 groups, 762 active referenced orders, no completed or unreferenced
retained orders, and canonical hash
`c181da6cad3cf0e9d8eb554313f34d010dd85fe93857670400d883697e8b0041`.

## Limits

The result proves deterministic, intelligence- and capability-aware bounded
replanning. It does not prove human-equivalent command, explicit package timing,
route deconfliction, tanker/escort dependency graphs, deception, learning, or
Sea Power outcome calibration. Those remain the next strategic/operational AI
depth gates.
