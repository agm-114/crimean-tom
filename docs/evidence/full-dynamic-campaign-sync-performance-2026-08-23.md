# Full dynamic campaign sync and performance proof — 23 August 2026

## Scope

This record qualifies the trusted-local POC against Sea Power
`0.8.2-23444+s358` and the complete Fulda Gap Gone Hot proving campaign, not the
one-unit worker shell. The campaign contains 32 regions, 31 bases, 60 entities,
and 34 groups. Sea Power loaded the authored
`Fulda Gap Gone Hot - GIUK Dynamic Campaign Window` mission with four native
combat units. CI was unavailable because repository compute minutes were
exhausted; all verification was local.

## Full-campaign handoff

- A fresh full campaign scheduled its authored USS Pharris entity from
  Operational to desired Tactical after player-control interest.
- The connected Sea Power worker materialized one owned Knox, published its
  checkpoint, and reached a Primary `Active` assignment with `leaseLive: true`.
- During an explicit seven-second active-assignment sample on campaign
  `d78a440bdd3740078261c01bd85f5381`, campaign time advanced from 570 to 620
  quanta while the assignment remained Active and lease-live. Sea Power was
  unpaused. The canonical `resolveByQuanta` remained the preparation deadline;
  live ownership was renewed by the connected heartbeat/session lease rather
  than rewriting that historical field.
- Removing interest first produced `scheduler-hysteresis-hold`; a stable second
  sample selected `active-island-outside-player-interest`. The companion
  checkpointed and suspended the native owned record, authority became
  `Released`, and the canonical entity returned to Operational while campaign
  advancement continued.
- The presentation provider then reported a visible model and map marker with
  `isSimulated: false`, `hasNativeSimulationObject: false`, and no game unique
  ID. Promotion and demotion never left two simulation authorities.

## Runtime usability

`focus-camera-on-unit` now accepts either the canonical proxy entity ID or its
stable proxy ID. On campaign `0c2316474d8b460da226a8f76269b051`, typed focus
returned `camera-focused-on-visual-render-proxy` while the proxy was visible,
the assignment was Released, and Sea Power remained unpaused. The pinned
camera-focus seam verified `switchToPivot`, `isAttachedToPivot`, and
`getPivotObject` before enabling the action. A typed screenshot materialized at
Sea Power's `_CrimeanTomScreenshot.png`; dark weather and inherited zoom made
the 3D silhouette subtle, so player-facing zoom framing remains product work.

## Defect found and fixed

The first scheduler request against an already advanced full campaign returned
HTTP 500. The captured exception was `terminal-entity`: destroyed and retired
entities still participated in scheduler interaction islands, so an unrelated
policy update tried to demote a destroyed Operational entity to Strategic.
Interaction-island construction now excludes terminal entities while retaining
them in canonical campaign history. The exact request against the same campaign
then returned HTTP 200 and scheduled only live entities.

## Paired system profile

The retained artifacts are:

- `.run/performance/vanilla-giuk.json`
- `.run/performance/dynamic-full-campaign.json`
- `.run/performance/vanilla-vs-dynamic-full.json`

Both sides used fresh Sea Power processes, the same mission, unpaused 1x
tactical time, ten samples over 20 seconds, and Sea Power build
`0.8.2-23444+s358`. The candidate used isolated campaign storage, campaign
`5ce3e137f15c4328bba50664920c34f7`, all 60 campaign entities, and exactly one
visible non-native proxy in every sample. Campaign setup was paused only to
stabilize the disposable fixture, then resumed before the timer; campaign time
advanced at 9.9165 quanta per wall second and tactical time at 0.9981 seconds
per wall second during capture.

| Metric | Vanilla GIUK | Full campaign + one proxy | Delta |
| --- | ---: | ---: | ---: |
| Average FPS | 60.003 | 60.001 | -0.003% |
| Minimum FPS | 59.997 | 59.998 | +0.002% |
| p95 main-thread capture | 2,191 us | 2,248 us | +2.602% |
| Tactical seconds / wall second | 0.9998 | 0.9981 | -0.170% |
| Sea Power normalized CPU | 2.753% | 2.745% | -0.291% |
| Sea Power working set | 5,078.867 MiB | 5,125.121 MiB | +46.254 MiB |
| Companion normalized CPU | 0.141% | 0.603% | +0.462 percentage points |
| Companion working set | 96.477 MiB | 540.117 MiB | +443.640 MiB |

The candidate ServiceHost, isolated from the normal development store, used
1.505% normalized CPU and 671.613 MiB working set while producing 0.9916
commits per wall second. Rejected trial campaigns in that isolated store were
paused, but their persisted history still makes this a system figure rather
than a cold single-campaign allocation measurement. Vanilla has no ServiceHost,
so this is reported as candidate system cost rather than a delta.
The candidate averaged 8.3 observed game objects versus 6.2 vanilla and one
visible proxy versus zero; the comparison is intentionally not unit-count
normalized.

## Qualification boundary

This is a 20-second POC profile, not a soak, scale envelope, or parity claim.
Rejected trial runs demonstrated why proxy visibility is a validity condition:
Operational combat can destroy or make the entity unavailable, at which point
the presentation record is correctly hidden. The retained run kept one proxy
visible for every sample. Companion memory is materially higher and needs a
longer allocation/retention investigation. The proof still covers one
Knox-class surface profile, no hostile/contact proxy, no campaign-owned hostile
outcome, and no player-facing campaign selection/results flow.
