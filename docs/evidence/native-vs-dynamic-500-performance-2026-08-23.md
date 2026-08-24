# Native versus dynamic 500-entity performance, 23 August 2026

## Result

The dynamic campaign kept Sea Power at effectively real time while the
all-native 500-unit control did not. On Sea Power `0.8.2-23444+s358`, the
dynamic side averaged 60.001 FPS and advanced tactical time at 0.9997 seconds
per wall second. The native control averaged 23.731 FPS and advanced tactical
time at 0.7571 seconds per wall second.

Sea Power's configured internal `GameTime.TimeDilation` remained `1.0` in both
runs. The native slowdown was therefore not an intentional time-dilation
setting. It was failure to keep up with wall time under load.

## Matched method

Both captures used fresh Sea Power processes, unpaused 1x tactical time, the
same game build, 30-second windows, three-second intervals, and ten samples.
The paired key was `ww3plus-500-entity-qualified`.

The baseline loaded `Crimean Tom - Native Campaign Benchmark - 0500 Units` and
let Sea Power physically simulate the generated 500-unit subset. The candidate
loaded the four-authored-unit `Fulda Gap Gone Hot - GIUK Dynamic Campaign
Window` shell while ServiceHost advanced an exact 500-entity WW3++ campaign.
One campaign-owned Knox was under tactical authority and 11 other campaign
Knox records appeared as visible, non-simulated proxies. This is a system-level
comparison of the two intended architectures, not a claim that 500 native
objects and 12 visible tactical objects perform equal work inside Sea Power.

The bounded campaign fixture retained the full seed's 55 regions and 81 bases.
Its 500 entities comprised 157 maritime, 63 fixed-wing, 13 rotary-wing, 50
logistics, 131 ground, 70 installation, and 16 abstract-land records. It also
contained 370 groups, 287 opening orders, 22 civilian-traffic entities, 93
fixed sites, and 41 resident strategic systems. The subset selection is stable
across campaign IDs and always retains the supported Knox tactical/proxy set.

## Measurements

| Metric | 500 native units | Dynamic 500 + 11 proxies + 1 tactical | Change |
| --- | ---: | ---: | ---: |
| Average FPS | 23.731 | 60.001 | +152.838% |
| Minimum FPS | 19.098 | 59.998 | +214.159% |
| p95 main-thread snapshot | 95,256 us | 2,286 us | -97.600% |
| Engine fixed timestep | 33.3333 ms | 16.6667 ms | 30 Hz to 60 Hz |
| Sea Power game fixed timestep | 33.3333 ms | 16.6667 ms | 30 Hz to 60 Hz |
| Internal time dilation | 1.0000 | 1.0000 | unchanged |
| Tactical seconds / wall second | 0.7571 | 0.9997 | +32.043% |
| Average observed Sea Power objects | 513.0 | 12.6 | context |
| Average visible campaign proxies | 0 | 11 | context |
| Sea Power normalized CPU | 8.190% | 2.908% | -64.493% |
| Sea Power working set | 8,080.125 MiB | 5,104.207 MiB | -2,975.918 MiB |
| Companion normalized CPU | 0.072% | 0.186% | +0.114 pp |
| Companion working set | 96.461 MiB | 170.066 MiB | +73.605 MiB |

The native control needed approximately 1.3208 wall seconds to produce one
tactical second. Equivalently, it ran 24.29% behind real time. The dynamic
candidate needed approximately 1.0003 wall seconds per tactical second.

Campaign advancement remained parallel to the tactical session throughout the
candidate capture: 10.0037 campaign quanta and 1.0004 durable commits per wall
second. No tactical sample was paused. ServiceHost used 1.084% normalized CPU,
189.297 MiB working set, and 111.301 MiB private memory during the window.

## Fixed-timestep interpretation

The pinned Sea Power build's `GameTime.Update()` selects a 0.0333333-second
fixed timestep after measured FPS falls below 30, and restores a
0.0166667-second timestep after FPS rises above 40. The observed native and
dynamic values exactly match those adaptive branches. `GameTime.TimeDilation`
is a separate multiplier and remained 1.0; it does not report wall-clock
slowdown when the engine cannot execute scheduled work fast enough.

## Qualification boundary and 4K target

This is a short local POC benchmark, not a soak or a maximum-capacity claim.
Only the 500-unit native scenario was considered safe to execute. The 1,000,
2,000, and full-4K native files remain manual stress controls and were not run.

The product architecture is still designed around the 4,096-entity dynamic
campaign. The earlier full-campaign proof held that backend at approximately
60 FPS with 12 visible proxies, and separately diagnosed the large-event replay
memory issue. The present test adds the missing matched 500-scale control and
explicit fixed-step/internal-dilation telemetry; it does not replace the 4K
qualification in
[Full dynamic campaign sync and performance proof](full-dynamic-campaign-sync-performance-2026-08-23.md).

Local raw artifacts are retained under `.run/performance`:

- `vanilla-native-500-qualified.json`
- `dynamic-offloaded-500-qualified.json`
- `vanilla-vs-dynamic-500-qualified.json`
