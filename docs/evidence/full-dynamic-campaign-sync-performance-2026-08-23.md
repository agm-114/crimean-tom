# Full dynamic campaign sync and performance proof, 23 August 2026

## Scope

This record supersedes the earlier 60-entity profile in this file. It qualifies
the trusted-local POC against Sea Power `0.8.2-23444+s358` and the current full
Fulda Gap Gone Hot WW3++ seed: 55 regions, 81 bases, 4,096 entities, and 919
groups. Sea Power loaded the authored `Fulda Gap Gone Hot - GIUK Dynamic
Campaign Window` mission, whose vanilla side contains four authored combat
units. CI was unavailable because repository compute minutes were exhausted;
all verification was local.

## Full-campaign tactical sync

A new isolated campaign, `62fc0d1d002d4a698d91cbe01fbd78ed`, bootstrapped
the 5,892-record full seed in 8.762 seconds. Player-control interest selected the
authored USS Pharris entity for Tactical fidelity. The first full-scale
scheduler pass committed 3,151 fidelity transitions in 24.087 seconds. The
connected Sea Power companion materialized the supported Knox profile and
reached a Primary Active assignment.

After player interest was removed, the first stable sample returned
`scheduler-hysteresis-hold` and the second returned
`active-island-outside-player-interest`. The assignment became Released, its
native simulation object was suspended, and the entity returned to Operational
authority. Presentation then exposed 12 friendly visual-only Knox proxies. All
12 reported `isSimulated: false` and `hasNativeSimulationObject: false`; no
proxy introduced a second simulation authority.

Earlier in the same 23 August functional proof, an Active assignment remained
lease-live while campaign time advanced in parallel with unpaused tactical
time. The assignment preparation deadline remained historical; connected
heartbeats renewed the live tactical lease without rewriting that field.

## Runtime usability retained from the functional proof

`focus-camera-on-unit` accepts either a canonical proxy entity ID or its stable
proxy ID. Typed focus returned `camera-focused-on-visual-render-proxy` while the
proxy was visible, its assignment was Released, and Sea Power remained
unpaused. The pinned seam verified `switchToPivot`, `isAttachedToPivot`, and
`getPivotObject` before enabling the action. Typed screenshot capture also
completed. Dark weather and inherited zoom made the 3D silhouette subtle, so
player-facing zoom framing remains product work.

The earlier scheduler proof also found and fixed a `terminal-entity` HTTP 500:
destroyed and retired entities had remained in scheduler interaction islands,
so an unrelated policy update attempted to demote a destroyed Operational
entity. Interaction-island construction now excludes terminal entities while
retaining them in canonical campaign history.

## Qualified paired profile

The retained local artifacts are:

- `.run/performance/vanilla-giuk-4096-qualified.json`
- `.run/performance/dynamic-full-4096-qualified.json`
- `.run/performance/vanilla-vs-dynamic-full-4096-qualified.json`

Both sides used fresh Sea Power processes, the same mission and game build,
unpaused 1x tactical time, a two-second interval, and exactly ten samples over
20 seconds. The capture script warmed its endpoints before timing, schedules
samples against a fixed cadence, and now rejects scenario/build, sample-count,
sample-interval, paused-clock, and non-advancing-campaign mismatches. The
candidate reported all 4,096 entities and 12 visible proxies in the qualifying
workload.

| Metric | Vanilla GIUK | Full 4,096-entity campaign + 12 proxies | Delta |
| --- | ---: | ---: | ---: |
| Average FPS | 60.001 | 60.000 | -0.002% |
| Minimum FPS | 59.998 | 59.994 | -0.007% |
| p95 main-thread capture | 2,149 us | 2,215 us | +3.071% |
| Tactical seconds / wall second | 1.0002 | 0.9822 | -1.800% |
| Sea Power normalized CPU | 2.485% | 2.793% | +0.308 pp |
| Sea Power working set | 4,968.469 MiB | 5,096.812 MiB | +128.343 MiB |
| Companion normalized CPU | 0.029% | 0.111% | +0.082 pp |
| Companion working set | 65.582 MiB | 159.125 MiB | +93.543 MiB |

During the candidate capture, campaign time advanced at 4.3692 quanta per wall
second and the service committed 0.4369 events per wall second. The isolated
ServiceHost reported 6.230% normalized CPU, 728.699 MiB working set, and
659.840 MiB private memory in that pre-fix capture. Vanilla has no ServiceHost,
so service cost is a candidate system figure rather than a delta.

## Memory and reconnect defects found

Three scale-dependent defects were isolated and fixed after the paired profile:

1. The Web SDK had enabled server GC in the companion, creating 24 GC heaps on
   the test machine. Explicit workstation GC reduced the full-campaign
   companion capture from approximately 710 MiB to 159.125 MiB working set.
2. Companion catch-up materialized the complete durable journal in one list.
   Catch-up now reads 16-event pages, bounded by the durable head captured at
   hello, and rejects gaps before entering the live stream.
3. The unattended-simulation timer called `journal.RecoverAsync()` every second
   merely to read campaign time and entity count. Recovery materializes all
   events after the latest 100-commit snapshot; each current full-state
   `CampaignSimulated` JSON event is about 8.39–10.62 MiB. The timer's async
   state retained that replay batch across its next wait, producing a sawtooth
   live heap of up to 1,204,129,355 bytes. The timer now reads the coordinator's
   durable-head-checked cached state instead. Crash recovery and unattended
   advancement remain enabled.

The large events also exposed gRPC's default 4 MiB client receive limit at
commit 9,051. Companion and Operational-worker clients now share a bounded
64 MiB service-frame limit. A fresh companion resumed at commit 9,050, crossed
the 8.39–10.62 MiB events, and reached head 9,183 without
`ResourceExhausted`. A later combined proof resumed at 9,191 and reached exact
head 9,300 with no size errors.

Forced-GC measurements against the same isolated 4,096-entity campaign were:

| Boundary | Service live managed heap | Service working set after forced GC |
| --- | ---: | ---: |
| Before unattended-cache fix, no live session | 1,204,129,355 bytes | 2,387.672 MiB |
| Fixed unattended loop, head 9,215 | 63,133,712 bytes | 332.492 MiB |
| Fixed loop after 36 more commits, head 9,251 | 63,272,194 bytes | 320.355 MiB |
| Fixed loop plus live caught-up companion, head 9,300 | 77,153,813 bytes | 390.996 MiB |

At the final live boundary the companion heap was 106,569,827 bytes and its
working set was 293.477 MiB. These forced-GC diagnostics are not a replacement
for a fresh paired performance capture; they prove that replay history no
longer accumulates in the live service.

## Qualification boundary

This is a 20-second paired profile plus a short live reconnect/memory proof, not
a soak, balance claim, or maximum scale envelope. The first 4,096-entity
scheduler pass still takes about 24 seconds and sequentially commits thousands
of fidelity transitions. Full-state simulation events are roughly 10 MiB and
should eventually become bounded deltas or referenced chunks even though they
no longer accumulate in memory. Tactical materialization remains one
authoritative Knox surface profile; ground, installation, air, rotary-wing,
subsurface, hostile/contact proxy, campaign-owned hostile outcome, and a
player-facing campaign selection/results loop remain outside this proof.
