# Testing and observation

## Test layers

Use the cheapest layer that can answer the question:

| Layer | Proves | Does not prove |
| --- | --- | --- |
| Compile | C# and references are valid. | Deployment or runtime load. |
| Hash/deploy check | The built DLL matches the installed DLL. | Loader execution. |
| Fresh log marker | AnchorChain reached Sea Power Runtime Bridge's entry point. | Feature behavior. |
| Menu/UI smoke | Game reached a usable screen. | Tactical hooks or campaign correctness. |
| Tactical smoke | A mission initialized and core panels respond. | Long-run simulation correctness. |
| Control round trip | Companion reaches the plugin, completes its dispatch queue, reports an executing thread equal to the Unity thread captured during initialization, and requires the actual dispatcher to reject a deliberate worker-thread call. | State mutation or remote networking. |
| Materialization round trip | A campaign entity enters and leaves a tactical worker without losing identity/state. | Balance or large-scale determinism. |
| Replay/property tests | Strategic invariants and determinism hold. | Unity integration. |

## Source inspection baseline

After a Sea Power update:

```powershell
pnpm decompile
pnpm decompile:anchorchain
```

Record:

- visible game version;
- timestamps/hashes of the inspected assemblies;
- count of generated C# files;
- important renamed/missing types from [Source map](../research/source-map.md);
- any decompiler warnings.

Do not commit the generated tree.

## Continuous integration

GitHub Actions (`.github/workflows/ci.yml`) builds the game-independent DLLs
from `CrimeanTom.CI.slnf`, builds the campaign monitor, and runs the campaign
kernel tests against that Release build. A second Windows job runs
`pnpm test:protocol`. The Sea Power Runtime Bridge plugin is excluded because
it needs installed game assemblies. One workflow run is in flight at a time so
a stacked `gh stack push` does not start a job pair per PR. `pnpm test:ci`
reproduces the Linux job locally; it does not run the Windows protocol job.

```powershell
pnpm test:ci
```

## Runtime baseline

Run the game-independent broad integration scenario first:

```powershell
pnpm test
```

Regenerate the pinned compiled determinism inventory after a game assembly or
classifier change:

```powershell
pnpm inventory:determinism
```

The ignored manifest is deterministic and includes method-level IL hashes. Its
`promotionCriticalUnknownCount` is an authority blocker, not a coverage number
to suppress by broadening name heuristics.

It starts the real companion and crosses Kestrel token authentication, the
current-user named-pipe boundary, one connected game peer, capability/catalog/
world reads, typed UI actions, and uncommitted port-draft validation. Avoid
reintroducing narrow unit-test matrices; extend this scenario when a new surface
fits the same end-to-end story.

```powershell
pnpm status
pnpm dev
```

`pnpm dev` may restart Sea Power, builds and deploys the plugin, launches through
Steam, and waits for a fresh health marker. A process alone is not success.

For an already running build:

```powershell
pnpm verify
pnpm logs
```

Validate the local control plane with:

```powershell
pnpm companion:status
pnpm game:control-test
```

The smoke test requires the companion/game handshake, invokes ping,
capabilities, and scene inspection, and proves the dispatch round trip. Ping and
scene each compare their actual executing thread with the Unity main-thread ID
captured during plugin initialization. A one-shot initialization self-test also
invokes the actual dispatcher from a managed worker thread and must receive a
structured `wrong-execution-thread` response. Its top-level capability result
must be `passed`; `pending` and sanitized `failed:*` results remain authority
blockers.

The capability response must reach `fileIdentityState: ready`, contain a
64-character `fingerprintSha256`, list all required managed assemblies, and
identify every reason `authorityEligible` is false. During P0, expected blockers
include unavailable catalog/mod fingerprints, uninstalled deterministic hooks,
and disabled mutating commands. A missing or pending hash must never silently
produce an authority-eligible client. Engine and game fixed-delta values are
reported as timing diagnostics because Sea Power may change them with FPS; they
are not stable compatibility-hash inputs.

