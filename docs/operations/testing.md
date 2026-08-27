# Testing and observation

## Purpose

Tests must answer whether the player loop works, the campaign keeps one
timeline, Sea Power behaves as expected, and the authored campaign stays within
budget. Prefer a few broad stories over many narrow tests.

No single layer is sufficient:

| Layer | Proves | Does not prove |
| --- | --- | --- |
| Build | Projects and generated contracts compile | Runtime or player behavior |
| Campaign integration | Deterministic commands, simulation, persistence, AI | Unity behavior |
| Protocol integration | Real service, companion, journal, leases, and bridge contracts | Sea Power rendering or physics |
| Runtime smoke | Plugin load, typed commands, UI state, live object behavior | Long campaign correctness |
| Player flow | The product can be used from start to recovery | Broad performance by itself |
| Benchmark | Measured time, memory, and scale on one setup | Correctness or parity by itself |

## Build and automated integration

Use repository scripts from the root:

```powershell
pnpm test
```

For narrower diagnosis:

```powershell
pnpm monitor:build
pnpm test:campaign
pnpm test:protocol
```

CI compute may be unavailable. A local result must record the commit, command,
exit code, machine, and game build. A passing local run is still valid evidence
when those details are retained.

## Run the local campaign stack

Start the campaign service and browser application:

```powershell
pnpm campaign:dev
```

Build, deploy, launch, and inspect the Sea Power integration with the
repository-local `sea-power-runtime` skill and these entry points:

```powershell
pnpm build
pnpm run deploy
pnpm launch
pnpm verify
pnpm status
pnpm logs
```

Start or inspect the local companion when it is not already managed by the
workflow:

```powershell
pnpm companion:start
pnpm companion:health
pnpm companion:status
```

Use typed Runtime Bridge inspection before desktop coordinates:

```powershell
pnpm game:ping
pnpm game:capabilities
pnpm game:scene
pnpm game:world
pnpm game:campaign
pnpm game:ui
pnpm game:screenshot
```

For a bounded live wait such as native flight-deck preparation, set one of Sea
Power's normal tactical compression steps and restore x1 afterward:

```powershell
pnpm game:time-compression -- -TimeCompression 10
pnpm game:time-compression -- -TimeCompression 1
```

Do not leave extra Sea Power or companion processes running after a test unless
the user asks to keep them open.

## Full-spectrum combat parity

The combined parity pair is the broad live combat check. One coastal battle
includes a carrier group, a native flight-deck launch, fixed-wing land attack
and SEAD, fighter interception, AEW, layered GBAD, surface missile combat,
submarines, helicopter ASW, an airfield, ground installations, and civilian
traffic.

Run the authored baseline and streamed counterpart with the same duration and
time compression:

```powershell
pnpm combat:parity -- -Case Combined -Mode Native -DurationSeconds 45 -TimeCompression 20 -OutputPath .run/combat-parity/full-spectrum-native.json
pnpm combat:parity -- -Case Combined -Mode Streamed -DurationSeconds 45 -TimeCompression 20 -OutputPath .run/combat-parity/full-spectrum-streamed.json
pnpm combat:parity -- -NativePath .run/combat-parity/full-spectrum-native.json -StreamedPath .run/combat-parity/full-spectrum-streamed.json
```

The comparison requires matching detection, weapon launch, damage, destruction,
air-to-land attack, GBAD engagement, carrier operations, helicopter and
submarine presence, civilian traffic, camera-focus traversal, and streamed
native readiness. The capture also records frame rate, fixed timestep, internal
time dilation, first detection and weapon times, and peak active weapons.

This pair compares authored and streamed units inside Sea Power. It proves that
streamed units become combat-ready native objects. It does not compare the
campaign service's combat model with Sea Power.

## Server simulation parity

Do not claim full server fidelity from aggregate campaign outcomes. Detection,
launch, damage, and destruction counts are smoke checks.

A server parity case must use the same versioned initial state, orders,
environment, doctrine, and random inputs for both runs. Capture the native Sea
Power trace first, then compare server event order and timing, tracks, movement,
fuel, ammunition, launched weapons, guidance state, damage, tactical decisions,
and the final portable checkpoint. Run the server once with all relevant
mechanics awake and once with selective sleeping enabled. The two server traces
must match before either is compared with Sea Power.

