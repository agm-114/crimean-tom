# Testing and observation

## Purpose

Tests must answer whether the player loop works, the campaign keeps one
timeline, Sea Power behaves as expected, and the 4,096-entity design stays
within budget. Prefer a few broad stories over many narrow tests.

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
pnpm deploy
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

Do not leave extra Sea Power or companion processes running after a test unless
the user asks to keep them open.

## First-playable acceptance story

Run this story against the full 4,096-entity campaign:

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
   hostile tracks.
8. Select each kind through the player input path. Confirm native symbology,
   labels, focus, and bounded loading feedback.
9. Select a distant friendly and confirm one non-simulating visual proxy appears
   above valid terrain without creating a duplicate.
10. Fight one campaign-owned hostile for at least 15 unpaused minutes while the
    campaign service advances unrelated entities.
11. Exit normally and confirm hostile fate, player damage, fuel, stores,
    ammunition, and objective effects commit once.
12. Save, restart the application, and confirm the same campaign head.
13. Repeat with a forced Sea Power termination. Confirm stale authority is
    fenced and no duplicate object or second timeline appears.

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

## Dynamic scale scenarios

The dynamic campaign uses the full seed with bounded subsets of 500, 1,000,
2,000, or 4,096 campaign entities. A dynamic count is not a native-object count.

## Performance comparison

Use the capture script for both baseline and candidate:

```powershell
pnpm performance:capture -- -Label vanilla-500 -DurationSeconds 120
pnpm performance:capture -- -Label dynamic-500 -DurationSeconds 120
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
- tactical-entry and selected-proxy latency; and
- checkpoint size and result-commit latency.

Use the 500, 1,000, 2,000, and 4,096 dynamic campaigns to validate scaling.
Create small, purpose-built Sea Power scenarios when a native comparison is
needed. Do not commit generated mass-native missions.

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