`pnpm game:control-test` enforces those file-identity/hash invariants, checks the
exact required assembly set, and captures capabilities twice to prove that
dynamic timing diagnostics do not perturb the compatibility fingerprint.
It also requires negotiation of transmitted cancellation and the Bulk (`-100`),
Normal (`0`), and Control (`100`) priority lanes, plus a passed execution-thread
guard self-test.

The broad scenario also seeds a recycled unrelated PID and stale token/log
artifacts, verifies missing/wrong/correct HTTP tokens, requires anonymous Windows
pipe access denial followed by current-user connection, and checks clean
companion shutdown. Protocol invariants that do not yet fit this flow should be
proven through a new broad failure/recovery scenario, not a collection of narrow
method tests.

### Named tactical workers (experimental second instance)

The default `pnpm dev` / `pnpm status` path remains the primary demo launcher.
Named workers use isolated state and do not build, deploy, or stop that default
process:

```powershell
pnpm worker:start -- -WorkerId worker-a -SkipBuild `
  -CampaignId <32-char-guid> `
  -CampaignGrpcUrl http://127.0.0.1:5082 `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -CampaignRunDirectory .run/campaign-service
pnpm worker:status -- -WorkerId worker-a -CampaignId <guid> `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -CampaignRunDirectory .run/campaign-service
pnpm worker:stop -- -WorkerId worker-a
```

Each named worker stores campaign companion state under its own run directory
(for example `.run/workers/worker-a/campaign-companion-state-<campaignId>`), so
two workers on the same campaign service receive distinct client instance IDs.

```powershell
pnpm worker:live-proof -- -CampaignId <guid> `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -WorkerIds review-a -WorkerIds review-b
```

Alternatively pass one quoted comma- or space-separated list:

```powershell
pnpm worker:live-proof -- -CampaignId <guid> -WorkerIds "review-a,review-b"
```

To verify workers from another repo root or deployment layout, pass `-WorkersRunRoot`:

```powershell
pnpm worker:live-proof -- -WorkersRunRoot C:\path\to\.run\workers `
  -CampaignId <guid> `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -CampaignTokenFile C:\path\to\.run\campaign-service\service.token `
  -WorkerIds worker-a,worker-b
```

From a worktree, point status at an external run directory per worker:

```powershell
pnpm worker:status -- -WorkerId worker-a `
  -RunDirectory C:\path\to\.run\workers\worker-a
