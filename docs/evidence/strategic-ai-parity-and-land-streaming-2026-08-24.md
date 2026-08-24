# Strategic AI, parity calibration, and land-streaming evidence, 24 August 2026

## Scope

This record qualifies algorithm version 15 against Sea Power
`0.8.2-23444+s358`. It covers the two-coalition strategic AI and operational
sub-AI loop, full 4,096-entity recovery behavior, physical-distance movement
calibration, terrain-safe land/installation materialization, and the player
camera path for a newly promoted installation. It is a local POC qualification,
not a claim of complete Sea Power combat parity or a finished campaign UI.

The implementation checkpoints are:

- `193560f`: persisted strategic direction for both coalitions.
- `171346b`: compact field-level and compressed simulation journal events.
- `5c9ebd8`: completed strategic-order retirement.
- `bc37065`: algorithm-v15 physical movement and resource calibration.
- `d342b19`: idempotent native camera reframing, including land framing.

## Strategic and operational AI

The campaign owns one persisted `CoalitionStrategy` per belligerent coalition.
The strategic layer evaluates known theater state and emits durable objectives;
operational group commitments and orders are then derived beneath those
objectives. Tactical assignments fence their owned interaction islands while
unrelated islands continue through the same campaign cycles. Completed orders
that no group references are removed from canonical state and their journal
subject revisions are retired.

An isolated full-seed v15 run produced:

| Boundary | Observation |
| --- | ---: |
| Bootstrap | 10,385.7 ms |
| Regions / bases / entities / groups | 55 / 81 / 4,096 / 919 |
| Driven campaign cycles | 20 separate advances of 10 quanta |
| Final campaign time / commit | 200 / 5,912 |
| Coalition strategies | 2 |
| Strategic objectives | 122 |
| Group commitments | 919 |
| Active orders | 802 |
| Completed / unreferenced orders retained | 0 / 0 |
| Recent operational outcomes | 467 |
| Average / maximum advance | 717.1 / 1,545.0 ms |
| Last advance | 626.4 ms |
| Service memory before restart | 77.4 MiB managed / 208.5 MiB working set |
| Journal growth over 20 cycles | 14,090,240 bytes |

The growth is dominated by current movement/order history across thousands of
entities; the recovered state proves that it is not recurrence of the earlier
completed-order state leak. Retention/compaction and a 10,000-commit storage
envelope remain open.

The read-only persistence probe recovered the full state in 1,542.7 ms with
algorithm version 15, 802 active referenced orders, and canonical hash
`f70f3b9d05cb904a3d11fc663dc26d1d9e5d0598ebbce5193bb3b051ddd7ef00`.
After an actual ServiceHost stop/start, the first diagnostic view retained time
200, commit 5,912, all 4,096 entities and 919 groups, 122 objectives, and the
same hash. Host recovery telemetry was 1,701.1 ms; after the view it held 17.0
MiB managed and 115.3 MiB working set.

## Native movement sample and emulator calibration

The native baseline used the authored four-unit
`Fulda Gap Gone Hot - GIUK Dynamic Campaign Window` mission and USS Pharris.
The experiment requested 300 controlled logical updates at a
`0.033333335`-second step with seed `19890911`:

- native logical time advanced exactly 10 seconds;
- the hook reported 300 game updates and 150 physics fixed updates;
- Pharris moved approximately 22 m from
  `63.1000746015, -15.0499284987` to
  `63.1002513200, -15.0497520173`;
- speed remained approximately 10.8 kn;
- damage and ammunition were unchanged.

The controlled hook did not execute one physics fixed update for every logical
update, so the observed 22 m is not treated as an exact ten-second kinematic
golden. At 10.8 kn, nominal physical travel over ten seconds is about 55.6 m.
Algorithm v15 therefore models surface cruise as 56 m per ten-quanta activity,
with domain-specific distances for air, rotary-wing, ground, installations,
abstract land, logistics, and subsurface units. It moves toward the next graph
leg anchor and changes region only after physically reaching the leg. Movement
no longer adds damage merely because a unit travelled, and fuel/stores burn is
domain- and telegraph-scaled instead of consuming enormous fixed fractions.

The 11-test campaign integration suite passes with new deterministic golden
hashes and asserts a sub-kilometer physical movement envelope, realistic-scale
fuel consumption, and no travel-created damage. This closes the most obvious
movement divergence; sensor, weapon, damage, and repeated native outcome-batch
calibration still remain before full parity can be claimed.

## Terrain-safe installation streaming

The live player proof used a campaign-owned `nato_small_airbase` at Ramstein
(`49.4369, 7.6003`) in the full dynamic-campaign mission shell. The bridge first
relocated the tactical terrain window and rejected materialization until the
target tile was loaded, finalized, flattened for land units, and classified as
land.

| Observation | Value |
| --- | ---: |
| Terrain tile | `(937, 202)` |
| Sampled terrain height | 265.2494507 m |
| Materialization duration | 264,782 us |
| Read-back ground clearance | +0.0671952 m |
| Native record | enabled, not destroyed |
| Stable identity | `campaign-unit:unit:crimean-tom:player-focus-airbase` |

Computer Use then selected the installation in the visible Formation Manager.
Typed focus selected the native campaign-owned unit and changed the player view
from the stale low horizon to an elevated oblique view with finalized terrain,
airbase/city geometry, the highlighted `Small airbase` formation row, and the
`Small airbase` status strip. Repeating focus while that same unit was already
selected completed through the local control script in 311.2 ms and preserved
the correct framing. This directly covers the prior no-op path in native
`RenderPosition.switchToObject`.

The currently qualified physical land profiles are M60, M113, Vulcan,
MIM-23 launcher, NATO EWR station, and NATO small airbase. They are primarily
attackable tactical targets; generalized ground formations, catalog-wide
installations, aircraft, rotary-wing, subsurface units, and detailed
ground-ground adjudication remain outside this POC.

## Verification disposition

- `CrimeanTom.Campaign.IntegrationTests`: **11/11 passed**.
- Full solution build after algorithm v15: **passed, zero warnings/errors**.
- Fresh Runtime Bridge deployment/launch: **passed**, fresh health marker at
  `2026-08-23T19:12:09.492Z` (UTC log time).
- Live terrain/materialization/player-camera proof: **passed**.
- Focused broad real-host protocol story: **incomplete**; it remained active
  without a failure after approximately three minutes and was stopped to avoid
  an unbounded helper-process fan-out. It is not counted as a pass. Earlier
  focused protocol coverage and the live runtime proof remain valid, but the
  latest full protocol suite should be rerun under a bounded test harness.

## Remaining parity and playability gaps

1. Build native observation batches for sensors, weapon expenditure, damage,
   countermeasures, and air/rotary/subsurface movement rather than relying on a
   single surface kinematic sample.
2. Connect player map intent to scheduler promotion and camera focus through a
   first-class campaign UI instead of scripts/operator endpoints.
3. Materialize and return the hostile side of a tactical engagement, including
   authoritative destruction/damage outcomes.
4. Add recurring airbase inventory/sortie/turnaround, IADS command-network, and
   runway damage/repair lifecycles.
5. Qualify long-session lease renewal, service/game restart grace, journal
   retention/compaction, and a 10,000-commit recovery envelope.