Each difference must produce one of four results:

- a defect;
- a measured and named tolerance;
- a versioned calibration rule tied to the pinned Sea Power build; or
- an unsupported-state rejection that prevents server authority.

The required mechanics and platform gaps are tracked in
[full-fidelity server simulation](../architecture/full-fidelity-server-simulation.md).

## First-playable acceptance story

Run this story against the complete authored campaign:

1. Start from no running Crimean Tom processes.
2. Create a campaign and confirm it is paused.
3. Inspect the friendly order of battle and coalition-known contacts.
4. Issue one player order, start time, and confirm its durable effect.
5. Select a campaign-owned task force and request tactical entry.
6. Confirm that every supported task-force member has a native representation.
   Top-level members must be real Sea Power objects. Hosted aircraft must appear
   in their host's flight-deck inventory and the owned-resource ledger without
   consuming world-object capacity until launch.
7. Maximize the tactical map and inspect local units, non-local friendlies, and
   hostile tracks. Automated runs should use the typed HUD request with
   `fullscreen`, `zoomLevel`, and map-center coordinates. Record the realized
   framing returned by the command because Sea Power can clamp the requested
   world-scale zoom to its native view bounds.
8. Select each kind through the player input path. Confirm native symbology,
   labels, focus, and bounded loading feedback.
9. Select a distant friendly and confirm one non-simulating visual proxy appears
   above valid terrain without creating a duplicate. Deselect it, sample memory
   diagnostics, and repeat across several units; hidden proxy roots and loaded
   materials must not accumulate.
10. Fight one campaign-owned hostile for at least 15 unpaused minutes while the
    campaign service advances unrelated entities.
11. Exit normally and confirm hostile fate, player damage, fuel, stores,
    ammunition, and objective effects commit once.
12. Save, restart the application, and confirm the same campaign head.
13. Repeat with a forced Sea Power termination. Confirm stale authority is
    fenced and no duplicate object or second timeline appears.

During steps 5 through 9, capture projection and control behavior separately:

- start one full projection replay and issue a control command while it applies;
- confirm the control command completes inside its deadline;
- confirm the replay reports received and applied generation and revision;
- repeat one scenario transition with the same idempotency key and confirm it
  does not run twice;
- select one distant terrain-backed unit and confirm exactly one relocation;
- confirm terrain polls do not pause or unpause tactical time;
- click a native unit near a campaign marker and confirm native selection wins;
- force one promoted unit to miss native readiness, confirm no owned object or
  capacity leak remains, confirm its map projection returns, then retry the same
  materialization effect and roll it back again;
- disconnect the game pipe immediately after native checkpoint capture but
  before the companion publishes `AssignmentPrepared`; confirm the checkpoint
  and ready assignment reach the service, the assignment activates, and no
  cancellation-derived failure frame appears;
- pause the campaign after a tactical worker disappears, let its wall-clock
  lease grace expire, and confirm fallback releases authority at the unchanged
  campaign quantum; and
- restart Sea Power without restarting the service and confirm a full projection
  replay restores the authorized records.

Also exit the Sea Power scene before requesting campaign return. The bridge
record should become `detached-after-scene-loss`, retain its current checkpoint,
complete the fence without issuing a tactical-pause command, and release at the
checkpoint revision. Confirm destroyed Unity wrappers report native readiness,
stores, orders, and control as false, and that snapshot, collider, memory,
focus, and projection scans do not dereference those wrappers. Restart the
companion while Sea Power is still outside a tactical scene and confirm the
positive release receipt removes the ledger record without trying to pause.
Repeat for one land or installation profile once that family is live-qualified.

After a normal exit, confirm the worker projection carries the exact released
assignment receipt before native reclamation. A successful projection that
omits the assignment must not authorize destruction. Confirm reclaimed units
are absent from Sea Power's active and disabled task-force collections, do not
appear in a subsequent save, and are recorded as leaving the area rather than
being destroyed. Repeat the exit after disconnecting between native creation
and the first checkpoint; the positive receipt must roll back the incomplete
object or release an already detached ledger entry.