```

Live proof refuses synthetic gRPC test sessions (`container-worker-probe`,
repeated-digit fingerprints, and similar protocol-test stand-ins). It also
requires each worker's recorded Sea Power PID to match the installed executable,
recorded UTC start time, and worker `-logFile` path before trusting companion
health. A mock pipe peer cannot qualify without that live game process. Success means
two real runtime+companion workers coexist with distinct game session IDs, client
instance IDs, and campaign session IDs. It does not claim safe two-container
island migration.

Limitations (fail closed):

- Steam's single-process launch may block a second game; the worker script uses
  direct `Sea Power.exe` launch as an experimental path only. Worker game starts
  set per-process `SteamAppId` and `SteamGameId` (default `1286220`, override
  with `-SteamAppId`) so Steamworks can recognize a developer launch while the
  Steam client remains running. The script does not write `steam_appid.txt` or
  mutate install/global environment variables.
- Same-user `Application.persistentDataPath`, `PlayerPrefs`, native logs, and
  `last_save` remain shared; per-worker isolation covers Crimean Tom control
  traffic, experimental probe saves, and `-logFile` redirection only.
- Do not run `pnpm worker:stop` against the default demo worker ID or paths.

**First live host attempt (21 Aug 2026):** `worker-a` companion start succeeded,
Unity initialized on the isolated direct launch, then Steamworks.NET exited with
`RestartAppIfNecessary returned true` while the default `pnpm dev` game remained
healthy. Transactional rollback removed worker PID/metadata files and stopped the
worker companion; the harness had waited 120s on plugin load because it did not
detect the early game exit. **No two-container claim** from that run. Per-process
Steam app context (`SteamAppId` / `SteamGameId`, default `1286220`) was the next
experiment.

**Coexistence proof (21 Aug 2026, campaign `08eed6175b1a4e1abf50004d4e5808c8`):**
with per-process Steam app context, two named workers coexist on the live host:

| Worker | Game PID | Companion PID | Game session | Client instance | Campaign session |
| --- | --- | --- | --- | --- | --- |
| `worker-a` | 47840 | 35088 | `75189b90542e446482ecdddd7a48dd88` | `39d074634b5d40d380a1191c33c109da` | `67d7dec5de9a4557b8c96bb731e30246` |
| `worker-b` | 46160 | 42392 | `d1269ddd9d6748faac76d4d05ee3185c` | `e3b6431ff5d74518ac47469734483ea1` | `a2a3074e3b7b4a698a87de28bd4e1931` |

`pnpm worker:live-proof` with `-WorkersRunRoot` against the main `.run\workers`
root returned `LIVE_PROOF_SUCCEEDED`. **Coexistence of two real runtime+companion
workers is proven** on this host. **Cross-worker tactical-island migration remains
unproven** — do not claim two-container migration or automatic rebalance until
`pnpm worker:live-migrate -Execute` (or `pnpm worker:migration-proof -Execute`)
succeeds with destination Primary Active and source Primary Released.

Closed-loop driver (22 August 2026): read-only `pnpm worker:live-migrate`
preflight does not repair campaign state. `-Execute` authorizes pinned
`prepare-source` on `-SourceWorkerId` and then reuses the migration-proof path.
Optional `-CreateCampaign` is a separate setup mutation: it allocates a journal,
bootstraps the Phase 4 Knox set, and exits. Named workers must then reconnect to
that campaign ID with a loaded player task force before preflight. See
`docs/evidence/phase-5-live-migrate-driver-2026-08-22.md`.

Two-process divergence sampler (22 August 2026): `pnpm worker:divergence-sample`
polls both workers' `/game/world` clocks and hashes. It never claims hot-shadow
failover. See `docs/evidence/e1a-divergence-sampler-2026-08-22.md`.

Cross-worker migration driver (implementation landed 21 Aug 2026):

```powershell
pnpm worker:migration-proof -- `
  -CampaignId 08eed6175b1a4e1abf50004d4e5808c8 `
  -CampaignHttpUrl http://127.0.0.1:5081 `
  -CampaignTokenFile C:\Users\Varun\repos\crimean-tom\.run\campaign-service\service.token `
  -WorkersRunRoot C:\Users\Varun\repos\crimean-tom\.run\workers `
  -SourceWorkerId worker-a `
  -DestinationWorkerId worker-b
```

Read-only preflight stops with exit `2` when migration is unsafe. A successful
`-AllowGhostRelease`-only repair step exits `0` with refreshed blockers (typically
`source-preparation-required` after Operational demotion) instead of retaining
stale pre-repair blockers or exiting `1`. Add `-Execute` only after preflight
passes. Use `-AllowGhostRelease` to release a disconnected legacy Primary
(`advance` + `resolve-expired`). Use `-AllowSourcePreparation` to pin Primary
handoff onto the requested source worker via `POST /control/campaigns/{id}/prepare-source`
from an Operational interaction island with no live Primary; the server promotes
back to Tactical after materialization. Scheduler promotion remains a separate,
nondeterministic `-AllowSchedulerPromotion` legacy step. `-AllowRepair` is a convenience alias for
`-AllowGhostRelease` plus `-AllowSourcePreparation` only; it never enables scheduler promotion. Migration execution passes `preferredDestinationClientInstanceId`
to `prepare-shadow` for pinned Shadow placement.
See `docs/evidence/phase-5-migration-proof-2026-08-21.md`.

After a mission is fully loaded and paused, the pinned experimental logical-tick
adapter can be exercised without menu input:

