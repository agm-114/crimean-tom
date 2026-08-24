# Full-campaign live sync, parity, and distant-land focus, 24 August 2026

## Scope

This record qualifies the local player sync POC against Sea Power
`0.8.2-23444+s358` and algorithm v18 using the retained full campaign:

- campaign `20260824000000000000000000000002`;
- seed `19890911`;
- 55 regions, 81 bases, 4,096 entities, and 919 groups; and
- the authored `Crimean Tom - Tactical Worker Container` mission.

It covers player-interest scheduling, tactical-island materialization, parallel
campaign advancement under a heartbeat-renewed tactical lease, reciprocal
return to the emulator, a native/emulator movement sample, and player-visible
camera jumps between remote maritime and land windows. This is POC evidence,
not a claim of complete combat parity or a finished campaign-map UI.

## Player-interest promotion and tactical authority

The selected entity was the Ramstein small-airbase installation
`14a9c63e37db782eb6ebbb9e9bb8f574`. Its interaction island also contained the
two resident MIM-23 systems
`199efcfcc4c3a40470da358f1863f9bd` and
`edf5cec01461aea17a0e9ddae9df562a`.

The initial full-state schedule call took 365.1 ms. The airbase and both defense
systems were Active by the next 500 ms observation, and the player camera
selected the airbase. A separate USS Pharris selection took 321.9 ms to
schedule and was Active by the next 500 ms observation. Following a runtime
upgrade, the supported lease-expiry/reschedule path created authority epoch 3;
the schedule request took 396.5 ms and all three land records were Active after
two seconds, with no recovery-required objects.

At campaign time 400 the airbase assignment's nominal resolution deadline was
520. The campaign then advanced through 410 and 530 while the tactical mission
remained active:

- Ramstein stayed fixed at its authoritative tactical state: fuel 1,000,000,
  stores 750,000, damage 0, and revision 9;
- an unrelated US Atlantic frigate moved from
  `31.511970,-78.064040` to `31.516207,-78.069891`, burned fuel, accumulated
  simulated damage, and advanced from revision 42 to 56; and
- fallback resolution at time 530 returned no assignments because the
  companion heartbeat kept all three leases live past the nominal deadline.

This proves that the tactical island is paused under native authority while
the rest of the strategic/Operational campaign continues in parallel. The
campaign is not globally paused by a player tactical session.

## Reciprocal native/emulator sample

USS Pharris (`5f513ed9f5688ca800b2f13040154d9b`) was returned from its
native assignment at the exact last native coordinate. One emulator activity
from campaign time 530 to 540 then moved it 56.029 m. At 10.8 knots the nominal
ten-second distance is 55.556 m, a ratio of 1.0085 and a difference of 0.85%.
Fuel fell from 998,940 to 998,920 milliunits, stores from 749,735 to 749,730,
damage stayed at 50,000, and revision advanced from 84 to 85.

For comparison, the controlled native experiment requested 300 logical
updates at `0.033333335` seconds:

| Boundary | Observation |
| --- | ---: |
| Native logical-time change | exactly 10 seconds |
| Game updates / physics fixed updates | 300 / 150 |
| Internal time dilation | 1 |
| Native Pharris movement | 16.153 m |
| Nominal 10.8-knot movement | 55.556 m |
| Native diagnostic / nominal ratio | 29.08% |
| Emulator movement | 56.029 m |
| Emulator / nominal difference | 0.85% |

The native controlled hook under-stepped physics at one fixed update for every
two logical updates, so its 16.153 m result is diagnostic rather than a valid
ten-second movement golden. The emulator is calibrated to physical speed, not
to that incomplete hook. More native observation batches remain necessary for
air, rotary-wing, subsurface, sensors, weapons, and damage.

## Distant land-window regression

The live defect was reproduced by first focusing the GIUK placeholder vessel.
Sea Power's active terrain center became `(817,141)`, while the already loaded
Hawk tile remained approximately 2,016 km away. Before the fix, the terrain
adapter treated any loaded land tile as ready even when it was outside the
active terrain window, allowing the camera to show a land unit against the
current ocean scene.

The bridge now requires the target tile to be inside the active terrain window.
Land focus takes a short native pause safe-point when tactical time is running,
relocates the window, focuses the unit, and restores the prior running state.

The final freshly built and deployed runtime produced:

| Player action | Result |
| --- | --- |
| GIUK vessel focus | center `(817,141)`, simulation still running |
| GIUK to German MIM-23 | 82.9 ms, HTTP 200 |
| MIM-23 focus postcondition | `camera-focused-on-land-unit-with-terrain-window-prepared` |
| MIM-23 terrain | tile `(935,196)`, finalized land, 228.536 m |
| MIM-23 post-focus state | selected, time compression 1, internal dilation 1, approximately 60 FPS |
| MIM-23 to Ramstein airbase | 68.8 ms, HTTP 200 |
| Ramstein player view | airbase, runway, road/building geometry, and land terrain visible |

The final screenshot SHA-256 was
`4188e7dac124867f1291ce25ed58195b28e70b1ff1ce2c155db90bd0eba158b6`.
The bridge DLL loaded through AnchorChain from the deployed runtime directory,
and the build/deployed hashes matched.

## Verification and remaining limits

- Full solution build: **passed**, zero warnings/errors.
- Campaign integration suite: **11/11 passed**.
- Fresh Runtime Bridge launch and health marker: **passed**.
- Full 4K schedule/materialize/lease/return story: **passed**.
- Running-time GIUK-to-land player focus: **passed**.
- Visual Ramstein land/airbase confirmation: **passed**.

The live run also exposed a restart boundary that remains open: Unity objects
do not survive a game-process restart, but active leases and durable assignment
baselines do. Replaying an assignment across an unchanged runtime can restore a
missing object; after a bridge upgrade the old runtime fingerprint correctly
fences that replay, requiring lease expiry and a new authority epoch. This run
used the supported expiry/reschedule path and preserved the last committed
checkpoints, but automatic restart/upgrade reconciliation is still required so
a player never performs that operator workflow.

Other remaining playability gates are a first-class campaign map selection UI,
hostile tactical-island materialization and result return, airbase
inventory/sortie/turnaround, IADS/SEAD relationships, and broader native combat
parity calibration.