Record every unsupported field and family. Do not reinterpret a visible proxy
or a service event as a complete tactical round trip.

## Terrain and proxy checks

For every ground unit or installation materialized in Sea Power:

- wait for loaded and finalized terrain;
- sample the terrain height at the final latitude and longitude;
- reject water or unresolved placement;
- confirm the visible model bounds remain above the terrain surface;
- inspect again after focus, proxy promotion, reconnect, and demotion; and
- confirm a failed placement leaves a selectable map record and clear error.

For map and visual proxies:

- compare symbols for local and non-local units of the same domain;
- test direct mouse selection, not only typed bridge selection;
- test overlapping symbols and large uncertainty regions;
- confirm repeated selection is idempotent;
- confirm hostile proxies use contact estimates, not true position or model;
- confirm proxies have no AI, physics, sensors, weapons, unique game identity,
  checkpoint, or publication path; and
- confirm promotion hides the proxy before the real object becomes visible.

## Performance comparison

Use the capture script for both baseline and candidate:

```powershell
pnpm performance:capture -- -Label vanilla-reference -DurationSeconds 120
pnpm performance:capture -- -Label authored-campaign -DurationSeconds 120
```

Compare retained captures with:

```powershell
pnpm performance:compare -- -BaselinePath <baseline.json> -CandidatePath <candidate.json>
```

Use the same machine, game build, graphics settings, camera state, scenario
pairing, warm-up, duration, and time controls. Record:

- Sea Power frame rate and frame-time distribution;
- engine and game fixed timestep;
- internal time dilation and selected time compression;
- process working set and managed memory where available;
- campaign advances and work units per wall second;
- native simulated objects, map records, visual proxies, Operational entities,
  contacts, weapons, and terrain-backed objects;
- viewport total records, geographic-prefilter records, selectable candidates,
  attached markers, complete refresh time including ordering and prefiltering,
  and worst main-thread slice;
- tactical-entry and selected-proxy latency; and
- checkpoint size and result-commit latency.

Profile the same authored campaign as its order of battle changes. Create small,
purpose-built Sea Power scenarios only when a native parity comparison is
needed; they are test cases, not alternate campaigns. Do not commit generated
mass-native missions.

## Parity portfolio

Use small matched cases before aggregate battles. Compare native Sea Power with
the Operational model for:

- movement, heading, depth or altitude, and fuel use;
- sensor detection, classification, uncertainty, and track aging;
- weapon range, launch conditions, flight time, interception, hit, and damage;
- aircraft and helicopter sortie, basing, turnaround, and recovery;
- submarine and surface behavior;
- ground target, radar, GBAD, airbase, and installation behavior; and
- player-visible controls, symbols, selection, and focus.

Keep source inputs and seeds. Record systematic differences as calibration
data or explicit unsupported behavior. Do not tune one aggregate battle until
its component paths agree well enough to explain the result.

## Persistence and recovery portfolio

Exercise:

- at least 10,000 commits with periodic snapshots;
- cold service restart and replay from the latest valid snapshot;
- online backup and side-directory restore;
- interrupted writes and partial blob creation;
- malformed or mismatched checkpoint, catalog, runtime, and ruleset inputs;
- bounded journal, snapshot, blob, and memory growth;
- one schema version change and one campaign algorithm change; and
- game, companion, and service failure at each tactical handoff stage.

Success means the service either recovers one canonical head or refuses the
operation without changing the source save.

## Retain test results

Store raw captures, logs, screenshots, and environment details with the run
artifacts or release record. Keep durable conclusions in current status and
supported claims. Do not add dated evidence pages, handoff diaries, or temporary
review documents to `docs/`.

When testing the temporary exact-enemy debug projection, the local campaign
service permits it by default. Set
`CRIMEAN_TOM_ALLOW_DEBUG_REVEAL_ALL_ENEMIES=false` to exercise the denied path.
The companion must then retain ordinary friendly and coalition-contact
projection rather than treating the optional debug denial as a sync failure.