```powershell
pwsh -File scripts/crimean-tom-control.ps1 run-controlled-ticks-experimental `
  -SkipBuild -TickCount 300 -TickStepSeconds 0.0333333333333333 -Seed 1042
```

Treat the response as diagnostics, not authority evidence. Exact requested time
and update counts are positive logical-clock evidence; unequal fixed-update
counts, listed uncontrolled sources, or hook mismatches remain explicit
determinism blockers.

Owned Phase 2 mutations start disabled. In a disposable, fully loaded paused
mission, arm only the current authenticated pipe session, dry-run, and then
materialize the first allow-listed surface fixture:

```powershell
pwsh -File scripts/crimean-tom-control.ps1 arm-owned-mutations -SkipBuild
pwsh -File scripts/crimean-tom-control.ps1 dry-run-owned-surface -SkipBuild `
  -Latitude 12.9396320322755 -Longitude 43.1309606993161 `
  -HeadingDegrees 144.830795288086
pwsh -File scripts/crimean-tom-control.ps1 materialize-owned-surface -SkipBuild `
  -Latitude 12.9396320322755 -Longitude 43.1309606993161 `
  -HeadingDegrees 144.830795288086
```

Use unique assignment/entity/idempotency values for a new live object. Repeating
the exact same payload is an idempotency check and must return the original
object/revision. Never aim this command at a user/scenario identity; the plugin
accepts only its `unit:crimean-tom:` ownership namespace.

The Windows ACL test impersonates the anonymous security token against the
actual broker, requires kernel access denial before handshake, then proves the
same listener still accepts the current user. This is not a substitute for the
final named-second-user proof, which requires a disposable credential or
pre-authorized helper unavailable to the current medium-integrity session.

Use the repository-local runtime skill for the compact diagnosis route. Its
BepInEx and AnchorChain references are intentionally separate edge-case guides.

## Deterministic UI smoke

The safe read-only smoke path is:

1. Launch through `pnpm launch` and require a fresh Sea Power Runtime Bridge marker.
2. Use a typed Runtime Bridge UI action for any navigation seam already
   discovered and fingerprinted on the pinned build.
3. Select the one Sea Power window returned by the desktop-control API only for
   remaining visual verification or automation gaps.
4. Close the news popup if present and press F7 if UnityExplorer obscures the
   game; replace these clicks when normal feature work discovers stable seams.
5. Confirm the main menu and record the version string.
6. Open Scenarios and select `Intro > 01 Red Sea Breakout - NATO (1988)`, using
   bridge automation when available.
7. Start the scenario and advance the briefing, again preferring bridge actions.
8. In tactical play, toggle pause and verify date/time plus formation/map UI.
9. Open Event Log and confirm tabs and live contact reporting.
10. Open Flight Deck and confirm capacity/operations surfaces.
11. Press Escape and verify Resume, Restart Mission, save/load, report, and exit
    actions are available.
12. Exit through the After Action Report and return to the menu.

Do not treat screen coordinates as constants. Desktop input is the fallback,
not the target interface: capture the current window before every remaining
desktop action, act once, and capture again. When Computer Use is unavailable,
use `pnpm game:screenshot` and `pnpm game:ui` plus typed bridge actions instead
of coordinate clicks. `pnpm game:bubble-probe` samples `/game/world` twice and
records rebase, geodetic, and loaded-terrain occupancy. It fails closed outside
a loaded tactical mission or before initial terrain loading completes, and it
does not qualify a combat radius. When a test repeatedly encounters a stable
game command/view-model seam, automate it in the bridge as part of that
feature's work rather than maintaining a coordinate script.

## Editor smoke

Without saving content:

1. Open Mission Editor.
2. Verify Scenario, Groups, Triggers, Mission Briefing, and Map Elements tabs.
3. Verify unit placement/filter toolbar and world map.
4. Leave through Back and accept the exit confirmation.

This catches major Noesis binding failures and mission-authoring regressions
without changing user files.

## Mod Manager smoke

Open Mod Manager but do not toggle or accept changes unless the test calls for
it. Verify User Data, Base Game, local data directories, and Anchor Chain are
discoverable. Missing Sea Power Runtime Bridge description text is a content-metadata issue;
it is not a failed DLL health check.

## Campaign smoke

At minimum verify:

- campaign list loads;
- a standard campaign shows narrative and Play/Load;
- Task Force Mode shows difficulty and persistence/economy rules;
- the Crimean Tom port-operations shell opens through the typed bridge action
  and reports populated ship, submarine, and air roster groups;
- returning to the main menu does not mutate or create a campaign save.

When campaign features exist, use a disposable named campaign/save. Never
overwrite the user's real campaign state as part of a smoke test.

The eventual port-operations integration scenario should cover the whole
administrative transaction in one run: external docked-force fixture, projection
generation, controlled restart/reload, refit and reinforcement draft, typed
import, validation, external commit, second projection, and verification that
unrelated theater entities continued advancing. Do not split this into a large
collection of view-model unit tests. A native mission result from the shell is
never acceptable evidence of an authoritative campaign outcome.

## Strategic engine tests

The standalone engine should use a small number of broad deterministic scenarios
covering movement, logistics, detection, repair, and reducers together, plus:

- property tests for conservation (units/stores cannot appear without an event),
  non-negative resources, graph reachability, and idempotent results;
- golden replay tests that compare final snapshot/event hashes;
- randomized long runs with fixed seeds;
- partition tests that compare single-worker, primary/shadow, and reassigned
  interaction-island outcomes;
- schema migration tests for every persisted version;
- catalog compatibility tests against the installed data fingerprint.

### Essential invariants

- A unit instance has one canonical location/revision at a campaign tick, even
  when several clients simulate replicas.
- One authority epoch has at most one primary event publisher per entity or
  interaction island.
- Events from an expired or fenced epoch cannot be committed.
- Every full replica identifies its checkpoint and applied event cursor.
- Stores and aircraft quantities never fall below zero.
- Applying the same tactical event twice changes state only once.
- Same version + seed + initial state + commands produces the same final hash.
- Cross-region transfer has exactly one source removal and one destination add.
- Destroyed units cannot be repaired or reissued without an explicit replacement
  event.
- A render proxy is never promotion eligible; an observer is eligible only when
  it has complete required state.

## Tactical worker contract tests

Keep small checkpoint/event fixtures for:

- surface-only engagement;
- air group hosted on a ship;
- submarine with uncertain contact;
- damaged unit with partially spent magazines;
- base capture/facility damage;
- retreat/withdrawal;
- event replay and duplicate-event rejection;
- primary loss and warm-shadow promotion;
- observer-to-shadow promotion eligibility;
- materialize, checkpoint, dematerialize, and rematerialize.

Test serialization, replay, fencing, and promotion separately from Sea Power.
Then run a small number of real game integrations to validate live
materialization, event capture, checkpointing, and correction.

## Load and tempo contract tests

Use the stress matrix in [Load management](../architecture/load-management.md) to throttle CPU
and GPU, collapse render rate, fill pipe/network/checkpoint queues, pause a game,
fail a migration destination, pressure every destination, repeat overload, and
submit false capacity metrics. Verify fixed steps and canonical hashes are not
stretched or skipped; old epochs never publish; optional work is shed before
global tempo; and a disabled simulation source remains connected for authorized
commands and filtered presentation.

Every global tempo change names an effective tick. All required replicas must
show the same tempo policy at that tick while keeping `FixedStepDuration`
unchanged. Thresholds and cooldowns come from recorded benchmarks.

## Evidence recording

For every live failure retain:

- game and plugin version/hash;
- launch time and relevant log excerpt;
- scenario/campaign name;
- gameplay difficulty/options;
- exact UI checkpoint reached;
- assignment, authority epoch, checkpoint/event cursor, and hashes, if applicable;
- screenshot only when it materially clarifies UI state.

Logs and generated save/mission fixtures may contain local paths or user-created
content. Sanitize before sharing.
